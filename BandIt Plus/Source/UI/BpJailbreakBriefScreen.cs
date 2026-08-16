using System;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.ScreenSystem;

namespace BandItPlus.UI
{
    // Wave 4.34 (2026-06-28): mounts the custom JAILBREAK briefing panel as a focus layer over the
    // captivity game menu. Modal; ticked from SubModuleClassEntry.OnApplicationTick for the open fade
    // and the living torch glow. The caller (JailbreakConseq) keeps a direct-launch fallback for when
    // Open returns false. Cloned from BanditParleyScreen.
    public static class BpJailbreakBriefScreen
    {
        private static GauntletLayer _layer;
        private static BpJailbreakBriefVM _vm;
        private static bool _isOpen;
        private static Action<string> _onChoice;
        private static TaleWorlds.CampaignSystem.CampaignTimeControlMode _prevTimeMode;
        private static bool _paused;

        public static bool IsOpen => _isOpen;

        public static bool Open(string captorName, string difficultyLabel, int guardCount, Action<string> onChoice)
        {
            try
            {
                if (_isOpen) return false;
                _onChoice = onChoice;
                TryLoadSpriteCategory("jailbreak");               // category holding the bp_jail scene-art sprite (jailbreak_1_tex.tpac)
                TryLoadSpriteCategory("ui_characterdeveloper");   // soft glow sprites (halo)

                _vm = new BpJailbreakBriefVM(captorName, difficultyLabel, guardCount);
                _layer = new GauntletLayer(name: "BpJailbreakBrief", localOrder: 9004, shouldClear: false);
                _layer.InputRestrictions.SetInputRestrictions();
                _layer.LoadMovie("BpJailbreakBrief", _vm);

                // FIX B (2026-07-01) — null-TopScreen soft-lock guard. With no screen to
                // attach the layer to, ABORT before mutating _isOpen / PauseGame, else the
                // game stays Stop-locked forever and _isOpen blocks the panel permanently.
                // The caller (BanditCaptivityBehavior) keeps a direct-launch fallback for false.
                var screen = ScreenManager.TopScreen;
                if (screen == null)
                {
                    _layer = null;
                    _vm = null;
                    _onChoice = null;
                    Log("Open aborted: TopScreen null");
                    return false;
                }
                screen.AddLayer(_layer);
                _host = screen;

                _layer.InputRestrictions.SetInputRestrictions();
                _layer.IsFocusLayer = true;
                ScreenManager.TrySetFocus(_layer);

                _isOpen = true;
                PauseGame();
                Log("brief opened: " + captorName + " / " + difficultyLabel + " / guards=" + guardCount);
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
        }

        // Called by the VM's Begin / Wait stamps.
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
            Log("brief closed");
        }

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
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-JailBrief] " + msg); }
            catch { }
        }
    }
}
