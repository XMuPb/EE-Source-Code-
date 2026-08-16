using System;
using TaleWorlds.Library;

namespace BandItPlus.UI
{
    // 2026-07-01 (Bandit-King GUI, Tasks 2-3): view-model for the reusable
    // premium event panel (BanditKingBrief.xml). Data-driven from a
    // BanditKingBriefData: art sprite id, hero portrait, clan banner code, a
    // title/subline/flavor/stakes block, a list of BriefRowVM info rows, and 1-2
    // buttons whose Actions come from the data. The cascade entrance (curtain →
    // art → header → flavor → rows → buttons) and the ease curve are copied
    // verbatim from BpJailbreakBriefVM.
    //
    // Portrait is a CharacterImageIdentifierVM built via the proven namespace-
    // resilient reflection recipe (CampInfoPanelMissionView.TryCreateChiefPortrait)
    // and exposed as object; the prefab's ImageIdentifierWidget binds it via
    // DataSource="{Portrait}". Banner is the serialized clan-banner code string,
    // bound by a BannerTableauWidget (BannerCodeText="@BannerCode") — the exact
    // pattern used by CampInfoPanel.xml / BanditCampPanelMissionView. Null hero /
    // clan → HasPortrait / HasBanner false so the prefab hides those widgets.
    public class BanditKingBriefVM : ViewModel
    {
        private const float FADE_IN = 0.45f;
        private float _t;

        // Raised when the player clicks a button (after the Action runs). The
        // screen subscribes to this to tear down the layer.
        public event Action CloseRequested;

        private readonly Action _onPrimary;
        private readonly Action _onSecondary;

        private string _artSprite, _title, _subline, _flavor, _stakes, _kName, _kType, _primaryText, _secondaryText, _bannerCode;
        private string _sectionLabel, _whyText, _storyText, _storySectionLabel;
        private bool _hasSecondary, _hasBanner, _hasPortrait, _hasWhy, _hasStory;
        private object _portrait;
        private readonly MBBindingList<BriefRowVM> _rows = new MBBindingList<BriefRowVM>();
        private readonly MBBindingList<BriefTagVM> _tags = new MBBindingList<BriefTagVM>();
        private bool _hasTags;

        public BanditKingBriefVM(BanditKingBriefData data)
        {
            if (data == null) data = new BanditKingBriefData();

            _onPrimary = data.OnPrimary;
            _onSecondary = data.OnSecondary;

            _artSprite = data.ArtSprite ?? "";
            _title = data.Title ?? "";
            _subline = data.Subline ?? "";
            _flavor = data.Flavor ?? "";
            _stakes = data.Stakes ?? "";
            _kName = data.KName ?? "";
            _kType = data.KType ?? "";
            _primaryText = string.IsNullOrEmpty(data.PrimaryText) ? BandItPlus.Localization.Get("bp_kingbriefvm_001", "Continue") : data.PrimaryText;
            _secondaryText = data.SecondaryText ?? "";
            _hasSecondary = !string.IsNullOrEmpty(data.SecondaryText);
            _sectionLabel = string.IsNullOrEmpty(data.SectionLabel) ? BandItPlus.Localization.Get("bp_kingbriefvm_002", "— WHAT THE SCOUTS REPORT —") : data.SectionLabel;
            _whyText = data.WhyText ?? "";
            _hasWhy = !string.IsNullOrEmpty(data.WhyText);
            _storyText = data.StoryText ?? "";
            _storySectionLabel = string.IsNullOrEmpty(data.StorySectionLabel) ? BandItPlus.Localization.Get("bp_kingbriefvm_003", "— THE TALE —") : data.StorySectionLabel;
            _hasStory = !string.IsNullOrEmpty(data.StoryText);

            // rows (each carries a per-row accent color for badge + glyph tint)
            try
            {
                if (data.Rows != null)
                    foreach (var r in data.Rows)
                        _rows.Add(new BriefRowVM(r.icon, r.label, r.value, r.accent));
            }
            catch (Exception ex) { Log("rows build fail: " + ex.Message); }

            // tag chips (visual pass: colored category tags in the header band)
            try
            {
                if (data.Tags != null)
                    foreach (var t in data.Tags)
                        _tags.Add(new BriefTagVM(t.text, t.accent));
                _hasTags = _tags.Count > 0;
            }
            catch (Exception ex) { Log("tags build fail: " + ex.Message); }

            // portrait (CharacterImageIdentifierVM via reflection; object)
            try
            {
                _portrait = BuildPortrait(data.PortraitHero);
                _hasPortrait = _portrait != null;
            }
            catch (Exception ex) { Log("portrait build fail: " + ex.Message); _portrait = null; _hasPortrait = false; }

            // banner (serialized clan-banner code string for BannerTableauWidget)
            try
            {
                _bannerCode = BuildBannerCode(data.BannerClan);
                _hasBanner = !string.IsNullOrEmpty(_bannerCode);
            }
            catch (Exception ex) { Log("banner build fail: " + ex.Message); _bannerCode = ""; _hasBanner = false; }
        }

        // ---- bound content ---------------------------------------------------
        [DataSourceProperty] public string ArtSprite { get { return _artSprite; } set { if (_artSprite != value) { _artSprite = value; OnPropertyChanged(nameof(ArtSprite)); } } }
        [DataSourceProperty] public object Portrait { get { return _portrait; } set { if (_portrait != value) { _portrait = value; OnPropertyChangedWithValue(value, nameof(Portrait)); } } }
        [DataSourceProperty] public bool HasPortrait { get { return _hasPortrait; } set { if (_hasPortrait != value) { _hasPortrait = value; OnPropertyChanged(nameof(HasPortrait)); } } }
        [DataSourceProperty] public string BannerCode { get { return _bannerCode; } set { if (_bannerCode != value) { _bannerCode = value; OnPropertyChanged(nameof(BannerCode)); } } }
        [DataSourceProperty] public bool HasBanner { get { return _hasBanner; } set { if (_hasBanner != value) { _hasBanner = value; OnPropertyChanged(nameof(HasBanner)); } } }
        [DataSourceProperty] public string Title { get { return _title; } set { if (_title != value) { _title = value; OnPropertyChanged(nameof(Title)); } } }
        [DataSourceProperty] public string Subline { get { return _subline; } set { if (_subline != value) { _subline = value; OnPropertyChanged(nameof(Subline)); } } }
        [DataSourceProperty] public string Flavor { get { return _flavor; } set { if (_flavor != value) { _flavor = value; OnPropertyChanged(nameof(Flavor)); } } }
        [DataSourceProperty] public string Stakes { get { return _stakes; } set { if (_stakes != value) { _stakes = value; OnPropertyChanged(nameof(Stakes)); } } }
        [DataSourceProperty] public string KName { get { return _kName; } set { if (_kName != value) { _kName = value; OnPropertyChanged(nameof(KName)); } } }
        [DataSourceProperty] public string KType { get { return _kType; } set { if (_kType != value) { _kType = value; OnPropertyChanged(nameof(KType)); } } }
        [DataSourceProperty] public string SectionLabel { get { return _sectionLabel; } set { if (_sectionLabel != value) { _sectionLabel = value; OnPropertyChanged(nameof(SectionLabel)); } } }
        [DataSourceProperty] public string WhyText { get { return _whyText; } set { if (_whyText != value) { _whyText = value; OnPropertyChanged(nameof(WhyText)); } } }
        [DataSourceProperty] public bool HasWhy { get { return _hasWhy; } set { if (_hasWhy != value) { _hasWhy = value; OnPropertyChanged(nameof(HasWhy)); } } }
        [DataSourceProperty] public string StoryText { get { return _storyText; } set { if (_storyText != value) { _storyText = value; OnPropertyChanged(nameof(StoryText)); } } }
        [DataSourceProperty] public string StorySectionLabel { get { return _storySectionLabel; } set { if (_storySectionLabel != value) { _storySectionLabel = value; OnPropertyChanged(nameof(StorySectionLabel)); } } }
        [DataSourceProperty] public bool HasStory { get { return _hasStory; } set { if (_hasStory != value) { _hasStory = value; OnPropertyChanged(nameof(HasStory)); } } }
        [DataSourceProperty] public MBBindingList<BriefRowVM> Rows { get { return _rows; } }
        [DataSourceProperty] public MBBindingList<BriefTagVM> Tags { get { return _tags; } }
        [DataSourceProperty] public bool HasTags { get { return _hasTags; } set { if (_hasTags != value) { _hasTags = value; OnPropertyChanged(nameof(HasTags)); } } }
        [DataSourceProperty] public string PrimaryText { get { return _primaryText; } set { if (_primaryText != value) { _primaryText = value; OnPropertyChanged(nameof(PrimaryText)); } } }
        [DataSourceProperty] public string SecondaryText { get { return _secondaryText; } set { if (_secondaryText != value) { _secondaryText = value; OnPropertyChanged(nameof(SecondaryText)); } } }
        [DataSourceProperty] public bool HasSecondary { get { return _hasSecondary; } set { if (_hasSecondary != value) { _hasSecondary = value; OnPropertyChanged(nameof(HasSecondary)); } } }

        // ---- cascade entrance alphas (copied from BpJailbreakBriefVM) ---------
        // INIT to 1f so the first Tick (which sets 0) is a real change → hides,
        // then the ramp fades in. The 1-frame flash is masked by the curtain.
        private float _artAlpha = 1f, _headerAlpha = 1f, _flavorAlpha = 1f, _rowsAlpha = 1f, _buttonsAlpha = 1f;
        private float _tagsAlpha = 1f, _titleGlowAlpha = 1f, _headerDriftY, _rowsDriftY, _buttonsDriftY;
        [DataSourceProperty] public float ArtAlpha { get { return _artAlpha; } set { if (value != _artAlpha) { _artAlpha = value; OnPropertyChangedWithValue(value, "ArtAlpha"); } } }
        [DataSourceProperty] public float HeaderAlpha { get { return _headerAlpha; } set { if (value != _headerAlpha) { _headerAlpha = value; OnPropertyChangedWithValue(value, "HeaderAlpha"); } } }
        [DataSourceProperty] public float FlavorAlpha { get { return _flavorAlpha; } set { if (value != _flavorAlpha) { _flavorAlpha = value; OnPropertyChangedWithValue(value, "FlavorAlpha"); } } }
        [DataSourceProperty] public float RowsAlpha { get { return _rowsAlpha; } set { if (value != _rowsAlpha) { _rowsAlpha = value; OnPropertyChangedWithValue(value, "RowsAlpha"); } } }
        [DataSourceProperty] public float ButtonsAlpha { get { return _buttonsAlpha; } set { if (value != _buttonsAlpha) { _buttonsAlpha = value; OnPropertyChangedWithValue(value, "ButtonsAlpha"); } } }
        // cinematic extras: tag pulse, title flash-glow, slide-in drift offsets
        [DataSourceProperty] public float TagsAlpha { get { return _tagsAlpha; } set { if (value != _tagsAlpha) { _tagsAlpha = value; OnPropertyChangedWithValue(value, "TagsAlpha"); } } }
        [DataSourceProperty] public float TitleGlowAlpha { get { return _titleGlowAlpha; } set { if (value != _titleGlowAlpha) { _titleGlowAlpha = value; OnPropertyChangedWithValue(value, "TitleGlowAlpha"); } } }
        [DataSourceProperty] public float HeaderDriftY { get { return _headerDriftY; } set { if (value != _headerDriftY) { _headerDriftY = value; OnPropertyChangedWithValue(value, "HeaderDriftY"); } } }
        [DataSourceProperty] public float RowsDriftY { get { return _rowsDriftY; } set { if (value != _rowsDriftY) { _rowsDriftY = value; OnPropertyChangedWithValue(value, "RowsDriftY"); } } }
        [DataSourceProperty] public float ButtonsDriftY { get { return _buttonsDriftY; } set { if (value != _buttonsDriftY) { _buttonsDriftY = value; OnPropertyChangedWithValue(value, "ButtonsDriftY"); } } }

        // open-fade curtain; cinematic ember lift: pure black at the start, a warm
        // firelight tint blooms mid-fade (peak at a=0.5), then burns off to clear.
        public string CurtainHex
        {
            get
            {
                float a = _t >= FADE_IN ? 0f : 1f - (_t / FADE_IN);
                float mid = a * (1f - a) * 4f; // 0 at ends, 1 at mid-fade
                int r = (int)(46f * mid), g = (int)(22f * mid), b = (int)(6f * mid);
                int al = (int)(a * 255f);
                return "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2") + al.ToString("X2");
            }
        }

        // CINEMATIC ENTRANCE (one of a kind): ember curtain lifts → art fades →
        // header SLIDES DOWN into place → gold FLASH ignites behind the title and
        // settles into a slow breathing glow → tag chips pop with a living pulse →
        // rows RISE UP into place → buttons rise last. Slide offsets ride
        // PositionYOffset (the proven BanditCinematic.xml binding); everything
        // eases out so motion lands soft, never bouncy.
        public void Tick(float dt)
        {
            _t += dt;

            ArtAlpha = Ease(_t, 0.15f, 0.55f);                    // art: slow reveal

            float headerE = Ease(_t, 0.30f, 0.42f);               // header slides down -16 → 0
            HeaderAlpha = headerE;
            HeaderDriftY = -16f * (1f - headerE);

            // title flash: ignites to full, decays to a low ember, then breathes
            float flash = Ease(_t, 0.42f, 0.22f) * (1f - 0.72f * Ease(_t, 0.68f, 0.70f));
            float breathe = _t > 1.4f ? 1f + 0.22f * (float)Math.Sin((_t - 1.4f) * 1.7f) : 1f;
            TitleGlowAlpha = flash * breathe;

            FlavorAlpha = Ease(_t, 0.50f, 0.36f);                 // flavor prose

            float tagsE = Ease(_t, 0.58f, 0.30f);                 // tags pop + living pulse
            float pulse = 0.88f + 0.12f * (float)Math.Sin(_t * 2.6f);
            TagsAlpha = tagsE * pulse;

            float rowsE = Ease(_t, 0.64f, 0.44f);                 // rows rise +18 → 0
            RowsAlpha = rowsE;
            RowsDriftY = 18f * (1f - rowsE);

            float btnE = Ease(_t, 0.84f, 0.38f);                  // buttons rise last
            ButtonsAlpha = btnE;
            ButtonsDriftY = 12f * (1f - btnE);

            if (_t < FADE_IN + 0.05f) OnPropertyChanged(nameof(CurtainHex));
        }

        // ease-out quad: fast in, gentle settle
        private static float Ease(float t, float start, float dur)
        {
            float p = (t - start) / dur;
            if (p <= 0f) return 0f;
            if (p >= 1f) return 1f;
            return 1f - (1f - p) * (1f - p);
        }

        // ---- buttons ---------------------------------------------------------
        public void ExecutePrimary()
        {
            try { _onPrimary?.Invoke(); } catch (Exception ex) { Log("primary action fail: " + ex.Message); }
            RaiseClose();
        }

        public void ExecuteSecondary()
        {
            try { _onSecondary?.Invoke(); } catch (Exception ex) { Log("secondary action fail: " + ex.Message); }
            RaiseClose();
        }

        private void RaiseClose()
        {
            try { CloseRequested?.Invoke(); } catch (Exception ex) { Log("close raise fail: " + ex.Message); }
        }

        // ---- portrait (namespace-resilient reflection; from CampInfoPanel) ---
        private static object BuildPortrait(TaleWorlds.CampaignSystem.Hero hero)
        {
            if (hero?.CharacterObject == null) return null;
            System.Type charCodeType = null, charImgIdVMType = null, charImgIdType = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                charCodeType    = charCodeType    ?? asm.GetType("TaleWorlds.Core.CharacterCode");
                charImgIdVMType = charImgIdVMType ?? asm.GetType("TaleWorlds.Core.ViewModelCollection.ImageIdentifiers.CharacterImageIdentifierVM");
                charImgIdType   = charImgIdType   ?? asm.GetType("TaleWorlds.Core.ImageIdentifiers.CharacterImageIdentifier");
            }
            if (charCodeType == null || charImgIdVMType == null) return null;

            var heroCharType = hero.CharacterObject.GetType();
            System.Reflection.MethodInfo createFrom = null;
            foreach (var m in charCodeType.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public))
            {
                if (m.Name != "CreateFrom") continue;
                var p = m.GetParameters();
                if (p.Length == 1 && p[0].ParameterType.IsAssignableFrom(heroCharType)) { createFrom = m; break; }
            }
            if (createFrom == null) return null;
            var charCode = createFrom.Invoke(null, new object[] { hero.CharacterObject });
            if (charCode == null) return null;

            foreach (var c in charImgIdVMType.GetConstructors())
            {
                var cp = c.GetParameters();
                if (cp.Length == 1 && cp[0].ParameterType.IsAssignableFrom(charCode.GetType()))
                    return c.Invoke(new object[] { charCode });
            }
            if (charImgIdType != null)
            {
                object charImgId = null;
                foreach (var c in charImgIdType.GetConstructors())
                {
                    var cp = c.GetParameters();
                    if (cp.Length == 1 && cp[0].ParameterType.IsAssignableFrom(charCode.GetType()))
                    { charImgId = c.Invoke(new object[] { charCode }); break; }
                }
                if (charImgId != null)
                    foreach (var c in charImgIdVMType.GetConstructors())
                    {
                        var cp = c.GetParameters();
                        if (cp.Length == 1 && cp[0].ParameterType.IsAssignableFrom(charImgIdType))
                            return c.Invoke(new object[] { charImgId });
                    }
            }
            return null;
        }

        // ---- banner (serialized clan-banner code string) ---------------------
        private static string BuildBannerCode(TaleWorlds.CampaignSystem.Clan clan)
        {
            try { return clan?.Banner?.Serialize(); }
            catch { return null; }
        }

        private static void Log(string msg)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-KingBrief] " + msg); }
            catch { }
        }
    }
}
