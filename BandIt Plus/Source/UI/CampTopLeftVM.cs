using TaleWorlds.Library;
using TaleWorlds.Core;

namespace BandItPlus.UI
{
    // Wave 4.12-fix v93 (2026-05-13): Top-left composite widget.
    //   Section A — Active Quest Tracker (food + gear vendor quest summary)
    //   Section B — Quick-Action Bar (4 stub buttons that fire explanatory popups
    //               in Phase 1; real action wiring deferred to Phase 2 so we don't
    //               accidentally bypass the walkable-hideout immersion design)
    //
    // Animation properties (TopLeftAlpha / TopLeftLeftMargin) mirror the main info
    // panel's BackdropAlpha / PanelLeftMargin so the same OnMissionTick easing
    // pattern from CampInfoPanelMissionView can be reused verbatim.
    public class CampTopLeftVM : ViewModel
    {
        private string _foodQuestLine = BandItPlus.Localization.Get("bp_camptopleftvm_001", "No active food quest");
        private string _gearQuestLine = BandItPlus.Localization.Get("bp_camptopleftvm_002", "No active gear quest");
        private string _foodTierIcon = "General\\TroopTierIcons\\icon_tier_1";
        private string _gearTierIcon = "General\\TroopTierIcons\\icon_tier_1";
        private bool _isFoodQuestActive;
        private bool _isGearQuestActive;
        private bool _hasNoQuestsActive = true;
        private float _topLeftAlpha;
        private float _topLeftLeftMargin = -360f;

        [DataSourceProperty] public string FoodQuestLine
        {
            get => _foodQuestLine;
            set { if (value == _foodQuestLine) return; _foodQuestLine = value; OnPropertyChangedWithValue(value, nameof(FoodQuestLine)); }
        }
        [DataSourceProperty] public string GearQuestLine
        {
            get => _gearQuestLine;
            set { if (value == _gearQuestLine) return; _gearQuestLine = value; OnPropertyChangedWithValue(value, nameof(GearQuestLine)); }
        }
        [DataSourceProperty] public string FoodTierIcon
        {
            get => _foodTierIcon;
            set { if (value == _foodTierIcon) return; _foodTierIcon = value; OnPropertyChangedWithValue(value, nameof(FoodTierIcon)); }
        }
        [DataSourceProperty] public string GearTierIcon
        {
            get => _gearTierIcon;
            set { if (value == _gearTierIcon) return; _gearTierIcon = value; OnPropertyChangedWithValue(value, nameof(GearTierIcon)); }
        }
        [DataSourceProperty] public bool IsFoodQuestActive
        {
            get => _isFoodQuestActive;
            set { if (value == _isFoodQuestActive) return; _isFoodQuestActive = value; OnPropertyChangedWithValue(value, nameof(IsFoodQuestActive)); }
        }
        [DataSourceProperty] public bool IsGearQuestActive
        {
            get => _isGearQuestActive;
            set { if (value == _isGearQuestActive) return; _isGearQuestActive = value; OnPropertyChangedWithValue(value, nameof(IsGearQuestActive)); }
        }
        [DataSourceProperty] public bool HasNoQuestsActive
        {
            get => _hasNoQuestsActive;
            set { if (value == _hasNoQuestsActive) return; _hasNoQuestsActive = value; OnPropertyChangedWithValue(value, nameof(HasNoQuestsActive)); }
        }
        [DataSourceProperty] public float TopLeftAlpha
        {
            get => _topLeftAlpha;
            set { if (value == _topLeftAlpha) return; _topLeftAlpha = value; OnPropertyChangedWithValue(value, nameof(TopLeftAlpha)); }
        }
        [DataSourceProperty] public float TopLeftLeftMargin
        {
            get => _topLeftLeftMargin;
            set { if (value == _topLeftLeftMargin) return; _topLeftLeftMargin = value; OnPropertyChangedWithValue(value, nameof(TopLeftLeftMargin)); }
        }

        // Quick-Action Bar handlers — Phase 1 stubs.
        // Each fires an inquiry popup explaining what would happen if the shortcut
        // were wired. Real shortcut implementations are deferred so we don't break
        // the existing dialog flow.
        public void ExecuteTalkChief()  { ShowHint(BandItPlus.Localization.Get("bp_camptopleftvm_003", "Chief"),       BandItPlus.Localization.Get("bp_camptopleftvm_004", "Walk to the chief in the camp to talk in person.")); }
        public void ExecuteFoodVendor() { ShowHint(BandItPlus.Localization.Get("bp_camptopleftvm_005", "Food Vendor"), BandItPlus.Localization.Get("bp_camptopleftvm_006", "Approach the food vendor's stall to trade or accept quests.")); }
        public void ExecuteGearVendor() { ShowHint(BandItPlus.Localization.Get("bp_camptopleftvm_007", "Gear Vendor"), BandItPlus.Localization.Get("bp_camptopleftvm_008", "Approach the gear vendor's stall to trade or accept quests.")); }
        public void ExecuteLeave()      { ShowHint(BandItPlus.Localization.Get("bp_camptopleftvm_009", "Leave Camp"),  BandItPlus.Localization.Get("bp_camptopleftvm_010", "Press Esc to leave camp via the game menu, or walk to the camp entry.")); }

        private static void ShowHint(string title, string body)
        {
            try
            {
                InformationManager.ShowInquiry(
                    new InquiryData(title, body, true, false, BandItPlus.Localization.Get("bp_camptopleftvm_011", "OK"), "",
                        () => { }, () => { }),
                    pauseGameActiveState: false);
            }
            catch { }
        }
    }
}
