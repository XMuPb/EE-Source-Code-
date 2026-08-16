using HarmonyLib;
using System;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.MapEvents;

namespace BandItPlus.Patches
{
    // Wave 4.15.0-fix41 (2026-06-10): pure-observer Harmony patches + event listeners
    // that log every raid-related call. User goal: trigger a real VANILLA raid as the
    // PLAYER (right-click village → "Raid the village") and observe in the log which
    // APIs the engine calls in order. Once we see the real call sequence, we can
    // replicate it for mod-spawned parties.
    //
    // PREFIX patches NEVER return false — these are pure observers, never block.

    public static class RaidLifecycleTracePatch
    {
        // Diagnostic trace toggle. This whole file is a pure-observer trace from a COMPLETED investigation
        // (fix41: learn the vanilla raid call sequence so we could replicate it). Left OFF by default so it
        // stops flooding the debug log (it had grown to ~500 MB). Flip to true only to re-run that raid-API probe.
        internal static bool Enabled = false;

        private static void Log(string msg)
        {
            if (!Enabled) return;
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-RaidTrace] " + msg); }
            catch { }
        }

        // Vanilla parameter is named `raider` (not `raiderParty`) — Harmony binds by name.
        [HarmonyPatch(typeof(ChangeVillageStateAction), "ApplyBySettingToBeingRaided")]
        public static class TraceApplyBySettingToBeingRaided
        {
            static void Prefix(Settlement settlement, MobileParty raider)
            {
                Log("ChangeVillageStateAction.ApplyBySettingToBeingRaided  " +
                    "settlement='" + (settlement?.Name?.ToString() ?? "<null>") + "'  " +
                    "raider='" + (raider?.StringId ?? "<null>") + "'  " +
                    "leader='" + (raider?.LeaderHero?.Name?.ToString() ?? "<null>") + "'  " +
                    "faction='" + (raider?.MapFaction?.Name?.ToString() ?? "<null>") + "'");
            }
        }

        [HarmonyPatch(typeof(ChangeVillageStateAction), "ApplyBySettingToLooted")]
        public static class TraceApplyBySettingToLooted
        {
            static void Prefix(Settlement settlement, MobileParty raider)
            {
                Log("ChangeVillageStateAction.ApplyBySettingToLooted  " +
                    "settlement='" + (settlement?.Name?.ToString() ?? "<null>") + "'  " +
                    "raider='" + (raider?.StringId ?? "<null>") + "'");
            }
        }

        // For patches where I don't know exact vanilla param names, use __args array
        // (Harmony's name-agnostic accessor) so a name guess won't crash on attach.
        [HarmonyPatch(typeof(EnterSettlementAction), "ApplyForParty")]
        public static class TraceEnterSettlementApplyForParty
        {
            static void Prefix(object[] __args)
            {
                var p = __args != null && __args.Length > 0 ? __args[0] as MobileParty : null;
                var s = __args != null && __args.Length > 1 ? __args[1] as Settlement : null;
                Log("EnterSettlementAction.ApplyForParty  " +
                    "party='" + (p?.StringId ?? "<null>") + "'  " +
                    "settlement='" + (s?.Name?.ToString() ?? "<null>") + "'  " +
                    "VS=" + (s?.Village?.VillageState.ToString() ?? "<n/a>"));
            }
        }

        [HarmonyPatch(typeof(LeaveSettlementAction), "ApplyForParty")]
        public static class TraceLeaveSettlementApplyForParty
        {
            static void Prefix(object[] __args)
            {
                var p = __args != null && __args.Length > 0 ? __args[0] as MobileParty : null;
                Log("LeaveSettlementAction.ApplyForParty  " +
                    "party='" + (p?.StringId ?? "<null>") + "'  " +
                    "wasInSettlement='" + (p?.CurrentSettlement?.Name?.ToString() ?? "<null>") + "'");
            }
        }

        // NOTE THE BARE [HarmonyPatch] — it must NOT carry (typeof(...), "name") here.
        //
        // 2026-07-26 crash: this class was written as
        //     [HarmonyPatch(typeof(MobileParty), "SetMoveRaidSettlement")]
        // *together with* TargetMethod() below, and Harmony rejects that combination
        // outright at PatchAll() time:
        //
        //     "You cannot combine TargetMethod, TargetMethods or [HarmonyPatchAll]
        //      with individual annotations [SetMoveRaidSettlement]"
        //         at HarmonyLib.PatchClassProcessor.BulkPatch(...)
        //
        // which threw out of PatchAll and killed startup. Compilation cannot catch
        // this — Harmony validates at runtime — so a green build proves nothing here.
        //
        // The attribute now carries no arguments, so TargetMethod() alone decides the
        // target. That matches every other dynamically-targeted patch in this assembly
        // (AtcVolunteerModelPatch, BanditAiSuppressionPatch, PeacefulBanditPatch, ...).
        [HarmonyPatch]
        public static class TraceSetMoveRaidSettlement
        {
            // MobileParty.SetMoveRaidSettlement does not exist on every supported game
            // build (absent on 1.3.15). Without this guard Harmony cannot resolve the
            // target and PatchAll() throws — which takes down EVERY patch in this
            // assembly, not just the tracer. Returning false skips this patch cleanly.
            static bool Prepare()
            {
                return BandItPlus.Compat.PartyOrderCompat.ResolveRaidMethodForPatching() != null;
            }

            // Bind to whatever overload this build actually exposes rather than
            // assuming the 1.4.5 arity.
            static System.Reflection.MethodBase TargetMethod()
            {
                return BandItPlus.Compat.PartyOrderCompat.ResolveRaidMethodForPatching();
            }

            static void Prefix(MobileParty __instance, object[] __args)
            {
                var s = __args != null && __args.Length > 0 ? __args[0] as Settlement : null;
                // fix42: extended attribute dump — capture every field the engine might
                // use to validate raid eligibility, so we can diff a vanilla lord's
                // SetMoveRaidSettlement call against our mod-spawned bandit's call.
                Log("MobileParty.SetMoveRaidSettlement  " +
                    "party='" + (__instance?.StringId ?? "<null>") + "'  " +
                    "leader='" + (__instance?.LeaderHero?.Name?.ToString() ?? "<null>") + "'  " +
                    "isBandit=" + (__instance?.IsBandit.ToString() ?? "<n/a>") + "  " +
                    "isBanditBossParty=" + (__instance?.IsBanditBossParty.ToString() ?? "<n/a>") + "  " +
                    "isLordParty=" + (__instance?.IsLordParty.ToString() ?? "<n/a>") + "  " +
                    "mapFaction='" + (__instance?.MapFaction?.Name?.ToString() ?? "<null>") + "'  " +
                    "factionIsBandit=" + (__instance?.MapFaction?.IsBanditFaction.ToString() ?? "<n/a>") + "  " +
                    "actualClan='" + (__instance?.ActualClan?.Name?.ToString() ?? "<null>") + "'  " +
                    "clanIsBandit=" + (__instance?.ActualClan?.IsBanditFaction.ToString() ?? "<n/a>") + "  " +
                    "homeSettlement='" + (__instance?.HomeSettlement?.Name?.ToString() ?? "<null>") + "'  " +
                    "target='" + (s?.Name?.ToString() ?? "<null>") + "'");
            }
        }

        // fix42: trace MapEvent.IsRaid getter to see if engine queries it during raid setup.
        // This sometimes catches the validation moment.
        [HarmonyPatch(typeof(MapEvent), "IsRaid", MethodType.Getter)]
        public static class TraceMapEventIsRaid
        {
            static void Postfix(MapEvent __instance, bool __result)
            {
                if (__result)
                {
                    var attacker = __instance?.AttackerSide?.LeaderParty;
                    Log("MapEvent.IsRaid getter returned TRUE  " +
                        "eventType=" + __instance?.EventType + "  " +
                        "attackerParty='" + (attacker?.MobileParty?.StringId ?? attacker?.Name?.ToString() ?? "<null>") + "'  " +
                        "attackerIsBandit=" + (attacker?.MobileParty?.IsBandit.ToString() ?? "<n/a>"));
                }
            }
        }
    }

    public class RaidLifecycleTraceBehavior : TaleWorlds.CampaignSystem.CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            try
            {
                CampaignEvents.VillageBeingRaided.AddNonSerializedListener(this, OnVillageBeingRaided);
                CampaignEvents.VillageStateChanged.AddNonSerializedListener(this, OnVillageStateChanged);
                CampaignEvents.MapEventStarted.AddNonSerializedListener(this, OnMapEventStarted);
                CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BP-RaidTrace] Event listeners registered: VillageBeingRaided, VillageStateChanged, MapEventStarted, MapEventEnded");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BP-RaidTrace] event-register threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public override void SyncData(IDataStore dataStore) { /* not saveable */ }

        private static void Log(string msg)
        {
            if (!RaidLifecycleTracePatch.Enabled) return;
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-RaidTrace] " + msg); }
            catch { }
        }

        private void OnVillageBeingRaided(Village village)
        {
            Log("EVENT VillageBeingRaided  village='" + village?.Name + "'  " +
                "VS=" + village?.VillageState + "  " +
                "lastAttacker='" + (village?.Settlement?.LastAttackerParty?.StringId ?? "<null>") + "'");
        }

        private void OnVillageStateChanged(Village village,
            Village.VillageStates oldState,
            Village.VillageStates newState,
            MobileParty raiderParty)
        {
            Log("EVENT VillageStateChanged  village='" + village?.Name + "'  " +
                oldState + " → " + newState + "  " +
                "raider='" + (raiderParty?.StringId ?? "<null>") + "'  " +
                "raiderLeader='" + (raiderParty?.LeaderHero?.Name?.ToString() ?? "<null>") + "'  " +
                "raiderFaction='" + (raiderParty?.MapFaction?.Name?.ToString() ?? "<null>") + "'");
        }

        private void OnMapEventStarted(MapEvent mapEvent, PartyBase attacker, PartyBase defender)
        {
            Log("EVENT MapEventStarted  type=" + mapEvent?.EventType + "  " +
                "isRaid=" + mapEvent?.IsRaid + "  " +
                "attacker='" + (attacker?.MobileParty?.StringId ?? attacker?.Name?.ToString() ?? "<null>") + "'  " +
                "defender='" + (defender?.Settlement?.Name?.ToString() ?? defender?.MobileParty?.StringId ?? defender?.Name?.ToString() ?? "<null>") + "'");
        }

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            Log("EVENT MapEventEnded  type=" + mapEvent?.EventType + "  " +
                "isRaid=" + mapEvent?.IsRaid + "  " +
                "winningSide=" + mapEvent?.WinningSide);
        }
    }
}
