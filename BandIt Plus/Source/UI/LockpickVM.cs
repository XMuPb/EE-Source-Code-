using TaleWorlds.Library;

namespace BandItPlus.UI
{
    // Wave 4.34 — bindable contract for the Skyrim-style lockpick GUI (Lockpick.xml). Driven each tick by
    // BpJailbreakMissionController. The pick is a REAL rotating needle (Rotation="@PickAngle"); the keyhole
    // glow gives warmer/colder feedback under tension (GlowAlpha + GlowColor); CYLINDER/PICK bars show turn +
    // pick health. All numeric props are float — Gauntlet widget numerics are float two-way bindings.
    public class LockpickVM : ViewModel
    {
        private string _title = BandItPlus.Localization.Get("bp_lockpickvm_001", "PICK THE LOCK");
        private string _hint = BandItPlus.Localization.Get("bp_lockpickvm_002", "A / D  rotate   ·   hold LMB  tension   ·   Esc  cancel");
        private string _status = "";
        private float _pickAngle;          // pick needle rotation (degrees) -> Widget.Rotation
        private float _wrenchAngle;        // tension wrench tilt (degrees)
        private float _turnFill;           // cylinder bar fill (px)
        private float _healthFill;         // pick health bar fill (px)
        private string _healthColor = "#7BE08AFF";
        private float _glowAlpha;          // keyhole glow strength 0..1
        private string _glowColor = "#C8A24EFF"; // keyhole glow tint (gold far -> green near sweet spot)

        [DataSourceProperty] public string Title { get { return _title; } set { if (value != _title) { _title = value; OnPropertyChangedWithValue(value, "Title"); } } }
        [DataSourceProperty] public string Hint { get { return _hint; } set { if (value != _hint) { _hint = value; OnPropertyChangedWithValue(value, "Hint"); } } }
        [DataSourceProperty] public string Status { get { return _status; } set { if (value != _status) { _status = value; OnPropertyChangedWithValue(value, "Status"); } } }
        [DataSourceProperty] public float PickAngle { get { return _pickAngle; } set { if (value != _pickAngle) { _pickAngle = value; OnPropertyChangedWithValue(value, "PickAngle"); } } }
        [DataSourceProperty] public float WrenchAngle { get { return _wrenchAngle; } set { if (value != _wrenchAngle) { _wrenchAngle = value; OnPropertyChangedWithValue(value, "WrenchAngle"); } } }
        [DataSourceProperty] public float TurnFill { get { return _turnFill; } set { if (value != _turnFill) { _turnFill = value; OnPropertyChangedWithValue(value, "TurnFill"); } } }
        [DataSourceProperty] public float HealthFill { get { return _healthFill; } set { if (value != _healthFill) { _healthFill = value; OnPropertyChangedWithValue(value, "HealthFill"); } } }
        [DataSourceProperty] public string HealthColor { get { return _healthColor; } set { if (value != _healthColor) { _healthColor = value; OnPropertyChangedWithValue(value, "HealthColor"); } } }
        [DataSourceProperty] public float GlowAlpha { get { return _glowAlpha; } set { if (value != _glowAlpha) { _glowAlpha = value; OnPropertyChangedWithValue(value, "GlowAlpha"); } } }
        [DataSourceProperty] public string GlowColor { get { return _glowColor; } set { if (value != _glowColor) { _glowColor = value; OnPropertyChangedWithValue(value, "GlowColor"); } } }
    }
}
