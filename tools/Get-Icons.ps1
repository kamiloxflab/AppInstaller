<#
.SYNOPSIS
  Pobiera ikony aplikacji do catalog/icons/<id>.png, żeby strona i program brały je z Twojej strony GitHub Pages.

.DESCRIPTION
  Dla każdej aplikacji próbuje po kolei adresów z pola "icons" w catalog/apps.json ("@domena" = usługa favicon).
  Ikona z catalog/icons ma pierwszeństwo przed adresami z katalogu, więc:
    - złą ikonę poprawisz, wrzucając własny plik PNG (64x64 lub 256x256) jako catalog/icons/<id>.png,
    - po uruchomieniu skryptu sprawdź pliki w Eksploratorze (widok: duże ikony) i podmień te, które nie pasują.
  Istniejące pliki nie są nadpisywane bez -Force.

.EXAMPLE
  .\tools\Get-Icons.ps1                 # brakujące ikony
  .\tools\Get-Icons.ps1 -Only chrome,firefox -Force
#>
[CmdletBinding()]
param(
    [string[]]$Only,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072

$root = Split-Path $PSScriptRoot -Parent
$dir  = Join-Path $root 'catalog\icons'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
$cat  = [IO.File]::ReadAllText((Join-Path $root 'catalog\apps.json'), $utf8) | ConvertFrom-Json

function Test-Image([byte[]]$b) {
    if ($b.Length -lt 100) { return $false }
    return ($b[0] -eq 0x89 -and $b[1] -eq 0x50) -or ($b[0] -eq 0 -and $b[1] -eq 0 -and $b[2] -eq 1) -or
           ($b[0] -eq 0xFF -and $b[1] -eq 0xD8) -or ($b[0] -eq 0x47 -and $b[1] -eq 0x49) -or ($b[0] -eq 0x52 -and $b[1] -eq 0x49)
}

$ok = 0; $miss = @()
foreach ($a in $cat.apps) {
    if ($Only -and ($Only -split ',') -notcontains $a.id) { continue }
    $target = Join-Path $dir "$($a.id).png"
    if ((Test-Path $target) -and -not $Force) { continue }

    $entries = @($a.icons) + @($a.icon) | Where-Object { $_ }
    $saved = $false
    foreach ($e in $entries) {
        $url = if ($e.StartsWith('@')) { $cat.iconService -replace '\{0\}', $e.Substring(1) } else { $e }
        try {
            $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 15 -Headers @{ 'User-Agent' = 'Mozilla/5.0 AppInstaller-icons' }
            $bytes = $r.Content
            if ($bytes -is [string]) { continue }
            if (Test-Image $bytes) {
                [IO.File]::WriteAllBytes($target, $bytes)
                Write-Host ("  {0,-22} <- {1}" -f $a.id, $url) -ForegroundColor Green
                $saved = $true; $ok++
                break
            }
        } catch { }
    }
    if (-not $saved) { $miss += $a.id }
}
Write-Host ""
Write-Host "Zapisano: $ok"
if ($miss) { Write-Host "Brak ikony (wrzuć ręcznie PNG do catalog\icons): $($miss -join ', ')" -ForegroundColor Yellow }
Write-Host "Następnie: git add catalog/icons && git commit -m Ikony && git push"
