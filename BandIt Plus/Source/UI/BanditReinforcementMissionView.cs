using System;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.ScreenSystem;
using BandItPlus.HideoutVisit;

namespace BandItPlus.UI
{
    // Wave 4.30.x — runs a countdown overlay, then manually spawns the backup troops as a
    // timed REINFORCEMENT WAVE via Mission.SpawnTroop. (Folding troops into the enemy roster
    // pre-battle put them in the INITIAL deployment — the roster is snapshotted at battle
    // start, spike 2026-06-22 — so they spawned immediately. Manual spawn is the only reliable
    // way to make them arrive at a chosen time.) Active only in a bandit field battle with
    // pending backup.
    public class BanditReinforcementMissionView : MissionView
    {
        private GauntletLayer _layer;
        private BanditReinforcementVM _vm;
        private TaleWorlds.MountAndBlade.View.Screens.MissionScreen _missionScreen;
        private BattleSideEnum _enemySide = BattleSideEnum.None;
        private bool _decided, _released, _done;
        private float _elapsed;
        private const float kDecideAt = 0.5f;

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            // Hold the reinforcement countdown until the battle actually starts. OnMissionTick also
            // fires during MissionMode.Deployment (before the player deploys / presses "start battle");
            // ticking there starts the "warband inbound" clock early, while units are still being placed.
            if (Mission != null && Mission.Mode == MissionMode.Deployment) return;
            _elapsed += dt;

            if (!_decided && _elapsed >= kDecideAt)
            {
                _decided = true;
                try { Begin(); } catch (Exception ex) { Log("Begin fail: " + ex.GetType().Name + ": " + ex.Message); }
            }

            if (_vm == null || _done) return;
            try
            {
                _vm.Tick(dt);
                if (!_released && _vm.PollFinished()) { Release(); }
                if (_released && _vm.IsFinishedFading) { Cleanup(); }
            }
            catch (Exception ex) { Log("tick fail: " + ex.Message); }
        }

        private void Begin()
        {
            var mission = Mission.Current;
            if (mission == null) return;
            // TASK B1 — allow siege/settlement battles when the wave is reinforcing the PLAYER's
            // side; the enemy-reinforcement path stays field-battle-only (byte-identical gate).
            bool playerSide = BandItPlus.BanditPower.BanditBackupBehavior.ReinforcePlayerSide;
            if (!(mission.IsFieldBattle || playerSide)) return;
            int warbands = BandItPlus.BanditPower.BanditBackupBehavior.CurrentBackupCount;
            if (warbands <= 0) return;
            int troops = BandItPlus.BanditPower.BanditBackupBehavior.CurrentBackupTroops;

            var playerBattleSide = mission.PlayerTeam != null ? mission.PlayerTeam.Side : BattleSideEnum.Defender;
            // Median VFX targets the side the wave arrives on: player side when reinforcing the
            // player, else the enemy side (unchanged for the enemy path).
            _enemySide = playerSide
                ? playerBattleSide
                : (playerBattleSide == BattleSideEnum.Attacker ? BattleSideEnum.Defender : BattleSideEnum.Attacker);

            int delay = MCMSettings.Instance != null ? MCMSettings.Instance.BanditBackupArrivalDelay : 30;
            _vm = new BanditReinforcementVM(delay, warbands, troops);

            TryLoadSpriteCategory("logo1");
            TryLoadSpriteCategory("ui_characterdeveloper");
            _layer = new GauntletLayer(name: "BanditReinforcement", localOrder: 146, shouldClear: false);
            _layer.LoadMovie("BanditReinforcement", _vm);
            var screen = ScreenManager.TopScreen as TaleWorlds.MountAndBlade.View.Screens.MissionScreen;
            if (screen != null) { screen.AddLayer(_layer); _missionScreen = screen; }
            Log("countdown started (" + delay + "s, " + warbands + " warbands / " + troops + " troops)");
        }

        private void Release()
        {
            _released = true;
            try { SpawnWave(); } catch (Exception ex) { Log("release fail: " + ex.GetType().Name + ": " + ex.Message); }
            try { _vm.BeginArrival(); } catch { }
            try { PlayArrivalVfx(); } catch (Exception ex) { Log("vfx fail: " + ex.Message); }
        }

        // Manually spawn the recorded backup troops as an enemy-side reinforcement wave.
        private void SpawnWave()
        {
            var mission = Mission.Current; if (mission == null) return;
            // TASK B1 — when reinforcing the player, target PlayerTeam; otherwise the enemy team
            // (byte-identical to before when playerSide == false). The spawn SIDE is ultimately
            // decided by PartyAgentOrigin (origin is main.Party for the player path, an enemy
            // bandit party for the enemy path); this team is the participation/null guard.
            bool playerSide = BandItPlus.BanditPower.BanditBackupBehavior.ReinforcePlayerSide;
            var team = playerSide ? mission.PlayerTeam : mission.PlayerEnemyTeam;
            if (team == null) { Log("SpawnWave: no " + (playerSide ? "player" : "enemy") + " team"); return; }
            var origin = BandItPlus.BanditPower.BanditBackupBehavior.OriginParty;
            if (origin == null) { Log("SpawnWave: no origin party"); return; }
            var troops = BandItPlus.BanditPower.BanditBackupBehavior.PendingTroops;
            if (troops == null || troops.Count == 0) { Log("SpawnWave: no pending troops"); return; }

            int total = troops.Count, spawned = 0;
            for (int i = 0; i < total; i++)
            {
                var ch = troops[i];
                if (ch == null) continue;
                try
                {
                    var fclass = ch.IsRanged ? FormationClass.Ranged
                               : (ch.IsMounted ? FormationClass.Cavalry : FormationClass.Infantry);
                    mission.SpawnTroop(
                        new TaleWorlds.CampaignSystem.AgentOrigins.PartyAgentOrigin(origin, ch),
                        false, true, ch.IsMounted, true, total, i, true, true,
                        (Vec3?)null, (Vec2?)null, formationIndex: fclass);
                    spawned++;
                }
                catch (Exception ex) { Log("SpawnTroop fail: " + ex.GetType().Name + ": " + ex.Message); }
            }
            Log("spawned reinforcement wave: " + spawned + "/" + total);
        }

        // Dust/smoke burst + war-horn at the enemy median. Reuses BP's PROVEN particle path
        // (Scene.CreateBurstParticle via reflection + a confirmed name) and the proven 2D sound
        // cue. The earlier "psys_game_burst_dust" / GetEventIdFromString names were invalid in
        // 1.4.5 and silently no-op'd (no exception, no render).
        private void PlayArrivalVfx()
        {
            var mission = Mission.Current; if (mission == null) return;
            try { SoundEvent.PlaySound2D("event:/ui/notification/war_declared"); }
            catch (Exception ex) { Log("sound fail: " + ex.Message); }
            try
            {
                if (mission.Scene != null)
                {
                    Vec3 pos = GetEnemyMedian(mission);
                    var frame = new MatrixFrame(Mat3.Identity, pos);
                    BandItPlus.HideoutVisit.HideoutVisitNpcSpawnBehavior.SpawnParticleViaReflection(
                        "psys_smoke_basic", mission.Scene, ref frame);
                }
            }
            catch (Exception ex) { Log("particle fail: " + ex.Message); }
        }

        private Vec3 GetEnemyMedian(Mission mission)
        {
            try
            {
                foreach (var team in mission.Teams)
                    if (team != null && team.Side == _enemySide && team.QuerySystem != null)
                        return team.QuerySystem.AveragePosition.ToVec3();
            }
            catch { }
            return Agent.Main != null ? Agent.Main.Position : Vec3.Zero;
        }

        private void Cleanup()
        {
            _done = true;
            try { if (_layer != null && _missionScreen != null) _missionScreen.RemoveLayer(_layer); } catch { }
            _layer = null; _vm = null;
        }

        public override void OnMissionScreenFinalize()
        {
            try { if (_layer != null && _missionScreen != null) _missionScreen.RemoveLayer(_layer); } catch { }
            _layer = null; _vm = null;
            base.OnMissionScreenFinalize();
        }

        private static void TryLoadSpriteCategory(string name)
        {
            try
            {
                var sd = UIResourceManager.SpriteData; if (sd == null) return;
                TaleWorlds.TwoDimension.SpriteCategory c;
                if (sd.SpriteCategories.TryGetValue(name, out c) && c != null && !c.IsLoaded)
                    c.Load(UIResourceManager.ResourceContext, UIResourceManager.ResourceDepot);
            }
            catch { }
        }

        private static void Log(string m) { try { HideoutPeacefulVisitState.Log("[BP-Reinf] " + m); } catch { } }
    }
}
