using TaleWorlds.Library;

namespace BandItPlus.UI
{
    // Wave 4.37 — premium escape OBJECTIVE tracker VM (EscapeHud.xml). Per objective: Text + marker Color +
    // active Glow + active-row Back(ing) highlight. BpJailbreakMissionController drives + pulses these.
    // NOTE: every numeric prop bound to a widget prop MUST be float (int crashes on two-way write-back).
    public class EscapeHudVM : ViewModel
    {
        private bool _show;
        private float _panelAlpha;
        private string _o1Text = "", _o2Text = "", _o3Text = "";
        private string _o1Color = "#564B31FF", _o2Color = "#564B31FF", _o3Color = "#564B31FF";
        private float _o1Glow, _o2Glow, _o3Glow;
        private float _o1Back, _o2Back, _o3Back;
        private string _hint = "";
        private string _headerInfo = "";
        private string _guardsLine = "";
        private string _rewardLine = "";
        private bool _rewardStruck;
        private string _prisonersLine = "";
        private bool _prisonersVisible;
        private string _chestLine = "";
        private bool _chestVisible;
        private float _alertAlpha;
        private float _cellmateAlpha;
        private bool _cellmateVisible;
        private string _toastText = "";
        private float _toastAlpha;
        private bool _toastVisible;
        private float _flourishAlpha;
        private bool _flourishVisible;
        private string _flourishStats = "", _flourishReward = "";
        private float _vignetteAlpha;
        private bool _vignetteVisible;
        private string _banterText = "";
        private bool _banterVisible;
        private string _warnText = "";
        private bool _warnVisible;

        [DataSourceProperty] public bool Show { get { return _show; } set { if (value != _show) { _show = value; OnPropertyChangedWithValue(value, "Show"); } } }
        [DataSourceProperty] public float PanelAlpha { get { return _panelAlpha; } set { if (value != _panelAlpha) { _panelAlpha = value; OnPropertyChangedWithValue(value, "PanelAlpha"); } } }
        [DataSourceProperty] public string O1Text { get { return _o1Text; } set { if (value != _o1Text) { _o1Text = value; OnPropertyChangedWithValue(value, "O1Text"); } } }
        [DataSourceProperty] public string O2Text { get { return _o2Text; } set { if (value != _o2Text) { _o2Text = value; OnPropertyChangedWithValue(value, "O2Text"); } } }
        [DataSourceProperty] public string O3Text { get { return _o3Text; } set { if (value != _o3Text) { _o3Text = value; OnPropertyChangedWithValue(value, "O3Text"); } } }
        [DataSourceProperty] public string O1Color { get { return _o1Color; } set { if (value != _o1Color) { _o1Color = value; OnPropertyChangedWithValue(value, "O1Color"); } } }
        [DataSourceProperty] public string O2Color { get { return _o2Color; } set { if (value != _o2Color) { _o2Color = value; OnPropertyChangedWithValue(value, "O2Color"); } } }
        [DataSourceProperty] public string O3Color { get { return _o3Color; } set { if (value != _o3Color) { _o3Color = value; OnPropertyChangedWithValue(value, "O3Color"); } } }
        [DataSourceProperty] public float O1Glow { get { return _o1Glow; } set { if (value != _o1Glow) { _o1Glow = value; OnPropertyChangedWithValue(value, "O1Glow"); } } }
        [DataSourceProperty] public float O2Glow { get { return _o2Glow; } set { if (value != _o2Glow) { _o2Glow = value; OnPropertyChangedWithValue(value, "O2Glow"); } } }
        [DataSourceProperty] public float O3Glow { get { return _o3Glow; } set { if (value != _o3Glow) { _o3Glow = value; OnPropertyChangedWithValue(value, "O3Glow"); } } }
        [DataSourceProperty] public float O1Back { get { return _o1Back; } set { if (value != _o1Back) { _o1Back = value; OnPropertyChangedWithValue(value, "O1Back"); } } }
        [DataSourceProperty] public float O2Back { get { return _o2Back; } set { if (value != _o2Back) { _o2Back = value; OnPropertyChangedWithValue(value, "O2Back"); } } }
        [DataSourceProperty] public float O3Back { get { return _o3Back; } set { if (value != _o3Back) { _o3Back = value; OnPropertyChangedWithValue(value, "O3Back"); } } }
        [DataSourceProperty] public string Hint { get { return _hint; } set { if (value != _hint) { _hint = value; OnPropertyChangedWithValue(value, "Hint"); } } }
        [DataSourceProperty] public string HeaderInfo { get { return _headerInfo; } set { if (value != _headerInfo) { _headerInfo = value; OnPropertyChangedWithValue(value, "HeaderInfo"); } } }
        [DataSourceProperty] public string GuardsLine { get { return _guardsLine; } set { if (value != _guardsLine) { _guardsLine = value; OnPropertyChangedWithValue(value, "GuardsLine"); } } }
        [DataSourceProperty] public string RewardLine { get { return _rewardLine; } set { if (value != _rewardLine) { _rewardLine = value; OnPropertyChangedWithValue(value, "RewardLine"); } } }
        [DataSourceProperty] public bool RewardStruck { get { return _rewardStruck; } set { if (value != _rewardStruck) { _rewardStruck = value; OnPropertyChangedWithValue(value, "RewardStruck"); } } }
        [DataSourceProperty] public string PrisonersLine { get { return _prisonersLine; } set { if (value != _prisonersLine) { _prisonersLine = value; OnPropertyChangedWithValue(value, "PrisonersLine"); } } }
        [DataSourceProperty] public bool PrisonersVisible { get { return _prisonersVisible; } set { if (value != _prisonersVisible) { _prisonersVisible = value; OnPropertyChangedWithValue(value, "PrisonersVisible"); } } }
        [DataSourceProperty] public string ChestLine { get { return _chestLine; } set { if (value != _chestLine) { _chestLine = value; OnPropertyChangedWithValue(value, "ChestLine"); } } }
        [DataSourceProperty] public bool ChestVisible { get { return _chestVisible; } set { if (value != _chestVisible) { _chestVisible = value; OnPropertyChangedWithValue(value, "ChestVisible"); } } }
        [DataSourceProperty] public float AlertAlpha { get { return _alertAlpha; } set { if (value != _alertAlpha) { _alertAlpha = value; OnPropertyChangedWithValue(value, "AlertAlpha"); } } }
        [DataSourceProperty] public float CellmateAlpha { get { return _cellmateAlpha; } set { if (value != _cellmateAlpha) { _cellmateAlpha = value; OnPropertyChangedWithValue(value, "CellmateAlpha"); } } }
        [DataSourceProperty] public bool CellmateVisible { get { return _cellmateVisible; } set { if (value != _cellmateVisible) { _cellmateVisible = value; OnPropertyChangedWithValue(value, "CellmateVisible"); } } }
        [DataSourceProperty] public string ToastText { get { return _toastText; } set { if (value != _toastText) { _toastText = value; OnPropertyChangedWithValue(value, "ToastText"); } } }
        [DataSourceProperty] public float ToastAlpha { get { return _toastAlpha; } set { if (value != _toastAlpha) { _toastAlpha = value; OnPropertyChangedWithValue(value, "ToastAlpha"); } } }
        [DataSourceProperty] public float FlourishAlpha { get { return _flourishAlpha; } set { if (value != _flourishAlpha) { _flourishAlpha = value; OnPropertyChangedWithValue(value, "FlourishAlpha"); } } }
        [DataSourceProperty] public bool ToastVisible { get { return _toastVisible; } set { if (value != _toastVisible) { _toastVisible = value; OnPropertyChangedWithValue(value, "ToastVisible"); } } }
        [DataSourceProperty] public bool FlourishVisible { get { return _flourishVisible; } set { if (value != _flourishVisible) { _flourishVisible = value; OnPropertyChangedWithValue(value, "FlourishVisible"); } } }
        [DataSourceProperty] public string FlourishStats { get { return _flourishStats; } set { if (value != _flourishStats) { _flourishStats = value; OnPropertyChangedWithValue(value, "FlourishStats"); } } }
        [DataSourceProperty] public string FlourishReward { get { return _flourishReward; } set { if (value != _flourishReward) { _flourishReward = value; OnPropertyChangedWithValue(value, "FlourishReward"); } } }
        [DataSourceProperty] public float VignetteAlpha { get { return _vignetteAlpha; } set { if (value != _vignetteAlpha) { _vignetteAlpha = value; OnPropertyChangedWithValue(value, "VignetteAlpha"); } } }
        [DataSourceProperty] public bool VignetteVisible { get { return _vignetteVisible; } set { if (value != _vignetteVisible) { _vignetteVisible = value; OnPropertyChangedWithValue(value, "VignetteVisible"); } } }
        [DataSourceProperty] public string BanterText { get { return _banterText; } set { if (value != _banterText) { _banterText = value; OnPropertyChangedWithValue(value, "BanterText"); } } }
        [DataSourceProperty] public bool BanterVisible { get { return _banterVisible; } set { if (value != _banterVisible) { _banterVisible = value; OnPropertyChangedWithValue(value, "BanterVisible"); } } }
        [DataSourceProperty] public string WarnText { get { return _warnText; } set { if (value != _warnText) { _warnText = value; OnPropertyChangedWithValue(value, "WarnText"); } } }
        [DataSourceProperty] public bool WarnVisible { get { return _warnVisible; } set { if (value != _warnVisible) { _warnVisible = value; OnPropertyChangedWithValue(value, "WarnVisible"); } } }
    }
}
