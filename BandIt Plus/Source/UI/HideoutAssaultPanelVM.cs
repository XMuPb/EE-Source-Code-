using TaleWorlds.Library;

namespace BandItPlus.UI
{
    // v176 (2026-05-18): ViewModel for the redesigned Hideout Assault panel —
    // a persistent in-battle HUD. The MissionView builds this with the
    // parameterless ctor, then sets every property once at ~3s. BannerVisual
    // is a BannerImageIdentifierVM (reflection-built by the MissionView, typed
    // here as the ViewModel base because BannerImageIdentifierVM is not a
    // compile-time-accessible type in this SDK — same reason as
    // BandItPlusNameplateMixin). The five PipNOn bools drive the difficulty
    // meter. (Live updates + the 4 animation systems are Plan B / v177; this
    // VM carries only PanelAlpha for a basic fade-in.)
    public class HideoutAssaultPanelVM : ViewModel
    {
        private bool _isVisible = true;
        private float _panelAlpha;
        private string _hideoutName = "";
        private string _bossName = "";
        private string _cultureTrustLine = "";
        private string _bannerCode = "";
        private bool _hasBanner;
        private string _tauntText = "";
        private string _defenderCount = "";
        private string _yourForces = "";
        private string _oddsText = "";
        private bool _pip1On, _pip2On, _pip3On, _pip4On, _pip5On;
        private string _garrisonValue = "";
        private string _defenderTier = "";
        private string _tacticsValue = "";
        private string _spoilsText = "";

        public HideoutAssaultPanelVM()
        {
            _isVisible = true;
            _panelAlpha = 0f;   // MissionView fades it in
        }

        [DataSourceProperty]
        public bool IsVisible
        {
            get => _isVisible;
            set { if (value != _isVisible) { _isVisible = value; OnPropertyChangedWithValue(value, nameof(IsVisible)); } }
        }

        // 0 = invisible, 1 = fully shown — MissionView ramps this for fade-in.
        [DataSourceProperty]
        public float PanelAlpha
        {
            get => _panelAlpha;
            set { if (value != _panelAlpha) { _panelAlpha = value; OnPropertyChangedWithValue(value, nameof(PanelAlpha)); } }
        }

        [DataSourceProperty]
        public string HideoutName
        {
            get => _hideoutName;
            set { if (value != _hideoutName) { _hideoutName = value; OnPropertyChangedWithValue(value, nameof(HideoutName)); } }
        }

        [DataSourceProperty]
        public string BossName
        {
            get => _bossName;
            set { if (value != _bossName) { _bossName = value; OnPropertyChangedWithValue(value, nameof(BossName)); } }
        }

        // e.g. "Forest Bandits · Trust: Trusted"
        [DataSourceProperty]
        public string CultureTrustLine
        {
            get => _cultureTrustLine;
            set { if (value != _cultureTrustLine) { _cultureTrustLine = value; OnPropertyChangedWithValue(value, nameof(CultureTrustLine)); } }
        }

        // The chief's clan-banner code string (banner.Serialize()). The prefab's
        // BannerTableauWidget renders it; HasBanner gates the crest over the ◆.
        [DataSourceProperty]
        public string BannerCode
        {
            get => _bannerCode;
            set { if (value != _bannerCode) { _bannerCode = value; OnPropertyChangedWithValue(value, nameof(BannerCode)); } }
        }

        [DataSourceProperty]
        public bool HasBanner
        {
            get => _hasBanner;
            set { if (value != _hasBanner) { _hasBanner = value; OnPropertyChangedWithValue(value, nameof(HasBanner)); } }
        }

        [DataSourceProperty]
        public string TauntText
        {
            get => _tauntText;
            set { if (value != _tauntText) { _tauntText = value; OnPropertyChangedWithValue(value, nameof(TauntText)); } }
        }

        [DataSourceProperty]
        public string DefenderCount
        {
            get => _defenderCount;
            set { if (value != _defenderCount) { _defenderCount = value; OnPropertyChangedWithValue(value, nameof(DefenderCount)); } }
        }

        [DataSourceProperty]
        public string YourForces
        {
            get => _yourForces;
            set { if (value != _yourForces) { _yourForces = value; OnPropertyChangedWithValue(value, nameof(YourForces)); } }
        }

        [DataSourceProperty]
        public string OddsText
        {
            get => _oddsText;
            set { if (value != _oddsText) { _oddsText = value; OnPropertyChangedWithValue(value, nameof(OddsText)); } }
        }

        [DataSourceProperty]
        public bool Pip1On
        {
            get => _pip1On;
            set { if (value != _pip1On) { _pip1On = value; OnPropertyChangedWithValue(value, nameof(Pip1On)); } }
        }

        [DataSourceProperty]
        public bool Pip2On
        {
            get => _pip2On;
            set { if (value != _pip2On) { _pip2On = value; OnPropertyChangedWithValue(value, nameof(Pip2On)); } }
        }

        [DataSourceProperty]
        public bool Pip3On
        {
            get => _pip3On;
            set { if (value != _pip3On) { _pip3On = value; OnPropertyChangedWithValue(value, nameof(Pip3On)); } }
        }

        [DataSourceProperty]
        public bool Pip4On
        {
            get => _pip4On;
            set { if (value != _pip4On) { _pip4On = value; OnPropertyChangedWithValue(value, nameof(Pip4On)); } }
        }

        [DataSourceProperty]
        public bool Pip5On
        {
            get => _pip5On;
            set { if (value != _pip5On) { _pip5On = value; OnPropertyChangedWithValue(value, nameof(Pip5On)); } }
        }

        [DataSourceProperty]
        public string GarrisonValue
        {
            get => _garrisonValue;
            set { if (value != _garrisonValue) { _garrisonValue = value; OnPropertyChangedWithValue(value, nameof(GarrisonValue)); } }
        }

        [DataSourceProperty]
        public string DefenderTier
        {
            get => _defenderTier;
            set { if (value != _defenderTier) { _defenderTier = value; OnPropertyChangedWithValue(value, nameof(DefenderTier)); } }
        }

        [DataSourceProperty]
        public string TacticsValue
        {
            get => _tacticsValue;
            set { if (value != _tacticsValue) { _tacticsValue = value; OnPropertyChangedWithValue(value, nameof(TacticsValue)); } }
        }

        [DataSourceProperty]
        public string SpoilsText
        {
            get => _spoilsText;
            set { if (value != _spoilsText) { _spoilsText = value; OnPropertyChangedWithValue(value, nameof(SpoilsText)); } }
        }

        // chief-reveal card extras: combined forces/odds line, live composition
        // mix, and the old BANDIT AI panel compressed to one power line.
        private string _forcesLine = "";
        public string ForcesLine
        {
            get => _forcesLine;
            set { if (value != _forcesLine) { _forcesLine = value; OnPropertyChangedWithValue(value, nameof(ForcesLine)); } }
        }

        private string _compositionValue = "";
        public string CompositionValue
        {
            get => _compositionValue;
            set { if (value != _compositionValue) { _compositionValue = value; OnPropertyChangedWithValue(value, nameof(CompositionValue)); } }
        }

        private string _powerLine = "";
        public string PowerLine
        {
            get => _powerLine;
            set { if (value != _powerLine) { _powerLine = value; OnPropertyChangedWithValue(value, nameof(PowerLine)); } }
        }

        // cascade entrance: four groups ease in staggered (header/identity,
        // taunt, stats, footer), each with its own alpha + small downward drift.
        private float _headAlpha, _tauntAlpha, _statsAlpha, _footAlpha;
        private float _headDriftY, _tauntDriftY, _statsDriftY, _footDriftY;
        public float HeadAlpha
        {
            get => _headAlpha;
            set { if (value != _headAlpha) { _headAlpha = value; OnPropertyChangedWithValue(value, nameof(HeadAlpha)); } }
        }
        public float TauntAlpha
        {
            get => _tauntAlpha;
            set { if (value != _tauntAlpha) { _tauntAlpha = value; OnPropertyChangedWithValue(value, nameof(TauntAlpha)); } }
        }
        public float StatsAlpha
        {
            get => _statsAlpha;
            set { if (value != _statsAlpha) { _statsAlpha = value; OnPropertyChangedWithValue(value, nameof(StatsAlpha)); } }
        }
        public float FootAlpha
        {
            get => _footAlpha;
            set { if (value != _footAlpha) { _footAlpha = value; OnPropertyChangedWithValue(value, nameof(FootAlpha)); } }
        }
        public float HeadDriftY
        {
            get => _headDriftY;
            set { if (value != _headDriftY) { _headDriftY = value; OnPropertyChangedWithValue(value, nameof(HeadDriftY)); } }
        }
        public float TauntDriftY
        {
            get => _tauntDriftY;
            set { if (value != _tauntDriftY) { _tauntDriftY = value; OnPropertyChangedWithValue(value, nameof(TauntDriftY)); } }
        }
        public float StatsDriftY
        {
            get => _statsDriftY;
            set { if (value != _statsDriftY) { _statsDriftY = value; OnPropertyChangedWithValue(value, nameof(StatsDriftY)); } }
        }
        public float FootDriftY
        {
            get => _footDriftY;
            set { if (value != _footDriftY) { _footDriftY = value; OnPropertyChangedWithValue(value, nameof(FootDriftY)); } }
        }

        // Sneak-in reflavor (2026-07-03): the card fires in BOTH hideout mission
        // variants; these default to the assault texts so assault rendering is
        // unchanged, and the view swaps them for the stealth variant.
        private string _modeTagText = BandItPlus.Localization.Get("bp_hideoutassaultpanelvm_001", "HIDEOUT ASSAULT");
        public string ModeTagText
        {
            get => _modeTagText;
            set { if (value != _modeTagText) { _modeTagText = value; OnPropertyChangedWithValue(value, nameof(ModeTagText)); } }
        }

        private string _sublineTagText = BandItPlus.Localization.Get("bp_hideoutassaultpanelvm_002", "· Hideout Assault");
        public string SublineTagText
        {
            get => _sublineTagText;
            set { if (value != _sublineTagText) { _sublineTagText = value; OnPropertyChangedWithValue(value, nameof(SublineTagText)); } }
        }

        private string _oddsLabel = BandItPlus.Localization.Get("bp_hideoutassaultpanelvm_003", "ODDS");
        public string OddsLabel
        {
            get => _oddsLabel;
            set { if (value != _oddsLabel) { _oddsLabel = value; OnPropertyChangedWithValue(value, nameof(OddsLabel)); } }
        }

        // Sets the difficulty meter from a 0..5 level (clamped).
        public void SetDifficulty(int level)
        {
            if (level < 0) level = 0;
            if (level > 5) level = 5;
            Pip1On = level >= 1;
            Pip2On = level >= 2;
            Pip3On = level >= 3;
            Pip4On = level >= 4;
            Pip5On = level >= 5;
        }
    }
}
