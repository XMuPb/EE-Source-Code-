using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BandItPlus.UI
{
    // v186 (2026-05-23): unified ViewModel for the new BanditCampPanel — replaces
    // the split CampInfoPanelVM + CampTopLeftVM. Same data, deduplicated:
    // the old ActiveFoodQuestText vs FoodQuestLine pair becomes the single
    // FoodQuestText below. All 25 DataSourceProperties documented per the spec
    // at docs/superpowers/specs/2026-05-23-bandit-camp-panel-design.md §4.
    public class BanditCampPanelVM : ViewModel
    {
        private string _campName = "";
        private string _loreLine = "";
        private string _bannerCodeText = "";
        private string _chiefName = "";
        private string _chiefTitle = "";
        private string _cultureName = "";

        private string _chiefTrustDots = "";
        private string _foodVendorTrustDots = "";
        private string _gearVendorTrustDots = "";

        private string _chiefQuestText = "";
        private string _foodQuestText = "";
        private string _gearQuestText = "";
        // 2026-06-06 Wave 4.13.9 — SLAVER activity row (4th ACTIVITY cell row).
        private string _slaverActivityText = "";
        private string _servicesText = "";

        private string _foodContactStatus = "";
        private string _gearContactStatus = "";
        private string _customOrderStatus = "";

        private string _timeOfDayLine = "";

        private string _action1Label = BandItPlus.Localization.Get("bp_camppanelvm_001", "Trade");
        private string _action2Label = BandItPlus.Localization.Get("bp_camppanelvm_002", "Drink");
        private string _action3Label = BandItPlus.Localization.Get("bp_camppanelvm_003", "Rest");
        private string _action4Label = BandItPlus.Localization.Get("bp_camppanelvm_004", "Listen");

        private float _backdropAlpha;
        private float _panelLeftMargin = -400f;
        private float _sectionGlowFactor = 1.0f;
        private bool _isPanelVisible;

        // v2 (2026-05-24): horizontal top panel additions.
        // Rapport (status words + status-colored indicator dots + filled counts)
        private string _chiefRapportLabel = BandItPlus.Localization.Get("bp_camppanelvm_005", "stranger");
        private string _slaverVendorRapportLabel = BandItPlus.Localization.Get("bp_camppanelvm_006", "stranger");  // 2026-06-05 Wave 4.13: slaver-trust rapport row
        private string _slaverVendorRapportDotColor = "#888888FF";
        private int _slaverVendorRapportFilled;
        private string _foodVendorRapportLabel = BandItPlus.Localization.Get("bp_camppanelvm_007", "stranger");
        private string _gearVendorRapportLabel = BandItPlus.Localization.Get("bp_camppanelvm_008", "stranger");
        private string _chiefRapportDotColor = "#888888FF";
        private string _foodVendorRapportDotColor = "#888888FF";
        private string _gearVendorRapportDotColor = "#888888FF";
        private int _chiefRapportFilled;
        private int _foodVendorRapportFilled;
        private int _gearVendorRapportFilled;
        // Network additions
        private string _rumorsCount = BandItPlus.Localization.Get("bp_camppanelvm_009", "none");
        private string _bountyAmount = "—";
        // 2026-05-26: Tier-3 Contacts reach. Populated by BanditCampPanelMissionView
        // from BanditDialogManager — counts active contacts (introduced or met) for
        // THIS camp's culture and surfaces them in the Network cell so the player
        // can see their earned reach without opening dialogue.
        private string _contactsText = "—";
        // Last visit
        private string _lastVisitLine = BandItPlus.Localization.Get("bp_camppanelvm_010", "first visit");
        private string _prisonersText = "—";  // 2026-06-05 Wave 4.13: slaver-camp prisoner indicator
        // Party stats
        private string _partyGoldText = BandItPlus.Localization.Get("bp_camppanelvm_011", "0g");
        private string _partyTroopsText = "0/0";
        private string _partyFoodText = BandItPlus.Localization.Get("bp_camppanelvm_012", "0 food");
        // Animation driver (replaces v1 PanelLeftMargin)
        private float _panelTopMargin = -220f;

        [DataSourceProperty] public string CampName { get { return _campName; } set { if (value != _campName) { _campName = value; OnPropertyChangedWithValue(value, nameof(CampName)); } } }
        [DataSourceProperty] public string LoreLine { get { return _loreLine; } set { if (value != _loreLine) { _loreLine = value; OnPropertyChangedWithValue(value, nameof(LoreLine)); } } }
        [DataSourceProperty] public string BannerCodeText { get { return _bannerCodeText; } set { if (value != _bannerCodeText) { _bannerCodeText = value; OnPropertyChangedWithValue(value, nameof(BannerCodeText)); } } }
        [DataSourceProperty] public string ChiefName { get { return _chiefName; } set { if (value != _chiefName) { _chiefName = value; OnPropertyChangedWithValue(value, nameof(ChiefName)); } } }
        [DataSourceProperty] public string ChiefTitle { get { return _chiefTitle; } set { if (value != _chiefTitle) { _chiefTitle = value; OnPropertyChangedWithValue(value, nameof(ChiefTitle)); } } }
        [DataSourceProperty] public string CultureName { get { return _cultureName; } set { if (value != _cultureName) { _cultureName = value; OnPropertyChangedWithValue(value, nameof(CultureName)); } } }

        [DataSourceProperty] public string ChiefTrustDots { get { return _chiefTrustDots; } set { if (value != _chiefTrustDots) { _chiefTrustDots = value; OnPropertyChangedWithValue(value, nameof(ChiefTrustDots)); } } }
        [DataSourceProperty] public string FoodVendorTrustDots { get { return _foodVendorTrustDots; } set { if (value != _foodVendorTrustDots) { _foodVendorTrustDots = value; OnPropertyChangedWithValue(value, nameof(FoodVendorTrustDots)); } } }
        [DataSourceProperty] public string GearVendorTrustDots { get { return _gearVendorTrustDots; } set { if (value != _gearVendorTrustDots) { _gearVendorTrustDots = value; OnPropertyChangedWithValue(value, nameof(GearVendorTrustDots)); } } }

        [DataSourceProperty] public string ChiefQuestText { get { return _chiefQuestText; } set { if (value != _chiefQuestText) { _chiefQuestText = value; OnPropertyChangedWithValue(value, nameof(ChiefQuestText)); } } }
        [DataSourceProperty] public string FoodQuestText { get { return _foodQuestText; } set { if (value != _foodQuestText) { _foodQuestText = value; OnPropertyChangedWithValue(value, nameof(FoodQuestText)); } } }
        [DataSourceProperty] public string GearQuestText { get { return _gearQuestText; } set { if (value != _gearQuestText) { _gearQuestText = value; OnPropertyChangedWithValue(value, nameof(GearQuestText)); } } }
        [DataSourceProperty] public string SlaverActivityText { get { return _slaverActivityText; } set { if (value != _slaverActivityText) { _slaverActivityText = value; OnPropertyChangedWithValue(value, nameof(SlaverActivityText)); } } }
        [DataSourceProperty] public string ServicesText { get { return _servicesText; } set { if (value != _servicesText) { _servicesText = value; OnPropertyChangedWithValue(value, nameof(ServicesText)); } } }

        [DataSourceProperty] public string FoodContactStatus { get { return _foodContactStatus; } set { if (value != _foodContactStatus) { _foodContactStatus = value; OnPropertyChangedWithValue(value, nameof(FoodContactStatus)); } } }
        [DataSourceProperty] public string GearContactStatus { get { return _gearContactStatus; } set { if (value != _gearContactStatus) { _gearContactStatus = value; OnPropertyChangedWithValue(value, nameof(GearContactStatus)); } } }
        [DataSourceProperty] public string CustomOrderStatus { get { return _customOrderStatus; } set { if (value != _customOrderStatus) { _customOrderStatus = value; OnPropertyChangedWithValue(value, nameof(CustomOrderStatus)); } } }

        [DataSourceProperty] public string TimeOfDayLine { get { return _timeOfDayLine; } set { if (value != _timeOfDayLine) { _timeOfDayLine = value; OnPropertyChangedWithValue(value, nameof(TimeOfDayLine)); } } }

        [DataSourceProperty] public string Action1Label { get { return _action1Label; } set { if (value != _action1Label) { _action1Label = value; OnPropertyChangedWithValue(value, nameof(Action1Label)); } } }
        [DataSourceProperty] public string Action2Label { get { return _action2Label; } set { if (value != _action2Label) { _action2Label = value; OnPropertyChangedWithValue(value, nameof(Action2Label)); } } }
        [DataSourceProperty] public string Action3Label { get { return _action3Label; } set { if (value != _action3Label) { _action3Label = value; OnPropertyChangedWithValue(value, nameof(Action3Label)); } } }
        [DataSourceProperty] public string Action4Label { get { return _action4Label; } set { if (value != _action4Label) { _action4Label = value; OnPropertyChangedWithValue(value, nameof(Action4Label)); } } }

        [DataSourceProperty] public float BackdropAlpha { get { return _backdropAlpha; } set { if (value != _backdropAlpha) { _backdropAlpha = value; OnPropertyChangedWithValue(value, nameof(BackdropAlpha)); } } }
        [DataSourceProperty] public float PanelLeftMargin { get { return _panelLeftMargin; } set { if (value != _panelLeftMargin) { _panelLeftMargin = value; OnPropertyChangedWithValue(value, nameof(PanelLeftMargin)); } } }
        [DataSourceProperty] public float SectionGlowFactor { get { return _sectionGlowFactor; } set { if (value != _sectionGlowFactor) { _sectionGlowFactor = value; OnPropertyChangedWithValue(value, nameof(SectionGlowFactor)); } } }
        [DataSourceProperty] public bool IsPanelVisible { get { return _isPanelVisible; } set { if (value != _isPanelVisible) { _isPanelVisible = value; OnPropertyChangedWithValue(value, nameof(IsPanelVisible)); } } }

        [DataSourceProperty] public string ChiefRapportLabel { get { return _chiefRapportLabel; } set { if (value != _chiefRapportLabel) { _chiefRapportLabel = value; OnPropertyChangedWithValue(value, nameof(ChiefRapportLabel)); } } }
        [DataSourceProperty] public string SlaverVendorRapportLabel { get { return _slaverVendorRapportLabel; } set { if (value != _slaverVendorRapportLabel) { _slaverVendorRapportLabel = value; OnPropertyChangedWithValue(value, nameof(SlaverVendorRapportLabel)); } } }
        [DataSourceProperty] public string SlaverVendorRapportDotColor { get { return _slaverVendorRapportDotColor; } set { if (value != _slaverVendorRapportDotColor) { _slaverVendorRapportDotColor = value; OnPropertyChangedWithValue(value, nameof(SlaverVendorRapportDotColor)); } } }
        [DataSourceProperty] public int SlaverVendorRapportFilled { get { return _slaverVendorRapportFilled; } set { if (value != _slaverVendorRapportFilled) { _slaverVendorRapportFilled = value; OnPropertyChangedWithValue(value, nameof(SlaverVendorRapportFilled)); } } }
        [DataSourceProperty] public string FoodVendorRapportLabel { get { return _foodVendorRapportLabel; } set { if (value != _foodVendorRapportLabel) { _foodVendorRapportLabel = value; OnPropertyChangedWithValue(value, nameof(FoodVendorRapportLabel)); } } }
        [DataSourceProperty] public string GearVendorRapportLabel { get { return _gearVendorRapportLabel; } set { if (value != _gearVendorRapportLabel) { _gearVendorRapportLabel = value; OnPropertyChangedWithValue(value, nameof(GearVendorRapportLabel)); } } }
        [DataSourceProperty] public string ChiefRapportDotColor { get { return _chiefRapportDotColor; } set { if (value != _chiefRapportDotColor) { _chiefRapportDotColor = value; OnPropertyChangedWithValue(value, nameof(ChiefRapportDotColor)); } } }
        [DataSourceProperty] public string FoodVendorRapportDotColor { get { return _foodVendorRapportDotColor; } set { if (value != _foodVendorRapportDotColor) { _foodVendorRapportDotColor = value; OnPropertyChangedWithValue(value, nameof(FoodVendorRapportDotColor)); } } }
        [DataSourceProperty] public string GearVendorRapportDotColor { get { return _gearVendorRapportDotColor; } set { if (value != _gearVendorRapportDotColor) { _gearVendorRapportDotColor = value; OnPropertyChangedWithValue(value, nameof(GearVendorRapportDotColor)); } } }
        [DataSourceProperty] public int ChiefRapportFilled { get { return _chiefRapportFilled; } set { if (value != _chiefRapportFilled) { _chiefRapportFilled = value; OnPropertyChangedWithValue(value, nameof(ChiefRapportFilled)); } } }
        [DataSourceProperty] public int FoodVendorRapportFilled { get { return _foodVendorRapportFilled; } set { if (value != _foodVendorRapportFilled) { _foodVendorRapportFilled = value; OnPropertyChangedWithValue(value, nameof(FoodVendorRapportFilled)); } } }
        [DataSourceProperty] public int GearVendorRapportFilled { get { return _gearVendorRapportFilled; } set { if (value != _gearVendorRapportFilled) { _gearVendorRapportFilled = value; OnPropertyChangedWithValue(value, nameof(GearVendorRapportFilled)); } } }
        [DataSourceProperty] public string RumorsCount { get { return _rumorsCount; } set { if (value != _rumorsCount) { _rumorsCount = value; OnPropertyChangedWithValue(value, nameof(RumorsCount)); } } }
        [DataSourceProperty] public string BountyAmount { get { return _bountyAmount; } set { if (value != _bountyAmount) { _bountyAmount = value; OnPropertyChangedWithValue(value, nameof(BountyAmount)); } } }
        [DataSourceProperty] public string ContactsText { get { return _contactsText; } set { if (value != _contactsText) { _contactsText = value; OnPropertyChangedWithValue(value, nameof(ContactsText)); } } }
        [DataSourceProperty] public string LastVisitLine { get { return _lastVisitLine; } set { if (value != _lastVisitLine) { _lastVisitLine = value; OnPropertyChangedWithValue(value, nameof(LastVisitLine)); } } }
        [DataSourceProperty] public string PrisonersText { get { return _prisonersText; } set { if (value != _prisonersText) { _prisonersText = value; OnPropertyChangedWithValue(value, nameof(PrisonersText)); } } }
        [DataSourceProperty] public string PartyGoldText { get { return _partyGoldText; } set { if (value != _partyGoldText) { _partyGoldText = value; OnPropertyChangedWithValue(value, nameof(PartyGoldText)); } } }
        [DataSourceProperty] public string PartyTroopsText { get { return _partyTroopsText; } set { if (value != _partyTroopsText) { _partyTroopsText = value; OnPropertyChangedWithValue(value, nameof(PartyTroopsText)); } } }
        [DataSourceProperty] public string PartyFoodText { get { return _partyFoodText; } set { if (value != _partyFoodText) { _partyFoodText = value; OnPropertyChangedWithValue(value, nameof(PartyFoodText)); } } }
        [DataSourceProperty] public float PanelTopMargin { get { return _panelTopMargin; } set { if (value != _panelTopMargin) { _panelTopMargin = value; OnPropertyChangedWithValue(value, nameof(PanelTopMargin)); } } }

        public void ExecuteAction1()
        {
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("v2.1 ExecuteAction1 fired (Trade button click reached VM)");
            ShowStubPopup(BandItPlus.Localization.Get("bp_camppanelvm_013", "Trade"),
                BandItPlus.Localization.Get("bp_camppanelvm_014", "Trade with the camp's vendors. Real trade UI lands in a follow-up wave; for now, this button is a placeholder so the panel layout is testable."));
        }

        public void ExecuteAction2()
        {
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("v2.1 ExecuteAction2 fired (Drink button click reached VM)");
            ShowStubPopup(BandItPlus.Localization.Get("bp_camppanelvm_015", "Drink"),
                BandItPlus.Localization.Get("bp_camppanelvm_016", "Drink with the chief. Real drink mechanic (relation boost + drunk state) lands in a follow-up wave."));
        }

        public void ExecuteAction3()
        {
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("v2.1 ExecuteAction3 fired (Rest button click reached VM)");
            ShowStubPopup(BandItPlus.Localization.Get("bp_camppanelvm_017", "Rest"),
                BandItPlus.Localization.Get("bp_camppanelvm_018", "Rest at the camp. Real rest mechanic (party healing over time) lands in a follow-up wave."));
        }

        public void ExecuteAction4()
        {
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("v2.1 ExecuteAction4 fired (Listen button click reached VM)");
            ShowStubPopup(BandItPlus.Localization.Get("bp_camppanelvm_019", "Eavesdrop"),
                BandItPlus.Localization.Get("bp_camppanelvm_020", "Eavesdrop on the camp. Real eavesdrop mechanic (intel gain + skill check) lands in a follow-up wave."));
        }

        private static void ShowStubPopup(string title, string body)
        {
            var data = new InquiryData(
                title,
                body,
                true, false,
                BandItPlus.Localization.Get("bp_camppanelvm_021", "Understood"), null,
                null, null);
            InformationManager.ShowInquiry(data, pauseGameActiveState: false);
        }
    }
}
