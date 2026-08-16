using System;
using BandItPlus.Cultures;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.LogEntries;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;
using TaleWorlds.SaveSystem;

namespace BandItPlus.Quests
{
    // Wave 4.12 (2026-05-10): compound multi-objective hard quest for the Slaver vendor.
    // Pattern follows BanditTrustQuest / BanditVendorQuest / BanditNamedTargetQuest.
    // Saveable type ID 5 registered in BandItPlusSaveableTypeDefiner.
    //
    // Bannerlord 1.2.x AND 1.3.x compatibility: subscribes only to canonical
    // CampaignEvents (HeroKilledEvent + MapEventEnded), uses HourlyTick override for
    // periodic polling. AVOIDS HeroPrisonerTaken — event signature/availability has
    // historically shifted between 1.2 and 1.3 patches; capture detection works just
    // as well via PrisonerRoster polling in HourlyTick.
    //
    // Progress tracking: one JournalLog entry per simultaneous objective, each prefixed
    // with a [KILL]/[CAPTURE]/[PRISONERS]/[MATERIALS] tag. ComputeCurrentForObjective
    // reads the entry's LogText tag to know which tracker to consult — no parallel data
    // structure needed.
    public class BanditSlaverQuest : QuestBase
    {
        [SaveableField(1)] private string _cultureId;
        [SaveableField(2)] private int _questTier;
        [SaveableField(3)] private int _killCount;
        [SaveableField(4)] private int _killTarget;
        [SaveableField(5)] private string _captureTargetHeroId;
        [SaveableField(6)] private bool _captureDone;
        [SaveableField(7)] private int _prisonersBaseline;
        [SaveableField(8)] private int _prisonersTarget;
        [SaveableField(9)] private string _materialsItemId;
        [SaveableField(10)] private int _materialsTarget;
        [SaveableField(11)] private int _rewardGold;
        [SaveableField(12)] private string _killCultureFilter;

        // Idempotent guard so we don't double-subscribe when DoRegisterEvents is called
        // both by RegisterEvents (if the contract works) AND by InitJournalEntries /
        // InitializeQuestOnGameLoad (belt-and-suspenders). NOT [SaveableField] — it's
        // runtime-only and correctly resets to false on save reload so handlers
        // re-attach via InitializeQuestOnGameLoad.
        private bool _eventsSubscribed;

        public string CultureId => _cultureId;
        public int QuestTier => _questTier;

        // Public wrappers around protected QuestBase methods so HideoutSlaverDialog
        // turn-in/cancel tokens can finalize the quest from outside the class.
        public void FinishWithSuccess() => CompleteQuestWithSuccess();
        public void FinishWithCancel() => CompleteQuestWithCancel();

        public BanditSlaverQuest(string questId, Hero questGiver, CampaignTime duration,
            int rewardGold, string cultureId, int questTier,
            int killTarget, string killCultureFilter,
            string captureTargetHeroId,
            int prisonersTarget,
            string materialsItemId, int materialsTarget)
            : base(questId, questGiver, duration, rewardGold)
        {
            _cultureId = cultureId;
            _questTier = questTier;
            _rewardGold = rewardGold;
            _killTarget = killTarget;
            _killCultureFilter = killCultureFilter;
            _captureTargetHeroId = captureTargetHeroId;
            _prisonersTarget = prisonersTarget;
            _materialsItemId = materialsItemId;
            _materialsTarget = materialsTarget;
        }

        // Wave 4.12-fix6 (2026-05-11): title now includes the slaver's authored name so
        // the player can identify WHO assigned the quest from the journal sidebar without
        // opening the entry. Falls back to "Slaver" if profile lookup fails.
        public override TextObject Title
        {
            get
            {
                string slaverName = "The Slaver";
                if (!string.IsNullOrEmpty(_cultureId)
                    && BandItPlus.Cultures.SlaverProfiles.ByCultureId.TryGetValue(_cultureId, out var p)
                    && !string.IsNullOrEmpty(p.Name))
                    slaverName = p.Name;
                return new TextObject("{=bp_hcs_017}{SLAVER}'s Work — Tier {TIER}")
                    .SetTextVariable("SLAVER", slaverName)
                    .SetTextVariable("TIER", _questTier);
            }
        }
        public override bool IsRemainingTimeHidden => true;

        // Wave 4.12-fix6 (2026-05-11): BLUE icon (issue-style), not gold (story-style).
        // User wants slaver quests visually marked as side-issues rather than apex
        // story content. Empty SpecialQuestType → IsSpecialQuest=false → blue icon.
        public override string SpecialQuestType => "";

        // ---------- Event wiring ----------

        protected override void RegisterEvents()
        {
            // 2026-06-04: vanilla QuestBase calls this on quest creation AND on game load.
            // Belt-and-suspenders: also called from InitJournalEntries (post-OnQuestStarted)
            // and InitializeQuestOnGameLoad. The internal helper guards against
            // double-registration via _eventsSubscribed.
            DoRegisterEvents("RegisterEvents");
        }

        private void DoRegisterEvents(string caller)
        {
            int step = 0;
            try
            {
                if (_eventsSubscribed)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditSlaverQuest.DoRegisterEvents(" + caller + "): already subscribed, skipping");
                    return;
                }
                // Only canonical events — works in both Bannerlord 1.2 and 1.3.
                step = 1; CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, OnHeroKilled);
                step = 2; CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
                step = 99;
                _eventsSubscribed = true;
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditSlaverQuest.DoRegisterEvents(" + caller + "): ALL 2 subscribed OK (culture='"
                    + (_cultureId ?? "<null>") + "' tier=" + _questTier + ")");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditSlaverQuest.DoRegisterEvents(" + caller + ") FAIL at step=" + step
                    + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Override Bannerlord's framework HourlyTick. Called once per in-game hour for
        // this quest instance — primary path for progress polling.
        protected override void HourlyTick()
        {
            try
            {
                if (!this.IsOngoing) return;
                UpdateProgress();
                if (AllObjectivesMet()) CompleteQuestWithSuccess();
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditSlaverQuest.HourlyTick EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnHeroKilled(Hero victim, Hero killer, KillCharacterAction.KillCharacterActionDetail detail, bool showNotification)
        {
            try
            {
                if (!this.IsOngoing) return;
                if (victim == null) return;

                // If victim is the capture target: quest fails (you killed who you should have captured).
                if (!string.IsNullOrEmpty(_captureTargetHeroId) && victim.StringId == _captureTargetHeroId)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditSlaverQuest: capture target '" + _captureTargetHeroId + "' died — cancelling quest");
                    CompleteQuestWithCancel();
                    return;
                }

                // Otherwise, count toward the kill objective when killer is player AND culture filter matches.
                if (_killTarget > 0 && killer == Hero.MainHero && CultureFilterMatches(victim))
                {
                    _killCount++;
                    UpdateProgress();
                    if (AllObjectivesMet()) CompleteQuestWithSuccess();
                }
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditSlaverQuest.OnHeroKilled EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            // Defensive resync after combat — prisoners and materials may have changed.
            try
            {
                if (!this.IsOngoing) return;
                UpdateProgress();
                if (AllObjectivesMet()) CompleteQuestWithSuccess();
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditSlaverQuest.OnMapEventEnded EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------- Progress tracking ----------

        private void UpdateProgress()
        {
            // Poll-based capture detection (works in both 1.2 and 1.3 — no event subscription needed).
            if (!_captureDone && !string.IsNullOrEmpty(_captureTargetHeroId))
            {
                var roster = MobileParty.MainParty?.PrisonRoster;
                if (roster != null)
                {
                    for (int i = 0; i < roster.Count; i++)
                    {
                        var ch = roster.GetCharacterAtIndex(i);
                        if (ch?.HeroObject?.StringId == _captureTargetHeroId)
                        {
                            _captureDone = true;
                            break;
                        }
                    }
                }
            }

            var entries = JournalEntries;
            if (entries == null) return;
            foreach (var entry in entries)
            {
                if (entry == null || entry.Range <= 0) continue;
                int current = ComputeCurrentForObjective(entry);
                entry.UpdateCurrentProgress(current);
            }
        }

        private int ComputeCurrentForObjective(JournalLog entry)
        {
            string txt = entry.LogText?.ToString() ?? "";
            if (txt.StartsWith("[KILL]")) return _killCount;
            if (txt.StartsWith("[CAPTURE]")) return _captureDone ? 1 : 0;
            if (txt.StartsWith("[PRISONERS]"))
            {
                int now = MobileParty.MainParty?.PrisonRoster?.TotalManCount ?? 0;
                return Math.Max(0, now - _prisonersBaseline);
            }
            if (txt.StartsWith("[MATERIALS]") && !string.IsNullOrEmpty(_materialsItemId))
            {
                var item = MBObjectManager.Instance.GetObject<TaleWorlds.Core.ItemObject>(_materialsItemId);
                if (item == null) return 0;
                return MobileParty.MainParty?.ItemRoster?.GetItemNumber(item) ?? 0;
            }
            return 0;
        }

        private bool AllObjectivesMet()
        {
            var entries = JournalEntries;
            if (entries == null) return false;
            bool hasAnyDiscrete = false;
            foreach (var entry in entries)
            {
                if (entry == null || entry.Range <= 0) continue;
                hasAnyDiscrete = true;
                if (entry.CurrentProgress < entry.Range) return false;
            }
            return hasAnyDiscrete;  // require at least one discrete tracker present
        }

        // Wave 4.12-fix7: small string helper for fallback culture-label rendering when
        // MBObjectManager doesn't resolve a BasicCultureObject (modded/unknown filters).
        private static string CapitalizeFirst(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0]) + s.Substring(1);
        }

        private bool CultureFilterMatches(Hero victim)
        {
            if (string.IsNullOrEmpty(_killCultureFilter) || _killCultureFilter == "*") return true;
            return victim?.Culture?.StringId == _killCultureFilter;
        }

        private void ForceCompleteJournalProgress()
        {
            var entries = JournalEntries;
            if (entries == null) return;
            foreach (var entry in entries)
            {
                if (entry == null || entry.Range <= 0) continue;
                entry.UpdateCurrentProgress(entry.Range);
            }
        }

        // ---------- Lifecycle hooks ----------

        protected override void OnCompleteWithSuccess()
        {
            try
            {
                ForceCompleteJournalProgress();
                if (Hero.MainHero != null && _rewardGold > 0)
                    GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, _rewardGold, false);

                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                bdm?.IncrementSlaverTrust(_cultureId);
                bdm?.SetActiveSlaverQuestTier(_cultureId, 0);

                // Relation +5 with the slaver Hero (Phase 7.1 will populate the registry;
                // for now this may be null, in which case relation is silently skipped).
                var slaverHero = BandItPlus.Heroes.SlaverHeroRegistry.Instance?.GetSlaver(_cultureId);
                if (slaverHero != null && Hero.MainHero != null)
                    ChangeRelationAction.ApplyPlayerRelation(slaverHero, +5, true, true);

                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditSlaverQuest.OnCompleteWithSuccess: tier " + _questTier + " for " + _cultureId
                    + " — +" + _rewardGold + " denars, +5 relation");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditSlaverQuest.OnCompleteWithSuccess EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        protected override void OnFinalize() { }
        protected override void OnTimedOut() { }

        protected override void InitializeQuestOnGameLoad()
        {
            // Re-render journal on save reload so the bar displays current values immediately.
            try { UpdateProgress(); }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditSlaverQuest.InitializeQuestOnGameLoad EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
            DoRegisterEvents("InitializeQuestOnGameLoad");
        }

        protected override void SetDialogs() { /* dialog tokens live in HideoutSlaverDialog */ }

        // ---------- Journal entries ----------

        public void InitJournalEntries()
        {
            try
            {
                // Wave 4.12-fix6 (2026-05-11): description now identifies WHO (slaver
                // name) and WHERE (culture/hideout). Falls back gracefully if profile
                // lookup misses (modded culture / corrupted save).
                string slaverName = "The slaver";
                string cultureLabel = _cultureId ?? "the camp";
                if (!string.IsNullOrEmpty(_cultureId)
                    && BandItPlus.Cultures.SlaverProfiles.ByCultureId.TryGetValue(_cultureId, out var profile))
                {
                    if (!string.IsNullOrEmpty(profile.Name)) slaverName = profile.Name;
                    if (!string.IsNullOrEmpty(profile.Title)) cultureLabel = profile.Title + " camp";
                }

                // Description log first (no Range; rendered as flavor body).
                // 2026-06-05 Wave 4.13: prose lift — atmospheric ledger-keeper voice replaces
                // the prior dry two-sentence brief. Keeps slaverName/cultureLabel substitution
                // for cross-culture coverage; the rest is mood. Per user feedback the journal
                // text should READ like the slaver speaks — heavy, calm, transactional.
                AddLog(new TextObject("{=bp_hcs_018}{SLAVER} at the {CULTURE_LABEL} keeps a ledger in his head — "
                    + "names to be ended, bodies to be brought, hands to be shown. He sets the work "
                    + "in front of you with the calm of a man who has counted enough debts to know "
                    + "exactly how they're paid. "
                    + "Every column gets filled, or none of them do. The flesh-trade rewards the "
                    + "thorough, and {SLAVER} has a long memory for those who leave a "
                    + "tally short. "
                    + "Come back when the slate is clean. He pays in old coin, in the right kind "
                    + "of company, and in a door that opens a little wider each time you finish "
                    + "what he asks.")
                    .SetTextVariable("SLAVER", slaverName)
                    .SetTextVariable("CULTURE_LABEL", cultureLabel));

                // Wave 4.12-fix7 (2026-05-11): objective descriptions now compose
                // culture/target/item names dynamically so the journal reads concretely
                // (e.g. "Slay 3 Sturgian lords or notables" instead of "Slay marked
                // targets"). No new SaveableFields — all data is already stored.

                if (_killTarget > 0)
                {
                    string killCultureLabel = "marked";
                    if (!string.IsNullOrEmpty(_killCultureFilter) && _killCultureFilter != "*")
                    {
                        var cultureObj = MBObjectManager.Instance.GetObject<TaleWorlds.Core.BasicCultureObject>(_killCultureFilter);
                        killCultureLabel = cultureObj?.Name?.ToString() ?? CapitalizeFirst(_killCultureFilter);
                    }
                    AddDiscreteLog(new TextObject("[KILL] " + new TextObject("{=bp_hcs_019}Put {KILL_N} {KILL_CULTURE} "
                        + "lords or notables in the ground for good. Battles fade and the wounded "
                        + "walk again — only an execution settles the mark. Defeat them, take them, "
                        + "and finish them yourself before the dust clears.")
                        .SetTextVariable("KILL_N", _killTarget)
                        .SetTextVariable("KILL_CULTURE", killCultureLabel).ToString()),
                        new TextObject("{=bp_slaverquest_001}Kills"), 0, _killTarget);
                }

                if (!string.IsNullOrEmpty(_captureTargetHeroId))
                {
                    var captureHero = Hero.Find(_captureTargetHeroId);
                    string captureName = captureHero?.Name?.ToString() ?? "the marked hero";
                    string captureWhere = captureHero?.HomeSettlement?.Name?.ToString();
                    string captureText = new TextObject("{=bp_hcs_020}Bring {CAPTURE_NAME} back alive. "
                        + "Wound, take, hold — but never let the killing-blow land. "
                        + "{SLAVER} wants this one breathing. A corpse pays nothing, and the "
                        + "ledger gets crossed out in red.")
                        .SetTextVariable("CAPTURE_NAME", captureName)
                        .SetTextVariable("SLAVER", slaverName).ToString();
                    if (!string.IsNullOrEmpty(captureWhere))
                        captureText += " " + new TextObject("{=bp_hcs_021}Last sighted near {WHERE}.")
                            .SetTextVariable("WHERE", captureWhere).ToString();
                    AddDiscreteLog(new TextObject("[CAPTURE] " + captureText),
                        new TextObject("{=bp_slaverquest_002}Captured"), 0, 1);
                }

                if (_prisonersTarget > 0)
                {
                    _prisonersBaseline = MobileParty.MainParty?.PrisonRoster?.TotalManCount ?? 0;
                    string prisonerSource = "any";
                    if (!string.IsNullOrEmpty(_killCultureFilter) && _killCultureFilter != "*")
                    {
                        var cultureObj = MBObjectManager.Instance.GetObject<TaleWorlds.Core.BasicCultureObject>(_killCultureFilter);
                        prisonerSource = cultureObj?.Name?.ToString() ?? CapitalizeFirst(_killCultureFilter);
                    }
                    AddDiscreteLog(new TextObject("[PRISONERS] " + new TextObject("{=bp_hcs_022}Walk back into the camp with "
                        + "{PRISONERS_N} prisoners on the rope behind you ({PRISONER_SOURCE}"
                        + " blood preferred — the trade pays better for marked stock). "
                        + "Defeat the enemy, choose 'Take Prisoner', and hold them. No ransoms, no "
                        + "manumissions, no clean-handed releases. The line goes back with the train, "
                        + "and the count is the count.")
                        .SetTextVariable("PRISONERS_N", _prisonersTarget)
                        .SetTextVariable("PRISONER_SOURCE", prisonerSource).ToString()),
                        new TextObject("{=bp_slaverquest_003}Prisoners"), 0, _prisonersTarget);
                }

                if (!string.IsNullOrEmpty(_materialsItemId) && _materialsTarget > 0)
                {
                    var item = MBObjectManager.Instance.GetObject<TaleWorlds.Core.ItemObject>(_materialsItemId);
                    string itemName = item?.Name?.ToString() ?? _materialsItemId;
                    AddDiscreteLog(new TextObject("[MATERIALS] " + new TextObject("{=bp_hcs_023}Bring {MATERIALS_N}× "
                        + "{ITEM_NAME} to the camp. {SLAVER} needs the stock — chains, "
                        + "yokes, cages, salt for the long road. The flesh-trade has a back-room "
                        + "of its own, and it runs on the dull things that nobody sings songs about. "
                        + "Buy them, take them, or harvest them — the count is what matters.")
                        .SetTextVariable("MATERIALS_N", _materialsTarget)
                        .SetTextVariable("ITEM_NAME", itemName)
                        .SetTextVariable("SLAVER", slaverName).ToString()),
                        new TextObject("{=bp_slaverquest_004}Gathered"), 0, _materialsTarget);
                }
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditSlaverQuest.InitJournalEntries EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
            DoRegisterEvents("InitJournalEntries");
        }
    }
}
