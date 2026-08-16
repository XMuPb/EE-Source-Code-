using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using BandItPlus.HideoutVisit;

namespace BandItPlus.Patches
{
    // v107 (2026-05-13): Phase 2 via Harmony postfix on Settlement.OwnerClan getter.
    //
    // Vanilla blocks programmatic ownership transfer for hideouts (the
    // ChangeOwnerOfSettlementAction guard rejects it), AND OwnerClan is a
    // computed property with no backing field we can reflection-write.
    //
    // Solution: postfix the getter — when ANY code (map nameplate, encyclopedia,
    // banner draw) asks for a hideout's OwnerClan, we return our chief's clan.
    //
    // Risk: vanilla code that DOES read OwnerClan (raid pathing, AI target
    // selection) will see chief.Clan instead. Enabled toggle lets us disable
    // instantly if breakage appears.
    [HarmonyPatch(typeof(Settlement), "OwnerClan", MethodType.Getter)]
    public static class HideoutOwnerClanPatch
    {
        // v108 (2026-05-13): DISABLED — replaced by UIExtenderEx ViewModelMixin
        // approach (see UI/BandItPlusNameplateMixin) which is targeted to the
        // map-nameplate VM only, not a global OwnerClan getter override.
        public static bool Enabled = false;
        private static bool _firstOverrideLogged;

        static void Postfix(Settlement __instance, ref Clan __result)
        {
            if (!Enabled || __instance == null || !__instance.IsHideout) return;
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.Heroes.BanditChiefRegistry>();
                if (bdm == null) return;
                var cultureId = __instance.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return;
                var chief = bdm.GetChief(cultureId);
                if (chief?.Clan == null) return;
                if (chief.Clan == __result) return;
                __result = chief.Clan;
                if (!_firstOverrideLogged)
                {
                    _firstOverrideLogged = true;
                    HideoutPeacefulVisitState.Log("v107 HideoutOwnerClanPatch: first override for "
                        + cultureId + " → " + (chief.Clan.Name?.ToString() ?? "?"));
                }
            }
            catch { }
        }
    }
}
