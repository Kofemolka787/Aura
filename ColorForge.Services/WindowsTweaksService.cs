using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed record WindowsTweaksResult(bool Success, int ExitCode, string Message, bool RestartRecommended);

public sealed record WindowsTweaksAudit(int DefinitionCount, int SafeCount, int CautionCount, int DangerCount, IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
}

public sealed class WindowsTweaksService
{
    public IReadOnlyList<WindowsTweakDefinition> Catalog => WindowsTweakCatalog.All;

    public static WindowsTweaksAudit AuditCatalog()
    {
        IReadOnlyList<string> errors = WindowsTweakCatalog.ValidateCatalog();
        return new WindowsTweaksAudit(
            WindowsTweakCatalog.All.Count,
            WindowsTweakCatalog.All.Count(x => x.Risk == WindowsTweakRisk.Safe),
            WindowsTweakCatalog.All.Count(x => x.Risk == WindowsTweakRisk.Caution),
            WindowsTweakCatalog.All.Count(x => x.Risk == WindowsTweakRisk.Danger),
            errors);
    }

    public static void InitializePreferences(WindowsTweakPreferences options, bool migrateLegacy)
    {
        if (options.CatalogVersion >= WindowsTweakCatalog.Version && options.EnabledTweakIds != null)
            return;

        HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
        if (migrateLegacy)
        {
            if (options.FastStartup) selected.Add("fast_startup");
            if (options.RemoveStartupDelay) selected.Add("remove_startup_delay");
            if (options.GamingPerformance)
            {
                selected.Add("disable_xbox_dvr");
                selected.Add("game_task_priority");
                selected.Add("disable_network_throttling");
            }
            if (options.ReduceVisualEffects) selected.Add("performance_over_appearance");
            if (options.DisableBackgroundApps) selected.Add("disable_background_apps");
            if (options.ReduceTelemetry)
            {
                selected.Add("disable_telemetry");
                selected.Add("disable_advertising_id");
                selected.Add("disable_tailored_experiences");
                selected.Add("disable_consumer_features");
            }
            if (options.RemoveCopilot) selected.Add("disable_copilot");
            if (options.RemoveConsumerApps) selected.Add("remove_consumer_apps");
            if (options.RemoveXboxApps) selected.Add("remove_xbox_components");
            if (options.DisableSysMain) selected.Add("disable_sysmain");
            if (options.DisableWindowsSearch) selected.Add("disable_search_indexing");
            if (options.DisablePrintSpooler) selected.Add("disable_print_spooler");
            if (options.RemoveMicrosoftStore) selected.Add("remove_microsoft_store");
        }
        else
        {
            selected.UnionWith(WindowsTweakCatalog.ProfileIds(WindowsTweakProfile.Low));
        }

        options.EnabledTweakIds = selected
            .Where(id => WindowsTweakCatalog.All.Any(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        options.CatalogVersion = WindowsTweakCatalog.Version;
    }

    public IReadOnlyList<string> ValidateSelection(IEnumerable<string> ids)
    {
        HashSet<string> selected = new(ids ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> known = new(Catalog.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
        List<string> errors = selected.Where(id => !known.Contains(id)).Select(id => $"Неизвестный твик: {id}").ToList();

        if (selected.Contains("fast_startup") && selected.Contains("disable_hibernation"))
            errors.Add("«Быстрый запуск» и «Отключить гибернацию» несовместимы. Оставь один из двух пунктов.");

        return errors;
    }

    public Task<WindowsTweaksResult> ApplyAsync(WindowsTweakPreferences options, CancellationToken cancellationToken = default)
    {
        string[] selectedIds = (options.EnabledTweakIds ?? new List<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyList<string> errors = ValidateSelection(selectedIds);
        if (errors.Count > 0)
            return Task.FromResult(new WindowsTweaksResult(false, -1, string.Join(Environment.NewLine, errors), false));
        if (selectedIds.Length == 0)
            return Task.FromResult(new WindowsTweaksResult(false, -1, "Не выбран ни один твик.", false));

        IReadOnlyList<WindowsTweakDefinition> selected = Catalog
            .Where(item => selectedIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return RunElevatedAsync(BuildApplyScript(selected), $"Применено твиков: {selected.Count}", true, cancellationToken);
    }

    public Task<WindowsTweaksResult> RestoreDefaultsAsync(CancellationToken cancellationToken = default) =>
        RunElevatedAsync(BuildRestoreScript(), "Сохранённые исходные параметры восстановлены", true, cancellationToken);

    internal static string BuildApplyScript(IReadOnlyList<WindowsTweakDefinition> selected)
    {
        StringBuilder script = new();
        script.AppendLine(CommonPreamble);
        script.AppendLine("Write-Step 'Создание точки восстановления'");
        script.AppendLine("try { Enable-ComputerRestore -Drive \"$($env:SystemDrive)\\\" -ErrorAction SilentlyContinue; Checkpoint-Computer -Description 'AURA before Windows tweaks' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop; Write-Output 'Точка восстановления создана.' } catch { Write-Output ('Точка восстановления недоступна: ' + $_.Exception.Message) }");

        foreach (WindowsTweakDefinition item in selected)
        {
            script.AppendLine("try {");
            script.AppendLine($"  Write-Step '{EscapePowerShell(item.Title)}'");
            if (item.RequiresWindows11)
            {
                script.AppendLine("  if ([Environment]::OSVersion.Version.Build -lt 22000) { Write-Output 'Пропущено: требуется Windows 11.' } else {");
                script.AppendLine(Indent(item.ApplyScript, 4));
                script.AppendLine("  }");
            }
            else
            {
                script.AppendLine(Indent(item.ApplyScript, 2));
            }
            script.AppendLine($"}} catch {{ Add-AuraError '{EscapePowerShell(item.Title)}' $_.Exception.Message }}");
        }

        script.AppendLine(CommonPostamble);
        return script.ToString();
    }

    internal static string BuildRestoreScript()
    {
        StringBuilder script = new();
        script.AppendLine(CommonPreamble);
        script.AppendLine("Write-Step 'Откат команд без реестрового снимка'");
        foreach (WindowsTweakDefinition item in WindowsTweakCatalog.All.Where(item => !string.IsNullOrWhiteSpace(item.RestoreScript)))
        {
            script.AppendLine("try {");
            script.AppendLine(Indent(item.RestoreScript, 2));
            script.AppendLine($"}} catch {{ Add-AuraError 'Откат: {EscapePowerShell(item.Title)}' $_.Exception.Message }}");
        }
        script.AppendLine("try { Restore-BuiltInApps } catch { Add-AuraError 'Встроенные приложения' $_.Exception.Message }");
        script.AppendLine("try { Restore-AuraBackups } catch { Add-AuraError 'Снимок исходных параметров' $_.Exception.Message }");
        script.AppendLine("if ($AuraErrors.Count -eq 0) { Clear-AuraBackups; $AuraSkipBackupSave = $true }");
        script.AppendLine(CommonPostamble);
        return script.ToString();
    }

    private static async Task<WindowsTweaksResult> RunElevatedAsync(
        string script,
        string successMessage,
        bool restartRecommended,
        CancellationToken cancellationToken)
    {
        string scriptDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AURA",
            "Runtime");
        Directory.CreateDirectory(scriptDirectory);
        string scriptPath = Path.Combine(scriptDirectory, $"tweaks-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Не удалось запустить PowerShell.");
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0
                ? new WindowsTweaksResult(true, 0, successMessage, restartRecommended)
                : new WindowsTweaksResult(false, process.ExitCode, "Некоторые операции не выполнены. Подробности: %TEMP%\\AURA-tweaks-last.log", false);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new WindowsTweaksResult(false, 1223, "Запрос прав администратора отменён.", false);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string Indent(string value, int spaces)
    {
        string prefix = new(' ', spaces);
        return string.Join(Environment.NewLine, value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => prefix + line));
    }

    private const string CommonPreamble = """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$LogPath = Join-Path $env:TEMP 'AURA-tweaks-last.log'
$AuraRoot = Join-Path $env:ProgramData 'AURA\Tweaks'
$RegistryBackupPath = Join-Path $AuraRoot 'registry.clixml'
$ServiceBackupPath = Join-Path $AuraRoot 'services.clixml'
$TaskBackupPath = Join-Path $AuraRoot 'tasks.clixml'
New-Item -Path $AuraRoot -ItemType Directory -Force | Out-Null
try { Start-Transcript -Path $LogPath -Force | Out-Null } catch {}

$AuraErrors = [System.Collections.Generic.List[string]]::new()
$AuraSkipBackupSave = $false
$AuraRegistryBackup = if (Test-Path $RegistryBackupPath) { @(Import-Clixml $RegistryBackupPath) } else { @() }
$AuraServiceBackup = if (Test-Path $ServiceBackupPath) { @(Import-Clixml $ServiceBackupPath) } else { @() }
$AuraTaskBackup = if (Test-Path $TaskBackupPath) { @(Import-Clixml $TaskBackupPath) } else { @() }

function Write-Step([string]$Name) {
  Write-Output ''
  Write-Output ('=== ' + $Name + ' ===')
}
function Add-AuraError([string]$Name, [string]$Message) {
  $entry = $Name + ': ' + $Message
  $AuraErrors.Add($entry)
  Write-Warning $entry
}
function Ensure-Key([string]$Path) {
  if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }
}
function Backup-RegValue([string]$Path, [string]$Name) {
  if ($AuraRegistryBackup | Where-Object { $_.Path -ieq $Path -and $_.Name -ieq $Name }) { return }
  $exists = $false
  $kind = 'String'
  $value = $null
  if (Test-Path $Path) {
    $key = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($null -ne $key -and $key.GetValueNames() -contains $Name) {
      $exists = $true
      $kind = $key.GetValueKind($Name).ToString()
      $value = $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    }
  }
  $script:AuraRegistryBackup += [pscustomobject]@{ Path=$Path; Name=$Name; Exists=$exists; Kind=$kind; Value=$value }
}
function Set-DwordBacked([string]$Path, [string]$Name, [uint32]$Value) {
  Backup-RegValue $Path $Name
  Ensure-Key $Path
  New-ItemProperty -LiteralPath $Path -Name $Name -PropertyType DWord -Value $Value -Force | Out-Null
}
function Set-StringBacked([string]$Path, [string]$Name, [string]$Value) {
  Backup-RegValue $Path $Name
  Ensure-Key $Path
  if ($Name -eq '') { (Get-Item -LiteralPath $Path).SetValue('', $Value, [Microsoft.Win32.RegistryValueKind]::String) }
  else { New-ItemProperty -LiteralPath $Path -Name $Name -PropertyType String -Value $Value -Force | Out-Null }
}
function Set-BinaryBacked([string]$Path, [string]$Name, [byte[]]$Value) {
  Backup-RegValue $Path $Name
  Ensure-Key $Path
  New-ItemProperty -LiteralPath $Path -Name $Name -PropertyType Binary -Value $Value -Force | Out-Null
}
function Remove-RegValueBacked([string]$Path, [string]$Name) {
  Backup-RegValue $Path $Name
  if (Test-Path $Path) { Remove-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue }
}
function Backup-ServiceState([string]$Name) {
  if ($AuraServiceBackup | Where-Object { $_.Name -ieq $Name }) { return }
  $service = Get-CimInstance Win32_Service -Filter ("Name='" + $Name.Replace("'", "''") + "'") -ErrorAction SilentlyContinue
  if ($null -ne $service) {
    $script:AuraServiceBackup += [pscustomobject]@{ Name=$Name; StartMode=$service.StartMode; State=$service.State }
  }
}
function Disable-ServiceBacked([string]$Name) {
  $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
  if ($null -eq $service) { Write-Output ($Name + ': служба не установлена'); return }
  Backup-ServiceState $Name
  Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
  Set-Service -Name $Name -StartupType Disabled
}
function Disable-TaskBacked([string]$Pattern) {
  $tasks = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { ($_.TaskPath + $_.TaskName) -like $Pattern })
  foreach ($task in $tasks) {
    $full = $task.TaskPath + $task.TaskName
    if (-not ($AuraTaskBackup | Where-Object { ($_.TaskPath + $_.TaskName) -ieq $full })) {
      $script:AuraTaskBackup += [pscustomobject]@{ TaskPath=$task.TaskPath; TaskName=$task.TaskName; Enabled=([string]$task.State -ne 'Disabled') }
    }
    Disable-ScheduledTask -TaskPath $task.TaskPath -TaskName $task.TaskName -ErrorAction SilentlyContinue | Out-Null
  }
}
function Save-AuraBackups {
  @($AuraRegistryBackup) | Export-Clixml -Path $RegistryBackupPath -Force
  @($AuraServiceBackup) | Export-Clixml -Path $ServiceBackupPath -Force
  @($AuraTaskBackup) | Export-Clixml -Path $TaskBackupPath -Force
}
function Restore-AuraBackups {
  foreach ($entry in @($AuraRegistryBackup)) {
    if ($entry.Exists) {
      Ensure-Key $entry.Path
      $kind = [Enum]::Parse([Microsoft.Win32.RegistryValueKind], [string]$entry.Kind)
      (Get-Item -LiteralPath $entry.Path).SetValue([string]$entry.Name, $entry.Value, $kind)
    } elseif (Test-Path $entry.Path) {
      try { (Get-Item -LiteralPath $entry.Path).DeleteValue([string]$entry.Name, $false) } catch {}
    }
  }
  foreach ($entry in @($AuraServiceBackup)) {
    $startup = switch ([string]$entry.StartMode) { 'Auto' { 'Automatic' } 'Disabled' { 'Disabled' } default { 'Manual' } }
    Set-Service -Name $entry.Name -StartupType $startup -ErrorAction SilentlyContinue
    if ($entry.State -eq 'Running') { Start-Service -Name $entry.Name -ErrorAction SilentlyContinue }
    else { Stop-Service -Name $entry.Name -Force -ErrorAction SilentlyContinue }
  }
  foreach ($entry in @($AuraTaskBackup)) {
    if ($entry.Enabled) { Enable-ScheduledTask -TaskPath $entry.TaskPath -TaskName $entry.TaskName -ErrorAction SilentlyContinue | Out-Null }
    else { Disable-ScheduledTask -TaskPath $entry.TaskPath -TaskName $entry.TaskName -ErrorAction SilentlyContinue | Out-Null }
  }
}
function Clear-AuraBackups {
  Remove-Item -LiteralPath $RegistryBackupPath,$ServiceBackupPath,$TaskBackupPath -Force -ErrorAction SilentlyContinue
}
function Remove-AppList([string[]]$Names) {
  foreach ($name in $Names) {
    Get-AppxPackage -Name $name -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue
  }
}
function Restore-BuiltInApps {
  Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue | ForEach-Object {
    $manifest = Join-Path $_.InstallLocation 'AppXManifest.xml'
    if (Test-Path $manifest) { Add-AppxPackage -DisableDevelopmentMode -Register $manifest -ErrorAction SilentlyContinue }
  }
}
function Enable-HighPerformancePlan {
  powercfg.exe /setactive SCHEME_MIN | Out-Null
  if ($LASTEXITCODE -ne 0) {
    $duplicate = powercfg.exe /duplicatescheme SCHEME_MIN
    if ($duplicate -match '([0-9a-fA-F-]{36})') { powercfg.exe /setactive $Matches[1] | Out-Null }
  }
}
function Enable-ClassicContextMenu {
  $path = 'HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32'
  Set-StringBacked $path '' ''
}
function Disable-ClassicContextMenu {
  Remove-Item -LiteralPath 'HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}' -Recurse -Force -ErrorAction SilentlyContinue
}
function Set-AutoHdrToken([bool]$Enabled) {
  $path = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
  $name = 'DirectXUserGlobalSettings'
  $current = if (Test-Path $path) { [string](Get-ItemProperty -LiteralPath $path -Name $name -ErrorAction SilentlyContinue).$name } else { '' }
  $tokens = @($current.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries) | Where-Object { $_ -notlike 'AutoHDREnable=*' })
  $tokens += ('AutoHDREnable=' + $(if ($Enabled) { '1' } else { '0' }))
  Set-StringBacked $path $name (($tokens -join ';') + ';')
}
function Enable-GpuMsiMode {
  $devices = @(Get-PnpDevice -Class Display -Status OK -ErrorAction SilentlyContinue)
  if ($devices.Count -eq 0) { throw 'Не найдена активная видеокарта PnP.' }
  foreach ($device in $devices) {
    $path = 'HKLM:\SYSTEM\CurrentControlSet\Enum\' + $device.InstanceId + '\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties'
    Set-DwordBacked $path 'MSISupported' 1
  }
}
function Disable-UsbDevicePowerSaving {
  $devices = @(Get-PnpDevice -Class USB -Status OK -ErrorAction SilentlyContinue)
  foreach ($device in $devices) {
    $path = 'HKLM:\SYSTEM\CurrentControlSet\Enum\' + $device.InstanceId + '\Device Parameters'
    $old = [uint32]0
    if (Test-Path $path) { $old = [uint32]((Get-ItemProperty -LiteralPath $path -Name 'PnPCapabilities' -ErrorAction SilentlyContinue).PnPCapabilities) }
    Set-DwordBacked $path 'PnPCapabilities' ($old -bor 24)
  }
}

""";

    private const string CommonPostamble = """

if (-not $AuraSkipBackupSave) {
  try { Save-AuraBackups } catch { Add-AuraError 'Сохранение отката' $_.Exception.Message }
}
Write-Step 'Готово'
if ($AuraErrors.Count -gt 0) {
  Write-Output ('Ошибок: ' + $AuraErrors.Count)
  $AuraErrors | ForEach-Object { Write-Output $_ }
  try { Stop-Transcript | Out-Null } catch {}
  exit 2
}
Write-Output 'AURA завершила операцию. Для полного эффекта перезагрузи компьютер.'
try { Stop-Transcript | Out-Null } catch {}
exit 0
""";
}
