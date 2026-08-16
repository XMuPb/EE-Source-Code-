using System;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace BandItPlus.UI.Console
{
    // V3 (2026-05-21): Royal Codex Console view-model. Replaces VMv2.
    // Open-book layout: left page = TOC bookmarks + Live Status, right page =
    // active chapter. 4 chapters: Bandit Power / Hideouts / Spoils / Diagnostics.
    public class BanditPlusConsoleVMv3 : ViewModel
    {
        public enum Category { BanditPower = 0, Hideouts = 1, Spoils = 2, Diagnostics = 3, Info = 4, Raids = 5, Hud = 6 }

        private Category _selectedCategory = Category.BanditPower;

        // Service.Tick increments 0->1 over ~0.3s on Open. Drives root widget
        // AlphaFactor + scale animation in the prefab.
        private float _openProgress;
        public float OpenProgress
        {
            get => _openProgress;
            set
            {
                if (Math.Abs(_openProgress - value) < 0.001f) return;
                _openProgress = value;
                OnPropertyChangedWithValue(value, nameof(OpenProgress));
            }
        }

        // Animation: chapter content fade-in on tab switch (A)
        private float _chapterContentAlpha = 1.0f;
        public float ChapterContentAlpha
        {
            get => _chapterContentAlpha;
            set
            {
                if (Math.Abs(_chapterContentAlpha - value) < 0.001f) return;
                _chapterContentAlpha = value;
                OnPropertyChangedWithValue(value, nameof(ChapterContentAlpha));
            }
        }

        // Animation: chapter content slide-in from right on tab switch (60 base + slide offset)
        private float _chapterContentMarginLeft = 60f;
        public float ChapterContentMarginLeft
        {
            get => _chapterContentMarginLeft;
            set
            {
                if (Math.Abs(_chapterContentMarginLeft - value) < 0.5f) return;
                _chapterContentMarginLeft = value;
                OnPropertyChangedWithValue(value, nameof(ChapterContentMarginLeft));
            }
        }

        // Animation: active stripe pulse (B)
        private float _activeStripeAlpha = 1.0f;
        public float ActiveStripeAlpha
        {
            get => _activeStripeAlpha;
            set
            {
                if (Math.Abs(_activeStripeAlpha - value) < 0.001f) return;
                _activeStripeAlpha = value;
                OnPropertyChangedWithValue(value, nameof(ActiveStripeAlpha));
            }
        }

        // Animation: bookmark slide-in cascade on Console open (C)
        private float _bookmark1Offset, _bookmark2Offset, _bookmark3Offset, _bookmark4Offset, _bookmark5Offset, _bookmark6Offset, _bookmark7Offset;
        public float Bookmark1Offset { get => _bookmark1Offset; set { if (Math.Abs(_bookmark1Offset - value) < 0.5f) return; _bookmark1Offset = value; OnPropertyChangedWithValue(value, nameof(Bookmark1Offset)); } }
        public float Bookmark2Offset { get => _bookmark2Offset; set { if (Math.Abs(_bookmark2Offset - value) < 0.5f) return; _bookmark2Offset = value; OnPropertyChangedWithValue(value, nameof(Bookmark2Offset)); } }
        public float Bookmark3Offset { get => _bookmark3Offset; set { if (Math.Abs(_bookmark3Offset - value) < 0.5f) return; _bookmark3Offset = value; OnPropertyChangedWithValue(value, nameof(Bookmark3Offset)); } }
        public float Bookmark4Offset { get => _bookmark4Offset; set { if (Math.Abs(_bookmark4Offset - value) < 0.5f) return; _bookmark4Offset = value; OnPropertyChangedWithValue(value, nameof(Bookmark4Offset)); } }
        public float Bookmark5Offset { get => _bookmark5Offset; set { if (Math.Abs(_bookmark5Offset - value) < 0.5f) return; _bookmark5Offset = value; OnPropertyChangedWithValue(value, nameof(Bookmark5Offset)); } }
        public float Bookmark6Offset { get => _bookmark6Offset; set { if (Math.Abs(_bookmark6Offset - value) < 0.5f) return; _bookmark6Offset = value; OnPropertyChangedWithValue(value, nameof(Bookmark6Offset)); } }
        public float Bookmark7Offset { get => _bookmark7Offset; set { if (Math.Abs(_bookmark7Offset - value) < 0.5f) return; _bookmark7Offset = value; OnPropertyChangedWithValue(value, nameof(Bookmark7Offset)); } }

        // Animation: drop cap fade-in on chapter switch (D)
        private float _dropCapAlpha = 1.0f;
        public float DropCapAlpha
        {
            get => _dropCapAlpha;
            set
            {
                if (Math.Abs(_dropCapAlpha - value) < 0.001f) return;
                _dropCapAlpha = value;
                OnPropertyChangedWithValue(value, nameof(DropCapAlpha));
            }
        }

        // Animation: Restore Verses pulse when chapter modified (E)
        private float _restoreColorFactor = 1.0f;
        public float RestoreColorFactor
        {
            get => _restoreColorFactor;
            set
            {
                if (Math.Abs(_restoreColorFactor - value) < 0.001f) return;
                _restoreColorFactor = value;
                OnPropertyChangedWithValue(value, nameof(RestoreColorFactor));
            }
        }

        public bool IsChapterModified
        {
            get
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) return false;
                try
                {
                    switch (_selectedCategory)
                    {
                        case Category.BanditPower:
                            return mcm.EnableBanditPower != true
                                || mcm.BanditMaxPartySize != 60
                                || mcm.BanditPowerRampYears != 6
                                || Math.Abs(mcm.BanditMountDamageMultiplier - 2.0f) > 0.01f
                                || mcm.EnableBanditBackup != true
                                || mcm.BanditBackupChance != 35
                                || mcm.BanditBackupMaxParties != 2
                                || mcm.BanditBackupArrivalDelay != 30
                                || mcm.EnableBanditCaptivity != true
                                || mcm.CaptivityEscapeDifficulty != 3;
                        case Category.Hud:
                            return mcm.ShowMapHud != false   // 07-10: preview HUD defaults OFF
                                || mcm.ShowRankUpToast != true
                                || mcm.ShowHudGlow != true
                                || mcm.ShowQuestAlert != true
                                || mcm.ShowChiefCard != true
                                || mcm.SneakShadowHud != true
                                || mcm.ShowBattlePanel != true
                                || mcm.HideoutStealthAlarm != true
                                || mcm.ShowMadKingPanels != true
                                || mcm.RebellionPanelsAsToasts != false
                                || mcm.RebellionPanelsBanditOnly != true;
                        case Category.Hideouts:
                            return mcm.PeacefulEncounters != true
                                || mcm.HideoutCountPerClan != 2
                                || mcm.RespectBiomes != true
                                || mcm.AllowVanillaHideouts != true
                                || Math.Abs(mcm.HideoutBossStrength - 1.0f) > 0.01f;
                        case Category.Spoils:
                            return Math.Abs(mcm.CoinDropMultiplier - 1.0f) > 0.01f
                                || mcm.DropKeepsakes != true
                                || mcm.UseEliteBonus != true
                                || Math.Abs(mcm.KeepsakeChanceMultiplier - 1.0f) > 0.01f
                                || mcm.ShowKeepsakeNotification != true;
                        case Category.Raids:
                            return Math.Abs(mcm.RaidSpawnChancePerHour - 0.04f) > 0.001f
                                || Math.Abs(mcm.DaysBetweenRaidsPerCulture - 14f) > 0.01f
                                || mcm.MaxConcurrentRaiders != 3
                                || Math.Abs(mcm.MaxRaidRange - 200f) > 0.01f
                                || mcm.RaidMusterBaseSize != 45
                                || Math.Abs(mcm.RaidYearlyEscalationPercent - 25f) > 0.01f;
                        default:
                            return false;
                    }
                }
                catch { return false; }
            }
        }

        public BanditPlusConsoleVMv3()
        {
            PollDiagnostics();
        }

        // Chapter visibility (right page)
        public bool IsCategoryPowerVisible => _selectedCategory == Category.BanditPower;
        public bool IsCategoryHideoutsVisible => _selectedCategory == Category.Hideouts;
        public bool IsCategorySpoilsVisible => _selectedCategory == Category.Spoils;
        public bool IsCategoryDiagnosticsVisible => _selectedCategory == Category.Diagnostics;
        public bool IsCategoryInfoVisible => _selectedCategory == Category.Info;
        public bool IsCategoryRaidsVisible => _selectedCategory == Category.Raids;
        public bool IsCategoryHudVisible => _selectedCategory == Category.Hud;

        // Bookmark active state (left page)
        public bool IsPowerSelected => _selectedCategory == Category.BanditPower;
        public bool IsHideoutsSelected => _selectedCategory == Category.Hideouts;
        public bool IsSpoilsSelected => _selectedCategory == Category.Spoils;
        public bool IsDiagnosticsSelected => _selectedCategory == Category.Diagnostics;
        public bool IsInfoSelected => _selectedCategory == Category.Info;
        public bool IsRaidsSelected => _selectedCategory == Category.Raids;
        public bool IsPowerUnselected => _selectedCategory != Category.BanditPower;
        public bool IsHideoutsUnselected => _selectedCategory != Category.Hideouts;
        public bool IsSpoilsUnselected => _selectedCategory != Category.Spoils;
        public bool IsDiagnosticsUnselected => _selectedCategory != Category.Diagnostics;
        public bool IsInfoUnselected => _selectedCategory != Category.Info;
        public bool IsRaidsUnselected => _selectedCategory != Category.Raids;
        public bool IsHudSelected => _selectedCategory == Category.Hud;
        public bool IsHudUnselected => _selectedCategory != Category.Hud;

        // Wave 4.17.1: Restore Verses only makes sense on chapters that HAVE settings.
        // Diagnostics is read-only telemetry and Info is static — hide the button there.
        public bool IsRestoreVisible => _selectedCategory != Category.Diagnostics
                                     && _selectedCategory != Category.Info;

        // Chapter heading. 07-16 (translator): the header used to be stored pre-split
        // ("andit Power", plus a hard-coded first letter shared across words) which a
        // translator can't translate and which breaks in other languages (Hideouts vs
        // HUD both forced to "H"). Now each chapter is ONE whole translatable title and
        // we derive the drop-cap + remainder at runtime, so every language gets its own
        // correct first letter. bp_consolevmv3_001-007 are obsolete.
        private string ChapterFullTitle
        {
            get
            {
                switch (_selectedCategory)
                {
                    case Category.BanditPower: return BandItPlus.Localization.Get("bp_consolevmv3_008", "Bandit Power");
                    case Category.Hideouts:    return BandItPlus.Localization.Get("bp_consolevmv3_009", "Hideouts");
                    case Category.Spoils:      return BandItPlus.Localization.Get("bp_consolevmv3_010", "Spoils of Battle");
                    case Category.Diagnostics: return BandItPlus.Localization.Get("bp_consolevmv3_011", "Live Status");
                    case Category.Info:        return BandItPlus.Localization.Get("bp_consolevmv3_012", "Info");
                    case Category.Raids:       return BandItPlus.Localization.Get("bp_consolevmv3_013", "Raids & War Bands");
                    case Category.Hud:         return BandItPlus.Localization.Get("bp_consolevmv3_014", "HUD");
                    default: return "";
                }
            }
        }

        public string ChapterDropCap
        {
            get
            {
                string t = ChapterFullTitle;
                return string.IsNullOrEmpty(t) ? "?" : t.Substring(0, 1);
            }
        }

        public string ChapterTitleRest
        {
            get
            {
                string t = ChapterFullTitle;
                return (string.IsNullOrEmpty(t) || t.Length < 2) ? "" : t.Substring(1);
            }
        }

        public string ChapterMarkText
        {
            get
            {
                switch (_selectedCategory)
                {
                    case Category.BanditPower: return BandItPlus.Localization.Get("bp_consolevmv3_015", "─ Cap. I ─");
                    case Category.Hideouts:    return BandItPlus.Localization.Get("bp_consolevmv3_016", "─ Cap. II ─");
                    case Category.Spoils:      return BandItPlus.Localization.Get("bp_consolevmv3_017", "─ Cap. III ─");
                    case Category.Raids:       return BandItPlus.Localization.Get("bp_consolevmv3_018", "─ Cap. IV ─");
                    case Category.Hud:         return BandItPlus.Localization.Get("bp_consolevmv3_019", "─ Cap. V ─");
                    case Category.Diagnostics: return BandItPlus.Localization.Get("bp_consolevmv3_020", "─ Cap. VI ─");
                    case Category.Info:        return BandItPlus.Localization.Get("bp_consolevmv3_021", "─ Cap. VII ─");
                    default: return "";
                }
            }
        }

        public string ChapterEpigraph
        {
            get
            {
                switch (_selectedCategory)
                {
                    case Category.BanditPower: return BandItPlus.Localization.Get("bp_consolevmv3_022", "Of the brigands and outlaws who plague these lands, the Empire wages perpetual war.");
                    case Category.Hideouts:    return BandItPlus.Localization.Get("bp_consolevmv3_023", "Hidden camps in mountain and marsh. To root them out, a captain needs scouts and resolve.");
                    case Category.Spoils:      return BandItPlus.Localization.Get("bp_consolevmv3_024", "What the wolves take from the merchants returns to the strongbox of him who slays them.");
                    case Category.Diagnostics: return BandItPlus.Localization.Get("bp_consolevmv3_025", "The chronicle records what the eye cannot count.");
                    case Category.Info:        return BandItPlus.Localization.Get("bp_consolevmv3_026", "Of the chronicler who set this codex down, and the hour of its making.");
                    case Category.Raids:       return BandItPlus.Localization.Get("bp_consolevmv3_027", "When the muster horn sounds in the hills, the villages bar their gates — and pray.");
                    case Category.Hud:         return BandItPlus.Localization.Get("bp_consolevmv3_028", "What the eye marks upon the road — your name, your infamy, writ in fire.");
                    default: return "";
                }
            }
        }

        public void ExecuteSelectCategoryPower() { Log("Click: BanditPower"); SetCategory(Category.BanditPower); }
        public void ExecuteSelectCategoryHideouts() { Log("Click: Hideouts"); SetCategory(Category.Hideouts); }
        public void ExecuteSelectCategorySpoils() { Log("Click: Spoils"); SetCategory(Category.Spoils); }
        public void ExecuteSelectCategoryDiagnostics() { Log("Click: Diagnostics"); SetCategory(Category.Diagnostics); }
        public void ExecuteSelectCategoryInfo() { Log("Click: Info"); SetCategory(Category.Info); }
        public void ExecuteSelectCategoryRaids() { Log("Click: Raids"); SetCategory(Category.Raids); }
        public void ExecuteSelectCategoryHud() { Log("Click: Hud"); SetCategory(Category.Hud); }

        // Bandit Power settings
        public bool EnableBanditPower
        {
            get => MCMSettings.Instance?.EnableBanditPower ?? true;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("EnableBanditPower setter - MCMSettings null"); return; }
                if (mcm.EnableBanditPower == value) return;
                mcm.EnableBanditPower = value;
                OnPropertyChangedWithValue(value, nameof(EnableBanditPower));
                OnPropertyChangedWithValue(!value, nameof(EnableBanditPowerOff));
            }
        }
        public bool EnableBanditPowerOff => !EnableBanditPower;
        public void ExecuteToggleEnableBanditPower() => EnableBanditPower = !EnableBanditPower;

        // ── Bandit Backup (Wave 4.29.0) ── nearby bandits answer a call for reinforcements.
        public bool EnableBanditBackup
        {
            get => MCMSettings.Instance?.EnableBanditBackup ?? true;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("EnableBanditBackup setter - MCMSettings null"); return; }
                if (mcm.EnableBanditBackup == value) return;
                mcm.EnableBanditBackup = value;
                OnPropertyChangedWithValue(value, nameof(EnableBanditBackup));
                OnPropertyChangedWithValue(!value, nameof(EnableBanditBackupOff));
            }
        }
        public bool EnableBanditBackupOff => !EnableBanditBackup;
        public void ExecuteToggleEnableBanditBackup() => EnableBanditBackup = !EnableBanditBackup;

        public int BanditBackupChance
        {
            get => MCMSettings.Instance?.BanditBackupChance ?? 35;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("BanditBackupChance setter - MCMSettings null"); return; }
                int clamped = value < 0 ? 0 : (value > 100 ? 100 : value);
                if (mcm.BanditBackupChance == clamped) return;
                mcm.BanditBackupChance = clamped;
                OnPropertyChangedWithValue(clamped, nameof(BanditBackupChance));
                OnPropertyChangedWithValue(BanditBackupChanceText, nameof(BanditBackupChanceText));
            }
        }
        public string BanditBackupChanceText => BanditBackupChance.ToString() + "%";
        public void ExecuteIncrementBackupChance() => BanditBackupChance = BanditBackupChance + 5;
        public void ExecuteDecrementBackupChance() => BanditBackupChance = BanditBackupChance - 5;

        public int BanditBackupMaxParties
        {
            get => MCMSettings.Instance?.BanditBackupMaxParties ?? 2;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("BanditBackupMaxParties setter - MCMSettings null"); return; }
                int clamped = value < 0 ? 0 : (value > 5 ? 5 : value);
                if (mcm.BanditBackupMaxParties == clamped) return;
                mcm.BanditBackupMaxParties = clamped;
                OnPropertyChangedWithValue(clamped, nameof(BanditBackupMaxParties));
                OnPropertyChangedWithValue(BanditBackupMaxPartiesText, nameof(BanditBackupMaxPartiesText));
            }
        }
        public string BanditBackupMaxPartiesText => BanditBackupMaxParties.ToString();
        public void ExecuteIncrementBackupMax() => BanditBackupMaxParties = BanditBackupMaxParties + 1;
        public void ExecuteDecrementBackupMax() => BanditBackupMaxParties = BanditBackupMaxParties - 1;

        public int BanditBackupArrivalDelay
        {
            get => MCMSettings.Instance?.BanditBackupArrivalDelay ?? 30;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("BanditBackupArrivalDelay setter - MCMSettings null"); return; }
                int clamped = value < 5 ? 5 : (value > 90 ? 90 : value);
                if (mcm.BanditBackupArrivalDelay == clamped) return;
                mcm.BanditBackupArrivalDelay = clamped;
                OnPropertyChangedWithValue(clamped, nameof(BanditBackupArrivalDelay));
                OnPropertyChangedWithValue(BanditBackupArrivalDelayText, nameof(BanditBackupArrivalDelayText));
            }
        }
        public string BanditBackupArrivalDelayText => BanditBackupArrivalDelay.ToString() + "s";
        public void ExecuteIncrementArrivalDelay() => BanditBackupArrivalDelay = BanditBackupArrivalDelay + 5;
        public void ExecuteDecrementArrivalDelay() => BanditBackupArrivalDelay = BanditBackupArrivalDelay - 5;

        public bool EnableBanditCaptivity
        {
            get => MCMSettings.Instance?.EnableBanditCaptivity ?? true;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("EnableBanditCaptivity setter - MCMSettings null"); return; }
                if (mcm.EnableBanditCaptivity == value) return;
                mcm.EnableBanditCaptivity = value;
                OnPropertyChangedWithValue(value, nameof(EnableBanditCaptivity));
                OnPropertyChangedWithValue(!value, nameof(EnableBanditCaptivityOff));
            }
        }
        public bool EnableBanditCaptivityOff => !EnableBanditCaptivity;
        public void ExecuteToggleEnableBanditCaptivity() => EnableBanditCaptivity = !EnableBanditCaptivity;

        public int CaptivityEscapeDifficulty
        {
            get => MCMSettings.Instance?.CaptivityEscapeDifficulty ?? 3;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("CaptivityEscapeDifficulty setter - MCMSettings null"); return; }
                int clamped = value < 1 ? 1 : (value > 5 ? 5 : value);
                if (mcm.CaptivityEscapeDifficulty == clamped) return;
                mcm.CaptivityEscapeDifficulty = clamped;
                OnPropertyChangedWithValue(clamped, nameof(CaptivityEscapeDifficulty));
                OnPropertyChangedWithValue(CaptivityEscapeDifficultyText, nameof(CaptivityEscapeDifficultyText));
            }
        }
        public string CaptivityEscapeDifficultyText => CaptivityEscapeDifficulty.ToString();
        public void ExecuteIncrementEscapeDifficulty() => CaptivityEscapeDifficulty = CaptivityEscapeDifficulty + 1;
        public void ExecuteDecrementEscapeDifficulty() => CaptivityEscapeDifficulty = CaptivityEscapeDifficulty - 1;

        public int BanditMaxPartySize
        {
            get => MCMSettings.Instance?.BanditMaxPartySize ?? 60;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("BanditMaxPartySize setter - MCMSettings null"); return; }
                // Wave 4.15.0 (2026-06-08): raised upper cap from 120 to 500. Raid parties
                // were capping at 120 troops and getting intercepted in transit; 500 gives
                // bandit chiefs the strength to actually reach villages + win raids.
                int clamped = value < 30 ? 30 : (value > 500 ? 500 : value);
                if (mcm.BanditMaxPartySize == clamped) return;
                mcm.BanditMaxPartySize = clamped;
                OnPropertyChangedWithValue(clamped, nameof(BanditMaxPartySize));
                OnPropertyChangedWithValue(BanditMaxPartySizeText, nameof(BanditMaxPartySizeText));
            }
        }
        public string BanditMaxPartySizeText => BanditMaxPartySize.ToString();
        public void ExecuteIncrementMaxPartySize() => BanditMaxPartySize = BanditMaxPartySize + 5;
        public void ExecuteDecrementMaxPartySize() => BanditMaxPartySize = BanditMaxPartySize - 5;

        // Wave 4.16.0 (2026-06-11): raid muster size — troops a war band gathers at
        // its hideout before departing to raid (scaled by power + campaign year).
        public int RaidMusterBaseSize
        {
            get => MCMSettings.Instance?.RaidMusterBaseSize ?? 45;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("RaidMusterBaseSize setter - MCMSettings null"); return; }
                int clamped = value < 20 ? 20 : (value > 200 ? 200 : value);
                if (mcm.RaidMusterBaseSize == clamped) return;
                mcm.RaidMusterBaseSize = clamped;
                OnPropertyChangedWithValue(clamped, nameof(RaidMusterBaseSize));
                OnPropertyChangedWithValue(RaidMusterBaseSizeText, nameof(RaidMusterBaseSizeText));
            }
        }
        public string RaidMusterBaseSizeText => RaidMusterBaseSize.ToString();
        public void ExecuteIncrementRaidMuster() => RaidMusterBaseSize = RaidMusterBaseSize + 5;
        public void ExecuteDecrementRaidMuster() => RaidMusterBaseSize = RaidMusterBaseSize - 5;

        // Wave 4.16.0: yearly escalation % — how much stronger/thirstier/more
        // demanding the bandits get per in-game year (0 = off, capped 4x overall).
        public int RaidYearlyEscalation
        {
            get => (int)(MCMSettings.Instance?.RaidYearlyEscalationPercent ?? 25f);
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("RaidYearlyEscalation setter - MCMSettings null"); return; }
                int clamped = value < 0 ? 0 : (value > 100 ? 100 : value);
                if ((int)mcm.RaidYearlyEscalationPercent == clamped) return;
                mcm.RaidYearlyEscalationPercent = clamped;
                OnPropertyChangedWithValue(clamped, nameof(RaidYearlyEscalation));
                OnPropertyChangedWithValue(RaidYearlyEscalationText, nameof(RaidYearlyEscalationText));
            }
        }
        public string RaidYearlyEscalationText => RaidYearlyEscalation + "%";
        public void ExecuteIncrementRaidEscalation() => RaidYearlyEscalation = RaidYearlyEscalation + 5;
        public void ExecuteDecrementRaidEscalation() => RaidYearlyEscalation = RaidYearlyEscalation - 5;

        // Wave 4.17.0 (2026-06-11): raid frequency knobs for the new Raids chapter.
        // Hourly spawn chance, shown as percent (MCM stores a 0-1 fraction).
        public int RaidChancePercent
        {
            get => (int)Math.Round((MCMSettings.Instance?.RaidSpawnChancePerHour ?? 0.04f) * 100f);
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("RaidChancePercent setter - MCMSettings null"); return; }
                int clamped = value < 1 ? 1 : (value > 20 ? 20 : value);
                if ((int)Math.Round(mcm.RaidSpawnChancePerHour * 100f) == clamped) return;
                mcm.RaidSpawnChancePerHour = clamped / 100f;
                OnPropertyChangedWithValue(clamped, nameof(RaidChancePercent));
                OnPropertyChangedWithValue(RaidChancePercentText, nameof(RaidChancePercentText));
            }
        }
        public string RaidChancePercentText => RaidChancePercent + "%";
        public void ExecuteIncrementRaidChance() => RaidChancePercent = RaidChancePercent + 1;
        public void ExecuteDecrementRaidChance() => RaidChancePercent = RaidChancePercent - 1;

        public int RaidCooldownDays
        {
            get => (int)(MCMSettings.Instance?.DaysBetweenRaidsPerCulture ?? 14f);
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("RaidCooldownDays setter - MCMSettings null"); return; }
                int clamped = value < 1 ? 1 : (value > 60 ? 60 : value);
                if ((int)mcm.DaysBetweenRaidsPerCulture == clamped) return;
                mcm.DaysBetweenRaidsPerCulture = clamped;
                OnPropertyChangedWithValue(clamped, nameof(RaidCooldownDays));
                OnPropertyChangedWithValue(RaidCooldownDaysText, nameof(RaidCooldownDaysText));
            }
        }
        public string RaidCooldownDaysText => RaidCooldownDays.ToString();
        public void ExecuteIncrementRaidCooldown() => RaidCooldownDays = RaidCooldownDays + 1;
        public void ExecuteDecrementRaidCooldown() => RaidCooldownDays = RaidCooldownDays - 1;

        public int MaxConcurrentRaiders
        {
            get => MCMSettings.Instance?.MaxConcurrentRaiders ?? 3;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("MaxConcurrentRaiders setter - MCMSettings null"); return; }
                int clamped = value < 1 ? 1 : (value > 10 ? 10 : value);
                if (mcm.MaxConcurrentRaiders == clamped) return;
                mcm.MaxConcurrentRaiders = clamped;
                OnPropertyChangedWithValue(clamped, nameof(MaxConcurrentRaiders));
                OnPropertyChangedWithValue(MaxConcurrentRaidersText, nameof(MaxConcurrentRaidersText));
            }
        }
        public string MaxConcurrentRaidersText => MaxConcurrentRaiders.ToString();
        public void ExecuteIncrementMaxRaiders() => MaxConcurrentRaiders = MaxConcurrentRaiders + 1;
        public void ExecuteDecrementMaxRaiders() => MaxConcurrentRaiders = MaxConcurrentRaiders - 1;

        public int MaxRaidRange
        {
            get => (int)(MCMSettings.Instance?.MaxRaidRange ?? 200f);
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("MaxRaidRange setter - MCMSettings null"); return; }
                int clamped = value < 50 ? 50 : (value > 500 ? 500 : value);
                if ((int)mcm.MaxRaidRange == clamped) return;
                mcm.MaxRaidRange = clamped;
                OnPropertyChangedWithValue(clamped, nameof(MaxRaidRange));
                OnPropertyChangedWithValue(MaxRaidRangeText, nameof(MaxRaidRangeText));
            }
        }
        public string MaxRaidRangeText => MaxRaidRange.ToString();
        public void ExecuteIncrementRaidRange() => MaxRaidRange = MaxRaidRange + 25;
        public void ExecuteDecrementRaidRange() => MaxRaidRange = MaxRaidRange - 25;

        public int BanditPowerRampYears
        {
            get => MCMSettings.Instance?.BanditPowerRampYears ?? 6;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("BanditPowerRampYears setter - MCMSettings null"); return; }
                int clamped = value < 1 ? 1 : (value > 12 ? 12 : value);
                if (mcm.BanditPowerRampYears == clamped) return;
                mcm.BanditPowerRampYears = clamped;
                OnPropertyChangedWithValue(clamped, nameof(BanditPowerRampYears));
                OnPropertyChangedWithValue(BanditPowerRampYearsText, nameof(BanditPowerRampYearsText));
                OnPropertyChanged(nameof(BanditPowerFactorText));
            }
        }
        public string BanditPowerRampYearsText => BanditPowerRampYears.ToString();
        public void ExecuteIncrementRampYears() => BanditPowerRampYears = BanditPowerRampYears + 1;
        public void ExecuteDecrementRampYears() => BanditPowerRampYears = BanditPowerRampYears - 1;

        public float BanditMountDamageMultiplier
        {
            get => MCMSettings.Instance?.BanditMountDamageMultiplier ?? 2.0f;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("BanditMountDamageMultiplier setter - MCMSettings null"); return; }
                float clamped = value < 1.0f ? 1.0f : (value > 5.0f ? 5.0f : value);
                clamped = (float)Math.Round(clamped * 2f, MidpointRounding.AwayFromZero) / 2f;
                if (Math.Abs(mcm.BanditMountDamageMultiplier - clamped) < 0.001f) return;
                mcm.BanditMountDamageMultiplier = clamped;
                OnPropertyChangedWithValue(clamped, nameof(BanditMountDamageMultiplier));
                OnPropertyChangedWithValue(BanditMountDamageMultiplierText, nameof(BanditMountDamageMultiplierText));
            }
        }
        public string BanditMountDamageMultiplierText => BanditMountDamageMultiplier.ToString("0.0") + "x";
        public void ExecuteIncrementMountDamage() => BanditMountDamageMultiplier = BanditMountDamageMultiplier + 0.5f;
        public void ExecuteDecrementMountDamage() => BanditMountDamageMultiplier = BanditMountDamageMultiplier - 0.5f;

        // Wave 4.28.x: campaign-map HUD on/off. Drives BanditHudService's mount gate;
        // the bar mounts/unmounts on the next tick when this flips.
        public bool ShowMapHud
        {
            get => MCMSettings.Instance?.ShowMapHud ?? true;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("ShowMapHud setter - MCMSettings null"); return; }
                if (mcm.ShowMapHud == value) return;
                mcm.ShowMapHud = value;
                OnPropertyChangedWithValue(value, nameof(ShowMapHud));
                OnPropertyChangedWithValue(!value, nameof(ShowMapHudOff));
            }
        }
        public bool ShowMapHudOff => !ShowMapHud;
        public void ExecuteToggleShowMapHud() => ShowMapHud = !ShowMapHud;

        // Wave 4.28.x: additional HUD chapter toggles.
        public bool ShowRankUpToast
        {
            get => MCMSettings.Instance?.ShowRankUpToast ?? true;
            set { var mcm = MCMSettings.Instance; if (mcm == null) return; if (mcm.ShowRankUpToast == value) return; mcm.ShowRankUpToast = value; OnPropertyChangedWithValue(value, nameof(ShowRankUpToast)); OnPropertyChangedWithValue(!value, nameof(ShowRankUpToastOff)); }
        }
        public bool ShowRankUpToastOff => !ShowRankUpToast;
        public void ExecuteToggleShowRankUpToast() => ShowRankUpToast = !ShowRankUpToast;

        public bool ShowHudGlow
        {
            get => MCMSettings.Instance?.ShowHudGlow ?? true;
            set { var mcm = MCMSettings.Instance; if (mcm == null) return; if (mcm.ShowHudGlow == value) return; mcm.ShowHudGlow = value; OnPropertyChangedWithValue(value, nameof(ShowHudGlow)); OnPropertyChangedWithValue(!value, nameof(ShowHudGlowOff)); }
        }
        public bool ShowHudGlowOff => !ShowHudGlow;
        public void ExecuteToggleShowHudGlow() => ShowHudGlow = !ShowHudGlow;

        public bool ShowQuestAlert
        {
            get => MCMSettings.Instance?.ShowQuestAlert ?? true;
            set { var mcm = MCMSettings.Instance; if (mcm == null) return; if (mcm.ShowQuestAlert == value) return; mcm.ShowQuestAlert = value; OnPropertyChangedWithValue(value, nameof(ShowQuestAlert)); OnPropertyChangedWithValue(!value, nameof(ShowQuestAlertOff)); }
        }
        public bool ShowQuestAlertOff => !ShowQuestAlert;
        public void ExecuteToggleShowQuestAlert() => ShowQuestAlert = !ShowQuestAlert;

        // 07-10: every BP HUD toggleable from the codex (user request) — chief
        // reveal card, sneak shadow bar, battle AI panel, hideout alarm system.
        public bool ShowChiefCard
        {
            get => MCMSettings.Instance?.ShowChiefCard ?? true;
            set { var mcm = MCMSettings.Instance; if (mcm == null) return; if (mcm.ShowChiefCard == value) return; mcm.ShowChiefCard = value; OnPropertyChangedWithValue(value, nameof(ShowChiefCard)); OnPropertyChangedWithValue(!value, nameof(ShowChiefCardOff)); }
        }
        public bool ShowChiefCardOff => !ShowChiefCard;
        public void ExecuteToggleShowChiefCard() => ShowChiefCard = !ShowChiefCard;

        public bool SneakShadowHud
        {
            get => MCMSettings.Instance?.SneakShadowHud ?? true;
            set { var mcm = MCMSettings.Instance; if (mcm == null) return; if (mcm.SneakShadowHud == value) return; mcm.SneakShadowHud = value; OnPropertyChangedWithValue(value, nameof(SneakShadowHud)); OnPropertyChangedWithValue(!value, nameof(SneakShadowHudOff)); }
        }
        public bool SneakShadowHudOff => !SneakShadowHud;
        public void ExecuteToggleSneakShadowHud() => SneakShadowHud = !SneakShadowHud;

        public bool ShowBattlePanel
        {
            get => MCMSettings.Instance?.ShowBattlePanel ?? true;
            set { var mcm = MCMSettings.Instance; if (mcm == null) return; if (mcm.ShowBattlePanel == value) return; mcm.ShowBattlePanel = value; OnPropertyChangedWithValue(value, nameof(ShowBattlePanel)); OnPropertyChangedWithValue(!value, nameof(ShowBattlePanelOff)); }
        }
        public bool ShowBattlePanelOff => !ShowBattlePanel;
        public void ExecuteToggleShowBattlePanel() => ShowBattlePanel = !ShowBattlePanel;

        // Maps to MCMSettings.HideoutStealthAlarm — the assault suspicion meter,
        // camp alarm and reinforcement wave as one switch.
        public bool HideoutAlarm
        {
            get => MCMSettings.Instance?.HideoutStealthAlarm ?? true;
            set { var mcm = MCMSettings.Instance; if (mcm == null) return; if (mcm.HideoutStealthAlarm == value) return; mcm.HideoutStealthAlarm = value; OnPropertyChangedWithValue(value, nameof(HideoutAlarm)); OnPropertyChangedWithValue(!value, nameof(HideoutAlarmOff)); }
        }
        public bool HideoutAlarmOff => !HideoutAlarm;
        public void ExecuteToggleHideoutAlarm() => HideoutAlarm = !HideoutAlarm;

        // 07-10: Bandit King / rebellion notification family (user request) —
        // the premium BanditKingBrief panels and how rebellion events render.
        public bool ShowMadKingPanels
        {
            get => MCMSettings.Instance?.ShowMadKingPanels ?? true;
            set { var mcm = MCMSettings.Instance; if (mcm == null) return; if (mcm.ShowMadKingPanels == value) return; mcm.ShowMadKingPanels = value; OnPropertyChangedWithValue(value, nameof(ShowMadKingPanels)); OnPropertyChangedWithValue(!value, nameof(ShowMadKingPanelsOff)); }
        }
        public bool ShowMadKingPanelsOff => !ShowMadKingPanels;
        public void ExecuteToggleShowMadKingPanels() => ShowMadKingPanels = !ShowMadKingPanels;

        public bool RebellionAsToasts
        {
            get => MCMSettings.Instance?.RebellionPanelsAsToasts ?? false;
            set { var mcm = MCMSettings.Instance; if (mcm == null) return; if (mcm.RebellionPanelsAsToasts == value) return; mcm.RebellionPanelsAsToasts = value; OnPropertyChangedWithValue(value, nameof(RebellionAsToasts)); OnPropertyChangedWithValue(!value, nameof(RebellionAsToastsOff)); }
        }
        public bool RebellionAsToastsOff => !RebellionAsToasts;
        public void ExecuteToggleRebellionAsToasts() => RebellionAsToasts = !RebellionAsToasts;

        public bool RebellionBanditOnly
        {
            get => MCMSettings.Instance?.RebellionPanelsBanditOnly ?? true;
            set { var mcm = MCMSettings.Instance; if (mcm == null) return; if (mcm.RebellionPanelsBanditOnly == value) return; mcm.RebellionPanelsBanditOnly = value; OnPropertyChangedWithValue(value, nameof(RebellionBanditOnly)); OnPropertyChangedWithValue(!value, nameof(RebellionBanditOnlyOff)); }
        }
        public bool RebellionBanditOnlyOff => !RebellionBanditOnly;
        public void ExecuteToggleRebellionBanditOnly() => RebellionBanditOnly = !RebellionBanditOnly;

        // Hideouts settings
        // (PeacefulEncounters is now a legacy fallback default — superseded by the bandit-origin choice +
        //  per-culture peace; the console toggle was removed 2026-06-28. The MCMSettings property + the
        //  `if (PeacefulEncounters)` fallback checks remain.)
        public int HideoutCountPerClan
        {
            get => MCMSettings.Instance?.HideoutCountPerClan ?? 2;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("HideoutCountPerClan setter - MCMSettings null"); return; }
                int clamped = value < 1 ? 1 : (value > 5 ? 5 : value);
                if (mcm.HideoutCountPerClan == clamped) return;
                mcm.HideoutCountPerClan = clamped;
                OnPropertyChangedWithValue(clamped, nameof(HideoutCountPerClan));
                OnPropertyChangedWithValue(HideoutCountPerClanText, nameof(HideoutCountPerClanText));
            }
        }
        public string HideoutCountPerClanText => HideoutCountPerClan.ToString();
        public void ExecuteIncrementHideoutCount() => HideoutCountPerClan = HideoutCountPerClan + 1;
        public void ExecuteDecrementHideoutCount() => HideoutCountPerClan = HideoutCountPerClan - 1;

        public bool RespectBiomes
        {
            get => MCMSettings.Instance?.RespectBiomes ?? true;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("RespectBiomes setter - MCMSettings null"); return; }
                if (mcm.RespectBiomes == value) return;
                mcm.RespectBiomes = value;
                OnPropertyChangedWithValue(value, nameof(RespectBiomes));
                OnPropertyChangedWithValue(!value, nameof(RespectBiomesOff));
            }
        }
        public bool RespectBiomesOff => !RespectBiomes;
        public void ExecuteToggleRespectBiomes() => RespectBiomes = !RespectBiomes;

        public bool AllowVanillaHideouts
        {
            get => MCMSettings.Instance?.AllowVanillaHideouts ?? true;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("AllowVanillaHideouts setter - MCMSettings null"); return; }
                if (mcm.AllowVanillaHideouts == value) return;
                mcm.AllowVanillaHideouts = value;
                OnPropertyChangedWithValue(value, nameof(AllowVanillaHideouts));
                OnPropertyChangedWithValue(!value, nameof(AllowVanillaHideoutsOff));
            }
        }
        public bool AllowVanillaHideoutsOff => !AllowVanillaHideouts;
        public void ExecuteToggleAllowVanillaHideouts() => AllowVanillaHideouts = !AllowVanillaHideouts;

        public float HideoutBossStrength
        {
            get => MCMSettings.Instance?.HideoutBossStrength ?? 1.0f;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("HideoutBossStrength setter - MCMSettings null"); return; }
                float clamped = value < 1.0f ? 1.0f : (value > 3.0f ? 3.0f : value);
                clamped = (float)Math.Round(clamped * 10f, MidpointRounding.AwayFromZero) / 10f;
                if (Math.Abs(mcm.HideoutBossStrength - clamped) < 0.001f) return;
                mcm.HideoutBossStrength = clamped;
                OnPropertyChangedWithValue(clamped, nameof(HideoutBossStrength));
                OnPropertyChangedWithValue(HideoutBossStrengthText, nameof(HideoutBossStrengthText));
            }
        }
        public string HideoutBossStrengthText => HideoutBossStrength.ToString("0.0") + "x";
        public void ExecuteIncrementBossStrength() => HideoutBossStrength = HideoutBossStrength + 0.1f;
        public void ExecuteDecrementBossStrength() => HideoutBossStrength = HideoutBossStrength - 0.1f;

        // Spoils settings
        public float CoinDropMultiplier
        {
            get => MCMSettings.Instance?.CoinDropMultiplier ?? 1.0f;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("CoinDropMultiplier setter - MCMSettings null"); return; }
                float clamped = value < 0.5f ? 0.5f : (value > 3.0f ? 3.0f : value);
                clamped = (float)Math.Round(clamped * 10f, MidpointRounding.AwayFromZero) / 10f;
                if (Math.Abs(mcm.CoinDropMultiplier - clamped) < 0.001f) return;
                mcm.CoinDropMultiplier = clamped;
                OnPropertyChangedWithValue(clamped, nameof(CoinDropMultiplier));
                OnPropertyChangedWithValue(CoinDropMultiplierText, nameof(CoinDropMultiplierText));
            }
        }
        public string CoinDropMultiplierText => CoinDropMultiplier.ToString("0.0") + "x";
        public void ExecuteIncrementCoinDrop() => CoinDropMultiplier = CoinDropMultiplier + 0.1f;
        public void ExecuteDecrementCoinDrop() => CoinDropMultiplier = CoinDropMultiplier - 0.1f;

        public bool DropKeepsakes
        {
            get => MCMSettings.Instance?.DropKeepsakes ?? true;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("DropKeepsakes setter - MCMSettings null"); return; }
                if (mcm.DropKeepsakes == value) return;
                mcm.DropKeepsakes = value;
                OnPropertyChangedWithValue(value, nameof(DropKeepsakes));
                OnPropertyChangedWithValue(!value, nameof(DropKeepsakesOff));
            }
        }
        public bool DropKeepsakesOff => !DropKeepsakes;
        public void ExecuteToggleDropKeepsakes() => DropKeepsakes = !DropKeepsakes;

        public bool UseEliteBonus
        {
            get => MCMSettings.Instance?.UseEliteBonus ?? true;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("UseEliteBonus setter - MCMSettings null"); return; }
                if (mcm.UseEliteBonus == value) return;
                mcm.UseEliteBonus = value;
                OnPropertyChangedWithValue(value, nameof(UseEliteBonus));
                OnPropertyChangedWithValue(!value, nameof(UseEliteBonusOff));
            }
        }
        public bool UseEliteBonusOff => !UseEliteBonus;
        public void ExecuteToggleUseEliteBonus() => UseEliteBonus = !UseEliteBonus;

        public float KeepsakeChanceMultiplier
        {
            get => MCMSettings.Instance?.KeepsakeChanceMultiplier ?? 1.0f;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("KeepsakeChanceMultiplier setter - MCMSettings null"); return; }
                float clamped = value < 0.5f ? 0.5f : (value > 3.0f ? 3.0f : value);
                clamped = (float)Math.Round(clamped * 10f, MidpointRounding.AwayFromZero) / 10f;
                if (Math.Abs(mcm.KeepsakeChanceMultiplier - clamped) < 0.001f) return;
                mcm.KeepsakeChanceMultiplier = clamped;
                OnPropertyChangedWithValue(clamped, nameof(KeepsakeChanceMultiplier));
                OnPropertyChangedWithValue(KeepsakeChanceMultiplierText, nameof(KeepsakeChanceMultiplierText));
            }
        }
        public string KeepsakeChanceMultiplierText => KeepsakeChanceMultiplier.ToString("0.0") + "x";
        public void ExecuteIncrementKeepsakeChance() => KeepsakeChanceMultiplier = KeepsakeChanceMultiplier + 0.1f;
        public void ExecuteDecrementKeepsakeChance() => KeepsakeChanceMultiplier = KeepsakeChanceMultiplier - 0.1f;

        public bool ShowKeepsakeNotification
        {
            get => MCMSettings.Instance?.ShowKeepsakeNotification ?? true;
            set
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null) { Log("ShowKeepsakeNotification setter - MCMSettings null"); return; }
                if (mcm.ShowKeepsakeNotification == value) return;
                mcm.ShowKeepsakeNotification = value;
                OnPropertyChangedWithValue(value, nameof(ShowKeepsakeNotification));
                OnPropertyChangedWithValue(!value, nameof(ShowKeepsakeNotificationOff));
            }
        }
        public bool ShowKeepsakeNotificationOff => !ShowKeepsakeNotification;
        public void ExecuteToggleShowKeepsakeNotification() => ShowKeepsakeNotification = !ShowKeepsakeNotification;

        public void ExecuteClose()
        {
            Log("Click: ExecuteClose (X button)");
            try { BanditPlusConsoleService.Close(); }
            catch (Exception ex) { Log("ExecuteClose fail: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void SetCategory(Category c)
        {
            if (_selectedCategory == c) return;
            _selectedCategory = c;
            // No-value OnPropertyChanged so Gauntlet re-reads each getter.
            OnPropertyChanged(nameof(IsCategoryPowerVisible));
            OnPropertyChanged(nameof(IsCategoryHideoutsVisible));
            OnPropertyChanged(nameof(IsCategorySpoilsVisible));
            OnPropertyChanged(nameof(IsCategoryDiagnosticsVisible));
            OnPropertyChanged(nameof(IsCategoryInfoVisible));
            OnPropertyChanged(nameof(IsCategoryRaidsVisible));
            OnPropertyChanged(nameof(IsCategoryHudVisible));
            OnPropertyChanged(nameof(IsPowerSelected));
            OnPropertyChanged(nameof(IsHideoutsSelected));
            OnPropertyChanged(nameof(IsSpoilsSelected));
            OnPropertyChanged(nameof(IsDiagnosticsSelected));
            OnPropertyChanged(nameof(IsInfoSelected));
            OnPropertyChanged(nameof(IsRaidsSelected));
            OnPropertyChanged(nameof(IsHudSelected));
            OnPropertyChanged(nameof(IsPowerUnselected));
            OnPropertyChanged(nameof(IsHideoutsUnselected));
            OnPropertyChanged(nameof(IsSpoilsUnselected));
            OnPropertyChanged(nameof(IsDiagnosticsUnselected));
            OnPropertyChanged(nameof(IsInfoUnselected));
            OnPropertyChanged(nameof(IsRaidsUnselected));
            OnPropertyChanged(nameof(IsHudUnselected));
            OnPropertyChanged(nameof(IsRestoreVisible));
            OnPropertyChanged(nameof(ChapterDropCap));
            OnPropertyChanged(nameof(ChapterTitleRest));
            OnPropertyChanged(nameof(ChapterMarkText));
            OnPropertyChanged(nameof(ChapterEpigraph));
            OnPropertyChanged(nameof(IsChapterModified));
            // Animations: fade chapter content + drop cap to 0, slide content from right; Service.Tick ramps back
            ChapterContentAlpha = 0f;
            DropCapAlpha = 0f;
            ChapterContentMarginLeft = 140f; // 60 base + 80 slide offset
            if (c == Category.Diagnostics) PollDiagnostics();
        }

        public void ExecuteResetTabDefaults()
        {
            var mcm = MCMSettings.Instance;
            if (mcm == null) { Log("ExecuteResetTabDefaults: MCMSettings null"); return; }
            switch (_selectedCategory)
            {
                case Category.BanditPower:
                    EnableBanditPower = true;
                    BanditMaxPartySize = 60;
                    BanditPowerRampYears = 6;
                    BanditMountDamageMultiplier = 2.0f;
                    EnableBanditBackup = true;
                    BanditBackupChance = 35;
                    BanditBackupMaxParties = 2;
                    BanditBackupArrivalDelay = 30;
                    EnableBanditCaptivity = true;
                    CaptivityEscapeDifficulty = 3;
                    break;
                case Category.Hud:
                    ShowMapHud = false;   // preview HUD ships next update — default OFF
                    ShowRankUpToast = true;
                    ShowHudGlow = true;
                    ShowQuestAlert = true;
                    ShowChiefCard = true;
                    SneakShadowHud = true;
                    ShowBattlePanel = true;
                    HideoutAlarm = true;
                    ShowMadKingPanels = true;
                    RebellionAsToasts = false;
                    RebellionBanditOnly = true;
                    break;
                case Category.Hideouts:
                    if (MCMSettings.Instance != null) MCMSettings.Instance.PeacefulEncounters = true;
                    HideoutCountPerClan = 2;
                    RespectBiomes = true;
                    AllowVanillaHideouts = true;
                    HideoutBossStrength = 1.0f;
                    break;
                case Category.Spoils:
                    CoinDropMultiplier = 1.0f;
                    DropKeepsakes = true;
                    UseEliteBonus = true;
                    KeepsakeChanceMultiplier = 1.0f;
                    ShowKeepsakeNotification = true;
                    break;
                case Category.Diagnostics:
                    break;
                case Category.Raids:
                    RaidChancePercent = 4;
                    RaidCooldownDays = 14;
                    MaxConcurrentRaiders = 3;
                    MaxRaidRange = 200;
                    RaidMusterBaseSize = 45;
                    RaidYearlyEscalation = 25;
                    break;
            }
            OnPropertyChanged(nameof(BanditPowerFactorText));
            Log("ExecuteResetTabDefaults: reset " + _selectedCategory + " chapter");
        }

        public string BanditPowerFactorText
        {
            get
            {
                try
                {
                    var camp = TaleWorlds.CampaignSystem.Campaign.Current;
                    if (camp == null) return "—";
                    int rampYears = BanditPowerRampYears;
                    if (rampYears < 1) rampYears = 1;
                    var now = TaleWorlds.CampaignSystem.CampaignTime.Now;
                    int season = (int)now.GetSeasonOfYear;
                    int dayOfSeason = (int)now.GetDayOfSeason;
                    float elapsed = (now.GetYear - 1084)
                        + ((dayOfSeason + (season * 21)) / 84f);
                    if (elapsed < 0f) elapsed = 0f;
                    float factor = elapsed / rampYears;
                    if (factor > 1.0f) factor = 1.0f;
                    float perYear = 1.0f / rampYears;
                    return new TextObject("{=bp_hcm_006}◇ Bandit Power Factor: {FACTOR}  ·  +{PER_YEAR}/year (yr {YEARS}) ◇")
                        .SetTextVariable("FACTOR", factor.ToString("0.00"))
                        .SetTextVariable("PER_YEAR", perYear.ToString("0.00"))
                        .SetTextVariable("YEARS", rampYears).ToString();
                }
                catch (Exception ex)
                {
                    Log("BanditPowerFactorText fail: " + ex.GetType().Name + ": " + ex.Message);
                    return "—";
                }
            }
        }

        // Live Status / Diagnostics
        public int ChiefCount { get; private set; }
        public string ChiefCountText => ChiefCount.ToString();
        public int PartyCount { get; private set; }
        public string PartyCountText => PartyCount.ToString();
        public int HideoutCount { get; private set; }
        public string HideoutCountText => HideoutCount.ToString();
        public string CampaignInfo { get; private set; } = BandItPlus.Localization.Get("bp_consolevmv3_029", "year 0 / 6");

        // ───── Wave 4.17.1: Raid Operations board (Diagnostics chapter) ─────
        private readonly string[] _raidOpTexts = new string[5];
        private readonly bool[] _raidOpVisible = new bool[5];
        public string RaidOp1Text => _raidOpTexts[0] ?? "";
        public string RaidOp2Text => _raidOpTexts[1] ?? "";
        public string RaidOp3Text => _raidOpTexts[2] ?? "";
        public string RaidOp4Text => _raidOpTexts[3] ?? "";
        public string RaidOp5Text => _raidOpTexts[4] ?? "";
        public bool RaidOp1Visible => _raidOpVisible[0];
        public bool RaidOp2Visible => _raidOpVisible[1];
        public bool RaidOp3Visible => _raidOpVisible[2];
        public bool RaidOp4Visible => _raidOpVisible[3];
        public bool RaidOp5Visible => _raidOpVisible[4];
        private string _raidLootedText = "0";
        public string RaidLootedText => _raidLootedText;
        private string _raidEscalationText = "1.00x";
        public string RaidEscalationText => _raidEscalationText;
        private string _raidReadinessText = "";
        public string RaidReadinessText => _raidReadinessText;

        private void PollRaidBoard()
        {
            try
            {
                var beh = BandItPlus.BanditRaid.BanditRaidBehavior.Instance;
                var diag = beh != null ? beh.GetRaidDiagnostics() : null;
                var ops = diag != null ? diag.Operations : null;

                for (int i = 0; i < 5; i++)
                {
                    string text = ops != null && i < ops.Count ? ops[i] : null;
                    if (i == 0 && string.IsNullOrEmpty(text))
                        text = BandItPlus.Localization.Get("bp_consolevmv3_030", "No war bands in the field.");
                    _raidOpTexts[i] = text ?? "";
                    _raidOpVisible[i] = !string.IsNullOrEmpty(text);
                }
                _raidLootedText = diag != null ? diag.LootedTotal : "0";
                _raidEscalationText = diag != null ? diag.Escalation : "1.00x";
                _raidReadinessText = diag != null ? diag.Readiness : "";

                OnPropertyChangedWithValue(RaidOp1Text, nameof(RaidOp1Text));
                OnPropertyChangedWithValue(RaidOp2Text, nameof(RaidOp2Text));
                OnPropertyChangedWithValue(RaidOp3Text, nameof(RaidOp3Text));
                OnPropertyChangedWithValue(RaidOp4Text, nameof(RaidOp4Text));
                OnPropertyChangedWithValue(RaidOp5Text, nameof(RaidOp5Text));
                OnPropertyChangedWithValue(RaidOp1Visible, nameof(RaidOp1Visible));
                OnPropertyChangedWithValue(RaidOp2Visible, nameof(RaidOp2Visible));
                OnPropertyChangedWithValue(RaidOp3Visible, nameof(RaidOp3Visible));
                OnPropertyChangedWithValue(RaidOp4Visible, nameof(RaidOp4Visible));
                OnPropertyChangedWithValue(RaidOp5Visible, nameof(RaidOp5Visible));
                OnPropertyChangedWithValue(RaidLootedText, nameof(RaidLootedText));
                OnPropertyChangedWithValue(RaidEscalationText, nameof(RaidEscalationText));
                OnPropertyChangedWithValue(RaidReadinessText, nameof(RaidReadinessText));
            }
            catch (Exception ex)
            {
                Log("PollRaidBoard: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public void PollDiagnostics()
        {
            try
            {
                int chiefs = 0;
                try
                {
                    var reg = BandItPlus.Heroes.BanditChiefRegistry.Instance;
                    if (reg != null)
                        foreach (var _ in reg.GetAllChiefHeroes()) chiefs++;
                }
                catch (Exception ex) { Log("PollDiagnostics chiefs: " + ex.GetType().Name + ": " + ex.Message); }
                int prevChiefs = ChiefCount;
                ChiefCount = chiefs;
                if (prevChiefs != chiefs)
                {
                    OnPropertyChangedWithValue(chiefs, nameof(ChiefCount));
                    OnPropertyChangedWithValue(ChiefCountText, nameof(ChiefCountText));
                }

                int parties = 0;
                try
                {
                    foreach (var _ in TaleWorlds.CampaignSystem.Party.MobileParty.AllBanditParties) parties++;
                }
                catch (Exception ex) { Log("PollDiagnostics parties: " + ex.GetType().Name + ": " + ex.Message); }
                int prevParties = PartyCount;
                PartyCount = parties;
                if (prevParties != parties)
                {
                    OnPropertyChangedWithValue(parties, nameof(PartyCount));
                    OnPropertyChangedWithValue(PartyCountText, nameof(PartyCountText));
                }

                int hideouts = 0;
                try
                {
                    foreach (var s in TaleWorlds.CampaignSystem.Settlements.Settlement.All)
                        if (s != null && s.IsHideout) hideouts++;
                }
                catch (Exception ex) { Log("PollDiagnostics hideouts: " + ex.GetType().Name + ": " + ex.Message); }
                int prevHideouts = HideoutCount;
                HideoutCount = hideouts;
                if (prevHideouts != hideouts)
                {
                    OnPropertyChangedWithValue(hideouts, nameof(HideoutCount));
                    OnPropertyChangedWithValue(HideoutCountText, nameof(HideoutCountText));
                }

                // Wave 4.17.1: Raid Operations board refresh.
                PollRaidBoard();

                string newInfo = BandItPlus.Localization.Get("bp_consolevmv3_029", "year 0 / 6");
                try
                {
                    var camp = TaleWorlds.CampaignSystem.Campaign.Current;
                    if (camp != null)
                    {
                        var now = TaleWorlds.CampaignSystem.CampaignTime.Now;
                        int year = now.GetYear;
                        int season = (int)now.GetSeasonOfYear;
                        int dayOfSeason = (int)now.GetDayOfSeason;
                        float fractionalYear = (season * 21f + dayOfSeason) / 84f;
                        float elapsed = (year - 1084) + fractionalYear;
                        if (elapsed < 0f) elapsed = 0f;
                        int rampYears = BanditPowerRampYears;
                        float pct = rampYears > 0 ? (elapsed / (float)rampYears) * 100f : 100f;
                        if (pct > 100f) pct = 100f;
                        newInfo = new TextObject("{=bp_hcm_007}year {ELAPSED} / {YEARS}  ({PCT}%)")
                            .SetTextVariable("ELAPSED", elapsed.ToString("0.0"))
                            .SetTextVariable("YEARS", rampYears)
                            .SetTextVariable("PCT", pct.ToString("0")).ToString();
                    }
                }
                catch (Exception ex) { Log("PollDiagnostics campaign: " + ex.GetType().Name + ": " + ex.Message); }
                if (CampaignInfo != newInfo)
                {
                    CampaignInfo = newInfo;
                    OnPropertyChangedWithValue(newInfo, nameof(CampaignInfo));
                }

                OnPropertyChanged(nameof(BanditPowerFactorText));
                Log("Diagnostics polled: chiefs=" + ChiefCount
                    + " parties=" + PartyCount + " hideouts=" + HideoutCount
                    + " info=" + CampaignInfo);
            }
            catch (Exception ex) { Log("PollDiagnostics outer: " + ex.GetType().Name + ": " + ex.Message); }
        }

        public void ExecuteRefreshDiagnostics() => PollDiagnostics();

        private static void Log(string msg)
        {
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("V3 VMv3: " + msg);
        }
    }
}
