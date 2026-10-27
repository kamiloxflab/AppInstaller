using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
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
                        item.State = ItemState.Waiting;
                        item.Progress = 100;
                        item.Status = Loc.T("St_Waiting");
                        return new Prepared { Source = s, File = file };
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
                    if (s.IsDirect) code = await RunInstallerAsync(prepared.File, s, ct).ConfigureAwait(false);
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

        static async Task<int> RunInstallerAsync(string file, SourceDef s, CancellationToken ct)
        {
            bool msi = s.Installer == "msi" || (s.Installer == null && file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
            ProcResult r;
            if (s.Installer == "extract")
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
                r = await Proc.RunAsync(setup, s.Args ?? "", ct, capture: false).ConfigureAwait(false);
            }
            else if (msi)
            {
                var log = Path.Combine(Path.GetDirectoryName(file), "msi.log");
                r = await Proc.RunAsync(Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                    "/i " + Proc.Quote(file) + " /qn /norestart /l*v " + Proc.Quote(log) + " " + (s.Args ?? ""), ct, capture: false).ConfigureAwait(false);
            }
            else
            {
                var args = s.Args ?? "";
                if (args.Trim().StartsWith("auto", StringComparison.OrdinalIgnoreCase))
                {
                    // "auto" lub "auto <dodatkowe argumenty>" — rozpoznaj instalator, nie zgaduj w ciemno
                    var det = InstallerDetect.Detect(file);
                    if (det == null)
                        throw new Exception("Unknown installer type — no safe silent switch (set 'args' in the catalog)");
                    args = det.Args + args.Trim().Substring(4);
                    Log.Info(Path.GetFileName(file) + ": detected " + det.Kind + " → " + args);
                }
                r = await Proc.RunAsync(file, args, ct, capture: false).ConfigureAwait(false);
            }
            return r.ExitCode;
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
