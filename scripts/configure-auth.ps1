param(
    [ValidateSet('Prototype','LocalDevelopment','Server')][string]$Mode = 'Prototype',
    [string]$BaseUrl = '',
    [string]$PublicKeyFile = ''
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$Key=''
if($Mode -ne 'Prototype') {
    $Uri=[Uri]$BaseUrl
    if(-not $Uri.IsAbsoluteUri -or $Uri.UserInfo -or $Uri.Query -or $Uri.Fragment){throw 'Invalid server URL.'}
    if($Uri.Scheme -ne 'https' -and -not($Mode -eq 'LocalDevelopment' -and $Uri.Scheme -eq 'http' -and $Uri.IsLoopback)){throw 'HTTPS required; development HTTP is restricted to loopback.'}
    $Key=(Get-Content -LiteralPath $PublicKeyFile -Raw).Trim()
    $Bytes=[Convert]::FromBase64String($Key)
    if($Bytes.Length -lt 64 -or $Bytes.Length -gt 512){throw 'Invalid SubjectPublicKeyInfo key.'}
    if(-not $BaseUrl.EndsWith('/')){$BaseUrl+='/' }
}
$Path=Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets/Runtime/AuthClient.json'
@{Mode=$Mode;BaseUrl=$BaseUrl;PublicKey=$Key} | ConvertTo-Json | Set-Content -LiteralPath $Path -Encoding UTF8
Write-Output ('Embedded authentication mode: '+$Mode+'. Rebuild Aura.csproj to apply.')
