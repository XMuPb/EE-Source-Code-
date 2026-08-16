using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using BandItPlus.HideoutVisit;

namespace BandItPlus.BanditPower
{
    // v168-fix (2026-05-17): awards Bandit Battle Spoils when the player wins a
    // battle against bandits.
    //
    // Replaces the original v168 Harmony patch on "BattleLootCampaignBehavior" —
    // that type does NOT exist in Bannerlord 1.3.x (loot is handled by MapEvent
    // itself), so the patch silently never attached. The supported hook is the
    // CampaignEvents.MapEventEnded event.
    //
    // No pre-battle snapshot is needed: MapEventParty.HealthyManCountAtStart is a
    // public value that survives post-battle, giving each defeated party's size
    // directly when the event fires.
    public class BanditSpoilsBehavior : CampaignBehaviorBase
    {
        private static bool _attachLogged;

        public override void RegisterEvents()
        {
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
            if (!_attachLogged)
            {
                _attachLogged = true;
                HideoutPeacefulVisitState.Log(
                    "v168 BanditSpoilsBehavior: registered MapEventEnded listener");
            }
        }

        // No saved state — the behavior only reacts to a transient event.
        public override void SyncData(IDataStore dataStore) { }

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            try
            {
                if (mapEvent == null) return;
                if (MCMSettings.Instance == null || !MCMSettings.Instance.EnableMod) return;

                var winnerSide = mapEvent.WinningSide;
                if (winnerSide == BattleSideEnum.None) return;

                // The player must be in this battle and on the winning side.
                if (mapEvent.PlayerSide == BattleSideEnum.None
                    || mapEvent.PlayerSide != winnerSide)
                    return;

                // 07-11 storyline guard: no spoils for story battles — the popup was
                // swallowing the tutorial's next-step notification, and quest fights
                // should pay exactly what the quest scripted.
                if (BandItPlus.Patches.BpStoryGuard.StoryTutorialActive()) return;
                if (BandItPlus.Patches.BpStoryGuard.EventHasQuestParty(mapEvent)) return;

                var loserSide = winnerSide == BattleSideEnum.Attacker
                    ? BattleSideEnum.Defender : BattleSideEnum.Attacker;
                var loserEventSide = mapEvent.GetMapEventSide(loserSide);
                if (loserEventSide == null) return;

                var defeated = new List<DefeatedBandit>();
                foreach (var mep in loserEventSide.Parties)
                {
                    if (mep == null) continue;
                    var mobile = mep.Party?.MobileParty;
                    var cultureId = mobile?.MapFaction?.Culture?.StringId;
                    if (!BanditLootRegistry.IsBanditCulture(cultureId)
                        && !(mobile?.IsBandit ?? false))
                        continue;
                    if (cultureId == null) continue;

                    // HealthyManCountAtStart survives post-battle — the defeated
                    // party's size when the fight began.
                    int count = mep.HealthyManCountAtStart;
                    if (count > 0)
                        defeated.Add(new DefeatedBandit
                        { CultureId = cultureId, TroopCount = count });
                }
                if (defeated.Count == 0) return;

                HideoutPeacefulVisitState.Log("v168 BanditSpoilsBehavior: player won vs "
                    + defeated.Count + " bandit party(ies)");
                BanditSpoils.Award(MobileParty.MainParty, defeated);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v168 BanditSpoilsBehavior.OnMapEventEnded fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
