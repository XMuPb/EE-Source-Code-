using System;
using BandItPlus.Cultures;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;
using TaleWorlds.SaveSystem;

namespace BandItPlus.Quests
{
    // 2026-06-03 story-expansion: Tier-3 raid-village quest, mirrors BanditNamedTargetQuest's
    // two-phase pattern. Player accepts → village picked from chief's enemy lords → blue
    // map marker → player raids village (state becomes Looted) → interim "return to chief"
    // log → player returns + picks dialog → reward + per-chief closure prose.
    public class BanditRaidVillageQuest : QuestBase
    {
        [SaveableField(1)] private string _cultureId;
        [SaveableField(2)] private string _targetVillageId;
        [SaveableField(3)] private string _targetLordId;
        [SaveableField(4)] private int _rewardGold;
        // Detection: village reached Looted state. Mirrors _killedByOtherCause/_killAwaitingChiefReturn.
        [SaveableField(5)] private bool _raidedByOtherCause;
        [SaveableField(6)] private bool _raidAwaitingChiefReturn;
        // 2026-06-03 edge-case handling: another lord raided the village before the player.
        // Quest does NOT auto-close — village recovers in a few days, player can still raid
        // for full bounty. This flag prevents spamming the "another's torch" log every hour.
        [SaveableField(7)] private bool _otherCauseRaidNotified;
        // Lord-died log (one-shot). Quest stays open since most chief grudges are clan-level.
        [SaveableField(8)] private bool _targetLordDeathNotified;
        // Owner-clan flip detection — cached at quest start. If changes, quest cancels.
        [SaveableField(9)] private string _originalOwnerClanId;

        public string CultureId => _cultureId;
        public string TargetVillageId => _targetVillageId;
        public string TargetLordId => _targetLordId;
        public int RewardGoldAmount => _rewardGold;
        public bool IsAwaitingChiefReturn => _raidAwaitingChiefReturn;
        public bool RaidedByOtherCause => _raidedByOtherCause;

        public Settlement TargetVillage
        {
            get
            {
                if (string.IsNullOrEmpty(_targetVillageId)) return null;
                try { return MBObjectManager.Instance?.GetObject<Settlement>(_targetVillageId); }
                catch { return null; }
            }
        }

        public Hero TargetLord
        {
            get
            {
                if (string.IsNullOrEmpty(_targetLordId)) return null;
                try { return Hero.Find(_targetLordId); }
                catch { return null; }
            }
        }

        public BanditRaidVillageQuest(string questId, Hero questGiver, CampaignTime duration, int rewardGold,
            string cultureId, string targetVillageId, string targetLordId)
            : base(questId, questGiver, duration, rewardGold)
        {
            _cultureId = cultureId;
            _targetVillageId = targetVillageId;
            _targetLordId = targetLordId;
            _rewardGold = rewardGold;
        }

        public override TextObject Title
        {
            get
            {
                string vname = TargetVillage?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_001", "the marked village");
                string cname = QuestGiver?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_002", "the chief");
                return new TextObject("{=bp_hcq_012}Raid {VILLAGE} for {CHIEF}")
                    .SetTextVariable("VILLAGE", vname)
                    .SetTextVariable("CHIEF", cname);
            }
        }

        public override bool IsRemainingTimeHidden => true;
        public override string SpecialQuestType => "BanditRaidVillageQuest";

        // Public wrapper for the protected CompleteQuestWithSuccess so the dialog
        // consequence in BanditDialogManager.OnCollectRaidVillageBounty can trigger
        // completion. Mirrors the BanditNamedTargetQuest pattern.
        public void FinishWithSuccess() => CompleteQuestWithSuccess();

        protected override void RegisterEvents()
        {
            // 2026-06-04: vanilla QuestBase calls this on quest creation AND on game load.
            // Previously this override wasn't logging anything which suggested it wasn't
            // being called — but we now also call DoRegisterEvents() explicitly from
            // InitJournalEntries() as a belt-and-suspenders measure. The internal helper
            // guards against double-registration.
            DoRegisterEvents("RegisterEvents");
        }

        // Idempotent guard so we don't double-subscribe when DoRegisterEvents is called
        // both by RegisterEvents (if the contract works) AND by InitJournalEntries (belt).
        private bool _eventsSubscribed;

        private void DoRegisterEvents(string caller)
        {
            int step = 0;
            try
            {
                if (_eventsSubscribed)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditRaidVillageQuest.DoRegisterEvents(" + caller + "): already subscribed, skipping");
                    return;
                }
                step = 1; CampaignEvents.SettlementEntered.AddNonSerializedListener(this, OnSettlementEntered);
                step = 2; CampaignEvents.HourlyTickSettlementEvent.AddNonSerializedListener(this, OnSettlementHourlyTick);
                step = 3; CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
                step = 4; CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, OnSettlementOwnerChanged);
                step = 5; CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, OnHeroKilled);
                step = 6; CampaignEvents.VillageBeingRaided.AddNonSerializedListener(this, OnVillageBeingRaided);
                // 2026-06-04 fix: VillageBeingRaided fires at raid START (state=BeingRaided)
                // and never with state=Looted — 63 log entries proved this. VillageStateChanged
                // is the lower-level primitive that fires on EVERY state transition including
                // the BeingRaided -> Looted final tick, with the raider party as the 4th arg.
                // Stable across 1.3.x AND 1.4.x. OnVillageBeingRaided + hourly-tick stay as
                // defense-in-depth fallbacks.
                step = 7; CampaignEvents.VillageStateChanged.AddNonSerializedListener(this, OnVillageStateChanged);
                step = 99;
                _eventsSubscribed = true;
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditRaidVillageQuest.DoRegisterEvents(" + caller + "): ALL 7 subscribed OK (targetVillage='"
                    + (_targetVillageId ?? "<null>") + "' culture='" + (_cultureId ?? "<null>") + "')");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditRaidVillageQuest.DoRegisterEvents(" + caller + ") FAIL at step=" + step
                    + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Primary raid detection path. Per BanditAllianceQuest Pattern C: VillageBeingRaided
        // fires multiple times during a raid (start, each loot-tick, completion). We only
        // act on the final transition to Looted. Settlement.LastAttackerParty identifies
        // the raider so we can distinguish player-raid from other-cause.
        private void OnVillageBeingRaided(Village village)
        {
            try
            {
                // 2026-06-04 diagnostic: log EVERY entry so we know if event fires at all
                // and which guard rejects it. Previously the early-returns were silent.
                string vId = village?.Settlement?.StringId ?? "<null>";
                string vState = village?.VillageState.ToString() ?? "<null>";
                bool ongoing = this.IsOngoing;
                bool targetMatch = !string.IsNullOrEmpty(_targetVillageId) && vId == _targetVillageId;
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "RaidVillage diag VillageBeingRaided: village='" + vId + "' state=" + vState
                    + " ongoing=" + ongoing + " targetVillage='" + (_targetVillageId ?? "<null>")
                    + "' targetMatch=" + targetMatch + " awaiting=" + _raidAwaitingChiefReturn);

                if (!ongoing) return;
                if (village == null || village.Settlement == null) return;
                if (string.IsNullOrEmpty(_targetVillageId)) return;
                if (!targetMatch) return;
                if (_raidAwaitingChiefReturn) return;
                if (village.VillageState != Village.VillageStates.Looted) return;

                bool playerRaid = false;
                MobileParty lastAttacker = null;
                try
                {
                    lastAttacker = village.Settlement.LastAttackerParty;
                    if (lastAttacker != null && Hero.MainHero != null)
                    {
                        if (lastAttacker == MobileParty.MainParty) playerRaid = true;
                        else if (lastAttacker.LeaderHero == Hero.MainHero) playerRaid = true;
                    }
                }
                catch { }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "RaidVillage VillageBeingRaided LOOTED-TRANSITION for '" + _targetVillageId
                    + "' lastAttacker='" + (lastAttacker?.Name?.ToString() ?? "<null>")
                    + "' playerRaid=" + playerRaid);
                TriggerRaidPending(playerRaid, source: "village-being-raided");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "RaidVillage OnVillageBeingRaided fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // 2026-06-04 primary raid-completion detection (replaces VillageBeingRaided's broken
        // Looted-transition path). CampaignEvents.VillageStateChanged fires on EVERY village
        // state transition including BeingRaided -> Looted. The 4th argument is the raider
        // MobileParty, so player-vs-other-cause is unambiguous without LastAttackerParty
        // reflection. Stable across game v1.3.x and v1.4.x War Sails.
        private void OnVillageStateChanged(Village village, Village.VillageStates oldState, Village.VillageStates newState, MobileParty raiderParty)
        {
            try
            {
                string vId = village?.Settlement?.StringId ?? "<null>";
                bool targetMatch = !string.IsNullOrEmpty(_targetVillageId) && vId == _targetVillageId;
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "RaidVillage diag VillageStateChanged: village='" + vId + "' " + oldState + "->" + newState
                    + " raider='" + (raiderParty?.Name?.ToString() ?? "<null>")
                    + "' targetMatch=" + targetMatch + " ongoing=" + this.IsOngoing
                    + " awaiting=" + _raidAwaitingChiefReturn);

                if (!this.IsOngoing) return;
                if (!targetMatch) return;
                if (_raidAwaitingChiefReturn) return;
                if (newState != Village.VillageStates.Looted) return;

                bool playerRaid = false;
                if (raiderParty != null && Hero.MainHero != null)
                {
                    if (raiderParty == MobileParty.MainParty) playerRaid = true;
                    else if (raiderParty.LeaderHero == Hero.MainHero) playerRaid = true;
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "RaidVillage VillageStateChanged LOOTED-TRANSITION for '" + _targetVillageId
                    + "' raider='" + (raiderParty?.Name?.ToString() ?? "<null>")
                    + "' playerRaid=" + playerRaid);
                TriggerRaidPending(playerRaid, source: "village-state-changed");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "RaidVillage OnVillageStateChanged fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Owner-clan flip: a siege/conquest moved the village to a different clan.
        // The {LORD} in the chief's grudge no longer holds it. Quest cancels with a
        // finder's-fee consolation (500g, no relation hit), and a per-quest log.
        private void OnSettlementOwnerChanged(Settlement settlement, bool openToClaim, Hero newOwner, Hero oldOwner, Hero capturerHero, ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail detail)
        {
            try
            {
                if (!this.IsOngoing) return;
                if (settlement == null || string.IsNullOrEmpty(_targetVillageId)) return;
                if (settlement.StringId != _targetVillageId) return;

                // Compare clan IDs — same clan keeps the quest valid (lord-to-heir transition).
                // Different clan = grudge no longer hits the originally-named lord's house.
                string newClanId = newOwner?.Clan?.StringId;
                if (!string.IsNullOrEmpty(_originalOwnerClanId)
                    && !string.IsNullOrEmpty(newClanId)
                    && _originalOwnerClanId == newClanId)
                {
                    return;       // same clan still owns it
                }

                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditRaidVillageQuest: village '" + _targetVillageId + "' owner-clan flipped — cancelling quest");

                string vName = settlement.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_001", "the marked village");
                string lordName = TargetLord?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_003", "the marked lord");
                string chiefName = QuestGiver?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_002", "the chief");
                AddLog(new TextObject(
                    "{=bp_hcq_013}{VILLAGE} is no longer held by {LORD}'s clan. {CHIEF}'s grudge has passed elsewhere — the contract no longer matches the target. 500 denars arrive by courier as a finder's fee for the intelligence already delivered.")
                    .SetTextVariable("VILLAGE", vName)
                    .SetTextVariable("LORD", lordName)
                    .SetTextVariable("CHIEF", chiefName));

                if (Hero.MainHero != null)
                    GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, 500, false);
                // Cancel without success/failure overhead. CompleteQuestWithCancel + return.
                CompleteQuestWithCancel();
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "RaidVillage OnSettlementOwnerChanged fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Target lord dies — quest stays open since most chief grudges are clan-level.
        // One-shot informational log so the player understands the village still owes.
        private void OnHeroKilled(Hero victim, Hero killer, KillCharacterAction.KillCharacterActionDetail detail, bool showNotification)
        {
            try
            {
                if (!this.IsOngoing) return;
                if (victim == null || string.IsNullOrEmpty(_targetLordId)) return;
                if (victim.StringId != _targetLordId) return;
                if (_targetLordDeathNotified) return;
                _targetLordDeathNotified = true;

                string lordName = victim.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_003", "the marked lord");
                string vName = TargetVillage?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_001", "the marked village");
                string chiefName = QuestGiver?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_002", "the chief");
                AddLog(new TextObject(
                    "{=bp_hcq_014}{LORD} is dead. {CHIEF}'s grudge against the clan stands - {VILLAGE} still owes the same debt, and burning it still answers the same call.")
                    .SetTextVariable("LORD", lordName)
                    .SetTextVariable("CHIEF", chiefName)
                    .SetTextVariable("VILLAGE", vName));
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditRaidVillageQuest: target lord " + _targetLordId + " killed — quest stays open (clan-grudge)");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "RaidVillage OnHeroKilled fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnSettlementHourlyTick(Settlement settlement)
        {
            try
            {
                if (!this.IsOngoing) return;
                if (settlement == null || settlement.Village == null) return;
                if (string.IsNullOrEmpty(_targetVillageId)) return;
                if (settlement.StringId != _targetVillageId) return;
                if (_raidAwaitingChiefReturn) return;
                // Reset the other-cause notification flag when the village recovers, so
                // a SECOND other-lord raid later (in the same quest's lifetime) re-fires the
                // log. Player-raid path is unaffected by this flag.
                if (settlement.Village.VillageState != Village.VillageStates.Looted)
                {
                    _otherCauseRaidNotified = false;
                    return;
                }
                if (settlement.Village.VillageState == Village.VillageStates.Looted)
                {
                    bool playerRaid = false;
                    try
                    {
                        var lastAttacker = settlement.LastAttackerParty;
                        if (lastAttacker != null && Hero.MainHero != null)
                        {
                            if (lastAttacker == MobileParty.MainParty) playerRaid = true;
                            else if (lastAttacker.LeaderHero == Hero.MainHero) playerRaid = true;
                        }
                    }
                    catch { }
                    TriggerRaidPending(playerRaid, source: "hourly-tick");
                }
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "RaidVillage OnSettlementHourlyTick fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnSettlementEntered(MobileParty mobileParty, Settlement settlement, Hero hero)
        {
            try
            {
                if (!this.IsOngoing) return;
                if (_raidAwaitingChiefReturn) return;
                if (settlement == null || settlement.Village == null) return;
                if (settlement.StringId != _targetVillageId) return;
                if (mobileParty != MobileParty.MainParty) return;
                if (settlement.Village.VillageState == Village.VillageStates.Looted)
                {
                    TriggerRaidPending(playerRaid: true, source: "settlement-entered");
                }
            }
            catch { }
        }

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            try
            {
                if (!this.IsOngoing) return;
                if (_raidAwaitingChiefReturn) return;
                var v = TargetVillage;
                if (v == null || v.Village == null) return;
                if (v.Village.VillageState == Village.VillageStates.Looted)
                {
                    bool playerRaid = false;
                    try
                    {
                        if (mapEvent != null && Hero.MainHero != null && mapEvent.InvolvedParties != null)
                        {
                            foreach (var p in mapEvent.InvolvedParties)
                            {
                                if (p != null && p.MobileParty == MobileParty.MainParty) { playerRaid = true; break; }
                            }
                        }
                    }
                    catch { }
                    TriggerRaidPending(playerRaid, source: "mapevent");
                }
            }
            catch { }
        }

        private void TriggerRaidPending(bool playerRaid, string source)
        {
            try
            {
                // 2026-06-03 edge-case: other-cause raids no longer auto-close the quest.
                // The village recovers in a few in-game days; the player can still raid it
                // themselves for full bounty. Just log once and keep the quest open.
                if (!playerRaid)
                {
                    if (_otherCauseRaidNotified) return;
                    _otherCauseRaidNotified = true;
                    string vName0 = TargetVillage?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_001", "the marked village");
                    string chiefName0 = QuestGiver?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_002", "the chief");
                    AddLog(new TextObject(
                        "{=bp_hcq_015}Another's torch reached {VILLAGE} first. The village will recover in time. {CHIEF}'s contract stands - burn it yourself when it is whole again, and the full bounty remains yours.")
                        .SetTextVariable("VILLAGE", vName0)
                        .SetTextVariable("CHIEF", chiefName0));
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditRaidVillageQuest: village '" + _targetVillageId + "' Looted by other-cause ("
                        + source + ") — quest stays open for player retry after recovery");
                    return;
                }

                if (_raidAwaitingChiefReturn) return;
                _raidedByOtherCause = false;  // playerRaid is true here
                _raidAwaitingChiefReturn = true;
                ForceCompleteJournalProgress();
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditRaidVillageQuest: village '" + _targetVillageId + "' Looted (" + source
                    + ") — playerRaid=true — awaiting chief return");
                string vName = TargetVillage?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_001", "the marked village");
                string chiefName = QuestGiver?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_002", "the chief");
                string hideoutName = QuestGiver?.HomeSettlement?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_004", "the hideout");
                AddLog(new TextObject(
                    "{=bp_hcq_016}You have put {VILLAGE} to the torch. Return to {CHIEF} at {HIDEOUT} to speak the words yourself - the silver and the standing change hands at the chief's own fire.")
                    .SetTextVariable("VILLAGE", vName)
                    .SetTextVariable("CHIEF", chiefName)
                    .SetTextVariable("HIDEOUT", hideoutName));
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "RaidVillage TriggerRaidPending fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public void ForceCompleteJournalProgress()
        {
            try
            {
                var entries = JournalEntries;
                if (entries == null) return;
                foreach (var entry in entries)
                {
                    if (entry == null) continue;
                    if (entry.Range <= 0) continue;
                    entry.UpdateCurrentProgress(entry.Range);
                }
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditRaidVillageQuest.ForceCompleteJournalProgress fail: " + ex.Message);
            }
        }

        protected override void OnCompleteWithSuccess()
        {
            try
            {
                ForceCompleteJournalProgress();
                int actualReward = _raidedByOtherCause ? (_rewardGold / 4) : _rewardGold;
                int relationDelta = _raidedByOtherCause ? +1 : +5;
                if (Hero.MainHero != null && actualReward > 0)
                    GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, actualReward, false);
                if (QuestGiver != null && Hero.MainHero != null)
                    ChangeRelationAction.ApplyPlayerRelation(QuestGiver, relationDelta, true, true);

                try
                {
                    string chiefName = QuestGiver?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_002", "the chief");
                    string vName = TargetVillage?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_001", "the marked village");
                    string lordName = TargetLord?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_003", "the marked lord");
                    string rewardWords = _rewardGold > 0 ? _rewardGold.ToString("N0") : BandItPlus.Localization.Get("bp_raidvillagequest_005", "the agreed");
                    string reducedRewardWords = actualReward > 0 ? actualReward.ToString("N0") : BandItPlus.Localization.Get("bp_raidvillagequest_006", "thin coin");
                    BandItPlus.Cultures.CultureProfile prof = null;
                    if (!string.IsNullOrEmpty(_cultureId))
                        BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(_cultureId, out prof);
                    string template = null;
                    if (prof != null && _raidedByOtherCause && !string.IsNullOrEmpty(prof.RaidVillageOtherCauseProse))
                        template = prof.RaidVillageOtherCauseProse;
                    else if (prof != null && !_raidedByOtherCause && !string.IsNullOrEmpty(prof.RaidVillageComplete))
                        template = prof.RaidVillageComplete;
                    else if (_raidedByOtherCause)
                        template = BandItPlus.Localization.Get("bp_raidvillagequest_007", "{VILLAGE} burns - but not by your hand. {CHIEF} counts out {REDUCED_REWARD} in quarter-pay and the bond sits a smaller notch firmer.");
                    else
                        template = BandItPlus.Localization.Get("bp_raidvillagequest_008", "{VILLAGE} burns, and {CHIEF} has heard the words from your own mouth. {REWARD} denars sit counted at the chief's fire. The contract closes.");
                    var entry = template
                        .Replace("{CHIEF}", chiefName)
                        .Replace("{VILLAGE}", vName)
                        .Replace("{LORD}", lordName)
                        .Replace("{REWARD}", rewardWords)
                        .Replace("{REDUCED_REWARD}", reducedRewardWords);
                    AddLog(new TextObject(entry));
                }
                catch (Exception logEx)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditRaidVillageQuest closure-log fail: " + logEx.GetType().Name + ": " + logEx.Message);
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditRaidVillageQuest: completed (otherCause=" + _raidedByOtherCause + ")"
                    + " rewarded " + actualReward + " gold + relation " + (relationDelta >= 0 ? "+" : "") + relationDelta);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("RaidVillage OnCompleteWithSuccess fail: " + ex.Message);
            }
        }

        protected override void OnFinalize() { }
        protected override void OnTimedOut() { }
        protected override void InitializeQuestOnGameLoad()
        {
            EnsureTargetTracked("load");
            DoRegisterEvents("InitializeQuestOnGameLoad");
        }
        protected override void SetDialogs() { /* dialogs registered in BanditDialogManager */ }

        public void InitJournalEntries()
        {
            try
            {
                string vName = TargetVillage?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_001", "the marked village");
                string lordName = TargetLord?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_003", "the marked lord");
                string chiefName = QuestGiver?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_raidvillagequest_002", "the chief");
                string rewardWords = _rewardGold > 0 ? _rewardGold.ToString("N0") : BandItPlus.Localization.Get("bp_raidvillagequest_005", "the agreed");
                string template = null;
                if (!string.IsNullOrEmpty(_cultureId)
                    && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(_cultureId, out var prof)
                    && prof != null
                    && !string.IsNullOrEmpty(prof.RaidVillageReason))
                {
                    template = prof.RaidVillageReason;
                }
                else
                {
                    template = BandItPlus.Localization.Get("bp_raidvillagequest_009", "{CHIEF} has named {VILLAGE}, the holding of Lord {LORD}, as the village whose burning will close an old debt. {REWARD} denars wait at the chief's fire when the contract closes.");
                }
                var entry = template
                    .Replace("{CHIEF}", chiefName)
                    .Replace("{VILLAGE}", vName)
                    .Replace("{LORD}", lordName)
                    .Replace("{REWARD}", rewardWords);
                AddLog(new TextObject(entry));
                AddDiscreteLog(
                    new TextObject("{=bp_hcq_017}Raid {VILLAGE} — put it to the torch")
                        .SetTextVariable("VILLAGE", vName),
                    new TextObject(vName),
                    0, 1);
                // 2026-06-03 edge-case: cache the original owner-clan ID so we can detect
                // ownership flips (siege/conquest moving the village to a different clan).
                try
                {
                    var v = TargetVillage;
                    _originalOwnerClanId = v?.OwnerClan?.StringId;
                }
                catch { }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditRaidVillageQuest.InitJournalEntries: village=" + _targetVillageId + " lord=" + _targetLordId
                    + " ownerClan=" + _originalOwnerClanId + " for " + _cultureId);
                EnsureTargetTracked("init");
                // 2026-06-04 belt-and-suspenders: call DoRegisterEvents directly. If the
                // vanilla RegisterEvents auto-hook didn't fire (it wasn't logging), this
                // path ensures the event listeners get attached at least once.
                DoRegisterEvents("InitJournalEntries");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditRaidVillageQuest.InitJournalEntries fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void EnsureTargetTracked(string source)
        {
            try
            {
                if (!this.IsOngoing) return;
                var v = TargetVillage;
                if (v == null) return;
                AddTrackedObject(v);
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditRaidVillageQuest.EnsureTargetTracked(" + source + "): tracking '" + v.Name + "'");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "RaidVillage EnsureTargetTracked(" + source + ") fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
