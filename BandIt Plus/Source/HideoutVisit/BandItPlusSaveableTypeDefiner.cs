using System.Collections.Generic;
using BandItPlus.HideoutVisit;
using BandItPlus.Quests;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.SaveSystem;

namespace BandItPlus.HideoutVisit
{
    // Wave 3c.4 Phase 6 (research-driven, task 8 of 10) — Save-game safety.
    //
    // When the player is inside a peaceful hideout walk and saves the game, Bannerlord serializes
    // PlayerEncounter.Current.LocationEncounter. That field's runtime type is our
    // WalkableHideoutEncounter (a custom subclass of vanilla HideoutEncounter). Without
    // registration via SaveableTypeDefiner, the save system can't serialize/deserialize the
    // custom subclass — load fails or restores as a plain HideoutEncounter (which would crash
    // again on subsequent CreateAndOpenMissionController calls).
    //
    // SaveableTypeDefiner pattern (per TaleWorlds.SaveSystem 1.2.x):
    //   1. Subclass SaveableTypeDefiner with a base save ID (per research recommendation > 100,000
    //      to avoid collision with vanilla / other mods' IDs).
    //   2. Override DefineClassTypes() and call AddClassDefinition(typeof(YourClass), localId).
    //   3. Bannerlord's save system auto-discovers all SaveableTypeDefiner subclasses with
    //      parameterless constructors at assembly load.
    //
    // Base ID 987_650 is chosen high enough to be safely above vanilla and most mod ranges.
    public class BandItPlusSaveableTypeDefiner : SaveableTypeDefiner
    {
        // Base ID for this mod's saveable types. Each AddClassDefinition adds an offset.
        public BandItPlusSaveableTypeDefiner() : base(987_650) { }

        protected override void DefineClassTypes()
        {
            // v214b-rollback-test (2026-05-31): TEMPORARILY DISABLED to test Agent A's
            // HIGH-confidence #1 finding — WalkableHideoutEncounter inherits from vanilla
            // HideoutEncounter, whose serialization schema may have changed in Bannerlord
            // v1.4.1 (War Sails). User confirms fresh-campaign save (test α) ALSO fails,
            // which points at registration-time failure (assembly load), not state corruption.
            //
            // EXPECTED OUTCOMES:
            //   - If save NOW works → confirms base-class schema drift is the cause;
            //     next step is to drop the subclass or stop persisting it.
            //   - If save STILL FAILS → re-enable this line and disable the 4 quest
            //     registrations (BanditTrustQuest, BanditVendorQuest, BanditNamedTargetQuest,
            //     BanditSlaverQuest) ONE AT A TIME to bisect.
            //
            // save034 risk: if save034 was taken mid-peaceful-hideout-walk, load may break
            // in a NEW way. Independent of α test outcome (fresh campaign).
            // v218 (2026-05-31): RE-ENABLED. v214 bisect proved WalkableHideoutEncounter
            // was NOT the cause of the save NRE — the actual cause was a missing
            // ConstructContainerDefinition(typeof(Dictionary<string, double>)) registered
            // in DefineContainerDefinitions() below. Walkable peaceful hideout encounters
            // need this saveable subclass back to survive save/load.
            AddClassDefinition(typeof(WalkableHideoutEncounter), 1);
            // Wave 4.7: trust quest type so journal entries persist across save/load.
            AddClassDefinition(typeof(BanditTrustQuest), 2);
            // Wave 4.11-fix v52 (2026-05-08): vendor quest — same persistence pattern.
            AddClassDefinition(typeof(BanditVendorQuest), 3);
            // Wave 4.11-fix v61 (2026-05-08): chief Tier 3 named-target hit quest.
            AddClassDefinition(typeof(BanditNamedTargetQuest), 4);
            // Wave 4.12 (2026-05-10): slaver compound multi-objective quest.
            AddClassDefinition(typeof(BanditSlaverQuest), 5);
            // Wave 5 (Chief Recruitment): three-chapter recruitment questline.
            // Offset 6 confirmed unused as of v218 — existing offsets are 1-5
            // (WalkableHideoutEncounter, BanditTrustQuest, BanditVendorQuest,
            // BanditNamedTargetQuest, BanditSlaverQuest). Per spec R7 the
            // SaveableType is ALWAYS registered regardless of the
            // MCMSettings.EnableChiefRecruitment toggle so saves taken with the
            // toggle in either state load cleanly.
            AddClassDefinition(typeof(BanditAllianceQuest), 6);
            // 2026-06-03 story-expansion: Tier-3 raid-village quest. Offset 7.
            AddClassDefinition(typeof(BanditRaidVillageQuest), 7);
            // Wave 4.25.0 (2026-06-12): parley hunt-pledge journal quest. Offset 8.
            AddClassDefinition(typeof(BanditHuntPledgeQuest), 8);
            // 2026-07-03: chief signature quest engine. Offset 9 confirmed unused
            // (existing offsets 1-8 above). Registered unconditionally so saves load
            // cleanly regardless of the EnableSignatureQuests toggle.
            AddClassDefinition(typeof(BanditSignatureQuest), 9);
        }

        // v217 ROOT CAUSE FIX (2026-05-31):
        //
        // Bannerlord's TaleWorlds.SaveSystem ships with built-in container definitions
        // for the dictionary/list permutations that vanilla and stock TaleWorlds modules
        // actually use — but NOT for arbitrary generic combinations. Vanilla never
        // serializes a Dictionary<string, double> anywhere, so the engine has no
        // built-in definition for it. Any mod that SyncData's a Dictionary<string, double>
        // through IDataStore (or marks one [SaveableField]) MUST register its container
        // definition here, or the engine's generic container-walking pass during
        // SaveContext.CollectContainerObjects → ContainerSaveData.GetChildObjects
        // dereferences a null ContainerDefinition → NRE → SaveResultType.GeneralFailure.
        //
        // BP has two such fields in BanditDialogManager:
        //   _pendingCustomOrderDueHours  (line 159)  Dictionary<string, double>
        //   _nextCaravanDeliveryHours    (line 165)  Dictionary<string, double>
        // Both store hours as double (per memory bannerlord-campaigntime-tohours, which
        // tells us to use CampaignTime.Now.ToHours which is double, NOT float).
        //
        // ROOT CAUSE VERIFIED VIA: v216 SaveExceptionTrap captured the throw site
        // (TaleWorlds.SaveSystem.Save.ContainerSaveData.GetChildObjects) and the full
        // env-stack confirmed the failure was in the engine's generic container pass,
        // NOT any specific behavior's CollectObjects. Inventory agent confirmed
        // Dictionary<string, double> is the ONLY non-vanilla container type in BP.
        //
        // After v217 ships green: re-enable WalkableHideoutEncounter at AddClassDefinition
        // offset 1 (currently commented out per v214 bisect — that wasn't the cause).
        protected override void DefineContainerDefinitions()
        {
            // v217 (2026-05-31): registered because BanditDialogManager.SyncData
            // persists Dictionary<string, double> for caravan timers.
            ConstructContainerDefinition(typeof(Dictionary<string, double>));

            // Wave 5 (Chief Recruitment): NEW container registrations required
            // per spec Section 3 and memory bannerlord-construct-container-definition.
            //
            // Dictionary<string, MobileParty> — BanditAllianceBehavior._activeSummonedWarband.
            //   The summoned bandit warband per allied chief; persists across save/load so the
            //   escort AI resumes after a mid-summon save. Vanilla does NOT register this
            //   permutation; omitting it produces SaveResultType.GeneralFailure with a null
            //   ContainerDefinition NRE in ContainerSaveData.GetChildObjects (same shape as
            //   the v217 bug).
            ConstructContainerDefinition(typeof(Dictionary<string, MobileParty>));

            // Dictionary<string, BanditAllianceQuest> — BanditAllianceBehavior._activeQuestByChief.
            //   The active in-flight alliance quest per chief. Element type is registered as
            //   a SaveableClass in DefineClassTypes() below; container itself still needs its
            //   own ConstructContainerDefinition call (vanilla never serializes a generic
            //   Dictionary<string, QuestBase>).
            ConstructContainerDefinition(typeof(Dictionary<string, BanditAllianceQuest>));

            // Living Chronicle: BanditChronicleBehavior persists chapter-id -> captured name.
            // (Dictionary<string,double> for the unlock-day map is already registered above.)
            ConstructContainerDefinition(typeof(Dictionary<string, string>));
        }
    }
}
