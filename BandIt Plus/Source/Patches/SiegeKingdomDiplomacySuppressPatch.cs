using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace BandItPlus.Patches
{
    // General relation-NRE safety net (kept after the Bandit Siege v2 rebel-clan re-architect).
    // TaleWorlds' DefaultDiplomacyModel relation methods deref faction.Leader / hero ARGUMENTS without
    // null-guarding the inputs (the engine guards outputs only, confirmed by 1.4.5 decompile). During the
    // brief windows when BandIt Plus stands up an independent rebel/holdout clan (or the engine's own
    // rebellions), a transient null leader/hero can flow into these models. These two thin guards return a
    // neutral 0 instead of throwing — exactly the guard shipped bandit mods keep. No kingdom/StringId is
    // involved any more (the temp-kingdom approach is gone).
    //
    // VERSION-SAFE: targets resolved by name+arity via plural TargetMethods() — a miss skips the patch
    // (never aborts PatchAll). Auto-discovered by Harmony.PatchAll() in SubModuleClassEntry.
    internal static class RelationGuard
    {
        public static void Log(string msg) { try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Siege] relguard: " + msg); } catch { } }

        public static MethodBase Resolve(string typeName, string methodName, int paramCount, Type returnType)
        {
            try
            {
                var t = typeof(Clan).Assembly.GetType(typeName);
                if (t == null) { Log("type missing " + typeName); return null; }
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    if (m.Name == methodName && m.GetParameters().Length == paramCount && (returnType == null || m.ReturnType == returnType))
                    { Log("attached " + typeName + "." + methodName); return m; }
            }
            catch (Exception ex) { Log("resolve threw " + ex.Message); }
            Log("NOT FOUND " + typeName + "." + methodName + " (skipped)");
            return null;
        }

        // The exact NRE condition: a faction whose Leader (or Leader.Clan) is null.
        public static bool HasNullLeaderChain(IFaction f)
        {
            try { return f != null && (f.Leader == null || f.Leader.Clan == null); }
            catch { return true; }
        }
    }

    // DefaultDiplomacyModel.GetRelationScore(IFaction, IFaction, IFaction) -> float
    // Return neutral 0 when any faction has a null leader chain (the deref site).
    [HarmonyPatch]
    public static class RelationScoreGuardPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var m = RelationGuard.Resolve("TaleWorlds.CampaignSystem.GameComponents.DefaultDiplomacyModel", "GetRelationScore", 3, typeof(float));
            if (m != null) yield return m;
        }

        static bool Prefix(object[] __args, ref float __result)
        {
            try
            {
                if (__args != null)
                    for (int i = 0; i < __args.Length && i < 3; i++)
                        if (__args[i] is IFaction f && RelationGuard.HasNullLeaderChain(f)) { __result = 0f; return false; }
            }
            catch { }
            return true;
        }
    }

    // DefaultDiplomacyModel.GetEffectiveRelation(Hero, Hero) -> int
    // Return neutral 0 if either hero argument is null (the input the engine fails to guard).
    [HarmonyPatch]
    public static class EffectiveRelationGuardPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var m = RelationGuard.Resolve("TaleWorlds.CampaignSystem.GameComponents.DefaultDiplomacyModel", "GetEffectiveRelation", 2, typeof(int));
            if (m != null) yield return m;
        }

        static bool Prefix(object[] __args, ref int __result)
        {
            try
            {
                if (__args != null)
                    for (int i = 0; i < __args.Length && i < 2; i++)
                        if (__args[i] == null) { __result = 0; return false; }
            }
            catch { }
            return true;
        }
    }

    // Keep the v2 holdout clans (bp_rebel_clan_*) INDEPENDENT — block them from ever joining a kingdom as a
    // vassal or mercenary. We can't flag them IsBanditFaction (BanditSpawnCampaignBehavior.GetInfestedHideoutCount
    // KeyNotFoundException-crashes on unregistered bandit clans), so a kingdom-join block is the surgical fix for
    // the "Vassal of the Western Empire" recruitment. ChangeKingdomAction.ApplyByJoinToKingdom /
    // ApplyByJoinFactionAsMercenary both take the joining Clan first.
    [HarmonyPatch]
    public static class SiegeKingdomJoinBlockPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var t = typeof(Clan).Assembly.GetType("TaleWorlds.CampaignSystem.Actions.ChangeKingdomAction");
            if (t == null) { RelationGuard.Log("ChangeKingdomAction missing — join-block skipped"); yield break; }
            foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public))
                if (m.Name == "ApplyByJoinToKingdom" || m.Name == "ApplyByJoinFactionAsMercenary")
                {
                    var ps = m.GetParameters();
                    if (ps.Length >= 1 && ps[0].ParameterType == typeof(Clan)) { RelationGuard.Log("join-block attached -> " + m.Name); yield return m; }
                }
        }

        static bool Prefix(object[] __args)
        {
            try
            {
                if (__args != null && __args.Length > 0 && __args[0] is Clan c && c.StringId != null && c.StringId.StartsWith("bp_rebel_clan_", System.StringComparison.Ordinal))
                    return false; // skip the join — holdout stays independent
            }
            catch { }
            return true;
        }
    }

    // When the Bandit King holds a TOWN (not just a castle), the vanilla daily settlement tick tries to spawn
    // patrol parties for it and NREs — the independent, kingdom-less holdout clan + its cloned bandit culture
    // lack what SpawnPatrolParty dereferences (a kingdom / a culture with patrol troops). The holdout needs no
    // vanilla patrols, so skip the spawn for any settlement owned by a bp_rebel_clan_ holdout.
    [HarmonyPatch]
    public static class HoldoutPatrolSpawnSuppressPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var m = RelationGuard.Resolve("TaleWorlds.CampaignSystem.CampaignBehaviors.PatrolPartiesCampaignBehavior", "SpawnPatrolParty", 1, typeof(void));
            if (m != null) yield return m;
        }

        static bool Prefix(object[] __args)
        {
            try
            {
                if (__args != null && __args.Length > 0 && __args[0] is Settlement s
                    && s.OwnerClan != null && s.OwnerClan.StringId != null
                    && s.OwnerClan.StringId.StartsWith("bp_rebel_clan_", System.StringComparison.Ordinal))
                    return false; // skip patrol spawn for holdout-owned settlements
            }
            catch { }
            return true;
        }
    }

    // The Bandit King's holdout must be AUTHORITATIVELY at peace with all bandits — he is their overlord. The engine
    // starts/keeps every bandit faction at war with non-bandit factions and re-declares it each war-tick, so calling
    // SetNeutral/MakePeaceAction only flip-flops (and spams "peace declared" popups). This postfix forces
    // IsAtWarAgainstFaction = false whenever ONE side is a bp_rebel_clan_ holdout and the OTHER is any bandit faction
    // (vanilla IsBanditFaction OR the mod's IsBanditFaction=false custom bp_chief_clan_ clans). Silent + authoritative:
    // the holdout never aggros looters/bandits and they never aggro it, with no notifications. Gated to the holdout
    // only — no other faction's stance is affected.
    [HarmonyPatch]
    public static class HoldoutBanditPeacePatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var m = RelationGuard.Resolve("TaleWorlds.CampaignSystem.FactionManager", "IsAtWarAgainstFaction", 2, typeof(bool));
            if (m != null) yield return m;
        }

        static void Postfix(object[] __args, ref bool __result)
        {
            try
            {
                if (!__result || __args == null || __args.Length < 2) return; // already not-at-war — nothing to override
                var a = __args[0] as IFaction; var b = __args[1] as IFaction;
                if ((IsHoldout(a) && IsBandit(b)) || (IsHoldout(b) && IsBandit(a))) __result = false;
            }
            catch { }
        }

        static bool IsHoldout(IFaction f)
        {
            var c = f as Clan;
            return c != null && c.StringId != null && c.StringId.StartsWith("bp_rebel_clan_", System.StringComparison.Ordinal);
        }

        static bool IsBandit(IFaction f)
        {
            if (f == null) return false;
            try { if (f.IsBanditFaction) return true; } catch { }
            var c = f as Clan;
            return c != null && c.StringId != null && c.StringId.StartsWith("bp_chief_clan_", System.StringComparison.Ordinal);
        }
    }
}
