using System;
using TaleWorlds.ModuleManager;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Detects loaded mods that modify the encyclopedia page layout in ways
    /// EE's widget injectors can't safely coexist with. Result is cached once
    /// at startup so per-Postfix checks are O(1).
    ///
    /// Symptom that motivated this: with Realm of Thrones loaded, EE's
    /// LoreSectionInjector / JournalSectionInjector / HeroTimelineSectionInjector
    /// anchor to EncyclopediaDividerButtonWidget and InsertChild into its parent.
    /// RoT replaces the settlement encyclopedia prefab, so the parent has a
    /// different shape — EE's injected widgets wrap into their own column and
    /// overlap the page (user bug report 2026-05-25, image S987QOQ).
    /// </summary>
    public static class ModCompatHelper
    {
        // Module IDs known to ship a modified EncyclopediaSettlementPage prefab
        // or restructured settlement VM tree.
        private static readonly string[] s_conflictingSettlementLayoutModules = new[]
        {
            "ROT-Core",
            "ROT-Content",
            "ROT-Map",
            "ROT-Dragon",
        };

        private static volatile bool s_detected;
        private static string s_detectedReason;
        private static volatile bool s_initialized;

        /// <summary>
        /// Walk the loaded module list and cache the detection flag.
        /// Safe to call multiple times — only the first call does work.
        /// Should be called from OnBeforeInitialModuleScreenSetAsRoot (after
        /// all modules have finished OnSubModuleLoad).
        /// </summary>
        public static void DetectAtStartup()
        {
            if (s_initialized) return;
            s_initialized = true;
            try
            {
                foreach (var module in ModuleHelper.GetModules())
                {
                    if (module == null || module.Id == null) continue;
                    for (int i = 0; i < s_conflictingSettlementLayoutModules.Length; i++)
                    {
                        if (string.Equals(module.Id, s_conflictingSettlementLayoutModules[i], StringComparison.OrdinalIgnoreCase))
                        {
                            s_detected = true;
                            s_detectedReason = module.Id;
                            try
                            {
                                MCMSettings.DebugLog("[ModCompat] Conflicting settlement-layout mod detected: " + module.Id
                                    + " - EE settlement encyclopedia injection will be skipped by default. "
                                    + "Toggle 'Override Layout Mod Compat' in MCM to force-enable.");
                            }
                            catch { }
                            return;
                        }
                    }
                }
                try { MCMSettings.DebugLog("[ModCompat] No conflicting settlement-layout mods detected."); } catch { }
            }
            catch (Exception ex)
            {
                try { MCMSettings.DebugLog("[ModCompat] DetectAtStartup failed: " + ex.ToString()); } catch { }
            }
        }

        /// <summary>
        /// True when a known conflicting layout mod (Realm of Thrones, etc.) is loaded.
        /// Returns false before DetectAtStartup() has run.
        /// </summary>
        public static bool IsConflictingSettlementLayoutModLoaded { get { return s_detected; } }

        /// <summary>
        /// The module Id that triggered detection, or null if none.
        /// </summary>
        public static string DetectedModuleId { get { return s_detectedReason; } }

        /// <summary>
        /// True when EE should bypass settlement-page injection entirely.
        /// False if no conflict detected, OR if the user has explicitly
        /// overridden via MCM (OverrideLayoutModCompat = true).
        /// </summary>
        public static bool ShouldSkipSettlementInjection()
        {
            if (!s_detected) return false;
            try
            {
                var s = MCMSettings.Instance;
                if (s != null && s.OverrideLayoutModCompat) return false;
            }
            catch { }
            return true;
        }
    }
}
