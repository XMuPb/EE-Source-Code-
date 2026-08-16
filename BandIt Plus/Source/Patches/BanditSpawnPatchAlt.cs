using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Party;

namespace BandItPlus.Patches
{
    [HarmonyPatch]
    public static class SpawnLooterPartyPatch
    {
        private static readonly string LogPath = BandItPlus.Diagnostics.BpDiag.P("bp_spawn_debug.log");

        // M13 fix: track party identity (HashSet) instead of a count snapshot.
        // Set-difference is order-independent and survives reentrant add/remove.
        [System.ThreadStatic]
        private static HashSet<MobileParty> _preSpawnParties;

        public static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.BanditSpawnCampaignBehavior");
            if (type == null) return null;
            var m = AccessTools.Method(type, "SpawnLooterParty");
            if (m != null) Log("SpawnLooterPatch: matched SpawnLooterParty (returns " + m.ReturnType.Name + ", " + m.GetParameters().Length + " params)");
            return m;
        }

        public static bool Prepare() => TargetMethod() != null;

        public static void Prefix()
        {
            try
            {
                if (_preSpawnParties == null) _preSpawnParties = new HashSet<MobileParty>();
                else _preSpawnParties.Clear();
                if (MobileParty.All != null)
                    foreach (var p in MobileParty.All) if (p != null) _preSpawnParties.Add(p);
            }
            catch { _preSpawnParties?.Clear(); }
        }

        public static void Postfix()
        {
            try
            {
                if (MobileParty.All == null) return;
                int found = 0;
                foreach (var p in MobileParty.All)
                {
                    if (p == null) continue;
                    if (_preSpawnParties != null && _preSpawnParties.Contains(p)) continue;
                    found++;
                    BanditSpawnPatch_Helpers.TrySubstitute(p, "SpawnLooterParty");
                }
                if (found > 0) Log("SpawnLooterPatch fired; new parties: " + found);
            }
            catch (Exception ex) { Log("SpawnLooterPatch ex: " + ex.Message); }
        }

        private static void Log(string msg)
        {
            try
            {
                if (!BandItPlus.Diagnostics.BpDiag.ShouldWrite(msg)) return;
                File.AppendAllText(LogPath, "[" + DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + "] " + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    [HarmonyPatch]
    public static class SpawnBanditsAroundHideoutPatch
    {
        private static readonly string LogPath = BandItPlus.Diagnostics.BpDiag.P("bp_spawn_debug.log");

        // M13 fix: track party identity (HashSet) instead of a count snapshot.
        [System.ThreadStatic]
        private static HashSet<MobileParty> _preSpawnParties;

        public static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.BanditSpawnCampaignBehavior");
            if (type == null) return null;
            var m = AccessTools.Method(type, "SpawnBanditsAroundHideout");
            if (m != null) Log("SpawnBanditsAroundHideoutPatch: matched (returns " + m.ReturnType.Name + ", " + m.GetParameters().Length + " params)");
            return m;
        }

        public static bool Prepare() => TargetMethod() != null;

        public static void Prefix()
        {
            try
            {
                if (_preSpawnParties == null) _preSpawnParties = new HashSet<MobileParty>();
                else _preSpawnParties.Clear();
                if (MobileParty.All != null)
                    foreach (var p in MobileParty.All) if (p != null) _preSpawnParties.Add(p);
            }
            catch { _preSpawnParties?.Clear(); }
        }

        public static void Postfix()
        {
            try
            {
                if (MobileParty.All == null) return;
                int found = 0;
                foreach (var p in MobileParty.All)
                {
                    if (p == null) continue;
                    if (_preSpawnParties != null && _preSpawnParties.Contains(p)) continue;
                    found++;
                    BanditSpawnPatch_Helpers.TrySubstitute(p, "SpawnBanditsAroundHideout");
                }
                if (found > 0) Log("SpawnBanditsAroundHideoutPatch fired; new parties: " + found);
            }
            catch (Exception ex) { Log("SpawnBanditsAroundHideoutPatch ex: " + ex.Message); }
        }

        private static void Log(string msg)
        {
            try
            {
                if (!BandItPlus.Diagnostics.BpDiag.ShouldWrite(msg)) return;
                File.AppendAllText(LogPath, "[" + DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + "] " + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    public static class BanditSpawnPatch_Helpers
    {
        private static readonly Random Rng = new Random();
        private const double BaseSubstitutionRate = 0.30;
        private static readonly string LogPath = BandItPlus.Diagnostics.BpDiag.P("bp_spawn_debug.log");

        public static void TrySubstitute(MobileParty newParty, string source)
        {
            if (newParty == null) { Log("[" + source + "] BAIL: party null"); return; }
            // 07-11 storyline guard: quest parties keep their scripted identity, and
            // tutorial-window spawns stay vanilla.
            if (BpStoryGuard.IsQuestParty(newParty)) { Log("[" + source + "] BAIL: quest party"); return; }
            if (BpStoryGuard.StoryTutorialActive()) { Log("[" + source + "] BAIL: story tutorial"); return; }
            if (MCMSettings.Instance == null) { Log("[" + source + "] BAIL: MCM null"); return; }
            if (!MCMSettings.Instance.EnableMod) { Log("[" + source + "] BAIL: mod disabled"); return; }
            if (MCMSettings.Instance.CompatibilityMode) { Log("[" + source + "] BAIL: compat mode"); return; }

            var vanillaCultureId = newParty.MapFaction?.Culture?.StringId;
            Log("[" + source + "] saw culture='" + (vanillaCultureId ?? "<null>") + "' party=" + newParty.StringId);
            if (string.IsNullOrEmpty(vanillaCultureId)) return;
            if (vanillaCultureId.StartsWith("bp_")) return;

            var candidates = BiomeEligibility.GetReplacements(vanillaCultureId);
            if (candidates.Count == 0) { Log("[" + source + "] no replacements for '" + vanillaCultureId + "'"); return; }

            var dice = Rng.NextDouble();
            var threshold = BaseSubstitutionRate * MCMSettings.Instance.SpawnFrequencyMultiplier;
            if (dice > threshold)
            {
                Log("[" + source + "] dice " + dice.ToString("F3") + " > " + threshold.ToString("F3") + " (" + vanillaCultureId + " kept)");
                return;
            }

            var enabled = new System.Collections.Generic.List<string>();
            foreach (var c in candidates)
            {
                if (BandItPlusCampaignBehavior.Instance != null &&
                    BandItPlusCampaignBehavior.Instance.IsClanEnabled(c))
                    enabled.Add(c);
            }
            if (enabled.Count == 0) return;

            var pickedClan = enabled[Rng.Next(enabled.Count)];
            var newCulture = TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObject<TaleWorlds.CampaignSystem.CultureObject>(pickedClan);
            if (newCulture == null) return;

            try
            {
                newParty.Party.SetCustomName(newCulture.Name);
                BandItPlusCampaignBehavior.Instance?.TrackParty(newParty, pickedClan);
                Log("[" + source + "] SUBSTITUTED " + vanillaCultureId + " -> " + pickedClan + " (" + newParty.StringId + ")");
            }
            catch (Exception ex) { Log("[" + source + "] SetCustomName failed: " + ex.Message); }
        }

        private static void Log(string msg)
        {
            try
            {
                if (!BandItPlus.Diagnostics.BpDiag.ShouldWrite(msg)) return;
                File.AppendAllText(LogPath, "[" + DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + "] " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
