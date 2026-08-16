using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace BandItPlus.Origins
{
    // Wave 4.19.0 (2026-06-11) — "Blood & Banners" origin system, Wave A.
    //
    // Replaces the binary PeacefulEncounters toggle with a CHOICE the player makes
    // once per campaign, shown shortly after the campaign map appears:
    //
    //   OUTLAW BLOOD — at peace with every bandit clan from birth.
    //   DRIFTER      — at war, but chiefs will parley (Wave B quest earns peace
    //                  per culture).
    //   LAWKEEPER    — at war; chiefs distrust you; bounty payouts +25%.
    //
    // Peace is tracked PER CULTURE in a saved CSV (no SaveableTypeDefiner needed —
    // see memory note on container definitions). The PeacefulBanditPatch war
    // overrides read IsAtPeaceWith() once an origin is chosen; until then the
    // legacy MCM toggle behavior applies (protects existing saves until the popup
    // is answered).
    public class BanditOriginBehavior : CampaignBehaviorBase
    {
        public enum BanditOrigin { Unchosen = 0, OutlawBlood = 1, Drifter = 2, Lawkeeper = 3 }

        private static BanditOriginBehavior _instance;
        public static BanditOriginBehavior Instance => _instance;

        private int _origin;                       // saved
        private int _perksApplied;                 // saved guard — origin id whose starting kit was granted (0 = none); blocks double-grant on reload
        private string _culturesAtPeaceCsv = "";   // saved, e.g. "forest_bandits,sea_raiders"
        private string _homeClanCulture = "";       // OutlawBlood's picked home clan (allied from turn one); empty = old save (blanket access)

        // Popup pacing — show once, ~3 real seconds after the map appears (popups
        // fired straight from session-launch hooks can hide behind the loading
        // splash; see memory note on modal popups during scene init).
        private bool _popupShown;
        private float _popupDelay;

        public BanditOrigin Origin => (BanditOrigin)_origin;
        public bool OriginChosen => _origin != 0;
        // Wave 4.28.0: HUD quest-slot alert — true while a hunt pledge is owed.
        public bool HasActiveHuntPledge => !string.IsNullOrEmpty(_huntTargetCulture);
        public float BountyMultiplier => Origin == BanditOrigin.Lawkeeper ? 1.25f : 1.0f;
        // Wave A.1 (user rule: "fresh game = war for EVERYONE, peace must be earned").
        // Origins no longer grant peace — they set the PRICE of earning it:
        // Outlaw Blood half tribute, Drifter full, Lawkeeper double.
        public float PeaceCostMultiplier =>
            Origin == BanditOrigin.OutlawBlood ? 0.5f
            : Origin == BanditOrigin.Lawkeeper ? 2.0f
            : 1.0f;

        public bool IsAtPeaceWith(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(_culturesAtPeaceCsv)) return false;
            // CSV containment with exact-token match. EARNED peace only — no origin
            // grants it for free (Wave A.1 design decision).
            foreach (var tok in _culturesAtPeaceCsv.Split(','))
                if (tok == cultureId) return true;
            return false;
        }

        // Wave B will call this when a chief's peace quest completes.
        public void GrantPeace(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId) || IsAtPeaceWith(cultureId)) return;
            _culturesAtPeaceCsv = string.IsNullOrEmpty(_culturesAtPeaceCsv)
                ? cultureId
                : _culturesAtPeaceCsv + "," + cultureId;
            Log("peace granted with " + cultureId + " (now: " + _culturesAtPeaceCsv + ")");
            // Wave 4.28.0: earning a clan's peace is a major bandit deed → infamy.
            try { BandItPlus.HUD.BanditRankBehavior.Instance?.AddInfamy(40, "earned peace with " + cultureId); } catch { }
        }

        // ── Wave B: parley + hunt-pledge state ──
        // One active hunt pledge at a time: kill a rival-culture war band within
        // 7 in-game days to earn peace with _huntTargetCulture. Saved.
        private string _huntTargetCulture = "";
        private double _huntExpiresHours;
        // Wave 4.25.10 — the parchment camp-invitation letter: queued at hunt
        // fulfilment, delivered a few ticks later by OnTick (runtime-only, NOT
        // saved — it is a momentary post-kill flourish).
        private string _pendingPeaceLetterCid = "";
        private float _peaceLetterDelay;
        // Per-culture re-offer cooldown after the player walks away (runtime-only).
        private readonly Dictionary<string, double> _parleyCooldownHours = new Dictionary<string, double>();
        private bool _parleyOpen; // a parley popup is on screen

        // Wave 4.21.0 — the GO-BETWEEN quest: camp entry must be vouched for,
        // per culture, by a contact in the town nearest that chief's seat.
        // CSV of cultures whose camps admit the player (saved). Outlaw Blood
        // bypasses entirely; earned peace also opens the camps.
        private string _campAccessCsv = "";
        private bool _goBetweenToastShown;      // the follow-up notification (saved)
        private float _goBetweenToastDelay;     // runtime timer after origin chosen
        private string _goBetweenMarkedTownId = ""; // map-tracked gate town (saved)
        private string _arrivalToastCsv = "";   // cultures whose arrival toast was shown (saved)
        private string _markedHideoutId = "";   // map-tracked chief hideout after vouching (saved)

        public override void RegisterEvents()
        {
            _instance = this;
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
            // Wave B: proximity parley offers + hunt-pledge tracking.
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, OnMobilePartyDestroyedHunt);
            // Wave 4.21.1: re-register the gate-town map flag after load.
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunchedReMark);
            // Wave 4.22.4: centered arrival toast when the player reaches a gate town.
            CampaignEvents.SettlementEntered.AddNonSerializedListener(this, OnSettlementEnteredArrival);
            // Wave 4.25.1 ("it should be in the quests book, [you] created none in there"):
            // the hunt-pledge CSV state (_huntTargetCulture) is restored by SyncData, but
            // the QuestBase journal object is only created at pledge-time via Begin(). A
            // pledge sworn before this build existed — OR any normal save/reload of a
            // not-yet-persisted pledge — leaves the Quests book empty. Re-create it here.
            // OnGameLoadFinished fires AFTER BanditChiefRegistry recreates the chiefs
            // (v223/v224), so GetChief resolves the quest giver. See memory note
            // feedback_bannerlord_load_event_ordering.
            CampaignEvents.OnGameLoadFinishedEvent.AddNonSerializedListener(this, OnGameLoadFinishedRehydrateHunt);
        }

        private void OnGameLoadFinishedRehydrateHunt()
        {
            // Wave 4.25.3: hideout peace-reconciliation. Runs HERE (not OnSessionLaunched)
            // because IsAtPeaceWith + the culture lookup need fully-restored state. If the
            // marked hideout's culture is already at peace, the go-between guide flag is
            // obsolete → UnmarkHideout (which now also UN-SPOTS, so the hideout vanishes
            // from the map). This retroactively clears a flag left stuck from before the
            // un-spot fix existed.
            try
            {
                if (!string.IsNullOrEmpty(_markedHideoutId))
                {
                    var cidOfMark = CultureOfMarkedHideout();
                    if (!string.IsNullOrEmpty(cidOfMark) && IsAtPeaceWith(cidOfMark))
                    {
                        // 2026-07-02: at peace, the guide flag is obsolete but the camp of a
                        // FRIENDLY culture must never vanish from the map — keep it spotted.
                        Log("hideout peace-reconcile: at peace with " + cidOfMark + " → untracking " + _markedHideoutId + " (stays spotted)");
                        UntrackHideoutKeepSpotted();
                    }
                }
            }
            catch (Exception rex) { Log("hideout peace-reconcile error: " + rex.GetType().Name + ": " + rex.Message); }

            // Hunt-pledge journal re-hydration.
            try
            {
                if (string.IsNullOrEmpty(_huntTargetCulture)) return;          // no active pledge
                if (_huntExpiresHours <= CampaignTime.Now.ToHours) return;     // lapsed; hourly tick clears it
                var chief = GetChief(_huntTargetCulture);                      // null tolerated (Title falls back)
                // Begin() is idempotent: if the quest already persisted in the save it
                // returns immediately, so this never duplicates.
                BandItPlus.Quests.BanditHuntPledgeQuest.Begin(_huntTargetCulture, chief, _huntExpiresHours);
                Log("hunt pledge re-hydrated on load for " + _huntTargetCulture);
            }
            catch (Exception ex) { Log("hunt re-hydrate error: " + ex.GetType().Name + ": " + ex.Message); }
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("_bpOriginChoice", ref _origin);
            dataStore.SyncData("_bpOriginPerksApplied", ref _perksApplied);
            dataStore.SyncData("_bpOriginPeaceCsv", ref _culturesAtPeaceCsv);
            dataStore.SyncData("_bpOriginHomeClan", ref _homeClanCulture);
            dataStore.SyncData("_bpOriginHuntCulture", ref _huntTargetCulture);
            dataStore.SyncData("_bpOriginHuntExpires", ref _huntExpiresHours);
            dataStore.SyncData("_bpOriginCampAccess", ref _campAccessCsv);
            dataStore.SyncData("_bpOriginGoBetweenToast", ref _goBetweenToastShown);
            dataStore.SyncData("_bpOriginGoBetweenTown", ref _goBetweenMarkedTownId);
            dataStore.SyncData("_bpOriginArrivalToasts", ref _arrivalToastCsv);
            dataStore.SyncData("_bpOriginMarkedHideout", ref _markedHideoutId);
            if (_culturesAtPeaceCsv == null) _culturesAtPeaceCsv = "";
            if (_homeClanCulture == null) _homeClanCulture = "";
            if (_huntTargetCulture == null) _huntTargetCulture = "";
            if (_campAccessCsv == null) _campAccessCsv = "";
            if (_goBetweenMarkedTownId == null) _goBetweenMarkedTownId = "";
            if (_arrivalToastCsv == null) _arrivalToastCsv = "";
            if (_markedHideoutId == null) _markedHideoutId = "";
        }

        // ── Wave 4.21.0: camp access (the Go-Between quest) ──

        public bool HasCampAccess(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return false;
            // 2026-07-02 (user: "you can walk in the camp ... that the bug"): while a
            // blood-pledge to THIS culture is unfulfilled, their camps are CLOSED —
            // the chief gave his terms; you do not drink at his fire while you owe
            // him blood. Fulfilment grants peace, which reopens everything below.
            if (!string.IsNullOrEmpty(_huntTargetCulture) && _huntTargetCulture == cultureId) return false;
            // Old saves (no home clan picked) keep the blanket "fires know you" access.
            // New OutlawBlood earns access from the picked home clan (peace) + the go-between.
            if (Origin == BanditOrigin.OutlawBlood && string.IsNullOrEmpty(_homeClanCulture)) return true;
            if (IsAtPeaceWith(cultureId)) return true;            // a chief's word opens his camps
            // Reflavor bridge: a hideout's Culture is the VANILLA id (e.g. steppe_bandits), but
            // peace/vouch is stored under the bp_ clan that reflavors it (bp_steppe_wolves) — so
            // the kin's own camp would read as locked. Honor peace with any bp_ reflavor BEFORE
            // the empty-CSV early return.
            foreach (var bpc in BandItPlus.BiomeEligibility.GetReplacements(cultureId))
                if (IsAtPeaceWith(bpc)) return true;
            if (string.IsNullOrEmpty(_campAccessCsv)) return false;
            foreach (var tok in _campAccessCsv.Split(','))
                if (tok == cultureId) return true;
            // ...and a go-between vouch stored under the bp_ reflavor id.
            foreach (var bpc in BandItPlus.BiomeEligibility.GetReplacements(cultureId))
                foreach (var tok in _campAccessCsv.Split(','))
                    if (tok == bpc) return true;
            return false;
        }

        public void GrantCampAccess(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId) || HasCampAccess(cultureId)) return;
            _campAccessCsv = string.IsNullOrEmpty(_campAccessCsv)
                ? cultureId
                : _campAccessCsv + "," + cultureId;
            Log("camp access granted: " + cultureId);

            // Clear the map flag if this culture's gate town was the tracked one.
            try
            {
                var gate = GetGateTown(cultureId);
                if (gate != null && gate.StringId == _goBetweenMarkedTownId)
                    UnmarkGateTown();
            }
            catch { }
        }

        // ── Map tracker flag on the gate town (the engine's quest-style marker) ──

        private void MarkGateTown(Settlement town)
        {
            try
            {
                if (town == null || Campaign.Current == null) return;
                Campaign.Current.VisualTrackerManager.RegisterObject(town);
                _goBetweenMarkedTownId = town.StringId;
                Log("gate town marked: " + town.Name);
            }
            catch (Exception ex) { Log("MarkGateTown error: " + ex.Message); }
        }

        private void UnmarkGateTown()
        {
            try
            {
                if (string.IsNullOrEmpty(_goBetweenMarkedTownId)) return;
                var town = Settlement.Find(_goBetweenMarkedTownId);
                if (town != null && Campaign.Current != null)
                    Campaign.Current.VisualTrackerManager.RemoveTrackedObject(town);
                Log("gate town unmarked: " + _goBetweenMarkedTownId);
                _goBetweenMarkedTownId = "";
            }
            catch (Exception ex) { Log("UnmarkGateTown error: " + ex.Message); }
        }

        // Wave 4.23.1: after the go-between vouches, flag the chief's HIDEOUT
        // so the player can ride straight to the camp. Cleared on arrival.
        public void MarkChiefHideout(string cultureId)
        {
            try
            {
                var seat = GetChiefSeat(GetChief(cultureId));
                if (seat == null || Campaign.Current == null) return;

                // Wave 4.23.2: hideouts are INVISIBLE on the map until spotted —
                // the go-between's directions count as spotting it, otherwise
                // the tracker flag points at nothing.
                try
                {
                    if (seat.Hideout != null && !seat.Hideout.IsSpotted)
                    {
                        seat.Hideout.IsSpotted = true;
                        seat.IsVisible = true;
                        Log("hideout spotted via go-between: " + seat.Name);
                    }
                }
                catch (Exception spotEx) { Log("spot fail: " + spotEx.Message); }

                Campaign.Current.VisualTrackerManager.RegisterObject(seat);
                _markedHideoutId = seat.StringId;
                Log("hideout marked: " + seat.Name + " (" + cultureId + ")");
            }
            catch (Exception ex) { Log("MarkChiefHideout error: " + ex.Message); }
        }

        // 2026-07-02: public camp-name lookup for callers that announce the marked
        // camp (go-between vouch toast) — resolves through the hideout-preferring seat.
        public string GetChiefCampName(string cultureId)
        {
            try { var s = GetChiefSeat(GetChief(cultureId)); return s != null && s.Name != null ? s.Name.ToString() : null; }
            catch { return null; }
        }

        private void UnmarkHideout()
        {
            try
            {
                if (string.IsNullOrEmpty(_markedHideoutId)) return;
                var s = Settlement.Find(_markedHideoutId);
                if (s != null && Campaign.Current != null)
                {
                    Campaign.Current.VisualTrackerManager.RemoveTrackedObject(s);
                    // Wave 4.25.3 ("if you click leave the hideout will disepear that what
                    // we want it to disapear in the map"): MarkChiefHideout SPOTTED this
                    // hideout (IsSpotted/IsVisible) to put it on the map. Removing the
                    // tracker alone leaves the spotted marker behind, so fully reverse the
                    // spotting — the hideout vanishes from the map, exactly like leaving a
                    // bandit hideout un-spots it in vanilla.
                    try
                    {
                        if (s.Hideout != null && s.Hideout.IsSpotted)
                            s.Hideout.IsSpotted = false;
                        s.IsVisible = false;
                    }
                    catch (Exception spotEx) { Log("un-spot fail: " + spotEx.Message); }
                }
                Log("hideout unmarked: " + _markedHideoutId);
                _markedHideoutId = "";
            }
            catch (Exception ex) { Log("UnmarkHideout error: " + ex.Message); }
        }

        // 2026-07-02 ("didnt marked the hideout"): drop the guide TRACKER but KEEP the
        // camp SPOTTED/visible. The old hunt-accept path called UnmarkHideout, which —
        // since the 4.25.3 un-spot change — made the camp VANISH from the map, so the
        // player left the parley and could never find the camp again. The 4.25.2
        // demark-on-accept design stands (no tracker during the hunt); visibility does not.
        private void UntrackHideoutKeepSpotted()
        {
            try
            {
                if (string.IsNullOrEmpty(_markedHideoutId)) return;
                var s = Settlement.Find(_markedHideoutId);
                if (s != null && Campaign.Current != null && Campaign.Current.VisualTrackerManager != null)
                    Campaign.Current.VisualTrackerManager.RemoveTrackedObject(s);
                Log("hideout untracked (stays spotted): " + _markedHideoutId);
                _markedHideoutId = "";
            }
            catch (Exception ex) { Log("UntrackHideoutKeepSpotted error: " + ex.Message); }
        }

        // Wave 4.25.2: map the currently-marked hideout settlement back to the parley
        // culture whose chief seats there. Used by the load reconciliation to decide
        // whether the guide flag is obsolete (peace already earned). No new saved
        // field needed — derived from the chief registry on demand.
        private string CultureOfMarkedHideout()
        {
            if (string.IsNullOrEmpty(_markedHideoutId)) return null;
            // Primary: the hideout settlement carries the bandit culture directly
            // (e.g. "steppe_bandits", same id as the parley culture) — no chief
            // dependency, so this resolves at any load stage. (BanditRaidBehavior reads
            // settlement.Culture.StringId the same way for IsHideout settlements.)
            var s = Settlement.Find(_markedHideoutId);
            if (s != null && s.Culture != null && !string.IsNullOrEmpty(s.Culture.StringId))
                return s.Culture.StringId;
            // Fallback: match by which chief seats here.
            foreach (var cid in ParleyCultures)
            {
                var seat = GetChiefSeat(GetChief(cid));
                if (seat != null && seat.StringId == _markedHideoutId) return cid;
            }
            return null;
        }

        // Trackers don't persist in saves — re-register on session launch.
        private void OnSessionLaunchedReMark(CampaignGameStarter starter)
        {
            try
            {
                if (!string.IsNullOrEmpty(_goBetweenMarkedTownId))
                {
                    var town = Settlement.Find(_goBetweenMarkedTownId);
                    if (town != null && Campaign.Current != null)
                        Campaign.Current.VisualTrackerManager.RegisterObject(town);
                }
                if (!string.IsNullOrEmpty(_markedHideoutId))
                {
                    // Re-register the tracker for an ACTIVE (not-yet-resolved) marker.
                    // The peace-reconciliation that may UNMARK it instead lives in
                    // OnGameLoadFinishedRehydrateHunt — it needs fully-restored peace
                    // state + chief lookups that aren't ready this early (session-launch
                    // fires ~3s before game-load-finished; see load_event_ordering note).
                    var hid = Settlement.Find(_markedHideoutId);
                    if (hid != null && Campaign.Current != null)
                        Campaign.Current.VisualTrackerManager.RegisterObject(hid);
                }
            }
            catch (Exception ex) { Log("ReMark error: " + ex.Message); }
        }

        // One go-between per culture — the person in town who can vouch the
        // player into that culture's camps. The looters' contact is the
        // one-eyed peddler from the chronicle itself.
        public static string GoBetweenName(string cultureId)
        {
            switch (cultureId)
            {
                case "looters": return BandItPlus.Localization.Get("bp_originbehavior_001", "the One-Eyed Peddler");
                case "forest_bandits": return BandItPlus.Localization.Get("bp_originbehavior_002", "Magda the Fence");
                case "mountain_bandits": return BandItPlus.Localization.Get("bp_originbehavior_003", "Toll-Keeper Brec");
                case "steppe_bandits": return BandItPlus.Localization.Get("bp_originbehavior_004", "Horse-Trader Qara");
                case "sea_raiders": return BandItPlus.Localization.Get("bp_originbehavior_005", "Salt-Wife Ingrun");
                case "desert_bandits": return BandItPlus.Localization.Get("bp_originbehavior_006", "Water-Seller Naim");
                case "bp_frost_reavers": return BandItPlus.Localization.Get("bp_originbehavior_007", "Furrier Olya");
                case "bp_marsh_stalkers": return BandItPlus.Localization.Get("bp_originbehavior_008", "Eel-Monger Sedge");
                case "bp_highwaymen": return BandItPlus.Localization.Get("bp_originbehavior_009", "Coachman Vell");
                case "bp_slaver_caravans": return BandItPlus.Localization.Get("bp_originbehavior_010", "Broker Zissa");
                case "bp_fallen_legionaries": return BandItPlus.Localization.Get("bp_originbehavior_011", "Old Quartermaster Varro");
                case "bp_sky_raiders": return BandItPlus.Localization.Get("bp_originbehavior_012", "Falconer Petra");
                case "bp_steppe_wolves": return BandItPlus.Localization.Get("bp_originbehavior_013", "Hide-Trader Bataar");
                case "bp_pagan_cult": return BandItPlus.Localization.Get("bp_originbehavior_014", "the Quiet Sister");
                default: return BandItPlus.Localization.Get("bp_originbehavior_015", "the go-between");
            }
        }

        // The gate town for a culture: the nearest TOWN to its chief's seat.
        public Settlement GetGateTown(string cultureId)
        {
            try
            {
                var chief = GetChief(cultureId);
                var seat = GetChiefSeat(chief);
                if (seat == null) return null;
                Settlement best = null;
                float bestD = float.MaxValue;
                foreach (var s in Settlement.All)
                {
                    if (s == null || !s.IsTown) continue;
                    float dx = s.Position.X - seat.Position.X;
                    float dy = s.Position.Y - seat.Position.Y;
                    float d = dx * dx + dy * dy;
                    if (d < bestD) { bestD = d; best = s; }
                }
                return best;
            }
            catch { return null; }
        }

        // Cultures that can hold parleys / need go-betweens (shared with
        // BanditDialogManager's town menu).
        internal static string[] AllParleyCultures => ParleyCultures;

        // ── Wave 4.22.4: arrival notification (CENTERED toast) when the player
        // reaches a gate town — fired by proximity (hourly) or entry, once per
        // culture (saved CSV). ──

        private const float ARRIVAL_RANGE = 10f; // world units for "close to"

        private bool ArrivalShown(string cid)
        {
            if (string.IsNullOrEmpty(_arrivalToastCsv)) return false;
            foreach (var tok in _arrivalToastCsv.Split(','))
                if (tok == cid) return true;
            return false;
        }

        private void TryShowArrivalToast(Settlement town)
        {
            try
            {
                if (town == null || !town.IsTown) return;
                if (!OriginChosen || (Origin == BanditOrigin.OutlawBlood && string.IsNullOrEmpty(_homeClanCulture))) return;
                foreach (var cid in ParleyCultures)
                {
                    if (HasCampAccess(cid) || ArrivalShown(cid)) continue;
                    var gate = GetGateTown(cid);
                    if (gate != town) continue;
                    // One arrival toast per TOWN, not per culture: mark this culture AND every
                    // reflavor twin that shares this gate town as shown, so a vanilla/bp pair
                    // (e.g. steppe_bandits + bp_steppe_wolves) can't double-toast the same go-between.
                    foreach (var twin in ParleyCultures)
                        if (!ArrivalShown(twin) && GetGateTown(twin) == town)
                            _arrivalToastCsv = string.IsNullOrEmpty(_arrivalToastCsv)
                                ? twin : _arrivalToastCsv + "," + twin;
                    string clan = CultureDisplay(cid);
                    BandItPlus.UI.BanditToastScreen.Show(
                        BandItPlus.Localization.Get("bp_originbehavior_016", "You Have Arrived"),
                        new TextObject("{=bp_hco_007}{GOBETWEEN} is here in {TOWN}. Seek them out — they can get you into the {CLAN} camps.")
                            .SetTextVariable("GOBETWEEN", GoBetweenName(cid))
                            .SetTextVariable("TOWN", town.Name)
                            .SetTextVariable("CLAN", clan).ToString(),
                        "event:/ui/notification/relation");
                    Log("arrival toast: " + cid + " at " + town.Name);
                    return; // one at a time
                }
            }
            catch (Exception ex) { Log("TryShowArrivalToast error: " + ex.Message); }
        }

        private void OnSettlementEnteredArrival(MobileParty party, Settlement settlement, Hero hero)
        {
            if (party == null || party != MobileParty.MainParty) return;
            // Reached a flagged settlement → clear its map marker. The player is now
            // physically inside, so the guide flag is obsolete (no point pointing them
            // at a settlement they're standing in). Covers BOTH the chief hideout and
            // the go-between gate town. Wave 4.25.4 ("unmark the settlement since user
            // is already inside").
            if (settlement != null && settlement.StringId == _markedHideoutId)
                UnmarkHideout();
            if (settlement != null && settlement.StringId == _goBetweenMarkedTownId)
                UnmarkGateTown();
            TryShowArrivalToast(settlement);
        }

        // The follow-up crest toast: points at the go-between for the culture
        // whose chief seat is nearest the player right now.
        private bool ShowGoBetweenToast()
        {
            var main = MobileParty.MainParty;
            if (main == null) return false;
            string bestCid = null;
            Settlement bestTown = null;
            float bestD = float.MaxValue;
            foreach (var cid in ParleyCultures)
            {
                if (HasCampAccess(cid)) continue;
                var chief = GetChief(cid);
                var seat = GetChiefSeat(chief);
                if (seat == null) continue;
                float dx = main.Position.X - seat.Position.X;
                float dy = main.Position.Y - seat.Position.Y;
                float d = dx * dx + dy * dy;
                var town = GetGateTown(cid);
                if (town == null) continue;
                if (d < bestD) { bestD = d; bestCid = cid; bestTown = town; }
            }
            if (bestCid == null || bestTown == null) return false;
            string clan = CultureDisplay(bestCid);
            BandItPlus.UI.BanditToastScreen.Show(
                BandItPlus.Localization.Get("bp_originbehavior_017", "The Go-Between"),
                new TextObject("{=bp_hco_008}The camps admit no strangers. Find {GOBETWEEN} in {TOWN} — they can get you into the {CLAN} camps.")
                    .SetTextVariable("GOBETWEEN", GoBetweenName(bestCid))
                    .SetTextVariable("TOWN", bestTown.Name)
                    .SetTextVariable("CLAN", clan).ToString(),
                "event:/ui/notification/relation");
            MarkGateTown(bestTown); // quest-style flag on the map
            Log("go-between toast: " + bestCid + " via " + bestTown.Name);
            return true;
        }

        private void OnTick(float dt)
        {
            // (Intro book animations are ticked from SubModuleClassEntry's
            // OnApplicationTick — the campaign tick stops while the book
            // pauses the game.)

            // Wave 4.21.0: the follow-up notification after the road is sworn —
            // names the first go-between and their town. Outlaw Blood skips it
            // (the camps already know him).
            if (OriginChosen && !_goBetweenToastShown)
            {
                _goBetweenToastDelay += dt;
                if (_goBetweenToastDelay >= 14f)
                {
                    try
                    {
                        // Consume the one-shot guard ONLY on a real show. Previously
                        // _goBetweenToastShown was set BEFORE the toast; if the deferred
                        // attempt early-returned (party/town not ready) the guard — a
                        // [SaveableField] — was burned forever, suppressing the follow-up
                        // this session AND across reloads. Now a not-ready attempt retries.
                        bool shown;
                        if (Origin != BanditOrigin.OutlawBlood || !string.IsNullOrEmpty(_homeClanCulture)) shown = ShowGoBetweenToast();
                        else
                        {
                            // Outlaw Blood needs no go-between — but he still
                            // gets the follow-up so the flow feels complete.
                            BandItPlus.UI.BanditToastScreen.Show(
                                BandItPlus.Localization.Get("bp_originbehavior_018", "The Fires Know You"),
                                BandItPlus.Localization.Get("bp_originbehavior_019", "No vouching needed — every camp already trades with you. Ride to any hideout and walk in under your own name."),
                                "event:/ui/notification/relation");
                            shown = true;
                        }
                        if (shown) _goBetweenToastShown = true;
                    }
                    catch (Exception gbEx) { Log("go-between toast error: " + gbEx.Message); }
                }
            }

            // Wave 4.25.10: deliver the queued parchment camp-invitation letter a few
            // seconds after the pledge is paid (reads like a rider catching up). Sits
            // ABOVE the OriginChosen early-return — the hunt completes long after the
            // origin is chosen, so anything below would never run for it.
            if (!string.IsNullOrEmpty(_pendingPeaceLetterCid))
            {
                _peaceLetterDelay += dt;
                // 07-11 crash fix: deliver ONLY while the map is the active state.
                // A focus letter opened over a native screen (quests, inventory)
                // attaches to the wrong screen and corrupts the reactivation of
                // the screen underneath. The letter simply waits for the map.
                bool onMap = false;
                try
                {
                    onMap = TaleWorlds.Core.Game.Current != null
                        && TaleWorlds.Core.Game.Current.GameStateManager != null
                        && TaleWorlds.Core.Game.Current.GameStateManager.ActiveState
                            is TaleWorlds.CampaignSystem.GameState.MapState;
                }
                catch { }
                if (_peaceLetterDelay >= 3.5f && onMap)
                {
                    string letterCid = _pendingPeaceLetterCid;
                    _pendingPeaceLetterCid = "";
                    try { ShowPeaceLetter(letterCid); }
                    catch (Exception plEx) { Log("peace letter error: " + plEx.GetType().Name + ": " + plEx.Message); }
                }
            }

            // 2026-07-02 ("i want it to appear after the Start Game"): the polished
            // title cinematic plays ONCE PER SESSION right after the map settles — on
            // campaigns whose origin is ALREADY chosen. Fresh campaigns are excluded
            // here because the full intro chain below opens the same cinematic first
            // (no double play). Sits ABOVE the OriginChosen early-return for the same
            // reason as the peace letter. Skippable as always; MCM-gated.
            // 2026-07-02 ("load your save should not play cinematic, only fresh game"):
            // the title cinematic belongs to the fresh-campaign intro chain below and
            // NOWHERE else — loads go straight to the map.
            if (_popupShown || OriginChosen) return;
            try
            {
                if (Campaign.Current == null || Hero.MainHero == null || MobileParty.MainParty == null) return;

                // 07-14: the origin is chosen NATIVELY now, as a backstory question in
                // the game's own character creation (the old custom cinematic + choice
                // screen read as "AI slop"). CharacterCreationOriginQuestionPatch records
                // the pick; here on the first map tick we apply its effects and never
                // show the old custom chain.
                int nativePick = BandItPlus.Patches.CharacterCreationOriginQuestionPatch.ChosenOrigin;
                if (nativePick >= 1 && nativePick <= 3)
                {
                    _popupShown = true;
                    BandItPlus.Patches.CharacterCreationOriginQuestionPatch.ChosenOrigin = -1; // consume
                    ApplyOrigin(nativePick); // sets _origin + grants the themed kit once
                    return;
                }

                // Fallback ONLY when the native question recorded nothing (patch failed
                // to attach, or a legacy save predating the origin system): the game's
                // NATIVE inquiry — no custom Gauntlet — after a short settle.
                _popupDelay += dt;
                if (_popupDelay < 3.0f) return; // let the map settle first
                _popupShown = true;
                ShowOriginPopup(); // native MultiSelectionInquiry (no custom Gauntlet)
            }
            catch (Exception ex)
            {
                _popupShown = true; // never spam a failing popup every tick
                Log("OnTick popup error: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Wave 4.25.10: the chief's parchment camp-invitation, shown a few seconds
        // after the pledge is paid (queued in the hunt-fulfilment handler, fired by
        // OnTick above). Book-ink prose over the player-authored "letter" parchment.
        private void ShowPeaceLetter(string cultureId)
        {
            Hero chief = GetChief(cultureId);
            string chiefName = chief?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_originbehavior_020", "the chief");
            string culture = CultureDisplay(cultureId);
            string title = BandItPlus.Localization.Get("bp_originbehavior_021", "An Invitation");
            string body = new TextObject("{=bp_hco_009}From the camp of the <span style=\"PlaceInk\">{CULTURE}</span>, by my own hand.\n\nWord reaches our fires, stranger.\n\nYou spilled <span style=\"NameInk\">rival blood</span> for our sake — and a debt of blood is paid in kind. The camps of the <span style=\"PlaceInk\">{CULTURE}</span> are open to you now. Come and sit at our hearth; eat our salt, trade with our people, and ride with our riders if you have the stomach for the work.\n\nNo blade of ours will seek you on the road again. Ask for me by name at any fire, and you will not be turned away — you have bought that much in blood.\n\nUntil the roads bring you to our camp,\n— <span style=\"NameInk\">{CHIEFNAME}</span>, who keeps the <span style=\"PlaceInk\">{CULTURE}</span>")
                .SetTextVariable("CULTURE", culture)
                .SetTextVariable("CHIEFNAME", chiefName).ToString();
            BandItPlus.UI.BanditLetterScreen.Open(title, body, null);
            Log("peace letter shown for " + cultureId + " (chief " + chiefName + ")");
        }

        // Applies the chosen origin — shared by the native question path and the
        // inquiry fallback.
        private void ApplyOrigin(int originId)
        {
            try
            {
                _origin = originId;
                Log("origin chosen: " + Origin);
                GrantOriginPerks(); // Wave 4.27.0: the origin now shapes your starting character
                string title;
                string msg;
                switch (Origin)
                {
                    case BanditOrigin.OutlawBlood:
                        title = BandItPlus.Localization.Get("bp_originbehavior_022", "Outlaw Blood — the road accepts");
                        msg = BandItPlus.Localization.Get("bp_originbehavior_023", "The clans remember your name. The camps trade with you from day one, and peace costs half.");
                        break;
                    case BanditOrigin.Lawkeeper:
                        title = BandItPlus.Localization.Get("bp_originbehavior_024", "Lawkeeper — the road accepts");
                        msg = BandItPlus.Localization.Get("bp_originbehavior_025", "Every bandit head pays head-money, bounties pay a quarter richer — but peace costs double.");
                        break;
                    default:
                        title = BandItPlus.Localization.Get("bp_originbehavior_026", "Drifter — the road accepts");
                        msg = BandItPlus.Localization.Get("bp_originbehavior_027", "You owe the road nothing. Camp vendors warm to you twice as fast. Seek the chiefs to parley.");
                        break;
                }
                // Wave 4.20.1: the custom crest toast (left-center, logo1) is the
                // notification; the engine line stays as a log-style echo.
                try { BandItPlus.UI.BanditToastScreen.Show(title, msg); } catch { }
                try
                {
                    InformationManager.DisplayMessage(new InformationMessage("⚑ " + msg, Colors.Yellow));
                }
                catch { }

                // Wave B: point the player at the nearest chiefs.
                AnnounceNearestChiefs(3);

                // OutlawBlood picks the clan that raised them — kin, allied from turn one.
                // Fresh characters only (old saves have no home clan and keep blanket access).
                if (Origin == BanditOrigin.OutlawBlood && string.IsNullOrEmpty(_homeClanCulture))
                {
                    try { ShowHomeClanPick(); } catch (Exception hcEx) { Log("home-clan pick error: " + hcEx.Message); }
                }
            }
            catch (Exception ex)
            {
                Log("ApplyOrigin error: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // The 8 custom outlaw clans an OutlawBlood player can be born into. The picked clan
        // becomes kin (peace + camp access from turn one); the rest are earned as normal.
        private static readonly string[] HomeClanCultures =
        {
            "bp_frost_reavers", "bp_marsh_stalkers", "bp_highwaymen", "bp_slaver_caravans",
            "bp_fallen_legionaries", "bp_sky_raiders", "bp_steppe_wolves", "bp_pagan_cult",
        };

        private void ShowHomeClanPick()
        {
            var elements = new List<InquiryElement>();
            foreach (var cid in HomeClanCultures)
            {
                string name = ClanDisplayName(cid);
                string shortId = cid.StartsWith("bp_") ? cid.Substring(3) : cid;
                string blurb = BandItPlus.Localization.Get("bp_blurb_" + shortId, name);
                elements.Add(new InquiryElement(cid, name, null, true, blurb));
            }
            MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                BandItPlus.Localization.Get("bp_homeclan_title", "The Clan That Raised You"),
                BandItPlus.Localization.Get("bp_homeclan_desc", "Outlaw blood runs in you. Whose fires did you grow up beside? They are kin — at peace, their camps open from the first day — while the rest of the wilds you must still win over."),
                elements,
                /* isExitShown: */ false,
                /* minSelectableOptionCount: */ 1,
                /* maxSelectableOptionCount: */ 1,
                BandItPlus.Localization.Get("bp_homeclan_choose", "Claim your kin"),
                null,
                OnHomeClanPicked,
                null));
        }

        private void OnHomeClanPicked(List<InquiryElement> picked)
        {
            try
            {
                if (picked == null || picked.Count == 0) return;
                string cid = picked[0].Identifier as string;
                if (string.IsNullOrEmpty(cid)) return;
                _homeClanCulture = cid;
                GrantPeace(cid);
                GrantCampAccess(cid);
                string name = ClanDisplayName(cid);
                try
                {
                    BandItPlus.UI.BanditToastScreen.Show(
                        BandItPlus.Localization.Get("bp_homeclan_done_title", "Kin at the Fire"),
                        new TextObject("{=bp_homeclan_done}You are kin to the {CLAN}. Their camps are open and their blades are yours; the rest of the wilds you must still win.")
                            .SetTextVariable("CLAN", name).ToString());
                }
                catch { }
                Log("OutlawBlood home clan picked: " + cid);
            }
            catch (Exception ex) { Log("OnHomeClanPicked error: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static string ClanDisplayName(string cultureId)
        {
            return CultureDisplay(cultureId);
        }

        private void ShowOriginPopup()
        {
            var elements = new List<InquiryElement>
            {
                // identifier, title, image (Wave C: custom sprites), enabled, hint
                new InquiryElement((int)BanditOrigin.OutlawBlood,
                    BandItPlus.Localization.Get("bp_originbehavior_028", "Outlaw Blood"),
                    null, true,
                    BandItPlus.Localization.Get("bp_originbehavior_029", "Raised among the lawless — the chiefs remember your name. You start at war like everyone, but when you come to parley, their peace terms cost HALF.")),
                new InquiryElement((int)BanditOrigin.Drifter,
                    BandItPlus.Localization.Get("bp_originbehavior_030", "Drifter"),
                    null, true,
                    BandItPlus.Localization.Get("bp_originbehavior_031", "You owe nothing to anyone. At war with every clan — but the chiefs will hear an outsider's terms at the usual price. Seek them in their hideouts.")),
                new InquiryElement((int)BanditOrigin.Lawkeeper,
                    BandItPlus.Localization.Get("bp_originbehavior_032", "Lawkeeper"),
                    null, true,
                    BandItPlus.Localization.Get("bp_originbehavior_033", "You hunted outlaws once; the road remembers. Bounties on raiders pay +25% — but any chief who deigns to parley will demand DOUBLE.")),
            };

            MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                BandItPlus.Localization.Get("bp_originbehavior_034", "Blood & Banners"),
                BandItPlus.Localization.Get("bp_originbehavior_035", "How did you come to Calradia's roads? Your past decides how the bandit clans receive you."),
                elements,
                /* isExitShown: */ false,
                /* minSelectableOptionCount: */ 1,
                /* maxSelectableOptionCount: */ 1,
                BandItPlus.Localization.Get("bp_originbehavior_036", "Choose"),
                null,
                OnOriginChosen,
                null));
        }

        private void OnOriginChosen(List<InquiryElement> picked)
        {
            try
            {
                if (picked == null || picked.Count == 0) { _popupShown = false; _popupDelay = 0f; return; }
                _origin = (int)picked[0].Identifier;
                Log("origin chosen: " + Origin);
                GrantOriginPerks(); // Wave 4.27.0: same starting kit via the fallback path
                string msg;
                switch (Origin)
                {
                    case BanditOrigin.OutlawBlood:
                        msg = BandItPlus.Localization.Get("bp_originbehavior_037", "The clans remember your name. When you parley, their terms will cost you half.");
                        break;
                    case BanditOrigin.Lawkeeper:
                        msg = BandItPlus.Localization.Get("bp_originbehavior_038", "The road remembers the lawkeeper. Bounties pay 25% more — and peace, should you ever want it, costs double.");
                        break;
                    default:
                        msg = BandItPlus.Localization.Get("bp_originbehavior_039", "You owe the road nothing. Seek the chiefs in their hideouts to parley for peace.");
                        break;
                }
                try
                {
                    InformationManager.DisplayMessage(new InformationMessage("⚑ " + msg, Colors.Yellow));
                }
                catch { }

                // Wave B: point the player at the nearest chiefs (user request: "the
                // user will receive the message what settlements he has to enter").
                AnnounceNearestChiefs(3);
            }
            catch (Exception ex)
            {
                Log("OnOriginChosen error: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ───────────────────── Wave B: earning peace ─────────────────────

        // Every culture that can hold a parley (must have a chief in the registry).
        private static readonly string[] ParleyCultures =
        {
            "looters", "forest_bandits", "mountain_bandits", "steppe_bandits", "sea_raiders", "desert_bandits",
            "bp_frost_reavers", "bp_marsh_stalkers", "bp_highwaymen", "bp_slaver_caravans",
            "bp_fallen_legionaries", "bp_sky_raiders", "bp_steppe_wolves", "bp_pagan_cult",
        };

        private const float PARLEY_RANGE = 8f;            // world units from the chief's hideout
        // Wave 4.25.0: 24h expired in ~80 real seconds at fast-forward (log 07:21→07:22
        // re-offers) and read as the double-open bug returning. 48h keeps declines quiet.
        private const double PARLEY_DECLINE_COOLDOWN = 48.0; // hours before re-offer
        private const double HUNT_DURATION_HOURS = 168.0;    // 7 in-game days
        private const int TRIBUTE_BASE_GOLD = 1500;

        private Hero GetChief(string cultureId)
        {
            try { return BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(cultureId); }
            catch { return null; }
        }

        // 2026-07-02 seat fix ("didnt marked the hideout"): a chief's SEAT is his
        // HIDEOUT. CurrentSettlement/HomeSettlement can be a TOWN (template artifact /
        // chief visiting — observed live: "The Reeve" seated at Amprela), which pointed
        // the envoy text, the guide marker and the camp-menu flow at a town with no
        // Hideout component. Prefer any hideout the chief is at/from; fall back to the
        // nearest hideout of his own culture; only then the raw settlement.
        private Settlement GetChiefSeat(Hero chief)
        {
            if (chief == null) return null;
            Settlement cur = null, home = null;
            try { cur = chief.CurrentSettlement; } catch { }
            try { home = chief.HomeSettlement; } catch { }
            if (cur != null && cur.IsHideout) return cur;
            if (home != null && home.IsHideout) return home;
            try
            {
                var anchor = cur ?? home;
                Settlement best = null; float bestSq = float.MaxValue;
                foreach (var s in Settlement.All)
                {
                    if (s == null || !s.IsHideout || s.Culture != chief.Culture) continue;
                    if (anchor == null) { best = s; break; }
                    float dx = s.Position.X - anchor.Position.X, dy = s.Position.Y - anchor.Position.Y;
                    float d = dx * dx + dy * dy;
                    if (d < bestSq) { bestSq = d; best = s; }
                }
                if (best != null) return best;
            }
            catch { }
            return cur ?? home;
        }

        // Post-origin guidance: name the nearest chiefs' hideouts so the player
        // knows where to go (user request).
        private void AnnounceNearestChiefs(int count)
        {
            try
            {
                var main = MobileParty.MainParty;
                if (main == null) return;
                var ranked = new List<KeyValuePair<float, string>>();
                foreach (var cid in ParleyCultures)
                {
                    var chief = GetChief(cid);
                    var seat = GetChiefSeat(chief);
                    if (chief == null || seat == null) continue;
                    var dx = main.Position.X - seat.Position.X;
                    var dy = main.Position.Y - seat.Position.Y;
                    ranked.Add(new KeyValuePair<float, string>(dx * dx + dy * dy,
                        new TextObject("{=bp_hco_010}{CHIEF} holds court at {SEAT}.")
                            .SetTextVariable("CHIEF", chief.Name)
                            .SetTextVariable("SEAT", seat.Name).ToString()));
                }
                ranked.Sort((a, b) => a.Key.CompareTo(b.Key));
                for (int i = 0; i < ranked.Count && i < count; i++)
                {
                    try
                    {
                        InformationManager.DisplayMessage(new InformationMessage(
                            new TextObject("{=bp_hco_011}⚑ Word on the road: {LINE}")
                                .SetTextVariable("LINE", ranked[i].Value).ToString(), Colors.Yellow));
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log("AnnounceNearestChiefs error: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void OnHourlyTick()
        {
            try
            {
                if (!OriginChosen || _parleyOpen) return;
                if (Campaign.Current == null || MobileParty.MainParty == null) return;

                // Hunt pledge expiry.
                if (!string.IsNullOrEmpty(_huntTargetCulture)
                    && CampaignTime.Now.ToHours >= _huntExpiresHours)
                {
                    var lapsed = _huntTargetCulture;
                    _huntTargetCulture = "";
                    try
                    {
                        InformationManager.DisplayMessage(new InformationMessage(
                            new TextObject("{=bp_hco_012}⚑ The chief's patience has run out — your pledge to the {CULTURE} is void. Return to their hideout to parley again.")
                                .SetTextVariable("CULTURE", CultureDisplay(lapsed)).ToString(), Colors.Red));
                    }
                    catch { }
                    Log("hunt pledge expired for " + lapsed);
                    // Wave 4.25.0: close the journal entry too. QuestBase's own timer may
                    // already have finalized it (OnTimedOut) — FindActive returns null then.
                    try { BandItPlus.Quests.BanditHuntPledgeQuest.FindActive(lapsed)?.ConcludeLapsed(); }
                    catch (Exception qex) { Log("hunt lapse quest close error: " + qex.Message); }
                }

                // Proximity parley: standing near a chief's hideout while at war.
                var main = MobileParty.MainParty;

                // Wave 4.22.4: arrival toast when CLOSE to a gate town (not only
                // on entry) — checks the nearest town within ARRIVAL_RANGE.
                try
                {
                    foreach (var s in Settlement.All)
                    {
                        if (s == null || !s.IsTown) continue;
                        float adx = main.Position.X - s.Position.X;
                        float ady = main.Position.Y - s.Position.Y;
                        if (adx * adx + ady * ady > ARRIVAL_RANGE * ARRIVAL_RANGE) continue;
                        TryShowArrivalToast(s);
                        break;
                    }
                }
                catch { }

                // Old-save OutlawBlood has blanket HasCampAccess (not a "vouch"), which would
                // let this proximity trigger auto-open the parley at any hideout with no player
                // interaction. New OutlawBlood only has standing with the picked home clan
                // (at peace -> skipped below) + earned clans, so the normal gate applies to him.
                if (Origin == BanditOrigin.OutlawBlood && string.IsNullOrEmpty(_homeClanCulture)) return;
                double nowH = CampaignTime.Now.ToHours;
                foreach (var cid in ParleyCultures)
                {
                    if (IsAtPeaceWith(cid)) continue;
                    // Wave 4.25.5 (bug: parley opened at an UN-vouched hideout before the
                    // player ever met the go-between guide). The parley must only be
                    // reachable AFTER camp access is earned — HasCampAccess is the vouch
                    // predicate (true for Outlaw Blood free-trade, for cultures already at
                    // peace, and for cultures the go-between vouched via GrantCampAccess,
                    // which is called alongside MarkChiefHideout in BanditDialogManager).
                    // Without this gate the proximity trigger fired for ANY chief hideout
                    // the player merely stumbled near, regardless of vouch state.
                    if (!HasCampAccess(cid)) continue;
                    // Wave 4.24.1 (log: re-opened 1s after pledging): an active
                    // hunt pledge IS this chief's terms — no re-parley until it
                    // resolves or lapses.
                    if (cid == _huntTargetCulture) continue;
                    double cd;
                    if (_parleyCooldownHours.TryGetValue(cid, out cd) && nowH < cd) continue;
                    var chief = GetChief(cid);
                    var seat = GetChiefSeat(chief);
                    if (chief == null || seat == null || !chief.IsAlive) continue;
                    var dx = main.Position.X - seat.Position.X;
                    var dy = main.Position.Y - seat.Position.Y;
                    if (dx * dx + dy * dy > PARLEY_RANGE * PARLEY_RANGE) continue;

                    ShowParley(cid, chief, seat);
                    return; // one parley at a time
                }
            }
            catch (Exception ex) { Log("OnHourlyTick error: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void ShowParley(string cultureId, Hero chief, Settlement seat)
        {
            _parleyOpen = true;
            int tribute = (int)(TRIBUTE_BASE_GOLD * PeaceCostMultiplier);
            bool canPay = Hero.MainHero != null && Hero.MainHero.Gold >= tribute;
            bool huntBusy = !string.IsNullOrEmpty(_huntTargetCulture);

            // Wave 4.24.0: BP's own parley panel first; vanilla inquiry only if
            // the custom screen fails to mount.
            bool customOpened = BandItPlus.UI.BanditParleyScreen.Open(
                new TextObject("{=bp_hco_013}Parley — {CHIEF}").SetTextVariable("CHIEF", chief.Name).ToString(),
                new TextObject("{=bp_hco_014}An envoy under a white flag leads you toward {SEAT}. {CHIEF} will hear your terms — once.")
                    .SetTextVariable("SEAT", seat.Name).SetTextVariable("CHIEF", chief.Name).ToString(),
                new TextObject("{=bp_hco_015}Pay tribute — {AMOUNT} gold").SetTextVariable("AMOUNT", tribute).ToString(),
                canPay
                    ? BandItPlus.Localization.Get("bp_originbehavior_040", "Buy peace outright. The chief's word is good; his clan will never trouble you again.")
                    : new TextObject("{=bp_hco_016}You cannot afford the chief's price ({AMOUNT} gold).").SetTextVariable("AMOUNT", tribute).ToString(),
                canPay,
                BandItPlus.Localization.Get("bp_originbehavior_041", "Pledge a hunt"),
                huntBusy
                    ? BandItPlus.Localization.Get("bp_originbehavior_042", "You already owe a hunt to another chief. Finish that pledge first.")
                    : BandItPlus.Localization.Get("bp_originbehavior_043", "Slay any RIVAL bandit war band (a different culture) within 7 days, and the chief grants peace for blood instead of gold."),
                !huntBusy,
                BandItPlus.Localization.Get("bp_originbehavior_044", "Leave the parley. The chief will hear your terms again in about 2 days."),
                choice => OnParleyChoiceCore(cultureId, chief, choice),
                chief); // v7: chief's portrait + banner on the board, culture-keyed lore
            if (customOpened) return;

            var elements = new List<InquiryElement>
            {
                new InquiryElement("pay",
                    new TextObject("{=bp_hco_015}Pay tribute — {AMOUNT} gold").SetTextVariable("AMOUNT", tribute).ToString(),
                    null, canPay,
                    canPay
                        ? BandItPlus.Localization.Get("bp_originbehavior_040", "Buy peace outright. The chief's word is good; his clan will never trouble you again.")
                        : new TextObject("{=bp_hco_016}You cannot afford the chief's price ({AMOUNT} gold).").SetTextVariable("AMOUNT", tribute).ToString()),
                new InquiryElement("hunt",
                    BandItPlus.Localization.Get("bp_originbehavior_041", "Pledge a hunt"),
                    null, !huntBusy,
                    huntBusy
                        ? BandItPlus.Localization.Get("bp_originbehavior_042", "You already owe a hunt to another chief. Finish that pledge first.")
                        : BandItPlus.Localization.Get("bp_originbehavior_043", "Slay any RIVAL bandit war band (a different culture) within 7 days, and the chief grants peace for blood instead of gold.")),
                new InquiryElement("leave",
                    BandItPlus.Localization.Get("bp_originbehavior_045", "Walk away"),
                    null, true,
                    BandItPlus.Localization.Get("bp_originbehavior_044", "Leave the parley. The chief will hear your terms again in about 2 days.")),
            };

            MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                new TextObject("{=bp_hco_013}Parley — {CHIEF}").SetTextVariable("CHIEF", chief.Name).ToString(),
                new TextObject("{=bp_hco_014}An envoy under a white flag leads you toward {SEAT}. {CHIEF} will hear your terms — once.")
                    .SetTextVariable("SEAT", seat.Name).SetTextVariable("CHIEF", chief.Name).ToString(),
                elements,
                /* isExitShown: */ false,
                /* minSelectableOptionCount: */ 1,
                /* maxSelectableOptionCount: */ 1,
                BandItPlus.Localization.Get("bp_originbehavior_046", "Decide"),
                null,
                picked => OnParleyChoice(cultureId, chief, picked),
                null));
        }

        // Inquiry-fallback adapter — the custom panel calls the Core directly.
        private void OnParleyChoice(string cultureId, Hero chief, List<InquiryElement> picked)
        {
            string choice = picked != null && picked.Count > 0 ? picked[0].Identifier as string : "leave";
            OnParleyChoiceCore(cultureId, chief, choice);
        }

        private void OnParleyChoiceCore(string cultureId, Hero chief, string choice)
        {
            _parleyOpen = false;
            try
            {
                if (string.IsNullOrEmpty(choice)) choice = "leave";
                double nowH = CampaignTime.Now.ToHours;
                switch (choice)
                {
                    case "pay":
                        int tribute = (int)(TRIBUTE_BASE_GOLD * PeaceCostMultiplier);
                        if (Hero.MainHero == null || Hero.MainHero.Gold < tribute)
                        {
                            _parleyCooldownHours[cultureId] = nowH + PARLEY_DECLINE_COOLDOWN;
                            return;
                        }
                        TaleWorlds.CampaignSystem.Actions.GiveGoldAction
                            .ApplyBetweenCharacters(Hero.MainHero, chief, tribute, false);
                        GrantPeace(cultureId);
                        try
                        {
                            InformationManager.DisplayMessage(new InformationMessage(
                                new TextObject("{=bp_hco_017}⚑ Peace with the {CULTURE} — sealed with {AMOUNT} gold. Their blades are no longer yours to fear.")
                                    .SetTextVariable("CULTURE", CultureDisplay(cultureId)).SetTextVariable("AMOUNT", tribute).ToString(),
                                Colors.Green));
                        }
                        catch { }
                        Log("parley: tribute peace with " + cultureId + " for " + tribute);
                        // Wave 4.25.2: deal struck at the camp → the go-between's guide
                        // flag has served its purpose. Clear it (the hideout stays
                        // spotted, so it's still findable manually).
                        UnmarkHideout();
                        break;

                    case "hunt":
                        _huntTargetCulture = cultureId;
                        _huntExpiresHours = nowH + HUNT_DURATION_HOURS;
                        try
                        {
                            InformationManager.DisplayMessage(new InformationMessage(
                                new TextObject("{=bp_hco_018}⚑ Pledge sworn: slay any rival bandit war band within 7 days, and the {CULTURE} will grant you peace.")
                                    .SetTextVariable("CULTURE", CultureDisplay(cultureId)).ToString(),
                                Colors.Yellow));
                        }
                        catch { }
                        Log("parley: hunt pledged for " + cultureId + " (expires +" + HUNT_DURATION_HOURS + "h)");
                        // Wave 4.25.0 ("i want pludge the hunt has run as a quest"): journal
                        // quest with the 7-day countdown + objective text. CSV state above
                        // stays the mechanical source of truth.
                        BandItPlus.Quests.BanditHuntPledgeQuest.Begin(cultureId, chief, _huntExpiresHours);
                        // Wave 4.25.2 + 2026-07-02 (user: "the hideout should disappear when
                        // he accepts"): pledge sworn → the camp CLOSES and VANISHES until the
                        // blood-debt is paid. Full unmark (tracker + un-spot). Fulfilment
                        // re-marks, re-spots and NAMES the camp (see the fulfil path).
                        UnmarkHideout();
                        break;

                    default:
                        _parleyCooldownHours[cultureId] = nowH + PARLEY_DECLINE_COOLDOWN;
                        try
                        {
                            InformationManager.DisplayMessage(new InformationMessage(
                                new TextObject("{=bp_hco_019}You leave the parley. The {CULTURE} chief will hear your terms again in about {DAYS} days.")
                                    .SetTextVariable("CULTURE", CultureDisplay(cultureId)).SetTextVariable("DAYS", (int)(PARLEY_DECLINE_COOLDOWN / 24.0)).ToString(),
                                Colors.White));
                        }
                        catch { }
                        Log("parley: declined for " + cultureId);
                        break;
                }

                // Living Chronicle — a parley concluded (pay = bought peace, hunt = blood-terms).
                if (choice == "pay" || choice == "hunt")
                    BanditChronicleBehavior.Instance?.Unlock(
                        "first_parley", chief != null ? chief.Name.ToString() : "");
            }
            catch (Exception ex) { Log("OnParleyChoice error: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void OnMobilePartyDestroyedHunt(MobileParty party, PartyBase destroyer)
        {
            try
            {
                if (string.IsNullOrEmpty(_huntTargetCulture) || party == null) return;

                // Wave 4.25.0 ("witch i did and it didnt do nothing"): every guard below
                // used to return SILENTLY — a non-qualifying kill looked like a dead
                // feature. Candidates (bandit-culture parties) now log their rejection
                // reason, and the two confusable cases hint in-game.
                var cid = party.MapFaction != null && party.MapFaction.Culture != null
                    ? party.MapFaction.Culture.StringId : null;
                bool isBanditCulture = false;
                if (!string.IsNullOrEmpty(cid))
                    foreach (var pc in ParleyCultures)
                        if (pc == cid) { isBanditCulture = true; break; }
                if (!isBanditCulture) return; // lords/villagers/caravans die constantly — not candidates, no log

                bool playerKill = destroyer != null
                    && (destroyer == PartyBase.MainParty
                        || (destroyer.MobileParty != null && destroyer.MobileParty == MobileParty.MainParty)
                        || (destroyer.LeaderHero != null && destroyer.LeaderHero == Hero.MainHero));
                // Hideout assaults and army fights can report a different destroyer —
                // credit any band that died in the PLAYER'S map event.
                if (!playerKill)
                {
                    try
                    {
                        var pme = TaleWorlds.CampaignSystem.MapEvents.MapEvent.PlayerMapEvent;
                        playerKill = pme != null && party.Party != null && party.Party.MapEvent == pme;
                    }
                    catch { }
                }
                if (!playerKill)
                {
                    Log("hunt: candidate band '" + (party.StringId ?? "?") + "' (" + cid
                        + ") destroyed, but not by the player (destroyer="
                        + (destroyer == null ? "<null>"
                            : destroyer.MobileParty != null ? destroyer.MobileParty.StringId : destroyer.ToString())
                        + ") — no credit");
                    return;
                }

                if (cid == _huntTargetCulture) // must be a RIVAL
                {
                    try
                    {
                        InformationManager.DisplayMessage(new InformationMessage(
                            new TextObject("{=bp_hco_020}⚑ These rode under the {CULTURE} banner — the pledge calls for RIVAL blood.")
                                .SetTextVariable("CULTURE", CultureDisplay(cid)).ToString(), Colors.Yellow));
                    }
                    catch { }
                    Log("hunt: rejected — killed band IS the pledged culture " + cid);
                    return;
                }
                // A war band, not a stray handful — count what they fielded.
                int men = party.MemberRoster != null ? party.MemberRoster.TotalManCount : 0;
                if (men > 0 && men < 8)
                {
                    try
                    {
                        InformationManager.DisplayMessage(new InformationMessage(
                            BandItPlus.Localization.Get("bp_originbehavior_047", "⚑ Too small a band to count — the pledge calls for a war band."),
                            Colors.Yellow));
                    }
                    catch { }
                    Log("hunt: rejected — band too small (" + men + " men, " + cid + ")");
                    return;
                }

                var earned = _huntTargetCulture;
                _huntTargetCulture = "";
                GrantPeace(earned);
                try
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        new TextObject("{=bp_hco_021}⚑ The pledge is paid in blood. Peace with the {CULTURE} is yours.")
                            .SetTextVariable("CULTURE", CultureDisplay(earned)).ToString(),
                        Colors.Green));
                }
                catch { }
                // Wave 4.25.0: close the journal quest with a success entry.
                try { BandItPlus.Quests.BanditHuntPledgeQuest.FindActive(earned)?.ConcludeFulfilled(CultureDisplay(cid)); }
                catch (Exception qex) { Log("hunt fulfil quest close error: " + qex.Message); }
                // 2026-07-02 ("didnt marked the hideout"): the peace letter invites the
                // player back to the camp — but accepting the pledge dropped the guide
                // flag, and before this fix even un-spotted the camp. RE-MARK the chief's
                // hideout now (spot + tracker) and SAY where it is, so the invitation can
                // actually be followed; arriving at peace opens the camp menu via the
                // HasCampAccess peace bypass. The tracker clears on arrival as usual.
                try
                {
                    MarkChiefHideout(earned);
                    var seatBack = GetChiefSeat(GetChief(earned));
                    if (seatBack != null)
                        InformationManager.DisplayMessage(new InformationMessage(
                            new TextObject("{=bp_hco_022}⚑ The {CULTURE} keep camp at {SEAT} — it is marked on your map. You are welcome at their fires.")
                                .SetTextVariable("CULTURE", CultureDisplay(earned)).SetTextVariable("SEAT", seatBack.Name).ToString(),
                            Colors.Green));
                }
                catch (Exception mex) { Log("hunt fulfil re-mark error: " + mex.Message); }
                // Wave 4.25.2 ("after the quest complete we need our custom notifications
                // appear"): the prominent left-side crest toast on top of the corner
                // message. Peace earned → the guide flag (if any) is obsolete.
                try
                {
                    BandItPlus.UI.BanditToastScreen.Show(
                        BandItPlus.Localization.Get("bp_originbehavior_048", "Pledge Honored"),
                        new TextObject("{=bp_hco_023}You struck down a {KILLED} war band. The {CULTURE} are at peace with you — their blades no longer seek your blood.")
                            .SetTextVariable("KILLED", CultureDisplay(cid)).SetTextVariable("CULTURE", CultureDisplay(earned)).ToString(),
                        "event:/ui/notification/peace");
                }
                catch (Exception tex) { Log("hunt fulfil toast error: " + tex.Message); }
                // Wave 4.25.10: queue the chief's parchment camp-invitation letter —
                // delivered ~3.5s later by OnTick, once the toast has shown.
                _pendingPeaceLetterCid = earned;
                _peaceLetterDelay = 0f;
                try { UnmarkHideout(); } catch { }
                Log("hunt fulfilled: killed " + cid + " band → peace with " + earned);
            }
            catch (Exception ex) { Log("OnMobilePartyDestroyedHunt error: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Wave 4.25.0: internal — BanditHuntPledgeQuest builds its journal text with it.
        internal static string CultureDisplay(string cid)
        {
            if (string.IsNullOrEmpty(cid)) return BandItPlus.Localization.Get("bp_originbehavior_049", "clan");
            // Localized clan/culture name: bp_clan_<short> exists for all 14 cultures (EN + RU).
            // Falls back to a humanized English name only if the id is somehow missing. Without
            // this, notifications showed the raw munged id ("steppe bandits").
            string shortId = cid.StartsWith("bp_") ? cid.Substring(3) : cid;
            var sb = new System.Text.StringBuilder();
            foreach (var p in shortId.Split('_'))
            {
                if (p.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToUpperInvariant(p[0]));
                if (p.Length > 1) sb.Append(p.Substring(1));
            }
            string fallback = sb.Length > 0 ? sb.ToString() : shortId;
            return BandItPlus.Localization.Get("bp_clan_" + shortId, fallback);
        }

        // ───────────────── Wave 4.27.0: origin starting kits ─────────────────
        // Picking an origin now SHAPES the character — themed skill XP, focus points,
        // attributes, traits, gold, a starting item, and standing with the bandit
        // clans (on top of the existing peace/bounty economics). Applied ONCE per
        // campaign (saved guard _perksApplied), called from BOTH the custom choice
        // screen (ApplyOrigin) and the inquiry fallback (OnOriginChosen). Every grant
        // is individually try/caught + logged so a single bad call never aborts the
        // rest and never crashes the campaign start. Balanced "standard kit" tuning.
        private void GrantOriginPerks()
        {
            try
            {
                if (_perksApplied != 0) { Log("perks already applied (" + _perksApplied + ") — skip"); return; }
                var h = Hero.MainHero;
                if (h == null || h.HeroDeveloper == null) { Log("GrantOriginPerks: MainHero/HeroDeveloper null"); return; }

                switch (Origin)
                {
                    case BanditOrigin.OutlawBlood: // camp-born rogue — Cunning
                        Xp(h, DefaultSkills.Roguery, 8000f); Xp(h, DefaultSkills.Scouting, 4000f);
                        Xp(h, DefaultSkills.Athletics, 2000f); Xp(h, DefaultSkills.Tactics, 2000f);
                        Foc(h, DefaultSkills.Roguery, 2); Foc(h, DefaultSkills.Scouting, 1);
                        Attr(h, DefaultCharacterAttributes.Cunning, 1); Attr(h, DefaultCharacterAttributes.Endurance, 1);
                        Trait(h, DefaultTraits.Calculating, 1);
                        Gold(h, 1200);
                        BanditRelations(+15);
                        GiveItem(new[] { "throwing_knives", "throwing_daggers", "hunting_bow" });
                        break;
                    case BanditOrigin.Drifter: // the balanced nobody
                        Xp(h, DefaultSkills.Scouting, 4500f); Xp(h, DefaultSkills.Trade, 4500f);
                        Xp(h, DefaultSkills.Riding, 3500f); Xp(h, DefaultSkills.OneHanded, 3000f); Xp(h, DefaultSkills.Roguery, 2000f);
                        Foc(h, DefaultSkills.Scouting, 1); Foc(h, DefaultSkills.Trade, 1); Foc(h, DefaultSkills.Riding, 1);
                        Attr(h, DefaultCharacterAttributes.Endurance, 1); Attr(h, DefaultCharacterAttributes.Social, 1);
                        Trait(h, DefaultTraits.Calculating, 1);
                        Gold(h, 1600);
                        GiveItem(new[] { "hunting_bow", "sumpter_horse", "throwing_knives" });
                        break;
                    case BanditOrigin.Lawkeeper: // ex-lawman fighter-leader — Steel/Standing
                        Xp(h, DefaultSkills.OneHanded, 8000f); Xp(h, DefaultSkills.Polearm, 4500f);
                        Xp(h, DefaultSkills.Leadership, 5000f); Xp(h, DefaultSkills.Charm, 3000f); Xp(h, DefaultSkills.Riding, 2500f);
                        Foc(h, DefaultSkills.OneHanded, 2); Foc(h, DefaultSkills.Leadership, 1);
                        Attr(h, DefaultCharacterAttributes.Vigor, 1); Attr(h, DefaultCharacterAttributes.Social, 1);
                        Trait(h, DefaultTraits.Honor, 1); Trait(h, DefaultTraits.Valor, 1);
                        Gold(h, 2200);
                        BanditRelations(-20);
                        GiveItem(new[] { "iron_spatha_sword_t2", "scimitar_t2", "empire_sword_1_t2", "khuzait_sword_1_t2" });
                        break;
                    default: return;
                }

                _perksApplied = (int)Origin;
                Log("origin perks granted for " + Origin);
                try { InformationManager.DisplayMessage(new InformationMessage("⚑ " + KitSummary(), Colors.Green)); } catch { }
            }
            catch (Exception ex) { Log("GrantOriginPerks error: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Per-grant helpers — each isolated so one failure never aborts the kit.
        private static void Xp(Hero h, SkillObject s, float xp)
        { try { if (s != null) h.HeroDeveloper.AddSkillXp(s, xp); } catch (Exception e) { Log("xp grant fail: " + e.Message); } }
        private static void Foc(Hero h, SkillObject s, int f)
        { try { if (s != null) h.HeroDeveloper.AddFocus(s, f, false); } catch (Exception e) { Log("focus grant fail: " + e.Message); } }
        private static void Attr(Hero h, CharacterAttribute a, int v)
        { try { if (a != null) h.HeroDeveloper.AddAttribute(a, v, false); } catch (Exception e) { Log("attr grant fail: " + e.Message); } }
        private static void Trait(Hero h, TraitObject t, int lvl)
        { try { if (t != null && h.GetTraitLevel(t) < lvl) h.SetTraitLevel(t, lvl); } catch (Exception e) { Log("trait grant fail: " + e.Message); } }
        private static void Gold(Hero h, int amount)
        { try { h.ChangeHeroGold(amount); } catch (Exception e) { Log("gold grant fail: " + e.Message); } }

        // Standing with every bandit chief (Outlaw Blood = kin, Lawkeeper = hated).
        private void BanditRelations(int delta)
        {
            try
            {
                foreach (var cid in ParleyCultures)
                {
                    try
                    {
                        var chief = GetChief(cid);
                        if (chief != null && chief.IsAlive)
                            ChangeRelationAction.ApplyPlayerRelation(chief, delta, true, false); // no per-chief notification spam
                    }
                    catch { }
                }
                Log("bandit relations " + (delta >= 0 ? "+" : "") + delta + " applied to chiefs");
            }
            catch (Exception e) { Log("relations grant fail: " + e.Message); }
        }

        // A themed starting weapon — tries candidate ids, takes the first that resolves.
        private static void GiveItem(string[] candidateIds)
        {
            try
            {
                var party = MobileParty.MainParty;
                if (party == null || candidateIds == null) return;
                foreach (var id in candidateIds)
                {
                    var item = MBObjectManager.Instance.GetObject<ItemObject>(id);
                    if (item != null) { party.ItemRoster.AddToCounts(item, 1); Log("origin item granted: " + id); return; }
                }
                Log("origin item: no candidate id resolved (" + string.Join(",", candidateIds) + ")");
            }
            catch (Exception e) { Log("item grant fail: " + e.Message); }
        }

        // One-line summary of what the chosen kit handed the player (green log line).
        private string KitSummary()
        {
            switch (Origin)
            {
                case BanditOrigin.OutlawBlood:
                    return BandItPlus.Localization.Get("bp_originbehavior_050", "Outlaw Blood: trained in Roguery & Scouting, marked Calculating, +1,200 gold, and the clans' goodwill.");
                case BanditOrigin.Lawkeeper:
                    return BandItPlus.Localization.Get("bp_originbehavior_051", "Lawkeeper: a hardened sword-arm and commander — Honour, Valour, +2,200 gold... and the bandits' hatred.");
                default:
                    return BandItPlus.Localization.Get("bp_originbehavior_052", "Drifter: a wanderer's spread — Scouting, Trade, Riding — and +1,600 gold to your name.");
            }
        }

        private static void Log(string msg)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Origin] " + msg); }
            catch { }
        }
    }
}
