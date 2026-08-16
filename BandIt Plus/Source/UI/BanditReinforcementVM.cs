using System;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace BandItPlus.UI
{
    // Wave 4.30.0 — premium overlay VM for the timed reinforcement arrival.
    // Owns the countdown clock; the MissionView polls PollFinished() to release the wave,
    // then calls BeginArrival() to play the arrival animation. dt-driven (mission time).
    public class BanditReinforcementVM : ViewModel
    {
        private const int TroughPx = 300;
        private readonly int _warbands, _troops;
        private float _total, _remaining, _pulseT, _arriveT, _panelAlpha;
        private bool _isCounting = true, _isArriving, _consumedFinish;

        public BanditReinforcementVM(int seconds, int warbands, int troops)
        {
            _total = _remaining = seconds < 1 ? 1 : seconds;
            _warbands = warbands; _troops = troops;
        }

        // True exactly once, when the countdown reaches 0 (MissionView fires the engine release).
        public bool PollFinished()
        {
            if (_remaining <= 0f && _isCounting && !_consumedFinish) { _consumedFinish = true; return true; }
            return false;
        }

        public void BeginArrival()
        {
            _isCounting = false; _isArriving = true; _arriveT = 0f;
            OnPropertyChanged(nameof(IsCounting));
            OnPropertyChanged(nameof(IsArriving));
            OnPropertyChanged(nameof(ArrivalText));
        }

        public void Tick(float dt)
        {
            _pulseT += dt;
            if (_panelAlpha < 1f && !_isArriving) _panelAlpha = Math.Min(1f, _panelAlpha + dt / 0.4f);

            if (_isCounting)
            {
                _remaining = Math.Max(0f, _remaining - dt);
                OnPropertyChanged(nameof(CountdownText));
                OnPropertyChanged(nameof(BarFillWidth));
            }
            if (_isArriving)
            {
                _arriveT += dt;
                if (_arriveT > 4f) _panelAlpha = Math.Max(0f, _panelAlpha - dt / 1.0f); // fade after 4s hold
                OnPropertyChanged(nameof(FlashHex));
                OnPropertyChanged(nameof(HeadlineAlpha));
            }
            OnPropertyChanged(nameof(GlowHex));
            OnPropertyChanged(nameof(PanelAlpha));
        }

        // True once the panel has fully faded after arrival — MissionView unmounts.
        public bool IsFinishedFading => _isArriving && _arriveT > 4f && _panelAlpha <= 0.01f;

        [DataSourceProperty] public bool IsCounting => _isCounting;
        [DataSourceProperty] public bool IsArriving => _isArriving;
        [DataSourceProperty] public float PanelAlpha => _panelAlpha;
        // horizontal shift of the whole panel in reference units. 0 = top-center
        // (field battles); the hideout alarm pushes it to the top-right corner so
        // it never overlaps the detection meter. set before LoadMovie.
        [DataSourceProperty] public float PanelShiftX { get; set; }
        [DataSourceProperty] public float PanelShiftY { get; set; }
        [DataSourceProperty] public string CountdownText => FormatTime(_remaining);
        [DataSourceProperty] public int BarFillWidth =>
            _total <= 0f ? 0 : (int)((_remaining / _total) * TroughPx);
        [DataSourceProperty] public string ArrivalText =>
            (_warbands == 1
                ? new TextObject("{=bp_hcm_004}{COUNT} warband · +{TROOPS} join the fray")
                : new TextObject("{=bp_hcm_005}{COUNT} warbands · +{TROOPS} join the fray"))
                .SetTextVariable("COUNT", _warbands).SetTextVariable("TROOPS", _troops).ToString();

        // Breathing amber firelight (alpha 0x6E..0xC8).
        [DataSourceProperty] public string GlowHex
        {
            get { double s = Math.Sin(_pulseT * 1.8) * 0.5 + 0.5; int a = 0x6E + (int)(0x5A * s); return "#C8881E" + a.ToString("X2"); }
        }
        // Arrival flash: bright gold, alpha ramps 0xFF -> 0x00 over the first 0.45s.
        [DataSourceProperty] public string FlashHex
        {
            get { float k = _arriveT <= 0f ? 1f : Math.Max(0f, 1f - _arriveT / 0.45f); int a = (int)(k * 255f); return "#FFE7A8" + a.ToString("X2"); }
        }
        // Arrival headline slide/fade-in over 0.5s.
        [DataSourceProperty] public float HeadlineAlpha => _arriveT >= 0.5f ? 1f : (_arriveT < 0f ? 0f : _arriveT / 0.5f);

        private static string FormatTime(float secs)
        {
            int s = (int)Math.Ceiling(secs); int m = s / 60; s = s % 60;
            return m + ":" + s.ToString("00");
        }
    }
}
