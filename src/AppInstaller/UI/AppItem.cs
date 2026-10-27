using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppInstaller.Core;

namespace AppInstaller.UI
{
    /// <summary>Kafelek aplikacji na liście wyboru.</summary>
    public class AppItem : INotifyPropertyChanged
    {
        public AppDef App { get; private set; }
        public bool IsCore { get; private set; }
        public string IconUrl { get; private set; }

        public AppItem(AppDef app, bool isCore, string iconUrl)
        {
            App = app;
            IsCore = isCore;
            IconUrl = iconUrl;
            _checked = isCore;
            Loc.Changed += () => On("Description");
        }

        public string Name { get { return App.Name; } }
        public string Description { get { return LocText.Get(App.Desc); } }
        public string Monogram { get { return string.IsNullOrEmpty(App.Name) ? "?" : App.Name.TrimStart('.').Substring(0, 1).ToUpperInvariant(); } }
        public string SourcesText { get { return string.Join(" · ", App.Sources.Select(s => s.IsDirect ? "direct" : s.Type).Distinct()); } }

        public Brush MonogramBrush
        {
            get
            {
                // stabilny kolor z identyfikatora
                int h = App.Id.Aggregate(17, (a, c) => a * 31 + c);
                var palette = new[] { "#0067C0", "#8764B8", "#038387", "#CA5010", "#498205", "#C239B3", "#0078D4", "#567C73" };
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[Math.Abs(h % palette.Length)]));
                b.Freeze();
                return b;
            }
        }

        bool _checked, _installed;
        ImageSource _icon;

        public bool IsChecked
        {
            get { return _checked; }
            set { if (IsCore) return; _checked = value; On("IsChecked"); var h = CheckedChanged; if (h != null) h(); }
        }
        public bool IsInstalled { get { return _installed; } set { _installed = value; On("IsInstalled"); } }
        public ImageSource Icon { get { return _icon; } set { _icon = value; On("Icon"); On("HasIcon"); } }
        public bool HasIcon { get { return _icon != null; } }

        public static event Action CheckedChanged;
        public event PropertyChangedEventHandler PropertyChanged;
        void On(string n) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(n)); }
    }

    /// <summary>Pobieranie ikon z cache na dysku (%LocalAppData%\AppInstaller\icons), odświeżanie co 30 dni.</summary>
    public static class IconCache
    {
        static string Dir { get { return Config.Ensure(Path.Combine(Config.UserDir, "icons")); } }

        public static async Task<ImageSource> GetAsync(string id, string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var file = Path.Combine(Dir, Net.SafeFileName(id) + ".img");
            try
            {
                if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-30))
                {
                    var bytes = await Net.GetBytesAsync(url, System.Threading.CancellationToken.None, 15);
                    if (bytes.Length > 0 && bytes.Length < 512 * 1024) File.WriteAllBytes(file, bytes);
                }
            }
            catch { /* offline — użyj starej kopii lub monogramu */ }
            if (!File.Exists(file)) return null;
            try
            {
                var bmp = new BitmapImage();
                using (var fs = File.OpenRead(file))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 64;
                    bmp.StreamSource = fs;
                    bmp.EndInit();
                }
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }
}
