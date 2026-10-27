<#
.SYNOPSIS
  Offline odpowiednik generatora webowego: tworzy exe z "doklejonym" wyborem aplikacji (jak Ninite).

.EXAMPLE
  .\tools\New-Bundle.ps1 -Exe out\AppInstaller.exe -Apps firefox,7zip,vlc -Out Biuro.exe
  # Alternatywa bez modyfikacji pliku (zachowuje podpis cyfrowy): zmień nazwę na
  #   AppInstaller[firefox+7zip+vlc].exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string[]]$Apps,
    [string]$Out = 'AppInstaller-bundle.exe',
    [ValidateSet('pl','en')] [string]$Lang = 'pl'
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$json = $utf8.GetBytes((@{ v = 1; apps = @($Apps | ForEach-Object { $_ -split ',' } | Where-Object { $_ }); lang = $Lang } | ConvertTo-Json -Compress))
$base = [IO.File]::ReadAllBytes((Resolve-Path $Exe))
$fs = [IO.File]::Create($Out)
try {
    $fs.Write($base, 0, $base.Length)
    $fs.Write($json, 0, $json.Length)
    $fs.Write([BitConverter]::GetBytes([int]$json.Length), 0, 4)
    $magic = [Text.Encoding]::ASCII.GetBytes('AIPAYLD1')
    $fs.Write($magic, 0, 8)
} finally { $fs.Dispose() }
Write-Host "Utworzono $Out ($($Apps -join ', '))" -ForegroundColor Green
