using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Public API for other mods to read Editable Encyclopedia descriptions.
    /// 
    /// This allows cross-mod integration — for example, an AI dialogue mod
    /// (like AI Influence) could read player-written notes about characters,
    /// clans, kingdoms, or settlements and use them as context.
    /// 
    /// Usage from another mod:
    /// <code>
    ///   // Add a reference to EditableEncyclopedia.dll in your project
    ///   // Then use the static API:
    ///   
    ///   string heroNotes = EditableEncyclopediaAPI.GetHeroDescription(hero);
    ///   string clanNotes = EditableEncyclopediaAPI.GetClanDescription(clan);
    ///   
    ///   // Or check if Editable Encyclopedia is even loaded:
    ///   if (EditableEncyclopediaAPI.IsAvailable)
    ///   {
    ///       var allNotes = EditableEncyclopediaAPI.GetAllDescriptions();
    ///   }
    /// </code>
    /// </summary>
    public static class EditableEncyclopediaAPI
    {
        private static string SafeGet(Func<string> getter, string fallback = "Unknown")
        {
            try { return getter() ?? fallback; }
            catch (Exception ex) { MCMSettings.DebugLog("SafeGet: " + ex.ToString()); return fallback; }
        }

        /// <summary>
        /// Returns true if Editable Encyclopedia is loaded and a campaign is active.
        /// Always check this before calling other API methods from external mods.
        /// </summary>
        public static bool IsAvailable =>
            EncyclopediaEditBehavior.Instance != null;

        // ── By StringId (generic) ─────────────────────────────────

        /// <summary>
        /// Gets the custom description for any object by its StringId.
        /// Returns null if no custom description exists.
        /// </summary>
        public static string GetDescription(string objectId)
        {
            if (!IsAvailable || string.IsNullOrEmpty(objectId))
                return null;
            return EncyclopediaEditBehavior.Instance.GetDescription(objectId);
        }

        /// <summary>
        /// Sets (or clears) the custom description for any object by its StringId.
        /// Pass null or empty to remove the custom description.
        /// </summary>
        public static void SetDescription(string objectId, string description)
        {
            if (!IsAvailable || string.IsNullOrEmpty(objectId))
                return;
            EncyclopediaEditBehavior.Instance.SetDescription(objectId, description);
        }

        /// <summary>
        /// Returns true if a custom description exists for the given StringId.
        /// </summary>
        public static bool HasDescription(string objectId)
        {
            if (!IsAvailable || string.IsNullOrEmpty(objectId))
                return false;
            return EncyclopediaEditBehavior.Instance.HasCustomDescription(objectId);
        }

        // ── By Game Object (typed helpers) ────────────────────────

        /// <summary>
        /// Gets the custom description for a Hero.
        /// Returns null if no custom description exists.
        /// </summary>
        public static string GetHeroDescription(Hero hero)
        {
            if (hero == null) return null;
            return GetDescription(hero.StringId);
        }

        /// <summary>
        /// Gets the custom description for a Clan.
        /// Returns null if no custom description exists.
        /// </summary>
        public static string GetClanDescription(Clan clan)
        {
            if (clan == null) return null;
            return GetDescription(clan.StringId);
        }

        /// <summary>
        /// Gets the custom description for a Kingdom.
        /// Returns null if no custom description exists.
        /// </summary>
        public static string GetKingdomDescription(Kingdom kingdom)
        {
            if (kingdom == null) return null;
            return GetDescription(kingdom.StringId);
        }

        /// <summary>
        /// Gets the custom description for a Settlement (town, castle, village).
        /// Returns null if no custom description exists.
        /// </summary>
        public static string GetSettlementDescription(Settlement settlement)
        {
            if (settlement == null) return null;
            return GetDescription(settlement.StringId);
        }

        // ── Bulk Access ───────────────────────────────────────────

        /// <summary>
        /// Returns a read-only copy of ALL custom descriptions.
        /// Key = StringId, Value = custom description text.
        /// Returns an empty dictionary if unavailable.
        /// </summary>
        public static Dictionary<string, string> GetAllDescriptions()
        {
            if (!IsAvailable)
                return new Dictionary<string, string>();
            return EncyclopediaEditBehavior.Instance.GetAllDescriptions();
        }

        /// <summary>
        /// Returns the total number of custom descriptions the player has written.
        /// </summary>
        public static int GetDescriptionCount()
        {
            if (!IsAvailable)
                return 0;
            return EncyclopediaEditBehavior.Instance.GetDescriptionCount();
        }

        // ── Hero Lookup Helpers ───────────────────────────────

        /// <summary>
        /// Returns all hero descriptions as a dictionary (StringId → description).
        /// Filters by heroes that exist in the current campaign.
        /// Returns an empty dictionary if unavailable.
        /// </summary>
        public static Dictionary<string, string> GetAllHeroDescriptions()
        {
            var result = new Dictionary<string, string>();
            if (!IsAvailable) return result;

            var allDescs = EncyclopediaEditBehavior.Instance.GetAllDescriptions();
            foreach (var hero in Hero.AllAliveHeroes)
            {
                if (hero != null && allDescs.TryGetValue(hero.StringId, out var description))
                    result[hero.StringId] = description;
            }
            foreach (var hero in Hero.DeadOrDisabledHeroes)
            {
                if (hero != null && !result.ContainsKey(hero.StringId) && allDescs.TryGetValue(hero.StringId, out var description))
                    result[hero.StringId] = description;
            }
            return result;
        }

        /// <summary>
        /// Returns all clan descriptions as a dictionary (StringId → description).
        /// Filters by clans that exist in the current campaign.
        /// Returns an empty dictionary if unavailable.
        /// </summary>
        public static Dictionary<string, string> GetAllClanDescriptions()
        {
            var result = new Dictionary<string, string>();
            if (!IsAvailable) return result;

            var allDescs = EncyclopediaEditBehavior.Instance.GetAllDescriptions();
            foreach (var clan in Clan.All)
            {
                if (clan != null && allDescs.TryGetValue(clan.StringId, out var description))
                {
                    result[clan.StringId] = description;
                }
            }
            return result;
        }

        /// <summary>
        /// Returns all kingdom descriptions as a dictionary (StringId → description).
        /// Filters by kingdoms that exist in the current campaign.
        /// Returns an empty dictionary if unavailable.
        /// </summary>
        public static Dictionary<string, string> GetAllKingdomDescriptions()
        {
            var result = new Dictionary<string, string>();
            if (!IsAvailable) return result;

            var allDescs = EncyclopediaEditBehavior.Instance.GetAllDescriptions();
            foreach (var kingdom in Kingdom.All)
            {
                if (kingdom != null && allDescs.TryGetValue(kingdom.StringId, out var description))
                {
                    result[kingdom.StringId] = description;
                }
            }
            return result;
        }

        /// <summary>
        /// Returns all settlement descriptions as a dictionary (StringId → description).
        /// Filters by settlements that exist in the current campaign.
        /// Returns an empty dictionary if unavailable.
        /// </summary>
        public static Dictionary<string, string> GetAllSettlementDescriptions()
        {
            var result = new Dictionary<string, string>();
            if (!IsAvailable) return result;

            var allDescs = EncyclopediaEditBehavior.Instance.GetAllDescriptions();
            foreach (var settlement in Settlement.All)
            {
                if (settlement != null && allDescs.TryGetValue(settlement.StringId, out var description))
                {
                    result[settlement.StringId] = description;
                }
            }
            return result;
        }

        // ── Change Notification ───────────────────────────────

        /// <summary>
        /// Event fired whenever a description is saved or removed.
        /// Args: (string objectId, string newDescription) — newDescription is null if removed.
        /// </summary>
        public static event Action<string, string> OnDescriptionChanged;

        /// <summary>
        /// Internal method to raise the OnDescriptionChanged event.
        /// Called from EncyclopediaEditBehavior.SetDescription.
        /// </summary>
        internal static void RaiseDescriptionChanged(string objectId, string description)
        {
            try
            {
                OnDescriptionChanged?.Invoke(objectId, description);
            }
            catch (Exception ex)
            {
                // Swallow exceptions from event handlers to prevent breaking the mod
                // Log for debugging purposes
                MCMSettings.DebugLog($"Error in OnDescriptionChanged event handler: {ex}");
            }
        }

        // ── Shared File Export ────────────────────────────────

        /// <summary>
        /// Exports all descriptions to the shared JSON file on disk so other mods can read them
        /// without needing a DLL reference.
        /// File location: Documents\Mount and Blade II Bannerlord\Configs\ModSettings\Global\EditableEncyclopedia\descriptions_export.json
        /// Returns true on success, false on failure.
        /// </summary>
        public static bool ExportToSharedFile()
        {
            if (!IsAvailable)
                return false;

            var behavior = EncyclopediaEditBehavior.Instance;
            var descriptions = behavior.GetAllDescriptions();
            var names = behavior.GetAllCustomNames();
            var titles = behavior.GetAllCustomTitles();
            var banners = behavior.GetAllCustomBanners();
            var cultures = behavior.GetAllCustomCulturesForExport();
            var occupations = behavior.GetAllCustomOccupationsForExport();
            var cultureDefs = behavior.GetAllCustomCultureDefsForExport();
            var heroInfoFields = behavior.GetAllHeroInfoFieldsForExport();
            var tags = behavior.GetAllTagsForExport();
            var timestamps = behavior.GetAllTimestampsForExport();
            var journal = behavior.GetAllJournalForExport();
            var relationNotes = behavior.GetAllRelationNotesForExport();
            var tagNotes = behavior.GetAllTagNotesForExport();
            var relationHistory = behavior.GetAllRelationHistoryForExport();
            var tagCategories = behavior.GetAllTagCategories();
            var tagPresets = behavior.GetAllTagPresets();
            var autoTagThresholds = behavior.GetAllPerHeroAutoTagThresholds();
            return SharedFileExporter.Export(descriptions, names, titles, banners, cultures, occupations, cultureDefs, heroInfoFields, tags, timestamps, journal, relationNotes, tagNotes, relationHistory, tagCategories, tagPresets, autoTagThresholds);
        }

        /// <summary>
        /// Imports all data (descriptions, names, titles, banners) from the shared JSON file
        /// on disk and merges them into the current campaign. Existing entries for the same
        /// object ID are overwritten by the imported values.
        /// Returns the total number of entries imported, or -1 on failure.
        /// </summary>
        public static int ImportFromSharedFile()
        {
            if (!IsAvailable)
                return -1;

            var data = SharedFileExporter.ImportAll();
            if (data == null)
                return -1;

            var behavior = EncyclopediaEditBehavior.Instance;
            int total = 0;
            if (data.Descriptions != null)
                total += behavior.ImportDescriptions(data.Descriptions);
            if (data.Names != null)
                total += behavior.ImportNames(data.Names);
            if (data.Titles != null)
                total += behavior.ImportTitles(data.Titles);
            if (data.Banners != null)
                total += behavior.ImportBanners(data.Banners);
            if (data.CultureDefs != null)
                total += behavior.ImportCultureDefs(data.CultureDefs);
            if (data.Cultures != null)
                total += behavior.ImportCultures(data.Cultures);
            if (data.Occupations != null)
                total += behavior.ImportOccupations(data.Occupations);
            if (data.HeroInfoFields != null)
                total += behavior.ImportHeroInfoFields(data.HeroInfoFields);
            if (data.Tags != null)
                total += behavior.ImportTags(data.Tags);
            if (data.Timestamps != null)
                behavior.ImportTimestamps(data.Timestamps);
            if (data.Journal != null)
                total += behavior.ImportJournal(data.Journal);
            if (data.RelationNotes != null)
                total += behavior.ImportRelationNotes(data.RelationNotes);
            if (data.TagNotes != null)
                total += behavior.ImportTagNotes(data.TagNotes);
            if (data.RelationHistory != null)
                total += behavior.ImportRelationHistory(data.RelationHistory);
            if (data.TagCategories != null)
                total += behavior.ImportTagCategories(data.TagCategories);
            if (data.TagPresets != null)
                total += behavior.ImportTagPresets(data.TagPresets);
            if (data.PerHeroAutoTagThresholds != null)
                total += behavior.ImportPerHeroAutoTagThresholds(data.PerHeroAutoTagThresholds);
            if (data.CultureDefs != null && data.CultureDefs.Count > 0)
                EncyclopediaEditBehavior.NeedsCustomCultureReapply = true;
            return total;
        }

        /// <summary>
        /// Returns the full path to the shared descriptions JSON file.
        /// </summary>
        public static string GetSharedFilePath()
        {
            return SharedFileExporter.GetExportFilePath();
        }

        /// <summary>
        /// Exports only hero descriptions to the shared JSON file.
        /// Returns true on success.
        /// </summary>
        public static bool ExportHeroDescriptions()
        {
            if (!IsAvailable) return false;
            var heroIds = new System.Collections.Generic.HashSet<string>();
            foreach (var hero in Hero.AllAliveHeroes)
            {
                if (hero != null) heroIds.Add(hero.StringId);
            }
            foreach (var hero in Hero.DeadOrDisabledHeroes)
            {
                if (hero != null) heroIds.Add(hero.StringId);
            }
            var filtered = EncyclopediaEditBehavior.Instance.GetDescriptionsForIds(heroIds);
            return SharedFileExporter.Export(filtered);
        }

        /// <summary>
        /// Exports only clan descriptions to the shared JSON file.
        /// Returns true on success.
        /// </summary>
        public static bool ExportClanDescriptions()
        {
            if (!IsAvailable) return false;
            var ids = new System.Collections.Generic.HashSet<string>();
            foreach (var clan in Clan.All)
            {
                if (clan != null) ids.Add(clan.StringId);
            }
            var filtered = EncyclopediaEditBehavior.Instance.GetDescriptionsForIds(ids);
            return SharedFileExporter.Export(filtered);
        }

        /// <summary>
        /// Exports only kingdom descriptions to the shared JSON file.
        /// Returns true on success.
        /// </summary>
        public static bool ExportKingdomDescriptions()
        {
            if (!IsAvailable) return false;
            var ids = new System.Collections.Generic.HashSet<string>();
            foreach (var kingdom in Kingdom.All)
            {
                if (kingdom != null) ids.Add(kingdom.StringId);
            }
            var filtered = EncyclopediaEditBehavior.Instance.GetDescriptionsForIds(ids);
            return SharedFileExporter.Export(filtered);
        }

        /// <summary>
        /// Exports only settlement descriptions to the shared JSON file.
        /// Returns true on success.
        /// </summary>
        public static bool ExportSettlementDescriptions()
        {
            if (!IsAvailable) return false;
            var ids = new System.Collections.Generic.HashSet<string>();
            foreach (var settlement in Settlement.All)
            {
                if (settlement != null) ids.Add(settlement.StringId);
            }
            var filtered = EncyclopediaEditBehavior.Instance.GetDescriptionsForIds(ids);
            return SharedFileExporter.Export(filtered);
        }

        /// <summary>
        /// Exports only banner codes to the shared JSON file.
        /// Returns true on success.
        /// </summary>
        public static bool ExportBannerCodes()
        {
            if (!IsAvailable) return false;
            var banners = EncyclopediaEditBehavior.Instance.GetAllCustomBanners();
            return SharedFileExporter.Export(null, null, null, banners);
        }

        /// <summary>
        /// Imports only banner codes from the shared JSON file.
        /// Returns the number of banners imported, or -1 on failure.
        /// </summary>
        public static int ImportBannersFromSharedFile()
        {
            if (!IsAvailable)
                return -1;

            var data = SharedFileExporter.ImportAll();
            if (data == null)
                return -1;

            if (data.Banners == null || data.Banners.Count == 0)
                return 0;

            var behavior = EncyclopediaEditBehavior.Instance;
            return behavior.ImportBanners(data.Banners);
        }

        /// <summary>
        /// Imports all data from the shared JSON file and returns per-section counts.
        /// Returns null on failure.
        /// </summary>
        public static ImportResult ImportFromSharedFileDetailed()
        {
            if (!IsAvailable)
                return null;

            var data = SharedFileExporter.ImportAll();
            if (data == null)
                return null;

            var behavior = EncyclopediaEditBehavior.Instance;
            var result = new ImportResult();
            if (data.Descriptions != null)
                result.Descriptions = behavior.ImportDescriptions(data.Descriptions);
            if (data.Names != null)
                result.Names = behavior.ImportNames(data.Names);
            if (data.Titles != null)
                result.Titles = behavior.ImportTitles(data.Titles);
            if (data.Banners != null)
                result.Banners = behavior.ImportBanners(data.Banners);
            if (data.CultureDefs != null)
                result.CultureDefs = behavior.ImportCultureDefs(data.CultureDefs);
            if (data.Cultures != null)
                result.Cultures = behavior.ImportCultures(data.Cultures);
            if (data.Occupations != null)
                result.Occupations = behavior.ImportOccupations(data.Occupations);
            if (data.HeroInfoFields != null)
                result.HeroInfoFields = behavior.ImportHeroInfoFields(data.HeroInfoFields);
            if (data.Tags != null)
                result.Tags = behavior.ImportTags(data.Tags);
            if (data.Timestamps != null)
                behavior.ImportTimestamps(data.Timestamps);
            if (data.Journal != null)
                result.Journal = behavior.ImportJournal(data.Journal);
            if (data.RelationNotes != null)
                result.RelationNotes = behavior.ImportRelationNotes(data.RelationNotes);
            if (data.TagNotes != null)
                result.TagNotes = behavior.ImportTagNotes(data.TagNotes);
            if (data.CultureDefs != null && data.CultureDefs.Count > 0)
                EncyclopediaEditBehavior.NeedsCustomCultureReapply = true;
            return result;
        }

        public class ImportResult
        {
            public int Descriptions;
            public int Names;
            public int Titles;
            public int Banners;
            public int CultureDefs;
            public int Cultures;
            public int Occupations;
            public int HeroInfoFields;
            public int Tags;
            public int Journal;
            public int RelationNotes;
            public int TagNotes;
            public int Total => Descriptions + Names + Titles + Banners + CultureDefs + Cultures + Occupations + HeroInfoFields + Tags + Journal + RelationNotes + TagNotes;
        }

        /// <summary>
        /// Removes all custom descriptions from the current campaign.
        /// Returns the number of descriptions that were removed.
        /// </summary>
        public static int ResetAllDescriptions()
        {
            if (!IsAvailable) return 0;
            return EncyclopediaEditBehavior.Instance.ClearAllDescriptions();
        }

        // ── Culture/Occupation Access ────────────────────────

        /// <summary>
        /// Returns the custom culture display name for a hero, or null if none set.
        /// </summary>
        public static string GetHeroCulture(string heroId)
        {
            if (!IsAvailable || string.IsNullOrEmpty(heroId)) return null;
            return EncyclopediaEditBehavior.Instance.GetCustomCultureDisplayName(heroId);
        }

        /// <summary>
        /// Returns the custom occupation value for a hero, or -1 if none set.
        /// </summary>
        public static int GetHeroOccupation(string heroId)
        {
            if (!IsAvailable || string.IsNullOrEmpty(heroId)) return -1;
            return EncyclopediaEditBehavior.Instance.GetCustomOccupation(heroId);
        }

        /// <summary>
        /// Returns the friendly display name for a custom occupation value.
        /// E.g., 21 → "Gang Leader".
        /// </summary>
        public static string GetOccupationDisplayName(int occupationValue)
        {
            return EncyclopediaOccupationEditPopup.GetFriendlyName(occupationValue);
        }

        /// <summary>
        /// Returns the count of heroes with custom culture assignments.
        /// </summary>
        public static int GetCustomCultureCount()
        {
            if (!IsAvailable) return 0;
            return EncyclopediaEditBehavior.Instance.GetCustomCultureCount();
        }

        /// <summary>
        /// Returns the count of heroes with custom occupation assignments.
        /// </summary>
        public static int GetCustomOccupationCount()
        {
            if (!IsAvailable) return 0;
            return EncyclopediaEditBehavior.Instance.GetCustomOccupationCount();
        }

        /// <summary>
        /// Returns all custom cultures for export (heroId → "cultureId|displayName").
        /// </summary>
        public static Dictionary<string, string> GetAllCustomCultures()
        {
            if (!IsAvailable) return new Dictionary<string, string>();
            return EncyclopediaEditBehavior.Instance.GetAllCustomCulturesForExport();
        }

        /// <summary>
        /// Returns all custom occupations for export (heroId → occupation enum name).
        /// </summary>
        public static Dictionary<string, string> GetAllCustomOccupations()
        {
            if (!IsAvailable) return new Dictionary<string, string>();
            return EncyclopediaEditBehavior.Instance.GetAllCustomOccupationsForExport();
        }

        // ── Hero Info Fields (Backstory, Personality, etc.) ─────────

        /// <summary>
        /// Gets a structured hero info field value, or null if not set.
        /// </summary>
        /// <param name="fieldKey">One of: backstory, personality, goals, relationships, rumors</param>
        /// <param name="heroId">The hero's StringId</param>
        public static string GetHeroInfoField(string fieldKey, string heroId)
        {
            if (!IsAvailable || string.IsNullOrEmpty(fieldKey) || string.IsNullOrEmpty(heroId))
                return null;
            return EncyclopediaEditBehavior.Instance.GetHeroInfoField(fieldKey, heroId);
        }

        /// <summary>
        /// Sets (or clears) a structured hero info field.
        /// </summary>
        public static void SetHeroInfoField(string fieldKey, string heroId, string text)
        {
            if (!IsAvailable || string.IsNullOrEmpty(fieldKey) || string.IsNullOrEmpty(heroId))
                return;
            EncyclopediaEditBehavior.Instance.SetHeroInfoField(fieldKey, heroId, text);
        }

        /// <summary>
        /// Returns true if the hero has at least one non-empty info field.
        /// </summary>
        public static bool HasAnyHeroInfoField(string heroId)
        {
            if (!IsAvailable || string.IsNullOrEmpty(heroId)) return false;
            return EncyclopediaEditBehavior.Instance.HasAnyHeroInfoField(heroId);
        }

        /// <summary>
        /// Returns the total count of hero info field entries.
        /// </summary>
        public static int GetHeroInfoFieldCount()
        {
            if (!IsAvailable) return 0;
            return EncyclopediaEditBehavior.Instance.GetHeroInfoFieldCount();
        }

        /// <summary>
        /// Returns all hero info fields for export ("fieldKey_heroId" → text).
        /// </summary>
        public static Dictionary<string, string> GetAllHeroInfoFields()
        {
            if (!IsAvailable) return new Dictionary<string, string>();
            return EncyclopediaEditBehavior.Instance.GetAllHeroInfoFieldsForExport();
        }

        /// <summary>
        /// Returns the available info field keys in display order.
        /// </summary>
        public static string[] GetInfoFieldKeys()
        {
            return (string[])EncyclopediaEditBehavior.InfoFieldKeys.Clone();
        }

        /// <summary>
        /// Returns ALL lore fields for a hero as a dictionary (fieldKey -> text).
        /// Field keys: "backstory", "personality", "goals", "relationships", "rumors", "chronicle".
        /// Only includes fields that have been set (non-null, non-empty).
        /// </summary>
        public static Dictionary<string, string> GetAllHeroLoreFields(string heroId)
        {
            var result = new Dictionary<string, string>();
            if (!IsAvailable || string.IsNullOrEmpty(heroId)) return result;
            try
            {
                foreach (var key in EncyclopediaEditBehavior.InfoFieldKeys)
                {
                    if (key == "description") continue; // description is separate
                    string val = EncyclopediaEditBehavior.Instance.GetHeroInfoField(key, heroId);
                    if (!string.IsNullOrEmpty(val))
                        result[key] = val;
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("EditableEncyclopediaAPI: GetAllHeroLoreFields failed: " + ex.ToString()); }
            return result;
        }

        /// <summary>
        /// Returns true if a hero has ANY lore field set (backstory, personality, goals, relationships, rumors, or chronicle).
        /// </summary>
        public static bool HasHeroLore(string heroId)
        {
            if (!IsAvailable || string.IsNullOrEmpty(heroId)) return false;
            return EncyclopediaEditBehavior.Instance.HasAnyHeroInfoField(heroId);
        }

        /// <summary>
        /// Returns the resolved Lore Story Template for a hero and field key.
        /// Placeholders ({name}, {culture}, {settlement}, {clan}, {faction}, {occupation}, {date})
        /// are filled with the hero's actual values.
        /// Returns null if no template exists for the field.
        /// Tries occupation-specific first (e.g., "Lord"), then culture-specific, then default.
        /// </summary>
        public static string GetLoreTemplate(string fieldKey, string heroId)
        {
            try
            {
                string templateKey = "template_" + fieldKey;
                string template = null;
                Hero hero = null;

                // Find hero
                if (!string.IsNullOrEmpty(heroId))
                {
                    foreach (var h in Hero.AllAliveHeroes)
                        if (h != null && h.StringId == heroId) { hero = h; break; }
                    if (hero == null)
                        foreach (var h in Hero.DeadOrDisabledHeroes)
                            if (h != null && h.StringId == heroId) { hero = h; break; }
                }

                if (hero != null)
                {
                    // Occupation-specific
                    string occKey = templateKey + "_" + hero.Occupation.ToString();
                    string occTemplate = Localization.L(occKey);
                    if (occTemplate != occKey) template = occTemplate;

                    // Culture-specific
                    if (template == null && hero.Culture?.Name != null)
                    {
                        string cultureKey = templateKey + "_" + hero.Culture.StringId;
                        string cultureTemplate = Localization.L(cultureKey);
                        if (cultureTemplate != cultureKey) template = cultureTemplate;
                    }
                }

                // Default template
                if (template == null)
                {
                    string defaultTemplate = Localization.L(templateKey);
                    if (defaultTemplate != templateKey) template = defaultTemplate;
                }

                if (template == null) return null;

                // Resolve placeholders
                if (hero != null)
                {
                    template = template.Replace("{name}", hero.Name?.ToString() ?? "");
                    template = template.Replace("{culture}", hero.Culture?.Name?.ToString() ?? "");
                    template = template.Replace("{faction}", hero.Clan?.Kingdom?.Name?.ToString() ?? "");
                    template = template.Replace("{clan}", hero.Clan?.Name?.ToString() ?? "");
                    template = template.Replace("{settlement}", hero.HomeSettlement?.Name?.ToString() ?? "");
                    template = template.Replace("{occupation}", hero.Occupation.ToString());
                }
                return template;
            }
            catch (Exception ex) { MCMSettings.DebugLog("EditableEncyclopediaAPI: GetLoreTemplate failed: " + ex.ToString()); return null; }
        }

        /// <summary>
        /// Returns the raw (unresolved) Lore Story Template for a character role and field.
        /// Role names: "Lord", "Merchant", "Wanderer", "GangLeader", "Preacher".
        /// Field keys: "backstory", "personality", "goals", "relationships", "rumors".
        /// Returns null if no template exists.
        /// </summary>
        public static string GetRoleTemplate(string role, string fieldKey)
        {
            try
            {
                string key = "template_" + fieldKey + "_" + role;
                string result = Localization.L(key);
                return result != key ? result : null;
            }
            catch (Exception ex) { MCMSettings.DebugLog("EditableEncyclopediaAPI: GetRoleTemplate failed: " + ex.ToString()); return null; }
        }

        /// <summary>
        /// Returns all available character role names that have Lore Story Templates.
        /// Default roles: Lord, Merchant, Wanderer, GangLeader, Preacher.
        /// </summary>
        public static string[] GetAvailableRoles()
        {
            return new[] { "Lord", "Merchant", "Wanderer", "GangLeader", "Preacher" };
        }

        /// <summary>
        /// Returns the lore field keys that have templates.
        /// Default: backstory, personality, goals, relationships, rumors.
        /// (Chronicle does not have templates — it's auto-dated entries.)
        /// </summary>
        public static string[] GetTemplateFieldKeys()
        {
            return new[] { "backstory", "personality", "goals", "relationships", "rumors" };
        }

        /// <summary>
        /// Returns ALL templates for a role as a dictionary (fieldKey -> raw template text).
        /// Example: GetAllRoleTemplates("Lord") returns backstory, personality, goals,
        /// relationships, and rumors templates for the Lord role.
        /// </summary>
        public static Dictionary<string, string> GetAllRoleTemplates(string role)
        {
            var result = new Dictionary<string, string>();
            foreach (var field in GetTemplateFieldKeys())
            {
                string tmpl = GetRoleTemplate(role, field);
                if (tmpl != null) result[field] = tmpl;
            }
            return result;
        }

        // ── Tags ──────────────────────────────────────────────────

        /// <summary>
        /// Gets the comma-separated tags for an object, or null if none set.
        /// </summary>
        public static string GetTags(string objectId)
        {
            if (!IsAvailable || string.IsNullOrEmpty(objectId))
                return null;
            return EncyclopediaEditBehavior.Instance.GetTags(objectId);
        }

        /// <summary>
        /// Sets (or clears) the tags for an object.
        /// Pass a comma-separated string (e.g. "ally, target, recruit").
        /// </summary>
        public static void SetTags(string objectId, string tags)
        {
            if (!IsAvailable || string.IsNullOrEmpty(objectId))
                return;
            EncyclopediaEditBehavior.Instance.SetTags(objectId, tags);
        }

        /// <summary>
        /// Returns true if the object has any tags assigned.
        /// </summary>
        public static bool HasTags(string objectId)
        {
            if (!IsAvailable || string.IsNullOrEmpty(objectId))
                return false;
            return EncyclopediaEditBehavior.Instance.HasTags(objectId);
        }

        /// <summary>
        /// Returns the total number of objects that have tags.
        /// </summary>
        public static int GetTagCount()
        {
            if (!IsAvailable) return 0;
            return EncyclopediaEditBehavior.Instance.GetTagCount();
        }

        /// <summary>
        /// Returns all tags for export (objectId → comma-separated tag string).
        /// </summary>
        public static Dictionary<string, string> GetAllTags()
        {
            if (!IsAvailable) return new Dictionary<string, string>();
            return EncyclopediaEditBehavior.Instance.GetAllTagsForExport();
        }

        // ── Tag Querying ──────────────────────────────────────────

        /// <summary>
        /// Returns all object IDs that have the specified tag (case-insensitive).
        /// </summary>
        public static List<string> GetObjectsWithTag(string tag)
        {
            if (!IsAvailable) return new List<string>();
            return EncyclopediaEditBehavior.Instance.GetObjectsWithTag(tag);
        }

        /// <summary>
        /// Returns all object IDs that have ANY of the specified tags.
        /// </summary>
        public static List<string> GetObjectsWithAnyTag(IEnumerable<string> tags)
        {
            if (!IsAvailable) return new List<string>();
            return EncyclopediaEditBehavior.Instance.GetObjectsWithAnyTag(tags);
        }

        /// <summary>
        /// Returns all object IDs that have ALL of the specified tags.
        /// </summary>
        public static List<string> GetObjectsWithAllTags(IEnumerable<string> tags)
        {
            if (!IsAvailable) return new List<string>();
            return EncyclopediaEditBehavior.Instance.GetObjectsWithAllTags(tags);
        }

        /// <summary>
        /// Returns a sorted list of all unique tags with their usage counts.
        /// </summary>
        public static List<TagUsageInfo> GetTagUsageCounts()
        {
            if (!IsAvailable) return new List<TagUsageInfo>();
            return EncyclopediaEditBehavior.Instance.GetTagUsageCounts();
        }

        /// <summary>
        /// Returns the usage count for a specific tag across all entries.
        /// </summary>
        public static int GetTagUsageCount(string tag)
        {
            if (!IsAvailable) return 0;
            return EncyclopediaEditBehavior.Instance.GetTagUsageCount(tag);
        }

        /// <summary>
        /// Returns a cached, sorted list of all unique tags.
        /// More efficient than parsing GetAllTags() for repeated calls.
        /// </summary>
        public static List<string> GetAllUniqueTags()
        {
            if (!IsAvailable) return new List<string>();
            return EncyclopediaEditBehavior.Instance.GetAllUniqueTags();
        }

        // ── Tag Bulk Operations ───────────────────────────────────

        /// <summary>
        /// Renames a tag globally across all entries.
        /// Returns the number of entries updated.
        /// </summary>
        public static int RenameTagGlobal(string oldTag, string newTag)
        {
            if (!IsAvailable) return 0;
            return EncyclopediaEditBehavior.Instance.RenameTagGlobal(oldTag, newTag);
        }

        /// <summary>
        /// Removes a tag globally from all entries.
        /// Returns the number of entries updated.
        /// </summary>
        public static int RemoveTagGlobal(string tag)
        {
            if (!IsAvailable) return 0;
            return EncyclopediaEditBehavior.Instance.RemoveTagGlobal(tag);
        }

        /// <summary>
        /// Adds a tag to multiple entries at once.
        /// Returns the number of entries updated.
        /// </summary>
        public static int AddTagToMultiple(IEnumerable<string> objectIds, string tag)
        {
            if (!IsAvailable) return 0;
            return EncyclopediaEditBehavior.Instance.AddTagToMultiple(objectIds, tag);
        }

        /// <summary>
        /// Removes a tag from multiple entries at once.
        /// Returns the number of entries updated.
        /// </summary>
        public static int RemoveTagFromMultiple(IEnumerable<string> objectIds, string tag)
        {
            if (!IsAvailable) return 0;
            return EncyclopediaEditBehavior.Instance.RemoveTagFromMultiple(objectIds, tag);
        }

        /// <summary>
        /// Merges two tags: replaces all occurrences of sourceTag with targetTag.
        /// Returns the number of entries updated.
        /// </summary>
        public static int MergeTags(string sourceTag, string targetTag)
        {
            if (!IsAvailable) return 0;
            return EncyclopediaEditBehavior.Instance.MergeTags(sourceTag, targetTag);
        }

        /// <summary>
        /// Clears all tags from all entries.
        /// Returns the number of entries that were cleared.
        /// </summary>
        public static int ClearAllTags()
        {
            if (!IsAvailable) return 0;
            return EncyclopediaEditBehavior.Instance.ClearAllTags();
        }

        // ── Tag Categories ───────────────────────────────────────

        /// <summary>
        /// Gets all user-defined tag categories.
        /// </summary>
        public static Dictionary<string, string> GetAllTagCategories()
        {
            if (!IsAvailable) return new Dictionary<string, string>();
            return EncyclopediaEditBehavior.Instance.GetAllTagCategories();
        }

        /// <summary>
        /// Sets or clears a tag category (name → comma-separated tags).
        /// </summary>
        public static void SetTagCategory(string categoryName, string tags)
        {
            if (!IsAvailable) return;
            EncyclopediaEditBehavior.Instance.SetTagCategory(categoryName, tags);
        }

        /// <summary>
        /// Removes a tag category.
        /// </summary>
        public static void RemoveTagCategory(string categoryName)
        {
            if (!IsAvailable) return;
            EncyclopediaEditBehavior.Instance.RemoveTagCategory(categoryName);
        }

        /// <summary>
        /// Returns the category a tag belongs to, or null.
        /// </summary>
        public static string GetTagCategory(string tag)
        {
            if (!IsAvailable) return null;
            return EncyclopediaEditBehavior.Instance.GetTagCategory(tag);
        }

        // ── Tag Presets/Templates ────────────────────────────────

        /// <summary>
        /// Gets all saved tag presets.
        /// </summary>
        public static Dictionary<string, string> GetAllTagPresets()
        {
            if (!IsAvailable) return new Dictionary<string, string>();
            return EncyclopediaEditBehavior.Instance.GetAllTagPresets();
        }

        /// <summary>
        /// Gets a specific tag preset by name.
        /// </summary>
        public static string GetTagPreset(string presetName)
        {
            if (!IsAvailable) return null;
            return EncyclopediaEditBehavior.Instance.GetTagPreset(presetName);
        }

        /// <summary>
        /// Saves a tag preset.
        /// </summary>
        public static void SetTagPreset(string presetName, string tags)
        {
            if (!IsAvailable) return;
            EncyclopediaEditBehavior.Instance.SetTagPreset(presetName, tags);
        }

        /// <summary>
        /// Removes a tag preset.
        /// </summary>
        public static void RemoveTagPreset(string presetName)
        {
            if (!IsAvailable) return;
            EncyclopediaEditBehavior.Instance.RemoveTagPreset(presetName);
        }

        /// <summary>
        /// Applies a preset to an object, merging with existing tags.
        /// </summary>
        public static string ApplyTagPreset(string objectId, string presetName)
        {
            if (!IsAvailable) return null;
            return EncyclopediaEditBehavior.Instance.ApplyTagPreset(objectId, presetName);
        }

        /// <summary>
        /// Returns the number of saved tag presets.
        /// </summary>
        public static int GetTagPresetCount()
        {
            if (!IsAvailable) return 0;
            return EncyclopediaEditBehavior.Instance.GetTagPresetCount();
        }

        // ── Per-Hero Auto-Tag Thresholds ─────────────────────────

        /// <summary>
        /// Gets per-hero auto-tag thresholds, or null if using global defaults.
        /// </summary>
        public static Tuple<int, int> GetPerHeroAutoTagThresholds(string heroId)
        {
            if (!IsAvailable) return null;
            return EncyclopediaEditBehavior.Instance.GetPerHeroAutoTagThresholds(heroId);
        }

        /// <summary>
        /// Sets per-hero auto-tag thresholds. Pass null to clear.
        /// </summary>
        public static void SetPerHeroAutoTagThresholds(string heroId, int? enemyThreshold, int? friendThreshold)
        {
            if (!IsAvailable) return;
            EncyclopediaEditBehavior.Instance.SetPerHeroAutoTagThresholds(heroId, enemyThreshold, friendThreshold);
        }

        /// <summary>
        /// Returns all per-hero thresholds for export.
        /// </summary>
        public static Dictionary<string, string> GetAllPerHeroAutoTagThresholds()
        {
            if (!IsAvailable) return new Dictionary<string, string>();
            return EncyclopediaEditBehavior.Instance.GetAllPerHeroAutoTagThresholds();
        }

        // ── Tag Filtering ────────────────────────────────────────

        /// <summary>
        /// Returns detailed info about all objects with a specific tag.
        /// Each tuple: (objectId, displayName, objectType).
        /// </summary>
        public static List<Tuple<string, string, string>> GetObjectsWithTagDetailed(string tag)
        {
            if (!IsAvailable) return new List<Tuple<string, string, string>>();
            return EncyclopediaEditBehavior.Instance.GetObjectsWithTagDetailed(tag);
        }

        // ── Tag Change Notification ───────────────────────────────

        /// <summary>
        /// Event fired whenever tags are saved or removed.
        /// Args: (string objectId, string newTags) — newTags is null if cleared.
        /// </summary>
        public static event Action<string, string> OnTagsChanged;

        /// <summary>
        /// Internal method to raise the OnTagsChanged event.
        /// </summary>
        internal static void RaiseTagsChanged(string objectId, string tags)
        {
            try
            {
                OnTagsChanged?.Invoke(objectId, tags);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog($"Error in OnTagsChanged event handler: {ex}");
            }
        }

        // ── Journal ──────────────────────────────────────────────

        /// <summary>
        /// Gets all journal entries for an object as a list of JournalEntry (Date + Text).
        /// Returns an empty list if no entries exist.
        /// </summary>
        public static List<JournalEntry> GetJournalEntries(string objectId)
        {
            if (!IsAvailable || string.IsNullOrEmpty(objectId))
                return new List<JournalEntry>();
            return EncyclopediaEditBehavior.Instance.GetJournalEntries(objectId);
        }

        /// <summary>
        /// Adds a journal entry with the current campaign date to the specified object.
        /// </summary>
        public static void AddJournalEntry(string objectId, string text)
        {
            if (!IsAvailable || string.IsNullOrEmpty(objectId)) return;
            EncyclopediaEditBehavior.Instance.AddJournalEntry(objectId, text);
        }

        /// <summary>
        /// Returns true if the object has any journal entries.
        /// </summary>
        public static bool HasJournal(string objectId)
        {
            if (!IsAvailable || string.IsNullOrEmpty(objectId)) return false;
            return EncyclopediaEditBehavior.Instance.HasJournal(objectId);
        }

        /// <summary>
        /// Clears all journal entries for an object.
        /// </summary>
        public static void ClearJournal(string objectId)
        {
            if (!IsAvailable || string.IsNullOrEmpty(objectId)) return;
            EncyclopediaEditBehavior.Instance.ClearJournal(objectId);
        }

        /// <summary>
        /// Returns the total number of objects that have journal entries.
        /// </summary>
        public static int GetJournalCount()
        {
            if (!IsAvailable) return 0;
            return EncyclopediaEditBehavior.Instance.GetJournalCount();
        }

        /// <summary>
        /// Returns all journal data for export (objectId → raw journal string).
        /// </summary>
        public static Dictionary<string, string> GetAllJournal()
        {
            if (!IsAvailable) return new Dictionary<string, string>();
            return EncyclopediaEditBehavior.Instance.GetAllJournalForExport();
        }

        /// <summary>
        /// Event fired whenever journal entries are added or cleared.
        /// Args: (string objectId)
        /// </summary>
        public static event Action<string> OnJournalChanged;

        internal static void RaiseJournalChanged(string objectId)
        {
            try
            {
                OnJournalChanged?.Invoke(objectId);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog($"Error in OnJournalChanged event handler: {ex}");
            }
        }

        // ── Relation Notes ──────────────────────────────────────────

        /// <summary>
        /// Sets a relation note from the main hero to a target hero.
        /// If note is null or empty, the note is removed to keep saves clean.
        /// </summary>
        public static void SetRelationNote(Hero target, string note)
        {
            if (!IsAvailable || target == null) return;
            string heroId = Hero.MainHero?.StringId;
            if (string.IsNullOrEmpty(heroId)) return;
            EncyclopediaEditBehavior.Instance.SetRelationNote(heroId, target.StringId, note);
            RaiseRelationNoteChanged(heroId, target.StringId, note);
        }

        /// <summary>
        /// Gets the relation note from the main hero to a target hero.
        /// Returns null if no note exists.
        /// </summary>
        public static string GetRelationNote(Hero target)
        {
            if (!IsAvailable || target == null) return null;
            string heroId = Hero.MainHero?.StringId;
            if (string.IsNullOrEmpty(heroId)) return null;
            return EncyclopediaEditBehavior.Instance.GetRelationNote(heroId, target.StringId);
        }

        /// <summary>
        /// Removes the relation note from the main hero to a target hero.
        /// </summary>
        public static void RemoveRelationNote(Hero target)
        {
            if (!IsAvailable || target == null) return;
            string heroId = Hero.MainHero?.StringId;
            if (string.IsNullOrEmpty(heroId)) return;
            EncyclopediaEditBehavior.Instance.SetRelationNote(heroId, target.StringId, null);
            RaiseRelationNoteChanged(heroId, target.StringId, null);
        }

        /// <summary>
        /// Returns true if the main hero has a relation note for the target hero.
        /// </summary>
        public static bool HasRelationNote(Hero target)
        {
            if (!IsAvailable || target == null) return false;
            string heroId = Hero.MainHero?.StringId;
            if (string.IsNullOrEmpty(heroId)) return false;
            return EncyclopediaEditBehavior.Instance.HasRelationNote(heroId, target.StringId);
        }

        /// <summary>
        /// Gets a relation note between any two heroes by their StringIds.
        /// Useful for mods that need to read notes for non-main heroes.
        /// </summary>
        public static string GetRelationNote(string heroId, string targetHeroId)
        {
            if (!IsAvailable || string.IsNullOrEmpty(heroId) || string.IsNullOrEmpty(targetHeroId))
                return null;
            return EncyclopediaEditBehavior.Instance.GetRelationNote(heroId, targetHeroId);
        }

        /// <summary>
        /// Sets a relation note between any two heroes by their StringIds.
        /// </summary>
        public static void SetRelationNote(string heroId, string targetHeroId, string note)
        {
            if (!IsAvailable || string.IsNullOrEmpty(heroId) || string.IsNullOrEmpty(targetHeroId))
                return;
            EncyclopediaEditBehavior.Instance.SetRelationNote(heroId, targetHeroId, note);
            RaiseRelationNoteChanged(heroId, targetHeroId, note);
        }

        /// <summary>
        /// Returns all relation notes for export (key → note text).
        /// </summary>
        public static Dictionary<string, string> GetAllRelationNotes()
        {
            if (!IsAvailable) return new Dictionary<string, string>();
            return EncyclopediaEditBehavior.Instance.GetAllRelationNotesForExport();
        }

        /// <summary>
        /// Returns the total number of relation notes stored.
        /// </summary>
        public static int GetRelationNoteCount()
        {
            if (!IsAvailable) return 0;
            return EncyclopediaEditBehavior.Instance.GetRelationNoteCount();
        }

        /// <summary>
        /// Event fired whenever a relation note is saved or removed.
        /// Args: (string heroId, string targetHeroId, string note) — note is null if removed.
        /// </summary>
        public static event Action<string, string, string> OnRelationNoteChanged;

        internal static void RaiseRelationNoteChanged(string heroId, string targetHeroId, string note)
        {
            try
            {
                OnRelationNoteChanged?.Invoke(heroId, targetHeroId, note);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog($"Error in OnRelationNoteChanged event handler: {ex}");
            }
        }

        // ── Info Stats (Computed from Game State) ───────────────

        /// <summary>
        /// Resolves a Hero by StringId across living and dead/disabled heroes.
        /// Single resolution path shared by every by-id hero overload on this API.
        /// Returns null when the id is empty or no hero matches.
        /// </summary>
        private static Hero ResolveHero(string heroId)
        {
            if (string.IsNullOrEmpty(heroId)) return null;
            try
            {
                foreach (var h in Hero.AllAliveHeroes)
                    if (h != null && h.StringId == heroId)
                        return h;
                foreach (var h in Hero.DeadOrDisabledHeroes)
                    if (h != null && h.StringId == heroId)
                        return h;
            }
            catch (Exception ex) { MCMSettings.DebugLog("EditableEncyclopediaAPI: ResolveHero failed: " + ex.ToString()); }
            return null;
        }

        /// <summary>
        /// Returns computed info stats for a Hero as key-value pairs, fog-masked.
        /// Rows come from the shared <see cref="HeroInfoStats"/> set — the same one the
        /// encyclopedia page renders — so the API and the page can never drift apart.
        /// Includes: Kingdom/Faction, Location, Status, Relation, Spouse, Children, At War,
        /// Wealth, Influence, Daily Income, Troops, Mercenaries, Morale, Lords, Companions,
        /// Towns, Castles, Garrisons, Workshops, Caravans, Alleys, Supporters,
        /// Battles, Kills, Tournaments, Hall Rank.
        /// Every value reads "???" while the hero is unknown to the player, matching what
        /// vanilla already does to Age and Occupation.
        /// Returns empty dictionary if hero not found or not available.
        /// NOTE: Dictionary guarantees no ordering — use <see cref="GetHeroInfoStatsOrdered(Hero)"/>
        /// when you intend to render these rows.
        /// </summary>
        public static Dictionary<string, string> GetHeroInfoStats(Hero hero)
        {
            var result = new Dictionary<string, string>();

            // Delegates to the ordered method so there is exactly ONE hero derivation.
            // Anything added there (Culture/Occupation included) shows up here automatically.
            //
            // Continuation rows need folding. HeroInfoStats emits the 2nd..Nth "At War" faction
            // under an EMPTY label — a Gauntlet trick so they line up beneath a fixed-width
            // column. That is right for an ordered list, but a dictionary has no row order to
            // lean on: copying them verbatim would file every continuation under the same ""
            // key, so only the LAST war would survive (a 3-war hero silently lost the middle
            // one) and a blank-label entry would leak into EE-WebAPI's JSON. Folding each
            // continuation into the preceding real key is lossless and keeps the payload clean.
            string lastKey = null;
            foreach (var row in GetHeroInfoStatsOrdered(hero))
            {
                if (!string.IsNullOrEmpty(row.Key))
                {
                    lastKey = row.Key;
                    // Indexer, never Add: a repeated key must overwrite, not throw.
                    result[lastKey] = row.Value;
                    continue;
                }

                // Defensive: a continuation with nothing to continue is dropped, not thrown on.
                if (lastKey == null) continue;

                string existing;
                if (result.TryGetValue(lastKey, out existing) && !string.IsNullOrEmpty(existing))
                    result[lastKey] = existing + ", " + row.Value;
                else
                    result[lastKey] = row.Value;
            }
            return result;
        }

        /// <summary>
        /// Hero Info stats in display order, already fog-masked. Prefer this over
        /// GetHeroInfoStats when you intend to render the rows — Dictionary guarantees no ordering.
        /// </summary>
        public static List<KeyValuePair<string, string>> GetHeroInfoStatsOrdered(Hero hero)
        {
            var rows = new List<KeyValuePair<string, string>>();
            if (hero == null) return rows;
            try
            {
                // Fog of war: hide everything for a hero the player has not met, matching what
                // vanilla does to Age and Occupation. Defaulting to hidden means a failed property
                // read errs toward concealing information rather than leaking it.
                bool hidden = true;
                try { hidden = !hero.IsKnownToPlayer; } catch { }

                // Culture, Occupation and Clan are API-only rows and lead the list; Culture and
                // Occupation keep positions 1 and 2, which is where the pre-2026-08-02 payload
                // had them. All three are deliberately NOT in HeroInfoStats: the encyclopedia
                // hero page renders them natively, so adding them there would paint duplicate
                // rows on a shipped panel. The API has no such native rows, so it keeps them and
                // stays a superset of what it returned before.
                //
                // Clan matters because HeroInfoStats only ever emits "Kingdom" (or "Faction" for
                // a minor faction). A hero in an ordinary kingdom clan had no clan row at all,
                // which is the most common case and left consumers showing no clan.
                //
                // Masked from HeroInfoStats.Hidden — one source of truth, never a literal "???".
                // The real values are not even read while hidden, so there is no path by which an
                // unmet hero's culture, occupation or clan reaches a caller.
                string culture = HeroInfoStats.Hidden;
                string occupation = HeroInfoStats.Hidden;
                string clanName = HeroInfoStats.Hidden;
                if (!hidden)
                {
                    // Same derivation the old API body used, so values match what consumers
                    // received before. Note this reads VANILLA culture/occupation only — the old
                    // body never consulted EncyclopediaEditBehavior for an EE custom override,
                    // and changing that here would silently alter the payload.
                    culture = SafeGet(() => hero.Culture?.Name?.ToString());
                    occupation = SafeGet(() =>
                    {
                        string occName = hero.CharacterObject?.Occupation.ToString() ?? "Unknown";
                        string friendly = EncyclopediaOccupationEditPopup.GetFriendlyName(occName);
                        return friendly ?? occName;
                    });
                    // null fallback, not "Unknown": a clanless hero (or a failed read) must drop
                    // the row entirely rather than assert anything. The "Kingdom" = "None" row
                    // that HeroInfoStats emits already covers the factionless case.
                    clanName = SafeGet(() => hero.Clan?.Name?.ToString(), null);
                }
                rows.Add(new KeyValuePair<string, string>("Culture", culture));
                rows.Add(new KeyValuePair<string, string>("Occupation", occupation));
                // While hidden this is the mask and therefore always present, so the row's mere
                // absence can never betray that an unmet hero is clanless.
                if (!string.IsNullOrEmpty(clanName))
                    rows.Add(new KeyValuePair<string, string>("Clan", clanName));

                rows.AddRange(HeroInfoStats.Build(hero, hidden));
            }
            catch (Exception ex) { MCMSettings.DebugLog("EditableEncyclopediaAPI: GetHeroInfoStatsOrdered failed: " + ex.ToString()); }
            return rows;
        }

        /// <summary>
        /// Returns computed info stats for a Hero by StringId.
        /// </summary>
        public static Dictionary<string, string> GetHeroInfoStats(string heroId)
        {
            var hero = ResolveHero(heroId);
            if (hero == null) return new Dictionary<string, string>();
            return GetHeroInfoStats(hero);
        }

        /// <summary>
        /// Hero Info stats in display order for a hero StringId, already fog-masked.
        /// Prefer this over GetHeroInfoStats(string) when you intend to render the rows.
        /// </summary>
        public static List<KeyValuePair<string, string>> GetHeroInfoStatsOrdered(string heroId)
        {
            var hero = ResolveHero(heroId);
            if (hero == null) return new List<KeyValuePair<string, string>>();
            return GetHeroInfoStatsOrdered(hero);
        }

        /// <summary>
        /// Returns computed info stats for a Clan.
        /// Includes: Kingdom, Leader, Culture, Renown, Influence, Troops, Parties,
        /// Lords, Companions, Towns, Castles, Villages, Caravans, At War With.
        /// </summary>
        public static Dictionary<string, string> GetClanInfoStats(Clan clan)
        {
            var stats = new Dictionary<string, string>();
            // Indexer, not Add: the ordered list is the single derivation and a future row
            // could repeat a label. Last write wins, exactly as the old inline code did.
            foreach (var row in GetClanInfoStatsOrdered(clan))
                stats[row.Key] = row.Value;
            return stats;
        }

        /// <summary>
        /// Clan Info stats in display order. Prefer this over GetClanInfoStats when you intend
        /// to render the rows — Dictionary guarantees no ordering.
        /// Not fog-masked: verified against shipped Bannerlord 1.4.7, EncyclopediaClanPageVM
        /// exposes no hidden-information property. Vanilla fogs heroes only.
        /// </summary>
        public static List<KeyValuePair<string, string>> GetClanInfoStatsOrdered(Clan clan)
        {
            var rows = new List<KeyValuePair<string, string>>();
            if (clan == null) return rows;
            Action<string, string> add = (label, value) =>
                rows.Add(new KeyValuePair<string, string>(label, value));
            try
            {
                add("Kingdom", SafeGet(() => clan.Kingdom?.Name?.ToString(), "Independent"));
                add("Leader", SafeGet(() => clan.Leader?.Name?.ToString()));
                add("Culture", SafeGet(() => clan.Culture?.Name?.ToString()));
                add("Renown", SafeGet(() => ((int)clan.Renown).ToString(), "0"));
                add("Influence", SafeGet(() => ((int)clan.Influence).ToString(), "0"));
                add("Troops", SafeGet(() =>
                {
                    int troops = 0;
                    foreach (var wpc in clan.WarPartyComponents)
                        if (wpc?.MobileParty?.MemberRoster != null)
                            troops += wpc.MobileParty.MemberRoster.TotalManCount;
                    return troops.ToString();
                }, "0"));
                add("Parties", SafeGet(() =>
                {
                    int parties = 0;
                    foreach (var wpc in clan.WarPartyComponents)
                        if (wpc?.MobileParty?.MemberRoster != null) parties++;
                    return parties.ToString();
                }, "0"));
                add("Lords", SafeGet(() =>
                {
                    int lords = 0;
                    foreach (var h in clan.Heroes)
                        if (h != null && h.IsAlive && h.IsLord) lords++;
                    return lords.ToString();
                }, "0"));
                add("Companions", SafeGet(() =>
                {
                    int companions = 0;
                    foreach (var h in clan.Heroes)
                        if (h != null && h.IsAlive && !h.IsLord) companions++;
                    return companions.ToString();
                }, "0"));
                add("Towns", SafeGet(() =>
                {
                    int towns = 0;
                    foreach (var s in clan.Settlements)
                        if (s != null && s.IsTown) towns++;
                    return towns.ToString();
                }, "0"));
                add("Castles", SafeGet(() =>
                {
                    int castles = 0;
                    foreach (var s in clan.Settlements)
                        if (s != null && s.IsCastle) castles++;
                    return castles.ToString();
                }, "0"));
                add("Villages", SafeGet(() =>
                {
                    int villages = 0;
                    foreach (var s in clan.Settlements)
                        if (s != null && s.IsVillage) villages++;
                    return villages.ToString();
                }, "0"));
            }
            catch (Exception ex) { MCMSettings.DebugLog("EditableEncyclopediaAPI: GetClanInfoStatsOrdered failed: " + ex.ToString()); }
            return rows;
        }

        /// <summary>
        /// Returns computed info stats for a Kingdom.
        /// Includes: Ruler, Culture, Clans, Lords, Towns, Castles, Villages,
        /// Strength, Influence, Wars, Total Troops, Total Garrisons.
        /// </summary>
        public static Dictionary<string, string> GetKingdomInfoStats(Kingdom kingdom)
        {
            var stats = new Dictionary<string, string>();
            foreach (var row in GetKingdomInfoStatsOrdered(kingdom))
                stats[row.Key] = row.Value;
            return stats;
        }

        /// <summary>
        /// Kingdom Info stats in display order. Prefer this over GetKingdomInfoStats when you
        /// intend to render the rows — Dictionary guarantees no ordering.
        /// Not fog-masked: verified against shipped Bannerlord 1.4.7, EncyclopediaFactionPageVM
        /// exposes no hidden-information property. Vanilla fogs heroes only.
        /// </summary>
        public static List<KeyValuePair<string, string>> GetKingdomInfoStatsOrdered(Kingdom kingdom)
        {
            var rows = new List<KeyValuePair<string, string>>();
            if (kingdom == null) return rows;
            Action<string, string> add = (label, value) =>
                rows.Add(new KeyValuePair<string, string>(label, value));
            try
            {
                add("Ruler", SafeGet(() => kingdom.Leader?.Name?.ToString()));
                add("Culture", SafeGet(() => kingdom.Culture?.Name?.ToString()));
                add("Clans", SafeGet(() => kingdom.Clans?.Count.ToString(), "0"));

                add("Lords", SafeGet(() =>
                {
                    int lords = 0;
                    foreach (var c in kingdom.Clans)
                        if (c != null)
                            foreach (var h in c.Heroes)
                                if (h != null && h.IsAlive && h.IsLord) lords++;
                    return lords.ToString();
                }, "0"));
                add("Total Troops", SafeGet(() =>
                {
                    int troops = 0;
                    foreach (var c in kingdom.Clans)
                        if (c != null)
                            foreach (var wpc in c.WarPartyComponents)
                                if (wpc?.MobileParty?.MemberRoster != null)
                                    troops += wpc.MobileParty.MemberRoster.TotalManCount;
                    return troops.ToString();
                }, "0"));
                add("Total Garrisons", SafeGet(() =>
                {
                    int garrisons = 0;
                    foreach (var c in kingdom.Clans)
                        if (c != null)
                            foreach (var s in c.Settlements)
                                if (s?.Town?.GarrisonParty != null)
                                    garrisons += s.Town.GarrisonParty.MemberRoster.TotalManCount;
                    return garrisons.ToString();
                }, "0"));
                add("Towns", SafeGet(() =>
                {
                    int towns = 0;
                    foreach (var c in kingdom.Clans)
                        if (c != null)
                            foreach (var s in c.Settlements)
                                if (s != null && s.IsTown) towns++;
                    return towns.ToString();
                }, "0"));
                add("Castles", SafeGet(() =>
                {
                    int castles = 0;
                    foreach (var c in kingdom.Clans)
                        if (c != null)
                            foreach (var s in c.Settlements)
                                if (s != null && s.IsCastle) castles++;
                    return castles.ToString();
                }, "0"));
                add("Villages", SafeGet(() =>
                {
                    int villages = 0;
                    foreach (var c in kingdom.Clans)
                        if (c != null)
                            foreach (var s in c.Settlements)
                                if (s != null && s.IsVillage) villages++;
                    return villages.ToString();
                }, "0"));

                // War rows are collected ONCE into a local, then emitted explicitly and
                // independently. "At War With" used to be a side effect of the SafeGet lambda
                // that produced "Active Wars", which only worked because C# evaluates the
                // argument before invoking add. Worse, if that lambda threw partway the war
                // list was dropped while "Active Wars" fell back to "0" — a value that
                // positively asserts peace, which is more misleading than no row at all.
                // Wording, value format and relative order are unchanged.
                var warNames = new List<string>();
                bool warScanComplete = true;
                try
                {
                    if (Kingdom.All != null)
                        foreach (var k in Kingdom.All)
                            if (k != null && k != kingdom && kingdom.IsAtWarWith(k))
                                warNames.Add(k.Name?.ToString() ?? "?");
                }
                catch (Exception warEx)
                {
                    warScanComplete = false;
                    MCMSettings.DebugLog("EditableEncyclopediaAPI: kingdom war scan failed: " + warEx.ToString());
                }

                // Any name gathered is a war that genuinely exists, so a partial list is still
                // true and worth surfacing. The COUNT is only meaningful if the scan finished —
                // an incomplete count would understate the wars, so that row is omitted instead.
                if (warNames.Count > 0)
                    add("At War With", string.Join(", ", warNames));
                if (warScanComplete)
                    add("Active Wars", warNames.Count.ToString());
            }
            catch (Exception ex) { MCMSettings.DebugLog("EditableEncyclopediaAPI: GetKingdomInfoStatsOrdered failed: " + ex.ToString()); }
            return rows;
        }

        /// <summary>
        /// Returns computed info stats for a Settlement.
        /// Includes: Owner, Governor, Culture, Prosperity, Loyalty, Security,
        /// Food, Militia, Garrison, Wall Level, Workshops, Bound Villages, Notables.
        /// </summary>
        public static Dictionary<string, string> GetSettlementInfoStats(Settlement settlement)
        {
            var stats = new Dictionary<string, string>();
            foreach (var row in GetSettlementInfoStatsOrdered(settlement))
                stats[row.Key] = row.Value;
            return stats;
        }

        /// <summary>
        /// Settlement Info stats in display order. Prefer this over GetSettlementInfoStats when
        /// you intend to render the rows — Dictionary guarantees no ordering.
        /// Not fog-masked: verified against shipped Bannerlord 1.4.7, EncyclopediaSettlementPageVM
        /// exposes no hidden-information property. Vanilla fogs heroes only.
        /// </summary>
        public static List<KeyValuePair<string, string>> GetSettlementInfoStatsOrdered(Settlement settlement)
        {
            var rows = new List<KeyValuePair<string, string>>();
            if (settlement == null) return rows;
            Action<string, string> add = (label, value) =>
                rows.Add(new KeyValuePair<string, string>(label, value));
            try
            {
                add("Owner", SafeGet(() => settlement.OwnerClan?.Leader?.Name?.ToString()));
                add("Clan", SafeGet(() => settlement.OwnerClan?.Name?.ToString()));
                add("Kingdom", SafeGet(() => settlement.OwnerClan?.Kingdom?.Name?.ToString(), "None"));
                add("Culture", SafeGet(() => settlement.Culture?.Name?.ToString()));

                if (settlement.IsTown || settlement.IsCastle)
                {
                    var town = settlement.Town;
                    if (town != null)
                    {
                        add("Prosperity", SafeGet(() => ((int)town.Prosperity).ToString(), "0"));
                        add("Loyalty", SafeGet(() => ((int)town.Loyalty).ToString(), "0"));
                        add("Security", SafeGet(() => ((int)town.Security).ToString(), "0"));
                        add("Food", SafeGet(() => ((int)town.FoodStocks).ToString(), "0"));
                        add("Garrison", SafeGet(() => town.GarrisonParty?.MemberRoster?.TotalManCount.ToString(), "0"));
                        add("Militia", SafeGet(() => ((int)settlement.Militia).ToString(), "0"));
                        add("Wall Level", SafeGet(() => town.GetWallLevel().ToString(), "0"));
                        add("Governor", SafeGet(() => town.Governor?.Name?.ToString(), "None"));
                    }
                }
                else if (settlement.IsVillage && settlement.Village != null)
                {
                    add("Hearths", SafeGet(() => ((int)settlement.Village.Hearth).ToString(), "0"));
                    add("Bound To", SafeGet(() => settlement.Village.Bound?.Name?.ToString()));
                    add("Militia", SafeGet(() => ((int)settlement.Militia).ToString(), "0"));
                }

                add("Bound Villages", SafeGet(() =>
                {
                    int bv = 0;
                    if (settlement.BoundVillages != null)
                        foreach (var v in settlement.BoundVillages)
                            if (v != null) bv++;
                    return bv.ToString();
                }, "0"));

                add("Notables", SafeGet(() =>
                {
                    int notables = 0;
                    if (settlement.Notables != null)
                        foreach (var n in settlement.Notables)
                            if (n != null) notables++;
                    return notables.ToString();
                }, "0"));

                // Workshop productions (towns only)
                if (settlement.IsTown && settlement.Town?.Workshops != null)
                {
                    add("Workshops", SafeGet(() =>
                    {
                        var sb = new System.Text.StringBuilder();
                        int count = 0;
                        foreach (var ws in settlement.Town.Workshops)
                        {
                            if (ws == null || ws.WorkshopType == null) continue;
                            string wsName = null;
                            try { wsName = ws.WorkshopType.Name?.ToString(); } catch { }
                            if (string.IsNullOrEmpty(wsName))
                                try { wsName = ws.WorkshopType.ToString(); } catch { }
                            if (string.IsNullOrEmpty(wsName))
                                try { wsName = ws.WorkshopType.ToString(); } catch { }
                            if (string.IsNullOrEmpty(wsName)) continue;
                            if (count > 0) sb.Append(", ");
                            sb.Append(wsName);
                            count++;
                        }
                        return count > 0 ? count + " (" + sb.ToString() + ")" : "0";
                    }, "0"));
                }

                // Trade Tax (towns and castles both have Town property)
                if ((settlement.IsTown || settlement.IsCastle) && settlement.Town != null)
                {
                    add("Trade Tax", SafeGet(() =>
                    {
                        try
                        {
                            int tax = (int)settlement.Town.TradeTaxAccumulated;
                            return tax.ToString("N0") + " denars";
                        }
                        catch { return "Unknown"; }
                    }, "Unknown"));
                }

                // Village production type
                if (settlement.IsVillage && settlement.Village != null)
                {
                    add("Produces", SafeGet(() =>
                    {
                        var vt = settlement.Village.VillageType;
                        if (vt == null) return "Unknown";

                        // Try PrimaryProduction first
                        try
                        {
                            var primaryProd = vt.PrimaryProduction;
                            if (primaryProd != null)
                            {
                                string prodName = primaryProd.Name?.ToString();
                                if (!string.IsNullOrEmpty(prodName)) return prodName;
                            }
                        }
                        catch { }

                        // Try Productions collection
                        try
                        {
                            var prods = vt.Productions;
                            if (prods != null)
                            {
                                var sb = new System.Text.StringBuilder();
                                int count = 0;
                                foreach (var prod in prods)
                                {
                                    if (prod.Item1 == null) continue;
                                    string itemName = prod.Item1.Name?.ToString();
                                    if (string.IsNullOrEmpty(itemName)) continue;
                                    if (count > 0) sb.Append(", ");
                                    sb.Append(itemName);
                                    count++;
                                }
                                if (count > 0) return sb.ToString();
                            }
                        }
                        catch { }

                        // Fallback: VillageType string
                        try { return vt.ShortName?.ToString(); } catch { }
                        return vt.ToString() ?? "Unknown";
                    }, "Unknown"));
                }

                // Bound village productions (for towns/castles)
                if ((settlement.IsTown || settlement.IsCastle) && settlement.BoundVillages != null)
                {
                    add("Village Produces", SafeGet(() =>
                    {
                        var allProds = new HashSet<string>();
                        foreach (var bv in settlement.BoundVillages)
                        {
                            if (bv?.Settlement?.Village?.VillageType == null) continue;
                            try
                            {
                                var prods = bv.Settlement.Village.VillageType.Productions;
                                if (prods != null)
                                {
                                    foreach (var prod in prods)
                                    {
                                        if (prod.Item1?.Name != null)
                                            allProds.Add(prod.Item1.Name.ToString());
                                    }
                                }
                            }
                            catch
                            {
                                string vtName = bv.Settlement.Village.VillageType.ShortName?.ToString() ?? bv.Settlement.Village.VillageType.ToString();
                                if (!string.IsNullOrEmpty(vtName)) allProds.Add(vtName);
                            }
                        }
                        return allProds.Count > 0 ? string.Join(", ", allProds) : "None";
                    }, "None"));
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("EditableEncyclopediaAPI: GetSettlementInfoStatsOrdered failed: " + ex.ToString()); }
            return rows;
        }

        /// <summary>
        /// Resolves a Settlement by StringId. Single resolution path shared by every
        /// by-id settlement overload on this API. Returns null when nothing matches.
        /// </summary>
        private static Settlement ResolveSettlement(string settlementId)
        {
            if (string.IsNullOrEmpty(settlementId)) return null;
            try
            {
                if (Settlement.All != null)
                    foreach (var s in Settlement.All)
                        if (s != null && s.StringId == settlementId)
                            return s;
            }
            catch (Exception ex) { MCMSettings.DebugLog("EditableEncyclopediaAPI: ResolveSettlement failed: " + ex.ToString()); }
            return null;
        }

        /// <summary>
        /// Returns computed info stats for a Settlement by StringId.
        /// </summary>
        public static Dictionary<string, string> GetSettlementInfoStats(string settlementId)
        {
            var settlement = ResolveSettlement(settlementId);
            if (settlement == null) return new Dictionary<string, string>();
            return GetSettlementInfoStats(settlement);
        }

        /// <summary>
        /// Settlement Info stats in display order for a settlement StringId.
        /// Prefer this over GetSettlementInfoStats(string) when you intend to render the rows.
        /// </summary>
        public static List<KeyValuePair<string, string>> GetSettlementInfoStatsOrdered(string settlementId)
        {
            var settlement = ResolveSettlement(settlementId);
            if (settlement == null) return new List<KeyValuePair<string, string>>();
            return GetSettlementInfoStatsOrdered(settlement);
        }

        // ── Chronicle & Chronicle Notes ─────────────────────────

        /// <summary>
        /// Returns the hero's Chronicle field text (the "chronicle" lore field).
        /// This is the auto-dated running journal stored as a hero info field.
        /// Returns null if no chronicle exists.
        /// </summary>
        public static string GetHeroChronicle(string heroId)
        {
            if (!IsAvailable || string.IsNullOrEmpty(heroId)) return null;
            return EncyclopediaEditBehavior.Instance.GetHeroInfoField("chronicle", heroId);
        }

        /// <summary>
        /// Returns all chronicle/journal entries across ALL entities (heroes, clans, kingdoms, settlements)
        /// as a flat list sorted by date (newest first). Each entry includes the source entity ID and type.
        /// This is the same data shown in the Global Chronicle Panel.
        /// </summary>
        public static List<ChronicleEntry> GetAllChronicleEntries()
        {
            var result = new List<ChronicleEntry>();
            if (!IsAvailable) return result;
            try
            {
                var allJournal = EncyclopediaEditBehavior.Instance.GetAllJournalForExport();
                if (allJournal == null) return result;
                foreach (var kvp in allJournal)
                {
                    if (string.IsNullOrEmpty(kvp.Value)) continue;
                    string[] entries = kvp.Value.Split('\n');
                    foreach (var entry in entries)
                    {
                        if (string.IsNullOrEmpty(entry)) continue;
                        int pipeIdx = entry.IndexOf('|');
                        if (pipeIdx > 0)
                        {
                            result.Add(new ChronicleEntry
                            {
                                EntityId = kvp.Key,
                                Date = entry.Substring(0, pipeIdx),
                                Text = entry.Substring(pipeIdx + 1)
                            });
                        }
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("EditableEncyclopediaAPI: GetAllChronicleEntries failed: " + ex.ToString()); }
            return result;
        }

        /// <summary>
        /// Permanently clears ALL chronicle/journal entries across every entity.
        /// Backed by EncyclopediaEditBehavior.ClearAllJournal(). No-op when the
        /// behavior is unavailable (e.g. no active campaign).
        /// </summary>
        public static void ClearAllChronicleEntries()
        {
            if (!IsAvailable) return;
            try
            {
                EncyclopediaEditBehavior.Instance.ClearAllJournal();
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("EditableEncyclopediaAPI: ClearAllChronicleEntries failed: " + ex.ToString());
            }
        }

        // ── Chronicle spoils (gold + looted items) ────────────────────────

        /// <summary>
        /// Builds the key a spoils record is stored under, from a chronicle entry's own fields.
        ///
        /// This is deliberately the same formula as
        /// EE-ChronicleNoters\ChronicleEntryPopulator.MakeStableId(entityId, date, text):
        ///     entityId + "|" + date + "|" + the first 24 characters of the RAW entry text.
        /// Pass the values straight off a <see cref="ChronicleEntry"/> from
        /// GetAllChronicleEntries() — do NOT pass text that has been marker-stripped or had its
        /// "[Category] " prefix removed, or the key will not match.
        /// </summary>
        public static string MakeChronicleEntryKey(string entityId, string date, string text)
        {
            try { return EncyclopediaEditBehavior.MakeChronicleKey(entityId, date, text); }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("EditableEncyclopediaAPI: MakeChronicleEntryKey failed: " + ex.ToString());
                return null;
            }
        }

        /// <summary>
        /// True if battle/raid/conquest spoils were recorded for this chronicle entry key.
        /// Key comes from <see cref="MakeChronicleEntryKey"/> (or, equivalently, from
        /// ChronicleEntryPopulator.MakeStableId). False when EE is unavailable.
        /// </summary>
        public static bool HasChronicleSpoils(string entryKey)
        {
            if (!IsAvailable || string.IsNullOrEmpty(entryKey)) return false;
            try { return EncyclopediaEditBehavior.Instance.HasChronicleSpoils(entryKey); }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("EditableEncyclopediaAPI: HasChronicleSpoils failed: " + ex.ToString());
                return false;
            }
        }

        /// <summary>
        /// Gold gained and items looted for a chronicle entry.
        ///
        /// Never returns null — an unknown key, an unavailable campaign or a parse failure all
        /// yield an empty <see cref="ChronicleSpoils"/> (Gold 0, empty Items). Gold may be
        /// negative: that is the gold the entry's hero LOST in the engagement.
        /// Items are the most valuable stacks first; when
        /// <see cref="ChronicleSpoils.Truncated"/> is true, <c>OmittedItemStacks</c> more stacks
        /// existed than were stored.
        /// </summary>
        public static ChronicleSpoils GetChronicleSpoils(string entryKey)
        {
            if (!IsAvailable || string.IsNullOrEmpty(entryKey)) return new ChronicleSpoils();
            try { return EncyclopediaEditBehavior.Instance.GetChronicleSpoils(entryKey) ?? new ChronicleSpoils(); }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("EditableEncyclopediaAPI: GetChronicleSpoils failed: " + ex.ToString());
                return new ChronicleSpoils();
            }
        }

        /// <summary>
        /// Convenience overload that builds the key itself from a chronicle entry's fields, so a
        /// consumer never has to reimplement the key formula. Pass ChronicleEntry.EntityId,
        /// ChronicleEntry.Date and the RAW ChronicleEntry.Text. Never returns null.
        /// </summary>
        public static ChronicleSpoils GetChronicleSpoils(string entityId, string date, string text)
        {
            return GetChronicleSpoils(MakeChronicleEntryKey(entityId, date, text));
        }

        /// <summary>
        /// Simple data class for chronicle entries returned by GetAllChronicleEntries().
        /// </summary>
        public class ChronicleEntry
        {
            public string EntityId;
            public string Date;
            public string Text;
        }
    }
}