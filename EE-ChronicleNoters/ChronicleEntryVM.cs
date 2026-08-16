using System;
using System.Collections.Generic;
using TaleWorlds.Library;

namespace EditableEncyclopedia.ChronicleNoters
{
    /// <summary>
    /// VM for one chronicle entry row in the redesigned panel's left list column.
    /// One instance per parsed entry. Carries everything the XML prefab needs to
    /// render the row (date, title, tags, star, note dot) AND the reading column
    /// (full body, player note, large title).
    ///
    /// 2026-05-28: Created during chronicle panel redesign (Phase 3 of the plan).
    /// Separate from <see cref="EditableEncyclopedia.ChronicleButtonVM"/> (which is
    /// the map-bar J-button click handler — NOT an entry-row VM).
    /// Wraps the data class <see cref="EditableEncyclopediaAPI.ChronicleEntry"/>
    /// (EntityId / Date / Text) with extra bindable display properties.
    /// </summary>
    public class ChronicleEntryVM : ViewModel
    {
        // Stable identity (drives bookmark lookup + reading-pane selection equality)
        public string EntryId { get; set; }

        private int _entryYear;
        public int EntryYear
        {
            get { return _entryYear; }
            set { if (_entryYear != value) { _entryYear = value; OnPropertyChanged("EntryYearLabel"); OnPropertyChanged("HasEntryYear"); OnPropertyChanged("ShortDateLabel"); } }
        }

        // Sort support (set by ChronicleEntryPopulator). SortDateKey = comparable full-date value
        // (year/season/day); BuildIndex = stable unique tiebreak so toggling sort always reverses,
        // even when many entries share the same date.
        public long SortDateKey;
        public int BuildIndex;
        /// <summary>String form for XML binding (Gauntlet has no int→string converter).</summary>
        [DataSourceProperty]
        public string EntryYearLabel { get { return _entryYear > 0 ? _entryYear.ToString() : string.Empty; } }
        [DataSourceProperty]
        public bool HasEntryYear { get { return _entryYear > 0; } }

        private string _dayValue = string.Empty;
        [DataSourceProperty]
        public string DayValue
        {
            get { return _dayValue; }
            set { if (_dayValue != value) { _dayValue = value ?? string.Empty; OnPropertyChanged("DayValue"); OnPropertyChanged("HasDayValue"); } }
        }
        [DataSourceProperty]
        public bool HasDayValue { get { return !string.IsNullOrEmpty(_dayValue); } }

        private string _seasonValue = string.Empty;
        [DataSourceProperty]
        public string SeasonValue
        {
            get { return _seasonValue; }
            set { if (_seasonValue != value) { _seasonValue = value ?? string.Empty; OnPropertyChanged("SeasonValue"); OnPropertyChanged("HasSeasonValue"); OnPropertyChanged("ShortDateLabel"); } }
        }
        [DataSourceProperty]
        public bool HasSeasonValue { get { return !string.IsNullOrEmpty(_seasonValue); } }

        [DataSourceProperty]
        public string ShortDateLabel
        {
            get
            {
                string s = (_seasonValue ?? string.Empty).Trim();
                string y = EntryYearLabel;
                if (string.IsNullOrEmpty(s)) return y;
                if (string.IsNullOrEmpty(y)) return s;
                return s + " " + y;
            }
        }

        private string _timelineContextLabel = string.Empty;
        [DataSourceProperty]
        public string TimelineContextLabel
        {
            get { return _timelineContextLabel; }
            set { if (_timelineContextLabel != value) { _timelineContextLabel = value ?? string.Empty; OnPropertyChanged("TimelineContextLabel"); OnPropertyChanged("HasTimelineContext"); } }
        }
        [DataSourceProperty]
        public bool HasTimelineContext { get { return !string.IsNullOrEmpty(_timelineContextLabel); } }

        // Event-type color (C2 muted-heraldic). Hex is #RRGGBBAA (Bannerlord format).
        // Precedence matches IconTextChar (War > Crime > Politics > Family > Other).
        private static string EventHexOpaque(EventTag tags)
        {
            if ((tags & EventTag.War) != 0) return "#A65A4EFF";
            if ((tags & EventTag.Crime) != 0) return "#BC9050FF";
            if ((tags & EventTag.Politics) != 0) return "#5A7A99FF";
            if ((tags & EventTag.Family) != 0) return "#6E8C5AFF";
            return "#8A7A60FF";
        }
        private static string EventHexForFlagDim(EventTag t)
        {
            if (t == EventTag.War) return "#A65A4E40";
            if (t == EventTag.Crime) return "#BC905040";
            if (t == EventTag.Politics) return "#5A7A9940";
            if (t == EventTag.Family) return "#6E8C5A40";
            return "#8A7A6040";
        }
        private static EventTag PrimaryEventFlag(EventTag t)
        {
            if ((t & EventTag.War) != 0) return EventTag.War;
            if ((t & EventTag.Crime) != 0) return EventTag.Crime;
            if ((t & EventTag.Politics) != 0) return EventTag.Politics;
            if ((t & EventTag.Family) != 0) return EventTag.Family;
            return EventTag.Other;
        }

        /// <summary>Opaque hex of the primary event tag's colour — drives icon-ring + story-card borders.</summary>
        [DataSourceProperty]
        public string EventColorHex { get { return EventHexOpaque(_eventTags); } }

        [DataSourceProperty]
        public string EventColorDimHex { get { return EventHexForFlagDim(PrimaryEventFlag(_eventTags)); } }

        // ── Extra entity-stat slots (RELATED card 2nd grid row) ──────────
        // Generic 3-slot model: each slot is a (Label, Value) pair + a Has gate.
        // The populator fills these per source-entity type (Hero AGE/RENOWN/TIER,
        // Clan TIER/RENOWN/FIEFS, Settlement TYPE/PROSPERITY/OWNER, Kingdom
        // CLANS/FIEFS/STRENGTH). Labels are dynamic (UPPERCASE), so the XML binds
        // both the value and the label. HasExtraStatN hides the cell when empty.
        private string _extraStat1Label = string.Empty;
        [DataSourceProperty] public string ExtraStat1Label { get { return _extraStat1Label; } set { if (_extraStat1Label != value) { _extraStat1Label = value ?? string.Empty; OnPropertyChanged("ExtraStat1Label"); } } }
        private string _extraStat1Value = string.Empty;
        [DataSourceProperty] public string ExtraStat1Value { get { return _extraStat1Value; } set { if (_extraStat1Value != value) { _extraStat1Value = value ?? string.Empty; OnPropertyChanged("ExtraStat1Value"); OnPropertyChanged("HasExtraStat1"); } } }
        [DataSourceProperty] public bool HasExtraStat1 { get { return !string.IsNullOrEmpty(_extraStat1Value); } }

        private string _extraStat2Label = string.Empty;
        [DataSourceProperty] public string ExtraStat2Label { get { return _extraStat2Label; } set { if (_extraStat2Label != value) { _extraStat2Label = value ?? string.Empty; OnPropertyChanged("ExtraStat2Label"); } } }
        private string _extraStat2Value = string.Empty;
        [DataSourceProperty] public string ExtraStat2Value { get { return _extraStat2Value; } set { if (_extraStat2Value != value) { _extraStat2Value = value ?? string.Empty; OnPropertyChanged("ExtraStat2Value"); OnPropertyChanged("HasExtraStat2"); } } }
        [DataSourceProperty] public bool HasExtraStat2 { get { return !string.IsNullOrEmpty(_extraStat2Value); } }

        private string _extraStat3Label = string.Empty;
        [DataSourceProperty] public string ExtraStat3Label { get { return _extraStat3Label; } set { if (_extraStat3Label != value) { _extraStat3Label = value ?? string.Empty; OnPropertyChanged("ExtraStat3Label"); } } }
        private string _extraStat3Value = string.Empty;
        [DataSourceProperty] public string ExtraStat3Value { get { return _extraStat3Value; } set { if (_extraStat3Value != value) { _extraStat3Value = value ?? string.Empty; OnPropertyChanged("ExtraStat3Value"); OnPropertyChanged("HasExtraStat3"); } } }
        [DataSourceProperty] public bool HasExtraStat3 { get { return !string.IsNullOrEmpty(_extraStat3Value); } }

        // Static click hooks invoked by ExecuteSelectEntry / ExecuteToggleStar.
        // Wired by GlobalChroniclePanel once at panel-open time.
        public static Action<ChronicleEntryVM> EntryClicked;
        public static Action<ChronicleEntryVM> BookmarkToggleRequested;
        // 2026-05-29: raised after a successful encyclopedia navigation so the host
        // panel layer can close itself (wired by GlobalChroniclePanelV2Layer.Open).
        public static Action CloseRequested;

        // ── Text content ──────────────────────────────────────────────
        private string _titleText;
        [DataSourceProperty]
        public string TitleText
        {
            get { return _titleText; }
            set { if (_titleText != value) { _titleText = value; OnPropertyChanged("TitleText"); } }
        }

        private string _bodyText;
        [DataSourceProperty]
        public string BodyText
        {
            get { return _bodyText; }
            set { if (_bodyText != value) { _bodyText = value; OnPropertyChanged("BodyText"); } }
        }

        private string _dateLabel;
        /// <summary>Short uppercased date for the list-row "YEAR 1084 · SUMMER" line.</summary>
        [DataSourceProperty]
        public string DateLabel
        {
            get { return _dateLabel; }
            set { if (_dateLabel != value) { _dateLabel = value; OnPropertyChanged("DateLabel"); } }
        }

        // ── Star / bookmark ──────────────────────────────────────────
        private bool _isStarred;
        [DataSourceProperty]
        public bool IsStarred
        {
            get { return _isStarred; }
            set { if (_isStarred != value) { _isStarred = value; OnPropertyChanged("IsStarred"); } }
        }

        public void ExecuteToggleStar()
        {
            IsStarred = !IsStarred;
            var h = BookmarkToggleRequested;
            if (h != null) h(this);
        }

        // ── Player note (orange note-block in reading pane) ─────────
        private string _noteText;
        [DataSourceProperty]
        public string NoteText
        {
            get { return _noteText; }
            set
            {
                if (_noteText != value)
                {
                    _noteText = value;
                    OnPropertyChanged("NoteText");
                    OnPropertyChanged("HasNote");
                }
            }
        }

        /// <summary>Drives the orange note-dot indicator on the list row + the reading-pane note block.</summary>
        [DataSourceProperty]
        public bool HasNote { get { return !string.IsNullOrEmpty(_noteText); } }

        // ── Tag bitmasks + display strings ───────────────────────────
        private EventTag _eventTags;
        public EventTag EventTags
        {
            get { return _eventTags; }
            set
            {
                if (_eventTags != value)
                {
                    _eventTags = value;
                    OnPropertyChanged("EventTagsLabel");
                    OnPropertyChanged("IconTextChar");
                    OnPropertyChanged("PrimaryEventTagLabel");
                    OnPropertyChanged("EventColorHex");
                    OnPropertyChanged("EventColorDimHex");
                    RebuildTagPills();
                }
            }
        }

        private EntityTag _entityTags;
        public EntityTag EntityTags
        {
            get { return _entityTags; }
            set
            {
                if (_entityTags != value)
                {
                    _entityTags = value;
                    OnPropertyChanged("EntityTagsLabel");
                    OnPropertyChanged("PrimaryEntityTagLabel");
                    OnPropertyChanged("EntityTagLabel");
                    OnPropertyChanged("HasEntityTag");
                    RebuildTagPills();
                }
            }
        }

        /// <summary>Space-separated event tag names ("War Politics"). Empty if no tags.</summary>
        [DataSourceProperty]
        public string EventTagsLabel { get { return FormatEventTags(_eventTags); } }

        /// <summary>Space-separated entity tag names ("Kingdom Clan"). Empty if no tags.</summary>
        [DataSourceProperty]
        public string EntityTagsLabel { get { return FormatEntityTags(_entityTags); } }

        /// <summary>First active event tag's display name — used for the single mini-tag in the list row.</summary>
        [DataSourceProperty]
        public string PrimaryEventTagLabel { get { return FirstEventTag(_eventTags); } }

        /// <summary>First active entity tag's display name — used for the single mini-tag in the list row.</summary>
        [DataSourceProperty]
        public string PrimaryEntityTagLabel { get { return FirstEntityTag(_entityTags); } }

        // ── Tag pills (right reading pane: separate CRIME / WAR / HERO chips) ──
        // Mirrors the proven YearStrip MBBindingList + ItemTemplate pattern that already
        // renders in this prefab. Event-tag names come first (brighter), then entity-tag
        // names. Rebuilt whenever EventTags or EntityTags change.
        private MBBindingList<TagPillVM> _tagPills = new MBBindingList<TagPillVM>();
        [DataSourceProperty]
        public MBBindingList<TagPillVM> TagPills
        {
            get { return _tagPills; }
            set { if (_tagPills != value) { _tagPills = value; OnPropertyChanged("TagPills"); } }
        }

        private void RebuildTagPills()
        {
            if (_tagPills == null) _tagPills = new MBBindingList<TagPillVM>();
            _tagPills.Clear();
            foreach (EventTag t in Enum.GetValues(typeof(EventTag)))
                if (t != EventTag.None && (_eventTags & t) == t)
                    _tagPills.Add(new TagPillVM(t.DisplayName().ToUpperInvariant(), EventHexForFlagDim(t)));
            foreach (EntityTag t in Enum.GetValues(typeof(EntityTag)))
                if (t != EntityTag.None && (_entityTags & t) == t)
                    _tagPills.Add(new TagPillVM(t.DisplayName().ToUpperInvariant(), "#C8985830"));
            OnPropertyChanged("TagPills");
        }

        // 2026-08-02: the IconSpriteId property was REMOVED along with the EventTag extension
        // it forwarded to. It claimed to be "the round type-icon at the start of each list
        // row", but no prefab ever bound @IconSpriteId — the list row's type icon is the
        // IconTextChar TextWidget below. Every sprite name it could return
        // (swords/skull/crown/scales/star) is absent from the shipped game and would have
        // painted a blank square. Its OnPropertyChanged notify in the EventTags setter went
        // with it, so nothing dangles.

        /// <summary>
        /// Single-character glyph for the primary event tag, used by the entry-row's
        /// type-icon TextWidget. Letters chosen for v1 because Galahad font support
        /// for Unicode glyphs (☠/⚔/♛/⚖) is unproven. Glyphs can be substituted in
        /// one edit later (see Task 4.3).
        /// </summary>
        [DataSourceProperty]
        public string IconTextChar
        {
            get
            {
                if ((_eventTags & EventTag.War) != 0) return "W";
                if ((_eventTags & EventTag.Crime) != 0) return "C";
                if ((_eventTags & EventTag.Politics) != 0) return "P";
                if ((_eventTags & EventTag.Family) != 0) return "F";
                if ((_eventTags & EventTag.Other) != 0) return "*";
                return "*";
            }
        }

        // ── Source-entity resolved metadata (right reading pane "RELATED" block) ──
        // The populator resolves the raw chronicle EntityId against MBObjectManager
        // (Hero / Clan / Settlement / Kingdom by StringId) and sets these strings.
        // If unresolved (engine-only id, dead reference, etc.), the strings stay empty
        // and the right pane simply hides those rows via IsVisible="@HasSourceXxx".
        private string _sourceEntityId = string.Empty;
        [DataSourceProperty]
        public string SourceEntityId
        {
            get { return _sourceEntityId; }
            set { if (_sourceEntityId != value) { _sourceEntityId = value ?? string.Empty; OnPropertyChanged("SourceEntityId"); OnPropertyChanged("HasSourceEntityId"); } }
        }
        [DataSourceProperty]
        public bool HasSourceEntityId { get { return !string.IsNullOrEmpty(_sourceEntityId); } }

        private string _sourceEntityName = string.Empty;
        [DataSourceProperty]
        public string SourceEntityName
        {
            get { return _sourceEntityName; }
            set { if (_sourceEntityName != value) { _sourceEntityName = value ?? string.Empty; OnPropertyChanged("SourceEntityName"); OnPropertyChanged("HasSourceEntity"); OnPropertyChanged("EntityTagLabel"); OnPropertyChanged("HasEntityTag"); } }
        }
        [DataSourceProperty]
        public bool HasSourceEntity { get { return !string.IsNullOrEmpty(_sourceEntityName); } }

        [DataSourceProperty]
        public string EntityTagLabel
        {
            get
            {
                string s = !string.IsNullOrEmpty(_sourceEntityName) ? _sourceEntityName : PrimaryEntityTagLabel;
                if (!string.IsNullOrEmpty(s) && s.Length > 18) s = s.Substring(0, 18);
                return s;
            }
        }

        [DataSourceProperty]
        public bool HasEntityTag { get { return !string.IsNullOrEmpty(EntityTagLabel); } }

        private string _sourceClanName = string.Empty;
        [DataSourceProperty]
        public string SourceClanName
        {
            get { return _sourceClanName; }
            set { if (_sourceClanName != value) { _sourceClanName = value ?? string.Empty; OnPropertyChanged("SourceClanName"); OnPropertyChanged("HasSourceClan"); OnPropertyChanged("ShowClanStatCell"); } }
        }
        [DataSourceProperty]
        public bool HasSourceClan { get { return !string.IsNullOrEmpty(_sourceClanName); } }

        /// <summary>Show the CLAN stat-cell only when a clan name exists AND the entity itself is NOT a clan (de-dupe).</summary>
        [DataSourceProperty]
        public bool ShowClanStatCell { get { return !string.IsNullOrEmpty(_sourceClanName) && !string.Equals(_sourceKindLabel, "CLAN", StringComparison.OrdinalIgnoreCase); } }

        private string _sourceKingdomName = string.Empty;
        [DataSourceProperty]
        public string SourceKingdomName
        {
            get { return _sourceKingdomName; }
            set { if (_sourceKingdomName != value) { _sourceKingdomName = value ?? string.Empty; OnPropertyChanged("SourceKingdomName"); OnPropertyChanged("HasSourceKingdom"); } }
        }
        [DataSourceProperty]
        public bool HasSourceKingdom { get { return !string.IsNullOrEmpty(_sourceKingdomName); } }

        private string _sourceCultureName = string.Empty;
        [DataSourceProperty]
        public string SourceCultureName
        {
            get { return _sourceCultureName; }
            set { if (_sourceCultureName != value) { _sourceCultureName = value ?? string.Empty; OnPropertyChanged("SourceCultureName"); OnPropertyChanged("HasSourceCulture"); } }
        }
        [DataSourceProperty]
        public bool HasSourceCulture { get { return !string.IsNullOrEmpty(_sourceCultureName); } }

        private string _sourceKindLabel = string.Empty;
        /// <summary>Human-readable kind: "HERO", "CLAN", "SETTLEMENT", "KINGDOM", or empty.</summary>
        [DataSourceProperty]
        public string SourceKindLabel
        {
            get { return _sourceKindLabel; }
            set { if (_sourceKindLabel != value) { _sourceKindLabel = value ?? string.Empty; OnPropertyChanged("SourceKindLabel"); OnPropertyChanged("ShowClanStatCell"); } }
        }

        // ── Hero portrait + Banner code (rendered via ImageIdentifierWidget + BannerVisualWidget) ──
        // Portrait is typed as `object` because CharacterImageIdentifierVM lives in a namespace
        // that has moved across patches (TaleWorlds.Core.ViewModelCollection.ImageIdentifiers
        // vs the older TaleWorlds.Core.ImageIdentifiers); see BandIt Plus's reflection recipe.
        // Banner is a raw BannerCode string consumed by `BannerVisualWidget BannerCode="..."`.
        private object _heroPortrait;
        [DataSourceProperty]
        public object HeroPortrait
        {
            get { return _heroPortrait; }
            set { if (_heroPortrait != value) { _heroPortrait = value; OnPropertyChanged("HeroPortrait"); OnPropertyChanged("HasHeroPortrait"); } }
        }
        [DataSourceProperty]
        public bool HasHeroPortrait { get { return _heroPortrait != null; } }

        private string _bannerCodeText = string.Empty;
        [DataSourceProperty]
        public string BannerCodeText
        {
            get { return _bannerCodeText; }
            set { if (_bannerCodeText != value) { _bannerCodeText = value ?? string.Empty; OnPropertyChanged("BannerCodeText"); OnPropertyChanged("HasBannerCode"); } }
        }
        [DataSourceProperty]
        public bool HasBannerCode { get { return !string.IsNullOrEmpty(_bannerCodeText); } }

        // 2026-08-02: the clan banner as a real BannerImageIdentifierVM, so an ImageIdentifierWidget
        // can bind it the same way HeroPortrait is bound. BannerCodeText above is kept for
        // compatibility, but nothing renders it — BannerVisualWidget has 0 usages in the shipped
        // game, which is why the banner never appeared. Typed `object` for the same reason as
        // HeroPortrait: the ImageIdentifiers namespace has moved between patches.
        private object _clanBanner;
        [DataSourceProperty]
        public object ClanBanner
        {
            get { return _clanBanner; }
            set { if (_clanBanner != value) { _clanBanner = value; OnPropertyChanged("ClanBanner"); OnPropertyChanged("HasClanBanner"); } }
        }
        [DataSourceProperty]
        public bool HasClanBanner { get { return _clanBanner != null; } }

        // ── Right-pane dossier content (all auto-derived; nothing is player-entered) ──
        private MBBindingList<ChronicleInfoRowVM> _infoRows = new MBBindingList<ChronicleInfoRowVM>();
        [DataSourceProperty]
        public MBBindingList<ChronicleInfoRowVM> InfoRows
        {
            get { return _infoRows; }
            set { if (_infoRows != value) { _infoRows = value; OnPropertyChanged("InfoRows"); OnPropertyChanged("HasInfoRows"); } }
        }
        [DataSourceProperty]
        public bool HasInfoRows { get { return _infoRows != null && _infoRows.Count > 0; } }

        private MBBindingList<ChronicleRefRowVM> _relatedEntries = new MBBindingList<ChronicleRefRowVM>();
        [DataSourceProperty]
        public MBBindingList<ChronicleRefRowVM> RelatedEntries
        {
            get { return _relatedEntries; }
            set { if (_relatedEntries != value) { _relatedEntries = value; OnPropertyChanged("RelatedEntries"); OnPropertyChanged("HasRelatedEntries"); } }
        }
        [DataSourceProperty]
        public bool HasRelatedEntries { get { return _relatedEntries != null && _relatedEntries.Count > 0; } }

        private MBBindingList<ChronicleRefRowVM> _sameYearEntries = new MBBindingList<ChronicleRefRowVM>();
        [DataSourceProperty]
        public MBBindingList<ChronicleRefRowVM> SameYearEntries
        {
            get { return _sameYearEntries; }
            set { if (_sameYearEntries != value) { _sameYearEntries = value; OnPropertyChanged("SameYearEntries"); OnPropertyChanged("HasSameYearEntries"); } }
        }
        [DataSourceProperty]
        public bool HasSameYearEntries { get { return _sameYearEntries != null && _sameYearEntries.Count > 0; } }

        // ── SPOILS (gold + looted items captured for this entry) ─────────────────────
        // 2026-08-02: rendering half of the battle/raid/conquest loot feature. The data is
        // owned by the sibling EditableEncyclopedia module and reached through
        // EditableEncyclopediaAPI.GetChronicleSpoils(entityId, date, RAW text); the populator
        // fills these in BuildSpoils().
        //
        // MBBindingList rule this file already lives by (see InfoRows / RelatedEntries above):
        // the Has* gates read .Count, but their notification only fires from the SETTER, which
        // only runs when the list REFERENCE changes. The populator therefore ASSIGNS a freshly
        // built list — mutating _spoilsItems in place would leave HasSpoilItems stale and the
        // whole block permanently hidden.
        private string _spoilsGoldLabel = string.Empty;
        /// <summary>Formatted denars, e.g. "1,240 denars" (or "90 denars lost" when the entry's hero lost gold).</summary>
        [DataSourceProperty]
        public string SpoilsGoldLabel
        {
            get { return _spoilsGoldLabel; }
            set
            {
                if (_spoilsGoldLabel != value)
                {
                    _spoilsGoldLabel = value ?? string.Empty;
                    OnPropertyChanged("SpoilsGoldLabel");
                    OnPropertyChanged("HasSpoilsGold");
                    OnPropertyChanged("HasSpoils");
                }
            }
        }
        [DataSourceProperty]
        public bool HasSpoilsGold { get { return !string.IsNullOrEmpty(_spoilsGoldLabel); } }

        private MBBindingList<ChronicleSpoilItemVM> _spoilsItems = new MBBindingList<ChronicleSpoilItemVM>();
        [DataSourceProperty]
        public MBBindingList<ChronicleSpoilItemVM> SpoilsItems
        {
            get { return _spoilsItems; }
            set
            {
                if (_spoilsItems != value)
                {
                    _spoilsItems = value;
                    OnPropertyChanged("SpoilsItems");
                    OnPropertyChanged("HasSpoilItems");
                    OnPropertyChanged("HasSpoils");
                }
            }
        }
        [DataSourceProperty]
        public bool HasSpoilItems { get { return _spoilsItems != null && _spoilsItems.Count > 0; } }

        private string _spoilsMoreLabel = string.Empty;
        /// <summary>Truncation remainder, e.g. "+7 more stacks". Empty when the stored list was complete.</summary>
        [DataSourceProperty]
        public string SpoilsMoreLabel
        {
            get { return _spoilsMoreLabel; }
            set
            {
                if (_spoilsMoreLabel != value)
                {
                    _spoilsMoreLabel = value ?? string.Empty;
                    OnPropertyChanged("SpoilsMoreLabel");
                    OnPropertyChanged("HasSpoilsMore");
                    OnPropertyChanged("HasSpoils");
                }
            }
        }
        [DataSourceProperty]
        public bool HasSpoilsMore { get { return !string.IsNullOrEmpty(_spoilsMoreLabel); } }

        /// <summary>
        /// Master gate for the whole SPOILS block. Zero gold with no items means the entry has
        /// no spoils record at all, so nothing is drawn — the populator simply leaves all three
        /// backing values at their empty defaults in that case.
        /// </summary>
        [DataSourceProperty]
        public bool HasSpoils { get { return HasSpoilsGold || HasSpoilItems || HasSpoilsMore; } }

        /// <summary>Header for the MORE FROM block, e.g. "MORE FROM ARADWYR".</summary>
        [DataSourceProperty]
        public string RelatedHeaderLabel
        {
            get
            {
                string n = !string.IsNullOrEmpty(SourceEntityName) ? SourceEntityName.ToUpperInvariant() : "THIS ENTITY";
                return "MORE FROM " + n;
            }
        }

        /// <summary>Header for the ALSO IN block, e.g. "ALSO IN 1084".</summary>
        [DataSourceProperty]
        public string SameYearHeaderLabel { get { return "ALSO IN " + EntryYearLabel; } }

        // ── Encyclopedia jump link (drives a "VIEW IN ENCYCLOPEDIA" button) ──
        // Populator sets this to entity.EncyclopediaLink (e.g. "Faction-clan_X").
        // Button click calls Campaign.Current.EncyclopediaManager.GoToLink — the
        // canonical 1.4.5-safe path (see feedback_bannerlord_encyclopedia_link_property memory).
        private string _encyclopediaLink = string.Empty;
        public string EncyclopediaLink
        {
            get { return _encyclopediaLink; }
            set { if (_encyclopediaLink != value) { _encyclopediaLink = value ?? string.Empty; OnPropertyChanged("CanOpenEncyclopedia"); } }
        }
        [DataSourceProperty]
        public bool CanOpenEncyclopedia { get { return !string.IsNullOrEmpty(_encyclopediaLink); } }

        public void ExecuteOpenEncyclopedia()
        {
            if (string.IsNullOrEmpty(_encyclopediaLink)) return;
            try
            {
                var em = TaleWorlds.CampaignSystem.Campaign.Current != null
                    ? TaleWorlds.CampaignSystem.Campaign.Current.EncyclopediaManager
                    : null;
                if (em != null)
                {
                    em.GoToLink(_encyclopediaLink);
                    // Close the host chronicle panel so the encyclopedia page is visible.
                    var c = CloseRequested;
                    if (c != null) c();
                }
            }
            catch { }
        }

        // ── Selection (drives "active" highlight in the list column) ──
        private bool _isSelected;
        [DataSourceProperty]
        public bool IsSelected
        {
            get { return _isSelected; }
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged("IsSelected"); } }
        }

        public void ExecuteSelectEntry()
        {
            var h = EntryClicked;
            if (h != null) h(this);
        }

        // ── Formatting helpers ───────────────────────────────────────
        private static string FormatEventTags(EventTag tags)
        {
            if (tags == EventTag.None) return string.Empty;
            var parts = new List<string>();
            foreach (EventTag t in Enum.GetValues(typeof(EventTag)))
                if (t != EventTag.None && (tags & t) == t) parts.Add(t.DisplayName());
            return string.Join(" ", parts);
        }

        private static string FormatEntityTags(EntityTag tags)
        {
            if (tags == EntityTag.None) return string.Empty;
            var parts = new List<string>();
            foreach (EntityTag t in Enum.GetValues(typeof(EntityTag)))
                if (t != EntityTag.None && (tags & t) == t) parts.Add(t.DisplayName());
            return string.Join(" ", parts);
        }

        private static string FirstEventTag(EventTag tags)
        {
            if (tags == EventTag.None) return string.Empty;
            foreach (EventTag t in Enum.GetValues(typeof(EventTag)))
                if (t != EventTag.None && (tags & t) == t) return t.DisplayName();
            return string.Empty;
        }

        private static string FirstEntityTag(EntityTag tags)
        {
            if (tags == EntityTag.None) return string.Empty;
            foreach (EntityTag t in Enum.GetValues(typeof(EntityTag)))
                if (t != EntityTag.None && (tags & t) == t) return t.DisplayName();
            return string.Empty;
        }
    }

    /// <summary>
    /// One tag pill in the reading pane's tag row (CRIME / WAR / HERO chips).
    /// Minimal item VM mirroring YearCellVM — just a display label. Bound via
    /// MBBindingList&lt;TagPillVM&gt; TagPills + an ItemTemplate in the prefab.
    /// </summary>
    public class TagPillVM : ViewModel
    {
        public TagPillVM(string label, string colorHex)
        {
            _label = label ?? string.Empty;
            _colorHex = colorHex ?? "#C8985830";
        }

        private string _label;
        [DataSourceProperty]
        public string Label
        {
            get { return _label; }
            set { if (_label != value) { _label = value ?? string.Empty; OnPropertyChanged("Label"); } }
        }

        private string _colorHex;
        /// <summary>Pill fill colour (event pills tinted by event type; entity pills neutral gold).</summary>
        [DataSourceProperty]
        public string ColorHex
        {
            get { return _colorHex; }
            set { if (_colorHex != value) { _colorHex = value ?? "#C8985830"; OnPropertyChanged("ColorHex"); } }
        }
    }
}
