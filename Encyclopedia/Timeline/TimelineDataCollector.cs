using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Collects timeline entries from three sources for a given hero:
    /// 1. Hero's own journal entries
    /// 2. Cross-referenced journal entries from other objects mentioning this hero
    /// 3. Native Bannerlord campaign log entries
    ///
    /// Includes a per-hero cache to avoid re-scanning all journals on every page refresh.
    /// Cache is invalidated when the hero ID changes or after a configurable TTL.
    /// </summary>
    public static class TimelineDataCollector
    {
        // Cache: last collected entries per hero, with timestamp for TTL
        private static string _cachedHeroId;
        private static List<TimelineEntry> _cachedEntries;
        private static int _cachedEntryCount; // journal entry count at cache time
        private static long _cacheTimeTicks;
        private const long CacheTtlTicks = 30 * TimeSpan.TicksPerSecond; // 30s cache

        // Cross-reference index: heroId → set of objectIds whose journals mention that hero.
        // Built once per session, invalidated when journal count changes.
        private static Dictionary<string, HashSet<string>> _crossRefIndex;
        private static int _crossRefJournalCount;

        /// <summary>
        /// Clears all cached data. Call on campaign change or save load.
        /// </summary>
        public static void ClearCache()
        {
            _cachedHeroId = null;
            _cachedEntries = null;
            _cachedEntryCount = 0;
            _cacheTimeTicks = 0;
            _crossRefIndex = null;
            _crossRefJournalCount = 0;
        }

        /// <summary>
        /// Collects all timeline-worthy entries for the given hero, sorted chronologically.
        /// Uses caching to avoid repeated expensive scans.
        /// </summary>
        public static List<TimelineEntry> CollectTimelineEntries(string heroId)
        {
            return CollectEntityTimelineEntries(heroId, true);
        }

        public static List<TimelineEntry> CollectEntityTimelineEntries(string entityId, bool includeNativeLog = false)
        {
            var behavior = EncyclopediaEditBehavior.Instance;
            if (behavior == null) return new List<TimelineEntry>();

            int totalCount = behavior.GetJournalCount();
            long now = DateTime.UtcNow.Ticks;

            if (_cachedHeroId == entityId && _cachedEntries != null
                && _cachedEntryCount == totalCount
                && (now - _cacheTimeTicks) < CacheTtlTicks)
            {
                return _cachedEntries;
            }

            var allJournal = behavior.GetAllJournalForExport();

            var entries = new List<TimelineEntry>();
            string entityMarker = "\u00ABh:" + entityId + "\u00BB";

            // Source 1: Entity's own journal entries
            CollectOwnJournalEntries(behavior, entityId, entries);
            int afterOwn = entries.Count;

            // Source 2: Cross-referenced journal entries
            CollectCrossReferencedEntries(behavior, entityId, entityMarker, allJournal, entries);
            int afterXref = entries.Count;

            // Source 3: Native Bannerlord campaign log entries (heroes only)
            if (includeNativeLog)
                CollectNativeLogEntries(entityId, entries);

            MCMSettings.DebugLog("Timeline collect for " + entityId + ": own=" + afterOwn
                + " xref=" + (afterXref - afterOwn) + " native=" + (entries.Count - afterXref)
                + " total=" + entries.Count);

            foreach (var e in entries)
                e.SortKey = TimelineTextProcessor.ComputeSortKey(e.Date);
            entries.Sort((a, b) => a.SortKey.CompareTo(b.SortKey));

            _cachedHeroId = entityId;
            _cachedEntries = entries;
            _cachedEntryCount = totalCount;
            _cacheTimeTicks = now;

            return entries;
        }

        private static void CollectOwnJournalEntries(EncyclopediaEditBehavior behavior,
            string heroId, List<TimelineEntry> entries)
        {
            var ownEntries = behavior.GetJournalEntries(heroId);
            for (int idx = 0; idx < ownEntries.Count; idx++)
            {
                var e = ownEntries[idx];
                if (!TimelineTextProcessor.IsTimelineWorthy(e.Text)) continue;

                string strippedText = TimelineTextProcessor.StripSlainTag(e.Text);
                var (icon, category) = TimelineTextProcessor.DetectIconAndCategory(strippedText);
                string rawText = TimelineTextProcessor.CleanEntryText(strippedText);
                string cleanText = TimelineTextProcessor.CleanEntryText(
                    TimelineTextProcessor.StripMarkersForDisplay(e.Text));
                if (string.IsNullOrWhiteSpace(cleanText)) continue;

                string simpleDate = TimelineTextProcessor.SimplifyDate(e.Date);

                // Skip duplicates within own journal entries
                if (TimelineTextProcessor.IsDuplicate(entries, simpleDate, cleanText)) continue;

                // Detect manual entries: no category tag = player-written
                string lower = e.Text.ToLowerInvariant();
                bool isManual = !lower.StartsWith("[war]") && !lower.StartsWith("[politics]")
                    && !lower.StartsWith("[crime]") && !lower.StartsWith("[family]")
                    && !lower.StartsWith("[other]");

                entries.Add(new TimelineEntry
                {
                    Date = simpleDate,
                    Text = cleanText,
                    RawText = rawText,
                    Icon = icon,
                    Category = category,
                    SourceObjectId = heroId,
                    IsManualEntry = isManual,
                    OriginalJournalIndex = isManual ? idx : -1
                });
            }
        }

        /// <summary>
        /// Builds a cross-reference index mapping heroId → set of objectIds whose journals
        /// mention that hero. Only rebuilt when journal count changes.
        /// </summary>
        private static void EnsureCrossRefIndex(Dictionary<string, string> allJournal)
        {
            if (_crossRefIndex != null && _crossRefJournalCount == allJournal.Count)
                return;

            _crossRefIndex = new Dictionary<string, HashSet<string>>();
            _crossRefJournalCount = allJournal.Count;

            foreach (var kvp in allJournal)
            {
                string objectId = kvp.Key;
                string journalText = kvp.Value;
                if (string.IsNullOrEmpty(journalText)) continue;

                // Scan for all hero markers «h:heroId» in this journal
                int pos = 0;
                string prefix = "\u00ABh:";
                string suffix = "\u00BB";
                while (pos < journalText.Length)
                {
                    int start = journalText.IndexOf(prefix, pos, StringComparison.Ordinal);
                    if (start < 0) break;

                    int idStart = start + prefix.Length;
                    int idEnd = journalText.IndexOf(suffix, idStart, StringComparison.Ordinal);
                    if (idEnd < 0) break;

                    string mentionedHeroId = journalText.Substring(idStart, idEnd - idStart);
                    if (!string.IsNullOrEmpty(mentionedHeroId) && mentionedHeroId != objectId)
                    {
                        if (!_crossRefIndex.TryGetValue(mentionedHeroId, out var objectIds))
                        {
                            objectIds = new HashSet<string>();
                            _crossRefIndex[mentionedHeroId] = objectIds;
                        }
                        objectIds.Add(objectId);
                    }

                    pos = idEnd + 1;
                }
            }
        }

        private static void CollectCrossReferencedEntries(EncyclopediaEditBehavior behavior,
            string heroId, string heroMarker, Dictionary<string, string> allJournal,
            List<TimelineEntry> entries)
        {
            // Build/refresh the cross-reference index
            EnsureCrossRefIndex(allJournal);

            // Only scan journals that are known to mention this hero
            HashSet<string> relevantObjectIds;
            if (!_crossRefIndex.TryGetValue(heroId, out relevantObjectIds))
                return; // No cross-references for this hero

            foreach (string objectId in relevantObjectIds)
            {
                string journalText;
                if (!allJournal.TryGetValue(objectId, out journalText)) continue;
                if (string.IsNullOrEmpty(journalText)) continue;

                foreach (var line in journalText.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!line.Contains(heroMarker)) continue;

                    int sep = line.IndexOf('|');
                    string date = sep > 0 ? line.Substring(0, sep) : "";
                    string text = sep > 0 ? line.Substring(sep + 1) : line;

                    if (!TimelineTextProcessor.IsTimelineWorthy(text)) continue;

                    string strippedText = TimelineTextProcessor.StripSlainTag(text);
                    string rawText = TimelineTextProcessor.CleanEntryText(strippedText);
                    string cleanText = TimelineTextProcessor.CleanEntryText(
                        TimelineTextProcessor.StripMarkersForDisplay(text));
                    if (string.IsNullOrWhiteSpace(cleanText)) continue;
                    string simpleDate = TimelineTextProcessor.SimplifyDate(date);

                    if (TimelineTextProcessor.IsDuplicate(entries, simpleDate, cleanText)) continue;

                    var (icon, category) = TimelineTextProcessor.DetectIconAndCategory(strippedText);
                    entries.Add(new TimelineEntry
                    {
                        Date = simpleDate,
                        Text = cleanText,
                        RawText = rawText,
                        Icon = icon,
                        Category = category,
                        SourceObjectId = objectId,
                        IsManualEntry = false
                    });
                }
            }
        }

        /// <summary>
        /// Collects native Bannerlord campaign log entries for the viewed hero.
        /// Uses cached reflection via TimelineReflectionCache for performance.
        /// </summary>
        private static void CollectNativeLogEntries(string heroId, List<TimelineEntry> entries)
        {
            try
            {
                var campaign = Campaign.Current;
                if (campaign == null) return;

                Hero viewedHero = Hero.FindFirst(h => h.StringId == heroId);
                if (viewedHero == null) return;

                object logHistory = TimelineReflectionCache.GetLogEntryHistory(campaign);
                if (logHistory == null) return;

                var logEntries = TimelineReflectionCache.GetLogEntries(logHistory);
                if (logEntries == null) return;

                int added = 0;
                foreach (object logEntry in logEntries)
                {
                    if (logEntry == null) continue;
                    try
                    {
                        string typeName = logEntry.GetType().Name.ToLowerInvariant();

                        // Skip spam/noise entry types
                        if (typeName.Contains("relation") && !typeName.Contains("vengeance")) continue;
                        if (typeName.Contains("persuasion")) continue;
                        if (typeName.Contains("barter")) continue;
                        if (typeName.Contains("conversation")) continue;
                        if (typeName.Contains("influence")) continue;
                        if (typeName.Contains("companion") && typeName.Contains("added")) continue;

                        // Skip world/political events
                        if (typeName.Contains("wardeclare") || typeName.Contains("war_declare")) continue;
                        if (typeName.Contains("peacedeclare") || typeName.Contains("peace") || typeName.Contains("makepeace")) continue;
                        if (typeName.Contains("kingdomdestroy") || typeName.Contains("kingdom_destroy")) continue;
                        if (typeName.Contains("clanchangekingdom") || typeName.Contains("clan_change")) continue;
                        if (typeName.Contains("rulerchange") || typeName.Contains("ruler_change")) continue;

                        if (!TimelineReflectionCache.IsHeroPrimaryActor(logEntry, viewedHero)) continue;

                        string text = TimelineReflectionCache.GetLogEntryText(logEntry);
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        string date = TimelineReflectionCache.GetLogEntryDate(logEntry);
                        string simpleDate = TimelineTextProcessor.SimplifyDate(date);

                        string cleanText = TimelineTextProcessor.StripHtmlTags(text);
                        if (string.IsNullOrWhiteSpace(cleanText)) continue;

                        if (TimelineTextProcessor.IsDuplicate(entries, simpleDate, cleanText)) continue;

                        var (icon, category) = TimelineTextProcessor.DetectIconAndCategory(cleanText);
                        if (category == TimelineCategory.Event)
                        {
                            var (typeIcon, typeCat) = TimelineTextProcessor.DetectFromTypeName(typeName);
                            icon = typeIcon;
                            category = typeCat;
                        }

                        entries.Add(new TimelineEntry
                        {
                            Date = simpleDate,
                            Text = cleanText,
                            RawText = text,
                            Icon = icon,
                            Category = category,
                            SourceObjectId = heroId,
                            IsManualEntry = false
                        });
                        added++;
                    }
                    catch (Exception entryEx)
                    {
                        MCMSettings.DebugLog("TimelineDataCollector: log entry error: " + entryEx.Message);
                    }
                }
                MCMSettings.DebugLog("TimelineDataCollector: added " + added + " native log entries for " + heroId);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("TimelineDataCollector: native log error: " + ex.ToString());
            }
        }
    }
}
