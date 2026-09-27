using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TechInspector.Core.Settings;

namespace TechInspector.Plugin.UI
{
    public enum RowMarker { None, Danger, Changed, Unusual }

    public sealed class OptionItem
    {
        public string Value { get; set; }
        public string Label { get; set; }
        public string Tip { get; set; }
        public override string ToString() => Label;
    }

    /// <summary>One setting row: label, one line of explanation, the control, and its state markers.</summary>
    public sealed class SettingRow : ViewModelBase
    {
        private readonly SettingsPage _page;
        private List<OptionItem> _options;

        public SettingRow(SettingsPage page, Setting setting)
        {
            _page = page;
            Setting = setting;
        }

        public Setting Setting { get; }
        public string Label => Setting.Label;
        public string Key => Setting.Key;
        public string Type => Setting.IsReadOnly ? "readonly" : Setting.Def.RequiredBySimHub ? "required" : Setting.Def.Type == "range" ? "num" : Setting.Def.Type;

        public bool IsInert => Setting.IsInert;
        /// <summary>Controls are greyed out while a parent setting switches this one off.</summary>
        public bool IsEditable => !Setting.IsInert;
        public RowMarker Marker =>
            !string.IsNullOrEmpty(Setting.Def.Danger) ? RowMarker.Danger :
            Setting.IsChanged ? RowMarker.Changed :
            Setting.IsUnusual ? RowMarker.Unusual : RowMarker.None;

        /// <summary>The explanation line: why it has no effect, or what it does.</summary>
        public string HelpText => IsInert ? Setting.InertMessage : Setting.Help;
        /// <summary>Amber note after the explanation: why the value is unusual.</summary>
        public string UnusualText => !IsInert && !Setting.IsChanged && Setting.IsUnusual && !HelpMentionsDefault ? " " + Setting.UnusualNote : null;
        public string WasText => Setting.IsChanged ? $" Was {Setting.Show(Setting.Applied)}." : null;
        /// <summary>A cost trade-off, shown once the value is changed.</summary>
        public string CautionText => Setting.IsChanged && !string.IsNullOrEmpty(Setting.Def.Caution) ? " " + Setting.Def.Caution : null;
        private bool HelpMentionsDefault => Setting.Help != null && Setting.Help.IndexOf("default", StringComparison.OrdinalIgnoreCase) >= 0 && Setting.Def.UnusualNote == null;

        public bool IsIniOnly => Setting.Def.IniOnly;
        public string IniOnlyTip => $"Not shown in iRacing's options screens. Stored only in {Setting.FileName}.";

        public string RawLine => $"{Setting.Key}={Setting.Draft}   {(string.IsNullOrEmpty(Setting.Note) ? "" : "; " + Setting.Note)}";
        public string SourceText
        {
            get
            {
                switch (Setting.Def.Source)
                {
                    case "research":
                        var hosts = (Setting.Def.Refs ?? new List<string>()).Take(2).Select(u => { try { return new Uri(u).Host.Replace("www.", ""); } catch { return u; } });
                        return "Description from " + string.Join(", ", hosts) + ".";
                    case "reason": return "Description worked out from the key name and iRacing's note. Not confirmed by a source.";
                    case "file": return Setting.FromCatalog ? "No description yet. Showing iRacing's note." : "New or unknown key. The control is worked out from iRacing's note.";
                    default: return null;
                }
            }
        }
        public string FirstRef => Setting.Def.Refs?.FirstOrDefault();

        public bool CanReset => !Setting.IsReadOnly && Setting.Def.Default.HasValue && !Setting.SameValue(Setting.Draft, _page.Session.DefaultOf(Setting));
        public string ResetTip => Setting.Def.Default.HasValue ? $"Reset to iRacing's default ({Setting.Show(_page.Session.DefaultOf(Setting))})." : null;
        public bool CanHide => !Setting.Def.RequiredBySimHub && string.IsNullOrEmpty(Setting.Def.Danger) && !Setting.IsReadOnly;
        public string DangerText => Setting.Def.Danger;

        // ── control values ──

        public bool IsOn
        {
            get => Setting.IsOn(Setting.Draft);
            set => _page.Change(this, Setting.Def.Invert ? (value ? "0" : "1") : (value ? "1" : "0"));
        }

        public List<OptionItem> Options
        {
            get
            {
                if (_options != null) return _options;
                _options = (Setting.Def.Options ?? new List<Core.Catalog.OptionDef>())
                    .Select(o => new OptionItem { Value = Setting.Format(o.Value), Label = o.Label, Tip = o.Tip }).ToList();
                // A value the list doesn't know stays selectable, so the file is never changed by just looking.
                if (!_options.Any(o => Setting.SameValue(o.Value, Setting.Applied)))
                    _options.Add(new OptionItem { Value = Setting.Applied, Label = Setting.Applied + " (not a listed option)" });
                return _options;
            }
        }

        public OptionItem SelectedOption
        {
            get => Options.FirstOrDefault(o => Setting.SameValue(o.Value, Setting.Draft));
            set { if (value != null) _page.Change(this, value.Value); }
        }

        public string OptionsTip => string.Join("\n", Options.Where(o => !string.IsNullOrEmpty(o.Tip)).Select(o => o.Label + ": " + o.Tip));
        public bool HasOptionsTip => Options.Any(o => !string.IsNullOrEmpty(o.Tip));

        public string Text
        {
            get => Setting.Draft;
            set => _page.Change(this, value);
        }

        /// <summary>For sliders: the pending value as a number. Setting it records a pending change.</summary>
        public double NumberValue
        {
            get => double.TryParse(Setting.Draft, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
            set => _page.Change(this, Setting.Format(value));
        }

        public string Unit => Setting.Def.Unit;
        public bool HasUnit => !string.IsNullOrEmpty(Setting.Def.Unit);
        public string ReadOnlyText => string.IsNullOrEmpty(Setting.Draft) ? "(empty)" : Setting.Draft;

        public string SearchText => string.Join(" ", new[] { Setting.Label, Setting.Key, Setting.Help, Setting.Group, Setting.SectionInfo.Name }
            .Concat(Options.Select(o => o.Label))).ToLower(CultureInfo.InvariantCulture);

        public void Refresh() => RaiseAll();
    }
}
