using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace BandItPlus.HideoutVisit
{
    // Minimal MissionLogic for peaceful hideout walk-around (Wave 3c.4 Phase 1).
    //
    // Bannerlord runs this on every mission tick after our Mission opens. We don't spawn
    // enemies or combat AI — the scene is loaded "naked" and the player walks around.
    //
    // The base class MissionLogic gives us hooks for OnBehaviorInitialize, AfterStart,
    // OnEndMission, etc. For Phase 1 we keep behavior minimal:
    //   - Log lifecycle events so we can see what happens
    //   - On end, ensure the peace flag is reset (also handled by Mission.EndMission Harmony patch)
    public class HideoutVisitMissionLogic : MissionLogic
    {
        public override void OnBehaviorInitialize()
        {
            base.OnBehaviorInitialize();
            HideoutPeacefulVisitState.Log("MissionLogic.OnBehaviorInitialize: peaceful walk-around mission active");

            // Phase 4.5.6 fix: explicitly set MissionMode to StartUp (non-combat).
            // Default Mode is Battle, which causes the engine to run combat-tick logic on every frame
            // (formation updates, enemy AI evaluation, etc). Without combat infrastructure, those
            // tick paths NRE when the player moves. StartUp is the proper mode for walk-around scenes.
            try
            {
                if (Mission != null)
                {
                    Mission.SetMissionMode(MissionMode.StartUp, true);
                    HideoutPeacefulVisitState.Log("MissionLogic: SetMissionMode(StartUp, atStart=true) — combat-tick logic suppressed");
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("MissionLogic: SetMissionMode failed — " + ex.Message);
            }
        }

        public override void AfterStart()
        {
            base.AfterStart();
            try
            {
                HideoutPeacefulVisitState.Log("MissionLogic.AfterStart: scene=" +
                    (Mission?.SceneName ?? "null") +
                    ", agents=" + (Mission?.Agents?.Count ?? -1) +
                    ", PlayerTeam=" + (Mission?.PlayerTeam == null ? "null" : "set") +
                    ", MainAgent=" + (Mission?.MainAgent == null ? "null" : "set"));

                SpawnPlayerAgent();
                // NPCs DISABLED — see Phase 4.5 architectural decision.
                // 3 attempts failed (uninit, full-init, full-init+AI) — vanilla NPCs in custom missions
                // require ~15+ MissionBehaviors (MissionAgentSpawnLogic, LocationCharactersBuilder, etc.)
                // we don't have. Reaching parity with vanilla town-walking is a multi-session research
                // project. The walkable hideout works WITHOUT in-scene NPCs; all interactions remain
                // available via the bp_hideout_camp menu (Talk/Trade/Armory/Doctor/Recruit).
                // SpawnNpcs();
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("AfterStart EXCEPTION: " + ex.GetType().Name + ": " + ex.Message + " | stack=" + ex.StackTrace);
            }
        }

        // Phase 2.1: spawn NPCs at vanilla scene-designer spawn points (sp_common, sp_npc_idle, etc.)
        // Goal for this iteration: visual confirmation — NPCs visible in the scene, on player's team
        // (so they don't attack), no AI or interaction yet. F-key interaction comes in Phase 2.2.
        private void SpawnNpcs()
        {
            HideoutPeacefulVisitState.Log("SpawnNpcs: ENTRY");
            try
            {
                if (Mission?.Scene == null) { HideoutPeacefulVisitState.Log("SpawnNpcs: BAIL — no Mission/Scene"); return; }

                // Use the hideout's own culture for visual variety. Falls back to looter if not found.
                var hideout = HideoutPeacefulVisitState.TargetHideout;
                var cultureId = hideout?.Culture?.StringId;
                CharacterObject npcChar = null;

                // Try a culture-specific basic troop first; common naming in our mod is "<culture>_recruit" or similar
                if (!string.IsNullOrEmpty(cultureId))
                {
                    foreach (var c in MBObjectManager.Instance.GetObjectTypeList<CharacterObject>())
                    {
                        if (c == null || c.Culture?.StringId != cultureId) continue;
                        if (c.IsHero) continue;
                        // Pick the first non-hero from the culture as our NPC visual
                        npcChar = c;
                        break;
                    }
                }

                // Fallback to vanilla looter
                if (npcChar == null)
                {
                    npcChar = MBObjectManager.Instance.GetObject<CharacterObject>("looter");
                }
                if (npcChar == null)
                {
                    HideoutPeacefulVisitState.Log("SpawnNpcs: BAIL — no usable CharacterObject");
                    return;
                }
                HideoutPeacefulVisitState.Log("SpawnNpcs: using character='" + npcChar.StringId + "' culture='" + (npcChar.Culture?.StringId ?? "?") + "'");

                // Spawn at known vanilla NPC spawn entities (discovered via Phase 4.5.4 entity scan)
                string[] npcSpots = {
                    "sp_common",
                    "sp_npc_idle",
                    "sp_npc_hangout_set",
                    "sp_guard_patrol_simple",
                    "sp_npc_lookout_point",
                    "sp_npc_gossip_set",
                };

                int spawned = 0;
                int seed = 0;
                foreach (var spotName in npcSpots)
                {
                    var ent = Mission.Scene.FindEntityWithName(spotName);
                    if (ent == null) continue;
                    var frame = ent.GetGlobalFrame();
                    var pos = frame.origin;
                    var dir = frame.rotation.f.AsVec2.Normalized();
                    try
                    {
                        // Phase 2.2 — same proper-init pattern that fixed the player agent:
                        // explicit Equipment + BodyProperties + IsFemale, plus AIControlled.
                        // Without these, NPCs would have unresolved mesh-skinning and crash on tick.
                        var npcEquipment = npcChar.RandomBattleEquipment ?? npcChar.Equipment;
                        var npcBody = npcChar.GetBodyProperties(npcEquipment, seed++);

                        var data = new AgentBuildData(npcChar)
                            .Equipment(npcEquipment)
                            .BodyProperties(npcBody)
                            .IsFemale(npcChar.IsFemale)
                            .InitialPosition(in pos)
                            .InitialDirection(in dir)
                            .NoHorses(true)
                            .Controller(AgentControllerType.AI);
                        if (Mission.PlayerTeam != null) data = data.Team(Mission.PlayerTeam);

                        var npcAgent = Mission.SpawnAgent(data, false);
                        if (npcAgent != null)
                        {
                            spawned++;
                            HideoutPeacefulVisitState.Log("SpawnNpcs: spawned at '" + spotName + "' pos=" + pos);
                        }
                    }
                    catch (Exception spawnEx)
                    {
                        HideoutPeacefulVisitState.Log("SpawnNpcs: spawn at '" + spotName + "' failed: " + spawnEx.Message);
                    }
                }
                HideoutPeacefulVisitState.Log("SpawnNpcs: spawned " + spawned + " NPCs total");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("SpawnNpcs EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Phase 5 (research-driven): use vanilla SandBox.SandBoxHelpers.MissionHelper.SpawnPlayer
        // instead of hand-rolled spawn — fires player-spawn events, builds AgentBuildData with
        // PartyAgentOrigin/Banner/clothing/mount/wieldInitialWeapons. Falls back to legacy spawn
        // path on reflection failure.
        private void SpawnPlayerAgent()
        {
            HideoutPeacefulVisitState.Log("SpawnPlayerAgent: ENTRY (Phase 5 — using vanilla MissionHelper.SpawnPlayer)");
            try
            {
                if (Mission?.Scene == null) { HideoutPeacefulVisitState.Log("SpawnPlayerAgent: BAIL — no Mission/Scene"); return; }

                // Locate sp_player entity (vanilla scene tag for player spawn position)
                var playerEntity = Mission.Scene.FindEntityWithName("sp_player");
                if (playerEntity == null)
                {
                    // Fall back to scanning by name (some scenes use slightly different names)
                    var entityList = new System.Collections.Generic.List<GameEntity>();
                    Mission.Scene.GetEntities(ref entityList);
                    foreach (var ent in entityList)
                    {
                        if (ent?.Name != null && ent.Name.ToLowerInvariant().Contains("player"))
                        {
                            playerEntity = ent;
                            HideoutPeacefulVisitState.Log("SpawnPlayerAgent: name-scan found '" + ent.Name + "'");
                            break;
                        }
                    }
                }

                if (playerEntity == null)
                {
                    HideoutPeacefulVisitState.Log("SpawnPlayerAgent: no sp_player entity found — falling back to legacy spawn");
                    LegacySpawnPlayer();
                    return;
                }

                // Resolve SandBox.SandBoxHelpers.MissionHelper.SpawnPlayer via reflection.
                // Signature per research: SpawnPlayer(GameEntity playerEntity, bool, bool, bool, bool)
                var helperType = ResolveType("SandBox.SandBoxHelpers.MissionHelper")
                              ?? ResolveType("SandBox.Source.Missions.MissionHelper");
                if (helperType == null)
                {
                    HideoutPeacefulVisitState.Log("SpawnPlayerAgent: MissionHelper type NOT FOUND — falling back to legacy spawn");
                    LegacySpawnPlayer();
                    return;
                }

                System.Reflection.MethodInfo spawnMethod = null;
                foreach (var m in helperType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (m.Name != "SpawnPlayer") continue;
                    var pars = m.GetParameters();
                    if (pars.Length >= 1 && pars[0].ParameterType == typeof(GameEntity)) { spawnMethod = m; break; }
                }
                if (spawnMethod == null)
                {
                    HideoutPeacefulVisitState.Log("SpawnPlayerAgent: SpawnPlayer method NOT FOUND on " + helperType.FullName + " — falling back to legacy spawn");
                    LegacySpawnPlayer();
                    return;
                }

                HideoutPeacefulVisitState.Log("SpawnPlayerAgent: calling " + helperType.FullName + ".SpawnPlayer with " + spawnMethod.GetParameters().Length + " args");
                var args = new object[spawnMethod.GetParameters().Length];
                args[0] = playerEntity;
                for (int i = 1; i < args.Length; i++) args[i] = false;
                spawnMethod.Invoke(null, args);
                HideoutPeacefulVisitState.Log("SpawnPlayerAgent: vanilla SpawnPlayer returned successfully");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("SpawnPlayerAgent EXCEPTION: " + ex.GetType().Name + ": " + ex.Message + " | inner=" + (ex.InnerException?.Message ?? "none") + " | stack=" + ex.StackTrace);
                try { LegacySpawnPlayer(); } catch { }
            }
        }

        private static Type ResolveType(string fullName)
        {
            try
            {
                var t = Type.GetType(fullName);
                if (t != null) return t;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var found = asm.GetType(fullName, false);
                    if (found != null) return found;
                }
            }
            catch { }
            return null;
        }

        // Legacy hand-rolled spawn (Phase 4.5.5) — kept as fallback if vanilla helper unavailable.
        private void LegacySpawnPlayer()
        {
            HideoutPeacefulVisitState.Log("SpawnPlayerAgent: ENTRY");

            if (Hero.MainHero == null)
            {
                HideoutPeacefulVisitState.Log("SpawnPlayerAgent: BAIL — Hero.MainHero is null");
                return;
            }
            if (Mission == null || Mission.Scene == null)
            {
                HideoutPeacefulVisitState.Log("SpawnPlayerAgent: BAIL — Mission or Scene is null");
                return;
            }

            Vec3 spawnPos = Vec3.Zero;
            Vec2 spawnDir = Vec2.Forward;
            GameEntity spawnEntity = null;

            // Phase 4.5.4: scan ALL scene entities by NAME (not tag) for spawn-related keywords.
            // Vanilla hideout scenes name spawn points like "sp_player", "boss_fight_player_pos_0",
            // "spawn_player", etc. — without tags. So FindEntityWithName/scan-by-name is the right API.
            try
            {
                var entityList = new System.Collections.Generic.List<GameEntity>();
                Mission.Scene.GetEntities(ref entityList);
                HideoutPeacefulVisitState.Log("SpawnPlayerAgent: scene has " + entityList.Count + " entities total — scanning by NAME for spawn keywords");

                int matches = 0;
                GameEntity firstSpawnMatch = null;
                GameEntity firstBossPosMatch = null;
                GameEntity firstPlayerMatch = null;

                foreach (var ent in entityList)
                {
                    if (ent == null) continue;
                    var name = ent.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    var lname = name.ToLowerInvariant();

                    bool isSpawn = lname.StartsWith("sp_") || lname.Contains("spawn");
                    bool isBossPos = lname.Contains("boss_fight") || lname.Contains("boss_pos");
                    bool isPlayerNamed = lname.Contains("player") && (lname.Contains("pos") || lname.Contains("spawn") || lname.Contains("start"));

                    if (isSpawn || isBossPos || isPlayerNamed)
                    {
                        HideoutPeacefulVisitState.Log("  SPAWN-CANDIDATE entity name='" + name + "'");
                        matches++;
                        if (firstPlayerMatch == null && lname.Contains("player")) firstPlayerMatch = ent;
                        else if (firstBossPosMatch == null && isBossPos) firstBossPosMatch = ent;
                        else if (firstSpawnMatch == null && isSpawn) firstSpawnMatch = ent;
                        if (matches >= 20) { HideoutPeacefulVisitState.Log("  (truncated spawn-candidate log at 20)"); break; }
                    }
                }

                // Priority: explicit player > boss_fight position > generic spawn
                spawnEntity = firstPlayerMatch ?? firstBossPosMatch ?? firstSpawnMatch;
                HideoutPeacefulVisitState.Log("SpawnPlayerAgent: found " + matches + " spawn-candidate entities; selected: " +
                    (spawnEntity == null ? "NONE" : spawnEntity.Name));
            }
            catch (Exception enumEx) { HideoutPeacefulVisitState.Log("entity scan failed: " + enumEx.Message); }

            // If name-scan failed, also try the original tag-based candidates as a backup.
            if (spawnEntity == null)
            {
                string[] candidateTags = new[] {
                    "sp_player_team_1", "sp_player", "sp_team_1", "spawn_point_player",
                    "hideout_player_spawn", "boss_fight_player_pos", "player_spawn",
                };
                foreach (var tag in candidateTags)
                {
                    spawnEntity = Mission.Scene.FindEntityWithTag(tag);
                    if (spawnEntity != null)
                    {
                        HideoutPeacefulVisitState.Log("SpawnPlayerAgent: matched tag '" + tag + "'");
                        break;
                    }
                }
            }

            if (spawnEntity != null)
            {
                var frame = spawnEntity.GetGlobalFrame();
                spawnPos = frame.origin;
                spawnDir = frame.rotation.f.AsVec2.Normalized();
                HideoutPeacefulVisitState.Log("SpawnPlayerAgent: spawn pos=" + spawnPos + " dir=" + spawnDir);
            }
            else
            {
                // Fallback: try Mission.GetInitialSpawnPath as last resort (vanilla spawn-path concept).
                try
                {
                    var spawnPath = Mission.GetInitialSpawnPath();
                    if (spawnPath != null)
                    {
                        var frame = spawnPath.GetFrameForDistance(0f);
                        spawnPos = frame.origin;
                        spawnDir = frame.rotation.f.AsVec2.Normalized();
                        HideoutPeacefulVisitState.Log("SpawnPlayerAgent: using initial spawn path point pos=" + spawnPos);
                    }
                    else
                    {
                        // Final fallback: ground-snap at origin (still likely lake but better than air).
                        Vec3 testPos = new Vec3(0f, 0f, 100f);
                        float groundZ = Mission.Scene.GetGroundHeightAtPosition(testPos, BodyFlags.CommonCollisionExcludeFlags);
                        spawnPos = new Vec3(0f, 0f, groundZ + 0.1f);
                        HideoutPeacefulVisitState.Log("SpawnPlayerAgent: NO spawn source found — ground-snapped origin pos=" + spawnPos);
                    }
                }
                catch (Exception fallbackEx)
                {
                    HideoutPeacefulVisitState.Log("fallback-spawn failed: " + fallbackEx.Message);
                }
            }

            // Ensure a player team exists. If Mission.PlayerTeam is null we can't pass null to .Team(),
            // so add one defensively.
            var playerTeam = Mission.PlayerTeam;
            if (playerTeam == null)
            {
                try
                {
                    playerTeam = Mission.Teams.Add(BattleSideEnum.Defender, 0, 0, null, true, false, true);
                    Mission.PlayerTeam = playerTeam;
                    HideoutPeacefulVisitState.Log("SpawnPlayerAgent: created PlayerTeam");
                }
                catch (Exception teamEx)
                {
                    HideoutPeacefulVisitState.Log("SpawnPlayerAgent: team creation failed — " + teamEx.Message + " (will try without team)");
                    playerTeam = null;
                }
            }

            // Build the agent and spawn.
            try
            {
                // Phase 4.5.5 fix: explicitly set Equipment, BodyProperties, IsFemale.
                // Without these, the spawned agent has no resolved mesh-skinning data, causing the
                // "pulled clothes" visual glitch AND mission-tick NRE on movement (the engine tries
                // to query equipment/skeleton during physics tick).
                var data = new AgentBuildData(Hero.MainHero.CharacterObject)
                    .Equipment(Hero.MainHero.BattleEquipment)
                    .BodyProperties(Hero.MainHero.BodyProperties)
                    .IsFemale(Hero.MainHero.IsFemale)
                    .InitialPosition(in spawnPos)
                    .InitialDirection(in spawnDir)
                    .NoHorses(true)
                    .Controller(AgentControllerType.Player);
                if (playerTeam != null) data = data.Team(playerTeam);

                HideoutPeacefulVisitState.Log("SpawnPlayerAgent: AgentBuildData built — Equipment=" +
                    (Hero.MainHero.BattleEquipment != null ? "set" : "null") +
                    ", BodyProperties=set, IsFemale=" + Hero.MainHero.IsFemale);

                var agent = Mission.SpawnAgent(data, false);
                if (agent != null)
                {
                    Mission.MainAgent = agent;
                    agent.Controller = AgentControllerType.Player;
                    HideoutPeacefulVisitState.Log("SpawnPlayerAgent: SUCCESS — agent spawned, MainAgent set");
                }
                else
                {
                    HideoutPeacefulVisitState.Log("SpawnPlayerAgent: SpawnAgent returned null");
                }
            }
            catch (Exception spawnEx)
            {
                HideoutPeacefulVisitState.Log("SpawnPlayerAgent: SpawnAgent EXCEPTION — " +
                    spawnEx.GetType().Name + ": " + spawnEx.Message);
            }
        }

        protected override void OnEndMission()
        {
            base.OnEndMission();
            HideoutPeacefulVisitState.Log("MissionLogic.OnEndMission: clearing peace flag");
            HideoutPeacefulVisitState.Reset();
        }
    }
}
