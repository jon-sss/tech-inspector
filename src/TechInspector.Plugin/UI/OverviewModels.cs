using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using MahApps.Metro.IconPacks;
using TechInspector.Core.Health;
using TechInspector.Core.Settings;

namespace TechInspector.Plugin.UI
{
    public sealed class StatusLine
    {
        public PackIconMaterialKind Icon { get; set; }
        public Brush IconBrush { get; set; }
        public string Text { get; set; }

        public static readonly Brush OkBrush = Frozen(0x7D, 0xDC, 0x6A), WarnBrush = Frozen(0xE8, 0xA3, 0x3A), BadBrush = Frozen(0xE3, 0x5D, 0x5D);
        public static StatusLine Ok(string t) => new StatusLine { Icon = PackIconMaterialKind.CheckBold, IconBrush = OkBrush, Text = t };
        public static StatusLine Warn(string t) => new StatusLine { Icon = PackIconMaterialKind.Alert, IconBrush = WarnBrush, Text = t };
        private static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
    }

    /// <summary>The unusual-values report: one card holding a row per value, each with its own actions.</summary>
    public sealed class UnusualCard
    {
        public string Title { get; set; }
        public List<UnusualRow> Rows { get; set; }
        public bool HasMore { get; set; }
        public string MoreText { get; set; }
    }

    public sealed class UnusualRow
    {
        public Setting Setting { get; set; }
        public string Label => Setting.Label;
        public string Section => Setting.SectionInfo.Name;
        public string Value => Setting.Show(Setting.Draft);
        public string Note => Setting.UnusualNote;
        public bool CanReset { get; set; }
        public string ResetTip { get; set; }
    }

    /// <summary>A plain finding: rating icon, title, one line, its links and Ignore.</summary>
    public sealed class FindingCard
    {
        public Finding Finding { get; set; }
        public string Title => Finding.Title;
        public string Detail => Finding.Detail;
        public bool HasSetting => Finding.Setting != null;
        /// <summary>For findings that live on another tab (the triple layout): the tab to open.</summary>
        public string Tab { get; set; }
        public bool HasTab => Tab != null;
        public string TabText => Tab == "triples" ? "Open Triple screens" : "Open";
        public PackIconMaterialKind Icon => Finding.Rating == Rating.Bad ? PackIconMaterialKind.CloseCircle : Finding.Rating == Rating.Good ? PackIconMaterialKind.CheckCircle : PackIconMaterialKind.Alert;
        public Brush IconBrush => Finding.Rating == Rating.Bad ? StatusLine.BadBrush : Finding.Rating == Rating.Good ? StatusLine.OkBrush : StatusLine.WarnBrush;
        public string RatingName => Finding.Rating.ToString();
    }

    public sealed class AgeChoice
    {
        public int Days { get; set; }
        public string Label { get; set; }
        public bool IsEnabled { get; set; }
        public override string ToString() => Label;
    }

    /// <summary>The telemetry finding and its inline cleanup panel.</summary>
    public sealed class TelemetryCard : ViewModelBase
    {
        private bool _open, _busy, _auto;
        private AgeChoice _selected;

        public string Title { get; set; }
        public string Detail { get; set; }
        public List<AgeChoice> Choices { get; set; }
        public TelemetryFolder Folder { get; set; }

        public bool PanelOpen { get => _open; set { if (Set(ref _open, value)) Raise(nameof(ToggleText)); } }
        public string ToggleText => _open ? "Close" : "Clean up";
        public bool Busy { get => _busy; set { if (Set(ref _busy, value)) { Raise(nameof(CanMove)); Raise(nameof(MoveText)); } } }
        public bool AutoClean { get => _auto; set => Set(ref _auto, value); }

        public AgeChoice Selected
        {
            get => _selected;
            set { if (Set(ref _selected, value)) { Raise(nameof(MoveText)); Raise(nameof(CanMove)); } }
        }

        public List<TelemetryFolder.IbtFile> Picked => Selected == null ? new List<TelemetryFolder.IbtFile>() : Folder.OlderThan(Selected.Days, DateTime.Now);
        public bool CanMove => !Busy && Picked.Count > 0;
        public string MoveText => Busy ? "Moving…" : $"Move {SettingsPage.Plural(Picked.Count, "file", "files")} to the Recycle Bin";
        public string SimNote => TechInspectorPlugin.IsSimProcessRunning() ? " The file iRacing is writing now is skipped." : "";
    }

    /// <summary>The result of a cleanup, shown in place of the telemetry finding until the next scan.</summary>
    public sealed class TelemetryDoneCard
    {
        public string Title { get; set; }
        public string Detail { get; set; }
        public List<string> Moved { get; set; }
        public bool CanUndo => Moved != null && Moved.Count > 0;
    }

    public sealed class IgnoredItem
    {
        public string Id { get; set; }
        public bool IsFinding { get; set; }
        public string Label { get; set; }
        public string Sub { get; set; }
        public string Value { get; set; }
        public string Note { get; set; }
    }
}
