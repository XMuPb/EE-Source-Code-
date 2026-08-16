using System;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace BandItPlus.HUD
{
    // Wave 4.28.0 — campaign-map HUD ViewModel. Drives the persistent top-center bar
    // that appears after the road (origin) is chosen. The frame is code-built in the
    // prefab (primitive widgets); this VM feeds the rank ribbon (rank name + tier +
    // gold + progress) and the breathing firelight glow, and exposes the sprite ids
    // for the crest + 6 status slots (slots render empty until hud_icons is Exported).
    public class BanditHudVM : ViewModel
    {
        private float _t;

        private float _barAlpha;
        private string _rankName = "";
        private string _tierText = "";
        private string _goldText = "";
        private float _progressWidth;
        private bool _questAlert;
        private string _glowHex = "#C0641E55";

        [DataSourceProperty]
        public float BarAlpha { get => _barAlpha; set { if (Math.Abs(_barAlpha - value) > 0.0001f) { _barAlpha = value; OnPropertyChanged("BarAlpha"); } } }
        [DataSourceProperty]
        public string RankName { get => _rankName; set { if (_rankName != value) { _rankName = value; OnPropertyChanged("RankName"); } } }
        [DataSourceProperty]
        public string TierText { get => _tierText; set { if (_tierText != value) { _tierText = value; OnPropertyChanged("TierText"); } } }
        [DataSourceProperty]
        public string GoldText { get => _goldText; set { if (_goldText != value) { _goldText = value; OnPropertyChanged("GoldText"); } } }
        [DataSourceProperty]
        public float ProgressWidth { get => _progressWidth; set { if (Math.Abs(_progressWidth - value) > 0.01f) { _progressWidth = value; OnPropertyChanged("ProgressWidth"); } } }
        [DataSourceProperty]
        public bool QuestAlert { get => _questAlert; set { if (_questAlert != value) { _questAlert = value; OnPropertyChanged("QuestAlert"); } } }
        [DataSourceProperty]
        public string GlowHex { get => _glowHex; set { if (_glowHex != value) { _glowHex = value; OnPropertyChanged("GlowHex"); } } }

        // crest + the 6 status-slot sprites (hud_icons category; empty until Exported)
        [DataSourceProperty] public string CrestSprite => "logo1";
        [DataSourceProperty] public string Slot0Sprite => "bp_bounty_hunter";
        [DataSourceProperty] public string Slot1Sprite => "bp_qest";
        [DataSourceProperty] public string Slot2Sprite => "bp_bandit_icon";
        [DataSourceProperty] public string Slot3Sprite => "bp_dual_swords";
        [DataSourceProperty] public string Slot4Sprite => "bp_trade";
        [DataSourceProperty] public string Slot5Sprite => "bp_campfire";

        // Wave 4.28.x: HUD sub-element visibility (Console HUD chapter toggles).
        // The breathing firelight halo binds IsVisible to this; quest alert folds
        // ShowQuestAlert into the QuestAlert flag below.
        [DataSourceProperty] public bool ShowHudGlow => MCMSettings.Instance == null || MCMSettings.Instance.ShowHudGlow;

        // ── Preview gating (Wave 4.28.x) ────────────────────────────────
        // The full HUD ships NEXT update. Until then it renders as a locked
        // preview: a dim wash over the bar + a big "UNAVAILABLE" watermark
        // running VERTICALLY down the bar (one letter per line — Gauntlet can't
        // rotate text, so newlines stack it). Gated on PreviewMode — flip it to
        // false (one line) to go live; nothing else needs to change.
        [DataSourceProperty] public bool PreviewMode => true;
        // newline-separated so the TextWidget renders it as a vertical column.
        [DataSourceProperty] public string PreviewLabel => BandItPlus.Localization.Get("bp_hudvm_001", "U\nN\nA\nV\nA\nI\nL\nA\nB\nL\nE");
        [DataSourceProperty] public string PreviewSubLabel => BandItPlus.Localization.Get("bp_hudvm_002", "Next Update");

        public void Tick(float dt)
        {
            _t += dt;
            if (_barAlpha < 1f) BarAlpha = Math.Min(1f, _barAlpha + dt / 0.4f); // fade-in
            Refresh();
            // breathing firelight glow (44..78 alpha on a warm amber)
            double s = Math.Sin(_t * 2.0) * 0.5 + 0.5;
            int a = 44 + (int)(34 * s);
            GlowHex = "#C0641E" + a.ToString("X2");
        }

        public void Refresh()
        {
            try
            {
                var rank = BanditRankBehavior.Instance;
                if (rank != null)
                {
                    RankName = rank.RankName;
                    TierText = new TextObject("{=bp_hcm_003}Tier {NUM}").SetTextVariable("NUM", Roman(rank.Tier + 1)).ToString();
                    GoldText = rank.GoldDisplay;
                    ProgressWidth = Math.Max(2f, rank.TierProgress * 74f); // infamy bar fill px (narrow bar)
                }
                var origin = BandItPlus.Origins.BanditOriginBehavior.Instance;
                // quest alert pulses when a hunt pledge is active (read via the public API).
                QuestAlert = origin != null && origin.HasActiveHuntPledge
                             && (MCMSettings.Instance == null || MCMSettings.Instance.ShowQuestAlert);
            }
            catch { }
        }

        private static string Roman(int n)
        {
            switch (n)
            {
                case 1: return "I"; case 2: return "II"; case 3: return "III";
                case 4: return "IV"; case 5: return "V"; default: return "MAX";
            }
        }
    }
}
