param([string]$Action, [string]$Identifier, [string]$RecordName, [string]$Challenge)

$ErrorActionPreference = 'Stop'
if ($Identifier -ne 'lecrocodile.duckdns.org' -or $RecordName -ne '_acme-challenge.lecrocodile.duckdns.org')
{
    throw 'Unexpected DNS challenge identifier.'
}
if ($Action -ne 'create')
{
    throw 'Unsupported DNS challenge operation.'
}
$tokenPath = Join-Path (Split-Path $PSScriptRoot -Parent) '.duckdns-token'
$secret = (Get-Content -LiteralPath $tokenPath -Raw).Trim()
if ($secret -notmatch '^[0-9a-fA-F-]{36}$' -or $Challenge -notmatch '^[A-Za-z0-9_-]+$')
{
    throw 'Invalid DNS challenge or token.'
}
try
{
    $uri = 'https://www.duckdns.org/update?domains=lecrocodile&token=' + $secret + '&txt=' + $Challenge
    $result = Invoke-RestMethod -Uri $uri -TimeoutSec 30
    if ($result -ne 'OK') { throw 'Duck DNS rejected the DNS challenge.' }
    Write-Output 'Duck DNS challenge updated.'
}
catch
{
    throw 'Duck DNS challenge update failed.'
}
