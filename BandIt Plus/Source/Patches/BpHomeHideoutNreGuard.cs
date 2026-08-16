using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem.CampaignBehaviors;   // BanditSpawnCampaignBehavior
using TaleWorlds.CampaignSystem.Settlements;          // Hideout
using BandItPlus.HideoutVisit;                        // HideoutPeacefulVisitState.Log

namespace BandItPlus.Patches
{
    // Wave 4.34 — guard a NATIVE NRE exposed by BP's captivity flow.
    //
    // BanditCaptivityBehavior marches the captor (e.g. a 'deserters'/'looter' party that carries a
    // BanditPartyComponent) into a hideout via SetMoveGoToSettlement. On entry the engine runs:
    //   Hideout.OnPartyEntered -> BanditPartyComponent.SetHomeHideout(this)
    //     -> CampaignEvents.OnHomeHideoutChanged(this, oldHomeHideout)
    //       -> BanditSpawnCampaignBehavior.OnHomeHideoutChanged(component, oldHomeHideout)
    // whose first line dereferences `oldHomeHideout.Settlement`. Looter/deserters parties are built with
    // their [SaveableProperty] Hideout left null, so `oldHomeHideout` is null on first hideout entry -> NRE
    // -> crash on the campaign tick. (Vanilla never hits this because those party types don't path to hideouts.)
    //
    // Scoped finalizer: swallow ONLY this exact case (NullReferenceException + oldHomeHideout == null) and
    // re-throw anything else, so no unrelated native bug is masked. When there is no prior hideout there is
    // nothing to decrement, so suppressing the density-cap bookkeeping loses nothing.
    [HarmonyPatch(typeof(BanditSpawnCampaignBehavior), "OnHomeHideoutChanged")]
    public static class BpHomeHideoutNreGuard
    {
        static Exception Finalizer(Exception __exception, Hideout oldHomeHideout)
        {
            if (__exception is NullReferenceException && oldHomeHideout == null)
            {
                try { HideoutPeacefulVisitState.Log("[BP-Jailbreak] suppressed native OnHomeHideoutChanged NRE (oldHomeHideout null; fresh looter/deserters captor entering hideout)"); } catch { }
                return null; // swallow only this benign, mod-induced NRE
            }
            return __exception; // re-throw everything else
        }
    }

    // 2026-07-02 crash guard (Makeb, 00:10:07): Helpers.SettlementHelper.IsGarrisonStarving
    // derefs its Settlement parameter with NO null check (v1.4.6 IL: first op is
    // settlement.IsStarving). A garrison party whose CurrentSettlement went null — e.g. an
    // ownership flip landing while that garrison was engaged in a MapEvent — NREs there from
    // DefaultPartyMoraleModel.GetEffectivePartyMorale during MapEvent.SimulateBattleRound and
    // hard-crashes the campaign tick. The ROOT fix is BP's defer-while-battle guards in
    // BanditRebelSiegeHelper.Capture / BanditRebellionTrigger.TryStartRebellion; this prefix is
    // the defense-in-depth layer so no vanilla/other-mod race can ever crash this path again.
    // A null settlement means "no settlement to starve in" => false is the semantically
    // correct result, identical to what the callee returns for a Town-less settlement.
    [HarmonyPatch(typeof(global::Helpers.SettlementHelper), "IsGarrisonStarving")]
    public static class GarrisonStarvingNullGuardPatch
    {
        static bool Prefix(Settlement settlement, ref bool __result)
        {
            if (settlement == null)
            {
                __result = false;
                try { HideoutPeacefulVisitState.Log("[BP-Guard] IsGarrisonStarving called with null settlement — returned false (garrison detached from settlement; crash averted)"); } catch { }
                return false; // skip the original (it would NRE)
            }
            return true;
        }
    }
}
