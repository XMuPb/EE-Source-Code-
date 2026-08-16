using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace BandItPlus.UI
{
    // Wave 4.34 (2026-06-28): view-model for the custom JAILBREAK briefing menu (BpJailbreakBrief.xml).
    // Drives a cascading entrance — curtain reveal, then art -> header -> flavor -> intel rows -> reward
    // -> buttons each ease in ~0.12s apart — plus a living torch flicker over the art. Only the captor line
    // and guard count are dynamic; the briefing prose is baked into the prefab. Begin/Wait routed by the screen.
    public class BpJailbreakBriefVM : ViewModel
    {
        private const float FADE_IN = 0.25f;
        private float _t;
        private float _pulseT;

        public string SublineText { get; }
        public string GuardsText { get; }

        // staggered entrance alphas (bound to AlphaFactor on each element group).
        // INIT to 1f (not 0): AlphaFactor renders at the default 1.0 until the binding's first CHANGE event,
        // and the `value != _field` guard would skip firing if the field already equalled the first computed
        // value (0). Starting at 1f guarantees the first tick (which sets 0) is a real change -> pushes
        // AlphaFactor to 0 (hidden), then the ramp fades it in. The 1-frame 1.0 flash is masked by the curtain.
        private float _artAlpha = 1f, _headerAlpha = 1f, _flavorAlpha = 1f, _rowsAlpha = 1f, _rewardAlpha = 1f, _buttonsAlpha = 1f;
        [DataSourceProperty] public float ArtAlpha { get { return _artAlpha; } set { if (value != _artAlpha) { _artAlpha = value; OnPropertyChangedWithValue(value, "ArtAlpha"); } } }
        [DataSourceProperty] public float HeaderAlpha { get { return _headerAlpha; } set { if (value != _headerAlpha) { _headerAlpha = value; OnPropertyChangedWithValue(value, "HeaderAlpha"); } } }
        [DataSourceProperty] public float FlavorAlpha { get { return _flavorAlpha; } set { if (value != _flavorAlpha) { _flavorAlpha = value; OnPropertyChangedWithValue(value, "FlavorAlpha"); } } }
        [DataSourceProperty] public float RowsAlpha { get { return _rowsAlpha; } set { if (value != _rowsAlpha) { _rowsAlpha = value; OnPropertyChangedWithValue(value, "RowsAlpha"); } } }
        [DataSourceProperty] public float RewardAlpha { get { return _rewardAlpha; } set { if (value != _rewardAlpha) { _rewardAlpha = value; OnPropertyChangedWithValue(value, "RewardAlpha"); } } }
        [DataSourceProperty] public float ButtonsAlpha { get { return _buttonsAlpha; } set { if (value != _buttonsAlpha) { _buttonsAlpha = value; OnPropertyChangedWithValue(value, "ButtonsAlpha"); } } }

        public BpJailbreakBriefVM(string captorName, string difficultyLabel, int guardCount)
        {
            string captor = string.IsNullOrEmpty(captorName) ? BandItPlus.Localization.Get("bp_jailbreakbriefvm_001", "bandits") : captorName;
            string diff = string.IsNullOrEmpty(difficultyLabel) ? "" : "   ·   " + difficultyLabel;
            SublineText = new TextObject("{=bp_hcj_001}Held by the {CAPTOR}").SetTextVariable("CAPTOR", captor).ToString() + diff;
            GuardsText = new TextObject("{=bp_hcj_002}Guards on watch — {COUNT}.  They pace the cell block in slow rounds. Raise the alarm and every last blade comes running.")
                .SetTextVariable("COUNT", guardCount).ToString();
        }

        // open-fade curtain (black -> clear) — reveals the empty frame and masks the cascade's first frame
        public string CurtainHex
        {
            get
            {
                float a = _t >= FADE_IN ? 0f : 1f - (_t / FADE_IN);
                int b = (int)(a * 255f);
                return "#000000" + b.ToString("X2");
            }
        }

        // living torch flicker over the art (warm amber; two sines for an irregular flame; alpha 0x20..0x46)
        public string TorchHex
        {
            get
            {
                double s = System.Math.Sin(_pulseT * 5.0) * 0.5 + 0.5;
                double s2 = System.Math.Sin(_pulseT * 11.0 + 1.3) * 0.5 + 0.5;
                int a = 0x20 + (int)(0x26 * (0.7 * s + 0.3 * s2));
                return "#FF9A30" + a.ToString("X2");
            }
        }

        public void Tick(float dt)
        {
            _t += dt;
            _pulseT += dt;
            ArtAlpha = Ease(_t, 0.10f, 0.40f);     // image
            HeaderAlpha = Ease(_t, 0.22f, 0.36f);  // title + subline + divider
            FlavorAlpha = Ease(_t, 0.34f, 0.36f);  // flavor prose + section label
            RowsAlpha = Ease(_t, 0.46f, 0.40f);    // the five intel rows
            RewardAlpha = Ease(_t, 0.60f, 0.36f);  // reward plaque + stakes
            ButtonsAlpha = Ease(_t, 0.72f, 0.36f); // Begin / Wait (last)
            if (_t < FADE_IN + 0.05f) OnPropertyChanged(nameof(CurtainHex));
            OnPropertyChanged(nameof(TorchHex));
        }

        // ease-out quad: fast in, gentle settle
        private static float Ease(float t, float start, float dur)
        {
            float p = (t - start) / dur;
            if (p <= 0f) return 0f;
            if (p >= 1f) return 1f;
            return 1f - (1f - p) * (1f - p);
        }

        public void ExecuteBegin() => BpJailbreakBriefScreen.Choose("begin");
        public void ExecuteWait() => BpJailbreakBriefScreen.Choose("wait");
    }
}
