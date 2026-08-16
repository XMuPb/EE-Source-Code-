using System;
using BandItPlus.HideoutVisit;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Settlements;

namespace BandItPlus.Patches
{
    // Wave 3c.4 Phase 6 (research-driven retry — task 4 of 10).
    //
    // Vanilla `PlayerEncounter.CreateLocationEncounter(Settlement)` is a static method that builds
    // a LocationEncounter subclass appropriate to the settlement type (Town/Village/Castle/Hideout)
    // and assigns it to the static property `PlayerEncounter.LocationEncounter`.
    //
    // For hideouts, vanilla creates a bare HideoutEncounter whose CreateAndOpenMissionController
    // is unimplemented (falls through to the abstract base) — that's the silent native crash
    // we hit in Phase 4.5 attempts.
    //
    // This Postfix runs AFTER vanilla's CreateLocationEncounter and, when our peaceful flag is set
    // for a hideout settlement, replaces the encounter with our WalkableHideoutEncounter subclass
    // (which routes to OpenVillageMission instead). The setter on PlayerEncounter.LocationEncounter
    // is public so we can assign directly.
    //
    // Verified signatures (TaleWorlds.CampaignSystem.dll, 1.2.x):
    //   PlayerEncounter.CreateLocationEncounter(Settlement) — static void
    //   PlayerEncounter.LocationEncounter — static property with public setter
    [HarmonyPatch(typeof(PlayerEncounter), "CreateLocationEncounter")]
    public static class CreateLocationEncounterPatch
    {
        public static void Postfix(Settlement settlement)
        {
            try
            {
                if (settlement == null) return;
                if (!settlement.IsHideout) return;
                if (!HideoutPeacefulVisitState.Active)
                {
                    // Not our peaceful flow — let vanilla's HideoutEncounter stand
                    return;
                }

                HideoutPeacefulVisitState.Log("CreateLocationEncounterPatch: peaceful flag set for hideout=" + settlement.StringId + " — swapping in WalkableHideoutEncounter");

                // BUG M12 NOTE (2026-04-30): vanilla just constructed a HideoutEncounter and
                // assigned it to LocationEncounter; we overwrite without explicit Finish/cleanup.
                // Risk: if vanilla's setter or HideoutEncounter ctor subscribed to events, those
                // subscriptions outlive the swap and could fire on a dead encounter. Mitigations:
                //   (a) log the swap so the original encounter is traceable in debug
                //   (b) swap IMMEDIATELY after the setter, before any tick gives the original
                //       encounter a chance to register with timed systems
                //   (c) the original encounter is GC'd once references drop — short-lived leak
                // True fix would require either calling base.Finish() (risky — may early-end the
                // encounter for the player) or replicating PlayerEncounter.LocationEncounter
                // setter side-effects on our subclass instance. Both are out of scope for this
                // batch. Leaving as documented known-low-risk leak.
                var prior = PlayerEncounter.LocationEncounter;
                if (prior != null && !(prior is WalkableHideoutEncounter))
                {
                    HideoutPeacefulVisitState.Log("CreateLocationEncounterPatch: replacing prior encounter type=" + prior.GetType().Name + " (will be GC'd)");
                }

                var walkable = new WalkableHideoutEncounter(settlement);
                PlayerEncounter.LocationEncounter = walkable;
                HideoutPeacefulVisitState.Log("CreateLocationEncounterPatch: PlayerEncounter.LocationEncounter swap COMPLETE");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("CreateLocationEncounterPatch EXCEPTION: " +
                    ex.GetType().Name + ": " + ex.Message + " | stack=" + ex.StackTrace);
                // Don't rethrow — let vanilla flow continue with the original encounter
            }
        }
    }
}
