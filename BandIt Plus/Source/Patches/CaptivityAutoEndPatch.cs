using System.Reflection;
using HarmonyLib;

namespace BandItPlus.Patches
{
    // Wave 4.31.0 — while BP captivity is active, suppress vanilla's wilderness auto-release
    // (PlayerCaptivityCampaignBehavior.CheckCaptivityChange) so the player stays captive through
    // the march + jail; BP ends captivity itself. Defensive: skips if the target isn't found,
    // so a name change can't abort PatchAll (see feedback_bannerlord_harmony_attach_log).
    [HarmonyPatch]
    public static class CaptivityAutoEndPatch
    {
        private static MethodBase Target()
        {
            var t = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.PlayerCaptivityCampaignBehavior");
            return t == null ? null : AccessTools.Method(t, "CheckCaptivityChange");
        }

        public static bool Prepare() { return Target() != null; }
        public static MethodBase TargetMethod() { return Target(); }

        public static bool Prefix()
        {
            var b = BandItPlus.Captivity.BanditCaptivityBehavior.Instance;
            return !(b != null && b.Active); // false = skip vanilla release while BP captivity active
        }
    }
}
