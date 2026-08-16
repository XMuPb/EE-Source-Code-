using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using BandItPlus.HideoutVisit;

namespace BandItPlus.Patches
{
    // v172 (2026-05-18): makes a hideout-assault battle fly the chief clan
    // banner on the in-battle morale bar.
    //
    // The hideout battle's defender Team copies its banner from the hideout
    // Settlement's PartyBase.Banner. PartyBase.Banner is a computed getter that
    // honours CustomBanner ONLY for mobile parties — for a settlement party it
    // returns the faction banner and ignores CustomBanner. So v171 setting
    // Settlement.Party.CustomBanner was inert (proven by the v171 diagnostic:
    // settlementParty.CustomBanner=SET, yet team banner != chief banner).
    //
    // Fix at the producer: postfix PartyBase.get_Banner so that for a hideout
    // settlement party WITH a CustomBanner (v171's BanditPartyBannerBehavior
    // sets it at session launch), the getter returns that CustomBanner. The
    // hideout battle Team then copies the chief banner AT CONSTRUCTION — so
    // there is no caching race (unlike v161's post-hoc Team.Banner write) and
    // no widget-tree hack (unlike v119's map-nameplate fix).
    //
    // Display-only: Banner is cosmetic; faction/clan/AI logic never reads it.
    // Auto-attached by Harmony.PatchAll() in SubModuleClassEntry.
    [HarmonyPatch(typeof(PartyBase), "Banner", MethodType.Getter)]
    public static class HideoutBannerPatch
    {
        private static bool _logged;

        private static void Postfix(PartyBase __instance, ref Banner __result)
        {
            try
            {
                // Mobile parties already honour CustomBanner — leave them alone.
                if (__instance == null || !__instance.IsSettlement) return;

                var settlement = __instance.Settlement;
                if (settlement == null || !settlement.IsHideout) return;

                var custom = __instance.CustomBanner;
                if (custom == null) return;

                __result = custom;
                if (!_logged)
                {
                    _logged = true;
                    HideoutPeacefulVisitState.Log(
                        "v172 HideoutBannerPatch: hideout settlement party Banner "
                        + "-> chief CustomBanner (" + settlement.Name + ")");
                }
            }
            catch { /* never throw out of a Harmony patch */ }
        }
    }
}
