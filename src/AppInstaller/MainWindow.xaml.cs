using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AppInstaller.Core;
using AppInstaller.UI;

namespace AppInstaller
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        readonly CommandLine _cl;
        readonly PayloadData _payload;
        Catalog _catalog;
        List<AppItem> _items = new List<AppItem>();
        List<InstallRow> _rows;
        CancellationTokenSource _cts;
        UpdateManifest _update;
        bool _installing, _batch;
        int _columns = 3;

        public int Columns { get { return _columns; } set { if (_columns != value) { _columns = value; On("Columns"); } } }

        public MainWindow(CommandLine cl, PayloadData payload)
        {
            _cl = cl;
            _payload = payload;
            _batch = cl.IsBatch || payload != null;
            InitializeComponent();

            if (_batch)
            {
                // "mini exe" z generatora / preset z CLI: małe okno postępu jak w Ninite
                Width = 720; Height = 600;
                MinWidth = 560; MinHeight = 440;
                ResizeMode = ResizeMode.CanMinimize;
            }

            SourceInitialized += (s, e) => ThemeManager.ApplyToWindow(this);
            Loaded += async (s, e) => await InitAsync();
            Scroller.SizeChanged += (s, e) => Columns = Math.Max(1, (int)((Scroller.ActualWidth - 32) / 300));
            Closing += OnClosing;
            Loc.Changed += Relocalize;
            ThemeManager.Changed += UpdateThemeButton;
            AppItem.CheckedChanged += UpdateSelection;
            PackageManagers.Status += key => Dispatcher.BeginInvoke(new Action(() => InstallSubtitle.Text = Loc.T(key)));
            UpdateThemeButton();
            LangText.Text = Loc.Lang.ToUpperInvariant();
        }

        // ============================ start ============================

        async Task InitAsync()
        {
            try
            {
                _catalog = await Task.Run(() => CatalogLoader.LoadAsync(!_cl.Offline, CancellationToken.None));
            }
            catch (Exception ex)
            {
                Log.Error("Catalog: " + ex);
                MessageBox.Show(this, ex.Message, "AppInstaller", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            _items = _catalog.Apps.Select(a => new AppItem(a, _catalog.IsCore(a), _catalog.IconUrls(a))).ToList();
            LoadingText.Visibility = Visibility.Collapsed;
            CatalogInfo.Text = Loc.T("Catalog_" + _catalog.Origin) + "\nv" + Config.AppVersion.ToString(3);
            BuildCategories();
            ApplyFilter();
            UpdateSelection();

            // ikony i stan "Zainstalowano" w tle
            var _ = LoadIconsAsync();
            var __ = Task.Run(() =>
            {
                InstalledApps.Refresh();
                foreach (var it in _items) { var inst = InstalledApps.IsInstalled(it.App); Dispatcher.BeginInvoke(new Action(() => it.IsInstalled = inst)); }
            });

            if (_batch)
            {
                List<AppDef> apps;
                var unknown = new List<string>();
                if (_payload != null) apps = CommandLine.Expand(_catalog, _payload.Apps, unknown);
                else apps = _cl.Resolve(_catalog, out unknown);
                foreach (var u in unknown) Log.Warn("Unknown app/preset: " + u);
                Title = Loc.T("UnattendedTitle", apps.Count);
                await StartInstallAsync(apps);
            }
            else if (!_cl.NoUpdate)
            {
                _update = await Task.Run(() => Updater.CheckAsync(CancellationToken.None));
                if (_update != null)
                {
                    UpdateText.Text = Loc.T("UpdateAvailable", _update.Version) +
                                      (_update.Notes != null ? "  " + LocText.Get(_update.Notes) : "");
                    UpdateBar.Visibility = Visibility.Visible;
                }
            }
        }

        async Task LoadIconsAsync()
        {
            using (var gate = new SemaphoreSlim(6))
            {
                var tasks = _items.Select(async it =>
                {
                    await gate.WaitAsync();
                    try { it.Icon = await IconCache.GetAsync(it.App.Id, it.IconUrls); }
                    finally { gate.Release(); }
                });
                await Task.WhenAll(tasks);
            }
        }

        // ============================ lista ============================

        void BuildCategories()
        {
            var sel = CategoryList.SelectedItem as CategoryItem;
            var list = new List<CategoryItem>
            {
                new CategoryItem { Id = "*", Glyph = "", Name = Loc.T("All"), Count = _items.Count(i => !i.IsCore) }
            };
            foreach (var c in _catalog.Categories)
            {
                var n = _items.Count(i => i.App.Category == c.Id && !i.IsCore);
                var core = _items.Count(i => i.App.Category == c.Id && i.IsCore);
                if (n + core == 0) continue;
                list.Add(new CategoryItem { Id = c.Id, Glyph = c.Glyph ?? "", Name = LocText.Get(c.Name), Count = n + core });
            }
            CategoryList.ItemsSource = list;
            CategoryList.SelectedItem = list.FirstOrDefault(c => sel != null && c.Id == sel.Id) ?? list[0];
        }

        void ApplyFilter()
        {
            if (_catalog == null) return;
            var cat = CategoryList.SelectedItem as CategoryItem;
            var catId = cat == null ? "*" : cat.Id;
            var q = (SearchBox.Text ?? "").Trim();
            Func<AppItem, bool> match = i =>
                (catId == "*" || i.App.Category == catId) &&
                (q.Length == 0 ||
                 i.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 i.Description.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 i.App.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);

            var core = _items.Where(i => i.IsCore && match(i)).ToList();
            var apps = _items.Where(i => !i.IsCore && match(i)).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            CoreItems.ItemsSource = core;
            CorePanel.Visibility = core.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AppItems.ItemsSource = apps;
            SectionTitle.Text = cat == null ? "" : cat.Name;
            SectionTitle.Visibility = apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoResults.Visibility = core.Count + apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void UpdateSelection()
        {
            // zależności ("requires"): zaznaczenie CapFrameX zaznacza też RTSS
            foreach (var it in _items.Where(i => i.IsChecked && i.App.Requires != null).ToList())
                foreach (var dep in it.App.Requires)
                {
                    var d = _items.FirstOrDefault(x => string.Equals(x.App.Id, dep, StringComparison.OrdinalIgnoreCase));
                    if (d != null && !d.IsChecked) d.IsChecked = true;
                }
            int n = _items.Count(i => i.IsChecked && !i.IsCore);
            int core = _items.Count(i => i.IsCore);
            SelectedText.Text = Loc.T("Selected", n, core);
            InstallText.Text = Loc.T("InstallCount", n + core);
        }

        void Category_Changed(object sender, SelectionChangedEventArgs e) { ApplyFilter(); Scroller.ScrollToTop(); }
        void Search_Changed(object sender, TextChangedEventArgs e) { ApplyFilter(); }

        void Card_Click(object sender, MouseButtonEventArgs e)
        {
            // klik w kafelek przełącza zaznaczenie (sam CheckBox obsługuje się sam)
            var visual = e.OriginalSource as System.Windows.Media.Visual;
            if (visual != null && FindParent<CheckBox>(visual) != null) return;
            var item = ((FrameworkElement)sender).DataContext as AppItem;
            if (item != null && !item.IsCore) item.IsChecked = !item.IsChecked;
        }

        void Clear_Click(object sender, RoutedEventArgs e)
        {
            foreach (var i in _items.Where(i => !i.IsCore)) i.IsChecked = false;
        }

        static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T) return (T)d;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        // ============================ instalacja ============================

        async void Install_Click(object sender, RoutedEventArgs e)
        {
            var ids = _items.Where(i => i.IsChecked).Select(i => i.App.Id);
            await StartInstallAsync(CommandLine.Expand(_catalog, ids, null));
        }

        async Task StartInstallAsync(List<AppDef> apps)
        {
            _installing = true;
            SelectView.Visibility = Visibility.Collapsed;
            InstallView.Visibility = Visibility.Visible;
            SearchBox.Visibility = Visibility.Hidden;
            ResultBar.Visibility = Visibility.Collapsed;
            BackButton.Visibility = Visibility.Collapsed;
            RestartButton.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Visible;
            CloseButton.IsEnabled = false;

            _rows = apps.Select(a => new InstallRow
            {
                Tile = _items.First(i => i.App == a),
                Item = new InstallItem(a, _catalog.IsCore(a))
            }).ToList();
            InstallItems.ItemsSource = _rows;
            InstallTitle.Text = Loc.T("InstallingTitle");
            foreach (var r in _rows) r.Item.PropertyChanged += (s, ev) => { if (ev.PropertyName == "State" || ev.PropertyName == "Progress") Dispatcher.BeginInvoke(new Action(UpdateOverall)); };
            UpdateOverall();
            Log.Info("UI install: " + string.Join(", ", apps.Select(a => a.Id)));

            _cts = new CancellationTokenSource();
            EngineResult res = null;
            bool cancelled = false;
            try
            {
                var engine = new InstallEngine(new EngineOptions { SourceOrder = _cl.SourceOrder });
                var items = _rows.Select(r => r.Item).ToList();
                res = await Task.Run(() => engine.RunAsync(items, _cts.Token));
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex) { Log.Error(ex.ToString()); }

            _installing = false;
            CancelButton.Visibility = Visibility.Collapsed;
            CloseButton.IsEnabled = true;
            BackButton.Visibility = _batch ? Visibility.Collapsed : Visibility.Visible;
            OverallBar.Value = 100;
            InstalledApps.Refresh();
            foreach (var it in _items) it.IsInstalled = InstalledApps.IsInstalled(it.App);

            ResultBar.Visibility = Visibility.Visible;
            if (cancelled || res == null)
            {
                InstallTitle.Text = Loc.T("Cancelled");
                ShowResult("", "CautionBrush", Loc.T("Cancelled"));
                return;
            }
            InstallTitle.Text = res.Failed == 0 ? Loc.T("Finished") : Loc.T("FinishedWithErrors");
            InstallSubtitle.Text = "";
            var text = Loc.T("Summary", res.Succeeded, res.Total, res.Failed) + (res.RebootRequired ? "  " + Loc.T("RebootNeeded") : "");
            if (res.Failed == 0) ShowResult("", "SuccessBrush", text);
            else ShowResult("", "CriticalBrush", text);
            RestartButton.Visibility = res.RebootRequired ? Visibility.Visible : Visibility.Collapsed;
        }

        void ShowResult(string glyph, string brush, string text)
        {
            ResultGlyph.Text = glyph;
            ResultGlyph.SetResourceReference(TextBlock.ForegroundProperty, brush);
            ResultBar.SetResourceReference(Border.BackgroundProperty, brush == "SuccessBrush" ? "SuccessBgBrush" : brush == "CriticalBrush" ? "CriticalBgBrush" : "InfoBgBrush");
            ResultText.Text = text;
        }

        void UpdateOverall()
        {
            if (_rows == null || _rows.Count == 0) return;
            int finished = _rows.Count(r => r.Item.IsFinished);
            // pobieranie = 40% wagi, instalacja = 60%
            double sum = _rows.Sum(r =>
                r.Item.IsFinished ? 1.0 :
                r.Item.State == ItemState.Installing ? 0.5 :
                r.Item.State == ItemState.Waiting ? 0.4 :
                r.Item.State == ItemState.Downloading && r.Item.Progress > 0 ? 0.4 * r.Item.Progress / 100.0 : 0);
            OverallBar.Value = sum * 100 / _rows.Count;
            if (_installing) InstallSubtitle.Text = Loc.T("Overall", finished, _rows.Count);
        }

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null && MessageBox.Show(this, Loc.T("ConfirmCancel"), Loc.T("AppTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                _cts.Cancel();
        }

        void Back_Click(object sender, RoutedEventArgs e)
        {
            InstallView.Visibility = Visibility.Collapsed;
            SelectView.Visibility = Visibility.Visible;
            SearchBox.Visibility = Visibility.Visible;
            Clear_Click(null, null);
        }

        void Close_Click(object sender, RoutedEventArgs e) { Close(); }

        void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            if (Log.FilePath != null) Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Log.FilePath + "\"") { UseShellExecute = true });
        }

        void Restart_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false });
            Close();
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (!_installing) return;
            if (MessageBox.Show(this, Loc.T("ConfirmCancel"), Loc.T("AppTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            if (_cts != null) _cts.Cancel();
        }

        // ============================ aktualizacja ============================

        async void Update_Click(object sender, RoutedEventArgs e)
        {
            if (_update == null) return;
            UpdateButton.IsEnabled = false;
            UpdateText.Text = Loc.T("Updating");
            try
            {
                await Task.Run(() => Updater.ApplyAsync(_update, p => Dispatcher.BeginInvoke(new Action(() =>
                    UpdateText.Text = Loc.T("Updating") + (p >= 0 ? " " + p + "%" : ""))), CancellationToken.None));
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Log.Error("Update: " + ex);
                UpdateText.Text = Loc.T("UpdateFailed", ex.Message);
                UpdateButton.IsEnabled = true;
            }
        }

        // ============================ język / motyw ============================

        void Lang_Click(object sender, RoutedEventArgs e)
        {
            var lang = Loc.Lang == "pl" ? "en" : "pl";
            Settings.Set("lang", lang);
            Loc.SetLanguage(lang);
        }

        void Relocalize()
        {
            LangText.Text = Loc.Lang.ToUpperInvariant();
            UpdateThemeButton();
            if (_catalog == null) return;
            CatalogInfo.Text = Loc.T("Catalog_" + _catalog.Origin) + "\nv" + Config.AppVersion.ToString(3);
            BuildCategories();
            ApplyFilter();
            UpdateSelection();
            if (_update != null && UpdateButton.IsEnabled) UpdateText.Text = Loc.T("UpdateAvailable", _update.Version);
        }

        void Theme_Click(object sender, RoutedEventArgs e) { ThemeManager.Set(ThemeManager.Next()); }

        void UpdateThemeButton()
        {
            var m = ThemeManager.Mode;
            ThemeGlyph.Text = m == ThemeMode.Auto ? "" : m == ThemeMode.Light ? "" : "";
            ThemeButton.ToolTip = Loc.T("Theme") + ": " + Loc.T(m == ThemeMode.Auto ? "ThemeAuto" : m == ThemeMode.Light ? "ThemeLight" : "ThemeDark");
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void On(string n) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(n)); }
    }
}
