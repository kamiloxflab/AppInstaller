using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AppInstaller.Core
{
    /// <summary>Lista DisplayName z kluczy Uninstall (HKLM 64/32-bit, HKCU) — do znacznika "Zainstalowano".</summary>
    public static class InstalledApps
    {
        static List<string> _names;

        public static void Refresh()
        {
            var list = new List<string>();
            const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var root = RegistryKey.OpenBaseKey(hive, view))
                    using (var k = root.OpenSubKey(key))
                    {
                        if (k == null) continue;
                        foreach (var sub in k.GetSubKeyNames())
                        {
                            using (var s = k.OpenSubKey(sub))
                            {
                                var n = s == null ? null : s.GetValue("DisplayName") as string;
                                if (!string.IsNullOrEmpty(n)) list.Add(n);
                            }
                        }
                    }
                }
                catch { }
            }
            _names = list;
        }

        public static bool IsInstalled(AppDef app)
        {
            if (string.IsNullOrEmpty(app.Detect)) return false;
            if (_names == null) Refresh();
            try
            {
                var rx = new Regex(app.Detect, RegexOptions.IgnoreCase);
                foreach (var n in _names) if (rx.IsMatch(n)) return true;
            }
            catch (ArgumentException) { }
            return false;
        }
    }
}
