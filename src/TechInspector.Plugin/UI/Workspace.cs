using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TechInspector.Core;
using TechInspector.Core.Health;
using TechInspector.Core.IO;
using TechInspector.Core.Settings;
using TechInspector.Core.Triples;

namespace TechInspector.Plugin.UI
{
    /// <summary>
    /// What every tab shares: the loaded settings and their pending changes, the last write (for
    /// Undo), and navigation between tabs. The changes bar, Settings and Overview all work
    /// through this, so a change made on one tab shows on the others.
    /// </summary>
    public sealed class Workspace
    {
        public Workspace(TechInspectorPlugin plugin) { Plugin = plugin; }

        public TechInspectorPlugin Plugin { get; }
        public SettingsPage Page { get; private set; }
        public WriteResult LastWrite { get; private set; }
        public int LastBackupNumber { get; private set; }

        /// <summary>A new page after loading, writing or undoing.</summary>
        public event EventHandler Reloaded;
        /// <summary>Pending values changed.</summary>
        public event EventHandler DraftsChanged;
        /// <summary>Ignored values or findings changed.</summary>
        public event EventHandler IgnoresChanged;
        /// <summary>Another tab asks to show a setting, a filter, or a tab.</summary>
        public event Action<Setting> GoToRequested;
        public event Action<string> FilterRequested;
        public event Action<string> TabRequested;

        /// <summary>Asks before a risky change; set by the page so it can show SimHub's dialog.</summary>
        public Func<SettingRow, string, Task<bool>> ConfirmDanger { get; set; }

        /// <summary>
        /// Reads the files again, keeping the view (search, filter, open sections) and, unless told
        /// otherwise, any changes not yet written: they're applied again to the fresh values.
        /// </summary>
        public void Load(bool keepDrafts = true)
        {
            _telemetry = null;   // iRacing may have written new telemetry since the last look
            var folder = Plugin.IracingFolder;
            if (folder == null) { Page = null; Reloaded?.Invoke(this, EventArgs.Empty); return; }
            var old = Page;
            var session = SettingsSession.Load(folder, Plugin.Settings.RendererFile, Plugin.Catalog, Plugin.Settings.IgnoredValues);
            var page = new SettingsPage(session, Plugin.Settings.HiddenIds, Plugin.Goals) { ConfirmDanger = (r, v) => ConfirmDanger?.Invoke(r, v) ?? Task.FromResult(true) };
            page.TabRequested += tab => TabRequested?.Invoke(tab);
            page.CopyViewFrom(old);
            if (keepDrafts && old != null)
                foreach (var p in old.Session.Pending)
                {
                    var fresh = session.FindById(p.Id);
                    if (fresh != null) session.SetDraft(fresh, p.Draft);
                }
            page.ShowMore = Plugin.Settings.ShowMore;
            page.ShowExpert = Plugin.Settings.ShowExpert;
            page.ShowIniLines = Plugin.Settings.ShowIniLines;
            page.DraftsChanged += (s, e) => DraftsChanged?.Invoke(this, EventArgs.Empty);
            page.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SettingsPage.ShowMore)) Plugin.Settings.ShowMore = page.ShowMore;
                if (e.PropertyName == nameof(SettingsPage.ShowExpert)) Plugin.Settings.ShowExpert = page.ShowExpert;
                if (e.PropertyName == nameof(SettingsPage.ShowIniLines)) Plugin.Settings.ShowIniLines = page.ShowIniLines;
            };
            page.Rebuild();
            Page = page;
            Reloaded?.Invoke(this, EventArgs.Empty);
        }

        public void SwitchRenderer(string file)
        {
            Plugin.Settings.RendererFile = file;
            Load(keepDrafts: false);
        }

        /// <summary>Backs up, writes the pending changes, and reloads. Throws if iRacing is running.</summary>
        public WriteResult Write()
        {
            var result = Page.Session.Write(Plugin.Backups, TechInspectorPlugin.IsSimProcessRunning);
            LastWrite = result;
            LastBackupNumber = result.Backup?.Number ?? 0;
            // Writing the layout on purpose makes it the layout to guard.
            LayoutUpdated = result.Written.Any(id => id.StartsWith(LayoutGuard.MonitorFile + "|", StringComparison.OrdinalIgnoreCase)
                && LayoutGuard.Keys.Any(k => id.EndsWith("|" + k, StringComparison.OrdinalIgnoreCase)));
            SimHub.Logging.Current.Info($"Tech Inspector: wrote {result.Written.Count} changes to {string.Join(", ", result.Files)}; backup #{result.Backup?.Number}");
            Load();
            if (LayoutUpdated) SaveLayout();
            Plugin.SaveSettings();
            BackupsChanged?.Invoke(this, EventArgs.Empty);
            return result;
        }

        /// <summary>Puts back the files the last write changed, from the backup taken before it.</summary>
        public void Undo()
        {
            if (LastWrite?.Backup == null) return;
            var written = LastWrite.Files.Select(f => Path.Combine(Plugin.IracingFolder, f)).ToList();
            Plugin.Backups.Restore(LastWrite.Backup, written, "Before undoing the last write");
            LastWrite = null;
            AdoptLayout(written);
            Load();
            BackupsChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>A backup was taken, restored, imported or deleted.</summary>
        public event EventHandler BackupsChanged;

        /// <summary>Backs up everything now: the settings files, controls, calibration, cameras and profiles.</summary>
        public Backup BackUpNow(string name)
        {
            var b = Plugin.Backups.Create("Manual backup", IracingPaths.BackupFiles(Plugin.IracingFolder),
                                          baseFolder: Plugin.IracingFolder, kind: BackupKind.Manual, name: name);
            SimHub.Logging.Current.Info($"Tech Inspector: backup #{b.Number} taken ({b.Files.Count} files)");
            BackupsChanged?.Invoke(this, EventArgs.Empty);
            return b;
        }

        /// <summary>
        /// Puts back a backup's files (or only some), after backing up the current ones. Returns
        /// that backup and whether the saved triple layout now follows the restored file.
        /// </summary>
        public (Backup Before, bool LayoutUpdated) Restore(Backup backup, IList<string> only = null, string reason = null)
        {
            if (TechInspectorPlugin.IsSimProcessRunning())
                throw new InvalidOperationException("iRacing is running. It rewrites its files when it closes, so close it first.");
            var before = Plugin.Backups.Restore(backup, only, reason);
            SimHub.Logging.Current.Info($"Tech Inspector: restored backup #{backup.Number} ({only?.Count ?? backup.Files.Count} files); backup #{before.Number} holds the files from before");
            LastWrite = null;
            bool layout = AdoptLayout((IEnumerable<string>)only ?? backup.Files);
            Load();
            BackupsChanged?.Invoke(this, EventArgs.Empty);
            return (before, layout);
        }

        /// <summary>Turns hand-made copies into backups. The copies stay where they are.</summary>
        public int Import(IEnumerable<StrayCopy> copies)
        {
            int n = 0;
            foreach (var c in copies) { Plugin.Backups.Import(c, Plugin.IracingFolder); n++; }
            SimHub.Logging.Current.Info($"Tech Inspector: imported {n} hand-made copies as backups");
            BackupsChanged?.Invoke(this, EventArgs.Empty);
            return n;
        }

        /// <summary>Tells the tabs the backup list changed some other way (a Recycle Bin undo).</summary>
        public void NotifyBackupsChanged() => BackupsChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// After a restore or undo puts a different triple layout back on purpose, that layout
        /// becomes the one to guard; otherwise the guard would undo it when iRacing next closes.
        /// </summary>
        private bool AdoptLayout(IEnumerable<string> restored)
        {
            if (Plugin.Settings.SavedLayout.Count == 0) return false;
            if (!restored.Any(f => string.Equals(Path.GetFileName(f), LayoutGuard.MonitorFile, StringComparison.OrdinalIgnoreCase))) return false;
            var session = SettingsSession.Load(Plugin.IracingFolder, LayoutGuard.MonitorFile, Plugin.Catalog, Plugin.Settings.IgnoredValues);
            if (LayoutGuard.Differences(session, Plugin.Settings.SavedLayout).Count == 0) return false;
            Plugin.Settings.SavedLayout = LayoutGuard.Capture(session);
            Plugin.Settings.SavedLayoutWhen = DateTime.Now;
            Plugin.SaveSettings();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>True when the last write changed the guarded triple layout.</summary>
        public bool LayoutUpdated { get; private set; }

        /// <summary>Takes the monitor file's current triple layout as the one to guard.</summary>
        public void SaveLayout()
        {
            if (Page == null || !string.Equals(Page.Session.RendererFile, LayoutGuard.MonitorFile, StringComparison.OrdinalIgnoreCase)) return;
            Plugin.Settings.SavedLayout = LayoutGuard.Capture(Page.Session);
            Plugin.Settings.SavedLayoutWhen = DateTime.Now;
            Plugin.SaveSettings();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The saved layout or its guard settings changed.</summary>
        public event EventHandler LayoutChanged;

        public void Ignore(Setting s) { Page.Session.Ignore(s); Plugin.SaveSettings(); RefreshRows(); }
        public void Unignore(string id) { Plugin.Settings.IgnoredValues.Remove(id); Plugin.SaveSettings(); RefreshRows(); }
        public void IgnoreFinding(string id) { Plugin.Settings.IgnoredFindings.Add(id); Plugin.SaveSettings(); IgnoresChanged?.Invoke(this, EventArgs.Empty); }
        public void UnignoreFinding(string id) { Plugin.Settings.IgnoredFindings.Remove(id); Plugin.SaveSettings(); IgnoresChanged?.Invoke(this, EventArgs.Empty); }

        private void RefreshRows()
        {
            Page?.RefreshAll();
            IgnoresChanged?.Invoke(this, EventArgs.Empty);
        }

        private TelemetryFolder _telemetry;

        /// <summary>The telemetry folder, scanned once per load.</summary>
        public TelemetryFolder Telemetry => _telemetry ?? (_telemetry = Plugin.IracingFolder == null ? null : TelemetryFolder.Scan(Plugin.IracingFolder));

        /// <summary>The health check over the loaded files; null when there's no iRacing folder.</summary>
        public HealthReport Health() => Page == null ? null : HealthCheck.Run(Page.Session, new HealthInputs
        {
            DocumentsFolder = Plugin.IracingFolder,
            InstallFolder = Plugin.InstallFolder,
            Telemetry = Telemetry,
        });

        /// <summary>
        /// How many things the Overview lists to look at: the unusual values as one, a large
        /// telemetry folder, and each other finding, leaving out the ones the user ignored.
        /// </summary>
        public int LookAtCount(HealthReport r)
        {
            if (r == null) return 0;
            var ignored = Plugin.Settings.IgnoredFindings;
            return (r.Unusual.Count > 0 ? 1 : 0)
                 + (r.TelemetryLarge && !ignored.Contains("telemetry") ? 1 : 0)
                 + r.Findings.Count(f => !ignored.Contains(f.Id))
                 + LayoutFindings().Count(f => !ignored.Contains(f.Id));
        }

        /// <summary>
        /// Triple layout problems for the Overview: iRacing changed the saved layout, or a triple
        /// setup has no saved layout for the guard to protect.
        /// </summary>
        public List<Finding> LayoutFindings()
        {
            var found = new List<Finding>();
            var session = Page?.Session;
            if (session == null || !string.Equals(session.RendererFile, LayoutGuard.MonitorFile, StringComparison.OrdinalIgnoreCase)) return found;
            var saved = Plugin.Settings.SavedLayout;
            bool triples = session.Find(LayoutGuard.MonitorFile, "MonitorSetup", "NumMonitors")?.Draft == "3";
            if (saved.Count > 0 && LayoutGuard.Differences(session, saved).Count > 0)
                found.Add(new Finding { Id = "layout-changed", Rating = Rating.Concerning, Title = "iRacing changed your triple layout", Detail = "Restore the saved layout before your next race." });
            else if (saved.Count == 0 && triples)
                found.Add(new Finding { Id = "layout-unsaved", Rating = Rating.Concerning, Title = "No saved triple layout yet", Detail = "Save it, so Tech Inspector can put it back when iRacing changes it." });
            return found;
        }

        /// <summary>Something the health check reads changed outside a reload, such as a telemetry cleanup.</summary>
        public event EventHandler HealthChanged;

        public void NotifyHealthChanged(bool rescanTelemetry)
        {
            if (rescanTelemetry) _telemetry = null;
            HealthChanged?.Invoke(this, EventArgs.Empty);
        }

        public void GoTo(Setting s) { TabRequested?.Invoke("settings"); GoToRequested?.Invoke(s); }
        public void ShowFilter(string filter) { TabRequested?.Invoke("settings"); FilterRequested?.Invoke(filter); }
        public void ShowTab(string tab) => TabRequested?.Invoke(tab);
    }
}
