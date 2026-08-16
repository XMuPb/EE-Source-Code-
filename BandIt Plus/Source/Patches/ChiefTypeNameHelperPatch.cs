using HarmonyLib;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Localization;

namespace BandItPlus.Patches
{
    // Wave 4.11 (2026-05-07): override the encyclopedia "Type:" line displayed in the
    // hover tooltip for our 14 named bandit chiefs with their per-clan ChiefTitle from
    // CultureProfileRegistry.<cultureId>.ChiefTitle ("Reaver-Chief", "Stalker-Matriarch",
    // "Toll-Reeve", etc.) instead of the default "Minor Faction Hero" string vanilla
    // produces (correct but flavorless).
    //
    // Target — discovered via DLL decompilation:
    //   Helpers.HeroHelper.GetCharacterTypeName(Hero hero) -> TextObject
    //
    // The vanilla method has a fixed precedence chain (IsArtisan/IsGangLeader/IsHeadman/
    // ... /IsMinorFactionHero/IsLord) and returns the first match's localized label.
    // We postfix it: if the hero is one of our registered chiefs, replace the vanilla
    // result with the chief's authored ChiefTitle. Doesn't touch state, doesn't affect
    // any non-chief hero, no NRE risk — pure display-only string substitution.
    //
    // Auto-attached by Harmony PatchAll() at mod load via the [HarmonyPatch] attribute.
    [HarmonyPatch(typeof(HeroHelper), nameof(HeroHelper.GetCharacterTypeName))]
    public static class ChiefTypeNameHelperPatch
    {
        public static void Postfix(Hero hero, ref TextObject __result)
        {
            try
            {
                if (hero == null) return;
                // The v2 holdout Bandit King (bp_rebel_clan_*) is not a registry chief, but he must show his
                // title in the Type row instead of "Unknown".
                if (hero.Clan != null && hero.Clan.StringId != null && hero.Clan.StringId.StartsWith("bp_rebel_clan_"))
                {
                    __result = new TextObject(hero.Clan.Leader == hero ? "{=bp_chieftypenamehelperpatch_001}Bandit King" : "{=bp_chieftypenamehelperpatch_002}Bandit Lieutenant");
                    return;
                }
                var reg = BandItPlus.Heroes.BanditChiefRegistry.Instance;
                if (reg == null || !reg.IsRegisteredChief(hero)) return;

                var cultureId = hero.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return;

                if (!BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out var profile)
                    || profile == null
                    || string.IsNullOrEmpty(profile.ChiefTitle))
                    return;

                __result = new TextObject("{=!}" + profile.ChiefTitle);
            }
            catch { /* never throw from a tooltip-helper postfix */ }
        }
    }
}
