using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.ScreenSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Injects a "Journal" section into the encyclopedia page widget tree.
    /// Displays Chronicle Notes (dated journal entries) below the description/activity area on the right side.
    /// Uses the same layer/widget discovery approach as TimestampWidgetInjector and LoreSectionInjector.
    /// </summary>
    public static class JournalSectionInjector
    {
        private static readonly BindingFlags AllFlags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static readonly List<Widget> _injectedWidgets = new List<Widget>();

        // Pagination state
        private static int EntriesPerPage => MCMSettings.Instance?.JournalPageSize ?? 10;
        private static int _currentPage = 0;
        private static string _currentObjectId;

        // Category filter state — true = show entries with this category
        private static bool _filterWar = true;
        private static bool _filterPolitics = true;
        private static bool _filterCrime = true;
        private static bool _filterFamily = true;
        private static bool _filterOther = true; // entries with no category tag

        // True when injecting into the narrow child[1] sidebar (Naval DLC fallback)
        private static bool _narrowSidebarMode = false;
        // The computed sidebar width in pixels (used to force fixed widths on children)
        private static float _sidebarWidth = DefaultSidebarWidth;

        // Retry state — widget tree may not be ready on page navigation.
        // _pendingObjectId / _refreshObjectId volatile so main thread sees latest payload
        // after observing _retryPending or _refreshPending = true (Theme 1: round 2 finding JC-1).
        private static volatile string _pendingObjectId;
        private static int _retryCount;
        private static System.Threading.Timer _retryTimer;
        private static volatile bool _retryPending;
        private static volatile bool _refreshPending;
        private static volatile string _refreshObjectId;
        private const int MaxRetries = 10;
        private const int RetryDelayMs = 150;

        private const float DefaultSidebarWidth = 300f;
        private const float MinSidebarWidth = 180f;
        private const float MaxSidebarWidth = 400f;
        private const float SidebarBreathingRoom = 30f;
        private const float MinMeasurableWidth = 100f;
        private const float HeaderMarginTopFallback = 15f;
        private const float NarrowHeaderMarginTop = 12f;
        private const float ContentMarginLeftDefault = 25f;
        private const float ContentMarginLeftExtraPx = 5f;
        private const float NarrowMaxContentMarginLeft = 15f;
        private const float ContentMarginRight = 10f;
        private const float ContentMarginBottom = 5f;
        private const float DividerLineHeightFallback = 4f;
        private const float SeparatorHeight = 1f;
        private const float NarrowTitleFontScale = 0.55f;
        private const float AvgCharWidthPx = 11.4f;
        private const int MinCharsPerLine = 15;
        private const float MinTextWidth = 120f;
        private const float NarrowTextPadding = 24f;
        private const int EditPopupMaxLength = 500;
        private const int MinDescriptionTextLength = 30;
        private const int MinLayerDescendants = 10;
        private const int MaxLayerDescendants = 2000;
        private const int DebugTextSnippetLength = 30;

        // Track which page types we've already logged methods for (diagnostics)
        private static readonly System.Collections.Generic.HashSet<string> _pageMethodsLogged
            = new System.Collections.Generic.HashSet<string>();

        // When true, GauntletLayer.ReleaseMovie skips null identifiers instead of crashing.
        // This allows CloseEncyclopedia to run fully even when movies weren't loaded.
        internal static volatile bool _suppressReleaseMovieNull;
        private static bool _releaseMoviePatchApplied;

        // Safety net: suppresses exceptions in outer CloseEncyclopedia
        internal static volatile bool _suppressCloseNullRef;
        // Set to true by CloseEncyclopediaFinalizer when it suppresses a crash,
        // indicating CloseEncyclopedia did NOT fully run its cleanup code.
        internal static volatile bool _closeEncyclopediaFailed;
        private static bool _closeEncyclopediaPatchApplied;

        // When true, EncyclopediaData.OnTick is completely skipped via prefix patch.
        // Safety net to prevent OnTick from re-enabling input restrictions after cleanup.
        internal static volatile bool _encyclopediaCorrupted;
        private static bool _onTickPatchApplied;

        // Cache of pageIds that have no handler in any loaded assembly.
        // Avoids rescanning 142+ assemblies on every repeated attempt.
        private static readonly System.Collections.Generic.HashSet<string> _missingPageCache
            = new System.Collections.Generic.HashSet<string>();

        // Navigation page types that are handled inline in vanilla's SetEncyclopediaPage
        // but missing from NavalDLC. These should NOT trigger assembly scanning.
        private static readonly System.Collections.Generic.HashSet<string> _navigationPageIds
            = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Home", "ListPage", "LastPage" };

        // Tracks whether the encyclopedia currently has a loaded page.
        // Set true after a successful SetEncyclopediaPage, cleared on close.
        // Used to distinguish initial open (redirect to Hero) from
        // in-encyclopedia navigation (skip and stay on current page).
        internal static volatile bool _encyclopediaHasPage;

        // Tracks the last navigation pageId added to _pages (e.g., "ListPage", "Home").
        // Cleared after the original method runs (success or failure).
        // Used by the finalizer to clean up temporary _pages entries.
        private static volatile string _lastNavPageIdAdded;

        // Finalizer on GauntletMapEncyclopediaView.ExecuteLink to handle
        // KeyNotFoundException from SetEncyclopediaPage (NavalDLC bug).
        // This covers ALL code paths: Chronicle links, N key, etc.
        private static bool _executeLinkFinalizerApplied;

        // Prefix on EncyclopediaData.SetEncyclopediaPage to check if the pageId
        // exists in _pages before accessing it. Prevents KeyNotFoundException at source.
        private static bool _setEncyclopediaPagePatchApplied;

        // Deferred navigation — GoToLink must not be called inside a widget event handler
        // because it tears down the widget tree mid-update. We store the link and process
        // it on the next OnApplicationTick via TickMainThread().
        private static volatile string _pendingNavLink;

        /// <summary>
        /// Schedules a deferred encyclopedia navigation. Safe to call from widget event handlers.
        /// The navigation will execute on the next TickMainThread call (outside the event handler).
        /// Link format: "Encyclopedia/Hero/lord_1_1" or "Encyclopedia/Settlement/town_ES3"
        /// </summary>
        public static void ScheduleNavigation(string encyclopediaLink)
        {
            _pendingNavLink = encyclopediaLink;
        }

        /// <summary>
        /// Must be called from the main game thread (e.g., OnApplicationTick).
        /// Processes deferred retry/refresh operations.
        /// </summary>
        public static void TickMainThread()
        {
            if (_refreshPending)
            {
                _refreshPending = false;
                if (!string.IsNullOrEmpty(_refreshObjectId))
                    InjectJournalSection(_refreshObjectId);
            }
            if (_retryPending)
            {
                _retryPending = false;
                if (!string.IsNullOrEmpty(_pendingObjectId))
                    DoInject(_pendingObjectId);
            }
            // Process deferred encyclopedia link navigation (must happen outside widget event handlers)
            var navLink = _pendingNavLink;
            if (navLink != null)
            {
                _pendingNavLink = null;
                NavigateToEncyclopediaLink(navLink);
            }
        }

        /// <summary>
        /// Schedules a journal section refresh on the next main-thread tick.
        /// Safe to call from any thread.
        /// </summary>
        public static void ScheduleRefresh(string objectId)
        {
            _refreshObjectId = objectId;
            _refreshPending = true;
        }

        /// <summary>
        /// Schedules removal of journal widgets on the next main-thread tick.
        /// </summary>
        public static void ScheduleClear()
        {
            _refreshObjectId = null;
            _refreshPending = false;
            _retryPending = false;
            DisposeRetryTimer();
        }

        /// <summary>
        /// Injects journal entries for the given object into the encyclopedia page.
        /// Call from HeroPageRefreshPatch or other page Postfix patches.
        /// </summary>
        public static void InjectJournalSection(string objectId)
        {
            DisposeRetryTimer();
            _retryPending = false;
            _pendingObjectId = objectId;
            _retryCount = 0;
            DoInject(objectId);
        }

        private static void DoInject(string objectId)
        {
            try
            {
                RemoveOldWidgets();

                // Reset pagination when viewing a different object
                if (_currentObjectId != objectId)
                {
                    _currentPage = 0;
                    _currentObjectId = objectId;
                }

                if (EncyclopediaEditBehavior.Instance == null) return;

                bool enabled = true;
                try
                {
                    var s = MCMSettings.Instance;
                    if (s != null) enabled = s.EnableJournal;
                }
                catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: failed to read EnableJournal setting: " + ex.ToString()); }
                if (!enabled) return;

                var rawEntries = EncyclopediaEditBehavior.Instance.GetJournalEntries(objectId);
                // Deduplicate entries by display text (strips entity markers for comparison)
                var entries = new List<JournalEntry>();
                var seenTexts = new HashSet<string>();
                foreach (var e in rawEntries)
                {
                    string stripped = EncyclopediaEditBehavior.StripEntityMarkers(e.Text);
                    if (seenTexts.Add(stripped))
                        entries.Add(e);
                }
                // Show section even with 0 entries so the "Add Note" button is accessible
                // (only skip if journal feature is disabled above)

                var topScreen = ScreenManager.TopScreen;
                if (topScreen == null) return;

                GauntletLayer encLayer = FindEncyclopediaLayer(topScreen);
                if (encLayer == null)
                {
                    ScheduleRetryIfNeeded("JournalSection: no encyclopedia layer");
                    return;
                }

                Widget layerRoot = GetLayerRootWidget(encLayer);
                if (layerRoot == null)
                {
                    MCMSettings.DebugLog("JournalSection: layer root is null");
                    return;
                }

                UIContext uiContext = layerRoot.EventManager?.Context as UIContext;
                if (uiContext == null)
                {
                    MCMSettings.DebugLog("JournalSection: cannot get UIContext");
                    return;
                }

                Widget encWindow = FindLargestChild(layerRoot);
                if (encWindow == null)
                {
                    MCMSettings.DebugLog("JournalSection: no encyclopedia window found");
                    return;
                }

                // Reset narrow sidebar mode each injection pass
                _narrowSidebarMode = false;

                // Primary: find the container injected by UIExtenderEx PrefabExtension (XPath patch)
                Widget chronicleContainer = FindWidgetById(encWindow, "EditableEncyclopedia_ChronicleContainer", 0);

                if (chronicleContainer != null)
                {
                    MCMSettings.DebugLog("JournalSection: found XPath-injected container, children=" + chronicleContainer.ChildCount);
                }
                else
                {
                    // Fallback: inject into child[1] of #RightSideList (the right sub-panel).
                    // In Naval DLC this panel has w=0 and no layout, so we fix it.
                    MCMSettings.DebugLog("JournalSection: XPath container not found, falling back to #RightSideList child[1]");
                    Widget rightSideList = FindWidgetById(encWindow, "RightSideList", 0);
                    if (rightSideList != null && rightSideList.ChildCount >= 2)
                    {
                        Widget sidebar = rightSideList.GetChild(1);
                        Widget mainCol = rightSideList.GetChild(0);

                        // Compute dynamic width: use whatever space remains after child[0]
                        // Try MeasuredSize first (computed layout), fall back to SuggestedWidth
                        float parentW = 0f;
                        float mainColW = 0f;
                        try
                        {
                            var msizeProp = typeof(Widget).GetProperty("MeasuredSize", AllFlags);
                            if (msizeProp != null)
                            {
                                var parentMs = msizeProp.GetValue(rightSideList);
                                var mainMs = msizeProp.GetValue(mainCol);
                                if (parentMs != null && mainMs != null)
                                {
                                    var xField = parentMs.GetType().GetField("X");
                                    var xProp = parentMs.GetType().GetProperty("X");
                                    if (xField != null)
                                    {
                                        parentW = Convert.ToSingle(xField.GetValue(parentMs));
                                        mainColW = Convert.ToSingle(xField.GetValue(mainMs));
                                    }
                                    else if (xProp != null)
                                    {
                                        parentW = Convert.ToSingle(xProp.GetValue(parentMs));
                                        mainColW = Convert.ToSingle(xProp.GetValue(mainMs));
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: MeasuredSize reflection failed: " + ex.ToString()); }
                        // Fallback to SuggestedWidth if MeasuredSize not available
                        if (parentW < MinMeasurableWidth) parentW = rightSideList.SuggestedWidth;
                        if (mainColW < MinMeasurableWidth) mainColW = mainCol.SuggestedWidth;

                        float sidebarW = DefaultSidebarWidth;
                        if (parentW > MinMeasurableWidth && mainColW > MinMeasurableWidth && parentW > mainColW)
                        {
                            sidebarW = parentW - mainColW - SidebarBreathingRoom;
                            if (sidebarW < MinSidebarWidth) sidebarW = MinSidebarWidth;
                            if (sidebarW > MaxSidebarWidth) sidebarW = MaxSidebarWidth;
                        }

                        // Fix the broken panel: give it width and vertical layout
                        sidebar.WidthSizePolicy = SizePolicy.Fixed;
                        sidebar.SuggestedWidth = sidebarW;
                        sidebar.MaxWidth = sidebarW;
                        sidebar.HeightSizePolicy = SizePolicy.CoverChildren;
                        sidebar.VerticalAlignment = VerticalAlignment.Top;
                        sidebar.MarginTop = 0f;
                        sidebar.ClipContents = true;
                        LoreSectionInjector.SetVerticalLayoutTopToBottom(sidebar);
                        MCMSettings.DebugLog("JournalSection: fixed child[1] w=" + sidebarW
                            + " (parent=" + parentW + " mainCol=" + mainColW + ") children=" + sidebar.ChildCount);

                        // Clear any leftover children from prior injection
                        // (RemoveOldWidgets can't track across page navigations)
                        while (sidebar.ChildCount > 0)
                            sidebar.RemoveChild(sidebar.GetChild(0));

                        // Track that we're in narrow sidebar mode for compact entry rendering
                        _narrowSidebarMode = true;
                        _sidebarWidth = sidebarW;
                        chronicleContainer = sidebar;
                    }
                }

                if (chronicleContainer == null)
                {
                    ScheduleRetryIfNeeded("JournalSection: no chronicle container found");
                    return;
                }

                MCMSettings.DebugLog("JournalSection: target panel type=" + chronicleContainer.GetType().Name
                    + " children=" + chronicleContainer.ChildCount + " id=" + (chronicleContainer.Id ?? ""));

                // Find the main content ListPanel for native section parts extraction (brushes, collapse arrow)
                Widget mainContentPanel = FindMainContentPanel(encWindow);
                BuildAndInsertJournalSection(uiContext, chronicleContainer, mainContentPanel ?? encWindow, encWindow, entries);

                MCMSettings.DebugLog("JournalSection: injected " + entries.Count + " entries for " + objectId);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: error: " + ex.ToString());
            }
        }

        private static void BuildAndInsertJournalSection(UIContext uiContext,
            Widget parent, Widget sectionSource, Widget brushSource, List<JournalEntry> entries)
        {
            // Grab brushes from the broader encyclopedia window for text styling
            Brush dividerLineBrush = FindDividerImageBrush(brushSource)
                                    ?? FindDividerImageBrush(sectionSource);

            Brush textBrush = FindBrushByName(brushSource, "Encyclopedia.SubPage.Info.Text")
                              ?? FindBrushByName(brushSource, "Encyclopedia.Stat.ValueText")
                              ?? FindAnyTextBrush(brushSource);

            // Find the description RichTextWidget brush — it renders left-aligned natively
            Brush descriptionBrush = FindDescriptionBrush(brushSource)
                                     ?? FindDescriptionBrush(sectionSource);
            Brush entryBrush = descriptionBrush ?? textBrush;

            // Find a native divider line widget for sprite cloning
            Widget nativeLineWidget = FindNativeDividerLine(sectionSource)
                                      ?? FindNativeDividerLine(brushSource);

            MCMSettings.DebugLog("JournalSection: textBrush=" + (textBrush?.Name ?? "null")
                + " descriptionBrush=" + (descriptionBrush?.Name ?? "null")
                + " dividerLineBrush=" + (dividerLineBrush?.Name ?? "null"));

            Brush journalEntryBrush = entryBrush ?? textBrush;
            string headerText = Localization.L("journal_section_header");
            if (entries.Count > 0)
                headerText += " (" + entries.Count + ")";

            // === Extract native section parts for collapsible header cloning ===
            // Search sectionSource first (the main content ListPanel where Owner/Notable dividers live)
            var native = LoreSectionInjector.ExtractNativeSectionParts(sectionSource);
            if (native.ReferenceHeader == null)
            {
                // Try brushSource (the full encyclopedia window) as fallback
                native = LoreSectionInjector.ExtractNativeSectionParts(brushSource);
            }
            if (native.ReferenceHeader == null)
            {
                // Divider buttons may be nested deeper — try children
                for (int i = 0; i < brushSource.ChildCount && native.ReferenceHeader == null; i++)
                {
                    native = LoreSectionInjector.ExtractNativeSectionParts(brushSource.GetChild(i));
                    if (native.ReferenceHeader == null)
                    {
                        var child = brushSource.GetChild(i);
                        for (int j = 0; j < child.ChildCount && native.ReferenceHeader == null; j++)
                            native = LoreSectionInjector.ExtractNativeSectionParts(child.GetChild(j));
                    }
                }
            }
            MCMSettings.DebugLog("JournalSection: native parts — indicator="
                + (native.NativeIndicator != null) + " header=" + (native.ReferenceHeader != null)
                + " line=" + (native.NativeLine != null));

            // === 1. Create collapsible header (ButtonWidget) ===
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
                            headerWrapper = ctor.Invoke(new object[] { uiContext }) as Widget;
                        break;
                    }
                    t = t.BaseType;
                }
            }
            if (headerWrapper == null)
                headerWrapper = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget");
            if (headerWrapper == null)
                headerWrapper = new Widget(uiContext);

            headerWrapper.Id = "EditableEncyclopediaJournalDivider";
            headerWrapper.WidthSizePolicy = SizePolicy.StretchToParent;
            headerWrapper.HeightSizePolicy = SizePolicy.CoverChildren;
            headerWrapper.ClipContents = true;
            if (native.ReferenceHeader != null)
            {
                headerWrapper.MarginTop = native.ReferenceHeader.MarginTop;
                headerWrapper.MarginBottom = native.ReferenceHeader.MarginBottom;
                headerWrapper.MarginLeft = native.ReferenceHeader.MarginLeft;
                headerWrapper.MarginRight = native.ReferenceHeader.MarginRight;
            }
            else
            {
                // No native header to copy from — add spacing so Journal doesn't
                // visually collapse into the "Never seen before" text above it.
                headerWrapper.MarginTop = HeaderMarginTopFallback;
            }
            // In narrow sidebar mode: small top margin so Chronicle doesn't crowd the top
            if (_narrowSidebarMode)
            {
                headerWrapper.MarginTop = NarrowHeaderMarginTop;
                headerWrapper.MarginLeft = 0f;
                headerWrapper.MarginRight = 0f;
            }
            headerWrapper.DoNotAcceptEvents = false;
            headerWrapper.DoNotPassEventsToChildren = true;

            // Inner horizontal bar (ListPanel) — arrow + title + line
            Widget headerBar = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel");
            if (headerBar == null)
                headerBar = new Widget(uiContext);
            headerBar.Id = "EditableEncyclopediaJournalPlacement";

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
            // In narrow sidebar, ensure StretchToParent on headerBar too
            if (_narrowSidebarMode)
            {
                headerBar.WidthSizePolicy = SizePolicy.StretchToParent;
            }

            // Collapse indicator arrow
            BrushWidget arrow = null;
            if (native.NativeIndicator != null)
            {
                arrow = new BrushWidget(uiContext);
                arrow.Id = "EditableEncyclopediaJournalIndicator";
                LoreSectionInjector.CopyLayoutProperties(native.NativeIndicator, arrow);
                if (native.IndicatorBrush != null)
                    arrow.Brush = native.IndicatorBrush;
                LoreSectionInjector.ForceWidgetBrushState(arrow, native.NativeIndicator);
                headerBar.AddChild(arrow);
            }

            // Title text "Journal"
            var titleText = new TextWidget(uiContext);
            titleText.Id = "EditableEncyclopediaJournalTitle";
            titleText.Text = headerText;
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
                if (journalEntryBrush != null)
                {
                    var hBrush = journalEntryBrush.Clone();
                    SetBrushColor(hBrush, 0.85f, 0.72f, 0.35f, 1f);
                    titleText.Brush = hBrush;
                }
            }
            // In narrow sidebar mode, scale down the title text significantly
            if (_narrowSidebarMode && titleText.Brush != null)
            {
                ScaleBrushFontSize(titleText.Brush, NarrowTitleFontScale);
            }
            headerBar.AddChild(titleText);

            // Horizontal separator line in header bar
            if (native.NativeLine != null)
            {
                Widget hLine = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ImageWidget");
                if (hLine == null)
                    hLine = new BrushWidget(uiContext);
                hLine.Id = "EditableEncyclopediaJournalHeaderLine";
                LoreSectionInjector.CopyLayoutProperties(native.NativeLine, hLine);
                if (native.LineBrush != null && hLine is BrushWidget bwHLine)
                    bwHLine.Brush = native.LineBrush;
                // In narrow sidebar, prevent the line from overflowing
                if (_narrowSidebarMode)
                    hLine.WidthSizePolicy = SizePolicy.StretchToParent;
                headerBar.AddChild(hLine);
            }

            // Clip headerBar to prevent divider line overflow
            headerBar.ClipContents = true;
            headerWrapper.AddChild(headerBar);

            // === 2. Create the content container ===
            float contentMarginLeft = ContentMarginLeftDefault;
            if (native.NativeIndicator != null)
                contentMarginLeft = native.NativeIndicator.SuggestedWidth
                    + native.NativeIndicator.MarginLeft + native.NativeIndicator.MarginRight + ContentMarginLeftExtraPx;
            if (_narrowSidebarMode)
                contentMarginLeft = Math.Min(contentMarginLeft, NarrowMaxContentMarginLeft);

            Widget contentContainer = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel");
            if (contentContainer == null)
                contentContainer = new Widget(uiContext);
            contentContainer.Id = "EditableEncyclopediaJournalContent";
            contentContainer.WidthSizePolicy = SizePolicy.StretchToParent;
            contentContainer.HeightSizePolicy = SizePolicy.CoverChildren;
            contentContainer.MarginLeft = contentMarginLeft;
            contentContainer.MarginRight = ContentMarginRight;
            contentContainer.MarginBottom = ContentMarginBottom;
            contentContainer.ClipContents = true;
            LoreSectionInjector.SetVerticalLayoutTopToBottom(contentContainer);

            // 2a. Golden divider line inside content container (visual separator)
            Widget lineWidget = null;
            {
                float lineHeight = (nativeLineWidget != null && nativeLineWidget.SuggestedHeight > 0)
                    ? nativeLineWidget.SuggestedHeight : DividerLineHeightFallback;

                var line = new Widget(uiContext);
                line.Id = "EditableEncyclopediaJournalLine";
                line.WidthSizePolicy = SizePolicy.StretchToParent;
                line.HeightSizePolicy = SizePolicy.Fixed;
                line.SuggestedHeight = lineHeight;

                bool spriteSet = false;
                if (nativeLineWidget != null)
                    spriteSet = TryCopyWidgetSprite(nativeLineWidget, line);
                if (!spriteSet)
                {
                    string[] spriteNames = { "horizontal_gradient_divider", "horizontal_divider", "divider_horizontal" };
                    foreach (string spriteName in spriteNames)
                    {
                        spriteSet = TrySetSpriteByName(uiContext, line, spriteName);
                        if (spriteSet) break;
                    }
                }
                if (spriteSet)
                {
                    if (nativeLineWidget != null)
                        TryCopyWidgetColor(nativeLineWidget, line);
                    TrySetHorizontalFlip(line, true);
                    lineWidget = line;
                }
                else if (dividerLineBrush != null)
                {
                    var bwLine = new BrushWidget(uiContext);
                    bwLine.Id = "EditableEncyclopediaJournalLine";
                    bwLine.WidthSizePolicy = SizePolicy.StretchToParent;
                    bwLine.HeightSizePolicy = SizePolicy.Fixed;
                    bwLine.SuggestedHeight = lineHeight;
                    bwLine.Brush = dividerLineBrush.Clone();
                    lineWidget = bwLine;
                }
            }
            if (lineWidget != null)
                contentContainer.AddChild(lineWidget);

            // 2c. Category filter toggles
            // Only show filters if there are entries with category tags
            bool hasCategories = entries.Any(e =>
                e.Text.StartsWith("[War]") || e.Text.StartsWith("[Politics]")
                || e.Text.StartsWith("[Crime]") || e.Text.StartsWith("[Family]"));

            if (hasCategories)
            {
                Widget filterRow = new ListPanel(uiContext);
                filterRow.Id = "EditableEncyclopediaJournalFilters";
                filterRow.WidthSizePolicy = SizePolicy.StretchToParent;
                filterRow.HeightSizePolicy = SizePolicy.CoverChildren;
                filterRow.MarginTop = 6f;
                filterRow.MarginBottom = 8f;
                filterRow.ClipContents = true;
                SetHorizontalLayout(filterRow);

                string objectIdForRefresh = _currentObjectId;
                bool allActive = _filterWar && _filterPolitics && _filterCrime && _filterFamily && _filterOther;
                AddFilterToggle(uiContext, filterRow, journalEntryBrush, "All", allActive,
                    new Color(0.9f, 0.85f, 0.6f, 1f), () => {
                        _filterWar = true; _filterPolitics = true; _filterCrime = true;
                        _filterFamily = true; _filterOther = true;
                        _currentPage = 0; ScheduleRefresh(objectIdForRefresh);
                    });
                AddFilterToggle(uiContext, filterRow, journalEntryBrush, "War", _filterWar,
                    new Color(1f, 0.13f, 0.13f, 1f), () => { _filterWar = !_filterWar; _currentPage = 0; ScheduleRefresh(objectIdForRefresh); });
                AddFilterToggle(uiContext, filterRow, journalEntryBrush, "Politics", _filterPolitics,
                    new Color(0.33f, 0.6f, 1f, 1f), () => { _filterPolitics = !_filterPolitics; _currentPage = 0; ScheduleRefresh(objectIdForRefresh); });
                AddFilterToggle(uiContext, filterRow, journalEntryBrush, "Crime", _filterCrime,
                    new Color(1f, 0.6f, 0.2f, 1f), () => { _filterCrime = !_filterCrime; _currentPage = 0; ScheduleRefresh(objectIdForRefresh); });
                AddFilterToggle(uiContext, filterRow, journalEntryBrush, "Family", _filterFamily,
                    new Color(0.4f, 0.8f, 0.4f, 1f), () => { _filterFamily = !_filterFamily; _currentPage = 0; ScheduleRefresh(objectIdForRefresh); });
                AddFilterToggle(uiContext, filterRow, journalEntryBrush, "Other", _filterOther,
                    new Color(0.7f, 0.7f, 0.7f, 1f), () => { _filterOther = !_filterOther; _currentPage = 0; ScheduleRefresh(objectIdForRefresh); });

                contentContainer.AddChild(filterRow);
            }

            // 2d. Filter entries by active categories (renumbered)
            var filteredEntries = new List<JournalEntry>();
            foreach (var entry in entries)
            {
                string cat = GetEntryCategory(entry.Text);
                if (cat == "War" && !_filterWar) continue;
                if (cat == "Politics" && !_filterPolitics) continue;
                if (cat == "Crime" && !_filterCrime) continue;
                if (cat == "Family" && !_filterFamily) continue;
                if (cat == null && !_filterOther) continue;
                filteredEntries.Add(entry);
            }

            // 2e. Pagination
            int totalFiltered = filteredEntries.Count;
            int totalPages = Math.Max(1, (totalFiltered + EntriesPerPage - 1) / EntriesPerPage);
            if (_currentPage >= totalPages) _currentPage = totalPages - 1;
            if (_currentPage < 0) _currentPage = 0;

            int startIdx = _currentPage * EntriesPerPage;
            int endIdx = Math.Min(startIdx + EntriesPerPage, totalFiltered);

            // 2f. Render entries for current page
            for (int ei = startIdx; ei < endIdx; ei++)
            {
                var entry = filteredEntries[ei];
                // Find original index in the unfiltered entries list (for edit/delete)
                int originalIndex = entries.IndexOf(entry);

                string tagText = null;
                string bodyText = entry.Text;
                // Strip internal [slain:N] metadata tags
                int slainIdx = bodyText.IndexOf(" [slain:");
                if (slainIdx >= 0 && slainIdx + 8 < bodyText.Length)
                {
                    int closeIdx = bodyText.IndexOf(']', slainIdx + 8);
                    if (closeIdx >= 0)
                        bodyText = bodyText.Remove(slainIdx, closeIdx - slainIdx + 1);
                }
                Color? tagColor = null;
                if (bodyText.StartsWith("[War]")) { tagText = "[War]"; tagColor = new Color(1f, 0.13f, 0.13f, 1f); bodyText = bodyText.Substring(5).TrimStart(); }
                else if (bodyText.StartsWith("[Politics]")) { tagText = "[Politics]"; tagColor = new Color(0.33f, 0.6f, 1f, 1f); bodyText = bodyText.Substring(10).TrimStart(); }
                else if (bodyText.StartsWith("[Crime]")) { tagText = "[Crime]"; tagColor = new Color(1f, 0.6f, 0.2f, 1f); bodyText = bodyText.Substring(7).TrimStart(); }
                else if (bodyText.StartsWith("[Family]")) { tagText = "[Family]"; tagColor = new Color(0.4f, 0.8f, 0.4f, 1f); bodyText = bodyText.Substring(8).TrimStart(); }

                // --- Entry separator (thin line between entries, not before first) ---
                if (ei > startIdx)
                {
                    Widget separator = new Widget(uiContext);
                    separator.Id = "EditableEncyclopediaJournalSep_" + ei;
                    separator.WidthSizePolicy = SizePolicy.StretchToParent;
                    separator.HeightSizePolicy = SizePolicy.Fixed;
                    separator.SuggestedHeight = SeparatorHeight;
                    separator.MarginTop = 2f;
                    separator.MarginBottom = 2f;
                    separator.MarginLeft = 4f;
                    separator.MarginRight = 4f;
                    SetWidgetColor(separator, 0.45f, 0.40f, 0.30f, 0.35f);
                    contentContainer.AddChild(separator);
                }

                // --- Entry container: vertical layout holding entry content ---
                Widget entryContainer = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel");
                if (entryContainer == null) entryContainer = new Widget(uiContext);
                entryContainer.Id = "EditableEncyclopediaJournalEntry_" + ei;
                entryContainer.WidthSizePolicy = SizePolicy.StretchToParent;
                entryContainer.HeightSizePolicy = SizePolicy.CoverChildren;
                entryContainer.MarginTop = 6f;
                entryContainer.MarginBottom = 6f;
                entryContainer.MarginLeft = 4f;
                entryContainer.MarginRight = 4f;
                entryContainer.ClipContents = true;
                LoreSectionInjector.SetVerticalLayoutTopToBottom(entryContainer);

                if (_narrowSidebarMode)
                {
                    // === NARROW SIDEBAR: separate widgets with manual word-wrap ===
                    float textW = _sidebarWidth - NarrowTextPadding;
                    if (textW < MinTextWidth) textW = MinTextWidth;
                    int charsPerLine = (int)(textW / AvgCharWidthPx);
                    if (charsPerLine < MinCharsPerLine) charsPerLine = MinCharsPerLine;

                    // --- Date widget first (above body, subtle) ---
                    if (!string.IsNullOrEmpty(entry.Date))
                    {
                        string displayDate = SimplifyChronicleDate(entry.Date);
                        // Combine tag + date on one line if tag exists
                        string dateDisplay = tagText != null ? (tagText + "  " + displayDate) : displayDate;

                        Widget dateRow = new ListPanel(uiContext);
                        dateRow.WidthSizePolicy = SizePolicy.StretchToParent;
                        dateRow.HeightSizePolicy = SizePolicy.CoverChildren;
                        dateRow.MarginBottom = 3f;
                        SetHorizontalLayout(dateRow);

                        if (tagText != null && tagColor.HasValue && journalEntryBrush != null)
                        {
                            var tagLabel = new TextWidget(uiContext);
                            tagLabel.Text = tagText;
                            tagLabel.WidthSizePolicy = SizePolicy.CoverChildren;
                            tagLabel.HeightSizePolicy = SizePolicy.CoverChildren;
                            var tagBrush = journalEntryBrush.Clone();
                            ScaleBrushFontSize(tagBrush, 0.75f);
                            SetBrushColor(tagBrush, tagColor.Value.Red, tagColor.Value.Green, tagColor.Value.Blue, 0.9f);
                            tagLabel.Brush = tagBrush;
                            tagLabel.MarginRight = 6f;
                            dateRow.AddChild(tagLabel);
                        }

                        var dateLbl = new TextWidget(uiContext);
                        dateLbl.Text = displayDate;
                        dateLbl.WidthSizePolicy = SizePolicy.CoverChildren;
                        dateLbl.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (journalEntryBrush != null)
                        {
                            var dateBrush = journalEntryBrush.Clone();
                            ScaleBrushFontSize(dateBrush, 0.70f);
                            SetBrushColor(dateBrush, 0.60f, 0.55f, 0.45f, 0.8f);
                            dateLbl.Brush = dateBrush;
                        }
                        dateRow.AddChild(dateLbl);

                        entryContainer.AddChild(dateRow);
                    }
                    else if (tagText != null && tagColor.HasValue && journalEntryBrush != null)
                    {
                        // Tag only (no date)
                        var tagLabel = new TextWidget(uiContext);
                        tagLabel.Text = tagText;
                        tagLabel.WidthSizePolicy = SizePolicy.CoverChildren;
                        tagLabel.HeightSizePolicy = SizePolicy.CoverChildren;
                        tagLabel.MarginBottom = 3f;
                        var tagBrush = journalEntryBrush.Clone();
                        ScaleBrushFontSize(tagBrush, 0.75f);
                        SetBrushColor(tagBrush, tagColor.Value.Red, tagColor.Value.Green, tagColor.Value.Blue, 0.9f);
                        tagLabel.Brush = tagBrush;
                        entryContainer.AddChild(tagLabel);
                    }

                    // --- Body widget (word-wrapped) ---
                    string plainBody = StripNameMarkers(bodyText);
                    string wrappedBody = WordWrap(plainBody, charsPerLine);
                    MCMSettings.DebugLog("JournalSection: sidebar entry charsPerLine=" + charsPerLine
                        + " textW=" + textW + " wrapped='" + wrappedBody.Replace("\n", "\\n") + "'");

                    Widget bodyWidget;
                    if (HasNameMarkers(bodyText))
                    {
                        bodyWidget = CreateColoredBodyWidget(uiContext, journalEntryBrush, bodyText, textW, charsPerLine);
                    }
                    else
                    {
                        bodyWidget = TryCreateRichTextWidget(uiContext, wrappedBody);
                        if (bodyWidget == null) { var tw = new TextWidget(uiContext); tw.Text = wrappedBody; bodyWidget = tw; }
                        bodyWidget.WidthSizePolicy = SizePolicy.Fixed;
                        bodyWidget.SuggestedWidth = textW;
                        bodyWidget.MaxWidth = textW;
                        bodyWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (journalEntryBrush != null)
                        {
                            var bbp = bodyWidget.GetType().GetProperty("Brush", AllFlags);
                            if (bbp != null && bbp.CanWrite) bbp.SetValue(bodyWidget, journalEntryBrush.Clone());
                        }
                    }
                    bodyWidget.MarginTop = 1f;
                    if (bodyWidget is BrushWidget bwBody) LoreSectionInjector.ForceTextAlignLeft(bwBody);
                    entryContainer.AddChild(bodyWidget);

                    // --- Edit/Delete buttons row ---
                    if (originalIndex >= 0)
                    {
                        Widget actionRow = new ListPanel(uiContext);
                        actionRow.WidthSizePolicy = SizePolicy.StretchToParent;
                        actionRow.HeightSizePolicy = SizePolicy.CoverChildren;
                        actionRow.MarginTop = 3f;
                        SetHorizontalLayout(actionRow);

                        string editObjectId = _currentObjectId;
                        int editIdx = originalIndex;
                        string editCurrentText = entry.Text;

                        Widget editBtn = CreateClickableLabel(uiContext, journalEntryBrush,
                            "[Edit]", new Color(0.65f, 0.58f, 0.40f, 0.8f), () =>
                            {
                                EditJournalEntry(editObjectId, editIdx, editCurrentText);
                            }, 0.65f);
                        editBtn.MarginRight = 8f;
                        actionRow.AddChild(editBtn);

                        Widget deleteBtn = CreateClickableLabel(uiContext, journalEntryBrush,
                            "[X]", new Color(0.8f, 0.3f, 0.3f, 0.7f), () =>
                            {
                                DeleteJournalEntry(editObjectId, editIdx);
                            }, 0.65f);
                        actionRow.AddChild(deleteBtn);

                        entryContainer.AddChild(actionRow);
                    }
                }
                else
                {
                    // === STANDARD LAYOUT (wide panel) ===
                    // --- Header row: [Category Tag] .............. [Date] ---
                    Widget headerRow = new ListPanel(uiContext);
                    headerRow.WidthSizePolicy = SizePolicy.StretchToParent;
                    headerRow.HeightSizePolicy = SizePolicy.CoverChildren;
                    headerRow.ClipContents = true;
                    SetHorizontalLayout(headerRow);

                    if (tagText != null && tagColor.HasValue && journalEntryBrush != null)
                    {
                        Widget tagWidget = TryCreateRichTextWidget(uiContext, tagText);
                        if (tagWidget == null) { var tw = new TextWidget(uiContext); tw.Text = tagText; tagWidget = tw; }
                        tagWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                        tagWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                        var tagBrush = journalEntryBrush.Clone();
                        ScaleBrushFontSize(tagBrush, 0.88f);
                        SetBrushColor(tagBrush, tagColor.Value.Red, tagColor.Value.Green, tagColor.Value.Blue, tagColor.Value.Alpha);
                        var tbp = tagWidget.GetType().GetProperty("Brush", AllFlags);
                        if (tbp != null && tbp.CanWrite) tbp.SetValue(tagWidget, tagBrush);
                        headerRow.AddChild(tagWidget);
                    }

                    // Spacer to push date to the right
                    Widget spacer = new Widget(uiContext);
                    spacer.WidthSizePolicy = SizePolicy.StretchToParent;
                    spacer.HeightSizePolicy = SizePolicy.Fixed;
                    spacer.SuggestedHeight = 1f;
                    headerRow.AddChild(spacer);

                    // Date (right-aligned on same row as tag)
                    if (!string.IsNullOrEmpty(entry.Date))
                    {
                        string displayDate = SimplifyChronicleDate(entry.Date);
                        Widget dateWidget = TryCreateRichTextWidget(uiContext, displayDate);
                        if (dateWidget == null) { var tw = new TextWidget(uiContext); tw.Text = displayDate; dateWidget = tw; }
                        dateWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                        dateWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (journalEntryBrush != null)
                        {
                            var dateBrush = journalEntryBrush.Clone();
                            ScaleBrushFontSize(dateBrush, 0.80f);
                            SetBrushColor(dateBrush, 0.60f, 0.55f, 0.45f, 0.8f);
                            var brushProp = dateWidget.GetType().GetProperty("Brush", AllFlags);
                            if (brushProp != null && brushProp.CanWrite)
                                brushProp.SetValue(dateWidget, dateBrush);
                        }
                        dateWidget.HorizontalAlignment = HorizontalAlignment.Right;
                        headerRow.AddChild(dateWidget);
                    }

                    // Edit/Delete buttons (right side of header row)
                    if (originalIndex >= 0)
                    {
                        string editObjectId = _currentObjectId;
                        int editIdx = originalIndex;
                        string editCurrentText = entry.Text;

                        Widget editBtn = CreateClickableLabel(uiContext, journalEntryBrush,
                            "[Edit]", new Color(0.65f, 0.58f, 0.40f, 0.8f), () =>
                            {
                                EditJournalEntry(editObjectId, editIdx, editCurrentText);
                            }, 0.70f);
                        editBtn.MarginLeft = 8f;
                        headerRow.AddChild(editBtn);

                        Widget deleteBtn = CreateClickableLabel(uiContext, journalEntryBrush,
                            "[X]", new Color(0.8f, 0.3f, 0.3f, 0.7f), () =>
                            {
                                DeleteJournalEntry(editObjectId, editIdx);
                            }, 0.70f);
                        deleteBtn.MarginLeft = 4f;
                        headerRow.AddChild(deleteBtn);
                    }

                    entryContainer.AddChild(headerRow);

                    // --- Body text ---
                    Widget bodyWidget;
                    if (HasNameMarkers(bodyText))
                    {
                        bodyWidget = CreateColoredBodyWidget(uiContext, journalEntryBrush, bodyText);
                    }
                    else
                    {
                        string plainBody = StripNameMarkers(bodyText);
                        bodyWidget = TryCreateRichTextWidget(uiContext, plainBody);
                        if (bodyWidget == null) { var tw = new TextWidget(uiContext); tw.Text = plainBody; bodyWidget = tw; }
                        bodyWidget.WidthSizePolicy = SizePolicy.StretchToParent;
                        bodyWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (journalEntryBrush != null)
                        {
                            var bbp = bodyWidget.GetType().GetProperty("Brush", AllFlags);
                            if (bbp != null && bbp.CanWrite) bbp.SetValue(bodyWidget, journalEntryBrush.Clone());
                        }
                    }
                    bodyWidget.MarginTop = 2f;
                    bodyWidget.MarginLeft = 2f;
                    if (bodyWidget is BrushWidget bwBody2)
                        LoreSectionInjector.ForceTextAlignLeft(bwBody2);
                    entryContainer.AddChild(bodyWidget);
                }

                contentContainer.AddChild(entryContainer);
            }

            // 2g. Pagination controls (only show if more than one page)
            if (totalPages > 1)
            {
                string objectIdForPaging = _currentObjectId;

                Widget pageRow = new ListPanel(uiContext);
                pageRow.Id = "EditableEncyclopediaJournalPaging";
                pageRow.WidthSizePolicy = SizePolicy.StretchToParent;
                pageRow.HeightSizePolicy = SizePolicy.CoverChildren;
                pageRow.MarginTop = 6f;
                SetHorizontalLayout(pageRow);

                // "< Prev" button
                if (_currentPage > 0)
                {
                    Widget prevBtn = CreateClickableLabel(uiContext, journalEntryBrush,
                        "< Prev", new Color(0.8f, 0.75f, 0.5f, 1f), () =>
                        {
                            _currentPage--;
                            ScheduleRefresh(objectIdForPaging);
                        });
                    prevBtn.MarginRight = 10f;
                    pageRow.AddChild(prevBtn);
                }

                // "Page X / Y" label
                Widget pageLabel = TryCreateRichTextWidget(uiContext, "Page " + (_currentPage + 1) + " / " + totalPages);
                if (pageLabel == null)
                {
                    var tw = new TextWidget(uiContext);
                    tw.Text = Localization.L("ui_page_of", _currentPage + 1, totalPages);
                    pageLabel = tw;
                }
                pageLabel.WidthSizePolicy = SizePolicy.CoverChildren;
                pageLabel.HeightSizePolicy = SizePolicy.CoverChildren;
                if (journalEntryBrush != null)
                {
                    var pageBrush = journalEntryBrush.Clone();
                    SetBrushColor(pageBrush, 0.55f, 0.55f, 0.55f, 1f);
                    var brushProp = pageLabel.GetType().GetProperty("Brush", AllFlags);
                    if (brushProp != null && brushProp.CanWrite)
                        brushProp.SetValue(pageLabel, pageBrush);
                }
                pageLabel.MarginRight = 10f;
                pageRow.AddChild(pageLabel);

                // "Next >" button
                if (_currentPage < totalPages - 1)
                {
                    Widget nextBtn = CreateClickableLabel(uiContext, journalEntryBrush,
                        "Next >", new Color(0.8f, 0.75f, 0.5f, 1f), () =>
                        {
                            _currentPage++;
                            ScheduleRefresh(objectIdForPaging);
                        });
                    pageRow.AddChild(nextBtn);
                }

                contentContainer.AddChild(pageRow);
            }

            // 2h. "Add Note" button
            {
                string addNoteObjectId = _currentObjectId;

                Widget addNoteRow = new ListPanel(uiContext);
                addNoteRow.Id = "EditableEncyclopediaJournalAddNote";
                addNoteRow.WidthSizePolicy = SizePolicy.StretchToParent;
                addNoteRow.HeightSizePolicy = SizePolicy.CoverChildren;
                addNoteRow.MarginTop = entries.Count > 0 ? 8f : 4f;
                addNoteRow.MarginBottom = 4f;
                SetHorizontalLayout(addNoteRow);

                Widget addBtn = CreateClickableLabel(uiContext, journalEntryBrush,
                    "+ Add Note", new Color(0.75f, 0.68f, 0.40f, 0.9f), () =>
                    {
                        AddJournalNote(addNoteObjectId);
                    }, _narrowSidebarMode ? 0.65f : 0.75f);
                addNoteRow.AddChild(addBtn);

                contentContainer.AddChild(addNoteRow);
            }

            // === 3. Wire up collapse toggle and insert into parent ===
            LoreSectionInjector.HookCollapseToggle(headerWrapper, contentContainer, arrow, native.ReferenceHeader);

            // Path 2 defensive validation: bail before inserting if the current
            // page is a Settlement page whose layout has been restructured by
            // a known conflicting mod (Realm of Thrones, etc.). Bug 2026-05-25.
            if (!EncyclopediaAnchorHelper.IsSafeToInjectOnCurrentPage(parent, "Journal"))
                return;

            parent.AddChild(headerWrapper);
            _injectedWidgets.Add(headerWrapper);
            parent.AddChild(contentContainer);
            _injectedWidgets.Add(contentContainer);

            MCMSettings.DebugLog("JournalSection: added collapsible header + " + filteredEntries.Count
                + " filtered entries (page " + (_currentPage + 1) + "/" + totalPages + ") to parent");
        }

        /// <summary>
        /// Returns the category tag name for a journal entry text, or null if no category.
        /// </summary>
        private static string GetEntryCategory(string text)
        {
            if (text.StartsWith("[War]")) return "War";
            if (text.StartsWith("[Politics]")) return "Politics";
            if (text.StartsWith("[Crime]")) return "Crime";
            if (text.StartsWith("[Family]")) return "Family";
            return null;
        }

        private static string TruncateString(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
            if (maxLen <= 2) return "..";
            return s.Substring(0, maxLen - 2) + "..";
        }

        /// <summary>
        /// Manually word-wraps text by inserting \n at word boundaries.
        /// Gauntlet's RichTextWidget does not auto-wrap programmatic widgets,
        /// so we compute line breaks ourselves.
        /// charsPerLine is estimated from (widthPx / avgCharWidthPx).
        /// </summary>
        public static string WordWrap(string text, int charsPerLine)
        {
            if (string.IsNullOrEmpty(text) || charsPerLine <= 0) return text;
            var result = new System.Text.StringBuilder();
            int col = 0;
            string[] words = text.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                string word = words[i];
                if (col > 0 && col + 1 + word.Length > charsPerLine)
                {
                    result.Append('\n');
                    col = 0;
                }
                if (col > 0) { result.Append(' '); col++; }
                result.Append(word);
                col += word.Length;
            }
            return result.ToString();
        }

        /// <summary>
        /// Simplifies "Day X of Season, Year" to "Season Year" for cleaner display.
        /// </summary>
        private static string SimplifyChronicleDate(string date)
        {
            if (string.IsNullOrEmpty(date)) return date;
            int ofIndex = date.IndexOf(" of ");
            if (ofIndex >= 0 && ofIndex + 4 <= date.Length)
            {
                string seasonYear = date.Substring(ofIndex + 4);
                return seasonYear.Replace(", ", " ");
            }
            return date;
        }

        /// <summary>
        /// Adds a category filter toggle button to the filter row.
        /// Active filters show with a subtle colored background; inactive filters show dimmed text only.
        /// </summary>
        private static void AddFilterToggle(UIContext uiContext, Widget filterRow,
            Brush baseBrush, string categoryName, bool isActive, Color activeColor, Action onClick)
        {
            string displayName = categoryName;
            float alpha = isActive ? 1f : 0.35f;
            Color displayColor = new Color(activeColor.Red, activeColor.Green, activeColor.Blue, alpha);

            Widget btn = CreateClickableLabel(uiContext, baseBrush, displayName, displayColor, onClick, 0.72f);
            btn.MarginRight = 4f;

            // Add subtle background tint for active filters to make them look like proper toggle buttons
            if (isActive)
            {
                try
                {
                    var colorProp = btn.GetType().GetProperty("Color", AllFlags);
                    if (colorProp != null && colorProp.CanWrite)
                        colorProp.SetValue(btn, new Color(activeColor.Red * 0.15f, activeColor.Green * 0.15f, activeColor.Blue * 0.15f, 0.6f));
                }
                catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: filter toggle color set failed: " + ex.ToString()); }
            }

            // Add padding around text for button-like feel
            btn.MarginTop = 1f;
            btn.MarginBottom = 1f;
            var firstChild = btn.ChildCount > 0 ? btn.GetChild(0) : null;
            if (firstChild != null)
            {
                firstChild.MarginLeft = 4f;
                firstChild.MarginRight = 4f;
            }
            filterRow.AddChild(btn);
        }

        /// <summary>
        /// Creates a clickable text label (ButtonWidget wrapping a TextWidget) that fires an action on click.
        /// </summary>
        private static Widget CreateClickableLabel(UIContext uiContext, Brush baseBrush,
            string text, Color color, Action onClick, float fontScale = 0.85f)
        {
            Widget btn = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget");
            if (btn == null)
                btn = new Widget(uiContext);

            btn.WidthSizePolicy = SizePolicy.CoverChildren;
            btn.HeightSizePolicy = SizePolicy.CoverChildren;
            btn.DoNotAcceptEvents = false;
            btn.DoNotPassEventsToChildren = true;

            var label = new TextWidget(uiContext);
            label.Text = text;
            label.WidthSizePolicy = SizePolicy.CoverChildren;
            label.HeightSizePolicy = SizePolicy.CoverChildren;
            if (baseBrush != null)
            {
                var brush = baseBrush.Clone();
                SetBrushColor(brush, color.Red, color.Green, color.Blue, color.Alpha);
                ScaleBrushFontSize(brush, fontScale);
                label.Brush = brush;
            }
            btn.AddChild(label);

            // Hook EventFire for click
            try
            {
                var eventFireEvent = btn.GetType().GetEvent("EventFire",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (eventFireEvent != null)
                {
                    Action<Widget, string, object[]> handler = (Widget sender, string eventName, object[] args) =>
                    {
                        if (eventName == "Click")
                            onClick?.Invoke();
                    };
                    eventFireEvent.AddEventHandler(btn, handler);
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: CreateClickableLabel EventFire hook error: " + ex.ToString());
            }

            return btn;
        }

        /// <summary>
        /// Sets RGBA color on a widget via reflection (Color property).
        /// </summary>
        private static void SetWidgetColor(Widget widget, float r, float g, float b, float a)
        {
            try
            {
                var colorProp = widget.GetType().GetProperty("Color", AllFlags);
                if (colorProp != null && colorProp.CanWrite)
                    colorProp.SetValue(widget, new Color(r, g, b, a));
            }
            catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: SetWidgetColor failed: " + ex.ToString()); }
        }

        /// <summary>
        /// Opens the edit popup for a journal entry.
        /// </summary>
        private static void EditJournalEntry(string objectId, int journalIndex, string currentText)
        {
            try
            {
                if (EncyclopediaEditBehavior.Instance == null) return;

                EncyclopediaInputBlocker.Block();

                EditPopupInjector.ScheduleShow(
                    "Edit Journal Entry",
                    currentText,
                    EditPopupMaxLength,
                    onConfirm: (string newText) =>
                    {
                        EncyclopediaInputBlocker.Unblock();
                        if (string.IsNullOrWhiteSpace(newText)) return;

                        EncyclopediaEditBehavior.Instance.ReplaceJournalEntry(objectId, journalIndex, newText);
                        MCMSettings.DebugLog("JournalSection: edited journal entry " + journalIndex + " for " + objectId);

                        ScheduleRefresh(objectId);
                    },
                    onCancel: () =>
                    {
                        EncyclopediaInputBlocker.Unblock();
                    }
                );
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: EditJournalEntry error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Deletes a journal entry after confirmation.
        /// </summary>
        private static void DeleteJournalEntry(string objectId, int journalIndex)
        {
            try
            {
                if (EncyclopediaEditBehavior.Instance == null) return;

                var inquiryData = new InquiryData(
                    Localization.L("journal_delete_title"),
                    Localization.L("journal_delete_desc"),
                    true, true,
                    Localization.L("ui_delete"), Localization.L("edit_cancel"),
                    delegate
                    {
                        EncyclopediaEditBehavior.Instance.RemoveJournalEntry(objectId, journalIndex);
                        MCMSettings.DebugLog("JournalSection: deleted journal entry " + journalIndex + " for " + objectId);
                        ScheduleRefresh(objectId);
                    },
                    delegate { });

                InformationManager.ShowInquiry(inquiryData, false, false);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: DeleteJournalEntry error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Opens a popup to add a new manual journal note.
        /// </summary>
        private static void AddJournalNote(string objectId)
        {
            try
            {
                if (EncyclopediaEditBehavior.Instance == null) return;

                EncyclopediaInputBlocker.Block();

                EditPopupInjector.ScheduleShow(
                    "Add Journal Note",
                    "",
                    EditPopupMaxLength,
                    onConfirm: (string noteText) =>
                    {
                        EncyclopediaInputBlocker.Unblock();
                        if (string.IsNullOrWhiteSpace(noteText)) return;

                        EncyclopediaEditBehavior.Instance.AddJournalEntry(objectId, noteText);
                        MCMSettings.DebugLog("JournalSection: added note for " + objectId);
                        ScheduleRefresh(objectId);
                    },
                    onCancel: () =>
                    {
                        EncyclopediaInputBlocker.Unblock();
                    }
                );
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: AddJournalNote error: " + ex.ToString());
            }
        }

        private static void ScaleBrushFontSize(Brush brush, float multiplier)
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
                        fontSizeProp.SetValue(brush, (int)(current * multiplier));
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
                                fsProp.SetValue(layer, (int)(intVal * multiplier));
                            else if (val is float floatVal && floatVal > 0)
                                fsProp.SetValue(layer, floatVal * multiplier);
                        }
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: ScaleBrushFontSize failed: " + ex.ToString()); }
        }

        // Category tag colors are applied via separate colored TextWidgets in the entry builder above.
        // RichTextWidget inline <span color> tags don't work with most native brushes.
        private static string ColorizeCategoryTags_Unused(string text)
        {
            return text;
        }

        // Entity name colors
        private static readonly Color HeroNameColor = new Color(1f, 0.85f, 0.4f, 1f);       // golden/warm
        private static readonly Color SettlementNameColor = new Color(0.4f, 0.9f, 0.85f, 1f); // cyan/teal
        private static readonly Color KingdomNameColor = new Color(0.85f, 0.55f, 1f, 1f);     // purple/violet
        private static readonly Color ClanNameColor = new Color(0.6f, 0.85f, 0.4f, 1f);       // green/lime

        /// <summary>
        /// Represents a text segment with optional coloring for named entities.
        /// </summary>
        public struct TextSegment
        {
            public string Text;
            public Color? Color;       // null = use default brush color
            public string EntityType;  // "Hero", "Settlement", "Kingdom", "Clan", or null
            public string EntityId;    // encyclopedia entity ID (e.g. "lord_1_1", "town_ES3"), null if unknown

            // Convenience properties for backwards compat
            public bool IsHero => EntityType == "Hero";
        }

        /// <summary>
        /// Parses text containing entity markers into colored segments with optional IDs for linking.
        /// Supported: «h:id»...«/h» (hero), «s:id»...«/s» (settlement),
        ///            «k:id»...«/k» (kingdom), «c:id»...«/c» (clan).
        /// </summary>
        public static List<TextSegment> ParseNameMarkers(string text)
        {
            var segments = new List<TextSegment>();
            if (string.IsNullOrEmpty(text)) return segments;

            // Marker definitions: open prefix, close tag, entity type, color
            var markerDefs = new[]
            {
                new { Prefix = "«h", Close = "«/h»", Type = "Hero", Col = HeroNameColor },
                new { Prefix = "«s", Close = "«/s»", Type = "Settlement", Col = SettlementNameColor },
                new { Prefix = "«k", Close = "«/k»", Type = "Kingdom", Col = KingdomNameColor },
                new { Prefix = "«c", Close = "«/c»", Type = "Clan", Col = ClanNameColor },
            };

            int i = 0;
            while (i < text.Length)
            {
                // Find the nearest marker of any type
                int bestPos = -1;
                int bestIdx = -1;
                for (int m = 0; m < markerDefs.Length; m++)
                {
                    int pos = text.IndexOf(markerDefs[m].Prefix, i, StringComparison.Ordinal);
                    if (pos < 0) continue;
                    // Disambiguate: «s vs «span — only accept «X» or «X:
                    if (pos + 2 < text.Length)
                    {
                        char next = text[pos + 2];
                        if (next != '»' && next != ':') continue;
                    }
                    if (bestPos < 0 || pos < bestPos)
                    {
                        bestPos = pos;
                        bestIdx = m;
                    }
                }

                if (bestPos < 0)
                {
                    if (i < text.Length)
                        segments.Add(new TextSegment { Text = text.Substring(i), Color = null });
                    break;
                }

                // Add plain text before the marker
                if (bestPos > i)
                    segments.Add(new TextSegment { Text = text.Substring(i, bestPos - i), Color = null });

                var def = markerDefs[bestIdx];
                string closeTag = def.Close;
                int tagContentStart = bestPos + 2;
                if (tagContentStart >= text.Length)
                {
                    segments.Add(new TextSegment { Text = text.Substring(bestPos), Color = null });
                    break;
                }
                int closeBracket = text.IndexOf('»', tagContentStart);
                if (closeBracket < 0)
                {
                    segments.Add(new TextSegment { Text = text.Substring(bestPos), Color = null });
                    break;
                }

                // Extract entity ID if present (between ':' and '»')
                string entityId = null;
                string tagContent = text.Substring(tagContentStart, closeBracket - tagContentStart);
                if (tagContent.StartsWith(":") && tagContent.Length > 1)
                    entityId = tagContent.Substring(1);

                int nameStart = closeBracket + 1;
                if (nameStart >= text.Length)
                {
                    segments.Add(new TextSegment { Text = text.Substring(bestPos), Color = null });
                    break;
                }
                int closeIdx = text.IndexOf(closeTag, nameStart, StringComparison.Ordinal);

                if (closeIdx < 0)
                {
                    segments.Add(new TextSegment { Text = text.Substring(bestPos), Color = null });
                    break;
                }

                string name = text.Substring(nameStart, closeIdx - nameStart);
                segments.Add(new TextSegment
                {
                    Text = name,
                    Color = def.Col,
                    EntityType = def.Type,
                    EntityId = entityId
                });
                i = closeIdx + closeTag.Length;
            }
            return segments;
        }

        /// <summary>
        /// Strips «h:id»...«/h» and «s:id»...«/s» markers from text, returning plain text.
        /// Also handles old format «h»...«/h» and «s»...«/s».
        /// </summary>
        private static string StripNameMarkers(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var segments = ParseNameMarkers(text);
            var sb = new System.Text.StringBuilder();
            foreach (var seg in segments) sb.Append(seg.Text);
            return sb.ToString();
        }

        /// <summary>
        /// Returns true if text contains any «h or «s name markers (with or without IDs).
        /// </summary>
        private static bool HasNameMarkers(string text)
        {
            return !string.IsNullOrEmpty(text) &&
                   (text.Contains("«h") || text.Contains("«s") || text.Contains("«k") || text.Contains("«c"));
        }

        /// <summary>
        /// Represents a fragment of text on a single line, with optional color and entity info.
        /// </summary>
        private struct LineFragment
        {
            public string Text;
            public Color? Color;
            public string EntityType;
            public string EntityId;
        }

        /// <summary>
        /// Creates a vertical container of horizontal lines, each containing individually
        /// colored TextWidgets. This avoids the RichTextWidget brush-override problem by
        /// giving each segment its own brush with SetBrushColor.
        /// </summary>
        private static Widget CreateColoredBodyWidget(UIContext uiContext, Brush baseBrush, string text,
            float fixedWidth = 0f, int charsPerLine = 0)
        {
            var segments = ParseNameMarkers(text);

            // Break segments into lines of fragments, word-wrapping as needed
            var lines = new List<List<LineFragment>>();
            var currentLine = new List<LineFragment>();
            int col = 0;

            foreach (var seg in segments)
            {
                if (seg.Color.HasValue)
                {
                    // Entity name: keep whole, don't split mid-name
                    string name = seg.Text;
                    if (charsPerLine > 0 && col > 0 && col + 1 + name.Length > charsPerLine)
                    {
                        // Wrap to next line
                        lines.Add(currentLine);
                        currentLine = new List<LineFragment>();
                        col = 0;
                    }
                    // Add leading space
                    if (col > 0)
                    {
                        currentLine.Add(new LineFragment { Text = " " });
                        col++;
                    }
                    currentLine.Add(new LineFragment
                    {
                        Text = name,
                        Color = seg.Color,
                        EntityType = seg.EntityType,
                        EntityId = seg.EntityId
                    });
                    col += name.Length;
                }
                else
                {
                    // Plain text: split into words, wrap as needed
                    string[] words = seg.Text.Split(' ');
                    for (int wi = 0; wi < words.Length; wi++)
                    {
                        string word = words[wi];
                        if (word.Length == 0) continue;
                        if (charsPerLine > 0 && col > 0 && col + 1 + word.Length > charsPerLine)
                        {
                            lines.Add(currentLine);
                            currentLine = new List<LineFragment>();
                            col = 0;
                        }
                        if (col > 0)
                        {
                            currentLine.Add(new LineFragment { Text = " " });
                            col++;
                        }
                        currentLine.Add(new LineFragment { Text = word });
                        col += word.Length;
                    }
                }
            }
            if (currentLine.Count > 0)
                lines.Add(currentLine);

            MCMSettings.DebugLog("JournalSection: ColoredBody lines=" + lines.Count
                + " segments=" + segments.Count + " text='" + StripNameMarkers(text) + "'");

            // Build vertical ListPanel container for lines
            var container = new ListPanel(uiContext);
            container.WidthSizePolicy = fixedWidth > 0f ? SizePolicy.Fixed : SizePolicy.StretchToParent;
            if (fixedWidth > 0f) { container.SuggestedWidth = fixedWidth; container.MaxWidth = fixedWidth; }
            container.HeightSizePolicy = SizePolicy.CoverChildren;
            LoreSectionInjector.SetVerticalLayoutTopToBottom(container);

            foreach (var line in lines)
            {
                // Horizontal ListPanel for this line
                var lineWidget = new ListPanel(uiContext);
                lineWidget.WidthSizePolicy = fixedWidth > 0f ? SizePolicy.Fixed : SizePolicy.StretchToParent;
                if (fixedWidth > 0f) { lineWidget.SuggestedWidth = fixedWidth; lineWidget.MaxWidth = fixedWidth; }
                lineWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                SetHorizontalLayout(lineWidget);

                foreach (var frag in line)
                {
                    var tw = new TextWidget(uiContext);
                    tw.Text = frag.Text;
                    tw.WidthSizePolicy = SizePolicy.CoverChildren;
                    tw.HeightSizePolicy = SizePolicy.CoverChildren;

                    if (baseBrush != null)
                    {
                        var fragBrush = baseBrush.Clone();
                        if (fixedWidth > 0f)
                            ScaleBrushFontSize(fragBrush, 0.95f);
                        if (frag.Color.HasValue)
                            SetBrushColor(fragBrush, frag.Color.Value.Red, frag.Color.Value.Green,
                                frag.Color.Value.Blue, frag.Color.Value.Alpha);
                        var bp = tw.GetType().GetProperty("Brush", AllFlags);
                        if (bp != null && bp.CanWrite) bp.SetValue(tw, fragBrush);
                    }

                    // Make entity names clickable by wrapping in ButtonWidget
                    if (!string.IsNullOrEmpty(frag.EntityType) && !string.IsNullOrEmpty(frag.EntityId))
                    {
                        string link = "Encyclopedia/" + frag.EntityType + "/" + frag.EntityId;

                        Widget btnWrap = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget");
                        if (btnWrap == null) btnWrap = new Widget(uiContext);
                        btnWrap.WidthSizePolicy = SizePolicy.CoverChildren;
                        btnWrap.HeightSizePolicy = SizePolicy.CoverChildren;
                        btnWrap.DoNotAcceptEvents = false;
                        btnWrap.DoNotPassEventsToChildren = true;
                        btnWrap.AddChild(tw);

                        try
                        {
                            var eventFireEvent = btnWrap.GetType().GetEvent("EventFire",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (eventFireEvent != null)
                            {
                                Action<Widget, string, object[]> handler = (Widget sender, string eventName, object[] args) =>
                                {
                                    if (eventName == "Click")
                                    {
                                        MCMSettings.DebugLog("JournalSection: entity click -> " + link);
                                        _pendingNavLink = link;
                                    }
                                };
                                eventFireEvent.AddEventHandler(btnWrap, handler);
                            }
                        }
                        catch (Exception ex)
                        {
                            MCMSettings.DebugLog("JournalSection: entity click hook error: " + ex.ToString());
                        }

                        lineWidget.AddChild(btnWrap);
                    }
                    else
                    {
                        lineWidget.AddChild(tw);
                    }
                }

                container.AddChild(lineWidget);
            }

            return container;
        }

        /// <summary>
        /// Hooks the EventFire event on a RichTextWidget to intercept link clicks
        /// and navigate to encyclopedia pages via EncyclopediaManager.GoToLink.
        /// </summary>
        public static void HookRichTextLinkClicks(Widget widget)
        {
            try
            {
                var eventFireEvent = widget.GetType().GetEvent("EventFire",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (eventFireEvent != null)
                {
                    Action<Widget, string, object[]> handler = (Widget sender, string eventName, object[] args) =>
                    {
                        // Filter out noisy mouse/hover events from logging
                        if (eventName != null && !eventName.StartsWith("Mouse") && eventName != "HoverBegin" && eventName != "HoverEnd")
                        {
                            MCMSettings.DebugLog("JournalSection: RichText EventFire: eventName=" + eventName
                                + " args=" + (args != null ? string.Join(",", args.Select(a => a?.ToString() ?? "null")) : "null"));
                        }
                        // The event name or args may contain the href URL
                        string link = null;
                        if (eventName != null && eventName.StartsWith("Encyclopedia/"))
                            link = eventName;
                        else if (eventName != null && eventName.StartsWith("event:") && eventName.Length > 6)
                            link = eventName.Substring(6);
                        else if (args != null)
                        {
                            foreach (var arg in args)
                            {
                                string s = arg?.ToString();
                                if (s != null && s.Contains("Encyclopedia/"))
                                {
                                    int idx = s.IndexOf("Encyclopedia/");
                                    link = s.Substring(idx);
                                    break;
                                }
                            }
                        }
                        // Defer navigation to next tick — calling GoToLink inside a widget
                        // event handler fails silently because the widget tree is mid-update.
                        if (link != null)
                            _pendingNavLink = link;
                    };
                    eventFireEvent.AddEventHandler(widget, handler);
                    widget.DoNotPassEventsToChildren = false;
                    MCMSettings.DebugLog("JournalSection: hooked RichText EventFire for link clicks");
                }
                else
                {
                    MCMSettings.DebugLog("JournalSection: EventFire event not found on RichTextWidget");
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: HookRichTextLinkClicks error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Navigates to an encyclopedia page. Resolves the game object from the link and
        /// calls EncyclopediaNavigatorVM.ExecuteLink(pageId, target).
        /// Link format: "Encyclopedia/Hero/lord_1_1" or "Encyclopedia/Settlement/town_ES3"
        /// </summary>
        private static void NavigateToEncyclopediaLink(string link)
        {
            try
            {
                MCMSettings.DebugLog("JournalSection: navigating to " + link);

                // Parse link: "Encyclopedia/Hero/lord_1_72_1" → type="Hero", objectId="lord_1_72_1"
                var parts = link.Split('/');
                if (parts.Length < 3)
                {
                    MCMSettings.DebugLog("JournalSection: invalid link format: " + link);
                    return;
                }
                string pageType = parts[1]; // "Hero", "Settlement", "Clan", "Kingdom"
                string objectId = parts[2]; // "lord_1_72_1"

                // Resolve the actual game object
                object target = ResolveEncyclopediaTarget(pageType, objectId);
                MCMSettings.DebugLog("JournalSection: resolved target: " + (target != null ? target.GetType().Name + " " + objectId : "null"));

                // Validate that the target has an encyclopedia page before navigating.
                // Entities not registered in the encyclopedia (minor clans, mercenary clans, etc.)
                // cause ExecuteLink to throw "key not present", partially open a broken page,
                // and freeze the game. Check ALL types, not just Clan.
                if (!IsEncyclopediaNavigable(pageType, objectId))
                {
                    MCMSettings.DebugLog("JournalSection: target " + pageType + "/" + objectId
                        + " is not navigable in encyclopedia, skipping");
                    return;
                }

                // Use GoToLink as the primary navigation method. It goes through
                // EncyclopediaManager which handles the full open+navigate flow and
                // doesn't hit the SetEncyclopediaPage/_pages bug that ExecuteLink does.
                // ExecuteLink is only used as a fallback if GoToLink fails.
                var topScreen = ScreenManager.TopScreen;
                if (topScreen == null) return;
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                // Fix NavalDLC bug: install SetEncyclopediaPage prefix early so it can
                // detect and fix missing keys in _pages (e.g., "Clan") on the fly.
                EnsureSetEncyclopediaPagePatched();

                // Chronicle panel moved to EE-ChronicleNoters in v2.6.0.
                // If EE-ChronicleNoters is loaded, its panel will close itself when the encyclopedia opens.
                // GlobalChroniclePanel.ForceClosePanel();

                // Check if the encyclopedia is already open BEFORE trying GoToLink.
                // When already open, GoToLink "succeeds" (no exception) but doesn't
                // actually navigate to a different page — it's designed to OPEN the
                // encyclopedia from outside, not navigate within it. Skip straight
                // to ExecuteLink which handles in-encyclopedia navigation.
                bool alreadyOpen = EncyclopediaPageTracker.IsEncyclopediaOpen();
                if (alreadyOpen)
                {
                    MCMSettings.DebugLog("JournalSection: encyclopedia already open — skipping GoToLink, using ExecuteLink for " + link);
                }

                bool goToLinkWorked = false;
                if (!alreadyOpen)
                {
                    MCMSettings.DebugLog("JournalSection: trying GoToLink for " + link);
                    try
                    {
                        var campaign = Campaign.Current;
                        if (campaign != null)
                        {
                            var mgr = campaign.EncyclopediaManager;
                            if (mgr != null)
                            {
                                // 2026-05-26 v1.4.5 fix: vanilla code (e.g., RecruitVolunteerTroopVM.ExecuteOpenEncyclopedia,
                                // 35 bytes of IL) opens the encyclopedia with EXACTLY this pattern:
                                //   Campaign.Current.EncyclopediaManager.GoToLink(entity.EncyclopediaLink);
                                // Our hand-built "Encyclopedia/Clan/clan_nord_3" string is the WRONG format — engine's
                                // GoToLink parses it as a typed link and NREs / KeyNotFounds on DLC entities. Every
                                // entity exposes a string `EncyclopediaLink` property that returns the engine's
                                // INTERNAL link format. Use that property; never hand-build the link.
                                var goToLink1 = mgr.GetType().GetMethod("GoToLink",
                                    BindingFlags.Instance | BindingFlags.Public,
                                    null, new[] { typeof(string) }, null);
                                if (goToLink1 != null)
                                {
                                    // Resolve the vanilla EncyclopediaLink from the target entity (Hero/Clan/etc).
                                    string vanillaLink = null;
                                    if (target != null)
                                    {
                                        try
                                        {
                                            var encLinkProp = target.GetType().GetProperty("EncyclopediaLink",
                                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            if (encLinkProp != null)
                                            {
                                                vanillaLink = encLinkProp.GetValue(target) as string;
                                                MCMSettings.DebugLog("JournalSection: target." + target.GetType().Name
                                                    + ".EncyclopediaLink = '" + (vanillaLink ?? "(null)") + "'");
                                            }
                                            else
                                            {
                                                MCMSettings.DebugLog("JournalSection: target type "
                                                    + target.GetType().FullName + " has NO EncyclopediaLink property");
                                            }
                                        }
                                        catch (Exception linkEx)
                                        {
                                            MCMSettings.DebugLog("JournalSection: EncyclopediaLink getter threw: " + linkEx.ToString());
                                        }
                                    }
                                    else
                                    {
                                        MCMSettings.DebugLog("JournalSection: target is null — falling back to hand-built link");
                                    }

                                    string linkToCall = !string.IsNullOrEmpty(vanillaLink) ? vanillaLink : link;
                                    MCMSettings.DebugLog("JournalSection: calling GoToLink('" + linkToCall + "') "
                                        + (linkToCall == link ? "[hand-built fallback]" : "[vanilla EncyclopediaLink]"));

                                    EncyclopediaPageTracker.BeginPageTransition();
                                    try
                                    {
                                        goToLink1.Invoke(mgr, new object[] { linkToCall });
                                        goToLinkWorked = true;
                                        MCMSettings.DebugLog("JournalSection: GoToLink returned without exception for " + linkToCall);
                                    }
                                    finally
                                    {
                                        EncyclopediaPageTracker.EndPageTransition();
                                    }
                                }
                                else
                                {
                                    MCMSettings.DebugLog("JournalSection: GoToLink(string) method not found on EncyclopediaManager");
                                }
                            }
                        }
                    }
                    catch (Exception goToEx)
                    {
                        var inner = goToEx.InnerException ?? goToEx;
                        MCMSettings.DebugLog("JournalSection: GoToLink failed: " + inner.GetType().Name
                            + ": " + inner.Message);
                    }

                    if (goToLinkWorked)
                    {
                        // Verify the encyclopedia actually opened. GoToLink can "succeed"
                        // (no exception) but fail to open the UI layer (e.g., NavalDLC
                        // with unregistered page types silently skips rendering).
                        bool encIsOpen = EncyclopediaPageTracker.IsEncyclopediaOpen();
                        int layerCount = topScreen?.Layers.Count ?? -1;
                        MCMSettings.DebugLog("JournalSection: after GoToLink — IsEncyclopediaOpen="
                            + encIsOpen + " layers=" + layerCount);
                        if (encIsOpen)
                            return; // Encyclopedia opened — done
                        MCMSettings.DebugLog("JournalSection: GoToLink didn't open encyclopedia UI — falling back to ExecuteLink");
                    }
                }

                // Fallback: try ExecuteLink via EncyclopediaNavigatorVM
                MCMSettings.DebugLog("JournalSection: falling back to ExecuteLink");

                object encManager = FindEncyclopediaScreenManager(topScreen, flags);
                if (encManager == null)
                {
                    MCMSettings.DebugLog("JournalSection: EncyclopediaScreenManager not found");
                    return;
                }

                object navigatorVM = FindNavigatorVM(encManager, flags);
                if (navigatorVM == null)
                {
                    MCMSettings.DebugLog("JournalSection: NavigatorVM not found");
                    return;
                }

                // Find ExecuteLink on NavigatorVM (signature: String pageId, Object target)
                System.Reflection.MethodInfo execLink = null;
                var navType = navigatorVM.GetType();
                while (navType != null && navType != typeof(object))
                {
                    execLink = navType.GetMethod("ExecuteLink", flags | BindingFlags.DeclaredOnly);
                    if (execLink != null) break;
                    navType = navType.BaseType;
                }

                if (execLink != null && target != null)
                {
                    var parms = execLink.GetParameters();
                    MCMSettings.DebugLog("JournalSection: calling ExecuteLink(" + pageType + ", " + objectId + ")");

                    // Snapshot layers before ExecuteLink so we can detect & remove
                    // any invisible layers created by a failed call.
                    var layersBefore = new System.Collections.Generic.HashSet<object>();
                    foreach (var layer in topScreen.Layers)
                        layersBefore.Add(layer);
                    int layerCountBefore = topScreen.Layers.Count;

                    // Bracket the ExecuteLink call with page transition flags to suppress
                    // false close detection. The IsEncyclopediaOpen field briefly toggles
                    // false during page navigation, which would otherwise close the UI.
                    EncyclopediaPageTracker.BeginPageTransition();
                    try
                    {
                        if (parms.Length == 2)
                            execLink.Invoke(navigatorVM, new object[] { pageType, target });
                        else if (parms.Length == 1)
                            execLink.Invoke(navigatorVM, new object[] { link });
                        else
                        {
                            // Fill remaining params with defaults
                            object[] args = new object[parms.Length];
                            args[0] = pageType;
                            if (parms.Length > 1) args[1] = target;
                            for (int i = 2; i < parms.Length; i++)
                            {
                                var pt = parms[i].ParameterType;
                                args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                            }
                            execLink.Invoke(navigatorVM, args);
                        }
                    }
                    catch (Exception exLink)
                    {
                        // ExecuteLink can throw for clans/factions with missing dictionary entries
                        // (common in NavalDLC). The throw happens mid-transition and creates an
                        // invisible encyclopedia layer that captures all input, freezing the UI.
                        MCMSettings.DebugLog("JournalSection: ExecuteLink threw for " + pageType + "/" + objectId
                            + ": " + exLink.Message
                            + (exLink.InnerException != null ? " inner: " + exLink.InnerException.Message : ""));

                        // If the error is KeyNotFoundException, try GoToLink instead.
                        // GoToLink uses EncyclopediaManager's own page resolution which
                        // works for pages that SetEncyclopediaPage can't handle (NavalDLC bug).
                        var innerEx = exLink.InnerException ?? exLink;
                        if (innerEx is System.Collections.Generic.KeyNotFoundException)
                        {
                            MCMSettings.DebugLog("JournalSection: ExecuteLink KeyNotFound — proceeding to error recovery (GoToLink skipped, it never works for missing page types)");
                            // Don't try GoToLink fallback here. GoToLink never calls
                            // SetEncyclopediaPage, so it can't fix missing _pages keys.
                            // Worse, the failed ExecuteLink leaves IsEncyclopediaOpen=True
                            // which makes GoToLink fallback return early with a corrupted state.
                            // Fall straight through to error recovery which installs the
                            // SetEncyclopediaPage prefix and retries with ExecuteLink.
                        }

                        // Show user-visible message so they know why navigation failed
                        try
                        {
                            InformationManager.DisplayMessage(
                                new InformationMessage(
                                    "Cannot open " + pageType + " page — encyclopedia entry may be unavailable",
                                    Colors.Yellow));
                        }
                        catch (Exception ex2) { MCMSettings.DebugLog("JournalSectionInjector: DisplayMessage after ExecuteLink failure failed: " + ex2.Message); }

                        // Cancel any pending injection retries
                        _retryPending = false;
                        DisposeRetryTimer();

                        // Remove any new layers that ExecuteLink created before throwing.
                        // These are invisible but capture input, freezing the game.
                        // CloseEncyclopedia also fails in this state, so we must remove
                        // the layers directly from the screen.
                        try
                        {
                            int layerCountAfter = topScreen.Layers.Count;
                            if (layerCountAfter > layerCountBefore)
                            {
                                MCMSettings.DebugLog("JournalSection: ExecuteLink added "
                                    + (layerCountAfter - layerCountBefore) + " layer(s) — removing");

                                // Collect new layers (can't modify during enumeration)
                                var newLayers = new System.Collections.Generic.List<ScreenLayer>();
                                foreach (var layer in topScreen.Layers)
                                {
                                    if (!layersBefore.Contains(layer) && layer is ScreenLayer sl)
                                        newLayers.Add(sl);
                                }

                                // Remove them from the screen
                                foreach (var sl in newLayers)
                                {
                                    try
                                    {
                                        topScreen.RemoveLayer(sl);
                                        MCMSettings.DebugLog("JournalSection: removed stale layer "
                                            + sl.GetType().Name + " from screen");
                                    }
                                    catch (Exception remEx)
                                    {
                                        MCMSettings.DebugLog("JournalSection: failed to remove layer: " + remEx.Message);
                                    }
                                }
                            }
                            else
                            {
                                MCMSettings.DebugLog("JournalSection: no new layers after failed ExecuteLink (count="
                                    + layerCountAfter + ")");
                            }
                        }
                        catch (Exception layerEx)
                        {
                            MCMSettings.DebugLog("JournalSection: layer cleanup error: " + layerEx.Message);
                        }

                        // Recovery: patch GauntletLayer.ReleaseMovie to skip null
                        // identifiers, then call CloseEncyclopedia so the full close
                        // procedure runs without crashing. This properly cleans up
                        // layers, input restrictions, state, and movie references.
                        try
                        {
                            var reflFlags2 = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                            // Get _encyclopediaData to find the GauntletLayer type
                            var encDataField = encManager.GetType().GetField("_encyclopediaData", reflFlags2);
                            object encData = encDataField?.GetValue(encManager);

                            // Patch GauntletLayer.ReleaseMovie to skip null identifiers
                            if (encData != null)
                            {
                                var layerField = encData.GetType().GetField("_activeGauntletLayer", reflFlags2);
                                var dataLayer = layerField?.GetValue(encData);
                                if (dataLayer != null)
                                    PatchReleaseMovieIfNeeded(dataLayer.GetType());
                            }

                            // Safety net patches
                            PatchCloseEncyclopediaIfNeeded(encManager.GetType());
                            if (encData != null)
                                PatchEncyclopediaDataOnTickIfNeeded(encData.GetType());

                            // Patch ExecuteLink with finalizer to handle KeyNotFoundException
                            // from ALL code paths (N key, native navigation, etc.)
                            PatchExecuteLinkFinalizerIfNeeded(encManager.GetType());

                            // Patch SetEncyclopediaPage to skip missing keys instead of crashing.
                            // This prevents the crash at the root — the page just won't load,
                            // and the encyclopedia shows the home/previous page instead.
                            if (encData != null)
                                PatchSetEncyclopediaPageIfNeeded(encData.GetType());

                            // Set flags and call CloseEncyclopedia
                            _suppressReleaseMovieNull = true;
                            _suppressCloseNullRef = true;
                            _encyclopediaCorrupted = true;
                            _closeEncyclopediaFailed = false;

                            // Save critical state that CloseEncyclopedia destroys but
                            // that's needed for the encyclopedia to reopen later.
                            // _pages holds registered page types (set during init, never recreated)
                            // _lists holds list view data (also set during init)
                            // IMPORTANT: deep copy the dictionary contents because CloseEncyclopedia
                            // may call .Clear() which empties the same dictionary object.
                            System.Collections.IDictionary savedPages = null, savedLists = null;
                            System.Reflection.FieldInfo pagesField = null, listsField = null;
                            if (encData != null)
                            {
                                pagesField = encData.GetType().GetField("_pages", reflFlags2);
                                listsField = encData.GetType().GetField("_lists", reflFlags2);
                                savedPages = DeepCopyDictionary(pagesField?.GetValue(encData));
                                savedLists = DeepCopyDictionary(listsField?.GetValue(encData));
                                if (savedPages != null)
                                    MCMSettings.DebugLog("JournalSection: saved _pages deep copy with "
                                        + savedPages.Count + " entries");
                            }

                            try
                            {
                                var closeMethod = encManager.GetType().GetMethod("CloseEncyclopedia",
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (closeMethod == null)
                                {
                                    var searchType = encManager.GetType();
                                    while (searchType != null && searchType != typeof(object))
                                    {
                                        closeMethod = searchType.GetMethod("CloseEncyclopedia",
                                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                            | BindingFlags.DeclaredOnly);
                                        if (closeMethod != null) break;
                                        searchType = searchType.BaseType;
                                    }
                                }

                                if (closeMethod != null)
                                {
                                    closeMethod.Invoke(encManager, null);
                                    MCMSettings.DebugLog("JournalSection: CloseEncyclopedia completed after failed ExecuteLink");
                                }
                            }
                            catch (Exception closeEx)
                            {
                                var inner = closeEx.InnerException ?? closeEx;
                                MCMSettings.DebugLog("JournalSection: CloseEncyclopedia error (suppressed): "
                                    + inner.GetType().Name + ": " + inner.Message);
                            }

                            _suppressReleaseMovieNull = false;

                            // Restore critical state destroyed by CloseEncyclopedia.
                            // We deep-copied the entries, so restore by repopulating the field.
                            if (encData != null)
                            {
                                RestoreDictionary(encData, pagesField, savedPages, "_pages");
                                RestoreDictionary(encData, listsField, savedLists, "_lists");
                            }

                            // Only clear corrupted flag if CloseEncyclopedia actually succeeded
                            if (!_closeEncyclopediaFailed)
                            {
                                _encyclopediaCorrupted = false;
                                MCMSettings.DebugLog("JournalSection: encyclopedia state cleaned — OnTick unblocked");
                            }
                            else
                            {
                                MCMSettings.DebugLog("JournalSection: CloseEncyclopedia failed — keeping OnTick blocked");
                            }

                            // Force IsEncyclopediaOpen=false if CloseEncyclopedia didn't
                            System.Reflection.FieldInfo isOpenField = null;
                            var mgrType = encManager.GetType();
                            while (mgrType != null && mgrType != typeof(object))
                            {
                                isOpenField = mgrType.GetField("<IsEncyclopediaOpen>k__BackingField", reflFlags2);
                                if (isOpenField != null && isOpenField.FieldType == typeof(bool)) break;
                                isOpenField = null;
                                mgrType = mgrType.BaseType;
                            }
                            if (isOpenField != null)
                            {
                                if (isOpenField.GetValue(encManager) is bool bOpen && bOpen)
                                {
                                    isOpenField.SetValue(encManager, false);
                                    MCMSettings.DebugLog("JournalSection: force-set IsEncyclopediaOpen=false");
                                }
                            }
                        }
                        catch (Exception resetEx)
                        {
                            MCMSettings.DebugLog("JournalSection: state reset error: " + resetEx.Message);
                        }

                        // Also clear the stale layer ref
                        EncyclopediaPageTracker.EncyclopediaLayerRef = null;

                        // Retry via ExecuteLink now that the SetEncyclopediaPage prefix is
                        // installed. The first attempt failed because _encyclopediaData
                        // didn't exist yet (created by CreateLayout during ExecuteLink).
                        // Now the prefix is active and will add missing _pages keys on the fly.
                        // GoToLink doesn't work — it never calls SetEncyclopediaPage.
                        // ExecuteLink goes through CreateLayout → SetEncyclopediaPage,
                        // where our prefix adds the missing key and lets it proceed.
                        if (_setEncyclopediaPagePatchApplied && execLink != null && target != null)
                        {
                            MCMSettings.DebugLog("JournalSection: retrying ExecuteLink after prefix installed");
                            try
                            {
                                if (parms.Length == 2)
                                    execLink.Invoke(navigatorVM, new object[] { pageType, target });
                                else if (parms.Length == 1)
                                    execLink.Invoke(navigatorVM, new object[] { link });
                                else
                                {
                                    object[] retryArgs = new object[parms.Length];
                                    retryArgs[0] = pageType;
                                    if (parms.Length > 1) retryArgs[1] = target;
                                    for (int i = 2; i < parms.Length; i++)
                                    {
                                        var pt = parms[i].ParameterType;
                                        retryArgs[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                                    }
                                    execLink.Invoke(navigatorVM, retryArgs);
                                }
                                bool retryOpened = EncyclopediaPageTracker.IsEncyclopediaOpen();
                                MCMSettings.DebugLog("JournalSection: retry ExecuteLink — IsEncyclopediaOpen=" + retryOpened);
                            }
                            catch (Exception retryEx)
                            {
                                var inner = retryEx.InnerException ?? retryEx;
                                MCMSettings.DebugLog("JournalSection: retry ExecuteLink error: "
                                    + inner.GetType().Name + ": " + inner.Message);
                            }
                        }
                    }
                    finally
                    {
                        EncyclopediaPageTracker.EndPageTransition();
                    }
                    return;
                }

                if (target == null)
                    MCMSettings.DebugLog("JournalSection: could not resolve target object for " + pageType + "/" + objectId);

                // Fallback: try GoToLink on EncyclopediaManager
                FallbackGoToLink(link);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: NavigateToEncyclopediaLink error: " + ex.Message
                    + (ex.InnerException != null ? " inner: " + ex.InnerException.Message : ""));
            }
        }

        /// <summary>
        /// Patches CloseEncyclopedia on the encyclopedia manager type with a Harmony finalizer
        /// that suppresses NullReferenceException when _suppressCloseNullRef is set.
        /// This prevents the crash when OnTick tries to close a partially-initialized encyclopedia
        /// after a failed ExecuteLink.
        /// </summary>
        private static void PatchCloseEncyclopediaIfNeeded(Type encManagerType)
        {
            if (_closeEncyclopediaPatchApplied) return;
            try
            {
                var declFlags = BindingFlags.Instance | BindingFlags.Public
                              | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                var closeMethod = encManagerType.GetMethod("CloseEncyclopedia", declFlags);
                if (closeMethod == null)
                {
                    // Search up the hierarchy
                    var searchType = encManagerType;
                    while (searchType != null && searchType != typeof(object))
                    {
                        closeMethod = searchType.GetMethod("CloseEncyclopedia",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly);
                        if (closeMethod != null) break;
                        searchType = searchType.BaseType;
                    }
                }

                if (closeMethod != null)
                {
                    var harmony = new HarmonyLib.Harmony("com.editableencyclopedia.closefix");
                    var finalizer = typeof(JournalSectionInjector).GetMethod("CloseEncyclopediaFinalizer",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(closeMethod, finalizer: new HarmonyLib.HarmonyMethod(finalizer));
                    _closeEncyclopediaPatchApplied = true;
                    MCMSettings.DebugLog("JournalSection: patched " + closeMethod.DeclaringType.Name
                        + ".CloseEncyclopedia() with NullRef finalizer");
                }
                else
                {
                    MCMSettings.DebugLog("JournalSection: CloseEncyclopedia method not found on " + encManagerType.Name);
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: failed to patch CloseEncyclopedia: " + ex.ToString());
            }
        }

        /// <summary>
        /// Harmony finalizer for CloseEncyclopedia — suppresses NullReferenceException
        /// when we've set _suppressCloseNullRef after a failed ExecuteLink. This prevents
        /// the crash from ReleaseMovie(null) on a partially-initialized encyclopedia.
        /// </summary>
        public static Exception CloseEncyclopediaFinalizer(Exception __exception)
        {
            if (__exception != null && _suppressCloseNullRef)
            {
                _suppressCloseNullRef = false;
                _closeEncyclopediaFailed = true; // signal that cleanup didn't fully run
                MCMSettings.DebugLog("JournalSection: suppressed " + __exception.GetType().Name
                    + " in CloseEncyclopedia after failed ExecuteLink: " + __exception.Message);
                return null; // suppress the exception
            }
            return __exception; // re-throw normally
        }

        /// <summary>
        /// Patches GauntletLayer.ReleaseMovie with a prefix that skips the call when the
        /// movie identifier parameter is null. This prevents the NullRef crash at the source,
        /// allowing CloseEncyclopedia to run its FULL cleanup code instead of crashing
        /// mid-way through.
        /// </summary>
        private static void PatchReleaseMovieIfNeeded(Type gauntletLayerType)
        {
            if (_releaseMoviePatchApplied) return;
            try
            {
                var releaseMethod = gauntletLayerType.GetMethod("ReleaseMovie",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (releaseMethod == null)
                {
                    var searchType = gauntletLayerType;
                    while (searchType != null && searchType != typeof(object))
                    {
                        releaseMethod = searchType.GetMethod("ReleaseMovie",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly);
                        if (releaseMethod != null) break;
                        searchType = searchType.BaseType;
                    }
                }

                if (releaseMethod != null)
                {
                    var harmony = new HarmonyLib.Harmony("com.editableencyclopedia.releasemoviefix");
                    var prefix = typeof(JournalSectionInjector).GetMethod("ReleaseMoviePrefix",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(releaseMethod, prefix: new HarmonyLib.HarmonyMethod(prefix));
                    _releaseMoviePatchApplied = true;
                    MCMSettings.DebugLog("JournalSection: patched " + releaseMethod.DeclaringType.Name
                        + ".ReleaseMovie() — will skip null identifiers");
                }
                else
                {
                    MCMSettings.DebugLog("JournalSection: ReleaseMovie not found on " + gauntletLayerType.Name);
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: failed to patch ReleaseMovie: " + ex.ToString());
            }
        }

        /// <summary>
        /// Harmony prefix for GauntletLayer.ReleaseMovie — skips the call entirely when the
        /// movie identifier is null AND _suppressReleaseMovieNull is set. This prevents the
        /// NullRef crash while letting normal ReleaseMovie calls through.
        /// </summary>
        public static bool ReleaseMoviePrefix(object __0)
        {
            if (__0 == null && _suppressReleaseMovieNull)
            {
                MCMSettings.DebugLog("JournalSection: skipped ReleaseMovie(null) — preventing NullRef");
                return false; // skip original
            }
            return true; // run normally
        }

        /// <summary>
        /// Patches EncyclopediaData.OnTick with a prefix that skips execution when
        /// _encyclopediaCorrupted is set. This prevents OnTick from re-enabling input
        /// restrictions or calling CloseEncyclopedia on corrupted state.
        /// </summary>
        private static void PatchEncyclopediaDataOnTickIfNeeded(Type encDataType)
        {
            if (_onTickPatchApplied) return;
            try
            {
                var onTick = encDataType.GetMethod("OnTick",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (onTick == null)
                {
                    // Search up hierarchy
                    var searchType = encDataType;
                    while (searchType != null && searchType != typeof(object))
                    {
                        onTick = searchType.GetMethod("OnTick",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly);
                        if (onTick != null) break;
                        searchType = searchType.BaseType;
                    }
                }

                if (onTick != null)
                {
                    var harmony = new HarmonyLib.Harmony("com.editableencyclopedia.ontickfix");
                    var prefix = typeof(JournalSectionInjector).GetMethod("EncyclopediaDataOnTickPrefix",
                        BindingFlags.Static | BindingFlags.Public);
                    var finalizer = typeof(JournalSectionInjector).GetMethod("EncyclopediaDataOnTickFinalizer",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(onTick,
                        prefix: new HarmonyLib.HarmonyMethod(prefix),
                        finalizer: new HarmonyLib.HarmonyMethod(finalizer));
                    _onTickPatchApplied = true;
                    MCMSettings.DebugLog("JournalSection: patched " + onTick.DeclaringType.Name
                        + ".OnTick() — will skip when corrupted + finalizer safety net");
                }
                else
                {
                    MCMSettings.DebugLog("JournalSection: OnTick method not found on " + encDataType.Name);
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: failed to patch OnTick: " + ex.ToString());
            }
        }

        /// <summary>
        /// Harmony prefix for EncyclopediaData.OnTick — skips execution entirely when
        /// the encyclopedia is in a corrupted state after a failed ExecuteLink.
        /// Returns false to skip original, true to run normally.
        /// </summary>
        public static bool EncyclopediaDataOnTickPrefix()
        {
            if (_encyclopediaCorrupted)
                return false; // skip OnTick
            return true; // run normally
        }

        /// <summary>
        /// Harmony finalizer for EncyclopediaData.OnTick — safety net that suppresses
        /// NullReferenceException when the encyclopedia is in a corrupted state. This
        /// catches cases where the prefix check races with state changes, or where
        /// _encyclopediaCorrupted was cleared prematurely.
        /// </summary>
        public static Exception EncyclopediaDataOnTickFinalizer(Exception __exception)
        {
            if (__exception is NullReferenceException)
            {
                // The encyclopedia is in a broken state — block future OnTick calls
                _encyclopediaCorrupted = true;
                MCMSettings.DebugLog("JournalSection: OnTick NullRef suppressed — blocking future OnTick calls");
                return null; // suppress
            }
            return __exception;
        }

        /// <summary>
        /// Patches GauntletMapEncyclopediaView.ExecuteLink with a Harmony finalizer that
        /// catches KeyNotFoundException from SetEncyclopediaPage (NavalDLC bug where certain
        /// page types aren't registered in _pages). This covers ALL code paths: Chronicle
        /// links, N key open, native navigation, etc. When the exception is caught, we call
        /// CloseEncyclopedia to clean up the partial state.
        /// </summary>
        private static void PatchExecuteLinkFinalizerIfNeeded(Type encManagerType)
        {
            if (_executeLinkFinalizerApplied) return;
            try
            {
                var execLink = encManagerType.GetMethod("ExecuteLink",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (execLink == null)
                {
                    var searchType = encManagerType;
                    while (searchType != null && searchType != typeof(object))
                    {
                        execLink = searchType.GetMethod("ExecuteLink",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly);
                        if (execLink != null) break;
                        searchType = searchType.BaseType;
                    }
                }

                if (execLink != null)
                {
                    var harmony = new HarmonyLib.Harmony("com.editableencyclopedia.executelinkfix");
                    var finalizer = typeof(JournalSectionInjector).GetMethod("ExecuteLinkKeyNotFoundFinalizer",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(execLink, finalizer: new HarmonyLib.HarmonyMethod(finalizer));
                    _executeLinkFinalizerApplied = true;
                    MCMSettings.DebugLog("JournalSection: patched " + execLink.DeclaringType.Name
                        + ".ExecuteLink() with KeyNotFoundException finalizer");
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: failed to patch ExecuteLink finalizer: " + ex.ToString());
            }
        }

        /// <summary>
        /// Harmony finalizer for GauntletMapEncyclopediaView.ExecuteLink — when a
        /// KeyNotFoundException occurs (page type not in _pages), suppress it and
        /// trigger CloseEncyclopedia to clean up the partial encyclopedia state.
        /// </summary>
        public static Exception ExecuteLinkKeyNotFoundFinalizer(Exception __exception, object __instance)
        {
            if (__exception is System.Collections.Generic.KeyNotFoundException)
            {
                MCMSettings.DebugLog("JournalSection: ExecuteLink KeyNotFoundException suppressed: " + __exception.Message);
                try
                {
                    // Patch SetEncyclopediaPage so future attempts with missing keys are
                    // handled by the prefix (auto-fix from _lists/assemblies, or skip).
                    var reflFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var encDataField = __instance.GetType().GetField("_encyclopediaData", reflFlags);
                    object encData = encDataField?.GetValue(__instance);
                    if (encData != null)
                        PatchSetEncyclopediaPageIfNeeded(encData.GetType());

                    // DO NOT call CloseEncyclopedia here — it leaves orphaned widget
                    // containers that cause NullRef in EventManager.DefragContainers().
                    // Just suppress the exception. The encyclopedia may be in a partial
                    // state but OnTick suppression will prevent follow-up NullRefs,
                    // and the user can close/reopen normally.
                    _encyclopediaCorrupted = true;
                    MCMSettings.DebugLog("JournalSection: ExecuteLink KeyNotFoundException — "
                        + "suppressed without CloseEncyclopedia to avoid DefragContainers crash");
                }
                catch (Exception recEx)
                {
                    MCMSettings.DebugLog("JournalSection: ExecuteLink finalizer error: " + recEx.Message);
                }
                return null; // suppress the KeyNotFoundException
            }
            return __exception;
        }

        /// <summary>
        /// Applies a Harmony prefix to EncyclopediaData.SetEncyclopediaPage so that
        /// missing page-type keys are skipped instead of throwing KeyNotFoundException.
        /// </summary>
        private static void PatchSetEncyclopediaPageIfNeeded(Type encDataType)
        {
            if (_setEncyclopediaPagePatchApplied) return;
            try
            {
                var setPage = encDataType.GetMethod("SetEncyclopediaPage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (setPage == null)
                {
                    var searchType = encDataType;
                    while (searchType != null && searchType != typeof(object))
                    {
                        setPage = searchType.GetMethod("SetEncyclopediaPage",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly);
                        if (setPage != null) break;
                        searchType = searchType.BaseType;
                    }
                }

                if (setPage != null)
                {
                    var harmony = new HarmonyLib.Harmony("com.editableencyclopedia.setpagefix");
                    var prefix = typeof(JournalSectionInjector).GetMethod("SetEncyclopediaPagePrefix",
                        BindingFlags.Static | BindingFlags.Public);
                    var finalizer = typeof(JournalSectionInjector).GetMethod("SetEncyclopediaPageFinalizer",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(setPage,
                        prefix: new HarmonyLib.HarmonyMethod(prefix),
                        finalizer: new HarmonyLib.HarmonyMethod(finalizer));
                    _setEncyclopediaPagePatchApplied = true;
                    MCMSettings.DebugLog("JournalSection: patched " + setPage.DeclaringType.Name
                        + ".SetEncyclopediaPage() with prefix + finalizer");
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: failed to patch SetEncyclopediaPage: " + ex.ToString());
            }
        }

        /// <summary>
        /// Harmony prefix for EncyclopediaData.SetEncyclopediaPage — checks whether the
        /// requested pageId exists in the _pages dictionary. If it doesn't, the original
        /// method is skipped entirely, preventing KeyNotFoundException and allowing the
        /// encyclopedia to remain open on its current/home page.
        /// </summary>
        public static bool SetEncyclopediaPagePrefix(object __instance, object[] __args)
        {
            try
            {
                // First arg is the pageId string
                if (__args == null || __args.Length == 0) return true;
                string pageId = __args[0] as string;
                if (pageId == null) return true;

                var reflFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var pagesField = __instance.GetType().GetField("_pages", reflFlags);
                if (pagesField == null) return true; // can't check, let it run

                var pages = pagesField.GetValue(__instance);
                if (pages == null) return true;

                bool keyMissing = false;

                // Use IDictionary to check ContainsKey
                var dict = pages as System.Collections.IDictionary;
                if (dict != null)
                {
                    keyMissing = !dict.Contains(pageId);
                }
                else
                {
                    // Fallback: try reflection ContainsKey
                    var containsMethod = pages.GetType().GetMethod("ContainsKey");
                    if (containsMethod != null)
                        keyMissing = !(bool)containsMethod.Invoke(pages, new object[] { pageId });
                }

                if (!keyMissing)
                {
                    // Valid page being loaded — mark encyclopedia as having a page.
                    _encyclopediaHasPage = true;
                    if (_encyclopediaCorrupted)
                    {
                        _encyclopediaCorrupted = false;
                        MCMSettings.DebugLog("JournalSection: valid page '" + pageId
                            + "' loading — cleared corrupted flag, OnTick unblocked");
                    }
                }

                if (keyMissing)
                {
                    // Try to fix the missing key on the fly — NavalDLC is missing "Clan"
                    // in _pages even though _lists has DefaultEncyclopediaClanPage.
                    // Find the page object from _lists and add it to _pages.
                    var listsField = __instance.GetType().GetField("_lists", reflFlags);
                    if (listsField != null && dict != null)
                    {
                        var lists = listsField.GetValue(__instance) as System.Collections.IDictionary;
                        if (lists != null)
                        {
                            object matchingPage = null;
                            foreach (System.Collections.DictionaryEntry entry in lists)
                            {
                                if (entry.Key != null
                                    && entry.Key.GetType().Name.IndexOf(pageId, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    matchingPage = entry.Key;
                                    break;
                                }
                            }
                            if (matchingPage != null)
                            {
                                dict[pageId] = matchingPage;
                                _encyclopediaHasPage = true;
                                MCMSettings.DebugLog("JournalSection: SetEncyclopediaPage — added missing '"
                                    + pageId + "' to _pages from _lists (" + matchingPage.GetType().Name
                                    + "). Proceeding with original method.");
                                return true; // let the original method run now
                            }
                        }
                    }

                    // Navigation page types (Home, ListPage, LastPage) are handled inline
                    // in vanilla's SetEncyclopediaPage but NavalDLC may have removed them
                    // from _pages. Try adding a temporary key so the original method can
                    // handle them (vanilla checks _pages first, then switches on pageId).
                    if (_navigationPageIds.Contains(pageId))
                    {
                        // LastPage is back-navigation — just skip silently
                        if (pageId == "LastPage")
                        {
                            MCMSettings.DebugLog("JournalSection: SetEncyclopediaPage — 'LastPage' skipped (no history)");
                            return false;
                        }

                        // Try to add the key to _pages so the original method doesn't throw.
                        // Use __args[1] (the page handler) if available, otherwise use any
                        // existing page handler as a placeholder.
                        if (dict != null)
                        {
                            object pageValue = (__args.Length >= 2) ? __args[1] : null;
                            if (pageValue == null)
                            {
                                // Use any existing page handler as placeholder
                                foreach (System.Collections.DictionaryEntry entry in dict)
                                {
                                    if (entry.Value != null)
                                    {
                                        pageValue = entry.Value;
                                        break;
                                    }
                                }
                            }
                            if (pageValue != null)
                            {
                                dict[pageId] = pageValue;
                                _encyclopediaHasPage = true;
                                _lastNavPageIdAdded = pageId;
                                MCMSettings.DebugLog("JournalSection: SetEncyclopediaPage — added '"
                                    + pageId + "' to _pages (value=" + pageValue.GetType().Name
                                    + "), letting original method handle it");
                                return true; // let original method run
                            }
                        }

                        // Fallback: redirect to Hero for initial open, skip for in-encyclopedia nav
                        if (_encyclopediaHasPage)
                        {
                            MCMSettings.DebugLog("JournalSection: SetEncyclopediaPage — '"
                                + pageId + "' skipped (staying on current page, no dict)");
                            return false;
                        }

                        if (dict != null && __args.Length >= 2 && dict.Contains("Hero"))
                        {
                            try
                            {
                                var mainHero = Hero.MainHero;
                                if (mainHero != null)
                                {
                                    __args[0] = "Hero";
                                    __args[1] = mainHero;
                                    _encyclopediaHasPage = true;
                                    MCMSettings.DebugLog("JournalSection: SetEncyclopediaPage — redirecting '"
                                        + pageId + "' to Hero (player) for initial open");
                                    return true;
                                }
                            }
                            catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: redirect to Hero page failed: " + ex.ToString()); }
                        }

                        MCMSettings.DebugLog("JournalSection: SetEncyclopediaPage — '"
                            + pageId + "' is a navigation page with no handler, skipping");
                        return false;
                    }

                    // For entity page types: search loaded assemblies (with negative cache).
                    if (dict != null && !_missingPageCache.Contains(pageId))
                    {
                        object assemblyMatch = FindPageHandlerInAssemblies(pageId, dict);
                        if (assemblyMatch != null)
                        {
                            dict[pageId] = assemblyMatch;
                            _encyclopediaHasPage = true;
                            MCMSettings.DebugLog("JournalSection: SetEncyclopediaPage — added missing '"
                                + pageId + "' to _pages from assembly scan ("
                                + assemblyMatch.GetType().FullName + "). Proceeding with original method.");
                            return true; // fixed, let the original method run
                        }
                        _missingPageCache.Add(pageId);
                    }

                    // Could not fix — skip the original method instead of throwing.
                    // Throwing triggers the ExecuteLink finalizer which calls CloseEncyclopedia,
                    // and that leaves orphaned widget containers causing NullRef in DefragContainers.
                    MCMSettings.DebugLog("JournalSection: SetEncyclopediaPage — pageId '"
                        + pageId + "' not found in _pages (available keys: "
                        + DictKeysPreview(dict ?? pages as System.Collections.IDictionary)
                        + "), skipping to prevent crash");

                    try
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(
                                "Encyclopedia: '" + pageId + "' page not available (NavalDLC limitation)",
                                Colors.Yellow));
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: DisplayMessage for missing page failed: " + ex.ToString()); }

                    return false; // skip SetEncyclopediaPage — prevents crash
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: SetEncyclopediaPagePrefix error: " + ex.ToString());
            }
            return true; // run original
        }

        /// <summary>
        /// Harmony finalizer for EncyclopediaData.SetEncyclopediaPage — catches any exception
        /// thrown when the original method tries to handle navigation page types (Home, ListPage)
        /// that we temporarily added to _pages. If the original method crashes, suppress the
        /// exception and clean up the temporary entry.
        /// </summary>
        public static Exception SetEncyclopediaPageFinalizer(Exception __exception, object __instance)
        {
            string navPageAdded = _lastNavPageIdAdded;
            _lastNavPageIdAdded = null;

            if (__exception != null)
            {
                MCMSettings.DebugLog("JournalSection: SetEncyclopediaPage threw for nav page '"
                    + (navPageAdded ?? "?") + "': " + __exception.GetType().Name + ": " + __exception.Message);

                // Remove the temporary _pages entry we added
                if (navPageAdded != null)
                {
                    try
                    {
                        var reflFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                        var pagesField = __instance.GetType().GetField("_pages", reflFlags);
                        if (pagesField != null)
                        {
                            var dict = pagesField.GetValue(__instance) as System.Collections.IDictionary;
                            if (dict != null && dict.Contains(navPageAdded))
                            {
                                dict.Remove(navPageAdded);
                                MCMSettings.DebugLog("JournalSection: removed temporary '" + navPageAdded + "' from _pages");
                            }
                        }
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: cleanup of temporary nav page entry failed: " + ex.ToString()); }
                }

                return null; // suppress — encyclopedia stays on current page
            }

            // Success — the original method handled the navigation page type
            if (navPageAdded != null)
            {
                MCMSettings.DebugLog("JournalSection: SetEncyclopediaPage handled '"
                    + navPageAdded + "' successfully (original method ran without error)");
            }
            return null;
        }

        /// <summary>
        /// Deep copies an IDictionary (or object castable to IDictionary) into a Hashtable.
        /// CloseEncyclopedia may call .Clear() on the original dictionary, so a shallow
        /// reference save would lose all entries.
        /// </summary>
        private static System.Collections.IDictionary DeepCopyDictionary(object dict)
        {
            if (dict == null) return null;
            try
            {
                var src = dict as System.Collections.IDictionary;
                if (src == null) return null;
                var copy = new System.Collections.Hashtable(src.Count);
                foreach (System.Collections.DictionaryEntry entry in src)
                    copy[entry.Key] = entry.Value;
                return copy;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: DeepCopyDictionary error: " + ex.ToString());
                return null;
            }
        }

        /// <summary>
        /// Restores dictionary contents from a deep copy into the field on the target object.
        /// If the field's dictionary still exists, repopulates it in-place.
        /// If the field was nulled, creates a new dictionary and assigns it.
        /// </summary>
        private static void RestoreDictionary(object target, System.Reflection.FieldInfo field,
            System.Collections.IDictionary saved, string name)
        {
            if (field == null || saved == null || saved.Count == 0) return;
            try
            {
                var current = field.GetValue(target) as System.Collections.IDictionary;
                if (current != null)
                {
                    // Dictionary still exists — repopulate in-place
                    current.Clear();
                    foreach (System.Collections.DictionaryEntry entry in saved)
                        current[entry.Key] = entry.Value;
                    MCMSettings.DebugLog("JournalSection: restored " + name + " in-place with "
                        + current.Count + " entries (keys: " + DictKeysPreview(current) + ")");
                }
                else
                {
                    // Field was nulled — create a new instance of the correct dictionary type
                    // (e.g., Dictionary<string, EncyclopediaPage>) and populate it.
                    try
                    {
                        var newDict = Activator.CreateInstance(field.FieldType) as System.Collections.IDictionary;
                        if (newDict != null)
                        {
                            foreach (System.Collections.DictionaryEntry entry in saved)
                                newDict[entry.Key] = entry.Value;
                            field.SetValue(target, newDict);
                            MCMSettings.DebugLog("JournalSection: restored " + name + " via new "
                                + field.FieldType.Name + " (" + newDict.Count + " entries)");
                        }
                        else
                        {
                            MCMSettings.DebugLog("JournalSection: RestoreDictionary(" + name
                                + ") — could not create instance of " + field.FieldType.Name);
                        }
                    }
                    catch (Exception createEx)
                    {
                        MCMSettings.DebugLog("JournalSection: RestoreDictionary(" + name
                            + ") create error: " + createEx.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: RestoreDictionary(" + name + ") error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Returns a preview of dictionary keys for diagnostic logging.
        /// </summary>
        private static string DictKeysPreview(System.Collections.IDictionary dict)
        {
            if (dict == null || dict.Count == 0) return "(empty)";
            var sb = new System.Text.StringBuilder();
            int i = 0;
            foreach (object key in dict.Keys)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(key?.ToString() ?? "null");
                if (++i >= 10) { sb.Append("..."); break; }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Searches loaded assemblies for a page handler type matching the given pageId.
        /// NavalDLC omits certain page types from _pages (Home, ListPage, etc.) even though
        /// the handler classes exist in the game assemblies. This method finds those handlers
        /// by matching type names against patterns like "DefaultEncyclopedia{pageId}Page" or
        /// any type whose name contains the pageId and inherits from a known page interface.
        /// Returns a new instance of the handler, or null if not found.
        /// </summary>
        private static object FindPageHandlerInAssemblies(string pageId, System.Collections.IDictionary existingPages)
        {
            try
            {
                // Determine the interface/base type of existing page handlers
                Type pageBaseType = null;
                foreach (System.Collections.DictionaryEntry entry in existingPages)
                {
                    if (entry.Value != null)
                    {
                        pageBaseType = entry.Value.GetType();
                        break;
                    }
                }
                if (pageBaseType == null) return null;

                // Collect all interfaces/base types that existing page handlers implement
                var pageInterfaces = new System.Collections.Generic.HashSet<Type>();
                foreach (var iface in pageBaseType.GetInterfaces())
                    pageInterfaces.Add(iface);
                var baseT = pageBaseType.BaseType;
                while (baseT != null && baseT != typeof(object))
                {
                    pageInterfaces.Add(baseT);
                    baseT = baseT.BaseType;
                }

                // Name patterns to search for, most specific first
                string[] patterns = new[]
                {
                    "DefaultEncyclopedia" + pageId + "Page",  // e.g. DefaultEncyclopediaHomePage
                    "Encyclopedia" + pageId + "Page",          // e.g. EncyclopediaHomePage
                    "DefaultEncyclopedia" + pageId,            // e.g. DefaultEncyclopediaHome
                    "Encyclopedia" + pageId,                    // e.g. EncyclopediaHome
                };

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (var type in asm.GetTypes())
                        {
                            if (type.IsAbstract || type.IsInterface) continue;

                            // Check name patterns
                            bool nameMatch = false;
                            foreach (var pattern in patterns)
                            {
                                if (string.Equals(type.Name, pattern, StringComparison.OrdinalIgnoreCase))
                                {
                                    nameMatch = true;
                                    break;
                                }
                            }
                            if (!nameMatch) continue;

                            // Verify it implements/inherits the same interface as existing page handlers
                            bool typeMatch = false;
                            foreach (var iface in pageInterfaces)
                            {
                                if (iface.IsAssignableFrom(type))
                                {
                                    typeMatch = true;
                                    break;
                                }
                            }
                            if (!typeMatch) continue;

                            // Try to create an instance
                            try
                            {
                                var instance = Activator.CreateInstance(type);
                                if (instance != null)
                                {
                                    MCMSettings.DebugLog("JournalSection: FindPageHandlerInAssemblies — found "
                                        + type.FullName + " for pageId '" + pageId + "'");
                                    return instance;
                                }
                            }
                            catch (Exception ctorEx)
                            {
                                MCMSettings.DebugLog("JournalSection: FindPageHandlerInAssemblies — "
                                    + type.FullName + " found but ctor failed: " + ctorEx.Message);
                            }
                        }
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: assembly scan failed for " + asm.GetName().Name + ": " + ex.ToString()); }
                }

                MCMSettings.DebugLog("JournalSection: FindPageHandlerInAssemblies — no handler found for '"
                    + pageId + "' (scanned " + AppDomain.CurrentDomain.GetAssemblies().Length + " assemblies)");
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: FindPageHandlerInAssemblies error: " + ex.ToString());
            }
            return null;
        }

        /// <summary>
        /// Resolves a game object (Hero, Settlement, Clan, Kingdom, Concept) from type + StringId.
        /// </summary>
        private static object ResolveEncyclopediaTarget(string pageType, string objectId)
        {
            try
            {
                switch (pageType)
                {
                    case "Hero":
                        var hero = Hero.FindFirst(h => h != null && h.StringId == objectId);
                        return hero;
                    case "Settlement":
                        foreach (var s in Settlement.All)
                            if (s.StringId == objectId) return s;
                        return null;
                    case "Clan":
                        foreach (var c in Clan.All)
                            if (c.StringId == objectId) return c;
                        return null;
                    case "Kingdom":
                        foreach (var k in Kingdom.All)
                            if (k.StringId == objectId) return k;
                        return null;
                    default:
                        MCMSettings.DebugLog("JournalSection: unknown page type: " + pageType);
                        return null;
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: ResolveEncyclopediaTarget error: " + ex.ToString());
                return null;
            }
        }

        private static object FindEncyclopediaScreenManager(object topScreen, BindingFlags flags)
        {
            var screenType = topScreen.GetType();
            while (screenType != null && screenType != typeof(object))
            {
                foreach (var field in screenType.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    if (field.Name.Contains("EncyclopediaScreenManager") || field.Name.Contains("Encyclopedia"))
                    {
                        try
                        {
                            var val = field.GetValue(topScreen);
                            if (val != null && val.GetType().Name.Contains("Encyclopedia"))
                                return val;
                        }
                        catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: FindEncyclopediaScreenManager field access failed: " + ex.ToString()); }
                    }
                }
                screenType = screenType.BaseType;
            }
            return null;
        }

        private static object FindNavigatorVM(object encManager, BindingFlags flags)
        {
            var encType = encManager.GetType();
            while (encType != null && encType != typeof(object))
            {
                var navField = encType.GetField("_navigatorDatasource", flags);
                if (navField != null)
                {
                    try { return navField.GetValue(encManager); } catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: FindNavigatorVM field access failed: " + ex.ToString()); }
                    break;
                }
                encType = encType.BaseType;
            }
            return null;
        }

        /// <summary>
        /// NavalDLC bug: the _pages dictionary in EncyclopediaData is missing the "Clan" key,
        /// even though _lists has DefaultEncyclopediaClanPage. This causes SetEncyclopediaPage
        /// to throw KeyNotFoundException for ALL clan pages.
        ///
        /// Fix: install the SetEncyclopediaPage prefix early (before any navigation).
        /// The prefix detects missing keys and auto-fixes them from _lists on the fly.
        /// EncyclopediaData lives on GauntletMapEncyclopediaView._encyclopediaData,
        /// NOT on Campaign.EncyclopediaManager.
        /// </summary>
        private static void EnsureSetEncyclopediaPagePatched()
        {
            // v1.4.5 (War Sails) compat: in Modern mode, the legacy SetEncyclopediaPage method
            // and EncyclopediaData/GauntletMapEncyclopediaView types are restructured. Instead of
            // patching a method-that-doesn't-help, we pre-populate Campaign.EncyclopediaManager._pages
            // with every page from GetEncyclopediaPages() so KeyNotFoundException for Clan/Hero/etc.
            // can't happen. EnsurePagesPopulated is idempotent + cached per Campaign instance, so it's
            // cheap to call every navigation.
            if (EncyclopediaCompat.Mode == EncyclopediaCompat.ApiMode.Modern_1_4)
            {
                EncyclopediaCompat.EnsurePagesPopulated();
                if (!_setEncyclopediaPagePatchApplied)
                {
                    _setEncyclopediaPagePatchApplied = true;
                    MCMSettings.DebugLog("JournalSection: EnsureSetEncyclopediaPagePatched — Modern 1.4.5+ API, "
                        + "skipping legacy patch and populating EncyclopediaManager._pages via GetEncyclopediaPages()");
                }
                return;
            }

            if (_setEncyclopediaPagePatchApplied) return;

            try
            {
                var topScreen = ScreenManager.TopScreen;
                if (topScreen == null) return;
                var reflFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                // Find GauntletMapEncyclopediaView on the screen
                object encManager = FindEncyclopediaScreenManager(topScreen, reflFlags);
                if (encManager == null) return;

                // Get _encyclopediaData from the view
                var encDataField = encManager.GetType().GetField("_encyclopediaData", reflFlags);
                object encData = encDataField?.GetValue(encManager);
                if (encData == null)
                {
                    MCMSettings.DebugLog("JournalSection: EnsureSetEncyclopediaPagePatched — _encyclopediaData not found");
                    return;
                }

                PatchSetEncyclopediaPageIfNeeded(encData.GetType());
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: EnsureSetEncyclopediaPagePatched error: " + ex.ToString());
            }
        }

        private static void FallbackGoToLink(string link)
        {
            try
            {
                var campaign = Campaign.Current;
                if (campaign == null) return;
                var mgr = campaign.EncyclopediaManager;
                if (mgr == null) return;
                var goToLink = mgr.GetType().GetMethod("GoToLink",
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(string) }, null);
                if (goToLink != null)
                {
                    MCMSettings.DebugLog("JournalSection: calling GoToLink fallback");
                    EncyclopediaPageTracker.BeginPageTransition();
                    try
                    {
                        goToLink.Invoke(mgr, new object[] { link });
                    }
                    finally
                    {
                        EncyclopediaPageTracker.EndPageTransition();
                    }
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: GoToLink fallback error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Checks whether a game object can safely be navigated to in the encyclopedia.
        /// Returns false for objects that would cause ExecuteLink to throw (e.g. minor clans,
        /// mercenary clans, or other entities not registered in the encyclopedia dictionary).
        /// </summary>
        public static bool IsEncyclopediaNavigable(string pageType, string objectId)
        {
            try
            {
                object target = ResolveEncyclopediaTarget(pageType, objectId);
                if (target == null) return false;

                // Quick checks for clans
                if (pageType == "Clan")
                {
                    var clan = target as Clan;
                    if (clan != null && (clan.IsMinorFaction || clan.IsBanditFaction || clan.IsEliminated))
                        return false;
                }

                // Verify the object is actually registered in the encyclopedia page
                var encMgr = Campaign.Current?.EncyclopediaManager;
                if (encMgr == null) return false;

                var targetType = target.GetType();
                var getPages = encMgr.GetType().GetMethod("GetPageOf",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getPages == null) return true; // can't verify, allow

                var page = getPages.Invoke(encMgr, new object[] { targetType });
                if (page == null) return false;

                // Check if the object is in the identified objects list
                var getIdentifiedObjs = page.GetType().GetMethod("GetIdentifiedObjects",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getIdentifiedObjs == null)
                    getIdentifiedObjs = page.GetType().GetMethod("GetItems",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getIdentifiedObjs != null)
                {
                    var items = getIdentifiedObjs.Invoke(page, null);
                    if (items is System.Collections.IEnumerable enumerable)
                    {
                        bool found = false;
                        foreach (var item in enumerable)
                        {
                            if (item == target) { found = true; break; }
                        }
                        if (!found)
                        {
                            MCMSettings.DebugLog("IsEncyclopediaNavigable: " + pageType + "/" + objectId
                                + " not in identified objects — blocking navigation");
                            return false;
                        }
                    }
                    else
                    {
                        MCMSettings.DebugLog("IsEncyclopediaNavigable: " + pageType + "/" + objectId
                            + " identified objects not enumerable (method=" + getIdentifiedObjs.Name + ")");
                    }
                }
                else
                {
                    // No item-list method available (e.g. DefaultEncyclopediaClanPage on NavalDLC).
                    // Log all methods on the page type for diagnostics (first time only).
                    if (!_pageMethodsLogged.Contains(page.GetType().Name))
                    {
                        _pageMethodsLogged.Add(page.GetType().Name);
                        var allMethods = page.GetType().GetMethods(
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        var methodsSb = new System.Text.StringBuilder();
                        methodsSb.Append("IsEncyclopediaNavigable: ").Append(page.GetType().Name)
                            .Append(" methods (").Append(allMethods.Length).Append("): ");
                        for (int mi = 0; mi < allMethods.Length; mi++)
                        {
                            if (mi > 0) methodsSb.Append(", ");
                            var m = allMethods[mi];
                            methodsSb.Append(m.Name).Append('(');
                            var mParams = m.GetParameters();
                            for (int pi = 0; pi < mParams.Length; pi++)
                            {
                                if (pi > 0) methodsSb.Append(',');
                                methodsSb.Append(mParams[pi].ParameterType.Name);
                            }
                            methodsSb.Append(')');
                        }
                        MCMSettings.DebugLog(methodsSb.ToString());
                    }

                    // Probe 1: IsValidEncyclopediaItem(Object) — the game's own validation.
                    // If it returns false or throws, the item can't be displayed.
                    bool probed = false;
                    var isValidMethod = page.GetType().GetMethod("IsValidEncyclopediaItem",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (isValidMethod != null && isValidMethod.GetParameters().Length == 1)
                    {
                        try
                        {
                            var result = isValidMethod.Invoke(page, new object[] { target });
                            probed = true;
                            if (result is bool bResult && !bResult)
                            {
                                MCMSettings.DebugLog("IsEncyclopediaNavigable: IsValidEncyclopediaItem returned false for "
                                    + pageType + "/" + objectId + " — blocking navigation");
                                return false;
                            }
                            MCMSettings.DebugLog("IsEncyclopediaNavigable: IsValidEncyclopediaItem=true for "
                                + pageType + "/" + objectId);
                        }
                        catch (Exception probeEx)
                        {
                            MCMSettings.DebugLog("IsEncyclopediaNavigable: IsValidEncyclopediaItem threw for "
                                + pageType + "/" + objectId + ": "
                                + (probeEx.InnerException?.Message ?? probeEx.Message)
                                + " — blocking navigation");
                            return false;
                        }
                    }

                    // Probe 2: GetStringID() with 0 params — may throw for bad entries.
                    // This calls the page's ID generator which can hit missing dictionary keys.
                    if (!probed)
                    {
                        var getStringId = page.GetType().GetMethod("GetStringID",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?? page.GetType().GetMethod("GetStringId",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (getStringId != null && getStringId.GetParameters().Length == 0)
                        {
                            // GetStringID with 0 params returns the page's own ID — not useful
                            // for per-object validation. Skip it.
                        }
                    }

                    // Probe 3: try other 1-param methods as fallback
                    if (!probed)
                    {
                        foreach (string probeName in new[] {
                            "GetViewModelForItem", "CreateViewModel",
                            "GetIdentifiedObjectStringId" })
                        {
                            var probeMethod = page.GetType().GetMethod(probeName,
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (probeMethod != null && probeMethod.GetParameters().Length == 1)
                            {
                                try
                                {
                                    probeMethod.Invoke(page, new object[] { target });
                                    probed = true;
                                    MCMSettings.DebugLog("IsEncyclopediaNavigable: " + probeName
                                        + " succeeded for " + pageType + "/" + objectId);
                                    break;
                                }
                                catch (Exception probeEx)
                                {
                                    MCMSettings.DebugLog("IsEncyclopediaNavigable: " + probeName + " threw for "
                                        + pageType + "/" + objectId + ": "
                                        + (probeEx.InnerException?.Message ?? probeEx.Message)
                                        + " — blocking navigation");
                                    return false;
                                }
                            }
                        }
                    }

                    if (!probed)
                    {
                        MCMSettings.DebugLog("IsEncyclopediaNavigable: " + pageType + "/" + objectId
                            + " no probe method found on " + page.GetType().Name
                            + " — allowing (may crash)");
                    }
                }

                // Check IsFiltered as a secondary signal
                var isFiltered = page.GetType().GetMethod("IsFiltered",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (isFiltered != null)
                {
                    try
                    {
                        bool filtered = (bool)isFiltered.Invoke(page, new object[] { target });
                        if (filtered) return false;
                    }
                    catch (Exception filterEx)
                    {
                        // If IsFiltered itself throws (e.g. KeyNotFoundException),
                        // the page cannot handle this object — block navigation
                        MCMSettings.DebugLog("IsEncyclopediaNavigable: IsFiltered threw for " + pageType + "/" + objectId
                            + ": " + filterEx.Message + " — blocking navigation");
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSectionInjector: IsEncyclopediaNavigable failed: " + ex.ToString());
                return false;
            }
        }

        private static string ColorToHex(Color c)
        {
            int r = (int)(Math.Min(c.Red, 1f) * 255);
            int g = (int)(Math.Min(c.Green, 1f) * 255);
            int b = (int)(Math.Min(c.Blue, 1f) * 255);
            return "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
        }

        private static void SetBrushColor(Brush brush, float r, float g, float b, float a)
        {
            if (brush == null) return;
            try
            {
                var color = new Color(r, g, b, a);
                // Try setting FontColor directly on brush
                var fontColorProp = brush.GetType().GetProperty("FontColor", AllFlags);
                if (fontColorProp != null && fontColorProp.CanWrite)
                {
                    fontColorProp.SetValue(brush, color);
                    return;
                }
                // Set Color on each layer
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
            catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: SetBrushColor failed: " + ex.ToString()); }
        }

        /// <summary>
        /// Finds the brush from the description RichTextWidget (the long hero description text).
        /// This brush renders left-aligned natively, unlike Encyclopedia.SubPage.Info.Text.
        /// </summary>
        private static Brush FindDescriptionBrush(Widget root, int depth = 0)
        {
            if (root == null || depth > 15) return null;
            string typeName = root.GetType().Name;
            if (typeName == "RichTextWidget")
            {
                string text = TryGetText(root);
                // The description is the longest text — look for text > 30 chars
                if (!string.IsNullOrEmpty(text) && text.Length > MinDescriptionTextLength)
                {
                    var brushProp = root.GetType().GetProperty("ReadOnlyBrush", AllFlags)
                                    ?? root.GetType().GetProperty("Brush", AllFlags);
                    if (brushProp != null)
                    {
                        var brush = brushProp.GetValue(root) as Brush;
                        if (brush != null)
                            return brush;
                    }
                }
            }
            for (int i = 0; i < root.ChildCount; i++)
            {
                var result = FindDescriptionBrush(root.GetChild(i), depth + 1);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// Finds a native divider line widget (ImageWidget inside an EncyclopediaDividerButtonWidget)
        /// to use as a reference for sprite cloning.
        /// </summary>
        private static Widget FindNativeDividerLine(Widget root, int depth = 0)
        {
            if (root == null || depth > 15) return null;
            // Look for ImageWidget inside a PlacementListPanel (the line after the section title text)
            if (root.GetType().Name == "ImageWidget" && root is BrushWidget
                && root.ParentWidget != null && root.ParentWidget.Id == "PlacementListPanel")
                return root;
            for (int i = 0; i < root.ChildCount; i++)
            {
                var result = FindNativeDividerLine(root.GetChild(i), depth + 1);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// Finds the brush used by native section header TextWidgets (#Text inside #PlacementListPanel).
        /// </summary>
        private static Brush FindSectionHeaderBrush(Widget root, int depth = 0)
        {
            if (root == null || depth > 15) return null;
            // Look for TextWidget with Id="Text" inside a parent with Id="PlacementListPanel"
            if (root is TextWidget tw && tw.Id == "Text" && tw.ReadOnlyBrush != null
                && tw.ParentWidget != null && tw.ParentWidget.Id == "PlacementListPanel")
                return tw.ReadOnlyBrush;
            for (int i = 0; i < root.ChildCount; i++)
            {
                var result = FindSectionHeaderBrush(root.GetChild(i), depth + 1);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// Finds the brush from the ImageWidget (divider line) inside section headers.
        /// </summary>
        private static Brush FindDividerImageBrush(Widget root, int depth = 0)
        {
            if (root == null || depth > 15) return null;
            // ImageWidget inside #PlacementListPanel is the horizontal line
            // ImageWidget extends BrushWidget, so cast to access ReadOnlyBrush
            string typeName = root.GetType().Name;
            if (typeName == "ImageWidget" && root is BrushWidget bw && bw.ReadOnlyBrush != null
                && root.ParentWidget != null && root.ParentWidget.Id == "PlacementListPanel")
                return bw.ReadOnlyBrush;
            for (int i = 0; i < root.ChildCount; i++)
            {
                var result = FindDividerImageBrush(root.GetChild(i), depth + 1);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// Sets a widget's StackLayout.LayoutMethod to horizontal.
        /// </summary>
        private static void SetHorizontalLayout(Widget panel)
        {
            try
            {
                var layoutProp = panel.GetType().GetProperty("StackLayout", AllFlags);
                if (layoutProp == null) return;
                var layout = layoutProp.GetValue(panel);
                if (layout == null) return;

                var methodProp = layout.GetType().GetProperty("LayoutMethod", AllFlags);
                if (methodProp == null || !methodProp.CanWrite) return;

                var enumType = methodProp.PropertyType;
                foreach (var ev in Enum.GetValues(enumType))
                {
                    if (ev.ToString().Contains("HorizontalLeftToRight"))
                    {
                        methodProp.SetValue(layout, ev);
                        return;
                    }
                }
                // Fallback: index 0 (typically HorizontalLeftToRight)
                var vals = Enum.GetValues(enumType);
                if (vals.Length > 0)
                    methodProp.SetValue(layout, vals.GetValue(0));
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: horizontal layout set failed: " + ex.ToString());
            }
        }

        public static void RemoveOldWidgets()
        {
            foreach (var widget in _injectedWidgets)
            {
                try
                {
                    if (widget == null) continue;
                    // If this is a reused native ListPanel, clear its children instead of removing it
                    if (widget.Id == "EditableEncyclopediaJournal" && widget.ParentWidget != null)
                    {
                        // Check if this is a native panel we reused (it has siblings)
                        var parent = widget.ParentWidget;
                        bool isReused = false;
                        for (int i = 0; i < parent.ChildCount; i++)
                        {
                            if (parent.GetChild(i) == widget && i < parent.ChildCount - 1)
                            {
                                // It's not the last child — it was likely a native panel
                                isReused = true;
                                break;
                            }
                        }
                        if (isReused || widget.GetType().Name == "ListPanel" || widget.GetType().Name == "NavigatableListPanel")
                        {
                            // Clear children we added, don't remove the panel itself
                            while (widget.ChildCount > 0)
                                widget.RemoveChild(widget.GetChild(0));
                            widget.Id = null;
                            continue;
                        }
                    }
                    if (widget.ParentWidget != null)
                        widget.ParentWidget.RemoveChild(widget);
                }
                catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: RemoveOldWidgets cleanup failed: " + ex.ToString()); }
            }
            _injectedWidgets.Clear();
        }

        // ─────────── Right Sub-Panel Discovery ───────────

        /// <summary>
        /// Finds the right sub-panel of the encyclopedia page (where "Clan" banner is shown).
        /// This is the narrower column on the far right within the Info section area.
        /// Strategy: find "Clan" text widget, walk up to the column container.
        /// </summary>
        /// <summary>
        /// Finds the right-side vertical ListPanel that contains the LastSeen widget
        /// and the Clan banner widget. Returns both the ListPanel (to insert into)
        /// and the index after the Clan widget (to position the journal below the Clan section).
        /// </summary>

        /// <summary>
        /// Finds the main scrollable content ListPanel inside #RightSideList.
        /// This is child[0]'s inner ListPanel — the one with ~10 children
        /// (description, Owner, Notable Characters, Villages, etc.).
        /// Adding the Chronicle here places it below Villages and inside the scroll flow.
        /// The sidebar column (child[1]) is NOT scrollable and causes floating.
        /// </summary>
        private static Widget FindScrollableContentPanel(Widget root)
        {
            Widget rightSideList = FindWidgetById(root, "RightSideList", 0);
            if (rightSideList != null && rightSideList.ChildCount >= 1)
            {
                Widget mainWrapper = rightSideList.GetChild(0); // child[0]: wrapper Widget
                MCMSettings.DebugLog("JournalSection: RightSideList child[0] type=" + mainWrapper.GetType().Name
                    + " children=" + mainWrapper.ChildCount);

                // The inner ListPanel (with ~10 children) is the scrollable content area
                // It's usually the first (and only) child of the wrapper Widget
                for (int i = 0; i < mainWrapper.ChildCount; i++)
                {
                    Widget child = mainWrapper.GetChild(i);
                    string typeName = child.GetType().Name;
                    if ((typeName == "ListPanel" || typeName == "NavigatableListPanel") && child.ChildCount >= 3)
                    {
                        MCMSettings.DebugLog("JournalSection: found main content ListPanel children=" + child.ChildCount);
                        return child;
                    }
                }

                // Fallback: if no ListPanel with 3+ children, try the first ListPanel
                for (int i = 0; i < mainWrapper.ChildCount; i++)
                {
                    Widget child = mainWrapper.GetChild(i);
                    string typeName = child.GetType().Name;
                    if (typeName == "ListPanel" || typeName == "NavigatableListPanel")
                    {
                        MCMSettings.DebugLog("JournalSection: using first ListPanel in wrapper children=" + child.ChildCount);
                        return child;
                    }
                }

                // Last resort: use the wrapper itself
                MCMSettings.DebugLog("JournalSection: no inner ListPanel found, using wrapper directly");
                return mainWrapper;
            }

            // Strategy 2: If no #RightSideList, try #RightSideRect
            Widget rightSideRect = FindWidgetById(root, "RightSideRect", 0);
            if (rightSideRect != null)
            {
                MCMSettings.DebugLog("JournalSection: trying #RightSideRect children=" + rightSideRect.ChildCount);
                return FindDeepestContentListPanel(rightSideRect);
            }

            return null;
        }

        /// <summary>
        /// Recursively finds the ListPanel with the most children (the main content panel)
        /// within a widget subtree. Used as a fallback for Naval DLC layouts.
        /// </summary>
        private static Widget FindDeepestContentListPanel(Widget root)
        {
            Widget best = null;
            int bestChildren = 0;
            FindDeepestContentListPanelRecurse(root, ref best, ref bestChildren, 0);
            return best;
        }

        private static void FindDeepestContentListPanelRecurse(Widget node, ref Widget best, ref int bestChildren, int depth)
        {
            if (node == null || depth > 10) return;
            string typeName = node.GetType().Name;
            if ((typeName == "ListPanel" || typeName == "NavigatableListPanel") && node.ChildCount > bestChildren)
            {
                best = node;
                bestChildren = node.ChildCount;
            }
            for (int i = 0; i < node.ChildCount; i++)
                FindDeepestContentListPanelRecurse(node.GetChild(i), ref best, ref bestChildren, depth + 1);
        }

        private static Widget FindRightSubPanel(Widget root)
        {
            // The right scrollable area structure is:
            //   ScrollablePanel #RightSideScrollablePanel
            //     Widget #RightSideRect children=1
            //       ListPanel #RightSideList children=2
            //         Widget children=1                    ← child[0]: wrapper with main content
            //           ListPanel children=10+             ← description, Owner, Notable Characters, Villages...
            //         ListPanel children=3                 ← child[1]: sidebar / "Never seen before" / history
            // We want child[1] — the sidebar panel — for Chronicle placement.

            // Strategy 1: Find #RightSideList → child[1] (the sidebar panel)
            Widget rightSideList = FindWidgetById(root, "RightSideList", 0);
            if (rightSideList != null)
            {
                MCMSettings.DebugLog("JournalSection: found #RightSideList children=" + rightSideList.ChildCount);
                if (rightSideList.ChildCount >= 2)
                {
                    Widget sidebar = rightSideList.GetChild(1);
                    MCMSettings.DebugLog("JournalSection: targeting sidebar child[1] type=" + sidebar.GetType().Name + " children=" + sidebar.ChildCount);
                    return sidebar;
                }
                // Fallback: if only 1 child, use it
                if (rightSideList.ChildCount >= 1)
                {
                    MCMSettings.DebugLog("JournalSection: only 1 child in RightSideList, using child[0] as fallback");
                    return rightSideList.GetChild(0);
                }
                return rightSideList;
            }

            // Strategy 2: Find #RightSideRect, drill into its first ListPanel
            Widget rightSideRect = FindWidgetById(root, "RightSideRect", 0);
            if (rightSideRect != null)
            {
                MCMSettings.DebugLog("JournalSection: found #RightSideRect children=" + rightSideRect.ChildCount);
                for (int i = 0; i < rightSideRect.ChildCount; i++)
                {
                    Widget child = rightSideRect.GetChild(i);
                    string typeName = child.GetType().Name;
                    if (typeName == "ListPanel" || typeName == "NavigatableListPanel")
                    {
                        MCMSettings.DebugLog("JournalSection: using first ListPanel in RightSideRect children=" + child.ChildCount);
                        return child;
                    }
                }
            }

            return null;
        }

        private static Widget FindWidgetById(Widget root, string id, int depth)
        {
            if (root == null || depth > 20) return null;
            if (root.Id == id) return root;
            for (int i = 0; i < root.ChildCount; i++)
            {
                var result = FindWidgetById(root.GetChild(i), id, depth + 1);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// Finds the main content ListPanel (child[0]'s inner ListPanel in #RightSideList)
        /// which contains the native EncyclopediaDividerButtonWidget sections (Owner, Notable Characters, etc.).
        /// Used as sectionSource for ExtractNativeSectionParts.
        /// </summary>
        private static Widget FindMainContentPanel(Widget root)
        {
            Widget rightSideList = FindWidgetById(root, "RightSideList", 0);
            if (rightSideList != null && rightSideList.ChildCount >= 1)
            {
                Widget wrapper = rightSideList.GetChild(0);
                for (int i = 0; i < wrapper.ChildCount; i++)
                {
                    Widget child = wrapper.GetChild(i);
                    string typeName = child.GetType().Name;
                    if (typeName == "ListPanel" || typeName == "NavigatableListPanel")
                    {
                        MCMSettings.DebugLog("JournalSection: found main content ListPanel children=" + child.ChildCount);
                        return child;
                    }
                }
                return wrapper;
            }
            return null;
        }

        /// <summary>
        /// Finds a text widget with short exact text (like "Clan", not "a rising new clan...").
        /// </summary>
        private static Widget FindShortLabelWidget(Widget parent, string text, int depth)
        {
            if (parent == null || depth > 20) return null;

            string widgetText = TryGetText(parent);
            if (!string.IsNullOrEmpty(widgetText)
                && widgetText.Length <= 20
                && widgetText.Trim().Equals(text, StringComparison.OrdinalIgnoreCase)
                && parent.Id != "EditableEncyclopediaJournal"
                && parent.Id != "JournalHeader"
                && parent.Id != "JournalEntry")
                return parent;

            for (int i = 0; i < parent.ChildCount; i++)
            {
                var result = FindShortLabelWidget(parent.GetChild(i), text, depth + 1);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// Walks up from a widget to find the nearest ListPanel ancestor.
        /// </summary>
        private static Widget FindAncestorListPanel(Widget widget, Widget stopAt)
        {
            Widget current = widget.ParentWidget;
            for (int depth = 0; depth < 15 && current != null && current != stopAt; depth++)
            {
                string typeName = current.GetType().Name;
                if (typeName == "ListPanel" || typeName == "NavigatableListPanel")
                    return current;
                current = current.ParentWidget;
            }
            return null;
        }

        /// <summary>
        /// Finds a widget by its type name (e.g., "ImageIdentifierWidget").
        /// </summary>
        private static Widget FindWidgetByType(Widget root, string typeName, int depth)
        {
            if (root == null || depth > 20) return null;
            if (root.GetType().Name == typeName) return root;
            for (int i = 0; i < root.ChildCount; i++)
            {
                var result = FindWidgetByType(root.GetChild(i), typeName, depth + 1);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// Finds a ListPanel with HorizontalAlignment.Right.
        /// </summary>
        private static Widget FindRightAlignedListPanel(Widget root, int depth)
        {
            if (root == null || depth > 12) return null;
            string typeName = root.GetType().Name;
            if ((typeName == "ListPanel" || typeName == "NavigatableListPanel")
                && root.HorizontalAlignment == HorizontalAlignment.Right
                && root.ChildCount >= 1)
                return root;
            for (int i = 0; i < root.ChildCount; i++)
            {
                var result = FindRightAlignedListPanel(root.GetChild(i), depth + 1);
                if (result != null) return result;
            }
            return null;
        }

        // ─────────── Debug Tree Dump ───────────

        private static void DumpWidgetTree(Widget widget, int depth, int maxDepth)
        {
            if (widget == null || depth > maxDepth) return;

            string indent = new string(' ', depth * 2);
            string typeName = widget.GetType().Name;
            string id = widget.Id ?? "";
            string text = TryGetText(widget);
            string textSnippet = "";
            if (!string.IsNullOrEmpty(text))
            {
                textSnippet = text.Length > DebugTextSnippetLength ? text.Substring(0, DebugTextSnippetLength) + "..." : text;
                textSnippet = " text='" + textSnippet + "'";
            }

            string align = "";
            if (widget.HorizontalAlignment != HorizontalAlignment.Left)
                align = " hAlign=" + widget.HorizontalAlignment;

            MCMSettings.DebugLog("JournalTree: " + indent + typeName
                + (string.IsNullOrEmpty(id) ? "" : " #" + id)
                + " children=" + widget.ChildCount
                + textSnippet + align);

            for (int i = 0; i < widget.ChildCount; i++)
                DumpWidgetTree(widget.GetChild(i), depth + 1, maxDepth);
        }

        // ─────────── Layer/Widget Helpers ───────────

        private static GauntletLayer FindEncyclopediaLayer(ScreenBase topScreen)
        {
            object encManager = EncyclopediaPageTracker.EncyclopediaManagerRef;
            if (encManager != null)
            {
                var encType = encManager.GetType();
                while (encType != null && encType != typeof(object))
                {
                    var layerField = encType.GetField("<Layer>k__BackingField", AllFlags);
                    if (layerField != null)
                    {
                        var layerVal = layerField.GetValue(encManager);
                        if (layerVal is GauntletLayer gl)
                            return gl;
                        break;
                    }
                    encType = encType.BaseType;
                }
            }

            GauntletLayer bestLayer = null;
            int bestDesc = 0;

            var layers = topScreen.Layers;
            for (int i = 0; i < layers.Count; i++)
            {
                if (!(layers[i] is GauntletLayer gl2)) continue;
                Widget root = GetLayerRootWidget(gl2);
                if (root == null) continue;
                int desc = CountDescendants(root);
                if (desc < MinLayerDescendants || desc > MaxLayerDescendants) continue;
                // Only consider layers with EncyclopediaDividerButtonWidget — prevents
                // picking Clan/Party/Inventory screen panels instead of the encyclopedia.
                if (!LoreSectionInjector.ContainsWidgetType(root, "EncyclopediaDividerButtonWidget")) continue;
                if (desc > bestDesc)
                {
                    bestDesc = desc;
                    bestLayer = gl2;
                }
            }

            return bestLayer;
        }

        private static Widget GetLayerRootWidget(GauntletLayer layer)
        {
            try
            {
                var type = typeof(GauntletLayer);
                foreach (var fieldName in new[] { "_gauntletUIContext", "_uiContext", "_context" })
                {
                    var field = type.GetField(fieldName, AllFlags);
                    if (field != null)
                    {
                        var uiCtx = field.GetValue(layer) as UIContext;
                        if (uiCtx?.EventManager?.Root != null)
                            return uiCtx.EventManager.Root;
                    }
                }
                foreach (var propName in new[] { "UIContext", "_gauntletUIContext" })
                {
                    var prop = type.GetProperty(propName, AllFlags);
                    if (prop != null)
                    {
                        var uiCtx = prop.GetValue(layer) as UIContext;
                        if (uiCtx?.EventManager?.Root != null)
                            return uiCtx.EventManager.Root;
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: GetLayerRootWidget failed: " + ex.ToString()); }
            return null;
        }

        private static Widget FindLargestChild(Widget parent)
        {
            Widget best = null;
            int bestDesc = 0;
            for (int i = 0; i < parent.ChildCount; i++)
            {
                Widget child = parent.GetChild(i);
                int desc = CountDescendants(child);
                if (desc > bestDesc)
                {
                    bestDesc = desc;
                    best = child;
                }
            }
            return best;
        }

        private static int CountDescendants(Widget widget, int depth = 0)
        {
            if (widget == null || depth > 20) return 0;
            int count = 0;
            for (int i = 0; i < widget.ChildCount; i++)
                count += 1 + CountDescendants(widget.GetChild(i), depth + 1);
            return count;
        }

        private static Widget FindWidgetByTextContent(Widget parent, string substring, int depth = 0)
        {
            if (parent == null || depth > 20) return null;

            string text = TryGetText(parent);
            if (!string.IsNullOrEmpty(text)
                && text.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0
                && parent.Id != "EditableEncyclopediaJournal"
                && parent.Id != "JournalHeader"
                && parent.Id != "JournalEntry")
                return parent;

            for (int i = 0; i < parent.ChildCount; i++)
            {
                var result = FindWidgetByTextContent(parent.GetChild(i), substring, depth + 1);
                if (result != null) return result;
            }

            return null;
        }

        private static string TryGetText(Widget widget)
        {
            if (widget is TextWidget tw) return tw.Text;
            try
            {
                var textProp = widget.GetType().GetProperty("Text",
                    BindingFlags.Instance | BindingFlags.Public);
                if (textProp != null && textProp.PropertyType == typeof(string))
                    return textProp.GetValue(widget) as string;
            }
            catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: TryGetText reflection failed: " + ex.ToString()); }
            return null;
        }

        /// <summary>
        /// Finds a brush by name in the widget tree (e.g., "Encyclopedia.SubPage.Info.Text").
        /// Matches both exact name and "(Clone)" variants.
        /// </summary>
        private static Brush FindBrushByName(Widget root, string brushName, int depth = 0)
        {
            if (root == null || depth > 15) return null;
            if (root is BrushWidget bw && bw.ReadOnlyBrush != null)
            {
                string name = bw.ReadOnlyBrush.Name;
                if (name == brushName || name == brushName + "(Clone)")
                    return bw.ReadOnlyBrush;
            }
            if (root is TextWidget tw && tw.ReadOnlyBrush != null)
            {
                string name = tw.ReadOnlyBrush.Name;
                if (name == brushName || name == brushName + "(Clone)")
                    return tw.ReadOnlyBrush;
            }
            for (int i = 0; i < root.ChildCount; i++)
            {
                var result = FindBrushByName(root.GetChild(i), brushName, depth + 1);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// Finds any text-bearing brush in the widget tree as a fallback.
        /// Prefers brushes with "Text" or "Encyclopedia" in the name.
        /// </summary>
        private static Brush FindAnyTextBrush(Widget root, int depth = 0)
        {
            if (root == null || depth > 10) return null;
            if (root is TextWidget tw && tw.ReadOnlyBrush != null)
                return tw.ReadOnlyBrush;
            if (root is BrushWidget bw && bw.ReadOnlyBrush != null)
            {
                string name = bw.ReadOnlyBrush.Name ?? "";
                if (name.Contains("Text") || name.Contains("Encyclopedia"))
                    return bw.ReadOnlyBrush;
            }
            for (int i = 0; i < root.ChildCount; i++)
            {
                var result = FindAnyTextBrush(root.GetChild(i), depth + 1);
                if (result != null) return result;
            }
            return null;
        }

        private static void SetBrush(Widget widget, Brush brush)
        {
            if (widget is BrushWidget bw)
                bw.Brush = brush;
            else if (widget is TextWidget tw)
                tw.Brush = brush;
        }

        /// <summary>
        /// Creates a ListPanel widget via reflection (it may not be directly accessible).
        /// ListPanel has built-in StackLayout support for vertical child stacking.
        /// </summary>
        private static Widget TryCreateListPanel(UIContext uiContext)
        {
            try
            {
                Type lpType = typeof(Widget).Assembly.GetType("TaleWorlds.GauntletUI.BaseTypes.ListPanel")
                              ?? typeof(Widget).Assembly.GetType("TaleWorlds.GauntletUI.ListPanel");
                if (lpType != null)
                {
                    var ctor = lpType.GetConstructor(new[] { typeof(UIContext) });
                    if (ctor != null)
                        return ctor.Invoke(new object[] { uiContext }) as Widget;
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: TryCreateListPanel failed: " + ex.ToString()); }
            return null;
        }

        /// <summary>
        /// Creates a RichTextWidget via reflection (matching native "Never seen before" widget type).
        /// </summary>
        public static Widget TryCreateRichTextWidget(UIContext uiContext, string text)
        {
            try
            {
                var richType = typeof(TextWidget).Assembly.GetType("TaleWorlds.GauntletUI.BaseTypes.RichTextWidget");
                if (richType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        richType = asm.GetType("TaleWorlds.GauntletUI.BaseTypes.RichTextWidget");
                        if (richType != null) break;
                    }
                }
                if (richType != null)
                {
                    var ctor = richType.GetConstructor(new[] { typeof(UIContext) });
                    if (ctor != null)
                    {
                        var widget = ctor.Invoke(new object[] { uiContext }) as Widget;
                        if (widget != null)
                        {
                            var textProp = richType.GetProperty("Text", AllFlags);
                            if (textProp != null && textProp.CanWrite)
                                textProp.SetValue(widget, text);
                            return widget;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: RichTextWidget creation failed: " + ex.ToString());
            }
            return null;
        }

        /// <summary>
        /// Modifies a Brush's text horizontal alignment across all its styles and layers.
        /// This is necessary because the brush renderer overrides TextWidget._text._horizontalAlignment
        /// with the brush's own text alignment on every render frame.
        /// </summary>
        private static void SetBrushTextAlignment(Brush brush, string alignmentName)
        {
            if (brush == null) return;
            try
            {
                // Dump all properties on Brush that contain "Align" or "Horizontal" or "Text" for diagnostics
                var brushType = brush.GetType();
                var allProps = brushType.GetProperties(AllFlags);
                var alignPropsSb = new System.Text.StringBuilder();
                alignPropsSb.Append("JournalSection: Brush type=").Append(brushType.Name).Append(" relevant props: ");
                bool firstProp = true;
                foreach (var p in allProps)
                {
                    string pName = p.Name;
                    if (pName.Contains("Align") || pName.Contains("Horizontal") || pName.Contains("Text")
                        || pName.Contains("Font") || pName.Contains("Style") || pName.Contains("Layer"))
                    {
                        if (!firstProp) alignPropsSb.Append(", ");
                        alignPropsSb.Append(pName).Append('(').Append(p.PropertyType.Name)
                            .Append(',').Append(p.CanWrite ? "rw" : "ro").Append(')');
                        firstProp = false;
                    }
                }
                MCMSettings.DebugLog(alignPropsSb.ToString());

                // Try all known property name patterns for text horizontal alignment
                string[] propNames = {
                    "TextHorizontalAlignment", "FontHorizontalAlignment",
                    "HorizontalAlignment", "TextAlignment"
                };

                // Strategy 1: Direct property on Brush
                foreach (string propName in propNames)
                {
                    if (TrySetEnumProperty(brush, propName, alignmentName))
                    {
                        MCMSettings.DebugLog("JournalSection: set Brush." + propName + "=" + alignmentName);
                        return;
                    }
                }

                // Strategy 2: DefaultStyle
                var defStyleProp = brushType.GetProperty("DefaultStyle", AllFlags);
                if (defStyleProp != null)
                {
                    var defStyle = defStyleProp.GetValue(brush);
                    if (defStyle != null)
                    {
                        MCMSettings.DebugLog("JournalSection: DefaultStyle type=" + defStyle.GetType().Name);
                        foreach (string propName in propNames)
                        {
                            if (TrySetEnumProperty(defStyle, propName, alignmentName))
                            {
                                MCMSettings.DebugLog("JournalSection: set DefaultStyle." + propName + "=" + alignmentName);
                                return;
                            }
                        }
                    }
                }

                // Strategy 3: Styles collection
                foreach (string collName in new[] { "Styles", "StyleLayers", "_styles" })
                {
                    var collProp = brushType.GetProperty(collName, AllFlags);
                    if (collProp == null) continue;
                    var coll = collProp.GetValue(brush) as System.Collections.IEnumerable;
                    if (coll == null) continue;
                    int count = 0;
                    foreach (var style in coll)
                    {
                        count++;
                        foreach (string propName in propNames)
                            TrySetEnumProperty(style, propName, alignmentName);
                    }
                    if (count > 0)
                    {
                        MCMSettings.DebugLog("JournalSection: set " + count + " styles via " + collName);
                        return;
                    }
                }

                // Strategy 4: Layers collection
                var layersProp = brushType.GetProperty("Layers", AllFlags);
                if (layersProp != null)
                {
                    var layers = layersProp.GetValue(brush) as System.Collections.IEnumerable;
                    if (layers != null)
                    {
                        int count = 0;
                        foreach (var layer in layers)
                        {
                            count++;
                            foreach (string propName in propNames)
                                TrySetEnumProperty(layer, propName, alignmentName);
                        }
                        if (count > 0)
                        {
                            MCMSettings.DebugLog("JournalSection: set " + count + " layers alignment");
                            return;
                        }
                    }
                }

                MCMSettings.DebugLog("JournalSection: SetBrushTextAlignment — no matching property found");
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: SetBrushTextAlignment failed: " + ex.ToString());
            }
        }

        /// <summary>
        /// Tries to set an enum property by name on an object, matching the enum value by name.
        /// Returns true if the property was found and set.
        /// </summary>
        private static bool TrySetEnumProperty(object obj, string propertyName, string enumValueName)
        {
            try
            {
                var prop = obj.GetType().GetProperty(propertyName, AllFlags);
                if (prop == null || !prop.CanWrite || !prop.PropertyType.IsEnum) return false;
                foreach (var ev in Enum.GetValues(prop.PropertyType))
                {
                    if (ev.ToString() == enumValueName)
                    {
                        prop.SetValue(obj, ev);
                        return true;
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: TrySetEnumProperty failed: " + ex.ToString()); }
            return false;
        }

        /// <summary>
        /// Initial one-time set of center alignment. The Harmony patch on TextWidget.OnRender
        /// (in LoreSectionInjector) maintains this every frame for widgets with Id="JournalHeader".
        /// </summary>
        private static void ForceTextAlignCenter(Widget widget)
        {
            if (widget == null) return;
            try
            {
                // Use the cached center value from LoreSectionInjector's Harmony setup
                if (LoreSectionInjector._centerAlignValue != null)
                {
                    var textField = widget.GetType().GetField("_text", AllFlags);
                    if (textField != null)
                    {
                        var textObj = textField.GetValue(widget);
                        if (textObj != null)
                        {
                            var hAlignField = textObj.GetType().GetField("_horizontalAlignment", AllFlags)
                                              ?? textObj.GetType().GetField("<HorizontalAlignment>k__BackingField", AllFlags);
                            if (hAlignField != null)
                            {
                                hAlignField.SetValue(textObj, LoreSectionInjector._centerAlignValue);
                                MCMSettings.DebugLog("JournalSection: initial center alignment set");
                                return;
                            }
                        }
                    }
                }
                MCMSettings.DebugLog("JournalSection: ForceTextAlignCenter — center value not available, relying on Harmony patch");
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: ForceTextAlignCenter failed: " + ex.ToString());
            }
        }

        /// <summary>
        /// Copies the Sprite property from one Widget to another via reflection.
        /// Widget.Sprite is set via XML and may not be publicly accessible.
        /// </summary>
        private static bool TryCopyWidgetSprite(Widget source, Widget target)
        {
            try
            {
                // Try public property first
                var spriteProp = typeof(Widget).GetProperty("Sprite", AllFlags);
                if (spriteProp != null && spriteProp.CanRead && spriteProp.CanWrite)
                {
                    var sprite = spriteProp.GetValue(source);
                    if (sprite != null)
                    {
                        spriteProp.SetValue(target, sprite);
                        MCMSettings.DebugLog("JournalSection: copied Sprite from native line via property");
                        return true;
                    }
                }
                // Try field
                var spriteField = typeof(Widget).GetField("_sprite", AllFlags)
                                  ?? typeof(Widget).GetField("Sprite", AllFlags);
                if (spriteField != null)
                {
                    var sprite = spriteField.GetValue(source);
                    if (sprite != null)
                    {
                        spriteField.SetValue(target, sprite);
                        MCMSettings.DebugLog("JournalSection: copied Sprite from native line via field");
                        return true;
                    }
                }
                MCMSettings.DebugLog("JournalSection: native line Widget has no Sprite set");
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: TryCopyWidgetSprite failed: " + ex.ToString());
            }
            return false;
        }

        /// <summary>
        /// Loads a sprite by name from UIContext.SpriteData and sets it on a Widget.
        /// </summary>
        private static bool TrySetSpriteByName(UIContext uiContext, Widget target, string spriteName)
        {
            try
            {
                // Get UIContext.SpriteData
                var sdProp = uiContext.GetType().GetProperty("SpriteData", AllFlags);
                if (sdProp == null) return false;
                var spriteData = sdProp.GetValue(uiContext);
                if (spriteData == null) return false;

                // Try GetSprite(string name) or similar
                object sprite = null;
                var getSprite = spriteData.GetType().GetMethod("GetSprite",
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(string) }, null);
                if (getSprite != null)
                    sprite = getSprite.Invoke(spriteData, new object[] { spriteName });

                if (sprite == null)
                {
                    // Try indexer or TryGetValue
                    var indexer = spriteData.GetType().GetProperty("Item",
                        BindingFlags.Instance | BindingFlags.Public,
                        null, null, new[] { typeof(string) }, null);
                    if (indexer != null)
                        sprite = indexer.GetValue(spriteData, new object[] { spriteName });
                }

                if (sprite == null) return false;

                // Set it on the target Widget
                var spriteProp = typeof(Widget).GetProperty("Sprite", AllFlags);
                if (spriteProp != null && spriteProp.CanWrite)
                {
                    spriteProp.SetValue(target, sprite);
                    return true;
                }
                var spriteField = typeof(Widget).GetField("_sprite", AllFlags);
                if (spriteField != null)
                {
                    spriteField.SetValue(target, sprite);
                    return true;
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: TrySetSpriteByName('" + spriteName + "') failed: " + ex.ToString());
            }
            return false;
        }

        /// <summary>
        /// Copies Color/Tint from one Widget to another.
        /// </summary>
        /// <summary>
        /// Sets HorizontalFlip on a Widget via reflection.
        /// </summary>
        private static void TrySetHorizontalFlip(Widget widget, bool flip)
        {
            try
            {
                var prop = widget.GetType().GetProperty("HorizontalFlip", AllFlags);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(widget, flip);
                    MCMSettings.DebugLog("JournalSection: set HorizontalFlip=" + flip);
                    return;
                }
                // Try field
                var field = widget.GetType().GetField("_horizontalFlip", AllFlags)
                            ?? widget.GetType().GetField("HorizontalFlip", AllFlags);
                if (field != null)
                {
                    field.SetValue(widget, flip);
                    MCMSettings.DebugLog("JournalSection: set HorizontalFlip field=" + flip);
                    return;
                }
                // Try IsHorizontalFlipEnabled
                var altProp = widget.GetType().GetProperty("IsHorizontalFlipEnabled", AllFlags);
                if (altProp != null && altProp.CanWrite)
                {
                    altProp.SetValue(widget, flip);
                    MCMSettings.DebugLog("JournalSection: set IsHorizontalFlipEnabled=" + flip);
                    return;
                }
                MCMSettings.DebugLog("JournalSection: HorizontalFlip property not found on " + widget.GetType().Name);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("JournalSection: TrySetHorizontalFlip failed: " + ex.ToString());
            }
        }

        private static void TryCopyWidgetColor(Widget source, Widget target)
        {
            try
            {
                var colorProp = typeof(Widget).GetProperty("Color", AllFlags);
                if (colorProp != null && colorProp.CanRead && colorProp.CanWrite)
                {
                    var color = colorProp.GetValue(source);
                    if (color != null)
                        colorProp.SetValue(target, color);
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: TryCopyWidgetColor failed: " + ex.ToString()); }
        }

        private static void ScheduleRetryIfNeeded(string debugMessage)
        {
            if (_retryCount < MaxRetries)
            {
                _retryCount++;
                MCMSettings.DebugLog(debugMessage + ", scheduling retry #" + _retryCount);
                // Dispose previous timer first so rapid navigation doesn't leak handles
                // and double-fire _retryPending (Theme 2: round 2 finding JC-2).
                DisposeRetryTimer();
                _retryTimer = new System.Threading.Timer(_ =>
                {
                    _retryPending = true;
                }, null, RetryDelayMs, System.Threading.Timeout.Infinite);
            }
            else
            {
                MCMSettings.DebugLog(debugMessage + " after " + MaxRetries + " retries");
            }
        }

        private static void DisposeRetryTimer()
        {
            var t = _retryTimer;
            _retryTimer = null;
            if (t != null)
                try { t.Dispose(); } catch (Exception ex) { MCMSettings.DebugLog("JournalSectionInjector: DisposeRetryTimer failed: " + ex.ToString()); }
        }

    }
}
