using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AppInstaller.Core;
using AppInstaller.UI;

namespace AppInstaller
{
    public partial class App : Application
    {
        static Mutex _single;

        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, ex) =>
            {
                Log.Error("UI exception: " + ex.Exception);
                MessageBox.Show(ex.Exception.Message, "AppInstaller", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };

            Settings.Load();
            var cl = CommandLine.Parse(e.Args);
            var payload = cl.IsBatch ? null : Payload.Read(Config.ExePath);
            Loc.SetLanguage(cl.Lang ?? (payload != null ? payload.Lang : null) ?? Settings.Get("lang", "auto"));

            bool console = cl.Quiet || cl.Help || cl.List || cl.ListPresets || cl.Error != null;
            if (console)
            {
                AttachConsole(-1);
                Console.WriteLine();
            }
            Log.Console = cl.Quiet;
            Log.Init(cl.LogFile);
            Updater.CleanupOld();

            if (cl.Error != null)
            {
                Console.WriteLine(cl.Error);
                Console.WriteLine(CommandLine.HelpText());
                Shutdown(2);
                return;
            }
            if (cl.Help) { Console.WriteLine(CommandLine.HelpText()); Shutdown(0); return; }

            bool created;
            _single = new Mutex(true, @"Global\AppInstaller.SingleInstance", out created);
            if (!created && !cl.Updated)
            {
                if (console) Console.WriteLine("AppInstaller is already running.");
                else MessageBox.Show(Loc.Lang == "pl" ? "Instalator jest już uruchomiony." : "App Installer is already running.", "AppInstaller");
                Shutdown(5);
                return;
            }

            if (cl.List || cl.ListPresets)
            {
                var cat = await CatalogLoader.LoadAsync(!cl.Offline, CancellationToken.None);
                if (cl.List)
                    foreach (var a in cat.Apps)
                        Console.WriteLine(string.Format("{0,-24} {1,-36} {2}{3}", a.Id, a.Name,
                            string.Join(" > ", a.Sources.Select(s => s.Group).Distinct()), cat.IsCore(a) ? "  [core]" : ""));
                if (cl.ListPresets)
                    foreach (var p in cat.Presets)
                        Console.WriteLine(string.Format("{0,-12} {1} — {2}", p.Key, LocText.Get(p.Value.Desc), string.Join(", ", p.Value.Apps)));
                Shutdown(0);
                return;
            }

            if (cl.Quiet && cl.IsBatch)
            {
                Shutdown(await RunHeadlessAsync(cl));
                return;
            }

            ThemeManager.Init();
            var w = new MainWindow(cl, payload);
            MainWindow = w;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            w.Show();
        }

        static async Task<int> RunHeadlessAsync(CommandLine cl)
        {
            try
            {
                var cat = await CatalogLoader.LoadAsync(!cl.Offline, CancellationToken.None);
                List<string> unknown;
                var apps = cl.Resolve(cat, out unknown);
                foreach (var u in unknown) Log.Warn("Unknown: " + u);
                Log.Info("Installing " + apps.Count + ": " + string.Join(", ", apps.Select(a => a.Id)));
                var items = apps.Select(a => new InstallItem(a, cat.IsCore(a))).ToList();
                var engine = new InstallEngine(new EngineOptions { SourceOrder = cl.SourceOrder });
                var res = await engine.RunAsync(items, CancellationToken.None);
                foreach (var i in items) Console.WriteLine(string.Format("  [{0}] {1} — {2}", i.State == ItemState.Done ? "OK" : "!!", i.App.Name, i.Status));
                Console.WriteLine(Loc.T("Summary", res.Succeeded, res.Total, res.Failed) + (res.RebootRequired ? " " + Loc.T("RebootNeeded") : ""));
                Console.WriteLine("Log: " + Log.FilePath);
                return res.ExitCode;
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
                return 1;
            }
        }
    }
}
