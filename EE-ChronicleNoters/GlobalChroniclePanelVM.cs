using System;
using System.Collections.Generic;
using TaleWorlds.Library;
using EditableEncyclopedia.ChronicleNoters;

namespace EditableEncyclopedia
{
    /// <summary>
    /// View model for the global chronicle panel (J-key panel).
    /// 2026-05-28 redesign expansion: added filter/search/sort/year-strip state +
    /// VisibleEntries list to back the new XML prefab. Existing ExecuteClose / OnClose
    /// callback preserved so the old programmatic-overlay code path stays working.
    /// </summary>
    internal class GlobalChroniclePanelVM : ViewModel
    {
        private readonly Action _onClose;

        public GlobalChroniclePanelVM(Action onClose)
        {
            _onClose = onClose;
        }

        public void ExecuteClose()
        {
            _onClose?.Invoke();
            var h = OnCloseRequested;
            if (h != null) h();
        }

        // ────────────────────────────────────────────────────────────────
        //  Redesigned panel state (2026-05-28)
        // ────────────────────────────────────────────────────────────────

        private ChronicleFilterState _filter = ChronicleFilterState.Default;
        private readonly MBBindingList<ChronicleEntryVM> _allEntries = new MBBindingList<ChronicleEntryVM>();
        private MBBindingList<ChronicleEntryVM> _visibleEntries = new MBBindingList<ChronicleEntryVM>();
        private int _totalCount;
        private ChronicleEntryVM _selectedEntry;
        private ChronicleYearStripVM _yearStrip = new ChronicleYearStripVM();
        private int? _pendingRangeFromYear;
        private string _searchQuery = string.Empty;

        // External hooks (wired by GlobalChroniclePanel at panel-open time)
        public Action OnCloseRequested;
        public Action OnResetRequested;

        // ── Visible entries (driven by RebuildVisible) ──────────────
        [DataSourceProperty]
        public MBBindingList<ChronicleEntryVM> VisibleEntries
        {
            get { return _visibleEntries; }
            set { if (_visibleEntries != value) { _visibleEntries = value; OnPropertyChanged("VisibleEntries"); OnPropertyChanged("VisibleCount"); } }
        }

        [DataSourceProperty]
        public int VisibleCount { get { return _visibleEntries == null ? 0 : _visibleEntries.Count; } }

        [DataSourceProperty]
        public int TotalCount
        {
            get { return _totalCount; }
            set
            {
                if (_totalCount != value)
                {
                    _totalCount = value;
                    OnPropertyChanged("TotalCount");
                    OnPropertyChanged("HeaderMetaLabel");
                    OnPropertyChanged("FooterCountLabel");
                    OnPropertyChanged("IsEmpty");
                    OnPropertyChanged("EmptyStateTitle");
                    OnPropertyChanged("EmptyStateHint");
                }
            }
        }

        [DataSourceProperty]
        public string HeaderMetaLabel { get { return _totalCount + " EVENTS"; } }

        [DataSourceProperty]
        public string FooterStatusLabel
        {
            get
            {
                if (_filter.YearFrom.HasValue && _filter.YearTo.HasValue)
                    return "FILTERED " + _filter.YearFrom.Value + "–" + _filter.YearTo.Value;
                if (_filter.HasActiveFilters) return "FILTERED";
                return "SCROLL FOR MORE";
            }
        }

        [DataSourceProperty]
        public string FooterCountLabel { get { return "SHOWING " + VisibleCount + " OF " + _totalCount; } }

        // ── Empty-state (shown when the visible list is empty) ──────
        [DataSourceProperty]
        public bool IsEmpty { get { return VisibleCount == 0; } }

        [DataSourceProperty]
        public string EmptyStateTitle
        {
            get { return _totalCount == 0 ? "No chronicle entries yet" : "No events match your filters"; }
        }

        [DataSourceProperty]
        public string EmptyStateHint
        {
            get { return _totalCount == 0 ? "Events you log will appear here." : "Try clearing search or filters."; }
        }

        // ── Search ──────────────────────────────────────────────────
        [DataSourceProperty]
        public string SearchQuery
        {
            get { return _searchQuery; }
            set
            {
                if (_searchQuery != value)
                {
                    _searchQuery = value ?? string.Empty;
                    _filter.SearchQuery = _searchQuery;
                    OnPropertyChanged("SearchQuery");
                    RebuildVisible();
                }
            }
        }

        // ── Year strip ──────────────────────────────────────────────
        [DataSourceProperty]
        public ChronicleYearStripVM YearStrip
        {
            get { return _yearStrip; }
            set { if (_yearStrip != value) { _yearStrip = value; OnPropertyChanged("YearStrip"); } }
        }

        // ── Selected entry (reading column) ──────────────────────────
        [DataSourceProperty]
        public ChronicleEntryVM SelectedEntry
        {
            get { return _selectedEntry; }
            set
            {
                if (_selectedEntry != value)
                {
                    if (_selectedEntry != null) _selectedEntry.IsSelected = false;
                    _selectedEntry = value;
                    if (_selectedEntry != null) _selectedEntry.IsSelected = true;
                    OnPropertyChanged("SelectedEntry");
                    OnPropertyChanged("ReadingNavCounterLabel");
                    OnPropertyChanged("HasSelectedEntry");
                }
            }
        }

        [DataSourceProperty]
        public bool HasSelectedEntry { get { return _selectedEntry != null; } }

        [DataSourceProperty]
        public string ReadingNavCounterLabel
        {
            get
            {
                if (_selectedEntry == null) return string.Empty;
                int idx = _visibleEntries.IndexOf(_selectedEntry);
                return idx < 0 ? string.Empty : "ENTRY " + (idx + 1) + " OF " + _visibleEntries.Count;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Filter chip commands (Event row)
        // ────────────────────────────────────────────────────────────────
        public void ExecuteToggleEventWar()      { ToggleEventTag(EventTag.War); }
        public void ExecuteToggleEventCrime()    { ToggleEventTag(EventTag.Crime); }
        public void ExecuteToggleEventPolitics() { ToggleEventTag(EventTag.Politics); }
        public void ExecuteToggleEventFamily()   { ToggleEventTag(EventTag.Family); }
        public void ExecuteToggleEventOther()    { ToggleEventTag(EventTag.Other); }

        // ────────────────────────────────────────────────────────────────
        //  Filter chip commands (Entity row)
        // ────────────────────────────────────────────────────────────────
        public void ExecuteToggleEntityKingdom()    { ToggleEntityTag(EntityTag.Kingdom); }
        public void ExecuteToggleEntityClan()       { ToggleEntityTag(EntityTag.Clan); }
        public void ExecuteToggleEntityHero()       { ToggleEntityTag(EntityTag.Hero); }
        public void ExecuteToggleEntitySettlement() { ToggleEntityTag(EntityTag.Settlement); }
        public void ExecuteToggleEntityOther()      { ToggleEntityTag(EntityTag.Other); }

        // ────────────────────────────────────────────────────────────────
        //  Chip count labels + active states (mockup parity, 2026-05-28)
        // ────────────────────────────────────────────────────────────────
        [DataSourceProperty] public string AllEventChipLabel    { get { return "ALL " + _allEntries.Count; } }
        [DataSourceProperty] public string WarChipLabel         { get { return "WAR "      + CountByEventTag(EventTag.War); } }
        [DataSourceProperty] public string CrimeChipLabel       { get { return "CRIME "    + CountByEventTag(EventTag.Crime); } }
        [DataSourceProperty] public string PoliticsChipLabel    { get { return "POLITICS " + CountByEventTag(EventTag.Politics); } }
        [DataSourceProperty] public string FamilyChipLabel      { get { return "FAMILY "   + CountByEventTag(EventTag.Family); } }
        [DataSourceProperty] public string OtherEventChipLabel  { get { return "OTHER "    + CountByEventTag(EventTag.Other); } }
        [DataSourceProperty] public string StarredChipLabel     { get { return "STARRED "  + CountStarred(); } }

        [DataSourceProperty] public string AllEntityChipLabel    { get { return "ALL " + _allEntries.Count; } }
        [DataSourceProperty] public string KingdomChipLabel      { get { return "KINGDOM "    + CountByEntityTag(EntityTag.Kingdom); } }
        [DataSourceProperty] public string ClanChipLabel         { get { return "CLAN "       + CountByEntityTag(EntityTag.Clan); } }
        [DataSourceProperty] public string HeroChipLabel         { get { return "HERO "       + CountByEntityTag(EntityTag.Hero); } }
        [DataSourceProperty] public string SettlementChipLabel   { get { return "SETTLEMENT " + CountByEntityTag(EntityTag.Settlement); } }
        [DataSourceProperty] public string OtherEntityChipLabel  { get { return "OTHER "      + CountByEntityTag(EntityTag.Other); } }

        // Active states drive chip visual highlighting
        [DataSourceProperty] public bool AllEventActive     { get { return _filter.ActiveEventTags  == EventTag.None; } }
        [DataSourceProperty] public bool WarActive          { get { return (_filter.ActiveEventTags  & EventTag.War)      != 0; } }
        [DataSourceProperty] public bool CrimeActive        { get { return (_filter.ActiveEventTags  & EventTag.Crime)    != 0; } }
        [DataSourceProperty] public bool PoliticsActive     { get { return (_filter.ActiveEventTags  & EventTag.Politics) != 0; } }
        [DataSourceProperty] public bool FamilyActive       { get { return (_filter.ActiveEventTags  & EventTag.Family)   != 0; } }
        [DataSourceProperty] public bool OtherEventActive   { get { return (_filter.ActiveEventTags  & EventTag.Other)    != 0; } }

        [DataSourceProperty] public bool AllEntityActive    { get { return _filter.ActiveEntityTags == EntityTag.None; } }
        [DataSourceProperty] public bool KingdomActive      { get { return (_filter.ActiveEntityTags & EntityTag.Kingdom)    != 0; } }
        [DataSourceProperty] public bool ClanActive         { get { return (_filter.ActiveEntityTags & EntityTag.Clan)       != 0; } }
        [DataSourceProperty] public bool HeroActive         { get { return (_filter.ActiveEntityTags & EntityTag.Hero)       != 0; } }
        [DataSourceProperty] public bool SettlementActive   { get { return (_filter.ActiveEntityTags & EntityTag.Settlement) != 0; } }
        [DataSourceProperty] public bool OtherEntityActive  { get { return (_filter.ActiveEntityTags & EntityTag.Other)      != 0; } }

        public void ExecuteToggleAllEvent()  { _filter.ActiveEventTags  = EventTag.None;  NotifyEventChipChanges(); RebuildVisible(); }
        public void ExecuteToggleAllEntity() { _filter.ActiveEntityTags = EntityTag.None; NotifyEntityChipChanges(); RebuildVisible(); }

        private int CountByEventTag(EventTag tag)
        {
            int n = 0;
            foreach (var e in _allEntries) if ((e.EventTags & tag) != 0) n++;
            return n;
        }
        private int CountByEntityTag(EntityTag tag)
        {
            int n = 0;
            foreach (var e in _allEntries) if ((e.EntityTags & tag) != 0) n++;
            return n;
        }
        private int CountStarred()
        {
            int n = 0;
            foreach (var e in _allEntries) if (e.IsStarred) n++;
            return n;
        }
        private void NotifyEventChipChanges()
        {
            OnPropertyChanged("AllEventActive");
            OnPropertyChanged("WarActive"); OnPropertyChanged("CrimeActive"); OnPropertyChanged("PoliticsActive");
            OnPropertyChanged("FamilyActive"); OnPropertyChanged("OtherEventActive");
        }
        private void NotifyEntityChipChanges()
        {
            OnPropertyChanged("AllEntityActive");
            OnPropertyChanged("KingdomActive"); OnPropertyChanged("ClanActive"); OnPropertyChanged("HeroActive");
            OnPropertyChanged("SettlementActive"); OnPropertyChanged("OtherEntityActive");
        }
        private void NotifyAllChipChanges()
        {
            NotifyEventChipChanges();
            NotifyEntityChipChanges();
            OnPropertyChanged("AllEventChipLabel"); OnPropertyChanged("AllEntityChipLabel");
            OnPropertyChanged("WarChipLabel"); OnPropertyChanged("CrimeChipLabel"); OnPropertyChanged("PoliticsChipLabel");
            OnPropertyChanged("FamilyChipLabel"); OnPropertyChanged("OtherEventChipLabel");
            OnPropertyChanged("KingdomChipLabel"); OnPropertyChanged("ClanChipLabel"); OnPropertyChanged("HeroChipLabel");
            OnPropertyChanged("SettlementChipLabel"); OnPropertyChanged("OtherEntityChipLabel");
            OnPropertyChanged("StarredChipLabel");
        }

        // ── Star + sort + reading nav + reset ───────────────────────
        public void ExecuteToggleStarredOnly()
        {
            _filter.StarredOnly = !_filter.StarredOnly;
            OnPropertyChanged("StarredOnlyActive");
            RebuildVisible();
        }
        [DataSourceProperty]
        public bool StarredOnlyActive { get { return _filter.StarredOnly; } }

        public void ExecuteToggleSortDirection()
        {
            _filter.Sort = _filter.Sort == SortDirection.Newest ? SortDirection.Oldest : SortDirection.Newest;
            OnPropertyChanged("SortLabel");
            RebuildVisible();
        }
        [DataSourceProperty]
        public string SortLabel { get { return _filter.Sort == SortDirection.Newest ? "DATE: NEWEST" : "DATE: OLDEST"; } }

        public void ExecuteSelectPrev()
        {
            if (_selectedEntry == null || _visibleEntries.Count == 0) return;
            int i = _visibleEntries.IndexOf(_selectedEntry);
            if (i > 0) SelectedEntry = _visibleEntries[i - 1];
        }

        public void ExecuteSelectNext()
        {
            if (_selectedEntry == null || _visibleEntries.Count == 0) return;
            int i = _visibleEntries.IndexOf(_selectedEntry);
            if (i >= 0 && i < _visibleEntries.Count - 1) SelectedEntry = _visibleEntries[i + 1];
        }

        public void ExecuteResetLogs()
        {
            var h = OnResetRequested;
            if (h != null) h();
        }

        // ────────────────────────────────────────────────────────────────
        //  Population API (called from GlobalChroniclePanel populate path)
        // ────────────────────────────────────────────────────────────────
        public void AddSourceEntry(ChronicleEntryVM e)
        {
            if (e != null) _allEntries.Add(e);
        }

        public void ClearSourceEntries()
        {
            _allEntries.Clear();
            _visibleEntries.Clear();
            _selectedEntry = null;
            _totalCount = 0;
            _filter = ChronicleFilterState.Default;
            _searchQuery = string.Empty;
            OnPropertyChanged("SearchQuery");
            OnPropertyChanged("SortLabel");
            OnPropertyChanged("StarredOnlyActive");
            OnPropertyChanged("VisibleEntries");
            OnPropertyChanged("VisibleCount");
            OnPropertyChanged("TotalCount");
            OnPropertyChanged("HeaderMetaLabel");
            OnPropertyChanged("FooterStatusLabel");
            OnPropertyChanged("FooterCountLabel");
            OnPropertyChanged("ReadingNavCounterLabel");
        }

        public void InitializeYearStripFromEntries()
        {
            var years = new SortedSet<int>();
            var withEvents = new HashSet<int>();
            foreach (var e in _allEntries)
            {
                if (e.EntryYear > 0)
                {
                    years.Add(e.EntryYear);
                    withEvents.Add(e.EntryYear);
                }
            }
            _yearStrip.Populate(years, withEvents);
            _yearStrip.OnYearClicked = OnYearStripClicked;
            NotifyAllChipChanges(); // counts derived from _allEntries — refresh now that population finished
            RebuildVisible();
        }

        // ────────────────────────────────────────────────────────────────
        //  Year strip click dispatch
        // ────────────────────────────────────────────────────────────────
        private void OnYearStripClicked(int year, bool shiftHeld)
        {
            if (shiftHeld)
            {
                if (!_pendingRangeFromYear.HasValue)
                {
                    _pendingRangeFromYear = year;
                    _yearStrip.RefreshHighlights(null, year, null);
                }
                else
                {
                    int from = Math.Min(_pendingRangeFromYear.Value, year);
                    int to = Math.Max(_pendingRangeFromYear.Value, year);
                    _filter.YearFrom = from;
                    _filter.YearTo = to;
                    _pendingRangeFromYear = null;
                    _yearStrip.RefreshHighlights(null, from, to);
                    OnPropertyChanged("FooterStatusLabel");
                    RebuildVisible();
                }
            }
            else
            {
                _pendingRangeFromYear = null;
                if (_filter.YearFrom == year && _filter.YearTo == year)
                {
                    _filter.YearFrom = null; _filter.YearTo = null;
                    _yearStrip.RefreshHighlights(null, null, null);
                }
                else
                {
                    _filter.YearFrom = year; _filter.YearTo = year;
                    _yearStrip.RefreshHighlights(year, year, year);
                }
                OnPropertyChanged("FooterStatusLabel");
                RebuildVisible();
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Filter rebuild
        // ────────────────────────────────────────────────────────────────
        private void ToggleEventTag(EventTag tag)
        {
            _filter.ActiveEventTags ^= tag;
            OnPropertyChanged("FooterStatusLabel");
            NotifyEventChipChanges();
            RebuildVisible();
        }

        private void ToggleEntityTag(EntityTag tag)
        {
            _filter.ActiveEntityTags ^= tag;
            OnPropertyChanged("FooterStatusLabel");
            NotifyEntityChipChanges();
            RebuildVisible();
        }

        private void RebuildVisible()
        {
            _visibleEntries.Clear();
            var matched = new List<ChronicleEntryVM>();
            foreach (var e in _allEntries)
            {
                if (ChronicleFilterMatcher.Matches(
                        in _filter,
                        e.EventTags, e.EntityTags,
                        e.IsStarred, e.EntryYear,
                        e.TitleText, e.BodyText,
                        e.SourceEntityName, e.SourceClanName, e.SourceKingdomName))
                {
                    matched.Add(e);
                }
            }

            matched.Sort((a, b) =>
            {
                int c = _filter.Sort == SortDirection.Newest
                    ? b.SortDateKey.CompareTo(a.SortDateKey)
                    : a.SortDateKey.CompareTo(b.SortDateKey);
                if (c != 0) return c;
                // stable, always-reversing tiebreak so same-date entries still flip on toggle
                return _filter.Sort == SortDirection.Newest
                    ? b.BuildIndex.CompareTo(a.BuildIndex)
                    : a.BuildIndex.CompareTo(b.BuildIndex);
            });

            foreach (var e in matched) _visibleEntries.Add(e);

            if (_selectedEntry == null || !_visibleEntries.Contains(_selectedEntry))
                SelectedEntry = _visibleEntries.Count > 0 ? _visibleEntries[0] : null;

            OnPropertyChanged("VisibleEntries");
            OnPropertyChanged("VisibleCount");
            OnPropertyChanged("FooterStatusLabel");
            OnPropertyChanged("FooterCountLabel");
            OnPropertyChanged("ReadingNavCounterLabel");
            OnPropertyChanged("IsEmpty");
            OnPropertyChanged("EmptyStateTitle");
            OnPropertyChanged("EmptyStateHint");
        }
    }
}
