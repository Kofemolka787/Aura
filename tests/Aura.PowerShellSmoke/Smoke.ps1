param([string]$RuntimePath = (Join-Path $PSScriptRoot '../../Assets/Runtime/WindowsTweaks.ps1'))
$ErrorActionPreference='Stop'
Set-StrictMode -Version 3.0
$tokens=$null; $errors=$null
[System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $RuntimePath),[ref]$tokens,[ref]$errors) | Out-Null
if($errors.Count){ throw ($errors | Out-String) }
. $RuntimePath -TargetSid 'S-1-5-21-1-2-3-1001' -RunId ('1'*32)
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
public static class TestRegistry {
 public static Dictionary<string,TestKey> Keys = new Dictionary<string,TestKey>(StringComparer.OrdinalIgnoreCase);
 public static Action BeforeWrite;
 public static bool FailWrite;
}
public sealed class TestKey {
 public string Path;
 public Dictionary<string,object> Values = new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
 public Dictionary<string,RegistryValueKind> Kinds = new Dictionary<string,RegistryValueKind>(StringComparer.OrdinalIgnoreCase);
 public TestKey(string path){Path=path;}
 public string[] GetValueNames(){return Values.Keys.ToArray();}
 public string[] GetSubKeyNames(){return TestRegistry.Keys.Keys.Where(p=>p.StartsWith(Path+"\\",StringComparison.OrdinalIgnoreCase)&&!p.Substring(Path.Length+1).Contains("\\")).ToArray();}
 public RegistryValueKind GetValueKind(string name){return Kinds[name];}
 public object GetValue(string name,object fallback,RegistryValueOptions options){return Values.ContainsKey(name)?Values[name]:fallback;}
 public void SetValue(string name,object value,RegistryValueKind kind){if(TestRegistry.BeforeWrite!=null)TestRegistry.BeforeWrite.Invoke(); if(TestRegistry.FailWrite)throw new InvalidOperationException("fixture write failure");Values[name]=value;Kinds[name]=kind;}
 public void DeleteValue(string name,bool throwIfMissing){Values.Remove(name);Kinds.Remove(name);}
}
'@
$root=Join-Path ([IO.Path]::GetTempPath()) ('aura-ps-tests-'+[Guid]::NewGuid().ToString('N'))
New-Item -Path $root -ItemType Directory | Out-Null
$script:AuraStatePath=Join-Path $root 'history.clixml'
$script:Checks=0
function Assert-Review([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message};$script:Checks++;Write-Output ('PASS: '+$Message)}
function Is-RegPath([string]$Path){return $Path.StartsWith('HKU:',[StringComparison]::OrdinalIgnoreCase)-or $Path.StartsWith('HKLM:',[StringComparison]::OrdinalIgnoreCase)}
function Test-Path {param([string]$Path,[string]$LiteralPath)
 $p=if($LiteralPath){$LiteralPath}else{$Path}
 if(Is-RegPath $p){return [TestRegistry]::Keys.ContainsKey($p)}
 return Microsoft.PowerShell.Management\Test-Path -LiteralPath $p
}
function Get-Item {param([string]$Path,[string]$LiteralPath)
 $p=if($LiteralPath){$LiteralPath}else{$Path}
 if(Is-RegPath $p){return [TestRegistry]::Keys[$p]}
 return Microsoft.PowerShell.Management\Get-Item -LiteralPath $p
}
function New-Item {param([string]$Path,[string]$ItemType,[switch]$Force)
 if(Is-RegPath $Path){[TestRegistry]::Keys[$Path]=[TestKey]::new($Path);return [TestRegistry]::Keys[$Path]}
 return Microsoft.PowerShell.Management\New-Item @PSBoundParameters
}
function Split-Path {param([string]$Path,[switch]$Parent)
 if(Is-RegPath $Path){return $Path.Substring(0,$Path.LastIndexOf('\'))}
 return Microsoft.PowerShell.Management\Split-Path @PSBoundParameters
}
function Remove-Item {param([string]$Path,[string]$LiteralPath,[string]$ErrorAction)
 $p=if($LiteralPath){$LiteralPath}else{$Path}
 if(Is-RegPath $p){[TestRegistry]::Keys.Remove($p)|Out-Null;return}
 Microsoft.PowerShell.Management\Remove-Item -LiteralPath $p -ErrorAction Stop
}
function Reset-Fixture {
 $AuraJournal.Clear();$AuraActions.Clear();$AuraErrors.Clear();[TestRegistry]::Keys.Clear()
 [TestRegistry]::BeforeWrite=$null;[TestRegistry]::FailWrite=$false
 $base='HKU:\'+$TargetSid
 [TestRegistry]::Keys[$base]=[TestKey]::new($base)
 [TestRegistry]::Keys[$base+'\Software']=[TestKey]::new($base+'\Software')
 if([IO.File]::Exists($AuraStatePath)){[IO.File]::Delete($AuraStatePath)}
}
try {
 Reset-Fixture
 Assert-Review ((Resolve-UserPath 'HKCU:\Software\AURA') -eq ('HKU:\'+$TargetSid+'\Software\AURA')) 'HKCU writes resolve to original user SID'
 $path='HKU:\'+$TargetSid+'\Software'
 $key=[TestRegistry]::Keys[$path];$key.SetValue('Existing',7,[Microsoft.Win32.RegistryValueKind]::DWord)
 [TestRegistry]::BeforeWrite=[Action]{
  $saved=Import-Clixml -LiteralPath $AuraStatePath
  if(@($saved.Entries|Where-Object {$_.Kind -eq 'Registry'}).Count -eq 0){throw 'mutation without durable backup'}
 }
 Set-DwordBacked 'HKCU:\Software' 'Existing' 99
 Assert-Review ($key.Values['Existing'] -eq 99) 'registry changes execute after durable original snapshot'
 Set-DwordBacked 'HKCU:\Software\NewParent\NewChild' 'Temporary' 1
 [TestRegistry]::BeforeWrite=$null
 Restore-AuraBackups
 Assert-Review ($key.Values['Existing'] -eq 7) 'restore returns exact previous registry value'
 Assert-Review (-not [TestRegistry]::Keys.ContainsKey($path+'\NewParent')) 'new keys are removed in reverse order without unrelated changes'
 Clear-AuraBackups
 Assert-Review (-not [IO.File]::Exists($AuraStatePath)) 'backup is cleared only after verified complete restoration'

 Reset-Fixture
 $path='HKU:\'+$TargetSid+'\Software';$key=[TestRegistry]::Keys[$path]
 $key.SetValue('Existing',7,[Microsoft.Win32.RegistryValueKind]::DWord)
 Set-DwordBacked 'HKCU:\Software' 'Existing' 99
 Restore-AuraBackups
 $key.SetValue('Existing',21,[Microsoft.Win32.RegistryValueKind]::DWord)
 Set-DwordBacked 'HKCU:\Software' 'Existing' 88
 Restore-AuraBackups
 Assert-Review ($key.Values['Existing'] -eq 21) 'reapply after partial restore captures the fresh original value'

 Reset-Fixture
 $path='HKU:\'+$TargetSid+'\Software';$key=[TestRegistry]::Keys[$path]
 $key.SetValue('Shared',7,[Microsoft.Win32.RegistryValueKind]::DWord)
 $script:AuraCurrentId='fixture_a';Set-DwordBacked 'HKCU:\Software' 'Shared' 99
 $script:AuraCurrentId='fixture_b';Set-DwordBacked 'HKCU:\Software' 'Shared' 88
 $entry=@($AuraJournal|Where-Object {$_.Kind -eq 'Registry'})[0]
 Assert-Review ($entry.TweakIds -contains 'fixture_a' -and $entry.TweakIds -contains 'fixture_b' -and $entry.Original.Value -eq 7) 'shared registry writes retain every tweak ID and a single exact original'
 $script:AuraCurrentId=''

 Reset-Fixture
 $script:NativeCalls=0
 function Invoke-SystemTool {param($Name,$Arguments)$script:NativeCalls++;throw 'unexpected unrelated native action'}
 Restore-AuraBackups
 Assert-Review ($script:NativeCalls -eq 0) 'empty history does not alter power plan, hibernation or applications'
 function Get-HibernationState {return $true}
 Set-HibernationBacked $true
 $blocked=$false
 try{Set-HibernationBacked $false}catch{$blocked=$true}
 Assert-Review ($blocked -and $script:NativeCalls -eq 0 -and $AuraJournal.Count -eq 0) 'unchanged hibernation is not rewritten and unsupported removal is blocked before writes'
 Remove-Item Function:\Get-HibernationState
 Remove-Item Function:\Invoke-SystemTool
 . $RuntimePath -TargetSid 'S-1-5-21-1-2-3-1001' -RunId ('1'*32)
 $script:AuraStatePath=Join-Path $root 'history.clixml'

 Reset-Fixture
 $path='HKU:\'+$TargetSid+'\Software';$key=[TestRegistry]::Keys[$path]
 $key.SetValue('Existing',7,[Microsoft.Win32.RegistryValueKind]::DWord)
 Set-DwordBacked 'HKCU:\Software' 'Existing' 99
 [TestRegistry]::FailWrite=$true
 Restore-AuraBackups
 Assert-Review ($AuraErrors.Count -gt 0 -and [IO.File]::Exists($AuraStatePath)) 'failed restoration retains backup and reports failure'
 $cleared=$false
 try{Clear-AuraBackups;$cleared=$true}catch{}
 Assert-Review (-not $cleared -and [IO.File]::Exists($AuraStatePath)) 'partial restoration cannot delete recovery state'
 [TestRegistry]::FailWrite=$false

 Reset-Fixture
 function Initialize-AuraRuntime {}
 function Enable-ComputerRestore {param($Drive,$ErrorAction)}
 function Checkpoint-Computer {param($Description,$RestorePointType,$ErrorAction)}
 function Finish-AuraOperation {}
 Invoke-AuraApply @([pscustomobject]@{Id='fixture_absent';Title='fixture';RequiresWindows11=$false;Script={}})
 Assert-Review ($AuraActions[0].Status -eq 'Skipped' -and -not [IO.File]::Exists($AuraStatePath)) 'an operation with no available targets is skipped and leaves no false pending snapshot'

 Reset-Fixture
 $path='HKU:\'+$TargetSid+'\Software';$key=[TestRegistry]::Keys[$path]
 $key.SetValue('Existing',7,[Microsoft.Win32.RegistryValueKind]::DWord)
 function Write-AtomicClixml {param($Path,$Value)throw 'fixture disk failure'}
 $failed=$false
 try{Set-DwordBacked 'HKCU:\Software' 'Existing' 99}catch{$failed=$true}
 Assert-Review ($failed -and $key.Values['Existing'] -eq 7) 'failed snapshot persistence prevents registry mutation'
 Write-Output ('PowerShell checks completed: '+$script:Checks+'; Windows calls were replaced by fixtures.')
} finally {
 [TestRegistry]::BeforeWrite=$null
 Microsoft.PowerShell.Management\Remove-Item -LiteralPath $root -Recurse -Force
}
