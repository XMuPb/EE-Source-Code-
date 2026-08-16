using System;
using TaleWorlds.Library;

namespace EditableEncyclopedia
{
    /// <summary>
    /// ViewModel for a single tag button in the Quick Tag Menu.
    /// Clicking toggles the tag on/off for the current encyclopedia entry.
    /// </summary>
    public class TagButtonVM : ViewModel
    {
        private string _displayText;
        private readonly Action _onToggle;

        public TagButtonVM(string displayText, Action onToggle)
        {
            _displayText = displayText;
            _onToggle = onToggle;
        }

        [DataSourceProperty]
        public string DisplayText
        {
            get => _displayText;
            set
            {
                if (_displayText != value)
                {
                    _displayText = value;
                    OnPropertyChangedWithValue(value, nameof(DisplayText));
                }
            }
        }

        public void ExecuteToggle()
        {
            _onToggle?.Invoke();
        }
    }

    /// <summary>
    /// ViewModel for the Quick Tag Menu popup (Ctrl+G).
    /// Shows preset tag buttons that toggle on click, plus an "Other..." button
    /// for custom tags. Most-used tags are shown first.
    /// </summary>
    public class QuickTagMenuVM : ViewModel
    {
        private string _titleText;
        private string _currentTagsText;
        private bool _isVisible;
        private MBBindingList<TagButtonVM> _tagButtons;
        private readonly Action _onClose;

        public QuickTagMenuVM(string title, string currentTagsText,
            MBBindingList<TagButtonVM> tagButtons, Action onClose)
        {
            _titleText = title;
            _currentTagsText = currentTagsText;
            _isVisible = true;
            _tagButtons = tagButtons;
            _onClose = onClose;
        }

        [DataSourceProperty]
        public string TitleText
        {
            get => _titleText;
            set
            {
                if (_titleText != value)
                {
                    _titleText = value;
                    OnPropertyChangedWithValue(value, nameof(TitleText));
                }
            }
        }

        [DataSourceProperty]
        public string CurrentTagsText
        {
            get => _currentTagsText;
            set
            {
                if (_currentTagsText != value)
                {
                    _currentTagsText = value;
                    OnPropertyChangedWithValue(value, nameof(CurrentTagsText));
                }
            }
        }

        [DataSourceProperty]
        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible != value)
                {
                    _isVisible = value;
                    OnPropertyChangedWithValue(value, nameof(IsVisible));
                }
            }
        }

        [DataSourceProperty]
        public MBBindingList<TagButtonVM> TagButtons
        {
            get => _tagButtons;
            set
            {
                if (_tagButtons != value)
                {
                    _tagButtons = value;
                    OnPropertyChangedWithValue(value, nameof(TagButtons));
                }
            }
        }

        [DataSourceProperty]
        public string CloseText => Localization.L("edit_done");

        public void ExecuteClose()
        {
            IsVisible = false;
            _onClose?.Invoke();
        }
    }
}
