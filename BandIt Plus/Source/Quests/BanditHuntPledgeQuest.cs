using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Localization;
using TaleWorlds.SaveSystem;

namespace BandItPlus.Quests
{
    // Wave 4.25.0 (2026-06-12): the parley HUNT PLEDGE as a real journal quest.
    //
    // User: "i want pludge the hunt has run as a quest" — and the same session
    // exposed WHY: "witch i did and it didnt do nothing". The pledge lived only
    // as a yellow chat line + hidden CSV state, so the player had no objective
    // text, no countdown, and no feedback when a kill didn't qualify.
    //
    // Division of labour:
    // - BanditOriginBehavior KEEPS the mechanics (_huntTargetCulture CSV state,
    //   kill detection in OnMobilePartyDestroyedHunt, hourly expiry). That state
    //   is the save-compatible source of truth.
    // - This quest is the JOURNAL PRESENTATION: objective text that spells out
    //   "rival" and "war band", the 7-day countdown, success/fail closure logs.
    //   The behavior drives it via Begin / FindActive / ConcludeFulfilled /
    //   ConcludeLapsed. If the quest ever fails to start (old save mid-pledge),
    //   the CSV mechanics still work — the journal entry is simply absent.
    //
    // QuestGiver = the pledged culture's chief (real Hero from BanditChiefRegistry).
    // Saveable type ID = 8 in BandItPlusSaveableTypeDefiner.
    public class BanditHuntPledgeQuest : QuestBase
    {
        [SaveableField(1)]
        private string _cultureId;

        public string CultureId => _cultureId;

        public BanditHuntPledgeQuest(string questId, Hero questGiver, CampaignTime duration,
            int rewardGold, string cultureId)
            : base(questId, questGiver, duration, rewardGold)
        {
            _cultureId = cultureId;
        }

        public override TextObject Title
        {
            get
            {
                string culture = BandItPlus.Origins.BanditOriginBehavior.CultureDisplay(_cultureId);
                return new TextObject("{=bp_hcq_001}The Pledge of Blood — {CULTURE}")
                    .SetTextVariable("CULTURE", culture);
            }
        }

        // The whole point of the quest conversion: show the 7-day countdown.
        public override bool IsRemainingTimeHidden => false;

        // Story-class quest — gold journal icon, not blue issue.
        public override string SpecialQuestType => "BanditHuntPledgeQuest";

        // Kill detection stays in BanditOriginBehavior (it owns GrantPeace and the
        // culture roster); this quest subscribes to nothing.
        protected override void RegisterEvents() { }
        protected override void SetDialogs() { }
        protected override void InitializeQuestOnGameLoad() { }
        protected override void OnFinalize() { }

        protected override void OnTimedOut()
        {
            // QuestBase's own timer can beat the behavior's hourly lapse check to the
            // same in-game hour. Close the journal here; the CSV state is cleared by
            // BanditOriginBehavior's check, which skips its quest call once this
            // instance stops being IsOngoing. Both paths are idempotent.
            try
            {
                AddLog(new TextObject(
                    "{=bp_hcq_002}Seven days came and went with no rival blood spilled. The pledge is void — the {CULTURE} owe you nothing.")
                    .SetTextVariable("CULTURE", BandItPlus.Origins.BanditOriginBehavior.CultureDisplay(_cultureId)));
            }
            catch { }
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                "BanditHuntPledgeQuest.OnTimedOut: " + (_cultureId ?? "<null>"));
        }

        // Called right after QuestManager.OnQuestStarted — same two-step start
        // sequence every BP quest uses (see HideoutSlaverDialog.OnAcceptSlaverQuest).
        public void InitJournalEntries()
        {
            try
            {
                string culture = BandItPlus.Origins.BanditOriginBehavior.CultureDisplay(_cultureId);
                string chief = QuestGiver?.Name?.ToString() ?? "The chief";
                AddLog(new TextObject(
                    "{=bp_hcq_003}{CHIEF} of the {CULTURE} has named the price of peace: blood for blood. "
                    + "Within seven days, destroy any RIVAL bandit war band — one that does NOT ride "
                    + "under the {CULTURE} banner. Looters, raiders, any other brotherhood will "
                    + "do, but the killing blow must be your party's own. Small bands of fewer than "
                    + "eight do not count. Succeed, and the {CULTURE} grant you their peace; "
                    + "fail, and the pledge is void.")
                    .SetTextVariable("CHIEF", chief)
                    .SetTextVariable("CULTURE", culture));
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditHuntPledgeQuest.InitJournalEntries EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ── behavior-driven closure ──

        public void ConcludeFulfilled(string killedCultureDisplay)
        {
            try
            {
                AddLog(new TextObject(
                    "{=bp_hcq_004}You struck down a {KILLED} war band. The pledge is paid in blood — the {CULTURE} grant you their peace.")
                    .SetTextVariable("KILLED", killedCultureDisplay ?? "rival")
                    .SetTextVariable("CULTURE", BandItPlus.Origins.BanditOriginBehavior.CultureDisplay(_cultureId)));
            }
            catch { }
            CompleteQuestWithSuccess();
        }

        public void ConcludeLapsed()
        {
            try
            {
                AddLog(new TextObject(
                    "{=bp_hcq_005}The seven days ran out. The pledge to the {CULTURE} is void.")
                    .SetTextVariable("CULTURE", BandItPlus.Origins.BanditOriginBehavior.CultureDisplay(_cultureId)));
            }
            catch { }
            CompleteQuestWithFail();
        }

        // ── statics for BanditOriginBehavior ──

        public static BanditHuntPledgeQuest FindActive(string cultureId)
        {
            try
            {
                var quests = Campaign.Current?.QuestManager?.Quests;
                if (quests == null) return null;
                foreach (var q in quests)
                    if (q is BanditHuntPledgeQuest hq && hq.IsOngoing && hq._cultureId == cultureId)
                        return hq;
            }
            catch { }
            return null;
        }

        // expiresAtHours is the ABSOLUTE CampaignTime.ToHours the pledge lapses —
        // BanditOriginBehavior._huntExpiresHours. The QuestBase display timer is
        // built from the REMAINING time so the journal countdown matches the saved
        // CSV expiry exactly, whether this is a fresh pledge or a re-hydration after
        // load. (Using a flat 168h would reset the timer on every reload.)
        public static void Begin(string cultureId, Hero chief, double expiresAtHours)
        {
            try
            {
                if (FindActive(cultureId) != null) return; // already present (persisted or re-hydrated)
                double remaining = expiresAtHours - CampaignTime.Now.ToHours;
                if (remaining <= 0.0) return; // already lapsed — the hourly tick clears the CSV
                var questId = "bp_huntpledge_" + cultureId + "_" + (long)CampaignTime.Now.ToHours;
                var quest = new BanditHuntPledgeQuest(
                    questId, chief, CampaignTime.HoursFromNow((float)remaining), 0, cultureId);
                Campaign.Current?.QuestManager?.OnQuestStarted(quest);
                quest.InitJournalEntries();
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BP-Origin] hunt pledge QUEST started for " + cultureId
                    + " (giver='" + (chief?.Name?.ToString() ?? "<null>") + "', remaining="
                    + remaining.ToString("F1") + "h)");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditHuntPledgeQuest.Begin EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
