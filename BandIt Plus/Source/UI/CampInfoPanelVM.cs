using System;
using TaleWorlds.Library;
using TaleWorlds.Core;
using BandItPlus.HideoutVisit;

namespace BandItPlus.UI
{
    // Wave 4.12-fix v84 (2026-05-13): ViewModel for the left-side persistent
    // RPG-style camp info panel. 7 sections per user pick (1,2,3,4,5,6,10):
    //   1. Camp identity (name, chief, culture)
    //   2. Chief trust tier + label
    //   3. Food vendor name + trust tier
    //   4. Gear vendor name + trust tier
    //   5. Active vendor quest summary
    //   6. Contact introduction status
    //  10. Banner sprite (via BannerCodeText)
    //
    // Refreshed each tick by CampInfoPanelMissionView so trust/quest changes
    // appearing mid-scene update live.
    public class CampInfoPanelVM : ViewModel
    {
        // Section 1: Camp identity
        private string _campName = "";
        private string _chiefName = "";
        private string _chiefTitle = "";
        private string _cultureName = "";
        // Section 2: Chief trust
        private string _chiefTrustLabel = "";
        // Section 3: Food vendor
        private string _foodVendorName = "";
        private string _foodVendorTrustLabel = "";
        // Section 4: Gear vendor
        private string _gearVendorName = "";
        private string _gearVendorTrustLabel = "";
        // Section 5: Active vendor quest
        private string _activeFoodQuestText = "";
        private string _activeGearQuestText = "";
        // v94.2: chief trust quest (surfaces "OnAcceptTrustQuest" state in UI)
        private string _activeChiefQuestText = "";
        // Section 6: Contact intro status
        private string _foodContactStatus = "";
        private string _gearContactStatus = "";
        // Section 7: Pending Custom Order
        private string _customOrderStatus = "";
        // Section 9: Services available
        private string _servicesText = "";
        // Section 10: Banner sprite
        private string _bannerCodeText = "";
        // v91 (2026-05-13): cinematic animation — fade-in + ongoing alpha pulse
        // v92: added slide-in from off-screen left for stronger cinematic feel
        private float _backdropAlpha = 0f;
        private float _panelLeftMargin = -260f;
        // v95 (2026-05-13): deep narrative polish.
        private string _playerBannerCode = "";
        private string _backdropTintHex = "#FFFFFFFF";    // time-of-day tint, default neutral
        // v96: Camp Memory display ("5th visit · First met Spring 12, Year 1084 · 243 days ago")
        private string _campMemoryLine = "";
        // v99 (2026-05-13): smart "Next step" hint — guides the player by reading
        // current trust + quest state and surfacing the most impactful next action.
        private string _nextStepHint = "";
        // v104 (2026-05-13): auto-hide panel when game is paused (Esc menu, etc).
        private bool _isPanelVisible = true;
        // v97 REVERTED (2026-05-13): tested empirically — Command.HoverBegin on plain
        // ButtonWidget does NOT fire in this Bannerlord version. Vanilla rule confirmed:
        // hover commands only work on HintWidget children. Tooltips require resolving
        // the HintViewModel namespace (deferred until dnSpy reconnaissance).
        // v94 (2026-05-13): cinematic polish — lore line, day/time, section glow
        private string _loreLine = "";
        private string _timeOfDayLine = "";
        private float _sectionGlowFactor = 1.0f;
        // v100 (2026-05-13): hover tooltips via runtime reflection.
        // HintViewModel lives in a namespace not exposed by our .csproj refs at compile
        // time (EE confirms namespace varies: TaleWorlds.Library.HintViewModel OR
        // TaleWorlds.Core.ViewModelCollection.HintViewModel by version). We store as
        // object — Gauntlet binds by property name, not static type. MissionView
        // populates these via reflection in OnMissionScreenInitialize.
        private object _talkChiefHint;
        private object _foodVendorHint;
        private object _gearVendorHint;
        private object _leaveHint;
        // v149: per-section hover hints (CHIEF / VENDORS / QUESTS / NETWORK)
        private object _chiefSectionHint;
        private object _vendorsSectionHint;
        private object _questsSectionHint;
        private object _networkSectionHint;
        // v150: trust progress-bar fill widths in pixels (tier 0..3 -> 0..150px)
        private float _chiefTrustFillWidth;
        private float _foodTrustFillWidth;
        private float _gearTrustFillWidth;
        // v151: chief face portrait — holds a CharacterImageIdentifierVM (typed as
        // object; built via reflection in the MissionView, namespace varies).
        private object _chiefPortrait;
        // v153 (code-review fix): gates the portrait widget's IsVisible so it is
        // never shown — nor its DataSource bindings resolved — when the portrait
        // failed to build (ChiefPortrait null, e.g. a culture with no chief).
        private bool _hasChiefPortrait;
        // v152: camp record — derived relationship-summary lines
        private string _campRecordLine1 = "";
        private string _campRecordLine2 = "";
        private string _campRecordLine3 = "";

        [DataSourceProperty] public string CampName
        {
            get => _campName;
            set { if (value == _campName) return; _campName = value; OnPropertyChangedWithValue(value, nameof(CampName)); }
        }
        [DataSourceProperty] public string ChiefName
        {
            get => _chiefName;
            set { if (value == _chiefName) return; _chiefName = value; OnPropertyChangedWithValue(value, nameof(ChiefName)); }
        }
        [DataSourceProperty] public string ChiefTitle
        {
            get => _chiefTitle;
            set { if (value == _chiefTitle) return; _chiefTitle = value; OnPropertyChangedWithValue(value, nameof(ChiefTitle)); }
        }
        [DataSourceProperty] public string CultureName
        {
            get => _cultureName;
            set { if (value == _cultureName) return; _cultureName = value; OnPropertyChangedWithValue(value, nameof(CultureName)); }
        }
        [DataSourceProperty] public string ChiefTrustLabel
        {
            get => _chiefTrustLabel;
            set { if (value == _chiefTrustLabel) return; _chiefTrustLabel = value; OnPropertyChangedWithValue(value, nameof(ChiefTrustLabel)); }
        }
        [DataSourceProperty] public string FoodVendorName
        {
            get => _foodVendorName;
            set { if (value == _foodVendorName) return; _foodVendorName = value; OnPropertyChangedWithValue(value, nameof(FoodVendorName)); }
        }
        [DataSourceProperty] public string FoodVendorTrustLabel
        {
            get => _foodVendorTrustLabel;
            set { if (value == _foodVendorTrustLabel) return; _foodVendorTrustLabel = value; OnPropertyChangedWithValue(value, nameof(FoodVendorTrustLabel)); }
        }
        [DataSourceProperty] public string GearVendorName
        {
            get => _gearVendorName;
            set { if (value == _gearVendorName) return; _gearVendorName = value; OnPropertyChangedWithValue(value, nameof(GearVendorName)); }
        }
        [DataSourceProperty] public string GearVendorTrustLabel
        {
            get => _gearVendorTrustLabel;
            set { if (value == _gearVendorTrustLabel) return; _gearVendorTrustLabel = value; OnPropertyChangedWithValue(value, nameof(GearVendorTrustLabel)); }
        }
        [DataSourceProperty] public string ActiveFoodQuestText
        {
            get => _activeFoodQuestText;
            set { if (value == _activeFoodQuestText) return; _activeFoodQuestText = value; OnPropertyChangedWithValue(value, nameof(ActiveFoodQuestText)); }
        }
        [DataSourceProperty] public string ActiveGearQuestText
        {
            get => _activeGearQuestText;
            set { if (value == _activeGearQuestText) return; _activeGearQuestText = value; OnPropertyChangedWithValue(value, nameof(ActiveGearQuestText)); }
        }
        [DataSourceProperty] public string ActiveChiefQuestText
        {
            get => _activeChiefQuestText;
            set { if (value == _activeChiefQuestText) return; _activeChiefQuestText = value; OnPropertyChangedWithValue(value, nameof(ActiveChiefQuestText)); }
        }
        [DataSourceProperty] public string FoodContactStatus
        {
            get => _foodContactStatus;
            set { if (value == _foodContactStatus) return; _foodContactStatus = value; OnPropertyChangedWithValue(value, nameof(FoodContactStatus)); }
        }
        [DataSourceProperty] public string GearContactStatus
        {
            get => _gearContactStatus;
            set { if (value == _gearContactStatus) return; _gearContactStatus = value; OnPropertyChangedWithValue(value, nameof(GearContactStatus)); }
        }
        [DataSourceProperty] public string BannerCodeText
        {
            get => _bannerCodeText;
            set { if (value == _bannerCodeText) return; _bannerCodeText = value; OnPropertyChangedWithValue(value, nameof(BannerCodeText)); }
        }
        [DataSourceProperty] public string CustomOrderStatus
        {
            get => _customOrderStatus;
            set { if (value == _customOrderStatus) return; _customOrderStatus = value; OnPropertyChangedWithValue(value, nameof(CustomOrderStatus)); }
        }
        [DataSourceProperty] public string ServicesText
        {
            get => _servicesText;
            set { if (value == _servicesText) return; _servicesText = value; OnPropertyChangedWithValue(value, nameof(ServicesText)); }
        }
        [DataSourceProperty] public float BackdropAlpha
        {
            get => _backdropAlpha;
            set { if (value == _backdropAlpha) return; _backdropAlpha = value; OnPropertyChangedWithValue(value, nameof(BackdropAlpha)); }
        }
        [DataSourceProperty] public float PanelLeftMargin
        {
            get => _panelLeftMargin;
            set { if (value == _panelLeftMargin) return; _panelLeftMargin = value; OnPropertyChangedWithValue(value, nameof(PanelLeftMargin)); }
        }
        [DataSourceProperty] public string LoreLine
        {
            get => _loreLine;
            set { if (value == _loreLine) return; _loreLine = value; OnPropertyChangedWithValue(value, nameof(LoreLine)); }
        }
        [DataSourceProperty] public string TimeOfDayLine
        {
            get => _timeOfDayLine;
            set { if (value == _timeOfDayLine) return; _timeOfDayLine = value; OnPropertyChangedWithValue(value, nameof(TimeOfDayLine)); }
        }
        [DataSourceProperty] public float SectionGlowFactor
        {
            get => _sectionGlowFactor;
            set { if (value == _sectionGlowFactor) return; _sectionGlowFactor = value; OnPropertyChangedWithValue(value, nameof(SectionGlowFactor)); }
        }
        [DataSourceProperty] public string PlayerBannerCode
        {
            get => _playerBannerCode;
            set { if (value == _playerBannerCode) return; _playerBannerCode = value; OnPropertyChangedWithValue(value, nameof(PlayerBannerCode)); }
        }
        [DataSourceProperty] public string BackdropTintHex
        {
            get => _backdropTintHex;
            set { if (value == _backdropTintHex) return; _backdropTintHex = value; OnPropertyChangedWithValue(value, nameof(BackdropTintHex)); }
        }
        [DataSourceProperty] public string CampMemoryLine
        {
            get => _campMemoryLine;
            set { if (value == _campMemoryLine) return; _campMemoryLine = value; OnPropertyChangedWithValue(value, nameof(CampMemoryLine)); }
        }
        [DataSourceProperty] public string NextStepHint
        {
            get => _nextStepHint;
            set { if (value == _nextStepHint) return; _nextStepHint = value; OnPropertyChangedWithValue(value, nameof(NextStepHint)); }
        }
        [DataSourceProperty] public bool IsPanelVisible
        {
            get => _isPanelVisible;
            set { if (value == _isPanelVisible) return; _isPanelVisible = value; OnPropertyChangedWithValue(value, nameof(IsPanelVisible)); }
        }
        // v97 REVERTED — hover-tooltip VM members removed.

        // v96 (2026-05-13): Lore Card — click chief banner to surface CampIntro lore.
        // Lore text is set by MissionView.RefreshVM from chiefProfile.CampIntro,
        // then ShowLoreCard fires an inquiry popup with the camp's authored prose.
        private string _campLoreCardBody = "";
        [DataSourceProperty] public string CampLoreCardBody
        {
            get => _campLoreCardBody;
            set { if (value == _campLoreCardBody) return; _campLoreCardBody = value; OnPropertyChangedWithValue(value, nameof(CampLoreCardBody)); }
        }
        // v99: player banner clickable — shows player's clan/title info (mirrors
        // the chief-banner lore card). Surfaces existing data already in the campaign.
        public void ExecutePlayerLore()
        {
            try
            {
                var hero = TaleWorlds.CampaignSystem.Hero.MainHero;
                if (hero == null) return;
                string name = hero.Name?.ToString() ?? BandItPlus.Localization.Get("bp_campinfopanelvm_001", "Unnamed");
                string clan = hero.Clan?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_campinfopanelvm_002", "Clanless");
                int renown = (int)(hero.Clan?.Renown ?? 0);
                string body = new TaleWorlds.Localization.TextObject("{=bp_hcc_001}{NAME} of {CLAN}\nClan Renown: {RENOWN}\nGold: {GOLD} denars")
                            .SetTextVariable("NAME", name)
                            .SetTextVariable("CLAN", clan)
                            .SetTextVariable("RENOWN", renown)
                            .SetTextVariable("GOLD", hero.Gold)
                            .ToString();
                InformationManager.ShowInquiry(
                    new InquiryData(BandItPlus.Localization.Get("bp_campinfopanelvm_003", "Your Banner"), body, true, false, BandItPlus.Localization.Get("bp_campinfopanelvm_004", "Close"), "",
                        () => { }, () => { }),
                    pauseGameActiveState: false);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v122 ExecutePlayerLore fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public void ExecuteShowLore()
        {
            try
            {
                if (string.IsNullOrEmpty(_campLoreCardBody)) return;
                InformationManager.ShowInquiry(
                    new InquiryData(BandItPlus.Localization.Get("bp_campinfopanelvm_005", "This Camp"), _campLoreCardBody, true, false, BandItPlus.Localization.Get("bp_campinfopanelvm_006", "Close"), "",
                        () => { }, () => { }),
                    pauseGameActiveState: false);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v122 ExecuteShowLore fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // v94.2: Quick-Action Bar button handlers (click).
        // v94.3: hover tooltips supplement click-popups — hover reveals action
        // semantic, click fires the inquiry.
        public void ExecuteTalkChief()  { ShowHint(BandItPlus.Localization.Get("bp_campinfopanelvm_007", "Chief"), BandItPlus.Localization.Get("bp_campinfopanelvm_008", "Walk to the chief in the camp to talk in person.")); }
        public void ExecuteFoodVendor() { ShowHint(BandItPlus.Localization.Get("bp_campinfopanelvm_009", "Food Vendor"), BandItPlus.Localization.Get("bp_campinfopanelvm_010", "Approach the food vendor's stall to trade or accept quests.")); }
        public void ExecuteGearVendor() { ShowHint(BandItPlus.Localization.Get("bp_campinfopanelvm_011", "Gear Vendor"), BandItPlus.Localization.Get("bp_campinfopanelvm_012", "Approach the gear vendor's stall to trade or accept quests.")); }
        // v99 (2026-05-13): Leave Camp ACTUALLY ends the mission now (was stub popup).
        // Confirms via Yes/No to prevent mis-clicks since X-mark sits next to vendor buttons.
        public void ExecuteLeave()
        {
            try
            {
                InformationManager.ShowInquiry(
                    new InquiryData(BandItPlus.Localization.Get("bp_campinfopanelvm_013", "Leave Camp"),
                        BandItPlus.Localization.Get("bp_campinfopanelvm_014", "Return to the world map?"),
                        true, true, BandItPlus.Localization.Get("bp_campinfopanelvm_015", "Leave"), BandItPlus.Localization.Get("bp_campinfopanelvm_016", "Stay"),
                        () => {
                            try { TaleWorlds.MountAndBlade.Mission.Current?.EndMission(); }
                            catch (Exception ex)
                            {
                                HideoutPeacefulVisitState.Log("v122 EndMission fail: "
                                    + ex.GetType().Name + ": " + ex.Message);
                            }
                        },
                        () => { }),
                    pauseGameActiveState: false);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v122 ExecuteLeave fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // v97 REVERTED — hover handler methods removed.

        // v100: hover handlers — empty bodies; Gauntlet's tooltip overlay renders
        // from the HintWidget's DataSource (the reflection-instantiated HintViewModel).
        public void ExecuteBeginHint(object hint) { }
        public void ExecuteEndHint() { }

        [DataSourceProperty] public object TalkChiefHint
        {
            get => _talkChiefHint;
            set { if (value == _talkChiefHint) return; _talkChiefHint = value; OnPropertyChangedWithValue(value, nameof(TalkChiefHint)); }
        }
        [DataSourceProperty] public object FoodVendorHint
        {
            get => _foodVendorHint;
            set { if (value == _foodVendorHint) return; _foodVendorHint = value; OnPropertyChangedWithValue(value, nameof(FoodVendorHint)); }
        }
        [DataSourceProperty] public object GearVendorHint
        {
            get => _gearVendorHint;
            set { if (value == _gearVendorHint) return; _gearVendorHint = value; OnPropertyChangedWithValue(value, nameof(GearVendorHint)); }
        }
        [DataSourceProperty] public object LeaveHint
        {
            get => _leaveHint;
            set { if (value == _leaveHint) return; _leaveHint = value; OnPropertyChangedWithValue(value, nameof(LeaveHint)); }
        }
        // v149: per-section hover hints
        [DataSourceProperty] public object ChiefSectionHint
        {
            get => _chiefSectionHint;
            set { if (value == _chiefSectionHint) return; _chiefSectionHint = value; OnPropertyChangedWithValue(value, nameof(ChiefSectionHint)); }
        }
        [DataSourceProperty] public object VendorsSectionHint
        {
            get => _vendorsSectionHint;
            set { if (value == _vendorsSectionHint) return; _vendorsSectionHint = value; OnPropertyChangedWithValue(value, nameof(VendorsSectionHint)); }
        }
        [DataSourceProperty] public object QuestsSectionHint
        {
            get => _questsSectionHint;
            set { if (value == _questsSectionHint) return; _questsSectionHint = value; OnPropertyChangedWithValue(value, nameof(QuestsSectionHint)); }
        }
        [DataSourceProperty] public object NetworkSectionHint
        {
            get => _networkSectionHint;
            set { if (value == _networkSectionHint) return; _networkSectionHint = value; OnPropertyChangedWithValue(value, nameof(NetworkSectionHint)); }
        }
        // v150: trust progress-bar fill widths — bound to the gold fill widgets'
        // SuggestedWidth so the bar fills proportionally with the trust tier.
        [DataSourceProperty] public float ChiefTrustFillWidth
        {
            get => _chiefTrustFillWidth;
            set { if (value == _chiefTrustFillWidth) return; _chiefTrustFillWidth = value; OnPropertyChangedWithValue(value, nameof(ChiefTrustFillWidth)); }
        }
        [DataSourceProperty] public float FoodTrustFillWidth
        {
            get => _foodTrustFillWidth;
            set { if (value == _foodTrustFillWidth) return; _foodTrustFillWidth = value; OnPropertyChangedWithValue(value, nameof(FoodTrustFillWidth)); }
        }
        [DataSourceProperty] public float GearTrustFillWidth
        {
            get => _gearTrustFillWidth;
            set { if (value == _gearTrustFillWidth) return; _gearTrustFillWidth = value; OnPropertyChangedWithValue(value, nameof(GearTrustFillWidth)); }
        }
        // v151: chief face portrait (CharacterImageIdentifierVM). The prefab's
        // ImageIdentifierWidget binds DataSource="{ChiefPortrait}".
        [DataSourceProperty] public object ChiefPortrait
        {
            get => _chiefPortrait;
            set { if (value == _chiefPortrait) return; _chiefPortrait = value; OnPropertyChangedWithValue(value, nameof(ChiefPortrait)); }
        }
        [DataSourceProperty] public bool HasChiefPortrait
        {
            get => _hasChiefPortrait;
            set { if (value == _hasChiefPortrait) return; _hasChiefPortrait = value; OnPropertyChangedWithValue(value, nameof(HasChiefPortrait)); }
        }
        // v152: camp record lines (CAMP RECORD section)
        [DataSourceProperty] public string CampRecordLine1
        {
            get => _campRecordLine1;
            set { if (value == _campRecordLine1) return; _campRecordLine1 = value; OnPropertyChangedWithValue(value, nameof(CampRecordLine1)); }
        }
        [DataSourceProperty] public string CampRecordLine2
        {
            get => _campRecordLine2;
            set { if (value == _campRecordLine2) return; _campRecordLine2 = value; OnPropertyChangedWithValue(value, nameof(CampRecordLine2)); }
        }
        [DataSourceProperty] public string CampRecordLine3
        {
            get => _campRecordLine3;
            set { if (value == _campRecordLine3) return; _campRecordLine3 = value; OnPropertyChangedWithValue(value, nameof(CampRecordLine3)); }
        }

        // v153 (code-review fix): a quick-action button click now shows a
        // non-blocking corner message, not a blocking OK-popup — clicking a HUD
        // quick-action should never interrupt the player with a modal dialog.
        private static void ShowHint(string title, string body)
        {
            try
            {
                InformationManager.DisplayMessage(new InformationMessage(title + " — " + body));
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v153 ShowHint fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
