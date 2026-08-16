using TaleWorlds.Library;

namespace BandItPlus.UI
{
    // Wave 4.33 — tiny VM for the persistent bottom-center "[F] Pick the lock" prompt (LockpickPrompt.xml).
    // BpJailbreakMissionController toggles Show based on whether the player is facing the locked cell door.
    public class LockpickPromptVM : ViewModel
    {
        private bool _show;
        private string _label = BandItPlus.Localization.Get("bp_lockpickpromptvm_001", "Pick the lock");
        [DataSourceProperty] public bool Show { get { return _show; } set { if (value != _show) { _show = value; OnPropertyChangedWithValue(value, "Show"); } } }
        [DataSourceProperty] public string Label { get { return _label; } set { if (value != _label) { _label = value; OnPropertyChangedWithValue(value, "Label"); } } }
    }
}
