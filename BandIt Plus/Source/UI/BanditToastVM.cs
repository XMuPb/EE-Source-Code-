using TaleWorlds.Library;

namespace BandItPlus.UI
{
    // Wave 4.21.1 (2026-06-12): staged toast animation per user direction —
    // 1) the crest (logo1) appears alone, 2) the banner strip UNFURLS
    // rightward from behind it, 3) the text inks in. Exit reverses:
    // text out → strip retracts → crest fades. Tick-driven from
    // SubModuleClassEntry.OnApplicationTick.
    public class BanditToastVM : ViewModel
    {
        private const float A_LOGO = 0.35f;   // crest fade-in
        private const float B_STRIP = 0.45f;  // strip unfurl
        private const float C_TEXT = 0.25f;   // text ink-in
        private const float HOLD = 6.0f;
        private const float X_TEXT = 0.20f;   // exit: text out
        private const float X_STRIP = 0.30f;  // exit: strip retract
        private const float X_LOGO = 0.25f;   // exit: crest fade

        private const float STRIP_FULL = 560f;

        private float _t;
        public bool Finished { get; private set; }

        public string TitleText { get; }
        public string BodyText { get; }

        public BanditToastVM(string title, string body)
        {
            TitleText = title ?? "";
            BodyText = body ?? "";
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        // Timeline anchors
        private float InEnd => A_LOGO + B_STRIP + C_TEXT;
        private float HoldEnd => InEnd + HOLD;
        private float OutTextEnd => HoldEnd + X_TEXT;
        private float OutStripEnd => OutTextEnd + X_STRIP;
        private float OutLogoEnd => OutStripEnd + X_LOGO;

        public float LogoAlpha
        {
            get
            {
                if (_t < A_LOGO) return Clamp01(_t / A_LOGO);
                if (_t < OutStripEnd) return 1f;
                return Clamp01(1f - (_t - OutStripEnd) / X_LOGO);
            }
        }

        public float StripWidth
        {
            get
            {
                if (_t < A_LOGO) return 0f;
                if (_t < A_LOGO + B_STRIP) return STRIP_FULL * Clamp01((_t - A_LOGO) / B_STRIP);
                if (_t < OutTextEnd) return STRIP_FULL;
                if (_t < OutStripEnd) return STRIP_FULL * Clamp01(1f - (_t - OutTextEnd) / X_STRIP);
                return 0f;
            }
        }

        public float TextAlpha
        {
            get
            {
                float textInStart = A_LOGO + B_STRIP - 0.08f; // slight overlap with unfurl
                if (_t < textInStart) return 0f;
                if (_t < textInStart + C_TEXT) return Clamp01((_t - textInStart) / C_TEXT);
                if (_t < HoldEnd) return 1f;
                return Clamp01(1f - (_t - HoldEnd) / X_TEXT);
            }
        }

        public void Tick(float dt)
        {
            if (Finished) return;
            _t += dt;
            OnPropertyChanged(nameof(LogoAlpha));
            OnPropertyChanged(nameof(StripWidth));
            OnPropertyChanged(nameof(TextAlpha));
            if (_t >= OutLogoEnd) Finished = true;
        }
    }
}
