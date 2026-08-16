using System;
using SandBox.ViewModelCollection.Missions.NameMarker;
using TaleWorlds.MountAndBlade;

namespace BandItPlus.HideoutVisit
{
    // Native vanilla Alt-nameplate driver for peaceful hideouts.
    //
    // ARCHITECTURE: Pushes a context onto SandBox's MissionNameMarkerFactory stack and
    // registers BanditCampNameMarkerProvider — which feeds Boss + Vendor agents into the
    // SAME nameplate widget vanilla uses for village Notables (NameMarker.xml driven by
    // MissionNameMarkerVM, surfaced by MissionGauntletNameMarkerView). The widget already
    // gates visibility off the Alt key (IsAltPressed → IsEnabled), already projects
    // world→screen positions, and already styles labels in the vanilla aesthetic. We
    // contribute zero pixels of UI — the game does it all.
    //
    // LIFECYCLE: Push in OnBehaviorInitialize (when mission.AddMissionBehavior(this) fires
    // from HideoutVisitNpcSpawnBehavior at spawn time), pop in OnRemoveBehavior (when the
    // mission tears down). Context ID "BandItPlus.BanditCamp" is the handle.
    //
    // PRIOR IMPL (chat-dump): polled InputKey.LeftAlt and dumped names via
    // InformationManager.DisplayMessage every 1.5s. Worked but non-vanilla and verbose.
    // Replaced now that the proper Gauntlet path was uncovered:
    //   - MissionNameMarkerFactory.PushContext(name, addDefaultProviders) → INameMarkerProviderContext
    //   - INameMarkerProviderContext.AddProvider<T>() where T : MissionNameMarkerProvider
    //   - MissionNameMarkerFactory.PopContext(context) on teardown
    //
    // addDefaultProviders=true keeps any vanilla village provider lookups active alongside
    // ours — they find no Notables in our scene so they're effectively no-ops, but we're
    // belt-and-braces in case future updates add useful default markers.
    public class HideoutAltLabelBehavior : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        private const string kContextId = "BandItPlus.BanditCamp";
        private MissionNameMarkerFactory.INameMarkerProviderContext _context;

        public override void OnBehaviorInitialize()
        {
            base.OnBehaviorInitialize();
            try
            {
                // BUG H8 FIX (2026-04-30): Defensive cleanup of any leftover context from a
                // previously-aborted mission. If the player saved-and-quit mid-hideout, or the
                // app crashed before OnEndMission/OnRemoveBehavior fired, our context stays on
                // the factory stack. Without this pre-push pop, a second hideout visit pushes
                // a duplicate "BandItPlus.BanditCamp" context — vanilla allows it, but the
                // pop-by-instance code at teardown only pops ONE, leaking the other forever.
                // Pop-by-ID is a safety net independent of our own _context tracking.
                try { MissionNameMarkerFactory.PopContext(kContextId); }
                catch { /* nothing to pop is fine */ }

                // Wave 4.11-fix v29 (2026-05-07): addDefaultProviders flipped to false.
                // Was true on the assumption that vanilla's default providers would find
                // no Notables in our scene — true when the chief was a generic
                // CharacterObject. Wave 4.11 v25 changed that: chief now spawns as a real
                // Hero, so vanilla's default provider DOES register a nameplate for him,
                // alongside our BanditCampNameMarkerProvider → DUPLICATE CHIEF NAMEPLATES
                // when player holds Alt. Switching to false makes our provider the sole
                // source.
                _context = MissionNameMarkerFactory.PushContext(kContextId, addDefaultProviders: false);
                if (_context == null)
                {
                    HideoutPeacefulVisitState.Log("HideoutAltLabelBehavior: PushContext returned null — native nameplates will not register");
                    return;
                }
                _context.AddProvider<BanditCampNameMarkerProvider>();
                HideoutPeacefulVisitState.Log("HideoutAltLabelBehavior: pushed context '" + kContextId + "' and added BanditCampNameMarkerProvider");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("HideoutAltLabelBehavior.OnBehaviorInitialize EXCEPTION: " + ex.Message);
            }
        }

        protected override void OnEndMission()
        {
            base.OnEndMission();
            PopOnce();
        }

        public override void OnRemoveBehavior()
        {
            PopOnce();
            base.OnRemoveBehavior();
        }

        private void PopOnce()
        {
            if (_context == null) return;
            try
            {
                MissionNameMarkerFactory.PopContext(_context);
                HideoutPeacefulVisitState.Log("HideoutAltLabelBehavior: popped context '" + kContextId + "'");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("HideoutAltLabelBehavior.PopOnce EXCEPTION: " + ex.Message);
            }
            finally
            {
                _context = null;
            }
        }
    }
}
