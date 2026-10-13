param(
    [string]$OutputDirectory = '',
    [string]$SigningThumbprint = '',
    [switch]$RequireSigned,
    [switch]$Expanded
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProjectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $ProjectRoot 'release-v0.11' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a fresh output directory; existing releases are preserved.' }
if ($RequireSigned -and (-not $SigningThumbprint -or $env:OS -ne 'Windows_NT')) { throw 'A Windows certificate store and SigningThumbprint are required.' }
function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('dotnet failed (' + $LASTEXITCODE + '): ' + $Arguments[0]) }
}
function Assert-Sha256([string]$RelativePath,[string]$Expected) {
    $Actual=(Get-FileHash -LiteralPath (Join-Path $ProjectRoot $RelativePath) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Actual -ne $Expected) { throw ('Resource SHA-256 mismatch: '+$RelativePath) }
}
Assert-Sha256 'Assets/Tools/PawnIO_setup.exe' '1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032'
Assert-Sha256 'Assets/PawnIO/RyzenSMU.bin' '301d9ca397108e09f31bfbd5ac4c9bb4f352a5de68532c32db3ba7ddcde93450'
Assert-Sha256 'Assets/PawnIO/IntelMSR.bin' 'd6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f'
Assert-Sha256 'Assets/Tools/PresentMon-2.5.1-x64.exe' '9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191'
$Stage=$OutputDirectory+'.stage-'+[Guid]::NewGuid().ToString('N')
New-Item -Path $Stage -ItemType Directory | Out-Null
Push-Location $ProjectRoot
try {
    Invoke-DotNet -Arguments @('restore','Aura.sln','--locked-mode','-maxcpucount:2')
    Invoke-DotNet -Arguments @('run','--project','tests/Aura.CatalogSmoke/Aura.CatalogSmoke.csproj','-c','Release','--no-restore')
    $TestResults=Join-Path $Stage 'TestResults'
    foreach ($Test in @('Aura.RegressionTests','Aura.AuthTests')) {
        Invoke-DotNet -Arguments @('test',('tests/'+$Test+'/'+$Test+'.csproj'),'-c','Release','--no-restore','--logger',('trx;LogFileName='+$Test+'.trx'),'--results-directory',$TestResults)
    }
    $PowerOutput=@(& (Join-Path $ProjectRoot 'tests/Aura.PowerShellSmoke/Smoke.ps1'))
    $PowerOutput | ForEach-Object { Write-Output $_ }
    $PowerOutput | Set-Content -LiteralPath (Join-Path $Stage 'powershell-fixtures.txt') -Encoding UTF8
    $PowerCount=0
    foreach($Line in $PowerOutput){if([string]$Line -match 'PowerShell checks completed: (\d+)'){$PowerCount=[int]$Matches[1]}}
    if($PowerCount -eq 0){throw 'PowerShell fixtures did not complete.'}
    $SingleFile=if($Expanded){'false'}else{'true'}
    $AppOutput=Join-Path $Stage 'app'
    Invoke-DotNet -Arguments @('publish','Aura.csproj','-c','Release','-r','win-x64','--self-contained','true','--no-restore',('-p:PublishSingleFile='+$SingleFile),'-o',$AppOutput)
    $Executable=Join-Path $AppOutput 'Aura.exe'
    if (-not (Test-Path -LiteralPath $Executable)) { throw 'Publish did not produce Aura.exe.' }
    if (-not $Expanded -and @(Get-ChildItem -LiteralPath $AppOutput -File | Where-Object Name -ne 'Aura.exe').Count -gt 0) { throw 'Unexpected files in single-file publish.' }
    $Signature='Unsigned development build'
    if ($SigningThumbprint) {
        if($env:OS -ne 'Windows_NT') { throw 'Authenticode signing requires Windows.' }
        & signtool sign /sha1 $SigningThumbprint /fd SHA256 /tr https://timestamp.digicert.com /td SHA256 $Executable
        if($LASTEXITCODE -ne 0){throw 'Authenticode signing failed.'}
        & signtool verify /pa $Executable
        if($LASTEXITCODE -ne 0){throw 'Authenticode verification failed.'}
        if((Get-AuthenticodeSignature -LiteralPath $Executable).Status -ne 'Valid'){throw 'Windows did not verify the signature.'}
        $Signature='Authenticode verified'
    }
    $Counts=@()
    foreach($Path in Get-ChildItem -LiteralPath $TestResults -Filter '*.trx') {
        [xml]$Trx=Get-Content -LiteralPath $Path.FullName
        $Counter=$Trx.TestRun.ResultSummary.Counters
        if([int]$Counter.total -eq 0 -or [int]$Counter.failed -gt 0 -or [int]$Counter.executed -ne [int]$Counter.total){throw 'Tests incomplete or failed.'}
        $Counts+=@{Suite=$Path.BaseName;Total=[int]$Counter.total;Passed=[int]$Counter.passed;Skipped=[int]$Counter.notExecuted}
    }
    $Auth=Get-Content -LiteralPath 'Assets/Runtime/AuthClient.json' -Raw | ConvertFrom-Json
    $Report=@{Version='0.11.0';CreatedAtUtc=[DateTime]::UtcNow.ToString('o');Sdk=(& dotnet --version);Sha256=(Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash.ToLowerInvariant();Bytes=(Get-Item -LiteralPath $Executable).Length;Signature=$Signature;AuthenticationMode=$Auth.Mode;Tests=$Counts;Catalog='82 definitions verified';PowerShell=($PowerCount.ToString()+' fixture checks; no native writes');NativeWindowsQA='Not performed by this script'}
    if($LASTEXITCODE -ne 0){throw 'SDK version check failed.'}
    $Report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Stage 'release-report.json') -Encoding UTF8
    Copy-Item 'THIRD-PARTY-NOTICES.md','SOURCE-README.md','RELEASE-CHECKLIST.md' -Destination $Stage
    Copy-Item 'ThirdParty' -Destination $Stage -Recurse
    Copy-Item 'packages.lock.json' -Destination $Stage
    Move-Item -LiteralPath $Stage -Destination $OutputDirectory
    Write-Host ('Release: '+$OutputDirectory+'; '+$Signature)
}
finally { Pop-Location }
