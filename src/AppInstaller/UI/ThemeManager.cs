using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AppInstaller.Core;
using Microsoft.Win32;

namespace AppInstaller.UI
{
    public enum ThemeMode { Auto, Light, Dark }

    /// <summary>
    /// Motyw jasny/ciemny (domyślnie jak w systemie), kolor akcentu z Windows,
    /// tło Mica na Windows 11 (fallback: jednolite tło na Windows 10).
    /// </summary>
    public static class ThemeManager
    {
        public static ThemeMode Mode { get; private set; }
        public static bool IsDark { get; private set; }
        public static bool MicaSupported { get { return Environment.OSVersion.Version.Build >= 22000; } }
        public static event Action Changed;

        public static void Init()
        {
            ThemeMode m;
            Mode = Enum.TryParse(Settings.Get("theme", "Auto"), out m) ? m : ThemeMode.Auto;
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
                    Application.Current.Dispatcher.BeginInvoke(new Action(Apply));
            };
            Apply();
        }

        public static void Set(ThemeMode mode)
        {
            Mode = mode;
            Settings.Set("theme", mode.ToString());
            Apply();
        }

        public static ThemeMode Next()
        {
            return Mode == ThemeMode.Auto ? ThemeMode.Light : Mode == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Auto;
        }

        public static void Apply()
        {
            IsDark = Mode == ThemeMode.Dark || (Mode == ThemeMode.Auto && SystemIsDark());
            var app = Application.Current;
            var dict = new ResourceDictionary { Source = new Uri("pack://application:,,,/AppInstaller;component/Themes/" + (IsDark ? "Dark" : "Light") + ".xaml") };
            var md = app.Resources.MergedDictionaries;
            // [0] = kolory (podmieniane), [1] = style kontrolek (stałe)
            if (md.Count > 0) md[0] = dict; else md.Insert(0, dict);

            var accent = SystemAccent();
            // W ciemnym motywie Windows używa jaśniejszego wariantu akcentu
            var a = IsDark ? Lighten(accent, 0.45) : Darken(accent, 0.05);
            app.Resources["AccentBrush"] = new SolidColorBrush(a);
            app.Resources["AccentHoverBrush"] = new SolidColorBrush(Color.FromArgb(0xE6, a.R, a.G, a.B));
            app.Resources["AccentPressedBrush"] = new SolidColorBrush(Color.FromArgb(0xCC, a.R, a.G, a.B));
            app.Resources["AccentSubtleBrush"] = new SolidColorBrush(Color.FromArgb(0x26, a.R, a.G, a.B));
            app.Resources["OnAccentBrush"] = new SolidColorBrush(IsDark ? Colors.Black : Colors.White);

            // Bez Mica: nieprzezroczyste tło okna
            if (!MicaSupported)
                app.Resources["WindowBackdropBrush"] = app.Resources["WindowBgSolidBrush"];

            foreach (Window w in app.Windows) ApplyToWindow(w);
            var h = Changed;
            if (h != null) h();
        }

        public static void ApplyToWindow(Window w)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));            // DWMWA_USE_IMMERSIVE_DARK_MODE
            if (!MicaSupported) return;

            var src = HwndSource.FromHwnd(hwnd);
            if (src != null && src.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
            int corner = 2;                                                   // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
            if (Environment.OSVersion.Version.Build >= 22621)
            {
                int backdrop = 2;                                             // DWMSBT_MAINWINDOW (Mica)
                DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));   // DWMWA_SYSTEMBACKDROP_TYPE
            }
            else
            {
                int mica = 1;
                DwmSetWindowAttribute(hwnd, 1029, ref mica, sizeof(int));     // nieudokumentowane, Win11 21H2
            }
        }

        static bool SystemIsDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        static Color SystemAccent()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    var v = k == null ? null : k.GetValue("AccentColor");
                    if (v is int)
                    {
                        var abgr = unchecked((uint)(int)v);
                        return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
                    }
                }
            }
            catch { }
            return Color.FromRgb(0x00, 0x67, 0xC0); // domyślny niebieski Windows 11
        }

        static Color Lighten(Color c, double f)
        {
            return Color.FromRgb((byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));
        }

        static Color Darken(Color c, double f)
        {
            return Color.FromRgb((byte)(c.R * (1 - f)), (byte)(c.G * (1 - f)), (byte)(c.B * (1 - f)));
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MARGINS { public int Left, Right, Top, Bottom; }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);
    }
}
