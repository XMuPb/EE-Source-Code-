using System;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.ScreenSystem;

namespace BandItPlus.UI
{
    // Wave 4.23.0 (2026-06-12): the custom CONTACT panel — BP's own dialog UI
    // for character meetings (go-between vouch, future chief talks). Modal
    // (focus + input restrictions); ticked from OnApplicationTick for the
    // open fade. The CALLER must keep its vanilla-inquiry fallback for when
    // Open returns false.
    public static class BanditContactScreen
    {
        private static GauntletLayer _layer;
        private static BanditContactVM _vm;
        private static bool _isOpen;

        public static bool IsOpen => _isOpen;

        public static bool Open(string title, string body, string acceptLabel, string leaveLabel,
            Action onAccept, Action onLeave = null)
        {
            try
            {
                if (_isOpen) return false;
                TryLoadSpriteCategory("logo1");
                TryLoadSpriteCategory("ui_characterdeveloper"); // soft glow sprites (halo, crest, top light)

                _vm = new BanditContactVM(title, body, acceptLabel, leaveLabel, onAccept, onLeave);
                _layer = new GauntletLayer(name: "BanditContact", localOrder: 9003, shouldClear: false);
                _layer.InputRestrictions.SetInputRestrictions();
                _layer.LoadMovie("BanditContact", _vm);

                var screen = ScreenManager.TopScreen;
                if (screen != null) screen.AddLayer(_layer);
                _host = screen;

                _layer.InputRestrictions.SetInputRestrictions();
                _layer.IsFocusLayer = true;
                ScreenManager.TrySetFocus(_layer);

                _isOpen = true;
                // parley-parity: quiet attention sting on open (DLL-verified id).
                try { TaleWorlds.Engine.SoundEvent.PlaySound2D("event:/ui/notification/alert"); } catch { }
                Log("contact panel opened: " + title);
                return true;
            }
            catch (Exception ex)
            {
                Log("Open fail: " + ex.GetType().Name + ": " + ex.Message);
                _isOpen = false;
                _layer = null;
                _vm = null;
                return false;
            }
        }

        public static void Tick(float dt)
        {
            try { _vm?.Tick(dt); } catch { }
            // parley-parity: Esc / right-click = Step away (same path as the button).
            try
            {
                if (_isOpen && _layer != null && _layer.Input != null
                    && (_layer.Input.IsKeyReleased(TaleWorlds.InputSystem.InputKey.Escape)
                        || _layer.Input.IsKeyReleased(TaleWorlds.InputSystem.InputKey.RightMouseButton)))
                    _vm?.ExecuteLeave();
            }
            catch { }
        }

        // 07-11 crash fix: remove from the HOSTING screen, not TopScreen-at-close.
        private static TaleWorlds.ScreenSystem.ScreenBase _host;

        // Called by the VM's buttons.
        internal static void CloseFromVM()
        {
            try
            {
                if (_layer != null)
                {
                    _layer.InputRestrictions.ResetInputRestrictions();
                    var screen = _host ?? ScreenManager.TopScreen;
                    if (screen != null) screen.RemoveLayer(_layer);
                }
            }
            catch (Exception ex) { Log("Close fail: " + ex.Message); }
            _layer = null;
            _vm = null;
            _host = null;
            _isOpen = false;
            Log("contact panel closed");
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
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Contact] " + msg); }
            catch { }
        }
    }
}
