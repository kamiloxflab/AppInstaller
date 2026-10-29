using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AppInstaller.Core
{
    public class ResolvedDownload
    {
        public string Url;
        public string FileName;
        /// <summary>"sha256:..." / "sha512:..." lub null.</summary>
        public string Hash;
        public string Version;
        /// <summary>Adres licencji MSIX (GitHub, "licenseAsset").</summary>
        public string LicenseUrl;
    }

    /// <summary>
    /// Ustala adres NAJNOWSZEJ wersji instalatora w momencie instalacji — nic nie jest "zaszyte" na sztywno.
    /// </summary>
    public static class Resolvers
    {
        public static Task<ResolvedDownload> ResolveAsync(AppDef app, SourceDef s, CancellationToken ct)
        {
            switch (s.Type)
            {
                case "github": return GitHubAsync(app, s, ct);
                case "dotnet": return DotNetAsync(app, s, ct);
                case "ps": return PowerShellAsync(app, s, ct);
                case "url": return Task.FromResult(new ResolvedDownload { Url = s.Url, FileName = s.FileName ?? GuessName(app, s, s.Url) });
                default: throw new NotSupportedException("Not a direct source: " + s.Type);
            }
        }

        static string GuessName(AppDef app, SourceDef s, string url)
        {
            try
            {
                var last = Uri.UnescapeDataString(new Uri(url).AbsolutePath.Split('/').Last());
                if (last.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || last.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                    return Net.SafeFileName(last);
            }
            catch { }
            return app.Id + (s.Installer == "msi" ? ".msi" : ".exe");
        }

        // ---------- GitHub Releases ----------

        [DataContract] class GhRelease
        {
            [DataMember(Name = "tag_name")] public string Tag { get; set; }
            [DataMember(Name = "assets")] public List<GhAsset> Assets { get; set; }
        }
        [DataContract] class GhAsset
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "browser_download_url")] public string Url { get; set; }
            [DataMember(Name = "digest")] public string Digest { get; set; }
        }

        static async Task<ResolvedDownload> GitHubAsync(AppDef app, SourceDef s, CancellationToken ct)
        {
            var json = await Net.GetBytesAsync("https://api.github.com/repos/" + s.Repo + "/releases/latest", ct).ConfigureAwait(false);
            var rel = Json.Parse<GhRelease>(json);
            var rx = new Regex(s.Asset, RegexOptions.IgnoreCase);
            var asset = (rel.Assets ?? new List<GhAsset>()).FirstOrDefault(a => rx.IsMatch(a.Name));
            if (asset == null) throw new Exception("No asset matching /" + s.Asset + "/ in " + s.Repo + " " + rel.Tag);
            string license = null;
            if (!string.IsNullOrEmpty(s.LicenseAsset))
            {
                var lrx = new Regex(s.LicenseAsset, RegexOptions.IgnoreCase);
                var lic = rel.Assets.FirstOrDefault(a => lrx.IsMatch(a.Name));
                if (lic == null) throw new Exception("No license asset /" + s.LicenseAsset + "/ in " + s.Repo + " " + rel.Tag);
                license = lic.Url;
            }
            return new ResolvedDownload
            {
                Url = asset.Url,
                FileName = Net.SafeFileName(asset.Name),
                Hash = asset.Digest,          // GitHub udostępnia "sha256:..." dla zasobów wydań
                Version = rel.Tag,
                LicenseUrl = license
            };
        }

        // ---------- .NET (oficjalne metadane wydań Microsoft, z SHA-512) ----------

        [DataContract] class DnChannel
        {
            [DataMember(Name = "latest-release")] public string LatestRelease { get; set; }
            [DataMember(Name = "releases")] public List<DnRelease> Releases { get; set; }
        }
        [DataContract] class DnRelease
        {
            [DataMember(Name = "release-version")] public string Version { get; set; }
            [DataMember(Name = "windowsdesktop")] public DnProduct WindowsDesktop { get; set; }
            [DataMember(Name = "runtime")] public DnProduct Runtime { get; set; }
            [DataMember(Name = "aspnetcore-runtime")] public DnProduct AspNet { get; set; }
        }
        [DataContract] class DnProduct
        {
            [DataMember(Name = "version")] public string Version { get; set; }
            [DataMember(Name = "files")] public List<DnFile> Files { get; set; }
        }
        [DataContract] class DnFile
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "rid")] public string Rid { get; set; }
            [DataMember(Name = "url")] public string Url { get; set; }
            [DataMember(Name = "hash")] public string Hash { get; set; }
        }

        static readonly string[] DotNetMetaHosts =
        {
            "https://builds.dotnet.microsoft.com/dotnet/release-metadata/{0}/releases.json",
            "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/{0}/releases.json"
        };

        static async Task<ResolvedDownload> DotNetAsync(AppDef app, SourceDef s, CancellationToken ct)
        {
            Exception last = null;
            foreach (var host in DotNetMetaHosts)
            {
                try
                {
                    var data = await Net.GetBytesAsync(string.Format(host, s.Channel), ct, 30).ConfigureAwait(false);
                    var ch = Json.Parse<DnChannel>(data);
                    var rel = ch.Releases.FirstOrDefault(r => r.Version == ch.LatestRelease) ?? ch.Releases.First();
                    var product = s.Product == "runtime" ? rel.Runtime : s.Product == "aspnetcore" ? rel.AspNet : rel.WindowsDesktop;
                    var file = product.Files.FirstOrDefault(f => f.Rid == s.Rid && f.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                    if (file == null) throw new Exception("No " + s.Rid + " installer in .NET " + rel.Version);
                    return new ResolvedDownload
                    {
                        Url = file.Url,
                        FileName = Net.SafeFileName(product.Version + "-" + file.Name),
                        Hash = string.IsNullOrEmpty(file.Hash) ? null : "sha512:" + file.Hash,
                        Version = product.Version
                    };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; }
            }
            throw last ?? new Exception(".NET metadata unavailable");
        }

        // ---------- PowerShell (scraping strony wydawcy) ----------

        static async Task<ResolvedDownload> PowerShellAsync(AppDef app, SourceDef s, CancellationToken ct)
        {
            var r = await Proc.PowerShellAsync(s.Script, ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            var lines = (r.Output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
            var url = lines.LastOrDefault(l => l.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            // skrypt może dodatkowo wypisać "sha256:<hex>" (np. Kate z pliku .sha256) albo "version:<x>"
            var hash = lines.LastOrDefault(l => Regex.IsMatch(l, "^sha(256|384|512):[0-9a-fA-F]{64,128}$"));
            var ver = lines.LastOrDefault(l => l.StartsWith("version:", StringComparison.OrdinalIgnoreCase));
            if (r.ExitCode != 0 || url == null)
                throw new Exception("PS resolver failed (exit " + r.ExitCode + "): " + Trim(r.Output));
            return new ResolvedDownload
            {
                Url = url,
                FileName = s.FileName ?? GuessName(app, s, url),
                Hash = hash == null ? null : hash.ToLowerInvariant(),
                Version = ver == null ? null : ver.Substring(8).Trim()
            };
        }

        static string Trim(string s)
        {
            s = (s ?? "").Trim();
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }
    }
}
