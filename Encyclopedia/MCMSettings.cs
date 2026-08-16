using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;
using MCM.Common;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace EditableEncyclopedia
{
    /// <summary>
    /// MCM v5 global settings for Editable Encyclopedia.
    /// If MCM is not installed, MCMSettings.Instance will be null and the mod
    /// falls back to hard-coded defaults everywhere it is read.
    /// </summary>
    public class MCMSettings : AttributeGlobalSettings<MCMSettings>
    {
        public override string Id => "EditableEncyclopedia_v1";
        public override string DisplayName => "Editable Encyclopedia";
        public override string FolderName => "EditableEncyclopedia";
        public override string FormatType => "json2";

        // Probe whether the web-extension mod assembly is loaded. Used by the
        // EnableWebExtension setter to refuse `true` when the extension isn't installed,
        // so the visibility patch hides the subgroup.
        //
        // The module was renamed EEWebExtension -> EE-WebAPI (assembly/DLL name changed,
        // the C# namespace stayed EEWebExtension). The assembly's GetName().Name follows
        // the DLL name, so we accept BOTH: "EE-WebAPI" (current) and "EEWebExtension" (old
        // installs) — otherwise a renamed install reads as "Not installed" and the toggle
        // can never enable.
        //
        // No cache: the original v2.5.4 cache had a startup TOCTOU — the first call could
        // run before the extension assembly registered, latching `false` for the entire
        // session. The scan walks ~50–200 assemblies (~1–5 µs); MCM only calls this on
        // toggle interactions and the visibility-patch postfix, never in a per-tick path.
        internal static bool IsEEWebExtensionLoaded()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var name = asm.GetName().Name;
                    if (name == "EE-WebAPI" || name == "EEWebExtension") return true;
                }
            }
            catch (Exception ex) { DebugLog("[MCMSettings] EEWebExtension assembly scan failed: " + ex.ToString()); }
            return false;
        }

        private static readonly Color WarningYellow = new Color(1f, 0.9f, 0.2f);

        private const string DebugLogPrefix = "[EE Debug] ";
        private const string LogFileName = "debug.log";
        private const string LogBackupFileName = "debug.old.log";
        private const string LogFolderName = "Logs";
        private const string DiscordUrl = "https://discord.com/users/404393620897136640";
        private const string BannerEditorUrl = "https://bannerlord.party/banner/";
        private const int DefaultTagFontScalePercent = 130;
        private const int DefaultAutoTagEnemyRelation = -30;
        private const int DefaultAutoTagFriendRelation = 30;
        private const int DefaultAutoTagDangerousParty = 200;
        private const int DefaultTimelinePageSize = 25;
        private const int DefaultJournalPageSize = 10;
        private const int DefaultChroniclePageSize = 22;
        private const int DefaultInitialKeyPollDelayMs = 500;
        private const int DefaultKeyPollIntervalMs = 50;
        // Raised 5000 -> 10000: lore topics now scroll in place (WrapFieldInScroll), so long
        // write-ups display cleanly. Still user-configurable higher in MCM if desired.
        private const int DefaultMaxDescriptionLength = 10000;
        private const int DefaultMaxNarrativeFieldLength = 5000;
        private const int DefaultMaxStatsFieldLength = 5000;

        // ── Info ─────────────────────────────────────────────────
        // These are display-only; the setters silently discard any changes
        // so the values always remain fixed.

        [SettingPropertyText(
            "Author",
            HintText = "The author of this mod.",
            Order = 0,
            RequireRestart = false)]
        [SettingPropertyGroup("1. Info/About", GroupOrder = 0)]
        public string Author
        {
            get => "XMuPb";
            set { /* read-only, ignore */ }
        }

        [SettingPropertyText(
            "Version",
            HintText = "Current version of Editable Encyclopedia.",
            Order = 1,
            RequireRestart = false)]
        [SettingPropertyGroup("1. Info/About", GroupOrder = 0)]
        public string Version
        {
            get => "v2.6.1.1";
            set { /* read-only, ignore */ }
        }

        [SettingPropertyButton(
            "Join Discord",
            Content = "Open Invite",
            HintText = "Click to open the Discord invite link (discord.com/users/404393620897136640) in your browser.",
            Order = 2,
            RequireRestart = false)]
        [SettingPropertyGroup("1. Info/About", GroupOrder = 0)]
        public Action OpenDiscordInvite
        {
            get => () =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = DiscordUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex) { MCMSettings.DebugLog("MCMSettings: Failed to open Discord link: " + ex.ToString()); }
            };
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyButton(
            "Encyclopedia Edit Statistics",
            Content = "Show Stats",
            HintText = "Displays a summary of all custom edits in the current campaign: descriptions, names, titles, banners, cultures, occupations, lore fields, tags, journal entries, and relation notes.",
            Order = 3,
            RequireRestart = false)]
        [SettingPropertyGroup("1. Info/About", GroupOrder = 0)]
        public Action ShowDescriptionStats
        {
            get => () =>
            {
                try
                {
                    if (!EditableEncyclopediaAPI.IsAvailable)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(Localization.L("msg_no_campaign"), Colors.Red));
                        return;
                    }

                    int total = EditableEncyclopediaAPI.GetDescriptionCount();
                    int heroes = EditableEncyclopediaAPI.GetAllHeroDescriptions().Count;
                    int clans = EditableEncyclopediaAPI.GetAllClanDescriptions().Count;
                    int kingdoms = EditableEncyclopediaAPI.GetAllKingdomDescriptions().Count;
                    int settlements = EditableEncyclopediaAPI.GetAllSettlementDescriptions().Count;
                    int other = total - heroes - clans - kingdoms - settlements;

                    var allDescs = EditableEncyclopediaAPI.GetAllDescriptions();
                    int totalChars = 0;
                    foreach (var kvp in allDescs)
                        totalChars += kvp.Value.Length;

                    // Count names, titles, banners, cultures, and occupations
                    int customNames = 0, customTitles = 0, customBanners = 0;
                    int cultureAssignments = 0, occupationAssignments = 0, cultureTypes = 0;
                    int infoFields = 0, heroesWithInfo = 0, infoFieldChars = 0;
                    int taggedEntries = 0;
                    int journalEntities = 0, journalTotalEntries = 0, journalChars = 0;
                    int relationNoteHeroes = 0, relationNoteEntries = 0;
                    var behavior = EncyclopediaEditBehavior.Instance;
                    if (behavior != null)
                    {
                        customNames = behavior.GetCustomNameCount();
                        customTitles = behavior.GetCustomTitleCount();
                        customBanners = behavior.GetCustomBannerCount();
                        cultureAssignments = behavior.GetCustomCultureCount();
                        occupationAssignments = behavior.GetCustomOccupationCount();
                        cultureTypes = behavior.GetCustomCultureDefinitionCount();
                        infoFields = behavior.GetHeroInfoFieldCount();
                        heroesWithInfo = behavior.GetHeroesWithInfoFieldsCount();
                        infoFieldChars = behavior.GetHeroInfoFieldCharacterCount();
                        taggedEntries = behavior.GetTagCount();
                        journalEntities = behavior.GetJournalCount();
                        journalTotalEntries = behavior.GetTotalJournalEntryCount();
                        journalChars = behavior.GetJournalCharacterCount();
                        relationNoteHeroes = behavior.GetRelationNoteCount();
                        relationNoteEntries = behavior.GetTotalRelationNoteEntryCount();
                    }

                    // Build comprehensive stats string
                    var sb = new System.Text.StringBuilder();

                    // Descriptions
                    sb.Append("--- Descriptions ---\n");
                    sb.Append("Total: ").Append(total).Append(" description(s), ").Append(totalChars.ToString("N0")).Append(" characters\n");
                    sb.Append("Heroes: ").Append(heroes).Append(" | Clans: ").Append(clans);
                    sb.Append(" | Kingdoms: ").Append(kingdoms).Append(" | Settlements: ").Append(settlements);
                    if (other > 0) sb.Append(" | Other: ").Append(other);

                    // Customizations
                    sb.Append("\n\n--- Customizations ---\n");
                    sb.Append("Names: ").Append(customNames).Append(" | Titles: ").Append(customTitles);
                    sb.Append(" | Banners: ").Append(customBanners).Append("\n");
                    sb.Append("Culture Types: ").Append(cultureTypes).Append(" | Culture Edits: ").Append(cultureAssignments);
                    sb.Append(" | Occupation Edits: ").Append(occupationAssignments);
                    if (taggedEntries > 0)
                        sb.Append("\nTagged Entities: ").Append(taggedEntries);

                    // Hero Lore
                    if (infoFields > 0)
                    {
                        sb.Append("\n\n--- Hero Lore ---\n");
                        sb.Append("Lore Fields: ").Append(infoFields).Append(" across ").Append(heroesWithInfo).Append(" hero(es)");
                        sb.Append(", ").Append(infoFieldChars.ToString("N0")).Append(" characters");
                    }

                    // Journal & Chronicle
                    if (journalEntities > 0)
                    {
                        sb.Append("\n\n--- Journal & Chronicle ---\n");
                        sb.Append("Entities with entries: ").Append(journalEntities).Append("\n");
                        sb.Append("Total entries: ").Append(journalTotalEntries.ToString("N0"));
                        sb.Append(", ").Append(journalChars.ToString("N0")).Append(" characters");
                    }

                    // Relation Notes
                    if (relationNoteHeroes > 0)
                    {
                        sb.Append("\n\n--- Relation Notes ---\n");
                        sb.Append("Heroes with notes: ").Append(relationNoteHeroes).Append("\n");
                        sb.Append("Total notes: ").Append(relationNoteEntries);
                    }

                    string stats = sb.ToString();

                    InformationManager.ShowInquiry(
                        new InquiryData(
                            Localization.L("stats_title"),
                            stats,
                            true,
                            false,
                            Localization.L("stats_ok"),
                            null,
                            null,
                            null),
                        false,
                        false);
                }
                catch (Exception ex)
                {
                    DebugLog("Stats error: " + ex.ToString());
                }
            };
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyButton(
            "Open Config Folder",
            Content = "Open Folder",
            HintText = "Opens the EditableEncyclopedia config folder where your MCM JSON settings are stored.",
            Order = 4,
            RequireRestart = false)]
        [SettingPropertyGroup("1. Info/About", GroupOrder = 0)]
        public Action OpenConfigFolder
        {
            get => () =>
            {
                try
                {
                    string configPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "Mount and Blade II Bannerlord",
                        "Configs",
                        "ModSettings",
                        "Global",
                        "EditableEncyclopedia");

                    if (!System.IO.Directory.Exists(configPath))
                        System.IO.Directory.CreateDirectory(configPath);

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = configPath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex) { MCMSettings.DebugLog("MCMSettings: Failed to open config folder: " + ex.ToString()); }
            };
            set { /* MCM requires a setter, ignore */ }
        }

        // ── General / Hints ───────────────────────────────────

        [SettingPropertyBool(
            "Show Edit Hint",
            HintText = "Append '[Ctrl+E to Edit Description]' to encyclopedia page text.",
            Order = 0,
            RequireRestart = false)]
        [SettingPropertyGroup("2. General/Hints", GroupOrder = 1)]
        public bool ShowEditHint { get; set; } = true;

        [SettingPropertyBool(
            "Show Name Edit Hint",
            HintText = "Show '[Ctrl+N to Edit Name]' hint on hero encyclopedia pages.",
            Order = 1,
            RequireRestart = false)]
        [SettingPropertyGroup("2. General/Hints", GroupOrder = 1)]
        public bool ShowNameEditHint { get; set; } = true;

        [SettingPropertyBool(
            "Show Banner Edit Hint",
            HintText = "Show '[Ctrl+B to Edit Banner]' hint on encyclopedia pages.",
            Order = 2,
            RequireRestart = false)]
        [SettingPropertyGroup("2. General/Hints", GroupOrder = 1)]
        public bool ShowBannerEditHint { get; set; } = true;

        [SettingPropertyBool(
            "Show Culture Edit Hint",
            HintText = "Show '[Ctrl+U to Edit Culture]' hint on hero encyclopedia pages.",
            Order = 3,
            RequireRestart = false)]
        [SettingPropertyGroup("2. General/Hints", GroupOrder = 1)]
        public bool ShowCultureEditHint { get; set; } = true;

        [SettingPropertyBool(
            "Show Occupation Edit Hint",
            HintText = "Show '[Ctrl+O to Edit Occupation]' hint on hero encyclopedia pages.",
            Order = 4,
            RequireRestart = false)]
        [SettingPropertyGroup("2. General/Hints", GroupOrder = 1)]
        public bool ShowOccupationEditHint { get; set; } = true;

        [SettingPropertyBool(
            "Show Tag Edit Hint",
            HintText = "Show '[G Tags]' hint on encyclopedia pages.",
            Order = 5,
            RequireRestart = false)]
        [SettingPropertyGroup("2. General/Hints", GroupOrder = 1)]
        public bool ShowTagEditHint { get; set; } = true;

        [SettingPropertyBool(
            "Show Confirmation Messages",
            HintText = "Display a green message when a description is saved.",
            Order = 0,
            RequireRestart = false)]
        [SettingPropertyGroup("2. General/Display", GroupOrder = 1)]
        public bool ShowConfirmationMessages { get; set; } = true;

        [SettingPropertyBool(
            "Show Edited Indicator",
            HintText = "Prepend '[Edited]' to encyclopedia descriptions that have been customized, making them easy to identify.",
            Order = 1,
            RequireRestart = false)]
        [SettingPropertyGroup("2. General/Display", GroupOrder = 1)]
        public bool ShowEditedIndicator { get; set; } = false;

        [SettingPropertyBool(
            "Show Edit Timestamp",
            HintText = "Display the in-game date when a description was last edited (e.g., 'Edited: Day 15 of Spring, 1084').",
            Order = 2,
            RequireRestart = false)]
        [SettingPropertyGroup("2. General/Display", GroupOrder = 1)]
        public bool ShowEditTimestamp { get; set; } = true;

        [SettingPropertyBool(
            "Auto-Export on Save",
            HintText = "Automatically export all data (descriptions, names, titles, banners, cultures, occupations, lore fields, tags, timestamps, journal entries, relation notes, tag notes, relation history, tag categories, tag presets, auto-tag thresholds) to the shared JSON file every time a save occurs. Keeps the export file always up-to-date.",
            Order = 0,
            RequireRestart = false)]
        [SettingPropertyGroup("4. Export", GroupOrder = 3)]
        public bool AutoExportOnSave { get; set; } = false;

        [SettingPropertyBool(
            "Auto-Import on Load",
            HintText = "Automatically import all data (descriptions, names, titles, banners, cultures, occupations, lore fields, tags, timestamps, journal entries, relation notes, tag notes, relation history, tag categories, tag presets, auto-tag thresholds) from the shared JSON file when a save is loaded. Useful for keeping data synced across campaigns.",
            Order = 0,
            RequireRestart = false)]
        [SettingPropertyGroup("5. Import", GroupOrder = 4)]
        public bool AutoImportOnLoad { get; set; } = false;

        // ── Editing / Pages ──────────────────────────────────

        [SettingPropertyBool(
            "Enable Hero Editing",
            HintText = "Allow editing descriptions on Hero encyclopedia pages.",
            Order = 0,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Pages", GroupOrder = 2)]
        public bool EnableHeroEditing { get; set; } = true;

        [SettingPropertyBool(
            "Enable Name Editing (Ctrl+N)",
            HintText = "Allow editing names on encyclopedia pages using Ctrl+N. Works on Hero (name + title), Clan, Kingdom, and Settlement pages.",
            Order = 0,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public bool EnableHeroNameEditing { get; set; } = true;

        [SettingPropertyInteger(
            "Max Name Length",
            10, 200,
            HintText = "Maximum number of characters allowed when editing names or titles via Ctrl+N. Shorter limits prevent UI overflow.",
            Order = 0,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public int MaxNameLength { get; set; } = 100;

        [SettingPropertyBool(
            "Enable Clan Editing",
            HintText = "Allow editing descriptions on Clan encyclopedia pages.",
            Order = 1,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Pages", GroupOrder = 2)]
        public bool EnableClanEditing { get; set; } = true;

        [SettingPropertyBool(
            "Enable Kingdom Editing",
            HintText = "Allow editing descriptions on Kingdom/Faction encyclopedia pages.",
            Order = 2,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Pages", GroupOrder = 2)]
        public bool EnableKingdomEditing { get; set; } = true;

        [SettingPropertyBool(
            "Enable Settlement Editing",
            HintText = "Allow editing descriptions on Settlement encyclopedia pages.",
            Order = 3,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Pages", GroupOrder = 2)]
        public bool EnableSettlementEditing { get; set; } = true;

        [SettingPropertyBool(
            "Override Layout Mod Compat",
            HintText = "If a known conflicting layout mod (e.g. Realm of Thrones) is detected at startup, EE skips settlement encyclopedia injection by default to avoid widget overlap. Enable this to force EE injection on anyway - only useful if you have manually verified compatibility.",
            Order = 4,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Pages", GroupOrder = 2)]
        public bool OverrideLayoutModCompat { get; set; } = false;

        [SettingPropertyBool(
            "Enable Banner/Flag Editing",
            HintText = "Allow editing banners/flags on encyclopedia pages using Ctrl+B. Paste a banner code to change the banner for Heroes, Clans, Kingdoms, or Settlements.",
            Order = 1,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public bool EnableBannerEditing { get; set; } = true;

        [SettingPropertyBool(
            "Enable Culture Editing",
            HintText = "Allow changing a hero's culture on Hero encyclopedia pages using Ctrl+U. You can select from existing cultures or create a custom one.",
            Order = 2,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public bool EnableCultureEditing { get; set; } = true;

        [SettingPropertyBool(
            "Enable Occupation Editing",
            HintText = "Allow changing a hero's occupation on Hero encyclopedia pages using Ctrl+O. Choose from available occupations like Lord, Wanderer, Merchant, etc.",
            Order = 3,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public bool EnableOccupationEditing { get; set; } = true;

        [SettingPropertyBool(
            "Enable Tag Editing",
            HintText = "Allow adding player-defined tags (e.g. ally, enemy, target) to any encyclopedia entry using Ctrl+G. Tags are displayed on the page and included in exports.",
            Order = 4,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public bool EnableTagEditing { get; set; } = true;

        [SettingPropertyInteger(
            "Tag Font Scale (%)",
            50, 300,
            HintText = "Scales the font size of the tag display on encyclopedia pages as a percentage. 100 = normal, 130 = default (1.3x), 200 = double size.",
            Order = 5,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public int TagFontScalePercent { get; set; } = DefaultTagFontScalePercent;

        [SettingPropertyBool(
            "Enable Auto-Tags",
            HintText = "Automatically generate tags for heroes based on game state (relation, party size, clan wealth). Auto-tags appear alongside manual tags with an 'Auto:' prefix and are recalculated daily.",
            Order = 6,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public bool EnableAutoTags { get; set; } = true;

        [SettingPropertyInteger(
            "Auto-Tag Enemy Relation",
            -100, 0,
            HintText = "Heroes with relation at or below this threshold get an 'Auto: Enemy' tag. Default: -30.",
            Order = 7,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public int AutoTagEnemyRelationThreshold { get; set; } = DefaultAutoTagEnemyRelation;

        [SettingPropertyInteger(
            "Auto-Tag Friend Relation",
            0, 100,
            HintText = "Heroes with relation at or above this threshold get an 'Auto: Friend' tag. Default: 30.",
            Order = 8,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public int AutoTagFriendRelationThreshold { get; set; } = DefaultAutoTagFriendRelation;

        [SettingPropertyInteger(
            "Auto-Tag Dangerous Party Size",
            50, 1000,
            HintText = "Heroes leading parties with at least this many troops get an 'Auto: Dangerous' tag. Default: 200.",
            Order = 9,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public int AutoTagDangerousPartySize { get; set; } = DefaultAutoTagDangerousParty;

        [SettingPropertyBool(
            "Enable Journal",
            HintText = "Allow adding chronicle notes (campaign diary) to any encyclopedia entry using Ctrl+J. Notes are displayed in a 'Journal' section on the page.",
            Order = 10,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public bool EnableJournal { get; set; } = true;

        [SettingPropertyBool(
            "Enable Auto-Chronicle",
            HintText = "Automatically log game events (battles, diplomacy, tournaments, captures, etc.) as chronicle notes on relevant encyclopedia entries.",
            Order = 5,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public bool EnableAutoJournal { get; set; } = true;

        [SettingPropertyBool(
            "Enable Global Chronicle Panel",
            HintText = "Show a 'Chronicle Notes' button on the campaign map that opens a panel with all world history events (war declarations, peace treaties, political changes, etc.).",
            Order = 5,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public bool EnableGlobalChronicle { get; set; } = true;

        [SettingPropertyBool(
            "Enable Relation Notes",
            HintText = "Allow writing personal notes about hero relationships using Ctrl+F. Notes appear in the Friends/Enemies section on hero encyclopedia pages.",
            Order = 11,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public bool EnableRelationNotes { get; set; } = true;

        [SettingPropertyBool(
            "Enable Timeline",
            HintText = "Show a 'Timeline' section on hero encyclopedia pages with a chronological personal biography of battles, captures, family events, and manual notes.",
            Order = 12,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public bool EnableTimeline { get; set; } = true;

        [SettingPropertyInteger(
            "Timeline Page Size",
            5, 100,
            HintText = "Number of timeline entries shown per page. Lower values improve performance for heroes with many events. Default: 25.",
            Order = 13,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public int TimelinePageSize { get; set; } = DefaultTimelinePageSize;

        [SettingPropertyBool(
            "Enable Lore Section",
            HintText = "Show the collapsible 'Lore' section on hero encyclopedia pages displaying narrative fields (Backstory, Personality, Goals, Relationships, Rumors). Disable to hide the Lore section entirely.",
            Order = 14,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public bool EnableLoreSection { get; set; } = true;

        [SettingPropertyBool(
            "Enable Inventory Lore",
            HintText = "Show the collapsible 'Inventory Lore' section on encyclopedia pages, listing carried items as a native icon grid with generated lore for each item. Disable to hide the section entirely.",
            Order = 15,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public bool EnableInventoryLore { get; set; } = true;

        [SettingPropertyBool(
            "Enable Info Stats",
            HintText = "Show the 'Info' stats panel on encyclopedia pages (Hero: Culture, Troops, Kills, Hall Rank, etc. | Clan: Renown, Troops, etc. | Kingdom: Clans, Wars, etc. | Settlement: Prosperity, Loyalty, etc.).",
            Order = 16,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public bool EnableInfoStats { get; set; } = true;

        [SettingPropertyBool(
            "Show Edit Popup Portrait",
            HintText = "Display a hero face portrait or clan/kingdom banner emblem in the edit popup title area. Disable for a simpler popup appearance.",
            Order = 17,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public bool ShowEditPopupPortrait { get; set; } = true;

        [SettingPropertyInteger(
            "Journal Page Size",
            5, 50,
            HintText = "Number of journal/chronicle entries shown per page on encyclopedia pages. Default: 10.",
            Order = 18,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public int JournalPageSize { get; set; } = DefaultJournalPageSize;

        [SettingPropertyInteger(
            "Chronicle Panel Page Size",
            5, 50,
            HintText = "Number of entries shown per page in the Global Chronicle Panel on the campaign map. Default: 22.",
            Order = 19,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public int ChroniclePageSize { get; set; } = DefaultChroniclePageSize;

        [SettingPropertyBool(
            "Show Journal Hint",
            HintText = "Show 'J Journal' in the keyboard shortcut hint on encyclopedia pages.",
            Order = 6,
            RequireRestart = false)]
        [SettingPropertyGroup("2. General/Hints", GroupOrder = 1)]
        public bool ShowJournalHint { get; set; } = true;

        [SettingPropertyBool(
            "Show Relation Note Hint",
            HintText = "Show 'F Friend Note' in the keyboard shortcut hint on hero encyclopedia pages.",
            Order = 7,
            RequireRestart = false)]
        [SettingPropertyGroup("2. General/Hints", GroupOrder = 1)]
        public bool ShowRelationNoteHint { get; set; } = true;

        [SettingPropertyBool(
            "Prevent Kingdom Color Overwrites",
            HintText = "In 1.3.13, the game's kingdom logic frequently forces banners back to default culture colors. Enable this to periodically re-apply your custom banners when the game overwrites them.",
            Order = 7,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public bool PreventKingdomColorOverwrites { get; set; } = true;

        [SettingPropertyButton(
            "Create Custom Banner",
            Content = "Open Banner Editor",
            HintText = "Opens bannerlord.party/banner in your browser where you can design a custom banner and copy its code. Then use Ctrl+B in the encyclopedia to paste it.",
            Order = 7,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Features", GroupOrder = 2)]
        public Action OpenBannerEditor
        {
            get => () =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = BannerEditorUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex) { MCMSettings.DebugLog("MCMSettings: Failed to open banner editor: " + ex.ToString()); }
            };
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyButton(
            "Manage Custom Cultures",
            Content = "Delete Cultures",
            HintText = "View and delete custom culture assignments. Shows a list of heroes with custom cultures — click to delete and restore to original.",
            Order = 0,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Management", GroupOrder = 2)]
        public Action ManageCustomCultures
        {
            get => () => HandleManageCustomCultures();
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyButton(
            "Manage Custom Occupations",
            Content = "Delete Occupations",
            HintText = "View and delete custom occupation assignments. Shows a list of heroes with custom occupations — click to delete and restore to original.",
            Order = 1,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Management", GroupOrder = 2)]
        public Action ManageCustomOccupations
        {
            get => () => HandleManageCustomOccupations();
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyButton(
            "Manage Tags",
            Content = "Manage Tags",
            HintText = "View, rename, and delete tags across all encyclopedia entries. Allows bulk tag management from the settings menu.",
            Order = 2,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Management", GroupOrder = 2)]
        public Action ManageTags
        {
            get => () => HandleManageTags();
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyButton(
            "Filter by Tag",
            Content = "Search Tags",
            HintText = "Search for all encyclopedia entries that have a specific tag. Shows matching heroes, clans, kingdoms, and settlements.",
            Order = 3,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Management", GroupOrder = 2)]
        public Action FilterByTag
        {
            get => () => HandleFilterByTag();
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyButton(
            "Manage Tag Presets",
            Content = "Presets",
            HintText = "Save and load tag presets (templates). A preset is a named collection of tags that can be quickly applied to any encyclopedia entry.",
            Order = 4,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Management", GroupOrder = 2)]
        public Action ManageTagPresets
        {
            get => () => HandleManageTagPresets();
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyButton(
            "Manage Tag Categories",
            Content = "Categories",
            HintText = "Create and manage custom tag categories. Categories group tags in the Quick Tag Menu and tag display.",
            Order = 5,
            RequireRestart = false)]
        [SettingPropertyGroup("3. Editing/Management", GroupOrder = 2)]
        public Action ManageTagCategories
        {
            get => () => HandleManageTagCategories();
            set { /* MCM requires a setter, ignore */ }
        }

        private static void HandleManageTags()
        {
            try
            {
                var behavior = EncyclopediaEditBehavior.Instance;
                if (behavior == null)
                {
                    InformationManager.DisplayMessage(
                        new InformationMessage(Localization.L("msg_no_campaign"), Colors.Red));
                    return;
                }

                var uniqueTags = behavior.GetAllUniqueTags();
                int taggedEntries = behavior.GetTagCount();
                if (uniqueTags.Count == 0)
                {
                    InformationManager.ShowInquiry(
                        new InquiryData(
                            Localization.L("manage_tags_title"),
                            Localization.L("manage_tags_empty"),
                            true, false,
                            Localization.L("stats_ok"),
                            null, null, null),
                        false, false);
                    return;
                }

                // Show summary and options
                string tagList = string.Join(", ", uniqueTags);
                InformationManager.ShowInquiry(
                    new InquiryData(
                        Localization.L("manage_tags_title"),
                        Localization.L("manage_tags_summary", uniqueTags.Count, taggedEntries, tagList),
                        true, true,
                        Localization.L("manage_tags_clear_all"),
                        Localization.L("manage_tags_rename_delete"),
                        delegate()
                        {
                            // Confirm clear all tags
                            InformationManager.ShowInquiry(
                                new InquiryData(
                                    Localization.L("manage_tags_title"),
                                    Localization.L("manage_tags_clear_confirm", taggedEntries),
                                    true, true,
                                    Localization.L("manage_delete"),
                                    Localization.L("edit_cancel"),
                                    delegate()
                                    {
                                        int cleared = behavior.ClearAllTags();
                                        InformationManager.DisplayMessage(
                                            new InformationMessage(
                                                Localization.L("manage_tags_cleared", cleared), Colors.Green));
                                    },
                                    null),
                                false, false);
                        },
                        delegate()
                        {
                            ShowTagManagementCarousel(uniqueTags, 0);
                        }),
                    false, false);
            }
            catch (Exception ex)
            {
                DebugLog("ManageTags error: " + ex.ToString());
            }
        }

        private static void ShowTagManagementCarousel(List<string> tags, int index)
        {
            if (index >= tags.Count)
            {
                InformationManager.DisplayMessage(
                    new InformationMessage(Localization.L("manage_done"), Colors.Green));
                return;
            }

            string tag = tags[index];
            var behavior = EncyclopediaEditBehavior.Instance;
            int usageCount = behavior?.GetTagUsageCount(tag) ?? 0;
            int remaining = tags.Count - index - 1;

            // Show tag with category info
            string category = behavior?.GetTagCategory(tag);
            string categoryInfo = !string.IsNullOrEmpty(category) ? " [" + category + "]" : "";

            string nextLabel = remaining > 0
                ? Localization.L("carousel_next", remaining)
                : Localization.L("edit_cancel");

            InformationManager.ShowInquiry(
                new InquiryData(
                    Localization.L("manage_tags_title"),
                    Localization.L("manage_tags_entry", tag + categoryInfo, usageCount, index + 1, tags.Count)
                        + "\n\n" + Localization.L("manage_tags_actions"),
                    true, true,
                    Localization.L("manage_tags_delete_tag"),
                    nextLabel,
                    delegate()
                    {
                        // Show sub-menu: Delete or Rename
                        InformationManager.ShowInquiry(
                            new InquiryData(
                                Localization.L("manage_tags_title"),
                                Localization.L("manage_tags_action_choose", tag, usageCount),
                                true, true,
                                Localization.L("manage_tags_delete_tag"),
                                Localization.L("manage_tags_rename_tag"),
                                delegate()
                                {
                                    try
                                    {
                                        int removed = behavior.RemoveTagGlobal(tag);
                                        DebugLog("Deleted tag '" + tag + "' from " + removed + " entries");
                                        InformationManager.DisplayMessage(
                                            new InformationMessage(
                                                Localization.L("tag_removed_global", tag, removed), Colors.Green));
                                    }
                                    catch (Exception ex)
                                    {
                                        DebugLog("Failed to delete tag: " + ex.ToString());
                                    }

                                    tags.RemoveAt(index);
                                    // Wrap to 0 when the last item was deleted (index == new Count).
                                    if (tags.Count > 0)
                                        ShowTagManagementCarousel(tags, index < tags.Count ? index : 0);
                                },
                                delegate()
                                {
                                    // Rename tag
                                    ShowRenameTagInput(tag, tags, index, behavior);
                                }),
                            false, false);
                    },
                    delegate()
                    {
                        if (remaining > 0)
                            ShowTagManagementCarousel(tags, index + 1);
                    }),
                false);
        }

        private static void ShowRenameTagInput(string oldTag, List<string> tags, int index, EncyclopediaEditBehavior behavior)
        {
            EEInquiry.Show(
                new TextInquiryData(
                    Localization.L("manage_tags_title"),
                    Localization.L("manage_tags_rename_prompt", oldTag),
                    true, true,
                    Localization.L("edit_done"),
                    Localization.L("edit_cancel"),
                    delegate(string newTag)
                    {
                        try
                        {
                            newTag = newTag?.Trim();
                            if (!string.IsNullOrEmpty(newTag))
                            {
                                int renamed = behavior.RenameTagGlobal(oldTag, newTag);
                                InformationManager.DisplayMessage(
                                    new InformationMessage(
                                        Localization.L("tag_renamed_global", oldTag, newTag, renamed), Colors.Green));
                                // Update the list to reflect the rename
                                tags[index] = newTag;
                            }
                        }
                        catch (Exception ex)
                        {
                            DebugLog("Tag rename error: " + ex.ToString());
                        }
                        ShowTagManagementCarousel(tags, index);
                    },
                    delegate()
                    {
                        ShowTagManagementCarousel(tags, index);
                    },
                    false, null),
                false, false);
        }

        // ── Tag Filtering ──

        private static void HandleFilterByTag()
        {
            try
            {
                var behavior = EncyclopediaEditBehavior.Instance;
                if (behavior == null)
                {
                    InformationManager.DisplayMessage(
                        new InformationMessage(Localization.L("msg_no_campaign"), Colors.Red));
                    return;
                }

                var uniqueTags = behavior.GetAllUniqueTags();
                if (uniqueTags.Count == 0)
                {
                    InformationManager.ShowInquiry(
                        new InquiryData(
                            Localization.L("tag_filter_title"),
                            Localization.L("manage_tags_empty"),
                            true, false,
                            Localization.L("stats_ok"),
                            null, null, null),
                        false, false);
                    return;
                }

                // Ask user to type a tag name to filter by
                string tagList = string.Join(", ", uniqueTags);
                EEInquiry.Show(
                    new TextInquiryData(
                        Localization.L("tag_filter_title"),
                        Localization.L("tag_filter_prompt", tagList),
                        true, true,
                        Localization.L("tag_filter_search"),
                        Localization.L("edit_cancel"),
                        delegate(string input)
                        {
                            try
                            {
                                string tag = input?.Trim();
                                if (string.IsNullOrEmpty(tag)) return;

                                var results = behavior.GetObjectsWithTagDetailed(tag);
                                if (results.Count == 0)
                                {
                                    InformationManager.DisplayMessage(
                                        new InformationMessage(
                                            Localization.L("tag_filter_no_results", tag),
                                            Colors.Yellow));
                                    return;
                                }

                                // Build results summary
                                var sb = new System.Text.StringBuilder();
                                sb.AppendLine(Localization.L("tag_filter_results_header", tag, results.Count));
                                int heroCount = 0, clanCount = 0, kingdomCount = 0, settlementCount = 0;
                                foreach (var r in results)
                                {
                                    sb.AppendLine("  " + r.Item3 + ": " + r.Item2);
                                    switch (r.Item3)
                                    {
                                        case "Hero": heroCount++; break;
                                        case "Clan": clanCount++; break;
                                        case "Kingdom": kingdomCount++; break;
                                        case "Settlement": settlementCount++; break;
                                    }
                                }
                                string summary = Localization.L("tag_filter_summary",
                                    results.Count, heroCount, clanCount, kingdomCount, settlementCount);
                                sb.Insert(0, summary + "\n\n");

                                InformationManager.ShowInquiry(
                                    new InquiryData(
                                        Localization.L("tag_filter_title") + " - " + tag,
                                        sb.ToString(),
                                        true, false,
                                        Localization.L("stats_ok"),
                                        null, null, null),
                                    false, false);
                            }
                            catch (Exception ex)
                            {
                                DebugLog("Tag filter error: " + ex.ToString());
                            }
                        },
                        delegate { },
                        false, null),
                    false, false);
            }
            catch (Exception ex)
            {
                DebugLog("HandleFilterByTag error: " + ex.ToString());
            }
        }

        // ── Tag Presets Management ──

        private static void HandleManageTagPresets()
        {
            try
            {
                var behavior = EncyclopediaEditBehavior.Instance;
                if (behavior == null)
                {
                    InformationManager.DisplayMessage(
                        new InformationMessage(Localization.L("msg_no_campaign"), Colors.Red));
                    return;
                }

                var presets = behavior.GetAllTagPresets();

                if (presets.Count == 0)
                {
                    // No presets yet — offer to create one
                    InformationManager.ShowInquiry(
                        new InquiryData(
                            Localization.L("tag_preset_title"),
                            Localization.L("tag_preset_empty"),
                            true, true,
                            Localization.L("tag_preset_create"),
                            Localization.L("edit_cancel"),
                            delegate() { ShowCreateTagPresetInput(behavior); },
                            null),
                        false, false);
                    return;
                }

                // Show presets summary with create/browse options
                var presetNames = new List<string>(presets.Keys);
                string presetList = "";
                foreach (var name in presetNames)
                    presetList += "\n  " + name + ": " + presets[name];

                InformationManager.ShowInquiry(
                    new InquiryData(
                        Localization.L("tag_preset_title"),
                        Localization.L("tag_preset_summary", presets.Count, presetList),
                        true, true,
                        Localization.L("tag_preset_create"),
                        Localization.L("tag_preset_browse"),
                        delegate() { ShowCreateTagPresetInput(behavior); },
                        delegate() { ShowTagPresetCarousel(presetNames, behavior, 0); }),
                    false, false);
            }
            catch (Exception ex)
            {
                DebugLog("HandleManageTagPresets error: " + ex.ToString());
            }
        }

        private static void ShowCreateTagPresetInput(EncyclopediaEditBehavior behavior)
        {
            EEInquiry.Show(
                new TextInquiryData(
                    Localization.L("tag_preset_title"),
                    Localization.L("tag_preset_create_prompt"),
                    true, true,
                    Localization.L("edit_done"),
                    Localization.L("edit_cancel"),
                    delegate(string input)
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(input)) return;

                            int colonIdx = input.IndexOf(':');
                            if (colonIdx <= 0 || colonIdx + 1 > input.Length)
                            {
                                InformationManager.DisplayMessage(
                                    new InformationMessage(Localization.L("tag_preset_invalid_format"), Colors.Red));
                                return;
                            }

                            string name = input.Substring(0, colonIdx).Trim();
                            string tags = colonIdx + 1 < input.Length ? input.Substring(colonIdx + 1).Trim() : "";

                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(tags))
                            {
                                InformationManager.DisplayMessage(
                                    new InformationMessage(Localization.L("tag_preset_invalid_format"), Colors.Red));
                                return;
                            }

                            behavior.SetTagPreset(name, tags);
                            InformationManager.DisplayMessage(
                                new InformationMessage(
                                    Localization.L("tag_preset_saved", name, tags), Colors.Green));
                        }
                        catch (Exception ex)
                        {
                            DebugLog("Create preset error: " + ex.ToString());
                        }
                    },
                    delegate { },
                    false, null),
                false, false);
        }

        private static void ShowTagPresetCarousel(List<string> presetNames, EncyclopediaEditBehavior behavior, int index)
        {
            if (index >= presetNames.Count)
            {
                InformationManager.DisplayMessage(
                    new InformationMessage(Localization.L("manage_done"), Colors.Green));
                return;
            }

            string name = presetNames[index];
            string tags = behavior.GetTagPreset(name) ?? "";
            int remaining = presetNames.Count - index - 1;

            string nextLabel = remaining > 0
                ? Localization.L("carousel_next", remaining)
                : Localization.L("edit_cancel");

            InformationManager.ShowInquiry(
                new InquiryData(
                    Localization.L("tag_preset_title"),
                    Localization.L("tag_preset_entry", name, tags, index + 1, presetNames.Count),
                    true, true,
                    Localization.L("tag_preset_delete"),
                    nextLabel,
                    delegate()
                    {
                        behavior.RemoveTagPreset(name);
                        InformationManager.DisplayMessage(
                            new InformationMessage(
                                Localization.L("tag_preset_deleted", name), Colors.Yellow));
                        presetNames.RemoveAt(index);
                        // Wrap to 0 when the last item was deleted (index == new Count).
                        if (presetNames.Count > 0)
                            ShowTagPresetCarousel(presetNames, behavior, index < presetNames.Count ? index : 0);
                    },
                    delegate()
                    {
                        if (remaining > 0)
                            ShowTagPresetCarousel(presetNames, behavior, index + 1);
                    }),
                false);
        }

        // ── Tag Categories Management ──

        private static void HandleManageTagCategories()
        {
            try
            {
                var behavior = EncyclopediaEditBehavior.Instance;
                if (behavior == null)
                {
                    InformationManager.DisplayMessage(
                        new InformationMessage(Localization.L("msg_no_campaign"), Colors.Red));
                    return;
                }

                var categories = behavior.GetAllTagCategories();

                if (categories.Count == 0)
                {
                    InformationManager.ShowInquiry(
                        new InquiryData(
                            Localization.L("tag_category_title"),
                            Localization.L("tag_category_empty"),
                            true, true,
                            Localization.L("tag_category_create"),
                            Localization.L("edit_cancel"),
                            delegate() { ShowCreateTagCategoryInput(behavior); },
                            null),
                        false, false);
                    return;
                }

                var categoryNames = new List<string>(categories.Keys);
                string categoryList = "";
                foreach (var name in categoryNames)
                    categoryList += "\n  " + name + ": " + categories[name];

                InformationManager.ShowInquiry(
                    new InquiryData(
                        Localization.L("tag_category_title"),
                        Localization.L("tag_category_summary", categories.Count, categoryList),
                        true, true,
                        Localization.L("tag_category_create"),
                        Localization.L("tag_category_browse"),
                        delegate() { ShowCreateTagCategoryInput(behavior); },
                        delegate() { ShowTagCategoryCarousel(categoryNames, behavior, 0); }),
                    false, false);
            }
            catch (Exception ex)
            {
                DebugLog("HandleManageTagCategories error: " + ex.ToString());
            }
        }

        private static void ShowCreateTagCategoryInput(EncyclopediaEditBehavior behavior)
        {
            EEInquiry.Show(
                new TextInquiryData(
                    Localization.L("tag_category_title"),
                    Localization.L("tag_category_create_prompt"),
                    true, true,
                    Localization.L("edit_done"),
                    Localization.L("edit_cancel"),
                    delegate(string input)
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(input)) return;

                            // Format: "CategoryName: tag1, tag2, tag3"
                            int colonIdx = input.IndexOf(':');
                            if (colonIdx <= 0)
                            {
                                InformationManager.DisplayMessage(
                                    new InformationMessage(Localization.L("tag_category_invalid_format"), Colors.Red));
                                return;
                            }

                            string name = colonIdx <= input.Length ? input.Substring(0, colonIdx).Trim() : "";
                            string tags = colonIdx + 1 < input.Length ? input.Substring(colonIdx + 1).Trim() : "";

                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(tags))
                            {
                                InformationManager.DisplayMessage(
                                    new InformationMessage(Localization.L("tag_category_invalid_format"), Colors.Red));
                                return;
                            }

                            behavior.SetTagCategory(name, tags);
                            InformationManager.DisplayMessage(
                                new InformationMessage(
                                    Localization.L("tag_category_saved", name, tags), Colors.Green));
                        }
                        catch (Exception ex)
                        {
                            DebugLog("Create category error: " + ex.ToString());
                        }
                    },
                    delegate { },
                    false, null),
                false, false);
        }

        private static void ShowTagCategoryCarousel(List<string> categoryNames, EncyclopediaEditBehavior behavior, int index)
        {
            if (index >= categoryNames.Count)
            {
                InformationManager.DisplayMessage(
                    new InformationMessage(Localization.L("manage_done"), Colors.Green));
                return;
            }

            string name = categoryNames[index];
            var categories = behavior.GetAllTagCategories();
            string tags = categories.ContainsKey(name) ? categories[name] : "";
            int remaining = categoryNames.Count - index - 1;

            string nextLabel = remaining > 0
                ? Localization.L("carousel_next", remaining)
                : Localization.L("edit_cancel");

            InformationManager.ShowInquiry(
                new InquiryData(
                    Localization.L("tag_category_title"),
                    Localization.L("tag_category_entry", name, tags, index + 1, categoryNames.Count),
                    true, true,
                    Localization.L("tag_category_delete"),
                    nextLabel,
                    delegate()
                    {
                        behavior.RemoveTagCategory(name);
                        InformationManager.DisplayMessage(
                            new InformationMessage(
                                Localization.L("tag_category_deleted", name), Colors.Yellow));
                        categoryNames.RemoveAt(index);
                        // Wrap to 0 when the last item was deleted (index == new Count).
                        if (categoryNames.Count > 0)
                            ShowTagCategoryCarousel(categoryNames, behavior, index < categoryNames.Count ? index : 0);
                    },
                    delegate()
                    {
                        if (remaining > 0)
                            ShowTagCategoryCarousel(categoryNames, behavior, index + 1);
                    }),
                false);
        }

        private static void HandleManageCustomCultures()
        {
            try
            {
                var behavior = EncyclopediaEditBehavior.Instance;
                if (behavior == null)
                {
                    InformationManager.DisplayMessage(
                        new InformationMessage(Localization.L("msg_no_campaign"), Colors.Red));
                    return;
                }

                var entries = behavior.GetAllCustomCultureEntries();
                if (entries.Count == 0)
                {
                    InformationManager.ShowInquiry(
                        new InquiryData(
                            Localization.L("manage_cultures_title"),
                            Localization.L("manage_cultures_empty"),
                            true, false,
                            Localization.L("stats_ok"),
                            null, null, null),
                        false, false);
                    return;
                }

                InformationManager.ShowInquiry(
                    new InquiryData(
                        Localization.L("manage_cultures_title"),
                        Localization.L("manage_cultures_prompt", entries.Count),
                        true, true,
                        Localization.L("manage_delete_all"),
                        Localization.L("manage_browse_one"),
                        delegate() { ConfirmDeleteAllCultures(behavior, entries); },
                        delegate() { ShowDeleteCultureCarousel(entries, 0); }),
                    false, false);
            }
            catch (Exception ex)
            {
                DebugLog("ManageCustomCultures error: " + ex.ToString());
            }
        }

        private static void ConfirmDeleteAllCultures(EncyclopediaEditBehavior behavior, List<Tuple<string, string, string>> entries)
        {
            InformationManager.ShowInquiry(
                new InquiryData(
                    Localization.L("manage_cultures_title"),
                    Localization.L("manage_delete_all_confirm", entries.Count),
                    true, true,
                    Localization.L("manage_delete"),
                    Localization.L("edit_cancel"),
                    delegate()
                    {
                        try
                        {
                            int deleted = 0;
                            foreach (var e in entries)
                            {
                                try
                                {
                                    RestoreHeroCulture(e.Item1, behavior);
                                    behavior.DeleteCustomCultureFull(e.Item1);
                                    deleted++;
                                }
                                catch (Exception ex) { MCMSettings.DebugLog("MCMSettings: Failed to delete culture for " + e.Item1 + ": " + ex.ToString()); }
                            }
                            DebugLog("Bulk deleted " + deleted + " custom cultures");
                            InformationManager.DisplayMessage(
                                new InformationMessage(
                                    Localization.L("manage_cultures_all_deleted", deleted), Colors.Green));
                        }
                        catch (Exception ex) { DebugLog("Bulk delete cultures failed: " + ex.ToString()); }
                    },
                    null),
                false, false);
        }

        // Reflection handles for Hero/CharacterObject restoration paths. Resolved once
        // (via Lazy<T>) and cached because Bannerlord doesn't change these at runtime;
        // caching saves a ~10-20 µs GetProperty/GetField walk on every restore-original click.
        // Project convention (CLAUDE.md): cache reflection lookups in static fields.
        private const System.Reflection.BindingFlags _restoreReflFlags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        private static readonly Lazy<System.Reflection.PropertyInfo> _heroCultureProp =
            new Lazy<System.Reflection.PropertyInfo>(
                () => typeof(Hero).GetProperty("Culture", _restoreReflFlags));
        private static readonly Lazy<System.Reflection.FieldInfo> _heroCultureField =
            new Lazy<System.Reflection.FieldInfo>(
                () => typeof(Hero).GetField("_culture", _restoreReflFlags)
                   ?? typeof(Hero).GetField("<Culture>k__BackingField", _restoreReflFlags));
        private static readonly Lazy<System.Reflection.PropertyInfo> _heroOccupationProp =
            new Lazy<System.Reflection.PropertyInfo>(
                () => typeof(Hero).GetProperty("Occupation", _restoreReflFlags));
        private static readonly Lazy<System.Reflection.FieldInfo> _heroOccupationField =
            new Lazy<System.Reflection.FieldInfo>(
                () => typeof(Hero).GetField("_occupation", _restoreReflFlags)
                   ?? typeof(Hero).GetField("<Occupation>k__BackingField", _restoreReflFlags));
        private static readonly Lazy<System.Reflection.PropertyInfo> _charCultureProp =
            new Lazy<System.Reflection.PropertyInfo>(
                () => typeof(CharacterObject).GetProperty("Culture", _restoreReflFlags));
        private static readonly Lazy<System.Reflection.FieldInfo> _charCultureField =
            new Lazy<System.Reflection.FieldInfo>(
                () => typeof(CharacterObject).GetField("_culture", _restoreReflFlags)
                   ?? typeof(CharacterObject).GetField("<Culture>k__BackingField", _restoreReflFlags));
        private static readonly Lazy<System.Reflection.PropertyInfo> _charOccupationProp =
            new Lazy<System.Reflection.PropertyInfo>(
                () => typeof(CharacterObject).GetProperty("Occupation", _restoreReflFlags));
        private static readonly Lazy<System.Reflection.FieldInfo> _charOccupationField =
            new Lazy<System.Reflection.FieldInfo>(
                () => typeof(CharacterObject).GetField("_occupation", _restoreReflFlags)
                   ?? typeof(CharacterObject).GetField("<Occupation>k__BackingField", _restoreReflFlags));

        private static void RestoreHeroCulture(string heroId, EncyclopediaEditBehavior behavior)
        {
            var hero = Hero.FindFirst(h => h != null && h.StringId == heroId);
            if (hero == null) return;

            string origId = behavior.GetOriginalCulture(heroId);
            if (string.IsNullOrEmpty(origId)) return;

            CultureObject origCulture = null;
            foreach (var c in TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObjectTypeList<CultureObject>())
            {
                if (c != null && string.Equals(c.StringId, origId, StringComparison.OrdinalIgnoreCase))
                { origCulture = c; break; }
            }
            if (origCulture == null) return;

            if (hero.CharacterObject != null)
            {
                var cp = _charCultureProp.Value;
                if (cp != null && cp.CanWrite)
                    cp.SetValue(hero.CharacterObject, origCulture);
                else
                {
                    var cf = _charCultureField.Value;
                    if (cf == null)
                    {
                        MCMSettings.DebugLog("MCMSettings: CharacterObject Culture field not found via reflection");
                    }
                    else if (cf.FieldType == typeof(CultureObject))
                        cf.SetValue(hero.CharacterObject, origCulture);
                }
            }
            var hp = _heroCultureProp.Value;
            if (hp != null && hp.CanWrite) hp.SetValue(hero, origCulture);
            else
            {
                var hf = _heroCultureField.Value;
                if (hf == null)
                {
                    MCMSettings.DebugLog("MCMSettings: Hero Culture field not found via reflection");
                    return;
                }
                if (hf.FieldType == typeof(CultureObject))
                    hf.SetValue(hero, origCulture);
            }
        }

        private static void RestoreHeroOccupation(string heroId, EncyclopediaEditBehavior behavior)
        {
            var hero = Hero.FindFirst(h => h != null && h.StringId == heroId);
            if (hero == null || hero.CharacterObject == null) return;

            int origOcc = behavior.GetOriginalOccupation(heroId);
            if (origOcc < 0) return;

            var op = _charOccupationProp.Value;
            if (op != null && op.CanWrite)
                op.SetValue(hero.CharacterObject, (Occupation)origOcc);
            else
            {
                var of = _charOccupationField.Value;
                if (of == null)
                    MCMSettings.DebugLog("MCMSettings: CharacterObject Occupation field not found via reflection");
                else
                    of.SetValue(hero.CharacterObject, (Occupation)origOcc);
            }
            try
            {
                var hp = _heroOccupationProp.Value;
                if (hp != null && hp.CanWrite) hp.SetValue(hero, (Occupation)origOcc);
                else
                {
                    var hf = _heroOccupationField.Value;
                    if (hf == null)
                    {
                        MCMSettings.DebugLog("MCMSettings: Hero Occupation field not found via reflection");
                        return;
                    }
                    if (hf.FieldType == typeof(Occupation))
                        hf.SetValue(hero, (Occupation)origOcc);
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("MCMSettings: Failed to restore hero occupation via reflection: " + ex.ToString()); }
        }

        private static void HandleManageCustomOccupations()
        {
            try
            {
                var behavior = EncyclopediaEditBehavior.Instance;
                if (behavior == null)
                {
                    InformationManager.DisplayMessage(
                        new InformationMessage(Localization.L("msg_no_campaign"), Colors.Red));
                    return;
                }

                var entries = behavior.GetAllCustomOccupationEntries();
                if (entries.Count == 0)
                {
                    InformationManager.ShowInquiry(
                        new InquiryData(
                            Localization.L("manage_occupations_title"),
                            Localization.L("manage_occupations_empty"),
                            true, false,
                            Localization.L("stats_ok"),
                            null, null, null),
                        false, false);
                    return;
                }

                InformationManager.ShowInquiry(
                    new InquiryData(
                        Localization.L("manage_occupations_title"),
                        Localization.L("manage_occupations_prompt", entries.Count),
                        true, true,
                        Localization.L("manage_delete_all"),
                        Localization.L("manage_browse_one"),
                        delegate() { ConfirmDeleteAllOccupations(behavior, entries); },
                        delegate() { ShowDeleteOccupationCarousel(entries, 0); }),
                    false, false);
            }
            catch (Exception ex)
            {
                DebugLog("ManageCustomOccupations error: " + ex.ToString());
            }
        }

        private static void ConfirmDeleteAllOccupations(EncyclopediaEditBehavior behavior, List<Tuple<string, int>> entries)
        {
            InformationManager.ShowInquiry(
                new InquiryData(
                    Localization.L("manage_occupations_title"),
                    Localization.L("manage_occupations_delete_all_confirm", entries.Count),
                    true, true,
                    Localization.L("manage_delete"),
                    Localization.L("edit_cancel"),
                    delegate()
                    {
                        try
                        {
                            int deleted = 0;
                            foreach (var e in entries)
                            {
                                try
                                {
                                    RestoreHeroOccupation(e.Item1, behavior);
                                    behavior.DeleteCustomOccupationFull(e.Item1);
                                    deleted++;
                                }
                                catch (Exception ex) { MCMSettings.DebugLog("MCMSettings: Failed to delete occupation for " + e.Item1 + ": " + ex.ToString()); }
                            }
                            DebugLog("Bulk deleted " + deleted + " custom occupations");
                            InformationManager.DisplayMessage(
                                new InformationMessage(
                                    Localization.L("manage_occupations_all_deleted", deleted), Colors.Green));
                        }
                        catch (Exception ex) { DebugLog("Bulk delete occupations failed: " + ex.ToString()); }
                    },
                    null),
                false, false);
        }

        private static void ShowDeleteCultureCarousel(List<Tuple<string, string, string>> entries, int index)
        {
            if (index >= entries.Count)
            {
                InformationManager.DisplayMessage(
                    new InformationMessage(Localization.L("manage_done"), Colors.Green));
                return;
            }

            var entry = entries[index];
            string heroId = entry.Item1;
            string cultureId = entry.Item2;
            string displayName = entry.Item3;

            // Try to find hero name
            string heroName = heroId;
            try
            {
                var hero = Hero.FindFirst(h => h != null && h.StringId == heroId);
                if (hero != null)
                    heroName = hero.Name?.ToString() ?? heroId;
            }
            catch (Exception ex) { MCMSettings.DebugLog("MCMSettings: Failed to resolve hero name for culture carousel: " + ex.ToString()); }

            string cultureName = !string.IsNullOrEmpty(displayName) ? displayName : cultureId;
            int remaining = entries.Count - index - 1;

            // Resolve original culture name for display
            string origCultureDisplay = "";
            try
            {
                var behavior = EncyclopediaEditBehavior.Instance;
                string origId = behavior?.GetOriginalCulture(heroId);
                if (!string.IsNullOrEmpty(origId))
                {
                    foreach (var c in TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObjectTypeList<CultureObject>())
                    {
                        if (c != null && string.Equals(c.StringId, origId, StringComparison.OrdinalIgnoreCase))
                        {
                            origCultureDisplay = c.Name?.ToString() ?? origId;
                            break;
                        }
                    }
                    if (string.IsNullOrEmpty(origCultureDisplay))
                        origCultureDisplay = origId;
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("MCMSettings: Failed to resolve original culture name: " + ex.ToString()); }

            string message = string.IsNullOrEmpty(origCultureDisplay)
                ? Localization.L("manage_culture_entry", heroName, cultureName, index + 1, entries.Count)
                : Localization.L("manage_culture_entry_with_original", heroName, cultureName, origCultureDisplay, index + 1, entries.Count);

            string nextLabel = remaining > 0
                ? Localization.L("carousel_next", remaining)
                : Localization.L("edit_cancel");

            InformationManager.ShowInquiry(
                new InquiryData(
                    Localization.L("manage_cultures_title"),
                    message,
                    true,
                    true,
                    Localization.L("manage_delete"),
                    nextLabel,
                    delegate()
                    {
                        try
                        {
                            var behavior = EncyclopediaEditBehavior.Instance;
                            if (behavior != null)
                                RestoreHeroCulture(heroId, behavior);
                            EncyclopediaEditBehavior.Instance?.DeleteCustomCultureFull(heroId);
                            DebugLog("Deleted custom culture for " + heroId);
                            InformationManager.DisplayMessage(
                                new InformationMessage(
                                    Localization.L("manage_culture_deleted", heroName, cultureName),
                                    Colors.Green));
                        }
                        catch (Exception ex)
                        {
                            DebugLog("Failed to delete culture: " + ex.ToString());
                        }

                        entries.RemoveAt(index);
                        // Wrap to 0 when the last item was deleted (index == new Count).
                        if (entries.Count > 0)
                            ShowDeleteCultureCarousel(entries, index < entries.Count ? index : 0);
                    },
                    delegate()
                    {
                        if (remaining > 0)
                            ShowDeleteCultureCarousel(entries, index + 1);
                    }
                ),
                false);
        }

        private static void ShowDeleteOccupationCarousel(List<Tuple<string, int>> entries, int index)
        {
            if (index >= entries.Count)
            {
                InformationManager.DisplayMessage(
                    new InformationMessage(Localization.L("manage_done"), Colors.Green));
                return;
            }

            var entry = entries[index];
            string heroId = entry.Item1;
            int occValue = entry.Item2;

            // Try to find hero name
            string heroName = heroId;
            try
            {
                var hero = Hero.FindFirst(h => h != null && h.StringId == heroId);
                if (hero != null)
                    heroName = hero.Name?.ToString() ?? heroId;
            }
            catch (Exception ex) { MCMSettings.DebugLog("MCMSettings: Failed to resolve hero name for occupation carousel: " + ex.ToString()); }

            string occName = EncyclopediaOccupationEditPopup.GetFriendlyName(occValue);

            int remaining = entries.Count - index - 1;

            // Resolve original occupation name for display
            string origOccDisplay = "";
            try
            {
                var behavior = EncyclopediaEditBehavior.Instance;
                int origOcc = behavior?.GetOriginalOccupation(heroId) ?? -1;
                if (origOcc >= 0)
                    origOccDisplay = EncyclopediaOccupationEditPopup.GetFriendlyName(origOcc);
            }
            catch (Exception ex) { MCMSettings.DebugLog("MCMSettings: Failed to resolve original occupation name: " + ex.ToString()); }

            string message = string.IsNullOrEmpty(origOccDisplay)
                ? Localization.L("manage_occupation_entry", heroName, occName, index + 1, entries.Count)
                : Localization.L("manage_occupation_entry_with_original", heroName, occName, origOccDisplay, index + 1, entries.Count);

            string nextLabel = remaining > 0
                ? Localization.L("carousel_next", remaining)
                : Localization.L("edit_cancel");

            InformationManager.ShowInquiry(
                new InquiryData(
                    Localization.L("manage_occupations_title"),
                    message,
                    true,
                    true,
                    Localization.L("manage_delete"),
                    nextLabel,
                    delegate()
                    {
                        try
                        {
                            var behavior = EncyclopediaEditBehavior.Instance;
                            if (behavior != null)
                                RestoreHeroOccupation(heroId, behavior);
                            EncyclopediaEditBehavior.Instance?.DeleteCustomOccupationFull(heroId);
                            DebugLog("Deleted custom occupation for " + heroId);
                            InformationManager.DisplayMessage(
                                new InformationMessage(
                                    Localization.L("manage_occupation_deleted", heroName, occName),
                                    Colors.Green));
                        }
                        catch (Exception ex)
                        {
                            DebugLog("Failed to delete occupation: " + ex.ToString());
                        }

                        entries.RemoveAt(index);
                        // Wrap to 0 when the last item was deleted (index == new Count).
                        if (entries.Count > 0)
                            ShowDeleteOccupationCarousel(entries, index < entries.Count ? index : 0);
                    },
                    delegate()
                    {
                        if (remaining > 0)
                            ShowDeleteOccupationCarousel(entries, index + 1);
                    }
                ),
                false);
        }

        // ── Export ────────────────────────────────────────────

        [SettingPropertyButton(
            "Export All to JSON",
            Content = "Export",
            HintText = "Exports all custom data (descriptions, names, titles, banners, cultures, occupations, lore fields, tags, timestamps, journal entries, relation notes, tag notes, relation history, tag categories, tag presets, auto-tag thresholds) to a JSON v10 file that can be shared or imported into another campaign.",
            Order = 1,
            RequireRestart = false)]
        [SettingPropertyGroup("4. Export", GroupOrder = 3)]
        public Action ExportDescriptions
        {
            get => () =>
            {
                try
                {
                    if (!EditableEncyclopediaAPI.IsAvailable)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(Localization.L("msg_no_campaign"), Colors.Red));
                        return;
                    }

                    var behavior = EncyclopediaEditBehavior.Instance;
                    int descCount = EditableEncyclopediaAPI.GetDescriptionCount();
                    int nameCount = behavior.GetCustomNameCount();
                    int titleCount = behavior.GetCustomTitleCount();
                    int bannerCount = behavior.GetCustomBannerCount();
                    int cultureCount = behavior.GetCustomCultureCount();
                    int occupationCount = behavior.GetCustomOccupationCount();
                    int cultureDefCount = behavior.GetCustomCultureDefinitionCount();
                    int infoFieldCount = behavior.GetHeroInfoFieldCount();
                    int tagCount = behavior.GetTagCount();
                    bool success = EditableEncyclopediaAPI.ExportToSharedFile();
                    if (success)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(Localization.L("msg_export_success", descCount, nameCount, titleCount, bannerCount, cultureCount, occupationCount, cultureDefCount, infoFieldCount, tagCount), Colors.Green));
                        DebugLog("Exported to: " + EditableEncyclopediaAPI.GetSharedFilePath());
                    }
                    else
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(Localization.L("msg_export_failed"), Colors.Red));
                    }
                }
                catch (Exception ex)
                {
                    DebugLog("Export error: " + ex.ToString());
                    InformationManager.DisplayMessage(
                        new InformationMessage(Localization.L("msg_export_error", ex.Message), Colors.Red));
                }
            };
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyButton(
            "Import All from JSON",
            Content = "Import",
            HintText = "Imports all data (descriptions, names, titles, banners, cultures, occupations) from the JSON file on disk and merges into the current campaign. Existing entries are overwritten.",
            Order = 1,
            RequireRestart = false)]
        [SettingPropertyGroup("5. Import", GroupOrder = 4)]
        public Action ImportDescriptions
        {
            get => () =>
            {
                try
                {
                    if (!EditableEncyclopediaAPI.IsAvailable)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(Localization.L("msg_no_campaign"), Colors.Red));
                        return;
                    }

                    var result = EditableEncyclopediaAPI.ImportFromSharedFileDetailed();
                    if (result == null)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(
                                Localization.L("msg_import_failed", EditableEncyclopediaAPI.GetSharedFilePath()),
                                Colors.Red));
                    }
                    else if (result.Total == 0)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(Localization.L("msg_import_empty"),
                                WarningYellow));
                    }
                    else
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(Localization.L("msg_import_success",
                                result.Descriptions, result.Names, result.Titles, result.Banners,
                                result.Cultures, result.Occupations, result.CultureDefs, result.HeroInfoFields, result.Tags), Colors.Green));
                    }
                }
                catch (Exception ex)
                {
                    DebugLog("Import error: " + ex.ToString());
                    InformationManager.DisplayMessage(
                        new InformationMessage(Localization.L("msg_import_error", ex.Message), Colors.Red));
                }
            };
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyButton(
            "Export Heroes Only",
            Content = "Export",
            HintText = "Exports only Hero descriptions to the shared JSON file.",
            Order = 2,
            RequireRestart = false)]
        [SettingPropertyGroup("4. Export", GroupOrder = 3)]
        public Action ExportHeroesOnly
        {
            get => () => ExportByType("Heroes", EditableEncyclopediaAPI.ExportHeroDescriptions, () => EditableEncyclopediaAPI.GetAllHeroDescriptions().Count);
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyButton(
            "Export Clans Only",
            Content = "Export",
            HintText = "Exports only Clan descriptions to the shared JSON file.",
            Order = 3,
            RequireRestart = false)]
        [SettingPropertyGroup("4. Export", GroupOrder = 3)]
        public Action ExportClansOnly
        {
            get => () => ExportByType("Clans", EditableEncyclopediaAPI.ExportClanDescriptions, () => EditableEncyclopediaAPI.GetAllClanDescriptions().Count);
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyButton(
            "Export Kingdoms Only",
            Content = "Export",
            HintText = "Exports only Kingdom descriptions to the shared JSON file.",
            Order = 4,
            RequireRestart = false)]
        [SettingPropertyGroup("4. Export", GroupOrder = 3)]
        public Action ExportKingdomsOnly
        {
            get => () => ExportByType("Kingdoms", EditableEncyclopediaAPI.ExportKingdomDescriptions, () => EditableEncyclopediaAPI.GetAllKingdomDescriptions().Count);
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyButton(
            "Export Settlements Only",
            Content = "Export",
            HintText = "Exports only Settlement descriptions to the shared JSON file.",
            Order = 5,
            RequireRestart = false)]
        [SettingPropertyGroup("4. Export", GroupOrder = 3)]
        public Action ExportSettlementsOnly
        {
            get => () => ExportByType("Settlements", EditableEncyclopediaAPI.ExportSettlementDescriptions, () => EditableEncyclopediaAPI.GetAllSettlementDescriptions().Count);
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyButton(
            "Export Banners Only",
            Content = "Export",
            HintText = "Exports only custom banner codes to the shared JSON file.",
            Order = 6,
            RequireRestart = false)]
        [SettingPropertyGroup("4. Export", GroupOrder = 3)]
        public Action ExportBannersOnly
        {
            get => () =>
            {
                try
                {
                    if (!EditableEncyclopediaAPI.IsAvailable)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(Localization.L("msg_no_campaign"), Colors.Red));
                        return;
                    }

                    var behavior = EncyclopediaEditBehavior.Instance;
                    int count = behavior.GetCustomBannerCount();
                    if (count == 0)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(Localization.L("msg_banner_export_none"),
                                WarningYellow));
                        return;
                    }

                    bool success = EditableEncyclopediaAPI.ExportBannerCodes();
                    if (success)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(Localization.L("msg_banner_export_success", count), Colors.Green));
                    }
                    else
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(Localization.L("msg_export_failed"), Colors.Red));
                    }
                }
                catch (Exception ex)
                {
                    DebugLog("Banner export error: " + ex.ToString());
                    InformationManager.DisplayMessage(
                        new InformationMessage(Localization.L("msg_export_error", ex.Message), Colors.Red));
                }
            };
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyButton(
            "Import Banners Only",
            Content = "Import",
            HintText = "Imports only custom banner codes from the shared JSON file into the current campaign.",
            Order = 2,
            RequireRestart = false)]
        [SettingPropertyGroup("5. Import", GroupOrder = 4)]
        public Action ImportBannersOnly
        {
            get => () =>
            {
                try
                {
                    if (!EditableEncyclopediaAPI.IsAvailable)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(Localization.L("msg_no_campaign"), Colors.Red));
                        return;
                    }

                    int result = EditableEncyclopediaAPI.ImportBannersFromSharedFile();
                    if (result < 0)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(
                                Localization.L("msg_import_failed", EditableEncyclopediaAPI.GetSharedFilePath()),
                                Colors.Red));
                    }
                    else if (result == 0)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(Localization.L("msg_banner_import_none"),
                                WarningYellow));
                    }
                    else
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(Localization.L("msg_banner_import_success", result), Colors.Green));
                    }
                }
                catch (Exception ex)
                {
                    DebugLog("Banner import error: " + ex.ToString());
                    InformationManager.DisplayMessage(
                        new InformationMessage(Localization.L("msg_import_error", ex.Message), Colors.Red));
                }
            };
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyButton(
            "Reset All Descriptions",
            Content = "Reset All",
            HintText = "Removes ALL custom descriptions from the current campaign. This cannot be undone!",
            Order = 0,
            RequireRestart = false)]
        [SettingPropertyGroup("6. Reset", GroupOrder = 5)]
        public Action ResetAllDescriptions
        {
            get => () =>
            {
                try
                {
                    if (!EditableEncyclopediaAPI.IsAvailable)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(Localization.L("msg_no_campaign"), Colors.Red));
                        return;
                    }

                    int count = EditableEncyclopediaAPI.GetDescriptionCount();
                    if (count == 0)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(Localization.L("msg_reset_all_none"),
                                WarningYellow));
                        return;
                    }

                    InformationManager.ShowInquiry(
                        new InquiryData(
                            Localization.L("reset_all_title"),
                            Localization.L("reset_all_message", count),
                            true,
                            true,
                            Localization.L("reset_all_confirm"),
                            Localization.L("reset_all_cancel"),
                            delegate
                            {
                                int removed = EditableEncyclopediaAPI.ResetAllDescriptions();
                                InformationManager.DisplayMessage(
                                    new InformationMessage(
                                        Localization.L("msg_reset_all_success", removed), Colors.Green));
                                DebugLog("Reset all descriptions: " + removed + " removed");
                            },
                            null),
                        false,
                        false);
                }
                catch (Exception ex)
                {
                    DebugLog("Reset all error: " + ex.ToString());
                    InformationManager.DisplayMessage(
                        new InformationMessage(Localization.L("msg_reset_all_error", ex.Message), Colors.Red));
                }
            };
            set { /* MCM requires a setter, ignore */ }
        }

        // ── Advanced ──────────────────────────────────────────

        [SettingPropertyInteger(
            "Initial Key Poll Delay (ms)",
            0, 2000,
            HintText = "Delay in milliseconds before the Ctrl+E key poller starts after opening an encyclopedia page. Prevents accidental triggers.",
            Order = 0,
            RequireRestart = false)]
        [SettingPropertyGroup("7. Advanced", GroupOrder = 6)]
        public int InitialKeyPollDelayMs { get; set; } = DefaultInitialKeyPollDelayMs;

        [SettingPropertyInteger(
            "Key Poll Interval (ms)",
            10, 500,
            HintText = "How often (in milliseconds) the mod checks for the Ctrl+E key press. Lower = more responsive, higher = less CPU usage.",
            Order = 1,
            RequireRestart = false)]
        [SettingPropertyGroup("7. Advanced", GroupOrder = 6)]
        public int KeyPollIntervalMs { get; set; } = DefaultKeyPollIntervalMs;

        [SettingPropertyInteger(
            "Max Description Length",
            0, 10000,
            HintText = "Maximum characters for the main Description field. Set to 0 for unlimited.",
            Order = 2,
            RequireRestart = false)]
        [SettingPropertyGroup("7. Advanced", GroupOrder = 6)]
        public int MaxDescriptionLength { get; set; } = DefaultMaxDescriptionLength;

        [SettingPropertyInteger(
            "Max Narrative Field Length",
            0, 10000,
            HintText = "Maximum characters for narrative fields (Backstory, Rumors/Secrets). Set to 0 for unlimited.",
            Order = 3,
            RequireRestart = false)]
        [SettingPropertyGroup("7. Advanced", GroupOrder = 6)]
        public int MaxNarrativeFieldLength { get; set; } = DefaultMaxNarrativeFieldLength;

        [SettingPropertyInteger(
            "Max Stats Field Length",
            0, 10000,
            HintText = "Maximum characters for short stats fields (Personality, Goals, Relationships). Set to 0 for unlimited.",
            Order = 4,
            RequireRestart = false)]
        [SettingPropertyGroup("7. Advanced", GroupOrder = 6)]
        public int MaxStatsFieldLength { get; set; } = DefaultMaxStatsFieldLength;

        [SettingPropertyDropdown(
            "Language",
            HintText = "Language for popups, messages, and runtime strings. Applies live — no restart needed. Note: MCM labels themselves stay in English (they are compile-time). Language files: Documents/Mount and Blade II Bannerlord/Configs/ModSettings/Global/EditableEncyclopedia/Languages/.",
            Order = 3,
            RequireRestart = false)]
        [SettingPropertyGroup("7. Advanced", GroupOrder = 6)]
        public Dropdown<string> Language { get; set; } = new Dropdown<string>(
            new string[] { "en", "tr", "de", "fr", "es", "zh", "ru", "pt", "ko", "ja", "pl", "uk" }, 0);

        // ── Debug ─────────────────────────────────────────────

        [SettingPropertyBool(
            "Debug Mode",
            HintText = "Enable verbose logging to the debug.log file (in the EditableEncyclopedia config folder). Does not show messages on screen unless 'Show Debug On Screen' is also enabled.",
            Order = 0,
            RequireRestart = false)]
        [SettingPropertyGroup("8. Debug", GroupOrder = 7)]
        public bool DebugMode { get; set; } = false;

        [SettingPropertyBool(
            "Show Debug On Screen",
            HintText = "When Debug Mode is enabled, also display debug messages as yellow in-game text. Warning: this is very verbose and may clutter the screen, especially on encyclopedia pages.",
            Order = 1,
            RequireRestart = false)]
        [SettingPropertyGroup("8. Debug", GroupOrder = 7)]
        public bool ShowDebugOnScreen { get; set; } = false;

        // ── Helpers ────────────────────────────────────────────

        /// <summary>
        /// Helper for Export by Type buttons. Avoids code duplication.
        /// </summary>
        private static void ExportByType(string typeName, Func<bool> exportFunc, Func<int> countFunc)
        {
            try
            {
                if (!EditableEncyclopediaAPI.IsAvailable)
                {
                    InformationManager.DisplayMessage(
                        new InformationMessage(Localization.L("msg_no_campaign"), Colors.Red));
                    return;
                }

                int count = countFunc();
                if (count == 0)
                {
                    InformationManager.DisplayMessage(
                        new InformationMessage(Localization.L("msg_type_export_none", typeName),
                            WarningYellow));
                    return;
                }

                bool success = exportFunc();
                if (success)
                {
                    InformationManager.DisplayMessage(
                        new InformationMessage(
                            Localization.L("msg_type_export_success", count, typeName), Colors.Green));
                }
                else
                {
                    InformationManager.DisplayMessage(
                        new InformationMessage(Localization.L("msg_type_export_failed", typeName), Colors.Red));
                }
            }
            catch (Exception ex)
            {
                DebugLog(typeName + " export error: " + ex.ToString());
                InformationManager.DisplayMessage(
                    new InformationMessage(Localization.L("msg_type_export_error", typeName, ex.Message), Colors.Red));
            }
        }

        /// <summary>
        /// Logs a debug message as a yellow in-game message and writes to a log file if Debug Mode is enabled.
        /// Log file location: Documents\Mount and Blade II Bannerlord\Configs\ModSettings\Global\EditableEncyclopedia\Logs\debug.log
        /// Safe to call even if MCM is not installed.
        /// </summary>
        public static void DebugLog(string message)
        {
            try
            {
                var settings = Instance;
                if (settings != null && settings.DebugMode)
                {
                    WriteToLogFile(message);

                    if (settings.ShowDebugOnScreen)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(DebugLogPrefix + message,
                                WarningYellow));
                    }
                }
            }
            catch (Exception)
            {
                // Suppress: DebugLog is itself the diagnostic channel, so re-throwing or
                // re-logging a failure here could cause infinite recursion or crash the
                // mod load. Worst case, a single message is silently dropped.
            }
        }

        private static readonly object _logLock = new object();
        private static string _logFolder;
        private static string _logFilePath;
        private static bool _logInitialized;

        private const long MaxLogFileSize = 5 * 1024 * 1024; // 5 MB

        private static void WriteToLogFile(string message)
        {
            try
            {
                lock (_logLock)
                {
                    if (!_logInitialized)
                    {
                        _logFolder = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                            "Mount and Blade II Bannerlord",
                            "Configs", "ModSettings", "Global", "EditableEncyclopedia", LogFolderName);
                        _logFilePath = Path.Combine(_logFolder, LogFileName);
                        _logInitialized = true;
                    }

                    if (!Directory.Exists(_logFolder))
                        Directory.CreateDirectory(_logFolder);

                    // Rotate log if it exceeds max size
                    if (File.Exists(_logFilePath))
                    {
                        var fileInfo = new FileInfo(_logFilePath);
                        if (fileInfo.Length > MaxLogFileSize)
                        {
                            string backupPath = Path.Combine(_logFolder, LogBackupFileName);
                            if (File.Exists(backupPath))
                                File.Delete(backupPath);
                            File.Move(_logFilePath, backupPath);
                        }
                    }

                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    string line = "[" + timestamp + "] " + message + Environment.NewLine;
                    File.AppendAllText(_logFilePath, line);
                }
            }
            catch (Exception)
            {
                // Suppress: this writer IS the diagnostic channel; can't recursively log
                // its own failure. Disk full / permission denied / file-locked all silently
                // drop the message rather than crashing the mod.
            }
        }

        // ── 9. Extensions ────────────────────────────────────────

        // v2.5.4: backing field for EnableWebExtension. Setter refuses `true` when the
        // EEWebExtension assembly isn't loaded — keeps the IsToggle subgroup collapsed
        // (its 18+ child settings hidden) when the extension isn't installed.
        private bool _enableWebExtension = true;

        [SettingPropertyBool(
            "Enable Web Extension (EE-WebAPI)",
            HintText = "Enable the EE Web Extension mod. When enabled and the EE-WebAPI module is installed, a local web server runs on http://127.0.0.1:8080/ allowing you to browse and edit encyclopedia data from your browser. Disable to prevent the web server from starting. Requires game restart to take effect.",
            Order = 0,
            RequireRestart = true)]
        // v2.5.9: reverted v2.5.8's IsToggle which collapsed the entire section into a
        // useless one-line "9. Extensions (Disabled)" row. Back to v2.5.6 behavior:
        // section 9 always shows its toggle + status + EEWebExtension subgroup row.
        // Smart setter below still locks the toggle off when EEWebExtension isn't loaded
        // so users can't enable a service that has no backing implementation.
        [SettingPropertyGroup("9. Extensions", GroupOrder = 8)]
        public bool EnableWebExtension
        {
            get => _enableWebExtension && IsEEWebExtensionLoaded();
            set
            {
                // Refuse to enable when the extension assembly isn't present.
                _enableWebExtension = value && IsEEWebExtensionLoaded();
            }
        }

        [SettingPropertyText(
            "Web Extension Status",
            HintText = "Shows whether the EE Web Extension module is currently loaded and running.",
            Order = 1,
            RequireRestart = false)]
        [SettingPropertyGroup("9. Extensions", GroupOrder = 8)]
        public string WebExtensionStatus
        {
            get
            {
                // Order matters: check assembly presence first. The smart setter on
                // EnableWebExtension forces it false when the assembly isn't loaded,
                // so checking EnableWebExtension first would always say "Disabled" and
                // the user would never see the "Not installed" hint.
                if (!IsEEWebExtensionLoaded())
                    return "Not installed — place EE-WebAPI module in Modules/ folder";
                if (!EnableWebExtension)
                    return "Disabled (toggle above to enable)";
                return "Active — http://127.0.0.1:8080/";
            }
            set { /* read-only, ignore */ }
        }

        // ── Web Extension (additional settings) ──────────────────

        [SettingPropertyInteger(
            "Web Server Port",
            1024, 65535,
            HintText = "The port number for the web server. Default is 8080 (http://127.0.0.1:8080). Change if another app uses port 8080. Requires restart.",
            Order = 1,
            RequireRestart = true)]
        [SettingPropertyGroup("9. Extensions/EE-WebAPI/Web Server", GroupOrder = 8)]
        public int WebServerPort { get; set; } = 8080;

        [SettingPropertyBool(
            "Auto-Open Browser",
            HintText = "Automatically open the web encyclopedia in your default browser when a campaign is loaded.",
            Order = 2,
            RequireRestart = false)]
        [SettingPropertyGroup("9. Extensions/EE-WebAPI/Web Server", GroupOrder = 8)]
        public bool WebAutoOpenBrowser { get; set; } = false;

        [SettingPropertyBool(
            "Allow External Access",
            HintText = "Allow connections from other devices on your local network (e.g. phone, tablet). When OFF, only this PC can access the web encyclopedia. When ON, any device on your network can connect via your PC's IP address. Requires restart.",
            Order = 3,
            RequireRestart = true)]
        [SettingPropertyGroup("9. Extensions/EE-WebAPI/Web Server", GroupOrder = 8)]
        public bool WebAllowExternalAccess { get; set; } = false;

        [SettingPropertyBool(
            "Enable Editing from Web",
            HintText = "Allow editing encyclopedia entries (descriptions, names, tags, etc.) from the web interface. When OFF, the web interface is read-only.",
            Order = 4,
            RequireRestart = false)]
        [SettingPropertyGroup("9. Extensions/EE-WebAPI/Web Server", GroupOrder = 8)]
        public bool WebEnableEditing { get; set; } = true;

        [SettingPropertyBool(
            "Enable Portrait Extraction",
            HintText = "Allow the web extension to extract hero portraits from the game for display in the web interface.",
            Order = 5,
            RequireRestart = false)]
        [SettingPropertyGroup("9. Extensions/EE-WebAPI/Web Server", GroupOrder = 8)]
        public bool WebEnablePortraitExtraction { get; set; } = true;

        [SettingPropertyInteger(
            "Live Sync Interval (seconds)",
            2, 60,
            HintText = "How often the web detail views auto-refresh with live game data. Lower = more responsive but more CPU. Default is 8 seconds.",
            Order = 0,
            RequireRestart = false)]
        [SettingPropertyGroup("9. Extensions/EE-WebAPI/Live Sync", GroupOrder = 8)]
        public int WebLiveSyncInterval { get; set; } = 8;

        [SettingPropertyBool(
            "Live Sync on Detail Views",
            HintText = "Automatically refresh detail views (Hero, Clan, Settlement, Kingdom) with live game data. Shows a LIVE badge when active.",
            Order = 1,
            RequireRestart = false)]
        [SettingPropertyGroup("9. Extensions/EE-WebAPI/Live Sync", GroupOrder = 8)]
        public bool WebLiveSyncEnabled { get; set; } = true;

        [SettingPropertyBool(
            "Live Chronicle Updates",
            HintText = "Push new chronicle events to the web interface in real-time (home page Live Chronicle section).",
            Order = 2,
            RequireRestart = false)]
        [SettingPropertyGroup("9. Extensions/EE-WebAPI/Live Sync", GroupOrder = 8)]
        public bool WebLiveChronicleEnabled { get; set; } = true;

        [SettingPropertyBool(
            "Show HUD Stats",
            HintText = "Display the player HUD bar in the web interface showing gold, influence, health, troops, and other live stats.",
            Order = 0,
            RequireRestart = false)]
        [SettingPropertyGroup("9. Extensions/EE-WebAPI/Web Display", GroupOrder = 8)]
        public bool WebShowHud { get; set; } = true;

        [SettingPropertyBool(
            "Show Intro Screen",
            HintText = "Show the animated intro screen with Viking artwork when the web encyclopedia first loads.",
            Order = 1,
            RequireRestart = false)]
        [SettingPropertyGroup("9. Extensions/EE-WebAPI/Web Display", GroupOrder = 8)]
        public bool WebShowIntro { get; set; } = true;

        [SettingPropertyBool(
            "Show Ember Particles",
            HintText = "Display ambient floating ember particles in the web interface for atmosphere. Disable for better performance.",
            Order = 2,
            RequireRestart = false)]
        [SettingPropertyGroup("9. Extensions/EE-WebAPI/Web Display", GroupOrder = 8)]
        public bool WebShowEmberParticles { get; set; } = true;

        [SettingPropertyBool(
            "Show Gold Spark Trail",
            HintText = "Display the gold spark trail effect that follows the mouse cursor. Disable for better performance.",
            Order = 3,
            RequireRestart = false)]
        [SettingPropertyGroup("9. Extensions/EE-WebAPI/Web Display", GroupOrder = 8)]
        public bool WebShowGoldSparks { get; set; } = true;

        [SettingPropertyBool(
            "Enable Sound Effects",
            HintText = "Play subtle UI sound effects (hover, click, navigation) in the web interface.",
            Order = 4,
            RequireRestart = false)]
        [SettingPropertyGroup("9. Extensions/EE-WebAPI/Web Display", GroupOrder = 8)]
        public bool WebEnableSounds { get; set; } = true;

        [SettingPropertyBool(
            "Enable Scroll Animations",
            HintText = "Cards and sections animate into view as you scroll. Disable for instant rendering.",
            Order = 5,
            RequireRestart = false)]
        [SettingPropertyGroup("9. Extensions/EE-WebAPI/Web Display", GroupOrder = 8)]
        public bool WebEnableScrollAnimations { get; set; } = true;

        [SettingPropertyInteger(
            "Cards Per Page",
            20, 200,
            HintText = "Number of cards shown per page in list views (Heroes, Clans, Settlements, Kingdoms). Higher values may impact performance.",
            Order = 6,
            RequireRestart = false)]
        [SettingPropertyGroup("9. Extensions/EE-WebAPI/Web Display", GroupOrder = 8)]
        public int WebCardsPerPage { get; set; } = 60;

        [SettingPropertyButton(
            "Open Web Encyclopedia",
            Content = "Open in Browser",
            HintText = "Open the web encyclopedia in your default browser.",
            Order = 0,
            RequireRestart = false)]
        [SettingPropertyGroup("9. Extensions/EE-WebAPI/Quick Actions", GroupOrder = 8)]
        public Action OpenWebBrowser
        {
            get => () =>
            {
                try
                {
                    int port = WebServerPort;
                    string url = "http://127.0.0.1:" + port;

                    // Try multiple methods — some Windows configurations block direct URL launch.
                    // Each fallback failure is logged so support reports show which method failed
                    // (antivirus, group policy, and shell-handler issues all manifest differently).
                    try
                    {
                        // Method 1: cmd /c start (most reliable on Windows)
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "cmd",
                            Arguments = "/c start " + url.Replace("&", "^&"),
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });
                        InformationManager.DisplayMessage(
                            new InformationMessage("[EE Web] Opening " + url + " in your browser...", Colors.Green));
                        return;
                    }
                    catch (Exception ex) { DebugLog("OpenWebBrowser method 1 (cmd /c start) failed: " + ex.ToString()); }

                    try
                    {
                        // Method 2: UseShellExecute with URL
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        });
                        InformationManager.DisplayMessage(
                            new InformationMessage("[EE Web] Opening " + url + " in your browser...", Colors.Green));
                        return;
                    }
                    catch (Exception ex) { DebugLog("OpenWebBrowser method 2 (ShellExecute URL) failed: " + ex.ToString()); }

                    try
                    {
                        // Method 3: explorer.exe with URL
                        Process.Start("explorer.exe", url);
                        InformationManager.DisplayMessage(
                            new InformationMessage("[EE Web] Opening " + url + " in your browser...", Colors.Green));
                        return;
                    }
                    catch (Exception ex) { DebugLog("OpenWebBrowser method 3 (explorer.exe) failed: " + ex.ToString()); }

                    // All methods failed
                    InformationManager.DisplayMessage(
                        new InformationMessage("[EE Web] Could not open browser. Please manually open: " + url, Colors.Yellow));
                }
                catch (Exception ex)
                {
                    DebugLog("OpenWebBrowser failed: " + ex.ToString());
                    InformationManager.DisplayMessage(
                        new InformationMessage("[EE Web] Failed to open browser: " + ex.Message, Colors.Red));
                }
            };
            set { /* MCM requires a setter, ignore */ }
        }
    }
}