using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AppInstaller.Core
{
    /// <summary>
    /// Bezpieczna autoaktualizacja:
    ///  1. update.json + update.json.sig z GitHub Pages; podpis RSA-3072 weryfikowany kluczem publicznym wbudowanym w exe
    ///     (przejęcie hostingu/DNS nie wystarczy do podmiany programu — potrzebny jest klucz prywatny z sekretu CI),
    ///  2. tylko wersja WYŻSZA niż bieżąca (brak downgrade),
    ///  3. pobranie wyłącznie przez HTTPS z dozwolonych hostów,
    ///  4. SHA-256 pliku musi zgadzać się z podpisanym manifestem,
    ///  5. jeśli bieżący exe jest podpisany Authenticode — nowy musi mieć ten sam podpis,
    ///  6. atomowa podmiana: exe → .old, nowy → exe, restart; .old usuwany przy następnym starcie.
    /// </summary>
    public static class Updater
    {
        public static void CleanupOld()
        {
            try
            {
                var old = Config.ExePath + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch { }
        }

        public static async Task<UpdateManifest> CheckAsync(CancellationToken ct)
        {
            if (!Crypto.HasPublicKey || Config.BaseUrl.Contains("TWOJ-LOGIN")) return null;
            try
            {
                var bust = "?t=" + DateTime.UtcNow.Ticks;
                var dataTask = Net.GetBytesAsync(Config.BaseUrl + Config.UpdateFile + bust, ct, 10);
                var sigTask = Net.GetStringAsync(Config.BaseUrl + Config.UpdateFile + Config.SignatureSuffix + bust, ct, 10);
                await Task.WhenAll(dataTask, sigTask).ConfigureAwait(false);
                if (!Crypto.VerifySignature(dataTask.Result, sigTask.Result))
                {
                    Log.Error("Update manifest signature INVALID — ignored");
                    return null;
                }
                var m = Json.Parse<UpdateManifest>(dataTask.Result);
                Version v;
                if (m == null || !Version.TryParse(m.Version, out v)) return null;
                if (Normalize(v) <= Normalize(Config.AppVersion)) return null;
                Log.Info("Update available: " + m.Version);
                return m;
            }
            catch (Exception ex)
            {
                Log.Warn("Update check failed: " + ex.Message);
                return null;
            }
        }

        static Version Normalize(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }

        /// <summary>Pobiera, weryfikuje i podmienia exe. Po sukcesie uruchamia nową wersję — wywołujący powinien zamknąć aplikację.</summary>
        public static async Task ApplyAsync(UpdateManifest m, Action<int> progress, CancellationToken ct)
        {
            Uri uri;
            if (!Uri.TryCreate(m.Url, UriKind.Absolute, out uri) || uri.Scheme != "https")
                throw new Exception("Update URL must be HTTPS");
            if (!Config.AllowedUpdateHosts.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase)))
                throw new Exception("Update host not allowed: " + uri.Host);
            if (string.IsNullOrWhiteSpace(m.Sha256) || m.Sha256.Trim().Length != 64)
                throw new Exception("Manifest has no SHA-256");

            var exe = Config.ExePath;
            var tmp = Path.Combine(Config.Ensure(Path.Combine(Config.DataDir, "update")), "AppInstaller-" + m.Version + ".exe");
            await Net.DownloadAsync(m.Url, tmp, progress, ct).ConfigureAwait(false);

            if (!Crypto.CheckHash(tmp, "sha256:" + m.Sha256.Trim()))
            {
                File.Delete(tmp);
                throw new Exception("SHA-256 mismatch");
            }

            string curSubject, newSubject;
            var cur = Crypto.VerifyAuthenticode(exe, out curSubject);
            if (cur == Crypto.SignatureState.Valid)
            {
                var nw = Crypto.VerifyAuthenticode(tmp, out newSubject);
                if (nw != Crypto.SignatureState.Valid || newSubject != curSubject)
                {
                    File.Delete(tmp);
                    throw new Exception("New version is not signed by the same publisher");
                }
            }

            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);           // działający exe można przemianować
            try { File.Copy(tmp, exe); }
            catch
            {
                File.Move(old, exe);       // przywróć
                throw;
            }
            try { File.Delete(tmp); } catch { }
            Log.Info("Updated to " + m.Version + " — restarting");
            Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = true });
        }
    }
}
