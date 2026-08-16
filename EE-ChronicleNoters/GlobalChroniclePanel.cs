using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Global Chronicle Panel — a campaign map overlay that displays all world history events.
    /// Shows a button on the bottom-right of the map HUD. When clicked, a panel slides up
    /// showing aggregated chronicle notes from all kingdoms, clans, and settlements.
    /// </summary>
    internal static class GlobalChroniclePanel
    {
        private const BindingFlags AllFlags =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private const int LeftMouseButtonKey = 224;
        private const int EscapeKey = 1;
        private const float FallbackButtonWidth = 320f;
        private const float FallbackButtonHeight = 60f;
        private const float FallbackButtonMarginBottom = 6f;
        private const float FallbackButtonMarginRight = 120f;
        private const float MapBarButtonWidth = 76f;
        private const float MapBarButtonHeight = 52f;
        private const int MapBarIconFontSize = 26;

        private static readonly object _stateLock = new object();

        // ─── State ───
        private static bool _buttonInjected;
        private static int _buttonInjectAttempts;
        private const int MaxInjectAttempts = 10;
        private static Widget _mapButton;
        private static TextWidget _mapButtonLabel;
        private static bool _btnHovered;
        private static bool _btnPressed;
        private static MethodInfo _showHintMethod;
        private static MethodInfo _hideHintMethod;
        private static GauntletLayer _buttonLayer;
        private static ScreenBase _ownerScreen; // the MapScreen that owns our layers
        private static int _layerCountOnOpen;  // layer count when panel opened — used to detect other panels
        private static string _lastScreenType;

        // ─── Deferred operations ───
        private static volatile bool _needsToggle;
        private static volatile bool _needsForceClose;

        /// <summary>
        /// Called every frame from SubModuleClassEntry.OnApplicationTick.
        /// Handles button injection on campaign map and panel animation.
        /// </summary>
        public static void TickMainThread(float dt)
        {
            try
            {
                var topScreen = ScreenManager.TopScreen;
                if (topScreen == null) return;

                string screenType = topScreen.GetType().Name;

                // Detect screen changes — clean up when leaving campaign map
                if (_lastScreenType != null && _lastScreenType != screenType)
                {
                    CleanUp();
                }
                _lastScreenType = screenType;

                // Only operate on campaign map screens
                if (!screenType.Contains("MapScreen")) return;

                // Check MCM master toggle for the whole chronicle feature
                try
                {
                    var settings = MCMSettings.Instance;
                    if (settings != null && !settings.EnableGlobalChronicle) return;
                }
                catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: MCM settings check failed: " + ex.ToString()); }

                // Inject button if not present (give up after MaxInjectAttempts to avoid spam)
                if (!_buttonInjected && _buttonInjectAttempts < MaxInjectAttempts)
                {
                    _buttonInjectAttempts++;
                    TryInjectButton(topScreen);
                }

                // Tick-based click detection for the chronicle button. The button layer has
                // InputRestrictions disabled (to not block map input), so widget click events
                // don't fire — we detect clicks manually instead.
                if (_buttonInjected && _mapButton != null && !GlobalChroniclePanelV2Layer.IsOpen)
                {
                    CheckButtonClick();
                    UpdateButtonHoverPress();
                }

                // Auto-close if another panel was opened on top (Inventory, Clan, etc.). These
                // panels add layers to the same MapScreen without changing screen type, so the
                // screen-change CleanUp never fires. Detect via layer-count increase.
                if (GlobalChroniclePanelV2Layer.IsOpen && _ownerScreen != null && _layerCountOnOpen > 0)
                {
                    int currentLayerCount = _ownerScreen.Layers.Count;
                    if (currentLayerCount > _layerCountOnOpen)
                    {
                        MCMSettings.DebugLog("GlobalChronicle: another panel opened (layers "
                            + _layerCountOnOpen + " → " + currentLayerCount + "), force-closing");
                        ForceClosePanel();
                    }
                }

                // Close panel on Escape key
                if (GlobalChroniclePanelV2Layer.IsOpen)
                {
                    CheckPanelDismiss();
                }

                lock (_stateLock)
                {
                    if (_needsForceClose)
                    {
                        _needsForceClose = false;
                        _needsToggle = false;
                        if (GlobalChroniclePanelV2Layer.IsOpen)
                            ForceClosePanelUnlocked();
                    }

                    if (_needsToggle)
                    {
                        _needsToggle = false;
                        TogglePanelUnlocked(topScreen);
                    }
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("GlobalChronicle: TickMainThread error: " + ex.ToString());
            }
        }

        // ─────────── Panel Dismiss (Escape / Click Outside) ───────────

        private static MethodInfo _cachedIsKeyReleasedMethod;
        private static bool _cachedIsKeyReleasedUseEnum;
        private static bool _dismissMethodsResolved;

        private static void CheckPanelDismiss()
        {
            try
            {
                Type inputType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    inputType = asm.GetType("TaleWorlds.InputSystem.Input");
                    if (inputType != null) break;
                }
                if (inputType == null) return;

                // Resolve IsKeyReleased once and cache
                if (!_dismissMethodsResolved)
                {
                    _dismissMethodsResolved = true;

                    _cachedIsKeyReleasedMethod = inputType.GetMethod("IsKeyReleased",
                        BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(int) }, null);
                    if (_cachedIsKeyReleasedMethod != null)
                    {
                        _cachedIsKeyReleasedUseEnum = false;
                        MCMSettings.DebugLog("GlobalChronicle: dismiss using IsKeyReleased(int)");
                    }
                    else
                    {
                        Type inputKeyType = null;
                        foreach (var asm2 in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            inputKeyType = asm2.GetType("TaleWorlds.InputSystem.InputKey");
                            if (inputKeyType != null) break;
                        }
                        if (inputKeyType != null)
                        {
                            _cachedIsKeyReleasedMethod = inputType.GetMethod("IsKeyReleased",
                                BindingFlags.Static | BindingFlags.Public, null, new[] { inputKeyType }, null);
                            _cachedIsKeyReleasedUseEnum = true;
                            MCMSettings.DebugLog("GlobalChronicle: dismiss using IsKeyReleased(InputKey)");
                        }
                    }
                    if (_cachedIsKeyReleasedMethod == null)
                        MCMSettings.DebugLog("GlobalChronicle: WARNING — no IsKeyReleased method found, Escape dismiss disabled");
                }

                if (_cachedIsKeyReleasedMethod != null)
                {
                    object arg = _cachedIsKeyReleasedUseEnum
                        ? Enum.ToObject(_cachedIsKeyReleasedMethod.GetParameters()[0].ParameterType, EscapeKey)
                        : (object)EscapeKey;
                    bool escReleased = (bool)_cachedIsKeyReleasedMethod.Invoke(null, new[] { arg });
                    if (escReleased)
                    {
                        _needsToggle = true;
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: CheckPanelDismiss failed: " + ex.ToString()); }
        }

        // ─────────── Click Detection ───────────

        private static bool _wasMouseDown;

        private static void CheckButtonClick()
        {
            try
            {
                // Get mouse state via TaleWorlds.InputSystem.Input (reflection)
                bool mouseDown = false;
                float mouseX = 0f, mouseY = 0f;

                Type inputType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    inputType = asm.GetType("TaleWorlds.InputSystem.Input");
                    if (inputType != null) break;
                }
                if (inputType == null) return;

                // Check left mouse button: Input.IsKeyDown(InputKey.LeftMouseButton)
                var isKeyDown = inputType.GetMethod("IsKeyDown",
                    BindingFlags.Static | BindingFlags.Public,
                    null, new[] { typeof(int) }, null);
                // InputKey.LeftMouseButton = 224 in Bannerlord
                if (isKeyDown != null)
                    mouseDown = (bool)isKeyDown.Invoke(null, new object[] { LeftMouseButtonKey });

                // Alternative: try enum-based overload
                if (isKeyDown == null)
                {
                    var inputKeyType = inputType.Assembly.GetType("TaleWorlds.InputSystem.InputKey");
                    if (inputKeyType != null)
                    {
                        var isKeyDownEnum = inputType.GetMethod("IsKeyDown",
                            BindingFlags.Static | BindingFlags.Public,
                            null, new[] { inputKeyType }, null);
                        if (isKeyDownEnum != null)
                        {
                            object lmb = Enum.ToObject(inputKeyType, LeftMouseButtonKey);
                            mouseDown = (bool)isKeyDownEnum.Invoke(null, new[] { lmb });
                        }
                    }
                }

                // Get mouse position
                var mousePosProp = inputType.GetProperty("MousePositionPixel",
                    BindingFlags.Static | BindingFlags.Public);
                if (mousePosProp != null)
                {
                    var pos = mousePosProp.GetValue(null);
                    if (pos is Vec2 v2)
                    {
                        mouseX = v2.X;
                        mouseY = v2.Y;
                    }
                }

                // Detect click (mouse-up after mouse-down)
                bool clicked = _wasMouseDown && !mouseDown;
                _wasMouseDown = mouseDown;

                if (clicked && _mapButton != null)
                {
                    // Get button bounds via reflection to avoid System.Numerics dependency.
                    // GlobalPosition and Size return Vector2 which needs that assembly.
                    float btnLeft = 0, btnTop = 0, btnW = 0, btnH = 0;
                    try
                    {
                        var gp = typeof(Widget).GetProperty("GlobalPosition",
                            BindingFlags.Instance | BindingFlags.Public);
                        var sz = typeof(Widget).GetProperty("Size",
                            BindingFlags.Instance | BindingFlags.Public);
                        if (gp != null && sz != null)
                        {
                            object pos = gp.GetValue(_mapButton);
                            object size = sz.GetValue(_mapButton);
                            if (pos != null && size != null)
                            {
                                var xField = pos.GetType().GetField("X");
                                var yField = pos.GetType().GetField("Y");
                                if (xField != null && yField != null)
                                {
                                    btnLeft = (float)xField.GetValue(pos);
                                    btnTop = (float)yField.GetValue(pos);
                                    btnW = (float)xField.GetValue(size);
                                    btnH = (float)yField.GetValue(size);
                                }
                            }
                        }
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: button bounds lookup failed: " + ex.ToString()); }

                    float btnRight = btnLeft + btnW;
                    float btnBottom = btnTop + btnH;

                    if (mouseX >= btnLeft && mouseX <= btnRight
                        && mouseY >= btnTop && mouseY <= btnBottom)
                    {
                        _needsToggle = true;
                        MCMSettings.DebugLog("GlobalChronicle: button clicked at "
                            + mouseX + "," + mouseY);
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: CheckButtonClick failed: " + ex.ToString()); }
        }

        // ─── Button hover/press colors ───
        private static readonly Color _goldNormal = Color.FromUint(0xFFD4A017);
        private static readonly Color _goldBright = Color.FromUint(0xFFFFD700);
        private static readonly Color _goldDark   = Color.FromUint(0xFFA07810);

        /// <summary>
        /// Polls mouse position each tick to update button text color for hover/press feedback.
        /// Uses the same manual hit-testing as CheckButtonClick because InputRestrictions are disabled.
        /// </summary>
        private static void UpdateButtonHoverPress()
        {
            if (_mapButtonLabel == null) return;
            try
            {
                // Get mouse position and button state
                Type inputType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    inputType = asm.GetType("TaleWorlds.InputSystem.Input");
                    if (inputType != null) break;
                }
                if (inputType == null) return;

                float mouseX = 0f, mouseY = 0f;
                var mousePosProp = inputType.GetProperty("MousePositionPixel",
                    BindingFlags.Static | BindingFlags.Public);
                if (mousePosProp != null)
                {
                    var pos = mousePosProp.GetValue(null);
                    if (pos is Vec2 v2) { mouseX = v2.X; mouseY = v2.Y; }
                }

                bool mouseDown = false;
                var isKeyDown = inputType.GetMethod("IsKeyDown",
                    BindingFlags.Static | BindingFlags.Public,
                    null, new[] { typeof(int) }, null);
                if (isKeyDown != null)
                    mouseDown = (bool)isKeyDown.Invoke(null, new object[] { LeftMouseButtonKey });

                // Get button bounds
                float btnLeft = 0, btnTop = 0, btnW = 0, btnH = 0;
                var gp = typeof(Widget).GetProperty("GlobalPosition", AllFlags);
                var sz = typeof(Widget).GetProperty("Size", AllFlags);
                if (gp != null && sz != null)
                {
                    object bpos = gp.GetValue(_mapButton);
                    object bsz = sz.GetValue(_mapButton);
                    if (bpos != null && bsz != null)
                    {
                        var xf = bpos.GetType().GetField("X");
                        var yf = bpos.GetType().GetField("Y");
                        if (xf != null && yf != null)
                        {
                            btnLeft = (float)xf.GetValue(bpos);
                            btnTop  = (float)yf.GetValue(bpos);
                            btnW    = (float)xf.GetValue(bsz);
                            btnH    = (float)yf.GetValue(bsz);
                        }
                    }
                }

                bool hovering = mouseX >= btnLeft && mouseX <= btnLeft + btnW
                             && mouseY >= btnTop  && mouseY <= btnTop + btnH;

                Color target;
                if (hovering && mouseDown)
                    target = _goldDark;
                else if (hovering)
                    target = _goldBright;
                else
                    target = _goldNormal;

                bool wasHovered = _btnHovered;
                bool wasPressed = _btnPressed;
                _btnHovered = hovering;
                _btnPressed = hovering && mouseDown;

                // Only update brush when state changes to avoid per-frame overhead
                if (_btnHovered != wasHovered || _btnPressed != wasPressed)
                {
                    try { _mapButtonLabel.ReadOnlyBrush.FontColor = target; }
                    catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: button font color update failed: " + ex.ToString()); }
                }

                // Show/hide native hint bar on hover state change
                if (_btnHovered != wasHovered)
                {
                    if (_showHintMethod != null && _btnHovered)
                    {
                        try { _showHintMethod.Invoke(null, new object[] { "Chronicle Notes" }); }
                        catch (Exception ex) { MCMSettings.DebugLog("GlobalChronicle: ShowHint error: " + ex.ToString()); }
                    }
                    else if (_hideHintMethod != null && !_btnHovered)
                    {
                        try { _hideHintMethod.Invoke(null, null); }
                        catch (Exception ex) { MCMSettings.DebugLog("GlobalChronicle: HideInformations error: " + ex.ToString()); }
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: UpdateButtonHoverPress failed: " + ex.ToString()); }
        }

        // ─────────── Button Injection ───────────

        private static void TryInjectButton(ScreenBase topScreen)
        {
            try
            {
                _ownerScreen = topScreen;

                // ── Strategy 1: inject into the native MapBar's shortcut button list ──
                // The left HUD icons live in ListPanel Id=MapBar. Each button is:
                //   Widget (wrapper)
                //     IconOffsetButtonWidget Id=NavigationButton Brush=MapBar.Left.Button
                //       IconBrushWidget Id=Icon Brush=MapBar.Left.Generic.Icon
                //       HintWidget
                //     HintWidget
                GauntletLayer mapLayer = FindMapLayer(topScreen);
                if (mapLayer != null)
                {
                    Widget mapRoot = LoreSectionInjector.GetLayerRootWidget(mapLayer);
                    if (mapRoot != null)
                    {
                        Widget mapBarList = FindWidgetByExactId(mapRoot, 0, 10, "MapBar");
                        if (mapBarList != null && mapBarList.GetType().Name.Contains("ListPanel"))
                        {
                            // Check if our button already exists in the MapBar (avoid duplicates on screen transitions)
                            bool alreadyExists = false;
                            for (int i = 0; i < mapBarList.ChildCount; i++)
                            {
                                Widget existing = mapBarList.GetChild(i);
                                if (existing.Id == "EditableEncyclopedia_ChronicleWrapper")
                                {
                                    alreadyExists = true;
                                    // Re-capture the existing button references
                                    for (int j = 0; j < existing.ChildCount; j++)
                                    {
                                        Widget child = existing.GetChild(j);
                                        if (child.Id == "EditableEncyclopedia_ChronicleButton")
                                        {
                                            _mapButton = child;
                                            for (int k = 0; k < child.ChildCount; k++)
                                            {
                                                if (child.GetChild(k) is TextWidget tw)
                                                    _mapButtonLabel = tw;
                                            }
                                        }
                                    }
                                    _buttonInjected = true;
                                    MCMSettings.DebugLog("GlobalChronicle: button already exists in MapBar, reusing");
                                    break;
                                }
                            }
                            if (alreadyExists) return;

                            MCMSettings.DebugLog("GlobalChronicle: found MapBar ListPanel, children=" + mapBarList.ChildCount);

                            UIContext uiContext = mapBarList.EventManager?.Context as UIContext;
                            if (uiContext != null)
                            {
                                // Log each button's icon size to identify them
                                for (int i = 0; i < mapBarList.ChildCount; i++)
                                {
                                    Widget w = mapBarList.GetChild(i);
                                    var iconInfoSb = new StringBuilder();
                                    for (int j = 0; j < w.ChildCount; j++)
                                    {
                                        Widget c = w.GetChild(j);
                                        if (c.Id == "NavigationButton")
                                        {
                                            for (int k = 0; k < c.ChildCount; k++)
                                            {
                                                Widget ic = c.GetChild(k);
                                                if (ic.Id == "Icon")
                                                {
                                                    iconInfoSb.Clear();
                                                    iconInfoSb.Append(" iconW=").Append(ic.SuggestedWidth)
                                                              .Append(" iconH=").Append(ic.SuggestedHeight);
                                                }
                                            }
                                            iconInfoSb.Append(" btnW=").Append(c.SuggestedWidth);
                                        }
                                    }
                                    MCMSettings.DebugLog("GlobalChronicle: MapBar[" + i + "]" + iconInfoSb);
                                }

                                // Clone brush from a sibling NavigationButton
                                Brush btnBrush = null;
                                for (int i = 0; i < mapBarList.ChildCount && btnBrush == null; i++)
                                {
                                    Widget wrapper = mapBarList.GetChild(i);
                                    for (int j = 0; j < wrapper.ChildCount; j++)
                                    {
                                        Widget child = wrapper.GetChild(j);
                                        if (child is BrushWidget bw && child.Id == "NavigationButton"
                                            && bw.ReadOnlyBrush != null)
                                        {
                                            btnBrush = bw.ReadOnlyBrush.Clone();
                                            break;
                                        }
                                    }
                                }

                                // Create wrapper widget (matches native structure)
                                Widget btnWrapper = new Widget(uiContext);
                                btnWrapper.Id = "EditableEncyclopedia_ChronicleWrapper";
                                btnWrapper.WidthSizePolicy = SizePolicy.CoverChildren;
                                btnWrapper.HeightSizePolicy = SizePolicy.CoverChildren;
                                btnWrapper.VerticalAlignment = VerticalAlignment.Bottom;

                                // Create the button itself
                                Widget btn = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget");
                                if (btn == null) btn = new Widget(uiContext);
                                btn.Id = "EditableEncyclopedia_ChronicleButton";
                                btn.WidthSizePolicy = SizePolicy.Fixed;
                                btn.HeightSizePolicy = SizePolicy.Fixed;
                                btn.SuggestedWidth = MapBarButtonWidth;
                                btn.SuggestedHeight = MapBarButtonHeight;
                                btn.DoNotPassEventsToChildren = false;
                                btn.DoNotAcceptEvents = false;
                                // Enable hint tracking so HoverBegin/HoverEnd fire
                                try
                                {
                                    var hintEnabledProp = btn.GetType().GetProperty("IsHintEnabled",
                                        BindingFlags.Instance | BindingFlags.Public);
                                    if (hintEnabledProp != null && hintEnabledProp.CanWrite)
                                        hintEnabledProp.SetValue(btn, true);
                                }
                                catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: IsHintEnabled set failed: " + ex.ToString()); }
                                btn.VerticalAlignment = VerticalAlignment.Center;
                                if (btnBrush != null && btn is BrushWidget bwBtn)
                                    bwBtn.Brush = btnBrush;

                                // Icon — golden "J" letter
                                var iconText = new TextWidget(uiContext);
                                iconText.Text = "J";
                                iconText.WidthSizePolicy = SizePolicy.StretchToParent;
                                iconText.HeightSizePolicy = SizePolicy.StretchToParent;
                                iconText.HorizontalAlignment = HorizontalAlignment.Center;
                                iconText.VerticalAlignment = VerticalAlignment.Center;
                                try
                                {
                                    Brush textBrush = uiContext.GetBrush("MapTextBrushGal");
                                    if (textBrush == null) textBrush = FindAnyTextBrush(mapRoot);
                                    if (textBrush != null)
                                    {
                                        var cloned = textBrush.Clone();
                                        SetBrushColor(cloned, 0.85f, 0.72f, 0.35f, 1f);
                                        try
                                        {
                                            foreach (var style in cloned.Styles)
                                                style.FontSize = MapBarIconFontSize;
                                            cloned.FontSize = MapBarIconFontSize;
                                        }
                                        catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: icon font size set failed: " + ex.ToString()); }
                                        iconText.Brush = cloned;
                                    }
                                }
                                catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: icon text brush setup failed: " + ex.ToString()); }
                                btn.AddChild(iconText);

                                btnWrapper.AddChild(btn);
                                // Insert before the last button (Kingdoms)
                                int insertIndex = mapBarList.ChildCount > 0
                                    ? mapBarList.ChildCount - 1 : 0;
                                MCMSettings.DebugLog("GlobalChronicle: inserting at index " + insertIndex
                                    + " of " + mapBarList.ChildCount);
                                mapBarList.AddChildAtIndex(btnWrapper, insertIndex);

                                HookButtonClick(btn, () => { _needsToggle = true; });

                                // Hook HoverBegin/HoverEnd via EventFire for native hint bar
                                ResolveHintMethods();

                                // Ensure the map bar layer accepts mouse input (Naval DLC can block it)
                                try
                                {
                                    var inputRestrictionsProp = mapLayer.GetType().GetProperty("InputRestrictions",
                                        BindingFlags.Instance | BindingFlags.Public);
                                    if (inputRestrictionsProp != null)
                                    {
                                        object restrictions = inputRestrictionsProp.GetValue(mapLayer);
                                        if (restrictions != null)
                                        {
                                            // Try SetInputRestrictions(bool, InputUsageMask) — InputUsageMask.MouseButtons = 1
                                            bool invoked = false;
                                            foreach (var m in restrictions.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                                            {
                                                if (m.Name != "SetInputRestrictions") continue;
                                                var pars = m.GetParameters();
                                                if (pars.Length == 2 && pars[0].ParameterType == typeof(bool)
                                                    && pars[1].ParameterType.IsEnum)
                                                {
                                                    // Convert 1 (MouseButtons) to the enum type
                                                    object mouseVal = Enum.ToObject(pars[1].ParameterType, 1);
                                                    m.Invoke(restrictions, new object[] { true, mouseVal });
                                                    invoked = true;
                                                    break;
                                                }
                                                if (pars.Length == 1 && pars[0].ParameterType == typeof(bool))
                                                {
                                                    m.Invoke(restrictions, new object[] { true });
                                                    invoked = true;
                                                    break;
                                                }
                                            }
                                            if (invoked)
                                                MCMSettings.DebugLog("GlobalChronicle: layer input restrictions enabled for mouse");
                                            else
                                                MCMSettings.DebugLog("GlobalChronicle: SetInputRestrictions method not found");
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    MCMSettings.DebugLog("GlobalChronicle: layer input restriction error: " + ex.ToString());
                                }

                                _mapButton = btn;
                                _mapButtonLabel = iconText;
                                _buttonInjected = true;
                                MCMSettings.DebugLog("GlobalChronicle: button injected into native MapBar ListPanel");
                                return;
                            }
                        }
                        else
                        {
                            MCMSettings.DebugLog("GlobalChronicle: MapBar ListPanel not found, falling back");
                        }
                    }
                }

                // ── Strategy 2 (fallback): create a dedicated overlay layer ──
                _buttonLayer = new GauntletLayer("EditableEncyclopediaChronicleBtn", 1000, false);
                _buttonLayer.IsFocusLayer = false;
                _buttonLayer.InputRestrictions.SetInputRestrictions(false);
                topScreen.AddLayer(_buttonLayer);

                MCMSettings.DebugLog("GlobalChronicle: button layer added (fallback), layers="
                    + topScreen.Layers.Count);

                Widget layerRoot = LoreSectionInjector.GetLayerRootWidget(_buttonLayer);
                if (layerRoot == null)
                {
                    topScreen.RemoveLayer(_buttonLayer);
                    _buttonLayer = null;
                    return;
                }

                UIContext fallbackCtx = layerRoot.EventManager?.Context as UIContext;
                if (fallbackCtx == null)
                {
                    topScreen.RemoveLayer(_buttonLayer);
                    _buttonLayer = null;
                    return;
                }

                var fallbackBtn = new BrushWidget(fallbackCtx);
                fallbackBtn.Id = "EditableEncyclopedia_ChronicleButton";
                fallbackBtn.WidthSizePolicy = SizePolicy.Fixed;
                fallbackBtn.HeightSizePolicy = SizePolicy.Fixed;
                fallbackBtn.SuggestedWidth = FallbackButtonWidth;
                fallbackBtn.SuggestedHeight = FallbackButtonHeight;
                fallbackBtn.DoNotPassEventsToChildren = true;
                fallbackBtn.DoNotAcceptEvents = false;
                fallbackBtn.IsVisible = true;
                fallbackBtn.IsEnabled = true;
                fallbackBtn.HorizontalAlignment = HorizontalAlignment.Center;
                fallbackBtn.VerticalAlignment = VerticalAlignment.Bottom;
                fallbackBtn.MarginBottom = FallbackButtonMarginBottom;
                fallbackBtn.MarginRight = FallbackButtonMarginRight;

                var fallbackLabel = new TextWidget(fallbackCtx);
                fallbackLabel.Text = "\u270D Chronicle World";
                fallbackLabel.WidthSizePolicy = SizePolicy.CoverChildren;
                fallbackLabel.HeightSizePolicy = SizePolicy.StretchToParent;
                fallbackLabel.HorizontalAlignment = HorizontalAlignment.Center;
                fallbackLabel.VerticalAlignment = VerticalAlignment.Center;
                fallbackLabel.MarginTop = 24f;
                fallbackLabel.MarginLeft = 12f;
                fallbackLabel.MarginRight = 12f;
                try
                {
                    if (fallbackLabel.ReadOnlyBrush != null)
                    {
                        var labelBrush = fallbackLabel.ReadOnlyBrush.Clone();
                        ScaleBrushFontSize(labelBrush, 0.85f);
                        labelBrush.FontColor = Color.FromUint(0xFFD4A017);
                        fallbackLabel.Brush = labelBrush;
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: fallback label brush setup failed: " + ex.ToString()); }
                fallbackBtn.AddChild(fallbackLabel);

                layerRoot.AddChild(fallbackBtn);
                HookButtonClick(fallbackBtn, () => { _needsToggle = true; });
                ResolveHintMethods();

                _mapButton = fallbackBtn;
                _mapButtonLabel = fallbackLabel;
                _buttonInjected = true;
                MCMSettings.DebugLog("GlobalChronicle: chronicle button ready (fallback overlay)");
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("GlobalChronicle: TryInjectButton error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Finds a widget by exact Id match (not substring).
        /// </summary>
        private static Widget FindWidgetByExactId(Widget w, int depth, int maxDepth, string id)
        {
            if (w == null || depth > maxDepth) return null;
            if (w.Id == id) return w;
            for (int i = 0; i < w.ChildCount; i++)
            {
                Widget result = FindWidgetByExactId(w.GetChild(i), depth + 1, maxDepth, id);
                if (result != null) return result;
            }
            return null;
        }

        private static GauntletLayer FindMapLayer(ScreenBase topScreen)
        {
            var layers = topScreen.Layers;
            var allFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            bool verbose = _buttonInjectAttempts <= 1; // Only log details on first attempt

            if (verbose)
            {
                for (int i = 0; i < layers.Count; i++)
                {
                    var layer = layers[i];
                    bool isGauntlet = layer is GauntletLayer;
                    string layerType = layer.GetType().Name;
                    MCMSettings.DebugLog("GlobalChronicle: layer " + i + " type=" + layerType
                        + " isGauntlet=" + isGauntlet);
                }
            }

            // Strategy 0: find layer by ViewModel type (most robust, works with Naval DLC)
            for (int i = 0; i < layers.Count; i++)
            {
                if (!(layers[i] is GauntletLayer gl)) continue;
                try
                {
                    // Check the ViewModel — "MapBarVM" is used even in Naval DLC
                    object vm = null;
                    var prop = typeof(GauntletLayer).GetProperty("ViewModel", allFlags);
                    if (prop != null) vm = prop.GetValue(gl);
                    if (vm == null)
                    {
                        var field = typeof(GauntletLayer).GetField("_viewModel", allFlags);
                        if (field != null) vm = field.GetValue(gl);
                    }
                    if (vm != null)
                    {
                        string vmName = vm.GetType().Name;
                        if (vmName.Contains("MapBar"))
                        {
                            if (verbose)
                                MCMSettings.DebugLog("GlobalChronicle: found layer by ViewModel=" + vmName);
                            return gl;
                        }
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: ViewModel check failed: " + ex.ToString()); }
            }

            // Strategy 1: deep search via MapScreen reflection for MapBar layer
            GauntletLayer mapBarLayer = FindMapBarLayer(topScreen, allFlags, verbose);
            if (mapBarLayer != null)
            {
                if (verbose)
                    MCMSettings.DebugLog("GlobalChronicle: found MapBar layer via reflection");
                return mapBarLayer;
            }

            // Strategy 2: find the layer that contains the bottom bar (HUD layer)
            for (int i = 0; i < layers.Count; i++)
            {
                if (!(layers[i] is GauntletLayer gl)) continue;
                Widget root = LoreSectionInjector.GetLayerRootWidget(gl);
                if (root == null) continue;
                if (HasNameplateChildren(root)) continue;
                Widget bar = FindBottomBarWidget(root, false);
                if (bar != null)
                {
                    if (verbose)
                        MCMSettings.DebugLog("GlobalChronicle: selected layer " + i + " (has bottom bar)");
                    return gl;
                }
            }

            // Strategy 3: pick the largest non-nameplate GauntletLayer
            GauntletLayer best = null;
            int bestDesc = 0;
            for (int i = 0; i < layers.Count; i++)
            {
                if (!(layers[i] is GauntletLayer gl)) continue;
                Widget root = LoreSectionInjector.GetLayerRootWidget(gl);
                if (root == null) continue;
                if (HasNameplateChildren(root)) continue;
                int desc = CountDescendants(root, 0, 12);
                if (desc > bestDesc)
                {
                    bestDesc = desc;
                    best = gl;
                }
            }
            if (best != null && verbose)
                MCMSettings.DebugLog("GlobalChronicle: selected largest non-nameplate layer, desc=" + bestDesc);
            return best;
        }

        /// <summary>
        /// Deep-dive search for the MapBar's GauntletLayer.
        /// Bannerlord's MapBar is often buried inside MapNavigationHandler or
        /// a specialized MapInformationManager, not as a direct layer on MapScreen.
        /// We recursively search the screen's fields for objects that contain
        /// GauntletLayer instances with MapBar-related widgets.
        /// </summary>
        private static GauntletLayer FindMapBarLayer(ScreenBase topScreen, BindingFlags allFlags,
            bool verbose = false)
        {
            try
            {
                Type screenType = topScreen.GetType();
                if (verbose)
                    MCMSettings.DebugLog("GlobalChronicle: MapScreen type=" + screenType.FullName);

                // Now do a deep search: visit all fields recursively up to depth 3
                // looking for any object that holds a GauntletLayer with MapBar content
                var visited = new HashSet<object>(new ReferenceEqualityComparer());
                GauntletLayer result = DeepSearchForMapBarLayer(topScreen, allFlags, 0, 3, visited);
                if (result != null) return result;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("GlobalChronicle: FindMapBarLayer error: " + ex.ToString());
            }
            return null;
        }

        private static GauntletLayer DeepSearchForMapBarLayer(object obj, BindingFlags allFlags,
            int depth, int maxDepth, HashSet<object> visited)
        {
            if (obj == null || depth > maxDepth) return null;
            if (!visited.Add(obj)) return null; // Already visited

            Type objType = obj.GetType();
            // Skip primitive types and strings
            if (objType.IsPrimitive || obj is string || objType.IsEnum) return null;

            // Walk the type hierarchy to get all fields including inherited private ones
            Type currentType = objType;
            while (currentType != null && currentType != typeof(object))
            {
                foreach (var field in currentType.GetFields(allFlags | BindingFlags.DeclaredOnly))
                {
                    object val = null;
                    try { val = field.GetValue(obj); } catch (Exception) { continue; }
                    if (val == null) continue;

                    // If this field IS a GauntletLayer, check if it has MapBar widgets
                    if (val is GauntletLayer gl)
                    {
                        Widget root = LoreSectionInjector.GetLayerRootWidget(gl);
                        if (root != null)
                        {
                            bool hasBar = HasMapBarWidgets(root, 0, 6);
                            if (hasBar)
                            {
                                MCMSettings.DebugLog("GlobalChronicle: deep[" + depth + "] "
                                    + objType.Name + "." + field.Name + " => MapBar layer found");
                                return gl;
                            }
                        }
                    }

                    // If this field is a handler/manager/view object, recurse into it
                    string valTypeName = val.GetType().Name;
                    if (depth < maxDepth && !val.GetType().IsPrimitive && !(val is string)
                        && !val.GetType().IsEnum
                        && (valTypeName.Contains("Map") || valTypeName.Contains("Handler")
                            || valTypeName.Contains("Manager") || valTypeName.Contains("View")
                            || valTypeName.Contains("Navigation") || valTypeName.Contains("Bar")
                            || valTypeName.Contains("Information") || valTypeName.Contains("Gauntlet")))
                    {
                        var result = DeepSearchForMapBarLayer(val, allFlags, depth + 1, maxDepth, visited);
                        if (result != null) return result;
                    }

                    // Also check collections
                    if (val is System.Collections.IEnumerable enumerable && !(val is string))
                    {
                        foreach (object item in enumerable)
                        {
                            if (item == null) continue;
                            var result = DeepSearchForMapBarLayer(item, allFlags, depth + 1, maxDepth, visited);
                            if (result != null) return result;
                        }
                    }
                }
                currentType = currentType.BaseType;
            }
            return null;
        }

        /// <summary>
        /// Checks if a widget tree contains MapBar-related widgets
        /// (e.g., date display, gold, time controls).
        /// </summary>
        private static bool HasMapBarWidgets(Widget w, int depth, int maxDepth)
        {
            if (w == null || depth > maxDepth) return false;
            string id = (w.Id ?? "").ToLowerInvariant();
            string typeName = w.GetType().Name.ToLowerInvariant();
            if (id.Contains("date") || id.Contains("time") || id.Contains("speed")
                || id.Contains("pause") || id.Contains("gold") || id.Contains("denar")
                || id.Contains("mapbar") || id.Contains("bottombar")
                || typeName.Contains("maptimecontrol") || typeName.Contains("mapbar"))
                return true;
            for (int i = 0; i < w.ChildCount; i++)
            {
                if (HasMapBarWidgets(w.GetChild(i), depth + 1, maxDepth))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Reference equality comparer for visited-set to avoid infinite loops.
        /// </summary>
        private class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private static bool HasNameplateChildren(Widget w, int depth = 0)
        {
            if (w == null || depth > 5) return false;
            string typeName = w.GetType().Name;
            if (typeName.Contains("Nameplate")) return true;
            for (int i = 0; i < Math.Min(w.ChildCount, 10); i++)
            {
                if (HasNameplateChildren(w.GetChild(i), depth + 1))
                    return true;
            }
            return false;
        }

        private static Widget FindBottomBarWidget(Widget root, bool log = true)
        {
            // Log the top-level widget tree (skip nameplate subtrees to avoid spam)
            if (log) LogWidgetTreeFiltered(root, 0, 6);

            // Strategy 1: look for known Bannerlord bottom-bar widget IDs (exact match)
            // Priority order: BottomInfoBar first (the info bar with gold/influence),
            // then MapBar (the navigation button bar), then others
            string[] exactIds = { "BottomInfoBar", "MapBar", "BottomBar", "MapTimeControl",
                                  "MapBottomBar", "BottomPanel", "InfoBarWidget" };
            foreach (string id in exactIds)
            {
                Widget byId = FindWidgetByExactId(root, 0, 12, id);
                if (byId != null)
                {
                    MCMSettings.DebugLog("GlobalChronicle: found bottom bar by exact ID: " + id);
                    return byId;
                }
            }

            // Strategy 2: find a widget near the bottom with multiple children
            Widget byLayout = FindBottomBarByLayout(root, 0, 8);
            if (byLayout != null)
            {
                MCMSettings.DebugLog("GlobalChronicle: found bottom bar by layout: " + (byLayout.Id ?? byLayout.GetType().Name));
                return byLayout;
            }

            return null;
        }

        private static void LogWidgetTreeFiltered(Widget w, int depth, int maxDepth)
        {
            if (w == null || depth > maxDepth) return;
            // Skip nameplate subtrees to avoid log spam
            string typeName = w.GetType().Name;
            if (typeName.Contains("Nameplate") && depth > 1) return;
            string indent = new string(' ', depth * 2);
            MCMSettings.DebugLog("GlobalChronicle: tree " + indent
                + typeName + " Id=" + (w.Id ?? "(null)")
                + " VAlign=" + w.VerticalAlignment
                + " HAlign=" + w.HorizontalAlignment
                + " Children=" + w.ChildCount);
            for (int i = 0; i < w.ChildCount; i++)
                LogWidgetTreeFiltered(w.GetChild(i), depth + 1, maxDepth);
        }

        private static Widget FindBottomBarByLayout(Widget w, int depth, int maxDepth)
        {
            if (w == null || depth > maxDepth) return null;

            // Accept any widget positioned at the bottom with multiple children
            if (w.VerticalAlignment == VerticalAlignment.Bottom && w.ChildCount >= 2)
            {
                // Check children for date/time/speed/pause IDs OR type names
                for (int i = 0; i < w.ChildCount; i++)
                {
                    Widget child = w.GetChild(i);
                    if (child == null) continue;
                    string id = (child.Id ?? "").ToLowerInvariant();
                    string typeName = child.GetType().Name.ToLowerInvariant();
                    if (id.Contains("date") || id.Contains("time") || id.Contains("speed")
                        || id.Contains("pause") || id.Contains("gold") || id.Contains("denar")
                        || id.Contains("influence") || id.Contains("map")
                        || typeName.Contains("maptimecontrol") || typeName.Contains("mapbar"))
                        return w;
                }
            }

            for (int i = 0; i < w.ChildCount; i++)
            {
                Widget result = FindBottomBarByLayout(w.GetChild(i), depth + 1, maxDepth);
                if (result != null) return result;
            }
            return null;
        }

        // ─────────── Panel Toggle ───────────

        private static void TogglePanel(ScreenBase topScreen)
        {
            lock (_stateLock)
            {
                TogglePanelUnlocked(topScreen);
            }
        }

        private static void TogglePanelUnlocked(ScreenBase topScreen)
        {
            // 2026-05-29: legacy V1 panel removed — the chronicle panel is always V2.
            if (GlobalChroniclePanelV2Layer.IsOpen)
            {
                GlobalChroniclePanelV2Layer.Close();
                MCMSettings.DebugLog("GlobalChronicle: closing V2 panel");
            }
            else
            {
                GlobalChroniclePanelV2Layer.Open(topScreen);
                // Arm the "another panel opened" monitor in TickMainThread so opening
                // Inventory/Clan/etc. over the panel force-closes it and frees input.
                _ownerScreen = topScreen;
                _layerCountOnOpen = topScreen.Layers.Count; // includes the just-added V2 layer
                MCMSettings.DebugLog("GlobalChronicle: opening V2 panel");
            }
        }

        /// <summary>
        /// Immediately closes the Chronicle Panel without animation.
        /// Used when another game panel opens on top — the overlay must be removed
        /// instantly so its InputRestrictions don't block the new panel.
        /// </summary>
        internal static void ForceClosePanel()
        {
            lock (_stateLock)
            {
                ForceClosePanelUnlocked();
            }
        }

        private static void ForceClosePanelUnlocked()
        {
            _layerCountOnOpen = 0;
            if (GlobalChroniclePanelV2Layer.IsOpen)
                GlobalChroniclePanelV2Layer.Close();
            MCMSettings.DebugLog("GlobalChronicle: panel force-closed");
        }

        /// <summary>
        /// Tries known native popup/panel sprite names.  Returns true if a suitable
        /// dark background sprite was applied (caller should NOT apply a colour tint).
        /// Returns false if only a generic white sprite was found (caller should tint dark).
        /// </summary>
        internal static bool TrySetNativeBackgroundSprite(UIContext uiContext, ImageWidget bg)
        {
            // Candidates in priority order — native dark panels first, generic fallbacks last.
            // "popup_*" / "encyclopedia_*" names come from the game's own SpriteSheets.
            string[] nativeDark = {
                // From native Popup.xml — Popup.SceneNotificaton.Selection.Item brush.
                // 9-slice with ExtendLeft/Right/Top/Bottom=19.
                "scene_popup_selection_card_9",
                "scene_popup_selection_card_hover_9",
                // Other confirmed 9-slice sprites
                "npc_dialogue_panel_9",
                "frame_9",
                "subpage_slick_frame_9",
                "name_shadow_9",
            };
            string[] genericFallback = { "BlankWhiteSquare_9" };

            try
            {
                var spriteDataProp = uiContext.GetType().GetProperty("SpriteData", AllFlags);
                if (spriteDataProp == null) return false;
                var spriteData = spriteDataProp.GetValue(uiContext);
                if (spriteData == null) return false;
                var getSprite = spriteData.GetType().GetMethod("GetSprite", AllFlags);
                if (getSprite == null) return false;
                var spriteProp = typeof(ImageWidget).GetProperty("Sprite",
                    BindingFlags.Instance | BindingFlags.Public);
                if (spriteProp == null) return false;

                // Try native dark sprites first
                foreach (string name in nativeDark)
                {
                    try
                    {
                        var sprite = getSprite.Invoke(spriteData, new object[] { name });
                        if (sprite != null)
                        {
                            spriteProp.SetValue(bg, sprite);
                            MCMSettings.DebugLog("GlobalChronicle: bg sprite (native dark) = " + name);
                            return true;
                        }
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: native dark sprite attempt failed: " + ex.ToString()); continue; }
                }

                // Fall back to generic white sprite (caller will tint dark)
                foreach (string name in genericFallback)
                {
                    try
                    {
                        var sprite = getSprite.Invoke(spriteData, new object[] { name });
                        if (sprite != null)
                        {
                            spriteProp.SetValue(bg, sprite);
                            MCMSettings.DebugLog("GlobalChronicle: bg sprite (fallback, will tint) = " + name);
                            return false;
                        }
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: fallback sprite attempt failed: " + ex.ToString()); continue; }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: TrySetNativeBackgroundSprite failed: " + ex.ToString()); }

            MCMSettings.DebugLog("GlobalChronicle: no bg sprite found at all");
            return false;
        }

        // ─────────── Helpers ───────────

        private static void HookButtonClick(Widget btn, Action onClick)
        {
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
                MCMSettings.DebugLog("GlobalChronicle: HookButtonClick error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Resolves MBInformationManager.ShowHint / HideInformations methods via reflection.
        /// Called once during button injection; the actual show/hide is driven by
        /// UpdateButtonHoverPress which polls mouse position.
        /// </summary>
        private static void ResolveHintMethods()
        {
            try
            {
                // Resolve hint methods once if not cached
                if (_showHintMethod == null)
                {
                    var pubStatic = BindingFlags.Static | BindingFlags.Public;

                    // 1. Try InformationManager (TaleWorlds.Library) directly — available at compile time
                    var imType = typeof(InformationManager);
                    // Try all known method names for showing a hint: ShowHint, AddHintInformation, ShowTip
                    string[] showNames = { "ShowHint", "AddHintInformation", "ShowTip" };
                    string[] hideNames = { "HideInformations", "HideTip", "HideHint" };
                    foreach (var name in showNames)
                    {
                        _showHintMethod = imType.GetMethod(name, pubStatic, null, new[] { typeof(string) }, null);
                        if (_showHintMethod != null) break;
                    }
                    foreach (var name in hideNames)
                    {
                        _hideHintMethod = imType.GetMethod(name, pubStatic, null, Type.EmptyTypes, null);
                        if (_hideHintMethod != null) break;
                    }

                    // 2. If not on InformationManager, scan for MBInformationManager / other types
                    if (_showHintMethod == null)
                    {
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            var mbImType = asm.GetType("TaleWorlds.Library.MBInformationManager")
                                        ?? asm.GetType("TaleWorlds.Core.MBInformationManager")
                                        ?? asm.GetType("TaleWorlds.Core.InformationManager");
                            if (mbImType == null) continue;
                            foreach (var name in showNames)
                            {
                                _showHintMethod = mbImType.GetMethod(name, pubStatic, null, new[] { typeof(string) }, null);
                                if (_showHintMethod != null) break;
                            }
                            if (_hideHintMethod == null)
                            {
                                foreach (var name in hideNames)
                                {
                                    _hideHintMethod = mbImType.GetMethod(name, pubStatic, null, Type.EmptyTypes, null);
                                    if (_hideHintMethod != null) break;
                                }
                            }
                            if (_showHintMethod != null) break;
                        }
                    }

                    // 3. Log what we found (or dump available methods for debugging)
                    if (_showHintMethod != null)
                    {
                        MCMSettings.DebugLog("GlobalChronicle: hint methods resolved — show="
                            + _showHintMethod.DeclaringType.Name + "." + _showHintMethod.Name
                            + " hide=" + (_hideHintMethod != null ? _hideHintMethod.DeclaringType.Name + "." + _hideHintMethod.Name : "null"));
                    }
                    else
                    {
                        MCMSettings.DebugLog("GlobalChronicle: no hint method found, dumping InformationManager methods:");
                        foreach (var m in imType.GetMethods(pubStatic))
                        {
                            MCMSettings.DebugLog("  " + m.Name + "("
                                + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))
                                + ")");
                        }
                    }
                }

                // Note: EventFire HoverBegin/HoverEnd don't fire on programmatic ButtonWidgets.
                // Hint show/hide is handled by UpdateButtonHoverPress via mouse-position polling.
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("GlobalChronicle: ResolveHintMethods error: " + ex.ToString());
            }
        }

        private static int CountDescendants(Widget w, int depth, int maxDepth)
        {
            if (w == null || depth > maxDepth) return 0;
            int count = w.ChildCount;
            for (int i = 0; i < w.ChildCount; i++)
                count += CountDescendants(w.GetChild(i), depth + 1, maxDepth);
            return count;
        }

        private static Brush FindAnyTextBrush(Widget root)
        {
            if (root == null) return null;
            return FindTextBrushRecursive(root, 0, 10);
        }

        private static Brush FindTextBrushRecursive(Widget w, int depth, int maxDepth)
        {
            if (w == null || depth > maxDepth) return null;
            if (w is TextWidget tw && tw.ReadOnlyBrush != null)
                return tw.ReadOnlyBrush;
            if (w is BrushWidget bw && bw.ReadOnlyBrush != null)
            {
                // Check if this is a text-related brush
                string name = bw.ReadOnlyBrush.Name ?? "";
                if (name.Contains("Text") || name.Contains("text"))
                    return bw.ReadOnlyBrush;
            }
            for (int i = 0; i < w.ChildCount; i++)
            {
                var result = FindTextBrushRecursive(w.GetChild(i), depth + 1, maxDepth);
                if (result != null) return result;
            }
            return null;
        }

        private static void SetBrushColor(Brush brush, float r, float g, float b, float a)
        {
            if (brush == null) return;
            try
            {
                var fontColorProp = brush.GetType().GetProperty("FontColor", AllFlags);
                if (fontColorProp != null && fontColorProp.CanWrite)
                    fontColorProp.SetValue(brush, new Color(r, g, b, a));
            }
            catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: SetBrushColor failed: " + ex.ToString()); }
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
                        fontSizeProp.SetValue(brush, (int)(current * multiplier));
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: ScaleBrushFontSize failed: " + ex.ToString()); }
        }

        private static void CleanUp()
        {
            lock (_stateLock)
            {
                if (GlobalChroniclePanelV2Layer.IsOpen)
                {
                    GlobalChroniclePanelV2Layer.Close();
                }

                _mapButton = null;
                _mapButtonLabel = null;
                _btnHovered = false;
                _btnPressed = false;

                if (_buttonLayer != null)
                {
                    try
                    {
                        var screen = _ownerScreen ?? ScreenManager.TopScreen;
                        if (screen != null)
                            screen.RemoveLayer(_buttonLayer);
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("GlobalChroniclePanel: button layer removal failed: " + ex.ToString()); }
                    _buttonLayer = null;
                }

                _ownerScreen = null;

                _buttonInjected = false;
                _buttonInjectAttempts = 0;
                _layerCountOnOpen = 0;
                _needsToggle = false;
                _needsForceClose = false;
                _dismissMethodsResolved = false;
                _cachedIsKeyReleasedMethod = null;
            }
        }
    }
}
