using System;
using System.Collections.Generic;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace BandItPlus.HideoutVisit
{
    // Vendor dialog + trade for peaceful walkable hideouts.
    //
    // Two vendors per camp: vendor[0] = FOOD, vendor[1] = GEAR. Type tracking is on
    // HideoutVisitNpcSpawnBehavior (`_foodVendor` / `_gearVendor` fields, accessed via the
    // public `IsAgentFoodVendor` / `IsAgentGearVendor` predicates).
    //
    // Dialog flow when player clicks a vendor:
    //   start → bp_vendor_food_root  (condition: peaceful flag + nearest active vendor is food)
    //         | bp_vendor_gear_root  (condition: peaceful flag + nearest active vendor is gear)
    //   → "Show me your wares"  → consequence opens vanilla trade screen with random roster
    //   → "Maybe later"          → close
    //
    // Inventory generation: random pull from biome-agnostic vanilla item pools each visit.
    public class HideoutVendorDialog : CampaignBehaviorBase
    {
        // Priority 150 — LOWER than HideoutGreeterDialog's 200. When player triggers
        // OnAgentInteraction (e.g. greeter guard reaches them), the dialog system evaluates
        // all matching `start` lines in priority order. Greeter wins (200 > 150). Vendor
        // dialog only fires when greeter is NOT actively claiming the conversation.
        private const int kPriority = 150;
        private static CampaignGameStarter _registeredStarter;

        // Wave 4.11-fix v41 (2026-05-07): tiered inventory pools. Higher vendor-trust
        // unlocks larger pools with better items. T2 includes T1 items, T3 includes T2.
        // Picked count + quantity-per-item also scales with tier (see OpenTradeWith).
        // Wave 4.12-fix v64 (2026-05-12): Custom Order item pools — player picks one
        // via Gauntlet popup at vendor T2+. Premium item, kingdom-leader only.
        private const int kCustomOrderFoodCost = 1500;
        private const int kCustomOrderGearCost = 3000;
        private const float kCustomOrderDeliveryDays = 5f;

        // (itemId, qty, label) tuples — player sees label in the picker.
        // Wave 4.12-fix v67 (2026-05-12): expanded pools — food 13 entries,
        // gear 14 entries. All itemIds cross-referenced against the existing
        // _foodPool/_gearPool arrays in this file (no missing-itemId risk).
        // MultiSelectionInquiry auto-scrolls so scaling up doesn't break UI.
        private static readonly (string itemId, int qty, string label)[] _customOrderFoodPool = new[]
        {
            // Feast-grade luxuries
            ("wine",         30, BandItPlus.Localization.Get("bp_hideoutvendordialog_001", "Feast Wine — 30 jars")),
            ("beer",         50, BandItPlus.Localization.Get("bp_hideoutvendordialog_002", "Hall Beer — 50 casks")),
            ("olives",       50, BandItPlus.Localization.Get("bp_hideoutvendordialog_003", "Pickled Olives — 50 lots")),
            ("date_fruit",   50, BandItPlus.Localization.Get("bp_hideoutvendordialog_004", "Quyaz Dates — 50 lots")),
            // Preserved / pantry
            ("smoked_fish",  60, BandItPlus.Localization.Get("bp_hideoutvendordialog_005", "Smoked Fish — 60 strings")),
            ("salt",         50, BandItPlus.Localization.Get("bp_hideoutvendordialog_006", "Pravend Salt — 50 sacks")),
            ("cheese",       80, BandItPlus.Localization.Get("bp_hideoutvendordialog_007", "Aged Cheese — 80 wheels")),
            ("butter",       60, BandItPlus.Localization.Get("bp_hideoutvendordialog_008", "Churned Butter — 60 crocks")),
            // Bulk staples (kingdom-scale)
            ("grain",       200, BandItPlus.Localization.Get("bp_hideoutvendordialog_009", "Hall Grain — 200 sacks")),
            ("bread",       100, BandItPlus.Localization.Get("bp_hideoutvendordialog_010", "Feast Bread — 100 loaves")),
            ("meat",         80, BandItPlus.Localization.Get("bp_hideoutvendordialog_011", "Fresh Meat — 80 cuts")),
            ("chicken",      40, BandItPlus.Localization.Get("bp_hideoutvendordialog_012", "Live Chickens — 40 birds")),
            ("fish",         60, BandItPlus.Localization.Get("bp_hideoutvendordialog_013", "Fresh Catch — 60 fish")),
        };
        private static readonly (string itemId, int qty, string label)[] _customOrderGearPool = new[]
        {
            // T3-grade single pieces (named/premium)
            ("courser",            1, BandItPlus.Localization.Get("bp_hideoutvendordialog_014", "Trained Courser — 1")),
            ("two_handed_axe_t2",  1, BandItPlus.Localization.Get("bp_hideoutvendordialog_015", "Champion's Two-Handed Axe — 1")),
            ("iron_polearm_t2",    1, BandItPlus.Localization.Get("bp_hideoutvendordialog_016", "Captain's Iron Polearm — 1")),
            ("nasal_helmet",       1, BandItPlus.Localization.Get("bp_hideoutvendordialog_017", "Noble's Nasal Helm — 1")),
            ("leather_helm",       1, BandItPlus.Localization.Get("bp_hideoutvendordialog_018", "Officer's Leather Helm — 1")),
            // Companion-kit sets (small batches)
            ("hunting_bow",        2, BandItPlus.Localization.Get("bp_hideoutvendordialog_019", "Companion Hunting Bow — 2")),
            ("iron_sword_t1",      3, BandItPlus.Localization.Get("bp_hideoutvendordialog_020", "Iron Sword Set — 3")),
            ("throwing_daggers",   5, BandItPlus.Localization.Get("bp_hideoutvendordialog_021", "Throwing Dagger Bundle — 5")),
            ("padded_cap",         3, BandItPlus.Localization.Get("bp_hideoutvendordialog_022", "Padded Cap Set — 3")),
            ("wooden_shield_a",    3, BandItPlus.Localization.Get("bp_hideoutvendordialog_023", "Wooden Shield Set — 3")),
            // Retainer-kit (larger batches for outfitting troops)
            ("iron_arrows",        5, BandItPlus.Localization.Get("bp_hideoutvendordialog_024", "Iron Arrow Stack — 5")),
            ("leather_vest",       3, BandItPlus.Localization.Get("bp_hideoutvendordialog_025", "Leather Vest Set — 3")),
            ("leather_jerkin",     3, BandItPlus.Localization.Get("bp_hideoutvendordialog_026", "Leather Jerkin Set — 3")),
            ("leather_boots",      3, BandItPlus.Localization.Get("bp_hideoutvendordialog_027", "Leather Boot Set — 3")),
        };

        // FOOD T1 — basic staples. Bread, meat, fish, cheese, butter, grain.
        private static readonly string[] _foodPoolT1 = new[]
        {
            "grain", "bread", "meat", "fish", "cheese", "butter"
        };
        // FOOD T2 — T1 + preserved goods (olives, dates, salt, smoked fish, chicken, beer).
        private static readonly string[] _foodPoolT2 = new[]
        {
            "grain", "bread", "meat", "fish", "cheese", "butter",
            "olives", "date_fruit", "salt", "smoked_fish", "chicken", "beer"
        };
        // FOOD T3 — full pool: "everything food/drink" a vendor would stock at max trust.
        // Wave 4.14.5 (2026-06-07): expanded from 13 → 17 with vanilla food/drink items.
        // Random selection still picks a subset per visit so stock varies.
        // Unknown item IDs are silently skipped by OpenTradeWith (defensive null check).
        private static readonly string[] _foodPoolT3 = new[]
        {
            // Staples (T1)
            "grain", "bread", "meat", "fish", "cheese", "butter",
            // Preserved + protein (T2)
            "olives", "date_fruit", "salt", "smoked_fish", "chicken", "beer",
            // Premium / drink (T3 additions)
            "wine", "grape", "hardtack", "honey", "spice"
        };

        // Wave 4.14.6 (2026-06-07): gear pools restructured to be TIER-DISTINCT (not
        // cumulative). Each vendor tier stocks gear at its own level — a T3 elite vendor
        // never serves peasant training axes; a T1 starter vendor never serves mail.
        // Random selection within tier preserved. Unknown item IDs silently skipped.

        // GEAR T1 — peasant kit (low tier). Padded cloth, training weapons, raw footwear.
        private static readonly string[] _gearPoolT1 = new[]
        {
            "padded_cloth", "rough_tied_boots", "wrapped_shoes",
            "training_axe_t2", "wooden_sword_t1",
            "wooden_shield_a", "hunting_bow", "iron_arrows"
        };
        // GEAR T2 — soldier kit (mid tier). Iron weapons, leather armor, basic helmets.
        private static readonly string[] _gearPoolT2 = new[]
        {
            // Body + footwear
            "leather_jerkin", "leather_vest", "leather_boots",
            // Helmets
            "padded_cap", "leather_helm",
            // One-handed weapons
            "iron_sword_t1", "iron_mace_t2", "hatchet",
            // Ranged + thrown
            "throwing_daggers", "hunting_bow", "iron_arrows", "short_bow",
            // Shields
            "round_shield_basic",
            // Pack mount
            "mule", "pony", "sumpter_horse"
        };
        // GEAR T3 — elite kit (high tier ONLY, no peasant carry-overs).
        // Mail, lamellar, nasal/imperial helmets, mounted-warrior gear, two-handed weapons.
        private static readonly string[] _gearPoolT3 = new[]
        {
            // Body armor (mid → high tier)
            "fur_armor", "mail_hauberk", "mail_chausses",
            "leather_lamellar_armor", "padded_leather",
            // Helmets (high tier)
            "nasal_helmet", "imperial_helmet",
            // Two-handed + polearms
            "two_handed_axe_t2", "iron_polearm_t2", "battle_axe",
            // Ranged + thrown (high tier)
            "throwing_javelins", "crossbow_a", "bolts_a",
            // Shields (high tier)
            "kite_shield_basic",
            // War mounts
            "saddle_horse", "barb_horse"
        };

        // Pool selector — returns the right tier's pool for the trade screen build.
        private static string[] GetFoodPool(int tier)
        {
            if (tier >= 3) return _foodPoolT3;
            if (tier >= 2) return _foodPoolT2;
            return _foodPoolT1;
        }
        private static string[] GetGearPool(int tier)
        {
            if (tier >= 3) return _gearPoolT3;
            if (tier >= 2) return _gearPoolT2;
            return _gearPoolT1;
        }

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        }

        public override void SyncData(IDataStore dataStore) { }

        // PARTNER-IDENTITY GATE (Wave 4.11-fix v36, 2026-05-07): the canonical partner-Agent
        // is captured by AgentInteractionPatch (Harmony prefix on Mission.OnAgentInteraction).
        // GetLookAgent() proved unreliable — the reticle drifts off the partner mid-frame when
        // NPCs are stacked tight (chief + vendor 6.7m apart with bodyguards between). The
        // captured agent IS the agent the engine has bound as conversation partner; reading it
        // is deterministic. GetLookAgent() remains the fallback for any code path that
        // somehow bypasses our prefix.
        private static Agent ResolvePartner()
        {
            return BandItPlus.Patches.AgentInteractionPatch.GetFreshPartner()
                ?? Mission.Current?.MainAgent?.GetLookAgent();
        }

        // (v36 throttled DiagLog removed in v60 cleanup — kept the bug-fix logic that
        // routed all conditions through ResolvePartner / SetVendorIdentity, dropped the
        // log-spam helper now that the routing works.)
        private static void DiagLog(string method, Agent partner, HideoutVisitNpcSpawnBehavior beh, bool result)
        {
            // No-op stub kept so existing call sites compile. Re-enable by uncommenting
            // the v36 body if a future routing bug needs evidence.
        }

        private static bool IsFoodVendorConv()
        {
            try
            {
                if (!HideoutPeacefulVisitState.Active) return false;
                var beh = Campaign.Current?.GetCampaignBehavior<HideoutVisitNpcSpawnBehavior>();
                if (beh == null) return false;
                if (beh.IsGreeterDispatchInFlight()) return false;
                var partner = ResolvePartner();
                bool match = partner != null && beh.IsAgentFoodVendor(partner);
                DiagLog("IsFoodVendorConv", partner, beh, match);
                if (!match) return false;
                // Wave 5 (T34, 2026-05-31): allied chief bypasses vendor Trust gate.
                bool allied = IsAlliedHere(CurrentCultureId());
                // Wave 4.11-fix v41: vendor trade requires VENDOR's own trust >= 1, not
                // chief's trust. Chief tolerance gets you in the gate; the vendor's
                // bring-me-X quest gets you their goods.
                if (!allied && GetFoodVendorTrust() < 1) return false;
                // Wave 4.12-fix v61 (2026-05-11): bail when a vendor quest is active so
                // IsFoodVendorQuestActive (registered later, same priority) wins and the
                // "I have what you asked" deliver line becomes reachable. T1 worked by
                // accident because vendor_trust=0 during the gate quest; T2/T3 expose
                // the latent bug because trust is already 1 while the quest is active.
                // Mirrors the long-standing IsFoodVendorGate guard at line ~193.
                if (GetFoodVendorQuestTier() != 0) return false;
                SetVendorIdentity(partner, "food");                 // v43: per-culture name
                return true;
            }
            catch { return false; }
        }

        private static bool IsGearVendorConv()
        {
            try
            {
                if (!HideoutPeacefulVisitState.Active) return false;
                var beh = Campaign.Current?.GetCampaignBehavior<HideoutVisitNpcSpawnBehavior>();
                if (beh == null) return false;
                if (beh.IsGreeterDispatchInFlight()) return false;
                var partner = ResolvePartner();
                bool match = partner != null && beh.IsAgentGearVendor(partner);
                DiagLog("IsGearVendorConv", partner, beh, match);
                if (!match) return false;
                // Wave 5 (T34, 2026-05-31): allied chief bypasses both vendor Trust gates.
                bool allied = IsAlliedHere(CurrentCultureId());
                // Wave 4.12-fix12 (2026-05-11): gear vendor gated on food vendor Tier-2
                // completion. Narrative: the food-master vouches for outsiders; gear-master
                // only deals once that vouch has been earned. Suppresses both trade and
                // gate paths; the new rebuff line below explains the gate to the player.
                if (!allied && GetFoodVendorTrust() < 2) return false;
                if (!allied && GetGearVendorTrust() < 1) return false;
                // Wave 4.12-fix v61 (2026-05-11): same active-quest guard as the food
                // vendor — bail during active gear quest so IsGearVendorQuestActive wins
                // and the deliver dialog becomes reachable.
                if (GetGearVendorQuestTier() != 0) return false;
                SetVendorIdentity(partner, "gear");                 // v43: per-culture name
                return true;
            }
            catch { return false; }
        }

        // Wave 4.11-fix v41: gate condition. Fires when CHIEF trusts the player but the
        // vendor doesn't yet, AND no vendor quest is currently outstanding. This is the
        // "the chief can trust you, but I don't" moment — vendor offers their first
        // bring-me-X quest. Once player accepts, the quest tier is recorded and this
        // condition stops firing (replaced by the quest-active condition below).
        private static bool IsFoodVendorGate()
        {
            try
            {
                if (!HideoutPeacefulVisitState.Active) return false;
                var beh = Campaign.Current?.GetCampaignBehavior<HideoutVisitNpcSpawnBehavior>();
                if (beh == null) return false;
                if (beh.IsGreeterDispatchInFlight()) return false;
                var partner = ResolvePartner();
                if (partner == null || !beh.IsAgentFoodVendor(partner)) return false;
                if (!HasMinimumTrust(1)) return false;             // chief MUST trust
                if (GetFoodVendorTrust() >= 1) return false;       // but vendor doesn't yet
                if (GetFoodVendorQuestTier() != 0) return false;   // and no quest pending
                SetVendorIdentity(partner, "food");                 // v43: per-culture name + gate speech
                SetVendorQuestVariables("food", 1);                // populate {BP_VENDOR_QUEST_DESC}
                return true;
            }
            catch { return false; }
        }

        private static bool IsGearVendorGate()
        {
            try
            {
                if (!HideoutPeacefulVisitState.Active) return false;
                var beh = Campaign.Current?.GetCampaignBehavior<HideoutVisitNpcSpawnBehavior>();
                if (beh == null) return false;
                if (beh.IsGreeterDispatchInFlight()) return false;
                var partner = ResolvePartner();
                if (partner == null || !beh.IsAgentGearVendor(partner)) return false;
                if (!HasMinimumTrust(1)) return false;
                // Wave 4.12-fix12 (2026-05-11): gear gate requires food vendor T2 done.
                if (GetFoodVendorTrust() < 2) return false;
                if (GetGearVendorTrust() >= 1) return false;
                if (GetGearVendorQuestTier() != 0) return false;
                SetVendorIdentity(partner, "gear");                 // v43: per-culture name + gate speech
                SetVendorQuestVariables("gear", 1);
                return true;
            }
            catch { return false; }
        }

        // Quest-active condition. Fires when player has accepted a vendor quest but
        // hasn't delivered yet. The dialog body branches on whether items are in hand.
        private static bool IsFoodVendorQuestActive()
        {
            try
            {
                if (!HideoutPeacefulVisitState.Active) return false;
                var beh = Campaign.Current?.GetCampaignBehavior<HideoutVisitNpcSpawnBehavior>();
                if (beh == null) return false;
                if (beh.IsGreeterDispatchInFlight()) return false;
                var partner = ResolvePartner();
                if (partner == null || !beh.IsAgentFoodVendor(partner)) return false;
                int qt = GetFoodVendorQuestTier();
                if (qt <= 0) return false;
                SetVendorIdentity(partner, "food");                 // v43: per-culture name
                SetVendorQuestVariables("food", qt);
                return true;
            }
            catch { return false; }
        }

        private static bool IsGearVendorQuestActive()
        {
            try
            {
                if (!HideoutPeacefulVisitState.Active) return false;
                var beh = Campaign.Current?.GetCampaignBehavior<HideoutVisitNpcSpawnBehavior>();
                if (beh == null) return false;
                if (beh.IsGreeterDispatchInFlight()) return false;
                var partner = ResolvePartner();
                if (partner == null || !beh.IsAgentGearVendor(partner)) return false;
                int qt = GetGearVendorQuestTier();
                if (qt <= 0) return false;
                SetVendorIdentity(partner, "gear");                 // v43: per-culture name
                SetVendorQuestVariables("gear", qt);
                return true;
            }
            catch { return false; }
        }

        // Wave 4.11-fix v44 (2026-05-08): multi-item delivery check — ALL items in the
        // spec must be in player inventory for the deliver option to appear. Even one
        // missing item means "Soon" is the only path forward until the player gathers
        // the rest. This is what makes T2/T3 quests actually feel like "do multiple
        // gathers" rather than a single shopping run.
        private static bool HasFoodVendorQuestItems()
        {
            int qt = GetFoodVendorQuestTier();
            if (qt <= 0) return false;
            var spec = BandItPlus.BanditDialogManager.GetVendorQuestSpec("food", qt);
            if (spec == null || spec.Items == null || spec.Items.Length == 0) return false;
            foreach (var (itemId, count) in spec.Items)
            {
                if (string.IsNullOrEmpty(itemId)) return false;
                if (CountPlayerItem(itemId) < count) return false;
            }
            return true;
        }

        private static bool HasGearVendorQuestItems()
        {
            int qt = GetGearVendorQuestTier();
            if (qt <= 0) return false;
            var spec = BandItPlus.BanditDialogManager.GetVendorQuestSpec("gear", qt);
            if (spec == null || spec.Items == null || spec.Items.Length == 0) return false;
            foreach (var (itemId, count) in spec.Items)
            {
                if (string.IsNullOrEmpty(itemId)) return false;
                if (CountPlayerItem(itemId) < count) return false;
            }
            return true;
        }

        // Player-line condition for "Got another job for me?" — visible only when the
        // vendor has more tiers to climb AND no quest is currently outstanding.
        private static bool CanOfferNextFoodTier()
        {
            int t = GetFoodVendorTrust();
            return t >= 1 && t < 3 && GetFoodVendorQuestTier() == 0;
        }

        private static bool CanOfferNextGearTier()
        {
            int t = GetGearVendorTrust();
            return t >= 1 && t < 3 && GetGearVendorQuestTier() == 0;
        }

        // Wave 4.14 (2026-06-06): tier-aware difficulty tag for vendor quest offer
        // player lines. Returns the next-tier label wrapped in a Persuasion span so
        // the dialog menu renders it colored. Tier 2 → (Hard), Tier 3 → (Extremly Hard).
        // Preserves user's authored "Extremly Hard" spelling per memory.
        private static string FormatVendorDifficultyTag(int nextTier)
        {
            if (nextTier == 1) return BandItPlus.Localization.Get("bp_hideoutvendordialog_028", "<span style=\"Conversation.Persuasion.Positive\">(Easy)</span>");
            if (nextTier == 2) return BandItPlus.Localization.Get("bp_hideoutvendordialog_029", "<span style=\"Conversation.Persuasion.Negative\">(Hard)</span>");
            if (nextTier == 3) return BandItPlus.Localization.Get("bp_hideoutvendordialog_030", "<span style=\"Conversation.Persuasion.Negative\">(Extremly Hard)</span>");
            return "";
        }
        private static bool CanOfferNextFoodTierWithDifficulty()
        {
            if (!CanOfferNextFoodTier()) return false;
            int nextTier = GetFoodVendorTrust() + 1;
            MBTextManager.SetTextVariable("BP_VENDOR_QUEST_DIFFICULTY_TAG", FormatVendorDifficultyTag(nextTier));
            return true;
        }
        private static bool CanOfferNextGearTierWithDifficulty()
        {
            if (!CanOfferNextGearTier()) return false;
            int nextTier = GetGearVendorTrust() + 1;
            MBTextManager.SetTextVariable("BP_VENDOR_QUEST_DIFFICULTY_TAG", FormatVendorDifficultyTag(nextTier));
            return true;
        }

        // Wave 4.14.3 (2026-06-06): tier-aware completion. Gate-accept always fires at
        // nextTier=1 (first quest) so the wrapper sets (Easy). Deliver fires at the
        // ACTIVE quest tier — wrapper sets (Easy)/(Hard)/(Extremly Hard) by reading
        // GetFoodVendorQuestTier() / GetGearVendorQuestTier().
        private static bool IsFoodVendorGateWithDifficulty()
        {
            if (!IsFoodVendorGate()) return false;
            MBTextManager.SetTextVariable("BP_VENDOR_QUEST_DIFFICULTY_TAG", FormatVendorDifficultyTag(1));
            return true;
        }
        private static bool IsGearVendorGateWithDifficulty()
        {
            if (!IsGearVendorGate()) return false;
            MBTextManager.SetTextVariable("BP_VENDOR_QUEST_DIFFICULTY_TAG", FormatVendorDifficultyTag(1));
            return true;
        }
        private static bool HasFoodVendorQuestItemsWithDifficulty()
        {
            if (!HasFoodVendorQuestItems()) return false;
            MBTextManager.SetTextVariable("BP_VENDOR_QUEST_DIFFICULTY_TAG", FormatVendorDifficultyTag(GetFoodVendorQuestTier()));
            return true;
        }
        private static bool HasGearVendorQuestItemsWithDifficulty()
        {
            if (!HasGearVendorQuestItems()) return false;
            MBTextManager.SetTextVariable("BP_VENDOR_QUEST_DIFFICULTY_TAG", FormatVendorDifficultyTag(GetGearVendorQuestTier()));
            return true;
        }

        // Consequence delegates — accept quest, deliver quest, cancel quest. Each writes
        // to the BanditDialogManager dictionaries (which SyncData persists). Item removal
        // happens INSIDE the deliver consequences so trust + inventory update atomically.
        private static void AcceptFoodVendorQuest()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm == null || string.IsNullOrEmpty(cid)) return;
                int nextTier = bdm.GetFoodVendorTrust(cid) + 1;
                bdm.SetFoodVendorQuest(cid, nextTier);
                HideoutPeacefulVisitState.Log("VendorQuest: food T" + nextTier + " accepted for " + cid);
                // Wave 4.11-fix v52: spawn a BanditVendorQuest QuestBase so the player
                // sees the order in their journal with progress bars (mirrors chief quest UX).
                CreateVendorQuestBase(cid, nextTier, "food");
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("AcceptFoodVendorQuest fail: " + ex.Message); }
        }

        private static void AcceptGearVendorQuest()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm == null || string.IsNullOrEmpty(cid)) return;
                int nextTier = bdm.GetGearVendorTrust(cid) + 1;
                bdm.SetGearVendorQuest(cid, nextTier);
                HideoutPeacefulVisitState.Log("VendorQuest: gear T" + nextTier + " accepted for " + cid);
                CreateVendorQuestBase(cid, nextTier, "gear");
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("AcceptGearVendorQuest fail: " + ex.Message); }
        }

        // Wave 4.11-fix v52: shared QuestBase spawner. Mirrors BanditDialogManager.OnAcceptTrustQuest
        // canonical pattern — resolve chief Hero as questGiver (vendors aren't Heroes),
        // construct quest, register via QuestManager.OnQuestStarted, populate journal
        // entries via InitJournalEntries (must run AFTER registration or logs are lost).
        private static void CreateVendorQuestBase(string cultureId, int tier, string vendorKind)
        {
            try
            {
                var hideout = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement;
                if (hideout == null) return;
                // Resolve a Hero questGiver — chief if registered, else clan leader, else MainHero.
                Hero questGiver = null;
                try
                {
                    var chiefReg = BandItPlus.Heroes.BanditChiefRegistry.Instance;
                    questGiver = chiefReg?.GetChief(cultureId);
                }
                catch { }
                if (questGiver == null) questGiver = hideout.OwnerClan?.Leader;
                if (questGiver == null) questGiver = Hero.MainHero;

                string questId = "bp_vendor_" + vendorKind + "_" + cultureId + "_t" + tier
                    + "_" + (long)CampaignTime.Now.ToHours;
                var vq = new BandItPlus.Quests.BanditVendorQuest(
                    questId, questGiver, CampaignTime.Never, 0,
                    cultureId, tier, vendorKind);
                Campaign.Current?.QuestManager?.OnQuestStarted(vq);
                vq.InitJournalEntries();
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("CreateVendorQuestBase fail: " + ex.Message);
            }
        }

        // Wave 4.11-fix v52: find and finalize the matching BanditVendorQuest.
        // Returns true if a quest was found + completed; false if none was active
        // (e.g., dialog-only quest accepted in a pre-v52 save).
        private static bool FinalizeVendorQuestBase(string cultureId, string vendorKind)
        {
            try
            {
                var quests = Campaign.Current?.QuestManager?.Quests;
                if (quests == null) return false;
                foreach (var q in quests)
                {
                    if (q is BandItPlus.Quests.BanditVendorQuest vq
                        && vq.CultureId == cultureId
                        && vq.VendorKind == vendorKind)
                    {
                        vq.FinishWithSuccess();   // OnCompleteWithSuccess → ForceCompleteJournalProgress
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("FinalizeVendorQuestBase fail: " + ex.Message);
            }
            return false;
        }

        // Wave 4.11-fix v44 (2026-05-08): multi-item delivery — loops the spec, removes
        // each (itemId, count) pair from the player's roster atomically. HasItems check
        // ran first as the dialog gate, so by the time we get here ALL items are present.
        private static void DeliverFoodVendorQuest()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm == null || string.IsNullOrEmpty(cid)) return;
                int qt = bdm.GetFoodVendorQuestTier(cid);
                if (qt <= 0) return;
                var spec = BandItPlus.BanditDialogManager.GetVendorQuestSpec("food", qt);
                if (spec == null || spec.Items == null) return;
                var removeLog = new System.Text.StringBuilder();
                foreach (var (itemId, count) in spec.Items)
                {
                    RemovePlayerItem(itemId, count);
                    if (removeLog.Length > 0) removeLog.Append(", ");
                    removeLog.Append("-").Append(count).Append(" ").Append(itemId);
                }
                bdm.IncrementFoodVendorTrust(cid);
                bdm.ClearFoodVendorQuest(cid);
                FinalizeVendorQuestBase(cid, "food");   // v52: archive QuestBase journal entry
                HideoutPeacefulVisitState.Log("VendorQuest: food T" + qt + " DELIVERED for " + cid + " (" + removeLog + ")");
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("DeliverFoodVendorQuest fail: " + ex.Message); }
        }

        private static void DeliverGearVendorQuest()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm == null || string.IsNullOrEmpty(cid)) return;
                int qt = bdm.GetGearVendorQuestTier(cid);
                if (qt <= 0) return;
                var spec = BandItPlus.BanditDialogManager.GetVendorQuestSpec("gear", qt);
                if (spec == null || spec.Items == null) return;
                var removeLog = new System.Text.StringBuilder();
                foreach (var (itemId, count) in spec.Items)
                {
                    RemovePlayerItem(itemId, count);
                    if (removeLog.Length > 0) removeLog.Append(", ");
                    removeLog.Append("-").Append(count).Append(" ").Append(itemId);
                }
                bdm.IncrementGearVendorTrust(cid);
                bdm.ClearGearVendorQuest(cid);
                FinalizeVendorQuestBase(cid, "gear");   // v52: archive QuestBase journal entry
                HideoutPeacefulVisitState.Log("VendorQuest: gear T" + qt + " DELIVERED for " + cid + " (" + removeLog + ")");
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("DeliverGearVendorQuest fail: " + ex.Message); }
        }

        private static void CancelFoodVendorQuest()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm == null || string.IsNullOrEmpty(cid)) return;
                bdm.ClearFoodVendorQuest(cid);
                CancelVendorQuestBase(cid, "food");
            }
            catch { }
        }

        private static void CancelGearVendorQuest()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm == null || string.IsNullOrEmpty(cid)) return;
                bdm.ClearGearVendorQuest(cid);
                CancelVendorQuestBase(cid, "gear");
            }
            catch { }
        }

        // Wave 4.11-fix v52: cancel-side counterpart to FinalizeVendorQuestBase.
        // Removes the journal entry without success-archive (no N/N freeze).
        private static void CancelVendorQuestBase(string cultureId, string vendorKind)
        {
            try
            {
                var quests = Campaign.Current?.QuestManager?.Quests;
                if (quests == null) return;
                foreach (var q in quests)
                {
                    if (q is BandItPlus.Quests.BanditVendorQuest vq
                        && vq.CultureId == cultureId
                        && vq.VendorKind == vendorKind)
                    {
                        // QuestBase has CompleteQuestWithCancel as protected — use a public
                        // wrapper if available, else fall back to FinishWithSuccess. Cleaner
                        // to add a public wrapper later; for now success-finalize prevents
                        // a stuck active-quest entry.
                        vq.FinishWithSuccess();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("CancelVendorQuestBase fail: " + ex.Message);
            }
        }

        // Wave 4.6.4: surfaces the vendor's per-instance name in BOTH the speaker label
        // (via ConversationNamePatch CharacterObject.Name override) AND the dialog body
        // (via BP_SPEAKER_NAME text variable). Falls back to "Vendor" if no custom name.
        private static void SetSpeakerName(Agent partner)
        {
            try
            {
                string n = partner?.Name?.ToString();
                if (string.IsNullOrEmpty(n)) n = BandItPlus.Localization.Get("bp_hideoutvendordialog_031", "Vendor");
                var ch = partner?.Character as CharacterObject;
                if (ch != null)
                    BandItPlus.Patches.ConversationNamePatch.OverrideName(ch, n);
                MBTextManager.SetTextVariable("BP_SPEAKER_NAME", n);
            }
            catch { }
        }

        // Wave 4.11-fix v43 (2026-05-08): per-culture vendor identity. Looks up the
        // VendorProfile for the current culture+vendor-kind; uses profile.Name as the
        // speaker label (overriding the agent's random Bannerlord-pool name) and stores
        // profile.GateSpeech in BP_VENDOR_GATE_SPEECH for the gate dialog body. The
        // {BP_VENDOR_QUEST_DESC} placeholder embedded in profile.GateSpeech is resolved
        // inline (Bannerlord's text-variable system doesn't recursively expand vars
        // inside var values, so we do the substitution ourselves before storing).
        private static void SetVendorIdentity(Agent partner, string vendorKind)
        {
            try
            {
                var cid = CurrentCultureId();
                BandItPlus.Cultures.VendorProfile profile = null;
                if (!string.IsNullOrEmpty(cid))
                {
                    profile = vendorKind == "food"
                        ? BandItPlus.Cultures.VendorProfiles.GetFood(cid)
                        : BandItPlus.Cultures.VendorProfiles.GetGear(cid);
                }

                // Speaker name: profile-specific if available, else fall back to the
                // agent's per-instance random name (existing SetSpeakerName behavior).
                string speakerName = profile?.Name;
                if (string.IsNullOrEmpty(speakerName))
                    speakerName = partner?.Name?.ToString();
                if (string.IsNullOrEmpty(speakerName))
                    speakerName = BandItPlus.Localization.Get("bp_hideoutvendordialog_032", "Vendor");
                var ch = partner?.Character as CharacterObject;
                if (ch != null)
                    BandItPlus.Patches.ConversationNamePatch.OverrideName(ch, speakerName);
                MBTextManager.SetTextVariable("BP_SPEAKER_NAME", speakerName);

                // Gate speech: per-vendor voice if profile present, else generic fallback.
                // Substitute {BP_VENDOR_QUEST_DESC} inline (Bannerlord doesn't recursively
                // expand text-variables inside var values).
                int tier = (vendorKind == "food" ? GetFoodVendorTrust() : GetGearVendorTrust()) + 1;
                if (tier < 1) tier = 1;
                if (tier > 3) tier = 3;
                var spec = BandItPlus.BanditDialogManager.GetVendorQuestSpec(vendorKind, tier);
                string questDisplay = spec?.Display;
                string gateSpeech = profile?.GateSpeech
                    ?? BandItPlus.Localization.Get("bp_hideoutvendordialog_033", "Aye, the chief lets you walk our camp without spilling your blood. That's the chief's judgment, traveler — and the chief's mine to follow. But these are my goods, and I don't know you yet. Bring me {BP_VENDOR_QUEST_DESC} from the road, and I'll think about doing business with your coin. Until then — the stall's shut.");
                gateSpeech = gateSpeech.Replace("{BP_VENDOR_QUEST_DESC}", questDisplay ?? BandItPlus.Localization.Get("bp_hideoutvendordialog_034", "what we ask"));
                MBTextManager.SetTextVariable("BP_VENDOR_GATE_SPEECH", gateSpeech);

                // Backstory variable — surfaced for future encyclopedia / extended-dialog
                // hooks. Cheap to set; no current consumer if profile is null.
                if (!string.IsNullOrEmpty(profile?.Backstory))
                    MBTextManager.SetTextVariable("BP_VENDOR_BACKSTORY", profile.Backstory);
            }
            catch { }
        }

        // Wave 4.6: rebuff condition — vendor identified but trust below trade threshold.
        // Drives the "go away stranger" line so the player gets clear feedback instead of
        // a silent vendor.
        private static bool IsFoodVendorRebuff()
        {
            try
            {
                if (!HideoutPeacefulVisitState.Active) return false;
                var beh = Campaign.Current?.GetCampaignBehavior<HideoutVisitNpcSpawnBehavior>();
                if (beh == null) return false;
                if (beh.IsGreeterDispatchInFlight()) return false;
                var partner = ResolvePartner();
                bool match = partner != null && beh.IsAgentFoodVendor(partner) && !HasMinimumTrust(1);
                DiagLog("IsFoodVendorRebuff", partner, beh, match);
                if (!match) return false;
                SetSpeakerName(partner);
                return true;
            }
            catch { return false; }
        }

        private static bool IsGearVendorRebuff()
        {
            try
            {
                if (!HideoutPeacefulVisitState.Active) return false;
                var beh = Campaign.Current?.GetCampaignBehavior<HideoutVisitNpcSpawnBehavior>();
                if (beh == null) return false;
                if (beh.IsGreeterDispatchInFlight()) return false;
                var partner = ResolvePartner();
                bool match = partner != null && beh.IsAgentGearVendor(partner) && !HasMinimumTrust(1);
                DiagLog("IsGearVendorRebuff", partner, beh, match);
                if (!match) return false;
                SetSpeakerName(partner);
                return true;
            }
            catch { return false; }
        }

        // Wave 4.12-fix12 (2026-05-11): gear vendor requires food vendor Tier-2 done.
        // Fires when: partner is gear vendor AND chief trust earned AND food vendor trust < 2.
        // Higher dialog priority (kPriority + 5) so it wins over the standard rebuff and the
        // trade/gate paths (which are suppressed by the food-T2 check inside them).
        private static bool IsGearVendorNeedsFoodT2()
        {
            try
            {
                if (!HideoutPeacefulVisitState.Active) return false;
                var beh = Campaign.Current?.GetCampaignBehavior<HideoutVisitNpcSpawnBehavior>();
                if (beh == null) return false;
                if (beh.IsGreeterDispatchInFlight()) return false;
                var partner = ResolvePartner();
                if (partner == null || !beh.IsAgentGearVendor(partner)) return false;
                if (!HasMinimumTrust(1)) return false;          // chief trust earned
                if (GetFoodVendorTrust() >= 2) return false;    // food vendor T2 already done — let trade flow take over
                SetSpeakerName(partner);
                return true;
            }
            catch { return false; }
        }

        // Wave 4.6: looks up the player's CHIEF trust tier for the current hideout's
        // culture by delegating to BanditDialogManager (source of truth for trust state).
        // Wave 5 (T34, 2026-05-31): allied chief => trust gate bypassed (returns true
        // regardless of required tier). IsAllied is null-safe.
        private static bool HasMinimumTrust(int required)
        {
            try
            {
                var s = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement;
                string cid = s?.Culture?.StringId ?? s?.OwnerClan?.Culture?.StringId;
                if (string.IsNullOrEmpty(cid)) return false;
                if (IsAlliedHere(cid)) return true;          // T34 alliance bypass
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                if (bdm == null) return false;
                return bdm.GetCultureTrust(cid) >= required;
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] HasMinimumTrust threw "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // Wave 5 (T34, 2026-05-31): allied-chief predicate for the current culture.
        // Used as the bypass switch for Trust gates throughout the vendor flow.
        // IsAllied is null-safe (false on null/empty cultureId).
        private static bool IsAlliedHere(string cultureId)
        {
            try
            {
                if (string.IsNullOrEmpty(cultureId)) return false;
                var alliance = BandItPlus.Behaviors.BanditAllianceBehavior.Instance;
                return alliance != null && alliance.IsAllied(cultureId);
            }
            catch (Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] IsAlliedHere threw "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // Wave 4.11-fix v41 (2026-05-07): vendor-trust helpers. Mirror HasMinimumTrust
        // pattern but read the vendor-specific dictionaries on BanditDialogManager.
        private static string CurrentCultureId()
        {
            try
            {
                var s = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement;
                return s?.Culture?.StringId ?? s?.OwnerClan?.Culture?.StringId;
            }
            catch { return null; }
        }

        private static int GetFoodVendorTrust()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm == null || string.IsNullOrEmpty(cid)) return 0;
                return bdm.GetFoodVendorTrust(cid);
            }
            catch { return 0; }
        }

        private static int GetGearVendorTrust()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm == null || string.IsNullOrEmpty(cid)) return 0;
                return bdm.GetGearVendorTrust(cid);
            }
            catch { return 0; }
        }

        private static int GetFoodVendorQuestTier()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm == null || string.IsNullOrEmpty(cid)) return 0;
                return bdm.GetFoodVendorQuestTier(cid);
            }
            catch { return 0; }
        }

        private static int GetGearVendorQuestTier()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm == null || string.IsNullOrEmpty(cid)) return 0;
                return bdm.GetGearVendorQuestTier(cid);
            }
            catch { return 0; }
        }

        // Item count + remove helpers for vendor quest delivery. Reads from the player's
        // main party item roster (where bought/foraged items live during travel).
        private static int CountPlayerItem(string itemId)
        {
            try
            {
                if (string.IsNullOrEmpty(itemId)) return 0;
                var party = MobileParty.MainParty;
                if (party == null) return 0;
                var item = MBObjectManager.Instance.GetObject<ItemObject>(itemId);
                if (item == null) return 0;
                return party.ItemRoster.GetItemNumber(item);
            }
            catch { return 0; }
        }

        private static void RemovePlayerItem(string itemId, int count)
        {
            try
            {
                if (string.IsNullOrEmpty(itemId) || count <= 0) return;
                var party = MobileParty.MainParty;
                if (party == null) return;
                var item = MBObjectManager.Instance.GetObject<ItemObject>(itemId);
                if (item == null) return;
                party.ItemRoster.AddToCounts(item, -count);
            }
            catch { /* defensive — never let inventory removal throw mid-dialog */ }
        }

        // Sets the BP_VENDOR_QUEST_DESC text variable from BanditDialogManager's per-tier
        // spec. Returns true so it can be used as a no-op condition delegate for dialog
        // lines that need the variable populated before the line renders.
        private static bool SetVendorQuestVariables(string vendorKind, int tier)
        {
            try
            {
                var spec = BandItPlus.BanditDialogManager.GetVendorQuestSpec(vendorKind, tier);
                MBTextManager.SetTextVariable("BP_VENDOR_QUEST_DESC",
                    string.IsNullOrEmpty(spec?.Display) ? BandItPlus.Localization.Get("bp_hideoutvendordialog_035", "what we ask") : spec.Display);
            }
            catch { }
            return true;
        }

        // Heuristic: when OnAgentInteraction fires, the conversation partner is the agent the
        // player clicked. They'll be within ~3m. We loop the agents the player is near and
        // pick whichever is a tracked vendor. Simple and version-agnostic.
        private static Agent NearestVendorToPlayer(HideoutVisitNpcSpawnBehavior beh)
        {
            var mission = Mission.Current;
            if (mission == null) return null;
            var player = mission.MainAgent;
            if (player == null || !player.IsActive()) return null;

            Agent best = null;
            float bestSq = 9f; // 3m radius
            foreach (var ag in beh.GetLabelAgents())
            {
                if (ag == null || !ag.IsActive()) continue;
                if (!beh.IsAgentFoodVendor(ag) && !beh.IsAgentGearVendor(ag)) continue;
                var dSq = ag.Position.DistanceSquared(player.Position);
                if (dSq < bestSq) { bestSq = dSq; best = ag; }
            }
            return best;
        }

        // === Wave 4.11-fix v59 (2026-05-08) — vendor Tier 1/2/3 menu helpers ===

        // Tier 2 gate (vendor_trust >= 2).
        private static bool HasFoodVendorTier2() => GetFoodVendorTrust() >= 2;
        private static bool HasGearVendorTier2() => GetGearVendorTrust() >= 2;

        // Tier 3 gate (vendor_trust >= 3).
        private static bool HasFoodVendorTier3() => GetFoodVendorTrust() >= 3;
        private static bool HasGearVendorTier3() => GetGearVendorTrust() >= 3;

        // T3 rare-piece purchase: T3 + flag not yet set for this (culture, vendorKind).
        private static bool CanFoodVendorBuyRare()
        {
            if (!HasFoodVendorTier3()) return false;
            var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
            var cid = CurrentCultureId();
            if (bdm == null || string.IsNullOrEmpty(cid)) return false;
            return !bdm.HasVendorRarePurchased(cid, "food");
        }
        private static bool CanGearVendorBuyRare()
        {
            if (!HasGearVendorTier3()) return false;
            var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
            var cid = CurrentCultureId();
            if (bdm == null || string.IsNullOrEmpty(cid)) return false;
            return !bdm.HasVendorRarePurchased(cid, "gear");
        }

        // T3 vendor pact: T3 + flag not yet set for this (culture, vendorKind).
        private static bool CanSwearFoodVendorPact()
        {
            if (!HasFoodVendorTier3()) return false;
            var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
            var cid = CurrentCultureId();
            if (bdm == null || string.IsNullOrEmpty(cid)) return false;
            return !bdm.HasVendorPactSworn(cid, "food");
        }
        private static bool CanSwearGearVendorPact()
        {
            if (!HasGearVendorTier3()) return false;
            var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
            var cid = CurrentCultureId();
            if (bdm == null || string.IsNullOrEmpty(cid)) return false;
            return !bdm.HasVendorPactSworn(cid, "gear");
        }

        // === T1: text-variable setters for vendor backstory + camp news ===

        // Sets BP_VENDOR_BACKSTORY_TEXT from VendorProfiles.Backstory. Always returns
        // true so the dialog line renders.
        // Wave 4.14.1 (2026-06-06): paragraph chunker for vendor backstory + news.
        // Mirrors slaver chunker (Wave 4.13.1). Single shared state — context string
        // ("food_backstory", "food_news", "gear_backstory", "gear_news") tracks which
        // body is currently loaded. Switching contexts auto-reloads from scratch.
        private static string _vendorChunkerContext = null;
        private static int _vendorChunkerIdx = 0;
        private static string[] _vendorChunkerParas = null;

        private static void ResetVendorChunker()
        {
            _vendorChunkerContext = null;
            _vendorChunkerIdx = 0;
            _vendorChunkerParas = null;
        }

        private static bool HasMoreVendorChunkerParas()
        {
            return _vendorChunkerParas != null && (_vendorChunkerIdx + 1) < _vendorChunkerParas.Length;
        }

        private static void AdvanceVendorChunkerPara()
        {
            _vendorChunkerIdx++;
        }

        // Shared helper: loads body on first call OR on context switch, then sets the
        // text variable to the current paragraph. Used by all 4 SetVendor* methods.
        private static bool ChunkVendorTextInto(string context, string fullBody, string textVar, string errorFallback)
        {
            try
            {
                if (_vendorChunkerContext != context || _vendorChunkerParas == null)
                {
                    if (string.IsNullOrEmpty(fullBody)) fullBody = BandItPlus.Localization.Get("bp_hideoutvendordialog_036", "Nothing worth telling, traveler.");
                    _vendorChunkerParas = fullBody.Split(new[] { "\n\n" }, System.StringSplitOptions.RemoveEmptyEntries);
                    _vendorChunkerIdx = 0;
                    _vendorChunkerContext = context;
                }
                if (_vendorChunkerParas == null || _vendorChunkerParas.Length == 0) return false;
                if (_vendorChunkerIdx >= _vendorChunkerParas.Length) _vendorChunkerIdx = _vendorChunkerParas.Length - 1;
                MBTextManager.SetTextVariable(textVar, _vendorChunkerParas[_vendorChunkerIdx]);
                return true;
            }
            catch { MBTextManager.SetTextVariable(textVar, errorFallback); return true; }
        }

        // SelfStoryBody (4-5 paragraphs) preferred over the legacy 1-2 sentence Backstory.
        private static bool SetVendorBackstoryFood()
        {
            var cid = CurrentCultureId();
            var profile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetFood(cid) : null;
            string body = !string.IsNullOrEmpty(profile?.SelfStoryBody)
                ? profile.SelfStoryBody
                : (!string.IsNullOrEmpty(profile?.Backstory) ? profile.Backstory : BandItPlus.Localization.Get("bp_hideoutvendordialog_037", "Nothing worth telling, traveler."));
            return ChunkVendorTextInto("food_backstory", body, "BP_VENDOR_BACKSTORY_TEXT",
                BandItPlus.Localization.Get("bp_hideoutvendordialog_038", "Some things travel better behind closed teeth."));
        }
        private static bool SetVendorBackstoryGear()
        {
            var cid = CurrentCultureId();
            var profile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetGear(cid) : null;
            string body = !string.IsNullOrEmpty(profile?.SelfStoryBody)
                ? profile.SelfStoryBody
                : (!string.IsNullOrEmpty(profile?.Backstory) ? profile.Backstory : BandItPlus.Localization.Get("bp_hideoutvendordialog_039", "Nothing worth telling, traveler."));
            return ChunkVendorTextInto("gear_backstory", body, "BP_VENDOR_BACKSTORY_TEXT",
                BandItPlus.Localization.Get("bp_hideoutvendordialog_040", "Some things travel better behind closed teeth."));
        }

        // Camp-news pool — small generic gossip about the camp. Random pick per call.
        private static readonly string[] _vendorCampNewsPool = new[]
        {
            BandItPlus.Localization.Get("bp_hideoutvendordialog_041", "The chief's been short with everyone since the salt shipment fell through. Walk careful with him today."),
            BandItPlus.Localization.Get("bp_hideoutvendordialog_042", "Two of the boys came back from a scouting run with their horses lathered. They won't say why. Bad sign."),
            BandItPlus.Localization.Get("bp_hideoutvendordialog_043", "A new face turned up at the fire last week. Says he's looking for work. Old Hod's keeping an eye on him."),
            BandItPlus.Localization.Get("bp_hideoutvendordialog_044", "We had a stranger try to wander in three nights back. Dogs caught the scent before he made the wall. He won't try again."),
            BandItPlus.Localization.Get("bp_hideoutvendordialog_045", "Trade's been thin this season. Caravans are running heavier escorts. Coin's tighter than my grandmother's fist."),
            BandItPlus.Localization.Get("bp_hideoutvendordialog_046", "One of the trainers cracked his ribs sparring. He'll be down a fortnight. Everyone's covering his rotations and complaining about it.")
        };

        // Wave 4.14 (2026-06-06): per-vendor NewsRumorsBody is the new long-form
        // road-intel news (4-5 paragraphs, per-culture per-vendor authored).
        // Falls back to the legacy random pool when not authored.
        private static bool SetVendorCampNewsFood()
        {
            var cid = CurrentCultureId();
            var profile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetFood(cid) : null;
            string body = !string.IsNullOrEmpty(profile?.NewsRumorsBody)
                ? profile.NewsRumorsBody
                : _vendorCampNewsPool[MBRandom.RandomInt(_vendorCampNewsPool.Length)];
            return ChunkVendorTextInto("food_news", body, "BP_VENDOR_CAMP_NEWS",
                BandItPlus.Localization.Get("bp_hideoutvendordialog_047", "Quiet week, traveler. Quiet weeks are the dangerous ones."));
        }
        private static bool SetVendorCampNewsGear()
        {
            var cid = CurrentCultureId();
            var profile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetGear(cid) : null;
            string body = !string.IsNullOrEmpty(profile?.NewsRumorsBody)
                ? profile.NewsRumorsBody
                : _vendorCampNewsPool[MBRandom.RandomInt(_vendorCampNewsPool.Length)];
            return ChunkVendorTextInto("gear_news", body, "BP_VENDOR_CAMP_NEWS",
                BandItPlus.Localization.Get("bp_hideoutvendordialog_048", "Quiet week, traveler. Quiet weeks are the dangerous ones."));
        }
        // Legacy shared method preserved for any callers I might have missed; just routes to food variant.
        private static bool SetVendorCampNews()
        {
            return SetVendorCampNewsFood();
        }

        // Road-gossip pool — Tier 2 specific, more substantial than the chief's scout
        // rumor pool. Vendor-specific phrasing (commercial angle, route-and-cargo focus).
        private static readonly string[] _vendorRoadGossipPool = new[]
        {
            BandItPlus.Localization.Get("bp_hideoutvendordialog_049", "Heard from a passing peddler — the Vlandian wool-traders have started running a heavier escort. Word is they lost a wagon two months back to a band who knew their route too well."),
            BandItPlus.Localization.Get("bp_hideoutvendordialog_050", "An imperial silk caravan's been moving through the southern pass every fortnight. The escort captain naps at noon. That's not gossip, traveler — that's a fact you can use."),
            BandItPlus.Localization.Get("bp_hideoutvendordialog_051", "Aserai dye-traders are coming up short on indigo this season. Whoever lays hands on a sealed indigo-jar can name their price in Quyaz next month."),
            BandItPlus.Localization.Get("bp_hideoutvendordialog_052", "There's a Sturgian fur convoy that runs the eastern road. Twelve guards, but two of them are drunkards. The captain knows. He's asking around for replacements."),
            BandItPlus.Localization.Get("bp_hideoutvendordialog_053", "Word among the road-folk: a bishop's tithe-wagon is bound for the northern lake-abbey. Eight guards, but priests don't fight, and the captain hasn't drawn steel in two campaigns."),
            BandItPlus.Localization.Get("bp_hideoutvendordialog_054", "A Khuzait horse-trader's been moving forty horses down the steppe-road every new moon. The risk is open ground. The reward is forty horses."),
            BandItPlus.Localization.Get("bp_hideoutvendordialog_055", "There's a rumor about a mercenary captain hauling pay-coin south. The chest is at the back of the column, only two old men guarding it. Captain rides at the front, eyes ahead."),
            BandItPlus.Localization.Get("bp_hideoutvendordialog_056", "Imperial tax-riders have been sloppy this season. They drink at every inn, sleep heavy, and ride three abreast like the road's their parlor. Won't last.")
        };

        // Wave 4.14.4 (2026-06-07): per-vendor long-form road gossip (T2RoadGossipBody),
        // chunked one paragraph per turn. Falls back to the generic random pool if a
        // particular culture's vendor hasn't been authored yet.
        private static bool SetVendorRoadGossipFood()
        {
            var cid = CurrentCultureId();
            var profile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetFood(cid) : null;
            string body = !string.IsNullOrEmpty(profile?.T2RoadGossipBody)
                ? profile.T2RoadGossipBody
                : _vendorRoadGossipPool[MBRandom.RandomInt(_vendorRoadGossipPool.Length)];
            return ChunkVendorTextInto("food_road_gossip", body, "BP_VENDOR_ROAD_GOSSIP",
                BandItPlus.Localization.Get("bp_hideoutvendordialog_057", "Roads are quiet this week, traveler. Quiet's expensive — somebody's paying for the silence."));
        }
        private static bool SetVendorRoadGossipGear()
        {
            var cid = CurrentCultureId();
            var profile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetGear(cid) : null;
            string body = !string.IsNullOrEmpty(profile?.T2RoadGossipBody)
                ? profile.T2RoadGossipBody
                : _vendorRoadGossipPool[MBRandom.RandomInt(_vendorRoadGossipPool.Length)];
            return ChunkVendorTextInto("gear_road_gossip", body, "BP_VENDOR_ROAD_GOSSIP",
                BandItPlus.Localization.Get("bp_hideoutvendordialog_058", "Roads are quiet this week, traveler. Quiet's expensive — somebody's paying for the silence."));
        }

        // === T2 consequences: bulk trade ===
        private static void OnFoodVendorBulk() { PendingBulkFood = true; }
        private static void OnGearVendorBulk() { PendingBulkGear = true; }

        // === T3 consequences: rare-piece + pact ===
        private static void OnFoodVendorRare()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm != null && !string.IsNullOrEmpty(cid))
                    bdm.SetVendorRarePurchased(cid, "food");
                PendingRareFood = true;
                HideoutPeacefulVisitState.Log("Vendor T3: rare food purchased for " + cid);
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("OnFoodVendorRare fail: " + ex.Message); }
        }
        private static void OnGearVendorRare()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm != null && !string.IsNullOrEmpty(cid))
                    bdm.SetVendorRarePurchased(cid, "gear");
                PendingRareGear = true;
                HideoutPeacefulVisitState.Log("Vendor T3: rare gear purchased for " + cid);
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("OnGearVendorRare fail: " + ex.Message); }
        }

        private static void OnFoodVendorSwearPact()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm != null && !string.IsNullOrEmpty(cid))
                    bdm.SetVendorPactSworn(cid, "food");
                // Wave 4.11-fix v62 (2026-05-08): one-shot 500 denar goodwill gift on
                // pact swearing. Vendors aren't Heroes (no relation gain available), so
                // gold is the cleanest immediate reward. Recurring effects (price
                // discount, settlement-marketplace stocking) are deferred to a Harmony
                // hook iteration.
                if (Hero.MainHero != null)
                    GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, 500, false);
                var pProfile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetFood(cid) : null;
                string pName = pProfile?.Name ?? BandItPlus.Localization.Get("bp_hideoutvendordialog_059", "The food vendor");
                BpShowInfoPopup(
                    new TextObject("{=bp_hcv_001}A Supply Pact Sworn — {NAME}").SetTextVariable("NAME", pName).ToString(),
                    BpPopupBody(
                        new TextObject("{=bp_hcv_002}{NAME} has sworn to keep your larder stocked when the time comes.").SetTextVariable("NAME", pName).ToString(),
                        BandItPlus.Localization.Get("bp_hideoutvendordialog_060", "When you raise a banner of your own, the supply lines run beneath it."),
                        BandItPlus.Localization.Get("bp_hideoutvendordialog_061", "+500 denars sealing-coin"),
                        BandItPlus.Localization.Get("bp_hideoutvendordialog_062", "Pact recorded — future waves will surface caravan deliveries to your hold")));
                HideoutPeacefulVisitState.Log("Vendor T3: food pact sworn for " + cid + " — gold +500");
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("OnFoodVendorSwearPact fail: " + ex.Message); }
        }
        private static void OnGearVendorSwearPact()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm != null && !string.IsNullOrEmpty(cid))
                    bdm.SetVendorPactSworn(cid, "gear");
                if (Hero.MainHero != null)
                    GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, 500, false);
                var pProfile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetGear(cid) : null;
                string pName = pProfile?.Name ?? BandItPlus.Localization.Get("bp_hideoutvendordialog_063", "The gear vendor");
                BpShowInfoPopup(
                    new TextObject("{=bp_hcv_003}A Forge Pact Sworn — {NAME}").SetTextVariable("NAME", pName).ToString(),
                    BpPopupBody(
                        new TextObject("{=bp_hcv_004}{NAME} has sworn to forge for your campaigns when the time comes.").SetTextVariable("NAME", pName).ToString(),
                        BandItPlus.Localization.Get("bp_hideoutvendordialog_064", "Steel, salt, or silk — whatever your banners need, it'll fly under them."),
                        BandItPlus.Localization.Get("bp_hideoutvendordialog_065", "+500 denars sealing-coin"),
                        BandItPlus.Localization.Get("bp_hideoutvendordialog_066", "Pact recorded — future waves will surface forge supply to your hold")));
                HideoutPeacefulVisitState.Log("Vendor T3: gear pact sworn for " + cid + " — gold +500");
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("OnGearVendorSwearPact fail: " + ex.Message); }
        }

        // ============================================================
        // Wave 4.12-fix v60 (2026-05-11): T2 craft-detail + custom-order, T3 family +
        // contacts. Four new chat options per vendor type, gated as follows:
        //   bp_vendor_food_craft       — visible if vendor_trust >= 2 (re-askable)
        //   bp_vendor_food_custom      — visible if vendor_trust >= 2 AND not-yet-placed
        //                                (one-shot; small denar goodwill on take)
        //   bp_vendor_food_family      — visible if vendor_trust >= 3 (re-askable)
        //   bp_vendor_food_contacts    — visible if vendor_trust >= 3 AND not-yet-given
        //                                (one-shot; mid denar goodwill on take)
        // Mirror set for gear. All text comes from VendorProfile fields populated in
        // BandItPlus.Cultures.VendorProfiles for all 28 vendors.
        // ============================================================

        // === Visibility conditions for one-shots ===
        // Wave 4.12-fix v63 (2026-05-11): Custom Order is now kingdom-gated.
        // Per user design call: only players who lead a kingdom can commission
        // bespoke supply orders. Vassals, mercenary clans, and unaffiliated
        // adventurers don't see the option. Aligns Custom Order's "I'm a ruler
        // outfitting my realm" fantasy with the player's actual political status.
        // Returns true ONLY when the player IS the leader of their kingdom
        // (founded their own, OR inherited rule). Vassals: false.
        private static bool PlayerOwnsKingdom()
        {
            try
            {
                var kingdom = Hero.MainHero?.Clan?.Kingdom;
                return kingdom != null && kingdom.Leader == Hero.MainHero;
            }
            catch { return false; }
        }

        private static bool CanFoodVendorCustomOrder()
        {
            if (!HasFoodVendorTier2()) return false;
            if (!PlayerOwnsKingdom()) return false;   // v63: kingdom-leader only
            var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
            var cid = CurrentCultureId();
            if (bdm == null || string.IsNullOrEmpty(cid)) return false;
            // v64: option is visible only when there's NO pending order. Once placed,
            // it disappears until pickup (or cancellation, if we ever add that).
            return !bdm.HasPendingCustomOrder(cid, "food");
        }
        private static bool CanGearVendorCustomOrder()
        {
            if (!HasGearVendorTier2()) return false;
            if (!PlayerOwnsKingdom()) return false;   // v63: kingdom-leader only
            var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
            var cid = CurrentCultureId();
            if (bdm == null || string.IsNullOrEmpty(cid)) return false;
            return !bdm.HasPendingCustomOrder(cid, "gear");
        }

        // v64: pickup-ready check — player has a pending order AND the delivery
        // date has passed. Surfaces the "Is my custom order ready?" dialog line.
        private static bool CanFoodVendorPickupCustomOrder()
        {
            if (!PlayerOwnsKingdom()) return false;
            var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
            var cid = CurrentCultureId();
            if (bdm == null || string.IsNullOrEmpty(cid)) return false;
            return bdm.IsCustomOrderReady(cid, "food");
        }
        private static bool CanGearVendorPickupCustomOrder()
        {
            if (!PlayerOwnsKingdom()) return false;
            var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
            var cid = CurrentCultureId();
            if (bdm == null || string.IsNullOrEmpty(cid)) return false;
            return bdm.IsCustomOrderReady(cid, "gear");
        }

        // v64: still-working check — player has a pending order but it's NOT
        // ready yet. Surfaces the "How's my custom order going?" dialog line.
        private static bool IsFoodVendorOrderWaiting()
        {
            if (!PlayerOwnsKingdom()) return false;
            var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
            var cid = CurrentCultureId();
            if (bdm == null || string.IsNullOrEmpty(cid)) return false;
            return bdm.HasPendingCustomOrder(cid, "food") && !bdm.IsCustomOrderReady(cid, "food");
        }
        private static bool IsGearVendorOrderWaiting()
        {
            if (!PlayerOwnsKingdom()) return false;
            var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
            var cid = CurrentCultureId();
            if (bdm == null || string.IsNullOrEmpty(cid)) return false;
            return bdm.HasPendingCustomOrder(cid, "gear") && !bdm.IsCustomOrderReady(cid, "gear");
        }
        private static bool CanFoodVendorIntroduceContacts()
        {
            if (!HasFoodVendorTier3()) return false;
            var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
            var cid = CurrentCultureId();
            if (bdm == null || string.IsNullOrEmpty(cid)) return false;
            return !bdm.HasVendorContactsIntroduced(cid, "food");
        }
        private static bool CanGearVendorIntroduceContacts()
        {
            if (!HasGearVendorTier3()) return false;
            var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
            var cid = CurrentCultureId();
            if (bdm == null || string.IsNullOrEmpty(cid)) return false;
            return !bdm.HasVendorContactsIntroduced(cid, "gear");
        }

        // === Text-variable setters (all return true so the dialog line renders) ===
        // Wave 4.14.4 (2026-06-07): T2CraftSecretBody (long-form, 3-4 paragraph chunked)
        // preferred over legacy 1-2 sentence T2CraftDetail.
        private static bool SetVendorCraftDetailFood()
        {
            var cid = CurrentCultureId();
            var profile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetFood(cid) : null;
            string body = !string.IsNullOrEmpty(profile?.T2CraftSecretBody)
                ? profile.T2CraftSecretBody
                : (!string.IsNullOrEmpty(profile?.T2CraftDetail) ? profile.T2CraftDetail
                    : BandItPlus.Localization.Get("bp_hideoutvendordialog_067", "Trade secrets travel best behind closed teeth, traveler. Maybe next pass."));
            return ChunkVendorTextInto("food_craft", body, "BP_VENDOR_CRAFT_DETAIL",
                BandItPlus.Localization.Get("bp_hideoutvendordialog_068", "The craft keeps its own counsel, traveler."));
        }
        private static bool SetVendorCraftDetailGear()
        {
            var cid = CurrentCultureId();
            var profile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetGear(cid) : null;
            string body = !string.IsNullOrEmpty(profile?.T2CraftSecretBody)
                ? profile.T2CraftSecretBody
                : (!string.IsNullOrEmpty(profile?.T2CraftDetail) ? profile.T2CraftDetail
                    : BandItPlus.Localization.Get("bp_hideoutvendordialog_069", "Trade secrets travel best behind closed teeth, traveler. Maybe next pass."));
            return ChunkVendorTextInto("gear_craft", body, "BP_VENDOR_CRAFT_DETAIL",
                BandItPlus.Localization.Get("bp_hideoutvendordialog_070", "The craft keeps its own counsel, traveler."));
        }
        private static bool SetVendorCustomOrderFood()
        {
            try
            {
                var cid = CurrentCultureId();
                var profile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetFood(cid) : null;
                MBTextManager.SetTextVariable("BP_VENDOR_CUSTOM_ORDER",
                    !string.IsNullOrEmpty(profile?.T2CustomOrderTease) ? profile.T2CustomOrderTease
                        : BandItPlus.Localization.Get("bp_hideoutvendordialog_071", "Tell me the count and the day, traveler. I'll set it aside."));
            }
            catch { MBTextManager.SetTextVariable("BP_VENDOR_CUSTOM_ORDER", BandItPlus.Localization.Get("bp_hideoutvendordialog_072", "Tell me the count and the day, traveler.")); }
            return true;
        }
        private static bool SetVendorCustomOrderGear()
        {
            try
            {
                var cid = CurrentCultureId();
                var profile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetGear(cid) : null;
                MBTextManager.SetTextVariable("BP_VENDOR_CUSTOM_ORDER",
                    !string.IsNullOrEmpty(profile?.T2CustomOrderTease) ? profile.T2CustomOrderTease
                        : BandItPlus.Localization.Get("bp_hideoutvendordialog_073", "Tell me the count and the day, traveler. I'll set it aside."));
            }
            catch { MBTextManager.SetTextVariable("BP_VENDOR_CUSTOM_ORDER", BandItPlus.Localization.Get("bp_hideoutvendordialog_074", "Tell me the count and the day, traveler.")); }
            return true;
        }
        private static bool SetVendorFamilyBeatFood()
        {
            try
            {
                var cid = CurrentCultureId();
                var profile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetFood(cid) : null;
                MBTextManager.SetTextVariable("BP_VENDOR_FAMILY_BEAT",
                    !string.IsNullOrEmpty(profile?.T3FamilyBeat) ? profile.T3FamilyBeat
                        : BandItPlus.Localization.Get("bp_hideoutvendordialog_075", "Family's a long fire, traveler. Some nights it warms; some nights it just smokes."));
            }
            catch { MBTextManager.SetTextVariable("BP_VENDOR_FAMILY_BEAT", BandItPlus.Localization.Get("bp_hideoutvendordialog_076", "Family's a long fire, traveler.")); }
            return true;
        }
        private static bool SetVendorFamilyBeatGear()
        {
            try
            {
                var cid = CurrentCultureId();
                var profile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetGear(cid) : null;
                MBTextManager.SetTextVariable("BP_VENDOR_FAMILY_BEAT",
                    !string.IsNullOrEmpty(profile?.T3FamilyBeat) ? profile.T3FamilyBeat
                        : BandItPlus.Localization.Get("bp_hideoutvendordialog_077", "Family's a long fire, traveler. Some nights it warms; some nights it just smokes."));
            }
            catch { MBTextManager.SetTextVariable("BP_VENDOR_FAMILY_BEAT", BandItPlus.Localization.Get("bp_hideoutvendordialog_078", "Family's a long fire, traveler.")); }
            return true;
        }
        private static bool SetVendorContactsFood()
        {
            try
            {
                var cid = CurrentCultureId();
                var profile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetFood(cid) : null;
                MBTextManager.SetTextVariable("BP_VENDOR_CONTACT_NAME",
                    !string.IsNullOrEmpty(profile?.T3ContactName) ? profile.T3ContactName
                        : BandItPlus.Localization.Get("bp_hideoutvendordialog_079", "I know a few who'd seat you, traveler. Walk careful and walk back."));
            }
            catch { MBTextManager.SetTextVariable("BP_VENDOR_CONTACT_NAME", BandItPlus.Localization.Get("bp_hideoutvendordialog_080", "I know a few who'd seat you, traveler.")); }
            return true;
        }
        private static bool SetVendorContactsGear()
        {
            try
            {
                var cid = CurrentCultureId();
                var profile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetGear(cid) : null;
                MBTextManager.SetTextVariable("BP_VENDOR_CONTACT_NAME",
                    !string.IsNullOrEmpty(profile?.T3ContactName) ? profile.T3ContactName
                        : BandItPlus.Localization.Get("bp_hideoutvendordialog_081", "I know a few who'd seat you, traveler. Walk careful and walk back."));
            }
            catch { MBTextManager.SetTextVariable("BP_VENDOR_CONTACT_NAME", BandItPlus.Localization.Get("bp_hideoutvendordialog_082", "I know a few who'd seat you, traveler.")); }
            return true;
        }

        // === Consequences for one-shots (set flag + small denar goodwill) ===
        private const int kCustomOrderGoodwillGold = 100;
        // v68 (2026-05-12): bumped 250 → 750 per Tier-1 Contacts improvement.
        private const int kContactsGoodwillGold = 750;
        private const int kContactsTradeXp = 50;

        // Wave 4.12-fix v72 (2026-05-12): Gauntlet popup helpers — premium
        // layout for major Contacts events. Uses Unicode dividers (U+2500
        // horizontal box-line, U+25C6 black diamond) per the established
        // BandIt Plus inquiry-popup pattern. ShowInquiry pauses game until
        // player clicks; intentional for milestone events.
        private const string kPopupDivider = "──────────────────────────────";

        public static void BpShowInfoPopup(string title, string body)
        {
            try
            {
                InformationManager.ShowInquiry(new InquiryData(
                    titleText: title,
                    text: body,
                    isAffirmativeOptionShown: true,
                    isNegativeOptionShown: false,
                    affirmativeText: BandItPlus.Localization.Get("bp_hideoutvendordialog_083", "Continue"),
                    negativeText: null,
                    affirmativeAction: null,
                    negativeAction: null,
                    soundEventPath: ""),
                    pauseGameActiveState: true);
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("BpShowInfoPopup fail: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Scene-init / settlement-entry safe variant: pauseGameActiveState:false avoids
        // the loading-splash deadlock (popup hidden behind the engine splash, game paused
        // waiting for "Continue" that can't be clicked). Use for any popup triggered from
        // scene-init hooks, OnSettlementEntered, or ContactArrival transitions.
        public static void BpShowInfoPopupNoPause(string title, string body)
        {
            try
            {
                InformationManager.ShowInquiry(new InquiryData(
                    titleText: title,
                    text: body,
                    isAffirmativeOptionShown: true,
                    isNegativeOptionShown: false,
                    affirmativeText: BandItPlus.Localization.Get("bp_hideoutvendordialog_084", "Continue"),
                    negativeText: null,
                    affirmativeAction: null,
                    negativeAction: null,
                    soundEventPath: ""),
                    pauseGameActiveState: false);
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("BpShowInfoPopupNoPause fail: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Builds a premium body block: header + divider + flavor + divider + rewards.
        public static string BpPopupBody(string header, string flavor, params string[] rewards)
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(header))
            {
                sb.Append(header).Append('\n');
                sb.Append(kPopupDivider).Append('\n');
            }
            if (!string.IsNullOrEmpty(flavor))
            {
                sb.Append(flavor).Append('\n');
                sb.Append(kPopupDivider).Append('\n');
            }
            if (rewards != null)
            {
                foreach (var r in rewards)
                {
                    if (!string.IsNullOrEmpty(r))
                        sb.Append("◆ ").Append(r).Append('\n');
                }
            }
            return sb.ToString().TrimEnd('\n');
        }

        // v71 (2026-05-12) Tier-5 foundation: bandit→real culture map. Mirrors
        // the one in ContactSettlementDiscountPatch (deliberately duplicated for
        // now to keep v71 self-contained; future cleanup wave can DRY them).
        private static readonly System.Collections.Generic.Dictionary<string, string> _contactRealCultureMap
            = new System.Collections.Generic.Dictionary<string, string>
        {
            { "forest_bandits",         "vlandia"  }, { "bp_frost_reavers",       "sturgia"  },
            { "bp_marsh_stalkers",      "battania" }, { "bp_highwaymen",          "vlandia"  },
            { "bp_slaver_caravans",     "aserai"   }, { "bp_fallen_legionaries",  "empire"   },
            { "bp_sky_raiders",         "battania" }, { "bp_steppe_wolves",       "khuzait"  },
            { "bp_pagan_cult",          "battania" }, { "looters",                "vlandia"  },
            { "mountain_bandits",       "aserai"   }, { "steppe_bandits",         "khuzait"  },
            { "sea_raiders",            "sturgia"  }, { "desert_bandits",         "aserai"   },
        };

        // v73 (2026-05-12): exact settlement assignment per (cid, vendorKind) — values
        // chosen to match the authored T3ContactName flavor (e.g. Brae's Aunt Lien
        // says "Pravend crossroads" → Pravend; Tariq's Salim says "Quyaz" → Quyaz).
        // Resolved by vanilla English display name at runtime; falls back to culture-
        // reservoir-sample if the settlement isn't found (heavily-modded saves).
        private static readonly System.Collections.Generic.Dictionary<string, string> _contactExactSettlementMap
            = new System.Collections.Generic.Dictionary<string, string>
        {
            { "forest_bandits:food",        "Pravend"     }, { "forest_bandits:gear",        "Galich"      },
            { "bp_frost_reavers:food",      "Revyl"       }, { "bp_frost_reavers:gear",      "Sibir"       },
            { "bp_marsh_stalkers:food",     "Sargot"      }, { "bp_marsh_stalkers:gear",     "Marunath"    },
            { "bp_highwaymen:food",         "Galich"      }, { "bp_highwaymen:gear",         "Pravend"     },
            { "bp_slaver_caravans:food",    "Quyaz"       }, { "bp_slaver_caravans:gear",    "Razih"       },
            { "bp_fallen_legionaries:food", "Lycaron"     }, { "bp_fallen_legionaries:gear", "Argoron"     },
            { "bp_sky_raiders:food",        "Marunath"    }, { "bp_sky_raiders:gear",        "Pen Cannoc"  },
            { "bp_steppe_wolves:food",      "Iskar"       }, { "bp_steppe_wolves:gear",      "Tubdenkhal"  },
            { "bp_pagan_cult:food",         "Dunglanys"   }, { "bp_pagan_cult:gear",         "Car Banseth" },
            { "looters:food",               "Pravend"     }, { "looters:gear",               "Pravend"     },
            { "mountain_bandits:food",      "Sanala"      }, { "mountain_bandits:gear",      "Razih"       },
            { "steppe_bandits:food",        "Akkalat"     }, { "steppe_bandits:gear",        "Chaikand"    },
            { "sea_raiders:food",           "Sibir"       }, { "sea_raiders:gear",           "Tyal"        },
            { "desert_bandits:food",        "Quyaz"       }, { "desert_bandits:gear",        "Quyaz"       },
        };

        // v73: try the exact-map lookup before culture-reservoir-sampling.
        // Returns null if no exact match (caller falls back to the broader sample).
        private static TaleWorlds.CampaignSystem.Settlements.Settlement TryExactSettlement(string cid, string vendorKind)
        {
            try
            {
                if (string.IsNullOrEmpty(cid) || string.IsNullOrEmpty(vendorKind)) return null;
                string key = cid + ":" + vendorKind;
                if (!_contactExactSettlementMap.TryGetValue(key, out var targetName)) return null;
                if (string.IsNullOrEmpty(targetName)) return null;
                foreach (var s in TaleWorlds.CampaignSystem.Settlements.Settlement.All)
                {
                    if (s == null || !s.IsTown) continue;
                    var sn = s.Name?.ToString();
                    if (!string.IsNullOrEmpty(sn) && string.Equals(sn, targetName, StringComparison.OrdinalIgnoreCase))
                        return s;
                }
                return null;
            }
            catch { return null; }
        }

        // Pick a random Town settlement of the bandit culture's mapped real culture.
        // Uses reservoir-sampling so we don't allocate a full List<Settlement>.
        // Returns null if no matching town exists (defensive — should never happen
        // in normal Calradia, but a heavily-modded campaign could remove towns).
        private static TaleWorlds.CampaignSystem.Settlements.Settlement PickContactSettlement(string cid, string vendorKind)
        {
            try
            {
                // v73: prefer the exact-flavor map; fall back to culture-reservoir-sample.
                var exact = TryExactSettlement(cid, vendorKind);
                if (exact != null) return exact;

                if (string.IsNullOrEmpty(cid)) return null;
                if (!_contactRealCultureMap.TryGetValue(cid, out var realCulture)) return null;
                TaleWorlds.CampaignSystem.Settlements.Settlement best = null;
                int seen = 0;
                foreach (var s in TaleWorlds.CampaignSystem.Settlements.Settlement.All)
                {
                    if (s == null || !s.IsTown) continue;
                    var sc = s.Culture?.StringId;
                    if (string.IsNullOrEmpty(sc)) continue;
                    // empire matches any imperial sub-culture; everything else is direct match.
                    bool match = sc == realCulture
                        || (realCulture == "empire" && sc.Contains("empire"));
                    if (!match) continue;
                    seen++;
                    if (MBRandom.RandomInt(seen) == 0) best = s;
                }
                return best;
            }
            catch { return null; }
        }

        // v71 Tier-5 foundation: assign a settlement to this contact and surface
        // a yellow banner pointing the player at it. If the player has already
        // been assigned for this intro (e.g. they're somehow re-triggering), we
        // re-announce the existing assignment instead of picking a new one.
        private static void AssignContactSettlement(BandItPlus.BanditDialogManager bdm, string cid, string vendorKind)
        {
            try
            {
                if (bdm == null || string.IsNullOrEmpty(cid) || string.IsNullOrEmpty(vendorKind)) return;
                var existing = bdm.GetContactSettlement(cid, vendorKind);
                TaleWorlds.CampaignSystem.Settlements.Settlement settlement;
                if (!string.IsNullOrEmpty(existing))
                {
                    settlement = TaleWorlds.CampaignSystem.Settlements.Settlement.Find(existing);
                }
                else
                {
                    settlement = PickContactSettlement(cid, vendorKind);
                    if (settlement != null)
                        bdm.SetContactSettlement(cid, vendorKind, settlement.StringId);
                }
                if (settlement != null)
                {
                    BpShowInfoPopup(
                        new TextObject("{=bp_hcv_005}Network Reach — {SETTLEMENT}").SetTextVariable("SETTLEMENT", settlement.Name?.ToString() ?? settlement.StringId).ToString(),
                        BpPopupBody(
                            new TextObject("{=bp_hcv_006}Your contact resides in {SETTLEMENT}.").SetTextVariable("SETTLEMENT", settlement.Name?.ToString() ?? settlement.StringId).ToString(),
                            BandItPlus.Localization.Get("bp_hideoutvendordialog_085", "Visit when convenient — they will recognize you on arrival."),
                            BandItPlus.Localization.Get("bp_hideoutvendordialog_086", "Settlement marked on your travels"),
                            new TextObject("{=bp_hcv_007}Trade prices in {CULTURE} towns improved (-5% buy / +5% sell)").SetTextVariable("CULTURE", settlement.Culture?.Name?.ToString() ?? "this culture's").ToString()));
                    HideoutPeacefulVisitState.Log("ContactSettlement assigned: " + cid + "/" + vendorKind + " -> " + settlement.StringId);
                    // v74 (2026-05-12): register the settlement on the vanilla quest-tracker
                    // so the player sees a green ring on the world map and an entry in the
                    // tracked-quests panel — same UX as vanilla "go here" objectives.
                    // ContactArrivalBehavior.TryMeet will remove the marker once the player
                    // actually visits, keeping the tracker panel clean across many intros.
                    try
                    {
                        Campaign.Current?.VisualTrackerManager?.RegisterObject(settlement);
                        HideoutPeacefulVisitState.Log("VisualTracker registered: " + settlement.StringId);
                    }
                    catch (Exception trackerEx)
                    {
                        HideoutPeacefulVisitState.Log("VisualTracker register fail: "
                            + trackerEx.GetType().Name + ": " + trackerEx.Message);
                    }
                }
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("AssignContactSettlement fail: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Wave 4.12-fix v64 (2026-05-12): Custom Order is now a real picker.
        // Player chooses from a Gauntlet MultiSelectionInquiry, pays cost upfront,
        // order is registered with a delivery date; pickup branch surfaces after.
        private static void OnFoodVendorCustomOrder()
        {
            try
            {
                var cid = CurrentCultureId();
                if (string.IsNullOrEmpty(cid)) return;
                ShowCustomOrderPicker("food", cid, _customOrderFoodPool, kCustomOrderFoodCost,
                    BandItPlus.Localization.Get("bp_hideoutvendordialog_087", "Food Vendor — Custom Order"));
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("OnFoodVendorCustomOrder fail: " + ex.GetType().Name + ": " + ex.Message); }
        }
        private static void OnGearVendorCustomOrder()
        {
            try
            {
                var cid = CurrentCultureId();
                if (string.IsNullOrEmpty(cid)) return;
                ShowCustomOrderPicker("gear", cid, _customOrderGearPool, kCustomOrderGearCost,
                    BandItPlus.Localization.Get("bp_hideoutvendordialog_088", "Gear Vendor — Custom Order"));
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("OnGearVendorCustomOrder fail: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void ShowCustomOrderPicker(string vendorKind, string cid,
            (string itemId, int qty, string label)[] pool, int cost, string title)
        {
            if (Hero.MainHero == null) return;
            if (Hero.MainHero.Gold < cost)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    new TextObject("{=bp_hcv_008}[BandIt Plus] Not enough denars for a custom order. (Need {COST}, have {GOLD})").SetTextVariable("COST", cost).SetTextVariable("GOLD", Hero.MainHero.Gold).ToString(),
                    Colors.Yellow));
                return;
            }

            var elements = new List<InquiryElement>();
            foreach (var entry in pool)
            {
                var item = MBObjectManager.Instance.GetObject<ItemObject>(entry.itemId);
                if (item == null) continue;   // skip items missing in this game build
                elements.Add(new InquiryElement(entry, entry.label, null));
            }
            if (elements.Count == 0)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    BandItPlus.Localization.Get("bp_hideoutvendordialog_089", "[BandIt Plus] Vendor: \"My stock can't deliver any of that this season, traveler.\""), Colors.Yellow));
                return;
            }

            var data = new MultiSelectionInquiryData(
                titleText: title,
                descriptionText: new TextObject("{=bp_hcv_009}Pick one. {COST} denars upfront. Ready in {DAYS} days.").SetTextVariable("COST", cost).SetTextVariable("DAYS", (int)kCustomOrderDeliveryDays).ToString(),
                inquiryElements: elements,
                isExitShown: true,
                minSelectableOptionCount: 0,
                maxSelectableOptionCount: 1,
                affirmativeText: BandItPlus.Localization.Get("bp_hideoutvendordialog_090", "Order"),
                negativeText: BandItPlus.Localization.Get("bp_hideoutvendordialog_091", "Leave"),
                affirmativeAction: list =>
                {
                    if (list == null || list.Count == 0) return;
                    var picked = (System.ValueTuple<string, int, string>)list[0].Identifier;
                    OnCustomOrderPicked(vendorKind, cid, picked.Item1, picked.Item2, picked.Item3, cost);
                },
                negativeAction: null);

            MBInformationManager.ShowMultiSelectionInquiry(data, false);
        }

        private static void OnCustomOrderPicked(string vendorKind, string cid,
            string itemId, int qty, string label, int cost)
        {
            try
            {
                if (Hero.MainHero == null) return;
                if (Hero.MainHero.Gold < cost) return;   // defensive recheck (popup is async)

                Hero.MainHero.ChangeHeroGold(-cost);
                double dueHours = CampaignTime.DaysFromNow(kCustomOrderDeliveryDays).ToHours;
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                bdm?.SetPendingCustomOrder(cid, vendorKind, itemId, qty, dueHours);

                InformationManager.DisplayMessage(new InformationMessage(
                    new TextObject("{=bp_hcv_010}[BandIt Plus] Custom order placed: {LABEL} — ready in {DAYS} days. (-{COST} denars)").SetTextVariable("LABEL", label).SetTextVariable("DAYS", (int)kCustomOrderDeliveryDays).SetTextVariable("COST", cost).ToString(),
                    Colors.Green));
                HideoutPeacefulVisitState.Log("Vendor Custom Order PLACED: " + cid + "/" + vendorKind
                    + " — " + qty + "× " + itemId + " — cost " + cost + " — due hours " + dueHours.ToString("F1"));
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("OnCustomOrderPicked fail: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Pickup consequences — surface the item into the player's inventory, clear pending state.
        private static void OnFoodVendorPickupCustomOrder() { PickupCustomOrder("food"); }
        private static void OnGearVendorPickupCustomOrder() { PickupCustomOrder("gear"); }

        private static void PickupCustomOrder(string vendorKind)
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm == null || string.IsNullOrEmpty(cid)) return;
                if (!bdm.IsCustomOrderReady(cid, vendorKind)) return;

                var itemId = bdm.GetPendingCustomOrderItemId(cid, vendorKind);
                int qty = bdm.GetPendingCustomOrderQuantity(cid, vendorKind);
                if (string.IsNullOrEmpty(itemId) || qty <= 0) { bdm.ClearPendingCustomOrder(cid, vendorKind); return; }

                var item = MBObjectManager.Instance.GetObject<ItemObject>(itemId);
                if (item != null && MobileParty.MainParty != null)
                {
                    MobileParty.MainParty.ItemRoster.AddToCounts(item, qty);
                    InformationManager.DisplayMessage(new InformationMessage(
                        new TextObject("{=bp_hcv_011}[BandIt Plus] Custom order delivered: {QTY}× {ITEM}").SetTextVariable("QTY", qty).SetTextVariable("ITEM", item.Name?.ToString() ?? itemId).ToString(),
                        Colors.Green));
                    HideoutPeacefulVisitState.Log("Vendor Custom Order DELIVERED: " + cid + "/" + vendorKind
                        + " — " + qty + "× " + itemId);
                }
                else
                {
                    HideoutPeacefulVisitState.Log("PickupCustomOrder: item '" + itemId
                        + "' not found or no MainParty for " + cid + "/" + vendorKind);
                }
                bdm.ClearPendingCustomOrder(cid, vendorKind);
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("PickupCustomOrder(" + vendorKind + ") fail: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Waiting-line text setter — shows "X days remaining" to the player.
        private static bool SetCustomOrderWaitingTextFood() { return SetCustomOrderWaitingText("food"); }
        private static bool SetCustomOrderWaitingTextGear() { return SetCustomOrderWaitingText("gear"); }
        private static bool SetCustomOrderWaitingText(string vendorKind)
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm == null || string.IsNullOrEmpty(cid)) { MBTextManager.SetTextVariable("BP_CUSTOM_ORDER_WAITING", BandItPlus.Localization.Get("bp_hideoutvendordialog_092", "Working on it, traveler.")); return true; }
                double due = bdm.GetPendingCustomOrderDueHours(cid, vendorKind);
                double now = CampaignTime.Now.ToHours;
                double hoursLeft = due - now;
                int daysLeft = (int)System.Math.Ceiling(hoursLeft / 24.0);
                if (daysLeft < 1) daysLeft = 1;
                MBTextManager.SetTextVariable("BP_CUSTOM_ORDER_WAITING",
                    new TextObject("{=bp_hcv_012}Still working on it, traveler — give me {DAYS} more day{PLURAL} and the goods'll be ready.").SetTextVariable("DAYS", daysLeft).SetTextVariable("PLURAL", daysLeft == 1 ? "" : "s").ToString());
            }
            catch { MBTextManager.SetTextVariable("BP_CUSTOM_ORDER_WAITING", BandItPlus.Localization.Get("bp_hideoutvendordialog_093", "Still working on it, traveler — come back later.")); }
            return true;
        }
        private static void OnFoodVendorIntroduceContacts()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm != null && !string.IsNullOrEmpty(cid))
                    bdm.SetVendorContactsIntroduced(cid, "food");
                if (Hero.MainHero != null)
                {
                    GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, kContactsGoodwillGold, false);
                    // v68 Tier 1: trade-network introduction grants Trade skill XP.
                    // Networking with a fence/contact IS a trade-skill exercise.
                    Hero.MainHero.HeroDeveloper?.AddSkillXp(DefaultSkills.Trade, kContactsTradeXp);
                }
                // v68 Tier 2: cross-vendor ripple. If the gear vendor of this same
                // culture is already unlocked (trust >= 1), word travels through the
                // camp and they trust the player one tier more (Increment caps at 3).
                // Guarded by >= 1 so we never bypass the gear vendor's gate quest —
                // this only rewards players who've engaged BOTH vendor lines.
                if (bdm != null && !string.IsNullOrEmpty(cid))
                {
                    int gearTrust = bdm.GetGearVendorTrust(cid);
                    if (gearTrust >= 1 && gearTrust < 3)
                        bdm.IncrementGearVendorTrust(cid);
                }
                // v72 premium popup: vendor's own name + per-vendor T3 contact text + structured rewards.
                var profile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetFood(cid) : null;
                string vendorName = profile?.Name ?? BandItPlus.Localization.Get("bp_hideoutvendordialog_094", "The food vendor");
                string flavor = profile?.T3ContactName ?? BandItPlus.Localization.Get("bp_hideoutvendordialog_095", "Word travels through the network on your behalf.");
                BpShowInfoPopup(
                    new TextObject("{=bp_hcv_013}A Contact Made — {NAME}").SetTextVariable("NAME", vendorName).ToString(),
                    BpPopupBody(
                        new TextObject("{=bp_hcv_014}{NAME} has named-dropped you to their out-of-camp contact.").SetTextVariable("NAME", vendorName).ToString(),
                        flavor,
                        new TextObject("{=bp_hcv_015}+{GOLD} denars goodwill").SetTextVariable("GOLD", kContactsGoodwillGold).ToString(),
                        new TextObject("{=bp_hcv_016}+{XP} Trade XP").SetTextVariable("XP", kContactsTradeXp).ToString(),
                        BandItPlus.Localization.Get("bp_hideoutvendordialog_096", "Gear vendor's trust ticks up (if they already know you)")));
                HideoutPeacefulVisitState.Log("Vendor T3: food contacts introduced for " + cid + " — gold +" + kContactsGoodwillGold + " Trade XP +" + kContactsTradeXp);
                // v71 Tier-5 foundation: assign a real settlement, tell the player where.
                AssignContactSettlement(bdm, cid, "food");
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("OnFoodVendorIntroduceContacts fail: " + ex.GetType().Name + ": " + ex.Message); }
        }
        private static void OnGearVendorIntroduceContacts()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                var cid = CurrentCultureId();
                if (bdm != null && !string.IsNullOrEmpty(cid))
                    bdm.SetVendorContactsIntroduced(cid, "gear");
                if (Hero.MainHero != null)
                {
                    GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, kContactsGoodwillGold, false);
                    Hero.MainHero.HeroDeveloper?.AddSkillXp(DefaultSkills.Trade, kContactsTradeXp);
                }
                // v68 Tier 2: mirror of the food consequence — food vendor's trust
                // ticks up if they know the player (>=1) but isn't yet maxed (<3).
                if (bdm != null && !string.IsNullOrEmpty(cid))
                {
                    int foodTrust = bdm.GetFoodVendorTrust(cid);
                    if (foodTrust >= 1 && foodTrust < 3)
                        bdm.IncrementFoodVendorTrust(cid);
                }
                var profile = !string.IsNullOrEmpty(cid) ? BandItPlus.Cultures.VendorProfiles.GetGear(cid) : null;
                string vendorName = profile?.Name ?? BandItPlus.Localization.Get("bp_hideoutvendordialog_097", "The gear vendor");
                string flavor = profile?.T3ContactName ?? BandItPlus.Localization.Get("bp_hideoutvendordialog_098", "Word travels through the network on your behalf.");
                BpShowInfoPopup(
                    new TextObject("{=bp_hcv_013}A Contact Made — {NAME}").SetTextVariable("NAME", vendorName).ToString(),
                    BpPopupBody(
                        new TextObject("{=bp_hcv_014}{NAME} has named-dropped you to their out-of-camp contact.").SetTextVariable("NAME", vendorName).ToString(),
                        flavor,
                        new TextObject("{=bp_hcv_015}+{GOLD} denars goodwill").SetTextVariable("GOLD", kContactsGoodwillGold).ToString(),
                        new TextObject("{=bp_hcv_016}+{XP} Trade XP").SetTextVariable("XP", kContactsTradeXp).ToString(),
                        BandItPlus.Localization.Get("bp_hideoutvendordialog_099", "Food vendor's trust ticks up (if they already know you)")));
                HideoutPeacefulVisitState.Log("Vendor T3: gear contacts introduced for " + cid + " — gold +" + kContactsGoodwillGold + " Trade XP +" + kContactsTradeXp);
                AssignContactSettlement(bdm, cid, "gear");
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("OnGearVendorIntroduceContacts fail: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            if (_registeredStarter == starter) return;
            _registeredStarter = starter;
            try
            {
                // ============================================================
                // Wave 4.11-fix v41 (2026-05-07): vendor-trust progression.
                // 5 dialog states per vendor type, gated by 4 conditions in priority order:
                //   1. Chief rebuff (chief_trust = 0) — "earn chief's trust first"
                //   2. Gate offer (chief>=1, vendor=0, no quest) — "chief trusts you, I don't, here's quest"
                //   3. Quest reminder (quest active) — "have you got my goods? / soon / cancel"
                //   4. Trade (vendor>=1) — opens tiered trade, optional next-tier quest offer
                // All five fire from `start` at the same priority — conditions disambiguate.
                // ============================================================

                // === FOOD VENDOR ===

                // Trade dialog (vendor_trust >= 1)
                starter.AddDialogLine(
                    "bp_vendor_food_start",
                    "start",
                    "bp_vendor_food_root",
                    "{=bp_hideoutvendordialog_100}{BP_SPEAKER_NAME}: Welcome back, traveler. The fire's warm and the goods are fresh. What'll it be today?",
                    new ConversationSentence.OnConditionDelegate(IsFoodVendorConv),
                    null,
                    kPriority,
                    null);
                starter.AddPlayerLine(
                    "bp_vendor_food_buy",
                    "bp_vendor_food_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_101}Show me your wares. <span style=\"Conversation.Persuasion.Positive\">(Trade)</span>",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(OpenFoodTrade),
                    kPriority,
                    null,
                    null);
                // "Got another job?" — only visible if vendor_trust 1..2 and no quest active.
                starter.AddPlayerLine(
                    "bp_vendor_food_ask_job",
                    "bp_vendor_food_root",
                    "bp_vendor_food_next_tier_offer",
                    "{=bp_hideoutvendordialog_102}Got another job for me? {BP_VENDOR_QUEST_DIFFICULTY_TAG}",
                    new ConversationSentence.OnConditionDelegate(CanOfferNextFoodTierWithDifficulty),
                    null,
                    kPriority,
                    null,
                    null);
                // Wave 4.14.1 (2026-06-06): priority lowered so "Maybe later" sorts to BOTTOM
                // of the menu (was in middle between quest-offer and backstory).
                starter.AddPlayerLine(
                    "bp_vendor_food_skip",
                    "bp_vendor_food_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_103}Maybe later.",
                    null,
                    null,
                    kPriority - 100,
                    null,
                    null);

                // === Wave 4.11-fix v59: vendor T1/T2/T3 menu additions (food) ===
                // T1 (always at this menu since vendor_trust>=1 to be here): backstory + camp news
                // Wave 4.14.1 (2026-06-06): chunker pattern. NPC speaks ONE paragraph
                // per turn; player picks "..." to advance OR "I see."/"Good to know."
                // to exit back to root. Mirrors slaver Wave 4.13.5.
                starter.AddPlayerLine(
                    "bp_vendor_food_aboutme",
                    "bp_vendor_food_root",
                    "bp_vendor_food_aboutme_response",
                    "{=bp_hideoutvendordialog_104}Tell me about yourself. <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(ResetVendorChunker),
                    kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_food_aboutme_response",
                    "bp_vendor_food_aboutme_response",
                    "bp_vendor_food_aboutme_choice",
                    "{=!}{BP_VENDOR_BACKSTORY_TEXT}",
                    new ConversationSentence.OnConditionDelegate(SetVendorBackstoryFood),
                    null, kPriority, null);
                starter.AddPlayerLine(
                    "bp_vendor_food_aboutme_more",
                    "bp_vendor_food_aboutme_choice",
                    "bp_vendor_food_aboutme_response",
                    "...",
                    new ConversationSentence.OnConditionDelegate(HasMoreVendorChunkerParas),
                    new ConversationSentence.OnConsequenceDelegate(AdvanceVendorChunkerPara),
                    kPriority + 10, null, null);
                starter.AddPlayerLine(
                    "bp_vendor_food_aboutme_done",
                    "bp_vendor_food_aboutme_choice",
                    "bp_vendor_food_relay",
                    "{=bp_hideoutvendordialog_105}I see.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(ResetVendorChunker),
                    kPriority, null, null);

                starter.AddPlayerLine(
                    "bp_vendor_food_camp_news",
                    "bp_vendor_food_root",
                    "bp_vendor_food_camp_news_response",
                    "{=bp_hideoutvendordialog_106}What's new in camp? <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(ResetVendorChunker),
                    kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_food_camp_news_response",
                    "bp_vendor_food_camp_news_response",
                    "bp_vendor_food_camp_news_choice",
                    "{=!}{BP_VENDOR_CAMP_NEWS}",
                    new ConversationSentence.OnConditionDelegate(SetVendorCampNewsFood),
                    null, kPriority, null);
                starter.AddPlayerLine(
                    "bp_vendor_food_camp_news_more",
                    "bp_vendor_food_camp_news_choice",
                    "bp_vendor_food_camp_news_response",
                    "...",
                    new ConversationSentence.OnConditionDelegate(HasMoreVendorChunkerParas),
                    new ConversationSentence.OnConsequenceDelegate(AdvanceVendorChunkerPara),
                    kPriority + 10, null, null);
                starter.AddPlayerLine(
                    "bp_vendor_food_camp_news_done",
                    "bp_vendor_food_camp_news_choice",
                    "bp_vendor_food_relay",
                    "{=bp_hideoutvendordialog_107}Good to know.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(ResetVendorChunker),
                    kPriority, null, null);
                // Wave 4.14.2 (2026-06-06): relay state. Without this, the engine has no
                // AddDialogLine to play when returning to bp_vendor_food_root from a
                // chunker exit, and falls through to auto-selecting the highest-priority
                // player line ("Show me your wares" → trade screen opens unexpectedly).
                // Mirrors slaver Wave 4.13.4 fix.
                starter.AddDialogLine(
                    "bp_vendor_food_relay_line",
                    "bp_vendor_food_relay",
                    "bp_vendor_food_root",
                    "{=bp_hideoutvendordialog_108}Anything else?",
                    null, null, kPriority, null);

                // T2: bulk + road gossip
                starter.AddPlayerLine(
                    "bp_vendor_food_bulk",
                    "bp_vendor_food_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_109}Anything in bulk this week? <span style=\"Conversation.Persuasion.Positive\">(Trade)</span>",
                    new ConversationSentence.OnConditionDelegate(HasFoodVendorTier2),
                    new ConversationSentence.OnConsequenceDelegate(OnFoodVendorBulk),
                    kPriority, null, null);

                // Wave 4.14.4 (2026-06-07): chunker pattern for T2RoadGossipBody.
                starter.AddPlayerLine(
                    "bp_vendor_food_road_gossip",
                    "bp_vendor_food_root",
                    "bp_vendor_food_road_gossip_response",
                    "{=bp_hideoutvendordialog_110}What's the gossip from the road? <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(HasFoodVendorTier2),
                    new ConversationSentence.OnConsequenceDelegate(ResetVendorChunker),
                    kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_food_road_gossip_response",
                    "bp_vendor_food_road_gossip_response",
                    "bp_vendor_food_road_gossip_choice",
                    "{=!}{BP_VENDOR_ROAD_GOSSIP}",
                    new ConversationSentence.OnConditionDelegate(SetVendorRoadGossipFood),
                    null, kPriority, null);
                starter.AddPlayerLine(
                    "bp_vendor_food_road_gossip_more",
                    "bp_vendor_food_road_gossip_choice",
                    "bp_vendor_food_road_gossip_response",
                    "...",
                    new ConversationSentence.OnConditionDelegate(HasMoreVendorChunkerParas),
                    new ConversationSentence.OnConsequenceDelegate(AdvanceVendorChunkerPara),
                    kPriority + 10, null, null);
                starter.AddPlayerLine(
                    "bp_vendor_food_road_gossip_done",
                    "bp_vendor_food_road_gossip_choice",
                    "bp_vendor_food_relay",
                    "{=bp_hideoutvendordialog_111}Good to know.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(ResetVendorChunker),
                    kPriority, null, null);

                // T3: rare-piece (one-shot) + pact tease
                starter.AddPlayerLine(
                    "bp_vendor_food_rare",
                    "bp_vendor_food_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_112}What's the rarest piece you keep back? <span style=\"Conversation.Persuasion.Positive\">(Trade)</span>",
                    new ConversationSentence.OnConditionDelegate(CanFoodVendorBuyRare),
                    new ConversationSentence.OnConsequenceDelegate(OnFoodVendorRare),
                    kPriority, null, null);

                starter.AddPlayerLine(
                    "bp_vendor_food_pact",
                    "bp_vendor_food_root",
                    "bp_vendor_food_pact_response",
                    "{=bp_hideoutvendordialog_113}Will you supply my house when the time comes? <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(CanSwearFoodVendorPact),
                    null, kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_food_pact_response",
                    "bp_vendor_food_pact_response",
                    "close_window",
                    "{=bp_hideoutvendordialog_114}{BP_SPEAKER_NAME}: My larder won't run dry on you, traveler. When you've a banner of your own to raise, you'll know where the supply lines run. Walk careful — and walk back when you need.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(OnFoodVendorSwearPact),
                    kPriority, null);

                // === Wave 4.12-fix v60 (2026-05-11): food vendor tier-deepening ===
                // T2 craft secret (re-askable)
                // Wave 4.14.4 (2026-06-07): chunker pattern for T2CraftSecretBody.
                starter.AddPlayerLine(
                    "bp_vendor_food_craft",
                    "bp_vendor_food_root",
                    "bp_vendor_food_craft_response",
                    "{=bp_hideoutvendordialog_115}Tell me a secret of your craft. <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(HasFoodVendorTier2),
                    new ConversationSentence.OnConsequenceDelegate(ResetVendorChunker),
                    kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_food_craft_response",
                    "bp_vendor_food_craft_response",
                    "bp_vendor_food_craft_choice",
                    "{=!}{BP_VENDOR_CRAFT_DETAIL}",
                    new ConversationSentence.OnConditionDelegate(SetVendorCraftDetailFood),
                    null, kPriority, null);
                starter.AddPlayerLine(
                    "bp_vendor_food_craft_more",
                    "bp_vendor_food_craft_choice",
                    "bp_vendor_food_craft_response",
                    "...",
                    new ConversationSentence.OnConditionDelegate(HasMoreVendorChunkerParas),
                    new ConversationSentence.OnConsequenceDelegate(AdvanceVendorChunkerPara),
                    kPriority + 10, null, null);
                starter.AddPlayerLine(
                    "bp_vendor_food_craft_done",
                    "bp_vendor_food_craft_choice",
                    "bp_vendor_food_relay",
                    "{=bp_hideoutvendordialog_116}I see.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(ResetVendorChunker),
                    kPriority, null, null);

                // T2 custom order (one-shot, +100 denar goodwill)
                starter.AddPlayerLine(
                    "bp_vendor_food_custom",
                    "bp_vendor_food_root",
                    "bp_vendor_food_custom_response",
                    "{=bp_hideoutvendordialog_117}I'd like to place a custom order. <span style=\"Conversation.Persuasion.Positive\">(Trade)</span>",
                    new ConversationSentence.OnConditionDelegate(CanFoodVendorCustomOrder),
                    null, kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_food_custom_response",
                    "bp_vendor_food_custom_response",
                    "close_window",
                    "{=!}{BP_VENDOR_CUSTOM_ORDER}",
                    new ConversationSentence.OnConditionDelegate(SetVendorCustomOrderFood),
                    new ConversationSentence.OnConsequenceDelegate(OnFoodVendorCustomOrder),
                    kPriority, null);

                // v64: Custom Order PICKUP — visible when pending order has matured
                starter.AddPlayerLine(
                    "bp_vendor_food_custom_pickup",
                    "bp_vendor_food_root",
                    "bp_vendor_food_custom_pickup_response",
                    "{=bp_hideoutvendordialog_118}Is my custom order ready? <span style=\"Conversation.Persuasion.Positive\">(Trade)</span>",
                    new ConversationSentence.OnConditionDelegate(CanFoodVendorPickupCustomOrder),
                    null, kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_food_custom_pickup_response",
                    "bp_vendor_food_custom_pickup_response",
                    "close_window",
                    "{=bp_hideoutvendordialog_119}{BP_SPEAKER_NAME}: Aye, traveler — here's what you asked for. Walked the road for it twice. Carry it well.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(OnFoodVendorPickupCustomOrder),
                    kPriority, null);

                // v64: Custom Order WAITING — visible when pending but not yet matured
                starter.AddPlayerLine(
                    "bp_vendor_food_custom_waiting",
                    "bp_vendor_food_root",
                    "bp_vendor_food_custom_waiting_response",
                    "{=bp_hideoutvendordialog_120}How's my custom order coming? <span style=\"Conversation.Persuasion.Positive\">(Trade)</span>",
                    new ConversationSentence.OnConditionDelegate(IsFoodVendorOrderWaiting),
                    null, kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_food_custom_waiting_response",
                    "bp_vendor_food_custom_waiting_response",
                    "bp_vendor_food_root",
                    "{=bp_hideoutvendordialog_121}{BP_SPEAKER_NAME}: {BP_CUSTOM_ORDER_WAITING}",
                    new ConversationSentence.OnConditionDelegate(SetCustomOrderWaitingTextFood),
                    null, kPriority, null);

                // T3 family beat (re-askable)
                starter.AddPlayerLine(
                    "bp_vendor_food_family",
                    "bp_vendor_food_root",
                    "bp_vendor_food_family_response",
                    "{=bp_hideoutvendordialog_122}Tell me about your family. <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(HasFoodVendorTier3),
                    null, kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_food_family_response",
                    "bp_vendor_food_family_response",
                    "bp_vendor_food_root",
                    "{=!}{BP_VENDOR_FAMILY_BEAT}",
                    new ConversationSentence.OnConditionDelegate(SetVendorFamilyBeatFood),
                    null, kPriority, null);

                // T3 contacts introduction (one-shot, +250 denar goodwill)
                starter.AddPlayerLine(
                    "bp_vendor_food_contacts",
                    "bp_vendor_food_root",
                    "bp_vendor_food_contacts_response",
                    "{=bp_hideoutvendordialog_123}Will you introduce me to your contacts? <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(CanFoodVendorIntroduceContacts),
                    null, kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_food_contacts_response",
                    "bp_vendor_food_contacts_response",
                    "close_window",
                    "{=!}{BP_VENDOR_CONTACT_NAME}",
                    new ConversationSentence.OnConditionDelegate(SetVendorContactsFood),
                    new ConversationSentence.OnConsequenceDelegate(OnFoodVendorIntroduceContacts),
                    kPriority, null);

                // Next-tier offer chain (only reachable via "Got another job?")
                starter.AddDialogLine(
                    "bp_vendor_food_next_tier_offer",
                    "bp_vendor_food_next_tier_offer",
                    "bp_vendor_food_next_tier_root",
                    "{=bp_hideoutvendordialog_124}{BP_SPEAKER_NAME}: Aye, traveler — there's another job that needs doing. Bring me {BP_VENDOR_QUEST_DESC} on your next pass, and the stall opens up wider for you.",
                    new ConversationSentence.OnConditionDelegate(SetFoodNextTierVars),
                    null,
                    kPriority,
                    null);
                starter.AddPlayerLine(
                    "bp_vendor_food_next_tier_accept",
                    "bp_vendor_food_next_tier_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_125}Done. I'll bring what you ask. {BP_VENDOR_QUEST_DIFFICULTY_TAG}",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(AcceptFoodVendorQuest),
                    kPriority,
                    null,
                    null);
                starter.AddPlayerLine(
                    "bp_vendor_food_next_tier_decline",
                    "bp_vendor_food_next_tier_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_126}Maybe next time.",
                    null,
                    null,
                    kPriority,
                    null,
                    null);

                // Gate dialog (chief_trust >= 1 BUT vendor_trust = 0 AND no quest active)
                // This is the "the chief trusts you, but I don't" beat — vendor offers their
                // first bring-me-X quest. Once player accepts (or declines), state advances.
                starter.AddDialogLine(
                    "bp_vendor_food_gate",
                    "start",
                    "bp_vendor_food_gate_root",
                    "{=!}{BP_VENDOR_GATE_SPEECH}",
                    new ConversationSentence.OnConditionDelegate(IsFoodVendorGateWithDifficulty),
                    null,
                    kPriority,
                    null);
                starter.AddPlayerLine(
                    "bp_vendor_food_gate_accept",
                    "bp_vendor_food_gate_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_127}Fair terms. I'll bring what you ask. {BP_VENDOR_QUEST_DIFFICULTY_TAG}",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(AcceptFoodVendorQuest),
                    kPriority,
                    null,
                    null);
                starter.AddPlayerLine(
                    "bp_vendor_food_gate_decline",
                    "bp_vendor_food_gate_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_128}Not interested in your errands.",
                    null,
                    null,
                    kPriority,
                    null,
                    null);

                // Quest-active dialog (player accepted vendor quest, hasn't delivered).
                // The "I have your goods" player line is gated by HasFoodVendorQuestItems
                // so it only shows when the player actually has the items in inventory.
                starter.AddDialogLine(
                    "bp_vendor_food_quest_active",
                    "start",
                    "bp_vendor_food_quest_root",
                    "{=bp_hideoutvendordialog_129}{BP_SPEAKER_NAME}: Haven't forgotten our deal, traveler. {BP_VENDOR_QUEST_DESC} — that's what we agreed. Got them on you, or are you here to waste my afternoon?",
                    new ConversationSentence.OnConditionDelegate(IsFoodVendorQuestActive),
                    null,
                    kPriority,
                    null);
                // Deliver — only visible if items in hand. Routes to bp_vendor_food_root so
                // trade opens immediately after delivery (vendor's just earned a tier).
                starter.AddPlayerLine(
                    "bp_vendor_food_quest_deliver",
                    "bp_vendor_food_quest_root",
                    "bp_vendor_food_quest_delivered",
                    "{=bp_hideoutvendordialog_130}I have what you asked. {BP_VENDOR_QUEST_DIFFICULTY_TAG}",
                    new ConversationSentence.OnConditionDelegate(HasFoodVendorQuestItemsWithDifficulty),
                    new ConversationSentence.OnConsequenceDelegate(DeliverFoodVendorQuest),
                    kPriority,
                    null,
                    null);
                starter.AddDialogLine(
                    "bp_vendor_food_quest_delivered",
                    "bp_vendor_food_quest_delivered",
                    "bp_vendor_food_root",
                    "{=bp_hideoutvendordialog_131}{BP_SPEAKER_NAME}: Aye, you brought what we asked. The stall's yours, traveler — sit a while, I'll show you what's on offer now.",
                    null,
                    null,
                    kPriority,
                    null);
                starter.AddPlayerLine(
                    "bp_vendor_food_quest_soon",
                    "bp_vendor_food_quest_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_132}Soon. I'll bring them.",
                    null,
                    null,
                    kPriority,
                    null,
                    null);
                starter.AddPlayerLine(
                    "bp_vendor_food_quest_cancel",
                    "bp_vendor_food_quest_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_133}I've changed my mind. Cancel the deal.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(CancelFoodVendorQuest),
                    kPriority,
                    null,
                    null);

                // === GEAR VENDOR — same shape as food, different speaker text ===

                starter.AddDialogLine(
                    "bp_vendor_gear_start",
                    "start",
                    "bp_vendor_gear_root",
                    "{=bp_hideoutvendordialog_134}{BP_SPEAKER_NAME}: Welcome back, traveler. Steel's sharp, leather's good — coin's coin. What're you after?",
                    new ConversationSentence.OnConditionDelegate(IsGearVendorConv),
                    null,
                    kPriority,
                    null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_buy",
                    "bp_vendor_gear_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_135}Show me your gear. <span style=\"Conversation.Persuasion.Positive\">(Trade)</span>",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(OpenGearTrade),
                    kPriority,
                    null,
                    null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_ask_job",
                    "bp_vendor_gear_root",
                    "bp_vendor_gear_next_tier_offer",
                    "{=bp_hideoutvendordialog_136}Got another job for me? {BP_VENDOR_QUEST_DIFFICULTY_TAG}",
                    new ConversationSentence.OnConditionDelegate(CanOfferNextGearTierWithDifficulty),
                    null,
                    kPriority,
                    null,
                    null);
                // Wave 4.14.1 (2026-06-06): priority lowered so "Maybe later" sorts to BOTTOM
                // of the menu instead of mid-list (was between backstory and quest-offer).
                starter.AddPlayerLine(
                    "bp_vendor_gear_skip",
                    "bp_vendor_gear_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_137}Maybe later.",
                    null,
                    null,
                    kPriority - 100,
                    null,
                    null);

                // === Wave 4.11-fix v59: vendor T1/T2/T3 menu additions (gear) ===
                // Wave 4.14.1 (2026-06-06): chunker pattern (mirrors slaver Wave 4.13.5).
                starter.AddPlayerLine(
                    "bp_vendor_gear_aboutme",
                    "bp_vendor_gear_root",
                    "bp_vendor_gear_aboutme_response",
                    "{=bp_hideoutvendordialog_138}Tell me about yourself. <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(ResetVendorChunker),
                    kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_gear_aboutme_response",
                    "bp_vendor_gear_aboutme_response",
                    "bp_vendor_gear_aboutme_choice",
                    "{=!}{BP_VENDOR_BACKSTORY_TEXT}",
                    new ConversationSentence.OnConditionDelegate(SetVendorBackstoryGear),
                    null, kPriority, null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_aboutme_more",
                    "bp_vendor_gear_aboutme_choice",
                    "bp_vendor_gear_aboutme_response",
                    "...",
                    new ConversationSentence.OnConditionDelegate(HasMoreVendorChunkerParas),
                    new ConversationSentence.OnConsequenceDelegate(AdvanceVendorChunkerPara),
                    kPriority + 10, null, null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_aboutme_done",
                    "bp_vendor_gear_aboutme_choice",
                    "bp_vendor_gear_relay",
                    "{=bp_hideoutvendordialog_139}I see.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(ResetVendorChunker),
                    kPriority, null, null);

                starter.AddPlayerLine(
                    "bp_vendor_gear_camp_news",
                    "bp_vendor_gear_root",
                    "bp_vendor_gear_camp_news_response",
                    "{=bp_hideoutvendordialog_140}What's new in camp? <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(ResetVendorChunker),
                    kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_gear_camp_news_response",
                    "bp_vendor_gear_camp_news_response",
                    "bp_vendor_gear_camp_news_choice",
                    "{=!}{BP_VENDOR_CAMP_NEWS}",
                    new ConversationSentence.OnConditionDelegate(SetVendorCampNewsGear),
                    null, kPriority, null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_camp_news_more",
                    "bp_vendor_gear_camp_news_choice",
                    "bp_vendor_gear_camp_news_response",
                    "...",
                    new ConversationSentence.OnConditionDelegate(HasMoreVendorChunkerParas),
                    new ConversationSentence.OnConsequenceDelegate(AdvanceVendorChunkerPara),
                    kPriority + 10, null, null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_camp_news_done",
                    "bp_vendor_gear_camp_news_choice",
                    "bp_vendor_gear_relay",
                    "{=bp_hideoutvendordialog_141}Good to know.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(ResetVendorChunker),
                    kPriority, null, null);
                // Wave 4.14.2 (2026-06-06): gear relay, same purpose as food relay above.
                starter.AddDialogLine(
                    "bp_vendor_gear_relay_line",
                    "bp_vendor_gear_relay",
                    "bp_vendor_gear_root",
                    "{=bp_hideoutvendordialog_142}Anything else?",
                    null, null, kPriority, null);

                starter.AddPlayerLine(
                    "bp_vendor_gear_bulk",
                    "bp_vendor_gear_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_143}Anything in bulk this week? <span style=\"Conversation.Persuasion.Positive\">(Trade)</span>",
                    new ConversationSentence.OnConditionDelegate(HasGearVendorTier2),
                    new ConversationSentence.OnConsequenceDelegate(OnGearVendorBulk),
                    kPriority, null, null);

                // Wave 4.14.4 (2026-06-07): chunker pattern for T2RoadGossipBody (gear).
                starter.AddPlayerLine(
                    "bp_vendor_gear_road_gossip",
                    "bp_vendor_gear_root",
                    "bp_vendor_gear_road_gossip_response",
                    "{=bp_hideoutvendordialog_144}What's the gossip from the road? <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(HasGearVendorTier2),
                    new ConversationSentence.OnConsequenceDelegate(ResetVendorChunker),
                    kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_gear_road_gossip_response",
                    "bp_vendor_gear_road_gossip_response",
                    "bp_vendor_gear_road_gossip_choice",
                    "{=!}{BP_VENDOR_ROAD_GOSSIP}",
                    new ConversationSentence.OnConditionDelegate(SetVendorRoadGossipGear),
                    null, kPriority, null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_road_gossip_more",
                    "bp_vendor_gear_road_gossip_choice",
                    "bp_vendor_gear_road_gossip_response",
                    "...",
                    new ConversationSentence.OnConditionDelegate(HasMoreVendorChunkerParas),
                    new ConversationSentence.OnConsequenceDelegate(AdvanceVendorChunkerPara),
                    kPriority + 10, null, null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_road_gossip_done",
                    "bp_vendor_gear_road_gossip_choice",
                    "bp_vendor_gear_relay",
                    "{=bp_hideoutvendordialog_145}Good to know.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(ResetVendorChunker),
                    kPriority, null, null);

                starter.AddPlayerLine(
                    "bp_vendor_gear_rare",
                    "bp_vendor_gear_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_146}What's the rarest piece you keep back? <span style=\"Conversation.Persuasion.Positive\">(Trade)</span>",
                    new ConversationSentence.OnConditionDelegate(CanGearVendorBuyRare),
                    new ConversationSentence.OnConsequenceDelegate(OnGearVendorRare),
                    kPriority, null, null);

                starter.AddPlayerLine(
                    "bp_vendor_gear_pact",
                    "bp_vendor_gear_root",
                    "bp_vendor_gear_pact_response",
                    "{=bp_hideoutvendordialog_147}Will you supply my house when the time comes? <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(CanSwearGearVendorPact),
                    null, kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_gear_pact_response",
                    "bp_vendor_gear_pact_response",
                    "close_window",
                    "{=bp_hideoutvendordialog_148}{BP_SPEAKER_NAME}: When you've a banner of your own to raise, traveler, my goods will fly under it. Steel, salt, or silk — whatever your campaigns need, you'll find it on my rack. Walk careful, and walk back armed.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(OnGearVendorSwearPact),
                    kPriority, null);

                // === Wave 4.12-fix v60 (2026-05-11): gear vendor tier-deepening ===
                // T2 craft secret (re-askable)
                // Wave 4.14.4 (2026-06-07): chunker pattern for T2CraftSecretBody (gear).
                starter.AddPlayerLine(
                    "bp_vendor_gear_craft",
                    "bp_vendor_gear_root",
                    "bp_vendor_gear_craft_response",
                    "{=bp_hideoutvendordialog_149}Tell me a secret of your craft. <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(HasGearVendorTier2),
                    new ConversationSentence.OnConsequenceDelegate(ResetVendorChunker),
                    kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_gear_craft_response",
                    "bp_vendor_gear_craft_response",
                    "bp_vendor_gear_craft_choice",
                    "{=!}{BP_VENDOR_CRAFT_DETAIL}",
                    new ConversationSentence.OnConditionDelegate(SetVendorCraftDetailGear),
                    null, kPriority, null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_craft_more",
                    "bp_vendor_gear_craft_choice",
                    "bp_vendor_gear_craft_response",
                    "...",
                    new ConversationSentence.OnConditionDelegate(HasMoreVendorChunkerParas),
                    new ConversationSentence.OnConsequenceDelegate(AdvanceVendorChunkerPara),
                    kPriority + 10, null, null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_craft_done",
                    "bp_vendor_gear_craft_choice",
                    "bp_vendor_gear_relay",
                    "{=bp_hideoutvendordialog_150}I see.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(ResetVendorChunker),
                    kPriority, null, null);

                // T2 custom order (one-shot, +100 denar goodwill)
                starter.AddPlayerLine(
                    "bp_vendor_gear_custom",
                    "bp_vendor_gear_root",
                    "bp_vendor_gear_custom_response",
                    "{=bp_hideoutvendordialog_151}I'd like to place a custom order. <span style=\"Conversation.Persuasion.Positive\">(Trade)</span>",
                    new ConversationSentence.OnConditionDelegate(CanGearVendorCustomOrder),
                    null, kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_gear_custom_response",
                    "bp_vendor_gear_custom_response",
                    "close_window",
                    "{=!}{BP_VENDOR_CUSTOM_ORDER}",
                    new ConversationSentence.OnConditionDelegate(SetVendorCustomOrderGear),
                    new ConversationSentence.OnConsequenceDelegate(OnGearVendorCustomOrder),
                    kPriority, null);

                // v64: Custom Order PICKUP — visible when pending order has matured
                starter.AddPlayerLine(
                    "bp_vendor_gear_custom_pickup",
                    "bp_vendor_gear_root",
                    "bp_vendor_gear_custom_pickup_response",
                    "{=bp_hideoutvendordialog_152}Is my custom order ready? <span style=\"Conversation.Persuasion.Positive\">(Trade)</span>",
                    new ConversationSentence.OnConditionDelegate(CanGearVendorPickupCustomOrder),
                    null, kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_gear_custom_pickup_response",
                    "bp_vendor_gear_custom_pickup_response",
                    "close_window",
                    "{=bp_hideoutvendordialog_153}{BP_SPEAKER_NAME}: Aye, traveler — forged it overnight and oiled it twice. Take it; the anvil's done what you paid for.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(OnGearVendorPickupCustomOrder),
                    kPriority, null);

                // v64: Custom Order WAITING — visible when pending but not yet matured
                starter.AddPlayerLine(
                    "bp_vendor_gear_custom_waiting",
                    "bp_vendor_gear_root",
                    "bp_vendor_gear_custom_waiting_response",
                    "{=bp_hideoutvendordialog_154}How's my custom order coming? <span style=\"Conversation.Persuasion.Positive\">(Trade)</span>",
                    new ConversationSentence.OnConditionDelegate(IsGearVendorOrderWaiting),
                    null, kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_gear_custom_waiting_response",
                    "bp_vendor_gear_custom_waiting_response",
                    "bp_vendor_gear_root",
                    "{=bp_hideoutvendordialog_173}{BP_SPEAKER_NAME}: {BP_CUSTOM_ORDER_WAITING}",
                    new ConversationSentence.OnConditionDelegate(SetCustomOrderWaitingTextGear),
                    null, kPriority, null);

                // T3 family beat (re-askable)
                starter.AddPlayerLine(
                    "bp_vendor_gear_family",
                    "bp_vendor_gear_root",
                    "bp_vendor_gear_family_response",
                    "{=bp_hideoutvendordialog_155}Tell me about your family. <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(HasGearVendorTier3),
                    null, kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_gear_family_response",
                    "bp_vendor_gear_family_response",
                    "bp_vendor_gear_root",
                    "{=!}{BP_VENDOR_FAMILY_BEAT}",
                    new ConversationSentence.OnConditionDelegate(SetVendorFamilyBeatGear),
                    null, kPriority, null);

                // T3 contacts introduction (one-shot, +250 denar goodwill)
                starter.AddPlayerLine(
                    "bp_vendor_gear_contacts",
                    "bp_vendor_gear_root",
                    "bp_vendor_gear_contacts_response",
                    "{=bp_hideoutvendordialog_156}Will you introduce me to your contacts? <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(CanGearVendorIntroduceContacts),
                    null, kPriority, null, null);
                starter.AddDialogLine(
                    "bp_vendor_gear_contacts_response",
                    "bp_vendor_gear_contacts_response",
                    "close_window",
                    "{=!}{BP_VENDOR_CONTACT_NAME}",
                    new ConversationSentence.OnConditionDelegate(SetVendorContactsGear),
                    new ConversationSentence.OnConsequenceDelegate(OnGearVendorIntroduceContacts),
                    kPriority, null);

                starter.AddDialogLine(
                    "bp_vendor_gear_next_tier_offer",
                    "bp_vendor_gear_next_tier_offer",
                    "bp_vendor_gear_next_tier_root",
                    "{=bp_hideoutvendordialog_157}{BP_SPEAKER_NAME}: Aye, traveler — the forge needs more. Bring me {BP_VENDOR_QUEST_DESC} on your next pass, and you'll see what real steel my stall keeps for partners.",
                    new ConversationSentence.OnConditionDelegate(SetGearNextTierVars),
                    null,
                    kPriority,
                    null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_next_tier_accept",
                    "bp_vendor_gear_next_tier_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_158}Done. I'll bring what you ask. {BP_VENDOR_QUEST_DIFFICULTY_TAG}",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(AcceptGearVendorQuest),
                    kPriority,
                    null,
                    null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_next_tier_decline",
                    "bp_vendor_gear_next_tier_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_159}Maybe next time.",
                    null,
                    null,
                    kPriority,
                    null,
                    null);

                starter.AddDialogLine(
                    "bp_vendor_gear_gate",
                    "start",
                    "bp_vendor_gear_gate_root",
                    "{=!}{BP_VENDOR_GATE_SPEECH}",
                    new ConversationSentence.OnConditionDelegate(IsGearVendorGateWithDifficulty),
                    null,
                    kPriority,
                    null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_gate_accept",
                    "bp_vendor_gear_gate_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_160}Fair terms. I'll bring what you ask. {BP_VENDOR_QUEST_DIFFICULTY_TAG}",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(AcceptGearVendorQuest),
                    kPriority,
                    null,
                    null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_gate_decline",
                    "bp_vendor_gear_gate_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_161}Not interested in your errands.",
                    null,
                    null,
                    kPriority,
                    null,
                    null);

                starter.AddDialogLine(
                    "bp_vendor_gear_quest_active",
                    "start",
                    "bp_vendor_gear_quest_root",
                    "{=bp_hideoutvendordialog_162}{BP_SPEAKER_NAME}: Haven't forgotten our deal, traveler. {BP_VENDOR_QUEST_DESC} — that's the count. Got them on you, or are we still waiting?",
                    new ConversationSentence.OnConditionDelegate(IsGearVendorQuestActive),
                    null,
                    kPriority,
                    null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_quest_deliver",
                    "bp_vendor_gear_quest_root",
                    "bp_vendor_gear_quest_delivered",
                    "{=bp_hideoutvendordialog_163}I have what you asked. {BP_VENDOR_QUEST_DIFFICULTY_TAG}",
                    new ConversationSentence.OnConditionDelegate(HasGearVendorQuestItemsWithDifficulty),
                    new ConversationSentence.OnConsequenceDelegate(DeliverGearVendorQuest),
                    kPriority,
                    null,
                    null);
                starter.AddDialogLine(
                    "bp_vendor_gear_quest_delivered",
                    "bp_vendor_gear_quest_delivered",
                    "bp_vendor_gear_root",
                    "{=bp_hideoutvendordialog_164}{BP_SPEAKER_NAME}: Aye, you brought what we asked. The rack's yours, traveler — see what real steel I keep for those who come through.",
                    null,
                    null,
                    kPriority,
                    null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_quest_soon",
                    "bp_vendor_gear_quest_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_165}Soon. I'll bring them.",
                    null,
                    null,
                    kPriority,
                    null,
                    null);
                starter.AddPlayerLine(
                    "bp_vendor_gear_quest_cancel",
                    "bp_vendor_gear_quest_root",
                    "close_window",
                    "{=bp_hideoutvendordialog_166}I've changed my mind. Cancel the deal.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(CancelGearVendorQuest),
                    kPriority,
                    null,
                    null);

                // === Chief-rebuff (chief_trust = 0). Unchanged from Wave 4.6. ===
                starter.AddDialogLine(
                    "bp_vendor_food_rebuff",
                    "start",
                    "close_window",
                    "{=bp_hideoutvendordialog_167}{BP_SPEAKER_NAME}: We don't deal with strangers, traveler. Talk to the chief — earn his trust — then come back with coin.",
                    new ConversationSentence.OnConditionDelegate(IsFoodVendorRebuff),
                    null,
                    kPriority,
                    null);

                starter.AddDialogLine(
                    "bp_vendor_gear_rebuff",
                    "start",
                    "close_window",
                    "{=bp_hideoutvendordialog_168}{BP_SPEAKER_NAME}: Steel and leather aren't for strangers. Earn the chief's trust first — then I'll think about selling to you.",
                    new ConversationSentence.OnConditionDelegate(IsGearVendorRebuff),
                    null,
                    kPriority,
                    null);

                // Wave 4.12-fix12 (2026-05-11): gear vendor requires food vendor Tier-2 done.
                // This rebuff fires when player has earned the chief's trust (passed
                // IsGearVendorRebuff) but hasn't yet earned the food-master's full trust.
                // Priority is one above the standard rebuff so it wins when both could match.
                starter.AddDialogLine(
                    "bp_vendor_gear_needs_food_t2",
                    "start",
                    "close_window",
                    "{=bp_hideoutvendordialog_169}{BP_SPEAKER_NAME}: The food-master's still got measure to take of you. Come back when your name carries through the cookhouse — then we'll talk steel.",
                    new ConversationSentence.OnConditionDelegate(IsGearVendorNeedsFoodT2),
                    null,
                    kPriority + 5,
                    null);

                HideoutPeacefulVisitState.Log("HideoutVendorDialog: registered v41 vendor flow (chief-rebuff / gate / quest-active / next-tier / trade)");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("HideoutVendorDialog.OnSessionLaunched fail: " + ex.Message);
            }
        }

        // Helpers used by the next-tier-offer chief response: populate {BP_VENDOR_QUEST_DESC}
        // before the chief's line renders so the displayed item count + name is correct.
        private static bool SetFoodNextTierVars()
        {
            int next = GetFoodVendorTrust() + 1;
            return SetVendorQuestVariables("food", next);
        }
        private static bool SetGearNextTierVars()
        {
            int next = GetGearVendorTrust() + 1;
            return SetVendorQuestVariables("gear", next);
        }

        // DEFERRED-TRADE 2026-04-30: opening the trade UI directly from the dialog consequence
        // causes the player to get stuck in conversation state after closing trade — the
        // conversation hadn't fully ended when trade took over the UI. Solution: dialog
        // consequence sets a flag, conversation closes naturally on next frame, then the
        // mission tick (in HideoutVisitNpcSpawnBehavior.OnMissionTick) sees the flag and
        // opens the trade screen ONE FRAME LATER, when the player is no longer in convo.
        public static volatile bool PendingFoodTrade;
        public static volatile bool PendingGearTrade;
        // Wave 4.11-fix v59 (2026-05-08): bulk-trade and rare-item pending flags. Same
        // deferred-trade pattern as the standard trade — set the flag in dialog
        // consequence, mission tick reads on next frame and opens the trade screen.
        public static volatile bool PendingBulkFood;
        public static volatile bool PendingBulkGear;
        public static volatile bool PendingRareFood;
        public static volatile bool PendingRareGear;

        // Wave 4.11-fix v41 (2026-05-07): defense-in-depth — even if dialog gating is
        // bypassed, the consequence re-validates VENDOR trust (not chief) before queuing
        // the trade screen.
        // Wave 4.12-fix v62 (2026-05-11): pick a real Town's SettlementComponent for
        // the trade screen instead of the Hideout's. Bannerlord's InventoryLogic
        // computes sell prices through MarketData (supply/demand × prosperity ×
        // MerchantsGoldCount). Town SettlementComponent has MarketData; Hideout
        // does NOT — so sell prices collapsed to 0. Substituting the nearest town's
        // component gives the trade screen real market data → sells work.
        //
        // Side-effect tradeoff: prices reflect the nearest town's market. That's
        // arguably more realistic (bandit camps fence to the nearest town) than
        // pretending the hideout has its own internal economy.
        private static TaleWorlds.CampaignSystem.Settlements.SettlementComponent GetNearestTownComponent()
        {
            try
            {
                var current = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement;
                if (current == null) return null;
                // Compute simple Euclidean distance via Settlement.Position
                // (CampaignVec2, has X/Y). Settlement.Position2D isn't directly
                // accessible in 1.2.x; MapDistanceModel.GetDistance(Settlement,
                // Settlement) needs 5+ args including NavigationType which adds
                // dependency surface. Euclidean is fine for "pick the nearest
                // town" — we don't need pathfinding accuracy.
                var cp = current.Position;
                TaleWorlds.CampaignSystem.Settlements.Settlement bestTown = null;
                float bestDistSq = float.MaxValue;
                foreach (var s in TaleWorlds.CampaignSystem.Settlements.Settlement.All)
                {
                    if (s == null || !s.IsTown) continue;
                    var sp = s.Position;
                    float dx = sp.X - cp.X;
                    float dy = sp.Y - cp.Y;
                    float dSq = dx * dx + dy * dy;
                    if (dSq < bestDistSq) { bestDistSq = dSq; bestTown = s; }
                }
                return bestTown?.Town;
            }
            catch { return null; }
        }

        private static void OpenFoodTrade()
        {
            if (GetFoodVendorTrust() < 1) return;
            PendingFoodTrade = true;
        }
        private static void OpenGearTrade()
        {
            if (GetGearVendorTrust() < 1) return;
            PendingGearTrade = true;
        }

        // Called from HideoutVisitNpcSpawnBehavior.OnMissionTick — runs the actual trade
        // screen open if either flag is set, then clears the flag. Tier read here (not at
        // queue time) so freshly-incremented trust applies immediately if the player just
        // delivered a quest and is now opening trade in the same conversation.
        public static void TickProcessPendingTrade()
        {
            try
            {
                if (PendingFoodTrade)
                {
                    PendingFoodTrade = false;
                    int tier = GetFoodVendorTrust();
                    var pool = GetFoodPool(tier);
                    // Wave 4.14.5 (2026-06-07): random partial picks preserved (each
                    // visit different). Pool itself expanded at T3 to be "everything
                    // food/drink", so the random selection feels rich + varied.
                    // T1: 6 picks, qty 4-18. T2: 8 picks, qty 5-22. T3: 14 picks, qty 8-32.
                    int picks = 6 + (tier - 1) * 2 + (tier >= 3 ? 4 : 0);
                    int minQty = 4 + (tier - 1) + (tier >= 3 ? 4 : 0);
                    int maxQty = 18 + (tier - 1) * 4 + (tier >= 3 ? 6 : 0);
                    OpenTradeWith(pool, "food T" + tier, minCount: minQty, maxCount: maxQty, picks: picks);
                }
                else if (PendingGearTrade)
                {
                    PendingGearTrade = false;
                    int tier = GetGearVendorTrust();
                    var pool = GetGearPool(tier);
                    // Wave 4.14.6 (2026-06-07): gear pools are now TIER-DISTINCT (T3 has
                    // ONLY elite items, no peasant carry-overs). Picks scaled so randomness
                    // is preserved — vendor never stocks the entire pool at once.
                    // T1: 5 picks qty 1-3 (random from 8-item peasant pool).
                    // T2: 8 picks qty 1-4 (random from 16-item soldier pool).
                    // T3: 11 picks qty 1-6 (random from 16-item elite pool — fresh mix per visit).
                    int picks = 5 + (tier - 1) * 3;
                    int minQty = 1;
                    int maxQty = 2 + tier + (tier >= 3 ? 1 : 0);
                    OpenTradeWith(pool, "gear T" + tier, minCount: minQty, maxCount: maxQty, picks: picks);
                }
                // Wave 4.11-fix v59: bulk-trade — single-item lot at fixed quantity.
                else if (PendingBulkFood)
                {
                    PendingBulkFood = false;
                    OpenSingleItemTrade("grain", 30, "food bulk");
                }
                else if (PendingBulkGear)
                {
                    PendingBulkGear = false;
                    OpenSingleItemTrade("hardwood", 20, "gear bulk");
                }
                // Rare-piece — one premium item per culture per save (flagged at consequence).
                else if (PendingRareFood)
                {
                    PendingRareFood = false;
                    OpenSingleItemTrade("wine", 1, "food rare");
                }
                else if (PendingRareGear)
                {
                    PendingRareGear = false;
                    OpenSingleItemTrade("courser", 1, "gear rare");
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("HideoutVendorDialog.TickProcessPendingTrade fail: " + ex.Message);
                PendingFoodTrade = false;
                PendingGearTrade = false;
                PendingBulkFood = false;
                PendingBulkGear = false;
                PendingRareFood = false;
                PendingRareGear = false;
            }
        }

        // Wave 4.11-fix v59 (2026-05-08): single-item trade-screen helper for bulk
        // and rare-piece flows. Builds an ItemRoster with just the one item × count
        // and opens the same OpenScreenAsTrade path the standard trade uses.
        private static void OpenSingleItemTrade(string itemId, int count, string label)
        {
            try
            {
                var roster = new ItemRoster();
                var item = MBObjectManager.Instance.GetObject<ItemObject>(itemId);
                if (item == null)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        BandItPlus.Localization.Get("bp_hideoutvendordialog_170", "Vendor: \"That stock didn't come in this season, friend.\""), Colors.Yellow));
                    HideoutPeacefulVisitState.Log("OpenSingleItemTrade: item id '" + itemId + "' not found for '" + label + "'");
                    return;
                }
                roster.AddToCounts(item, count);
                HideoutPeacefulVisitState.Log("HideoutVendorDialog: opening " + label + " trade with " + count + "× " + itemId);

                bool tradeOpened = false;
                try
                {
                    // Wave 4.12-fix v62 (2026-05-11): use nearest Town's SC for real
                    // market data (sell prices fix). Fall back to hideout SC only if
                    // no town found (shouldn't happen in normal play).
                    var sc = GetNearestTownComponent()
                        ?? TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement?.SettlementComponent;
                    if (sc != null)
                    {
                        InventoryScreenHelper.OpenScreenAsTrade(roster, sc, InventoryScreenHelper.InventoryCategoryType.None, null);
                        tradeOpened = true;
                        HideoutPeacefulVisitState.Log("OpenSingleItemTrade: opened TRADE screen with " + sc.GetType().Name);
                    }
                }
                catch (Exception trEx)
                {
                    HideoutPeacefulVisitState.Log("OpenSingleItemTrade: OpenScreenAsTrade failed: " + trEx.Message);
                }

                if (!tradeOpened)
                {
                    try
                    {
                        InventoryScreenHelper.OpenScreenAsReceiveItems(roster,
                            new TextObject("{=bp_vendor_special}Vendor's special offer"), null);
                    }
                    catch (Exception riEx)
                    {
                        HideoutPeacefulVisitState.Log("OpenSingleItemTrade: receive-items fallback also failed: " + riEx.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("OpenSingleItemTrade(" + label + ") EXCEPTION: " + ex.Message);
            }
        }

        // Build a randomized ItemRoster from a pool, then ask vanilla to open the trade screen.
        // SettlementComponent is null because we're not in a real merchant settlement — vanilla
        // accepts this in 1.2.x for ad-hoc merchant interactions. If the call fails, fall back
        // to a chat-message acknowledgment so the player gets feedback either way.
        private static void OpenTradeWith(string[] pool, string label, int minCount, int maxCount, int picks = 8)
        {
            try
            {
                var roster = new ItemRoster();
                // Wave 4.11-fix v41: picks count now passed in by caller (tier-aware) with
                // small +/- jitter so visits don't feel mechanically identical.
                if (picks < 1) picks = 1;
                picks = picks + MBRandom.RandomInt(3) - 1;   // ±1 jitter
                if (picks < 1) picks = 1;
                if (picks > pool.Length) picks = pool.Length;
                var taken = new HashSet<int>();
                int added = 0;
                for (int i = 0; i < picks * 4 && added < picks; i++)
                {
                    int idx = MBRandom.RandomInt(pool.Length);
                    if (taken.Contains(idx)) continue;
                    taken.Add(idx);
                    var item = MBObjectManager.Instance.GetObject<ItemObject>(pool[idx]);
                    if (item == null) continue;
                    int count = minCount + MBRandom.RandomInt(maxCount - minCount + 1);
                    roster.AddToCounts(item, count);
                    added++;
                }
                HideoutPeacefulVisitState.Log("HideoutVendorDialog: opening " + label + " trade with " + added + " distinct items");

                if (added == 0)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        BandItPlus.Localization.Get("bp_hideoutvendordialog_171", "Vendor: \"Sorry friend, my stock's empty today.\""), Colors.Yellow));
                    return;
                }

                // OpenScreenAsTrade NREs with null SettlementComponent (vanilla expects a real
                // merchant-backed settlement). Try the current settlement's component first
                // (hideout's Hideout component is a SettlementComponent subclass — may or may
                // not work depending on what the trade screen reads). If that NREs too, fall
                // back to OpenScreenAsReceiveItems which presents the items as a "free gift"
                // pickup — not real trade with pricing, but a working UI the player can use
                // to take vendor wares. Honest about the limitation: vendor "gives" stock.
                bool tradeOpened = false;
                try
                {
                    // Wave 4.12-fix v62 (2026-05-11): use nearest Town's SC instead of
                    // the Hideout's. Hideout SC has no MarketData → sell prices return
                    // 0. Town SC has MarketData → sells return real denars. Falls back
                    // to hideout SC only if no town found (defensive).
                    var sc = GetNearestTownComponent()
                        ?? TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement?.SettlementComponent;
                    if (sc != null)
                    {
                        InventoryScreenHelper.OpenScreenAsTrade(roster, sc, InventoryScreenHelper.InventoryCategoryType.None, null);
                        tradeOpened = true;
                        HideoutPeacefulVisitState.Log("HideoutVendorDialog: opened TRADE screen with " + sc.GetType().Name);
                    }
                }
                catch (Exception trEx)
                {
                    HideoutPeacefulVisitState.Log("HideoutVendorDialog: OpenScreenAsTrade failed (" + trEx.GetType().Name + "): " + trEx.Message + " — falling back to receive-items");
                }

                if (!tradeOpened)
                {
                    try
                    {
                        InventoryScreenHelper.OpenScreenAsReceiveItems(roster,
                            new TextObject("{=bp_vendor_stock}Vendor's stock"), null);
                        HideoutPeacefulVisitState.Log("HideoutVendorDialog: opened RECEIVE-ITEMS screen as fallback");
                    }
                    catch (Exception riEx)
                    {
                        HideoutPeacefulVisitState.Log("HideoutVendorDialog: OpenScreenAsReceiveItems also failed (" + riEx.GetType().Name + "): " + riEx.Message);
                        InformationManager.DisplayMessage(new InformationMessage(
                            BandItPlus.Localization.Get("bp_hideoutvendordialog_172", "Vendor: \"Trade UI isn't working right now, traveler. Come back later.\""), Colors.Yellow));
                    }
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("HideoutVendorDialog.OpenTradeWith(" + label + ") EXCEPTION: " + ex.Message);
            }
        }
    }
}
