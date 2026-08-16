using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ScreenSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Safe capitalize first letter — prevents Substring crashes on empty/null strings.
    /// </summary>
    internal static class SafeStringHelper
    {
        internal static string CapFirst(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (s.Length == 1) return s.ToUpperInvariant();
            return s.Substring(0, 1).ToUpperInvariant() + s.Substring(1);
        }
    }

    /// <summary>
    /// Injects a collapsible "Relation Notes" section into the encyclopedia hero page
    /// widget tree, placed after the Lore section (before Friends/Enemies).
    /// Visually identical to the Lore section — uses the same header/collapse pattern.
    /// Lists all heroes that have a relation note with the currently viewed hero.
    /// Features: clickable hero names, edit-on-click, delete button, relation score,
    /// full text display, note count in header, persistent collapse state.
    /// </summary>
    internal static class RelationNotesSectionInjector
    {
        // Track injected widgets so we can remove them on page refresh
        private static readonly List<Widget> _injectedWidgets = new List<Widget>();

        // Collapse state persistence — keyed by heroId (the page being viewed)
        private static readonly Dictionary<string, bool> _collapseStates = new Dictionary<string, bool>();

        // Current hero being viewed (needed for click handlers).
        // Volatile so the read-modify-write in click handlers vs. main-thread reads is consistent.
        private static volatile string _currentHeroId;

        // Retry state — widget tree may not be ready on back/forward navigation.
        // _pendingHeroId volatile so main thread sees latest payload after
        // observing _retryPending=true (Theme 1: round 3 finding RNC-4).
        private static volatile string _pendingHeroId;
        private static int _retryCount;
        private static System.Threading.Timer _retryTimer;
        private static volatile bool _retryPending;
        private const int MaxRetries = 10;
        private const int RetryDelayMs = 150;
        private const int MinTreeDescendants = 50;
        private const int HistoryTextMaxLength = 50;
        private const int HistoryTextEllipsisOffset = 3;

        /// <summary>
        /// Must be called from the main game thread (e.g., OnApplicationTick).
        /// Processes deferred retry when widget tree wasn't ready.
        /// </summary>
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
                try { t.Dispose(); } catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
        }

        /// <summary>
        /// Injects a "Relation Notes" section into the hero encyclopedia page widget tree.
        /// Call this from the HeroPageRefreshPatch Postfix after the page is built.
        /// </summary>
        public static void InjectRelationNotesSection(string heroId)
        {
            DisposeRetryTimer();
            _retryPending = false;
            _pendingHeroId = heroId;
            _currentHeroId = heroId;
            _retryCount = 0;
            DoInject(heroId);
        }

        // Available tags for the category/tag system
        public static readonly string[] AvailableTags = { "ally", "rival", "trade", "family", "other" };

        // Current search filter text (persists during page session)
        private static string _currentSearchFilter = "";

        /// <summary>
        /// Data class for a resolved relation note entry.
        /// </summary>
        private class NoteEntry
        {
            public string OtherHeroId;
            public string SourceHeroId; // for bidirectional: who wrote the note
            public string HeroName;
            public string NoteText;
            public int RelationScore;
            public bool HasRelationScore;
            public bool IsDead;
            public bool IsPrisoner;
            public string ClanName;
            public string Tag; // category tag: ally, rival, trade, family, other
            public bool IsTagLocked; // true if tag is locked by player
            public string SuggestedTag; // auto-suggested tag (null if none)
            public List<RelationHistoryEntry> History; // relation change history
            public bool IsBidirectional; // true if this is a note FROM another hero ABOUT the viewed hero
        }

        private static void DoInject(string heroId)
        {
            try
            {
                RemoveOldWidgets();

                if (EncyclopediaEditBehavior.Instance == null) return;

                // Collect relation notes for this hero (own notes)
                var notes = EncyclopediaEditBehavior.Instance.GetRelationNotesForHero(heroId);

                // Collect bidirectional notes (notes others wrote about this hero)
                var notesAbout = EncyclopediaEditBehavior.Instance.GetRelationNotesAboutHero(heroId);

                bool hasNoNotes = (notes == null || notes.Count == 0) && (notesAbout == null || notesAbout.Count == 0);

                // Resolve the viewing hero for relation score lookup
                Hero viewingHero = FindHero(heroId);

                // Resolve hero names and relation scores for each note
                var resolvedNotes = new List<NoteEntry>();
                if (notes != null)
                {
                    foreach (var (otherHeroId, noteText) in notes)
                    {
                        var entry = ResolveNoteEntry(heroId, otherHeroId, noteText, viewingHero, false);
                        if (entry != null) resolvedNotes.Add(entry);
                    }
                }

                // Resolve bidirectional notes
                var bidirectionalNotes = new List<NoteEntry>();
                if (notesAbout != null)
                {
                    foreach (var (sourceHeroId, noteText) in notesAbout)
                    {
                        // Skip self-notes (viewing hero's own notes already collected above)
                        if (sourceHeroId == heroId) continue;
                        var entry = ResolveNoteEntry(heroId, sourceHeroId, noteText, viewingHero, true);
                        if (entry != null)
                        {
                            entry.SourceHeroId = sourceHeroId;
                            bidirectionalNotes.Add(entry);
                        }
                    }
                }

                if (resolvedNotes.Count == 0 && bidirectionalNotes.Count == 0)
                    hasNoNotes = true;

                // Sort by relation score — enemies first (lowest), friends last (highest)
                SortNoteEntries(resolvedNotes);
                SortNoteEntries(bidirectionalNotes);

                // Find the encyclopedia GauntletLayer
                var topScreen = ScreenManager.TopScreen;
                if (topScreen == null) return;

                bool layerWasSizeFallback;
                GauntletLayer encLayer = LoreSectionInjector.FindEncyclopediaLayer(topScreen, out layerWasSizeFallback);
                if (encLayer == null)
                {
                    if (_retryCount < MaxRetries)
                    {
                        _retryCount++;
                        MCMSettings.DebugLog("RelationNotesSection: no encyclopedia layer found, scheduling retry #"
                            + _retryCount + " in " + RetryDelayMs + "ms");
                        // Theme 2 fix: dispose previous timer before reassigning (round 3 finding RNC-3).
                        DisposeRetryTimer();
                        _retryTimer = new System.Threading.Timer(_ =>
                        {
                            _retryPending = true;
                        }, null, RetryDelayMs, System.Threading.Timeout.Infinite);
                    }
                    else
                    {
                        MCMSettings.DebugLog("RelationNotesSection: no encyclopedia layer found after " + MaxRetries + " retries");
                    }
                    return;
                }

                Widget layerRoot = LoreSectionInjector.GetLayerRootWidget(encLayer);
                if (layerRoot == null)
                {
                    MCMSettings.DebugLog("RelationNotesSection: layer root is null");
                    return;
                }

                UIContext uiContext = layerRoot.EventManager?.Context as UIContext;
                if (uiContext == null)
                {
                    MCMSettings.DebugLog("RelationNotesSection: cannot get UIContext");
                    return;
                }

                Widget encWindow = LoreSectionInjector.FindEncyclopediaContentWidget(layerRoot)
                                ?? LoreSectionInjector.FindLargestChild(layerRoot);
                if (encWindow == null)
                {
                    MCMSettings.DebugLog("RelationNotesSection: no encyclopedia window found");
                    return;
                }

                // Find the insertion point — AFTER Lore section (before Friends/Enemies)
                Widget insertionParent = null;
                int insertionIndex = -1;

                if (FindRelationNotesInsertionPoint(encWindow, out insertionParent, out insertionIndex))
                {
                    MCMSettings.DebugLog("RelationNotesSection: found insertion point at index " + insertionIndex
                        + " in " + insertionParent.GetType().Name + " (children=" + insertionParent.ChildCount + ")");
                    BuildAndInsertSection(uiContext, insertionParent, insertionIndex, heroId, resolvedNotes, bidirectionalNotes);
                }
                else if (FindRelationNotesInsertionPoint(layerRoot, out insertionParent, out insertionIndex))
                {
                    MCMSettings.DebugLog("RelationNotesSection: found insertion point via layerRoot at index "
                        + insertionIndex + " in " + insertionParent.GetType().Name);
                    BuildAndInsertSection(uiContext, insertionParent, insertionIndex, heroId, resolvedNotes, bidirectionalNotes);
                }
                else
                {
                    // Fallback: try all layers
                    bool found = false;
                    var allLayers = topScreen.Layers;
                    for (int li = 0; li < allLayers.Count; li++)
                    {
                        if (!(allLayers[li] is GauntletLayer gl)) continue;
                        Widget altRoot = LoreSectionInjector.GetLayerRootWidget(gl);
                        if (altRoot == null || altRoot == layerRoot) continue;
                        // Skip non-encyclopedia layers (e.g. Clan/Party panels)
                        if (!LoreSectionInjector.ContainsWidgetType(altRoot, "EncyclopediaDividerButtonWidget")) continue;
                        if (FindRelationNotesInsertionPoint(altRoot, out insertionParent, out insertionIndex))
                        {
                            UIContext altCtx = altRoot.EventManager?.Context as UIContext;
                            if (altCtx != null) uiContext = altCtx;
                            BuildAndInsertSection(uiContext, insertionParent, insertionIndex, heroId, resolvedNotes, bidirectionalNotes);
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        // If the widget tree is too small, the encyclopedia page hasn't
                        // fully loaded yet — retry instead of falling back to a random panel.
                        int encDesc = LoreSectionInjector.CountDescendants(encWindow);

                        // If the layer was selected via size-based fallback (no content
                        // match), the real encyclopedia layer may still be loading.
                        if (layerWasSizeFallback && _retryCount < MaxRetries)
                        {
                            _retryCount++;
                            EncyclopediaPageTracker.EncyclopediaLayerRef = null;
                            MCMSettings.DebugLog("RelationNotesSection: size-fallback layer had no insertion point, "
                                + "clearing cached layer and scheduling retry #" + _retryCount
                                + " in " + RetryDelayMs + "ms");
                            // Theme 2 fix: dispose previous timer before reassigning (round 3 finding RNC-3).
                            DisposeRetryTimer();
                            _retryTimer = new System.Threading.Timer(_ =>
                            {
                                _retryPending = true;
                            }, null, RetryDelayMs, System.Threading.Timeout.Infinite);
                        }
                        else if (encDesc < MinTreeDescendants && _retryCount < MaxRetries)
                        {
                            _retryCount++;
                            MCMSettings.DebugLog("RelationNotesSection: tree too small (descendants=" + encDesc
                                + "), scheduling retry #" + _retryCount + " in " + RetryDelayMs + "ms");
                            // Theme 2 fix: dispose previous timer before reassigning (round 3 finding RNC-3).
                            DisposeRetryTimer();
                            _retryTimer = new System.Threading.Timer(_ =>
                            {
                                _retryPending = true;
                            }, null, RetryDelayMs, System.Threading.Timeout.Infinite);
                        }
                        else if (encDesc >= MinTreeDescendants)
                        {
                            // Last fallback: append to main content panel
                            Widget contentPanel = LoreSectionInjector.FindMainContentPanel(encWindow)
                                               ?? LoreSectionInjector.FindMainContentPanel(layerRoot);
                            if (contentPanel != null)
                            {
                                MCMSettings.DebugLog("RelationNotesSection: fallback — appending to content panel");
                                BuildAndInsertSection(uiContext, contentPanel, contentPanel.ChildCount, heroId, resolvedNotes, bidirectionalNotes);
                            }
                            else
                            {
                                MCMSettings.DebugLog("RelationNotesSection: content panel not found in large tree");
                            }
                        }
                        else if (_retryCount < MaxRetries)
                        {
                            _retryCount++;
                            MCMSettings.DebugLog("RelationNotesSection: tree not ready, scheduling retry #"
                                + _retryCount + " in " + RetryDelayMs + "ms");
                            // Theme 2 fix: dispose previous timer before reassigning (round 3 finding RNC-3).
                            DisposeRetryTimer();
                            _retryTimer = new System.Threading.Timer(_ =>
                            {
                                _retryPending = true;
                            }, null, RetryDelayMs, System.Threading.Timeout.Infinite);
                        }
                        else
                        {
                            MCMSettings.DebugLog("RelationNotesSection: all strategies failed after " + MaxRetries + " retries");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("RelationNotesSection: error: " + ex.ToString());
            }
        }

        private static NoteEntry ResolveNoteEntry(string viewingHeroId, string otherHeroId, string noteText,
            Hero viewingHero, bool isBidirectional)
        {
            string name = ResolveHeroName(otherHeroId);
            if (name == null) name = otherHeroId;

            var entry = new NoteEntry
            {
                OtherHeroId = otherHeroId,
                HeroName = name,
                NoteText = noteText,
                HasRelationScore = false,
                RelationScore = 0,
                IsBidirectional = isBidirectional
            };

            Hero targetHero = FindHero(otherHeroId);
            if (viewingHero != null && targetHero != null)
            {
                try
                {
                    entry.RelationScore = viewingHero.GetRelation(targetHero);
                    entry.HasRelationScore = true;
                }
                catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
            }

            if (targetHero != null)
            {
                try { entry.IsDead = targetHero.IsDead; } catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
                try { entry.IsPrisoner = targetHero.IsPrisoner; } catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
                try { entry.ClanName = targetHero.Clan?.Name?.ToString(); } catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
            }

            // Load tag, lock state, and suggestion
            if (!isBidirectional)
            {
                entry.Tag = EncyclopediaEditBehavior.Instance.GetRelationNoteTag(viewingHeroId, otherHeroId);
                entry.IsTagLocked = EncyclopediaEditBehavior.Instance.IsRelationNoteTagLocked(viewingHeroId, otherHeroId);
                entry.SuggestedTag = EncyclopediaEditBehavior.Instance.SuggestRelationNoteTag(viewingHeroId, otherHeroId);
            }
            else
            {
                entry.Tag = EncyclopediaEditBehavior.Instance.GetRelationNoteTag(otherHeroId, viewingHeroId);
            }

            // Load relation history
            try
            {
                entry.History = EncyclopediaEditBehavior.Instance.GetRelationHistory(viewingHeroId, otherHeroId);
            }
            catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); entry.History = new List<RelationHistoryEntry>(); }

            return entry;
        }

        private static void SortNoteEntries(List<NoteEntry> entries)
        {
            entries.Sort((a, b) =>
            {
                if (a.HasRelationScore && b.HasRelationScore)
                    return a.RelationScore.CompareTo(b.RelationScore);
                if (a.HasRelationScore) return -1;
                if (b.HasRelationScore) return 1;
                return 0;
            });
        }

        private static bool MatchesFilter(NoteEntry entry, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;
            string lower = filter.ToLowerInvariant();
            if (entry.HeroName != null && entry.HeroName.ToLowerInvariant().Contains(lower)) return true;
            if (entry.NoteText != null && entry.NoteText.ToLowerInvariant().Contains(lower)) return true;
            if (entry.Tag != null && entry.Tag.ToLowerInvariant().Contains(lower)) return true;
            if (entry.ClanName != null && entry.ClanName.ToLowerInvariant().Contains(lower)) return true;
            return false;
        }

        public static void RemoveOldWidgets()
        {
            foreach (var widget in _injectedWidgets)
            {
                try
                {
                    if (widget?.ParentWidget != null)
                        widget.ParentWidget.RemoveChild(widget);
                }
                catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
            }
            _injectedWidgets.Clear();
        }

        // ─────────── Insertion Point Detection ───────────

        /// <summary>
        /// Finds the right place to insert the Relation Notes section — AFTER the Lore
        /// section (before Friends/Enemies). Falls back to searching for Friends/Enemies
        /// if Lore is not found.
        /// </summary>
        private static bool FindRelationNotesInsertionPoint(Widget root, out Widget parent, out int index)
        {
            parent = null;
            index = -1;

            // Strategy: Find the Lore section (injected by LoreSectionInjector) first,
            // then insert AFTER it. Fall back to Friends/Enemies if Lore not found.
            // Each section is a header + content pair, so we need to find the content
            // widget and insert after it.
            foreach (string sectionName in new[] { "LoreContent", "Lore", "Enemies", "Friends", "Companions", "Allies" })
            {
                Widget sectionById = LoreSectionInjector.FindWidgetByIdContaining(root, sectionName, 0);
                if (sectionById == null) continue;

                // Walk up to find the section-level container (same logic as LoreSectionInjector)
                Widget current = sectionById;
                for (int depth = 0; depth < 10 && current != null; depth++)
                {
                    Widget sectionParent = current.ParentWidget;
                    if (sectionParent == null) break;

                    int idx = LoreSectionInjector.FindChildIndex(sectionParent, current);
                    if (idx >= 0 && sectionParent.ChildCount >= 2)
                    {
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

                        // Found the sections container — find the LAST section (Enemies or Friends)
                        // and insert after it. Each section is typically a header widget followed by
                        // a content widget, so we look for the last child that matches our section.
                        int lastSectionEnd = FindLastSectionEnd(sectionParent);

                        parent = sectionParent;
                        index = lastSectionEnd >= 0 ? lastSectionEnd : sectionParent.ChildCount;
                        MCMSettings.DebugLog("RelationNotesSection: found '" + sectionName + "' by Id, inserting at index "
                            + index + " in " + sectionParent.GetType().Name
                            + " (children=" + sectionParent.ChildCount + ")");
                        return true;
                    }

                    current = sectionParent;
                }
            }

            return false;
        }

        /// <summary>
        /// Finds the index after the Lore section in the parent container.
        /// Sections are pairs of (header, content) widgets. Returns the index
        /// after the Lore content widget so Relation Notes appears between
        /// Lore and Friends/Enemies.
        /// </summary>
        private static int FindLastSectionEnd(Widget parent)
        {
            // Look for the Lore content widget by Id
            for (int i = 0; i < parent.ChildCount; i++)
            {
                Widget child = parent.GetChild(i);
                if (child != null && child.Id != null && child.Id.Contains("LoreContent"))
                {
                    return i + 1; // Insert after Lore content
                }
            }

            // Fallback: look for the Lore header, then skip header+content pair
            for (int i = 0; i < parent.ChildCount; i++)
            {
                Widget child = parent.GetChild(i);
                if (child != null && child.Id != null && child.Id.Contains("LoreDivider"))
                {
                    // Header is at i, content is at i+1
                    return Math.Min(i + 2, parent.ChildCount);
                }
            }

            // Last fallback: look for Friends/Enemies header and insert before it
            for (int i = 0; i < parent.ChildCount; i++)
            {
                Widget child = parent.GetChild(i);
                if (child == null) continue;
                string childId = child.Id;
                if (childId != null)
                {
                    foreach (string name in new[] { "Friends", "Enemies", "Companions", "Allies" })
                    {
                        if (childId.Contains(name))
                            return i; // Insert before Friends/Enemies
                    }
                }
            }

            // Ultimate fallback: insert at end
            return parent.ChildCount;
        }

        // ─────────── Widget Creation ───────────

        /// <summary>
        /// Builds and inserts the Relation Notes section with all improvements:
        /// clickable hero names, edit-on-click, delete button, relation score,
        /// full text display, note count in header, persistent collapse state.
        /// </summary>
        private static void BuildAndInsertSection(UIContext uiContext, Widget parent, int index,
            string heroId, List<NoteEntry> entries, List<NoteEntry> bidirectionalEntries)
        {
            try
            {
                // === 1. Extract native widget references ===
                var native = LoreSectionInjector.ExtractNativeSectionParts(parent);

                Brush contentTextBrush = LoreSectionInjector.FindBrushByName(parent, "Encyclopedia.SubPage.Info.Text")
                                         ?? LoreSectionInjector.FindAnyTextBrush(parent);

                Brush labelBrush = LoreSectionInjector.FindBrushByName(parent, "Encyclopedia.Stat.DefinitionText");

                Brush valueBrush = LoreSectionInjector.FindBrushByName(parent, "Encyclopedia.Stat.ValueText");

                // Log native indicator properties for debugging
                if (native.NativeIndicator != null)
                {
                    var ni = native.NativeIndicator;
                    MCMSettings.DebugLog("RelNotesSection: native indicator — "
                        + "W=" + ni.SuggestedWidth + " H=" + ni.SuggestedHeight
                        + " WPolicy=" + ni.WidthSizePolicy + " HPolicy=" + ni.HeightSizePolicy
                        + " margins=" + ni.MarginLeft + "/" + ni.MarginTop + "/" + ni.MarginRight + "/" + ni.MarginBottom
                        + " hAlign=" + ni.HorizontalAlignment + " vAlign=" + ni.VerticalAlignment
                        + " vis=" + ni.IsVisible + " enabled=" + ni.IsEnabled
                        + " brush=" + (ni is BrushWidget bwDbg ? bwDbg.ReadOnlyBrush?.Name : "?"));
                }

                MCMSettings.DebugLog("RelNotesSection: brushes — indicator="
                    + (native.IndicatorBrush != null) + " headerText="
                    + (native.HeaderTextBrush != null) + " line="
                    + (native.LineBrush != null) + " content="
                    + (contentTextBrush != null ? contentTextBrush.Name : "null")
                    + " label=" + (labelBrush != null ? labelBrush.Name : "null"));

                // === 2. Create the header (visual clone of native section) ===

                Widget headerWrapper = null;
                if (native.ReferenceHeader != null)
                {
                    Type t = native.ReferenceHeader.GetType();
                    while (t != null && t != typeof(Widget))
                    {
                        if (t.Name == "ButtonWidget")
                        {
                            var ctor = t.GetConstructor(new[] { typeof(UIContext) });
                            if (ctor != null)
                            {
                                headerWrapper = ctor.Invoke(new object[] { uiContext }) as Widget;
                                MCMSettings.DebugLog("RelNotesSection: created ButtonWidget from type "
                                    + t.FullName);
                            }
                            break;
                        }
                        t = t.BaseType;
                    }
                }
                if (headerWrapper == null)
                {
                    headerWrapper = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget");
                    if (headerWrapper != null)
                        MCMSettings.DebugLog("RelNotesSection: created ButtonWidget via TryCreateWidgetByType");
                }
                if (headerWrapper == null)
                {
                    headerWrapper = new Widget(uiContext);
                    MCMSettings.DebugLog("RelNotesSection: fell back to plain Widget for header");
                }
                headerWrapper.Id = "EditableEncyclopedia_RelNotesDivider";
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

                // Inner horizontal bar (ListPanel) — clone layout from native PlacementListPanel
                Widget headerBar = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel");
                if (headerBar == null)
                    headerBar = new Widget(uiContext);
                headerBar.Id = "EditableEncyclopedia_RelNotesPlacement";

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

                // Collapse indicator — clone ALL properties from native widget
                BrushWidget arrow = null;
                if (native.NativeIndicator != null)
                {
                    arrow = new BrushWidget(uiContext);
                    arrow.Id = "EditableEncyclopedia_RelNotesIndicator";
                    LoreSectionInjector.CopyLayoutProperties(native.NativeIndicator, arrow);
                    if (native.IndicatorBrush != null)
                        arrow.Brush = native.IndicatorBrush;

                    LoreSectionInjector.ForceWidgetBrushState(arrow, native.NativeIndicator);

                    headerBar.AddChild(arrow);
                    MCMSettings.DebugLog("RelNotesSection: arrow cloned — W=" + arrow.SuggestedWidth
                        + " H=" + arrow.SuggestedHeight + " WPolicy=" + arrow.WidthSizePolicy
                        + " HPolicy=" + arrow.HeightSizePolicy);
                }

                // Title text "Relation Notes (N)" — clone properties from native title
                var titleText = new TextWidget(uiContext);
                titleText.Id = "EditableEncyclopedia_RelNotesTitle";
                // Improvement #6: Show note count in header
                int totalCount = entries.Count + (bidirectionalEntries != null ? bidirectionalEntries.Count : 0);
                titleText.Text = totalCount > 0
                    ? Localization.L("relation_notes_header") + " (" + totalCount + ")"
                    : Localization.L("relation_notes_header");
                if (native.NativeTitle != null)
                {
                    LoreSectionInjector.CopyLayoutProperties(native.NativeTitle, titleText);
                    if (native.HeaderTextBrush != null)
                        titleText.Brush = native.HeaderTextBrush;
                }
                else
                {
                    titleText.WidthSizePolicy = SizePolicy.CoverChildren;
                    titleText.HeightSizePolicy = SizePolicy.CoverChildren;
                    titleText.VerticalAlignment = VerticalAlignment.Center;
                    if (native.HeaderTextBrush != null)
                        titleText.Brush = native.HeaderTextBrush;
                }
                headerBar.AddChild(titleText);

                // Horizontal separator line — clone from native line widget
                if (native.NativeLine != null)
                {
                    Widget line = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ImageWidget");
                    if (line == null)
                        line = new BrushWidget(uiContext);
                    line.Id = "EditableEncyclopedia_RelNotesLine";
                    LoreSectionInjector.CopyLayoutProperties(native.NativeLine, line);
                    if (native.LineBrush != null && line is BrushWidget bwLine)
                        bwLine.Brush = native.LineBrush;
                    headerBar.AddChild(line);
                }

                headerWrapper.AddChild(headerBar);

                // === 3. Create the content container ===
                // Align content with the header title text (just past the arrow indicator).
                float contentMarginLeft = 25f;
                if (native.NativeIndicator != null)
                    contentMarginLeft = native.NativeIndicator.SuggestedWidth
                        + native.NativeIndicator.MarginLeft + native.NativeIndicator.MarginRight + 5f;

                Widget contentContainer = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel");
                if (contentContainer == null)
                    contentContainer = new Widget(uiContext);
                contentContainer.Id = "EditableEncyclopedia_RelNotesContent";
                contentContainer.WidthSizePolicy = SizePolicy.StretchToParent;
                contentContainer.HeightSizePolicy = SizePolicy.CoverChildren;
                contentContainer.MarginLeft = contentMarginLeft;
                contentContainer.MarginBottom = 5f;
                LoreSectionInjector.SetVerticalLayoutTopToBottom(contentContainer);

                // Note preview truncation limit
                const int MaxNotePreviewLength = 60;

                // === 3a. Search filter hint text ===
                if (entries.Count > 3)
                {
                    var searchHint = new TextWidget(uiContext);
                    searchHint.Id = "EditableEncyclopedia_RelNotesSearchHint";
                    searchHint.Text = !string.IsNullOrEmpty(_currentSearchFilter)
                        ? Localization.L("relation_note_search_placeholder") + " \"" + _currentSearchFilter + "\""
                        : Localization.L("relation_note_search_placeholder");
                    searchHint.WidthSizePolicy = SizePolicy.CoverChildren;
                    searchHint.HeightSizePolicy = SizePolicy.CoverChildren;
                    searchHint.MarginBottom = 5f;
                    if (contentTextBrush != null)
                    {
                        var hintBrush = contentTextBrush.Clone();
                        SetBrushColor(hintBrush, 0.5f, 0.5f, 0.5f, 0.7f);
                        searchHint.Brush = hintBrush;
                    }

                    // Make it clickable to open search
                    Widget searchButton = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget");
                    if (searchButton == null) searchButton = new Widget(uiContext);
                    searchButton.Id = "EditableEncyclopedia_RelNotesSearchBtn";
                    searchButton.WidthSizePolicy = SizePolicy.CoverChildren;
                    searchButton.HeightSizePolicy = SizePolicy.CoverChildren;
                    searchButton.DoNotAcceptEvents = false;
                    searchButton.DoNotPassEventsToChildren = true;
                    searchButton.AddChild(searchHint);

                    string capturedHeroIdForSearch = heroId;
                    HookWidgetClick(searchButton, () =>
                    {
                        EncyclopediaInputBlocker.Block();
                        try
                        {
                            EditPopupInjector.ScheduleShow(
                                Localization.L("relation_note_search_placeholder"),
                                _currentSearchFilter,
                                100,
                                onConfirm: (string filterText) =>
                                {
                                    EncyclopediaInputBlocker.Unblock();
                                    _currentSearchFilter = filterText ?? "";
                                    try { EncyclopediaPageTracker.CurrentRefreshAction?.Invoke(); }
                                    catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
                                },
                                onCancel: () =>
                                {
                                    EncyclopediaInputBlocker.Unblock();
                                    _currentSearchFilter = "";
                                    try { EncyclopediaPageTracker.CurrentRefreshAction?.Invoke(); }
                                    catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
                                });
                        }
                        catch (Exception ex)
                        {
                            // Theme 5 fix: ScheduleShow can throw before installing handlers,
                            // leaving the blocker permanently engaged (round 3 finding RNC-2).
                            EncyclopediaInputBlocker.Unblock();
                            MCMSettings.DebugLog("RelNotesSection: search dialog error: " + ex.ToString());
                        }
                    });

                    contentContainer.AddChild(searchButton);
                }

                // Apply search filter
                var filteredEntries = new List<NoteEntry>();
                foreach (var e in entries)
                {
                    if (MatchesFilter(e, _currentSearchFilter))
                        filteredEntries.Add(e);
                }

                // Pre-filter bidirectional for label decisions
                var filteredBidirPrecheck = new List<NoteEntry>();
                foreach (var e in bidirectionalEntries)
                {
                    if (MatchesFilter(e, _currentSearchFilter))
                        filteredBidirPrecheck.Add(e);
                }

                // Resolve viewed hero name for "You → Hero" / "Others → Hero" labels
                string viewedHeroName = ResolveHeroName(heroId) ?? heroId;

                // Empty state hint when no notes exist
                if (filteredEntries.Count == 0 && filteredBidirPrecheck.Count == 0)
                {
                    var hintWidget = new TextWidget(uiContext);
                    hintWidget.Id = "EditableEncyclopedia_RelNotesEmptyHint";
                    hintWidget.Text = Localization.L("ui_relation_hint");
                    hintWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                    hintWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                    if (contentTextBrush != null)
                    {
                        var hintBrush = contentTextBrush.Clone();
                        SetBrushColor(hintBrush, 0.5f, 0.5f, 0.45f, 0.45f); // very dim
                        hintWidget.Brush = hintBrush;
                    }
                    contentContainer.AddChild(hintWidget);
                }

                // Add "You → HeroName:" sub-label if both own + bidirectional notes exist
                if (filteredEntries.Count > 0 && filteredBidirPrecheck.Count > 0)
                {
                    var youLabel = new TextWidget(uiContext);
                    youLabel.Id = "EditableEncyclopedia_RelNotesYouLabel";
                    youLabel.Text = Localization.L("ui_you_label");
                    youLabel.WidthSizePolicy = SizePolicy.CoverChildren;
                    youLabel.HeightSizePolicy = SizePolicy.CoverChildren;
                    youLabel.MarginBottom = 3f;
                    if (contentTextBrush != null)
                    {
                        var youBrush = contentTextBrush.Clone();
                        SetBrushColor(youBrush, 0.7f, 0.65f, 0.5f, 0.7f);
                        youLabel.Brush = youBrush;
                    }
                    contentContainer.AddChild(youLabel);
                }

                // Build note entries with all improvements
                for (int fi = 0; fi < filteredEntries.Count; fi++)
                {
                    var entry = filteredEntries[fi];

                    // Top margin between entries (Lore uses 10f between fields)
                    if (fi > 0)
                    {
                        var spacer = new Widget(uiContext);
                        spacer.WidthSizePolicy = SizePolicy.StretchToParent;
                        spacer.HeightSizePolicy = SizePolicy.Fixed;
                        spacer.SuggestedHeight = 10f;
                        contentContainer.AddChild(spacer);
                    }

                    // Vertical container for two-line layout: metadata row + note text row
                    Widget rowOuter = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel");
                    if (rowOuter == null) rowOuter = new Widget(uiContext);
                    rowOuter.Id = "EditableEncyclopedia_RelNotesRowOuter_" + fi;
                    rowOuter.WidthSizePolicy = SizePolicy.StretchToParent;
                    rowOuter.HeightSizePolicy = SizePolicy.CoverChildren;
                    LoreSectionInjector.SetVerticalLayoutTopToBottom(rowOuter);

                    // Line 1: hero name, score, tags, lock, delete
                    Widget row = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel");
                    if (row == null) row = new Widget(uiContext);
                    row.Id = "EditableEncyclopedia_RelNotesRow_" + fi;
                    row.WidthSizePolicy = SizePolicy.StretchToParent;
                    row.HeightSizePolicy = SizePolicy.CoverChildren;
                    LoreSectionInjector.CopyOrSetHorizontalLayout(null, row);

                    // --- Clan name prefix (shows clan affiliation) ---
                    if (!string.IsNullOrEmpty(entry.ClanName))
                    {
                        var clanWidget = new TextWidget(uiContext);
                        clanWidget.Id = "EditableEncyclopedia_RelNotesClan_" + fi;
                        clanWidget.Text = entry.ClanName + " ";
                        clanWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                        clanWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (contentTextBrush != null)
                        {
                            var clanBrush = contentTextBrush.Clone();
                            SetBrushColor(clanBrush, 0.55f, 0.55f, 0.55f, 1f); // dim gray
                            clanWidget.Brush = clanBrush;
                        }
                        row.AddChild(clanWidget);
                    }

                    // --- Clickable hero name button ---
                    Widget nameButton = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget");
                    if (nameButton == null)
                        nameButton = new Widget(uiContext);
                    nameButton.Id = "EditableEncyclopedia_RelNotesNameBtn_" + fi;
                    nameButton.WidthSizePolicy = SizePolicy.CoverChildren;
                    nameButton.HeightSizePolicy = SizePolicy.CoverChildren;
                    nameButton.DoNotAcceptEvents = false;
                    nameButton.DoNotPassEventsToChildren = true;

                    var nameWidget = new TextWidget(uiContext);
                    nameWidget.Id = "EditableEncyclopedia_RelNotesLabel_" + fi;
                    nameWidget.Text = entry.HeroName;
                    nameWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                    nameWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                    if (labelBrush != null)
                        nameWidget.Brush = labelBrush;
                    else if (contentTextBrush != null)
                        nameWidget.Brush = contentTextBrush;
                    nameButton.AddChild(nameWidget);

                    // Hook click to navigate to hero page
                    string capturedHeroId = entry.OtherHeroId;
                    HookWidgetClick(nameButton, () =>
                    {
                        NavigateToHeroPage(capturedHeroId);
                    });

                    row.AddChild(nameButton);

                    // --- Colored relation score ---
                    if (entry.HasRelationScore)
                    {
                        string sign = entry.RelationScore >= 0 ? "+" : "";
                        var relationWidget = new TextWidget(uiContext);
                        relationWidget.Id = "EditableEncyclopedia_RelNotesScore_" + fi;
                        relationWidget.Text = " " + sign + entry.RelationScore;
                        relationWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                        relationWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (contentTextBrush != null)
                        {
                            var relBrush = contentTextBrush.Clone();
                            if (entry.RelationScore > 0)
                                SetBrushColor(relBrush, 0.2f, 0.8f, 0.2f, 1f);  // green for positive
                            else if (entry.RelationScore < 0)
                                SetBrushColor(relBrush, 0.9f, 0.2f, 0.2f, 1f);  // red for negative
                            else
                                SetBrushColor(relBrush, 0.7f, 0.7f, 0.7f, 1f);  // gray for neutral
                            relationWidget.Brush = relBrush;
                        }
                        row.AddChild(relationWidget);
                    }

                    // --- Hero status tag [Dead] / [Prisoner] ---
                    if (entry.IsDead)
                    {
                        var statusWidget = new TextWidget(uiContext);
                        statusWidget.Id = "EditableEncyclopedia_RelNotesStatus_" + fi;
                        statusWidget.Text = " [Dead]";
                        statusWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                        statusWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (contentTextBrush != null)
                        {
                            var statusBrush = contentTextBrush.Clone();
                            SetBrushColor(statusBrush, 0.6f, 0.3f, 0.3f, 1f); // dark red
                            statusWidget.Brush = statusBrush;
                        }
                        row.AddChild(statusWidget);
                    }
                    else if (entry.IsPrisoner)
                    {
                        var statusWidget = new TextWidget(uiContext);
                        statusWidget.Id = "EditableEncyclopedia_RelNotesStatus_" + fi;
                        statusWidget.Text = " [Prisoner]";
                        statusWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                        statusWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (contentTextBrush != null)
                        {
                            var statusBrush = contentTextBrush.Clone();
                            SetBrushColor(statusBrush, 0.8f, 0.6f, 0.2f, 1f); // amber
                            statusWidget.Brush = statusBrush;
                        }
                        row.AddChild(statusWidget);
                    }

                    // --- Tag badge + lock icon ---
                    if (!string.IsNullOrEmpty(entry.Tag))
                    {
                        string lockIcon = entry.IsTagLocked ? " L" : "";
                        string tagLabel = SafeStringHelper.CapFirst(entry.Tag);

                        var tagWidget = new TextWidget(uiContext);
                        tagWidget.Id = "EditableEncyclopedia_RelNotesTag_" + fi;
                        tagWidget.Text = " [" + tagLabel + lockIcon + "]";
                        tagWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                        tagWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (contentTextBrush != null)
                        {
                            var tagBrush = contentTextBrush.Clone();
                            switch (entry.Tag.ToLowerInvariant())
                            {
                                case "ally": SetBrushColor(tagBrush, 0.3f, 0.7f, 0.4f, 1f); break;
                                case "rival": SetBrushColor(tagBrush, 0.8f, 0.3f, 0.3f, 1f); break;
                                case "trade": SetBrushColor(tagBrush, 0.7f, 0.65f, 0.3f, 1f); break;
                                case "family": SetBrushColor(tagBrush, 0.5f, 0.6f, 0.8f, 1f); break;
                                default: SetBrushColor(tagBrush, 0.6f, 0.6f, 0.6f, 1f); break;
                            }
                            tagWidget.Brush = tagBrush;
                        }

                        // Make tag clickable to change it
                        Widget tagButton = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget");
                        if (tagButton == null) tagButton = new Widget(uiContext);
                        tagButton.Id = "EditableEncyclopedia_RelNotesTagBtn_" + fi;
                        tagButton.WidthSizePolicy = SizePolicy.CoverChildren;
                        tagButton.HeightSizePolicy = SizePolicy.CoverChildren;
                        tagButton.DoNotAcceptEvents = false;
                        tagButton.DoNotPassEventsToChildren = true;
                        tagButton.AddChild(tagWidget);

                        string tagHeroId = heroId;
                        string tagTargetId = entry.OtherHeroId;
                        string tagTargetName = entry.HeroName;
                        HookWidgetClick(tagButton, () =>
                        {
                            ShowTagPicker(tagHeroId, tagTargetId, tagTargetName);
                        });
                        row.AddChild(tagButton);

                        // Lock toggle button
                        Widget lockButton = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget");
                        if (lockButton == null) lockButton = new Widget(uiContext);
                        lockButton.Id = "EditableEncyclopedia_RelNotesLock_" + fi;
                        lockButton.WidthSizePolicy = SizePolicy.CoverChildren;
                        lockButton.HeightSizePolicy = SizePolicy.CoverChildren;
                        lockButton.DoNotAcceptEvents = false;
                        lockButton.DoNotPassEventsToChildren = true;

                        var lockText = new TextWidget(uiContext);
                        lockText.Text = entry.IsTagLocked ? " \u25C9" : " \u25CE"; // ◉ locked / ◎ unlocked
                        lockText.WidthSizePolicy = SizePolicy.CoverChildren;
                        lockText.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (contentTextBrush != null)
                        {
                            var lockBrush = contentTextBrush.Clone();
                            if (entry.IsTagLocked)
                                SetBrushColor(lockBrush, 0.7f, 0.65f, 0.4f, 0.7f); // warm gold when locked
                            else
                                SetBrushColor(lockBrush, 0.5f, 0.5f, 0.5f, 0.4f); // dim gray when unlocked
                            lockText.Brush = lockBrush;
                        }
                        TrySetTooltip(lockButton, entry.IsTagLocked
                            ? Localization.L("relation_note_tag_unlock_confirm")
                            : Localization.L("relation_note_tag_lock_confirm"));
                        lockButton.AddChild(lockText);

                        string lockHeroId = heroId;
                        string lockTargetId = entry.OtherHeroId;
                        bool wasLocked = entry.IsTagLocked;
                        HookWidgetClick(lockButton, () =>
                        {
                            EncyclopediaEditBehavior.Instance?.SetRelationNoteTagLock(
                                lockHeroId, lockTargetId, !wasLocked);
                            MCMSettings.DebugLog("RelNotesSection: " + (wasLocked ? "unlocked" : "locked")
                                + " tag for " + lockHeroId + " -> " + lockTargetId);
                            try { EncyclopediaPageTracker.CurrentRefreshAction?.Invoke(); }
                            catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
                        });
                        row.AddChild(lockButton);
                    }
                    else if (!string.IsNullOrEmpty(entry.SuggestedTag))
                    {
                        // Show suggested tag as a dim clickable badge
                        string sugLabel = SafeStringHelper.CapFirst(entry.SuggestedTag);

                        Widget sugButton = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget");
                        if (sugButton == null) sugButton = new Widget(uiContext);
                        sugButton.Id = "EditableEncyclopedia_RelNotesSugTag_" + fi;
                        sugButton.WidthSizePolicy = SizePolicy.CoverChildren;
                        sugButton.HeightSizePolicy = SizePolicy.CoverChildren;
                        sugButton.DoNotAcceptEvents = false;
                        sugButton.DoNotPassEventsToChildren = true;

                        var sugText = new TextWidget(uiContext);
                        sugText.Text = " [" + sugLabel + "?]";
                        sugText.WidthSizePolicy = SizePolicy.CoverChildren;
                        sugText.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (contentTextBrush != null)
                        {
                            var sugBrush = contentTextBrush.Clone();
                            SetBrushColor(sugBrush, 0.55f, 0.55f, 0.45f, 0.6f);
                            sugText.Brush = sugBrush;
                        }
                        sugButton.AddChild(sugText);

                        string sugHeroId = heroId;
                        string sugTargetId = entry.OtherHeroId;
                        string sugTargetName = entry.HeroName;
                        string sugTag = entry.SuggestedTag;
                        HookWidgetClick(sugButton, () =>
                        {
                            OfferTagSuggestion(sugHeroId, sugTargetId, sugTargetName, sugTag);
                        });
                        row.AddChild(sugButton);
                    }
                    else
                    {
                        // Show a dim [+Tag] button when no tag and no suggestion
                        Widget addTagButton = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget");
                        if (addTagButton == null) addTagButton = new Widget(uiContext);
                        addTagButton.Id = "EditableEncyclopedia_RelNotesAddTag_" + fi;
                        addTagButton.WidthSizePolicy = SizePolicy.CoverChildren;
                        addTagButton.HeightSizePolicy = SizePolicy.CoverChildren;
                        addTagButton.DoNotAcceptEvents = false;
                        addTagButton.DoNotPassEventsToChildren = true;

                        var addTagText = new TextWidget(uiContext);
                        addTagText.Text = " [+Tag]";
                        addTagText.WidthSizePolicy = SizePolicy.CoverChildren;
                        addTagText.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (contentTextBrush != null)
                        {
                            var dimBrush = contentTextBrush.Clone();
                            SetBrushColor(dimBrush, 0.5f, 0.5f, 0.5f, 0.6f);
                            addTagText.Brush = dimBrush;
                        }
                        addTagButton.AddChild(addTagText);

                        string atHeroId = heroId;
                        string atTargetId = entry.OtherHeroId;
                        string atTargetName = entry.HeroName;
                        HookWidgetClick(addTagButton, () =>
                        {
                            ShowTagPicker(atHeroId, atTargetId, atTargetName);
                        });
                        row.AddChild(addTagButton);
                    }

                    // --- Delete button (styled dim, small) ---
                    Widget deleteButton = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget");
                    if (deleteButton == null)
                        deleteButton = new Widget(uiContext);
                    deleteButton.Id = "EditableEncyclopedia_RelNotesDelete_" + fi;
                    deleteButton.WidthSizePolicy = SizePolicy.CoverChildren;
                    deleteButton.HeightSizePolicy = SizePolicy.CoverChildren;
                    deleteButton.DoNotAcceptEvents = false;
                    deleteButton.DoNotPassEventsToChildren = true;
                    deleteButton.MarginLeft = 5f;

                    var deleteText = new TextWidget(uiContext);
                    deleteText.Id = "EditableEncyclopedia_RelNotesDeleteText_" + fi;
                    deleteText.Text = "\u00D7"; // × multiplication sign as clean delete icon
                    deleteText.WidthSizePolicy = SizePolicy.CoverChildren;
                    deleteText.HeightSizePolicy = SizePolicy.CoverChildren;
                    if (contentTextBrush != null)
                    {
                        var delBrush = contentTextBrush.Clone();
                        SetBrushColor(delBrush, 0.6f, 0.35f, 0.35f, 0.5f); // dim red, low opacity
                        deleteText.Brush = delBrush;
                    }
                    TrySetTooltip(deleteButton, Localization.L("relation_note_delete_confirm"));
                    deleteButton.AddChild(deleteText);

                    // Hook click to delete note
                    string delViewingId = heroId;
                    string delTargetId = entry.OtherHeroId;
                    string delTargetName = entry.HeroName;
                    HookWidgetClick(deleteButton, () =>
                    {
                        DeleteNoteFromSection(delViewingId, delTargetId, delTargetName);
                    });

                    row.AddChild(deleteButton);

                    rowOuter.AddChild(row);

                    // Line 2: note text (indented, on its own line)
                    string fullNoteText = entry.NoteText;
                    bool isTruncated = fullNoteText.Length > MaxNotePreviewLength;
                    string displayText = isTruncated
                        ? fullNoteText.Substring(0, MaxNotePreviewLength - 3) + "..."
                        : fullNoteText;

                    Widget noteButton = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget");
                    if (noteButton == null)
                        noteButton = new Widget(uiContext);
                    noteButton.Id = "EditableEncyclopedia_RelNotesValBtn_" + fi;
                    noteButton.WidthSizePolicy = SizePolicy.StretchToParent;
                    noteButton.HeightSizePolicy = SizePolicy.CoverChildren;
                    noteButton.DoNotAcceptEvents = false;
                    noteButton.DoNotPassEventsToChildren = true;
                    noteButton.MarginLeft = 15f; // indent under the hero name

                    var noteWidget = new TextWidget(uiContext);
                    noteWidget.Id = "EditableEncyclopedia_RelNotesVal_" + fi;
                    noteWidget.Text = displayText;
                    noteWidget.WidthSizePolicy = SizePolicy.StretchToParent;
                    noteWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                    noteWidget.HorizontalAlignment = HorizontalAlignment.Left;
                    if (contentTextBrush != null)
                        noteWidget.Brush = contentTextBrush.Clone();
                    LoreSectionInjector.ForceTextAlignLeft(noteWidget);
                    noteButton.AddChild(noteWidget);

                    // Tooltip for full note text on hover (only if truncated)
                    if (isTruncated)
                        TrySetTooltip(noteButton, fullNoteText);

                    // Hook click to edit note
                    string capturedTargetId = entry.OtherHeroId;
                    string capturedTargetName = entry.HeroName;
                    string capturedViewingHeroId = heroId;
                    HookWidgetClick(noteButton, () =>
                    {
                        EditNoteFromSection(capturedViewingHeroId, capturedTargetId, capturedTargetName);
                    });

                    rowOuter.AddChild(noteButton);

                    contentContainer.AddChild(rowOuter);

                    // --- Relation history sub-rows (mini-timeline of relation changes) ---
                    if (entry.History != null && entry.History.Count > 0)
                    {
                        // Show last 5 history entries, most recent first
                        int histStart = Math.Max(0, entry.History.Count - 5);
                        for (int hi = entry.History.Count - 1; hi >= histStart; hi--)
                        {
                            var histEntry = entry.History[hi];
                            Widget histRow = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel");
                            if (histRow == null) histRow = new Widget(uiContext);
                            histRow.Id = "EditableEncyclopedia_RelNotesHist_" + fi + "_" + hi;
                            histRow.WidthSizePolicy = SizePolicy.StretchToParent;
                            histRow.HeightSizePolicy = SizePolicy.CoverChildren;
                            histRow.MarginLeft = 15f;
                            LoreSectionInjector.CopyOrSetHorizontalLayout(null, histRow);

                            // Change indicator (colored +N / -N)
                            var changeWidget = new TextWidget(uiContext);
                            changeWidget.Text = histEntry.Change + " ";
                            changeWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                            changeWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                            if (contentTextBrush != null)
                            {
                                var chBrush = contentTextBrush.Clone();
                                if (histEntry.Change.StartsWith("+"))
                                    SetBrushColor(chBrush, 0.2f, 0.7f, 0.2f, 0.8f);
                                else if (histEntry.Change.StartsWith("-"))
                                    SetBrushColor(chBrush, 0.8f, 0.2f, 0.2f, 0.8f);
                                else
                                    SetBrushColor(chBrush, 0.6f, 0.6f, 0.6f, 0.8f);
                                changeWidget.Brush = chBrush;
                            }
                            histRow.AddChild(changeWidget);

                            // Description text
                            string histText = histEntry.Text;
                            if (histText.Length > HistoryTextMaxLength) histText = histText.Substring(0, HistoryTextMaxLength - HistoryTextEllipsisOffset) + "...";
                            var histTextWidget = new TextWidget(uiContext);
                            histTextWidget.Text = histText;
                            histTextWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                            histTextWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                            if (contentTextBrush != null)
                            {
                                var htBrush = contentTextBrush.Clone();
                                SetBrushColor(htBrush, 0.55f, 0.55f, 0.55f, 0.8f);
                                histTextWidget.Brush = htBrush;
                            }
                            histRow.AddChild(histTextWidget);

                            // Date (dim, right side)
                            var histDateWidget = new TextWidget(uiContext);
                            histDateWidget.Text = " (" + histEntry.Date + ")";
                            histDateWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                            histDateWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                            if (contentTextBrush != null)
                            {
                                var hdBrush = contentTextBrush.Clone();
                                SetBrushColor(hdBrush, 0.45f, 0.45f, 0.45f, 0.6f);
                                histDateWidget.Brush = hdBrush;
                            }
                            histRow.AddChild(histDateWidget);

                            contentContainer.AddChild(histRow);
                        }

                        if (entry.History.Count > 5)
                        {
                            var moreWidget = new TextWidget(uiContext);
                            moreWidget.Text = "  ... " + (entry.History.Count - 5) + " more";
                            moreWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                            moreWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                            moreWidget.MarginLeft = 15f;
                            if (contentTextBrush != null)
                            {
                                var moreBrush = contentTextBrush.Clone();
                                SetBrushColor(moreBrush, 0.45f, 0.45f, 0.45f, 0.5f);
                                moreWidget.Brush = moreBrush;
                            }
                            contentContainer.AddChild(moreWidget);
                        }
                    }
                }

                // === 3b. Bidirectional notes — notes others wrote about this hero ===
                // Already filtered above into filteredBidirPrecheck
                var filteredBidir = filteredBidirPrecheck;

                if (filteredBidir.Count > 0)
                {
                    // Small separator
                    var bidirSpacer = new Widget(uiContext);
                    bidirSpacer.WidthSizePolicy = SizePolicy.StretchToParent;
                    bidirSpacer.HeightSizePolicy = SizePolicy.Fixed;
                    bidirSpacer.SuggestedHeight = 8f;
                    contentContainer.AddChild(bidirSpacer);

                    // Dim sub-label: "Others about {HeroName}:"
                    var othersLabel = new TextWidget(uiContext);
                    othersLabel.Id = "EditableEncyclopedia_RelNotesBidirLabel";
                    othersLabel.Text = Localization.L("ui_others_about", viewedHeroName);
                    othersLabel.WidthSizePolicy = SizePolicy.CoverChildren;
                    othersLabel.HeightSizePolicy = SizePolicy.CoverChildren;
                    othersLabel.MarginBottom = 3f;
                    if (contentTextBrush != null)
                    {
                        var othersBrush = contentTextBrush.Clone();
                        SetBrushColor(othersBrush, 0.6f, 0.55f, 0.45f, 0.6f);
                        othersLabel.Brush = othersBrush;
                    }
                    contentContainer.AddChild(othersLabel);

                    for (int bi = 0; bi < filteredBidir.Count; bi++)
                    {
                        var bEntry = filteredBidir[bi];

                        if (bi > 0)
                        {
                            var bSpacer = new Widget(uiContext);
                            bSpacer.WidthSizePolicy = SizePolicy.StretchToParent;
                            bSpacer.HeightSizePolicy = SizePolicy.Fixed;
                            bSpacer.SuggestedHeight = 5f;
                            contentContainer.AddChild(bSpacer);
                        }

                        Widget bRow = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel");
                        if (bRow == null) bRow = new Widget(uiContext);
                        bRow.Id = "EditableEncyclopedia_RelNotesBidir_" + bi;
                        bRow.WidthSizePolicy = SizePolicy.StretchToParent;
                        bRow.HeightSizePolicy = SizePolicy.CoverChildren;
                        LoreSectionInjector.CopyOrSetHorizontalLayout(null, bRow);

                        // Clickable hero name
                        Widget bNameButton = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget");
                        if (bNameButton == null) bNameButton = new Widget(uiContext);
                        bNameButton.WidthSizePolicy = SizePolicy.CoverChildren;
                        bNameButton.HeightSizePolicy = SizePolicy.CoverChildren;
                        bNameButton.DoNotAcceptEvents = false;
                        bNameButton.DoNotPassEventsToChildren = true;

                        var bNameWidget = new TextWidget(uiContext);
                        bNameWidget.Text = bEntry.HeroName;
                        bNameWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                        bNameWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (labelBrush != null)
                            bNameWidget.Brush = labelBrush;
                        else if (contentTextBrush != null)
                            bNameWidget.Brush = contentTextBrush;
                        bNameButton.AddChild(bNameWidget);

                        string bCapturedId = bEntry.OtherHeroId;
                        HookWidgetClick(bNameButton, () => { NavigateToHeroPage(bCapturedId); });
                        bRow.AddChild(bNameButton);

                        // Tag if present
                        if (!string.IsNullOrEmpty(bEntry.Tag))
                        {
                            var bTagWidget = new TextWidget(uiContext);
                            bTagWidget.Text = " [" + SafeStringHelper.CapFirst(bEntry.Tag) + "]";
                            bTagWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                            bTagWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                            if (contentTextBrush != null)
                            {
                                var bTagBrush = contentTextBrush.Clone();
                                SetBrushColor(bTagBrush, 0.55f, 0.55f, 0.55f, 0.5f);
                                bTagWidget.Brush = bTagBrush;
                            }
                            bRow.AddChild(bTagWidget);
                        }

                        // Vertical container for two-line layout (matches own notes)
                        Widget bRowOuter = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel");
                        if (bRowOuter == null) bRowOuter = new Widget(uiContext);
                        bRowOuter.Id = "EditableEncyclopedia_RelNotesBidirOuter_" + bi;
                        bRowOuter.WidthSizePolicy = SizePolicy.StretchToParent;
                        bRowOuter.HeightSizePolicy = SizePolicy.CoverChildren;
                        LoreSectionInjector.SetVerticalLayoutTopToBottom(bRowOuter);

                        bRowOuter.AddChild(bRow);

                        // Line 2: note text (indented, read-only, dimmed)
                        string bFullText = bEntry.NoteText;
                        bool bTruncated = bFullText.Length > MaxNotePreviewLength;
                        string bDisplayText = bTruncated
                            ? bFullText.Substring(0, MaxNotePreviewLength - 3) + "..."
                            : bFullText;

                        var bNoteWidget = new TextWidget(uiContext);
                        bNoteWidget.Text = bDisplayText;
                        bNoteWidget.WidthSizePolicy = SizePolicy.StretchToParent;
                        bNoteWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                        bNoteWidget.HorizontalAlignment = HorizontalAlignment.Left;
                        bNoteWidget.MarginLeft = 15f; // indent to match own notes
                        if (contentTextBrush != null)
                        {
                            var bNoteBrush = contentTextBrush.Clone();
                            SetBrushColor(bNoteBrush, 0.55f, 0.53f, 0.5f, 0.65f); // distinctly dimmed for read-only
                            bNoteWidget.Brush = bNoteBrush;
                        }
                        LoreSectionInjector.ForceTextAlignLeft(bNoteWidget);
                        bRowOuter.AddChild(bNoteWidget);

                        if (bTruncated)
                            TrySetTooltip(bNoteWidget, bFullText);

                        contentContainer.AddChild(bRowOuter);
                    }
                }

                // === 3c. [+ Add Note] button at bottom of section ===
                {
                    var addNoteSpacer = new Widget(uiContext);
                    addNoteSpacer.WidthSizePolicy = SizePolicy.StretchToParent;
                    addNoteSpacer.HeightSizePolicy = SizePolicy.Fixed;
                    addNoteSpacer.SuggestedHeight = 6f;
                    contentContainer.AddChild(addNoteSpacer);

                    Widget addNoteButton = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget");
                    if (addNoteButton == null) addNoteButton = new Widget(uiContext);
                    addNoteButton.Id = "EditableEncyclopedia_RelNotesAddBtn";
                    addNoteButton.WidthSizePolicy = SizePolicy.CoverChildren;
                    addNoteButton.HeightSizePolicy = SizePolicy.CoverChildren;
                    addNoteButton.DoNotAcceptEvents = false;
                    addNoteButton.DoNotPassEventsToChildren = true;

                    var addNoteText = new TextWidget(uiContext);
                    addNoteText.Text = "+ Add Note";
                    addNoteText.WidthSizePolicy = SizePolicy.CoverChildren;
                    addNoteText.HeightSizePolicy = SizePolicy.CoverChildren;
                    if (contentTextBrush != null)
                    {
                        var addBrush = contentTextBrush.Clone();
                        SetBrushColor(addBrush, 0.6f, 0.55f, 0.4f, 0.5f); // dim gold
                        addNoteText.Brush = addBrush;
                    }
                    addNoteButton.AddChild(addNoteText);

                    string addHeroId = heroId;
                    HookWidgetClick(addNoteButton, () =>
                    {
                        // Open the relation notes picker to select a hero to write a note about
                        try
                        {
                            RelationNotesInjector.ScheduleHeroPicker(addHeroId);
                        }
                        catch (Exception ex)
                        {
                            MCMSettings.DebugLog("RelNotesSection: AddNote click error: " + ex.ToString());
                        }
                    });
                    contentContainer.AddChild(addNoteButton);
                }

                // === 3d. Wire up click-to-collapse on the header button ===
                // Improvement #7: Persist collapse state
                HookCollapseToggleWithPersistence(headerWrapper, contentContainer, arrow,
                    native.ReferenceHeader, heroId);

                // Apply persisted collapse state
                if (_collapseStates.TryGetValue(heroId, out bool isCollapsed) && isCollapsed)
                {
                    contentContainer.IsVisible = false;
                    if (arrow != null)
                    {
                        try
                        {
                            var setStateMethod = arrow.GetType().GetMethod("SetState",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                null, new[] { typeof(string) }, null);
                            if (setStateMethod != null)
                                setStateMethod.Invoke(arrow, new object[] { "Collapsed" });
                        }
                        catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
                    }
                }

                // === 4. Insert header + content into the parent ===
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
                    MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString());
                    parent.AddChild(headerWrapper);
                    parent.AddChild(contentContainer);
                }

                _injectedWidgets.Add(headerWrapper);
                _injectedWidgets.Add(contentContainer);
                MCMSettings.DebugLog("RelNotesSection: injected native-style section with " + entries.Count + " notes");
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("RelNotesSection: build error: " + ex.ToString());
            }
        }

        // ─────────── Tag Picker ───────────

        private static void ShowTagPicker(string heroId, string targetHeroId, string targetName)
        {
            try
            {
                if (EncyclopediaEditBehavior.Instance == null) return;

                string currentTag = EncyclopediaEditBehavior.Instance.GetRelationNoteTag(heroId, targetHeroId) ?? "";
                string title = Localization.L("relation_note_category_title");
                string desc = Localization.L("relation_note_category_message", targetName);

                // Find InquiryElement and MultiSelectionInquiryData types
                Type inquiryElementType = null;
                Type multiSelDataType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (var t in asm.GetTypes())
                        {
                            if (t.Name == "InquiryElement" && inquiryElementType == null)
                                inquiryElementType = t;
                            if (t.Name == "MultiSelectionInquiryData" && multiSelDataType == null)
                                multiSelDataType = t;
                        }
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
                }

                if (inquiryElementType == null || multiSelDataType == null)
                {
                    MCMSettings.DebugLog("RelNotesSection: MultiSelection types not found for tag picker");
                    return;
                }

                var showMethod = MultiSelectionMethodCache.Find(multiSelDataType);
                if (showMethod == null)
                {
                    MCMSettings.DebugLog("RelNotesSection: ShowMultiSelectionInquiry not found for tag picker");
                    return;
                }

                var elementCtor = MultiSelectionMethodCache.FindInquiryElementCtor(inquiryElementType, out bool secondParamIsTextObject);
                if (elementCtor == null) return;

                var elementCtorParams = elementCtor.GetParameters();
                var listType = typeof(List<>).MakeGenericType(inquiryElementType);
                var elements = (System.Collections.IList)Activator.CreateInstance(listType);

                // Build list: one entry per tag, plus a "Clear" option
                foreach (string tag in AvailableTags)
                {
                    string locKey = "relation_note_tag_" + tag;
                    string label = Localization.L(locKey);
                    if (tag == currentTag.ToLowerInvariant())
                        label = "* " + label;
                    elements.Add(MultiSelectionMethodCache.CreateInquiryElement(
                        elementCtor, elementCtorParams, secondParamIsTextObject,
                        tag, label));
                }

                // Add a "Clear" option
                elements.Add(MultiSelectionMethodCache.CreateInquiryElement(
                    elementCtor, elementCtorParams, secondParamIsTextObject,
                    "_clear_", Localization.L("edit_cancel") + " (clear tag)"));

                // Build callbacks
                var actionType = typeof(Action<>).MakeGenericType(listType);

                var handler = new TagPickerHandler(heroId, targetHeroId);
                var onSelectedMethod = typeof(TagPickerHandler).GetMethod("OnSelected");
                var onCancelMethod = typeof(TagPickerHandler).GetMethod("OnCancel");
                if (onSelectedMethod == null || onCancelMethod == null)
                {
                    MCMSettings.DebugLog("[RelationNotesSectionInjector]: GetMethod for TagPickerHandler returned null");
                    return;
                }
                var affParam = System.Linq.Expressions.Expression.Parameter(listType, "elements");
                var affCall = System.Linq.Expressions.Expression.Call(
                    System.Linq.Expressions.Expression.Constant(handler),
                    onSelectedMethod,
                    System.Linq.Expressions.Expression.Convert(affParam, typeof(object)));
                var affirmativeDelegate = System.Linq.Expressions.Expression.Lambda(actionType, affCall, affParam).Compile();

                var negParam = System.Linq.Expressions.Expression.Parameter(listType, "elements");
                var negCall = System.Linq.Expressions.Expression.Call(
                    System.Linq.Expressions.Expression.Constant(handler),
                    onCancelMethod,
                    System.Linq.Expressions.Expression.Convert(negParam, typeof(object)));
                var negativeDelegate = System.Linq.Expressions.Expression.Lambda(actionType, negCall, negParam).Compile();

                // Find constructor
                System.Reflection.ConstructorInfo dataCtor = null;
                foreach (var ctor in multiSelDataType.GetConstructors())
                {
                    var pars = ctor.GetParameters();
                    if (pars.Length >= 8 && (dataCtor == null || pars.Length > dataCtor.GetParameters().Length))
                        dataCtor = ctor;
                }

                if (dataCtor == null)
                {
                    MCMSettings.DebugLog("RelNotesSection: MultiSelectionInquiryData ctor not found for tag picker");
                    return;
                }

                object dataObj = MultiSelectionMethodCache.BuildMultiSelectionInquiryData(
                    dataCtor, title, desc,
                    Localization.L("edit_save"), Localization.L("edit_cancel"),
                    elements, listType, affirmativeDelegate, negativeDelegate);

                var showParams = showMethod.GetParameters();
                if (showParams.Length == 1)
                    showMethod.Invoke(null, new object[] { dataObj });
                else if (showParams.Length == 2)
                    showMethod.Invoke(null, new object[] { dataObj, false });
                else
                    showMethod.Invoke(null, new object[] { dataObj, false, false });

                MCMSettings.DebugLog("RelNotesSection: showing tag picker for " + heroId + " -> " + targetHeroId);
                EncyclopediaNavigationGuard.ScheduleNavigateBack();
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("RelNotesSection: ShowTagPicker error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Shows an Apply/Skip dialog for a suggested tag.
        /// Called when the user clicks a [Ally?] / [Rival?] suggestion badge.
        /// </summary>
        private static void OfferTagSuggestion(string heroId, string targetHeroId, string targetName, string suggestedTag)
        {
            try
            {
                string tagLabel = SafeStringHelper.CapFirst(suggestedTag);
                string title = Localization.L("relation_note_suggest_tag_title");
                string message = Localization.L("relation_note_suggest_tag_message", targetName, tagLabel);

                var inquiryData = new InquiryData(
                    title, message, true, true,
                    Localization.L("relation_note_suggest_tag_apply"),
                    Localization.L("relation_note_suggest_tag_skip"),
                    delegate
                    {
                        EncyclopediaEditBehavior.Instance?.SetRelationNoteTag(heroId, targetHeroId, suggestedTag);
                        MCMSettings.DebugLog("RelNotesSection: applied suggested tag '" + suggestedTag
                            + "' for " + heroId + " -> " + targetHeroId);
                        try { EncyclopediaPageTracker.CurrentRefreshAction?.Invoke(); }
                        catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
                    },
                    delegate
                    {
                        MCMSettings.DebugLog("RelNotesSection: skipped suggested tag for " + targetHeroId);
                    });

                InformationManager.ShowInquiry(inquiryData, false, false);
                EncyclopediaNavigationGuard.ScheduleNavigateBack();
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("RelNotesSection: OfferTagSuggestion error: " + ex.ToString());
            }
        }

        /// <summary>
        /// After saving a NEW note (was empty before), auto-assign tag if strong condition met.
        /// Only applies on first creation, never overwrites.
        /// </summary>
        public static void TryAutoTagOnFirstCreation(string heroId, string targetHeroId)
        {
            try
            {
                if (EncyclopediaEditBehavior.Instance == null) return;

                // Only if no tag is set
                string existing = EncyclopediaEditBehavior.Instance.GetRelationNoteTag(heroId, targetHeroId);
                if (!string.IsNullOrEmpty(existing)) return;

                string suggested = EncyclopediaEditBehavior.Instance.SuggestRelationNoteTag(heroId, targetHeroId);
                if (string.IsNullOrEmpty(suggested)) return;

                EncyclopediaEditBehavior.Instance.SetRelationNoteTag(heroId, targetHeroId, suggested);
                MCMSettings.DebugLog("RelNotesSection: auto-tagged '" + suggested
                    + "' on first creation for " + heroId + " -> " + targetHeroId);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("RelNotesSection: TryAutoTagOnFirstCreation error: " + ex.ToString());
            }
        }

        private class TagPickerHandler
        {
            private readonly string _heroId;
            private readonly string _targetHeroId;

            public TagPickerHandler(string heroId, string targetHeroId)
            {
                _heroId = heroId;
                _targetHeroId = targetHeroId;
            }

            public void OnSelected(object elementsObj)
            {
                try
                {
                    var list = elementsObj as System.Collections.IList;
                    if (list == null || list.Count == 0) return;

                    string selectedTag = MultiSelectionMethodCache.GetInquiryElementId(list[0]);
                    if (string.IsNullOrEmpty(selectedTag)) return;

                    if (selectedTag == "_clear_")
                    {
                        EncyclopediaEditBehavior.Instance?.SetRelationNoteTag(_heroId, _targetHeroId, "");
                        MCMSettings.DebugLog("RelNotesSection: cleared tag for " + _heroId + " -> " + _targetHeroId);
                    }
                    else
                    {
                        EncyclopediaEditBehavior.Instance?.SetRelationNoteTag(_heroId, _targetHeroId, selectedTag);
                        MCMSettings.DebugLog("RelNotesSection: set tag '" + selectedTag + "' for " + _heroId + " -> " + _targetHeroId);
                    }

                    try { EncyclopediaPageTracker.CurrentRefreshAction?.Invoke(); }
                    catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
                }
                catch (Exception ex)
                {
                    MCMSettings.DebugLog("RelNotesSection: TagPickerHandler.OnSelected error: " + ex.ToString());
                }
            }

            public void OnCancel(object elementsObj)
            {
                MCMSettings.DebugLog("RelNotesSection: tag picker cancelled");
            }
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
                            MCMSettings.DebugLog("RelNotesSection: click handler error: " + ex.ToString());
                        }
                    }
                };

                eventFireEvent.AddEventHandler(widget, handler);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("RelNotesSection: HookWidgetClick error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Defers navigation to a hero's encyclopedia page.
        /// Delegates to JournalSectionInjector.ScheduleNavigation which handles
        /// the deferred tick-based navigation with ExecuteLink and GoToLink fallback.
        /// </summary>
        private static void NavigateToHeroPage(string heroId)
        {
            string link = "Encyclopedia/Hero/" + heroId;
            MCMSettings.DebugLog("RelNotesSection: scheduling navigation to " + link);
            JournalSectionInjector.ScheduleNavigation(link);
        }

        /// <summary>
        /// Opens the note editor for editing an existing note from the section.
        /// Uses the same flow as RelationNotesInjector.ShowNotePopup.
        /// </summary>
        private static void EditNoteFromSection(string viewingHeroId, string targetHeroId, string targetName)
        {
            try
            {
                if (EncyclopediaEditBehavior.Instance == null) return;

                string existingNote = EncyclopediaEditBehavior.Instance.GetRelationNote(viewingHeroId, targetHeroId) ?? "";
                bool isNewNote = string.IsNullOrEmpty(existingNote);
                string title = Localization.L("relation_note_title", targetName);

                // Show suggestion in title if available
                string suggested = EncyclopediaEditBehavior.Instance.SuggestRelationNoteTag(viewingHeroId, targetHeroId);
                if (!string.IsNullOrEmpty(suggested) && isNewNote)
                {
                    string sugLabel = SafeStringHelper.CapFirst(suggested);
                    title += " [" + sugLabel + "?]";
                }

                EncyclopediaInputBlocker.Block();

                const int maxNoteLength = 500;

                EditPopupInjector.ScheduleShow(
                    title,
                    existingNote,
                    maxNoteLength,
                    onConfirm: (string newText) =>
                    {
                        EncyclopediaInputBlocker.Unblock();

                        // Enforce max length — truncate and warn if somehow exceeded
                        if (!string.IsNullOrEmpty(newText) && newText.Length > maxNoteLength)
                        {
                            MCMSettings.DebugLog("RelNotesSection: note too long (" + newText.Length + "/" + maxNoteLength
                                + "), truncating for " + targetHeroId);
                            newText = newText.Substring(0, maxNoteLength);
                            try
                            {
                                string warnMsg = Localization.L("relation_note_too_long",
                                    newText.Length.ToString(), maxNoteLength.ToString());
                                InformationManager.DisplayMessage(new InformationMessage(warnMsg));
                            }
                            catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
                        }

                        EncyclopediaEditBehavior.Instance.SetRelationNote(viewingHeroId, targetHeroId, newText);
                        MCMSettings.DebugLog("RelNotesSection: saved note for " + viewingHeroId + " -> " + targetHeroId
                            + " (" + (string.IsNullOrEmpty(newText) ? "cleared" : newText.Length + " chars") + ")");

                        // Auto-tag on first creation only (strong signals)
                        if (isNewNote && !string.IsNullOrEmpty(newText))
                            TryAutoTagOnFirstCreation(viewingHeroId, targetHeroId);

                        try { EditableEncyclopediaAPI.RaiseRelationNoteChanged(viewingHeroId, targetHeroId, newText); }
                        catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }

                        try { EncyclopediaPageTracker.CurrentRefreshAction?.Invoke(); }
                        catch (Exception ex) { MCMSettings.DebugLog("RelNotesSection: refresh error: " + ex.ToString()); }
                    },
                    onCancel: () =>
                    {
                        EncyclopediaInputBlocker.Unblock();
                        MCMSettings.DebugLog("RelNotesSection: cancelled note edit for " + targetHeroId);
                    }
                );
            }
            catch (Exception ex)
            {
                // Theme 5 fix: ensure blocker isn't permanently engaged on synchronous
                // exception path (round 3 finding RNC-2).
                EncyclopediaInputBlocker.Unblock();
                MCMSettings.DebugLog("RelNotesSection: EditNoteFromSection error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Deletes a note after confirmation via native inquiry dialog.
        /// </summary>
        private static void DeleteNoteFromSection(string viewingHeroId, string targetHeroId, string targetName)
        {
            try
            {
                if (EncyclopediaEditBehavior.Instance == null) return;

                string title = Localization.L("relation_note_delete_title");
                string message = Localization.L("relation_note_delete_message", targetName);

                var inquiryData = new InquiryData(
                    title, message, true, true,
                    Localization.L("relation_note_delete_confirm"),
                    Localization.L("edit_cancel"),
                    delegate
                    {
                        EncyclopediaEditBehavior.Instance.SetRelationNote(viewingHeroId, targetHeroId, "");
                        MCMSettings.DebugLog("RelNotesSection: deleted note for " + viewingHeroId + " -> " + targetHeroId);

                        try { EditableEncyclopediaAPI.RaiseRelationNoteChanged(viewingHeroId, targetHeroId, ""); }
                        catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }

                        try { EncyclopediaPageTracker.CurrentRefreshAction?.Invoke(); }
                        catch (Exception ex) { MCMSettings.DebugLog("RelNotesSection: refresh error: " + ex.ToString()); }
                    },
                    delegate
                    {
                        MCMSettings.DebugLog("RelNotesSection: delete cancelled for " + targetHeroId);
                    });

                InformationManager.ShowInquiry(inquiryData, false, false);
                EncyclopediaNavigationGuard.ScheduleNavigateBack();
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("RelNotesSection: DeleteNoteFromSection error: " + ex.ToString());
            }
        }

        // ─────────── Collapse Toggle with Persistence ───────────

        /// <summary>
        /// Hooks collapse toggle like LoreSectionInjector.HookCollapseToggle but also
        /// persists the collapse state in _collapseStates.
        /// </summary>
        private static void HookCollapseToggleWithPersistence(Widget headerWidget, Widget contentContainer,
            BrushWidget arrow, Widget nativeHeader, string heroId)
        {
            try
            {
                var eventFireEvent = headerWidget.GetType().GetEvent("EventFire",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (eventFireEvent == null)
                {
                    MCMSettings.DebugLog("RelNotesSection: EventFire event not found on header widget");
                    return;
                }

                Action<Widget, string, object[]> eventHandler = (Widget sender, string eventName, object[] args) =>
                {
                    if (eventName == "Click")
                    {
                        bool wasVisible = contentContainer.IsVisible;
                        contentContainer.IsVisible = !wasVisible;

                        // Persist collapse state
                        _collapseStates[heroId] = wasVisible; // wasVisible=true → now collapsed

                        if (arrow != null)
                        {
                            string newState = wasVisible ? "Collapsed" : "Expanded";
                            try
                            {
                                var setStateMethod = arrow.GetType().GetMethod("SetState",
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                    null, new[] { typeof(string) }, null);
                                if (setStateMethod != null)
                                    setStateMethod.Invoke(arrow, new object[] { newState });
                            }
                            catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
                        }
                        MCMSettings.DebugLog("RelNotesSection: toggled to "
                            + (wasVisible ? "collapsed" : "expanded"));
                    }
                };

                eventFireEvent.AddEventHandler(headerWidget, eventHandler);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("RelNotesSection: HookCollapseToggle error: " + ex.ToString());
            }
        }

        // ─────────── Brush & Tooltip Helpers ───────────

        private static void SetBrushColor(Brush brush, float r, float g, float b, float a)
        {
            if (brush == null) return;
            try
            {
                var color = new Color(r, g, b, a);
                var fontColorProp = brush.GetType().GetProperty("FontColor",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fontColorProp != null && fontColorProp.CanWrite)
                {
                    fontColorProp.SetValue(brush, color);
                    return;
                }
                foreach (var layersPropName in new[] { "TextLayers", "Layers" })
                {
                    var layersProp = brush.GetType().GetProperty(layersPropName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (layersProp == null) continue;
                    var layers = layersProp.GetValue(brush) as System.Collections.IEnumerable;
                    if (layers == null) continue;
                    foreach (var layer in layers)
                    {
                        var colorProp = layer.GetType().GetProperty("Color",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (colorProp != null && colorProp.CanWrite)
                            colorProp.SetValue(layer, color);
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
        }

        /// <summary>
        /// Sets a tooltip (HintViewModel) on a widget so the full text shows on hover.
        /// Uses reflection since HintViewModel may not be directly available.
        /// </summary>
        private static void TrySetTooltip(Widget widget, string tooltipText)
        {
            try
            {
                // Find HintViewModel type
                Type hintVmType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    hintVmType = asm.GetType("TaleWorlds.Library.HintViewModel");
                    if (hintVmType != null) break;
                    hintVmType = asm.GetType("TaleWorlds.Core.ViewModelCollection.HintViewModel");
                    if (hintVmType != null) break;
                    hintVmType = asm.GetType("TaleWorlds.CampaignSystem.ViewModelCollection.HintViewModel");
                    if (hintVmType != null) break;
                }
                if (hintVmType == null)
                {
                    // Try broader search
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            foreach (var t in asm.GetTypes())
                            {
                                if (t.Name == "HintViewModel")
                                {
                                    hintVmType = t;
                                    break;
                                }
                            }
                        }
                        catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
                        if (hintVmType != null) break;
                    }
                }

                if (hintVmType == null)
                {
                    MCMSettings.DebugLog("RelNotesSection: HintViewModel type not found for tooltip");
                    return;
                }

                // Find TextObject type for the constructor parameter
                Type textObjType = typeof(TaleWorlds.Localization.TextObject);

                // Create TextObject with our tooltip text
                var textObj = new TaleWorlds.Localization.TextObject(tooltipText);

                // Create HintViewModel(TextObject)
                var ctor = hintVmType.GetConstructor(new[] { textObjType });
                if (ctor == null)
                {
                    // Try constructor with TextObject and string
                    ctor = hintVmType.GetConstructor(new[] { textObjType, typeof(string) });
                    if (ctor != null)
                    {
                        var hint = ctor.Invoke(new object[] { textObj, null });
                        SetWidgetHint(widget, hint);
                        return;
                    }
                    MCMSettings.DebugLog("RelNotesSection: HintViewModel ctor not found");
                    return;
                }

                var hintObj = ctor.Invoke(new object[] { textObj });
                SetWidgetHint(widget, hintObj);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("RelNotesSection: TrySetTooltip error: " + ex.ToString());
            }
        }

        private static void SetWidgetHint(Widget widget, object hintViewModel)
        {
            try
            {
                var eventFireEvent = widget.GetType().GetEvent("EventFire",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (eventFireEvent == null) return;

                // Cache reflection lookups for InformationManager hint methods
                var imType = typeof(InformationManager);
                var pubStatic = BindingFlags.Static | BindingFlags.Public;

                // Try ShowHint(string) first, then AddHintInformation(string)
                var showHint = imType.GetMethod("ShowHint", pubStatic, null, new[] { typeof(string) }, null)
                            ?? imType.GetMethod("AddHintInformation", pubStatic, null, new[] { typeof(string) }, null);

                var hideHint = imType.GetMethod("HideInformations", pubStatic, null, Type.EmptyTypes, null);

                if (showHint == null)
                {
                    MCMSettings.DebugLog("RelNotesSection: no ShowHint/AddHintInformation method found");
                    return;
                }

                string tooltipStr = hintViewModel.ToString();
                Action<Widget, string, object[]> handler = (Widget sender, string eventName, object[] args) =>
                {
                    try
                    {
                        if (eventName == "HoverBegin")
                            showHint.Invoke(null, new object[] { tooltipStr });
                        else if (eventName == "HoverEnd" && hideHint != null)
                            hideHint.Invoke(null, null);
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesSectionInjector]: " + ex.ToString()); }
                };

                eventFireEvent.AddEventHandler(widget, handler);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("RelNotesSection: SetWidgetHint error: " + ex.ToString());
            }
        }

        // ─────────── Hero Resolution ───────────

        private static Hero FindHero(string heroId)
        {
            foreach (var h in Hero.AllAliveHeroes)
            {
                if (h?.StringId == heroId)
                    return h;
            }
            foreach (var h in Hero.DeadOrDisabledHeroes)
            {
                if (h?.StringId == heroId)
                    return h;
            }
            return null;
        }

        private static string ResolveHeroName(string heroId)
        {
            // Check custom name first
            if (EncyclopediaEditBehavior.Instance != null)
            {
                string customName = EncyclopediaEditBehavior.Instance.GetCustomName(heroId);
                if (!string.IsNullOrEmpty(customName))
                    return customName;
            }

            Hero hero = FindHero(heroId);
            return hero?.Name?.ToString();
        }
    }
}
