using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Harmony-patches MCMv5's <c>SettingsPropertyGroupDefinition.SubGroups</c> getter to hide the
    /// "EEWebExtension" subgroup of "9. Extensions" when:
    ///   (a) the EEWebExtension assembly is not loaded, OR
    ///   (b) <see cref="MCMSettings.EnableWebExtension"/> is <c>false</c>.
    ///
    /// We patch the getter (not the discoverer) so the filter is dynamic — toggling
    /// <c>EnableWebExtension</c> in the MCM panel reflects immediately on the next render
    /// without requiring a reload, because MCM re-queries the getter every frame.
    ///
    /// We use <c>GroupNameRaw</c> for the predicate (not <c>GroupName</c>) to bypass the
    /// translation patches in <see cref="McmLocalizer"/>, so the rule is stable across languages.
    ///
    /// MCMv5-version-fragile: tested against MCMv5 v5.11.3.0. If MCM renames or restructures
    /// <c>SettingsPropertyGroupDefinition</c>, <see cref="TryApply"/> logs a no-op and bails out
    /// gracefully — settings still work, the subgroup just stays visible.
    /// </summary>
    public static class WebExtensionVisibilityPatch
    {
        private static bool _applied;
        private static readonly object _lock = new object();
        private static MethodInfo _groupNameRawGetter;

        // Re-entrancy guard — prevents stack overflow if reading MCMSettings.Instance during
        // the postfix somehow re-triggers MCM's group-tree walk on the same thread.
        [ThreadStatic] private static int _depth;

        /// <summary>
        /// Installs the postfix patch on <c>SettingsPropertyGroupDefinition.SubGroups</c> getter.
        /// Idempotent — safe to call multiple times. Logs and returns silently if MCMv5
        /// internals can't be located (defensive against future MCM versions).
        /// </summary>
        public static void TryApply(Harmony harmony)
        {
            lock (_lock)
            {
                if (_applied) return;

                try
                {
                    var asm = FindMcmAssembly();
                    if (asm == null)
                    {
                        MCMSettings.DebugLog("[WebExtVis] MCMv5 assembly not found - skipping (subgroup will stay visible)");
                        return;
                    }

                    var groupType = asm.GetType("MCM.Abstractions.SettingsPropertyGroupDefinition");
                    if (groupType == null)
                    {
                        MCMSettings.DebugLog("[WebExtVis] SettingsPropertyGroupDefinition not found - skipping");
                        return;
                    }

                    var subGroupsGet = groupType.GetProperty("SubGroups", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                    if (subGroupsGet == null)
                    {
                        MCMSettings.DebugLog("[WebExtVis] SubGroups getter not found - skipping");
                        return;
                    }

                    _groupNameRawGetter = groupType.GetProperty("GroupNameRaw", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                    if (_groupNameRawGetter == null)
                    {
                        MCMSettings.DebugLog("[WebExtVis] GroupNameRaw getter not found - skipping");
                        return;
                    }

                    var postfix = new HarmonyMethod(typeof(WebExtensionVisibilityPatch).GetMethod(
                        nameof(SubGroupsPostfix), BindingFlags.NonPublic | BindingFlags.Static));
                    harmony.Patch(subGroupsGet, postfix: postfix);

                    _applied = true;
                    MCMSettings.DebugLog("[WebExtVis] Patched SubGroups getter - EEWebExtension subgroup will hide when extension is unloaded or disabled");
                }
                catch (Exception ex)
                {
                    MCMSettings.DebugLog("[WebExtVis] TryApply error: " + ex.ToString());
                }
            }
        }

        /// <summary>
        /// Locates the MCMv5 assembly by sniffing for a known type. Mirrors
        /// <see cref="McmLocalizer"/>'s lookup so behavior stays consistent.
        /// </summary>
        private static Assembly FindMcmAssembly()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.GetType("MCM.Abstractions.SettingsPropertyGroupDefinition") != null)
                        return asm;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Postfix that filters the EEWebExtension subgroup out of "9. Extensions"'s SubGroups
        /// enumerable when the extension is unavailable or disabled.
        ///
        /// Uses <c>ref object</c> so Harmony binds against the generic
        /// <c>IEnumerable&lt;SettingsPropertyGroupDefinition&gt;</c> return type without our
        /// project needing a compile-time reference to the MCMv5 type.
        /// </summary>
        private static void SubGroupsPostfix(object __instance, ref object __result)
        {
            if (_depth > 0) return;
            if (__result == null || __instance == null) return;

            _depth++;
            try
            {
                // Cheap path: only act on "9. Extensions". For every other group, no-op.
                string parentName;
                try { parentName = _groupNameRawGetter.Invoke(__instance, null) as string; }
                catch { return; }

                if (parentName != "9. Extensions") return;

                bool extLoaded = MCMSettings.IsEEWebExtensionLoaded();
                bool extEnabled = false;
                if (extLoaded)
                {
                    try
                    {
                        var inst = MCMSettings.Instance;
                        if (inst != null) extEnabled = inst.EnableWebExtension;
                    }
                    catch { /* if Instance not ready yet, treat as disabled */ }
                }

                // If both loaded and enabled, no filtering needed.
                if (extLoaded && extEnabled) return;

                // Build a typed List<SettingsPropertyGroupDefinition> matching MCM's contract,
                // skipping the "EEWebExtension" leaf node.
                var sourceEnumerable = __result as IEnumerable;
                if (sourceEnumerable == null) return;

                Type elementType = __instance.GetType();
                Type listType = typeof(List<>).MakeGenericType(elementType);
                var filtered = (IList)Activator.CreateInstance(listType);

                foreach (var sub in sourceEnumerable)
                {
                    if (sub == null) { filtered.Add(null); continue; }
                    string subName = null;
                    try { subName = _groupNameRawGetter.Invoke(sub, null) as string; } catch { }
                    if (subName == "EE-WebAPI") continue; // hide (subgroup leaf renamed from EEWebExtension)
                    filtered.Add(sub);
                }

                __result = filtered;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("[WebExtVis] SubGroupsPostfix error: " + ex.ToString());
            }
            finally
            {
                _depth--;
            }
        }
    }
}
