using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SimHub.Plugins.Styles;
using TechInspector.Core.Health;
using TechInspector.Core.Settings;

namespace TechInspector.Plugin.UI
{
    /// <summary>
    /// The health overview: a banner with the headline and status, then what to look at (the
    /// unusual-values report, telemetry cleanup, and other findings, each ignorable), then the
    /// checks that passed.
    /// </summary>
    public partial class OverviewTab : UserControl
    {
        private const int UnusualShown = 5;
        private static readonly string[] Months = CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames;

        private Workspace _ws;
        private bool _unusualAll, _ignoredOpen;
        private TelemetryCard _telemetry;
        private TelemetryDoneCard _telemetryDone;

        public OverviewTab() { InitializeComponent(); }

        public void Attach(Workspace ws)
        {
            _ws = ws;
            _ws.Reloaded += (s, e) => RefreshIfShown();
            _ws.DraftsChanged += (s, e) => RefreshIfShown();
            _ws.IgnoresChanged += (s, e) => RefreshIfShown();
            _ws.LayoutChanged += (s, e) => RefreshIfShown();
            _ws.Plugin.SimStateChanged += (s, e) => Dispatcher.BeginInvoke(new Action(RefreshIfShown));
            IsVisibleChanged += (s, e) => { if (IsVisible) { _telemetry = null; Refresh(); } };
            Refresh();
        }

        private SettingsSession Session => _ws?.Page?.Session;
        private PluginSettings Prefs => _ws.Plugin.Settings;

        /// <summary>Only work while the Overview is on screen; it refreshes when shown.</summary>
        private void RefreshIfShown() { if (IsVisible) Refresh(); }

        public void Refresh()
        {
            if (_ws == null) return;
            var plugin = _ws.Plugin;
            var status = new List<StatusLine>
            {
                TechInspectorPlugin.IsSimProcessRunning()
                    ? StatusLine.Warn("iRacing is running. Changes wait until it closes.")
                    : StatusLine.Ok("iRacing is closed. Changes are written right away."),
            };
            var last = plugin.Backups?.List().FirstOrDefault();
            status.Add(StatusLine.Ok(last == null
                ? "No backups yet. One is taken before every write."
                : $"Last backup: {When(last.Created)} (#{last.Number})."));
            if (Session != null && string.Equals(Session.RendererFile, Core.Triples.LayoutGuard.MonitorFile, StringComparison.OrdinalIgnoreCase))
            {
                var saved = plugin.Settings.SavedLayout;
                int wanted = (int)(Session.Find(Core.Triples.LayoutGuard.MonitorFile, "MonitorSetup", "NumMonitors")?.Draft == "3" ? 3 : 1);
                if (saved.Count > 0 && Core.Triples.LayoutGuard.Differences(Session, saved).Count > 0)
                    status.Add(StatusLine.Warn("iRacing changed your triple layout. Restore it on the Triple screens tab."));
                else if (saved.Count > 0)
                    status.Add(StatusLine.Ok("Triple layout matches your saved layout."));
                else if (wanted == 3)
                    status.Add(StatusLine.Warn("No saved triple layout yet. Save it on the Triple screens tab."));
            }
            if (plugin.LastGuardRestore is var gr && gr.HasValue)
            {
                var what = SettingsPage.Plural(gr.Value.Count, "triple layout setting", "triple layout settings");
                status.Add(StatusLine.Ok($"Put back {what} iRacing changed at {gr.Value.When:HH:mm}."));
            }
            if (plugin.LastAutoClean is var ac && ac.HasValue)
                status.Add(StatusLine.Ok($"Telemetry cleanup moved {SettingsPage.Plural(ac.Value.Files, "file", "files")} ({HealthCheck.Gb(ac.Value.Bytes)}) to the Recycle Bin when iRacing closed at {ac.Value.When:HH:mm}."));
            StatusList.ItemsSource = status;

            if (Session == null)
            {
                Headline.Text = "No iRacing folder found under Documents.";
                Cards.ItemsSource = null;
                return;
            }

            var telemetryFolder = _telemetry?.Folder ?? TelemetryFolder.Scan(plugin.IracingFolder);
            var report = HealthCheck.Run(Session, new HealthInputs
            {
                DocumentsFolder = plugin.IracingFolder,
                InstallFolder = plugin.InstallFolder,
                Telemetry = telemetryFolder,
            });

            var cards = new List<object>();
            var ignoredItems = new List<IgnoredItem>();

            if (report.Unusual.Count > 0)
            {
                var shown = _unusualAll ? report.Unusual : report.Unusual.Take(UnusualShown).ToList();
                cards.Add(new UnusualCard
                {
                    Title = SettingsPage.Plural(report.Unusual.Count, "setting has an unusual value", "settings have unusual values"),
                    Rows = shown.Select(s => new UnusualRow
                    {
                        Setting = s,
                        CanReset = !s.IsReadOnly && s.Def.Default.HasValue,
                        ResetTip = s.Def.Default.HasValue ? $"Sets it back to {s.Show(Session.DefaultOf(s))}. Written with your other changes." : null,
                    }).ToList(),
                    HasMore = report.Unusual.Count > UnusualShown,
                    MoreText = _unusualAll ? "Show fewer" : $"Show all {report.Unusual.Count}",
                });
            }

            if (_telemetryDone != null) cards.Add(_telemetryDone);
            else if (report.TelemetryLarge && !Prefs.IgnoredFindings.Contains("telemetry"))
                cards.Add(_telemetry = BuildTelemetryCard(telemetryFolder, _telemetry));
            else if (report.TelemetryLarge)
                ignoredItems.Add(new IgnoredItem { Id = "telemetry", IsFinding = true, Label = $"Telemetry logging is using {HealthCheck.Gb(telemetryFolder.TotalBytes)}", Sub = "Finding" });

            foreach (var f in report.Findings)
            {
                if (Prefs.IgnoredFindings.Contains(f.Id)) ignoredItems.Add(new IgnoredItem { Id = f.Id, IsFinding = true, Label = f.Title, Sub = "Finding" });
                else cards.Add(new FindingCard { Finding = f });
            }

            // Values the user accepted, still listed so they can be un-ignored.
            foreach (var kv in Prefs.IgnoredValues)
            {
                var s = Session.FindById(kv.Key);
                if (s == null) continue;
                ignoredItems.Add(new IgnoredItem
                {
                    Id = kv.Key, Label = s.Label, Sub = s.SectionInfo.Name, Value = s.Show(kv.Value),
                    Note = Setting.SameValue(kv.Value, s.Draft) ? "Accepted at this value." : "Value changed since, so it's checked again.",
                });
            }

            int n = cards.Count(c => !(c is TelemetryDoneCard));
            Headline.Text = n == 0 ? "Nothing to look at. Everything checks out." : $"{SettingsPage.Plural(n, "thing", "things")} to look at. Everything else checks out.";
            LookAtTitle.Visibility = cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Cards.ItemsSource = cards;

            IgnoredLine.Visibility = ignoredItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            IgnoredText.Text = SettingsPage.Plural(ignoredItems.Count, "item ignored", "items ignored") + " ·";
            IgnoredToggle.Content = _ignoredOpen ? "Hide" : "Show";
            IgnoredList.ItemsSource = ignoredItems;
            IgnoredList.Visibility = _ignoredOpen && ignoredItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            PassedCount.Text = "  " + report.Passed.Count;
            PassedList.ItemsSource = report.Passed;
            AlsoLine.Visibility = report.InertCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            InertLink.Content = SettingsPage.Plural(report.InertCount, "setting has", "settings have") + " no effect right now";
        }

        private TelemetryCard BuildTelemetryCard(TelemetryFolder folder, TelemetryCard previous)
        {
            var now = DateTime.Now;
            string Count(int days)
            {
                var pick = folder.OlderThan(days, now);
                return pick.Count == 0 ? "none" : $"{SettingsPage.Plural(pick.Count, "file", "files")}, {HealthCheck.Gb(pick.Sum(f => f.Bytes))}";
            }
            var choices = new[] { (7, "Files older than a week"), (30, "Files older than 30 days"), (90, "Files older than 90 days"), (0, "All files except the newest") }
                .Select(c => new AgeChoice { Days = c.Item1, Label = $"{c.Item2} · {Count(c.Item1)}", IsEnabled = folder.OlderThan(c.Item1, now).Count > 0 })
                .ToList();
            var oldest = folder.Oldest;
            var card = new TelemetryCard
            {
                Folder = folder,
                Title = $"Telemetry logging is using {HealthCheck.Gb(folder.TotalBytes)}",
                Detail = $"{folder.Files.Count} .ibt files" + (oldest.HasValue ? $" since {oldest.Value.Day} {Months[oldest.Value.Month - 1]}" : "") + ". SimHub doesn't use them.",
                Choices = choices,
                AutoClean = Prefs.TelemetryAutoCleanDays > 0,
                PanelOpen = previous?.PanelOpen ?? false,
            };
            var keep = previous?.Selected?.Days ?? (Prefs.TelemetryAutoCleanDays > 0 ? Prefs.TelemetryAutoCleanDays : 30);
            card.Selected = choices.FirstOrDefault(c => c.Days == keep && c.IsEnabled) ?? choices.FirstOrDefault(c => c.IsEnabled);
            return card;
        }

        private static string When(DateTime t)
        {
            var today = DateTime.Today;
            var day = t.Date == today ? "today" : t.Date == today.AddDays(-1) ? "yesterday" : $"{t.Day} {Months[t.Month - 1]}";
            return $"{day} at {t:HH:mm}";
        }

        private static T Item<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

        // ── unusual values ──

        private void ReviewUnusual_Click(object sender, RoutedEventArgs e) => _ws.ShowFilter(SettingsPage.FilterUnusual);
        private void UnusualMore_Click(object sender, RoutedEventArgs e) { _unusualAll = !_unusualAll; Refresh(); }
        private void GoToUnusual_Click(object sender, RoutedEventArgs e) => _ws.GoTo(Item<UnusualRow>(sender).Setting);
        private void IgnoreUnusual_Click(object sender, RoutedEventArgs e) => _ws.Ignore(Item<UnusualRow>(sender).Setting);

        private void ResetUnusual_Click(object sender, RoutedEventArgs e)
        {
            var s = Item<UnusualRow>(sender).Setting;
            var row = _ws.Page.RowFor(s);
            if (row != null) _ws.Page.Reset(row);   // a normal pending change: the changes bar shows it
        }

        // ── findings ──

        private void FindingSetting_Click(object sender, RoutedEventArgs e) => _ws.GoTo(Item<FindingCard>(sender).Finding.Setting);
        private void IgnoreFinding_Click(object sender, RoutedEventArgs e) => _ws.IgnoreFinding((string)((FrameworkElement)sender).Tag);
        private void Inert_Click(object sender, RoutedEventArgs e) => _ws.ShowFilter(SettingsPage.FilterInert);
        private void IgnoredToggle_Click(object sender, RoutedEventArgs e) { _ignoredOpen = !_ignoredOpen; Refresh(); }

        private void Unignore_Click(object sender, RoutedEventArgs e)
        {
            var item = Item<IgnoredItem>(sender);
            if (item.IsFinding) _ws.UnignoreFinding(item.Id);
            else _ws.Unignore(item.Id);
        }

        // ── telemetry ──

        private void TelemetryToggle_Click(object sender, RoutedEventArgs e) { if (_telemetry != null) _telemetry.PanelOpen = !_telemetry.PanelOpen; }
        private void TelemetrySetting_Click(object sender, RoutedEventArgs e) => _ws.GoTo(Session?.Find(Core.IracingPaths.App, "Misc", "irsdkAutoLogDisk"));
        private void TelemetryFolder_Click(object sender, RoutedEventArgs e) => TechInspectorPlugin.OpenPath(System.IO.Path.Combine(_ws.Plugin.IracingFolder, "telemetry"));

        private async void TelemetryMove_Click(object sender, RoutedEventArgs e)
        {
            var card = _telemetry;
            if (card == null || !card.CanMove) return;
            var pick = card.Picked;
            var days = card.Selected.Days;
            card.Busy = true;
            List<string> moved;
            try { moved = await Task.Run(() => RecycleBin.Send(pick.Select(f => f.Path).ToList())); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("Tech Inspector: telemetry cleanup failed", ex);
                card.Busy = false;
                await SHMessageBox.Show("Couldn't move the files to the Recycle Bin: " + ex.Message, "Telemetry cleanup", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            Prefs.TelemetryAutoCleanDays = card.AutoClean ? Math.Max(days, 1) : 0;
            _ws.Plugin.SaveSettings();
            long bytes = pick.Where(f => moved.Contains(f.Path)).Sum(f => f.Bytes);
            var after = TelemetryFolder.Scan(_ws.Plugin.IracingFolder);
            _telemetryDone = new TelemetryDoneCard
            {
                Title = $"Moved {SettingsPage.Plural(moved.Count, "file", "files")} ({HealthCheck.Gb(bytes)}) to the Recycle Bin",
                Detail = $"The telemetry folder is now {HealthCheck.Gb(after.TotalBytes)}." +
                         (card.AutoClean ? (days > 0 ? $" Files older than {days} days are moved each time iRacing closes." : " Each time iRacing closes, files from earlier sessions are moved.") : ""),
                Moved = moved,
            };
            _telemetry = null;
            Refresh();
        }

        private async void TelemetryUndo_Click(object sender, RoutedEventArgs e)
        {
            var done = _telemetryDone;
            if (done?.Moved == null) return;
            int restored;
            try { restored = RecycleBin.Restore(done.Moved); }
            catch (Exception ex) { SimHub.Logging.Current.Error("Tech Inspector: restore failed", ex); restored = 0; }
            if (restored < done.Moved.Count)
                await SHMessageBox.Show($"Restored {restored} of {done.Moved.Count} files. Restore the rest from the Recycle Bin.", "Telemetry cleanup", MessageBoxButton.OK, MessageBoxImage.Information);
            _telemetryDone = null;
            _telemetry = null;
            Refresh();
        }
    }
}
