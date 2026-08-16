using System;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.MountAndBlade.View.MissionViews;
using BandItPlus.HideoutVisit;

namespace BandItPlus.UI
{
    // Wave 4.12-fix v77 (2026-05-12): MissionView that owns the camp-entry
    // banner GauntletLayer for the lifetime of the walkable hideout scene.
    // Auto-hides the banner after kDisplaySeconds via the VM's IsVisible
    // property (which the prefab binds to its root Widget).
    //
    // Layer priority 150 places it above HUD widgets (~100) but below modal
    // popups (~10000) — visible while playing but not interfering with menus.
    public class CampEntryBannerMissionView : MissionView
    {
        // v80 (2026-05-13): bumped 6s → 12s. At 6s the banner faded right as the
        // greeter conversation finished, so the player rarely got to read it.
        // 12s gives clear time before the greeter arrives (~5s) AND survives past
        // the convo so the player can re-read after refocusing on the screen.
        private const float kDisplaySeconds = 12f;

        private GauntletLayer _layer;
        private CampEntryBannerVM _vm;
        private float _elapsed;
        private bool _fadedOut;

        private readonly string _campName;
        private readonly string _subtitleText;
        private readonly string _bannerCode;

        public CampEntryBannerMissionView(string campName, string subtitleText, string bannerCode = "")
        {
            _campName = campName ?? "";
            _subtitleText = subtitleText ?? "";
            _bannerCode = bannerCode ?? "";
        }

        private bool _layerAddedToScreen;
        private TaleWorlds.MountAndBlade.View.Screens.MissionScreen _missionScreen;

        public override void OnMissionScreenInitialize()
        {
            base.OnMissionScreenInitialize();
            try
            {
                _vm = new CampEntryBannerVM(_campName, _subtitleText, _bannerCode);
                _layer = new GauntletLayer(name: "CampEntryBanner", localOrder: 150, shouldClear: false);
                _layer.LoadMovie("CampEntryBanner", _vm);
                HideoutPeacefulVisitState.Log("v78: CampEntryBanner prebuilt (VM+layer+movie); waiting for MissionScreen to attach via OnMissionScreenTick");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v78 CampEntryBanner pre-init fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            // v79: OnMissionScreenTick never fires when MissionView is added via
            // Mission.AddMissionBehavior (vs MissionScreen.AddMissionView). Use
            // the base MissionBehavior.OnMissionTick which DOES fire, and grab
            // the active MissionScreen from ScreenManager.TopScreen instead of
            // the protected `MissionScreen` property (which stays null in this
            // registration path).
            if (!_layerAddedToScreen && _layer != null)
            {
                try
                {
                    var screen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen
                        as TaleWorlds.MountAndBlade.View.Screens.MissionScreen;
                    if (screen != null)
                    {
                        screen.AddLayer(_layer);
                        _missionScreen = screen;
                        _layerAddedToScreen = true;
                        HideoutPeacefulVisitState.Log("v79: CampEntryBanner layer attached via ScreenManager.TopScreen for "
                            + _campName);
                    }
                }
                catch (Exception ex)
                {
                    HideoutPeacefulVisitState.Log("v79 CampEntryBanner addlayer fail: "
                        + ex.GetType().Name + ": " + ex.Message);
                    _layerAddedToScreen = true;
                }
            }

            if (_fadedOut || _vm == null) return;
            _elapsed += dt;
            if (_elapsed >= kDisplaySeconds)
            {
                _vm.IsVisible = false;
                _fadedOut = true;
                HideoutPeacefulVisitState.Log("v79: CampEntryBanner hidden after "
                    + kDisplaySeconds + "s");
            }
        }

        public override void OnMissionScreenFinalize()
        {
            try
            {
                if (_layer != null && _missionScreen != null)
                    _missionScreen.RemoveLayer(_layer);
                _layer = null;
                _vm = null;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v77 CampEntryBanner finalize fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            base.OnMissionScreenFinalize();
        }
    }
}
