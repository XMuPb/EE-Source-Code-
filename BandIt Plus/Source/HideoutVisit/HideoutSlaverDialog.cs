using System;
using System.Collections.Generic;
using BandItPlus.Cultures;
using BandItPlus.Quests;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace BandItPlus.HideoutVisit
{
    // Wave 4.12 (2026-05-10): slaver vendor dialog. Parallels HideoutVendorDialog.cs pattern.
    //
    // Bannerlord 1.2.x AND 1.3.x compatibility: uses only canonical APIs
    // (AddDialogLine, AddPlayerLine, ShowMultiSelectionInquiry, MemberRoster.AddToCounts,
    // GiveGoldAction). No version-sensitive event subscriptions; no reflection.
    //
    // Phases included:
    //   2.3 — T0 gate dialog (greet → leave OR offer quest)
    //   4.1 — Trade root + OnShowTroops
    //   4.2 — Hero-pick popup via ShowMultiSelectionInquiry
    //   5.1 — Quest offer/accept/turn-in tokens
    //
    // Spec:  docs/superpowers/specs/2026-05-10-slaver-vendor-design.md
    // Plan:  docs/superpowers/plans/2026-05-10-slaver-vendor.md
    public class HideoutSlaverDialog : CampaignBehaviorBase
    {
        // Priority 150 (vendor-level) — slaver dialog only fires when partner = slaver NPC.
        private const int kPriority = 150;

        // Stashed reference for the currently-turn-in-able quest so OnTurnInSlaverQuest
        // doesn't need to re-walk QuestManager.
        private static BanditSlaverQuest _completableQuest;

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        }

        public override void SyncData(IDataStore dataStore) { }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            try
            {
                // ---------------------------------------------------------------
                // T0 gate (trust=0): slaver greets + offers first compound quest.
                // ---------------------------------------------------------------
                starter.AddDialogLine("bp_slaver_t0_greet",
                    "start", "bp_slaver_t0_options",
                    "{=!}{SLAVER_GATE_SPEECH}",
                    new ConversationSentence.OnConditionDelegate(IsSlaverConvT0),
                    null, kPriority);

                starter.AddPlayerLine("bp_slaver_t0_dowork",
                    "bp_slaver_t0_options", "bp_slaver_quest_offer",
                    "{=bp_hideoutslaverdialog_001}I'll do your work. <span style=\"Conversation.Persuasion.Positive\">(Easy)</span>",
                    null, null, kPriority);

                starter.AddPlayerLine("bp_slaver_t0_leave",
                    "bp_slaver_t0_options", "close_window",
                    "{=bp_hideoutslaverdialog_002}Not today.",
                    null, null, kPriority);

                // ---------------------------------------------------------------
                // T1+ trade root (trust >= 1): troops / heroes / more work / exit.
                // ---------------------------------------------------------------
                starter.AddDialogLine("bp_slaver_trade_greet",
                    "start", "bp_slaver_trade_options",
                    "{=bp_hideoutslaverdialog_003}What is it?",
                    new ConversationSentence.OnConditionDelegate(IsSlaverConvT1Plus),
                    null, kPriority);

                // 2026-06-06 Wave 4.13.8 — Option C: two player lines with mutually
                // exclusive conditions. Pool non-empty → recruit popup; pool empty →
                // chat-native NPC line explaining the gate.
                starter.AddPlayerLine("bp_slaver_show_troops",
                    "bp_slaver_trade_options", "bp_slaver_trade_relay",
                    "{=bp_hideoutslaverdialog_004}Show me the line. <span style=\"Conversation.Persuasion.Positive\">(Recruit)</span>",
                    new ConversationSentence.OnConditionDelegate(HasSlaverTroopsAvailable),
                    new ConversationSentence.OnConsequenceDelegate(OnShowTroops),
                    kPriority);
                starter.AddPlayerLine("bp_slaver_show_troops_empty",
                    "bp_slaver_trade_options", "bp_slaver_no_troops_response",
                    "{=bp_hideoutslaverdialog_005}Show me the line. <span style=\"Conversation.Persuasion.Positive\">(Recruit)</span>",
                    new ConversationSentence.OnConditionDelegate(HasNoSlaverTroopsAvailable),
                    null, kPriority);
                starter.AddDialogLine("bp_slaver_no_troops_response_line",
                    "bp_slaver_no_troops_response", "bp_slaver_trade_relay",
                    "{=bp_hideoutslaverdialog_006}Trust runs thin. Bring me work first, then we'll talk about the line.",
                    null, null, kPriority);

                starter.AddPlayerLine("bp_slaver_show_heroes",
                    "bp_slaver_trade_options", "bp_slaver_trade_relay",
                    "{=bp_hideoutslaverdialog_007}Show me what's in chains. <span style=\"Conversation.Persuasion.Positive\">(Recruit)</span>",
                    new ConversationSentence.OnConditionDelegate(HasSlaverHeroesAvailable),
                    new ConversationSentence.OnConsequenceDelegate(OnShowHeroes),
                    kPriority);
                starter.AddPlayerLine("bp_slaver_show_heroes_empty",
                    "bp_slaver_trade_options", "bp_slaver_no_heroes_response",
                    "{=bp_hideoutslaverdialog_008}Show me what's in chains. <span style=\"Conversation.Persuasion.Positive\">(Recruit)</span>",
                    new ConversationSentence.OnConditionDelegate(HasNoSlaverHeroesAvailable),
                    null, kPriority);
                starter.AddDialogLine("bp_slaver_no_heroes_response_line",
                    "bp_slaver_no_heroes_response", "bp_slaver_trade_relay",
                    "{=bp_hideoutslaverdialog_009}Cages are empty for you today. The right names come with time and trust.",
                    null, null, kPriority);

                // Wave 4.12-fix (2026-05-11): cellar narrative options.
                // Trust >= 2: peek at the cellar from above (popup with CellarFlavor prose).
                // Trust >= 3: physically descend (walk-in via menu transition).
                starter.AddPlayerLine("bp_slaver_peek_cellar",
                    "bp_slaver_trade_options", "bp_slaver_trade_relay",
                    "{=bp_hideoutslaverdialog_010}What's down in your cellar? <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(IsCellarPeekAvailable),
                    new ConversationSentence.OnConsequenceDelegate(OnPeekCellar),
                    kPriority);

                starter.AddPlayerLine("bp_slaver_walk_cellar",
                    "bp_slaver_trade_options", "close_window",
                    "{=bp_hideoutslaverdialog_011}Take me down to the cellar. <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(IsCellarWalkInAvailable),
                    new ConversationSentence.OnConsequenceDelegate(OnWalkDownToCellar),
                    kPriority);

                // Wave 4.12-fix10 (2026-05-11): chat/story options.
                // 2026-06-06 Wave 4.13: inline dialog (chief-style chat, no popup).
                // 2026-06-06 Wave 4.13.1: paragraph-chunked — NPC speaks ONE paragraph
                // per turn; player picks "..." to advance, "I see." to exit.
                // 2026-06-06 Wave 4.13.5: chat SUB-MENU. Backstory/news now live under
                // a "Tell me about your trade..." parent option, and after a chat ends
                // the player returns to the sub-menu (not the trade menu) so they can
                // immediately ask the other topic without re-navigating.
                starter.AddPlayerLine("bp_slaver_chat_subgroup_enter",
                    "bp_slaver_trade_options", "bp_slaver_chat_relay",
                    "{=bp_hideoutslaverdialog_012}Tell me about your trade... <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    null, null, kPriority);
                starter.AddDialogLine("bp_slaver_chat_relay_line",
                    "bp_slaver_chat_relay", "bp_slaver_chat_options",
                    "{=bp_hideoutslaverdialog_013}Ask.",
                    null, null, kPriority);
                starter.AddPlayerLine("bp_slaver_chat_self",
                    "bp_slaver_chat_options", "bp_slaver_backstory_response",
                    "{=bp_hideoutslaverdialog_014}What pays a man into this trade? <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(ResetSlaverBackstoryState),
                    kPriority);
                starter.AddDialogLine("bp_slaver_backstory_response_line",
                    "bp_slaver_backstory_response", "bp_slaver_backstory_choice",
                    "{=!}{SLAVER_BACKSTORY_TEXT}",
                    new ConversationSentence.OnConditionDelegate(SetSlaverBackstoryText),
                    null, kPriority);
                // 2026-06-06 Wave 4.13.3 — fallback DialogLine. Without this, if
                // SetSlaverBackstoryText returns false (culture not in SlaverProfiles,
                // empty body, or split produced zero paragraphs), the engine has no
                // NPC line to play and falls through to "Click to continue" — which
                // strands the dialog because the exit player-lines live on _choice,
                // not _response. Lower priority so the authored line wins when valid.
                starter.AddDialogLine("bp_slaver_backstory_response_fallback",
                    "bp_slaver_backstory_response", "bp_slaver_backstory_choice",
                    "{=bp_hideoutslaverdialog_015}My past is mine. Yours is yours. Coin is what we share.",
                    null, null, kPriority - 10);
                // "..." available while more paragraphs remain — loops back to response.
                starter.AddPlayerLine("bp_slaver_backstory_more",
                    "bp_slaver_backstory_choice", "bp_slaver_backstory_response",
                    "...",
                    new ConversationSentence.OnConditionDelegate(HasMoreBackstoryParas),
                    new ConversationSentence.OnConsequenceDelegate(AdvanceBackstoryPara),
                    kPriority + 10);
                // Exit option — always shown, returns to trade menu.
                starter.AddPlayerLine("bp_slaver_backstory_done",
                    "bp_slaver_backstory_choice", "bp_slaver_chat_relay",
                    "{=bp_hideoutslaverdialog_016}I see.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(ResetSlaverBackstoryState),
                    kPriority);

                starter.AddPlayerLine("bp_slaver_chat_news",
                    "bp_slaver_chat_options", "bp_slaver_news_response",
                    "{=bp_hideoutslaverdialog_017}What's the word on the road? <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(ResetSlaverNewsState),
                    kPriority);
                // 2026-06-06 Wave 4.13.5 — exit option on the chat sub-menu.
                starter.AddPlayerLine("bp_slaver_chat_step_back",
                    "bp_slaver_chat_options", "bp_slaver_trade_relay",
                    "{=bp_hideoutslaverdialog_018}Step back.",
                    null, null, kPriority - 10);
                starter.AddDialogLine("bp_slaver_news_response_line",
                    "bp_slaver_news_response", "bp_slaver_news_choice",
                    "{=!}{SLAVER_NEWS_TEXT}",
                    new ConversationSentence.OnConditionDelegate(SetSlaverNewsText),
                    null, kPriority);
                // 2026-06-06 Wave 4.13.3 — same fallback pattern as backstory.
                starter.AddDialogLine("bp_slaver_news_response_fallback",
                    "bp_slaver_news_response", "bp_slaver_news_choice",
                    "{=bp_hideoutslaverdialog_019}Nothing fresh on the road today. Quieter than I'd like.",
                    null, null, kPriority - 10);

                // 2026-06-06 Wave 4.13.4 — RELAY STATE. Every player action that returns
                // to the trade menu (show troops, show heroes, peek cellar, chat exits)
                // routes through bp_slaver_trade_relay first so this NPC line fires and
                // gives the engine a valid transition. Without this, the engine has no
                // NPC line queued when re-entering bp_slaver_trade_options and falls
                // through to a stuck "Click to continue" prompt. Mirrors the chief
                // dialog's bp_encounter_back_relay pattern in BanditDialogManager.
                starter.AddDialogLine("bp_slaver_trade_relay_line",
                    "bp_slaver_trade_relay", "bp_slaver_trade_options",
                    "{=bp_hideoutslaverdialog_020}Anything else?",
                    null, null, kPriority);
                starter.AddPlayerLine("bp_slaver_news_more",
                    "bp_slaver_news_choice", "bp_slaver_news_response",
                    "...",
                    new ConversationSentence.OnConditionDelegate(HasMoreNewsParas),
                    new ConversationSentence.OnConsequenceDelegate(AdvanceNewsPara),
                    kPriority + 10);
                starter.AddPlayerLine("bp_slaver_news_done",
                    "bp_slaver_news_choice", "bp_slaver_chat_relay",
                    "{=bp_hideoutslaverdialog_021}Good to know.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(ResetSlaverNewsState),
                    kPriority);

                starter.AddPlayerLine("bp_slaver_more_work",
                    "bp_slaver_trade_options", "bp_slaver_quest_offer",
                    "{=bp_hideoutslaverdialog_022}Any names that need work? {BP_SLAVER_QUEST_DIFFICULTY_TAG}",
                    new ConversationSentence.OnConditionDelegate(IsMoreWorkAvailable),
                    null, kPriority);

                starter.AddPlayerLine("bp_slaver_exit",
                    "bp_slaver_trade_options", "close_window",
                    "{=bp_hideoutslaverdialog_023}Until the next trade.",
                    null, null, kPriority);

                // ---------------------------------------------------------------
                // Quest offer (chained from T0 OR T1+ "more work").
                // ---------------------------------------------------------------
                starter.AddDialogLine("bp_slaver_quest_offer_line",
                    "bp_slaver_quest_offer", "bp_slaver_quest_offer_options",
                    "{=!}{SLAVER_QUEST_INTRO}",
                    new ConversationSentence.OnConditionDelegate(SetSlaverQuestIntroText),
                    null, kPriority);

                starter.AddPlayerLine("bp_slaver_quest_accept",
                    "bp_slaver_quest_offer_options", "close_window",
                    "{=bp_hideoutslaverdialog_024}I'll do it. <span style=\"Conversation.Persuasion.Positive\">(Easy)</span>",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(OnAcceptSlaverQuest),
                    kPriority);

                starter.AddPlayerLine("bp_slaver_quest_decline",
                    "bp_slaver_quest_offer_options", "close_window",
                    "{=bp_hideoutslaverdialog_025}Not today.",
                    null, null, kPriority);

                // ---------------------------------------------------------------
                // Quest turn-in (objectives met → slaver pays out).
                // ---------------------------------------------------------------
                starter.AddDialogLine("bp_slaver_turnin_line",
                    "start", "bp_slaver_turnin_options",
                    "{=!}{SLAVER_QUEST_THANKS}",
                    new ConversationSentence.OnConditionDelegate(IsSlaverQuestComplete),
                    null, kPriority + 10);  // slightly higher priority so it pre-empts trade root

                starter.AddPlayerLine("bp_slaver_turnin_collect",
                    "bp_slaver_turnin_options", "close_window",
                    "{=bp_hideoutslaverdialog_026}Pay me. <span style=\"Conversation.Persuasion.Positive\">(Easy)</span>",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(OnTurnInSlaverQuest),
                    kPriority + 10);

                HideoutPeacefulVisitState.Log("HideoutSlaverDialog: registered v4.12 dialog (T0/trade/quest/turn-in)");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("HideoutSlaverDialog.OnSessionLaunched fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ===================================================================
        // Conditions
        // ===================================================================

        private static bool IsSlaverConvT0()
        {
            try
            {
                if (!HideoutPeacefulVisitState.Active) return false;
                var spawnBeh = Campaign.Current?.GetCampaignBehavior<HideoutVisitNpcSpawnBehavior>();
                if (spawnBeh == null || spawnBeh.IsGreeterDispatchInFlight()) return false;
                var partner = ResolvePartner();
                if (partner == null || !spawnBeh.IsAgentSlaverVendor(partner)) return false;

                var hideout = HideoutPeacefulVisitState.TargetHideout;
                var cultureId = hideout?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return false;
                if (!SlaverProfiles.ByCultureId.TryGetValue(cultureId, out var profile)) return false;

                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                if ((bdm?.GetSlaverTrust(cultureId) ?? 0) > 0) return false;
                if ((bdm?.GetActiveSlaverQuestTier(cultureId) ?? 0) != 0) return false;  // suppress if quest already active

                MBTextManager.SetTextVariable("SLAVER_GATE_SPEECH", profile.GateSpeech);
                SetSlaverIdentity(partner, profile);
                return true;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("IsSlaverConvT0 EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // Wave 4.12-fix5 (2026-05-11): override speaker label so the dialog UI shows the
        // slaver's authored cultural name (e.g. "Halgar the Iron-Collared") instead of
        // the agent's CharacterObject template name. Mirrors HideoutVendorDialog's
        // SetVendorIdentity v43 pattern.
        private static void SetSlaverIdentity(Agent partner, BandItPlus.Cultures.SlaverProfile profile)
        {
            try
            {
                if (partner == null || profile == null) return;
                string speakerName = profile.Name;
                if (string.IsNullOrEmpty(speakerName)) speakerName = BandItPlus.Localization.Get("bp_hideoutslaverdialog_027", "Slaver");
                var ch = partner.Character as CharacterObject;
                if (ch != null)
                    BandItPlus.Patches.ConversationNamePatch.OverrideName(ch, speakerName);
                MBTextManager.SetTextVariable("BP_SPEAKER_NAME", speakerName);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("SetSlaverIdentity EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool IsSlaverConvT1Plus()
        {
            try
            {
                if (!HideoutPeacefulVisitState.Active) return false;
                var spawnBeh = Campaign.Current?.GetCampaignBehavior<HideoutVisitNpcSpawnBehavior>();
                if (spawnBeh == null || spawnBeh.IsGreeterDispatchInFlight()) return false;
                var partner = ResolvePartner();
                if (partner == null || !spawnBeh.IsAgentSlaverVendor(partner)) return false;

                var hideout = HideoutPeacefulVisitState.TargetHideout;
                var cultureId = hideout?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return false;
                if (!SlaverProfiles.ByCultureId.TryGetValue(cultureId, out var profile)) return false;

                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                if (bdm == null) return false;
                // Wave 4.12-fix11 (2026-05-11): fires when EITHER trust earned OR
                // active quest in progress. Without the active-quest check, clicking the
                // slaver between accept-T1 and complete-T1 matched no dialog token
                // (trust=0 still during the quest window). Same bug shape we fixed for
                // the chief in Wave 4.12-fix3.
                int slaverTrust = bdm.GetSlaverTrust(cultureId);
                int activeTier = bdm.GetActiveSlaverQuestTier(cultureId);
                bool matches = slaverTrust >= 1 || activeTier > 0;
                // 2026-06-06 Wave 4.13: speaker-label override. Without this, ongoing
                // slaver conversations (trust>=1) fell back to the troop class name
                // ("Forest Bandits Hero") instead of the slaver's authored name
                // ("Halgar the Iron-Collared"). Mirrors HideoutVendorDialog v43 where
                // SetVendorIdentity is called from EVERY matching condition.
                if (matches) SetSlaverIdentity(partner, profile);
                return matches;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("IsSlaverConvT1Plus EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // 2026-06-06 Wave 4.13.8 — Option C: availability gates for show_troops /
        // show_heroes player lines. If pool is non-empty the action option fires; if
        // empty an alternate option transitions to a chat-native explanation NPC line.
        private static bool HasSlaverTroopsAvailable()
        {
            try
            {
                var hideout = HideoutPeacefulVisitState.TargetHideout;
                var cultureId = hideout?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return false;
                if (!SlaverProfiles.ByCultureId.TryGetValue(cultureId, out var profile)) return false;
                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                int trust = bdm?.GetSlaverTrust(cultureId) ?? 0;
                if (trust >= 0 && profile.TroopT1 != null)
                    foreach (var id in profile.TroopT1)
                        if (MBObjectManager.Instance.GetObject<CharacterObject>(id) != null) return true;
                if (trust >= 2 && profile.TroopT2 != null)
                    foreach (var id in profile.TroopT2)
                        if (MBObjectManager.Instance.GetObject<CharacterObject>(id) != null) return true;
                if (trust >= 3 && profile.TroopT3 != null)
                    foreach (var id in profile.TroopT3)
                        if (MBObjectManager.Instance.GetObject<CharacterObject>(id) != null) return true;
                return false;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("HasSlaverTroopsAvailable EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }
        private static bool HasNoSlaverTroopsAvailable() => !HasSlaverTroopsAvailable();

        private static bool HasSlaverHeroesAvailable()
        {
            try
            {
                var hideout = HideoutPeacefulVisitState.TargetHideout;
                var cultureId = hideout?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return false;
                if (!SlaverProfiles.ByCultureId.TryGetValue(cultureId, out var profile)) return false;
                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                int trust = bdm?.GetSlaverTrust(cultureId) ?? 0;
                var pool = BuildSlaverHeroPool(profile, trust);
                return pool != null && pool.Count > 0;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("HasSlaverHeroesAvailable EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }
        private static bool HasNoSlaverHeroesAvailable() => !HasSlaverHeroesAvailable();

        private static bool IsMoreWorkAvailable()
        {
            try
            {
                var hideout = HideoutPeacefulVisitState.TargetHideout;
                var cultureId = hideout?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return false;
                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                if (bdm == null) return false;
                int trust = bdm.GetSlaverTrust(cultureId);
                int activeTier = bdm.GetActiveSlaverQuestTier(cultureId);
                if (!(trust < 3 && activeTier == 0)) return false;
                // 2026-06-06 Wave 4.14 — tier-aware difficulty tag.
                // Next quest tier = trust + 1 → Easy (1) / Hard (2) / Extremly Hard (3).
                int nextTier = trust + 1;
                string tag;
                if (nextTier == 1) tag = "{=bp_hideoutslaverdialog_028}<span style=\"Conversation.Persuasion.Positive\">(Easy)</span>";
                else if (nextTier == 2) tag = "{=bp_hideoutslaverdialog_029}<span style=\"Conversation.Persuasion.Negative\">(Hard)</span>";
                else if (nextTier == 3) tag = "{=bp_hideoutslaverdialog_030}<span style=\"Conversation.Persuasion.Negative\">(Extremly Hard)</span>";
                else tag = "";
                MBTextManager.SetTextVariable("BP_SLAVER_QUEST_DIFFICULTY_TAG", tag);
                return true;
            }
            catch { return false; }
        }

        // True when the player has an active BanditSlaverQuest for THIS culture AND all
        // discrete objectives in its journal are at full progress. Caches the matching
        // quest in _completableQuest for the consequence delegate.
        private static bool IsSlaverQuestComplete()
        {
            try
            {
                if (!HideoutPeacefulVisitState.Active) return false;
                var spawnBeh = Campaign.Current?.GetCampaignBehavior<HideoutVisitNpcSpawnBehavior>();
                if (spawnBeh == null || spawnBeh.IsGreeterDispatchInFlight()) return false;
                var partner = ResolvePartner();
                if (partner == null || !spawnBeh.IsAgentSlaverVendor(partner)) return false;

                var hideout = HideoutPeacefulVisitState.TargetHideout;
                var cultureId = hideout?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return false;
                if (!SlaverProfiles.ByCultureId.TryGetValue(cultureId, out var profile)) return false;

                var quests = Campaign.Current?.QuestManager?.Quests;
                if (quests == null) return false;
                foreach (var q in quests)
                {
                    if (!(q is BanditSlaverQuest sq) || sq.CultureId != cultureId) continue;
                    bool allDone = true;
                    bool hasAnyDiscrete = false;
                    foreach (var e in sq.JournalEntries)
                    {
                        if (e == null || e.Range <= 0) continue;
                        hasAnyDiscrete = true;
                        if (e.CurrentProgress < e.Range) { allDone = false; break; }
                    }
                    if (!hasAnyDiscrete || !allDone) continue;

                    _completableQuest = sq;
                    int tier = sq.QuestTier;
                    var spec = tier switch { 1 => profile.QuestT1, 2 => profile.QuestT2, 3 => profile.QuestT3, _ => null };
                    if (spec != null)
                        MBTextManager.SetTextVariable("SLAVER_QUEST_THANKS", spec.ThanksText);
                    // 2026-06-06 Wave 4.13: speaker-label override on turn-in branch too.
                    SetSlaverIdentity(partner, profile);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("IsSlaverQuestComplete EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // Sets SLAVER_QUEST_INTRO from the next-tier compound quest spec.
        private static bool SetSlaverQuestIntroText()
        {
            try
            {
                var hideout = HideoutPeacefulVisitState.TargetHideout;
                var cultureId = hideout?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return false;
                if (!SlaverProfiles.ByCultureId.TryGetValue(cultureId, out var profile)) return false;
                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                int currentTrust = bdm?.GetSlaverTrust(cultureId) ?? 0;
                int nextTier = currentTrust + 1;
                SlaverCompoundQuest spec = nextTier switch
                {
                    1 => profile.QuestT1,
                    2 => profile.QuestT2,
                    3 => profile.QuestT3,
                    _ => null,
                };
                if (spec == null) return false;
                MBTextManager.SetTextVariable("SLAVER_QUEST_INTRO", spec.IntroText);
                // 2026-06-06 Wave 4.13: speaker-label override on quest-offer branch.
                // Resolve partner here since this condition doesn't otherwise need it;
                // skip silently if partner can't be resolved (text variable still set).
                try
                {
                    var spawnBeh = Campaign.Current?.GetCampaignBehavior<HideoutVisitNpcSpawnBehavior>();
                    var partner = ResolvePartner();
                    if (spawnBeh != null && partner != null && spawnBeh.IsAgentSlaverVendor(partner))
                        SetSlaverIdentity(partner, profile);
                }
                catch { /* defensive — override is cosmetic, never block dialog on resolve failure */ }
                return true;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("SetSlaverQuestIntroText EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // ===================================================================
        // Consequences
        // ===================================================================

        private static void OnAcceptSlaverQuest()
        {
            try
            {
                var hideout = HideoutPeacefulVisitState.TargetHideout;
                var cultureId = hideout?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return;
                if (!SlaverProfiles.ByCultureId.TryGetValue(cultureId, out var profile)) return;
                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                int currentTrust = bdm?.GetSlaverTrust(cultureId) ?? 0;
                int nextTier = currentTrust + 1;
                SlaverCompoundQuest spec = nextTier switch
                {
                    1 => profile.QuestT1,
                    2 => profile.QuestT2,
                    3 => profile.QuestT3,
                    _ => null,
                };
                if (spec == null) return;

                int killTarget = 0; string killFilter = null;
                string captureTargetId = null;
                int prisonersTarget = 0;
                string materialsItemId = null; int materialsTarget = 0;
                foreach (var obj in spec.Objectives)
                {
                    switch (obj.Kind)
                    {
                        case ObjectiveKind.Kill:
                            killTarget = obj.Count; killFilter = obj.CultureFilter; break;
                        case ObjectiveKind.Capture:
                            captureTargetId = obj.TargetId == "RUNTIME_PICK"
                                ? PickCaptureTarget(killFilter ?? "*") : obj.TargetId;
                            break;
                        case ObjectiveKind.Prisoners:
                            prisonersTarget = obj.Count; break;
                        case ObjectiveKind.Materials:
                            materialsItemId = obj.TargetId; materialsTarget = obj.Count; break;
                    }
                }

                var slaverHero = BandItPlus.Heroes.SlaverHeroRegistry.Instance?.GetSlaver(cultureId);
                var questId = "bp_slaver_" + cultureId + "_t" + nextTier + "_" + (long)CampaignTime.Now.ToHours;
                var quest = new BanditSlaverQuest(
                    questId, slaverHero, CampaignTime.Never, spec.RewardGold, cultureId, nextTier,
                    killTarget, killFilter,
                    captureTargetId,
                    prisonersTarget,
                    materialsItemId, materialsTarget);

                // StartQuest runs RegisterEvents — kill/battle detectors live
                // same-session (07-03 signature-quest lesson).
                quest.StartQuest();
                quest.InitJournalEntries();
                bdm?.SetActiveSlaverQuestTier(cultureId, nextTier);

                HideoutPeacefulVisitState.Log("Slaver quest spawned: tier " + nextTier + " for " + cultureId);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("OnAcceptSlaverQuest EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void OnTurnInSlaverQuest()
        {
            try
            {
                if (_completableQuest == null) return;
                _completableQuest.FinishWithSuccess();
                _completableQuest = null;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("OnTurnInSlaverQuest EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void OnShowTroops()
        {
            try
            {
                var hideout = HideoutPeacefulVisitState.TargetHideout;
                var cultureId = hideout?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return;
                if (!SlaverProfiles.ByCultureId.TryGetValue(cultureId, out var profile)) return;
                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                int trust = bdm?.GetSlaverTrust(cultureId) ?? 0;

                // Build cumulative troop pool from profile based on trust tier.
                var pool = new List<CharacterObject>();
                // 2026-06-06 Wave 4.13.8 — Option B: T1 troops available at trust 0
                // so new players can recruit immediately without doing a quest first.
                // T2/T3 still gated by trust 2/3 to preserve the progression curve.
                if (trust >= 0 && profile.TroopT1 != null)
                    foreach (var id in profile.TroopT1)
                    {
                        var c = MBObjectManager.Instance.GetObject<CharacterObject>(id);
                        if (c != null) pool.Add(c);
                    }
                if (trust >= 2 && profile.TroopT2 != null)
                    foreach (var id in profile.TroopT2)
                    {
                        var c = MBObjectManager.Instance.GetObject<CharacterObject>(id);
                        if (c != null) pool.Add(c);
                    }
                if (trust >= 3 && profile.TroopT3 != null)
                    foreach (var id in profile.TroopT3)
                    {
                        var c = MBObjectManager.Instance.GetObject<CharacterObject>(id);
                        if (c != null) pool.Add(c);
                    }

                // 2026-06-06 Wave 4.13.8 — Option C: empty pool is now a guarded path.
                // The AddPlayerLine for show_troops is gated by HasSlaverTroopsAvailable
                // so this consequence only fires when pool is non-empty. Defensive guard
                // kept for race conditions / missing CharacterObject IDs.
                if (pool.Count == 0) return;

                int recruited = 0; int totalCost = 0;
                var recruitLines = new System.Text.StringBuilder();
                foreach (var ch in pool)
                {
                    if (recruited >= 3) break;
                    int cost = 100 * (int)ch.Tier;
                    if (Hero.MainHero == null || Hero.MainHero.Gold < totalCost + cost) break;
                    PartyBase.MainParty.MemberRoster.AddToCounts(ch, 1);
                    totalCost += cost;
                    recruited++;
                    recruitLines.AppendLine(new TextObject("{=bp_hcs_002}  • {NAME} (T{TIER}, {COST} denars)")
                        .SetTextVariable("NAME", ch.Name)
                        .SetTextVariable("TIER", (int)ch.Tier)
                        .SetTextVariable("COST", cost).ToString());
                }

                if (recruited == 0)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        BandItPlus.Localization.Get("bp_hideoutslaverdialog_031", "[BandIt Plus] Not enough denars for the slaver's fighters."), Colors.Yellow));
                    return;
                }

                GiveGoldAction.ApplyBetweenCharacters(Hero.MainHero, null, totalCost, true);
                InformationManager.DisplayMessage(new InformationMessage(
                    new TextObject("{=bp_hcs_001}[BandIt Plus] Slaver's fighters joined your party (-{COST} denars):\n{LIST}")
                        .SetTextVariable("COST", totalCost)
                        .SetTextVariable("LIST", recruitLines.ToString().TrimEnd()).ToString(), Colors.Green));
                HideoutPeacefulVisitState.Log("Slaver troop recruit: " + recruited + " troops, " + totalCost + " denars for " + cultureId);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("OnShowTroops EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void OnShowHeroes()
        {
            try
            {
                var hideout = HideoutPeacefulVisitState.TargetHideout;
                var cultureId = hideout?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return;
                if (!SlaverProfiles.ByCultureId.TryGetValue(cultureId, out var profile)) return;
                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                int trust = bdm?.GetSlaverTrust(cultureId) ?? 0;

                var heroes = BuildSlaverHeroPool(profile, trust);
                // 2026-06-06 Wave 4.13.8 — Option C: same as troops. Player line gated
                // by HasSlaverHeroesAvailable so empty case routes through chat-native
                // explanation. Defensive guard kept for race conditions.
                if (heroes.Count == 0) return;

                var elements = new List<InquiryElement>();
                foreach (var h in heroes)
                {
                    int cost = GetHeroCost(h, profile, trust);
                    string label = new TextObject("{=bp_hcs_003}{NAME} — {COST} denars")
                        .SetTextVariable("NAME", h.Name?.ToString() ?? BandItPlus.Localization.Get("bp_hideoutslaverdialog_032", "Unknown"))
                        .SetTextVariable("COST", cost).ToString();
                    elements.Add(new InquiryElement(h, label, null));
                }

                var data = new MultiSelectionInquiryData(
                    titleText: new TextObject("{=bp_hcs_004}{NAME}'s Captives").SetTextVariable("NAME", profile.Name).ToString(),
                    descriptionText: BandItPlus.Localization.Get("bp_hideoutslaverdialog_033", "Pick one. Coin first, questions never."),
                    inquiryElements: elements,
                    isExitShown: true,
                    minSelectableOptionCount: 0,
                    maxSelectableOptionCount: 1,
                    affirmativeText: BandItPlus.Localization.Get("bp_hideoutslaverdialog_034", "Buy"),
                    negativeText: BandItPlus.Localization.Get("bp_hideoutslaverdialog_035", "Leave"),
                    affirmativeAction: list => OnHeroPicked(
                        list != null && list.Count > 0 ? list[0].Identifier as Hero : null,
                        profile, trust),
                    negativeAction: null);

                MBInformationManager.ShowMultiSelectionInquiry(data, false);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("OnShowHeroes EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ===================================================================
        // Helpers
        // ===================================================================

        // ResolvePartner mirrors HideoutVendorDialog: uses the Harmony-captured fresh partner
        // from AgentInteractionPatch (per feedback_bannerlord_conversation_partner_capture.md
        // memory), falling back to GetLookAgent if the patch hasn't captured yet.
        private static Agent ResolvePartner()
        {
            return BandItPlus.Patches.AgentInteractionPatch.GetFreshPartner()
                ?? Mission.Current?.MainAgent?.GetLookAgent();
        }

        // Random pick from alive lords matching the culture filter. Returns null if no
        // candidates — the quest will then run without a capture objective for that tier.
        private static string PickCaptureTarget(string cultureFilter)
        {
            try
            {
                var candidates = new List<Hero>();
                foreach (var h in Hero.AllAliveHeroes)
                {
                    if (h == null || !h.IsLord || h.PartyBelongedTo == null || h.HomeSettlement == null) continue;
                    if (h.MapFaction?.IsBanditFaction != false) continue;
                    if (cultureFilter != "*" && h.Culture?.StringId != cultureFilter) continue;
                    candidates.Add(h);
                }
                if (candidates.Count == 0) return null;
                return candidates[MBRandom.RandomInt(candidates.Count)].StringId;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("PickCaptureTarget EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static List<Hero> BuildSlaverHeroPool(SlaverProfile profile, int trust)
        {
            var pool = new List<Hero>();

            // T1: wanderers matching profile.T1HeroPool occupation IDs.
            // 2026-06-06 Wave 4.13.8 — Option B: T1 heroes available at trust 0
            // so new players can recruit a starting captive without questing first.
            if (trust >= 0 && profile.T1HeroPool != null)
            {
                foreach (var occId in profile.T1HeroPool)
                {
                    var charObj = MBObjectManager.Instance.GetObject<CharacterObject>(occId);
                    if (charObj == null) continue;
                    foreach (var h in Hero.AllAliveHeroes)
                    {
                        if (h?.CharacterObject == charObj && h.CompanionOf == null) { pool.Add(h); break; }
                    }
                }
            }

            // T2: real captured lords currently in the world's prisoner pool, matching tier filter.
            if (trust >= 2 && !string.IsNullOrEmpty(profile.T2PrisonerCriteria))
            {
                int added = 0;
                foreach (var h in Hero.AllAliveHeroes)
                {
                    if (added >= 2) break;
                    if (h == null || !h.IsPrisoner) continue;
                    if (h.Clan != null && h.Clan.Tier >= 2) { pool.Add(h); added++; }
                }
            }

            // T3: unique named hero from registry (Phase 7.1 expands the stub to populate it).
            if (trust >= 3 && profile.T3UniqueHero != null)
            {
                var unique = BandItPlus.Heroes.SlaverHeroRegistry.Instance?.GetT3Hero(profile.CultureId);
                if (unique != null && unique.IsAlive && unique.CompanionOf == null) pool.Add(unique);
            }

            return pool;
        }

        private static int GetHeroCost(Hero h, SlaverProfile profile, int trust)
        {
            if (h == null) return 0;
            if (h.IsPrisoner) return 3000 + (h.Clan?.Tier ?? 0) * 1000;  // T2 prisoner pricing
            if (profile.T3UniqueHero != null && h.Name?.ToString() == profile.T3UniqueHero.Name) return 8000;
            return 1500;  // T1 wanderer flat
        }

        // ===================================================================
        // Cellar narrative (Wave 4.12-fix, 2026-05-11)
        // ===================================================================

        // Available at slaver-trust >= 2: pure narrative peek (popup with CellarFlavor).
        private static bool IsCellarPeekAvailable()
        {
            try
            {
                var hideout = HideoutPeacefulVisitState.TargetHideout;
                var cultureId = hideout?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return false;
                if (!SlaverProfiles.ByCultureId.TryGetValue(cultureId, out var p)) return false;
                if (string.IsNullOrEmpty(p.CellarFlavor)) return false;
                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                return (bdm?.GetSlaverTrust(cultureId) ?? 0) >= 2;
            }
            catch { return false; }
        }

        // Available at slaver-trust >= 3: full walk-in. v1 ships as a richer menu-mode
        // experience (Bannerlord game-menu page with cellar prose + a "leave" action) —
        // this avoids depending on a custom 3D scene asset. Future wave can swap the
        // consequence to CampaignMission.OpenIndoorMission with a vanilla prison scene
        // for a true 3D walk-in.
        private static bool IsCellarWalkInAvailable()
        {
            try
            {
                var hideout = HideoutPeacefulVisitState.TargetHideout;
                var cultureId = hideout?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return false;
                if (!SlaverProfiles.ByCultureId.ContainsKey(cultureId)) return false;
                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                return (bdm?.GetSlaverTrust(cultureId) ?? 0) >= 3;
            }
            catch { return false; }
        }

        private static void OnPeekCellar()
        {
            try
            {
                var hideout = HideoutPeacefulVisitState.TargetHideout;
                var cultureId = hideout?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return;
                if (!SlaverProfiles.ByCultureId.TryGetValue(cultureId, out var profile)) return;
                if (string.IsNullOrEmpty(profile.CellarFlavor)) return;

                // Popup with cellar prose. Uses native InquiryData (vanilla style).
                var inq = new InquiryData(
                    new TextObject("{=bp_hcs_005}{NAME} — the Cellar").SetTextVariable("NAME", profile.Name).ToString(),
                    profile.CellarFlavor,
                    true, false, BandItPlus.Localization.Get("bp_hideoutslaverdialog_036", "I've seen enough."), null,
                    affirmativeAction: null,
                    negativeAction: null);
                InformationManager.ShowInquiry(inq, true);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("OnPeekCellar EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // v1 walk-in: shows the cellar prose in a popup with affirmative "Walk back up."
        // The mental model: the player has 'descended'; this is what they see down there.
        // Future wave can replace the body with CampaignMission.OpenIndoorMission for a
        // genuine 3D scene transition. Keeping the consequence shape stable makes that
        // future swap trivial — the dialog token stays the same.
        private static void OnWalkDownToCellar()
        {
            try
            {
                var hideout = HideoutPeacefulVisitState.TargetHideout;
                var cultureId = hideout?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return;
                if (!SlaverProfiles.ByCultureId.TryGetValue(cultureId, out var profile)) return;
                if (string.IsNullOrEmpty(profile.CellarFlavor)) return;

                string body =
                    new TextObject("{=bp_hcs_006}You follow the slaver down the wooden steps.").ToString() + "\n\n"
                    + "──────────────────────────────────\n\n"
                    + profile.CellarFlavor
                    + "\n\n──────────────────────────────────\n\n"
                    + new TextObject("{=bp_hcs_007}You climb back up. The hatch closes behind you.").ToString();

                var inq = new InquiryData(
                    new TextObject("{=bp_hcs_008}Into the Cellar — {NAME}").SetTextVariable("NAME", profile.Name).ToString(),
                    body,
                    true, false, BandItPlus.Localization.Get("bp_hideoutslaverdialog_037", "Walk back up."), null,
                    affirmativeAction: null,
                    negativeAction: null);
                InformationManager.ShowInquiry(inq, true);
                HideoutPeacefulVisitState.Log("Slaver cellar walk-in for " + cultureId);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("OnWalkDownToCellar EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ===================================================================
        // Chat / story dialog (Wave 4.12-fix10, 2026-05-11)
        // ===================================================================

        // 2026-06-06 Wave 4.13.2 — T1: dynamic culture-name substitution. Tokens
        // ({CULTURE_BAT}, {CULTURE_STU}, {CULTURE_KHU}, {CULTURE_ASE}, {CULTURE_VLA},
        // {CULTURE_EMP}) in prose are resolved at runtime via MBObjectManager so mods
        // that rename cultures (ROT, BannerKings, custom culture mods) get the correct
        // adjective form. Vanilla fallback if the API lookup fails.
        private static string ApplyCultureSubstitutions(string body)
        {
            if (string.IsNullOrEmpty(body)) return body;
            try
            {
                var mgr = TaleWorlds.ObjectSystem.MBObjectManager.Instance;
                if (mgr == null) return body;
                string bat = mgr.GetObject<TaleWorlds.Core.BasicCultureObject>("battania")?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_hideoutslaverdialog_038", "Battanian");
                string stu = mgr.GetObject<TaleWorlds.Core.BasicCultureObject>("sturgia")?.Name?.ToString()  ?? BandItPlus.Localization.Get("bp_hideoutslaverdialog_039", "Sturgian");
                string khu = mgr.GetObject<TaleWorlds.Core.BasicCultureObject>("khuzait")?.Name?.ToString()  ?? BandItPlus.Localization.Get("bp_hideoutslaverdialog_040", "Khuzait");
                string ase = mgr.GetObject<TaleWorlds.Core.BasicCultureObject>("aserai")?.Name?.ToString()   ?? BandItPlus.Localization.Get("bp_hideoutslaverdialog_041", "Aserai");
                string vla = mgr.GetObject<TaleWorlds.Core.BasicCultureObject>("vlandia")?.Name?.ToString()  ?? BandItPlus.Localization.Get("bp_hideoutslaverdialog_042", "Vlandian");
                string emp = mgr.GetObject<TaleWorlds.Core.BasicCultureObject>("empire")?.Name?.ToString()   ?? BandItPlus.Localization.Get("bp_hideoutslaverdialog_043", "Imperial");
                body = body.Replace("{CULTURE_BAT}", bat)
                           .Replace("{CULTURE_STU}", stu)
                           .Replace("{CULTURE_KHU}", khu)
                           .Replace("{CULTURE_ASE}", ase)
                           .Replace("{CULTURE_VLA}", vla)
                           .Replace("{CULTURE_EMP}", emp);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("ApplyCultureSubstitutions EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
            return body;
        }

        // 2026-06-06 Wave 4.13.1: paragraph-chunked inline dialog. Body prose is split
        // on \n\n into paragraphs; one paragraph shown per turn. Player picks "..." to
        // advance, "I see." to exit. Index + cache reset on entry to topic.
        private static int _backstoryParaIdx = 0;
        private static string[] _backstoryParas = null;

        private static void ResetSlaverBackstoryState()
        {
            _backstoryParaIdx = 0;
            _backstoryParas = null;
        }

        private static bool SetSlaverBackstoryText()
        {
            try
            {
                if (_backstoryParas == null)
                {
                    var hideout = HideoutPeacefulVisitState.TargetHideout;
                    var cultureId = hideout?.Culture?.StringId;
                    if (string.IsNullOrEmpty(cultureId)) return false;
                    if (!SlaverProfiles.ByCultureId.TryGetValue(cultureId, out var profile)) return false;

                    string body = !string.IsNullOrEmpty(profile.SelfStoryBody)
                        ? profile.SelfStoryBody
                        : (!string.IsNullOrEmpty(profile.Backstory)
                            ? profile.Backstory
                            : BandItPlus.Localization.Get("bp_hideoutslaverdialog_044", "My past is mine. Yours is yours. Coin is what we share."));

                    body = ApplyCultureSubstitutions(body);
                    _backstoryParas = body.Split(new[] { "\n\n" }, System.StringSplitOptions.RemoveEmptyEntries);
                    _backstoryParaIdx = 0;
                }

                if (_backstoryParas == null || _backstoryParas.Length == 0) return false;
                if (_backstoryParaIdx >= _backstoryParas.Length) _backstoryParaIdx = _backstoryParas.Length - 1;

                MBTextManager.SetTextVariable("SLAVER_BACKSTORY_TEXT", _backstoryParas[_backstoryParaIdx]);
                return true;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("SetSlaverBackstoryText EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static bool HasMoreBackstoryParas()
        {
            return _backstoryParas != null && (_backstoryParaIdx + 1) < _backstoryParas.Length;
        }

        private static void AdvanceBackstoryPara()
        {
            _backstoryParaIdx++;
        }

        // 2026-06-06 Wave 4.13.1: paragraph-chunked same as backstory.
        private static int _newsParaIdx = 0;
        private static string[] _newsParas = null;

        private static void ResetSlaverNewsState()
        {
            _newsParaIdx = 0;
            _newsParas = null;
        }

        private static bool SetSlaverNewsText()
        {
            try
            {
                if (_newsParas == null)
                {
                    var hideout = HideoutPeacefulVisitState.TargetHideout;
                    var cultureId = hideout?.Culture?.StringId;
                    var profile = !string.IsNullOrEmpty(cultureId)
                        && SlaverProfiles.ByCultureId.TryGetValue(cultureId, out var p) ? p : null;

                    string body;
                    if (profile != null && !string.IsNullOrEmpty(profile.NewsRumorsBody))
                    {
                        body = profile.NewsRumorsBody;
                    }
                    else
                    {
                        if (SlaverRumors == null || SlaverRumors.Length == 0) return false;
                        body = SlaverRumors[MBRandom.RandomInt(SlaverRumors.Length)];
                    }

                    body = ApplyCultureSubstitutions(body);
                    _newsParas = body.Split(new[] { "\n\n" }, System.StringSplitOptions.RemoveEmptyEntries);
                    _newsParaIdx = 0;
                }

                if (_newsParas == null || _newsParas.Length == 0) return false;
                if (_newsParaIdx >= _newsParas.Length) _newsParaIdx = _newsParas.Length - 1;

                MBTextManager.SetTextVariable("SLAVER_NEWS_TEXT", _newsParas[_newsParaIdx]);
                return true;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("SetSlaverNewsText EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static bool HasMoreNewsParas()
        {
            return _newsParas != null && (_newsParaIdx + 1) < _newsParas.Length;
        }

        private static void AdvanceNewsPara()
        {
            _newsParaIdx++;
        }

        // Shared rumor pool — the slaver's network of contacts brings in news. Pick is
        // random each visit; daily cooldown not enforced (low cost — just text).
        private static readonly string[] SlaverRumors = new[]
        {
            BandItPlus.Localization.Get("bp_hideoutslaverdialog_045", "\"A noblewoman went missing on the road south of Pravend three nights past. Her own brother put the bounty up. Her own brother — think about that.\""),
            BandItPlus.Localization.Get("bp_hideoutslaverdialog_046", "\"There's a Sturgian thane keeps a private dungeon under his hall. Rotates the prisoners every six months. The ones who go out the back gate don't come home.\""),
            BandItPlus.Localization.Get("bp_hideoutslaverdialog_047", "\"Vlandian knight-orders are buying scribes now. Doesn't sound like much — but scribes know who has debts, and debts buy themselves out one way or another.\""),
            BandItPlus.Localization.Get("bp_hideoutslaverdialog_048", "\"Old trick coming back into fashion: hire mercenaries to lose a fight on purpose. Loser's prisoners walk free... straight into the hands of the man who arranged it.\""),
            BandItPlus.Localization.Get("bp_hideoutslaverdialog_049", "\"Khuzait khans have started taking hostages from each other again. Means civil war's two summers away. Means prisoner prices triple by autumn.\""),
            BandItPlus.Localization.Get("bp_hideoutslaverdialog_050", "\"Empire deserter sergeant was hanging around the south road two weeks ago looking to sell his unit's secrets. Imperial scouts found him last week — in pieces.\""),
            BandItPlus.Localization.Get("bp_hideoutslaverdialog_051", "\"Heard a story about a young lord whose father sold him at twelve to pay a gambling debt. Boy grew up, paid the debt back to his own ledger, bought the family back. Don't know if he kept the rest of them slaves or freed them. Maybe both.\""),
            BandItPlus.Localization.Get("bp_hideoutslaverdialog_052", "\"Trick of the trade: the rich don't fetch the best prices. The HALF-rich do — the ones whose families will scrape together everything to ransom them, but can't quite afford it. They pay double.\""),
            BandItPlus.Localization.Get("bp_hideoutslaverdialog_053", "\"Battanian highlands have a saying: 'A man with one chain has one master. A man with no chain has two — the one who watches and the one who tells him where to look.'\""),
            BandItPlus.Localization.Get("bp_hideoutslaverdialog_054", "\"Aserai have a thing called a marriage-debt — woman's family pays the groom if the marriage ends. Some women arrange the divorce and split the gold. Some don't get the chance.\""),
            BandItPlus.Localization.Get("bp_hideoutslaverdialog_055", "\"The Empress's tax-collectors don't return from the Battanian woods anymore. Whether they're dead or just rich, no one says.\""),
            BandItPlus.Localization.Get("bp_hideoutslaverdialog_056", "\"Sturgian ice-fisher told me last winter the salt-cod stocks have collapsed. By the time you see famine in their cities, the slave-block already knows. They sell themselves to feed their kin. We don't even have to raid.\""),
        };

        // ===================================================================

        // Hero recruitment: handle 3 paths (T1 wanderer / T2 prisoner / T3 named).
        // Uses low-level Clan + MemberRoster API for 1.2/1.3 compatibility — avoids
        // depending on AddCompanionAction signature stability across versions.
        private static void OnHeroPicked(Hero picked, SlaverProfile profile, int trust)
        {
            try
            {
                if (picked == null || Hero.MainHero == null) return;
                int cost = GetHeroCost(picked, profile, trust);
                if (Hero.MainHero.Gold < cost)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        new TextObject("{=bp_hcs_009}[BandIt Plus] Not enough denars ({COST} required, {GOLD} on hand).")
                            .SetTextVariable("COST", cost)
                            .SetTextVariable("GOLD", Hero.MainHero.Gold).ToString(),
                        Colors.Yellow));
                    return;
                }
                GiveGoldAction.ApplyBetweenCharacters(Hero.MainHero, null, cost, true);

                if (picked.IsPrisoner)
                {
                    // T2 path: release prisoner state, adopt into player clan, add to party.
                    picked.ChangeState(Hero.CharacterStates.Active);
                }
                // Adopt into player clan if not already a companion.
                if (picked.Clan != Clan.PlayerClan)
                {
                    try { picked.Clan = Clan.PlayerClan; } catch { /* some Heroes are clan-locked; ignore */ }
                }
                // Add to main party member roster.
                PartyBase.MainParty.MemberRoster.AddToCounts(picked.CharacterObject, 1);
                try { picked.SetHasMet(); } catch { }

                InformationManager.DisplayMessage(new InformationMessage(
                    new TextObject("{=bp_hcs_010}[BandIt Plus] {NAME} walks free with you. -{COST} denars.")
                        .SetTextVariable("NAME", picked.Name)
                        .SetTextVariable("COST", cost).ToString(),
                    Colors.Green));
                HideoutPeacefulVisitState.Log("Slaver hero recruited: " + picked.StringId + " for " + cost + " denars");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("OnHeroPicked EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
