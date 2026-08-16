using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.MountAndBlade;

namespace BandItPlus.HideoutVisit
{
    // Pass C — first-visit dialog tree fired when player initiates conversation with a
    // bandit during peaceful walkable hideout visit. Lines themed for medieval bandit camp:
    // suspicion, gruff hospitality, hierarchy (chief at center, sentries at perimeter).
    //
    // Token state machine:
    //   "start" → bp_hideout_greet_root  [condition: peaceful flag active]
    //     Bandit: "Hold there, stranger! What brings you to our camp?"
    //   Player options at bp_hideout_greet_root:
    //     1. "Just passing through, friend." → bp_hideout_greet_passing → close_window
    //     2. "I came to speak with your chief." → bp_hideout_greet_chief_resp → close_window
    //     3. "I came to trade." → bp_hideout_greet_trade_resp → close_window
    //     4. "None of your concern." → bp_hideout_greet_aggro_resp → close_window
    //     5. "Just looking around." → bp_hideout_greet_curious_resp → close_window
    //
    // Priority 200 (higher than vanilla 100 + the existing bp_encounter_start at default
    // priority) ensures our line wins when both could match — i.e., during peaceful hideout.
    public class HideoutGreeterDialog : CampaignBehaviorBase
    {
        private const int kPriority = 200;

        // BUG M17 FIX (2026-04-30, REVISED 2026-04-30): per-CampaignGameStarter idempotency.
        // Initial fix used a process-static `bool _registered` which broke save reload —
        // Bannerlord builds a NEW CampaignGameStarter for each save-load cycle, so gating on
        // a process flag meant the second session got ZERO dialog tokens registered (greeter
        // never fired regression observed 2026-04-30). Fix: track the starter REFERENCE we
        // last registered with. New starter → register fresh. Same starter → skip.
        private static CampaignGameStarter _registeredStarter;

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        }

        public override void SyncData(IDataStore dataStore) { }

        // PARTNER-IDENTITY GATE (2026-04-30, REVISED): the auto-dispatch path
        // (TickGreeterIntercept → TryStartGuardConversation) calls OnAgentInteraction with
        // the guard as partner, but the player's look-direction may not be at the guard at
        // that exact instant — they may still be facing the way they were walking. So
        // GetLookAgent could return wrong/null and greeter would fall through to vanilla's
        // "I'm not allowed to talk to you" fallback. Fix: during dispatch, greeter wins
        // unconditionally. After dispatch (player explicitly clicks an NPC), use look-agent
        // identity check to scope greeter to the actual dispatched guard.
        private static bool IsPeacefulHideoutGreeter()
        {
            try
            {
                if (!HideoutPeacefulVisitState.Active) return false;
                var beh = Campaign.Current?.GetCampaignBehavior<HideoutVisitNpcSpawnBehavior>();
                if (beh == null) return false;
                // Wave 4.11-fix v33 (2026-05-07): scope "always wins" STRICTLY to our
                // synchronous OnAgentInteraction call (`_invokingDispatch`). Previously
                // used IsGreeterDispatchInFlight() which returns true for the WIDE window
                // (_hasGreeted && !_conversationStarted) — 8-15s while guard runs to
                // player. During that window, if the player clicked the chief or a vendor,
                // the greeter dialog hijacked the convo because the condition returned
                // true regardless of partner identity. Fix: narrow gate to the sync flag,
                // and use identity check for everything else (including when player
                // clicks the dispatched guard manually).
                if (beh.IsCurrentlyInvokingDispatch()) return true;
                // Outside our sync-trigger call: only match if the conversation partner
                // is the dispatched guard. Wave 4.11-fix v36: canonical partner capture
                // via AgentInteractionPatch instead of GetLookAgent (reticle-drift fix).
                var partner = BandItPlus.Patches.AgentInteractionPatch.GetFreshPartner()
                              ?? Mission.Current?.MainAgent?.GetLookAgent();
                return partner != null && beh.IsAgentDispatchedGuard(partner);
            }
            catch { return false; }
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            // M17 (revised): per-starter idempotency. New starter = re-register tokens.
            if (_registeredStarter == starter)
            {
                HideoutPeacefulVisitState.Log("HideoutGreeterDialog: OnSessionLaunched skipped (dialog tokens already registered with this starter)");
                return;
            }
            _registeredStarter = starter;
            try
            {
                // Entry: bandit greets player suspiciously
                starter.AddDialogLine(
                    "bp_hideout_greet_start",
                    "start",
                    "bp_hideout_greet_root",
                    "{=bp_hideoutgreeterdialog_001}Hold there, stranger! What brings you to our camp? Speak quickly — the chief sent me to find out.",
                    new ConversationSentence.OnConditionDelegate(IsPeacefulHideoutGreeter),
                    null,
                    kPriority,
                    null);

                // Player options
                starter.AddPlayerLine(
                    "bp_hideout_greet_pass",
                    "bp_hideout_greet_root",
                    "bp_hideout_greet_passing",
                    "{=bp_hideoutgreeterdialog_002}Just passing through, friend. I'll be on my way.",
                    null, null, kPriority);

                starter.AddPlayerLine(
                    "bp_hideout_greet_chief",
                    "bp_hideout_greet_root",
                    "bp_hideout_greet_chief_resp",
                    "{=bp_hideoutgreeterdialog_003}I came to speak with your chief.",
                    null, null, kPriority);

                starter.AddPlayerLine(
                    "bp_hideout_greet_trade",
                    "bp_hideout_greet_root",
                    "bp_hideout_greet_trade_resp",
                    "{=bp_hideoutgreeterdialog_004}I came to trade. Coin in hand, no trouble.",
                    null, null, kPriority);

                starter.AddPlayerLine(
                    "bp_hideout_greet_aggro",
                    "bp_hideout_greet_root",
                    "bp_hideout_greet_aggro_resp",
                    "{=bp_hideoutgreeterdialog_005}None of your concern, dog. Step aside.",
                    null, null, kPriority);

                starter.AddPlayerLine(
                    "bp_hideout_greet_curious",
                    "bp_hideout_greet_root",
                    "bp_hideout_greet_curious_resp",
                    "{=bp_hideoutgreeterdialog_006}Just looking around. Curiosity, nothing more.",
                    null, null, kPriority);

                // Bandit responses to each option → close_window
                starter.AddDialogLine(
                    "bp_hideout_greet_passing_resp",
                    "bp_hideout_greet_passing",
                    "close_window",
                    "{=bp_hideoutgreeterdialog_007}Hmph. Pass through then, but quickly. We don't like guests who linger. Don't touch our things.",
                    null, null, kPriority);

                starter.AddDialogLine(
                    "bp_hideout_greet_chief_response",
                    "bp_hideout_greet_chief_resp",
                    "close_window",
                    "{=bp_hideoutgreeterdialog_008}The chief? He sits in the heart of the camp. Walk to him directly. Don't stray, don't speak to anyone you don't have to. He doesn't suffer fools.",
                    null, null, kPriority);

                starter.AddDialogLine(
                    "bp_hideout_greet_trade_response",
                    "bp_hideout_greet_trade_resp",
                    "close_window",
                    "{=bp_hideoutgreeterdialog_009}Trade? Find the seller by the fire. He sets the prices and there's no haggling — argue with him and you'll find a knife where your purse used to be.",
                    null, null, kPriority);

                starter.AddDialogLine(
                    "bp_hideout_greet_aggro_response",
                    "bp_hideout_greet_aggro_resp",
                    "close_window",
                    "{=bp_hideoutgreeterdialog_010}Mind your tongue, traveler. We've buried better men than you for less. Walk on. The chief is watching. Don't make me say it twice.",
                    null, null, kPriority);

                starter.AddDialogLine(
                    "bp_hideout_greet_curious_response",
                    "bp_hideout_greet_curious_resp",
                    "close_window",
                    "{=bp_hideoutgreeterdialog_011}Look all you want. Touch nothing. Steal nothing. The chief gave you that much grace because you walked in alone. Don't waste it.",
                    null, null, kPriority);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("HideoutGreeterDialog OnSessionLaunched EXCEPTION: " + ex.Message);
            }
        }
    }
}
