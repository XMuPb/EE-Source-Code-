using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Handles exporting and importing descriptions to/from a shared JSON file on disk.
    /// This allows other mods to read descriptions without needing a direct DLL reference.
    /// 
    /// File location:
    ///   Documents\Mount and Blade II Bannerlord\Configs\ModSettings\Global\EditableEncyclopedia\descriptions_export.json
    /// </summary>
    public static class SharedFileExporter
    {
        private static readonly string ExportFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord",
            "Configs", "ModSettings", "Global", "EditableEncyclopedia");

        private static readonly string ExportFileName = "descriptions_export.json";

        /// <summary>
        /// Returns the full path to the shared export file.
        /// </summary>
        public static string GetExportFilePath()
            => Path.Combine(ExportFolder, ExportFileName);

        /// <summary>
        /// Writes all current descriptions to the shared JSON file.
        /// Creates the directory if it doesn't exist.
        /// Returns true on success, false on failure.
        /// </summary>
        public static bool Export(Dictionary<string, string> descriptions)
        {
            return Export(descriptions, null, null, null);
        }

        /// <summary>
        /// Writes descriptions, names, titles, and banners to the shared JSON file.
        /// Any section can be null to skip it.
        /// Returns true on success, false on failure.
        /// </summary>
        public static bool Export(Dictionary<string, string> descriptions,
            Dictionary<string, string> names,
            Dictionary<string, string> titles,
            Dictionary<string, string> banners)
        {
            return Export(descriptions, names, titles, banners, null, null);
        }

        /// <summary>
        /// Writes all data sections to the shared JSON file (v4 format).
        /// Includes cultures, culture definitions, and occupations alongside descriptions, names, titles, and banners.
        /// Any section can be null to skip it.
        /// Returns true on success, false on failure.
        /// </summary>
        public static bool Export(Dictionary<string, string> descriptions,
            Dictionary<string, string> names,
            Dictionary<string, string> titles,
            Dictionary<string, string> banners,
            Dictionary<string, string> cultures,
            Dictionary<string, string> occupations,
            Dictionary<string, string> cultureDefs = null,
            Dictionary<string, string> heroInfoFields = null,
            Dictionary<string, string> tags = null,
            Dictionary<string, string> timestamps = null,
            Dictionary<string, string> journal = null,
            Dictionary<string, string> relationNotes = null,
            Dictionary<string, string> tagNotes = null,
            Dictionary<string, string> relationHistory = null,
            Dictionary<string, string> tagCategories = null,
            Dictionary<string, string> tagPresets = null,
            Dictionary<string, string> perHeroAutoTagThresholds = null,
            Dictionary<string, string> relationNoteTags = null,
            Dictionary<string, string> relationNoteTagLocks = null,
            Dictionary<string, string> itemLore = null)
        {
            try
            {
                if (!Directory.Exists(ExportFolder))
                    Directory.CreateDirectory(ExportFolder);

                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"version\": 12,");
                sb.AppendLine($"  \"exportedAt\": \"{DateTime.UtcNow:O}\",");

                // Descriptions
                int descCount = descriptions?.Count ?? 0;
                sb.AppendLine($"  \"descriptionCount\": {descCount},");
                sb.AppendLine("  \"descriptions\": {");
                AppendDictionary(sb, descriptions);
                sb.AppendLine("  },");

                // Names
                int nameCount = names?.Count ?? 0;
                sb.AppendLine($"  \"nameCount\": {nameCount},");
                sb.AppendLine("  \"names\": {");
                AppendDictionary(sb, names);
                sb.AppendLine("  },");

                // Titles
                int titleCount = titles?.Count ?? 0;
                sb.AppendLine($"  \"titleCount\": {titleCount},");
                sb.AppendLine("  \"titles\": {");
                AppendDictionary(sb, titles);
                sb.AppendLine("  },");

                // Banners
                int bannerCount = banners?.Count ?? 0;
                sb.AppendLine($"  \"bannerCount\": {bannerCount},");
                sb.AppendLine("  \"banners\": {");
                AppendDictionary(sb, banners);
                sb.AppendLine("  },");

                // Cultures (v3)
                int cultureCount = cultures?.Count ?? 0;
                sb.AppendLine($"  \"cultureCount\": {cultureCount},");
                sb.AppendLine("  \"cultures\": {");
                AppendDictionary(sb, cultures);
                sb.AppendLine("  },");

                // Occupations (v3)
                int occupationCount = occupations?.Count ?? 0;
                sb.AppendLine($"  \"occupationCount\": {occupationCount},");
                sb.AppendLine("  \"occupations\": {");
                AppendDictionary(sb, occupations);
                sb.AppendLine("  },");

                // Culture Definitions (v4) — custom culture troop tree definitions
                int cultureDefCount = cultureDefs?.Count ?? 0;
                sb.AppendLine($"  \"cultureDefCount\": {cultureDefCount},");
                sb.AppendLine("  \"cultureDefs\": {");
                AppendDictionary(sb, cultureDefs);
                sb.AppendLine("  },");

                // Hero Info Fields (v4) — structured hero fields (backstory, personality, etc.)
                int heroInfoCount = heroInfoFields?.Count ?? 0;
                sb.AppendLine($"  \"heroInfoFieldCount\": {heroInfoCount},");
                sb.AppendLine("  \"heroInfoFields\": {");
                AppendDictionary(sb, heroInfoFields);
                sb.AppendLine("  },");

                // Tags (v5) — player-defined tags per encyclopedia entry
                int tagCount = tags?.Count ?? 0;
                sb.AppendLine($"  \"tagCount\": {tagCount},");
                sb.AppendLine("  \"tags\": {");
                AppendDictionary(sb, tags);
                sb.AppendLine("  },");

                // Edit timestamps (v6) — in-game date when each entry was last edited
                int timestampCount = timestamps?.Count ?? 0;
                sb.AppendLine($"  \"timestampCount\": {timestampCount},");
                sb.AppendLine("  \"timestamps\": {");
                AppendDictionary(sb, timestamps);
                sb.AppendLine("  },");

                // Journal entries (v7) — dated player journal per entry
                int journalCount = journal?.Count ?? 0;
                sb.AppendLine($"  \"journalCount\": {journalCount},");
                sb.AppendLine("  \"journal\": {");
                AppendDictionary(sb, journal);
                sb.AppendLine("  },");

                // Relation notes (v8) — player notes about hero relationships
                int relationNoteCount = relationNotes?.Count ?? 0;
                sb.AppendLine($"  \"relationNoteCount\": {relationNoteCount},");
                sb.AppendLine("  \"relationNotes\": {");
                AppendDictionary(sb, relationNotes);
                sb.AppendLine("  },");

                // Tag notes (v9) — player notes attached to specific tags on entities
                int tagNoteCount = tagNotes?.Count ?? 0;
                sb.AppendLine($"  \"tagNoteCount\": {tagNoteCount},");
                sb.AppendLine("  \"tagNotes\": {");
                AppendDictionary(sb, tagNotes);
                sb.AppendLine("  },");

                // Relation history (v10) — relation change tracking between heroes
                int relationHistoryCount = relationHistory?.Count ?? 0;
                sb.AppendLine($"  \"relationHistoryCount\": {relationHistoryCount},");
                sb.AppendLine("  \"relationHistory\": {");
                AppendDictionary(sb, relationHistory);
                sb.AppendLine("  },");

                // Tag categories (v10) — user-defined tag category groupings
                int tagCategoryCount = tagCategories?.Count ?? 0;
                sb.AppendLine($"  \"tagCategoryCount\": {tagCategoryCount},");
                sb.AppendLine("  \"tagCategories\": {");
                AppendDictionary(sb, tagCategories);
                sb.AppendLine("  },");

                // Tag presets (v10) — saved tag preset templates
                int tagPresetCount = tagPresets?.Count ?? 0;
                sb.AppendLine($"  \"tagPresetCount\": {tagPresetCount},");
                sb.AppendLine("  \"tagPresets\": {");
                AppendDictionary(sb, tagPresets);
                sb.AppendLine("  },");

                // Per-hero auto-tag thresholds (v10) — custom relation thresholds per hero
                int autoTagThresholdCount = perHeroAutoTagThresholds?.Count ?? 0;
                sb.AppendLine($"  \"perHeroAutoTagThresholdCount\": {autoTagThresholdCount},");
                sb.AppendLine("  \"perHeroAutoTagThresholds\": {");
                AppendDictionary(sb, perHeroAutoTagThresholds);
                sb.AppendLine("  },");

                // Relation note tags (v11) — tag assigned to each relation note (ally, rival, etc.)
                int relationNoteTagCount = relationNoteTags?.Count ?? 0;
                sb.AppendLine($"  \"relationNoteTagCount\": {relationNoteTagCount},");
                sb.AppendLine("  \"relationNoteTags\": {");
                AppendDictionary(sb, relationNoteTags);
                sb.AppendLine("  },");

                // Relation note tag locks (v11) — locked tags that won't be auto-suggested
                int relationNoteTagLockCount = relationNoteTagLocks?.Count ?? 0;
                sb.AppendLine($"  \"relationNoteTagLockCount\": {relationNoteTagLockCount},");
                sb.AppendLine("  \"relationNoteTagLocks\": {");
                AppendDictionary(sb, relationNoteTagLocks);
                sb.AppendLine("  },");

                // Item lore overrides (v12) — key "<itemId>|<ownerId>", player's custom item lore
                int itemLoreCount = itemLore?.Count ?? 0;
                sb.AppendLine($"  \"itemLoreCount\": {itemLoreCount},");
                sb.AppendLine("  \"itemLore\": {");
                AppendDictionary(sb, itemLore);
                sb.AppendLine("  }");

                sb.AppendLine("}");

                string filePath = GetExportFilePath();
                File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                EECoreLogger.For("EE-Core").Debug("[SharedFileExporter]: " + ex.ToString());
                return false;
            }
        }

        private static void AppendDictionary(StringBuilder sb, Dictionary<string, string> dict)
        {
            if (dict == null || dict.Count == 0) return;
            bool first = true;
            foreach (var kvp in dict)
            {
                if (!first) sb.AppendLine(",");
                first = false;
                sb.Append($"    \"{EscapeJsonString(kvp.Key)}\": \"{EscapeJsonString(kvp.Value)}\"");
            }
            sb.AppendLine();
        }

        /// <summary>
        /// Reads descriptions from the shared JSON file (legacy compatibility).
        /// Returns null if the file doesn't exist or can't be parsed.
        /// </summary>
        public static Dictionary<string, string> Import()
        {
            var result = ImportAll();
            return result?.Descriptions;
        }

        /// <summary>
        /// Reads all data (descriptions, names, titles, banners) from the shared JSON file.
        /// Returns null if the file doesn't exist or can't be parsed.
        /// </summary>
        public static ExportData ImportAll()
        {
            try
            {
                string filePath = GetExportFilePath();
                if (!File.Exists(filePath))
                    return null;

                string json = File.ReadAllText(filePath, Encoding.UTF8);
                var data = new ExportData
                {
                    Descriptions = ParseJsonSection(json, "descriptions"),
                    Names = ParseJsonSection(json, "names"),
                    Titles = ParseJsonSection(json, "titles"),
                    Banners = ParseJsonSection(json, "banners"),
                    Cultures = ParseJsonSection(json, "cultures"),
                    Occupations = ParseJsonSection(json, "occupations"),
                    CultureDefs = ParseJsonSection(json, "cultureDefs"),
                    HeroInfoFields = ParseJsonSection(json, "heroInfoFields"),
                    Tags = ParseJsonSection(json, "tags"),
                    Timestamps = ParseJsonSection(json, "timestamps"),
                    Journal = ParseJsonSection(json, "journal"),
                    RelationNotes = ParseJsonSection(json, "relationNotes"),
                    TagNotes = ParseJsonSection(json, "tagNotes"),
                    RelationHistory = ParseJsonSection(json, "relationHistory"),
                    TagCategories = ParseJsonSection(json, "tagCategories"),
                    TagPresets = ParseJsonSection(json, "tagPresets"),
                    PerHeroAutoTagThresholds = ParseJsonSection(json, "perHeroAutoTagThresholds"),
                    RelationNoteTags = ParseJsonSection(json, "relationNoteTags"),
                    RelationNoteTagLocks = ParseJsonSection(json, "relationNoteTagLocks"),
                    ItemLore = ParseJsonSection(json, "itemLore")
                };

                // Fallback for version 1 files that only have descriptions
                if (data.Descriptions == null)
                    data.Descriptions = ParseJson(json);

                return data;
            }
            catch (Exception ex)
            {
                EECoreLogger.For("EE-Core").Debug("[SharedFileExporter]: " + ex.ToString());
                return null;
            }
        }

        /// <summary>
        /// Holds all exported data sections.
        /// </summary>
        public class ExportData
        {
            public Dictionary<string, string> Descriptions;
            public Dictionary<string, string> Names;
            public Dictionary<string, string> Titles;
            public Dictionary<string, string> Banners;
            public Dictionary<string, string> Cultures;
            public Dictionary<string, string> Occupations;
            public Dictionary<string, string> CultureDefs;
            public Dictionary<string, string> HeroInfoFields;
            public Dictionary<string, string> Tags;
            public Dictionary<string, string> Timestamps;
            public Dictionary<string, string> Journal;
            public Dictionary<string, string> RelationNotes;
            public Dictionary<string, string> TagNotes;
            public Dictionary<string, string> RelationHistory;
            public Dictionary<string, string> TagCategories;
            public Dictionary<string, string> TagPresets;
            public Dictionary<string, string> PerHeroAutoTagThresholds;
            public Dictionary<string, string> RelationNoteTags;
            public Dictionary<string, string> RelationNoteTagLocks;
            public Dictionary<string, string> ItemLore;
        }

        /// <summary>
        /// Parses a named JSON section (e.g., "descriptions", "names", "banners").
        /// Returns null if the section doesn't exist.
        /// </summary>
        private static Dictionary<string, string> ParseJsonSection(string json, string sectionName)
        {
            int sectionStart = json.IndexOf("\"" + sectionName + "\"");
            if (sectionStart == -1)
                return null;

            int braceStart = json.IndexOf('{', sectionStart);
            if (braceStart == -1)
                return null;

            int braceEnd = FindMatchingBrace(json, braceStart);
            if (braceEnd == -1)
                return null;

            string content = json.Substring(braceStart + 1, braceEnd - braceStart - 1);
            return ParseKeyValuePairs(content);
        }

        /// <summary>
        /// Simple JSON parser for the descriptions export format (legacy).
        /// Only supports the specific format we use: { "descriptions": { "key": "value", ... } }
        /// </summary>
        private static Dictionary<string, string> ParseJson(string json)
        {
            return ParseJsonSection(json, "descriptions");
        }

        /// <summary>
        /// Parses key-value pairs from a JSON object body (content between braces).
        /// </summary>
        private static Dictionary<string, string> ParseKeyValuePairs(string content)
        {
            var result = new Dictionary<string, string>();
            int pos = 0;
            while (pos < content.Length)
            {
                while (pos < content.Length && char.IsWhiteSpace(content[pos]))
                    pos++;

                if (pos >= content.Length) break;

                if (content[pos] == ',') { pos++; continue; }

                if (content[pos] != '"') break;

                int keyStart = pos + 1;
                int keyEnd = FindStringEnd(content, keyStart);
                if (keyEnd == -1) break;

                string key = UnescapeJsonString(content.Substring(keyStart, keyEnd - keyStart));
                pos = keyEnd + 1;

                while (pos < content.Length && (char.IsWhiteSpace(content[pos]) || content[pos] == ':'))
                    pos++;

                if (pos >= content.Length || content[pos] != '"') break;

                int valueStart = pos + 1;
                int valueEnd = FindStringEnd(content, valueStart);
                if (valueEnd == -1) break;

                string value = UnescapeJsonString(content.Substring(valueStart, valueEnd - valueStart));
                pos = valueEnd + 1;

                result[key] = value;
            }
            return result;
        }

        /// <summary>
        /// Finds the matching closing brace for an opening brace.
        /// </summary>
        private static int FindMatchingBrace(string json, int openBracePos)
        {
            int depth = 1;
            bool inString = false;
            bool escaped = false;

            for (int i = openBracePos + 1; i < json.Length; i++)
            {
                char c = json[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (!inString)
                {
                    if (c == '{')
                        depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                            return i;
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// Finds the end of a JSON string (the closing quote).
        /// </summary>
        private static int FindStringEnd(string content, int start)
        {
            bool escaped = false;
            for (int i = start; i < content.Length; i++)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (content[i] == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (content[i] == '"')
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Escapes a string for JSON output.
        /// </summary>
        private static string EscapeJsonString(string str)
        {
            if (string.IsNullOrEmpty(str))
                return "";

            var sb = new StringBuilder(str.Length);
            foreach (char c in str)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32)
                            sb.AppendFormat("\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Unescapes a JSON string.
        /// </summary>
        private static string UnescapeJsonString(string str)
        {
            if (string.IsNullOrEmpty(str))
                return "";

            var sb = new StringBuilder(str.Length);
            for (int i = 0; i < str.Length; i++)
            {
                if (str[i] == '\\' && i + 1 < str.Length)
                {
                    char next = str[i + 1];
                    switch (next)
                    {
                        case '\\': sb.Append('\\'); i++; break;
                        case '"': sb.Append('"'); i++; break;
                        case 'n': sb.Append('\n'); i++; break;
                        case 'r': sb.Append('\r'); i++; break;
                        case 't': sb.Append('\t'); i++; break;
                        case 'u':
                            if (i + 5 < str.Length)
                            {
                                string hex = str.Substring(i + 2, 4);
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int code))
                                {
                                    sb.Append((char)code);
                                    i += 5;
                                }
                                else
                                {
                                    sb.Append('\\');
                                }
                            }
                            else
                            {
                                sb.Append('\\');
                            }
                            break;
                        default:
                            sb.Append('\\');
                            break;
                    }
                }
                else
                {
                    sb.Append(str[i]);
                }
            }
            return sb.ToString();
        }
    }
}
