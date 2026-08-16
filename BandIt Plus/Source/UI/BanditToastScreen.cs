using System;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.ScreenSystem;

namespace BandItPlus.UI
{
    // Wave 4.20.1 (2026-06-12): the BANDIT PLUS toast — a passive left-center
    // crest notification (logo1 over a dark banner strip). NON-focus,
    // NO input restrictions: the game keeps running underneath. Generic
    // title/body so future systems (raids, alliances) can reuse it.
    public static class BanditToastScreen
    {
        private static GauntletLayer _layer;
        private static BanditToastVM _vm;
        private static bool _isOpen;

        public static bool IsOpen => _isOpen;

        // sound: any native "event:/ui/..." path; null/empty = silent. Verified
        // available (engine string scan 2026-06-12): notification/kingdom_decision,
        // peace, war_declared, quest_fail, relation, coins_positive, ...
        public static void Show(string title, string body,
            string sound = "event:/ui/notification/kingdom_decision",
            bool centered = false)
        {
            try
            {
                if (_isOpen) CloseNow(); // replace any toast already showing

                TryLoadSpriteCategory("logo1");

                if (!string.IsNullOrEmpty(sound))
                {
                    try { TaleWorlds.Engine.SoundEvent.PlaySound2D(sound); }
                    catch (Exception sEx) { Log("sound fail: " + sEx.Message); }
                }

                _vm = new BanditToastVM(title, body);
                _layer = new GauntletLayer(name: "BanditToast", localOrder: 8500, shouldClear: false);
                _layer.LoadMovie(centered ? "BanditToastCenter" : "BanditToast", _vm);

                var screen = ScreenManager.TopScreen;
                if (screen != null) screen.AddLayer(_layer);
                _host = screen;

                _isOpen = true;
                Log("toast shown: " + title);
            }
            catch (Exception ex)
            {
                Log("Show fail: " + ex.GetType().Name + ": " + ex.Message);
                _isOpen = false;
                _layer = null;
                _vm = null;
            }
        }

        // Driven from SubModuleClassEntry.OnApplicationTick while open.
        public static void Tick(float dt)
        {
            try
            {
                if (_vm == null) return;
                _vm.Tick(dt);
                if (_vm.Finished) CloseNow();
            }
            catch { }
        }

        // 07-11 crash fix: the layer must be removed from the screen that HOSTS it.
        // TopScreen can change between Show and CloseNow (toast auto-closed while the
        // quest journal covered the map -> orphaned layer -> OnActivate NRE on return).
        private static ScreenBase _host;

        private static void CloseNow()
        {
            try
            {
                if (_layer != null)
                {
                    var screen = _host ?? ScreenManager.TopScreen;
                    if (screen != null) screen.RemoveLayer(_layer);
                }
            }
            catch (Exception ex) { Log("Close fail: " + ex.Message); }
            _layer = null;
            _vm = null;
            _host = null;
            _isOpen = false;
        }

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
            catch (Exception ex) { Log("sprite load '" + categoryName + "' failed: " + ex.Message); }
        }

        private static void Log(string msg)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Toast] " + msg); }
            catch { }
        }
    }
}
