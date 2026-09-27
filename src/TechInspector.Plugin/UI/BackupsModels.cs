using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TechInspector.Core;
using TechInspector.Core.IO;
using TechInspector.Core.Settings;

namespace TechInspector.Plugin.UI
{
    /// <summary>One backup in the history, with its compare panel.</summary>
    public sealed class BackupRow : ViewModelBase
    {
        private static readonly string[] Months = CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames;

        public BackupRow(Backup b) { Backup = b; }

        public Backup Backup { get; }
        public string Number => "#" + Backup.Number;
        public string When => Stamp(Backup.Created);
        public string WhenFull => Backup.Created.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        public bool Kept => Backup.Kept;

        public string Title =>
            Backup.Kind == BackupKind.Manual ? (string.IsNullOrEmpty(Backup.Name) ? "Manual backup" : "“" + Backup.Name + "”")
            : Backup.Reason;

        /// <summary>What it holds: the one file for a copy, a few names, or a count.</summary>
        public string Detail
        {
            get
            {
                var names = Backup.Files.Select(Backup.ShortName).ToList();
                if (names.Count <= 3) return string.Join(", ", names);
                bool profiles = names.Any(n => n.StartsWith(IracingPaths.ProfilesFolder + "\\", StringComparison.OrdinalIgnoreCase));
                bool controls = names.Any(n => n.Equals("controls.cfg", StringComparison.OrdinalIgnoreCase));
                var what = controls && profiles ? "settings, controls and profiles" : controls ? "settings and controls" : "settings";
                return $"{names.Count} files: {what}";
            }
        }

        private bool _open;
        public bool CompareOpen { get => _open; set { if (Set(ref _open, value)) Raise(nameof(CompareText)); } }
        public string CompareText => _open ? "Hide" : "Compare";

        public List<FileDiffRow> Files { get; set; } = new List<FileDiffRow>();
        public string SameNote { get; set; }
        public bool HasSameNote => !string.IsNullOrEmpty(SameNote);

        public static string Stamp(DateTime t)
        {
            var today = DateTime.Today;
            if (t.Date == today) return "Today " + t.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (t.Date == today.AddDays(-1)) return "Yesterday " + t.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (t.Year == today.Year) return $"{t.Day} {Months[t.Month - 1]} {t:HH:mm}";
            return $"{t.Day} {Months[t.Month - 1]} {t.Year}";
        }

        /// <summary>Fills the compare panel: the files that differ, and one line for those that match.</summary>
        public void LoadCompare(SettingsSession session, Core.Catalog.SettingsCatalog catalog)
        {
            var diffs = BackupStore.Compare(Backup);
            Files = diffs.Where(d => d.State != FileState.Same).Select(d => new FileDiffRow(this, d, session, catalog)).ToList();
            var same = diffs.Where(d => d.State == FileState.Same).Select(d => d.Name).ToList();
            SameNote =
                diffs.Count == 0 ? "The files in this backup are no longer there."
                : Files.Count == 0 ? "Everything in this backup matches your current files."
                : same.Count == 0 ? null
                : same.Count <= 3 ? "Same as now: " + string.Join(", ", same) + "."
                : $"Same as now: {string.Join(", ", same.Take(2))} and {same.Count - 2} more.";
            Raise(nameof(Files));
            Raise(nameof(SameNote));
            Raise(nameof(HasSameNote));
        }
    }

    /// <summary>A file in a backup that differs from the current one.</summary>
    public sealed class FileDiffRow : ViewModelBase
    {
        private const int KeysShown = 12;

        public FileDiffRow(BackupRow owner, FileDiff d, SettingsSession session, Core.Catalog.SettingsCatalog catalog)
        {
            Owner = owner;
            Diff = d;
            AllKeys = d.Keys.Select(k => new KeyDiffRow(d, k, session, catalog)).ToList();
        }

        public BackupRow Owner { get; }
        public FileDiff Diff { get; }
        public string Name => Diff.Name;
        public string State =>
            Diff.State == FileState.MissingNow ? "Not there now"
            : AllKeys.Count > 0 ? SettingsPage.Plural(AllKeys.Count, "setting differs", "settings differ")
            : "Different";

        public List<KeyDiffRow> AllKeys { get; }
        public bool HasKeys => AllKeys.Count > 0;

        private bool _all;
        public IEnumerable<KeyDiffRow> Keys => _all ? AllKeys : AllKeys.Take(KeysShown);
        public bool HasMore => AllKeys.Count > KeysShown;
        public string MoreText => _all ? "Show fewer" : $"Show all {AllKeys.Count}";
        public void ToggleMore() { _all = !_all; Raise(nameof(Keys)); Raise(nameof(MoreText)); }
    }

    /// <summary>One setting that differs, labelled from the catalog where it's known.</summary>
    public sealed class KeyDiffRow
    {
        public KeyDiffRow(FileDiff file, KeyDiff k, SettingsSession session, Core.Catalog.SettingsCatalog catalog)
        {
            var fileName = Path.GetFileName(file.Original);
            bool top = !file.Name.Contains("\\");
            var s = top ? session?.Find(fileName, k.Section, k.Key) : null;
            var def = s?.Def ?? (top ? catalog?.Find(fileName, k.Section, k.Key) : null);
            Label = def?.Label ?? k.Key;
            Sub = def != null
                ? (catalog?.FindSection(fileName, k.Section)?.Name ?? k.Section)
                : "[" + k.Section + "]";
            Now = Show(s, k.Now);
            Then = Show(s, k.Then);
            Tip = $"[{k.Section}] {k.Key}";
        }

        public string Label { get; }
        public string Sub { get; }
        public string Now { get; }
        public string Then { get; }
        public string Tip { get; }

        private static string Show(Setting s, string v) => v == null ? "Not there" : s != null ? s.Show(v) : (v.Length == 0 ? "(empty)" : v);
    }

    /// <summary>The result of the last action on this tab, with Undo where it can be undone.</summary>
    public sealed class BackupsDone
    {
        public string Title { get; set; }
        public string Detail { get; set; }
        public bool HasDetail => !string.IsNullOrEmpty(Detail);
        public string ActionText { get; set; }
        public bool HasAction => ActionText != null;
        public Action Run { get; set; }
        public bool IsError { get; set; }
    }
}
