using System;
using TaleWorlds.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.InputSystem;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;

namespace BandItPlus.UI.Console
{
    // V3 (2026-05-21): Royal Codex Console. Layer at localOrder 9000 (above
    // engine PAUSED overlay around 5000). OpenProgress drives a 0->1 ramp
    // over ~0.3s for combined scale+alpha animation on the codex root widget.
    public static class BanditPlusConsoleService
    {
        private static GauntletLayer _layer;
        private static BanditPlusConsoleVMv3 _vm;
        private static bool _isOpen;
        private const float kOpenDuration = 0.3f;
        private static float _animElapsed; // accumulates dt for ambient pulses (stripe + restore verses)
        private static float _slideElapsed; // resets on Open, drives bookmark cascade
        private static float _diagPollAccum; // Wave 4.17.1: 1s auto-refresh of the Raid Operations board

        public static void Tick(float dt)
        {
            try
            {
                // 2026-05-22: Ctrl+Shift+B hotkey removed — map-bar button
                // (BanditPlusMapBarInjector) is now the canonical open path.
                // Tick still runs every frame; bails when closed so animations
                // and the Escape-to-close handler below only execute when open.
                if (!_isOpen) return;

                if (_vm != null && _vm.OpenProgress < 1.0f)
                {
                    float next = _vm.OpenProgress + (dt / kOpenDuration);
                    if (next > 1.0f) next = 1.0f;
                    _vm.OpenProgress = next;
                }

                if (_vm != null)
                {
                    _animElapsed += dt;
                    _slideElapsed += dt;

                    // A. Chapter content fade-in on tab switch (0 -> 1 over 0.2s)
                    if (_vm.ChapterContentAlpha < 1.0f)
                    {
                        float next = _vm.ChapterContentAlpha + dt / 0.2f;
                        _vm.ChapterContentAlpha = next > 1.0f ? 1.0f : next;
                    }

                    // A2. Chapter content slide-in from right (MarginLeft 140 -> 60 over 0.3s)
                    if (_vm.ChapterContentMarginLeft > 60.5f)
                    {
                        float next = _vm.ChapterContentMarginLeft - 80f * dt / 0.3f;
                        _vm.ChapterContentMarginLeft = next < 60f ? 60f : next;
                    }

                    // D. Drop cap fade-in on chapter switch (0 -> 1 over 0.25s)
                    if (_vm.DropCapAlpha < 1.0f)
                    {
                        float next = _vm.DropCapAlpha + dt / 0.25f;
                        _vm.DropCapAlpha = next > 1.0f ? 1.0f : next;
                    }

                    // B. Active stripe gentle pulse — 1.5s period, alpha 0.7 ↔ 1.0
                    float stripePulse = 0.7f + 0.3f * (float)((Math.Sin(_animElapsed * (2.0 * Math.PI / 1.5)) + 1.0) * 0.5);
                    _vm.ActiveStripeAlpha = stripePulse;

                    // C. Bookmark slide-in cascade on open — 4 bookmarks, 80ms stagger, 0.35s each
                    const float startOffset = -120f;
                    const float slideDuration = 0.35f;
                    const float stagger = 0.08f;
                    for (int n = 0; n < 7; n++)
                    {
                        float t = _slideElapsed - n * stagger;
                        float p = t < 0f ? 0f : (t > slideDuration ? 1f : t / slideDuration);
                        float eased = 1f - (1f - p) * (1f - p) * (1f - p); // ease-out cubic
                        float offset = startOffset * (1f - eased);
                        switch (n)
                        {
                            case 0: _vm.Bookmark1Offset = offset; break;
                            case 1: _vm.Bookmark2Offset = offset; break;
                            case 2: _vm.Bookmark3Offset = offset; break;
                            case 3: _vm.Bookmark4Offset = offset; break;
                            case 4: _vm.Bookmark5Offset = offset; break;
                            case 5: _vm.Bookmark6Offset = offset; break;
                            case 6: _vm.Bookmark7Offset = offset; break; // Wave 4.28.x: VII slot
                        }
                    }

                    // D2. Wave 4.17.1: live Raid Operations board — auto-refresh once per
                    // second while the Diagnostics chapter is open (replaces the old
                    // Refresh button, which the bottom Restore-Verses bar overlapped).
                    if (_vm.IsCategoryDiagnosticsVisible)
                    {
                        _diagPollAccum += dt;
                        if (_diagPollAccum >= 1.0f)
                        {
                            _diagPollAccum = 0f;
                            _vm.PollDiagnostics();
                        }
                    }

                    // E. Restore Verses pulse when chapter has been modified from defaults
                    if (_vm.IsChapterModified)
                    {
                        float restorePulse = 0.85f + 0.25f * (float)((Math.Sin(_animElapsed * (2.0 * Math.PI / 1.0)) + 1.0) * 0.5);
                        _vm.RestoreColorFactor = restorePulse;
                    }
                    else
                    {
                        _vm.RestoreColorFactor = 1.0f;
                    }
                }

                if (Input.IsKeyPressed(InputKey.Escape))
                    Close();
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("V3 Console Tick fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void OpenFromButton() => Open();

        private static void Open()
        {
            try
            {
                if (_isOpen) return;
                if (MCMSettings.Instance == null)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "V3 Console: MCMSettings.Instance null - refusing to open");
                    return;
                }
                _vm = new BanditPlusConsoleVMv3();
                _vm.OpenProgress = 0f;
                _slideElapsed = 0f; // restart bookmark slide-in cascade on every open

                // localOrder 9000 puts the Console above the engine PAUSED
                // overlay (~5000) - fixes the V2 PAUSED-on-top regression.
                _layer = new GauntletLayer(name: "BanditPlusConsoleV3", localOrder: 9000, shouldClear: false);
                _layer.InputRestrictions.SetInputRestrictions();
                // Custom / non-resident sprite categories aren't loaded on the map screen
                // by default — force them before LoadMovie or the crest + glows render blank.
                TryLoadSpriteCategory("logo1");                 // BANDIT PLUS crest emblem
                TryLoadSpriteCategory("thumbnail");             // BANDIT PLUS art (bottom-left plate)
                TryLoadSpriteCategory("ui_characterdeveloper"); // glow sprites (halo, crest, seam)
                _layer.LoadMovie("BanditPlusConsoleV3", _vm);

                var screen = ScreenManager.TopScreen;
                if (screen != null) screen.AddLayer(_layer);

                _layer.InputRestrictions.SetInputRestrictions();
                _layer.IsFocusLayer = true;
                ScreenManager.TrySetFocus(_layer);

                _isOpen = true;
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("V3 Console: opened");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("V3 Console Open fail: "
                    + ex.GetType().Name + ": " + ex.Message);
                _isOpen = false;
                _layer = null;
                _vm = null;
            }
        }

        public static void Close()
        {
            try
            {
                // Persist every change made this session to MCM's Global JSON —
                // the single choke point covering all setters + Restore Verses
                // (07-03: values used to silently revert on game restart).
                MCMSettings.PersistNow();
                if (_layer != null)
                {
                    _layer.InputRestrictions.ResetInputRestrictions();
                    var screen = ScreenManager.TopScreen;
                    if (screen != null) screen.RemoveLayer(_layer);
                }
                _layer = null;
                _vm = null;
                _isOpen = false;
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("V3 Console: closed — settings persisted");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("V3 Console Close fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Force a sprite category into memory (map-screen layers don't get custom /
        // non-resident categories by default — same helper the HUD/origin screens use).
        private static void TryLoadSpriteCategory(string categoryName)
        {
            try
            {
                var spriteData = UIResourceManager.SpriteData;
                if (spriteData == null) return;
                TaleWorlds.TwoDimension.SpriteCategory category;
                if (!spriteData.SpriteCategories.TryGetValue(categoryName, out category) || category == null) return;
                if (!category.IsLoaded)
                    category.Load(UIResourceManager.ResourceContext, UIResourceManager.ResourceDepot);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "V3 Console sprite '" + categoryName + "' load failed: " + ex.Message);
            }
        }
    }
}
