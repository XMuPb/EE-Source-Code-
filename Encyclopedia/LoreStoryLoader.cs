using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Loads pre-written lore entries from .txt files in the LoreStory/ folder
    /// inside the module directory. Each hero gets a unique entry (no duplicates)
    /// selected deterministically by hashing the hero's StringId.
    ///
    /// File format: entries separated by "---" on its own line.
    /// Supports placeholders: {name}, {settlement}, {culture}, {clan}, {occupation}, {date}
    ///
    /// Supports occupation-specific files: Backstory_Lord.txt, Backstory_Merchant.txt, etc.
    /// Falls back to the general file (Backstory.txt) if no occupation-specific file exists.
    ///
    /// Users can add, edit, or replace entries by modifying the .txt files.
    /// </summary>
    internal static class LoreStoryLoader
    {
        private const string FolderName = "LoreStory";
        private const string EntrySeparator = "---";
        private static readonly System.Random _rng = new System.Random();

        // Field name → list of entries (general pool)
        private static Dictionary<string, List<string>> _generalEntries;

        // "fieldKey_OccupationName" → list of entries (occupation-specific pool)
        private static Dictionary<string, List<string>> _occupationEntries;

        // Track which entries have been assigned to prevent duplicates
        // Key = fieldKey (or fieldKey_Occupation), Value = set of assigned indices
        private static Dictionary<string, HashSet<int>> _assignedIndices;

        private static bool _loaded;
        private static string _moduleFolder;

        // Map field keys to file names
        private static readonly Dictionary<string, string> FieldToFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "backstory", "Backstory" },
            { "personality", "Personality" },
            { "goals", "Goals" },
            { "relationships", "Relationships" },
            { "rumors", "RumorsAndSecrets" },
            { "founding", "Founding" },
            { "territory", "Territory" },
            { "traditions", "Traditions" },
            { "rivals", "Rivals" },
            { "history", "History" },
            { "laws", "Laws" },
            { "culture_lore", "CultureLore" },
            { "military", "Military" },
            { "economy", "Economy" },
            { "landmarks", "Landmarks" },
            { "legends", "Legends" }
        };

        /// <summary>
        /// Loads all lore story files from the module's LoreStory/ folder.
        /// Called once on first access. Safe to call multiple times.
        /// </summary>
        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            _generalEntries = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _occupationEntries = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _assignedIndices = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

            _moduleFolder = FindModuleFolder();
            if (string.IsNullOrEmpty(_moduleFolder))
            {
                MCMSettings.DebugLog("LoreStoryLoader: module folder not found");
                return;
            }

            string loreFolder = Path.Combine(_moduleFolder, FolderName);
            if (!Directory.Exists(loreFolder))
            {
                MCMSettings.DebugLog("LoreStoryLoader: LoreStory folder not found at " + loreFolder);
                return;
            }

            // Load from subfolder structure: LoreStory/Backstory/General.txt, LoreStory/Backstory/Lord.txt, etc.
            // Also supports legacy flat structure: LoreStory/Backstory.txt, LoreStory/Backstory_Lord.txt
            foreach (var kvp in FieldToFileName)
            {
                string subFolder = Path.Combine(loreFolder, kvp.Value);

                // New subfolder structure: LoreStory/Backstory/General.txt
                if (Directory.Exists(subFolder))
                {
                    string generalFile = Path.Combine(subFolder, "General.txt");
                    if (File.Exists(generalFile))
                    {
                        var entries = LoadEntriesFromFile(generalFile);
                        _generalEntries[kvp.Key] = entries;
                        MCMSettings.DebugLog("LoreStoryLoader: loaded " + entries.Count + " entries from " + kvp.Value + "/General.txt");
                    }

                    // Occupation-specific: LoreStory/Backstory/Lord.txt, Merchant.txt, etc.
                    foreach (string file in Directory.GetFiles(subFolder, "*.txt"))
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file);
                        if (string.Equals(fileName, "General", StringComparison.OrdinalIgnoreCase)) continue;
                        string key = kvp.Key + "_" + fileName;

                        var occEntries = LoadEntriesFromFile(file);
                        _occupationEntries[key] = occEntries;
                        MCMSettings.DebugLog("LoreStoryLoader: loaded " + occEntries.Count + " entries from " + kvp.Value + "/" + fileName + ".txt");
                    }
                }

                // Legacy flat structure fallback: LoreStory/Backstory.txt
                if (!_generalEntries.ContainsKey(kvp.Key))
                {
                    string filePath = Path.Combine(loreFolder, kvp.Value + ".txt");
                    if (File.Exists(filePath))
                    {
                        var entries = LoadEntriesFromFile(filePath);
                        _generalEntries[kvp.Key] = entries;
                        MCMSettings.DebugLog("LoreStoryLoader: loaded " + entries.Count + " entries from " + kvp.Value + ".txt (legacy)");
                    }
                }
            }

            // Legacy flat occupation files: LoreStory/Backstory_Lord.txt
            foreach (var kvp in FieldToFileName)
            {
                foreach (string file in Directory.GetFiles(loreFolder, kvp.Value + "_*.txt"))
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    int underscoreIdx = fileName.IndexOf('_');
                    if (underscoreIdx < 0 || underscoreIdx + 1 >= fileName.Length) continue;
                    string occName = fileName.Substring(underscoreIdx + 1);
                    string key = kvp.Key + "_" + occName;
                    if (_occupationEntries.ContainsKey(key)) continue; // subfolder version takes priority

                    var entries = LoadEntriesFromFile(file);
                    _occupationEntries[key] = entries;
                    MCMSettings.DebugLog("LoreStoryLoader: loaded " + entries.Count + " entries from " + fileName + ".txt");
                }
            }

            int totalGeneral = 0;
            foreach (var v in _generalEntries.Values) totalGeneral += v.Count;
            int totalOcc = 0;
            foreach (var v in _occupationEntries.Values) totalOcc += v.Count;
            MCMSettings.DebugLog("LoreStoryLoader: total " + totalGeneral + " general + " + totalOcc + " occupation-specific entries loaded");
        }

        /// <summary>
        /// Gets a unique lore entry for the given hero and field.
        /// Returns null if no entries are available.
        /// Each hero gets a different entry — no duplicates across heroes.
        /// </summary>
        public static string GetLoreEntry(string fieldKey, string heroId)
        {
            EnsureLoaded();

            if (string.IsNullOrEmpty(fieldKey) || string.IsNullOrEmpty(heroId))
                return null;

            // Determine occupation for occupation-specific lookup
            string occupationName = GetHeroOccupationName(heroId);

            // Try occupation-specific pool first
            string occPoolKey = !string.IsNullOrEmpty(occupationName) ? fieldKey + "_" + occupationName : null;
            string entry = null;

            if (occPoolKey != null && _occupationEntries.TryGetValue(occPoolKey, out var occEntries) && occEntries.Count > 0)
            {
                entry = PickUniqueEntry(occPoolKey, occEntries, heroId);
            }

            // Fall back to general pool
            if (entry == null && _generalEntries.TryGetValue(fieldKey, out var generalEntries) && generalEntries.Count > 0)
            {
                entry = PickUniqueEntry(fieldKey, generalEntries, heroId);
            }

            if (entry == null) return null;

            // Resolve placeholders
            return ResolvePlaceholders(entry, heroId);
        }

        /// <summary>
        /// Picks a unique entry from the pool for this hero, ensuring no two heroes
        /// in the same session get the same entry.
        /// </summary>
        private static string PickUniqueEntry(string poolKey, List<string> pool, string heroId)
        {
            if (pool.Count == 0) return null;

            if (!_assignedIndices.TryGetValue(poolKey, out var assigned))
            {
                assigned = new HashSet<int>();
                _assignedIndices[poolKey] = assigned;
            }

            // Generate a deterministic hash from heroId
            int hash = GetStableHash(heroId);
            int poolSize = pool.Count;

            // Try to find an unassigned index starting from the hero's preferred index
            int startIdx = ((hash % poolSize) + poolSize) % poolSize;
            for (int attempt = 0; attempt < poolSize; attempt++)
            {
                int idx = (startIdx + attempt) % poolSize;
                if (!assigned.Contains(idx))
                {
                    assigned.Add(idx);
                    return pool[idx];
                }
            }

            // All entries assigned — allow reuse (more heroes than entries)
            // Use the hero's preferred index
            return pool[startIdx];
        }

        /// <summary>
        /// Generates a stable hash code from a string that's consistent across sessions.
        /// (String.GetHashCode is NOT stable across .NET versions/runs)
        /// </summary>
        internal static int GetStableHash(string text)
        {
            unchecked
            {
                int hash = 5381;
                for (int i = 0; i < text.Length; i++)
                    hash = ((hash << 5) + hash) + text[i];
                return hash & 0x7FFFFFFF;
            }
        }

        private static string GetHeroOccupationName(string heroId)
        {
            try
            {
                // Check custom occupation first
                var behavior = EncyclopediaEditBehavior.Instance;
                if (behavior != null)
                {
                    int customOcc = behavior.GetCustomOccupation(heroId);
                    if (customOcc >= 0)
                        return ((Occupation)customOcc).ToString();
                }

                // Fall back to native
                Hero hero = Hero.FindFirst(h => h.StringId == heroId);
                if (hero != null)
                    return hero.Occupation.ToString();
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("LoreStoryLoader: GetHeroOccupationName failed: " + ex.ToString());
            }
            return null;
        }

        internal static string ResolvePlaceholders(string text, string heroId)
        {
            try
            {
                Hero hero = Hero.FindFirst(h => h.StringId == heroId);
                if (hero == null)
                {
                    // Not a hero — try settlement, kingdom, clan
                    return ResolveNonHeroPlaceholders(text, heroId);
                }

                // Basic placeholders
                text = text.Replace("{name}", hero.Name?.ToString() ?? "Unknown");

                // Culture — check custom culture first
                string cultureName = hero.Culture?.Name?.ToString() ?? "Unknown";
                try
                {
                    var behavior = EncyclopediaEditBehavior.Instance;
                    if (behavior != null)
                    {
                        string customCulture = behavior.GetCustomCulture(heroId);
                        if (!string.IsNullOrEmpty(customCulture))
                        {
                            int pipe = customCulture.IndexOf('|');
                            if (pipe > 0 && pipe + 1 < customCulture.Length)
                                cultureName = customCulture.Substring(pipe + 1);
                            else
                                cultureName = customCulture;
                        }
                    }
                }
                catch { }
                text = text.Replace("{culture}", cultureName);

                text = text.Replace("{clan}", hero.Clan?.Name?.ToString() ?? "None");
                text = text.Replace("{settlement}", hero.HomeSettlement?.Name?.ToString() ?? "Unknown");

                // Occupation — check custom occupation first
                string occDisplay = hero.Occupation.ToString();
                try
                {
                    var behavior = EncyclopediaEditBehavior.Instance;
                    if (behavior != null)
                    {
                        int customOcc = behavior.GetCustomOccupation(heroId);
                        if (customOcc >= 0)
                            occDisplay = ((Occupation)customOcc).ToString();
                    }
                }
                catch { }
                text = text.Replace("{occupation}", occDisplay);

                // Kingdom
                text = text.Replace("{kingdom}", hero.Clan?.Kingdom?.Name?.ToString() ?? "No Kingdom");
                text = text.Replace("{faction}", hero.Clan?.Kingdom?.Name?.ToString() ?? hero.Clan?.Name?.ToString() ?? "None");

                // Gender
                text = text.Replace("{gender}", hero.IsFemale ? "female" : "male");
                text = text.Replace("{he}", hero.IsFemale ? "she" : "he");
                text = text.Replace("{He}", hero.IsFemale ? "She" : "He");
                text = text.Replace("{him}", hero.IsFemale ? "her" : "him");
                text = text.Replace("{his}", hero.IsFemale ? "her" : "his");
                text = text.Replace("{His}", hero.IsFemale ? "Her" : "His");
                text = text.Replace("{man}", hero.IsFemale ? "woman" : "man");
                text = text.Replace("{lord}", hero.IsFemale ? "lady" : "lord");
                text = text.Replace("{Lord}", hero.IsFemale ? "Lady" : "Lord");
                text = text.Replace("{son}", hero.IsFemale ? "daughter" : "son");
                text = text.Replace("{brother}", hero.IsFemale ? "sister" : "brother");

                // Family
                text = text.Replace("{spouse}", hero.Spouse?.Name?.ToString() ?? "no one");
                text = text.Replace("{father}", hero.Father?.Name?.ToString() ?? "unknown father");
                text = text.Replace("{mother}", hero.Mother?.Name?.ToString() ?? "unknown mother");

                // Age
                try { text = text.Replace("{age}", ((int)hero.Age).ToString()); }
                catch { text = text.Replace("{age}", "unknown"); }

                // Title (custom or default)
                try
                {
                    var behavior = EncyclopediaEditBehavior.Instance;
                    string title = behavior?.GetCustomTitle(heroId);
                    if (string.IsNullOrEmpty(title))
                        title = hero.IsFemale ? "Lady" : "Lord";
                    text = text.Replace("{title}", title);
                }
                catch { text = text.Replace("{title}", ""); }

                // Date
                try
                {
                    string date = CampaignTime.Now.ToString();
                    text = text.Replace("{date}", date);
                }
                catch { text = text.Replace("{date}", "Unknown Date"); }

                // ── Extended placeholders ──

                // Hometown (same as settlement but clearer name)
                text = text.Replace("{hometown}", hero.HomeSettlement?.Name?.ToString() ?? "an unknown place");

                // Ruler of the kingdom
                text = text.Replace("{ruler}", hero.Clan?.Kingdom?.Leader?.Name?.ToString() ?? "no ruler");

                // Wealth
                try { text = text.Replace("{wealth}", hero.Gold.ToString("N0")); }
                catch { text = text.Replace("{wealth}", "0"); }

                // Troops in party
                try { text = text.Replace("{troops}", (hero.PartyBelongedTo?.MemberRoster?.TotalManCount ?? 0).ToString()); }
                catch { text = text.Replace("{troops}", "0"); }

                // Clan renown
                try { text = text.Replace("{renown}", ((int)(hero.Clan?.Renown ?? 0)).ToString()); }
                catch { text = text.Replace("{renown}", "0"); }

                // Influence
                try { text = text.Replace("{influence}", ((int)(hero.Clan?.Influence ?? 0)).ToString()); }
                catch { text = text.Replace("{influence}", "0"); }

                // Children count
                try { text = text.Replace("{children_count}", (hero.Children?.Count ?? 0).ToString()); }
                catch { text = text.Replace("{children_count}", "0"); }

                // Clan tier
                try { text = text.Replace("{tier}", (hero.Clan?.Tier ?? 0).ToString()); }
                catch { text = text.Replace("{tier}", "0"); }

                // Random companion from party/clan
                try
                {
                    var companions = hero.Clan?.Companions;
                    if (companions != null && companions.Count > 0)
                        text = text.Replace("{companion}", companions[_rng.Next(companions.Count)].Name?.ToString() ?? "a companion");
                    else
                        text = text.Replace("{companion}", "no companion");
                }
                catch { text = text.Replace("{companion}", "no companion"); }

                // Random enemy hero (at war)
                try
                {
                    string enemyName = "no enemy";
                    if (hero.Clan?.Kingdom != null)
                    {
                        var enemies = new List<Hero>();
                        foreach (var k in Kingdom.All)
                        {
                            if (k != hero.Clan.Kingdom && hero.Clan.Kingdom.IsAtWarWith(k) && k.Leader != null)
                                enemies.Add(k.Leader);
                        }
                        if (enemies.Count > 0) enemyName = enemies[_rng.Next(enemies.Count)].Name?.ToString() ?? "an enemy";
                    }
                    text = text.Replace("{enemy}", enemyName);
                }
                catch { text = text.Replace("{enemy}", "no enemy"); }

                // Random friendly lord
                try
                {
                    string friendName = "no ally";
                    if (hero.Clan?.Kingdom != null)
                    {
                        var allies = new List<Hero>();
                        foreach (var c in hero.Clan.Kingdom.Clans)
                        {
                            if (c != hero.Clan && c.Leader != null)
                                allies.Add(c.Leader);
                        }
                        if (allies.Count > 0) friendName = allies[_rng.Next(allies.Count)].Name?.ToString() ?? "an ally";
                    }
                    text = text.Replace("{friend}", friendName);
                }
                catch { text = text.Replace("{friend}", "no ally"); }

                // Battle count from journal
                try
                {
                    int battles = 0;
                    var behavior = EncyclopediaEditBehavior.Instance;
                    if (behavior != null)
                    {
                        var entries = behavior.GetJournalEntries(heroId);
                        foreach (var e in entries)
                            if ((e.Text ?? "").Contains("Defeated") || (e.Text ?? "").Contains("Victory"))
                                battles++;
                    }
                    text = text.Replace("{battle_count}", battles.ToString());
                }
                catch { text = text.Replace("{battle_count}", "0"); }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("LoreStoryLoader: ResolvePlaceholders failed: " + ex.ToString());
            }
            return text;
        }

        private static List<string> LoadEntriesFromFile(string filePath)
        {
            var entries = new List<string>();
            try
            {
                string content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                string[] blocks = content.Split(new[] { "\n" + EntrySeparator + "\n", "\r\n" + EntrySeparator + "\r\n" },
                    StringSplitOptions.RemoveEmptyEntries);

                foreach (string block in blocks)
                {
                    string trimmed = block.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        entries.Add(trimmed);
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("LoreStoryLoader: failed to read " + filePath + ": " + ex.ToString());
            }
            return entries;
        }

        internal static string FindModuleFolder()
        {
            try
            {
                // Try to find the module folder via the assembly location
                string assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(assemblyPath))
                {
                    // Assembly is at: Modules/EditableEncyclopedia/bin/Win64_Shipping_Client/EditableEncyclopedia.dll
                    string dir = Path.GetDirectoryName(assemblyPath);
                    // Go up 2 levels: bin/Win64_Shipping_Client → bin → EditableEncyclopedia
                    for (int i = 0; i < 2; i++)
                    {
                        if (dir != null) dir = Path.GetDirectoryName(dir);
                    }
                    if (dir != null && Directory.Exists(Path.Combine(dir, "bin")))
                    {
                        MCMSettings.DebugLog("LoreStoryLoader: module folder = " + dir);
                        return dir;
                    }
                }

                // Fallback: search common paths
                string basePath = AppDomain.CurrentDomain.BaseDirectory;
                string modulesPath = Path.Combine(basePath, "..", "Modules", "EditableEncyclopedia");
                if (Directory.Exists(modulesPath))
                    return Path.GetFullPath(modulesPath);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("LoreStoryLoader: FindModuleFolder failed: " + ex.ToString());
            }
            return null;
        }

        /// <summary>
        /// Clears loaded data. Call on campaign change.
        /// </summary>
        public static void ClearCache()
        {
            _loaded = false;
            _generalEntries = null;
            _occupationEntries = null;
            _assignedIndices = null;
        }
        private static string ResolveNonHeroPlaceholders(string text, string entityId)
        {
            try
            {
                // Try settlement
                foreach (var settlement in Settlement.All)
                {
                    if (settlement.StringId == entityId)
                    {
                        text = text.Replace("{name}", settlement.Name?.ToString() ?? entityId);
                        text = text.Replace("{settlement}", settlement.Name?.ToString() ?? entityId);
                        text = text.Replace("{culture}", settlement.Culture?.Name?.ToString() ?? "Unknown");
                        text = text.Replace("{clan}", settlement.OwnerClan?.Name?.ToString() ?? "None");
                        text = text.Replace("{kingdom}", settlement.OwnerClan?.Kingdom?.Name?.ToString() ?? "No Kingdom");
                        text = text.Replace("{faction}", settlement.OwnerClan?.Kingdom?.Name?.ToString() ?? settlement.OwnerClan?.Name?.ToString() ?? "None");
                        text = text.Replace("{owner}", settlement.OwnerClan?.Leader?.Name?.ToString() ?? "Unknown");
                        text = text.Replace("{ruler}", settlement.OwnerClan?.Kingdom?.Leader?.Name?.ToString() ?? "no ruler");
                        try { text = text.Replace("{prosperity}", ((int)(settlement.Town?.Prosperity ?? 0)).ToString("N0")); }
                        catch { text = text.Replace("{prosperity}", "0"); }
                        try { text = text.Replace("{garrison}", (settlement.Town?.GarrisonParty?.MemberRoster?.TotalManCount ?? 0).ToString()); }
                        catch { text = text.Replace("{garrison}", "0"); }
                        text = text.Replace("{bound_town}", settlement.BoundVillages != null && settlement.BoundVillages.Count > 0
                            ? settlement.Village?.Bound?.Name?.ToString() ?? settlement.Name?.ToString() ?? ""
                            : settlement.IsTown || settlement.IsCastle ? settlement.Name?.ToString() ?? "" : "None");
                        try { text = text.Replace("{militia}", ((int)(settlement.Town?.Militia ?? settlement.Village?.Militia ?? 0)).ToString()); }
                        catch { text = text.Replace("{militia}", "0"); }
                        try { text = text.Replace("{date}", CampaignTime.Now.ToString()); }
                        catch { text = text.Replace("{date}", "Unknown Date"); }
                        return text;
                    }
                }

                // Try kingdom
                foreach (var kingdom in Kingdom.All)
                {
                    if (kingdom.StringId == entityId)
                    {
                        text = text.Replace("{name}", kingdom.Name?.ToString() ?? entityId);
                        text = text.Replace("{kingdom}", kingdom.Name?.ToString() ?? entityId);
                        text = text.Replace("{culture}", kingdom.Culture?.Name?.ToString() ?? "Unknown");
                        text = text.Replace("{clan}", kingdom.RulingClan?.Name?.ToString() ?? "None");
                        text = text.Replace("{settlement}", kingdom.RulingClan?.HomeSettlement?.Name?.ToString() ?? "the capital");
                        text = text.Replace("{faction}", kingdom.Name?.ToString() ?? "None");
                        text = text.Replace("{ruler}", kingdom.Leader?.Name?.ToString() ?? "no ruler");
                        try { text = text.Replace("{clans_count}", kingdom.Clans?.Count.ToString() ?? "0"); }
                        catch { text = text.Replace("{clans_count}", "0"); }
                        try
                        {
                            int towns = 0, castles = 0, villages = 0;
                            foreach (var s in Settlement.All)
                            {
                                if (s.OwnerClan?.Kingdom == kingdom)
                                {
                                    if (s.IsTown) towns++;
                                    else if (s.IsCastle) castles++;
                                    else if (s.IsVillage) villages++;
                                }
                            }
                            text = text.Replace("{towns_count}", towns.ToString());
                            text = text.Replace("{castles_count}", castles.ToString());
                            text = text.Replace("{villages_count}", villages.ToString());
                        }
                        catch
                        {
                            text = text.Replace("{towns_count}", "0");
                            text = text.Replace("{castles_count}", "0");
                            text = text.Replace("{villages_count}", "0");
                        }
                        try
                        {
                            int str = 0;
                            foreach (var c in kingdom.Clans)
                                if (c?.Heroes != null)
                                    foreach (var h in c.Heroes)
                                        str += h?.PartyBelongedTo?.MemberRoster?.TotalManCount ?? 0;
                            text = text.Replace("{strength}", str.ToString("N0"));
                        }
                        catch { text = text.Replace("{strength}", "0"); }
                        try { text = text.Replace("{date}", CampaignTime.Now.ToString()); }
                        catch { text = text.Replace("{date}", "Unknown Date"); }
                        return text;
                    }
                }

                // Try clan
                foreach (var clan in Clan.All)
                {
                    if (clan.StringId == entityId)
                    {
                        text = text.Replace("{name}", clan.Name?.ToString() ?? entityId);
                        text = text.Replace("{clan}", clan.Name?.ToString() ?? entityId);
                        text = text.Replace("{culture}", clan.Culture?.Name?.ToString() ?? "Unknown");
                        text = text.Replace("{kingdom}", clan.Kingdom?.Name?.ToString() ?? "No Kingdom");
                        text = text.Replace("{settlement}", clan.HomeSettlement?.Name?.ToString() ?? "their lands");
                        text = text.Replace("{faction}", clan.Kingdom?.Name?.ToString() ?? clan.Name?.ToString() ?? "None");
                        text = text.Replace("{ruler}", clan.Kingdom?.Leader?.Name?.ToString() ?? "no ruler");
                        text = text.Replace("{leader}", clan.Leader?.Name?.ToString() ?? "Unknown");
                        try { text = text.Replace("{tier}", clan.Tier.ToString()); }
                        catch { text = text.Replace("{tier}", "0"); }
                        try { text = text.Replace("{renown}", ((int)clan.Renown).ToString()); }
                        catch { text = text.Replace("{renown}", "0"); }
                        try { text = text.Replace("{influence}", ((int)clan.Influence).ToString()); }
                        catch { text = text.Replace("{influence}", "0"); }
                        try { text = text.Replace("{wealth}", clan.Gold.ToString("N0")); }
                        catch { text = text.Replace("{wealth}", "0"); }
                        try
                        {
                            int lords = 0;
                            foreach (var h in clan.Heroes) if (h != null && h.IsLord) lords++;
                            text = text.Replace("{lords_count}", lords.ToString());
                        }
                        catch { text = text.Replace("{lords_count}", "0"); }
                        try
                        {
                            int str = 0;
                            if (clan.Heroes != null)
                                foreach (var h in clan.Heroes)
                                    str += h?.PartyBelongedTo?.MemberRoster?.TotalManCount ?? 0;
                            text = text.Replace("{strength}", str.ToString("N0"));
                        }
                        catch { text = text.Replace("{strength}", "0"); }
                        try { text = text.Replace("{date}", CampaignTime.Now.ToString()); }
                        catch { text = text.Replace("{date}", "Unknown Date"); }
                        return text;
                    }
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("LoreStoryLoader: ResolveNonHeroPlaceholders failed: " + ex.ToString());
            }
            return text;
        }
    }
}
