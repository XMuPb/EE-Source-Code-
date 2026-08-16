using System;
using System.Collections.Generic;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;

namespace EditableEncyclopedia.ChronicleNoters
{
    /// <summary>
    /// VM for the dual-purpose year strip across the top of the chronicle body.
    /// Each cell is a clickable year pill. Click jumps to that year; Shift+Click
    /// applies a range filter (first shift-click sets FROM, second sets TO).
    ///
    /// Owner: GlobalChroniclePanelVM. Populated via <see cref="Populate"/> after
    /// the entry list is built. Highlight state (active / in-range / range-edge)
    /// is refreshed via <see cref="RefreshHighlights"/> in response to clicks.
    /// </summary>
    public class ChronicleYearStripVM : ViewModel
    {
        private MBBindingList<YearCellVM> _years = new MBBindingList<YearCellVM>();

        [DataSourceProperty]
        public MBBindingList<YearCellVM> Years
        {
            get { return _years; }
            set { if (_years != value) { _years = value; OnPropertyChanged("Years"); } }
        }

        /// <summary>
        /// Fired when a year cell is clicked. Second arg is true if Shift is held.
        /// Wired by the panel VM to dispatch jump / range-set behavior.
        /// </summary>
        public Action<int, bool> OnYearClicked;

        public void Populate(IEnumerable<int> distinctYearsAscending, HashSet<int> yearsWithEvents)
        {
            _years.Clear();
            if (distinctYearsAscending == null) { OnPropertyChanged("Years"); return; }
            foreach (int y in distinctYearsAscending)
            {
                bool hasEvents = yearsWithEvents != null && yearsWithEvents.Contains(y);
                var cell = new YearCellVM(y, hasEvents);
                cell.ClickHandler = (yr, shift) => { var h = OnYearClicked; if (h != null) h(yr, shift); };
                _years.Add(cell);
            }
            OnPropertyChanged("Years");
        }

        /// <summary>
        /// Updates IsActive / IsRangeEdge / IsInRange across all cells.
        /// Call after the user clicks (jump) or completes a shift-click pair (range).
        /// </summary>
        public void RefreshHighlights(int? activeYear, int? rangeFromYear, int? rangeToYear)
        {
            foreach (var cell in _years)
            {
                cell.IsActive = activeYear.HasValue && cell.Year == activeYear.Value;
                cell.IsRangeEdge = (rangeFromYear.HasValue && rangeFromYear.Value == cell.Year)
                                || (rangeToYear.HasValue && rangeToYear.Value == cell.Year);
                cell.IsInRange = rangeFromYear.HasValue && rangeToYear.HasValue
                              && cell.Year > rangeFromYear.Value && cell.Year < rangeToYear.Value;
            }
        }
    }

    /// <summary>
    /// One year pill in the year strip. Knows its year, whether it has events
    /// (drives the dot marker), and its current highlight state.
    /// </summary>
    public class YearCellVM : ViewModel
    {
        public int Year { get; }
        public Action<int, bool> ClickHandler;

        public YearCellVM(int year, bool hasEvents)
        {
            Year = year;
            _yearLabel = year.ToString();
            _hasEvents = hasEvents;
        }

        private string _yearLabel;
        [DataSourceProperty]
        public string YearLabel
        {
            get { return _yearLabel; }
            set { if (_yearLabel != value) { _yearLabel = value; OnPropertyChanged("YearLabel"); } }
        }

        private bool _hasEvents;
        [DataSourceProperty]
        public bool HasEvents
        {
            get { return _hasEvents; }
            set { if (_hasEvents != value) { _hasEvents = value; OnPropertyChanged("HasEvents"); } }
        }

        private bool _isActive;
        [DataSourceProperty]
        public bool IsActive
        {
            get { return _isActive; }
            set { if (_isActive != value) { _isActive = value; OnPropertyChanged("IsActive"); } }
        }

        private bool _isInRange;
        [DataSourceProperty]
        public bool IsInRange
        {
            get { return _isInRange; }
            set { if (_isInRange != value) { _isInRange = value; OnPropertyChanged("IsInRange"); } }
        }

        private bool _isRangeEdge;
        [DataSourceProperty]
        public bool IsRangeEdge
        {
            get { return _isRangeEdge; }
            set { if (_isRangeEdge != value) { _isRangeEdge = value; OnPropertyChanged("IsRangeEdge"); } }
        }

        /// <summary>
        /// Gauntlet click target. Checks Shift state via Bannerlord's InputManager;
        /// invokes ClickHandler with (year, shiftHeld).
        /// </summary>
        public void ExecuteClick()
        {
            bool shift = false;
            try
            {
                shift = Input.IsKeyDown(InputKey.LeftShift) || Input.IsKeyDown(InputKey.RightShift);
            }
            catch
            {
                // Fallback: if the InputManager surface changed in a future engine,
                // treat as no-shift. Range-filter feature degrades, click-jump still works.
            }
            var h = ClickHandler;
            if (h != null) h(Year, shift);
        }
    }
}
