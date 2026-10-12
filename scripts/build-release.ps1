$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ReleasePath = Join-Path $ProjectRoot 'release-v0.10'

function Assert-Sha256([string]$RelativePath, [string]$Expected) {
    $Path = Join-Path $ProjectRoot $RelativePath
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Missing release resource: $RelativePath"
    }
    $Actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Actual -ne $Expected) {
        throw "SHA-256 mismatch for $RelativePath`nExpected: $Expected`nActual:   $Actual"
    }
    Write-Host "Verified $RelativePath" -ForegroundColor DarkGreen
}

Assert-Sha256 'Assets\Tools\PawnIO_setup.exe' '1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032'
Assert-Sha256 'Assets\PawnIO\RyzenSMU.bin' '301d9ca397108e09f31bfbd5ac4c9bb4f352a5de68532c32db3ba7ddcde93450'
Assert-Sha256 'Assets\PawnIO\IntelMSR.bin' 'd6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f'
Assert-Sha256 'Assets\Tools\PresentMon-2.5.1-x64.exe' '9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191'

Push-Location $ProjectRoot
try {
    dotnet restore .\Aura.csproj -r win-x64
    dotnet run --project .\tests\Aura.CatalogSmoke\Aura.CatalogSmoke.csproj -c Release

    if (Test-Path -LiteralPath $ReleasePath) {
        Remove-Item -LiteralPath $ReleasePath -Recurse -Force
    }

    dotnet publish .\Aura.csproj -c Release -r win-x64 --self-contained true --no-restore -o $ReleasePath

    $Executable = Join-Path $ReleasePath 'Aura.exe'
    if (-not (Test-Path -LiteralPath $Executable)) {
        throw 'Publish completed without Aura.exe.'
    }

    $Unexpected = @(Get-ChildItem -LiteralPath $ReleasePath -File | Where-Object Name -ne 'Aura.exe')
    if ($Unexpected.Count -gt 0) {
        throw ('Single-file publish produced unexpected files: ' + (($Unexpected | Select-Object -ExpandProperty Name) -join ', '))
    }

    $Hash = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Release ready: $Executable" -ForegroundColor Green
    Write-Host "SHA-256: $Hash"
}
finally {
    Pop-Location
}
