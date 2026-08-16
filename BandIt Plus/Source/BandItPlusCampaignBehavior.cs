using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace BandItPlus
{
    public class BandItPlusCampaignBehavior : CampaignBehaviorBase
    {
        [SaveableField(1)]
        private Dictionary<string, string> _clanOwnedParties = new Dictionary<string, string>();

        public static BandItPlusCampaignBehavior Instance { get; private set; }

        public BandItPlusCampaignBehavior()
        {
            Instance = this;
        }

        public override void RegisterEvents()
        {
            CampaignEvents.OnGameLoadedEvent.AddNonSerializedListener(this, OnGameLoaded);
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, OnPartyDestroyed);
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("_clanOwnedParties", ref _clanOwnedParties);
            if (_clanOwnedParties == null)
                _clanOwnedParties = new Dictionary<string, string>();
        }

        public void TrackParty(MobileParty party, string clanId)
        {
            if (party == null || string.IsNullOrEmpty(clanId)) return;
            _clanOwnedParties[party.StringId] = clanId;
        }

        public bool IsClanEnabled(string clanId)
        {
            var s = MCMSettings.Instance;
            if (s == null) return true;
            return clanId switch
            {
                "bp_frost_reavers"      => s.EnableFrostReavers,
                "bp_marsh_stalkers"     => s.EnableMarshStalkers,
                "bp_highwaymen"         => s.EnableHighwaymen,
                "bp_slaver_caravans"    => s.EnableSlaverCaravans,
                "bp_fallen_legionaries" => s.EnableFallenLegionaries,
                "bp_sky_raiders"        => s.EnableSkyRaiders,
                "bp_steppe_wolves"      => s.EnableSteppeWolves,
                "bp_pagan_cult"         => s.EnablePaganCult,
                _ => false,
            };
        }

        private void OnGameLoaded(CampaignGameStarter starter)
        {
            // 07-12 GoC-interop heal: repair any ownerless settlement left by a prior Bandit-King
            // rebellion BEFORE the session starts. Mods like Guilds of Calradia walk every party's
            // MapFaction on OnSessionStart and NRE on an ownerless town's militia; OnGameLoaded runs
            // before OnSessionStart (engine IL-verified), so this fixes an already-affected save on load.
            try
            {
                int healed = BandItPlus.BanditRaid.BanditSeizeHelper.RepairOwnerlessSettlements();
                if (healed > 0)
                    InformationManager.DisplayMessage(new InformationMessage(
                        "[BandIt Plus] Repaired " + healed + " ownerless settlement(s) from a prior rebellion.", Colors.Gray));
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Ownerless-settlement repair error: " + ex.Message, Colors.Red));
            }

            try
            {
                var toRemove = new List<string>();
                foreach (var kv in _clanOwnedParties)
                {
                    if (!IsClanEnabled(kv.Value))
                    {
                        var party = MobileParty.All.Find(p => p.StringId == kv.Key);
                        if (party != null)
                        {
                            DestroyPartyAction.Apply(null, party);
                            InformationManager.DisplayMessage(new InformationMessage(
                                "[BandIt Plus] Cleaned up disabled clan party: " + kv.Value, Colors.Gray));
                        }
                        toRemove.Add(kv.Key);
                    }
                }
                foreach (var k in toRemove) _clanOwnedParties.Remove(k);
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] OnGameLoaded cleanup error: " + ex.Message, Colors.Red));
            }
        }

        private void OnPartyDestroyed(MobileParty party, PartyBase destroyer)
        {
            if (party == null) return;
            _clanOwnedParties.Remove(party.StringId);
        }

        public void ForceClearAllParties()
        {
            try
            {
                var toRemove = new List<string>();
                foreach (var kv in _clanOwnedParties)
                {
                    var party = MobileParty.All.Find(p => p.StringId == kv.Key);
                    if (party != null) DestroyPartyAction.Apply(null, party);
                    toRemove.Add(kv.Key);
                }
                foreach (var k in toRemove) _clanOwnedParties.Remove(k);
                InformationManager.DisplayMessage(new InformationMessage(
                    Localization.Get("bp_campaignbehavior_001", "[BandIt Plus] All BandIt Plus parties cleared."), Colors.Yellow));
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] ForceClear error: " + ex.Message, Colors.Red));
            }
        }
    }
}
