using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using GameReaderCommon;
using MahApps.Metro.IconPacks;
using SimHub.Plugins;
using TechInspector.Core;
using TechInspector.Core.Catalog;
using TechInspector.Core.IO;

namespace TechInspector.Plugin
{
    [PluginName("Tech Inspector")]
    [PluginAuthor("Simpatico Sim Shop")]
    [PluginDescription("Settings, triple screens and health checks for iRacing")]
    public class TechInspectorPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public const string SimProcess = "iRacingSim64DX11";
        public const string ShopUrl = "https://simpaticosims.com/";
        public const string DonateUrl = "https://ko-fi.com/simpaticosimshop";
        public const string SourceUrl = "https://github.com/jon-sss/tech-inspector";

        private static ImageSource _icon;
        private bool _simRunning;

        public PluginManager PluginManager { get; set; }

        public static string Version { get; } =
            typeof(TechInspectorPlugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
            ?? typeof(TechInspectorPlugin).Assembly.GetName().Version.ToString(3);

        /// <summary>The iRacing folder under Documents, or null when it isn't found.</summary>
        public string IracingFolder { get; private set; }

        /// <summary>Where iRacing is installed, from its uninstall entry; null when not found.</summary>
        public string InstallFolder { get; private set; }

        /// <summary>The last automatic telemetry cleanup, for the Overview: when, how many files, how many bytes.</summary>
        public (DateTime When, int Files, long Bytes)? LastAutoClean { get; private set; }

        /// <summary>The plugin's own preferences (hidden settings, ignored values, view options).</summary>
        public PluginSettings Settings { get; private set; } = new PluginSettings();

        /// <summary>Backups of iRacing's files, kept in SimHub's data folder (PluginsData\Common\TechInspector\Backups).</summary>
        public BackupStore Backups { get; private set; }

        private SettingsCatalog _catalog;
        /// <summary>The researched catalog, embedded in TechInspector.Core.</summary>
        public SettingsCatalog Catalog => _catalog ?? (_catalog = SettingsCatalog.LoadEmbedded());

        public ImageSource PictureIcon => _icon ?? (_icon = MenuIcon());

        public string LeftMenuTitle => "Tech Inspector";

        /// <summary>
        /// True while the sim itself runs. Changes are only ever written while this is false,
        /// because the sim rewrites its files on exit.
        /// </summary>
        public bool SimRunning => _simRunning || Process.GetProcessesByName(SimProcess).Length > 0;

        public event EventHandler SimStateChanged;

        /// <summary>The authoritative check before any write: is the sim's process alive right now?</summary>
        public static bool IsSimProcessRunning() => Process.GetProcessesByName(SimProcess).Length > 0;

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info($"Tech Inspector {Version} starting");
            Settings = this.ReadCommonSettings("Settings", () => new PluginSettings());
            Backups = new BackupStore(pluginManager.GetCommonStoragePath("TechInspector", "Backups"));
            try
            {
                IracingFolder = IracingPaths.FindDocumentsFolder();
                InstallFolder = FindInstallFolder();
                SimHub.Logging.Current.Info(IracingFolder == null
                    ? "Tech Inspector: no iRacing folder under Documents"
                    : $"Tech Inspector: iRacing folder {IracingFolder}");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("Tech Inspector: could not look for the iRacing folder", ex);
            }

            this.AttachDelegate("Version", () => Version);
            this.AttachDelegate("SimRunning", () => SimRunning);
        }

        /// <summary>On SimHub's critical path: only notes whether iRacing is running.</summary>
        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            bool running = data.GameRunning && string.Equals(data.GameName, "IRacing", StringComparison.OrdinalIgnoreCase);
            if (running == _simRunning) return;
            _simRunning = running;
            SimStateChanged?.Invoke(this, EventArgs.Empty);
            if (!running) System.Threading.Tasks.Task.Run(AfterSimClosed);
        }

        /// <summary>Raised after iRacing closed and rewrote its files, so the page reloads them.</summary>
        public event EventHandler FilesChanged;

        /// <summary>The last time the saved triple layout was put back automatically, and how many settings.</summary>
        public (DateTime When, int Count)? LastGuardRestore { get; private set; }

        /// <summary>
        /// After iRacing closes: wait for the sim to be fully gone (it writes its files on exit), put
        /// back the saved triple layout if iRacing changed it, clean up telemetry if asked, then tell
        /// the page to reload.
        /// </summary>
        private void AfterSimClosed()
        {
            System.Threading.Thread.Sleep(10000);
            if (IsSimProcessRunning() || IracingFolder == null) return;
            GuardLayout();
            if (Settings.TelemetryAutoCleanDays > 0) AutoCleanTelemetry();
            FilesChanged?.Invoke(this, EventArgs.Empty);
        }

        private void GuardLayout()
        {
            try
            {
                if (!Settings.GuardAutoRestore || Settings.SavedLayout.Count == 0) return;
                var session = TechInspector.Core.Settings.SettingsSession.Load(IracingFolder, TechInspector.Core.Triples.LayoutGuard.MonitorFile, Catalog, Settings.IgnoredValues);
                int n = TechInspector.Core.Triples.LayoutGuard.StageRestore(session, Settings.SavedLayout);
                if (n == 0) return;
                session.Write(Backups, IsSimProcessRunning, "Before putting back the triple layout");
                LastGuardRestore = (DateTime.Now, n);
                SimHub.Logging.Current.Info($"Tech Inspector: iRacing changed {n} triple layout settings; put them back");
            }
            catch (Exception ex) { SimHub.Logging.Current.Error("Tech Inspector: could not restore the triple layout", ex); }
        }

        /// <summary>Telemetry files older than the chosen age go to the Recycle Bin, keeping the newest file.</summary>
        private void AutoCleanTelemetry()
        {
            try
            {
                var folder = TechInspector.Core.Health.TelemetryFolder.Scan(IracingFolder);
                var pick = folder.OlderThan(Settings.TelemetryAutoCleanDays, DateTime.Now);
                if (pick.Count == 0) return;
                var moved = RecycleBin.Send(pick.Select(f => f.Path).ToList());
                LastAutoClean = (DateTime.Now, moved.Count, pick.Where(f => moved.Contains(f.Path)).Sum(f => f.Bytes));
                SimHub.Logging.Current.Info($"Tech Inspector: moved {moved.Count} telemetry files older than {Settings.TelemetryAutoCleanDays} days to the Recycle Bin");
            }
            catch (Exception ex) { SimHub.Logging.Current.Error("Tech Inspector: automatic telemetry cleanup failed", ex); }
        }

        /// <summary>iRacing's install folder, from the "iRacing.com Race Simulation" uninstall entry.</summary>
        private static string FindInstallFolder()
        {
            foreach (var root in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root))
                    {
                        if (key == null) continue;
                        foreach (var name in key.GetSubKeyNames())
                            using (var app = key.OpenSubKey(name))
                                if ((app?.GetValue("DisplayName") as string)?.StartsWith("iRacing.com Race Simulation", StringComparison.OrdinalIgnoreCase) == true)
                                    return app.GetValue("InstallLocation") as string;
                    }
                }
                catch (Exception) { /* not readable: try the next place */ }
            }
            var fallback = @"C:\Program Files (x86)\iRacing";
            return System.IO.Directory.Exists(fallback) ? fallback : null;
        }

        public void End(PluginManager pluginManager) => SaveSettings();

        public void SaveSettings() => this.SaveCommonSettings("Settings", Settings);

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager) => new UI.MainControl(this);

        /// <summary>
        /// Opens a web page in the user's browser. SimHub often runs as administrator; handing the
        /// address to Explorer opens the browser as the signed-in user instead of elevated.
        /// </summary>
        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + url + "\"") { UseShellExecute = false }); }
            catch (Exception ex) { SimHub.Logging.Current.Error("Tech Inspector: could not open " + url, ex); }
        }

        /// <summary>Opens a file or folder in Explorer, as the signed-in user.</summary>
        public static void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", System.IO.File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + path + "\"") { UseShellExecute = false }); }
            catch (Exception ex) { SimHub.Logging.Current.Error("Tech Inspector: could not open " + path, ex); }
        }

        /// <summary>The Material "clipboard-check-outline" glyph, black on transparent like SimHub's own menu icons.</summary>
        private static ImageSource MenuIcon()
        {
            var data = PackIconMaterialDataFactory.DataIndex.Value[PackIconMaterialKind.ClipboardCheckOutline];
            var drawing = new GeometryDrawing(Brushes.Black, null, Geometry.Parse(data));
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }
    }
}
