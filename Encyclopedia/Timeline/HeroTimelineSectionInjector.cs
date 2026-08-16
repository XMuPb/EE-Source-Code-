using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ScreenSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Injects a collapsible "Timeline" section into the encyclopedia hero page.
    /// Timeline = personal hero biography. Only shows events where the hero personally
    /// participated: battles, captures, sieges, raids, kills, family, clan changes, tournaments.
    /// World events (war/peace declarations, kingdom politics) are excluded — those belong in Chronicle.
    ///
    /// Data collection is delegated to TimelineDataCollector.
    /// Text processing is delegated to TimelineTextProcessor.
    /// Reflection caching is delegated to TimelineReflectionCache.
    /// </summary>
    public static class HeroTimelineSectionInjector
    {
        private static readonly List<Widget> _injectedWidgets = new List<Widget>();
        private static readonly Dictionary<string, bool> _collapseStates = new Dictionary<string, bool>();

        // Pagination state per hero
        private static readonly Dictionary<string, int> _currentPage = new Dictionary<string, int>();

        // Category filter state per hero (multi-select: set of active categories)
        private static readonly Dictionary<string, HashSet<TimelineCategory>> _activeFilters = new Dictionary<string, HashSet<TimelineCategory>>();

        // Search state per hero
        private static readonly Dictionary<string, string> _searchTerm = new Dictionary<string, string>();

        // Sort order per hero (true = newest first)
        private static readonly Dictionary<string, bool> _sortNewestFirst = new Dictionary<string, bool>();

        // Adaptive column widths
        private const float DateColumnWidthNormal = 120f;
        private const float IconColumnWidthNormal = 100f;
        private const float DateColumnWidthNarrow = 80f;
        private const float IconColumnWidthNarrow = 70f;
        private const float NarrowThreshold = 500f;

        // _pendingHeroId volatile so main thread sees latest payload after
        // observing _retryPending=true (Theme 1 fix: round 3 finding TLC-1).
        private static volatile string _pendingHeroId;
        private static int _retryCount;
        private static System.Threading.Timer _retryTimer;
        private static volatile bool _retryPending;
        private const int MaxRetries = 10;
        private const int RetryDelayMs = 150;
        private const int DefaultPageSize = 25;
        private const int MinTreeDescendants = 50;
        private const int TooltipTextLengthThreshold = 80;
        private const int SearchPreviewLength = 20;
        private const int InitialInjectDelayMs = 600;

        private static readonly BindingFlags AllFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// Schedules widget cleanup to run on the next main-thread tick.
        /// Safe to call from any thread (timer callbacks, etc.).
        /// </summary>
        public static void ScheduleClear()
        {
            DisposeRetryTimer();
            _retryPending = false;
            _clearPending = true;
        }

        private static volatile bool _clearPending;

        public static void TickMainThread()
        {
            if (_clearPending)
            {
                _clearPending = false;
                RemoveOldWidgets();
            }
            if (_retryPending)
            {
                _retryPending = false;
                if (!EncyclopediaPageTracker.IsEncyclopediaOpen())
                {
                    _retryCount = MaxRetries;
                    DisposeRetryTimer();
                    return;
                }
                if (!string.IsNullOrEmpty(_pendingHeroId))
                    DoInject(_pendingHeroId);
            }
        }

        private static void DisposeRetryTimer()
        {
            var t = _retryTimer;
            _retryTimer = null;
            if (t != null)
                try { t.Dispose(); } catch (Exception ex) { MCMSettings.DebugLog("[HeroTimelineSectionInjector]: " + ex.ToString()); }
        }

        public static void InjectTimelineSection(string heroId)
        {
            DisposeRetryTimer();
            _retryPending = false;
            _pendingHeroId = heroId;
            _retryCount = 0;
            // Delay initial inject so Relation Notes section is already present
            _retryTimer = new System.Threading.Timer(_ =>
            {
                _retryPending = true;
            }, null, InitialInjectDelayMs, System.Threading.Timeout.Infinite);
        }

        private static void RemoveOldWidgets()
        {
            foreach (var w in _injectedWidgets)
            {
                try
                {
                    if (w?.ParentWidget != null)
                        w.ParentWidget.RemoveChild(w);
                }
                catch (Exception ex) { MCMSettings.DebugLog("[HeroTimelineSectionInjector]: " + ex.ToString()); }
            }
            _injectedWidgets.Clear();
        }

        private static void DoInject(string heroId)
        {
            try
            {
                RemoveOldWidgets();

                if (EncyclopediaEditBehavior.Instance == null) return;

                // Collect timeline entries — use native log only for hero pages
                string pageType = EncyclopediaPageTracker.CurrentPageType;
                bool isHero = pageType == "Hero";
                var timelineEntries = TimelineDataCollector.CollectEntityTimelineEntries(heroId, isHero);

                var topScreen = ScreenManager.TopScreen;
                if (topScreen == null) return;

                bool layerWasSizeFallback;
                GauntletLayer encLayer = LoreSectionInjector.FindEncyclopediaLayer(topScreen, out layerWasSizeFallback);
                if (encLayer == null)
                {
                    if (_retryCount < MaxRetries)
                    {
                        _retryCount++;
                        // Theme 2 fix: dispose previous timer before reassigning (round 3 finding TLC-2).
                        DisposeRetryTimer();
                        _retryTimer = new System.Threading.Timer(_ =>
                        {
                            _retryPending = true;
                        }, null, RetryDelayMs, System.Threading.Timeout.Infinite);
                    }
                    return;
                }

                Widget layerRoot = LoreSectionInjector.GetLayerRootWidget(encLayer);
                if (layerRoot == null) return;

                UIContext uiContext = layerRoot.EventManager?.Context as UIContext;
                if (uiContext == null) return;

                Widget encWindow = LoreSectionInjector.FindEncyclopediaContentWidget(layerRoot)
                                ?? LoreSectionInjector.FindLargestChild(layerRoot);
                if (encWindow == null) return;

                // Find the sections container by locating RelNotes or other known sections
                Widget parent = null;
                int index = -1;

                if (!FindTimelineInsertionPoint(encWindow, out parent, out index))
                {
                    // Fallback: try from layerRoot
                    if (!FindTimelineInsertionPoint(layerRoot, out parent, out index))
                    {
                        // Try searching ALL GauntletLayers for the timeline insertion point
                        // (the captured layer may not be the one with the encyclopedia content)
                        bool foundInOtherLayer = false;
                        var allLayers = topScreen.Layers;
                        for (int li = 0; li < allLayers.Count; li++)
                        {
                            if (!(allLayers[li] is GauntletLayer gl)) continue;
                            Widget altRoot = LoreSectionInjector.GetLayerRootWidget(gl);
                            if (altRoot == null || altRoot == layerRoot) continue;
                            // Skip non-encyclopedia layers (e.g. Clan/Party panels)
                            if (!LoreSectionInjector.ContainsWidgetType(altRoot, "EncyclopediaDividerButtonWidget")) continue;
                            if (FindTimelineInsertionPoint(altRoot, out parent, out index))
                            {
                                UIContext altCtx = altRoot.EventManager?.Context as UIContext;
                                if (altCtx != null) uiContext = altCtx;
                                MCMSettings.DebugLog("HeroTimeline: found insertion point in alternate layer " + li);
                                foundInOtherLayer = true;
                                break;
                            }
                        }

                        if (!foundInOtherLayer)
                        {
                            // If the widget tree is too small, retry instead of injecting into wrong panel
                            int encDesc = LoreSectionInjector.CountDescendants(encWindow);
                            if (encDesc < MinTreeDescendants && _retryCount < MaxRetries)
                            {
                                _retryCount++;
                                MCMSettings.DebugLog("HeroTimeline: tree too small (descendants=" + encDesc
                                    + "), scheduling retry #" + _retryCount + " in " + RetryDelayMs + "ms");
                                // Theme 2 fix: dispose previous timer before reassigning (round 3 finding TLC-2).
                                DisposeRetryTimer();
                                _retryTimer = new System.Threading.Timer(_ =>
                                {
                                    _retryPending = true;
                                }, null, RetryDelayMs, System.Threading.Timeout.Infinite);
                                return;
                            }
                            if (encDesc < MinTreeDescendants)
                            {
                                // Tree never grew large enough — give up rather than inject into wrong panel
                                MCMSettings.DebugLog("HeroTimeline: tree still too small after max retries, giving up");
                                return;
                            }
                            // Last resort: use generic content panel (only when tree is large enough)
                            parent = LoreSectionInjector.FindMainContentPanel(encWindow) ?? encWindow;
                            index = parent.ChildCount;
                        }
                    }
                }

                BuildAndInsertSection(uiContext, parent, index, heroId, timelineEntries);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("HeroTimeline: inject error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Finds the correct parent container and insertion index for the Timeline section.
        /// Uses the same strategy as RelationNotesSectionInjector: locate known widgets
        /// (RelNotes, Lore, Friends/Enemies) by ID, walk up to the sections container,
        /// then find the exact index to insert after Relation Notes.
        /// </summary>
        private static bool FindTimelineInsertionPoint(Widget root, out Widget parent, out int index)
        {
            parent = null;
            index = -1;

            // Search for known section widgets — prefer RelNotes (we insert after it)
            // For non-hero pages, also look for Journal, Info, Leader, Members, Wars, etc.
            foreach (string sectionName in new[] { "EditableEncyclopedia_RelNotesContent",
                "EditableEncyclopedia_RelNotesDivider", "EditableEncyclopediaJournalDivider",
                "EditableEncyclopediaJournalContent", "EditableEncyclopediaStatsDivider",
                "EditableEncyclopediaStatsContent", "LoreContent", "Lore",
                "Enemies", "Friends", "Companions", "Allies",
                "Leader", "Members", "Settlements", "Clans", "Wars", "Owner" })
            {
                Widget sectionById = LoreSectionInjector.FindWidgetByIdContaining(root, sectionName, 0);
                if (sectionById == null) continue;

                // Walk up to find the sections-level container (same logic as RelationNotes)
                Widget current = sectionById;
                for (int depth = 0; depth < 10 && current != null; depth++)
                {
                    Widget sectionParent = current.ParentWidget;
                    if (sectionParent == null) break;

                    int idx = LoreSectionInjector.FindChildIndex(sectionParent, current);
                    if (idx >= 0 && sectionParent.ChildCount >= 2)
                    {
                        // Check if this is the sections container (siblings have many descendants)
                        int maxSibDesc = 0;
                        for (int si = 0; si < sectionParent.ChildCount; si++)
                        {
                            Widget sib = sectionParent.GetChild(si);
                            if (sib == current) continue;
                            int sd = LoreSectionInjector.CountDescendants(sib);
                            if (sd > maxSibDesc) maxSibDesc = sd;
                        }

                        if (maxSibDesc < 5)
                        {
                            current = sectionParent;
                            continue;
                        }

                        // Found the sections container — now find where to insert
                        parent = sectionParent;
                        index = FindIndexAfterRelNotes(sectionParent);
                        MCMSettings.DebugLog("HeroTimeline: found insertion via '" + sectionName
                            + "' at index " + index + " in " + sectionParent.GetType().Name
                            + " (children=" + sectionParent.ChildCount + ")");
                        return true;
                    }

                    current = sectionParent;
                }
            }

            return false;
        }

        /// <summary>
        /// Within the sections container, finds the index after the last RelNotes widget.
        /// Falls back to after Lore, then before Friends/Enemies, then end.
        /// </summary>
        private static int FindIndexAfterRelNotes(Widget parent)
        {
            int lastRelNotes = -1;
            int lastLore = -1;
            int lastJournal = -1;
            int lastInfoStats = -1;
            int lastInfoSection = -1;
            int firstNativeSection = -1;
            string pageType = EncyclopediaPageTracker.CurrentPageType;
            bool isHero = pageType == "Hero";

            for (int i = 0; i < parent.ChildCount; i++)
            {
                var child = parent.GetChild(i);
                if (child == null) continue;

                string childId = child.Id;

                // Check our injected widgets by ID
                if (childId != null)
                {
                    if (childId.StartsWith("EditableEncyclopedia_RelNotes"))
                    { lastRelNotes = i; continue; }
                    if (childId.Contains("Lore"))
                    { lastLore = i; continue; }
                    if (childId.StartsWith("EditableEncyclopediaJournal"))
                    { lastJournal = i; continue; }
                    if (childId.StartsWith("EditableEncyclopediaStats"))
                    { lastInfoStats = i; continue; }
                }

                // Detect native "Info" section by checking for EncyclopediaDividerButtonWidget
                // with "Info" text content, or by type name containing "Divider"
                if (!isHero && lastInfoSection < 0)
                {
                    string typeName = child.GetType().Name;
                    if (typeName.Contains("DividerButton") || typeName.Contains("Divider"))
                    {
                        // This is a collapsible section header — check its text
                        string headerText = FindTextInChildren(child);
                        if (!string.IsNullOrEmpty(headerText) &&
                            headerText.IndexOf("Info", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // Found the native Info section — scan forward to find its content
                            // The Info content is the next sibling(s) until the next section header
                            lastInfoSection = i;
                            for (int j = i + 1; j < parent.ChildCount; j++)
                            {
                                var nextChild = parent.GetChild(j);
                                if (nextChild == null) break;
                                string nextType = nextChild.GetType().Name;
                                if (nextType.Contains("DividerButton") || nextType.Contains("Divider"))
                                    break;
                                lastInfoSection = j;
                            }
                            continue;
                        }
                    }
                }

                // Detect native sections (Leader, Members, Settlements, Wars, Owner, etc.)
                if (firstNativeSection < 0)
                {
                    bool isNative = false;
                    if (childId != null)
                    {
                        foreach (string name in new[] { "Friends", "Enemies", "Companions", "Allies",
                            "AlliesDivider", "EnemiesDivider", "FamilyDivider",
                            "Leader", "Members", "Settlements", "Clans", "Wars",
                            "Owner", "Notable" })
                        {
                            if (childId.Contains(name)) { isNative = true; break; }
                        }
                    }
                    if (!isNative && !isHero)
                    {
                        string text = FindTextInChildren(child);
                        if (!string.IsNullOrEmpty(text))
                        {
                            foreach (string header in new[] { "Leader", "Members", "Settlements", "Clans",
                                "Wars", "Owner", "Notable", "Notables", "Villages" })
                            {
                                if (text.IndexOf(header, StringComparison.OrdinalIgnoreCase) >= 0)
                                { isNative = true; break; }
                            }
                        }
                    }
                    if (isNative) firstNativeSection = i;
                }
            }

            // Hero pages: after Lore > after Relation Notes > before Friends/Enemies > end
            // Timeline goes between Lore and Relation Notes
            if (isHero)
            {
                if (lastLore >= 0) return lastLore + 1;
                if (lastRelNotes >= 0) return lastRelNotes + 1;
                if (firstNativeSection >= 0) return firstNativeSection;
                return parent.ChildCount;
            }

            // Non-hero pages: after Lore > after Journal > after Info stats > after native Info section > before native sections > end
            if (lastLore >= 0) return lastLore + 1;
            if (lastJournal >= 0) return lastJournal + 1;
            if (lastInfoStats >= 0) return lastInfoStats + 1;
            if (lastInfoSection >= 0) return lastInfoSection + 1;
            if (firstNativeSection >= 0) return firstNativeSection;
            return parent.ChildCount;
        }

        private static string FindTextInChildren(Widget widget)
        {
            if (widget == null) return null;
            string text = LoreSectionInjector.TryGetText(widget);
            if (!string.IsNullOrEmpty(text)) return text;
            // Search 2 levels deep: DividerButton > ListPanel > TextWidget
            for (int i = 0; i < widget.ChildCount && i < 8; i++)
            {
                var child = widget.GetChild(i);
                text = LoreSectionInjector.TryGetText(child);
                if (!string.IsNullOrEmpty(text)) return text;
                if (child != null)
                {
                    for (int j = 0; j < child.ChildCount && j < 8; j++)
                    {
                        text = LoreSectionInjector.TryGetText(child.GetChild(j));
                        if (!string.IsNullOrEmpty(text)) return text;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Applies active category filters, search term, and sort order to the full entry list.
        /// </summary>
        private static List<TimelineEntry> ApplyFilters(string heroId, List<TimelineEntry> allEntries)
        {
            HashSet<TimelineCategory> filters = null;
            _activeFilters.TryGetValue(heroId, out filters);
            string search = null;
            _searchTerm.TryGetValue(heroId, out search);

            bool hasFilter = filters != null && filters.Count > 0 && !filters.Contains(TimelineCategory.All);
            bool hasSearch = !string.IsNullOrEmpty(search);

            List<TimelineEntry> result;
            if (!hasFilter && !hasSearch)
            {
                result = new List<TimelineEntry>(allEntries);
            }
            else
            {
                result = new List<TimelineEntry>();
                foreach (var e in allEntries)
                {
                    if (hasFilter && !filters.Contains(e.Category))
                        continue;
                    if (hasSearch
                        && e.Text.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0
                        && (e.Date == null || e.Date.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0))
                        continue;
                    result.Add(e);
                }
            }

            // Apply sort order
            bool newestFirst;
            if (_sortNewestFirst.TryGetValue(heroId, out newestFirst) && newestFirst)
                result.Reverse();

            return result;
        }

        /// <summary>
        /// Gets categories with their counts from the full entry list, for building filter buttons.
        /// </summary>
        private static Dictionary<TimelineCategory, int> GetCategoryCounts(List<TimelineEntry> entries)
        {
            var counts = new Dictionary<TimelineCategory, int>();
            foreach (var e in entries)
            {
                int c;
                counts.TryGetValue(e.Category, out c);
                counts[e.Category] = c + 1;
            }
            return counts;
        }

        /// <summary>
        /// Gets the effective page size from MCM settings.
        /// </summary>
        private static int GetPageSize()
        {
            var settings = MCMSettings.Instance;
            if (settings != null)
            {
                try
                {
                    var prop = settings.GetType().GetProperty("TimelinePageSize", AllFlags);
                    if (prop != null)
                    {
                        int val = (int)prop.GetValue(settings);
                        if (val > 0) return val;
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("[HeroTimelineSectionInjector]: " + ex.ToString()); }
            }
            return DefaultPageSize;
        }

        /// <summary>
        /// Checks if timeline is enabled in MCM settings.
        /// </summary>
        public static bool IsEnabled()
        {
            var settings = MCMSettings.Instance;
            if (settings != null)
            {
                try
                {
                    var prop = settings.GetType().GetProperty("EnableTimeline", AllFlags);
                    if (prop != null)
                        return (bool)prop.GetValue(settings);
                }
                catch (Exception ex) { MCMSettings.DebugLog("[HeroTimelineSectionInjector]: " + ex.ToString()); }
            }
            return true; // default enabled
        }

        /// <summary>
        /// Detects whether the encyclopedia panel is narrow (sidebar mode).
        /// </summary>
        private static bool IsNarrowPanel(Widget parent)
        {
            try
            {
                if (parent != null && parent.SuggestedWidth > 0 && parent.SuggestedWidth < NarrowThreshold)
                    return true;
            }
            catch (Exception ex) { MCMSettings.DebugLog("[HeroTimelineSectionInjector]: " + ex.ToString()); }
            return false;
        }

        private static void BuildAndInsertSection(UIContext uiContext, Widget parent, int index,
            string heroId, List<TimelineEntry> entries)
        {
            try
            {
                var native = LoreSectionInjector.ExtractNativeSectionParts(parent);

                // Find a small-text brush for entry content
                Brush contentTextBrush = LoreSectionInjector.FindBrushByName(parent, "Encyclopedia.SubPage.History.Text")
                                         ?? LoreSectionInjector.FindBrushByName(parent, "Encyclopedia.Stat.DefinitionText")
                                         ?? LoreSectionInjector.FindBrushByName(parent, "Encyclopedia.Stat.ValueText")
                                         ?? LoreSectionInjector.FindBrushByName(parent, "Encyclopedia.SubPage.Info.Text")
                                         ?? LoreSectionInjector.FindAnyTextBrush(parent);
                MCMSettings.DebugLog("HeroTimeline: contentTextBrush=" + (contentTextBrush?.Name ?? "null"));

                // === Apply filtering, sorting, and pagination ===
                var categoryCounts = GetCategoryCounts(entries);
                var filteredEntries = ApplyFilters(heroId, entries);
                int totalFiltered = filteredEntries.Count;

                int pageSize = GetPageSize();
                if (!_currentPage.ContainsKey(heroId))
                    _currentPage[heroId] = 0;
                int currentPage = _currentPage[heroId];
                int totalPages = Math.Max(1, (totalFiltered + pageSize - 1) / pageSize);
                if (currentPage >= totalPages) currentPage = totalPages - 1;
                if (currentPage < 0) currentPage = 0;
                _currentPage[heroId] = currentPage;

                int pageStart = currentPage * pageSize;
                int pageEnd = Math.Min(pageStart + pageSize, totalFiltered);
                var pageEntries = totalFiltered > 0
                    ? filteredEntries.GetRange(pageStart, pageEnd - pageStart)
                    : new List<TimelineEntry>();

                // Detect narrow panel for adaptive column widths
                bool narrow = IsNarrowPanel(parent);

                // === Header ===
                Widget headerWrapper = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget")
                                    ?? new Widget(uiContext);
                headerWrapper.Id = "EditableEncyclopedia_TimelineDivider";
                headerWrapper.WidthSizePolicy = SizePolicy.StretchToParent;
                headerWrapper.HeightSizePolicy = SizePolicy.CoverChildren;
                if (native.ReferenceHeader != null)
                {
                    headerWrapper.MarginTop = native.ReferenceHeader.MarginTop;
                    headerWrapper.MarginBottom = native.ReferenceHeader.MarginBottom;
                    headerWrapper.MarginLeft = native.ReferenceHeader.MarginLeft;
                    headerWrapper.MarginRight = native.ReferenceHeader.MarginRight;
                }
                headerWrapper.DoNotAcceptEvents = false;
                headerWrapper.DoNotPassEventsToChildren = true;

                Widget headerBar = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                                ?? new Widget(uiContext);
                headerBar.Id = "EditableEncyclopedia_TimelinePlacement";
                if (native.ReferencePlacement != null)
                {
                    LoreSectionInjector.CopyLayoutProperties(native.ReferencePlacement, headerBar);
                    LoreSectionInjector.CopyOrSetHorizontalLayout(native.ReferencePlacement, headerBar);
                }
                else
                {
                    headerBar.WidthSizePolicy = SizePolicy.StretchToParent;
                    headerBar.HeightSizePolicy = SizePolicy.CoverChildren;
                    LoreSectionInjector.CopyOrSetHorizontalLayout(null, headerBar);
                }

                // Arrow indicator
                BrushWidget arrow = null;
                if (native.NativeIndicator != null)
                {
                    arrow = new BrushWidget(uiContext);
                    arrow.Id = "EditableEncyclopedia_TimelineIndicator";
                    LoreSectionInjector.CopyLayoutProperties(native.NativeIndicator, arrow);
                    if (native.IndicatorBrush != null) arrow.Brush = native.IndicatorBrush;
                    LoreSectionInjector.ForceWidgetBrushState(arrow, native.NativeIndicator);
                    headerBar.AddChild(arrow);
                }

                // Title — show filtered count vs total
                var titleText = new TextWidget(uiContext);
                titleText.Id = "EditableEncyclopedia_TimelineTitle";
                string titleStr = Localization.L("hero_timeline_header");
                if (totalFiltered != entries.Count)
                    titleStr += " (" + totalFiltered + "/" + entries.Count + ")";
                else
                    titleStr += " (" + entries.Count + ")";
                titleText.Text = titleStr;
                if (native.NativeTitle != null)
                {
                    LoreSectionInjector.CopyLayoutProperties(native.NativeTitle, titleText);
                    if (native.HeaderTextBrush != null) titleText.Brush = native.HeaderTextBrush;
                }
                else
                {
                    titleText.WidthSizePolicy = SizePolicy.CoverChildren;
                    titleText.HeightSizePolicy = SizePolicy.CoverChildren;
                    titleText.VerticalAlignment = VerticalAlignment.Center;
                    if (native.HeaderTextBrush != null) titleText.Brush = native.HeaderTextBrush;
                }
                headerBar.AddChild(titleText);

                // Line separator
                if (native.NativeLine != null)
                {
                    Widget line = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ImageWidget")
                               ?? new BrushWidget(uiContext);
                    line.Id = "EditableEncyclopedia_TimelineLine";
                    LoreSectionInjector.CopyLayoutProperties(native.NativeLine, line);
                    if (native.LineBrush != null && line is BrushWidget bwLine)
                        bwLine.Brush = native.LineBrush;
                    headerBar.AddChild(line);
                }

                headerWrapper.AddChild(headerBar);

                // === Content Container ===
                float contentMarginLeft = 25f;
                if (native.NativeIndicator != null)
                    contentMarginLeft = native.NativeIndicator.SuggestedWidth
                        + native.NativeIndicator.MarginLeft + native.NativeIndicator.MarginRight + 5f;

                Widget contentContainer = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                                       ?? new Widget(uiContext);
                contentContainer.Id = "EditableEncyclopedia_TimelineContent";
                contentContainer.WidthSizePolicy = SizePolicy.StretchToParent;
                contentContainer.HeightSizePolicy = SizePolicy.CoverChildren;
                contentContainer.MarginLeft = contentMarginLeft;
                contentContainer.MarginBottom = 5f;
                LoreSectionInjector.SetVerticalLayoutTopToBottom(contentContainer);

                // Thin divider line below header for visual structure
                var dividerLine = new Widget(uiContext);
                dividerLine.Id = "EditableEncyclopedia_TimelineDividerLine";
                dividerLine.WidthSizePolicy = SizePolicy.StretchToParent;
                dividerLine.HeightSizePolicy = SizePolicy.Fixed;
                dividerLine.SuggestedHeight = 1f;
                dividerLine.MarginBottom = 6f;
                dividerLine.MarginTop = 2f;
                try
                {
                    var colorProp = dividerLine.GetType().GetProperty("Color", AllFlags);
                    if (colorProp != null && colorProp.CanWrite)
                        colorProp.SetValue(dividerLine, new Color(0.4f, 0.38f, 0.32f, 0.5f));
                }
                catch (Exception ex) { MCMSettings.DebugLog("[HeroTimelineSectionInjector]: " + ex.ToString()); }
                if (native.LineBrush != null)
                {
                    try
                    {
                        var bwDiv = new BrushWidget(uiContext);
                        bwDiv.Id = "EditableEncyclopedia_TimelineDividerLine";
                        bwDiv.WidthSizePolicy = SizePolicy.StretchToParent;
                        bwDiv.HeightSizePolicy = SizePolicy.Fixed;
                        bwDiv.SuggestedHeight = 1f;
                        bwDiv.MarginBottom = 6f;
                        bwDiv.MarginTop = 2f;
                        bwDiv.Brush = native.LineBrush;
                        contentContainer.AddChild(bwDiv);
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("[HeroTimelineSectionInjector]: " + ex.ToString()); contentContainer.AddChild(dividerLine); }
                }
                else
                {
                    contentContainer.AddChild(dividerLine);
                }

                // === Toolbar: Category filters + Search + Sort + Add Note ===
                BuildToolbar(uiContext, contentContainer, contentTextBrush, heroId, categoryCounts, entries.Count);

                // === Empty state message ===
                if (pageEntries.Count == 0)
                {
                    var emptyLabel = new TextWidget(uiContext);
                    emptyLabel.Id = "EditableEncyclopedia_TimelineEmpty";
                    emptyLabel.Text = entries.Count == 0
                        ? Localization.L("timeline_no_events")
                        : Localization.L("timeline_empty");
                    emptyLabel.WidthSizePolicy = SizePolicy.CoverChildren;
                    emptyLabel.HeightSizePolicy = SizePolicy.CoverChildren;
                    emptyLabel.MarginTop = 8f;
                    emptyLabel.MarginBottom = 8f;
                    if (contentTextBrush != null)
                    {
                        var emptyBrush = contentTextBrush.Clone();
                        ScaleBrushFontSize(emptyBrush, 0.65f, 18);
                        SetBrushColor(emptyBrush, 0.5f, 0.48f, 0.4f, 0.7f);
                        emptyLabel.Brush = emptyBrush;
                    }
                    contentContainer.AddChild(emptyLabel);
                }

                // === Build timeline entries (paginated) ===
                // Adaptive column widths for normal vs narrow sidebar
                float DateColumnWidth = narrow ? DateColumnWidthNarrow : DateColumnWidthNormal;
                float IconColumnWidth = narrow ? IconColumnWidthNarrow : IconColumnWidthNormal;

                // Get active search for highlighting
                string activeSearchForHighlight = null;
                _searchTerm.TryGetValue(heroId, out activeSearchForHighlight);

                for (int i = 0; i < pageEntries.Count; i++)
                {
                    var entry = pageEntries[i];

                    if (i > 0)
                    {
                        var spacer = new Widget(uiContext);
                        spacer.WidthSizePolicy = SizePolicy.StretchToParent;
                        spacer.HeightSizePolicy = SizePolicy.Fixed;
                        spacer.SuggestedHeight = 4f;
                        contentContainer.AddChild(spacer);
                    }

                    int entryIndex = pageStart + i; // global index for IDs

                    // Main row: [Date (fixed-width)] [Icon] [Text]
                    Widget row = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                              ?? new Widget(uiContext);
                    row.Id = "EditableEncyclopedia_TimelineRow_" + entryIndex;
                    row.WidthSizePolicy = SizePolicy.StretchToParent;
                    row.HeightSizePolicy = SizePolicy.CoverChildren;
                    LoreSectionInjector.CopyOrSetHorizontalLayout(null, row);

                    // Date widget — fixed width column for alignment (first)
                    var dateWidget = new TextWidget(uiContext);
                    dateWidget.Id = "EditableEncyclopedia_TimelineDate_" + entryIndex;
                    dateWidget.Text = !string.IsNullOrEmpty(entry.Date) ? entry.Date : "";
                    dateWidget.WidthSizePolicy = SizePolicy.Fixed;
                    dateWidget.SuggestedWidth = DateColumnWidth;
                    dateWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                    dateWidget.VerticalAlignment = VerticalAlignment.Top;
                    dateWidget.MarginRight = 10f;
                    dateWidget.MarginTop = 2f;
                    if (contentTextBrush != null)
                    {
                        var dateBrush = contentTextBrush.Clone();
                        ScaleBrushFontSize(dateBrush, 0.65f, 18);
                        SetBrushColor(dateBrush, 0.6f, 0.55f, 0.4f, 1f);
                        dateWidget.Brush = dateBrush;
                    }
                    row.AddChild(dateWidget);

                    // Icon widget — fixed width for column alignment, color-coded by category
                    var iconWidget = new TextWidget(uiContext);
                    iconWidget.Id = "EditableEncyclopedia_TimelineIcon_" + entryIndex;
                    iconWidget.Text = !string.IsNullOrEmpty(entry.Icon) ? entry.Icon : "-";
                    iconWidget.WidthSizePolicy = SizePolicy.Fixed;
                    iconWidget.SuggestedWidth = IconColumnWidth;
                    iconWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                    iconWidget.VerticalAlignment = VerticalAlignment.Top;
                    iconWidget.HorizontalAlignment = HorizontalAlignment.Center;
                    iconWidget.MarginRight = 4f;
                    iconWidget.MarginTop = 2f;
                    if (contentTextBrush != null)
                    {
                        var iconBrush = contentTextBrush.Clone();
                        ScaleBrushFontSize(iconBrush, 0.7f, 20);
                        // Apply category-specific color
                        var catColor = entry.GetCategoryColor();
                        SetBrushColor(iconBrush, catColor.Red, catColor.Green, catColor.Blue, catColor.Alpha);
                        iconWidget.Brush = iconBrush;
                    }
                    row.AddChild(iconWidget);

                    // Event text — use rich text with clickable hero/settlement links if available
                    Widget textWidget = CreateTimelineTextWidget(uiContext, entry, contentTextBrush, entryIndex, activeSearchForHighlight);
                    row.AddChild(textWidget);

                    // Tooltip for long text entries
                    if (entry.Text != null && entry.Text.Length > TooltipTextLengthThreshold)
                        TrySetTooltip(textWidget, entry.Text);

                    // Edit/Delete buttons for manual (player-written) entries
                    if (entry.IsManualEntry && entry.OriginalJournalIndex >= 0)
                    {
                        Brush btnBrush = null;
                        if (contentTextBrush != null)
                        {
                            btnBrush = contentTextBrush.Clone();
                            ScaleBrushFontSize(btnBrush, 0.6f, 16);
                            SetBrushColor(btnBrush, 0.6f, 0.55f, 0.4f, 0.9f);
                        }

                        // --- Edit button "[Edit]" ---
                        Widget editButton = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget")
                                          ?? new Widget(uiContext);
                        editButton.Id = "EditableEncyclopedia_TimelineEdit_" + entryIndex;
                        editButton.WidthSizePolicy = SizePolicy.CoverChildren;
                        editButton.HeightSizePolicy = SizePolicy.CoverChildren;
                        editButton.DoNotPassEventsToChildren = true;
                        editButton.MarginLeft = 8f;
                        editButton.VerticalAlignment = VerticalAlignment.Center;

                        var editLabel = new TextWidget(uiContext);
                        editLabel.Id = "EditableEncyclopedia_TimelineEditText_" + entryIndex;
                        editLabel.Text = "[Edit]";
                        editLabel.WidthSizePolicy = SizePolicy.CoverChildren;
                        editLabel.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (btnBrush != null) editLabel.Brush = btnBrush.Clone();
                        editButton.AddChild(editLabel);

                        string editHeroId = heroId;
                        int editJournalIdx = entry.OriginalJournalIndex;
                        string editCurrentText = entry.Text;
                        HookWidgetClick(editButton, () =>
                        {
                            EditTimelineEntry(editHeroId, editJournalIdx, editCurrentText);
                        });
                        row.AddChild(editButton);

                        // --- Delete button "[X]" ---
                        Widget deleteButton = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget")
                                            ?? new Widget(uiContext);
                        deleteButton.Id = "EditableEncyclopedia_TimelineDelete_" + entryIndex;
                        deleteButton.WidthSizePolicy = SizePolicy.CoverChildren;
                        deleteButton.HeightSizePolicy = SizePolicy.CoverChildren;
                        deleteButton.DoNotPassEventsToChildren = true;
                        deleteButton.MarginLeft = 4f;
                        deleteButton.VerticalAlignment = VerticalAlignment.Center;

                        var deleteLabel = new TextWidget(uiContext);
                        deleteLabel.Id = "EditableEncyclopedia_TimelineDeleteText_" + entryIndex;
                        deleteLabel.Text = "[X]";
                        deleteLabel.WidthSizePolicy = SizePolicy.CoverChildren;
                        deleteLabel.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (btnBrush != null)
                        {
                            var delBrush = btnBrush.Clone();
                            SetBrushColor(delBrush, 0.7f, 0.3f, 0.3f, 0.9f);
                            deleteLabel.Brush = delBrush;
                        }
                        deleteButton.AddChild(deleteLabel);

                        string delHeroId = heroId;
                        int delJournalIdx = entry.OriginalJournalIndex;
                        HookWidgetClick(deleteButton, () =>
                        {
                            DeleteTimelineEntry(delHeroId, delJournalIdx);
                        });
                        row.AddChild(deleteButton);
                    }

                    contentContainer.AddChild(row);

                    // Commander / event source line (indented under the main row)
                    string commanderName = TimelineTextProcessor.ExtractCommanderName(entry, heroId);
                    if (!string.IsNullOrEmpty(commanderName))
                    {
                        Widget cmdRow = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                                     ?? new Widget(uiContext);
                        cmdRow.Id = "EditableEncyclopedia_TimelineCmd_" + entryIndex;
                        cmdRow.WidthSizePolicy = SizePolicy.StretchToParent;
                        cmdRow.HeightSizePolicy = SizePolicy.CoverChildren;
                        cmdRow.MarginLeft = DateColumnWidth + 10f; // align with icon column (past date)
                        LoreSectionInjector.CopyOrSetHorizontalLayout(null, cmdRow);

                        var cmdLabel = new TextWidget(uiContext);
                        cmdLabel.Text = Localization.L("ui_commander_prefix", commanderName);
                        cmdLabel.WidthSizePolicy = SizePolicy.CoverChildren;
                        cmdLabel.HeightSizePolicy = SizePolicy.CoverChildren;
                        cmdLabel.VerticalAlignment = VerticalAlignment.Center;
                        if (contentTextBrush != null)
                        {
                            var cmdBrush = contentTextBrush.Clone();
                            ScaleBrushFontSize(cmdBrush, 0.55f, 16);
                            SetBrushColor(cmdBrush, 0.5f, 0.5f, 0.45f, 0.8f);
                            cmdLabel.Brush = cmdBrush;
                        }
                        cmdRow.AddChild(cmdLabel);
                        contentContainer.AddChild(cmdRow);
                    }
                }

                // === Pagination controls ===
                if (totalPages > 1)
                {
                    BuildPaginationControls(uiContext, contentContainer, contentTextBrush, heroId, currentPage, totalPages);
                }

                // === Collapse toggle ===
                HookCollapseToggle(headerWrapper, contentContainer, arrow, native.ReferenceHeader, heroId);

                // Apply persisted collapse state (from save file, falling back to in-memory)
                bool isCollapsed = false;
                if (_collapseStates.TryGetValue(heroId, out isCollapsed)) { }
                else if (EncyclopediaEditBehavior.Instance != null)
                    isCollapsed = EncyclopediaEditBehavior.Instance.GetTimelineCollapsed(heroId);
                if (isCollapsed)
                {
                    contentContainer.IsVisible = false;
                    if (arrow != null)
                    {
                        try
                        {
                            var setStateMethod = arrow.GetType().GetMethod("SetState",
                                AllFlags, null, new[] { typeof(string) }, null);
                            setStateMethod?.Invoke(arrow, new object[] { "Collapsed" });
                        }
                        catch (Exception ex) { MCMSettings.DebugLog("[HeroTimelineSectionInjector]: " + ex.ToString()); }
                    }
                }

                // === Insert into parent ===
                // Path 2 defensive validation: bail before inserting if the current
                // page is a Settlement page whose layout has been restructured by
                // a known conflicting mod (Realm of Thrones, etc.). Bug 2026-05-25.
                if (!EncyclopediaAnchorHelper.IsSafeToInjectOnCurrentPage(parent, "Timeline"))
                    return;

                try
                {
                    if (index >= 0 && index <= parent.ChildCount)
                    {
                        parent.AddChildAtIndex(contentContainer, index);
                        parent.AddChildAtIndex(headerWrapper, index);
                    }
                    else
                    {
                        parent.AddChild(headerWrapper);
                        parent.AddChild(contentContainer);
                    }
                }
                catch (Exception ex)
                {
                    MCMSettings.DebugLog("[HeroTimelineSectionInjector]: " + ex.ToString());
                    parent.AddChild(headerWrapper);
                    parent.AddChild(contentContainer);
                }

                _injectedWidgets.Add(headerWrapper);
                _injectedWidgets.Add(contentContainer);
                MCMSettings.DebugLog("HeroTimeline: injected section with " + pageEntries.Count
                    + "/" + totalFiltered + " entries (page " + (currentPage + 1) + "/" + totalPages + ")");
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("HeroTimeline: build error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Builds the toolbar with category filters, sort toggle, search, and Add Note button.
        /// </summary>
        private static void BuildToolbar(UIContext uiContext, Widget container, Brush contentTextBrush,
            string heroId, Dictionary<TimelineCategory, int> categoryCounts, int totalEntries)
        {
            // === Row 1: Category filter buttons ===
            Widget filterRow = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                            ?? new Widget(uiContext);
            filterRow.Id = "EditableEncyclopedia_TimelineFilters";
            filterRow.WidthSizePolicy = SizePolicy.StretchToParent;
            filterRow.HeightSizePolicy = SizePolicy.CoverChildren;
            filterRow.MarginBottom = 4f;
            LoreSectionInjector.CopyOrSetHorizontalLayout(null, filterRow);

            HashSet<TimelineCategory> activeFilters = null;
            _activeFilters.TryGetValue(heroId, out activeFilters);
            bool noFilter = activeFilters == null || activeFilters.Count == 0 || activeFilters.Contains(TimelineCategory.All);

            // "All" button (clears multi-select)
            AddFilterButton(uiContext, filterRow, contentTextBrush, heroId,
                Localization.L("timeline_filter_all") + " (" + totalEntries + ")",
                TimelineCategory.All, noFilter);

            // Category filter buttons with counts — only show categories that have entries
            var categoryOrder = new[]
            {
                TimelineCategory.Battle, TimelineCategory.Siege, TimelineCategory.Capture,
                TimelineCategory.Death, TimelineCategory.Family, TimelineCategory.Marriage,
                TimelineCategory.Tournament, TimelineCategory.Politics, TimelineCategory.Raid,
                TimelineCategory.Vengeance, TimelineCategory.War, TimelineCategory.Peace,
                TimelineCategory.Army, TimelineCategory.Quest, TimelineCategory.Skill,
                TimelineCategory.Rebellion, TimelineCategory.Event
            };

            foreach (var cat in categoryOrder)
            {
                int count;
                if (!categoryCounts.TryGetValue(cat, out count) || count == 0) continue;
                string catName = Localization.L("timeline_cat_" + cat.ToString().ToLowerInvariant());
                string label = catName + " (" + count + ")";
                bool active = !noFilter && activeFilters != null && activeFilters.Contains(cat);
                AddFilterButton(uiContext, filterRow, contentTextBrush, heroId, label, cat, active);
            }

            container.AddChild(filterRow);

            // === Row 2: Sort toggle + Search + Add Note ===
            Widget toolRow = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                          ?? new Widget(uiContext);
            toolRow.Id = "EditableEncyclopedia_TimelineToolbar";
            toolRow.WidthSizePolicy = SizePolicy.StretchToParent;
            toolRow.HeightSizePolicy = SizePolicy.CoverChildren;
            toolRow.MarginBottom = 6f;
            LoreSectionInjector.CopyOrSetHorizontalLayout(null, toolRow);

            Brush toolBrush = null;
            if (contentTextBrush != null)
            {
                toolBrush = contentTextBrush.Clone();
                ScaleBrushFontSize(toolBrush, 0.6f, 16);
                SetBrushColor(toolBrush, 0.6f, 0.55f, 0.4f, 0.9f);
            }

            // Sort toggle button
            bool newestFirst;
            _sortNewestFirst.TryGetValue(heroId, out newestFirst);
            Widget sortBtn = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget")
                           ?? new Widget(uiContext);
            sortBtn.Id = "EditableEncyclopedia_TimelineSort";
            sortBtn.WidthSizePolicy = SizePolicy.CoverChildren;
            sortBtn.HeightSizePolicy = SizePolicy.CoverChildren;
            sortBtn.DoNotPassEventsToChildren = true;
            sortBtn.MarginRight = 10f;
            sortBtn.VerticalAlignment = VerticalAlignment.Center;

            var sortText = new TextWidget(uiContext);
            sortText.Text = newestFirst ? Localization.L("timeline_sort_newest") : Localization.L("timeline_sort_oldest");
            sortText.WidthSizePolicy = SizePolicy.CoverChildren;
            sortText.HeightSizePolicy = SizePolicy.CoverChildren;
            if (toolBrush != null) sortText.Brush = toolBrush.Clone();
            sortBtn.AddChild(sortText);

            string sortHeroId = heroId;
            HookWidgetClick(sortBtn, () =>
            {
                bool current;
                _sortNewestFirst.TryGetValue(sortHeroId, out current);
                _sortNewestFirst[sortHeroId] = !current;
                _currentPage[sortHeroId] = 0;
                RefreshTimeline(sortHeroId);
            });
            toolRow.AddChild(sortBtn);

            // Search button
            string activeSearch = null;
            _searchTerm.TryGetValue(heroId, out activeSearch);

            Widget searchButton = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget")
                                ?? new Widget(uiContext);
            searchButton.Id = "EditableEncyclopedia_TimelineSearch";
            searchButton.WidthSizePolicy = SizePolicy.CoverChildren;
            searchButton.HeightSizePolicy = SizePolicy.CoverChildren;
            searchButton.DoNotPassEventsToChildren = true;
            searchButton.MarginRight = 4f;
            searchButton.VerticalAlignment = VerticalAlignment.Center;

            string searchLabel = string.IsNullOrEmpty(activeSearch)
                ? Localization.L("timeline_search")
                : Localization.L("timeline_search") + ": \"" + (activeSearch.Length > SearchPreviewLength ? activeSearch.Substring(0, SearchPreviewLength) + "..." : activeSearch) + "\"";
            var searchTextW = new TextWidget(uiContext);
            searchTextW.Text = searchLabel;
            searchTextW.WidthSizePolicy = SizePolicy.CoverChildren;
            searchTextW.HeightSizePolicy = SizePolicy.CoverChildren;
            if (toolBrush != null)
            {
                var sBrush = toolBrush.Clone();
                if (!string.IsNullOrEmpty(activeSearch))
                    SetBrushColor(sBrush, 0.8f, 0.7f, 0.4f, 1f);
                searchTextW.Brush = sBrush;
            }
            searchButton.AddChild(searchTextW);

            string capturedHeroId = heroId;
            HookWidgetClick(searchButton, () => ShowSearchDialog(capturedHeroId));
            toolRow.AddChild(searchButton);

            // Clear search button (only if search is active)
            if (!string.IsNullOrEmpty(activeSearch))
            {
                Widget clearButton = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget")
                                   ?? new Widget(uiContext);
                clearButton.Id = "EditableEncyclopedia_TimelineSearchClear";
                clearButton.WidthSizePolicy = SizePolicy.CoverChildren;
                clearButton.HeightSizePolicy = SizePolicy.CoverChildren;
                clearButton.DoNotPassEventsToChildren = true;
                clearButton.MarginRight = 10f;
                clearButton.VerticalAlignment = VerticalAlignment.Center;

                var clearText = new TextWidget(uiContext);
                clearText.Text = "[X]";
                clearText.WidthSizePolicy = SizePolicy.CoverChildren;
                clearText.HeightSizePolicy = SizePolicy.CoverChildren;
                if (toolBrush != null)
                {
                    var clBrush = toolBrush.Clone();
                    SetBrushColor(clBrush, 0.7f, 0.3f, 0.3f, 0.9f);
                    clearText.Brush = clBrush;
                }
                clearButton.AddChild(clearText);

                string clearHeroId = heroId;
                HookWidgetClick(clearButton, () =>
                {
                    _searchTerm.Remove(clearHeroId);
                    _currentPage[clearHeroId] = 0;
                    RefreshTimeline(clearHeroId);
                });
                toolRow.AddChild(clearButton);
            }

            // Separator
            var sep = new TextWidget(uiContext);
            sep.Text = " | ";
            sep.WidthSizePolicy = SizePolicy.CoverChildren;
            sep.HeightSizePolicy = SizePolicy.CoverChildren;
            sep.VerticalAlignment = VerticalAlignment.Center;
            if (toolBrush != null)
            {
                var sepBrush = toolBrush.Clone();
                SetBrushColor(sepBrush, 0.5f, 0.48f, 0.4f, 0.5f);
                sep.Brush = sepBrush;
            }
            toolRow.AddChild(sep);

            // Add Note button
            Widget addNoteBtn = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget")
                              ?? new Widget(uiContext);
            addNoteBtn.Id = "EditableEncyclopedia_TimelineAddNote";
            addNoteBtn.WidthSizePolicy = SizePolicy.CoverChildren;
            addNoteBtn.HeightSizePolicy = SizePolicy.CoverChildren;
            addNoteBtn.DoNotPassEventsToChildren = true;
            addNoteBtn.VerticalAlignment = VerticalAlignment.Center;

            var addNoteText = new TextWidget(uiContext);
            addNoteText.Text = Localization.L("timeline_add_note");
            addNoteText.WidthSizePolicy = SizePolicy.CoverChildren;
            addNoteText.HeightSizePolicy = SizePolicy.CoverChildren;
            if (toolBrush != null)
            {
                var addBrush = toolBrush.Clone();
                SetBrushColor(addBrush, 0.55f, 0.70f, 0.55f, 0.9f); // soft green
                addNoteText.Brush = addBrush;
            }
            addNoteBtn.AddChild(addNoteText);

            string addHeroId = heroId;
            HookWidgetClick(addNoteBtn, () => AddTimelineNote(addHeroId));
            toolRow.AddChild(addNoteBtn);

            container.AddChild(toolRow);
        }

        /// <summary>
        /// Adds a single category filter button (multi-select toggle).
        /// </summary>
        private static void AddFilterButton(UIContext uiContext, Widget toolbar, Brush contentTextBrush,
            string heroId, string label, TimelineCategory category, bool isActive)
        {
            Widget btn = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget")
                       ?? new Widget(uiContext);
            btn.Id = "EditableEncyclopedia_TimelineFilter_" + category;
            btn.WidthSizePolicy = SizePolicy.CoverChildren;
            btn.HeightSizePolicy = SizePolicy.CoverChildren;
            btn.DoNotPassEventsToChildren = true;
            btn.MarginRight = 6f;
            btn.VerticalAlignment = VerticalAlignment.Center;

            var text = new TextWidget(uiContext);
            text.Text = label;
            text.WidthSizePolicy = SizePolicy.CoverChildren;
            text.HeightSizePolicy = SizePolicy.CoverChildren;
            if (contentTextBrush != null)
            {
                var brush = contentTextBrush.Clone();
                ScaleBrushFontSize(brush, 0.6f, 16);
                if (isActive)
                    SetBrushColor(brush, 0.9f, 0.8f, 0.4f, 1f); // highlighted gold
                else if (category != TimelineCategory.All)
                {
                    var tempEntry = new TimelineEntry { Category = category };
                    var catColor = tempEntry.GetCategoryColor();
                    SetBrushColor(brush, catColor.Red, catColor.Green, catColor.Blue, 0.7f);
                }
                else
                    SetBrushColor(brush, 0.6f, 0.55f, 0.4f, 0.8f);
                text.Brush = brush;
            }
            btn.AddChild(text);

            string capturedHeroId = heroId;
            TimelineCategory capturedCat = category;
            HookWidgetClick(btn, () =>
            {
                if (capturedCat == TimelineCategory.All)
                {
                    // "All" clears all filters
                    _activeFilters.Remove(capturedHeroId);
                }
                else
                {
                    // Toggle this category in the multi-select set
                    HashSet<TimelineCategory> filters;
                    if (!_activeFilters.TryGetValue(capturedHeroId, out filters))
                    {
                        filters = new HashSet<TimelineCategory>();
                        _activeFilters[capturedHeroId] = filters;
                    }
                    // Remove "All" if present
                    filters.Remove(TimelineCategory.All);
                    if (filters.Contains(capturedCat))
                        filters.Remove(capturedCat);
                    else
                        filters.Add(capturedCat);
                    // If nothing selected, clear to show all
                    if (filters.Count == 0)
                        _activeFilters.Remove(capturedHeroId);
                }
                _currentPage[capturedHeroId] = 0;
                RefreshTimeline(capturedHeroId);
            });

            toolbar.AddChild(btn);
        }

        /// <summary>
        /// Builds Prev/Next pagination controls at the bottom of the entries.
        /// </summary>
        private static void BuildPaginationControls(UIContext uiContext, Widget container, Brush contentTextBrush,
            string heroId, int currentPage, int totalPages)
        {
            var spacer = new Widget(uiContext);
            spacer.WidthSizePolicy = SizePolicy.StretchToParent;
            spacer.HeightSizePolicy = SizePolicy.Fixed;
            spacer.SuggestedHeight = 6f;
            container.AddChild(spacer);

            Widget pageRow = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                          ?? new Widget(uiContext);
            pageRow.Id = "EditableEncyclopedia_TimelinePagination";
            pageRow.WidthSizePolicy = SizePolicy.StretchToParent;
            pageRow.HeightSizePolicy = SizePolicy.CoverChildren;
            LoreSectionInjector.CopyOrSetHorizontalLayout(null, pageRow);

            Brush pageBrush = null;
            if (contentTextBrush != null)
            {
                pageBrush = contentTextBrush.Clone();
                ScaleBrushFontSize(pageBrush, 0.65f, 18);
                SetBrushColor(pageBrush, 0.6f, 0.55f, 0.4f, 0.9f);
            }

            // Prev button
            if (currentPage > 0)
            {
                Widget prevBtn = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget")
                               ?? new Widget(uiContext);
                prevBtn.Id = "EditableEncyclopedia_TimelinePagePrev";
                prevBtn.WidthSizePolicy = SizePolicy.CoverChildren;
                prevBtn.HeightSizePolicy = SizePolicy.CoverChildren;
                prevBtn.DoNotPassEventsToChildren = true;
                prevBtn.MarginRight = 10f;
                prevBtn.VerticalAlignment = VerticalAlignment.Center;

                var prevText = new TextWidget(uiContext);
                prevText.Text = Localization.L("timeline_page_prev");
                prevText.WidthSizePolicy = SizePolicy.CoverChildren;
                prevText.HeightSizePolicy = SizePolicy.CoverChildren;
                if (pageBrush != null) prevText.Brush = pageBrush.Clone();
                prevBtn.AddChild(prevText);

                string prevHeroId = heroId;
                HookWidgetClick(prevBtn, () =>
                {
                    int prevPage;
                    _currentPage.TryGetValue(prevHeroId, out prevPage);
                    _currentPage[prevHeroId] = Math.Max(0, prevPage - 1);
                    RefreshTimeline(prevHeroId);
                });
                pageRow.AddChild(prevBtn);
            }

            // Page indicator
            var pageLabel = new TextWidget(uiContext);
            pageLabel.Text = Localization.L("timeline_page_indicator", (currentPage + 1).ToString(), totalPages.ToString());
            pageLabel.WidthSizePolicy = SizePolicy.CoverChildren;
            pageLabel.HeightSizePolicy = SizePolicy.CoverChildren;
            pageLabel.VerticalAlignment = VerticalAlignment.Center;
            pageLabel.MarginRight = 10f;
            if (pageBrush != null) pageLabel.Brush = pageBrush.Clone();
            pageRow.AddChild(pageLabel);

            // Next button
            if (currentPage < totalPages - 1)
            {
                Widget nextBtn = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget")
                               ?? new Widget(uiContext);
                nextBtn.Id = "EditableEncyclopedia_TimelinePageNext";
                nextBtn.WidthSizePolicy = SizePolicy.CoverChildren;
                nextBtn.HeightSizePolicy = SizePolicy.CoverChildren;
                nextBtn.DoNotPassEventsToChildren = true;
                nextBtn.VerticalAlignment = VerticalAlignment.Center;

                var nextText = new TextWidget(uiContext);
                nextText.Text = Localization.L("timeline_page_next");
                nextText.WidthSizePolicy = SizePolicy.CoverChildren;
                nextText.HeightSizePolicy = SizePolicy.CoverChildren;
                if (pageBrush != null) nextText.Brush = pageBrush.Clone();
                nextBtn.AddChild(nextText);

                string nextHeroId = heroId;
                HookWidgetClick(nextBtn, () =>
                {
                    int nextPage;
                    _currentPage.TryGetValue(nextHeroId, out nextPage);
                    _currentPage[nextHeroId] = nextPage + 1;
                    RefreshTimeline(nextHeroId);
                });
                pageRow.AddChild(nextBtn);
            }

            container.AddChild(pageRow);
        }

        /// <summary>
        /// Shows a native text inquiry dialog for entering a search term.
        /// </summary>
        private static void ShowSearchDialog(string heroId)
        {
            try
            {
                string currentSearch = "";
                _searchTerm.TryGetValue(heroId, out currentSearch);

                string title = Localization.L("timeline_search_title");

                EncyclopediaInputBlocker.Block();
                EditPopupInjector.ScheduleShow(
                    title,
                    currentSearch ?? "",
                    200,
                    onConfirm: (string searchText) =>
                    {
                        EncyclopediaInputBlocker.Unblock();
                        if (string.IsNullOrWhiteSpace(searchText))
                            _searchTerm.Remove(heroId);
                        else
                            _searchTerm[heroId] = searchText.Trim();
                        _currentPage[heroId] = 0;
                        RefreshTimeline(heroId);
                    },
                    onCancel: () =>
                    {
                        EncyclopediaInputBlocker.Unblock();
                    }
                );
            }
            catch (Exception ex)
            {
                // Theme 5 fix: Block() is called at the top of the try; if ScheduleShow throws
                // before installing onConfirm/onCancel handlers, the blocker stays on permanently.
                // Round 3 finding TLW-6.
                EncyclopediaInputBlocker.Unblock();
                MCMSettings.DebugLog("HeroTimeline: ShowSearchDialog error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Re-injects the timeline section to reflect filter/page/search changes.
        /// </summary>
        private static void RefreshTimeline(string heroId)
        {
            _pendingHeroId = heroId;
            _retryCount = 0;
            DoInject(heroId);
        }

        /// <summary>
        /// Scales brush font size by multiplier and clamps to maxSize.
        /// </summary>
        private static void ScaleBrushFontSize(Brush brush, float multiplier, int maxSize = 0)
        {
            if (brush == null) return;
            try
            {
                var fontSizeProp = brush.GetType().GetProperty("FontSize", AllFlags);
                if (fontSizeProp != null && fontSizeProp.CanRead && fontSizeProp.CanWrite
                    && fontSizeProp.PropertyType == typeof(int))
                {
                    int current = (int)fontSizeProp.GetValue(brush);
                    if (current > 0)
                    {
                        int newSize = (int)(current * multiplier);
                        if (maxSize > 0 && newSize > maxSize) newSize = maxSize;
                        fontSizeProp.SetValue(brush, newSize);
                        return;
                    }
                }
                foreach (var layersPropName in new[] { "TextLayers", "Layers" })
                {
                    var layersProp = brush.GetType().GetProperty(layersPropName, AllFlags);
                    if (layersProp == null) continue;
                    var layers = layersProp.GetValue(brush) as System.Collections.IEnumerable;
                    if (layers == null) continue;
                    foreach (var layer in layers)
                    {
                        var fsProp = layer.GetType().GetProperty("FontSize", AllFlags);
                        if (fsProp != null && fsProp.CanRead && fsProp.CanWrite)
                        {
                            object val = fsProp.GetValue(layer);
                            if (val is int intVal && intVal > 0)
                            {
                                int newSize = (int)(intVal * multiplier);
                                if (maxSize > 0 && newSize > maxSize) newSize = maxSize;
                                fsProp.SetValue(layer, newSize);
                            }
                            else if (val is float floatVal && floatVal > 0)
                            {
                                float newSize = floatVal * multiplier;
                                if (maxSize > 0 && newSize > maxSize) newSize = maxSize;
                                fsProp.SetValue(layer, newSize);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("[HeroTimelineSectionInjector]: " + ex.ToString()); }
        }

        private static void SetBrushColor(Brush brush, float r, float g, float b, float a)
        {
            if (brush == null) return;
            try
            {
                var color = new Color(r, g, b, a);
                var fontColorProp = brush.GetType().GetProperty("FontColor", AllFlags);
                if (fontColorProp != null && fontColorProp.CanWrite)
                {
                    fontColorProp.SetValue(brush, color);
                    return;
                }
                foreach (var layersPropName in new[] { "TextLayers", "Layers" })
                {
                    var layersProp = brush.GetType().GetProperty(layersPropName, AllFlags);
                    if (layersProp == null) continue;
                    var layers = layersProp.GetValue(brush) as System.Collections.IEnumerable;
                    if (layers == null) continue;
                    foreach (var layer in layers)
                    {
                        var colorProp = layer.GetType().GetProperty("Color", AllFlags);
                        if (colorProp != null && colorProp.CanWrite)
                            colorProp.SetValue(layer, color);
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("[HeroTimelineSectionInjector]: " + ex.ToString()); }
        }

        private static void HookCollapseToggle(Widget headerWidget, Widget contentContainer,
            BrushWidget arrow, Widget referenceHeader, string heroId)
        {
            try
            {
                var eventFireEvent = headerWidget.GetType().GetEvent("EventFire",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (eventFireEvent == null) return;

                Action<Widget, string, object[]> eventHandler = (Widget sender, string eventName, object[] args) =>
                {
                    if (eventName != "Click") return;
                    bool wasVisible = contentContainer.IsVisible;
                    contentContainer.IsVisible = !wasVisible;
                    _collapseStates[heroId] = wasVisible;
                    // Persist to save file
                    try { EncyclopediaEditBehavior.Instance?.SetTimelineCollapsed(heroId, wasVisible); }
                    catch (Exception ex) { MCMSettings.DebugLog("[HeroTimelineSectionInjector]: " + ex.ToString()); }

                    if (arrow != null)
                    {
                        try
                        {
                            var setStateMethod = arrow.GetType().GetMethod("SetState",
                                AllFlags, null, new[] { typeof(string) }, null);
                            setStateMethod?.Invoke(arrow, new object[] { wasVisible ? "Collapsed" : "Expanded" });
                        }
                        catch (Exception ex) { MCMSettings.DebugLog("[HeroTimelineSectionInjector]: " + ex.ToString()); }
                    }
                };

                eventFireEvent.AddEventHandler(headerWidget, eventHandler);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("HeroTimeline: HookCollapseToggle error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Creates a text widget for a timeline entry. Uses rich text with clickable
        /// hero/settlement links when marker data is available, falls back to plain text.
        /// Optionally highlights search matches with bold tags.
        /// </summary>
        private static Widget CreateTimelineTextWidget(UIContext uiContext, TimelineEntry entry,
            Brush contentTextBrush, int index, string searchHighlight = null)
        {
            Widget textWidget = null;

            // Try rich text with clickable links if we have raw marked text
            if (!string.IsNullOrEmpty(entry.RawText))
            {
                string richHtml = null;

                if (entry.RawText.Contains("\u00ABh") || entry.RawText.Contains("\u00ABs")
                    || entry.RawText.Contains("\u00ABc") || entry.RawText.Contains("\u00ABk"))
                {
                    // Our custom «h:id»Name«/h» markers — convert to HTML links
                    var segments = JournalSectionInjector.ParseNameMarkers(entry.RawText);
                    var sb = new StringBuilder();
                    foreach (var seg in segments)
                    {
                        if (seg.Color.HasValue)
                        {
                            string style = seg.IsHero ? "Link.Hero" : "Link.Settlement";
                            sb.Append("<a style=\"");
                            sb.Append(style);
                            sb.Append("\"");
                            if (!string.IsNullOrEmpty(seg.EntityId) && !string.IsNullOrEmpty(seg.EntityType))
                            {
                                sb.Append(" href=\"event:Encyclopedia/");
                                sb.Append(seg.EntityType);
                                sb.Append("/");
                                sb.Append(seg.EntityId);
                                sb.Append("\"");
                            }
                            sb.Append(">");
                            sb.Append(seg.Text);
                            sb.Append("</a>");
                        }
                        else
                        {
                            string plainText = seg.Text;
                            if (!string.IsNullOrEmpty(searchHighlight))
                                plainText = HighlightSearchMatch(plainText, searchHighlight);
                            sb.Append(plainText);
                        }
                    }
                    richHtml = sb.ToString();
                }
                else if (entry.RawText.Contains("<a "))
                {
                    // Native Bannerlord HTML — convert native hrefs to encyclopedia links
                    richHtml = TimelineTextProcessor.ConvertNativeHtmlLinks(entry.RawText);
                }

                if (!string.IsNullOrEmpty(richHtml))
                {
                    textWidget = JournalSectionInjector.TryCreateRichTextWidget(uiContext, richHtml);
                    if (textWidget != null)
                    {
                        JournalSectionInjector.HookRichTextLinkClicks(textWidget);
                    }
                }
            }

            // Fallback to plain text (with search highlighting via rich text)
            if (textWidget == null)
            {
                string displayText = entry.Text;
                if (!string.IsNullOrEmpty(searchHighlight) && !string.IsNullOrEmpty(displayText))
                {
                    string highlighted = HighlightSearchMatch(displayText, searchHighlight);
                    if (highlighted != displayText)
                    {
                        // Use rich text widget for highlighting
                        var rtw = JournalSectionInjector.TryCreateRichTextWidget(uiContext, highlighted);
                        if (rtw != null) textWidget = rtw;
                    }
                }
                if (textWidget == null)
                {
                    var tw = new TextWidget(uiContext);
                    tw.Text = displayText;
                    textWidget = tw;
                }
            }

            textWidget.Id = "EditableEncyclopedia_TimelineText_" + index;
            textWidget.WidthSizePolicy = SizePolicy.StretchToParent;
            textWidget.HeightSizePolicy = SizePolicy.CoverChildren;
            textWidget.HorizontalAlignment = HorizontalAlignment.Left;
            textWidget.VerticalAlignment = VerticalAlignment.Top;
            if (contentTextBrush != null)
            {
                var textBrush = contentTextBrush.Clone();
                ScaleBrushFontSize(textBrush, 0.7f, 22);
                var bp = textWidget.GetType().GetProperty("Brush", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (bp != null && bp.CanWrite) bp.SetValue(textWidget, textBrush);
            }
            if (textWidget is BrushWidget bw)
                LoreSectionInjector.ForceTextAlignLeft(bw);

            return textWidget;
        }

        /// <summary>
        /// Wraps search matches in bold tags for rich text highlighting.
        /// </summary>
        private static string HighlightSearchMatch(string text, string search)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(search)) return text;
            int idx = text.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return text;

            var sb = new StringBuilder();
            int pos = 0;
            while (idx >= 0 && pos < text.Length)
            {
                sb.Append(text, pos, idx - pos);
                sb.Append("<b>");
                sb.Append(text, idx, search.Length);
                sb.Append("</b>");
                pos = idx + search.Length;
                idx = text.IndexOf(search, pos, StringComparison.OrdinalIgnoreCase);
            }
            if (pos < text.Length) sb.Append(text, pos, text.Length - pos);
            return sb.ToString();
        }

        // ─────────── Click Handlers ───────────

        /// <summary>
        /// Hooks into a ButtonWidget's EventFire to call an action on Click.
        /// </summary>
        private static void HookWidgetClick(Widget widget, Action onClick)
        {
            try
            {
                var eventFireEvent = widget.GetType().GetEvent("EventFire",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (eventFireEvent == null) return;

                Action<Widget, string, object[]> handler = (Widget sender, string eventName, object[] args) =>
                {
                    if (eventName == "Click")
                    {
                        try { onClick(); }
                        catch (Exception ex)
                        {
                            MCMSettings.DebugLog("HeroTimeline: click handler error: " + ex.ToString());
                        }
                    }
                };

                eventFireEvent.AddEventHandler(widget, handler);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("HeroTimeline: HookWidgetClick error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Opens the edit popup for a manual timeline entry.
        /// </summary>
        private static void EditTimelineEntry(string heroId, int journalIndex, string currentText)
        {
            try
            {
                if (EncyclopediaEditBehavior.Instance == null) return;

                string title = Localization.L("timeline_edit_title");

                EncyclopediaInputBlocker.Block();

                EditPopupInjector.ScheduleShow(
                    title,
                    currentText,
                    500,
                    onConfirm: (string newText) =>
                    {
                        EncyclopediaInputBlocker.Unblock();

                        if (string.IsNullOrWhiteSpace(newText)) return;

                        EncyclopediaEditBehavior.Instance.ReplaceJournalEntry(heroId, journalIndex, newText);
                        MCMSettings.DebugLog("HeroTimeline: edited journal entry " + journalIndex + " for " + heroId);

                        TimelineDataCollector.ClearCache();

                        try { EncyclopediaPageTracker.CurrentRefreshAction?.Invoke(); }
                        catch (Exception ex) { MCMSettings.DebugLog("HeroTimeline: refresh error: " + ex.ToString()); }
                    },
                    onCancel: () =>
                    {
                        EncyclopediaInputBlocker.Unblock();
                        MCMSettings.DebugLog("HeroTimeline: cancelled edit for " + heroId);
                    }
                );
            }
            catch (Exception ex)
            {
                // Theme 5 fix: ensure blocker isn't permanently engaged on synchronous
                // exception path (round 3 finding TLW-6).
                EncyclopediaInputBlocker.Unblock();
                MCMSettings.DebugLog("HeroTimeline: EditTimelineEntry error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Deletes a manual timeline entry after confirmation.
        /// </summary>
        private static void DeleteTimelineEntry(string heroId, int journalIndex)
        {
            try
            {
                if (EncyclopediaEditBehavior.Instance == null) return;

                string title = Localization.L("timeline_delete_title");
                string message = Localization.L("timeline_delete_message");

                var inquiryData = new InquiryData(
                    title, message, true, true,
                    Localization.L("timeline_delete_confirm"),
                    Localization.L("edit_cancel"),
                    delegate
                    {
                        EncyclopediaEditBehavior.Instance.RemoveJournalEntry(heroId, journalIndex);
                        MCMSettings.DebugLog("HeroTimeline: deleted journal entry " + journalIndex + " for " + heroId);

                        TimelineDataCollector.ClearCache();

                        try { EncyclopediaPageTracker.CurrentRefreshAction?.Invoke(); }
                        catch (Exception ex) { MCMSettings.DebugLog("HeroTimeline: refresh error: " + ex.ToString()); }
                    },
                    delegate
                    {
                        MCMSettings.DebugLog("HeroTimeline: delete cancelled for " + heroId);
                    });

                InformationManager.ShowInquiry(inquiryData, false, false);
                EncyclopediaNavigationGuard.ScheduleNavigateBack();
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("HeroTimeline: DeleteTimelineEntry error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Opens a popup to add a new manual timeline note for the hero.
        /// </summary>
        private static void AddTimelineNote(string heroId)
        {
            try
            {
                if (EncyclopediaEditBehavior.Instance == null) return;

                string title = Localization.L("timeline_add_note_title");

                EncyclopediaInputBlocker.Block();

                EditPopupInjector.ScheduleShow(
                    title,
                    "",
                    500,
                    onConfirm: (string noteText) =>
                    {
                        EncyclopediaInputBlocker.Unblock();
                        if (string.IsNullOrWhiteSpace(noteText)) return;

                        EncyclopediaEditBehavior.Instance.AddJournalEntry(heroId, noteText);
                        MCMSettings.DebugLog("HeroTimeline: added note for " + heroId);

                        TimelineDataCollector.ClearCache();

                        try { EncyclopediaPageTracker.CurrentRefreshAction?.Invoke(); }
                        catch (Exception ex) { MCMSettings.DebugLog("HeroTimeline: refresh error: " + ex.ToString()); }
                    },
                    onCancel: () =>
                    {
                        EncyclopediaInputBlocker.Unblock();
                    }
                );
            }
            catch (Exception ex)
            {
                // Theme 5 fix: ensure blocker isn't permanently engaged on synchronous
                // exception path (round 3 finding TLW-6).
                EncyclopediaInputBlocker.Unblock();
                MCMSettings.DebugLog("HeroTimeline: AddTimelineNote error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Sets a tooltip on a widget for hover text display.
        /// Delegates to reflection-based HintViewModel approach.
        /// </summary>
        private static void TrySetTooltip(Widget widget, string tooltipText)
        {
            if (widget == null || string.IsNullOrEmpty(tooltipText)) return;
            try
            {
                // The engine's real hover hint is MBInformationManager.ShowHint(string) /
                // HideInformations() on "HoverBegin"/"HoverEnd" — NOT InformationManager.ShowTooltip
                // (whose Type arg is a domain dispatch key, so List<object> rendered nothing). Attach
                // the handler unconditionally so nothing bails before wiring hover.
                var eventFireEvent = widget.GetType().GetEvent("EventFire",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (eventFireEvent == null) return;

                string hintText = tooltipText;
                Action<Widget, string, object[]> handler = (sender, eventName, args) =>
                {
                    try
                    {
                        if (eventName == "HoverBegin" || eventName == "OnHoverBegin"
                            || eventName == "MouseOver" || eventName == "Hover")
                            ShowNativeHint(hintText);
                        else if (eventName == "HoverEnd" || eventName == "OnHoverEnd"
                                 || eventName == "MouseOut")
                            HideNativeHint();
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("HeroTimeline: hover handler: " + ex.Message); }
                };
                eventFireEvent.AddEventHandler(widget, handler);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("HeroTimeline: TrySetTooltip error: " + ex.ToString());
            }
        }

        // --- Native hover-hint API (TaleWorlds.Core.MBInformationManager.ShowHint / HideInformations),
        //     resolved once via reflection — the exact path HintViewModel.ExecuteBeginHint uses. ---
        private static bool _hintApiResolved;
        private static MethodInfo _showHintMethod;
        private static MethodInfo _hideInfoMethod;

        private static void ResolveHintApi()
        {
            if (_hintApiResolved) return;
            _hintApiResolved = true;
            try
            {
                Type mbType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    mbType = asm.GetType("TaleWorlds.Core.MBInformationManager");
                    if (mbType != null) break;
                }
                if (mbType == null) { MCMSettings.DebugLog("HeroTimeline hint: MBInformationManager not found"); return; }
                _showHintMethod = mbType.GetMethod("ShowHint",
                    BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string) }, null);
                _hideInfoMethod = mbType.GetMethod("HideInformations",
                    BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
            }
            catch (Exception ex) { MCMSettings.DebugLog("HeroTimeline ResolveHintApi: " + ex.Message); }
        }

        private static void ShowNativeHint(string text)
        {
            ResolveHintApi();
            if (_showHintMethod != null) _showHintMethod.Invoke(null, new object[] { text });
        }

        private static void HideNativeHint()
        {
            ResolveHintApi();
            if (_hideInfoMethod != null) _hideInfoMethod.Invoke(null, null);
        }
    }
}
