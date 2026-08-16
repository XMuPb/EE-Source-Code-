using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Settlements.Locations;
using TaleWorlds.Core;

namespace BandItPlus.HideoutVisit
{
    // Wave 3c.4 Phase 6 (research-driven retry — task 3 of 10).
    //
    // Previous Phase 4.5 attempts failed because we called MissionState.OpenNew directly,
    // bypassing PlayerEncounter.LocationEncounter setup. Vanilla village mission behaviors all
    // assume PlayerEncounter.Current.LocationEncounter is non-null and a proper subclass of
    // LocationEncounter — that's why our movement crashed silently in native code.
    //
    // This class subclasses HideoutEncounter (vanilla) and overrides CreateAndOpenMissionController
    // to call CampaignMission.OpenVillageMission(...) instead of the unimplemented base. That
    // routes the player through vanilla's tested 27-behavior village mission stack — same one
    // used to walk into actual villages — which already includes MissionAgentHandler,
    // MissionLocationLogic, mount/dismount handlers, conversation, and boundary placers.
    //
    // Verified signatures (via reflection on TaleWorlds.CampaignSystem.dll, 1.2.x):
    //   HideoutEncounter ctor: HideoutEncounter(Settlement settlement)
    //   LocationEncounter.CreateAndOpenMissionController →
    //     IMission CreateAndOpenMissionController(Location nextLocation,
    //                                             Location previousLocation = null,
    //                                             CharacterObject talkToChar = null,
    //                                             string playerSpecialSpawnTag = null)
    //   CampaignMission.OpenVillageMission(string scene, Location location, CharacterObject talkToChar) -> IMission
    //
    // Future tasks (4-10) will Harmony-patch PlayerEncounter.CreateLocationEncounter to swap in
    // this subclass, override IsTavern/IsWorkshopLocation, register a SaveableTypeDefiner, etc.
    public class WalkableHideoutEncounter : HideoutEncounter
    {
        // BUG H4 FIX (2026-04-30): Defensive parameterless ctor for save-deserialization safety.
        // Bannerlord's save system typically uses FormatterServices.GetUninitializedObject (which
        // bypasses ctors), but registered SaveableTypeDefiner types can also be instantiated via
        // reflection in some code paths — having a non-public parameterless ctor available makes
        // both paths safe. The :base(null) call relies on HideoutEncounter's ctor not dereferencing
        // the settlement arg; verified safe in 1.2.x by inspection. If a future game patch crashes
        // here, the user only sees a save-load failure (testable), not an in-game break.
        protected WalkableHideoutEncounter() : base(null) { }

        public WalkableHideoutEncounter(Settlement encounterSettlement) : base(encounterSettlement)
        {
            HideoutPeacefulVisitState.Log("WalkableHideoutEncounter: ctor for hideout=" +
                (encounterSettlement?.StringId ?? "null"));
        }

        public override IMission CreateAndOpenMissionController(
            Location nextLocation,
            Location previousLocation = null,
            CharacterObject talkToChar = null,
            string playerSpecialSpawnTag = null)
        {
            HideoutPeacefulVisitState.Log("WalkableHideoutEncounter.CreateAndOpenMissionController: " +
                "nextLocation=" + (nextLocation?.StringId ?? "null") +
                ", previousLocation=" + (previousLocation?.StringId ?? "null") +
                ", talkToChar=" + (talkToChar?.StringId ?? "null") +
                ", spawnTag=" + (playerSpecialSpawnTag ?? "null"));

            try
            {
                if (nextLocation == null)
                {
                    HideoutPeacefulVisitState.Log("WalkableHideoutEncounter: BAIL — nextLocation is null, falling through to base");
                    return base.CreateAndOpenMissionController(nextLocation, previousLocation, talkToChar, playerSpecialSpawnTag);
                }

                // Resolve scene name from the Location (set per-settlement via XML, e.g. "steppe_hideout_2_sv")
                string sceneName = nextLocation.GetSceneName(0);
                HideoutPeacefulVisitState.Log("WalkableHideoutEncounter: scene resolved = '" + (sceneName ?? "null") + "'");

                if (string.IsNullOrEmpty(sceneName))
                {
                    HideoutPeacefulVisitState.Log("WalkableHideoutEncounter: BAIL — empty scene name, falling through to base");
                    return base.CreateAndOpenMissionController(nextLocation, previousLocation, talkToChar, playerSpecialSpawnTag);
                }

                HideoutPeacefulVisitState.Log("WalkableHideoutEncounter: calling CampaignMission.OpenVillageMission");
                var mission = CampaignMission.OpenVillageMission(sceneName, nextLocation, talkToChar);
                HideoutPeacefulVisitState.Log("WalkableHideoutEncounter: OpenVillageMission returned " +
                    (mission == null ? "null" : "IMission instance"));
                return mission;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("WalkableHideoutEncounter EXCEPTION: " +
                    ex.GetType().Name + ": " + ex.Message + " | stack=" + ex.StackTrace);
                throw;
            }
        }
    }
}
