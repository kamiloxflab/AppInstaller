using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AppInstaller.Core
{
    /// <summary>
    /// Wybór aplikacji "doklejony" do exe przez generator webowy (jak Ninite) — bez przebudowy programu.
    ///
    /// Format końcówki pliku:  [JSON UTF-8][int32 LE długość JSON]["AIPAYLD1"]
    /// Alternatywa zachowująca podpis cyfrowy exe: nazwa pliku, np.  AppInstaller[firefox+7zip+vlc].exe
    ///
    /// Payload zawiera WYŁĄCZNIE identyfikatory aplikacji — nigdy kod. Polecenia instalacji
    /// pochodzą zawsze z podpisanego katalogu, więc spreparowany exe nie może wstrzyknąć skryptu.
    /// </summary>
    public static class Payload
    {
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("AIPAYLD1");
        const int MaxJson = 64 * 1024;

        public static PayloadData Read(string exePath)
        {
            try
            {
                using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < 12) return FromFileName(exePath);
                    var tail = new byte[12];
                    fs.Seek(-12, SeekOrigin.End);
                    ReadExact(fs, tail);
                    for (int i = 0; i < 8; i++)
                        if (tail[4 + i] != Magic[i]) return FromFileName(exePath);
                    int len = BitConverter.ToInt32(tail, 0);
                    if (len <= 0 || len > MaxJson || len > fs.Length - 12) return null;
                    var json = new byte[len];
                    fs.Seek(-12 - len, SeekOrigin.End);
                    ReadExact(fs, json);
                    var p = Json.Parse<PayloadData>(json);
                    return p != null && p.Apps != null && p.Apps.Count > 0 ? p : null;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Payload read failed: " + ex.Message);
                return null;
            }
        }

        static PayloadData FromFileName(string exePath)
        {
            var m = Regex.Match(Path.GetFileNameWithoutExtension(exePath) ?? "", @"\[([A-Za-z0-9._+,\- ]+)\]");
            if (!m.Success) return null;
            var ids = m.Groups[1].Value.Split(new[] { '+', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            return ids.Count == 0 ? null : new PayloadData { V = 1, Apps = ids };
        }

        static void ReadExact(Stream s, byte[] buf)
        {
            int off = 0;
            while (off < buf.Length)
            {
                int n = s.Read(buf, off, buf.Length - off);
                if (n <= 0) throw new EndOfStreamException();
                off += n;
            }
        }

        /// <summary>Do testów / narzędzi: tworzy exe z payloadem (odpowiednik generatora webowego).</summary>
        public static void Write(string baseExe, string target, IEnumerable<string> apps, string lang)
        {
            var json = Encoding.UTF8.GetBytes(Json.Write(new PayloadData { V = 1, Apps = apps.ToList(), Lang = lang }));
            using (var o = File.Create(target))
            {
                var b = File.ReadAllBytes(baseExe);
                o.Write(b, 0, b.Length);
                o.Write(json, 0, json.Length);
                o.Write(BitConverter.GetBytes(json.Length), 0, 4);
                o.Write(Magic, 0, Magic.Length);
            }
        }
    }
}
