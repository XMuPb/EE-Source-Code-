using System;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.ScreenSystem;

namespace BandItPlus.UI
{
    // 2026-07-01 (Bandit-King GUI, Task 3): mounts the reusable BanditKingBrief
    // premium event panel as a focus layer over the campaign map. Modal; ticked
    // from SubModuleClassEntry.OnApplicationTick for the open fade + cascade.
    // Cloned from BpJailbreakBriefScreen (static-class + GauntletLayer-on-TopScreen
    // pattern shared by every BandIt Plus panel — the plan's "ScreenBase" note is
    // superseded by the actual proven repo pattern, which this mirrors exactly).
    //
    // Sprite fix: the module's compiled .tpac sheets (rebellion_siege) are NOT
    // auto-loaded into Gauntlet despite Config.xml AlwaysLoad, so we force a
    // clean Unload → Load before LoadMovie via TryLoadSpriteCategory (copied
    // from BanditIntroStoryScreen).
    public static class BanditKingBriefScreen
    {
        private static GauntletLayer _layer;
        private static BanditKingBriefVM _vm;
        private static bool _isOpen;
        private static TaleWorlds.CampaignSystem.CampaignTimeControlMode _prevTimeMode;
        private static bool _paused;

        // FIX A (2026-07-01) — serialize the modal queue. GoToWar enqueues Rises +
        // Offer (+ Marches) into the same main-thread drain; the first Open() sets
        // _isOpen=true and the rest USED to hit "if (_isOpen) return false" and get
        // DROPPED (losing the Offer panel's accept/refuse delegates). Now a panel that
        // arrives while one is open is backlogged here and re-opened from CloseNow, so
        // it shows Rises -> Offer -> Marches one-at-a-time as each is dismissed.
        private static readonly System.Collections.Generic.Queue<BanditKingBriefData> _pending
            = new System.Collections.Generic.Queue<BanditKingBriefData>();

        public static bool IsOpen => _isOpen;

        public static bool Open(BanditKingBriefData data)
        {
            try
            {
                // FIX A — one modal at a time: backlog instead of drop.
                if (_isOpen) { _pending.Enqueue(data); return false; }
                if (data == null) return false;

                // Load the module art category BEFORE LoadMovie (rebellion_siege
                // holds banditking_mad / _offer / _marching, rebellion_start /
                // _victory / _defeat).
                TryLoadSpriteCategory("rebellion_siege");
                // POLISH: the intel-row perk glyphs (SPPerks\...) + soft glow circles
                // live in vanilla SandBox's ui_characterdeveloper category. Load it the
                // same way BpJailbreakBriefScreen does so the badges + icons resolve now.
                TryLoadSpriteCategory("ui_characterdeveloper");

                _vm = new BanditKingBriefVM(data);
                _vm.CloseRequested += OnVmCloseRequested;

                _layer = new GauntletLayer(name: "BanditKingBrief", localOrder: 9005, shouldClear: false);
                _layer.InputRestrictions.SetInputRestrictions();
                _layer.LoadMovie("BanditKingBrief", _vm);

                // FIX B — null-TopScreen soft-lock guard. If there is no screen to
                // attach the layer to, ABORT before mutating _isOpen / PauseGame,
                // otherwise the game stays Stop-locked forever and _isOpen blocks
                // every future panel. Fix A's _pending re-shows on the next event, so
                // a dropped open here is harmless.
                var screen = ScreenManager.TopScreen;
                if (screen == null)
                {
                    try { if (_vm != null) _vm.CloseRequested -= OnVmCloseRequested; } catch { }
                    _layer = null;
                    _vm = null;
                    Log("Open aborted: TopScreen null");
                    return false;
                }
                screen.AddLayer(_layer);
                _host = screen;

                _layer.InputRestrictions.SetInputRestrictions();
                _layer.IsFocusLayer = true;
                ScreenManager.TrySetFocus(_layer);

                _isOpen = true;
                PauseGame();

                // 2026-07-02 (sound pass): per-event 2D sting on open (war horn for the
                // Rising, marching horn for the Marches, rebellion bell, etc). Fire-and-
                // forget: an unknown id is silently ignored by FMOD, never throws.
                try
                {
                    if (!string.IsNullOrEmpty(data.SoundId))
                        TaleWorlds.Engine.SoundEvent.PlaySound2D(data.SoundId);
                }
                catch (Exception sEx) { Log("open sound fail: " + sEx.Message); }

                Log("brief opened: " + (data.Title ?? "?"));
                return true;
            }
            catch (Exception ex)
            {
                Log("Open fail: " + ex.GetType().Name + ": " + ex.Message);
                _isOpen = false;
                _layer = null;
                _vm = null;
                return false;
            }
        }

        // Drives the VM's open fade + cascade. Called every frame from
        // SubModuleClassEntry.OnApplicationTick while the screen is open.
        public static void Tick(float dt)
        {
            try { _vm?.Tick(dt); } catch { }
        }

        // Subscribed to the VM's CloseRequested (raised after a button Action).
        private static void OnVmCloseRequested()
        {
            CloseNow();
        }

        // 07-11 crash fix: remove from the HOSTING screen, not TopScreen-at-close.
        private static TaleWorlds.ScreenSystem.ScreenBase _host;

        private static void CloseNow()
        {
            UnpauseGame();
            try
            {
                if (_vm != null) _vm.CloseRequested -= OnVmCloseRequested;
                if (_layer != null)
                {
                    _layer.InputRestrictions.ResetInputRestrictions();
                    var screen = _host ?? ScreenManager.TopScreen;
                    if (screen != null) screen.RemoveLayer(_layer);
                }
            }
            catch (Exception ex) { Log("Close fail: " + ex.Message); }
            _layer = null;
            _vm = null;
            _host = null;
            _isOpen = false;
            Log("brief closed");

            // FIX A — drain the backlog: show the next queued panel (Rises -> Offer ->
            // Marches). _isOpen is already false, so this Open takes the normal path.
            try { if (_pending.Count > 0) Open(_pending.Dequeue()); }
            catch (Exception e) { Log("pending open: " + e.Message); }
        }

        private static void PauseGame()
        {
            try
            {
                var camp = TaleWorlds.CampaignSystem.Campaign.Current;
                if (camp == null) return;
                _prevTimeMode = camp.TimeControlMode;
                camp.SetTimeControlModeLock(false);
                camp.TimeControlMode = TaleWorlds.CampaignSystem.CampaignTimeControlMode.Stop;
                camp.SetTimeControlModeLock(true);
                _paused = true;
            }
            catch (Exception ex) { Log("pause fail: " + ex.Message); }
        }

        private static void UnpauseGame()
        {
            try
            {
                if (!_paused) return;
                _paused = false;
                var camp = TaleWorlds.CampaignSystem.Campaign.Current;
                if (camp != null)
                {
                    camp.SetTimeControlModeLock(false);
                    camp.TimeControlMode = _prevTimeMode;
                }
            }
            catch (Exception ex) { Log("unpause fail: " + ex.Message); }
        }

        // Loads a module sprite category into the Gauntlet resource depot.
        // Copied verbatim from BanditIntroStoryScreen — forces a clean
        // Unload → Load so the compiled .tpac textures re-resolve NOW, after
        // module assets are registered (AlwaysLoad runs too early).
        private static void TryLoadSpriteCategory(string categoryName)
        {
            try
            {
                var spriteData = UIResourceManager.SpriteData;
                if (spriteData == null) { Log("sprite: SpriteData null"); return; }
                TaleWorlds.TwoDimension.SpriteCategory category;
                if (!spriteData.SpriteCategories.TryGetValue(categoryName, out category) || category == null)
                {
                    Log("sprite: category '" + categoryName + "' not registered — module sheet missing from SpriteData");
                    return;
                }

                Log("sprite: '" + categoryName + "' IsLoaded=" + category.IsLoaded + " " + DescribeSheets(category));

                if (category.IsLoaded)
                {
                    try { category.Unload(); } catch (Exception uEx) { Log("sprite: Unload threw " + uEx.Message); }
                }
                category.Load(UIResourceManager.ResourceContext, UIResourceManager.ResourceDepot);
                Log("sprite: '" + categoryName + "' RELOADED — " + DescribeSheets(category));
            }
            catch (Exception ex)
            {
                Log("sprite load '" + categoryName + "' failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string DescribeSheets(TaleWorlds.TwoDimension.SpriteCategory category)
        {
            try
            {
                var sheets = category.SpriteSheets;
                if (sheets == null) return "sheets=<null>";
                int total = 0, nulls = 0;
                foreach (var tex in sheets)
                {
                    total++;
                    if (tex == null) nulls++;
                }
                return "sheets=" + total + " nullSheets=" + nulls;
            }
            catch (Exception ex)
            {
                return "sheets=err(" + ex.GetType().Name + ")";
            }
        }

        private static void Log(string msg)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-KingBrief] " + msg); }
            catch { }
        }
    }
}
