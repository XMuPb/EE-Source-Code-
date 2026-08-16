using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;

namespace BandItPlus.Patches
{
    [HarmonyPatch]
    public static class BanditSpawnPatch
    {
        private static readonly Random Rng = new Random();
        private const double BaseSubstitutionRate = 0.30;
        private static readonly string LogPath = BandItPlus.Diagnostics.BpDiag.P("bp_spawn_debug.log");
        private static MethodBase _resolvedTarget;

        // BUG M13 FIX (2026-04-30): track party identity (HashSet<MobileParty>) instead of a
        // count snapshot. The previous count-window approach assumed `MobileParty.All` is an
        // append-only list between Prefix and Postfix — but reentrant code (events, other
        // Harmony patches firing on the same thread) may add or REMOVE parties in that window.
        // Removal would push our spawned party DOWN the list, so iterating from _preSpawnCount
        // would miss it AND process unrelated parties. Set-difference is order-independent
        // and survives any concurrent mutations.
        [System.ThreadStatic]
        private static HashSet<MobileParty> _preSpawnParties;

        public static MethodBase TargetMethod()
        {
            if (_resolvedTarget != null) return _resolvedTarget;
            var type = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.BanditSpawnCampaignBehavior");
            if (type == null) { Log("TargetMethod: type not found"); return null; }

            // SpawnBanditParty(Clan) returns void in this Bannerlord version (1.2.x).
            var m1 = AccessTools.Method(type, "SpawnBanditParty");
            if (m1 != null)
            {
                _resolvedTarget = m1;
                Log("TargetMethod: matched SpawnBanditParty (returns " + m1.ReturnType.Name + ", " + m1.GetParameters().Length + " params)");
                return m1;
            }
            Log("TargetMethod: SpawnBanditParty not found");
            return null;
        }

        public static bool Prepare()
        {
            var found = TargetMethod() != null;
            Log("Prepare: " + found);
            return found;
        }

        // Prefix: snapshot the set of EXISTING parties so the Postfix can find newly added ones.
        public static void Prefix()
        {
            try
            {
                if (_preSpawnParties == null) _preSpawnParties = new HashSet<MobileParty>();
                else _preSpawnParties.Clear();
                if (MobileParty.All != null)
                {
                    foreach (var p in MobileParty.All) if (p != null) _preSpawnParties.Add(p);
                }
            }
            catch { _preSpawnParties?.Clear(); }
        }

        // Postfix: process parties present now but not in the pre-snapshot — those are the
        // newly-added ones. Order-independent; survives reentrant add/remove between Prefix
        // and Postfix (M13 fix).
        public static void Postfix()
        {
            try
            {
                if (MCMSettings.Instance == null || !MCMSettings.Instance.EnableMod) return;
                if (MCMSettings.Instance.CompatibilityMode) return;
                if (MobileParty.All == null || _preSpawnParties == null) return;

                foreach (var p in MobileParty.All)
                {
                    if (p == null) continue;
                    if (_preSpawnParties.Contains(p)) continue; // already existed before — skip
                    TrySubstitute(p);
                }
            }
            catch (Exception ex)
            {
                Log("Postfix exception: " + ex.Message);
            }
        }

        private static void TrySubstitute(MobileParty newParty)
        {
            if (newParty == null) return;
            // 07-11 storyline guard: quest parties keep their scripted identity, and
            // tutorial-window spawns stay vanilla.
            if (BpStoryGuard.IsQuestParty(newParty)) return;
            if (BpStoryGuard.StoryTutorialActive()) return;
            var vanillaCultureId = newParty.MapFaction?.Culture?.StringId;
            if (string.IsNullOrEmpty(vanillaCultureId)) return;
            if (vanillaCultureId.StartsWith("bp_")) return;

            var candidates = BiomeEligibility.GetReplacements(vanillaCultureId);
            if (candidates.Count == 0) return;

            var dice = Rng.NextDouble();
            var threshold = BaseSubstitutionRate * MCMSettings.Instance.SpawnFrequencyMultiplier;
            if (dice > threshold) { Log("dice " + dice.ToString("F3") + " > " + threshold.ToString("F3") + " (" + vanillaCultureId + " kept)"); return; }

            var enabled = new List<string>();
            foreach (var c in candidates)
            {
                if (BandItPlusCampaignBehavior.Instance != null &&
                    BandItPlusCampaignBehavior.Instance.IsClanEnabled(c))
                    enabled.Add(c);
            }
            if (enabled.Count == 0) return;

            var pickedClan = enabled[Rng.Next(enabled.Count)];
            var newCulture = MBObjectManager.Instance.GetObject<CultureObject>(pickedClan);
            if (newCulture == null) return;

            try
            {
                newParty.Party.SetCustomName(newCulture.Name);
                BandItPlusCampaignBehavior.Instance?.TrackParty(newParty, pickedClan);
                Log("SUBSTITUTED " + vanillaCultureId + " -> " + pickedClan + " (party " + newParty.StringId + ")");
            }
            catch (Exception ex)
            {
                Log("SetCustomName failed: " + ex.Message);
            }
        }

        private static void Log(string msg)
        {
            try
            {
                if (!BandItPlus.Diagnostics.BpDiag.ShouldWrite(msg)) return;
                File.AppendAllText(LogPath,
                    "[" + DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + "] " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
