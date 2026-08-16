using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;
using BandItPlus.HideoutVisit;

namespace BandItPlus.BanditPower
{
    // v168: computes and awards the "Spoils of Victory" reward after the player
    // wins a battle against bandits. Called once per battle by BanditSpoilsBehavior.
    //
    // A defeated bandit party is described by a DefeatedBandit — its culture and
    // its healthy troop count at battle start (MapEventParty.HealthyManCountAtStart,
    // a public value that survives post-battle, so no pre-battle snapshot needed).
    public struct DefeatedBandit
    {
        public string CultureId;
        public int TroopCount;
    }

    public static class BanditSpoils
    {
        private static readonly Random Rng = new Random();

        public static void Award(MobileParty winner, List<DefeatedBandit> defeated)
        {
            try
            {
                if (winner == null || defeated == null || defeated.Count == 0) return;
                var s = MCMSettings.Instance;
                if (s == null || !s.EnableMod) return;

                // --- strength factor (hybrid: campaign age + this battle) ---
                int totalTroops = 0;
                bool beatAnElite = false;
                foreach (var d in defeated)
                {
                    totalTroops += d.TroopCount;
                    var e = BanditLootRegistry.Get(d.CultureId);
                    if (e != null && e.IsEliteClan) beatAnElite = true;
                }
                if (totalTroops <= 0) return;

                double ageFactor = BanditPowerBehavior.PowerFactor(); // 0..1, already public
                double partyFactor = Clamp01(totalTroops / 80.0);
                double strength = 0.5 + ageFactor * 0.5 + partyFactor * 1.0; // ~0.5 .. 2.0

                int goldAwarded = 0, keepsakesAwarded = 0, gearAwarded = 0;
                var lines = new List<string>();

                // --- coin ---
                int bonusGold = (int)Math.Round(totalTroops * 15.0 * strength * s.CoinDropMultiplier);
                if (bonusGold > 0 && Hero.MainHero != null)
                {
                    GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, bonusGold, false);
                    goldAwarded = bonusGold;
                    lines.Add(bonusGold + " denars from the bandits' purses");
                }

                // --- keepsakes (one roll per distinct defeated culture, cap 2) ---
                if (s.DropKeepsakes)
                {
                    var seen = new HashSet<string>();
                    foreach (var d in defeated)
                    {
                        if (keepsakesAwarded >= 2) break;
                        if (!seen.Add(d.CultureId)) continue;
                        var e = BanditLootRegistry.Get(d.CultureId);
                        if (e == null || string.IsNullOrEmpty(e.KeepsakeItemId)) continue;

                        double chance = e.KeepsakeBaseChance * s.KeepsakeChanceMultiplier
                            * (s.UseEliteBonus && e.IsEliteClan ? 1.5 : 1.0);
                        if (Rng.NextDouble() >= chance) continue;

                        var item = MBObjectManager.Instance.GetObject<ItemObject>(e.KeepsakeItemId);
                        if (item == null) continue;
                        winner.ItemRoster?.AddToCounts(item, 1);
                        keepsakesAwarded++;
                        lines.Add("Keepsake: " + item.Name);
                    }
                }

                // --- themed gear ---
                double gearChance = Clamp01(0.45 * strength);
                if (Rng.NextDouble() < gearChance)
                {
                    int gearCount = strength >= 1.5 ? 2 : 1;
                    if (s.UseEliteBonus && beatAnElite) gearCount++;

                    var pool = new List<string>();
                    foreach (var d in defeated)
                    {
                        var e = BanditLootRegistry.Get(d.CultureId);
                        if (e?.GearPool == null) continue;
                        foreach (var id in e.GearPool)
                            if (!pool.Contains(id)) pool.Add(id); // de-dup across cultures
                    }
                    for (int i = 0; i < gearCount && pool.Count > 0; i++)
                    {
                        int idx = Rng.Next(pool.Count);
                        string id = pool[idx];
                        pool.RemoveAt(idx); // draw without replacement — distinct items
                        var item = MBObjectManager.Instance.GetObject<ItemObject>(id);
                        if (item == null) continue; // bad ID -> skip gracefully
                        winner.ItemRoster?.AddToCounts(item, 1);
                        gearAwarded++;
                        lines.Add("Spoils: " + item.Name);
                    }
                }

                // --- popup ---
                if (s.ShowKeepsakeNotification && lines.Count > 0)
                    ShowSpoilsPopup(lines);

                HideoutPeacefulVisitState.Log("v168 BanditSpoils: awarded " + goldAwarded
                    + "g, " + keepsakesAwarded + " keepsake(s), " + gearAwarded + " gear"
                    + " (strength=" + strength.ToString("0.00") + ")");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v168 BanditSpoils.Award fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // v176: pre-battle spoils estimate — the projected coin reward for
        // clearing a hideout with `defenderCount` defenders, using the SAME
        // strength + gold formula as Award() but awarding nothing. The v176
        // Hideout Assault panel shows this in its spoils line. The result is
        // deterministic (Award's gold roll is not random). Returns 0 if the
        // mod is off or the count is non-positive.
        public static int EstimateSpoils(int defenderCount)
        {
            try
            {
                var s = MCMSettings.Instance;
                if (s == null || !s.EnableMod || defenderCount <= 0) return 0;
                double ageFactor = BanditPowerBehavior.PowerFactor();
                double partyFactor = Clamp01(defenderCount / 80.0);
                double strength = 0.5 + ageFactor * 0.5 + partyFactor * 1.0;
                return (int)Math.Round(defenderCount * 15.0 * strength * s.CoinDropMultiplier);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v176 EstimateSpoils fail: "
                    + ex.GetType().Name + ": " + ex.Message);
                return 0;
            }
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        // Non-pausing premium popup. Body is built with the mod's shared
        // BpPopupBody formatter so it matches every other BandIt Plus popup
        // (clean divider, ◆ bullets). pauseGameActiveState:false — this fires
        // after EVERY bandit battle, so it must never freeze the campaign map.
        private static void ShowSpoilsPopup(List<string> lines)
        {
            try
            {
                string body = HideoutVendorDialog.BpPopupBody(
                    BandItPlus.Localization.Get("bp_spoils_001", "The field is yours. You take from the fallen:"), null, lines.ToArray());
                InformationManager.ShowInquiry(new InquiryData(
                    titleText: BandItPlus.Localization.Get("bp_spoils_002", "Spoils of Victory"),
                    text: body,
                    isAffirmativeOptionShown: true,
                    isNegativeOptionShown: false,
                    affirmativeText: BandItPlus.Localization.Get("bp_spoils_003", "Continue"),
                    negativeText: null,
                    affirmativeAction: null,
                    negativeAction: null,
                    soundEventPath: ""),
                    pauseGameActiveState: false);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v168 BanditSpoils.ShowSpoilsPopup fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
