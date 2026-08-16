using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BandItPlus.Patches
{
    // Wave 4.26.0 (2026-06-17): when the player is at PEACE with a bandit culture, HIDE
    // the vanilla hideout COMBAT options on the "hideout_place" menu — "Sneak in now",
    // "Assault hideout", "Wait until nightfall to sneak in", "Send troops to clear". We
    // postfix each OPTION's vanilla on-condition and force it false when the camp
    // welcomes the player; only BP's "Visit the camp." + the vanilla "Leave" remain.
    // Gate: BanditDialogManager.ShouldSuppressHideoutCombat() (reuses BP's peace logic).
    //
    // IMPORTANT (2026-06-17 fix): the WAIT option's condition is
    // "game_menu_wait_until_nightfall_on_condition" (the OPTION), NOT
    // "hideout_wait_menu_on_condition" (the wait-MENU's own condition) — patching the
    // latter left the option visible. Names verified from CampaignSystem.dll metadata;
    // resolved at runtime (HideoutCampaignBehavior first, then a whole-assembly fallback
    // since the wait option may live in a different behavior type) and each attach logged.
    [HarmonyPatch]
    public static class HideoutCombatOptionPatch
    {
        private static readonly string[] Conditions =
        {
            "game_menu_hideout_sneak_in_on_condition",        // Sneak in now
            "game_menu_assault_hideout_parties_on_condition", // Assault hideout
            "game_menu_wait_until_nightfall_on_condition",    // Wait until nightfall to sneak in
            "game_menu_send_troops_hideout_on_condition",     // Send troops to clear
        };

        static IEnumerable<MethodBase> TargetMethods()
        {
            var hideoutType = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.HideoutCampaignBehavior")
                              ?? AccessTools.TypeByName("HideoutCampaignBehavior");
            var campaignAsm = typeof(TaleWorlds.CampaignSystem.Campaign).Assembly;

            foreach (var name in Conditions)
            {
                MethodBase m = (hideoutType != null) ? AccessTools.Method(hideoutType, name) : null;
                if (m == null) m = FindInAssembly(campaignAsm, name); // option may sit on another behavior

                if (m != null) { Log("patching " + (m.DeclaringType != null ? m.DeclaringType.Name : "?") + "." + name); yield return m; }
                else Log("condition NOT found anywhere: " + name);
            }
        }

        // Search the CampaignSystem assembly for a method by exact name (private menu
        // callbacks aren't all on HideoutCampaignBehavior). Only runs in-game where every
        // dependency is loaded, so GetTypes() is safe here.
        private static MethodBase FindInAssembly(Assembly asm, string name)
        {
            try
            {
                foreach (var t in asm.GetTypes())
                {
                    var m = AccessTools.Method(t, name);
                    if (m != null) return m;
                }
            }
            catch { }
            return null;
        }

        // Force the combat option hidden when the camp is at peace with the player.
        static void Postfix(ref bool __result)
        {
            try
            {
                if (!__result) return; // already hidden/disabled by vanilla
                // 07-11 storyline guard: never hide combat options while the tutorial
                // runs — the story's own hideout assault must stay attackable.
                if (BpStoryGuard.StoryTutorialActive()) return;
                if (BandItPlus.BanditDialogManager.ShouldSuppressHideoutCombat())
                    __result = false;
            }
            catch { }
        }

        private static void Log(string msg)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-HideoutCombat] " + msg); }
            catch { }
        }
    }
}
