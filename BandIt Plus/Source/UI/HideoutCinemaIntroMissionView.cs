using System;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.MountAndBlade.View.MissionViews;
using BandItPlus.HideoutVisit;

namespace BandItPlus.UI
{
    // v123 (2026-05-15): MissionView owning the first-visit cinematic intro
    // GauntletLayer. Shows a modal narrative card (HideoutCinemaIntro.xml) with
    // the culture's authored narration; the Continue button removes the layer.
    //
    // localOrder 200 places it above CampInfoPanel (140) and CampEntryBanner
    // (150) — it's a modal card meant to sit on top of the whole scene UI.
    // Layer-removal uses the v122 cached-MissionScreen pattern (re-querying
    // TopScreen at finalize returns null and leaks the layer).
    public class HideoutCinemaIntroMissionView : MissionView
    {
        private GauntletLayer _layer;
        private HideoutCinemaIntroVM _vm;
        private bool _layerAddedToScreen;
        private bool _dismissed;
        private bool _missionPaused;
        private float _shownElapsed;
        private float _animT;
        private TaleWorlds.Engine.SoundEvent _ambientSound;
        private TaleWorlds.MountAndBlade.View.Screens.MissionScreen _missionScreen;
        private readonly string _narrationText;
        private readonly string _titleText;

        public HideoutCinemaIntroMissionView(string narrationText, string titleText)
        {
            _narrationText = narrationText ?? "";
            _titleText = titleText ?? "";
        }

        public override void OnMissionScreenInitialize()
        {
            base.OnMissionScreenInitialize();
            try
            {
                _vm = new HideoutCinemaIntroVM(_narrationText, _titleText, OnContinue);
                _layer = new GauntletLayer(name: "HideoutCinemaIntro", localOrder: 200, shouldClear: true);
                _layer.LoadMovie("HideoutCinemaIntro", _vm);
                // v124: input is claimed AFTER the layer is attached (in
                // OnMissionTick) — claiming it here, pre-attach, did not stick
                // and the Continue button received no clicks.
                HideoutPeacefulVisitState.Log("v124: HideoutCinemaIntro prebuilt");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v123 HideoutCinemaIntro init fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnContinue()
        {
            RemoveLayerNow();
        }

        // v138: MUST be OnMissionTick. v137 moved everything to OnMissionScreenTick
        // (hoping to survive the engine pause) but that method is NOT called for a
        // MissionView added via AddMissionBehavior — the attach block never ran and
        // the card never loaded (log: "prebuilt" with no "attached"). OnMissionTick
        // IS called. Trade-off: if the engine pause halts OnMissionTick, the
        // click-anywhere poll and pulse freeze once paused — but the Continue
        // ButtonWidget still dismisses, since UI input survives the pause.
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
                        _missionScreen = screen;   // v122 pattern: cache for removal
                        // v128: claim input only — SetInputRestrictions(false) IS
                        // the "claim" call (see CampInfoPanelMissionView v104.5).
                        // v126's IsFocusLayer=true was a wrong guess: CampInfoPanel's
                        // working ButtonWidgets never set it. The real Continue-button
                        // fix is DoNotPassEventsToChildren in the prefab (the child
                        // brown-fill Widget was eating the click).
                        try
                        {
                            _layer.InputRestrictions.SetInputRestrictions(false);
                            _layer.IsFocusLayer = false;
                        }
                        catch { }
                        _layerAddedToScreen = true;
                        // v2 (2026-05-25): signal so BanditCampPanel hides while we're up
                        HideoutPeacefulVisitState.CinematicActive = true;
                        StartAmbientSound();
                        HideoutPeacefulVisitState.Log("v140: HideoutCinemaIntro attached + input claimed");
                    }
                }
                catch (Exception ex)
                {
                    HideoutPeacefulVisitState.Log("v126 HideoutCinemaIntro addlayer fail: "
                        + ex.GetType().Name + ": " + ex.Message);
                    _layerAddedToScreen = true;
                }
            }

            // Click-anywhere dismiss: poll LeftMouseButton/Escape so "Press Left
            // Click to Continue" works everywhere, not only on the button. Runs
            // in OnMissionTick — works until the engine pauses; after that the
            // Continue ButtonWidget is the dismiss path. The 0.6s guard stops a
            // stray click from the loading screen instantly closing the card.
            if (_layerAddedToScreen && !_dismissed)
            {
                _shownElapsed += dt;
                if (_shownElapsed > 0.6f)
                {
                    bool dismiss =
                        TaleWorlds.InputSystem.Input.IsKeyPressed(TaleWorlds.InputSystem.InputKey.LeftMouseButton)
                        || TaleWorlds.InputSystem.Input.IsKeyPressed(TaleWorlds.InputSystem.InputKey.Escape);
                    if (dismiss)
                    {
                        HideoutPeacefulVisitState.Log("v126: HideoutCinemaIntro dismissed via input poll");
                        OnContinue();
                    }
                }
            }

            // v143: one master clock drives the whole entrance.
            //  - CardAlpha   : slow 0.8s fade-up of the card shell.
            //  - HeaderAlpha / RuleAlpha / TextAlpha : staggered reveal — header,
            //    then the gold rules, then the narration build in sequence.
            //  - GoldShimmer : continuous slow sine — gold dividers + title glow.
            //  - ButtonPulse : the Continue button's blinking-gold pulse.
            // Pause is deferred until the reveal finishes (TextAlpha == 1):
            // OnMissionTick drives these ramps, so pausing first would freeze
            // them mid-reveal.
            if (_layerAddedToScreen && !_dismissed && _vm != null)
            {
                _animT += dt;
                _vm.CardAlpha   = Clamp01(_animT / 0.8f);
                _vm.HeaderAlpha = Clamp01((_animT - 0.5f) / 0.4f);
                _vm.RuleAlpha   = Clamp01((_animT - 0.9f) / 0.4f);
                _vm.TextAlpha   = Clamp01((_animT - 1.3f) / 0.5f);
                _vm.GoldShimmer = 1.1f + 0.5f * (float)Math.Sin(_animT * 2.5);
                _vm.ButtonPulse = 1.1f + 0.7f * (float)Math.Sin(_animT * 3.5);
                if (_vm.TextAlpha >= 1f) SetMissionPaused(true);
            }
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        // Removes the layer and releases the input claim. Idempotent — safe to
        // call from both OnContinue and OnMissionScreenFinalize. Releasing the
        // input restriction is mandatory (see feedback_ee_input_blocker_pattern).
        private void RemoveLayerNow()
        {
            if (_dismissed) return;
            _dismissed = true;
            // v2 (2026-05-25): clear flag so BanditCampPanel can slide in.
            HideoutPeacefulVisitState.CinematicActive = false;
            // v135: unpause the mission before tearing the layer down, so the
            // game always resumes even if layer removal below throws.
            SetMissionPaused(false);
            StopAmbientSound();
            try
            {
                if (_layer != null)
                {
                    try { _layer.InputRestrictions.SetInputRestrictions(true); } catch { }
                    try { _layer.IsFocusLayer = false; } catch { }
                    var screen = _missionScreen
                        ?? (TaleWorlds.ScreenSystem.ScreenManager.TopScreen
                            as TaleWorlds.MountAndBlade.View.Screens.MissionScreen);
                    if (screen != null) screen.RemoveLayer(_layer);
                    _layer = null;
                }
                _missionScreen = null;
                _vm = null;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v123 HideoutCinemaIntro remove fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // v139: pause via Mission.PauseAITick — NOT MBCommon.PauseGameEngine().
        // The engine pause was a HARD freeze: it also froze UI input and halted
        // OnMissionTick, so the card became un-dismissable (softlock — v138 log
        // ended at PAUSED with no dismiss) and the button pulse stopped dead.
        // PauseAITick freezes the NPCs (the visible motion in a hideout) while
        // leaving the mission ticking and the UI alive — the card stays
        // interactive and animated. It is a partial pause (particles/player
        // still move): the safe, correct trade for an interactive modal.
        private void SetMissionPaused(bool paused)
        {
            if (paused == _missionPaused) return;
            try
            {
                var mission = Mission;
                if (mission == null) { HideoutPeacefulVisitState.Log("v139 cinema pause: Mission null"); return; }
                mission.PauseAITick = paused;
                _missionPaused = paused;
                HideoutPeacefulVisitState.Log("v139 cinema AI " + (paused ? "PAUSED" : "UNPAUSED"));
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v139 cinema pause(" + paused + ") fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // v141: looping forest-wilderness ambience while the card is shown
        // (event:/mission/ambient/area/forest — user choice over campfire).
        // Vanilla ambient events are 3D emitters (max_distance), so the event is
        // positioned at the player or it plays inaudibly at the scene origin.
        // There is no SoundEvent.Stop() — Pause()+Release() is the teardown.
        // CreateEventFromString returning a null-sound-event (bad path) is
        // handled, not crashed. NOTE: a forest bed plays even at non-forest
        // hideouts (desert/steppe/sea) — accepted trade per user choice.
        private void StartAmbientSound()
        {
            try
            {
                var scene = Mission?.Scene;
                if (scene == null) { HideoutPeacefulVisitState.Log("v141 ambient: no scene"); return; }
                _ambientSound = TaleWorlds.Engine.SoundEvent.CreateEventFromString(
                    "event:/mission/ambient/area/forest", scene);
                if (_ambientSound == null || _ambientSound.IsNullSoundEvent())
                {
                    HideoutPeacefulVisitState.Log("v141 ambient: CreateEventFromString gave null event");
                    _ambientSound = null;
                    return;
                }
                var agent = Mission?.MainAgent;
                if (agent != null) _ambientSound.SetPosition(agent.Position);
                _ambientSound.Play();
                HideoutPeacefulVisitState.Log("v141 forest ambient started");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v141 ambient start fail: "
                    + ex.GetType().Name + ": " + ex.Message);
                _ambientSound = null;
            }
        }

        private void StopAmbientSound()
        {
            if (_ambientSound == null) return;
            try { _ambientSound.Pause(); } catch { }
            try { _ambientSound.Release(); } catch { }
            _ambientSound = null;
            HideoutPeacefulVisitState.Log("v141 forest ambient stopped");
        }

        public override void OnMissionScreenFinalize()
        {
            RemoveLayerNow();
            base.OnMissionScreenFinalize();
        }
    }
}
