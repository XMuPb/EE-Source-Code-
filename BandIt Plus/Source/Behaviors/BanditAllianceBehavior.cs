using System;
using System.Collections.Generic;
using System.Reflection;
using BandItPlus.HideoutVisit;
using BandItPlus.Quests;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;
using TaleWorlds.SaveSystem;

namespace BandItPlus.Behaviors
{
    // BreakReason values per the SHARED CONTRACT. Used by BreakAlliance() callers
    // to discriminate the break popup body + journal-log text.
    public enum BreakReason
    {
        DirectAttack = 1,
        KingdomWar = 2,
        ChiefDeath = 3,
    }

    // Wave 5 (Chief Recruitment) - persistent CampaignBehaviorBase managing
    // alliance state for all 14 BP chiefs.
    //
    // SCOPE OF PHASE 1 (THIS SKELETON):
    //   - Singleton wire-up + RegisterEvents subscriptions to 4 vanilla events
    //     (MapEventEnded, ClanChangedKingdom, WarDeclared, HeroKilledEvent) with
    //     attach-success/failure logging per memory bannerlord-harmony-attach-log.
    //   - SyncData for all 5 dictionaries (so save/load round-trips cleanly
    //     once any state is ever written).
    //   - All 11 public-API methods stubbed with sensible default returns and
    //     no side-effects. Phase 2-5 fill the bodies.
    //
    // SCOPE OF FUTURE PHASES (NOT IN THIS SKELETON):
    //   - Phase 2: dialog-eligibility predicate body (cultureTrust + Tier-3 quest
    //     completion lookup against BanditDialogManager state)
    //   - Phase 3: actual MapEventEnded -> active-quest dispatch + break-condition
    //     detection (R4 predicate)
    //   - Phase 4: MarkAllied side-effects (seed _lastSummonHoursByChief, fire
    //     journal entry, etc.)
    //   - Phase 5: Summon Warband lifecycle (CanSummonWarband + TrySummonWarband
    //     full body with terrain validation + reflection LeaderHero assignment)
    public class BanditAllianceBehavior : CampaignBehaviorBase
    {
        public static BanditAllianceBehavior Instance { get; private set; }

        // ----- State -----
        private Dictionary<string, bool> _alliedByChief = new Dictionary<string, bool>();
        private Dictionary<string, double> _allianceTimeByChief = new Dictionary<string, double>();
        private Dictionary<string, double> _lastSummonHoursByChief = new Dictionary<string, double>();
        private Dictionary<string, BanditAllianceQuest> _activeQuestByChief = new Dictionary<string, BanditAllianceQuest>();
        private Dictionary<string, MobileParty> _activeSummonedWarband = new Dictionary<string, MobileParty>();
        // Wave 5 / Phase 3 / Task 18 — per-chief Ch1 soft-fail cooldown. Keyed by cultureId,
        // value is the absolute CampaignTime.ToHours at which the cooldown expires. Populated
        // by ArmCh1Cooldown from BanditAllianceQuest.FailSoft (30 in-game days = 720 hours
        // per spec Section 2 Ch1 soft-fail row). IsEligibleForOffer short-circuits on
        // IsCh1OnCooldown so a soft-failed chief cannot re-offer for the cooldown duration.
        private Dictionary<string, double> _ch1CooldownEndsByChief = new Dictionary<string, double>();

        // T37 — pending despawn timer (per-culture). When DespawnReturnHome is
        // scheduled (after MapEventEnded or 24h-no-battle), we set _pendingDestroyByCulture[cid]
        // to the absolute hour at which the warband should finally be destroyed (~6h after
        // schedule, to give a "pseudo-return-home" travel window). HourlyAllianceMaintenance
        // drains the map and calls ImmediateDestroy when the due hour passes.
        // NOT [SaveableField] / NOT SyncData'd: per spec, a pending-return-home that
        // crosses a save boundary is safe to drop; OnGameLoaded reconciles by pruning
        // orphaned _activeSummonedWarband entries with null/inactive MobileParty refs.
        private readonly Dictionary<string, double> _pendingDestroyByCulture
            = new Dictionary<string, double>();

        public BanditAllianceBehavior()
        {
            Instance = this;
        }

        // ===== Public API (signatures EXACTLY per SHARED CONTRACT) =====

        public bool IsAllied(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return false;
            if (_alliedByChief == null) return false;
            bool v;
            return _alliedByChief.TryGetValue(cultureId, out v) && v;
        }

        // Wave 5 / Phase 2 / Task 10 — Section 0 predicate. Single source of truth for
        // "can this chief offer an alliance right now?". Combines five gates:
        //   (1) MCM toggle EnableChiefRecruitment
        //   (2) culture trust >= 3 (NOT 2 as Section 0 of the spec says — verified against
        //       BanditDialogManager.OnCompleteTrustQuest + IncreaseCultureTrust short-circuit)
        //   (3) player has spoken to chief at least once AFTER T3 quest completion
        //   (4) no in-flight alliance quest AND not already allied
        //   (5) player Kingdom (if any) NOT at war with chief's culture-region major faction
        //       — best-effort per R4: if mapping unresolvable, default to non-blocking
        public bool IsEligibleForOffer(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return false;

            // (0) Ch1 soft-fail cooldown — set by BanditAllianceQuest.FailSoft on timer
            // expiry (30 in-game days). Returns true immediately while cooldown is active.
            if (IsCh1OnCooldown(cultureId))
                return false;

            // (1) MCM toggle — Section 7 says toggle OFF must block all new offers.
            try
            {
                if (MCMSettings.Instance == null || !MCMSettings.Instance.EnableChiefRecruitment) return false;
            }
            catch (Exception mcmEx)
            {
                // MCMSettings.Instance can transiently throw early in module-load. Log and
                // refuse the offer rather than crashing — per [[csharp-empty-catch-silent-failures]].
                HideoutPeacefulVisitState.Log("BanditAllianceBehavior.IsEligibleForOffer MCM read fail "
                    + mcmEx.GetType().Name + ": " + mcmEx.Message);
                return false;
            }

            // (4a) Already allied — no re-offer.
            if (_alliedByChief != null && _alliedByChief.TryGetValue(cultureId, out bool allied) && allied) return false;

            // (4b) Active quest already in flight — no second offer for the same chief.
            if (_activeQuestByChief != null && _activeQuestByChief.ContainsKey(cultureId)) return false;

            // (2) Trust gate — trust caps at 3 in BP (NOT 2 as Section 0 of spec says; verified
            // against BanditDialogManager.OnCompleteTrustQuest passing newTier up to 3, and
            // IncreaseCultureTrust short-circuit at cur >= 3).
            var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
            if (bdm == null) return false;
            int trust = bdm.GetCultureTrust(cultureId);
            if (trust < 3) return false;

            // (3) Player must have spoken to chief at least once AFTER T3 completion. The
            // tracker is seeded inside OnCompleteTrustQuest (the same conversation that
            // finishes T3 counts), so this is true the moment T3 finishes.
            if (!bdm.HasSpokenAfterT3Completion(cultureId)) return false;

            // (5) Player Kingdom vs chief's mapped major faction — best-effort per R4.
            // If the player has no kingdom, or the mapping can't be resolved, default to TRUE
            // (do not block). Only return false when a positive war state is confirmed.
            try
            {
                Kingdom playerKingdom = Hero.MainHero?.Clan?.Kingdom;
                if (playerKingdom != null)
                {
                    Kingdom mapped = ResolveMappedMajorFaction(cultureId);
                    if (mapped != null && FactionManager.IsAtWarAgainstFaction(playerKingdom, mapped))
                    {
                        return false;
                    }
                }
            }
            catch (Exception warEx)
            {
                HideoutPeacefulVisitState.Log("BanditAllianceBehavior.IsEligibleForOffer war-check fail "
                    + warEx.GetType().Name + ": " + warEx.Message);
                // Best-effort: on any war-check exception, do NOT block the offer.
            }

            return true;
        }

        // Helper for the R4 war-check. Returns the vanilla Kingdom whose Culture matches the
        // chief's culture-region root, or null if no mapping resolves. The 6 vanilla-extended
        // bandit cultures have a hardcoded best-effort table; BP-authored cultures map via
        // CultureProfileRegistry.ByCultureId — if the profile lacks a VanillaCultureRoot
        // field (current state) the lookup returns null and the caller treats this as
        // "no mapping" = non-blocking.
        private Kingdom ResolveMappedMajorFaction(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return null;
            string vanillaCultureId = null;

            // Vanilla-extended bandit cultures: direct mapping.
            switch (cultureId)
            {
                case "sea_raiders":      vanillaCultureId = "sturgia"; break;
                case "forest_bandits":   vanillaCultureId = "battania"; break;
                case "mountain_bandits": vanillaCultureId = "vlandia"; break;
                case "steppe_bandits":   vanillaCultureId = "khuzait"; break;
                case "desert_bandits":   vanillaCultureId = "aserai"; break;
                case "looters":          vanillaCultureId = "empire"; break;
            }

            // BP-authored cultures: try VanillaCultureRoot off the CultureProfile if present.
            // CultureProfileRegistry is a static class with a ByCultureId Dictionary<string,CultureProfile>.
            if (vanillaCultureId == null)
            {
                try
                {
                    if (BandItPlus.Cultures.CultureProfileRegistry.ByCultureId != null
                        && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out var profile)
                        && profile != null)
                    {
                        // VanillaCultureRoot is an OPTIONAL string field — current CultureProfile
                        // does not declare it, so this reflection returns null and we fall through.
                        var fld = profile.GetType().GetField("VanillaCultureRoot",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (fld != null)
                        {
                            vanillaCultureId = fld.GetValue(profile) as string;
                        }
                    }
                }
                catch (Exception profEx)
                {
                    HideoutPeacefulVisitState.Log("ResolveMappedMajorFaction profile read fail "
                        + profEx.GetType().Name + ": " + profEx.Message);
                    return null;
                }
            }

            if (string.IsNullOrEmpty(vanillaCultureId)) return null;

            try
            {
                var cultureObj = MBObjectManager.Instance?.GetObject<CultureObject>(vanillaCultureId);
                if (cultureObj == null) return null;
                foreach (var k in Kingdom.All)
                {
                    if (k != null && !k.IsEliminated && k.Culture == cultureObj) return k;
                }
            }
            catch (Exception kEx)
            {
                HideoutPeacefulVisitState.Log("ResolveMappedMajorFaction kingdom-scan fail "
                    + kEx.GetType().Name + ": " + kEx.Message);
            }
            return null;
        }

        public void MarkAllied(string cultureId, double hours)
        {
            if (string.IsNullOrEmpty(cultureId)) return;
            // Defensive null-init: SyncData restores nulls if save predates the field
            // (handled in SyncData) but a code path that calls MarkAllied before
            // OnGameLoaded fires would still NRE without this guard.
            if (_alliedByChief == null) _alliedByChief = new Dictionary<string, bool>();
            if (_allianceTimeByChief == null) _allianceTimeByChief = new Dictionary<string, double>();
            if (_lastSummonHoursByChief == null) _lastSummonHoursByChief = new Dictionary<string, double>();

            _alliedByChief[cultureId] = true;
            _allianceTimeByChief[cultureId] = hours;
            // Seed the summon cooldown to 0.0 per spec Section 4 Ch3 accept-path + TECH-B1.
            // TrySummonWarband uses TryGetValue so the absent-key path is already safe,
            // but seeding here is the explicit contract from the spec (and lets the first
            // summon check after `now - 0.0 >= 168.0` succeed once a week has passed).
            _lastSummonHoursByChief[cultureId] = 0.0;

            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "BanditAllianceBehavior.MarkAllied: cultureId=" + cultureId + " hours=" + hours.ToString("F2"));
        }

        public void BreakAlliance(string cultureId, BreakReason reason)
        {
            if (string.IsNullOrEmpty(cultureId)) return;

            // T38 — Idempotency guard. BreakAlliance can fire from multiple sources for
            // the same culture in the same tick (e.g. WarDeclared AND ClanChangedKingdom
            // may both route to KingdomWar for the same chief; OnMapEventEnded direct-
            // attack + a parallel chief-on-defender check could double-fire too). Early-
            // out on the second call so the popup only shows once and Trust reset only
            // logs once. Uses the same IsAllied predicate the rest of the behavior reads.
            bool wasAllied = false;
            if (_alliedByChief == null
                || !_alliedByChief.TryGetValue(cultureId, out wasAllied)
                || !wasAllied)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceBehavior.BreakAlliance no-op (not allied): cultureId="
                    + cultureId + " reason=" + reason);
                return;
            }

            // Clear alliance state. Use Remove() rather than set-to-false so IsAllied's
            // TryGetValue path returns false-by-absence (cleaner than false-by-value;
            // also keeps the dict from accumulating dead "false" rows across long campaigns).
            if (_alliedByChief != null) _alliedByChief.Remove(cultureId);
            if (_allianceTimeByChief != null) _allianceTimeByChief.Remove(cultureId);
            if (_lastSummonHoursByChief != null) _lastSummonHoursByChief.Remove(cultureId);

            // T38 — Despawn any active summoned warband immediately on break. Per spec
            // Section 8 R4 "Alliance benefits revoked (vendor, passage, summon)" — the
            // summoned warband is an active benefit so it must vanish with the alliance.
            // For DirectAttack the warband is typically the defender side of the just-
            // ended map event, so OnMapEventEndedForSummon may already have scheduled
            // a return-home; ImmediateDestroy is idempotent (IsActive guard + dict
            // Remove) so a double dispatch is safe.
            try
            {
                if (_activeSummonedWarband != null
                    && _activeSummonedWarband.TryGetValue(cultureId, out var wb))
                {
                    ImmediateDestroy(cultureId, wb);
                }
            }
            catch (Exception despawnEx)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceBehavior.BreakAlliance despawn-warband fail "
                    + despawnEx.GetType().Name + ": " + despawnEx.Message);
            }

            // Force-trust-reset to Tier 0 per spec break-popup body ("Trust drops to
            // Tier 0"). T38 — uses the new public ResetCultureTrust passthrough on
            // BanditDialogManager (replaces the earlier reflection path against the
            // private SetCultureTrust).
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                if (bdm != null)
                {
                    bdm.ResetCultureTrust(cultureId);
                }
                else
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "BanditAllianceBehavior.BreakAlliance: BanditDialogManager null - trust not reset");
                }
            }
            catch (Exception trustEx)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceBehavior.BreakAlliance trust-reset fail "
                    + trustEx.GetType().Name + ": " + trustEx.Message);
            }

            // If a quest is still active (break-during-Ch1/2/3 edge case), log and
            // clear the entry. The quest's own break listener finalizes on the same
            // event tick (war/death events fire to both this behavior AND the quest's
            // listeners). NOTE: removed the !q.IsFinalized guard — that property does
            // not exist on BanditAllianceQuest as of T38, and the log/remove pair is
            // safe regardless.
            try
            {
                if (_activeQuestByChief != null
                    && _activeQuestByChief.TryGetValue(cultureId, out var q)
                    && q != null)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "BanditAllianceBehavior.BreakAlliance: quest still active for "
                        + cultureId + " chapter=" + q.Chapter
                        + " - quest's own break listener will finalize");
                }
                if (_activeQuestByChief != null) _activeQuestByChief.Remove(cultureId);
            }
            catch (Exception qEx)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceBehavior.BreakAlliance quest-clear fail "
                    + qEx.GetType().Name + ": " + qEx.Message);
            }

            // T38 — Structured break popup per spec Section 2 + memory
            // [[bannerlord-premium-inquiry-popup]]: title "The pact is broken",
            // 1-2 line in-character chief reaction from CultureProfile.AllianceBrokenText,
            // structured Consequences block with U+2500 dividers and U+25C6 bullets.
            try
            {
                ShowBreakPopup(cultureId, reason);
            }
            catch (Exception popupEx)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceBehavior.BreakAlliance popup fail "
                    + popupEx.GetType().Name + ": " + popupEx.Message);
            }

            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "BanditAllianceBehavior.BreakAlliance: cultureId=" + cultureId + " reason=" + reason);
        }

        // T38 — Structured break popup. Built from the chief's AllianceBrokenText
        // (per-culture authored prose, 14 chiefs populated in CultureProfileRegistry)
        // + a reason-specific subline + a 3-bullet Consequences block. Uses U+2500 /
        // U+25C6 markers per [[bannerlord-premium-inquiry-popup]]. Non-pausing so it
        // doesn't interrupt post-battle map-event flow — player is already focused
        // on the just-ended battle screen.
        private void ShowBreakPopup(string cultureId, BreakReason reason)
        {
            const string DIV = "────────────────────────────";
            string TITLE = BandItPlus.Localization.Get("bp_alliancebehavior_001", "The pact is broken");

            string chiefLine = null;
            try
            {
                BandItPlus.Cultures.CultureProfile profile;
                if (BandItPlus.Cultures.CultureProfileRegistry.ByCultureId != null
                    && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out profile)
                    && profile != null
                    && !string.IsNullOrEmpty(profile.AllianceBrokenText))
                {
                    chiefLine = profile.AllianceBrokenText;
                }
            }
            catch (Exception profEx)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] ShowBreakPopup profile-read fail "
                    + profEx.GetType().Name + ": " + profEx.Message);
            }
            if (string.IsNullOrEmpty(chiefLine))
            {
                chiefLine = BandItPlus.Localization.Get("bp_alliancebehavior_002", "The chief turns from you in silence.");
            }

            string reasonLine;
            switch (reason)
            {
                case BreakReason.DirectAttack: reasonLine = BandItPlus.Localization.Get("bp_alliancebehavior_003", "You raised arms against the warband."); break;
                case BreakReason.KingdomWar:   reasonLine = BandItPlus.Localization.Get("bp_alliancebehavior_004", "Your banner now flies with their enemies."); break;
                case BreakReason.ChiefDeath:   reasonLine = BandItPlus.Localization.Get("bp_alliancebehavior_005", "The chief has fallen."); break;
                default:                       reasonLine = ""; break;
            }

            string consequences = new TextObject("{=bp_hcp_012}Consequences:\n◆  Trust drops to Tier 0\n◆  Alliance benefits revoked (vendor, passage, summon)\n◆  Re-eligibility: rebuild Trust to Tier 3; Chapter 2's trial will spawn a tier+1 target party").ToString();
            string body =
                chiefLine + "\n" +
                DIV + "\n" +
                reasonLine + "\n" +
                DIV + "\n" +
                consequences;

            try
            {
                InformationManager.ShowInquiry(
                    new InquiryData(
                        titleText: TITLE,
                        text: body,
                        isAffirmativeOptionShown: true,
                        isNegativeOptionShown: false,
                        affirmativeText: BandItPlus.Localization.Get("bp_alliancebehavior_006", "I understand"),
                        negativeText: null,
                        affirmativeAction: null,
                        negativeAction: null,
                        soundEventPath: ""),
                    pauseGameActiveState: false);
            }
            catch (Exception inqEx)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] ShowBreakPopup InformationManager.ShowInquiry threw "
                    + inqEx.GetType().Name + ": " + inqEx.Message);
            }
        }

        // Wave 5 / Phase 4 / Task 12 — Section 5 summon gate (pure predicate; no mutation).
        // Combines four gates: (a) non-empty cultureId, (b) IsAllied, (c) 168h cooldown
        // since last summon using TryGetValue per spec TECH-B1 fix (never bare indexer —
        // KeyNotFoundException on first-summon), (d) chief alive + not prisoner, (e) no
        // already-active summoned warband for this culture. Safe to call as a dialog
        // `condition` delegate — no side effects.
        public bool CanSummonWarband(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return false;
            if (!IsAllied(cultureId)) return false;

            // Cooldown: 168 in-game hours = 7 days. TryGetValue per [[TECH-B1]] so first-summon
            // path doesn't throw KeyNotFoundException even if MarkAllied seed somehow skipped.
            double lastHours = 0.0;
            if (_lastSummonHoursByChief != null)
            {
                _lastSummonHoursByChief.TryGetValue(cultureId, out lastHours);
            }
            double now = CampaignTime.Now.ToHours;
            if (now - lastHours < 168.0) return false;

            // Chief alive + not prisoner. Per memory bannerlord-bandit-party-no-leader-hero:
            // use BanditChiefRegistry.GetChief (vanilla bandit parties have no LeaderHero).
            try
            {
                var chief = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(cultureId);
                if (chief == null) return false;
                if (chief.IsDead || chief.IsPrisoner) return false;
            }
            catch (Exception chiefEx)
            {
                HideoutPeacefulVisitState.Log("BanditAllianceBehavior.CanSummonWarband chief-lookup fail "
                    + chiefEx.GetType().Name + ": " + chiefEx.Message);
                return false;
            }

            // One summoned warband per culture at a time. Existing entry with IsActive=true blocks.
            if (_activeSummonedWarband != null
                && _activeSummonedWarband.TryGetValue(cultureId, out var existing)
                && existing != null
                && existing.IsActive)
            {
                return false;
            }

            return true;
        }

        // Wave 5 / Phase 4 / Task 12 — Section 5 gate + commit. Returns true and consumes the
        // cooldown if all gates pass. Phase 5's SpawnWarband(cultureId) does the actual party
        // spawn — splitting this way lets CanSummonWarband be a pure dialog-condition predicate
        // while TrySummonWarband owns the commit. On hard spawn failure downstream, Phase 5
        // must rollback _lastSummonHoursByChief to avoid burning a 7-day cooldown on a no-op.
        public bool TrySummonWarband(string cultureId, out string denyReason)
        {
            denyReason = null;

            if (string.IsNullOrEmpty(cultureId))
            {
                denyReason = "invalid_culture";
                return false;
            }

            if (!IsAllied(cultureId))
            {
                denyReason = "not_allied";
                return false;
            }

            // Cooldown check — TryGetValue per spec TECH-B1 fix (prevents KeyNotFoundException
            // on first summon if MarkAllied somehow skipped the seed).
            double lastHours = 0.0;
            if (_lastSummonHoursByChief != null)
            {
                _lastSummonHoursByChief.TryGetValue(cultureId, out lastHours);
            }
            double now = CampaignTime.Now.ToHours;
            double remaining = 168.0 - (now - lastHours);
            if (remaining > 0.0)
            {
                denyReason = "cooldown_remaining_hours=" + remaining.ToString("F1");
                return false;
            }

            // Chief alive + not prisoner.
            Hero chief = null;
            try
            {
                chief = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(cultureId);
            }
            catch (Exception chiefEx)
            {
                HideoutPeacefulVisitState.Log("BanditAllianceBehavior.TrySummonWarband chief-lookup fail "
                    + chiefEx.GetType().Name + ": " + chiefEx.Message);
                denyReason = "chief_lookup_failed";
                return false;
            }
            if (chief == null)
            {
                denyReason = "chief_null";
                return false;
            }
            if (chief.IsDead)
            {
                denyReason = "chief_dead";
                return false;
            }
            if (chief.IsPrisoner)
            {
                denyReason = "chief_prisoner";
                return false;
            }

            // Already an active summoned warband for this culture.
            if (_activeSummonedWarband != null
                && _activeSummonedWarband.TryGetValue(cultureId, out var existing)
                && existing != null
                && existing.IsActive)
            {
                denyReason = "warband_already_summoned";
                return false;
            }

            // Commit the cooldown stamp BEFORE spawn so a dialog-side double-click can't
            // double-fire. If the spawn fails downstream we ROLLBACK the stamp at the
            // bottom of this method — preserves the "no burning a 7-day cooldown on a
            // no-op" contract from the T12 docstring.
            if (_lastSummonHoursByChief == null) _lastSummonHoursByChief = new Dictionary<string, double>();
            double previousStamp;
            bool hadPreviousStamp = _lastSummonHoursByChief.TryGetValue(cultureId, out previousStamp);
            _lastSummonHoursByChief[cultureId] = now;

            // ===== T36 — Spawn lifecycle per spec Section 5.5 =====
            // Carry-forwards:
            //   [bannerlord-bandit-party-no-leader-hero] — reflect-set MobileParty._leaderHero
            //       on the spawned bandit party so it reads as "led by chief".
            //   [csharp-empty-catch-silent-failures] — every catch logs Type+Message.
            //   T22/T23 sibling SpawnCh2TargetParty / TryR1Fallback patterns: use the
            //       6-arg CreateBanditParty(id, clan:null, hideout, isBossParty, template, CampaignVec2)
            //       overload, CampaignVec2 (NOT Vec2), template-driven roster (NOT a separate
            //       InitializeMobilePartyAtPosition call), SetMoveEscortParty on MobileParty
            //       (NOT MobilePartyAi) with NavigationType.Default + isTargetingPort:false.

            var main = MobileParty.MainParty;
            if (main == null)
            {
                if (hadPreviousStamp) _lastSummonHoursByChief[cultureId] = previousStamp;
                else _lastSummonHoursByChief.Remove(cultureId);
                denyReason = "no_main_party";
                return false;
            }

            // --- Terrain validation: ~1.0 unit out from MainParty, 8 rotated offsets.
            // GetPosition2D returns Vec2 in campaign-map units (matching sibling pattern in
            // SpawnCh2TargetParty above which uses ~0.5-unit offsets near a hideout — here
            // we use 1.0 because MainParty is a moving entity and the player needs to see
            // the spawn pop into clear adjacent terrain, not on top of MainParty's footprint).
            Vec2 anchor = main.GetPosition2D;
            Vec2[] offsetCandidates = new Vec2[]
            {
                new Vec2( 1.0f,  0.0f),
                new Vec2(-1.0f,  0.0f),
                new Vec2( 0.0f,  1.0f),
                new Vec2( 0.0f, -1.0f),
                new Vec2( 0.7f,  0.7f),
                new Vec2( 0.7f, -0.7f),
                new Vec2(-0.7f,  0.7f),
                new Vec2(-0.7f, -0.7f),
            };
            CampaignVec2 spawnPos = new CampaignVec2(anchor, /* isOnLand: */ true);
            bool spawnPosOk = false;
            try
            {
                var sceneWrapper = Campaign.Current?.MapSceneWrapper;
                if (sceneWrapper != null)
                {
                    for (int i = 0; i < offsetCandidates.Length; i++)
                    {
                        Vec2 candVec = anchor + offsetCandidates[i];
                        CampaignVec2 cand = new CampaignVec2(candVec, /* isOnLand: */ true);
                        try
                        {
                            PathFaceRecord face = sceneWrapper.GetFaceIndex(cand);
                            if (face.IsValid())
                            {
                                spawnPos = cand;
                                spawnPosOk = true;
                                break;
                            }
                        }
                        catch (Exception faceEx)
                        {
                            HideoutPeacefulVisitState.Log(
                                "[BP-Alliance] TrySummonWarband GetFaceIndex(offset "
                                + i + ") threw " + faceEx.GetType().Name + ": " + faceEx.Message);
                        }
                    }
                }
            }
            catch (Exception terrainEx)
            {
                HideoutPeacefulVisitState.Log(
                    "[BP-Alliance] TrySummonWarband terrain-validation outer threw "
                    + terrainEx.GetType().Name + ": " + terrainEx.Message);
            }
            if (!spawnPosOk)
            {
                // Final fallback per spec: spawn at MainParty exact position — engine will
                // resolve overlap. MainParty's own position is guaranteed-valid since it's
                // already on the map. Log so we can see this in the field.
                spawnPos = new CampaignVec2(anchor, /* isOnLand: */ true);
                HideoutPeacefulVisitState.Log(
                    "[BP-Alliance] TrySummonWarband terrain validation failed all 8 rotations; "
                    + "falling back to MainParty pos for " + cultureId);
            }

            // --- Resolve hideout component (required by CreateBanditParty). The chief's
            // HomeSettlement is the canonical anchor; if missing, scan Settlement.All for
            // a hideout matching this cultureId (best-effort fallback).
            Settlement hideoutSettlement = chief.HomeSettlement;
            if (hideoutSettlement == null || hideoutSettlement.Hideout == null)
            {
                try
                {
                    foreach (var s in Settlement.All)
                    {
                        if (s == null) continue;
                        if (!s.IsHideout) continue;
                        if (s.Culture != null && s.Culture.StringId == cultureId
                            && s.Hideout != null)
                        {
                            hideoutSettlement = s;
                            break;
                        }
                    }
                }
                catch (Exception scanEx)
                {
                    HideoutPeacefulVisitState.Log(
                        "[BP-Alliance] TrySummonWarband hideout scan threw "
                        + scanEx.GetType().Name + ": " + scanEx.Message);
                }
            }
            if (hideoutSettlement == null || hideoutSettlement.Hideout == null)
            {
                if (hadPreviousStamp) _lastSummonHoursByChief[cultureId] = previousStamp;
                else _lastSummonHoursByChief.Remove(cultureId);
                denyReason = "no_hideout_anchor";
                return false;
            }

            // --- Pick a culture-matched party template (~20 troops). Boss template gives
            // a heavier roster appropriate for a summoned warband; fall back to default
            // then looters_template per the SpawnCh2TargetParty pattern.
            CultureObject culture = chief.Culture ?? hideoutSettlement.Culture;
            PartyTemplateObject template = null;
            if (culture != null)
            {
                template = culture.BanditBossPartyTemplate ?? culture.DefaultPartyTemplate;
            }
            if (template == null)
            {
                try { template = MBObjectManager.Instance.GetObject<PartyTemplateObject>("looters_template"); }
                catch (Exception tmplEx)
                {
                    HideoutPeacefulVisitState.Log(
                        "[BP-Alliance] TrySummonWarband fallback-template lookup threw "
                        + tmplEx.GetType().Name + ": " + tmplEx.Message);
                }
            }
            if (template == null)
            {
                if (hadPreviousStamp) _lastSummonHoursByChief[cultureId] = previousStamp;
                else _lastSummonHoursByChief.Remove(cultureId);
                denyReason = "no_party_template";
                return false;
            }

            // --- Spawn via the verified 6-arg CreateBanditParty overload. This single call
            // creates AND positions the party AND seeds the troop roster from the template,
            // so no separate InitializeMobilePartyAtPosition pass is needed.
            // 07-03: WarPartyComponent.OnInitialize calls Clan.OnWarPartyAdded — a
            // null clan is a guaranteed NRE (proven by the signature-quest field
            // test). Resolve the real bandit clan like every proven spawner does.
            var warbandClan = BandItPlus.Quests.BanditSignatureQuest.ResolveBanditClan(cultureId);
            if (warbandClan == null)
            {
                if (hadPreviousStamp) _lastSummonHoursByChief[cultureId] = previousStamp;
                else _lastSummonHoursByChief.Remove(cultureId);
                denyReason = "no_bandit_clan";
                HideoutPeacefulVisitState.Log(
                    "[BP-Alliance] TrySummonWarband: no bandit clan for culture " + cultureId);
                return false;
            }
            string partyId = "bp_alliance_warband_" + cultureId
                + "_" + ((int)now).ToString();
            MobileParty warband = null;
            try
            {
                warband = BanditPartyComponent.CreateBanditParty(
                    partyId,
                    warbandClan,
                    hideoutSettlement.Hideout,
                    /* isBossParty: */ true,
                    template,
                    spawnPos);
            }
            catch (Exception spawnEx)
            {
                HideoutPeacefulVisitState.Log(
                    "[BP-Alliance] TrySummonWarband CreateBanditParty threw "
                    + spawnEx.GetType().Name + ": " + spawnEx.Message);
                if (hadPreviousStamp) _lastSummonHoursByChief[cultureId] = previousStamp;
                else _lastSummonHoursByChief.Remove(cultureId);
                denyReason = "spawn_failed";
                return false;
            }
            if (warband == null)
            {
                HideoutPeacefulVisitState.Log(
                    "[BP-Alliance] TrySummonWarband CreateBanditParty returned null");
                if (hadPreviousStamp) _lastSummonHoursByChief[cultureId] = previousStamp;
                else _lastSummonHoursByChief.Remove(cultureId);
                denyReason = "spawn_returned_null";
                return false;
            }

            // --- Custom name so the on-map nameplate reads "{Chief}'s Warband".
            try
            {
                var pname = new TextObject(
                    "{=bp_alliance_warband_name}{CHIEF}'s Warband");
                pname.SetTextVariable("CHIEF", chief.Name);
                warband.Party.SetCustomName(pname);
            }
            catch (Exception nameEx)
            {
                HideoutPeacefulVisitState.Log(
                    "[BP-Alliance] TrySummonWarband SetCustomName threw "
                    + nameEx.GetType().Name + ": " + nameEx.Message);
            }

            // --- Reflect-set _leaderHero so party.LeaderHero == chief. Vanilla bandit
            // parties have no LeaderHero per memory bannerlord-bandit-party-no-leader-hero,
            // and downstream BP behaviors (ChangeRelationAction etc.) need a real Hero.
            try
            {
                var leaderField = typeof(MobileParty).GetField(
                    "_leaderHero",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (leaderField != null)
                {
                    leaderField.SetValue(warband, chief);
                }
                else
                {
                    HideoutPeacefulVisitState.Log(
                        "[BP-Alliance] TrySummonWarband MobileParty._leaderHero field not found "
                        + "via reflection (engine version mismatch?)");
                }
            }
            catch (Exception leaderEx)
            {
                HideoutPeacefulVisitState.Log(
                    "[BP-Alliance] TrySummonWarband _leaderHero reflect-set threw "
                    + leaderEx.GetType().Name + ": " + leaderEx.Message);
            }

            // --- AI: escort MainParty so the warband tags along on the world map.
            // SetMoveEscortParty lives on MobileParty itself (NOT MobilePartyAi) per the
            // verified T23 R1-fallback pattern; signature in 1.4.x War Sails is
            // (MobileParty, NavigationType, bool isTargetingPort).
            try
            {
                if (warband.Ai != null)
                {
                    warband.Ai.SetDoNotMakeNewDecisions(false);
                }
                BandItPlus.Compat.PartyOrderCompat.EscortParty(warband, main);
            }
            catch (Exception escortEx)
            {
                HideoutPeacefulVisitState.Log(
                    "[BP-Alliance] TrySummonWarband SetMoveEscortParty threw "
                    + escortEx.GetType().Name + ": " + escortEx.Message);
                // Non-fatal — the party still exists, just won't track the player.
                // Caller still gets true (party is on the map). T37 despawn will catch
                // a stranded summoned warband when the next summon attempt logs.
            }

            // --- Record state. _activeSummonedWarband[cultureId] = spawnedParty fulfills the
            // T12 docstring contract; T37 will read this dict in DespawnWarband.
            if (_activeSummonedWarband == null)
                _activeSummonedWarband = new Dictionary<string, MobileParty>();
            _activeSummonedWarband[cultureId] = warband;

            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "BanditAllianceBehavior.TrySummonWarband GRANT cultureId="
                + cultureId + " now=" + now.ToString("F2")
                + " partyId=" + warband.StringId
                + " pos=(" + spawnPos.X.ToString("F2") + "," + spawnPos.Y.ToString("F2") + ")"
                + " troops=" + warband.MemberRoster.TotalManCount
                + " template=" + template.StringId);
            return true;
        }

        public void RegisterActiveQuest(string cultureId, BanditAllianceQuest q)
        {
            if (string.IsNullOrEmpty(cultureId) || q == null) return;
            if (_activeQuestByChief == null) _activeQuestByChief = new Dictionary<string, BanditAllianceQuest>();
            _activeQuestByChief[cultureId] = q;
            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "BanditAllianceBehavior.RegisterActiveQuest: cultureId=" + cultureId
                + " chapter=" + q.Chapter);
        }

        public void UnregisterActiveQuest(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return;
            if (_activeQuestByChief == null) return;
            if (_activeQuestByChief.Remove(cultureId))
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceBehavior.UnregisterActiveQuest: cultureId=" + cultureId);
            }
        }

        public BanditAllianceQuest GetActiveQuest(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return null;
            if (_activeQuestByChief == null) return null;
            BanditAllianceQuest q;
            return _activeQuestByChief.TryGetValue(cultureId, out q) ? q : null;
        }

        public double GetAllianceAgeHours(string cultureId)
        {
            // Spec Section 4: -1.0 sentinel for not-allied (cleaner than throwing or 0.0,
            // because 0.0 is a legitimate "just allied this tick" value and callers gating
            // on age >= N would false-positive a not-allied chief at hour 0 as "fresh ally").
            if (string.IsNullOrEmpty(cultureId)) return -1.0;
            if (_allianceTimeByChief == null) return -1.0;
            double startedAt;
            if (!_allianceTimeByChief.TryGetValue(cultureId, out startedAt)) return -1.0;
            double age = CampaignTime.Now.ToHours - startedAt;
            return age >= 0.0 ? age : 0.0;
        }

        // ===== Wave 5 / Phase 3 / Task 18 — Ch1 soft-fail cooldown helpers =====

        // Arms the per-chief Ch1 cooldown. Called from BanditAllianceQuest.FailSoft on
        // Ch1 deadline expiry. cooldownHours = 30 days * 24 = 720h per spec Section 2.
        public void ArmCh1Cooldown(string cultureId, double cooldownHours)
        {
            if (string.IsNullOrEmpty(cultureId)) return;
            if (_ch1CooldownEndsByChief == null)
                _ch1CooldownEndsByChief = new Dictionary<string, double>();
            double endsAt = CampaignTime.Now.ToHours + cooldownHours;
            _ch1CooldownEndsByChief[cultureId] = endsAt;
            HideoutPeacefulVisitState.Log(
                "[BAB] ArmCh1Cooldown culture=" + cultureId
                + " endsAtHours=" + endsAt.ToString("F1")
                + " (" + cooldownHours.ToString("F0") + "h from now)");
        }

        public bool IsCh1OnCooldown(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return false;
            if (_ch1CooldownEndsByChief == null) return false;
            double endsAt;
            if (!_ch1CooldownEndsByChief.TryGetValue(cultureId, out endsAt)) return false;
            return CampaignTime.Now.ToHours < endsAt;
        }

        // ===== CampaignBehaviorBase overrides =====

        public override void RegisterEvents()
        {
            // Per memory bannerlord-harmony-attach-log: every event subscription
            // logs attach success/failure so the in-flight diagnostic trail tells
            // us which hooks took. Per memory bannerlord-campaignevents-naming:
            // use the BARE property names (NOT OnXxxEvent suffix variants); the
            // listener method is named OnXxx.
            //
            // NOTE: HeroKilled is exposed as CampaignEvents.HeroKilledEvent in
            // 1.2.x-1.4.x (confirmed via BP sibling subscriptions in
            // BanditNamedTargetQuest.cs, BanditSlaverQuest.cs, and EE
            // EncyclopediaEditBehavior.cs). Using the bare 'HeroKilled' name
            // would silently no-op per memory bannerlord-harmony-attach-log.
            //
            // Reflection-verify each event property exists before subscribing so a
            // wrong name doesn't silently no-op per memory bannerlord-harmony-attach-log.

            TryAttach("MapEventEnded", () =>
            {
                CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
            });

            // NOTE: ClanChangedKingdom is exposed as CampaignEvents.OnClanChangedKingdomEvent
            // in 1.2.x-1.4.x (confirmed via EE EncyclopediaEditBehavior.cs:230). The bare
            // name 'ClanChangedKingdom' does not compile against the shipped DLL.
            TryAttach("OnClanChangedKingdomEvent", () =>
            {
                CampaignEvents.OnClanChangedKingdomEvent.AddNonSerializedListener(this, OnClanChangedKingdom);
            });

            TryAttach("WarDeclared", () =>
            {
                CampaignEvents.WarDeclared.AddNonSerializedListener(this, OnWarDeclared);
            });

            TryAttach("HeroKilledEvent", () =>
            {
                CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, OnHeroKilled);
            });

            // === Wave 5 / T27 — Ch3 trigger A: stock SettlementEntered ===
            // Covers non-walkable hideout entries (vanilla menu path, stock encounter).
            // Per memory bannerlord-campaignevents-naming: bare property, never OnXxxEvent.
            try
            {
                CampaignEvents.SettlementEntered.AddNonSerializedListener(this, OnSettlementEntered_AllianceCh3);
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BanditAllianceBehavior] subscribed CampaignEvents.SettlementEntered OK");
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BanditAllianceBehavior] FAILED to subscribe CampaignEvents.SettlementEntered: "
                    + ex.GetType().Name + " " + ex.Message);
            }

            // === Wave 5 / T27 — Ch3 trigger B: walkable peaceful activation ===
            // Covers WalkableHideoutEncounter path (does NOT fire SettlementEntered).
            // Raised from BanditDialogManager.OnWalkInCampConsequence after Active=true.
            try
            {
                HideoutPeacefulVisitState.WalkableActivated += OnWalkableActivated_AllianceCh3;
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BanditAllianceBehavior] subscribed HideoutPeacefulVisitState.WalkableActivated OK");
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BanditAllianceBehavior] FAILED to subscribe HideoutPeacefulVisitState.WalkableActivated: "
                    + ex.GetType().Name + " " + ex.Message);
            }

            // === T37 — Summon Warband despawn lifecycle ===
            // Three triggers per spec Section 5.5 (whichever fires first):
            //   1) MapEventEnded participation -> 6h pseudo-return-home then destroy
            //   2) 24h-no-battle (drained on HourlyTickEvent) -> same
            //   3) MainParty enters a settlement -> immediate destroy
            // SettlementEntered is already subscribed above (Ch3 routing); we extend
            // that listener inline rather than double-attaching. MapEventEnded is
            // already subscribed via TryAttach above; we extend OnMapEventEnded inline
            // with a despawn dispatch.
            try
            {
                CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, HourlyAllianceMaintenance);
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BanditAllianceBehavior] subscribed HourlyTickEvent (T37 despawn drain) OK");
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BanditAllianceBehavior] FAILED to subscribe HourlyTickEvent (T37): "
                    + ex.GetType().Name + " " + ex.Message);
            }

            // OnSessionLaunched/OnGameLoaded — prune orphan summoned warbands whose
            // MobileParty was deleted across save/load boundaries. Per memory
            // bannerlord-campaignevents-naming: bare property + reflection-verified
            // via TryAttach so a wrong name is logged not silently no-op'd.
            TryAttach("OnSessionLaunchedEvent", () =>
            {
                CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(
                    this, OnSessionLaunched_PruneOrphanWarbands);
            });
        }

        // ===== Wave 5 / T27 — Hideout-entry detection routing =====

        // Stock SettlementEntered listener. Filters for main-party + hideout, then
        // routes to the shared OnHideoutEntered. Per memory bannerlord-campaignevents-naming
        // the listener is named OnXxx where Xxx matches the bare property.
        private void OnSettlementEntered_AllianceCh3(MobileParty party, Settlement settlement, Hero hero)
        {
            try
            {
                if (party != MobileParty.MainParty) return;

                // T37 — MainParty entering ANY settlement triggers immediate despawn
                // of every active summoned warband per spec Section 5.5 trigger #3.
                // Runs FIRST so it fires for towns/villages/castles too (not just hideouts).
                // SettlementEntered is a campaign event that fires on the main thread,
                // but we still go through the queue for symmetry with the MapEventEnded path.
                if (_activeSummonedWarband != null && _activeSummonedWarband.Count > 0)
                {
                    var keys = new List<string>(_activeSummonedWarband.Keys);
                    foreach (var cultureId in keys)
                    {
                        MobileParty wb;
                        if (!_activeSummonedWarband.TryGetValue(cultureId, out wb)) continue;
                        var capturedCid = cultureId;
                        var capturedWb = wb;
                        SubModuleClassEntry.EnqueueMainThread(
                            () => ImmediateDestroy(capturedCid, capturedWb));
                    }
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP-Alliance] MainParty entered " + settlement?.StringId
                        + " - despawning " + keys.Count + " summoned warband(s)");
                }

                if (settlement == null || !settlement.IsHideout) return;
                OnHideoutEntered(settlement);
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BanditAllianceBehavior] OnSettlementEntered_AllianceCh3 threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Walkable-peaceful activation listener. The walkable path bypasses
        // CampaignEvents.SettlementEntered (LocationEncounter route), so this hook
        // covers it. Same OnHideoutEntered route as the stock path.
        private void OnWalkableActivated_AllianceCh3(Settlement settlement)
        {
            try
            {
                if (settlement == null || !settlement.IsHideout) return;
                OnHideoutEntered(settlement);
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BanditAllianceBehavior] OnWalkableActivated_AllianceCh3 threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Shared route — both detection paths converge here. Looks up active quest
        // by chief cultureId and routes to BanditAllianceQuest.OnHideoutEntered
        // which decides whether to fire the Ch3 ritual popup. Per memory
        // bannerlord-encyclopedia-link-property we read Settlement.Culture.StringId
        // directly off the entity (never hand-built strings).
        private void OnHideoutEntered(Settlement settlement)
        {
            string cultureId = settlement?.Culture?.StringId;
            if (string.IsNullOrEmpty(cultureId)) return;

            if (_activeQuestByChief == null) return;

            BanditAllianceQuest q;
            if (!_activeQuestByChief.TryGetValue(cultureId, out q) || q == null) return;

            // Only Ch3 cares about hideout entry; earlier chapters ignore.
            if (q.Chapter != 3) return;

            try
            {
                q.OnHideoutEntered(settlement);
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BanditAllianceBehavior] q.OnHideoutEntered threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("_bp_alliance_alliedByChief", ref _alliedByChief);
            dataStore.SyncData("_bp_alliance_timeByChief", ref _allianceTimeByChief);
            dataStore.SyncData("_bp_alliance_lastSummonHoursByChief", ref _lastSummonHoursByChief);
            dataStore.SyncData("_bp_alliance_activeQuestByChief", ref _activeQuestByChief);
            dataStore.SyncData("_bp_alliance_activeSummonedWarband", ref _activeSummonedWarband);
            dataStore.SyncData("_bp_alliance_ch1CooldownEndsByChief", ref _ch1CooldownEndsByChief);

            // Defensive: SyncData on load may produce nulls if the save was
            // taken before the field existed (save036 era). Re-instantiate
            // empty so all dictionary call sites are safe.
            if (_alliedByChief == null) _alliedByChief = new Dictionary<string, bool>();
            if (_allianceTimeByChief == null) _allianceTimeByChief = new Dictionary<string, double>();
            if (_lastSummonHoursByChief == null) _lastSummonHoursByChief = new Dictionary<string, double>();
            if (_activeQuestByChief == null) _activeQuestByChief = new Dictionary<string, BanditAllianceQuest>();
            if (_activeSummonedWarband == null) _activeSummonedWarband = new Dictionary<string, MobileParty>();
            if (_ch1CooldownEndsByChief == null) _ch1CooldownEndsByChief = new Dictionary<string, double>();
        }

        // ===== Event handler stubs (Phase 2-5 fill bodies) =====

        private void OnMapEventEnded(MapEvent ev)
        {
            // T24 — dispatch into each active quest's OnBattleResolved when the
            // player participated in the battle. The quest itself owns the
            // random-encounter filter (only its spawned _ch2TargetParty counts),
            // so this dispatcher is intentionally permissive: any player-side
            // MapEventEnded routes to every active quest, the quest decides.
            //
            // Break-condition #1 (DirectAttack on an allied chief) is still
            // deferred to a later phase — only OnBattleResolved dispatch is
            // wired in T24.
            try
            {
                if (ev == null) return;
                if (_activeQuestByChief == null || _activeQuestByChief.Count == 0) return;

                // Only dispatch for battles the player participated in. Non-player
                // battles can't satisfy or fail a player-driven trial.
                if (ev.PlayerSide == BattleSideEnum.None) return;

                BattleSideEnum winner = ev.WinningSide;
                bool playerWon = winner != BattleSideEnum.None && ev.PlayerSide == winner;

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceBehavior.OnMapEventEnded dispatch evType=" + ev.EventType
                    + " playerSide=" + ev.PlayerSide
                    + " winner=" + winner
                    + " playerWon=" + playerWon
                    + " activeQuestCount=" + _activeQuestByChief.Count
                    + " alliedCount=" + CountAllied());

                // Snapshot keys to avoid mutation-during-enumeration if a quest
                // calls UnregisterActiveQuest synchronously via FailHard/Win paths.
                var keys = new List<string>(_activeQuestByChief.Keys);
                foreach (var cid in keys)
                {
                    BanditAllianceQuest q;
                    if (!_activeQuestByChief.TryGetValue(cid, out q)) continue;
                    if (q == null) continue;
                    try
                    {
                        q.OnBattleResolved(ev, playerWon);
                    }
                    catch (Exception qex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "BanditAllianceBehavior.OnMapEventEnded quest dispatch threw culture="
                            + cid + " " + qex.GetType().Name + ": " + qex.Message);
                    }
                }

                // T37 — dispatch despawn for summoned warbands that participated
                // in this battle. Per spec Section 5.5 trigger #1: a summoned warband
                // that fought in a MapEvent (won, lost, OR player-fled — any participation)
                // initiates the 6h pseudo-return-home then destroys itself.
                // CLAUDE.md threading: MapEventEnded MAY fire on a background thread,
                // so all party mutations are deferred via SubModuleClassEntry.EnqueueMainThread.
                OnMapEventEndedForSummon(ev);

                // T38 — Break-condition #1: DirectAttack. If MainParty (or any party
                // in MainParty's Army) was on the attacker side AND the defender side
                // contains the chief CharacterObject of any allied culture, that
                // alliance breaks. Per memory bannerlord-bandit-party-no-leader-hero
                // we DO NOT check LeaderHero — we roster-walk for chief.CharacterObject.
                OnMapEventEndedForBreak(ev);
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceBehavior.OnMapEventEnded threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // T38 — Break-condition #1: direct attack against an allied chief's warband.
        // Predicate per spec Section 8 R4:
        //   attacker-side membership includes MainParty (or MainParty.Army member)
        //   AND defender-side rosters include the chief's CharacterObject.
        // Walks all allied cultures every dispatch (typically ≤14 entries, usually 0-1
        // mid-campaign — cheap). Per memory bannerlord-bandit-party-no-leader-hero,
        // the LeaderHero check is unreliable for bandit parties; we ALWAYS use
        // BanditChiefRegistry.GetChief(cultureId) and roster-walk for the chief's
        // CharacterObject. Defer the BreakAlliance call to main thread per CLAUDE.md
        // threading note — MapEventEnded may fire on a background thread and
        // BreakAlliance touches the UI (popup + party destroy).
        private void OnMapEventEndedForBreak(MapEvent ev)
        {
            if (ev == null) return;
            if (_alliedByChief == null || _alliedByChief.Count == 0) return;

            var alliedCultures = new List<string>();
            foreach (var kv in _alliedByChief) if (kv.Value) alliedCultures.Add(kv.Key);
            if (alliedCultures.Count == 0) return;

            var main = MobileParty.MainParty;
            if (main == null) return;
            var mainArmy = main.Army;

            // Attacker-side membership: MainParty itself OR a member of MainParty's Army.
            bool attackerIsPlayer = false;
            try
            {
                var attackerSide = ev.AttackerSide;
                if (attackerSide != null && attackerSide.Parties != null)
                {
                    for (int i = 0; i < attackerSide.Parties.Count; i++)
                    {
                        var party = attackerSide.Parties[i].Party;
                        if (party == null) continue;
                        var mp = party.MobileParty;
                        if (mp == null) continue;
                        if (mp == main) { attackerIsPlayer = true; break; }
                        if (mainArmy != null && mp.Army == mainArmy) { attackerIsPlayer = true; break; }
                    }
                }
            }
            catch (Exception attEx)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] OnMapEventEndedForBreak attacker-scan threw "
                    + attEx.GetType().Name + ": " + attEx.Message);
                return;
            }
            if (!attackerIsPlayer) return;

            // For each allied culture, roster-walk the defender side for the chief
            // CharacterObject. Per memory bannerlord-bandit-party-no-leader-hero —
            // chief is the named Hero from BanditChiefRegistry, vanilla bandit parties
            // have no LeaderHero so we MUST roster-walk.
            foreach (var cultureId in alliedCultures)
            {
                Hero chief = null;
                try
                {
                    chief = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(cultureId);
                }
                catch (Exception chiefEx)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP-Alliance] OnMapEventEndedForBreak chief lookup threw culture="
                        + cultureId + " " + chiefEx.GetType().Name + ": " + chiefEx.Message);
                    continue;
                }
                if (chief == null) continue;
                var chiefChar = chief.CharacterObject;
                if (chiefChar == null) continue;

                bool chiefOnDefender = false;
                try
                {
                    var defenderSide = ev.DefenderSide;
                    if (defenderSide != null && defenderSide.Parties != null)
                    {
                        for (int i = 0; i < defenderSide.Parties.Count; i++)
                        {
                            var party = defenderSide.Parties[i].Party;
                            var roster = party?.MemberRoster;
                            if (roster == null) continue;
                            int rosterCount = roster.Count;
                            for (int j = 0; j < rosterCount; j++)
                            {
                                if (roster.GetCharacterAtIndex(j) == chiefChar)
                                {
                                    chiefOnDefender = true;
                                    break;
                                }
                            }
                            if (chiefOnDefender) break;
                        }
                    }
                }
                catch (Exception defEx)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP-Alliance] OnMapEventEndedForBreak defender-scan threw culture="
                        + cultureId + " " + defEx.GetType().Name + ": " + defEx.Message);
                    continue;
                }
                if (!chiefOnDefender) continue;

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] BREAK (DirectAttack) detected for " + cultureId);
                var capturedCid = cultureId;
                SubModuleClassEntry.EnqueueMainThread(
                    () => BreakAlliance(capturedCid, BreakReason.DirectAttack));
            }
        }

        // T37 — Summon Warband post-battle despawn dispatcher. Called from
        // OnMapEventEnded (already inside its try/catch). Scans _activeSummonedWarband
        // for parties that participated in the just-ended battle and schedules each
        // for despawn via the main-thread queue. SAFE TO CALL FROM ANY THREAD.
        private void OnMapEventEndedForSummon(MapEvent ev)
        {
            if (ev == null) return;
            if (_activeSummonedWarband == null || _activeSummonedWarband.Count == 0) return;

            var keys = new List<string>(_activeSummonedWarband.Keys);
            foreach (var cultureId in keys)
            {
                MobileParty warband;
                if (!_activeSummonedWarband.TryGetValue(cultureId, out warband) || warband == null) continue;

                bool participated = false;
                try
                {
                    for (int sideIdx = 0; sideIdx < 2; sideIdx++)
                    {
                        var side = ev.GetMapEventSide((BattleSideEnum)sideIdx);
                        if (side == null) continue;
                        if (side.Parties == null) continue;
                        for (int p = 0; p < side.Parties.Count; p++)
                        {
                            var sideParty = side.Parties[p].Party;
                            if (sideParty != null && sideParty.MobileParty == warband)
                            {
                                participated = true;
                                break;
                            }
                        }
                        if (participated) break;
                    }
                }
                catch (Exception ex)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP-Alliance] OnMapEventEndedForSummon scan threw "
                        + ex.GetType().Name + ": " + ex.Message);
                }

                if (!participated) continue;

                // Defer the mutation to the main thread per CLAUDE.md threading note.
                var capturedCultureId = cultureId;
                var capturedWarband = warband;
                SubModuleClassEntry.EnqueueMainThread(
                    () => DespawnReturnHome(capturedCultureId, capturedWarband));
            }
        }

        // T37 — Schedules a summoned warband to head back to its chief's home
        // hideout (~6h pseudo-return-home), then drop into _pendingDestroyByCulture
        // for HourlyAllianceMaintenance to destroy at the due hour. MUST RUN ON
        // MAIN THREAD (mutates MobileParty.Ai). Idempotent — re-scheduling for the
        // same culture overwrites the due hour with the new value.
        private void DespawnReturnHome(string cultureId, MobileParty warband)
        {
            if (string.IsNullOrEmpty(cultureId)) return;
            if (warband == null || !warband.IsActive)
            {
                // Already destroyed or invalid — just prune state.
                if (_activeSummonedWarband != null) _activeSummonedWarband.Remove(cultureId);
                _pendingDestroyByCulture.Remove(cultureId);
                return;
            }
            try
            {
                Hero chief = null;
                try
                {
                    chief = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(cultureId);
                }
                catch (Exception chiefEx)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP-Alliance] DespawnReturnHome chief lookup threw "
                        + chiefEx.GetType().Name + ": " + chiefEx.Message);
                }
                Settlement home = chief?.HomeSettlement;
                if (home != null)
                {
                    try
                    {
                        BandItPlus.Compat.PartyOrderCompat.GoToSettlement(warband, home);
                    }
                    catch (Exception aex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP-Alliance] DespawnReturnHome SetMoveGoToSettlement threw "
                            + aex.GetType().Name + ": " + aex.Message);
                    }
                }
                // Schedule final destroy ~6 in-game hours from now.
                double dueHours = CampaignTime.Now.ToHours + 6.0;
                _pendingDestroyByCulture[cultureId] = dueHours;
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] DespawnReturnHome culture=" + cultureId
                    + " home=" + (home != null ? home.StringId : "<none>")
                    + " destroyAtHour=" + dueHours.ToString("F2"));
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] DespawnReturnHome threw "
                    + ex.GetType().Name + ": " + ex.Message);
                // Fallback: kill the party immediately to avoid orphans.
                ImmediateDestroy(cultureId, warband);
            }
        }

        // T37 — Final destroy step. MUST RUN ON MAIN THREAD (DestroyPartyAction
        // touches party-list state). Safe to call repeatedly (idempotent via IsActive
        // guard + dict Remove).
        private void ImmediateDestroy(string cultureId, MobileParty warband)
        {
            try
            {
                if (warband != null && warband.IsActive)
                {
                    DestroyPartyAction.Apply(null, warband);
                }
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] ImmediateDestroy DestroyPartyAction threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            if (_activeSummonedWarband != null) _activeSummonedWarband.Remove(cultureId);
            _pendingDestroyByCulture.Remove(cultureId);
            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "[BP-Alliance] ImmediateDestroy culture=" + cultureId + " complete");
        }

        // T37 — Hourly drain. Two responsibilities:
        //   (1) 24h-no-battle despawn: any active summoned warband whose
        //       _lastSummonHoursByChief stamp is ≥ 24h old AND not already in
        //       _pendingDestroyByCulture starts its return-home sequence.
        //   (2) Pending-destroy timer drain: any entry in _pendingDestroyByCulture
        //       whose due hour has passed gets ImmediateDestroy'd.
        // Runs on main thread (HourlyTickEvent dispatched by Campaign tick).
        private void HourlyAllianceMaintenance()
        {
            try
            {
                if (_activeSummonedWarband == null) return;
                double nowH = CampaignTime.Now.ToHours;

                // (1) 24h-no-battle despawn.
                var summonedKeys = new List<string>(_activeSummonedWarband.Keys);
                foreach (var cultureId in summonedKeys)
                {
                    double lastSummon;
                    if (_lastSummonHoursByChief == null
                        || !_lastSummonHoursByChief.TryGetValue(cultureId, out lastSummon))
                    {
                        lastSummon = 0.0;
                    }
                    if (nowH - lastSummon < 24.0) continue;
                    // Skip if a return-home is already in flight (don't restart the 6h timer).
                    if (_pendingDestroyByCulture.ContainsKey(cultureId)) continue;
                    MobileParty wb;
                    if (_activeSummonedWarband.TryGetValue(cultureId, out wb))
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP-Alliance] 24h-no-battle despawn for " + cultureId);
                        DespawnReturnHome(cultureId, wb);
                    }
                }

                // (2) Pending-destroy timer drain.
                var pendingKeys = new List<string>(_pendingDestroyByCulture.Keys);
                foreach (var cultureId in pendingKeys)
                {
                    double dueH;
                    if (!_pendingDestroyByCulture.TryGetValue(cultureId, out dueH)) continue;
                    if (nowH < dueH) continue;
                    MobileParty wb = null;
                    if (_activeSummonedWarband != null)
                    {
                        _activeSummonedWarband.TryGetValue(cultureId, out wb);
                    }
                    ImmediateDestroy(cultureId, wb);
                }

                // v220 Patch 3 — Chief-to-hideout enforcement. BP intentionally
                // anchors chief Heroes to a town's clan for encyclopedia visibility
                // (BanditChiefRegistry.cs ~line 388), which causes the engine to
                // spawn the chief as a notable in that town's interior scene
                // (e.g. Skarvac walking around Diathma's castle hall). This loop
                // relocates each chief back to their hideout via the canonical
                // EnterSettlementAction.ApplyForCharacterOnly API every hour,
                // UNLESS:
                //   (a) the chief is in the player's party (Ch2_InTrial scene —
                //       relocating would yank them mid-trial), OR
                //   (b) the chief is dead/prisoner.
                EnforceChiefAtHideouts();
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] HourlyAllianceMaintenance threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // v220 Patch 3 — Glue each registered chief Hero to their hideout so they
        // stop appearing as walking notables in their anchor town. Safe to call
        // hourly; idempotent if chief is already at home. Skips the chief if they
        // are currently in the player's party (Ch2_InTrial) so the trial scene is
        // not disrupted by a mid-action teleport.
        private void EnforceChiefAtHideouts()
        {
            try
            {
                var registry = BandItPlus.Heroes.BanditChiefRegistry.Instance;
                if (registry == null) return;

                // Iterate all registered chief Heroes. GetAllChiefHeroes is the
                // canonical enumerator (T6 + Wave 4 BanditChiefRegistry contract).
                System.Collections.Generic.IEnumerable<Hero> chiefs = null;
                try { chiefs = registry.GetAllChiefHeroes(); }
                catch (Exception gex)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP-Alliance] EnforceChiefAtHideouts GetAllChiefHeroes threw "
                        + gex.GetType().Name + ": " + gex.Message);
                    return;
                }
                if (chiefs == null) return;

                foreach (var chief in chiefs)
                {
                    if (chief == null) continue;
                    if (chief.IsDead || chief.IsPrisoner) continue;
                    // Skip chiefs currently riding with the player (Ch2_InTrial).
                    if (chief.PartyBelongedTo != null
                        && chief.PartyBelongedTo == MobileParty.MainParty)
                    {
                        continue;
                    }
                    // Resolve the chief's hideout. Per BanditChiefRegistry the
                    // chief's HomeSettlement is the town anchor (e.g. Diathma).
                    // We instead want the hideout — look it up by cultureId from
                    // existing chief→culture mapping (BanditChiefRegistry exposes
                    // GetChief(cultureId); we reverse-lookup by iterating cultures).
                    // For now: settle for the first IsHideout settlement matching
                    // chief.Culture; if none found, leave chief alone.
                    Settlement hideout = null;
                    try
                    {
                        if (Settlement.All != null && chief.Culture != null)
                        {
                            foreach (var s in Settlement.All)
                            {
                                if (s != null && s.IsHideout && s.Culture == chief.Culture)
                                {
                                    hideout = s;
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception sex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP-Alliance] EnforceChiefAtHideouts settlement scan threw "
                            + sex.GetType().Name + ": " + sex.Message);
                        continue;
                    }
                    if (hideout == null) continue;
                    if (chief.CurrentSettlement == hideout) continue; // already home — idempotent skip

                    try
                    {
                        EnterSettlementAction.ApplyForCharacterOnly(chief, hideout);
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP-Alliance] EnforceChiefAtHideouts moved "
                            + (chief.Name != null ? chief.Name.ToString() : chief.StringId)
                            + " from " + (chief.CurrentSettlement?.StringId ?? "<none>")
                            + " -> " + hideout.StringId);
                    }
                    catch (Exception aex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP-Alliance] EnforceChiefAtHideouts EnterSettlementAction "
                            + (chief.StringId ?? "<null>") + " -> " + hideout.StringId + " threw "
                            + aex.GetType().Name + ": " + aex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] EnforceChiefAtHideouts threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // T37 — Save-load orphan reconciliation. If a summoned warband's MobileParty
        // was deleted while the save was offline (or was destroyed by other code that
        // didn't notify this behavior), prune the dict entry so subsequent
        // CanSummonWarband doesn't false-block on a dead ref. Also resumes escort AI
        // for valid surviving warbands (saved state may have lost the AI target).
        private void OnSessionLaunched_PruneOrphanWarbands(CampaignGameStarter starter)
        {
            // v220 Patch 1 — Migrate in-flight Ch1 quests from legacy 7-day to 25-day
            // deadline. Idempotent per BanditAllianceQuest.MigrateCh1DeadlineToV220.
            try
            {
                if (_activeQuestByChief != null && _activeQuestByChief.Count > 0)
                {
                    var qKeys = new List<string>(_activeQuestByChief.Keys);
                    int migrated = 0;
                    foreach (var cid in qKeys)
                    {
                        BandItPlus.Quests.BanditAllianceQuest q;
                        if (!_activeQuestByChief.TryGetValue(cid, out q) || q == null) continue;
                        try { q.MigrateCh1DeadlineToV220(); migrated++; }
                        catch (Exception qex)
                        {
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BP-Alliance] migrate-ch1-deadline " + cid + " threw "
                                + qex.GetType().Name + ": " + qex.Message);
                        }
                    }
                    if (migrated > 0)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP-Alliance] OnSessionLaunched: ran 25-day Ch1 deadline migration on "
                            + migrated + " in-flight quest(s)");
                    }
                }
            }
            catch (Exception mex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance][ERR] OnSessionLaunched migration loop "
                    + mex.GetType().Name + ": " + mex.Message);
            }

            try
            {
                if (_activeSummonedWarband == null || _activeSummonedWarband.Count == 0) return;
                var keys = new List<string>(_activeSummonedWarband.Keys);
                int pruned = 0;
                int resumed = 0;
                foreach (var cultureId in keys)
                {
                    MobileParty wb;
                    if (!_activeSummonedWarband.TryGetValue(cultureId, out wb)) continue;
                    bool orphan = (wb == null) || !wb.IsActive;
                    if (!orphan)
                    {
                        try
                        {
                            // MobileParty.All is the canonical world-party list; if our
                            // ref isn't in it, the engine has already removed the party.
                            bool inList = false;
                            foreach (var p in MobileParty.All)
                            {
                                if (p == wb) { inList = true; break; }
                            }
                            if (!inList) orphan = true;
                        }
                        catch (Exception scanEx)
                        {
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BP-Alliance] orphan-scan AllMobileParties threw "
                                + scanEx.GetType().Name + ": " + scanEx.Message);
                        }
                    }
                    if (orphan)
                    {
                        _activeSummonedWarband.Remove(cultureId);
                        _pendingDestroyByCulture.Remove(cultureId);
                        pruned++;
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP-Alliance] OnSessionLaunched: pruned orphan summoned warband for " + cultureId);
                        continue;
                    }
                    // Survivor — resume escort AI; saved state may have lost the AI target.
                    try
                    {
                        BandItPlus.Compat.PartyOrderCompat.EscortParty(wb, MobileParty.MainParty);
                        resumed++;
                    }
                    catch (Exception escortEx)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP-Alliance] OnSessionLaunched escort-resume threw "
                            + escortEx.GetType().Name + ": " + escortEx.Message);
                    }
                }
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] OnSessionLaunched summoned-warband reconcile: pruned="
                    + pruned + " resumed=" + resumed);
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] OnSessionLaunched_PruneOrphanWarbands threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // T38 — Break-condition #2: player joined a kingdom. Re-evaluate every allied
        // culture against the new kingdom's stances (the player's kingdom may already
        // be at war with that chief's culture-region kingdom). Only acts on PlayerClan
        // joining a kingdom — clan-changes for NPC clans are irrelevant. The complementary
        // path "player's kingdom war-declares on chief-aligned kingdom" is handled by
        // OnWarDeclared. Per Phase 1 carry-forward, the engine event name in 1.2.x-1.4.x
        // is OnClanChangedKingdomEvent (not ClanChangedKingdom).
        private void OnClanChangedKingdom(Clan clan, Kingdom oldKingdom, Kingdom newKingdom,
            ChangeKingdomAction.ChangeKingdomActionDetail detail, bool showNotification)
        {
            try
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceBehavior.OnClanChangedKingdom: clan="
                    + (clan != null ? clan.StringId : "<null>")
                    + " oldK=" + (oldKingdom != null ? oldKingdom.StringId : "<null>")
                    + " newK=" + (newKingdom != null ? newKingdom.StringId : "<null>")
                    + " detail=" + detail);

                if (clan == null || clan != Clan.PlayerClan) return;
                if (newKingdom == null) return;
                if (_alliedByChief == null || _alliedByChief.Count == 0) return;

                var alliedCultures = new List<string>();
                foreach (var kv in _alliedByChief) if (kv.Value) alliedCultures.Add(kv.Key);
                foreach (var cultureId in alliedCultures)
                {
                    EvaluateKingdomWarBreakForCulture(cultureId, newKingdom);
                }
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceBehavior.OnClanChangedKingdom threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // T38 — Break-condition #2: war declared. If the player's kingdom is one side
        // and the other faction's culture matches an allied chief's culture-region root,
        // that alliance breaks. WarDeclared has 3-arg signature in 1.2.x-1.4.x:
        // (IFaction, IFaction, DeclareWarAction.DeclareWarDetail).
        private void OnWarDeclared(IFaction f1, IFaction f2, DeclareWarAction.DeclareWarDetail detail)
        {
            try
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceBehavior.OnWarDeclared: f1="
                    + (f1 != null ? f1.StringId : "<null>")
                    + " f2=" + (f2 != null ? f2.StringId : "<null>")
                    + " detail=" + detail);
                EvaluateKingdomWarBreaks(f1, f2);
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceBehavior.OnWarDeclared threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // T38 — Helper: evaluate a single war-declaration event against all allied
        // cultures. If the player's kingdom is on one side and the OTHER side's culture
        // matches an allied chief's culture-region root, fire BreakAlliance(KingdomWar).
        // Defers to main thread per CLAUDE.md threading note.
        private void EvaluateKingdomWarBreaks(IFaction f1, IFaction f2)
        {
            if (f1 == null || f2 == null) return;
            if (_alliedByChief == null || _alliedByChief.Count == 0) return;

            Kingdom playerKingdom = null;
            try { playerKingdom = Hero.MainHero?.Clan?.Kingdom; }
            catch (Exception kEx)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] EvaluateKingdomWarBreaks playerKingdom-read threw "
                    + kEx.GetType().Name + ": " + kEx.Message);
                return;
            }
            if (playerKingdom == null) return;

            // Player must be a participant in this war for it to affect any alliance.
            IFaction other = null;
            if (f1 == playerKingdom) other = f2;
            else if (f2 == playerKingdom) other = f1;
            else return;
            if (other == null) return;

            string otherCultureId = other.Culture?.StringId;
            if (string.IsNullOrEmpty(otherCultureId)) return;

            var alliedCultures = new List<string>();
            foreach (var kv in _alliedByChief) if (kv.Value) alliedCultures.Add(kv.Key);
            foreach (var cultureId in alliedCultures)
            {
                string root = BandItPlus.Cultures.CultureProfileRegistry.GetVanillaCultureRoot(cultureId);
                if (string.IsNullOrEmpty(root)) continue; // exempt (e.g. looters)
                if (root != otherCultureId) continue;

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] BREAK (KingdomWar via WarDeclared) detected for "
                    + cultureId + " vs " + other.StringId);
                var capturedCid = cultureId;
                SubModuleClassEntry.EnqueueMainThread(
                    () => BreakAlliance(capturedCid, BreakReason.KingdomWar));
            }
        }

        // T38 — Helper: re-evaluate one allied culture against the new player kingdom's
        // existing wars. Called from OnClanChangedKingdom when the player JOINS a
        // kingdom (the kingdom may already be at war with the chief's culture-region
        // kingdom — we don't want to wait for the next WarDeclared to detect that).
        // Walks Kingdom.All and tests FactionManager.IsAtWarAgainstFaction to find any
        // active enemy whose culture matches the chief's root.
        private void EvaluateKingdomWarBreakForCulture(string cultureId, Kingdom playerKingdom)
        {
            if (string.IsNullOrEmpty(cultureId) || playerKingdom == null) return;
            string root = BandItPlus.Cultures.CultureProfileRegistry.GetVanillaCultureRoot(cultureId);
            if (string.IsNullOrEmpty(root)) return; // exempt

            try
            {
                foreach (var k in Kingdom.All)
                {
                    if (k == null || k.IsEliminated) continue;
                    if (k == playerKingdom) continue;
                    if (k.Culture == null || k.Culture.StringId != root) continue;
                    if (!FactionManager.IsAtWarAgainstFaction(playerKingdom, k)) continue;

                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP-Alliance] BREAK (KingdomWar via ClanChangedKingdom) detected for "
                        + cultureId + " vs " + k.StringId);
                    var capturedCid = cultureId;
                    SubModuleClassEntry.EnqueueMainThread(
                        () => BreakAlliance(capturedCid, BreakReason.KingdomWar));
                    return;
                }
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] EvaluateKingdomWarBreakForCulture threw culture="
                    + cultureId + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // T38 — Break-condition #3: chief death. If victim is the chief of an allied
        // culture, BreakAlliance(ChiefDeath). Also dispatches into any in-flight
        // alliance quest's OnChiefDied so the quest can FailHard cleanly.
        // HeroKilledEvent signature in 1.2.x-1.4.x: (Hero victim, Hero killer,
        // KillCharacterAction.KillCharacterActionDetail detail, bool showNotification).
        // Per Phase 1 carry-forward the event property name is HeroKilledEvent (not
        // bare HeroKilled).
        private void OnHeroKilled(Hero victim, Hero killer,
            KillCharacterAction.KillCharacterActionDetail detail, bool showNotification)
        {
            try
            {
                if (victim == null) return;

                // (a) Active-quest dispatch — fires for ANY chief death (even non-allied),
                // so an in-flight Ch1/Ch2/Ch3 quest can FailHard if its target chief died
                // mid-trial. Walks _activeQuestByChief (not _alliedByChief) because the
                // pre-alliance Ch1/Ch2 phases run with no IsAllied=true entry yet.
                if (_activeQuestByChief != null && _activeQuestByChief.Count > 0)
                {
                    var questKeys = new List<string>(_activeQuestByChief.Keys);
                    foreach (var cid in questKeys)
                    {
                        BanditAllianceQuest q;
                        if (!_activeQuestByChief.TryGetValue(cid, out q) || q == null) continue;
                        Hero chief = null;
                        try { chief = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(cid); }
                        catch (Exception chiefEx)
                        {
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BP-Alliance] OnHeroKilled quest-dispatch chief-lookup threw culture="
                                + cid + " " + chiefEx.GetType().Name + ": " + chiefEx.Message);
                            continue;
                        }
                        if (chief == null || chief != victim) continue;
                        try { q.OnChiefDied(victim); }
                        catch (Exception qex)
                        {
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BP-Alliance] OnHeroKilled q.OnChiefDied threw culture="
                                + cid + " " + qex.GetType().Name + ": " + qex.Message);
                        }
                    }
                }

                // (b) Break-condition #3 — chief of an ALLIED culture died.
                if (_alliedByChief == null || _alliedByChief.Count == 0) return;
                var allyKeys = new List<string>(_alliedByChief.Keys);
                foreach (var cultureId in allyKeys)
                {
                    bool isAllied;
                    if (!_alliedByChief.TryGetValue(cultureId, out isAllied) || !isAllied) continue;
                    Hero chief = null;
                    try { chief = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(cultureId); }
                    catch (Exception chiefEx)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP-Alliance] OnHeroKilled ally chief-lookup threw culture="
                            + cultureId + " " + chiefEx.GetType().Name + ": " + chiefEx.Message);
                        continue;
                    }
                    if (chief == null || chief != victim) continue;

                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP-Alliance] BREAK (ChiefDeath) detected for " + cultureId);
                    // HeroKilledEvent fires on the main thread, but route via
                    // EnqueueMainThread for symmetry with the other two break paths
                    // (idempotent and cheap).
                    var capturedCid = cultureId;
                    SubModuleClassEntry.EnqueueMainThread(
                        () => BreakAlliance(capturedCid, BreakReason.ChiefDeath));
                }
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceBehavior.OnHeroKilled threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ===== Internal helpers =====

        private int CountAllied()
        {
            int n = 0;
            foreach (var kv in _alliedByChief) if (kv.Value) n++;
            return n;
        }

        // Reflection-verifies the named event property exists on CampaignEvents
        // BEFORE invoking the subscription delegate. Per memory
        // bannerlord-harmony-attach-log: dynamically-resolved subscriptions can
        // no-op silently if a name has drifted across engine versions; the
        // explicit reflection check + log makes the failure visible.
        private void TryAttach(string eventPropertyName, Action subscribe)
        {
            try
            {
                Type t = typeof(CampaignEvents);
                PropertyInfo p = t.GetProperty(eventPropertyName,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (p == null)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "BanditAllianceBehavior.RegisterEvents: ATTACH-FAIL property '"
                        + eventPropertyName + "' not found on CampaignEvents");
                    return;
                }
                subscribe();
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceBehavior.RegisterEvents: ATTACH-OK " + eventPropertyName);
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceBehavior.RegisterEvents: ATTACH-EXCEPTION " + eventPropertyName
                    + " - " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
