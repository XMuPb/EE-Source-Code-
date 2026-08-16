using System;
using BandItPlus.Cultures;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.LogEntries;
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
    // Wave 4.11-fix v61 (2026-05-08): chief Tier 3 named-target hit quest. Player
    // commits to hunting a specific noble Hero on the chief's behalf. Quest completes
    // when the target dies — by the player's hand OR by anyone else's. Realistic
    // outcome semantic ("I sent a man, the man is dead, credit goes to me") AND
    // simpler implementation (no MapEvent participant tracking).
    //
    // Mirrors BanditTrustQuest / BanditVendorQuest QuestBase patterns:
    // - Title pulls chief name + target name
    // - JournalEntries = description log + 1 discrete tracker (0/1, set to 1 on kill)
    // - HourlyTick override + MapEventEnded refresher (defensive)
    // - OnCompleteWithSuccess fires reward delivery (gold + chief relation)
    // - QuestGiver = the chief Hero (real Hero, no portrait suppression needed unlike vendor quests)
    //
    // Saveable type ID = 4. Old saves without BanditNamedTargetQuest instances load
    // fine; new saves persist via this type registration.
    public class BanditNamedTargetQuest : QuestBase
    {
        [SaveableField(1)]
        private string _cultureId;

        [SaveableField(2)]
        private string _targetHeroId;

        [SaveableField(3)]
        private int _rewardGold;

        // 2026-06-03 story-expansion: set to true in OnHeroKilled when the killer is
        // NOT MainHero or a member of MainHero's party. OnCompleteWithSuccess reads
        // this to apply a reduced reward (~25%) and a distinct per-chief journal log.
        [SaveableField(4)]
        private bool _killedByOtherCause;

        // 2026-06-03 story-expansion: two-phase resolution. OnHeroKilled sets this to
        // true (instead of completing the quest immediately) and adds an interim log
        // directing the player to return to the chief. The actual reward + closure
        // log only fires when the player picks the "I've ended the man you named"
        // dialog at the chief, which calls FinishWithSuccess.
        [SaveableField(5)]
        private bool _killAwaitingChiefReturn;

        public string CultureId => _cultureId;
        public string TargetHeroId => _targetHeroId;
        public int RewardGoldAmount => _rewardGold;
        public bool IsAwaitingChiefReturn => _killAwaitingChiefReturn;
        public bool KilledByOtherCause => _killedByOtherCause;

        public Hero TargetHero
        {
            get
            {
                if (string.IsNullOrEmpty(_targetHeroId)) return null;
                // v62 fix (2026-05-08): Hero is NOT tracked by MBObjectManager —
                // that's for XML-defined game-data objects (ItemObject, CharacterObject).
                // Heroes are runtime campaign data; the canonical lookup is Hero.Find(stringId).
                // Without this fix the Title and InitJournalEntries fell through to the
                // "?? the marked man" fallback because GetObject<Hero> always returned null.
                return Hero.Find(_targetHeroId);
            }
        }

        public void FinishWithSuccess() => CompleteQuestWithSuccess();
        public void FinishWithCancel() => CompleteQuestWithCancel();

        public BanditNamedTargetQuest(string questId, Hero questGiver, CampaignTime duration,
            int rewardGold, string cultureId, string targetHeroId)
            : base(questId, questGiver, duration, rewardGold)
        {
            _cultureId = cultureId;
            _targetHeroId = targetHeroId;
            _rewardGold = rewardGold;
        }

        public override TextObject Title
        {
            get
            {
                string targetName = TargetHero?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_namedtargetquest_001", "the marked man");
                string chiefName = QuestGiver?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_namedtargetquest_002", "the chief");
                return new TextObject("{=bp_hcq_006}Hunt {TARGET} for {CHIEF}")
                    .SetTextVariable("TARGET", targetName)
                    .SetTextVariable("CHIEF", chiefName);
            }
        }

        public override bool IsRemainingTimeHidden => true;

        // Tier-3 hunt is a story-class quest — gold journal icon, not blue issue.
        public override string SpecialQuestType => "BanditNamedTargetQuest";

        protected override void RegisterEvents()
        {
            // 2026-06-04: vanilla QuestBase calls this on quest creation AND on game load.
            // We route through DoRegisterEvents() so InitJournalEntries() and
            // InitializeQuestOnGameLoad() can also invoke it as a belt-and-suspenders
            // measure. The internal helper guards against double-registration.
            DoRegisterEvents("RegisterEvents");
        }

        // Idempotent guard so we don't double-subscribe when DoRegisterEvents is called
        // by RegisterEvents (vanilla contract) AND by InitJournalEntries / InitializeQuestOnGameLoad
        // (belt). Runtime-only — NOT a SaveableField.
        private bool _eventsSubscribed;

        private void DoRegisterEvents(string caller)
        {
            int step = 0;
            try
            {
                if (_eventsSubscribed)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditNamedTargetQuest.DoRegisterEvents(" + caller + "): already subscribed, skipping");
                    return;
                }
                step = 1; CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, OnHeroKilled);
                step = 2; CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
                // 2026-06-03 story-expansion: capture handling. If the target is jailed
                // alive in another lord's dungeon, the quest emits a pending log so the
                // player knows what's blocking them. Released → also logged.
                step = 3; CampaignEvents.HeroPrisonerTaken.AddNonSerializedListener(this, OnHeroPrisonerTaken);
                step = 4; CampaignEvents.HeroPrisonerReleased.AddNonSerializedListener(this, OnHeroPrisonerReleased);
                step = 99;
                _eventsSubscribed = true;
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditNamedTargetQuest.DoRegisterEvents(" + caller + "): ALL 4 subscribed OK (target='"
                    + (_targetHeroId ?? "<null>") + "' culture='" + (_cultureId ?? "<null>") + "')");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditNamedTargetQuest.DoRegisterEvents(" + caller + ") FAIL at step=" + step
                    + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Quest completion trigger — when the tracked Hero dies (by any cause), we
        // mark the journal entry complete and fire FinishWithSuccess. Bannerlord's
        // HeroKilledEvent fires after Hero.Die() runs, so the killed hero's state
        // has already settled by the time we read it.
        // 2026-06-03 story-expansion: distinguish player-kill from other-cause death.
        // Player-kill → full reward + per-chief NamedTargetComplete log.
        // Other-cause → reduced reward (~25%) + per-chief NamedTargetOtherCauseProse log.
        private void OnHeroKilled(Hero victim, Hero killer, KillCharacterAction.KillCharacterActionDetail detail, bool showNotification)
        {
            try
            {
                if (!this.IsOngoing) return;
                if (victim == null || string.IsNullOrEmpty(_targetHeroId)) return;
                if (victim.StringId != _targetHeroId) return;

                // Determine if the killing was credited to the player or a companion in their party.
                // Player kills, companion kills, and "execute prisoner" all set killer=MainHero (or
                // a companion Hero already in MainParty.MemberRoster). Anything else — another lord's
                // ambush, disease (detail = DiedInBattle/Childbirth/Execution-by-other), old age — is
                // counted as other-cause.
                bool playerKill = false;
                if (killer != null && Hero.MainHero != null)
                {
                    if (killer == Hero.MainHero) playerKill = true;
                    else if (killer.PartyBelongedTo != null
                             && killer.PartyBelongedTo == MobileParty.MainParty)
                    {
                        playerKill = true;
                    }
                }
                _killedByOtherCause = !playerKill;
                _killAwaitingChiefReturn = true;

                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditNamedTargetQuest: target '" + _targetHeroId + "' killed by '"
                    + (killer?.Name?.ToString() ?? "unknown")
                    + "' — playerKill=" + playerKill
                    + " — awaiting chief return");
                ForceCompleteJournalProgress();

                // 2026-06-03 story-expansion: two-phase resolution. Instead of completing
                // immediately, post an interim log directing the player back to the chief.
                // The quest stays open until they pick the "I've ended the man you named"
                // dialog at the chief's hideout — that path fires the actual reward + closure.
                try
                {
                    string targetName = victim?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_namedtargetquest_003", "the marked man");
                    string chiefName = QuestGiver?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_namedtargetquest_004", "the chief");
                    string hideoutName = QuestGiver?.HomeSettlement?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_namedtargetquest_005", "the hideout");
                    AddLog(new TextObject(
                        "{=bp_hcq_007}You have killed {TARGET}. Return to {CHIEF} at {HIDEOUT} to claim what was promised - speak the words to him yourself, and the silver and the standing change hands.")
                        .SetTextVariable("TARGET", targetName)
                        .SetTextVariable("CHIEF", chiefName)
                        .SetTextVariable("HIDEOUT", hideoutName));
                }
                catch (Exception logEx)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditNamedTargetQuest interim-log fail: " + logEx.GetType().Name + ": " + logEx.Message);
                }
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("OnHeroKilled fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // 2026-06-03 story-expansion: prisoner-taken handler. If the target is captured
        // by another lord, the quest emits a journal entry so the player knows. Quest
        // stays open — when target dies later (in dungeon, after release, executed by
        // captor), OnHeroKilled handles completion normally.
        private void OnHeroPrisonerTaken(PartyBase captorParty, Hero prisoner)
        {
            try
            {
                if (!this.IsOngoing) return;
                if (prisoner == null || string.IsNullOrEmpty(_targetHeroId)) return;
                if (prisoner.StringId != _targetHeroId) return;
                string targetName = prisoner.Name?.ToString() ?? BandItPlus.Localization.Get("bp_namedtargetquest_006", "the marked man");
                string captorName = captorParty?.LeaderHero?.Name?.ToString()
                                    ?? captorParty?.Settlement?.Name?.ToString()
                                    ?? captorParty?.Name?.ToString()
                                    ?? BandItPlus.Localization.Get("bp_namedtargetquest_007", "unknown hands");
                AddLog(new TextObject("{=bp_hcq_008}{TARGET} has been taken prisoner by {CAPTOR}. The contract remains open. Wait for him to be released, or break the keep yourself - either way, the deed is still owed.")
                    .SetTextVariable("TARGET", targetName)
                    .SetTextVariable("CAPTOR", captorName));
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditNamedTargetQuest: target captured by " + captorName);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "OnHeroPrisonerTaken fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // 2026-06-03 story-expansion: prisoner-released handler. Pairs with OnHeroPrisonerTaken
        // so the player knows when the target is back in the field and the hunt can resume.
        private void OnHeroPrisonerReleased(Hero prisoner, PartyBase captorParty, IFaction faction, EndCaptivityDetail detail, bool showNotification)
        {
            try
            {
                if (!this.IsOngoing) return;
                if (prisoner == null || string.IsNullOrEmpty(_targetHeroId)) return;
                if (prisoner.StringId != _targetHeroId) return;
                string targetName = prisoner.Name?.ToString() ?? BandItPlus.Localization.Get("bp_namedtargetquest_008", "the marked man");
                AddLog(new TextObject("{=bp_hcq_009}{TARGET} is loose again. The chief's contract still stands, and the road is open.")
                    .SetTextVariable("TARGET", targetName));
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditNamedTargetQuest: target released, detail=" + detail);
                // Re-track on release — the target's PartyBelongedTo will have a fresh
                // value (their old party, a new escort, or a wandering reform) and the
                // map marker needs to follow.
                EnsureTargetTracked("released");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "OnHeroPrisonerReleased fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            // Defensive — sometimes Hero death races the HeroKilledEvent dispatch in
            // edge cases (siege defeat, etc.). Refresh on MapEventEnded as a backup.
            try
            {
                if (!this.IsOngoing) return;
                if (string.IsNullOrEmpty(_targetHeroId)) return;
                var t = Hero.Find(_targetHeroId);     // v62 fix — same root cause as TargetHero getter
                if (t != null && !t.IsAlive)
                {
                    // 2026-06-03 story-expansion: backup path when HeroKilledEvent failed
                    // to dispatch. Set pending-collection state (NOT auto-complete) so
                    // the player still goes through the return-to-chief dialog flow.
                    if (!_killAwaitingChiefReturn)
                    {
                        _killAwaitingChiefReturn = true;
                        _killedByOtherCause = true;  // missed the event = couldn't determine killer = treat as other-cause
                        ForceCompleteJournalProgress();
                        try
                        {
                            string targetName = t?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_namedtargetquest_009", "the marked man");
                            string chiefName = QuestGiver?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_namedtargetquest_010", "the chief");
                            string hideoutName = QuestGiver?.HomeSettlement?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_namedtargetquest_011", "the hideout");
                            AddLog(new TextObject(
                                "{=bp_hcq_010}You have heard that {TARGET} is dead. Return to {CHIEF} at {HIDEOUT} to confirm the deed and claim what was promised.")
                                .SetTextVariable("TARGET", targetName)
                                .SetTextVariable("CHIEF", chiefName)
                                .SetTextVariable("HIDEOUT", hideoutName));
                        }
                        catch { }
                        BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                            "BanditNamedTargetQuest: MapEventEnded backup-path triggered (killer unknown) — awaiting chief return");
                    }
                    return;
                }
                // 2026-06-03 story-expansion: re-assert the blue map marker. Battles can
                // disband a target's party and re-spawn them in a fresh one (or move them
                // to a captor's party). AddTrackedObject is idempotent so calling it on the
                // same party is cheap — calling it on a NEW party updates the marker.
                EnsureTargetTracked("mapevent");
            }
            catch { }
        }

        // Force the discrete tracker to 1/1 at completion. Called from OnHeroKilled
        // before FinishWithSuccess so the journal archives showing complete.
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
                    "BanditNamedTargetQuest.ForceCompleteJournalProgress fail: " + ex.Message);
            }
        }

        protected override void OnCompleteWithSuccess()
        {
            try
            {
                ForceCompleteJournalProgress();

                // 2026-06-03 story-expansion: branch reward + closure log on whether
                // the killing was credited to the player. Player-kill = full reward +
                // relation +5 + NamedTargetComplete log. Other-cause = quarter reward +
                // relation +1 + NamedTargetOtherCauseProse log.
                int actualReward = _killedByOtherCause ? (_rewardGold / 4) : _rewardGold;
                int relationDelta = _killedByOtherCause ? +1 : +5;

                if (Hero.MainHero != null && actualReward > 0)
                {
                    GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, actualReward, false);
                }
                if (QuestGiver != null && Hero.MainHero != null)
                {
                    ChangeRelationAction.ApplyPlayerRelation(QuestGiver, relationDelta, true, true);
                }

                // Per-chief closure log. Picks NamedTargetOtherCauseProse template if the
                // killing was by another hand, NamedTargetComplete otherwise. Substitution
                // placeholders: {CHIEF}, {TARGET}, {REWARD} (full), {REDUCED_REWARD} (quarter).
                try
                {
                    string chiefName = QuestGiver?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_namedtargetquest_012", "the chief");
                    string targetName = TargetHero?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_namedtargetquest_013", "the marked man");
                    string rewardWords = _rewardGold > 0 ? _rewardGold.ToString("N0") : BandItPlus.Localization.Get("bp_namedtargetquest_014", "the agreed");
                    string reducedRewardWords = actualReward > 0 ? actualReward.ToString("N0") : BandItPlus.Localization.Get("bp_namedtargetquest_015", "thin coin");
                    string template = null;
                    BandItPlus.Cultures.CultureProfile prof = null;
                    if (!string.IsNullOrEmpty(_cultureId))
                    {
                        BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(_cultureId, out prof);
                    }
                    if (prof != null && _killedByOtherCause && !string.IsNullOrEmpty(prof.NamedTargetOtherCauseProse))
                    {
                        template = prof.NamedTargetOtherCauseProse;
                    }
                    else if (prof != null && !_killedByOtherCause && !string.IsNullOrEmpty(prof.NamedTargetComplete))
                    {
                        template = prof.NamedTargetComplete;
                    }
                    else if (_killedByOtherCause)
                    {
                        template = BandItPlus.Localization.Get("bp_namedtargetquest_020", "{TARGET} is dead - but not by your hand. Word reached {CHIEF} from a roadside ambush a rival lord's men finished without us. The grudge stands answered, after a fashion. {CHIEF} counts out {REDUCED_REWARD} denars in quarter-pay and sets the bond a smaller notch firmer. The contract closes, cooler than an earned kill.");
                    }
                    else
                    {
                        template = BandItPlus.Localization.Get("bp_namedtargetquest_021", "{TARGET} is dead. {CHIEF} has been told, and {CHIEF} has heard. {REWARD} denars are counted out at the chief's fire and the relation between the two of you is stamped a degree deeper. The contract closes.");
                    }
                    var entry = template
                        .Replace("{CHIEF}", chiefName)
                        .Replace("{TARGET}", targetName)
                        .Replace("{REWARD}", rewardWords)
                        .Replace("{REDUCED_REWARD}", reducedRewardWords);
                    AddLog(new TextObject(entry));
                }
                catch (Exception logEx)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditNamedTargetQuest closure-log fail: " + logEx.GetType().Name + ": " + logEx.Message);
                }

                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditNamedTargetQuest: completed (otherCause=" + _killedByOtherCause + ")"
                    + " rewarded " + actualReward + " gold + relation " + (relationDelta >= 0 ? "+" : "") + relationDelta
                    + " with " + (QuestGiver?.Name?.ToString() ?? "?"));
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("OnCompleteWithSuccess fail: " + ex.Message);
            }
        }

        protected override void OnFinalize() { }
        protected override void OnTimedOut() { }
        // 2026-06-03 story-expansion: re-track the target on save load. AddTrackedObject
        // state does NOT survive save/load on QuestBase, so we re-call it whenever the
        // quest is rehydrated. Mirrors the BanditAllianceQuest pattern.
        protected override void InitializeQuestOnGameLoad()
        {
            EnsureTargetTracked("load");
            DoRegisterEvents("InitializeQuestOnGameLoad");
        }

        // 2026-06-03 story-expansion: ensure the target's current MobileParty is on the
        // visual tracker (campaign-map blue quest marker). Called from InitJournalEntries
        // (initial registration), InitializeQuestOnGameLoad (after save load), and
        // OnHeroPrisonerReleased (target back in field with possibly a new party). Idempotent.
        private void EnsureTargetTracked(string source)
        {
            try
            {
                if (!this.IsOngoing) return;
                if (string.IsNullOrEmpty(_targetHeroId)) return;
                var t = TargetHero;
                if (t == null || !t.IsAlive) return;
                var party = t.PartyBelongedTo;
                if (party == null) return;
                AddTrackedObject(party);
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditNamedTargetQuest.EnsureTargetTracked(" + source + "): tracking '"
                    + (party.Name?.ToString() ?? "?") + "' for target " + _targetHeroId);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "EnsureTargetTracked(" + source + ") fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
        protected override void SetDialogs() { /* dialog tokens registered in BanditDialogManager */ }

        // Public so the dialog manager can populate journal entries after registration.
        // Mirrors BanditTrustQuest / BanditVendorQuest pattern — pre-registration log
        // calls don't render.
        public void InitJournalEntries()
        {
            try
            {
                string targetName = TargetHero?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_namedtargetquest_016", "the marked man");
                string chiefName = QuestGiver?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_namedtargetquest_017", "the chief");
                string homeName = TargetHero?.HomeSettlement?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_namedtargetquest_018", "his hold");
                string rewardWords = _rewardGold > 0 ? _rewardGold.ToString("N0") : BandItPlus.Localization.Get("bp_namedtargetquest_019", "the agreed");

                // 2026-06-03 story-expansion: prefer per-chief NamedTargetReason
                // (third-person journal narrative threading the chief's specific
                // grudge). Falls back to a generic line.
                string template = null;
                if (!string.IsNullOrEmpty(_cultureId)
                    && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(_cultureId, out var prof)
                    && prof != null
                    && !string.IsNullOrEmpty(prof.NamedTargetReason))
                {
                    template = prof.NamedTargetReason;
                }
                else
                {
                    template = BandItPlus.Localization.Get("bp_namedtargetquest_022", "{CHIEF} has named {TARGET} of {HOME} as the man he wants dead. The reason runs deeper than the bandit-tongue says aloud - but the deed is what's needed, not the why. {REWARD} denars wait at the chief's fire when the contract closes.");
                }
                var entry = template
                    .Replace("{CHIEF}", chiefName)
                    .Replace("{TARGET}", targetName)
                    .Replace("{HOME}", homeName)
                    .Replace("{REWARD}", rewardWords);
                AddLog(new TextObject(entry));
                AddDiscreteLog(
                    new TextObject("{=bp_hcq_011}Hunt {TARGET} — find them and end them")
                        .SetTextVariable("TARGET", targetName),
                    new TextObject(targetName),
                    0, 1);
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditNamedTargetQuest.InitJournalEntries: target=" + _targetHeroId + " for " + _cultureId);
                // 2026-06-03 story-expansion: blue-mark the target's party on the campaign
                // map so the player can actually find them. Bannerlord's AddTrackedObject
                // adds a quest-marker arrow that follows the party as it moves.
                EnsureTargetTracked("init");
                // 2026-06-04: belt-and-suspenders — vanilla RegisterEvents may not always
                // fire reliably for runtime-constructed quests, so re-call here. The helper
                // is idempotent via _eventsSubscribed.
                DoRegisterEvents("InitJournalEntries");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditNamedTargetQuest.InitJournalEntries EXCEPTION: " + ex.Message);
            }
        }
    }
}
