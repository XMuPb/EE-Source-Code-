using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace BandItPlus.Patches
{
    // 07-11 storyline guard (player reports): BP treated the main-quest tutorial's
    // scripted parties like ordinary bandits — the backup fold DESTROYED the second
    // Radagos raider party (quest bricked), the peace override broke the raider
    // encounters, and the spoils popup swallowed the story's next-step notification.
    // Central predicates for every system that must stand down. StoryMode types are
    // reached via reflection so the assembly reference stays optional.
    public static class BpStoryGuard
    {
        private static bool _resolved;
        private static PropertyInfo _current;      // StoryModeManager.Current (static)
        private static PropertyInfo _mainStory;    // StoryModeManager.MainStoryLine
        private static PropertyInfo _tutorial;     // MainStoryLine.TutorialPhase
        private static PropertyInfo _completed;    // TutorialPhase.IsCompleted
        private static PropertyInfo _skipped;      // TutorialPhase.IsSkipped

        // 1s memo — the peace patches sit on a ~580 calls/sec path. Never cache
        // longer: the session can load a different save without a process restart.
        private static bool _memoVal;
        private static DateTime _memoAt = DateTime.MinValue;

        // True while a StoryMode campaign is inside the tutorial chain (brother,
        // healer, Radagos' raiders + hideout). Sandbox / finished / skipped → false.
        public static bool StoryTutorialActive()
        {
            try
            {
                if ((DateTime.Now - _memoAt).TotalSeconds < 1.0) return _memoVal;
                _memoAt = DateTime.Now;
                _memoVal = Compute();
                return _memoVal;
            }
            catch { return false; }
        }

        private static bool Compute()
        {
            Resolve();
            if (_current == null) return false;
            object mgr = _current.GetValue(null);
            if (mgr == null) return false;                      // Sandbox campaign
            object story = _mainStory != null ? _mainStory.GetValue(mgr) : null;
            object tut = story != null && _tutorial != null ? _tutorial.GetValue(story) : null;
            if (tut == null) return false;
            if (_completed != null && (bool)_completed.GetValue(tut)) return false;
            if (_skipped != null && (bool)_skipped.GetValue(tut)) return false;
            return true;
        }

        // Any party in this map event that a quest is tracking?
        public static bool EventHasQuestParty(MapEvent mapEvent)
        {
            try
            {
                if (mapEvent == null) return false;
                return SideHasQuestParty(mapEvent.AttackerSide) || SideHasQuestParty(mapEvent.DefenderSide);
            }
            catch { return false; }
        }

        private static bool SideHasQuestParty(MapEventSide side)
        {
            if (side == null || side.Parties == null) return false;
            foreach (var ep in side.Parties)
            {
                var mp = ep != null && ep.Party != null ? ep.Party.MobileParty : null;
                if (mp != null && mp.IsCurrentlyUsedByAQuest) return true;
            }
            return false;
        }

        public static bool IsQuestParty(MobileParty p)
        {
            try { return p != null && p.IsCurrentlyUsedByAQuest; }
            catch { return false; }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var mgrType = AccessTools.TypeByName("StoryMode.StoryModeManager");
                if (mgrType == null)
                {
                    Log("StoryMode assembly not loaded — guard stays idle");
                    return;
                }
                _current = mgrType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                _mainStory = mgrType.GetProperty("MainStoryLine");
                _tutorial = _mainStory != null ? _mainStory.PropertyType.GetProperty("TutorialPhase") : null;
                var tutType = _tutorial != null ? _tutorial.PropertyType : null;
                _completed = tutType != null ? tutType.GetProperty("IsCompleted") : null;
                _skipped = tutType != null ? tutType.GetProperty("IsSkipped") : null;
                Log("resolved Current=" + (_current != null) + " MainStoryLine=" + (_mainStory != null)
                    + " TutorialPhase=" + (_tutorial != null) + " IsCompleted=" + (_completed != null)
                    + " IsSkipped=" + (_skipped != null));
            }
            catch (Exception ex)
            {
                Log("resolve failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Log(string msg)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-StoryGuard] " + msg); }
            catch { }
        }
    }
}
