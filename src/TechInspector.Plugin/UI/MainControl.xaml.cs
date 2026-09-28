using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MahApps.Metro.IconPacks;
using WoteverLocalization;

namespace TechInspector.Plugin.UI
{
    /// <summary>The plugin's page: Overview, Settings, Triple screens, Backups and About, with the changes bar under every tab.</summary>
    public partial class MainControl : UserControl
    {
        private readonly TechInspectorPlugin _plugin;
        private Workspace _ws;
        private DispatcherTimer _verdictTimer;
        private bool _hasVerdict;

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
            // The verdict follows every change, a little behind so typing doesn't rerun the checks per key.
            _verdictTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _verdictTimer.Tick += (s, e) => { _verdictTimer.Stop(); UpdateVerdict(); };
            void Soon() { _verdictTimer.Stop(); _verdictTimer.Start(); }
            _ws.Reloaded += (s, e) => Soon();
            _ws.DraftsChanged += (s, e) => Soon();
            _ws.IgnoresChanged += (s, e) => Soon();
            _ws.HealthChanged += (s, e) => Soon();
            SettingsView.Attach(_ws);
            OverviewView.Attach(_ws);
            TriplesView.Attach(_ws);
            BackupsView.Attach(_ws);
            Bar.Attach(_ws);
            UpdateVerdict();
        }

        /// <summary>The header's one-line verdict: what the Overview lists to look at, or all clear.</summary>
        private void UpdateVerdict()
        {
            try
            {
                var report = _ws.Health();
                _hasVerdict = report != null;
                ShowVerdict();
                if (report == null) return;
                int n = _ws.LookAtCount(report);
                VerdictIcon.Kind = n == 0 ? PackIconMaterialKind.CheckCircle : PackIconMaterialKind.Alert;
                VerdictIcon.Foreground = (Brush)FindResource(n == 0 ? "OkBrush" : "WarnBrush");
                VerdictText.Text = n == 0
                    ? T("TechInspector_VerdictClear", "Inspection: all clear")
                    : string.Format(T("TechInspector_VerdictLook", "Inspection: {0} to look at"), SettingsPage.Plural(n, "thing", "things"));
            }
            catch (Exception ex) { SimHub.Logging.Current.Error("Tech Inspector: health check for the header failed", ex); }
        }

        private void Verdict_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = OverviewItem;

        /// <summary>The Overview states the verdict itself, so the header line only shows it on the other tabs.</summary>
        private void ShowVerdict() =>
            Verdict.Visibility = !_hasVerdict ? Visibility.Collapsed : Tabs.SelectedItem == OverviewItem ? Visibility.Hidden : Visibility.Visible;

        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, Tabs)) ShowVerdict();
        }

        private void Logo_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => TechInspectorPlugin.OpenUrl(TechInspectorPlugin.ShopUrl);

        private void Shop_Click(object sender, RoutedEventArgs e) => TechInspectorPlugin.OpenUrl(TechInspectorPlugin.ShopUrl);

        private void Donate_Click(object sender, RoutedEventArgs e) => TechInspectorPlugin.OpenUrl(TechInspectorPlugin.DonateUrl);

        private void Contact_Click(object sender, RoutedEventArgs e) => TechInspectorPlugin.OpenUrl("mailto:" + TechInspectorPlugin.ContactEmail);

        private void Source_Click(object sender, RoutedEventArgs e) => TechInspectorPlugin.OpenUrl(TechInspectorPlugin.SourceUrl);

        private void Issues_Click(object sender, RoutedEventArgs e) => TechInspectorPlugin.OpenUrl(TechInspectorPlugin.SourceUrl + "/issues");
    }
}
