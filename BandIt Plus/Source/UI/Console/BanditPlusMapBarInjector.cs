using System;
using System.Reflection;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;

namespace BandItPlus.UI.Console
{
    // v183 Phase 3 final (2026-05-21): direct port of EE's GlobalChroniclePanel
    // runtime button injection. UIExtenderEx PrefabExtension was abandoned —
    // doesn't survive Naval DLC's NavalMapScreen substitution.
    //
    // Mechanism (EE-proven, works on Naval DLC):
    //   1. Find the MapBar GauntletLayer by ViewModel type name "MapBarVM"
    //      (Strategy 0 from EE's FindMapLayer — Naval-DLC-safe).
    //   2. Get layer root via _gauntletUIContext.EventManager.Root (EE's
    //      LoreSectionInjector.GetLayerRootWidget pattern).
    //   3. Find ListPanel Id="MapBar" inside (the left icon row).
    //   4. Build wrapper + ButtonWidget + sword sprite child.
    //   5. AddChildAtIndex before the last button (Kingdoms slot).
    //   6. HookButtonClick via reflection on EventFire event → opens Console.
    //
    // Self-limits to 30 attempts (~0.5s) to avoid spamming logs forever if the
    // MapBar layer never appears (e.g. on screens where it isn't supposed to).
    public static class BanditPlusMapBarInjector
    {
        private const BindingFlags AllFlags =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const int LeftMouseButtonKey = 224;
        private const int MaxInjectAttempts = 30;
        private const float ButtonWidth = 76f;
        private const float ButtonHeight = 52f;

        private static bool _injected;
        private static int _attempts;
        private static string _lastScreenName = "";
        private static Widget _button;
        private static Widget _icon;

        // Tints for hover polling. Start at full white so the sprite is unambiguously
        // visible; brighten to warm gold on hover (mirrors EE's _goldBright).
        private static readonly Color _iconColorNormal = Color.FromUint(0xFFFFFFFFU);
        private static readonly Color _iconColorHover  = Color.FromUint(0xFFFFD700U);

        // Hover state tracked across ticks (EE pattern).
        private static bool _wasHovered;
        private static int _hoverBoundsLogCounter; // logs every ~120 ticks for first ~10s

        public static void Tick(float dt)
        {
            try
            {
                if (Mission.Current != null) return;

                var topScreen = ScreenManager.TopScreen;
                if (topScreen == null) return;

                var screenName = topScreen.GetType().Name;
                if (screenName != _lastScreenName)
                {
                    _lastScreenName = screenName;
                    _attempts = 0;
                    _button = null;
                    _icon = null;
                    _injected = false;
                    _wasHovered = false;
                }

                if (screenName.IndexOf("Map", StringComparison.OrdinalIgnoreCase) < 0) return;

                // Inject phase: try to add the button until injected or attempts exhausted.
                if (!_injected && _attempts < MaxInjectAttempts)
                {
                    _attempts++;
                    TryInjectButton(topScreen);
                }

                // Hover polling phase: once the button is live, poll mouse vs its bounds
                // every tick (EE's UpdateButtonHoverPress pattern — hover events don't
                // fire reliably for child widgets in the MapBar ListPanel).
                if (_injected && _button != null && _icon != null)
                {
                    PollHoverState();
                }
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v183 MapBarInjector Tick fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // EE's UpdateButtonHoverPress, distilled. Reads mouse position + button bounds
        // via reflection (Vector2/Vec2 has .X/.Y as fields, awkward to type cleanly
        // without taking System.Numerics dep), compares, and tints the icon Color.
        // Only updates Color on state change to avoid per-frame churn.
        private static void PollHoverState()
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

                // v184 fix: MousePositionPixel returns TaleWorlds.Library.Vec2 — X/Y are
                // PROPERTIES, not fields. GetField("X") returns null and silently leaves
                // mouseX=0, which is why hover never fired. EE's pattern is to cast to Vec2
                // and use the properties directly.
                float mouseX = 0f, mouseY = 0f;
                var mousePosProp = inputType.GetProperty("MousePositionPixel",
                    BindingFlags.Static | BindingFlags.Public);
                if (mousePosProp != null)
                {
                    var pos = mousePosProp.GetValue(null);
                    if (pos is TaleWorlds.Library.Vec2 v2)
                    {
                        mouseX = v2.X;
                        mouseY = v2.Y;
                    }
                }

                // Read button bounds — GlobalPosition / Size also return Vec2 (X/Y properties).
                var bt = _button.GetType();
                var gpProp = bt.GetProperty("GlobalPosition", BindingFlags.Instance | BindingFlags.Public);
                var szProp = bt.GetProperty("Size", BindingFlags.Instance | BindingFlags.Public);
                if (gpProp == null || szProp == null) return;
                object posObj = gpProp.GetValue(_button);
                object sizeObj = szProp.GetValue(_button);
                if (posObj == null || sizeObj == null) return;
                if (!(posObj is TaleWorlds.Library.Vec2 bPos)) return;
                if (!(sizeObj is TaleWorlds.Library.Vec2 bSize)) return;

                float bLeft = bPos.X;
                float bTop  = bPos.Y;
                float bW    = bSize.X;
                float bH    = bSize.Y;

                bool hovering = mouseX >= bLeft && mouseX <= bLeft + bW
                             && mouseY >= bTop  && mouseY <= bTop + bH;

                // Periodic diagnostic: log every ~120 ticks for ~10 seconds. Earlier
                // one-shot at frame 0 showed size=(0,0) because layout hadn't run.
                // Periodic logging lets us see when (if ever) bounds settle, and what
                // MousePositionPixel actually returns over time.
                _hoverBoundsLogCounter++;
                if (_hoverBoundsLogCounter <= 600 && (_hoverBoundsLogCounter % 120) == 1)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "v183 hover bounds [t=" + _hoverBoundsLogCounter + "]: btn=("
                        + bLeft + "," + bTop + ") size=(" + bW + "x" + bH
                        + ") mouse=(" + mouseX + "," + mouseY + ")");
                }

                if (hovering != _wasHovered)
                {
                    _wasHovered = hovering;
                    _icon.Color = hovering ? _iconColorHover : _iconColorNormal;
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "v183 hover: " + (hovering ? "ENTER" : "LEAVE")
                        + " @ (" + mouseX + "," + mouseY + ")");
                }
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v183 PollHoverState fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void TryInjectButton(ScreenBase topScreen)
        {
            // Strategy 0 (EE-proven, Naval-DLC-safe): find GauntletLayer whose ViewModel
            // type name contains "MapBar". Works because MapBarVM is the VM TaleWorlds
            // attaches to the campaign-map HUD layer on all versions.
            GauntletLayer mapBarLayer = FindMapBarLayer(topScreen);
            if (mapBarLayer == null) return;

            Widget mapRoot = GetLayerRootWidget(mapBarLayer);
            if (mapRoot == null) return;

            // EE's FindWidgetByExactId: look for ListPanel Id="MapBar" within the HUD layer.
            Widget mapBarList = FindWidgetByExactId(mapRoot, 0, 12, "MapBar");
            if (mapBarList == null || !mapBarList.GetType().Name.Contains("ListPanel")) return;

            // Avoid double-inject on screen re-entry.
            for (int i = 0; i < mapBarList.ChildCount; i++)
            {
                if (mapBarList.GetChild(i).Id == "BanditPlus_ConsoleWrapper")
                {
                    _injected = true;
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "v183 MapBar: button already present, skipping");
                    return;
                }
            }

            UIContext ctx = mapBarList.EventManager?.Context as UIContext;
            if (ctx == null) return;

            // Clone the brush from a sibling NavigationButton so we visually match the row.
            Brush btnBrush = null;
            for (int i = 0; i < mapBarList.ChildCount && btnBrush == null; i++)
            {
                Widget wrapper = mapBarList.GetChild(i);
                for (int j = 0; j < wrapper.ChildCount; j++)
                {
                    if (wrapper.GetChild(j) is BrushWidget bw
                        && bw.Id == "NavigationButton"
                        && bw.ReadOnlyBrush != null)
                    {
                        btnBrush = bw.ReadOnlyBrush.Clone();
                        break;
                    }
                }
            }

            // Build wrapper Widget (matches the native list's per-item structure).
            var btnWrapper = new Widget(ctx)
            {
                Id = "BanditPlus_ConsoleWrapper",
                WidthSizePolicy = SizePolicy.CoverChildren,
                HeightSizePolicy = SizePolicy.CoverChildren,
                VerticalAlignment = VerticalAlignment.Bottom,
            };

            // The button itself — typed as ButtonWidget so clicks fire EventFire("Click").
            // DoNotPassEventsToChildren=true: input stays on the button; child icon is
            // decorative-only. Bannerlord native prefab pattern (matches what we used in
            // BanditPlusMapBarButton.xml). Without this, the 32x32 icon child eats every
            // click in its area and clicks only register on the rim.
            var btn = new ButtonWidget(ctx)
            {
                Id = "BanditPlus_ConsoleButton",
                WidthSizePolicy = SizePolicy.Fixed,
                HeightSizePolicy = SizePolicy.Fixed,
                SuggestedWidth = ButtonWidth,
                SuggestedHeight = ButtonHeight,
                DoNotPassEventsToChildren = true,
                DoNotAcceptEvents = false,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (btnBrush != null) btn.Brush = btnBrush;

            // Book icon — the custom "icon-book" sprite (icon-book_1_tex.tpac). Portrait art (1024x1536),
            // so the widget is sized portrait (30x42) to avoid squish. Starts pure white for max visibility.
            var icon = new Widget(ctx)
            {
                WidthSizePolicy = SizePolicy.Fixed,
                HeightSizePolicy = SizePolicy.Fixed,
                SuggestedWidth = 30f,
                SuggestedHeight = 42f,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Color = _iconColorNormal,
            };
            try
            {
                // Ensure the custom icon-book category (icon-book_1_tex.tpac) is loaded on the map screen —
                // the sprite generator strips AlwaysLoad, so GetSprite returns null until we load it.
                try { UIResourceManager.LoadSpriteCategory("icon-book"); } catch { }
                // SpriteData / Sprite live in TaleWorlds.TwoDimension which BandIt Plus doesn't
                // reference at compile time. Reflect to avoid adding the dep just for this.
                var spriteDataProp = ctx.GetType().GetProperty("SpriteData", AllFlags);
                var spriteData = spriteDataProp?.GetValue(ctx);
                if (spriteData != null)
                {
                    var getSprite = spriteData.GetType().GetMethod("GetSprite", new[] { typeof(string) });
                    var sprite = getSprite?.Invoke(spriteData, new object[] { "icon-book" });
                    if (sprite != null)
                    {
                        var spriteProp = icon.GetType().GetProperty("Sprite", AllFlags);
                        spriteProp?.SetValue(icon, sprite);
                    }
                }
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v183 MapBar icon sprite fail: " + ex.GetType().Name + ": " + ex.Message);
            }
            btn.AddChild(icon);
            _icon = icon;
            btnWrapper.AddChild(btn);

            // Insert before the last button (Kingdoms slot), like EE does.
            int insertIndex = mapBarList.ChildCount > 0 ? mapBarList.ChildCount - 1 : 0;
            mapBarList.AddChildAtIndex(btnWrapper, insertIndex);

            HookButtonEvents(btn, () =>
            {
                try
                {
                    BanditPlusConsoleService.OpenFromButton();
                }
                catch (Exception ex)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "v183 MapBar onClick fail: " + ex.GetType().Name + ": " + ex.Message);
                }
            });

            _button = btn;
            _injected = true;
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                "v183 MapBar: button injected (index=" + insertIndex
                + " of " + mapBarList.ChildCount + ", layer.VM=" + (mapBarLayer != null ? "ok" : "null")
                + ")");
        }

        // Find the GauntletLayer whose widget tree contains ListPanel Id="MapBar".
        //
        // Naval DLC stores the HUD layer in ScreenManager.SortedLayers (which aggregates
        // every active layer across all screens + global layers), NOT in
        // topScreen.Layers. So we iterate SortedLayers and test each layer's widget tree
        // for the actual target widget — most direct evidence of the right layer.
        //
        // Falls back to topScreen.Layers if SortedLayers isn't reachable.
        private static GauntletLayer FindMapBarLayer(ScreenBase topScreen)
        {
            // Primary path: ScreenManager.SortedLayers
            try
            {
                Type smType = typeof(ScreenManager);
                var sortedProp = smType.GetProperty("SortedLayers",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var sortedLayers = sortedProp?.GetValue(null) as System.Collections.IEnumerable;
                if (sortedLayers != null)
                {
                    foreach (var layer in sortedLayers)
                    {
                        if (!(layer is GauntletLayer gl)) continue;
                        var root = GetLayerRootWidget(gl);
                        if (root == null) continue;
                        var hit = FindWidgetByExactId(root, 0, 12, "MapBar");
                        if (hit != null && hit.GetType().Name.Contains("ListPanel"))
                        {
                            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                                "v183 MapBar layer found via SortedLayers");
                            return gl;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v183 FindMapBarLayer SortedLayers fail: " + ex.GetType().Name + ": " + ex.Message);
            }

            // Fallback: topScreen.Layers (works for non-DLC MapScreen).
            foreach (var layer in topScreen.Layers)
            {
                if (!(layer is GauntletLayer gl)) continue;
                var root = GetLayerRootWidget(gl);
                if (root == null) continue;
                var hit = FindWidgetByExactId(root, 0, 12, "MapBar");
                if (hit != null && hit.GetType().Name.Contains("ListPanel"))
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "v183 MapBar layer found via topScreen.Layers (fallback)");
                    return gl;
                }
            }
            return null;
        }

        // EE's LoreSectionInjector.GetLayerRootWidget — reflects GauntletLayer's
        // _gauntletUIContext (or fallback) to reach EventManager.Root.
        private static Widget GetLayerRootWidget(GauntletLayer layer)
        {
            try
            {
                var t = typeof(GauntletLayer);
                foreach (var fieldName in new[] { "_gauntletUIContext", "_uiContext", "_context" })
                {
                    var f = t.GetField(fieldName, AllFlags);
                    if (f == null) continue;
                    var uiCtx = f.GetValue(layer) as UIContext;
                    if (uiCtx?.EventManager?.Root != null) return uiCtx.EventManager.Root;
                }
                foreach (var propName in new[] { "UIContext", "_gauntletUIContext" })
                {
                    var p = t.GetProperty(propName, AllFlags);
                    if (p == null) continue;
                    var uiCtx = p.GetValue(layer) as UIContext;
                    if (uiCtx?.EventManager?.Root != null) return uiCtx.EventManager.Root;
                }
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v183 GetLayerRootWidget fail: " + ex.GetType().Name + ": " + ex.Message);
            }
            return null;
        }

        // EE's FindWidgetByExactId — straight recursive Id-match.
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

        // Hook EventFire for Click + HoverBegin/HoverEnd. With
        // DoNotPassEventsToChildren=true on the button, mouse events stay on the button
        // and don't get eaten by the icon child — so HoverBegin/HoverEnd dispatch to us.
        // Logs each first-fire of every event name so we can verify which events flow.
        private static readonly System.Collections.Generic.HashSet<string> _firedEventNames
            = new System.Collections.Generic.HashSet<string>();
        private static void HookButtonEvents(Widget btn, Action onClick)
        {
            try
            {
                var eventFireEvent = btn.GetType().GetEvent("EventFire", AllFlags);
                if (eventFireEvent == null) return;
                Action<Widget, string, object[]> handler = (Widget sender, string eventName, object[] args) =>
                {
                    // Diagnostic: log each event name once so we can verify HoverBegin/HoverEnd
                    // actually fire on this button.
                    if (_firedEventNames.Add(eventName))
                    {
                        BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                            "v183 button event fired: '" + eventName + "'");
                    }

                    if (eventName == "Click")
                    {
                        onClick?.Invoke();
                    }
                    else if (eventName == "HoverBegin" || eventName == "MouseEnter" || eventName == "PointerEnter")
                    {
                        if (_icon != null) _icon.Color = _iconColorHover;
                    }
                    else if (eventName == "HoverEnd" || eventName == "MouseLeave" || eventName == "PointerExit")
                    {
                        if (_icon != null) _icon.Color = _iconColorNormal;
                    }
                };
                eventFireEvent.AddEventHandler(btn, handler);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v183 HookButtonEvents fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
