using System;
using TaleWorlds.Library;

namespace EditableEncyclopedia
{
    /// <summary>
    /// ViewModel for a single selectable item in the selection popup.
    /// </summary>
    public class SelectionItemVM : ViewModel
    {
        private string _displayText;
        private bool _isCurrent;
        private readonly Action _onSelect;

        public SelectionItemVM(string text, bool isCurrent, Action onSelect)
        {
            _displayText = text;
            _isCurrent = isCurrent;
            _onSelect = onSelect;
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

        [DataSourceProperty]
        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent != value)
                {
                    _isCurrent = value;
                    OnPropertyChangedWithValue(value, nameof(IsCurrent));
                }
            }
        }

        public void ExecuteSelect()
        {
            _onSelect?.Invoke();
        }
    }

    /// <summary>
    /// ViewModel for the custom Gauntlet selection popup.
    /// Shows a scrollable list of clickable items with a cancel button.
    /// Used when InformationManager.ShowMultiSelectionInquiry is unavailable.
    /// </summary>
    public class SelectionPopupVM : ViewModel
    {
        private string _titleText;
        private string _descriptionText;
        private bool _isVisible;
        private MBBindingList<SelectionItemVM> _items;
        private readonly Action _onCancel;

        public SelectionPopupVM(string title, string description,
            MBBindingList<SelectionItemVM> items, Action onCancel)
        {
            _titleText = title;
            _descriptionText = description ?? "";
            _isVisible = true;
            _items = items;
            _onCancel = onCancel;
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
        public MBBindingList<SelectionItemVM> Items
        {
            get => _items;
            set
            {
                if (_items != value)
                {
                    _items = value;
                    OnPropertyChangedWithValue(value, nameof(Items));
                }
            }
        }

        [DataSourceProperty]
        public string CancelText => Localization.L("edit_cancel");

        public void ExecuteCancel()
        {
            IsVisible = false;
            _onCancel?.Invoke();
        }
    }
}
