using System;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Settlements;
using BandItPlus.HideoutVisit;

namespace BandItPlus.BanditRaid
{
    // Forces a settlement rebellion via the engine's private RebellionsCampaignBehavior.StartRebellionEvent(Settlement).
    // Reflection-cached + version-safe (mirrors BanditWarbandFactory.MoveToPoint). MoreSettlementAction's
    // PlayerRebellionPatch invokes the same private method, confirming it works in 1.4.x. On any failure the
    // caller falls back to the devastation "sack" — this never crashes.
    public static class BanditRebellionTrigger
    {
        private static bool _probed;
        private static object _behavior;        // RebellionsCampaignBehavior instance
        private static MethodInfo _startMethod; // private StartRebellionEvent(Settlement)

        public static bool TryStartRebellion(Settlement settlement)
        {
            if (settlement == null) return false;
            // 2026-07-02 crash guard: never force a rebellion (ownership flip) while a battle
            // is active at the settlement — same engaged-garrison detach NRE class as the
            // Makeb siege crash (the PRIOR session died on THIS path at Usanc Castle, 23:55:50).
            try
            {
                if (settlement.Party != null && settlement.Party.MapEvent != null)
                {
                    Log("rebellion DEFERRED: battle in progress at " + (settlement.Name != null ? settlement.Name.ToString() : "?"));
                    return false;
                }
            }
            catch { }
            try
            {
                if (!_probed) Probe();
                if (_behavior == null || _startMethod == null)
                {
                    Log("StartRebellionEvent unavailable — caller uses devastation fallback");
                    return false;
                }
                _startMethod.Invoke(_behavior, new object[] { settlement });
                Log("forced rebellion at " + (settlement.Name != null ? settlement.Name.ToString() : "?"));
                return true;
            }
            catch (Exception ex) { Log("TryStartRebellion threw " + ex.GetType().Name + ": " + ex.Message); return false; }
        }

        // Resolve the reflection target up-front (call at session launch) so the log confirms the force path
        // is armed immediately — no need to wait for a blockade to reach its window-end.
        public static void WarmUp()
        {
            try { if (!_probed) Probe(); } catch { }
        }

        private static void Probe()
        {
            _probed = true;
            try
            {
                _behavior = Campaign.Current?.GetCampaignBehavior<RebellionsCampaignBehavior>();
                if (_behavior == null) { Log("RebellionsCampaignBehavior not found in campaign"); return; }
                _startMethod = _behavior.GetType().GetMethod("StartRebellionEvent",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null, new[] { typeof(Settlement) }, null);
                Log(_startMethod != null
                    ? "StartRebellionEvent(Settlement) resolved"
                    : "StartRebellionEvent(Settlement) NOT found on " + _behavior.GetType().Name);
            }
            catch (Exception ex) { Log("Probe threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void Log(string msg)
        {
            try { HideoutPeacefulVisitState.Log("[BP-Blockade] " + msg); } catch { }
        }
    }
}
