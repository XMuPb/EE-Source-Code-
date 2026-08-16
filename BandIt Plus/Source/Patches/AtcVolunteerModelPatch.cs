using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BandItPlus.Patches
{
    // 07-11 ATC interop (crash report): Adonnay's Troop Changer replaces the volunteer
    // model, and its GetDailyVolunteerProductionProbability NREs at OnNewGameCreated on
    // notables whose culture it never configured. Our chiefs are exactly that — bp_*
    // culture, gang-leader template, injected into Settlement.Notables for the
    // encyclopedia. Prefix: BP-culture heroes produce no volunteers, skip ATC's body.
    // Prepare() returns false when ATC isn't installed, so the patch costs nothing.
    [HarmonyPatch]
    public static class AtcVolunteerModelPatch
    {
        // 2026-07-26: widened from a single hard-coded type name.
        // A user's harmony.log showed:
        //   AccessTools.TypeByName: Could not find type named
        //   AdonnaysTroopChanger.Models.ATCVolunteerProductionModel
        // while the same user reported "crashes again when starting a new game with
        // ATC". A one-name probe cannot distinguish "ATC absent" from "ATC present
        // under a different type name" — both look identical in the log. Try the known
        // variants, then fall back to a scan by simple name so a namespace reshuffle in
        // a future ATC release does not silently disarm this guard.
        private static readonly string[] kAtcTypeNames =
        {
            "AdonnaysTroopChanger.Models.ATCVolunteerProductionModel",
            "AdonnaysTroopChanger.ATCVolunteerProductionModel",
            "AdonnaysTroopChanger.Models.AtcVolunteerProductionModel"
        };

        private static Type ResolveAtcModelType()
        {
            foreach (var name in kAtcTypeNames)
            {
                var t = AccessTools.TypeByName(name);
                if (t != null) return t;
            }

            // Last resort: ATC is loaded but moved. Scan only assemblies whose name
            // mentions the mod, so this stays cheap and cannot match anything else.
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var an = asm.GetName().Name;
                    if (an == null || an.IndexOf("AdonnaysTroopChanger", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name.IndexOf("VolunteerProductionModel", StringComparison.OrdinalIgnoreCase) >= 0)
                            return t;
                    }
                }
            }
            catch { }
            return null;
        }

        public static MethodBase TargetMethod()
        {
            var t = ResolveAtcModelType();
            if (t == null) return null;
            return AccessTools.Method(t, "GetDailyVolunteerProductionProbability");
        }

        public static bool Prepare()
        {
            var t = ResolveAtcModelType();
            if (t == null)
            {
                Log("ATC not present (no matching volunteer-model type) — guard skipped");
                return false;
            }
            var m = AccessTools.Method(t, "GetDailyVolunteerProductionProbability");
            if (m == null)
            {
                Log("ATC type '" + t.FullName + "' found but GetDailyVolunteerProductionProbability "
                    + "is missing — guard skipped. ATC's API changed; this needs re-checking.");
                return false;
            }
            Log("ATC detected ('" + t.FullName + "') — volunteer guard attached");
            return true;
        }

        // __0 = the notable Hero (positional binding — ATC's parameter name is not ours
        // to rely on). Returning false skips ATC's body for our heroes only.
        public static bool Prefix(Hero __0, ref float __result)
        {
            try
            {
                var cid = __0 != null && __0.Culture != null ? __0.Culture.StringId : null;
                if (cid != null && cid.StartsWith("bp_", StringComparison.Ordinal))
                {
                    __result = 0f;   // BP chiefs never produce volunteers
                    return false;
                }
            }
            catch { }
            return true;
        }

        private static void Log(string msg)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-ATC] " + msg); }
            catch { }
        }
    }
}
