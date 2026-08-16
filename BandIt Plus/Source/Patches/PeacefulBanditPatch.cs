using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BandItPlus.Patches
{
    [HarmonyPatch]
    public static class IsAtWarAgainstFactionPatch
    {
        private static readonly HashSet<string> BanditCultures = new HashSet<string>
        {
            "looters", "forest_bandits", "mountain_bandits", "steppe_bandits", "sea_raiders", "desert_bandits",
            "bp_frost_reavers", "bp_marsh_stalkers", "bp_highwaymen", "bp_slaver_caravans",
            "bp_fallen_legionaries", "bp_sky_raiders", "bp_steppe_wolves", "bp_pagan_cult",
        };

        private static int _hitCount = 0;
        private static int _overrideCount = 0;
        private static DateTime _lastLog = DateTime.MinValue;

        public static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("TaleWorlds.CampaignSystem.FactionManager");
            if (type == null) return null;
            return AccessTools.Method(type, "IsAtWarAgainstFaction");
        }

        public static bool Prepare()
        {
            var found = TargetMethod() != null;
            Log("Prepare: " + found);
            return found;
        }

        public static void Postfix(IFaction faction1, IFaction faction2, ref bool __result)
        {
            if (!__result) return;

            // v65 perf (2026-05-10): hoist the bandit-culture filter to the top.
            // FactionManager.IsAtWarAgainstFaction is called ~580/sec per in-game log;
            // ~95% of calls are player-vs-kingdom and have nothing to do with us.
            // Cheapest possible early-out is reading IFaction.Culture (property chain
            // on the args we already have) and HashSet.Contains. If neither faction
            // is bandit-cultured, this patch has nothing to do — bail before touching
            // MCMSettings, Hero.MainHero, or any other state.
            string c1 = faction1?.Culture?.StringId;
            string c2 = faction2?.Culture?.StringId;
            bool f1Bandit = c1 != null && BanditCultures.Contains(c1);
            bool f2Bandit = c2 != null && BanditCultures.Contains(c2);
            if (!f1Bandit && !f2Bandit) return;

            // 07-11 storyline guard: the tutorial's raider chain needs vanilla
            // war-state — the peace override broke the scripted encounters.
            if (BpStoryGuard.StoryTutorialActive()) return;

            try
            {
                if (MCMSettings.Instance == null || !MCMSettings.Instance.EnableMod) return;

                var playerClan = Hero.MainHero?.Clan;
                if (playerClan == null) return;

                // v66 (2026-05-12): kingdom-aware faction match. When the player
                // forms/leads a kingdom, FactionManager queries with the Kingdom as
                // the IFaction (Kingdom IS-A IFaction; Clan IS-A IFaction). Without
                // this guard, founding a kingdom silently breaks BandIt Plus peace
                // — playerClan != Kingdom, so the early-out fires and vanilla
                // kingdom-vs-bandit war leaks through.
                var playerKingdom = playerClan.Kingdom;
                bool f1IsPlayer = (faction1 == playerClan) || (playerKingdom != null && faction1 == playerKingdom);
                bool f2IsPlayer = (faction2 == playerClan) || (playerKingdom != null && faction2 == playerKingdom);
                if (!f1IsPlayer && !f2IsPlayer) return;

                // v65 pact wiring (2026-05-10): per-culture chief pact overrides the
                // MCM PeacefulEncounters toggle. If global peaceful is off but the
                // player has sworn a Tier-3 pact with this culture, the chief honors
                // the peace. If neither global peace nor pact applies, bail.
                string banditCid = f1IsPlayer ? c2 : c1;
                bool pactOverride = false;
                // Wave 4.19.0 "Blood & Banners": once the player has chosen an origin,
                // per-culture peace SUPERSEDES the legacy global PeacefulEncounters
                // toggle. Outlaw Blood = peace with all; Drifter/Lawkeeper = war until
                // peace is earned per culture (GrantPeace) or a Tier-3 pact is sworn.
                var origins = BandItPlus.Origins.BanditOriginBehavior.Instance;
                if (origins != null && origins.OriginChosen)
                {
                    bool peace = origins.IsAtPeaceWith(banditCid);
                    // The bp_ clans are name-only reflavors of vanilla bandit parties (a
                    // "Highwaymen" party's Culture is still "looters"). Peace is stored under the
                    // bp_ id, so also honor peace with any bp_ clan that reflavors THIS vanilla
                    // culture (BiomeEligibility map) — otherwise the picked/parleyed clan's
                    // parties stay hostile because the engine files them under the vanilla culture.
                    if (!peace)
                        foreach (var bp in BandItPlus.BiomeEligibility.GetReplacements(banditCid))
                            if (origins.IsAtPeaceWith(bp)) { peace = true; break; }
                    if (!peace)
                    {
                        var bdmO = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                        if (bdmO != null && bdmO.IsChiefPactSworn(banditCid))
                        {
                            peace = true;
                            pactOverride = true;
                        }
                    }
                    if (!peace) return;
                }
                else
                {
                    // Legacy path — origin not yet chosen (popup pending / old save).
                    bool peacefulMcm = MCMSettings.Instance.PeacefulEncounters;
                    if (!peacefulMcm)
                    {
                        var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                        if (bdm != null && bdm.IsChiefPactSworn(banditCid))
                            pactOverride = true;
                        if (!pactOverride) return;
                    }
                }

                _hitCount++;
                __result = false;
                _overrideCount++;

                if (MCMSettings.Instance.DebugLogging && (DateTime.Now - _lastLog).TotalSeconds > 5)
                {
                    _lastLog = DateTime.Now;
                    Log("Override active. hit=" + _hitCount + " override=" + _overrideCount
                        + " latest=" + banditCid + (pactOverride ? " (pact)" : ""));
                }
            }
            catch (Exception ex) { Log("Postfix exception: " + ex.Message); }
        }

        private static void Log(string msg)
        {
            try
            {
                File.AppendAllText(BandItPlus.Diagnostics.BpDiag.P("bp_peace_debug.log"),
                    "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] IsAtWarPatch: " + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    [HarmonyPatch]
    public static class IsAtConstantWarAgainstFactionPatch
    {
        private static readonly HashSet<string> BanditCultures = new HashSet<string>
        {
            "looters", "forest_bandits", "mountain_bandits", "steppe_bandits", "sea_raiders", "desert_bandits",
            "bp_frost_reavers", "bp_marsh_stalkers", "bp_highwaymen", "bp_slaver_caravans",
            "bp_fallen_legionaries", "bp_sky_raiders", "bp_steppe_wolves", "bp_pagan_cult",
        };

        private static int _overrideCount = 0;
        private static DateTime _lastLog = DateTime.MinValue;

        public static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("TaleWorlds.CampaignSystem.FactionManager");
            if (type == null) return null;
            return AccessTools.Method(type, "IsAtConstantWarAgainstFaction");
        }

        public static bool Prepare()
        {
            var found = TargetMethod() != null;
            try { File.AppendAllText(BandItPlus.Diagnostics.BpDiag.P("bp_peace_debug.log"),
                "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] ConstantWarPatch: Prepare: " + found + Environment.NewLine); } catch { }
            return found;
        }

        public static void Postfix(IFaction faction1, IFaction faction2, ref bool __result)
        {
            if (!__result) return;

            // v65 perf (2026-05-10): same hoist as IsAtWarAgainstFactionPatch — bail
            // before any MCM/Hero touches when neither faction is bandit-cultured.
            string c1 = faction1?.Culture?.StringId;
            string c2 = faction2?.Culture?.StringId;
            bool f1Bandit = c1 != null && BanditCultures.Contains(c1);
            bool f2Bandit = c2 != null && BanditCultures.Contains(c2);
            if (!f1Bandit && !f2Bandit) return;

            // 07-11 storyline guard: the tutorial's raider chain needs vanilla
            // war-state — the peace override broke the scripted encounters.
            if (BpStoryGuard.StoryTutorialActive()) return;

            try
            {
                if (MCMSettings.Instance == null || !MCMSettings.Instance.EnableMod) return;

                var playerClan = Hero.MainHero?.Clan;
                if (playerClan == null) return;

                // v66 (2026-05-12): kingdom-aware faction match — same rationale as
                // IsAtWarAgainstFactionPatch above. Founding a kingdom changes the
                // IFaction Bannerlord passes here from Clan to Kingdom.
                var playerKingdom = playerClan.Kingdom;
                bool f1IsPlayer = (faction1 == playerClan) || (playerKingdom != null && faction1 == playerKingdom);
                bool f2IsPlayer = (faction2 == playerClan) || (playerKingdom != null && faction2 == playerKingdom);
                if (!f1IsPlayer && !f2IsPlayer) return;

                // Wave 4.19.0 "Blood & Banners": origin per-culture peace supersedes
                // the global toggle once an origin is chosen (same logic as the
                // IsAtWarAgainstFactionPatch above).
                string banditCid2 = f1IsPlayer ? c2 : c1;
                var origins2 = BandItPlus.Origins.BanditOriginBehavior.Instance;
                if (origins2 != null && origins2.OriginChosen)
                {
                    bool peace2 = origins2.IsAtPeaceWith(banditCid2);
                    if (!peace2)
                        foreach (var bp in BandItPlus.BiomeEligibility.GetReplacements(banditCid2))
                            if (origins2.IsAtPeaceWith(bp)) { peace2 = true; break; }
                    if (!peace2)
                    {
                        var bdm2 = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                        if (bdm2 == null || !bdm2.IsChiefPactSworn(banditCid2)) return;
                    }
                }
                else
                {
                    if (!MCMSettings.Instance.PeacefulEncounters) return;
                }

                __result = false;
                _overrideCount++;

                if (MCMSettings.Instance.DebugLogging && (DateTime.Now - _lastLog).TotalSeconds > 5)
                {
                    _lastLog = DateTime.Now;
                    string latest = f1IsPlayer ? c2 : c1;
                    try { File.AppendAllText(BandItPlus.Diagnostics.BpDiag.P("bp_peace_debug.log"),
                        "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] ConstantWarPatch: Override count=" + _overrideCount + " latest=" + latest + Environment.NewLine); } catch { }
                }
            }
            catch (Exception ex) { try { File.AppendAllText(BandItPlus.Diagnostics.BpDiag.P("bp_peace_debug.log"), "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] ConstantWarPatch Postfix exception: " + ex.Message + Environment.NewLine); } catch { } }
        }
    }

    // 07-03 crash guard. Our IsAtWarAgainstFaction postfix makes at-peace bandit
    // cultures read as NOT-at-war — so the vanilla Attack button routes through
    // BeHostileAction.ApplyEncounterHostileAction's "declare war on a neutral"
    // branch, which runs ChangeRelationAction.ApplyPlayerRelation(
    // defender.MapFaction.Leader, -10). Bandit clans have no Leader → null hero
    // → NRE in DefaultDiplomacyModel.GetHeroesForEffectiveRelation → hard crash
    // (field-proven attacking the signature-quest column; latent for ANY
    // at-peace bandit party). Guard: in that exact geometry, do the vanilla
    // bookkeeping (exempt check + ApplyInternal) and skip only the
    // relation/war-declaration pair that cannot survive a leaderless faction.
    [HarmonyPatch]
    public static class EncounterHostileLeaderlessGuardPatch
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(TaleWorlds.CampaignSystem.Actions.BeHostileAction),
                "ApplyEncounterHostileAction");
        }

        public static bool Prepare()
        {
            var found = TargetMethod() != null;
            Log("Prepare: " + found);
            return found;
        }

        public static bool Prefix(
            TaleWorlds.CampaignSystem.Party.PartyBase attackerParty,
            TaleWorlds.CampaignSystem.Party.PartyBase defenderParty)
        {
            try
            {
                if (attackerParty == null || defenderParty == null) return true;
                if (attackerParty != TaleWorlds.CampaignSystem.Party.PartyBase.MainParty) return true;
                var defFaction = defenderParty.MapFaction;
                if (defFaction == null || defFaction.Leader != null) return true;   // vanilla path is safe
                if (attackerParty.MapFaction == defFaction) return true;            // vanilla skips the branch itself
                if (FactionManager.IsAtWarAgainstFaction(attackerParty.MapFaction, defFaction)) return true;

                // Exact crash geometry: player attacks a not-at-war party of a
                // LEADERLESS faction. Replicate vanilla minus the two null-lethal calls.
                if (Campaign.Current.Models.EncounterModel.IsEncounterExemptFromHostileActions(attackerParty, defenderParty))
                    return false;
                try
                {
                    AccessTools.Method(
                        typeof(TaleWorlds.CampaignSystem.Actions.BeHostileAction), "ApplyInternal")
                        ?.Invoke(null, new object[] { attackerParty, defenderParty, 6f });
                }
                catch (Exception aex)
                {
                    Log("ApplyInternal reflection threw " + aex.GetType().Name + ": " + aex.Message);
                }
                Log("crash averted: hostile action vs leaderless faction '" + defFaction.StringId
                    + "' — relation/war-declaration skipped");
                return false;
            }
            catch (Exception ex)
            {
                Log("Prefix exception (vanilla fallthrough): " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        private static void Log(string msg)
        {
            try
            {
                File.AppendAllText(BandItPlus.Diagnostics.BpDiag.P("bp_peace_debug.log"),
                    "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] EncounterHostileGuard: " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
