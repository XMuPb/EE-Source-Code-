using System;
using System.Reflection;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.ScreenSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Injects an "Edited: ..." timestamp TextWidget into the encyclopedia page,
    /// positioned directly above an anchor widget found by text search:
    ///   1. "seen" — hero pages: above "Never seen before" / "Last seen at …"
    ///   2. Localized edited prefix — all edited pages (e.g. "[Edited]", "[Bearbeitet]", "[已编辑]")
    ///   3. Localized hint shortcut — all pages with hint (e.g. "Ctrl+E", "Strg+E")
    /// Always removes and re-injects on each call to handle page navigation.
    /// If the widget tree isn't ready yet (back/return navigation), retries
    /// automatically with a short delay.
    /// </summary>
    internal static class TimestampWidgetInjector
    {
        private static TextWidget _textWidget;
        private static bool _widgetReady;

        // Retry state for deferred injection when widget tree isn't ready
        private static string _pendingText;
        private static bool _pendingVisible;

        /// <summary>Current timestamp text (e.g. "Edited: Day 1 of Summer, 1084").</summary>
        public static string CurrentText => _pendingVisible ? _pendingText : null;
        private static int _retryCount;
        private static System.Threading.Timer _retryTimer;
        private const int MaxRetries = 10;
        private const int RetryDelayMs = 150;

        // Main-thread deferred operations — Gauntlet widgets must only be
        // modified from the main game thread, never from timer callbacks.
        private static volatile bool _retryPending;
        private static volatile bool _clearPending;

        private const int MaxWidgetTreeDepth = 20;
        private const int MinLayerDescendants = 10;
        private const int MaxLayerDescendants = 2000;
        private const int LogTextTruncateLength = 60;
        private const int DescSearchLength = 30;
        private const int MinDescLength = 4;
        private const float TimestampMarginBottom = 5f;

        private static readonly BindingFlags AllFlags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // Cached reflection accessor for Text property on non-TextWidget types
        private static PropertyInfo _cachedTextProp;
        private static Type _cachedTextPropType;

        public static void InjectOrUpdate(string text, bool visible)
        {
            // Cancel any pending retry — this is a fresh call
            DisposeRetryTimer();
            _clearPending = false;
            _retryPending = false;
            _pendingText = text;
            _pendingVisible = visible;
            _retryCount = 0;

            DoInjectOrUpdate(text, visible);
        }

        public static void Clear()
        {
            DisposeRetryTimer();
            _retryPending = false;
            RemoveOldWidget();
        }

        // ForceReinject removed — DescriptionFormatter now includes timestamp directly

        /// <summary>
        /// Schedules a Clear() to run on the next main-thread tick.
        /// Safe to call from any thread (timer callbacks, etc.).
        /// </summary>
        public static void ScheduleClear()
        {
            DisposeRetryTimer();
            _retryPending = false;
            _clearPending = true;
        }

        /// <summary>
        /// Must be called from the main game thread (e.g., OnApplicationTick).
        /// Processes deferred retry and clear operations that were scheduled
        /// from background timer threads.
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
                // Abort retry if encyclopedia was closed
                if (!EncyclopediaPageTracker.IsEncyclopediaOpen())
                {
                    _retryCount = MaxRetries;
                    DisposeRetryTimer();
                    return;
                }
                DoInjectOrUpdate(_pendingText, _pendingVisible);
            }
        }

        private static void DoInjectOrUpdate(string text, bool visible)
        {
            try
            {
                // Always remove previous widget and re-inject fresh.
                // The page content is rebuilt on navigation (back/forward),
                // so we can't reuse an old widget reference.
                RemoveOldWidget();

                if (visible)
                    TryInjectIntoEncyclopedia();

                if (_textWidget != null)
                {
                    _textWidget.Text = text ?? "";
                    _textWidget.IsVisible = visible;
                }

                MCMSettings.DebugLog("TimestampOverlay: text='" + text + "', visible=" + visible
                    + " widgetReady=" + _widgetReady);

                // If visible but injection failed (widget tree not fully built yet
                // after back/return navigation), schedule a retry after a short delay.
                // The retry sets a flag that is processed on the main game thread
                // via TickMainThread(), because Gauntlet widgets must not be
                // modified from background timer threads.
                if (visible && !_widgetReady && _retryCount < MaxRetries)
                {
                    _retryCount++;
                    MCMSettings.DebugLog("TimestampOverlay: scheduling retry #" + _retryCount
                        + " in " + RetryDelayMs + "ms (deferred to main thread)");
                    _retryTimer = new System.Threading.Timer(_ =>
                    {
                        // Signal the main thread to perform the retry —
                        // DO NOT call DoInjectOrUpdate here (wrong thread)
                        _retryPending = true;
                    }, null, RetryDelayMs, System.Threading.Timeout.Infinite);
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("TimestampOverlay error: " + ex.ToString());
            }
        }

        private static void RemoveOldWidget()
        {
            if (_textWidget != null)
            {
                try
                {
                    if (_textWidget.ParentWidget != null)
                        _textWidget.ParentWidget.RemoveChild(_textWidget);
                }
                catch (Exception ex) { MCMSettings.DebugLog("[TimestampWidgetInjector]: " + ex.ToString()); }
            }
            _textWidget = null;
            _widgetReady = false;
        }

        private static void DisposeRetryTimer()
        {
            try { _retryTimer?.Dispose(); }
            catch (Exception ex) { MCMSettings.DebugLog("[TimestampWidgetInjector]: " + ex.ToString()); }
            _retryTimer = null;
        }

        // ─────────── Widget Injection ───────────

        private static void TryInjectIntoEncyclopedia()
        {
            try
            {
                var topScreen = ScreenManager.TopScreen;
                if (topScreen == null) return;

                GauntletLayer encLayer = FindEncyclopediaLayer(topScreen);
                if (encLayer == null)
                {
                    MCMSettings.DebugLog("TimestampOverlay: no encyclopedia layer found");
                    return;
                }

                Widget layerRoot = GetLayerRootWidget(encLayer);
                if (layerRoot == null)
                {
                    MCMSettings.DebugLog("TimestampOverlay: layer root is null");
                    return;
                }

                Widget encWindow = FindLargestChild(layerRoot);
                if (encWindow == null)
                {
                    MCMSettings.DebugLog("TimestampOverlay: no encyclopedia window in layer root");
                    return;
                }

                UIContext uiContext = layerRoot.EventManager?.Context as UIContext;
                if (uiContext == null)
                {
                    MCMSettings.DebugLog("TimestampOverlay: cannot get UIContext");
                    return;
                }

                // Check if our widget already exists in the tree (prevent duplicates after re-save)
                Widget existingTimestamp = FindWidgetById(encWindow, "EditableEncyclopediaTimestamp");
                if (existingTimestamp != null)
                {
                    _textWidget = existingTimestamp as TextWidget;
                    _widgetReady = _textWidget != null;
                    MCMSettings.DebugLog("TimestampOverlay: reusing existing widget");
                    return;
                }

                // Try anchor strategies in order of specificity:
                // 1. "seen" — hero pages: "Never seen before" / "Last seen at …"
                // 2. Custom description text — the known description content (most reliable on re-save)
                // 3. Localized edited prefix — all edited pages (e.g. "[Edited]", "[Bearbeitet]", "[已编辑]")
                // 4. Localized hint substring — all pages with edit hint (e.g. "Ctrl+E", "Strg+E")
                var anchors = new System.Collections.Generic.List<string> { "seen" };
                try
                {
                    // Prioritize description text over [Edited] prefix — after a save+refresh,
                    // the "seen" text is gone, and anchoring to [Edited] puts us at idx=0
                    // which merges the timestamp visually with the [Edited] prefix
                    string desc = EncyclopediaPageTracker.CurrentDescription;
                    if (!string.IsNullOrEmpty(desc) && desc.Length >= MinDescLength)
                    {
                        string searchPart = desc.Length > DescSearchLength ? desc.Substring(0, DescSearchLength) : desc;
                        anchors.Add(searchPart);
                    }

                    string pfx = Localization.L("edited_prefix")?.Trim();
                    if (!string.IsNullOrEmpty(pfx))
                        anchors.Add(pfx);

                    // Extract keyboard shortcut from hint (e.g. "Ctrl+E" or "Strg+E")
                    string hint = Localization.L("hint_edit")?.Trim();
                    if (!string.IsNullOrEmpty(hint))
                    {
                        int bracket = hint.IndexOf('[');
                        int space = bracket >= 0 ? hint.IndexOf(' ', bracket + 1) : -1;
                        if (bracket >= 0 && space > bracket)
                            anchors.Add(hint.Substring(bracket + 1, space - bracket - 1));
                        else
                            anchors.Add(hint);
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("[TimestampWidgetInjector]: " + ex.ToString()); }

                foreach (string anchor in anchors)
                {
                    if (TryInjectAboveAnchor(encWindow, uiContext, anchor))
                        return;
                }

                MCMSettings.DebugLog("TimestampOverlay: no anchor found for any strategy");
                // Clear cached layer so next retry does a fresh search —
                // the real encyclopedia layer may not have been ready yet.
                EncyclopediaPageTracker.EncyclopediaLayerRef = null;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("TimestampOverlay: injection error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Finds a widget whose Text contains <paramref name="searchText"/> and
        /// inserts the timestamp TextWidget as a sibling directly before it.
        /// </summary>
        private static bool TryInjectAboveAnchor(Widget encWindow, UIContext uiContext, string searchText)
        {
            Widget anchor = FindWidgetByTextContent(encWindow, searchText);
            if (anchor == null || anchor.ParentWidget == null)
                return false;

            Widget parent = anchor.ParentWidget;
            int idx = FindChildIndex(parent, anchor);
            string anchorText = TryGetText(anchor) ?? "(?)";
            // Truncate long text for logging
            if (anchorText.Length > LogTextTruncateLength)
                anchorText = anchorText.Substring(0, LogTextTruncateLength) + "...";
            MCMSettings.DebugLog("TimestampOverlay: anchor='" + searchText + "' type="
                + anchor.GetType().Name + " text='" + anchorText
                + "' parent=" + parent.GetType().Name
                + " children=" + parent.ChildCount + " idx=" + idx);

            _textWidget = new TextWidget(uiContext);
            _textWidget.Id = "EditableEncyclopediaTimestamp";
            // Mirror the anchor widget's layout so our text sits in the same column
            _textWidget.WidthSizePolicy = anchor.WidthSizePolicy;
            _textWidget.HeightSizePolicy = anchor.HeightSizePolicy;
            _textWidget.HorizontalAlignment = anchor.HorizontalAlignment;
            _textWidget.VerticalAlignment = anchor.VerticalAlignment;
            _textWidget.MarginLeft = anchor.MarginLeft;
            _textWidget.MarginRight = anchor.MarginRight;
            _textWidget.MarginTop = anchor.MarginTop;
            _textWidget.MarginBottom = TimestampMarginBottom;
            _textWidget.DoNotAcceptEvents = true;

            // Copy brush from anchor (works for TextWidget, RichTextWidget, etc.)
            if (anchor is BrushWidget bw && bw.ReadOnlyBrush != null)
                _textWidget.Brush = bw.ReadOnlyBrush;

            // Insert before the anchor so we appear above it
            if (idx >= 0)
            {
                try
                {
                    parent.AddChildAtIndex(_textWidget, idx);
                    MCMSettings.DebugLog("TimestampOverlay: inserted at index " + idx);
                }
                catch (Exception ex)
                {
                    MCMSettings.DebugLog("TimestampOverlay: AddChildAtIndex failed ("
                        + ex.Message + "), appending");
                    parent.AddChild(_textWidget);
                }
            }
            else
            {
                parent.AddChild(_textWidget);
            }

            _widgetReady = true;
            return true;
        }

        // ─────────── Layer Detection ───────────

        private static GauntletLayer FindEncyclopediaLayer(ScreenBase topScreen)
        {
            // Use the layer captured by EncyclopediaLayerCapturePatch (most reliable)
            var capturedLayer = EncyclopediaPageTracker.EncyclopediaLayerRef;
            if (capturedLayer is GauntletLayer captured)
            {
                foreach (var layer in topScreen.Layers)
                {
                    if (object.ReferenceEquals(layer, captured))
                    {
                        MCMSettings.DebugLog("TimestampOverlay: using captured layer reference");
                        return captured;
                    }
                }
                EncyclopediaPageTracker.EncyclopediaLayerRef = null;
            }

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
                            MCMSettings.DebugLog("TimestampOverlay: got layer from manager");
                            return gl;
                        }
                        MCMSettings.DebugLog("TimestampOverlay: manager Layer="
                            + (layerVal?.GetType().Name ?? "null"));
                        break;
                    }
                    encType = encType.BaseType;
                }
            }

            MCMSettings.DebugLog("TimestampOverlay: searching all layers");
            GauntletLayer bestLayer = null;
            int bestDesc = 0;
            int bestIdx = -1;

            var layers = topScreen.Layers;
            for (int i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                if (!(layer is GauntletLayer gl)) continue;

                Widget root = GetLayerRootWidget(gl);
                if (root == null) continue;

                int desc = CountDescendants(root);

                if (desc < MinLayerDescendants || desc > MaxLayerDescendants) continue;
                // Only consider layers with EncyclopediaDividerButtonWidget — prevents
                // picking Clan/Party/Inventory screen panels instead of the encyclopedia.
                if (!LoreSectionInjector.ContainsWidgetType(root, "EncyclopediaDividerButtonWidget")) continue;

                if (desc > bestDesc)
                {
                    bestDesc = desc;
                    bestLayer = gl;
                    bestIdx = i;
                }
            }

            if (bestLayer != null)
            {
                MCMSettings.DebugLog("TimestampOverlay: picked layer[" + bestIdx
                    + "] (" + bestDesc + " descendants)");
                EncyclopediaPageTracker.EncyclopediaLayerRef = bestLayer;
            }
            else
                MCMSettings.DebugLog("TimestampOverlay: no layer matched range " + MinLayerDescendants + "-" + MaxLayerDescendants);

            return bestLayer;
        }

        // ─────────── Helpers ───────────

        private static Widget FindWidgetById(Widget parent, string id, int depth = 0)
        {
            if (depth > 20) return null;
            if (parent.Id == id) return parent;
            for (int i = 0; i < parent.ChildCount; i++)
            {
                Widget found = FindWidgetById(parent.GetChild(i), id, depth + 1);
                if (found != null) return found;
            }
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
            if (widget == null || depth > MaxWidgetTreeDepth) return 0;
            int count = 0;
            for (int i = 0; i < widget.ChildCount; i++)
                count += 1 + CountDescendants(widget.GetChild(i), depth + 1);
            return count;
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
            catch (Exception ex)
            {
                MCMSettings.DebugLog("TimestampOverlay: GetLayerRootWidget error: " + ex.ToString());
            }

            return null;
        }

        /// <summary>
        /// Searches the widget tree for any widget with a Text property containing
        /// the given substring. Handles TextWidget, RichTextWidget, and any other
        /// text-bearing widget type via reflection.
        /// </summary>
        private static Widget FindWidgetByTextContent(Widget parent, string substring, int depth = 0)
        {
            if (parent == null || depth > MaxWidgetTreeDepth) return null;

            string text = TryGetText(parent);
            if (!string.IsNullOrEmpty(text)
                && text.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0
                && parent.Id != "EditableEncyclopediaTimestamp")
                return parent;

            for (int i = 0; i < parent.ChildCount; i++)
            {
                var result = FindWidgetByTextContent(parent.GetChild(i), substring, depth + 1);
                if (result != null)
                    return result;
            }

            return null;
        }

        /// <summary>
        /// Gets the Text property from any widget type. Returns the text directly
        /// for TextWidget, or uses reflection for RichTextWidget and other types.
        /// </summary>
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
                MCMSettings.DebugLog("[TimestampWidgetInjector]: " + ex.ToString());
                return null;
            }
        }
    }
}
