<#
.SYNOPSIS
  Składa stronę GitHub Pages: generator webowy, podpisany katalog, podpisany manifest aktualizacji i AppInstaller.exe.

.EXAMPLE
  # lokalnie (klucz z pliku):
  .\tools\Build-Site.ps1 -Exe out\AppInstaller.exe -Version 1.0.0 -ReleaseUrl https://github.com/ja/AppInstaller/releases/download/v1.0.0/AppInstaller.exe -KeyFile keys\private.xml
  # w CI klucz pochodzi z sekretu SIGNING_KEY_XML (zmienna środowiskowa)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$ReleaseUrl,
    [string]$KeyFile,
    [string]$Out = 'site'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$keyXml = if ($KeyFile) { Get-Content $KeyFile -Raw } else { $env:SIGNING_KEY_XML }
if (-not $keyXml -or $keyXml -notmatch '<D>') { throw 'Brak klucza prywatnego (sekret SIGNING_KEY_XML lub -KeyFile).' }

function Write-Signed([string]$Path, [byte[]]$Bytes) {
    [IO.File]::WriteAllBytes($Path, $Bytes)
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
    $rsa.PersistKeyInCsp = $false
    $rsa.FromXmlString($keyXml)
    $sig = $rsa.SignData($Bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    [IO.File]::WriteAllText("$Path.sig", [Convert]::ToBase64String($sig))
    Write-Host "  podpisano $(Split-Path $Path -Leaf)"
}

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Path $Out | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)

# --- walidacja katalogu (żeby literówka nie trafiła do użytkowników) ---
$catalogBytes = [IO.File]::ReadAllBytes((Join-Path $root 'catalog\apps.json'))
$cat = $utf8.GetString($catalogBytes) | ConvertFrom-Json
$ids = @($cat.apps.id)
$dupes = $ids | Group-Object | Where-Object Count -gt 1
if ($dupes) { throw "Zduplikowane id: $($dupes.Name -join ', ')" }
foreach ($c in $cat.core) { if ($ids -notcontains $c) { throw "core: brak aplikacji '$c'" } }
foreach ($p in $cat.presets.PSObject.Properties) {
    foreach ($a in $p.Value.apps) { if ($ids -notcontains $a) { throw "preset '$($p.Name)': brak aplikacji '$a'" } }
}
foreach ($a in $cat.apps) {
    if (-not $a.sources) { throw "'$($a.id)': brak źródeł" }
    if (-not $a.desc.pl -or -not $a.desc.en) { throw "'$($a.id)': brak opisu PL/EN" }
    foreach ($s in $a.sources) {
        if ($s.type -notin 'github','url','dotnet','ps','winget','choco') { throw "'$($a.id)': nieznany typ źródła '$($s.type)'" }
        if ($s.url -and $s.url -notlike 'https://*') { throw "'$($a.id)': adres musi być HTTPS" }
    }
}
Write-Host "Katalog OK: $($ids.Count) aplikacji"

# --- katalog + podpis ---
Write-Signed (Join-Path $Out 'apps.json') $catalogBytes

# --- exe + manifest aktualizacji + podpis ---
Copy-Item $Exe (Join-Path $Out 'AppInstaller.exe')
$sha = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    version = $Version
    url     = $ReleaseUrl
    sha256  = $sha
    notes   = [ordered]@{ pl = ''; en = '' }
} | ConvertTo-Json -Compress
Write-Signed (Join-Path $Out 'update.json') $utf8.GetBytes($manifest)

# --- generator webowy (fragment HTML opakowany w pełny dokument) ---
$fragment = [IO.File]::ReadAllText((Join-Path $root 'web\index.html'), $utf8)
$page = @"
<!doctype html>
<html lang="pl">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Crect width='16' height='16' rx='3' fill='%230067c0'/%3E%3Cpath fill='white' d='M7.3 3h1.4v5.3l1.9-1.9.9.9L8 10.8 4.5 7.3l.9-.9 1.9 1.9zM4 12h8v1.2H4z'/%3E%3C/svg%3E">
</head>
<body>
$fragment
</body>
</html>
"@
[IO.File]::WriteAllText((Join-Path $Out 'index.html'), $page, $utf8)
Copy-Item (Join-Path $root 'catalog\icons') (Join-Path $Out 'icons') -Recurse -ErrorAction SilentlyContinue
New-Item -ItemType File -Path (Join-Path $Out '.nojekyll') | Out-Null
Write-Host "Strona gotowa w '$Out' (wersja $Version, sha256 $sha)" -ForegroundColor Green
