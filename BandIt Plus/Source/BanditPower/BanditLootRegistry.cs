using System.Collections.Generic;

namespace BandItPlus.BanditPower
{
    // v168: per-culture loot data for the Bandit Battle Spoils system. One
    // entry per all 14 bandit cultures (8 bp_ custom clans + 6 vanilla).
    // Pure data — BanditSpoils reads it. Mirrors CultureProfileRegistry's
    // "all per-culture content in one table" pattern.
    public sealed class BanditLootEntry
    {
        public string CultureId;
        public string DisplayName;       // popup text, e.g. "Mountain Bandit"
        public string KeepsakeItemId;    // custom Goods item
        public double KeepsakeBaseChance; // 0.06 .. 0.10
        public bool IsEliteClan;         // true = bp_ clan -> UseEliteBonus applies
        public string[] GearPool;        // curated vanilla item StringIds
    }

    public static class BanditLootRegistry
    {
        private static readonly Dictionary<string, BanditLootEntry> Map =
            new Dictionary<string, BanditLootEntry>();

        static BanditLootRegistry()
        {
            // --- 8 bp_ custom clans (elite) ---
            Add("bp_frost_reavers", BandItPlus.Localization.Get("bp_lootregistry_001", "Frost Reaver"), "bp_frost_touched_talisman", 0.08, true,
                new[] { "throwing_axe", "highland_throwing_axe", "battle_axe", "round_shield",
                        "fur_coat", "leather_gloves" });
            Add("bp_marsh_stalkers", BandItPlus.Localization.Get("bp_lootregistry_002", "Marsh Stalker"), "bp_bog_witch_charm", 0.08, true,
                new[] { "hunting_bow", "bound_crude_bow", "barbed_javelin", "wooden_shield",
                        "leather_cap", "padded_cloth" });
            Add("bp_highwaymen", BandItPlus.Localization.Get("bp_lootregistry_003", "Highwayman"), "bp_stolen_signet_ring", 0.10, true,
                new[] { "billhook", "hatchet", "throwing_knives", "horsemans_kite_shield",
                        "leather_vest", "sturdy_horse" });
            Add("bp_slaver_caravans", BandItPlus.Localization.Get("bp_lootregistry_004", "Slaver"), "bp_slavers_iron_key", 0.08, true,
                new[] { "whip", "scimitar", "javelin", "leather_lamellar_armor",
                        "desert_round_shield", "desert_horse" });
            Add("bp_fallen_legionaries", BandItPlus.Localization.Get("bp_lootregistry_005", "Fallen Legionary"), "bp_tarnished_imperial_sigil", 0.06, true,
                new[] { "imperial_short_sword", "menavlion", "pilum", "imperial_shield",
                        "imperial_mail_armor", "imperial_horse" });
            Add("bp_sky_raiders", BandItPlus.Localization.Get("bp_lootregistry_006", "Sky Raider"), "bp_falconers_gauntlet", 0.09, true,
                new[] { "noble_bow", "war_bow", "throwing_axe", "leather_gloves",
                        "padded_leather", "hunting_bow" });
            Add("bp_steppe_wolves", BandItPlus.Localization.Get("bp_lootregistry_007", "Steppe Wolf"), "bp_wolf_tooth_necklace", 0.08, true,
                new[] { "nomad_bow", "lance", "nomad_sabre", "leather_lamellar_armor",
                        "steppe_horse", "leather_cap" });
            Add("bp_pagan_cult", BandItPlus.Localization.Get("bp_lootregistry_008", "Pagan Cultist"), "bp_druids_bone_idol", 0.07, true,
                new[] { "cleaver", "wooden_club", "throwing_stone", "wooden_shield",
                        "tribal_robe", "leather_apron" });

            // --- 6 vanilla bandit cultures (non-elite) ---
            Add("looters", BandItPlus.Localization.Get("bp_lootregistry_009", "Looter"), "bp_keepsake_looters", 0.06, false,
                new[] { "throwing_stone", "wooden_club", "sickle", "hatchet",
                        "rough_tied_boots", "leather_gloves" });
            Add("forest_bandits", BandItPlus.Localization.Get("bp_lootregistry_010", "Forest Bandit"), "bp_keepsake_forest_bandits", 0.07, false,
                new[] { "hunting_bow", "barbed_javelin", "billhook", "wooden_shield",
                        "leather_cap", "padded_cloth" });
            Add("mountain_bandits", BandItPlus.Localization.Get("bp_lootregistry_011", "Mountain Bandit"), "bp_keepsake_mountain_bandits", 0.07, false,
                new[] { "battle_axe", "throwing_axe", "highland_billhook", "round_shield",
                        "fur_coat", "leather_gloves" });
            Add("steppe_bandits", BandItPlus.Localization.Get("bp_lootregistry_012", "Steppe Bandit"), "bp_keepsake_steppe_bandits", 0.07, false,
                new[] { "nomad_bow", "nomad_sabre", "lance", "leather_lamellar_armor",
                        "steppe_horse", "leather_cap" });
            Add("desert_bandits", BandItPlus.Localization.Get("bp_lootregistry_013", "Desert Bandit"), "bp_keepsake_desert_bandits", 0.07, false,
                new[] { "scimitar", "javelin", "desert_round_shield", "leather_lamellar_armor",
                        "desert_horse", "leather_gloves" });
            Add("sea_raiders", BandItPlus.Localization.Get("bp_lootregistry_014", "Sea Raider"), "bp_keepsake_sea_raiders", 0.08, false,
                new[] { "battle_axe", "throwing_axe", "two_handed_axe", "round_shield",
                        "fur_coat", "leather_cap" });
        }

        private static void Add(string id, string display, string keepsake,
                                double chance, bool elite, string[] gear)
        {
            Map[id] = new BanditLootEntry
            {
                CultureId = id, DisplayName = display, KeepsakeItemId = keepsake,
                KeepsakeBaseChance = chance, IsEliteClan = elite, GearPool = gear
            };
        }

        // Null when cultureId is not a known bandit culture.
        public static BanditLootEntry Get(string cultureId)
            => cultureId != null && Map.TryGetValue(cultureId, out var e) ? e : null;

        public static bool IsBanditCulture(string cultureId)
            => cultureId != null && Map.ContainsKey(cultureId);
    }
}
