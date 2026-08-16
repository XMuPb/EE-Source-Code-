 using System.Collections.Generic;

namespace BandItPlus
{
    public enum LootTier { Unknown, Common, Elite }

    public static class ClanTierClassifier
    {
        private static readonly HashSet<string> EliteClans = new HashSet<string>
        {
            "bp_frost_reavers",
            "bp_slaver_caravans",
            "bp_fallen_legionaries",
            "bp_steppe_wolves",
        };

        private static readonly HashSet<string> CommonClans = new HashSet<string>
        {
            "bp_highwaymen",
            "bp_marsh_stalkers",
            "bp_sky_raiders",
            "bp_pagan_cult",
        };

        public static LootTier Classify(string clanId)
        {
            if (EliteClans.Contains(clanId)) return LootTier.Elite;
            if (CommonClans.Contains(clanId)) return LootTier.Common;
            return LootTier.Unknown;
        }

        public static double GetCoinMultiplier(LootTier tier) => tier switch
        {
            LootTier.Elite => 1.40,
            LootTier.Common => 1.15,
            _ => 1.0,
        };
    }
}
