using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.Core;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;
using TaleWorlds.SaveSystem;
using BandItPlus.Cultures;

namespace BandItPlus.Quests
{
    // Wave 5 (Chief Recruitment) — three-chapter recruitment quest, one active
    // per allied chief at a time. State is encoded as two ints (_chapter,
    // _objectiveStep) + four timer doubles + several target/destination refs.
    //
    // SAVEABLE: registered at offset 6 in BandItPlusSaveableTypeDefiner per
    // spec Section 7 + memory bannerlord-questbase-patterns.
    //
    // THIS FILE IS THE PHASE 1 SKELETON. Methods labeled "Phase X will fill"
    // are intentionally stubs that log + return; chapter-advancement logic
    // arrives in Phase 2 (Ch1), Phase 3 (Ch2), Phase 4 (Ch3). Public dispatch
    // surface is final and matches the SHARED CONTRACT — Phase 2-5 wire INTO
    // these methods but do not change their signatures.
    public class BanditAllianceQuest : QuestBase
    {
        // ----- State -----
        [SaveableField(1)] private int _chapter;                        // 1, 2, or 3
        // 2026-07-26: CS0414 (assigned but never read) is CORRECT here and must NOT be
        // "fixed" by deleting the field. It is [SaveableField(2)] — part of this quest's
        // on-disk save layout. Removing it would break every existing save that contains
        // an active alliance quest. Written for forward/backward save compatibility;
        // nothing reads it at runtime any more.
#pragma warning disable CS0414
        [SaveableField(2)] private int _objectiveStep;
#pragma warning restore CS0414
        [SaveableField(3)] private string _chiefCultureId;
        [SaveableField(4)] private double _ch1DueHours;                 // Ch1 7-day deadline as CampaignTime.ToHours per memory bannerlord-campaigntime-tohours
        [SaveableField(5)] private double _ch2PreTrialDueHours;
        [SaveableField(6)] private double _ch2InTrialDueHours;
        [SaveableField(7)] private double _ch2ChiefWoundedSinceHours;   // -1 = chief not currently wounded
        [SaveableField(8)] private bool _ch2InTrial;                    // true once chief is in player party + target party spawned
        [SaveableField(9)] private string _ch1TargetSeed;               // resolved target identity (lord StringId / party StringId / etc)
        [SaveableField(10)] private MobileParty _ch1TrackedParty;       // Pattern B (DestroyPartiesOfType) tracked party reference
        [SaveableField(11)] private MobileParty _ch2TargetParty;        // Ch2 trial target party
        [SaveableField(12)] private int _ch1RaidCount;                  // Pattern C (RaidVillagesOfClan) running count
        [SaveableField(13)] private int _ch1RaidCountTarget;            // Pattern C target N
        [SaveableField(14)] private Settlement _ch1EscortDestination;   // Pattern D (EscortToSettlement) destination
        [SaveableField(15)] private MobileParty _ch1EscortCaravan;      // Pattern D caravan being escorted
        // T23 — R1 army-tagalong fallback state (persists across save/load so the
        // teardown path in Win/FailHard/FailSoft finds the right party even if
        // the player saved mid-trial).
        [SaveableField(16)] private bool _r1FallbackActive;
        [SaveableField(17)] private MobileParty _r1ChiefParty;

        public bool IsR1FallbackActive => _r1FallbackActive;

        // ----- Public read-only accessors -----
        public int Chapter => _chapter;
        public string ChiefCultureId => _chiefCultureId;
        public bool IsInTrial => _chapter == 2 && _ch2InTrial;

        // v221 (2026-06-01) — idempotency flag for RegisterEvents. Engine may or
        // may not auto-call our RegisterEvents override on quest creation
        // (verified bug: trap log showed ZERO "MobilePartyDestroyed subscribed"
        // entries despite quest creation succeeding, which meant the Pattern B
        // listener was never wired up and kills didn't advance the quest).
        // We now invoke RegisterEvents explicitly from the public ctor; the flag
        // prevents double-subscription if the engine also invokes it on save-load.
        // Transient — not a [SaveableField]; resets to false per session.
        //
        // 2026-07-26: the legacy `_eventsRegistered` mirror flag was REMOVED. It was
        // written once and read nowhere — its comment claimed it existed "so any other
        // code in this file that still checks _eventsRegistered observes the same
        // state", but no such checker remains. `_eventsSubscribed` below is the only
        // live guard.

        // DoRegisterEvents idempotency flag. This flag guards the entire
        // DoRegisterEvents(caller) helper so it stays a
        // no-op on the second invocation regardless of which entry point
        // (RegisterEvents / InitializeQuestOnGameLoad) calls it. Transient — not
        // a [SaveableField].
        private bool _eventsSubscribed;

        // ----- Ctors -----
        // QuestBase save reconstruction requires a parameterless protected ctor.
        protected BanditAllianceQuest()
            : base(null, null, CampaignTime.Never, 0)
        {
            _ch2ChiefWoundedSinceHours = -1.0;
        }

        // Public ctor invoked from BanditAllianceBehavior.RegisterActiveQuest call
        // site (Phase 2 Task X — dialog accept handler).
        public BanditAllianceQuest(Hero questGiver, string cultureId, CampaignTime ch1Deadline)
            : base("bp_alliance_quest_" + (cultureId ?? "unknown"),
                   questGiver,
                   ch1Deadline,
                   0)
        {
            _chapter = 1;
            _objectiveStep = 0;
            _chiefCultureId = cultureId ?? string.Empty;
            _ch1DueHours = ch1Deadline.ToHours;
            _ch2PreTrialDueHours = 0.0;
            _ch2InTrialDueHours = 0.0;
            _ch2ChiefWoundedSinceHours = -1.0;
            _ch2InTrial = false;
            _ch1TargetSeed = null;
            _ch1RaidCount = 0;
            _ch1RaidCountTarget = 0;

            // (QuestBase calls SetDialogs() itself during base construction in
            // this version; we don't override InitializeQuestOnCreation because
            // it isn't virtual on this QuestBase API — per memory note
            // [[bannerlord-questbase-patterns]] the ctor IS the right place for
            // per-create setup including the Pattern A/B/C/D dispatcher.)
            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "BanditAllianceQuest.ctor: chief=" + (_chiefCultureId ?? "<null>")
                + " ch1DueHours=" + _ch1DueHours.ToString("F2"));

            // ----- T19: Pattern dispatcher -----
            // Pick the Pattern A/B/C/D handler from the chief's CultureProfile
            // and post the Ch1 accept journal breadcrumb. Wrapped in try/catch
            // per [[csharp-empty-catch-silent-failures]] so a malformed profile
            // can't blow up the whole ctor.
            try
            {
                CultureProfile prof = null;
                if (!string.IsNullOrEmpty(_chiefCultureId))
                    CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);

                if (prof == null)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ] ctor NO CultureProfile for " + (_chiefCultureId ?? "<null>")
                        + " -> FailHard");
                    FailHard("no_culture_profile");
                    return;
                }

                // v221j — Use the questGiver Hero the dialog manager already
                // resolved (via its 3-fallback chain). Previously the ctor did its
                // OWN GetChief lookup which returns null on certain save states —
                // so the journal rendered "Earn the trust of forest_bandits" with
                // the raw cultureId. By using questGiver, we get the proper Hero
                // (Granny Hod or MainHero fallback) and render its Name.
                Hero chief = questGiver
                    ?? BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(_chiefCultureId);

                // v221j — second-stage name fallback: when chief still null OR
                // chief is the player (MainHero used as last-resort), use the
                // authored ChiefName from CultureProfile so the journal still
                // says "Granny Hod" instead of "forest_bandits" or "Bortur".
                TextObject chiefDisplayName = null;
                if (chief != null && chief != Hero.MainHero)
                {
                    chiefDisplayName = chief.Name;
                }
                else if (!string.IsNullOrEmpty(prof.ChiefName))
                {
                    chiefDisplayName = new TextObject(prof.ChiefName);
                }
                else if (chief != null)
                {
                    chiefDisplayName = chief.Name;
                }
                else
                {
                    chiefDisplayName = new TextObject(_chiefCultureId);
                }
                Settlement hideout = chief?.HomeSettlement;
                // v221d (2026-06-01): REMOVED misleading "{LOCATION}" binding.
                // {LOCATION} was previously bound to chief.HomeSettlement (the chief's
                // TOWN anchor used for encyclopedia visibility — e.g., "Diathma" for
                // both Skarvac and Granny Hod) which made the journal say "complete
                // the task at Diathma" even when the actual target was a party
                // wandering elsewhere. The pattern-specific logs (world-scan / hinted /
                // upgrade) now carry the target's REAL location. This top-level log
                // stays generic and just frames the chief + deadline.
                // 2026-06-03 quest-prose: route through ResolveProse — flagship
                // chiefs (Granny Hod et al.) get full authored prose via Layer 1;
                // mould chiefs get culture-flavored Layer 2; everyone else gets
                // the legacy short form (Layer 3, identical to pre-2026-06-03 text).
                const string legacyCh1Accept = "Earn the trust of {CHIEF_NAME} within 25 days — see the next journal entries for your specific target and location.";
                TextObject acceptLog = ResolveProse(
                    "AllianceCh1AcceptProse",
                    "bp_alliance_ch1_accept_log",
                    legacyCh1Accept);
                // Retain explicit CHIEF binding for backwards compat with any pre-2026-06-03
                // localization strings still using {CHIEF}; BindChiefVars also binds {CHIEF_NAME}.
                acceptLog.SetTextVariable("CHIEF", chiefDisplayName);
                AddLog(acceptLog);

                // v221c (2026-06-01): REMOVED unconditional AllianceCh1TaskBrief post.
                // The brief is authored flavor (e.g., Granny Hod's "Destroy the rival
                // Twin-Oak forest gang...") that refers to spec'd parties that don't
                // exist at runtime, so it contradicts the actual world-scan target.
                // The pattern-specific logs (world-scan named target, hinted target,
                // or filter-watch upgrade) carry the actionable info now.
                // CultureProfile.AllianceCh1TaskBrief stays populated for future use
                // in chief conversation lines / encyclopedia.

                // Dispatch to the per-pattern initializer. Pattern initializers are
                // parameterless and re-lookup the profile internally (Phase 1 contract).
                switch (prof.AllianceCh1Pattern)
                {
                    case AllianceCh1Pattern.KillNamedLord:
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ] Dispatch Pattern A (KillNamedLord) for " + _chiefCultureId);
                        InitializePatternA();
                        break;

                    case AllianceCh1Pattern.DestroyPartiesOfType:
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ] Dispatch Pattern B (DestroyPartiesOfType) for " + _chiefCultureId);
                        InitializePatternB();
                        break;

                    case AllianceCh1Pattern.RaidVillagesOfClan:
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ] Dispatch Pattern C (RaidVillagesOfClan) for " + _chiefCultureId);
                        InitializePatternC();
                        break;

                    case AllianceCh1Pattern.EscortToSettlement:
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ] Dispatch Pattern D (EscortToSettlement) for " + _chiefCultureId);
                        InitializePatternD();
                        break;

                    default:
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ] Dispatch UNKNOWN pattern " + prof.AllianceCh1Pattern
                            + " for " + _chiefCultureId + " -> FailHard");
                        FailHard("unknown_pattern");
                        return;
                }

                // Register with the behavior so cross-component dispatch (OnHideoutEntered etc.)
                // can find us by cultureId.
                BandItPlus.Behaviors.BanditAllianceBehavior.Instance?.RegisterActiveQuest(_chiefCultureId, this);

                // v221 — MANUALLY invoke RegisterEvents because the engine doesn't
                // auto-call our override on quest creation (verified bug: trap log
                // showed zero "MobilePartyDestroyed subscribed" entries despite
                // successful quest creation). The idempotency flag in RegisterEvents
                // prevents double-subscription if the engine also invokes it later.
                try
                {
                    RegisterEvents();
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ] ctor: explicit RegisterEvents() invoked for " + (_chiefCultureId ?? "<null>"));
                }
                catch (Exception reEx)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ][ERR] ctor explicit RegisterEvents threw "
                        + reEx.GetType().Name + ": " + reEx.Message);
                }
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ] ctor dispatch threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ----- QuestBase overrides -----
        private static readonly TextObject _titleText =
            new TextObject("{=bp_alliance_quest_title}Alliance with the Chief");
        public override TextObject Title => _titleText;

        // v219 (2026-06-01): SHOW the engine countdown chip — players asked for
        // "Expires in N days" on the Alliance with the Chief entry. The base ctor
        // passes ch1Deadline to QuestBase so DueTime is already correct for Ch1.
        // Per-chapter transitions extend the deadline via ChangeQuestDueTime so the
        // chip keeps reflecting the live per-chapter timer (Ch2_PreTrial 7d,
        // Ch2_InTrial 7d, Ch3_Ritual is open-ended).
        public override bool IsRemainingTimeHidden => false;

        // v220 deadline migration (2026-06-01) — bumps in-flight Ch1 quests from
        // the legacy 7-day deadline to the v219b 25-day deadline. Called from
        // BanditAllianceBehavior.OnSessionLaunched. Idempotent: skips if quest
        // is past Ch1 (no migration needed), if already at-or-beyond the target,
        // or if not currently active. Safe to invoke multiple times.
        public void MigrateCh1DeadlineToV220()
        {
            try
            {
                if (!this.IsOngoing) return;
                if (_chapter != 1) return;
                double nowH = CampaignTime.Now.ToHours;
                // Original 7-day quest started at (_ch1DueHours - 7d). New 25-day
                // deadline targets (originalStart + 25d). If the saved _ch1DueHours
                // already reflects >=25 days from start, this no-ops.
                double inferredStartH = _ch1DueHours - (7.0 * 24.0);
                double newDueH = inferredStartH + (25.0 * 24.0);
                if (newDueH <= _ch1DueHours + 0.01)
                {
                    // Already at or beyond the target — no migration needed.
                    return;
                }
                double remainingH = newDueH - nowH;
                if (remainingH <= 0.0)
                {
                    // Migration target is already in the past — quest is overdue
                    // anyway; let the timer expire naturally rather than retroactively
                    // extend into nothing.
                    return;
                }
                ChangeQuestDueTime(CampaignTime.HoursFromNow((float)remainingH));
                _ch1DueHours = newDueH;
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ] MigrateCh1Deadline culture=" + (_chiefCultureId ?? "<null>")
                    + " oldDueH=" + (inferredStartH + 7.0 * 24.0).ToString("F1")
                    + " newDueH=" + newDueH.ToString("F1")
                    + " remainingH=" + remainingH.ToString("F1"));
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ][ERR] MigrateCh1Deadline " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        protected override void InitializeQuestOnGameLoad()
        {
            // Phase 1 skeleton — no rebuild needed; all relevant state is
            // [SaveableField] and reconstructed automatically. Phase 2-4 may
            // add per-chapter post-load fix-ups (e.g. re-attach tracked party).
            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "BanditAllianceQuest.InitializeQuestOnGameLoad: chapter=" + _chapter
                + " chief=" + (_chiefCultureId ?? "<null>"));

            // 2026-06-04 belt-and-suspenders — guarantee event subscription on
            // save-load even if the engine doesn't auto-invoke RegisterEvents on
            // this quest after reconstruction. DoRegisterEvents is idempotent.
            DoRegisterEvents("InitializeQuestOnGameLoad");
        }

        protected override void SetDialogs()
        {
            // Phase 2-4 dialog branches live in BanditDialogManager (per spec Section 3
            // table — alliance dialogs are added to the existing chief greeting dialog,
            // NOT on the quest itself). This override exists only to satisfy QuestBase.
        }

        protected override void HourlyTick()
        {
            // Wave 5 / Phase 3 / Tasks 18 + 21 — Ch1 timer + per-pattern target revalidation,
            // plus Ch2_PreTrial 7-day idle timer (T21) and Ch2_InTrial dispatch (T25 stub).
            try
            {
                // (0) MCM kill-switch — if alliance feature was turned off mid-quest, pause
                //     without failing. Player can re-enable to resume. Applies to ALL chapters.
                if (MCMSettings.Instance == null || !MCMSettings.Instance.EnableChiefRecruitment)
                    return;

                double nowHours = CampaignTime.Now.ToHours;

                // ----------------------------------------------------------------
                // Chapter 1 — per-pattern revalidation + 7-day deadline
                // ----------------------------------------------------------------
                if (_chapter == 1)
                {
                    CultureProfile prof = null;
                    if (!string.IsNullOrEmpty(_chiefCultureId))
                        CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);
                    if (prof == null) return;

                    // (1) Per-pattern target revalidation. Patterns A and B can drift (target
                    //     hero dies of unrelated causes, tracked party disbands, etc.); D's
                    //     destination can become unreachable. C revalidates clan-viability.
                    switch (prof.AllianceCh1Pattern)
                    {
                        case AllianceCh1Pattern.KillNamedLord:
                            TickRevalidatePatternATarget();
                            break;
                        case AllianceCh1Pattern.DestroyPartiesOfType:
                            TickRevalidatePatternBTarget();
                            break;
                        case AllianceCh1Pattern.RaidVillagesOfClan:
                            TickRevalidatePatternCTarget();
                            break;
                        case AllianceCh1Pattern.EscortToSettlement:
                            TickRevalidatePatternDTarget();
                            break;
                    }

                    // (2) Ch1 deadline check — soft-fail on expiry. _ch1DueHours == 0.0 means
                    //     the ctor didn't seed a deadline (defensive — should never happen in
                    //     production but guard against an old save reload).
                    if (_ch1DueHours > 0.0 && nowHours >= _ch1DueHours)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ] HourlyTick Ch1 deadline expired culture=" + _chiefCultureId
                            + " dueHours=" + _ch1DueHours.ToString("F1")
                            + " nowHours=" + nowHours.ToString("F1")
                            + " -> FailSoft");
                        FailSoft("ch1_deadline_expired");
                    }
                    return;
                }

                // ----------------------------------------------------------------
                // Chapter 2 — Pre-Trial idle timer (T21) + In-Trial dispatch (T25 stub)
                // ----------------------------------------------------------------
                if (_chapter == 2 && !_ch2InTrial)
                {
                    // Ch2_PreTrial: idle timer — awaiting player dialog to start the trial.
                    // _ch2PreTrialDueHours seeded in AdvanceToCh2PreTrial() (now + 7d).
                    if (_ch2PreTrialDueHours > 0.0 && nowHours >= _ch2PreTrialDueHours)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ] HourlyTick Ch2_PreTrial idle expired culture=" + _chiefCultureId
                            + " dueHours=" + _ch2PreTrialDueHours.ToString("F1")
                            + " nowHours=" + nowHours.ToString("F1")
                            + " -> FailSoft");
                        FailSoft("ch2_pretrial_idle_expired");
                    }
                    return;
                }

                if (_chapter == 2 && _ch2InTrial)
                {
                    // Ch2_InTrial invariants enforcement lives in T25; stub keeps HourlyTick
                    // building green between T21 and T25.
                    Ch2InTrialHourly(nowHours);
                    return;
                }

                // Ch3 timer enforcement (if any) lands in Phase 4; falls through harmlessly.
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ] HourlyTick threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // T25 — Ch2_InTrial invariants enforcement (spec Section 4.5):
        //   1. Chief dead -> FailHard
        //   2. Chief prisoner -> FailHard
        //   3. Chief removed from MainParty roster pre-resolution (in-party path
        //      only; R1 fallback is exempt) -> FailHard
        //   4. Chief wounded -> pause; on recovery, resume (or Win if target died
        //      during pause); on >14d wounded -> FailSoft
        //   5. Target party vanished without player engagement -> respawn ONCE,
        //      then FailSoft on the second vanish
        //   6. Idle timer (_ch2InTrialDueHours) expired (only while not wounded)
        //      -> FailHard with chief relocation + target destruction
        private void Ch2InTrialHourly(double nowHours)
        {
            try
            {
                Hero chief = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(_chiefCultureId);
                if (chief == null)
                {
                    // Chief object vanished — treat as Failed_Hard
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP_ALLIANCE][ERR] Ch2InTrialHourly chief=null culture=" + _chiefCultureId
                        + " -> FailHard");
                    FailHard("ch2_chief_lookup_failed_hourly");
                    return;
                }

                // ---------- Invariant 1: Chief death (any cause, any time) ----------
                if (chief.IsDead)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP_ALLIANCE] Ch2InTrialHourly chief dead detected -> FailHard");
                    try { MobileParty.MainParty.MemberRoster.RemoveTroop(chief.CharacterObject, 1); }
                    catch (Exception rex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE] Ch2InTrialHourly dead-path RemoveTroop "
                            + rex.GetType().Name + ": " + rex.Message);
                    }
                    R1TeardownIfActive();
                    FailHard("ch2_chief_dead_detected_hourly");
                    return;
                }

                // ---------- Invariant 2: Chief became prisoner outside the trial battle ----------
                if (chief.IsPrisoner)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP_ALLIANCE] Ch2InTrialHourly chief prisoner detected -> FailHard");
                    try { MobileParty.MainParty.MemberRoster.RemoveTroop(chief.CharacterObject, 1); }
                    catch (Exception rex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE] Ch2InTrialHourly prisoner-path RemoveTroop "
                            + rex.GetType().Name + ": " + rex.Message);
                    }
                    R1TeardownIfActive();
                    FailHard("ch2_chief_prisoner_detected_hourly");
                    return;
                }

                // ---------- Invariant 3: In-party removal detection (Section 4.5 row) ----------
                // Only enforced when NOT in R1 fallback (R1 keeps chief in his own party, not MainParty).
                if (!_r1FallbackActive)
                {
                    bool chiefStillInRoster = false;
                    try
                    {
                        chiefStillInRoster = MobileParty.MainParty.MemberRoster
                            .GetTroopRoster()
                            .Any(e => e.Character == chief.CharacterObject);
                    }
                    catch (Exception rex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE][ERR] Ch2InTrialHourly roster scan "
                            + rex.GetType().Name + ": " + rex.Message);
                        // On scan failure, treat as still-in-roster (defensive — avoid false-positive fail)
                        chiefStillInRoster = true;
                    }
                    if (!chiefStillInRoster)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE] Ch2InTrialHourly chief removed from roster pre-resolution -> FailHard");
                        FailHard("ch2_chief_removed_from_roster");
                        return;
                    }
                }

                // ---------- Invariant 4: Wounded-recovery resume / wounded-timeout failsoft ----------
                if (_ch2ChiefWoundedSinceHours >= 0.0)
                {
                    if (!chief.IsWounded)
                    {
                        // Chief recovered; resume.
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE] Ch2InTrialHourly chief recovered -> resuming trial");
                        _ch2ChiefWoundedSinceHours = -1.0;

                        // If the target party was destroyed during the wounded period (by chief's faction
                        // or external), count as Win — chief participated in the original engagement
                        // (spec Section 4.5 row "target died during wounded pause").
                        if (_ch2TargetParty == null || !_ch2TargetParty.IsActive)
                        {
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BP_ALLIANCE] Ch2InTrialHourly target gone during wounded period -> counting as Win");
                            Ch2InTrialHourlyWin(chief);
                            return;
                        }
                        // Otherwise: trial resumes, fall through to remaining checks.
                    }
                    else
                    {
                        // Still wounded — check 14-day soft-fail timeout (14d = 14.0 * 24.0 hours)
                        if (nowHours - _ch2ChiefWoundedSinceHours >= (24.0 * 14.0))
                        {
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BP_ALLIANCE] Ch2InTrialHourly chief wounded > 14d -> FailSoft");
                            try { MobileParty.MainParty.MemberRoster.RemoveTroop(chief.CharacterObject, 1); }
                            catch (Exception rex)
                            {
                                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                    "[BP_ALLIANCE] Ch2InTrialHourly wounded-timeout RemoveTroop "
                                    + rex.GetType().Name + ": " + rex.Message);
                            }
                            R1TeardownIfActive();
                            FailSoft("ch2_chief_wounded_timeout");
                            return;
                        }
                        // Wounded but within window — pause; do NOT enforce idle timer while wounded.
                        return;
                    }
                }

                // ---------- Invariant 5: Target party vanished outside our influence ----------
                // (e.g., another faction destroyed it before player engaged)
                if (_ch2TargetParty == null || !_ch2TargetParty.IsActive)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP_ALLIANCE] Ch2InTrialHourly target vanished without player engagement -> re-spawn check");
                    // Re-spawn ONCE so player gets a chance; if it vanishes a second time, FailSoft.
                    // Reuse the unused-in-Ch2 _ch1RaidCount field as the respawn counter.
                    if (_ch1RaidCount >= 1)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE] Ch2InTrialHourly target vanished twice -> FailSoft");
                        try { MobileParty.MainParty.MemberRoster.RemoveTroop(chief.CharacterObject, 1); }
                        catch (Exception rex)
                        {
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BP_ALLIANCE] Ch2InTrialHourly target-vanished-twice RemoveTroop "
                                + rex.GetType().Name + ": " + rex.Message);
                        }
                        R1TeardownIfActive();
                        FailSoft("ch2_target_vanished_twice");
                        return;
                    }
                    _ch1RaidCount++;
                    Settlement hideout = chief.HomeSettlement;
                    if (hideout != null)
                    {
                        try
                        {
                            _ch2TargetParty = SpawnCh2TargetParty(chief, hideout);
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BP_ALLIANCE] Ch2InTrialHourly target respawn attempt complete, party="
                                + (_ch2TargetParty != null ? _ch2TargetParty.StringId : "<null>"));
                        }
                        catch (Exception sex)
                        {
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BP_ALLIANCE][ERR] Ch2InTrialHourly target respawn threw "
                                + sex.GetType().Name + ": " + sex.Message);
                        }
                    }
                    else
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE][ERR] Ch2InTrialHourly cannot respawn — chief.HomeSettlement=null");
                    }
                    return;
                }

                // ---------- Invariant 6: Ch2_InTrial idle timer (7d, only while not wounded) ----------
                if (_ch2InTrialDueHours > 0.0 && nowHours >= _ch2InTrialDueHours)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP_ALLIANCE] Ch2InTrialHourly idle timer expired culture=" + _chiefCultureId
                        + " dueHours=" + _ch2InTrialDueHours.ToString("F1")
                        + " nowHours=" + nowHours.ToString("F1")
                        + " -> FailHard");
                    try { MobileParty.MainParty.MemberRoster.RemoveTroop(chief.CharacterObject, 1); }
                    catch (Exception rex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE] Ch2InTrialHourly idle-timeout RemoveTroop "
                            + rex.GetType().Name + ": " + rex.Message);
                    }

                    // Force chief relocation to hideout so player isn't permanently stuck with the corpse
                    Settlement chiefHome = chief.HomeSettlement;
                    if (chiefHome != null && chief.CurrentSettlement != chiefHome)
                    {
                        try { EnterSettlementAction.ApplyForCharacterOnly(chief, chiefHome); }
                        catch (Exception eex)
                        {
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BP_ALLIANCE][ERR] Ch2InTrialHourly idle-timeout relocate "
                                + eex.GetType().Name + ": " + eex.Message);
                        }
                    }

                    // Destroy the target party (it's no longer relevant)
                    if (_ch2TargetParty != null && _ch2TargetParty.IsActive)
                    {
                        try { DestroyPartyAction.Apply(null, _ch2TargetParty); }
                        catch (Exception dex)
                        {
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BP_ALLIANCE] Ch2InTrialHourly idle-timeout DestroyParty "
                                + dex.GetType().Name + ": " + dex.Message);
                        }
                    }

                    R1TeardownIfActive();
                    FailHard("ch2_intrial_idle_expired");
                    return;
                }
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP_ALLIANCE][ERR] Ch2InTrialHourly " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Win helper invoked from the wounded-recovery branch when target died during pause.
        // Mirrors the post-battle Win path in OnBattleResolved (roster removal, R1 teardown,
        // chief relocation, state clear, AdvanceToCh3Ritual).
        private void Ch2InTrialHourlyWin(Hero chief)
        {
            try
            {
                if (!_r1FallbackActive)
                {
                    try
                    {
                        MobileParty.MainParty.MemberRoster.RemoveTroop(chief.CharacterObject, 1);
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE] Ch2InTrialHourlyWin chief removed from MainParty roster");
                    }
                    catch (Exception rex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE][ERR] Ch2InTrialHourlyWin RemoveTroop "
                            + rex.GetType().Name + ": " + rex.Message);
                    }
                }
                else
                {
                    R1TeardownIfActive();
                }

                Settlement chiefHome = chief.HomeSettlement;
                if (chiefHome == null)
                {
                    foreach (Settlement s in Settlement.All)
                    {
                        if (s.IsHideout && s.Culture == chief.Culture) { chiefHome = s; break; }
                    }
                }
                if (chiefHome != null)
                {
                    try
                    {
                        EnterSettlementAction.ApplyForCharacterOnly(chief, chiefHome);
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE] Ch2InTrialHourlyWin EnterSettlementAction.ApplyForCharacterOnly(chief="
                            + chief.StringId + ", hideout=" + chiefHome.StringId + ")");
                    }
                    catch (Exception eex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE][ERR] Ch2InTrialHourlyWin relocate "
                            + eex.GetType().Name + ": " + eex.Message);
                    }
                }
                else
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP_ALLIANCE][ERR] Ch2InTrialHourlyWin — no home hideout for chief; skipping relocation");
                }

                _ch2TargetParty = null;
                _ch2InTrial = false;
                _ch2ChiefWoundedSinceHours = -1.0;
                AdvanceToCh3Ritual();
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP_ALLIANCE][ERR] Ch2InTrialHourlyWin " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        protected override void RegisterEvents()
        {
            // 2026-06-04 belt-and-suspenders refactor — the engine's contract for
            // invoking RegisterEvents on quest creation vs save-load is unreliable
            // (verified v221 trap log: zero MobilePartyDestroyed subscribe entries
            // despite successful quest creation). All subscription logic is now
            // funneled into DoRegisterEvents(caller) which is idempotent via
            // _eventsSubscribed and is called from BOTH this override AND
            // InitializeQuestOnGameLoad() so at least one entry point fires.
            DoRegisterEvents("RegisterEvents");
        }

        private void DoRegisterEvents(string caller)
        {
            int step = 0;
            try
            {
                if (_eventsSubscribed)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "BanditAllianceQuest.DoRegisterEvents(" + caller + "): already subscribed, skipping");
                    return;
                }

                // Pattern A (KillNamedLord) — subscribe to HeroKilledEvent and filter
                // inside the listener to the resolved _ch1TargetSeed. Per memory note
                // "bannerlord-harmony-attach-log": event name MUST be HeroKilledEvent
                // (sibling BanditNamedTargetQuest + BanditSlaverQuest confirm).
                step = 1; CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, OnHeroKilledForPatternA);

                // T15 Pattern B — MobilePartyDestroyed listener gated to DestroyPartiesOfType
                // chiefs. Per memory bannerlord-campaignevents-naming: use bare property name.
                // Signature is (MobileParty destroyed, PartyBase destroyer) per CampaignSystem.dll.
                step = 2; CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this,
                    (destroyed, destroyer) =>
                    {
                        CultureProfile prof = null;
                        if (!string.IsNullOrEmpty(_chiefCultureId))
                            CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);
                        if (prof == null) return;
                        if (prof.AllianceCh1Pattern != AllianceCh1Pattern.DestroyPartiesOfType) return;
                        OnMobilePartyDestroyedForPatternB(destroyed, destroyer);
                    });

                // T16 Pattern C — VillageBeingRaided listener gated to RaidVillagesOfClan chiefs.
                // Per memory bannerlord-campaignevents-naming: use bare property name. Verified
                // via MetadataLoadContext dump on 1.4.5 CampaignSystem.dll — signature is
                // IMbEvent<Village> (single-arg). Settlement.LastAttackerParty is a public
                // property (MobileParty) on Settlement. Village.VillageStates includes Looted.
                step = 3; CampaignEvents.VillageBeingRaided.AddNonSerializedListener(this,
                    (village) =>
                    {
                        CultureProfile prof = null;
                        if (!string.IsNullOrEmpty(_chiefCultureId))
                            CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);
                        if (prof == null) return;
                        if (prof.AllianceCh1Pattern != AllianceCh1Pattern.RaidVillagesOfClan) return;

                        // VillageBeingRaided fires multiple times during the raid lifecycle
                        // (start, per-loot-tick, completion). Only count on transition to
                        // Looted so we don't increment N times per village.
                        MobileParty raider = village != null && village.Settlement != null
                            ? village.Settlement.LastAttackerParty
                            : null;
                        bool completed = village != null
                            && village.VillageState == Village.VillageStates.Looted;
                        OnVillageRaidedForPatternC(village, raider, completed);
                    });

                // T17 Pattern D — SettlementEntered listener gated to EscortToSettlement
                // chiefs. Per memory bannerlord-campaignevents-naming: bare property name.
                // Signature is (MobileParty party, Settlement settlement, Hero hero) per
                // sibling listeners in BanditPowerBehavior + ContactArrivalBehavior.
                step = 4; CampaignEvents.SettlementEntered.AddNonSerializedListener(this,
                    (party, settlement, hero) =>
                    {
                        CultureProfile prof = null;
                        if (!string.IsNullOrEmpty(_chiefCultureId))
                            CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);
                        if (prof == null) return;
                        if (prof.AllianceCh1Pattern != AllianceCh1Pattern.EscortToSettlement) return;
                        OnSettlementEnteredForPatternD(party, settlement, hero);
                    });

                step = 99;
                _eventsSubscribed = true;
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceQuest.DoRegisterEvents(" + caller + "): ALL 4 subscribed OK (culture="
                    + (_chiefCultureId ?? "<null>") + " chapter=" + _chapter + ")");
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "BanditAllianceQuest.DoRegisterEvents(" + caller + ") FAIL at step=" + step
                    + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // (No SyncData override — QuestBase does not expose one as virtual on
        // this Bannerlord API surface. [SaveableField]-marked fields auto-sync
        // via the engine's AutoGeneratedSaveManager; sibling BP quests
        // (BanditTrustQuest etc.) confirm the convention. Adding a manual
        // SyncData here would simply not be called.)

        // ===== Public dispatch surface invoked by BanditAllianceBehavior =====
        // These signatures are FINAL per the SHARED CONTRACT. Phase 2-5 will fill
        // the bodies; Phase 1 stubs log + return so any wire-up before logic lands
        // produces a traceable breadcrumb instead of a silent failure.

        // T24 — Ch2_InTrial battle triage. Invoked by BanditAllianceBehavior.OnMapEventEnded
        // for any MapEvent the player participated in. Applies the random-encounter rule
        // (only the spawned _ch2TargetParty counts toward trial progress/failure) then
        // triages by chief Hero state (IsDead/IsPrisoner -> FailHard, IsWounded -> pause,
        // Win -> EnterSettlementAction.ApplyForCharacterOnly + AdvanceToCh3Ritual).
        public void OnBattleResolved(MapEvent ev, bool playerWon)
        {
            try
            {
                if (_chapter != 2 || !_ch2InTrial) return;
                if (ev == null) return;

                Hero chief = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(_chiefCultureId);
                if (chief == null)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP_ALLIANCE][ERR] OnBattleResolved chief=null culture=" + _chiefCultureId);
                    FailHard("ch2_battle_chief_lookup_failed");
                    return;
                }

                // --- Random-encounter rule (spec Section 4 Ch2 step 6 last bullet) ---
                // Only the spawned _ch2TargetParty counts. Other battles do NOT progress NOR fail.
                bool targetInvolved = false;
                if (_ch2TargetParty != null)
                {
                    if (ev.AttackerSide != null)
                    {
                        foreach (MapEventParty p in ev.AttackerSide.Parties)
                        {
                            if (p != null && p.Party != null && p.Party.MobileParty == _ch2TargetParty)
                            { targetInvolved = true; break; }
                        }
                    }
                    if (!targetInvolved && ev.DefenderSide != null)
                    {
                        foreach (MapEventParty p in ev.DefenderSide.Parties)
                        {
                            if (p != null && p.Party != null && p.Party.MobileParty == _ch2TargetParty)
                            { targetInvolved = true; break; }
                        }
                    }
                }

                if (!targetInvolved)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP_ALLIANCE] OnBattleResolved random-encounter (target not involved) — ignoring per spec rule");
                    return;
                }

                // --- Explicit Hero-state predicate per spec Section 4 Ch2 step 6 ---
                bool chiefIsDead     = chief.IsDead;
                bool chiefIsPrisoner = chief.IsPrisoner;
                bool chiefIsWounded  = chief.IsWounded;

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP_ALLIANCE] OnBattleResolved playerWon=" + playerWon
                    + " chief.IsDead=" + chiefIsDead
                    + " chief.IsPrisoner=" + chiefIsPrisoner
                    + " chief.IsWounded=" + chiefIsWounded);

                // ----- Failed_Hard: chief dead or captured -----
                if (chiefIsDead || chiefIsPrisoner)
                {
                    try
                    {
                        MobileParty.MainParty.MemberRoster.RemoveTroop(chief.CharacterObject, 1);
                    }
                    catch (Exception rex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE] OnBattleResolved dead/prisoner-path RemoveTroop "
                            + rex.GetType().Name + ": " + rex.Message
                            + " (chief may already be removed by death event)");
                    }
                    R1TeardownIfActive();
                    FailHard(chiefIsDead ? "ch2_chief_dead_post_battle" : "ch2_chief_prisoner_post_battle");
                    return;
                }

                // ----- PAUSED: chief wounded — set timer, await recovery -----
                if (chiefIsWounded)
                {
                    _ch2ChiefWoundedSinceHours = CampaignTime.Now.ToHours;
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP_ALLIANCE] Ch2 PAUSED chief wounded since=" + _ch2ChiefWoundedSinceHours.ToString("F1"));
                    // Keep chief in roster — do NOT remove. Trial state persists.
                    // If target party already destroyed by this battle, Task 25 hourly will count it as Win on recovery.
                    return;
                }

                // ----- Lose: player lost battle but chief survives, conscious -----
                if (!playerWon)
                {
                    // Target survived; spec says battle-lost = Failed_Hard (Section 4.5 row "Player flees mid-battle")
                    try
                    {
                        MobileParty.MainParty.MemberRoster.RemoveTroop(chief.CharacterObject, 1);
                    }
                    catch (Exception rex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE][ERR] OnBattleResolved lose-path RemoveTroop " + rex.GetType().Name + ": " + rex.Message);
                    }
                    R1TeardownIfActive();
                    FailHard("ch2_battle_lost");
                    return;
                }

                // ----- Win: target destroyed AND chief alive, conscious, free -----
                // (1) Remove chief from MainParty roster (or detach from Army in R1 mode via teardown)
                if (!_r1FallbackActive)
                {
                    try
                    {
                        MobileParty.MainParty.MemberRoster.RemoveTroop(chief.CharacterObject, 1);
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE] OnBattleResolved Win — chief removed from MainParty roster");
                    }
                    catch (Exception rex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE][ERR] OnBattleResolved Win-path RemoveTroop " + rex.GetType().Name + ": " + rex.Message);
                    }
                }
                else
                {
                    R1TeardownIfActive();
                }

                // (2) Explicit chief-home-hideout lookup
                Settlement chiefHomeHideout = chief.HomeSettlement;
                if (chiefHomeHideout == null)
                {
                    // Fallback: find any hideout matching the chief's culture
                    foreach (Settlement s in Settlement.All)
                    {
                        if (s.IsHideout && s.Culture == chief.Culture) { chiefHomeHideout = s; break; }
                    }
                }

                // (3) EXPLICIT EnterSettlementAction.ApplyForCharacterOnly call
                if (chiefHomeHideout != null)
                {
                    try
                    {
                        EnterSettlementAction.ApplyForCharacterOnly(chief, chiefHomeHideout);
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE] OnBattleResolved Win — EnterSettlementAction.ApplyForCharacterOnly(chief="
                            + chief.StringId + ", hideout=" + chiefHomeHideout.StringId + ")");
                    }
                    catch (Exception eex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE][ERR] EnterSettlementAction.ApplyForCharacterOnly " + eex.GetType().Name + ": " + eex.Message);
                    }

                    // Post-condition verify per spec Section 4 Ch2 step 7
                    bool relocOk = chief.CurrentSettlement == chiefHomeHideout;
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP_ALLIANCE] Post-relocation assert chief.CurrentSettlement==hideout: " + relocOk);
                }
                else
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP_ALLIANCE][ERR] OnBattleResolved Win — no home hideout for chief; skipping relocation");
                }

                // (4) Clear target-party reference + flip trial flag off
                _ch2TargetParty = null;
                _ch2InTrial = false;
                _ch2ChiefWoundedSinceHours = -1.0;

                // (5) Advance to Ch3
                AdvanceToCh3Ritual();
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP_ALLIANCE][ERR] OnBattleResolved " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // T28 — Ch3 ritual popup trigger. BanditAllianceBehavior dispatches both
        // the stock SettlementEntered (LocationEncounter) and the walkable peaceful
        // activation routes here; we gate on _chapter==3 AND matching cultureId so
        // visits to unrelated hideouts are no-ops. Decline keeps quest in Ch3 so the
        // player can re-enter to retry; accept marks allied + grants gift + completes.
        public void OnHideoutEntered(Settlement settlement)
        {
            if (settlement == null || !settlement.IsHideout) return;
            if (_chapter != 3) return;

            string enteredCultureId = settlement.Culture != null ? settlement.Culture.StringId : null;
            if (string.IsNullOrEmpty(enteredCultureId) || enteredCultureId != _chiefCultureId)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP_ALLIANCE] OnHideoutEntered Ch3 wrong-hideout culture=" + (enteredCultureId ?? "<null>")
                    + " expected=" + (_chiefCultureId ?? "<null>"));
                return;
            }

            try
            {
                ShowCh3RitualPopup(settlement);
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP_ALLIANCE][ERR] ShowCh3RitualPopup " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Premium-inquiry structured-body recipe per memory note
        // [[bannerlord-premium-inquiry-popup]]: U+2500 horizontal-rule divider +
        // U+25C6 bullet markers; pauseGameActiveState=true for the milestone weight.
        // Body prose comes from CultureProfile.AllianceCh3RitualText (per-chief).
        private void ShowCh3RitualPopup(Settlement hideout)
        {
            Hero chief = BandItPlus.Heroes.BanditChiefRegistry.Instance != null
                ? BandItPlus.Heroes.BanditChiefRegistry.Instance.GetChief(_chiefCultureId)
                : null;
            string chiefName = chief != null && chief.Name != null
                ? chief.Name.ToString()
                : BandItPlus.Localization.Get("bp_alliancequest_001", "the chief");

            CultureProfile prof = null;
            if (!string.IsNullOrEmpty(_chiefCultureId))
                CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);
            string ritualBody = prof != null && !string.IsNullOrEmpty(prof.AllianceCh3RitualText)
                ? prof.AllianceCh3RitualText
                : new TextObject("{=bp_hcp_006}{CHIEF} accepts your oath; the rite is sealed in old custom.")
                    .SetTextVariable("CHIEF", chiefName).ToString();

            string divider = new string('─', 32);
            string bullets = new TextObject("{=bp_hcp_007}◆ Accept and you become {CHIEF}'s sworn ally.\n◆ Free passage through this hideout.\n◆ Free service from his vendors.\n◆ You may summon his warband to your side, once every seven days.")
                .SetTextVariable("CHIEF", chiefName).ToString();
            string body =
                ritualBody
                + "\n\n" + divider
                + "\n\n" + bullets;

            string title = new TextObject("{=bp_hcp_008}{CHIEF}'s Oath")
                .SetTextVariable("CHIEF", chiefName).ToString();
            string acceptText = BandItPlus.Localization.Get("bp_alliancequest_002", "Take the oath");
            string declineText = BandItPlus.Localization.Get("bp_alliancequest_003", "Not yet");

            InformationManager.ShowInquiry(
                new InquiryData(
                    title,
                    body,
                    true,                              // isAffirmativeOptionShown
                    true,                              // isNegativeOptionShown
                    acceptText,
                    declineText,
                    new Action(OnCh3PopupAccept),      // affirmativeAction
                    new Action(OnCh3PopupDecline),     // negativeAction
                    string.Empty,                      // soundEventPath
                    0,                                 // expireTime
                    null),                             // expireAction
                pauseGameActiveState: true);
        }

        private void OnCh3PopupAccept()
        {
            try
            {
                // 1. Mark allied + seed _lastSummonHoursByChief (per spec §4 Ch3 accept).
                double nowHours = CampaignTime.Now.ToHours;
                if (BandItPlus.Behaviors.BanditAllianceBehavior.Instance != null)
                    BandItPlus.Behaviors.BanditAllianceBehavior.Instance.MarkAllied(_chiefCultureId, nowHours);

                // Living Chronicle — first clan sworn to the player.
                try
                {
                    var chiefHero = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(_chiefCultureId);
                    string clanName = chiefHero?.Clan?.Name?.ToString();
                    if (string.IsNullOrEmpty(clanName)) clanName = chiefHero?.Name?.ToString();
                    BandItPlus.Origins.BanditChronicleBehavior.Instance?.Unlock("kin_oath", clanName);
                }
                catch { /* chronicle must never block the alliance grant */ }

                // 2. Gift item from CultureProfile.AllianceCh3GiftItemId (best-effort).
                CultureProfile prof = null;
                if (!string.IsNullOrEmpty(_chiefCultureId))
                    CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);
                string giftId = prof != null ? prof.AllianceCh3GiftItemId : null;
                if (!string.IsNullOrEmpty(giftId))
                {
                    ItemObject gift = MBObjectManager.Instance.GetObject<ItemObject>(giftId);
                    if (gift != null)
                    {
                        try
                        {
                            PartyBase.MainParty.ItemRoster.AddToCounts(gift, 1);
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BP_ALLIANCE] Ch3 gift granted itemId=" + giftId + " culture=" + _chiefCultureId);
                        }
                        catch (Exception gex)
                        {
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BP_ALLIANCE][ERR] Ch3 gift AddToCounts "
                                + gex.GetType().Name + ": " + gex.Message);
                        }
                    }
                    else
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BP_ALLIANCE][WARN] Ch3 gift ItemObject not found for itemId=" + giftId
                            + " culture=" + _chiefCultureId);
                    }
                }

                // 3. Complete the quest.
                CompleteSuccessfully();
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP_ALLIANCE][ERR] OnCh3PopupAccept "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnCh3PopupDecline()
        {
            // Quest stays in Ch3; re-shows on next hideout entry. Nothing to do.
            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "[BP_ALLIANCE] Ch3 ritual declined by player culture=" + _chiefCultureId);
        }

        public void OnChiefDied(Hero deadChief)
        {
            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "BanditAllianceQuest.OnChiefDied STUB: chapter=" + _chapter
                + " chiefCulture=" + _chiefCultureId
                + " deadHero=" + (deadChief != null ? deadChief.StringId : "<null>"));
            // Phase 2-4 (any-chapter Failed_Hard transition) fills this.
        }

        #region Pattern A — KillNamedLord

        // T14 Phase 3 Ch1 — Pattern A: resolve a named major-faction lord (from
        // CultureProfile.AllianceCh1TargetSeedHint if it looks like a hero stringId,
        // otherwise pick a random eligible lord filtered by culture-affinity of
        // the bandit chief's culture). Validate-and-reroll on dead/captured/etc.
        // Returns null after MaxRerollAttempts — caller must FailHard.
        private const int MaxRerollAttempts = 16;

        // Map bp_* bandit cultures → vanilla culture stringId used to pick a
        // "culture-region" lord pool. Mirrors the affinity-by-design table in
        // the spec Section 6.5; kept inline (no extra schema field) because the
        // 8 BP cultures already lock in this affinity by authored brief flavor.
        private static readonly Dictionary<string, string> _bpCultureAffinity = new Dictionary<string, string>
        {
            { "bp_frost_reavers",       "sturgia"  },
            { "bp_marsh_stalkers",      "battania" },
            { "bp_highwaymen",          "vlandia"  },
            { "bp_slaver_caravans",     "aserai"   },
            { "bp_fallen_legionaries",  "empire"   },
            { "bp_sky_raiders",         "battania" },
            { "bp_steppe_wolves",       "khuzait"  },
            { "bp_pagan_cult",          "battania" },
        };

        private string ResolveAffinityCultureId()
        {
            if (string.IsNullOrEmpty(_chiefCultureId)) return null;
            // BP-authored cultures: lookup table.
            if (_bpCultureAffinity.TryGetValue(_chiefCultureId, out var aff))
                return aff;
            // Vanilla bandit cultures (looters/forest_bandits/etc.) — no affinity
            // is authored; fall back to null so the random pool spans all majors.
            return null;
        }

        // Attempt the optional VanillaCultureRoot field via reflection so we don't
        // hard-fail when (a) the field doesn't exist on this CultureProfile build
        // or (b) the profile didn't set it. Mirrors BanditAllianceBehavior usage.
        private static string TryReadVanillaCultureRoot(CultureProfile prof)
        {
            if (prof == null) return null;
            try
            {
                FieldInfo fld = prof.GetType().GetField("VanillaCultureRoot",
                    BindingFlags.Public | BindingFlags.Instance);
                if (fld == null) return null;
                return fld.GetValue(prof) as string;
            }
            catch { return null; }
        }

        private Hero ResolvePatternATarget(CultureProfile prof)
        {
            // 1) Hint path — only attempt MBObjectManager lookup if the hint looks
            //    like a hero stringId (not the "culture_region_minor_noble_random"
            //    sentinel used by authored profiles to mean "pick random").
            string hint = prof != null ? prof.AllianceCh1TargetSeedHint : null;
            if (!string.IsNullOrEmpty(hint) && !hint.Contains("random") && !hint.Contains("_caravan_") && !hint.Contains("_party"))
            {
                Hero hinted = MBObjectManager.Instance.GetObject<Hero>(hint);
                if (IsValidPatternATarget(hinted))
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.A] ResolveTarget hint='" + hint + "' -> valid hero "
                        + hinted.Name.ToString());
                    return hinted;
                }
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.A] ResolveTarget hint='" + hint + "' INVALID, falling back to random pool");
            }

            // 2) Random pool — culture-region filter. Prefer VanillaCultureRoot off
            //    the profile (set by spec when authored), else fall back to the
            //    inline _bpCultureAffinity map. Null affinity = no filter.
            string affinity = TryReadVanillaCultureRoot(prof) ?? ResolveAffinityCultureId();

            List<Hero> pool = new List<Hero>();
            foreach (Hero h in Hero.AllAliveHeroes)
            {
                if (!IsValidPatternATarget(h)) continue;
                if (affinity != null)
                {
                    if (h.Culture == null || h.Culture.StringId != affinity) continue;
                }
                pool.Add(h);
            }
            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "[BAQ.A] ResolveTarget random pool size=" + pool.Count
                + " affinity=" + (affinity ?? "<any>"));
            if (pool.Count == 0) return null;

            // Deterministic per-quest seed so save/load resolves identically until
            // first reroll triggers an InvalidPatternATarget reroll.
            int seed = (_chiefCultureId ?? "x").GetHashCode() ^ (int)CampaignTime.Now.ToHours;
            Random rng = new Random(seed);
            for (int i = 0; i < MaxRerollAttempts; i++)
            {
                Hero pick = pool[rng.Next(pool.Count)];
                if (IsValidPatternATarget(pick)) return pick;
            }
            return null;
        }

        private static bool IsValidPatternATarget(Hero h)
        {
            if (h == null) return false;
            if (h == Hero.MainHero) return false;
            if (!h.IsAlive) return false;
            if (h.IsDead) return false;
            if (h.IsPrisoner) return false;
            if (h.Clan == null) return false;
            if (h.Clan == Clan.PlayerClan) return false;
            // Skip bandit / minor-faction lords — Pattern A targets are MAJOR-faction
            // named lords only. Per memory bannerlord-bandit-party-no-leader-hero,
            // BP-promoted chiefs live on minor-faction clans; we already filter
            // those out here so we never accidentally target our own ally chief.
            if (h.IsMinorFactionHero) return false;
            if (h.Clan.IsBanditFaction) return false;
            return true;
        }

        // Called by the dialog-accept handler (Task 19 dispatcher) right after the
        // BanditAllianceQuest is constructed for a Pattern A chief. Public so the
        // behavior layer can invoke it after RegisterActiveQuest is wired.
        public void InitializePatternA()
        {
            CultureProfile prof = null;
            if (!string.IsNullOrEmpty(_chiefCultureId))
                CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);
            if (prof == null)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.A] InitializePatternA NO PROFILE for culture="
                    + (_chiefCultureId ?? "<null>") + " -> FailHard");
                FailHard("no_culture_profile_for_pattern_a");
                return;
            }

            Hero target = ResolvePatternATarget(prof);
            if (target == null)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.A] InitializePatternA NULL target for culture=" + _chiefCultureId
                    + " -> FailHard");
                FailHard("no_valid_pattern_a_target");
                return;
            }
            _ch1TargetSeed = target.StringId;
            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "[BAQ.A] InitializePatternA target=" + target.Name.ToString()
                + " (" + target.StringId + ") culture=" + _chiefCultureId);
        }

        // HeroKilledEvent listener (subscribed in RegisterEvents). Gates on:
        //   - quest still in Ch1
        //   - culture profile is Pattern A
        //   - _ch1TargetSeed resolved
        //   - victim.StringId matches _ch1TargetSeed
        // On match, advances to Ch2_PreTrial.
        private void OnHeroKilledForPatternA(Hero victim, Hero killer,
            KillCharacterAction.KillCharacterActionDetail detail, bool showNotification)
        {
            try
            {
                if (!this.IsOngoing) return;
                if (_chapter != 1) return;
                if (string.IsNullOrEmpty(_ch1TargetSeed)) return;
                if (victim == null || victim.StringId != _ch1TargetSeed) return;

                // Pattern guard — only fire for Pattern A chiefs (other patterns
                // share the same quest class but resolve via different listeners).
                CultureProfile prof = null;
                if (!string.IsNullOrEmpty(_chiefCultureId))
                    CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);
                if (prof == null || prof.AllianceCh1Pattern != AllianceCh1Pattern.KillNamedLord)
                    return;

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.A] OnHeroKilled MATCH victim=" + victim.Name.ToString()
                    + " killer=" + (killer != null ? killer.Name.ToString() : "<unknown>")
                    + " detail=" + detail + " -> AdvanceToCh2PreTrial");
                AdvanceToCh2PreTrial();
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.A] OnHeroKilled threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Called from HourlyTick (Phase 2 Task 15) — re-validate target; if it
        // became invalid (joined another faction, permanently captured, etc.),
        // attempt a reroll. If reroll yields nothing, leave _ch1TargetSeed in
        // place until Ch1 timer expiry handles it via FailSoft.
        public void TickRevalidatePatternATarget()
        {
            try
            {
                if (_chapter != 1) return;
                if (string.IsNullOrEmpty(_ch1TargetSeed)) return;
                CultureProfile prof = null;
                if (!string.IsNullOrEmpty(_chiefCultureId))
                    CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);
                if (prof == null || prof.AllianceCh1Pattern != AllianceCh1Pattern.KillNamedLord)
                    return;

                Hero current = MBObjectManager.Instance.GetObject<Hero>(_ch1TargetSeed);
                if (IsValidPatternATarget(current)) return; // still good

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.A] TickRevalidate target " + _ch1TargetSeed
                    + " became invalid -> attempt reroll");
                Hero replacement = ResolvePatternATarget(prof);
                if (replacement != null && replacement.StringId != _ch1TargetSeed)
                {
                    _ch1TargetSeed = replacement.StringId;
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.A] TickRevalidate rerolled -> " + replacement.Name.ToString()
                        + " (" + replacement.StringId + ")");
                }
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.A] TickRevalidate threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        #endregion

        #region Pattern B — DestroyPartiesOfType

        // T15 Phase 3 Ch1 — Pattern B: resolve a target world party via the chief's
        // AllianceCh1TargetSeedHint, otherwise enter filter-watch mode where the
        // MobilePartyDestroyed listener accepts the first player-killed party that
        // matches a culture/template/clan filter token. Per the SHARED CONTRACT we
        // do NOT spawn new parties or add Harmony patches — only vanilla helpers.
        //
        // Token format used internally for _ch1TargetSeed when in filter-watch mode:
        //   "filter:culture=<id>"   — any party of that culture
        //   "filter:template=<id>"  — any party with that party-template stringid
        //   "filter:clan=<id>"      — any party belonging to that clan
        // Otherwise _ch1TargetSeed holds an explicit party StringId.

        public void InitializePatternB()
        {
            CultureProfile prof = null;
            if (!string.IsNullOrEmpty(_chiefCultureId))
                CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);
            if (prof == null)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.B] InitializePatternB NO PROFILE for culture="
                    + (_chiefCultureId ?? "<null>") + " -> FailHard");
                FailHard("no_culture_profile_for_pattern_b");
                return;
            }

            // 1) Seed hint = StringId of an existing world party (preferred path).
            //    Hints like "asmund_caravan_party" / "vlandian_outrider_party" / etc.
            //    are scripted party-marker StringIds the spec expects to be live in
            //    the world when the quest starts. If we can find one, prefer it.
            string hint = prof.AllianceCh1TargetSeedHint;
            if (!string.IsNullOrEmpty(hint))
            {
                MobileParty hinted = null;
                try
                {
                    if (MobileParty.All != null)
                        hinted = MobileParty.All
                            .FirstOrDefault(p => p != null && p.StringId == hint);
                }
                catch (Exception ex)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.B] InitializePatternB AllMobileParties scan threw "
                        + ex.GetType().Name + ": " + ex.Message);
                }
                if (hinted != null && !hinted.IsDisbanding)
                {
                    _ch1TrackedParty = hinted;
                    _ch1TargetSeed = hinted.StringId;
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.B] InitializePatternB hinted party=" + hinted.Name.ToString()
                        + " (" + hinted.StringId + ")");

                    // v219 — campaign-map marker + richer journal text for hinted-party path.
                    try { AddTrackedObject(hinted); }
                    catch (Exception trkEx)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.B][ERR] AddTrackedObject(hinted) " + trkEx.GetType().Name + ": " + trkEx.Message);
                    }
                    try
                    {
                        // 2026-06-03 quest-prose: hinted-target path routes through ResolveProse
                        // using the same AllianceCh1HuntBriefProse field as the world-scan path.
                        // Preserves existing loc key bp_alliance_ch1_b_hunt for backwards compat.
                        const string legacyHuntBriefHinted = "Hunt down {PARTY_NAME} — last seen near {AREA}. Destroy them within 25 days.";
                        TextObject huntLog = ResolveProse(
                            "AllianceCh1HuntBriefProse",
                            "bp_alliance_ch1_b_hunt",
                            legacyHuntBriefHinted);
                        huntLog.SetTextVariable("PARTY_NAME", hinted.Name ?? new TextObject("{=bp_alliancequest_004}the marked party"));
                        Settlement nearby = hinted.CurrentSettlement
                            ?? hinted.HomeSettlement
                            ?? hinted.LastVisitedSettlement;
                        huntLog.SetTextVariable("AREA",
                            nearby?.Name ?? new TextObject(_chiefCultureId ?? "{=bp_alliancequest_005}the region"));
                        AddLog(huntLog);
                    }
                    catch (Exception logEx)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.B][ERR] hinted huntLog " + logEx.GetType().Name + ": " + logEx.Message);
                    }
                    return;
                }
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.B] InitializePatternB hint='" + hint
                    + "' not found in world -> trying world-scan");
            }

            // v221 — world-scan: pick an actual live party from MobileParty.All
            // matching the chief's culture affinity. Prefer lordless parties
            // (deserters/caravans/raiders) because they get destroyed cleanly when
            // wiped — lord-led parties can escape with 1 troop and survive the
            // battle, which keeps the engine's MobilePartyDestroyed event from
            // firing. This was the v220 pain point: filter-watch's vague journal
            // text + the user couldn't tell which party to attack.
            {
                string scanAffinity = TryReadVanillaCultureRoot(prof) ?? ResolveAffinityCultureId();
                if (string.IsNullOrEmpty(scanAffinity)) scanAffinity = "empire"; // safe fallback
                MobileParty picked = null;
                try
                {
                    if (MobileParty.All != null)
                    {
                        // v222 (2026-06-01) — REORDERED. Pass order is now:
                        //   Pass A: LORD PARTIES (patrols) — preferred because lord parties
                        //           have a home fief and predictable patrol routes around it.
                        //           The player can actually FIND and INTERCEPT them.
                        //   Pass B: BANDIT parties — second choice, also relatively
                        //           geo-bounded (hideouts have neighborhoods).
                        //   Pass C: CARAVANS — LAST RESORT. Caravans cross-cross the entire
                        //           empire constantly; the settlement-pin marker flips between
                        //           towns every game-hour as the caravan moves. User reported
                        //           "i been wating nothing etc, plus it keep marking me others
                        //           settlements" — that was Convoy of Ethirea the Sutler doing
                        //           the empire-trade-route loop. Lord parties are now preferred.
                        // 2026-06-03 random-pick: collect ALL valid candidates per pass, then
                        // pick ONE at random via MBRandom. Pass priority preserved (lord > bandit
                        // > caravan) but WITHIN a pass the choice is random. Once _ch1TrackedParty
                        // is set (below), it's fixed for the rest of the quest — save/load shows
                        // the same target, no save-scumming pressure.
                        var lordCandidates = new System.Collections.Generic.List<MobileParty>();
                        foreach (var p in MobileParty.All)
                        {
                            if (!IsValidPatternBTarget(p, scanAffinity)) continue;
                            if (!p.IsLordParty) continue;
                            lordCandidates.Add(p);
                        }
                        if (lordCandidates.Count > 0)
                            picked = lordCandidates[MBRandom.RandomInt(lordCandidates.Count)];

                        if (picked == null)
                        {
                            var banditCandidates = new System.Collections.Generic.List<MobileParty>();
                            foreach (var p in MobileParty.All)
                            {
                                if (!IsValidPatternBTarget(p, scanAffinity)) continue;
                                if (!p.IsBandit) continue;
                                banditCandidates.Add(p);
                            }
                            if (banditCandidates.Count > 0)
                                picked = banditCandidates[MBRandom.RandomInt(banditCandidates.Count)];
                        }
                        if (picked == null)
                        {
                            var caravanCandidates = new System.Collections.Generic.List<MobileParty>();
                            foreach (var p in MobileParty.All)
                            {
                                if (!IsValidPatternBTarget(p, scanAffinity)) continue;
                                if (!p.IsCaravan) continue;
                                caravanCandidates.Add(p);
                            }
                            if (caravanCandidates.Count > 0)
                                picked = caravanCandidates[MBRandom.RandomInt(caravanCandidates.Count)];
                        }

                        // Diagnostic: log how many candidates the picker considered so you can
                        // see in trap log "considered X lords, Y bandits, Z caravans -> picked W".
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.B] InitializePatternB random-pick: lordCands="
                            + lordCandidates.Count
                            + " (picked=" + (picked != null ? picked.Name.ToString() : "<null>") + ")");
                    }
                }
                catch (Exception scanEx)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.B][ERR] InitializePatternB world-scan threw "
                        + scanEx.GetType().Name + ": " + scanEx.Message);
                }

                if (picked != null)
                {
                    _ch1TrackedParty = picked;
                    _ch1TargetSeed = picked.StringId;

                    try { AddTrackedObject(picked); }
                    catch (Exception trkEx)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.B][ERR] AddTrackedObject(world-scan) "
                            + trkEx.GetType().Name + ": " + trkEx.Message);
                    }

                    // v221k — also place the settlement pin marker so the player
                    // sees a big visible pin at the target's current location.
                    UpdateNearbySettlementMarker();

                    try
                    {
                        Settlement nearScan = picked.CurrentSettlement
                            ?? picked.HomeSettlement
                            ?? picked.LastVisitedSettlement;
                        // 2026-06-03 quest-prose: route through ResolveProse.
                        const string legacyHuntBriefWorldScan =
                            "Track and destroy {PARTY_NAME} — {ARTICLE} {CULTURE} {KIND} last seen near {AREA}."
                            + " Find them on the map (gold quest marker) and engage solo so the kill counts.";
                        TextObject scanLog = ResolveProse(
                            "AllianceCh1HuntBriefProse",
                            "bp_alliance_ch1_b_world_scan",
                            legacyHuntBriefWorldScan);
                        scanLog.SetTextVariable("PARTY_NAME",
                            picked.Name ?? new TextObject("{=bp_alliancequest_004}the marked party"));
                        scanLog.SetTextVariable("CULTURE", new TextObject(PrettyCulture(scanAffinity)));
                        scanLog.SetTextVariable("KIND", new TextObject(DescribePartyKind(picked)));
                        scanLog.SetTextVariable("ARTICLE",
                            new TextObject(ArticleFor(PrettyCulture(scanAffinity))));
                        scanLog.SetTextVariable("AREA",
                            nearScan?.Name ?? new TextObject("{=bp_alliancequest_006}the open road"));
                        AddLog(scanLog);
                    }
                    catch (Exception logEx)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.B][ERR] world-scan huntLog "
                            + logEx.GetType().Name + ": " + logEx.Message);
                    }

                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.B] InitializePatternB world-scan picked party="
                        + picked.Name.ToString()
                        + " (" + picked.StringId + ") culture=" + scanAffinity
                        + " kind=" + DescribePartyKind(picked));
                    return;
                }

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.B] InitializePatternB world-scan found NO live "
                    + scanAffinity + " party -> filter-watch fallback");
            }

            // 2) Filter-watch mode — derive a culture-affinity token from the same
            //    helpers Pattern A uses (VanillaCultureRoot field or _bpCultureAffinity
            //    table). MobilePartyDestroyed listener will then accept the first
            //    player-killed party matching this culture.
            string affinity = TryReadVanillaCultureRoot(prof) ?? ResolveAffinityCultureId();
            if (string.IsNullOrEmpty(affinity)) affinity = "empire"; // safe fallback
            string token = "filter:culture=" + affinity;
            _ch1TargetSeed = token;
            _ch1TrackedParty = null;
            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "[BAQ.B] InitializePatternB filter-watch token=" + token
                + " (culture=" + _chiefCultureId + ")");

            // v219 — campaign-map marker on the chief's home settlement + richer
            // journal text so the player knows WHERE to patrol and WHAT culture
            // counts (the hinted party id was a template not a runtime party, so
            // we anchor on the region instead).
            try
            {
                Hero chiefForMark = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(_chiefCultureId);
                Settlement markHere = chiefForMark?.HomeSettlement;
                if (markHere != null)
                {
                    try { AddTrackedObject(markHere); }
                    catch (Exception trkEx)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.B][ERR] AddTrackedObject(filter-area) " + trkEx.GetType().Name + ": " + trkEx.Message);
                    }
                    // 2026-06-03 quest-prose: route through ResolveProse (filter-watch initial entry).
                    const string legacyFilterWatch =
                        "Patrol the region near {AREA} — destroy any {CULTURE} party that ventures too close to {CHIEF}'s holdings within 25 days.";
                    TextObject watchLog = ResolveProse(
                        "AllianceMidQuestUpgradeProse",
                        "bp_alliance_ch1_b_filter",
                        legacyFilterWatch);
                    watchLog.SetTextVariable("AREA", markHere.Name ?? new TextObject("{=bp_alliancequest_007}the marked region"));
                    watchLog.SetTextVariable("CULTURE", new TextObject(affinity));
                    watchLog.SetTextVariable("CHIEF", chiefForMark?.Name ?? new TextObject(_chiefCultureId));
                    AddLog(watchLog);
                }
                else
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.B] filter-watch: no chief.HomeSettlement to mark — journal stays without map waypoint");
                }
            }
            catch (Exception markEx)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.B][ERR] filter-watch marker " + markEx.GetType().Name + ": " + markEx.Message);
            }
        }

        private static bool MatchesPatternBFilter(MobileParty p, string token)
        {
            if (p == null || string.IsNullOrEmpty(token)) return false;
            if (!token.StartsWith("filter:"))
            {
                // Direct-id token — exact StringId match (used when tracked party
                // is destroyed by something other than the player and we recover
                // by stringId equality).
                return p.StringId == token;
            }

            int eqIdx = token.IndexOf('=');
            if (eqIdx <= 0) return false;
            string key = token.Substring("filter:".Length, eqIdx - "filter:".Length);
            string val = token.Substring(eqIdx + 1);

            try
            {
                switch (key)
                {
                    case "culture":
                        return (p.MapFaction != null && p.MapFaction.Culture != null
                                && p.MapFaction.Culture.StringId == val)
                            || (p.LeaderHero != null && p.LeaderHero.Culture != null
                                && p.LeaderHero.Culture.StringId == val);
                    case "template":
                        // PartyTemplate isn't exposed on MobileParty/PartyBase in this
                        // 1.2.x API surface — fall back to substring matching the
                        // party's StringId (vanilla naming convention "<template>_N").
                        return !string.IsNullOrEmpty(p.StringId) && p.StringId.Contains(val);
                    case "clan":
                        return p.ActualClan != null && p.ActualClan.StringId == val;
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.B] MatchesPatternBFilter threw " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // MobilePartyDestroyed listener body (subscribed in RegisterEvents above,
        // already gated to DestroyPartiesOfType chiefs by the lambda wrapper).
        private void OnMobilePartyDestroyedForPatternB(MobileParty destroyed, PartyBase destroyer)
        {
            try
            {
                if (!this.IsOngoing) return;
                if (_chapter != 1) return;
                if (destroyed == null) return;

                // (a) Explicit tracked-party path.
                if (_ch1TrackedParty != null && destroyed == _ch1TrackedParty)
                {
                    // 2026-06-03 player-only kill rule. Previously: "destroyed by anyone
                    // counts" — but user observed a lord-patrol target captured offscreen
                    // by a rival faction, and the quest advanced without the player
                    // engaging. The quest spec was "destroy" but player UX is "be the
                    // destroyer". Branch (b) below already enforces this for the
                    // filter-watch path; aligning branch (a) with the same rule.
                    //
                    // When destroyer != player: log who actually killed the target +
                    // early-return WITHOUT advancing. TickRevalidate's stillActive check
                    // on the next hourly tick sees the destroyed party (IsActive=false),
                    // drops it, sets _ch1TargetSeed back to "filter:culture=...", and the
                    // subsequent upgrade scan picks a new target with a MidQuestUpgrade
                    // journal entry ("My runners lost the old mark... try {PARTY_NAME}").
                    bool playerWasDestroyer = destroyer != null
                        && destroyer.MobileParty == MobileParty.MainParty;
                    if (!playerWasDestroyer)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.B] Tracked party " + destroyed.Name.ToString()
                            + " destroyed by " + (destroyer?.Name?.ToString() ?? "<unknown>")
                            + " — NOT player. Quest stays on Ch1; TickRevalidate will re-pick a new target next hourly tick.");
                        return;
                    }

                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.B] OnMobilePartyDestroyed tracked party=" + destroyed.Name.ToString()
                        + " destroyed BY PLAYER -> AdvanceToCh2PreTrial");
                    AdvanceToCh2PreTrial();
                    return;
                }

                // (b) Filter-watch path — must be destroyed BY the player so a passing
                //     lord wiping an arbitrary same-culture party doesn't satisfy the
                //     objective on the player's behalf. destroyer is the PartyBase of
                //     the winning side.
                if (string.IsNullOrEmpty(_ch1TargetSeed)) return;
                if (!_ch1TargetSeed.StartsWith("filter:")) return; // tracked path covered above
                if (destroyer == null || destroyer.MobileParty != MobileParty.MainParty) return;
                if (!MatchesPatternBFilter(destroyed, _ch1TargetSeed)) return;

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.B] OnMobilePartyDestroyed filter-match party=" + destroyed.Name.ToString()
                    + " token=" + _ch1TargetSeed + " -> AdvanceToCh2PreTrial");
                AdvanceToCh2PreTrial();
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.B] OnMobilePartyDestroyed threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // v221k — last settlement we placed a marker on (for the "settlement pin"
        // visual). Transient — not a SaveableField; resets to null per session;
        // worst case we re-issue the marker on first hourly tick after load.
        private Settlement _ch1MarkedNearbySettlement;

        // v221k — settlement-pin marker for the tracked party. Renders a big
        // visible pin on the town/village the target is currently in (or its
        // last visited / home settlement when traveling between towns). Updates
        // every hourly tick as the caravan moves so the pin tracks the convoy.
        private void UpdateNearbySettlementMarker()
        {
            try
            {
                if (_ch1TrackedParty == null) return;

                Settlement target = _ch1TrackedParty.CurrentSettlement
                    ?? _ch1TrackedParty.LastVisitedSettlement
                    ?? _ch1TrackedParty.HomeSettlement;
                if (target == null) return;

                if (_ch1MarkedNearbySettlement == target) return; // already pinned here

                if (_ch1MarkedNearbySettlement != null)
                {
                    try { RemoveTrackedObject(_ch1MarkedNearbySettlement); } catch { }
                }
                try { AddTrackedObject(target); }
                catch (Exception aex)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.B][ERR] UpdateNearbySettlementMarker AddTrackedObject "
                        + aex.GetType().Name + ": " + aex.Message);
                    return;
                }

                Settlement prev = _ch1MarkedNearbySettlement;
                _ch1MarkedNearbySettlement = target;

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.B] UpdateNearbySettlementMarker pinned "
                    + (target.Name?.ToString() ?? target.StringId)
                    + (prev != null ? " (was " + (prev.Name?.ToString() ?? prev.StringId) + ")" : ""));
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.B][ERR] UpdateNearbySettlementMarker " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // 2026-06-03 quest-prose: three-tier prose resolution helpers.
        //   Layer 1: flagship CultureProfile field override
        //   Layer 2: per-culture AllianceProseMoulds template
        //   Layer 3: legacy short-form fallback (existing strings preserved)
        // All resolved text wraps in TextObject with the same {=loc_key} so SetTextVariable
        // calls fire normally on the returned object — no binding-code changes downstream.
        private TextObject ResolveProse(string fieldName, string locKey, string legacyShort)
        {
            BandItPlus.Cultures.CultureProfile prof = null;
            if (!string.IsNullOrEmpty(_chiefCultureId))
                BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);

            Hero chief = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(_chiefCultureId);

            // Layer 1: flagship override — read the field from CultureProfile via reflection.
            string fieldVal = null;
            try
            {
                if (prof != null)
                {
                    var field = typeof(BandItPlus.Cultures.CultureProfile).GetField(fieldName);
                    if (field != null)
                        fieldVal = field.GetValue(prof) as string;
                }
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.Prose] Layer1 reflect threw " + ex.GetType().Name + ": " + ex.Message);
            }
            if (!string.IsNullOrEmpty(fieldVal))
                return BindChiefVars(new TextObject("{=" + locKey + "}" + fieldVal), chief, prof);

            // Layer 2: per-culture mould template.
            if (!string.IsNullOrEmpty(_chiefCultureId)
                && BandItPlus.Cultures.AllianceProseMoulds.ByCultureId.TryGetValue(_chiefCultureId, out var mould))
            {
                var tmpl = mould.GetTemplate(fieldName);
                if (!string.IsNullOrEmpty(tmpl))
                    return BindChiefVars(new TextObject("{=" + locKey + "}" + tmpl), chief, prof);
            }

            // Layer 3: legacy short form (preserves current journal text).
            return new TextObject("{=" + locKey + "}" + legacyShort);
        }

        // Bind {CHIEF_NAME} and {CHIEF_TITLE} on the resolved TextObject so all
        // flagship + mould prose can reference the chief generically.
        private TextObject BindChiefVars(TextObject text, Hero chief, BandItPlus.Cultures.CultureProfile prof)
        {
            if (text == null) return text;
            if (chief?.Name != null) text.SetTextVariable("CHIEF_NAME", chief.Name);
            string title = ResolveChiefTitle(chief, prof);
            text.SetTextVariable("CHIEF_TITLE", new TextObject(title ?? string.Empty));
            return text;
        }

        // Prefer the explicit CultureProfile.ChiefTitle (Wave 4.11 encyclopedia label
        // — "Wood-Wife", "Pass-Lord", etc.) for {CHIEF_TITLE}. Falls back to chief name.
        private string ResolveChiefTitle(Hero chief, BandItPlus.Cultures.CultureProfile prof)
        {
            if (prof != null && !string.IsNullOrEmpty(prof.ChiefTitle))
                return prof.ChiefTitle;
            return chief?.Name?.ToString() ?? string.Empty;
        }

        // v221d helpers — shared by InitializePatternB world-scan AND
        // TickRevalidatePatternBTarget auto-upgrade scan.

        // Common eligibility predicate: must be a live, non-main, same-culture,
        // NON-BANDIT-FACTION party. Type-specific filtering (bandit-type vs caravan
        // vs lord patrol) is applied by the caller's pass loop.
        //
        // v221d: also excludes MapFaction.IsBanditFaction parties — a bandit chief
        // wouldn't send the player to fight other bandits (forest_bandits vs
        // looters is family infighting; he wants you to hit OUTSIDERS — the major
        // kingdoms whose patrols/caravans cross his territory). Even though we
        // pick by Culture (e.g. "empire"), bandit-faction parties can sometimes
        // carry that culture stringid via their leader's culture, so the explicit
        // IsBanditFaction guard is the safe block.
        private static bool IsValidPatternBTarget(MobileParty p, string targetCulture)
        {
            if (p == null || !p.IsActive || p.IsDisbanding) return false;
            if (p == MobileParty.MainParty) return false;
            if (p.IsVillager || p.IsMilitia) return false;        // peaceful village/town parties
            if (p.MapFaction != null && p.MapFaction.IsBanditFaction) return false; // no bandit-vs-bandit
            var cid = p.MapFaction?.Culture?.StringId;
            if (cid != targetCulture) return false;
            return true;
        }

        // Returns the appropriate noun ("raider band", "caravan", "lord patrol") for
        // the picked target so the journal text matches what the player will see.
        private static string DescribePartyKind(MobileParty p)
        {
            if (p == null) return BandItPlus.Localization.Get("bp_alliancequest_008", "party");
            if (p.IsBandit) return BandItPlus.Localization.Get("bp_alliancequest_009", "raider / deserter band");
            if (p.IsCaravan) return BandItPlus.Localization.Get("bp_alliancequest_010", "caravan");
            if (p.IsLordParty) return BandItPlus.Localization.Get("bp_alliancequest_011", "lord patrol");
            return BandItPlus.Localization.Get("bp_alliancequest_012", "warband");
        }

        // Capitalizes a culture id ("empire" → "Empire", "vlandian" → "Vlandian")
        // so journal text reads naturally.
        private static string PrettyCulture(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return BandItPlus.Localization.Get("bp_alliancequest_013", "unknown");
            return char.ToUpperInvariant(cultureId[0]) + cultureId.Substring(1);
        }

        // Pick the right indefinite article based on the FIRST LETTER of the pretty
        // culture string. "an Empire", "a Vlandian", "an Aserai".
        private static string ArticleFor(string nounStart)
        {
            if (string.IsNullOrEmpty(nounStart)) return BandItPlus.Localization.Get("bp_alliancequest_014", "a");
            char c = char.ToLowerInvariant(nounStart[0]);
            return (c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u')
                ? BandItPlus.Localization.Get("bp_alliancequest_015", "an")
                : BandItPlus.Localization.Get("bp_alliancequest_014", "a");
        }

        // Called from HourlyTick (Phase 2 Task) — two responsibilities:
        //   (A) If the tracked party gets disbanded or goes inactive without being
        //       destroyed, drop back to filter-watch.
        //   (B) v221 — If the quest is in filter-watch mode (e.g. ctor world-scan
        //       found no live target, OR the quest pre-dates v221's world-scan
        //       fix), scan MobileParty.All for a matching-culture party and
        //       UPGRADE filter-watch -> tracked. This auto-fixes existing quests
        //       in-flight without requiring re-accept.
        public void TickRevalidatePatternBTarget()
        {
            try
            {
                if (_chapter != 1) return;

                CultureProfile prof = null;
                if (!string.IsNullOrEmpty(_chiefCultureId))
                    CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);
                if (prof == null || prof.AllianceCh1Pattern != AllianceCh1Pattern.DestroyPartiesOfType)
                    return;

                // (A) tracked party went inactive OR is no longer a valid target type
                // (v221e: ALSO drops the tracked party if it's IsVillager / IsMilitia /
                // IsBanditFaction. This catches the case where a pre-v221d session picked
                // a villager party like "Fishers of Alosea" under the old broad filter —
                // on next session-load the new IsValidPatternBTarget predicate kicks in
                // and we forcibly re-pick a proper bandit/caravan/lord target).
                if (_ch1TrackedParty != null)
                {
                    string aff = TryReadVanillaCultureRoot(prof) ?? ResolveAffinityCultureId();
                    if (string.IsNullOrEmpty(aff)) aff = "empire";

                    bool stillActive = _ch1TrackedParty.IsActive && !_ch1TrackedParty.IsDisbanding;
                    bool stillValidType = IsValidPatternBTarget(_ch1TrackedParty, aff);

                    if (stillActive && stillValidType)
                    {
                        // v222 (2026-06-01) — caravan→lord opportunistic UPGRADE.
                        // If the current target is a caravan (wandering, marker thrashes
                        // between towns every hour — user reported "i been wating nothing
                        // etc, plus it keep marking me others settlements"), and a lord
                        // patrol of the same culture is available, swap to the lord.
                        // Lord parties have home fiefs + predictable patrol routes,
                        // so the player can actually intercept them.
                        if (_ch1TrackedParty.IsCaravan)
                        {
                            MobileParty lordUpgrade = null;
                            try
                            {
                                if (MobileParty.All != null)
                                {
                                    // 2026-06-03 random-pick: collect all valid lord candidates, pick one at random.
                                    var lordUpgradeCandidates = new System.Collections.Generic.List<MobileParty>();
                                    foreach (var p in MobileParty.All)
                                    {
                                        if (p == _ch1TrackedParty) continue;
                                        if (!IsValidPatternBTarget(p, aff)) continue;
                                        if (!p.IsLordParty) continue;
                                        lordUpgradeCandidates.Add(p);
                                    }
                                    if (lordUpgradeCandidates.Count > 0)
                                        lordUpgrade = lordUpgradeCandidates[MBRandom.RandomInt(lordUpgradeCandidates.Count)];
                                }
                            }
                            catch (Exception upEx)
                            {
                                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                    "[BAQ.B][ERR] caravan->lord upgrade scan threw "
                                    + upEx.GetType().Name + ": " + upEx.Message);
                            }

                            if (lordUpgrade != null)
                            {
                                string oldCaravanName = _ch1TrackedParty.Name != null
                                    ? _ch1TrackedParty.Name.ToString() : "<unnamed>";
                                try { RemoveTrackedObject(_ch1TrackedParty); } catch { }
                                _ch1TrackedParty = lordUpgrade;
                                _ch1TargetSeed = lordUpgrade.StringId;
                                try { AddTrackedObject(lordUpgrade); } catch { }
                                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                    "[BAQ.B] v222 caravan->lord upgrade: dropped "
                                    + oldCaravanName + " -> picked "
                                    + lordUpgrade.Name.ToString() + " ("
                                    + lordUpgrade.StringId + ")");
                                try
                                {
                                    Settlement nearUp = lordUpgrade.CurrentSettlement
                                        ?? lordUpgrade.HomeSettlement
                                        ?? lordUpgrade.LastVisitedSettlement;
                                    // 2026-06-03 quest-prose: route through ResolveProse (caravan->lord upgrade).
                                    const string legacyCaravanToLord =
                                        "Target updated: the previous caravan"
                                        + " wandered too widely. Now track and destroy"
                                        + " {PARTY_NAME} — {ARTICLE} {CULTURE} {KIND} based near {AREA}."
                                        + " A lord patrol stays in its home region; intercept on the map.";
                                    TextObject upLog = ResolveProse(
                                        "AllianceMidQuestUpgradeProse",
                                        "bp_alliance_ch1_b_caravan_to_lord",
                                        legacyCaravanToLord);
                                    upLog.SetTextVariable("PARTY_NAME",
                                        lordUpgrade.Name ?? new TextObject("{=bp_alliancequest_016}the marked patrol"));
                                    upLog.SetTextVariable("CULTURE", new TextObject(PrettyCulture(aff)));
                                    upLog.SetTextVariable("KIND", new TextObject(DescribePartyKind(lordUpgrade)));
                                    upLog.SetTextVariable("ARTICLE", new TextObject(ArticleFor(PrettyCulture(aff))));
                                    upLog.SetTextVariable("AREA",
                                        nearUp?.Name ?? new TextObject("{=bp_alliancequest_006}the open road"));
                                    AddLog(upLog);
                                }
                                catch (Exception logEx)
                                {
                                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                        "[BAQ.B][ERR] caravan->lord upgrade huntLog "
                                        + logEx.GetType().Name + ": " + logEx.Message);
                                }
                                UpdateNearbySettlementMarker();
                                return;
                            }
                        }

                        // v221k — also place a settlement marker on the target's
                        // current/nearest settlement so the player has a big visible
                        // pin to navigate toward. Settlement markers render larger
                        // than party markers (and don't move every tick). Update
                        // the marker as the caravan moves to a new town.
                        UpdateNearbySettlementMarker();
                        return;
                    }

                    string oldName = _ch1TrackedParty.Name != null ? _ch1TrackedParty.Name.ToString() : "<unnamed>";
                    string dropReason = !stillActive ? "no_longer_active" : "fails_v221d_predicate";
                    try { RemoveTrackedObject(_ch1TrackedParty); } catch { }
                    _ch1TrackedParty = null;
                    _ch1TargetSeed = "filter:culture=" + aff;
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.B] TickRevalidate dropped tracked party " + oldName
                        + " reason=" + dropReason
                        + " -> filter-watch token=" + _ch1TargetSeed);
                    // Don't immediately re-upgrade — let the next tick run pass (B).
                    return;
                }

                // (B) v221 — quest is in filter-watch mode. Try to upgrade to a
                // specific named party so the player gets a clear map marker +
                // journal text. Same scan-and-pick logic as InitializePatternB.
                if (string.IsNullOrEmpty(_ch1TargetSeed) || !_ch1TargetSeed.StartsWith("filter:")) return;

                // Extract the culture from "filter:culture=<value>"
                string targetCulture = null;
                int eqIdx = _ch1TargetSeed.IndexOf('=');
                if (eqIdx > 0 && eqIdx + 1 < _ch1TargetSeed.Length)
                    targetCulture = _ch1TargetSeed.Substring(eqIdx + 1);
                if (string.IsNullOrEmpty(targetCulture)) return;

                MobileParty picked = null;
                try
                {
                    if (MobileParty.All != null)
                    {
                        // v222 (2026-06-01) — REORDERED pass priority. Lord patrols FIRST
                        // (geo-bounded → catchable), bandits SECOND, caravans LAST RESORT.
                        // 2026-06-03 random-pick: within each pass, collect all valid
                        // candidates and pick ONE at random via MBRandom. Pass priority
                        // preserved (lord > bandit > caravan). This is the TickRevalidate
                        // filter-watch upgrade scan — fires hourly when quest is in
                        // filter-watch mode (e.g., after a target died offscreen and the
                        // player-only kill rule dropped it).
                        var revalLordCandidates = new System.Collections.Generic.List<MobileParty>();
                        foreach (var p in MobileParty.All)
                        {
                            if (!IsValidPatternBTarget(p, targetCulture)) continue;
                            if (!p.IsLordParty) continue;
                            revalLordCandidates.Add(p);
                        }
                        if (revalLordCandidates.Count > 0)
                            picked = revalLordCandidates[MBRandom.RandomInt(revalLordCandidates.Count)];

                        if (picked == null)
                        {
                            var revalBanditCandidates = new System.Collections.Generic.List<MobileParty>();
                            foreach (var p in MobileParty.All)
                            {
                                if (!IsValidPatternBTarget(p, targetCulture)) continue;
                                if (!p.IsBandit) continue;
                                revalBanditCandidates.Add(p);
                            }
                            if (revalBanditCandidates.Count > 0)
                                picked = revalBanditCandidates[MBRandom.RandomInt(revalBanditCandidates.Count)];
                        }
                        if (picked == null)
                        {
                            var revalCaravanCandidates = new System.Collections.Generic.List<MobileParty>();
                            foreach (var p in MobileParty.All)
                            {
                                if (!IsValidPatternBTarget(p, targetCulture)) continue;
                                if (!p.IsCaravan) continue;
                                revalCaravanCandidates.Add(p);
                            }
                            if (revalCaravanCandidates.Count > 0)
                                picked = revalCaravanCandidates[MBRandom.RandomInt(revalCaravanCandidates.Count)];
                        }
                    }
                }
                catch (Exception scanEx)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.B][ERR] TickRevalidate upgrade scan threw "
                        + scanEx.GetType().Name + ": " + scanEx.Message);
                    return;
                }

                if (picked == null)
                {
                    // v221b — defensive: ensure the chief's home settlement is always
                    // tracked so the player has SOMETHING to navigate to even when no
                    // specific upgrade target exists yet. QuestBase tracked-objects may
                    // not persist across save-load; re-issuing every hour keeps the
                    // marker visible. AddTrackedObject is idempotent (no double-marker).
                    try
                    {
                        Hero chiefForMark = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(_chiefCultureId);
                        Settlement markHere = chiefForMark?.HomeSettlement;
                        if (markHere != null) AddTrackedObject(markHere);
                    }
                    catch (Exception fbEx)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.B][ERR] TickRevalidate fallback settlement marker "
                            + fbEx.GetType().Name + ": " + fbEx.Message);
                    }
                    return; // no candidate this tick — try again next hour
                }

                _ch1TrackedParty = picked;
                _ch1TargetSeed = picked.StringId;

                try { AddTrackedObject(picked); }
                catch (Exception trkEx)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.B][ERR] TickRevalidate AddTrackedObject "
                        + trkEx.GetType().Name + ": " + trkEx.Message);
                }

                try
                {
                    Settlement nearScan = picked.CurrentSettlement
                        ?? picked.HomeSettlement
                        ?? picked.LastVisitedSettlement;
                    string pretty = PrettyCulture(targetCulture);
                    // 2026-06-03 quest-prose: route through ResolveProse (mid-quest target swap).
                    const string legacyUpgrade =
                        "A specific target has been identified: track and destroy"
                        + " {PARTY_NAME} — {ARTICLE} {CULTURE} {KIND} last seen near {AREA}."
                        + " A gold marker now follows them on the map.";
                    TextObject upgradeLog = ResolveProse(
                        "AllianceMidQuestUpgradeProse",
                        "bp_alliance_ch1_b_upgrade",
                        legacyUpgrade);
                    upgradeLog.SetTextVariable("PARTY_NAME",
                        picked.Name ?? new TextObject("{=bp_alliancequest_004}the marked party"));
                    upgradeLog.SetTextVariable("CULTURE", new TextObject(pretty));
                    upgradeLog.SetTextVariable("KIND", new TextObject(DescribePartyKind(picked)));
                    upgradeLog.SetTextVariable("ARTICLE", new TextObject(ArticleFor(pretty)));
                    upgradeLog.SetTextVariable("AREA",
                        nearScan?.Name ?? new TextObject("{=bp_alliancequest_006}the open road"));
                    AddLog(upgradeLog);
                }
                catch (Exception logEx)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.B][ERR] TickRevalidate upgrade log "
                        + logEx.GetType().Name + ": " + logEx.Message);
                }

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.B] TickRevalidate UPGRADED filter-watch -> tracked party="
                    + picked.Name.ToString() + " (" + picked.StringId + ") culture=" + targetCulture
                    + " kind=" + DescribePartyKind(picked));
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.B] TickRevalidate threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        #endregion

        #region Pattern C — RaidVillagesOfClan

        // T16 Phase 3 Ch1 — Pattern C: count completed village raids by MainParty against
        // villages owned by a specific target clan. Hint format (CultureProfile.AllianceCh1TargetSeedHint):
        //   "clan:<clanId>"           — target N defaults to 3
        //   "clan:<clanId>:N=<count>" — target N explicit (Bjornulf coastal variant uses N=1)
        // _ch1TargetSeed holds "clan:<clanId>" (without the N= suffix) after Initialize.
        // _ch1RaidCountTarget holds the resolved N.
        // _ch1RaidCount running tally; advance to Ch2_PreTrial on reaching target.

        public void InitializePatternC()
        {
            CultureProfile prof = null;
            if (!string.IsNullOrEmpty(_chiefCultureId))
                CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);
            if (prof == null)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.C] InitializePatternC NO PROFILE for culture="
                    + (_chiefCultureId ?? "<null>") + " -> FailHard");
                FailHard("no_culture_profile_for_pattern_c");
                return;
            }

            _ch1RaidCount = 0;

            // Parse hint: "clan:<id>" or "clan:<id>:N=<count>"
            string hint = prof.AllianceCh1TargetSeedHint;
            int target = 3;
            string clanFilter = null;

            if (!string.IsNullOrEmpty(hint) && hint.StartsWith("clan:"))
            {
                string body = hint.Substring("clan:".Length);
                int nIdx = body.IndexOf(":N=");
                if (nIdx > 0)
                {
                    clanFilter = body.Substring(0, nIdx);
                    int parsed;
                    if (int.TryParse(body.Substring(nIdx + ":N=".Length), out parsed) && parsed > 0)
                        target = parsed;
                }
                else
                {
                    clanFilter = body;
                }
            }

            // Fallback — pick the first culture-affinity clan that owns >= target villages.
            if (string.IsNullOrEmpty(clanFilter))
            {
                string affinity = TryReadVanillaCultureRoot(prof) ?? ResolveAffinityCultureId() ?? "empire";
                Clan candidate = null;
                try
                {
                    candidate = Clan.All.FirstOrDefault(c =>
                        c != null
                        && c.Culture != null
                        && c.Culture.StringId == affinity
                        && c != Clan.PlayerClan
                        && !c.IsBanditFaction
                        && c.Settlements != null
                        && c.Settlements.Count(s => s != null && s.IsVillage) >= target);
                }
                catch (Exception ex)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.C] InitializePatternC clan-scan threw "
                        + ex.GetType().Name + ": " + ex.Message);
                }
                if (candidate != null) clanFilter = candidate.StringId;
            }

            if (string.IsNullOrEmpty(clanFilter))
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.C] InitializePatternC no valid clan target for culture="
                    + (_chiefCultureId ?? "<null>") + " -> FailHard");
                FailHard("no_valid_pattern_c_clan");
                return;
            }

            _ch1TargetSeed = "clan:" + clanFilter;
            _ch1RaidCountTarget = target;
            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "[BAQ.C] InitializePatternC clan=" + clanFilter
                + " target=" + target + " (culture=" + _chiefCultureId + ")");
        }

        // Listener body (subscribed in RegisterEvents above, already gated to
        // RaidVillagesOfClan chiefs by the lambda wrapper). Per VillageBeingRaided
        // semantics: fires multiple times per raid; only the Looted-state tick counts.
        private void OnVillageRaidedForPatternC(Village village, MobileParty raiderParty, bool completed)
        {
            try
            {
                if (!this.IsOngoing) return;
                if (_chapter != 1) return;
                if (!completed) return;
                if (village == null) return;
                if (raiderParty == null || raiderParty != MobileParty.MainParty) return;
                if (string.IsNullOrEmpty(_ch1TargetSeed)) return;
                if (!_ch1TargetSeed.StartsWith("clan:")) return;

                string wanted = _ch1TargetSeed.Substring("clan:".Length);
                Clan owner = village.Settlement != null ? village.Settlement.OwnerClan : null;
                if (owner == null || owner.StringId != wanted) return;

                _ch1RaidCount++;
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.C] OnVillageRaided village="
                    + (village.Name != null ? village.Name.ToString() : "<unnamed>")
                    + " count=" + _ch1RaidCount + "/" + _ch1RaidCountTarget);

                if (_ch1RaidCount >= _ch1RaidCountTarget)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.C] OnVillageRaided target reached -> AdvanceToCh2PreTrial");
                    AdvanceToCh2PreTrial();
                }
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.C] OnVillageRaided threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Called from HourlyTick (Phase 2 Task) — if the target clan was destroyed or lost
        // all its villages mid-quest, re-seed against another culture-affinity clan so
        // Ch1 can still complete before timer expiry. If no replacement exists, leave the
        // existing seed in place; Ch1 timer will FailSoft naturally.
        public void TickRevalidatePatternCTarget()
        {
            try
            {
                if (_chapter != 1) return;
                if (string.IsNullOrEmpty(_ch1TargetSeed) || !_ch1TargetSeed.StartsWith("clan:")) return;
                CultureProfile prof = null;
                if (!string.IsNullOrEmpty(_chiefCultureId))
                    CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);
                if (prof == null || prof.AllianceCh1Pattern != AllianceCh1Pattern.RaidVillagesOfClan)
                    return;

                string wanted = _ch1TargetSeed.Substring("clan:".Length);
                Clan current = Clan.All.FirstOrDefault(c => c != null && c.StringId == wanted);
                int remaining = _ch1RaidCountTarget - _ch1RaidCount;
                bool stillViable = current != null
                    && !current.IsEliminated
                    && current.Settlements != null
                    && current.Settlements.Count(s => s != null && s.IsVillage
                        && s.Village != null
                        && s.Village.VillageState != Village.VillageStates.Looted) >= remaining;
                if (stillViable) return;

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.C] TickRevalidate clan=" + wanted
                    + " no longer viable (remaining=" + remaining + ") -> attempt reseed");

                string affinity = TryReadVanillaCultureRoot(prof) ?? ResolveAffinityCultureId() ?? "empire";
                Clan replacement = Clan.All.FirstOrDefault(c =>
                    c != null
                    && c.Culture != null
                    && c.Culture.StringId == affinity
                    && c != Clan.PlayerClan
                    && !c.IsBanditFaction
                    && !c.IsEliminated
                    && c.StringId != wanted
                    && c.Settlements != null
                    && c.Settlements.Count(s => s != null && s.IsVillage) >= remaining);
                if (replacement != null)
                {
                    _ch1TargetSeed = "clan:" + replacement.StringId;
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.C] TickRevalidate reseeded -> clan=" + replacement.StringId);
                }
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.C] TickRevalidate threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        #endregion

        #region Pattern D — EscortToSettlement

        // T17 Phase 3 Ch1 — Pattern D: resolve a destination settlement (vanilla town
        // or village of the rival affinity culture, or a scripted hint id like
        // "fence_town_local" / "looter_hideout_home" / "aserai_village_random") and
        // advance to Ch2_PreTrial when the player main party enters that destination.
        //
        // SHARED CONTRACT: no new Harmony patches, no engine-internal party spawns
        // that bypass vanilla helpers. The plan's "full" variant attempted to spawn
        // a tracked caravan via BanditPartyComponent.CreateBanditParty; that API is
        // unused elsewhere in this project + has version-skew risk on 1.4.x. Per the
        // plan's documented fallback path we ship the SIMPLIFIED variant only:
        // _ch1EscortCaravan stays null and arrival = MainParty entering destination.
        // If a future task wires a real caravan, OnSettlementEnteredForPatternD
        // already handles the two-party arrival check.

        public void InitializePatternD()
        {
            CultureProfile prof = null;
            if (!string.IsNullOrEmpty(_chiefCultureId))
                CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);
            if (prof == null)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.D] InitializePatternD NO PROFILE for culture="
                    + (_chiefCultureId ?? "<null>") + " -> FailHard");
                FailHard("no_culture_profile_for_pattern_d");
                return;
            }

            Settlement destination = ResolvePatternDDestination(prof);
            if (destination == null)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.D] InitializePatternD no valid destination for culture="
                    + (_chiefCultureId ?? "<null>") + " -> FailHard");
                FailHard("no_valid_pattern_d_destination");
                return;
            }
            _ch1EscortDestination = destination;
            _ch1TargetSeed = "settlement:" + destination.StringId;
            _ch1EscortCaravan = null; // simplified variant — no spawned caravan
            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "[BAQ.D] InitializePatternD destination="
                + (destination.Name != null ? destination.Name.ToString() : "<unnamed>")
                + " (" + destination.StringId + ") culture=" + _chiefCultureId
                + " variant=simplified");
        }

        private Settlement ResolvePatternDDestination(CultureProfile prof)
        {
            // 1) Scripted hint path — "settlement:<id>" for an exact StringId, or one
            //    of the authored sentinel strings ("fence_town_local",
            //    "looter_hideout_home", "aserai_village_random"). Sentinels fall back
            //    to the random pool below.
            string hint = prof != null ? prof.AllianceCh1TargetSeedHint : null;
            if (!string.IsNullOrEmpty(hint) && hint.StartsWith("settlement:"))
            {
                string id = hint.Substring("settlement:".Length);
                Settlement s = Settlement.Find(id);
                if (s != null) return s;
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.D] ResolveDestination scripted id=" + id
                    + " not found, falling back to random pool");
            }

            // 2) Random pool — town or village of the rival affinity culture (same
            //    helpers Patterns A/B/C use: VanillaCultureRoot field or
            //    _bpCultureAffinity table). Empire is the universal safe fallback.
            string affinity = TryReadVanillaCultureRoot(prof) ?? ResolveAffinityCultureId() ?? "empire";
            List<Settlement> pool = new List<Settlement>();
            try
            {
                if (Settlement.All != null)
                {
                    foreach (Settlement s in Settlement.All)
                    {
                        if (s == null) continue;
                        if (!(s.IsTown || s.IsVillage)) continue;
                        if (s.MapFaction == null) continue;
                        if (s.MapFaction.IsBanditFaction) continue;
                        if (s.Culture == null) continue;
                        if (s.Culture.StringId != affinity) continue;
                        pool.Add(s);
                    }
                }
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.D] ResolveDestination Settlement.All scan threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "[BAQ.D] ResolveDestination random pool size=" + pool.Count
                + " affinity=" + affinity);
            if (pool.Count == 0) return null;

            // Deterministic per-quest seed so save/load resolves identically.
            int seed = (_chiefCultureId ?? "x").GetHashCode() ^ (int)CampaignTime.Now.ToHours;
            return pool[new Random(seed).Next(pool.Count)];
        }

        // SettlementEntered listener body (subscribed in RegisterEvents above, already
        // gated to EscortToSettlement chiefs by the lambda wrapper). Per the simplified
        // variant: arrival counts when MainParty enters the destination. If a caravan
        // ever exists (_ch1EscortCaravan != null), BOTH must be at the destination so
        // a player who races ahead can't satisfy the objective alone.
        private void OnSettlementEnteredForPatternD(MobileParty party, Settlement settlement, Hero hero)
        {
            try
            {
                if (!this.IsOngoing) return;
                if (_chapter != 1) return;
                if (_ch1EscortDestination == null) return;
                if (settlement == null) return;
                if (settlement != _ch1EscortDestination) return;

                bool mainArrived = party == MobileParty.MainParty;
                bool caravanArrived = _ch1EscortCaravan != null && party == _ch1EscortCaravan;
                if (!mainArrived && !caravanArrived) return;

                // If a tracked caravan exists, gate on BOTH-present (caravan still
                // active + co-located). Simplified variant skips this entirely.
                if (_ch1EscortCaravan != null && _ch1EscortCaravan.IsActive)
                {
                    bool caravanHere = _ch1EscortCaravan.CurrentSettlement == settlement;
                    if (!caravanHere)
                    {
                        try
                        {
                            float d = _ch1EscortCaravan.GetPosition2D.Distance(settlement.GetPosition2D);
                            if (d < 2f) caravanHere = true;
                        }
                        catch { /* defensive — position read failure shouldn't gate arrival */ }
                    }
                    if (!caravanHere)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.D] OnSettlementEntered main arrived at "
                            + settlement.StringId + " but caravan not co-located -> wait");
                        return;
                    }
                }

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.D] OnSettlementEntered destination="
                    + (settlement.Name != null ? settlement.Name.ToString() : "<unnamed>")
                    + " arrived (mainArrived=" + mainArrived + " caravanArrived=" + caravanArrived
                    + ") -> AdvanceToCh2PreTrial");

                // Despawn any tracked caravan now that delivery is complete.
                if (_ch1EscortCaravan != null && _ch1EscortCaravan.IsActive)
                {
                    try { DestroyPartyAction.Apply(null, _ch1EscortCaravan); }
                    catch (Exception cleanupEx)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.D] DestroyPartyAction threw "
                            + cleanupEx.GetType().Name + ": " + cleanupEx.Message);
                    }
                }
                _ch1EscortCaravan = null;

                AdvanceToCh2PreTrial();
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.D] OnSettlementEntered threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Called from HourlyTick (Phase 2 Task) — escort-failure path. If a tracked
        // caravan was destroyed before reaching the destination, FailSoft. If the
        // destination itself becomes unreachable (eliminated/converted to bandit
        // ownership), attempt re-resolve; if no replacement, leave seed in place
        // and let the Ch1 timer FailSoft naturally.
        public void TickRevalidatePatternDTarget()
        {
            try
            {
                if (_chapter != 1) return;
                CultureProfile prof = null;
                if (!string.IsNullOrEmpty(_chiefCultureId))
                    CultureProfileRegistry.ByCultureId.TryGetValue(_chiefCultureId, out prof);
                if (prof == null || prof.AllianceCh1Pattern != AllianceCh1Pattern.EscortToSettlement)
                    return;

                // (a) Caravan destroyed before arrival -> FailSoft (escort lost).
                //     Note: in simplified variant _ch1EscortCaravan is null and this
                //     branch never fires. Reserved for the future full variant.
                if (_ch1EscortCaravan != null && !_ch1EscortCaravan.IsActive)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.D] TickRevalidate caravan no longer active before arrival -> FailSoft");
                    _ch1EscortCaravan = null;
                    FailSoft("pattern_d_caravan_lost");
                    return;
                }

                // (b) Destination no longer reachable -> attempt re-resolve.
                bool destinationBad = _ch1EscortDestination == null
                    || _ch1EscortDestination.MapFaction == null
                    || _ch1EscortDestination.MapFaction.IsBanditFaction;
                if (!destinationBad) return;

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.D] TickRevalidate destination no longer reachable -> attempt reseed");
                Settlement replacement = ResolvePatternDDestination(prof);
                if (replacement != null && replacement != _ch1EscortDestination)
                {
                    _ch1EscortDestination = replacement;
                    _ch1TargetSeed = "settlement:" + replacement.StringId;
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.D] TickRevalidate reseeded destination -> "
                        + replacement.StringId);
                }
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.D] TickRevalidate threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        #endregion

        // ===== Per-chapter transitions (private; Phase 2-4 fill) =====
        // Stubs throw NotImplementedException ONLY inside an IfDebug block; in
        // release/skeleton mode they log + return so a wrong call site can't NRE
        // the game during a real save/load. Phase 2-4 will replace each stub
        // with full transition + journal-log + chief-roster mutation code.

        private void AdvanceToCh2PreTrial()
        {
            if (_chapter != 1)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ] AdvanceToCh2PreTrial called from chapter=" + _chapter
                    + " — ignoring (already advanced)");
                return;
            }

            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "[BAQ] AdvanceToCh2PreTrial culture=" + (_chiefCultureId ?? "<null>"));

            _chapter = 2;
            _ch2InTrial = false;
            // Arm the Ch2_PreTrial 7-day idle timer (spec Section 4 state machine)
            _ch2PreTrialDueHours = CampaignTime.Now.ToHours + (7.0 * 24.0);

            // Despawn any tracked caravan that's still around (Pattern D edge case if completion
            // races SettlementEntered cleanup)
            if (_ch1EscortCaravan != null && _ch1EscortCaravan.IsActive)
            {
                try { DestroyPartyAction.Apply(null, _ch1EscortCaravan); }
                catch (Exception ex)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ] AdvanceToCh2PreTrial DestroyPartyAction threw "
                        + ex.GetType().Name + ": " + ex.Message);
                }
                _ch1EscortCaravan = null;
            }

            // Journal breadcrumb (spec Section 2 table — "Ch1 -> Ch2 (success)")
            try
            {
                Hero chief = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(_chiefCultureId);

                // 2026-06-03 hideout-binding fix. chief.HomeSettlement returns the chief's
                // BIRTH/ORIGIN town — for BP synthesized chiefs created via the
                // merchant_empire template, that's an Empire town like Diathma, NOT the
                // bandit hideout where the chief actually lives. User reported the Ch2
                // advance journal said "Come to Diathma after dusk" when it should say
                // "Come to Granny Hod's Hideout".
                //
                // Three-tier resolution:
                //   1. chief.CurrentSettlement IF it's a hideout (BP's EnforceChiefAtHideouts
                //      pins each chief to their bandit hideout every hour, so this is
                //      reliable 99% of the time)
                //   2. Scan Settlement.All for the first hideout whose culture matches
                //      _chiefCultureId (covers race window between Enforce ticks)
                //   3. Fall back to chief.HomeSettlement (legacy behavior, will say Diathma
                //      but at least doesn't blow up)
                Settlement hideout = null;
                if (chief?.CurrentSettlement != null && chief.CurrentSettlement.IsHideout)
                    hideout = chief.CurrentSettlement;
                if (hideout == null)
                {
                    try
                    {
                        foreach (var s in Settlement.All)
                        {
                            if (s == null || !s.IsHideout) continue;
                            if (s.Culture?.StringId != _chiefCultureId) continue;
                            hideout = s;
                            break;
                        }
                    }
                    catch (Exception hex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ] AdvanceToCh2PreTrial hideout-by-culture scan threw "
                            + hex.GetType().Name + ": " + hex.Message);
                    }
                }
                if (hideout == null) hideout = chief?.HomeSettlement;

                // 2026-06-03 quest-prose: route through ResolveProse (Ch1->Ch2 advancement).
                const string legacyCh2Advance = "Return to {CHIEF_NAME} at {HIDEOUT} to speak of the trial.";
                TextObject log = ResolveProse(
                    "AllianceCh2AdvanceProse",
                    "bp_alliance_ch1_to_ch2_log",
                    legacyCh2Advance);
                // Retain explicit CHIEF binding for backwards compat; BindChiefVars also binds {CHIEF_NAME}.
                log.SetTextVariable("CHIEF", chief?.Name ?? new TextObject(_chiefCultureId));
                log.SetTextVariable("HIDEOUT", hideout?.Name ?? new TextObject("{=bp_alliancequest_017}the hideout"));
                AddLog(log);
            }
            catch (Exception logEx)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ] AdvanceToCh2PreTrial AddLog threw "
                    + logEx.GetType().Name + ": " + logEx.Message);
            }
        }

        // T22 Phase 3 Ch2 — chief-in-party injection + post-condition assert + target party spawn.
        // Called by the Ch2-accept dialog consequence (wired in Phase 5 / T30 / T31). Implements
        // spec Section 4 Ch2 steps 2-3 verbatim:
        //   (a) explicit Hero-insertion via MemberRoster.AddToCounts(c, 1, insertAtFront:true)
        //   (b) post-condition assert that the chief is actually present as a Hero
        //   (c) on success: spawn target party near hideout + flip _ch2InTrial + arm idle timer
        //   (d) on failure: roll back, hand off to TryR1Fallback (T23 — currently a stub that
        //       returns false; T23 will swap the body for the army-tagalong fallback)
        //
        // Memory anchors:
        //   [bannerlord-bandit-party-no-leader-hero] — chief party can have no LeaderHero
        //   [csharp-empty-catch-silent-failures]    — every catch logs type+message
        //   [bannerlord-campaigntime-tohours]       — _ch2InTrialDueHours uses .ToHours

        // T31 — public entry point invoked by BanditDialogManager when the player
        // accepts the Ch2 trial. Routes to the existing private transition so the
        // state machine retains a single owner. Defensive pre-check logs (does not
        // throw) so a misplaced dialog wiring never corrupts quest state.
        public void BeginTrial()
        {
            if (_chapter != 2 || _ch2InTrial)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BanditAllianceQuest] BeginTrial called in wrong state — chapter="
                    + _chapter + " inTrial=" + _ch2InTrial);
                return;
            }
            AdvanceToCh2InTrial();
        }

        private void AdvanceToCh2InTrial()
        {
            try
            {
                // Pre-checks — must be in Ch2_PreTrial; reject re-entry.
                if (_chapter != 2)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2] AdvanceToCh2InTrial called from chapter=" + _chapter
                        + " — ignoring (must be 2)");
                    return;
                }
                if (_ch2InTrial)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2] AdvanceToCh2InTrial called when already in trial — ignoring");
                    return;
                }

                Hero chief = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(_chiefCultureId);
                if (chief == null)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2][ERR] AdvanceToCh2InTrial chief=null culture=" + _chiefCultureId);
                    FailHard("ch2_chief_lookup_failed");
                    return;
                }

                if (chief.IsDead || chief.IsPrisoner)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2][ERR] AdvanceToCh2InTrial chief unavailable IsDead=" + chief.IsDead
                        + " IsPrisoner=" + chief.IsPrisoner);
                    FailHard("ch2_chief_dead_or_prisoner_pre_inject");
                    return;
                }

                // --- (a) EXACT chief-in-party insertion overload per spec Section 4 step 2 ---
                //     AddToCounts(CharacterObject character, int count, bool insertAtFront)
                MobileParty.MainParty.MemberRoster.AddToCounts(
                    chief.CharacterObject,
                    1,
                    /* insertAtFront: */ true);

                // --- (b) Post-condition assert per spec Section 4 step 3 ---
                bool chiefIsHeroInRoster = MobileParty.MainParty.MemberRoster
                    .GetTroopRoster()
                    .Any(e => e.Character == chief.CharacterObject && e.Character.IsHero);

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.Ch2] AdvanceToCh2InTrial chief=" + chief.StringId
                    + " AddToCounts(insertAtFront:true) postAssert="
                    + (chiefIsHeroInRoster ? "PASS" : "FAIL"));

                if (!chiefIsHeroInRoster)
                {
                    // R1 FALLBACK PATH — assert FAILED, hand off to Task 23.
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2][R1] post-condition assert FAILED — invoking R1 army-tagalong fallback");

                    // Roll back the failed insert before the fallback retries with a different
                    // strategy (e.g. tagging the chief party as an army member rather than
                    // physically merging).
                    try
                    {
                        MobileParty.MainParty.MemberRoster.RemoveTroop(chief.CharacterObject, 1);
                    }
                    catch (Exception rbex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.Ch2][R1] rollback RemoveTroop threw "
                            + rbex.GetType().Name + ": " + rbex.Message);
                    }

                    if (!TryR1Fallback(chief))
                    {
                        FailHard("ch2_inject_assert_failed_and_r1_fallback_failed");
                        return;
                    }
                    // R1 succeeded — proceed to target-party spawn below.
                }

                // --- (c) Spawn target party at hideout-adjacent location ---
                Settlement chiefHideout = chief.HomeSettlement;
                if (chiefHideout == null)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2][ERR] AdvanceToCh2InTrial chief.HomeSettlement=null");
                    FailHard("ch2_chief_no_home_hideout");
                    return;
                }

                MobileParty target = SpawnCh2TargetParty(chief, chiefHideout);
                if (target == null)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2][ERR] AdvanceToCh2InTrial target spawn returned null");
                    FailHard("ch2_target_spawn_failed");
                    return;
                }
                _ch2TargetParty = target;

                // --- (d) Flip the trial-active flag + start idle timer ---
                _ch2InTrial = true;
                _objectiveStep = 1;
                _ch2InTrialDueHours = CampaignTime.Now.ToHours + (24.0 * 7.0);
                _ch2ChiefWoundedSinceHours = -1.0;

                // Journal breadcrumb (spec Section 2 table — "Ch2 trial start")
                try
                {
                    TextObject log = new TextObject(
                        "{=bp_alliance_ch2_journal}Ride out with {CHIEF}. The trial begins when you engage {TARGET}.");
                    log.SetTextVariable("CHIEF", chief.Name);
                    log.SetTextVariable("TARGET", target.Name ?? new TextObject("{=bp_alliancequest_018}the target"));
                    AddLog(log);
                }
                catch (Exception logEx)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2] AdvanceToCh2InTrial AddLog threw "
                        + logEx.GetType().Name + ": " + logEx.Message);
                }

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.Ch2] AdvanceToCh2InTrial COMPLETE chief=" + chief.StringId
                    + " target=" + target.StringId
                    + " due=" + _ch2InTrialDueHours.ToString("F1"));
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.Ch2][ERR] AdvanceToCh2InTrial " + ex.GetType().Name + ": " + ex.Message);
                FailHard("ch2_inject_unexpected_exception");
            }
        }

        // T22 — culture-flavored target party spawn near the chief's hideout.
        // Uses BanditPartyComponent.CreateBanditParty(string, Clan, Hideout, bool, PartyTemplateObject, CampaignVec2)
        // verified via MetadataLoadContext probe against the installed
        // TaleWorlds.CampaignSystem.dll. This single call creates AND positions the party
        // so no separate InitializeMobilePartyAtPosition is needed.
        private MobileParty SpawnCh2TargetParty(Hero chief, Settlement hideout)
        {
            try
            {
                // Hideout component is REQUIRED — CreateBanditParty expects a non-null
                // Hideout. HomeSettlement should always have one for bandit chiefs, but
                // guard defensively per [[csharp-empty-catch-silent-failures]].
                if (hideout.Hideout == null)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2][ERR] SpawnCh2TargetParty hideout.Hideout=null on settlement="
                        + hideout.StringId);
                    return null;
                }

                // Position offset from hideout (campaign-map units; ~0.5 unit ≈ short step).
                Vec2 anchor = hideout.GetPosition2D;
                Vec2 spawnVec = anchor + new Vec2(0.5f, 0.5f);

                // Validate terrain via the engine's GetFaceIndex (by-ref CampaignVec2 -> PathFaceRecord).
                // If the initial offset lands in unwalkable terrain, walk a small grid of offsets
                // looking for a valid face. Fall back to the hideout's own position (always valid)
                // if nothing nearby works.
                CampaignVec2 spawnPos = new CampaignVec2(spawnVec, /* isOnLand: */ true);
                bool valid = false;
                try
                {
                    PathFaceRecord face = Campaign.Current.MapSceneWrapper.GetFaceIndex(spawnPos);
                    valid = face.IsValid();
                }
                catch (Exception faceEx)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2] SpawnCh2TargetParty GetFaceIndex(initial) threw "
                        + faceEx.GetType().Name + ": " + faceEx.Message);
                }
                if (!valid)
                {
                    float[] offsets = { -0.5f, 0.0f, 0.5f, 1.0f };
                    for (int i = 0; i < offsets.Length && !valid; i++)
                    {
                        for (int j = 0; j < offsets.Length && !valid; j++)
                        {
                            Vec2 candVec = anchor + new Vec2(offsets[i], offsets[j]);
                            CampaignVec2 cand = new CampaignVec2(candVec, /* isOnLand: */ true);
                            try
                            {
                                PathFaceRecord face = Campaign.Current.MapSceneWrapper.GetFaceIndex(cand);
                                if (face.IsValid()) { spawnPos = cand; valid = true; }
                            }
                            catch { /* defensive — keep scanning */ }
                        }
                    }
                    if (!valid)
                    {
                        // Last resort — at the hideout itself (engine guarantees this is on a valid face).
                        spawnPos = new CampaignVec2(anchor, /* isOnLand: */ true);
                    }
                }

                // Culture-matched party template. Tier-+1 retry-after-break is deferred until
                // T26 wires the break/retry state machine; T22 ships the baseline template.
                CultureObject culture = chief.Culture ?? hideout.Culture;
                PartyTemplateObject template = null;
                if (culture != null)
                {
                    template = culture.BanditBossPartyTemplate ?? culture.DefaultPartyTemplate;
                }
                if (template == null)
                {
                    template = MBObjectManager.Instance.GetObject<PartyTemplateObject>("looters_template");
                }
                if (template == null)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2][ERR] SpawnCh2TargetParty no party template available");
                    return null;
                }

                string partyId = "bp_alliance_trial_target_" + _chiefCultureId
                    + "_" + ((int)CampaignTime.Now.ToHours).ToString();

                // 07-03: null clan NREs in WarPartyComponent.OnInitialize (1.4.5+) —
                // resolve the culture's real bandit clan (signature-quest lesson).
                var targetClan = BanditSignatureQuest.ResolveBanditClan(_chiefCultureId);
                if (targetClan == null)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2][ERR] SpawnCh2TargetParty: no bandit clan for " + _chiefCultureId);
                    return null;
                }
                MobileParty party = null;
                try
                {
                    party = BanditPartyComponent.CreateBanditParty(
                        partyId,
                        targetClan,
                        hideout.Hideout,
                        /* isBossParty: */ false,
                        template,
                        spawnPos);
                }
                catch (Exception cex)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2][ERR] SpawnCh2TargetParty CreateBanditParty threw "
                        + cex.GetType().Name + ": " + cex.Message);
                    return null;
                }
                if (party == null)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2][ERR] SpawnCh2TargetParty CreateBanditParty returned null");
                    return null;
                }

                // SetCustomName lives on PartyBase, not MobileParty — go through party.Party.
                try
                {
                    TextObject name = new TextObject("{=bp_alliance_ch2_target_name}Trial Target — {CHIEF}");
                    name.SetTextVariable("CHIEF", chief.Name);
                    party.Party.SetCustomName(name);
                }
                catch (Exception nex)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2] SpawnCh2TargetParty SetCustomName threw "
                        + nex.GetType().Name + ": " + nex.Message);
                }

                try
                {
                    if (party.Ai != null) party.Ai.SetDoNotMakeNewDecisions(true);
                    party.SetMoveModeHold();
                }
                catch (Exception aex)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2] SpawnCh2TargetParty Ai/Move setup threw "
                        + aex.GetType().Name + ": " + aex.Message);
                }

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.Ch2] SpawnCh2TargetParty id=" + party.StringId
                    + " pos=(" + spawnPos.X.ToString("F2") + "," + spawnPos.Y.ToString("F2") + ")"
                    + " troops=" + party.MemberRoster.TotalManCount
                    + " template=" + template.StringId);

                return party;
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.Ch2][ERR] SpawnCh2TargetParty " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        // T23 — R1 army-tagalong fallback for the case where the chief Hero
        // can't be physically inserted into MainParty (synthesized minor-faction
        // chiefs sometimes reject AddToCounts(insertAtFront:true)). Strategy:
        //   (1) locate (or synthesize) the chief's MobileParty
        //   (2) escort-AI it onto MainParty so it tags along on the map
        //   (3) if player has a Kingdom, also merge into MainParty's Army for
        //       battle co-deployment; otherwise escort-AI alone is sufficient
        //   (4) persist _r1FallbackActive + _r1ChiefParty so the symmetric
        //       teardown in Win/Fail paths can detach the party cleanly
        //
        // Carry-forwards:
        //   [bannerlord-bandit-party-no-leader-hero] — bandit parties may have
        //       no LeaderHero; we never assume one exists.
        //   [csharp-empty-catch-silent-failures] — every catch logs Type+Message.
        private bool TryR1Fallback(Hero chief)
        {
            if (chief == null)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.Ch2][R1] TryR1Fallback chief=null — aborting");
                return false;
            }

            try
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.Ch2][R1] entering fallback for chief=" + chief.StringId);

                // --- Step 1: locate (or synthesize) the chief's MobileParty ---
                MobileParty chiefParty = chief.PartyBelongedTo;
                bool partyWasSynthesized = false;

                if (chiefParty == null)
                {
                    Settlement hideout = chief.HomeSettlement;
                    if (hideout == null || hideout.Hideout == null)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.Ch2][R1] chief has no HomeSettlement/Hideout — aborting fallback");
                        return false;
                    }

                    CultureObject culture = chief.Culture ?? hideout.Culture;
                    PartyTemplateObject tmpl = culture != null
                        ? (culture.BanditBossPartyTemplate ?? culture.DefaultPartyTemplate)
                        : null;
                    if (tmpl == null)
                    {
                        tmpl = MBObjectManager.Instance.GetObject<PartyTemplateObject>("looters_template");
                    }
                    if (tmpl == null)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.Ch2][R1] no party template available for synth — aborting fallback");
                        return false;
                    }

                    string synthId = "bp_alliance_r1_chiefparty_" + (_chiefCultureId ?? "x")
                        + "_" + ((int)CampaignTime.Now.ToHours).ToString();
                    // hideout.GetPosition2D returns Vec2 — wrap as CampaignVec2 for the
                    // BanditPartyComponent.CreateBanditParty signature (matches the
                    // SpawnCh2TargetParty pattern above in this same file).
                    CampaignVec2 spawnPos = new CampaignVec2(hideout.GetPosition2D, /* isOnLand: */ true);
                    // 07-03: same null-clan NRE fix as SpawnCh2TargetParty above.
                    var synthClan = BanditSignatureQuest.ResolveBanditClan(_chiefCultureId);
                    if (synthClan == null)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.Ch2][R1][ERR] no bandit clan for " + _chiefCultureId + " — aborting synth");
                        return false;
                    }
                    try
                    {
                        chiefParty = BanditPartyComponent.CreateBanditParty(
                            synthId,
                            synthClan,
                            hideout.Hideout,
                            /* isBossParty: */ true,
                            tmpl,
                            spawnPos);
                    }
                    catch (Exception cex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.Ch2][R1] CreateBanditParty (synth) threw "
                            + cex.GetType().Name + ": " + cex.Message);
                        return false;
                    }
                    if (chiefParty == null)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.Ch2][R1] CreateBanditParty (synth) returned null");
                        return false;
                    }

                    partyWasSynthesized = true;

                    // Try to inject the chief into THIS new roster (best-effort —
                    // if it still fails we have a faceless escort party, which is
                    // acceptable for the trial since the player just needs ANY
                    // friendly party to satisfy R1's army-tagalong contract).
                    try
                    {
                        chiefParty.MemberRoster.AddToCounts(chief.CharacterObject, 1, true);
                    }
                    catch (Exception aex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.Ch2][R1] synth-roster AddToCounts threw "
                            + aex.GetType().Name + ": " + aex.Message
                            + " — continuing with faceless escort party");
                    }

                    try
                    {
                        TextObject cname = new TextObject("{=bp_alliance_r1_party_name}{CHIEF}'s Warband");
                        cname.SetTextVariable("CHIEF", chief.Name);
                        chiefParty.Party.SetCustomName(cname);
                    }
                    catch (Exception nex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.Ch2][R1] SetCustomName threw "
                            + nex.GetType().Name + ": " + nex.Message);
                    }

                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2][R1] synthesized chief party id=" + chiefParty.StringId
                        + " troops=" + chiefParty.MemberRoster.TotalManCount);
                }
                else
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2][R1] using chief's existing party id=" + chiefParty.StringId);
                }

                // --- Step 2: escort-AI the chief party onto MainParty ---
                // SetMoveEscortParty lives on MobileParty itself (NOT MobilePartyAi)
                // per TaleWorlds.CampaignSystem.dll metadata dump.
                try
                {
                    if (chiefParty.Ai != null)
                    {
                        chiefParty.Ai.SetDoNotMakeNewDecisions(false);
                    }
                    BandItPlus.Compat.PartyOrderCompat.EscortParty(chiefParty, MobileParty.MainParty);
                }
                catch (Exception eex)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2][R1] SetMoveEscortParty threw "
                        + eex.GetType().Name + ": " + eex.Message);
                }

                // --- Step 3: optional Army wrapper (only if player is in a Kingdom) ---
                // The Army API in 1.2.x/1.4.x is finicky for non-kingdom leaders;
                // escort-AI alone gives us the on-map tagalong behavior we need.
                // If a kingdom IS available, try to formalize via Army for battle
                // co-deployment (army members get pulled into player-led MapEvents).
                Kingdom playerKingdom = Hero.MainHero != null && Hero.MainHero.Clan != null
                    ? Hero.MainHero.Clan.Kingdom : null;
                if (playerKingdom != null && MobileParty.MainParty.Army == null)
                {
                    try
                    {
                        Type armyType = typeof(Army);
                        ConstructorInfo armyCtor = armyType.GetConstructor(
                            BindingFlags.NonPublic | BindingFlags.Instance,
                            null,
                            new Type[] { typeof(Kingdom), typeof(MobileParty), typeof(Army.ArmyTypes) },
                            null);
                        if (armyCtor != null)
                        {
                            Army a = (Army)armyCtor.Invoke(new object[]
                            {
                                playerKingdom, MobileParty.MainParty, Army.ArmyTypes.Patrolling
                            });
                            PropertyInfo armyProp = typeof(MobileParty).GetProperty("Army");
                            if (armyProp != null && armyProp.CanWrite)
                            {
                                armyProp.SetValue(MobileParty.MainParty, a);
                            }
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BAQ.Ch2][R1] transient Army created (kingdom="
                                + playerKingdom.StringId + ")");
                        }
                        else
                        {
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BAQ.Ch2][R1] Army ctor (Kingdom,MobileParty,ArmyTypes) not found via reflection "
                                + "— skipping Army wrapper, escort-AI-only");
                        }
                    }
                    catch (Exception aex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.Ch2][R1] Army creation threw "
                            + aex.GetType().Name + ": " + aex.Message
                            + " — escort-AI-only path");
                    }
                }

                // Merge chief party into the Army if we now have one.
                if (MobileParty.MainParty.Army != null)
                {
                    try
                    {
                        MobileParty.MainParty.Army.AddPartyToMergedParties(chiefParty);
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.Ch2][R1] AddPartyToMergedParties OK chiefParty="
                            + chiefParty.StringId
                            + " army.Leader=" + (MobileParty.MainParty.Army.LeaderParty != null
                                ? MobileParty.MainParty.Army.LeaderParty.StringId : "<null>"));
                    }
                    catch (Exception mex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.Ch2][R1] AddPartyToMergedParties threw "
                            + mex.GetType().Name + ": " + mex.Message
                            + " — escort-AI-only path still active");
                    }
                }
                else
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ.Ch2][R1] no Army wrapper (player kingdom=null or ctor failed) "
                        + "— escort-AI tagalong only");
                }

                // --- Step 4: persist R1 state so teardown can find this party ---
                _r1FallbackActive = true;
                _r1ChiefParty = chiefParty;

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.Ch2][R1] fallback ENGAGED chiefParty=" + chiefParty.StringId
                    + " synthesized=" + partyWasSynthesized
                    + " army=" + (MobileParty.MainParty.Army != null));
                return true;
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.Ch2][R1][ERR] TryR1Fallback " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // T23 — symmetric teardown for the R1 fallback. Called from Win,
        // CompleteSuccessfully, FailHard, and FailSoft so the chief party
        // is detached from the player's Army (if attached) and returned to
        // its hideout. If R1 synthesized the party, destroy it after the
        // chief is teleported back to his hideout.
        //
        // Idempotent: safe to call when _r1FallbackActive=false (no-op).
        private void R1TeardownIfActive()
        {
            if (!_r1FallbackActive)
                return;

            try
            {
                Hero chief = BandItPlus.Heroes.BanditChiefRegistry.Instance != null
                    ? BandItPlus.Heroes.BanditChiefRegistry.Instance.GetChief(_chiefCultureId)
                    : null;
                Settlement hideout = chief != null ? chief.HomeSettlement : null;

                MobileParty chiefParty = _r1ChiefParty;
                if (chiefParty != null && chiefParty.IsActive)
                {
                    // Detach from Army if still attached. The 1.2.x/1.4.x Army API
                    // has no public RemovePartyFromMergedParties — sever the party-
                    // side link via MobileParty.Army = null (verified setter present
                    // in TaleWorlds.CampaignSystem.dll metadata).
                    if (chiefParty.Army != null)
                    {
                        try
                        {
                            chiefParty.Army = null;
                        }
                        catch (Exception rmex)
                        {
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BAQ.Ch2][R1][ERR] chiefParty.Army = null threw "
                                + rmex.GetType().Name + ": " + rmex.Message);
                        }
                    }

                    // Send chief party home. SetMoveGoToSettlement is on MobileParty
                    // itself, not MobilePartyAi.
                    try
                    {
                        if (hideout != null)
                        {
                            BandItPlus.Compat.PartyOrderCompat.GoToSettlement(chiefParty, hideout);
                        }
                    }
                    catch (Exception aex)
                    {
                        BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                            "[BAQ.Ch2][R1][ERR] SetMoveGoToSettlement "
                            + aex.GetType().Name + ": " + aex.Message);
                    }

                    // If R1 synthesized this party (id prefix "bp_alliance_r1_chiefparty_"),
                    // teleport chief home immediately and destroy the transient party.
                    bool wasSynthesized = chiefParty.StringId != null
                        && chiefParty.StringId.StartsWith("bp_alliance_r1_chiefparty_");
                    if (wasSynthesized)
                    {
                        try
                        {
                            if (chief != null && hideout != null)
                            {
                                EnterSettlementAction.ApplyForCharacterOnly(chief, hideout);
                            }
                        }
                        catch (Exception eex)
                        {
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BAQ.Ch2][R1][ERR] EnterSettlementAction "
                                + eex.GetType().Name + ": " + eex.Message);
                        }

                        try
                        {
                            DestroyPartyAction.Apply(null, chiefParty);
                        }
                        catch (Exception dex)
                        {
                            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                                "[BAQ.Ch2][R1][ERR] DestroyPartyAction "
                                + dex.GetType().Name + ": " + dex.Message);
                        }
                    }
                }

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.Ch2][R1] teardown OK chiefParty="
                    + (chiefParty != null ? chiefParty.StringId : "<null>"));
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ.Ch2][R1][ERR] R1TeardownIfActive "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _r1FallbackActive = false;
                _r1ChiefParty = null;
            }
        }

        // T24 — minimal Ch3 transition. Phase 6 will add the Ritual popup wrapper
        // on top; this stub keeps state + journal correct so saves taken between
        // Ch2-Win and Phase-6 deploy reload cleanly.
        private void AdvanceToCh3Ritual()
        {
            try
            {
                _chapter = 3;
                _objectiveStep = 0;
                _ch2InTrial = false;

                Hero chief = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(_chiefCultureId);
                Settlement hideout = chief != null ? chief.HomeSettlement : null;

                TextObject log = new TextObject("{=bp_alliance_ch2_to_ch3_journal}Return to {HIDEOUT} to take the oath.");
                log.SetTextVariable("HIDEOUT", hideout != null && hideout.Name != null
                    ? hideout.Name
                    : new TextObject("{=bp_alliancequest_017}the hideout"));
                AddLog(log);

                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP_ALLIANCE] AdvanceToCh3Ritual culture=" + _chiefCultureId);
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP_ALLIANCE][ERR] AdvanceToCh3Ritual " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // T28 — terminal "Sworn" branch. Posts the journal log, tears down the
        // R1 escort fallback if it was still active, unregisters this quest from
        // the behavior's active-quest pointer BEFORE CompleteQuestWithSuccess drops
        // it from the campaign pool (so the behavior doesn't hold a stale ref).
        private void CompleteSuccessfully()
        {
            try
            {
                Hero chief = BandItPlus.Heroes.BanditChiefRegistry.Instance != null
                    ? BandItPlus.Heroes.BanditChiefRegistry.Instance.GetChief(_chiefCultureId)
                    : null;
                TextObject sworn = new TextObject("{=bp_alliance_ch3_sworn_log}Sworn. {CHIEF} is your ally.");
                sworn.SetTextVariable("CHIEF", chief != null && chief.Name != null
                    ? chief.Name
                    : new TextObject(_chiefCultureId ?? "{=bp_alliancequest_001}the chief"));
                AddLog(sworn);
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP_ALLIANCE][ERR] CompleteSuccessfully AddLog "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            R1TeardownIfActive();

            try
            {
                if (BandItPlus.Behaviors.BanditAllianceBehavior.Instance != null)
                    BandItPlus.Behaviors.BanditAllianceBehavior.Instance.UnregisterActiveQuest(_chiefCultureId);
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP_ALLIANCE][ERR] CompleteSuccessfully UnregisterActiveQuest "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "[BP_ALLIANCE] CompleteSuccessfully culture=" + (_chiefCultureId ?? "<null>"));
            CompleteQuestWithSuccess();
        }

        private void FailSoft(string reason)
        {
            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "[BAQ] FailSoft culture=" + (_chiefCultureId ?? "<null>")
                + " reason=" + (reason ?? "<null>") + " chapter=" + _chapter);

            // (1) Arm 30-day cooldown on the behavior so IsEligibleForOffer returns false
            //     for this chief until the cooldown expires. Spec Section 2 Ch1 soft-fail row.
            const double THIRTY_DAYS_HOURS = 30.0 * 24.0;
            var bab = BandItPlus.Behaviors.BanditAllianceBehavior.Instance;
            if (bab != null) bab.ArmCh1Cooldown(_chiefCultureId, THIRTY_DAYS_HOURS);

            // (2) Trust ledger drops one notch — defer to BanditDialogManager which owns
            //     _cultureTrust. Wrapped in try/catch per [[csharp-empty-catch-silent-failures]].
            try
            {
                var bdm = Campaign.Current != null
                    ? Campaign.Current.GetCampaignBehavior<BandItPlus.BanditDialogManager>()
                    : null;
                if (bdm != null) bdm.AdjustCultureTrust(_chiefCultureId, -1);
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ] FailSoft AdjustCultureTrust threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            // (3) Journal breadcrumb per spec Section 2 table row "Ch1 -> Failed_Soft (timer)".
            try
            {
                AddLog(new TextObject(
                    "{=bp_alliance_ch1_soft_fail_telegraph}The offer has been withdrawn — return in 30 days to try again."));
            }
            catch (Exception logEx)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ] FailSoft AddLog threw " + logEx.GetType().Name + ": " + logEx.Message);
            }

            // (4) Unregister from behavior dispatch table BEFORE quest engine clean-up so
            //     RegisterActiveQuest's "no second offer for the same chief" gate clears.
            if (bab != null) bab.UnregisterActiveQuest(_chiefCultureId);

            // (4b) R1 fallback teardown (no-op if not engaged).
            R1TeardownIfActive();

            // (5) CompleteQuestWithCancel per spec Section 4 / Section 7 (CancelQuest doesn't
            //     exist on QuestBase in this engine version).
            CompleteQuestWithCancel();
        }

        private void FailHard(string reason)
        {
            BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                "[BAQ] FailHard culture=" + (_chiefCultureId ?? "<null>")
                + " reason=" + (reason ?? "<null>") + " chapter=" + _chapter);

            try
            {
                AddLog(new TextObject(
                    "{=bp_alliance_ch1_hard_fail_log}The arrangement collapsed before it could begin."));
            }
            catch (Exception logEx)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BAQ] FailHard AddLog threw " + logEx.GetType().Name + ": " + logEx.Message);
            }

            // Best-effort caravan despawn for Pattern D
            if (_ch1EscortCaravan != null && _ch1EscortCaravan.IsActive)
            {
                try { DestroyPartyAction.Apply(null, _ch1EscortCaravan); }
                catch (Exception ex)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BAQ] FailHard DestroyPartyAction threw "
                        + ex.GetType().Name + ": " + ex.Message);
                }
                _ch1EscortCaravan = null;
            }

            BandItPlus.Behaviors.BanditAllianceBehavior.Instance?.UnregisterActiveQuest(_chiefCultureId);

            // R1 fallback teardown (no-op if not engaged).
            R1TeardownIfActive();

            CompleteQuestWithFail();
        }
    }
}
