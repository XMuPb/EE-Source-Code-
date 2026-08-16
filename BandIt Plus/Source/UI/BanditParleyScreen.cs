using System;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.ScreenSystem;

namespace BandItPlus.UI
{
    // Wave 4.24.0 (2026-06-12): mounts the custom PARLEY panel. Modal; ticked
    // from OnApplicationTick for the open fade. The CALLER must keep its
    // vanilla MultiSelectionInquiry fallback for when Open returns false.
    public static class BanditParleyScreen
    {
        private static GauntletLayer _layer;
        private static BanditParleyVM _vm;
        private static bool _isOpen;
        private static Action<string> _onChoice;
        // Wave 4.28.x: pause the campaign clock while the parley is open.
        private static TaleWorlds.CampaignSystem.CampaignTimeControlMode _prevTimeMode;
        private static bool _paused;

        public static bool IsOpen => _isOpen;

        public static bool Open(string title, string body,
            string payLabel, string payHint, bool canPay,
            string huntLabel, string huntHint, bool canHunt,
            string walkHint, Action<string> onChoice,
            TaleWorlds.CampaignSystem.Hero chief = null)
        {
            try
            {
                if (_isOpen) return false;
                _onChoice = onChoice;
                TryLoadSpriteCategory("logo1");
                TryLoadSpriteCategory("ui_characterdeveloper"); // soft glow sprites (halo, crest, jewel)
                TryLoadSpriteCategory("parley"); // 2026-07-02 redesign: full-art camp background (parley_1..4)

                _vm = new BanditParleyVM(title, body,
                    payLabel, payHint, canPay, huntLabel, huntHint, canHunt, walkHint, chief);
                _layer = new GauntletLayer(name: "BanditParley", localOrder: 9003, shouldClear: false);
                _layer.InputRestrictions.SetInputRestrictions();
                _layer.LoadMovie("BanditParley", _vm);

                var screen = ScreenManager.TopScreen;
                if (screen != null) screen.AddLayer(_layer);
                _host = screen;

                _layer.InputRestrictions.SetInputRestrictions();
                _layer.IsFocusLayer = true;
                ScreenManager.TrySetFocus(_layer);

                // v6 sound pass: open sting — outlaws naming their price (DLL-verified id).
                try { TaleWorlds.Engine.SoundEvent.PlaySound2D("event:/ui/notification/ransom_offer"); } catch { }

                _isOpen = true;
                PauseGame();
                Log("parley panel opened: " + title);
                return true;
            }
            catch (Exception ex)
            {
                Log("Open fail: " + ex.GetType().Name + ": " + ex.Message);
                _isOpen = false;
                _layer = null;
                _vm = null;
                _onChoice = null;
                return false;
            }
        }

        public static void Tick(float dt)
        {
            try { _vm?.Tick(dt); } catch { }
            // v7: Esc / right-click = Walk away (same path as the button — retreat
            // horn + the caller's "leave" handling). Polled on the layer's own
            // input context, so it never fires while the parley is closed.
            try
            {
                if (_isOpen && _layer != null && _layer.Input != null
                    && (_layer.Input.IsKeyReleased(TaleWorlds.InputSystem.InputKey.Escape)
                        || _layer.Input.IsKeyReleased(TaleWorlds.InputSystem.InputKey.RightMouseButton)))
                    _vm?.ExecuteWalk();
            }
            catch { }
        }

        // Called by the VM's option stamps.
        internal static void Choose(string choice)
        {
            var cb = _onChoice;
            CloseNow();
            try { cb?.Invoke(choice); } catch (Exception ex) { Log("choice cb fail: " + ex.Message); }
        }

        // 07-11 crash fix: remove from the HOSTING screen, not TopScreen-at-close.
        private static TaleWorlds.ScreenSystem.ScreenBase _host;

        private static void CloseNow()
        {
            UnpauseGame();
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
            _onChoice = null;
            Log("parley panel closed");
        }

        // Pause the campaign clock on open; restore the player's prior speed on close.
        private static void PauseGame()
        {
            try
            {
                var camp = TaleWorlds.CampaignSystem.Campaign.Current;
                if (camp == null) return;
                _prevTimeMode = camp.TimeControlMode;
                camp.SetTimeControlModeLock(false);
                camp.TimeControlMode = TaleWorlds.CampaignSystem.CampaignTimeControlMode.Stop;
                camp.SetTimeControlModeLock(true);
                _paused = true;
            }
            catch (Exception ex) { Log("pause fail: " + ex.Message); }
        }

        private static void UnpauseGame()
        {
            try
            {
                if (!_paused) return;
                _paused = false;
                var camp = TaleWorlds.CampaignSystem.Campaign.Current;
                if (camp != null)
                {
                    camp.SetTimeControlModeLock(false);
                    camp.TimeControlMode = _prevTimeMode;
                }
            }
            catch (Exception ex) { Log("unpause fail: " + ex.Message); }
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
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Parley] " + msg); }
            catch { }
        }
    }
}
