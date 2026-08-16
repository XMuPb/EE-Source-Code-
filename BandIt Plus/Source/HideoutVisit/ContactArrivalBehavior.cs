using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BandItPlus.HideoutVisit
{
    // Wave 4.12-fix v72 (2026-05-12) — Tier 5 dialog-hub starter.
    //
    // When the player enters a real-world settlement that hosts one of their
    // intro'd contacts for the first time, fire a premium Gauntlet popup that
    // names the contact, recognizes the player, and awards an arrival bonus.
    // Subscribes to CampaignEvents.OnSettlementEnteredEvent.
    public class ContactArrivalBehavior : CampaignBehaviorBase
    {
        private const int kArrivalBonus = 1500;

        // 2026-05-26: one-shot per CAMPAIGN — after the first contact-arrival popup
        // we chase it with a beta-thanks card. Persisted via SyncData so the player
        // sees the end-of-quest-line acknowledgement exactly once per save, not
        // once per game launch (the earlier static-field version re-fired on every
        // session start, which felt like spam on long campaigns).
        //
        // Loading an old save where this field is absent: SyncData will default the
        // ref to false, so any player who already saw the popup in a previous launch
        // will see it ONE more time on their next contact arrival in the loaded save,
        // and then never again. Acceptable for a beta marker.
        private bool _betaEndScreenShown;

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
            CampaignEvents.SettlementEntered.AddNonSerializedListener(this, OnSettlementEntered);
        }

        public override void SyncData(IDataStore dataStore)
        {
            // 2026-05-26: persist the beta-thanks one-shot flag so it fires
            // exactly once per campaign. bool is a primitive — no SaveableTypeDefiner
            // registration required. Loading older saves: the ref defaults to false,
            // which is the correct behavior (player has not yet seen the popup
            // in this save's lifetime).
            try
            {
                dataStore.SyncData("_bp_betaEndScreenShown", ref _betaEndScreenShown);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("ContactArrival.SyncData fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnSettlementEntered(MobileParty party, Settlement settlement, Hero hero)
        {
            try
            {
                if (party != MobileParty.MainParty) return;
                if (settlement == null || string.IsNullOrEmpty(settlement.StringId)) return;
                if (Hero.MainHero == null) return;
                if (MCMSettings.Instance == null || !MCMSettings.Instance.EnableMod) return;

                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                if (bdm == null) return;

                foreach (var cid in _allBanditCultures)
                {
                    TryMeet(bdm, cid, "food", settlement);
                    TryMeet(bdm, cid, "gear", settlement);
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("ContactArrival.OnSettlementEntered fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void TryMeet(BanditDialogManager bdm, string cid, string vendorKind, Settlement currentSettlement)
        {
            try
            {
                var assignedId = bdm.GetContactSettlement(cid, vendorKind);
                if (string.IsNullOrEmpty(assignedId)) return;
                if (assignedId != currentSettlement.StringId) return;
                if (bdm.HasContactSettlementMet(cid, vendorKind)) return;

                bdm.SetContactSettlementMet(cid, vendorKind, true);

                var profile = vendorKind == "food"
                    ? BandItPlus.Cultures.VendorProfiles.GetFood(cid)
                    : BandItPlus.Cultures.VendorProfiles.GetGear(cid);
                string vendorName = profile?.Name ?? (vendorKind + " vendor");
                string flavor = profile?.T3ContactName ?? BandItPlus.Localization.Get("bp_contactarrivalbehavior_001", "They give you a knowing nod and slip a purse across the table.");

                if (Hero.MainHero != null)
                    Hero.MainHero.ChangeHeroGold(kArrivalBonus);

                HideoutVendorDialog.BpShowInfoPopupNoPause(
                    new TaleWorlds.Localization.TextObject("{=bp_hch_001}An Out-of-Camp Reunion — {SETTLEMENT}")
                        .SetTextVariable("SETTLEMENT", currentSettlement.Name?.ToString() ?? currentSettlement.StringId).ToString(),
                    HideoutVendorDialog.BpPopupBody(
                        new TaleWorlds.Localization.TextObject("{=bp_hch_002}You have found {VENDOR}'s contact in {SETTLEMENT}.")
                            .SetTextVariable("VENDOR", vendorName)
                            .SetTextVariable("SETTLEMENT", currentSettlement.Name?.ToString() ?? currentSettlement.StringId).ToString(),
                        flavor,
                        new TaleWorlds.Localization.TextObject("{=bp_hch_003}+{BONUS} denars (arrival purse from the network)")
                            .SetTextVariable("BONUS", kArrivalBonus).ToString(),
                        BandItPlus.Localization.Get("bp_contactarrivalbehavior_002", "Contact-network status: actively recognized in this settlement")));

                HideoutPeacefulVisitState.Log("ContactArrival MET: " + cid + "/" + vendorKind
                    + " at " + currentSettlement.StringId + " — +" + kArrivalBonus + " denars");

                // 2026-05-26: chain a beta-end-of-quest-line "thank you" card after
                // the first contact-arrival of the session. Reaching a contact
                // settlement IS the end of the current trust-quest content
                // (chief → vendor → introduction → arrival), so this is the
                // natural moment to acknowledge it without breaking immersion in
                // the player's first ten hours.
                //
                // Same popup helper as the arrival card (NoPause variant — see
                // feedback_bannerlord_modal_popup_loading_splash_deadlock — and
                // matching premium-popup style per reference_bannerlord_premium_
                // inquiry_popup).
                if (!_betaEndScreenShown)
                {
                    _betaEndScreenShown = true;
                    try
                    {
                        HideoutVendorDialog.BpShowInfoPopupNoPause(
                            BandItPlus.Localization.Get("bp_contactarrivalbehavior_003", "End of the Beta Trail — Thank You"),
                            HideoutVendorDialog.BpPopupBody(
                                BandItPlus.Localization.Get("bp_contactarrivalbehavior_004", "You have ridden the trust system to its current end. Chief trust, vendor trust, the camp's network of contacts, and now an arrival reunion in a real-world settlement — this is everything BandIt Plus' current beta has to offer."),
                                BandItPlus.Localization.Get("bp_contactarrivalbehavior_005", "Thank you for playing the BandIt Plus Beta. The mod is the work of one person and a great many late nights, and it ships because riders like you cross the map, find the gaps, and tell me where it hurts."),
                                BandItPlus.Localization.Get("bp_contactarrivalbehavior_006", "More is being built — new cultures, deeper chief chains, named hunts, kingdom-level consequences for the trade you do here, and a few things not yet ready to be hinted at"),
                                BandItPlus.Localization.Get("bp_contactarrivalbehavior_007", "Save your campaign. Send me feedback. The next wave starts from what this one taught.")));
                        HideoutPeacefulVisitState.Log("ContactArrival: beta end-of-quest-line popup shown");
                    }
                    catch (Exception betaEx)
                    {
                        HideoutPeacefulVisitState.Log("ContactArrival beta-thanks popup fail: "
                            + betaEx.GetType().Name + ": " + betaEx.Message);
                    }
                }

                // v74 (2026-05-12): clean up the quest-tracker ring now that the player has
                // actually arrived. Keeps the tracked-objectives panel uncluttered for the
                // next contact intro.
                try
                {
                    Campaign.Current?.VisualTrackerManager?.RemoveTrackedObject(currentSettlement);
                }
                catch (Exception trackerEx)
                {
                    HideoutPeacefulVisitState.Log("VisualTracker remove fail: "
                        + trackerEx.GetType().Name + ": " + trackerEx.Message);
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("ContactArrival.TryMeet fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
