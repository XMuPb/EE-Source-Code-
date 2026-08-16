using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.ScreenSystem;

namespace BandItPlus.UI
{
    // Wave 4.25.10 (2026-06-16): the parchment LETTER screen. Open(title, body,
    // onClose) mounts a full-screen layer whose card is the "letter" parchment
    // sprite, with the chief's invitation in book-ink over the blank middle.
    // Ticked from OnApplicationTick for the fade; click anywhere closes.
    public static class BanditLetterScreen
    {
        private static GauntletLayer _layer;
        private static BanditLetterVM _vm;
        private static bool _isOpen;
        private static CampaignTimeControlMode _prevTimeMode;
        private static bool _timeCaptured;

        public static bool IsOpen => _isOpen;

        public static bool Open(string title, string body, Action onClose = null)
        {
            try
            {
                if (_isOpen) return false;
                TryLoadSpriteCategory("letter"); // the player-authored parchment sprite

                _vm = new BanditLetterVM(title, body, onClose);
                _layer = new GauntletLayer(name: "BanditLetter", localOrder: 9004, shouldClear: false);
                _layer.InputRestrictions.SetInputRestrictions();
                _layer.LoadMovie("BanditLetter", _vm);

                var screen = ScreenManager.TopScreen;
                if (screen != null) screen.AddLayer(_layer);
                _host = screen;

                _layer.InputRestrictions.SetInputRestrictions();
                _layer.IsFocusLayer = true;
                ScreenManager.TrySetFocus(_layer);

                _isOpen = true;

                // Pause the campaign clock while the letter is read (the typewriter still
                // animates — it ticks on the app tick, not the campaign clock). Re-asserted
                // in Tick like the cinematic/intro screens; restored in CloseFromVM.
                try
                {
                    var campaign = Campaign.Current;
                    if (campaign != null)
                    {
                        _prevTimeMode = campaign.TimeControlMode;
                        _timeCaptured = true;
                        campaign.TimeControlMode = CampaignTimeControlMode.Stop;
                    }
                }
                catch (Exception pex) { Log("pause failed: " + pex.Message); }

                Log("letter opened: " + title);
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
            // Hold the campaign paused while the letter is up (re-assert, matching the
            // cinematic/intro screens) so it can't be un-paused underneath the letter.
            try
            {
                var campaign = Campaign.Current;
                if (_isOpen && campaign != null && campaign.TimeControlMode != CampaignTimeControlMode.Stop)
                    campaign.TimeControlMode = CampaignTimeControlMode.Stop;
            }
            catch { }
        }

        // 07-11 crash fix: remove from the HOSTING screen, not TopScreen-at-close.
        private static TaleWorlds.ScreenSystem.ScreenBase _host;

        // Called by the VM (click-to-dismiss).
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
            _host = null;
            try { CustomSoundPlayer.Stop(); } catch { } // kill the typewriter loop on exit

            // Restore the campaign clock to its prior speed.
            try
            {
                var campaign = Campaign.Current;
                if (_timeCaptured && campaign != null) campaign.TimeControlMode = _prevTimeMode;
            }
            catch (Exception pex) { Log("unpause failed: " + pex.Message); }
            _timeCaptured = false;

            _layer = null;
            _vm = null;
            _isOpen = false;
            Log("letter closed");
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
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Letter] " + msg); }
            catch { }
        }
    }
}
