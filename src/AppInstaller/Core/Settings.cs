using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AppInstaller.Core
{
    /// <summary>Proste ustawienia użytkownika (klucz=wartość) w %LocalAppData%\AppInstaller\settings.ini.</summary>
    public static class Settings
    {
        static readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static string FilePath { get { return Path.Combine(Config.UserDir, "settings.ini"); } }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var i = line.IndexOf('=');
                    if (i > 0) Values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { }
        }

        public static string Get(string key, string def = null)
        {
            string v;
            return Values.TryGetValue(key, out v) ? v : def;
        }

        public static void Set(string key, string value)
        {
            Values[key] = value;
            try { File.WriteAllLines(FilePath, Values.Select(kv => kv.Key + "=" + kv.Value)); } catch { }
        }
    }
}
