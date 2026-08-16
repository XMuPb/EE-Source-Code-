using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.ComponentInterfaces;
using BandItPlus.HideoutVisit;

namespace BandItPlus.Patches
{
    // v167 (2026-05-17): bandit blows that land on a mount deal extra damage,
    // so a mounted enemy's horse drops faster and the rider ends up on foot.
    //
    // This is the moddable answer to "make the AI dismount the player". The AI
    // CANNOT be told to AIM at a horse — agent melee target selection is native
    // (C++) and no battle-AI mod (RBM, ImprovedCombatAI) overrides it. But the
    // damage a hit deals IS moddable: AgentApplyDamageModel.CalculateDamage
    // returns the final post-armour blow magnitude the engine applies. Scaling
    // that return value up whenever a bandit's hit happens to land on a mount
    // means every stray swing at a horse counts for more — mounts go down,
    // riders drop on foot, and no native aiming had to be touched. When a horse
    // dies the engine dismounts its rider automatically, so this delivers the
    // original request indirectly.
    //
    // CalculateDamage is a non-virtual method on the base AgentApplyDamageModel;
    // SandboxAgentApplyDamageModel (the campaign model) inherits it unchanged,
    // so patching the base method covers the campaign path.
    //
    // Auto-attached by Harmony.PatchAll() in SubModuleClassEntry.
    [HarmonyPatch(typeof(AgentApplyDamageModel), "CalculateDamage")]
    public static class BanditMountDamagePatch
    {
        // Logged once per launch — just enough to confirm in debug.log that the
        // patch is live and firing (matches TacticBehaviorWeightPatch's style).
        private static bool _logged;

        private static void Postfix(ref AttackInformation attackInformation, ref float __result)
        {
            try
            {
                if (__result <= 0f) return; // nothing to scale

                // The blow must have landed on a mount, and must not be a bandit
                // striking one of their own horses.
                if (!attackInformation.IsVictimAgentMount) return;
                if (attackInformation.IsFriendlyFire) return;

                // Attacker must be a bandit. Vanilla bandits, looters, and the
                // 8 BandIt Plus clans all carry Occupation.Bandit; troops a
                // bandit party has recruited via Bandit Power do not — and a
                // hired regular isn't really "the bandits".
                if (!(attackInformation.AttackerAgentCharacter is CharacterObject co)
                    || co.Occupation != Occupation.Bandit)
                    return;

                var s = MCMSettings.Instance;
                if (s == null || !s.EnableMod || !s.EnableBanditPower) return;

                float mult = s.BanditMountDamageMultiplier;
                if (mult <= 1.0f) return; // feature dialled off in MCM

                __result *= mult;

                if (!_logged)
                {
                    _logged = true;
                    HideoutPeacefulVisitState.Log(
                        "v167 BanditMountDamage: first scaled bandit hit on a mount (x"
                        + mult + ")");
                }
            }
            catch { /* never throw out of a Harmony patch */ }
        }
    }
}
