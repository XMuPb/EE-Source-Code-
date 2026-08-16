using System;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using BandItPlus.HideoutVisit;

namespace BandItPlus.BanditPower
{
    // v162 (2026-05-17): a roaming bandit party flies its culture chief's clan
    // banner on EVERY surface — campaign-map nameplate, encounter "Battle"
    // screen, in-battle morale bar, scoreboard, encyclopedia — via the engine's
    // own per-party banner override, PartyBase.CustomBanner.
    //
    // PartyBase.Banner is a computed property (CustomBanner ?? faction banner);
    // every banner UI reads party.Banner and derives an image code from it.
    // Setting CustomBanner is DISPLAY-ONLY — the bandit clan and faction are
    // untouched (Clan.Banner is a separate property). This is the single
    // upstream source that replaces the v156 nameplate-VM patch and the
    // abandoned v161 in-battle reflection hack: one assignment instead of
    // per-UI whack-a-mole.
    //
    // Idempotent: re-applying assigns the same banner reference, so
    // OnSessionLaunched (parties that already exist) plus HourlyTickPartyEvent
    // (parties spawned later) together cover every bandit party cheaply.
    public class BanditPartyBannerBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            CampaignEvents.OnAfterSessionLaunchedEvent.AddNonSerializedListener(this, OnAfterSessionLaunched);
            CampaignEvents.HourlyTickPartyEvent.AddNonSerializedListener(this, OnHourlyTickParty);
        }

        public override void SyncData(IDataStore dataStore) { }

        private void OnSessionLaunched(CampaignGameStarter starter) => RunFullSweep("session");

        // v173-fix (2026-05-18): BanditChiefRegistry reassigns chiefs to their
        // dedicated clans ("Stage 2") DURING session launch — ~0.3s AFTER our
        // OnSessionLaunched sweep. So the session sweep can capture a chief's
        // temporary Stage-1 (anchor-town, Empire-ish) clan banner. The diagnostic
        // proved exactly this: hideout CustomBanner ended up an Empire banner.
        // OnAfterSessionLaunched fires after every OnSessionLaunched listener —
        // i.e. after Stage 2 — so this re-sweep picks up each chief's FINAL
        // dedicated-clan banner. (Mobile parties also self-correct via
        // OnHourlyTickParty; hideout settlement parties have no such tick, so
        // this re-sweep is what makes the hideout fix actually stick.)
        private void OnAfterSessionLaunched(CampaignGameStarter starter) => RunFullSweep("after-session");

        // Applies CustomBanner to every bandit mobile party and every hideout
        // settlement party. Idempotent (TryApply/TryApplyHideout no-op when the
        // banner already matches), so running it twice is cheap.
        private void RunFullSweep(string phase)
        {
            int n = 0;
            foreach (var party in MobileParty.All)
                if (TryApply(party)) n++;
            HideoutPeacefulVisitState.Log("v162 party banner: " + phase
                + " sweep applied to " + n + " bandit parties");

            int h = 0;
            foreach (var hideout in Hideout.All)
                if (hideout != null && TryApplyHideout(hideout.Settlement)) h++;
            HideoutPeacefulVisitState.Log("v171 hideout banner: " + phase
                + " sweep applied to " + h + " hideout settlement(s)");
        }

        private void OnHourlyTickParty(MobileParty party)
        {
            TryApply(party);
        }

        private static bool _firstLogged;

        // PartyBase.CustomBanner has a non-public setter; write its auto-property
        // backing field directly (for an auto-property that is exactly what the
        // setter does). Resolved once and cached.
        private static FieldInfo _customBannerField;
        private static bool _fieldResolved;

        // Sets a bandit party's CustomBanner to its culture chief's clan banner.
        // Returns true if the party is a bandit party with a resolved banner
        // (whether newly applied or already applied).
        // Wave 4.16.1 (2026-06-11): made public so BanditRaidBehavior can apply
        // the banner at raid-party spawn instead of waiting for the hourly sweep
        // (user saw vanilla banners on fresh raid war bands).
        public static bool TryApply(MobileParty party)
        {
            try
            {
                if (party == null) return false;

                var mapFaction = party.MapFaction;
                if (mapFaction == null || !mapFaction.IsBanditFaction) return false;

                string cultureId = mapFaction.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return false;

                var chief = Campaign.Current?
                    .GetCampaignBehavior<BandItPlus.Heroes.BanditChiefRegistry>()?
                    .GetChief(cultureId);
                var banner = chief?.Clan?.Banner;
                if (banner == null) return false;

                var pb = party.Party;
                if (pb == null) return false;
                if (pb.CustomBanner == banner) return true; // already ours — idempotent

                if (!_fieldResolved)
                {
                    _fieldResolved = true;
                    _customBannerField = typeof(PartyBase).GetField("<CustomBanner>k__BackingField",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    HideoutPeacefulVisitState.Log("v162 CustomBanner field="
                        + (_customBannerField != null ? _customBannerField.Name : "NOT FOUND"));
                }
                if (_customBannerField == null) return false;

                _customBannerField.SetValue(pb, banner);
                if (!_firstLogged)
                {
                    _firstLogged = true;
                    HideoutPeacefulVisitState.Log("v162 party CustomBanner set — culture="
                        + cultureId + " party=" + party.StringId);
                }
                return true;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v162 CustomBanner fail: "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // v171: sets a hideout settlement party's CustomBanner to its culture
        // chief's clan banner. The hideout-assault defender Team takes its
        // banner from this PartyBase — confirmed by the v171 diagnostic
        // (team==settlementParty). Reuses TryApply's backing-field write.
        private static bool TryApplyHideout(Settlement settlement)
        {
            try
            {
                if (settlement == null || !settlement.IsHideout) return false;

                string cultureId = settlement.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return false;

                var chief = Campaign.Current?
                    .GetCampaignBehavior<BandItPlus.Heroes.BanditChiefRegistry>()?
                    .GetChief(cultureId);
                var banner = chief?.Clan?.Banner;
                if (banner == null) return false;

                var pb = settlement.Party;
                if (pb == null) return false;
                if (pb.CustomBanner == banner) return true; // already ours — idempotent

                if (!_fieldResolved)
                {
                    _fieldResolved = true;
                    _customBannerField = typeof(PartyBase).GetField("<CustomBanner>k__BackingField",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (_customBannerField == null) return false;

                _customBannerField.SetValue(pb, banner);
                return true;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v171 hideout CustomBanner fail: "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }
    }
}
