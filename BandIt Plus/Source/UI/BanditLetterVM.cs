using System;
using System.Collections.Generic;
using System.Text;
using TaleWorlds.Library;

namespace BandItPlus.UI
{
    // Wave 4.25.11 (2026-06-17): the parchment LETTER popup. On open the parchment
    // RISES into view (LetterSlideY) as the curtain clears, then the body "writes"
    // itself out one character at a time (typewriter), preserving the NameInk/
    // PlaceInk rich-text spans. Click once to finish the writing instantly, again
    // to dismiss. AlphaFactor is NOT bindable — fade is Color-hex, motion is offsets.
    public class BanditLetterVM : ViewModel
    {
        private const float FADE_IN = 0.5f;       // curtain clear + parchment rise
        private const float WRITE_DELAY = 0.55f;  // let the parchment settle first
        private const float CHARS_PER_SEC = 75f;  // the writing hand's pace
        private const float RISE_PIXELS = 44f;    // how far the parchment rises

        private float _t;
        private float _writeT;

        public string TitleText { get; }
        public string HintText => BandItPlus.Localization.Get("bp_lettervm_001", "Press to Continue");

        // Each visible character carries the rich-text style active at its position,
        // so the typewriter can reveal N chars without ever cutting a <span> tag.
        private struct Glyph { public char Ch; public string Style; }
        private readonly List<Glyph> _glyphs;
        private int _revealed;
        private string _visibleBody = "";
        private float _blinkT;
        private bool _blinkOn = true;
        private bool _typingSoundOn;

        private readonly Action _onClose;

        public BanditLetterVM(string title, string body, Action onClose)
        {
            TitleText = title ?? "";
            _onClose = onClose;
            _glyphs = Parse(body ?? "");
        }

        // The body bound to the RichTextWidget — grows as the letter "writes".
        public string VisibleBody => _visibleBody;

        // The flashing "Press to Continue" prompt — visible only AFTER the writing
        // finishes, blinked on/off by the timer in Tick.
        public bool ContinueVisible => _glyphs.Count > 0 && _revealed >= _glyphs.Count && _blinkOn;

        // Black overlay fading clear as the letter settles into view.
        public string CurtainHex
        {
            get
            {
                float a = _t >= FADE_IN ? 0f : 1f - (_t / FADE_IN);
                int b = (int)(a * 255f);
                return "#000000" + b.ToString("X2");
            }
        }

        // The parchment rises from below into place (ease-out quad), then breathes
        // with a gentle held-letter sway so the sprite never sits dead-still.
        public float LetterSlideY
        {
            get
            {
                float p = _t >= FADE_IN ? 1f : _t / FADE_IN;
                float ease = 1f - (1f - p) * (1f - p);
                float rise = (1f - ease) * RISE_PIXELS;
                float sway = 4f * (float)System.Math.Sin(_t * 0.7);
                return rise + sway;
            }
        }

        // Gentle horizontal drift — the letter sways as if lightly held by firelight.
        public float LetterSwayX => 6f * (float)System.Math.Sin(_t * 0.5 + 0.6f);

        public void Tick(float dt)
        {
            _t += dt;
            OnPropertyChanged(nameof(CurtainHex));
            OnPropertyChanged(nameof(LetterSlideY));
            OnPropertyChanged(nameof(LetterSwayX));

            if (_t >= WRITE_DELAY && _revealed < _glyphs.Count)
            {
                if (!_typingSoundOn) { CustomSoundPlayer.PlayLoop("bp_typing.wav"); _typingSoundOn = true; }
                _writeT += dt;
                int target = (int)(_writeT * CHARS_PER_SEC);
                if (target > _glyphs.Count) target = _glyphs.Count;
                if (target != _revealed)
                {
                    _revealed = target;
                    _visibleBody = Build(_revealed);
                    OnPropertyChanged(nameof(VisibleBody));
                    if (_revealed >= _glyphs.Count) { OnPropertyChanged(nameof(ContinueVisible)); StopTypingSound(); }
                }
            }

            // Once the writing is done, flash the "Press to Continue" prompt.
            if (_glyphs.Count > 0 && _revealed >= _glyphs.Count)
            {
                _blinkT += dt;
                if (_blinkT >= (_blinkOn ? 0.7f : 0.5f))
                {
                    _blinkT = 0f;
                    _blinkOn = !_blinkOn;
                    OnPropertyChanged(nameof(ContinueVisible));
                }
            }
        }

        public void ExecuteClose()
        {
            // First click finishes the writing instantly; the next dismisses.
            if (_revealed < _glyphs.Count)
            {
                _revealed = _glyphs.Count;
                _visibleBody = Build(_revealed);
                OnPropertyChanged(nameof(VisibleBody));
                OnPropertyChanged(nameof(ContinueVisible));
                StopTypingSound();
                return;
            }
            BanditLetterScreen.CloseFromVM();
            try { _onClose?.Invoke(); } catch { }
        }

        private void StopTypingSound()
        {
            if (!_typingSoundOn) return;
            try { CustomSoundPlayer.Stop(); } catch { }
            _typingSoundOn = false;
        }

        // Split rich text into glyphs carrying their active span style, so the
        // typewriter can reveal characters without ever slicing a <span> tag.
        private static List<Glyph> Parse(string body)
        {
            var list = new List<Glyph>(body.Length);
            string style = "";
            int i = 0;
            while (i < body.Length)
            {
                if (body[i] == '<')
                {
                    if (i + 13 <= body.Length && body.Substring(i, 13) == "<span style=\"")
                    {
                        int q = body.IndexOf('"', i + 13);
                        if (q > 0)
                        {
                            style = body.Substring(i + 13, q - (i + 13));
                            int close = body.IndexOf('>', q);
                            i = (close > 0) ? close + 1 : body.Length;
                            continue;
                        }
                    }
                    else if (i + 7 <= body.Length && body.Substring(i, 7) == "</span>")
                    {
                        style = "";
                        i += 7;
                        continue;
                    }
                }
                list.Add(new Glyph { Ch = body[i], Style = style });
                i++;
            }
            return list;
        }

        // Re-emit the first n glyphs as rich text, opening/closing spans cleanly
        // so the colour holds even mid-write and the markup is always well-formed.
        private string Build(int n)
        {
            var sb = new StringBuilder();
            string open = "";
            for (int i = 0; i < n; i++)
            {
                var g = _glyphs[i];
                if (g.Style != open)
                {
                    if (open.Length > 0) sb.Append("</span>");
                    if (g.Style.Length > 0) sb.Append("<span style=\"").Append(g.Style).Append("\">");
                    open = g.Style;
                }
                sb.Append(g.Ch);
            }
            if (open.Length > 0) sb.Append("</span>");
            return sb.ToString();
        }
    }
}
