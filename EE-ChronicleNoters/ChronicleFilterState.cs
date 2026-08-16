using System;

namespace EditableEncyclopedia.ChronicleNoters
{
    public enum SortDirection { Newest, Oldest }

    /// <summary>
    /// Filter state for the chronicle panel — drives which entries appear in the visible list
    /// after the user toggles chips, types in search, or applies a year range. Pure data
    /// struct, no UI dependency. Default state shows ALL entries newest-first.
    /// </summary>
    public struct ChronicleFilterState
    {
        public EventTag ActiveEventTags;
        public EntityTag ActiveEntityTags;
        public bool StarredOnly;
        public string SearchQuery;
        public SortDirection Sort;
        public int? YearFrom;
        public int? YearTo;

        public static ChronicleFilterState Default
        {
            get
            {
                return new ChronicleFilterState
                {
                    ActiveEventTags = EventTag.None,
                    ActiveEntityTags = EntityTag.None,
                    StarredOnly = false,
                    SearchQuery = null,
                    Sort = SortDirection.Newest,
                    YearFrom = null,
                    YearTo = null,
                };
            }
        }

        public bool HasActiveFilters
        {
            get
            {
                return ActiveEventTags != EventTag.None
                    || ActiveEntityTags != EntityTag.None
                    || StarredOnly
                    || !string.IsNullOrEmpty(SearchQuery)
                    || YearFrom.HasValue
                    || YearTo.HasValue;
            }
        }
    }

    /// <summary>
    /// Predicate that decides whether a given chronicle entry passes the current filter.
    /// Kept as a static method (rather than instance) so callers can use `in` to avoid
    /// copying the filter struct in hot iteration loops.
    /// </summary>
    public static class ChronicleFilterMatcher
    {
        public static bool Matches(
            in ChronicleFilterState f,
            EventTag eventTags, EntityTag entityTags,
            bool starred, int year,
            string title, string body,
            string entityName, string clanName, string kingdomName)
        {
            // Event-dimension chip filter: any active chip → entry must include at least one matching tag.
            if (f.ActiveEventTags != EventTag.None && (eventTags & f.ActiveEventTags) == EventTag.None) return false;
            // Entity-dimension chip filter: same rule.
            if (f.ActiveEntityTags != EntityTag.None && (entityTags & f.ActiveEntityTags) == EntityTag.None) return false;
            if (f.StarredOnly && !starred) return false;
            if (f.YearFrom.HasValue && year < f.YearFrom.Value) return false;
            if (f.YearTo.HasValue && year > f.YearTo.Value) return false;
            if (!string.IsNullOrEmpty(f.SearchQuery))
            {
                string q = f.SearchQuery;
                bool hit = Contains(title, q) || Contains(body, q)
                        || Contains(entityName, q) || Contains(clanName, q) || Contains(kingdomName, q);
                if (!hit) return false;
            }
            return true;
        }

        private static bool Contains(string s, string q)
        {
            return s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
