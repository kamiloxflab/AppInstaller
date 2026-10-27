using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace AppInstaller.Core
{
    /// <summary>Teksty w kilku językach: {"pl": "...", "en": "..."}.</summary>
    public static class LocText
    {
        public static string Get(Dictionary<string, string> d)
        {
            if (d == null || d.Count == 0) return "";
            string v;
            if (d.TryGetValue(Loc.Lang, out v) && !string.IsNullOrEmpty(v)) return v;
            if (d.TryGetValue("en", out v) && !string.IsNullOrEmpty(v)) return v;
            if (d.TryGetValue("pl", out v) && !string.IsNullOrEmpty(v)) return v;
            return d.Values.First();
        }
    }

    [DataContract]
    public class Catalog
    {
        [DataMember(Name = "schema")] public int Schema { get; set; }
        [DataMember(Name = "updated")] public string Updated { get; set; }
        /// <summary>Szablon adresu ikony dla wpisów "@domena", np. https://www.google.com/s2/favicons?sz=64&amp;domain={0}</summary>
        [DataMember(Name = "iconService")] public string IconService { get; set; }
        /// <summary>Aplikacje instalowane zawsze, bez możliwości odznaczenia.</summary>
        [DataMember(Name = "core")] public List<string> Core { get; set; }
        [DataMember(Name = "categories")] public List<Category> Categories { get; set; }
        [DataMember(Name = "apps")] public List<AppDef> Apps { get; set; }
        /// <summary>Presety (wyłącznie z linii poleceń).</summary>
        [DataMember(Name = "presets")] public Dictionary<string, Preset> Presets { get; set; }

        [IgnoreDataMember] public string Origin { get; set; }

        public AppDef Find(string id)
        {
            if (Apps == null || id == null) return null;
            return Apps.FirstOrDefault(a => string.Equals(a.Id, id.Trim(), System.StringComparison.OrdinalIgnoreCase));
        }

        public bool IsCore(AppDef a)
        {
            return Core != null && Core.Any(c => string.Equals(c, a.Id, System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Adresy ikony w kolejności prób:
        ///  1) własna ikona z repo: catalog/icons/&lt;id&gt;.png (publikowana na stronie jako icons/&lt;id&gt;.png),
        ///  2) wpisy z "icons" (pełny URL, "@domena" przez iconService lub ścieżka względna),
        ///  3) pojedyncze "icon" (zgodność wsteczna).
        /// </summary>
        public List<string> IconUrls(AppDef a)
        {
            var list = new List<string>();
            if (!Config.BaseUrl.Contains("TWOJ-LOGIN")) list.Add(Config.BaseUrl + "icons/" + a.Id + ".png");
            var entries = new List<string>();
            if (a.Icons != null) entries.AddRange(a.Icons);
            if (!string.IsNullOrEmpty(a.Icon)) entries.Add(a.Icon);
            foreach (var e in entries)
            {
                if (string.IsNullOrWhiteSpace(e)) continue;
                string url;
                if (e.StartsWith("@")) { if (string.IsNullOrEmpty(IconService)) continue; url = string.Format(IconService, e.Substring(1)); }
                else if (e.StartsWith("http://") || e.StartsWith("https://")) url = e;
                else url = Config.BaseUrl + e.TrimStart('/');
                if (!list.Contains(url)) list.Add(url);
            }
            return list;
        }
    }

    [DataContract]
    public class Category
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public Dictionary<string, string> Name { get; set; }
        [DataMember(Name = "glyph")] public string Glyph { get; set; }
    }

    [DataContract]
    public class Preset
    {
        [DataMember(Name = "desc")] public Dictionary<string, string> Desc { get; set; }
        [DataMember(Name = "apps")] public List<string> Apps { get; set; }
    }

    [DataContract]
    public class AppDef
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "category")] public string Category { get; set; }
        /// <summary>"@domena.pl" (usługa ikon), pełny URL lub ścieżka względna do BaseUrl (np. icons/x.png).</summary>
        [DataMember(Name = "icon")] public string Icon { get; set; }
        /// <summary>Kilka źródeł ikony — używane jest pierwsze, które działa.</summary>
        [DataMember(Name = "icons")] public List<string> Icons { get; set; }
        [DataMember(Name = "desc")] public Dictionary<string, string> Desc { get; set; }
        [DataMember(Name = "homepage")] public string Homepage { get; set; }
        /// <summary>Regex dopasowany do DisplayName w kluczach Uninstall — tylko do oznaczenia "Zainstalowano".</summary>
        [DataMember(Name = "detect")] public string Detect { get; set; }
        /// <summary>Źródła w kolejności prób (fallback).</summary>
        [DataMember(Name = "sources")] public List<SourceDef> Sources { get; set; }
    }

    [DataContract]
    public class SourceDef
    {
        /// <summary>github | url | dotnet | ps | winget | choco</summary>
        [DataMember(Name = "type")] public string Type { get; set; }

        // github
        [DataMember(Name = "repo")] public string Repo { get; set; }
        [DataMember(Name = "asset")] public string Asset { get; set; }
        // url
        [DataMember(Name = "url")] public string Url { get; set; }
        // dotnet
        [DataMember(Name = "channel")] public string Channel { get; set; }
        [DataMember(Name = "product")] public string Product { get; set; }
        [DataMember(Name = "rid")] public string Rid { get; set; }
        // ps — skrypt wypisujący na stdout adres instalatora (ostatnia linia http...)
        [DataMember(Name = "script")] public string Script { get; set; }

        // winget / choco
        [DataMember(Name = "id")] public string Id { get; set; }
        /// <summary>x86 / x64 / arm64 — wymuszenie architektury (winget --architecture, choco --x86).</summary>
        [DataMember(Name = "arch")] public string Arch { get; set; }
        [DataMember(Name = "extra")] public string Extra { get; set; }
        /// <summary>
        /// Instalacja jako zalogowany użytkownik bez uprawnień administratora (np. Spotify odmawia instalacji "jako admin").
        /// Uruchamiane przez jednorazowe zadanie Harmonogramu z poziomem "Limited".
        /// </summary>
        [DataMember(Name = "asUser")] public bool AsUser { get; set; }

        // wspólne dla źródeł bezpośrednich
        /// <summary>exe | msi (domyślnie z rozszerzenia pliku).</summary>
        [DataMember(Name = "installer")] public string Installer { get; set; }
        [DataMember(Name = "args")] public string Args { get; set; }
        [DataMember(Name = "fileName")] public string FileName { get; set; }
        /// <summary>Jeśli ustawione — wymagany ważny podpis Authenticode, a Subject certyfikatu musi zawierać ten tekst.</summary>
        [DataMember(Name = "signer")] public string Signer { get; set; }
        /// <summary>Dodatkowe kody wyjścia uznawane za sukces.</summary>
        [DataMember(Name = "okCodes")] public List<int> OkCodes { get; set; }

        public bool IsDirect
        {
            get { return Type == "github" || Type == "url" || Type == "dotnet" || Type == "ps"; }
        }

        /// <summary>Grupa źródła używana przy --source (direct/winget/choco).</summary>
        public string Group
        {
            get { return IsDirect ? "direct" : Type; }
        }

        public override string ToString()
        {
            if (IsDirect) return "direct:" + Type;
            return Type + ":" + Id;
        }
    }

    [DataContract]
    public class UpdateManifest
    {
        [DataMember(Name = "version")] public string Version { get; set; }
        [DataMember(Name = "url")] public string Url { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
        [DataMember(Name = "notes")] public Dictionary<string, string> Notes { get; set; }
    }

    [DataContract]
    public class PayloadData
    {
        [DataMember(Name = "v")] public int V { get; set; }
        [DataMember(Name = "apps")] public List<string> Apps { get; set; }
        [DataMember(Name = "lang")] public string Lang { get; set; }
    }
}
