using System;
using TaleWorlds.Library;

namespace EditableEncyclopedia
{
    public class ChronicleButtonVM : ViewModel
    {
        private readonly Action _onToggle;

        public ChronicleButtonVM(Action onToggle)
        {
            _onToggle = onToggle;
        }

        public void ExecuteOpenChronicle()
        {
            _onToggle?.Invoke();
        }
    }
}
