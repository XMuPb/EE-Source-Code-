using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Translates MCM setting labels, hints, and group headers at render time by
    /// Harmony-patching MCMv5's internal property wrapper and group definition getters.
    ///
    /// How it works:
    /// 1. On mod load (after Localization.Initialize), TryApply() registers 3 postfix patches
    /// 2. When MCM renders the settings panel, it calls get_DisplayName / get_HintText / get_GroupName
    ///    on each property/group
    /// 3. Our postfix computes a localization key from the raw English text and looks it up via
    ///    Localization.L(). If a translation exists (i.e., the key resolves to something different
    ///    than the key itself), the result is replaced. Otherwise the original English is kept.
    /// 4. Because the getters are called on every render, language changes are picked up live
    ///    (the per-tick language poll in SubModuleClassEntry updates _currentLanguage, and the
    ///    next MCM panel render reads the new translations).
    ///
    /// To add translations for a setting:
    ///   - Look at the attribute's Name in MCMSettings.cs (e.g., "Debug Mode")
    ///   - Compute the key: "mcm_" + slug("Debug Mode") + "_name" → "mcm_debug_mode_name"
    ///   - Add that key to Localization.cs GetEnglishDefaults (for diagnostic clarity)
    ///   - Add it to GetTurkishDefaults, GetGermanDefaults, etc. with translated text
    ///   - Same pattern for hints: "mcm_debug_mode_hint"
    ///   - For group headers: "mcm_group_" + slug("8. Debug") → "mcm_group_8_debug"
    /// </summary>
    public static class McmLocalizer
    {
        private static bool _applied = false;
        private static readonly object _lock = new object();

        // Cache raw-English → localization-key lookups so we don't slugify on every render
        private static readonly Dictionary<string, string> _nameKeyCache = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> _hintKeyCache = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> _groupKeyCache = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> _contentKeyCache = new Dictionary<string, string>();

        // Hint lookups key off the DisplayName (not the hint text), to match the
        // slug scheme DumpDiscoveredKeys emits and the *_hint entries stored in
        // GetXxxDefaults dicts. Cached by wrapper instance to avoid reflection-per-render.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, string> _hintKeyByInstance
            = new System.Runtime.CompilerServices.ConditionalWeakTable<object, string>();

        /// <summary>
        /// Installs the 3 Harmony postfix patches. Safe to call multiple times — applies once.
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
                        MCMSettings.DebugLog("[McmLocalizer] MCMv5 assembly not found — skipping");
                        return;
                    }

                    var wrapperType = asm.GetType("MCM.Abstractions.Wrapper.BasePropertyDefinitionWrapper");
                    var groupType = asm.GetType("MCM.Abstractions.SettingsPropertyGroupDefinition");
                    var buttonWrapperType = asm.GetType("MCM.Abstractions.Wrapper.PropertyDefinitionButtonWrapper");

                    if (wrapperType == null || groupType == null)
                    {
                        MCMSettings.DebugLog("[McmLocalizer] Expected MCM types not found in assembly — skipping");
                        return;
                    }

                    var displayNameGet = wrapperType.GetProperty("DisplayName", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                    var hintTextGet = wrapperType.GetProperty("HintText", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                    var groupNameGet = groupType.GetProperty("GroupName", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                    var contentGet = buttonWrapperType?.GetProperty("Content", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();

                    var postfixName = new HarmonyMethod(typeof(McmLocalizer).GetMethod(nameof(DisplayNamePostfix), BindingFlags.NonPublic | BindingFlags.Static));
                    var postfixHint = new HarmonyMethod(typeof(McmLocalizer).GetMethod(nameof(HintTextPostfix), BindingFlags.NonPublic | BindingFlags.Static));
                    var postfixGroup = new HarmonyMethod(typeof(McmLocalizer).GetMethod(nameof(GroupNamePostfix), BindingFlags.NonPublic | BindingFlags.Static));
                    var postfixContent = new HarmonyMethod(typeof(McmLocalizer).GetMethod(nameof(ContentPostfix), BindingFlags.NonPublic | BindingFlags.Static));

                    int patched = 0;
                    if (displayNameGet != null) { harmony.Patch(displayNameGet, postfix: postfixName); patched++; }
                    if (hintTextGet != null) { harmony.Patch(hintTextGet, postfix: postfixHint); patched++; }
                    if (groupNameGet != null) { harmony.Patch(groupNameGet, postfix: postfixGroup); patched++; }
                    if (contentGet != null) { harmony.Patch(contentGet, postfix: postfixContent); patched++; }

                    _applied = true;
                    MCMSettings.DebugLog($"[McmLocalizer] Patched {patched}/4 MCM getters for live localization");

                    // One-shot diagnostic: dump every discovered MCM label to debug.log
                    // so translators know which keys to add. Reads attribute metadata via
                    // reflection on MCMSettings rather than waiting for MCM to render.
                    DumpDiscoveredKeys();
                }
                catch (Exception ex)
                {
                    MCMSettings.DebugLog("[McmLocalizer] TryApply error: " + ex.ToString());
                }
            }
        }

        /// <summary>
        /// Finds the MCMv5 assembly by scanning loaded assemblies for known type names.
        /// </summary>
        private static Assembly FindMcmAssembly()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.GetType("MCM.Abstractions.Wrapper.BasePropertyDefinitionWrapper") != null)
                        return asm;
                }
                catch { }
            }
            return null;
        }

        // ── Postfix patches ────────────────────────────────────────────────

        private static void DisplayNamePostfix(object __instance, ref string __result)
        {
            // Capture raw English DisplayName slug for this instance BEFORE we translate
            // __result below. This becomes the hint-key source for HintTextPostfix.
            // DO NOT read DisplayName via reflection after this point — the property
            // getter is Harmony-patched (by us) and would return the translated value,
            // producing a Cyrillic/Hanzi slug that matches nothing in the dicts.
            if (__instance != null && !string.IsNullOrEmpty(__result))
            {
                try
                {
                    if (!_hintKeyByInstance.TryGetValue(__instance, out _))
                    {
                        string hintKey = "mcm_" + Slugify(__result) + "_hint";
                        _hintKeyByInstance.Add(__instance, hintKey);
                    }
                }
                catch { /* already added or instance not trackable — fall through */ }
            }

            __result = Translate(__result, _nameKeyCache, "mcm_", "_name");
        }

        private static void HintTextPostfix(object __instance, ref string __result)
        {
            // The hint key was stored by DisplayNamePostfix from the RAW English
            // DisplayName — that's the slug scheme the *_hint dict entries use.
            if (__instance == null) return;
            if (!_hintKeyByInstance.TryGetValue(__instance, out string key) || string.IsNullOrEmpty(key))
                return;

            try
            {
                string translated = Localization.L(key);
                if (translated != key && !string.IsNullOrEmpty(translated))
                    __result = translated;
            }
            catch { }
        }

        private static void GroupNamePostfix(ref string __result)
        {
            __result = Translate(__result, _groupKeyCache, "mcm_group_", "");
        }

        private static void ContentPostfix(ref string __result)
        {
            __result = Translate(__result, _contentKeyCache, "mcm_", "_content");
        }

        /// <summary>
        /// Core translation logic: compute a localization key from the raw English text,
        /// look it up via Localization.L(), and return the translation if one exists.
        /// If no translation exists (L returns the key itself), return the original raw text.
        /// </summary>
        private static string Translate(string raw, Dictionary<string, string> cache, string prefix, string suffix)
        {
            if (string.IsNullOrEmpty(raw)) return raw;

            string key;
            lock (cache)
            {
                if (!cache.TryGetValue(raw, out key))
                {
                    key = prefix + Slugify(raw) + suffix;
                    cache[raw] = key;
                }
            }

            try
            {
                string translated = Localization.L(key);
                // Localization.L returns the key itself if no translation found.
                // In that case, keep the original English raw text.
                if (translated != key && !string.IsNullOrEmpty(translated))
                    return translated;
            }
            catch { }

            return raw;
        }

        /// <summary>
        /// Walks MCMSettings via reflection, reads every [SettingProperty*] and
        /// [SettingPropertyGroup] attribute, computes the localization key for each,
        /// and logs a diagnostic dump to debug.log so translators can see what to translate.
        /// Runs once at mod load.
        /// </summary>
        private static void DumpDiscoveredKeys()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("[McmLocalizer] Discovered MCM keys (copy to language dicts to translate):");

                var mcmType = typeof(MCMSettings);
                var groups = new HashSet<string>();
                int nameCount = 0, hintCount = 0, contentCount = 0;

                foreach (var prop in mcmType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    foreach (var attr in prop.GetCustomAttributes(inherit: false))
                    {
                        var attrType = attr.GetType();
                        var attrName = attrType.Name;

                        // Property-level attributes (Name + HintText)
                        if (attrName.StartsWith("SettingProperty") && attrName != "SettingPropertyGroupAttribute")
                        {
                            var displayNameField = attrType.GetProperty("DisplayName");
                            var hintTextField = attrType.GetProperty("HintText");
                            string displayName = displayNameField?.GetValue(attr) as string;
                            string hintText = hintTextField?.GetValue(attr) as string;

                            if (!string.IsNullOrEmpty(displayName))
                            {
                                string nameKey = "mcm_" + Slugify(displayName) + "_name";
                                sb.AppendLine($"  [\"{nameKey}\"] = \"{EscapeForLog(displayName)}\",");
                                nameCount++;
                            }
                            if (!string.IsNullOrEmpty(hintText))
                            {
                                string hintKey = "mcm_" + Slugify(displayName ?? prop.Name) + "_hint";
                                sb.AppendLine($"  [\"{hintKey}\"] = \"{EscapeForLog(hintText)}\",");
                                hintCount++;
                            }

                            // Button Content (the label rendered INSIDE the button)
                            if (attrName == "SettingPropertyButtonAttribute")
                            {
                                var contentField = attrType.GetProperty("Content");
                                string contentText = contentField?.GetValue(attr) as string;
                                if (!string.IsNullOrEmpty(contentText))
                                {
                                    string contentKey = "mcm_" + Slugify(contentText) + "_content";
                                    sb.AppendLine($"  [\"{contentKey}\"] = \"{EscapeForLog(contentText)}\",  // button content");
                                    contentCount++;
                                }
                            }
                        }

                        // Group attributes — MCM splits GroupName on '/' at render time and
                        // returns each segment separately from the GroupName getter, so the
                        // localization key MUST target per-segment slugs (not the full-path slug).
                        if (attrName == "SettingPropertyGroupAttribute")
                        {
                            var groupNameField = attrType.GetProperty("GroupName");
                            string groupName = groupNameField?.GetValue(attr) as string;
                            if (!string.IsNullOrEmpty(groupName))
                            {
                                foreach (var rawSegment in groupName.Split('/'))
                                {
                                    string segment = rawSegment.Trim();
                                    if (segment.Length > 0 && groups.Add(segment))
                                    {
                                        string groupKey = "mcm_group_" + Slugify(segment);
                                        sb.AppendLine($"  [\"{groupKey}\"] = \"{EscapeForLog(segment)}\",  // group segment");
                                    }
                                }
                            }
                        }
                    }
                }

                sb.AppendLine($"[McmLocalizer] Total: {nameCount} names, {hintCount} hints, {contentCount} button contents, {groups.Count} group segments");
                MCMSettings.DebugLog(sb.ToString());
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("[McmLocalizer] DumpDiscoveredKeys error: " + ex.ToString());
            }
        }

        private static string EscapeForLog(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        }

        /// <summary>
        /// Converts arbitrary text to a lowercase snake_case slug suitable as a localization key.
        /// Example: "Debug Mode" → "debug_mode", "1. Info/About" → "1_info_about".
        /// </summary>
        private static string Slugify(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new System.Text.StringBuilder(text.Length);
            bool lastWasSep = true;
            foreach (var c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                    lastWasSep = false;
                }
                else if (!lastWasSep)
                {
                    sb.Append('_');
                    lastWasSep = true;
                }
            }
            // Trim trailing underscore
            if (sb.Length > 0 && sb[sb.Length - 1] == '_') sb.Length--;
            return sb.ToString();
        }
    }
}
