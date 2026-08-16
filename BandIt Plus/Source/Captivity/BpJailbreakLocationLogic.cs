using TaleWorlds.CampaignSystem;                       // CampaignMission
using TaleWorlds.CampaignSystem.Settlements.Locations; // Location
using TaleWorlds.MountAndBlade;                        // MissionLogic

namespace BandItPlus.Captivity
{
    // Wave 4.32 — hideout-safe replacement for SandBox's MissionLocationLogic, which NREs at a hideout
    // by dereferencing LocationComplex.Current. We only need it to bind our standalone Location so
    // MissionAgentHandler.SpawnLocationCharacters() (which reads CampaignMission.Current.Location)
    // spawns our guards. See 2026-06-24-jailbreak-native-reference.md + the Location/win spike.
    public sealed class BpJailbreakLocationLogic : MissionLogic
    {
        private readonly Location _loc;
        public BpJailbreakLocationLogic(Location loc) { _loc = loc; }
        public override void OnCreated()
        {
            base.OnCreated();
            try
            {
                if (_loc != null && CampaignMission.Current != null)
                {
                    CampaignMission.Current.Location = _loc;
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Jailbreak] LocationLogic: bound Location " + _loc.StringId);
                }
                else
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Jailbreak] LocationLogic: NOT bound (loc="
                        + (_loc != null) + " campMission=" + (CampaignMission.Current != null) + ")");
                }
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Jailbreak] LocationLogic OnCreated fail: " + ex.Message);
            }
        }
    }
}
