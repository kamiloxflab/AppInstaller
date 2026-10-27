<#
.SYNOPSIS
  Tworzy parę kluczy RSA-3072 do podpisywania katalogu (apps.json) i manifestu aktualizacji (update.json).

.DESCRIPTION
  keys\public.xml  -> commitujesz do repozytorium; jest wbudowywany w AppInstaller.exe
  keys\private.xml -> NIGDY nie commituj (.gitignore). Wklej jego zawartość jako sekret
                      repozytorium GitHub o nazwie SIGNING_KEY_XML, a plik przechowuj offline.

  Uruchom raz:  powershell -ExecutionPolicy Bypass -File tools\New-SigningKey.ps1
#>
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dir  = Join-Path $root 'keys'
$pub  = Join-Path $dir 'public.xml'
$priv = Join-Path $dir 'private.xml'

if ((Test-Path $pub) -and -not $Force) {
    throw "Klucz już istnieje ($pub). Zmiana klucza unieważni aktualizacje dla starszych wersji exe! Użyj -Force tylko świadomie."
}
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider 3072
$rsa.PersistKeyInCsp = $false
[IO.File]::WriteAllText($pub,  $rsa.ToXmlString($false))
[IO.File]::WriteAllText($priv, $rsa.ToXmlString($true))

Write-Host "Utworzono:" -ForegroundColor Green
Write-Host "  $pub   (commit)"
Write-Host "  $priv  (SEKRET — nie commituj)"
Write-Host ""
Write-Host "Dodaj sekret w GitHub: Settings > Secrets and variables > Actions > New repository secret"
Write-Host "  Nazwa: SIGNING_KEY_XML   Wartość: zawartość private.xml"
if (Get-Command gh -ErrorAction SilentlyContinue) {
    Write-Host "albo:  gh secret set SIGNING_KEY_XML < keys\private.xml"
}
