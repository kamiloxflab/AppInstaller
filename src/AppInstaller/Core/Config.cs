using System;
using System.IO;
using System.Reflection;

namespace AppInstaller.Core
{
    /// <summary>
    /// Jedyne miejsce, które trzeba zmienić po sforkowaniu repozytorium.
    /// The only place you need to edit after forking the repository.
    /// </summary>
    public static class Config
    {
        // >>> ZMIEŃ / CHANGE: adres GitHub Pages (bez własnej domeny), zakończony "/".
        public const string BaseUrl = "https://kamiloxflab.github.io/AppInstaller/";

        public const string CatalogFile = "apps.json";
        public const string UpdateFile = "update.json";
        public const string SignatureSuffix = ".sig";

        /// <summary>Hosty, z których wolno pobrać nową wersję programu (obrona w głąb; integralność gwarantuje podpis + SHA-256).</summary>
        public static readonly string[] AllowedUpdateHosts =
        {
            "github.com",
            "objects.githubusercontent.com",
            "release-assets.githubusercontent.com",
            new Uri(BaseUrl).Host
        };

        public const int MaxParallelDownloads = 4;
        public static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(45);

        public static Version AppVersion
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        public static string ExePath
        {
            get { return System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName; }
        }

        /// <summary>%ProgramData%\AppInstaller — cache katalogu, pobrane instalatory, logi.</summary>
        public static string DataDir
        {
            get { return Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AppInstaller")); }
        }

        /// <summary>%LocalAppData%\AppInstaller — ustawienia użytkownika, ikony.</summary>
        public static string UserDir
        {
            get { return Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppInstaller")); }
        }

        public static string Ensure(string dir)
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
