using System;
using System.Reflection;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.ScreenSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Wraps the hero description RichTextWidget (Id="HeroInformationText") in a
    /// fixed-height ScrollablePanel so long descriptions don't overflow the page.
    /// Scheduled from HeroPageRefreshPatch.Postfix, executed on main thread tick.
    /// </summary>
    internal static class HeroDescriptionScrollPatch
    {
        private static readonly BindingFlags AllFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static volatile bool _pending;
        private static int _retryCount;
        private const int MaxRetries = 10;
        private const int RetryDelayMs = 150;
        private static System.Threading.Timer _retryTimer;
        private const string WrapperId = "EE_DescScrollWrapper";
        private const float MaxHeight = 250f;

        public static void Schedule()
        {
            DisposeTimer();
            _retryCount = 0;
            _pending = true;
            MCMSettings.DebugLog("DescScroll: Schedule() called");
        }

        public static void TickMainThread()
        {
            if (!_pending) return;
            _pending = false;
            DoWrap();
        }

        private static void DisposeTimer()
        {
            var t = _retryTimer;
            _retryTimer = null;
            if (t != null)
                try { t.Dispose(); } catch { }
        }

        /// <summary>
        /// Minimum text length (characters) to trigger scroll wrapping.
        /// Short descriptions don't need a fixed-height container.
        /// </summary>
        private const int MinTextLengthForScroll = 300;

        private static void DoWrap()
        {
            try
            {
                // Find the encyclopedia layer
                var topScreen = ScreenManager.TopScreen;
                if (topScreen == null) return;

                Widget descWidget = null;
                UIContext uiContext = null;

                foreach (var layer in topScreen.Layers)
                {
                    if (!(layer is GauntletLayer gl)) continue;
                    Widget root = GetLayerRootWidget(gl);
                    if (root == null) continue;

                    descWidget = FindDescriptionWidget(root);
                    if (descWidget != null)
                    {
                        uiContext = root.EventManager?.Context as UIContext;
                        break;
                    }
                }

                if (descWidget == null || uiContext == null)
                {
                    if (_retryCount < MaxRetries)
                    {
                        _retryCount++;
                        // Dispose previous timer first to prevent leaks on rapid retry
                        // (Theme 2: round 3 finding FMW-6).
                        _retryTimer?.Dispose();
                        _retryTimer = new System.Threading.Timer(_ => { _pending = true; },
                            null, RetryDelayMs, System.Threading.Timeout.Infinite);
                    }
                    return;
                }

                var parent = descWidget.ParentWidget;
                if (parent == null) return;

                // Already wrapped?
                if (parent.Id == WrapperId)
                {
                    MCMSettings.DebugLog("DescScroll: already wrapped, skipping");
                    return;
                }

                // Check text length — only wrap if text is long enough to overflow
                string descText = "";
                try
                {
                    var textProp = descWidget.GetType().GetProperty("Text", AllFlags);
                    if (textProp != null)
                        descText = textProp.GetValue(descWidget) as string ?? "";
                }
                catch { }

                if (descText.Length < MinTextLengthForScroll)
                {
                    MCMSettings.DebugLog("DescScroll: text too short (" + descText.Length
                        + " chars), skipping wrap");
                    return;
                }

                // Find the index of the description widget in its parent
                int idx = -1;
                for (int i = 0; i < parent.ChildCount; i++)
                {
                    if (parent.GetChild(i) == descWidget)
                    { idx = i; break; }
                }
                if (idx < 0) return;

                MCMSettings.DebugLog("DescScroll: found HeroInformationText at index " + idx
                    + " parent=" + parent.GetType().Name + " children=" + parent.ChildCount
                    + " textLen=" + descText.Length);

                // Remove the widget from its parent
                parent.RemoveChild(descWidget);

                // Set the RichTextWidget to CoverChildren so it grows with text
                descWidget.HeightSizePolicy = SizePolicy.CoverChildren;

                // Create the fixed-height wrapper
                var wrapper = new Widget(uiContext);
                wrapper.Id = WrapperId;
                wrapper.WidthSizePolicy = SizePolicy.StretchToParent;
                wrapper.HeightSizePolicy = SizePolicy.Fixed;
                wrapper.SuggestedHeight = MaxHeight;
                wrapper.ClipContents = true;

                // Try creating a ScrollablePanel
                bool scrollOk = false;
                try
                {
                    Type scrollType = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        scrollType = asm.GetType("TaleWorlds.GauntletUI.BaseTypes.ScrollablePanel");
                        if (scrollType != null) break;
                    }

                    if (scrollType != null)
                    {
                        var scrollPanel = (Widget)Activator.CreateInstance(scrollType, new object[] { uiContext });
                        scrollPanel.WidthSizePolicy = SizePolicy.StretchToParent;
                        scrollPanel.HeightSizePolicy = SizePolicy.StretchToParent;
                        scrollPanel.ClipContents = true;

                        // ClipRect: fixed-size viewport that clips content
                        var clipRect = new Widget(uiContext);
                        clipRect.Id = "EE_DescClipRect";
                        clipRect.WidthSizePolicy = SizePolicy.StretchToParent;
                        clipRect.HeightSizePolicy = SizePolicy.StretchToParent;
                        clipRect.ClipContents = true;

                        // InnerPanel: grows with content, separate from ClipRect
                        // This is the key fix — InnerPanel must be a DIFFERENT widget
                        // from ClipRect so the ScrollablePanel can scroll it within
                        // the fixed ClipRect viewport.
                        var innerPanel = new Widget(uiContext);
                        innerPanel.Id = "EE_DescInnerPanel";
                        innerPanel.WidthSizePolicy = SizePolicy.StretchToParent;
                        innerPanel.HeightSizePolicy = SizePolicy.CoverChildren;

                        innerPanel.AddChild(descWidget);
                        clipRect.AddChild(innerPanel);
                        scrollPanel.AddChild(clipRect);

                        // Set ScrollablePanel properties
                        var clipRectProp = scrollType.GetProperty("ClipRect", AllFlags);
                        var innerPanelProp = scrollType.GetProperty("InnerPanel", AllFlags);
                        var autoHideProp = scrollType.GetProperty("AutoHideScrollBars", AllFlags);
                        var vertScrollProp = scrollType.GetProperty("VerticalScrollbar", AllFlags);

                        if (clipRectProp != null && clipRectProp.CanWrite)
                            clipRectProp.SetValue(scrollPanel, clipRect);
                        if (innerPanelProp != null && innerPanelProp.CanWrite)
                            innerPanelProp.SetValue(scrollPanel, innerPanel);
                        if (autoHideProp != null && autoHideProp.CanWrite)
                            autoHideProp.SetValue(scrollPanel, true);

                        wrapper.AddChild(scrollPanel);
                        scrollOk = true;
                        MCMSettings.DebugLog("DescScroll: wrapped in ScrollablePanel (ClipRect != InnerPanel)");
                    }
                }
                catch (Exception ex)
                {
                    MCMSettings.DebugLog("DescScroll: ScrollablePanel failed: " + ex.ToString());
                }

                if (!scrollOk)
                {
                    // Fallback: just put the widget in the fixed-height wrapper
                    wrapper.AddChild(descWidget);
                    MCMSettings.DebugLog("DescScroll: using simple clip wrapper");
                }

                // Insert wrapper where the original widget was
                parent.AddChildAtIndex(wrapper, idx);
                MCMSettings.DebugLog("DescScroll: inserted wrapper at index " + idx);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("DescScroll: error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Finds the hero description RichTextWidget by matching its type name and brush.
        /// The widget has no reliable Id at runtime (Bannerlord 1.3.13 strips prefab IDs).
        /// </summary>
        private static Widget FindDescriptionWidget(Widget parent, int depth = 0)
        {
            if (parent == null || depth > 25) return null;

            string typeName = parent.GetType().Name;

            if (typeName == "RichTextWidget")
            {
                try
                {
                    if (parent is BrushWidget bw && bw.ReadOnlyBrush != null)
                    {
                        string brushName = bw.ReadOnlyBrush.Name;
                        if (brushName != null && brushName.IndexOf("Encyclopedia.SubPage.Info.Text", StringComparison.OrdinalIgnoreCase) >= 0)
                            return parent;
                    }
                }
                catch { }
            }

            for (int i = 0; i < parent.ChildCount; i++)
            {
                var result = FindDescriptionWidget(parent.GetChild(i), depth + 1);
                if (result != null) return result;
            }
            return null;
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
                MCMSettings.DebugLog("DescScroll: GetLayerRootWidget error: " + ex.ToString());
            }
            return null;
        }

        // ── Hide/show wrapper during edit popups ──
        // When any edit popup is open, the description RichTextWidget still renders
        // behind the popup (Gauntlet has no z-order-based clipping). We hide the
        // wrapper to prevent the description text from showing through the popup.

        /// <summary>
        /// Hides the description scroll wrapper (if present) to prevent it from
        /// rendering behind edit popups.
        /// </summary>
        internal static void HideWrapper()
        {
            try
            {
                var wrapper = FindWrapperWidget();
                if (wrapper != null && wrapper.IsVisible)
                {
                    wrapper.IsVisible = false;
                    MCMSettings.DebugLog("DescScroll: hid wrapper for popup");
                }
            }
            catch { }
        }

        /// <summary>
        /// Shows the description scroll wrapper again after an edit popup closes.
        /// </summary>
        internal static void ShowWrapper()
        {
            try
            {
                var wrapper = FindWrapperWidget();
                if (wrapper != null && !wrapper.IsVisible)
                {
                    wrapper.IsVisible = true;
                    MCMSettings.DebugLog("DescScroll: restored wrapper after popup");
                }
            }
            catch { }
        }

        /// <summary>
        /// Finds the EE_DescScrollWrapper widget in the current encyclopedia screen.
        /// </summary>
        private static Widget FindWrapperWidget()
        {
            var topScreen = ScreenManager.TopScreen;
            if (topScreen == null) return null;
            foreach (var layer in topScreen.Layers)
            {
                if (!(layer is GauntletLayer gl)) continue;
                Widget root = GetLayerRootWidget(gl);
                if (root == null) continue;
                var found = FindWidgetById(root, WrapperId, 0);
                if (found != null) return found;
            }
            return null;
        }

        private static Widget FindWidgetById(Widget parent, string id, int depth)
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

        // ── Scissor clipping for RichTextWidget.OnRender ──
        // RichTextWidget rendering bypasses ClipContents, so we apply a GPU scissor
        // rect matching the EE_DescScrollWrapper bounds.

        private static bool _clipPatchApplied;
        private static PropertyInfo _twoDimCtxProp;
        private static MethodInfo _setScissorMethod;
        private static MethodInfo _resetScissorMethod;
        private static ConstructorInfo _scissorCtor;
        private static Type _scissorTestInfoType;
        // Volatile so the prefix's check-then-set on _scissorMethodSearched is visible
        // across threads (Theme 1: round 3 finding FMW-10).
        private static volatile bool _scissorMethodSearched;
        private static bool _scissorReady;
        [ThreadStatic] private static bool _scissorActive;
        private static int _scissorLogCount;
        private static int _previewScissorLogCount;

        /// <summary>
        /// Patches RichTextWidget.OnRender with a prefix+finalizer to apply scissor
        /// clipping when the widget is inside our scroll wrapper.
        /// Called from SubModuleClassEntry.OnGameStart.
        /// </summary>
        internal static void TryApplyDescriptionClipPatch(HarmonyLib.Harmony harmony)
        {
            if (_clipPatchApplied) return;
            try
            {
                // Find RichTextWidget type
                Type richTextType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    richTextType = asm.GetType("TaleWorlds.GauntletUI.BaseTypes.RichTextWidget");
                    if (richTextType != null) break;
                }
                if (richTextType == null)
                {
                    MCMSettings.DebugLog("DescScrollClip: RichTextWidget type not found");
                    return;
                }

                var target = richTextType.GetMethod("OnRender", AllFlags);
                if (target == null)
                {
                    MCMSettings.DebugLog("DescScrollClip: OnRender not found on RichTextWidget");
                    return;
                }

                var prefix = typeof(HeroDescriptionScrollPatch).GetMethod(
                    nameof(DescriptionRenderPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                var finalizer = typeof(HeroDescriptionScrollPatch).GetMethod(
                    nameof(DescriptionRenderFinalizer),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(target,
                    prefix: new HarmonyLib.HarmonyMethod(prefix),
                    finalizer: new HarmonyLib.HarmonyMethod(finalizer));
                _clipPatchApplied = true;
                MCMSettings.DebugLog("DescScrollClip: patched RichTextWidget.OnRender for scissor clipping");
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("DescScrollClip: patch failed: " + ex.ToString());
            }
        }

        /// <summary>
        /// Walks up the parent chain (max 5 levels) looking for the scroll wrapper widget.
        /// Returns the wrapper if found, null otherwise.
        /// </summary>
        /// <summary>
        /// Id used by the TextInquiry preview clip wrapper.
        /// </summary>
        private const string PreviewClipId = "__EE_PreviewClip";

        private static Widget FindScrollWrapper(Widget widget)
        {
            // Check the widget itself first (direct constrain mode — no wrapper parent)
            if (widget != null && widget.Id == PreviewClipId)
                return widget;

            var w = widget?.ParentWidget;
            for (int i = 0; i < 8 && w != null; i++)
            {
                if (w.Id == WrapperId || w.Id == PreviewClipId)
                    return w;
                w = w.ParentWidget;
            }
            return null;
        }

        /// <summary>
        /// Harmony prefix: Before RichTextWidget.OnRender, if the widget is inside our
        /// EE_DescScrollWrapper, push a scissor rect to clip text rendering.
        /// </summary>
        private static void DescriptionRenderPrefix(Widget __instance)
        {
            try
            {
                var wrapper = FindScrollWrapper(__instance);
                if (wrapper == null) return;

                // One-time setup: discover ScissorTestInfo type and constructors
                if (!_scissorMethodSearched)
                {
                    _scissorMethodSearched = true;

                    var uiCtx = __instance.Context;
                    if (uiCtx == null) return;

                    _twoDimCtxProp = uiCtx.GetType().GetProperty("TwoDimensionContext", AllFlags);
                    if (_twoDimCtxProp == null) return;

                    var twoDimCtx = _twoDimCtxProp.GetValue(uiCtx);
                    if (twoDimCtx == null) return;

                    _setScissorMethod = twoDimCtx.GetType().GetMethod("SetScissor", AllFlags);
                    _resetScissorMethod = twoDimCtx.GetType().GetMethod("ResetScissor", AllFlags);

                    if (_setScissorMethod == null || _resetScissorMethod == null)
                    {
                        MCMSettings.DebugLog("DescScrollClip: SetScissor/ResetScissor not found");
                        return;
                    }

                    var setParams = _setScissorMethod.GetParameters();
                    if (setParams.Length != 1)
                    {
                        MCMSettings.DebugLog("DescScrollClip: SetScissor has " + setParams.Length + " params, expected 1");
                        return;
                    }

                    _scissorTestInfoType = setParams[0].ParameterType;

                    foreach (var ctor in _scissorTestInfoType.GetConstructors(
                        AllFlags | BindingFlags.Static))
                    {
                        var ctorParams = ctor.GetParameters();
                        if (ctorParams.Length >= 4)
                            _scissorCtor = ctor;
                    }

                    _scissorReady = _scissorCtor != null || _scissorTestInfoType.IsValueType;
                    MCMSettings.DebugLog("DescScrollClip: scissorReady=" + _scissorReady
                        + " hasCtor=" + (_scissorCtor != null)
                        + " isValueType=" + _scissorTestInfoType.IsValueType);
                }

                if (!_scissorReady || _setScissorMethod == null) return;

                var ctx = __instance.Context;
                if (ctx == null) return;
                var twoDimContext = _twoDimCtxProp.GetValue(ctx);
                if (twoDimContext == null) return;

                // Get wrapper's global position and size for the scissor rect
                var wrapperType = wrapper.GetType();
                object globalPos = wrapperType.GetProperty("GlobalPosition", AllFlags).GetValue(wrapper);
                object size = wrapperType.GetProperty("Size", AllFlags).GetValue(wrapper);
                var vecType = globalPos.GetType();
                float x = (float)vecType.GetField("X").GetValue(globalPos);
                float y = (float)vecType.GetField("Y").GetValue(globalPos);
                float w = (float)vecType.GetField("X").GetValue(size);
                float h = (float)vecType.GetField("Y").GetValue(size);

                // Create ScissorTestInfo and call SetScissor
                object scissorInfo = null;
                if (_scissorCtor != null)
                {
                    var ctorParams = _scissorCtor.GetParameters();
                    if (ctorParams.Length == 4)
                        scissorInfo = _scissorCtor.Invoke(new object[] { x, y, x + w, y + h });
                    else if (ctorParams.Length == 5)
                        scissorInfo = _scissorCtor.Invoke(new object[] { true, x, y, x + w, y + h });
                }
                else if (_scissorTestInfoType.IsValueType)
                {
                    scissorInfo = Activator.CreateInstance(_scissorTestInfoType);
                    foreach (var f in _scissorTestInfoType.GetFields(AllFlags))
                    {
                        string fn = f.Name.ToLower();
                        if (fn.Contains("x") && !fn.Contains("y") && !fn.Contains("w") && !fn.Contains("h")
                            && f.FieldType == typeof(float))
                            f.SetValue(scissorInfo, x);
                        else if (fn.Contains("y") && f.FieldType == typeof(float))
                            f.SetValue(scissorInfo, y);
                        else if ((fn.Contains("w") || fn.Contains("right") || fn.Contains("x2")) && f.FieldType == typeof(float))
                            f.SetValue(scissorInfo, x + w);
                        else if ((fn.Contains("h") || fn.Contains("bottom") || fn.Contains("y2")) && f.FieldType == typeof(float))
                            f.SetValue(scissorInfo, y + h);
                        else if (fn.Contains("enabled") && f.FieldType == typeof(bool))
                            f.SetValue(scissorInfo, true);
                    }
                }

                if (scissorInfo != null)
                {
                    _setScissorMethod.Invoke(twoDimContext, new object[] { scissorInfo });
                    _scissorActive = true;

                    // Use separate log counters for encyclopedia desc vs preview
                    bool isPreview = wrapper.Id == PreviewClipId;
                    if (isPreview)
                    {
                        if (_previewScissorLogCount < 5)
                        {
                            _previewScissorLogCount++;
                            MCMSettings.DebugLog("PreviewScissor: SetScissor — x=" + x
                                + " y=" + y + " x2=" + (x + w) + " y2=" + (y + h)
                                + " wrapperW=" + w + " wrapperH=" + h
                                + " widgetType=" + __instance.GetType().Name);
                        }
                    }
                    else if (_scissorLogCount < 3)
                    {
                        _scissorLogCount++;
                        MCMSettings.DebugLog("DescScrollClip: SetScissor — x=" + x
                            + " y=" + y + " x2=" + (x + w) + " y2=" + (y + h));
                    }
                }
            }
            catch (Exception ex)
            {
                if (_scissorLogCount < 3)
                {
                    _scissorLogCount++;
                    MCMSettings.DebugLog("DescScrollClip: prefix error: " + ex.ToString());
                }
            }
        }

        /// <summary>
        /// Harmony finalizer: After RichTextWidget.OnRender, reset scissor state.
        /// </summary>
        private static void DescriptionRenderFinalizer(Widget __instance)
        {
            try
            {
                if (!_scissorActive) return;
                _scissorActive = false;

                if (_resetScissorMethod == null || _twoDimCtxProp == null) return;

                var ctx = __instance.Context;
                if (ctx == null) return;
                var twoDimContext = _twoDimCtxProp.GetValue(ctx);
                if (twoDimContext == null) return;

                _resetScissorMethod.Invoke(twoDimContext, null);
            }
            catch { }
        }
    }
}
