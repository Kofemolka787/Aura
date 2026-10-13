param([string]$TargetSid, [string]$RunId)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$AuraErrors = [System.Collections.Generic.List[string]]::new()
$AuraJournal = [System.Collections.Generic.List[object]]::new()
$AuraActions = [System.Collections.Generic.List[object]]::new()
$AuraCurrentId = ''
$AuraSkipBackupSave = $false
$AuraRestorePoint = 'Не запрашивалась'
$AuraTouched=0

function Write-Step([string]$Name) { Write-Output $Name }
function Add-AuraError([string]$Name, [string]$Message) {
    $AuraErrors.Add($Name + ': ' + $Message)
    Write-Warning ($Name + ': ' + $Message)
}
function Initialize-AuraRuntime {
    if ($TargetSid -notmatch '^S-1-5-[0-9-]+$' -or $RunId -notmatch '^[a-fA-F0-9]{32}$') { throw 'Некорректный пользователь или идентификатор операции.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Нужны права администратора.' }
    $script:AuraMutex = [Threading.Mutex]::new($false, 'Global\AURA.WindowsTweaks')
    try { $acquired = $AuraMutex.WaitOne(5000) } catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (-not $acquired) { throw 'Другой процесс выполняет изменения Windows.' }
    $base = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AURA'
    $script:AuraRoot = Join-Path (Join-Path $base 'TweaksV2') $TargetSid
    foreach ($directory in @($base, (Split-Path $AuraRoot), $AuraRoot)) {
        if (Test-Path -LiteralPath $directory) {
            if ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Каталог состояния не должен быть ссылкой.' }
        } else { New-Item -Path $directory -ItemType Directory | Out-Null }
        Protect-AuraDirectory $directory 'S-1-5-11'
    }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $acl.SetOwner($admins)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($TargetSid), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $AuraRoot -AclObject $acl
    $script:AuraStatePath = Join-Path $AuraRoot 'history.clixml'
    $script:AuraResultPath = Join-Path $AuraRoot ($RunId + '.json')
    $script:LogPath = Join-Path $AuraRoot ($RunId + '.log')
    try { Start-Transcript -Path $LogPath -Force | Out-Null } catch {}
    if (-not (Get-PSDrive -Name HKU -ErrorAction SilentlyContinue)) { New-PSDrive -Name HKU -PSProvider Registry -Root HKEY_USERS | Out-Null }
    if (-not (Test-Path -LiteralPath ('HKU:\' + $TargetSid))) { throw 'Профиль исходного пользователя не загружен.' }
    if (Test-Path -LiteralPath $AuraStatePath) {
        Assert-AuraTrustedFile $AuraStatePath
        if ((Get-Item -LiteralPath $AuraStatePath).Length -gt 8388608) { throw 'Снимок слишком большой.' }
        $state = Import-Clixml -LiteralPath $AuraStatePath
        if ($state.Version -ne 2 -or $state.TargetSid -ne $TargetSid) { throw 'Версия или пользователь снимка не совпадают.' }
        foreach ($entry in @($state.Entries)) {
            if ($entry.Kind -notin @('Registry','CreatedKey','Service','Task','PowerSetting','PowerPlan','Hibernate','Ntfs','Bcd','ReservedStorage')) { throw 'Неизвестный тип снимка.' }
            $AuraJournal.Add($entry)
        }
        foreach ($action in @($state.Actions)) { $AuraActions.Add($action) }
    }
}

function Protect-AuraDirectory([string]$Path,[string]$ReaderSid) {
    $acl=[Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach($sid in @('S-1-5-18','S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($ReaderSid),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Assert-AuraTrustedFile([string]$Path) {
    $item=Get-Item -LiteralPath $Path
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Снимок не должен быть ссылкой.' }
    $acl=Get-Acl -LiteralPath $Path
    $owner=$acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if($owner -notin @('S-1-5-18','S-1-5-32-544')) { throw 'Владелец снимка не доверенный. Данные не исполняются.' }
    $write=[Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if($rule.AccessControlType -eq 'Allow' -and ($rule.FileSystemRights -band $write) -ne 0 -and $rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544')) {
            throw 'Снимок разрешает запись непривилегированному пользователю.'
        }
    }
}

function Write-AtomicClixml([string]$Path, $Value) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $Value | Export-Clixml -LiteralPath $temporary -Depth 12 -Force
        $stream = [IO.File]::Open($temporary, 'Open', 'ReadWrite', 'None')
        try { $stream.Flush($true) } finally { $stream.Dispose() }
        if ([IO.File]::Exists($Path)) {
            $previous=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.previous'
            [IO.File]::Replace($temporary, $Path, $previous)
            [IO.File]::Delete($previous)
        }
        else { [IO.File]::Move($temporary, $Path) }
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}
function Save-AuraBackups {
    Write-AtomicClixml $AuraStatePath ([pscustomobject]@{ Version=2; TargetSid=$TargetSid; Entries=@($AuraJournal.ToArray()); Actions=@($AuraActions.ToArray()) })
}
function Add-AuraEntry([string]$Kind, [string]$Key, $Original, $Desired) {
    $existing = @($AuraJournal | Where-Object { $_.Kind -eq $Kind -and $_.Key -eq $Key })
    if ($existing.Count -gt 0) {
        $entry = $existing[0]
        if (-not $entry.PSObject.Properties['TweakIds']) { $entry | Add-Member -NotePropertyName TweakIds -NotePropertyValue @($entry.TweakId) }
        if ($entry.Status -eq 'Restored') { $entry.Original=$Original; $entry.TweakIds=@($AuraCurrentId) }
        elseif ($entry.TweakIds -notcontains $AuraCurrentId) { $entry.TweakIds+=@($AuraCurrentId) }
        $entry.Desired = $Desired
        $entry.Status = 'Prepared'
    } else {
        $entry = [pscustomobject]@{ Kind=$Kind; Key=$Key; Original=$Original; Desired=$Desired; Status='Prepared'; TweakId=$AuraCurrentId; TweakIds=@($AuraCurrentId) }
        $AuraJournal.Add($entry)
    }
    Save-AuraBackups
    return $entry
}
function Complete-AuraEntry($Entry) { $Entry.Status='Applied'; $script:AuraTouched++; Save-AuraBackups }
function Resolve-UserPath([string]$Path) {
    if ($Path.StartsWith('HKCU:\', [StringComparison]::OrdinalIgnoreCase)) { return 'HKU:\' + $TargetSid + '\' + $Path.Substring(6) }
    return $Path
}
function Ensure-Key([string]$Path) {
    $Path = Resolve-UserPath $Path
    $missing = [System.Collections.Generic.List[string]]::new()
    $parent = $Path
    while (-not (Test-Path -LiteralPath $parent)) {
        $missing.Add($parent)
        $parent = Split-Path -Parent $parent
        if ([string]::IsNullOrEmpty($parent)) { throw 'Некорректный путь реестра.' }
    }
    for ($i=$missing.Count-1; $i -ge 0; $i--) {
        $entry = Add-AuraEntry 'CreatedKey' $missing[$i] $false $true
        New-Item -Path $missing[$i] -Force | Out-Null
        Complete-AuraEntry $entry
    }
}
function Read-RegSnapshot([string]$Path, [string]$Name) {
    $exists=$false; $kind='String'; $value=$null
    if (Test-Path -LiteralPath $Path) {
        $key=Get-Item -LiteralPath $Path
        if ($key.GetValueNames() -contains $Name) {
            $exists=$true; $kind=$key.GetValueKind($Name).ToString()
            $value=$key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        }
    }
    return [pscustomobject]@{ Path=$Path; Name=$Name; Exists=$exists; Kind=$kind; Value=$value }
}
function Set-RegBacked([string]$Path,[string]$Name,[string]$Kind,$Value,[bool]$Remove=$false) {
    $Path=Resolve-UserPath $Path
    Ensure-Key $Path
    $original=Read-RegSnapshot $Path $Name
    $desired=[pscustomobject]@{ Path=$Path; Name=$Name; Exists=(-not $Remove); Kind=$Kind; Value=$Value }
    $entry=Add-AuraEntry 'Registry' ($Path+'|'+$Name) $original $desired
    $key=Get-Item -LiteralPath $Path
    if ($Remove) { $key.DeleteValue($Name,$false) }
    else { $key.SetValue($Name,$Value,[Enum]::Parse([Microsoft.Win32.RegistryValueKind],$Kind)) }
    if (-not (Test-RegSnapshot $desired)) { throw 'Реестр не подтвердил изменение.' }
    Complete-AuraEntry $entry
}
function Test-RegSnapshot($Expected) {
    $actual=Read-RegSnapshot $Expected.Path $Expected.Name
    if ($actual.Exists -ne $Expected.Exists) { return $false }
    if (-not $Expected.Exists) { return $true }
    if ($actual.Kind -ne $Expected.Kind) { return $false }
    return (($actual.Value | ConvertTo-Json -Compress) -ceq ($Expected.Value | ConvertTo-Json -Compress))
}
function Set-DwordBacked([string]$Path,[string]$Name,[uint32]$Value) { Set-RegBacked $Path $Name 'DWord' ([int][BitConverter]::ToInt32([BitConverter]::GetBytes($Value),0)) }
function Set-StringBacked([string]$Path,[string]$Name,[string]$Value) { Set-RegBacked $Path $Name 'String' $Value }
function Set-BinaryBacked([string]$Path,[string]$Name,[byte[]]$Value) { Set-RegBacked $Path $Name 'Binary' $Value }
function Remove-RegValueBacked([string]$Path,[string]$Name) { Set-RegBacked $Path $Name 'String' $null $true }
function Invoke-SystemTool([string]$Name,[string[]]$Arguments) {
    if ($Name -notin @('powercfg.exe','bcdedit.exe','fsutil.exe','sc.exe')) { throw 'Неизвестная системная команда.' }
    $tool=Join-Path ([Environment]::SystemDirectory) $Name
    $output=@(& $tool @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw ($Name+' failed ('+$LASTEXITCODE+'): '+($output -join ' ')) }
    return ($output -join [Environment]::NewLine)
}
function Disable-ServiceBacked([string]$Name) {
    $service=Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $service) { Write-Output ($Name+': служба отсутствует'); return }
    $cim=Get-CimInstance Win32_Service -Filter ("Name='"+$Name.Replace("'","''")+"'")
    $key=Get-Item -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$Name)
    $delayed=$key.GetValue('DelayedAutoStart',0)
    if ([string]$service.Status -notin @('Running','Stopped','Paused')) { throw 'Служба находится в переходном состоянии. Повтори позже.' }
    $original=[pscustomobject]@{ StartMode=$cim.StartMode; State=[string]$service.Status; Delayed=$delayed; DelayedExists=($key.GetValueNames() -contains 'DelayedAutoStart') }
    $entry=Add-AuraEntry 'Service' $Name $original 'Disabled'
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $Name -ErrorAction Stop; (Get-Service $Name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(10)) }
    Set-Service -Name $Name -StartupType Disabled -ErrorAction Stop
    if ((Get-CimInstance Win32_Service -Filter ("Name='"+$Name.Replace("'","''")+"'")).StartMode -ne 'Disabled') { throw 'Служба не подтвердила изменение.' }
    Complete-AuraEntry $entry
}
function Disable-TaskBacked([string]$Pattern) {
    $tasks=@(Get-ScheduledTask | Where-Object { ($_.TaskPath+$_.TaskName) -like $Pattern })
    foreach ($task in $tasks) {
        $original=[pscustomobject]@{ TaskPath=$task.TaskPath; TaskName=$task.TaskName; Enabled=([string]$task.State -ne 'Disabled') }
        $entry=Add-AuraEntry 'Task' ($task.TaskPath+$task.TaskName) $original $false
        Disable-ScheduledTask -TaskPath $task.TaskPath -TaskName $task.TaskName -ErrorAction Stop | Out-Null
        if ([string](Get-ScheduledTask -TaskPath $task.TaskPath -TaskName $task.TaskName).State -ne 'Disabled') { throw 'Задание не подтвердило отключение.' }
        Complete-AuraEntry $entry
    }
}
function Initialize-PowerApi {
    if ('AuraNativePower' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class AuraNativePower {
 [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root,out IntPtr scheme);
 [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root,ref Guid scheme);
 [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root,ref Guid scheme,ref Guid subgroup,ref Guid setting,out uint value);
 [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root,ref Guid scheme,ref Guid subgroup,ref Guid setting,out uint value);
 [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root,ref Guid scheme,ref Guid subgroup,ref Guid setting,uint value);
 [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root,ref Guid scheme,ref Guid subgroup,ref Guid setting,uint value);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr value);
 static void Check(uint error) { if(error!=0) throw new Win32Exception((int)error); }
 public static Guid Active() { IntPtr p; Check(PowerGetActiveScheme(IntPtr.Zero,out p)); try { return (Guid)Marshal.PtrToStructure(p,typeof(Guid)); } finally { LocalFree(p); } }
 public static void Activate(Guid scheme) { Check(PowerSetActiveScheme(IntPtr.Zero,ref scheme)); if(Active()!=scheme) throw new InvalidOperationException("Power scheme readback failed"); }
 public static uint Read(Guid scheme,Guid group,Guid setting,bool ac) { uint v; Check(ac?PowerReadACValueIndex(IntPtr.Zero,ref scheme,ref group,ref setting,out v):PowerReadDCValueIndex(IntPtr.Zero,ref scheme,ref group,ref setting,out v)); return v; }
 public static void Write(Guid scheme,Guid group,Guid setting,bool ac,uint value) { Check(ac?PowerWriteACValueIndex(IntPtr.Zero,ref scheme,ref group,ref setting,value):PowerWriteDCValueIndex(IntPtr.Zero,ref scheme,ref group,ref setting,value)); if(Read(scheme,group,setting,ac)!=value) throw new InvalidOperationException("Power value readback failed"); }
}
'@
}
function Enable-HighPerformancePlan {
    Initialize-PowerApi
    $original=[AuraNativePower]::Active().ToString()
    $target='8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'
    # Do not create extra power plans as a side effect.
    $entry=Add-AuraEntry 'PowerPlan' 'active' $original $target
    [AuraNativePower]::Activate([Guid]$target)
    Complete-AuraEntry $entry
}
function Set-PowerSettingBacked([string]$Type,[int]$Ac,[int]$Dc) {
    Initialize-PowerApi
    $ids=switch ($Type) {
        'disk' { @('0012ee47-9041-4b5d-9b77-535fba8b1442','6738e2c4-e8a5-4a42-b16a-e040e769756e') }
        'processor-min' { @('54533251-82be-4824-96c1-47b60b740d00','893dee8e-2bef-41e0-89c6-b55d0929964c') }
        'usb' { @('2a737441-1930-4402-8d77-b2bebba308a3','48e6b7a6-50f5-4782-a5d4-53bb8f07e226') }
        default { throw 'Неизвестный параметр питания.' }
    }
    $scheme=[AuraNativePower]::Active(); $group=[Guid]$ids[0]; $setting=[Guid]$ids[1]
    $original=[pscustomobject]@{ Scheme=$scheme.ToString(); Group=$group.ToString(); Setting=$setting.ToString(); Ac=[AuraNativePower]::Read($scheme,$group,$setting,$true); Dc=[AuraNativePower]::Read($scheme,$group,$setting,$false) }
    $desired=[pscustomobject]@{ Ac=$(if($Ac -lt 0){$original.Ac}else{$Ac}); Dc=$(if($Dc -lt 0){$original.Dc}else{$Dc}) }
    $entry=Add-AuraEntry 'PowerSetting' ($scheme.ToString()+'|'+$Type) $original $desired
    [AuraNativePower]::Write($scheme,$group,$setting,$true,[uint32]$desired.Ac)
    [AuraNativePower]::Write($scheme,$group,$setting,$false,[uint32]$desired.Dc)
    [AuraNativePower]::Activate($scheme)
    Complete-AuraEntry $entry
}
function Get-HibernationState {
    $value=(Get-Item -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Power').GetValue('HibernateEnabled',$null)
    if ($null -eq $value -or $value -notin @(0,1)) { throw 'Не удалось прочитать исходное состояние гибернации.' }
    return [bool]$value
}
function Set-HibernationBacked([bool]$Enabled) {
    $previous=Get-HibernationState
    if($previous -eq $Enabled) { return }
    if($previous -and -not $Enabled) { throw 'Отключение гибернации недоступно: точное восстановление типа и размера hiberfil.sys не реализовано.' }
    $power='HKLM:\SYSTEM\CurrentControlSet\Control\Power'
    $registry=@('HibernateEnabled','HibernateEnabledDefault','HiberFileType','HiberFileSizePercent' | ForEach-Object { Read-RegSnapshot $power $_ })
    $registry+=Read-RegSnapshot 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' 'HiberbootEnabled'
    $original=[pscustomobject]@{ Enabled=$previous; Registry=$registry }
    $entry=Add-AuraEntry 'Hibernate' 'state' $original $Enabled
    $argument=if($Enabled){'on'}else{'off'}
    Invoke-SystemTool 'powercfg.exe' @('/hibernate',$argument) | Out-Null
    if ((Get-HibernationState) -ne $Enabled) { throw 'Гибернация не подтвердила изменение.' }
    Complete-AuraEntry $entry
}
function Get-NtfsState {
    $output=Invoke-SystemTool 'fsutil.exe' @('behavior','query','DisableLastAccess')
    if ($output -notmatch '=\s*([0-3])\b') { throw 'Не удалось прочитать исходный режим NTFS.' }
    return [int]$Matches[1]
}
function Set-NtfsBacked([int]$Value) {
    $entry=Add-AuraEntry 'Ntfs' 'DisableLastAccess' (Get-NtfsState) $Value
    Invoke-SystemTool 'fsutil.exe' @('behavior','set','DisableLastAccess',[string]$Value) | Out-Null
    if ((Get-NtfsState) -ne $Value) { throw 'NTFS не подтвердил изменение.' }
    Complete-AuraEntry $entry
}
function Set-HypervisorBacked {
    $output=Invoke-SystemTool 'bcdedit.exe' @('/enum','{current}','/v')
    $guids=[regex]::Matches($output,'\{[0-9a-fA-F-]{36}\}')
    if ($guids.Count -eq 0) { throw 'Не удалось определить текущую запись загрузчика.' }
    $loader=$guids[0].Value
    $original=$null
    if ($output -match '(?im)^\s*hypervisorlaunchtype\s+(\S+)') {
        $original=$Matches[1]
        if ($original -notin @('Auto','Off')) { throw 'Неизвестное исходное значение BCD.' }
    }
    $entry=Add-AuraEntry 'Bcd' $loader $original 'Off'
    Invoke-SystemTool 'bcdedit.exe' @('/set',$loader,'hypervisorlaunchtype','Off') | Out-Null
    $verify=Invoke-SystemTool 'bcdedit.exe' @('/enum',$loader,'/v')
    if ($verify -notmatch '(?im)^\s*hypervisorlaunchtype\s+Off\s*$') { throw 'BCD не подтвердил изменение.' }
    Complete-AuraEntry $entry
}
function Get-ReservedStorageState {
    $state=Get-WindowsReservedStorageState -ErrorAction Stop
    $value=if($state.PSObject.Properties['State']){[string]$state.State}else{[string]$state}
    if ($value -notin @('Enabled','Disabled')) { throw 'Не удалось определить состояние Reserved Storage.' }
    return $value
}
function Set-ReservedStorageBacked {
    $entry=Add-AuraEntry 'ReservedStorage' 'state' (Get-ReservedStorageState) 'Disabled'
    Set-WindowsReservedStorageState -State Disabled -ErrorAction Stop | Out-Null
    if ((Get-ReservedStorageState) -ne 'Disabled') { throw 'Reserved Storage не подтвердил изменение.' }
    Complete-AuraEntry $entry
}
function Enable-ClassicContextMenu {
    Set-StringBacked 'HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32' '' ''
}
function Set-AutoHdrToken([bool]$Enabled) {
    $path=Resolve-UserPath 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
    $name='DirectXUserGlobalSettings'
    $current=Read-RegSnapshot $path $name
    $text=if($current.Exists){[string]$current.Value}else{''}
    $tokens=@($text.Split(';',[StringSplitOptions]::RemoveEmptyEntries) | Where-Object { $_ -notlike 'AutoHDREnable=*' })
    $tokens+=('AutoHDREnable='+$(if($Enabled){'1'}else{'0'}))
    Set-StringBacked $path $name (($tokens -join ';')+';')
}
function Enable-GpuMsiMode {
    $devices=@(Get-PnpDevice -Class Display -Status OK -ErrorAction Stop)
    if ($devices.Count -eq 0) { throw 'Нет активных GPU PnP.' }
    foreach ($device in $devices) {
        Set-DwordBacked ('HKLM:\SYSTEM\CurrentControlSet\Enum\'+$device.InstanceId+'\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties') 'MSISupported' 1
    }
}
function Disable-UsbDevicePowerSaving {
    foreach ($device in @(Get-PnpDevice -Class USB -Status OK -ErrorAction Stop)) {
        $path='HKLM:\SYSTEM\CurrentControlSet\Enum\'+$device.InstanceId+'\Device Parameters'
        $current=Read-RegSnapshot $path 'PnPCapabilities'
        $value=if($current.Exists){[uint32]$current.Value}else{[uint32]0}
        Set-DwordBacked $path 'PnPCapabilities' ($value -bor 24)
    }
}
function Remove-AppList([string[]]$Names) { throw 'Удаление AppX заблокировано: нет проверенного источника установочных MSIX для точного восстановления.' }
function Restore-AuraEntry($Entry) {
    $original=$Entry.Original
    switch ($Entry.Kind) {
        'Registry' {
            if ($original.Exists) {
                if (-not (Test-Path -LiteralPath $original.Path)) { New-Item -Path $original.Path -Force | Out-Null }
                (Get-Item -LiteralPath $original.Path).SetValue([string]$original.Name,$original.Value,[Enum]::Parse([Microsoft.Win32.RegistryValueKind],[string]$original.Kind))
            } elseif (Test-Path -LiteralPath $original.Path) {
                (Get-Item -LiteralPath $original.Path).DeleteValue([string]$original.Name,$false)
            }
            if (-not (Test-RegSnapshot $original)) { throw 'Исходное значение реестра не восстановлено.' }
        }
        'CreatedKey' {
            if (Test-Path -LiteralPath $Entry.Key) {
                $key=Get-Item -LiteralPath $Entry.Key
                if ($key.GetValueNames().Length -ne 0 -or $key.GetSubKeyNames().Length -ne 0) { throw 'В созданном ключе есть другие данные; они сохранены.' }
                Remove-Item -LiteralPath $Entry.Key -ErrorAction Stop
            }
        }
        'Service' {
            $startup=switch ([string]$original.StartMode) { 'Auto' {'Automatic'} 'Disabled' {'Disabled'} default {'Manual'} }
            Set-Service -Name $Entry.Key -StartupType $startup -ErrorAction Stop
            if ($original.StartMode -eq 'Auto' -and $original.Delayed -eq 1) {
                Invoke-SystemTool 'sc.exe' @('config',$Entry.Key,'start=','delayed-auto') | Out-Null
            }
            $serviceKey=Get-Item -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$Entry.Key)
            if ($original.DelayedExists) { $serviceKey.SetValue('DelayedAutoStart',[int]$original.Delayed,[Microsoft.Win32.RegistryValueKind]::DWord) }
            else { $serviceKey.DeleteValue('DelayedAutoStart',$false) }
            if ($original.State -eq 'Running') { Start-Service -Name $Entry.Key -ErrorAction Stop }
            elseif ($original.State -eq 'Paused') { Start-Service -Name $Entry.Key -ErrorAction Stop; Suspend-Service -Name $Entry.Key -ErrorAction Stop }
            else { Stop-Service -Name $Entry.Key -ErrorAction Stop }
            (Get-Service -Name $Entry.Key).WaitForStatus($original.State,[TimeSpan]::FromSeconds(10))
            $cim=Get-CimInstance Win32_Service -Filter ("Name='"+$Entry.Key.Replace("'","''")+"'")
            if ($cim.StartMode -ne $original.StartMode) { throw 'Служба не подтвердила исходный режим.' }
            if ($serviceKey.GetValue('DelayedAutoStart',0) -ne $original.Delayed) { throw 'Служба не подтвердила исходную задержку.' }
        }
        'Task' {
            if ($original.Enabled) { Enable-ScheduledTask -TaskPath $original.TaskPath -TaskName $original.TaskName -ErrorAction Stop | Out-Null }
            else { Disable-ScheduledTask -TaskPath $original.TaskPath -TaskName $original.TaskName -ErrorAction Stop | Out-Null }
            $enabled=[string](Get-ScheduledTask -TaskPath $original.TaskPath -TaskName $original.TaskName).State -ne 'Disabled'
            if ($enabled -ne $original.Enabled) { throw 'Задание не подтвердило исходное состояние.' }
        }
        'PowerSetting' {
            Initialize-PowerApi
            [AuraNativePower]::Write([Guid]$original.Scheme,[Guid]$original.Group,[Guid]$original.Setting,$true,[uint32]$original.Ac)
            [AuraNativePower]::Write([Guid]$original.Scheme,[Guid]$original.Group,[Guid]$original.Setting,$false,[uint32]$original.Dc)
            if([AuraNativePower]::Active() -eq [Guid]$original.Scheme) { [AuraNativePower]::Activate([Guid]$original.Scheme) }
        }
        'PowerPlan' { Initialize-PowerApi; [AuraNativePower]::Activate([Guid]$original) }
        'Hibernate' {
            if(-not $original.PSObject.Properties['Enabled'] -or $original.Enabled) { throw 'Снимок гибернации прежнего формата или неподдерживаемый тип; требуется ручная проверка.' }
            Invoke-SystemTool 'powercfg.exe' @('/hibernate','off') | Out-Null
            foreach($registry in $original.Registry) {
                $restore=[pscustomobject]@{ Kind='Registry'; Original=$registry; Status='Prepared' }
                Restore-AuraEntry $restore
            }
            if ((Get-HibernationState) -ne [bool]$original.Enabled) { throw 'Гибернация не восстановлена.' }
        }
        'Ntfs' {
            Invoke-SystemTool 'fsutil.exe' @('behavior','set','DisableLastAccess',[string]$original) | Out-Null
            if ((Get-NtfsState) -ne [int]$original) { throw 'NTFS не восстановлен.' }
        }
        'Bcd' {
            if ($null -eq $original) {
                $output=Invoke-SystemTool 'bcdedit.exe' @('/enum',$Entry.Key,'/v')
                if ($output -match '(?im)^\s*hypervisorlaunchtype\s+') { Invoke-SystemTool 'bcdedit.exe' @('/deletevalue',$Entry.Key,'hypervisorlaunchtype') | Out-Null }
            } else { Invoke-SystemTool 'bcdedit.exe' @('/set',$Entry.Key,'hypervisorlaunchtype',[string]$original) | Out-Null }
            $verify=Invoke-SystemTool 'bcdedit.exe' @('/enum',$Entry.Key,'/v')
            if ($null -eq $original -and $verify -match '(?im)^\s*hypervisorlaunchtype\s+') { throw 'BCD не восстановлен.' }
            if ($null -ne $original -and $verify -notmatch ('(?im)^\s*hypervisorlaunchtype\s+'+[regex]::Escape([string]$original)+'\s*$')) { throw 'BCD не подтвердил исходное значение.' }
        }
        'ReservedStorage' {
            Set-WindowsReservedStorageState -State $original -ErrorAction Stop | Out-Null
            if ((Get-ReservedStorageState) -ne $original) { throw 'Reserved Storage не восстановлен.' }
        }
        default { throw 'Неизвестный тип операции.' }
    }
    $Entry.Status='Restored'
    Save-AuraBackups
}
function Restore-AuraBackups {
    $entries=@($AuraJournal.ToArray())
    for ($index=$entries.Count-1; $index -ge 0; $index--) {
        if ($entries[$index].Status -eq 'Restored') { continue }
        try { Restore-AuraEntry $entries[$index] }
        catch { Add-AuraError $entries[$index].Key $_.Exception.Message }
    }
}
function Clear-AuraBackups {
    if ($AuraErrors.Count -gt 0 -or @($AuraJournal | Where-Object { $_.Status -ne 'Restored' }).Count -gt 0) { throw 'Откат не завершён. Снимок сохранён.' }
    if (Test-Path -LiteralPath $AuraStatePath) { Remove-Item -LiteralPath $AuraStatePath -ErrorAction Stop }
}
function Finish-AuraOperation {
    if (-not $AuraSkipBackupSave) { try { Save-AuraBackups } catch { Add-AuraError 'Сохранение состояния' $_.Exception.Message } }
    $result=[pscustomobject]@{ RunId=$RunId; RecordedAtUtc=[DateTime]::UtcNow.ToString('o'); Success=($AuraErrors.Count -eq 0); Actions=@($AuraActions | Where-Object { $_.PSObject.Properties['RunId'] -and $_.RunId -eq $RunId }); Errors=@($AuraErrors.ToArray()); RestorePoint=$AuraRestorePoint; LogPath=$LogPath; Remaining=@($AuraJournal | Where-Object { $_.Status -ne 'Restored' }).Count }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $AuraResultPath -Encoding UTF8
    try { Stop-Transcript | Out-Null } catch {}
    if ($script:AuraMutex) { $AuraMutex.ReleaseMutex(); $AuraMutex.Dispose() }
    if ($AuraErrors.Count -gt 0) { exit 2 }
    exit 0
}
function Invoke-AuraApply($Selections) {
    Initialize-AuraRuntime
    try {
        try {
            Enable-ComputerRestore -Drive ([IO.Path]::GetPathRoot([Environment]::SystemDirectory)) -ErrorAction Stop
            Checkpoint-Computer -Description 'AURA before Windows tweaks' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop
            $script:AuraRestorePoint='Создана'
        } catch { $script:AuraRestorePoint='Недоступна: '+$_.Exception.Message }
        foreach ($selection in $Selections) {
            $script:AuraCurrentId=$selection.Id
            $action=[pscustomobject]@{ RunId=$RunId; Id=$selection.Id; Title=$selection.Title; Status='Pending'; Detail=''; TimeUtc=[DateTime]::UtcNow.ToString('o') }
            $AuraActions.Add($action)
            Save-AuraBackups
            try {
                if ($selection.RequiresWindows11 -and [Environment]::OSVersion.Version.Build -lt 22000) {
                    $action.Status='Skipped'; $action.Detail='Требуется Windows 11'
                } else {
                    Write-Step $selection.Title
                    $script:AuraTouched=0
                    & $selection.Script
                    if($AuraTouched -gt 0) { $action.Status='Applied' }
                    else { $action.Status='Skipped'; $action.Detail='Доступных изменений для этой системы не найдено' }
                }
            } catch { $action.Status='Failed'; $action.Detail=$_.Exception.Message; Add-AuraError $selection.Title $_.Exception.Message }
            Save-AuraBackups
        }
        if($AuraErrors.Count -eq 0 -and $AuraJournal.Count -eq 0) { Clear-AuraBackups; $script:AuraSkipBackupSave=$true }
    } catch { Add-AuraError 'Операция' $_.Exception.Message }
    Finish-AuraOperation
}
function Invoke-AuraRestore {
    Initialize-AuraRuntime
    try {
        Restore-AuraBackups
        $ids=@($AuraJournal | ForEach-Object { if($_.PSObject.Properties['TweakIds']){$_.TweakIds}else{$_.TweakId} } | Select-Object -Unique)
        foreach($id in $ids) {
            $entries=@($AuraJournal | Where-Object { $_.TweakId -eq $id -or ($_.PSObject.Properties['TweakIds'] -and $_.TweakIds -contains $id) })
            $failed=@($entries | Where-Object { $_.Status -ne 'Restored' }).Count -gt 0
            $prior=@($AuraActions | Where-Object { $_.Id -eq $id }) | Select-Object -Last 1
            $title=if($prior){$prior.Title}else{$id}
            $AuraActions.Add([pscustomobject]@{ RunId=$RunId; Id=$id; Title=$title; Status=$(if($failed){'Failed'}else{'Restored'}); Detail='Возврат сохранённого состояния'; TimeUtc=[DateTime]::UtcNow.ToString('o') })
        }
        if ($AuraErrors.Count -eq 0) {
            foreach ($action in $AuraActions) { if ($action.Status -eq 'Applied') { $action.Status='Restored' } }
            Clear-AuraBackups; $script:AuraSkipBackupSave=$true
        }
    } catch { Add-AuraError 'Восстановление' $_.Exception.Message }
    Finish-AuraOperation
}

