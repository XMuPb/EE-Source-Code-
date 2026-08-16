using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.MountAndBlade.View.Screens; // MissionScreen (alarm meter layer)
using BandItPlus.HideoutVisit;

namespace BandItPlus.UI
{
    // v176 (2026-05-18): MissionView for the redesigned Hideout Assault panel —
    // a persistent crimson in-battle HUD. At ~3s it decides whether this is a
    // hideout assault with bandits, resolves every value once, builds the VM +
    // a GauntletLayer, fades the panel in, and keeps it for the WHOLE battle.
    // The panel is removed only at OnMissionScreenFinalize (win / defeat /
    // retreat all finalize the mission screen). Live updates + the 4 animation
    // systems are Plan B (v177).
    public class HideoutAssaultPanelMissionView : MissionView
    {
        private const float kDecideAt = 3f;  // wait for agents to spawn
        private const float kFadeIn = 0.6f;

        private GauntletLayer _layer;
        private HideoutAssaultPanelVM _vm;
        private float _elapsed;
        private bool _decided;
        private bool _layerAddedToScreen;
        private float _syncT;
        private int _initialFoes;   // live-pip baseline: defenders alive at first sync
        private int _initialPips;   // live-pip baseline: pre-battle difficulty level

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            _elapsed += dt;

            if (!_decided && _elapsed >= kDecideAt)
            {
                _decided = true;
                try
                {
                    if (IsHideoutAssaultBattle()
                        && (MCMSettings.Instance == null || MCMSettings.Instance.ShowChiefCard))
                    {
                        _vm = BuildVM();
                        _layer = new GauntletLayer(name: "HideoutAssaultPanel", localOrder: 145, shouldClear: false);
                        _layer.LoadMovie("HideoutBrief", _vm);
                        HideoutPeacefulVisitState.Log("chief reveal card built — "
                            + _vm.HideoutName + " / " + _vm.BossName);
                    }
                }
                catch (Exception ex)
                {
                    HideoutPeacefulVisitState.Log("v176 HideoutAssaultPanel build fail: "
                        + ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (_layer != null && !_layerAddedToScreen)
            {
                try
                {
                    var screen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen
                        as TaleWorlds.MountAndBlade.View.Screens.MissionScreen;
                    if (screen != null)
                    {
                        screen.AddLayer(_layer);
                        _layerAddedToScreen = true;
                    }
                }
                catch { _layerAddedToScreen = true; }
            }

            // cascade entrance: root eases in, then header -> taunt -> stats ->
            // footer stagger in with a small downward drift. the card then stays
            // for the whole battle, its numbers live-synced below.
            if (_vm != null)
            {
                float t = _elapsed - kDecideAt;
                float a = t < kFadeIn ? t / kFadeIn : 1f;
                _vm.PanelAlpha = a < 0f ? 0f : (a > 1f ? 1f : a);

                _vm.HeadAlpha = CascadeEase(t, 0.00f, out float hd); _vm.HeadDriftY = hd;
                _vm.TauntAlpha = CascadeEase(t, 0.30f, out float td); _vm.TauntDriftY = td;
                _vm.StatsAlpha = CascadeEase(t, 0.60f, out float sd); _vm.StatsDriftY = sd;
                _vm.FootAlpha = CascadeEase(t, 0.90f, out float fd); _vm.FootDriftY = fd;

                // live sync (1 Hz): the counts breathe with the battle — defenders
                // fall as they die, jump when the alarm wave lands, odds re-read.
                _syncT += dt;
                if (_syncT >= 1f)
                {
                    _syncT = 0f;
                    try { SyncLiveCounts(); }
                    catch (Exception sx) { HideoutPeacefulVisitState.Log("card live sync fail: " + sx.Message); }
                }
            }
        }

        private static float CascadeEase(float t, float start, out float drift)
        {
            float e = (t - start) / 0.5f;
            e = e < 0f ? 0f : (e > 1f ? 1f : e);
            e = e * e * (3f - 2f * e); // smoothstep
            drift = -12f * (1f - e);
            return e;
        }

        private void SyncLiveCounts()
        {
            var mission = Mission.Current;
            if (mission == null || _vm == null || mission.PlayerEnemyTeam == null || mission.PlayerTeam == null) return;
            int foes = 0, friends = 0, melee = 0, ranged = 0, cav = 0;
            foreach (var a in mission.Agents)
            {
                if (a == null || !a.IsActive() || !a.IsHuman) continue;
                if (a.Team == mission.PlayerEnemyTeam)
                {
                    foes++;
                    if (a.Character is CharacterObject co)
                    {
                        if (co.IsMounted) cav++;
                        else if (co.IsRanged) ranged++;
                        else melee++;
                    }
                    else melee++;
                }
                else if (a.Team == mission.PlayerTeam) friends++;
            }
            _vm.DefenderCount = foes.ToString();
            _vm.YourForces = friends.ToString();
            _vm.CompositionValue = melee + "·" + ranged + "·" + cav + " M·R·C";
            _vm.OddsText = friends <= 0 ? BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_001", "Grim")
                : foes > friends * 1.25f ? BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_002", "Outnumbered")
                : friends > foes * 1.25f ? BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_003", "Favored")
                : BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_004", "Even");

            // Sneak-in variant: the ODDS slot reads RISK — proximity-based, not
            // force-ratio (you're not fielding an army, you're crouching in one).
            if (BpSneakShadowController.IsStealthVariant())
            {
                int near = 0;
                var me = Agent.Main;
                if (me != null && me.IsActive())
                {
                    foreach (var a in mission.Agents)
                    {
                        if (a == null || !a.IsActive() || !a.IsHuman || a.Team != mission.PlayerEnemyTeam) continue;
                        if (a.Position.Distance(me.Position) < 30f) near++;
                    }
                }
                _vm.OddsText = near <= 1 ? BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_005", "Low") : near <= 3 ? BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_006", "Wary") : BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_007", "High");
            }

            // live pips: the opening assessment is the baseline; the meter drains
            // as the camp thins out and surges when the alarm wave lands.
            if (_initialFoes <= 0 && foes > 0)
            {
                _initialFoes = foes;
                _initialPips = (_vm.Pip1On ? 1 : 0) + (_vm.Pip2On ? 1 : 0) + (_vm.Pip3On ? 1 : 0)
                             + (_vm.Pip4On ? 1 : 0) + (_vm.Pip5On ? 1 : 0);
                if (_initialPips <= 0) _initialPips = 1;
            }
            if (_initialFoes > 0)
            {
                int level = foes <= 0 ? 0
                    : (int)Math.Ceiling(_initialPips * (double)foes / _initialFoes);
                if (level > 5) level = 5;
                _vm.SetDifficulty(level);
            }
        }

        public override void OnMissionScreenFinalize()
        {
            try
            {
                if (_layer != null && MissionScreen != null)
                    MissionScreen.RemoveLayer(_layer);
                _layer = null;
                _vm = null;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v176 HideoutAssaultPanel finalize fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            base.OnMissionScreenFinalize();
        }

        // True when the current mission is a hideout assault with bandits.
        private static bool IsHideoutAssaultBattle()
        {
            var mission = Mission.Current;
            if (mission == null) return false;
            if (MobileParty.MainParty?.MapEvent?.IsHideoutBattle != true) return false;

            int bandit = 0, total = 0;
            foreach (var agent in mission.Agents)
            {
                if (agent == null || !agent.IsHuman) continue;
                total++;
                if (agent.Character is CharacterObject co && co.Occupation == Occupation.Bandit)
                    bandit++;
                if (total >= 60) break;
            }
            return bandit > 0;
        }

        // Resolves every panel value once and returns a fully-populated VM.
        private static HideoutAssaultPanelVM BuildVM()
        {
            var vm = new HideoutAssaultPanelVM
            {
                HideoutName = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_008", "Bandit Hideout"),
                BossName = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_009", "The Hideout's Chief"),
                CultureTrustLine = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_010", "Bandits"),
                TauntText = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_011", "\"You should not have come here.\""),
                DefenderCount = "—",
                YourForces = "—",
                OddsText = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_012", "Unknown"),
                GarrisonValue = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_013", "Active"),
                DefenderTier = "—",
                TacticsValue = "—",
                SpoilsText = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_014", "Spoils await the victor"),
            };

            // --- counts from live agents ---
            int defenders = 0, yours = 0;
            long tierSum = 0; int tierN = 0;
            var mission = Mission.Current;
            if (mission != null)
            {
                foreach (var agent in mission.Agents)
                {
                    if (agent == null || !agent.IsHuman) continue;
                    bool isBandit = agent.Character is CharacterObject co && co.Occupation == Occupation.Bandit;
                    if (isBandit)
                    {
                        defenders++;
                        if (agent.Character is CharacterObject bc) { tierSum += bc.Tier; tierN++; }
                    }
                    else if (agent.Team != null && agent.Team.IsPlayerTeam)
                    {
                        yours++;
                    }
                }
            }
            vm.DefenderCount = defenders > 0 ? defenders.ToString() : "—";
            vm.YourForces = yours > 0 ? yours.ToString() : "—";
            vm.DefenderTier = tierN > 0 ? new TaleWorlds.Localization.TextObject("{=bp_hch_005}Tier {N}").SetTextVariable("N", (int)Math.Round((double)tierSum / tierN)).ToString() : "—";

            // --- odds + difficulty ---
            double ratio = yours > 0 ? (double)defenders / yours : 2.0;
            vm.OddsText = ratio < 0.85 ? BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_015", "Favorable") : (ratio < 1.25 ? BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_004", "Even") : BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_002", "Outnumbered"));

            double defenseFactor = 1.0;
            try { defenseFactor = BandItPlus.BanditPower.HideoutDefenseBehavior.GetCurrentDefenseFactor(); }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v176 defenseFactor read fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            double score = ratio * defenseFactor;
            int diff = score < 0.8 ? 1 : (score < 1.1 ? 2 : (score < 1.5 ? 3 : (score < 2.2 ? 4 : 5)));
            vm.SetDifficulty(diff);

            // --- garrison % from the v169 defense factor ---
            int garrisonPct = (int)Math.Round((defenseFactor - 1.0) * 100.0);
            vm.GarrisonValue = garrisonPct > 0 ? "+" + garrisonPct + "%" : BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_013", "Active");

            // --- tactics ---
            // 2026-07-28: this used to read BanditBattleTacticsBehavior.TacticOptionCount
            // and render "N options". That behaviour moved to the separate BrainPlus
            // module along with all other combat-AI code, and BandItPlus no longer
            // manages tactics at all — so a count here would be reporting on something
            // this mod does not do. Falls back to the same localized "Active" string the
            // old catch-block already used, keeping the row meaningful without claiming
            // ownership of tactics.
            vm.TacticsValue = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_013", "Active");

            // --- spoils estimate from v168 ---
            try
            {
                int gold = BandItPlus.BanditPower.BanditSpoils.EstimateSpoils(defenders);
                vm.SpoilsText = gold > 0
                    ? new TaleWorlds.Localization.TextObject("{=bp_hch_006}Spoils: ~{GOLD} denars + keepsake").SetTextVariable("GOLD", gold).ToString()
                    : BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_014", "Spoils await the victor");
            }
            catch (Exception ex)
            {
                vm.SpoilsText = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_014", "Spoils await the victor");
                HideoutPeacefulVisitState.Log("v176 spoils estimate read fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            // --- hideout / boss / culture / trust / taunt / banner ---
            try
            {
                Settlement settlement = MobileParty.MainParty?.MapEvent?.MapEventSettlement;
                if (settlement != null)
                {
                    string sName = settlement.Name?.ToString();
                    if (!string.IsNullOrEmpty(sName)) vm.HideoutName = sName;

                    string cultureId = settlement.Culture?.StringId;
                    string cultureName = settlement.Culture?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_010", "Bandits");

                    var chief = Campaign.Current?
                        .GetCampaignBehavior<BandItPlus.Heroes.BanditChiefRegistry>()?
                        .GetChief(cultureId);
                    string chiefName = chief?.Name?.ToString();
                    if (!string.IsNullOrEmpty(chiefName)) vm.BossName = chiefName;

                    // trust tier with this culture
                    string trustLabel = null;
                    try
                    {
                        var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                        if (bdm != null && !string.IsNullOrEmpty(cultureId))
                        {
                            int tier = bdm.GetCultureTrust(cultureId);
                            trustLabel = tier <= 0 ? BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_016", "Stranger") : "Tier " + tier;
                        }
                    }
                    catch (Exception tex)
                    {
                        trustLabel = null;
                        HideoutPeacefulVisitState.Log("v176 trust read fail: "
                            + tex.GetType().Name + ": " + tex.Message);
                    }
                    vm.CultureTrustLine = trustLabel != null
                        ? cultureName + " · Trust: " + trustLabel
                        : cultureName;

                    // taunt from the culture profile
                    try
                    {
                        if (!string.IsNullOrEmpty(cultureId)
                            && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId
                                .TryGetValue(cultureId, out var profile)
                            && profile != null
                            && !string.IsNullOrEmpty(profile.ThreatResponse))
                        {
                            vm.TauntText = "\"" + profile.ThreatResponse + "\"";
                        }
                    }
                    catch (Exception tex)
                    {
                        HideoutPeacefulVisitState.Log("v176 taunt read fail: "
                            + tex.GetType().Name + ": " + tex.Message);
                    }

                    // banner crest — the chief's clan-banner code string,
                    // rendered by the prefab's BannerTableauWidget (CampInfoPanel's
                    // proven in-mission pattern; v176's MaskedTextureWidget never
                    // painted — the v176-fix swapped the mechanism).
                    if (chief?.Clan?.Banner != null)
                    {
                        vm.BannerCode = chief.Clan.Banner.Serialize();
                        vm.HasBanner = !string.IsNullOrEmpty(vm.BannerCode);
                    }
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v176 HideoutAssaultPanel resolve fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            // chief-reveal extras: forces line, live composition mix, and the old
            // BANDIT AI panel compressed to one power line.
            try
            {
                vm.ForcesLine = vm.DefenderCount + "  vs  " + vm.YourForces;
                // the subline carries the culture only — the crimson "· Hideout
                // Assault" is a separate static widget on the card so it can be red.
                string ctl = vm.CultureTrustLine ?? "";
                int cut = ctl.IndexOf("· Trust", StringComparison.Ordinal);
                if (cut < 0) cut = ctl.IndexOf("Trust:", StringComparison.Ordinal);
                if (cut > 0) vm.CultureTrustLine = ctl.Substring(0, cut).TrimEnd();
            }
            catch { vm.ForcesLine = ""; }
            try
            {
                int melee = 0, ranged = 0, cav = 0;
                var msn = Mission.Current;
                if (msn != null && msn.PlayerEnemyTeam != null)
                    foreach (var a in msn.Agents)
                    {
                        if (a == null || !a.IsActive() || !a.IsHuman || a.Team != msn.PlayerEnemyTeam) continue;
                        if (a.Character is CharacterObject co)
                        {
                            if (co.IsMounted) cav++;
                            else if (co.IsRanged) ranged++;
                            else melee++;
                        }
                        else melee++;
                    }
                vm.CompositionValue = melee + "·" + ranged + "·" + cav + " M·R·C";
            }
            catch { vm.CompositionValue = ""; }
            try
            {
                float factor = BandItPlus.BanditPower.BanditPowerBehavior.PowerFactor();
                int tier = factor < 0.5f ? 1 : (factor < 1.0f ? 2 : (factor < 1.5f ? 3 : (factor < 2.0f ? 4 : 5)));
                string edge = factor < 0.5f ? BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_029", "Wary") : (factor < 1.0f ? BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_030", "Bold") : BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_031", "Ferocious"));
                vm.PowerLine = new TaleWorlds.Localization.TextObject("{=bp_hch_007}Bandit Power · Tier {TIER} — {EDGE}").SetTextVariable("TIER", tier).SetTextVariable("EDGE", edge).ToString();
            }
            catch { vm.PowerLine = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_017", "Bandit Power"); }

            // Sneak-in variant: same card, infiltration flavor (2026-07-03).
            if (BpSneakShadowController.IsStealthVariant())
            {
                vm.ModeTagText = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_018", "HIDEOUT INFILTRATION");
                vm.SublineTagText = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_019", "· Hideout Infiltration");
                vm.OddsLabel = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_020", "RISK");
            }

            return vm;
        }
    }

    // hideout raid ALARM, phase 1 — for the classic ASSAULT scene mission only.
    // the camp starts unaware; a suspicion meter (the AlarmMeter HUD from the
    // jailbreak) climbs while any bandit has the player or his troops in his
    // view cone. full detection — our meter hitting 1.0, a bandit natively
    // flipping to Alarmed (the engine's own vision), or a witnessed kill —
    // sounds the horn and wakes the WHOLE camp: every bandit goes Alarmed and
    // every enemy formation charges. the SNEAK-IN mission variant is left
    // alone: the engine's StealthFailCounter owns it (spotted = mission fail).
    // phase 2 adds alarm reinforcements + payoffs; the wave probe below logs
    // the vanilla controller's spawn methods (dev builds) for that wiring.
    // MCM HideoutStealthAlarm turns it off.
    public class BpHideoutAlarmController : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        // true between the alarm and the reinforcement wave LANDING; WaveAgents
        // holds the landed wave. HideoutPhaseHoldPatch postfixes the controller's
        // IsSideDepleted with KeepsBanditSideAlive() so the boss scene cannot
        // fire while the wave is inbound or any of its agents still stand — the
        // controller's own troop counter never sees agents it didn't spawn.
        public static volatile bool HoldFirstPhase;
        public static volatile bool AlarmRang;   // the chief-reveal card fades on this
        public static readonly List<Agent> WaveAgents = new List<Agent>();

        public static bool KeepsBanditSideAlive()
        {
            if (HoldFirstPhase) return true;
            var wa = WaveAgents;
            for (int i = wa.Count - 1; i >= 0; i--)
            {
                var a = wa[i];
                if (a == null || !a.IsActive()) wa.RemoveAt(i);
            }
            return wa.Count > 0;
        }

        private bool _checked;      // one-time mission gate done
        private bool _active;       // hideout assault + feature enabled
        private bool _alarmed;      // camp fully awake — job done
        private float _pollT;       // 4 Hz detection cadence
        private float _detection;   // 0..1 suspicion
        private float _pulseT;      // ALARMED heartbeat clock
        private GauntletLayer _reinLayer;        // countdown banner (BanditReinforcement.xml)
        private BanditReinforcementVM _reinVm;
        private bool _reinReleased;
        private bool _reinCleaned;
        private int _reinWant;
        private float _reinThreat01;
        private GauntletLayer _layer;
        private AlarmMeterVM _vm;
        private bool _layerAdded;
        private bool _probeLogged;

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            try
            {
                if (!_checked)
                {
                    // agents spawn a few ticks in — wait before gating
                    if (Mission.Current == null || Mission.Current.Agents.Count < 2) return;
                    _checked = true;
                    HoldFirstPhase = false; // fresh mission, never inherit a hold
                    AlarmRang = false;
                    WaveAgents.Clear();
                    bool on = true;
                    try { on = MCMSettings.Instance == null || MCMSettings.Instance.HideoutStealthAlarm; } catch { }
                    // this belongs to the classic ASSAULT scene (HideoutMissionController,
                    // bandits unaware, no fail state). the SNEAK-IN variant runs the
                    // engine's own stealth (StealthFailCounterMissionLogic — spotted =
                    // mission FAILED), so ours must stand down there and not fight it.
                    bool sceneMission = false, nativeStealth = false;
                    foreach (var mb in Mission.Current.MissionBehaviors)
                    {
                        if (mb == null) continue;
                        string n = mb.GetType().Name;
                        if (n == "HideoutMissionController") sceneMission = true;
                        else if (n.IndexOf("StealthFailCounter", StringComparison.Ordinal) >= 0) nativeStealth = true;
                    }
                    bool anyAsleep = false;
                    if (sceneMission && Mission.Current.PlayerEnemyTeam != null)
                        foreach (var b in Mission.Current.Agents)
                        {
                            if (b == null || !b.IsActive() || !b.IsHuman || b.Team != Mission.Current.PlayerEnemyTeam) continue;
                            try { if (b.CurrentWatchState != Agent.WatchState.Alarmed) { anyAsleep = true; break; } } catch { }
                        }
                    _active = on && IsHideoutAssault() && sceneMission && !nativeStealth && anyAsleep;
                    if (_active) Log("armed — assault entry, camp unaware");
                    else if (on && IsHideoutAssault())
                        Log("standing down — " + (nativeStealth ? "native stealth mission owns spotted=fail" : "camp already alert or not the scene mission"));
                }
                if (!_active) return;
                if (_alarmed) { PulseAlarmed(dt); TickReinforcements(dt); return; }

                _pollT += dt;
                if (_pollT < 0.25f) return;
                float step = _pollT; _pollT = 0f;

                UpdateDetection(step);
                UpdateMeter();
            }
            catch (Exception ex)
            {
                _active = false;
                Log("tick fail (disabled): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            base.OnAgentRemoved(affectedAgent, affectorAgent, agentState, blow);
            try
            {
                if (!_active || _alarmed || affectedAgent == null || Mission.Current == null) return;
                if (affectedAgent.Team != Mission.Current.PlayerEnemyTeam) return;
                // a bandit died — did anyone hear it?
                foreach (var b in Mission.Current.Agents)
                {
                    if (b == null || !b.IsActive() || !b.IsHuman || b.Team != affectedAgent.Team || b == affectedAgent) continue;
                    if (b.Position.Distance(affectedAgent.Position) < 16f) { RingAlarm("a kill was witnessed"); return; }
                }
            }
            catch { }
        }

        public override void OnRemoveBehavior()
        {
            base.OnRemoveBehavior();
            HoldFirstPhase = false; // never let a hold outlive its mission
            WaveAgents.Clear();
            try
            {
                var screen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen as MissionScreen;
                if (screen != null)
                {
                    if (_layer != null) screen.RemoveLayer(_layer);
                    if (_reinLayer != null) screen.RemoveLayer(_reinLayer);
                }
            }
            catch { }
            _layer = null; _vm = null; _reinLayer = null; _reinVm = null;
        }

        // same predicate as the assault panel above: hideout map event + bandit agents.
        private static bool IsHideoutAssault()
        {
            var mission = Mission.Current;
            if (mission == null) return false;
            if (MobileParty.MainParty?.MapEvent?.IsHideoutBattle != true) return false;
            foreach (var agent in mission.Agents)
            {
                if (agent == null || !agent.IsHuman) continue;
                if (agent.Character is CharacterObject co && co.Occupation == Occupation.Bandit) return true;
            }
            return false;
        }

        private void UpdateDetection(float step)
        {
            var mission = Mission.Current;
            var enemyTeam = mission.PlayerEnemyTeam;
            if (enemyTeam == null) return;

            float sens = 1f;
            try { if (MCMSettings.Instance != null) sens = MCMSettings.Instance.HideoutAlarmSensitivity; } catch { }
            if (sens < 0.2f) sens = 0.2f; if (sens > 3f) sens = 3f;

            // intruders = the player + his troops (the whole army can be spotted)
            var intruders = new List<Agent>();
            foreach (var a in mission.Agents)
                if (a != null && a.IsActive() && a.IsHuman && a.Team == mission.PlayerTeam) intruders.Add(a);
            if (intruders.Count == 0) return;

            float best = 0f;
            foreach (var b in mission.Agents)
            {
                if (b == null || !b.IsActive() || !b.IsHuman || b.Team != enemyTeam) continue;
                // the engine's own vision beat our meter — a bandit is already alarmed
                try { if (b.CurrentWatchState == Agent.WatchState.Alarmed) { RingAlarm("a bandit saw you"); return; } }
                catch { }
                foreach (var m in intruders)
                {
                    var to = m.Position - b.Position;
                    float dist = to.Length;
                    if (dist > 28f) continue;
                    to.Normalize();
                    float face = Vec3.DotProduct(b.LookDirection, to);
                    if (face < 0.45f) continue;                      // ~±63° view cone
                    float closeness = 1f - dist / 28f;
                    float gain = (0.25f + 0.75f * closeness) * face; // near + centered = fast
                    if (gain > best) best = gain;
                }
            }

            if (best > 0f) _detection = Math.Min(1f, _detection + step * 0.55f * sens * best);
            else _detection = Math.Max(0f, _detection - step * 0.30f);
            if (_detection >= 1f) RingAlarm("suspicion peaked");
        }

        private void RingAlarm(string why)
        {
            if (_alarmed) return;
            _alarmed = true;
            AlarmRang = true;
            Log("ALARM (" + why + ") — waking the whole camp");
            try { TaleWorlds.Engine.SoundEvent.PlaySound2D("event:/ui/mission/horns/attack"); } catch { }

            var mission = Mission.Current;
            var enemyTeam = mission?.PlayerEnemyTeam;
            if (enemyTeam == null) return;

            int woke = 0;
            foreach (var b in mission.Agents)
            {
                if (b == null || !b.IsActive() || !b.IsHuman || b.Team != enemyTeam) continue;
                try { b.SetWatchState(Agent.WatchState.Alarmed); woke++; } catch { }
            }
            try { BeginReinforcementCountdown(); } catch (Exception rex) { Log("reinforcements fail: " + rex.GetType().Name + ": " + rex.Message); }
            try
            {
                foreach (var f in enemyTeam.FormationsIncludingEmpty)
                    if (f != null && f.CountOfUnits > 0)
                        f.SetMovementOrder(MovementOrder.MovementOrderCharge);
            }
            catch (Exception ex) { Log("charge orders fail: " + ex.Message); }

            ProbeWaveRelease();

            EnsureLayer();
            if (_vm != null)
            {
                _vm.Show = true; _vm.State = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_021", "ALARMED"); _vm.Tint = "#E0402CFF";
                _vm.FillWidth = 172f; _vm.ContainerAlpha = 1f; _vm.EyeAlpha = 1f;
            }
            _pulseT = 0f;
            Log("alarm raised on " + woke + " bandits");
        }

        // on alarm the camp calls for help: size up the raiders, start the same
        // countdown banner the field-battle backup uses, and when it hits zero
        // the wave arrives through the mission spawn points, already alarmed.
        private void BeginReinforcementCountdown()
        {
            int cap = 16;
            try { if (MCMSettings.Instance != null) cap = MCMSettings.Instance.HideoutAlarmReinforcements; } catch { }
            if (cap <= 0) return;
            if (cap > 20) cap = 20;

            var mission = Mission.Current;
            var enemyTeam = mission.PlayerEnemyTeam;

            // the horn answers what it hears: squad size, troop quality, and the
            // raider's name. small green bands get a handful; famous warbands
            // full of veterans get the whole camp.
            int squad = 0; float tierSum = 0f;
            foreach (var a in mission.Agents)
            {
                if (a == null || !a.IsActive() || !a.IsHuman || a.Team != mission.PlayerTeam) continue;
                squad++;
                if (a.Character is CharacterObject pc) tierSum += pc.Tier;
            }
            float avgTier = squad > 0 ? tierSum / squad : 0f;
            int clanTier = 0;
            try { clanTier = TaleWorlds.CampaignSystem.Clan.PlayerClan?.Tier ?? 0; } catch { }

            int want = (int)Math.Round(squad * 1.0f + avgTier * 1.5f + clanTier * 1.0f);
            if (want < 4) want = 4;
            if (want > cap) want = cap;
            Log("reinforcements: squad=" + squad + " avgTier=" + avgTier.ToString("0.0")
                + " clanTier=" + clanTier + " -> " + want + " (cap " + cap + ")");

            _reinWant = want;
            _reinThreat01 = cap > 4 ? (want - 4f) / (cap - 4f) : 1f;
            int delay = 30;
            try { if (MCMSettings.Instance != null) delay = MCMSettings.Instance.BanditBackupArrivalDelay; } catch { }
            if (delay < 10) delay = 10; if (delay > 90) delay = 90;
            try
            {
                var screen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen as MissionScreen;
                if (screen != null)
                {
                    _reinVm = new BanditReinforcementVM(delay, 1, want);
                    _reinVm.PanelShiftY = 44f; // stacked directly under the ALARMED meter
                    _reinLayer = new GauntletLayer("BpHideoutReinf", 8, false);
                    _reinLayer.LoadMovie("BanditReinforcement", _reinVm);
                    screen.AddLayer(_reinLayer);
                }
            }
            catch (Exception bex) { Log("reinforcement banner fail: " + bex.Message); _reinVm = null; }
            if (_reinVm == null)
            {
                // no banner, no drama — just send them now
                _reinReleased = true; _reinCleaned = true;
                try { SpawnReinforcementsNow(); } catch (Exception sx) { Log("reinforcement wave fail: " + sx.Message); }
            }
            else Log("reinforcements inbound in " + delay + "s");

            // hold the boss scene until the wave lands (the landing registers the
            // agents with the clear-camp objective, which takes over from there).
            if (!_reinReleased) { HoldFirstPhase = true; Log("first phase HELD until the wave lands"); }

        }

        // the countdown banner ticks while the camp fights; at zero the wave lands.
        private void TickReinforcements(float dt)
        {
            if (_reinVm == null || _reinCleaned) return;
            try
            {
                _reinVm.Tick(dt);
                if (!_reinReleased && _reinVm.PollFinished())
                {
                    _reinReleased = true;
                    try { SpawnReinforcementsNow(); } catch (Exception ex) { Log("reinforcement wave fail: " + ex.GetType().Name + ": " + ex.Message); }
                    try { _reinVm.BeginArrival(); } catch { }
                    try { TaleWorlds.Engine.SoundEvent.PlaySound2D("event:/ui/notification/war_declared"); } catch { }
                }
                if (_reinReleased && _reinVm.IsFinishedFading)
                {
                    _reinCleaned = true;
                    var screen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen as MissionScreen;
                    if (screen != null && _reinLayer != null) screen.RemoveLayer(_reinLayer);
                    _reinLayer = null; _reinVm = null;
                }
            }
            catch (Exception ex) { _reinCleaned = true; Log("reinforcement tick fail: " + ex.Message); }
        }

        // the wave itself — clones of the bandits already living here (culture-
        // correct for free), from the hideout's own defender party. NO horses:
        // this is a camp on foot, so mounted templates arrive dismounted.
        private void SpawnReinforcementsNow()
        {
            int want = _reinWant;
            if (want <= 0) return;
            var mission = Mission.Current;
            if (mission == null) return;
            var enemyTeam = mission.PlayerEnemyTeam;
            TaleWorlds.CampaignSystem.Party.PartyBase origin = null;
            try { origin = MobileParty.MainParty?.MapEvent?.DefenderSide?.LeaderParty; } catch { }
            if (origin == null) { Log("reinforcements: no defender party on the map event"); return; }

            var pool = new List<CharacterObject>();
            foreach (var a in mission.Agents)
                if (a != null && a.IsHuman && a.Team == enemyTeam
                    && a.Character is CharacterObject co && co.Occupation == Occupation.Bandit)
                    pool.Add(co);
            if (pool.Count == 0) { Log("reinforcements: no bandit templates alive"); return; }
            // quality scales with the threat too: weak raiders face whoever is
            // nearest; strong raiders pull the camp's veterans off their cots.
            pool.Sort((x, y) => x.Tier.CompareTo(y.Tier));
            int skipGreens = (int)(_reinThreat01 * pool.Count * 0.75f);
            if (skipGreens >= pool.Count) skipGreens = pool.Count - 1;

            int spawned = 0;
            var newAgents = new List<Agent>();
            for (int i = 0; i < want; i++)
            {
                var ch = pool[(skipGreens + i) % pool.Count];
                try
                {
                    var fclass = ch.IsRanged ? FormationClass.Ranged : FormationClass.Infantry;
                    var agent = mission.SpawnTroop(
                        new TaleWorlds.CampaignSystem.AgentOrigins.PartyAgentOrigin(origin, ch),
                        false, true, false, true, want, i, true, true,
                        (Vec3?)null, (Vec2?)null, formationIndex: fclass);
                    if (agent != null) { newAgents.Add(agent); WaveAgents.Add(agent); }
                    spawned++;
                }
                catch (Exception ex) { Log("reinforcement spawn fail: " + ex.GetType().Name + ": " + ex.Message); }
            }
            Log("reinforcements: " + spawned + "/" + want + " bandits answer the horn");

            // fold the wave into the clear-camp objective properly: add the agents
            // to its tracked list AND grow its required total. the total is a
            // readonly captured at construction (progress = total - alive), so
            // list-only registration is exactly what made the bar go negative.
            // deaths decrement automatically — vanilla removes any dead agent from
            // the list. boss pacing stays with the IsSideDepleted postfix.
            try
            {
                object hmc = null;
                foreach (var mb in mission.MissionBehaviors)
                    if (mb != null && mb.GetType().Name == "HideoutMissionController") { hmc = mb; break; }
                var rflags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var lfi = hmc?.GetType().GetField("_clearObjectiveTargetAgents", rflags);
                var list = lfi != null ? lfi.GetValue(hmc) as System.Collections.IList : null;
                var ofi = hmc?.GetType().GetField("_clearTheMainCampObjective", rflags);
                var obj = ofi != null ? ofi.GetValue(hmc) : null;
                var rfi = obj?.GetType().GetField("_requiredProgressAmount", rflags);
                if (list != null && obj != null && rfi != null)
                {
                    foreach (var ag in newAgents) list.Add(ag);
                    int req = (int)rfi.GetValue(obj);
                    rfi.SetValue(obj, req + newAgents.Count);
                    Log("objective grown: +" + newAgents.Count + " agents, total " + req + " -> " + (req + newAgents.Count));
                }
                else Log("objective grow skipped (list/objective/total not found) — bar will ignore the wave");
            }
            catch (Exception lex) { Log("objective grow fail: " + lex.Message); }
            HoldFirstPhase = false; // wave landed — WaveAgents holds the phase now

            // charge orders were issued before these agents existed — put the
            // newcomers on the attack too, or they stand at the spawn points.
            try
            {
                foreach (var f in enemyTeam.FormationsIncludingEmpty)
                    if (f != null && f.CountOfUnits > 0)
                        f.SetMovementOrder(MovementOrder.MovementOrderCharge);
            }
            catch (Exception oex) { Log("wave charge orders fail: " + oex.Message); }
        }

        // evidence probe (dev builds): the controller's spawn/phase methods and
        // boss/phase/spawner field values at alarm time — decides whether an
        // early SpawnBossAndBodyguards release is safe in a later pass.
        private void ProbeWaveRelease()
        {
            try
            {
                if (!BandItPlus.Diagnostics.BpBuild.IsDev || _probeLogged) return;
                _probeLogged = true;
                object hmc = null;
                foreach (var mb in Mission.Current.MissionBehaviors)
                    if (mb != null && mb.GetType().Name == "HideoutMissionController") { hmc = mb; break; }
                if (hmc == null) { Log("wave probe: no HideoutMissionController behavior found"); return; }
                var flags = System.Reflection.BindingFlags.Instance
                          | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                var names = new List<string>();
                foreach (var m in hmc.GetType().GetMethods(flags))
                    if (m.Name.IndexOf("Spawn", StringComparison.OrdinalIgnoreCase) >= 0
                        || m.Name.IndexOf("Phase", StringComparison.OrdinalIgnoreCase) >= 0
                        || m.Name.IndexOf("Wave", StringComparison.OrdinalIgnoreCase) >= 0)
                        names.Add(m.Name);
                Log("HideoutMissionController spawn/phase methods: " + string.Join(", ", names));
                var fields = new List<string>();
                foreach (var fi in hmc.GetType().GetFields(flags))
                    if (fi.Name.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0
                        || fi.Name.IndexOf("phase", StringComparison.OrdinalIgnoreCase) >= 0
                        || fi.Name.IndexOf("spawner", StringComparison.OrdinalIgnoreCase) >= 0
                        || fi.Name.IndexOf("state", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        object v = null; try { v = fi.GetValue(hmc); } catch { }
                        fields.Add(fi.Name + "=" + (v == null ? "null" : v.ToString()));
                    }
                Log("HideoutMissionController fields: " + string.Join(", ", fields));
            }
            catch (Exception ex) { Log("wave probe fail: " + ex.Message); }
        }

        private void EnsureLayer()
        {
            if (_layerAdded) return;
            try
            {
                var screen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen as MissionScreen;
                if (screen == null) return;
                _vm = new AlarmMeterVM();
                _layer = new GauntletLayer("BpHideoutAlarm", 6, false);
                _layer.LoadMovie("AlarmMeter", _vm);
                screen.AddLayer(_layer);
                _layerAdded = true;
            }
            catch (Exception ex) { Log("alarm layer fail: " + ex.Message); _layerAdded = true; }
        }

        // full-red heartbeat once the camp is awake — RingAlarm set the colors,
        // this keeps them breathing (and stops UpdateMeter overwriting the red).
        private void PulseAlarmed(float dt)
        {
            if (_vm == null) return;
            _pulseT += dt;
            float p = 0.72f + 0.28f * (float)Math.Sin(_pulseT * 8f);
            _vm.ContainerAlpha = p;
            _vm.EyeAlpha = p;
        }

        private void UpdateMeter()
        {
            EnsureLayer();
            if (_vm == null || _alarmed) return;
            float shown = _detection;
            _vm.Show = shown > 0.04f;
            _vm.FillWidth = shown * 172f;
            _vm.ContainerAlpha = Math.Min(1f, 0.28f + shown * 0.72f);
            _vm.EyeAlpha = Math.Min(1f, 0.20f + shown * 0.80f);
            if (shown >= 0.5f) { _vm.Tint = "#E0A038FF"; _vm.State = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_022", "SUSPICIOUS"); }
            else { _vm.Tint = "#C8A24EFF"; _vm.State = ""; }
        }

        private static void Log(string msg)
        {
            try { HideoutPeacefulVisitState.Log("[BP-HideoutAlarm] " + msg); }
            catch { }
        }
    }

    // ================================================================
    // Sneak-in SHADOW BAR (2026-07-03). The native sneak mission runs
    // StealthFailCounterMissionLogic: any alarmed bandit starts a 5-second
    // fail timer the player cannot see. This bar makes stealth legible —
    // amber suspicion while someone is close to spotting you (read-only
    // cone math, alarm-controller family), a red draining grace bar once
    // you're SEEN (the fill IS the native FailCounterElapsedTime), and a
    // green flash when you clear it. Arms ONLY when the stealth logic is
    // present — the exact inverse of BpHideoutAlarmController's gate, so
    // the two HUDs can never co-exist.
    // ================================================================
    public class BpSneakShadowController : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        private const float kTrackPx = 276f;
        private bool _checked, _armed;
        private float _elapsed;
        private GauntletLayer _layer;
        private AlarmMeterVM _vm;
        private SandBox.Missions.StealthFailCounterMissionLogic _stealth;
        private float _suspicion;
        private float _scanT;
        private int _watchers;
        private bool _wasSeen;
        private float _clearBeat;
        private float _pulseT;

        // Also consumed by the chief-reveal card for the infiltration reflavor.
        public static bool IsStealthVariant()
        {
            try
            {
                return Mission.Current != null
                    && Mission.Current.GetMissionBehavior<SandBox.Missions.StealthFailCounterMissionLogic>() != null;
            }
            catch { return false; }
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            try
            {
                _elapsed += dt;
                if (!_checked)
                {
                    if (_elapsed < 2f) return;
                    _checked = true;
                    bool on = MCMSettings.Instance != null && MCMSettings.Instance.EnableMod
                        && MCMSettings.Instance.SneakShadowHud;
                    bool hideout = MobileParty.MainParty?.MapEvent?.IsHideoutBattle == true;
                    _stealth = on && hideout
                        ? Mission.Current?.GetMissionBehavior<SandBox.Missions.StealthFailCounterMissionLogic>()
                        : null;
                    _armed = _stealth != null;
                    if (_armed)
                    {
                        _vm = new AlarmMeterVM();
                        _layer = new GauntletLayer("BpSneakShadow", 7, false);
                        _layer.LoadMovie("SneakShadowBar", _vm);
                        var screen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen;
                        if (screen != null) screen.AddLayer(_layer);
                        Log("armed — stealth logic present, grace="
                            + _stealth.FailCounterSeconds.ToString("F1") + "s");
                    }
                    return;
                }
                if (!_armed || _vm == null) return;
                UpdateShadow(dt);
            }
            catch (Exception ex)
            {
                Log("[ERR] tick " + ex.GetType().Name + ": " + ex.Message);
                _armed = false;
            }
        }

        private void UpdateShadow(float dt)
        {
            // The native grace timer overrides everything: elapsed > 0 means an
            // alarmed bandit exists and the 5s fail countdown is running.
            float graceElapsed = 0f, graceTotal = 5f;
            try
            {
                graceElapsed = _stealth.FailCounterElapsedTime;
                graceTotal = _stealth.FailCounterSeconds;
            }
            catch { }
            bool seen = graceElapsed > 0.001f;

            _scanT += dt;
            if (_scanT >= 0.2f) { _scanT = 0f; ScanWatchers(); }

            if (seen)
            {
                if (!_wasSeen) Log("SEEN — native grace timer running");
                _wasSeen = true;
                _clearBeat = 0f;
                float remaining = graceTotal - graceElapsed;
                if (remaining < 0f) remaining = 0f;
                _pulseT += dt * 7f;
                _vm.Show = true;
                _vm.ContainerAlpha = 0.82f + 0.18f * (float)Math.Abs(Math.Sin(_pulseT));
                _vm.EyeAlpha = 1f;
                _vm.Tint = "#E05252FF";
                _vm.State = new TaleWorlds.Localization.TextObject("{=bp_hch_008}SEEN · {SECONDS}s").SetTextVariable("SECONDS", remaining.ToString("F1")).ToString();
                _vm.HintText = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_023", "vanish or silence them");
                _vm.FillWidth = kTrackPx * (graceTotal > 0f ? remaining / graceTotal : 0f);
                _vm.WatchersText = new TaleWorlds.Localization.TextObject("{=bp_hch_009}WATCHERS · {COUNT}").SetTextVariable("COUNT", _watchers).ToString();
                return;
            }

            if (_wasSeen)
            {
                _wasSeen = false;
                _clearBeat = 1.6f;
                _suspicion = 0f;
                Log("cleared — grace timer reset");
            }

            if (_clearBeat > 0f)
            {
                _clearBeat -= dt;
                _vm.Show = true;
                _vm.ContainerAlpha = 1f;
                _vm.EyeAlpha = 1f;
                _vm.Tint = "#7FB069FF";
                _vm.State = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_024", "CLEAR");
                _vm.HintText = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_025", "the camp settles");
                _vm.FillWidth = 0f;
                _vm.WatchersText = new TaleWorlds.Localization.TextObject("{=bp_hch_009}WATCHERS · {COUNT}").SetTextVariable("COUNT", _watchers).ToString();
                return;
            }

            // Amber early warning — invisible until someone starts noticing you.
            bool visible = _suspicion > 0.03f;
            float target = visible ? 1f : 0f;
            float alpha = _vm.ContainerAlpha + (target - _vm.ContainerAlpha) * Math.Min(1f, dt * 5f);
            _vm.ContainerAlpha = alpha;
            _vm.Show = visible || alpha > 0.02f;
            if (!_vm.Show) return;
            _vm.EyeAlpha = 0.35f + 0.65f * _suspicion;
            _vm.Tint = "#C9973FFF";
            _vm.State = BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_026", "UNSEEN");
            _vm.HintText = _suspicion < 0.5f ? BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_027", "keep to the shadows") : BandItPlus.Localization.Get("bp_hideoutassaultpanelmissionview_028", "someone is looking your way");
            _vm.FillWidth = kTrackPx * _suspicion;
            _vm.WatchersText = new TaleWorlds.Localization.TextObject("{=bp_hch_009}WATCHERS · {COUNT}").SetTextVariable("COUNT", _watchers).ToString();
        }

        // Read-only cone scan (alarm-controller family): a bandit within 28m whose
        // look direction points at the player counts as a watcher; the strongest
        // watcher drives the suspicion rise, absence decays it.
        private void ScanWatchers()
        {
            _watchers = 0;
            float strongest = 0f;
            try
            {
                var mission = Mission.Current;
                var me = Agent.Main;
                if (mission == null || me == null || !me.IsActive())
                {
                    _suspicion -= 0.30f * 0.2f;
                    if (_suspicion < 0f) _suspicion = 0f;
                    return;
                }
                foreach (var a in mission.Agents)
                {
                    if (a == null || !a.IsActive() || !a.IsHuman || a == me) continue;
                    if (!(a.Character is CharacterObject co) || co.Occupation != Occupation.Bandit) continue;
                    var to = me.Position - a.Position;
                    float dist = to.Length;
                    if (dist > 28f || dist < 0.01f) continue;
                    var look = a.LookDirection;
                    float dot = (look.x * to.x + look.y * to.y + look.z * to.z) / dist;
                    if (dot < 0.45f) continue;
                    _watchers++;
                    float closeness = 1f - dist / 28f;
                    float gain = (0.25f + 0.75f * closeness) * dot;
                    if (gain > strongest) strongest = gain;
                }
            }
            catch (Exception ex)
            {
                Log("ScanWatchers " + ex.GetType().Name + ": " + ex.Message);
            }
            float sens = 1f;
            try { if (MCMSettings.Instance != null) sens = MCMSettings.Instance.HideoutAlarmSensitivity; } catch { }
            if (_watchers > 0) _suspicion += 0.55f * sens * strongest * 0.2f;
            else _suspicion -= 0.30f * 0.2f;
            if (_suspicion < 0f) _suspicion = 0f;
            if (_suspicion > 1f) _suspicion = 1f;
        }

        public override void OnRemoveBehavior()
        {
            try
            {
                var screen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen;
                if (screen != null && _layer != null) screen.RemoveLayer(_layer);
                _layer = null;
            }
            catch { }
            base.OnRemoveBehavior();
        }

        private static void Log(string msg)
        {
            try { HideoutPeacefulVisitState.Log("[BP-Sneak] " + msg); }
            catch { }
        }
    }
}
