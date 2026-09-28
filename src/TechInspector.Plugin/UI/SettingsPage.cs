using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using TechInspector.Core.Settings;

namespace TechInspector.Plugin.UI
{
    // ── Items inside a section card ──
    public sealed class GroupHeaderItem { public string Title { get; set; } }
    public sealed class TierDividerItem { public string Title { get; set; } }
    public sealed class MoreLinkItem { public SectionCard Card { get; set; } public string Text { get; set; } }
    public sealed class ReviewItem { public string File { get; set; } public string Setting { get; set; } public string Now { get; set; } public string New { get; set; } }
    public sealed class SectionFootItem { public SectionCard Card { get; set; } public int Hidden { get; set; } public int Expert { get; set; } public bool HasHidden => Hidden > 0; public bool HasExpert => Expert > 0; }

    /// <summary>A focus area: its title and the section cards under it.</summary>
    public sealed class AreaGroup
    {
        public string Title { get; set; }
        public List<SectionCard> Cards { get; set; }
    }

    /// <summary>One section card: a SimHub expander whose body is built only while it's open.</summary>
    public sealed class SectionCard : ViewModelBase
    {
        private readonly SettingsPage _page;
        private bool _expanded;

        public SectionCard(SettingsPage page, SectionInfo info) { _page = page; Info = info; }

        public SectionInfo Info { get; }
        public string Name => Info.Name;
        public string RawName => $"[{Info.Section}] · {Info.FileName}";
        public string CountText { get; set; }
        public string ChangedText { get; set; }
        public bool HasChanged => !string.IsNullOrEmpty(ChangedText);
        public ObservableCollection<object> Items { get; } = new ObservableCollection<object>();

        public bool IsExpanded
        {
            get => _expanded;
            set
            {
                if (!Set(ref _expanded, value)) return;
                _page.Expanded[Info.Key] = value;
                _page.FillCard(this);
            }
        }

        internal void SetExpandedSilently(bool value) { _expanded = value; Raise(nameof(IsExpanded)); }
        internal void RaiseCounts() { Raise(nameof(CountText)); Raise(nameof(ChangedText)); Raise(nameof(HasChanged)); }
    }

    /// <summary>
    /// The Settings tab's state and layout rules, ported from the prototype: focus areas, tiers
    /// (core shows, "more" behind Show more, expert behind the toggle), search and filters.
    /// Changed and unusual rows always show, whatever their tier.
    /// </summary>
    public sealed partial class SettingsPage : ViewModelBase
    {
        public const string FilterAll = "all", FilterChanged = "changed", FilterUnusual = "unusual", FilterInert = "inert", FilterIniOnly = "ini";

        private readonly Dictionary<string, SettingRow> _rows = new Dictionary<string, SettingRow>();
        private readonly HashSet<string> _moreOpen = new HashSet<string>();
        private readonly ISet<string> _hidden;
        private string _query = "", _filter = FilterAll, _summary;
        private bool _showMore, _showExpert, _showIniLines;
        private List<AreaGroup> _areas = new List<AreaGroup>();

        public SettingsPage(SettingsSession session, ISet<string> hidden, IEnumerable<Core.Catalog.GoalDef> goals = null)
        {
            Session = session;
            _hidden = hidden;
            foreach (var s in session.Settings) _rows[s.Id] = new SettingRow(this, s);
            BuildNav(goals);
        }

        public SettingsSession Session { get; }
        public Dictionary<string, bool> Expanded { get; } = new Dictionary<string, bool>();
        /// <summary>Raised after any pending value changes, so the changes bar can update.</summary>
        public event EventHandler DraftsChanged;
        /// <summary>Raised when a dangerous change needs a yes before it's applied. The handler calls back with the answer.</summary>
        public Func<SettingRow, string, System.Threading.Tasks.Task<bool>> ConfirmDanger { get; set; }

        public List<AreaGroup> Areas { get => _areas; private set => Set(ref _areas, value); }
        public string Summary { get => _summary; private set => Set(ref _summary, value); }
        public bool IsEmpty => Areas.Count == 0;
        public string EmptyText => $"No settings match “{_query}”. Try the key name as it appears in the file, like MaxCarsToDraw.";

        public string Query { get => _query; set { if (Set(ref _query, value ?? "")) Rebuild(); } }
        public string Filter { get => _filter; set { if (Set(ref _filter, value ?? FilterAll)) Rebuild(); } }
        public bool ShowMore { get => _showMore; set { if (Set(ref _showMore, value)) Rebuild(); } }
        public bool ShowExpert { get => _showExpert; set { if (Set(ref _showExpert, value)) Rebuild(); } }
        public bool ShowIniLines { get => _showIniLines; set => Set(ref _showIniLines, value); }
        public string QueryLower => _query.Trim().ToLower(CultureInfo.InvariantCulture);
        private bool Filtering => QueryLower.Length > 0 || _filter != FilterAll;

        public IEnumerable<Setting> Pending => Session.Pending;

        /// <summary>The review list: file, setting, value now and value to write.</summary>
        public List<ReviewItem> ReviewItems => Session.Pending
            .OrderBy(s => s.FileName).ThenBy(s => s.Label)
            .Select(s => new ReviewItem { File = s.FileName, Setting = s.Label, Now = s.Show(s.Applied), New = s.Show(s.Draft) }).ToList();

        /// <summary>After a reload (a write, an undo, another graphics file), keeps what the user was looking at.</summary>
        public void CopyViewFrom(SettingsPage old)
        {
            if (old == null) return;
            _query = old._query; _filter = old._filter;
            var same = _nav.FirstOrDefault(n => n.IsSelectable && n.Key == old._selected?.Key);
            if (same != null) _selected = same;
            foreach (var kv in old.Expanded) Expanded[kv.Key] = kv.Value;
            foreach (var k in old._moreOpen) _moreOpen.Add(k);
        }
        public int PendingCount => Session.Pending.Count();

        private bool Pinned(Setting s) => s.IsChanged || s.IsUnusual;
        private bool IsHidden(Setting s) => _hidden.Contains(s.Id);

        private bool Matches(SettingRow r)
        {
            var s = r.Setting;
            if (IsHidden(s) && _filter == FilterAll && QueryLower.Length == 0) return false;
            if (QueryLower.Length > 0 && !r.SearchText.Contains(QueryLower)) return false;
            switch (_filter)
            {
                case FilterChanged: return s.IsChanged;
                case FilterUnusual: return s.IsUnusual;
                case FilterInert: return s.IsInert;
                case FilterIniOnly: return s.Def.IniOnly;
                default: return true;
            }
        }

        /// <summary>Recomputes which sections show and the summary line. Open cards rebuild their rows.</summary>
        public void Rebuild()
        {
            int matches = 0, expertMatches = 0;
            var areas = new List<AreaGroup>();
            foreach (var info in Session.Sections)
            {
                var rows = Session.In(info).Select(s => _rows[s.Id]).ToList();
                var avail = rows.Where(r => !r.Setting.IsExpert || _showExpert).ToList();
                if (Filtering) expertMatches += rows.Count(r => r.Setting.IsExpert && !_showExpert && Matches(r));
                if (avail.Count == 0) continue;
                int shown;
                if (Filtering)
                {
                    shown = avail.Count(Matches);
                    if (shown == 0) continue;
                    matches += shown;
                }
                else shown = Split(info, avail).Shown;

                var card = new SectionCard(this, info)
                {
                    CountText = Filtering ? $"{shown} of {Plural(avail.Count, "setting", "settings")}" : Plural(shown, "setting", "settings"),
                    ChangedText = rows.Count(r => r.Setting.IsChanged) is int ch && ch > 0 ? $"{ch} changed · " : null,
                };
                // All settings opens as an index of closed sections; searches and filters open what they match.
                bool open = Expanded.TryGetValue(info.Key, out var o) ? o : Filtering;
                card.SetExpandedSilently(open);
                if (open) FillCard(card);

                var area = areas.FirstOrDefault(a => a.Title == info.Area);
                if (area == null) areas.Add(area = new AreaGroup { Title = info.Area, Cards = new List<SectionCard>() });
                area.Cards.Add(card);
            }
            Areas = areas;
            Raise(nameof(IsEmpty));
            Raise(nameof(EmptyText));

            int nMore = Session.Settings.Count(s => s.Tier == "more"), nExpert = Session.Settings.Count(s => s.IsExpert);
            if (QueryLower.Length > 0) Summary = $"{Plural(matches, "setting matches", "settings match")} “{_query.Trim()}”." + (expertMatches > 0 ? $" {expertMatches} more in expert settings." : "");
            else if (Filtering) Summary = Plural(matches, "setting", "settings") + ".";
            else if (!_showMore && !_showExpert) Summary = $"Showing the settings drivers change most. {nMore} more and {nExpert} expert settings are hidden.";
            else if (!_showExpert) Summary = $"Showing all settings except {nExpert} expert ones.";
            else if (!_showMore) Summary = $"{nMore} more settings are hidden.";
            else Summary = $"Showing all {Session.Settings.Count} settings.";
            RefreshNav();
        }

        private sealed class Tiers { public List<SettingRow> Core, More, Expert; public bool MoreOpen; public int Shown; }

        private Tiers Split(SectionInfo info, List<SettingRow> avail)
        {
            var vis = avail.Where(r => !IsHidden(r.Setting)).ToList();
            var t = new Tiers
            {
                Core = vis.Where(r => r.Setting.Tier == "core" || Pinned(r.Setting)).ToList(),
                More = vis.Where(r => r.Setting.Tier == "more" && !Pinned(r.Setting)).ToList(),
                Expert = vis.Where(r => r.Setting.IsExpert && !Pinned(r.Setting)).ToList(),
            };
            if (t.Core.Count == 0) { t.Core = t.More; t.More = new List<SettingRow>(); }
            if (t.Core.Count == 0) { t.Core = t.Expert; t.Expert = new List<SettingRow>(); }
            t.MoreOpen = _showMore || _moreOpen.Contains(info.Key);
            t.Shown = t.Core.Count + (t.MoreOpen ? t.More.Count : 0) + t.Expert.Count;
            return t;
        }

        /// <summary>Builds an open card's body: group headings, rows, the tier dividers, Show more, and the footer.</summary>
        public void FillCard(SectionCard card)
        {
            card.Items.Clear();
            if (!card.IsExpanded) return;
            var rows = Session.In(card.Info).Select(s => _rows[s.Id]).ToList();
            var avail = rows.Where(r => !r.Setting.IsExpert || _showExpert).ToList();
            if (Filtering)
            {
                AddRows(card, avail.Where(Matches).ToList());
            }
            else
            {
                var t = Split(card.Info, avail);
                // Shown "more" rows join their groups (after the core rows in each), so no heading repeats.
                AddRows(card, t.MoreOpen ? t.Core.Concat(t.More).ToList() : t.Core);
                if (t.More.Count > 0 && !_showMore)
                    card.Items.Add(new MoreLinkItem { Card = card, Text = t.MoreOpen ? "Show less" : "Show more" });
                if (t.Expert.Count > 0) { card.Items.Add(new TierDividerItem { Title = "Expert" }); AddRows(card, t.Expert); }
            }
            int hidden = rows.Count(r => IsHidden(r.Setting)), expert = rows.Count(r => r.Setting.IsExpert);
            if ((hidden > 0 && _filter == FilterAll && QueryLower.Length == 0) || (expert > 0 && !_showExpert))
                card.Items.Add(new SectionFootItem { Card = card, Hidden = _filter == FilterAll && QueryLower.Length == 0 ? hidden : 0, Expert = _showExpert ? 0 : expert });
        }

        private void AddRows(SectionCard card, List<SettingRow> rows)
        {
            // Leads and hand-written entries first; within the rest, core rows before pinned extras.
            int Ord(SettingRow r) { int k = Session.SortKey(r.Setting); return k >= 10000 && r.Setting.Tier != "core" ? k + 1000000 : k; }
            var ordered = rows.OrderBy(Ord).ToList();
            var groups = ordered.Select(r => r.Setting.Group ?? "").Distinct().OrderBy(g => g.Length == 0 ? 1 : 0).ToList();
            foreach (var g in groups)
            {
                if (groups.Count > 1)
                    card.Items.Add(new GroupHeaderItem { Title = g.Length == 0 ? "Other" : string.Equals(g, card.Name, StringComparison.OrdinalIgnoreCase) ? "General" : g });
                foreach (var r in ordered.Where(r => (r.Setting.Group ?? "") == g)) card.Items.Add(r);
            }
        }

        /// <summary>Refreshes every row's markers, e.g. after an unusual value was ignored elsewhere.</summary>
        public void RefreshAll()
        {
            foreach (var r in _rows.Values) r.Refresh();
            Rebuild();
        }

        public SettingRow RowFor(Setting s) => s != null && _rows.TryGetValue(s.Id, out var r) ? r : null;

        /// <summary>Makes a setting visible: clears the search and filter, opens its section and tier.</summary>
        public void Reveal(Setting s)
        {
            _query = ""; Raise(nameof(Query));
            _filter = FilterAll; Raise(nameof(Filter));
            _selected = AllNav; Raise(nameof(Selected));
            if (s.IsExpert) { _showExpert = true; Raise(nameof(ShowExpert)); }
            if (s.Tier == "more") _moreOpen.Add(s.SectionInfo.Key);
            _hidden.Remove(s.Id);
            Expanded[s.SectionInfo.Key] = true;
            Rebuild();
        }

        public void ToggleMore(SectionCard card)
        {
            if (!_moreOpen.Remove(card.Info.Key)) _moreOpen.Add(card.Info.Key);
            FillCard(card);
            card.CountText = Plural(Split(card.Info, Session.In(card.Info).Select(s => _rows[s.Id]).Where(r => !r.Setting.IsExpert || _showExpert).ToList()).Shown, "setting", "settings");
            card.RaiseCounts();
        }

        public void ShowHidden(SectionCard card)
        {
            foreach (var s in Session.In(card.Info)) _hidden.Remove(s.Id);
            Rebuild();
        }

        public void Hide(SettingRow row) { _hidden.Add(row.Setting.Id); Rebuild(); }

        /// <summary>Applies a pending value, asking first for dangerous changes. Refreshes the rows it can affect.</summary>
        public async void Change(SettingRow row, string value)
        {
            var s = row.Setting;
            var before = s.Draft;
            if (!string.IsNullOrEmpty(s.Def.Danger) && ConfirmDanger != null && !Setting.SameValue(value, before))
            {
                if (!await ConfirmDanger(row, value)) { row.Refresh(); return; }
            }
            var error = Session.SetDraft(s, value);
            if (error != null) { row.Refresh(); return; }
            AfterChange(s);
        }

        public void Reset(SettingRow row)
        {
            var d = Session.DefaultOf(row.Setting);
            if (d != null) Change(row, d);
        }

        /// <summary>Tells listeners (the changes bar, the other tabs) that pending values changed.</summary>
        public void NotifyDraftsChanged() => DraftsChanged?.Invoke(this, EventArgs.Empty);

        public void DiscardAll()
        {
            Session.DiscardAll();
            foreach (var r in _rows.Values) r.Refresh();
            Rebuild();
            DraftsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void AfterChange(Setting s)
        {
            // Dependencies live within a section, so refreshing the section's rows keeps inert states right.
            foreach (var other in Session.In(s.SectionInfo)) _rows[other.Id].Refresh();
            foreach (var area in Areas)
                foreach (var card in area.Cards.Where(c => c.Info == s.SectionInfo))
                {
                    int ch = Session.In(card.Info).Count(x => x.IsChanged);
                    card.ChangedText = ch > 0 ? $"{ch} changed · " : null;
                    card.RaiseCounts();
                }
            RefreshNav();
            DraftsChanged?.Invoke(this, EventArgs.Empty);
        }

        public static string Plural(int n, string one, string many) => n + " " + (n == 1 ? one : many);
    }
}
