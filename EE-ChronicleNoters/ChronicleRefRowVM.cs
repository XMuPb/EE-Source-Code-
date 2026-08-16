using System;
using TaleWorlds.Library;

namespace EditableEncyclopedia.ChronicleNoters
{
    /// <summary>One clickable row under MORE FROM / ALSO IN.</summary>
    public class ChronicleRefRowVM : ViewModel
    {
        private readonly Action<string> _onSelect;
        private string _text = string.Empty;
        private string _yearLabel = string.Empty;

        public string TargetEntryId { get; private set; }

        public ChronicleRefRowVM(string targetEntryId, string text, string yearLabel, Action<string> onSelect)
        {
            TargetEntryId = targetEntryId ?? string.Empty;
            _text = text ?? string.Empty;
            _yearLabel = yearLabel ?? string.Empty;
            _onSelect = onSelect;
        }

        [DataSourceProperty]
        public string Text
        {
            get { return _text; }
            set { if (_text != value) { _text = value ?? string.Empty; OnPropertyChanged("Text"); } }
        }

        [DataSourceProperty]
        public string YearLabel
        {
            get { return _yearLabel; }
            set { if (_yearLabel != value) { _yearLabel = value ?? string.Empty; OnPropertyChanged("YearLabel"); } }
        }

        public void ExecuteSelect()
        {
            if (_onSelect != null && !string.IsNullOrEmpty(TargetEntryId)) _onSelect(TargetEntryId);
        }
    }
}
