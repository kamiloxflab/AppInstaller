using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace AppInstaller.Core
{
    /// <summary>
    /// Lokalizacja PL/EN. W XAML: {Binding [Klucz], Source={x:Static core:Loc.I}} — zmiana języka odświeża UI na żywo.
    /// Nowy tekst = jedna linia w tabeli poniżej.
    /// </summary>
    public class Loc : INotifyPropertyChanged
    {
        public static readonly Loc I = new Loc();
        public static string Lang { get; private set; }

        static readonly Dictionary<string, string[]> S = new Dictionary<string, string[]>
        {
            //  klucz                    PL                                                         EN
            { "AppTitle",           new[] { "Instalator aplikacji",                                   "App Installer" } },
            { "Search",             new[] { "Szukaj aplikacji",                                       "Search apps" } },
            { "All",                new[] { "Wszystkie",                                              "All apps" } },
            { "Core",               new[] { "Zawsze instalowane",                                     "Always installed" } },
            { "CoreHint",           new[] { "Biblioteki wymagane przez większość programów. Instalowane automatycznie przy każdym uruchomieniu.", "Runtimes most programs need. Installed automatically every time." } },
            { "Selected",           new[] { "Wybrano: {0} + {1} bazowych",                            "Selected: {0} + {1} base" } },
            { "Install",            new[] { "Zainstaluj",                                             "Install" } },
            { "InstallCount",       new[] { "Zainstaluj ({0})",                                       "Install ({0})" } },
            { "Clear",              new[] { "Wyczyść wybór",                                          "Clear selection" } },
            { "Installed",          new[] { "Zainstalowano",                                          "Installed" } },
            { "NoResults",          new[] { "Brak aplikacji pasujących do wyszukiwania.",             "No apps match your search." } },
            { "Sources",            new[] { "Źródła",                                                 "Sources" } },
            { "Src_Direct",         new[] { "strona wydawcy",                                         "vendor site" } },

            { "InstallingTitle",    new[] { "Instalowanie",                                           "Installing" } },
            { "Overall",            new[] { "{0} z {1} ukończono",                                    "{0} of {1} completed" } },
            { "St_Queued",          new[] { "W kolejce",                                              "Queued" } },
            { "St_Resolving",       new[] { "Sprawdzanie najnowszej wersji…",                         "Looking up the latest version…" } },
            { "St_Downloading",     new[] { "Pobieranie… {0}%",                                       "Downloading… {0}%" } },
            { "St_DownloadingNoSize", new[] { "Pobieranie…",                                          "Downloading…" } },
            { "St_Waiting",         new[] { "Pobrano · czeka na instalację",                          "Downloaded · waiting to install" } },
            { "St_Installing",      new[] { "Instalowanie ({0})…",                                    "Installing ({0})…" } },
            { "St_Fallback",        new[] { "{0} — błąd, próbuję: {1}…",                              "{0} failed, trying {1}…" } },
            { "St_Done",            new[] { "Zainstalowano ({0})",                                    "Installed ({0})" } },
            { "St_Failed",          new[] { "Błąd: {0}",                                              "Failed: {0}" } },
            { "AllSourcesFailed",   new[] { "żadne źródło nie zadziałało",                            "no source worked" } },
            { "NoSource",           new[] { "brak źródeł",                                            "no sources" } },
            { "RebootShort",        new[] { "wymaga restartu",                                        "restart needed" } },
            { "EnsureWinget",       new[] { "Instalowanie winget…",                                   "Installing winget…" } },
            { "EnsureChoco",        new[] { "Instalowanie Chocolatey…",                               "Installing Chocolatey…" } },
            { "Finished",           new[] { "Gotowe",                                                 "All done" } },
            { "FinishedWithErrors", new[] { "Zakończono z błędami",                                   "Finished with errors" } },
            { "Summary",            new[] { "Zainstalowano {0} z {1}. Błędy: {2}.",                   "Installed {0} of {1}. Errors: {2}." } },
            { "RebootNeeded",       new[] { "Aby dokończyć, uruchom ponownie komputer.",              "Restart your PC to finish." } },
            { "Restart",            new[] { "Uruchom ponownie",                                       "Restart now" } },
            { "OpenLog",            new[] { "Otwórz log",                                             "Open log" } },
            { "Close",              new[] { "Zamknij",                                                "Close" } },
            { "Back",               new[] { "Wróć do listy",                                          "Back to list" } },
            { "Cancel",             new[] { "Anuluj",                                                 "Cancel" } },
            { "Cancelled",          new[] { "Anulowano",                                              "Cancelled" } },
            { "ConfirmCancel",      new[] { "Przerwać instalację? Bieżący instalator zostanie zatrzymany.", "Stop installing? The current installer will be terminated." } },

            { "UpdateAvailable",    new[] { "Dostępna jest nowa wersja {0}.",                          "Version {0} is available." } },
            { "UpdateNow",          new[] { "Aktualizuj",                                             "Update" } },
            { "Updating",           new[] { "Aktualizowanie…",                                        "Updating…" } },
            { "UpdateFailed",       new[] { "Aktualizacja nie powiodła się: {0}",                     "Update failed: {0}" } },

            { "Theme",              new[] { "Motyw",                                                  "Theme" } },
            { "ThemeAuto",          new[] { "Jak w systemie",                                         "Use system setting" } },
            { "ThemeLight",         new[] { "Jasny",                                                  "Light" } },
            { "ThemeDark",          new[] { "Ciemny",                                                 "Dark" } },
            { "Language",           new[] { "Język",                                                  "Language" } },
            { "Catalog_online",     new[] { "Katalog: aktualny (podpis OK)",                          "Catalog: up to date (signature OK)" } },
            { "Catalog_cache",      new[] { "Katalog: kopia lokalna (offline)",                       "Catalog: local copy (offline)" } },
            { "Catalog_builtin",    new[] { "Katalog: wbudowany",                                     "Catalog: built-in" } },
            { "Loading",            new[] { "Wczytywanie katalogu…",                                  "Loading catalog…" } },
            { "UnattendedTitle",    new[] { "Instalacja pakietu ({0} aplikacji)",                     "Installing bundle ({0} apps)" } },
        };

        public string this[string key] { get { return T(key); } }

        public static string T(string key, params object[] args)
        {
            string[] v;
            if (!S.TryGetValue(key, out v)) return key;
            var s = Lang == "pl" ? v[0] : v[1];
            return args != null && args.Length > 0 ? string.Format(s, args) : s;
        }

        public static void SetLanguage(string lang)
        {
            if (string.IsNullOrEmpty(lang) || lang == "auto")
                lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pl" ? "pl" : "en";
            Lang = lang == "pl" ? "pl" : "en";
            var h = I.PropertyChanged;
            if (h != null) h(I, new PropertyChangedEventArgs("Item[]"));
            var c = Changed;
            if (c != null) c();
        }

        public static event Action Changed;
        public event PropertyChangedEventHandler PropertyChanged;

        static Loc() { SetLanguage(null); }
    }
}
