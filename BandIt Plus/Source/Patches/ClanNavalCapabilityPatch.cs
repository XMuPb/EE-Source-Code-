using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;

namespace BandItPlus.Patches
{
    // Wave 4.11-fix v34 (2026-05-07): suppress NRE in Clan.HasNavalNavigationCapability
    // for our bp_chief_clan_* clans. The vanilla getter dereferences settlements/parties
    // that our partially-init clans don't have. MarriageOfferCampaignBehavior.DailyTickClan
    // calls this getter for ALL non-bandit clans every campaign-day → crashes.
    //
    // We can't flip Clan.IsBanditFaction=true (Wave 4.9 set it false specifically so the
    // clans appear in the encyclopedia). Can't unset IsMinorFaction without other side
    // effects. The cleanest fix is a Harmony prefix that short-circuits the getter for
    // our specific clans only — returns false (no naval capability) without ever
    // entering the buggy enumeration. Vanilla code paths that consult this getter will
    // see "no naval capability" which is a safe, conservative answer for our chiefs.
    //
    // Auto-attached by Harmony's PatchAll() at mod load.
    //
    // v212-rollback-step-1 (2026-05-31): [HarmonyPatch] attribute COMMENTED OUT to test
    // whether THIS patch is the cause of SaveResultType.GeneralFailure on save. Audit
    // synthesis flagged it HIGH-confidence: the Prefix returns false (skips vanilla
    // body) on a v1.4.1-introduced naval API getter; the v1.4.1 save serializer may
    // depend on the vanilla lazy-init that we skip, causing GeneralFailure during the
    // serialize-Clan phase. RE-ENABLE if save still fails after this test.
    // v218 (2026-05-31): RE-ENABLED. v212 bisect proved this patch was NOT the cause
    // of the save NRE — actual cause was a missing ConstructContainerDefinition(
    // typeof(Dictionary<string, double>)) — see BandItPlusSaveableTypeDefiner.cs +
    // memory note bannerlord-construct-container-definition. This patch's anti-NRE
    // role (suppressing MarriageOfferCampaignBehavior.DailyTickClan crashes on our
    // bp_chief_clan_* partial-init clans) is still needed.
    [HarmonyPatch(typeof(Clan), nameof(Clan.HasNavalNavigationCapability), MethodType.Getter)]
    public static class ClanNavalCapabilityPatch
    {
        public static bool Prefix(Clan __instance, ref bool __result)
        {
            try
            {
                // Our chief clans (bp_chief_clan_*) are synthetic land-bandit clans with no naval data,
                // and the vanilla getter NREs dereferencing settlements/parties they lack — always suppress.
                if (__instance != null
                    && __instance.StringId != null
                    && __instance.StringId.StartsWith("bp_chief_clan_"))
                {
                    __result = false;
                    return false; // skip vanilla getter body — no NRE
                }

                // The v2 rebel-clan-siege holdout clans (bp_rebel_clan_*) — the Bandit King — adopt the
                // captured fief's REAL culture (crash-safe) and CAN become naval once their lord parties
                // own ships. If NO party owns a ship yet, suppress (return false, no NRE). Once a party
                // owns ships, fall through to vanilla so the engine reports REAL naval capability.
                if (__instance != null
                    && __instance.StringId != null
                    && __instance.StringId.StartsWith("bp_rebel_clan_"))
                {
                    bool ownsShip = false;
                    try
                    {
                        foreach (var wpc in __instance.WarPartyComponents)
                        {
                            var mp = wpc?.MobileParty;
                            if (mp?.Party?.Ships != null && mp.Party.Ships.Count > 0) { ownsShip = true; break; }
                        }
                    }
                    catch { }
                    if (!ownsShip) { __result = false; return false; }
                    return true; // let vanilla report real naval capability
                }
            }
            catch { /* defensive — fall through to vanilla */ }
            return true; // let vanilla run for all other clans
        }
    }
}
