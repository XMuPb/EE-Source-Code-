using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Election;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Campaign behavior that persists custom encyclopedia descriptions
    /// into the save file so they survive save/load cycles.
    /// </summary>
    public class EncyclopediaEditBehavior : CampaignBehaviorBase
    {
        // Key = StringId of the object (Hero, Clan, Kingdom, Settlement)
        // Value = Custom description text written by the player
        [SaveableField(1)]
        private Dictionary<string, string> _customDescriptions = new Dictionary<string, string>();

        // Key = "name_" + StringId or "title_" + StringId
        // Value = Custom name or title text written by the player
        [SaveableField(2)]
        private Dictionary<string, string> _customNames = new Dictionary<string, string>();

        // Key = StringId of the object
        // Value = Formatted in-game date string when the description was last edited
        [SaveableField(3)]
        private Dictionary<string, string> _editTimestamps = new Dictionary<string, string>();

        // Key = StringId of the object (Clan, Kingdom, Hero, Settlement)
        // Value = Serialized banner code string
        [SaveableField(4)]
        private Dictionary<string, string> _customBannerCodes = new Dictionary<string, string>();

        // Key = StringId of the hero
        // Value = Culture StringId (e.g. "empire", "sturgia", or a custom culture id)
        [SaveableField(5)]
        private Dictionary<string, string> _customCultures = new Dictionary<string, string>();

        // Key = StringId of the hero
        // Value = Occupation enum value as int (e.g. 2 = Lord, 4 = Wanderer)
        [SaveableField(6)]
        private Dictionary<string, int> _customOccupations = new Dictionary<string, int>();

        // Key = custom culture StringId (e.g. "ee_custom_myculture_1234567890")
        // Value = "displayName|baseCultureId|basicTroopId|eliteBasicTroopId"
        // Stores full custom culture definitions with troop tree assignments.
        [SaveableField(7)]
        private Dictionary<string, string> _customCultureDefs = new Dictionary<string, string>();

        // Key = "fieldKey_" + heroId (e.g. "backstory_lord_1_1", "personality_lord_1_1")
        // Value = Structured text content for the field
        // Valid field keys: backstory, personality, goals, relationships, rumors
        [SaveableField(8)]
        private Dictionary<string, string> _heroInfoFields = new Dictionary<string, string>();

        // Key = StringId of the object (Hero, Clan, Kingdom, Settlement)
        // Value = Comma-separated tag list (e.g. "ally, target, recruit")
        [SaveableField(9)]
        private Dictionary<string, string> _customTags = new Dictionary<string, string>();

        // Key = StringId of the object (Hero, Clan, Kingdom, Settlement)
        // Value = Newline-separated journal entries, each formatted as "date|text"
        // e.g. "Day 5 of Spring, 1084|Captured at Varcheg\nDay 12 of Spring, 1084|Released for ransom"
        [SaveableField(10)]
        private Dictionary<string, string> _journalEntries = new Dictionary<string, string>();

        // Per-hero battle counters that DON'T decay with the journal-trim cap.
        // Format: "W:N|L:N|C:N|HK:N|TK:N|T:N" (Wins, Losses, Captures, HeroKills, TroopKills, Tournaments)
        [SaveableField(19)]
        private Dictionary<string, string> _battleStats = new Dictionary<string, string>();

        // Key = heroId, Value = "1" if collapsed, absent or "0" if expanded
        // Persisted across saves so timeline collapse state survives game restart
        private Dictionary<string, string> _timelineCollapseStates = new Dictionary<string, string>();

        // Key = "viewingHeroId_targetHeroId" (e.g. "lord_1_1_lord_2_3")
        // Value = Player's note about this relationship
        [SaveableField(11)]
        private Dictionary<string, string> _relationNotes = new Dictionary<string, string>();

        // Key = "heroId_targetHeroId" (same format as relation notes)
        // Value = Newline-separated history entries, each "date|change|description"
        // e.g. "Day 5 of Spring, 1085|+5|Helped in battle\nDay 12 of Summer, 1085|-10|Executed prisoner"
        [SaveableField(12)]
        private Dictionary<string, string> _relationHistory = new Dictionary<string, string>();

        // Key = "viewingHeroId_targetHeroId" (same format as relation notes)
        // Value = Tag name (e.g. "ally", "rival", "trade", "family", "other")
        [SaveableField(17)]
        private Dictionary<string, string> _relationNoteTags = new Dictionary<string, string>();

        // Key = "viewingHeroId_targetHeroId" (same format as relation notes)
        // Value = "1" if tag is locked (player chose to prevent auto-suggest from changing it)
        [SaveableField(18)]
        private Dictionary<string, string> _relationNoteTagLocks = new Dictionary<string, string>();

        // Key = "objectId|tagName" (e.g. "lord_1_1|enemy")
        // Value = Player's note about this tag on this entity
        // e.g. "killed my brother", "future marriage candidate"
        [SaveableField(13)]
        private Dictionary<string, string> _tagNotes = new Dictionary<string, string>();

        // Item lore overrides (v2.6.0). Key = "<itemStringId>|<ownerStringId>"
        // (e.g. "vlandian_2haxe_a|lord_1_1"). Value = the player's custom lore.
        // Generated lore is never stored; ItemLoreLoader reproduces it
        // deterministically, so only overrides live in the save.
        [SaveableField(20)]
        private Dictionary<string, string> _itemLoreOverrides = new Dictionary<string, string>();

        // Battle / raid / conquest spoils attached to auto-journal chronicle lines (v2.6.2).
        // Key   = the chronicle entry key: "<entityId>|<date>|<first 24 chars of the stored text>".
        //         Produced by MakeChronicleKey() below, which is a byte-for-byte copy of
        //         ChronicleEntryPopulator.MakeStableId(EntityId, Date, Text) in EE-ChronicleNoters,
        //         so a consumer can recompute the key from a ChronicleEntry and always match.
        // Value = "<gold>|<omittedStacks>|<itemId>*<count>;<itemId>*<count>;..."
        //         e.g. "240|0|grain*12;hides*5"  or  "-90|3|" (gold lost, 3 stacks dropped by the cap).
        //         Item StringIds never contain '|', '*' or ';', and AddJournalEntry already
        //         replaces '|' in entry text with '-', so neither half of the pair can collide.
        [SaveableField(21)]
        private Dictionary<string, string> _chronicleSpoils = new Dictionary<string, string>();

        /// <summary>
        /// The 5 structured hero info field keys, in display order.
        /// </summary>
        internal static readonly string[] InfoFieldKeys = { "backstory", "personality", "goals", "relationships", "rumors" };

        /// <summary>
        /// Fields displayed as key-value pairs in the Info/Stats panel (short character traits).
        /// </summary>
        internal static readonly string[] StatsFieldKeys = new string[0];

        /// <summary>
        /// Fields displayed as formatted text paragraphs in the Lore section (long-form narrative).
        /// All editable fields use multi-line templates, so they all render in the Lore section
        /// with proper vertical layout rather than the Stats panel where multi-line values overlap.
        /// </summary>
        internal static readonly string[] NarrativeFieldKeys = { "backstory", "personality", "goals", "relationships", "rumors" };

        internal static readonly string[] ClanFieldKeys = { "founding", "territory", "traditions", "rivals" };
        internal static readonly string[] KingdomFieldKeys = { "history", "laws", "culture_lore", "military" };
        internal static readonly string[] SettlementFieldKeys = { "history", "economy", "landmarks", "legends" };

        internal static string[] GetFieldKeysForPageType(string pageType)
        {
            switch (pageType)
            {
                case "Clan": return ClanFieldKeys;
                case "Kingdom": return KingdomFieldKeys;
                case "Settlement": return SettlementFieldKeys;
                default: return NarrativeFieldKeys;
            }
        }

        private const int DefaultAutoTagEnemyRelationThreshold = -30;
        private const int DefaultAutoTagFriendRelationThreshold = 30;
        private const int DefaultAutoTagDangerousPartySize = 200;
        private const int AutoTagRichClanGoldThreshold = 100000;
        private const float AutoTagNearbyDistanceThreshold = 30f;
        private const int RelationSuggestRivalThreshold = -40;
        private const int RelationSuggestAllyThreshold = 40;
        private const int PreBattleTroopCacheMaxSize = 100;
        private const int SmallFightTroopThreshold = 50;
        private const int MinorAiBattleTroopThreshold = 100;
        private const int BattleRescuerCacheMaxSize = 200;
        private const int MaxJournalEntriesPerEntity = 30;
        private const int RelationChangeSignificantPositive = 10;
        private const int RelationChangePositive = 5;
        private const int RelationChangeMajorNegative = -20;
        private const int RelationChangeSignificantNegative = -10;

        public static EncyclopediaEditBehavior Instance { get; private set; }

        /// <summary>
        /// Set to true after SyncData loads custom banner codes from a save.
        /// The banner watchdog will perform a full re-apply on the next tick cycle.
        /// </summary>
        internal static bool NeedsBannerReapply { get; set; } = false;

        /// <summary>
        /// Set to true after SyncData loads custom culture definitions from a save.
        /// CustomCultureManager will re-register CultureObjects and re-apply hero.Culture on the next tick.
        /// </summary>
        internal static bool NeedsCustomCultureReapply { get; set; } = false;

        /// <summary>
        /// Set to true after SyncData loads custom names from a save.
        /// TickNameReapply will push saved names onto Hero/Settlement/Clan/Kingdom objects on the next tick.
        /// </summary>
        internal static bool NeedsNameReapply { get; set; } = false;

        /// <summary>
        /// Set by SyncData when loading a save. Checked by RegisterEvents to suppress
        /// the intro dialog. Needed because RegisterEvents fires AFTER SyncData on load.
        /// </summary>
        private bool _loadedFromSave = false;

        public override void RegisterEvents()
        {
            Instance = this;
            // Clear stale settlement culture tracking from any previous game session
            SaveSanitizerPatch.ClearTracking();

            // Schedule intro dialog only for NEW campaigns.
            // On loaded saves, SyncData runs BEFORE RegisterEvents and sets _loadedFromSave=true.
            if (_loadedFromSave)
            {
                _loadedFromSave = false;
                MCMSettings.DebugLog("RegisterEvents: loaded save detected, skipping intro dialog");
            }
            else
            {
                _showIntroOnNextTick = true;
                MCMSettings.DebugLog("RegisterEvents: scheduling intro dialog (new game)");
            }

            // Each event registration is wrapped in try-catch for cross-version compatibility.
            // Some events (OnChildConceivedEvent, OnMarriageOfferedToPlayerEvent, etc.) may not
            // exist in older Bannerlord versions (e.g., v1.2.12).

            // 2026-05-28: Auto-journal subscribers are gated on EE-ChronicleNoters presence.
            // When ChronicleNoters is disabled, EE-Editable stops creating NEW chronicle entries.
            // Existing _journalEntries save data is preserved (SyncData still runs) so re-enabling
            // ChronicleNoters resumes the auto-journal feed without losing history.
            // Auto-tags + custom-culture reapply ticks below are SEPARATE features and remain registered.
            bool _autoJournalEnabled = PeerRegistry.Has("EE-ChronicleNoters");
            MCMSettings.DebugLog("RegisterEvents: auto-journal subscribers " + (_autoJournalEnabled ? "ENABLED" : "SKIPPED") + " (PeerRegistry EE-ChronicleNoters=" + _autoJournalEnabled + ")");
            if (_autoJournalEnabled)
            {
            // Auto-journal event listeners — War
            try { CampaignEvents.MapEventStarted.AddNonSerializedListener(this, OnMapEventStarted); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: MapEventStarted failed: " + ex.ToString()); }
            try { CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: MapEventEnded failed: " + ex.ToString()); }
            try { CampaignEvents.OnSiegeEventStartedEvent.AddNonSerializedListener(this, OnSiegeStarted); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: OnSiegeEventStartedEvent failed: " + ex.ToString()); }
            try { CampaignEvents.RaidCompletedEvent.AddNonSerializedListener(this, OnRaidCompleted); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: RaidCompletedEvent failed: " + ex.ToString()); }
            try { CampaignEvents.HeroPrisonerTaken.AddNonSerializedListener(this, OnHeroPrisonerTaken); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: HeroPrisonerTaken failed: " + ex.ToString()); }
            try { CampaignEvents.HeroPrisonerReleased.AddNonSerializedListener(this, OnHeroPrisonerReleased); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: HeroPrisonerReleased failed: " + ex.ToString()); }
            try { CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, OnHeroKilled); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: HeroKilledEvent failed: " + ex.ToString()); }
            // Auto-journal event listeners — Politics
            try { CampaignEvents.WarDeclared.AddNonSerializedListener(this, OnWarDeclared); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: WarDeclared failed: " + ex.ToString()); }
            try { CampaignEvents.MakePeace.AddNonSerializedListener(this, OnMakePeace); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: MakePeace failed: " + ex.ToString()); }
            try { CampaignEvents.OnClanChangedKingdomEvent.AddNonSerializedListener(this, OnClanChangedKingdom); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: OnClanChangedKingdomEvent failed: " + ex.ToString()); }
            try { CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, OnSettlementOwnerChanged); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: OnSettlementOwnerChangedEvent failed: " + ex.ToString()); }
            try { CampaignEvents.OnClanDestroyedEvent.AddNonSerializedListener(this, OnClanDestroyed); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: OnClanDestroyedEvent failed: " + ex.ToString()); }
            // Auto-journal event listeners — Family
            try { CampaignEvents.TournamentFinished.AddNonSerializedListener(this, OnTournamentFinished); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: TournamentFinished failed: " + ex.ToString()); }
            try { CampaignEvents.HeroCreated.AddNonSerializedListener(this, OnHeroCreated); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: HeroCreated failed: " + ex.ToString()); }
            try { CampaignEvents.OnMarriageOfferedToPlayerEvent.AddNonSerializedListener(this, OnMarriageOffered); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: OnMarriageOfferedToPlayerEvent failed: " + ex.ToString()); }
            try { CampaignEvents.OnChildConceivedEvent.AddNonSerializedListener(this, OnChildConceived); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: OnChildConceivedEvent failed: " + ex.ToString()); }
            // Auto-journal event listeners — Military / Economy
            try { CampaignEvents.ArmyCreated.AddNonSerializedListener(this, OnArmyCreated); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: ArmyCreated failed: " + ex.ToString()); }
            try { CampaignEvents.ArmyDispersed.AddNonSerializedListener(this, OnArmyDispersed); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: ArmyDispersed failed: " + ex.ToString()); }
            try { CampaignEvents.OnHeroChangedClanEvent.AddNonSerializedListener(this, OnHeroChangedClan); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: OnHeroChangedClanEvent failed: " + ex.ToString()); }
            try { CampaignEvents.KingdomDecisionConcluded.AddNonSerializedListener(this, OnKingdomDecisionConcluded); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: KingdomDecisionConcluded failed: " + ex.ToString()); }
            try { CampaignEvents.RebellionFinished.AddNonSerializedListener(this, OnRebellionFinished); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: RebellionFinished failed: " + ex.ToString()); }
            try { CampaignEvents.OnQuestStartedEvent.AddNonSerializedListener(this, OnQuestStarted); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: OnQuestStartedEvent failed: " + ex.ToString()); }
            try { CampaignEvents.OnQuestCompletedEvent.AddNonSerializedListener(this, OnQuestCompleted); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: OnQuestCompletedEvent failed: " + ex.ToString()); }
            try { CampaignEvents.HeroLevelledUp.AddNonSerializedListener(this, OnHeroLevelledUp); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: HeroLevelledUp failed: " + ex.ToString()); }
            try { CampaignEvents.HeroGainedSkill.AddNonSerializedListener(this, OnHeroGainedSkill); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: HeroGainedSkill failed: " + ex.ToString()); }
            try { CampaignEvents.HeroWounded.AddNonSerializedListener(this, OnHeroWounded); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: HeroWounded failed: " + ex.ToString()); }
            TryRegisterEventByReflection("HeroComesOfAge", new Action<Hero>(OnHeroComesOfAge));
            TryRegisterEventByReflection("RansomOfferedToPlayer", new Action<Hero>(OnRansomOfferedToPlayer));
            // MercenaryTroopChangedInTown removed — too noisy for chronicle (fires frequently as troops rotate)
            // DailyTick detects: ruler changes, kingdom destruction, coming of age, marriages
            try { CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTickAutoJournal); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: DailyTickEvent failed: " + ex.ToString()); }
            } // end auto-journal gate
            // DailyTick: auto-generate tags based on game state
            try { CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTickAutoTags); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: DailyTickEvent auto-tags failed: " + ex.ToString()); }
            // v2.5.3 fix: re-apply RefreshNotableVolunteers every hour for notables with custom culture.
            // The game's volunteer-replenishment logic refills slots from the notable's home-settlement
            // culture when slots empty (after hire), bypassing our reflection write to Hero._culture —
            // so the slots silently revert to default culture troops between manual culture changes.
            // Re-applying every game-hour keeps the slots overridden continuously.
            try { CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTickReapplyCustomCultures); } catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: HourlyTickEvent custom-culture reapply failed: " + ex.ToString()); }
            // Relation change tracking — use reflection since CharacterRelationChangedEvent may not exist
            TryRegisterRelationChangeEvent();
        }

        /// <summary>
        /// v2.5.3 fix: every game-hour, re-apply RefreshNotableVolunteers for every notable that has
        /// a custom-culture assignment. The game's volunteer-replenishment logic uses the notable's
        /// underlying state (likely home-settlement culture) to refill empty slots after hire, which
        /// silently reverts our slot overrides. Re-applying once per game hour keeps the recruit
        /// roster on the user's chosen culture.
        /// </summary>
        private void OnHourlyTickReapplyCustomCultures()
        {
            try
            {
                if (_customCultures == null || _customCultures.Count == 0) return;
                var objMgr = TaleWorlds.ObjectSystem.MBObjectManager.Instance;
                if (objMgr == null) return;
                int reapplied = 0;
                foreach (var hero in Hero.AllAliveHeroes)
                {
                    if (hero == null) continue;
                    bool isNotable = false;
                    try { isNotable = hero.IsNotable; } catch (Exception ex) { MCMSettings.DebugLog("OnHourlyTickReapplyCustomCultures: isNotable check failed: " + ex.ToString()); continue; }
                    if (!isNotable) continue;

                    string customCultureId = GetCustomCulture(hero.StringId);
                    if (string.IsNullOrEmpty(customCultureId)) continue;

                    CultureObject customCulture = null;
                    try { customCulture = objMgr.GetObject<CultureObject>(customCultureId); }
                    catch (Exception ex) { MCMSettings.DebugLog("OnHourlyTickReapplyCustomCultures: GetObject failed for " + customCultureId + ": " + ex.ToString()); continue; }
                    if (customCulture == null) continue;

                    try
                    {
                        EncyclopediaCultureEditPopup.RefreshNotableVolunteers(hero, customCulture, hero.StringId);
                        reapplied++;
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("OnHourlyTickReapplyCustomCultures: RefreshNotableVolunteers failed for " + hero.StringId + ": " + ex.ToString()); }
                }
                if (reapplied > 0)
                    MCMSettings.DebugLog("OnHourlyTickReapplyCustomCultures: re-applied volunteer override for " + reapplied + " notable(s)");
            }
            catch (Exception ex) { MCMSettings.DebugLog("OnHourlyTickReapplyCustomCultures failed: " + ex.ToString()); }
        }

        public override void SyncData(IDataStore dataStore)
        {
            // Ensure Instance is set (defensive — RegisterEvents may not have run yet)
            if (Instance == null) Instance = this;

            // Track that a save was loaded — RegisterEvents fires AFTER SyncData on load,
            // so we can't cancel the intro here (RegisterEvents would re-enable it).
            // Instead, RegisterEvents checks _loadedFromSave and skips scheduling.
            if (dataStore.IsLoading)
            {
                _loadedFromSave = true;
                MCMSettings.DebugLog("SyncData: marking as loaded save (intro will be suppressed)");
            }

            dataStore.SyncData("EditableEncyclopedia_Descriptions", ref _customDescriptions);
            dataStore.SyncData("EditableEncyclopedia_Names", ref _customNames);
            dataStore.SyncData("EditableEncyclopedia_Timestamps", ref _editTimestamps);
            dataStore.SyncData("EditableEncyclopedia_BannerCodes", ref _customBannerCodes);
            dataStore.SyncData("EditableEncyclopedia_Cultures", ref _customCultures);
            dataStore.SyncData("EditableEncyclopedia_Occupations", ref _customOccupations);
            dataStore.SyncData("EditableEncyclopedia_CultureDefs", ref _customCultureDefs);
            dataStore.SyncData("EditableEncyclopedia_HeroInfoFields", ref _heroInfoFields);
            dataStore.SyncData("EditableEncyclopedia_Tags", ref _customTags);
            dataStore.SyncData("EditableEncyclopedia_Journal", ref _journalEntries);
            dataStore.SyncData("EditableEncyclopedia_BattleStats", ref _battleStats);
            dataStore.SyncData("EditableEncyclopedia_TimelineCollapse", ref _timelineCollapseStates);
            dataStore.SyncData("EditableEncyclopedia_RelationNotes", ref _relationNotes);
            dataStore.SyncData("EditableEncyclopedia_RelationHistory", ref _relationHistory);
            dataStore.SyncData("EditableEncyclopedia_TagNotes", ref _tagNotes);
            dataStore.SyncData("EditableEncyclopedia_TagCategories", ref _tagCategories);
            dataStore.SyncData("EditableEncyclopedia_TagPresets", ref _tagPresets);
            dataStore.SyncData("EditableEncyclopedia_PerHeroAutoTagThresholds", ref _perHeroAutoTagThresholds);
            dataStore.SyncData("EditableEncyclopedia_RelationNoteTags", ref _relationNoteTags);
            dataStore.SyncData("EditableEncyclopedia_RelationNoteTagLocks", ref _relationNoteTagLocks);
            dataStore.SyncData("EditableEncyclopedia_ItemLoreOverrides", ref _itemLoreOverrides);
            // New in v2.6.2. Saves written before this key existed simply leave the field at its
            // field-initialiser value (an empty dictionary) — SyncData does not clear or throw for
            // a key it cannot find, which is the same way _battleStats (field 19) and
            // _itemLoreOverrides (field 20) were introduced into already-shipped saves.
            dataStore.SyncData("EditableEncyclopedia_ChronicleSpoils", ref _chronicleSpoils);

            // Use dataStore.IsLoading for reliable load detection — the old ReferenceEquals
            // approach failed when saves had no prior mod data (empty dictionaries aren't replaced)
            bool isLoading = dataStore.IsLoading;

            if (_customDescriptions == null)
                _customDescriptions = new Dictionary<string, string>();
            if (_customNames == null)
                _customNames = new Dictionary<string, string>();
            if (_editTimestamps == null)
                _editTimestamps = new Dictionary<string, string>();
            if (_customBannerCodes == null)
                _customBannerCodes = new Dictionary<string, string>();
            if (_customCultures == null)
                _customCultures = new Dictionary<string, string>();
            if (_customOccupations == null)
                _customOccupations = new Dictionary<string, int>();
            if (_customCultureDefs == null)
                _customCultureDefs = new Dictionary<string, string>();
            if (_heroInfoFields == null)
                _heroInfoFields = new Dictionary<string, string>();
            if (_customTags == null)
                _customTags = new Dictionary<string, string>();
            if (_journalEntries == null)
                _journalEntries = new Dictionary<string, string>();
            if (_timelineCollapseStates == null)
                _timelineCollapseStates = new Dictionary<string, string>();
            if (_relationNotes == null)
                _relationNotes = new Dictionary<string, string>();
            if (_relationHistory == null)
                _relationHistory = new Dictionary<string, string>();
            if (_tagNotes == null)
                _tagNotes = new Dictionary<string, string>();
            if (_tagCategories == null)
                _tagCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_tagPresets == null)
                _tagPresets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_perHeroAutoTagThresholds == null)
                _perHeroAutoTagThresholds = new Dictionary<string, string>();
            if (_relationNoteTags == null)
                _relationNoteTags = new Dictionary<string, string>();
            if (_relationNoteTagLocks == null)
                _relationNoteTagLocks = new Dictionary<string, string>();
            if (_itemLoreOverrides == null)
                _itemLoreOverrides = new Dictionary<string, string>();
            if (_chronicleSpoils == null)
                _chronicleSpoils = new Dictionary<string, string>();

            if (isLoading)
            {
                MCMSettings.DebugLog("SyncData: loading save ("
                    + _customDescriptions.Count + " desc, "
                    + _customNames.Count + " names, "
                    + _customBannerCodes.Count + " banners)");

                // Immediately modify Settlement.Name TextObjects in-place for any custom names.
                // This MUST happen before the nameplate LoadMovie creates widgets (which bake the text).
                try
                {
                    if (_customNames != null && _customNames.Count > 0)
                    {
                        int immediateNames = 0;
                        var toFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                        foreach (var settlement in TaleWorlds.CampaignSystem.Settlements.Settlement.All)
                        {
                            if (settlement == null) continue;
                            string customName = GetCustomName(settlement.StringId);
                            if (string.IsNullOrEmpty(customName)) continue;

                            var nameTO = settlement.Name;
                            if (nameTO != null)
                            {
                                var valueField = typeof(TaleWorlds.Localization.TextObject).GetField("Value", toFlags);
                                if (valueField != null)
                                {
                                    valueField.SetValue(nameTO, customName);
                                    var cachedTokens = typeof(TaleWorlds.Localization.TextObject).GetField("cachedTokens", toFlags);
                                    if (cachedTokens != null) cachedTokens.SetValue(nameTO, null);
                                    var cachedLangId = typeof(TaleWorlds.Localization.TextObject).GetField("cachedTextLanguageId", toFlags);
                                    if (cachedLangId != null) cachedLangId.SetValue(nameTO, -1);
                                    immediateNames++;
                                }
                            }
                        }
                        MCMSettings.DebugLog("SyncData: immediately applied " + immediateNames + " settlement name TextObjects in-place");
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("SyncData: immediate name apply failed: " + ex.ToString()); }

                // Clear timeline cache — stale entries from previous session would cause
                // duplicates since the native LogEntryHistory is re-created on load.
                try { TimelineDataCollector.ClearCache(); } catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: TimelineDataCollector.ClearCache failed: " + ex.ToString()); }

                // Deduplicate journal entries on load — remove exact duplicate lines
                // that may have accumulated from event replay on previous loads
                try { DeduplicateJournalEntries(); } catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: DeduplicateJournalEntries failed: " + ex.ToString()); }

                // One-time seed of persistent battle counters from existing journal text
                // (so existing players don't reset to 0 after the upgrade)
                if (_battleStats == null) _battleStats = new Dictionary<string, string>();
                try { MigrateBattleStatsFromJournal(); } catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: MigrateBattleStatsFromJournal failed: " + ex.ToString()); }

                // Pre-populate the auto-log dedup set with existing journal entries
                // so events replayed during load don't get re-logged.
                // Reset _campaignFullyLoaded so the dedup set is preserved until first DailyTick.
                _campaignFullyLoaded = false;
                _autoLoggedThisDay.Clear();
                _lastAutoLogDay = -1f;
                try { PrePopulateAutoLogDedup(); } catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: PrePopulateAutoLogDedup failed: " + ex.ToString()); }

                // Drop transient spoils state left over from a previous campaign in this session,
                // and discard any saved spoils whose chronicle line no longer exists (the journal
                // is trimmed to MaxJournalEntriesPerEntity, which can orphan a record).
                try { ChronicleSpoilsCollector.Reset(); } catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: ChronicleSpoilsCollector.Reset failed: " + ex.ToString()); }
                try { PruneOrphanedChronicleSpoils(); } catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: PruneOrphanedChronicleSpoils failed: " + ex.ToString()); }

                // Schedule a full banner re-apply after loading a save with custom banners
                if (_customBannerCodes.Count > 0)
                    NeedsBannerReapply = true;

                // Schedule name re-apply after loading a save with custom names
                if (_customNames.Count > 0)
                {
                    NeedsNameReapply = true;
                    MCMSettings.DebugLog("SyncData: scheduling name re-apply for " + _customNames.Count + " entries");
                }

                // Re-register custom culture definitions (troop trees) from the save
                if (_customCultureDefs.Count > 0)
                {
                    // Phase 1 — IMMEDIATELY recreate CultureObjects in MBObjectManager so any
                    // settlement.Culture access between SyncData and the first tick won't NRE.
                    // The hero/settlement reapply still defers via NeedsCustomCultureReapply
                    // because Hero.AllAliveHeroes/Clan.Settlements may not be populated yet.
                    try
                    {
                        CustomCultureManager.RegisterOnlyFromSaveData();
                        MCMSettings.DebugLog("SyncData: pre-registered " + _customCultureDefs.Count + " custom CultureObjects (phase 1)");
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("SyncData: phase-1 culture register failed: " + ex.ToString()); }

                    NeedsCustomCultureReapply = true;
                    MCMSettings.DebugLog("SyncData: scheduling phase-2 hero/settlement culture reapply");
                }

                // Validate custom cultures on load — remove orphaned references
                try
                {
                    ValidateCustomCulturesOnLoad();
                }
                catch (Exception ex) { MCMSettings.DebugLog("SyncData: culture validation failed: " + ex.ToString()); }

                // Auto-import from JSON file on load if enabled
                try
                {
                    var settings = MCMSettings.Instance;
                    if (settings != null && settings.AutoImportOnLoad)
                    {
                        // Import directly to avoid IsAvailable check (Instance may not be set yet)
                        var data = SharedFileExporter.ImportAll();
                        if (data != null)
                        {
                            int descCount = 0, nameCount = 0, titleCount = 0, bannerCount = 0, cultureDefCount = 0, cultureCount = 0, occupationCount = 0, infoFieldCount = 0, tagCount = 0;
                            if (data.Descriptions != null)
                                descCount = ImportDescriptions(data.Descriptions);
                            if (data.Names != null)
                                nameCount = ImportNames(data.Names);
                            if (data.Titles != null)
                                titleCount = ImportTitles(data.Titles);
                            if (data.Banners != null)
                                bannerCount = ImportBanners(data.Banners);
                            if (data.CultureDefs != null)
                                cultureDefCount = ImportCultureDefs(data.CultureDefs);
                            if (data.Cultures != null)
                                cultureCount = ImportCultures(data.Cultures);
                            if (data.Occupations != null)
                                occupationCount = ImportOccupations(data.Occupations);
                            if (data.HeroInfoFields != null)
                                infoFieldCount = ImportHeroInfoFields(data.HeroInfoFields);
                            if (data.Tags != null)
                                tagCount = ImportTags(data.Tags);
                            if (data.Timestamps != null)
                                ImportTimestamps(data.Timestamps);
                            if (data.Journal != null)
                                ImportJournal(data.Journal);
                            if (data.RelationNotes != null)
                                ImportRelationNotes(data.RelationNotes);
                            if (data.TagNotes != null)
                                ImportTagNotes(data.TagNotes);
                            if (data.RelationHistory != null)
                                ImportRelationHistory(data.RelationHistory);
                            if (data.TagCategories != null)
                                ImportTagCategories(data.TagCategories);
                            if (data.TagPresets != null)
                                ImportTagPresets(data.TagPresets);
                            if (data.PerHeroAutoTagThresholds != null)
                                ImportPerHeroAutoTagThresholds(data.PerHeroAutoTagThresholds);
                            if (data.RelationNoteTags != null)
                                ImportRelationNoteTags(data.RelationNoteTags);
                            if (data.RelationNoteTagLocks != null)
                                ImportRelationNoteTagLocks(data.RelationNoteTagLocks);
                            if (data.ItemLore != null)
                                ImportItemLore(data.ItemLore);

                            int total = descCount + nameCount + titleCount + bannerCount + cultureDefCount + cultureCount + occupationCount + infoFieldCount + tagCount;
                            if (total > 0)
                            {
                                MCMSettings.DebugLog("Auto-imported from JSON on load: "
                                    + descCount + " desc, " + nameCount + " names, "
                                    + titleCount + " titles, " + bannerCount + " banners, "
                                    + cultureDefCount + " cultureDefs, "
                                    + cultureCount + " cultures, " + occupationCount + " occupations");

                                if (bannerCount > 0)
                                    NeedsBannerReapply = true;
                                if (cultureDefCount > 0)
                                    NeedsCustomCultureReapply = true;

                                TaleWorlds.Library.InformationManager.DisplayMessage(
                                    new TaleWorlds.Library.InformationMessage(
                                        Localization.L("msg_auto_import_success",
                                            descCount, nameCount, titleCount, bannerCount, cultureCount, occupationCount, cultureDefCount, infoFieldCount, tagCount),
                                        TaleWorlds.Library.Colors.Green));
                            }
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    MCMSettings.DebugLog("Auto-import on load failed: " + ex.ToString());
                }

                // Auto-tags will be lazily evaluated on first encyclopedia page access
                _autoTagsEvaluated = false;
            }
            else
            {
                MCMSettings.DebugLog("SyncData: saving game ("
                    + _customDescriptions.Count + " desc, "
                    + _customNames.Count + " names, "
                    + _customBannerCodes.Count + " banners)");

                // Auto-export on save if enabled
                try
                {
                    var settings = MCMSettings.Instance;
                    if (settings != null && settings.AutoExportOnSave)
                    {
                        var descriptions = GetAllDescriptions();
                        var names = GetAllCustomNames();
                        var titles = GetAllCustomTitles();
                        var banners = GetAllCustomBanners();
                        var cultures = GetAllCustomCulturesForExport();
                        var occupations = GetAllCustomOccupationsForExport();
                        var cultureDefs = GetAllCustomCultureDefsForExport();
                        var heroInfoFields = GetAllHeroInfoFieldsForExport();
                        var tags = GetAllTagsForExport();
                        var timestamps = GetAllTimestampsForExport();
                        var journal = GetAllJournalForExport();
                        var relationNotes = GetAllRelationNotesForExport();
                        var tagNotes = GetAllTagNotesForExport();
                        var relationHistory = GetAllRelationHistoryForExport();
                        var tagCategories = GetAllTagCategories();
                        var tagPresets = GetAllTagPresets();
                        var autoTagThresholds = GetAllPerHeroAutoTagThresholds();
                        var relNoteTags = GetAllRelationNoteTagsForExport();
                        var relNoteTagLocks = GetAllRelationNoteTagLocksForExport();
                        var itemLore = GetAllItemLoreForExport();
                        bool exported = SharedFileExporter.Export(descriptions, names, titles, banners, cultures, occupations, cultureDefs, heroInfoFields, tags, timestamps, journal, relationNotes, tagNotes, relationHistory, tagCategories, tagPresets, autoTagThresholds, relNoteTags, relNoteTagLocks, itemLore);
                        MCMSettings.DebugLog("Auto-exported all data on game save: " + (exported ? "success" : "failed"));
                    }
                }
                catch (System.Exception ex)
                {
                    MCMSettings.DebugLog("Auto-export on save failed: " + ex.ToString());
                }

            }

            // Intro is handled in RegisterEvents (fires for both new games and loads)
        }

        /// <summary>
        /// Gets the custom description for an object, or null if none was set.
        /// </summary>
        public string GetDescription(string objectId)
        {
            if (_customDescriptions.TryGetValue(objectId, out var desc))
                return desc;
            return null;
        }

        /// <summary>
        /// Sets (or clears) a custom description for an object.
        /// </summary>
        public void SetDescription(string objectId, string description)
        {
            if (string.IsNullOrWhiteSpace(description))
            {
                _customDescriptions.Remove(objectId);
                _editTimestamps.Remove(objectId);
            }
            else
            {
                _customDescriptions[objectId] = description;

                // Record the in-game date when this description was edited
                try
                {
                    _editTimestamps[objectId] = GetCurrentGameDateString();
                }
                catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: SetDescription timestamp failed: " + ex.ToString()); }
            }

            // Fire the change event for cross-mod listeners
            EditableEncyclopediaAPI.RaiseDescriptionChanged(objectId,
                string.IsNullOrWhiteSpace(description) ? null : description);
        }

        /// <summary>
        /// Returns true if the player has written a custom description for this object.
        /// </summary>
        public bool HasCustomDescription(string objectId)
        {
            return _customDescriptions.ContainsKey(objectId);
        }

        // ── Timestamp methods ──

        /// <summary>
        /// Gets the formatted in-game date when this object's description was last edited, or null.
        /// </summary>
        public string GetEditTimestamp(string objectId)
        {
            if (_editTimestamps.TryGetValue(objectId, out var ts))
                return ts;
            return null;
        }

        /// <summary>
        /// Returns the current in-game date as a formatted string like "Day 15 of Spring, 1084".
        /// </summary>
        internal static string GetCurrentGameDateString()
        {
            var now = CampaignTime.Now;
            int year = now.GetYear;
            int season = (int)now.GetSeasonOfYear;

            int rawDay = now.GetDayOfSeason;
            int day = rawDay + 1; // 0-based → 1-based

            string[] seasonNames = { "Spring", "Summer", "Autumn", "Winter" };
            string seasonName = (season >= 0 && season < seasonNames.Length) ? seasonNames[season] : "Unknown";

            return "Day " + day + " of " + seasonName + ", " + year;
        }

        // ── Custom Name/Title methods ──

        public string GetCustomName(string objectId)
        {
            if (_customNames.TryGetValue("name_" + objectId, out var name))
                return name;
            return null;
        }

        public string GetCustomTitle(string objectId)
        {
            if (_customNames.TryGetValue("title_" + objectId, out var title))
                return title;
            return null;
        }

        public void SetCustomName(string objectId, string name)
        {
            string key = "name_" + objectId;
            if (string.IsNullOrWhiteSpace(name))
                _customNames.Remove(key);
            else
                _customNames[key] = name;
        }

        /// <summary>
        /// Stores the original hero name (before any custom edits) so it can be restored on reset.
        /// Only stored once — if already set, does nothing.
        /// </summary>
        public void StoreOriginalName(string objectId, string originalName)
        {
            string key = "origname_" + objectId;
            if (!_customNames.ContainsKey(key) && !string.IsNullOrEmpty(originalName))
                _customNames[key] = originalName;
        }

        public string GetOriginalName(string objectId)
        {
            if (_customNames.TryGetValue("origname_" + objectId, out var name))
                return name;
            return null;
        }

        public void SetCustomTitle(string objectId, string title)
        {
            string key = "title_" + objectId;
            if (string.IsNullOrWhiteSpace(title))
                _customNames.Remove(key);
            else
                _customNames[key] = title;
        }

        /// <summary>
        /// Stores the original hero title (before any custom edits) so it can be restored on reset.
        /// Only stored once — if already set, does nothing.
        /// </summary>
        public void StoreOriginalTitle(string objectId, string originalTitle)
        {
            string key = "origtitle_" + objectId;
            if (!_customNames.ContainsKey(key))
                _customNames[key] = originalTitle ?? "";
        }

        public string GetOriginalTitle(string objectId)
        {
            if (_customNames.TryGetValue("origtitle_" + objectId, out var title))
                return title;
            return null;
        }

        public bool HasCustomName(string objectId)
        {
            return _customNames.ContainsKey("name_" + objectId);
        }

        public bool HasCustomTitle(string objectId)
        {
            return _customNames.ContainsKey("title_" + objectId);
        }

        // ── Custom Banner methods ──

        public string GetCustomBannerCode(string objectId)
        {
            if (_customBannerCodes.TryGetValue(objectId, out var code))
                return code;
            return null;
        }

        public void SetCustomBannerCode(string objectId, string bannerCode)
        {
            if (string.IsNullOrWhiteSpace(bannerCode))
                _customBannerCodes.Remove(objectId);
            else
                _customBannerCodes[objectId] = bannerCode;
        }

        public bool HasCustomBannerCode(string objectId)
        {
            return _customBannerCodes.ContainsKey(objectId);
        }

        /// <summary>
        /// Stores the original banner code so it can be restored on reset.
        /// Only stored once per object.
        /// </summary>
        public void StoreOriginalBannerCode(string objectId, string bannerCode)
        {
            string key = "origbanner_" + objectId;
            if (!_customBannerCodes.ContainsKey(key) && !string.IsNullOrEmpty(bannerCode))
                _customBannerCodes[key] = bannerCode;
        }

        public string GetOriginalBannerCode(string objectId)
        {
            if (_customBannerCodes.TryGetValue("origbanner_" + objectId, out var code))
                return code;
            return null;
        }

        // ── Custom Culture methods ──

        public string GetCustomCulture(string objectId)
        {
            if (_customCultures.TryGetValue(objectId, out var cultureId))
                return cultureId;
            return null;
        }

        public void SetCustomCulture(string objectId, string cultureId)
        {
            if (string.IsNullOrWhiteSpace(objectId)) return;
            if (string.IsNullOrWhiteSpace(cultureId))
            {
                _customCultures.Remove(objectId);
            }
            else
            {
                // Validate that the cultureId is either a known game culture or a registered custom culture
                bool valid = false;
                try
                {
                    var objMgr = TaleWorlds.ObjectSystem.MBObjectManager.Instance;
                    if (objMgr != null)
                    {
                        foreach (var c in objMgr.GetObjectTypeList<TaleWorlds.CampaignSystem.CultureObject>())
                        {
                            if (c != null && string.Equals(c.StringId, cultureId, StringComparison.OrdinalIgnoreCase))
                            { valid = true; break; }
                        }
                    }
                    // Also accept custom culture definitions
                    if (!valid && HasCustomCultureDefinition(cultureId)) valid = true;
                    // Also accept display names that map to known cultures
                    if (!valid)
                    {
                        string matchId = FindCustomCultureIdByDisplayName(cultureId);
                        if (!string.IsNullOrEmpty(matchId)) valid = true;
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("SetCustomCulture validation error: " + ex.ToString()); }

                if (!valid)
                    MCMSettings.DebugLog("WARNING: SetCustomCulture for " + objectId + " with unvalidated culture '" + cultureId + "' — may cause issues on reload");

                _customCultures[objectId] = cultureId;
            }
        }

        public bool HasCustomCulture(string objectId)
        {
            return _customCultures.ContainsKey(objectId);
        }

        /// <summary>
        /// Validates all custom culture assignments on save load.
        /// Removes entries pointing to cultures that no longer exist in the game,
        /// preventing crashes from orphaned references.
        /// </summary>
        private void ValidateCustomCulturesOnLoad()
        {
            if (_customCultures == null || _customCultures.Count == 0) return;

            // Build a set of valid culture StringIds
            var validCultures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var objMgr = TaleWorlds.ObjectSystem.MBObjectManager.Instance;
                if (objMgr != null)
                {
                    foreach (var c in objMgr.GetObjectTypeList<TaleWorlds.CampaignSystem.CultureObject>())
                    {
                        if (c?.StringId != null) validCultures.Add(c.StringId);
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("ValidateCustomCultures: failed to enumerate cultures: " + ex.ToString()); return; }

            // Also accept custom culture definition IDs
            if (_customCultureDefs != null)
            {
                foreach (var key in _customCultureDefs.Keys)
                    validCultures.Add(key);
            }

            // Check each non-metadata entry (skip "orig_", "displayname_" prefixed keys)
            var orphaned = new List<string>();
            foreach (var kvp in _customCultures)
            {
                if (kvp.Key.StartsWith("orig_") || kvp.Key.StartsWith("displayname_")) continue;
                if (string.IsNullOrWhiteSpace(kvp.Value)) { orphaned.Add(kvp.Key); continue; }
                if (kvp.Value == "null" || kvp.Value == "None") { orphaned.Add(kvp.Key); continue; }

                // Check if the assigned culture ID is valid
                if (!validCultures.Contains(kvp.Value))
                {
                    // Also check if it matches a display name
                    bool foundByName = false;
                    if (_customCultureDefs != null)
                    {
                        foreach (var def in _customCultureDefs.Values)
                        {
                            var parts = def.Split('|');
                            if (parts.Length > 0 && string.Equals(parts[0], kvp.Value, StringComparison.OrdinalIgnoreCase))
                            { foundByName = true; break; }
                        }
                    }
                    if (!foundByName)
                        orphaned.Add(kvp.Key);
                }
            }

            // Remove orphaned entries
            if (orphaned.Count > 0)
            {
                foreach (var key in orphaned)
                {
                    MCMSettings.DebugLog("ValidateCustomCultures: removing orphaned culture assignment for '" + key + "' (was '" + _customCultures[key] + "')");
                    _customCultures.Remove(key);
                }
                MCMSettings.DebugLog("ValidateCustomCultures: cleaned up " + orphaned.Count + " orphaned culture assignments");
            }
            else
            {
                MCMSettings.DebugLog("ValidateCustomCultures: all " + _customCultures.Count + " culture entries are valid");
            }
        }

        public void StoreOriginalCulture(string objectId, string cultureId)
        {
            if (string.IsNullOrWhiteSpace(objectId) || string.IsNullOrWhiteSpace(cultureId)) return;
            // Reject obviously invalid values like "null", "None", empty-like strings
            if (cultureId == "null" || cultureId == "None" || cultureId.Length < 2) return;
            string key = "orig_" + objectId;
            if (!_customCultures.ContainsKey(key))
                _customCultures[key] = cultureId;
        }

        public string GetOriginalCulture(string objectId)
        {
            if (_customCultures.TryGetValue("orig_" + objectId, out var cultureId))
                return cultureId;
            return null;
        }

        public void SetCustomCultureDisplayName(string objectId, string displayName)
        {
            string key = "displayname_" + objectId;
            if (string.IsNullOrWhiteSpace(displayName))
                _customCultures.Remove(key);
            else
                _customCultures[key] = displayName;
        }

        public string GetCustomCultureDisplayName(string objectId)
        {
            if (_customCultures.TryGetValue("displayname_" + objectId, out var name))
                return name;
            return null;
        }

        /// <summary>
        /// Removes all custom culture data for a hero (custom culture id, display name).
        /// Does NOT remove the stored original — that's needed for future resets.
        /// </summary>
        public void RemoveCustomCulture(string objectId)
        {
            _customCultures.Remove(objectId);
            _customCultures.Remove("displayname_" + objectId);
        }

        public int GetCustomCultureCount()
        {
            int count = 0;
            foreach (var key in _customCultures.Keys)
                if (!key.StartsWith("orig_") && !key.StartsWith("displayname_")) count++;
            return count;
        }

        // ── Custom Culture Definition methods (troop trees) ──

        /// <summary>
        /// Stores a custom culture definition with troop tree assignments.
        /// </summary>
        /// <param name="customCultureId">Unique id for the custom culture (e.g. "ee_custom_myname_1234")</param>
        /// <param name="displayName">Player-chosen display name</param>
        /// <param name="baseCultureId">StringId of the base culture to clone visual properties from</param>
        /// <param name="basicTroopId">StringId of the CharacterObject to use as basic troop root</param>
        /// <param name="eliteTroopId">StringId of the CharacterObject to use as elite troop root</param>
        public void SetCustomCultureDefinition(string customCultureId, string displayName, string baseCultureId, string basicTroopId, string eliteTroopId)
        {
            if (string.IsNullOrWhiteSpace(customCultureId) || string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(baseCultureId))
            {
                MCMSettings.DebugLog("SetCustomCultureDefinition: rejected — missing required fields");
                return;
            }
            _customCultureDefs[customCultureId] = displayName + "|" + baseCultureId + "|" + (basicTroopId ?? "") + "|" + (eliteTroopId ?? "");
        }

        /// <summary>
        /// Gets a custom culture definition by id.
        /// Returns (displayName, baseCultureId, basicTroopId, eliteTroopId) or null if not found.
        /// </summary>
        public Tuple<string, string, string, string> GetCustomCultureDefinition(string customCultureId)
        {
            if (!_customCultureDefs.TryGetValue(customCultureId, out var value))
                return null;
            var parts = value.Split('|');
            if (parts.Length < 4) return null;
            return Tuple.Create(parts[0], parts[1], parts[2], parts[3]);
        }

        /// <summary>
        /// Returns true if a custom culture definition exists with this id.
        /// </summary>
        public bool HasCustomCultureDefinition(string customCultureId)
        {
            return _customCultureDefs.ContainsKey(customCultureId);
        }

        /// <summary>
        /// Removes a custom culture definition.
        /// </summary>
        public void RemoveCustomCultureDefinition(string customCultureId)
        {
            _customCultureDefs.Remove(customCultureId);
        }

        /// <summary>
        /// Returns all custom culture definitions as a list of (id, displayName, baseCultureId, basicTroopId, eliteTroopId).
        /// </summary>
        public List<Tuple<string, string, string, string, string>> GetAllCustomCultureDefinitions()
        {
            var result = new List<Tuple<string, string, string, string, string>>();
            foreach (var kvp in _customCultureDefs)
            {
                var parts = kvp.Value.Split('|');
                if (parts.Length >= 4)
                    result.Add(Tuple.Create(kvp.Key, parts[0], parts[1], parts[2], parts[3]));
            }
            return result;
        }

        /// <summary>
        /// Finds a custom culture definition id by its display name (case-insensitive).
        /// Returns the custom culture id, or null if not found.
        /// </summary>
        public string FindCustomCultureIdByDisplayName(string displayName)
        {
            foreach (var kvp in _customCultureDefs)
            {
                var parts = kvp.Value.Split('|');
                if (parts.Length >= 1 && string.Equals(parts[0], displayName, StringComparison.OrdinalIgnoreCase))
                    return kvp.Key;
            }
            return null;
        }

        public int GetCustomCultureDefinitionCount()
        {
            return _customCultureDefs.Count;
        }

        // ── Custom Occupation methods ──

        public int GetCustomOccupation(string objectId)
        {
            if (_customOccupations.TryGetValue(objectId, out var occupation))
                return occupation;
            return -1; // -1 means no custom occupation
        }

        public void SetCustomOccupation(string objectId, int occupation)
        {
            if (occupation < 0)
                _customOccupations.Remove(objectId);
            else
                _customOccupations[objectId] = occupation;
        }

        public bool HasCustomOccupation(string objectId)
        {
            return _customOccupations.ContainsKey(objectId);
        }

        public void StoreOriginalOccupation(string objectId, int occupation)
        {
            string key = "orig_" + objectId;
            if (!_customOccupations.ContainsKey(key))
                _customOccupations[key] = occupation;
        }

        public int GetOriginalOccupation(string objectId)
        {
            if (_customOccupations.TryGetValue("orig_" + objectId, out var occupation))
                return occupation;
            return -1;
        }

        /// <summary>
        /// Removes all custom occupation data for a hero.
        /// Does NOT remove the stored original — that's needed for future resets.
        /// </summary>
        public void RemoveCustomOccupation(string objectId)
        {
            _customOccupations.Remove(objectId);
        }

        public int GetCustomOccupationCount()
        {
            int count = 0;
            foreach (var key in _customOccupations.Keys)
                if (!key.StartsWith("orig_")) count++;
            return count;
        }

        /// <summary>
        /// Removes custom culture/occupation entries for heroes that no longer exist,
        /// and cleans up orphaned orig_/displayname_ keys whose base entry is gone.
        /// Call this after culture/occupation changes or before building sidebar filters.
        /// </summary>
        public void PurgeOrphanedCustomEntries()
        {
            // Build set of valid hero IDs
            var validHeroIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in Hero.AllAliveHeroes)
                if (h != null && !string.IsNullOrEmpty(h.StringId))
                    validHeroIds.Add(h.StringId);
            foreach (var h in Hero.DeadOrDisabledHeroes)
                if (h != null && !string.IsNullOrEmpty(h.StringId))
                    validHeroIds.Add(h.StringId);

            int removed = 0;

            // --- Clean cultures (use snapshot to avoid collection-modified crash) ---
            var cultureKeysToRemove = new List<string>();
            var cultureKeysSnapshot = new List<string>(_customCultures.Keys);
            foreach (var key in cultureKeysSnapshot)
            {
                string heroId;
                if (key.StartsWith("orig_") && key.Length > 5)
                    heroId = key.Substring(5);
                else if (key.StartsWith("displayname_") && key.Length > 12)
                    heroId = key.Substring(12);
                else
                    heroId = key;

                // Remove if the hero no longer exists
                if (!string.IsNullOrEmpty(heroId) && !validHeroIds.Contains(heroId))
                    cultureKeysToRemove.Add(key);
            }

            // Also remove displayname/orig entries whose base custom entry was already removed
            foreach (var key in cultureKeysSnapshot)
            {
                if (cultureKeysToRemove.Contains(key)) continue;
                if (key.StartsWith("displayname_") && key.Length > 12)
                {
                    string heroId = key.Substring(12);
                    if (!string.IsNullOrEmpty(heroId) && !_customCultures.ContainsKey(heroId))
                        cultureKeysToRemove.Add(key);
                }
                else if (key.StartsWith("orig_") && key.Length > 5)
                {
                    string heroId = key.Substring(5);
                    if (!string.IsNullOrEmpty(heroId) && !_customCultures.ContainsKey(heroId))
                        cultureKeysToRemove.Add(key);
                }
            }

            foreach (var key in cultureKeysToRemove)
            {
                _customCultures.Remove(key);
                removed++;
            }

            // --- Clean occupations (use snapshot) ---
            var occKeysToRemove = new List<string>();
            var occKeysSnapshot = new List<string>(_customOccupations.Keys);
            foreach (var key in occKeysSnapshot)
            {
                string heroId = (key.StartsWith("orig_") && key.Length > 5) ? key.Substring(5) : key;
                if (!string.IsNullOrEmpty(heroId) && !validHeroIds.Contains(heroId))
                    occKeysToRemove.Add(key);
            }

            // Remove orig entries whose base entry is gone
            foreach (var key in occKeysSnapshot)
            {
                if (occKeysToRemove.Contains(key)) continue;
                if (key.StartsWith("orig_") && key.Length > 5)
                {
                    string heroId = key.Substring(5);
                    if (!string.IsNullOrEmpty(heroId) && !_customOccupations.ContainsKey(heroId))
                        occKeysToRemove.Add(key);
                }
            }

            foreach (var key in occKeysToRemove)
            {
                _customOccupations.Remove(key);
                removed++;
            }

            if (removed > 0)
                MCMSettings.DebugLog("Purged " + removed + " orphaned custom entries (" + cultureKeysToRemove.Count + " culture, " + occKeysToRemove.Count + " occupation)");
        }

        /// <summary>
        /// Returns all custom culture entries as (heroObjectId, cultureStringId, displayName) tuples.
        /// Skips "orig_" and "displayname_" prefixed keys.
        /// </summary>
        public List<Tuple<string, string, string>> GetAllCustomCultureEntries()
        {
            var result = new List<Tuple<string, string, string>>();
            foreach (var kvp in _customCultures)
            {
                if (kvp.Key.StartsWith("orig_") || kvp.Key.StartsWith("displayname_"))
                    continue;
                string heroId = kvp.Key;
                string cultureId = kvp.Value;
                string displayName = GetCustomCultureDisplayName(heroId);
                result.Add(Tuple.Create(heroId, cultureId, displayName));
            }
            return result;
        }

        /// <summary>
        /// Removes ALL custom culture data for a hero including the stored original.
        /// Use this for permanent deletion from the MCM management screen.
        /// </summary>
        public void DeleteCustomCultureFull(string objectId)
        {
            _customCultures.Remove(objectId);
            _customCultures.Remove("displayname_" + objectId);
            _customCultures.Remove("orig_" + objectId);
        }

        /// <summary>
        /// Returns all custom occupation entries as (heroObjectId, occupationValue) tuples.
        /// Skips "orig_" prefixed keys.
        /// </summary>
        public List<Tuple<string, int>> GetAllCustomOccupationEntries()
        {
            var result = new List<Tuple<string, int>>();
            foreach (var kvp in _customOccupations)
            {
                if (kvp.Key.StartsWith("orig_"))
                    continue;
                result.Add(Tuple.Create(kvp.Key, kvp.Value));
            }
            return result;
        }

        /// <summary>
        /// Removes ALL custom occupation data for a hero including the stored original.
        /// Use this for permanent deletion from the MCM management screen.
        /// </summary>
        public void DeleteCustomOccupationFull(string objectId)
        {
            _customOccupations.Remove(objectId);
            _customOccupations.Remove("orig_" + objectId);
        }

        // ── Culture/Occupation Export Helpers ──

        /// <summary>
        /// Returns all custom cultures as a dictionary suitable for JSON export.
        /// Key = heroId, Value = "cultureId|displayName" (pipe-separated).
        /// Skips "orig_" and "displayname_" prefixed keys.
        /// </summary>
        public Dictionary<string, string> GetAllCustomCulturesForExport()
        {
            var result = new Dictionary<string, string>();
            foreach (var kvp in _customCultures)
            {
                if (kvp.Key.StartsWith("orig_") || kvp.Key.StartsWith("displayname_"))
                    continue;
                string heroId = kvp.Key;
                string cultureId = kvp.Value;
                string displayName = GetCustomCultureDisplayName(heroId);
                // Format: "cultureId|displayName" or just "cultureId" if no custom display name
                string exportValue = string.IsNullOrEmpty(displayName)
                    ? cultureId
                    : cultureId + "|" + displayName;
                result[heroId] = exportValue;
            }
            return result;
        }

        /// <summary>
        /// Returns all custom occupations as a dictionary suitable for JSON export.
        /// Key = heroId, Value = occupation enum name (e.g., "GangLeader").
        /// Skips "orig_" prefixed keys.
        /// </summary>
        public Dictionary<string, string> GetAllCustomOccupationsForExport()
        {
            var result = new Dictionary<string, string>();
            foreach (var kvp in _customOccupations)
            {
                if (kvp.Key.StartsWith("orig_"))
                    continue;
                string heroId = kvp.Key;
                try
                {
                    string occName = ((TaleWorlds.CampaignSystem.Occupation)kvp.Value).ToString();
                    result[heroId] = occName;
                }
                catch
                {
                    result[heroId] = kvp.Value.ToString();
                }
            }
            return result;
        }

        /// <summary>
        /// Returns the player's custom lore for an (item, owner) pair, or empty
        /// string when none has been written.
        /// </summary>
        public string GetItemLoreOverride(string itemId, string ownerId)
        {
            if (string.IsNullOrEmpty(itemId)) return string.Empty;
            if (_itemLoreOverrides == null) return string.Empty;

            string key = itemId + "|" + ownerId;
            string value;
            return _itemLoreOverrides.TryGetValue(key, out value) ? value : string.Empty;
        }

        /// <summary>
        /// Stores custom lore for an (item, owner) pair. Passing null or empty
        /// clears the override so generated lore returns.
        /// </summary>
        public void SetItemLoreOverride(string itemId, string ownerId, string text)
        {
            if (string.IsNullOrEmpty(itemId)) return;
            if (_itemLoreOverrides == null)
                _itemLoreOverrides = new Dictionary<string, string>();

            string key = itemId + "|" + ownerId;
            if (string.IsNullOrEmpty(text))
                _itemLoreOverrides.Remove(key);
            else
                _itemLoreOverrides[key] = text;
        }

        public Dictionary<string, string> GetAllItemLoreForExport()
        {
            return _itemLoreOverrides != null
                ? new Dictionary<string, string>(_itemLoreOverrides)
                : new Dictionary<string, string>();
        }

        public int ImportItemLore(Dictionary<string, string> itemLore)
        {
            if (itemLore == null) return 0;
            if (_itemLoreOverrides == null)
                _itemLoreOverrides = new Dictionary<string, string>();

            int count = 0;
            foreach (var kvp in itemLore)
            {
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                _itemLoreOverrides[kvp.Key] = kvp.Value;
                count++;
            }
            return count;
        }

        /// <summary>
        /// Imports cultures from a dictionary (heroId → "cultureId|displayName").
        /// Returns the number of entries imported.
        /// </summary>
        public int ImportCultures(Dictionary<string, string> cultures)
        {
            if (cultures == null) return 0;
            int count = 0;
            foreach (var kvp in cultures)
            {
                string heroId = kvp.Key;
                string value = kvp.Value;
                if (string.IsNullOrEmpty(heroId) || string.IsNullOrEmpty(value))
                    continue;

                // Parse "cultureId|displayName" format
                string cultureId;
                string displayName = null;
                int pipeIndex = value.IndexOf('|');
                if (pipeIndex >= 0)
                {
                    cultureId = value.Substring(0, pipeIndex);
                    displayName = value.Substring(pipeIndex + 1);
                }
                else
                {
                    cultureId = value;
                }

                SetCustomCulture(heroId, cultureId);
                if (!string.IsNullOrEmpty(displayName))
                    SetCustomCultureDisplayName(heroId, displayName);

                count++;
            }
            return count;
        }

        /// <summary>
        /// Imports occupations from a dictionary (heroId → occupation enum name).
        /// Returns the number of entries imported.
        /// </summary>
        public int ImportOccupations(Dictionary<string, string> occupations)
        {
            if (occupations == null) return 0;
            int count = 0;
            foreach (var kvp in occupations)
            {
                string heroId = kvp.Key;
                string occName = kvp.Value;
                if (string.IsNullOrEmpty(heroId) || string.IsNullOrEmpty(occName))
                    continue;

                try
                {
                    int occValue;
                    // Try parsing as enum name first, then as integer
                    if (Enum.IsDefined(typeof(TaleWorlds.CampaignSystem.Occupation), occName))
                        occValue = (int)Enum.Parse(typeof(TaleWorlds.CampaignSystem.Occupation), occName);
                    else if (int.TryParse(occName, out int parsed))
                        occValue = parsed;
                    else
                        continue;

                    SetCustomOccupation(heroId, occValue);
                    count++;
                }
                catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: ImportOccupations parse failed: " + ex.ToString()); }
            }
            return count;
        }

        /// <summary>
        /// Returns all custom culture definitions for JSON export.
        /// Key = customCultureId (e.g. "ee_custom_newcs_1234"), Value = "displayName|baseCultureId|basicTroopId|eliteTroopId".
        /// </summary>
        public Dictionary<string, string> GetAllCustomCultureDefsForExport()
        {
            return new Dictionary<string, string>(_customCultureDefs);
        }

        /// <summary>
        /// Imports custom culture definitions from a dictionary.
        /// Key = customCultureId, Value = "displayName|baseCultureId|basicTroopId|eliteTroopId".
        /// Returns the number of definitions imported.
        /// </summary>
        public int ImportCultureDefs(Dictionary<string, string> cultureDefs)
        {
            if (cultureDefs == null) return 0;
            int count = 0;
            foreach (var kvp in cultureDefs)
            {
                if (string.IsNullOrEmpty(kvp.Key) || string.IsNullOrEmpty(kvp.Value))
                    continue;
                // Validate the format: must have at least 4 pipe-separated parts
                var parts = kvp.Value.Split('|');
                if (parts.Length < 4)
                    continue;
                _customCultureDefs[kvp.Key] = kvp.Value;
                count++;
            }
            return count;
        }

        // ── Hero Info Fields (Backstory, Personality, Goals, etc.) ──

        /// <summary>
        /// Gets a structured hero info field value, or null if not set.
        /// </summary>
        /// <param name="fieldKey">One of: backstory, personality, goals, relationships, rumors</param>
        /// <param name="heroId">The hero's StringId</param>
        public string GetHeroInfoField(string fieldKey, string heroId)
        {
            if (_heroInfoFields.TryGetValue(fieldKey + "_" + heroId, out var value))
                return value;
            return null;
        }

        /// <summary>
        /// Sets (or clears) a structured hero info field.
        /// </summary>
        public void SetHeroInfoField(string fieldKey, string heroId, string text)
        {
            string key = fieldKey + "_" + heroId;
            if (string.IsNullOrWhiteSpace(text))
                _heroInfoFields.Remove(key);
            else
                _heroInfoFields[key] = text;
        }

        /// <summary>
        /// Returns true if the hero has at least one non-empty info field.
        /// </summary>
        public bool HasAnyHeroInfoField(string heroId)
        {
            foreach (var fk in InfoFieldKeys)
            {
                if (_heroInfoFields.ContainsKey(fk + "_" + heroId))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the number of non-empty hero info field entries across all heroes.
        /// </summary>
        public int GetHeroInfoFieldCount()
        {
            return _heroInfoFields.Count;
        }

        /// <summary>
        /// Returns all hero info fields for JSON export.
        /// Key = "fieldKey_heroId", Value = text.
        /// </summary>
        public Dictionary<string, string> GetAllHeroInfoFieldsForExport()
        {
            return new Dictionary<string, string>(_heroInfoFields);
        }

        /// <summary>
        /// Imports hero info fields from a dictionary.
        /// Returns the number of entries imported.
        /// </summary>
        public int ImportHeroInfoFields(Dictionary<string, string> fields)
        {
            if (fields == null) return 0;
            int count = 0;
            foreach (var kvp in fields)
            {
                if (string.IsNullOrEmpty(kvp.Key) || string.IsNullOrEmpty(kvp.Value))
                    continue;
                _heroInfoFields[kvp.Key] = kvp.Value;
                count++;
            }
            return count;
        }

        // ── Custom Tags methods ──

        /// <summary>
        /// Gets the comma-separated tags string for an object, or null if none set.
        /// </summary>
        public string GetTags(string objectId)
        {
            if (_customTags.TryGetValue(objectId, out var tags))
                return tags;
            return null;
        }

        /// <summary>
        /// Sets (or clears) the tags for an object.
        /// Tags are stored as a comma-separated string (e.g. "ally, target, recruit").
        /// </summary>
        public void SetTags(string objectId, string tags)
        {
            if (string.IsNullOrWhiteSpace(tags))
                _customTags.Remove(objectId);
            else
                _customTags[objectId] = tags.Trim();
            InvalidateTagCache();
            EditableEncyclopediaAPI.RaiseTagsChanged(objectId, tags);
        }

        /// <summary>
        /// Returns true if the object has any tags assigned.
        /// </summary>
        public bool HasTags(string objectId)
        {
            return _customTags.ContainsKey(objectId) && !string.IsNullOrWhiteSpace(_customTags[objectId]);
        }

        /// <summary>
        /// Returns the number of objects that have tags.
        /// </summary>
        public int GetTagCount()
        {
            return _customTags.Count;
        }

        /// <summary>
        /// Returns the total number of individual journal entries across all entities.
        /// </summary>
        public int GetTotalJournalEntryCount()
        {
            int count = 0;
            foreach (var kvp in _journalEntries)
            {
                if (string.IsNullOrEmpty(kvp.Value)) continue;
                count += kvp.Value.Split('\n').Length;
            }
            return count;
        }

        /// <summary>
        /// Returns the total character count across all journal entries.
        /// </summary>
        public int GetJournalCharacterCount()
        {
            int count = 0;
            foreach (var kvp in _journalEntries)
                if (!string.IsNullOrEmpty(kvp.Value)) count += kvp.Value.Length;
            return count;
        }

        /// <summary>
        /// Returns the total character count across all hero info fields.
        /// </summary>
        public int GetHeroInfoFieldCharacterCount()
        {
            int count = 0;
            foreach (var kvp in _heroInfoFields)
                if (!string.IsNullOrEmpty(kvp.Value)) count += kvp.Value.Length;
            return count;
        }

        /// <summary>
        /// Returns the number of unique heroes that have info fields.
        /// </summary>
        public int GetHeroesWithInfoFieldsCount()
        {
            var heroes = new HashSet<string>();
            foreach (var key in _heroInfoFields.Keys)
            {
                int underscore = key.IndexOf('_');
                if (underscore >= 0) heroes.Add(key.Substring(underscore + 1));
            }
            return heroes.Count;
        }

        /// <summary>
        /// Returns the total number of relation note entries (individual hero→hero notes).
        /// </summary>
        public int GetTotalRelationNoteEntryCount()
        {
            int count = 0;
            foreach (var kvp in _relationNotes)
            {
                if (string.IsNullOrEmpty(kvp.Value)) continue;
                count += kvp.Value.Split('\n').Length;
            }
            return count;
        }

        /// <summary>
        /// Returns all tags for JSON export.
        /// Key = objectId, Value = comma-separated tag string.
        /// </summary>
        public Dictionary<string, string> GetAllTagsForExport()
        {
            return new Dictionary<string, string>(_customTags);
        }

        /// <summary>
        /// Returns all edit timestamps for export.
        /// Key = objectId, Value = formatted in-game date string.
        /// </summary>
        public Dictionary<string, string> GetAllTimestampsForExport()
        {
            return new Dictionary<string, string>(_editTimestamps);
        }

        /// <summary>
        /// Imports edit timestamps from a dictionary.
        /// Only imports timestamps for objects that don't already have one (preserves local timestamps).
        /// Returns the number of entries imported.
        /// </summary>
        public int ImportTimestamps(Dictionary<string, string> timestamps)
        {
            if (timestamps == null || timestamps.Count == 0)
                return 0;

            int count = 0;
            foreach (var kvp in timestamps)
            {
                if (!string.IsNullOrEmpty(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                {
                    if (!_editTimestamps.ContainsKey(kvp.Key))
                    {
                        _editTimestamps[kvp.Key] = kvp.Value;
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// Imports tags from a dictionary.
        /// Returns the number of entries imported.
        /// </summary>
        public int ImportTags(Dictionary<string, string> tags)
        {
            return ImportTags(tags, TagImportMode.Overwrite);
        }

        /// <summary>
        /// Imports tags with a specified merge strategy.
        /// </summary>
        public int ImportTags(Dictionary<string, string> tags, TagImportMode mode)
        {
            if (tags == null) return 0;
            int count = 0;
            foreach (var kvp in tags)
            {
                if (string.IsNullOrEmpty(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                    continue;

                switch (mode)
                {
                    case TagImportMode.Skip:
                        if (!_customTags.ContainsKey(kvp.Key))
                        {
                            _customTags[kvp.Key] = kvp.Value;
                            count++;
                        }
                        break;

                    case TagImportMode.Merge:
                        if (_customTags.TryGetValue(kvp.Key, out var existing))
                        {
                            var merged = MergeTagStrings(existing, kvp.Value);
                            if (merged != existing)
                            {
                                _customTags[kvp.Key] = merged;
                                count++;
                            }
                        }
                        else
                        {
                            _customTags[kvp.Key] = kvp.Value;
                            count++;
                        }
                        break;

                    default: // Overwrite
                        _customTags[kvp.Key] = kvp.Value;
                        count++;
                        break;
                }
            }
            InvalidateTagCache();
            return count;
        }

        /// <summary>
        /// Merges two comma-separated tag strings, deduplicating case-insensitively.
        /// </summary>
        private static string MergeTagStrings(string a, string b)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var s in new[] { a, b })
            {
                if (string.IsNullOrEmpty(s)) continue;
                foreach (var part in s.Split(','))
                {
                    string tag = part.Trim();
                    if (!string.IsNullOrEmpty(tag) && seen.Add(tag))
                        result.Add(tag);
                }
            }
            return result.Count > 0 ? string.Join(", ", result) : null;
        }

        // ── Tag Querying ──

        /// <summary>
        /// Returns all object IDs that have the specified tag (case-insensitive).
        /// </summary>
        public List<string> GetObjectsWithTag(string tag)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(tag)) return result;
            string tagLower = tag.Trim().ToLowerInvariant();
            foreach (var kvp in _customTags)
            {
                if (TagStringContains(kvp.Value, tagLower))
                    result.Add(kvp.Key);
            }
            return result;
        }

        /// <summary>
        /// Returns all object IDs that have ANY of the specified tags.
        /// </summary>
        public List<string> GetObjectsWithAnyTag(IEnumerable<string> tags)
        {
            var tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tags)
            {
                string trimmed = t?.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    tagSet.Add(trimmed);
            }
            if (tagSet.Count == 0) return new List<string>();

            var result = new List<string>();
            foreach (var kvp in _customTags)
            {
                foreach (var part in kvp.Value.Split(','))
                {
                    if (tagSet.Contains(part.Trim()))
                    {
                        result.Add(kvp.Key);
                        break;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Returns all object IDs that have ALL of the specified tags.
        /// </summary>
        public List<string> GetObjectsWithAllTags(IEnumerable<string> tags)
        {
            var tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tags)
            {
                string trimmed = t?.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    tagSet.Add(trimmed);
            }
            if (tagSet.Count == 0) return new List<string>();

            var result = new List<string>();
            foreach (var kvp in _customTags)
            {
                var entryTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in kvp.Value.Split(','))
                {
                    string trimmed = part.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        entryTags.Add(trimmed);
                }
                if (entryTags.IsSupersetOf(tagSet))
                    result.Add(kvp.Key);
            }
            return result;
        }

        /// <summary>
        /// Returns a sorted list of all unique tags with their usage counts.
        /// </summary>
        public List<TagUsageInfo> GetTagUsageCounts()
        {
            EnsureTagCacheValid();
            var result = new List<TagUsageInfo>();
            foreach (var kvp in _tagUsageCache)
                result.Add(new TagUsageInfo { Tag = kvp.Key, Count = kvp.Value });
            result.Sort((a, b) => string.Compare(a.Tag, b.Tag, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private static bool TagStringContains(string tagString, string tagLower)
        {
            if (string.IsNullOrEmpty(tagString)) return false;
            foreach (var part in tagString.Split(','))
            {
                if (part.Trim().ToLowerInvariant() == tagLower)
                    return true;
            }
            return false;
        }

        // ── Tag Cache ──

        private Dictionary<string, int> _tagUsageCache;
        private bool _tagCacheValid;

        private void InvalidateTagCache()
        {
            _tagCacheValid = false;
            _tagUsageCache = null;
        }

        private void EnsureTagCacheValid()
        {
            if (_tagCacheValid && _tagUsageCache != null) return;
            _tagUsageCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _customTags)
            {
                if (string.IsNullOrEmpty(kvp.Value)) continue;
                foreach (var part in kvp.Value.Split(','))
                {
                    string tag = part.Trim();
                    if (string.IsNullOrEmpty(tag)) continue;
                    if (_tagUsageCache.ContainsKey(tag))
                        _tagUsageCache[tag]++;
                    else
                        _tagUsageCache[tag] = 1;
                }
            }
            _tagCacheValid = true;
        }

        /// <summary>
        /// Returns the cached list of all unique tags (sorted).
        /// More efficient than CollectAllUniqueTags() for repeated calls.
        /// </summary>
        public List<string> GetAllUniqueTags()
        {
            EnsureTagCacheValid();
            var sorted = new List<string>(_tagUsageCache.Keys);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            return sorted;
        }

        /// <summary>
        /// Returns the usage count for a specific tag.
        /// </summary>
        public int GetTagUsageCount(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return 0;
            EnsureTagCacheValid();
            return _tagUsageCache.TryGetValue(tag.Trim(), out int count) ? count : 0;
        }

        // ── Tag Bulk Operations ──

        /// <summary>
        /// Renames a tag globally across all entries (case-insensitive match).
        /// Returns the number of entries updated.
        /// </summary>
        public int RenameTagGlobal(string oldTag, string newTag)
        {
            if (string.IsNullOrWhiteSpace(oldTag) || string.IsNullOrWhiteSpace(newTag))
                return 0;

            oldTag = oldTag.Trim();
            newTag = newTag.Trim();
            int updated = 0;

            var keys = new List<string>(_customTags.Keys);
            foreach (var key in keys)
            {
                string value = _customTags[key];
                string replaced = ReplaceTagInString(value, oldTag, newTag);
                if (replaced != value)
                {
                    _customTags[key] = replaced;
                    updated++;
                }
            }
            if (updated > 0) InvalidateTagCache();
            return updated;
        }

        /// <summary>
        /// Removes a tag globally from all entries.
        /// Returns the number of entries updated.
        /// </summary>
        public int RemoveTagGlobal(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return 0;
            tag = tag.Trim();
            int updated = 0;

            var keys = new List<string>(_customTags.Keys);
            foreach (var key in keys)
            {
                string value = _customTags[key];
                string replaced = RemoveTagFromString(value, tag);
                if (replaced != value)
                {
                    if (string.IsNullOrEmpty(replaced))
                        _customTags.Remove(key);
                    else
                        _customTags[key] = replaced;
                    updated++;
                }
            }
            if (updated > 0) InvalidateTagCache();
            return updated;
        }

        /// <summary>
        /// Adds a tag to multiple entries at once.
        /// Returns the number of entries updated.
        /// </summary>
        public int AddTagToMultiple(IEnumerable<string> objectIds, string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return 0;
            tag = tag.Trim();
            int updated = 0;

            foreach (var objectId in objectIds)
            {
                if (string.IsNullOrEmpty(objectId)) continue;
                string current = GetTags(objectId) ?? "";
                if (!TagStringContains(current, tag.ToLowerInvariant()))
                {
                    string newTags = string.IsNullOrEmpty(current) ? tag : current + ", " + tag;
                    _customTags[objectId] = newTags;
                    updated++;
                }
            }
            if (updated > 0) InvalidateTagCache();
            return updated;
        }

        /// <summary>
        /// Removes a tag from multiple entries at once.
        /// Returns the number of entries updated.
        /// </summary>
        public int RemoveTagFromMultiple(IEnumerable<string> objectIds, string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return 0;
            tag = tag.Trim();
            int updated = 0;

            foreach (var objectId in objectIds)
            {
                if (string.IsNullOrEmpty(objectId)) continue;
                if (!_customTags.TryGetValue(objectId, out string current)) continue;
                string replaced = RemoveTagFromString(current, tag);
                if (replaced != current)
                {
                    if (string.IsNullOrEmpty(replaced))
                        _customTags.Remove(objectId);
                    else
                        _customTags[objectId] = replaced;
                    updated++;
                }
            }
            if (updated > 0) InvalidateTagCache();
            return updated;
        }

        /// <summary>
        /// Merges two tags: replaces all occurrences of sourceTag with targetTag.
        /// Returns the number of entries updated.
        /// </summary>
        public int MergeTags(string sourceTag, string targetTag)
        {
            return RenameTagGlobal(sourceTag, targetTag);
        }

        /// <summary>
        /// Clears all tags from all entries.
        /// Returns the number of entries that were cleared.
        /// </summary>
        public int ClearAllTags()
        {
            int count = _customTags.Count;
            _customTags.Clear();
            InvalidateTagCache();
            return count;
        }

        // ── Tag Notes ──

        /// <summary>
        /// Returns the note attached to a specific tag on a specific object.
        /// </summary>
        public string GetTagNote(string objectId, string tag)
        {
            if (string.IsNullOrEmpty(objectId) || string.IsNullOrEmpty(tag)) return null;
            string key = objectId + "|" + tag.Trim().ToLowerInvariant();
            return _tagNotes.TryGetValue(key, out var note) ? note : null;
        }

        /// <summary>
        /// Sets (or clears) a note on a specific tag for a specific object.
        /// </summary>
        public void SetTagNote(string objectId, string tag, string note)
        {
            if (string.IsNullOrEmpty(objectId) || string.IsNullOrEmpty(tag)) return;
            string key = objectId + "|" + tag.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(note))
                _tagNotes.Remove(key);
            else
                _tagNotes[key] = note.Trim();
        }

        /// <summary>
        /// Returns all tag notes for a given object as a dictionary of tag → note.
        /// </summary>
        public Dictionary<string, string> GetAllTagNotes(string objectId)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(objectId)) return result;
            string prefix = objectId + "|";
            foreach (var kvp in _tagNotes)
            {
                if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    string tag = kvp.Key.Substring(prefix.Length);
                    result[tag] = kvp.Value;
                }
            }
            return result;
        }

        /// <summary>
        /// Returns the total number of tag notes in the save.
        /// </summary>
        public int GetTagNoteCount()
        {
            return _tagNotes.Count;
        }

        /// <summary>
        /// Returns a copy of all tag notes for JSON export.
        /// </summary>
        public Dictionary<string, string> GetAllTagNotesForExport()
        {
            return new Dictionary<string, string>(_tagNotes);
        }

        /// <summary>
        /// Merges imported tag notes into the current save data.
        /// Returns the number of entries imported.
        /// </summary>
        public int ImportTagNotes(Dictionary<string, string> notes)
        {
            if (notes == null) return 0;
            int count = 0;
            foreach (var kvp in notes)
            {
                _tagNotes[kvp.Key] = kvp.Value;
                count++;
            }
            return count;
        }

        // ── Auto-Generated Tags ──
        // These are computed from game state (relation, party size, clan wealth)
        // and stored in memory only (not saved). They are merged with manual tags for display.
        private Dictionary<string, string> _autoTags = new Dictionary<string, string>();
        private bool _autoTagsEvaluated;
        internal bool _showIntroOnNextTick;

        /// <summary>
        /// Returns the auto-generated tags for an object (runtime-only, not saved).
        /// </summary>
        public string GetAutoTags(string objectId)
        {
            if (_autoTags.TryGetValue(objectId, out var tags))
                return tags;
            return null;
        }

        /// <summary>
        /// Returns manual tags merged with auto-generated tags for display.
        /// Auto-tags are prefixed with "AUTO:" to distinguish them.
        /// </summary>
        public string GetTagsWithAuto(string objectId)
        {
            // Lazy-evaluate auto-tags on first access so they appear immediately
            // without waiting for the first DailyTick
            if (!_autoTagsEvaluated)
            {
                try { EvaluateAutoTags(); }
                catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: EvaluateAutoTags failed: " + ex.ToString()); }
            }
            string manual = GetTags(objectId);
            string auto = GetAutoTags(objectId);
            if (string.IsNullOrEmpty(auto)) return manual;
            if (string.IsNullOrEmpty(manual)) return auto;
            return manual + ", " + auto;
        }

        /// <summary>
        /// Evaluates and updates auto-generated tags for all heroes based on game state.
        /// Called from DailyTick when auto-tags are enabled in MCM.
        /// </summary>
        internal void EvaluateAutoTags()
        {
            try
            {
                var settings = MCMSettings.Instance;
                if (settings != null && !settings.EnableAutoTags) return;

                var mainHero = Hero.MainHero;
                if (mainHero == null) return;

                var newAutoTags = new Dictionary<string, string>();
                int relationThresholdEnemy = DefaultAutoTagEnemyRelationThreshold;
                int relationThresholdFriend = DefaultAutoTagFriendRelationThreshold;
                int partySizeDangerous = DefaultAutoTagDangerousPartySize;

                try
                {
                    if (settings != null)
                    {
                        relationThresholdEnemy = settings.AutoTagEnemyRelationThreshold;
                        relationThresholdFriend = settings.AutoTagFriendRelationThreshold;
                        partySizeDangerous = settings.AutoTagDangerousPartySize;
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: EvaluateAutoTags settings read failed: " + ex.ToString()); }

                // Determine player's kingdom for "At War" and "Ally Clan" checks
                var playerKingdom = mainHero.Clan?.Kingdom;

                foreach (var hero in Hero.AllAliveHeroes)
                {
                    if (hero == null || hero == mainHero) continue;
                    try
                    {
                        var autoTagList = new List<string>();

                        // Per-hero thresholds override global defaults
                        int heroEnemyThreshold = relationThresholdEnemy;
                        int heroFriendThreshold = relationThresholdFriend;
                        var perHero = GetPerHeroAutoTagThresholds(hero.StringId);
                        if (perHero != null)
                        {
                            heroEnemyThreshold = perHero.Item1;
                            heroFriendThreshold = perHero.Item2;
                        }

                        // Relation-based auto tags
                        int relation = hero.GetRelation(mainHero);
                        if (relation <= heroEnemyThreshold)
                            autoTagList.Add("Auto: Enemy");
                        else if (relation >= heroFriendThreshold)
                            autoTagList.Add("Auto: Friend");

                        // Party size: dangerous
                        if (hero.PartyBelongedTo != null && hero.PartyBelongedTo.MemberRoster != null)
                        {
                            int partySize = hero.PartyBelongedTo.MemberRoster.TotalManCount;
                            if (partySize >= partySizeDangerous)
                                autoTagList.Add("Auto: Dangerous");
                        }

                        // Clan wealth: rich
                        if (hero.Clan != null && hero.Clan.Leader == hero)
                        {
                            int clanGold = hero.Clan.Gold;
                            if (clanGold >= AutoTagRichClanGoldThreshold)
                                autoTagList.Add("Auto: Rich");
                        }

                        // Prisoner status
                        if (hero.IsPrisoner)
                            autoTagList.Add("Auto: Prisoner");

                        // NEW: At War — hero's faction is at war with player's faction
                        if (playerKingdom != null && hero.Clan?.Kingdom != null
                            && hero.Clan.Kingdom != playerKingdom
                            && playerKingdom.IsAtWarWith(hero.Clan.Kingdom))
                            autoTagList.Add("Auto: At War");

                        // NEW: Ally Clan — hero's clan is in the same kingdom as player
                        if (playerKingdom != null && hero.Clan?.Kingdom != null
                            && hero.Clan.Kingdom == playerKingdom
                            && hero.Clan != mainHero.Clan)
                            autoTagList.Add("Auto: Ally Clan");

                        // NEW: Mercenary — hero's clan is a mercenary clan
                        if (hero.Clan != null && hero.Clan.IsMinorFaction
                            && hero.Clan.Leader == hero && !hero.Clan.IsRebelClan)
                            autoTagList.Add("Auto: Mercenary");

                        // NEW: Wounded — hero has significant health loss
                        if (hero.IsWounded)
                            autoTagList.Add("Auto: Wounded");

                        // NEW: Ruler — hero is the ruler of their kingdom
                        if (hero.Clan?.Kingdom != null && hero.Clan.Kingdom.Leader == hero)
                            autoTagList.Add("Auto: Ruler");

                        if (autoTagList.Count > 0)
                            newAutoTags[hero.StringId] = string.Join(", ", autoTagList);
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: EvaluateAutoTags hero processing failed: " + ex.ToString()); }
                }

                // Auto-tags for settlements
                foreach (var settlement in Settlement.All)
                {
                    if (settlement == null) continue;
                    try
                    {
                        var autoTagList = new List<string>();

                        // Nearby — settlement is within close range of player party
                        if (mainHero.PartyBelongedTo != null)
                        {
                            Vec2? sPos = TryGetPosition2D(settlement);
                            Vec2? pPos = TryGetPosition2D(mainHero.PartyBelongedTo);
                            if (sPos.HasValue && pPos.HasValue)
                            {
                                float distance = sPos.Value.Distance(pPos.Value);
                                if (distance < AutoTagNearbyDistanceThreshold)
                                    autoTagList.Add("Auto: Nearby");
                            }
                        }

                        // Under Siege
                        if (settlement.IsUnderSiege)
                            autoTagList.Add("Auto: Under Siege");

                        if (autoTagList.Count > 0)
                            newAutoTags[settlement.StringId] = string.Join(", ", autoTagList);
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: EvaluateAutoTags settlement processing failed: " + ex.ToString()); }
                }

                _autoTags = newAutoTags;
                _autoTagsEvaluated = true;
                MCMSettings.DebugLog("AutoTags: evaluated " + newAutoTags.Count + " entities with auto-tags");
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("AutoTags evaluation error: " + ex.ToString());
            }
        }

        private static string ReplaceTagInString(string tagString, string oldTag, string newTag)
        {
            if (string.IsNullOrEmpty(tagString)) return tagString;
            var parts = tagString.Split(',');
            bool changed = false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            foreach (var part in parts)
            {
                string tag = part.Trim();
                if (string.IsNullOrEmpty(tag)) continue;
                if (string.Equals(tag, oldTag, StringComparison.OrdinalIgnoreCase))
                {
                    tag = newTag;
                    changed = true;
                }
                if (seen.Add(tag.ToLowerInvariant()))
                    result.Add(tag);
            }
            if (!changed) return tagString;
            return result.Count > 0 ? string.Join(", ", result) : null;
        }

        internal static string RemoveTagFromString(string tagString, string tagToRemove)
        {
            if (string.IsNullOrEmpty(tagString)) return tagString;
            var parts = tagString.Split(',');
            bool changed = false;
            var result = new List<string>();

            foreach (var part in parts)
            {
                string tag = part.Trim();
                if (string.IsNullOrEmpty(tag)) continue;
                if (string.Equals(tag, tagToRemove, StringComparison.OrdinalIgnoreCase))
                {
                    changed = true;
                    continue;
                }
                result.Add(tag);
            }
            if (!changed) return tagString;
            return result.Count > 0 ? string.Join(", ", result) : null;
        }

        // ── Tag Categories (User-Defined) ──
        // Key = category name (e.g. "Diplomacy", "My Custom Group")
        // Value = comma-separated tag names belonging to this category
        [SaveableField(14)]
        private Dictionary<string, string> _tagCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets all tag categories as a dictionary of category name → comma-separated tags.
        /// </summary>
        public Dictionary<string, string> GetAllTagCategories()
        {
            if (_tagCategories == null)
                _tagCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return new Dictionary<string, string>(_tagCategories, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Sets (or clears) a tag category.
        /// </summary>
        public void SetTagCategory(string categoryName, string commaSeparatedTags)
        {
            if (_tagCategories == null)
                _tagCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(categoryName)) return;
            if (string.IsNullOrWhiteSpace(commaSeparatedTags))
                _tagCategories.Remove(categoryName);
            else
                _tagCategories[categoryName] = commaSeparatedTags.Trim();
        }

        /// <summary>
        /// Removes a tag category.
        /// </summary>
        public void RemoveTagCategory(string categoryName)
        {
            if (_tagCategories == null) return;
            if (!string.IsNullOrEmpty(categoryName))
                _tagCategories.Remove(categoryName);
        }

        /// <summary>
        /// Returns the category name a tag belongs to, or null if uncategorized.
        /// Checks user-defined categories first, then built-in defaults.
        /// </summary>
        public string GetTagCategory(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string tagLower = tag.Trim().ToLowerInvariant();

            // Check user-defined categories first
            if (_tagCategories != null)
            {
                foreach (var kvp in _tagCategories)
                {
                    foreach (var part in kvp.Value.Split(','))
                    {
                        if (part.Trim().ToLowerInvariant() == tagLower)
                            return kvp.Key;
                    }
                }
            }

            // Fall back to built-in categories
            return GetBuiltInTagCategory(tagLower);
        }

        /// <summary>
        /// Returns the built-in category for a known tag.
        /// </summary>
        private static string GetBuiltInTagCategory(string tagLower)
        {
            switch (tagLower)
            {
                case "ally": case "enemy": case "rival": case "friend":
                case "trusted": case "hostile": case "traitor":
                    return "Diplomacy";
                case "target": case "spy": case "warlord": case "raid leader":
                case "good commander": case "dangerous": case "assassination target":
                    return "Military";
                case "trade hub": case "rich": case "ransom target":
                    return "Economy";
                case "future vassal": case "recruit": case "claimant":
                case "governor": case "future governor":
                    return "Vassalage";
                case "marriage candidate": case "family":
                    return "Family";
                case "prisoner": case "dead": case "wanted":
                case "honorable": case "dishonorable":
                    return "Status";
                default:
                    return null;
            }
        }

        // ── Tag Presets/Templates ──
        // Key = preset name (e.g. "War Planning", "Trade Route")
        // Value = comma-separated tag names in this preset
        [SaveableField(15)]
        private Dictionary<string, string> _tagPresets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets all tag presets as a dictionary of preset name → comma-separated tags.
        /// </summary>
        public Dictionary<string, string> GetAllTagPresets()
        {
            if (_tagPresets == null)
                _tagPresets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return new Dictionary<string, string>(_tagPresets, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets a specific tag preset by name.
        /// </summary>
        public string GetTagPreset(string presetName)
        {
            if (_tagPresets == null || string.IsNullOrWhiteSpace(presetName)) return null;
            return _tagPresets.TryGetValue(presetName.Trim(), out var tags) ? tags : null;
        }

        /// <summary>
        /// Saves a tag preset. Pass null/empty tags to delete.
        /// </summary>
        public void SetTagPreset(string presetName, string commaSeparatedTags)
        {
            if (_tagPresets == null)
                _tagPresets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(presetName)) return;
            if (string.IsNullOrWhiteSpace(commaSeparatedTags))
                _tagPresets.Remove(presetName.Trim());
            else
                _tagPresets[presetName.Trim()] = commaSeparatedTags.Trim();
        }

        /// <summary>
        /// Deletes a tag preset by name.
        /// </summary>
        public void RemoveTagPreset(string presetName)
        {
            if (_tagPresets == null) return;
            if (!string.IsNullOrEmpty(presetName))
                _tagPresets.Remove(presetName.Trim());
        }

        /// <summary>
        /// Applies a preset to an object — merges preset tags with existing tags.
        /// Returns the updated tag string.
        /// </summary>
        public string ApplyTagPreset(string objectId, string presetName)
        {
            if (string.IsNullOrEmpty(objectId) || string.IsNullOrEmpty(presetName)) return null;
            string presetTags = GetTagPreset(presetName);
            if (string.IsNullOrEmpty(presetTags)) return GetTags(objectId);
            string current = GetTags(objectId) ?? "";
            string merged = MergeTagStrings(current, presetTags);
            SetTags(objectId, merged);
            return merged;
        }

        /// <summary>
        /// Returns the count of saved tag presets.
        /// </summary>
        public int GetTagPresetCount()
        {
            return _tagPresets?.Count ?? 0;
        }

        // ── Per-Hero Auto-Tag Thresholds ──
        // Key = heroId
        // Value = "enemyThreshold|friendThreshold" (e.g. "-50|50")
        [SaveableField(16)]
        private Dictionary<string, string> _perHeroAutoTagThresholds = new Dictionary<string, string>();

        /// <summary>
        /// Gets the per-hero auto-tag thresholds, or null if using global defaults.
        /// Returns (enemyThreshold, friendThreshold) or null.
        /// </summary>
        public Tuple<int, int> GetPerHeroAutoTagThresholds(string heroId)
        {
            if (_perHeroAutoTagThresholds == null || string.IsNullOrEmpty(heroId)) return null;
            if (!_perHeroAutoTagThresholds.TryGetValue(heroId, out var value)) return null;
            var parts = value.Split('|');
            if (parts.Length < 2) return null;
            if (int.TryParse(parts[0], out int enemy) && int.TryParse(parts[1], out int friend))
                return Tuple.Create(enemy, friend);
            return null;
        }

        /// <summary>
        /// Sets per-hero auto-tag thresholds. Pass null to clear (use global defaults).
        /// </summary>
        public void SetPerHeroAutoTagThresholds(string heroId, int? enemyThreshold, int? friendThreshold)
        {
            if (_perHeroAutoTagThresholds == null)
                _perHeroAutoTagThresholds = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(heroId)) return;
            if (enemyThreshold == null || friendThreshold == null)
                _perHeroAutoTagThresholds.Remove(heroId);
            else
                _perHeroAutoTagThresholds[heroId] = enemyThreshold.Value + "|" + friendThreshold.Value;
        }

        /// <summary>
        /// Returns the count of heroes with custom auto-tag thresholds.
        /// </summary>
        public int GetPerHeroAutoTagThresholdCount()
        {
            return _perHeroAutoTagThresholds?.Count ?? 0;
        }

        /// <summary>
        /// Returns all per-hero auto-tag thresholds for export.
        /// </summary>
        public Dictionary<string, string> GetAllPerHeroAutoTagThresholds()
        {
            if (_perHeroAutoTagThresholds == null) return new Dictionary<string, string>();
            return new Dictionary<string, string>(_perHeroAutoTagThresholds);
        }

        public Dictionary<string, string> GetAllRelationNoteTagsForExport()
        {
            if (_relationNoteTags == null) return new Dictionary<string, string>();
            return new Dictionary<string, string>(_relationNoteTags);
        }

        public Dictionary<string, string> GetAllRelationNoteTagLocksForExport()
        {
            if (_relationNoteTagLocks == null) return new Dictionary<string, string>();
            return new Dictionary<string, string>(_relationNoteTagLocks);
        }

        public int ImportRelationNoteTags(Dictionary<string, string> tags)
        {
            if (tags == null) return 0;
            if (_relationNoteTags == null) _relationNoteTags = new Dictionary<string, string>();
            int count = 0;
            foreach (var kvp in tags)
            {
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                _relationNoteTags[kvp.Key] = kvp.Value ?? "";
                count++;
            }
            return count;
        }

        public int ImportRelationNoteTagLocks(Dictionary<string, string> locks)
        {
            if (locks == null) return 0;
            if (_relationNoteTagLocks == null) _relationNoteTagLocks = new Dictionary<string, string>();
            int count = 0;
            foreach (var kvp in locks)
            {
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                _relationNoteTagLocks[kvp.Key] = kvp.Value ?? "";
                count++;
            }
            return count;
        }

        /// <summary>
        /// Imports tag categories from a dictionary. Overwrites existing categories.
        /// </summary>
        public int ImportTagCategories(Dictionary<string, string> categories)
        {
            if (categories == null) return 0;
            if (_tagCategories == null)
                _tagCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            foreach (var kvp in categories)
            {
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                _tagCategories[kvp.Key] = kvp.Value ?? "";
                count++;
            }
            return count;
        }

        /// <summary>
        /// Imports tag presets from a dictionary. Overwrites existing presets.
        /// </summary>
        public int ImportTagPresets(Dictionary<string, string> presets)
        {
            if (presets == null) return 0;
            if (_tagPresets == null)
                _tagPresets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            foreach (var kvp in presets)
            {
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                _tagPresets[kvp.Key] = kvp.Value ?? "";
                count++;
            }
            return count;
        }

        /// <summary>
        /// Imports per-hero auto-tag thresholds from a dictionary. Overwrites existing thresholds.
        /// </summary>
        public int ImportPerHeroAutoTagThresholds(Dictionary<string, string> thresholds)
        {
            if (thresholds == null) return 0;
            if (_perHeroAutoTagThresholds == null)
                _perHeroAutoTagThresholds = new Dictionary<string, string>();
            int count = 0;
            foreach (var kvp in thresholds)
            {
                if (string.IsNullOrEmpty(kvp.Key) || string.IsNullOrEmpty(kvp.Value))
                    continue;
                _perHeroAutoTagThresholds[kvp.Key] = kvp.Value;
                count++;
            }
            return count;
        }

        // ── Tag-Based Filtering ──

        /// <summary>
        /// Returns all object IDs that have the specified tag, resolved to game objects with display names.
        /// Returns a list of (objectId, displayName, objectType) tuples.
        /// </summary>
        public List<Tuple<string, string, string>> GetObjectsWithTagDetailed(string tag)
        {
            var result = new List<Tuple<string, string, string>>();
            if (string.IsNullOrWhiteSpace(tag)) return result;

            var objectIds = GetObjectsWithTag(tag);
            foreach (var objectId in objectIds)
            {
                string displayName = objectId;
                string objectType = "Unknown";

                try
                {
                    // Try Hero
                    var hero = Hero.FindFirst(h => h != null && h.StringId == objectId);
                    if (hero != null) { displayName = hero.Name?.ToString() ?? objectId; objectType = "Hero"; }

                    // Try Clan
                    if (objectType == "Unknown")
                    {
                        foreach (var clan in Clan.All)
                        {
                            if (clan != null && clan.StringId == objectId)
                            { displayName = clan.Name?.ToString() ?? objectId; objectType = "Clan"; break; }
                        }
                    }

                    // Try Kingdom
                    if (objectType == "Unknown")
                    {
                        foreach (var kingdom in Kingdom.All)
                        {
                            if (kingdom != null && kingdom.StringId == objectId)
                            { displayName = kingdom.Name?.ToString() ?? objectId; objectType = "Kingdom"; break; }
                        }
                    }

                    // Try Settlement
                    if (objectType == "Unknown")
                    {
                        foreach (var settlement in Settlement.All)
                        {
                            if (settlement != null && settlement.StringId == objectId)
                            { displayName = settlement.Name?.ToString() ?? objectId; objectType = "Settlement"; break; }
                        }
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: GetObjectsWithTagDetailed resolve failed: " + ex.ToString()); }

                result.Add(Tuple.Create(objectId, displayName, objectType));
            }

            return result;
        }

        // ── Statistics helpers ──

        public int GetCustomNameCount()
        {
            int count = 0;
            foreach (var key in _customNames.Keys)
                if (key.StartsWith("name_")) count++;
            return count;
        }

        public int GetCustomTitleCount()
        {
            int count = 0;
            foreach (var key in _customNames.Keys)
                if (key.StartsWith("title_")) count++;
            return count;
        }

        public int GetCustomBannerCount()
        {
            int count = 0;
            foreach (var key in _customBannerCodes.Keys)
                if (!key.StartsWith("origbanner_")) count++;
            return count;
        }

        // ── Public API methods (used by EditableEncyclopediaAPI) ──

        /// <summary>
        /// Returns a read-only copy of all custom descriptions.
        /// Key = StringId of the game object, Value = custom description text.
        /// </summary>
        public Dictionary<string, string> GetAllDescriptions()
        {
            return new Dictionary<string, string>(_customDescriptions);
        }

        /// <summary>
        /// Returns the total number of custom descriptions stored.
        /// </summary>
        public int GetDescriptionCount()
        {
            return _customDescriptions.Count;
        }

        /// <summary>
        /// Merges imported descriptions into the current set.
        /// Existing descriptions for the same object ID are overwritten.
        /// Returns the number of descriptions that were imported.
        /// </summary>
        public int ImportDescriptions(Dictionary<string, string> descriptions)
        {
            if (descriptions == null || descriptions.Count == 0)
                return 0;

            int count = 0;
            foreach (var kvp in descriptions)
            {
                if (!string.IsNullOrEmpty(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                {
                    _customDescriptions[kvp.Key] = kvp.Value;
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Returns all descriptions whose StringId starts with the given prefix.
        /// Useful for filtering by object type (e.g., "lord_" for heroes).
        /// </summary>
        internal Dictionary<string, string> GetDescriptionsByPrefix(string prefix)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(prefix))
                return result;

            foreach (var kvp in _customDescriptions)
            {
                if (kvp.Key.StartsWith(prefix))
                {
                    result[kvp.Key] = kvp.Value;
                }
            }
            return result;
        }

        /// <summary>
        /// Removes ALL custom descriptions. Returns the number of descriptions that were removed.
        /// </summary>
        public int ClearAllDescriptions()
        {
            int count = _customDescriptions.Count;
            var keys = new List<string>(_customDescriptions.Keys);
            _customDescriptions.Clear();

            // Fire change events for each removed description
            foreach (var key in keys)
            {
                EditableEncyclopediaAPI.RaiseDescriptionChanged(key, null);
            }

            return count;
        }

        /// <summary>
        /// Returns descriptions filtered by a set of valid object IDs.
        /// </summary>
        public Dictionary<string, string> GetDescriptionsForIds(HashSet<string> validIds)
        {
            var result = new Dictionary<string, string>();
            foreach (var kvp in _customDescriptions)
            {
                if (validIds.Contains(kvp.Key))
                {
                    result[kvp.Key] = kvp.Value;
                }
            }
            return result;
        }

        // ── Export/Import helpers for names, titles, banners ──

        /// <summary>
        /// Returns all custom names (key = objectId, value = custom name).
        /// Strips the "name_" prefix from the internal keys.
        /// </summary>
        public Dictionary<string, string> GetAllCustomNames()
        {
            var result = new Dictionary<string, string>();
            foreach (var kvp in _customNames)
            {
                if (kvp.Key.StartsWith("name_"))
                    result[kvp.Key.Substring(5)] = kvp.Value;
            }
            return result;
        }

        /// <summary>
        /// Returns all custom titles (key = objectId, value = custom title).
        /// Strips the "title_" prefix from the internal keys.
        /// </summary>
        public Dictionary<string, string> GetAllCustomTitles()
        {
            var result = new Dictionary<string, string>();
            foreach (var kvp in _customNames)
            {
                if (kvp.Key.StartsWith("title_"))
                    result[kvp.Key.Substring(6)] = kvp.Value;
            }
            return result;
        }

        /// <summary>
        /// Returns all custom banner codes (key = objectId, value = banner code).
        /// Excludes "origbanner_" backup entries.
        /// </summary>
        public Dictionary<string, string> GetAllCustomBanners()
        {
            var result = new Dictionary<string, string>();
            foreach (var kvp in _customBannerCodes)
            {
                if (!kvp.Key.StartsWith("origbanner_"))
                    result[kvp.Key] = kvp.Value;
            }
            return result;
        }

        /// <summary>
        /// Merges imported custom names. Adds "name_" prefix to keys.
        /// Returns the number imported.
        /// </summary>
        public int ImportNames(Dictionary<string, string> names)
        {
            if (names == null || names.Count == 0) return 0;
            int count = 0;
            foreach (var kvp in names)
            {
                if (!string.IsNullOrEmpty(kvp.Key) && !string.IsNullOrEmpty(kvp.Value))
                {
                    _customNames["name_" + kvp.Key] = kvp.Value;
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Merges imported custom titles. Adds "title_" prefix to keys.
        /// Returns the number imported.
        /// </summary>
        public int ImportTitles(Dictionary<string, string> titles)
        {
            if (titles == null || titles.Count == 0) return 0;
            int count = 0;
            foreach (var kvp in titles)
            {
                if (!string.IsNullOrEmpty(kvp.Key) && !string.IsNullOrEmpty(kvp.Value))
                {
                    _customNames["title_" + kvp.Key] = kvp.Value;
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Merges imported custom banner codes.
        /// Returns the number imported.
        /// </summary>
        public int ImportBanners(Dictionary<string, string> banners)
        {
            if (banners == null || banners.Count == 0) return 0;
            int count = 0;
            foreach (var kvp in banners)
            {
                if (!string.IsNullOrEmpty(kvp.Key) && !string.IsNullOrEmpty(kvp.Value))
                {
                    _customBannerCodes[kvp.Key] = kvp.Value;
                    count++;
                }
            }
            return count;
        }

        // ── Journal Entry methods ──

        /// <summary>
        /// Gets all journal entries for an object as a raw newline-separated string.
        /// Each line is formatted as "date|text". Returns null if none exist.
        /// </summary>
        public string GetJournalRaw(string objectId)
        {
            if (_journalEntries.TryGetValue(objectId, out var raw))
                return raw;
            return null;
        }

        /// <summary>
        /// Gets parsed journal entries for an object as a list of (date, text) tuples.
        /// Returns an empty list if no entries exist.
        /// </summary>
        public List<JournalEntry> GetJournalEntries(string objectId)
        {
            var result = new List<JournalEntry>();
            if (!_journalEntries.TryGetValue(objectId, out var raw) || string.IsNullOrEmpty(raw))
                return result;

            foreach (var line in raw.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int sep = line.IndexOf('|');
                if (sep > 0)
                    result.Add(new JournalEntry { Date = line.Substring(0, sep), Text = line.Substring(sep + 1) });
                else
                    result.Add(new JournalEntry { Date = "", Text = line });
            }
            return result;
        }

        /// <summary>
        /// Adds a new journal entry with the current campaign date.
        /// </summary>
        public void AddJournalEntry(string objectId, string text)
        {
            AddJournalEntryInternal(objectId, text);
        }

        /// <summary>
        /// Same as <see cref="AddJournalEntry"/>, but returns the line that was actually stored
        /// ("date|text") — or null when nothing was written (blank text, or a dedup hit).
        ///
        /// AutoLog needs the stored date and the stored *sanitised* text to build a chronicle key
        /// that cannot drift from the one the renderer computes. The public void overload above
        /// keeps its exact signature because BandIt Plus resolves
        /// EditableEncyclopediaAPI.AddJournalEntry(string, string) by reflection.
        /// </summary>
        private string AddJournalEntryInternal(string objectId, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            string date;
            try { date = GetCurrentGameDateString(); }
            catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: AddJournalEntry date lookup failed: " + ex.ToString()); date = "Unknown Date"; }

            // Sanitize: remove newlines and pipes from user text
            text = text.Replace("\n", " ").Replace("\r", " ").Replace("|", "-");

            string entry = date + "|" + text;

            // Prevent duplicate entries — skip if ANY existing entry for this object
            // matches the new entry (exact date+text) OR has the same display text on any date.
            if (_journalEntries.TryGetValue(objectId, out var existing) && !string.IsNullOrEmpty(existing))
            {
                // Check all entries for exact match (date+text)
                if (existing == entry || existing.Contains("\n" + entry) || existing.StartsWith(entry + "\n"))
                    return null;
                // Also check for same text on a different date (prevents near-duplicates from
                // events that fire on save/load with slightly different timestamps)
                if (existing.Contains("|" + text) || existing.Contains("|" + text + "\n"))
                    return null;
                // Strip entity markers and compare — catches entries with different «h:id» tags
                // but same display text (e.g., after custom name changes)
                string strippedNew = StripEntityMarkers(text);
                foreach (var line in existing.Split('\n'))
                {
                    int p = line.IndexOf('|');
                    if (p < 0) continue;
                    string strippedExisting = StripEntityMarkers(line.Substring(p + 1));
                    if (strippedExisting == strippedNew) return null;
                }

                string combined = existing + "\n" + entry;
                // Trim oldest entries if over the limit
                var lines = combined.Split('\n');
                if (lines.Length > MaxJournalEntriesPerEntity)
                {
                    int trimCount = lines.Length - MaxJournalEntriesPerEntity;
                    combined = string.Join("\n", lines, trimCount, lines.Length - trimCount);
                }
                _journalEntries[objectId] = combined;
            }
            else
                _journalEntries[objectId] = entry;

            // Bump persistent battle counters (NOT subject to journal trim)
            try { IncrementBattleStatsFromText(objectId, text); } catch (Exception ex) { MCMSettings.DebugLog("IncrementBattleStatsFromText failed: " + ex.ToString()); }

            EditableEncyclopediaAPI.RaiseJournalChanged(objectId);
            return entry;
        }

        // ── Persistent battle counters (immune to journal trim) ──

        /// <summary>Parse and increment the per-hero battle counters from the same text patterns
        /// that the legacy stat scan used. Idempotent at the per-text level (same call increments
        /// the same way as the journal scan would).</summary>
        private void IncrementBattleStatsFromText(string objectId, string t)
        {
            if (string.IsNullOrEmpty(objectId) || string.IsNullOrEmpty(t)) return;
            int dW = 0, dL = 0, dC = 0, dHK = 0, dTK = 0, dT = 0;
            if (t.Contains("Defeated by ")) dL++;
            else if ((t.Contains("Defeated ") || t.Contains("Victory ")) && t.StartsWith("[War]")) dW++;
            if (t.Contains("Captured by ") || t.Contains("Taken prisoner by ")) dC++;
            if ((t.Contains("Killed ") || t.Contains("Slew ")) && !t.Contains("Killed by ") && t.StartsWith("[War]")) dHK++;
            if (t.Contains("tournament")) dT++;
            int slainIdx = t.IndexOf("[slain:");
            if (slainIdx >= 0)
            {
                int numStart = slainIdx + 7;
                int numEnd = t.IndexOf(']', numStart);
                if (numEnd > numStart && int.TryParse(t.Substring(numStart, numEnd - numStart), out int slain))
                    dTK += slain;
            }
            if (dW == 0 && dL == 0 && dC == 0 && dHK == 0 && dTK == 0 && dT == 0) return;

            ParseBattleStats(objectId, out int w, out int l, out int c, out int hk, out int tk, out int trn);
            w += dW; l += dL; c += dC; hk += dHK; tk += dTK; trn += dT;
            _battleStats[objectId] = "W:" + w + "|L:" + l + "|C:" + c + "|HK:" + hk + "|TK:" + tk + "|T:" + trn;
        }

        private void ParseBattleStats(string objectId, out int w, out int l, out int c, out int hk, out int tk, out int trn)
        {
            w = l = c = hk = tk = trn = 0;
            if (!_battleStats.TryGetValue(objectId, out var raw) || string.IsNullOrEmpty(raw)) return;
            foreach (var part in raw.Split('|'))
            {
                int p = part.IndexOf(':');
                if (p <= 0) continue;
                string key = part.Substring(0, p);
                if (!int.TryParse(part.Substring(p + 1), out int v)) continue;
                switch (key)
                {
                    case "W": w = v; break;
                    case "L": l = v; break;
                    case "C": c = v; break;
                    case "HK": hk = v; break;
                    case "TK": tk = v; break;
                    case "T": trn = v; break;
                }
            }
        }

        /// <summary>Public getter for the persistent battle counters. Returns 0s if unset.</summary>
        public void GetBattleStats(string objectId, out int wins, out int losses, out int captures, out int heroKills, out int troopKills, out int tournaments)
        {
            ParseBattleStats(objectId, out wins, out losses, out captures, out heroKills, out troopKills, out tournaments);
        }

        /// <summary>One-time migration: seed the persistent counters from existing journal text
        /// for any hero that doesn't already have a counter entry. Called on save load.</summary>
        private void MigrateBattleStatsFromJournal()
        {
            if (_journalEntries == null) return;
            int migrated = 0;
            foreach (var kvp in _journalEntries)
            {
                string heroId = kvp.Key;
                if (string.IsNullOrEmpty(heroId)) continue;
                if (_battleStats.ContainsKey(heroId)) continue; // already has counter — don't overwrite
                if (string.IsNullOrEmpty(kvp.Value)) continue;

                int w = 0, l = 0, c = 0, hk = 0, tk = 0, trn = 0;
                foreach (var line in kvp.Value.Split('\n'))
                {
                    int p = line.IndexOf('|');
                    string t = p > 0 ? line.Substring(p + 1) : line;
                    if (string.IsNullOrEmpty(t)) continue;
                    if (t.Contains("Defeated by ")) l++;
                    else if ((t.Contains("Defeated ") || t.Contains("Victory ")) && t.StartsWith("[War]")) w++;
                    if (t.Contains("Captured by ") || t.Contains("Taken prisoner by ")) c++;
                    if ((t.Contains("Killed ") || t.Contains("Slew ")) && !t.Contains("Killed by ") && t.StartsWith("[War]")) hk++;
                    if (t.Contains("tournament")) trn++;
                    int slainIdx = t.IndexOf("[slain:");
                    if (slainIdx >= 0)
                    {
                        int numStart = slainIdx + 7;
                        int numEnd = t.IndexOf(']', numStart);
                        if (numEnd > numStart && int.TryParse(t.Substring(numStart, numEnd - numStart), out int slain))
                            tk += slain;
                    }
                }
                if (w + l + c + hk + tk + trn > 0)
                {
                    _battleStats[heroId] = "W:" + w + "|L:" + l + "|C:" + c + "|HK:" + hk + "|TK:" + tk + "|T:" + trn;
                    migrated++;
                }
            }
            if (migrated > 0) MCMSettings.DebugLog("MigrateBattleStatsFromJournal: seeded " + migrated + " hero counters from existing journal");
        }

        /// <summary>
        /// Removes a single journal entry by index (0-based).
        /// </summary>
        public void RemoveJournalEntry(string objectId, int index)
        {
            if (!_journalEntries.TryGetValue(objectId, out var raw) || string.IsNullOrEmpty(raw))
                return;

            var lines = new List<string>(raw.Split('\n'));
            if (index < 0 || index >= lines.Count) return;

            lines.RemoveAt(index);
            if (lines.Count == 0)
                _journalEntries.Remove(objectId);
            else
                _journalEntries[objectId] = string.Join("\n", lines);

            EditableEncyclopediaAPI.RaiseJournalChanged(objectId);
        }

        /// <summary>
        /// Replaces a journal entry's text at the given index, preserving the original date.
        /// </summary>
        public void ReplaceJournalEntry(string objectId, int index, string newText)
        {
            if (string.IsNullOrWhiteSpace(newText)) return;
            if (!_journalEntries.TryGetValue(objectId, out var raw) || string.IsNullOrEmpty(raw))
                return;

            var lines = new List<string>(raw.Split('\n'));
            if (index < 0 || index >= lines.Count) return;

            // Preserve original date, replace text
            string line = lines[index];
            int sep = line.IndexOf('|');
            string date = sep > 0 ? line.Substring(0, sep) : "";
            newText = newText.Replace("\n", " ").Replace("\r", " ").Replace("|", "-");
            lines[index] = date + "|" + newText;
            _journalEntries[objectId] = string.Join("\n", lines);

            EditableEncyclopediaAPI.RaiseJournalChanged(objectId);
        }

        /// <summary>
        /// Removes all journal entries for an object.
        /// </summary>
        public void ClearJournal(string objectId)
        {
            _journalEntries.Remove(objectId);
            EditableEncyclopediaAPI.RaiseJournalChanged(objectId);
        }

        /// <summary>
        /// Returns true if the object has any journal entries.
        /// </summary>
        public bool HasJournal(string objectId)
        {
            return _journalEntries.ContainsKey(objectId) && !string.IsNullOrEmpty(_journalEntries[objectId]);
        }

        /// <summary>
        /// Returns the total number of objects that have journal entries.
        /// </summary>
        public int GetJournalCount()
        {
            return _journalEntries.Count;
        }

        /// <summary>
        /// Gets the persisted timeline collapse state for a hero. Returns true if collapsed.
        /// </summary>
        public bool GetTimelineCollapsed(string heroId)
        {
            string val;
            return _timelineCollapseStates.TryGetValue(heroId, out val) && val == "1";
        }

        /// <summary>
        /// Sets the persisted timeline collapse state for a hero.
        /// </summary>
        public void SetTimelineCollapsed(string heroId, bool collapsed)
        {
            if (collapsed)
                _timelineCollapseStates[heroId] = "1";
            else
                _timelineCollapseStates.Remove(heroId);
        }

        /// <summary>
        /// Returns all journal entries for export.
        /// Key = objectId, Value = raw newline-separated journal string.
        /// </summary>
        public Dictionary<string, string> GetAllJournalForExport()
        {
            return new Dictionary<string, string>(_journalEntries);
        }

        /// <summary>
        /// Imports journal entries from a dictionary. Appends to existing entries.
        /// Returns the number of entries imported.
        /// </summary>
        public int ImportJournal(Dictionary<string, string> journal)
        {
            if (journal == null) return 0;
            int count = 0;
            foreach (var kvp in journal)
            {
                if (string.IsNullOrEmpty(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                    continue;

                if (_journalEntries.TryGetValue(kvp.Key, out var existing) && !string.IsNullOrEmpty(existing))
                    _journalEntries[kvp.Key] = existing + "\n" + kvp.Value;
                else
                    _journalEntries[kvp.Key] = kvp.Value;
                count++;
            }
            return count;
        }

        /// <summary>
        /// Clears all journal entries across all objects.
        /// Returns the number of objects cleared.
        /// </summary>
        public int ClearAllJournal()
        {
            try
            {
                if (_journalEntries == null)
                {
                    _journalEntries = new Dictionary<string, string>();
                    return 0;
                }
                int count = _journalEntries.Count;
                _journalEntries.Clear();
                MCMSettings.DebugLog("EncyclopediaEditBehavior: ClearAllJournal cleared " + count + " journal object(s)");
                return count;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("EncyclopediaEditBehavior: ClearAllJournal failed: " + ex.ToString());
                return 0;
            }
        }
        // ── Relation Notes ──

        /// <summary>
        /// Gets the note for a relationship between two heroes.
        /// </summary>
        public string GetRelationNote(string heroId, string targetHeroId)
        {
            string key = heroId + "_" + targetHeroId;
            // Fallback: check legacy pipe-separated key for saves from older versions
            if (!_relationNotes.TryGetValue(key, out var note))
            {
                string legacyKey = heroId + "|" + targetHeroId;
                if (_relationNotes.TryGetValue(legacyKey, out note))
                {
                    // Migrate to new key format
                    _relationNotes[key] = note;
                    _relationNotes.Remove(legacyKey);
                }
            }
            return note;
        }

        /// <summary>
        /// Sets (or clears) the note for a relationship between two heroes.
        /// If the note text is empty/whitespace, removes the entry to keep saves clean.
        /// </summary>
        public void SetRelationNote(string heroId, string targetHeroId, string note)
        {
            string key = heroId + "_" + targetHeroId;
            if (string.IsNullOrWhiteSpace(note))
                _relationNotes.Remove(key);
            else
                _relationNotes[key] = note;

            // Clean up any legacy pipe-separated key
            string legacyKey = heroId + "|" + targetHeroId;
            _relationNotes.Remove(legacyKey);
        }

        /// <summary>
        /// Returns true if a relation note exists for the given hero pair.
        /// </summary>
        public bool HasRelationNote(string heroId, string targetHeroId)
        {
            string key = heroId + "_" + targetHeroId;
            if (_relationNotes.ContainsKey(key) && !string.IsNullOrEmpty(_relationNotes[key]))
                return true;
            // Check legacy key
            string legacyKey = heroId + "|" + targetHeroId;
            return _relationNotes.ContainsKey(legacyKey) && !string.IsNullOrEmpty(_relationNotes[legacyKey]);
        }

        /// <summary>
        /// Returns all relation notes where the specified hero is the source (viewer).
        /// Each entry is (targetHeroId, note).
        /// </summary>
        public List<(string otherHeroId, string note)> GetRelationNotesForHero(string heroId)
        {
            var results = new List<(string, string)>();
            string prefix = heroId + "_";
            string legacyPrefix = heroId + "|";
            foreach (var kvp in _relationNotes)
            {
                if (string.IsNullOrEmpty(kvp.Value)) continue;
                if (kvp.Key.StartsWith(prefix))
                    results.Add((kvp.Key.Substring(prefix.Length), kvp.Value));
                else if (kvp.Key.StartsWith(legacyPrefix))
                    results.Add((kvp.Key.Substring(legacyPrefix.Length), kvp.Value));
            }
            return results;
        }

        /// <summary>
        /// Returns all relation notes where the specified hero is the target (i.e., notes others wrote about them).
        /// Each entry is (sourceHeroId, note).
        /// </summary>
        public List<(string sourceHeroId, string note)> GetRelationNotesAboutHero(string targetHeroId)
        {
            var results = new List<(string, string)>();
            string suffix = "_" + targetHeroId;
            string legacySuffix = "|" + targetHeroId;
            foreach (var kvp in _relationNotes)
            {
                if (string.IsNullOrEmpty(kvp.Value)) continue;
                if (kvp.Key.EndsWith(suffix))
                    results.Add((kvp.Key.Substring(0, kvp.Key.Length - suffix.Length), kvp.Value));
                else if (kvp.Key.EndsWith(legacySuffix))
                    results.Add((kvp.Key.Substring(0, kvp.Key.Length - legacySuffix.Length), kvp.Value));
            }
            return results;
        }

        /// <summary>
        /// Returns the total number of relation notes stored.
        /// </summary>
        public int GetRelationNoteCount()
        {
            int count = 0;
            foreach (var kvp in _relationNotes)
            {
                if (!string.IsNullOrEmpty(kvp.Value)) count++;
            }
            return count;
        }

        /// <summary>
        /// Gets the tag for a relation note (e.g. "ally", "rival", "trade", "family", "other").
        /// Returns null if no tag is set.
        /// </summary>
        public string GetRelationNoteTag(string heroId, string targetHeroId)
        {
            if (_relationNoteTags == null) return null;
            string key = heroId + "_" + targetHeroId;
            _relationNoteTags.TryGetValue(key, out var tag);
            return tag;
        }

        /// <summary>
        /// Sets or clears the tag for a relation note.
        /// </summary>
        public void SetRelationNoteTag(string heroId, string targetHeroId, string tag)
        {
            if (_relationNoteTags == null) _relationNoteTags = new Dictionary<string, string>();
            string key = heroId + "_" + targetHeroId;
            if (string.IsNullOrWhiteSpace(tag))
                _relationNoteTags.Remove(key);
            else
                _relationNoteTags[key] = tag;
        }

        /// <summary>
        /// Returns true if the tag is locked (player chose to prevent auto-suggest).
        /// </summary>
        public bool IsRelationNoteTagLocked(string heroId, string targetHeroId)
        {
            if (_relationNoteTagLocks == null) return false;
            string key = heroId + "_" + targetHeroId;
            return _relationNoteTagLocks.ContainsKey(key);
        }

        /// <summary>
        /// Locks or unlocks a relation note tag.
        /// </summary>
        public void SetRelationNoteTagLock(string heroId, string targetHeroId, bool locked)
        {
            if (_relationNoteTagLocks == null) _relationNoteTagLocks = new Dictionary<string, string>();
            string key = heroId + "_" + targetHeroId;
            if (locked)
                _relationNoteTagLocks[key] = "1";
            else
                _relationNoteTagLocks.Remove(key);
        }

        /// <summary>
        /// Suggests a tag based on strong signals only.
        /// Returns null if no strong signal exists.
        /// Thresholds: relation &lt;= -40 → rival, &gt;= +40 → ally, same clan → family.
        /// </summary>
        public string SuggestRelationNoteTag(string heroId, string targetHeroId)
        {
            // Don't suggest if tag is locked
            if (IsRelationNoteTagLocked(heroId, targetHeroId)) return null;

            // Don't suggest if a tag is already manually set
            string existing = GetRelationNoteTag(heroId, targetHeroId);
            if (!string.IsNullOrEmpty(existing)) return null;

            Hero viewingHero = null;
            Hero targetHero = null;
            foreach (var h in Hero.AllAliveHeroes)
            {
                if (h?.StringId == heroId) viewingHero = h;
                if (h?.StringId == targetHeroId) targetHero = h;
                if (viewingHero != null && targetHero != null) break;
            }
            if (viewingHero == null || targetHero == null)
            {
                // Check dead heroes too
                foreach (var h in Hero.DeadOrDisabledHeroes)
                {
                    if (viewingHero == null && h?.StringId == heroId) viewingHero = h;
                    if (targetHero == null && h?.StringId == targetHeroId) targetHero = h;
                    if (viewingHero != null && targetHero != null) break;
                }
            }
            if (viewingHero == null || targetHero == null) return null;

            // Same clan → family (strongest signal, checked first)
            try
            {
                if (viewingHero.Clan != null && targetHero.Clan != null
                    && viewingHero.Clan == targetHero.Clan)
                    return "family";
            }
            catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: SuggestRelationNoteTag clan check failed: " + ex.ToString()); }

            // Relation-based (strong signals only)
            try
            {
                int relation = viewingHero.GetRelation(targetHero);
                if (relation <= RelationSuggestRivalThreshold) return "rival";
                if (relation >= RelationSuggestAllyThreshold) return "ally";
            }
            catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: SuggestRelationNoteTag relation check failed: " + ex.ToString()); }

            return null;
        }

        /// <summary>
        /// Returns all relation notes for export.
        /// </summary>
        public Dictionary<string, string> GetAllRelationNotesForExport()
        {
            return new Dictionary<string, string>(_relationNotes);
        }

        /// <summary>
        /// Returns all relation history entries for export.
        /// </summary>
        public Dictionary<string, string> GetAllRelationHistoryForExport()
        {
            if (_relationHistory == null) return new Dictionary<string, string>();
            return new Dictionary<string, string>(_relationHistory);
        }

        /// <summary>
        /// Imports relation history. Appends to existing history for the same key.
        /// </summary>
        public int ImportRelationHistory(Dictionary<string, string> history)
        {
            if (history == null) return 0;
            if (_relationHistory == null)
                _relationHistory = new Dictionary<string, string>();
            int count = 0;
            foreach (var kvp in history)
            {
                if (string.IsNullOrEmpty(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                    continue;
                if (_relationHistory.TryGetValue(kvp.Key, out var existing) && !string.IsNullOrEmpty(existing))
                    _relationHistory[kvp.Key] = existing + "\n" + kvp.Value;
                else
                    _relationHistory[kvp.Key] = kvp.Value;
                count++;
            }
            return count;
        }

        /// <summary>
        /// Imports relation notes. Overwrites existing notes for the same key.
        /// </summary>
        public int ImportRelationNotes(Dictionary<string, string> notes)
        {
            if (notes == null) return 0;
            int count = 0;
            foreach (var kvp in notes)
            {
                if (string.IsNullOrEmpty(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                    continue;
                _relationNotes[kvp.Key] = kvp.Value;
                count++;
            }
            return count;
        }

        // ── Relation History ──

        /// <summary>
        /// Records a relation change between two heroes with date and description.
        /// Only tracks changes involving the player's main hero.
        /// </summary>
        public void AddRelationHistoryEntry(string heroId, string targetHeroId, int change, string description)
        {
            if (string.IsNullOrEmpty(heroId) || string.IsNullOrEmpty(targetHeroId)) return;

            string date;
            try { date = GetCurrentGameDateString(); }
            catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: AddRelationHistoryEntry date lookup failed: " + ex.ToString()); date = "Unknown Date"; }

            string sign = change >= 0 ? "+" : "";
            string entry = date + "|" + sign + change + "|" + description.Replace("\n", " ").Replace("|", "-");
            string key = heroId + "_" + targetHeroId;

            if (_relationHistory.TryGetValue(key, out var existing) && !string.IsNullOrEmpty(existing))
                _relationHistory[key] = existing + "\n" + entry;
            else
                _relationHistory[key] = entry;
        }

        /// <summary>
        /// Gets relation history entries for a hero pair.
        /// Returns list of (date, changeStr, description) tuples.
        /// </summary>
        public List<RelationHistoryEntry> GetRelationHistory(string heroId, string targetHeroId)
        {
            var result = new List<RelationHistoryEntry>();
            string key = heroId + "_" + targetHeroId;
            if (!_relationHistory.TryGetValue(key, out var raw) || string.IsNullOrEmpty(raw))
                return result;

            foreach (var line in raw.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(new[] { '|' }, 3);
                if (parts.Length >= 3)
                {
                    result.Add(new RelationHistoryEntry
                    {
                        Date = parts[0],
                        Change = parts[1],
                        Text = parts[2]
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// Gets all relation history entries where the player hero is the viewer
        /// and the target matches the given heroId.
        /// Used by the Timeline to show relation changes for a specific hero.
        /// </summary>
        public List<RelationHistoryEntry> GetRelationHistoryForHero(string targetHeroId)
        {
            var result = new List<RelationHistoryEntry>();
            string mainHeroId = Hero.MainHero?.StringId;
            if (string.IsNullOrEmpty(mainHeroId)) return result;

            string key = mainHeroId + "_" + targetHeroId;
            if (!_relationHistory.TryGetValue(key, out var raw) || string.IsNullOrEmpty(raw))
                return result;

            foreach (var line in raw.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(new[] { '|' }, 3);
                if (parts.Length >= 3)
                {
                    result.Add(new RelationHistoryEntry
                    {
                        Date = parts[0],
                        Change = parts[1],
                        Text = "Relation " + parts[1] + ": " + parts[2]
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// Attempts to register for CharacterRelationChangedEvent via reflection.
        /// This event may not exist in all Bannerlord versions.
        /// </summary>
        private void TryRegisterRelationChangeEvent()
        {
            try
            {
                // Look for a field/property named "CharacterRelationChangedEvent" on CampaignEvents
                var campaignEventsType = typeof(CampaignEvents);
                var eventProp = campaignEventsType.GetProperty("CharacterRelationChangedEvent",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                object eventObj = null;
                if (eventProp != null)
                {
                    eventObj = eventProp.GetValue(null);
                }
                else
                {
                    var eventField = campaignEventsType.GetField("CharacterRelationChangedEvent",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (eventField != null)
                    {
                        eventObj = eventField.GetValue(null);
                    }
                }

                if (eventObj == null)
                {
                    MCMSettings.DebugLog("CharacterRelationChangedEvent not found — relation tracking disabled.");
                    return;
                }

                // The event object should have AddNonSerializedListener(object, Action<Hero, Hero, int>)
                var addMethod = eventObj.GetType().GetMethod("AddNonSerializedListener");
                if (addMethod == null)
                {
                    MCMSettings.DebugLog("AddNonSerializedListener not found on CharacterRelationChangedEvent.");
                    return;
                }

                Action<Hero, Hero, int> handler = OnCharacterRelationChanged;
                addMethod.Invoke(eventObj, new object[] { this, handler });
                MCMSettings.DebugLog("CharacterRelationChangedEvent registered via reflection.");
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("TryRegisterRelationChangeEvent failed: " + ex.ToString());
            }
        }

        /// <summary>
        /// Registers a CampaignEvents event by name via reflection (for events that may not exist in all versions).
        /// </summary>
        private void TryRegisterEventByReflection(string eventName, Delegate handler)
        {
            try
            {
                var ceType = typeof(CampaignEvents);
                var prop = ceType.GetProperty(eventName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                object eventObj = prop?.GetValue(null);
                if (eventObj == null)
                {
                    var field = ceType.GetField(eventName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    eventObj = field?.GetValue(null);
                }
                if (eventObj == null) { MCMSettings.DebugLog("RegisterEvents: " + eventName + " not found — skipped."); return; }
                var addMethod = eventObj.GetType().GetMethod("AddNonSerializedListener");
                if (addMethod == null) { MCMSettings.DebugLog("RegisterEvents: " + eventName + " has no AddNonSerializedListener."); return; }
                addMethod.Invoke(eventObj, new object[] { this, handler });
                MCMSettings.DebugLog("RegisterEvents: " + eventName + " registered via reflection.");
            }
            catch (Exception ex) { MCMSettings.DebugLog("RegisterEvents: " + eventName + " failed: " + ex.ToString()); }
        }

        /// <summary>
        /// Event handler for CharacterRelationChangedEvent — tracks relation changes involving the player.
        /// </summary>
        private void OnCharacterRelationChanged(Hero hero1, Hero hero2, int relationChange)
        {
            try
            {
                if (hero1 == null || hero2 == null || relationChange == 0) return;

                var mainHero = Hero.MainHero;
                if (mainHero == null) return;

                // Only track if player is involved
                if (hero1.StringId != mainHero.StringId && hero2.StringId != mainHero.StringId) return;

                // Determine which hero is the "target" (the one who isn't the player)
                Hero target = hero1.StringId == mainHero.StringId ? hero2 : hero1;
                string targetName = target.Name?.ToString() ?? "Unknown";

                // Try to determine reason from recent game events
                string reason = DetermineRelationChangeReason(target, relationChange);

                AddRelationHistoryEntry(mainHero.StringId, target.StringId, relationChange, reason);
                MCMSettings.DebugLog("RelationHistory: " + targetName + " " + (relationChange >= 0 ? "+" : "") + relationChange + " (" + reason + ")");
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("OnCharacterRelationChanged error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Tries to determine the reason for a relation change based on context.
        /// </summary>
        private string DetermineRelationChangeReason(Hero target, int change)
        {
            // Simple heuristic based on common game mechanics
            if (change >= RelationChangeSignificantPositive) return "Significant positive event";
            if (change >= RelationChangePositive) return "Positive interaction";
            if (change > 0) return "Minor positive event";
            if (change <= RelationChangeMajorNegative) return "Major negative event";
            if (change <= RelationChangeSignificantNegative) return "Significant negative event";
            if (change < 0) return "Negative interaction";
            return "Unknown";
        }

        // ── Auto-Journal Event Handlers ──
        // Categories: [War] [Politics] [Family] [Crime]
        // Importance: Minor / Major / Historic
        // Anti-spam: dedup per objectId+text per game day, ignore small bandit fights

        private bool IsAutoJournalEnabled
        {
            get
            {
                var settings = MCMSettings.Instance;
                return settings != null ? settings.EnableAutoJournal : true;
            }
        }

        // Anti-spam: track "objectId|text" logged this game day to prevent duplicates
        private readonly HashSet<string> _autoLoggedThisDay = new HashSet<string>();
        private float _lastAutoLogDay = -1f;

        /// <summary>
        /// Removes exact duplicate lines from all journal entries on load.
        /// Duplicates accumulate when events replay during save/load and bypass the dedup set.
        /// </summary>
        private void DeduplicateJournalEntries()
        {
            int totalRemoved = 0;
            var keys = new List<string>(_journalEntries.Keys);
            foreach (var key in keys)
            {
                if (!_journalEntries.TryGetValue(key, out var raw) || string.IsNullOrEmpty(raw)) continue;
                var lines = raw.Split('\n');
                if (lines.Length <= 1) continue;

                var seen = new HashSet<string>();
                var unique = new List<string>();
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // Dedup key: text after the date|separator, with entity markers stripped
                    // so entries that differ only in «h:id»Name«/h» formatting are caught
                    int pipe = line.IndexOf('|');
                    string textPart = pipe > 0 ? line.Substring(pipe + 1) : line;
                    string dedupKey = StripEntityMarkers(textPart);
                    if (seen.Add(dedupKey))
                        unique.Add(line);
                    else
                        totalRemoved++;
                }
                if (unique.Count < lines.Length)
                    _journalEntries[key] = string.Join("\n", unique);
            }
            if (totalRemoved > 0)
                MCMSettings.DebugLog("DeduplicateJournalEntries: removed " + totalRemoved + " duplicate entries across all journals");
            else
                MCMSettings.DebugLog("DeduplicateJournalEntries: no duplicates found in " + keys.Count + " journal entries");
        }

        /// <summary>
        /// Strips «h:id»Name«/h», «c:id»Name«/c», «k:id»Name«/k», «s:id»Name«/s» entity markers,
        /// leaving just the display name. Used for dedup comparison so entries with different
        /// hero IDs but same display text are detected as duplicates.
        /// </summary>
        public static string StripEntityMarkers(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string result = text;
            while (true)
            {
                int start = result.IndexOf("\u00AB"); // «
                if (start < 0) break;
                int markerEnd = result.IndexOf("\u00BB", start); // »
                if (markerEnd < 0) break;
                // Check for closing tag «/h» «/c» «/k» «/s»
                string between = result.Substring(start, markerEnd - start + 1);
                if (between.StartsWith("\u00AB/"))
                {
                    // Remove closing tag
                    result = result.Remove(start, markerEnd - start + 1);
                }
                else
                {
                    // Opening tag «h:id» — remove tag but keep content after »
                    result = result.Remove(start, markerEnd - start + 1);
                }
            }
            return result;
        }

        /// <summary>
        /// Pre-populates the auto-log dedup set with all existing journal entries
        /// so that events replayed after save/load don't create duplicates.
        /// </summary>
        private void PrePopulateAutoLogDedup()
        {
            foreach (var kvp in _journalEntries)
            {
                if (string.IsNullOrEmpty(kvp.Value)) continue;
                foreach (var line in kvp.Value.Split('\n'))
                {
                    int pipe = line.IndexOf('|');
                    if (pipe < 0) continue;
                    string text = line.Substring(pipe + 1);
                    _autoLoggedThisDay.Add(kvp.Key + "|" + text);
                }
            }
            MCMSettings.DebugLog("AutoJournal: pre-populated dedup set with " + _autoLoggedThisDay.Count + " existing entries");
        }

        // Gate initial-load spam: specific event handlers check this flag to skip
        // logging during campaign initialization. Battle events are NOT gated.
        private bool _campaignFullyLoaded = false;

        /// <summary>
        /// Writes an auto-journal line and returns the chronicle entry key it now owns —
        /// or null when nothing was written (feature off, blank id, dedup hit, or an error).
        ///
        /// The return value is what makes spoils capture safe: it is derived from the values that
        /// were *actually stored* (the date AddJournalEntry stamped, and the text after
        /// AddJournalEntry sanitised it), so it can never disagree with the key a renderer
        /// recomputes from the same stored entry. Callers that have nothing to attach can keep
        /// ignoring the return value — all 100+ existing call sites do.
        /// </summary>
        private string AutoLog(string objectId, string text)
        {
            if (!IsAutoJournalEnabled) return null;
            if (string.IsNullOrEmpty(objectId)) return null;
            try
            {
                // Reset tracking on new game day — but preserve load-dedup entries
                // until the campaign is fully loaded to prevent event replay duplicates
                float currentDay = (float)CampaignTime.Now.ToDays;
                if (currentDay != _lastAutoLogDay)
                {
                    if (_campaignFullyLoaded)
                        _autoLoggedThisDay.Clear();
                    _lastAutoLogDay = currentDay;
                }

                // Deduplicate: skip if same objectId+text already logged
                string key = objectId + "|" + text;
                if (!_autoLoggedThisDay.Add(key)) return null;

                string stored = AddJournalEntryInternal(objectId, text);
                if (string.IsNullOrEmpty(stored)) return null;
                int sep = stored.IndexOf('|');
                if (sep < 0) return null;
                return MakeChronicleKey(objectId, stored.Substring(0, sep), stored.Substring(sep + 1));
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal error: " + ex.ToString()); }
            return null;
        }

        // ── Chronicle spoils (gold + looted items per chronicle entry) ──

        /// <summary>
        /// Builds the chronicle entry key for an entry.
        ///
        /// This MUST stay byte-for-byte identical to
        /// EE-ChronicleNoters\ChronicleEntryPopulator.cs MakeStableId(entityId, date, text):
        ///     entityId + "|" + date + "|" + first 24 characters of the RAW stored text.
        /// "Raw" means before «h:…» marker stripping and before the "[Category] " prefix is
        /// removed — the renderer computes its EntryId from ChronicleEntry.Text, which is the
        /// unmodified stored text (see EditableEncyclopediaAPI.GetAllChronicleEntries).
        /// If that formula ever changes on either side, spoils silently stop resolving, so the
        /// two copies are deliberately trivial and identical rather than shared through a
        /// cross-module call that could version-skew.
        /// </summary>
        internal static string MakeChronicleKey(string entityId, string date, string text)
        {
            string e = entityId ?? string.Empty;
            string d = date ?? string.Empty;
            string t = text ?? string.Empty;
            if (t.Length > 24) t = t.Substring(0, 24);
            return e + "|" + d + "|" + t;
        }

        /// <summary>Hard ceiling on stored spoils records, so a long campaign cannot bloat the save.</summary>
        private const int MaxChronicleSpoilsRecords = 600;

        /// <summary>
        /// Persists the spoils of one chronicle entry under the key AutoLog just returned.
        /// No-ops on a null key (AutoLog wrote nothing) or on empty spoils, so a battle with no
        /// gold and no loot never creates a record.
        /// </summary>
        private void RecordChronicleSpoils(string entryKey, ChronicleSpoils spoils)
        {
            try
            {
                if (string.IsNullOrEmpty(entryKey) || spoils == null || spoils.IsEmpty) return;
                if (_chronicleSpoils == null) _chronicleSpoils = new Dictionary<string, string>();

                if (_chronicleSpoils.Count >= MaxChronicleSpoilsRecords && !_chronicleSpoils.ContainsKey(entryKey))
                {
                    PruneOrphanedChronicleSpoils();
                    // Still at the ceiling with live records only? Keep the save bounded and skip.
                    if (_chronicleSpoils.Count >= MaxChronicleSpoilsRecords) return;
                }

                _chronicleSpoils[entryKey] = SerializeChronicleSpoils(spoils);
            }
            catch (Exception ex) { MCMSettings.DebugLog("RecordChronicleSpoils failed: " + ex.ToString()); }
        }

        private static string SerializeChronicleSpoils(ChronicleSpoils spoils)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(spoils.Gold.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('|');
            sb.Append(spoils.OmittedItemStacks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('|');
            if (spoils.Items != null)
            {
                bool first = true;
                foreach (var item in spoils.Items)
                {
                    if (item == null || string.IsNullOrEmpty(item.ItemId) || item.Count <= 0) continue;
                    if (!first) sb.Append(';');
                    first = false;
                    sb.Append(item.ItemId);
                    sb.Append('*');
                    sb.Append(item.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Reads back the spoils for a chronicle entry key. Never returns null — an unknown or
        /// malformed key yields an empty record.
        /// </summary>
        internal ChronicleSpoils GetChronicleSpoils(string entryKey)
        {
            var result = new ChronicleSpoils();
            try
            {
                if (string.IsNullOrEmpty(entryKey) || _chronicleSpoils == null) return result;
                string raw;
                if (!_chronicleSpoils.TryGetValue(entryKey, out raw) || string.IsNullOrEmpty(raw)) return result;

                // Split into exactly 3 so a stray '|' inside the item section (which item
                // StringIds never contain, but be safe) cannot silently drop stacks.
                string[] parts = raw.Split(new char[] { '|' }, 3);
                if (parts.Length < 3) return result;

                int gold;
                if (int.TryParse(parts[0], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out gold))
                    result.Gold = gold;

                int omitted;
                if (int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out omitted) && omitted > 0)
                    result.OmittedItemStacks = omitted;

                if (parts[2].Length == 0) return result;
                foreach (var chunk in parts[2].Split(';'))
                {
                    if (string.IsNullOrEmpty(chunk)) continue;
                    int star = chunk.LastIndexOf('*');
                    if (star <= 0 || star == chunk.Length - 1) continue;
                    int count;
                    if (!int.TryParse(chunk.Substring(star + 1), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out count) || count <= 0) continue;
                    result.Items.Add(new ChronicleSpoilItem(chunk.Substring(0, star), count));
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("GetChronicleSpoils failed: " + ex.ToString()); }
            return result;
        }

        internal bool HasChronicleSpoils(string entryKey)
        {
            try
            {
                return !string.IsNullOrEmpty(entryKey)
                       && _chronicleSpoils != null
                       && _chronicleSpoils.ContainsKey(entryKey);
            }
            catch (Exception ex) { MCMSettings.DebugLog("HasChronicleSpoils failed: " + ex.ToString()); return false; }
        }

        /// <summary>
        /// Drops spoils records whose chronicle line no longer exists. The journal is trimmed to
        /// MaxJournalEntriesPerEntity per entity, so old lines (and their keys) do disappear.
        /// Rebuilding the live key set is the same single walk PrePopulateAutoLogDedup does.
        /// </summary>
        private void PruneOrphanedChronicleSpoils()
        {
            if (_chronicleSpoils == null || _chronicleSpoils.Count == 0) return;
            var live = new HashSet<string>();
            foreach (var kvp in _journalEntries)
            {
                if (string.IsNullOrEmpty(kvp.Value)) continue;
                foreach (var line in kvp.Value.Split('\n'))
                {
                    int pipe = line.IndexOf('|');
                    if (pipe < 0) continue;
                    live.Add(MakeChronicleKey(kvp.Key, line.Substring(0, pipe), line.Substring(pipe + 1)));
                }
            }
            var orphans = new List<string>();
            foreach (var key in _chronicleSpoils.Keys)
            {
                if (!live.Contains(key)) orphans.Add(key);
            }
            foreach (var key in orphans) _chronicleSpoils.Remove(key);
            if (orphans.Count > 0)
                MCMSettings.DebugLog("ChronicleSpoils: pruned " + orphans.Count + " orphaned record(s), "
                    + _chronicleSpoils.Count + " remain");
        }

        /// <summary>
        /// Tries to get a 2D position from an object via reflection (Position2D, GatePosition, GetPosition2D, etc.).
        /// Returns null if no position property found.
        /// </summary>
        private static Vec2? TryGetPosition2D(object obj)
        {
            if (obj == null) return null;
            try
            {
                var type = obj.GetType();
                // Try common position property names
                string[] propNames = { "Position2D", "GatePosition", "GetPosition2D" };
                foreach (var name in propNames)
                {
                    var prop = type.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (prop != null)
                    {
                        var val = prop.GetValue(obj);
                        if (val is Vec2 v) return v;
                        // Handle CampaignVec2 or similar — extract X/Y via reflection
                        if (val != null)
                        {
                            var xProp = val.GetType().GetProperty("x") ?? val.GetType().GetProperty("X");
                            var yProp = val.GetType().GetProperty("y") ?? val.GetType().GetProperty("Y");
                            if (xProp != null && yProp != null)
                                return new Vec2(Convert.ToSingle(xProp.GetValue(val)), Convert.ToSingle(yProp.GetValue(val)));
                        }
                    }
                    // Try as method
                    var method = type.GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (method != null)
                    {
                        var val = method.Invoke(obj, null);
                        if (val is Vec2 v2) return v2;
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: TryGetPosition2D failed: " + ex.ToString()); }
            return null;
        }

        /// <summary>
        /// Finds the nearest settlement and returns "near SettlementName". Returns fallback if none found.
        /// </summary>
        private static string GetNearestLocationString(object positionSource, string fallback = "the field")
        {
            try
            {
                Vec2? pos = null;
                if (positionSource is Vec2 v) pos = v;
                else pos = TryGetPosition2D(positionSource);

                if (pos == null) return fallback;
                Vec2 position = pos.Value;

                Settlement nearest = null;
                float bestDist = float.MaxValue;
                foreach (var s in Settlement.All)
                {
                    if (s == null || s.IsHideout) continue;
                    Vec2? sPos = TryGetPosition2D(s);
                    if (sPos == null) continue;
                    float dx = sPos.Value.x - position.x;
                    float dy = sPos.Value.y - position.y;
                    float dist = dx * dx + dy * dy;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        nearest = s;
                    }
                }
                if (nearest != null)
                    return "near " + "«s:" + nearest.StringId + "»" + nearest.Name + "«/s»";
            }
            catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: GetNearestLocationString failed: " + ex.ToString()); }
            return fallback;
        }

        /// <summary>
        /// Returns the plain name of the nearest settlement to the given object, or null.
        /// Uses reflection to find Position2D/GatePosition on the source object.
        /// </summary>
        internal static string GetNearestSettlementName(object positionSource)
        {
            try
            {
                Vec2? pos = TryGetPosition2D(positionSource);
                if (pos == null) return null;
                Vec2 position = pos.Value;

                Settlement nearest = null;
                float bestDist = float.MaxValue;
                foreach (var s in Settlement.All)
                {
                    if (s == null || s.IsHideout) continue;
                    Vec2? sPos = TryGetPosition2D(s);
                    if (sPos == null) continue;
                    float dx = sPos.Value.x - position.x;
                    float dy = sPos.Value.y - position.y;
                    float dist = dx * dx + dy * dy;
                    if (dist < bestDist) { bestDist = dist; nearest = s; }
                }
                return nearest?.Name?.ToString();
            }
            catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: GetNearestSettlementName failed: " + ex.ToString()); return null; }
        }

        private static string HeroInfo(Hero hero)
        {
            if (hero == null) return "Unknown";
            string name = hero.Name?.ToString() ?? "Unknown";
            string markedName = "«h:" + hero.StringId + "»" + name + "«/h»";
            // Add kingdom or clan affiliation with markers
            if (hero.Clan?.Kingdom != null)
            {
                string kName = hero.Clan.Kingdom.Name?.ToString() ?? "";
                if (!string.IsNullOrEmpty(kName))
                    return markedName + " of " + "«k:" + hero.Clan.Kingdom.StringId + "»" + kName + "«/k»";
            }
            else if (hero.Clan != null)
            {
                string cName = hero.Clan.Name?.ToString() ?? "";
                if (!string.IsNullOrEmpty(cName))
                    return markedName + " of " + "«c:" + hero.Clan.StringId + "»" + cName + "«/c»";
            }
            return markedName;
        }

        private static string PartyInfo(MapEventSide side)
        {
            var hero = side?.LeaderParty?.LeaderHero;
            if (hero != null) return HeroInfo(hero);
            return side?.LeaderParty?.Name?.ToString() ?? "Unknown";
        }

        // Cache pre-battle troop counts keyed by MapEvent identity hash
        private readonly Dictionary<int, Tuple<int, int>> _preBattleTroopCounts = new Dictionary<int, Tuple<int, int>>();

        // Cache prisoner→rescuer mappings from battles so OnHeroPrisonerReleased can show who freed them.
        // Key = prisoner StringId, Value = rescuer Hero. Cleared after use or on next battle.
        private readonly Dictionary<string, Hero> _battleRescuers = new Dictionary<string, Hero>();

        private void OnMapEventStarted(MapEvent mapEvent, PartyBase attackerParty, PartyBase defenderParty)
        {
            if (!IsAutoJournalEnabled) return;
            if (mapEvent == null) return;
            try
            {
                int key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(mapEvent);
                // Use the party parameters directly — mapEvent.AttackerSide.Parties
                // may not be fully populated yet at event start time.
                int attackers = attackerParty?.NumberOfAllMembers ?? 0;
                int defenders = defenderParty?.NumberOfAllMembers ?? 0;
                // Also try side totals as they may include reinforcements
                int sideAttackers = TotalTroops(mapEvent.AttackerSide);
                int sideDefenders = TotalTroops(mapEvent.DefenderSide);
                // Use the larger of direct party count vs side total
                attackers = Math.Max(attackers, sideAttackers);
                defenders = Math.Max(defenders, sideDefenders);
                _preBattleTroopCounts[key] = Tuple.Create(attackers, defenders);
                MCMSettings.DebugLog("AutoJournal: MapEventStarted cached troops: " + attackers + " vs " + defenders + " (key=" + key + ")");
                // Prevent unbounded growth — remove oldest entries if too many
                if (_preBattleTroopCounts.Count > PreBattleTroopCacheMaxSize)
                    _preBattleTroopCounts.Clear();
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal: MapEventStarted error: " + ex.ToString()); }
        }

        private static int TotalTroops(MapEventSide side)
        {
            try { return side?.Parties?.Sum(p => p.Party?.NumberOfAllMembers ?? 0) ?? 0; } catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: TotalTroops failed: " + ex.ToString()); return 0; }
        }

        /// <summary>
        /// Collects heroes captured (now in prison roster) by the winning side after a battle.
        /// </summary>
        private static List<Hero> CollectCapturedHeroes(MapEventSide winnerSide)
        {
            var captured = new List<Hero>();
            try
            {
                if (winnerSide?.Parties == null) return captured;
                foreach (var partyInfo in winnerSide.Parties)
                {
                    var party = partyInfo.Party;
                    if (party?.PrisonRoster == null) continue;
                    foreach (var troop in party.PrisonRoster.GetTroopRoster())
                    {
                        var hero = troop.Character?.HeroObject;
                        if (hero != null && !captured.Contains(hero))
                            captured.Add(hero);
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("CollectCapturedHeroes error: " + ex.ToString()); }
            return captured;
        }

        /// <summary>
        /// Collects heroes from both sides who died or were wounded in this battle.
        /// </summary>
        private static void CollectHeroCasualties(MapEvent mapEvent,
            out List<Hero> killed, out List<Hero> wounded)
        {
            killed = new List<Hero>();
            wounded = new List<Hero>();
            try
            {
                if (mapEvent == null) return;
                var allParties = mapEvent.AttackerSide.Parties.Union(mapEvent.DefenderSide.Parties);
                foreach (var partyInfo in allParties)
                {
                    var hero = partyInfo.Party?.LeaderHero;
                    if (hero == null) continue;
                    if (hero.IsDead && !killed.Contains(hero))
                        killed.Add(hero);
                    else if (hero.IsWounded && !wounded.Contains(hero))
                        wounded.Add(hero);
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("CollectHeroCasualties error: " + ex.ToString()); }
        }

        /// <summary>
        /// Builds a compact hero list string like "«h:id»Name«/h», «h:id2»Name2«/h»" (max 3 names).
        /// </summary>
        private static string FormatHeroList(List<Hero> heroes, int max = 3)
        {
            if (heroes == null || heroes.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            int count = Math.Min(heroes.Count, max);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("«h:" + heroes[i].StringId + "»" + heroes[i].Name + "«/h»");
            }
            if (heroes.Count > max)
                sb.Append(" +" + (heroes.Count - max) + " more");
            return sb.ToString();
        }

        // ── War Events ──

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            if (!IsAutoJournalEnabled) return;
            if (mapEvent == null) return;
            try
            {
                bool isBattle = mapEvent.IsSiegeAssault || mapEvent.IsSiegeOutside ||
                                mapEvent.IsFieldBattle || mapEvent.IsSallyOut;
                if (!isBattle) return;

                // Use pre-battle troop counts (cached at MapEventStarted) for accurate numbers
                int attackerCount, defenderCount;
                int key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(mapEvent);
                Tuple<int, int> cached;
                if (_preBattleTroopCounts.TryGetValue(key, out cached))
                {
                    attackerCount = cached.Item1;
                    defenderCount = cached.Item2;
                    _preBattleTroopCounts.Remove(key);
                    MCMSettings.DebugLog("AutoJournal: MapEventEnded using cached troops: " + attackerCount + " vs " + defenderCount);
                }
                else
                {
                    // Fallback: post-battle counts (may be inaccurate for losers)
                    attackerCount = TotalTroops(mapEvent.AttackerSide);
                    defenderCount = TotalTroops(mapEvent.DefenderSide);
                    MCMSettings.DebugLog("AutoJournal: MapEventEnded cache MISS, using post-battle: " + attackerCount + " vs " + defenderCount + " (key=" + key + ", cache size=" + _preBattleTroopCounts.Count + ")");
                }
                // Check if any side has a lord/noble leader (not just bandits)
                bool hasLordLeader = mapEvent.AttackerSide?.LeaderParty?.LeaderHero != null
                                  || mapEvent.DefenderSide?.LeaderParty?.LeaderHero != null;

                // Skip minor AI party encounters and bandit skirmishes.
                // Only log battles that involve a named hero or are significant in scale.
                bool isSmallFight = (attackerCount + defenderCount) < SmallFightTroopThreshold;
                if (isSmallFight && !hasLordLeader) return;

                // For AI-only battles (no player involvement), require higher troop threshold
                // to avoid flooding chronicle with minor AI skirmishes
                bool playerInvolved = false;
                try
                {
                    var mainHero = Hero.MainHero;
                    if (mainHero != null)
                    {
                        foreach (var party in mapEvent.AttackerSide.Parties.Union(mapEvent.DefenderSide.Parties))
                        {
                            if (party.Party?.LeaderHero?.StringId == mainHero.StringId)
                            { playerInvolved = true; break; }
                        }
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: OnMapEventEnded player involvement check failed: " + ex.ToString()); }
                bool isMinorAIBattle = !playerInvolved && (attackerCount + defenderCount) < MinorAiBattleTroopThreshold;
                if (isMinorAIBattle && !mapEvent.IsSiegeAssault) return;

                var winner = mapEvent.BattleState == BattleState.AttackerVictory
                    ? mapEvent.AttackerSide : mapEvent.DefenderSide;
                var loser = mapEvent.BattleState == BattleState.AttackerVictory
                    ? mapEvent.DefenderSide : mapEvent.AttackerSide;

                string winnerInfo = PartyInfo(winner);
                string loserInfo = PartyInfo(loser);

                // Location — use "at Settlement" for sieges, "near Settlement" for field battles
                string locationStr;
                string settlementName = mapEvent.MapEventSettlement?.Name?.ToString();
                string settlementId = mapEvent.MapEventSettlement?.StringId;
                if (settlementName != null)
                {
                    locationStr = " at " + "«s:" + (settlementId ?? "") + "»" + settlementName + "«/s»";
                }
                else
                {
                    string loc = GetNearestLocationString(mapEvent);
                    if (loc == "the field")
                        loc = GetNearestLocationString(
                            (object)mapEvent.AttackerSide?.LeaderParty?.MobileParty
                            ?? (object)mapEvent.DefenderSide?.LeaderParty?.MobileParty);
                    locationStr = " " + loc; // "near X" already has the preposition
                }

                // Calculate casualties (pre-battle - post-battle survivors)
                int loserPostBattle = TotalTroops(loser);
                int winnerPostBattle = TotalTroops(winner);
                int loserPreBattle = (mapEvent.BattleState == BattleState.AttackerVictory) ? defenderCount : attackerCount;
                int winnerPreBattle = (mapEvent.BattleState == BattleState.AttackerVictory) ? attackerCount : defenderCount;
                int enemyCasualties = Math.Max(0, loserPreBattle - loserPostBattle);
                int ownCasualties = Math.Max(0, winnerPreBattle - winnerPostBattle);

                // Collect captured and killed/wounded heroes
                var capturedHeroes = CollectCapturedHeroes(winner);
                CollectHeroCasualties(mapEvent, out var killedHeroes, out var woundedHeroes);

                // ── Build narrative battle prose ──

                // Describe battle outcome flavor
                string BattleVerdict(int ownLost, int enemyLost, int ownTotal, int enemyTotal)
                {
                    float ownLossRatio = ownTotal > 0 ? (float)ownLost / ownTotal : 0;
                    float enemyLossRatio = enemyTotal > 0 ? (float)enemyLost / enemyTotal : 0;
                    if (ownLost == 0 && enemyLost > 0) return "a flawless victory";
                    if (ownLossRatio < 0.1f && enemyLossRatio > 0.5f) return "a decisive triumph";
                    if (ownLossRatio < 0.25f) return "a swift victory";
                    if (ownLossRatio > 0.5f) return "a pyrrhic victory";
                    if (ownLossRatio > 0.75f) return "a hard-fought victory at great cost";
                    return "a victory";
                }

                string DefeatVerdict(int ownLost, int enemyLost, int ownTotal, int enemyTotal)
                {
                    float ownLossRatio = ownTotal > 0 ? (float)ownLost / ownTotal : 0;
                    float enemyLossRatio = enemyTotal > 0 ? (float)enemyLost / enemyTotal : 0;
                    if (ownLossRatio > 0.9f) return "a devastating rout";
                    if (ownLossRatio > 0.5f && enemyLossRatio < 0.1f) return "a crushing defeat";
                    if (ownLossRatio > 0.5f) return "a bitter defeat";
                    if (enemyLossRatio > 0.3f) return "a hard-fought defeat";
                    return "a defeat";
                }

                // Build hero detail prose (captured/killed/wounded)
                string BuildHeroDetailProse(List<Hero> captured, List<Hero> killed, List<Hero> wounded, string heroId)
                {
                    var sb = new System.Text.StringBuilder();
                    var relevantCaptured = heroId != null ? captured.Where(h => h.StringId != heroId).ToList() : captured;
                    var relevantKilled = heroId != null ? killed.Where(h => h.StringId != heroId).ToList() : killed;
                    var relevantWounded = heroId != null ? wounded.Where(h => h.StringId != heroId).ToList() : wounded;

                    if (relevantCaptured.Count > 0)
                        sb.Append(" " + FormatHeroList(relevantCaptured) + (relevantCaptured.Count == 1 ? " was" : " were") + " taken prisoner.");
                    if (relevantKilled.Count > 0)
                        sb.Append(" " + FormatHeroList(relevantKilled) + (relevantKilled.Count == 1 ? " was" : " were") + " slain.");
                    if (relevantWounded.Count > 0)
                        sb.Append(" " + FormatHeroList(relevantWounded) + (relevantWounded.Count == 1 ? " was" : " were") + " wounded.");
                    return sb.ToString();
                }

                // Log for hero participants (deduplicate by hero StringId)
                var loggedHeroes = new HashSet<string>();
                foreach (var party in mapEvent.AttackerSide.Parties.Union(mapEvent.DefenderSide.Parties))
                {
                    var hero = party.Party?.LeaderHero;
                    if (hero == null) continue;
                    if (!loggedHeroes.Add(hero.StringId)) continue;

                    // Real spoils for THIS hero, captured by ChronicleSpoilsCollector's Harmony
                    // hooks while the engine was handing gold and loot out. Positive gold for the
                    // winner (PlunderedGold), negative for the loser (GoldLost).
                    ChronicleSpoils heroSpoils = ChronicleSpoilsCollector.PeekForHero(mapEvent, hero.StringId);
                    string spoilsKey;

                    bool isWinner = winner.Parties.Any(p => p.Party?.LeaderHero == hero);
                    string heroDetailProse = BuildHeroDetailProse(capturedHeroes, killedHeroes, woundedHeroes, hero.StringId);
                    // Hidden metadata tag for kill stat accumulation (stripped from display by UI)
                    int slainForHero = isWinner ? enemyCasualties : ownCasualties;
                    string slainTag = slainForHero > 0 ? " [slain:" + slainForHero + "]" : "";

                    if (isWinner)
                    {
                        string verdict = BattleVerdict(ownCasualties, enemyCasualties, winnerPreBattle, loserPreBattle);
                        spoilsKey = AutoLog(hero.StringId, "[War] Victory" + locationStr + " — a force of " + winnerPreBattle
                            + " engaged " + loserInfo + " (" + loserPreBattle + "), resulting in " + verdict
                            + " with " + ownCasualties + (ownCasualties == 1 ? " casualty" : " casualties")
                            + " while " + enemyCasualties + " of the enemy " + (enemyCasualties == 1 ? "was" : "were") + " slain."
                            + heroDetailProse + slainTag);
                    }
                    else
                    {
                        string verdict = DefeatVerdict(enemyCasualties, ownCasualties, loserPreBattle, winnerPreBattle);
                        string oddsStr = loserPreBattle < winnerPreBattle
                            ? "outnumbered " + loserPreBattle + " to " + winnerPreBattle
                            : loserPreBattle + " strong against " + winnerPreBattle;
                        spoilsKey = AutoLog(hero.StringId, "[War] Defeated by " + winnerInfo + locationStr + " — " + oddsStr
                            + ", suffering " + verdict
                            + ". Lost " + enemyCasualties + (enemyCasualties == 1 ? " soldier" : " soldiers")
                            + " while inflicting " + ownCasualties + " " + (ownCasualties == 1 ? "casualty" : "casualties") + " on the enemy."
                            + heroDetailProse + slainTag);
                    }
                    RecordChronicleSpoils(spoilsKey, heroSpoils);
                }

                // Log for clans of participating heroes
                var loggedClans = new HashSet<string>();
                foreach (var party in mapEvent.AttackerSide.Parties.Union(mapEvent.DefenderSide.Parties))
                {
                    var hero = party.Party?.LeaderHero;
                    if (hero?.Clan == null) continue;
                    if (!loggedClans.Add(hero.Clan.StringId)) continue;

                    bool isWinnerClan = winner.Parties.Any(p => p.Party?.LeaderHero?.Clan == hero.Clan);
                    if (isWinnerClan)
                        AutoLog(hero.Clan.StringId, "[War] " + HeroInfo(hero) + " won a battle against " + loserInfo + locationStr
                            + " (" + winnerPreBattle + " vs " + loserPreBattle + ", " + enemyCasualties + " enemy slain)");
                    else
                        AutoLog(hero.Clan.StringId, "[War] " + HeroInfo(hero) + " was defeated by " + winnerInfo + locationStr
                            + " (" + loserPreBattle + " vs " + winnerPreBattle + ", lost " + enemyCasualties + ")");
                }

                // Log for settlement if siege
                if (mapEvent.MapEventSettlement != null && (mapEvent.IsSiegeAssault || mapEvent.IsSiegeOutside))
                {
                    string attackerInfo = PartyInfo(mapEvent.AttackerSide);
                    bool fell = mapEvent.BattleState == BattleState.AttackerVictory;
                    int attackerLosses = fell ? ownCasualties : enemyCasualties;
                    int defenderLosses = fell ? enemyCasualties : ownCasualties;
                    string siegeHeroDetail = BuildHeroDetailProse(capturedHeroes, killedHeroes, woundedHeroes, null);

                    // Spoils of the assault as a whole = everything the winning side's leaders took.
                    var winnerHeroIds = new List<string>();
                    foreach (var wp in winner.Parties)
                    {
                        var wHero = wp.Party?.LeaderHero;
                        if (wHero != null) winnerHeroIds.Add(wHero.StringId);
                    }
                    ChronicleSpoils siegeSpoils = ChronicleSpoilsCollector.PeekAggregate(mapEvent, winnerHeroIds);
                    string siegeKey;

                    if (fell)
                    {
                        siegeKey = AutoLog(mapEvent.MapEventSettlement.StringId,
                            "[War] Besieged by " + attackerInfo + " — the walls were breached and the settlement fell."
                            + " Attackers (" + attackerCount + ") lost " + attackerLosses
                            + ", defenders (" + defenderCount + ") lost " + defenderLosses + "." + siegeHeroDetail);

                        // ChangeOwnerOfSettlementAction fires a moment later on its own event with
                        // no access to this MapEvent, so hand the numbers across explicitly.
                        ChronicleSpoilsCollector.StashSettlementSpoils(mapEvent.MapEventSettlement.StringId, siegeSpoils);
                    }
                    else
                    {
                        siegeKey = AutoLog(mapEvent.MapEventSettlement.StringId,
                            "[War] Besieged by " + attackerInfo + " — the garrison held firm and repelled the assault."
                            + " Attackers (" + attackerCount + ") lost " + attackerLosses
                            + ", defenders (" + defenderCount + ") lost " + defenderLosses + "." + siegeHeroDetail);
                    }
                    RecordChronicleSpoils(siegeKey, siegeSpoils);

                    // Log to kingdom
                    var kingdom = mapEvent.MapEventSettlement.OwnerClan?.Kingdom;
                    if (kingdom != null)
                    {
                        string sLink = "«s:" + mapEvent.MapEventSettlement.StringId + "»" + mapEvent.MapEventSettlement.Name + "«/s»";
                        if (fell)
                            AutoLog(kingdom.StringId, "[War] " + sLink + " fell to " + attackerInfo
                                + " after a siege. Attackers lost " + attackerLosses + ", defenders lost " + defenderLosses);
                        else
                            AutoLog(kingdom.StringId, "[War] " + sLink + " held against " + attackerInfo
                                + ". Attackers lost " + attackerLosses + ", defenders lost " + defenderLosses);
                    }
                }

                // Track which prisoners were freed by the winning side so we can
                // attribute the rescuer in OnHeroPrisonerReleased
                try
                {
                    var winnerLeader = winner?.LeaderParty?.LeaderHero;
                    if (winnerLeader != null)
                    {
                        // Prisoners held by the losing side's parties will be freed
                        foreach (var loserPartyInfo in loser.Parties)
                        {
                            var loserParty = loserPartyInfo.Party;
                            if (loserParty?.PrisonRoster == null) continue;
                            foreach (var troop in loserParty.PrisonRoster.GetTroopRoster())
                            {
                                if (troop.Character?.HeroObject != null)
                                {
                                    _battleRescuers[troop.Character.HeroObject.StringId] = winnerLeader;
                                }
                            }
                        }
                        // Prevent unbounded growth
                        if (_battleRescuers.Count > BattleRescuerCacheMaxSize)
                            _battleRescuers.Clear();
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("AutoJournal: rescuer tracking error: " + ex.ToString()); }
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal MapEventEnded error: " + ex.ToString()); }
        }

        private void OnMakePeace(IFaction faction1, IFaction faction2, MakePeaceAction.MakePeaceDetail detail)
        {
            if (faction1 == null || faction2 == null) return;
            // Log to the two factions (kingdom or clan) — no cross-ref to individual clans to avoid spam
            AutoLog(faction1.StringId, "[Politics] A peace accord was reached with " + faction2.Name + ". The war has ended.");
            AutoLog(faction2.StringId, "[Politics] A peace accord was reached with " + faction1.Name + ". The war has ended.");
            // If a clan declared peace, also log to its kingdom
            if (faction1 is Clan c1 && c1.Kingdom != null)
                AutoLog(c1.Kingdom.StringId, "[Politics] A peace accord was reached with " + faction2.Name + ". The war has ended.");
            if (faction2 is Clan c2 && c2.Kingdom != null)
                AutoLog(c2.Kingdom.StringId, "[Politics] A peace accord was reached with " + faction1.Name + ". The war has ended.");
        }

        private void OnWarDeclared(IFaction faction1, IFaction faction2, DeclareWarAction.DeclareWarDetail detail)
        {
            if (faction1 == null || faction2 == null) return;
            // Log to the two factions — no cross-ref to individual clans to avoid spam
            AutoLog(faction1.StringId, "[War] War has been declared against " + faction2.Name + ". Hostilities have begun.");
            AutoLog(faction2.StringId, "[War] " + faction1.Name + " has declared war. Hostilities have begun.");
            // If a clan declared war, also log to its kingdom
            if (faction1 is Clan c1 && c1.Kingdom != null)
                AutoLog(c1.Kingdom.StringId, "[War] War has been declared against " + faction2.Name + ". Hostilities have begun.");
            if (faction2 is Clan c2 && c2.Kingdom != null)
                AutoLog(c2.Kingdom.StringId, "[War] " + faction1.Name + " has declared war. Hostilities have begun.");
        }

        // ── Death & Capture Events ──

        private void OnHeroKilled(Hero victim, Hero killer, KillCharacterAction.KillCharacterActionDetail detail, bool showNotification)
        {
            if (victim == null) return;
            string cause = detail.ToString();
            bool isClanLeader = victim.Clan?.Leader == victim;
            string leaderTag = isClanLeader ? ", leader of " + "«c:" + victim.Clan.StringId + "»" + victim.Clan.Name + "«/c»" : "";

            if (killer != null)
            {
                AutoLog(victim.StringId, "[War] Fell in battle — slain by " + HeroInfo(killer) + " (" + cause + ")");
                AutoLog(killer.StringId, "[War] Slew " + HeroInfo(victim) + leaderTag + " in combat (" + cause + ")");

                // Auto-suggest relation note if player is involved
                SuggestRelationNote(killer, victim,
                    "Killed " + victim.Name + leaderTag + " (" + cause + ")");
                SuggestRelationNote(victim, killer,
                    "Killed by " + killer.Name + " (" + cause + ")");
            }
            else
            {
                AutoLog(victim.StringId, "[Family] Passed away (" + cause + ")");
            }
            // Log to clan
            string deathTag = killer != null ? "[War]" : "[Family]";
            if (victim.Clan != null)
                AutoLog(victim.Clan.StringId, deathTag + " " + "«h:" + victim.StringId + "»" + victim.Name + "«/h»" + leaderTag + " has fallen (" + cause + ")");
            // Log to kingdom
            if (victim.Clan?.Kingdom != null)
                AutoLog(victim.Clan.Kingdom.StringId, deathTag + " " + "«h:" + victim.StringId + "»" + victim.Name + "«/h»" + " of " + "«c:" + victim.Clan.StringId + "»" + victim.Clan.Name + "«/c»" + leaderTag + " has fallen (" + cause + ")");
        }

        private void OnHeroPrisonerTaken(PartyBase party, Hero prisoner)
        {
            if (prisoner == null) return;
            string captorInfo = party?.LeaderHero != null ? HeroInfo(party.LeaderHero)
                              : party?.Name?.ToString() ?? "Unknown";
            string prisonerInfo = HeroInfo(prisoner);

            // Determine capture location
            string locationStr = "";
            string locationPlain = "";
            try
            {
                var mobileParty = party?.MobileParty;
                if (mobileParty?.CurrentSettlement != null)
                {
                    locationStr = " at " + "«s:" + mobileParty.CurrentSettlement.StringId + "»" + mobileParty.CurrentSettlement.Name + "«/s»";
                    locationPlain = " at " + mobileParty.CurrentSettlement.Name;
                }
                else if (mobileParty != null)
                {
                    locationStr = " " + GetNearestLocationString(mobileParty);
                    locationPlain = locationStr;
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: OnHeroPrisonerTaken location lookup failed: " + ex.ToString()); }

            AutoLog(prisoner.StringId, "[War] Taken prisoner by " + captorInfo + locationStr + ". Now held in captivity.");
            if (party?.LeaderHero != null)
                AutoLog(party.LeaderHero.StringId, "[War] Took " + prisonerInfo + " as prisoner" + locationStr);
            // Log to clan
            if (prisoner.Clan != null)
                AutoLog(prisoner.Clan.StringId, "[War] " + "«h:" + prisoner.StringId + "»" + prisoner.Name + "«/h»" + " was taken prisoner by " + captorInfo + locationStr);

            // Auto-suggest relation note if player is involved
            if (party?.LeaderHero != null)
            {
                SuggestRelationNote(party.LeaderHero, prisoner,
                    "Captured " + prisoner.Name + locationPlain);
                SuggestRelationNote(prisoner, party.LeaderHero,
                    "Captured by " + party.LeaderHero.Name + locationPlain);
            }
        }

        /// <summary>
        /// Offers a quick popup to add a relation note when the player's hero is involved
        /// in a significant event with another hero (e.g., kill, capture).
        /// Only fires if viewingHero is the player's main hero.
        /// </summary>
        private void SuggestRelationNote(Hero viewingHero, Hero targetHero, string suggestedNote)
        {
            try
            {
                if (viewingHero == null || targetHero == null) return;
                if (Hero.MainHero == null || viewingHero.StringId != Hero.MainHero.StringId) return;

                // Check if journal/relation notes features are enabled
                var settings = MCMSettings.Instance;
                if (settings != null && !settings.EnableJournal) return;
                if (!(MCMSettings.Instance?.EnableRelationNotes ?? true)) return;

                string viewId = viewingHero.StringId;
                string targetId = targetHero.StringId;
                string targetName = targetHero.Name?.ToString() ?? "Unknown";

                string dateStr;
                try { dateStr = GetCurrentGameDateString(); }
                catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: SuggestRelationNote date lookup failed: " + ex.ToString()); dateStr = ""; }

                string fullSuggestion = string.IsNullOrEmpty(dateStr)
                    ? suggestedNote
                    : suggestedNote;

                InformationManager.ShowInquiry(
                    new InquiryData(
                        Localization.L("relation_note_suggest_title"),
                        Localization.L("relation_note_suggest_message", targetName, fullSuggestion),
                        true, true,
                        Localization.L("relation_note_suggest_accept"),
                        Localization.L("edit_cancel"),
                        () =>
                        {
                            // Add the relation note
                            SetRelationNote(viewId, targetId, fullSuggestion);
                            EditableEncyclopediaAPI.RaiseRelationNoteChanged(viewId, targetId, fullSuggestion);

                            bool showConfirm = true;
                            try
                            {
                                var s = MCMSettings.Instance;
                                if (s != null) showConfirm = s.ShowConfirmationMessages;
                            }
                            catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: SuggestRelationNote settings read failed: " + ex.ToString()); }

                            if (showConfirm)
                            {
                                InformationManager.DisplayMessage(new InformationMessage(
                                    Localization.L("relation_note_suggest_added", targetName),
                                    new Color(0.2f, 0.8f, 0.2f)));
                            }
                        },
                        () => { /* cancelled */ }),
                    false, false);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("SuggestRelationNote error: " + ex.ToString());
            }
        }

        private void OnHeroPrisonerReleased(Hero prisoner, PartyBase party, IFaction faction, EndCaptivityDetail detail, bool isFleeOrKill)
        {
            if (prisoner == null) return;
            string captorName = party?.LeaderHero?.Name?.ToString() ?? party?.Name?.ToString() ?? "";
            string factionName = faction?.Name?.ToString() ?? party?.LeaderHero?.Clan?.Kingdom?.Name?.ToString() ?? "";
            string captorInfo = "";
            if (!string.IsNullOrEmpty(captorName))
            {
                string captorId = party?.LeaderHero?.StringId ?? "";
                captorInfo = " from " + "«h:" + captorId + "»" + captorName + "«/h»";
                if (!string.IsNullOrEmpty(factionName) && factionName != captorName) captorInfo += " of " + factionName;
            }
            else if (!string.IsNullOrEmpty(factionName))
            {
                captorInfo = " from " + factionName;
            }
            string locationStr = "";
            // Try to get location from the captor party, or the prisoner's own party
            object posSource = (object)party?.MobileParty ?? (object)prisoner?.PartyBelongedTo;
            if (posSource != null)
                locationStr = " " + GetNearestLocationString(posSource);

            // Friendly release reason text
            string reasonStr;
            string verb;
            string detailStr = detail.ToString();
            if (detailStr.IndexOf("Escape", StringComparison.OrdinalIgnoreCase) >= 0)
            { verb = "Escaped"; reasonStr = "escaped captivity"; }
            else if (detailStr.IndexOf("Ransom", StringComparison.OrdinalIgnoreCase) >= 0)
            { verb = "Ransomed"; reasonStr = "ransomed"; }
            else if (detailStr.IndexOf("Released", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     detailStr.IndexOf("Choice", StringComparison.OrdinalIgnoreCase) >= 0)
            { verb = "Released"; reasonStr = "set free"; }
            else
            { verb = "Released"; reasonStr = detailStr; }

            // Check if we tracked a rescuer from a recent battle
            string rescuerStr = "";
            Hero rescuer = null;
            _battleRescuers.TryGetValue(prisoner.StringId, out rescuer);
            _battleRescuers.Remove(prisoner.StringId);

            // Fallback: check if the prisoner just joined a party (rescuer's party)
            if (rescuer == null)
            {
                var joinedPartyLeader = prisoner.PartyBelongedTo?.LeaderHero;
                if (joinedPartyLeader != null && joinedPartyLeader != prisoner)
                    rescuer = joinedPartyLeader;
            }

            if (rescuer != null)
            {
                rescuerStr = " (rescued by " + "«h:" + rescuer.StringId + "»" + rescuer.Name + "«/h»" + ")";
                reasonStr = null; // Don't show generic reason when we have rescuer
            }

            string reasonPart = reasonStr != null ? " (" + reasonStr + ")" : "";
            AutoLog(prisoner.StringId, "[War] " + verb + captorInfo + locationStr + ". Freedom at last." + rescuerStr + reasonPart);
            if (party?.LeaderHero != null)
                AutoLog(party.LeaderHero.StringId, "[War] Released prisoner " + "«h:" + prisoner.StringId + "»" + prisoner.Name + "«/h»" + locationStr + rescuerStr + reasonPart);

            // Also log to the rescuer's timeline
            if (rescuer != null && rescuer != party?.LeaderHero)
                AutoLog(rescuer.StringId, "[War] Liberated " + "«h:" + prisoner.StringId + "»" + prisoner.Name + "«/h»" + " from captivity" + captorInfo + locationStr);
        }

        // ── Politics Events ──

        private void OnClanChangedKingdom(Clan clan, Kingdom oldKingdom, Kingdom newKingdom, ChangeKingdomAction.ChangeKingdomActionDetail detail, bool showNotification)
        {
            if (clan == null) return;
            string reason = detail.ToString();
            if (oldKingdom != null && newKingdom != null)
            {
                AutoLog(clan.StringId, "[Politics] Renounced allegiance to " + "«k:" + oldKingdom.StringId + "»" + oldKingdom.Name + "«/k»" + " and pledged loyalty to " + "«k:" + newKingdom.StringId + "»" + newKingdom.Name + "«/k»" + " (" + reason + ")");
                AutoLog(oldKingdom.StringId, "[Politics] " + "«c:" + clan.StringId + "»" + clan.Name + "«/c»" + " has betrayed the realm and defected to " + "«k:" + newKingdom.StringId + "»" + newKingdom.Name + "«/k»");
                AutoLog(newKingdom.StringId, "[Politics] " + "«c:" + clan.StringId + "»" + clan.Name + "«/c»" + " has sworn fealty, joining from " + "«k:" + oldKingdom.StringId + "»" + oldKingdom.Name + "«/k»");
            }
            else if (newKingdom != null)
            {
                AutoLog(clan.StringId, "[Politics] Pledged allegiance to " + "«k:" + newKingdom.StringId + "»" + newKingdom.Name + "«/k»" + " (" + reason + ")");
                AutoLog(newKingdom.StringId, "[Politics] " + "«c:" + clan.StringId + "»" + clan.Name + "«/c»" + " has sworn fealty to the realm");
            }
            else if (oldKingdom != null)
            {
                AutoLog(clan.StringId, "[Politics] Renounced allegiance to " + "«k:" + oldKingdom.StringId + "»" + oldKingdom.Name + "«/k»" + " and departed (" + reason + ")");
                AutoLog(oldKingdom.StringId, "[Politics] " + "«c:" + clan.StringId + "»" + clan.Name + "«/c»" + " has abandoned the realm and departed");
            }
        }

        private void OnSettlementOwnerChanged(Settlement settlement, bool openToClaim, Hero newOwner, Hero oldOwner, Hero capturerHero, ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail detail)
        {
            if (settlement == null) return;
            string newInfo = newOwner != null ? HeroInfo(newOwner) : "Unknown";
            string oldInfo = oldOwner != null ? HeroInfo(oldOwner) : "Unknown";
            string capturer = capturerHero != null ? HeroInfo(capturerHero) : "";
            string sName = "«s:" + settlement.StringId + "»" + (settlement.Name?.ToString() ?? "Unknown") + "«/s»";

            // Build context-rich description based on how ownership changed
            string settlementEntry;
            string tag = "[Politics]";
            switch (detail)
            {
                case ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.BySiege:
                    tag = "[War]";
                    settlementEntry = tag + " The settlement has fallen to " + (string.IsNullOrEmpty(capturer) ? newInfo : capturer) + ". The banners of " + oldInfo + " are torn down and new colors are raised.";
                    break;
                case ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.ByKingDecision:
                    settlementEntry = tag + " By royal decree, the settlement has been granted to " + newInfo + ". A new lord takes the seat of power.";
                    break;
                case ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.ByBarter:
                    settlementEntry = tag + " The settlement was traded from " + oldInfo + " to " + newInfo + " through negotiation.";
                    break;
                case ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.ByLeaveFaction:
                    settlementEntry = tag + " With " + oldInfo + " departing the realm, the settlement passes to " + newInfo + ".";
                    break;
                default:
                    settlementEntry = tag + " Ownership has changed hands — from " + oldInfo + " to " + newInfo + ".";
                    break;
            }

            // Only a change by force has spoils. The assault's MapEvent already ended (and its
            // accumulator is unreachable from here), so OnMapEventEnded stashed the aggregate
            // under the settlement id — consume it once, for the entries this handler writes.
            bool byForce = detail == ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.BySiege;
            ChronicleSpoils conquestSpoils = byForce
                ? ChronicleSpoilsCollector.TakeSettlementSpoils(settlement.StringId)
                : new ChronicleSpoils();

            string settlementKey = AutoLog(settlement.StringId, settlementEntry);
            if (byForce) RecordChronicleSpoils(settlementKey, conquestSpoils);

            // Log to involved heroes
            if (newOwner != null)
            {
                switch (detail)
                {
                    case ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.BySiege:
                        RecordChronicleSpoils(
                            AutoLog(newOwner.StringId, "[War] Seized " + sName + " by force of arms, wresting it from " + oldInfo),
                            conquestSpoils);
                        break;
                    case ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.ByKingDecision:
                        AutoLog(newOwner.StringId, "[Politics] Was granted lordship of " + sName + " by royal decree");
                        break;
                    default:
                        AutoLog(newOwner.StringId, tag + " Received lordship of " + sName);
                        break;
                }
            }
            if (oldOwner != null)
            {
                switch (detail)
                {
                    case ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.BySiege:
                        AutoLog(oldOwner.StringId, "[War] " + sName + " was lost — taken by " + (string.IsNullOrEmpty(capturer) ? newInfo : capturer) + " after a siege");
                        break;
                    default:
                        AutoLog(oldOwner.StringId, tag + " Relinquished lordship of " + sName);
                        break;
                }
            }
            // Log capturer if different from new owner
            if (capturerHero != null && capturerHero != newOwner)
            {
                string capturerKey = AutoLog(capturerHero.StringId, "[War] Conquered " + sName + " in battle — lordship awarded to " + newInfo);
                if (byForce) RecordChronicleSpoils(capturerKey, conquestSpoils);
            }

            // Log to kingdoms
            if (newOwner?.Clan?.Kingdom != null)
                AutoLog(newOwner.Clan.Kingdom.StringId, tag + " " + sName + " is now under the banner of " + newInfo);
            if (oldOwner?.Clan?.Kingdom != null && oldOwner.Clan.Kingdom != newOwner?.Clan?.Kingdom)
                AutoLog(oldOwner.Clan.Kingdom.StringId, tag + " The realm has lost " + sName + " to " + newInfo);
        }

        private void OnClanDestroyed(Clan clan)
        {
            if (clan == null) return;
            try
            {
                AutoLog(clan.StringId, "[Politics] The clan has been dissolved. Its name fades from the annals of history.");
                if (clan.Kingdom != null)
                    AutoLog(clan.Kingdom.StringId, "[Politics] " + "«c:" + clan.StringId + "»" + clan.Name + "«/c»" + " has been dissolved — their banner flies no more");
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal ClanDestroyed error: " + ex.ToString()); }
        }

        // ── DailyTick polling for events with no hookable listener ──
        // Detects: ruler changes, kingdom destruction, coming of age, marriages, pregnancies
        // Rebellion detection not possible via polling (no flag to check), but settlement owner changes cover revolts.

        private readonly Dictionary<string, string> _lastKnownRulers = new Dictionary<string, string>();
        private readonly HashSet<string> _knownEliminatedKingdoms = new HashSet<string>();
        private readonly HashSet<string> _knownMarriages = new HashSet<string>();
        private readonly HashSet<string> _knownWars = new HashSet<string>();
        private bool _warStateInitialized = false;

        private void OnDailyTickAutoJournal()
        {
            if (!IsAutoJournalEnabled) return;
            // Mark campaign as fully loaded after first DailyTick
            if (!_campaignFullyLoaded)
            {
                _campaignFullyLoaded = true;
                MCMSettings.DebugLog("AutoJournal: campaign fully loaded — initial-load gate lifted.");
            }
            try
            {
                // Detect ruler changes
                foreach (var kingdom in Kingdom.All)
                {
                    if (kingdom == null || kingdom.Leader == null) continue;
                    string kid = kingdom.StringId;
                    string currentRulerId = kingdom.Leader.StringId;

                    if (_lastKnownRulers.TryGetValue(kid, out var prevRulerId))
                    {
                        if (prevRulerId != currentRulerId)
                        {
                            string newName = kingdom.Leader.Name?.ToString() ?? "Unknown";
                            AutoLog(kid, "[Politics] " + "«h:" + currentRulerId + "»" + newName + "«/h»" + " has ascended to the throne of " + "«k:" + kid + "»" + kingdom.Name + "«/k»" + ". A new era begins.");
                            AutoLog(currentRulerId, "[Politics] Ascended to the throne as ruler of " + "«k:" + kid + "»" + kingdom.Name + "«/k»");
                        }
                    }
                    _lastKnownRulers[kid] = currentRulerId;

                    // Detect kingdom destruction
                    if (kingdom.IsEliminated && _knownEliminatedKingdoms.Add(kid))
                    {
                        AutoLog(kid, "[Politics] The kingdom has crumbled — all lands lost, its legacy reduced to ashes.");
                        foreach (var k in Kingdom.All)
                        {
                            if (k != null && k != kingdom && !k.IsEliminated)
                                AutoLog(k.StringId, "[Politics] The " + "«k:" + kid + "»" + kingdom.Name + "«/k»" + " has been destroyed. Its banners fly no more.");
                        }
                    }
                }

                // Detect active wars (catches pre-existing wars from save load + new wars)
                try
                {
                    var currentWars = new HashSet<string>();
                    var kingdoms = Kingdom.All;
                    if (kingdoms != null)
                    {
                        foreach (var k1 in kingdoms)
                        {
                            if (k1 == null || k1.IsEliminated) continue;
                            foreach (var k2 in kingdoms)
                            {
                                if (k2 == null || k2 == k1 || k2.IsEliminated) continue;
                                if (k1.IsAtWarWith(k2))
                                {
                                    // Use sorted key to avoid duplicates (A|B == B|A)
                                    string a = k1.StringId, b = k2.StringId;
                                    string warKey = string.Compare(a, b) < 0 ? a + "|" + b : b + "|" + a;
                                    currentWars.Add(warKey);

                                    if (_knownWars.Add(warKey) && _warStateInitialized)
                                    {
                                        // New war detected — skip logging here, OnWarDeclared handles it
                                    }
                                    else if (!_warStateInitialized)
                                    {
                                        // Initial load — log existing wars once
                                        _knownWars.Add(warKey);
                                        AutoLog(k1.StringId, "[War] The realm stands at war with " + "«k:" + k2.StringId + "»" + k2.Name + "«/k»");
                                    }
                                }
                            }
                        }

                        // Detect ended wars (peace)
                        if (_warStateInitialized)
                        {
                            var endedWars = new List<string>();
                            foreach (var warKey in _knownWars)
                                if (!currentWars.Contains(warKey))
                                    endedWars.Add(warKey);
                            foreach (var warKey in endedWars)
                            {
                                _knownWars.Remove(warKey);
                                var parts = warKey.Split('|');
                                if (parts.Length == 2)
                                {
                                    var ka = Kingdom.All?.FirstOrDefault(x => x.StringId == parts[0]);
                                    var kb = Kingdom.All?.FirstOrDefault(x => x.StringId == parts[1]);
                                    if (ka != null && kb != null)
                                    {
                                        // Peace detected — skip logging here, OnMakePeace handles it
                                    }
                                }
                            }
                        }

                        _warStateInitialized = true;
                    }
                }
                catch (Exception warEx)
                {
                    MCMSettings.DebugLog("AutoJournal war state tracking error: " + warEx.Message);
                }

                // Detect marriages (came of age is excluded — too noisy for Chronicle)
                foreach (var hero in Hero.AllAliveHeroes)
                {
                    if (hero == null) continue;
                    try
                    {
                        // Marriage: detect new spouse pairings
                        if (hero.Spouse != null)
                        {
                            string a = hero.StringId, b = hero.Spouse.StringId;
                            string key = string.Compare(a, b) < 0 ? a + "|" + b : b + "|" + a;
                            if (_knownMarriages.Add(key))
                            {
                                AutoLog(hero.StringId, "[Family] Wed " + HeroInfo(hero.Spouse) + " in a ceremony uniting their houses.");
                                AutoLog(hero.Spouse.StringId, "[Family] Wed " + HeroInfo(hero) + " in a ceremony uniting their houses.");
                                if (hero.Clan != null)
                                    AutoLog(hero.Clan.StringId, "[Family] A wedding was held — " + "«h:" + hero.StringId + "»" + hero.Name + "«/h»" + " and " + "«h:" + hero.Spouse.StringId + "»" + hero.Spouse.Name + "«/h»" + " are now joined in marriage.");
                                if (hero.Spouse.Clan != null && hero.Spouse.Clan != hero.Clan)
                                    AutoLog(hero.Spouse.Clan.StringId, "[Family] A wedding was held — " + "«h:" + hero.Spouse.StringId + "»" + hero.Spouse.Name + "«/h»" + " and " + "«h:" + hero.StringId + "»" + hero.Name + "«/h»" + " are now joined in marriage.");
                            }
                        }
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("EncyclopediaEditBehavior: DailyTick marriage detection failed: " + ex.ToString()); }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal DailyTick error: " + ex.ToString()); }
        }

        private void OnDailyTickAutoTags()
        {
            EvaluateAutoTags();
        }

        // ── War / Raid / Siege Events ──

        private void OnRaidCompleted(BattleSideEnum side, RaidEventComponent raidEvent)
        {
            try
            {
                var settlement = raidEvent?.MapEvent?.MapEventSettlement;
                if (settlement == null) return;
                var raiderHero = raidEvent?.MapEvent?.AttackerSide?.LeaderParty?.LeaderHero;
                string raiderInfo = raiderHero != null ? HeroInfo(raiderHero) : "Unknown raiders";

                // What the raid actually yielded, gathered item-by-item from the engine's own
                // RaidEventComponent.LootItemInRaid calls (plus any plundered gold from the
                // militia fight). RaidCompleted is dispatched from OnBeforeFinalize, so the
                // MapEvent — and therefore the accumulator — is still alive here.
                ChronicleSpoils raidSpoils = ChronicleSpoilsCollector.PeekForHero(
                    raidEvent != null ? raidEvent.MapEvent : null,
                    raiderHero != null ? raiderHero.StringId : null);

                string villageKey = AutoLog(settlement.StringId, "[Crime] The village was pillaged and burned by " + raiderInfo + ". Stores and livestock were seized.");
                RecordChronicleSpoils(villageKey, raidSpoils);
                if (raiderHero != null)
                {
                    string raiderKey = AutoLog(raiderHero.StringId, "[Crime] Raided and plundered the village of " + "«s:" + settlement.StringId + "»" + settlement.Name + "«/s»");
                    RecordChronicleSpoils(raiderKey, raidSpoils);
                }
                // Log to kingdom that owns the village
                if (settlement.OwnerClan?.Kingdom != null)
                    AutoLog(settlement.OwnerClan.Kingdom.StringId, "[Crime] " + "«s:" + settlement.StringId + "»" + settlement.Name + "«/s»" + " was raided and plundered by " + raiderInfo);
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal Raid error: " + ex.ToString()); }
        }

        private void OnSiegeStarted(SiegeEvent siegeEvent)
        {
            try
            {
                var settlement = siegeEvent?.BesiegedSettlement;
                var besieger = siegeEvent?.BesiegerCamp?.LeaderParty?.LeaderHero;
                if (settlement == null) return;
                string besiegerInfo = besieger != null ? HeroInfo(besieger) : "Unknown army";

                AutoLog(settlement.StringId, "[War] " + besiegerInfo + " has laid siege to the settlement. Siege camps are being erected.");
                if (besieger != null)
                    AutoLog(besieger.StringId, "[War] Laid siege to " + "«s:" + settlement.StringId + "»" + settlement.Name + "«/s»" + " — siege camps erected.");
                // Log to defending kingdom
                if (settlement.OwnerClan?.Kingdom != null)
                    AutoLog(settlement.OwnerClan.Kingdom.StringId, "[War] " + "«s:" + settlement.StringId + "»" + settlement.Name + "«/s»" + " is under siege by " + besiegerInfo);
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal Siege error: " + ex.ToString()); }
        }

        // ── Family Events ──

        private void OnTournamentFinished(CharacterObject winner, MBReadOnlyList<CharacterObject> participants, Town town, ItemObject prize)
        {
            if (winner == null || town == null) return;
            if (winner.HeroObject != null)
            {
                string prizeStr = prize != null ? " (prize: " + prize.Name + ")" : "";
                AutoLog(winner.HeroObject.StringId, "[Family] Emerged victorious in the tournament at " + "«s:" + (town?.Settlement?.StringId ?? "") + "»" + town?.Name + "«/s»" + prizeStr);
                AutoLog(town?.Settlement?.StringId, "[Family] " + "«h:" + winner.HeroObject.StringId + "»" + winner.HeroObject.Name + "«/h»" + " won the tournament, besting all challengers" + prizeStr);
            }
        }

        private void OnHeroCreated(Hero hero, bool isBornNaturally)
        {
            if (hero == null || !isBornNaturally) return;
            if (!_campaignFullyLoaded) return; // Skip initial-load hero creation
            string parentInfo = "";
            if (hero.Father != null && hero.Mother != null)
                parentInfo = " to " + "«h:" + hero.Father.StringId + "»" + hero.Father.Name + "«/h»" + " and " + "«h:" + hero.Mother.StringId + "»" + hero.Mother.Name + "«/h»";
            else if (hero.Father != null)
                parentInfo = " to " + "«h:" + hero.Father.StringId + "»" + hero.Father.Name + "«/h»";
            else if (hero.Mother != null)
                parentInfo = " to " + "«h:" + hero.Mother.StringId + "»" + hero.Mother.Name + "«/h»";

            AutoLog(hero.StringId, "[Family] Brought into the world" + parentInfo + ". A new life begins.");
            if (hero.Clan != null)
                AutoLog(hero.Clan.StringId, "[Family] A child, " + "«h:" + hero.StringId + "»" + hero.Name + "«/h»" + ", was born into the clan. The bloodline continues.");
            if (hero.Father != null)
                AutoLog(hero.Father.StringId, "[Family] Was blessed with a child — " + "«h:" + hero.StringId + "»" + hero.Name + "«/h»");
            if (hero.Mother != null)
                AutoLog(hero.Mother.StringId, "[Family] Gave birth to " + "«h:" + hero.StringId + "»" + hero.Name + "«/h»");
        }

        private void OnMarriageOffered(Hero suitor, Hero maiden)
        {
            if (!IsAutoJournalEnabled) return;
            if (suitor == null || maiden == null) return;
            AutoLog(suitor.StringId, "[Family] Sought the hand of " + HeroInfo(maiden) + " in marriage.");
            AutoLog(maiden.StringId, "[Family] Received a proposal of marriage from " + HeroInfo(suitor) + ".");
            if (suitor.Clan != null)
                AutoLog(suitor.Clan.StringId, "[Family] " + "«h:" + suitor.StringId + "»" + suitor.Name + "«/h»" + " has sought the hand of " + "«h:" + maiden.StringId + "»" + maiden.Name + "«/h»" + " in marriage.");
            if (maiden.Clan != null && maiden.Clan != suitor.Clan)
                AutoLog(maiden.Clan.StringId, "[Family] " + "«h:" + suitor.StringId + "»" + suitor.Name + "«/h»" + " has sought the hand of " + "«h:" + maiden.StringId + "»" + maiden.Name + "«/h»" + " in marriage.");
        }

        private void OnChildConceived(Hero mother)
        {
            if (!IsAutoJournalEnabled) return;
            if (mother == null) return;
            string fatherInfo = mother.Spouse != null ? " with " + HeroInfo(mother.Spouse) : "";
            AutoLog(mother.StringId, "[Family] With child — an heir is expected" + fatherInfo + ".");
            if (mother.Spouse != null)
                AutoLog(mother.Spouse.StringId, "[Family] An heir is expected with " + HeroInfo(mother) + ".");
            if (mother.Clan != null)
            {
                string entry = "[Family] " + "«h:" + mother.StringId + "»" + mother.Name + "«/h»" + " is with child";
                if (mother.Spouse != null)
                    entry += ", sired by " + "«h:" + mother.Spouse.StringId + "»" + mother.Spouse.Name + "«/h»";
                entry += ". The clan awaits a new heir.";
                AutoLog(mother.Clan.StringId, entry);
            }
        }

        // ── Army Events ──

        private void OnArmyCreated(Army army)
        {
            if (army == null) return;
            try
            {
                var leader = army.LeaderParty?.LeaderHero;
                if (leader == null) return;
                string armyName = army.Name?.ToString() ?? "an army";
                int partyCount = army.Parties?.Count ?? 0;
                string locationStr = "";
                if (leader.PartyBelongedTo?.CurrentSettlement != null)
                    locationStr = " at " + "«s:" + leader.PartyBelongedTo.CurrentSettlement.StringId + "»" + leader.PartyBelongedTo.CurrentSettlement.Name + "«/s»";
                else
                    locationStr = " " + GetNearestLocationString(leader.PartyBelongedTo);

                AutoLog(leader.StringId, "[War] Rallied the lords and mustered " + armyName + " — " + partyCount + " parties answer the call" + locationStr + ".");
                if (leader.Clan != null)
                    AutoLog(leader.Clan.StringId, "[War] " + HeroInfo(leader) + " has raised " + armyName + ", rallying " + partyCount + " parties to the banner.");
                if (leader.Clan?.Kingdom != null)
                    AutoLog(leader.Clan.Kingdom.StringId, "[War] " + HeroInfo(leader) + " has mustered " + armyName + " — " + partyCount + " parties march together.");
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal ArmyCreated error: " + ex.ToString()); }
        }

        private void OnArmyDispersed(Army army, Army.ArmyDispersionReason reason, bool isPlayersArmy)
        {
            if (army == null) return;
            try
            {
                var leader = army.LeaderParty?.LeaderHero;
                if (leader == null) return;
                string armyName = army.Name?.ToString() ?? "the army";
                string reasonStr;
                switch (reason)
                {
                    case Army.ArmyDispersionReason.DismissalRequestedWithInfluence: reasonStr = "dismissed"; break;
                    case Army.ArmyDispersionReason.FoodProblem: reasonStr = "food shortage"; break;
                    case Army.ArmyDispersionReason.CohesionDepleted: reasonStr = "cohesion depleted"; break;
                    default: reasonStr = reason.ToString().Replace("_", " ").ToLower(); break;
                }

                AutoLog(leader.StringId, "[War] " + armyName + " has been disbanded — " + reasonStr + ". The lords return to their holdings.");
                if (leader.Clan?.Kingdom != null)
                    AutoLog(leader.Clan.Kingdom.StringId, "[War] " + armyName + ", led by " + HeroInfo(leader) + ", has been disbanded — " + reasonStr + ".");
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal ArmyDispersed error: " + ex.ToString()); }
        }

        // ── Hero Lifecycle Events ──

        private void OnHeroComesOfAge(Hero hero)
        {
            if (hero == null) return;
            try
            {
                AutoLog(hero.StringId, "[Family] Has come of age — no longer a child, but ready to forge their own destiny.");
                if (hero.Clan != null)
                    AutoLog(hero.Clan.StringId, "[Family] " + "«h:" + hero.StringId + "»" + hero.Name + "«/h»" + " has come of age and stands ready to serve the clan.");
                if (hero.Father != null)
                    AutoLog(hero.Father.StringId, "[Family] " + "«h:" + hero.StringId + "»" + hero.Name + "«/h»" + " has come of age. A proud moment.");
                if (hero.Mother != null)
                    AutoLog(hero.Mother.StringId, "[Family] " + "«h:" + hero.StringId + "»" + hero.Name + "«/h»" + " has come of age. A proud moment.");
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal HeroComesOfAge error: " + ex.ToString()); }
        }

        private void OnHeroWounded(Hero hero)
        {
            if (hero == null) return;
            try
            {
                AutoLog(hero.StringId, "[War] Suffered wounds in combat. Recovering from injuries.");
                if (hero.Clan != null)
                    AutoLog(hero.Clan.StringId, "[War] " + "«h:" + hero.StringId + "»" + hero.Name + "«/h»" + " was wounded in battle and is recovering.");
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal HeroWounded error: " + ex.ToString()); }
        }

        private const int LevelMilestoneInterval = 5;

        private void OnHeroLevelledUp(Hero hero, bool shouldNotify)
        {
            if (hero == null) return;
            if (!_campaignFullyLoaded) return; // Skip initial-load level assignments
            try
            {
                // Only log for named heroes at milestone levels (5, 10, 15, 20, ...)
                if (!hero.IsLord && !hero.IsPlayerCompanion && hero != Hero.MainHero) return;
                if (hero.Level % LevelMilestoneInterval != 0) return;
                AutoLog(hero.StringId, "[Other] Through experience and hardship, has grown stronger — now level " + hero.Level + ".");
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal HeroLevelledUp error: " + ex.ToString()); }
        }

        // Track skill milestones (every 50 points) to avoid flooding
        private readonly Dictionary<string, Dictionary<string, int>> _lastKnownSkillTiers = new Dictionary<string, Dictionary<string, int>>();
        private const int SkillMilestoneTier = 50;

        private void OnHeroGainedSkill(Hero hero, SkillObject skill, int change, bool shouldNotify)
        {
            if (hero == null || skill == null) return;
            if (!_campaignFullyLoaded) return; // Skip initial-load skill assignments
            try
            {
                // Only log for player hero and companions (lords would spam too much)
                if (!hero.IsPlayerCompanion && hero != Hero.MainHero) return;

                int currentLevel = hero.GetSkillValue(skill);
                int currentTier = currentLevel / SkillMilestoneTier;

                // Check if this crosses a milestone boundary
                if (!_lastKnownSkillTiers.TryGetValue(hero.StringId, out var skills))
                {
                    skills = new Dictionary<string, int>();
                    _lastKnownSkillTiers[hero.StringId] = skills;
                }

                int previousTier;
                if (!skills.TryGetValue(skill.StringId, out previousTier))
                {
                    // First time seeing this skill — initialize without logging
                    skills[skill.StringId] = currentTier;
                    return;
                }

                if (currentTier > previousTier)
                {
                    string skillName = skill.Name?.ToString() ?? skill.StringId;
                    AutoLog(hero.StringId, "[Other] Honed their mastery of " + skillName + " — skill now at " + currentLevel + ".");
                }
                skills[skill.StringId] = currentTier;

                // Prevent unbounded growth
                if (_lastKnownSkillTiers.Count > 500)
                    _lastKnownSkillTiers.Clear();
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal HeroGainedSkill error: " + ex.ToString()); }
        }

        // ── Companion / Clan Membership Events ──

        private readonly HashSet<string> _recentClanChanges = new HashSet<string>();

        private void OnHeroChangedClan(Hero hero, Clan oldClan)
        {
            if (hero == null) return;
            if (!_campaignFullyLoaded) return; // Skip initial-load clan assignments
            try
            {
                // Deduplicate — this event can fire multiple times for the same hero
                string dedupeKey = hero.StringId + "|" + (oldClan?.StringId ?? "") + "|" + (hero.Clan?.StringId ?? "");
                if (!_recentClanChanges.Add(dedupeKey)) return;
                if (_recentClanChanges.Count > 200) _recentClanChanges.Clear();

                string heroName = hero.Name?.ToString() ?? "Unknown";
                string heroLink = "«h:" + hero.StringId + "»" + heroName + "«/h»";
                bool isCompanion = hero.IsPlayerCompanion || hero.CompanionOf != null;

                if (oldClan != null && hero.Clan != null)
                {
                    if (isCompanion)
                    {
                        AutoLog(hero.StringId, "[Politics] Entered the service of " + "«c:" + hero.Clan.StringId + "»" + hero.Clan.Name + "«/c»" + " as a trusted companion.");
                        AutoLog(hero.Clan.StringId, "[Politics] " + heroLink + " has joined the retinue as a companion.");
                        AutoLog(oldClan.StringId, "[Politics] " + heroLink + " has departed the service of the clan.");
                    }
                    else
                    {
                        AutoLog(hero.StringId, "[Politics] Left the ranks of " + "«c:" + oldClan.StringId + "»" + oldClan.Name + "«/c»" + " and joined " + "«c:" + hero.Clan.StringId + "»" + hero.Clan.Name + "«/c»" + ".");
                        AutoLog(hero.Clan.StringId, "[Politics] " + heroLink + " has joined the clan from " + "«c:" + oldClan.StringId + "»" + oldClan.Name + "«/c»" + ".");
                        AutoLog(oldClan.StringId, "[Politics] " + heroLink + " has departed, joining " + "«c:" + hero.Clan.StringId + "»" + hero.Clan.Name + "«/c»" + ".");
                    }
                }
                else if (hero.Clan != null)
                {
                    AutoLog(hero.StringId, "[Politics] Entered the service of " + "«c:" + hero.Clan.StringId + "»" + hero.Clan.Name + "«/c»" + ".");
                    AutoLog(hero.Clan.StringId, "[Politics] " + heroLink + " has joined the clan.");
                }
                else if (oldClan != null)
                {
                    AutoLog(hero.StringId, "[Politics] Parted ways with " + "«c:" + oldClan.StringId + "»" + oldClan.Name + "«/c»" + " and set out alone.");
                    AutoLog(oldClan.StringId, "[Politics] " + heroLink + " has departed the clan.");
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal HeroChangedClan error: " + ex.ToString()); }
        }

        // ── Kingdom Decision Events ──

        private void OnKingdomDecisionConcluded(KingdomDecision decision, DecisionOutcome outcome, bool isPlayerInvolved)
        {
            if (decision == null) return;
            try
            {
                var kingdom = decision.Kingdom;
                if (kingdom == null) return;

                string decisionText = null;

                // Try to extract a readable decision summary from the type name
                string typeName = decision.GetType().Name;
                if (typeName.Contains("Policy"))
                {
                    // Use reflection to safely get policy name
                    try
                    {
                        var policyProp = decision.GetType().GetProperty("Policy");
                        var policy = policyProp?.GetValue(decision);
                        var nameProp = policy?.GetType().GetProperty("Name");
                        string policyName = nameProp?.GetValue(policy)?.ToString() ?? "a policy";
                        decisionText = "[Politics] The council has deliberated on the policy of " + policyName + ". The decree has been issued.";
                    }
                    catch { decisionText = "[Politics] A policy matter was brought before the council and a decision was rendered."; }
                }
                else if (typeName.Contains("War") || typeName.Contains("Peace"))
                {
                    decisionText = "[Politics] The council voted on matters of " + (typeName.Contains("War") ? "war — the drums of battle echo through the halls" : "peace — envoys have been dispatched") + ".";
                }
                else if (typeName.Contains("Claimant") || typeName.Contains("Settlement"))
                {
                    decisionText = "[Politics] The council deliberated on a settlement claim. A lord has been chosen to hold the fief.";
                }
                else
                {
                    decisionText = "[Politics] The royal council has convened and rendered a decision.";
                }

                if (decisionText != null)
                    AutoLog(kingdom.StringId, decisionText);
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal KingdomDecision error: " + ex.ToString()); }
        }

        // ── Rebellion Events ──

        private void OnRebellionFinished(Settlement settlement, Clan rebelliousClan)
        {
            if (settlement == null) return;
            try
            {
                string sName = "«s:" + settlement.StringId + "»" + settlement.Name + "«/s»";
                string clanInfo = rebelliousClan != null
                    ? "«c:" + rebelliousClan.StringId + "»" + rebelliousClan.Name + "«/c»"
                    : "rebels";

                AutoLog(settlement.StringId, "[War] The people have risen in rebellion under the banner of " + clanInfo + ". The streets run with the chaos of revolt.");
                if (rebelliousClan != null)
                    AutoLog(rebelliousClan.StringId, "[War] Rose up in rebellion at " + sName + ", seizing control from the ruling lord.");
                if (settlement.OwnerClan?.Kingdom != null)
                    AutoLog(settlement.OwnerClan.Kingdom.StringId, "[War] Rebellion has erupted at " + sName + " — " + clanInfo + " has risen against the crown.");
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal RebellionFinished error: " + ex.ToString()); }
        }

        // ── Quest Events ──

        private void OnQuestStarted(QuestBase quest)
        {
            if (quest == null) return;
            try
            {
                var questGiver = quest.QuestGiver;
                string questTitle = quest.Title?.ToString() ?? "a quest";

                // Log to the quest giver
                if (questGiver != null)
                    AutoLog(questGiver.StringId, "[Other] Entrusted a task — \"" + questTitle + "\" — seeking aid.");

                // Log to the player
                if (Hero.MainHero != null)
                    AutoLog(Hero.MainHero.StringId, "[Other] Took on the task \"" + questTitle + "\""
                        + (questGiver != null ? ", commissioned by " + HeroInfo(questGiver) : "") + ".");
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal QuestStarted error: " + ex.ToString()); }
        }

        private void OnQuestCompleted(QuestBase quest, QuestBase.QuestCompleteDetails detail)
        {
            if (quest == null) return;
            try
            {
                var questGiver = quest.QuestGiver;
                string questTitle = quest.Title?.ToString() ?? "a quest";
                string resultStr;
                switch (detail)
                {
                    case QuestBase.QuestCompleteDetails.Success: resultStr = "fulfilled with honor"; break;
                    case QuestBase.QuestCompleteDetails.Fail: resultStr = "ended in failure"; break;
                    case QuestBase.QuestCompleteDetails.Cancel: resultStr = "was abandoned"; break;
                    case QuestBase.QuestCompleteDetails.Timeout: resultStr = "expired — the moment has passed"; break;
                    default: resultStr = "concluded (" + detail + ")"; break;
                }

                if (Hero.MainHero != null)
                    AutoLog(Hero.MainHero.StringId, "[Other] The task \"" + questTitle + "\" " + resultStr + ".");
                if (questGiver != null)
                    AutoLog(questGiver.StringId, "[Other] The task \"" + questTitle + "\" " + resultStr + ".");
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal QuestCompleted error: " + ex.ToString()); }
        }

        // ── Ransom Events ──

        private void OnRansomOfferedToPlayer(Hero captiveHero)
        {
            if (captiveHero == null || Hero.MainHero == null) return;
            try
            {
                AutoLog(Hero.MainHero.StringId, "[War] A ransom has been offered for the release of " + HeroInfo(captiveHero) + ". Gold for freedom.");
                AutoLog(captiveHero.StringId, "[War] A ransom has been proposed to " + HeroInfo(Hero.MainHero) + " for release from captivity.");
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal RansomOffered error: " + ex.ToString()); }
        }

        // ── Mercenary Events ──

        private void OnMercenaryTroopChanged(Town town, CharacterObject oldTroop, CharacterObject newTroop)
        {
            if (town?.Settlement == null) return;
            try
            {
                string sName = "«s:" + town.Settlement.StringId + "»" + town.Settlement.Name + "«/s»";
                string troopName = newTroop?.Name?.ToString() ?? "mercenaries";
                AutoLog(town.Settlement.StringId, "[Other] A band of " + troopName + " has arrived in the settlement, offering their swords for coin.");
            }
            catch (Exception ex) { MCMSettings.DebugLog("AutoJournal MercenaryTroopChanged error: " + ex.ToString()); }
        }

    }

    // Note: JournalEntry, RelationHistoryEntry, EditableEncyclopediaSaveDefiner,
    // TagImportMode, and TagUsageInfo moved to EE-Core/SaveableTypes.cs in v2.6.0.
    // Same namespace, same field shapes, same SaveableTypeDefiner base ID.
}