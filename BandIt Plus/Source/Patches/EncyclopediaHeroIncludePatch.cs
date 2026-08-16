using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encyclopedia;
using TaleWorlds.CampaignSystem.Encyclopedia.Pages;

namespace BandItPlus.Patches
{
    // Wave 4.8c: force-include BandItPlus chief Heroes in Encyclopedia → Heroes tab.
    //
    // Discovery from EditableEncyclopediaPatches.cs (the user's flagship encyclopedia mod):
    // the actual displayed list is built by EncyclopediaListVM.Refresh in the namespace
    // TaleWorlds.CampaignSystem.ViewModelCollection.Encyclopedia.List. That namespace
    // isn't statically referenceable cleanly from our DLL, so we resolve at runtime via
    // reflection — same pattern as EditableEncyclopedia uses (line 10340-10370).
    //
    // Vanilla GetListItems applies a filter that excludes notables whose CharacterObject
    // has non-standard Occupation/Culture values. Our HeroCreator-generated chiefs fail
    // that filter even though they pass IsValidEncyclopediaItem. We APPEND them to the
    // displayed list directly via the VM's items collection.
    public static class EncyclopediaListPatcher
    {
        public static void TryPatch(Harmony harmony)
        {
            try
            {
                // EditableEncyclopedia uses typeof(EncyclopediaHeroPageVM).Assembly which
                // resolves to SandBox.ViewModelCollection.dll. We do the same — search a
                // few candidate assemblies because the VM might also live in SandBox.dll.
                // The actual assembly per offline reflection:
                // TaleWorlds.CampaignSystem.ViewModelCollection.dll
                Type listType = null;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    listType = asm.GetType("TaleWorlds.CampaignSystem.ViewModelCollection.Encyclopedia.List.EncyclopediaListVM");
                    if (listType != null) break;
                }
                if (listType == null)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "EncyclopediaListPatcher: could not find EncyclopediaListVM type in any candidate assembly");
                    return;
                }

                var refreshMethod = listType.GetMethod("Refresh",
                    BindingFlags.Instance | BindingFlags.Public);
                if (refreshMethod == null)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "EncyclopediaListPatcher: could not find Refresh method on " + listType.FullName);
                    return;
                }

                var postfix = typeof(EncyclopediaListPatcher)
                    .GetMethod(nameof(RefreshPostfix), BindingFlags.Static | BindingFlags.Public);
                harmony.Patch(refreshMethod, postfix: new HarmonyMethod(postfix));
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "EncyclopediaListPatcher: hooked " + listType.FullName + ".Refresh");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "EncyclopediaListPatcher: TryPatch failed: " + ex.Message);
            }
        }

        // Wave 4.8c-fix: extract the underlying EncyclopediaListItem from an
        // EncyclopediaListItemVM wrapper so we can read its TypeName.
        private static object GetUnderlyingListItem(object vmItem, BindingFlags flags)
        {
            if (vmItem == null) return null;
            // Look for a property holding the EncyclopediaListItem (not the Object hero).
            var props = vmItem.GetType().GetProperties(flags);
            foreach (var p in props)
            {
                if (p.PropertyType == typeof(EncyclopediaListItem))
                    return p.GetValue(vmItem);
            }
            // Look in private fields too.
            var fields = vmItem.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (var f in fields)
            {
                if (f.FieldType == typeof(EncyclopediaListItem))
                    return f.GetValue(vmItem);
            }
            return null;
        }

        public static void RefreshPostfix(object __instance)
        {
            try
            {
                var reg = BandItPlus.Heroes.BanditChiefRegistry.Instance;
                if (reg == null) return;

                // Find the items collection — same property names as EditableEncyclopedia.
                var vmType = __instance.GetType();
                var flags = BindingFlags.Instance | BindingFlags.Public;
                object items = null;
                foreach (var name in new[] { "Items", "ListItems", "FilteredItems" })
                {
                    var prop = vmType.GetProperty(name, flags);
                    if (prop != null) { items = prop.GetValue(__instance); if (items != null) break; }
                }
                if (items == null) return;

                var enumerable = items as IEnumerable;
                if (enumerable == null) return;

                // Collect chief Hero references that aren't already in the list.
                var heroesToAdd = new List<Hero>();
                var alreadyInList = new HashSet<string>();
                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    var objProp = item.GetType().GetProperty("Object", flags) ?? item.GetType().GetProperty("Obj", flags);
                    if (objProp == null) continue;
                    var hero = objProp.GetValue(item) as Hero;
                    if (hero != null) alreadyInList.Add(hero.StringId);
                }
                foreach (var h in reg.GetAllChiefHeroes())
                {
                    if (alreadyInList.Contains(h.StringId)) continue;
                    heroesToAdd.Add(h);
                }

                if (heroesToAdd.Count == 0)
                {
                    // Identify which chief Heroes are present and what type their wrapping
                    // VM is — tells us if vanilla categorized them as Hero or as Troop.
                    int chiefMatches = 0;
                    string sampleVmType = "(none)";
                    foreach (var item in enumerable)
                    {
                        if (item == null) continue;
                        var op = item.GetType().GetProperty("Object", flags) ?? item.GetType().GetProperty("Obj", flags);
                        if (op == null) continue;
                        var hero = op.GetValue(item) as Hero;
                        if (hero != null && reg.IsRegisteredChief(hero))
                        {
                            chiefMatches++;
                            sampleVmType = item.GetType().Name;
                        }
                    }
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "EncyclopediaListVM.Refresh postfix: list size=" + alreadyInList.Count
                        + ", chief matches=" + chiefMatches + " sampleVmType=" + sampleVmType);
                    return;
                }

                // Append. The collection might be MBBindingList<EncyclopediaListItemVM> or
                // a generic List — both have an Add(object) reachable via reflection.
                var addMethod = items.GetType().GetMethod("Add", BindingFlags.Instance | BindingFlags.Public);
                if (addMethod == null) return;

                int added = 0;
                // Find a VANILLA HERO item to mimic exactly (typeName, structure). Items
                // grabbing the first generic item gave us a non-hero template — so the
                // typeName string we used didn't match what the Heroes tab filters by.
                object heroTemplateItem = null;
                foreach (var existing in enumerable)
                {
                    if (existing == null) continue;
                    var op = existing.GetType().GetProperty("Object", flags) ?? existing.GetType().GetProperty("Obj", flags);
                    if (op == null) continue;
                    if (op.GetValue(existing) is Hero) { heroTemplateItem = existing; break; }
                }
                if (heroTemplateItem == null)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "EncyclopediaListVM.Refresh postfix: no vanilla hero item in list to mimic — page may not be Heroes tab");
                    return;
                }

                // Probe the template item to discover the typeName and other fields used by vanilla.
                var listItemFromTemplate = GetUnderlyingListItem(heroTemplateItem, flags);
                string vanillaTypeName = listItemFromTemplate != null
                    ? (listItemFromTemplate.GetType().GetProperty("TypeName")?.GetValue(listItemFromTemplate) as string)
                      ?? "Hero"
                    : "Hero";
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "EncyclopediaListVM.Refresh postfix: probed vanilla hero typeName='" + vanillaTypeName + "'");

                object templateItem = heroTemplateItem;

                var vmItemType = templateItem.GetType();
                var ctor = vmItemType.GetConstructors().FirstOrDefault();
                if (ctor == null) return;

                foreach (var hero in heroesToAdd)
                {
                    try
                    {
                        // Wave 4.11-fix v19 (2026-05-07): research subagent confirmed vanilla
                        // pattern is `() => InformationManager.ShowTooltip(typeof(Hero), hero, false)`
                        // and TypeName="Hero" (the GetIdentifier result). v17's per-chief
                        // ChiefTitle in TypeName broke type-routing; v19 reverts that and adds
                        // the Action so the tooltip actually invokes vanilla's Hero handler
                        // instead of falling through to "Unknown".
                        var heroLocal = hero;
                        var listItem = new EncyclopediaListItem(
                            heroLocal,
                            heroLocal.Name?.ToString() ?? BandItPlus.Localization.Get("bp_encyclopediaheroincludepatch_001", "Bandit Chief"),
                            heroLocal.EncyclopediaText?.ToString() ?? "",
                            heroLocal.StringId,
                            "Hero",
                            true,
                            (System.Action)(() => {
                                try { TaleWorlds.Library.InformationManager.ShowTooltip(typeof(Hero), heroLocal, false); } catch { }
                            }));
                        var ctorParams = ctor.GetParameters();
                        object vmItem = null;
                        if (ctorParams.Length == 1 && ctorParams[0].ParameterType.IsAssignableFrom(typeof(EncyclopediaListItem)))
                        {
                            vmItem = ctor.Invoke(new object[] { listItem });
                        }
                        else if (ctorParams.Length == 1 && ctorParams[0].ParameterType.IsAssignableFrom(typeof(object)))
                        {
                            vmItem = ctor.Invoke(new object[] { listItem });
                        }
                        if (vmItem != null) { addMethod.Invoke(items, new object[] { vmItem }); added++; }
                    }
                    catch { /* skip this hero */ }
                }

                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "EncyclopediaListVM.Refresh postfix: appended " + added + "/"
                    + heroesToAdd.Count + " chiefs (vmItemType=" + vmItemType.Name + ")");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "EncyclopediaListVM.Refresh postfix exception: " + ex.Message);
            }
        }
    }

    // Wave 4.8c-final: patch the Hero PAGE's list builder directly. This is the
    // per-tab list (Heroes tab) — different from EncyclopediaListVM (master).
    [HarmonyPatch(typeof(DefaultEncyclopediaHeroPage), "InitializeListItems")]
    public static class EncyclopediaHeroPageInitPatch
    {
        public static void Postfix(ref IEnumerable<EncyclopediaListItem> __result)
        {
            try
            {
                var reg = BandItPlus.Heroes.BanditChiefRegistry.Instance;
                if (reg == null || __result == null) return;

                // Materialize the lazy iterator so we can append.
                var list = __result.ToList();
                int before = list.Count;
                int added = 0;
                var existingHeroes = new HashSet<Hero>();
                foreach (var item in list)
                {
                    if (item.Object is Hero h) existingHeroes.Add(h);
                }
                foreach (var chief in reg.GetAllChiefHeroes())
                {
                    if (existingHeroes.Contains(chief)) continue;
                    // Wave 4.11-fix v19 (2026-05-07): same fix as the sibling RefreshPostfix —
                    // TypeName="Hero" (vanilla GetIdentifier) + Action that calls vanilla's
                    // hero tooltip handler.
                    var chiefLocal = chief;
                    list.Add(new EncyclopediaListItem(
                        chiefLocal,
                        chiefLocal.Name?.ToString() ?? BandItPlus.Localization.Get("bp_encyclopediaheroincludepatch_002", "Bandit Chief"),
                        chiefLocal.EncyclopediaText?.ToString() ?? "",
                        chiefLocal.StringId,
                        "Hero",
                        true,
                        (System.Action)(() => {
                            try { TaleWorlds.Library.InformationManager.ShowTooltip(typeof(Hero), chiefLocal, false); } catch { }
                        })));
                    added++;
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "EncyclopediaHeroPageInitPatch: vanilla returned " + before
                    + " hero items, appended " + added + " chiefs (total=" + list.Count + ")");
                if (added > 0) __result = list;
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "EncyclopediaHeroPageInitPatch: " + ex.Message);
            }
        }
    }

    // Wave 4.8c: also keep IsValidEncyclopediaItem patch for the per-item filter (search/etc).
    [HarmonyPatch(typeof(DefaultEncyclopediaHeroPage), "IsValidEncyclopediaItem")]
    public static class EncyclopediaHeroIncludePatch
    {
        public static void Postfix(object o, ref bool __result)
        {
            try
            {
                var hero = o as Hero;
                if (hero == null) return;
                var reg = BandItPlus.Heroes.BanditChiefRegistry.Instance;
                if (reg == null) return;
                if (reg.IsRegisteredChief(hero) && !__result) __result = true;
            }
            catch { }
        }
    }

    // Wave 4.29.2 (2026-07-02, "i want bandtiplus"): the origin chronicle on the
    // encyclopedia pages, PURE BandIt Plus — no EditableEncyclopedia, no API, no
    // storage. Paints the authored backstory straight onto the NATIVE description
    // text of the player hero / player clan pages: postfix on the page VMs' Refresh,
    // runtime-resolved exactly like EncyclopediaListPatcher above (the page-VM
    // namespace isn't statically referenceable from our DLL). Render-time only —
    // idempotent and save-safe. Attached DEFERRED (BanditChiefRegistry, session
    // launch) per the CampaignUIHelper static-cctor rule documented there.
    public static class EncyclopediaChroniclePatcher
    {
        private static PropertyInfo _heroObjProp;
        private static PropertyInfo _heroTextProp;
        private static PropertyInfo _clanObjProp;
        private static PropertyInfo _clanTextProp;
        private static bool _patched; // OnSessionLaunched re-fires per load — attach once

        public static void TryPatch(Harmony harmony)
        {
            if (_patched) return;
            _patched = true;
            TryPatchOne(harmony,
                "TaleWorlds.CampaignSystem.ViewModelCollection.Encyclopedia.Pages.EncyclopediaHeroPageVM",
                nameof(HeroPagePostfix));
            TryPatchOne(harmony,
                "TaleWorlds.CampaignSystem.ViewModelCollection.Encyclopedia.Pages.EncyclopediaClanPageVM",
                nameof(ClanPagePostfix));
        }

        private static void TryPatchOne(Harmony harmony, string typeName, string postfixName)
        {
            try
            {
                Type vmType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    vmType = asm.GetType(typeName);
                    if (vmType != null) break;
                }
                if (vmType == null) { Log("ChroniclePatcher: type not found: " + typeName); return; }
                var refresh = vmType.GetMethod("Refresh", BindingFlags.Instance | BindingFlags.Public);
                if (refresh == null) { Log("ChroniclePatcher: no Refresh on " + typeName); return; }
                var postfix = typeof(EncyclopediaChroniclePatcher)
                    .GetMethod(postfixName, BindingFlags.Static | BindingFlags.Public);
                harmony.Patch(refresh, postfix: new HarmonyMethod(postfix));
                Log("ChroniclePatcher: hooked " + typeName + ".Refresh");
            }
            catch (Exception ex)
            { Log("ChroniclePatcher: patch failed for " + typeName + ": " + ex.Message); }
        }

        public static void HeroPagePostfix(object __instance)
        {
            try
            {
                var origins = BandItPlus.Origins.BanditOriginBehavior.Instance;
                if (origins == null || !origins.OriginChosen) return;
                var hero = GetObj(__instance, ref _heroObjProp) as Hero;
                if (hero == null || hero != Hero.MainHero || Clan.PlayerClan == null) return;
                string story = BandItPlus.Origins.BanditChronicleCodex
                    .BuildHeroStory(hero, Clan.PlayerClan, origins.Origin);
                SetText(__instance, ref _heroTextProp, story);
            }
            catch (Exception ex) { Log("ChroniclePatcher hero page: " + ex.Message); }
        }

        public static void ClanPagePostfix(object __instance)
        {
            try
            {
                var origins = BandItPlus.Origins.BanditOriginBehavior.Instance;
                if (origins == null || !origins.OriginChosen) return;
                var clan = GetObj(__instance, ref _clanObjProp) as Clan;
                if (clan == null || clan != Clan.PlayerClan || Hero.MainHero == null) return;
                string story = BandItPlus.Origins.BanditChronicleCodex
                    .BuildClanStory(Hero.MainHero, clan);
                SetText(__instance, ref _clanTextProp, story);
            }
            catch (Exception ex) { Log("ChroniclePatcher clan page: " + ex.Message); }
        }

        private static object GetObj(object vm, ref PropertyInfo cache)
        {
            if (cache == null)
                cache = vm.GetType().GetProperty("Obj", BindingFlags.Instance | BindingFlags.Public)
                     ?? vm.GetType().GetProperty("Object", BindingFlags.Instance | BindingFlags.Public);
            return cache != null ? cache.GetValue(vm) : null;
        }

        private static void SetText(object vm, ref PropertyInfo cache, string text)
        {
            if (cache == null)
            {
                foreach (var name in new[] { "InformationText", "DescriptionText", "HistoryText" })
                {
                    var p = vm.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                    if (p != null && p.PropertyType == typeof(string) && p.CanWrite) { cache = p; break; }
                }
                if (cache == null)
                { Log("ChroniclePatcher: no writable text property on " + vm.GetType().Name); return; }
                Log("ChroniclePatcher: writing via " + vm.GetType().Name + "." + cache.Name);
            }
            cache.SetValue(vm, text);
        }

        private static void Log(string msg)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Codex] " + msg); }
            catch { }
        }
    }
}
