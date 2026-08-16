using System;
using System.Collections.Generic;
using System.Linq;
using SandBox;                          // SandBoxHelpers
using SandBox.Missions;                 // StealthFailCounterMissionLogic, OnStealthMissionCounterFailedEvent
using SandBox.Missions.MissionLogics;   // MissionAgentHandler
using TaleWorlds.CampaignSystem;        // Hero, Game, CharacterObject, Campaign, Occupation
using TaleWorlds.Core;                  // MissionMode
using TaleWorlds.Library;               // InquiryData, Vec3
using TaleWorlds.Engine;                // GameEntity
using SandBox.Missions.AgentBehaviors;  // AlarmedBehaviorGroup
using TaleWorlds.MountAndBlade;         // Mission, Agent, MissionLogic, Formation, Team, AgentState, KillingBlow, Blow, AttackCollisionData, MissionWeapon, AIStateFlag
using TaleWorlds.Localization;          // TextObject
using TaleWorlds.InputSystem;           // Input, InputKey, InputUsageMask
using TaleWorlds.Engine.GauntletUI;     // GauntletLayer
using TaleWorlds.ScreenSystem;          // ScreenManager
using TaleWorlds.MountAndBlade.View.Screens; // MissionScreen
using BandItPlus.UI;                     // LockpickVM
using BandItPlus.HideoutVisit;          // Log

namespace BandItPlus.Captivity
{
    // Wave 4.32 — player-as-prisoner jailbreak. Reuses the engine stealth stack (MissionMode.Stealth +
    // StealthFailCounterMissionLogic + the guards' patrol/alarm AI) but puts Agent.Main in the prisoner
    // slot. Adapted from SandBox PrisonBreakMissionController (see jailbreak-native-reference.md), dropping
    // the rescued-NPC/escort half. Routes its outcome to BanditCaptivityBehavior.
    public class BpJailbreakMissionController : MissionLogic
    {
        private readonly int _difficulty;
        private List<Agent> _aliveGuardAgents = new List<Agent>();
        private StealthFailCounterMissionLogic _failCounter;
        private bool _failedByStealth;
        private bool _won;
        private bool _routed;
        private bool _hadGuards;
        private int _captivesPlaced;
        private Agent _cellmateAgent;
        private readonly List<Agent> _prisonerAgents = new List<Agent>();   // the OTHER caged captives — freeable via [F]
        private readonly List<Agent> _freedAgents = new List<Agent>();      // freed ones — re-contour green each tick (LOD clears it)
        private int _prisonersFreed;
        private TaleWorlds.Core.Equipment _savedGear;   // the player's real battle gear (deep clone) — restored at the stash
        private static readonly uint FreedContourColor = new TaleWorlds.Library.Color(0.45f, 0.95f, 0.45f, 1f).ToUnsignedInteger(); // green "freed" glow
        private bool _cellmateGreeted;
        private float _cellmateGreetTimer;
        public static Agent CurrentCellmate;   // the captive in the player's cell — set during the mission so the dialog matches this exact agent
        public static bool CellmateFollowing;  // he agreed to come -> paint the follower indicator
        public static bool CellmateDead;       // he was cut down in the pit -> the sworn-companion reward is lost
        private static readonly uint FollowerContourColor = new TaleWorlds.Library.Color(1f, 0.84f, 0.35f, 1f).ToUnsignedInteger(); // gold glow (through walls)
        // Cell-block world positions (real 'civilian'-level prisoner points) for ambient captives — the matching
        // sp_dungeon_prisoner_* tags are 'civilian'-level and don't load at the 'prison_break' level, so tag-spawn
        // falls back to one shared origin (the cluster). We place captives here by world-position instead.
        private static readonly TaleWorlds.Library.Vec3[] _cellCaptiveSpots =
        {
            new TaleWorlds.Library.Vec3(288.8f, 94.1f, 0.33f, -1f),
            new TaleWorlds.Library.Vec3(274.8f, 115.8f, 0.33f, -1f),
            new TaleWorlds.Library.Vec3(277.5f, 107.7f, 0.33f, -1f),
            new TaleWorlds.Library.Vec3(299.283f, 96.404f, 0.330f, -1f),
        };
        private static TaleWorlds.Library.Vec3 CellCaptiveSpot(int i) => _cellCaptiveSpots[i % _cellCaptiveSpots.Length];

        // Wave 4.41 — lootable chests along the escape route (positions read off the on-screen pos readout).
        private static readonly TaleWorlds.Library.Vec3[] _chestSpots =
        {
            new TaleWorlds.Library.Vec3(140.9f, 194.5f, 6.88f, -1f),
            new TaleWorlds.Library.Vec3(159.5f, 170.2f, 7.04f, -1f),
            new TaleWorlds.Library.Vec3(165.6f, 176.2f, 4.89f, -1f),
            new TaleWorlds.Library.Vec3(176.1f, 166.0f, 5.14f, -1f),
            new TaleWorlds.Library.Vec3(208.2f, 167.6f, 4.17f, -1f),
            new TaleWorlds.Library.Vec3(205.9f, 165.3f, 4.12f, -1f),
            new TaleWorlds.Library.Vec3(225.9f, 145.1f, 4.32f, -1f),
            new TaleWorlds.Library.Vec3(250.5f, 141.9f, 4.04f, -1f),
            new TaleWorlds.Library.Vec3(258.6f, 152.4f, 4.11f, -1f),
            new TaleWorlds.Library.Vec3(276.5f, 154.6f, 4.05f, -1f),
            new TaleWorlds.Library.Vec3(291.3f, 124.0f, 0.12f, -1f),
            new TaleWorlds.Library.Vec3(215.7f, 159.3f, 4.0f, -1f),
        };
        private readonly bool[] _chestLooted = new bool[_chestSpots.Length];
        private bool _hasKey;                  // stolen the master key -> all chests open instantly
        private int _lockpickChestIndex = -1;  // -1 = the cell door; >=0 = lockpicking that chest
        private const bool ShowPositionReadout = false;  // live "pos X, Y, Z" coord-grabbing aid — OFF for normal play (all 12 chests placed)

        // Make the cellmate follow the player (native FollowAgentBehavior — the exact path bodyguards use).
        // Called from the dialog when the player chooses "escape together".
        public static void StartCellmateFollow()
        {
            var cm = CurrentCellmate;
            if (cm == null || !cm.IsActive()) return;
            try
            {
                var nav = cm.GetComponent<CampaignAgentComponent>()?.AgentNavigator;
                if (nav == null) return;
                var daily = nav.GetBehaviorGroup<DailyBehaviorGroup>() ?? nav.AddBehaviorGroup<DailyBehaviorGroup>();
                var pteam = Mission.Current?.PlayerTeam;
                if (pteam != null && cm.Team != pteam) cm.SetTeam(pteam, false);  // same team -> never flips to Fight
                var follow = daily.GetBehavior<FollowAgentBehavior>() ?? daily.AddBehavior<FollowAgentBehavior>();
                follow.SetTargetAgent(Agent.Main);
                daily.SetScriptedBehavior<FollowAgentBehavior>();
                nav.ForceThink(0f);
                CellmateFollowing = true;
                Log("cellmate now following the player");
            }
            catch (Exception ex) { Log("StartCellmateFollow fail: " + ex.Message); }
        }

        // Suppress the native "[F] Talk" prompt for the cellmate after the intro (no re-opening the dialog).
        // The prompt is gated by MissionConversationLogic.IsThereAgentAction, which skips agents in its private
        // _uninteractableAgents set (no public adder -> reflection).
        public static void SuppressCellmateTalk()
        {
            var cm = CurrentCellmate;
            if (cm == null) return;
            try
            {
                var cl = Mission.Current?.GetMissionBehavior<SandBox.Conversation.MissionLogics.MissionConversationLogic>();
                if (cl == null) return;
                var f = typeof(SandBox.Conversation.MissionLogics.MissionConversationLogic).GetField("_uninteractableAgents",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                (f?.GetValue(cl) as System.Collections.Generic.HashSet<Agent>)?.Add(cm);
                Log("cellmate [F] Talk suppressed");
            }
            catch (Exception ex) { Log("SuppressCellmateTalk fail: " + ex.Message); }
        }

        // Force-load the perk + mapbar sprite sheets so the native HUD icons (SPPerks\*, MapBar\*) render in-mission.
        private static void EnsureIconSprites()
        {
            try
            {
                TaleWorlds.Engine.GauntletUI.UIResourceManager.LoadSpriteCategory("ui_characterdeveloper");
            }
            catch (Exception ex) { Log("icon sprite load fail: " + ex.Message); }
        }

        // Ambient pit audio (Wave 4.40) — a looping dungeon bed, plus occasional drips + chain rattles in the dark.
        private void StartAmbient()
        {
            try
            {
                _ambientBed = SoundEvent.CreateEventFromString("event:/mission/ambient/area/interior/dungeon", Mission.Scene);
                if (_ambientBed != null && _ambientBed.IsValid) _ambientBed.Play();
            }
            catch (Exception ex) { Log("ambient start fail: " + ex.Message); }
        }

        private void StopAmbient()
        {
            try { if (_ambientBed != null) { _ambientBed.Stop(); _ambientBed.Release(); } } catch { }
            _ambientBed = null;
        }

        private void PlayAmbientDetail(string ev)
        {
            try
            {
                var p = Agent.Main.Position;
                float ang = MBRandom.RandomFloat * 6.2832f, dist = 3f + MBRandom.RandomFloat * 4f;   // 3-7m off, random bearing
                var pos = new TaleWorlds.Library.Vec3(p.x + (float)Math.Cos(ang) * dist, p.y + (float)Math.Sin(ang) * dist, p.z + 0.6f, -1f);
                PlayClip3D(ev, pos);
            }
            catch { }
        }
        private const bool SpawnGuardsEnabled = true;
        private GameEntity _cellDoor;
        private bool _sealed;
        private float _doorDamage;
        // 2026-07-26: _doorOpening is never set true anywhere in this file, so the
        // swing-open animation in OnMissionTick is currently dead code. Left in place
        // (the feature is half-built, not broken-by-accident) but flagged here — if it
        // is ever enabled, _doorBaseFrame below must already hold the real captured
        // pose, which it now does.
        private bool _doorOpening;
        private float _doorOpenT;
        private int _doorFaceGroupId = -1; // navmesh face group blocked while the cell is locked (-2 = native 1+2)
        // 2026-07-26: now actually captured at instantiation. It was declared,
        // documented as "the door's captured closed pose", read by the swing animation
        // — and never assigned, so it was permanently the default all-zero MatrixFrame.
        // Had the swing ever run, the door would have jumped to the world origin with a
        // degenerate rotation. CS0649 was pointing at a real defect, not noise.
        private TaleWorlds.Library.MatrixFrame _doorBaseFrame;
        private TaleWorlds.Library.Vec3 _doorRefPos;           // fixed VISUAL doorway position (lockpick proximity)
        private TaleWorlds.Library.Vec3 _escapePoint = new TaleWorlds.Library.Vec3(152.0f, 235.9f, 6.99f, -1f); // the scene's exit door that "leads out"
        private const string DoorMesh = "empire_dungeon_cell_door_a_closed";
        private const string DoorBody = "bo_empire_dungeon_cell_door_a_closed"; // authored collision hull

        // Lockpick GUI (Wave 4.33) — Skyrim-style rotating-pick minigame.
        private GauntletLayer _lockLayer;
        private LockpickVM _lockVm;
        private bool _lockOpen;
        private float _pickAngle;     // current pick orientation (deg)
        private float _sweetSpot;     // hidden unlock angle (deg)
        private float _cylTurn;       // cylinder turn progress 0..1
        private float _pickHealth = 1f;
        private float _breakFlashT;        // red keyhole-flash timer after a pick snaps
        private float _clickTimer;         // throttle for the pick-scrape click
        private GauntletLayer _promptLayer;   // persistent bottom-center "[F] Pick the lock" prompt
        private LockpickPromptVM _promptVm;
        private bool _promptAdded;
        private GauntletLayer _alarmLayer;    // top-center "spotted" detection meter
        private AlarmMeterVM _alarmVm;
        private bool _alarmAdded;
        private float _detection;             // self-computed detection 0..1 (rises while a guard sees you)
        private float _alarmPulseT;           // pulse accumulator for the SPOTTED flash
        private GauntletLayer _escapeHudLayer; // right-side objective tracker
        private EscapeHudVM _escapeHudVm;
        private bool _escapeHudAdded;
        private float _escapeHudPulseT;
        private int _prevS1, _prevS2, _prevS3;
        private bool _escaping;
        private float _escapeFlourishT;
        private bool _convoSeen;              // the cellmate dialog has occurred (so the HUD shows after it)
        // Clean-escape stats (Wave 4.38): elapsed time, whether a guard ever clearly spotted you, peak guard count.
        private float _escapeElapsed;
        private bool _everSpotted;
        private int _guardsTotal;
        private float _vignetteT;             // spotted-vignette heartbeat clock
        private float _banterT;               // cellmate banter visible-timer
        private bool _banterIntro, _banterSuspicious, _banterSpotted, _banterNearExit;
        private float _warnT;                 // pick-snap warning subtitle timer
        private bool _spottedAll;             // once clearly spotted, raise the alarm on every guard (they converge)
        private TaleWorlds.Library.Vec3 _lastPos;   // for footstep-noise speed (crouch/walk = quiet, run = loud)
        private bool _lastPosSet;
        private float _footstepT;
        private SoundEvent _ambientBed;             // looping dungeon ambience
        private float _dripT = 3f, _chainT = 7f;    // ambient detail one-shot timers
        // Handoff to the campaign-side success handler, which applies the scaled renown + gold reward.
        internal static int RewardRenown;
        internal static int RewardGold;
        internal static bool RewardGhost;
        private GauntletLayer _brandLayer;    // bottom-left credit watermark
        private bool _brandAdded;
        private BrandVM _brandVm;
        private float _brandPulseT;

        private readonly string _captorName;
        public BpJailbreakMissionController(int difficulty, string captorName) { _difficulty = difficulty; _captorName = captorName; CellmateDead = false; }

        public override void OnBehaviorInitialize()
        {
            base.OnBehaviorInitialize();
            try { Game.Current.EventManager.RegisterEvent<OnStealthMissionCounterFailedEvent>(OnStealthFailed); }
            catch (Exception ex) { Log("event register fail: " + ex.Message); }
        }

        public override void AfterStart()
        {
            try
            {
                Mission.SetMissionMode(MissionMode.Stealth, true);
                EnsureIconSprites();
                StartAmbient();
                Mission.IsInventoryAccessible = false;
                Mission.IsQuestScreenAccessible = false;
                Mission.IsKingdomWindowAccessible = false;
                Log("AS1: mode set");

                var mah = Mission.GetMissionBehavior<MissionAgentHandler>();
                Log("AS2: mah=" + (mah != null) + " props=" + (mah == null || mah.TownPassageProps == null ? "null" : mah.TownPassageProps.Count.ToString()));
                if (mah != null && mah.TownPassageProps != null)
                    foreach (var prop in mah.TownPassageProps) prop.Deactivate();

                _failCounter = Mission.GetMissionBehavior<StealthFailCounterMissionLogic>();
                if (_failCounter != null)
                {
                    _failCounter.FailCounterSeconds = BanditJailbreakLauncher.FailSeconds(_difficulty);
                    _failCounter.SetFailTexts(
                        new TextObject("{=bp_jailbreakmissioncontroller_030}Caught!"),
                        new TextObject("{=bp_jailbreakmissioncontroller_031}The brigands raised the alarm and beat you back into the pit."));
                }
                Log("AS3: failCounter=" + (_failCounter != null));

                Mission.AllowAiTicking = false;
                Mission.DoesMissionRequireCivilianEquipment = false; // guards spawn in BATTLE gear (armor), not civilian tunics
                SandBoxHelpers.MissionHelper.SpawnPlayer(civilianEquipment: false, noHorses: true);
                Log("AS4: player spawned, main=" + (Agent.Main != null));

                // Place the player at the cell spawn spot (located via the live position readout).
                if (Agent.Main != null)
                {
                    try
                    {
                        Agent.Main.TeleportToPosition(new TaleWorlds.Library.Vec3(285.433f, 93.485f, 0.330f, -1f));
                        Log("AS4b: player placed at " + Agent.Main.Position);
                    }
                    catch (Exception ex) { Log("AS4b place fail: " + ex.Message); }
                }

                // Spawn guards via the PER-AGENT method (NOT SpawnLocationCharacters, which fires the
                // settlement event RecruitmentAgentSpawnBehavior NREs on at a hideout). Still wires the
                // navigator + AddStealthAgentBehaviors -> full stealth AI.
                int guardsSpawned = 0;
                if (SpawnGuardsEnabled && mah != null && CampaignMission.Current != null && CampaignMission.Current.Location != null)
                {
                    foreach (var lc in CampaignMission.Current.Location.GetCharacterList())
                    {
                        try
                        {
                            var ga = mah.SpawnDefaultLocationCharacter(lc, true);
                            if (ga != null && ga.Character is CharacterObject co)
                            {
                                if (co.Occupation == Occupation.Villager)
                                {
                                    // Ambient captive: keep it UNARMED (the spawn path re-applies the troop's armed set)
                                    // and place it in the cell block BY WORLD-POSITION (its tag doesn't load at this level).
                                    try { var pe = (co.FirstCivilianEquipment ?? co.Equipment).Clone(true); ga.UpdateSpawnEquipmentAndRefreshVisuals(pe); } catch (Exception exq) { Log("captive disarm fail: " + exq.Message); }
                                    if (_cellmateAgent == null) { _cellmateAgent = ga; CurrentCellmate = ga; } // first captive (cell spot next to player) = the talkable cellmate
                                    else { _prisonerAgents.Add(ga); }                                          // every other captive is freeable
                                    try { ga.TeleportToPosition(CellCaptiveSpot(_captivesPlaced)); _captivesPlaced++; } catch (Exception exq) { Log("captive place fail: " + exq.Message); }
                                }
                                else
                                {
                                    guardsSpawned++;
                                    // The LocationCharacter spawn path drops the AgentData equipment override, leaving
                                    // the troop's CIVILIAN set (tunic). Force the troop's BATTLE gear post-spawn.
                                    try
                                    {
                                        if (co.FirstBattleEquipment != null)
                                            ga.UpdateSpawnEquipmentAndRefreshVisuals(co.FirstBattleEquipment);
                                    }
                                    catch (Exception exq) { Log("guard regear fail: " + exq.Message); }
                                }
                            }
                        }
                        catch (Exception ex) { Log("guard spawn fail: " + ex.Message); }
                    }
                }
                int enemyCount = Mission.Agents.Count(a => a != null && a.IsHuman && a.Team == Mission.PlayerEnemyTeam);
                _hadGuards = enemyCount > 0;
                Log("AS5: spawnReturns=" + guardsSpawned + " enemyAgents=" + enemyCount + " total=" + Mission.Agents.Count);
                RelocateCellGuards();
                Mission.AllowAiTicking = true;

                if (Agent.Main != null)
                {
                    Agent.Main.SetClothingColor1(4279111698u);
                    Agent.Main.SetClothingColor2(4279111698u);
                    // Prisoner: strip all weapons + shield (keep clothes/armor).
                    var prisonerEq = Hero.MainHero.BattleEquipment.Clone(false);
                    prisonerEq[EquipmentIndex.Weapon0] = default(EquipmentElement);
                    prisonerEq[EquipmentIndex.Weapon1] = default(EquipmentElement);
                    prisonerEq[EquipmentIndex.Weapon2] = default(EquipmentElement);
                    prisonerEq[EquipmentIndex.Weapon3] = default(EquipmentElement);
                    prisonerEq[EquipmentIndex.ExtraWeaponSlot] = default(EquipmentElement);
                    try { _savedGear = new TaleWorlds.Core.Equipment(TaleWorlds.CampaignSystem.Hero.MainHero.BattleEquipment); } catch (Exception exg) { Log("save gear fail: " + exg.Message); }
                    Agent.Main.UpdateSpawnEquipmentAndRefreshVisuals(prisonerEq);
                    var pteam = Mission.Current.Teams.Player;
                    Log("AS6: pteam=" + (pteam != null));
                    Agent.Main.Formation = new Formation(pteam, 0);
                    Agent.Main.SetCrouchMode(true);
                }

                _aliveGuardAgents = Mission.Agents.Where(x =>
                    x != null && x.IsHuman && x != Agent.Main && x.Team == Mission.PlayerEnemyTeam).ToList();

                // Seal the cell: hide the open-door mesh + instantiate a closed door at the same frame.
                SealCellDoor();
                FullDoorScan();
                Log("AS7: sealed=" + _sealed);

                Log("AfterStart done; main=" + (Agent.Main != null) + " guards=" + _aliveGuardAgents.Count);
            }
            catch (Exception ex) { Log("AfterStart EXCEPTION: " + ex.GetType().Name + ": " + ex.Message + " | STACK: " + ex.StackTrace); }
        }

        public override void OnMissionTick(float dt)
        {
            // INVISIBLE WALL: physics bodies don't stop agents and the doorway navmesh is the shared floor
            // (group 0), so while the cell is locked we keep the player on the cell side by clamping their
            // position to a plane just before the door — a wall the player physically cannot cross.
            if (_sealed && Agent.Main != null && Agent.Main.IsActive())
            {
                var cellP = new TaleWorlds.Library.Vec3(285.433f, 93.485f, 0.330f, -1f);
                var doorP = new TaleWorlds.Library.Vec3(289.682f, 93.984f, 0.330f, -1f);
                float dx = doorP.x - cellP.x, dy = doorP.y - cellP.y;
                float dlen = (float)Math.Sqrt(dx * dx + dy * dy);
                if (dlen > 0.01f)
                {
                    dx /= dlen; dy /= dlen;
                    var pc = Agent.Main.Position;
                    float proj = (pc.x - cellP.x) * dx + (pc.y - cellP.y) * dy; // distance along cell->door
                    float limit = dlen - 0.55f; // hold ~0.55m before the door plane (still in lockpick range)
                    if (proj > limit)
                    {
                        var np = new TaleWorlds.Library.Vec3(cellP.x + dx * limit, cellP.y + dy * limit, pc.z, -1f);
                        Agent.Main.TeleportToPosition(np);
                    }
                }
            }

            // Animate the door swinging open after lockpick/break (collision already removed in Unseal).
            if (_doorOpening && _cellDoor != null)
            {
                _doorOpenT += dt / 1.5f;
                float k = _doorOpenT < 1f ? _doorOpenT : 1f;
                var fr = _doorBaseFrame;                  // the door's OWN captured closed pose (real or fallback)
                fr.rotation.RotateAboutUp(1.45f * k);     // swing ~83 deg around its hinge/pivot
                try { _cellDoor.SetFrame(ref fr); } catch { }
                if (_doorOpenT >= 1f) { _doorOpening = false; Log("door fully open"); }
            }

            // Bottom-center "[F]" prompt: "Pick the lock" at the sealed door, then "Escape the jail" at the exit point.
            bool showPrompt = false; string promptLabel = BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_001", "Pick the lock");
            int chestNear = -1; Agent guardNear = null; bool guardBehind = false;
            if (Agent.Main != null && Agent.Main.IsActive())
            {
                if (_sealed && !_lockOpen)
                {
                    var toDoor = _doorRefPos - Agent.Main.Position;
                    float hSq = toDoor.x * toDoor.x + toDoor.y * toDoor.y;
                    var look = Agent.Main.LookDirection;
                    float tl = (float)Math.Sqrt(hSq), ll = (float)Math.Sqrt(look.x * look.x + look.y * look.y);
                    float facing = (tl > 0.01f && ll > 0.01f) ? (toDoor.x * look.x + toDoor.y * look.y) / (tl * ll) : 0f;
                    if (hSq < 5.5f && facing > 0.30f)   // ~2.3m + must face the door (tightened for balance)
                    {
                        showPrompt = true; promptLabel = BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_001", "Pick the lock");
                        if (Input.IsKeyPressed(InputKey.F)) { _lockpickChestIndex = -1; OpenLockpick(); }
                    }
                }
                else if (!_sealed && Agent.Main.Position.DistanceSquared(_escapePoint) < 9f)
                {
                    showPrompt = true; promptLabel = BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_002", "Escape the jail");
                    if (Input.IsKeyPressed(InputKey.F) && !_escaping)
                    {
                        _won = true; _escaping = true; _escapeFlourishT = 1.7f;
                        Log("escaped via exit -> WIN (flourish)");
                        try { PlayClip("event:/ui/notification/unlock"); } catch { }
                        try
                        {
                            ComputeEscapeReward(out _, out _, out _, out string sLine, out string rLine);
                            if (_escapeHudVm != null) { _escapeHudVm.FlourishStats = sLine; _escapeHudVm.FlourishReward = rLine; }
                        }
                        catch (Exception ex) { Log("flourish stats fail: " + ex.Message); }
                    }
                }
                else if (!_sealed && (chestNear = NearestUnlootedChest()) >= 0)
                {
                    showPrompt = true; promptLabel = _hasKey ? BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_003", "Open the chest") : BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_004", "Pick the chest lock");
                    if (Input.IsKeyPressed(InputKey.F)) { if (_hasKey) LootChest(chestNear); else { _lockpickChestIndex = chestNear; OpenLockpick(); } }
                }
                else if (!_sealed && !_hasKey && (guardNear = NearestPickpocketGuard(out guardBehind)) != null)
                {
                    showPrompt = true; promptLabel = guardBehind ? BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_005", "Steal the key") : BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_006", "Steal the key  (he'll spot you!)");
                    if (Input.IsKeyPressed(InputKey.F)) TryStealKey(guardNear, guardBehind);
                }
                else if (!_sealed)
                {
                    Agent fp = NearestFreeablePrisoner();
                    if (fp != null)
                    {
                        showPrompt = true; promptLabel = BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_007", "Free this prisoner");
                        if (Input.IsKeyPressed(InputKey.F)) FreePrisoner(fp);
                    }
                }
            }
            EnsurePromptLayer();
            if (!showPrompt && ShowPositionReadout && Agent.Main != null && Agent.Main.IsActive())
            {
                var rp = Agent.Main.Position;
                showPrompt = true;
                promptLabel = "pos  " + rp.x.ToString("0.0") + " ,  " + rp.y.ToString("0.0") + " ,  " + rp.z.ToString("0.0");
            }
            if (_promptVm != null) { _promptVm.Show = showPrompt; _promptVm.Label = promptLabel; }
            if (_lockOpen) TickLockpick(dt);

            // Cellmate intro: auto-start his conversation once the player + cellmate are settled in the cell.
            if (!_cellmateGreeted && _cellmateAgent != null && _cellmateAgent.IsActive() && Agent.Main != null && Agent.Main.IsActive())
            {
                _cellmateGreetTimer += dt;
                if (_cellmateGreetTimer >= 1.0f)
                {
                    _cellmateGreeted = true;
                    try { Mission.GetMissionBehavior<SandBox.Conversation.MissionLogics.MissionConversationLogic>()?.StartConversation(_cellmateAgent, true); }
                    catch (Exception ex) { Log("cellmate talk fail: " + ex.Message); }
                }
            }

            // Follower indicator: gold contour glow (visible through walls) on the recruited cellmate. Re-applied
            // each tick because agent LOD/rebuild clears it.
            if (CellmateFollowing && CurrentCellmate != null && CurrentCellmate.IsActive())
            {
                try { CurrentCellmate.AgentVisuals?.SetContourColor(FollowerContourColor, true); } catch { }
            }
            for (int fi = 0; fi < _freedAgents.Count; fi++)
            {
                var fa = _freedAgents[fi];
                if (fa != null && fa.IsActive()) { try { fa.AgentVisuals?.SetContourColor(FreedContourColor, true); } catch { } }
            }

            // Footstep noise (Wave 4.40 "crouch = quieter"): RUNNING near a guard is loud — it bumps their alarm;
            // walking/crouching is slow, so it's quiet. Speed comes from a position delta (no crouch API needed).
            if (Agent.Main != null && Agent.Main.IsActive())
            {
                var pp = Agent.Main.Position;
                if (_lastPosSet)
                {
                    float mx = pp.x - _lastPos.x, my = pp.y - _lastPos.y;
                    float speed = dt > 0.0001f ? (float)Math.Sqrt(mx * mx + my * my) / dt : 0f;
                    _footstepT += dt;
                    if (speed > 3.2f && _footstepT >= 0.55f)   // ~running; a walk/crouch is ~1.5 m/s
                    {
                        _footstepT = 0f;
                        var fnoise = Agent.Main.GetWorldPosition();   // footstep SOURCE = the player, so guards investigate toward you
                        foreach (var g in LiveGuards())
                        {
                            if (g.Position.DistanceSquared(pp) >= 36f) continue;   // within ~6m
                            try { g.GetComponent<CampaignAgentComponent>()?.AgentNavigator?.GetBehaviorGroup<AlarmedBehaviorGroup>()?.AddAlarmFactor(0.10f, fnoise); } catch { }
                        }
                    }
                }
                _lastPos = pp; _lastPosSet = true;
            }

            // Ambient detail — occasional drips + chain rattles a few metres off in the dark.
            if (Agent.Main != null && Agent.Main.IsActive())
            {
                _dripT -= dt;
                if (_dripT <= 0f) { _dripT = 5f + MBRandom.RandomFloat * 5f; PlayAmbientDetail("event:/mission/ambient/detail/faucet"); }
                _chainT -= dt;
                if (_chainT <= 0f) { _chainT = 11f + MBRandom.RandomFloat * 8f; PlayAmbientDetail("event:/mission/combat/impact/metal_weapon/metal"); }
            }

            // Spotting meter — rises while a guard has line of sight; decays otherwise.
            EnsureAlarmLayer();
            UpdateAlarmMeter(dt);
            UpdateEscapeHud(dt);
            EnsureBrandLayer();
            UpdateBrand(dt);
            if (_convoSeen && !_escaping && !_won && !_routed) _escapeElapsed += dt;

            if (_escaping)
            {
                _escapeFlourishT -= dt;
                if (_escapeHudVm != null) { _escapeHudVm.FlourishVisible = true; _escapeHudVm.FlourishAlpha = Math.Min(1f, _escapeHudVm.FlourishAlpha + dt * 3f); }
                if (_escapeFlourishT <= 0f) Mission.EndMission();
                return;
            }
            if (_routed || _won) return;

            // Lose: knocked out by the guards.
            if (Agent.Main == null || !Agent.Main.IsActive())
            {
                Log("player down -> fail (caught)");
                Mission.EndMission();
                return;
            }
            // Lose: the alarm was sustained — the guards have you cornered.
            if (_failedByStealth)
            {
                Log("alarm sustained -> caught, fail");
                Mission.EndMission();
                return;
            }
            // (Escape is now via the "[F] Escape the jail" prompt at _escapePoint, handled above.)
        }

        // ── Lockpick minigame (Skyrim-style rotating pick) ───────────────────────
        // Persistent bottom-center "[F] Pick the lock" prompt layer (no input capture; toggled by proximity).
        private void EnsurePromptLayer()
        {
            if (_promptAdded) return;
            try
            {
                var screen = ScreenManager.TopScreen as MissionScreen;
                if (screen == null) return;
                _promptVm = new LockpickPromptVM();
                _promptLayer = new GauntletLayer("BpLockpickPrompt", 5, false);
                _promptLayer.LoadMovie("LockpickPrompt", _promptVm);
                screen.AddLayer(_promptLayer);
                _promptAdded = true;
            }
            catch (Exception ex) { Log("prompt layer fail: " + ex.Message); _promptAdded = true; }
        }

        // Top-center "spotted" detection meter layer (no input capture).
        private void EnsureAlarmLayer()
        {
            if (_alarmAdded) return;
            try
            {
                var screen = ScreenManager.TopScreen as MissionScreen;
                if (screen == null) return;
                _alarmVm = new AlarmMeterVM();
                _alarmLayer = new GauntletLayer("BpAlarmMeter", 6, false);
                _alarmLayer.LoadMovie("AlarmMeter", _alarmVm);
                screen.AddLayer(_alarmLayer);
                _alarmAdded = true;
            }
            catch (Exception ex) { Log("alarm layer fail: " + ex.Message); _alarmAdded = true; }
        }

        private void EnsureEscapeHudLayer()
        {
            if (_escapeHudAdded) return;
            try
            {
                var screen = ScreenManager.TopScreen as MissionScreen;
                if (screen == null) return;
                _escapeHudVm = new EscapeHudVM();
                _escapeHudLayer = new GauntletLayer("BpEscapeHud", 7, false);
                _escapeHudLayer.LoadMovie("EscapeHud", _escapeHudVm);
                screen.AddLayer(_escapeHudLayer);
                _escapeHudAdded = true;
            }
            catch (Exception ex) { Log("escape hud layer fail: " + ex.Message); _escapeHudAdded = true; }
        }

        private void EnsureBrandLayer()
        {
            if (_brandAdded) return;
            try
            {
                var screen = ScreenManager.TopScreen as MissionScreen;
                if (screen == null) return;
                _brandVm = new BrandVM();
                _brandLayer = new GauntletLayer("BpBrand", 4, false);
                _brandLayer.LoadMovie("BrandCredit", _brandVm);
                screen.AddLayer(_brandLayer);
                _brandAdded = true;
            }
            catch (Exception ex) { Log("brand layer fail: " + ex.Message); _brandAdded = true; }
        }

        // Breathe the logo glow + the gold accent for a living watermark.
        private void UpdateBrand(float dt)
        {
            if (_brandVm == null) return;
            _brandPulseT += dt;
            float p = 0.5f + 0.5f * (float)Math.Sin(_brandPulseT * 1.8f);
            _brandVm.Glow = 0.28f + 0.52f * p;
            _brandVm.AccentAlpha = 0.55f + 0.45f * p;
        }

        // Right-side objective tracker — appears once the cellmate dialog has ended, and the rows light up as you
        // progress: pick the lock -> slip past the guards -> reach the exit door.
        private void UpdateEscapeHud(float dt)
        {
            EnsureEscapeHudLayer();
            if (_escapeHudVm == null) return;
            if (Mission.Mode == MissionMode.Conversation) _convoSeen = true;
            bool show = _convoSeen && Mission.Mode != MissionMode.Conversation && !_won && !_routed;
            _escapeHudVm.Show = show;
            if (!show) { _escapeHudVm.PanelAlpha = 0f; _escapeHudVm.VignetteVisible = false; _escapeHudVm.BanterVisible = false; _escapeHudVm.WarnVisible = false; return; }
            _escapeHudVm.PanelAlpha = Math.Min(1f, _escapeHudVm.PanelAlpha + dt * 2.5f);

            _escapeHudVm.O1Text = BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_008", "Pick the lock on the cell door");
            _escapeHudVm.O2Text = BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_009", "Slip past the guards");
            _escapeHudVm.O3Text = BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_010", "Reach the door  —  press [F]");
            string hdrLoc = string.IsNullOrEmpty(_captorName)
                ? BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_032", "Beneath the bandit hideout")
                : new TextObject("{=bp_jailbreakmissioncontroller_033}Held by {NAME}").SetTextVariable("NAME", _captorName).ToString();
            string hdrDiff = _difficulty <= 2
                ? BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_034", "Easy")
                : _difficulty == 3 ? BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_035", "Normal")
                : BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_036", "Hard");
            _escapeHudVm.HeaderInfo = hdrLoc + "   ·   " + hdrDiff;
            int guardsLeft = Mission.Agents.Count(a => a != null && a.IsHuman && a.IsActive() && a.Team == Mission.PlayerEnemyTeam);
            if (guardsLeft > _guardsTotal) _guardsTotal = guardsLeft;
            _escapeHudVm.GuardsLine = new TextObject("{=bp_jailbreakmissioncontroller_037}Guards on patrol:  {N}").SetTextVariable("N", guardsLeft).ToString();
            _escapeHudVm.RewardLine = (CellmateFollowing || CellmateDead) ? BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_011", "Reward:  your freedom  +  a sworn companion") : BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_012", "Reward:  your freedom");
            _escapeHudVm.RewardStruck = CellmateDead;   // cross out the companion reward if he fell
            int prisonersTotal = _prisonersFreed + _prisonerAgents.Count;
            _escapeHudVm.PrisonersVisible = prisonersTotal > 0;
            if (prisonersTotal > 0) _escapeHudVm.PrisonersLine = new TextObject("{=bp_jailbreakmissioncontroller_038}Prisoners freed:  {FREED} / {TOTAL}").SetTextVariable("FREED", _prisonersFreed).SetTextVariable("TOTAL", prisonersTotal).ToString();
            int chestsLooted = 0; for (int ck = 0; ck < _chestLooted.Length; ck++) if (_chestLooted[ck]) chestsLooted++;
            _escapeHudVm.ChestVisible = true;
            _escapeHudVm.ChestLine = new TextObject("{=bp_jailbreakmissioncontroller_039}Chests:  {LOOTED} / {TOTAL}       Key:  {KEY}").SetTextVariable("LOOTED", chestsLooted).SetTextVariable("TOTAL", _chestSpots.Length).SetTextVariable("KEY", _hasKey ? "✓" : "✗").ToString();

            _escapeHudPulseT += dt;
            float pulse = 0.40f + 0.60f * (float)Math.Sin(_escapeHudPulseT * 3.0f);
            bool nearExit = Agent.Main != null && Agent.Main.IsActive() && Agent.Main.Position.DistanceSquared(_escapePoint) < 9f;
            int ns1 = _sealed ? 1 : 2;
            int ns2 = _sealed ? 0 : (nearExit ? 2 : 1);
            int ns3 = (!_sealed && nearExit) ? 1 : 0;
            if (ns1 == 2 && _prevS1 < 2) TriggerToast(BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_013", "Lock picked!"));
            else if (ns2 == 2 && _prevS2 < 2) TriggerToast(BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_014", "Past the guards!"));
            _prevS1 = ns1; _prevS2 = ns2; _prevS3 = ns3;
            SetObj(1, ns1, pulse); SetObj(2, ns2, pulse); SetObj(3, ns3, pulse);
            if (_escapeHudVm.ToastAlpha > 0f)
            {
                _escapeHudVm.ToastAlpha = Math.Max(0f, _escapeHudVm.ToastAlpha - dt * 0.55f);
                if (_escapeHudVm.ToastAlpha <= 0f) _escapeHudVm.ToastVisible = false;
            }
            _escapeHudVm.Hint = _sealed
                ? BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_015", "Face the cell door and press [F] to pick the lock.")
                : nearExit
                    ? BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_016", "The way out — press [F] to escape!")
                    : BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_017", "Keep to the shadows. Stay out of the guards' sight.");

            // red "spotted" flash when a guard is actually detecting you (uses the same AlarmFactor the meter does)
            float alertPulse = 0.5f + 0.5f * (float)Math.Sin(_escapeHudPulseT * 7f);
            _escapeHudVm.AlertAlpha = _detection > 0.40f ? ((_detection - 0.40f) / 0.60f) * alertPulse : 0f;

            // spotted red vignette — ramps in past 0.45 detection, heartbeat-pulse that speeds up the more spotted you are.
            float danger = Math.Max(0f, (_detection - 0.45f) / 0.55f);
            if (danger > 0.01f)
            {
                _vignetteT += dt * (3f + danger * 7f);
                float vp = 0.45f + 0.55f * (float)Math.Sin(_vignetteT);
                _escapeHudVm.VignetteVisible = true;
                _escapeHudVm.VignetteAlpha = danger * 0.26f * vp;
            }
            else { _escapeHudVm.VignetteVisible = false; _escapeHudVm.VignetteAlpha = 0f; }

            // cellmate banter — short whispered lines on key beats (only while he's actually with you).
            if (CellmateFollowing)
            {
                string say = null;
                if (!_banterIntro) { _banterIntro = true; say = BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_018", "Stay close. We go out together."); }
                else if (!_banterSpotted && _detection >= 0.60f) { _banterSpotted = true; say = BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_019", "They've seen us — go, go!"); }
                else if (!_banterSuspicious && _detection >= 0.25f) { _banterSuspicious = true; say = BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_020", "Easy… a guard. Keep still."); }
                else if (!_banterNearExit && nearExit) { _banterNearExit = true; say = BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_021", "The door — we're almost free!"); }
                if (say != null) { _escapeHudVm.BanterText = "“" + say + "”"; _escapeHudVm.BanterVisible = true; _banterT = 3.8f; }
            }
            if (_banterT > 0f) { _banterT -= dt; if (_banterT <= 0f) _escapeHudVm.BanterVisible = false; }
            if (_warnT > 0f) { _warnT -= dt; if (_warnT <= 0f) _escapeHudVm.WarnVisible = false; }

            // cellmate-following status line
            _escapeHudVm.CellmateVisible = CellmateFollowing;
        }

        // st: 0 pending, 1 active, 2 done. Active marker glows + gets a breathing highlight bar behind its row.
        private void SetObj(int n, int st, float pulse)
        {
            string col = st == 2 ? "#D8B45CFF" : st == 1 ? "#FFE9A8FF" : "#5A4E33FF";
            float glow = st == 1 ? pulse : 0f;
            float back = st == 1 ? 0.10f + 0.10f * pulse : 0f;
            switch (n)
            {
                case 1: _escapeHudVm.O1Color = col; _escapeHudVm.O1Glow = glow; _escapeHudVm.O1Back = back; break;
                case 2: _escapeHudVm.O2Color = col; _escapeHudVm.O2Glow = glow; _escapeHudVm.O2Back = back; break;
                case 3: _escapeHudVm.O3Color = col; _escapeHudVm.O3Glow = glow; _escapeHudVm.O3Back = back; break;
            }
        }

        private void TriggerToast(string text)
        {
            if (_escapeHudVm == null) return;
            _escapeHudVm.ToastText = text;
            _escapeHudVm.ToastAlpha = 1f;
            _escapeHudVm.ToastVisible = true;
            try { PlayClip("event:/ui/notification/relation_increase"); } catch { }
        }

        // Self-computed detection 0..1: a guard "sees" the player within ~22m + inside a ~60deg cone,
        // accumulating while seen and decaying otherwise. TODO(decompile): swap this proxy for the real
        // max AlarmedBehaviorGroup.AlarmFactor so the meter is exactly in sync with the native catch.
        private void UpdateAlarmMeter(float dt)
        {
            if (_alarmVm == null) return;
            // Real engine detection: max AlarmFactor across enemy guards (0..1). The engine raises AlarmFactor
            // ONLY with true line of sight (FOV + range + wall occlusion), so cover keeps it decaying to 0;
            // 1.0 == a guard is fully alarmed -> native StealthFailCounter starts the catch. No proxy, no reflection.
            float seen = 0f;
            var me = Agent.Main;
            if (me != null && me.IsActive() && !_won && !_routed)
            {
                foreach (var a in Mission.Agents)
                {
                    if (a == null || !a.IsActive() || !a.IsHuman || a == me) continue;
                    if (a.Team != Mission.PlayerEnemyTeam) continue;
                    var nav = a.GetComponent<CampaignAgentComponent>()?.AgentNavigator;
                    var grp = nav?.GetBehaviorGroup<AlarmedBehaviorGroup>();
                    if (grp == null) continue;
                    float over = grp.AlarmFactor - 1.0f; if (over > seen) seen = over;   // subtract the ~1.0 "investigate/Cautious" floor: a noise-alarmed guard reads ~0; only vision climbing toward 2.0 = spotted
                }
            }
            _detection = Math.Min(1f, Math.Max(0f, seen));
            if (_detection >= 0.60f) _everSpotted = true;   // a guard clearly saw you — Ghost bonus lost
            if (!_spottedAll && _detection >= 0.70f) { _spottedAll = true; try { RaiseAlarm(); } catch { } Log("player SPOTTED -> raise alarm on all guards"); }

            float shown = _failedByStealth ? 1f : _detection;
            _alarmVm.Show = shown > 0.04f;
            _alarmVm.FillWidth = shown * 172f;
            if (shown >= 0.98f)
            {
                _alarmPulseT += dt;
                float p = 0.72f + 0.28f * (float)Math.Sin(_alarmPulseT * 8f);
                _alarmVm.ContainerAlpha = p; _alarmVm.EyeAlpha = p;
                _alarmVm.Tint = "#E0402CFF"; _alarmVm.State = BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_022", "SPOTTED");
            }
            else
            {
                _alarmVm.ContainerAlpha = Math.Min(1f, 0.28f + shown * 0.72f);
                _alarmVm.EyeAlpha = Math.Min(1f, 0.20f + shown * 0.80f);
                if (shown >= 0.45f) { _alarmVm.Tint = "#E0A038FF"; _alarmVm.State = BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_023", "SUSPICIOUS"); }
                else { _alarmVm.Tint = "#C8A24EFF"; _alarmVm.State = ""; }
            }
        }

        private void OpenLockpick()
        {
            try
            {
                _lockVm = new LockpickVM();
                _pickAngle = 90f;
                _sweetSpot = MBRandom.RandomFloat * 360f;
                _cylTurn = 0f; _pickHealth = 1f;
                UpdateLockVm();
                _lockLayer = new GauntletLayer("BpLockpick", 210, false);
                _lockLayer.LoadMovie("Lockpick", _lockVm);
                _lockLayer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
                _lockLayer.IsFocusLayer = true;
                var screen = ScreenManager.TopScreen as MissionScreen;
                if (screen != null) { screen.AddLayer(_lockLayer); ScreenManager.TrySetFocus(_lockLayer); }
                _lockOpen = true;
                Log("lockpick GUI opened (sweet=" + (int)_sweetSpot + " rog=" + RogueryLevel() + ")");
            }
            catch (Exception ex) { Log("OpenLockpick fail: " + ex.GetType().Name + ": " + ex.Message); CloseLockpick(); }
        }

        private void TickLockpick(float dt)
        {
            if (_lockVm == null) { _lockOpen = false; return; }
            if (Input.IsKeyPressed(InputKey.Escape)) { Log("lockpick cancelled"); CloseLockpick(); return; }

            if (Input.IsKeyDown(InputKey.A)) _pickAngle -= 150f * dt;
            if (Input.IsKeyDown(InputKey.D)) _pickAngle += 150f * dt;
            while (_pickAngle < 0f) _pickAngle += 360f;
            while (_pickAngle >= 360f) _pickAngle -= 360f;

            // pick-scrape tick while rotating
            if (Input.IsKeyDown(InputKey.A) || Input.IsKeyDown(InputKey.D))
            { _clickTimer += dt; if (_clickTimer >= 0.13f) { _clickTimer = 0f; PlayClip("event:/ui/checkbox"); } }
            else _clickTimer = 0.13f;

            float tolerance = 9f + RogueryLevel() * 0.10f;    // 9deg at 0, 39deg at 300
            float delta = AngleDelta(_pickAngle, _sweetSpot);  // 0..180
            float closeness = 1f - delta / 180f;               // 0 far .. 1 at the sweet spot
            bool tension = Input.IsKeyDown(InputKey.LeftMouseButton);

            if (tension)
            {
                // warmer/colder "give": the keyhole glow builds + greens as you near the sweet spot
                _lockVm.GlowAlpha = 0.30f + closeness * 0.70f;
                _lockVm.GlowColor = delta <= tolerance ? "#7BE08AFF"
                                   : (closeness > 0.82f ? "#C8E060FF"
                                   : (closeness > 0.58f ? "#E0B046FF" : "#C0531EFF"));
                if (delta <= tolerance)
                {
                    _cylTurn += dt * 0.9f;
                    if (_cylTurn >= 1f) { LockpickSuccess(); return; }
                }
                else
                {
                    float cap = 0.12f + closeness * 0.55f;
                    if (_cylTurn < cap) _cylTurn += dt * 0.6f; else _cylTurn = cap;
                    _pickHealth -= dt * (0.18f + (delta / 180f) * 0.55f);
                    if (_pickHealth <= 0f) { LockpickBreak(); return; }
                }
            }
            else
            {
                _lockVm.GlowAlpha = 0.10f;
                _lockVm.GlowColor = "#6E5A2EFF";
                _cylTurn -= dt * 2.5f;
                if (_cylTurn < 0f) _cylTurn = 0f;
            }
            if (_breakFlashT > 0f) { _breakFlashT -= dt; _lockVm.GlowAlpha = 0.95f; _lockVm.GlowColor = "#FF4530FF"; }
            UpdateLockVm();
        }

        private void UpdateLockVm()
        {
            if (_lockVm == null) return;
            _lockVm.PickAngle = _pickAngle;            // REAL needle rotation (degrees) about the keyhole hub
            _lockVm.WrenchAngle = _cylTurn * 14f;      // tension wrench tilts as the cylinder turns
            float t = _cylTurn * 260f; _lockVm.TurnFill = t < 0f ? 0f : (t > 260f ? 260f : t);
            float h = _pickHealth * 260f; _lockVm.HealthFill = h < 0f ? 0f : (h > 260f ? 260f : h);
            _lockVm.HealthColor = _pickHealth > 0.5f ? "#7BE08AFF" : (_pickHealth > 0.25f ? "#E8C25AFF" : "#FF6B6BFF");
        }

        private void LockpickSuccess()
        {
            if (_lockVm != null) { _lockVm.Status = BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_024", "UNLOCKED"); _lockVm.TurnFill = 260f; }
            PlayClip("event:/ui/notification/unlock");
            Log("lockpick success");
            CloseLockpick();
            if (_lockpickChestIndex >= 0) { int c = _lockpickChestIndex; _lockpickChestIndex = -1; LootChest(c); }
            else Unseal(false);
        }

        private void LockpickBreak()
        {
            Log("pick broke -> noise/alarm");
            if (_lockVm != null) _lockVm.Status = BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_025", "PICK BROKE!");
            try { LockpickNoise(); } catch { }
            if (_escapeHudVm != null) { _escapeHudVm.WarnText = BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_026", "The lockpick snapped! Keep quiet — they may have heard you."); _escapeHudVm.WarnVisible = true; }
            _warnT = 3.8f;
            PlayClip("event:/mission/combat/impact/metal_weapon/metal");   // 2D snap so it's audible wherever you pick (the 3D-at-door clip was inaudible at chests)
            _breakFlashT = 0.5f;
            _pickHealth = 1f; _cylTurn = 0f; _sweetSpot = MBRandom.RandomFloat * 360f;
            UpdateLockVm();
        }

        // Wave 4.40 — a snapped pick is a sharp NOISE: a moderate, distance-based suspicion bump on guards
        // (louder the closer they are) plus a HUD-meter nudge — risky, but not the instant full alarm a break
        // used to trigger. Repeated snaps stack toward a real "!" alert.
        // Live enemy guards straight from the mission — the cached _aliveGuardAgents filter missed them, so the
        // noise/alarm code reads this instead and can't go stale (matches what the over-head markers use).
        private List<Agent> LiveGuards()
        {
            var list = new List<Agent>();
            var t = Mission.PlayerEnemyTeam;
            if (t == null) return list;
            var agents = Mission.Agents;
            for (int i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                if (a != null && a.IsHuman && a.IsActive() && a != Agent.Main && a.Team == t) list.Add(a);
            }
            return list;
        }

        // Chest looting (Wave 4.41): nearest un-looted chest within reach, the pickpocket target, and the loot roll.
        private int NearestUnlootedChest()
        {
            if (Agent.Main == null) return -1;
            var p = Agent.Main.Position;
            int best = -1; float bestSq = 3.5f;   // ~1.9m (tightened for balance — must stand at the chest)
            for (int i = 0; i < _chestSpots.Length; i++)
            {
                if (_chestLooted[i]) continue;
                float d = p.DistanceSquared(_chestSpots[i]);
                if (d < bestSq) { bestSq = d; best = i; }
            }
            return best;
        }

        private Agent NearestPickpocketGuard(out bool fromBehind)
        {
            fromBehind = false;
            if (Agent.Main == null) return null;
            var p = Agent.Main.Position;
            Agent best = null; float bestSq = 6.25f;   // ~2.5m
            foreach (var g in LiveGuards())
            {
                float d = g.Position.DistanceSquared(p);
                if (d < bestSq) { bestSq = d; best = g; }
            }
            if (best != null)
            {
                var look = best.LookDirection;
                float dot = (p.x - best.Position.x) * look.x + (p.y - best.Position.y) * look.y;
                fromBehind = dot < 0f;   // player is behind the guard -> safe to lift the key
            }
            return best;
        }

        private void TryStealKey(Agent guard, bool fromBehind)
        {
            if (_hasKey || guard == null) return;
            if (fromBehind)
            {
                _hasKey = true;
                try { PlayClip("event:/ui/notification/quest_finished"); } catch { }
                try { TriggerToast(BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_027", "Master key lifted — every chest opens now")); } catch { }
                Log("master key pickpocketed");
            }
            else
            {
                try
                {
                    var grp = guard.GetComponent<CampaignAgentComponent>()?.AgentNavigator?.GetBehaviorGroup<AlarmedBehaviorGroup>();
                    if (grp != null) { grp.AddAlarmFactor(2f, Agent.Main.GetWorldPosition()); guard.SetAlarmState((Agent.AIStateFlag)2); }
                }
                catch { }
                _detection = Math.Min(1f, _detection + 0.5f);
                try { TriggerToast(BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_028", "Caught! He felt your hand")); } catch { }
                Log("pickpocket caught -> guard alerted");
            }
        }

        private void LootChest(int i)
        {
            if (i < 0 || i >= _chestLooted.Length || _chestLooted[i]) return;
            _chestLooted[i] = true;
            int tier = 1 + MBRandom.RandomInt(5);                 // 1..5
            int gold = 60 * tier + MBRandom.RandomInt(80 * tier); // ~60..~700
            string itemNote = "";
            try
            {
                var items = Game.Current.ObjectManager.GetObjectTypeList<ItemObject>();
                ItemObject pick = null;
                if (tier >= 4 && MBRandom.RandomFloat < 0.5f)
                    pick = RandomItem(items, it => it != null && (int)it.Tier >= 2 && (it.WeaponComponent != null || it.ArmorComponent != null) && it.Value > 200 && it.Value < 50000);
                if (pick == null && MBRandom.RandomFloat < 0.7f)
                    pick = RandomItem(items, it => it != null && it.IsTradeGood && it.Value > 0);
                if (pick != null) { TaleWorlds.CampaignSystem.Party.MobileParty.MainParty.ItemRoster.AddToCounts(pick, 1); itemNote = "    +  " + pick.Name; }
            }
            catch (Exception ex) { Log("chest item fail: " + ex.Message); }
            try { TaleWorlds.CampaignSystem.Actions.GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, gold, false); } catch { }
            try { TriggerToast("Looted  " + gold + " gold" + itemNote); } catch { }
            Log("looted chest " + i + " tier " + tier + " -> " + gold + " gold" + itemNote);
        }

        private static ItemObject RandomItem(IReadOnlyList<ItemObject> items, Func<ItemObject, bool> pred)
        {
            if (items == null || items.Count == 0) return null;
            for (int t = 0; t < 14; t++) { var it = items[MBRandom.RandomInt(items.Count)]; if (pred(it)) return it; }
            return null;
        }

        private void LockpickNoise()
        {
            if (Agent.Main == null) return;
            var noise = Agent.Main.GetWorldPosition();   // alarm SOURCE = the player at the cell -> guards should path HERE
            int total = 0, alerted = 0; float nearest = 99999f;
            foreach (var g in LiveGuards())
            {
                total++;
                float dist = (float)Math.Sqrt(g.Position.DistanceSquared(_doorRefPos));
                if (dist < nearest) nearest = dist;
                try
                {
                    var grp = g.GetComponent<CampaignAgentComponent>()?.AgentNavigator?.GetBehaviorGroup<AlarmedBehaviorGroup>();
                    if (grp == null || dist > 60f) continue;
                    float before = grp.AlarmFactor;
                    float cur = grp.AlarmFactor;
                    if (cur < 1.1f) grp.AddAlarmFactor(1.1f - cur, noise);   // push just past the 1.0 investigate gate — enough to path here, far below "spotted"
                    g.SetAlarmState((Agent.AIStateFlag)1);   // Cautious — PAIR it: the guard now paths to the noise to investigate
                    alerted++;
                    Log("  noise->guard d=" + dist.ToString("0.0") + "m alarm " + before.ToString("0.00") + "=>" + grp.AlarmFactor.ToString("0.00"));
                }
                catch { }
            }
            Log("LockpickNoise: guards=" + total + " nearest=" + nearest.ToString("0.0") + "m alerted=" + alerted);
        }

        private void CloseLockpick()
        {
            try
            {
                if (_lockLayer != null)
                {
                    _lockLayer.InputRestrictions.ResetInputRestrictions();
                    _lockLayer.IsFocusLayer = false;
                    var screen = ScreenManager.TopScreen as MissionScreen;
                    if (screen != null) screen.RemoveLayer(_lockLayer);
                }
            }
            catch (Exception ex) { Log("CloseLockpick fail: " + ex.Message); }
            _lockLayer = null; _lockVm = null; _lockOpen = false;
        }

        private static float AngleDelta(float a, float b)
        {
            float d = Math.Abs(a - b) % 360f;
            return d > 180f ? 360f - d : d;
        }

        private static int RogueryLevel()
        {
            try { return Hero.MainHero != null ? Hero.MainHero.GetSkillValue(TaleWorlds.Core.DefaultSkills.Roguery) : 0; }
            catch { return 0; }
        }

        // ── Sound (Native FMOD events; loaded for every mission) ─────────────────
        private void PlayClip(string path)
        {
            try { TaleWorlds.Engine.SoundEvent.CreateEventFromString(path, Mission.Scene).Play(); }
            catch (Exception ex) { Log("sfx " + path + " fail: " + ex.Message); }
        }
        private void PlayClip3D(string path, TaleWorlds.Library.Vec3 pos)
        {
            try { TaleWorlds.Engine.SoundEvent.CreateEventFromString(path, Mission.Scene).PlayInPosition(pos); }
            catch (Exception ex) { Log("sfx3d " + path + " fail: " + ex.Message); }
        }

        // ── Cell door: lockpick (quiet) / break (loud) ───────────────────────────
        private GameEntity FindNearestEntityByPrefab(string prefab, TaleWorlds.Library.Vec3 near)
        {
            try
            {
                var list = new List<GameEntity>();
                Mission.Scene.GetEntities(ref list);
                GameEntity best = null; float bestSq = float.MaxValue;
                foreach (var e in list)
                {
                    if (e == null) continue;
                    string pn = null; try { pn = e.GetPrefabName(); } catch { }
                    if (pn != prefab) continue;
                    float d = e.GlobalPosition.DistanceSquared(near);
                    if (d < bestSq) { bestSq = d; best = e; }
                }
                return best;
            }
            catch (Exception ex) { Log("FindEntity fail: " + ex.Message); return null; }
        }

        // Full-scene diagnostic: log every functional UsableMachine + every door/gate entity in the WHOLE
        // scene (with positions) so we can find the REAL doors that exist here instead of faking one.
        private void FullDoorScan()
        {
            try
            {
                int u = 0;
                if (Mission.MissionObjects != null)
                {
                    foreach (var mo in Mission.MissionObjects)
                    {
                        var um = mo as UsableMachine;
                        if (um == null) continue;
                        var ge = um.GameEntity;
                        Log("usable: " + um.GetType().Name + " @ " + (ge != null ? ge.GlobalPosition.ToString() : "?"));
                        if (++u >= 40) break;
                    }
                }
                Log("usable scan done (" + u + ")");

                var list = new List<GameEntity>();
                Mission.Scene.GetEntities(ref list);
                int d = 0;
                foreach (var e in list)
                {
                    if (e == null) continue;
                    string pn = null; try { pn = e.GetPrefabName(); } catch { }
                    if (string.IsNullOrEmpty(pn)) continue;
                    if (pn.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0
                        || pn.IndexOf("gate", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Log("door-ent: " + pn + " @ " + e.GlobalPosition + " hasBody=" + e.HasBody());
                        if (++d >= 40) break;
                    }
                }
                Log("door-ent scan done (" + d + ")");
            }
            catch (Exception ex) { Log("door scan fail: " + ex.Message); }
        }

        // Move any guard that spawned inside the player's cell out to a far sp_guard post.
        private void RelocateCellGuards()
        {
            try
            {
                var cell = new TaleWorlds.Library.Vec3(285.433f, 93.485f, 0.330f, -1f);
                // FindEntitiesWithTag("sp_guard") returns nothing, so borrow valid navmesh positions from
                // the guards that spawned AWAY from the cell, then move any cell-guard onto one of those.
                var guards = new List<Agent>();
                foreach (var a in Mission.Agents.ToList())
                    if (a != null && a.IsHuman && a.Team == Mission.PlayerEnemyTeam) guards.Add(a);
                var far = guards.Where(a => a.Position.DistanceSquared(cell) > 64f).ToList(); // spawned >8m from cell
                if (far.Count == 0) { Log("relocate: no far guards to borrow positions from"); return; }
                int fi = 0, moved = 0;
                foreach (var a in guards)
                {
                    if (a.Position.DistanceSquared(cell) < 25f) // within ~5m of the cell = in the cell
                    {
                        a.TeleportToPosition(far[fi % far.Count].Position); fi++; moved++;
                    }
                }
                Log("relocate: moved " + moved + " cell guards (" + far.Count + " far guards)");
            }
            catch (Exception ex) { Log("relocate fail: " + ex.Message); }
        }

        private void SealCellDoor()
        {
            try
            {
                // Place the REAL closed-door mesh over the baked-OPEN doorway (runtime level switching is proven
                // impossible). Lockpick removes it -> the baked open doorway behind is revealed (door "opens").
                var doorPos = new TaleWorlds.Library.Vec3(289.682f, 93.984f, 0.185f, -1f);
                _doorRefPos = doorPos;
                var fr = TaleWorlds.Library.MatrixFrame.Identity;
                fr.rotation.RotateAboutUp(-1.222f);
                fr.origin = doorPos;
                _cellDoor = GameEntity.Instantiate(Mission.Scene, DoorMesh, fr);
                _sealed = _cellDoor != null;
                // 2026-07-26: capture the closed pose the swing animation pivots from.
                // Previously never assigned — see the _doorBaseFrame declaration.
                _doorBaseFrame = fr;
                // No collision body on the door — physics doesn't stop agents anyway, and the invisible-wall
                // position-clamp (OnMissionTick) handles blocking. (Removed the old capsule "hero box".)
                if (_sealed) { Log("seal: placed real closed door (visual only; invisible wall blocks) @ " + _cellDoor.GlobalPosition); }
                else Log("seal: closed-door instantiate FAILED");

                // Physics bodies do NOT stop agents (proven) — agent locomotion is navmesh-driven. Block the
                // NAVMESH face at the doorway, exactly like vanilla locked doors (Scene.SetAbilityOfFacesWithId).
                // Read the navmesh face group AT the doorway (groups 1-6 were empty), then disable just that
                // group so agents (incl. the player) can't cross - the way vanilla locked doors work.
                try
                {
                    var np = new TaleWorlds.Library.Vec3(289.682f, 93.984f, 0.4f, -1f);
                    var rec = default(TaleWorlds.Library.PathFaceRecord);
                    Mission.Scene.GetNavMeshFaceIndex(ref rec, np, false);
                    int gid = Mission.Scene.GetIdOfNavMeshFace(rec.FaceIndex);
                    Log("doorway face: idx=" + rec.FaceIndex + " group=" + gid);
                    if (gid > 0)
                    {
                        _doorFaceGroupId = gid;
                        int affected = Mission.Scene.SetAbilityOfFacesWithId(gid, false);
                        Log("door navmesh: BLOCKED group " + gid + " affected=" + affected);
                    }
                    else Log("door navmesh: doorway group=" + gid + " (0=shared floor) - not blocking (would lock whole area); need a blocker face");
                }
                catch (Exception exn) { Log("door navmesh block fail: " + exn.Message); }
            }
            catch (Exception ex) { Log("seal fail: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Scene.GetEntities returns only roots; the real cell door may be nested, so walk children too.
        private static void CollectEntitiesRecursive(GameEntity e, List<GameEntity> acc)
        {
            if (e == null) return;
            acc.Add(e);
            int n = 0; try { n = e.ChildCount; } catch { }
            for (int i = 0; i < n; i++)
            {
                GameEntity c = null; try { c = e.GetChild(i); } catch { }
                if (c != null) CollectEntitiesRecursive(c, acc);
            }
        }

        // The instantiated door mesh carries no collision body; attach a real one at runtime
        // (the dropped-weapon path: PhysicsShape.GetFromResource + AddPhysics static).
        private bool TryAttachDoorBody(GameEntity door)
        {
            // A WIDE vertical capsule barrier, explicitly positioned at the door's world origin. The authored
            // door-panel hull attaches but lets the player slip past (edge gaps / unknown local offset), so we
            // plug the whole doorway with a fat capsule that has no gaps.
            try
            {
                var gf = door.GetGlobalFrame();
                var up = new TaleWorlds.Library.Vec3(0f, 0f, 1f, -1f);
                var p1 = gf.origin + up * 0.1f;
                var p2 = gf.origin + up * 2.4f;
                door.AddCapsuleAsBody(p1, p2, 0.95f, BodyFlags.Barrier3D, "wood");
                bool ok = door.HasBody();
                Log("door body: wide capsule r=0.95 @ " + gf.origin + " hasBody=" + ok + " flag=" + door.BodyFlag);
                return ok;
            }
            catch (Exception ex) { Log("door body capsule fail: " + ex.Message); return false; }
        }

        public override void OnRegisterBlow(Agent attacker, Agent victim, WeakGameEntity realHitEntity, Blow b, ref AttackCollisionData collisionData, in MissionWeapon attackerWeapon)
        {
            if (!_sealed || _cellDoor == null || attacker != Agent.Main || victim != null || !realHitEntity.IsValid) return;
            // The door is static; a blow whose hit-entity sits at the door's position = hitting the door.
            if (realHitEntity.GlobalPosition.DistanceSquared(_cellDoor.GlobalPosition) < 4f)
            {
                _doorDamage += b.InflictedDamage;
                Log("door hit, dmg=" + _doorDamage);
                if (_doorDamage >= 120f) Unseal(true);
            }
        }

        private void Unseal(bool loud)
        {
            if (!_sealed) return;
            _sealed = false;
            Log("door opened (" + (loud ? "BROKEN" : "picked") + ") -> removing closed door, revealing baked open doorway");
            PlayClip3D("event:/mission/siege/door/open", _doorRefPos);
            try
            {
                if (_cellDoor != null)
                {
                    if (_cellDoor.HasBody()) _cellDoor.RemovePhysics();
                    _cellDoor.Remove(0);
                    _cellDoor = null;
                }
            }
            catch (Exception ex) { Log("door remove fail: " + ex.Message); }
            // Re-enable the navmesh face so the player can walk through the now-open doorway.
            try
            {
                if (_doorFaceGroupId == -3) { for (int g = 1; g <= 6; g++) Mission.Scene.SetAbilityOfFacesWithId(g, true); }
                else if (_doorFaceGroupId == -2) { Mission.Scene.SetAbilityOfFacesWithId(1, true); Mission.Scene.SetAbilityOfFacesWithId(2, true); }
                else if (_doorFaceGroupId >= 0) Mission.Scene.SetAbilityOfFacesWithId(_doorFaceGroupId, true);
                Log("door navmesh: UNBLOCKED (" + _doorFaceGroupId + ")");
            }
            catch (Exception exn) { Log("door navmesh unblock fail: " + exn.Message); }
            if (loud) RaiseAlarm();
        }

        private void RaiseAlarm()
        {
            int n = 0;
            foreach (var g in LiveGuards())
            {
                try
                {
                    g.GetComponent<CampaignAgentComponent>().AgentNavigator.GetBehaviorGroup<AlarmedBehaviorGroup>().AddAlarmFactor(2f, g.GetWorldPosition());
                    g.SetAlarmState((Agent.AIStateFlag)2);   // PatrollingCautious — the missing native pairing that makes guards converge/aggro
                    n++;
                }
                catch { }
            }
            Log("alarm raised on " + n + " guards");
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            if (affectedAgent != null) _aliveGuardAgents.Remove(affectedAgent);
            if (affectedAgent != null && affectedAgent == CurrentCellmate) { CellmateDead = true; CellmateFollowing = false; Log("cellmate cut down in the pit -> sworn companion lost"); }
            if (_won || _routed || !_hadGuards) return;
            // Win: every enemy guard is down (these dungeon scenes have no exit passage to reach).
            bool anyEnemyLeft = Mission.Agents.Any(a => a != null && a.IsHuman && a.IsActive()
                && a.Team == Mission.PlayerEnemyTeam);
            if (!anyEnemyLeft)
            {
                _won = true;
                Log("all guards down -> WIN");
                Mission.EndMission();
            }
        }

        public override InquiryData OnEndMissionRequest(out bool canLeave)
        {
            canLeave = Agent.Main == null || !Agent.Main.IsActive();
            return null;
        }

        public void OnStealthFailed(OnStealthMissionCounterFailedEvent obj)
        {
            _failedByStealth = true;
            Log("stealth alarm sustained -> caught");
        }

        protected override void OnEndMission()
        {
            CurrentCellmate = null;
            CellmateFollowing = false;
            StopAmbient();
            try { CloseLockpick(); } catch { }
            try { if (_promptLayer != null) { var s = ScreenManager.TopScreen as MissionScreen; if (s != null) s.RemoveLayer(_promptLayer); _promptLayer = null; } } catch { }
            try { if (_alarmLayer != null) { var s = ScreenManager.TopScreen as MissionScreen; if (s != null) s.RemoveLayer(_alarmLayer); _alarmLayer = null; } } catch { }
            try { if (_escapeHudLayer != null) { var s = ScreenManager.TopScreen as MissionScreen; if (s != null) s.RemoveLayer(_escapeHudLayer); _escapeHudLayer = null; } } catch { }
            try { if (_brandLayer != null) { var s = ScreenManager.TopScreen as MissionScreen; if (s != null) s.RemoveLayer(_brandLayer); _brandLayer = null; } } catch { }
            try { Game.Current.EventManager.UnregisterEvent<OnStealthMissionCounterFailedEvent>(OnStealthFailed); } catch { }
            BanditJailbreakLauncher.RestoreSettlementContext();
            if (_routed) return;
            _routed = true;
            var bp = BanditCaptivityBehavior.Instance;
            if (bp == null) { Log("no behavior to route to"); return; }
            if (_won)
            {
                try { ComputeEscapeReward(out int rRen, out int rGold, out bool rGhost, out _, out _); RewardRenown = rRen; RewardGold = rGold; RewardGhost = rGhost; }
                catch (Exception ex) { Log("reward calc fail: " + ex.Message); RewardRenown = 0; RewardGold = 0; }
                bp.OnJailbreakSucceeded();
            }
            else bp.OnJailbreakFailed();
        }

        // Wave 4.38 — free the OTHER caged captives: nearest unfreed prisoner within reach, then [F] frees it.
        private Agent NearestFreeablePrisoner()
        {
            if (Agent.Main == null) return null;
            var mp = Agent.Main.Position;
            float bestSq = 6.25f; Agent best = null;   // within ~2.5m
            for (int i = 0; i < _prisonerAgents.Count; i++)
            {
                var p = _prisonerAgents[i];
                if (p == null || !p.IsActive()) continue;
                float d = p.Position.DistanceSquared(mp);
                if (d < bestSq) { bestSq = d; best = p; }
            }
            return best;
        }

        private void FreePrisoner(Agent p)
        {
            if (p == null) return;
            _prisonerAgents.Remove(p);
            _freedAgents.Add(p);
            _prisonersFreed++;
            try { p.AgentVisuals?.SetContourColor(FreedContourColor, true); } catch { }
            _detection = Math.Min(1f, _detection + 0.15f);   // rattling the cage open makes noise — guards may hear
            try { PlayClip("event:/ui/notification/quest_finished"); } catch { }
            Log("freed prisoner -> " + _prisonersFreed);
        }

        // Wave 4.38 — recover your confiscated gear at the stash: re-equip the saved loadout + draw weapons.
        private void RecoverGear()
        {
            try
            {
                if (_savedGear != null && Agent.Main != null && Agent.Main.IsActive())
                {
                    Agent.Main.UpdateSpawnEquipmentAndRefreshVisuals(new TaleWorlds.Core.Equipment(_savedGear));
                    try { Agent.Main.WieldInitialWeapons(Agent.WeaponWieldActionType.InstantAfterPickUp); } catch { }
                }
            }
            catch (Exception ex) { Log("recover gear fail: " + ex.GetType().Name + ": " + ex.Message); }
            try { TriggerToast(BandItPlus.Localization.Get("bp_jailbreakmissioncontroller_029", "Gear recovered")); } catch { }
            Log("gear recovered");
        }

        // Compute the clean-escape reward + the flourish stat/reward lines from the run's stats. Deterministic,
        // so it serves both the on-screen flourish and the campaign-side reward handoff.
        private void ComputeEscapeReward(out int renown, out int gold, out bool ghost, out string statsLine, out string rewardLine)
        {
            ghost = !_everSpotted;
            int guardsEvaded = _guardsTotal;
            renown = 6 + _difficulty * 2 + guardsEvaded + _prisonersFreed * 3;
            gold = 120 + _difficulty * 40 + guardsEvaded * 25 + _prisonersFreed * 35;
            if (ghost) { renown = (int)(renown * 1.5f); gold = (int)(gold * 1.5f); }
            int t = (int)_escapeElapsed;
            string mmss = (t / 60) + ":" + (t % 60).ToString("00");
            string freedBit = _prisonersFreed > 0 ? "   ·   Freed  " + _prisonersFreed : "";
            statsLine = "Time  " + mmss + "   ·   Evaded  " + guardsEvaded + freedBit + (ghost ? "   ·   Ghost — never seen" : "");
            rewardLine = "+" + renown + " Renown      ·      +" + gold + " Gold";
        }

        internal static void Log(string m)
        {
            try { HideoutPeacefulVisitState.Log("[BP-Jailbreak] " + m); } catch { }
        }
    }
}
