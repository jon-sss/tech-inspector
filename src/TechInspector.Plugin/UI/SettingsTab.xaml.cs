using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SimHub.Plugins.Styles;
using TechInspector.Core;
using TechInspector.Core.Settings;

namespace TechInspector.Plugin.UI
{
    public partial class SettingsTab : UserControl
    {
        public static readonly RoutedCommand FocusSearch = new RoutedCommand();

        private Workspace _ws;
        private bool _loadingModes;

        public SettingsTab()
        {
            InitializeComponent();
            CommandBindings.Add(new CommandBinding(FocusSearch, (s, e) => { SearchBox.Focus(); SearchBox.SelectAll(); }));
        }

        private SettingsPage Page => _ws?.Page;

        public void Attach(Workspace ws)
        {
            _ws = ws;
            _ws.ConfirmDanger = ConfirmDangerAsync;
            _ws.Reloaded += (s, e) => DataContext = _ws.Page;
            _ws.GoToRequested += GoTo;
            _ws.FilterRequested += f => { if (Page != null) { Page.Query = ""; Page.Filter = f; } };
            LoadModes();
            DataContext = _ws.Page;
        }

        /// <summary>The display modes whose renderer file exists; the choice is remembered.</summary>
        private void LoadModes()
        {
            _loadingModes = true;
            ModeBox.Items.Clear();
            var folder = _ws.Plugin.IracingFolder;
            if (folder != null)
            {
                var names = new[] { ("Monitor", "rendererDX11Monitor.ini"), ("OpenXR", "rendererDX11OpenXR.ini"), ("Oculus", "rendererDX11Oculus.ini"), ("OpenVR", "rendererDX11OpenVR.ini"), ("Template", "rendererDX11.ini") };
                foreach (var (label, file) in names)
                    if (File.Exists(Path.Combine(folder, file)))
                        ModeBox.Items.Add(new ComboBoxItem { Content = $"{label} · {file}", Tag = file });
            }
            var want = _ws.Plugin.Settings.RendererFile;
            ModeBox.SelectedItem = ModeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == want) ?? ModeBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
            _loadingModes = false;
        }

        private string RendererFile => (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? IracingPaths.RendererModes[0];

        /// <summary>Opens a setting's section and tier, then scrolls it into view and focuses its control.</summary>
        public void GoTo(Setting s)
        {
            if (Page == null || s == null) return;
            var target = Page.Session.FindById(s.Id) ?? s;
            Page.Reveal(target);
            var row = Page.RowFor(target);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var element = Find(this, row);
                if (element == null) return;
                element.BringIntoView();
                var control = FindFocusable(element);
                (control ?? element).Focus();
            }), DispatcherPriority.Loaded);
        }

        private static FrameworkElement Find(DependencyObject root, object dataContext)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe && ReferenceEquals(fe.DataContext, dataContext) && fe is ContentPresenter) return fe;
                var found = Find(child, dataContext);
                if (found != null) return found;
            }
            return null;
        }

        private static UIElement FindFocusable(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Control c && c.Focusable && c.IsEnabled && !(c is Button)) return c;
                var found = FindFocusable(child);
                if (found != null) return found;
            }
            return null;
        }

        private async Task<bool> ConfirmDangerAsync(SettingRow row, string value)
        {
            var res = await SHMessageBox.Show(row.DangerText, $"Change {row.Label}?", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            return res == System.Windows.Forms.DialogResult.OK;
        }

        // ── handlers ──

        private async void Mode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingModes || _ws == null) return;
            if (Page != null && Page.PendingCount > 0)
            {
                var res = await SHMessageBox.Show("Switching the graphics file discards the changes you haven't written.", "Discard changes?", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (res != System.Windows.Forms.DialogResult.OK)
                {
                    _loadingModes = true;
                    ModeBox.SelectedItem = ModeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Page.Session.RendererFile);
                    _loadingModes = false;
                    return;
                }
            }
            _ws.SwitchRenderer(RendererFile);
        }

        private void Search_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && SearchBox.Text.Length > 0) { SearchBox.Text = ""; e.Handled = true; }
        }

        private static T Item<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

        private void MoreLink_Click(object sender, RoutedEventArgs e) => Page.ToggleMore(Item<MoreLinkItem>(sender).Card);
        private void ShowHidden_Click(object sender, RoutedEventArgs e) => Page.ShowHidden(Item<SectionFootItem>(sender).Card);
        private void ShowExpert_Click(object sender, RoutedEventArgs e) => Page.ShowExpert = true;
        private void Reset_Click(object sender, RoutedEventArgs e) => Page.Reset(Item<SettingRow>(sender));
        private void Hide_Click(object sender, RoutedEventArgs e) { Page.Hide(Item<SettingRow>(sender)); _ws.Plugin.SaveSettings(); }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            if (_ws.Plugin.IracingFolder != null) TechInspectorPlugin.OpenPath(Path.Combine(_ws.Plugin.IracingFolder, RendererFile));
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_ws.Plugin.IracingFolder != null) TechInspectorPlugin.OpenPath(_ws.Plugin.IracingFolder);
        }
    }
}
