using System;
using System.IO;
using System.Text;

namespace AppInstaller.Core
{
    public static class Log
    {
        static readonly object Sync = new object();
        static StreamWriter _writer;

        public static string FilePath { get; private set; }
        public static event Action<string> Line;
        public static bool Console { get; set; }

        public static void Init(string customPath)
        {
            try
            {
                FilePath = customPath ?? Path.Combine(Config.Ensure(Path.Combine(Config.DataDir, "logs")),
                    "AppInstaller-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                _writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                _writer.AutoFlush = true;
                CleanupOld();
            }
            catch { _writer = null; }
            Info("AppInstaller " + Config.AppVersion + " | " + Environment.OSVersion + " | x64OS=" + Environment.Is64BitOperatingSystem);
        }

        static void CleanupOld()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                foreach (var f in Directory.GetFiles(dir, "AppInstaller-*.log"))
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-30)) File.Delete(f);
            }
            catch { }
        }

        public static void Info(string msg) { Write("INFO", msg); }
        public static void Warn(string msg) { Write("WARN", msg); }
        public static void Error(string msg) { Write("ERR ", msg); }

        static void Write(string lvl, string msg)
        {
            var line = DateTime.Now.ToString("HH:mm:ss") + " " + lvl + " " + msg;
            lock (Sync)
            {
                try { if (_writer != null) _writer.WriteLine(line); } catch { }
                if (Console) try { System.Console.WriteLine(line); } catch { }
            }
            var h = Line;
            if (h != null) try { h(line); } catch { }
        }
    }
}
