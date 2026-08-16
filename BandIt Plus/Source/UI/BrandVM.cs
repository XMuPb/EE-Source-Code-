using TaleWorlds.Library;

namespace BandItPlus.UI
{
    // Wave 4.36 — credit watermark / logo VM (BrandCredit.xml). BpJailbreakMissionController breathes the emblem
    // glow + the gold accent each tick for a living look. Numeric props bound to widget props MUST be float.
    public class BrandVM : ViewModel
    {
        private float _glow = 0.5f;
        private float _accentAlpha = 0.8f;

        [DataSourceProperty] public float Glow { get { return _glow; } set { if (value != _glow) { _glow = value; OnPropertyChangedWithValue(value, "Glow"); } } }
        [DataSourceProperty] public float AccentAlpha { get { return _accentAlpha; } set { if (value != _accentAlpha) { _accentAlpha = value; OnPropertyChangedWithValue(value, "AccentAlpha"); } } }
    }
}
