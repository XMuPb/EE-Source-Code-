using System;
using System.IO;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BandItPlus.HideoutVisit
{
    // Shared state + entry helper for Wave 3c.4 (walkable hideout, Phase 1).
    // PHASE 1 SCOPE: Walk in, no enemies spawn (best-effort via Harmony patch),
    // player can move around, leaves via mission/Esc. NPCs come in Phase 2.
    public static class HideoutPeacefulVisitState
    {
        public static bool Active;

        // Wave 5 (Alliance Ch3): walkable-peaceful path does NOT fire CampaignEvents.SettlementEntered,
        // so BanditAllianceBehavior can't otherwise detect entry. Expose an event the behavior
        // subscribes to. Per spec §4 Ch3 trigger B + memory note bannerlord-harmony-attach-log.
        // Raised by BanditDialogManager immediately AFTER it sets Active = true on the walkable
        // peaceful entry path. Subscribers MUST be exception-safe — the raise site swallows
        // subscriber throws so activation never breaks.
        public static event Action<Settlement> WalkableActivated;

        // Raises WalkableActivated with a try/catch barrier so a subscriber throw cannot break
        // the walkable-peaceful activation path. Call EXACTLY ONCE per walkable entry, AFTER
        // Active = true is set + TargetHideout is assigned.
        public static void RaiseWalkableActivated(Settlement settlement)
        {
            try
            {
                WalkableActivated?.Invoke(settlement);
            }
            catch (Exception ex)
            {
                Log("[HideoutPeacefulVisitState] WalkableActivated subscriber threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }
        // v2 (2026-05-25): set TRUE by HideoutCinemaIntroMissionView while its
        // narrative layer is mounted; FALSE on dismiss. BanditCampPanelMissionView
        // gates IsPanelVisible on this so the camp HUD doesn't overlap the
        // cinematic story text.
        public static bool CinematicActive;
        public static Settlement TargetHideout;
        // Stored at module load by SubModuleClassEntry. Used by HideoutVisitNpcSpawnBehavior
        // for dynamic Patch/Unpatch of AnimationPoint.SetAgentItemsVisibility — scoped to the
        // peaceful mission window so encyclopedia preview is never affected.
        public static HarmonyLib.Harmony HarmonyInstance;

        public static void Log(string msg)
        {
            try
            {
                // release builds only keep error-ish lines (BpDiag.ShouldWrite);
                // dev builds log everything.
                if (!BandItPlus.Diagnostics.BpDiag.ShouldWrite(msg)) return;
                File.AppendAllText(BandItPlus.Diagnostics.BpDiag.P("bp_peace_debug.log"),
                    "[" + DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + "] HideoutVisit: " + msg + Environment.NewLine);
            }
            catch { }
        }

        // BUG M8 + M10 + M11 FIX (2026-04-30): The entire `Open()` method, `BuildPeacefulMissionBehaviors`,
        // and `ResolveTypeAcrossLoadedAssemblies` were DEAD CODE. The actual peaceful-hideout entry path
        // is `BanditDialogManager` setting `Active = true` directly + `WalkableHideoutEncounter`
        // (CreateLocationEncounterPatch swaps it in) calling `CampaignMission.OpenVillageMission`. The
        // hand-rolled `MissionState.OpenNew` path with reflection-built behavior list was the older
        // Phase 4.5.1 architecture that was abandoned for the village-mission-stack approach. Keeping
        // the dead method risked: (M8) Active flag leaking on early-bail, (M10) Activator failures
        // silently dropping critical handlers, (M11) double-mission-state corruption if both paths
        // ran concurrently. Removing it entirely eliminates all three risk classes.

        public static void Reset()
        {
            Log("Reset() — clearing peaceful flag");
            Active = false;
            TargetHideout = null;
        }

        // T35 (Wave 5 alliance): canonical predicate for any code that needs to gate
        // aggression vs peaceful entry. Returns TRUE when the player should be allowed
        // peaceful passage regardless of normal hostility checks — i.e. the encounter
        // settlement's culture is currently allied via BanditAllianceBehavior.
        //
        // Safe to call from any thread / any phase. Returns FALSE on any exception so
        // a malformed encounter state never accidentally suppresses vanilla hostility.
        //
        // Call sites: aggression-decision points in the hideout-entry pipeline (menu
        // hostility prompts, bandit-start dialog conditions, mission spawn gates).
        // The plan's bypass snippet inverted — short-circuits aggression to FALSE
        // (== "no aggression, allow peaceful path").
        public static bool ShouldBypassAggression(Settlement settlement)
        {
            try
            {
                var cultureId = settlement?.Culture?.StringId;
                if (string.IsNullOrEmpty(cultureId)) return false;

                var alliance = BandItPlus.Behaviors.BanditAllianceBehavior.Instance;
                if (alliance == null) return false;

                if (alliance.IsAllied(cultureId))
                {
                    Log("[BP-Alliance] ShouldBypassAggression=true for allied culture " + cultureId);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log("[BP-Alliance] ShouldBypassAggression threw "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // T35 convenience overload — resolves the encounter settlement automatically.
        // Use this from contexts where the settlement isn't already in scope (dialog
        // conditions, menu-init callbacks). Returns FALSE if no encounter is active.
        public static bool ShouldBypassAggression()
        {
            try
            {
                return ShouldBypassAggression(PlayerEncounter.EncounterSettlement);
            }
            catch (Exception ex)
            {
                Log("[BP-Alliance] ShouldBypassAggression() overload threw "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // ResolveSceneName(Settlement) removed (M11 cleanup) — was only called by the deleted Open()
        // method. WalkableHideoutEncounter.CreateAndOpenMissionController now uses Location.GetSceneName(0)
        // directly via the village-mission framework.
    }
}
