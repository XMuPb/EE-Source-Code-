using System;
using TaleWorlds.Library;

namespace EditableEncyclopedia
{
    /// <summary>
    /// ViewModel for the multi-line lore editor popup.
    /// Drives a large text editing area for Hero Description and Backstory fields.
    /// </summary>
    public class EncyclopediaEditVM : ViewModel
    {
        private string _titleText;
        private string _descriptionText;
        private string _charCountText;
        private bool _isVisible;
        private readonly Action<string> _onConfirm;
        private readonly Action _onCancel;
        private readonly int _maxLength;

        public EncyclopediaEditVM(string title, string currentText, int maxLength,
            Action<string> onConfirm, Action onCancel)
        {
            _titleText = title ?? "Edit";
            _descriptionText = currentText ?? string.Empty;
            _maxLength = maxLength;
            _onConfirm = onConfirm;
            _onCancel = onCancel;
            _isVisible = true;
            UpdateCharCount();
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
        public string DescriptionText
        {
            get => _descriptionText;
            set
            {
                if (_descriptionText != value)
                {
                    _descriptionText = value;
                    OnPropertyChangedWithValue(value, nameof(DescriptionText));
                    UpdateCharCount();
                }
            }
        }

        [DataSourceProperty]
        public string CharCountText
        {
            get => _charCountText;
            set
            {
                if (_charCountText != value)
                {
                    _charCountText = value;
                    OnPropertyChangedWithValue(value, nameof(CharCountText));
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
        public string SaveButtonText => Localization.L("edit_done");

        [DataSourceProperty]
        public string CancelButtonText => Localization.L("edit_cancel");

        public void ExecuteConfirm()
        {
            IsVisible = false;
            _onConfirm?.Invoke(DescriptionText);
        }

        public void ExecuteCancel()
        {
            IsVisible = false;
            _onCancel?.Invoke();
        }

        private void UpdateCharCount()
        {
            int len = _descriptionText?.Length ?? 0;
            CharCountText = _maxLength > 0
                ? len + " / " + _maxLength
                : len.ToString();
        }
    }
}
