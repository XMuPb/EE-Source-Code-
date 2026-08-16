using System;
using System.Collections.Generic;
using System.Reflection;
using BandItPlus.HideoutVisit;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BandItPlus.Patches
{
    // Phase 1 best-effort peace patch: when HideoutPeacefulVisitState.Active is true,
    // suppress hideout combat agent spawning. Method names vary across Bannerlord versions —
    // we use Prepare() to discover the actual target by name pattern at load time, log what we
    // found, and skip patching gracefully if no match exists.
    [HarmonyPatch]
    public static class HideoutMissionSuppressEnemySpawnPatch
    {
        private static readonly string[] CandidateMethodNames = new[]
        {
            "SpawnHideoutTroops",
            "SpawnDefenders",
            "SpawnAttackers",
            "SpawnAgentsForBattle",
            "SpawnAgents",
            "SpawnTroops",
            "AddHideoutDefendersToScene",
            "OnBehaviorInitialize",
        };

        private static MethodBase _resolvedTarget;

        public static IEnumerable<MethodBase> TargetMethods()
        {
            if (_resolvedTarget != null) yield return _resolvedTarget;
        }

        public static bool Prepare()
        {
            try
            {
                var hmcType = AccessTools.TypeByName("SandBox.Missions.MissionLogics.Hideout.HideoutMissionController");
                if (hmcType == null)
                {
                    HideoutPeacefulVisitState.Log("Prepare: HideoutMissionController type NOT FOUND — peace patch will be no-op.");
                    return false;
                }

                foreach (var name in CandidateMethodNames)
                {
                    var m = AccessTools.Method(hmcType, name);
                    if (m != null)
                    {
                        _resolvedTarget = m;
                        HideoutPeacefulVisitState.Log("Prepare: resolved patch target = HideoutMissionController." + name);
                        return true;
                    }
                }

                HideoutPeacefulVisitState.Log("Prepare: no candidate method matched on HideoutMissionController. " +
                    "Phase 1 will load the scene but NOT suppress enemy spawning — combat will trigger. " +
                    "Phase 2 will iterate based on the actual method name.");
                // BUG H6 FIX (2026-04-30): surface a user-visible warning so the player knows
                // peaceful mode is degraded (was silent before — user could enter a hideout
                // expecting peace and get jumped by combat-spawned bandits). One-shot via
                // InformationManager so it appears in the chat log on first failure detection.
                // Wrapped in try/catch because InformationManager may not yet be initialized
                // at module-load time when Prepare runs.
                try
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        BandItPlus.Localization.Get("bp_hideoutpeacefulvisitpatch_001", "BandIt Plus: peaceful hideout patch is degraded — combat may still trigger. Check bp_peace_debug.log."),
                        Colors.Red));
                }
                catch { /* defensive — InformationManager not always available at Prepare time */ }
                return false;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("Prepare EXCEPTION: " + ex.Message);
                return false;
            }
        }

        public static bool Prefix()
        {
            if (!HideoutPeacefulVisitState.Active) return true;
            HideoutPeacefulVisitState.Log("Prefix: peaceful flag active — skipping " +
                (_resolvedTarget?.Name ?? "?"));
            return false;
        }
    }

    // Reset peace flag whenever a Mission ends. Vanilla BasicLeaveMissionLogic + LeaveMissionLogic
    // in the village stack handle PlayerEncounter.LeaveSettlement() / Finish() correctly themselves.
    //
    // PRIOR BUG (Phase 6 task 9 v1): we tried to add defensive LeaveSettlement+Finish calls here.
    // That fired DURING vanilla's cleanup and nulled PlayerEncounter.LocationEncounter before
    // MissionLocationLogic.OnRemoveBehavior() ran during finalize → NRE in native cleanup.
    // Stack trace observed:
    //   SandBox.Missions.MissionLogics.MissionLocationLogic.OnRemoveBehavior()
    //   Mission.RemoveMissionBehavior → Mission.OnMissionStateFinalize → MissionState.OnFinalize
    //
    // FIX: revert to flag-reset-only. Vanilla handles the rest.
    [HarmonyPatch(typeof(Mission), nameof(Mission.EndMission))]
    public static class MissionEndResetPeaceflagPatch
    {
        public static void Postfix()
        {
            if (HideoutPeacefulVisitState.Active)
            {
                HideoutPeacefulVisitState.Log("MissionEndResetPeaceflagPatch: clearing peaceful flag on EndMission (letting vanilla handle PlayerEncounter cleanup)");
                HideoutPeacefulVisitState.Reset();
            }
        }
    }

    // Harmony PREFIX on Mission.EndMission. Runs BEFORE the body executes — meaning BEFORE
    // set_MissionEnded(true) fires UsableMachine.OnMissionEnded, which is the iteration that
    // crashes in SandBox.Objects.AnimationPoints.AnimationPoint.SetAgentItemsVisibility(false)
    // when our chair-bound sitters get torn down.
    //
    // Fix: explicitly release every sitter's chair binding here, while agent state is still
    // valid. By the time vanilla's UsableMachine teardown reaches each chair, the binding is
    // already cleared — no NRE.
    //
    // Why CampaignEvents.OnMissionEndedEvent (used in HideoutVisitNpcSpawnBehavior.OnMissionEnded)
    // wasn't enough: that dispatcher fires AFTER set_MissionEnded(true) has already triggered
    // the UsableMachine teardown. Confirmed by user reproducing the same NRE after we shipped
    // the campaign-event handler. The Harmony Prefix is the only hook point that wins the race.
    // CRASH FIX: when peaceful flag is active, skip BattleAgentLogic.OnAgentHit entirely.
    // Fall damage in peaceful walkable hideouts routes through the combat-blow system
    // (Mission.FallDamageCallback → RegisterBlow → OnAgentHit → BattleAgentLogic.OnAgentHit)
    // and crashes there because BattleAgentLogic expects combat-AI state our peaceful agents
    // don't have. Skipping the body prevents the NRE; combat damage shouldn't happen in
    // peaceful mode anyway, so this is a no-op for legitimate game state.
    [HarmonyPatch]
    public static class PeacefulNoBattleAgentLogicHitPatch
    {
        public static MethodBase TargetMethod()
        {
            try
            {
                var type = AccessTools.TypeByName("SandBox.Missions.MissionLogics.BattleAgentLogic");
                if (type == null)
                {
                    HideoutPeacefulVisitState.Log("PeacefulNoBattleAgentLogicHitPatch: BattleAgentLogic type not found — patch is no-op");
                    return null;
                }
                // BUG M9 FIX (2026-04-30): pass explicit parameter type array to AccessTools.Method.
                // Without it, AccessTools picks the first matching method by name — but in 1.2.x
                // BattleAgentLogic has multiple OnAgentHit overloads (Agent, Agent, in MissionWeapon,
                // ref AttackCollisionData, in Blow). Picking the wrong overload silently no-ops the
                // patch — fall damage still NREs in BattleAgentLogic. Iterate all OnAgentHit methods
                // and pick the one with the most parameters (the "full" handler vanilla calls in
                // the fall-damage path). If still ambiguous, fall back to the first found and log
                // the count so we can refine.
                MethodInfo method = null;
                int bestParamCount = -1;
                int candidateCount = 0;
                foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "OnAgentHit") continue;
                    candidateCount++;
                    int pc = m.GetParameters().Length;
                    if (pc > bestParamCount) { bestParamCount = pc; method = m; }
                }
                if (method == null)
                {
                    HideoutPeacefulVisitState.Log("PeacefulNoBattleAgentLogicHitPatch: OnAgentHit method not found on BattleAgentLogic (0 candidates)");
                    return null;
                }
                HideoutPeacefulVisitState.Log("PeacefulNoBattleAgentLogicHitPatch: resolved BattleAgentLogic.OnAgentHit (" + candidateCount + " overloads, picked " + bestParamCount + "-param), patch will skip body when peaceful flag active");
                return method;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("PeacefulNoBattleAgentLogicHitPatch.TargetMethod EXCEPTION: " + ex.Message);
                return null;
            }
        }

        public static bool Prefix()
        {
            // Return false = skip the original method body entirely.
            // Return true = let vanilla execute normally (non-peaceful missions).
            return !HideoutPeacefulVisitState.Active;
        }
    }

    // CHAIR-BIND TEARDOWN NRE FIX (2026-04-30): re-enables `agent.UseGameObject(usable, 0)` chair
    // binding in HideoutVisitNpcSpawnBehavior. Previous 6 fix iterations all crashed in
    // SandBox.Objects.AnimationPoints.AnimationPoint.SetAgentItemsVisibility(false) during
    // mission teardown — vanilla expected metadata our peaceful agents don't carry (LocationCharacter
    // backing, full bone hierarchy, etc.). Same defensive pattern as PeacefulNoBattleAgentLogicHitPatch
    // above: skip the vanilla body entirely when our peaceful flag is active. Visual cost = none
    // (mission is already ending; SetAgentItemsVisibility just toggles weapon visuals during the
    // sit-pose teardown frame which the player never sees). Combat-mode missions are unaffected.
    // CHAIR-BIND TEARDOWN PROTECTION (DYNAMIC, 2026-04-30):
    // Previously this used [HarmonyPatch] auto-discovery which caused the encyclopedia render
    // bug — AnimationPoint.SetAgentItemsVisibility is a hot 3D-render method, and Harmony's
    // IL trampoline (installed by PatchAll at module load) corrupted character preview state.
    // Now we Patch dynamically only when entering a peaceful mission, and Unpatch on mission
    // end. The patch is registered ONLY during the brief mission window (a few minutes max),
    // never present when encyclopedia is viewed (which happens outside missions).
    public static class PeacefulNoAnimationPointVisibilityPatch
    {
        private static MethodBase _cachedTarget;
        private static bool _isPatched;

        // Resolve the SandBox.Objects.AnimationPoints.AnimationPoint.SetAgentItemsVisibility
        // method via reflection. Cached after first lookup. Called by Enable() to know what
        // to patch, and Disable() to know what to unpatch.
        private static MethodBase ResolveTarget()
        {
            if (_cachedTarget != null) return _cachedTarget;
            try
            {
                var type = AccessTools.TypeByName("SandBox.Objects.AnimationPoints.AnimationPoint");
                if (type == null)
                {
                    HideoutPeacefulVisitState.Log("PeacefulNoAnimationPointVisibilityPatch: AnimationPoint type not found — chair-bind protection unavailable");
                    return null;
                }
                MethodInfo best = null;
                int bestPc = -1;
                int candidateCount = 0;
                foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "SetAgentItemsVisibility") continue;
                    candidateCount++;
                    int pc = m.GetParameters().Length;
                    if (pc > bestPc) { bestPc = pc; best = m; }
                }
                if (best == null) return null;
                _cachedTarget = best;
                HideoutPeacefulVisitState.Log("PeacefulNoAnimationPointVisibilityPatch: resolved target (" + candidateCount + " overloads, picked " + bestPc + "-param)");
                return _cachedTarget;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("PeacefulNoAnimationPointVisibilityPatch.ResolveTarget EXCEPTION: " + ex.Message);
                return null;
            }
        }

        // Apply the patch. Called from HideoutVisitNpcSpawnBehavior.OnMissionStarted when
        // peaceful flag becomes active. Idempotent — calling twice is a no-op.
        public static void Enable()
        {
            if (_isPatched) return;
            try
            {
                var harmony = HideoutPeacefulVisitState.HarmonyInstance;
                if (harmony == null) { HideoutPeacefulVisitState.Log("PeacefulNoAnimationPointVisibilityPatch.Enable: Harmony instance is null"); return; }
                var target = ResolveTarget();
                if (target == null) return;
                var prefix = AccessTools.Method(typeof(PeacefulNoAnimationPointVisibilityPatch), nameof(Prefix));
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                _isPatched = true;
                HideoutPeacefulVisitState.Log("PeacefulNoAnimationPointVisibilityPatch.Enable: dynamically patched for this mission");
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("PeacefulNoAnimationPointVisibilityPatch.Enable fail: " + ex.Message); }
        }

        // Remove the patch. Called from HideoutVisitNpcSpawnBehavior.OnMissionEnded — always
        // safe to call (idempotent). Critical: must run on EVERY mission end to prevent the
        // patch from leaking into post-mission state and breaking encyclopedia.
        public static void Disable()
        {
            if (!_isPatched) return;
            try
            {
                var harmony = HideoutPeacefulVisitState.HarmonyInstance;
                if (harmony == null) return;
                var target = ResolveTarget();
                if (target == null) return;
                harmony.Unpatch(target, HarmonyPatchType.Prefix, "xmupb.banditplus");
                _isPatched = false;
                HideoutPeacefulVisitState.Log("PeacefulNoAnimationPointVisibilityPatch.Disable: dynamically unpatched");
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("PeacefulNoAnimationPointVisibilityPatch.Disable fail: " + ex.Message); }
        }

        // CRITICAL: only skip when the call is HIDING items (show=false), which is the
        // teardown direction that NREs. Calls with show=true happen during agent init/spawn
        // and conversation prep — we MUST let vanilla run those, otherwise items never appear
        // (half-naked agents) and downstream systems (greeter conversation, encyclopedia) break.
        // Harmony binds the first parameter via __0 by position. Returning true = let vanilla
        // execute. Returning false = skip body. So the rule is:
        //   show==true  → return true  (let vanilla make items visible normally)
        //   show==false → return false (skip the NRE-prone teardown path)
        public static bool Prefix(bool __0) { return __0; }
    }

    [HarmonyPatch(typeof(Mission), nameof(Mission.EndMission))]
    public static class MissionEndReleaseUsablesPatch
    {
        public static void Prefix(Mission __instance)
        {
            if (!HideoutPeacefulVisitState.Active) return;
            try
            {
                var beh = Campaign.Current?.GetCampaignBehavior<HideoutVisitNpcSpawnBehavior>();
                beh?.ReleaseAllSittersFromChairs();
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("MissionEndReleaseUsablesPatch EXCEPTION: " + ex.Message);
            }
        }
    }

    // hideout defenders' SHIELDS paint the banner captured at agent spawn:
    // PartyGroupAgentOrigin.Banner = MapFaction.Banner for leaderless bandit
    // parties — the vanilla bandit clan, never the chief clan (it bypasses
    // PartyBase.Banner/CustomBanner completely, and PartyGroupAgentOrigin.
    // SetBanner throws NotImplementedException). the one seam every consumer
    // shares is Mission.SpawnAgent: its AgentBuildData feeds shield tableaus,
    // banner-bearer weapons and visuals. for defender-team agents in hideout
    // scenes, swap in the chief clan's banner via the public fluent builder.
    [HarmonyPatch]
    public static class HideoutShieldBannerPatch
    {
        private static bool _loggedApply;

        static bool Prepare()
        {
            var ok = TargetMethod() != null;
            if (!ok) HideoutPeacefulVisitState.Log("[BP-ShieldBanner] Mission.SpawnAgent not found — patch skipped");
            return ok;
        }

        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Mission), "SpawnAgent",
                new[] { typeof(AgentBuildData), typeof(bool) });
        }

        static void Prefix(AgentBuildData agentBuildData)
        {
            try
            {
                if (agentBuildData == null) return;
                var mission = Mission.Current;
                if (mission == null) return;
                bool hideout = false;
                foreach (var mb in mission.MissionBehaviors)
                    if (mb != null && mb.GetType().Name == "HideoutMissionController") { hideout = true; break; }
                if (!hideout) return;
                var team = agentBuildData.AgentTeam;
                if (team == null || team.Side != TaleWorlds.Core.BattleSideEnum.Defender) return;
                var banner = ResolveChiefBanner();
                if (banner == null) return;
                agentBuildData.Banner(banner);
                if (!_loggedApply)
                {
                    _loggedApply = true;
                    HideoutPeacefulVisitState.Log("[BP-ShieldBanner] defender shields now carry the chief clan banner");
                }
            }
            catch { }
        }

        private static TaleWorlds.Core.Banner ResolveChiefBanner()
        {
            try
            {
                var ev = TaleWorlds.CampaignSystem.MapEvents.MapEvent.PlayerMapEvent;
                string cid = ev?.DefenderSide?.LeaderParty?.MapFaction?.Culture?.StringId;
                if (string.IsNullOrEmpty(cid)) return null;
                var chief = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(cid);
                return chief?.Clan?.Banner;
            }
            catch { return null; }
        }
    }

    // the boss scene is gated by IsSideDepleted(Defender): CheckBattleResolved
    // sees the bandit side at zero NumberOfActiveTroops, runs a 4s timer, then
    // StartCinematic. our reinforcements are invisible to that counter (it only
    // counts troops the controller's own supplier spawned), so the scene fired
    // while they were still fighting. this postfix keeps the bandit side "alive"
    // while the alarm wave is inbound or any of its agents still stand — then
    // vanilla's own pacing takes over and the boss waits for the real last kill.
    [HarmonyPatch]
    public static class HideoutPhaseHoldPatch
    {
        private static bool _loggedHold;

        static bool Prepare() { return TargetMethod() != null; }

        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("SandBox.Missions.MissionLogics.Hideout.HideoutMissionController");
            return t != null ? AccessTools.Method(t, "IsSideDepleted") : null;
        }

        static void Postfix(TaleWorlds.Core.BattleSideEnum side, ref bool __result)
        {
            try
            {
                if (!__result || side != TaleWorlds.Core.BattleSideEnum.Defender) return;
                if (!BandItPlus.UI.BpHideoutAlarmController.KeepsBanditSideAlive()) { _loggedHold = false; return; }
                __result = false;
                if (!_loggedHold)
                {
                    _loggedHold = true;
                    HideoutPeacefulVisitState.Log("[BP-HideoutAlarm] bandit side held alive — wave pending or still fighting");
                }
            }
            catch { }
        }
    }

    // same KeyNotFound insurance as the alarmed-state guard below, but for the
    // controller's OnAgentRemoved — our wave agents flow through its removal
    // bookkeeping too now that they sit in the objective list.
    [HarmonyPatch]
    public static class HideoutAgentRemovedGuardPatch
    {
        static bool Prepare() { return TargetMethod() != null; }

        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("SandBox.Missions.MissionLogics.Hideout.HideoutMissionController");
            return t != null ? AccessTools.Method(t, "OnAgentRemoved") : null;
        }

        static Exception Finalizer(Exception __exception)
        {
            if (__exception is KeyNotFoundException)
            {
                try { HideoutPeacefulVisitState.Log("[BP-HideoutAlarm] removal bookkeeping skipped for unmanaged agent (crash averted)"); }
                catch { }
                return null;
            }
            return __exception;
        }
    }

    // HideoutMissionController.OnAgentAlarmedStateChanged does a raw dictionary
    // lookup on the agent — a bookkeeping map filled only for agents the controller
    // spawned itself. agents WE spawn (the alarm reinforcements) were never
    // registered, so the lookup throws KeyNotFound and hard-crashes the mission.
    // the finalizer swallows exactly that exception: vanilla bookkeeping for its
    // own agents is untouched, and for foreign agents it safely no-ops.
    [HarmonyPatch]
    public static class HideoutAlarmedStateGuardPatch
    {
        private static bool _logged;

        static bool Prepare()
        {
            var ok = TargetMethod() != null;
            if (!ok) HideoutPeacefulVisitState.Log("[BP-HideoutAlarm] guard: OnAgentAlarmedStateChanged not found — patch skipped");
            return ok;
        }

        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("SandBox.Missions.MissionLogics.Hideout.HideoutMissionController");
            return t != null ? AccessTools.Method(t, "OnAgentAlarmedStateChanged") : null;
        }

        static Exception Finalizer(Exception __exception)
        {
            if (__exception is KeyNotFoundException)
            {
                if (!_logged)
                {
                    _logged = true;
                    try { HideoutPeacefulVisitState.Log("[BP-HideoutAlarm] controller bookkeeping skipped for unmanaged agent (crash averted)"); }
                    catch { }
                }
                return null; // suppress — the controller doesn't manage this agent
            }
            return __exception;
        }
    }
}
