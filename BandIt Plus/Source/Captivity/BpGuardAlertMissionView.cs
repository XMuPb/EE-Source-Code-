using System;
using TaleWorlds.Engine;                          // Screen (RealScreenResolution*)
using SandBox;                                   // CampaignAgentComponent
using SandBox.Missions.AgentBehaviors;          // AlarmedBehaviorGroup
using TaleWorlds.Engine.GauntletUI;              // GauntletLayer
using TaleWorlds.Library;                         // Vec3, MathF
using TaleWorlds.MountAndBlade;                   // Agent, Mission, MBWindowManager
using TaleWorlds.MountAndBlade.View.MissionViews; // MissionView
using TaleWorlds.MountAndBlade.View.Screens;      // MissionScreen (CombatCamera)
using BandItPlus.UI;                              // GuardAlertVM

namespace BandItPlus.Captivity
{
    // Wave 4.39 — over-guard awareness markers. A MissionView (so we get base.MissionScreen + a per-frame
    // screen tick AFTER the camera update). Each frame it projects every aware enemy guard's head to screen
    // space via MBWindowManager.WorldToScreen and shows "?" (suspicious) / "!" (spotted) driven by the real
    // AlarmedBehaviorGroup.AlarmFactor. Pixels -> reference units via UIContext.InverseScale.
    public class BpGuardAlertMissionView : MissionView
    {
        private const int Slots = 6;
        private GauntletLayer _layer;
        private GuardAlertVM _vm;
        private float _barkClock;
        private int _barkLine;
        private static readonly string[] SuspiciousBarks = { BandItPlus.Localization.Get("bp_guardalertmissionview_001", "What was that?"), BandItPlus.Localization.Get("bp_guardalertmissionview_002", "Who's there?"), BandItPlus.Localization.Get("bp_guardalertmissionview_003", "Did you hear that?"), BandItPlus.Localization.Get("bp_guardalertmissionview_004", "Show yourself…"), BandItPlus.Localization.Get("bp_guardalertmissionview_005", "Someone's about.") };
        private static readonly string[] AlertBarks = { BandItPlus.Localization.Get("bp_guardalertmissionview_006", "Here they are!"), BandItPlus.Localization.Get("bp_guardalertmissionview_007", "They're trying to escape!"), BandItPlus.Localization.Get("bp_guardalertmissionview_008", "Ring the bell!"), BandItPlus.Localization.Get("bp_guardalertmissionview_009", "Sound the alarm!"), BandItPlus.Localization.Get("bp_guardalertmissionview_010", "Stop them!") };

        public override void OnMissionScreenInitialize()
        {
            base.OnMissionScreenInitialize();
            try
            {
                _vm = new GuardAlertVM();
                _layer = new GauntletLayer("BpGuardAlert", 1, false);
                _layer.LoadMovie("GuardAlertHud", _vm);
                MissionScreen.AddLayer(_layer);
                BpJailbreakMissionController.Log("guard-alert view ready");
            }
            catch (Exception ex) { BpJailbreakMissionController.Log("guard-alert init fail: " + ex.Message); }
        }

        public override void OnMissionScreenFinalize()
        {
            try { if (_layer != null && MissionScreen != null) MissionScreen.RemoveLayer(_layer); } catch { }
            _layer = null; _vm = null;
            base.OnMissionScreenFinalize();
        }

        public override void OnMissionScreenTick(float dt)
        {
            base.OnMissionScreenTick(dt);
            if (_vm == null || _layer == null || MissionScreen == null || Mission == null) return;

            float inv = 1f;
            try { inv = _layer.UIContext.InverseScale; } catch { }

            int slot = 0;
            float barkAlarm = 0f, barkX = 0f, barkY = 0f; bool barkSet = false;   // the most-suspicious guard gets the "What's that noise?" bark
            var agents = Mission.Agents;
            for (int i = 0; i < agents.Count && slot < Slots; i++)
            {
                var a = agents[i];
                if (a == null || !a.IsActive() || !a.IsHuman || a == Agent.Main) continue;
                if (Mission.PlayerEnemyTeam == null || a.Team != Mission.PlayerEnemyTeam) continue;

                float alarm = GetAlarm(a);
                if (alarm < 0.12f) continue;   // unaware -> no marker

                Vec3 e = a.GetEyeGlobalPosition();
                var head = new Vec3(e.x, e.y, e.z + 0.55f, -1f);   // a little clearance above the head
                float x = 0f, y = 0f, w = 0f;
                MBWindowManager.WorldToScreen(MissionScreen.CombatCamera, head, ref x, ref y, ref w);
                if (w < 0f || !MathF.IsValidValue(x) || !MathF.IsValidValue(y)) continue;   // behind camera / invalid

                _vm.SetSlot(slot, x * inv - 22f, y * inv - 46f, alarm);   // center the 44px marker, float it above
                if (alarm > barkAlarm) { barkAlarm = alarm; barkX = x * inv; barkY = y * inv; barkSet = true; }   // loudest-aware guard gets the bark
                slot++;
            }
            for (; slot < Slots; slot++) _vm.HideSlot(slot);
            if (barkSet)
            {
                _barkClock += dt;
                if (_barkClock >= 2.8f) { _barkClock = 0f; _barkLine++; }   // rotate the line every ~2.8s so it doesn't spam one phrase
                string[] set = barkAlarm >= 0.6f ? AlertBarks : SuspiciousBarks;   // spotted -> alert lines; suspicious -> "what was that?"
                _vm.BarkText = set[_barkLine % set.Length];
                _vm.BarkX = barkX - 100f; _vm.BarkY = barkY; _vm.BarkShow = true;
            }
            else { _vm.BarkShow = false; _barkClock = 0f; }

            // exit compass marker — project the escape door; clamp to the screen edge when it's off-screen/behind.
            var exit = new Vec3(152f, 235.9f, 6.99f, -1f);
            float ex = 0f, ey = 0f, ew = 0f;
            MBWindowManager.WorldToScreen(MissionScreen.CombatCamera, exit, ref ex, ref ey, ref ew);
            if (!MathF.IsValidValue(ex) || !MathF.IsValidValue(ey)) { _vm.ExitShow = false; }
            else
            {
                float rw = Screen.RealScreenResolutionWidth;
                float rh = Screen.RealScreenResolutionHeight;
                if (ew < 0f) { ex = rw - ex; ey = rh; }   // behind the camera -> mirror x, pin to the bottom edge
                const float m = 50f;
                ex = Math.Max(m, Math.Min(rw - m, ex));
                ey = Math.Max(m, Math.Min(rh - m, ey));
                _vm.ExitX = ex * inv - 40f;
                _vm.ExitY = ey * inv - 20f;
                _vm.ExitShow = true;
            }

            // follower companion beacon — a gold marker over the recruited cellmate's head.
            var mate = BpJailbreakMissionController.CurrentCellmate;
            if (BpJailbreakMissionController.CellmateFollowing && mate != null && mate.IsActive())
            {
                Vec3 me2 = mate.GetEyeGlobalPosition();
                var mh = new Vec3(me2.x, me2.y, me2.z + 0.62f, -1f);
                float fx = 0f, fy = 0f, fw = 0f;
                MBWindowManager.WorldToScreen(MissionScreen.CombatCamera, mh, ref fx, ref fy, ref fw);
                if (fw >= 0f && MathF.IsValidValue(fx) && MathF.IsValidValue(fy))
                {
                    _vm.FollowerX = fx * inv - 21f;
                    _vm.FollowerY = fy * inv - 44f;
                    _vm.FollowerShow = true;
                }
                else _vm.FollowerShow = false;
            }
            else _vm.FollowerShow = false;
        }

        private static float GetAlarm(Agent a)
        {
            try
            {
                var grp = a.GetComponent<CampaignAgentComponent>()?.AgentNavigator?.GetBehaviorGroup<AlarmedBehaviorGroup>();
                return grp != null ? grp.AlarmFactor : 0f;
            }
            catch { return 0f; }
        }
    }
}
