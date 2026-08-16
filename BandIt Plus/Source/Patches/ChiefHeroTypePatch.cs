using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BandItPlus.Patches
{
    // Wave 4.11 (2026-05-06): override the encyclopedia hero-page "Type" field
    // for our 14 named bandit chiefs with per-clan flavor titles authored in
    // CultureProfileRegistry.<cultureId>.ChiefTitle (e.g. "Reaver-Chief",
    // "Stalker-Matriarch", "Toll-Reeve").
    //
    // Reflection-based patching follows the EncyclopediaListPatcher pattern in this
    // same Patches folder — BandItPlus.csproj does NOT reference
    // TaleWorlds.CampaignSystem.ViewModelCollection.dll, so we look up the VM type
    // at runtime instead of via [HarmonyPatch(typeof(...))]. Same outcome, no DLL
    // dependency added.
    //
    // Strategy: postfix EncyclopediaHeroPageVM.Refresh, walk Stats list, find the
    // "Occupation" entry, override its Value with the per-culture title. If no
    // Occupation row exists, inject one via the StringPairItemVM ctor.
    public static class ChiefHeroTypePatch
    {
        private static FieldInfo _heroField;
        private static PropertyInfo _heroProp;
        private static PropertyInfo _statsProp;
        private static Type _stringPairItemType;
        private static PropertyInfo _definitionProp;
        private static PropertyInfo _valueProp;
        private static bool _reflectionResolved;
        private static bool _reflectionFailed;

        public static void TryPatch(Harmony harmony)
        {
            try
            {
                Type vmType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    vmType = asm.GetType("TaleWorlds.CampaignSystem.ViewModelCollection.Encyclopedia.Pages.EncyclopediaHeroPageVM");
                    if (vmType != null) break;
                }
                if (vmType == null)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "ChiefHeroTypePatch: could not find EncyclopediaHeroPageVM type");
                    return;
                }

                var refreshMethod = vmType.GetMethod("Refresh",
                    BindingFlags.Instance | BindingFlags.Public);
                if (refreshMethod == null)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "ChiefHeroTypePatch: could not find Refresh on " + vmType.FullName);
                    return;
                }

                var postfix = typeof(ChiefHeroTypePatch)
                    .GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.Public);
                harmony.Patch(refreshMethod, postfix: new HarmonyMethod(postfix));
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "ChiefHeroTypePatch: hooked " + vmType.FullName + ".Refresh");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "ChiefHeroTypePatch: TryPatch failed: " + ex.Message);
            }
        }

        // Postfix takes object (not the VM type) since the type isn't statically
        // referenceable from this assembly. Harmony binds __instance by name.
        public static void Postfix(object __instance)
        {
            if (__instance == null) return;
            try
            {
                if (!_reflectionResolved && !_reflectionFailed) ResolveReflection(__instance);
                if (_reflectionFailed) return;

                Hero hero = null;
                if (_heroProp != null) hero = _heroProp.GetValue(__instance) as Hero;
                else if (_heroField != null) hero = _heroField.GetValue(__instance) as Hero;
                if (hero == null) return;

                var reg = BandItPlus.Heroes.BanditChiefRegistry.Instance;
                if (reg == null || !reg.IsRegisteredChief(hero)) return;

                var cultureId = hero.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return;
                if (!BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out var profile)
                    || profile == null
                    || string.IsNullOrEmpty(profile.ChiefTitle))
                    return;
                var title = profile.ChiefTitle;

                var stats = _statsProp?.GetValue(__instance) as IEnumerable;
                if (stats == null) return;
                bool found = false;
                // Wave 4.11-fix v15 (2026-05-06): one-shot diagnostic — collect all
                // Definitions we see so the log tells us what label keys the encyclopedia
                // actually uses. Disable after we know the right match string.
                var diagDefs = new System.Collections.Generic.List<string>();
                foreach (var item in stats)
                {
                    if (item == null) continue;
                    var def = _definitionProp?.GetValue(item) as string;
                    if (def == null) continue;
                    diagDefs.Add(def);
                    if (def.IndexOf("Occupation", StringComparison.OrdinalIgnoreCase) >= 0
                        || def.IndexOf("Type", StringComparison.OrdinalIgnoreCase) >= 0
                        || def.IndexOf("Profession", StringComparison.OrdinalIgnoreCase) >= 0
                        || def.IndexOf("Role", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try { _valueProp?.SetValue(item, title); found = true; break; } catch { }
                    }
                }
                // Wave 4.11-fix v18 (2026-05-06): v16 InformationText/InfoText overrides
                // REMOVED. Diagnostic showed they were clobbering the chief's full bio
                // paragraph with just the short title (Pass-Lord, etc.) — wrong target,
                // and corrupted the encyclopedia page's description box. Type display is
                // handled by the EncyclopediaListItem.TypeName field set in
                // EncyclopediaListPatcher (v17), not by this page-VM postfix.

                // Diagnostic kept for debugging Stats list contents (still useful).
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "ChiefHeroTypePatch: stats-walk " + (hero.Name?.ToString() ?? "?")
                    + " foundMatch=" + found
                    + " title='" + title + "'"
                    + " defs=[" + string.Join("|", diagDefs.ToArray()) + "]");

                if (!found)
                {
                    try
                    {
                        var addMethod = stats.GetType().GetMethod("Add");
                        if (addMethod != null && _stringPairItemType != null)
                        {
                            var ctor2 = _stringPairItemType.GetConstructor(new[] { typeof(string), typeof(string) });
                            object newItem = null;
                            if (ctor2 != null)
                                newItem = ctor2.Invoke(new object[] { BandItPlus.Localization.Get("bp_chiefherotypepatch_001", "Type"), title });
                            else
                            {
                                var ctor3 = _stringPairItemType.GetConstructor(new[] { typeof(string), typeof(string), typeof(string) });
                                if (ctor3 != null)
                                    newItem = ctor3.Invoke(new object[] { BandItPlus.Localization.Get("bp_chiefherotypepatch_001", "Type"), title, null });
                            }
                            if (newItem != null) addMethod.Invoke(stats, new[] { newItem });
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "ChiefHeroTypePatch.Postfix exception: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ResolveReflection(object vm)
        {
            try
            {
                var vmType = vm.GetType();

                _heroProp = vmType.GetProperty("Hero",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_heroProp == null)
                {
                    _heroField = vmType.GetField("_hero",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? vmType.GetField("<Hero>k__BackingField",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                }

                _statsProp = vmType.GetProperty("Stats",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (_statsProp != null)
                {
                    var sample = _statsProp.GetValue(vm) as IEnumerable;
                    if (sample != null)
                    {
                        foreach (var s in sample)
                        {
                            if (s == null) continue;
                            _stringPairItemType = s.GetType();
                            break;
                        }
                    }
                }
                if (_stringPairItemType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var t = asm.GetType("TaleWorlds.Library.StringPairItemVM");
                        if (t != null) { _stringPairItemType = t; break; }
                    }
                }
                if (_stringPairItemType != null)
                {
                    _definitionProp = _stringPairItemType.GetProperty("Definition",
                        BindingFlags.Instance | BindingFlags.Public);
                    _valueProp = _stringPairItemType.GetProperty("Value",
                        BindingFlags.Instance | BindingFlags.Public);
                }

                if ((_heroProp == null && _heroField == null)
                    || _statsProp == null
                    || _definitionProp == null
                    || _valueProp == null)
                {
                    _reflectionFailed = true;
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "ChiefHeroTypePatch: reflection resolve failed | heroProp="
                        + (_heroProp != null) + " heroField=" + (_heroField != null)
                        + " statsProp=" + (_statsProp != null)
                        + " defProp=" + (_definitionProp != null)
                        + " valProp=" + (_valueProp != null));
                    return;
                }

                _reflectionResolved = true;
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "ChiefHeroTypePatch: reflection resolved OK | spType="
                    + (_stringPairItemType?.FullName ?? "?"));
            }
            catch (Exception ex)
            {
                _reflectionFailed = true;
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "ChiefHeroTypePatch.ResolveReflection: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
