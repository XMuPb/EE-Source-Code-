using System;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace BandItPlus.Patches
{
    // Wave 4.11-fix v36 (2026-05-07): canonical conversation-partner capture.
    //
    // ROOT CAUSE that earlier waves missed:
    // Dialog conditions in HideoutVendorDialog / BanditDialogManager were reading
    //   `Mission.Current.MainAgent.GetLookAgent()`
    // to identify the conversation partner. GetLookAgent returns whatever agent the
    // player's RETICLE is aimed at THIS frame — NOT the agent the engine has bound as
    // the conversation partner. When F-press fires OnAgentInteraction(player, partner),
    // the engine evaluates start conditions synchronously. During that microsecond, the
    // player's reticle may have drifted off the partner (particularly when boss/vendors
    // are stacked tight, e.g. mountain_hideout_004_sv has chief and food vendor 6.7m
    // apart with bodyguards between). Result: GetLookAgent returns the wrong agent OR
    // null → wrong dialog tree fires (or none, falling through to vanilla).
    //
    // Symptoms reported by user (2026-05-07):
    //   - "im pressing on the chief, and it pop ups the Vendors chat"
    //     (reticle drifted to nearby vendor → IsAgentFoodVendor matched → vendor dialog won)
    //   - "Vendorse NPC when clikcing on them it dosent work it still shows the vanilla chat"
    //     (reticle off vendor at eval time → no condition matched → vanilla fallback)
    //
    // Fix: Harmony prefix captures the `agent` parameter of Mission.OnAgentInteraction
    // into LastPartner. The captured agent is the canonical partner for the conversation
    // about to start. Dialog conditions read LastPartner instead of GetLookAgent.
    //
    // Auto-attached by Harmony.PatchAll() at mod load.
    [HarmonyPatch(typeof(Mission), nameof(Mission.OnAgentInteraction))]
    public static class AgentInteractionPatch
    {
        // Most-recently-captured conversation partner (the `agent` arg to OnAgentInteraction).
        public static Agent LastPartner;
        // TickCount(ms) when LastPartner was captured. Used to expire stale captures so
        // dialog conditions evaluated long after a convo ends don't see ghost partners.
        public static int LastPartnerTick;

        // CRITICAL — Harmony binds prefix args BY NAME against the original method's
        // parameter names. The actual TaleWorlds signature (verified from crash log
        // 2026-05-07) is:
        //     void OnAgentInteraction(Agent requesterAgent, Agent targetAgent,
        //                             SByte agentBoneIndex)
        // NOT (Agent userAgent, Agent agent, int distanceCode). Mismatched names →
        // "Parameter X not found" exception during Harmony.PatchAll() at mod load,
        // which crashes the game before SubModule.OnSubModuleLoad finishes.
        public static void Prefix(Agent requesterAgent, Agent targetAgent, sbyte agentBoneIndex)
        {
            try
            {
                // Only track when the player is the initiator. Other agents calling
                // OnAgentInteraction (NPC-to-NPC chatter, scripted events) shouldn't
                // overwrite the player's partner.
                if (requesterAgent == null || requesterAgent != Mission.Current?.MainAgent) return;
                LastPartner = targetAgent;
                LastPartnerTick = Environment.TickCount;
            }
            catch { /* defensive — never let this prefix break interaction */ }
        }

        // Returns the partner Agent if captured within the last `freshMs` milliseconds,
        // otherwise null. 1500ms covers the entire dialog start-condition evaluation
        // window (engine evaluates all start conditions within ~1 frame of the prefix
        // firing, so even 100ms would suffice — 1500ms is generous safety margin).
        public static Agent GetFreshPartner(int freshMs = 1500)
        {
            if (LastPartner == null) return null;
            if (Environment.TickCount - LastPartnerTick > freshMs) return null;
            return LastPartner;
        }

        // Clear capture — call from mission-end / encounter-end paths if desired.
        // Not strictly required; freshness window naturally expires the value.
        public static void Clear()
        {
            LastPartner = null;
            LastPartnerTick = 0;
        }
    }
}
