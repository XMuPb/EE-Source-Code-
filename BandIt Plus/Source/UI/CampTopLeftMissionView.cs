using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.MountAndBlade.View.MissionViews;
using BandItPlus.HideoutVisit;

namespace BandItPlus.UI
{
    // Wave 4.12-fix v93 (2026-05-13): Top-left composite MissionView.
    // Hosts the active quest tracker + quick-action bar.
    // Mirrors CampInfoPanelMissionView attach pattern (ScreenManager.TopScreen
    // via OnMissionTick because the MissionScreen property stays null when added
    // via AddMissionBehavior).
    public class CampTopLeftMissionView : MissionView
    {
        private GauntletLayer _layer;
        private CampTopLeftVM _vm;
        private bool _layerAddedToScreen;
        private TaleWorlds.MountAndBlade.View.Screens.MissionScreen _missionScreen;
        private float _tickAccum;

        private readonly string _cultureId;

        // Animation state — same easing curve as the main info panel.
        private float _elapsedSinceAttach;
        private bool _loggedAnimStart;
        private const float FadeInSeconds = 1.5f;
        private const float SlideInSeconds = 0.6f;
        private const float SlideStartX = -360f;
        private const float SlideEndX = 20f;
        private const float PulseMid = 0.92f;
        private const float PulseAmp = 0.08f;
        private const float PulseFreq = 2.0f;

        public CampTopLeftMissionView(Settlement settlement)
        {
            _cultureId = settlement?.Culture?.StringId ?? "";
        }

        public override void OnMissionScreenInitialize()
        {
            base.OnMissionScreenInitialize();
            try
            {
                _vm = new CampTopLeftVM();
                RefreshVM();
                _layer = new GauntletLayer(name: "CampTopLeft", localOrder: 141, shouldClear: false);
                _layer.LoadMovie("CampTopLeft", _vm);
                // v93: input restrictions so the buttons receive mouse clicks.
                // Pattern from EditableEncyclopedia\GlobalChroniclePanel.cs:1243 —
                // non-focus layer + SetInputRestrictions(false) lets buttons receive
                // clicks without stealing global input focus from the mission.
                _layer.IsFocusLayer = false;
                _layer.InputRestrictions.SetInputRestrictions(false);
                HideoutPeacefulVisitState.Log("v93: CampTopLeft prebuilt for culture " + _cultureId);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v93 CampTopLeft pre-init fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
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
                        _elapsedSinceAttach = 0f;
                        HideoutPeacefulVisitState.Log("v93: CampTopLeft layer attached");
                    }
                }
                catch (Exception ex)
                {
                    HideoutPeacefulVisitState.Log("v93 CampTopLeft addlayer fail: "
                        + ex.GetType().Name + ": " + ex.Message);
                    _layerAddedToScreen = true;
                }
            }

            // Cinematic slide-in + fade-in + pulse.
            if (_layerAddedToScreen && _vm != null)
            {
                _elapsedSinceAttach += dt;
                float slideT = _elapsedSinceAttach >= SlideInSeconds
                    ? 1f
                    : _elapsedSinceAttach / SlideInSeconds;
                float slideEased = 1f - (1f - slideT) * (1f - slideT) * (1f - slideT);
                _vm.TopLeftLeftMargin = SlideStartX + slideEased * (SlideEndX - SlideStartX);

                float fadeIn = _elapsedSinceAttach >= FadeInSeconds
                    ? 1f
                    : _elapsedSinceAttach / FadeInSeconds;
                float pulse = PulseMid + PulseAmp * (float)System.Math.Sin(_elapsedSinceAttach * PulseFreq);
                _vm.TopLeftAlpha = fadeIn * pulse;

                if (!_loggedAnimStart && _elapsedSinceAttach > 0.05f)
                {
                    _loggedAnimStart = true;
                    HideoutPeacefulVisitState.Log(
                        "v93 anim t=" + _elapsedSinceAttach.ToString("F2") +
                        " alpha=" + _vm.TopLeftAlpha.ToString("F2") +
                        " left=" + _vm.TopLeftLeftMargin.ToString("F1"));
                }
            }

            _tickAccum += dt;
            if (_tickAccum >= 1.0f)
            {
                _tickAccum = 0f;
                if (_vm != null) RefreshVM();
            }
        }

        public override void OnMissionScreenFinalize()
        {
            try
            {
                if (_layer != null)
                {
                    var screen = _missionScreen ?? TaleWorlds.ScreenSystem.ScreenManager.TopScreen
                        as TaleWorlds.MountAndBlade.View.Screens.MissionScreen;
                    if (screen != null) screen.RemoveLayer(_layer);
                    _layer = null;
                }
                _vm = null;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v93 CampTopLeft finalize fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            base.OnMissionScreenFinalize();
        }

        private void RefreshVM()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                if (bdm == null) return;

                int foodTier = bdm.GetFoodVendorQuestTier(_cultureId);
                int gearTier = bdm.GetGearVendorQuestTier(_cultureId);

                _vm.IsFoodQuestActive = foodTier > 0;
                _vm.IsGearQuestActive = gearTier > 0;
                _vm.HasNoQuestsActive = foodTier == 0 && gearTier == 0;

                if (_vm.IsFoodQuestActive)
                {
                    _vm.FoodQuestLine = new TaleWorlds.Localization.TextObject("{=bp_hcc_020}Food quest — Tier {TIER} in progress").SetTextVariable("TIER", foodTier).ToString();
                    _vm.FoodTierIcon = TierIcon(foodTier);
                }

                if (_vm.IsGearQuestActive)
                {
                    _vm.GearQuestLine = new TaleWorlds.Localization.TextObject("{=bp_hcc_021}Gear quest — Tier {TIER} in progress").SetTextVariable("TIER", gearTier).ToString();
                    _vm.GearTierIcon = TierIcon(gearTier);
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v93 CampTopLeft refresh fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Vanilla troop tier icon set — perfect reuse for quest tier display
        // since both convey "1, 2, 3" progression with bronze/silver/gold visuals.
        private static string TierIcon(int tier)
        {
            if (tier <= 1) return "General\\TroopTierIcons\\icon_tier_1";
            if (tier == 2) return "General\\TroopTierIcons\\icon_tier_2";
            return "General\\TroopTierIcons\\icon_tier_3";
        }
    }
}
