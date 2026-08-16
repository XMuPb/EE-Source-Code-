using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace BandItPlus.Patches
{
    // Wave 4.12-fix v69 (2026-05-12) — Tier 3 of the Contacts roadmap.
    //
    // What it does: after the player intro's with a bandit camp vendor's contact
    // (T3 trust + click "Will you introduce me to your contacts?"), towns of a
    // matching real-world culture give the player a small trade benefit:
    //   - Buy prices: -5% (player pays less)
    //   - Sell prices: +5% (player receives more)
    //
    // Mechanism: Harmony POSTFIX on Town.GetItemPrice. Vanilla computes the
    // price using supply/demand/prosperity, then this patch scales the final
    // number if the player has an active contact in a bandit culture mapped
    // to this town's real culture. Non-destructive — vanilla math runs in full,
    // we only modify the output.
    //
    // Mapping rationale: each of the 14 BandIt Plus cultures lives "near" a
    // real Calradian culture. forest_bandits / looters operate in Vlandian
    // territory; bp_frost_reavers / sea_raiders haunt Sturgian coasts; etc.
    // The contact line ("Aunt Lien at Pravend crossroads") already implies
    // the geographic association — this patch makes the world reflect it.
    [HarmonyPatch]
    public static class ContactSettlementDiscountPatch
    {
        private const float kBuyDiscount = 0.95f;   // player pays 95% of base
        private const float kSellBonus = 1.05f;     // player gets 105% of base

        // Bandit culture StringId -> real Bannerlord culture StringId.
        // Real cultures in 1.2.x: vlandia, sturgia, battania, khuzait, aserai, empire.
        private static readonly Dictionary<string, string> _banditToRealCulture
            = new Dictionary<string, string>
        {
            { "forest_bandits",         "vlandia"  },
            { "bp_frost_reavers",       "sturgia"  },
            { "bp_marsh_stalkers",      "battania" },
            { "bp_highwaymen",          "vlandia"  },
            { "bp_slaver_caravans",     "aserai"   },
            { "bp_fallen_legionaries",  "empire"   },
            { "bp_sky_raiders",         "battania" },
            { "bp_steppe_wolves",       "khuzait"  },
            { "bp_pagan_cult",          "battania" },
            { "looters",                "vlandia"  },
            { "mountain_bandits",       "aserai"   },
            { "steppe_bandits",         "khuzait"  },
            { "sea_raiders",            "sturgia"  },
            { "desert_bandits",         "aserai"   },
        };

        private static int _modifyCount = 0;
        private static DateTime _lastLog = DateTime.MinValue;

        public static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("TaleWorlds.CampaignSystem.Settlements.Town");
            if (type == null) return null;
            // Try (ItemObject, MobileParty, bool) signature first
            var m = AccessTools.Method(type, "GetItemPrice", new Type[] {
                AccessTools.TypeByName("TaleWorlds.Core.ItemObject"),
                AccessTools.TypeByName("TaleWorlds.CampaignSystem.Party.MobileParty"),
                typeof(bool)
            });
            if (m != null) return m;
            // Fall back: (EquipmentElement, MobileParty, bool)
            m = AccessTools.Method(type, "GetItemPrice", new Type[] {
                AccessTools.TypeByName("TaleWorlds.Core.EquipmentElement"),
                AccessTools.TypeByName("TaleWorlds.CampaignSystem.Party.MobileParty"),
                typeof(bool)
            });
            if (m != null) return m;
            // Last resort: first GetItemPrice we find
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.Name == "GetItemPrice") return method;
            }
            return null;
        }

        public static bool Prepare()
        {
            var found = TargetMethod() != null;
            try { File.AppendAllText(BandItPlus.Diagnostics.BpDiag.P("bp_peace_debug.log"),
                "[" + DateTime.Now.ToString("HH:mm:ss.fff")
                + "] ContactSettlementDiscount: Prepare: " + found
                + " (target=" + (TargetMethod()?.Name ?? "null") + ")"
                + Environment.NewLine); } catch { }
            return found;
        }

        // 2026-05-26 polish: per-settlement scaling + same-culture stacking with cap.
        //
        //   Per (cultureId, vendorKind) contact contribution:
        //     • introduced only (settlement assigned, not yet visited)          -> 2.5%
        //     • introduced + arrived AT THIS TOWN (the assigned settlement)     -> 5.0%
        //     • introduced + arrived but at OTHER towns of same real culture    -> 2.5%
        //
        //   Stacking: every active contact (food or gear) of every bandit
        //   culture mapped to this town's real culture contributes. Aserai
        //   (3 bandit cultures x 2 vendor kinds = 6 possible contacts) could
        //   uncapped reach +/-30% — kCapPct holds the ceiling at +/-10%.
        //
        //   Why "introduced" gets half before arrival: the T3 dialogue choice
        //   ("Will you introduce me to your contacts?") is the player's
        //   commitment; the arrival popup is the payoff. Giving the half-rate
        //   BEFORE arrival rewards the commitment, and going to the assigned
        //   settlement now demonstrably DOUBLES the local prices the player
        //   sees in that town — a concrete reason to actually make the ride.
        private const float kHalfPct = 0.025f;
        private const float kFullPct = 0.05f;
        private const float kCapPct  = 0.10f;

        // Postfix runs after vanilla price calc. __instance = Town being queried.
        // __result = the price vanilla computed (int). isSelling = direction.
        public static void Postfix(Town __instance, bool isSelling, ref int __result)
        {
            try
            {
                if (__instance == null || __result <= 0) return;
                if (MCMSettings.Instance == null || !MCMSettings.Instance.EnableMod) return;

                var townCultureId = __instance.Settlement?.Culture?.StringId;
                if (string.IsNullOrEmpty(townCultureId)) return;

                // Wave 5 alliance bypass (T34, 2026-05-31): if ANY bandit culture
                // mapped to this town's real culture has the player allied with its
                // chief, drive the price to 0 (engine "free" sentinel). Mirrors the
                // "allied chief => max discount, skip Trust ladder" rule. IsAllied
                // is null-safe (returns false on null/empty cultureId).
                try
                {
                    var alliance = BandItPlus.Behaviors.BanditAllianceBehavior.Instance;
                    if (alliance != null)
                    {
                        foreach (var kvp in _banditToRealCulture)
                        {
                            if (kvp.Value != townCultureId) continue;
                            if (alliance.IsAllied(kvp.Key))
                            {
                                __result = 0;
                                return;
                            }
                        }
                    }
                }
                catch (Exception allyEx)
                {
                    BandItPlus.Diagnostics.SaveExceptionTrap.Log(
                        "[BP-Alliance] ContactSettlementDiscountPatch allied-bypass threw "
                        + allyEx.GetType().Name + ": " + allyEx.Message);
                }

                var townId = __instance.Settlement?.StringId;
                if (string.IsNullOrEmpty(townId)) return;

                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                if (bdm == null) return;

                // Sum every active contact's contribution for this real culture.
                // 14 bandit cultures x 2 vendor kinds = at most 28 quick lookups —
                // hot path is safe.
                float total = 0f;
                foreach (var kvp in _banditToRealCulture)
                {
                    if (kvp.Value != townCultureId) continue;
                    string banditCid = kvp.Key;

                    string[] vendorKinds = { "food", "gear" };
                    foreach (var vk in vendorKinds)
                    {
                        string assigned = bdm.GetContactSettlement(banditCid, vk);
                        if (string.IsNullOrEmpty(assigned)) continue; // not introduced

                        // Default: half rate (introduced only, or arrived elsewhere)
                        float contribution = kHalfPct;
                        // Full rate ONLY if this is the contact's assigned town AND met
                        if (assigned == townId && bdm.HasContactSettlementMet(banditCid, vk))
                            contribution = kFullPct;

                        total += contribution;
                    }
                }
                if (total <= 0f) return;
                if (total > kCapPct) total = kCapPct;

                // Apply: buy = (1 - total), sell = (1 + total). Clamp to >= 1.
                float scale = isSelling ? (1f + total) : (1f - total);
                int scaled = (int)Math.Round(__result * scale);
                if (scaled < 1) scaled = 1;
                __result = scaled;

                _modifyCount++;
                if (MCMSettings.Instance.DebugLogging && (DateTime.Now - _lastLog).TotalSeconds > 5)
                {
                    _lastLog = DateTime.Now;
                    try { File.AppendAllText(BandItPlus.Diagnostics.BpDiag.P("bp_peace_debug.log"),
                        "[" + DateTime.Now.ToString("HH:mm:ss.fff")
                        + "] ContactSettlementDiscount: applied count=" + _modifyCount
                        + " townCulture=" + townCultureId
                        + " isSelling=" + isSelling
                        + Environment.NewLine); } catch { }
                }
            }
            catch { /* silent - pricing is hot path; never throw */ }
        }
    }
}
