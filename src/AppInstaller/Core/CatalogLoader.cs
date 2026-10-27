using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace AppInstaller.Core
{
    /// <summary>
    /// Kolejność: 1) zdalny katalog z GitHub Pages (TYLKO z ważnym podpisem),
    /// 2) ostatni zweryfikowany katalog z cache, 3) katalog wbudowany w exe.
    /// Katalog zawiera skrypty PowerShell uruchamiane z prawami administratora,
    /// dlatego niepodpisany katalog zdalny nigdy nie jest używany.
    /// </summary>
    public static class CatalogLoader
    {
        static string CachePath { get { return Path.Combine(Config.Ensure(Path.Combine(Config.DataDir, "cache")), Config.CatalogFile); } }

        public static async Task<Catalog> LoadAsync(bool allowRemote, CancellationToken ct)
        {
            if (allowRemote && Crypto.HasPublicKey && !Config.BaseUrl.Contains("TWOJ-LOGIN"))
            {
                try
                {
                    var dataTask = Net.GetBytesAsync(Config.BaseUrl + Config.CatalogFile + "?t=" + DateTime.UtcNow.Ticks, ct, 15);
                    var sigTask = Net.GetStringAsync(Config.BaseUrl + Config.CatalogFile + Config.SignatureSuffix + "?t=" + DateTime.UtcNow.Ticks, ct, 15);
                    await Task.WhenAll(dataTask, sigTask).ConfigureAwait(false);
                    if (Crypto.VerifySignature(dataTask.Result, sigTask.Result))
                    {
                        var c = Validate(Json.Parse<Catalog>(dataTask.Result), "online");
                        try
                        {
                            File.WriteAllBytes(CachePath, dataTask.Result);
                            File.WriteAllText(CachePath + Config.SignatureSuffix, sigTask.Result);
                        }
                        catch { }
                        Log.Info("Catalog: online (signature OK), " + c.Apps.Count + " apps");
                        return c;
                    }
                    Log.Error("Catalog: REMOTE SIGNATURE INVALID — ignored");
                }
                catch (Exception ex) { Log.Warn("Catalog: remote unavailable — " + ex.Message); }
            }
            else if (allowRemote)
            {
                Log.Warn("Catalog: no public key or BaseUrl not configured — using built-in catalog only");
            }

            try
            {
                if (File.Exists(CachePath) && File.Exists(CachePath + Config.SignatureSuffix))
                {
                    var data = File.ReadAllBytes(CachePath);
                    if (Crypto.VerifySignature(data, File.ReadAllText(CachePath + Config.SignatureSuffix)))
                    {
                        var c = Validate(Json.Parse<Catalog>(data), "cache");
                        Log.Info("Catalog: cache (signature OK)");
                        return c;
                    }
                }
            }
            catch (Exception ex) { Log.Warn("Catalog: cache invalid — " + ex.Message); }

            Log.Info("Catalog: built-in");
            return LoadEmbedded();
        }

        public static Catalog LoadEmbedded()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("AppInstaller.apps.json"))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return Validate(Json.Parse<Catalog>(ms.ToArray()), "builtin");
            }
        }

        static Catalog Validate(Catalog c, string origin)
        {
            if (c == null || c.Apps == null || c.Apps.Count == 0) throw new InvalidDataException("Empty catalog");
            if (c.Core == null) c.Core = new System.Collections.Generic.List<string>();
            if (c.Categories == null) c.Categories = new System.Collections.Generic.List<Category>();
            if (c.Presets == null) c.Presets = new System.Collections.Generic.Dictionary<string, Preset>(StringComparer.OrdinalIgnoreCase);
            else c.Presets = new System.Collections.Generic.Dictionary<string, Preset>(c.Presets, StringComparer.OrdinalIgnoreCase);
            foreach (var a in c.Apps)
                if (a.Sources == null) a.Sources = new System.Collections.Generic.List<SourceDef>();
            c.Origin = origin;
            return c;
        }
    }
}
