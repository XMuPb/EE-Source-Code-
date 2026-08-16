using TaleWorlds.Library;

namespace BandItPlus.UI
{
    // 2026-07-01 (Bandit-King GUI, Task 3 — POLISH): one data-driven info row for
    // the reusable BanditKingBrief panel, now art-matched to BpJailbreakBrief's
    // rich intel rows. The prefab's ListPanel item template binds:
    //   Icon      — a real SPPerks sprite id (e.g. "SPPerks\\OneHandedShieldWall")
    //   AccentHex — the row's accent color, tinting the glyph
    //   BadgeHex  — the same accent dimmed to a translucent halo (accent + "77")
    //   Label     — muted uppercase caption (left)
    //   Value     — bright value (right)
    // Accent defaults to jailbreak gold (#F0CC68FF) when omitted so the older
    // 3-arg callers keep rendering unchanged.
    public class BriefRowVM : ViewModel
    {
        private const string DEFAULT_ACCENT = "#F0CC68FF";

        private string _icon, _label, _value, _accentHex, _badgeHex;

        public BriefRowVM(string icon, string label, string value)
            : this(icon, label, value, DEFAULT_ACCENT) { }

        public BriefRowVM(string icon, string label, string value, string accentHex)
        {
            _icon = icon;
            _label = label;
            _value = value;
            _accentHex = string.IsNullOrEmpty(accentHex) ? DEFAULT_ACCENT : accentHex;
            _badgeHex = MakeBadgeHex(_accentHex);
        }

        // Turn a solid "#RRGGBBAA" accent into a translucent halo "#RRGGBB77" for
        // the outer badge circle (matches the jailbreak brief's 0x77 badge alpha).
        private static string MakeBadgeHex(string accent)
        {
            try
            {
                if (string.IsNullOrEmpty(accent)) return "#F0CC6877";
                string hex = accent.StartsWith("#") ? accent.Substring(1) : accent;
                if (hex.Length >= 6) return "#" + hex.Substring(0, 6) + "77";
                return "#F0CC6877";
            }
            catch { return "#F0CC6877"; }
        }

        [DataSourceProperty] public string Icon { get { return _icon; } set { if (_icon != value) { _icon = value; OnPropertyChanged(nameof(Icon)); } } }
        [DataSourceProperty] public string Label { get { return _label; } set { if (_label != value) { _label = value; OnPropertyChanged(nameof(Label)); } } }
        [DataSourceProperty] public string Value { get { return _value; } set { if (_value != value) { _value = value; OnPropertyChanged(nameof(Value)); } } }
        [DataSourceProperty] public string AccentHex { get { return _accentHex; } set { if (_accentHex != value) { _accentHex = value; OnPropertyChanged(nameof(AccentHex)); } } }
        [DataSourceProperty] public string BadgeHex { get { return _badgeHex; } set { if (_badgeHex != value) { _badgeHex = value; OnPropertyChanged(nameof(BadgeHex)); } } }
    }

    // 2026-07-01 (visual pass): one colored category TAG chip for the header band
    // (e.g. "TOTAL WAR" / "SIEGE"). The prefab's tag ItemTemplate binds:
    //   Text      — the chip label (uppercase)
    //   AccentHex — solid accent: chip border color
    //   FillHex   — the same accent washed to ~18% alpha: chip body tint
    // Lives in this file next to BriefRowVM so no .csproj Compile entry is needed.
    public class BriefTagVM : ViewModel
    {
        private const string DEFAULT_ACCENT = "#F0CC68FF";

        private string _text, _accentHex, _fillHex;

        public BriefTagVM(string text, string accentHex)
        {
            _text = text ?? "";
            _accentHex = string.IsNullOrEmpty(accentHex) ? DEFAULT_ACCENT : accentHex;
            _fillHex = MakeFillHex(_accentHex);
        }

        // "#RRGGBBAA" accent → "#RRGGBB2E" translucent body wash.
        private static string MakeFillHex(string accent)
        {
            try
            {
                if (string.IsNullOrEmpty(accent)) return "#F0CC682E";
                string hex = accent.StartsWith("#") ? accent.Substring(1) : accent;
                if (hex.Length >= 6) return "#" + hex.Substring(0, 6) + "2E";
                return "#F0CC682E";
            }
            catch { return "#F0CC682E"; }
        }

        [DataSourceProperty] public string Text { get { return _text; } set { if (_text != value) { _text = value; OnPropertyChanged(nameof(Text)); } } }
        [DataSourceProperty] public string AccentHex { get { return _accentHex; } set { if (_accentHex != value) { _accentHex = value; OnPropertyChanged(nameof(AccentHex)); } } }
        [DataSourceProperty] public string FillHex { get { return _fillHex; } set { if (_fillHex != value) { _fillHex = value; OnPropertyChanged(nameof(FillHex)); } } }
    }
}
