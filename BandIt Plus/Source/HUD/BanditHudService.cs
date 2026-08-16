using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.ScreenSystem;

namespace BandItPlus.HUD
{
    // Wave 4.28.0 — mounts the persistent campaign-map HUD bar. Reuses the proven
    // Console layer-mount pattern, but PASSIVE: no focus layer, no input restriction
    // (the map stays fully interactive). Shown only while MapState is active AND an
    // origin has been chosen; unmounted on missions / states that leave the map.
    // Ticked every frame from SubModuleClassEntry.OnApplicationTick.
    public static class BanditHudService
    {
        private static GauntletLayer _layer;
        private static BanditHudVM _vm;
        private static ScreenBase _host;
        private static bool _mounted;

        public static void Tick(float dt)
        {
            try
            {
                bool onMap = Campaign.Current != null
                    && Game.Current != null
                    && Game.Current.GameStateManager != null
                    && Game.Current.GameStateManager.ActiveState is MapState;
                var origin = BandItPlus.Origins.BanditOriginBehavior.Instance;
                bool chosen = origin != null && origin.OriginChosen;
                // Console toggle (Bandit Power chapter, "Show map HUD"): default ON if
                // MCM isn't ready yet. Flipping it off makes this fall through to Unmount.
                bool hudEnabled = MCMSettings.Instance == null || MCMSettings.Instance.ShowMapHud;
                var top = ScreenManager.TopScreen;

                if (onMap && chosen && hudEnabled && top != null)
                {
                    if (!_mounted || _host != top) { Unmount(); Mount(top); }
                    if (_vm != null) _vm.Tick(dt);
                }
                else if (_mounted)
                {
                    Unmount();
                }
            }
            catch (Exception ex) { Log("Tick fail: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void Mount(ScreenBase screen)
        {
            try
            {
                _vm = new BanditHudVM();
                // Custom sprite categories are NOT resident on the campaign map — force-load
                // them before LoadMovie or the crest/icons render blank (origin-screen pattern).
                TryLoadSpriteCategory("hud_icons");            // the 6 status icons
                TryLoadSpriteCategory("logo1");                // crest
                TryLoadSpriteCategory("ui_characterdeveloper"); // firelight glow sprite
                // low localOrder = passive overlay above the map terrain; NOT a focus
                // layer and NO input restriction, so map clicks / drag still pass through.
                _layer = new GauntletLayer(name: "BanditPlusHud", localOrder: 120, shouldClear: false);
                _layer.LoadMovie("BanditHud", _vm);
                screen.AddLayer(_layer);
                _host = screen;
                _mounted = true;
                Log("HUD mounted on " + screen.GetType().Name);
            }
            catch (Exception ex)
            {
                Log("Mount fail: " + ex.GetType().Name + ": " + ex.Message);
                _layer = null; _vm = null; _host = null; _mounted = false;
            }
        }

        private static void Unmount()
        {
            try { if (_layer != null && _host != null) _host.RemoveLayer(_layer); } catch { }
            _layer = null; _vm = null; _host = null; _mounted = false;
        }

        // Force a custom sprite category into memory (campaign-map layers don't get them
        // resident by default — same helper the origin screen uses).
        private static void TryLoadSpriteCategory(string categoryName)
        {
            try
            {
                var spriteData = UIResourceManager.SpriteData;
                if (spriteData == null) return;
                TaleWorlds.TwoDimension.SpriteCategory category;
                if (!spriteData.SpriteCategories.TryGetValue(categoryName, out category) || category == null)
                { Log("sprite category not found: " + categoryName); return; }
                if (!category.IsLoaded)
                    category.Load(UIResourceManager.ResourceContext, UIResourceManager.ResourceDepot);
                Log("sprite category '" + categoryName + "' loaded=" + category.IsLoaded);
            }
            catch (Exception ex) { Log("sprite load '" + categoryName + "' failed: " + ex.Message); }
        }

        private static void Log(string msg)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-HUD] " + msg); }
            catch { }
        }
    }
}
