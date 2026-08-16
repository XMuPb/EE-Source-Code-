using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Injects a "Lore" section into the encyclopedia hero page widget tree,
    /// placed after the Info (Stats) section and before Friends/Enemies.
    /// Creates a visual panel with a header and flowing text for narrative
    /// fields like Backstory and Rumors.
    /// </summary>
    public static class LoreSectionInjector
    {
        private static readonly BindingFlags AllFlags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // Track injected widgets so we can remove them on page refresh
        private static readonly List<Widget> _injectedWidgets = new List<Widget>();

        // Retry state — widget tree may not be ready on back/forward navigation.
        // _pendingHeroId volatile so main thread sees latest payload after
        // observing _retryPending=true (Theme 1 fix: round 2 finding LC-3).
        private static volatile string _pendingHeroId;
        private static int _retryCount;
        private static System.Threading.Timer _retryTimer;
        private static volatile bool _retryPending;
        private const int MaxRetries = 10;
        private const int RetryDelayMs = 150;

        private const float DefaultContentMarginLeft = 25f;
        private const float ContentMarginPadding = 5f;
        private const float FieldSpacerHeight = 10f;
        private const float LabelValueGapHeight = 3f;
        private const float DeleteButtonMarginLeft = 8f;
        private const float AddButtonMarginLeft = 10f;
        // 82 (was 90): the scrollpanel's scrollbar + right margin narrow the text column, so
        // 90-char manual lines re-wrapped in the TextWidget and orphaned the last word
        // ("the"/"with" alone). 82 keeps every line inside the narrower scroll box.
        private const int WordWrapMaxChars = 82;
        // A long lore topic (Backstory, etc.) is capped to this height and scrolls in place, so a
        // huge write-up doesn't stretch the whole encyclopedia page. ~11 lines at the estimate below.
        private const float LoreTopicMaxHeight = 264f;
        private const float LoreLineHeightEstimate = 24f;
        private const int MinDescendantsForContent = 50;
        private const int MinLayerDescendants = 10;
        private const int MaxLayerDescendants = 2000;
        private const int TextSnippetMaxLength = 40;
        private const int MaxWidgetTreeDepth = 20;
        private const int MaxSearchDepth = 15;
        private const int MaxContainsTypeDepth = 12;
        private const int MaxBrushSearchDepth = 10;
        private const float AddSpacerHeightWithFields = 8f;
        private const float AddSpacerHeightEmpty = 4f;
        private const int MaxColorValue = 255;

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
                // Abort retry if encyclopedia was closed (e.g. failed navigation recovery)
                if (!EncyclopediaPageTracker.IsEncyclopediaOpen())
                {
                    _retryCount = MaxRetries; // prevent further retries
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
                try { t.Dispose(); } catch (Exception ex) { MCMSettings.DebugLog("LoreSectionInjector: timer dispose failed: " + ex.ToString()); }
        }

        /// <summary>
        /// Injects narrative fields (Backstory, Rumors, etc.) as a Lore section
        /// into the hero encyclopedia page widget tree.
        /// Call this from the HeroPageRefreshPatch Postfix after the page is built.
        /// </summary>
        public static void InjectLoreSection(string heroId)
        {
            DisposeRetryTimer();
            _retryPending = false;
            _pendingHeroId = heroId;
            _retryCount = 0;
            DoInject(heroId);
        }

        private static void DoInject(string heroId)
        {
            try
            {
                RemoveOldWidgets();

                if (EncyclopediaEditBehavior.Instance == null) return;

                // Collect narrative fields that have content
                // Each entry: (fieldKey, label, value)
                var fields = new List<(string fieldKey, string label, string value)>();
                var emptyFieldKeys = new List<string>();
                string pageType = EncyclopediaPageTracker.CurrentPageType ?? "Hero";
                string[] fieldKeys = EncyclopediaEditBehavior.GetFieldKeysForPageType(pageType);
                foreach (string fieldKey in fieldKeys)
                {
                    string value = EncyclopediaEditBehavior.Instance.GetHeroInfoField(fieldKey, heroId);
                    string label = Localization.L("info_field_" + fieldKey);
                    if (string.IsNullOrEmpty(value))
                    {
                        // Fall back to role/culture/default template so Lords, Merchants,
                        // Wanderers, Gang Leaders, and Preachers show placeholder lore.
                        try
                        {
                            string tmpl = EncyclopediaEditPopup.ResolveFieldTemplate(fieldKey, heroId);
                            if (!string.IsNullOrEmpty(tmpl))
                            {
                                fields.Add((fieldKey, label, NormalizeLoreText(tmpl)));
                                continue;
                            }
                        }
                        catch (Exception ex) { MCMSettings.DebugLog("LoreSectionInjector: template resolution failed for " + fieldKey + ": " + ex.ToString()); }
                        emptyFieldKeys.Add(fieldKey);
                    }
                    else
                        fields.Add((fieldKey, label, NormalizeLoreText(value)));
                }

                bool hasNoFields = fields.Count == 0;

                // Find the encyclopedia GauntletLayer
                var topScreen = ScreenManager.TopScreen;
                if (topScreen == null) return;

                bool layerWasSizeFallback;
                GauntletLayer encLayer = FindEncyclopediaLayer(topScreen, out layerWasSizeFallback);
                if (encLayer == null)
                {
                    // Before retrying, check if the encyclopedia is still open.
                    // If it was closed (e.g. by a failed navigation recovery), stop retrying.
                    if (!EncyclopediaPageTracker.IsEncyclopediaOpen())
                    {
                        MCMSettings.DebugLog("LoreSection: encyclopedia closed, aborting injection");
                        return;
                    }
                    if (_retryCount < MaxRetries)
                    {
                        _retryCount++;
                        MCMSettings.DebugLog("LoreSection: no encyclopedia layer found, scheduling retry #"
                            + _retryCount + " in " + RetryDelayMs + "ms");
                        // Dispose previous timer first to prevent leaks on rapid navigation
                        // (Theme 2: round 2 finding LC-1).
                        _retryTimer?.Dispose();
                        _retryTimer = new System.Threading.Timer(_ =>
                        {
                            _retryPending = true;
                        }, null, RetryDelayMs, System.Threading.Timeout.Infinite);
                    }
                    else
                    {
                        MCMSettings.DebugLog("LoreSection: no encyclopedia layer found after " + MaxRetries + " retries");
                    }
                    return;
                }

                Widget layerRoot = GetLayerRootWidget(encLayer);
                if (layerRoot == null)
                {
                    MCMSettings.DebugLog("LoreSection: layer root is null");
                    return;
                }

                UIContext uiContext = layerRoot.EventManager?.Context as UIContext;
                if (uiContext == null)
                {
                    MCMSettings.DebugLog("LoreSection: cannot get UIContext");
                    return;
                }

                // Try to find the actual encyclopedia content widget (not settlement overlay, etc.)
                Widget encWindow = FindEncyclopediaContentWidget(layerRoot)
                                ?? FindLargestChild(layerRoot);
                if (encWindow == null)
                {
                    MCMSettings.DebugLog("LoreSection: no encyclopedia window found");
                    return;
                }

                MCMSettings.DebugLog("LoreSection: encWindow type=" + encWindow.GetType().Name
                    + " children=" + encWindow.ChildCount
                    + " descendants=" + CountDescendants(encWindow));

                Widget insertionParent = null;
                int insertionIndex = -1;

                // Try to find the page content container — search encWindow first, then layerRoot
                if (FindInsertionPoint(encWindow, out insertionParent, out insertionIndex))
                {
                    MCMSettings.DebugLog("LoreSection: found insertion point at index " + insertionIndex
                        + " in " + insertionParent.GetType().Name + " (children=" + insertionParent.ChildCount + ")");

                    BuildAndInsertLoreSection(uiContext, insertionParent, insertionIndex, heroId, fields, emptyFieldKeys);
                }
                else
                {
                    MCMSettings.DebugLog("LoreSection: could not find insertion point in encWindow, trying layerRoot");

                    // Retry from the full layer root — the widget tree structure may differ
                    // on subsequent visits (e.g., back/forward navigation)
                    if (FindInsertionPoint(layerRoot, out insertionParent, out insertionIndex))
                    {
                        MCMSettings.DebugLog("LoreSection: found insertion point via layerRoot at index "
                            + insertionIndex + " in " + insertionParent.GetType().Name
                            + " (children=" + insertionParent.ChildCount + ")");
                        BuildAndInsertLoreSection(uiContext, insertionParent, insertionIndex, heroId, fields, emptyFieldKeys);
                    }
                    else
                    {
                        // Try searching ALL GauntletLayers for the encyclopedia content
                        MCMSettings.DebugLog("LoreSection: layerRoot search also failed, trying all layers");
                        bool foundInOtherLayer = false;
                        var allLayers = topScreen.Layers;
                        MCMSettings.DebugLog("LoreSection: scanning " + allLayers.Count + " layers for insertion point");
                        for (int li = 0; li < allLayers.Count; li++)
                        {
                            if (!(allLayers[li] is GauntletLayer gl)) continue;
                            Widget altRoot = GetLayerRootWidget(gl);
                            if (altRoot == null || altRoot == layerRoot) continue;
                            int altDesc = CountDescendants(altRoot);
                            MCMSettings.DebugLog(new StringBuilder("LoreSection: alt layer ")
                                .Append(li).Append(" type=").Append(altRoot.GetType().Name)
                                .Append(" descendants=").Append(altDesc).ToString());
                            // Skip non-encyclopedia layers (e.g. Clan/Party panels)
                            if (!ContainsWidgetType(altRoot, "EncyclopediaDividerButtonWidget")) continue;
                            if (FindInsertionPoint(altRoot, out insertionParent, out insertionIndex))
                            {
                                UIContext altCtx = altRoot.EventManager?.Context as UIContext;
                                if (altCtx != null) uiContext = altCtx;
                                MCMSettings.DebugLog("LoreSection: found insertion point in alternate layer " + li
                                    + " at index " + insertionIndex + " in " + insertionParent.GetType().Name);
                                BuildAndInsertLoreSection(uiContext, insertionParent, insertionIndex, heroId, fields, emptyFieldKeys);
                                foundInOtherLayer = true;
                                break;
                            }
                        }

                        if (!foundInOtherLayer)
                        {
                            // If the widget tree is too small, the encyclopedia page hasn't
                            // fully loaded yet — retry instead of falling back to a random panel.
                            int encDesc = CountDescendants(encWindow);

                            // If the layer was selected via size-based fallback (no content
                            // match for Friends/Enemies/Info), the real encyclopedia layer
                            // may not have loaded yet.  Retry instead of injecting into the
                            // wrong panel — the content-matched layer will appear later.
                            if (layerWasSizeFallback && _retryCount < MaxRetries)
                            {
                                _retryCount++;
                                // Clear the stale size-fallback reference so the next attempt
                                // re-scans all layers and can find the real content layer.
                                EncyclopediaPageTracker.EncyclopediaLayerRef = null;
                                MCMSettings.DebugLog("LoreSection: size-fallback layer had no insertion point, "
                                    + "clearing cached layer and scheduling retry #" + _retryCount
                                    + " in " + RetryDelayMs + "ms");
                                _retryTimer = new System.Threading.Timer(_ =>
                                {
                                    _retryPending = true;
                                }, null, RetryDelayMs, System.Threading.Timeout.Infinite);
                            }
                            else if (encDesc < MinDescendantsForContent && _retryCount < MaxRetries)
                            {
                                _retryCount++;
                                MCMSettings.DebugLog("LoreSection: tree too small (descendants=" + encDesc
                                    + "), scheduling retry #" + _retryCount + " in " + RetryDelayMs + "ms");
                                _retryTimer = new System.Threading.Timer(_ =>
                                {
                                    _retryPending = true;
                                }, null, RetryDelayMs, System.Threading.Timeout.Infinite);
                            }
                            else if (encDesc >= MinDescendantsForContent)
                            {
                                MCMSettings.DebugLog("LoreSection: all layer searches failed, trying mod-agnostic structural anchor");

                                // CHANGE 2 (2026-05-27 ROT fix): Mod-agnostic structural anchor finder.
                                // Works on vanilla, ROT, and any future modded prefab because it relies
                                // on STRUCTURE (child count, width signal), not brush/Id names mods rename.
                                Widget structAnchor = FindBestStructuralAnchor(encWindow)
                                                      ?? FindBestStructuralAnchor(layerRoot);
                                if (structAnchor != null && IsAcceptableInsertionContainer(structAnchor))
                                {
                                    MCMSettings.DebugLog("LoreSection: using structural anchor ("
                                        + structAnchor.GetType().Name + ", children=" + structAnchor.ChildCount
                                        + ", sw=" + structAnchor.SuggestedWidth + ")");
                                    BuildAndInsertLoreSection(uiContext, structAnchor, structAnchor.ChildCount, heroId, fields, emptyFieldKeys);
                                }
                                else
                                {
                                    // CHANGE 1: Safety bail-out. Legacy content-panel fallback kept for
                                    // back-compat but now gated by IsAcceptableInsertionContainer so we
                                    // never render a warped section into a narrow side panel
                                    // (root cause of the ROT settlement bug 2026-05-27).
                                    Widget contentPanel = FindMainContentPanel(encWindow)
                                                       ?? FindMainContentPanel(layerRoot);
                                    if (contentPanel != null && IsAcceptableInsertionContainer(contentPanel))
                                    {
                                        MCMSettings.DebugLog("LoreSection: using legacy content-panel fallback ("
                                            + contentPanel.GetType().Name + ", children=" + contentPanel.ChildCount + ")");
                                        BuildAndInsertLoreSection(uiContext, contentPanel, contentPanel.ChildCount, heroId, fields, emptyFieldKeys);
                                    }
                                    else
                                    {
                                        MCMSettings.DebugLog("LoreSection: SKIPPING injection — no acceptable container (modded prefab?). Better invisible than warped.");
                                    }
                                }
                            }
                            else if (_retryCount < MaxRetries)
                            {
                                _retryCount++;
                                MCMSettings.DebugLog("LoreSection: tree not ready, scheduling retry #"
                                    + _retryCount + " in " + RetryDelayMs + "ms");
                                _retryTimer = new System.Threading.Timer(_ =>
                                {
                                    _retryPending = true;
                                }, null, RetryDelayMs, System.Threading.Timeout.Infinite);
                            }
                            else
                            {
                                MCMSettings.DebugLog("LoreSection: all strategies failed after "
                                    + MaxRetries + " retries");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("LoreSection: error: " + ex.ToString());
            }
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
                catch (Exception ex) { MCMSettings.DebugLog("LoreSectionInjector: failed to remove widget: " + ex.ToString()); }
            }
            _injectedWidgets.Clear();
        }

        // ─────────── Widget Creation ───────────

        /// <summary>
        /// Holds references to widgets and brushes extracted from a native section header.
        /// </summary>
        public class NativeSectionParts
        {
            public Widget ReferenceHeader;     // EncyclopediaDividerButtonWidget
            public Widget ReferencePlacement;  // ListPanel #PlacementListPanel
            public Widget NativeIndicator;     // BrushWidget #CollapseIndicator
            public Widget NativeTitle;         // TextWidget #Text
            public Widget NativeLine;          // ImageWidget
            public Brush IndicatorBrush;
            public Brush HeaderTextBrush;
            public Brush LineBrush;
        }

        /// <summary>
        /// Extracts widget references and brushes from an existing EncyclopediaDividerButtonWidget.
        /// </summary>
        public static NativeSectionParts ExtractNativeSectionParts(Widget parent)
        {
            var parts = new NativeSectionParts();

            for (int i = 0; i < parent.ChildCount; i++)
            {
                Widget child = parent.GetChild(i);
                if (child.GetType().Name != "EncyclopediaDividerButtonWidget") continue;

                parts.ReferenceHeader = child;
                for (int j = 0; j < child.ChildCount; j++)
                {
                    Widget sub = child.GetChild(j);
                    if (sub.GetType().Name != "ListPanel") continue;

                    parts.ReferencePlacement = sub;
                    for (int k = 0; k < sub.ChildCount; k++)
                    {
                        Widget item = sub.GetChild(k);
                        if (item.Id == "CollapseIndicator" && item is BrushWidget bw1)
                        {
                            parts.NativeIndicator = item;
                            parts.IndicatorBrush = bw1.ReadOnlyBrush;
                        }
                        else if (item.Id == "Text" && item is TextWidget tw)
                        {
                            parts.NativeTitle = item;
                            parts.HeaderTextBrush = tw.ReadOnlyBrush;
                        }
                        else if (item.GetType().Name == "ImageWidget" && item is BrushWidget bw2)
                        {
                            parts.NativeLine = item;
                            parts.LineBrush = bw2.ReadOnlyBrush;
                        }
                    }
                    break;
                }
                break;
            }
            return parts;
        }

        /// <summary>
        /// Copies common layout properties from a source widget to a target widget.
        /// </summary>
        public static void CopyLayoutProperties(Widget source, Widget target)
        {
            if (source == null || target == null) return;
            target.WidthSizePolicy = source.WidthSizePolicy;
            target.HeightSizePolicy = source.HeightSizePolicy;
            target.SuggestedWidth = source.SuggestedWidth;
            target.SuggestedHeight = source.SuggestedHeight;
            target.MaxWidth = source.MaxWidth;
            target.MaxHeight = source.MaxHeight;
            target.MinWidth = source.MinWidth;
            target.MinHeight = source.MinHeight;
            target.MarginTop = source.MarginTop;
            target.MarginBottom = source.MarginBottom;
            target.MarginLeft = source.MarginLeft;
            target.MarginRight = source.MarginRight;
            target.HorizontalAlignment = source.HorizontalAlignment;
            target.VerticalAlignment = source.VerticalAlignment;
            target.IsEnabled = source.IsEnabled;
            target.IsVisible = source.IsVisible;
        }

        /// <summary>
        /// Forces a BrushWidget to activate its brush sprite layer by copying the visual state
        /// from a reference widget, or falling back to common state names.
        ///
        /// The problem: a standalone BrushWidget (not parented by a ButtonWidget) never gets
        /// its visual state driven, so the BrushRenderer has no active sprite layer and renders
        /// nothing. We fix this by invoking SetState via reflection.
        /// </summary>
        public static void ForceWidgetBrushState(BrushWidget target, Widget nativeSource)
        {
            try
            {
                // 1. Read the native widget's current state
                string nativeState = null;
                var curStateProp = nativeSource.GetType().GetProperty("CurrentState",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (curStateProp != null && curStateProp.CanRead)
                    nativeState = curStateProp.GetValue(nativeSource) as string;

                MCMSettings.DebugLog("LoreSection: native indicator CurrentState='"
                    + (nativeState ?? "null") + "'");

                // 2. Find SetState on the target
                var setStateMethod = target.GetType().GetMethod("SetState",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(string) }, null);

                if (setStateMethod != null)
                {
                    // Try the native state first, then common fallbacks
                    string[] candidates = !string.IsNullOrEmpty(nativeState)
                        ? new[] { nativeState, "Default", "Normal", "Pressed" }
                        : new[] { "Default", "Normal", "Pressed" };

                    foreach (string state in candidates)
                    {
                        try
                        {
                            setStateMethod.Invoke(target, new object[] { state });
                            MCMSettings.DebugLog("LoreSection: arrow SetState('" + state + "') succeeded");
                            return;
                        }
                        catch (Exception ex) { MCMSettings.DebugLog("LoreSectionInjector: SetState('" + state + "') failed: " + ex.ToString()); }
                    }
                }
                else
                {
                    MCMSettings.DebugLog("LoreSection: SetState method not found on BrushWidget");
                }

                // 3. Fallback: try setting CurrentState property directly
                var targetStateProp = target.GetType().GetProperty("CurrentState",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (targetStateProp != null && targetStateProp.CanWrite)
                {
                    string state = nativeState ?? "Default";
                    targetStateProp.SetValue(target, state);
                    MCMSettings.DebugLog("LoreSection: arrow CurrentState set to '" + state + "'");
                    return;
                }

                // 4. Last resort: copy the BrushRenderer state from native to target
                var rendererField = typeof(BrushWidget).GetField("_brushRenderer",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (rendererField == null)
                    rendererField = typeof(BrushWidget).GetField("BrushRenderer",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (rendererField != null)
                {
                    MCMSettings.DebugLog("LoreSection: found BrushRenderer field '"
                        + rendererField.Name + "'");
                    // Log its type for future debugging
                    var rendererVal = rendererField.GetValue(target);
                    if (rendererVal != null)
                    {
                        MCMSettings.DebugLog("LoreSection: BrushRenderer type="
                            + rendererVal.GetType().FullName);
                        // Try calling Render or SetState on the renderer itself
                        var rSetState = rendererVal.GetType().GetMethod("SetState",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (rSetState != null)
                        {
                            rSetState.Invoke(rendererVal, new object[] { nativeState ?? "Default" });
                            MCMSettings.DebugLog("LoreSection: BrushRenderer.SetState succeeded");
                        }
                    }
                }
                else
                {
                    MCMSettings.DebugLog("LoreSection: BrushRenderer field not found");
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("LoreSection: ForceWidgetBrushState error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Hooks a click handler on the header ButtonWidget to toggle the content
        /// container's visibility and rotate the arrow indicator.
        ///
        /// GauntletUI's ButtonWidget fires click events through the EventFire event
        /// (Action&lt;Widget, string, object[]&gt;) with eventName="Click".
        /// The header must have DoNotPassEventsToChildren=true so child widgets
        /// (text, arrow) don't steal mouse press events from the parent button.
        /// </summary>
        public static void HookCollapseToggle(Widget headerWidget, Widget contentContainer,
            BrushWidget arrow, Widget nativeHeader)
        {
            try
            {
                // Log native header's event settings for comparison
                if (nativeHeader != null)
                {
                    MCMSettings.DebugLog("LoreSection: native header DoNotPassEventsToChildren="
                        + nativeHeader.DoNotPassEventsToChildren
                        + " DoNotAcceptEvents=" + nativeHeader.DoNotAcceptEvents);
                }

                // Get the EventFire event — this is how ButtonWidget signals clicks
                var eventFireEvent = headerWidget.GetType().GetEvent("EventFire",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (eventFireEvent == null)
                {
                    MCMSettings.DebugLog("LoreSection: EventFire event not found on header widget");
                    return;
                }

                // Subscribe to EventFire — toggle content on Click
                Action<Widget, string, object[]> eventHandler = (Widget sender, string eventName, object[] args) =>
                {
                    if (eventName == "Click")
                    {
                        bool wasVisible = contentContainer.IsVisible;
                        contentContainer.IsVisible = !wasVisible;
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
                            catch (Exception ex) { MCMSettings.DebugLog("LoreSectionInjector: toggle arrow SetState failed: " + ex.ToString()); }
                        }
                        MCMSettings.DebugLog("LoreSection: toggled to "
                            + (wasVisible ? "collapsed" : "expanded"));
                    }
                };

                eventFireEvent.AddEventHandler(headerWidget, eventHandler);
                MCMSettings.DebugLog("LoreSection: subscribed to EventFire, DoNotPassEventsToChildren="
                    + headerWidget.DoNotPassEventsToChildren);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("LoreSection: HookCollapseToggle error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Tries to create a widget by type name via reflection.
        /// Searches the assembly containing Widget first, then all loaded assemblies.
        /// </summary>
        public static Widget TryCreateWidgetByType(UIContext uiContext, string typeName)
        {
            try
            {
                // Try the Gauntlet assembly first
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
                MCMSettings.DebugLog("LoreSection: TryCreateWidgetByType(" + typeName + ") failed: " + ex.ToString());
            }
            return null;
        }

        /// <summary>
        /// Copies the StackLayout.LayoutMethod from one ListPanel to another via reflection.
        /// If sourcePanel is null, sets HorizontalLeftToRight as default.
        /// </summary>
        public static void CopyOrSetHorizontalLayout(Widget sourcePanel, Widget targetPanel)
        {
            try
            {
                var layoutProp = targetPanel.GetType().GetProperty("StackLayout", AllFlags);
                if (layoutProp == null) return;
                var targetLayout = layoutProp.GetValue(targetPanel);
                if (targetLayout == null) return;

                var methodProp = targetLayout.GetType().GetProperty("LayoutMethod", AllFlags);
                if (methodProp == null || !methodProp.CanWrite) return;

                if (sourcePanel != null)
                {
                    // Copy from source
                    var srcLayoutProp = sourcePanel.GetType().GetProperty("StackLayout", AllFlags);
                    if (srcLayoutProp != null)
                    {
                        var srcLayout = srcLayoutProp.GetValue(sourcePanel);
                        if (srcLayout != null)
                        {
                            var srcMethodProp = srcLayout.GetType().GetProperty("LayoutMethod", AllFlags);
                            if (srcMethodProp != null)
                            {
                                methodProp.SetValue(targetLayout, srcMethodProp.GetValue(srcLayout));
                                return;
                            }
                        }
                    }
                }

                // Fallback: set HorizontalLeftToRight
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
                MCMSettings.DebugLog("LoreSection: layout copy failed: " + ex.ToString());
            }
        }

        // 2026-05-28: Removed dead method SetVerticalLayout(Widget). It had zero callers
        // and was functionally identical to SetVerticalLayoutTopToBottom (both set the
        // VerticalBottomToTop enum value per the Bannerlord LayoutMethod inversion).
        // If a future "newest-first" vertical helper is needed, write it fresh against
        // the inverted-enum reference doc (memory: reference_bannerlord_layoutmethod_enum_inverted).

        /// <summary>
        /// Sets a ListPanel's StackLayout.LayoutMethod to VerticalTopToBottom — children
        /// stack from the top down (first AddChild appears at the top). Use this for
        /// prose / body text where wrapped lines must read top-to-bottom in source order.
        /// Bug 2026-05-25: previously all callers used <see cref="SetVerticalLayout"/>
        /// (BottomToTop), which caused wrapped lore body text to render with lines
        /// stacked in reverse vertical order.
        /// </summary>
        // Cache so we only log the enum once per run to avoid spam.
        private static bool _layoutEnumLogged = false;

        public static void SetVerticalLayoutTopToBottom(Widget panel)
        {
            try
            {
                var layoutProp = panel.GetType().GetProperty("StackLayout", AllFlags);
                if (layoutProp == null) { MCMSettings.DebugLog("LoreSection: SetVTopToBottom — no StackLayout prop"); return; }
                var layout = layoutProp.GetValue(panel);
                if (layout == null) { MCMSettings.DebugLog("LoreSection: SetVTopToBottom — StackLayout is null"); return; }

                var methodProp = layout.GetType().GetProperty("LayoutMethod", AllFlags);
                if (methodProp == null || !methodProp.CanWrite) { MCMSettings.DebugLog("LoreSection: SetVTopToBottom — no LayoutMethod prop"); return; }

                var enumType = methodProp.PropertyType;

                // One-time enum dump for diagnosis
                if (!_layoutEnumLogged)
                {
                    var allVals = new System.Text.StringBuilder();
                    foreach (var ev in Enum.GetValues(enumType))
                    {
                        if (allVals.Length > 0) allVals.Append(", ");
                        allVals.Append(ev.ToString()).Append("=").Append((int)ev);
                    }
                    MCMSettings.DebugLog("LoreSection: LayoutMethod enum values: " + allVals);
                    _layoutEnumLogged = true;
                }

                // ============================================================================
                // BANNERLORD LAYOUT-METHOD ENUM QUIRK (1.4.x ONLY — user-corrected 2026-05-28).
                // On Bannerlord 1.4.x the LayoutMethod enum names are INVERTED vs. visual reading:
                //
                //   1.4.x: VerticalBottomToTop  → renders TOP-DOWN  (what prose body needs)
                //   1.4.x: VerticalTopToBottom  → renders BOTTOM-UP (counterintuitive)
                //
                //   1.3.x: enum names work as named (VerticalTopToBottom = top-down).
                //
                // BannerlordVersion.TopDownLayoutEnumName() selects the right value per engine.
                // The 2026-05-27 diagnostic that verified VerticalBottomToTop renders top-down
                // was actually performed on 1.4.x (memory previously misattributed to 1.3.x).
                // Memory: reference_bannerlord_layoutmethod_enum_inverted.
                // ============================================================================
                // 2026-05-28: Version-conditional. The LayoutMethod enum is inverted only on
                // 1.4.x; on 1.3.x the enum names work as named. BannerlordVersion picks the
                // correct name for the running engine.
                string targetName = BannerlordVersion.TopDownLayoutEnumName();
                foreach (var ev in Enum.GetValues(enumType))
                {
                    if (ev.ToString() == targetName)
                    {
                        methodProp.SetValue(layout, ev);
                        return;
                    }
                }

                // Try 2: any value that does NOT match the "opposite-of-target" or horizontal names.
                // Computes the opposite enum name once and skips values that contain it.
                string oppositeName = BannerlordVersion.BottomUpLayoutEnumName();
                foreach (var ev in Enum.GetValues(enumType))
                {
                    string n = ev.ToString();
                    if (n.Contains(oppositeName) || n.Contains("RightLeft") || n.Contains("Horizontal")) continue;
                    methodProp.SetValue(layout, ev);
                    var readBack = methodProp.GetValue(layout);
                    MCMSettings.DebugLog("LoreSection: SetVTopToBottom — chose '" + ev + "' (Try2 non-reverse, target='" + targetName + "'), read-back='" + readBack + "'");
                    return;
                }

                // Try 3: index 0 — universal last-resort default.
                var vals = Enum.GetValues(enumType);
                if (vals.Length > 0)
                {
                    var v = vals.GetValue(0);
                    methodProp.SetValue(layout, v);
                    var readBack = methodProp.GetValue(layout);
                    MCMSettings.DebugLog("LoreSection: SetVTopToBottom — chose '" + v + "' (Try3 index0), read-back='" + readBack + "'");
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("LoreSection: SetVerticalLayoutTopToBottom failed: " + ex.ToString());
            }
        }

        // Collapse state persistence
        private static readonly Dictionary<string, bool> _loreCollapseStates = new Dictionary<string, bool>();

        private static void BuildAndInsertLoreSection(UIContext uiContext, Widget parent, int index,
            string heroId, List<(string fieldKey, string label, string value)> fields, List<string> emptyFieldKeys)
        {
            try
            {
                // === 1. Extract native widget references ===
                var native = ExtractNativeSectionParts(parent);

                // CHANGE 3 (2026-05-27 ROT fix): adaptive brush resolution.
                // Try vanilla brush name first, then known modded variants, then any text brush
                // in the tree. This means a modded prefab (ROT, future) gets styled with WHATEVER
                // brush it actually uses, instead of unstyled defaults.
                Brush contentTextBrush = FindBrushByName(parent, "Encyclopedia.SubPage.Info.Text")
                                         ?? FindBrushByName(parent, "Encyclopedia.SubPage.History.Text")          // ROT-style
                                         ?? FindBrushByName(parent, "Encyclopedia.SubPage.Element.Properties.Text") // ROT-style
                                         ?? FindAnyTextBrush(parent);

                // Stat label brush for field labels (e.g., "History", "Economy & Trade").
                // Labels MUST be GOLDEN / header-style, visually distinct from body text. Chain
                // through known title/header brush names before falling to body brush, otherwise
                // field labels render cream like body text on Settlement pages where the structural
                // fallback fires (user reported 2026-05-27 after first fix pass: "labels not golden,
                // just in the Settlements").
                Brush labelBrush = FindBrushByName(parent, "Encyclopedia.Stat.DefinitionText")           // vanilla label (golden)
                                   ?? FindBrushByName(parent, "Encyclopedia.SubPage.Title.Text")        // ROT golden title style
                                   ?? FindBrushByName(parent, "Encyclopedia.SubPage.Element.Properties.Text") // alt modded label
                                   ?? FindBrushByName(parent, "Encyclopedia.SubPage.Header.Text")        // vanilla golden header
                                   ?? contentTextBrush;                                                   // last-resort body brush

                // Stat value brush for lore field values. Body-style is correct here (values are
                // the long-form text below each label) — contentTextBrush fallback is fine.
                Brush valueBrush = FindBrushByName(parent, "Encyclopedia.Stat.ValueText")
                                   ?? contentTextBrush;

                // Also scan for all unique text brush names for debug logging
                var allBrushNames = new System.Collections.Generic.HashSet<string>();
                CollectTextBrushNames(parent, allBrushNames, 0);
                MCMSettings.DebugLog("LoreSection: all text brush names in tree: "
                    + string.Join(", ", allBrushNames));

                // Dump the first native section's content to discover brush names for labels/values
                if (native.ReferenceHeader != null)
                {
                    int headerIdx = -1;
                    for (int i = 0; i < parent.ChildCount; i++)
                    {
                        if (parent.GetChild(i) == native.ReferenceHeader)
                        { headerIdx = i; break; }
                    }
                    if (headerIdx >= 0 && headerIdx + 1 < parent.ChildCount)
                    {
                        MCMSettings.DebugLog("LoreSection: === Native Info content tree ===");
                        DumpWidgetTree(parent.GetChild(headerIdx + 1), 0, 5);
                    }
                }

                // Log native indicator properties for debugging
                if (native.NativeIndicator != null)
                {
                    var ni = native.NativeIndicator;
                    MCMSettings.DebugLog("LoreSection: native indicator — "
                        + "W=" + ni.SuggestedWidth + " H=" + ni.SuggestedHeight
                        + " WPolicy=" + ni.WidthSizePolicy + " HPolicy=" + ni.HeightSizePolicy
                        + " margins=" + ni.MarginLeft + "/" + ni.MarginTop + "/" + ni.MarginRight + "/" + ni.MarginBottom
                        + " hAlign=" + ni.HorizontalAlignment + " vAlign=" + ni.VerticalAlignment
                        + " vis=" + ni.IsVisible + " enabled=" + ni.IsEnabled
                        + " brush=" + (ni is BrushWidget bwDbg ? bwDbg.ReadOnlyBrush?.Name : "?"));
                }
                else
                {
                    MCMSettings.DebugLog("LoreSection: native indicator NOT FOUND");
                }

                MCMSettings.DebugLog("LoreSection: brushes — indicator="
                    + (native.IndicatorBrush != null) + " headerText="
                    + (native.HeaderTextBrush != null) + " line="
                    + (native.LineBrush != null) + " content="
                    + (contentTextBrush != null ? contentTextBrush.Name : "null")
                    + " label=" + (labelBrush != null ? labelBrush.Name : "null"));

                // === 2. Create the header (visual clone of native section) ===

                // Outer wrapper — use ButtonWidget so we get native click handling.
                // Find the ButtonWidget type from the native header's type hierarchy.
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
                                MCMSettings.DebugLog("LoreSection: created ButtonWidget from type "
                                    + t.FullName);
                            }
                            break;
                        }
                        t = t.BaseType;
                    }
                }
                if (headerWrapper == null)
                {
                    headerWrapper = TryCreateWidgetByType(uiContext, "ButtonWidget");
                    if (headerWrapper != null)
                        MCMSettings.DebugLog("LoreSection: created ButtonWidget via TryCreateWidgetByType");
                }
                if (headerWrapper == null)
                {
                    headerWrapper = new Widget(uiContext);
                    MCMSettings.DebugLog("LoreSection: fell back to plain Widget for header");
                }
                headerWrapper.Id = "EditableEncyclopedia_LoreDivider";
                headerWrapper.WidthSizePolicy = SizePolicy.StretchToParent;
                headerWrapper.HeightSizePolicy = SizePolicy.CoverChildren;
                if (native.ReferenceHeader != null)
                {
                    headerWrapper.MarginTop = native.ReferenceHeader.MarginTop;
                    headerWrapper.MarginBottom = native.ReferenceHeader.MarginBottom;
                    headerWrapper.MarginLeft = native.ReferenceHeader.MarginLeft;
                    headerWrapper.MarginRight = native.ReferenceHeader.MarginRight;
                }
                // Ensure the ButtonWidget receives click events directly
                // (children must not steal the mouse press from the parent button)
                headerWrapper.DoNotAcceptEvents = false;
                headerWrapper.DoNotPassEventsToChildren = true;

                // Inner horizontal bar (ListPanel) — clone layout from native PlacementListPanel
                Widget headerBar = TryCreateWidgetByType(uiContext, "ListPanel");
                if (headerBar == null)
                    headerBar = new Widget(uiContext);
                headerBar.Id = "EditableEncyclopedia_LorePlacement";

                if (native.ReferencePlacement != null)
                {
                    CopyLayoutProperties(native.ReferencePlacement, headerBar);
                    CopyOrSetHorizontalLayout(native.ReferencePlacement, headerBar);
                }
                else
                {
                    headerBar.WidthSizePolicy = SizePolicy.StretchToParent;
                    headerBar.HeightSizePolicy = SizePolicy.CoverChildren;
                    CopyOrSetHorizontalLayout(null, headerBar);
                }

                // Collapse indicator — clone ALL properties from native widget
                BrushWidget arrow = null;
                if (native.NativeIndicator != null)
                {
                    arrow = new BrushWidget(uiContext);
                    arrow.Id = "EditableEncyclopedia_LoreIndicator";
                    CopyLayoutProperties(native.NativeIndicator, arrow);
                    if (native.IndicatorBrush != null)
                        arrow.Brush = native.IndicatorBrush;

                    // Force the brush visual state so the sprite layer actually renders.
                    // A standalone BrushWidget (not inside a ButtonWidget) never gets its
                    // state driven, so the brush renderer has no active sprite layer.
                    ForceWidgetBrushState(arrow, native.NativeIndicator);

                    headerBar.AddChild(arrow);
                    MCMSettings.DebugLog("LoreSection: arrow cloned — W=" + arrow.SuggestedWidth
                        + " H=" + arrow.SuggestedHeight + " WPolicy=" + arrow.WidthSizePolicy
                        + " HPolicy=" + arrow.HeightSizePolicy);
                }

                // Title text "Lore" — clone properties from native title
                var titleText = new TextWidget(uiContext);
                titleText.Id = "EditableEncyclopedia_LoreTitle";
                titleText.Text = Localization.L("info_field_lore_header");
                if (native.NativeTitle != null)
                {
                    CopyLayoutProperties(native.NativeTitle, titleText);
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
                    Widget line = TryCreateWidgetByType(uiContext, "ImageWidget");
                    if (line == null)
                        line = new BrushWidget(uiContext);
                    line.Id = "EditableEncyclopedia_LoreLine";
                    CopyLayoutProperties(native.NativeLine, line);
                    if (native.LineBrush != null && line is BrushWidget bwLine)
                        bwLine.Brush = native.LineBrush;
                    headerBar.AddChild(line);
                }

                headerWrapper.AddChild(headerBar);

                // === 3. Create the content container ===
                // Align content with the header title text (just past the arrow indicator).
                float contentMarginLeft = DefaultContentMarginLeft;
                if (native.NativeIndicator != null)
                    contentMarginLeft = native.NativeIndicator.SuggestedWidth
                        + native.NativeIndicator.MarginLeft + native.NativeIndicator.MarginRight + ContentMarginPadding;

                Widget contentContainer = TryCreateWidgetByType(uiContext, "ListPanel");
                if (contentContainer == null)
                    contentContainer = new Widget(uiContext);
                contentContainer.Id = "EditableEncyclopedia_LoreContent";
                contentContainer.WidthSizePolicy = SizePolicy.StretchToParent;
                contentContainer.HeightSizePolicy = SizePolicy.CoverChildren;
                contentContainer.MarginLeft = contentMarginLeft;
                contentContainer.MarginBottom = ContentMarginPadding;
                // Bug 2026-05-25: must be TopToBottom — rows (gaps, field labels,
                // wrapped lines) are added in logical/source order via forward loops
                // and must render top-down.
                SetVerticalLayoutTopToBottom(contentContainer);

                // "Edit Description" button removed per user request — not shown in the Lore section.

                // Empty state hint
                if (fields.Count == 0)
                {
                    var hintWidget = new TextWidget(uiContext);
                    hintWidget.Id = "EditableEncyclopedia_LoreEmptyHint";
                    hintWidget.Text = Localization.L("ui_lore_hint");
                    hintWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                    hintWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                    if (contentTextBrush != null)
                    {
                        var hintBrush = contentTextBrush.Clone();
                        LoreSectionHelpers.SetBrushColor(hintBrush, 0.5f, 0.5f, 0.45f, 0.45f);
                        hintWidget.Brush = hintBrush;
                    }
                    contentContainer.AddChild(hintWidget);
                }

                // Build individual field entries: label (highlighted) + value (clickable to edit), with spacing
                for (int fi = 0; fi < fields.Count; fi++)
                {
                    var field = fields[fi];

                    // Top margin between fields
                    if (fi > 0)
                    {
                        var spacer = new Widget(uiContext);
                        spacer.WidthSizePolicy = SizePolicy.StretchToParent;
                        spacer.HeightSizePolicy = SizePolicy.Fixed;
                        spacer.SuggestedHeight = FieldSpacerHeight;
                        contentContainer.AddChild(spacer);
                    }

                    // Field label row: label text + delete button
                    Widget labelRow = TryCreateWidgetByType(uiContext, "ListPanel");
                    if (labelRow == null) labelRow = new Widget(uiContext);
                    labelRow.Id = "EditableEncyclopedia_LoreLabelRow_" + fi;
                    labelRow.WidthSizePolicy = SizePolicy.StretchToParent;
                    labelRow.HeightSizePolicy = SizePolicy.CoverChildren;
                    CopyOrSetHorizontalLayout(null, labelRow);

                    var labelWidget = new TextWidget(uiContext);
                    labelWidget.Id = "EditableEncyclopedia_LoreLabel_" + fi;
                    labelWidget.Text = field.label;
                    labelWidget.WidthSizePolicy = SizePolicy.CoverChildren;
                    labelWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                    if (labelBrush != null)
                        labelWidget.Brush = labelBrush;
                    else if (contentTextBrush != null)
                    {
                        var goldenBrush = contentTextBrush.Clone();
                        try
                        {
                            var fcProp = goldenBrush.GetType().GetProperty("FontColor",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                            if (fcProp != null)
                                fcProp.SetValue(goldenBrush, new Color(0.85f, 0.75f, 0.45f, 1f));
                        }
                        catch { }
                        labelWidget.Brush = goldenBrush;
                    }
                    labelRow.AddChild(labelWidget);

                    // Delete button (dim ×)
                    Widget delButton = TryCreateWidgetByType(uiContext, "ButtonWidget");
                    if (delButton == null) delButton = new Widget(uiContext);
                    delButton.Id = "EditableEncyclopedia_LoreDel_" + fi;
                    delButton.WidthSizePolicy = SizePolicy.CoverChildren;
                    delButton.HeightSizePolicy = SizePolicy.CoverChildren;
                    delButton.DoNotAcceptEvents = false;
                    delButton.DoNotPassEventsToChildren = true;
                    delButton.MarginLeft = DeleteButtonMarginLeft;

                    var delText = new TextWidget(uiContext);
                    delText.Text = "\u00D7"; // × symbol
                    delText.WidthSizePolicy = SizePolicy.CoverChildren;
                    delText.HeightSizePolicy = SizePolicy.CoverChildren;
                    if (contentTextBrush != null)
                    {
                        var delBrush = contentTextBrush.Clone();
                        LoreSectionHelpers.SetBrushColor(delBrush, 0.6f, 0.35f, 0.35f, 0.4f);
                        delText.Brush = delBrush;
                    }
                    delButton.AddChild(delText);

                    string delFieldKey = field.fieldKey;
                    string delHeroId = heroId;
                    LoreSectionHelpers.HookWidgetClick(delButton, () =>
                    {
                        EncyclopediaEditPopup.TryDeleteField(delFieldKey, delHeroId);
                    });
                    labelRow.AddChild(delButton);

                    contentContainer.AddChild(labelRow);

                    // Small gap between label and value
                    var gap = new Widget(uiContext);
                    gap.WidthSizePolicy = SizePolicy.StretchToParent;
                    gap.HeightSizePolicy = SizePolicy.Fixed;
                    gap.SuggestedHeight = LabelValueGapHeight;
                    contentContainer.AddChild(gap);

                    // Clickable container for the field value — click to edit
                    Widget valueButton = TryCreateWidgetByType(uiContext, "ButtonWidget");
                    if (valueButton == null) valueButton = new Widget(uiContext);
                    valueButton.Id = "EditableEncyclopedia_LoreValBtn_" + fi;
                    valueButton.WidthSizePolicy = SizePolicy.StretchToParent;
                    valueButton.HeightSizePolicy = SizePolicy.CoverChildren;
                    valueButton.DoNotAcceptEvents = false;
                    valueButton.DoNotPassEventsToChildren = true;

                    // Hook click to edit this field
                    string editFieldKey = field.fieldKey;
                    string editHeroId = heroId;
                    LoreSectionHelpers.HookWidgetClick(valueButton, () =>
                    {
                        EncyclopediaEditPopup.TryOpenField(editFieldKey, editHeroId);
                    });

                    // Field value — split each line on ':' so the key part (e.g., "Born in")
                    // renders in the label brush (dark yellow) and the value in content brush (white).
                    Widget valueContainer = TryCreateWidgetByType(uiContext, "ListPanel");
                    if (valueContainer == null) valueContainer = new Widget(uiContext);
                    valueContainer.Id = "EditableEncyclopedia_LoreValContainer_" + fi;
                    valueContainer.WidthSizePolicy = SizePolicy.StretchToParent;
                    valueContainer.HeightSizePolicy = SizePolicy.CoverChildren;
                    // Bug 2026-05-25: must be TopToBottom — wrapped lines of a field's value
                    // are added in source order via forward loop and must render top-down.
                    SetVerticalLayoutTopToBottom(valueContainer);

                    // TextWidget doesn't word-wrap when created programmatically
                    // in v1.3.13, so we manually split long text at word boundaries.
                    string[] lines = field.value.Split(new[] { '\n' }, StringSplitOptions.None);
                    for (int li = 0; li < lines.Length; li++)
                    {
                        string line = lines[li];
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            // Blank line between paragraphs (from "\n\n" or a "---" marker): add a
                            // small vertical gap so paragraphs read distinctly.
                            if (li > 0 && li < lines.Length - 1)
                            {
                                var paraGap = new Widget(uiContext);
                                paraGap.Id = "EditableEncyclopedia_LoreParaGap_" + fi + "_" + li;
                                paraGap.WidthSizePolicy = SizePolicy.StretchToParent;
                                paraGap.HeightSizePolicy = SizePolicy.Fixed;
                                paraGap.SuggestedHeight = 8f;
                                valueContainer.AddChild(paraGap);
                            }
                            continue;
                        }

                        var wrappedLines = WordWrapText(line, WordWrapMaxChars);
                        for (int wi = 0; wi < wrappedLines.Count; wi++)
                        {
                            string wrappedLine = wrappedLines[wi];

                            // Check for markdown formatting (bold, italic, cross-refs, colors, dividers)
                            if (MarkdownFormatter.HasFormatting(wrappedLine))
                            {
                                var mdSegments = MarkdownFormatter.ParseLine(wrappedLine);

                                // Divider line — render as a thin horizontal line
                                if (mdSegments.Count == 1 && mdSegments[0].Style == MarkdownFormatter.SegmentStyle.Divider)
                                {
                                    var divider = new Widget(uiContext);
                                    divider.WidthSizePolicy = SizePolicy.StretchToParent;
                                    divider.HeightSizePolicy = SizePolicy.Fixed;
                                    divider.SuggestedHeight = 1f;
                                    divider.MarginTop = 4f;
                                    divider.MarginBottom = 4f;
                                    divider.MarginLeft = 10f;
                                    divider.MarginRight = 10f;
                                    try
                                    {
                                        var colorProp = divider.GetType().GetProperty("Color", BindingFlags.Instance | BindingFlags.Public);
                                        if (colorProp != null)
                                            colorProp.SetValue(divider, new Color(0.5f, 0.45f, 0.35f, 0.5f));
                                    }
                                    catch { }
                                    valueContainer.AddChild(divider);
                                    continue;
                                }

                                // Check if all segments are plain (no actual formatting found)
                                bool allPlain = true;
                                foreach (var seg in mdSegments)
                                    if (seg.Style != MarkdownFormatter.SegmentStyle.Normal) { allPlain = false; break; }

                                if (allPlain)
                                {
                                    // No formatting — render as plain text
                                    var lineWidget = new TextWidget(uiContext);
                                    lineWidget.Text = wrappedLine;
                                    lineWidget.WidthSizePolicy = SizePolicy.StretchToParent;
                                    lineWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                                    if (contentTextBrush != null) lineWidget.Brush = contentTextBrush.Clone();
                                    ForceTextAlignLeft(lineWidget);
                                    valueContainer.AddChild(lineWidget);
                                    continue;
                                }

                                // Formatted line — render segments, handling inline dividers
                                Widget curRow = null;
                                int segNum = 0;
                                foreach (var seg in mdSegments)
                                {
                                    // Divider — flush current row and add a horizontal line
                                    if (seg.Style == MarkdownFormatter.SegmentStyle.Divider)
                                    {
                                        if (curRow != null) { valueContainer.AddChild(curRow); curRow = null; }
                                        var divW = new Widget(uiContext);
                                        divW.WidthSizePolicy = SizePolicy.StretchToParent;
                                        divW.HeightSizePolicy = SizePolicy.Fixed;
                                        divW.SuggestedHeight = 1f;
                                        divW.MarginTop = 6f;
                                        divW.MarginBottom = 6f;
                                        divW.MarginLeft = 10f;
                                        divW.MarginRight = 10f;
                                        try
                                        {
                                            var cp = divW.GetType().GetProperty("Color", BindingFlags.Instance | BindingFlags.Public);
                                            if (cp != null) cp.SetValue(divW, new Color(0.5f, 0.45f, 0.35f, 0.5f));
                                        }
                                        catch { }
                                        valueContainer.AddChild(divW);
                                        continue;
                                    }

                                    if (string.IsNullOrEmpty(seg.Text)) continue;

                                    // Create row on demand
                                    if (curRow == null)
                                    {
                                        curRow = TryCreateWidgetByType(uiContext, "ListPanel") ?? new Widget(uiContext);
                                        curRow.Id = "EditableEncyclopedia_LoreLine_" + fi + "_" + li + "_" + wi + "_" + segNum;
                                        curRow.WidthSizePolicy = SizePolicy.StretchToParent;
                                        curRow.HeightSizePolicy = SizePolicy.CoverChildren;
                                        CopyOrSetHorizontalLayout(null, curRow);
                                    }

                                    var segW = new TextWidget(uiContext);
                                    segW.Text = seg.Text;
                                    segW.WidthSizePolicy = SizePolicy.CoverChildren;
                                    segW.HeightSizePolicy = SizePolicy.CoverChildren;

                                    switch (seg.Style)
                                    {
                                        case MarkdownFormatter.SegmentStyle.Bold:
                                            if (contentTextBrush != null)
                                            {
                                                var boldBrush = contentTextBrush.Clone();
                                                SetBrushFontColor(boldBrush, new Color(0.92f, 0.88f, 0.78f, 1f));
                                                segW.Brush = boldBrush;
                                            }
                                            break;
                                        case MarkdownFormatter.SegmentStyle.Italic:
                                            if (contentTextBrush != null)
                                            {
                                                var italicBrush = contentTextBrush.Clone();
                                                SetBrushFontColor(italicBrush, new Color(0.75f, 0.70f, 0.60f, 0.9f));
                                                segW.Brush = italicBrush;
                                            }
                                            break;
                                        case MarkdownFormatter.SegmentStyle.CrossRef:
                                            if (labelBrush != null)
                                                segW.Brush = labelBrush.Clone();
                                            else if (contentTextBrush != null)
                                            {
                                                var goldenBrush = contentTextBrush.Clone();
                                                SetBrushFontColor(goldenBrush, new Color(0.85f, 0.75f, 0.45f, 1f));
                                                segW.Brush = goldenBrush;
                                            }
                                            break;
                                        case MarkdownFormatter.SegmentStyle.Colored:
                                            if (contentTextBrush != null && seg.CustomColor.HasValue)
                                            {
                                                var colorBrush = contentTextBrush.Clone();
                                                SetBrushFontColor(colorBrush, seg.CustomColor.Value);
                                                segW.Brush = colorBrush;
                                            }
                                            break;
                                        case MarkdownFormatter.SegmentStyle.Strikethrough:
                                            if (contentTextBrush != null)
                                            {
                                                var strikeBrush = contentTextBrush.Clone();
                                                SetBrushFontColor(strikeBrush, new Color(0.50f, 0.48f, 0.42f, 0.5f));
                                                segW.Brush = strikeBrush;
                                            }
                                            break;
                                        case MarkdownFormatter.SegmentStyle.Underline:
                                            if (contentTextBrush != null)
                                            {
                                                var underBrush = contentTextBrush.Clone();
                                                SetBrushFontColor(underBrush, new Color(0.90f, 0.85f, 0.70f, 1f));
                                                segW.Brush = underBrush;
                                            }
                                            break;
                                        case MarkdownFormatter.SegmentStyle.Small:
                                            if (contentTextBrush != null)
                                            {
                                                var smallBrush = contentTextBrush.Clone();
                                                SetBrushFontColor(smallBrush, new Color(0.60f, 0.55f, 0.45f, 0.8f));
                                                smallBrush.FontSize = Math.Max(12, smallBrush.FontSize - 4);
                                                segW.Brush = smallBrush;
                                            }
                                            break;
                                        case MarkdownFormatter.SegmentStyle.Heading:
                                            if (contentTextBrush != null)
                                            {
                                                var headBrush = contentTextBrush.Clone();
                                                SetBrushFontColor(headBrush, new Color(0.92f, 0.88f, 0.75f, 1f));
                                                headBrush.FontSize = headBrush.FontSize + 4;
                                                segW.Brush = headBrush;
                                            }
                                            segW.WidthSizePolicy = SizePolicy.StretchToParent;
                                            break;
                                        case MarkdownFormatter.SegmentStyle.Quote:
                                            if (contentTextBrush != null)
                                            {
                                                var quoteBrush = contentTextBrush.Clone();
                                                SetBrushFontColor(quoteBrush, new Color(0.70f, 0.65f, 0.55f, 0.9f));
                                                segW.Brush = quoteBrush;
                                            }
                                            segW.MarginLeft = 20f;
                                            segW.WidthSizePolicy = SizePolicy.StretchToParent;
                                            break;
                                        default:
                                            if (contentTextBrush != null) segW.Brush = contentTextBrush.Clone();
                                            break;
                                    }

                                    ForceTextAlignLeft(segW);
                                    curRow.AddChild(segW);
                                    segNum++;
                                }
                                if (curRow != null) valueContainer.AddChild(curRow);
                            }
                            else
                            {
                                // Normal line — no formatting
                                var lineWidget = new TextWidget(uiContext);
                                lineWidget.Id = "EditableEncyclopedia_LoreLine_" + fi + "_" + li + "_" + wi;
                                lineWidget.Text = wrappedLine;
                                lineWidget.WidthSizePolicy = SizePolicy.StretchToParent;
                                lineWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                                if (contentTextBrush != null)
                                    lineWidget.Brush = contentTextBrush.Clone();
                                ForceTextAlignLeft(lineWidget);
                                valueContainer.AddChild(lineWidget);
                            }
                        }
                    }

                    // If this topic's prose is long, cap its height and let it scroll in place so a
                    // huge write-up doesn't stretch the whole encyclopedia page. Short topics are
                    // untouched. ChildCount ~= number of rendered lines/rows.
                    int loreLineCount = valueContainer.ChildCount;
                    if (loreLineCount * LoreLineHeightEstimate > LoreTopicMaxHeight)
                    {
                        Widget scrolled = WrapFieldInScroll(uiContext, valueContainer, LoreTopicMaxHeight);
                        if (scrolled != null)
                        {
                            // The button captures events by default (DoNotPassEventsToChildren=true),
                            // which would swallow the mouse-wheel; allow child events so the
                            // ScrollablePanel scrolls. A click on the prose still bubbles up to the
                            // button, so click-to-edit keeps working.
                            valueButton.DoNotPassEventsToChildren = false;
                            // Pin the button to the capped height. It is CoverChildren by default,
                            // which grows to the full prose and DEFEATS the clip (the prose showed
                            // full-length, no scroll). Matches canonical TryWrapInScrollablePanel.
                            valueButton.HeightSizePolicy = SizePolicy.Fixed;
                            valueButton.SuggestedHeight = LoreTopicMaxHeight;
                            valueButton.ScaledSuggestedHeight = LoreTopicMaxHeight;
                            valueButton.AddChild(scrolled);
                        }
                        else
                        {
                            valueButton.AddChild(valueContainer); // fallback: unclipped
                        }
                    }
                    else
                    {
                        valueButton.AddChild(valueContainer);
                    }
                    contentContainer.AddChild(valueButton);
                }

                // Per-field add buttons for empty fields
                if (emptyFieldKeys.Count > 0)
                {
                    var addSpacer = new Widget(uiContext);
                    addSpacer.WidthSizePolicy = SizePolicy.StretchToParent;
                    addSpacer.HeightSizePolicy = SizePolicy.Fixed;
                    addSpacer.SuggestedHeight = fields.Count > 0 ? AddSpacerHeightWithFields : AddSpacerHeightEmpty;
                    contentContainer.AddChild(addSpacer);

                    Widget addRow = TryCreateWidgetByType(uiContext, "ListPanel");
                    if (addRow == null) addRow = new Widget(uiContext);
                    addRow.Id = "EditableEncyclopedia_LoreAddRow";
                    addRow.WidthSizePolicy = SizePolicy.StretchToParent;
                    addRow.HeightSizePolicy = SizePolicy.CoverChildren;
                    CopyOrSetHorizontalLayout(null, addRow);

                    for (int ei = 0; ei < emptyFieldKeys.Count; ei++)
                    {
                        string emptyKey = emptyFieldKeys[ei];
                        string emptyLabel = Localization.L("info_field_" + emptyKey);

                        Widget addBtn = TryCreateWidgetByType(uiContext, "ButtonWidget");
                        if (addBtn == null) addBtn = new Widget(uiContext);
                        addBtn.Id = "EditableEncyclopedia_LoreAdd_" + ei;
                        addBtn.WidthSizePolicy = SizePolicy.CoverChildren;
                        addBtn.HeightSizePolicy = SizePolicy.CoverChildren;
                        addBtn.DoNotAcceptEvents = false;
                        addBtn.DoNotPassEventsToChildren = true;
                        if (ei > 0) addBtn.MarginLeft = AddButtonMarginLeft;

                        var addText = new TextWidget(uiContext);
                        addText.Text = "+ " + emptyLabel;
                        addText.WidthSizePolicy = SizePolicy.CoverChildren;
                        addText.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (contentTextBrush != null)
                        {
                            var addBrush = contentTextBrush.Clone();
                            LoreSectionHelpers.SetBrushColor(addBrush, 0.6f, 0.55f, 0.4f, 0.45f);
                            addText.Brush = addBrush;
                        }
                        addBtn.AddChild(addText);

                        string capturedKey = emptyKey;
                        string capturedHeroId = heroId;
                        LoreSectionHelpers.HookWidgetClick(addBtn, () =>
                        {
                            EncyclopediaEditPopup.TryOpenField(capturedKey, capturedHeroId);
                        });
                        addRow.AddChild(addBtn);
                    }

                    contentContainer.AddChild(addRow);
                }

                // === 3b. Wire up click-to-collapse with persistence ===
                LoreSectionHelpers.HookCollapseToggleWithPersistence(
                    headerWrapper, contentContainer, arrow, native.ReferenceHeader, heroId, _loreCollapseStates);

                // Apply persisted collapse state
                if (_loreCollapseStates.TryGetValue(heroId, out bool isCollapsed) && isCollapsed)
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
                        catch (Exception ex) { MCMSettings.DebugLog("LoreSectionInjector: collapsed state arrow SetState failed: " + ex.ToString()); }
                    }
                }

                // === 4. Insert header + content into the parent ===
                // Path 2 defensive validation: bail before inserting if the current
                // page is a Settlement page whose layout has been restructured by
                // a known conflicting mod (Realm of Thrones, etc.). Bug 2026-05-25.
                if (!EncyclopediaAnchorHelper.IsSafeToInjectOnCurrentPage(parent, "Lore"))
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
                    MCMSettings.DebugLog("LoreSectionInjector: AddChildAtIndex failed, appending instead: " + ex.ToString());
                    parent.AddChild(headerWrapper);
                    parent.AddChild(contentContainer);
                }

                _injectedWidgets.Add(headerWrapper);
                _injectedWidgets.Add(contentContainer);
                MCMSettings.DebugLog("LoreSection: injected native-style section with " + fields.Count + " fields");
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("LoreSection: build error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Try to create a RichTextWidget via reflection, since it may support
        /// better text wrapping than plain TextWidget.
        /// </summary>
        /// <summary>
        /// Sets the font color on a Brush via reflection.
        /// </summary>
        private static void SetBrushFontColor(Brush brush, Color color)
        {
            if (brush == null) return;
            try
            {
                var fcProp = brush.GetType().GetProperty("FontColor", AllFlags);
                if (fcProp != null)
                    fcProp.SetValue(brush, color);
            }
            catch (Exception ex) { MCMSettings.DebugLog("LoreSection: SetBrushFontColor failed: " + ex.ToString()); }
        }

        /// <summary>
        /// Extracts the font color from a Brush as a hex string (e.g., "#FFD700").
        /// Uses reflection to handle version differences.
        /// </summary>
        private static string GetBrushFontColorHex(Brush brush)
        {
            if (brush == null) return null;
            try
            {
                // Try Brush.FontColor property (TaleWorlds.Library.Color)
                var fontColorProp = brush.GetType().GetProperty("FontColor", AllFlags);
                if (fontColorProp != null)
                {
                    var color = fontColorProp.GetValue(brush);
                    if (color != null)
                    {
                        // Color has R, G, B, A float properties (0-1 range)
                        var rProp = color.GetType().GetProperty("Red", AllFlags)
                                    ?? color.GetType().GetProperty("R", AllFlags);
                        var gProp = color.GetType().GetProperty("Green", AllFlags)
                                    ?? color.GetType().GetProperty("G", AllFlags);
                        var bProp = color.GetType().GetProperty("Blue", AllFlags)
                                    ?? color.GetType().GetProperty("B", AllFlags);
                        if (rProp != null && gProp != null && bProp != null)
                        {
                            float r = Convert.ToSingle(rProp.GetValue(color));
                            float g = Convert.ToSingle(gProp.GetValue(color));
                            float b = Convert.ToSingle(bProp.GetValue(color));
                            int ri = (int)(Math.Min(1f, Math.Max(0f, r)) * MaxColorValue);
                            int gi = (int)(Math.Min(1f, Math.Max(0f, g)) * MaxColorValue);
                            int bi = (int)(Math.Min(1f, Math.Max(0f, b)) * MaxColorValue);
                            return "#" + ri.ToString("X2") + gi.ToString("X2") + bi.ToString("X2");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("LoreSection: GetBrushFontColorHex failed: " + ex.ToString());
            }
            return null;
        }

        /// <summary>
        /// Escapes text so angle brackets don't break rich text markup.
        /// </summary>
        private static string EscapeRichText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // Only escape angle brackets that could be confused with tags
            return text.Replace("<", "《").Replace(">", "》");
        }

        /// <summary>
        /// Initial one-time set of _text._horizontalAlignment on a TextWidget.
        /// The Harmony prefix on OnRender maintains this every frame.
        /// </summary>
        /// <summary>
        /// Wraps a lore topic's prose in a code-built native ScrollablePanel so long text scrolls in
        /// place with a real scrollbar. Structure (verified against the decompiled ScrollablePanel):
        /// ScrollablePanel(Fixed) > [ ClipRect(ClipContents) > content(CoverChildren) , Scrollbar(+Handle) ].
        /// The scrollbar is REQUIRED — OnMouseScroll only scrolls when VerticalScrollbar != null, and
        /// OnLateUpdate auto-computes its MaxValue / handle-size / offset from ClipRect vs content
        /// height. (The earlier attempt set only ClipRect+InnerPanel and never scrolled; a bare
        /// prefab Instantiate NREs because it has no GauntletMovie/GauntletView for databinding.)
        /// Returns null on failure WITHOUT touching 'content', so the caller falls back to inline.
        /// </summary>
        private static Widget WrapFieldInScroll(UIContext uiContext, Widget content, float targetHeight)
        {
            try
            {
                Type scrollType = null;
                Type barType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (scrollType == null)
                        scrollType = asm.GetType("TaleWorlds.GauntletUI.BaseTypes.ScrollablePanel");
                    if (barType == null)
                        barType = asm.GetType("TaleWorlds.GauntletUI.BaseTypes.ScrollbarWidget");
                    if (scrollType != null && barType != null) break;
                }
                if (scrollType == null || barType == null)
                {
                    MCMSettings.DebugLog("LoreScroll: ScrollablePanel/ScrollbarWidget type not found");
                    return null;
                }

                var scrollPanel = (Widget)Activator.CreateInstance(scrollType, new object[] { uiContext });
                scrollPanel.Id = "EE_LoreTopicScroll";
                scrollPanel.WidthSizePolicy = SizePolicy.StretchToParent;
                scrollPanel.HeightSizePolicy = SizePolicy.Fixed;
                scrollPanel.SuggestedHeight = targetHeight;
                scrollPanel.ScaledSuggestedHeight = targetHeight;

                // Viewport that masks overflow (inset on the right to leave room for the bar).
                var clipRect = new Widget(uiContext);
                clipRect.Id = "EE_LoreTopicClip";
                clipRect.WidthSizePolicy = SizePolicy.StretchToParent;
                clipRect.HeightSizePolicy = SizePolicy.StretchToParent;
                clipRect.MarginRight = 14f;
                clipRect.ClipContents = true;

                // The already-built prose column becomes the scroll InnerPanel.
                content.WidthSizePolicy = SizePolicy.StretchToParent;
                content.HeightSizePolicy = SizePolicy.CoverChildren;
                clipRect.AddChild(content);
                scrollPanel.AddChild(clipRect);

                // Scrollbar + handle (both REQUIRED). The panel drives value/size each frame.
                var scrollbar = (Widget)Activator.CreateInstance(barType, new object[] { uiContext });
                scrollbar.Id = "EE_LoreTopicScrollbar";
                scrollbar.WidthSizePolicy = SizePolicy.Fixed;
                scrollbar.SuggestedWidth = 9f;
                scrollbar.HeightSizePolicy = SizePolicy.StretchToParent;
                scrollbar.HorizontalAlignment = HorizontalAlignment.Right;
                scrollbar.MarginTop = 4f;
                scrollbar.MarginBottom = 4f;
                SetScrollProp(scrollbar, "MinValue", 0f);
                SetScrollProp(scrollbar, "MaxValue", 100f);
                SetScrollEnum(scrollbar, "AlignmentAxis", "Vertical");
                Brush bedBrush = GetScrollBrush("Encyclopedia.Scrollbar.Flat.Bed");
                if (bedBrush != null) SetScrollProp(scrollbar, "Brush", bedBrush);

                var handle = new BrushWidget(uiContext);
                handle.Id = "EE_LoreTopicScrollHandle";
                handle.WidthSizePolicy = SizePolicy.Fixed;
                handle.SuggestedWidth = 8f;
                handle.HeightSizePolicy = SizePolicy.Fixed;
                handle.SuggestedHeight = 50f;
                handle.HorizontalAlignment = HorizontalAlignment.Center;
                Brush handleBrush = GetScrollBrush("Encyclopedia.Scrollbar.Flat.Handle");
                if (handleBrush != null) handle.Brush = handleBrush;
                scrollbar.AddChild(handle);
                SetScrollProp(scrollbar, "Handle", handle);

                scrollPanel.AddChild(scrollbar);

                // Wire the panel: clip + inner + the scrollbar + vertical mouse-wheel.
                SetScrollProp(scrollPanel, "ClipRect", clipRect);
                SetScrollProp(scrollPanel, "InnerPanel", content);
                SetScrollProp(scrollPanel, "VerticalScrollbar", scrollbar);
                SetScrollEnum(scrollPanel, "MouseScrollAxis", "Vertical");
                SetScrollProp(scrollPanel, "AutoHideScrollBars", true);

                MCMSettings.DebugLog("LoreScroll: code panel wired (scrollbar+handle), h=" + targetHeight);
                return scrollPanel;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("LoreScroll WrapFieldInScroll: " + ex.ToString());
                return null;
            }
        }

        // Sets a widget member by property OR field (ScrollablePanel.MouseScrollAxis is a public
        // FIELD, not a property, so a property-only setter silently missed it).
        private static void SetScrollProp(object target, string name, object value)
        {
            try
            {
                var t = target.GetType();
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanWrite) { p.SetValue(target, value); return; }
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) { f.SetValue(target, value); return; }
                MCMSettings.DebugLog("LoreScroll: member '" + name + "' not settable on " + t.Name);
            }
            catch (Exception ex) { MCMSettings.DebugLog("LoreScroll SetScrollProp " + name + ": " + ex.Message); }
        }

        private static void SetScrollEnum(object target, string name, string enumValueName)
        {
            try
            {
                var t = target.GetType();
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanWrite) { p.SetValue(target, Enum.Parse(p.PropertyType, enumValueName)); return; }
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) { f.SetValue(target, Enum.Parse(f.FieldType, enumValueName)); return; }
                MCMSettings.DebugLog("LoreScroll: enum member '" + name + "' not settable on " + t.Name);
            }
            catch (Exception ex) { MCMSettings.DebugLog("LoreScroll SetScrollEnum " + name + ": " + ex.Message); }
        }

        private static Brush GetScrollBrush(string brushName)
        {
            try
            {
                Type rmType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    rmType = asm.GetType("TaleWorlds.Engine.GauntletUI.UIResourceManager");
                    if (rmType != null) break;
                }
                if (rmType == null) return null;
                var bfProp = rmType.GetProperty("BrushFactory",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var bf = bfProp != null ? bfProp.GetValue(null) : null;
                if (bf == null) return null;
                var getBrush = bf.GetType().GetMethod("GetBrush", new[] { typeof(string) });
                if (getBrush == null) return null;
                return getBrush.Invoke(bf, new object[] { brushName }) as Brush;
            }
            catch { return null; }
        }

        public static void ForceTextAlignLeft(BrushWidget widget)
        {
            if (widget == null || _twoDimTextField == null || _textHAlignField == null || _leftAlignValue == null)
                return;
            // v2.5.3 follow-up: _twoDimTextField is TextWidget._text. RichTextWidget is a SIBLING
            // subclass of BrushWidget (not a child of TextWidget), so calling GetValue with a
            // RichTextWidget instance throws ArgumentException. Skip silently when the widget's
            // actual type doesn't match the field's declaring type.
            if (!_twoDimTextField.DeclaringType.IsInstanceOfType(widget))
                return;
            try
            {
                var textObj = _twoDimTextField.GetValue(widget);
                if (textObj != null)
                    _textHAlignField.SetValue(textObj, _leftAlignValue);
            }
            catch (Exception ex) { MCMSettings.DebugLog("LoreSectionInjector: ForceTextAlignLeft failed: " + ex.ToString()); }
        }

        /// <summary>
        /// Splits text into multiple lines at word boundaries, each at most
        /// <paramref name="maxCharsPerLine"/> characters. Used because TextWidget
        /// and RichTextWidget don't word-wrap when created programmatically in v1.3.13.
        /// </summary>
        /// <summary>
        /// Repairs lore text for DISPLAY only (non-destructive — the saved value is untouched;
        /// re-editing the entry fixes it permanently). Handles the corruption seen from pasting
        /// multi-paragraph text over the editor's pre-filled value: (a) de-dupes text whose opening
        /// recurs near the midpoint (the paste-on-top doubling), (b) joins mid-paragraph soft line
        /// breaks so single words don't orphan, (c) inserts a missing space where a sentence was
        /// glued to the next ("ruins.Over" -> "ruins. Over"). Blank-line paragraph breaks are kept.
        /// </summary>
        private static string NormalizeLoreText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string t = text.Replace("\r\n", "\n").Replace("\r", "\n");

            // Split into paragraphs on blank lines OR a standalone "---" marker. "---" survives the
            // game's single-line text input (which eats blank lines on paste), so it's the reliable
            // way to add paragraph breaks in typed/pasted lore. Within each paragraph, flatten
            // whitespace so stray newlines don't orphan words, and add a missing space after a
            // sentence glued to the next capital ("ruins.Over" -> "ruins. Over").
            string[] paras = System.Text.RegularExpressions.Regex.Split(
                t, "(?:\\n[ \\t]*\\n)|(?:[ \\t]*-{3,}[ \\t]*)");
            var cleaned = new List<string>();
            for (int i = 0; i < paras.Length; i++)
            {
                string p = System.Text.RegularExpressions.Regex.Replace(paras[i], "\\s+", " ").Trim();
                p = System.Text.RegularExpressions.Regex.Replace(p, "([.!?])([A-Z])", "$1 $2");
                if (p.Length > 0) cleaned.Add(p);
            }
            string result = string.Join("\n\n", cleaned);

            // De-dup: pasting over the editor's pre-filled value doubles the text; if the opening
            // recurs near the midpoint, keep one copy. (Display only — the saved value is untouched.)
            if (result.Length > 80)
            {
                string head = result.Substring(0, 40);
                int rep = result.IndexOf(head, 40, StringComparison.Ordinal);
                if (rep > 0 && Math.Abs(rep - result.Length / 2) < result.Length / 10)
                {
                    MCMSettings.DebugLog("LoreText: de-duped doubled text");
                    result = result.Substring(0, rep).Trim();
                }
            }
            return result;
        }

        public static List<string> WordWrapText(string text, int maxCharsPerLine)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                result.Add(text ?? "");
                return result;
            }

            int pos = 0;
            while (pos < text.Length)
            {
                if (pos + maxCharsPerLine >= text.Length)
                {
                    if (pos >= 0 && pos <= text.Length)
                        result.Add(text.Substring(pos));
                    break;
                }

                // Find last space within the limit
                int end = Math.Min(pos + maxCharsPerLine, text.Length - 1);
                int searchCount = end - pos;
                if (searchCount <= 0)
                {
                    result.Add(text.Substring(pos));
                    break;
                }
                int lastSpace = text.LastIndexOf(' ', end, searchCount);
                if (lastSpace <= pos)
                    lastSpace = Math.Min(end + 1, text.Length);

                int length = Math.Min(lastSpace - pos, text.Length - pos);
                if (length > 0)
                    result.Add(text.Substring(pos, length));
                pos = lastSpace;
                // Skip the space
                if (pos < text.Length && text[pos] == ' ')
                    pos++;
            }

            return result;
        }

        /// <summary>
        /// Sets the Brush property on a widget via reflection.
        /// Used when the compile-time type is Widget but the runtime type
        /// (e.g. RichTextWidget) inherits from BrushWidget and has a Brush property.
        /// </summary>
        private static void SetWidgetBrush(Widget widget, Brush brush)
        {
            try
            {
                var prop = widget.GetType().GetProperty("Brush", AllFlags);
                if (prop != null && prop.CanWrite)
                    prop.SetValue(widget, brush);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("LoreSection: SetWidgetBrush failed: " + ex.ToString());
            }
        }

        private static Widget TryCreateRichTextWidget(UIContext uiContext, string text)
        {
            try
            {
                // RichTextWidget is in TaleWorlds.GauntletUI.BaseTypes
                var richType = typeof(TextWidget).Assembly.GetType("TaleWorlds.GauntletUI.BaseTypes.RichTextWidget");
                if (richType == null)
                {
                    // Try alternate namespaces
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
                MCMSettings.DebugLog("LoreSection: RichTextWidget creation failed: " + ex.ToString());
            }
            return null;
        }

        // ─────────── Insertion Point Detection ───────────

        /// <summary>
        /// Finds the right place to insert the Lore section.
        /// Strategy: Find the "Companions" or "Friends" section header in the widget tree,
        /// then insert BEFORE it (which puts us after Info).
        /// This avoids matching hint text in the description RichTextWidget.
        /// </summary>
        public static bool FindInsertionPoint(Widget root, out Widget parent, out int index)
        {
            parent = null;
            index = -1;

            string pageType = EncyclopediaPageTracker.CurrentPageType ?? "Hero";
            bool isHero = pageType == "Hero";

            // Insert right after the Info section for ALL page types (Hero included) when the
            // after-Info anchor can be found. This places our sections at the top, above the
            // native ones, and — crucially — NEVER lands between a native section's header and its
            // content. A user reported our Timeline/Relation Notes sections inserting BETWEEN the
            // "Friends" header and the friend portraits (worse with other UI mods like Affairs of
            // Calradia that reshape the encyclopedia tree), which split/broke the Friends/Enemies
            // layout. The Friends/Enemies anchor below is kept only as a fallback.
            if (FindInsertionAfterInfo(root, out parent, out index))
                return true;

            // Strategy 0: Find section widgets by Id (most reliable).
            // Bannerlord encyclopedia widgets have Ids like "FriendsSection", "EnemiesSection", etc.
            foreach (string sectionName in new[] { "Companions", "Friends", "Allies", "Enemies" })
            {
                Widget sectionById = FindWidgetByIdContaining(root, sectionName, 0);
                if (sectionById == null) continue;

                // Walk up from the matched widget to find the section-level container.
                // Skip parents whose children are all tiny (header bars).
                Widget current = sectionById;
                for (int depth = 0; depth < 10 && current != null; depth++)
                {
                    Widget sectionParent = current.ParentWidget;
                    if (sectionParent == null) break;

                    int idx = FindChildIndex(sectionParent, current);
                    if (idx >= 0 && sectionParent.ChildCount >= 2)
                    {
                        // Check siblings have substantial descendants (not just a header bar)
                        int maxSibDesc = 0;
                        for (int si = 0; si < sectionParent.ChildCount; si++)
                        {
                            Widget sib = sectionParent.GetChild(si);
                            if (sib == current) continue;
                            int sd = CountDescendants(sib);
                            if (sd > maxSibDesc) maxSibDesc = sd;
                        }

                        if (maxSibDesc < 5)
                        {
                            MCMSettings.DebugLog("LoreSection: Strategy0 depth=" + depth
                                + " skipping — siblings too small (maxSibDesc=" + maxSibDesc + ")");
                            current = sectionParent;
                            continue;
                        }

                        parent = sectionParent;
                        index = idx;
                        MCMSettings.DebugLog("LoreSection: found '" + sectionName + "' by Id ('"
                            + (sectionById.Id ?? "") + "'), inserting at index " + idx
                            + " in " + sectionParent.GetType().Name
                            + " (children=" + sectionParent.ChildCount + ") depth=" + depth);
                        return true;
                    }

                    current = sectionParent;
                }
            }

            // Strategy 1: Find a section header widget for "Companions", "Friends", or "Enemies"
            // by text content, then walk up to find the sections container.
            foreach (string sectionName in new[] { "Companions", "Friends", "Allies", "Enemies" })
            {
                Widget sectionWidget = FindSectionHeaderWidget(root, sectionName);
                if (sectionWidget == null) continue;

                MCMSettings.DebugLog("LoreSection: found '" + sectionName + "' section widget, type="
                    + sectionWidget.GetType().Name);

                // Walk up to find a parent that is the sections container.
                // The sections container should have multiple children, one of which contains
                // our found section. We also check if a sibling contains "Info" text as confirmation,
                // but don't require it (relaxed for overlay/settlement views).
                // IMPORTANT: Skip parents whose children are all small (few descendants) — those
                // are horizontal header bars, not vertical section containers.
                Widget current = sectionWidget;
                for (int depth = 0; depth < 8 && current != null; depth++)
                {
                    Widget sectionParent = current.ParentWidget;
                    if (sectionParent == null) break;

                    if (sectionParent.ChildCount >= 2)
                    {
                        int idx = FindChildIndex(sectionParent, current);
                        if (idx >= 0)
                        {
                            // Check that this parent's children are actual sections (have many descendants),
                            // not just text labels in a horizontal header bar.
                            int maxChildDescendants = 0;
                            bool hasInfoSibling = false;
                            for (int i = 0; i < sectionParent.ChildCount; i++)
                            {
                                Widget sibling = sectionParent.GetChild(i);
                                if (sibling == current) continue;
                                int sibDesc = CountDescendants(sibling);
                                if (sibDesc > maxChildDescendants) maxChildDescendants = sibDesc;
                                if (sibling.GetType().Name == "EncyclopediaDividerButtonWidget"
                                    || ContainsText(sibling, "Info"))
                                {
                                    hasInfoSibling = true;
                                }
                            }

                            MCMSettings.DebugLog("LoreSection: Strategy1 depth=" + depth
                                + " parent=" + sectionParent.GetType().Name
                                + " children=" + sectionParent.ChildCount
                                + " maxSibDesc=" + maxChildDescendants
                                + " hasInfoSibling=" + hasInfoSibling);

                            // If siblings have very few descendants (< 5), this is likely a header bar
                            // (e.g., horizontal row of text labels). Keep walking up.
                            if (maxChildDescendants < 5 && !hasInfoSibling)
                            {
                                MCMSettings.DebugLog("LoreSection: skipping — siblings too small, likely header bar");
                                current = sectionParent;
                                continue;
                            }

                            if (hasInfoSibling || sectionParent.ChildCount >= 3)
                            {
                                parent = sectionParent;
                                index = idx;
                                MCMSettings.DebugLog("LoreSection: inserting before '" + sectionName
                                    + "' at index " + idx + " in " + sectionParent.GetType().Name
                                    + " (children=" + sectionParent.ChildCount + ") depth=" + depth
                                    + " hasInfoSibling=" + hasInfoSibling);
                                return true;
                            }
                        }
                    }

                    current = sectionParent;
                }
            }

            // Strategy 2: Fallback — find the Info section header and insert after it
            Widget infoHeader = FindSectionHeaderWidget(root, "Info");
            if (infoHeader != null)
            {
                Widget current = infoHeader;
                for (int depth = 0; depth < 8 && current != null; depth++)
                {
                    Widget p = current.ParentWidget;
                    if (p != null && p.ChildCount >= 3)
                    {
                        int idx = FindChildIndex(p, current);
                        if (idx >= 0)
                        {
                            parent = p;
                            index = idx + 1;
                            MCMSettings.DebugLog("LoreSection: fallback — inserting after Info at index "
                                + (idx + 1) + " in " + p.GetType().Name + " (children=" + p.ChildCount + ")");
                            return true;
                        }
                    }
                    current = p;
                }
            }

            return false;
        }

        internal static bool FindInsertionAfterInfo(Widget root, out Widget parent, out int index)
        {
            parent = null;
            index = -1;

            var allWidgets = new List<Widget>();
            CollectAllWidgets(root, allWidgets, 20);

            foreach (var widget in allWidgets)
            {
                string typeName = widget.GetType().Name;
                bool isDividerButton = typeName.Contains("DividerButton");
                bool isOurStats = widget.Id != null && widget.Id.StartsWith("EditableEncyclopediaStats");

                if (!isDividerButton && !isOurStats) continue;

                // For native DividerButton, check for "Info" text
                if (isDividerButton)
                {
                    string text = FindTextInWidget(widget, 3);
                    if (string.IsNullOrEmpty(text) || text.IndexOf("Info", StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                // For our custom Stats widget, it IS the Info section

                Widget infoParent = widget.ParentWidget;
                if (infoParent == null) continue;

                int infoIdx = FindChildIndex(infoParent, widget);
                if (infoIdx < 0) continue;

                int insertIdx = infoIdx + 1;
                for (int i = infoIdx + 1; i < infoParent.ChildCount; i++)
                {
                    var child = infoParent.GetChild(i);
                    if (child == null) break;

                    // Skip past our own Stats widgets (they're part of the Info section)
                    if (child.Id != null && child.Id.StartsWith("EditableEncyclopediaStats"))
                    {
                        insertIdx = i + 1;
                        continue;
                    }

                    if (child.GetType().Name.Contains("DividerButton")) break;

                    string childText = FindTextInWidget(child, 2);
                    if (!string.IsNullOrEmpty(childText))
                    {
                        bool isSection = false;
                        foreach (string header in new[] { "Leader", "Members", "Settlements", "Clans",
                            "Wars", "Owner", "Notable", "Notables", "Villages", "Fiefs" })
                        {
                            if (childText.IndexOf(header, StringComparison.OrdinalIgnoreCase) >= 0)
                            { isSection = true; break; }
                        }
                        if (isSection) break;
                    }

                    if (child.Id != null && (child.Id.StartsWith("EditableEncyclopedia") ||
                        child.Id.Contains("Journal") || child.Id.Contains("Timeline")))
                        break;

                    insertIdx = i + 1;
                }

                parent = infoParent;
                index = insertIdx;
                MCMSettings.DebugLog("LoreSection: found Info section, inserting after it at index " + insertIdx
                    + " in " + infoParent.GetType().Name + " (children=" + infoParent.ChildCount + ")");
                return true;
            }

            return false;
        }

        private static string FindTextInWidget(Widget widget, int maxDepth)
        {
            if (widget == null || maxDepth <= 0) return null;
            string text = TryGetText(widget);
            if (!string.IsNullOrEmpty(text)) return text;
            for (int i = 0; i < widget.ChildCount && i < 8; i++)
            {
                text = FindTextInWidget(widget.GetChild(i), maxDepth - 1);
                if (!string.IsNullOrEmpty(text)) return text;
            }
            return null;
        }

        private static void CollectAllWidgets(Widget root, List<Widget> results, int maxDepth)
        {
            if (root == null || maxDepth <= 0) return;
            results.Add(root);
            for (int i = 0; i < root.ChildCount; i++)
                CollectAllWidgets(root.GetChild(i), results, maxDepth - 1);
        }

        /// <summary>
        /// Finds a section header widget by text. Skips RichTextWidget matches
        /// (which are description text, not section headers) and skips our own
        /// injected widgets.
        /// </summary>
        private static Widget FindSectionHeaderWidget(Widget parent, string text, int depth = 0)
        {
            if (parent == null || depth > MaxWidgetTreeDepth) return null;

            // Skip our own widgets
            if (parent.Id != null && parent.Id.StartsWith("EditableEncyclopedia"))
                return null;

            // Skip RichTextWidget — those are description text, not headers
            string typeName = parent.GetType().Name;
            if (typeName != "RichTextWidget")
            {
                string widgetText = TryGetText(parent);
                if (!string.IsNullOrEmpty(widgetText)
                    && widgetText.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                    return parent;
            }

            for (int i = 0; i < parent.ChildCount; i++)
            {
                var result = FindSectionHeaderWidget(parent.GetChild(i), text, depth + 1);
                if (result != null) return result;
            }
            return null;
        }

        // CHANGE 2 helpers (2026-05-27 ROT fix): mod-agnostic structural anchor.
        // Walks tree, scores candidates by structural properties (child count, width),
        // returns best fit. Works on any prefab — vanilla, ROT, future mods.
        public static Widget FindBestStructuralAnchor(Widget root)
        {
            if (root == null) return null;
            Widget best = null;
            int bestScore = 0;
            ScanForStructuralAnchor(root, 0, ref best, ref bestScore);
            return best;
        }

        private static void ScanForStructuralAnchor(Widget node, int depth, ref Widget best, ref int bestScore)
        {
            if (node == null || depth > 12) return;
            string typeName = node.GetType().Name;
            bool isContainer = typeName == "ListPanel" || typeName == "Widget";
            if (isContainer)
            {
                int childCount = node.ChildCount;
                if (childCount >= 6 && childCount <= 50)
                {
                    int descendants = CountDescendants(node);
                    int score = descendants + childCount * 5;
                    if (node.SuggestedWidth >= 400) score += 50;
                    try { if (node.WidthSizePolicy.ToString() == "StretchToParent") score += 30; } catch { }
                    if (score > bestScore) { bestScore = score; best = node; }
                }
            }
            for (int i = 0; i < node.ChildCount; i++)
                ScanForStructuralAnchor(node.GetChild(i), depth + 1, ref best, ref bestScore);
        }

        // CHANGE 1: Safety guard. Rejects containers that would produce a warped layout
        // (narrow side panels, sparse containers). The ROT settlement bug came from
        // FindMainContentPanel returning a 4-child side panel — this check catches it.
        public static bool IsAcceptableInsertionContainer(Widget w)
        {
            if (w == null) return false;
            if (w.ChildCount < 6) return false;
            if (w.SuggestedWidth > 0 && w.SuggestedWidth < 350) return false;
            return true;
        }

        /// <summary>
        /// Finds the main content panel (typically a ListPanel inside a ScrollablePanel)
        /// as a fallback insertion target.
        /// </summary>
        public static Widget FindMainContentPanel(Widget root)
        {
            // Look for a ListPanel with multiple children (the page sections container)
            return FindWidgetByType(root, "ListPanel", minChildren: 3);
        }

        // ─────────── Layer / Root Detection (shared with TimestampWidgetInjector) ───────────

        public static GauntletLayer FindEncyclopediaLayer(ScreenBase topScreen, out bool wasSizeFallback)
        {
            wasSizeFallback = false;
            // Primary: use the layer captured by EncyclopediaLayerCapturePatch.
            // This is the most reliable path — the layer was captured via Harmony postfix
            // on GauntletMapEncyclopediaView when the encyclopedia opened.
            var capturedLayer = EncyclopediaPageTracker.EncyclopediaLayerRef;
            if (capturedLayer is GauntletLayer captured)
            {
                // Verify the layer is still on the screen (not stale from a previous session)
                foreach (var layer in topScreen.Layers)
                {
                    if (object.ReferenceEquals(layer, captured))
                        return captured;
                }
                // Stale reference — clear it
                EncyclopediaPageTracker.EncyclopediaLayerRef = null;
            }

            // Secondary: try reading the layer directly from the encyclopedia manager
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
                        {
                            EncyclopediaPageTracker.EncyclopediaLayerRef = gl;
                            return gl;
                        }
                        break;
                    }
                    encType = encType.BaseType;
                }
            }

            // Fallback: search all layers for the best encyclopedia candidate.
            // Prefer layers that contain encyclopedia section text (Friends/Enemies/Info)
            // over layers that just happen to be large (e.g. settlement overlay).
            GauntletLayer bestContentLayer = null;
            int bestContentDesc = 0;
            GauntletLayer bestSizeLayer = null;
            int bestSizeDesc = 0;
            var layers = topScreen.Layers;
            MCMSettings.DebugLog("LoreSection: FindEncyclopediaLayer scanning " + layers.Count + " layers");
            for (int i = 0; i < layers.Count; i++)
            {
                if (!(layers[i] is GauntletLayer gl)) continue;
                Widget root = GetLayerRootWidget(gl);
                if (root == null) continue;
                int desc = CountDescendants(root);
                MCMSettings.DebugLog(new StringBuilder("LoreSection: layer ")
                    .Append(i).Append(" type=").Append(root.GetType().Name)
                    .Append(" descendants=").Append(desc).ToString());
                if (desc < MinLayerDescendants || desc > MaxLayerDescendants) continue;

                // Check if this layer contains encyclopedia section headers.
                // Require EncyclopediaDividerButtonWidget to distinguish the real
                // encyclopedia from Clan/Party screens that also have Friends/Enemies.
                bool hasEncContent = (ContainsText(root, "Friends") || ContainsText(root, "Enemies")
                                  || ContainsText(root, "Info"))
                                  && ContainsWidgetType(root, "EncyclopediaDividerButtonWidget");
                if (hasEncContent && desc > bestContentDesc)
                {
                    bestContentDesc = desc;
                    bestContentLayer = gl;
                }
                // Only consider layers with EncyclopediaDividerButtonWidget for
                // size fallback too — Clan/Party screens can have 800+ descendants
                // but no encyclopedia widgets.
                if (desc > bestSizeDesc && ContainsWidgetType(root, "EncyclopediaDividerButtonWidget"))
                {
                    bestSizeDesc = desc;
                    bestSizeLayer = gl;
                }
            }
            // Prefer content-matched layer (has "Friends"/"Enemies"/"Info" text).
            if (bestContentLayer != null)
            {
                MCMSettings.DebugLog("LoreSection: selected content-matched layer (descendants=" + bestContentDesc + ")");
                EncyclopediaPageTracker.EncyclopediaLayerRef = bestContentLayer;
                return bestContentLayer;
            }
            // Fallback: use the largest layer in the valid range (10-2000 descendants).
            // Content text matching can fail due to localization, mods, or timing,
            // but a layer with 100+ descendants is almost certainly the encyclopedia.
            if (bestSizeLayer != null && bestSizeDesc >= MinDescendantsForContent)
            {
                MCMSettings.DebugLog("LoreSection: no content match, using size-based fallback layer (descendants=" + bestSizeDesc + ")");
                wasSizeFallback = true;
                EncyclopediaPageTracker.EncyclopediaLayerRef = bestSizeLayer;
                return bestSizeLayer;
            }
            MCMSettings.DebugLog("LoreSection: no suitable layer found, returning null to trigger retry");
            return null;
        }

        public static Widget GetLayerRootWidget(GauntletLayer layer)
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
            catch (Exception ex) { MCMSettings.DebugLog("LoreSectionInjector: GetLayerRootWidget failed: " + ex.ToString()); }
            return null;
        }

        /// <summary>
        /// 2026-05-30: 1.4.x LayoutMethod-inversion compat for STATIC XML prefabs that hardcode
        /// "VerticalBottomToTop" (the pre-1.4 top-down value). On 1.4+ that renders bottom-up, so
        /// walk the loaded layer's widget tree and re-point every such stack to the version-correct
        /// top-down enum. No-op on &lt;=1.3 (the as-authored prefab is already correct there).
        /// </summary>
        public static void ApplyTopDownLayoutToTree(GauntletLayer layer)
        {
            try
            {
                if (BannerlordVersion.IsLayoutEnumInverted()) return; // <=1.3: prefab already correct
                string topDown = BannerlordVersion.TopDownLayoutEnumName(); // "VerticalTopToBottom" on 1.4
                if (topDown == "VerticalBottomToTop") return;               // defensive: nothing to change
                Widget root = GetLayerRootWidget(layer);
                if (root == null) { MCMSettings.DebugLog("ApplyTopDownLayoutToTree: could not resolve layer root"); return; }
                int n = FlipVerticalBottomToTop(root, topDown, 0);
                MCMSettings.DebugLog("ApplyTopDownLayoutToTree: re-pointed " + n + " vertical stack(s) to '" + topDown + "'");
            }
            catch (Exception ex) { MCMSettings.DebugLog("ApplyTopDownLayoutToTree failed: " + ex.ToString()); }
        }

        private static int FlipVerticalBottomToTop(Widget w, string topDownName, int depth)
        {
            if (w == null || depth > 40) return 0;
            int count = 0;
            try
            {
                var layoutProp = w.GetType().GetProperty("StackLayout", AllFlags);
                if (layoutProp != null)
                {
                    var layout = layoutProp.GetValue(w);
                    if (layout != null)
                    {
                        var methodProp = layout.GetType().GetProperty("LayoutMethod", AllFlags);
                        if (methodProp != null && methodProp.CanWrite)
                        {
                            var cur = methodProp.GetValue(layout);
                            if (cur != null && cur.ToString() == "VerticalBottomToTop")
                            {
                                foreach (var ev in Enum.GetValues(methodProp.PropertyType))
                                {
                                    if (ev.ToString() == topDownName) { methodProp.SetValue(layout, ev); count++; break; }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            for (int i = 0; i < w.ChildCount; i++)
                count += FlipVerticalBottomToTop(w.GetChild(i), topDownName, depth + 1);
            return count;
        }

        // ─────────── Widget Tree Helpers ───────────

        /// <summary>
        /// Finds the encyclopedia content widget by looking for a subtree that contains
        /// encyclopedia section headers (Info, Friends, Enemies). Avoids picking settlement
        /// overlays or other non-encyclopedia widgets.
        /// </summary>
        public static Widget FindEncyclopediaContentWidget(Widget root)
        {
            if (root == null) return null;
            // Check if this root itself contains encyclopedia sections
            if (!ContainsText(root, "Friends") && !ContainsText(root, "Enemies"))
                return null;

            // Walk down recursively to find the most specific container that still
            // contains encyclopedia section text. Stop when no child contains it.
            return FindDeepestEncyclopediaContainer(root);
        }

        private static Widget FindDeepestEncyclopediaContainer(Widget widget, int depth = 0)
        {
            if (widget == null || depth > MaxSearchDepth) return widget;
            for (int i = 0; i < widget.ChildCount; i++)
            {
                Widget child = widget.GetChild(i);
                // Skip settlement overlay widgets
                if (child.GetType().Name.Contains("SettlementOverlay")) continue;
                if (ContainsText(child, "Friends") || ContainsText(child, "Enemies"))
                    return FindDeepestEncyclopediaContainer(child, depth + 1);
            }
            return widget;
        }

        public static Widget FindLargestChild(Widget parent)
        {
            Widget best = null;
            int bestDesc = 0;
            Widget bestNonOverlay = null;
            int bestNonOverlayDesc = 0;
            for (int i = 0; i < parent.ChildCount; i++)
            {
                Widget child = parent.GetChild(i);
                int desc = CountDescendants(child);
                bool isOverlay = child.GetType().Name.Contains("SettlementOverlay")
                              || child.GetType().Name.Contains("NavalMap");
                if (desc > bestDesc)
                {
                    bestDesc = desc;
                    best = child;
                }
                if (!isOverlay && desc > bestNonOverlayDesc)
                {
                    bestNonOverlayDesc = desc;
                    bestNonOverlay = child;
                }
            }
            // Prefer non-overlay widgets to avoid picking settlement UI
            return bestNonOverlay ?? best;
        }

        private static void DumpWidgetTree(Widget widget, string indent, int depth, int maxDepth)
        {
            if (widget == null || depth > maxDepth) return;
            string id = widget.Id ?? "(no id)";
            string text = TryGetText(widget) ?? "";
            if (text.Length > TextSnippetMaxLength) text = text.Substring(0, TextSnippetMaxLength) + "...";
            string typeName = widget.GetType().Name;
            var sb = new StringBuilder("LoreSection:TREE ");
            sb.Append(indent).Append(typeName)
              .Append(" id=").Append(id)
              .Append(" children=").Append(widget.ChildCount);
            if (text.Length > 0)
                sb.Append(" text=\"").Append(text).Append('"');
            MCMSettings.DebugLog(sb.ToString());
            var childIndent = indent + "  ";
            for (int i = 0; i < widget.ChildCount; i++)
                DumpWidgetTree(widget.GetChild(i), childIndent, depth + 1, maxDepth);
        }

        public static int CountDescendants(Widget widget, int depth = 0)
        {
            if (widget == null || depth > MaxWidgetTreeDepth) return 0;
            int count = 0;
            for (int i = 0; i < widget.ChildCount; i++)
                count += 1 + CountDescendants(widget.GetChild(i), depth + 1);
            return count;
        }

        public static int FindChildIndex(Widget parent, Widget child)
        {
            for (int i = 0; i < parent.ChildCount; i++)
                if (parent.GetChild(i) == child) return i;
            return -1;
        }

        /// <summary>
        /// Searches for a widget containing the given text substring.
        /// </summary>
        private static Widget FindWidgetByTextContent(Widget parent, string substring, int depth = 0)
        {
            if (parent == null || depth > MaxWidgetTreeDepth) return null;
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

        /// <summary>
        /// Checks if a widget subtree contains the given text (shallow, max depth 5).
        /// </summary>
        private static bool ContainsText(Widget widget, string substring, int depth = 0)
        {
            if (widget == null || depth > MaxSearchDepth) return false;
            string text = TryGetText(widget);
            if (!string.IsNullOrEmpty(text)
                && text.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            for (int i = 0; i < widget.ChildCount; i++)
                if (ContainsText(widget.GetChild(i), substring, depth + 1)) return true;
            return false;
        }

        /// <summary>
        /// Checks if a widget subtree contains a widget with the given type name.
        /// Used to verify a layer is an encyclopedia page (has EncyclopediaDividerButtonWidget).
        /// </summary>
        public static bool ContainsWidgetType(Widget widget, string typeName, int depth = 0)
        {
            if (widget == null || depth > MaxContainsTypeDepth) return false;
            if (widget.GetType().Name == typeName) return true;
            for (int i = 0; i < widget.ChildCount; i++)
                if (ContainsWidgetType(widget.GetChild(i), typeName, depth + 1)) return true;
            return false;
        }

        /// <summary>
        /// Finds a widget by its type name (e.g., "ListPanel", "ScrollablePanel"),
        /// optionally requiring a minimum number of children.
        /// </summary>
        public static Widget FindWidgetByIdContaining(Widget widget, string idFragment, int depth)
        {
            if (widget == null || depth > MaxWidgetTreeDepth) return null;
            // Skip our own injected widgets
            if (widget.Id != null && widget.Id.StartsWith("EditableEncyclopedia")) return null;
            if (widget.Id != null && widget.Id.IndexOf(idFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                return widget;
            for (int i = 0; i < widget.ChildCount; i++)
            {
                var result = FindWidgetByIdContaining(widget.GetChild(i), idFragment, depth + 1);
                if (result != null) return result;
            }
            return null;
        }

        private static Widget FindWidgetByType(Widget parent, string typeName, int minChildren = 0, int depth = 0)
        {
            if (parent == null || depth > MaxSearchDepth) return null;
            if (parent.GetType().Name == typeName && parent.ChildCount >= minChildren)
                return parent;
            for (int i = 0; i < parent.ChildCount; i++)
            {
                var result = FindWidgetByType(parent.GetChild(i), typeName, minChildren, depth + 1);
                if (result != null) return result;
            }
            return null;
        }

        public static string TryGetText(Widget widget)
        {
            if (widget is TextWidget tw) return tw.Text;
            try
            {
                var textProp = widget.GetType().GetProperty("Text", BindingFlags.Instance | BindingFlags.Public);
                if (textProp != null && textProp.PropertyType == typeof(string))
                    return textProp.GetValue(widget) as string;
            }
            catch (Exception ex) { MCMSettings.DebugLog("LoreSectionInjector: TryGetText reflection failed: " + ex.ToString()); }
            return null;
        }

        /// <summary>
        /// Finds a brush by name in the widget tree (e.g., "Encyclopedia.Title").
        /// </summary>
        public static Brush FindBrushByName(Widget root, string brushName, int depth = 0)
        {
            if (root == null || depth > MaxSearchDepth) return null;
            // Check BrushWidget
            if (root is BrushWidget bw && bw.ReadOnlyBrush != null)
            {
                string name = bw.ReadOnlyBrush.Name;
                // Match both "BrushName" and "BrushName(Clone)" variants
                if (name == brushName || name == brushName + "(Clone)")
                    return bw.ReadOnlyBrush;
            }
            // Also check TextWidget (stat labels use TextWidget, not BrushWidget)
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
        /// </summary>
        public static Brush FindAnyTextBrush(Widget root, int depth = 0)
        {
            if (root == null || depth > MaxBrushSearchDepth) return null;
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

        /// <summary>
        /// Collects all unique brush names from TextWidget and BrushWidget nodes in the tree.
        /// Used for debug logging to discover available brush styles.
        /// </summary>
        private static void CollectTextBrushNames(Widget root, System.Collections.Generic.HashSet<string> names, int depth)
        {
            if (root == null || depth > MaxSearchDepth) return;
            if (root is TextWidget tw && tw.ReadOnlyBrush != null && tw.ReadOnlyBrush.Name != null)
                names.Add(tw.ReadOnlyBrush.Name);
            if (root is BrushWidget bw && bw.ReadOnlyBrush != null && bw.ReadOnlyBrush.Name != null)
                names.Add(bw.ReadOnlyBrush.Name);
            for (int i = 0; i < root.ChildCount; i++)
                CollectTextBrushNames(root.GetChild(i), names, depth + 1);
        }

        // ─────────── Debug Tree Dump ───────────

        private static void DumpWidgetTree(Widget widget, int depth, int maxDepth)
        {
            if (widget == null || depth > maxDepth) return;

            string typeName = widget.GetType().Name;
            string id = widget.Id ?? "";
            string text = TryGetText(widget);

            var sb = new StringBuilder("LoreSection:TREE ");
            sb.Append(' ', depth * 2);
            sb.Append(typeName);
            if (!string.IsNullOrEmpty(id))
                sb.Append(" #").Append(id);
            sb.Append(" children=").Append(widget.ChildCount);
            if (!string.IsNullOrEmpty(text))
            {
                string snippet = text.Length > TextSnippetMaxLength ? text.Substring(0, TextSnippetMaxLength) + "..." : text;
                sb.Append(" text='").Append(snippet).Append('\'');
            }
            if (widget is BrushWidget bw2 && bw2.ReadOnlyBrush != null)
                sb.Append(" brush=").Append(bw2.ReadOnlyBrush.Name);

            MCMSettings.DebugLog(sb.ToString());

            for (int i = 0; i < widget.ChildCount; i++)
                DumpWidgetTree(widget.GetChild(i), depth + 1, maxDepth);
        }

        // ================================================================
        //  Harmony patch: force Left alignment on lore section TextWidgets
        // ================================================================

        private static FieldInfo _twoDimTextField;     // TextWidget._text
        private static FieldInfo _textHAlignField;     // Text._horizontalAlignment
        private static object _leftAlignValue;          // TextHorizontalAlignment.Left enum value
        public static object _centerAlignValue;        // TextHorizontalAlignment.Center enum value
        private static bool _harmonyPatchApplied = false;

        /// <summary>
        /// Applies a Harmony postfix on TextWidget's render/update method so that
        /// any TextWidget whose Id starts with "EditableEncyclopedia_Lore" always
        /// has its internal _text._horizontalAlignment forced to Left.
        /// </summary>
        public static void TryApplyHarmonyPatch(Harmony harmony)
        {
            if (_harmonyPatchApplied) return;
            try
            {
                var textWidgetType = typeof(TextWidget);

                // Cache the reflection fields we need for the postfix
                _twoDimTextField = textWidgetType.GetField("_text", AllFlags);
                if (_twoDimTextField == null)
                {
                    MCMSettings.DebugLog("LoreSection: Harmony: _text field not found on TextWidget");
                    return;
                }

                // Get the _horizontalAlignment field on the Text object
                var textType = _twoDimTextField.FieldType;
                _textHAlignField = textType.GetField("_horizontalAlignment", AllFlags);
                if (_textHAlignField == null)
                {
                    // Try property-backed field
                    _textHAlignField = textType.GetField("<HorizontalAlignment>k__BackingField", AllFlags);
                }
                if (_textHAlignField == null)
                {
                    MCMSettings.DebugLog("LoreSection: Harmony: _horizontalAlignment field not found on Text");
                    return;
                }

                // Cache the Left and Center enum values
                var enumType = _textHAlignField.FieldType;
                foreach (var ev in Enum.GetValues(enumType))
                {
                    string name = ev.ToString();
                    if (name == "Left")
                        _leftAlignValue = ev;
                    else if (name == "Center")
                        _centerAlignValue = ev;
                }
                if (_leftAlignValue == null)
                {
                    MCMSettings.DebugLog("LoreSection: Harmony: 'Left' not found in " + enumType.Name);
                    return;
                }

                // Find the target method — try OnRender, OnLateUpdate, HandleTextChanged
                string[] candidates = { "OnRender", "OnLateUpdate", "UpdateText", "SetText" };
                MethodInfo target = null;
                string patchedMethod = null;
                foreach (var name in candidates)
                {
                    target = textWidgetType.GetMethod(name, AllFlags);
                    if (target != null)
                    {
                        patchedMethod = name;
                        break;
                    }
                }

                if (target == null)
                {
                    // Fallback: list all methods to find one
                    var methods = textWidgetType.GetMethods(AllFlags);
                    var names = new List<string>();
                    foreach (var m in methods)
                    {
                        if (m.DeclaringType == textWidgetType)
                            names.Add(m.Name);
                    }
                    MCMSettings.DebugLog("LoreSection: Harmony: no render method found. TextWidget methods: "
                        + string.Join(", ", names));
                    return;
                }

                var prefix = typeof(LoreSectionInjector).GetMethod(
                    nameof(TextWidgetRenderPrefix), BindingFlags.Static | BindingFlags.NonPublic);
                if (prefix == null)
                {
                    MCMSettings.DebugLog("LoreSection: Harmony: TextWidgetRenderPrefix method not found");
                    return;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                _harmonyPatchApplied = true;
                MCMSettings.DebugLog("LoreSection: Harmony: patched TextWidget." + patchedMethod
                    + "() to force Left alignment on lore widgets");
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("LoreSection: Harmony patch failed: " + ex.ToString());
            }
        }

        /// <summary>
        /// Harmony prefix: BEFORE TextWidget renders, if it's one of our lore widgets
        /// (except the title), force _text._horizontalAlignment = Left.
        /// The title ("Goals") is intentionally left centered.
        /// </summary>
        private static void TextWidgetRenderPrefix(TextWidget __instance)
        {
            try
            {
                if (_twoDimTextField == null || _textHAlignField == null) return;

                var id = __instance.Id;
                if (id == null)
                    return;

                // Journal header — force Center alignment
                if (id == "JournalHeader" && _centerAlignValue != null)
                {
                    var textObj = _twoDimTextField.GetValue(__instance);
                    if (textObj != null)
                        _textHAlignField.SetValue(textObj, _centerAlignValue);
                    return;
                }

                // Handle lore widgets, journal entry widgets, and relation notes — force Left alignment
                bool isLore = id.StartsWith("EditableEncyclopedia_Lore");
                bool isJournal = id.StartsWith("JournalEntry");
                bool isRelNotes = id.StartsWith("EditableEncyclopedia_RelNotes");
                if (!isLore && !isJournal && !isRelNotes)
                    return;
                // Skip the lore title/label and relation notes title — they should stay centered
                if (id.StartsWith("EditableEncyclopedia_LoreTitle")
                    || id.StartsWith("EditableEncyclopedia_LoreLabel")
                    || id.StartsWith("EditableEncyclopedia_RelNotesTitle"))
                    return;

                var textObj2 = _twoDimTextField.GetValue(__instance);
                if (textObj2 == null) return;

                _textHAlignField.SetValue(textObj2, _leftAlignValue);
            }
            catch (Exception ex) { MCMSettings.DebugLog("LoreSectionInjector: TextWidgetRenderPrefix failed: " + ex.ToString()); }
        }
    }

    /// <summary>
    /// Helper methods for LoreSectionInjector widget click handling and brush styling.
    /// </summary>
    public static class LoreSectionHelpers
    {
        private static readonly BindingFlags AllFlags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        public static void HookWidgetClick(Widget widget, Action onClick)
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
                            MCMSettings.DebugLog("LoreSection: click handler error: " + ex.ToString());
                        }
                    }
                };

                eventFireEvent.AddEventHandler(widget, handler);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("LoreSection: HookWidgetClick error: " + ex.ToString());
            }
        }

        public static void SetBrushColor(Brush brush, float r, float g, float b, float a)
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
            catch (Exception ex) { MCMSettings.DebugLog("LoreSectionInjector: SetBrushColor failed: " + ex.ToString()); }
        }

        public static void HookCollapseToggleWithPersistence(Widget headerWidget, Widget contentContainer,
            BrushWidget arrow, Widget nativeHeader, string heroId, Dictionary<string, bool> collapseStates)
        {
            try
            {
                var eventFireEvent = headerWidget.GetType().GetEvent("EventFire",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (eventFireEvent == null) return;

                Action<Widget, string, object[]> eventHandler = (Widget sender, string eventName, object[] args) =>
                {
                    if (eventName == "Click")
                    {
                        bool wasVisible = contentContainer.IsVisible;
                        contentContainer.IsVisible = !wasVisible;
                        collapseStates[heroId] = wasVisible;

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
                            catch (Exception ex) { MCMSettings.DebugLog("LoreSectionInjector: persistent collapse arrow SetState failed: " + ex.ToString()); }
                        }
                    }
                };

                eventFireEvent.AddEventHandler(headerWidget, eventHandler);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("LoreSection: HookCollapseToggle error: " + ex.ToString());
            }
        }
    }
}
