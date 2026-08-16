using System;
using System.IO;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace BandItPlus
{
    public class PlayerBanditNeutrality : CampaignBehaviorBase
    {
        public static PlayerBanditNeutrality Instance { get; private set; }

        private static readonly string[] BpClanCultureIds = new[]
        {
            "bp_frost_reavers", "bp_marsh_stalkers", "bp_highwaymen", "bp_slaver_caravans",
            "bp_fallen_legionaries", "bp_sky_raiders", "bp_steppe_wolves", "bp_pagan_cult",
            // Vanilla bandit cultures — also set neutral so player walks past Looters etc. without auto-aggro
            "looters", "forest_bandits", "mountain_bandits", "steppe_bandits", "sea_raiders", "desert_bandits"
        };

        public PlayerBanditNeutrality() { Instance = this; }

        public override void RegisterEvents()
        {
            CampaignEvents.OnNewGameCreatedEvent.AddNonSerializedListener(this, OnNewGameCreated);
            CampaignEvents.OnGameLoadedEvent.AddNonSerializedListener(this, OnGameLoaded);
        }

        private void OnGameLoaded(CampaignGameStarter starter)
        {
            Log("OnGameLoaded fired. MCM null=" + (MCMSettings.Instance == null) +
                ", PeacefulEncounters=" + (MCMSettings.Instance?.PeacefulEncounters ?? false));
            if (MCMSettings.Instance != null && MCMSettings.Instance.PeacefulEncounters)
                ResetAllRelations();
        }

        private void OnNewGameCreatedExt(CampaignGameStarter starter)
        {
            Log("OnNewGameCreated fired");
            ResetAllRelations();
        }

        public override void SyncData(IDataStore dataStore) { }

        private void OnNewGameCreated(CampaignGameStarter starter)
        {
            Log("OnNewGameCreated fired");
            ResetAllRelations();
        }

        public void ResetAllRelations()
        {
            Log("ResetAllRelations called");
            try
            {
                var playerClan = Hero.MainHero?.Clan;
                if (playerClan == null) { Log("BAIL: Hero.MainHero.Clan is null"); return; }
                Log("Player clan: " + playerClan.StringId);
                Log("Total Clan.All count: " + Clan.All.Count);

                int matchedCount = 0;
                int setCount = 0;
                foreach (var bpCultureId in BpClanCultureIds)
                {
                    foreach (var clan in Clan.All)
                    {
                        if (clan == null) continue;
                        if (clan.Culture?.StringId != bpCultureId) continue;
                        matchedCount++;
                        try
                        {
                            FactionManager.SetNeutral(playerClan, clan);
                            // Also try DeclarePeace for good measure
                            // Verify the relation actually changed
                            var rel = FactionManager.GetRelationBetweenClans(playerClan, clan);
                            Log("  matched clan='" + clan.StringId + "' culture='" + bpCultureId + "' newRelation=" + rel);
                            setCount++;
                        }
                        catch (Exception innerEx)
                        {
                            Log("  SetNeutral failed for clan='" + clan.StringId + "': " + innerEx.Message);
                        }
                    }
                }
                Log("Matched " + matchedCount + " clans, set " + setCount + " to neutral");

                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Set " + setCount + "/" + matchedCount + " bandit clan relations to neutral.", Colors.Yellow));
            }
            catch (Exception ex)
            {
                Log("ResetAllRelations FATAL: " + ex.Message);
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] ResetAllRelations error: " + ex.Message, Colors.Red));
            }
        }

        private static void Log(string msg)
        {
            try
            {
                File.AppendAllText(BandItPlus.Diagnostics.BpDiag.P("bp_peace_debug.log"),
                    "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] PlayerBanditNeutrality: " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
