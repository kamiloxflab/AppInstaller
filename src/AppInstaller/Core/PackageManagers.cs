using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AppInstaller.Core
{
    /// <summary>
    /// Wykrywanie i (w razie potrzeby) instalacja winget oraz Chocolatey — dopiero wtedy,
    /// gdy któraś aplikacja faktycznie musi z nich skorzystać.
    /// </summary>
    public static class PackageManagers
    {
        static readonly SemaphoreSlim WingetLock = new SemaphoreSlim(1, 1);
        static readonly SemaphoreSlim ChocoLock = new SemaphoreSlim(1, 1);
        static string _winget, _choco;
        static bool _wingetTried, _chocoTried;

        public static event Action<string> Status;

        // ================= winget =================

        public static async Task<string> EnsureWingetAsync(CancellationToken ct)
        {
            await WingetLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_winget != null) return _winget;
                _winget = await FindWingetAsync(ct).ConfigureAwait(false);
                if (_winget != null || _wingetTried) return _winget;
                _wingetTried = true;

                Log.Warn("winget not found — installing");
                Raise("EnsureWinget");
                var r = await Proc.PowerShellAsync(InstallWingetScript, ct, TimeSpan.FromMinutes(15)).ConfigureAwait(false);
                Log.Info("winget installer exit " + r.ExitCode + ": " + Short(r.Output));
                _winget = await FindWingetAsync(ct).ConfigureAwait(false);
                if (_winget == null) Log.Error("winget still unavailable");
                else
                {
                    // pierwsze uruchomienie: zaakceptuj źródła, odśwież indeks
                    await Proc.RunAsync(_winget, "source update --disable-interactivity", ct, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                }
                return _winget;
            }
            finally { WingetLock.Release(); }
        }

        static async Task<string> FindWingetAsync(CancellationToken ct)
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe"),
                "winget.exe"
            };
            foreach (var c in candidates)
            {
                if (await WorksAsync(c, "--version", ct).ConfigureAwait(false)) return c;
            }
            // Instalacja "provisioned" (np. świeży system/LTSC) — exe w Program Files\WindowsApps
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
                var arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
                if (string.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "ARM64", StringComparison.OrdinalIgnoreCase)) arch = "arm64";
                var dirs = Directory.GetDirectories(root, "Microsoft.DesktopAppInstaller_*_" + arch + "__8wekyb3d8bbwe");
                Array.Sort(dirs);
                for (int i = dirs.Length - 1; i >= 0; i--)
                {
                    var exe = Path.Combine(dirs[i], "winget.exe");
                    if (File.Exists(exe) && await WorksAsync(exe, "--version", ct).ConfigureAwait(false)) return exe;
                }
            }
            catch { /* brak uprawnień do listowania WindowsApps */ }
            return null;
        }

        const string InstallWingetScript = @"
$arch = switch ($env:PROCESSOR_ARCHITECTURE) { 'ARM64' {'arm64'} 'x86' {'x86'} default {'x64'} }
try {
    if (-not (Get-PackageProvider -ListAvailable -Name NuGet -ErrorAction SilentlyContinue)) {
        Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope AllUsers | Out-Null
    }
    Install-Module -Name Microsoft.WinGet.Client -Repository PSGallery -Force -Scope AllUsers -AllowClobber | Out-Null
    Import-Module Microsoft.WinGet.Client -Force
    Repair-WinGetPackageManager -AllUsers -Latest -Force
    Write-Output 'OK: Repair-WinGetPackageManager'
    exit 0
} catch { Write-Output ('Module path failed: ' + $_) }

# Fallback: paczka msixbundle + zależności z oficjalnego wydania microsoft/winget-cli
$tmp = Join-Path $env:TEMP ('winget-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tmp | Out-Null
$rel = Invoke-RestMethod 'https://api.github.com/repos/microsoft/winget-cli/releases/latest' -Headers @{ 'User-Agent' = 'AppInstaller' }
$bundle = $rel.assets | Where-Object { $_.name -like '*.msixbundle' } | Select-Object -First 1
$deps   = $rel.assets | Where-Object { $_.name -like '*Dependencies.zip' } | Select-Object -First 1
$lic    = $rel.assets | Where-Object { $_.name -like '*License1.xml' } | Select-Object -First 1
Invoke-WebRequest $bundle.browser_download_url -OutFile ""$tmp\winget.msixbundle"" -UseBasicParsing
Invoke-WebRequest $deps.browser_download_url -OutFile ""$tmp\deps.zip"" -UseBasicParsing
Expand-Archive ""$tmp\deps.zip"" ""$tmp\deps"" -Force
$depFiles = @(Get-ChildItem ""$tmp\deps"" -Recurse -Include *.appx,*.msix | Where-Object { $_.FullName -match ""\\$arch\\"" } | ForEach-Object FullName)
if ($lic) {
    Invoke-WebRequest $lic.browser_download_url -OutFile ""$tmp\license.xml"" -UseBasicParsing
    Add-AppxProvisionedPackage -Online -PackagePath ""$tmp\winget.msixbundle"" -DependencyPackagePath $depFiles -LicensePath ""$tmp\license.xml"" | Out-Null
} else {
    Add-AppxProvisionedPackage -Online -PackagePath ""$tmp\winget.msixbundle"" -DependencyPackagePath $depFiles -SkipLicense | Out-Null
}
try { Add-AppxPackage -Path ""$tmp\winget.msixbundle"" -DependencyPath $depFiles -ErrorAction Stop } catch { Write-Output ('Add-AppxPackage: ' + $_) }
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Output 'OK: msixbundle'
";

        // ================= Chocolatey =================

        public static async Task<string> EnsureChocoAsync(CancellationToken ct)
        {
            await ChocoLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_choco != null) return _choco;
                _choco = FindChoco();
                if (_choco != null || _chocoTried) return _choco;
                _chocoTried = true;

                Log.Warn("Chocolatey not found — installing");
                Raise("EnsureChoco");
                // Oficjalna metoda instalacji z community.chocolatey.org
                var r = await Proc.PowerShellAsync(
                    "Set-ExecutionPolicy Bypass -Scope Process -Force; " +
                    "iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))",
                    ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
                Log.Info("choco installer exit " + r.ExitCode + ": " + Short(r.Output));
                _choco = FindChoco();
                if (_choco == null) Log.Error("Chocolatey still unavailable");
                return _choco;
            }
            finally { ChocoLock.Release(); }
        }

        static string FindChoco()
        {
            var env = Environment.GetEnvironmentVariable("ChocolateyInstall");
            var paths = new[]
            {
                env != null ? Path.Combine(env, @"bin\choco.exe") : null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"chocolatey\bin\choco.exe")
            };
            foreach (var p in paths) if (p != null && File.Exists(p)) return p;
            return null;
        }

        // ================= pomocnicze =================

        static async Task<bool> WorksAsync(string exe, string args, CancellationToken ct)
        {
            try
            {
                var r = await Proc.RunAsync(exe, args, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                return r.ExitCode == 0;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        static void Raise(string key)
        {
            var h = Status;
            if (h != null) h(key);
        }

        static string Short(string s)
        {
            s = (s ?? "").Trim();
            return s.Length > 500 ? s.Substring(s.Length - 500) : s;
        }
    }
}
