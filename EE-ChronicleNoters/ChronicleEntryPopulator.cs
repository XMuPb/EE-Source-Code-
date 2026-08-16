using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using EditableEncyclopediaApi = EditableEncyclopedia.EditableEncyclopediaAPI;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace EditableEncyclopedia.ChronicleNoters
{
    /// <summary>
    /// Pure-data populator. Reads journal data from EditableEncyclopediaAPI and produces
    /// ChronicleEntryVM instances ready for the redesigned panel.
    ///
    /// Three responsibilities:
    ///   1. ExtractYear — pull a 4-digit year out of a free-form Bannerlord date string
    ///   2. ClassifyEventTags — keyword-sniff War/Crime/Politics/Family/Other from entry text
    ///   3. ClassifyEntityTags — derive Kingdom/Clan/Hero/Settlement/Other from the entity-id prefix
    ///
    /// Pure static; no UI dependency; safe to call from any thread (read-only consumer of the
    /// EditableEncyclopediaAPI surface).
    /// </summary>
    public static class ChronicleEntryPopulator
    {
        /// <summary>
        /// Builds a VM list from the global chronicle data. Caller usually feeds these
        /// into GlobalChroniclePanelVM.AddSourceEntry before InitializeYearStripFromEntries.
        /// </summary>
        public static List<ChronicleEntryVM> BuildAllEntryVMs(
            Func<string, bool> isBookmarked = null,
            Func<string, string> getNoteText = null,
            Action<string> selectById = null)
        {
            var result = new List<ChronicleEntryVM>();
            List<EditableEncyclopediaApi.ChronicleEntry> raw;
            try
            {
                raw = EditableEncyclopediaApi.GetAllChronicleEntries();
            }
            catch
            {
                return result;
            }
            if (raw == null) return result;

            int buildIdx = 0;
            foreach (var src in raw)
            {
                if (src == null || string.IsNullOrEmpty(src.Text)) continue;

                // Strip Bannerlord rich-text markers («h:...», «k:...», «c:...», etc.) so the
                // title/body show as plain text in the panel. EncyclopediaEditBehavior owns the
                // canonical stripper since it's used elsewhere for dedup + display.
                string cleanText;
                try { cleanText = EncyclopediaEditBehavior.StripEntityMarkers(src.Text) ?? src.Text; }
                catch { cleanText = src.Text; }

                // 2026-08-02: capture the authored category BEFORE the prefix is stripped. EE's
                // auto-journal handlers already label every entry they write ("[War] ", "[Politics] ",
                // ...), and EncyclopediaEditBehavior.cs:3091 treats that prefix as authoritative too.
                // Keyword-sniffing the stripped text is a strictly worse approximation of a label we
                // already have, so read it first and only guess when there is no usable prefix.
                EventTag? prefixEventTag = ExtractPrefixEventTag(cleanText);

                // 2026-05-29: strip leading "[Word]" category prefix (e.g., "[War] Victory near...")
                // because the tag pills already show the category visually — the bracket is redundant.
                cleanText = _bracketPrefixRegex.Replace(cleanText, "");

                var vm = new ChronicleEntryVM();
                vm.EntryId   = MakeStableId(src.EntityId, src.Date, src.Text);
                vm.EntryYear = ExtractYear(src.Date);
                vm.DateLabel = (src.Date ?? string.Empty).ToUpperInvariant();
                var dateMatch = _dayOfSeasonYearRegex.Match(vm.DateLabel);
                if (dateMatch.Success)
                {
                    vm.DayValue = dateMatch.Groups[1].Value;
                    vm.SeasonValue = dateMatch.Groups[2].Value;
                }
                vm.BuildIndex = buildIdx++;
                vm.SortDateKey = ComputeSortDateKey(vm.EntryYear, vm.SeasonValue, vm.DayValue);
                vm.TitleText = TruncateForTitle(cleanText);
                vm.BodyText  = cleanText;
                // Authoritative prefix wins; the keyword sniffer is only the fallback for entries
                // that carry no recognisable "[Category]" label (hand-written notes, "[Edited]", ...).
                vm.EventTags = prefixEventTag.HasValue ? prefixEventTag.Value : ClassifyEventTags(cleanText);
                vm.EntityTags = ClassifyEntityTags(src.EntityId, cleanText);

                // 2026-05-29: resolve raw entity id (Hero/Clan/Settlement/Kingdom by StringId).
                // Wrap individual lookups in their own try/catch so one engine collection blowing
                // up doesn't kill the whole populate (defensive — Hero.AllAliveHeroes accessor
                // has thrown during very early loads in past versions).
                vm.SourceEntityId = src.EntityId;
                ResolveSourceEntity(vm, src.EntityId);

                // 2026-08-02: gold + looted items captured by the sibling EditableEncyclopedia
                // module. The RAW src.Text is passed deliberately — NOT cleanText. The spoils key
                // is entityId + "|" + date + "|" + first 24 chars of the text as STORED, so
                // marker-stripped or "[Category]"-stripped text would build a different key and
                // the block would silently never appear. This is the same triple already fed to
                // MakeStableId above (EntryId), which is by construction the same string.
                BuildSpoils(vm, src.EntityId, src.Date, src.Text);

                if (isBookmarked != null)
                {
                    try { vm.IsStarred = isBookmarked(vm.EntryId); } catch { }
                }
                if (getNoteText != null)
                {
                    try { vm.NoteText = getNoteText(vm.EntryId); } catch { }
                }

                result.Add(vm);
            }
            ComputeTimelineContext(result);
            // ONE grouped pass over the finished list. Must run after every VM exists —
            // per-entry scanning inside the loop above would be O(n²) on a long campaign.
            BuildCrossReferences(result, selectById);
            return result;
        }

        private static void ComputeTimelineContext(List<ChronicleEntryVM> all)
        {
            if (all == null || all.Count == 0) return;

            var perYearTotal = new Dictionary<int, int>();
            var perEntityTotal = new Dictionary<string, int>();
            foreach (var vm in all)
            {
                if (vm.EntryYear > 0)
                {
                    int n;
                    perYearTotal[vm.EntryYear] = perYearTotal.TryGetValue(vm.EntryYear, out n) ? n + 1 : 1;
                }
                if (!string.IsNullOrEmpty(vm.SourceEntityId))
                {
                    int n;
                    perEntityTotal[vm.SourceEntityId] = perEntityTotal.TryGetValue(vm.SourceEntityId, out n) ? n + 1 : 1;
                }
            }

            var rankInYear = new Dictionary<int, int>();
            foreach (var vm in all)
            {
                if (vm.EntryYear <= 0) continue;

                int rank;
                rank = rankInYear.TryGetValue(vm.EntryYear, out rank) ? rank + 1 : 1;
                rankInYear[vm.EntryYear] = rank;

                int yearTotal = perYearTotal[vm.EntryYear];
                int entityTotal = 0;
                if (!string.IsNullOrEmpty(vm.SourceEntityId))
                    perEntityTotal.TryGetValue(vm.SourceEntityId, out entityTotal);

                bool showYear = yearTotal > 1;
                bool showEntity = entityTotal > 1 && !string.IsNullOrEmpty(vm.SourceEntityName);
                if (!showYear && !showEntity) continue;

                var sb = new System.Text.StringBuilder();
                if (showYear) sb.Append(rank).Append(" of ").Append(yearTotal).Append(" entries this year");
                if (showEntity)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(entityTotal - 1).Append(" other about ").Append(vm.SourceEntityName);
                }
                vm.TimelineContextLabel = sb.ToString();
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Year extraction
        // ─────────────────────────────────────────────────────────────
        private static readonly Regex _yearRegex =
            new Regex(@"\b(\d{3,4})\b", RegexOptions.Compiled);

        // Strips leading "[Word]" category prefix from chronicle entry text. The auto-journal
        // handlers add prefixes like "[War] ", "[Family] ", "[Crime] " for human readability,
        // but the V3 panel already shows the category via tag pills — the bracket is noise.
        private static readonly Regex _bracketPrefixRegex =
            new Regex(@"^\[\w+\]\s*", RegexOptions.Compiled);

        private static readonly Regex _dayOfSeasonYearRegex =
            new Regex(@"^DAY\s+(\d+)\s+OF\s+(\w+),\s+(\d+)$", RegexOptions.Compiled);

        public static int ExtractYear(string dateString)
        {
            if (string.IsNullOrEmpty(dateString)) return 0;
            var m = _yearRegex.Match(dateString);
            if (m.Success && int.TryParse(m.Value, out int y))
                return y;
            return 0;
        }

        // ─────────────────────────────────────────────────────────────
        //  Entry-id stable hash (date+entity+first 24 chars of text)
        //  Chronicle data has no per-entry id today; we synthesize one
        //  that's stable across save/reload for bookmark persistence.
        // ─────────────────────────────────────────────────────────────
        public static string MakeStableId(string entityId, string date, string text)
        {
            string e = entityId ?? string.Empty;
            string d = date ?? string.Empty;
            string t = (text ?? string.Empty);
            if (t.Length > 24) t = t.Substring(0, 24);
            return e + "|" + d + "|" + t;
        }

        // Build a comparable full-date key from year + season name + day-of-season.
        // Season order: Spring < Summer < Autumn(Fall) < Winter. Unknown -> 0.
        private static long ComputeSortDateKey(int year, string seasonUpper, string dayDigits)
        {
            int seasonOrd = 0;
            if (!string.IsNullOrEmpty(seasonUpper))
            {
                switch (seasonUpper.Trim().ToUpperInvariant())
                {
                    case "SPRING": seasonOrd = 1; break;
                    case "SUMMER": seasonOrd = 2; break;
                    case "AUTUMN": case "FALL": seasonOrd = 3; break;
                    case "WINTER": seasonOrd = 4; break;
                }
            }
            int day = 0; int.TryParse(dayDigits, out day);
            return (long)year * 100000L + (long)seasonOrd * 1000L + (long)day;
        }

        private static string TruncateForTitle(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int end = -1;
            for (int i = 0; i < text.Length && i < 65; i++)
            {
                char c = text[i];
                if (c == '.' || c == '!' || c == '?') { end = i + 1; break; }
            }
            if (end >= 0) return text.Substring(0, end).TrimEnd();

            const int limit = 65;
            if (text.Length <= limit) return text;
            int cut = text.LastIndexOf(' ', System.Math.Min(limit, text.Length - 1));
            if (cut < 45) cut = limit;             // no reasonable space -> hard cut
            return text.Substring(0, cut).TrimEnd() + "...";
        }

        // ─────────────────────────────────────────────────────────────
        //  Event-tag classification (authored prefix — authoritative)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the leading "[Word]" category label that EE's auto-journal handlers write and maps
        /// it straight to an EventTag. The handler that wrote the entry knew what kind of event it
        /// was — a battle handler writes "[War]", a marriage handler writes "[Family]" — so this
        /// beats any keyword guess made after the fact.
        ///
        /// MUST be called on the text BEFORE _bracketPrefixRegex strips the prefix for display.
        ///
        /// Returns null (no category) when there is no bracket prefix at all, or when the word
        /// inside the brackets is not one of the five known categories — e.g. "[Edited]". The
        /// caller then falls back to ClassifyEventTags. Deliberately does NOT invent a mapping for
        /// unknown words: guessing that "[Battle]" means War would re-introduce the same class of
        /// error this method exists to remove.
        /// </summary>
        public static EventTag? ExtractPrefixEventTag(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            // Reuse the same pattern the stripper uses, so "what gets recognised" and "what gets
            // removed" can never drift apart.
            var m = _bracketPrefixRegex.Match(text);
            if (!m.Success) return null;

            // m.Value is "[Word]" plus trailing whitespace — lift the word out of the brackets.
            string token = m.Value.Trim();
            int close = token.IndexOf(']');
            if (close < 2) return null;                       // "[]" or malformed
            string word = token.Substring(1, close - 1);

            // Case-insensitive: EE writes both "[War]" and "[war]" in different handlers.
            if (string.Equals(word, "War", StringComparison.OrdinalIgnoreCase))      return EventTag.War;
            if (string.Equals(word, "Crime", StringComparison.OrdinalIgnoreCase))    return EventTag.Crime;
            if (string.Equals(word, "Politics", StringComparison.OrdinalIgnoreCase)) return EventTag.Politics;
            if (string.Equals(word, "Family", StringComparison.OrdinalIgnoreCase))   return EventTag.Family;
            if (string.Equals(word, "Other", StringComparison.OrdinalIgnoreCase))    return EventTag.Other;

            return null;                                      // unrecognised -> fall through to keywords
        }

        // ─────────────────────────────────────────────────────────────
        //  Event-tag classification (keyword sniffer — fallback only)
        // ─────────────────────────────────────────────────────────────
        public static EventTag ClassifyEventTags(string text)
        {
            if (string.IsNullOrEmpty(text)) return EventTag.Other;
            string t = text.ToLowerInvariant();
            EventTag tags = EventTag.None;

            if (Contains(t, "war", "battle", "siege", "attacked", "raid",
                          "skirmish", "assault", "killed in", "fell in battle"))
                tags |= EventTag.War;

            if (Contains(t, "bandit", "robbed", "murder", "crime",
                          "ambush", "prisoner", "executed"))
                tags |= EventTag.Crime;

            if (Contains(t, "throne", "kingdom", "council", "treaty", "alliance",
                          "declared peace", "declared war", "joined", "vassal",
                          "kingdom decision", "rebellion", "destroyed clan"))
                tags |= EventTag.Politics;

            if (Contains(t, "born", "married", "wedding", "died", "death",
                          "daughter", "son", "child", "wounded", "leveled",
                          "came of age", "marriage offered"))
                tags |= EventTag.Family;

            if (tags == EventTag.None) tags = EventTag.Other;
            return tags;
        }

        // ─────────────────────────────────────────────────────────────
        //  Entity-tag classification
        //  Heuristic order: text keywords first, then entity-id prefix.
        //  Both can contribute (a clan-changes-kingdom event tags as both).
        // ─────────────────────────────────────────────────────────────
        public static EntityTag ClassifyEntityTags(string entityId, string text)
        {
            EntityTag tags = EntityTag.None;
            string id = entityId ?? string.Empty;
            string t = (text ?? string.Empty).ToLowerInvariant();

            string idl = id.ToLowerInvariant();
            if (idl.StartsWith("lord_") || idl.StartsWith("ng_") || idl.StartsWith("ws_")
                || idl.StartsWith("companion_") || idl.Contains("hero")) tags |= EntityTag.Hero;
            if (idl.StartsWith("kingdom_") || idl.EndsWith("_kingdom")) tags |= EntityTag.Kingdom;
            if (idl.StartsWith("clan_") || idl.EndsWith("_clan")) tags |= EntityTag.Clan;
            if (idl.StartsWith("town_") || idl.StartsWith("castle_") || idl.StartsWith("village_")
                || idl.StartsWith("hideout_") || idl.StartsWith("settlement_")) tags |= EntityTag.Settlement;

            if (Contains(t, "kingdom", "throne", "realm")) tags |= EntityTag.Kingdom;
            if (Contains(t, "clan ")) tags |= EntityTag.Clan;
            if (Contains(t, "village", "town", "city", "castle", "settlement", "hideout"))
                tags |= EntityTag.Settlement;

            if (tags == EntityTag.None) tags = EntityTag.Other;
            return tags;
        }

        private static bool Contains(string haystack, params string[] needles)
        {
            for (int i = 0; i < needles.Length; i++)
                if (haystack.IndexOf(needles[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // ─────────────────────────────────────────────────────────────
        //  Source-entity resolution
        //  Walks the four entity collections by StringId; first hit wins.
        //  Heroes are checked first because the chronicle is dominated by
        //  hero-life events (born/married/died/wounded/etc).
        // ─────────────────────────────────────────────────────────────
        private static void ResolveSourceEntity(ChronicleEntryVM vm, string entityId)
        {
            if (vm == null || string.IsNullOrEmpty(entityId)) return;

            // Hero (most common)
            try
            {
                var hero = Hero.AllAliveHeroes != null
                    ? Hero.AllAliveHeroes.FirstOrDefault(h => h != null && h.StringId == entityId)
                    : null;
                if (hero == null && Hero.DeadOrDisabledHeroes != null)
                    hero = Hero.DeadOrDisabledHeroes.FirstOrDefault(h => h != null && h.StringId == entityId);
                if (hero != null)
                {
                    vm.SourceKindLabel = "HERO";
                    vm.SourceEntityName = SafeName(hero.Name);
                    if (hero.Clan != null)
                    {
                        vm.SourceClanName = SafeName(hero.Clan.Name);
                        if (hero.Clan.Kingdom != null) vm.SourceKingdomName = SafeName(hero.Clan.Kingdom.Name);
                    }
                    if (hero.Culture != null) vm.SourceCultureName = SafeName(hero.Culture.Name);
                    vm.EncyclopediaLink = SafeLink(hero.EncyclopediaLink);
                    BuildHeroPortrait(vm, hero);
                    BuildBannerImage(vm, hero.Clan != null ? hero.Clan.Banner : null);
                    BuildInfoRows(vm);
                    // Extra stats — AGE / RENOWN / TIER (each isolated; empty on failure).
                    try { SetStat(vm, 1, "AGE", ((int)hero.Age).ToString()); } catch { }
                    try { if (hero.Clan != null) SetStat(vm, 2, "RENOWN", ((int)hero.Clan.Renown).ToString()); } catch { }
                    try { if (hero.Clan != null) SetStat(vm, 3, "TIER", hero.Clan.Tier.ToString()); } catch { }
                    return;
                }
            }
            catch { }

            // Clan
            try
            {
                var clan = Clan.All != null
                    ? Clan.All.FirstOrDefault(c => c != null && c.StringId == entityId)
                    : null;
                if (clan != null)
                {
                    vm.SourceKindLabel = "CLAN";
                    vm.SourceEntityName = SafeName(clan.Name);
                    vm.SourceClanName = SafeName(clan.Name);
                    if (clan.Kingdom != null) vm.SourceKingdomName = SafeName(clan.Kingdom.Name);
                    if (clan.Culture != null) vm.SourceCultureName = SafeName(clan.Culture.Name);
                    vm.EncyclopediaLink = SafeLink(clan.EncyclopediaLink);
                    if (clan.Leader != null) BuildHeroPortrait(vm, clan.Leader);
                    BuildBannerImage(vm, clan.Banner);
                    BuildInfoRows(vm);
                    // Extra stats — TIER / RENOWN / FIEFS (each isolated; empty on failure).
                    try { SetStat(vm, 1, "TIER", clan.Tier.ToString()); } catch { }
                    try { SetStat(vm, 2, "RENOWN", ((int)clan.Renown).ToString()); } catch { }
                    try { if (clan.Fiefs != null) SetStat(vm, 3, "FIEFS", clan.Fiefs.Count.ToString()); } catch { }
                    return;
                }
            }
            catch { }

            // Settlement
            try
            {
                var sett = Settlement.All != null
                    ? Settlement.All.FirstOrDefault(s => s != null && s.StringId == entityId)
                    : null;
                if (sett != null)
                {
                    vm.SourceKindLabel = sett.IsTown ? "TOWN"
                                       : sett.IsCastle ? "CASTLE"
                                       : sett.IsVillage ? "VILLAGE"
                                       : sett.IsHideout ? "HIDEOUT"
                                       : "SETTLEMENT";
                    vm.SourceEntityName = SafeName(sett.Name);
                    if (sett.OwnerClan != null)
                    {
                        vm.SourceClanName = SafeName(sett.OwnerClan.Name);
                        if (sett.OwnerClan.Kingdom != null) vm.SourceKingdomName = SafeName(sett.OwnerClan.Kingdom.Name);
                        BuildBannerImage(vm, sett.OwnerClan.Banner);
                    }
                    BuildInfoRows(vm);
                    if (sett.Culture != null) vm.SourceCultureName = SafeName(sett.Culture.Name);
                    vm.EncyclopediaLink = SafeLink(sett.EncyclopediaLink);
                    // Extra stats — TYPE / PROSPERITY / OWNER (each isolated; empty on failure).
                    // 1.3.x has no Settlement.Town property; reach the Town via SettlementComponent.
                    try
                    {
                        SetStat(vm, 1, "TYPE", sett.IsTown ? "Town"
                                            : sett.IsCastle ? "Castle"
                                            : sett.IsVillage ? "Village"
                                            : sett.IsHideout ? "Hideout"
                                            : "Settlement");
                    }
                    catch { }
                    try
                    {
                        if (sett.IsTown)
                        {
                            var town = sett.SettlementComponent as Town;
                            if (town != null) SetStat(vm, 2, "PROSPERITY", ((int)town.Prosperity).ToString());
                        }
                    }
                    catch { }
                    try { if (sett.OwnerClan != null) SetStat(vm, 3, "OWNER", SafeName(sett.OwnerClan.Name)); } catch { }
                    return;
                }
            }
            catch { }

            // Kingdom
            try
            {
                var kg = Kingdom.All != null
                    ? Kingdom.All.FirstOrDefault(k => k != null && k.StringId == entityId)
                    : null;
                if (kg != null)
                {
                    vm.SourceKindLabel = "KINGDOM";
                    vm.SourceEntityName = SafeName(kg.Name);
                    vm.SourceKingdomName = SafeName(kg.Name);
                    if (kg.Culture != null) vm.SourceCultureName = SafeName(kg.Culture.Name);
                    vm.EncyclopediaLink = SafeLink(kg.EncyclopediaLink);
                    // Kingdom banner: prefer the ruling clan's banner since Kingdom doesn't carry a Banner directly
                    if (kg.RulingClan != null) BuildBannerImage(vm, kg.RulingClan.Banner);
                    BuildInfoRows(vm);
                    // Extra stats — CLANS / FIEFS / STRENGTH (each isolated; empty on failure).
                    // 1.3.x exposes CurrentTotalStrength (no TotalStrength member).
                    try { if (kg.Clans != null) SetStat(vm, 1, "CLANS", kg.Clans.Count.ToString()); } catch { }
                    try { if (kg.Fiefs != null) SetStat(vm, 2, "FIEFS", kg.Fiefs.Count.ToString()); } catch { }
                    try { SetStat(vm, 3, "STRENGTH", ((int)kg.CurrentTotalStrength).ToString()); } catch { }
                    return;
                }
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        //  INFO grid
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Fill the INFO grid from EE's shared, already-fog-masked stat set. Do NOT re-derive these
        /// values and do NOT re-apply the mask — EditableEncyclopediaAPI owns both. Assigning a fresh
        /// list (rather than mutating) is what raises HasInfoRows.
        /// </summary>
        private static void BuildInfoRows(ChronicleEntryVM vm)
        {
            if (vm == null) return;
            try
            {
                string kind = (vm.SourceKindLabel ?? string.Empty).Trim();
                string id = vm.SourceEntityId ?? string.Empty;
                if (kind.Length == 0 || id.Length == 0) return;

                // Ordered variants only — the Dictionary overloads guarantee no row order.
                List<KeyValuePair<string, string>> stats = null;

                if (string.Equals(kind, "HERO", StringComparison.OrdinalIgnoreCase))
                {
                    stats = EditableEncyclopediaApi.GetHeroInfoStatsOrdered(id);
                }
                else if (string.Equals(kind, "CLAN", StringComparison.OrdinalIgnoreCase))
                {
                    // No string-id overload exists for clans — resolve the object the same way
                    // ResolveSourceEntity does (StringId scan over Clan.All).
                    var clan = Clan.All != null
                        ? Clan.All.FirstOrDefault(c => c != null && c.StringId == id)
                        : null;
                    if (clan != null) stats = EditableEncyclopediaApi.GetClanInfoStatsOrdered(clan);
                }
                else if (string.Equals(kind, "KINGDOM", StringComparison.OrdinalIgnoreCase))
                {
                    // Same: kingdom stats take the object, so resolve it by StringId first.
                    var kingdom = Kingdom.All != null
                        ? Kingdom.All.FirstOrDefault(k => k != null && k.StringId == id)
                        : null;
                    if (kingdom != null) stats = EditableEncyclopediaApi.GetKingdomInfoStatsOrdered(kingdom);
                }
                else if (IsSettlementKind(kind))
                {
                    stats = EditableEncyclopediaApi.GetSettlementInfoStatsOrdered(id);
                }
                // Anything unrecognised: stats stays null and InfoRows is left empty.

                if (stats == null || stats.Count == 0) return;

                var rows = new MBBindingList<ChronicleInfoRowVM>();
                foreach (var kv in stats)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    rows.Add(new ChronicleInfoRowVM(kv.Key, kv.Value ?? string.Empty));
                }
                // ASSIGN, never .Add onto the existing list — HasInfoRows only fires on a
                // reference change, so mutating in place would leave the UI block hidden.
                vm.InfoRows = rows;
            }
            catch { }
        }

        /// <summary>
        /// True for every SourceKindLabel ResolveSourceEntity assigns in the Settlement branch.
        /// All five route through GetSettlementInfoStatsOrdered(settlementId).
        /// </summary>
        private static bool IsSettlementKind(string kind)
        {
            return string.Equals(kind, "TOWN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "CASTLE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "VILLAGE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "HIDEOUT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "SETTLEMENT", StringComparison.OrdinalIgnoreCase);
        }

        // ─────────────────────────────────────────────────────────────
        //  Cross-reference lists (MORE FROM / ALSO IN)
        // ─────────────────────────────────────────────────────────────

        /// <summary>Max rows per cross-reference list. A long-lived lord would otherwise
        /// produce a pane hundreds of rows deep.</summary>
        private const int MaxRefRows = 8;

        /// <summary>
        /// One grouped pass: bucket every entry by source entity and by year label, then hand each
        /// entry the (capped) siblings from its own two buckets. O(n), not O(n²) — per-entry
        /// scanning of the full list would quadratically blow up on a long campaign.
        /// </summary>
        private static void BuildCrossReferences(List<ChronicleEntryVM> all, Action<string> onSelect)
        {
            if (all == null || all.Count == 0) return;
            try
            {
                // Keyed on EntryYearLabel (string), NOT EntryYear (int) — a Dictionary<string,...>
                // cannot be keyed on an int.
                var byEntity = new Dictionary<string, List<ChronicleEntryVM>>(StringComparer.OrdinalIgnoreCase);
                var byYear = new Dictionary<string, List<ChronicleEntryVM>>(StringComparer.OrdinalIgnoreCase);

                // Pass 1 — bucket.
                foreach (var e in all)
                {
                    if (e == null) continue;
                    AddToBucket(byEntity, e.SourceEntityId, e);
                    AddToBucket(byYear, e.EntryYearLabel, e);
                }

                // Pass 2 — assign.
                foreach (var e in all)
                {
                    if (e == null) continue;

                    List<ChronicleEntryVM> bucket;
                    var related = new MBBindingList<ChronicleRefRowVM>();
                    if (!string.IsNullOrEmpty(e.SourceEntityId) && byEntity.TryGetValue(e.SourceEntityId, out bucket))
                        FillRefRows(related, bucket, e, onSelect);
                    e.RelatedEntries = related;

                    var sameYear = new MBBindingList<ChronicleRefRowVM>();
                    if (!string.IsNullOrEmpty(e.EntryYearLabel) && byYear.TryGetValue(e.EntryYearLabel, out bucket))
                        FillRefRows(sameYear, bucket, e, onSelect);
                    e.SameYearEntries = sameYear;
                }
            }
            catch { }
        }

        private static void AddToBucket(Dictionary<string, List<ChronicleEntryVM>> map, string key, ChronicleEntryVM e)
        {
            if (string.IsNullOrEmpty(key)) return;
            List<ChronicleEntryVM> bucket;
            if (!map.TryGetValue(key, out bucket))
            {
                bucket = new List<ChronicleEntryVM>();
                map[key] = bucket;
            }
            bucket.Add(e);
        }

        /// <summary>
        /// Copies up to MaxRefRows siblings out of a bucket, excluding <paramref name="self"/> by
        /// reference AND by EntryId (duplicate chronicle lines can synthesize the same stable id,
        /// and an entry must never link to itself).
        /// </summary>
        private static void FillRefRows(
            MBBindingList<ChronicleRefRowVM> target,
            List<ChronicleEntryVM> bucket,
            ChronicleEntryVM self,
            Action<string> onSelect)
        {
            for (int i = 0; i < bucket.Count && target.Count < MaxRefRows; i++)
            {
                var other = bucket[i];
                if (other == null) continue;
                if (ReferenceEquals(other, self)) continue;
                if (!string.IsNullOrEmpty(self.EntryId)
                    && string.Equals(other.EntryId, self.EntryId, StringComparison.Ordinal)) continue;
                target.Add(new ChronicleRefRowVM(other.EntryId, other.TitleText, other.EntryYearLabel, onSelect));
            }
        }

        // Build a CharacterImageIdentifierVM for a hero's portrait. Bannerlord moved the type
        // across namespaces (TaleWorlds.Core.ViewModelCollection.ImageIdentifiers vs the older
        // TaleWorlds.Core.ImageIdentifiers), so we use namespace-resilient reflection — same
        // recipe BandIt Plus's CampInfoPanelMissionView.TryCreateChiefPortrait proved out.
        // The XML binds DataSource="{HeroPortrait}" on an ImageIdentifierWidget.
        private static Type _charImgIdVMType;
        private static Type _charCodeType;
        private static MethodInfo _charCodeCreateFrom;
        private static ConstructorInfo _charImgIdVMCtor;
        private static bool _portraitReflectionResolved;

        private static void BuildHeroPortrait(ChronicleEntryVM vm, Hero hero)
        {
            if (vm == null || hero == null || hero.CharacterObject == null) return;
            EnsurePortraitReflection();
            if (_charImgIdVMCtor == null || _charCodeCreateFrom == null) return;
            try
            {
                var code = _charCodeCreateFrom.Invoke(null, new object[] { hero.CharacterObject });
                if (code == null) return;
                vm.HeroPortrait = _charImgIdVMCtor.Invoke(new[] { code });
            }
            catch { }
        }

        private static void EnsurePortraitReflection()
        {
            if (_portraitReflectionResolved) return;
            _portraitReflectionResolved = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    _charCodeType = _charCodeType ?? asm.GetType("TaleWorlds.Core.CharacterCode");
                    _charImgIdVMType = _charImgIdVMType
                        ?? asm.GetType("TaleWorlds.Core.ViewModelCollection.ImageIdentifiers.CharacterImageIdentifierVM")
                        ?? asm.GetType("TaleWorlds.Core.ImageIdentifiers.CharacterImageIdentifierVM");
                    if (_charCodeType != null && _charImgIdVMType != null) break;
                }
                if (_charCodeType != null)
                    _charCodeCreateFrom = _charCodeType.GetMethod(
                        "CreateFrom",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(BasicCharacterObject) },
                        null);
                if (_charImgIdVMType != null && _charCodeType != null)
                    _charImgIdVMCtor = _charImgIdVMType.GetConstructor(new[] { _charCodeType });
            }
            catch { }
        }

        // 2026-08-02: BannerCodeText alone never rendered anything. The old comment claimed a
        // `BannerVisualWidget BannerCode="..."` would consume it, but that widget has ZERO usages
        // in the shipped game and no such declarative path exists. The working route — verified
        // against 1.4.7 — is a BannerImageIdentifierVM bound to an ImageIdentifierWidget, exactly
        // like the hero portrait. The old ImageTypeCode enum is gone entirely; the image type IS
        // the ImageIdentifierVM subclass. Serialize() is still set for backwards compatibility.
        private static void BuildBannerImage(ChronicleEntryVM vm, Banner banner)
        {
            if (vm == null || banner == null) return;
            try
            {
                vm.BannerCodeText = banner.Serialize() ?? string.Empty;
            }
            catch { }
            BuildClanBanner(vm, banner);
        }

        // Namespace-resilient like EnsurePortraitReflection — the ImageIdentifiers namespace has
        // moved across patches, so never bind the type at compile time.
        private static Type _bannerImgIdVMType;
        private static ConstructorInfo _bannerImgIdVMCtor;
        private static bool _bannerReflectionResolved;

        private static void BuildClanBanner(ChronicleEntryVM vm, Banner banner)
        {
            if (vm == null || banner == null) return;
            EnsureBannerReflection();
            if (_bannerImgIdVMCtor == null) return;
            try
            {
                // ctor(Banner banner, bool nineGrid) — nineGrid:false renders the banner flat,
                // which is what a small inline row image wants.
                vm.ClanBanner = _bannerImgIdVMCtor.Invoke(new object[] { banner, false });
            }
            catch { }
        }

        private static void EnsureBannerReflection()
        {
            if (_bannerReflectionResolved) return;
            _bannerReflectionResolved = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    _bannerImgIdVMType = _bannerImgIdVMType
                        ?? asm.GetType("TaleWorlds.Core.ViewModelCollection.ImageIdentifiers.BannerImageIdentifierVM")
                        ?? asm.GetType("TaleWorlds.Core.ImageIdentifiers.BannerImageIdentifierVM");
                    if (_bannerImgIdVMType != null) break;
                }
                if (_bannerImgIdVMType != null)
                    _bannerImgIdVMCtor = _bannerImgIdVMType.GetConstructor(new[] { typeof(Banner), typeof(bool) });
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        //  SPOILS (gold + looted items) — 2026-08-02
        // ─────────────────────────────────────────────────────────────
        // The capture/persistence half lives in the sibling EditableEncyclopedia module
        // (ChronicleSpoilsCollector.cs harvests the engine's own battle/raid numbers;
        // EncyclopediaEditBehavior._chronicleSpoils saves them). Everything below only READS
        // that data through EditableEncyclopediaAPI and turns it into bindable VM state.

        /// <summary>
        /// How many item stacks the SPOILS strip draws. 12 fills exactly two rows of the
        /// prefab's 6-column grid; stacks past that are folded into the "+N more stacks"
        /// remainder line together with the stacks EE's own storage cap already dropped.
        /// </summary>
        private const int MaxRenderedSpoilStacks = 12;

        private static void BuildSpoils(ChronicleEntryVM vm, string entityId, string date, string rawText)
        {
            if (vm == null) return;

            ChronicleSpoils spoils;
            try
            {
                // 3-arg overload: it builds the key itself via MakeChronicleEntryKey, which is
                // byte-for-byte MakeStableId above. Never returns null; an unknown key yields an
                // empty ChronicleSpoils, so "no record" and "empty record" collapse to the same
                // harmless no-op below.
                spoils = EditableEncyclopediaApi.GetChronicleSpoils(entityId, date, rawText);
            }
            catch { return; }

            // Gold 0 AND no items => no spoils block at all (ChronicleSpoils.IsEmpty). Leaving the
            // VM's three backing values at their empty defaults keeps HasSpoils false.
            if (spoils == null || spoils.IsEmpty) return;

            if (spoils.Gold != 0) vm.SpoilsGoldLabel = FormatDenars(spoils.Gold);

            // Build a FRESH list and ASSIGN it. Mutating vm.SpoilsItems in place would never fire
            // the setter, so HasSpoilItems would stay stale and the strip would stay hidden.
            var rows = new MBBindingList<ChronicleSpoilItemVM>();
            int overflow = 0;
            if (spoils.Items != null)
            {
                foreach (var stack in spoils.Items)
                {
                    if (stack == null || string.IsNullOrEmpty(stack.ItemId)) continue;
                    if (rows.Count >= MaxRenderedSpoilStacks) { overflow++; continue; }
                    var row = BuildSpoilItemRow(stack);
                    if (row == null) continue;   // unresolvable item id — skip, never a blank tile
                    rows.Add(row);
                }
            }
            vm.SpoilsItems = rows;

            int more = spoils.OmittedItemStacks + overflow;
            if (more > 0)
            {
                vm.SpoilsMoreLabel = "+"
                    + more.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + (more == 1 ? " more stack" : " more stacks");
            }
        }

        /// <summary>
        /// One spoils row, or null when the item StringId no longer resolves (its mod was
        /// removed, or the item was renamed between saves). Returning null makes the caller SKIP
        /// the stack outright, which is deliberate: a tile with no resolvable item would render
        /// as a blank square with no error and no log line.
        /// </summary>
        private static ChronicleSpoilItemVM BuildSpoilItemRow(ChronicleSpoilItem stack)
        {
            if (stack == null || string.IsNullOrEmpty(stack.ItemId)) return null;

            ItemObject item = null;
            try
            {
                var om = TaleWorlds.ObjectSystem.MBObjectManager.Instance;
                if (om != null) item = om.GetObject<ItemObject>(stack.ItemId);
            }
            catch { item = null; }
            if (item == null) return null;

            string name = string.Empty;
            try { name = SafeName(item.Name); } catch { }

            // The resolved ItemObject rides along so the row's hover tooltip can read its stats
            // without a second MBObjectManager lookup — this method has already proved the id
            // resolves, and a repeat lookup could only fail in ways ruled out three lines above.
            return new ChronicleSpoilItemVM(BuildItemImage(item), stack.Count, name, item);
        }

        /// <summary>"1,240 denars", or "90 denars lost" when the entry's hero came out behind.</summary>
        private static string FormatDenars(int gold)
        {
            long abs = gold < 0 ? -(long)gold : gold;
            string n = abs.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            return gold < 0 ? n + " denars lost" : n + " denars";
        }

        // ItemImageIdentifierVM is a real sibling of the CharacterImageIdentifierVM and
        // BannerImageIdentifierVM used above — verified in this install by reflecting
        // bin\Win64_Shipping_Client\TaleWorlds.Core.ViewModelCollection.dll:
        //   TaleWorlds.Core.ViewModelCollection.ImageIdentifiers.ItemImageIdentifierVM
        //     : ImageIdentifierVM
        //     .ctor(TaleWorlds.Core.ItemObject itemObject, System.String bannerCode)
        // The ctor stores both args, news up ItemImageIdentifier(ItemObject, string) and assigns
        // base ImageIdentifierVM.ImageIdentifier — it never dereferences bannerCode, so passing
        // string.Empty (this is ordinary loot, not a custom player banner item) is safe.
        // Resolved by reflection for the same reason as the two above: the ImageIdentifiers
        // namespace has moved between patches, so it is never bound at compile time.
        private static Type _itemImgIdVMType;
        private static ConstructorInfo _itemImgIdVMCtor;
        private static bool _itemImageReflectionResolved;

        private static object BuildItemImage(ItemObject item)
        {
            if (item == null) return null;
            EnsureItemImageReflection();
            if (_itemImgIdVMCtor == null) return null;
            try { return _itemImgIdVMCtor.Invoke(new object[] { item, string.Empty }); }
            catch { return null; }
        }

        private static void EnsureItemImageReflection()
        {
            if (_itemImageReflectionResolved) return;
            _itemImageReflectionResolved = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    _itemImgIdVMType = _itemImgIdVMType
                        ?? asm.GetType("TaleWorlds.Core.ViewModelCollection.ImageIdentifiers.ItemImageIdentifierVM")
                        ?? asm.GetType("TaleWorlds.Core.ImageIdentifiers.ItemImageIdentifierVM");
                    if (_itemImgIdVMType != null) break;
                }
                if (_itemImgIdVMType != null)
                    _itemImgIdVMCtor = _itemImgIdVMType.GetConstructor(new[] { typeof(ItemObject), typeof(string) });
            }
            catch { }
        }

        private static string SafeName(TaleWorlds.Localization.TextObject t)
        {
            try { return t == null ? string.Empty : t.ToString() ?? string.Empty; }
            catch { return string.Empty; }
        }

        // entity.EncyclopediaLink can be null for engine-only or freshly-created objects.
        private static string SafeLink(string link) { return link ?? string.Empty; }

        // Set an extra-stat slot (1/2/3): writes Label + Value together, but only when the
        // value is non-empty. Caller wraps each computation in its own try/catch so a missing
        // member or null reference just leaves the slot empty (cell hides via HasExtraStatN).
        private static void SetStat(ChronicleEntryVM vm, int slot, string label, string value)
        {
            if (vm == null || string.IsNullOrEmpty(value) || string.IsNullOrEmpty(label)) return;
            switch (slot)
            {
                case 1: vm.ExtraStat1Label = label; vm.ExtraStat1Value = value; break;
                case 2: vm.ExtraStat2Label = label; vm.ExtraStat2Value = value; break;
                case 3: vm.ExtraStat3Label = label; vm.ExtraStat3Value = value; break;
            }
        }
    }
}
