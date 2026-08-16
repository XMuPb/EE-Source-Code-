using System;
using TaleWorlds.Library;
using BandItPlus.HideoutVisit;

namespace BandItPlus.UI
{
    // v123 (2026-05-15): ViewModel for the first-visit cinematic intro card.
    // Shown ONCE per hideout (first visit) with the culture's authored narration.
    // Dismissed by the Continue button -> ExecuteContinue -> _onContinue callback
    // (the MissionView removes its layer).
    public class HideoutCinemaIntroVM : ViewModel
    {
        private string _narrationText = "";
        private string _titleText = "";
        private bool _isVisible = true;
        private float _cardAlpha;
        private float _buttonPulse = 1f;
        private float _headerAlpha;
        private float _ruleAlpha;
        private float _textAlpha;
        private float _goldShimmer = 1f;
        private readonly Action _onContinue;

        public HideoutCinemaIntroVM(string narrationText, string titleText, Action onContinue)
        {
            // Normalize CRLF -> LF: the source string is a C# verbatim literal,
            // so it carries the file's \r\n. Gauntlet RichTextWidget renders \n.
            _narrationText = (narrationText ?? "").Replace("\r", "");
            _titleText = (titleText ?? "").Trim();
            _onContinue = onContinue;
        }

        [DataSourceProperty] public string NarrationText
        {
            get => _narrationText;
            set { if (value == _narrationText) return; _narrationText = value; OnPropertyChangedWithValue(value, nameof(NarrationText)); }
        }

        // v133: card header — the hideout's name, rendered in a gold display
        // brush above the narration. Gives the card font + color variety
        // (display title vs body) and structure.
        [DataSourceProperty] public string TitleText
        {
            get => _titleText;
            set { if (value == _titleText) return; _titleText = value; OnPropertyChangedWithValue(value, nameof(TitleText)); }
        }

        [DataSourceProperty] public bool IsVisible
        {
            get => _isVisible;
            set { if (value == _isVisible) return; _isVisible = value; OnPropertyChangedWithValue(value, nameof(IsVisible)); }
        }

        // v135: fade-in animation. The root widget's AlphaFactor is bound to this;
        // the MissionView ramps it 0 -> 1 over the first ~0.35s after the card
        // attaches, so the card fades up rather than popping in.
        [DataSourceProperty] public float CardAlpha
        {
            get => _cardAlpha;
            set { if (value == _cardAlpha) return; _cardAlpha = value; OnPropertyChangedWithValue(value, nameof(CardAlpha)); }
        }

        // v137: Continue-button "click me" pulse. The button's AlphaFactor is
        // bound to this; the MissionView breathes it ~0.72..1.0 on a sine.
        [DataSourceProperty] public float ButtonPulse
        {
            get => _buttonPulse;
            set { if (value == _buttonPulse) return; _buttonPulse = value; OnPropertyChangedWithValue(value, nameof(ButtonPulse)); }
        }

        // v143: staggered reveal — these AlphaFactors ramp 0->1 in sequence
        // (header, then dividers, then text) so the card builds in cinematically
        // rather than appearing all at once.
        [DataSourceProperty] public float HeaderAlpha
        {
            get => _headerAlpha;
            set { if (value == _headerAlpha) return; _headerAlpha = value; OnPropertyChangedWithValue(value, nameof(HeaderAlpha)); }
        }

        [DataSourceProperty] public float RuleAlpha
        {
            get => _ruleAlpha;
            set { if (value == _ruleAlpha) return; _ruleAlpha = value; OnPropertyChangedWithValue(value, nameof(RuleAlpha)); }
        }

        [DataSourceProperty] public float TextAlpha
        {
            get => _textAlpha;
            set { if (value == _textAlpha) return; _textAlpha = value; OnPropertyChangedWithValue(value, nameof(TextAlpha)); }
        }

        // v143: continuous gold shimmer — ColorFactor for the gold dividers and
        // the header title; the MissionView breathes it on a slow sine so the
        // gold elements gently glow the whole time the card is shown.
        [DataSourceProperty] public float GoldShimmer
        {
            get => _goldShimmer;
            set { if (value == _goldShimmer) return; _goldShimmer = value; OnPropertyChangedWithValue(value, nameof(GoldShimmer)); }
        }

        public void ExecuteContinue()
        {
            try
            {
                IsVisible = false;
                _onContinue?.Invoke();
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v123 ExecuteContinue fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
