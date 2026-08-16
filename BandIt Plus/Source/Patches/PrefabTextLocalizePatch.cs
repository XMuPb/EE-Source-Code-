using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Localization;

namespace BandItPlus.Patches
{
    // 07-13 (translator): BP prefab labels carry {=bp_pf_*} markers so they translate.
    // When the ACTIVE language has the id (English always does; a translation once the
    // translator covers the id), the engine resolves it fine. The only rough edge is a
    // translation that MISSES an id: static prefab text doesn't fall back to the inline
    // English the way code text does, so it would print the raw {=id} tag.
    //
    // This postfix walks each BP movie after it loads and, for any Text still holding a
    // {=-marker, re-runs it through TextObject.ToString() — the same resolver the code
    // text uses — which resolves to the translation if present and falls back to the inline
    // English if not. Net effect: a label shows the translation, or English, but never the
    // raw id. Idempotent (skips already-resolved text); scoped to BP's own prefabs.
    [HarmonyPatch]
    public static class PrefabTextLocalizePatch
    {
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly HashSet<string> BpMovies = new HashSet<string>(StringComparer.Ordinal)
        {
            "GuardAlertHud", "LockpickPrompt", "AlarmMeter", "EscapeHud", "BrandCredit", "Lockpick",
            "BanditHud",
            "BanditAiStatus", "BanditCampPanel", "BanditContact", "BanditKingBrief", "BanditLetter",
            "BanditParley", "BanditReinforcement", "BanditToast", "BanditToastCenter", "BpJailbreakBrief",
            "CampEntryBanner", "CampInfoPanel", "CampTopLeft", "BanditPlusConsoleV3", "HideoutBrief",
            "HideoutBanditAiPanel", "HideoutCinemaIntro", "SneakShadowBar",
        };

        public static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("TaleWorlds.Engine.GauntletUI.GauntletLayer")
                    ?? AccessTools.TypeByName("TaleWorlds.MountAndBlade.GauntletUI.GauntletLayer");
            if (t == null) return null;
            return AccessTools.Method(t, "LoadMovie", new[] { typeof(string), typeof(TaleWorlds.Library.ViewModel) });
        }

        public static bool Prepare()
        {
            var found = TargetMethod() != null;
            Log(found ? "attached to GauntletLayer.LoadMovie" : "GauntletLayer.LoadMovie not found");
            return found;
        }

        public static void Postfix(string movieName, object __result)
        {
            try
            {
                if (movieName == null || !BpMovies.Contains(movieName) || __result == null) return;
                object movie = null;
                try { movie = __result.GetType().GetProperty("Movie", F)?.GetValue(__result); } catch { }
                object root = FindWidgetRoot(movie ?? __result, 0, new HashSet<object>());
                if (root == null) { Log("no widget root for " + movieName); return; }
                int n = ResolveTree(root, 0);
                Log((n > 0 ? "resolved " + n : "no unresolved markers") + " in " + movieName);
            }
            catch (Exception ex) { Log("Postfix " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static int ResolveTree(object widget, int depth)
        {
            if (widget == null || depth > 14) return 0;
            int n = 0;
            var t = widget.GetType();
            try
            {
                var textProp = t.GetProperty("Text", F);
                if (textProp != null && textProp.PropertyType == typeof(string) && textProp.CanWrite)
                {
                    var val = textProp.GetValue(widget) as string;
                    if (!string.IsNullOrEmpty(val) && val.StartsWith("{=", StringComparison.Ordinal))
                    {
                        string resolved = new TextObject(val).ToString();
                        if (!string.IsNullOrEmpty(resolved) && resolved != val) { textProp.SetValue(widget, resolved); n++; }
                    }
                }
            }
            catch { }
            try
            {
                var ccProp = t.GetProperty("ChildCount", F);
                var getChild = t.GetMethod("GetChild", F, null, new[] { typeof(int) }, null);
                if (ccProp != null && getChild != null)
                {
                    int cc = (int)ccProp.GetValue(widget);
                    for (int i = 0; i < cc; i++)
                    {
                        object child = null;
                        try { child = getChild.Invoke(widget, new object[] { i }); } catch { }
                        n += ResolveTree(child, depth + 1);
                    }
                }
            }
            catch { }
            return n;
        }

        private static object FindWidgetRoot(object obj, int depth, HashSet<object> seen)
        {
            if (obj == null || depth > 7 || !seen.Add(obj)) return null;
            var t = obj.GetType();
            if (t.GetProperty("ChildCount", F) != null
                && t.GetMethod("GetChild", F, null, new[] { typeof(int) }, null) != null)
                return obj;
            foreach (var pn in new[] { "RootWidget", "Root", "RootView", "Target" })
            {
                var p = t.GetProperty(pn, F);
                if (p == null) continue;
                object pv = null; try { pv = p.GetValue(obj); } catch { }
                var r = FindWidgetRoot(pv, depth + 1, seen);
                if (r != null) return r;
            }
            foreach (var f in AllFields(t))
            {
                var ftn = f.FieldType.Name;
                bool follow = ftn.IndexOf("Context", StringComparison.OrdinalIgnoreCase) >= 0
                    || ftn.IndexOf("Movie", StringComparison.OrdinalIgnoreCase) >= 0
                    || ftn.IndexOf("View", StringComparison.OrdinalIgnoreCase) >= 0
                    || ftn.IndexOf("Widget", StringComparison.OrdinalIgnoreCase) >= 0
                    || ftn.IndexOf("Gauntlet", StringComparison.OrdinalIgnoreCase) >= 0
                    || ftn.IndexOf("Target", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!follow) continue;
                object fv = null; try { fv = f.GetValue(obj); } catch { }
                if (fv == null) continue;
                if (fv is System.Collections.IEnumerable en && !(fv is string))
                {
                    foreach (var item in en) { var r = FindWidgetRoot(item, depth + 1, seen); if (r != null) return r; }
                }
                else { var r = FindWidgetRoot(fv, depth + 1, seen); if (r != null) return r; }
            }
            return null;
        }

        private static List<FieldInfo> AllFields(Type type)
        {
            var list = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                list.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            return list;
        }

        private static void Log(string msg)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Loc] " + msg); } catch { }
        }
    }
}
