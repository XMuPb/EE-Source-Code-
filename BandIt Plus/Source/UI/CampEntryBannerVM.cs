using TaleWorlds.Library;

namespace BandItPlus.UI
{
    // Wave 4.12-fix v77 (2026-05-12): ViewModel for the Gauntlet camp-entry
    // banner. Exposes camp/chief/culture strings plus a Visible bool the
    // MissionView toggles to drive fade-out after the display duration.
    public class CampEntryBannerVM : ViewModel
    {
        private string _campName = "";
        private string _subtitleText = "";
        private string _bannerCode = "";
        private bool _isVisible = true;

        [DataSourceProperty]
        public string CampName
        {
            get => _campName;
            set
            {
                if (value == _campName) return;
                _campName = value;
                OnPropertyChangedWithValue(value, nameof(CampName));
            }
        }

        [DataSourceProperty]
        public string SubtitleText
        {
            get => _subtitleText;
            set
            {
                if (value == _subtitleText) return;
                _subtitleText = value;
                OnPropertyChangedWithValue(value, nameof(SubtitleText));
            }
        }

        [DataSourceProperty]
        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (value == _isVisible) return;
                _isVisible = value;
                OnPropertyChangedWithValue(value, nameof(IsVisible));
            }
        }

        // v81: banner sprite. Canonical Bannerlord 1.3.x property name is
        // BannerCodeText — used by BannerTableauWidget to render a serialized
        // banner. EditableEncyclopedia confirms this is the primary name
        // (with BannerCode / BannerKey as fallbacks).
        [DataSourceProperty]
        public string BannerCodeText
        {
            get => _bannerCode;
            set
            {
                if (value == _bannerCode) return;
                _bannerCode = value;
                OnPropertyChangedWithValue(value, nameof(BannerCodeText));
            }
        }

        public CampEntryBannerVM(string campName, string subtitleText, string bannerCode = "")
        {
            _campName = campName ?? "";
            _subtitleText = subtitleText ?? "";
            _bannerCode = bannerCode ?? "";
            _isVisible = true;
        }
    }
}
