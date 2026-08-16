using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;

namespace BandItPlus.HideoutVisit
{
    // Wave 4.12-fix v70 (2026-05-12) — Tier 4 of Contacts roadmap.
    //
    // After the player intro's with a bandit vendor's contact, that contact
    // periodically sends a small supply caravan that drops culture-themed
    // goods into the player's main-party stash. Frequency: ~30 in-game days
    // per intro, staggered so 28 intros don't all deliver on the same day.
    //
    // State lives on BanditDialogManager (saveable) — this behavior is just
    // the scheduler. Subscribes to DailyTickEvent (1x/in-game-day).
    public class ContactCaravanBehavior : CampaignBehaviorBase
    {
        private const float kDeliveryIntervalDays = 30f;
        private const double kDeliveryIntervalHours = kDeliveryIntervalDays * 24.0;

        // Small per-delivery item pool — keeps caravans feeling like "ongoing
        // trickle of supply," not "another feast-scale shipment."
        private static readonly (string itemId, int qty, string label)[] _foodCaravanPool = new[]
        {
            ("grain",        30, BandItPlus.Localization.Get("bp_contactcaravanbehavior_001", "30 sacks of grain")),
            ("wine",          5, BandItPlus.Localization.Get("bp_contactcaravanbehavior_002", "5 jars of wine")),
            ("cheese",       10, BandItPlus.Localization.Get("bp_contactcaravanbehavior_003", "10 wheels of cheese")),
            ("smoked_fish",  15, BandItPlus.Localization.Get("bp_contactcaravanbehavior_004", "15 strings of smoked fish")),
            ("salt",         10, BandItPlus.Localization.Get("bp_contactcaravanbehavior_005", "10 sacks of salt")),
            ("date_fruit",   10, BandItPlus.Localization.Get("bp_contactcaravanbehavior_006", "10 lots of dates")),
            ("olives",       10, BandItPlus.Localization.Get("bp_contactcaravanbehavior_007", "10 lots of olives")),
            ("butter",       10, BandItPlus.Localization.Get("bp_contactcaravanbehavior_008", "10 crocks of butter")),
        };
        private static readonly (string itemId, int qty, string label)[] _gearCaravanPool = new[]
        {
            ("hardwood",        10, BandItPlus.Localization.Get("bp_contactcaravanbehavior_009", "10 hardwood")),
            ("iron",             5, BandItPlus.Localization.Get("bp_contactcaravanbehavior_010", "5 iron ingots")),
            ("iron_sword_t1",    1, BandItPlus.Localization.Get("bp_contactcaravanbehavior_011", "1 iron sword")),
            ("hunting_bow",      1, BandItPlus.Localization.Get("bp_contactcaravanbehavior_012", "1 hunting bow")),
            ("leather_jerkin",   1, BandItPlus.Localization.Get("bp_contactcaravanbehavior_013", "1 leather jerkin")),
            ("leather_boots",    2, BandItPlus.Localization.Get("bp_contactcaravanbehavior_014", "2 pairs of leather boots")),
            ("padded_cap",       1, BandItPlus.Localization.Get("bp_contactcaravanbehavior_015", "1 padded cap")),
            ("wooden_shield_a",  1, BandItPlus.Localization.Get("bp_contactcaravanbehavior_016", "1 wooden shield")),
        };

        private static readonly string[] _allBanditCultures = new[]
        {
            "forest_bandits", "bp_frost_reavers", "bp_marsh_stalkers",
            "bp_highwaymen",  "bp_slaver_caravans", "bp_fallen_legionaries",
            "bp_sky_raiders", "bp_steppe_wolves", "bp_pagan_cult",
            "looters", "mountain_bandits", "steppe_bandits",
            "sea_raiders", "desert_bandits",
        };

        public override void RegisterEvents()
        {
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
        }

        public override void SyncData(IDataStore dataStore) { }

        // v72: batched delivery lines per tick — consolidated into ONE popup
        // at end of tick rather than 1 popup per delivery. Keeps the
        // "Gauntlet-everywhere" UX promise without click-OK-fest if many
        // contacts deliver on the same day.
        private System.Collections.Generic.List<string> _todaysDeliveryLines
            = new System.Collections.Generic.List<string>();

        private void OnDailyTick()
        {
            try
            {
                if (MCMSettings.Instance == null || !MCMSettings.Instance.EnableMod) return;
                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                if (bdm == null || Hero.MainHero == null) return;

                _todaysDeliveryLines.Clear();
                double nowHours = CampaignTime.Now.ToHours;
                foreach (var cid in _allBanditCultures)
                {
                    TryDelivery(bdm, cid, "food", nowHours);
                    TryDelivery(bdm, cid, "gear", nowHours);
                }

                if (_todaysDeliveryLines.Count > 0)
                {
                    HideoutVendorDialog.BpShowInfoPopup(
                        new TaleWorlds.Localization.TextObject("{=bp_hch_004}Caravans Arrived — {COUNT} contact{PLURAL}")
                            .SetTextVariable("COUNT", _todaysDeliveryLines.Count)
                            .SetTextVariable("PLURAL", _todaysDeliveryLines.Count == 1 ? "" : "s").ToString(),
                        HideoutVendorDialog.BpPopupBody(
                            BandItPlus.Localization.Get("bp_contactcaravanbehavior_017", "Your contact network sent supply to the camp today."),
                            string.Join("\n", _todaysDeliveryLines),
                            BandItPlus.Localization.Get("bp_contactcaravanbehavior_018", "Items added to your stash")));
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("ContactCaravan.OnDailyTick fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void TryDelivery(BanditDialogManager bdm, string cid, string vendorKind, double nowHours)
        {
            if (!bdm.HasVendorContactsIntroduced(cid, vendorKind)) return;

            double dueHours = bdm.GetNextCaravanDeliveryHours(cid, vendorKind);
            if (dueHours <= 0.0)
            {
                int stagger = Math.Abs((cid + ":" + vendorKind).GetHashCode()) % 30;
                bdm.SetNextCaravanDeliveryHours(cid, vendorKind, nowHours + (1 + stagger) * 24.0);
                return;
            }
            if (nowHours < dueHours) return;

            DeliverCaravan(bdm, cid, vendorKind, nowHours);
        }

        private void DeliverCaravan(BanditDialogManager bdm, string cid, string vendorKind, double nowHours)
        {
            try
            {
                var pool = vendorKind == "food" ? _foodCaravanPool : _gearCaravanPool;
                var pick = pool[MBRandom.RandomInt(pool.Length)];

                var item = MBObjectManager.Instance.GetObject<ItemObject>(pick.itemId);
                if (item == null || MobileParty.MainParty == null)
                {
                    bdm.SetNextCaravanDeliveryHours(cid, vendorKind, nowHours + kDeliveryIntervalHours);
                    HideoutPeacefulVisitState.Log("ContactCaravan: item '" + pick.itemId
                        + "' missing or no MainParty — rescheduled " + cid + "/" + vendorKind);
                    return;
                }

                MobileParty.MainParty.ItemRoster.AddToCounts(item, pick.qty);

                var profile = vendorKind == "food"
                    ? BandItPlus.Cultures.VendorProfiles.GetFood(cid)
                    : BandItPlus.Cultures.VendorProfiles.GetGear(cid);
                string vendorName = profile?.Name ?? (vendorKind + " vendor");

                // v72: collect into today's batch instead of firing per-delivery banner.
                _todaysDeliveryLines.Add("◆ " + vendorName + " — " + pick.label);

                HideoutPeacefulVisitState.Log("ContactCaravan DELIVERED: " + cid + "/" + vendorKind
                    + " — " + pick.qty + "x " + pick.itemId);

                bdm.SetNextCaravanDeliveryHours(cid, vendorKind, nowHours + kDeliveryIntervalHours);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("ContactCaravan.DeliverCaravan fail: "
                    + ex.GetType().Name + ": " + ex.Message);
                bdm.SetNextCaravanDeliveryHours(cid, vendorKind, nowHours + kDeliveryIntervalHours);
            }
        }
    }
}
