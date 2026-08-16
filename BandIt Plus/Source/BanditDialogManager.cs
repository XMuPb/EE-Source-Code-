using System;
using System.Collections.Generic;
using BandItPlus.Cultures;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Extensions;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace BandItPlus
{
    public class BanditDialogManager : CampaignBehaviorBase
    {
        // === Wave 5 alliance dialog priority table (DO NOT collide) =====================
        // All alliance branches use the default priority (no explicit priority arg on the
        // AddPlayerLine / AddDialogLine overload). They are made mutually exclusive via
        // condition predicates so the registration-order rule per memory note
        // bannerlord-dialog-priority-registration-order does not bite. Conditions are ALL
        // evaluated against MobileParty/Hero context at dialog-open time — there is no
        // shared mutex.
        //
        //   Branch id (output dialog state)          Player line id                 Condition predicate
        //   ----------------------------------------  -----------------------------  -------------------------------
        //   bp_alliance_offer_chief (chief greets)    bp_alliance_offer_player       IsAllianceOfferEligible        (T29)
        //     -> bp_alliance_offer_choice
        //         -> bp_alliance_ch1_accept_chief     bp_alliance_ch1_accept         (always after offer)            (T29 + T30 line)
        //         -> bp_alliance_ch1_decline_chief    bp_alliance_ch1_decline        (always after offer)            (T29)
        //   (Branches 2-6: Ch1 progress check-in / Ch2 trial accept / Ch2 trial result /
        //    Summon Warband / break-alliance lever — added in T30+.)
        // =================================================================================

        // Inline lore for all 14 bandit cultures (8 BP + 6 vanilla). Three topics per culture.
        private static readonly Dictionary<string, string> Lore = new Dictionary<string, string>
        {
            { "bp_frost_reavers",      "{=bp_dialogmanager_001}We are the Frost Reavers. The cold is our blade and our shield. Where lords' columns freeze and starve, we feast. The mountain villages pay us in salt and silver, and we leave them their roofs." },
            { "bp_marsh_stalkers",     "{=bp_dialogmanager_002}We are the Marsh Stalkers. The fens hide us better than any wall. We know every sinkhole, every floating log. A cataphract chasing a Stalker into the marsh becomes a drowned cataphract." },
            { "bp_highwaymen",         "{=bp_dialogmanager_003}Highwaymen, plain and clear. We work the trade roads — caravans, tax-wagons, fat merchants who paid for soft beds and no escort. We take the purse and leave the body, mostly." },
            { "bp_slaver_caravans",    "{=bp_dialogmanager_004}Slavers. We move what no one admits they want — debtors, captives, the unwanted children of the powerful. Every empire pretends we don't exist. Every empire's coin reaches our hands." },
            { "bp_fallen_legionaries", "{=bp_dialogmanager_005}Once we marched under the eagle. The legion was disbanded; the back-pay never came. Now we wear our old armor and call ourselves what we please. The empire taught us how to kill, and we have been generous with the lesson." },
            { "bp_sky_raiders",        "{=bp_dialogmanager_006}Sky Raiders — what others call the falconers of the cliffs. We strike from the high passes and vanish before the heralds finish naming us. Birds hunt for us, and we hunt for ourselves." },
            { "bp_steppe_wolves",      "{=bp_dialogmanager_007}We are the wolves of the open plain. No walls, no towns, no tax collectors. We follow the herds and the weak caravans, and we pay no homage to any khan." },
            { "bp_pagan_cult",         "{=bp_dialogmanager_008}We keep the old rites. The new gods of the empire are pale things, born of paper. Our god drinks blood and grants us the courage to take what we need. The lords call us bandits because they fear what we remember." },
            { "looters",               "{=bp_dialogmanager_009}We are no clan. We are hungry men with knives. The lords' wars made us this, and the lords' tax-men keep us this way. If you have bread, share it. If not, well — we'll see." },
            { "forest_bandits",        "{=bp_dialogmanager_010}The forest is our roof. We hunt boar, deer, and the occasional fool who thought the woods belonged to his lord. The trees know us better than any village priest." },
            { "mountain_bandits",      "{=bp_dialogmanager_011}We hold the high passes. Every traveler over the spine of the world meets us, sooner or later. Most pay; some fight; a few we send back with their boots so they can warn the next caravan." },
            { "steppe_bandits",        "{=bp_dialogmanager_012}Riders of the open. We were Khuzait once, or Aserai, or sons of nameless mothers — it does not matter now. The horse and the bow are our home." },
            { "sea_raiders",           "{=bp_dialogmanager_013}We came from the cold north, where the sun forgets the land. The empire's coastal towns are soft and full. We take what we need and leave with the tide before their levies remember which spear is theirs." },
            { "desert_bandits",        "{=bp_dialogmanager_014}We rule the dunes between oases. Caravans pay us safe-passage or pay us in blood — both flow about the same in the dry country. The sun is on our side, not yours." },
        };

        private static readonly Dictionary<string, string> History = new Dictionary<string, string>
        {
            { "bp_frost_reavers",      "{=bp_dialogmanager_015}Three winters past, the Sturgian lord we served forgot our pay. We took the larders and the silver and walked into the mountains. The lord froze in his hall waiting for taxes that never came. We have not been hungry since." },
            { "bp_marsh_stalkers",     "{=bp_dialogmanager_016}Our grandfathers were river-people, fishermen and reed-cutters. The empire drained the lowlands for farms; the farms failed; the people scattered. Some of us went back to the marshes the empire abandoned. We never left." },
            { "bp_highwaymen",         "{=bp_dialogmanager_017}We were caravan guards once, most of us. The merchants paid us to fight off bandits — until the merchants stopped paying, blamed us for losses, and sold our names to the magistrates. We became what we were paid to fight. Better wages." },
            { "bp_slaver_caravans",    "{=bp_dialogmanager_018}The trade is older than the empire. Long before the eagle, we moved bodies across the southern sands. Empires rise and fall; the chains stay. We are not proud of what we do. We are not ashamed either." },
            { "bp_fallen_legionaries", "{=bp_dialogmanager_019}Twelfth Legion, second cohort, disbanded after the Battanian campaign. The senate voted us out of existence to save coin. We voted ourselves back in, with different colors. Some of the old centurions still drill the boys. The empire trained us too well to be wasted on farming." },
            { "bp_sky_raiders",        "{=bp_dialogmanager_020}We are the children of the cliff-villages, where the falconers used to train hunting birds for the noble houses. The houses fell; the birds remained; we learned to feed ourselves. The cliffs hide us still." },
            { "bp_steppe_wolves",      "{=bp_dialogmanager_021}We were a clan of the great khanate, until the khan's brother sold our pastures to a Vlandian count. We rode east, then south, then anywhere the count's men could not reach. The grass is everywhere; the count is not." },
            { "bp_pagan_cult",         "{=bp_dialogmanager_022}When the empire's missionaries burned the sacred grove at the bend of the river, the elders cursed them and walked into the deep wood. We are the children of that walking. The grove is gone; the curse runs in our blood." },
            { "looters",               "{=bp_dialogmanager_023}There is no story. We were farmers, or porters, or soldiers, or beggars. The war came; the harvest failed; the lord died and his cousin raised the rents. We took to the road. There are thousands of us. The lords made us." },
            { "forest_bandits",        "{=bp_dialogmanager_024}Long ago the forest was free — anyone could hunt, anyone could cut wood. Then a lord drew a line on a map and called the trees his. Our great-grandfathers chose the trees over the lord. We are still choosing." },
            { "mountain_bandits",      "{=bp_dialogmanager_025}Our people held these passes before the empire had a name for them. We charged tolls under the old kings and we charge them now. The flag changes; the road does not." },
            { "steppe_bandits",        "{=bp_dialogmanager_026}We split from the great clans three generations back, when the council ruled that small clans must pay tribute to the great. We pay tribute to no one. The price is loneliness, and we have grown used to it." },
            { "sea_raiders",           "{=bp_dialogmanager_027}Our fathers raided these coasts; our grandfathers raided them; the songs in our halls go back to the cold-water kings. The empire calls it crime. We call it inheritance." },
            { "desert_bandits",        "{=bp_dialogmanager_028}The sultan's tax men rode out one summer and never came back. The next summer no men rode out. The sand has long memory. So do we." },
        };

        private static readonly Dictionary<string, string> Lords = new Dictionary<string, string>
        {
            { "bp_frost_reavers",      "{=bp_dialogmanager_029}Plenty. A Sturgian count brought a hundred druzhinniks last spring. Eighty walked back without their cloaks. He hasn't sent another column since." },
            { "bp_marsh_stalkers",     "{=bp_dialogmanager_030}Lords come, lords drown. We don't fight them — the marsh does." },
            { "bp_highwaymen",         "{=bp_dialogmanager_031}We avoid lords with banners. We hunt their tax-collectors instead. A magistrate's purse weighs as much as a knight's, and the magistrate has no escort." },
            { "bp_slaver_caravans",    "{=bp_dialogmanager_032}The lords pretend to oppose us in public and buy from us in private. It is a comfortable arrangement for everyone except the merchandise." },
            { "bp_fallen_legionaries", "{=bp_dialogmanager_033}We fought a Vlandian baron's column three weeks ago. He had cataphracts; we had a road we knew. His armor is on our backs now. He is in the imperial dispatches as 'lost in skirmish.'" },
            { "bp_sky_raiders",        "{=bp_dialogmanager_034}Lords don't follow us up the cliffs. The clever ones don't try. The proud ones we strip and roll back down the scree." },
            { "bp_steppe_wolves",      "{=bp_dialogmanager_035}Khuzait nobles ride out occasionally. Their horses tire before ours; they go home. We have never lost a chase on the open grass." },
            { "bp_pagan_cult",         "{=bp_dialogmanager_036}The empire's lords burn our shrines and we burn their barns. It has gone on so long no one remembers who began it. The gods will tell us when to stop." },
            { "looters",               "{=bp_dialogmanager_037}Lords? They send their levies — boys with sharpened sticks who have never seen a real fight. We don't kill them. We take their boots and let them walk home." },
            { "forest_bandits",        "{=bp_dialogmanager_038}Knights don't enter the forest. Their squires do, sometimes. We send the squires home with empty saddlebags and an education." },
            { "mountain_bandits",      "{=bp_dialogmanager_039}Lords prefer to negotiate. Most pay the toll. The ones who don't, we throw their banner-bearers off the high road as a courtesy to the next column." },
            { "steppe_bandits",        "{=bp_dialogmanager_040}We have outlasted three khans. The nobles ride in circles on the steppe; we ride straight lines. They never catch us." },
            { "sea_raiders",           "{=bp_dialogmanager_041}When a lord's ship comes after us, we burn it and salt the deck. The lord's son writes us a letter promising vengeance. We laugh. He gets older. We do not." },
            { "desert_bandits",        "{=bp_dialogmanager_042}Sultans come and go. The sand stays. Lords who hunt us run out of water before they run out of pride. We bury them in their own armor and reuse the swords." },
        };

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            // Wave 4.6.4: clear ConversationNamePatch overrides on every session launch
            // so they don't leak across save/load cycles.
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this,
                _ => BandItPlus.Patches.ConversationNamePatch.ClearAll());
            // Wave 4.11-fix v58 (2026-05-08): daily tick checks Tier-3 troop-loan
            // return date and removes the loaned bandits from the player's party
            // when the duration expires.
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTickCheckTroopLoan);
        }

        // Wave 4.5 — persistent record of hideouts the player has personally walked into.
        // The chief refuses to talk to a stranger; only after the player has entered the
        // camp at least once does the "Talk to the chief" action open dialog.
        private List<string> _visitedHideouts = new List<string>();

        // Wave 4.5 — bribe-refusal cooldown. Session-only by design: each save reload gives
        // the player a fresh chance, since the chief's mood is fluid in narrative terms.
        private readonly Dictionary<string, CampaignTime> _bribeRefusalCooldown =
            new Dictionary<string, CampaignTime>();

        // Wave 4.6 — per-culture trust progression. Tier 0 = stranger (locked services),
        // Tier 1 = tolerated (Trade/Doctor/Map unlocked), Tier 2 = trusted (everything unlocked).
        // Keyed by Settlement.Culture.StringId so trust transfers across all hideouts of the
        // same culture.
        private Dictionary<string, int> _cultureTrust = new Dictionary<string, int>();

        // Signature quests (2026-07-03): once-per-campaign record per culture.
        // 0 none / 1 active / 2 done-honored / 3 done-betrayed. Same
        // Dictionary<string,int> shape as _cultureTrust (container proven).
        private Dictionary<string, int> _signatureState = new Dictionary<string, int>();

        // Conversation-scoped signature scratch (runtime only, never saved) —
        // set by a choice line's consequence, consumed by the result line.
        private string _sigPendingResult;
        private BandItPlus.Cultures.SignatureChoice _sigPendingChoice;
        private bool _sigPendingFail;
        private BandItPlus.Cultures.SignatureChoice _sigStockChoice;
        private CharacterObject _sigNamedCaptain;   // speaker renamed via ConversationNamePatch, cleared on exit

        // Wave 5 (Chief Recruitment): tracks the in-game hour at which the player most recently
        // initiated dialog with the chief AFTER a Tier-3 trust quest completed for that culture.
        // Used by BanditAllianceBehavior.IsEligibleForOffer (Section 0 predicate). Stored as
        // CampaignTime.Now.ToHours (double) per [[bannerlord-campaigntime-tohours]]. Key absent
        // OR value < 0 means "player has not spoken to chief since T3 completion".
        private Dictionary<string, double> _lastSpokeAfterT3CompletionHours = new Dictionary<string, double>();

        // Wave 4.6 — currently-issued quest tier per culture. Missing key = no quest active.
        // Value = 1 means a Tier-1 quest is outstanding; 2 means Tier-2. Quest content is
        // looked up at read time from CultureProfile, not stored — keeps SyncData primitive.
        private Dictionary<string, int> _activeQuestTier = new Dictionary<string, int>();

        // Wave 4.7e — which variant of the tier-pool the player accepted. Mirrors
        // _activeQuestTier; missing key means variant 0 (legacy / single-variant culture).
        private Dictionary<string, int> _activeQuestVariant = new Dictionary<string, int>();

        // Wave 4.11-fix v41 (2026-05-07) — vendor-trust progression. Independent of chief
        // trust: the chief tolerating you doesn't mean the food vendor / gear vendor will
        // trade with you. Each vendor type per culture has its own 0..3 ladder, earned by
        // completing vendor-specific bring-me-X quests. Tier gates: T1 unlocks basic trade,
        // T2 unlocks mid-tier inventory, T3 unlocks rare/expensive items. Tier 0 fires the
        // gate dialog ("chief trusts you, I don't") and offers the T1 quest.
        private Dictionary<string, int> _foodVendorTrust = new Dictionary<string, int>();
        private Dictionary<string, int> _gearVendorTrust = new Dictionary<string, int>();
        // Outstanding vendor quest tier — value N means a Tier-N quest is awaiting delivery.
        // Cleared on quest completion (item exchange). Missing key = no quest accepted.
        private Dictionary<string, int> _foodVendorQuestTier = new Dictionary<string, int>();
        private Dictionary<string, int> _gearVendorQuestTier = new Dictionary<string, int>();

        // Wave 4.11-fix v58 (2026-05-08) — Tier 3 chief relationship apex state.
        // Per-culture flags for one-shot ritual options (oath, pact). These persist
        // forever once set — Tier 3 ceremonies are not repeatable.
        private Dictionary<string, bool> _chiefOathTaken = new Dictionary<string, bool>();
        private Dictionary<string, bool> _pactSworn = new Dictionary<string, bool>();
        // Single-active-loan model: only one bandit-troop loan can be out at a time
        // across all cultures. Empty culture string = no active loan.
        private string _activeTroopLoanCulture = "";
        private int _activeTroopLoanCount = 0;
        private CampaignTime _activeTroopLoanReturnDate = CampaignTime.Zero;

        // Wave 4.11-fix v59 (2026-05-08) — vendor Tier 3 one-shot flags. Keyed by
        // composite string "cultureId:vendorKind" (e.g. "forest_bandits:food",
        // "mountain_bandits:gear"). 28 possible entries (14 cultures × 2 vendor types).
        private Dictionary<string, bool> _vendorRarePurchased = new Dictionary<string, bool>();
        private Dictionary<string, bool> _vendorPactSworn = new Dictionary<string, bool>();
        // Wave 4.12-fix v60 (2026-05-11): two new vendor one-shots — T2 custom order
        // (narrative tease + small denar goodwill) and T3 contacts introduction
        // (cross-camp name-drop + denar goodwill). Same VendorFlagKey scheme.
        // _vendorCustomOrderPlaced is DEPRECATED as of v64 (real delivery loop);
        // kept here for save-load back-compat but no longer read at runtime.
        private Dictionary<string, bool> _vendorCustomOrderPlaced = new Dictionary<string, bool>();
        private Dictionary<string, bool> _vendorContactsIntroduced = new Dictionary<string, bool>();

        // Wave 4.12-fix v64 (2026-05-12): Custom Order upgraded from narrative tease
        // to real delivery mechanic. Per (cultureId, vendorKind) — player picks item
        // from Gauntlet popup, gold deducted upfront, vendor delivers after N days.
        // Three parallel dicts keyed by VendorFlagKey: itemId, quantity, due-ticks.
        private Dictionary<string, string> _pendingCustomOrderItemId = new Dictionary<string, string>();
        private Dictionary<string, int> _pendingCustomOrderQuantity = new Dictionary<string, int>();
        private Dictionary<string, double> _pendingCustomOrderDueHours = new Dictionary<string, double>();

        // Wave 4.12-fix v70 (2026-05-12): Tier-4 Contacts — recurring caravan
        // schedule. Per (cultureId, vendorKind) — when the next supply caravan
        // is due (CampaignTime hours absolute). Zero/missing = first-delivery
        // hasn't been scheduled yet; ContactCaravanBehavior auto-stagger fills it.
        private Dictionary<string, double> _nextCaravanDeliveryHours = new Dictionary<string, double>();

        // Wave 4.12-fix v71 (2026-05-12): Tier-5 Contacts foundation. Per
        // (cultureId, vendorKind), which real-world Settlement.StringId hosts
        // this contact. Stored at intro time. Foundation for the future
        // dialog-hub: future waves use this to mark notables / spawn quests
        // at the named settlement.
        private Dictionary<string, string> _contactSettlements = new Dictionary<string, string>();

        // Wave 4.12-fix v72 (2026-05-12): Tier-5 dialog hub. True once the
        // player has entered the contact's assigned settlement for the first
        // time, fired the welcome popup, and received the arrival bonus.
        private Dictionary<string, bool> _contactSettlementsMet = new Dictionary<string, bool>();

        // Wave 4.12 (2026-05-10) — slaver/prisoners vendor trust + outstanding quest tier.
        // Per-culture, unlocked when chief Tier-3 reached. Backwards compatible: old saves
        // without these keys load as empty dicts via null-guards below.
        private Dictionary<string, int> _slaverVendorTrust = new Dictionary<string, int>();
        private Dictionary<string, int> _activeSlaverQuestTier = new Dictionary<string, int>();

        // v96 (2026-05-13): Camp Memory — per-camp visit count + first-visit timestamp
        // (in CampaignTime hours since epoch). Keyed by settlement.StringId. Surfaces
        // via CampInfoPanel as "N visits · First met Day X, Year Y · Z days ago".
        private Dictionary<string, int> _campVisitCounts = new Dictionary<string, int>();
        private Dictionary<string, double> _campFirstVisitHours = new Dictionary<string, double>();

        // 2026-06-03 cinematic-shown tracking. The vanilla _campVisitCounts gate was
        // failing in user playtest — "first visit" cinematic was firing on every
        // hideout entry across many sessions even though visit count should have
        // grown past 1. Either Dictionary<string, int> isn't persisting (no
        // ConstructContainerDefinition registered) OR RecordHideoutVisit isn't
        // firing reliably. Dedicated dict uses Dictionary<string, double>, the
        // SAME type as the proven-working _campFirstVisitHours, so persistence
        // is guaranteed. Value stores the campaign-hours when the cinematic ran
        // (for debugging / future use); ContainsKey is the actual gate.
        private Dictionary<string, double> _cinematicShownHideouts = new Dictionary<string, double>();

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("_bp_visitedHideouts", ref _visitedHideouts);
            dataStore.SyncData("_bp_cultureTrust", ref _cultureTrust);
            dataStore.SyncData("_bp_signatureState", ref _signatureState);
            // Wave 5 (Chief Recruitment): tracker for Section 0 IsEligibleForOffer predicate.
            dataStore.SyncData("_bp_lastSpokeAfterT3CompletionHours", ref _lastSpokeAfterT3CompletionHours);
            dataStore.SyncData("_bp_activeQuestTier", ref _activeQuestTier);
            dataStore.SyncData("_bp_activeQuestVariant", ref _activeQuestVariant);
            dataStore.SyncData("_bp_foodVendorTrust", ref _foodVendorTrust);
            dataStore.SyncData("_bp_gearVendorTrust", ref _gearVendorTrust);
            dataStore.SyncData("_bp_foodVendorQuestTier", ref _foodVendorQuestTier);
            dataStore.SyncData("_bp_gearVendorQuestTier", ref _gearVendorQuestTier);
            // v58: Tier 3 state
            dataStore.SyncData("_bp_chiefOathTaken", ref _chiefOathTaken);
            dataStore.SyncData("_bp_pactSworn", ref _pactSworn);
            dataStore.SyncData("_bp_activeTroopLoanCulture", ref _activeTroopLoanCulture);
            dataStore.SyncData("_bp_activeTroopLoanCount", ref _activeTroopLoanCount);
            dataStore.SyncData("_bp_activeTroopLoanReturnDate", ref _activeTroopLoanReturnDate);
            // v59: vendor Tier 3 flags
            dataStore.SyncData("_bp_vendorRarePurchased", ref _vendorRarePurchased);
            dataStore.SyncData("_bp_vendorPactSworn", ref _vendorPactSworn);
            // v60: vendor T2 custom-order + T3 contacts one-shots
            dataStore.SyncData("_bp_vendorCustomOrderPlaced", ref _vendorCustomOrderPlaced);
            dataStore.SyncData("_bp_vendorContactsIntroduced", ref _vendorContactsIntroduced);
            // v64: pending Custom Order (real delivery loop)
            dataStore.SyncData("_bp_pendingCustomOrderItemId", ref _pendingCustomOrderItemId);
            dataStore.SyncData("_bp_pendingCustomOrderQuantity", ref _pendingCustomOrderQuantity);
            dataStore.SyncData("_bp_pendingCustomOrderDueHours", ref _pendingCustomOrderDueHours);
            // v96: Camp Memory persistence
            dataStore.SyncData("_bp_campVisitCounts", ref _campVisitCounts);
            dataStore.SyncData("_bp_campFirstVisitHours", ref _campFirstVisitHours);
            // 2026-06-03: cinematic-shown tracking (separate from visit count for reliability)
            dataStore.SyncData("_bp_cinematicShownHideouts", ref _cinematicShownHideouts);
            // v70: recurring contact caravan schedule
            dataStore.SyncData("_bp_nextCaravanDeliveryHours", ref _nextCaravanDeliveryHours);
            // v71: Tier-5 settlement assignment per contact
            dataStore.SyncData("_bp_contactSettlements", ref _contactSettlements);
            // v72: Tier-5 arrival-met flag per contact
            dataStore.SyncData("_bp_contactSettlementsMet", ref _contactSettlementsMet);
            // Wave 4.12: slaver vendor trust + active-quest-tier per culture.
            dataStore.SyncData("_bp_slaverTrust", ref _slaverVendorTrust);
            dataStore.SyncData("_bp_activeSlaverQuestTier", ref _activeSlaverQuestTier);
            if (_visitedHideouts == null) _visitedHideouts = new List<string>();
            if (_cultureTrust == null) _cultureTrust = new Dictionary<string, int>();
            if (_lastSpokeAfterT3CompletionHours == null) _lastSpokeAfterT3CompletionHours = new Dictionary<string, double>();
            if (_activeQuestTier == null) _activeQuestTier = new Dictionary<string, int>();
            if (_activeQuestVariant == null) _activeQuestVariant = new Dictionary<string, int>();
            if (_foodVendorTrust == null) _foodVendorTrust = new Dictionary<string, int>();
            if (_gearVendorTrust == null) _gearVendorTrust = new Dictionary<string, int>();
            if (_foodVendorQuestTier == null) _foodVendorQuestTier = new Dictionary<string, int>();
            if (_gearVendorQuestTier == null) _gearVendorQuestTier = new Dictionary<string, int>();
            if (_chiefOathTaken == null) _chiefOathTaken = new Dictionary<string, bool>();
            if (_pactSworn == null) _pactSworn = new Dictionary<string, bool>();
            if (_activeTroopLoanCulture == null) _activeTroopLoanCulture = "";
            if (_vendorRarePurchased == null) _vendorRarePurchased = new Dictionary<string, bool>();
            if (_vendorPactSworn == null) _vendorPactSworn = new Dictionary<string, bool>();
            if (_vendorCustomOrderPlaced == null) _vendorCustomOrderPlaced = new Dictionary<string, bool>();
            if (_vendorContactsIntroduced == null) _vendorContactsIntroduced = new Dictionary<string, bool>();
            if (_pendingCustomOrderItemId == null) _pendingCustomOrderItemId = new Dictionary<string, string>();
            if (_pendingCustomOrderQuantity == null) _pendingCustomOrderQuantity = new Dictionary<string, int>();
            if (_pendingCustomOrderDueHours == null) _pendingCustomOrderDueHours = new Dictionary<string, double>();
            if (_nextCaravanDeliveryHours == null) _nextCaravanDeliveryHours = new Dictionary<string, double>();
            if (_contactSettlements == null) _contactSettlements = new Dictionary<string, string>();
            if (_contactSettlementsMet == null) _contactSettlementsMet = new Dictionary<string, bool>();
            if (_slaverVendorTrust == null) _slaverVendorTrust = new Dictionary<string, int>();
            if (_activeSlaverQuestTier == null) _activeSlaverQuestTier = new Dictionary<string, int>();
        }

        private bool HasVisitedHideout(Settlement hideout)
        {
            return hideout != null && _visitedHideouts != null
                && _visitedHideouts.Contains(hideout.StringId);
        }

        // 2026-06-05 Wave 4.13: public StringId overload so the BanditCampPanel HUD can
        // wire LastVisitLine to a "first visit" vs "returning visitor" lookup. The private
        // Settlement-typed overload above stays as the internal call site for the dialog gate.
        public bool HasVisitedHideoutById(string settlementId)
        {
            return _visitedHideouts != null && !string.IsNullOrEmpty(settlementId)
                && _visitedHideouts.Contains(settlementId);
        }

        private void MarkHideoutVisited(Settlement hideout)
        {
            if (hideout == null || _visitedHideouts == null) return;
            if (!_visitedHideouts.Contains(hideout.StringId))
                _visitedHideouts.Add(hideout.StringId);
        }

        // --- Wave 4.6 trust helpers ---
        // Public so other behaviors (e.g. HideoutVendorDialog) can read trust state without
        // duplicating the dictionary lookup logic.
        public static string GetCultureIdForHideout(Settlement hideout)
        {
            return hideout?.Culture?.StringId
                ?? hideout?.OwnerClan?.Culture?.StringId
                ?? "";
        }

        public int GetCultureTrust(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return 0;
            return _cultureTrust != null && _cultureTrust.TryGetValue(cultureId, out int tier) ? tier : 0;
        }

        private int GetCultureTrustForHideout(Settlement hideout)
        {
            return GetCultureTrust(GetCultureIdForHideout(hideout));
        }

        private void SetCultureTrust(string cultureId, int tier)
        {
            if (string.IsNullOrEmpty(cultureId) || _cultureTrust == null) return;
            _cultureTrust[cultureId] = tier;
        }

        // Wave 5 / Phase 6 / Task 38 — public passthrough so BanditAllianceBehavior.BreakAlliance
        // can drop trust to Tier 0 without reflection-calling the private SetCultureTrust.
        // Force-zero (not delta-down) per spec Section 8 R4: "Trust drops to Tier 0".
        public void ResetCultureTrust(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return;
            try
            {
                if (_cultureTrust == null) _cultureTrust = new Dictionary<string, int>();
                _cultureTrust[cultureId] = 0;
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] Trust reset to Tier 0 for " + cultureId);
            }
            catch (System.Exception ex)
            {
                BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                    "[BP-Alliance] ResetCultureTrust threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Wave 5 / Phase 3 / Task 18 — public wrapper for BanditAllianceQuest.FailSoft so the
        // alliance soft-fail path can drop trust by one notch without reflection-calling the
        // private SetCultureTrust. Clamps at 0 (no negative trust); cap-at-3 is enforced by
        // the existing IncreaseCultureTrust path so positive deltas via this API are fine too.
        public void AdjustCultureTrust(string cultureId, int delta)
        {
            if (string.IsNullOrEmpty(cultureId)) return;
            if (_cultureTrust == null) _cultureTrust = new Dictionary<string, int>();
            int current;
            if (!_cultureTrust.TryGetValue(cultureId, out current)) current = 0;
            int next = System.Math.Max(0, current + delta);
            _cultureTrust[cultureId] = next;
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                "[BDM] AdjustCultureTrust culture=" + cultureId
                + " " + current + " -> " + next + " (delta=" + delta + ")");
        }

        // Wave 5 (Chief Recruitment): records that the player has initiated dialog with the
        // chief of cultureId AFTER the T3 trust quest for that culture completed. Called from
        // OnCompleteTrustQuest (the dialog session that completes T3 IS a conversation) and
        // from any future dialog branch that wants to refresh the timestamp. Section 0
        // predicate (BanditAllianceBehavior.IsEligibleForOffer) relies on this.
        public void MarkSpokeAfterT3Completion(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return;
            if (_lastSpokeAfterT3CompletionHours == null) _lastSpokeAfterT3CompletionHours = new Dictionary<string, double>();
            _lastSpokeAfterT3CompletionHours[cultureId] = CampaignTime.Now.ToHours;
        }

        // Wave 5 (Chief Recruitment): returns true iff MarkSpokeAfterT3Completion was called
        // for cultureId at some point AND the stored value is non-negative. Negative sentinel
        // -1.0 means "explicitly cleared" (future: post-alliance-break re-arm). Used by
        // BanditAllianceBehavior.IsEligibleForOffer.
        public bool HasSpokenAfterT3Completion(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return false;
            if (_lastSpokeAfterT3CompletionHours == null) return false;
            if (!_lastSpokeAfterT3CompletionHours.TryGetValue(cultureId, out double hours)) return false;
            return hours >= 0.0;
        }

        // --- Wave 4.11-fix v41: vendor-trust accessors ---
        // Public so HideoutVendorDialog can read/write trust state without duplicating dict
        // lookups. All four return 0 when culture isn't found, mirroring the chief-trust
        // pattern. Trust caps at 3 (T0 stranger / T1 tolerated / T2 trusted / T3 confidant).

        public int GetFoodVendorTrust(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return 0;
            return _foodVendorTrust != null && _foodVendorTrust.TryGetValue(cultureId, out int t) ? t : 0;
        }

        public int GetGearVendorTrust(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return 0;
            return _gearVendorTrust != null && _gearVendorTrust.TryGetValue(cultureId, out int t) ? t : 0;
        }

        // Wave 4.20.0 origin perk — DRIFTER: "fair dealing, nothing personal."
        // Camp vendors warm to the Drifter twice as fast (+2 tiers per quest).
        private static int VendorTrustStep()
        {
            try
            {
                var origins = BandItPlus.Origins.BanditOriginBehavior.Instance;
                if (origins != null && origins.Origin == BandItPlus.Origins.BanditOriginBehavior.BanditOrigin.Drifter)
                    return 2;
            }
            catch { }
            return 1;
        }

        public void IncrementFoodVendorTrust(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId) || _foodVendorTrust == null) return;
            int cur = GetFoodVendorTrust(cultureId);
            if (cur >= 3) return;
            _foodVendorTrust[cultureId] = System.Math.Min(3, cur + VendorTrustStep());
        }

        public void IncrementGearVendorTrust(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId) || _gearVendorTrust == null) return;
            int cur = GetGearVendorTrust(cultureId);
            if (cur >= 3) return;
            _gearVendorTrust[cultureId] = System.Math.Min(3, cur + VendorTrustStep());
        }

        // Vendor-quest accessors. Tier 0 = no quest accepted. Setting to N means the
        // player has accepted a Tier-N quest and owes the corresponding items on next
        // visit. Cleared on quest completion via ClearFoodVendorQuest / ClearGearVendorQuest.

        public int GetFoodVendorQuestTier(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return 0;
            return _foodVendorQuestTier != null && _foodVendorQuestTier.TryGetValue(cultureId, out int t) ? t : 0;
        }

        public int GetGearVendorQuestTier(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return 0;
            return _gearVendorQuestTier != null && _gearVendorQuestTier.TryGetValue(cultureId, out int t) ? t : 0;
        }

        public void SetFoodVendorQuest(string cultureId, int tier)
        {
            if (string.IsNullOrEmpty(cultureId) || _foodVendorQuestTier == null) return;
            _foodVendorQuestTier[cultureId] = tier;
        }

        public void SetGearVendorQuest(string cultureId, int tier)
        {
            if (string.IsNullOrEmpty(cultureId) || _gearVendorQuestTier == null) return;
            _gearVendorQuestTier[cultureId] = tier;
        }

        public void ClearFoodVendorQuest(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId) || _foodVendorQuestTier == null) return;
            _foodVendorQuestTier.Remove(cultureId);
        }

        public void ClearGearVendorQuest(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId) || _gearVendorQuestTier == null) return;
            _gearVendorQuestTier.Remove(cultureId);
        }

        // Wave 4.12 (2026-05-10) — slaver-trust accessors (parallel to vendor-trust pattern).
        // Trust caps at 3. Active quest tier dictionary tracks the currently in-flight tier;
        // value 0 / missing key = no active slaver quest for that culture.
        public int GetSlaverTrust(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId) || _slaverVendorTrust == null) return 0;
            return _slaverVendorTrust.TryGetValue(cultureId, out int v) ? v : 0;
        }

        public void IncrementSlaverTrust(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId) || _slaverVendorTrust == null) return;
            int current = GetSlaverTrust(cultureId);
            _slaverVendorTrust[cultureId] = System.Math.Min(3, current + VendorTrustStep());
        }

        public int GetActiveSlaverQuestTier(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId) || _activeSlaverQuestTier == null) return 0;
            return _activeSlaverQuestTier.TryGetValue(cultureId, out int v) ? v : 0;
        }

        public void SetActiveSlaverQuestTier(string cultureId, int tier)
        {
            if (string.IsNullOrEmpty(cultureId) || _activeSlaverQuestTier == null) return;
            if (tier <= 0) _activeSlaverQuestTier.Remove(cultureId);
            else _activeSlaverQuestTier[cultureId] = tier;
        }

        // Wave 4.11-fix v59 (2026-05-08): vendor Tier 3 flag accessors. Composite key
        // "cultureId:vendorKind" so food and gear track independently per culture.
        private static string VendorFlagKey(string cultureId, string vendorKind) => cultureId + ":" + vendorKind;

        public bool HasVendorRarePurchased(string cultureId, string vendorKind)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind)) return false;
            return _vendorRarePurchased != null
                && _vendorRarePurchased.TryGetValue(VendorFlagKey(cultureId, vendorKind), out bool v) && v;
        }

        public void SetVendorRarePurchased(string cultureId, string vendorKind)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind) || _vendorRarePurchased == null) return;
            _vendorRarePurchased[VendorFlagKey(cultureId, vendorKind)] = true;
        }

        public bool HasVendorPactSworn(string cultureId, string vendorKind)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind)) return false;
            return _vendorPactSworn != null
                && _vendorPactSworn.TryGetValue(VendorFlagKey(cultureId, vendorKind), out bool v) && v;
        }

        // v65 (2026-05-10): public accessor for the chief pact flag. Used by
        // IsAtWarAgainstFactionPatch as a per-culture override of the MCM
        // PeacefulEncounters toggle — sworn clans honor the peace even if the
        // player has the global peaceful-encounters setting turned off.
        public bool IsChiefPactSworn(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId) || _pactSworn == null) return false;
            return _pactSworn.TryGetValue(cultureId, out bool v) && v;
        }

        public void SetVendorPactSworn(string cultureId, string vendorKind)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind) || _vendorPactSworn == null) return;
            _vendorPactSworn[VendorFlagKey(cultureId, vendorKind)] = true;
        }

        // Wave 4.12-fix v60 (2026-05-11): T2 custom order + T3 contacts one-shots.
        public bool HasVendorCustomOrderPlaced(string cultureId, string vendorKind)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind)) return false;
            return _vendorCustomOrderPlaced != null
                && _vendorCustomOrderPlaced.TryGetValue(VendorFlagKey(cultureId, vendorKind), out bool v) && v;
        }

        public void SetVendorCustomOrderPlaced(string cultureId, string vendorKind)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind) || _vendorCustomOrderPlaced == null) return;
            _vendorCustomOrderPlaced[VendorFlagKey(cultureId, vendorKind)] = true;
        }

        public bool HasVendorContactsIntroduced(string cultureId, string vendorKind)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind)) return false;
            return _vendorContactsIntroduced != null
                && _vendorContactsIntroduced.TryGetValue(VendorFlagKey(cultureId, vendorKind), out bool v) && v;
        }

        public void SetVendorContactsIntroduced(string cultureId, string vendorKind)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind) || _vendorContactsIntroduced == null) return;
            _vendorContactsIntroduced[VendorFlagKey(cultureId, vendorKind)] = true;
        }

        // v69 (2026-05-12) Tier-3 Contacts settlement discount: derived check —
        // "has the player intro'd with EITHER vendor (food OR gear) of this
        // bandit culture?" Used by ContactSettlementDiscountPatch to decide
        // whether to apply -5%/+5% pricing at real-world settlements whose
        // culture maps to this bandit culture.
        public bool HasIntroducedContactInCulture(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId) || _vendorContactsIntroduced == null) return false;
            return _vendorContactsIntroduced.ContainsKey(VendorFlagKey(cultureId, "food"))
                || _vendorContactsIntroduced.ContainsKey(VendorFlagKey(cultureId, "gear"));
        }

        // v70 (2026-05-12) Tier-4 Contacts caravan schedule accessors.
        // Returns 0.0 if no delivery scheduled yet (initial state). Behavior code
        // treats 0.0 as "needs first-delivery scheduling via stagger logic."
        public double GetNextCaravanDeliveryHours(string cultureId, string vendorKind)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind)) return 0.0;
            if (_nextCaravanDeliveryHours == null) return 0.0;
            _nextCaravanDeliveryHours.TryGetValue(VendorFlagKey(cultureId, vendorKind), out var v);
            return v;
        }

        public void SetNextCaravanDeliveryHours(string cultureId, string vendorKind, double hours)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind)) return;
            if (_nextCaravanDeliveryHours == null) return;
            _nextCaravanDeliveryHours[VendorFlagKey(cultureId, vendorKind)] = hours;
        }

        // v71 (2026-05-12) Tier-5 Contacts foundation: per-(cid, kind) settlement
        // assignment. Returns null if no settlement assigned yet (intro hasn't run
        // OR ran before v71). Future waves use this to spawn / mark notables.
        public string GetContactSettlement(string cultureId, string vendorKind)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind)) return null;
            if (_contactSettlements == null) return null;
            _contactSettlements.TryGetValue(VendorFlagKey(cultureId, vendorKind), out var v);
            return v;
        }

        public void SetContactSettlement(string cultureId, string vendorKind, string settlementId)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind) || string.IsNullOrEmpty(settlementId)) return;
            if (_contactSettlements == null) return;
            _contactSettlements[VendorFlagKey(cultureId, vendorKind)] = settlementId;
        }

        // v72 (2026-05-12) Tier-5 arrival-met flag accessors.
        public bool HasContactSettlementMet(string cultureId, string vendorKind)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind)) return false;
            return _contactSettlementsMet != null
                && _contactSettlementsMet.TryGetValue(VendorFlagKey(cultureId, vendorKind), out bool v) && v;
        }

        public void SetContactSettlementMet(string cultureId, string vendorKind, bool met)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind)) return;
            if (_contactSettlementsMet == null) return;
            _contactSettlementsMet[VendorFlagKey(cultureId, vendorKind)] = met;
        }

        // (HasVendorContactsIntroduced already declared above in v60 block —
        //  ContactCaravanBehavior uses that existing method.)

        // Wave 4.12-fix v64 (2026-05-12): pending Custom Order accessors.
        // Pending order exists when the player has paid for a bespoke item that
        // hasn't been picked up yet. Same VendorFlagKey scheme as the other dicts.
        public bool HasPendingCustomOrder(string cultureId, string vendorKind)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind)) return false;
            return _pendingCustomOrderItemId != null
                && _pendingCustomOrderItemId.ContainsKey(VendorFlagKey(cultureId, vendorKind));
        }

        public void SetPendingCustomOrder(string cultureId, string vendorKind, string itemId, int qty, double dueHours)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind)) return;
            if (_pendingCustomOrderItemId == null || _pendingCustomOrderQuantity == null || _pendingCustomOrderDueHours == null) return;
            var key = VendorFlagKey(cultureId, vendorKind);
            _pendingCustomOrderItemId[key] = itemId;
            _pendingCustomOrderQuantity[key] = qty;
            _pendingCustomOrderDueHours[key] = dueHours;
        }

        public string GetPendingCustomOrderItemId(string cultureId, string vendorKind)
        {
            if (!HasPendingCustomOrder(cultureId, vendorKind)) return null;
            _pendingCustomOrderItemId.TryGetValue(VendorFlagKey(cultureId, vendorKind), out var v);
            return v;
        }

        public int GetPendingCustomOrderQuantity(string cultureId, string vendorKind)
        {
            if (!HasPendingCustomOrder(cultureId, vendorKind)) return 0;
            _pendingCustomOrderQuantity.TryGetValue(VendorFlagKey(cultureId, vendorKind), out var v);
            return v;
        }

        public double GetPendingCustomOrderDueHours(string cultureId, string vendorKind)
        {
            if (!HasPendingCustomOrder(cultureId, vendorKind)) return 0.0;
            _pendingCustomOrderDueHours.TryGetValue(VendorFlagKey(cultureId, vendorKind), out var v);
            return v;
        }

        public bool IsCustomOrderReady(string cultureId, string vendorKind)
        {
            if (!HasPendingCustomOrder(cultureId, vendorKind)) return false;
            return CampaignTime.Now.ToHours >= GetPendingCustomOrderDueHours(cultureId, vendorKind);
        }

        public void ClearPendingCustomOrder(string cultureId, string vendorKind)
        {
            if (string.IsNullOrEmpty(cultureId) || string.IsNullOrEmpty(vendorKind)) return;
            var key = VendorFlagKey(cultureId, vendorKind);
            _pendingCustomOrderItemId?.Remove(key);
            _pendingCustomOrderQuantity?.Remove(key);
            _pendingCustomOrderDueHours?.Remove(key);
        }

        // Vendor-quest item requirements per tier. Returns (itemId, count) for the given
        // vendor kind ("food" or "gear") and tier (1, 2, or 3). All item IDs are vanilla
        // Bannerlord ItemObject StringIds — verified to resolve at game load.
        // Wave 4.11-fix v44 (2026-05-08): multi-item quest specs. T1 = single item (easy
        // starter), T2 = two items (mid), T3 = three items (hard). Difficulty escalates
        // on TWO axes: quantity AND variety. Variety is the harder axis — gathering 25
        // wine + 20 dates + 15 olives forces the player to plan multi-stop trade routes
        // because no single Calradian market sells all three in bulk. A bare quantity
        // ramp ("50 wine") could be done in one shopping run; this can't.
        //
        // Approximate cost-to-completion per tier (indicative):
        //   Food T1: 20 hides                                  ≈ ~1,400 denars
        //   Food T2: 30 grain + 15 cheese                      ≈ ~1,950 denars
        //   Food T3: 25 wine + 20 date_fruit + 15 olives       ≈ ~5,500 denars
        //   Gear T1: 15 iron                                   ≈ ~450 denars
        //   Gear T2: 20 linen + 10 hardwood                    ≈ ~2,000 denars
        //   Gear T3: 25 fur + 20 hardwood + 15 iron            ≈ ~4,250 denars
        public class VendorQuestSpec
        {
            public (string itemId, int count)[] Items;
            public string Display;     // pre-formatted "X foo and Y bar and Z baz" string
        }

        public static VendorQuestSpec GetVendorQuestSpec(string vendorKind, int tier)
        {
            if (vendorKind == "food")
            {
                switch (tier)
                {
                    case 1: return new VendorQuestSpec {
                        Items = new[] { ("hides", 20) },
                        Display = Localization.Get("bp_dialogmanager_043", "20 hides")
                    };
                    case 2: return new VendorQuestSpec {
                        Items = new[] { ("grain", 30), ("cheese", 15) },
                        Display = Localization.Get("bp_dialogmanager_044", "30 sacks of grain and 15 wheels of cheese")
                    };
                    case 3: return new VendorQuestSpec {
                        Items = new[] { ("wine", 25), ("date_fruit", 20), ("olives", 15) },
                        Display = Localization.Get("bp_dialogmanager_045", "25 jugs of wine, 20 dates, and 15 olives")
                    };
                }
            }
            else if (vendorKind == "gear")
            {
                switch (tier)
                {
                    case 1: return new VendorQuestSpec {
                        Items = new[] { ("iron", 15) },
                        Display = Localization.Get("bp_dialogmanager_046", "15 pieces of iron")
                    };
                    case 2: return new VendorQuestSpec {
                        Items = new[] { ("linen", 20), ("hardwood", 10) },
                        Display = Localization.Get("bp_dialogmanager_047", "20 bolts of linen and 10 lengths of hardwood")
                    };
                    case 3: return new VendorQuestSpec {
                        Items = new[] { ("fur", 25), ("hardwood", 20), ("iron", 15) },
                        Display = Localization.Get("bp_dialogmanager_048", "25 furs, 20 lengths of hardwood, and 15 pieces of iron")
                    };
                }
            }
            return null;
        }

        private bool HasActiveQuest(string cultureId)
        {
            return !string.IsNullOrEmpty(cultureId)
                && _activeQuestTier != null
                && _activeQuestTier.ContainsKey(cultureId);
        }

        private int GetActiveQuestTier(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return 0;
            return _activeQuestTier != null && _activeQuestTier.TryGetValue(cultureId, out int t) ? t : 0;
        }

        // v94.2 (2026-05-13): public wrapper so the CampInfoPanel UI can surface
        // chief trust quests (previously only vendor quests were tracked in the panel,
        // which made the QUESTS section appear empty even when a chief task was active).
        public int GetActiveChiefQuestTier(string cultureId) => GetActiveQuestTier(cultureId);

        // v95 (2026-05-13): public wrapper for active chief quest details — enables
        // the "Quest Stakes" UI surfacing (item names, counts, gold amounts).
        public TrustQuest GetActiveChiefQuestData(string cultureId) => GetActiveTrustQuest(cultureId);

        // v96 (2026-05-13): Camp Memory accessors. Settlement keyed (settlement.StringId).
        // RecordHideoutVisit fires once per scene-init; the panel reads counts+first-visit
        // to surface "5th visit · First met Day 89, Year 1083 · 243 days ago".
        public void RecordHideoutVisit(string settlementId)
        {
            if (string.IsNullOrEmpty(settlementId)) return;
            try
            {
                if (_campVisitCounts == null) _campVisitCounts = new Dictionary<string, int>();
                if (_campFirstVisitHours == null) _campFirstVisitHours = new Dictionary<string, double>();
                _campVisitCounts.TryGetValue(settlementId, out int cur);
                _campVisitCounts[settlementId] = cur + 1;
                if (!_campFirstVisitHours.ContainsKey(settlementId))
                    _campFirstVisitHours[settlementId] = CampaignTime.Now.ToHours;
            }
            catch (Exception ex) {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("RecordHideoutVisit fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
        public int GetCampVisitCount(string settlementId)
        {
            if (string.IsNullOrEmpty(settlementId) || _campVisitCounts == null) return 0;
            return _campVisitCounts.TryGetValue(settlementId, out int c) ? c : 0;
        }

        // 2026-06-03: cinematic-shown getter. Returns TRUE if the first-visit
        // cinematic has ever been shown for this hideout (across all sessions).
        // Used by HideoutVisitNpcSpawnBehavior to gate the cinematic injection
        // so it only fires once per hideout.
        public bool HasShownCinematicForHideout(string settlementId)
        {
            if (string.IsNullOrEmpty(settlementId) || _cinematicShownHideouts == null) return false;
            return _cinematicShownHideouts.ContainsKey(settlementId);
        }

        // 2026-06-03: cinematic-shown setter. Records that the cinematic has
        // been shown for this hideout at the current campaign time. Idempotent
        // (re-recording is fine; ContainsKey gate stays true).
        public void MarkCinematicShown(string settlementId)
        {
            if (string.IsNullOrEmpty(settlementId)) return;
            try
            {
                if (_cinematicShownHideouts == null)
                    _cinematicShownHideouts = new Dictionary<string, double>();
                _cinematicShownHideouts[settlementId] = CampaignTime.Now.ToHours;
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "MarkCinematicShown fail for '" + settlementId + "': "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }
        public double GetCampFirstVisitHours(string settlementId)
        {
            if (string.IsNullOrEmpty(settlementId) || _campFirstVisitHours == null) return 0;
            return _campFirstVisitHours.TryGetValue(settlementId, out double h) ? h : 0;
        }

        private void SetActiveQuest(string cultureId, int tier)
        {
            if (string.IsNullOrEmpty(cultureId) || _activeQuestTier == null) return;
            _activeQuestTier[cultureId] = tier;
        }

        // Wave 4.7e — overload that also pins the random variant chosen for this offer.
        private void SetActiveQuest(string cultureId, int tier, int variantIdx)
        {
            if (string.IsNullOrEmpty(cultureId) || _activeQuestTier == null) return;
            _activeQuestTier[cultureId] = tier;
            if (_activeQuestVariant == null) _activeQuestVariant = new Dictionary<string, int>();
            _activeQuestVariant[cultureId] = variantIdx;
        }

        private void ClearActiveQuest(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId) || _activeQuestTier == null) return;
            _activeQuestTier.Remove(cultureId);
            _activeQuestVariant?.Remove(cultureId);
        }

        // Wave 4.7e — returns the variant the player accepted (or 0 for unknown / legacy).
        private int GetActiveQuestVariant(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId) || _activeQuestVariant == null) return 0;
            return _activeQuestVariant.TryGetValue(cultureId, out int v) ? v : 0;
        }

        // Wave 4.7e — deterministic daily seed for the OFFERED variant before acceptance.
        // Same culture+tier returns the same variant for the entire in-game day, so dialog
        // text matches when the player walks back. Resets the next day, giving the chief a
        // different errand. Falls back to 0 if pool is null/empty/single-variant.
        public static int PickOfferedVariantIdx(string cultureId, int tier, int poolLen)
        {
            if (poolLen <= 1) return 0;
            try
            {
                long day = (long)CampaignTime.Now.ToDays;
                int hash = (cultureId?.GetHashCode() ?? 0) ^ (tier * 1009) ^ (int)(day & 0x7fffffff);
                int idx = Math.Abs(hash) % poolLen;
                return idx;
            }
            catch { return 0; }
        }

        private TrustQuest GetActiveTrustQuest(string cultureId)
        {
            if (!HasActiveQuest(cultureId)) return null;
            int tier = GetActiveQuestTier(cultureId);
            if (!CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out var profile) || profile == null)
                return null;
            // Wave 4.7e: prefer Pool[variant] for randomized cultures; fall back to legacy singleton.
            TrustQuest[] pool = tier == 1 ? profile.TrustQuestTier1Pool
                              : tier == 2 ? profile.TrustQuestTier2Pool
                              : tier == 3 ? profile.TrustQuestTier3Pool
                              : null;
            if (pool != null && pool.Length > 0)
            {
                int idx = GetActiveQuestVariant(cultureId);
                if (idx < 0 || idx >= pool.Length) idx = 0;
                return pool[idx];
            }
            return tier == 1 ? profile.TrustQuestTier1
                 : tier == 2 ? profile.TrustQuestTier2
                 : null;
        }

        // Wave 4.7e — quest the chief is currently OFFERING (player hasn't accepted yet).
        // Uses daily-seeded variant so dialog text is consistent within a session.
        private TrustQuest GetOfferedTrustQuest(string cultureId, int tier)
        {
            if (!CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out var profile) || profile == null)
                return null;
            TrustQuest[] pool = tier == 1 ? profile.TrustQuestTier1Pool
                              : tier == 2 ? profile.TrustQuestTier2Pool
                              : tier == 3 ? profile.TrustQuestTier3Pool
                              : null;
            if (pool != null && pool.Length > 0)
            {
                int idx = PickOfferedVariantIdx(cultureId, tier, pool.Length);
                return pool[idx];
            }
            return tier == 1 ? profile.TrustQuestTier1
                 : tier == 2 ? profile.TrustQuestTier2
                 : null;
        }

        private bool IsActiveQuestComplete(string cultureId)
        {
            var quest = GetActiveTrustQuest(cultureId);
            if (quest == null) return false;
            if (quest.IsGoldQuest)
                return Hero.MainHero != null && Hero.MainHero.Gold >= quest.GoldAmount;
            if (quest.ItemIds == null || quest.Counts == null
                || quest.ItemIds.Length != quest.Counts.Length
                || quest.ItemIds.Length == 0) return false;
            var roster = MobileParty.MainParty?.ItemRoster;
            if (roster == null) return false;
            for (int i = 0; i < quest.ItemIds.Length; i++)
            {
                var item = MBObjectManager.Instance.GetObject<ItemObject>(quest.ItemIds[i]);
                if (item == null) return false;
                if (roster.GetItemNumber(item) < quest.Counts[i]) return false;
            }
            return true;
        }

        // Returns true on success. Re-validates before mutating to avoid partial deduction
        // if the player dropped items between dialog open and selecting "I have your items".
        private bool DeductActiveQuestPayment(string cultureId)
        {
            var quest = GetActiveTrustQuest(cultureId);
            if (quest == null) return false;
            if (quest.IsGoldQuest)
            {
                if (Hero.MainHero == null || Hero.MainHero.Gold < quest.GoldAmount) return false;
                GiveGoldAction.ApplyBetweenCharacters(Hero.MainHero, null, quest.GoldAmount, true);
                return true;
            }
            if (quest.ItemIds == null || quest.Counts == null
                || quest.ItemIds.Length != quest.Counts.Length) return false;
            var roster = MobileParty.MainParty?.ItemRoster;
            if (roster == null) return false;
            for (int i = 0; i < quest.ItemIds.Length; i++)
            {
                var item = MBObjectManager.Instance.GetObject<ItemObject>(quest.ItemIds[i]);
                if (item == null || roster.GetItemNumber(item) < quest.Counts[i]) return false;
            }
            for (int i = 0; i < quest.ItemIds.Length; i++)
            {
                var item = MBObjectManager.Instance.GetObject<ItemObject>(quest.ItemIds[i]);
                roster.AddToCounts(item, -quest.Counts[i]);
            }
            return true;
        }

        // Sets args.IsEnabled = false with tooltip when player's culture trust is below
        // the required tier. Caller appends `return true;` so the option still renders
        // (greyed out, motivational).
        private void ApplyTrustGate(MenuCallbackArgs args, int requiredTier)
        {
            // Wave 4.20.0 origin perk — OUTLAW BLOOD: "the fires know you."
            // Tier-1 privileges (trade, basic camp access) are open from day one;
            // deeper trust (tier 2+) must still be earned like everyone else.
            try
            {
                var origins = BandItPlus.Origins.BanditOriginBehavior.Instance;
                if (requiredTier <= 1 && origins != null
                    && origins.Origin == BandItPlus.Origins.BanditOriginBehavior.BanditOrigin.OutlawBlood)
                    return;
            }
            catch { }

            var hideout = GetCurrentHideout();
            int trust = GetCultureTrustForHideout(hideout);
            if (trust >= requiredTier) return;
            args.IsEnabled = false;
            string msg = requiredTier == 1
                ? "{=bp_dialogmanager_049}They don't trade with strangers. Speak to the chief and earn their trust first."
                : "{=bp_dialogmanager_050}Only trusted comrades have run of the camp. Earn the chief's deeper trust first.";
            args.Tooltip = new TextObject(msg);
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            try
            {
                starter.AddDialogLine("bp_encounter_start", "start", "bp_encounter_root",
                    "{=bp_dialog_hail}Halt. State your business.",
                    new ConversationSentence.OnConditionDelegate(IsBpEncounter), null);

                // v220 Patch 2 — Notable-context bridge. When the player meets a chief
                // Hero OUTSIDE the hideout encounter (e.g. chief is anchored to a town's
                // clan per BanditChiefRegistry and the engine spawns him as a notable in
                // that town's interior scene), route the conversation into the same
                // bp_encounter_root state so the full alliance dialog tree (T29-T32
                // branches) is available there too. The condition is mutually exclusive
                // with IsBpEncounter so this bridge only fires in non-encounter contexts.
                starter.AddDialogLine("bp_chief_notable_start", "start", "bp_encounter_root",
                    "{=bp_dialog_chief_notable_hail}You catch my eye, traveler. Speak.",
                    new ConversationSentence.OnConditionDelegate(IsBpChiefAnywhere), null);

                // Signature quests (2026-07-03): herald/offer/confront/return lines.
                RegisterSignatureDialogs(starter);

                // Tier 1 (tolerated): small talk, surface-level clan info, vanilla trade.
                // 2026-06-03 menu cleanup: "Tell me about your clan..." is now a
                // SUBMENU OPENER that consolidates the old lore/history/religion
                // player-lines under one heading. Reduces root-level menu density.
                starter.AddPlayerLine("bp_player_lore_open", "bp_encounter_root", "bp_encounter_lore_menu",
                    "{=bp_dialogmanager_051}Tell me about your clan... <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(IsChiefTier1), null);
                starter.AddPlayerLine("bp_player_hideout", "bp_encounter_root", "bp_encounter_hideout",
                    "{=bp_dialogmanager_052}Where is your hideout? <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(IsChiefTier1), null);

                // 2026-06-03 LORE submenu: intermediate chief line + sub-options.
                // Player picks "Tell me about your clan..." → chief acknowledges with
                // a single line → player gets a smaller picker of clan-related subtopics.
                starter.AddDialogLine("bp_chief_lore_menu", "bp_encounter_lore_menu", "bp_encounter_lore_choices",
                    "{=bp_dialogmanager_053}Aye. What would you have me speak of?",
                    null, null);

                // 2026-06-03 [Back] bug fix: relay chief line. When player picks [Back]
                // from any submenu, output goes to bp_encounter_back_relay (NOT directly
                // to bp_encounter_root). This relay fires THIS chief line, then outputs
                // to bp_encounter_root where the root player picker re-shows correctly.
                // Without this relay, the engine had no chief line to fire at root and
                // showed only one stray player option.
                starter.AddDialogLine("bp_chief_back_relay", "bp_encounter_back_relay", "bp_encounter_root",
                    "{=bp_dialogmanager_054}Mm. Something else, then?",
                    null, null);
                starter.AddPlayerLine("bp_player_lore", "bp_encounter_lore_choices", "bp_encounter_lore",
                    "{=bp_dialogmanager_055}Your clan's beginnings. <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    null, null);
                starter.AddPlayerLine("bp_player_history", "bp_encounter_lore_choices", "bp_encounter_history",
                    "{=bp_dialogmanager_056}Your own story. <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    null, null);
                starter.AddPlayerLine("bp_player_religion", "bp_encounter_lore_choices", "bp_encounter_religion",
                    "{=bp_dialogmanager_057}What gods you worship. <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(IsChiefTier2), null);
                starter.AddPlayerLine("bp_player_lore_back", "bp_encounter_lore_choices", "bp_encounter_back_relay",
                    "{=bp_dialogmanager_058}[Back]",
                    null, null);
                // 2026-06-03 [Back] bug fix: back-buttons now route through a relay token
                // (bp_encounter_back_relay) so the engine has a chief line to fire before
                // the root player picker re-shows. Direct output=bp_encounter_root left the
                // engine in a broken state (only one stray option visible).
                // 2026-06-03: bp_player_pleasantry REMOVED per user request — pure
                // chit-chat with no game effect. Menu was 17 items deep; this is the
                // first of 3 deletions to drop it to a manageable size.
                // Wave 4.11-fix v56 (2026-05-08): bp_player_trade REMOVED — was redundant
                // with vanilla "I'd like to do business with you" line, AND chiefs don't
                // trade in our walkable-hideout system (vendors do). Players who want
                // commerce go to the food/gear vendors via blue "!" markers. Cleaner menu.
                // === Tier 2 (trusted) — Wave 4.11-fix v57 (2026-05-08) rebuild ===
                // Cut: "Why don't you join a kingdom?" — tonally dead, bandit answer is
                // always the same shape ("the kingdom betrayed me"). Repurposed: lords
                // question now asks for actionable patrol-gap intel using live nearby
                // hero data instead of generic flavor. Kept: gods/worship as atmosphere.
                // Added: clan-rivalry intel, caravan whisper, and a Tier-3 pact tease.
                // 2026-06-03 INTEL submenu opener at root. Consolidates lords /
                // clan_intel / caravan_intel / rumor under one heading. Reduces
                // root-level menu density while preserving all existing intel-gathering
                // options (each now a sub-pick after the player asks "Have your scouts
                // seen anything?").
                starter.AddPlayerLine("bp_player_intel_open", "bp_encounter_root", "bp_encounter_intel_menu",
                    "{=bp_dialogmanager_059}Have your scouts seen anything? <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(IsChiefTier2), null);
                // 2026-06-03: bp_player_religion MOVED into the LORE submenu above.

                // INTEL submenu intermediate (chief line) + sub-options.
                starter.AddDialogLine("bp_chief_intel_menu", "bp_encounter_intel_menu", "bp_encounter_intel_choices",
                    "{=bp_dialogmanager_060}Aye, my watchers keep ears on a few things. What kind?",
                    null, null);
                starter.AddPlayerLine("bp_player_lords", "bp_encounter_intel_choices", "bp_encounter_lords",
                    "{=bp_dialogmanager_061}Lords who aren't watching their roads. <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    null, null);
                starter.AddPlayerLine("bp_player_clan_intel", "bp_encounter_intel_choices", "bp_encounter_clan_intel",
                    "{=bp_dialogmanager_062}Your score with the other bandit clans around here. <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    null, null);
                starter.AddPlayerLine("bp_player_caravan_intel", "bp_encounter_intel_choices", "bp_encounter_caravan_intel",
                    "{=bp_dialogmanager_063}Caravans worth taking. <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    null, null);
                starter.AddPlayerLine("bp_player_intel_back", "bp_encounter_intel_choices", "bp_encounter_back_relay",
                    "{=bp_dialogmanager_064}[Back]",
                    null, null);
                // 2026-06-03 [Back] bug fix: back-buttons now route through a relay token
                // (bp_encounter_back_relay) so the engine has a chief line to fire before
                // the root player picker re-shows. Direct output=bp_encounter_root left the
                // engine in a broken state (only one stray option visible).
                starter.AddPlayerLine("bp_player_pact_tease", "bp_encounter_root", "bp_encounter_pact_tease",
                    "{=bp_dialogmanager_065}Will you bleed for me when the time comes? <span style=\"Conversation.Persuasion.Neutral\">(Story)</span>",
                    new ConversationSentence.OnConditionDelegate(IsChiefTier2), null);

                // === Tier 3 (kin-by-oath) — Wave 4.11-fix v58 (2026-05-08) ===
                // Relationship-apex options. Three are pure flag-set ceremony (oath,
                // pact-cash, burdens monologue); one is a real bandit-troop loan with
                // bounded duration (DailyTickEvent removes the troops on return).
                // 2026-06-03 menu-cleanup: Tier-3 oath_brother + pact_answer REMOVED from
                // root menu — they live exclusively under bp_encounter_pact_tease_branch
                // now (nested follow-ups after the chief's pact-tease reply). Previously
                // these appeared in both places, which read as duplicates. The branch
                // mirrors below (bp_player_oath_brother_nested / bp_player_pact_answer_nested)
                // are the sole entry points.
                // 2026-06-03: bp_player_burdens REMOVED per user request — flavor only,
                // no game effect. 2nd of 3 menu-cleanup deletions.
                // 2026-06-03 menu-cleanup: troop_loan also moved from root to the
                // pact-tease branch so all three Tier-3 ceremony lines live in one
                // narrative tree. See bp_player_troop_loan_nested below.

                // Wave 4.11-fix v61 (2026-05-08): chief Tier 3 named-target hunt option.
                // Visible only at Tier 3 AND when no active hunt exists for THIS culture.
                // Spawns a BanditNamedTargetQuest with a randomly-picked nearby noble as
                // target. Quest completes when target dies (player kill OR any other cause).
                starter.AddPlayerLine("bp_player_namedtarget", "bp_encounter_root", "bp_encounter_namedtarget",
                    "{=bp_dialogmanager_066}Where does the man you'd see dead ride? I'll find him for you. <span style=\"Conversation.Persuasion.Negative\">(Challange)</span>",
                    new ConversationSentence.OnConditionDelegate(CanRequestNamedTargetHunt), null);

                // 2026-06-03 story-expansion: two-phase named-target collection. After
                // killing the target, the player returns to the chief and picks this line
                // to formally close the contract. Condition gates visibility to the chief
                // whose hunt is in the awaiting-collection state.
                starter.AddPlayerLine("bp_player_collect_named_target", "bp_encounter_root", "bp_encounter_collect_named_target",
                    "{=bp_dialogmanager_067}I've ended the man you named. <span style=\"Conversation.Persuasion.Positive\">(Easy)</span>",
                    new ConversationSentence.OnConditionDelegate(CanCollectNamedTargetBounty), null);

                // 2026-06-03 story-expansion: Tier-3 raid-village quest. Distinct from
                // the kill-noble named-target. Spawns BanditRaidVillageQuest with a random
                // village owned by a lord enemy to the chief.
                starter.AddPlayerLine("bp_player_raidvillage", "bp_encounter_root", "bp_encounter_raidvillage",
                    "{=bp_dialogmanager_068}Whose village burns next? <span style=\"Conversation.Persuasion.Negative\">(Hard)</span>",
                    new ConversationSentence.OnConditionDelegate(CanRequestRaidVillage), null);
                starter.AddPlayerLine("bp_player_collect_raid_village", "bp_encounter_root", "bp_encounter_collect_raid_village",
                    "{=bp_dialogmanager_069}The village burns. Pay the debt. <span style=\"Conversation.Persuasion.Positive\">(Easy)</span>",
                    new ConversationSentence.OnConditionDelegate(CanCollectRaidVillageBounty), null);

                // === Wave 5 (T29) alliance offer prompt ===========================
                // Eligibility = BanditAllianceBehavior.IsEligibleForOffer (Section 0
                // predicate: cultureTrust >= 2 + Tier-3 BanditTrustQuest completed +
                // no active BanditAllianceQuest + MCM toggle on + spoken-since-T3 flag
                // set + soft-fail cooldown elapsed + no kingdom-war block).
                starter.AddPlayerLine(
                    "bp_alliance_offer_player",
                    "bp_encounter_root",
                    "bp_alliance_offer_chief",
                    "{=bp_dialogmanager_070}Speak of the alliance you mentioned. <span style=\"Conversation.Persuasion.Negative\">(Extremly Hard)</span>",
                    new ConversationSentence.OnConditionDelegate(IsAllianceOfferEligible),
                    null);

                starter.AddDialogLine(
                    "bp_alliance_offer_chief_line",
                    "bp_alliance_offer_chief",
                    "bp_alliance_offer_choice",
                    "{=bp_dialogmanager_071}You've stood beside us long enough. There is a thing we ask of those who would be more than guests.",
                    null, null);

                starter.AddPlayerLine(
                    "bp_alliance_ch1_accept",
                    "bp_alliance_offer_choice",
                    "bp_alliance_ch1_accept_chief",
                    "{=bp_dialogmanager_072}I am ready. What must I do?",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(OnAcceptAllianceOffer));

                starter.AddPlayerLine(
                    "bp_alliance_ch1_decline",
                    "bp_alliance_offer_choice",
                    "bp_alliance_ch1_decline_chief",
                    "{=bp_dialogmanager_073}Not yet.",
                    null, null);

                starter.AddDialogLine(
                    "bp_alliance_ch1_decline_chief_line",
                    "bp_alliance_ch1_decline_chief",
                    "bp_encounter_root",
                    "{=bp_dialogmanager_074}Then return when your stomach is harder.",
                    null, null);

                // === Wave 5 (T30) alliance Ch1 accept response (soft-fail telegraph) ===
                // Fires immediately after the T29 accept choice. Reads the per-culture
                // Ch1 task brief from CultureProfileRegistry so each chief describes the
                // task in their own voice, then appends the spec §2 soft-fail telegraph
                // line ("a failure is no shame — return in a moon's turn"). Closes the
                // conversation (close_window) — player will pursue the Ch1 objective on
                // the campaign map.
                starter.AddDialogLine(
                    "bp_alliance_ch1_accept_chief_line",
                    "bp_alliance_ch1_accept_chief",
                    "close_window",
                    "{=!}{BP_ALLIANCE_CH1_BRIEF} A failure in this errand is no shame — return in a moon's turn and try again.",
                    new ConversationSentence.OnConditionDelegate(SetAllianceCh1BriefVariable),
                    null);

                // === Wave 5 (T30) alliance Ch2 ready-for-trial prompt ===================
                // Visible only when the chief's active alliance quest is in Ch2_PreTrial
                // (chapter 2, not yet in-trial — Section 4 state machine). Mutually
                // exclusive with the offer branch (IsEligibleForOffer returns false once
                // a quest exists) and with any post-alliance branch (IsAllied false until
                // Ch3 completes). The chief response IS the hard-fail telegraph per spec §2.
                starter.AddPlayerLine(
                    "bp_alliance_ch2_trial_player",
                    "bp_encounter_root",
                    "bp_alliance_ch2_trial_chief",
                    "{=bp_dialogmanager_075}I'm ready for the trial. Ride out with me.",
                    new ConversationSentence.OnConditionDelegate(IsAllianceCh2PreTrial),
                    null);

                starter.AddDialogLine(
                    "bp_alliance_ch2_trial_chief_line",
                    "bp_alliance_ch2_trial_chief",
                    "bp_alliance_ch2_choice",
                    "{=bp_dialogmanager_076}Once we ride out together, there is no second chance. If you fall or flee, my door closes to you forever.",
                    null, null);

                // === Wave 5 (T31) alliance Ch2 accept / decline branches ==============
                // Replaces the T30 placeholder. Accept drives the quest into Ch2_InTrial
                // via BanditAllianceQuest.BeginTrial() — that public wrapper delegates to
                // the existing private AdvanceToCh2InTrial transition (chief into
                // MainParty.MemberRoster + target party spawn near hideout + journal
                // log). Decline returns to bp_encounter_root so the player can retry
                // later (quest stays in Ch2_PreTrial). Per spec §4 Ch2.
                starter.AddPlayerLine(
                    "bp_alliance_ch2_accept",
                    "bp_alliance_ch2_choice",
                    "bp_alliance_ch2_accept_chief",
                    "{=bp_dialogmanager_077}Then there will be no falling, and no fleeing. Let's ride.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(OnAcceptCh2Trial));

                starter.AddPlayerLine(
                    "bp_alliance_ch2_decline",
                    "bp_alliance_ch2_choice",
                    "bp_alliance_ch2_decline_chief",
                    "{=bp_dialogmanager_078}Not today. I'll return when I am sure.",
                    null, null);

                starter.AddDialogLine(
                    "bp_alliance_ch2_accept_chief_line",
                    "bp_alliance_ch2_accept_chief",
                    "close_window",
                    "{=bp_dialogmanager_079}Then it's done. I'll bring my axe. Lead, and I follow.",
                    null, null);

                starter.AddDialogLine(
                    "bp_alliance_ch2_decline_chief_line",
                    "bp_alliance_ch2_decline_chief",
                    "bp_encounter_root",
                    "{=bp_dialogmanager_080}Then return with steel sharper than your tongue.",
                    null, null);

                // === Wave 5 (T31) post-alliance "How fares the alliance?" line =========
                // Deliberately ONE confirmation line per spec §2 — deeper personal-
                // conversations tree is queued for sub-feature A. Visible only when the
                // chief's culture has an active alliance AND no in-trial quest is
                // outstanding (chief would be in the player's party then, not in camp).
                // Mutually exclusive with the offer prompt (which requires !IsAllied) and
                // the Ch2 prompts (which require an active quest). Per memory
                // [bannerlord-dialog-priority-registration-order] each "start" gate is
                // mutually exclusive so registration order is not load-bearing.
                starter.AddPlayerLine(
                    "bp_alliance_post_chat_player",
                    "bp_encounter_root",
                    "bp_alliance_post_chat_chief",
                    "{=bp_dialogmanager_081}How fares the alliance, brother?",
                    new ConversationSentence.OnConditionDelegate(IsAllied_ForChat),
                    null);

                starter.AddDialogLine(
                    "bp_alliance_post_chat_chief_line",
                    "bp_alliance_post_chat_chief",
                    "bp_encounter_root",
                    "{=!}We've stood {BP_ALLIANCE_AGE} sworn, you and I. The road is the road. Speak, and my boys are at your back.",
                    new ConversationSentence.OnConditionDelegate(SetAllianceAgeVariable),
                    null);

                // === Wave 5 / Phase 5 / T32 — alliance Summon Warband (ready path) ===
                // Visible when player is allied to THIS chief AND the 7-day cooldown is ready.
                // Consequence calls BanditAllianceBehavior.TrySummonWarband which commits the
                // cooldown stamp (per Phase 2 contract). Full party-spawn lifecycle is Phase 6
                // T36/T37 — the chief just confirms verbally for now.
                // Mutually exclusive with the deny line below (one returns true iff the other
                // returns false). Per memory note bannerlord-dialog-priority-registration-order.
                starter.AddPlayerLine(
                    "bp_alliance_summon_player",
                    "bp_encounter_root",
                    "bp_alliance_summon_chief",
                    "{=bp_dialogmanager_082}Rally your warband. I have need of your axes today.",
                    new ConversationSentence.OnConditionDelegate(IsAllied_CanSummon),
                    new ConversationSentence.OnConsequenceDelegate(OnSummonWarbandAccept));

                starter.AddDialogLine(
                    "bp_alliance_summon_chief_line",
                    "bp_alliance_summon_chief",
                    "close_window",
                    "{=!}{BP_SUMMON_RESULT}",
                    null, null);

                // === Wave 5 / Phase 5 / T32 — alliance Summon Warband (deny path) ===
                // Visible when allied AND cooldown NOT ready. Pure feedback — no consequence,
                // returns to bp_encounter_root. The condition composes the human-readable
                // denial text into BP_SUMMON_DENY.
                starter.AddPlayerLine(
                    "bp_alliance_summon_deny_player",
                    "bp_encounter_root",
                    "bp_alliance_summon_deny_chief",
                    "{=bp_dialogmanager_083}Rally your warband. I have need of your axes today.",
                    new ConversationSentence.OnConditionDelegate(IsAllied_SummonOnCooldown),
                    null);

                starter.AddDialogLine(
                    "bp_alliance_summon_deny_chief_line",
                    "bp_alliance_summon_deny_chief",
                    "bp_encounter_root",
                    "{=!}{BP_SUMMON_DENY}",
                    null, null);

                // Wave 4.11-fix v56 (2026-05-08): threat + bribe now CAMPAIGN-MAP ONLY.
                // Hidden in walkable-hideout chief tree at all tiers (tonally wrong while
                // sitting in the chief's own camp as a guest). Still visible on the
                // campaign-map encounter (where you're meeting a bandit party in the
                // field — threats/bribes belong there).
                starter.AddPlayerLine("bp_player_threat", "bp_encounter_root", "bp_encounter_threat",
                    "{=bp_dialogmanager_084}Stand aside or I'll cut you down. <span style=\"Conversation.Persuasion.Negative\">(Hard)</span>",
                    new ConversationSentence.OnConditionDelegate(CanThreatenChiefOnMap), null);
                starter.AddPlayerLine("bp_player_bribe", "bp_encounter_root", "bp_encounter_bribe",
                    "{=bp_dialogmanager_085}I have gold for safe passage. ({BRIBE_AMOUNT} denars) <span style=\"Conversation.Persuasion.Positive\">(Trade)</span>",
                    new ConversationSentence.OnConditionDelegate(IsBribeAvailableOnMap), null);

                // === Wave 4.11-fix v56 (2026-05-08): new Tier 1+ in-camp options ===
                // Drink with chief (relationship-building, +1 chief relation), scout
                // rumor (chief shares a road tip from his men), and a graceful sever
                // option that replaces the tonally-wrong threat for in-camp use.
                // 2026-06-03: bp_player_drink REMOVED per user request — flavor only,
                // no game effect. 3rd of 3 menu-cleanup deletions (now 14 items vs 17).
                // 2026-06-03: bp_player_rumor REMOVED from root — the new INTEL submenu
                // opener ("Have your scouts seen anything?") covers the same conversational
                // ground with structured sub-options (lords / clan_intel / caravan_intel)
                // that have specific game effects. The old generic-rumor chief response
                // (bp_encounter_rumor) is preserved in source but no longer reachable.
                // 2026-06-03 menu-clarity rename: sever and farewell sounded
                // identical to the player ("done here" vs "changed my mind") but
                // have DIFFERENT game effects — sever applies a -1 relation hit
                // and burns trust (alliance-cooling exit), farewell is a casual
                // close. Bracketed UI-style on sever signals special weight.
                starter.AddPlayerLine("bp_player_sever", "bp_encounter_root", "bp_encounter_sever",
                    "{=bp_dialogmanager_086}[Walk away from this alliance.]",
                    new ConversationSentence.OnConditionDelegate(IsCanSeverInCamp), null);

                starter.AddPlayerLine("bp_player_farewell", "bp_encounter_root", "bp_encounter_farewell",
                    "{=bp_dialogmanager_087}Farewell for now.", null, null);

                starter.AddDialogLine("bp_lore_response", "bp_encounter_lore", "bp_encounter_root",
                    "{=!}{LORE_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetLoreVariable), null);

                starter.AddDialogLine("bp_history_response", "bp_encounter_history", "bp_encounter_root",
                    "{=!}{HISTORY_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetHistoryVariable), null);

                starter.AddDialogLine("bp_hideout_response", "bp_encounter_hideout", "bp_encounter_root",
                    "{=!}{HIDEOUT_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetHideoutVariable), null);

                // v57: lords response now uses live-lookup of nearby noble heroes
                // instead of static lore — gives the player an actionable name they
                // can hunt or avoid.
                starter.AddDialogLine("bp_lords_response", "bp_encounter_lords", "bp_encounter_root",
                    "{=!}{LORDS_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetLordsRoadsVariable), null);

                // v57: bp_kingdom_response REMOVED (paired with bp_player_kingdom cut).

                starter.AddDialogLine("bp_religion_response", "bp_encounter_religion", "bp_encounter_root",
                    "{=!}{RELIGION_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetReligionVariable), null);

                // v57 new responses for the three operational additions.
                starter.AddDialogLine("bp_clan_intel_response", "bp_encounter_clan_intel", "bp_encounter_root",
                    "{=!}{CLAN_INTEL_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetClanIntelVariable), null);

                starter.AddDialogLine("bp_caravan_intel_response", "bp_encounter_caravan_intel", "bp_encounter_root",
                    "{=!}{CARAVAN_INTEL_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetCaravanIntelVariable), null);

                // v57: pact tease is purely narrative — no faction-state change. The
                // line implies a future deeper bond without committing the codebase to
                // implementing it. Tier 3 mechanics (real pact, named-target hits,
                // multi-clan coalitions) are deferred to a multi-session iteration.
                // 2026-06-03 story-expansion: per-chief reply via {PACT_TEASE_BLURB}
                // placeholder set by SetPactTeaseVariable. Output is bp_encounter_pact_tease_branch
                // so the player can pick the Tier-3 ceremony lines as nested follow-ups
                // (hybrid placement: still also at root for fast access on return visits).
                starter.AddDialogLine("bp_pact_tease_response", "bp_encounter_pact_tease", "bp_encounter_pact_tease_branch",
                    "{=!}{PACT_TEASE_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetPactTeaseVariable), null);

                // 2026-06-03 hybrid Tier-3 placement: mirror oath_brother + pact_answer
                // as nested follow-ups inside the pact-tease branch (same conditions as
                // the root-level entries). Players who explore the relationship through
                // pact-tease see the ceremony options where they narratively belong.
                // Outputs route to the SAME encounter tokens as the root-level entries
                // (bp_encounter_oath_brother / bp_encounter_pact_answer), so the chief
                // response + OnConsequence (OnTakeBrotherOath / OnSwearPact) fire once
                // and identically regardless of which path the player took.
                starter.AddPlayerLine("bp_player_oath_brother_nested", "bp_encounter_pact_tease_branch", "bp_encounter_oath_brother",
                    "{=bp_dialogmanager_088}Then call me brother. <span style=\"Conversation.Persuasion.Negative\">(Hard)</span>",
                    new ConversationSentence.OnConditionDelegate(CanTakeBrotherOath), null);
                starter.AddPlayerLine("bp_player_pact_answer_nested", "bp_encounter_pact_tease_branch", "bp_encounter_pact_answer",
                    "{=bp_dialogmanager_089}You asked once if I'd bleed for you. Hear my answer. <span style=\"Conversation.Persuasion.Negative\">(Hard)</span>",
                    new ConversationSentence.OnConditionDelegate(CanSwearPact), null);
                starter.AddPlayerLine("bp_player_troop_loan_nested", "bp_encounter_pact_tease_branch", "bp_encounter_troop_loan",
                    "{=bp_dialogmanager_090}Send your boys with mine for a season. <span style=\"Conversation.Persuasion.Negative\">(Hard)</span>",
                    new ConversationSentence.OnConditionDelegate(CanRequestTroopLoan), null);
                starter.AddPlayerLine("bp_player_pact_tease_back", "bp_encounter_pact_tease_branch", "bp_encounter_back_relay",
                    "{=bp_dialogmanager_091}[Step back to other matters.]",
                    null, null);

                // === Tier 3 chief responses (v58) ===

                // Brother oath — one-shot ritual moment. After this fires, _chiefOathTaken
                // is set for this culture and the option self-disables. Drink-with-me
                // option's response could later check the flag for a brother-wording
                // variant, but for v58 we keep it simple: oath = ceremony, no further
                // mechanical change. Pure narrative weight.
                // 2026-06-03 story-expansion: per-chief authored response via
                // {OATH_BROTHER_BLURB} placeholder set by SetOathBrotherVariable.
                starter.AddDialogLine("bp_oath_brother_response", "bp_encounter_oath_brother", "close_window",
                    "{=!}{OATH_BROTHER_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetOathBrotherVariable),
                    new ConversationSentence.OnConsequenceDelegate(OnTakeBrotherOath));

                // Pact answer — cashes the Tier 2 pact_tease. Sets _pactSworn flag for
                // future faction-state systems to consume. Narrative-only for now.
                // 2026-06-03 story-expansion: per-chief authored response via
                // {PACT_ANSWER_BLURB} placeholder set by SetPactAnswerVariable.
                starter.AddDialogLine("bp_pact_answer_response", "bp_encounter_pact_answer", "close_window",
                    "{=!}{PACT_ANSWER_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetPactAnswerVariable),
                    new ConversationSentence.OnConsequenceDelegate(OnSwearPact));

                // Burdens monologue — chief shares his deepest grievance. Pulls a small
                // relation reward (+5) and sets a culture-flavored response variable
                // from the existing ChiefBackstory data. One-shot per visit; reusable
                // across visits (each time the player asks, the chief's grievance comes
                // out a little fuller).
                starter.AddDialogLine("bp_burdens_response", "bp_encounter_burdens", "bp_encounter_root",
                    "{=!}{BURDEN_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetBurdensVariable),
                    new ConversationSentence.OnConsequenceDelegate(OnAskBurdens));

                // Troop loan — REAL gameplay mechanic. Adds N bandits to player's party
                // for 7 days. Daily-tick removes them on return.
                // 2026-06-03 story-expansion: per-chief authored response via
                // {TROOP_LOAN_BLURB} placeholder set by SetTroopLoanVariable.
                starter.AddDialogLine("bp_troop_loan_response", "bp_encounter_troop_loan", "close_window",
                    "{=!}{TROOP_LOAN_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetTroopLoanVariable),
                    new ConversationSentence.OnConsequenceDelegate(OnAcceptTroopLoan));

                // v61: named-target hunt response — picks a random nearby noble at
                // accept time and spawns the BanditNamedTargetQuest. Chief names the
                // target inline so the player knows who they're hunting before the
                // journal entry renders. Closes window.
                starter.AddDialogLine("bp_namedtarget_response", "bp_encounter_namedtarget", "close_window",
                    "{=!}{NAMEDTARGET_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetNamedTargetVariable),
                    new ConversationSentence.OnConsequenceDelegate(OnAcceptNamedTargetHunt));

                // 2026-06-03 story-expansion: collect-bounty chief response. Fires
                // per-chief closure prose (NamedTargetComplete for player-kill, or
                // NamedTargetOtherCauseProse for other-cause death). Consequence calls
                // FinishWithSuccess on the pending quest, which triggers reward delivery
                // via OnCompleteWithSuccess.
                starter.AddDialogLine("bp_collect_named_target_response", "bp_encounter_collect_named_target", "close_window",
                    "{=!}{COLLECT_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetCollectNamedTargetVariable),
                    new ConversationSentence.OnConsequenceDelegate(OnCollectNamedTargetBounty));

                // 2026-06-03 story-expansion: raid-village accept + collect responses.
                starter.AddDialogLine("bp_raidvillage_response", "bp_encounter_raidvillage", "close_window",
                    "{=!}{RAID_VILLAGE_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetRaidVillageVariable),
                    new ConversationSentence.OnConsequenceDelegate(OnAcceptRaidVillage));
                starter.AddDialogLine("bp_collect_raid_village_response", "bp_encounter_collect_raid_village", "close_window",
                    "{=!}{COLLECT_RAID_VILLAGE_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetCollectRaidVillageVariable),
                    new ConversationSentence.OnConsequenceDelegate(OnCollectRaidVillageBounty));

                starter.AddDialogLine("bp_pleasantry_response", "bp_encounter_pleasantry", "bp_encounter_root",
                    "{=!}{PLEASANTRY_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetPleasantryVariable), null);

                starter.AddDialogLine("bp_threat_response", "bp_encounter_threat", "bp_encounter_root",
                    "{=!}{THREAT_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetThreatVariable), null);

                // v56: bp_trade_response removed (paired with bp_player_trade removal).
                starter.AddDialogLine("bp_bribe_response", "bp_encounter_bribe", "close_window",
                    "{=bp_bribe_response}A pleasure doing business with you.", null,
                    new ConversationSentence.OnConsequenceDelegate(OnBribeConsequence));

                starter.AddDialogLine("bp_farewell_response", "bp_encounter_farewell", "close_window",
                    "{=bp_dialog_farewell}Good.", null,
                    new ConversationSentence.OnConsequenceDelegate(EndEncounterNoCombat));

                // === Wave 4.11-fix v56: new chief responses for drink/rumor/sever ===
                // Drink: short flavor exchange + small relation gain via consequence.
                starter.AddDialogLine("bp_drink_response", "bp_encounter_drink", "close_window",
                    "{=bp_dialogmanager_092}We've got mead, ale, or whatever the boys haven't drunk yet. Sit, traveler — your fire as much as mine for tonight.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(OnDrinkConsequence));

                // Rumor: chief shares a short road tip. Random pick from a small generic
                // pool for now — culture-specific rumor pools can be authored later as a
                // VendorProfiles-style extension.
                starter.AddDialogLine("bp_rumor_response", "bp_encounter_rumor", "bp_encounter_root",
                    "{=!}{RUMOR_BLURB}",
                    new ConversationSentence.OnConditionDelegate(SetRumorVariable), null);

                // Sever: graceful in-camp exit when player wants out without combat.
                // Small relation hit (-1) reflects the cooled relationship without
                // triggering hostile-faction state. Closes window.
                starter.AddDialogLine("bp_sever_response", "bp_encounter_sever", "close_window",
                    "{=bp_dialogmanager_093}Then walk back the way you came in, traveler. The fire stays here. So do my men — and so does the trust you spent today.",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(OnSeverConsequence));

                // === Wave 4.6 trust quest dialog ===
                // v56b (2026-05-08): renamed from "I'd like to do business with you" —
                // confusing because the chief doesn't trade. The line actually asks the
                // chief if he has a trust-quest to offer, so the new wording is accurate
                // to its function.
                starter.AddPlayerLine(
                    "bp_trust_offer_request",
                    "bp_encounter_root",
                    "bp_trust_offer_chief",
                    "{=bp_dialogmanager_094}Do you have any quest for me? <span style=\"Conversation.Persuasion.Neutral\">(Quests)</span>",
                    new ConversationSentence.OnConditionDelegate(() => PrepareQuestOfferText()),
                    null,
                    150);

                starter.AddDialogLine(
                    "bp_trust_offer_chief_line",
                    "bp_trust_offer_chief",
                    "bp_trust_offer_player",
                    "{=!}{BP_QUEST_INTRO}",
                    null, null, 150);

                starter.AddPlayerLine(
                    "bp_trust_offer_accept",
                    "bp_trust_offer_player",
                    "close_window",
                    "{=bp_dialogmanager_095}I'll bring it. ({BP_QUEST_LABEL}) <span style=\"Conversation.Persuasion.Positive\">(Easy)</span>",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(OnAcceptTrustQuest),
                    150);

                starter.AddPlayerLine(
                    "bp_trust_offer_decline",
                    "bp_trust_offer_player",
                    "close_window",
                    "{=bp_dialogmanager_096}Not interested.",
                    null, null, 150);

                starter.AddPlayerLine(
                    "bp_trust_remind_request",
                    "bp_encounter_root",
                    "bp_trust_remind_chief",
                    "{=bp_dialogmanager_097}About that errand — what was it again?",
                    new ConversationSentence.OnConditionDelegate(() => IsTrustQuestActiveReminder() && PrepareQuestReminderText()),
                    null,
                    150);

                starter.AddDialogLine(
                    "bp_trust_remind_chief_line",
                    "bp_trust_remind_chief",
                    "close_window",
                    "{=bp_dialogmanager_098}I asked for {BP_QUEST_LABEL}. Bring it when you have it. Don't bother me until you do.",
                    null, null, 150);

                starter.AddPlayerLine(
                    "bp_trust_handover_request",
                    "bp_encounter_root",
                    "bp_trust_handover_chief",
                    "{=bp_dialogmanager_099}I have what you asked for. ({BP_QUEST_LABEL})",
                    new ConversationSentence.OnConditionDelegate(() => IsTrustQuestActiveAndComplete() && PrepareQuestThanksText()),
                    null,
                    160);

                starter.AddDialogLine(
                    "bp_trust_handover_chief_line",
                    "bp_trust_handover_chief",
                    "close_window",
                    "{=!}{BP_QUEST_THANKS}",
                    null,
                    new ConversationSentence.OnConsequenceDelegate(OnCompleteTrustQuest),
                    160);

                // === Wave 4.6.1: chief dialog from inside the walkable camp ===
                // When player presses F on the boss NPC during a peaceful walkable hideout,
                // route them into bp_encounter_root so all the existing chief options
                // (lore/history/trust quest/etc.) become available in-scene too. Per-culture
                // greeting set via CHIEF_CAMP_GREETING text variable. Priority 170 — higher
                // than the campaign-map bp_encounter_start (default) so this wins when both
                // could match, but lower than greeter (200) so guard intercept still works.
                // === Wave 4.11-fix v39 (2026-05-07): first-meet identity exchange ===
                // On TRUST=0 (stranger): chief delivers slow-burn appraisal greeting → player
                // names self → chief delivers culture-specific loyalty speech → routed into
                // bp_encounter_root so player can accept the trust quest. Priority 180 beats
                // the standard chief_camp_start (170) so first-meet path wins when trust=0.
                // When trust>=1, IsChiefFirstMeetConv returns false and chief_camp_start (170)
                // takes over with ChiefGreetingTolerated body — return-visit recognition.
                starter.AddDialogLine(
                    "bp_chief_firstmeet_start",
                    "start",
                    "bp_chief_firstmeet_player_intro",
                    "{=!}{CHIEF_FIRSTMEET_GREETING}",
                    new ConversationSentence.OnConditionDelegate(IsChiefFirstMeetConv),
                    null,
                    180);

                // Wave 4.11-fix v40: TWO player intro options at the firstmeet beat — the
                // diplomat path and the pragmatist path. Different player personalities,
                // both converge on the chief's loyalty speech. Adds conversational depth
                // without doubling the chief's authoring burden.
                starter.AddPlayerLine(
                    "bp_chief_firstmeet_player_intro_diplomat",
                    "bp_chief_firstmeet_player_intro",
                    "bp_chief_firstmeet_loyalty",
                    "{=bp_dialogmanager_100}I'm called {PLAYER.NAME}. I walk no man's banner — I came because I'd heard your camp deals fairly with strangers who carry their own coin and their own steel.",
                    null,
                    null,
                    180);

                starter.AddPlayerLine(
                    "bp_chief_firstmeet_player_intro_blunt",
                    "bp_chief_firstmeet_player_intro",
                    "bp_chief_firstmeet_loyalty",
                    "{=bp_dialogmanager_101}{PLAYER.NAME}'s the name. Save the speeches, old friend — I came to do business, not exchange courtesies.",
                    null,
                    null,
                    180);

                starter.AddPlayerLine(
                    "bp_chief_firstmeet_player_intro_cautious",
                    "bp_chief_firstmeet_player_intro",
                    "bp_chief_firstmeet_loyalty",
                    "{=bp_dialogmanager_102}{PLAYER.NAME}. I'd rather hear what your camp wants from me than waste breath on flattery — your boys' steel said enough on the way in.",
                    null,
                    null,
                    180);

                // Loyalty speech now routes into a player-reaction beat (not directly into
                // bp_encounter_root) so the player can register a tonal response to the
                // chief's terms before standard options become available.
                starter.AddDialogLine(
                    "bp_chief_firstmeet_loyalty",
                    "bp_chief_firstmeet_loyalty",
                    "bp_chief_firstmeet_reaction",
                    "{=!}{CHIEF_LOYALTY_SPEECH}",
                    new ConversationSentence.OnConditionDelegate(SetChiefLoyaltyVariable),
                    null,
                    180);

                // Player reaction beat: three tonal options after hearing the loyalty
                // speech. Acknowledge → root (standard options including trust quest).
                // Curious → ask one short question, chief gives task hook → root.
                // Refuse → chief delivers cold-close line, conversation ends.
                starter.AddPlayerLine(
                    "bp_chief_firstmeet_reaction_curious",
                    "bp_chief_firstmeet_reaction",
                    "bp_chief_firstmeet_task_hint",
                    "{=bp_dialogmanager_103}Plain enough. Tell me what your men want done — I'll judge the work before I commit to it.",
                    null,
                    null,
                    180);

                starter.AddPlayerLine(
                    "bp_chief_firstmeet_reaction_acknowledge",
                    "bp_chief_firstmeet_reaction",
                    "bp_encounter_root",
                    "{=bp_dialogmanager_104}Fair terms. I'll hear what else you have to say.",
                    null,
                    null,
                    180);

                starter.AddPlayerLine(
                    "bp_chief_firstmeet_reaction_refuse",
                    "bp_chief_firstmeet_reaction",
                    "bp_chief_firstmeet_rejection",
                    "{=bp_dialogmanager_105}I take no errands from outlaws. Find another fool to fetch your hides.",
                    null,
                    null,
                    180);

                // Chief's response to "tell me about the task" — short generic prompt that
                // points the player at the trust-quest accept option in the standard tree
                // (which itself has the culture-specific quest text via OfferTrustQuest).
                starter.AddDialogLine(
                    "bp_chief_firstmeet_task_hint",
                    "bp_chief_firstmeet_task_hint",
                    "bp_encounter_root",
                    "{=bp_dialogmanager_106}Then hear it from my own mouth, traveler — I'll tell you what we want, and you'll tell me whether your steel's worth what I'm asking it to do. Speak when you're ready.",
                    null,
                    null,
                    180);

                // Chief's cold-close response to the refusal. Fires only when player
                // explicitly rejects the loyalty terms. Conversation ends — player can
                // walk away or come back later (trust will still be 0 on return, so the
                // firstmeet flow plays again from the top).
                starter.AddDialogLine(
                    "bp_chief_firstmeet_rejection",
                    "bp_chief_firstmeet_rejection",
                    "close_window",
                    "{=bp_dialogmanager_107}Then walk back the way you came in, traveler — and hope my boys remember you walked in unbloodied. The fire's not yours, the trade's not yours, and the road back is shorter the faster you take it.",
                    null,
                    null,
                    180);

                starter.AddDialogLine(
                    "bp_chief_camp_start",
                    "start",
                    "bp_encounter_root",
                    "{=!}{CHIEF_CAMP_GREETING}",
                    new ConversationSentence.OnConditionDelegate(IsChiefCampConv),
                    null,
                    170);

                // === Wave 4.6.3: ambient catch-all for any other in-camp NPC ===
                // Camp has 25 NPCs; only the boss has chief dialog. The vendors and greeter
                // have their own dialog. Wanderers/sitters/trainers/guards have nothing —
                // pressing F on them was firing vanilla "I'm not allowed to talk to you."
                // This catch-all gives them a non-committal redirect line so the immersion
                // holds. Priority 100 (low) so it loses to anything more specific.
                starter.AddDialogLine(
                    "bp_camp_ambient_npc",
                    "start",
                    "close_window",
                    "{=bp_dialogmanager_108}{BP_SPEAKER_NAME}: I'm just a fighter, traveler. Talk to the chief if you've got business — he sits at the heart of the camp.",
                    new ConversationSentence.OnConditionDelegate(IsCampAmbientNpc),
                    null,
                    100);

                // Add a "Talk" option to the bandit encounter menu so player can trigger the dialog above.
                starter.AddGameMenuOption("encounter", "bp_talk_option",
                    "{=bp_dialogmanager_109}Talk to the bandits.",
                    new GameMenuOption.OnConditionDelegate(IsBpEncounterMenuVisible),
                    new GameMenuOption.OnConsequenceDelegate(OnTalkConsequence),
                    false, -1, false);

                starter.AddGameMenuOption("encounter_attacker", "bp_talk_option_attacker",
                    "{=bp_dialogmanager_110}Talk to the bandits.",
                    new GameMenuOption.OnConditionDelegate(IsBpEncounterMenuVisible),
                    new GameMenuOption.OnConsequenceDelegate(OnTalkConsequence),
                    false, -1, false);

                // === Wave 3c.3: single "Visit the camp" entry on the vanilla hideout menu.
                // All peaceful options live in the bp_hideout_camp submenu below to keep the
                // top-level menu uncluttered (one peace option + vanilla hostile options).
                starter.AddGameMenuOption("hideout_place", "bp_visit_camp",
                    "{=bp_dialogmanager_111}Visit the camp.",
                    new GameMenuOption.OnConditionDelegate(IsHideoutPeacefulVisible),
                    new GameMenuOption.OnConsequenceDelegate(OnVisitCampConsequence),
                    false, 1, false);

                // Wave 4.21.0 — the Go-Between: town menu option to get vouched
                // into a culture's camps. Appears only in the gate town of a
                // culture the player can't yet enter (origin chosen, not Outlaw).
                starter.AddGameMenuOption("town", "bp_gobetween",
                    "{=!}{BP_GOBETWEEN_LABEL}",
                    new GameMenuOption.OnConditionDelegate(IsGoBetweenVisible),
                    new GameMenuOption.OnConsequenceDelegate(OnGoBetweenConsequence),
                    false, 4, false);

                // === bp_hideout_camp submenu: all peaceful options grouped here ===
                // Intro text is set per-culture by OnCampMenuInit via SetTextVariable.
                // Wave 4.4 menu polish: section dividers (disabled options with em-dash labels)
                // visually group related actions. Index gaps between sections (0/19/29/39/49)
                // keep dividers locked above their group regardless of which options are visible
                // for the current culture. Title + time-of-day flavor injected by OnCampMenuInit.
                starter.AddGameMenu("bp_hideout_camp",
                    "{=!}{CAMP_INTRO_TEXT}",
                    new OnInitDelegate(OnCampMenuInit),
                    GameMenu.MenuOverlayType.None, GameMenu.MenuFlags.None, null);

                // -- Section: ENTER --
                starter.AddGameMenuOption("bp_hideout_camp", "bp_section_enter",
                    "{=bp_dialogmanager_112}— ENTER THE CAMP —",
                    args => { args.IsEnabled = false; return true; },
                    _ => { }, false, 0, false);

                // -- Wave 3c.4 Phase 6 — walk into the camp (3D scene). Promoted to top of
                // the menu as the headline action. New approach: set HideoutPeacefulVisitState
                // flag, call PlayerEncounter.EnterSettlement() to use vanilla's tested
                // encounter flow. CreateLocationEncounterPatch swaps in WalkableHideoutEncounter
                // which calls CampaignMission.OpenVillageMission — same 27-behavior stack
                // vanilla uses for villages.
                starter.AddGameMenuOption("bp_hideout_camp", "bp_camp_walk_in",
                    "{=bp_dialogmanager_113}Walk into the camp.",
                    new GameMenuOption.OnConditionDelegate(IsHideoutPeacefulVisible),
                    new GameMenuOption.OnConsequenceDelegate(OnWalkInCampConsequence),
                    false, 10, false);

                // -- Section: GATE TOLL --
                // Wave 4.11-fix v64 (2026-05-08): chief-talk + trade + armory + map options
                // removed from outside menu. Player must walk INTO the camp to speak with
                // the chief or trade with vendors. Section header renamed to reflect what
                // actually remains here (the bribe is a sentry-tier transaction).
                starter.AddGameMenuOption("bp_hideout_camp", "bp_section_talk",
                    "{=bp_dialogmanager_114}— GATE TOLL —",
                    args => { args.IsEnabled = false; return true; },
                    _ => { }, false, 19, false);

                starter.AddGameMenuOption("bp_hideout_camp", "bp_camp_bribe",
                    "{=bp_dialogmanager_115}Bribe the chief for safe passage. ({CAMP_BRIBE_AMOUNT} denars)",
                    new GameMenuOption.OnConditionDelegate(IsCampBribeVisible),
                    new GameMenuOption.OnConsequenceDelegate(OnCampBribeConsequence),
                    false, 21, false);

                // -- Section: GATE SERVICES --
                // Doctor + recruitment remain at the gate (tents pitched at the perimeter,
                // not inside). Trading & armory moved inside the camp at v64.
                starter.AddGameMenuOption("bp_hideout_camp", "bp_section_trade",
                    "{=bp_dialogmanager_116}— GATE SERVICES —",
                    args => { args.IsEnabled = false; return true; },
                    _ => { }, false, 29, false);

                starter.AddGameMenuOption("bp_hideout_camp", "bp_camp_doctor",
                    "{=bp_dialogmanager_117}Visit the camp doctor.",
                    new GameMenuOption.OnConditionDelegate(IsHideoutDoctorVisible),
                    new GameMenuOption.OnConsequenceDelegate(OnHideoutDoctorConsequence),
                    false, 32, false);

                // Wave 4.12 (2026-05-11): "Offer gold for their fighters" removed —
                // troop recruitment moved to the Slaver vendor's trade root.

                // -- Section: PERIMETER --
                // v64: renamed from "CAMP LIFE" to better describe these gate-edge actions.
                // Two new entries added: scout + watchtower.
                starter.AddGameMenuOption("bp_hideout_camp", "bp_section_camp_life",
                    "{=bp_dialogmanager_118}— PERIMETER —",
                    args => { args.IsEnabled = false; return true; },
                    _ => { }, false, 39, false);

                starter.AddGameMenuOption("bp_hideout_camp", "bp_camp_scout",
                    "{=bp_dialogmanager_119}Scout the perimeter for nearby parties.",
                    new GameMenuOption.OnConditionDelegate(IsCampScoutVisible),
                    new GameMenuOption.OnConsequenceDelegate(OnCampScoutConsequence),
                    false, 40, false);

                starter.AddGameMenuOption("bp_hideout_camp", "bp_camp_watchtower",
                    "{=bp_dialogmanager_120}Watch from the watchtower.",
                    new GameMenuOption.OnConditionDelegate(IsCampWatchtowerVisible),
                    new GameMenuOption.OnConsequenceDelegate(OnCampWatchtowerConsequence),
                    false, 41, false);

                starter.AddGameMenuOption("bp_hideout_camp", "bp_camp_drink",
                    "{=bp_dialogmanager_121}Drink with the bandits. (10 denars, +1 relation)",
                    new GameMenuOption.OnConditionDelegate(IsCampDrinkVisible),
                    new GameMenuOption.OnConsequenceDelegate(OnCampDrinkConsequence),
                    false, 42, false);

                starter.AddGameMenuOption("bp_hideout_camp", "bp_camp_eavesdrop",
                    "{=bp_dialogmanager_122}Eavesdrop on bandit talk.",
                    new GameMenuOption.OnConditionDelegate(IsCampEavesdropVisible),
                    new GameMenuOption.OnConsequenceDelegate(OnCampEavesdropConsequence),
                    false, 43, false);

                starter.AddGameMenuOption("bp_hideout_camp", "bp_camp_rest",
                    "{=bp_dialogmanager_123}Rest in the camp. (+5 morale)",
                    new GameMenuOption.OnConditionDelegate(IsCampRestVisible),
                    new GameMenuOption.OnConsequenceDelegate(OnCampRestConsequence),
                    false, 44, false);

                // Wave 4.12 (2026-05-11): CULTURE-SPECIFIC section removed entirely —
                // the per-culture chief service (Granny Hod's provisions, etc.) and the
                // three extras (deep-wood druid, hide-tanner, wolf-hound trainer, etc.)
                // moved off the gate menu. Their replacements live inside the camp:
                // the chief's Tier 1/2/3 menus and the slaver's trade root now carry
                // the per-culture content. The consequence helpers (OnCultureServiceConsequence,
                // OnCultureExtraConsequence) and condition predicates (IsCultureServiceVisible,
                // IsCultureExtraVisible) remain in the file for now — they're dead code
                // but harmless; a future wave can prune them after a soak period.

                // -- Back --
                starter.AddGameMenuOption("bp_hideout_camp", "bp_camp_back",
                    "{=bp_dialogmanager_124}Back to the hideout entrance.",
                    null,
                    new GameMenuOption.OnConsequenceDelegate(_ => GameMenu.SwitchToMenu("hideout_place")),
                    true, -1, false);
                // Note: vanilla "Leave" already exists on hideout_place — no need to add another.

            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Dialog registration error: " + ex.Message, Colors.Red));
            }
        }

        // Wave 4.6.2: realistic chief dialog gating. The chief is a stranger at Tier 0,
        // gradually opens up as trust grows. Resolves the encounter's culture id from
        // the hideout (peaceful walkable visits) OR the encountered party (random world-
        // map encounters). Returns 0 if culture can't be resolved — chief stays guarded.
        private string GetCurrentEncounterCultureId()
        {
            try
            {
                var hideout = GetCurrentHideout();
                if (hideout != null)
                {
                    string cid = GetCultureIdForHideout(hideout);
                    if (!string.IsNullOrEmpty(cid)) return cid;
                }
                var party = PlayerEncounter.EncounteredParty?.MobileParty
                          ?? MobileParty.ConversationParty;
                if (party != null)
                {
                    return party.MapFaction?.Culture?.StringId
                        ?? party.LeaderHero?.Clan?.Culture?.StringId
                        ?? "";
                }
            }
            catch (Exception ex) {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("GetCurrentEncounterCultureId fail: " + ex.GetType().Name + ": " + ex.Message);
            }
            return "";
        }

        private int GetCurrentEncounterTrust()
        {
            return GetCultureTrust(GetCurrentEncounterCultureId());
        }

        // Wave 4.6.2 dialog condition methods — gate existing chief options by trust tier.
        // Tier 1 (tolerated): unlocks small talk, surface-level clan info, vanilla trade.
        // Tier 2 (trusted): unlocks deep operational/political/spiritual topics.
        private bool IsChiefTier1() { return GetCurrentEncounterTrust() >= 1; }
        private bool IsChiefTier2() { return GetCurrentEncounterTrust() >= 2; }

        // Wave 4.6.3: catch-all for in-camp NPCs that have no specific dialog (wanderers,
        // sitters, trainers, idle guards). Returns true ONLY if peaceful flag is on AND the
        // partner is not the boss / not a vendor / not the dispatched greeter — i.e., a
        // generic bandit. Prevents vanilla "I'm not allowed to talk to you" from firing.
        private bool IsCampAmbientNpc()
        {
            try
            {
                if (!BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Active) return false;
                var beh = Campaign.Current?.GetCampaignBehavior<BandItPlus.HideoutVisit.HideoutVisitNpcSpawnBehavior>();
                if (beh == null) return false;
                if (beh.IsGreeterDispatchInFlight()) return false;
                // Wave 4.11-fix v36 (2026-05-07): use canonical partner capture (Harmony
                // prefix on Mission.OnAgentInteraction) instead of GetLookAgent. See
                // BandItPlus.Patches.AgentInteractionPatch for rationale.
                var partner = BandItPlus.Patches.AgentInteractionPatch.GetFreshPartner()
                              ?? Mission.Current?.MainAgent?.GetLookAgent();
                if (partner == null) return false;
                // If partner is a known special role, let that role's dialog win.
                if (beh.IsAgentBoss(partner)) return false;
                if (beh.IsAgentFoodVendor(partner)) return false;
                if (beh.IsAgentGearVendor(partner)) return false;
                if (beh.IsAgentDispatchedGuard(partner)) return false;
                // Otherwise this is an ambient camp NPC — fire the generic line.
                // Wave 4.6.4: surface the agent's per-instance unique name (set by Wave 4
                // spawn system via Agent._name) in BOTH the speaker label and dialog body.
                string ambientName = partner.Name?.ToString() ?? Localization.Get("bp_dialogmanager_125", "Bandit");
                var ambientChar = partner.Character as CharacterObject;
                if (ambientChar != null)
                    BandItPlus.Patches.ConversationNamePatch.OverrideName(ambientChar, ambientName);
                MBTextManager.SetTextVariable("BP_SPEAKER_NAME", ambientName);
                return true;
            }
            catch { return false; }
        }

        // Wave 4.11-fix v39 (2026-05-07): first-meet condition. Mirrors IsChiefCampConv
        // logic for partner identification, but ONLY matches when culture-trust is exactly
        // 0 (stranger). Sets CHIEF_FIRSTMEET_GREETING text variable so the dialog body
        // shows the slow-burn appraisal greeting. After player names themselves, dialog
        // flows to bp_chief_firstmeet_loyalty (which sets CHIEF_LOYALTY_SPEECH and routes
        // into bp_encounter_root for trust-quest acceptance / standard options).
        private bool IsChiefFirstMeetConv()
        {
            try
            {
                if (!BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Active) return false;
                var beh = Campaign.Current?.GetCampaignBehavior<BandItPlus.HideoutVisit.HideoutVisitNpcSpawnBehavior>();
                if (beh == null) return false;
                if (beh.IsGreeterDispatchInFlight()) return false;

                bool isBossConv = beh.IsBossConvBeingInitiated();
                if (!isBossConv)
                {
                    var partner = BandItPlus.Patches.AgentInteractionPatch.GetFreshPartner()
                                  ?? Mission.Current?.MainAgent?.GetLookAgent();
                    isBossConv = partner != null && beh.IsAgentBoss(partner);
                }
                if (!isBossConv) return false;

                // Trust-0 gate: only stranger first-meets fire this token.
                if (GetCurrentEncounterTrust() != 0) return false;

                // Wave 4.12-fix3 (2026-05-11): also suppress first-meet when player
                // already has an active trust quest for this culture. Reason: trust
                // only increments on quest COMPLETION, so it stays at 0 throughout the
                // entire T1 quest lifecycle. Without this check, every visit between
                // accept-T1 and complete-T1 would re-fire the slow-burn intro + name
                // exchange + loyalty speech — same conversation, every time. Once a
                // quest is accepted, the player and chief have already established the
                // relationship; lower-priority bp_chief_camp_start (170) takes over.
                var hideout = GetCurrentHideout();
                var cultureId = hideout?.Culture?.StringId;
                if (!string.IsNullOrEmpty(cultureId) && HasActiveQuest(cultureId)) return false;

                var profile = GetProfileForHideout(hideout);
                string chiefName = profile?.ChiefName ?? Localization.Get("bp_dialogmanager_126", "The chief");
                string greeting = profile?.ChiefGreeting ?? "{=bp_dialogmanager_127}What brings you to me, traveler?";
                var chiefChar = beh.GetBossCharacter() as CharacterObject;
                if (chiefChar != null)
                    BandItPlus.Patches.ConversationNamePatch.OverrideName(chiefChar, chiefName);
                MBTextManager.SetTextVariable("CHIEF_FIRSTMEET_GREETING", greeting);
                return true;
            }
            catch { return false; }
        }

        // Wave 4.11-fix v39: sets CHIEF_LOYALTY_SPEECH from the per-culture profile when
        // bp_chief_firstmeet_loyalty fires. Returning true so the line always renders.
        private bool SetChiefLoyaltyVariable()
        {
            try
            {
                var hideout = GetCurrentHideout();
                var profile = GetProfileForHideout(hideout);
                string speech = profile?.ChiefLoyaltySpeech
                    ?? "{=bp_dialogmanager_128}Names mean nothing to me until deeds back them, traveler. There's a task. Bring back the wage of it, and we'll talk again as something other than strangers.";
                MBTextManager.SetTextVariable("CHIEF_LOYALTY_SPEECH", speech);
                return true;
            }
            catch { return true; }
        }

        // Wave 4.6.1: condition for "press F on boss inside walkable camp" → opens the
        // chief's dialog tree (bp_encounter_root). Sets CHIEF_CAMP_GREETING from the per-
        // culture profile so the greeting reads in the chief's voice. Greeter wins over us
        // by priority — guard intercept still happens before this fires.
        private bool IsChiefCampConv()
        {
            try
            {
                if (!BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Active) return false;
                var beh = Campaign.Current?.GetCampaignBehavior<BandItPlus.HideoutVisit.HideoutVisitNpcSpawnBehavior>();
                if (beh == null) return false;
                if (beh.IsGreeterDispatchInFlight()) return false;

                // Wave 4.6.5d: PRIMARY identification — flag set by TickBossInteraction
                // during the synchronous OnAgentInteraction call. GetLookAgent() returns
                // null during programmatic triggering, so we can't rely on it alone.
                bool isBossConv = beh.IsBossConvBeingInitiated();

                // Fallback: canonical partner capture (Wave 4.11-fix v36, 2026-05-07).
                // For F-press paths the engine routes through Mission.OnAgentInteraction,
                // and AgentInteractionPatch captures the partner Agent reliably. We keep
                // GetLookAgent as a deeper fallback for any code path that may bypass our
                // prefix, but the captured agent is the canonical source.
                if (!isBossConv)
                {
                    var partner = BandItPlus.Patches.AgentInteractionPatch.GetFreshPartner()
                                  ?? Mission.Current?.MainAgent?.GetLookAgent();
                    isBossConv = partner != null && beh.IsAgentBoss(partner);
                    // (v36 ChiefCondDiag log removed in v60 cleanup — partner identity
                    // is reliable now that AgentInteractionPatch is in place.)
                }

                if (!isBossConv) return false;
                var hideout = GetCurrentHideout();
                var profile = GetProfileForHideout(hideout);
                // Wave 4.6.4: surface the chief's culture-specific authored name in BOTH
                // the speaker label (via ConversationNamePatch override) AND the dialog
                // body. The Harmony patch makes CharacterObject.Name return our authored
                // name for this specific CharacterObject during the conversation.
                string chiefName = profile?.ChiefName ?? Localization.Get("bp_dialogmanager_129", "The chief");
                // Wave 4.11-fix v38 (2026-05-07): tier-aware chief greeting. Trust 0
                // (stranger) → ChiefGreeting (slow-burn appraisal). Trust 1+ (tolerated
                // and beyond) → ChiefGreetingTolerated (warmer recognition). Falls back
                // to ChiefGreeting if no tolerated line authored for that culture.
                int chiefTrust = GetCurrentEncounterTrust();
                // Wave 4.12-fix3 (2026-05-11): also treat "active quest in progress" as
                // a tolerated/warmer relationship — the player has accepted the chief's
                // work even if trust hasn't yet been earned. This pairs with the
                // IsChiefFirstMeetConv suppression: when an active quest exists, the
                // first-meet intro is skipped AND the chief uses his return-visit voice
                // instead of the stranger greeting.
                var campConvHideout = GetCurrentHideout();
                var campConvCultureId = campConvHideout?.Culture?.StringId;
                bool hasActiveQuest = !string.IsNullOrEmpty(campConvCultureId) && HasActiveQuest(campConvCultureId);
                string greeting;
                if ((chiefTrust >= 1 || hasActiveQuest) && !string.IsNullOrEmpty(profile?.ChiefGreetingTolerated))
                    greeting = profile.ChiefGreetingTolerated;
                else
                    greeting = profile?.ChiefGreeting ?? "{=bp_dialogmanager_130}What brings you to me, traveler?";
                // Wave 4.6.5d: get boss CharacterObject via behavior accessor (works
                // whether we identified via flag or via look-agent fallback).
                var chiefChar = beh.GetBossCharacter() as CharacterObject;
                if (chiefChar != null)
                    BandItPlus.Patches.ConversationNamePatch.OverrideName(chiefChar, chiefName);
                MBTextManager.SetTextVariable("CHIEF_CAMP_GREETING", greeting);
                return true;
            }
            catch { return false; }
        }

        // --- Wave 4.6 trust quest dialog conditions and consequences ---
        private bool IsTrustQuestOfferable()
        {
            try
            {
                var hideout = GetCurrentHideout();
                if (hideout == null || !HasVisitedHideout(hideout)) return false;
                string cultureId = GetCultureIdForHideout(hideout);
                if (string.IsNullOrEmpty(cultureId)) return false;
                int trust = GetCultureTrust(cultureId);
                // Wave 4.7e: extended trust ladder to 3 tiers.
                if (trust >= 3) return false;
                if (HasActiveQuest(cultureId)) return false;
                if (!CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out var profile) || profile == null) return false;
                int nextTier = trust + 1;
                var nextQuest = GetOfferedTrustQuest(cultureId, nextTier);
                return nextQuest != null;
            }
            catch { return false; }
        }

        private bool IsTrustQuestActiveReminder()
        {
            try
            {
                var hideout = GetCurrentHideout();
                if (hideout == null) return false;
                string cultureId = GetCultureIdForHideout(hideout);
                if (!HasActiveQuest(cultureId)) return false;
                return !IsActiveQuestComplete(cultureId);
            }
            catch { return false; }
        }

        private bool IsTrustQuestActiveAndComplete()
        {
            try
            {
                var hideout = GetCurrentHideout();
                if (hideout == null) return false;
                string cultureId = GetCultureIdForHideout(hideout);
                if (!HasActiveQuest(cultureId)) return false;
                return IsActiveQuestComplete(cultureId);
            }
            catch { return false; }
        }

        // Side-effect-bearing dialog condition: returns true iff offer applies AND
        // sets BP_QUEST_INTRO and BP_QUEST_LABEL based on the next-tier quest.
        private bool PrepareQuestOfferText()
        {
            if (!IsTrustQuestOfferable()) return false;
            var hideout = GetCurrentHideout();
            string cultureId = GetCultureIdForHideout(hideout);
            int trust = GetCultureTrust(cultureId);
            // Wave 4.7e: pull from random pool (daily-seeded) so the chief offers
            // different errands each day. Variant snapshot happens at acceptance time.
            var quest = GetOfferedTrustQuest(cultureId, trust + 1);
            if (quest == null) return false;
            MBTextManager.SetTextVariable("BP_QUEST_INTRO", quest.IntroText ?? "");
            MBTextManager.SetTextVariable("BP_QUEST_LABEL", quest.ShortLabel ?? "");
            return true;
        }

        private bool PrepareQuestReminderText()
        {
            var hideout = GetCurrentHideout();
            string cultureId = GetCultureIdForHideout(hideout);
            var quest = GetActiveTrustQuest(cultureId);
            if (quest == null) return false;
            MBTextManager.SetTextVariable("BP_QUEST_LABEL", quest.ShortLabel ?? "");
            return true;
        }

        private bool PrepareQuestThanksText()
        {
            var hideout = GetCurrentHideout();
            string cultureId = GetCultureIdForHideout(hideout);
            var quest = GetActiveTrustQuest(cultureId);
            if (quest == null) return false;
            MBTextManager.SetTextVariable("BP_QUEST_THANKS", quest.ThanksText ?? "");
            MBTextManager.SetTextVariable("BP_QUEST_LABEL", quest.ShortLabel ?? "");
            return true;
        }

        private void OnAcceptTrustQuest()
        {
            try
            {
                var hideout = GetCurrentHideout();
                if (hideout == null) return;
                string cultureId = GetCultureIdForHideout(hideout);
                if (string.IsNullOrEmpty(cultureId)) return;
                int trust = GetCultureTrust(cultureId);
                // Wave 4.7e: tier scales with current trust; Tier 3 is the last step.
                int questTier = Math.Min(trust + 1, 3);

                // Wave 4.7e: snapshot the daily-seeded variant so the accepted quest's
                // items don't change if the player visits another day before turning in.
                int variantIdx = 0;
                if (CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out var profileForPick) && profileForPick != null)
                {
                    var pool = questTier == 1 ? profileForPick.TrustQuestTier1Pool
                             : questTier == 2 ? profileForPick.TrustQuestTier2Pool
                             : questTier == 3 ? profileForPick.TrustQuestTier3Pool
                             : null;
                    if (pool != null && pool.Length > 1)
                        variantIdx = PickOfferedVariantIdx(cultureId, questTier, pool.Length);
                }
                SetActiveQuest(cultureId, questTier, variantIdx);

                // Wave 4.7 (final): pinned API. Campaign.Current.QuestManager.OnQuestStarted
                // is the public registration entry point in Bannerlord 1.2.x (named like
                // an event handler but functions as Add/Register). Discovered via reflection
                // dump — see HideoutPeacefulVisitState.Log for the discovery trail.
                try
                {
                    // Wave 4.8b-fix: resolve a candidate hero through the full fallback
                    // chain FIRST (encounter party → OwnerClan.Heroes → MainHero), THEN
                    // pass to EnsureChiefNamed so the candidate gets renamed to the
                    // culture's ChiefName (e.g., "Skarvac of the High Pass"). This way
                    // it doesn't matter which fallback path produced the hero — they
                    // all get the cultural rename applied.
                    var chiefReg = BandItPlus.Heroes.BanditChiefRegistry.Instance;
                    // First check: do we already have a promoted chief? Use them directly.
                    Hero questGiver = chiefReg?.GetChief(cultureId);
                    if (questGiver == null)
                    {
                        // Resolve candidate through fallback chain.
                        var encParty = PlayerEncounter.EncounteredParty?.MobileParty;
                        Hero candidate = encParty?.LeaderHero ?? encParty?.Party?.LeaderHero;
                        if (candidate == null) candidate = hideout.OwnerClan?.Leader;
                        if (candidate == null && hideout.OwnerClan?.Heroes != null)
                        {
                            foreach (var h in hideout.OwnerClan.Heroes)
                                if (h != null && h.IsAlive) { candidate = h; break; }
                        }
                        // Promote if we have a candidate AND the registry is available.
                        if (candidate != null && chiefReg != null)
                            questGiver = chiefReg.EnsureChiefNamed(candidate, cultureId);
                        else
                            questGiver = candidate;
                    }
                    // Last-resort safety net so OnQuestStarted never gets a null QuestGiver.
                    if (questGiver == null) questGiver = Hero.MainHero;

                    string questId = "bp_trust_" + cultureId + "_t" + questTier
                        + "_v" + variantIdx + "_" + (long)CampaignTime.Now.ToHours;
                    var jq = new BandItPlus.Quests.BanditTrustQuest(
                        questId, questGiver, CampaignTime.Never, 100,
                        cultureId, questTier, variantIdx);
                    Campaign.Current?.QuestManager?.OnQuestStarted(jq);
                    // Wave 4.7c: discrete logs must be added AFTER OnQuestStarted —
                    // pre-registration AddDiscreteLog calls don't render.
                    jq.InitJournalEntries();
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "QuestGiver resolved: " + (questGiver?.Name?.ToString() ?? "NULL")
                        + " from clan " + (hideout.OwnerClan?.StringId ?? "(none)"));
                }
                catch (Exception jrnEx)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "OnAcceptTrustQuest journal-add fail: " + jrnEx.Message);
                }

                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "OnAcceptTrustQuest: cultureId=" + cultureId + " questTier=" + questTier + " variantIdx=" + variantIdx);
                InformationManager.DisplayMessage(new InformationMessage(
                    Localization.Get("bp_dialogmanager_131", "[BandIt Plus] You agreed to the chief's errand. Check the quest journal."), Colors.Yellow));
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Accept quest error: " + ex.Message, Colors.Red));
            }
        }

        private void OnCompleteTrustQuest()
        {
            try
            {
                var hideout = GetCurrentHideout();
                if (hideout == null) return;
                string cultureId = GetCultureIdForHideout(hideout);
                if (string.IsNullOrEmpty(cultureId)) return;
                int newTier = GetActiveQuestTier(cultureId);

                if (!DeductActiveQuestPayment(cultureId))
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        Localization.Get("bp_dialogmanager_132", "[BandIt Plus] You no longer have what was asked. The chief frowns."),
                        Colors.Red));
                    return;
                }
                SetCultureTrust(cultureId, newTier);
                // Wave 5 (Chief Recruitment): when the T3 trust quest completes, seed the
                // "spoken since T3" tracker. The dialog session that triggered this completion
                // IS a conversation with the chief, so it counts. Section 0 predicate +
                // BanditAllianceBehavior.IsEligibleForOffer rely on this bump.
                if (newTier == 3)
                {
                    MarkSpokeAfterT3Completion(cultureId);
                }
                ClearActiveQuest(cultureId);

                // Wave 4.7: finalize the matching journal entry. Search QuestManager.Quests
                // for a BanditTrustQuest with matching cultureId + tier and complete it.
                try
                {
                    var quests = Campaign.Current?.QuestManager?.Quests;
                    if (quests != null)
                    {
                        foreach (var q in quests)
                        {
                            if (q is BandItPlus.Quests.BanditTrustQuest btq
                                && btq.CultureId == cultureId
                                && btq.QuestTier == newTier)
                            {
                                // Wave 4.11-fix v50 (2026-05-08): bar finalization moved to
                                // BanditTrustQuest.OnCompleteWithSuccess override — the
                                // canonical QuestBase lifecycle hook. Engine calls it
                                // automatically when FinishWithSuccess→CompleteQuestWithSuccess
                                // fires, so we no longer call ForceCompleteJournalProgress
                                // directly from this dialog consequence. Cleaner separation:
                                // dialog deducts items + sets trust, quest finalizes its own
                                // journal display.
                                btq.FinishWithSuccess();
                                break;
                            }
                        }
                    }
                }
                catch (Exception jrnEx)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "OnCompleteTrustQuest journal-finish fail: " + jrnEx.Message);
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "OnCompleteTrustQuest: cultureId=" + cultureId + " newTier=" + newTier);
                InformationManager.DisplayMessage(new InformationMessage(
                    newTier == 1
                        ? Localization.Get("bp_dialogmanager_133", "[BandIt Plus] You are now tolerated by this culture. Trade, Doctor, and Hideout Map are open to you.")
                        : Localization.Get("bp_dialogmanager_134", "[BandIt Plus] You are now trusted. The Armory, Recruitment, and culture-specific services are open to you."),
                    Colors.Green));
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Complete quest error: " + ex.Message, Colors.Red));
            }
        }

        // 7-day cooldown per hideout for recruitment (non-persistent — resets on save reload, by design).
        private readonly Dictionary<string, CampaignTime> _hideoutRecruitCooldown = new Dictionary<string, CampaignTime>();

        private static Settlement GetCurrentHideout()
        {
            try
            {
                var s = Settlement.CurrentSettlement ?? PlayerEncounter.EncounterSettlement;
                if (s != null && s.IsHideout) return s;
            }
            catch (Exception ex) {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("GetCurrentHideout fail: " + ex.GetType().Name + ": " + ex.Message);
            }
            return null;
        }

        private static MobileParty GetHideoutParty(Settlement hideout)
        {
            if (hideout == null) return null;
            try
            {
                if (hideout.Party?.MobileParty != null) return hideout.Party.MobileParty;
                if (hideout.Parties != null)
                {
                    foreach (var p in hideout.Parties) { if (p != null) return p; }
                }
            }
            catch (Exception ex) {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("GetHideoutParty fail: " + ex.GetType().Name + ": " + ex.Message);
            }
            return null;
        }

        private static bool IsBanditHideout(Settlement hideout)
        {
            var cultureId = hideout?.Culture?.StringId;
            if (string.IsNullOrEmpty(cultureId)) return false;
            return cultureId.StartsWith("bp_") || VanillaBanditCultures.Contains(cultureId);
        }

        // Wave 4.26.0 (2026-06-17): HIDE the vanilla hideout combat options when the
        // player is at peace with the CURRENT encounter hideout's culture (read by
        // HideoutCombatOptionPatch). Delegates to the per-settlement gate below.
        public static bool ShouldSuppressHideoutCombat()
        {
            return IsHideoutAtPeace(GetCurrentHideout());
        }

        // Wave 4.27.0 (2026-06-17): the per-settlement peace gate (ANY hideout, not just
        // the current encounter) — also read by BandItPlusNameplateMixin to neutralise an
        // at-peace hideout's hostile (red) map nameplate color.
        public static bool IsHideoutAtPeace(Settlement hideout)
        {
            try
            {
                if (MCMSettings.Instance == null || !MCMSettings.Instance.EnableMod) return false;
                if (!IsBanditHideout(hideout)) return false;
                string cid = GetCultureIdForHideout(hideout);
                if (string.IsNullOrEmpty(cid)) return false;

                var origins = BandItPlus.Origins.BanditOriginBehavior.Instance;
                if (origins == null) return false;
                if (origins.IsAtPeaceWith(cid)) return true;                          // earned / granted peace
                if (origins.OriginChosen && origins.HasCampAccess(cid)) return true;  // vouched / Outlaw Blood
                if (!origins.OriginChosen && MCMSettings.Instance.PeacefulEncounters) return true; // global peaceful mode
                return false;
            }
            catch { return false; }
        }

        // Wave 4.28.x: STRICTER than IsHideoutAtPeace — true ONLY when peace is actually
        // EARNED with that chief (tribute paid / hunt completed = IsAtPeaceWith), NOT merely
        // vouched (camp access) or global peaceful mode. The nameplate COLOR uses this so a
        // hideout stays hostile-red until real peace; combat-suppression keeps the broader
        // IsHideoutAtPeace (you still walk in to parley while the chief is technically enemy).
        public static bool IsHideoutAtEarnedPeace(Settlement hideout)
        {
            try
            {
                if (MCMSettings.Instance == null || !MCMSettings.Instance.EnableMod) return false;
                if (!IsBanditHideout(hideout)) return false;
                string cid = GetCultureIdForHideout(hideout);
                if (string.IsNullOrEmpty(cid)) return false;
                var origins = BandItPlus.Origins.BanditOriginBehavior.Instance;
                return origins != null && origins.IsAtPeaceWith(cid);
            }
            catch { return false; }
        }

        // ── Wave 4.21.0: the Go-Between town option ──
        private string _gateTownMatchCid; // culture matched for the current town menu

        private bool IsGoBetweenVisible(MenuCallbackArgs args)
        {
            try
            {
                if (MCMSettings.Instance == null || !MCMSettings.Instance.EnableMod) return false;
                var origins = BandItPlus.Origins.BanditOriginBehavior.Instance;
                if (origins == null || !origins.OriginChosen) return false;
                // NOTE: no blanket "OutlawBlood -> hide" gate. With the kin-pick, OutlawBlood only
                // has access to its picked home clan, so it still needs a go-between for OTHER
                // cultures. The HasCampAccess loop below already hides this option when every
                // culture is accessible — which is exactly the old blanket-access case.

                var town = Settlement.CurrentSettlement;
                if (town == null || !town.IsTown) return false;

                foreach (var cid in BandItPlus.Origins.BanditOriginBehavior.AllParleyCultures)
                {
                    if (origins.HasCampAccess(cid)) continue;
                    var gate = origins.GetGateTown(cid);
                    if (gate != town) continue;
                    _gateTownMatchCid = cid;
                    string clan = BandItPlus.Origins.BanditOriginBehavior.CultureDisplay(cid);
                    MBTextManager.SetTextVariable("BP_GOBETWEEN_LABEL",
                        "Seek " + BandItPlus.Origins.BanditOriginBehavior.GoBetweenName(cid)
                        + " — the " + clan + " go-between");
                    args.optionLeaveType = GameMenuOption.LeaveType.Conversation;
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        private void OnGoBetweenConsequence(MenuCallbackArgs args)
        {
            try
            {
                var origins = BandItPlus.Origins.BanditOriginBehavior.Instance;
                string cid = _gateTownMatchCid;
                if (origins == null || string.IsNullOrEmpty(cid)) return;
                string name = BandItPlus.Origins.BanditOriginBehavior.GoBetweenName(cid);
                string clan = BandItPlus.Origins.BanditOriginBehavior.CultureDisplay(cid);

                string body =
                    "You find " + name + " in a quiet corner, already watching you.\n\n"
                    + "\"So you're the one the road talks about. The " + clan
                    + " don't open their camps to strangers — but they'll open them to a name they trust.\n\n"
                    + "I'll send word ahead. Walk in under my name, and keep it clean — what you do in there lands on me.\"";

                System.Action accept = () =>
                {
                    try
                    {
                        origins.GrantCampAccess(cid);
                        // Wave 4.23.1: flag the chief's hideout on the map so the
                        // player can ride straight to the camp.
                        origins.MarkChiefHideout(cid);
                        // 2026-07-02 (pledge-flow parity): NAME the camp in the toast —
                        // resolved through the hideout-preferring seat fix.
                        string campName = null;
                        try { campName = origins.GetChiefCampName(cid); } catch { }
                        BandItPlus.UI.BanditToastScreen.Show(
                            "Camp Access — " + char.ToUpper(clan[0]) + clan.Substring(1),
                            name + " has vouched for you. The " + clan
                            + " camps will let you through — "
                            + (string.IsNullOrEmpty(campName)
                                ? "their camp is marked on your map."
                                : "their camp at " + campName + " is marked on your map."),
                            "event:/ui/notification/peace");
                    }
                    catch { }
                };

                // Wave 4.23.0: BP's own contact panel; vanilla inquiry only if
                // the custom screen fails to mount.
                bool opened = BandItPlus.UI.BanditContactScreen.Open(
                    name, body, Localization.Get("bp_dialogmanager_135", "Their word is sent"), Localization.Get("bp_dialogmanager_136", "Step away"), accept);
                if (!opened)
                {
                    InformationManager.ShowInquiry(new InquiryData(
                        name, body, true, false, Localization.Get("bp_dialogmanager_137", "Their word is sent"), null,
                        () => accept(), null), true);
                }
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BDM] OnGoBetweenConsequence threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private bool IsHideoutPeacefulVisible(MenuCallbackArgs args)
        {
            try
            {
                if (MCMSettings.Instance == null || !MCMSettings.Instance.EnableMod) return false;

                var hideout = GetCurrentHideout();
                if (!IsBanditHideout(hideout)) return false;
                string cid = GetCultureIdForHideout(hideout);

                // Wave 4.21.0 — the Go-Between gate: with an origin chosen, the
                // camp only admits the player once vouched for (per culture).
                // Outlaw Blood and earned peace bypass inside HasCampAccess.
                var origins = BandItPlus.Origins.BanditOriginBehavior.Instance;
                if (origins != null && origins.OriginChosen)
                {
                    if (!string.IsNullOrEmpty(cid) && !origins.HasCampAccess(cid))
                    {
                        var town = origins.GetGateTown(cid);
                        args.IsEnabled = false;
                        var vouchT = new TextObject(town != null
                            ? "{=bp_hideout_vouch_town}They won't let a stranger near. Find {GOBETWEEN} in {TOWN} — they can vouch for you."
                            : "{=bp_hideout_vouch}They won't let a stranger near. Find {GOBETWEEN} — they can vouch for you.");
                        vouchT.SetTextVariable("GOBETWEEN", BandItPlus.Origins.BanditOriginBehavior.GoBetweenName(cid));
                        if (town != null) vouchT.SetTextVariable("TOWN", town.Name);
                        args.Tooltip = vouchT;
                        args.optionLeaveType = GameMenuOption.LeaveType.Mission;
                        return true; // visible but locked, with directions
                    }
                }
                else if (!MCMSettings.Instance.PeacefulEncounters) return false;

                // Bug B fix (keep OutlawBlood/vouched perk): on the entry path, if the
                // player is genuinely at war with this hideout's bandit faction AND has
                // no camp-access standing, lock it — make peace (or assault) first.
                // FactionManager.IsAtWarAgainstFaction is peace-patched (PeacefulBanditPatch),
                // so it reads false for earned peace / pact / peaceful-mode; HasCampAccess
                // covers Outlaw Blood + go-between-vouched, who keep their walk-in-at-war perk.
                var pf = Hero.MainHero?.MapFaction;
                var bf = hideout != null ? hideout.MapFaction : null;
                bool hasAccess = origins != null && origins.HasCampAccess(cid);
                if (pf != null && bf != null && !hasAccess
                    && FactionManager.IsAtWarAgainstFaction(pf, bf))
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("{=bp_hideoutatwar_001}You are at war with them — make peace before you can walk into their camp.");
                    args.optionLeaveType = GameMenuOption.LeaveType.Mission;
                    return true; // visible but locked
                }

                args.optionLeaveType = GameMenuOption.LeaveType.Mission;
                return true;
            }
            catch { return false; }
        }

        private bool IsHideoutRecruitVisible(MenuCallbackArgs args)
        {
            try
            {
                if (!IsHideoutPeacefulVisible(args)) return false;
                ApplyTrustGate(args, 2);
                if (!args.IsEnabled) return true;

                var hideout = GetCurrentHideout();
                var party = GetHideoutParty(hideout);
                if (party == null || party.MemberRoster == null || party.MemberRoster.Count == 0)
                    return false;

                if (_hideoutRecruitCooldown.TryGetValue(hideout.StringId, out var nextOk)
                    && nextOk.IsFuture)
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("{=bp_dialogmanager_138}They turned you away last time. Come back in a few days.");
                    return true;
                }

                if (Hero.MainHero != null && Hero.MainHero.Gold < 50)
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("{=bp_dialogmanager_139}You need at least 50 denars.");
                }

                args.optionLeaveType = GameMenuOption.LeaveType.Recruit;
                return true;
            }
            catch { return false; }
        }

        private void OnHideoutTalkConsequence(MenuCallbackArgs args)
        {
            try
            {
                var hideout = GetCurrentHideout();

                // Wave 4.5: chief refuses to receive strangers. Player must walk into the
                // camp at least once before he'll grant an audience.
                if (!HasVisitedHideout(hideout))
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        Localization.Get("bp_dialogmanager_140", "[BandIt Plus] The chief refuses to see a stranger. \"Walk into our camp first, traveler. Let us see you with our own eyes. Then — perhaps — we'll talk.\""),
                        Colors.Yellow));
                    return;
                }

                var party = GetHideoutParty(hideout);
                var leader = party?.LeaderHero ?? party?.Party?.LeaderHero;
                CharacterObject speaker = null;

                if (leader != null) speaker = leader.CharacterObject;
                else if (party?.MemberRoster != null && party.MemberRoster.Count > 0)
                    speaker = party.MemberRoster.GetCharacterAtIndex(0);

                if (speaker == null)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        Localization.Get("bp_dialogmanager_141", "[BandIt Plus] Nobody to talk to here."), Colors.Yellow));
                    return;
                }

                CampaignMapConversation.OpenConversation(
                    new ConversationCharacterData(Hero.MainHero.CharacterObject),
                    new ConversationCharacterData(speaker, party?.Party));
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Hideout talk error: " + ex.Message, Colors.Red));
            }
        }

        private void OnHideoutTradeConsequence(MenuCallbackArgs args)
        {
            try
            {
                var hideout = GetCurrentHideout();
                var party = GetHideoutParty(hideout);
                if (hideout == null)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        Localization.Get("bp_dialogmanager_142", "[BandIt Plus] No hideout found."), Colors.Red));
                    return;
                }

                var sc = hideout.SettlementComponent;
                if (sc == null)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        Localization.Get("bp_dialogmanager_143", "[BandIt Plus] Hideout has no settlement component."), Colors.Red));
                    return;
                }

                // Trade against the bandit party's actual loot. If empty, seed with a small bundle
                // so the screen isn't barren (bandits "find something" to barter).
                var roster = party?.ItemRoster;
                if (roster == null || roster.Count == 0)
                {
                    roster = BuildTradeFallbackRoster();
                }

                Helpers.InventoryScreenHelper.OpenScreenAsTrade(
                    roster,
                    sc,
                    Helpers.InventoryScreenHelper.InventoryCategoryType.None,
                    null);
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Hideout trade error: " + ex.Message, Colors.Red));
            }
        }

        // Small barter bundle: cheap loot a bandit camp would plausibly have.
        private static ItemRoster BuildTradeFallbackRoster()
        {
            var roster = new ItemRoster();
            try
            {
                int added = 0;
                foreach (var item in Items.All)
                {
                    if (item == null) continue;
                    if (item.NotMerchandise || item.IsCraftedByPlayer) continue;
                    if (item.Value <= 0 || item.Value > 600) continue;

                    bool tradeGood = item.ItemCategory != null
                        && (item.ItemType == ItemObject.ItemTypeEnum.Goods
                            || item.ItemType == ItemObject.ItemTypeEnum.Animal
                            || item.ItemType == ItemObject.ItemTypeEnum.Horse);
                    bool cheapGear = (item.ItemType == ItemObject.ItemTypeEnum.OneHandedWeapon
                        || item.ItemType == ItemObject.ItemTypeEnum.TwoHandedWeapon
                        || item.ItemType == ItemObject.ItemTypeEnum.Polearm
                        || item.ItemType == ItemObject.ItemTypeEnum.Bow
                        || item.ItemType == ItemObject.ItemTypeEnum.Arrows
                        || item.ItemType == ItemObject.ItemTypeEnum.Bolts
                        || item.ItemType == ItemObject.ItemTypeEnum.HeadArmor
                        || item.ItemType == ItemObject.ItemTypeEnum.BodyArmor)
                        && item.Tier <= ItemObject.ItemTiers.Tier2;

                    if (!tradeGood && !cheapGear) continue;
                    roster.AddToCounts(item, 1);
                    if (++added >= 12) break;
                }
            }
            catch (Exception ex) {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("BuildTradeFallbackRoster fail: " + ex.GetType().Name + ": " + ex.Message);
            }
            return roster;
        }

        private void OnHideoutRecruitConsequence(MenuCallbackArgs args)
        {
            try
            {
                var hideout = GetCurrentHideout();
                var party = GetHideoutParty(hideout);
                if (hideout == null || party?.MemberRoster == null || party.MemberRoster.Count == 0)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        Localization.Get("bp_dialogmanager_144", "[BandIt Plus] No one is willing to join you."), Colors.Yellow));
                    return;
                }

                int totalCost = 0;
                int recruited = 0;
                int maxToRecruit = 3;
                var rosterCount = party.MemberRoster.Count;
                var taken = new List<CharacterObject>();

                for (int i = 0; i < rosterCount && recruited < maxToRecruit; i++)
                {
                    var ch = party.MemberRoster.GetCharacterAtIndex(i);
                    var available = party.MemberRoster.GetElementCopyAtIndex(i).Number;
                    if (ch == null || ch.IsHero || available <= 0) continue;

                    int costPer = 50 * Math.Max(1, ch.Tier);
                    if (Hero.MainHero == null || Hero.MainHero.Gold < totalCost + costPer) break;

                    totalCost += costPer;
                    taken.Add(ch);
                    recruited++;
                }

                if (recruited == 0)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        Localization.Get("bp_dialogmanager_145", "[BandIt Plus] Not enough gold or no eligible troops."), Colors.Yellow));
                    return;
                }

                foreach (var ch in taken)
                {
                    party.MemberRoster.AddToCounts(ch, -1);
                    PartyBase.MainParty.MemberRoster.AddToCounts(ch, 1);
                }

                GiveGoldAction.ApplyBetweenCharacters(Hero.MainHero, null, totalCost, true);
                _hideoutRecruitCooldown[hideout.StringId] = CampaignTime.DaysFromNow(7);

                // Build a grouped roster summary.
                var grouped = new Dictionary<string, int>();
                foreach (var ch in taken)
                {
                    string n = ch.Name?.ToString() ?? Localization.Get("bp_dialogmanager_146", "fighter");
                    if (grouped.ContainsKey(n)) grouped[n]++; else grouped[n] = 1;
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine(Localization.Get("bp_dialogmanager_147", "Coin changes hands at the gate. The chief nods at the men he's letting walk with you."));
                sb.AppendLine();
                sb.AppendLine("─────────────────────────────────");
                sb.AppendLine();
                sb.AppendLine(Localization.Get("bp_dialogmanager_148", "NEW TO YOUR BANNER"));
                foreach (var kv in grouped)
                    sb.AppendLine("    ▸  " + kv.Value + " ×  " + kv.Key);
                sb.AppendLine();
                sb.AppendLine("─────────────────────────────────");
                sb.AppendLine();
                sb.AppendLine("◆  Paid: " + totalCost + " denars");
                sb.AppendLine(Localization.Get("bp_dialogmanager_149", "◆  The camp will not sell again for seven days."));

                ShowCampInquiry(Localization.Get("bp_dialogmanager_150", "Bought Their Loyalty"), sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Hideout recruit error: " + ex.Message, Colors.Red));
            }
        }

        // === Wave 3c.3: Visit camp + new D-options ===
        private void OnVisitCampConsequence(MenuCallbackArgs args)
        {
            try { GameMenu.SwitchToMenu("bp_hideout_camp"); }
            catch (Exception ex) { InformationManager.DisplayMessage(new InformationMessage(
                "[BandIt Plus] Visit camp error: " + ex.Message, Colors.Red)); }
        }

        // === Wave 3c.4 Phase 6 (research-driven, task 5 of 10) — Walk into camp via vanilla flow ===
        // Sets the peaceful flag + TargetHideout, then triggers vanilla PlayerEncounter.EnterSettlement().
        // Vanilla then calls CreateLocationEncounter(settlement) — our Harmony postfix detects the
        // peaceful flag + hideout and swaps in WalkableHideoutEncounter, which routes through
        // CampaignMission.OpenVillageMission (the tested 27-behavior village mission stack).
        private void OnWalkInCampConsequence(MenuCallbackArgs args)
        {
            try
            {
                var hideout = GetCurrentHideout();
                if (hideout == null)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        Localization.Get("bp_dialogmanager_151", "[BandIt Plus] No hideout in current encounter."), Colors.Red));
                    return;
                }

                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Active = true;
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.TargetHideout = hideout;
                // Wave 4.5: mark the hideout as personally visited — the chief now "knows" the
                // player and the Talk-to-chief option will open dialog instead of refusing.
                MarkHideoutVisited(hideout);
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "OnWalkInCampConsequence: peaceful flag SET for hideout=" + hideout.StringId +
                    ", calling PlayerEncounter.EnterSettlement()");

                // Wave 5 / T27 — walkable peaceful path does NOT fire CampaignEvents.SettlementEntered,
                // so raise our own event for BanditAllianceBehavior to detect Ch3 hideout entry.
                // Per spec §4 Ch3 trigger B; raise barrier swallows subscriber throws.
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.RaiseWalkableActivated(hideout);

                PlayerEncounter.EnterSettlement();
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "OnWalkInCampConsequence: PlayerEncounter.EnterSettlement() returned");

                // Vanilla EnterSettlement sets up the encounter but doesn't auto-trigger the mission;
                // for towns/villages a subsequent menu click does that. Hideouts have no such menu,
                // so we invoke CreateAndOpenMissionController directly with the hideout_center Location.
                var location = hideout.LocationComplex?.GetLocationWithId("hideout_center");
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "OnWalkInCampConsequence: hideout_center location = " +
                    (location == null ? "NULL" : location.StringId));

                if (location != null && PlayerEncounter.LocationEncounter != null)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "OnWalkInCampConsequence: calling LocationEncounter.CreateAndOpenMissionController directly");
                    PlayerEncounter.LocationEncounter.CreateAndOpenMissionController(location);
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "OnWalkInCampConsequence: CreateAndOpenMissionController returned");
                }
                else
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "OnWalkInCampConsequence: BAIL — location null OR LocationEncounter null");
                }
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "OnWalkInCampConsequence EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Walk-in error: " + ex.Message, Colors.Red));
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Reset();
            }
        }

        // Sets per-culture intro text and culture-service labels every time the camp menu opens.
        // Wave 4.4: prepend a culture-flavored title line ("Frost Reaver Camp — Vargolf the
        // Iron-eyed") plus a time-of-day mood sentence so the body text feels alive instead
        // of a static block. Both prepend cleanly to existing CampIntro + ChiefGreeting.
        private void OnCampMenuInit(MenuCallbackArgs args)
        {
            try
            {
                var hideout = GetCurrentHideout();
                var profile = GetProfileForHideout(hideout);
                if (profile != null)
                {
                    string cultureId = hideout?.Culture?.StringId ?? hideout?.OwnerClan?.Culture?.StringId;
                    string title = BuildCampTitleLine(profile, cultureId);
                    string timeFlavor = BuildTimeOfDayFlavor();
                    var intro = title + "\n" + timeFlavor + "\n\n"
                              + profile.CampIntro + "\n\n"
                              + profile.ChiefGreeting;
                    MBTextManager.SetTextVariable("CAMP_INTRO_TEXT", intro);
                    MBTextManager.SetTextVariable("CULTURE_SERVICE_LABEL", profile.ServiceLabel);
                    SetExtraLabel(profile, 0, "CULTURE_EXTRA1_LABEL");
                    SetExtraLabel(profile, 1, "CULTURE_EXTRA2_LABEL");
                    SetExtraLabel(profile, 2, "CULTURE_EXTRA3_LABEL");
                }
                else
                {
                    string timeFlavor = BuildTimeOfDayFlavor();
                    MBTextManager.SetTextVariable("CAMP_INTRO_TEXT",
                        "Bandit Camp\n" + timeFlavor + "\n\n" +
                        "The camp sprawls out before you. Bandits sharpen blades. A grizzled chief watches you from a fur-draped stool.");
                    MBTextManager.SetTextVariable("CULTURE_SERVICE_LABEL", "{=bp_dialogmanager_152}Look around the camp.");
                    MBTextManager.SetTextVariable("CULTURE_EXTRA1_LABEL", "");
                    MBTextManager.SetTextVariable("CULTURE_EXTRA2_LABEL", "");
                    MBTextManager.SetTextVariable("CULTURE_EXTRA3_LABEL", "");
                }
            }
            catch (Exception ex) {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("OnCampMenuInit fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Builds the menu title line. Derives a "X Camp" name from the culture id (e.g.
        // "bp_frost_reavers" → "Frost Reaver Camp") and appends the chief name when present.
        private static string BuildCampTitleLine(CultureProfile profile, string cultureId)
        {
            try
            {
                string campName = new TextObject("{=bp_camptitle_001}{CULTURE} Camp").SetTextVariable("CULTURE", HumanizeCultureName(cultureId)).ToString();
                if (profile != null && !string.IsNullOrEmpty(profile.ChiefName))
                    return campName + " — " + profile.ChiefName;
                return campName;
            }
            catch
            {
                return Localization.Get("bp_dialogmanager_153", "Bandit Camp");
            }
        }

        // "bp_frost_reavers" → "Frost Reaver", "looters" → "Looter", etc. Strips the bp_ prefix
        // and any plural -s, then title-cases the underscore-separated words.
        private static string HumanizeCultureName(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return "Bandit";
            string id = cultureId;
            if (id.StartsWith("bp_")) id = id.Substring(3);
            var parts = id.Split('_');
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (string.IsNullOrEmpty(p)) continue;
                // Drop trailing 's' to singularize ("reavers" → "reaver", "stalkers" → "stalker").
                if (i == parts.Length - 1 && p.Length > 2 && p.EndsWith("s") && !p.EndsWith("ss"))
                    p = p.Substring(0, p.Length - 1);
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToUpperInvariant(p[0]));
                if (p.Length > 1) sb.Append(p.Substring(1));
            }
            return sb.Length > 0 ? sb.ToString() : "Bandit";
        }

        // Time-of-day mood line based on CampaignTime's current hour. Four windows:
        // dawn 5-8, day 8-18, dusk 18-21, night 21-5.
        private static string BuildTimeOfDayFlavor()
        {
            try
            {
                float hour = CampaignTime.Now.CurrentHourInDay;
                if (hour >= 5f && hour < 8f)
                    return Localization.Get("bp_dialogmanager_154", "Dawn light slants through the trees. Smoke from last night's fires still drifts low between the tents.");
                if (hour >= 8f && hour < 18f)
                    return Localization.Get("bp_dialogmanager_155", "Midday sun finds the camp at work — blades being sharpened, horses rubbed down, voices carrying across the clearing.");
                if (hour >= 18f && hour < 21f)
                    return Localization.Get("bp_dialogmanager_156", "The sun is sinking. Cookfires are being lit. A wineskin passes from one shadowed face to the next.");
                return Localization.Get("bp_dialogmanager_157", "Deep night. Only the watchfires burn. Most of the camp sleeps; the few still awake speak in low voices.");
            }
            catch
            {
                return "";
            }
        }

        private static void SetExtraLabel(CultureProfile profile, int idx, string varName)
        {
            string label = "";
            if (profile?.ExtraServices != null && idx < profile.ExtraServices.Length
                && profile.ExtraServices[idx] != null)
            {
                label = profile.ExtraServices[idx].Label ?? "";
            }
            MBTextManager.SetTextVariable(varName, label);
        }

        // -- Extra services (sub-project 3) --
        private bool IsCultureExtraVisible(MenuCallbackArgs args, int idx)
        {
            try
            {
                if (!IsHideoutPeacefulVisible(args)) return false;
                var profile = GetProfileForHideout(GetCurrentHideout());
                if (profile?.ExtraServices == null || idx >= profile.ExtraServices.Length
                    || profile.ExtraServices[idx] == null)
                    return false;
                args.optionLeaveType = GameMenuOption.LeaveType.Trade;
                ApplyTrustGate(args, 2);
                return true;
            }
            catch { return false; }
        }

        private void OnCultureExtraConsequence(MenuCallbackArgs args, int idx)
        {
            try
            {
                var hideout = GetCurrentHideout();
                var profile = GetProfileForHideout(hideout);
                if (profile?.ExtraServices == null || idx >= profile.ExtraServices.Length
                    || profile.ExtraServices[idx] == null)
                {
                    GameMenu.SwitchToMenu("bp_hideout_camp");
                    return;
                }

                var svc = profile.ExtraServices[idx];
                InformationManager.DisplayMessage(new InformationMessage(svc.Flavor ?? "", Colors.Yellow));

                switch (svc.Kind)
                {
                    case ServiceKind.TrophyRoom:
                    case ServiceKind.PoisonBrewer:
                    case ServiceKind.SlaveMarket:
                    case ServiceKind.HorseMaster:
                    case ServiceKind.Hunter:
                    case ServiceKind.SteppeTrader:
                    case ServiceKind.CoastalSmuggler:
                        DoCultureTradeScreen(hideout, svc.Kind);
                        break;
                    case ServiceKind.InfoBroker:
                        DoRevealHideouts(hideout, 1, svc.GoldCost > 0 ? svc.GoldCost : 100);
                        break;
                    case ServiceKind.Falconer:
                        DoRevealHideouts(hideout, 5, svc.GoldCost);
                        break;
                    case ServiceKind.SandScout:
                        DoRevealHideouts(hideout, 3, svc.GoldCost);
                        break;
                    case ServiceKind.Oracle:
                        DoOracleOrBeggar(profile.OracleLines != null && profile.OracleLines.Length > 0
                            ? profile.OracleLines : profile.Rumors, svc.GoldCost);
                        GameMenu.SwitchToMenu("bp_hideout_camp");
                        break;
                    case ServiceKind.BeggarEar:
                        DoOracleOrBeggar(profile.Rumors, svc.GoldCost);
                        GameMenu.SwitchToMenu("bp_hideout_camp");
                        break;
                    case ServiceKind.VeteranHall:
                    case ServiceKind.TollMaster:
                        DoCostlyMoraleAction(svc.GoldCost, svc.MoraleGain > 0 ? svc.MoraleGain : 3,
                            svc.Flavor ?? Localization.Get("bp_dialogmanager_158", "Done."));
                        GameMenu.SwitchToMenu("bp_hideout_camp");
                        break;
                }
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Extra service error: " + ex.Message, Colors.Red));
            }
        }

        private static CultureProfile GetProfileForHideout(Settlement hideout)
        {
            var cid = hideout?.Culture?.StringId;
            if (string.IsNullOrEmpty(cid)) return null;
            CultureProfileRegistry.ByCultureId.TryGetValue(cid, out var p);
            return p;
        }

        // -- Culture-specific service (dispatch by ServiceKind) --
        private bool IsCultureServiceVisible(MenuCallbackArgs args)
        {
            try
            {
                if (!IsHideoutPeacefulVisible(args)) return false;
                var profile = GetProfileForHideout(GetCurrentHideout());
                if (profile == null)
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("{=bp_dialogmanager_159}These bandits offer no special service.");
                    return false; // hide entirely if no profile
                }
                args.optionLeaveType = GameMenuOption.LeaveType.Trade;
                ApplyTrustGate(args, 2);
                return true;
            }
            catch { return false; }
        }

        private void OnCultureServiceConsequence(MenuCallbackArgs args)
        {
            try
            {
                var hideout = GetCurrentHideout();
                var profile = GetProfileForHideout(hideout);
                if (profile == null || hideout == null)
                {
                    GameMenu.SwitchToMenu("bp_hideout_camp");
                    return;
                }

                InformationManager.DisplayMessage(new InformationMessage(
                    profile.ServiceFlavor, Colors.Yellow));

                switch (profile.Kind)
                {
                    case ServiceKind.TrophyRoom:
                    case ServiceKind.PoisonBrewer:
                    case ServiceKind.SlaveMarket:
                    case ServiceKind.HorseMaster:
                    case ServiceKind.Hunter:
                    case ServiceKind.SteppeTrader:
                    case ServiceKind.CoastalSmuggler:
                        DoCultureTradeScreen(hideout, profile.Kind);
                        break;

                    case ServiceKind.InfoBroker:
                        DoRevealHideouts(hideout, 2, 200);
                        break;
                    case ServiceKind.Falconer:
                        DoRevealHideouts(hideout, 5, 0);
                        break;
                    case ServiceKind.SandScout:
                        DoRevealHideouts(hideout, 3, 0);
                        break;

                    case ServiceKind.Oracle:
                        DoOracleOrBeggar(profile.OracleLines, 0);
                        GameMenu.SwitchToMenu("bp_hideout_camp");
                        break;
                    case ServiceKind.BeggarEar:
                        DoOracleOrBeggar(profile.Rumors, 5);
                        GameMenu.SwitchToMenu("bp_hideout_camp");
                        break;

                    case ServiceKind.VeteranHall:
                        DoCostlyMoraleAction(1000, 5, Localization.Get("bp_dialogmanager_160", "Aurelius drills your soldiers hard. Bruises and shouted Latin. Your party is grimmer, sharper."));
                        GameMenu.SwitchToMenu("bp_hideout_camp");
                        break;
                    case ServiceKind.TollMaster:
                        DoCostlyMoraleAction(500, 3, Localization.Get("bp_dialogmanager_161", "Skarvac's mark hangs from your saddle. The mountain bandits will not trouble you for a moon."));
                        GameMenu.SwitchToMenu("bp_hideout_camp");
                        break;
                }
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Culture service error: " + ex.Message, Colors.Red));
            }
        }

        // -- Generic dispatcher: trade screen with culture-filtered roster --
        private void DoCultureTradeScreen(Settlement hideout, ServiceKind kind)
        {
            try
            {
                var sc = hideout?.SettlementComponent;
                if (sc == null) return;
                var roster = BuildCultureFilteredRoster(kind);
                Helpers.InventoryScreenHelper.OpenScreenAsTrade(
                    roster, sc, Helpers.InventoryScreenHelper.InventoryCategoryType.None, null);
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Trade screen error: " + ex.Message, Colors.Red));
            }
        }

        private static ItemRoster BuildCultureFilteredRoster(ServiceKind kind)
        {
            var roster = new ItemRoster();
            int added = 0;
            foreach (var item in Items.All)
            {
                if (item == null || item.NotMerchandise || item.IsCraftedByPlayer) continue;
                if (item.Value <= 0 || item.Value > 50000) continue;

                bool match = false;
                switch (kind)
                {
                    case ServiceKind.TrophyRoom:
                        // Cliff-forge / trophy room: full armory (all armor pieces + all weapons + shields)
                        // tier 3+ so the screen has stock even at fresh games.
                        match = item.Tier >= ItemObject.ItemTiers.Tier3 &&
                                (item.ItemType == ItemObject.ItemTypeEnum.HeadArmor
                                || item.ItemType == ItemObject.ItemTypeEnum.BodyArmor
                                || item.ItemType == ItemObject.ItemTypeEnum.LegArmor
                                || item.ItemType == ItemObject.ItemTypeEnum.HandArmor      // gloves
                                || item.ItemType == ItemObject.ItemTypeEnum.Cape           // cloaks/capes
                                || item.ItemType == ItemObject.ItemTypeEnum.Shield
                                || item.ItemType == ItemObject.ItemTypeEnum.OneHandedWeapon
                                || item.ItemType == ItemObject.ItemTypeEnum.TwoHandedWeapon
                                || item.ItemType == ItemObject.ItemTypeEnum.Polearm
                                || item.ItemType == ItemObject.ItemTypeEnum.Bow
                                || item.ItemType == ItemObject.ItemTypeEnum.Crossbow
                                || item.ItemType == ItemObject.ItemTypeEnum.Arrows
                                || item.ItemType == ItemObject.ItemTypeEnum.Bolts
                                || item.ItemType == ItemObject.ItemTypeEnum.Thrown
                                || item.ItemType == ItemObject.ItemTypeEnum.HorseHarness);
                        break;
                    case ServiceKind.PoisonBrewer:
                        match = item.ItemType == ItemObject.ItemTypeEnum.Arrows
                             || item.ItemType == ItemObject.ItemTypeEnum.Bolts
                             || item.ItemType == ItemObject.ItemTypeEnum.Thrown
                             || item.ItemType == ItemObject.ItemTypeEnum.Bow;
                        break;
                    case ServiceKind.SlaveMarket:
                        match = item.Tier <= ItemObject.ItemTiers.Tier2 &&
                                (item.ItemType == ItemObject.ItemTypeEnum.OneHandedWeapon
                                || item.ItemType == ItemObject.ItemTypeEnum.Polearm
                                || item.ItemType == ItemObject.ItemTypeEnum.HeadArmor);
                        break;
                    case ServiceKind.HorseMaster:
                    case ServiceKind.SteppeTrader:
                        match = item.ItemType == ItemObject.ItemTypeEnum.Horse;
                        break;
                    case ServiceKind.Hunter:
                        match = item.ItemType == ItemObject.ItemTypeEnum.Goods
                             || item.ItemType == ItemObject.ItemTypeEnum.Animal
                             || item.ItemType == ItemObject.ItemTypeEnum.Bow;
                        break;
                    case ServiceKind.CoastalSmuggler:
                        match = item.Value >= 100 && item.Value <= 8000 &&
                                (item.ItemType == ItemObject.ItemTypeEnum.Goods
                                || item.ItemType == ItemObject.ItemTypeEnum.OneHandedWeapon
                                || item.ItemType == ItemObject.ItemTypeEnum.BodyArmor);
                        break;
                }
                if (!match) continue;
                roster.AddToCounts(item, 1);
                // TrophyRoom (cliff-forge / trophy room) gets a bigger stock so it feels like a real armory.
                int cap = (kind == ServiceKind.TrophyRoom) ? 40 : 20;
                if (++added >= cap) break;
            }
            return roster;
        }

        // -- Reveal N nearest unseen hideouts of same culture --
        // v64-fix3 (2026-05-08): culture-service helpers now use the standardized
        // popup helper instead of dumping to the feed. Insufficient-gold cases also
        // popup so the player can't miss the failure.
        private void DoRevealHideouts(Settlement hideout, int count, int cost)
        {
            try
            {
                if (cost > 0)
                {
                    if (Hero.MainHero == null || Hero.MainHero.Gold < cost)
                    {
                        ShowCampInquiry(Localization.Get("bp_dialogmanager_162", "Map Service"),
                            "Not enough coin.\n\n─────────────────────────────────\n\n◆  Required: " + cost + " denars\n◆  Available: " + (Hero.MainHero?.Gold ?? 0));
                        return;
                    }
                    GiveGoldAction.ApplyBetweenCharacters(Hero.MainHero, null, cost, true);
                }

                var here = MobileParty.MainParty?.GetPosition2D ?? hideout.GetPosition2D;
                var candidates = new List<Settlement>();
                foreach (var s in Settlement.All)
                {
                    if (s == null || !s.IsHideout || s == hideout || s.IsVisible) continue;
                    if (s.Culture?.StringId != hideout.Culture?.StringId) continue;
                    candidates.Add(s);
                }
                candidates.Sort((a, b) => a.GetPosition2D.DistanceSquared(here).CompareTo(b.GetPosition2D.DistanceSquared(here)));

                var revealedNames = new List<string>();
                foreach (var s in candidates)
                {
                    s.IsVisible = true;
                    revealedNames.Add(s.Name?.ToString() ?? Localization.Get("bp_dialogmanager_163", "an unnamed hideout"));
                    if (revealedNames.Count >= count) break;
                }

                var sb = new System.Text.StringBuilder();
                if (revealedNames.Count == 0)
                {
                    sb.AppendLine(Localization.Get("bp_dialogmanager_164", "They scratch their beards."));
                    sb.AppendLine();
                    sb.AppendLine(Localization.Get("bp_dialogmanager_165", "\"No more hideouts of our blood that you don't already know about. Walk easier for it.\""));
                }
                else
                {
                    sb.AppendLine(Localization.Get("bp_dialogmanager_166", "They mark your map with the points of their knives."));
                    sb.AppendLine();
                    sb.AppendLine("─────────────────────────────────");
                    sb.AppendLine();
                    sb.AppendLine(Localization.Get("bp_dialogmanager_167", "REVEALED ON YOUR MAP"));
                    foreach (var name in revealedNames)
                        sb.AppendLine("    ▸  " + name);
                    if (cost > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("◆  Paid: " + cost + " denars");
                    }
                }
                ShowCampInquiry(Localization.Get("bp_dialogmanager_168", "Map Service"), sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "DoRevealHideouts EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Reveal error: " + ex.Message, Colors.Red));
            }
        }

        // -- Oracle / Beggar: random message from pool --
        private void DoOracleOrBeggar(string[] pool, int cost)
        {
            try
            {
                if (cost > 0)
                {
                    if (Hero.MainHero == null || Hero.MainHero.Gold < cost)
                    {
                        ShowCampInquiry(Localization.Get("bp_dialogmanager_169", "They Won't Speak Without Coin"),
                            "Not enough denars in your purse.\n\n─────────────────────────────────\n\n◆  Required: " + cost + " denars\n◆  Available: " + (Hero.MainHero?.Gold ?? 0));
                        return;
                    }
                    GiveGoldAction.ApplyBetweenCharacters(Hero.MainHero, null, cost, true);
                }
                if (pool == null || pool.Length == 0)
                {
                    ShowCampInquiry(Localization.Get("bp_dialogmanager_170", "Silence"), Localization.Get("bp_dialogmanager_171", "They have nothing to say."));
                    return;
                }
                var line = pool[MBRandom.RandomInt(pool.Length)];
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(line);
                if (cost > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("─────────────────────────────────");
                    sb.AppendLine();
                    sb.AppendLine("◆  Paid: " + cost + " denars");
                }
                ShowCampInquiry(Localization.Get("bp_dialogmanager_172", "They Speak"), sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                // v64-fix3: was empty catch — added log so silent failures are at least recorded.
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "DoOracleOrBeggar EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // -- Costly action that grants morale (Veteran Hall, Toll Master) --
        private void DoCostlyMoraleAction(int cost, float morale, string flavor)
        {
            try
            {
                if (Hero.MainHero == null || Hero.MainHero.Gold < cost)
                {
                    ShowCampInquiry(Localization.Get("bp_dialogmanager_173", "Insufficient Coin"),
                        "You don't have the denars they want.\n\n─────────────────────────────────\n\n◆  Required: " + cost + " denars\n◆  Available: " + (Hero.MainHero?.Gold ?? 0));
                    return;
                }
                GiveGoldAction.ApplyBetweenCharacters(Hero.MainHero, null, cost, true);
                if (MobileParty.MainParty != null) MobileParty.MainParty.RecentEventsMorale += morale;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(flavor);
                sb.AppendLine();
                sb.AppendLine("─────────────────────────────────");
                sb.AppendLine();
                sb.AppendLine("◆  Paid: " + cost + " denars");
                sb.AppendLine("◆  Party morale: +" + (int)morale);
                ShowCampInquiry(Localization.Get("bp_dialogmanager_174", "The Camp's Service"), sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                // v64-fix3: was empty catch — log so silent failures are recorded.
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "DoCostlyMoraleAction EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Daily-cooldown maps for camp activities (per hideout, non-persistent — fine for short visits).
        private readonly Dictionary<string, CampaignTime> _campDrinkCooldown = new Dictionary<string, CampaignTime>();
        private readonly Dictionary<string, CampaignTime> _campRestCooldown = new Dictionary<string, CampaignTime>();
        private readonly Dictionary<string, CampaignTime> _campEavesdropCooldown = new Dictionary<string, CampaignTime>();
        private readonly Dictionary<string, CampaignTime> _campMapCooldown = new Dictionary<string, CampaignTime>();
        // v64 (2026-05-08): two new gate-tier perimeter actions.
        private readonly Dictionary<string, CampaignTime> _campScoutCooldown = new Dictionary<string, CampaignTime>();
        private readonly Dictionary<string, CampaignTime> _campWatchtowerCooldown = new Dictionary<string, CampaignTime>();

        // v64 (2026-05-08): popup helper for camp-menu actions. Replaces feed-spam
        // DisplayMessage calls with a proper Gauntlet inquiry window. By default the
        // affirmative callback re-opens bp_hideout_camp so the player stays in the
        // camp menu loop. Pass returnToCampMenu=false for actions that already leave
        // the encounter (bribe accept/refuse) — the LeaveEncounter flag will resolve
        // after the inquiry is dismissed.
        private static void ShowCampInquiry(string title, string body, bool returnToCampMenu = true)
        {
            try
            {
                var inquiry = new InquiryData(
                    title,
                    body,
                    true,   // affirmative shown
                    false,  // negative hidden
                    Localization.Get("bp_dialogmanager_175", "Done"),
                    null,
                    () => { if (returnToCampMenu) GameMenu.SwitchToMenu("bp_hideout_camp"); },
                    null);
                InformationManager.ShowInquiry(inquiry, true);
            }
            catch (Exception ex) { InformationManager.DisplayMessage(new InformationMessage(
                "[BandIt Plus] ShowCampInquiry error: " + ex.Message, Colors.Red)); }
        }

        private static bool IsOnCooldown(Dictionary<string, CampaignTime> map, string key)
        {
            return map.TryGetValue(key, out var t) && t.IsFuture;
        }

        // -- Bribe (mirrors dialog version) --
        private static int CurrentCampBribeAmount()
        {
            var profile = GetProfileForHideout(GetCurrentHideoutStatic());
            return profile != null ? profile.BribeAmount : 200;
        }

        private static Settlement GetCurrentHideoutStatic()
        {
            try
            {
                var s = Settlement.CurrentSettlement ?? PlayerEncounter.EncounterSettlement;
                if (s != null && s.IsHideout) return s;
            } catch { }
            return null;
        }

        private bool IsCampBribeVisible(MenuCallbackArgs args)
        {
            try
            {
                if (!IsHideoutPeacefulVisible(args)) return false;
                int amt = CurrentCampBribeAmount();
                MBTextManager.SetTextVariable("CAMP_BRIBE_AMOUNT", amt);

                // Wave 4.5: if chief recently refused a bribe, lock out the option for a
                // few days so the player can't just spam-retry until the random roll passes.
                var hideout = GetCurrentHideout();
                if (hideout != null
                    && _bribeRefusalCooldown.TryGetValue(hideout.StringId, out var until)
                    && until.IsFuture)
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("{=bp_dialogmanager_176}The chief is still angry from your last attempt.");
                    return true;
                }

                if (Hero.MainHero == null || Hero.MainHero.Gold < amt)
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("You need " + amt + " denars.");
                }
                return true;
            }
            catch { return false; }
        }

        // Wave 4.5: bribe is now a relation-modulated roll. Base 50% accept, ±0.5% per
        // relation point, clamped 10–90% so neither extreme is deterministic. On refuse the
        // chief throws the purse back, relation drops, and the option is locked out for a
        // few days. On accept, existing safe-passage behavior fires.
        private void OnCampBribeConsequence(MenuCallbackArgs args)
        {
            try
            {
                int amt = CurrentCampBribeAmount();
                if (Hero.MainHero == null || Hero.MainHero.Gold < amt) return;

                var hideout = GetCurrentHideout();
                var leader = GetHideoutParty(hideout)?.LeaderHero;
                int relation = leader != null ? (int)leader.GetRelationWithPlayer() : 0;
                int chance = Math.Max(10, Math.Min(90, 50 + (relation / 2)));

                // v64-fix2 (2026-05-08): bribe was forcing LeaveEncounter on BOTH paths,
                // which kicked the player out of the camp menu and (per user report) made
                // re-entering unreliable. The narrative weight of "chief is angry" is
                // already carried by the popup + 3-day cooldown + relation drop — we don't
                // need to physically expel them. Same for the accept path: paying for safe
                // passage shouldn't force them off the world map.
                //
                // Spam fix: cooldown is set on BOTH paths now (success = 1 day, refusal =
                // 3 days). IsCampBribeVisible reads this dictionary and disables the option.
                // Even within the same encounter the second click finds it greyed out.
                //
                // Relation target fix: vanilla bandit MobileParty rarely has LeaderHero set,
                // so the previous code silently no-op'd the relation change. Resolve the
                // promoted chief Hero via BanditChiefRegistry.GetChief(cultureId) — that's
                // the real Hero that the rest of the dialog system already uses.
                Hero relationTarget = leader;
                if (relationTarget == null)
                {
                    string cid = GetCultureIdForHideout(hideout);
                    if (!string.IsNullOrEmpty(cid))
                        relationTarget = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(cid);
                }

                if (MBRandom.RandomInt(100) < chance)
                {
                    // ACCEPT — player paid for safe passage; let them stay and explore the
                    // camp menu (drink, scout, eavesdrop). No LeaveEncounter on success.
                    GiveGoldAction.ApplyBetweenCharacters(Hero.MainHero, null, amt, true);
                    if (relationTarget != null) ChangeRelationAction.ApplyPlayerRelation(relationTarget, 5, true, true);
                    if (hideout != null)
                        _bribeRefusalCooldown[hideout.StringId] = CampaignTime.DaysFromNow(1f);
                    var sbA = new System.Text.StringBuilder();
                    sbA.AppendLine(Localization.Get("bp_dialogmanager_177", "The chief weighs your purse, then pockets it."));
                    sbA.AppendLine();
                    sbA.AppendLine(Localization.Get("bp_dialogmanager_178", "\"Walk safely. For now.\""));
                    sbA.AppendLine();
                    sbA.AppendLine("─────────────────────────────────");
                    sbA.AppendLine();
                    sbA.AppendLine("◆  Paid: " + amt + " denars");
                    sbA.AppendLine("◆  Relation with " + (relationTarget?.Name?.ToString() ?? Localization.Get("bp_dialogmanager_179", "the chief")) + ": +5");
                    sbA.AppendLine(Localization.Get("bp_dialogmanager_180", "◆  No further bribes today."));
                    ShowCampInquiry(Localization.Get("bp_dialogmanager_181", "Bribe Accepted"), sbA.ToString().TrimEnd());
                }
                else
                {
                    // REFUSE — gold thrown back, relation drops, soft hostile.
                    // v64-fix3 (2026-05-08): user wants the chief's anger to expel the
                    // player from the hideout entirely (restored from v64-fix2 which had
                    // it stripped). Re-entry remains possible because IsHideoutPeacefulVisible
                    // doesn't gate on the bribe cooldown — only the bribe button itself
                    // is locked for 3 days. Order matters: render popup FIRST so the
                    // narrative beat lands, then set LeaveEncounter for the next tick.
                    if (relationTarget != null) ChangeRelationAction.ApplyPlayerRelation(relationTarget, -3, true, true);
                    if (hideout != null)
                        _bribeRefusalCooldown[hideout.StringId] = CampaignTime.DaysFromNow(3f);
                    var sbR = new System.Text.StringBuilder();
                    sbR.AppendLine(Localization.Get("bp_dialogmanager_182", "The chief hurls the purse back at your feet."));
                    sbR.AppendLine();
                    sbR.AppendLine(Localization.Get("bp_dialogmanager_183", "\"You think coin buys a stranger's safety in MY camp? Walk out the way you came — and don't come back for some days, unless you want a knife between your shoulders.\""));
                    sbR.AppendLine();
                    sbR.AppendLine("─────────────────────────────────");
                    sbR.AppendLine();
                    sbR.AppendLine("◆  Relation with " + (relationTarget?.Name?.ToString() ?? Localization.Get("bp_dialogmanager_184", "the chief")) + ": -3");
                    sbR.AppendLine(Localization.Get("bp_dialogmanager_185", "◆  The camp will not bargain again for three days."));
                    sbR.AppendLine(Localization.Get("bp_dialogmanager_186", "◆  You are escorted from the hideout."));
                    // returnToCampMenu=false — the affirmative callback won't try to
                    // re-open the camp menu (we're leaving).
                    ShowCampInquiry(Localization.Get("bp_dialogmanager_187", "Bribe Refused"), sbR.ToString().TrimEnd(), false);
                    PlayerEncounter.LeaveEncounter = true;
                }
            }
            catch (Exception ex) { InformationManager.DisplayMessage(new InformationMessage(
                "[BandIt Plus] Bribe error: " + ex.Message, Colors.Red)); }
        }

        // -- Drink with the bandits --
        private bool IsCampDrinkVisible(MenuCallbackArgs args)
        {
            try
            {
                if (!IsHideoutPeacefulVisible(args)) return false;
                var hideout = GetCurrentHideout();
                if (hideout != null && IsOnCooldown(_campDrinkCooldown, hideout.StringId))
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("{=bp_dialogmanager_188}They've already had enough drinking with you today.");
                    return true;
                }
                if (Hero.MainHero == null || Hero.MainHero.Gold < 10)
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("{=bp_dialogmanager_189}You need 10 denars to buy a round.");
                }
                return true;
            }
            catch { return false; }
        }

        private void OnCampDrinkConsequence(MenuCallbackArgs args)
        {
            try
            {
                var hideout = GetCurrentHideout();
                if (hideout == null) return;
                if (IsOnCooldown(_campDrinkCooldown, hideout.StringId)) return;

                GiveGoldAction.ApplyBetweenCharacters(Hero.MainHero, null, 10, true);
                // v64-fix2: vanilla bandit parties rarely have LeaderHero set; fall back
                // to the promoted chief Hero from BanditChiefRegistry so relation actually
                // applies (and the player sees the "+1 relation" toast).
                Hero relTarget = GetHideoutParty(hideout)?.LeaderHero;
                if (relTarget == null)
                {
                    string cid = GetCultureIdForHideout(hideout);
                    if (!string.IsNullOrEmpty(cid))
                        relTarget = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(cid);
                }
                if (relTarget != null) ChangeRelationAction.ApplyPlayerRelation(relTarget, 1, true, true);
                _campDrinkCooldown[hideout.StringId] = CampaignTime.DaysFromNow(1);

                var profile = GetProfileForHideout(hideout);
                var flavor = !string.IsNullOrEmpty(profile?.DrinkFlavor) ? profile.DrinkFlavor
                    : Localization.Get("bp_dialogmanager_190", "You share a horn of mead. The bandits warm to you a little.");
                ShowCampInquiry(
                    Localization.Get("bp_dialogmanager_191", "Drinking with the Bandits"),
                    flavor + "\n\n— You spent 10 denars. Relation +1.");
            }
            catch (Exception ex) { InformationManager.DisplayMessage(new InformationMessage(
                "[BandIt Plus] Drink error: " + ex.Message, Colors.Red)); }
        }

        // -- Eavesdrop --
        private static readonly string[] EavesdropRumors = new[]
        {
            Localization.Get("bp_dialogmanager_192", "\"Heard a Vlandian merchant's caravan rolls through Pendraic next week. Fat one. We'll take it.\""),
            Localization.Get("bp_dialogmanager_193", "\"The lord at Sanala increased his levies — boys from villages, scared of their own swords.\""),
            Localization.Get("bp_dialogmanager_194", "\"Khuzaits are pushing west again. Easy raids on the steppe edges while the riders are gone.\""),
            Localization.Get("bp_dialogmanager_195", "\"Old hideout up north has been abandoned. Looter brothers all dead. Their stash is still there if anyone's brave.\""),
            Localization.Get("bp_dialogmanager_196", "\"A noblewoman lost her caravan three days east. Reward posted, but no one's gone after it.\""),
            Localization.Get("bp_dialogmanager_197", "\"Town watch in Marunath is bribable. Half the guard there owes us favors already.\""),
            Localization.Get("bp_dialogmanager_198", "\"Rumor in the south: Aserai sultan paying mercenaries to clear bandits. We move at night now.\""),
            Localization.Get("bp_dialogmanager_199", "\"Imperial deserter named Crispus is selling armor cheap — won't ask where it came from.\""),
            Localization.Get("bp_dialogmanager_200", "\"There's a relic in a Battanian tomb west of here. Whoever digs it up first sells it for a fortune.\""),
            Localization.Get("bp_dialogmanager_201", "\"Boatmen at Omor will smuggle anything if you pay enough. They asked us not to mention names.\""),
            Localization.Get("bp_dialogmanager_202", "\"That kingdom war up north — both sides recruit anyone who can hold a spear. Easy pay if you don't mind dying.\""),
            Localization.Get("bp_dialogmanager_203", "\"Sturgian raiders sacked a coast village last winter. Survivors say they took twenty captives. Slave market knows where.\""),
            Localization.Get("bp_dialogmanager_204", "\"A thief who robbed the tournament prize is hiding in the marshes. Bounty's high.\""),
            Localization.Get("bp_dialogmanager_205", "\"Caravans avoid the eastern road since we put two heads on poles. Now they take the longer route — through OUR canyon.\""),
            Localization.Get("bp_dialogmanager_206", "\"The blacksmith at the next town up is fencing stolen goods. Pays half value but asks no questions.\""),
            Localization.Get("bp_dialogmanager_207", "\"Some lord's daughter eloped with a peasant. Family wants her back. Whole or otherwise.\""),
            Localization.Get("bp_dialogmanager_208", "\"Rebel cell hiding in the Battanian highlands. They pay anyone who'll fight the empire.\""),
            Localization.Get("bp_dialogmanager_209", "\"That bandit chief everyone fears? He's just an old man with sons. Cut down the sons and he folds.\""),
            Localization.Get("bp_dialogmanager_210", "\"Salt prices dropping. Salt smugglers are leaving the trade. Empty caravans coming back from the salt marshes — easy targets.\""),
            Localization.Get("bp_dialogmanager_211", "\"Heard the Western Empire is hiring mercenaries to hunt 'undesirables.' That means us. Stay sharp.\""),
        };

        private bool IsCampEavesdropVisible(MenuCallbackArgs args)
        {
            try
            {
                if (!IsHideoutPeacefulVisible(args)) return false;
                var hideout = GetCurrentHideout();
                if (hideout != null && IsOnCooldown(_campEavesdropCooldown, hideout.StringId))
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("{=bp_dialogmanager_212}Already heard their gossip today.");
                }
                return true;
            }
            catch { return false; }
        }

        private void OnCampEavesdropConsequence(MenuCallbackArgs args)
        {
            try
            {
                var hideout = GetCurrentHideout();
                if (hideout == null) return;
                if (IsOnCooldown(_campEavesdropCooldown, hideout.StringId)) return;

                // Prefer per-culture rumors; fall back to generic pool if profile missing.
                var profile = GetProfileForHideout(hideout);
                string[] pool = (profile?.Rumors != null && profile.Rumors.Length > 0)
                    ? profile.Rumors : EavesdropRumors;
                var rumor = pool[MBRandom.RandomInt(pool.Length)];
                _campEavesdropCooldown[hideout.StringId] = CampaignTime.DaysFromNow(1);

                // v64-fix3 (2026-05-08): user reported that rumors describing loot/coin
                // never produced anything. Add a 25% chance of a small denar windfall
                // (purse spotted, contact paid, etc.) so the "sometimes you get
                // something" intuition matches reality. Range scales loosely with party
                // tier so it stays meaningful but never replaces real loot sources.
                int reward = 0;
                if (MBRandom.RandomInt(100) < 25)
                {
                    reward = 20 + MBRandom.RandomInt(41); // 20–60 denars
                    if (Hero.MainHero != null)
                        GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, reward, false);
                }

                var bodySb = new System.Text.StringBuilder();
                bodySb.AppendLine(Localization.Get("bp_dialogmanager_213", "You linger by the fire and listen."));
                bodySb.AppendLine();
                bodySb.AppendLine(rumor);
                if (reward > 0)
                {
                    bodySb.AppendLine();
                    bodySb.AppendLine("─────────────────────────────────");
                    bodySb.AppendLine();
                    bodySb.AppendLine(Localization.Get("bp_dialogmanager_214", "◆  You spot a fat purse left by the cookfire — and slip it into your belt while no one's looking."));
                    bodySb.AppendLine("◆  +" + reward + " denars.");
                }
                ShowCampInquiry(Localization.Get("bp_dialogmanager_215", "Eavesdropping at the Cookfire"), bodySb.ToString().TrimEnd());
            }
            catch (Exception ex) { InformationManager.DisplayMessage(new InformationMessage(
                "[BandIt Plus] Eavesdrop error: " + ex.Message, Colors.Red)); }
        }

        // -- Rest in the camp (morale +5) --
        private bool IsCampRestVisible(MenuCallbackArgs args)
        {
            try
            {
                if (!IsHideoutPeacefulVisible(args)) return false;
                var hideout = GetCurrentHideout();
                if (hideout != null && IsOnCooldown(_campRestCooldown, hideout.StringId))
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("{=bp_dialogmanager_216}Already rested today.");
                }
                return true;
            }
            catch { return false; }
        }

        private void OnCampRestConsequence(MenuCallbackArgs args)
        {
            try
            {
                var hideout = GetCurrentHideout();
                if (hideout == null) return;
                if (IsOnCooldown(_campRestCooldown, hideout.StringId)) return;

                // v64-fix2: RecentEventsMorale alone decays toward 0 quickly. Bump it AND
                // also push the morale notification so the player sees the change in the
                // bottom-left feed, in case the popup body is missed.
                var party = MobileParty.MainParty;
                float beforeMorale = 0f, afterMorale = 0f;
                if (party != null)
                {
                    beforeMorale = party.Morale;
                    party.RecentEventsMorale += 5f;
                    afterMorale = party.Morale;
                }
                _campRestCooldown[hideout.StringId] = CampaignTime.DaysFromNow(1);

                var profile = GetProfileForHideout(hideout);
                var flavor = !string.IsNullOrEmpty(profile?.RestFlavor) ? profile.RestFlavor
                    : Localization.Get("bp_dialogmanager_217", "Your party rests by the cookfires. Morale rises.");
                ShowCampInquiry(
                    Localization.Get("bp_dialogmanager_218", "Resting in the Camp"),
                    flavor + "\n\n─────────────────────────────────\n\n◆  Party morale: +5 (recent-events buffer)\n◆  Resting again locked for one day.");
            }
            catch (Exception ex) { InformationManager.DisplayMessage(new InformationMessage(
                "[BandIt Plus] Rest error: " + ex.Message, Colors.Red)); }
        }

        // -- v64: Scout the perimeter (lists nearby parties within 30 map units) --
        // Free, daily cooldown. Reveals what the chief's pickets see — useful tactical
        // intel before deciding to enter the camp or move on.
        // v64-fix2 (2026-05-08): scouting requires troops to send out. With fewer than
        // 5 men, the player has nobody to spare for picket duty.
        private const int kScoutMinTroops = 5;

        private bool IsCampScoutVisible(MenuCallbackArgs args)
        {
            try
            {
                if (!IsHideoutPeacefulVisible(args)) return false;
                int men = MobileParty.MainParty?.MemberRoster?.TotalManCount ?? 0;
                if (men < kScoutMinTroops)
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("You need at least " + kScoutMinTroops + " troops to send scouts (" + men + "/" + kScoutMinTroops + ").");
                    return true;
                }
                var hideout = GetCurrentHideout();
                if (hideout != null && IsOnCooldown(_campScoutCooldown, hideout.StringId))
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("{=bp_dialogmanager_219}The pickets have already given today's report.");
                }
                return true;
            }
            catch { return false; }
        }

        private void OnCampScoutConsequence(MenuCallbackArgs args)
        {
            try
            {
                var hideout = GetCurrentHideout();
                if (hideout == null) return;
                if (IsOnCooldown(_campScoutCooldown, hideout.StringId)) return;

                int menCheck = MobileParty.MainParty?.MemberRoster?.TotalManCount ?? 0;
                if (menCheck < kScoutMinTroops)
                {
                    ShowCampInquiry(
                        Localization.Get("bp_dialogmanager_220", "No Scouts to Spare"),
                        "You don't have the men. You'd be sending yourself.\n\n─────────────────────────────────\n\n◆  Required: " + kScoutMinTroops + " troops in your party\n◆  Current: " + menCheck);
                    return;
                }

                _campScoutCooldown[hideout.StringId] = CampaignTime.DaysFromNow(1);

                // v64-fix (2026-05-08): replaced log-feed spam with a Gauntlet inquiry popup.
                // Collect first, sort by distance, then format into one body string.
                var hideoutPos = hideout.GetPosition2D;
                const float scoutRadius = 30f;
                const float scoutRadiusSq = scoutRadius * scoutRadius;
                var sightings = new List<(MobileParty party, float dist)>();
                foreach (var party in MobileParty.All)
                {
                    if (party == null || party == MobileParty.MainParty) continue;
                    if (party.IsActive == false) continue;
                    float distSq = hideoutPos.DistanceSquared(party.GetPosition2D);
                    if (distSq > scoutRadiusSq) continue;
                    sightings.Add((party, (float)System.Math.Sqrt(distSq)));
                }
                sightings.Sort((a, b) => a.dist.CompareTo(b.dist));

                string hideoutName = hideout.Name?.ToString() ?? Localization.Get("bp_dialogmanager_221", "the camp");
                string title = Localization.Get("bp_dialogmanager_222", "Perimeter Report");
                string body;
                if (sightings.Count == 0)
                {
                    var emptySb = new System.Text.StringBuilder();
                    emptySb.AppendLine("◆  " + hideoutName);
                    emptySb.AppendLine("◆  Watch range: " + ((int)scoutRadius) + " leagues");
                    emptySb.AppendLine();
                    emptySb.AppendLine("─────────────────────────────────");
                    emptySb.AppendLine();
                    emptySb.AppendLine(Localization.Get("bp_dialogmanager_223", "The pickets shake their heads."));
                    emptySb.AppendLine();
                    emptySb.AppendLine(Localization.Get("bp_dialogmanager_224", "The hills are empty. No parties stir within a day's ride of the camp."));
                    body = emptySb.ToString().TrimEnd();
                }
                else
                {
                    // Bucket sightings by distance band for hierarchical display.
                    // Bannerlord renders proportional fonts so we don't try column
                    // alignment — instead lean on whitespace + decorative chars +
                    // section grouping for a premium feel.
                    var atGates = new List<(MobileParty p, float d)>();
                    var closeQuarters = new List<(MobileParty p, float d)>();
                    var withinRide = new List<(MobileParty p, float d)>();
                    foreach (var s in sightings)
                    {
                        if (s.dist <= 5f) atGates.Add(s);
                        else if (s.dist <= 15f) closeQuarters.Add(s);
                        else withinRide.Add(s);
                    }

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("◆  " + hideoutName);
                    sb.AppendLine("◆  Watch range: " + ((int)scoutRadius) + " leagues  ·  " +
                                  sightings.Count + " sighting" + (sightings.Count > 1 ? "s" : ""));
                    sb.AppendLine();
                    sb.AppendLine("─────────────────────────────────");
                    sb.AppendLine();

                    int totalShown = 0;
                    const int hardCap = 10;

                    void AppendBand(string heading, List<(MobileParty p, float d)> bucket)
                    {
                        if (bucket.Count == 0) return;
                        sb.AppendLine(heading);
                        foreach (var s in bucket)
                        {
                            if (totalShown >= hardCap) return;
                            var p = s.p;
                            string name = p.Name?.ToString() ?? Localization.Get("bp_dialogmanager_225", "unnamed band");
                            string faction = p.MapFaction?.Name?.ToString() ?? Localization.Get("bp_dialogmanager_226", "unknown banner");
                            int men = p.MemberRoster != null ? p.MemberRoster.TotalManCount : 0;
                            string strengthLabel = men > 0 ? (men + " men") : Localization.Get("bp_dialogmanager_227", "size unknown");
                            sb.AppendLine("    ▸  " + name + "  ·  " + faction);
                            sb.AppendLine("        " + strengthLabel + "  ·  " + ((int)s.d) + " leagues");
                            totalShown++;
                        }
                        sb.AppendLine();
                    }

                    AppendBand(Localization.Get("bp_dialogmanager_228", "AT THE GATES"), atGates);
                    AppendBand(Localization.Get("bp_dialogmanager_229", "CLOSE QUARTERS"), closeQuarters);
                    AppendBand(Localization.Get("bp_dialogmanager_230", "WITHIN A DAY'S RIDE"), withinRide);

                    int remaining = sightings.Count - totalShown;
                    if (remaining > 0)
                    {
                        sb.AppendLine("…and " + remaining + " other movement" + (remaining > 1 ? "s" : "") + " too far for the pickets to read clearly.");
                        sb.AppendLine();
                    }

                    sb.AppendLine("─────────────────────────────────");

                    // Closing line varies with the threat profile.
                    string closer;
                    if (atGates.Count >= 3) closer = Localization.Get("bp_dialogmanager_231", "The chief frowns. \"Crowded morning. Stay sharp.\"");
                    else if (atGates.Count > 0) closer = Localization.Get("bp_dialogmanager_232", "The chief grunts. \"Same as always.\"");
                    else if (sightings.Count >= 5) closer = Localization.Get("bp_dialogmanager_233", "The chief nods. \"Plenty of movement, none of it ours.\"");
                    else closer = Localization.Get("bp_dialogmanager_234", "The chief shrugs. \"Quiet enough.\"");
                    sb.AppendLine();
                    sb.AppendLine(closer);

                    body = sb.ToString().TrimEnd();
                }

                ShowCampInquiry(title, body);
            }
            catch (Exception ex) { InformationManager.DisplayMessage(new InformationMessage(
                "[BandIt Plus] Scout error: " + ex.Message, Colors.Red)); }
        }

        // -- v64: Watch from the watchtower (atmospheric flavor, daily cooldown) --
        private static readonly string[] WatchtowerFlavor = new[]
        {
            Localization.Get("bp_dialogmanager_235", "From the tower the world is small and slow. Smoke threads from a distant village. A hawk turns on the high air. The day passes without consequence."),
            Localization.Get("bp_dialogmanager_236", "Below, the camp moves like clockwork — sentries swap, dogs circle, a child runs among the tents. The road stays empty. The chief's banners hang limp."),
            Localization.Get("bp_dialogmanager_237", "A storm builds in the north. The wind smells of wet stone. The watchman beside you spits and says nothing. He has stood here longer than you have been alive, his eyes say."),
            Localization.Get("bp_dialogmanager_238", "The setting sun catches every blade in the valley below. You count them, then stop counting. Every blade is a man, and every man, somewhere, is owed."),
            Localization.Get("bp_dialogmanager_239", "An unmarked rider comes out of the trees, sees the watchtower, and turns his horse around. The watchman makes no signal. The rider was never there."),
            Localization.Get("bp_dialogmanager_240", "From this height the land's truth is plain — the merchant roads thread between bandit territories like veins between teeth. The chiefs know what they are. So do you."),
            Localization.Get("bp_dialogmanager_241", "Crows gather over a copse two hills east. The watchman shakes his head. 'Wolves got something. Or somebody.' He goes back to chewing his cud.")
        };

        private bool IsCampWatchtowerVisible(MenuCallbackArgs args)
        {
            try
            {
                if (!IsHideoutPeacefulVisible(args)) return false;
                var hideout = GetCurrentHideout();
                if (hideout != null && IsOnCooldown(_campWatchtowerCooldown, hideout.StringId))
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("{=bp_dialogmanager_242}The watchman has already shared his view today.");
                }
                return true;
            }
            catch { return false; }
        }

        private void OnCampWatchtowerConsequence(MenuCallbackArgs args)
        {
            try
            {
                var hideout = GetCurrentHideout();
                if (hideout == null) return;
                if (IsOnCooldown(_campWatchtowerCooldown, hideout.StringId)) return;

                _campWatchtowerCooldown[hideout.StringId] = CampaignTime.DaysFromNow(1);
                var line = WatchtowerFlavor[MBRandom.RandomInt(WatchtowerFlavor.Length)];
                ShowCampInquiry(
                    Localization.Get("bp_dialogmanager_243", "From the Watchtower"),
                    "You climb the watchtower.\n\n" + line);
            }
            catch (Exception ex) { InformationManager.DisplayMessage(new InformationMessage(
                "[BandIt Plus] Watchtower error: " + ex.Message, Colors.Red)); }
        }

        // -- Ask about other hideouts (reveal one nearby of same culture) --
        private bool IsCampMapVisible(MenuCallbackArgs args)
        {
            try
            {
                if (!IsHideoutPeacefulVisible(args)) return false;
                ApplyTrustGate(args, 1);
                if (!args.IsEnabled) return true;
                var hideout = GetCurrentHideout();
                if (hideout != null && IsOnCooldown(_campMapCooldown, hideout.StringId))
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("{=bp_dialogmanager_244}Already shared what they know today.");
                    return true;
                }
                if (Hero.MainHero == null || Hero.MainHero.Gold < 50)
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("{=bp_dialogmanager_245}You need 50 denars.");
                }
                return true;
            }
            catch { return false; }
        }

        private void OnCampMapConsequence(MenuCallbackArgs args)
        {
            try
            {
                var hideout = GetCurrentHideout();
                if (hideout == null) return;
                if (IsOnCooldown(_campMapCooldown, hideout.StringId)) return;
                if (Hero.MainHero == null || Hero.MainHero.Gold < 50) return;

                Settlement nearestUnseen = null;
                float nearestDist = float.MaxValue;
                if (Settlement.All != null)
                {
                    var here = MobileParty.MainParty?.GetPosition2D ?? hideout.GetPosition2D;
                    foreach (var s in Settlement.All)
                    {
                        if (s == null || !s.IsHideout) continue;
                        if (s == hideout) continue;
                        if (s.Culture?.StringId != hideout.Culture?.StringId) continue;
                        if (s.IsVisible) continue;
                        var d = s.GetPosition2D.DistanceSquared(here);
                        if (d < nearestDist) { nearestDist = d; nearestUnseen = s; }
                    }
                }

                if (nearestUnseen == null)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        Localization.Get("bp_dialogmanager_246", "[BandIt Plus] The chief shrugs. \"You already know all the camps of our kind.\""),
                        Colors.Yellow));
                    return;
                }

                GiveGoldAction.ApplyBetweenCharacters(Hero.MainHero, null, 50, true);
                nearestUnseen.IsVisible = true;
                _campMapCooldown[hideout.StringId] = CampaignTime.DaysFromNow(1);

                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] The chief sketches in the dirt. \"" +
                    (nearestUnseen.Name?.ToString() ?? Localization.Get("bp_dialogmanager_247", "A hideout")) +
                    " — that's where our cousins make camp.\" Marked on your map.",
                    Colors.Yellow));
                GameMenu.SwitchToMenu("bp_hideout_camp");
            }
            catch (Exception ex) { InformationManager.DisplayMessage(new InformationMessage(
                "[BandIt Plus] Map error: " + ex.Message, Colors.Red)); }
        }

        private bool IsHideoutTradeVisible(MenuCallbackArgs args)
        {
            if (!IsHideoutPeacefulVisible(args)) return false;
            args.optionLeaveType = GameMenuOption.LeaveType.Trade;
            ApplyTrustGate(args, 1);
            return true;
        }

        private bool IsHideoutArmoryVisible(MenuCallbackArgs args)
        {
            if (!IsHideoutPeacefulVisible(args)) return false;
            args.optionLeaveType = GameMenuOption.LeaveType.Trade;
            ApplyTrustGate(args, 2);
            return true;
        }




        // === Armory: open trade screen with bandit-style gear ===
        private static ItemRoster _banditArmoryRoster;
        private static CampaignTime _banditArmoryRefreshAt;

        private static ItemRoster BuildBanditArmoryRoster()
        {
            var roster = new ItemRoster();
            try
            {
                int added = 0;
                foreach (var item in Items.All)
                {
                    if (item == null) continue;
                    if (item.IsCraftedByPlayer) continue;
                    if (item.NotMerchandise) continue;

                    bool isBanditGear = false;
                    var cultureId = item.Culture?.StringId;
                    if (!string.IsNullOrEmpty(cultureId)
                        && (cultureId == "looters" || cultureId == "forest_bandits"
                            || cultureId == "mountain_bandits" || cultureId == "steppe_bandits"
                            || cultureId == "sea_raiders" || cultureId == "desert_bandits"
                            || cultureId.StartsWith("bp_")))
                    {
                        isBanditGear = true;
                    }

                    bool armorOrWeapon = item.ItemType == ItemObject.ItemTypeEnum.HeadArmor
                        || item.ItemType == ItemObject.ItemTypeEnum.BodyArmor
                        || item.ItemType == ItemObject.ItemTypeEnum.LegArmor
                        || item.ItemType == ItemObject.ItemTypeEnum.HandArmor
                        || item.ItemType == ItemObject.ItemTypeEnum.Cape
                        || item.ItemType == ItemObject.ItemTypeEnum.Shield
                        || item.ItemType == ItemObject.ItemTypeEnum.OneHandedWeapon
                        || item.ItemType == ItemObject.ItemTypeEnum.TwoHandedWeapon
                        || item.ItemType == ItemObject.ItemTypeEnum.Polearm
                        || item.ItemType == ItemObject.ItemTypeEnum.Bow
                        || item.ItemType == ItemObject.ItemTypeEnum.Crossbow
                        || item.ItemType == ItemObject.ItemTypeEnum.Arrows
                        || item.ItemType == ItemObject.ItemTypeEnum.Bolts;

                    if (!armorOrWeapon) continue;
                    if (!isBanditGear && item.Tier > ItemObject.ItemTiers.Tier3) continue;
                    if (item.Value <= 0 || item.Value > 5000) continue;

                    roster.AddToCounts(item, 1);
                    if (++added >= 25) break;
                }
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Armory roster build error: " + ex.Message, Colors.Red));
            }
            return roster;
        }

        private void OnHideoutArmoryConsequence(MenuCallbackArgs args)
        {
            try
            {
                var hideout = GetCurrentHideout();
                if (hideout == null)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        Localization.Get("bp_dialogmanager_248", "[BandIt Plus] No hideout found."), Colors.Red));
                    return;
                }

                if (_banditArmoryRoster == null || _banditArmoryRoster.Count == 0
                    || _banditArmoryRefreshAt.IsPast)
                {
                    _banditArmoryRoster = BuildBanditArmoryRoster();
                    _banditArmoryRefreshAt = CampaignTime.DaysFromNow(7);
                }

                var sc = hideout.SettlementComponent;
                if (sc == null)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        Localization.Get("bp_dialogmanager_249", "[BandIt Plus] Hideout has no settlement component."), Colors.Red));
                    return;
                }

                Helpers.InventoryScreenHelper.OpenScreenAsTrade(
                    _banditArmoryRoster,
                    sc,
                    Helpers.InventoryScreenHelper.InventoryCategoryType.None,
                    null);
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Armory error: " + ex.Message, Colors.Red));
            }
        }

        // === Camp doctor: heal wounded troops for gold ===
        private bool IsHideoutDoctorVisible(MenuCallbackArgs args)
        {
            try
            {
                if (!IsHideoutPeacefulVisible(args)) return false;
                ApplyTrustGate(args, 1);

                // Wave 4.12-fix (2026-05-11): camp doctor now also requires the slaver
                // Tier-1 quest to be completed. The slaver appears at chief Tier-3 and
                // his first quest gates this medical service — narrative: the doctor
                // works for the slaver's operation; you don't get access until you've
                // proven useful to the slaver.
                var hideout = GetCurrentHideout();
                var cultureId = hideout?.Culture?.StringId;
                int slaverTrust = !string.IsNullOrEmpty(cultureId) ? GetSlaverTrust(cultureId) : 0;
                if (slaverTrust < 1)
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("{=bp_dialogmanager_250}The doctor answers to the slaver. Earn his trust first.");
                    return true;
                }

                int wounded = PartyBase.MainParty?.MemberRoster?.TotalWounded ?? 0;
                if (wounded <= 0)
                {
                    args.IsEnabled = false;
                    args.Tooltip = new TextObject("{=bp_dialogmanager_251}Nobody in your party is wounded.");
                }
                return true;
            }
            catch { return false; }
        }

        private void OnHideoutDoctorConsequence(MenuCallbackArgs args)
        {
            try
            {
                int wounded = PartyBase.MainParty?.MemberRoster?.TotalWounded ?? 0;
                if (wounded <= 0)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        Localization.Get("bp_dialogmanager_252", "[BandIt Plus] No wounded troops."), Colors.Yellow));
                    return;
                }
                int costPer = 10;
                int total = wounded * costPer;
                if (Hero.MainHero == null || Hero.MainHero.Gold < total)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        "[BandIt Plus] Doctor wants " + total + " denars (" + costPer +
                        " per wounded). You can't afford it.", Colors.Yellow));
                    return;
                }
                var roster = PartyBase.MainParty.MemberRoster;
                for (int i = 0; i < roster.Count; i++)
                {
                    int w = roster.GetElementWoundedNumber(i);
                    if (w > 0) roster.SetElementWoundedNumber(i, 0);
                }
                GiveGoldAction.ApplyBetweenCharacters(Hero.MainHero, null, total, true);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(Localization.Get("bp_dialogmanager_253", "The camp doctor — a wiry old field-medic with hands that don't shake — works through your wounded with bone needle and bandage."));
                sb.AppendLine();
                sb.AppendLine("─────────────────────────────────");
                sb.AppendLine();
                sb.AppendLine("◆  Patched up: " + wounded + " troop" + (wounded > 1 ? "s" : ""));
                sb.AppendLine("◆  Paid: " + total + " denars  ·  (" + costPer + " per wounded)");
                ShowCampInquiry(Localization.Get("bp_dialogmanager_254", "Camp Doctor"), sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Doctor error: " + ex.Message, Colors.Red));
            }
        }

        // ============================================================
        // Chief signature quests (2026-07-03) — dialog layer.
        // The quest engine (BanditSignatureQuest) owns the journal + target
        // party lifecycle; everything conversational lives here. All prose
        // comes from CultureProfile.Signature — no strings authored below.
        // ============================================================

        public int GetSignatureState(string cultureId)
        {
            int v;
            return cultureId != null && _signatureState.TryGetValue(cultureId, out v) ? v : 0;
        }

        public void SetSignatureState(string cultureId, int state)
        {
            if (string.IsNullOrEmpty(cultureId)) return;
            _signatureState[cultureId] = state;
            SigLog("state[" + cultureId + "] = " + state);
        }

        private bool SignatureEnabled()
        {
            return MCMSettings.Instance != null && MCMSettings.Instance.EnableMod
                && MCMSettings.Instance.EnableSignatureQuests;
        }

        // Culture id of the chief the player is talking to, or null when the
        // conversation partner is not a registered chief with authored signature
        // content. Works in all three chief contexts (camp, roadside, notable).
        private string GetSignatureChiefCulture()
        {
            try
            {
                var partner = Hero.OneToOneConversationHero;
                if (partner == null) return null;
                var registry = BandItPlus.Heroes.BanditChiefRegistry.Instance;
                if (registry == null) return null;
                foreach (var kv in BandItPlus.Cultures.CultureProfileRegistry.ByCultureId)
                {
                    if (kv.Value == null || kv.Value.Signature == null) continue;
                    Hero chief = null;
                    try { chief = registry.GetChief(kv.Key); } catch { }
                    if (chief != null && chief == partner) return kv.Key;
                }
            }
            catch (Exception ex) { SigLog("GetSignatureChiefCulture " + ex.GetType().Name + ": " + ex.Message); }
            return null;
        }

        private BandItPlus.Cultures.SignatureQuestSpec GetSignatureSpec(string cultureId)
        {
            BandItPlus.Cultures.CultureProfile p;
            if (cultureId != null
                && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out p))
                return p.Signature;
            return null;
        }

        private void RegisterSignatureDialogs(CampaignGameStarter starter)
        {
            // Herald — a rank-and-file roadside bandit points at the chief.
            starter.AddPlayerLine("bp_sig_herald_ask", "bp_encounter_root", "bp_sig_herald",
                "{=bp_dialogmanager_255}Any word from your chief?",
                new ConversationSentence.OnConditionDelegate(CanHearSignatureHerald), null);
            starter.AddDialogLine("bp_sig_herald_line", "bp_sig_herald", "bp_encounter_root",
                "{=!}{BP_SIG_HERALD}",
                new ConversationSentence.OnConditionDelegate(SetSignatureHeraldVar), null);

            // Offer — at the chief, trust T2+, once per campaign.
            starter.AddPlayerLine("bp_sig_ask", "bp_encounter_root", "bp_sig_offer",
                "{=bp_dialogmanager_256}You said there was real work. I'm listening.",
                new ConversationSentence.OnConditionDelegate(CanAskSignature), null);
            starter.AddDialogLine("bp_sig_offer_busy", "bp_sig_offer", "close_window",
                "{=!}{BP_SIG_BUSY}",
                new ConversationSentence.OnConditionDelegate(SetSignatureBusyVar), null);
            starter.AddDialogLine("bp_sig_offer_text", "bp_sig_offer", "bp_sig_offer_answer",
                "{=!}{BP_SIG_OFFER}",
                new ConversationSentence.OnConditionDelegate(SetSignatureOfferVar), null);
            starter.AddPlayerLine("bp_sig_accept", "bp_sig_offer_answer", "bp_sig_accepted",
                "{=bp_dialogmanager_257}I'll see it done.", null, null);
            starter.AddPlayerLine("bp_sig_decline", "bp_sig_offer_answer", "bp_sig_declined",
                "{=bp_dialogmanager_258}Not yet.", null, null);
            starter.AddDialogLine("bp_sig_accepted_line", "bp_sig_accepted", "close_window",
                "{=!}{BP_SIG_ACCEPT}",
                new ConversationSentence.OnConditionDelegate(SetSignatureAcceptVar),
                new ConversationSentence.OnConsequenceDelegate(OnAcceptSignature));
            starter.AddDialogLine("bp_sig_declined_line", "bp_sig_declined", "bp_encounter_root",
                "{=!}{BP_SIG_DECLINE}",
                new ConversationSentence.OnConditionDelegate(SetSignatureDeclineVar), null);

            // Confrontation — the spawned target party's own tree. Battle choices
            // close WITHOUT LeaveEncounter so the vanilla encounter menu (Attack)
            // comes back; peaceful choices resolve in-conversation and leave.
            starter.AddDialogLine("bp_sig_confront_start", "start", "bp_sig_confront",
                "{=!}{BP_SIG_INTRO}",
                new ConversationSentence.OnConditionDelegate(IsSignatureConfrontStart), null);
            starter.AddPlayerLine("bp_sig_choice_0", "bp_sig_confront", "bp_sig_result",
                "{=!}{BP_SIG_CHOICE_0}",
                new ConversationSentence.OnConditionDelegate(CanPickSignatureChoice0),
                new ConversationSentence.OnConsequenceDelegate(OnPickSignatureChoice0));
            starter.AddPlayerLine("bp_sig_choice_1", "bp_sig_confront", "bp_sig_result",
                "{=!}{BP_SIG_CHOICE_1}",
                new ConversationSentence.OnConditionDelegate(CanPickSignatureChoice1),
                new ConversationSentence.OnConsequenceDelegate(OnPickSignatureChoice1));
            starter.AddPlayerLine("bp_sig_choice_2", "bp_sig_confront", "bp_sig_result",
                "{=!}{BP_SIG_CHOICE_2}",
                new ConversationSentence.OnConditionDelegate(CanPickSignatureChoice2),
                new ConversationSentence.OnConsequenceDelegate(OnPickSignatureChoice2));
            starter.AddPlayerLine("bp_sig_withdraw", "bp_sig_confront", "close_window",
                "{=bp_dialogmanager_259}Another time. Hold your road.", null,
                new ConversationSentence.OnConsequenceDelegate(OnSignatureWithdraw));
            starter.AddDialogLine("bp_sig_result_line", "bp_sig_result", "close_window",
                "{=!}{BP_SIG_RESULT}",
                new ConversationSentence.OnConditionDelegate(SetSignatureResultVar),
                new ConversationSentence.OnConsequenceDelegate(OnSignatureResolve));

            // Return — the chief's closing beat (Khalid inserts the stock question).
            starter.AddPlayerLine("bp_sig_return", "bp_encounter_root", "bp_sig_return_root",
                "{=bp_dialogmanager_260}The work you gave me is done.",
                new ConversationSentence.OnConditionDelegate(CanReturnSignature), null);
            starter.AddDialogLine("bp_sig_stock_ask", "bp_sig_return_root", "bp_sig_stock",
                "{=!}{BP_SIG_STOCK_INTRO}",
                new ConversationSentence.OnConditionDelegate(HasSignatureStockChoice), null);
            starter.AddPlayerLine("bp_sig_stock_a", "bp_sig_stock", "bp_sig_stock_res",
                "{=!}{BP_SIG_STOCK_A}",
                new ConversationSentence.OnConditionDelegate(SetSignatureStockAVar),
                new ConversationSentence.OnConsequenceDelegate(OnSignatureStockA));
            starter.AddPlayerLine("bp_sig_stock_b", "bp_sig_stock", "bp_sig_stock_res",
                "{=!}{BP_SIG_STOCK_B}",
                new ConversationSentence.OnConditionDelegate(SetSignatureStockBVar),
                new ConversationSentence.OnConsequenceDelegate(OnSignatureStockB));
            starter.AddDialogLine("bp_sig_stock_res_line", "bp_sig_stock_res", "bp_sig_finish",
                "{=!}{BP_SIG_STOCK_RESULT}",
                new ConversationSentence.OnConditionDelegate(SetSignatureStockResultVar), null);
            starter.AddDialogLine("bp_sig_close_direct", "bp_sig_return_root", "close_window",
                "{=!}{BP_SIG_CLOSING}",
                new ConversationSentence.OnConditionDelegate(SetSignatureClosingDirect),
                new ConversationSentence.OnConsequenceDelegate(OnCompleteSignature));
            starter.AddDialogLine("bp_sig_finish_line", "bp_sig_finish", "close_window",
                "{=!}{BP_SIG_CLOSING}",
                new ConversationSentence.OnConditionDelegate(SetSignatureClosingVar),
                new ConversationSentence.OnConsequenceDelegate(OnCompleteSignature));

            // Betrayal aftermath — the chief's permanent cold shoulder (Khalid).
            starter.AddPlayerLine("bp_sig_betrayed_ask", "bp_encounter_root", "bp_sig_betrayed",
                "{=bp_dialogmanager_261}About the work on the roads...",
                new ConversationSentence.OnConditionDelegate(CanAskBetrayedSignature), null);
            starter.AddDialogLine("bp_sig_betrayed_line", "bp_sig_betrayed", "close_window",
                "{=!}{BP_SIG_BETRAYED}",
                new ConversationSentence.OnConditionDelegate(SetSignatureBetrayedVar), null);

            SigLog("dialogs registered");
        }

        // ---------------- herald ----------------

        private bool CanHearSignatureHerald()
        {
            try
            {
                if (!SignatureEnabled()) return false;
                if (Hero.OneToOneConversationHero != null) return false;  // chiefs/notables use the offer path
                var party = MobileParty.ConversationParty;
                string cid = party?.MapFaction?.Culture?.StringId;
                var spec = GetSignatureSpec(cid);
                if (spec == null || string.IsNullOrEmpty(spec.HeraldLine)) return false;
                if (GetSignatureState(cid) != 0) return false;
                if (GetCultureTrust(cid) < 2) return false;
                return BandItPlus.Quests.BanditSignatureQuest.FindActive() == null;
            }
            catch { return false; }
        }

        private bool SetSignatureHeraldVar()
        {
            try
            {
                string cid = MobileParty.ConversationParty?.MapFaction?.Culture?.StringId;
                var spec = GetSignatureSpec(cid);
                if (spec == null) return false;
                MBTextManager.SetTextVariable("BP_SIG_HERALD", spec.HeraldLine);
                return true;
            }
            catch { return false; }
        }

        // ---------------- offer ----------------

        // One diag line per distinct gate outcome — the 07-03 field test burned a
        // round because a hidden offer line left no trace of WHY it was hidden.
        private string _sigLastGateDiag;

        private void SigGateDiag(string key)
        {
            if (key == _sigLastGateDiag) return;
            _sigLastGateDiag = key;
            SigLog("offer gate: " + key);
        }

        private bool CanAskSignature()
        {
            try
            {
                if (!SignatureEnabled()) return false;
                var partner = Hero.OneToOneConversationHero;
                string cid = GetSignatureChiefCulture();
                if (cid == null)
                {
                    // Only worth a diag line when we're actually facing a chief-like
                    // partner — a null on random villagers would spam the log.
                    if (partner != null && BandItPlus.Heroes.BanditChiefRegistry.Instance != null)
                        SigGateDiag("no authored Signature for partner '" + partner.Name + "' (wave 2/3 chief or not a chief)");
                    return false;
                }
                if (GetSignatureState(cid) != 0)
                {
                    SigGateDiag(cid + " state=" + GetSignatureState(cid) + " (already active/done/betrayed)");
                    return false;
                }
                if (GetCultureTrust(cid) < 2)
                {
                    SigGateDiag(cid + " trust=" + GetCultureTrust(cid) + " < 2");
                    return false;
                }
                var origins = BandItPlus.Origins.BanditOriginBehavior.Instance;
                if (origins != null && origins.OriginChosen
                    && !origins.IsAtPeaceWith(cid) && !IsChiefPactSworn(cid))
                {
                    SigGateDiag(cid + " not at peace / no pact");
                    return false;
                }
                SigGateDiag(cid + " offer VISIBLE");
                return true;
            }
            catch { return false; }
        }

        private bool SetSignatureBusyVar()
        {
            try
            {
                if (BandItPlus.Quests.BanditSignatureQuest.FindActive() == null) return false;
                var spec = GetSignatureSpec(GetSignatureChiefCulture());
                if (spec == null) return false;
                MBTextManager.SetTextVariable("BP_SIG_BUSY",
                    string.IsNullOrEmpty(spec.BusyLine)
                        ? "{=bp_dialogmanager_262}You carry unfinished work. Come back with empty hands." : spec.BusyLine);
                return true;
            }
            catch { return false; }
        }

        private bool SetSignatureOfferVar()
        {
            try
            {
                if (BandItPlus.Quests.BanditSignatureQuest.FindActive() != null) return false;
                var spec = GetSignatureSpec(GetSignatureChiefCulture());
                if (spec == null) return false;
                MBTextManager.SetTextVariable("BP_SIG_OFFER", spec.OfferProse);
                return true;
            }
            catch { return false; }
        }

        private bool SetSignatureAcceptVar()
        {
            try
            {
                var spec = GetSignatureSpec(GetSignatureChiefCulture());
                if (spec == null) return false;
                MBTextManager.SetTextVariable("BP_SIG_ACCEPT", spec.AcceptProse);
                return true;
            }
            catch { return false; }
        }

        private bool SetSignatureDeclineVar()
        {
            try
            {
                var spec = GetSignatureSpec(GetSignatureChiefCulture());
                if (spec == null) return false;
                MBTextManager.SetTextVariable("BP_SIG_DECLINE", spec.DeclineNudge);
                return true;
            }
            catch { return false; }
        }

        private void OnAcceptSignature()
        {
            try
            {
                string cid = GetSignatureChiefCulture();
                if (cid == null) return;
                Hero chief = Hero.OneToOneConversationHero;
                var quest = BandItPlus.Quests.BanditSignatureQuest.Begin(cid, chief);
                if (quest == null)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        Localization.Get("bp_dialogmanager_263", "[BandIt Plus] The chief's job could not be set up — see the log."), Colors.Red));
                    SigLog("[ERR] OnAcceptSignature: Begin returned null for " + cid);
                    return;
                }
                SetSignatureState(cid, 1);
                SigLog("offer accepted for " + cid);
            }
            catch (Exception ex) { SigLog("[ERR] OnAcceptSignature " + ex.GetType().Name + ": " + ex.Message); }
        }

        // ---------------- confrontation ----------------

        private bool IsSignatureConfrontStart()
        {
            try
            {
                if (!SignatureEnabled()) return false;
                var q = BandItPlus.Quests.BanditSignatureQuest.FindActive();
                if (q == null || q.Stage != "travel") return false;
                if (MobileParty.ConversationParty == null
                    || MobileParty.ConversationParty != q.TargetParty) return false;
                var spec = q.Spec;
                if (spec == null) return false;
                MBTextManager.SetTextVariable("BP_SIG_INTRO", spec.ConfrontIntro);
                // Speaker nameplate: "Steppe Bandit Boss" -> the authored captain
                // (07-03 feedback). Same ConversationNamePatch channel the chiefs use.
                try
                {
                    var speaker = CharacterObject.OneToOneConversationCharacter;
                    if (speaker != null && !string.IsNullOrEmpty(spec.CaptainName))
                    {
                        BandItPlus.Patches.ConversationNamePatch.OverrideName(speaker, spec.CaptainName);
                        _sigNamedCaptain = speaker;
                    }
                }
                catch { }
                return true;
            }
            catch { return false; }
        }

        private void ClearSignatureCaptainName()
        {
            try
            {
                if (_sigNamedCaptain != null)
                {
                    BandItPlus.Patches.ConversationNamePatch.ClearOverride(_sigNamedCaptain);
                    _sigNamedCaptain = null;
                }
            }
            catch { }
        }

        private bool CanPickSignatureChoice0() { return CanPickSignatureChoice(0); }
        private bool CanPickSignatureChoice1() { return CanPickSignatureChoice(1); }
        private bool CanPickSignatureChoice2() { return CanPickSignatureChoice(2); }
        private void OnPickSignatureChoice0() { OnPickSignatureChoice(0); }
        private void OnPickSignatureChoice1() { OnPickSignatureChoice(1); }
        private void OnPickSignatureChoice2() { OnPickSignatureChoice(2); }

        private bool CanPickSignatureChoice(int index)
        {
            try
            {
                var q = BandItPlus.Quests.BanditSignatureQuest.FindActive();
                if (q == null || q.Stage != "travel") return false;
                var spec = q.Spec;
                if (spec == null || spec.Choices == null || index >= spec.Choices.Length) return false;
                var choice = spec.Choices[index];
                if (choice == null || string.IsNullOrEmpty(choice.Label)) return false;
                // A laughed-off demand stays spent — the captain won't hear it twice.
                if (q.DemandFailed
                    && (choice.Requirement == BandItPlus.Cultures.SignatureRequirement.StrengthRatio
                        || choice.Requirement == BandItPlus.Cultures.SignatureRequirement.Renown)) return false;
                // Buy-out options hide when the purse can't cover them.
                if (choice.Requirement == BandItPlus.Cultures.SignatureRequirement.GoldCost
                    && Hero.MainHero.Gold < choice.RequirementValue) return false;
                MBTextManager.SetTextVariable("BP_SIG_CHOICE_" + index, choice.Label);
                return true;
            }
            catch { return false; }
        }

        private void OnPickSignatureChoice(int index)
        {
            try
            {
                _sigPendingResult = null;
                _sigPendingChoice = null;
                _sigPendingFail = false;
                var q = BandItPlus.Quests.BanditSignatureQuest.FindActive();
                var spec = q?.Spec;
                if (q == null || spec == null || spec.Choices == null || index >= spec.Choices.Length) return;
                var choice = spec.Choices[index];

                bool pass = true;
                switch (choice.Requirement)
                {
                    case BandItPlus.Cultures.SignatureRequirement.StrengthRatio:
                        // PartyBase.EstimatedStrength — 1.4.5 rename of TotalStrength
                        // (verified against the shipped DLL in BanditSiegeBehavior).
                        float mine = MobileParty.MainParty != null ? MobileParty.MainParty.Party.EstimatedStrength : 0f;
                        float theirs = q.TargetParty != null ? q.TargetParty.Party.EstimatedStrength : 1f;
                        pass = mine >= theirs * (choice.RequirementValue / 100f);
                        SigLog("demand check: mine=" + mine.ToString("F0") + " theirs=" + theirs.ToString("F0")
                            + " need x" + (choice.RequirementValue / 100f).ToString("F2") + " -> " + (pass ? "PASS" : "FAIL"));
                        break;
                    case BandItPlus.Cultures.SignatureRequirement.Renown:
                        float renown = Hero.MainHero.Clan != null ? Hero.MainHero.Clan.Renown : 0f;
                        pass = renown >= choice.RequirementValue;
                        SigLog("renown check: " + renown.ToString("F0") + " vs " + choice.RequirementValue
                            + " -> " + (pass ? "PASS" : "FAIL"));
                        break;
                    case BandItPlus.Cultures.SignatureRequirement.GoldCost:
                        TaleWorlds.CampaignSystem.Actions.GiveGoldAction.ApplyBetweenCharacters(
                            Hero.MainHero, null, choice.RequirementValue, false);
                        SigLog("paid " + choice.RequirementValue + " for choice " + index);
                        break;
                }

                if (!pass)
                {
                    q.DemandFailed = true;
                    _sigPendingFail = true;
                    _sigPendingResult = string.IsNullOrEmpty(spec.DemandFailProse)
                        ? "{=bp_dialogmanager_264}You'll take nothing from us today." : spec.DemandFailProse;
                    return;
                }
                _sigPendingChoice = choice;
                _sigPendingResult = choice.ResultProse;
            }
            catch (Exception ex) { SigLog("[ERR] OnPickSignatureChoice " + ex.GetType().Name + ": " + ex.Message); }
        }

        private bool SetSignatureResultVar()
        {
            if (string.IsNullOrEmpty(_sigPendingResult)) return false;
            MBTextManager.SetTextVariable("BP_SIG_RESULT", _sigPendingResult);
            return true;
        }

        private void OnSignatureResolve()
        {
            try
            {
                ClearSignatureCaptainName();
                var q = BandItPlus.Quests.BanditSignatureQuest.FindActive();
                var choice = _sigPendingChoice;
                bool failed = _sigPendingFail;
                _sigPendingResult = null;
                _sigPendingChoice = null;
                _sigPendingFail = false;
                if (q == null) return;

                if (failed)
                {
                    // Demand laughed off — conversation closes to the encounter
                    // menu; the player escalates (Attack) or leaves and returns.
                    SigLog("demand failed — choices reduced, encounter menu next");
                    return;
                }
                if (choice == null) return;

                switch (choice.Outcome)
                {
                    case BandItPlus.Cultures.SignatureOutcome.Battle:
                        // No LeaveEncounter: fall back to the vanilla encounter
                        // menu where Attack lives (bandit factions are always
                        // engine-hostile regardless of BP's soft peace).
                        SigLog("choice -> battle; encounter menu next");
                        break;
                    case BandItPlus.Cultures.SignatureOutcome.ComplyFull:
                        q.MarkResolvedPeaceful("full", choice.GoldToPlayer);
                        PlayerEncounter.LeaveEncounter = true;
                        break;
                    case BandItPlus.Cultures.SignatureOutcome.ComplyHalf:
                        q.MarkResolvedPeaceful("half", choice.GoldToPlayer);
                        PlayerEncounter.LeaveEncounter = true;
                        break;
                    case BandItPlus.Cultures.SignatureOutcome.Betray:
                        string cid = q.CultureId;
                        q.MarkBetrayed(choice.GoldToPlayer);
                        SetSignatureState(cid, 3);
                        AdjustCultureTrust(cid, -1);
                        try
                        {
                            Hero chief = q.QuestGiver ?? BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(cid);
                            if (chief != null)
                                TaleWorlds.CampaignSystem.Actions.ChangeRelationAction.ApplyPlayerRelation(chief, -10, true, true);
                        }
                        catch (Exception rex) { SigLog("betray relation " + rex.GetType().Name + ": " + rex.Message); }
                        SigLog("BETRAYED " + cid + ": trust -1, relation -10");
                        PlayerEncounter.LeaveEncounter = true;
                        break;
                }
            }
            catch (Exception ex) { SigLog("[ERR] OnSignatureResolve " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void OnSignatureWithdraw()
        {
            ClearSignatureCaptainName();
            try { PlayerEncounter.LeaveEncounter = true; } catch { }
        }

        // ---------------- return + closing ----------------

        private BandItPlus.Quests.BanditSignatureQuest GetReturnableSignatureQuest()
        {
            string cid = GetSignatureChiefCulture();
            if (cid == null) return null;
            var q = BandItPlus.Quests.BanditSignatureQuest.FindActiveForCulture(cid);
            return q != null && q.Stage == "return" ? q : null;
        }

        private bool CanReturnSignature()
        {
            try { return SignatureEnabled() && GetReturnableSignatureQuest() != null; }
            catch { return false; }
        }

        private bool HasSignatureStockChoice()
        {
            try
            {
                var q = GetReturnableSignatureQuest();
                var spec = q?.Spec;
                if (q == null || spec == null) return false;
                if (q.Outcome != "battle") return false;
                if (spec.PostBattleChoices == null || spec.PostBattleChoices.Length < 2
                    || string.IsNullOrEmpty(spec.PostBattleChoiceIntro)) return false;
                MBTextManager.SetTextVariable("BP_SIG_STOCK_INTRO", spec.PostBattleChoiceIntro);
                return true;
            }
            catch { return false; }
        }

        private bool SetSignatureStockAVar()
        {
            try
            {
                var spec = GetReturnableSignatureQuest()?.Spec;
                if (spec?.PostBattleChoices == null || spec.PostBattleChoices.Length < 1) return false;
                MBTextManager.SetTextVariable("BP_SIG_STOCK_A", spec.PostBattleChoices[0].Label);
                return true;
            }
            catch { return false; }
        }

        private bool SetSignatureStockBVar()
        {
            try
            {
                var spec = GetReturnableSignatureQuest()?.Spec;
                if (spec?.PostBattleChoices == null || spec.PostBattleChoices.Length < 2) return false;
                MBTextManager.SetTextVariable("BP_SIG_STOCK_B", spec.PostBattleChoices[1].Label);
                return true;
            }
            catch { return false; }
        }

        private void OnSignatureStockA() { PickSignatureStock(0); }
        private void OnSignatureStockB() { PickSignatureStock(1); }

        private void PickSignatureStock(int index)
        {
            try
            {
                var q = GetReturnableSignatureQuest();
                var spec = q?.Spec;
                if (q == null || spec?.PostBattleChoices == null || index >= spec.PostBattleChoices.Length) return;
                _sigStockChoice = spec.PostBattleChoices[index];
                q.Flavor = index == 0 ? "stock-a" : "stock-b";
                SigLog("stock choice " + q.Flavor);
            }
            catch (Exception ex) { SigLog("[ERR] PickSignatureStock " + ex.GetType().Name + ": " + ex.Message); }
        }

        private bool SetSignatureStockResultVar()
        {
            if (_sigStockChoice == null || string.IsNullOrEmpty(_sigStockChoice.ResultProse)) return false;
            MBTextManager.SetTextVariable("BP_SIG_STOCK_RESULT", _sigStockChoice.ResultProse);
            return true;
        }

        private string PickSignatureClosing(BandItPlus.Quests.BanditSignatureQuest q,
            BandItPlus.Cultures.SignatureQuestSpec spec)
        {
            if (q.Outcome == "othercause" && !string.IsNullOrEmpty(spec.ClosingProseOtherCause))
                return spec.ClosingProseOtherCause;
            if (q.Outcome == "half" && !string.IsNullOrEmpty(spec.ClosingProseAlt))
                return spec.ClosingProseAlt;
            if (q.Flavor == "stock-b" && !string.IsNullOrEmpty(spec.ClosingProseAlt))
                return spec.ClosingProseAlt;
            return spec.ClosingProse;
        }

        private bool SetSignatureClosingDirect()
        {
            try
            {
                var q = GetReturnableSignatureQuest();
                var spec = q?.Spec;
                if (q == null || spec == null) return false;
                if (HasSignatureStockChoiceSilent(q, spec)) return false;  // stock path handles closing
                MBTextManager.SetTextVariable("BP_SIG_CLOSING", PickSignatureClosing(q, spec));
                return true;
            }
            catch { return false; }
        }

        private bool HasSignatureStockChoiceSilent(BandItPlus.Quests.BanditSignatureQuest q,
            BandItPlus.Cultures.SignatureQuestSpec spec)
        {
            return q.Outcome == "battle" && spec.PostBattleChoices != null
                && spec.PostBattleChoices.Length >= 2 && !string.IsNullOrEmpty(spec.PostBattleChoiceIntro);
        }

        private bool SetSignatureClosingVar()
        {
            try
            {
                var q = GetReturnableSignatureQuest();
                var spec = q?.Spec;
                if (q == null || spec == null) return false;
                MBTextManager.SetTextVariable("BP_SIG_CLOSING", PickSignatureClosing(q, spec));
                return true;
            }
            catch { return false; }
        }

        private void OnCompleteSignature()
        {
            try
            {
                var q = GetReturnableSignatureQuest();
                var spec = q?.Spec;
                if (q == null || spec == null) return;
                string cid = q.CultureId;
                bool otherCause = q.Outcome == "othercause";

                int gold = spec.RewardGold;
                if (_sigStockChoice != null && _sigStockChoice.GoldToPlayer > 0)
                    gold = _sigStockChoice.GoldToPlayer;
                if (otherCause) gold = gold / 2;
                if (q.Outcome == "half") gold = gold / 2;   // half a toll, half a cut
                if (gold > 0)
                    TaleWorlds.CampaignSystem.Actions.GiveGoldAction.ApplyBetweenCharacters(
                        null, Hero.MainHero, gold, false);

                int rel = spec.RewardRelation;
                if (otherCause && rel > 1) rel = Math.Max(1, rel / 2);
                try
                {
                    Hero chief = q.QuestGiver ?? Hero.OneToOneConversationHero;
                    if (chief != null && rel != 0)
                        TaleWorlds.CampaignSystem.Actions.ChangeRelationAction.ApplyPlayerRelation(chief, rel, true, true);
                }
                catch (Exception rex) { SigLog("closing relation " + rex.GetType().Name + ": " + rex.Message); }

                if (_sigStockChoice != null && _sigStockChoice.BonusRenown > 0)
                {
                    try
                    {
                        TaleWorlds.CampaignSystem.Actions.GainRenownAction.Apply(
                            Hero.MainHero, _sigStockChoice.BonusRenown, false);
                        SigLog("bonus renown +" + _sigStockChoice.BonusRenown);
                    }
                    catch (Exception gex) { SigLog("renown " + gex.GetType().Name + ": " + gex.Message); }
                }

                if (spec.RewardItemIds != null && spec.RewardItemCounts != null)
                {
                    for (int i = 0; i < spec.RewardItemIds.Length && i < spec.RewardItemCounts.Length; i++)
                    {
                        try
                        {
                            var item = TaleWorlds.ObjectSystem.MBObjectManager.Instance
                                .GetObject<TaleWorlds.Core.ItemObject>(spec.RewardItemIds[i]);
                            if (item == null)
                            {
                                SigLog("[ERR] reward item not found: " + spec.RewardItemIds[i]);
                                continue;
                            }
                            int count = spec.RewardItemCounts[i];
                            if (otherCause && count > 1) count = Math.Max(1, count / 2);
                            MobileParty.MainParty.ItemRoster.AddToCounts(item, count);
                            SigLog("reward item " + spec.RewardItemIds[i] + " x" + count);
                        }
                        catch (Exception iex) { SigLog("reward item " + iex.GetType().Name + ": " + iex.Message); }
                    }
                }

                if (spec.RewardFoodVendorTrustBump) IncrementFoodVendorTrust(cid);

                SetSignatureState(cid, 2);
                q.ConcludeHonored("The chief's work is done — " + (spec.Title ?? Localization.Get("bp_dialogmanager_265", "the job")) + ".");
                _sigStockChoice = null;

                InformationManager.DisplayMessage(new InformationMessage(
                    "Signature complete: " + spec.Title + "  (+" + gold + " denars, +" + rel + " relation)",
                    Colors.Yellow));
                SigLog("COMPLETE " + cid + " outcome=" + q.Outcome + " gold=" + gold + " rel=" + rel);
            }
            catch (Exception ex) { SigLog("[ERR] OnCompleteSignature " + ex.GetType().Name + ": " + ex.Message); }
        }

        // ---------------- betrayal aftermath ----------------

        private bool CanAskBetrayedSignature()
        {
            try
            {
                if (!SignatureEnabled()) return false;
                string cid = GetSignatureChiefCulture();
                if (cid == null || GetSignatureState(cid) != 3) return false;
                var spec = GetSignatureSpec(cid);
                return spec != null && !string.IsNullOrEmpty(spec.BetrayedProse);
            }
            catch { return false; }
        }

        private bool SetSignatureBetrayedVar()
        {
            try
            {
                var spec = GetSignatureSpec(GetSignatureChiefCulture());
                if (spec == null || string.IsNullOrEmpty(spec.BetrayedProse)) return false;
                MBTextManager.SetTextVariable("BP_SIG_BETRAYED", spec.BetrayedProse);
                return true;
            }
            catch { return false; }
        }

        private static void SigLog(string msg)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Sig] " + msg); }
            catch { }
        }

        // Vanilla bandit culture IDs - peaceful dialog applies to these too when MCM toggle on.
        private static readonly HashSet<string> VanillaBanditCultures = new HashSet<string>
        {
            "looters", "forest_bandits", "mountain_bandits", "steppe_bandits", "sea_raiders", "desert_bandits"
        };

        private bool IsBpEncounter()
        {
            try
            {
                if (MCMSettings.Instance == null || !MCMSettings.Instance.EnableMod) return false;

                var party = MobileParty.ConversationParty;
                var cultureId = party?.MapFaction?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return false;

                // Match BP clans (bp_*) AND vanilla bandit cultures
                bool banditCulture = cultureId.StartsWith("bp_") || VanillaBanditCultures.Contains(cultureId);
                if (!banditCulture) return false;

                // Signature quests: the quest target party is technically a
                // bandit-culture party but carries its own confrontation tree —
                // the friendly roadside dialog must never fire on it (start-token
                // conditions must stay mutually exclusive).
                if (BandItPlus.Quests.BanditSignatureQuest.IsSignatureTargetParty(party)) return false;

                // Wave 4.20.2 (user bug report: "we cannot fight the looters"):
                // once an ORIGIN is chosen, the roadside parley dialog only fires
                // for cultures the player has EARNED peace with (or whose chief
                // pact is sworn). At war = steel — fall through to the vanilla
                // combat encounter. Peace is made at the hideouts, not the road.
                var origins = BandItPlus.Origins.BanditOriginBehavior.Instance;
                if (origins != null && origins.OriginChosen)
                    return origins.IsAtPeaceWith(cultureId) || IsChiefPactSworn(cultureId);

                // Legacy path (no origin chosen, e.g. pre-origin saves): the old
                // PeacefulEncounters MCM toggle decides.
                return MCMSettings.Instance.PeacefulEncounters;
            }
            catch { return false; }
        }

        // v220 Patch 2 — Notable-context predicate. True when the player is
        // conversing one-on-one with a registered BP chief Hero OUTSIDE the
        // hideout-encounter context (i.e. IsBpEncounter is false). Used by the
        // bp_chief_notable_start bridge to route notable-context conversations
        // into bp_encounter_root so the full alliance dialog tree is reachable
        // when the chief appears as a town notable (e.g. Skarvac walking around
        // Diathma's castle hall — BanditChiefRegistry anchors chief Heroes to
        // a town's clan for encyclopedia visibility, which makes them spawn as
        // notables of that town).
        private bool IsBpChiefAnywhere()
        {
            try
            {
                if (MCMSettings.Instance == null || !MCMSettings.Instance.EnableMod) return false;
                if (IsBpEncounter()) return false; // existing bridge handles this — exclusive

                var partner = Hero.OneToOneConversationHero;
                if (partner == null) return false;

                var registry = BandItPlus.Heroes.BanditChiefRegistry.Instance;
                if (registry == null) return false;

                System.Collections.Generic.IEnumerable<Hero> chiefs = null;
                try { chiefs = registry.GetAllChiefHeroes(); }
                catch (Exception gex)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "[BanditDialogManager] IsBpChiefAnywhere GetAllChiefHeroes threw "
                        + gex.GetType().Name + ": " + gex.Message);
                    return false;
                }
                if (chiefs == null) return false;

                foreach (var c in chiefs)
                {
                    if (c == partner) return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BanditDialogManager] IsBpChiefAnywhere threw "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static string CurrentBanditCultureId()
        {
            // 2026-06-03 v4 multi-source resolver. ConversationParty returns empty in BP
            // hideout peaceful visits (chief is a Hero standing in a scene, NOT a MobileParty
            // on the map). Log evidence: rawId='' source=generic-fallback for both Lore and
            // History helpers. Try the canonical TaleWorlds hero-in-conversation API first,
            // then fall back through chief.Clan.Culture, then chief.Culture, then the legacy
            // ConversationParty path for wilderness encounters where chief commands a party.
            try
            {
                var chief = Hero.OneToOneConversationHero;
                if (chief != null)
                {
                    var c = chief.Clan?.Culture?.StringId;
                    if (!string.IsNullOrEmpty(c)) return c;
                    c = chief.Culture?.StringId;
                    if (!string.IsNullOrEmpty(c)) return c;
                }
                var partyCulture = MobileParty.ConversationParty?.MapFaction?.Culture?.StringId;
                if (!string.IsNullOrEmpty(partyCulture)) return partyCulture;
                return "";
            }
            catch { return ""; }
        }

        // 2026-06-03 vanilla→BP fallback: when the player meets a vanilla bandit chief
        // (forest_bandits/mountain_bandits/sea_raiders/desert_bandits/steppe_bandits) the
        // CultureProfileRegistry only has bp_* keys → direct lookup fails → generic line
        // fired (screenshot evidence: "We are bandits..."). Map each vanilla culture to a
        // thematically-fitting BP culture so every conversation gets rich authored prose.
        private static string ResolveProfileCultureId(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            if (BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.ContainsKey(id)) return id;
            switch (id)
            {
                case "forest_bandits":   return "bp_marsh_stalkers";
                case "mountain_bandits": return "bp_frost_reavers";
                case "sea_raiders":      return "bp_sky_raiders";
                case "desert_bandits":   return "bp_slaver_caravans";
                case "steppe_bandits":   return "bp_steppe_wolves";
                default:                  return id;
            }
        }

        private bool SetLoreVariable()
        {
            var rawId = CurrentBanditCultureId();
            var id = ResolveProfileCultureId(rawId);
            string blurb = null;
            string source = "(none)";
            bool profileFound = false;
            int profileFieldLen = 0;
            if (!string.IsNullOrEmpty(id)
                && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(id, out var prof)
                && prof != null)
            {
                profileFound = true;
                profileFieldLen = prof.ClanHistory?.Length ?? 0;
                if (!string.IsNullOrEmpty(prof.ClanHistory))
                {
                    blurb = prof.ClanHistory;
                    source = "CultureProfile.ClanHistory";
                }
            }
            if (blurb == null && Lore.TryGetValue(id, out var v))
            {
                blurb = v;
                source = "Lore dict";
            }
            if (blurb == null)
            {
                blurb = "{=bp_dialogmanager_266}We are bandits. The lords have a name for us; we have a name for them. We do not get along.";
                source = "generic-fallback";
            }
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                "v4 SetLoreVariable: rawId='" + (rawId ?? "<null>")
                + "' resolvedId='" + (id ?? "<null>")
                + "' profileFound=" + profileFound
                + " profileClanHistoryLen=" + profileFieldLen
                + " source=" + source
                + " resolvedLen=" + (blurb?.Length ?? 0));
            MBTextManager.SetTextVariable("LORE_BLURB", blurb);
            return true;
        }

        private bool IsBpEncounterMenuVisible(MenuCallbackArgs args)
        {
            try
            {
                if (MCMSettings.Instance == null || !MCMSettings.Instance.EnableMod) return false;

                var party = PlayerEncounter.EncounteredMobileParty ?? MobileParty.ConversationParty;
                var cultureId = party?.MapFaction?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return false;

                bool isBandit = cultureId.StartsWith("bp_") || VanillaBanditCultures.Contains(cultureId);
                if (!isBandit) return false;

                // Wave 4.20.2: same origin-aware gate as IsBpEncounter — the
                // "talk" menu option only appears for cultures at earned peace
                // (or sworn pact); at war the encounter is combat-only.
                var origins = BandItPlus.Origins.BanditOriginBehavior.Instance;
                if (origins != null && origins.OriginChosen)
                {
                    if (!(origins.IsAtPeaceWith(cultureId) || IsChiefPactSworn(cultureId)))
                        return false;
                }
                else if (!MCMSettings.Instance.PeacefulEncounters) return false;

                args.optionLeaveType = GameMenuOption.LeaveType.Conversation;
                return true;
            }
            catch { return false; }
        }

        private void OnTalkConsequence(MenuCallbackArgs args)
        {
            try
            {
                var party = PlayerEncounter.EncounteredMobileParty;
                if (party == null) return;
                var leader = party.LeaderHero ?? party.Party?.LeaderHero;
                if (leader != null)
                {
                    CampaignMapConversation.OpenConversation(
                        new ConversationCharacterData(Hero.MainHero.CharacterObject),
                        new ConversationCharacterData(leader.CharacterObject, party.Party));
                }
                else if (party.MemberRoster?.Count > 0)
                {
                    // Fallback: talk to a roster member
                    var ch = party.MemberRoster.GetCharacterAtIndex(0);
                    if (ch != null)
                        CampaignMapConversation.OpenConversation(
                            new ConversationCharacterData(Hero.MainHero.CharacterObject),
                            new ConversationCharacterData(ch, party.Party));
                }
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Talk option error: " + ex.Message, Colors.Red));
            }
        }

        private bool SetHistoryVariable()
        {
            var rawId = CurrentBanditCultureId();
            var id = ResolveProfileCultureId(rawId);
            string blurb = null;
            string source = "(none)";
            bool profileFound = false;
            int profileFieldLen = 0;
            if (!string.IsNullOrEmpty(id)
                && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(id, out var prof)
                && prof != null)
            {
                profileFound = true;
                profileFieldLen = prof.ChiefBackstory?.Length ?? 0;
                if (!string.IsNullOrEmpty(prof.ChiefBackstory))
                {
                    blurb = prof.ChiefBackstory;
                    source = "CultureProfile.ChiefBackstory";
                }
            }
            if (blurb == null && History.TryGetValue(id, out var v))
            {
                blurb = v;
                source = "History dict";
            }
            if (blurb == null)
            {
                blurb = "{=bp_dialogmanager_267}Long ago, our ancestors took to the wilds. Hunger, war, or vengeance drove them — none of us remember which. Now we live by the blade and the bow. The lords' laws do not reach us here.";
                source = "generic-fallback";
            }
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                "v4 SetHistoryVariable: rawId='" + (rawId ?? "<null>")
                + "' resolvedId='" + (id ?? "<null>")
                + "' profileFound=" + profileFound
                + " profileChiefBackstoryLen=" + profileFieldLen
                + " source=" + source
                + " resolvedLen=" + (blurb?.Length ?? 0));
            MBTextManager.SetTextVariable("HISTORY_BLURB", blurb);
            return true;
        }

        private bool SetHideoutVariable()
        {
            try
            {
                // 2026-06-03 v4: same upstream bug as SetLore/SetHistory — ConversationParty
                // is null in BP hideout peaceful visits. Use CurrentBanditCultureId (multi-source
                // resolver) and ResolveProfileCultureId (vanilla→BP map for profile lookup).
                var rawId = CurrentBanditCultureId();
                var profileId = ResolveProfileCultureId(rawId);

                CultureProfile prof = null;
                if (!string.IsNullOrEmpty(profileId))
                    BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(profileId, out prof);

                // Find the nearest hideout for placeholder substitution + map-pointer.
                // Try profileId first (the BP/mapped culture), then rawId (vanilla culture)
                // as fallback — bandit hideouts use vanilla culture ids on the map.
                Settlement nearest = null;
                if (Settlement.All != null && MobileParty.MainParty != null)
                {
                    float nearestDist = float.MaxValue;
                    var here = MobileParty.MainParty.GetPosition2D;
                    foreach (var s in Settlement.All)
                    {
                        if (s == null || !s.IsHideout) continue;
                        var sid = s.Culture?.StringId;
                        if (sid != profileId && sid != rawId) continue;
                        var d = s.GetPosition2D.DistanceSquared(here);
                        if (d < nearestDist) { nearestDist = d; nearest = s; }
                    }
                }
                string hideoutName = nearest?.Name?.ToString() ?? Localization.Get("bp_dialogmanager_268", "the wilds");

                // Build the response:
                //   Tier A (best): per-chief authored HideoutDirections (3 paragraphs,
                //                  voice-distinct, with {HIDEOUT_NAME} substitution).
                //   Tier B (legacy fallback): CampIntro + nearest-settlement pointer.
                //   Tier C (generic fallback): one-liner.
                string blurb;
                string source;
                if (prof != null && !string.IsNullOrEmpty(prof.HideoutDirections))
                {
                    blurb = prof.HideoutDirections.Replace("{HIDEOUT_NAME}", hideoutName);
                    source = "CultureProfile.HideoutDirections";
                }
                else if (prof != null && !string.IsNullOrEmpty(prof.CampIntro))
                {
                    var location = nearest != null
                        ? "Our hideout lies near " + hideoutName + ". Come there if you have business with us - but not as an enemy."
                        : Localization.Get("bp_dialogmanager_269", "Find it yourself. We do not give such secrets to strangers.");
                    blurb = prof.CampIntro + "\n\n" + location;
                    source = "CampIntro+location fallback";
                }
                else
                {
                    blurb = nearest != null
                        ? "Our hideout lies near " + hideoutName + ". Come there if you have business with us - but not as an enemy."
                        : Localization.Get("bp_dialogmanager_269", "Find it yourself. We do not give such secrets to strangers.");
                    source = "generic-fallback";
                }

                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetHideoutVariable: rawId='" + (rawId ?? "<null>")
                    + "' profileId='" + (profileId ?? "<null>")
                    + "' hideoutName='" + hideoutName
                    + "' source=" + source
                    + " resolvedLen=" + (blurb?.Length ?? 0));

                MBTextManager.SetTextVariable("HIDEOUT_BLURB", blurb);
                return true;
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetHideoutVariable fail: " + ex.GetType().Name + ": " + ex.Message);
                MBTextManager.SetTextVariable("HIDEOUT_BLURB", "{=bp_dialogmanager_270}Find it yourself.");
                return true;
            }
        }

        private bool SetLordsVariable()
        {
            // 2026-06-03 v4 story-expansion: 3-tier resolver — per-chief authored prose
            // (CultureProfile.ScoutsLordsProse), then short Lords dict, then generic line.
            var rawId = CurrentBanditCultureId();
            var profileId = ResolveProfileCultureId(rawId);
            string blurb = null;
            string source = "(none)";
            if (!string.IsNullOrEmpty(profileId)
                && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(profileId, out var prof)
                && prof != null
                && !string.IsNullOrEmpty(prof.ScoutsLordsProse))
            {
                blurb = prof.ScoutsLordsProse;
                source = "CultureProfile.ScoutsLordsProse";
            }
            else if (Lords.TryGetValue(rawId, out var v) || Lords.TryGetValue(profileId, out v))
            {
                blurb = v;
                source = "Lords dict";
            }
            else
            {
                blurb = "{=bp_dialogmanager_271}Plenty. They send their levies after us — boys with spears who don't know one end of a horse from the other. We send the survivors back without their boots.";
                source = "generic-fallback";
            }
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                "v4 SetLordsVariable: rawId='" + (rawId ?? "<null>")
                + "' profileId='" + (profileId ?? "<null>")
                + "' source=" + source
                + " resolvedLen=" + (blurb?.Length ?? 0));
            MBTextManager.SetTextVariable("LORDS_BLURB", blurb);
            return true;
        }

        private static CultureProfile GetProfileForConversationParty()
        {
            var id = ResolveProfileCultureId(CurrentBanditCultureId());
            if (string.IsNullOrEmpty(id)) return null;
            CultureProfileRegistry.ByCultureId.TryGetValue(id, out var p);
            return p;
        }

        private bool SetKingdomVariable()
        {
            var profile = GetProfileForConversationParty();
            var blurb = !string.IsNullOrEmpty(profile?.WhyNotKingdom) ? profile.WhyNotKingdom
                      : "{=bp_dialogmanager_272}Kings tax cookfires and call it law. We tax the same caravans and call it survival. The difference is honesty.";
            MBTextManager.SetTextVariable("KINGDOM_BLURB", blurb);
            return true;
        }

        private bool SetReligionVariable()
        {
            var profile = GetProfileForConversationParty();
            var blurb = !string.IsNullOrEmpty(profile?.Religion) ? profile.Religion
                      : "{=bp_dialogmanager_273}Old gods. The kind no priest collects coin for. Out here we pray with our hands, not our knees.";
            MBTextManager.SetTextVariable("RELIGION_BLURB", blurb);
            return true;
        }

        private bool SetPleasantryVariable()
        {
            var profile = GetProfileForConversationParty();
            var blurb = !string.IsNullOrEmpty(profile?.Pleasantry) ? profile.Pleasantry
                      : "{=bp_dialogmanager_274}A grunt of acknowledgement. \"It serves. We've had worse. Most who walk in armed don't notice the camp at all — they notice the boots they leave with.\"";
            MBTextManager.SetTextVariable("PLEASANTRY_BLURB", blurb);
            return true;
        }

        private bool SetThreatVariable()
        {
            var profile = GetProfileForConversationParty();
            var blurb = !string.IsNullOrEmpty(profile?.ThreatResponse) ? profile.ThreatResponse
                      : "{=bp_dialogmanager_275}A dozen blades shift. \"You're outnumbered, friend. Try it. We'll save the boots for the lord who comes looking.\"";
            MBTextManager.SetTextVariable("THREAT_BLURB", blurb);
            return true;
        }

        private static int CurrentBribeAmount()
        {
            var p = GetProfileForConversationParty();
            return p != null ? p.BribeAmount : 200;
        }

        private bool IsBribeAvailable()
        {
            try
            {
                int amt = CurrentBribeAmount();
                MBTextManager.SetTextVariable("BRIBE_AMOUNT", amt);
                return Hero.MainHero != null && Hero.MainHero.Gold >= amt;
            }
            catch { return false; }
        }

        // Wave 4.11-fix v40 (2026-05-07): bribe-gate variant that ALSO requires the player
        // to have built trust>=1 with the chief. Hides the bribe option on first-meet —
        // tonally wrong inside the chief's own camp where the player has just heard the
        // loyalty speech ("prove yourself"). Reappears once the player has earned tolerance.
        private bool IsBribeAvailableAndTrusted()
        {
            return IsBribeAvailable() && GetCurrentEncounterTrust() >= 1;
        }

        // Wave 4.11-fix v56 (2026-05-08): bribe + threat now show ONLY on the campaign-
        // map encounter, never in the walkable-hideout chief tree. Reasoning: when sitting
        // in the chief's own camp as a tolerated guest, threatening or paying him off is
        // tonally bizarre. On the campaign map (meeting his bandit party in the field),
        // both options fit the encounter framing.
        private bool CanThreatenChiefOnMap()
        {
            return !BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Active;
        }

        private bool IsBribeAvailableOnMap()
        {
            return IsBribeAvailable() && !BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Active;
        }

        // v56: sever option ("I think we're done here") fires only inside a walkable
        // hideout where the player has earned at least Tier 1. Lets the player gracefully
        // exit a relationship without combat or coin. Hidden on campaign-map encounter
        // (use farewell or threat there).
        private bool IsCanSeverInCamp()
        {
            return BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Active && IsChiefTier1();
        }

        // 2026-06-03 v4: BP hideout peaceful visits have no MobileParty (chief is a Hero
        // in a scene, not a party leader on the map). ConversationParty.LeaderHero returned
        // null, ChangeRelationAction silent no-op, trust never moved. Fall back to
        // Hero.OneToOneConversationHero (the canonical TaleWorlds API for scene-based
        // chief conversations). Matches the fix pattern from CurrentBanditCultureId v4.
        private static Hero ResolveChiefHero()
        {
            try
            {
                var chief = Hero.OneToOneConversationHero;
                if (chief != null) return chief;
                return MobileParty.ConversationParty?.LeaderHero;
            }
            catch { return null; }
        }

        // v56: chief drink consequence — small relation gain (+1) and a chief log line.
        // Cheap, flavor-rich, fits the Tier 1 "trusted guest" beat.
        private void OnDrinkConsequence()
        {
            try
            {
                var leader = ResolveChiefHero();
                if (leader != null && Hero.MainHero != null)
                {
                    ChangeRelationAction.ApplyPlayerRelation(leader, +1, true, true);
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "Chief drink: relation +1 with chief (" + (leader?.Name?.ToString() ?? "<null>") + ")");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("OnDrinkConsequence fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // v56: chief sever consequence — small relation hit (-1), no faction-state change.
        // The player walks away with cooled goodwill, not a declared feud.
        private void OnSeverConsequence()
        {
            try
            {
                var leader = ResolveChiefHero();
                if (leader != null && Hero.MainHero != null)
                {
                    ChangeRelationAction.ApplyPlayerRelation(leader, -1, true, true);
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "Chief sever: relation -1 with chief (" + (leader?.Name?.ToString() ?? "<null>") + ")");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("OnSeverConsequence fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // === Wave 4.11-fix v58 (2026-05-08) — Tier 3 helpers & consequences ===

        // Trust gate: Tier 3 == trust >= 3. Mirrors IsChiefTier1 / IsChiefTier2 pattern.
        private bool IsChiefTier3() { return GetCurrentEncounterTrust() >= 3; }

        // Brother oath: Tier 3 + not yet taken for this culture (one-shot per culture).
        private bool CanTakeBrotherOath()
        {
            try
            {
                if (!IsChiefTier3()) return false;
                var cid = GetCultureIdForHideout(GetCurrentHideout());
                if (string.IsNullOrEmpty(cid)) return false;
                return !(_chiefOathTaken != null && _chiefOathTaken.TryGetValue(cid, out var taken) && taken);
            }
            catch { return false; }
        }

        // Pact swearing: Tier 3 + not yet sworn for this culture (one-shot per culture).
        private bool CanSwearPact()
        {
            try
            {
                if (!IsChiefTier3()) return false;
                var cid = GetCultureIdForHideout(GetCurrentHideout());
                if (string.IsNullOrEmpty(cid)) return false;
                return !(_pactSworn != null && _pactSworn.TryGetValue(cid, out var sworn) && sworn);
            }
            catch { return false; }
        }

        // === Wave 5 (T29) alliance offer eligibility + accept consequence ============
        // Wave 5 alliance: offer eligibility is whatever BanditAllianceBehavior.IsEligibleForOffer
        // returns — that method is the SINGLE source of truth per spec §0. Reads:
        // _cultureTrust[cultureId] >= 2 AND a Tier-3 BanditTrustQuest has completed AND no
        // active BanditAllianceQuest exists for this chief AND MCM EnableChiefRecruitment is
        // true. Typed catch per memory note csharp-empty-catch-silent-failures.
        private bool IsAllianceOfferEligible()
        {
            try
            {
                string cultureId = GetCurrentEncounterCultureId();
                if (string.IsNullOrEmpty(cultureId)) return false;
                if (BandItPlus.Behaviors.BanditAllianceBehavior.Instance == null) return false;
                return BandItPlus.Behaviors.BanditAllianceBehavior.Instance.IsEligibleForOffer(cultureId);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BanditDialogManager] IsAllianceOfferEligible threw "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // Accept consequence: construct Ch1 quest with a 7-day deadline per spec §4 Ch1
        // mechanics and register it with the alliance behavior. The BanditAllianceQuest
        // ctor itself ALSO calls RegisterActiveQuest internally (Phase 2 wiring), but
        // calling it here as well is a defensive idempotent overwrite — matches the plan
        // and ensures the dispatch table is populated even if a future refactor moves the
        // ctor-side registration. Also registers with the campaign quest manager so the
        // journal entry surfaces (mirrors OnAcceptTrustQuest pattern).
        private void OnAcceptAllianceOffer()
        {
            try
            {
                string cultureId = GetCurrentEncounterCultureId();
                if (string.IsNullOrEmpty(cultureId))
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "[BanditDialogManager] OnAcceptAllianceOffer: cultureId unresolved — abort");
                    return;
                }

                var chiefReg = BandItPlus.Heroes.BanditChiefRegistry.Instance;
                Hero chief = chiefReg?.GetChief(cultureId);

                // v221f — Fallback #1: conversation partner. Often null when the
                // chief is rendered as a non-Hero scene agent (camp boss without
                // a Hero record) — proven null at 14:40:29 in the trap log.
                if (chief == null)
                {
                    Hero partner = Hero.OneToOneConversationHero;
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "[BanditDialogManager] OnAcceptAllianceOffer: GetChief returned null for "
                        + cultureId + " — fallback #1 (conversation partner): "
                        + (partner?.Name?.ToString() ?? "<null>"));
                    chief = partner;
                }

                // v221h — Fallback #2 REWRITTEN: find the chief by SETTLEMENT match,
                // not culture-string match. The player is inside a hideout (they
                // walked into camp via the BP encounter); EnforceChiefAtHideouts
                // keeps every chief Hero at their hideout settlement. So the chief
                // whose CurrentSettlement == player's CurrentSettlement IS the chief
                // the player is talking to — regardless of culture-string drift between
                // BP-authored chief.Culture (e.g. "bp_marsh_stalkers") and the
                // dialog-context cultureId (e.g. "forest_bandits") which can differ
                // when BP synthesizes a chief with its own culture but maps it to a
                // vanilla bandit culture in CultureProfileRegistry.
                if (chief == null && chiefReg != null)
                {
                    try
                    {
                        Settlement playerSettlement = MobileParty.MainParty?.CurrentSettlement;
                        var allChiefs = chiefReg.GetAllChiefHeroes();

                        if (playerSettlement != null && allChiefs != null)
                        {
                            foreach (var h in allChiefs)
                            {
                                if (h == null || h.IsDead || h.IsPrisoner) continue;
                                if (h.CurrentSettlement == playerSettlement)
                                {
                                    chief = h;
                                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                                        "[BanditDialogManager] OnAcceptAllianceOffer: fallback #2 ("
                                        + "GetAllChiefHeroes settlement-match) found "
                                        + (h.Name?.ToString() ?? "<unnamed>")
                                        + " at " + playerSettlement.StringId
                                        + " for cultureId=" + cultureId);
                                    break;
                                }
                            }
                        }

                        // Fallback #2b — if no chief is co-located (chief may have
                        // wandered off between EnforceChiefAtHideouts ticks), iterate
                        // by culture-string as the secondary lookup.
                        if (chief == null && allChiefs != null)
                        {
                            foreach (var h in allChiefs)
                            {
                                if (h == null || h.IsDead || h.IsPrisoner) continue;
                                if (h.Culture != null && h.Culture.StringId == cultureId)
                                {
                                    chief = h;
                                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                                        "[BanditDialogManager] OnAcceptAllianceOffer: fallback #2b ("
                                        + "GetAllChiefHeroes culture-string match) found "
                                        + (h.Name?.ToString() ?? "<unnamed>") + " for " + cultureId);
                                    break;
                                }
                            }
                        }

                        // Fallback #2c — last resort: log what we DID find so we can
                        // diagnose. Print every chief's StringId + Culture + CurrentSettlement.
                        if (chief == null && allChiefs != null)
                        {
                            var diag = new System.Text.StringBuilder();
                            diag.Append("[BanditDialogManager] OnAcceptAllianceOffer: fallback #2c diag — chiefs in registry:");
                            foreach (var h in allChiefs)
                            {
                                if (h == null) { diag.Append(" <null>;"); continue; }
                                diag.Append(" ").Append(h.StringId ?? "?")
                                    .Append(" name=").Append(h.Name?.ToString() ?? "?")
                                    .Append(" cult=").Append(h.Culture?.StringId ?? "?")
                                    .Append(" at=").Append(h.CurrentSettlement?.StringId ?? "<map>")
                                    .Append(" dead=").Append(h.IsDead).Append(";");
                            }
                            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(diag.ToString());
                            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                                "[BanditDialogManager] OnAcceptAllianceOffer: requested cultureId="
                                + cultureId + " playerSettlement=" + (playerSettlement?.StringId ?? "<null>"));
                        }
                    }
                    catch (Exception fbEx)
                    {
                        BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                            "[BanditDialogManager] OnAcceptAllianceOffer: fallback #2 threw "
                            + fbEx.GetType().Name + ": " + fbEx.Message);
                    }
                }

                // v221i — Fallback #3 (LAST RESORT): use Hero.MainHero as the
                // quest-giver. Sibling quests like BanditVendorQuest don't need a
                // Hero (they're cultureId-only), but QuestBase's ctor requires a
                // non-null Hero for the journal portrait. Using MainHero gets the
                // quest CREATED — journal will show the player's portrait instead
                // of the chief's, but the mechanic works and the player can play
                // through. Cosmetic fix can land later when chief synthesis is
                // straightened out. The cultureId is the canonical identity for
                // alliance bookkeeping (BanditAllianceBehavior._activeQuestByChief),
                // not the questGiver Hero — so this is purely a portrait substitute.
                if (chief == null)
                {
                    chief = Hero.MainHero;
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "[BanditDialogManager] OnAcceptAllianceOffer: fallback #3 (LAST RESORT) — "
                        + "using Hero.MainHero as quest-giver for " + cultureId
                        + " — journal portrait will be the player; mechanic still works.");
                }

                if (chief == null)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "[BanditDialogManager] OnAcceptAllianceOffer: NO chief resolvable for "
                        + cultureId + " (all fallbacks failed including MainHero) — abort");
                    return;
                }

                // v219 tuning (2026-06-01): bumped Ch1 timer from 7 days → 25 days.
                // Rationale: Pattern B's filter-watch mode can require significant world
                // travel + patrol time to encounter a same-culture party in the chief's
                // region; 7 days was too tight when starting far from the chief. Ch2 idle
                // timers (PreTrial / InTrial) stay at 7 days — those measure "talk-then-
                // fight" not "find target". Stored on the quest as a double via
                // CampaignTime.ToHours per memory note bannerlord-campaigntime-tohours.
                CampaignTime ch1Deadline = CampaignTime.HoursFromNow(25f * 24f);

                var quest = new BandItPlus.Quests.BanditAllianceQuest(chief, cultureId, ch1Deadline);

                // StartQuest, not QuestManager.OnQuestStarted (07-03 signature-quest
                // lesson, decompile-verified): StartQuest is what runs RegisterEvents,
                // so the pattern A/B/C detectors work in the accepting session instead
                // of waking up only after a reload.
                quest.StartQuest();

                // Idempotent overwrite into the alliance behavior's dispatch table.
                // The BanditAllianceQuest ctor also does this, but the plan calls for it
                // explicitly here so the registration is robust against ctor refactors.
                BandItPlus.Behaviors.BanditAllianceBehavior.Instance?.RegisterActiveQuest(cultureId, quest);

                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BanditDialogManager] OnAcceptAllianceOffer: alliance quest created for cultureId=" + cultureId
                    + " chief=" + (chief?.Name?.ToString() ?? "<null>"));
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BanditDialogManager] OnAcceptAllianceOffer threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // === Wave 5 (T30) Ch1-accept brief variable + Ch2 pre-trial gate ===============
        // Sets BP_ALLIANCE_CH1_BRIEF to the per-culture task brief from CultureProfileRegistry
        // so the soft-fail telegraph line can lead with the chief's own description of the
        // task. Always returns true (the line should always render after the accept choice);
        // side-effect is the text-variable mutation. Typed catch per memory note
        // csharp-empty-catch-silent-failures.
        private bool SetAllianceCh1BriefVariable()
        {
            try
            {
                string cultureId = GetCurrentEncounterCultureId();
                BandItPlus.Cultures.CultureProfile profile = null;
                if (!string.IsNullOrEmpty(cultureId))
                {
                    BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out profile);
                }
                string brief = profile?.AllianceCh1TaskBrief ?? "{=bp_dialogmanager_276}Prove your worth.";
                MBTextManager.SetTextVariable("BP_ALLIANCE_CH1_BRIEF", brief, false);
                return true;
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BanditDialogManager] SetAllianceCh1BriefVariable threw "
                    + ex.GetType().Name + ": " + ex.Message);
                MBTextManager.SetTextVariable("BP_ALLIANCE_CH1_BRIEF", "{=bp_dialogmanager_276}Prove your worth.", false);
                return true;
            }
        }

        // Gate for the Ch2 ready-for-trial player line: visible only when the chief's
        // active alliance quest is in Ch2_PreTrial (chapter==2 AND !IsInTrial per the
        // Section 4 state machine). Returns false defensively on any null/throw —
        // mutually exclusive with bp_alliance_offer_player (which requires no active
        // quest) and any future Ch3-allied branch.
        private bool IsAllianceCh2PreTrial()
        {
            try
            {
                string cultureId = GetCurrentEncounterCultureId();
                if (string.IsNullOrEmpty(cultureId)) return false;
                if (BandItPlus.Behaviors.BanditAllianceBehavior.Instance == null) return false;

                BandItPlus.Quests.BanditAllianceQuest q =
                    BandItPlus.Behaviors.BanditAllianceBehavior.Instance.GetActiveQuest(cultureId);
                if (q == null) return false;

                return q.Chapter == 2 && !q.IsInTrial;
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BanditDialogManager] IsAllianceCh2PreTrial threw "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // T31 — Ch2 accept consequence. Routes through the quest's public BeginTrial()
        // wrapper which delegates to the existing private AdvanceToCh2InTrial state
        // transition (chief into MainParty.MemberRoster + target party spawn + journal
        // log). Typed catch + log per [csharp-empty-catch-silent-failures].
        private void OnAcceptCh2Trial()
        {
            try
            {
                string cultureId = GetCurrentEncounterCultureId();
                if (string.IsNullOrEmpty(cultureId)) return;
                if (BandItPlus.Behaviors.BanditAllianceBehavior.Instance == null) return;

                BandItPlus.Quests.BanditAllianceQuest q =
                    BandItPlus.Behaviors.BanditAllianceBehavior.Instance.GetActiveQuest(cultureId);
                if (q == null)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "[BanditDialogManager] OnAcceptCh2Trial: no active quest for cultureId=" + cultureId);
                    return;
                }

                q.BeginTrial();
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BanditDialogManager] alliance Ch2_InTrial started for cultureId=" + cultureId);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BanditDialogManager] OnAcceptCh2Trial threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // T31 — gate for the post-alliance "How fares the alliance?" player line.
        // True when the chief's culture has an active alliance AND no in-trial quest
        // is outstanding (chief would be in player's party then, not in camp). Mutually
        // exclusive with IsEligibleForOffer (which requires !IsAllied) and with the
        // Ch2 prompts (which require an active quest).
        private bool IsAllied_ForChat()
        {
            try
            {
                string cultureId = GetCurrentEncounterCultureId();
                if (string.IsNullOrEmpty(cultureId)) return false;
                if (BandItPlus.Behaviors.BanditAllianceBehavior.Instance == null) return false;
                if (!BandItPlus.Behaviors.BanditAllianceBehavior.Instance.IsAllied(cultureId)) return false;

                // Exclude the in-trial window — chief is travelling with the player, not in his camp.
                BandItPlus.Quests.BanditAllianceQuest q =
                    BandItPlus.Behaviors.BanditAllianceBehavior.Instance.GetActiveQuest(cultureId);
                if (q != null && q.IsInTrial) return false;

                return true;
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BanditDialogManager] IsAllied_ForChat threw "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // === Wave 5 / Phase 5 / T32 — Summon Warband dialog gates ===

        // True when player is allied AND cooldown ready. Mirror condition of
        // IsAllied_SummonOnCooldown so only ONE of the two prompt lines ever shows.
        private bool IsAllied_CanSummon()
        {
            try
            {
                string cultureId = GetCurrentEncounterCultureId();
                if (string.IsNullOrEmpty(cultureId)) return false;
                if (BandItPlus.Behaviors.BanditAllianceBehavior.Instance == null) return false;
                if (!BandItPlus.Behaviors.BanditAllianceBehavior.Instance.IsAllied(cultureId)) return false;
                return BandItPlus.Behaviors.BanditAllianceBehavior.Instance.CanSummonWarband(cultureId);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BanditDialogManager] IsAllied_CanSummon threw "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // True when player is allied AND cooldown NOT ready. Sets BP_SUMMON_DENY to a
        // human-readable line for the chief's response. Calls TrySummonWarband purely
        // to read its denyReason token — per Phase 2 contract, the cooldown-not-ready
        // path early-returns BEFORE committing the cooldown stamp, so this is safe.
        private bool IsAllied_SummonOnCooldown()
        {
            try
            {
                string cultureId = GetCurrentEncounterCultureId();
                if (string.IsNullOrEmpty(cultureId)) return false;
                if (BandItPlus.Behaviors.BanditAllianceBehavior.Instance == null) return false;
                if (!BandItPlus.Behaviors.BanditAllianceBehavior.Instance.IsAllied(cultureId)) return false;
                if (BandItPlus.Behaviors.BanditAllianceBehavior.Instance.CanSummonWarband(cultureId)) return false;

                string denyReason;
                BandItPlus.Behaviors.BanditAllianceBehavior.Instance.TrySummonWarband(cultureId, out denyReason);
                MBTextManager.SetTextVariable("BP_SUMMON_DENY", FormatSummonDenyReason(denyReason), false);
                return true;
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BanditDialogManager] IsAllied_SummonOnCooldown threw "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // Consequence — actually attempt the summon. On success, BP_SUMMON_RESULT gets
        // the affirmative line; on race-failure (cooldown ticked between condition and
        // consequence, or chief just died), BP_SUMMON_RESULT gets the human-formatted
        // denial. Full party-spawn lifecycle is Phase 6 T36/T37; today this only commits
        // the cooldown stamp per Phase 2 T12.
        private void OnSummonWarbandAccept()
        {
            try
            {
                string cultureId = GetCurrentEncounterCultureId();
                if (string.IsNullOrEmpty(cultureId)) return;
                if (BandItPlus.Behaviors.BanditAllianceBehavior.Instance == null) return;

                string denyReason;
                bool ok = BandItPlus.Behaviors.BanditAllianceBehavior.Instance.TrySummonWarband(cultureId, out denyReason);
                if (ok)
                {
                    MBTextManager.SetTextVariable("BP_SUMMON_RESULT",
                        "{=bp_dialogmanager_277}It is done. The horn is wound. They ride from the hills with me — for one fight, and one only. Lead them well.",
                        false);
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "[BanditDialogManager] summon warband OK for cultureId=" + cultureId);
                }
                else
                {
                    MBTextManager.SetTextVariable("BP_SUMMON_RESULT", FormatSummonDenyReason(denyReason), false);
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "[BanditDialogManager] summon warband DENIED for cultureId="
                        + cultureId + ": " + (denyReason ?? "<null>"));
                }
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BanditDialogManager] OnSummonWarbandAccept threw "
                    + ex.GetType().Name + ": " + ex.Message);
                MBTextManager.SetTextVariable("BP_SUMMON_RESULT",
                    "{=bp_dialogmanager_278}Something is amiss. Return another day.", false);
            }
        }

        // BanditAllianceBehavior emits machine-readable deny tokens
        // (e.g. "cooldown_remaining_hours=42.3", "chief_prisoner", "chief_dead").
        // Translate them to in-character lines the player actually sees.
        private string FormatSummonDenyReason(string token)
        {
            try
            {
                if (string.IsNullOrEmpty(token))
                    return "{=bp_dialogmanager_279}My boys cannot rally so soon. Return in a few days.";

                if (token.StartsWith("cooldown_remaining_hours=", StringComparison.Ordinal))
                {
                    double hoursRemaining = 0.0;
                    string val = token.Substring("cooldown_remaining_hours=".Length);
                    double.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out hoursRemaining);
                    int daysRemaining = (int)Math.Ceiling(hoursRemaining / 24.0);
                    if (daysRemaining <= 1)
                        return "{=bp_dialogmanager_280}My boys rode hard for you. Give them a day before you ask again.";
                    return "My boys rode hard for you. Give them " + daysRemaining + " more days before you ask again.";
                }

                if (token == "chief_dead")
                    return "{=bp_dialogmanager_281}(The chief's name is no longer spoken.)";
                if (token == "chief_prisoner")
                    return "{=bp_dialogmanager_282}My chief is in chains. There is no one to wind the horn.";
                if (token == "chief_null" || token == "chief_lookup_failed")
                    return "{=bp_dialogmanager_283}The chief is not here. Find him first.";
                if (token == "warband_already_summoned")
                    return "{=bp_dialogmanager_284}My boys are already at your back. Lead them — don't ask twice.";
                if (token == "not_allied")
                    return "{=bp_dialogmanager_285}We have no oath between us, traveler. Ride on.";

                return "{=bp_dialogmanager_279}My boys cannot rally so soon. Return in a few days.";
            }
            catch
            {
                return "{=bp_dialogmanager_279}My boys cannot rally so soon. Return in a few days.";
            }
        }

        // T31 — populates BP_ALLIANCE_AGE for the post-alliance reply line. Displays
        // the alliance age in seasons (Calradian season ≈ 84 days per vanilla). Returns
        // true unconditionally so the dialog line ALWAYS renders even if the helper
        // logs an error — fallback text "many a" still reads naturally in the line.
        private bool SetAllianceAgeVariable()
        {
            try
            {
                string cultureId = GetCurrentEncounterCultureId();
                double ageHours = BandItPlus.Behaviors.BanditAllianceBehavior.Instance.GetAllianceAgeHours(cultureId);
                int seasons = (int)(ageHours / (24.0 * 84.0));
                string ageLabel;
                if (seasons <= 0)
                    ageLabel = "{=bp_dialogmanager_286}less than a season";
                else if (seasons == 1)
                    ageLabel = "{=bp_dialogmanager_287}one season";
                else
                    ageLabel = seasons + " seasons";
                MBTextManager.SetTextVariable("BP_ALLIANCE_AGE", ageLabel, false);
                return true;
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BanditDialogManager] SetAllianceAgeVariable threw "
                    + ex.GetType().Name + ": " + ex.Message);
                MBTextManager.SetTextVariable("BP_ALLIANCE_AGE", "{=bp_dialogmanager_288}many a", false);
                return true;
            }
        }

        // Troop loan: Tier 3 + no active loan currently outstanding. One global active
        // loan at a time across all cultures keeps the mechanic from being spammable
        // and the saved state minimal.
        private bool CanRequestTroopLoan()
        {
            try
            {
                if (!IsChiefTier3()) return false;
                return string.IsNullOrEmpty(_activeTroopLoanCulture);
            }
            catch { return false; }
        }

        // Sets BURDEN_BLURB from CultureProfile.ChiefBackstory (already authored in
        // Wave 4.10). Pulls the first 2 sentences as the "monologue" — the rest is
        // available in the encyclopedia for players who want the full bio.
        private bool SetBurdensVariable()
        {
            try
            {
                var hideout = GetCurrentHideout();
                var cultureId = GetCultureIdForHideout(hideout);
                if (string.IsNullOrEmpty(cultureId)) return false;
                if (!Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out var profile) || profile == null) return false;
                string backstory = profile.ChiefBackstory ?? "";
                // Trim to first 2 sentences for the monologue beat — full backstory
                // is already in the chief's encyclopedia entry.
                int firstDot = backstory.IndexOf('.');
                int secondDot = (firstDot >= 0) ? backstory.IndexOf('.', firstDot + 1) : -1;
                string monologue = (secondDot > 0)
                    ? backstory.Substring(0, secondDot + 1)
                    : backstory;
                if (string.IsNullOrEmpty(monologue))
                    monologue = "{=bp_dialogmanager_289}There are things I carry, traveler — but they sit better in my chest than in your ears. Another fire. Another night.";
                MBTextManager.SetTextVariable("BURDEN_BLURB", monologue);
            }
            catch { MBTextManager.SetTextVariable("BURDEN_BLURB", "{=bp_dialogmanager_290}Some burdens travel with a man until they go in the earth with him, traveler. Mine are like that."); }
            return true;
        }

        // === Tier 3 consequences ===

        private void OnTakeBrotherOath()
        {
            try
            {
                var cid = GetCultureIdForHideout(GetCurrentHideout());
                if (string.IsNullOrEmpty(cid) || _chiefOathTaken == null) return;
                _chiefOathTaken[cid] = true;
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "Tier3: brother-oath sworn for " + cid);
                InformationManager.DisplayMessage(new InformationMessage(
                    Localization.Get("bp_dialogmanager_291", "[BandIt Plus] You and the chief have sworn the bandit-oath. The clan names you brother now."),
                    Colors.Green));
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("OnTakeBrotherOath fail: " + ex.Message);
            }
        }

        private void OnSwearPact()
        {
            try
            {
                var cid = GetCultureIdForHideout(GetCurrentHideout());
                if (string.IsNullOrEmpty(cid) || _pactSworn == null) return;
                _pactSworn[cid] = true;

                // Wave 4.11-fix v62 (2026-05-08): pact swearing now produces a visible
                // one-shot reward — relation buff with the chief clan + 500 denar gift.
                // Transforms the previously narrative-only pact line into a real moment
                // the player remembers. The recurring mechanic (clan won't engage in
                // hostile encounters) is a separate Harmony-patch iteration.
                var party = MobileParty.ConversationParty;
                var leader = party?.LeaderHero;
                if (leader != null && Hero.MainHero != null)
                {
                    ChangeRelationAction.ApplyPlayerRelation(leader, +20, true, true);
                }
                if (Hero.MainHero != null)
                {
                    GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, 500, false);
                }

                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "Tier3: pact sworn for " + cid + " — relation +20, gold +500");
                InformationManager.DisplayMessage(new InformationMessage(
                    Localization.Get("bp_dialogmanager_292", "[BandIt Plus] The chief has sworn his pact. His clan stands with you when steel is drawn. (+20 relation, +500 denars)"),
                    Colors.Green));
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("OnSwearPact fail: " + ex.Message);
            }
        }

        private void OnAskBurdens()
        {
            try
            {
                var party = MobileParty.ConversationParty;
                var leader = party?.LeaderHero;
                if (leader != null && Hero.MainHero != null)
                {
                    ChangeRelationAction.ApplyPlayerRelation(leader, +5, true, true);
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("Tier3: burdens asked, relation +5");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("OnAskBurdens fail: " + ex.Message);
            }
        }

        // === Tier 3 troop loan: real bandits added to player's party for 7 days. ===
        // Picks a culture-appropriate vanilla bandit CharacterObject (T2/T3 brigand-class).
        // Daily-tick listener (registered in Wave 4.11-fix v58 RegisterEvents) removes
        // them on the return date.
        private const int kTroopLoanCount = 10;
        private const int kTroopLoanDurationDays = 7;

        private static string GetCultureBanditTroopId(string cultureId)
        {
            switch (cultureId)
            {
                case "looters":                  return "looter";
                case "forest_bandits":           return "forest_brigand";
                case "mountain_bandits":         return "mountain_brigand";
                case "sea_raiders":              return "sea_raider";
                case "desert_bandits":           return "desert_bandit";
                case "steppe_bandits":           return "steppe_bandit";
                case "bp_frost_reavers":         return "sea_raider";
                case "bp_marsh_stalkers":        return "forest_brigand";
                case "bp_highwaymen":            return "mountain_brigand";
                case "bp_slaver_caravans":      return "desert_bandit";
                case "bp_fallen_legionaries":   return "looter";
                case "bp_sky_raiders":           return "mountain_brigand";
                case "bp_steppe_wolves":         return "steppe_bandit";
                case "bp_pagan_cult":            return "forest_bandit";
                default:                          return "looter";
            }
        }

        private void OnAcceptTroopLoan()
        {
            try
            {
                var cid = GetCultureIdForHideout(GetCurrentHideout());
                if (string.IsNullOrEmpty(cid)) return;
                var party = MobileParty.MainParty;
                if (party == null) return;

                var troopId = GetCultureBanditTroopId(cid);
                var troop = MBObjectManager.Instance.GetObject<CharacterObject>(troopId);
                if (troop == null)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "TroopLoan: failed — bandit troop id '" + troopId + "' not found for " + cid);
                    return;
                }

                party.MemberRoster.AddToCounts(troop, kTroopLoanCount, false);
                _activeTroopLoanCulture = cid;
                _activeTroopLoanCount = kTroopLoanCount;
                _activeTroopLoanReturnDate = CampaignTime.DaysFromNow(kTroopLoanDurationDays);

                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "Tier3: troop loan started — " + kTroopLoanCount + " " + troopId + " from " + cid + " for " + kTroopLoanDurationDays + " days");
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] " + kTroopLoanCount + " " + troop.Name + " join your party for " + kTroopLoanDurationDays + " days.",
                    Colors.Green));
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("OnAcceptTroopLoan fail: " + ex.Message);
            }
        }

        // === Wave 4.11-fix v61 (2026-05-08) — chief Tier 3 named-target hunt ===

        // Selected target Hero, set in SetNamedTargetVariable and consumed by
        // OnAcceptNamedTargetHunt. Static so the condition pre-stage and the
        // consequence post-stage agree on which Hero was just announced to the
        // player. Cleared after consume to avoid stale picks across visits.
        private static Hero _pendingNamedTarget;
        private const int kNamedTargetReward = 5000;

        // 2026-06-03 story-expansion: Tier-3 raid-village quest helpers.
        // Pending fields set in SetRaidVillageVariable, consumed in OnAcceptRaidVillage.
        private static Settlement _pendingRaidVillage;
        private static Hero _pendingRaidLord;
        // 2026-06-03 raid-village reward balanced lower than kill-noble (5000 denars)
        // because the player already pockets ~3000-6000 of natural raid loot during the
        // raid action itself. Net take after chief bounty ~6500-9500.
        // Quarter-pay (other-cause) = 875.
        private const int kRaidVillageReward = 3500;

        // Visibility gate: Tier 3 + no active BanditRaidVillageQuest for THIS chief's culture.
        private bool CanRequestRaidVillage()
        {
            try
            {
                if (!IsChiefTier3()) return false;
                var cid = CurrentBanditCultureId();
                if (string.IsNullOrEmpty(cid)) return false;
                var resolved = ResolveProfileCultureId(cid);
                var quests = Campaign.Current?.QuestManager?.Quests;
                if (quests == null) return true;
                foreach (var q in quests)
                {
                    if (q is BandItPlus.Quests.BanditRaidVillageQuest rq
                        && rq.IsOngoing
                        && (rq.CultureId == cid || rq.CultureId == resolved))
                    {
                        return false;       // one open raid-village contract at a time per chief
                    }
                }
                return true;
            }
            catch { return false; }
        }

        // Pick a random village whose owner-lord is NOT same culture as the chief, alive,
        // belongs to a major faction. Store in pending fields; consumed by OnAcceptRaidVillage.
        private bool SetRaidVillageVariable()
        {
            try
            {
                _pendingRaidVillage = null;
                _pendingRaidLord = null;
                var rawId = CurrentBanditCultureId();
                var profileId = ResolveProfileCultureId(rawId);
                var candidates = new System.Collections.Generic.List<Settlement>();
                if (Settlement.All != null)
                {
                    foreach (var s in Settlement.All)
                    {
                        if (s == null || !s.IsVillage) continue;
                        if (s.Village == null) continue;
                        var owner = s.OwnerClan?.Leader;
                        if (owner == null || !owner.IsAlive) continue;
                        if (owner == Hero.MainHero) continue;
                        if (owner.MapFaction == null) continue;
                        if (owner.MapFaction.IsBanditFaction || owner.MapFaction.IsMinorFaction) continue;
                        // Don't pick a village whose owner's faction culture matches the chief's
                        // tributed kingdom — for the v1 random pick, any major-faction village qualifies.
                        candidates.Add(s);
                    }
                }
                if (candidates.Count == 0)
                {
                    MBTextManager.SetTextVariable("RAID_VILLAGE_BLURB",
                        "{=bp_dialogmanager_293}Quiet season, traveler. No village in my reach is worth your torch this moon. Come back when the lords ride more openly.");
                    return true;
                }
                var pick = candidates[MBRandom.RandomInt(candidates.Count)];
                _pendingRaidVillage = pick;
                _pendingRaidLord = pick.OwnerClan?.Leader;

                string vName = pick.Name?.ToString() ?? Localization.Get("bp_dialogmanager_294", "the marked village");
                string lordName = _pendingRaidLord?.Name?.ToString() ?? Localization.Get("bp_dialogmanager_295", "the marked lord");
                // Word-form matches kRaidVillageReward (3500). If the constant changes,
                // update this string too so accept-speech prose reads naturally.
                string rewardWords = Localization.Get("bp_dialogmanager_296", "three thousand five hundred");

                BandItPlus.Cultures.CultureProfile prof = null;
                if (!string.IsNullOrEmpty(profileId))
                    BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(profileId, out prof);
                string template = null;
                if (prof != null && !string.IsNullOrEmpty(prof.RaidVillageAcceptProse))
                    template = prof.RaidVillageAcceptProse;
                else
                    template = Localization.Get("bp_dialogmanager_297", "There is a village called {VILLAGE}, held by Lord {LORD}. Put it to the torch and come back with the smoke in your cloak - {REWARD} denars at this fire when the deed is done.");

                var blurb = template.Replace("{VILLAGE}", vName).Replace("{LORD}", lordName).Replace("{REWARD}", rewardWords);
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetRaidVillageVariable: village=" + vName + " lord=" + lordName + " profileId=" + profileId);
                MBTextManager.SetTextVariable("RAID_VILLAGE_BLURB", blurb);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("SetRaidVillageVariable fail: " + ex.GetType().Name + ": " + ex.Message);
                MBTextManager.SetTextVariable("RAID_VILLAGE_BLURB", "{=bp_dialogmanager_298}Some other day, traveler. No village calls for fire today.");
            }
            return true;
        }

        // Spawn the BanditRaidVillageQuest consuming the picked village.
        private void OnAcceptRaidVillage()
        {
            try
            {
                if (_pendingRaidVillage == null || _pendingRaidLord == null) return;
                var hideout = GetCurrentHideout();
                if (hideout == null) return;
                var cultureId = GetCultureIdForHideout(hideout);
                if (string.IsNullOrEmpty(cultureId)) return;

                var chiefReg = BandItPlus.Heroes.BanditChiefRegistry.Instance;
                Hero questGiver = chiefReg?.GetChief(cultureId);
                if (questGiver == null) questGiver = hideout.OwnerClan?.Leader ?? Hero.MainHero;

                string questId = "bp_raidvillage_" + cultureId + "_"
                    + (_pendingRaidVillage.StringId ?? "x") + "_"
                    + (long)CampaignTime.Now.ToHours;
                var rq = new BandItPlus.Quests.BanditRaidVillageQuest(
                    questId, questGiver, CampaignTime.Never, kRaidVillageReward,
                    cultureId, _pendingRaidVillage.StringId, _pendingRaidLord.StringId);
                // StartQuest runs RegisterEvents — raid detectors live same-session
                // (07-03 signature-quest lesson).
                rq.StartQuest();
                rq.InitJournalEntries();

                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "Tier3 raid-village accepted: village='" + _pendingRaidVillage.StringId
                    + "' lord='" + _pendingRaidLord.StringId + "' for " + cultureId);
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] You've taken a raid-village contract. Target: "
                    + (_pendingRaidVillage.Name?.ToString() ?? "?") + ".",
                    Colors.Green));
                _pendingRaidVillage = null;
                _pendingRaidLord = null;
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("OnAcceptRaidVillage fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Collect-bounty visibility: an active BanditRaidVillageQuest for THIS chief in pending state.
        private bool CanCollectRaidVillageBounty()
        {
            try
            {
                var cid = CurrentBanditCultureId();
                if (string.IsNullOrEmpty(cid)) return false;
                var resolved = ResolveProfileCultureId(cid);
                var quests = Campaign.Current?.QuestManager?.Quests;
                if (quests == null) return false;
                foreach (var q in quests)
                {
                    if (q is BandItPlus.Quests.BanditRaidVillageQuest rq
                        && rq.IsOngoing
                        && rq.IsAwaitingChiefReturn
                        && (rq.CultureId == cid || rq.CultureId == resolved))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        private bool SetCollectRaidVillageVariable()
        {
            try
            {
                BandItPlus.Quests.BanditRaidVillageQuest match = null;
                var cid = CurrentBanditCultureId();
                var resolved = ResolveProfileCultureId(cid);
                var quests = Campaign.Current?.QuestManager?.Quests;
                if (quests != null)
                {
                    foreach (var q in quests)
                    {
                        if (q is BandItPlus.Quests.BanditRaidVillageQuest rq
                            && rq.IsOngoing
                            && rq.IsAwaitingChiefReturn
                            && (rq.CultureId == cid || rq.CultureId == resolved))
                        {
                            match = rq; break;
                        }
                    }
                }
                if (match == null)
                {
                    MBTextManager.SetTextVariable("COLLECT_RAID_VILLAGE_BLURB",
                        "{=bp_dialogmanager_299}Speak plain, traveler. Either the village burned or it didn't.");
                    return true;
                }
                string chiefName = match.QuestGiver?.Name?.ToString() ?? Localization.Get("bp_dialogmanager_179", "the chief");
                string vName = match.TargetVillage?.Name?.ToString() ?? Localization.Get("bp_dialogmanager_294", "the marked village");
                string lordName = match.TargetLord?.Name?.ToString() ?? Localization.Get("bp_dialogmanager_295", "the marked lord");
                int fullReward = match.RewardGoldAmount;
                int reducedReward = fullReward / 4;
                string rewardWords = fullReward > 0 ? fullReward.ToString("N0") : Localization.Get("bp_dialogmanager_300", "the agreed");
                string reducedRewardWords = reducedReward > 0 ? reducedReward.ToString("N0") : Localization.Get("bp_dialogmanager_301", "thin coin");

                BandItPlus.Cultures.CultureProfile prof = null;
                if (!string.IsNullOrEmpty(match.CultureId))
                    BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(match.CultureId, out prof);

                string template = null;
                if (prof != null && match.RaidedByOtherCause && !string.IsNullOrEmpty(prof.RaidVillageOtherCauseProse))
                    template = prof.RaidVillageOtherCauseProse;
                else if (prof != null && !match.RaidedByOtherCause && !string.IsNullOrEmpty(prof.RaidVillageComplete))
                    template = prof.RaidVillageComplete;
                else if (match.RaidedByOtherCause)
                    template = Localization.Get("bp_dialogmanager_302", "{VILLAGE} burns - but not by your hand. {CHIEF} counts out {REDUCED_REWARD} denars and sets the bond a smaller notch firmer.");
                else
                    template = Localization.Get("bp_dialogmanager_303", "{VILLAGE} burns. {CHIEF} hears the words from your own mouth now. {REWARD} denars at the chief's fire. The contract closes.");
                var blurb = template
                    .Replace("{CHIEF}", chiefName)
                    .Replace("{VILLAGE}", vName)
                    .Replace("{LORD}", lordName)
                    .Replace("{REWARD}", rewardWords)
                    .Replace("{REDUCED_REWARD}", reducedRewardWords);
                MBTextManager.SetTextVariable("COLLECT_RAID_VILLAGE_BLURB", blurb);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("SetCollectRaidVillageVariable fail: " + ex.GetType().Name + ": " + ex.Message);
                MBTextManager.SetTextVariable("COLLECT_RAID_VILLAGE_BLURB", "{=bp_dialogmanager_304}The deed is done. The silver is yours.");
            }
            return true;
        }

        private void OnCollectRaidVillageBounty()
        {
            try
            {
                var cid = CurrentBanditCultureId();
                var resolved = ResolveProfileCultureId(cid);
                var quests = Campaign.Current?.QuestManager?.Quests;
                if (quests == null) return;
                BandItPlus.Quests.BanditRaidVillageQuest match = null;
                foreach (var q in quests)
                {
                    if (q is BandItPlus.Quests.BanditRaidVillageQuest rq
                        && rq.IsOngoing
                        && rq.IsAwaitingChiefReturn
                        && (rq.CultureId == cid || rq.CultureId == resolved))
                    {
                        match = rq; break;
                    }
                }
                if (match != null)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "OnCollectRaidVillageBounty: closing quest for cultureId=" + match.CultureId);
                    match.FinishWithSuccess();
                }
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("OnCollectRaidVillageBounty fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // 2026-06-03 story-expansion: two-phase named-target collection helpers.
        // CanCollectNamedTargetBounty: shows "I've ended the man you named." player line
        // when an active BanditNamedTargetQuest for THIS chief's culture is in the
        // awaiting-chief-return state (kill happened, reward not yet delivered).
        private bool CanCollectNamedTargetBounty()
        {
            try
            {
                var cid = CurrentBanditCultureId();
                if (string.IsNullOrEmpty(cid)) return false;
                var resolved = ResolveProfileCultureId(cid);
                var quests = Campaign.Current?.QuestManager?.Quests;
                if (quests == null) return false;
                foreach (var q in quests)
                {
                    if (q is BandItPlus.Quests.BanditNamedTargetQuest nq
                        && nq.IsOngoing
                        && nq.IsAwaitingChiefReturn
                        && (nq.CultureId == cid || nq.CultureId == resolved))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        // SetCollectNamedTargetVariable: build the per-chief closure prose with
        // {CHIEF}/{TARGET}/{REWARD}/{REDUCED_REWARD} substitution and stuff it into
        // COLLECT_BLURB. Picks NamedTargetComplete for player-kill, NamedTargetOtherCauseProse
        // for other-cause death.
        private bool SetCollectNamedTargetVariable()
        {
            try
            {
                BandItPlus.Quests.BanditNamedTargetQuest match = null;
                var cid = CurrentBanditCultureId();
                var resolved = ResolveProfileCultureId(cid);
                var quests = Campaign.Current?.QuestManager?.Quests;
                if (quests != null)
                {
                    foreach (var q in quests)
                    {
                        if (q is BandItPlus.Quests.BanditNamedTargetQuest nq
                            && nq.IsOngoing
                            && nq.IsAwaitingChiefReturn
                            && (nq.CultureId == cid || nq.CultureId == resolved))
                        {
                            match = nq;
                            break;
                        }
                    }
                }
                if (match == null)
                {
                    MBTextManager.SetTextVariable("COLLECT_BLURB",
                        "{=bp_dialogmanager_305}Speak plainly, traveler. Either the man is dead or he isn't.");
                    return true;
                }

                string chiefName = match.QuestGiver?.Name?.ToString() ?? Localization.Get("bp_dialogmanager_179", "the chief");
                string targetName = match.TargetHero?.Name?.ToString() ?? Localization.Get("bp_dialogmanager_306", "the marked man");
                int fullReward = match.RewardGoldAmount;
                int reducedReward = fullReward / 4;
                string rewardWords = fullReward > 0 ? fullReward.ToString("N0") : Localization.Get("bp_dialogmanager_300", "the agreed");
                string reducedRewardWords = reducedReward > 0 ? reducedReward.ToString("N0") : Localization.Get("bp_dialogmanager_301", "thin coin");

                BandItPlus.Cultures.CultureProfile prof = null;
                if (!string.IsNullOrEmpty(match.CultureId))
                    BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(match.CultureId, out prof);

                string template = null;
                if (prof != null && match.KilledByOtherCause && !string.IsNullOrEmpty(prof.NamedTargetOtherCauseProse))
                    template = prof.NamedTargetOtherCauseProse;
                else if (prof != null && !match.KilledByOtherCause && !string.IsNullOrEmpty(prof.NamedTargetComplete))
                    template = prof.NamedTargetComplete;
                else if (match.KilledByOtherCause)
                    template = Localization.Get("bp_dialogmanager_307", "{TARGET} is dead - but not by your hand. The grudge stands answered, after a fashion. {CHIEF} counts out {REDUCED_REWARD} denars in quarter-pay and sets the bond a smaller notch firmer.");
                else
                    template = Localization.Get("bp_dialogmanager_308", "{TARGET} is dead. {CHIEF} has heard the words from your own mouth now. {REWARD} denars are counted out at the chief's fire. The contract closes.");

                var blurb = template
                    .Replace("{CHIEF}", chiefName)
                    .Replace("{TARGET}", targetName)
                    .Replace("{REWARD}", rewardWords)
                    .Replace("{REDUCED_REWARD}", reducedRewardWords);
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetCollectNamedTargetVariable: otherCause=" + match.KilledByOtherCause
                    + " resolvedLen=" + (blurb?.Length ?? 0));
                MBTextManager.SetTextVariable("COLLECT_BLURB", blurb);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "SetCollectNamedTargetVariable fail: " + ex.GetType().Name + ": " + ex.Message);
                MBTextManager.SetTextVariable("COLLECT_BLURB", "{=bp_dialogmanager_304}The deed is done. The silver is yours.");
            }
            return true;
        }

        // OnCollectNamedTargetBounty: triggers actual quest completion. Finds the
        // matching pending quest and calls FinishWithSuccess — which fires the existing
        // OnCompleteWithSuccess path (reward gold drop + relation bump + per-chief
        // closure log entry). The dialog reply itself uses the same prose so the
        // player sees the chief speak it before the journal logs it.
        private void OnCollectNamedTargetBounty()
        {
            try
            {
                var cid = CurrentBanditCultureId();
                var resolved = ResolveProfileCultureId(cid);
                var quests = Campaign.Current?.QuestManager?.Quests;
                if (quests == null) return;
                BandItPlus.Quests.BanditNamedTargetQuest match = null;
                foreach (var q in quests)
                {
                    if (q is BandItPlus.Quests.BanditNamedTargetQuest nq
                        && nq.IsOngoing
                        && nq.IsAwaitingChiefReturn
                        && (nq.CultureId == cid || nq.CultureId == resolved))
                    {
                        match = nq; break;
                    }
                }
                if (match != null)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "OnCollectNamedTargetBounty: closing quest for cultureId=" + match.CultureId);
                    match.FinishWithSuccess();
                }
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "OnCollectNamedTargetBounty fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Visibility gate: Tier 3 + no active BanditNamedTargetQuest for this culture.
        private bool CanRequestNamedTargetHunt()
        {
            try
            {
                if (!IsChiefTier3()) return false;
                var cid = GetCultureIdForHideout(GetCurrentHideout());
                if (string.IsNullOrEmpty(cid)) return false;
                var quests = Campaign.Current?.QuestManager?.Quests;
                if (quests == null) return true;
                foreach (var q in quests)
                {
                    if (q is BandItPlus.Quests.BanditNamedTargetQuest nq && nq.CultureId == cid)
                        return false;       // already hunting one for this culture
                }
                return true;
            }
            catch { return false; }
        }

        // Picks a random eligible noble target and stores it in _pendingNamedTarget.
        // Sets NAMEDTARGET_BLURB to the chief's announcement line. Returns true so
        // the dialog renders. Falls back to a generic "no target found" line if no
        // eligible Hero is available (extremely unlikely in normal play).
        private bool SetNamedTargetVariable()
        {
            try
            {
                _pendingNamedTarget = null;
                var candidates = new List<Hero>();
                if (Hero.AllAliveHeroes != null)
                {
                    foreach (var h in Hero.AllAliveHeroes)
                    {
                        if (h == null || !h.IsAlive) continue;
                        if (h == Hero.MainHero) continue;
                        if (h.IsHumanPlayerCharacter) continue;
                        if (h.MapFaction == null) continue;
                        if (h.MapFaction.IsBanditFaction || h.MapFaction.IsMinorFaction) continue;
                        if (!h.IsLord) continue;
                        if (h.PartyBelongedTo == null) continue;       // need them on a party so player can find
                        if (h.HomeSettlement == null) continue;
                        candidates.Add(h);
                    }
                }
                if (candidates.Count == 0)
                {
                    MBTextManager.SetTextVariable("NAMEDTARGET_BLURB",
                        "{=bp_dialogmanager_309}There's a man, traveler — but the world's too quiet this season. Come back when the lords are riding more openly, and I'll name him then.");
                    return true;
                }
                var pick = candidates[MBRandom.RandomInt(candidates.Count)];
                _pendingNamedTarget = pick;
                string targetName = pick.Name?.ToString() ?? Localization.Get("bp_dialogmanager_306", "the marked man");
                string homeName = pick.HomeSettlement?.Name?.ToString() ?? Localization.Get("bp_dialogmanager_310", "his hold");
                string rewardWords = Localization.Get("bp_dialogmanager_311", "five thousand"); // reward gold = 5000; word form for natural prose

                // 2026-06-03 v4 story-expansion: prefer per-chief authored NamedTargetAcceptProse
                // with {TARGET}/{HOME}/{REWARD} substitution. Falls back to a generic line.
                var rawId = CurrentBanditCultureId();
                var profileId = ResolveProfileCultureId(rawId);
                string template = null;
                string source = "(none)";
                if (!string.IsNullOrEmpty(profileId)
                    && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(profileId, out var prof)
                    && prof != null
                    && !string.IsNullOrEmpty(prof.NamedTargetAcceptProse))
                {
                    template = prof.NamedTargetAcceptProse;
                    source = "CultureProfile.NamedTargetAcceptProse";
                }
                else
                {
                    template = Localization.Get("bp_dialogmanager_312", "His name is {TARGET}. He keeps his banner over {HOME}, and his party rides the roads between his fiefs. Find him, end him - and the gold I owe will be waiting at this fire when you come back. {REWARD} denars, traveler. The reason runs deeper than that, but the gold is what's owed.");
                    source = "generic-fallback";
                }
                var blurb = template.Replace("{TARGET}", targetName).Replace("{HOME}", homeName).Replace("{REWARD}", rewardWords);
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetNamedTargetVariable: rawId='" + (rawId ?? "<null>")
                    + "' profileId='" + (profileId ?? "<null>")
                    + "' target=" + targetName + " home=" + homeName
                    + " source=" + source
                    + " resolvedLen=" + (blurb?.Length ?? 0));
                MBTextManager.SetTextVariable("NAMEDTARGET_BLURB", blurb);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("SetNamedTargetVariable fail: " + ex.Message);
                MBTextManager.SetTextVariable("NAMEDTARGET_BLURB",
                    "{=bp_dialogmanager_313}Some other day, traveler. The man I'd name isn't worth the hunt today.");
            }
            return true;
        }

        // Consequence — spawn the BanditNamedTargetQuest using the previously-picked
        // _pendingNamedTarget. Mirrors the OnAcceptTrustQuest registration pattern.
        private void OnAcceptNamedTargetHunt()
        {
            try
            {
                if (_pendingNamedTarget == null) return;       // SetNamedTargetVariable failed/no candidates
                var hideout = GetCurrentHideout();
                if (hideout == null) return;
                var cultureId = GetCultureIdForHideout(hideout);
                if (string.IsNullOrEmpty(cultureId)) return;

                var chiefReg = BandItPlus.Heroes.BanditChiefRegistry.Instance;
                Hero questGiver = chiefReg?.GetChief(cultureId);
                if (questGiver == null) questGiver = hideout.OwnerClan?.Leader ?? Hero.MainHero;

                string questId = "bp_namedtarget_" + cultureId + "_"
                    + (_pendingNamedTarget.StringId ?? "x") + "_"
                    + (long)CampaignTime.Now.ToHours;
                var hq = new BandItPlus.Quests.BanditNamedTargetQuest(
                    questId, questGiver, CampaignTime.Never, kNamedTargetReward,
                    cultureId, _pendingNamedTarget.StringId);
                // StartQuest runs RegisterEvents — bounty kill detection lives
                // same-session (07-03 signature-quest lesson).
                hq.StartQuest();
                hq.InitJournalEntries();

                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "Tier3 named-target accepted: target='" + _pendingNamedTarget.StringId + "' for " + cultureId);
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] You've taken a named-target hunt from the chief. The mark: "
                    + (_pendingNamedTarget.Name?.ToString() ?? "?") + ".",
                    Colors.Green));
                _pendingNamedTarget = null;       // consumed
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("OnAcceptNamedTargetHunt fail: " + ex.Message);
            }
        }

        // Daily-tick callback: if an active loan exists and its return date has passed,
        // remove the loaned troops from the player's party and clear the loan slot.
        private void OnDailyTickCheckTroopLoan()
        {
            try
            {
                if (string.IsNullOrEmpty(_activeTroopLoanCulture)) return;
                if (CampaignTime.Now < _activeTroopLoanReturnDate) return;

                var party = MobileParty.MainParty;
                if (party != null)
                {
                    var troopId = GetCultureBanditTroopId(_activeTroopLoanCulture);
                    var troop = MBObjectManager.Instance.GetObject<CharacterObject>(troopId);
                    if (troop != null)
                    {
                        // Take back as many as we lent, capped by what's actually in the
                        // roster (player may have lost some in battle — those don't return).
                        int currentCount = party.MemberRoster.GetTroopCount(troop);
                        int removeCount = Math.Min(currentCount, _activeTroopLoanCount);
                        if (removeCount > 0)
                            party.MemberRoster.AddToCounts(troop, -removeCount, false);

                        InformationManager.DisplayMessage(new InformationMessage(
                            "[BandIt Plus] The chief's boys return home. " + removeCount + " " + troop.Name + " leave your party.",
                            Colors.Yellow));
                    }
                }

                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "Tier3: troop loan returned — culture=" + _activeTroopLoanCulture + " count=" + _activeTroopLoanCount);
                _activeTroopLoanCulture = "";
                _activeTroopLoanCount = 0;
                _activeTroopLoanReturnDate = CampaignTime.Zero;
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("OnDailyTickCheckTroopLoan fail: " + ex.Message);
            }
        }

        // === Wave 4.11-fix v57 (2026-05-08) — Tier 2 operational helpers ===

        // Live nearby-lord lookup. Iterates AllAliveHeroes, filters to non-bandit nobles
        // whose home settlement is within ~150 map-units of the current hideout, picks
        // a random one, and emits a rumor about their patrol gap. Falls back to a
        // generic line if no nearby lord can be resolved (e.g. desert hideout with no
        // imperial neighbors).
        private bool SetLordsRoadsVariable()
        {
            try
            {
                // v57 first-pass: skip distance filter — Settlement.Position2D isn't
                // directly accessible without a MapPoint cast and the small lord pool
                // makes random selection acceptable for now. Future iteration could
                // add proximity weighting via Settlement-as-IMapPoint cast if the
                // rumors feel too distant from the hideout's region.
                var candidates = new List<Hero>();
                if (Hero.AllAliveHeroes != null)
                {
                    foreach (var h in Hero.AllAliveHeroes)
                    {
                        if (h == null || !h.IsAlive) continue;
                        if (h == Hero.MainHero) continue;
                        if (h.IsHumanPlayerCharacter) continue;
                        if (h.MapFaction == null) continue;
                        if (h.MapFaction.IsBanditFaction || h.MapFaction.IsMinorFaction) continue;
                        if (!h.IsLord) continue;
                        if (h.HomeSettlement == null) continue;     // need a hold name to reference
                        candidates.Add(h);
                    }
                }
                string blurb;
                if (candidates.Count > 0)
                {
                    var pick = candidates[MBRandom.RandomInt(candidates.Count)];
                    int idx = MBRandom.RandomInt(_lordRoadsTemplates.Length);
                    blurb = _lordRoadsTemplates[idx]
                        .Replace("{LORD}", pick.Name?.ToString() ?? Localization.Get("bp_dialogmanager_314", "the lord"))
                        .Replace("{HOME}", pick.HomeSettlement?.Name?.ToString() ?? Localization.Get("bp_dialogmanager_310", "his hold"));
                }
                else
                {
                    blurb = "{=bp_dialogmanager_315}Nobles around here are running their patrols tight this season. My boys haven't found a gap worth riding through. Ask me again when the lords get fat from a feast and lazy with their riders.";
                }
                MBTextManager.SetTextVariable("LORDS_BLURB", blurb);
            }
            catch { MBTextManager.SetTextVariable("LORDS_BLURB", "{=bp_dialogmanager_316}Nothing actionable, traveler. Lords keep their roads tight this week."); }
            return true;
        }

        private static readonly string[] _lordRoadsTemplates = new[]
        {
            Localization.Get("bp_dialogmanager_317", "{LORD}'s outriders only ride the south leg out of {HOME} — the north road's been quiet for two weeks. If you've a quick party, that's an open door."),
            Localization.Get("bp_dialogmanager_318", "Rumor among my scouts: {LORD} is feasting half his garrison this fortnight. The road past {HOME} is thinly watched until the wine runs out."),
            Localization.Get("bp_dialogmanager_319", "{LORD} pulled men back to {HOME} for a tournament. The patrol on his border-road is two riders deep — half what it was. Quick window, traveler."),
            Localization.Get("bp_dialogmanager_320", "{LORD} of {HOME} hasn't replaced the captain who got himself killed in the spring. His patrols have a rhythm now. My boys could draw it for you on a map, if you'd pay for the ink."),
            Localization.Get("bp_dialogmanager_321", "Heard {LORD}'s tax-rider was lost on the road three days back. Until {HOME} sends another, that road is tax-free for whoever moves first.")
        };

        // Inter-clan rivalry/alliance rumors. 2026-06-03 v4 story-expansion:
        // 3-tier resolver — per-chief authored prose (CultureProfile.ScoutsClanProse),
        // then random pick from generic _clanIntelPool, then short fallback.
        private bool SetClanIntelVariable()
        {
            try
            {
                var rawId = CurrentBanditCultureId();
                var profileId = ResolveProfileCultureId(rawId);
                string blurb = null;
                string source = "(none)";
                if (!string.IsNullOrEmpty(profileId)
                    && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(profileId, out var prof)
                    && prof != null
                    && !string.IsNullOrEmpty(prof.ScoutsClanProse))
                {
                    blurb = prof.ScoutsClanProse;
                    source = "CultureProfile.ScoutsClanProse";
                }
                else
                {
                    int idx = MBRandom.RandomInt(_clanIntelPool.Length);
                    blurb = _clanIntelPool[idx];
                    source = "_clanIntelPool[" + idx + "]";
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetClanIntelVariable: rawId='" + (rawId ?? "<null>")
                    + "' profileId='" + (profileId ?? "<null>")
                    + "' source=" + source
                    + " resolvedLen=" + (blurb?.Length ?? 0));
                MBTextManager.SetTextVariable("CLAN_INTEL_BLURB", blurb);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetClanIntelVariable fail: " + ex.GetType().Name + ": " + ex.Message);
                MBTextManager.SetTextVariable("CLAN_INTEL_BLURB", "{=bp_dialogmanager_322}We keep our own counsel, traveler. Other clans keep theirs.");
            }
            return true;
        }

        private static readonly string[] _clanIntelPool = new[]
        {
            "{=bp_dialogmanager_323}There's a chief south of here who took my brother's hand in payment for a debt that was never owed. His name I keep behind my teeth — but if you're hunting clans, he hangs his banner above a brown river.",
            "{=bp_dialogmanager_324}The forest-folk and the mountain-folk have been quarrelling over a Battanian tribute road. Both clans are bleeding men. The road's open to a third walker who won't wear either banner.",
            "{=bp_dialogmanager_325}Steppe wolves owe my clan three horses from a winter we don't speak about. If you ride through their lands and hear of an unfed mare, that mare is mine.",
            "{=bp_dialogmanager_326}There's a coastal chief whose hall sits where my grandfather's hall used to. We don't trade words. We don't trade steel either, but only because the sea hasn't run dry yet.",
            "{=bp_dialogmanager_327}Word in the bandit-tongue: a desert chief's been buying up steel from the coast at three times the rate. Either he's arming up for something — or someone's selling him weapons that don't exist.",
            "{=bp_dialogmanager_328}The pagan cult's been quiet this season. That's never good news. My boys read the bones the same way they do, and the bones say something's coming on the autumn winds.",
            "{=bp_dialogmanager_329}If you ever bring word from the chief who owes me three horses, traveler, I'll tell you which clan in these hills will open their gates to you — and which won't."
        };

        // 2026-06-03 story-expansion: per-chief Tier-3 "Send your boys with mine for a
        // season" troop-loan acceptance. 2-tier resolver: CultureProfile.TroopLoanProse → fallback.
        private bool SetTroopLoanVariable()
        {
            try
            {
                var rawId = CurrentBanditCultureId();
                var profileId = ResolveProfileCultureId(rawId);
                string blurb = null;
                string source = "(none)";
                if (!string.IsNullOrEmpty(profileId)
                    && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(profileId, out var prof)
                    && prof != null
                    && !string.IsNullOrEmpty(prof.TroopLoanProse))
                {
                    blurb = prof.TroopLoanProse;
                    source = "CultureProfile.TroopLoanProse";
                }
                else
                {
                    blurb = "{=bp_dialogmanager_330}Done. Ten of my boys ride with you for the next seven days, traveler - pick of the camp. Bring them back alive when the time's up, or don't bring them back at all. Either way, the debt's even.";
                    source = "generic-fallback";
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetTroopLoanVariable: rawId='" + (rawId ?? "<null>")
                    + "' profileId='" + (profileId ?? "<null>")
                    + "' source=" + source
                    + " resolvedLen=" + (blurb?.Length ?? 0));
                MBTextManager.SetTextVariable("TROOP_LOAN_BLURB", blurb);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetTroopLoanVariable fail: " + ex.GetType().Name + ": " + ex.Message);
                MBTextManager.SetTextVariable("TROOP_LOAN_BLURB", "{=bp_dialogmanager_331}Done. Ten of my boys ride with you for seven days.");
            }
            return true;
        }

        // 2026-06-03 story-expansion: per-chief Tier-3 "Then call me brother" acceptance
        // speech. 2-tier resolver: CultureProfile.OathBrotherProse → generic fallback.
        private bool SetOathBrotherVariable()
        {
            try
            {
                var rawId = CurrentBanditCultureId();
                var profileId = ResolveProfileCultureId(rawId);
                string blurb = null;
                string source = "(none)";
                if (!string.IsNullOrEmpty(profileId)
                    && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(profileId, out var prof)
                    && prof != null
                    && !string.IsNullOrEmpty(prof.OathBrotherProse))
                {
                    blurb = prof.OathBrotherProse;
                    source = "CultureProfile.OathBrotherProse";
                }
                else
                {
                    blurb = "{=bp_dialogmanager_332}Then it's spoken, traveler. Bring water from a fire that's not your own. We drink from the same horn now. The boys will know the name - and so will the bones in the earth, and the salt, and the steel in the dark. Walk careful. The oath cuts both ways.";
                    source = "generic-fallback";
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetOathBrotherVariable: rawId='" + (rawId ?? "<null>")
                    + "' profileId='" + (profileId ?? "<null>")
                    + "' source=" + source
                    + " resolvedLen=" + (blurb?.Length ?? 0));
                MBTextManager.SetTextVariable("OATH_BROTHER_BLURB", blurb);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetOathBrotherVariable fail: " + ex.GetType().Name + ": " + ex.Message);
                MBTextManager.SetTextVariable("OATH_BROTHER_BLURB", "{=bp_dialogmanager_333}Then it's spoken, traveler.");
            }
            return true;
        }

        // 2026-06-03 story-expansion: per-chief Tier-3 "Hear my answer" pact-acceptance
        // speech (war-bond, NOT hearth-bond). 2-tier resolver: CultureProfile.PactAnswerProse → fallback.
        private bool SetPactAnswerVariable()
        {
            try
            {
                var rawId = CurrentBanditCultureId();
                var profileId = ResolveProfileCultureId(rawId);
                string blurb = null;
                string source = "(none)";
                if (!string.IsNullOrEmpty(profileId)
                    && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(profileId, out var prof)
                    && prof != null
                    && !string.IsNullOrEmpty(prof.PactAnswerProse))
                {
                    blurb = prof.PactAnswerProse;
                    source = "CultureProfile.PactAnswerProse";
                }
                else
                {
                    blurb = "{=bp_dialogmanager_334}Then I'll answer plain. When you raise a banner, traveler, my boys are at your back. When you lose one, my boys are at your back still. The blood-debt I owe the lords runs longer than the road. Yours runs alongside it now. Speak this oath again when steel is drawn - the sound of it carries, even from this far.";
                    source = "generic-fallback";
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetPactAnswerVariable: rawId='" + (rawId ?? "<null>")
                    + "' profileId='" + (profileId ?? "<null>")
                    + "' source=" + source
                    + " resolvedLen=" + (blurb?.Length ?? 0));
                MBTextManager.SetTextVariable("PACT_ANSWER_BLURB", blurb);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetPactAnswerVariable fail: " + ex.GetType().Name + ": " + ex.Message);
                MBTextManager.SetTextVariable("PACT_ANSWER_BLURB", "{=bp_dialogmanager_335}Then I'll answer plain.");
            }
            return true;
        }

        // 2026-06-03 story-expansion: per-chief Tier-2 "Will you bleed for me when the
        // time comes?" reply. 3-tier resolver: CultureProfile.PactTeaseProse (authored
        // per-chief), then a generic neutral fallback. Uses v4 multi-source culture
        // resolver so the response fires correctly in scene-based chief conversations.
        private bool SetPactTeaseVariable()
        {
            try
            {
                var rawId = CurrentBanditCultureId();
                var profileId = ResolveProfileCultureId(rawId);
                string blurb = null;
                string source = "(none)";
                if (!string.IsNullOrEmpty(profileId)
                    && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(profileId, out var prof)
                    && prof != null
                    && !string.IsNullOrEmpty(prof.PactTeaseProse))
                {
                    blurb = prof.PactTeaseProse;
                    source = "CultureProfile.PactTeaseProse";
                }
                else
                {
                    blurb = "{=bp_dialogmanager_336}When the time comes, traveler, you'll know my answer. My boys are mine - but the road we walk together has already begun. Speak again when you've a banner of your own to raise.";
                    source = "generic-fallback";
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetPactTeaseVariable: rawId='" + (rawId ?? "<null>")
                    + "' profileId='" + (profileId ?? "<null>")
                    + "' source=" + source
                    + " resolvedLen=" + (blurb?.Length ?? 0));
                MBTextManager.SetTextVariable("PACT_TEASE_BLURB", blurb);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetPactTeaseVariable fail: " + ex.GetType().Name + ": " + ex.Message);
                MBTextManager.SetTextVariable("PACT_TEASE_BLURB", "{=bp_dialogmanager_337}When the time comes, traveler, you'll know my answer.");
            }
            return true;
        }

        // Caravan-target intel — Tier 2 specific (chunkier, more actionable than the
        // Tier 1 scout rumor pool which was generic road-news). These name routes,
        // escort sizes, and rough timings. 2026-06-03 v4 story-expansion: 3-tier
        // resolver — per-chief authored prose (CultureProfile.ScoutsCaravanProse),
        // then random pick from generic _caravanIntelPool, then short fallback.
        private bool SetCaravanIntelVariable()
        {
            try
            {
                var rawId = CurrentBanditCultureId();
                var profileId = ResolveProfileCultureId(rawId);
                string blurb = null;
                string source = "(none)";
                if (!string.IsNullOrEmpty(profileId)
                    && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(profileId, out var prof)
                    && prof != null
                    && !string.IsNullOrEmpty(prof.ScoutsCaravanProse))
                {
                    blurb = prof.ScoutsCaravanProse;
                    source = "CultureProfile.ScoutsCaravanProse";
                }
                else
                {
                    int idx = MBRandom.RandomInt(_caravanIntelPool.Length);
                    blurb = _caravanIntelPool[idx];
                    source = "_caravanIntelPool[" + idx + "]";
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetCaravanIntelVariable: rawId='" + (rawId ?? "<null>")
                    + "' profileId='" + (profileId ?? "<null>")
                    + "' source=" + source
                    + " resolvedLen=" + (blurb?.Length ?? 0));
                MBTextManager.SetTextVariable("CARAVAN_INTEL_BLURB", blurb);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v4 SetCaravanIntelVariable fail: " + ex.GetType().Name + ": " + ex.Message);
                MBTextManager.SetTextVariable("CARAVAN_INTEL_BLURB", "{=bp_dialogmanager_338}Roads are quiet this week, traveler. Worth checking back.");
            }
            return true;
        }

        private static readonly string[] _caravanIntelPool = new[]
        {
            "{=bp_dialogmanager_339}An imperial silk caravan is moving south of the eastern pass — six guards, two wagons, escorted by an outrider who naps at noon. They'll pass the third milestone four days from now.",
            "{=bp_dialogmanager_340}Vlandian wool merchants run the western salt-road every fortnight. Eight guards, three wagons. The escort thins after they cross the first ford because the captain hates getting his boots wet.",
            "{=bp_dialogmanager_341}A Sturgian fur convoy has been hauling pelts down to imperial markets. Twelve guards, but two of them are drunkards and one rides at the rear half-asleep. Hit the rear, take the pelts, leave the front in the dust.",
            "{=bp_dialogmanager_342}The Aserai dye-traders cross the southern caravan-road every new moon. Six guards. They carry an iron-bound chest under the second cart — that's where the indigo and gold-thread go.",
            "{=bp_dialogmanager_343}A bishop's tithe-wagon was seen on the imperial highway, bound for the abbey at the northern lake. Eight guards, but priests don't fight, and the captain hasn't drawn steel in two campaigns.",
            "{=bp_dialogmanager_344}There's a Khuzait horse-trader running the eastern steppe-road. Forty horses, six riders, no wagons. The risk is the open ground — there's nowhere to ambush. The reward is forty horses.",
            "{=bp_dialogmanager_345}Mercenary captain hauling pay-coin to a contract south of here. Thirty riders, but the chest is at the very back of the column with only two old men guarding it. The captain himself rides at the front."
        };

        // v56: shared rumor pool — generic for all cultures in this iteration. Per-culture
        // rumor pools can be authored later by adding a Rumors[] field to CultureProfile
        // (already exists per the field list — currently used for greeter passes only).
        private static readonly string[] _chiefRumorPool = new[]
        {
            "{=bp_dialogmanager_346}My boys spotted a Vlandian caravan on the south road three days back — heavy escort, heavier wagons. Likely on its way back through by week's end.",
            "{=bp_dialogmanager_347}There's a tax-rider working the villages two days east. He travels with four men and a clerk. The clerk carries the ledger.",
            "{=bp_dialogmanager_348}Imperial patrols have thinned out near the border the past fortnight. Either they're regrouping or they've got bigger trouble.",
            "{=bp_dialogmanager_349}Heard a noble lost his hunting party in the woods west of here. Lost his temper too. Anyone who finds his ring will hear about it.",
            "{=bp_dialogmanager_350}There's a new chief south of us who took his clan from a man who was kind to me once. I have not forgotten.",
            "{=bp_dialogmanager_351}The salt-traders have been buying out everyone's stock at half-price. Someone in the towns is hoarding. Worth knowing.",
            "{=bp_dialogmanager_352}A mercenary captain rode through last week looking for swords. Said his name with the kind of weight that means he's worth knowing — or worth avoiding.",
            "{=bp_dialogmanager_353}The road-shrine north of the pass has been burned. Not by us. Someone wants the road less holy. We don't know who yet."
        };

        private bool SetRumorVariable()
        {
            try
            {
                int idx = MBRandom.RandomInt(_chiefRumorPool.Length);
                MBTextManager.SetTextVariable("RUMOR_BLURB", _chiefRumorPool[idx]);
            }
            catch { MBTextManager.SetTextVariable("RUMOR_BLURB", "{=bp_dialogmanager_354}Nothing worth telling, traveler. Quiet roads this week."); }
            return true;
        }

        private void OnBribeConsequence()
        {
            try
            {
                int amt = CurrentBribeAmount();
                if (Hero.MainHero != null && Hero.MainHero.Gold >= amt)
                {
                    GiveGoldAction.ApplyBetweenCharacters(Hero.MainHero, null, amt, true);
                    var leader = ResolveChiefHero();
                    if (leader != null)
                    {
                        try { ChangeRelationAction.ApplyPlayerRelation(leader, 5, true, true); } catch { }
                    }
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "Chief bribe: " + amt + "g + relation +5 with chief (" + (leader?.Name?.ToString() ?? "<null>") + ")");
                }
                EndEncounterNoCombat();
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Bribe error: " + ex.Message, Colors.Red));
            }
        }

        private static void TradeLog(string msg)
        {
            try { System.IO.File.AppendAllText(BandItPlus.Diagnostics.BpDiag.P("bp_peace_debug.log"),
                "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] DialogTrade: " + msg + Environment.NewLine); } catch { }
        }

        private void OnTradeConsequence()
        {
            TradeLog("ENTRY");
            try
            {
                var party = MobileParty.ConversationParty;
                if (party == null) { TradeLog("BAIL: no conversation party"); EndEncounterNoCombat(); return; }

                // Try to open the real trade screen if we have a SettlementComponent (hideout context).
                var hideout = Settlement.CurrentSettlement ?? PlayerEncounter.EncounterSettlement;
                TradeLog("CurrentSettlement=" + (Settlement.CurrentSettlement?.StringId ?? "null")
                    + ", EncounterSettlement=" + (PlayerEncounter.EncounterSettlement?.StringId ?? "null")
                    + ", IsHideout=" + (hideout?.IsHideout ?? false));

                if (hideout != null && hideout.IsHideout && hideout.SettlementComponent != null)
                {
                    TradeLog("opening real trade screen with hideout SettlementComponent=" +
                        hideout.SettlementComponent.GetType().Name);
                    var roster = party.ItemRoster;
                    if (roster == null || roster.Count == 0)
                    {
                        TradeLog("party roster empty, using fallback");
                        roster = BuildTradeFallbackRoster();
                    }
                    Helpers.InventoryScreenHelper.OpenScreenAsTrade(
                        roster,
                        hideout.SettlementComponent,
                        Helpers.InventoryScreenHelper.InventoryCategoryType.None,
                        null);
                    TradeLog("OpenScreenAsTrade returned");
                    return;
                }

                // World-map encounter: no SettlementComponent. Tell player to visit a hideout instead.
                TradeLog("no settlement context — redirecting player");
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] " + (party.Name?.ToString() ?? Localization.Get("bp_dialogmanager_355", "The chief")) +
                    ": \"Not on the road, friend. Come to our camp if you want to trade proper.\"",
                    Colors.Yellow));
                EndEncounterNoCombat();
            }
            catch (Exception ex)
            {
                TradeLog("EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] Trade error: " + ex.Message, Colors.Red));
            }
        }

        private void EndEncounterNoCombat()
        {
            try { PlayerEncounter.LeaveEncounter = true; }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BandIt Plus] EndEncounter error: " + ex.Message, Colors.Red));
            }
        }
    }
}
