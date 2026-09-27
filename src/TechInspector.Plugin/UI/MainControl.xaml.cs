using System;
using System.Windows;
using System.Windows.Controls;
using WoteverLocalization;

namespace TechInspector.Plugin.UI
{
    /// <summary>The plugin's page: Overview, Settings, Triple screens, Backups and About, with the changes bar under every tab.</summary>
    public partial class MainControl : UserControl
    {
        private readonly TechInspectorPlugin _plugin;
        private Workspace _ws;

        public MainControl()
        {
            InitializeComponent();
        }

        public MainControl(TechInspectorPlugin plugin) : this()
        {
            _plugin = plugin;
            VersionText.Text = string.Format(T("TechInspector_VersionLine", "Version {0}"), TechInspectorPlugin.Version);
            TouchList.ItemsSource = new[]
            {
                T("TechInspector_Touch1", "Changes iRacing's files in Documents\\iRacing only while the sim is closed, and only when you write changes or restore a backup."),
                T("TechInspector_Touch2", "Backs up those files before every write. Backups stay in SimHub's data folder."),
                T("TechInspector_Touch3", "Moves telemetry files and hand-made copies to the Recycle Bin only when you ask."),
                T("TechInspector_Touch4", "Never changes the sim or its install folder."),
                T("TechInspector_Touch5", "Doesn't connect to the internet. Links open in your browser only when you click them."),
            };
            Loaded += (s, e) => Start();
        }

        private static string T(string key, string fallback) => SLoc.GetValue(key, fallback);

        /// <summary>Loads the settings once, then hands the shared workspace to each tab and the bar.</summary>
        private void Start()
        {
            if (_ws != null) return;
            _ws = new Workspace(_plugin);
            _ws.TabRequested += tab => Tabs.SelectedItem = tab == "settings" ? SettingsItem : tab == "triples" ? TriplesItem : tab == "backups" ? BackupsItem : tab == "about" ? AboutItem : (object)OverviewItem;
            // iRacing rewrites its files when it closes: read them again (pending changes are kept).
            _plugin.FilesChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() => { try { _ws.Load(); } catch (Exception ex) { SimHub.Logging.Current.Error("Tech Inspector: reload failed", ex); } }));
            try { _ws.Load(); }
            catch (Exception ex) { SimHub.Logging.Current.Error("Tech Inspector: could not load the settings files", ex); }
            SettingsView.Attach(_ws);
            OverviewView.Attach(_ws);
            TriplesView.Attach(_ws);
            BackupsView.Attach(_ws);
            Bar.Attach(_ws);
        }

        private void Logo_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => TechInspectorPlugin.OpenUrl(TechInspectorPlugin.ShopUrl);

        private void Shop_Click(object sender, RoutedEventArgs e) => TechInspectorPlugin.OpenUrl(TechInspectorPlugin.ShopUrl);

        private void Donate_Click(object sender, RoutedEventArgs e) => TechInspectorPlugin.OpenUrl(TechInspectorPlugin.DonateUrl);

        private void Source_Click(object sender, RoutedEventArgs e) => TechInspectorPlugin.OpenUrl(TechInspectorPlugin.SourceUrl);

        private void Issues_Click(object sender, RoutedEventArgs e) => TechInspectorPlugin.OpenUrl(TechInspectorPlugin.SourceUrl + "/issues");
    }
}
