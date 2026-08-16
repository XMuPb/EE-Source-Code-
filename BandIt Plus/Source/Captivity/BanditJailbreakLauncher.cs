using System;
using System.Collections.Generic;
using System.Linq;
using Helpers;                                          // CharacterHelper
using SandBox;                                          // SandBoxMissions, SandBoxManager, SandBox mission behaviors
using SandBox.Missions;                                 // StealthFailCounterMissionLogic, StealthAreaMissionLogic, StealthPatrolPointMissionLogic
using SandBox.Missions.MissionLogics;                   // MissionAgentHandler
using TaleWorlds.CampaignSystem;                        // Hero, Game, Campaign, CharacterObject, CharacterHelper, CultureObject
using TaleWorlds.CampaignSystem.AgentOrigins;           // SimpleAgentOrigin
using TaleWorlds.CampaignSystem.Party;                  // MobileParty
using TaleWorlds.CampaignSystem.Settlements;            // Settlement
using TaleWorlds.CampaignSystem.Settlements.Locations;  // Location, LocationCharacter, AddBehaviorsDelegate, CharacterRelations
using TaleWorlds.Core;                                  // Equipment, ActionSetCode, DecalAtlasGroup
using TaleWorlds.Library;                               // MBRandom
using TaleWorlds.Localization;                          // TextObject
using TaleWorlds.MountAndBlade;                         // MissionState, Mission, MissionBehavior, AgentData, MissionInitializerRecord, behaviors
using TaleWorlds.MountAndBlade.View;                    // ViewCreator (mission UI views: HUD/Esc/Leave)
using BandItPlus.HideoutVisit;

namespace BandItPlus.Captivity
{
    // Wave 4.32 — opens the player-as-prisoner jailbreak mission from the captivity jail menu.
    // Clones SandBox's OpenPrisonBreakMission (see jailbreak-native-reference.md), substituting our
    // controller + a hideout-safe location logic, and building a standalone Location of stealth guards.
    public static class BanditJailbreakLauncher
    {
        // TEMP scouting aid: skip guard spawning so the player can freely walk the cell block and read the
        // escape-point coords off the on-screen "pos X, Y, Z" readout. Flip back to false to restore guards.
        private const bool DisableGuardsForScouting = false;   // guards ON (re-enabled after scouting/HUD testing)

        // 5 (easy) .. 9 (hard)
        public static int GuardCount(int difficulty) => 4 + Math.Max(1, Math.Min(5, difficulty));
        // 16s (easy) .. 8s (hard) once the alarm is raised
        public static float FailSeconds(int difficulty) => 18f - 2f * Math.Max(1, Math.Min(5, difficulty));

        // Borrow a native dungeon-stealth scene; empire is the safe default (44 settlements use it).
        public static string ResolveScene(MobileParty captor)
        {
            string c = (captor?.ActualClan?.Culture ?? captor?.LeaderHero?.Culture)?.StringId ?? "empire";
            switch (c)
            {
                case "aserai": return "aserai_dungeon_stealth";
                case "battania": return "battania_dungeon_stealth";
                case "khuzait": return "khuzait_dungeon_stealth";
                case "sturgia": return "sturgia_dungeon_stealth";
                case "vlandia": return "vlandia_dungeon_stealth";
                default: return "empire_dungeon_stealth";
            }
        }

        public static void Open(MobileParty captor, Settlement hideout, int difficulty)
        {
            try
            {
                InstallTrapOnce();
                string scene = ResolveScene(captor);
                int guards = GuardCount(difficulty);
                // Resolve the captor's BANDIT culture. ActualClan covers custom bandit clans; Party.Culture
                // covers vanilla looters/forest_bandits (no clan/leader). NEVER fall back to the player's
                // kingdom culture — that spawned civilian-dressed recruits. Default to looters.
                var culture = captor?.ActualClan?.Culture ?? captor?.Party?.Culture ?? captor?.LeaderHero?.Culture
                              ?? Game.Current.ObjectManager.GetObject<CultureObject>("looters");
                Log("opening jailbreak scene=" + scene + " guards=" + guards + " failSec=" + FailSeconds(difficulty));

                // MissionAgentHandler.EarlyStart() derefs Settlement.CurrentSettlement (null for a hideout-
                // launched mission) -> NRE. Provide the hideout as CurrentSettlement for the mission's
                // lifetime via the MainParty backing field (no AddMobileParty/teleport/SettlementEntered
                // side effects). Restored in BpJailbreakMissionController.OnEndMission.
                SetSettlementContext(hideout);

                Location loc = BuildJailbreakLocation(scene, culture, guards);

                // Runtime upgrade-level mask switching is empirically impossible here (mask never changes), so load
                // prison_break (baked OPEN doorway) and place a real closed-door mesh we CAN remove on lockpick.
                var rec = SandBoxMissions.CreateSandBoxMissionInitializerRecord(scene, "prison_break", true, (TaleWorlds.Engine.DecalAtlasGroup)3);
                rec.DisableCorpseFadeOut = true;

                Mission mission = MissionState.OpenNew("BpJailbreak", rec, (Mission m) => new MissionBehavior[]
                {
                    new TaleWorlds.MountAndBlade.Source.Missions.MissionOptionsComponent(),
                    new CampaignMissionComponent(),
                    new MissionBasicTeamLogic(),
                    new BasicLeaveMissionLogic(),
                    new LeaveMissionLogic(),
                    new SandBoxMissionHandler(),
                    new BattleAgentLogic(),
                    new MissionAgentLookHandler(),
                    new MissionAgentHandler(),
                    new StealthAreaMissionLogic(),
                    new BpJailbreakLocationLogic(loc),              // replaces MissionLocationLogic (hideout-safe)
                    new HeroSkillHandler(),
                    new AgentHumanAILogic(),
                    new MissionCrimeHandler(),
                    new TaleWorlds.MountAndBlade.Source.Missions.Handlers.MissionFacialAnimationHandler(),
                    new LocationItemSpawnHandler(),
                    new BpJailbreakMissionController(difficulty, captor?.Name?.ToString()),   // replaces PrisonBreakMissionController
                    new CorpseDraggingMissionLogic(),
                    new StealthFailCounterMissionLogic(),
                    new VisualTrackerMissionBehavior(),
                    new EquipmentControllerLeaveLogic(),
                    new BattleSurgeonLogic(),
                    new StealthPatrolPointMissionLogic(),

                    // Mission UI: name "BpJailbreak" matches no [ViewMethod], so attach the standard
                    // single-player views directly (they are MissionView : MissionBehavior).
                    ViewCreator.CreateMissionSingleplayerEscapeMenu(TaleWorlds.CampaignSystem.CampaignOptions.IsIronmanMode), // Esc menu + Options entry
                    ViewCreator.CreateOptionsUIHandler(),                       // Options panel
                    ViewCreator.CreateMissionAgentStatusUIHandler(m),          // bottom HUD (health/crosshair)
                    ViewCreator.CreateMissionLeaveView(),                      // Tab-to-leave
                    // Native "spotted" alarm-meter view pending exact decompiled type (research mis-named the creator).
                    // Detection LOGIC already works: StealthFailCounterMissionLogic (above) + guards' AlarmedBehaviorGroup
                    // (AddStealthAgentBehaviors) raise AlarmFactor by vision; gated on MissionMode.Stealth (set in controller).
                    new SandBox.Conversation.MissionLogics.MissionConversationLogic(),     // enables the cellmate talk screen
                    new SandBox.View.Missions.MissionConversationCameraView(),             // frames the conversation camera
                    SandBox.View.SandBoxViewCreator.CreateMissionConversationView(m),      // renders the dialog UI
                    new BpGuardAlertMissionView(),                                          // over-guard "?"/"!" awareness markers
                }, true, true);
                if (mission != null) mission.ForceNoFriendlyFire = true;
                Log("MissionState.OpenNew returned " + (mission == null ? "null" : "ok"));
            }
            catch (Exception ex)
            {
                Log("Open EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                RestoreSettlementContext();
                BanditCaptivityBehavior.Instance?.OnJailbreakAborted();
            }
        }

        // ── Settlement context (fix: MissionAgentHandler.EarlyStart NRE on null Settlement.CurrentSettlement) ──
        private static System.Reflection.FieldInfo _curSettlementField;
        private static System.Reflection.FieldInfo CurSettlementField =>
            _curSettlementField ?? (_curSettlementField = typeof(MobileParty).GetField(
                "_currentSettlement", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        private static Settlement _savedCurrentSettlement;
        private static bool _contextSet;

        private static void SetSettlementContext(Settlement s)
        {
            try
            {
                var f = CurSettlementField;
                if (f == null || s == null) { Log("SetSettlementContext: field/settlement null (f=" + (f != null) + " s=" + (s != null) + ")"); return; }
                _savedCurrentSettlement = f.GetValue(MobileParty.MainParty) as Settlement;
                f.SetValue(MobileParty.MainParty, s);
                _contextSet = true;
                Log("settlement context set -> " + s.StringId + " (was " + (_savedCurrentSettlement?.StringId ?? "null") + ")");
            }
            catch (Exception ex) { Log("SetSettlementContext fail: " + ex.Message); }
        }

        public static void RestoreSettlementContext()
        {
            if (!_contextSet) return;
            try
            {
                var f = CurSettlementField;
                if (f != null) f.SetValue(MobileParty.MainParty, _savedCurrentSettlement);
                Log("settlement context restored -> " + (_savedCurrentSettlement?.StringId ?? "null"));
            }
            catch (Exception ex) { Log("RestoreSettlementContext fail: " + ex.Message); }
            finally { _contextSet = false; _savedCurrentSettlement = null; }
        }

        // Standalone Location (locationComplex:null is safe — only HERO chars touch the complex).
        private static Location BuildJailbreakLocation(string scene, CultureObject culture, int guardCount)
        {
            var loc = new Location("bp_jailbreak_cell",
                new TextObject("{=bp_jailbreaklauncher_001}Cell"), new TextObject("{=bp_jailbreaklauncher_002}Cell Door"),
                0, true, false, null, null, null, null,
                new string[] { scene, scene, scene, scene }, null);
            int made = 0;
            if (!DisableGuardsForScouting)
            {
                for (int i = 0; i < guardCount; i++)
                {
                    var g = CreateGuard(culture);
                    if (g != null) { loc.AddCharacter(g); made++; }
                }
            }
            // CS0162 is expected: DisableGuardsForScouting is a compile-time const
            // (false), so this branch is unreachable in a normal build. It is a
            // deliberate scouting switch flipped while authoring the jail scene —
            // suppressed rather than deleted.
#pragma warning disable CS0162
            else Log("SCOUTING MODE: guards disabled — walk to the exit spot and read the pos readout");
#pragma warning restore CS0162
            // Ambient captives — other prisoners idling in the cells (atmosphere). Friendly + stationary, so
            // they never fight, never trip the spotting alarm, never count as escape targets. The cell spawn-
            // point entity drives each captive pose (so the action-set suffix stays the vanilla "_guard").
            string[] prisonerSpawns =
            {
                "sp_dungeon_prisoner_idle_exhausted",
                "sp_dungeon_prisnoer_sitting_ground",   // native scene misspelling — kept verbatim
                "sp_dungeon_prisnoer_leaning_wall_b",
                "sp_dungeon_prisnoer_holding_cage",
            };
            int prisoners = 0;
            foreach (var tag in prisonerSpawns)
            {
                var p = CreatePrisoner(tag);
                if (p != null) { loc.AddCharacter(p); prisoners++; }
            }
            Log("built jailbreak Location with " + made + "/" + guardCount + " guards + " + prisoners + " prisoners");
            return loc;
        }

        // Mirrors native CreatePrisonBreakGuard/GetPrisonGuardAgentData, using the captor culture's
        // BasicTroop tier 2-3 instead of Settlement.CurrentSettlement.Owner.Culture (null at a hideout).
        private static LocationCharacter CreateGuard(CultureObject culture)
        {
            try
            {
                var basicTroop = culture?.BasicTroop
                    ?? Game.Current.ObjectManager.GetObject<CharacterObject>("looter");
                var tree = CharacterHelper.GetTroopTree(basicTroop, 2f, 3f).ToList();
                if (tree.Count == 0) return null;
                var melee = tree.Where(x => !x.IsRanged).ToList();
                var pool = melee.Count > 0 ? melee : tree;
                var troop = pool[MBRandom.RandomInt(pool.Count)];

                var eq = (troop.FirstBattleEquipment ?? troop.Equipment).Clone(false); // BATTLE gear + weapons (not civilian)
                var agentData = new AgentData(new SimpleAgentOrigin(troop, -1, null, default(UniqueTroopDescriptor)))
                    .Equipment(eq).NoHorses(true);
                Campaign.Current.Models.AgeModel.GetAgeLimitForLocation(troop, out int min, out int max, "");
                if (max > min) agentData.Age(MBRandom.RandomInt(min, max));

                var abm = SandBoxManager.Instance.AgentBehaviorManager;
                return new LocationCharacter(agentData,
                    new LocationCharacter.AddBehaviorsDelegate(abm.AddStealthAgentBehaviors), // prison_break has patrol points -> patrolling guards
                    "stealth_agent", true, (LocationCharacter.CharacterRelations)2,
                    ActionSetCode.GenerateActionSetNameWithSuffix(agentData.AgentMonster, agentData.AgentIsFemale, "_guard"),
                    false, false, null, false, false, true, null, false);
            }
            catch (Exception ex) { Log("CreateGuard fail: " + ex.GetType().Name + ": " + ex.Message); return null; }
        }

        // Captives are commoners grabbed from across Calradia, so vary the culture for visual variety.
        private static readonly string[] _captiveCultures = { "empire", "aserai", "battania", "khuzait", "sturgia", "vlandia" };

        // One ambient captive at a cell spawn-point (which drives the idle/sitting/leaning pose). Friendly +
        // stationary villager — verified recipe: AddFixedCharacterBehaviors, CharacterRelations 1 (Friendly),
        // civilian equipment, "_guard" action suffix (the captive pose comes from the spawn entity, not here).
        private static LocationCharacter CreatePrisoner(string spawnTag)
        {
            try
            {
                string cid = _captiveCultures[MBRandom.RandomInt(_captiveCultures.Length)];
                var co = Game.Current.ObjectManager.GetObject<CharacterObject>("villager_" + cid)
                         ?? Game.Current.ObjectManager.GetObject<CharacterObject>("villager_empire");
                if (co == null) return null;
                var eq = (co.FirstCivilianEquipment ?? co.Equipment).Clone(true); // true = clone WITHOUT weapons — captives are unarmed
                var data = new AgentData(new SimpleAgentOrigin(co, -1, null, default(UniqueTroopDescriptor))).Equipment(eq).NoHorses(true);
                Campaign.Current.Models.AgeModel.GetAgeLimitForLocation(co, out int min, out int max, "");
                if (max > min) data.Age(MBRandom.RandomInt(min, max));

                var abm = SandBoxManager.Instance.AgentBehaviorManager;
                return new LocationCharacter(data,
                    new LocationCharacter.AddBehaviorsDelegate(abm.AddFixedCharacterBehaviors), // stationary, non-fighting
                    spawnTag, true, (LocationCharacter.CharacterRelations)1,                     // Friendly
                    ActionSetCode.GenerateActionSetNameWithSuffix(data.AgentMonster, data.AgentIsFemale, "_guard"),
                    true, false, null, false, false, true, null, false);
            }
            catch (Exception ex) { Log("CreatePrisoner fail: " + ex.GetType().Name + ": " + ex.Message); return null; }
        }

        // First-run instrumentation: the Location-less mission open is the biggest unknown.
        private static bool _trapInstalled;
        private static void InstallTrapOnce()
        {
            if (_trapInstalled) return;
            _trapInstalled = true;
            AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
            {
                try { Log("[FirstChance] " + e.Exception.GetType().Name + ": " + e.Exception.Message); } catch { }
            };
        }

        internal static void Log(string m) => BpJailbreakMissionController.Log(m);
    }
}
