using System;
using System.Collections.Generic;
using System.Text;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Handles text processing for timeline entries: date parsing/formatting,
    /// HTML stripping, marker stripping, icon/category detection, and duplicate detection.
    /// </summary>
    public static class TimelineTextProcessor
    {
        private const int CloseMarkerLength = 4; // length of «/h» or «/s»
        private const int SlainTagPrefixLength = 8; // length of " [slain:"
        // ──────────────────────────────────────────────────────────────────
        // Date processing
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Simplifies various date formats to "Season Year" for cleaner display.
        /// Handles: "Day X of Season, Year", "Season Year", "Day X of Season Year",
        /// "Season, Year", raw year numbers, and non-standard formats gracefully.
        /// </summary>
        public static string SimplifyDate(string date)
        {
            if (string.IsNullOrEmpty(date)) return "";

            string trimmed = date.Trim();
            if (trimmed.Length == 0) return "";

            // "Day 1 of Summer, 1084" → "Summer 1084"
            int ofIndex = trimmed.IndexOf(" of ");
            if (ofIndex >= 0)
            {
                string seasonYear = trimmed.Substring(ofIndex + 4);
                return seasonYear.Replace(", ", " ").Replace(",", " ").Trim();
            }

            // "Summer, 1084" → "Summer 1084"
            if (trimmed.Contains(","))
                return trimmed.Replace(", ", " ").Replace(",", " ").Trim();

            // Try to detect and normalize any format with a recognized season name
            string[] parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                // Find season and year tokens wherever they appear
                string seasonToken = null;
                string yearToken = null;
                foreach (string p in parts)
                {
                    if (seasonToken == null && ParseSeasonIndex(p) >= 0 && !IsNumeric(p))
                        seasonToken = CanonicalSeasonName(p);
                    int yr;
                    if (yearToken == null && int.TryParse(p, out yr) && yr > 0)
                        yearToken = p;
                }
                if (seasonToken != null && yearToken != null)
                    return seasonToken + " " + yearToken;
                if (yearToken != null)
                    return yearToken;
                if (seasonToken != null)
                    return seasonToken;
            }

            // Already "Summer 1084" or just a year number
            return trimmed;
        }

        /// <summary>
        /// Returns canonical (title case) season name, or the original string if not a season.
        /// </summary>
        private static string CanonicalSeasonName(string token)
        {
            switch (token.ToLowerInvariant())
            {
                case "spring": return "Spring";
                case "summer": return "Summer";
                case "autumn": case "fall": return "Autumn";
                case "winter": return "Winter";
                default: return token;
            }
        }

        private static bool IsNumeric(string s)
        {
            int dummy;
            return int.TryParse(s, out dummy);
        }

        /// <summary>
        /// Computes a numeric sort key from a simplified date like "Summer 1084".
        /// Format: year * 10 + seasonIndex (Spring=0, Summer=1, Autumn=2, Winter=3).
        /// Handles multiple date formats gracefully, including reversed order,
        /// bare years, and unrecognized tokens.
        /// </summary>
        public static int ComputeSortKey(string simpleDate)
        {
            if (string.IsNullOrEmpty(simpleDate)) return 0;

            string[] parts = simpleDate.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return 0;

            // Scan all tokens for season and year in any position
            int season = -1;
            int year = -1;
            foreach (string p in parts)
            {
                if (season < 0)
                {
                    int s = ParseSeasonIndex(p);
                    if (s >= 0 && !IsNumeric(p))
                        season = s;
                }
                int yr;
                if (year < 0 && int.TryParse(p, out yr) && yr > 0)
                    year = yr;
            }

            if (year >= 0)
                return year * 10 + Math.Max(season, 0);

            return 0;
        }

        /// <summary>
        /// Parses a season name to its index. Returns -1 for unrecognized tokens.
        /// Spring=0, Summer=1, Autumn/Fall=2, Winter=3.
        /// </summary>
        private static int ParseSeasonIndex(string season)
        {
            switch (season.ToLowerInvariant())
            {
                case "spring": return 0;
                case "summer": return 1;
                case "autumn": case "fall": return 2;
                case "winter": return 3;
                default: return -1;
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Text cleaning
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Strips only the [slain:N] metadata tag from text, preserving everything else.
        /// </summary>
        public static string StripSlainTag(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int slainIdx = text.IndexOf(" [slain:");
            if (slainIdx < 0) return text;
            int closeIdx = text.IndexOf(']', slainIdx + SlainTagPrefixLength);
            if (closeIdx < 0) return text;
            return text.Remove(slainIdx, closeIdx - slainIdx + 1);
        }

        /// <summary>
        /// Strips hero/settlement markers: «h:heroId»Name«/h» → Name, «s:settId»Name«/s» → Name.
        /// </summary>
        public static string StripMarkersForDisplay(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            string result = text;

            // Strip [slain:N] tags (internal metadata for kill tracking)
            int slainIdx = result.IndexOf(" [slain:");
            if (slainIdx >= 0)
            {
                int closeIdx = result.IndexOf(']', slainIdx + SlainTagPrefixLength);
                if (closeIdx >= 0)
                    result = result.Remove(slainIdx, closeIdx - slainIdx + 1);
            }
            while (true)
            {
                int start = result.IndexOf("\u00ABh:");
                if (start < 0) start = result.IndexOf("\u00ABs:");
                if (start < 0) start = result.IndexOf("\u00ABc:");
                if (start < 0) start = result.IndexOf("\u00ABk:");
                if (start < 0) break;

                int markerEnd = result.IndexOf("\u00BB", start);
                if (markerEnd < 0) break;

                result = result.Remove(start, markerEnd - start + 1);

                int closeStart = result.IndexOf("\u00AB/h\u00BB", start);
                if (closeStart < 0) closeStart = result.IndexOf("\u00AB/s\u00BB", start);
                if (closeStart < 0) closeStart = result.IndexOf("\u00AB/c\u00BB", start);
                if (closeStart < 0) closeStart = result.IndexOf("\u00AB/k\u00BB", start);
                if (closeStart >= 0)
                    result = result.Remove(closeStart, CloseMarkerLength);
            }
            return result;
        }

        /// <summary>
        /// Strips category tags [War], [Politics], etc. and trims whitespace.
        /// </summary>
        public static string CleanEntryText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            string result = text;
            foreach (string tag in new[] { "[War] ", "[Politics] ", "[Crime] ", "[Family] ", "[Other] " })
            {
                if (result.StartsWith(tag))
                {
                    result = result.Substring(tag.Length);
                    break;
                }
            }
            return result.Trim();
        }

        /// <summary>
        /// Strips all HTML tags from text, collapsing multiple spaces.
        /// </summary>
        public static string StripHtmlTags(string html)
        {
            if (string.IsNullOrEmpty(html)) return html;
            if (!html.Contains("<")) return html;

            var sb = new StringBuilder(html.Length);
            bool inTag = false;
            for (int i = 0; i < html.Length; i++)
            {
                char c = html[i];
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(c);
            }
            string result = sb.ToString();
            while (result.Contains("  "))
                result = result.Replace("  ", " ");
            return result.Trim();
        }

        /// <summary>
        /// Converts native Bannerlord HTML links to encyclopedia link format.
        /// Native: href="event:Hero-lord_3_8" → href="event:Encyclopedia/Hero/lord_3_8"
        /// Also strips bold tags which RichTextWidget may not support.
        /// </summary>
        public static string ConvertNativeHtmlLinks(string html)
        {
            if (string.IsNullOrEmpty(html)) return html;

            var sb = new StringBuilder(html.Length + 64);
            int pos = 0;
            string hrefMarker = "href=\"event:";

            while (pos < html.Length)
            {
                int hrefStart = html.IndexOf(hrefMarker, pos, StringComparison.Ordinal);
                if (hrefStart < 0)
                {
                    sb.Append(html, pos, html.Length - pos);
                    break;
                }

                sb.Append(html, pos, hrefStart + hrefMarker.Length - pos);
                pos = hrefStart + hrefMarker.Length;

                int quoteEnd = html.IndexOf('"', pos);
                if (quoteEnd < 0)
                {
                    sb.Append(html, pos, html.Length - pos);
                    break;
                }
                string nativeId = html.Substring(pos, quoteEnd - pos);

                string converted = ConvertNativeIdToEncyclopediaLink(nativeId);
                sb.Append(converted);
                pos = quoteEnd;
            }

            string result = sb.ToString();
            result = result.Replace("<b>", "").Replace("</b>", "");
            return result;
        }

        private static string ConvertNativeIdToEncyclopediaLink(string nativeId)
        {
            string[] knownPrefixes = { "Hero-", "Settlement-", "Clan-", "Kingdom-" };
            foreach (string prefix in knownPrefixes)
            {
                if (nativeId.StartsWith(prefix, StringComparison.Ordinal))
                {
                    string type = prefix.TrimEnd('-');
                    string objectId = nativeId.Substring(prefix.Length);
                    return "Encyclopedia/" + type + "/" + objectId;
                }
            }
            return nativeId;
        }

        // ──────────────────────────────────────────────────────────────────
        // Icon & category detection
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Detects event category from text content. Returns both an icon string
        /// and a TimelineCategory for filtering and color-coding.
        /// </summary>
        public static (string Icon, TimelineCategory Category) DetectIconAndCategory(string originalText)
        {
            string lower = originalText.ToLowerInvariant();

            // Battle/siege entries must be checked BEFORE death/capture to avoid
            // casualty counts ("were slain", "Killed 12") or hero details ("was taken prisoner")
            // triggering the wrong category.
            if (lower.Contains("defeated") || lower.Contains("victory near")
                || lower.Contains("victory at"))
            {
                // Siege battles get [Siege] tag; field battles get [Battle]
                if (lower.Contains("siege"))
                    return ("[Siege]", TimelineCategory.Siege);
                return ("[Battle]", TimelineCategory.Battle);
            }

            if (lower.Contains("killed") || lower.Contains("died") || lower.Contains("death")
                || lower.Contains("slain") || lower.Contains("slew") || lower.Contains("fallen")
                || lower.Contains("executed") || lower.Contains("passed away"))
                return ("[Death]", TimelineCategory.Death);

            if (lower.Contains("captured") || lower.Contains("prisoner") || lower.Contains("captivity")
                || lower.Contains("escaped") || lower.Contains("released") || lower.Contains("ransom"))
                return ("[Capture]", TimelineCategory.Capture);

            if (lower.Contains("siege") || lower.Contains("besieged") || lower.Contains("fell to")
                || lower.Contains("held against"))
                return ("[Siege]", TimelineCategory.Siege);

            if (lower.Contains("raided") || lower.Contains("pillaged") || lower.Contains("raid")
                || lower.Contains("[crime]"))
                return ("[Raid]", TimelineCategory.Raid);

            if (lower.Contains("tournament"))
                return ("[Tournament]", TimelineCategory.Tournament);

            if (lower.Contains("born") || lower.Contains("a child was born"))
                return ("[Birth]", TimelineCategory.Birth);

            if (lower.Contains("married") || lower.Contains("marriage"))
                return ("[Marriage]", TimelineCategory.Marriage);

            if (lower.Contains("peace") || lower.Contains("alliance"))
                return ("[Peace]", TimelineCategory.Peace);

            if (lower.Contains("vengeance") || lower.Contains("sworn") || lower.Contains("grudge"))
                return ("[Vengeance]", TimelineCategory.Vengeance);

            if (lower.Contains("insult"))
                return ("[Insult]", TimelineCategory.Insult);

            if (lower.Contains("joined") || lower.Contains("defect") || lower.Contains("ruler")
                || lower.Contains("clan") || lower.Contains("kingdom") || lower.Contains("destroyed")
                || lower.Contains("granted") || lower.Contains("ownership")
                || lower.Contains("service of") || lower.Contains("departed") || lower.Contains("allegiance")
                || lower.Contains("fealty") || lower.Contains("companion") || lower.Contains("lordship"))
                return ("[Politics]", TimelineCategory.Politics);

            if (lower.Contains("mustered") || lower.Contains("dispersed"))
                return ("[Army]", TimelineCategory.Army);

            if (lower.Contains("rebellion") || lower.Contains("rebelled"))
                return ("[Rebellion]", TimelineCategory.Rebellion);

            if (lower.Contains("quest "))
                return ("[Quest]", TimelineCategory.Quest);

            if (lower.Contains("now level") || lower.Contains("mastery of") || lower.Contains("skill now at"))
                return ("[Skill]", TimelineCategory.Skill);

            if (lower.Contains("war declared") || lower.Contains("declared war"))
                return ("[War]", TimelineCategory.War);

            if (lower.Contains("[war]") || lower.Contains("battle"))
                return ("[Battle]", TimelineCategory.Battle);

            if (lower.Contains("[family]") || lower.Contains("expecting") || lower.Contains("came of age"))
                return ("[Family]", TimelineCategory.Family);

            return ("-", TimelineCategory.Event);
        }

        /// <summary>
        /// Detects category from a native log entry type name (fallback when text detection
        /// returns the default category).
        /// </summary>
        public static (string Icon, TimelineCategory Category) DetectFromTypeName(string typeName)
        {
            string lower = typeName.ToLowerInvariant();

            if (lower.Contains("vengeance") || lower.Contains("grudge"))
                return ("[Vengeance]", TimelineCategory.Vengeance);
            if (lower.Contains("insult"))
                return ("[Insult]", TimelineCategory.Insult);
            if (lower.Contains("battle") || lower.Contains("combat"))
                return ("[Battle]", TimelineCategory.Battle);
            if (lower.Contains("prisoner") || lower.Contains("capture"))
                return ("[Capture]", TimelineCategory.Capture);
            if (lower.Contains("marriage") || lower.Contains("married"))
                return ("[Marriage]", TimelineCategory.Marriage);
            if (lower.Contains("birth") || lower.Contains("born"))
                return ("[Birth]", TimelineCategory.Birth);
            if (lower.Contains("death") || lower.Contains("killed"))
                return ("[Death]", TimelineCategory.Death);
            if (lower.Contains("tournament"))
                return ("[Tournament]", TimelineCategory.Tournament);
            if (lower.Contains("siege"))
                return ("[Siege]", TimelineCategory.Siege);
            if (lower.Contains("army"))
                return ("[Army]", TimelineCategory.Army);
            if (lower.Contains("quest"))
                return ("[Quest]", TimelineCategory.Quest);
            if (lower.Contains("rebellion"))
                return ("[Rebellion]", TimelineCategory.Rebellion);

            return ("[Event]", TimelineCategory.Event);
        }

        // ──────────────────────────────────────────────────────────────────
        // Timeline worthiness (personal vs world events)
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Determines if a journal entry belongs in a hero's personal Timeline.
        /// Timeline = personal biography. Only events where the hero personally acted.
        /// World events (war/peace declarations, kingdom politics) belong in Chronicle.
        /// </summary>
        public static bool IsTimelineWorthy(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string lower = text.ToLowerInvariant();

            // Entries with category tags ([War], [Politics], etc.) are always worthy —
            // they were logged to this entity's journal because the entity was involved.
            // This applies to heroes, settlements, kingdoms, and clans.
            bool hasCategoryTag = lower.StartsWith("[war]") || lower.StartsWith("[politics]")
                || lower.StartsWith("[crime]") || lower.StartsWith("[family]")
                || lower.StartsWith("[other]");
            if (hasCategoryTag) return true;

            // ── EXCLUDE world events from untagged entries (native log, cross-refs) ──
            if (lower.Contains("declared war")) return false;
            if (lower.Contains("made peace with")) return false;
            if (lower.Contains("kingdom destroyed")) return false;
            if (lower.Contains("was destroyed")) return false;
            if (lower.Contains("clan destroyed")) return false;
            if (lower.Contains("now held by")) return false;
            if (lower.Contains("granted to") && lower.Contains("royal decree")) return false;
            if (lower.Contains("ownership changed")) return false;
            if (lower.Contains("clan ") && lower.Contains("defected to")) return false;
            if (lower.Contains("clan ") && lower.Contains(" joined from")) return false;
            if (lower.Contains("clan ") && lower.Contains(" departed")) return false;
            if (lower.Contains("became ruler of")) return false;
            if (lower.Contains(" fell to ") && !lower.Contains("defeated")) return false;
            if (lower.Contains(" held against ")) return false;
            if (lower.Contains(" besieged by ")) return false;
            if (lower.Contains(" raided by ")) return false;
            if (lower.Contains("pillaged by")) return false;

            // ── INCLUDE personal events (untagged) ──
            if (lower.Contains("defeated")) return true;
            if (lower.Contains("captured")) return true;
            if (lower.Contains("escaped") || lower.Contains("released") || lower.Contains("ransomed")) return true;
            if (lower.Contains("killed") || lower.Contains("died")) return true;
            if (lower.Contains("began siege")) return true;
            if (lower.Contains("raided the")) return true;
            if (lower.Contains("tournament")) return true;
            if (lower.Contains("married")) return true;
            if (lower.Contains("born")) return true;
            if (lower.StartsWith("[politics] joined ") || lower.StartsWith("[politics] left ")) return true;
            if (lower.Contains("[war] captured") || lower.Contains("[war] lost")) return true;
            if (lower.Contains("[politics] granted") && !lower.Contains("granted to")) return true;

            // Manual journal entries (no category tag = player-written notes)
            // All remaining untagged entries are considered worthy (player-written)
            return true;
        }

        // ──────────────────────────────────────────────────────────────────
        // Duplicate detection — improved with direction awareness
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Checks if an entry with the same date and similar text already exists.
        /// Improved: direction-aware — "Defeated X" and "Defeated by X" are treated
        /// as different events (winner vs loser perspective), while "Married X" from
        /// multiple perspectives is still deduplicated.
        /// </summary>
        public static bool IsDuplicate(List<TimelineEntry> entries, string date, string text)
        {
            string textLower = text.ToLowerInvariant();
            foreach (var existing in entries)
            {
                if (existing.Date != date) continue;
                string existingLower = existing.Text.ToLowerInvariant();

                // Exact match
                if (existingLower == textLower) return true;

                // Containment match (one text is subset of the other)
                if (existingLower.Contains(textLower) || textLower.Contains(existingLower))
                    return true;

                // "Once-per-day" events: a hero can only marry/be born/die once per day
                foreach (string kw in new[] { "married", "born", "died", "executed", "came of age" })
                    if (existingLower.Contains(kw) && textLower.Contains(kw))
                        return true;

                // Cross-source capture/release dedup: the mod logs "Captured by X" / "Released from X"
                // while the native game logs "taken prisoner by X" / "released after battle".
                // Treat these as synonyms so we keep only the more detailed mod entry.
                bool existingIsCapture = existingLower.Contains("captured") || existingLower.Contains("prisoner");
                bool newIsCapture = textLower.Contains("captured") || textLower.Contains("prisoner");
                if (existingIsCapture && newIsCapture) return true;

                bool existingIsRelease = existingLower.Contains("released") || existingLower.Contains("set free");
                bool newIsRelease = textLower.Contains("released") || textLower.Contains("set free");
                if (existingIsRelease && newIsRelease) return true;

                // Direction-aware dedup: "defeated" without "by" vs "defeated by" are different perspectives
                // Same event verb + shared name word → same event UNLESS direction differs
                foreach (string verb in new[] { "captured", "escaped", "released",
                    "ransomed", "siege", "raided", "tournament", "killed" })
                {
                    if (!existingLower.Contains(verb) || !textLower.Contains(verb)) continue;

                    var wordsA = new HashSet<string>(existingLower.Split(new[] { ' ' },
                        StringSplitOptions.RemoveEmptyEntries));
                    foreach (var w in textLower.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        if (w.Length > 3 && w != verb && wordsA.Contains(w))
                            return true;
                }

                // Special "defeated" handling: direction-aware
                if (existingLower.Contains("defeated") && textLower.Contains("defeated"))
                {
                    bool existingIsDefeat = existingLower.Contains("defeated by");
                    bool newIsDefeat = textLower.Contains("defeated by");
                    // Same direction (both won or both lost) + shared name = duplicate
                    if (existingIsDefeat == newIsDefeat)
                    {
                        var wordsA = new HashSet<string>(existingLower.Split(new[] { ' ' },
                            StringSplitOptions.RemoveEmptyEntries));
                        foreach (var w in textLower.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                            if (w.Length > 3 && w != "defeated" && w != "by" && wordsA.Contains(w))
                                return true;
                    }
                    // Different direction = different event perspective, not a duplicate
                }
            }
            return false;
        }

        // ──────────────────────────────────────────────────────────────────
        // Commander extraction — extended to support sieges and raids
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts the commander/leader name from a timeline entry.
        /// Extended beyond battles to also cover sieges, raids, and captures.
        /// </summary>
        public static string ExtractCommanderName(TimelineEntry entry, string viewedHeroId)
        {
            if (entry == null) return null;

            // Show commander for battle, siege, raid, and capture events
            string lower = (entry.Text ?? "").ToLowerInvariant();
            bool isCommandEvent = lower.Contains("defeated") || lower.Contains("siege")
                || lower.Contains("besieged") || lower.Contains("captured by")
                || lower.Contains("raided") || lower.Contains("pillaged")
                || lower.Contains("began siege") || lower.Contains("fell to");
            if (!isCommandEvent) return null;

            // If cross-referenced from another hero's journal, that hero is the commander
            if (!string.IsNullOrEmpty(entry.SourceObjectId) && !string.IsNullOrEmpty(viewedHeroId)
                && entry.SourceObjectId != viewedHeroId)
            {
                try
                {
                    var hero = TaleWorlds.CampaignSystem.Hero.FindFirst(h => h.StringId == entry.SourceObjectId);
                    if (hero != null)
                        return hero.Name?.ToString();
                }
                catch (Exception ex) { MCMSettings.DebugLog("[TimelineTextProcessor]: " + ex.ToString()); }
            }

            // Extract from raw text hero markers «h:id»Name«/h»
            if (!string.IsNullOrEmpty(entry.RawText))
            {
                try
                {
                    int markerStart = entry.RawText.IndexOf("\u00ABh:");
                    if (markerStart >= 0)
                    {
                        int markerEnd = entry.RawText.IndexOf("\u00BB", markerStart);
                        int nameEnd = markerEnd >= 0 ? entry.RawText.IndexOf("\u00AB/h\u00BB", markerEnd) : -1;
                        if (markerEnd >= 0 && nameEnd > markerEnd && nameEnd - markerEnd - 1 >= 0)
                        {
                            string name = entry.RawText.Substring(markerEnd + 1, nameEnd - markerEnd - 1);
                            if (!string.IsNullOrWhiteSpace(name))
                                return name;
                        }
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("[TimelineTextProcessor]: " + ex.ToString()); }
            }

            return null;
        }
    }
}
