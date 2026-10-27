using System;
using System.IO;
using System.Text;

namespace AppInstaller.Core
{
    /// <summary>
    /// Rozpoznaje typ instalatora .exe po sygnaturach w pliku i zwraca przełącznik cichej instalacji.
    /// Używane, gdy w katalogu źródło ma "args": "auto" (np. instalator, dla którego wydawca nie publikuje przełączników).
    /// </summary>
    public static class InstallerDetect
    {
        public class Result
        {
            public string Kind;
            public string Args;
        }

        static readonly Tuple<string, string, string>[] Signatures =
        {
            //           sygnatura w pliku                       typ                   cichy przełącznik
            Tuple.Create("Inno Setup Setup Data",               "Inno Setup",         "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-"),
            Tuple.Create("Inno Setup",                          "Inno Setup",         "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-"),
            Tuple.Create("Nullsoft Install System",             "NSIS",               "/S"),
            Tuple.Create("NullsoftInst",                        "NSIS",               "/S"),
            Tuple.Create(".wixburn",                            "WiX Burn",           "/quiet /norestart"),
            Tuple.Create("SquirrelSetup",                       "Squirrel",           "--silent"),
            Tuple.Create("Squirrel.Windows",                    "Squirrel",           "--silent"),
            Tuple.Create("Advanced Installer",                  "Advanced Installer", "/exenoui /qn /norestart"),
            Tuple.Create("InstallShield",                       "InstallShield",      "/s /v\"/qn /norestart\""),
        };

        /// <summary>Przeszukuje początek pliku (stub instalatora), domyślnie 8 MB. Zwraca null, gdy typ nieznany.</summary>
        public static Result Detect(string path, int maxBytes = 8 * 1024 * 1024)
        {
            byte[] buf;
            using (var fs = File.OpenRead(path))
            {
                var len = (int)Math.Min(fs.Length, maxBytes);
                buf = new byte[len];
                int off = 0, n;
                while (off < len && (n = fs.Read(buf, off, len - off)) > 0) off += n;
            }
            // Latin1 zachowuje bajty 1:1 — szukamy ASCII, także w UTF-16 (zasoby wersji)
            var ascii = Encoding.GetEncoding(28591).GetString(buf);
            var utf16 = Encoding.Unicode.GetString(buf, 0, buf.Length & ~1);
            foreach (var s in Signatures)
            {
                if (ascii.IndexOf(s.Item1, StringComparison.Ordinal) >= 0 || utf16.IndexOf(s.Item1, StringComparison.Ordinal) >= 0)
                    return new Result { Kind = s.Item2, Args = s.Item3 };
            }
            return null;
        }
    }
}
