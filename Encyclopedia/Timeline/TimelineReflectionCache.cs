using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Caches reflection lookups for native Bannerlord campaign log access.
    /// Avoids repeated expensive reflection scans on every timeline injection.
    /// Thread-safe: cache is populated once and read-only afterward.
    /// </summary>
    public static class TimelineReflectionCache
    {
        private const int MinToStringLength = 5;

        private static readonly BindingFlags AllFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // Cached accessors for LogEntryHistory
        private static bool _logHistoryResolved;
        private static MemberInfo _logHistoryMember; // PropertyInfo or FieldInfo
        private static bool _logHistoryIsProperty;

        // Cached accessor for the log entries collection within the history object
        private static bool _logEntriesResolved;
        private static MemberInfo _logEntriesMember;
        private static bool _logEntriesIsProperty;
        private static bool _logEntriesIsSelf; // history object itself is IEnumerable

        // Cached hero field lookups per LogEntry type
        private static readonly Dictionary<Type, CachedHeroFields> _heroFieldsCache
            = new Dictionary<Type, CachedHeroFields>();

        // Cached GetEncyclopediaText / GetNotificationText method per type
        private static readonly Dictionary<Type, MethodInfo> _textMethodCache
            = new Dictionary<Type, MethodInfo>();

        // Cached GameTime property per type
        private static readonly Dictionary<Type, PropertyInfo> _timePropertyCache
            = new Dictionary<Type, PropertyInfo>();

        /// <summary>
        /// Clears all cached reflection data. Call when campaign changes.
        /// </summary>
        public static void ClearCache()
        {
            _logHistoryResolved = false;
            _logHistoryMember = null;
            _logEntriesResolved = false;
            _logEntriesMember = null;
            _logEntriesIsSelf = false;
            lock (_heroFieldsCache) _heroFieldsCache.Clear();
            lock (_textMethodCache) _textMethodCache.Clear();
            lock (_timePropertyCache) _timePropertyCache.Clear();
        }

        /// <summary>
        /// Gets the LogEntryHistory object from Campaign.Current using cached reflection.
        /// Returns null if not found.
        /// </summary>
        public static object GetLogEntryHistory(Campaign campaign)
        {
            if (campaign == null) return null;

            if (!_logHistoryResolved)
            {
                _logHistoryResolved = true;
                var campaignType = campaign.GetType();

                foreach (string name in new[] { "LogEntryHistory", "_logEntryHistory", "logEntryHistory" })
                {
                    var prop = campaignType.GetProperty(name, AllFlags);
                    if (prop != null)
                    {
                        _logHistoryMember = prop;
                        _logHistoryIsProperty = true;
                        MCMSettings.DebugLog("TimelineReflectionCache: cached LogEntryHistory via property " + name);
                        break;
                    }
                    var field = campaignType.GetField(name, AllFlags);
                    if (field != null)
                    {
                        _logHistoryMember = field;
                        _logHistoryIsProperty = false;
                        MCMSettings.DebugLog("TimelineReflectionCache: cached LogEntryHistory via field " + name);
                        break;
                    }
                }
            }

            if (_logHistoryMember == null) return null;

            try
            {
                return _logHistoryIsProperty
                    ? ((PropertyInfo)_logHistoryMember).GetValue(campaign)
                    : ((FieldInfo)_logHistoryMember).GetValue(campaign);
            }
            catch (Exception ex) { MCMSettings.DebugLog("[TimelineReflectionCache]: " + ex.ToString()); return null; }
        }

        /// <summary>
        /// Gets the log entries IEnumerable from a LogEntryHistory object using cached reflection.
        /// </summary>
        public static System.Collections.IEnumerable GetLogEntries(object logHistory)
        {
            if (logHistory == null) return null;

            if (!_logEntriesResolved)
            {
                _logEntriesResolved = true;
                var histType = logHistory.GetType();

                // Try known property/field names first
                string[] knownNames = { "GameActionLogs", "GameLogs", "Logs", "AllLogs",
                    "_gameActionLogs", "_gameLogs", "_logs", "_allLogs" };

                foreach (string propName in knownNames)
                {
                    var prop = histType.GetProperty(propName, AllFlags);
                    if (prop != null)
                    {
                        var val = prop.GetValue(logHistory) as System.Collections.IEnumerable;
                        if (val != null)
                        {
                            _logEntriesMember = prop;
                            _logEntriesIsProperty = true;
                            MCMSettings.DebugLog("TimelineReflectionCache: cached entries via property " + propName);
                            return val;
                        }
                    }
                    var field = histType.GetField(propName, AllFlags);
                    if (field != null)
                    {
                        var val = field.GetValue(logHistory) as System.Collections.IEnumerable;
                        if (val != null)
                        {
                            _logEntriesMember = field;
                            _logEntriesIsProperty = false;
                            MCMSettings.DebugLog("TimelineReflectionCache: cached entries via field " + propName);
                            return val;
                        }
                    }
                }

                // Scan all properties for IEnumerable
                foreach (var prop in histType.GetProperties(AllFlags))
                {
                    try
                    {
                        if (prop.GetIndexParameters().Length > 0) continue;
                        var val = prop.GetValue(logHistory) as System.Collections.IEnumerable;
                        if (val != null && !(val is string))
                        {
                            _logEntriesMember = prop;
                            _logEntriesIsProperty = true;
                            MCMSettings.DebugLog("TimelineReflectionCache: cached entries via scan property " + prop.Name);
                            return val;
                        }
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("[TimelineReflectionCache]: " + ex.ToString()); }
                }

                // Scan all fields
                foreach (var field in histType.GetFields(AllFlags))
                {
                    try
                    {
                        var val = field.GetValue(logHistory) as System.Collections.IEnumerable;
                        if (val != null && !(val is string))
                        {
                            _logEntriesMember = field;
                            _logEntriesIsProperty = false;
                            MCMSettings.DebugLog("TimelineReflectionCache: cached entries via scan field " + field.Name);
                            return val;
                        }
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("[TimelineReflectionCache]: " + ex.ToString()); }
                }

                // Self-enumerable fallback
                if (logHistory is System.Collections.IEnumerable selfEnum)
                {
                    _logEntriesIsSelf = true;
                    return selfEnum;
                }

                MCMSettings.DebugLog("TimelineReflectionCache: no log entries found on " + histType.Name);
                return null;
            }

            // Use cached accessor
            if (_logEntriesIsSelf) return logHistory as System.Collections.IEnumerable;
            if (_logEntriesMember == null) return null;

            try
            {
                return _logEntriesIsProperty
                    ? ((PropertyInfo)_logEntriesMember).GetValue(logHistory) as System.Collections.IEnumerable
                    : ((FieldInfo)_logEntriesMember).GetValue(logHistory) as System.Collections.IEnumerable;
            }
            catch (Exception ex) { MCMSettings.DebugLog("[TimelineReflectionCache]: " + ex.ToString()); return null; }
        }

        /// <summary>
        /// Gets the display text from a native LogEntry using cached method lookup.
        /// </summary>
        public static string GetLogEntryText(object logEntry)
        {
            var type = logEntry.GetType();

            MethodInfo textMethod;
            bool needsLookup;
            lock (_textMethodCache)
                needsLookup = !_textMethodCache.TryGetValue(type, out textMethod);

            if (needsLookup)
            {
                // Find the best text method
                foreach (string methodName in new[] { "GetEncyclopediaText", "GetNotificationText" })
                {
                    try
                    {
                        var method = type.GetMethod(methodName, AllFlags, null, Type.EmptyTypes, null);
                        if (method != null)
                        {
                            textMethod = method;
                            break;
                        }
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("[TimelineReflectionCache]: " + ex.ToString()); }
                }
                lock (_textMethodCache)
                    _textMethodCache[type] = textMethod; // may be null — that's cached too
            }

            if (textMethod != null)
            {
                try
                {
                    var result = textMethod.Invoke(logEntry, null);
                    if (result != null)
                    {
                        string text = result.ToString();
                        if (!string.IsNullOrWhiteSpace(text) && text != result.GetType().FullName)
                            return text.Trim();
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("[TimelineReflectionCache]: " + ex.ToString()); }
            }

            // Fallback: try TextObject properties
            foreach (string propName in new[] { "EncyclopediaText", "NotificationText", "Description" })
            {
                try
                {
                    var prop = type.GetProperty(propName, AllFlags);
                    if (prop != null)
                    {
                        var val = prop.GetValue(logEntry);
                        if (val != null)
                        {
                            string text = val.ToString();
                            if (!string.IsNullOrWhiteSpace(text) && text != val.GetType().FullName)
                                return text.Trim();
                        }
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("[TimelineReflectionCache]: " + ex.ToString()); }
            }

            // Last resort: ToString
            try
            {
                string str = logEntry.ToString();
                if (!string.IsNullOrWhiteSpace(str) && !str.Contains("LogEntry")
                    && !str.Contains("TaleWorlds") && str.Length > MinToStringLength)
                    return str.Trim();
            }
            catch (Exception ex) { MCMSettings.DebugLog("[TimelineReflectionCache]: " + ex.ToString()); }

            return null;
        }

        /// <summary>
        /// Gets the date string from a native LogEntry using cached property lookup.
        /// </summary>
        public static string GetLogEntryDate(object logEntry)
        {
            var type = logEntry.GetType();

            PropertyInfo timeProp;
            bool needsLookup;
            lock (_timePropertyCache)
                needsLookup = !_timePropertyCache.TryGetValue(type, out timeProp);

            if (needsLookup)
            {
                foreach (string propName in new[] { "GameTime", "CreationTime", "EventTime" })
                {
                    var prop = type.GetProperty(propName, AllFlags);
                    if (prop != null)
                    {
                        timeProp = prop;
                        break;
                    }
                }
                lock (_timePropertyCache)
                    _timePropertyCache[type] = timeProp;
            }

            if (timeProp == null) return "";

            try
            {
                var val = timeProp.GetValue(logEntry);
                if (val == null) return "";

                if (val is CampaignTime ct)
                    return FormatCampaignTime(ct);

                // Reflection fallback for CampaignTime-like structs
                var yearProp = val.GetType().GetProperty("GetYear", AllFlags);
                var seasonProp = val.GetType().GetProperty("GetSeasonOfYear", AllFlags);
                var dayProp = val.GetType().GetProperty("GetDayOfSeason", AllFlags);
                if (yearProp != null && seasonProp != null && dayProp != null)
                {
                    int year = (int)yearProp.GetValue(val);
                    int season = (int)seasonProp.GetValue(val);
                    int day = (int)dayProp.GetValue(val) + 1;
                    string[] seasonNames = { "Spring", "Summer", "Autumn", "Winter" };
                    string seasonName = (season >= 0 && season < seasonNames.Length) ? seasonNames[season] : "Unknown";
                    return "Day " + day + " of " + seasonName + ", " + year;
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("[TimelineReflectionCache]: " + ex.ToString()); }

            return "";
        }

        private static string FormatCampaignTime(CampaignTime ct)
        {
            try
            {
                int year = ct.GetYear;
                int season = (int)ct.GetSeasonOfYear;
                int day = ct.GetDayOfSeason + 1;
                string[] seasonNames = { "Spring", "Summer", "Autumn", "Winter" };
                string seasonName = (season >= 0 && season < seasonNames.Length) ? seasonNames[season] : "Unknown";
                return "Day " + day + " of " + seasonName + ", " + year;
            }
            catch (Exception ex) { MCMSettings.DebugLog("[TimelineReflectionCache]: " + ex.ToString()); return ""; }
        }

        /// <summary>
        /// Checks if the viewed hero is the PRIMARY actor in a native LogEntry.
        /// Uses cached field lookups per LogEntry type for performance.
        /// Improved: includes additional field name variants for better detection.
        /// </summary>
        public static bool IsHeroPrimaryActor(object logEntry, Hero hero)
        {
            var type = logEntry.GetType();
            CachedHeroFields cached;
            bool needsLookup;
            lock (_heroFieldsCache)
                needsLookup = !_heroFieldsCache.TryGetValue(type, out cached);

            if (needsLookup)
            {
                cached = BuildHeroFieldsCache(type);
                lock (_heroFieldsCache)
                    _heroFieldsCache[type] = cached;
            }

            // Check primary fields
            foreach (var accessor in cached.PrimaryFields)
            {
                try
                {
                    Hero found = accessor(logEntry);
                    if (found != null)
                        return found.StringId == hero.StringId;
                }
                catch (Exception ex) { MCMSettings.DebugLog("[TimelineReflectionCache]: " + ex.ToString()); }
            }

            // If any primary field exists on the type, hero isn't the primary actor
            if (cached.HasPrimaryFields) return false;

            // Check secondary fields
            foreach (var accessor in cached.SecondaryFields)
            {
                try
                {
                    Hero found = accessor(logEntry);
                    if (found != null)
                        return found.StringId == hero.StringId;
                }
                catch (Exception ex) { MCMSettings.DebugLog("[TimelineReflectionCache]: " + ex.ToString()); }
            }

            // Dynamic scan: only the FIRST non-null Hero field is the primary actor.
            // This prevents "X sworn vengeance against Y" from appearing on Y's
            // timeline when the first Hero field is X.
            bool anyHeroFound = false;
            foreach (var accessor in cached.DynamicHeroFields)
            {
                try
                {
                    Hero found = accessor(logEntry);
                    if (found != null)
                    {
                        if (!anyHeroFound)
                        {
                            // First hero field = primary actor
                            anyHeroFound = true;
                            if (found.StringId == hero.StringId)
                                return true;
                            else
                                return false; // First hero isn't our target — not primary
                        }
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("[TimelineReflectionCache]: " + ex.ToString()); }
            }

            if (anyHeroFound)
                return false; // Found heroes but first wasn't our target

            // Text fallback
            string text = GetLogEntryText(logEntry);
            if (!string.IsNullOrEmpty(text) && hero.Name != null
                && text.Contains(hero.Name.ToString()))
                return true;

            return false;
        }

        private static CachedHeroFields BuildHeroFieldsCache(Type type)
        {
            var cached = new CachedHeroFields();

            // Extended primary field names — the hero who initiates/performs the action
            string[] primaryNames = {
                "Hero", "Hero1", "MainHero", "InsultingHero",
                "Attacker", "Killer", "Winner", "Initiator",
                "SiegeAttacker", "RaidLeader", "Captor",
                "Challenger", "Leader", "Commander", "Actor",
                "BesiegingHero", "RaidingHero", "ExecutingHero",
                "MarryingHero", "TournamentWinner" };

            // Secondary field names — the hero who is acted upon
            string[] secondaryNames = {
                "Hero2", "OtherHero", "Defender", "Victim",
                "InsultedHero", "Loser", "RelatedHero", "TargetHero",
                "SiegeDefender", "Captive", "Prisoner",
                "Opponent", "Target", "DefendingHero", "DefeatedHero",
                "CapturedHero", "KilledHero", "ExecutedHero",
                "MarriedHero", "TournamentLoser" };

            foreach (string name in primaryNames)
            {
                var accessor = BuildHeroAccessor(type, name);
                if (accessor != null)
                {
                    cached.PrimaryFields.Add(accessor);
                    cached.HasPrimaryFields = true;
                }
            }

            foreach (string name in secondaryNames)
            {
                var accessor = BuildHeroAccessor(type, name);
                if (accessor != null)
                    cached.SecondaryFields.Add(accessor);
            }

            // Only do dynamic scan if no named fields found
            if (!cached.HasPrimaryFields && cached.SecondaryFields.Count == 0)
            {
                foreach (var prop in type.GetProperties(AllFlags))
                {
                    if (prop.PropertyType != typeof(Hero)) continue;
                    if (prop.GetIndexParameters().Length > 0) continue;
                    var localProp = prop;
                    cached.DynamicHeroFields.Add(obj => localProp.GetValue(obj) as Hero);
                }
                foreach (var field in type.GetFields(AllFlags))
                {
                    if (field.FieldType != typeof(Hero)) continue;
                    var localField = field;
                    cached.DynamicHeroFields.Add(obj => localField.GetValue(obj) as Hero);
                }
            }

            return cached;
        }

        private static Func<object, Hero> BuildHeroAccessor(Type type, string name)
        {
            var prop = type.GetProperty(name, AllFlags);
            if (prop != null && (prop.PropertyType == typeof(Hero) || typeof(Hero).IsAssignableFrom(prop.PropertyType)))
                return obj => prop.GetValue(obj) as Hero;

            string camelCaseName = string.IsNullOrEmpty(name) ? "_"
                : name.Length > 1 ? "_" + char.ToLower(name[0]) + name.Substring(1)
                : "_" + char.ToLower(name[0]);
            var field = type.GetField(name, AllFlags)
                     ?? type.GetField(camelCaseName, AllFlags);
            if (field != null && (field.FieldType == typeof(Hero) || typeof(Hero).IsAssignableFrom(field.FieldType)))
                return obj => field.GetValue(obj) as Hero;

            return null;
        }

        private class CachedHeroFields
        {
            public List<Func<object, Hero>> PrimaryFields = new List<Func<object, Hero>>();
            public List<Func<object, Hero>> SecondaryFields = new List<Func<object, Hero>>();
            public List<Func<object, Hero>> DynamicHeroFields = new List<Func<object, Hero>>();
            public bool HasPrimaryFields;
        }
    }
}
