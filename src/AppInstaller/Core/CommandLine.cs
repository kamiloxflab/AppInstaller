using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AppInstaller.Core
{
    /// <summary>
    /// AppInstaller.exe [opcje]
    ///   --preset NAZWA[,NAZWA2]   preset z katalogu (wiele po przecinku)
    ///   --preset-file PLIK.json   własny preset: {"apps":["firefox","7zip"]}
    ///   --apps ID[,ID...]         konkretne aplikacje
    ///   --quiet                   bez okna (log + kod wyjścia) — dla skryptów/Intune/GPO
    ///   --source winget,direct,choco   wymuszona kolejność/zakres źródeł
    ///   --lang pl|en   --log PLIK   --no-update   --offline
    ///   --list | --list-presets   wypisz katalog / presety
    ///   --help
    /// Bez --preset/--apps: zwykłe okno wyboru (presety nie są dostępne w UI — tylko w CLI).
    /// Pakiety bazowe (VC++ AIO, .NET Desktop) są dołączane zawsze.
    /// </summary>
    public class CommandLine
    {
        public List<string> Presets = new List<string>();
        public string PresetFile;
        public List<string> Apps = new List<string>();
        public bool Quiet, List, ListPresets, Help, NoUpdate, Offline, Updated;
        public string[] SourceOrder;
        public string Lang, LogFile;
        public string Error;

        public bool IsBatch { get { return Presets.Count > 0 || PresetFile != null || Apps.Count > 0; } }

        public static CommandLine Parse(string[] args)
        {
            var c = new CommandLine();
            for (int i = 0; i < args.Length; i++)
            {
                var raw = args[i];
                string a = raw, val = null;
                // obsługa: --opcja wartość | --opcja=wartość | /opcja:wartość
                if (a.StartsWith("/")) a = "--" + a.Substring(1);
                int eq = a.IndexOfAny(new[] { '=', ':' }, 2);
                if (a.StartsWith("--") && eq > 0) { val = a.Substring(eq + 1); a = a.Substring(0, eq); }
                a = a.ToLowerInvariant();

                Func<string> next = () =>
                {
                    if (val != null) return val;
                    if (i + 1 < args.Length) return args[++i];
                    c.Error = "Missing value for " + raw;
                    return "";
                };

                switch (a)
                {
                    case "--preset": case "-p": c.Presets.AddRange(Split(next())); break;
                    case "--preset-file": c.PresetFile = next(); break;
                    case "--apps": case "-a": c.Apps.AddRange(Split(next())); break;
                    case "--quiet": case "-q": case "--silent": c.Quiet = true; break;
                    case "--source": c.SourceOrder = Split(next()).Select(s => s.ToLowerInvariant()).ToArray(); break;
                    case "--lang": c.Lang = next().ToLowerInvariant(); break;
                    case "--log": c.LogFile = next(); break;
                    case "--no-update": c.NoUpdate = true; break;
                    case "--offline": c.Offline = true; c.NoUpdate = true; break;
                    case "--list": c.List = true; break;
                    case "--list-presets": c.ListPresets = true; break;
                    case "--updated": c.Updated = true; break;
                    case "--help": case "-h": case "-?": case "--?": c.Help = true; break;
                    default: c.Error = "Unknown option: " + raw; break;
                }
            }
            if (c.SourceOrder != null && c.SourceOrder.Any(s => s != "direct" && s != "winget" && s != "choco"))
                c.Error = "--source accepts: direct, winget, choco";
            return c;
        }

        static IEnumerable<string> Split(string s)
        {
            return (s ?? "").Split(new[] { ',', ';', '+' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0);
        }

        /// <summary>Rozwija presety/listy do aplikacji z katalogu. Pakiety bazowe zawsze na początku.</summary>
        public List<AppDef> Resolve(Catalog cat, out List<string> unknown)
        {
            unknown = new List<string>();
            var ids = new List<string>(Apps);
            foreach (var p in Presets)
            {
                Preset preset;
                if (cat.Presets.TryGetValue(p, out preset) && preset.Apps != null) ids.AddRange(preset.Apps);
                else unknown.Add("preset:" + p);
            }
            if (PresetFile != null)
            {
                try
                {
                    var preset = Json.Parse<Preset>(File.ReadAllBytes(PresetFile));
                    if (preset.Apps != null) ids.AddRange(preset.Apps);
                }
                catch (Exception ex) { unknown.Add("preset-file:" + ex.Message); }
            }
            return Expand(cat, ids, unknown);
        }

        public static List<AppDef> Expand(Catalog cat, IEnumerable<string> ids, List<string> unknown)
        {
            var result = new List<AppDef>();
            foreach (var id in cat.Core.Concat(ids))
                Add(cat, id, result, unknown, 0);
            return result;
        }

        /// <summary>Dodaje aplikację, a przed nią jej zależności ("requires"), bez duplikatów.</summary>
        static void Add(Catalog cat, string id, List<AppDef> result, List<string> unknown, int depth)
        {
            var app = cat.Find(id);
            if (app == null) { if (unknown != null) unknown.Add(id); return; }
            if (result.Contains(app) || depth > 5) return;
            if (app.Requires != null)
                foreach (var dep in app.Requires) Add(cat, dep, result, unknown, depth + 1);
            if (!result.Contains(app)) result.Add(app);
        }

        public static string HelpText()
        {
            return Loc.Lang == "pl" ? @"
AppInstaller — instalator aplikacji (zawsze najnowsze wersje)

  AppInstaller.exe                          okno wyboru aplikacji
  AppInstaller.exe --preset biuro           instalacja presetu z oknem postępu
  AppInstaller.exe --preset biuro,dev -q    bez okna (kod wyjścia: 0 OK, 1 błędy, 3010 restart)
  AppInstaller.exe --apps firefox,7zip,vlc
  AppInstaller.exe --preset-file moj.json   {""apps"":[""firefox"",""7zip""]}

Opcje:
  --source direct,winget,choco   kolejność i zakres źródeł (domyślnie: z katalogu)
  --lang pl|en   --log PLIK   --no-update   --offline (tylko katalog wbudowany)
  --list         lista aplikacji      --list-presets   lista presetów
Pakiety bazowe (VC++ AIO, .NET Desktop 8/9/10 x64+x86) są instalowane zawsze.
" : @"
AppInstaller — always installs the latest versions

  AppInstaller.exe                          app picker window
  AppInstaller.exe --preset office          install a preset with progress window
  AppInstaller.exe --preset office,dev -q   no window (exit code: 0 OK, 1 errors, 3010 restart)
  AppInstaller.exe --apps firefox,7zip,vlc
  AppInstaller.exe --preset-file my.json    {""apps"":[""firefox"",""7zip""]}

Options:
  --source direct,winget,choco   source order and scope (default: from catalog)
  --lang pl|en   --log FILE   --no-update   --offline (built-in catalog only)
  --list         list apps            --list-presets   list presets
Base runtimes (VC++ AIO, .NET Desktop 8/9/10 x64+x86) are always installed.
";
        }
    }
}
