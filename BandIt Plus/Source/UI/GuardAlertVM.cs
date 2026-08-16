using TaleWorlds.Library;

namespace BandItPlus.UI
{
    // Wave 4.39 — over-guard "?"/"!" awareness markers. Fixed pool of 6 slots; BpGuardAlertMissionView projects
    // each aware guard's head to screen space (X/Y in reference units) and toggles Question (suspicious) /
    // Alert (spotted). NOTE: every numeric prop bound to a widget prop MUST be float.
    public class GuardAlertVM : ViewModel
    {
        private float _m0X, _m0Y, _m1X, _m1Y, _m2X, _m2Y, _m3X, _m3Y, _m4X, _m4Y, _m5X, _m5Y;
        private bool _m0Q, _m0A, _m1Q, _m1A, _m2Q, _m2A, _m3Q, _m3A, _m4Q, _m4A, _m5Q, _m5A;
        private float _exitX, _exitY;
        private bool _exitShow;
        private float _followerX, _followerY;
        private bool _followerShow;
        private float _barkX, _barkY;
        private bool _barkShow;
        private string _barkText = "";

        [DataSourceProperty] public float M0X { get { return _m0X; } set { if (value != _m0X) { _m0X = value; OnPropertyChangedWithValue(value, "M0X"); } } }
        [DataSourceProperty] public float M0Y { get { return _m0Y; } set { if (value != _m0Y) { _m0Y = value; OnPropertyChangedWithValue(value, "M0Y"); } } }
        [DataSourceProperty] public bool M0Q { get { return _m0Q; } set { if (value != _m0Q) { _m0Q = value; OnPropertyChangedWithValue(value, "M0Q"); } } }
        [DataSourceProperty] public bool M0A { get { return _m0A; } set { if (value != _m0A) { _m0A = value; OnPropertyChangedWithValue(value, "M0A"); } } }
        [DataSourceProperty] public float M1X { get { return _m1X; } set { if (value != _m1X) { _m1X = value; OnPropertyChangedWithValue(value, "M1X"); } } }
        [DataSourceProperty] public float M1Y { get { return _m1Y; } set { if (value != _m1Y) { _m1Y = value; OnPropertyChangedWithValue(value, "M1Y"); } } }
        [DataSourceProperty] public bool M1Q { get { return _m1Q; } set { if (value != _m1Q) { _m1Q = value; OnPropertyChangedWithValue(value, "M1Q"); } } }
        [DataSourceProperty] public bool M1A { get { return _m1A; } set { if (value != _m1A) { _m1A = value; OnPropertyChangedWithValue(value, "M1A"); } } }
        [DataSourceProperty] public float M2X { get { return _m2X; } set { if (value != _m2X) { _m2X = value; OnPropertyChangedWithValue(value, "M2X"); } } }
        [DataSourceProperty] public float M2Y { get { return _m2Y; } set { if (value != _m2Y) { _m2Y = value; OnPropertyChangedWithValue(value, "M2Y"); } } }
        [DataSourceProperty] public bool M2Q { get { return _m2Q; } set { if (value != _m2Q) { _m2Q = value; OnPropertyChangedWithValue(value, "M2Q"); } } }
        [DataSourceProperty] public bool M2A { get { return _m2A; } set { if (value != _m2A) { _m2A = value; OnPropertyChangedWithValue(value, "M2A"); } } }
        [DataSourceProperty] public float M3X { get { return _m3X; } set { if (value != _m3X) { _m3X = value; OnPropertyChangedWithValue(value, "M3X"); } } }
        [DataSourceProperty] public float M3Y { get { return _m3Y; } set { if (value != _m3Y) { _m3Y = value; OnPropertyChangedWithValue(value, "M3Y"); } } }
        [DataSourceProperty] public bool M3Q { get { return _m3Q; } set { if (value != _m3Q) { _m3Q = value; OnPropertyChangedWithValue(value, "M3Q"); } } }
        [DataSourceProperty] public bool M3A { get { return _m3A; } set { if (value != _m3A) { _m3A = value; OnPropertyChangedWithValue(value, "M3A"); } } }
        [DataSourceProperty] public float M4X { get { return _m4X; } set { if (value != _m4X) { _m4X = value; OnPropertyChangedWithValue(value, "M4X"); } } }
        [DataSourceProperty] public float M4Y { get { return _m4Y; } set { if (value != _m4Y) { _m4Y = value; OnPropertyChangedWithValue(value, "M4Y"); } } }
        [DataSourceProperty] public bool M4Q { get { return _m4Q; } set { if (value != _m4Q) { _m4Q = value; OnPropertyChangedWithValue(value, "M4Q"); } } }
        [DataSourceProperty] public bool M4A { get { return _m4A; } set { if (value != _m4A) { _m4A = value; OnPropertyChangedWithValue(value, "M4A"); } } }
        [DataSourceProperty] public float M5X { get { return _m5X; } set { if (value != _m5X) { _m5X = value; OnPropertyChangedWithValue(value, "M5X"); } } }
        [DataSourceProperty] public float M5Y { get { return _m5Y; } set { if (value != _m5Y) { _m5Y = value; OnPropertyChangedWithValue(value, "M5Y"); } } }
        [DataSourceProperty] public bool M5Q { get { return _m5Q; } set { if (value != _m5Q) { _m5Q = value; OnPropertyChangedWithValue(value, "M5Q"); } } }
        [DataSourceProperty] public bool M5A { get { return _m5A; } set { if (value != _m5A) { _m5A = value; OnPropertyChangedWithValue(value, "M5A"); } } }
        [DataSourceProperty] public float ExitX { get { return _exitX; } set { if (value != _exitX) { _exitX = value; OnPropertyChangedWithValue(value, "ExitX"); } } }
        [DataSourceProperty] public float ExitY { get { return _exitY; } set { if (value != _exitY) { _exitY = value; OnPropertyChangedWithValue(value, "ExitY"); } } }
        [DataSourceProperty] public bool ExitShow { get { return _exitShow; } set { if (value != _exitShow) { _exitShow = value; OnPropertyChangedWithValue(value, "ExitShow"); } } }
        [DataSourceProperty] public float FollowerX { get { return _followerX; } set { if (value != _followerX) { _followerX = value; OnPropertyChangedWithValue(value, "FollowerX"); } } }
        [DataSourceProperty] public float FollowerY { get { return _followerY; } set { if (value != _followerY) { _followerY = value; OnPropertyChangedWithValue(value, "FollowerY"); } } }
        [DataSourceProperty] public bool FollowerShow { get { return _followerShow; } set { if (value != _followerShow) { _followerShow = value; OnPropertyChangedWithValue(value, "FollowerShow"); } } }
        [DataSourceProperty] public float BarkX { get { return _barkX; } set { if (value != _barkX) { _barkX = value; OnPropertyChangedWithValue(value, "BarkX"); } } }
        [DataSourceProperty] public float BarkY { get { return _barkY; } set { if (value != _barkY) { _barkY = value; OnPropertyChangedWithValue(value, "BarkY"); } } }
        [DataSourceProperty] public bool BarkShow { get { return _barkShow; } set { if (value != _barkShow) { _barkShow = value; OnPropertyChangedWithValue(value, "BarkShow"); } } }
        [DataSourceProperty] public string BarkText { get { return _barkText; } set { if (value != _barkText) { _barkText = value; OnPropertyChangedWithValue(value, "BarkText"); } } }

        // alarm >= 0.6 -> "!" (spotted); below -> "?" (suspicious).
        public void SetSlot(int i, float x, float y, float alarm)
        {
            bool alert = alarm >= 0.6f; bool q = !alert;
            switch (i)
            {
                case 0: M0X = x; M0Y = y; M0Q = q; M0A = alert; break;
                case 1: M1X = x; M1Y = y; M1Q = q; M1A = alert; break;
                case 2: M2X = x; M2Y = y; M2Q = q; M2A = alert; break;
                case 3: M3X = x; M3Y = y; M3Q = q; M3A = alert; break;
                case 4: M4X = x; M4Y = y; M4Q = q; M4A = alert; break;
                case 5: M5X = x; M5Y = y; M5Q = q; M5A = alert; break;
            }
        }

        public void HideSlot(int i)
        {
            switch (i)
            {
                case 0: M0Q = false; M0A = false; break;
                case 1: M1Q = false; M1A = false; break;
                case 2: M2Q = false; M2A = false; break;
                case 3: M3Q = false; M3A = false; break;
                case 4: M4Q = false; M4A = false; break;
                case 5: M5Q = false; M5A = false; break;
            }
        }
    }
}
