using System.Collections.Generic;

namespace BandItPlus
{
    public static class BiomeEligibility
    {
        private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Map =
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["forest_bandits"]   = new[] { "bp_marsh_stalkers", "bp_pagan_cult" },
                ["mountain_bandits"] = new[] { "bp_frost_reavers",  "bp_sky_raiders" },
                ["steppe_bandits"]   = new[] { "bp_steppe_wolves" },
                ["sea_raiders"]      = new string[0],
                ["desert_bandits"]   = new[] { "bp_slaver_caravans", "bp_sky_raiders" },
                ["looters"]          = new[] { "bp_highwaymen", "bp_fallen_legionaries" },
            };

        public static IReadOnlyList<string> GetReplacements(string vanillaCultureId)
        {
            return Map.TryGetValue(vanillaCultureId, out var list) ? list : new string[0];
        }

        public static IReadOnlyList<string> RoadAdjacentClans { get; } =
            new[] { "bp_highwaymen", "bp_fallen_legionaries" };
    }
}
