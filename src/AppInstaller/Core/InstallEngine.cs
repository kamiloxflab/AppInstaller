using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AppInstaller.Core
{
    public enum ItemState { Queued, Resolving, Downloading, Waiting, Installing, Done, Failed, Skipped }

    public class InstallItem : INotifyPropertyChanged
    {
        public AppDef App { get; private set; }
        public bool IsCore { get; private set; }

        public InstallItem(AppDef app, bool isCore)
        {
            App = app;
            IsCore = isCore;
            _status = Loc.T("St_Queued");
        }

        ItemState _state;
        int _progress;
        string _status, _source;

        public ItemState State { get { return _state; } set { _state = value; On("State"); On("IsActive"); On("IsFinished"); } }
        /// <summary>0–100, -1 = nieokreślony.</summary>
        public int Progress { get { return _progress; } set { _progress = value; On("Progress"); On("IsIndeterminate"); } }
        public string Status { get { return _status; } set { _status = value; On("Status"); } }
        public string SourceUsed { get { return _source; } set { _source = value; On("SourceUsed"); } }
        public bool RebootRequired { get; set; }

        public bool IsIndeterminate { get { return _progress < 0; } }
        public bool IsActive { get { return _state == ItemState.Resolving || _state == ItemState.Downloading || _state == ItemState.Installing; } }
        public bool IsFinished { get { return _state == ItemState.Done || _state == ItemState.Failed || _state == ItemState.Skipped; } }

        internal Task<Prepared> PrepareTask;

        public event PropertyChangedEventHandler PropertyChanged;
        void On(string n) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(n)); }
    }

    internal class Prepared
    {
        public SourceDef Source;
        public string File;
        public string Version;
        /// <summary>Dodatkowe pobrane pliki: klucz z "files" (+ "license" dla MSIX) → ścieżka.</summary>
        public Dictionary<string, string> Extras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public class EngineOptions
    {
        /// <summary>Wymuszona kolejność grup źródeł, np. ["winget","direct","choco"]. null = kolejność z katalogu.</summary>
        public string[] SourceOrder;
        public int MaxParallelDownloads = Config.MaxParallelDownloads;
    }

    public class EngineResult
    {
        public int Total, Succeeded, Failed;
        public bool RebootRequired;
        public int ExitCode { get { return Failed > 0 ? 1 : RebootRequired ? 3010 : 0; } }
    }

    /// <summary>
    /// Pobieranie równolegle, instalacja sekwencyjnie (instalatory MSI/Windows Installer nie mogą działać równocześnie).
    /// Dla każdej aplikacji źródła są próbowane po kolei — przy błędzie następuje fallback do kolejnego.
    /// </summary>
    public class InstallEngine
    {
        static readonly int[] DefaultOk = { 0, 1641, 3010 };
        // winget: brak nowszej wersji / już zainstalowane
        static readonly int[] WingetOk = { 0, unchecked((int)0x8A15002B), unchecked((int)0x8A150061), 3010, 1641 };
        static readonly int[] ChocoOk = { 0, 1641, 3010 };

        readonly EngineOptions _opt;
        readonly string _downloadDir;

        public InstallEngine(EngineOptions opt)
        {
            _opt = opt ?? new EngineOptions();
            _downloadDir = Config.Ensure(Path.Combine(Config.DataDir, "downloads"));
        }

        IEnumerable<SourceDef> Ordered(AppDef app)
        {
            if (_opt.SourceOrder == null || _opt.SourceOrder.Length == 0) return app.Sources;
            var order = _opt.SourceOrder.Select(s => s.ToLowerInvariant()).ToList();
            return app.Sources
                .Where(s => order.Contains(s.Group))
                .OrderBy(s => order.IndexOf(s.Group))
                .ThenBy(s => app.Sources.IndexOf(s));
        }

        public async Task<EngineResult> RunAsync(IList<InstallItem> items, CancellationToken ct)
        {
            var result = new EngineResult { Total = items.Count };
            var gate = new SemaphoreSlim(Math.Max(1, _opt.MaxParallelDownloads));

            // 1) start pobierania dla wszystkich (ograniczone semaforem)
            foreach (var it in items)
            {
                var item = it;
                item.PrepareTask = Task.Run(() => PrepareAsync(item, gate, ct), ct);
            }

            // 2) instalacja po kolei, w kolejności listy (najpierw pakiety bazowe)
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                bool ok = false;
                try { ok = await InstallOneAsync(item, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Error(item.App.Id + ": " + ex.Message);
                }
                if (ok) result.Succeeded++;
                else
                {
                    result.Failed++;
                    if (item.State != ItemState.Failed)
                    {
                        item.State = ItemState.Failed;
                        item.Status = Loc.T("St_Failed", Loc.T("AllSourcesFailed"));
                    }
                }
                if (item.RebootRequired) result.RebootRequired = true;
            }
            CleanupDownloads();
            Log.Info(string.Format("Finished: {0}/{1} ok, {2} failed, reboot={3}", result.Succeeded, result.Total, result.Failed, result.RebootRequired));
            return result;
        }

        async Task<Prepared> PrepareAsync(InstallItem item, SemaphoreSlim gate, CancellationToken ct)
        {
            var direct = Ordered(item.App).Where(s => s.IsDirect).ToList();
            if (direct.Count == 0) return null;

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var s in direct)
                {
                    try
                    {
                        item.State = ItemState.Resolving;
                        item.Progress = -1;
                        item.Status = Loc.T("St_Resolving");
                        var rd = await Resolvers.ResolveAsync(item.App, s, ct).ConfigureAwait(false);
                        if (!rd.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            throw new Exception("Refusing non-HTTPS download: " + rd.Url);
                        Log.Info(item.App.Id + ": " + s.Type + " → " + rd.Url + (rd.Version != null ? " (" + rd.Version + ")" : ""));

                        var dir = Config.Ensure(Path.Combine(_downloadDir, item.App.Id));
                        var file = Path.Combine(dir, rd.FileName);
                        item.State = ItemState.Downloading;
                        item.Status = Loc.T("St_Downloading", 0);
                        await Net.DownloadAsync(rd.Url, file, p =>
                        {
                            item.Progress = p;
                            item.Status = p >= 0 ? Loc.T("St_Downloading", p) : Loc.T("St_DownloadingNoSize");
                        }, ct).ConfigureAwait(false);

                        Verify(item.App, s, rd, file);
                        var prep = new Prepared { Source = s, File = file, Version = rd.Version };
                        await DownloadExtrasAsync(item.App, s, rd, dir, prep, ct).ConfigureAwait(false);
                        item.State = ItemState.Waiting;
                        item.Progress = 100;
                        item.Status = Loc.T("St_Waiting");
                        return prep;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log.Warn(item.App.Id + ": direct source '" + s.Type + "' failed — " + ex.Message);
                    }
                }
                item.State = ItemState.Waiting;
                item.Status = Loc.T("St_Waiting");
                return null;
            }
            finally { gate.Release(); }
        }

        /// <summary>Licencja MSIX i pliki z "files" (konfiguracje .inf/.reg) — wyłącznie HTTPS.</summary>
        static async Task DownloadExtrasAsync(AppDef app, SourceDef s, ResolvedDownload rd, string dir, Prepared prep, CancellationToken ct)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (!string.IsNullOrEmpty(rd.LicenseUrl)) list.Add(new KeyValuePair<string, string>("license", rd.LicenseUrl));
            if (s.Files != null) list.AddRange(s.Files);
            foreach (var kv in list)
            {
                if (!Regex.IsMatch(kv.Key, "^[A-Za-z0-9_]+$")) throw new Exception("Invalid file key: " + kv.Key);
                if (!kv.Value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new Exception("Extra file must be HTTPS: " + kv.Value);
                var ext = Path.GetExtension(new Uri(kv.Value).AbsolutePath);
                if (string.IsNullOrEmpty(ext) || ext.Length > 6) ext = ".dat";
                var path = Path.Combine(dir, "extra-" + kv.Key + ext);
                await Net.DownloadAsync(kv.Value, path, null, ct).ConfigureAwait(false);
                prep.Extras[kv.Key] = path;
                Log.Info(app.Id + ": extra '" + kv.Key + "' ← " + kv.Value);
            }
        }

        static string Expand(string args, Prepared prep)
        {
            if (string.IsNullOrEmpty(args)) return args ?? "";
            foreach (var kv in prep.Extras) args = args.Replace("{" + kv.Key + "}", kv.Value);
            return args;
        }

        static void Verify(AppDef app, SourceDef s, ResolvedDownload rd, string file)
        {
            if (!string.IsNullOrEmpty(rd.Hash))
            {
                if (!Crypto.CheckHash(file, rd.Hash)) { File.Delete(file); throw new Exception("Hash mismatch (" + rd.Hash.Split(':')[0] + ")"); }
                Log.Info(app.Id + ": hash OK");
            }
            string subject;
            var sig = Crypto.VerifyAuthenticode(file, out subject);
            Log.Info(app.Id + ": Authenticode " + sig + (subject != null ? " — " + subject : ""));
            if (!string.IsNullOrEmpty(s.Signer))
            {
                if (sig != Crypto.SignatureState.Valid || subject == null || subject.IndexOf(s.Signer, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    File.Delete(file);
                    throw new Exception("Signature check failed (expected signer '" + s.Signer + "')");
                }
            }
            else if (sig == Crypto.SignatureState.Invalid)
            {
                // podpis jest, ale uszkodzony/niezaufany — to nigdy nie jest w porządku
                File.Delete(file);
                throw new Exception("Invalid Authenticode signature");
            }
        }

        async Task<bool> InstallOneAsync(InstallItem item, CancellationToken ct)
        {
            Prepared prepared = null;
            try { prepared = await item.PrepareTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn(item.App.Id + ": prepare crashed — " + ex.Message); }

            string previous = null;
            foreach (var s in Ordered(item.App))
            {
                ct.ThrowIfCancellationRequested();
                if (s.IsDirect && (prepared == null || prepared.Source != s)) continue;

                var label = SourceLabel(s);
                if (previous != null)
                    Log.Warn(item.App.Id + ": " + previous + " failed → fallback to " + label);
                item.SourceUsed = label;
                item.State = ItemState.Installing;
                item.Progress = -1;
                item.Status = previous == null ? Loc.T("St_Installing", label) : Loc.T("St_Fallback", previous, label);

                int code;
                try
                {
                    if (s.IsDirect) code = await RunInstallerAsync(item.App, prepared, s, ct).ConfigureAwait(false);
                    else if (s.Type == "winget") code = await RunWingetAsync(item, s, ct).ConfigureAwait(false);
                    else if (s.Type == "choco") code = await RunChocoAsync(item, s, ct).ConfigureAwait(false);
                    else { Log.Warn("Unknown source type " + s.Type); continue; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warn(item.App.Id + ": " + label + " — " + ex.Message);
                    previous = label;
                    continue;
                }

                var ok = (s.Type == "winget" ? WingetOk : s.Type == "choco" ? ChocoOk : DefaultOk)
                    .Concat(s.OkCodes ?? new List<int>()).Contains(code);
                Log.Info(item.App.Id + ": " + label + " exit " + code + (ok ? " OK" : " FAIL"));
                if (ok)
                {
                    if (s.IsDirect) await PostInstallAsync(item.App, s, prepared, ct).ConfigureAwait(false);
                    if (code == 3010 || code == 1641) item.RebootRequired = true;
                    item.State = ItemState.Done;
                    item.Progress = 100;
                    item.Status = Loc.T("St_Done", label) + (item.RebootRequired ? " · " + Loc.T("RebootShort") : "");
                    return true;
                }
                previous = label + " (" + code + ")";
            }
            item.State = ItemState.Failed;
            item.Status = Loc.T("St_Failed", previous ?? Loc.T("NoSource"));
            return false;
        }

        static string SourceLabel(SourceDef s)
        {
            return s.IsDirect ? Loc.T("Src_Direct") : s.Type == "winget" ? "winget" : "Chocolatey";
        }

        /// <summary>Import ustawień (.reg) i restart programu — błąd tu nie unieważnia samej instalacji.</summary>
        static async Task PostInstallAsync(AppDef app, SourceDef s, Prepared prep, CancellationToken ct)
        {
            try
            {
                string reg;
                if (!string.IsNullOrEmpty(s.PostReg) && prep.Extras.TryGetValue(s.PostReg, out reg))
                {
                    var r = await Proc.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe"),
                        "/S " + Proc.Quote(reg), ct, TimeSpan.FromMinutes(1), capture: false).ConfigureAwait(false);
                    Log.Info(app.Id + ": settings imported (" + Path.GetFileName(reg) + ", exit " + r.ExitCode + ")");
                }
                if (!string.IsNullOrEmpty(s.PostRestart))
                {
                    var exe = Environment.ExpandEnvironmentVariables(s.PostRestart);
                    if (File.Exists(exe))
                    {
                        await Proc.RunAsync(exe, "-exit", ct, TimeSpan.FromSeconds(30), capture: false).ConfigureAwait(false);
                        await Task.Delay(3000, ct).ConfigureAwait(false);
                        // explorer.exe uruchamia program bez uprawnień administratora, w sesji użytkownika
                        System.Diagnostics.Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), Proc.Quote(exe));
                        Log.Info(app.Id + ": restarted " + exe);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn(app.Id + ": post-install step failed — " + ex.Message); }
        }

        static async Task<int> RunInstallerAsync(AppDef app, Prepared prep, SourceDef s, CancellationToken ct)
        {
            var file = prep.File;
            if (s.Installer == "portable")
                return await Task.Run(() => Portable.Install(app, s, file, prep.Version), ct).ConfigureAwait(false);
            if (s.Installer == "msix")
                return await InstallMsixAsync(app, prep, ct).ConfigureAwait(false);

            bool msi = s.Installer == "msi" || (s.Installer == null && file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
            ProcResult r;
            if (s.Installer == "zip")
            {
                // np. CapFrameX: *_installer.zip → CapFrameXBootstrapper*.exe /S
                if (string.IsNullOrEmpty(s.Run)) throw new Exception("zip installer needs 'run' (file name regex)");
                var dir = Path.Combine(Path.GetDirectoryName(file), "unzipped");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                Portable.Extract(file, dir);
                var rx = new Regex(s.Run, RegexOptions.IgnoreCase);
                var setup = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                    .Where(f => rx.IsMatch(Path.GetFileName(f))).OrderBy(f => f.Length).FirstOrDefault();
                if (setup == null) throw new Exception("No file matching /" + s.Run + "/ in the archive");
                Log.Info(app.Id + ": running " + setup.Substring(dir.Length + 1));
                if (setup.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                    r = await Proc.RunAsync(Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                        "/i " + Proc.Quote(setup) + " /qn /norestart " + Expand(s.Args, prep), ct, capture: false).ConfigureAwait(false);
                else
                {
                    string sigSubject;
                    if (Crypto.VerifyAuthenticode(setup, out sigSubject) == Crypto.SignatureState.Invalid)
                        throw new Exception("Invalid Authenticode signature: " + Path.GetFileName(setup));
                    r = await Proc.RunAsync(setup, Expand(s.Args, prep), ct, capture: false).ConfigureAwait(false);
                }
            }
            else if (s.Installer == "extract")
            {
                // np. DirectX: directx_Jun2010_redist.exe /Q /C /T:"<dir>"  →  <dir>\DXSETUP.exe /silent
                if (string.IsNullOrEmpty(s.Run) || s.Run.Contains("..") || Path.IsPathRooted(s.Run))
                    throw new Exception("Invalid 'run' for extract installer");
                var dir = Path.Combine(Path.GetDirectoryName(file), "extracted");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                var x = await Proc.RunAsync(file, (s.ExtractArgs ?? "").Replace("{dir}", dir), ct, TimeSpan.FromMinutes(10), capture: false).ConfigureAwait(false);
                var setup = Path.Combine(dir, s.Run);
                if (!File.Exists(setup))
                    throw new Exception("Extraction failed (exit " + x.ExitCode + "): " + s.Run + " not found");
                r = await Proc.RunAsync(setup, Expand(s.Args, prep), ct, capture: false).ConfigureAwait(false);
            }
            else if (msi)
            {
                var log = Path.Combine(Path.GetDirectoryName(file), "msi.log");
                r = await Proc.RunAsync(Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                    "/i " + Proc.Quote(file) + " /qn /norestart /l*v " + Proc.Quote(log) + " " + Expand(s.Args, prep), ct, capture: false).ConfigureAwait(false);
            }
            else
            {
                var args = Expand(s.Args, prep);
                if (args.Trim().StartsWith("auto", StringComparison.OrdinalIgnoreCase))
                {
                    // "auto" lub "auto <dodatkowe argumenty>" — rozpoznaj instalator, nie zgaduj w ciemno
                    var det = InstallerDetect.Detect(file);
                    if (det == null)
                        throw new Exception("Unknown installer type — no safe silent switch (set 'args' in the catalog)");
                    args = det.Args + args.Trim().Substring(4);
                    Log.Info(Path.GetFileName(file) + ": detected " + det.Kind + " → " + args);
                }
                if (s.AsUser)
                {
                    // instalatory per-user (Spotify, Discord…) odmawiają pracy jako administrator
                    Log.Info(Path.GetFileName(file) + ": running as signed-in user");
                    return await Proc.RunAsUserAsync(file, args, ct).ConfigureAwait(false);
                }
                r = await Proc.RunAsync(file, args, ct, capture: false).ConfigureAwait(false);
            }
            return r.ExitCode;
        }

        /// <summary>
        /// Pakiet MSIX/MSIXBundle (NanaZip): provisioning dla wszystkich użytkowników + rejestracja dla bieżącego.
        /// </summary>
        static async Task<int> InstallMsixAsync(AppDef app, Prepared prep, CancellationToken ct)
        {
            string lic;
            prep.Extras.TryGetValue("license", out lic);
            Func<string, string> q = x => "'" + x.Replace("'", "''") + "'";
            var script =
                "$pkg = " + q(prep.File) + "\n" +
                (lic != null
                    ? "Add-AppxProvisionedPackage -Online -PackagePath $pkg -LicensePath " + q(lic) + " | Out-Null\n"
                    : "Add-AppxProvisionedPackage -Online -PackagePath $pkg -SkipLicense | Out-Null\n") +
                "try { Add-AppxPackage -Path $pkg -ErrorAction Stop } catch { Write-Output ('Add-AppxPackage: ' + $_) }\n" +
                "Write-Output 'MSIX-OK'";
            var r = await Proc.PowerShellAsync(script, ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            Log.Info(app.Id + ": msix → " + (r.Output ?? "").Trim());
            return (r.Output ?? "").Contains("MSIX-OK") ? 0 : (r.ExitCode != 0 ? r.ExitCode : 1);
        }

        async Task<int> RunWingetAsync(InstallItem item, SourceDef s, CancellationToken ct)
        {
            var winget = await PackageManagers.EnsureWingetAsync(ct).ConfigureAwait(false);
            if (winget == null) throw new Exception("winget unavailable");
            item.Status = Loc.T("St_Installing", "winget");
            var args = "install --id " + Proc.Quote(s.Id) + " --exact --source winget --silent --disable-interactivity" +
                       " --accept-package-agreements --accept-source-agreements" +
                       (s.Arch != null ? " --architecture " + s.Arch : "") +
                       (s.AsUser ? " --scope user" : "") +
                       (s.Extra != null ? " " + s.Extra : "");
            if (s.AsUser)
            {
                // instalator odmawia pracy jako administrator — uruchom jako zalogowany użytkownik
                item.Status = Loc.T("St_InstallingUser");
                Log.Info(item.App.Id + ": winget as user " + args);
                return await Proc.RunAsUserAsync(winget, args, ct).ConfigureAwait(false);
            }
            var r = await Proc.RunAsync(winget, args, ct, onLine: l => { if (Printable(l)) Log.Info("  winget│ " + l.Trim()); }).ConfigureAwait(false);
            return r.ExitCode;
        }

        async Task<int> RunChocoAsync(InstallItem item, SourceDef s, CancellationToken ct)
        {
            var choco = await PackageManagers.EnsureChocoAsync(ct).ConfigureAwait(false);
            if (choco == null) throw new Exception("Chocolatey unavailable");
            item.Status = Loc.T("St_Installing", "Chocolatey");
            var args = "install " + s.Id + " -y --no-progress --limit-output" +
                       (s.Arch == "x86" ? " --x86" : "") +
                       (s.Extra != null ? " " + s.Extra : "");
            var r = await Proc.RunAsync(choco, args, ct, onLine: l => { if (Printable(l)) Log.Info("  choco │ " + l.Trim()); }).ConfigureAwait(false);
            return r.ExitCode;
        }

        static bool Printable(string l)
        {
            if (string.IsNullOrWhiteSpace(l)) return false;
            var t = l.Trim();
            // pomiń paski postępu winget (█ ▒) i spinnery
            return t.Length > 1 && t.IndexOf('█') < 0 && t.IndexOf('▒') < 0 && t != "-" && t != "\\" && t != "|" && t != "/";
        }

        void CleanupDownloads()
        {
            try { Directory.Delete(_downloadDir, true); } catch { }
        }
    }
}
