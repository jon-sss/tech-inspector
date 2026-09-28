using System;
using System.Collections.Generic;
using System.Linq;
using TechInspector.Core.Catalog;
using TechInspector.Core.Settings;

namespace TechInspector.Plugin.UI
{
    /// <summary>What a rail entry's checkpoint mark says.</summary>
    public enum NavMark { None, Tick, Unusual, Changed, Link }

    /// <summary>One entry in the Settings rail: a heading, a common fix, or a list of settings.</summary>
    public abstract class NavItem : ViewModelBase
    {
        private string _count;
        private NavMark _mark;
        private bool _visible = true;

        public string Key { get; protected set; }
        public string Title { get; protected set; }
        public virtual bool IsSelectable => true;
        public string CountText { get => _count; set => Set(ref _count, value); }
        public NavMark Mark { get => _mark; set { if (Set(ref _mark, value)) Raise(nameof(MarkTip)); } }
        public bool IsVisible { get => _visible; set => Set(ref _visible, value); }

        public string MarkTip =>
            _mark == NavMark.Unusual ? "Has an unusual value" :
            _mark == NavMark.Changed ? "Has changes not written" :
            _mark == NavMark.Tick ? "Nothing here needs a look" :
            _mark == NavMark.Link ? "Opens another tab" : null;
    }

    public sealed class NavHeading : NavItem
    {
        public NavHeading(string title, bool first = false) { Title = title; Key = "h:" + title; IsFirst = first; }
        public override bool IsSelectable => false;
        public bool IsFirst { get; }
    }

    /// <summary>A common fix: a goal with its handful of settings.</summary>
    public sealed class GoalNav : NavItem
    {
        public GoalNav(GoalDef goal, List<SettingRow> rows, List<SectionLink> sections)
        {
            Goal = goal; Rows = rows; Sections = sections;
            Key = "goal:" + goal.Id; Title = goal.Title;
            if (goal.Tab != null) Mark = NavMark.Link;
        }

        public GoalDef Goal { get; }
        public string Summary => Goal.Summary;
        public List<SettingRow> Rows { get; }
        /// <summary>The full sections these settings come from, for "In full".</summary>
        public List<SectionLink> Sections { get; }
        public bool HasSections => Sections.Count > 0;
        public string Tab => Goal.Tab;
    }

    /// <summary>A browsable list: every setting, or one of the filters.</summary>
    public sealed class ListNav : NavItem
    {
        public ListNav(string filter, string title, bool alwaysShown)
        {
            Filter = filter; Title = title; AlwaysShown = alwaysShown; Key = "list:" + filter;
        }

        public string Filter { get; }
        /// <summary>False for lists that only appear when they have something in them.</summary>
        public bool AlwaysShown { get; }
    }

    public sealed class SectionLink
    {
        public SectionInfo Info { get; set; }
        public string Name => Info.Name;
    }

    public sealed partial class SettingsPage
    {
        private NavItem _selected;
        private List<NavItem> _nav = new List<NavItem>();

        /// <summary>The rail: common fixes, then the lists.</summary>
        public List<NavItem> Nav => _nav;

        /// <summary>Asks the page's host to open another tab (the Triple screens goal).</summary>
        public event Action<string> TabRequested;

        private ListNav AllNav => _nav.OfType<ListNav>().First(n => n.Filter == FilterAll);

        private void BuildNav(IEnumerable<GoalDef> goals)
        {
            var nav = new List<NavItem> { new NavHeading("Common fixes", first: true) };
            foreach (var g in goals ?? Enumerable.Empty<GoalDef>())
            {
                var rows = g.Resolve(Session).Where(s => _rows.ContainsKey(s.Id)).Select(s => _rows[s.Id]).ToList();
                if (g.Tab == null && rows.Count == 0) continue;
                var sections = rows.Select(r => r.Setting.SectionInfo).Distinct().Select(i => new SectionLink { Info = i }).ToList();
                nav.Add(new GoalNav(g, rows, sections));
            }
            nav.Add(new NavHeading("Browse"));
            nav.Add(new ListNav(FilterAll, "All settings", true));
            nav.Add(new ListNav(FilterUnusual, "Unusual values", false));
            nav.Add(new ListNav(FilterChanged, "Changed, not written", false));
            nav.Add(new ListNav(FilterInert, "No effect right now", false));
            nav.Add(new ListNav(FilterIniOnly, "Not in iRacing's options", true));
            _nav = nav;
            _selected = nav.OfType<GoalNav>().FirstOrDefault(n => n.Tab == null) ?? (NavItem)AllNav;
        }

        /// <summary>The rail selection. A goal shows its settings; a list shows the matching sections.</summary>
        public NavItem Selected
        {
            get => _selected;
            set
            {
                if (value == null || !value.IsSelectable) return;
                if (value is GoalNav g && g.Tab != null) { TabRequested?.Invoke(g.Tab); return; }   // the rail puts its selection back
                bool clearQuery = _query.Length > 0;
                if (!Set(ref _selected, value) && !clearQuery) return;
                _query = ""; Raise(nameof(Query));
                if (value is ListNav l) { _filter = l.Filter; Raise(nameof(Filter)); }
                Rebuild();
            }
        }

        public GoalNav Goal => _selected as GoalNav;
        /// <summary>True when the pane shows a common fix rather than a list or search results.</summary>
        public bool ShowingGoal => _selected is GoalNav && QueryLower.Length == 0;
        public bool ShowingList => !ShowingGoal;

        /// <summary>The list pane's title: the search, or the list chosen in the rail.</summary>
        public string ListTitle => QueryLower.Length > 0 ? "Search results" : (_selected as ListNav)?.Title ?? "All settings";

        /// <summary>Under All settings: the way to see the rows the summary says are hidden.</summary>
        public bool CanShowMore => ShowingList && !_showMore && !Filtering;
        public string ShowMoreText => "Show " + Session.Settings.Count(s => s.Tier == "more").ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " more";
        public bool CanShowExpert => ShowingList && !_showExpert && Session.Settings.Any(s => s.IsExpert);
        public string ShowExpertText => "Show " + Session.Settings.Count(s => s.IsExpert) + " expert settings";

        /// <summary>Selects the list for a filter, e.g. when the Overview asks to review unusual values.</summary>
        public void SelectList(string filter)
        {
            var target = _nav.OfType<ListNav>().FirstOrDefault(n => n.Filter == filter) ?? AllNav;
            target.IsVisible = true;
            Selected = target;
        }

        /// <summary>Opens a section in All settings, for "In full" links.</summary>
        public void OpenSection(SectionInfo info)
        {
            Expanded[info.Key] = true;
            if (_selected != AllNav) Selected = AllNav; else Rebuild();
        }

        /// <summary>Recounts the rail: marks on the common fixes, counts on the lists.</summary>
        private void RefreshNav()
        {
            foreach (var n in _nav)
            {
                if (n is GoalNav g && g.Tab == null)
                {
                    g.Mark = g.Rows.Any(r => r.Setting.IsChanged) ? NavMark.Changed : g.Rows.Any(r => r.Setting.IsUnusual) ? NavMark.Unusual : NavMark.Tick;
                }
                else if (n is ListNav l)
                {
                    int count = Session.Settings.Count(s => ListMatch(l.Filter, s));
                    l.CountText = count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
                    l.Mark = l.Filter == FilterUnusual && count > 0 ? NavMark.Unusual : l.Filter == FilterChanged && count > 0 ? NavMark.Changed : NavMark.None;
                    l.IsVisible = l.AlwaysShown || count > 0 || l == _selected;
                }
            }
            Raise(nameof(Goal)); Raise(nameof(ShowingGoal)); Raise(nameof(ShowingList)); Raise(nameof(ListTitle));
            Raise(nameof(CanShowMore)); Raise(nameof(ShowMoreText)); Raise(nameof(CanShowExpert)); Raise(nameof(ShowExpertText));
        }

        private bool ListMatch(string filter, Setting s)
        {
            switch (filter)
            {
                case FilterChanged: return s.IsChanged;
                case FilterUnusual: return s.IsUnusual;
                case FilterInert: return s.IsInert;
                case FilterIniOnly: return s.Def.IniOnly && (!s.IsExpert || _showExpert);
                default: return !s.IsExpert || _showExpert;
            }
        }
    }
}
