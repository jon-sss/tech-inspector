using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SimHub.Plugins.Styles;

namespace TechInspector.Plugin.UI
{
    /// <summary>
    /// The bar at the bottom of every tab while something is unwritten: review, discard, write
    /// (or queue while iRacing runs), then the result with Undo.
    /// </summary>
    public partial class ChangesBar : UserControl
    {
        private enum BarState { Hidden, Pending, Queued, Written, Undone, Error }

        private Workspace _ws;
        private BarState _bar;
        private bool _reviewOpen;
        private int _closedTicks;
        private string _error;
        private readonly DispatcherTimer _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        private readonly DispatcherTimer _fade = new DispatcherTimer();

        public ChangesBar()
        {
            InitializeComponent();
            _watch.Tick += Watch_Tick;
            _fade.Tick += (s, e) => { _fade.Stop(); if (_bar == BarState.Written || _bar == BarState.Undone) { _bar = BarState.Hidden; UpdateBar(); } };
            Unloaded += (s, e) => _watch.Stop();
            Loaded += (s, e) => { if (_ws != null) _watch.Start(); };
        }

        public void Attach(Workspace ws)
        {
            _ws = ws;
            _ws.DraftsChanged += (s, e) =>
            {
                if (_bar == BarState.Written || _bar == BarState.Undone || _bar == BarState.Error) _bar = BarState.Pending;
                if (_reviewOpen) ReviewList.ItemsSource = _ws.Page?.ReviewItems;
                UpdateBar();
            };
            _ws.Plugin.SimStateChanged += (s, e) => Dispatcher.BeginInvoke(new Action(UpdateBar));
            _watch.Start();
            UpdateBar();
        }

        private void UpdateBar()
        {
            int n = _ws?.Page?.PendingCount ?? 0;
            if (n == 0 && (_bar == BarState.Pending || _bar == BarState.Queued || _bar == BarState.Error)) _bar = BarState.Hidden;
            if (n > 0 && _bar == BarState.Hidden) _bar = BarState.Pending;
            bool running = TechInspectorPlugin.IsSimProcessRunning();

            Visibility = _bar == BarState.Hidden ? Visibility.Collapsed : Visibility.Visible;
            BarDot.Visibility = Show(_bar == BarState.Pending || _bar == BarState.Queued);
            BarDone.Visibility = Show(_bar == BarState.Written || _bar == BarState.Undone);
            BarError.Visibility = Show(_bar == BarState.Error);
            ReviewLink.Visibility = Show(_bar == BarState.Pending || _bar == BarState.Queued || _bar == BarState.Error);
            CancelLink.Visibility = Show(_bar == BarState.Queued);
            UndoLink.Visibility = Show(_bar == BarState.Written);
            BarButtons.Visibility = Show(_bar == BarState.Pending || _bar == BarState.Queued || _bar == BarState.Error);
            WriteButton.Visibility = Show(_bar != BarState.Queued);
            ReviewPanel.Visibility = Show(_reviewOpen && ReviewLink.Visibility == Visibility.Visible);
            ReviewLink.Content = _reviewOpen ? "Hide list" : "Review";

            switch (_bar)
            {
                case BarState.Pending:
                    BarTitle.Text = SettingsPage.Plural(n, "change not written", "changes not written");
                    BarNote.Text = running ? "iRacing is running. They'll be written when it closes." : "A backup is taken first.";
                    WriteButton.Content = running ? "Write when iRacing closes" : "Back up and write";
                    break;
                case BarState.Queued:
                    BarTitle.Text = SettingsPage.Plural(n, "change", "changes") + " queued";
                    BarNote.Text = "They'll be written when iRacing closes.";
                    break;
                case BarState.Written:
                    var w = _ws.LastWrite;
                    BarTitle.Text = w == null ? "Written." : "Written to " + JoinFiles(w.Files) + ".";
                    BarNote.Text = $"Backup #{_ws.LastBackupNumber} saved first." +
                                   (w != null && w.Skipped.Count > 0 ? $" {SettingsPage.Plural(w.Skipped.Count, "setting was", "settings were")} skipped: iRacing had removed them." : "");
                    break;
                case BarState.Undone:
                    BarTitle.Text = "Undone. The files are back as they were.";
                    BarNote.Text = "The written version was backed up too.";
                    break;
                case BarState.Error:
                    BarTitle.Text = "Couldn't write the changes.";
                    BarNote.Text = _error;
                    WriteButton.Content = "Try again";
                    break;
            }
        }

        private static Visibility Show(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

        private static string JoinFiles(IList<string> files) =>
            files.Count <= 1 ? string.Join("", files) : string.Join(", ", files.Take(files.Count - 1)) + " and " + files.Last();

        /// <summary>While changes are queued, write them once the sim has been gone for two checks in a row.</summary>
        private void Watch_Tick(object sender, EventArgs e)
        {
            if (_bar == BarState.Queued)
            {
                if (TechInspectorPlugin.IsSimProcessRunning()) _closedTicks = 0;
                else if (++_closedTicks >= 2) { _closedTicks = 0; DoWrite(); }
            }
            else if (_bar == BarState.Pending) UpdateBar();   // keeps the button text right as iRacing starts or stops
        }

        private void DoWrite()
        {
            try
            {
                _ws.Write();
                _bar = BarState.Written;
                _reviewOpen = false;
                Fade(TimeSpan.FromSeconds(20));
            }
            catch (InvalidOperationException) when (TechInspectorPlugin.IsSimProcessRunning())
            {
                _bar = BarState.Queued;   // iRacing started in the meantime: wait for it again
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("Tech Inspector: write failed", ex);
                _error = ex.Message;
                _bar = BarState.Error;
            }
            UpdateBar();
        }

        private void Fade(TimeSpan after) { _fade.Stop(); _fade.Interval = after; _fade.Start(); }

        private void Write_Click(object sender, RoutedEventArgs e)
        {
            if (TechInspectorPlugin.IsSimProcessRunning()) { _bar = BarState.Queued; _closedTicks = 0; UpdateBar(); }
            else DoWrite();
        }

        private void CancelQueue_Click(object sender, RoutedEventArgs e) { _bar = BarState.Pending; UpdateBar(); }

        private void Review_Click(object sender, RoutedEventArgs e)
        {
            _reviewOpen = !_reviewOpen;
            if (_reviewOpen) ReviewList.ItemsSource = _ws.Page?.ReviewItems;
            UpdateBar();
        }

        private void Discard_Click(object sender, RoutedEventArgs e)
        {
            _ws.Page?.DiscardAll();
            _bar = BarState.Hidden;
            _reviewOpen = false;
            UpdateBar();
        }

        private async void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (TechInspectorPlugin.IsSimProcessRunning())
            {
                await SHMessageBox.Show("iRacing is running and rewrites its files when it closes. Close iRacing, then undo.", "Close iRacing first", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                _ws.Undo();
                _bar = BarState.Undone;
                Fade(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("Tech Inspector: undo failed", ex);
                _error = "Undo failed: " + ex.Message;
                _bar = BarState.Error;
            }
            UpdateBar();
        }
    }
}
