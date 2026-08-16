using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.ScreenSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Injects a "Tags: Enemy Ally Target" display into the encyclopedia page left panel.
    /// Each tag is rendered as a colored badge with category-specific colors.
    /// Uses "Tags:" in golden brush and individual colored TextWidgets per tag.
    /// </summary>
    internal static class TagWidgetInjector
    {
        private static Widget _tagWidget;       // container row (ListPanel) or single fallback widget
        private static Widget _tagLabelWidget;   // "Tags: " TextWidget with label brush
        private static Widget _tagValueWidget;   // tag values TextWidget with content brush
        private static Widget _tagNotesWidget;   // separate row for tag notes (below tags)
        private static bool _widgetReady;

        // _pendingTags/_pendingVisible volatile so main thread sees latest payload
        // after observing _retryPending=true (Theme 1 fix: round 2 finding TC-1).
        private static volatile string _pendingTags;
        private static volatile bool _pendingVisible;
        private static int _retryCount;
        private static System.Threading.Timer _retryTimer;
        private static volatile bool _retryPending;
        private static volatile bool _clearPending;
        private const int MaxRetries = 15;
        private const int BaseRetryDelayMs = 100;
        private static bool _injectionFailed;

        private static readonly BindingFlags AllFlags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static PropertyInfo _cachedTextProp;
        private static Type _cachedTextPropType;

        private static PropertyInfo _cachedSetTextProp;
        private static Type _cachedSetTextPropType;

        private static PropertyInfo _cachedBrushFactoryProp;
        private static Type _cachedBrushFactoryPropType;

        private static MethodInfo _cachedGetBrushMethod;
        private static Type _cachedGetBrushMethodType;

        private static PropertyInfo _cachedStackLayoutProp;
        private static Type _cachedStackLayoutPropType;

        private static PropertyInfo _cachedLayoutMethodProp;
        private static Type _cachedLayoutMethodPropType;

        private static readonly Dictionary<Type, PropertyInfo> _cachedLayerColorProps = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> _cachedLayerFontSizeProps = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<string, PropertyInfo> _cachedBrushLayersProps = new Dictionary<string, PropertyInfo>();
        private static readonly Dictionary<string, FieldInfo> _cachedFieldLookups = new Dictionary<string, FieldInfo>();
        private static readonly Dictionary<string, PropertyInfo> _cachedPropLookups = new Dictionary<string, PropertyInfo>();

        private static PropertyInfo _cachedNoteBrushProp;
        private static Type _cachedNoteBrushPropType;

        private static PropertyInfo _cachedFontColorPropDirect;
        private static Type _cachedFontColorPropDirectType;

        private static PropertyInfo _cachedBrushFontSizePropDirect;
        private static Type _cachedBrushFontSizePropDirectType;

        private static readonly Dictionary<string, FieldInfo> _cachedGauntletLayerFields = new Dictionary<string, FieldInfo>();
        private static readonly Dictionary<string, PropertyInfo> _cachedGauntletLayerProps = new Dictionary<string, PropertyInfo>();

        private static readonly Dictionary<string, FieldInfo> _cachedEncManagerFields = new Dictionary<string, FieldInfo>();
        private static readonly Dictionary<string, PropertyInfo> _cachedEncManagerProps = new Dictionary<string, PropertyInfo>();

        public static void InjectOrUpdate(string tags)
        {
            DisposeRetryTimer();
            _clearPending = false;
            _retryPending = false;
            _retryCount = 0;
            _injectionFailed = false;

            bool hasTags = !string.IsNullOrEmpty(tags);
            _pendingTags = tags;
            _pendingVisible = hasTags;

            DoInjectOrUpdate(tags, hasTags);
        }

        public static void Clear()
        {
            DisposeRetryTimer();
            _retryPending = false;
            RemoveOldWidget();
        }

        public static void ScheduleClear()
        {
            DisposeRetryTimer();
            _retryPending = false;
            _clearPending = true;
        }

        /// <summary>
        /// Must be called from the main game thread (e.g., OnApplicationTick).
        /// Processes deferred retry and clear operations.
        /// </summary>
        public static void TickMainThread()
        {
            if (_clearPending)
            {
                _clearPending = false;
                RemoveOldWidget();
            }
            if (_retryPending)
            {
                _retryPending = false;
                // Abort retry if encyclopedia was closed (e.g. failed navigation, user closed it)
                if (!EncyclopediaPageTracker.IsEncyclopediaOpen())
                {
                    _retryCount = MaxRetries;
                    DisposeRetryTimer();
                    return;
                }
                DoInjectOrUpdate(_pendingTags, _pendingVisible);
            }
        }

        // ─────────── Core Inject/Update ───────────

        private static void DoInjectOrUpdate(string tags, bool visible)
        {
            try
            {
                RemoveOldWidget();

                if (visible)
                    TryInjectIntoPortraitPanel(tags);

                if (_tagWidget != null)
                {
                    _tagWidget.IsVisible = visible;
                    // Update text on sub-widgets if they exist
                    if (_tagValueWidget != null && visible)
                        SetWidgetText(_tagValueWidget, tags ?? "");
                }
                if (_tagNotesWidget != null)
                    _tagNotesWidget.IsVisible = visible;

                MCMSettings.DebugLog("TagWidget: visible=" + visible + " widgetReady=" + _widgetReady);

                if (visible && !_widgetReady && _retryCount < MaxRetries)
                {
                    _retryCount++;
                    // Exponential backoff: 100ms, 150ms, 225ms, ... capped at 500ms
                    int delay = Math.Min(500, (int)(BaseRetryDelayMs * Math.Pow(1.5, _retryCount - 1)));
                    MCMSettings.DebugLog("TagWidget: scheduling retry #" + _retryCount
                        + " in " + delay + "ms");
                    // Theme 2 fix: dispose previous timer before reassigning (round 4 finding V-W1).
                    DisposeRetryTimer();
                    _retryTimer = new System.Threading.Timer(_ =>
                    {
                        _retryPending = true;
                    }, null, delay, System.Threading.Timeout.Infinite);
                }
                else if (visible && !_widgetReady && _retryCount >= MaxRetries && !_injectionFailed)
                {
                    _injectionFailed = true;
                    MCMSettings.DebugLog("TagWidget: injection failed after " + MaxRetries + " retries");
                    try
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(
                                Localization.L("tag_injection_failed"),
                                Colors.Yellow));
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("TagWidgetInjector: DisplayMessage failed: " + ex.ToString()); }
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("TagWidget error: " + ex.ToString());
            }
        }

        private static void RemoveOldWidget()
        {
            if (_tagNotesWidget != null)
            {
                try
                {
                    if (_tagNotesWidget.ParentWidget != null)
                        _tagNotesWidget.ParentWidget.RemoveChild(_tagNotesWidget);
                }
                catch (Exception ex) { MCMSettings.DebugLog("TagWidgetInjector: failed to remove tag notes widget: " + ex.ToString()); }
            }
            _tagNotesWidget = null;
            if (_tagWidget != null)
            {
                try
                {
                    if (_tagWidget.ParentWidget != null)
                        _tagWidget.ParentWidget.RemoveChild(_tagWidget);
                }
                catch (Exception ex) { MCMSettings.DebugLog("TagWidgetInjector: failed to remove tag widget: " + ex.ToString()); }
            }
            _tagWidget = null;
            _tagLabelWidget = null;
            _tagValueWidget = null;
            _widgetReady = false;
        }

        private static void DisposeRetryTimer()
        {
            try { _retryTimer?.Dispose(); }
            catch (Exception ex) { MCMSettings.DebugLog("TagWidgetInjector: failed to dispose retry timer: " + ex.ToString()); }
            _retryTimer = null;
        }

        // ─────────── Widget Injection (Portrait Panel) ───────────

        private static void TryInjectIntoPortraitPanel(string tags)
        {
            try
            {
                var topScreen = ScreenManager.TopScreen;
                if (topScreen == null) return;

                GauntletLayer encLayer = FindEncyclopediaLayer(topScreen);
                if (encLayer == null)
                {
                    MCMSettings.DebugLog("TagWidget: no encyclopedia layer found");
                    return;
                }

                Widget layerRoot = GetLayerRootWidget(encLayer);
                if (layerRoot == null)
                {
                    MCMSettings.DebugLog("TagWidget: layer root is null");
                    return;
                }

                UIContext uiContext = layerRoot.EventManager?.Context as UIContext;
                if (uiContext == null)
                {
                    MCMSettings.DebugLog("TagWidget: cannot get UIContext");
                    return;
                }

                Widget encWindow = FindLargestChild(layerRoot);
                if (encWindow == null)
                {
                    MCMSettings.DebugLog("TagWidget: no encyclopedia window");
                    return;
                }

                // Step 1: Find an anchor widget in the LEFT panel and its containing ListPanel.
                // Anchor priority determines tag placement for each page type:
                //   - "Skills" for hero pages (insert before Skills)
                //   - "Bound settlement" for village pages (insert after Bound settlement)
                //   - "Culture" for town/castle settlement pages (insert after Culture)
                //   - "Part of" for clan pages (insert after the faction/banner area)
                //   - "G Tags" / "E Edit" hint text as final fallback
                string pageType = EncyclopediaPageTracker.CurrentPageType;
                Widget anchorWidget = null;
                string anchorName = null;
                foreach (var candidate in new[] { "Skills", "Bound settlement", "Culture", "Part of", "G Tags", "E Edit" })
                {
                    anchorWidget = FindWidgetByTextContent(encWindow, candidate);
                    if (anchorWidget != null) { anchorName = candidate; break; }
                }
                if (anchorWidget == null)
                {
                    MCMSettings.DebugLog("TagWidget: no anchor widget found");
                    return;
                }
                MCMSettings.DebugLog("TagWidget: using anchor '" + anchorName + "' pageType=" + (pageType ?? "null"));

                // Walk up from the anchor to find the nearest ListPanel ancestor —
                // that's the vertical stack container where we add our widget.
                Widget stackPanel = FindAncestorListPanel(anchorWidget);
                if (stackPanel == null)
                {
                    MCMSettings.DebugLog("TagWidget: no ListPanel ancestor found");
                    return;
                }

                // For Kingdom and Clan pages, the fallback anchors (G Tags, E Edit) are in the
                // RIGHT panel. Tags should go in the LEFT panel (after the banner image).
                // Strategy: find the entity name widget which is in the left panel,
                // then walk up to find its content container and append there.
                // Note: "Part of" is already in the left panel, so we don't need FindLeftPanelViaName for it —
                // the stackPanel from FindAncestorListPanel is already correct.
                // For Kingdom pages, "Culture" is inside the injected stats grid (a tiny ListPanel with 2 children),
                // so we must also use left panel detection to avoid inserting into the stats row.
                bool useLeftPanel = (pageType == "Kingdom" || pageType == "Clan")
                    && (anchorName == "G Tags" || anchorName == "E Edit" || anchorName == "Culture");
                if (useLeftPanel)
                {
                    Widget leftContainer = FindLeftPanelViaName(encWindow);
                    // FindLeftPanelViaName can fail for Kingdom pages because the name
                    // (e.g. "Aserai") appears in the title bar, description, and left panel.
                    // The depth-first search may find the wrong occurrence first.
                    // Fall back to the two-column layout detection approach.
                    if (leftContainer == null)
                    {
                        leftContainer = FindLeftPanel(encWindow);
                        if (leftContainer != null)
                            MCMSettings.DebugLog("TagWidget: found left panel via two-column detection for " + pageType);
                    }
                    if (leftContainer != null)
                    {
                        stackPanel = leftContainer;
                        MCMSettings.DebugLog("TagWidget: using left panel for " + pageType
                            + " type=" + leftContainer.GetType().Name
                            + " children=" + leftContainer.ChildCount
                            + " desc=" + CountDescendants(leftContainer));
                    }
                    else
                    {
                        MCMSettings.DebugLog("TagWidget: WARNING - could not find left panel for " + pageType
                            + ", tags will be placed in fallback location");
                    }
                }

                MCMSettings.DebugLog("TagWidget: stack panel type=" + stackPanel.GetType().Name
                    + " children=" + stackPanel.ChildCount
                    + " desc=" + CountDescendants(stackPanel));

                // Find the anchor's section container (immediate child of the stack panel)
                Widget anchorContainer = FindAncestorChildOf(anchorWidget, stackPanel);
                int anchorIdx = anchorContainer != null
                    ? FindChildIndex(stackPanel, anchorContainer)
                    : -1;

                // Determine insertion index based on anchor type
                int insertIdx;
                if (useLeftPanel)
                {
                    insertIdx = -1; // append at end of left panel (after banner)
                    MCMSettings.DebugLog("TagWidget: appending to left panel for " + pageType);
                }
                else if (anchorName == "Skills" && anchorIdx >= 0)
                {
                    insertIdx = anchorIdx;
                    MCMSettings.DebugLog("TagWidget: inserting before Skills at idx=" + insertIdx);
                }
                else if (anchorName == "Part of")
                {
                    insertIdx = -1; // append at end (after banner)
                    MCMSettings.DebugLog("TagWidget: appending after banner in clan left panel");
                }
                else if (anchorIdx >= 0)
                {
                    // "Bound settlement", "Culture", or hint text — insert after anchor
                    insertIdx = anchorIdx + 1;
                    MCMSettings.DebugLog("TagWidget: inserting after '" + anchorName + "' at idx=" + insertIdx);
                }
                else
                {
                    insertIdx = -1; // will append
                }

                // On unmet hero pages, the NeverMetDisclaimer ("You haven't met this hero yet.")
                // sits at the bottom of the left panel. Find it by text.
                Widget disclaimerWidget = FindWidgetByTextContent(encWindow, "haven't met");
                if (disclaimerWidget != null)
                {
                    MCMSettings.DebugLog("TagWidget: found disclaimer text, widget type="
                        + disclaimerWidget.GetType().Name
                        + " parent type=" + (disclaimerWidget.ParentWidget?.GetType().Name ?? "null")
                        + " parent children=" + (disclaimerWidget.ParentWidget?.ChildCount ?? 0));
                }

                // Step 2: Find brushes for proper font rendering.
                // First try getting brushes from the BrushFactory (works across all page types).
                // Then fall back to searching the widget tree.
                Brush labelBrush = TryGetBrushFromFactory(uiContext, "Encyclopedia.Stat.DefinitionText");
                Brush valueBrush = TryGetBrushFromFactory(uiContext, "Encyclopedia.Stat.ValueText");

                // Fall back to widget tree search if BrushFactory didn't have them
                if (labelBrush == null)
                    labelBrush = FindBrushByName(encWindow, "Encyclopedia.Stat.DefinitionText");
                if (valueBrush == null)
                    valueBrush = FindBrushByName(encWindow, "Encyclopedia.Stat.ValueText")
                                ?? FindAnyTextBrush(encWindow);

                // On settlement pages, named stat brushes may not exist.
                // Fall back to cloning the brush from the anchor widget itself
                // (e.g., "Bound settlement" or "Culture" label — already golden).
                if (labelBrush == null && anchorWidget is TextWidget anchorTw && anchorTw.ReadOnlyBrush != null)
                    labelBrush = anchorTw.ReadOnlyBrush;
                if (labelBrush == null && anchorWidget is BrushWidget anchorBw && anchorBw.ReadOnlyBrush != null)
                    labelBrush = anchorBw.ReadOnlyBrush;

                // On unmet hero pages, stat brushes may not exist — fall back to disclaimer brush
                if (disclaimerWidget is BrushWidget disclaimerBw && disclaimerBw.ReadOnlyBrush != null)
                {
                    if (labelBrush == null)
                        labelBrush = disclaimerBw.ReadOnlyBrush;
                    if (valueBrush == null)
                        valueBrush = disclaimerBw.ReadOnlyBrush;
                }
                // Also try the page name widget as brush source
                if (valueBrush == null)
                {
                    Widget nameSource = FindWidgetByTextContent(stackPanel,
                        EncyclopediaPageTracker.CurrentName ?? "");
                    if (nameSource is BrushWidget nbw && nbw.ReadOnlyBrush != null)
                        valueBrush = nbw.ReadOnlyBrush;
                }

                MCMSettings.DebugLog("TagWidget: labelBrush=" + (labelBrush?.Name ?? "null")
                    + " valueBrush=" + (valueBrush?.Name ?? "null"));

                // Step 3: Create a vertical container that holds horizontal rows of tags.
                // Tags wrap to new rows when they exceed the available width.
                Widget container = TryCreateWidgetByType(uiContext, "ListPanel");
                if (container == null) container = new Widget(uiContext);
                container.Id = "EditableEncyclopediaTagWidget";
                container.WidthSizePolicy = SizePolicy.StretchToParent;
                container.HeightSizePolicy = SizePolicy.CoverChildren;
                container.HorizontalAlignment = HorizontalAlignment.Center;
                container.MarginTop = 2f;
                container.MarginBottom = 2f;
                container.DoNotAcceptEvents = true;
                // Vertical layout for the container (rows stack top-to-bottom)
                // ListPanel defaults to vertical, so no need to set StackLayout

                // Find a nearby text widget to copy font/brush from (e.g., "Culture: Nord")
                Widget cultureWidget = FindWidgetByTextContent(encWindow, "Culture");
                Brush nearbyBrush = null;
                if (cultureWidget is TextWidget ctw && ctw.ReadOnlyBrush != null)
                    nearbyBrush = ctw.ReadOnlyBrush;

                // Use nearby Culture widget brush as label brush fallback (golden on settlement pages)
                if (labelBrush == null && nearbyBrush != null)
                    labelBrush = nearbyBrush;

                // Scale font using MCM-configurable value (default 1.3x)
                bool isUnmetHero = disclaimerWidget != null;
                float fontScale = GetTagFontScale();

                // Estimate available width from the stack panel
                float availableWidth = stackPanel.SuggestedWidth;
                if (availableWidth <= 0) availableWidth = 320f; // fallback portrait panel width
                // Approximate character width based on font size (heuristic)
                float baseFontSize = 20f * fontScale;
                float charWidth = baseFontSize * 0.55f;

                // Create the first horizontal row
                Widget currentRow = CreateHorizontalRow(uiContext, 0);
                container.AddChild(currentRow);
                int rowIndex = 0;

                // "Tags: " label — use label brush for golden color
                _tagLabelWidget = new TextWidget(uiContext);
                _tagLabelWidget.Id = "EditableEncyclopediaTagLabel";
                ((TextWidget)_tagLabelWidget).Text = Localization.L("ui_tags_prefix");
                _tagLabelWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                _tagLabelWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                if (labelBrush != null)
                {
                    Brush lb = labelBrush.Clone();
                    ScaleBrushFontSize(lb, fontScale);
                    ((TextWidget)_tagLabelWidget).Brush = lb;
                }
                currentRow.AddChild(_tagLabelWidget);
                float currentRowWidth = 6f * charWidth; // "Tags: " ~6 chars

                // Tag values — render each tag as a colored badge, wrapping to new rows
                Brush baseBrush = valueBrush ?? nearbyBrush;
                if (!string.IsNullOrEmpty(tags))
                {
                    string[] tagParts = tags.Split(',');
                    // Sort tags by priority (threats first, then allies, etc.)
                    Array.Sort(tagParts, (a, b) =>
                        GetTagSortPriority(a.Trim()).CompareTo(GetTagSortPriority(b.Trim())));
                    for (int ti = 0; ti < tagParts.Length; ti++)
                    {
                        string tag = tagParts[ti].Trim();
                        if (string.IsNullOrEmpty(tag)) continue;

                        string icon = GetTagIcon(tag);
                        // Shorten auto-tag display: "Auto: Friend" → "Friend" (icon+color distinguish them)
                        bool isAutoTag = tag.StartsWith("Auto: ", StringComparison.OrdinalIgnoreCase);
                        string displayText = isAutoTag ? tag.Substring(6) : tag;
                        string fullText = icon + displayText;

                        // Estimate this tag's width
                        float tagEstWidth = fullText.Length * charWidth * (isAutoTag ? 0.85f : 1f) + 12f;

                        // Wrap to new row if this tag would overflow
                        if (currentRowWidth + tagEstWidth > availableWidth && currentRow.ChildCount > 0)
                        {
                            rowIndex++;
                            currentRow = CreateHorizontalRow(uiContext, rowIndex);
                            container.AddChild(currentRow);

                            // Add invisible spacer matching the "Tags:" label width
                            // so subsequent rows align with the first tag on row 0
                            var rowIndentSpacer = new Widget(uiContext);
                            rowIndentSpacer.WidthSizePolicy = SizePolicy.Fixed;
                            rowIndentSpacer.SuggestedWidth = _tagLabelWidget.SuggestedWidth > 0
                                ? _tagLabelWidget.SuggestedWidth
                                : 6f * charWidth;
                            rowIndentSpacer.HeightSizePolicy = SizePolicy.Fixed;
                            rowIndentSpacer.SuggestedHeight = 1f;
                            rowIndentSpacer.DoNotAcceptEvents = true;
                            currentRow.AddChild(rowIndentSpacer);

                            currentRowWidth = 6f * charWidth; // account for indent spacer width
                        }

                        // Track whether this is the first tag on the current row
                        // Row 0 children: flex spacer + "Tags:" label + tag widgets
                        // Row 1+ children: flex spacer + indent spacer + tag widgets
                        bool isFirstTagOnRow = currentRow.ChildCount <= 2;

                        var tagWidget = new TextWidget(uiContext);
                        tagWidget.Id = "EditableEncyclopediaTag_" + ti;
                        // Add leading spaces for separation since MarginLeft is unreliable with flex spacers
                        tagWidget.Text = isFirstTagOnRow ? fullText : "  " + fullText;
                        tagWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                        tagWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                        tagWidget.MarginLeft = currentRow.ChildCount > 0 ? 12f : 0f;
                        if (baseBrush != null)
                        {
                            Brush tb = baseBrush.Clone();
                            float tagScale = isAutoTag ? fontScale * 0.85f : fontScale;
                            ScaleBrushFontSize(tb, tagScale);
                            Color tagColor = GetTagColor(tag);
                            // Dim auto-tags slightly to distinguish from manual tags
                            float alpha = isAutoTag ? 0.75f : tagColor.Alpha;
                            SetBrushColor(tb, tagColor.Red, tagColor.Green, tagColor.Blue, alpha);
                            tagWidget.Brush = tb;
                        }
                        currentRow.AddChild(tagWidget);
                        currentRowWidth += tagEstWidth;
                    }
                }
                // Keep _tagValueWidget as null — individual tag widgets are used instead
                _tagValueWidget = null;

                // Add right spacers to each row to complete centering (pairs with left spacer)
                for (int ri = 0; ri < container.ChildCount; ri++)
                {
                    Widget row = container.GetChild(ri);
                    row.AddChild(CreateFlexSpacer(uiContext));
                }

                _tagWidget = container;

                // Build a separate notes container below the tags with word-wrapping
                string currentObjectId = EncyclopediaPageTracker.CurrentObjectId;
                if (!string.IsNullOrEmpty(currentObjectId) && EncyclopediaEditBehavior.Instance != null)
                {
                    var tagNotes = EncyclopediaEditBehavior.Instance.GetAllTagNotes(currentObjectId);
                    if (tagNotes.Count > 0)
                    {
                        // Vertical container for notes (each note gets its own word-wrapped widget)
                        Widget notesContainer = TryCreateWidgetByType(uiContext, "ListPanel");
                        if (notesContainer == null) notesContainer = new Widget(uiContext);
                        notesContainer.Id = "EditableEncyclopediaTagNotesRow";
                        notesContainer.WidthSizePolicy = SizePolicy.StretchToParent;
                        notesContainer.HeightSizePolicy = SizePolicy.CoverChildren;
                        notesContainer.HorizontalAlignment = HorizontalAlignment.Center;
                        notesContainer.MarginTop = 1f;
                        notesContainer.MarginBottom = 2f;
                        notesContainer.DoNotAcceptEvents = true;
                        // Vertical layout (ListPanel default) — each note stacks below

                        // Compute word-wrap parameters (same approach as Chronicle/Journal)
                        float noteTextWidth = availableWidth - 16f;
                        if (noteTextWidth < 120f) noteTextWidth = 120f;
                        float noteCharWidth = charWidth * 0.85f;
                        int charsPerLine = (int)(noteTextWidth / noteCharWidth);
                        if (charsPerLine < 15) charsPerLine = 15;

                        int noteIdx = 0;
                        foreach (var noteKvp in tagNotes)
                        {
                            string noteTag = noteKvp.Key;
                            string noteText = noteKvp.Value;
                            if (string.IsNullOrEmpty(noteText)) continue;

                            string displayTag = noteTag.Length > 0
                                ? char.ToUpperInvariant(noteTag[0]) + noteTag.Substring(1)
                                : noteTag;

                            string prefix = noteIdx == 0 ? "Notes: " : "";
                            string noteFullText = prefix + "\u00BB " + displayTag + ": " + noteText;

                            // Word-wrap long notes using the same approach as Chronicle sidebar
                            string wrappedText = JournalSectionInjector.WordWrap(noteFullText, charsPerLine);

                            // Try RichTextWidget first (supports \n wrapping), fall back to TextWidget
                            Widget noteWidget = JournalSectionInjector.TryCreateRichTextWidget(uiContext, wrappedText);
                            if (noteWidget == null)
                            {
                                var tw = new TextWidget(uiContext);
                                tw.Text = wrappedText;
                                noteWidget = tw;
                            }
                            noteWidget.Id = "EditableEncyclopediaTagNote_" + noteTag;
                            noteWidget.WidthSizePolicy = SizePolicy.Fixed;
                            noteWidget.SuggestedWidth = noteTextWidth;
                            noteWidget.MaxWidth = noteTextWidth;
                            noteWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                            noteWidget.HorizontalAlignment = HorizontalAlignment.Center;
                            if (baseBrush != null)
                            {
                                Brush noteBrush = baseBrush.Clone();
                                float ns = fontScale * 0.85f;
                                ScaleBrushFontSize(noteBrush, ns);
                                SetBrushColor(noteBrush, 0.7f, 0.7f, 0.6f, 0.9f); // muted gold
                                // Apply brush for "Notes:" label portion on first note
                                if (noteIdx == 0 && labelBrush != null)
                                {
                                    // Use the muted gold for the whole widget (label + note text together)
                                }
                                var noteType = noteWidget.GetType();
                                PropertyInfo bp;
                                if (_cachedNoteBrushPropType == noteType)
                                {
                                    bp = _cachedNoteBrushProp;
                                }
                                else
                                {
                                    bp = noteType.GetProperty("Brush", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                    _cachedNoteBrushPropType = noteType;
                                    _cachedNoteBrushProp = bp;
                                }
                                if (bp != null && bp.CanWrite) bp.SetValue(noteWidget, noteBrush);
                            }
                            notesContainer.AddChild(noteWidget);
                            noteIdx++;
                        }

                        _tagNotesWidget = notesContainer;
                    }
                }

                // Insert at computed position, or above NeverMetDisclaimer, or append
                if (disclaimerWidget != null && disclaimerWidget.ParentWidget != null)
                {
                    // Unmet hero page: the disclaimer's parent is a generic Widget (not ListPanel),
                    // so children overlay. Position tags at absolute bottom with enough margin
                    // to sit ABOVE the disclaimer text.
                    _tagWidget.VerticalAlignment = VerticalAlignment.Bottom;
                    _tagWidget.MarginBottom = _tagNotesWidget != null ? 70f : 45f;
                    Widget disclaimerParent = disclaimerWidget.ParentWidget;
                    try
                    {
                        disclaimerParent.AddChild(_tagWidget);
                        if (_tagNotesWidget != null)
                        {
                            _tagNotesWidget.VerticalAlignment = VerticalAlignment.Bottom;
                            _tagNotesWidget.MarginBottom = 45f;
                            disclaimerParent.AddChild(_tagNotesWidget);
                        }
                        MCMSettings.DebugLog("TagWidget: added to disclaimer parent with Bottom alignment + margin");
                    }
                    catch (Exception ex)
                    {
                        MCMSettings.DebugLog("TagWidget: disclaimer parent insert failed: " + ex.ToString());
                        stackPanel.AddChild(_tagWidget);
                        if (_tagNotesWidget != null) stackPanel.AddChild(_tagNotesWidget);
                    }
                }
                else if (insertIdx >= 0)
                {
                    try
                    {
                        stackPanel.AddChildAtIndex(_tagWidget, insertIdx);
                        if (_tagNotesWidget != null)
                            stackPanel.AddChildAtIndex(_tagNotesWidget, insertIdx + 1);
                        MCMSettings.DebugLog("TagWidget: inserted at idx=" + insertIdx
                            + " (anchor='" + anchorName + "')");
                    }
                    catch (Exception ex)
                    {
                        MCMSettings.DebugLog("TagWidgetInjector: AddChildAtIndex failed, appending instead: " + ex.ToString());
                        stackPanel.AddChild(_tagWidget);
                        if (_tagNotesWidget != null) stackPanel.AddChild(_tagNotesWidget);
                    }
                }
                else
                {
                    stackPanel.AddChild(_tagWidget);
                    if (_tagNotesWidget != null) stackPanel.AddChild(_tagNotesWidget);
                    MCMSettings.DebugLog("TagWidget: appended to stack panel (no Skills container)");
                }

                _widgetReady = true;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("TagWidget: injection error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Walks up from 'widget' to find the nearest ListPanel ancestor.
        /// ListPanels have vertical stack layout, so children are properly positioned.
        /// </summary>
        private static Widget FindAncestorListPanel(Widget widget)
        {
            Widget current = widget.ParentWidget;
            for (int i = 0; i < 15 && current != null; i++)
            {
                string typeName = current.GetType().Name;
                if (typeName == "ListPanel" || typeName == "NavigatableListPanel")
                {
                    return current;
                }
                current = current.ParentWidget;
            }
            return null;
        }

        /// <summary>
        /// Finds the ancestor of 'widget' that is a direct child of 'targetParent'.
        /// Used to find the Skills section container within the stack panel.
        /// </summary>
        private static Widget FindAncestorChildOf(Widget widget, Widget targetParent)
        {
            Widget current = widget;
            while (current != null)
            {
                if (current.ParentWidget == targetParent)
                    return current;
                current = current.ParentWidget;
            }
            return null;
        }

        /// <summary>
        /// Finds the left panel content container by locating the entity name widget
        /// (e.g., "Aserai", "Argoros") and walking up to find a suitable ListPanel ancestor.
        /// The name is always in the left panel, so its parent chain leads us there.
        /// We specifically look for a ListPanel to ensure proper vertical stacking of children.
        /// </summary>
        private static Widget FindLeftPanelViaName(Widget encWindow)
        {
            string entityName = EncyclopediaPageTracker.CurrentName;
            if (string.IsNullOrEmpty(entityName)) return null;

            Widget nameWidget = FindWidgetByTextContent(encWindow, entityName);
            if (nameWidget == null)
            {
                MCMSettings.DebugLog("TagWidget: FindLeftPanelViaName: name widget '" + entityName + "' not found");
                return null;
            }

            MCMSettings.DebugLog("TagWidget: FindLeftPanelViaName: found name '" + entityName + "' type="
                + nameWidget.GetType().Name);

            // Walk up from the name widget to find the left column ListPanel.
            // The name's immediate parent may be a small ListPanel (just the name area),
            // so we keep walking until we find one with enough descendants to include
            // both the name area and the banner image (the actual column panel).
            // Guard: if the name widget is in a very large subtree (> 100 descendants),
            // it's likely the page title/header, not the left panel entity name.
            // Return null so the caller can use a fallback approach.
            int encDescendants = CountDescendants(encWindow);
            Widget nameParentCheck = nameWidget.ParentWidget;
            if (nameParentCheck != null)
            {
                int nameSubtreeSize = CountDescendants(nameParentCheck);
                // If the name's parent subtree covers most of the page, it's the title area
                if (encDescendants > 0 && nameSubtreeSize > encDescendants * 0.7)
                {
                    MCMSettings.DebugLog("TagWidget: FindLeftPanelViaName: name '" + entityName
                        + "' appears to be in title/header area (subtree=" + nameSubtreeSize
                        + " vs page=" + encDescendants + "), skipping");
                    return null;
                }
            }

            Widget bestListPanel = null;
            Widget current = nameWidget.ParentWidget;
            int depth = 0;
            while (current != null && current != encWindow && depth < 15)
            {
                string typeName = current.GetType().Name;
                if (typeName == "ListPanel" || typeName == "NavigatableListPanel")
                {
                    int desc = CountDescendants(current);
                    MCMSettings.DebugLog("TagWidget: FindLeftPanelViaName: candidate ListPanel type="
                        + typeName + " children=" + current.ChildCount
                        + " desc=" + desc + " depth=" + depth);
                    // Reject if this ListPanel is too large — it's likely a page-level container,
                    // not the left column. The left panel should have < 40% of total descendants.
                    if (encDescendants > 0 && desc > encDescendants * 0.4)
                    {
                        MCMSettings.DebugLog("TagWidget: FindLeftPanelViaName: candidate too large, skipping");
                        break;
                    }
                    bestListPanel = current;
                    // The actual left column panel has >= 8 descendants (name area + banner).
                    // Small ListPanels (< 8 desc) are just inner containers — keep looking.
                    if (desc >= 8)
                        break;
                }
                current = current.ParentWidget;
                depth++;
            }

            if (bestListPanel != null)
                return bestListPanel;

            // Fallback: if no ListPanel found, walk up again looking for any container
            // with enough descendants to include both name and banner
            current = nameWidget.ParentWidget;
            depth = 0;
            while (current != null && current != encWindow && depth < 15)
            {
                if (current.ChildCount > 1)
                {
                    int desc = CountDescendants(current);
                    if (desc >= 5)
                    {
                        MCMSettings.DebugLog("TagWidget: FindLeftPanelViaName: fallback container type="
                            + current.GetType().Name + " children=" + current.ChildCount
                            + " desc=" + desc + " depth=" + depth);
                        return current;
                    }
                }
                current = current.ParentWidget;
                depth++;
            }

            // Last resort: parent of name widget
            MCMSettings.DebugLog("TagWidget: FindLeftPanelViaName: using name parent as last resort");
            return nameWidget.ParentWidget;
        }

        /// <summary>
        /// Finds the first ListPanel descendant within a widget (depth-first, shallow).
        /// Used when the left panel itself is a generic Widget — we need the inner ListPanel
        /// to get proper vertical stacking of children (name, banner, tags).
        /// </summary>
        private static Widget FindFirstListPanel(Widget parent)
        {
            if (parent == null) return null;
            string parentType = parent.GetType().Name;
            if (parentType == "ListPanel" || parentType == "NavigatableListPanel")
                return parent;
            for (int i = 0; i < parent.ChildCount; i++)
            {
                Widget child = parent.GetChild(i);
                string typeName = child.GetType().Name;
                if (typeName == "ListPanel" || typeName == "NavigatableListPanel")
                    return child;
            }
            // One more level deep
            for (int i = 0; i < parent.ChildCount; i++)
            {
                Widget child = parent.GetChild(i);
                for (int j = 0; j < child.ChildCount; j++)
                {
                    Widget grandchild = child.GetChild(j);
                    string typeName = grandchild.GetType().Name;
                    if (typeName == "ListPanel" || typeName == "NavigatableListPanel")
                        return grandchild;
                }
            }
            return null;
        }

        /// <summary>
        /// Finds the left panel in a two-column encyclopedia page layout.
        /// The left panel is typically the narrower ListPanel child (contains name + banner),
        /// while the right panel is wider (contains description, sections).
        /// </summary>
        private static Widget FindLeftPanel(Widget encWindow)
        {
            // The page layout has a top-level container with two column children.
            // Walk down to find the horizontal container that holds both columns.
            // The left panel is the first ListPanel child, or the one with fewer descendants.
            Widget twoColumnContainer = FindTwoColumnContainer(encWindow, 0);
            if (twoColumnContainer == null)
            {
                MCMSettings.DebugLog("TagWidget: FindLeftPanel: no two-column container found");
                return null;
            }

            Widget leftCandidate = null;
            int leftDesc = int.MaxValue;
            for (int i = 0; i < twoColumnContainer.ChildCount; i++)
            {
                Widget child = twoColumnContainer.GetChild(i);
                string typeName = child.GetType().Name;
                if (typeName != "ListPanel" && typeName != "NavigatableListPanel" && typeName != "Widget")
                    continue;
                int desc = CountDescendants(child);
                // The left panel (name + banner) has fewer descendants than the right (sections)
                if (desc < leftDesc)
                {
                    leftDesc = desc;
                    leftCandidate = child;
                }
            }

            // If the left candidate is a generic Widget (not a ListPanel), try to find
            // a ListPanel inside it for proper vertical stacking of children.
            if (leftCandidate != null)
            {
                string leftType = leftCandidate.GetType().Name;
                if (leftType != "ListPanel" && leftType != "NavigatableListPanel")
                {
                    Widget innerList = FindFirstListPanel(leftCandidate);
                    if (innerList != null)
                    {
                        MCMSettings.DebugLog("TagWidget: FindLeftPanel: using inner ListPanel instead of " + leftType);
                        leftCandidate = innerList;
                    }
                }
                MCMSettings.DebugLog("TagWidget: FindLeftPanel: found left panel type="
                    + leftCandidate.GetType().Name + " children=" + leftCandidate.ChildCount
                    + " desc=" + leftDesc);
            }

            return leftCandidate;
        }

        /// <summary>
        /// Finds the horizontal two-column container in the encyclopedia page.
        /// It's a widget with exactly 2 significant children (left and right columns).
        /// </summary>
        private static Widget FindTwoColumnContainer(Widget widget, int depth)
        {
            if (widget == null || depth > 10) return null;

            // Look for a widget with 2-3 children where at least 2 have many descendants
            int significantChildren = 0;
            for (int i = 0; i < widget.ChildCount; i++)
            {
                if (CountDescendants(widget.GetChild(i)) > 5)
                    significantChildren++;
            }
            if (significantChildren == 2 && widget.ChildCount <= 4)
                return widget;

            // Recurse into the largest child
            Widget best = null;
            int bestDesc = 0;
            for (int i = 0; i < widget.ChildCount; i++)
            {
                Widget child = widget.GetChild(i);
                int desc = CountDescendants(child);
                if (desc > bestDesc) { bestDesc = desc; best = child; }
            }
            if (best != null)
                return FindTwoColumnContainer(best, depth + 1);
            return null;
        }

        // ─────────── Helpers ───────────

        private static Widget FindWidgetById(Widget parent, string id, int depth = 0)
        {
            if (parent == null || depth > 20) return null;
            if (parent.Id == id) return parent;
            for (int i = 0; i < parent.ChildCount; i++)
            {
                var result = FindWidgetById(parent.GetChild(i), id, depth + 1);
                if (result != null) return result;
            }
            return null;
        }

        private static Widget FindWidgetByTextContent(Widget parent, string substring, int depth = 0)
        {
            if (parent == null || depth > 20) return null;

            string text = TryGetText(parent);
            if (!string.IsNullOrEmpty(text)
                && text.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0
                && (parent.Id == null || !parent.Id.StartsWith("EditableEncyclopedia")))
                return parent;

            for (int i = 0; i < parent.ChildCount; i++)
            {
                var result = FindWidgetByTextContent(parent.GetChild(i), substring, depth + 1);
                if (result != null) return result;
            }
            return null;
        }

        private static int FindChildIndex(Widget parent, Widget child)
        {
            for (int i = 0; i < parent.ChildCount; i++)
            {
                if (parent.GetChild(i) == child)
                    return i;
            }
            return -1;
        }

        private static string TryGetText(Widget widget)
        {
            if (widget is TextWidget tw) return tw.Text;

            var widgetType = widget.GetType();
            try
            {
                PropertyInfo textProp;
                if (_cachedTextPropType == widgetType)
                {
                    textProp = _cachedTextProp;
                }
                else
                {
                    textProp = widgetType.GetProperty("Text",
                        BindingFlags.Instance | BindingFlags.Public);
                    if (textProp != null && textProp.PropertyType == typeof(string))
                    {
                        _cachedTextPropType = widgetType;
                        _cachedTextProp = textProp;
                    }
                    else
                    {
                        return null;
                    }
                }

                return textProp?.GetValue(widget) as string;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("TagWidgetInjector: TryGetText failed: " + ex.ToString());
                return null;
            }
        }

        private static void SetWidgetText(Widget widget, string text)
        {
            if (widget is TextWidget tw) { tw.Text = text; return; }
            try
            {
                var widgetType = widget.GetType();
                PropertyInfo textProp;
                if (_cachedSetTextPropType == widgetType)
                {
                    textProp = _cachedSetTextProp;
                }
                else
                {
                    textProp = widgetType.GetProperty("Text", AllFlags);
                    _cachedSetTextPropType = widgetType;
                    _cachedSetTextProp = textProp;
                }
                if (textProp != null && textProp.CanWrite)
                    textProp.SetValue(widget, text);
            }
            catch (Exception ex) { MCMSettings.DebugLog("TagWidgetInjector: SetWidgetText failed: " + ex.ToString()); }
        }

        // ─────────── Brush & Layout Helpers ───────────

        /// <summary>
        /// Attempts to get a named brush from the UIContext's BrushFactory.
        /// This is more reliable than searching the widget tree since brushes are globally registered.
        /// </summary>
        private static Brush TryGetBrushFromFactory(UIContext uiContext, string brushName)
        {
            if (uiContext == null || string.IsNullOrEmpty(brushName)) return null;
            try
            {
                var uiType = uiContext.GetType();
                PropertyInfo factoryProp;
                if (_cachedBrushFactoryPropType == uiType)
                {
                    factoryProp = _cachedBrushFactoryProp;
                }
                else
                {
                    factoryProp = uiType.GetProperty("BrushFactory", AllFlags)
                               ?? uiType.GetProperty("Brushes", AllFlags);
                    _cachedBrushFactoryPropType = uiType;
                    _cachedBrushFactoryProp = factoryProp;
                }
                if (factoryProp == null) return null;

                var factory = factoryProp.GetValue(uiContext);
                if (factory == null) return null;

                var factoryType = factory.GetType();
                MethodInfo getBrush;
                if (_cachedGetBrushMethodType == factoryType)
                {
                    getBrush = _cachedGetBrushMethod;
                }
                else
                {
                    getBrush = factoryType.GetMethod("GetBrush",
                        BindingFlags.Instance | BindingFlags.Public,
                        null, new[] { typeof(string) }, null);
                    _cachedGetBrushMethodType = factoryType;
                    _cachedGetBrushMethod = getBrush;
                }
                if (getBrush != null)
                {
                    var result = getBrush.Invoke(factory, new object[] { brushName }) as Brush;
                    if (result != null)
                    {
                        MCMSettings.DebugLog("TagWidget: got brush '" + brushName + "' from BrushFactory");
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                // GetBrush may throw if brush not found — that's fine
                MCMSettings.DebugLog("TagWidget: BrushFactory lookup for '" + brushName + "' failed: " + ex.InnerException?.Message ?? ex.Message);
            }
            return null;
        }

        /// <summary>
        /// Finds a brush by name in the widget tree (e.g., "Encyclopedia.Stat.DefinitionText").
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
        /// Finds any text brush in the widget tree as a fallback.
        /// </summary>
        private static Brush FindAnyTextBrush(Widget root, int depth = 0)
        {
            if (root == null || depth > 15) return null;
            if (root is TextWidget tw && tw.ReadOnlyBrush != null)
                return tw.ReadOnlyBrush;
            if (root is BrushWidget bw && bw.ReadOnlyBrush != null)
            {
                string name = bw.ReadOnlyBrush.Name ?? "";
                if (name.Contains("Text"))
                    return bw.ReadOnlyBrush;
            }
            for (int i = 0; i < root.ChildCount; i++)
            {
                var result = FindAnyTextBrush(root.GetChild(i), depth + 1);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// Creates a widget by type name via reflection (e.g., "ListPanel").
        /// </summary>
        private static Widget TryCreateWidgetByType(UIContext uiContext, string typeName)
        {
            try
            {
                Type wType = typeof(Widget).Assembly.GetType("TaleWorlds.GauntletUI.BaseTypes." + typeName)
                          ?? typeof(Widget).Assembly.GetType("TaleWorlds.GauntletUI." + typeName);
                if (wType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        wType = asm.GetType("TaleWorlds.GauntletUI.BaseTypes." + typeName)
                             ?? asm.GetType("TaleWorlds.GauntletUI." + typeName);
                        if (wType != null) break;
                    }
                }
                if (wType != null)
                {
                    var ctor = wType.GetConstructor(new[] { typeof(UIContext) });
                    if (ctor != null)
                        return ctor.Invoke(new object[] { uiContext }) as Widget;
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("TagWidget: TryCreateWidgetByType(" + typeName + ") failed: " + ex.ToString());
            }
            return null;
        }

        /// <summary>
        /// Creates a horizontal row (ListPanel) for tag wrapping.
        /// </summary>
        private static Widget CreateHorizontalRow(UIContext uiContext, int rowIndex)
        {
            Widget row = TryCreateWidgetByType(uiContext, "ListPanel");
            if (row == null) row = new Widget(uiContext);
            row.Id = "EditableEncyclopediaTagRow_" + rowIndex;
            row.WidthSizePolicy = SizePolicy.StretchToParent;
            row.HeightSizePolicy = SizePolicy.CoverChildren;
            row.HorizontalAlignment = HorizontalAlignment.Center;
            row.MarginTop = rowIndex > 0 ? 1f : 0f;
            row.DoNotAcceptEvents = true;
            CopyOrSetHorizontalLayout(row);
            // Left spacer pushes content to center (paired with right spacer added after content)
            row.AddChild(CreateFlexSpacer(uiContext));
            return row;
        }

        /// <summary>
        /// Creates a flexible spacer widget that stretches to fill available space.
        /// Used in pairs (left + right) inside horizontal rows to center content.
        /// </summary>
        private static Widget CreateFlexSpacer(UIContext uiContext)
        {
            var spacer = new Widget(uiContext);
            spacer.WidthSizePolicy = SizePolicy.StretchToParent;
            spacer.HeightSizePolicy = SizePolicy.Fixed;
            spacer.SuggestedHeight = 1f;
            spacer.DoNotAcceptEvents = true;
            return spacer;
        }

        /// <summary>
        /// Sets a ListPanel's StackLayout.LayoutMethod to HorizontalLeftToRight via reflection.
        /// </summary>
        private static void CopyOrSetHorizontalLayout(Widget targetPanel)
        {
            try
            {
                var panelType = targetPanel.GetType();
                PropertyInfo layoutProp;
                if (_cachedStackLayoutPropType == panelType)
                {
                    layoutProp = _cachedStackLayoutProp;
                }
                else
                {
                    layoutProp = panelType.GetProperty("StackLayout", AllFlags);
                    _cachedStackLayoutPropType = panelType;
                    _cachedStackLayoutProp = layoutProp;
                }
                if (layoutProp == null) return;
                var targetLayout = layoutProp.GetValue(targetPanel);
                if (targetLayout == null) return;

                var layoutType = targetLayout.GetType();
                PropertyInfo methodProp;
                if (_cachedLayoutMethodPropType == layoutType)
                {
                    methodProp = _cachedLayoutMethodProp;
                }
                else
                {
                    methodProp = layoutType.GetProperty("LayoutMethod", AllFlags);
                    _cachedLayoutMethodPropType = layoutType;
                    _cachedLayoutMethodProp = methodProp;
                }
                if (methodProp == null || !methodProp.CanWrite) return;

                var enumType = methodProp.PropertyType;
                foreach (var ev in Enum.GetValues(enumType))
                {
                    if (ev.ToString().Contains("HorizontalLeft"))
                    {
                        methodProp.SetValue(targetLayout, ev);
                        return;
                    }
                }
                // Last resort: index 2 (typically HorizontalLeftToRight)
                var vals = Enum.GetValues(enumType);
                if (vals.Length > 2)
                    methodProp.SetValue(targetLayout, vals.GetValue(2));
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("TagWidget: horizontal layout set failed: " + ex.ToString());
            }
        }

        /// <summary>
        /// Returns a color for a tag based on its name. Known categories get distinct colors;
        /// unknown tags get a default neutral color.
        /// </summary>
        private static Color GetTagColor(string tag)
        {
            string lower = tag.ToLowerInvariant();
            switch (lower)
            {
                // Hostile
                case "enemy":       return new Color(1f, 0.2f, 0.2f, 1f);       // red
                case "rival":       return new Color(0.9f, 0.25f, 0.25f, 1f);    // dark red
                case "traitor":     return new Color(0.7f, 0.2f, 0.8f, 1f);      // purple
                case "hostile":     return new Color(0.95f, 0.2f, 0.2f, 1f);     // red
                // Friendly
                case "ally":        return new Color(0.2f, 0.8f, 0.2f, 1f);      // green
                case "friend":      return new Color(0.3f, 0.85f, 0.3f, 1f);     // bright green
                case "trusted":     return new Color(0.25f, 0.75f, 0.4f, 1f);    // teal green
                // Strategic
                case "target":      return new Color(1f, 0.6f, 0.1f, 1f);        // orange
                case "spy":         return new Color(0.7f, 0.3f, 0.9f, 1f);      // purple
                case "recruit":     return new Color(0.3f, 0.7f, 1f, 1f);        // light blue
                case "vassal":
                case "future vassal": return new Color(0.4f, 0.6f, 0.9f, 1f);    // blue
                // Economy
                case "trade hub":   return new Color(0.3f, 0.6f, 1f, 1f);        // blue
                case "rich":        return new Color(0.9f, 0.8f, 0.2f, 1f);      // gold
                // Character
                case "dishonorable": return new Color(0.8f, 0.4f, 0.1f, 1f);     // dark orange
                case "honorable":   return new Color(0.9f, 0.85f, 0.3f, 1f);     // gold
                case "good commander": return new Color(0.3f, 0.6f, 1f, 1f);     // blue
                case "warlord":     return new Color(0.85f, 0.3f, 0.2f, 1f);     // crimson
                case "raid leader": return new Color(0.9f, 0.4f, 0.15f, 1f);     // burnt orange
                // Governance
                case "governor":    return new Color(0.5f, 0.8f, 0.9f, 1f);      // cyan
                case "future governor": return new Color(0.5f, 0.8f, 0.9f, 1f);  // cyan
                // Family
                case "marriage candidate": return new Color(1f, 0.5f, 0.7f, 1f); // pink
                case "family":      return new Color(0.7f, 0.9f, 0.4f, 1f);      // lime
                // Status
                case "prisoner":    return new Color(0.8f, 0.6f, 0.2f, 1f);      // amber
                case "dead":        return new Color(0.5f, 0.3f, 0.3f, 1f);      // dark gray-red
                case "wanted":      return new Color(1f, 0.4f, 0.1f, 1f);        // red-orange
                // Strategic (advanced)
                case "assassination target": return new Color(0.85f, 0.1f, 0.1f, 1f); // deep red
                case "claimant":    return new Color(0.9f, 0.75f, 0.2f, 1f);     // royal gold
                case "ransom target": return new Color(0.8f, 0.65f, 0.15f, 1f);  // amber gold
                case "dangerous":   return new Color(0.95f, 0.3f, 0.1f, 1f);     // fiery red
                // Auto-generated tags (dimmer versions to distinguish from manual)
                case "auto: enemy":     return new Color(0.8f, 0.3f, 0.3f, 0.9f);  // dim red
                case "auto: friend":    return new Color(0.3f, 0.7f, 0.3f, 0.9f);  // dim green
                case "auto: dangerous": return new Color(0.8f, 0.4f, 0.15f, 0.9f); // dim orange
                case "auto: rich":      return new Color(0.75f, 0.65f, 0.25f, 0.9f); // dim gold
                case "auto: prisoner":  return new Color(0.65f, 0.5f, 0.2f, 0.9f);  // dim amber
                case "auto: at war":    return new Color(0.9f, 0.25f, 0.15f, 0.9f); // dim fiery red
                case "auto: ally clan": return new Color(0.3f, 0.65f, 0.3f, 0.9f);  // dim green
                case "auto: mercenary": return new Color(0.7f, 0.55f, 0.2f, 0.9f);  // dim bronze
                case "auto: wounded":   return new Color(0.8f, 0.4f, 0.4f, 0.9f);   // dim pink-red
                case "auto: ruler":     return new Color(0.85f, 0.75f, 0.3f, 0.9f); // dim royal gold
                case "auto: nearby":    return new Color(0.5f, 0.7f, 0.9f, 0.9f);   // dim sky blue
                case "auto: under siege": return new Color(0.9f, 0.35f, 0.1f, 0.9f); // dim siege red
                // Default
                default:            return new Color(0.75f, 0.75f, 0.75f, 1f);   // light gray
            }
        }

        /// <summary>
        /// Returns a unicode icon prefix for known tag names.
        /// Icons are chosen to fit Bannerlord's medieval theme.
        /// </summary>
        internal static string GetTagIcon(string tag)
        {
            string lower = tag.ToLowerInvariant();
            switch (lower)
            {
                // Hostile
                case "enemy":       return "\u2694 "; // ⚔
                case "rival":       return "\u2694 "; // ⚔
                case "traitor":     return "\u2620 "; // ☠
                case "hostile":     return "\u2694 "; // ⚔
                // Friendly
                case "ally":        return "\u2764 "; // ❤  (closest to shield)
                case "friend":      return "\u2764 "; // ❤
                case "trusted":     return "\u2605 "; // ★
                // Strategic
                case "target":      return "\u25CE "; // ◎
                case "spy":         return "\u25C8 "; // ◈
                case "recruit":     return "\u2691 "; // ⚑
                case "vassal":
                case "future vassal": return "\u2691 "; // ⚑
                // Economy
                case "trade hub":   return "\u2696 "; // ⚖
                case "rich":        return "\u2666 "; // ♦
                // Character
                case "honorable":   return "\u2655 "; // ♕
                case "dishonorable": return "\u2639 "; // ☹
                case "good commander": return "\u2654 "; // ♔
                case "warlord":     return "\u2654 "; // ♔
                case "raid leader": return "\u2620 "; // ☠
                // Governance
                case "governor":    return "\u2302 "; // ⌂
                case "future governor": return "\u2302 "; // ⌂
                // Family
                case "marriage candidate": return "\u2661 "; // ♡
                case "family":      return "\u263A "; // ☺
                // Status
                case "prisoner":    return "\u26D4 "; // ⛔
                case "dead":        return "\u2620 "; // ☠
                case "wanted":      return "\u26A0 "; // ⚠
                // Strategic (advanced)
                case "assassination target": return "\u2620 "; // ☠
                case "claimant":    return "\u2655 "; // ♕
                case "ransom target": return "\u2666 "; // ♦
                case "dangerous":   return "\u26A0 "; // ⚠
                // Auto-generated tags
                case "auto: enemy":     return "\u2694 "; // ⚔
                case "auto: friend":    return "\u2764 "; // ❤
                case "auto: dangerous": return "\u26A0 "; // ⚠
                case "auto: rich":      return "\u2666 "; // ♦
                case "auto: prisoner":  return "\u26D4 "; // ⛔
                case "auto: at war":    return "\u2694 "; // ⚔
                case "auto: ally clan": return "\u2691 "; // ⚑
                case "auto: mercenary": return "\u2696 "; // ⚖
                case "auto: wounded":   return "\u2620 "; // ☠
                case "auto: ruler":     return "\u2655 "; // ♕
                case "auto: nearby":    return "\u25CE "; // ◎
                case "auto: under siege": return "\u2694 "; // ⚔
                default:            return "\u2022 "; // •
            }
        }

        /// <summary>
        /// Returns a sort priority for tags. Lower number = higher priority (shown first).
        /// </summary>
        internal static int GetTagSortPriority(string tag)
        {
            string lower = tag.ToLowerInvariant();
            switch (lower)
            {
                // Highest priority: immediate threats
                case "target":      return 0;
                case "assassination target": return 1;
                case "enemy":       return 2;
                case "hostile":     return 3;
                case "dangerous":   return 4;
                case "wanted":      return 5;
                case "traitor":     return 6;
                case "rival":       return 7;
                // High priority: allies
                case "ally":        return 10;
                case "friend":      return 11;
                case "trusted":     return 12;
                case "family":      return 13;
                // Medium: strategic
                case "spy":         return 20;
                case "recruit":     return 21;
                case "vassal":
                case "future vassal": return 22;
                case "claimant":    return 23;
                case "ransom target": return 24;
                case "marriage candidate": return 25;
                // Lower: economy/governance
                case "trade hub":   return 30;
                case "rich":        return 31;
                case "governor":    return 32;
                case "future governor": return 33;
                // Character traits
                case "warlord":     return 40;
                case "raid leader": return 41;
                case "good commander": return 42;
                case "honorable":   return 43;
                case "dishonorable": return 44;
                // Status
                case "prisoner":    return 50;
                case "dead":        return 51;
                // Auto-generated tags (shown after manual tags)
                case "auto: enemy":     return 60;
                case "auto: friend":    return 61;
                case "auto: dangerous": return 62;
                case "auto: rich":      return 63;
                case "auto: prisoner":  return 64;
                case "auto: at war":    return 65;
                case "auto: ally clan": return 66;
                case "auto: mercenary": return 67;
                case "auto: wounded":   return 68;
                case "auto: ruler":     return 69;
                case "auto: nearby":    return 70;
                case "auto: under siege": return 71;
                // Default: user-defined tags
                default:            return 100;
            }
        }

        private static void SetBrushColor(Brush brush, float r, float g, float b, float a)
        {
            if (brush == null) return;
            try
            {
                var color = new Color(r, g, b, a);
                var brushType = brush.GetType();
                PropertyInfo fontColorProp;
                if (_cachedFontColorPropDirectType == brushType)
                {
                    fontColorProp = _cachedFontColorPropDirect;
                }
                else
                {
                    fontColorProp = brushType.GetProperty("FontColor", AllFlags);
                    _cachedFontColorPropDirectType = brushType;
                    _cachedFontColorPropDirect = fontColorProp;
                }
                if (fontColorProp != null && fontColorProp.CanWrite)
                {
                    fontColorProp.SetValue(brush, color);
                    return;
                }
                foreach (var layersPropName in new[] { "TextLayers", "Layers" })
                {
                    string layersCacheKey = brushType.FullName + "." + layersPropName;
                    PropertyInfo layersProp;
                    if (!_cachedBrushLayersProps.TryGetValue(layersCacheKey, out layersProp))
                    {
                        layersProp = brushType.GetProperty(layersPropName, AllFlags);
                        _cachedBrushLayersProps[layersCacheKey] = layersProp;
                    }
                    if (layersProp == null) continue;
                    var layers = layersProp.GetValue(brush) as System.Collections.IEnumerable;
                    if (layers == null) continue;
                    foreach (var layer in layers)
                    {
                        var layerType = layer.GetType();
                        PropertyInfo colorProp;
                        if (!_cachedLayerColorProps.TryGetValue(layerType, out colorProp))
                        {
                            colorProp = layerType.GetProperty("Color", AllFlags);
                            _cachedLayerColorProps[layerType] = colorProp;
                        }
                        if (colorProp != null && colorProp.CanWrite)
                            colorProp.SetValue(layer, color);
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("TagWidgetInjector: SetBrushColor failed: " + ex.ToString()); }
        }

        /// <summary>
        /// Scales the font size on all text layers of a brush by the given multiplier.
        /// Brush font size is stored in BrushLayer/TextLayer objects accessible via Brush.Layers or Brush.TextLayers.
        /// </summary>
        private static void ScaleBrushFontSize(Brush brush, float multiplier)
        {
            if (brush == null) return;
            try
            {
                var brushType = brush.GetType();
                PropertyInfo fontSizeProp;
                if (_cachedBrushFontSizePropDirectType == brushType)
                {
                    fontSizeProp = _cachedBrushFontSizePropDirect;
                }
                else
                {
                    fontSizeProp = brushType.GetProperty("FontSize", AllFlags);
                    _cachedBrushFontSizePropDirectType = brushType;
                    _cachedBrushFontSizePropDirect = fontSizeProp;
                }
                if (fontSizeProp != null && fontSizeProp.CanRead && fontSizeProp.CanWrite
                    && fontSizeProp.PropertyType == typeof(int))
                {
                    int current = (int)fontSizeProp.GetValue(brush);
                    if (current > 0)
                    {
                        fontSizeProp.SetValue(brush, (int)(current * multiplier));
                        MCMSettings.DebugLog("TagWidget: scaled Brush.FontSize " + current + " -> " + (int)(current * multiplier));
                        return;
                    }
                }

                foreach (var layersPropName in new[] { "TextLayers", "Layers" })
                {
                    string layersCacheKey = brushType.FullName + "." + layersPropName;
                    PropertyInfo layersProp;
                    if (!_cachedBrushLayersProps.TryGetValue(layersCacheKey, out layersProp))
                    {
                        layersProp = brushType.GetProperty(layersPropName, AllFlags);
                        _cachedBrushLayersProps[layersCacheKey] = layersProp;
                    }
                    if (layersProp == null) continue;
                    var layers = layersProp.GetValue(brush) as System.Collections.IEnumerable;
                    if (layers == null) continue;

                    foreach (var layer in layers)
                    {
                        var layerType = layer.GetType();
                        PropertyInfo fsProp;
                        if (!_cachedLayerFontSizeProps.TryGetValue(layerType, out fsProp))
                        {
                            fsProp = layerType.GetProperty("FontSize", AllFlags);
                            _cachedLayerFontSizeProps[layerType] = fsProp;
                        }
                        if (fsProp != null && fsProp.CanRead && fsProp.CanWrite)
                        {
                            object val = fsProp.GetValue(layer);
                            if (val is int intVal && intVal > 0)
                            {
                                fsProp.SetValue(layer, (int)(intVal * multiplier));
                                MCMSettings.DebugLog("TagWidget: scaled layer FontSize " + intVal + " -> " + (int)(intVal * multiplier));
                            }
                            else if (val is float floatVal && floatVal > 0)
                            {
                                fsProp.SetValue(layer, floatVal * multiplier);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("TagWidget: ScaleBrushFontSize failed: " + ex.ToString());
            }
        }

        // ─────────── Layer & Root Detection ───────────

        private static GauntletLayer FindEncyclopediaLayer(ScreenBase topScreen)
        {
            // 0. Use the layer captured by EncyclopediaLayerCapturePatch (most reliable)
            var capturedLayer = EncyclopediaPageTracker.EncyclopediaLayerRef;
            if (capturedLayer is GauntletLayer captured)
            {
                foreach (var layer in topScreen.Layers)
                {
                    if (object.ReferenceEquals(layer, captured))
                    {
                        MCMSettings.DebugLog("TagWidget: using captured layer reference");
                        return captured;
                    }
                }
                EncyclopediaPageTracker.EncyclopediaLayerRef = null;
            }

            // 1. Try direct reference from encyclopedia manager
            object encManager = EncyclopediaPageTracker.EncyclopediaManagerRef;
            if (encManager != null)
            {
                var managerType = encManager.GetType();
                string backingFieldKey = managerType.FullName + ".<Layer>k__BackingField";
                FieldInfo layerField;
                if (!_cachedEncManagerFields.TryGetValue(backingFieldKey, out layerField))
                {
                    var encType = managerType;
                    while (encType != null && encType != typeof(object))
                    {
                        layerField = encType.GetField("<Layer>k__BackingField", AllFlags);
                        if (layerField != null) break;
                        encType = encType.BaseType;
                    }
                    _cachedEncManagerFields[backingFieldKey] = layerField;
                }
                if (layerField != null)
                {
                    var layerVal = layerField.GetValue(encManager);
                    if (layerVal is GauntletLayer gl)
                    {
                        MCMSettings.DebugLog("TagWidget: got layer from manager");
                        return gl;
                    }
                    MCMSettings.DebugLog("TagWidget: manager Layer field found but value is "
                        + (layerVal?.GetType().Name ?? "null"));
                }

                string layerPropKey = managerType.FullName + ".Layer";
                PropertyInfo layerProp;
                if (!_cachedEncManagerProps.TryGetValue(layerPropKey, out layerProp))
                {
                    var encType = managerType;
                    while (encType != null && encType != typeof(object))
                    {
                        layerProp = encType.GetProperty("Layer", AllFlags);
                        if (layerProp != null) break;
                        encType = encType.BaseType;
                    }
                    _cachedEncManagerProps[layerPropKey] = layerProp;
                }
                if (layerProp != null)
                {
                    var layerVal = layerProp.GetValue(encManager);
                    if (layerVal is GauntletLayer gl)
                    {
                        MCMSettings.DebugLog("TagWidget: got layer from manager property");
                        return gl;
                    }
                }

                string directFieldKey = managerType.FullName + "._layer";
                FieldInfo directField;
                if (!_cachedEncManagerFields.TryGetValue(directFieldKey, out directField))
                {
                    directField = managerType.GetField("_layer", AllFlags)
                        ?? managerType.GetField("_gauntletLayer", AllFlags);
                    _cachedEncManagerFields[directFieldKey] = directField;
                }
                if (directField != null)
                {
                    var layerVal = directField.GetValue(encManager);
                    if (layerVal is GauntletLayer gl)
                    {
                        MCMSettings.DebugLog("TagWidget: got layer from manager _layer field");
                        return gl;
                    }
                }

                MCMSettings.DebugLog("TagWidget: manager type=" + encManager.GetType().Name
                    + " — could not extract layer");
            }
            else
            {
                MCMSettings.DebugLog("TagWidget: no EncyclopediaManagerRef");
            }

            // 2. Fallback: search layers for one containing encyclopedia-specific content.
            //    The encyclopedia layer contains the hint text "[E Edit" or the page name text.
            var layers = topScreen.Layers;
            for (int i = 0; i < layers.Count; i++)
            {
                if (!(layers[i] is GauntletLayer gl)) continue;
                Widget root = GetLayerRootWidget(gl);
                if (root == null) continue;
                int desc = CountDescendants(root);
                if (desc < 10 || desc > 2000) continue;

                // Check if this layer contains encyclopedia hint text
                if (FindWidgetByTextContent(root, "E Edit") != null
                    || FindWidgetByTextContent(root, "G Tags") != null)
                {
                    MCMSettings.DebugLog("TagWidget: found encyclopedia layer[" + i
                        + "] by content match (" + desc + " descendants)");
                    EncyclopediaPageTracker.EncyclopediaLayerRef = gl;
                    return gl;
                }
            }

            MCMSettings.DebugLog("TagWidget: no encyclopedia layer found in any method");
            return null;
        }

        private static Widget GetLayerRootWidget(GauntletLayer layer)
        {
            try
            {
                var type = typeof(GauntletLayer);

                foreach (var fieldName in new[] { "_gauntletUIContext", "_uiContext", "_context" })
                {
                    string cacheKey = type.FullName + "." + fieldName;
                    FieldInfo field;
                    if (!_cachedGauntletLayerFields.TryGetValue(cacheKey, out field))
                    {
                        field = type.GetField(fieldName, AllFlags);
                        _cachedGauntletLayerFields[cacheKey] = field;
                    }
                    if (field != null)
                    {
                        var uiCtx = field.GetValue(layer) as UIContext;
                        if (uiCtx?.EventManager?.Root != null)
                            return uiCtx.EventManager.Root;
                    }
                }

                foreach (var propName in new[] { "UIContext", "_gauntletUIContext" })
                {
                    string cacheKey = type.FullName + "." + propName;
                    PropertyInfo prop;
                    if (!_cachedGauntletLayerProps.TryGetValue(cacheKey, out prop))
                    {
                        prop = type.GetProperty(propName, AllFlags);
                        _cachedGauntletLayerProps[cacheKey] = prop;
                    }
                    if (prop != null)
                    {
                        var uiCtx = prop.GetValue(layer) as UIContext;
                        if (uiCtx?.EventManager?.Root != null)
                            return uiCtx.EventManager.Root;
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("TagWidgetInjector: GetLayerRootWidget failed: " + ex.ToString()); }
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

        /// <summary>
        /// Gets the tag font scale from MCM settings (default 1.3x = 130%).
        /// </summary>
        private static float GetTagFontScale()
        {
            try
            {
                var s = MCMSettings.Instance;
                if (s != null)
                {
                    float scale = s.TagFontScalePercent / 100f;
                    MCMSettings.DebugLog("TagWidget: GetTagFontScale() = " + scale + " (" + s.TagFontScalePercent + "%)");
                    return scale;
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("TagWidgetInjector: GetTagFontScale failed: " + ex.ToString()); }
            return 1.3f;
        }
    }
}
