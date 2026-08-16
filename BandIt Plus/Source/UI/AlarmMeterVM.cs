using TaleWorlds.Library;

namespace BandItPlus.UI
{
    // Wave 4.34 — custom "spotted" detection meter (AlarmMeter.xml). Top-center eye + fill bar that
    // rises while a guard has line of sight to the player. Driven each tick by BpJailbreakMissionController.
    // NOTE: every numeric prop bound to a widget prop MUST be float (int crashes on two-way write-back).
    public class AlarmMeterVM : ViewModel
    {
        private bool _show;
        private float _containerAlpha;        // 0..1 panel fade
        private float _fillWidth;             // px of the detection fill (0..track width)
        private float _eyeAlpha;              // iris/glow strength 0..1
        private string _tint = "#C8A24EFF";   // gold default -> amber -> red
        private string _state = "";           // "", "SUSPICIOUS", "SPOTTED"

        [DataSourceProperty] public bool Show { get { return _show; } set { if (value != _show) { _show = value; OnPropertyChangedWithValue(value, "Show"); } } }
        [DataSourceProperty] public float ContainerAlpha { get { return _containerAlpha; } set { if (value != _containerAlpha) { _containerAlpha = value; OnPropertyChangedWithValue(value, "ContainerAlpha"); } } }
        [DataSourceProperty] public float FillWidth { get { return _fillWidth; } set { if (value != _fillWidth) { _fillWidth = value; OnPropertyChangedWithValue(value, "FillWidth"); } } }
        [DataSourceProperty] public float EyeAlpha { get { return _eyeAlpha; } set { if (value != _eyeAlpha) { _eyeAlpha = value; OnPropertyChangedWithValue(value, "EyeAlpha"); } } }
        [DataSourceProperty] public string Tint { get { return _tint; } set { if (value != _tint) { _tint = value; OnPropertyChangedWithValue(value, "Tint"); } } }
        [DataSourceProperty] public string State { get { return _state; } set { if (value != _state) { _state = value; OnPropertyChangedWithValue(value, "State"); } } }

        // 2026-07-03 sneak-in Shadow Bar extras (SneakShadowBar.xml). Additive —
        // the jailbreak/alarm movies don't bind these, so they are unaffected.
        private string _watchersText = "";
        private string _hintText = "";
        [DataSourceProperty] public string WatchersText { get { return _watchersText; } set { if (value != _watchersText) { _watchersText = value; OnPropertyChangedWithValue(value, "WatchersText"); } } }
        [DataSourceProperty] public string HintText { get { return _hintText; } set { if (value != _hintText) { _hintText = value; OnPropertyChangedWithValue(value, "HintText"); } } }
    }
}
