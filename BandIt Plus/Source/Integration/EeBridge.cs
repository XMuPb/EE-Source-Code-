using System;
using System.Reflection;
using BandItPlus.HideoutVisit;

namespace BandItPlus.Integration
{
    // Optional soft-dependency bridge to the EditableEncyclopedia ("EE-Core") mod. Everything is
    // by reflection so BandIt Plus never compile-references EE's assembly — a hard reference would
    // TypeLoad-fail whenever EE is absent. `Available` is false when EE isn't loaded (or no campaign
    // is active), and callers fall back to BandIt Plus's own inline chronicle rendering.
    public static class EeBridge
    {
        private static bool _resolved;
        private static PropertyInfo _isAvailable;      // static bool EditableEncyclopediaAPI.IsAvailable
        private static MethodInfo _addJournalEntry;    // static void AddJournalEntry(string objectId, string text)

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    t = asm.GetType("EditableEncyclopedia.EditableEncyclopediaAPI", false);
                    if (t != null) break;
                }
                if (t == null) return; // EE not installed — stays unavailable, fallback path used
                _isAvailable = t.GetProperty("IsAvailable", BindingFlags.Public | BindingFlags.Static);
                _addJournalEntry = t.GetMethod("AddJournalEntry", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string), typeof(string) }, null);
                HideoutPeacefulVisitState.Log("[BP-EeBridge] EE-Core resolved: IsAvailable="
                    + (_isAvailable != null) + " AddJournalEntry=" + (_addJournalEntry != null));
            }
            catch (Exception ex)
            {
                _isAvailable = null; _addJournalEntry = null;
                HideoutPeacefulVisitState.Log("[BP-EeBridge] resolve failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // True only when EE is loaded AND its IsAvailable reports a live campaign.
        public static bool Available
        {
            get
            {
                Resolve();
                if (_isAvailable == null || _addJournalEntry == null) return false;
                try { return (bool)_isAvailable.GetValue(null); }
                catch { return false; }
            }
        }

        public static void AddJournalEntry(string objectId, string text)
        {
            Resolve();
            if (_addJournalEntry == null || string.IsNullOrEmpty(objectId) || string.IsNullOrEmpty(text)) return;
            try { _addJournalEntry.Invoke(null, new object[] { objectId, text }); }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("[BP-EeBridge] AddJournalEntry: " + ex.GetType().Name + ": " + ex.Message); }
        }
    }
}
