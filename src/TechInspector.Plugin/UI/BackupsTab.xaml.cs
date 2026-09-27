using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SimHub.Plugins.Styles;
using TechInspector.Core.Health;
using TechInspector.Core.IO;

namespace TechInspector.Plugin.UI
{
    /// <summary>
    /// Backups: take one now, turn hand-made copies into backups, and look through the history
    /// with compare and restore. Every restore backs up the current files first.
    /// </summary>
    public partial class BackupsTab : UserControl
    {
        private Workspace _ws;
        private readonly HashSet<string> _open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<StrayCopy> _strays = new List<StrayCopy>();
        private BackupsDone _done;

        public BackupsTab() { InitializeComponent(); }

        public void Attach(Workspace ws)
        {
            _ws = ws;
            _ws.BackupsChanged += (s, e) => RefreshIfShown();
            _ws.Reloaded += (s, e) => RefreshIfShown();
            IsVisibleChanged += (s, e) => { if (IsVisible) Refresh(); };
            Refresh();
        }

        private BackupStore Store => _ws.Plugin.Backups;
        private string Folder => _ws.Plugin.IracingFolder;

        private void RefreshIfShown() { if (IsVisible) Refresh(); }

        public void Refresh()
        {
            if (_ws == null || Store == null) return;
            BackUpButton.IsEnabled = Folder != null;
            NameBox.IsEnabled = Folder != null;
            if (Folder == null) WhatText.Text = "No iRacing folder was found under Documents, so there's nothing to back up.";

            var list = Store.List();
            var rows = list.Select(b => new BackupRow(b)).ToList();
            foreach (var r in rows.Where(r => _open.Contains(r.Backup.Id))) Open(r);
            _open.IntersectWith(rows.Select(r => r.Backup.Id));
            History.ItemsSource = rows;
            EmptyCard.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryCount.Text = rows.Count == 0 ? "" : $"   {SettingsPage.Plural(rows.Count, "backup", "backups")}, {Size(Store.TotalBytes)}";
            FinePrint.Text = $"One is taken before every write; the last {BackupStore.KeepAuto} of those are kept. "
                           + "Backups you take or import stay until you delete them. All are stored in SimHub's data folder, not in your iRacing folder.";

            _strays = Folder == null ? new List<StrayCopy>() : StrayCopy.Find(Folder, Store.ImportedSources()).ToList();
            ShowImportCard();
            ShowDone();
        }

        private void ShowImportCard()
        {
            ImportCard.Visibility = _strays.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_strays.Count == 0) return;
            var byFile = _strays.GroupBy(c => c.OriginalName).OrderByDescending(g => g.Count()).ToList();
            ImportTitle.Text = byFile.Count == 1
                ? $"Your iRacing folder has {SettingsPage.Plural(_strays.Count, "hand-made copy", "hand-made copies")} of {byFile[0].Key}."
                : $"Your iRacing folder has {SettingsPage.Plural(_strays.Count, "hand-made copy", "hand-made copies")} of settings files.";
            var parts = byFile.Select(g => g.Count() > 1 ? $"{g.Key} ({g.Count()})" : g.Key);
            ImportDetail.Text = (byFile.Count > 1 ? string.Join(", ", parts) + ". " : "")
                              + "Import adds each as a backup, dated by the copy's file time. The copies stay where they are.";
            ImportButton.ToolTip = string.Join("\n", _strays.Select(c => $"{c.FileName}   {BackupRow.Stamp(c.Modified)}"));
        }

        private void ShowDone()
        {
            DoneCard.Visibility = _done == null ? Visibility.Collapsed : Visibility.Visible;
            DoneCard.DataContext = null;   // rebind: the result object doesn't raise change events
            DoneCard.DataContext = _done;
            if (_done == null) return;
            DoneIcon.Kind = _done.IsError ? MahApps.Metro.IconPacks.PackIconMaterialKind.AlertCircle : MahApps.Metro.IconPacks.PackIconMaterialKind.CheckCircle;
            DoneIcon.Foreground = _done.IsError ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x73, 0x73))
                                                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7D, 0xDC, 0x6A));
        }

        private void SetDone(BackupsDone done) { _done = done; ShowDone(); }

        private void Fail(string what, Exception ex)
        {
            SimHub.Logging.Current.Error("Tech Inspector: " + what, ex);
            SetDone(new BackupsDone { Title = what[0].ToString().ToUpperInvariant() + what.Substring(1) + ".", Detail = ex.Message, IsError = true });
        }

        private void Open(BackupRow row)
        {
            row.LoadCompare(_ws.Page?.Session, _ws.Plugin.Catalog);
            row.CompareOpen = true;
        }

        private static string Size(long bytes) =>
            bytes >= 1_000_000_000 ? HealthCheck.Gb(bytes)
            : bytes >= 1_000_000 ? $"{bytes / 1_000_000.0:0.0} MB"
            : $"{Math.Max(1, bytes / 1000)} KB";

        private static T Item<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

        /// <summary>Restores need iRacing closed: it rewrites its files when it exits.</summary>
        private static async Task<bool> SimClosed()
        {
            if (!TechInspectorPlugin.IsSimProcessRunning()) return true;
            await SHMessageBox.Show("iRacing is running and rewrites its files when it closes. Close iRacing, then restore.", "Close iRacing first", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        // ── take one now ──

        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { BackUpNow_Click(sender, e); e.Handled = true; }
        }

        private void BackUpNow_Click(object sender, RoutedEventArgs e)
        {
            if (Folder == null) return;
            try
            {
                var name = NameBox.Text?.Trim();
                var b = _ws.BackUpNow(name);
                NameBox.Text = "";
                SetDone(new BackupsDone
                {
                    Title = $"Backup #{b.Number} saved.",
                    Detail = SettingsPage.Plural(b.Files.Count, "file", "files") + (string.IsNullOrEmpty(name) ? "" : $", named “{name}”") + ". Kept until you delete it.",
                });
            }
            catch (Exception ex) { Fail("couldn't take the backup", ex); }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(Store.Root);
            TechInspectorPlugin.OpenPath(Store.Root);
        }

        // ── history ──

        private void Compare_Click(object sender, RoutedEventArgs e)
        {
            var row = Item<BackupRow>(sender);
            if (row == null) return;
            if (row.CompareOpen) { row.CompareOpen = false; _open.Remove(row.Backup.Id); return; }
            try { Open(row); _open.Add(row.Backup.Id); }
            catch (Exception ex) { Fail("couldn't compare the backup", ex); }
        }

        private void KeysMore_Click(object sender, RoutedEventArgs e) => Item<FileDiffRow>(sender)?.ToggleMore();

        private async void Restore_Click(object sender, RoutedEventArgs e)
        {
            var row = Item<BackupRow>(sender);
            if (row == null || !await SimClosed()) return;
            var b = row.Backup;
            var what = b.Kind == BackupKind.Imported
                ? $"Puts back {b.ShortName(b.Files[0])} as it was in {Path.GetFileName(b.SourceFile)}, dated {BackupRow.Stamp(b.Created)}."
                : $"Puts back the {SettingsPage.Plural(b.Files.Count, "file", "files")} in this backup, from {BackupRow.Stamp(b.Created)}.";
            var res = await SHMessageBox.Show(what + " Your current files are backed up first, so you can undo this.",
                                              $"Restore backup #{b.Number}?", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (res != System.Windows.Forms.DialogResult.OK) return;
            DoRestore(b, null);
        }

        private async void RestoreFile_Click(object sender, RoutedEventArgs e)
        {
            var file = Item<FileDiffRow>(sender);
            if (file == null || !await SimClosed()) return;
            var b = file.Owner.Backup;
            var res = await SHMessageBox.Show($"Puts back {file.Name} from backup #{b.Number} ({BackupRow.Stamp(b.Created)}). The current file is backed up first, so you can undo this.",
                                              $"Restore {Path.GetFileName(file.Name)}?", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (res != System.Windows.Forms.DialogResult.OK) return;
            DoRestore(b, new[] { file.Diff.Original });
        }

        private void DoRestore(Backup b, IList<string> only)
        {
            try
            {
                var (before, layout) = _ws.Restore(b, only);
                var count = only?.Count ?? b.Files.Count;
                var files = only != null ? b.ShortName(only[0]) : SettingsPage.Plural(count, "file", "files");
                SetDone(new BackupsDone
                {
                    Title = $"Restored {files} from backup #{b.Number}.",
                    Detail = $"Backup #{before.Number} holds your files from before." + (layout ? " Your saved triple layout now matches the restored file." : ""),
                    ActionText = "Undo",
                    Run = () => UndoRestore(before),
                });
            }
            catch (Exception ex) { Fail("couldn't restore the backup", ex); }
        }

        private async void UndoRestore(Backup before)
        {
            if (!await SimClosed()) return;
            try
            {
                _ws.Restore(before, before.Files.ToList(), "Before undoing a restore");
                SetDone(new BackupsDone { Title = "Undone. Your files are back as they were before the restore." });
            }
            catch (Exception ex) { Fail("couldn't undo the restore", ex); }
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            var row = Item<BackupRow>(sender);
            if (row == null) return;
            var b = row.Backup;
            try
            {
                bool gone = await Task.Run(() => RecycleBin.Send(new[] { b.Folder }).Count == 1);
                _ws.NotifyBackupsChanged();
                SetDone(gone
                    ? new BackupsDone { Title = $"Backup #{b.Number} moved to the Recycle Bin.", ActionText = "Undo", Run = () => UndoDelete(b) }
                    : new BackupsDone { Title = $"Backup #{b.Number} couldn't be moved to the Recycle Bin.", Detail = "A file in it may be open in another program.", IsError = true });
            }
            catch (Exception ex) { Fail("couldn't delete the backup", ex); }
        }

        private async void UndoDelete(Backup b)
        {
            int n = await Task.Run(() => RecycleBin.Restore(new[] { b.Folder }));
            _ws.NotifyBackupsChanged();
            SetDone(n > 0
                ? new BackupsDone { Title = $"Backup #{b.Number} is back." }
                : new BackupsDone { Title = $"Backup #{b.Number} wasn't found in the Recycle Bin.", Detail = "Restore it from the Recycle Bin yourself.", IsError = true });
        }

        // ── hand-made copies ──

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var copies = _strays.ToList();
            if (copies.Count == 0) return;
            try
            {
                int n = _ws.Import(copies);
                SetDone(new BackupsDone
                {
                    Title = $"Added {SettingsPage.Plural(n, "backup", "backups")} from your copies.",
                    Detail = "The copies are still in your iRacing folder.",
                    ActionText = "Move the copies to the Recycle Bin",
                    Run = () => RecycleCopies(copies),
                });
            }
            catch (Exception ex) { Fail("couldn't import the copies", ex); }
        }

        private async void RecycleCopies(List<StrayCopy> copies)
        {
            try
            {
                var moved = await Task.Run(() => RecycleBin.Send(copies.Select(c => c.Path).ToList()));
                SetDone(new BackupsDone
                {
                    Title = $"Moved {SettingsPage.Plural(moved.Count, "copy", "copies")} to the Recycle Bin.",
                    Detail = moved.Count < copies.Count ? $"{copies.Count - moved.Count} couldn't be moved and are still in your iRacing folder." : "Their backups stay in the history.",
                    ActionText = "Undo",
                    Run = () => UndoRecycleCopies(moved),
                });
            }
            catch (Exception ex) { Fail("couldn't move the copies", ex); }
        }

        private async void UndoRecycleCopies(List<string> moved)
        {
            int n = await Task.Run(() => RecycleBin.Restore(moved));
            SetDone(n == moved.Count
                ? new BackupsDone { Title = $"Put {SettingsPage.Plural(n, "copy", "copies")} back in your iRacing folder." }
                : new BackupsDone { Title = $"Put back {n} of {moved.Count} copies.", Detail = "Restore the rest from the Recycle Bin.", IsError = true });
            Refresh();
        }

        private void DoneAction_Click(object sender, RoutedEventArgs e)
        {
            var run = _done?.Run;
            if (run == null) return;
            _done.ActionText = null;   // one click
            ShowDone();
            run();
        }
    }
}
