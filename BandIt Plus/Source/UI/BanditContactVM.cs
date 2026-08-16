using System;
using TaleWorlds.Library;

namespace BandItPlus.UI
{
    // Wave 4.23.0 (2026-06-12): view-model for the custom CONTACT panel —
    // the mod's own dialog UI (replaces vanilla ShowInquiry for character
    // meetings like the go-between vouch). Generic: title, body, two buttons.
    //
    // 2026-07-02 (parley-parity rebuild): CINEMATIC ENTRANCE — header fades,
    // the body settles, then the two parchment strips SLIDE IN from the left
    // (the parley board's signature). Per-choice sounds. The breathing
    // CrestHaloHex / twinkling JewelHex pulses now light the new leather board.
    public class BanditContactVM : ViewModel
    {
        private const float FADE_IN = 0.30f;
        private float _t;

        public string TitleText { get; }
        public string BodyText { get; }
        public string AcceptText { get; }
        public string LeaveText { get; }

        private readonly Action _onAccept;
        private readonly Action _onLeave;

        public BanditContactVM(string title, string body, string acceptLabel, string leaveLabel,
            Action onAccept, Action onLeave)
        {
            TitleText = title ?? "";
            BodyText = body ?? "";
            AcceptText = acceptLabel ?? BandItPlus.Localization.Get("bp_contactvm_001", "Agreed");
            LeaveText = leaveLabel ?? BandItPlus.Localization.Get("bp_contactvm_002", "Step away");
            _onAccept = onAccept;
            _onLeave = onLeave;
        }

        // Black curtain over the panel, fading clear on open.
        public string CurtainHex
        {
            get
            {
                float a = _t >= FADE_IN ? 0f : 1f - (_t / FADE_IN);
                int b = (int)(a * 255f);
                return "#000000" + b.ToString("X2");
            }
        }

        // Wave 4.28.x: continuous clock for the living firelight pulses (never stops).
        private float _pulseT;

        // ---- entrance cascade (floats — Gauntlet numeric props must be float) ----
        // INIT 1f so the first Tick's 0 registers as a change; curtain masks the frame.
        private float _headerAlpha = 1f, _bodyAlpha = 1f, _strip1Alpha = 1f, _strip2Alpha = 1f;
        private float _strip1DriftX, _strip2DriftX;
        public float HeaderAlpha { get { return _headerAlpha; } set { if (value != _headerAlpha) { _headerAlpha = value; OnPropertyChangedWithValue(value, "HeaderAlpha"); } } }
        public float BodyAlpha { get { return _bodyAlpha; } set { if (value != _bodyAlpha) { _bodyAlpha = value; OnPropertyChangedWithValue(value, "BodyAlpha"); } } }
        public float Strip1Alpha { get { return _strip1Alpha; } set { if (value != _strip1Alpha) { _strip1Alpha = value; OnPropertyChangedWithValue(value, "Strip1Alpha"); } } }
        public float Strip2Alpha { get { return _strip2Alpha; } set { if (value != _strip2Alpha) { _strip2Alpha = value; OnPropertyChangedWithValue(value, "Strip2Alpha"); } } }
        public float Strip1DriftX { get { return _strip1DriftX; } set { if (value != _strip1DriftX) { _strip1DriftX = value; OnPropertyChangedWithValue(value, "Strip1DriftX"); } } }
        public float Strip2DriftX { get { return _strip2DriftX; } set { if (value != _strip2DriftX) { _strip2DriftX = value; OnPropertyChangedWithValue(value, "Strip2DriftX"); } } }

        public void Tick(float dt)
        {
            _pulseT += dt;
            _t += dt;
            if (_t < FADE_IN + 0.05f)
                OnPropertyChanged(nameof(CurtainHex));

            // entrance: header -> body -> accept strip -> leave strip
            HeaderAlpha = Ease(_t, 0.10f, 0.30f);
            BodyAlpha = Ease(_t, 0.24f, 0.36f);
            float e1 = Ease(_t, 0.44f, 0.30f); Strip1Alpha = e1; Strip1DriftX = -46f * (1f - e1);
            float e2 = Ease(_t, 0.58f, 0.30f); Strip2Alpha = e2; Strip2DriftX = -46f * (1f - e2);

            OnPropertyChanged(nameof(CrestHaloHex));
            OnPropertyChanged(nameof(JewelHex));
        }

        private static float Ease(float t, float start, float dur)
        {
            float p = (t - start) / dur;
            if (p <= 0f) return 0f;
            if (p >= 1f) return 1f;
            return 1f - (1f - p) * (1f - p);
        }

        // Breathing firelight (the board's lamplight pool).
        public string CrestHaloHex
        {
            get
            {
                double s = Math.Sin(_pulseT * 1.8) * 0.5 + 0.5;
                int a = 0x28 + (int)(0x2C * s);
                return "#C8881E" + a.ToString("X2");
            }
        }

        // Twinkling jewel glow (the header gem).
        public string JewelHex
        {
            get
            {
                double s = Math.Sin(_pulseT * 2.6 + 1.0) * 0.5 + 0.5;
                int a = 0x50 + (int)(0x70 * s);
                return "#FFD27A" + a.ToString("X2");
            }
        }

        private static void PlaySound(string id)
        {
            try { TaleWorlds.Engine.SoundEvent.PlaySound2D(id); } catch { }
        }

        public void ExecuteAccept()
        {
            PlaySound("event:/ui/notification/kingdom_decision"); // a pact struck
            BanditContactScreen.CloseFromVM();
            try { _onAccept?.Invoke(); } catch { }
        }

        public void ExecuteLeave()
        {
            PlaySound("event:/ui/mission/horns/retreat");
            BanditContactScreen.CloseFromVM();
            try { _onLeave?.Invoke(); } catch { }
        }
    }
}
