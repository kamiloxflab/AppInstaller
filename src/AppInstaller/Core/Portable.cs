using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Win32;

namespace AppInstaller.Core
{
    /// <summary>
    /// Instalacja aplikacji dostarczanej jako ZIP (portable), np. TubeFlow:
    ///  - rozpakowanie do %ProgramFiles%\&lt;installDir&gt; (podmiana poprzedniej wersji),
    ///  - skrót w Menu Start (wszyscy użytkownicy) i opcjonalnie na pulpicie,
    ///  - wpis w "Aplikacje zainstalowane" z odinstalowaniem.
    /// </summary>
    public static class Portable
    {
        const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

        public static int Install(AppDef app, SourceDef s, string zip, string version)
        {
            if (string.IsNullOrEmpty(s.Exe) || s.Exe.Contains("..") || Path.IsPathRooted(s.Exe))
                throw new Exception("Portable source needs a relative 'exe'");
            var dirName = Net.SafeFileName(string.IsNullOrEmpty(s.InstallDir) ? app.Name : s.InstallDir);
            var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), dirName);
            var staging = target + ".new";
            var old = target + ".old";

            // 1) rozpakuj obok (ochrona przed zip-slip), dopiero potem podmień folder
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            if (zip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) Extract(zip, staging);
            else File.Copy(zip, Path.Combine(staging, s.Exe), true);   // pojedynczy exe (single-file)
            var root = SingleTopFolder(staging);
            if (!File.Exists(Path.Combine(root, s.Exe)))
                throw new Exception("'" + s.Exe + "' not found in the archive");

            // 2) zamknij działającą starą wersję, podmień folder
            KillRunning(target);
            if (Directory.Exists(old)) Directory.Delete(old, true);
            if (Directory.Exists(target)) Directory.Move(target, old);
            try
            {
                Directory.Move(root, target);
            }
            catch
            {
                if (Directory.Exists(old) && !Directory.Exists(target)) Directory.Move(old, target);
                throw;
            }
            TryDelete(staging);
            TryDelete(old);

            var exe = Path.Combine(target, s.Exe);

            // 3) skróty
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), app.Name + ".lnk");
            CreateShortcut(startMenu, exe);
            string desktop = null;
            if (s.Desktop)
            {
                desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), app.Name + ".lnk");
                CreateShortcut(desktop, exe);
            }

            // 4) wpis odinstalowania (widoczny w Ustawieniach i wykrywany przez "detect")
            var key = "AppInstaller-" + app.Id;
            var uninstall = "cmd.exe /c taskkill /f /im \"" + Path.GetFileName(exe) + "\" >nul 2>&1" +
                            " & rmdir /s /q \"" + target + "\"" +
                            " & del /f /q \"" + startMenu + "\"" +
                            (desktop != null ? " & del /f /q \"" + desktop + "\"" : "") +
                            " & reg delete \"HKLM\\" + UninstallRoot + key + "\" /f /reg:64";
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = hklm.CreateSubKey(UninstallRoot + key))
            {
                k.SetValue("DisplayName", app.Name);
                k.SetValue("DisplayVersion", (version ?? "").TrimStart('v', 'V'));
                k.SetValue("DisplayIcon", exe);
                k.SetValue("InstallLocation", target);
                k.SetValue("Publisher", string.IsNullOrEmpty(s.Publisher) ? app.Name : s.Publisher);
                k.SetValue("UninstallString", uninstall);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(DirSize(target) / 1024), RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
            Log.Info(app.Id + ": portable installed to " + target + " (" + version + ")");
            return 0;
        }

        internal static void Extract(string zip, string dest)
        {
            var full = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
            using (var z = ZipFile.OpenRead(zip))
            {
                foreach (var e in z.Entries)
                {
                    var path = Path.GetFullPath(Path.Combine(dest, e.FullName));
                    if (!path.StartsWith(full, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("Archive entry outside target: " + e.FullName);
                    if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    e.ExtractToFile(path, true);
                }
            }
        }

        /// <summary>ZIP z jednym folderem głównym (TubeFlow-1.3.0\...) → używamy jego zawartości.</summary>
        static string SingleTopFolder(string dir)
        {
            var dirs = Directory.GetDirectories(dir);
            return Directory.GetFiles(dir).Length == 0 && dirs.Length == 1 ? dirs[0] : dir;
        }

        static void KillRunning(string folder)
        {
            var prefix = folder.TrimEnd('\\') + "\\";
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var path = p.MainModule.FileName;
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(5000); }
                }
                catch { /* procesy systemowe / bez dostępu */ }
                finally { p.Dispose(); }
            }
        }

        static void CreateShortcut(string lnk, string target)
        {
            // WScript.Shell przez późne wiązanie — bez dodatkowych referencji COM
            var t = Type.GetTypeFromProgID("WScript.Shell");
            var shell = Activator.CreateInstance(t);
            try
            {
                var sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                var st = sc.GetType();
                st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { target });
                st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
                st.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
                st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }

        static long DirSize(string d)
        {
            try { return new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch { return 0; }
        }

        static void TryDelete(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }
    }
}
