using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.InputSystem;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace BandItPlus.HideoutVisit
{
    // Wave 3c.4 Phase 6 (research-driven, task 7 of 10) — NPCs in walkable hideout scene.
    //
    // Subscribes to CampaignEvents.OnMissionStartedEvent. When a mission starts AND our
    // peaceful flag is set (meaning we're entering a walkable hideout), spawns a small set
    // of culture-appropriate non-hostile bandit NPCs at vanilla scene-designer positions
    // (sp_common, sp_npc_idle, sp_guard_patrol_simple, etc.).
    //
    // Phase 2.1/2.2 (under MissionState.OpenNew) crashed because the minimal mission stack
    // lacked MissionAgentHandler / AgentHumanAILogic etc. Now under OpenVillageMission, the
    // full 27-behavior village stack provides all NPC lifecycle support — same one vanilla
    // uses for villagers. NPCs should not crash this time.
    //
    // SAFETY: defensive try/catch per spawn so one failure doesn't break the rest. Limited to
    // 3 NPCs initially (smaller than Phase 2.1's 6) for a less risky first attempt.
    public class HideoutVisitNpcSpawnBehavior : CampaignBehaviorBase
    {
        // Feature A (Wave 3c.4 polish): make NPCs wander between scene spawn points so the camp
        // visibly moves instead of standing frozen. Each NPC gets an initial destination at spawn,
        // and a periodic tick reassigns destinations when they arrive (or every 10s as a fallback).
        //
        // Wave 3c.4 polish v2 — role assignment. Spawned NPCs are now divided into five jobs so the
        // camp reads as a working settlement instead of a milling crowd:
        //   BOSS     — the hideout chief: highest-tier troop in the culture, stationary at scene center
        //              (or sp_boss/sp_chief/sp_leader/throne if the scene defines one). Tracked
        //              separately in _bossAgent so Feature C can wire dialog to him.
        //   GUARD    — stays at sp_guard_* spawn, weapons drawn, watch state Cautious (alert idle)
        //   TRAINER  — paired with another trainer; both face each other, weapons drawn (sparring stance)
        //   VENDOR   — stationary at sp_common, no weapon, just present (suggests selling/trading)
        //   WANDERER — original wandering behavior (the bulk of the camp)
        // Only WANDERERs are reassigned by the tick loop. Roles are decided once at spawn and frozen.
        private enum NpcRole { Wanderer, Guard, Trainer, Vendor, Boss, Sitter, Sleeper, Captive }
        private readonly List<Agent> _wanderingNpcs = new List<Agent>();
        private readonly List<Vec3> _wanderTargets = new List<Vec3>();
        private readonly Dictionary<Agent, NpcRole> _npcRoles = new Dictionary<Agent, NpcRole>();
        // BUG M7 FIX (2026-04-30): track bodyguard agents separately so the greeter dispatch
        // can exclude them. Bodyguards have NpcRole.Guard for animation purposes but visually
        // belong with the chief — sending one off as a greeter looks wrong.
        private readonly HashSet<Agent> _bodyguards = new HashSet<Agent>();

        // VENDOR TYPING (2026-04-30): vendor[0] = food, vendor[1] = gear. Used by
        // HideoutVendorDialog to route trade dialog to the right inventory pool. Public
        // accessors below let dialog conditions check which vendor is being talked to.
        private Agent _foodVendor;
        private Agent _gearVendor;
        // Wave 4.12 (2026-05-10): slaver vendor — 3rd vendor NPC, gated by chief Tier-3.
        // Spawned in a SEPARATE pass after food/gear because vendorSpots only typically holds
        // 2 entries (food tent + gear tent); slaver position is offset 1.5m from gear vendor.
        private Agent _slaverVendor;
        // Wave 4.12-fix4 (2026-05-11): per-frame dedup flag for TickSlaverInteraction
        // (parallel to _bossInteractionLoggedThisFrame). Prevents repeated trigger if F
        // is held across multiple frames.
        private bool _slaverInteractionLoggedThisFrame;
        public bool IsAgentFoodVendor(Agent ag) { return ag != null && ag == _foodVendor; }
        public bool IsAgentGearVendor(Agent ag) { return ag != null && ag == _gearVendor; }
        public bool IsAgentSlaverVendor(Agent ag) { return ag != null && ag == _slaverVendor; }
        public bool IsAgentDispatchedGuard(Agent ag) { return ag != null && ag == _dispatchedGuard; }
        // Wave 4.6.1: chief dialog from inside the 3D walkable camp uses this predicate to
        // identify the boss NPC. Routes the player into the trust-quest dialog tree via
        // BanditDialogManager's bp_encounter_root state.
        public bool IsAgentBoss(Agent ag) { return ag != null && ag == _bossAgent; }

        // Wave 4.6.5d: side-channel flag set by TickBossInteraction during the SYNCHRONOUS
        // OnAgentInteraction call. Dialog tree evaluation runs inside that call. Without
        // this flag, IsChiefCampConv has no reliable way to identify the boss as the
        // conversation partner because GetLookAgent() returns null during programmatic
        // conversation triggering. Flag is true only during the OnAgentInteraction window.
        private bool _bossConvBeingInitiated;
        public bool IsBossConvBeingInitiated() { return _bossConvBeingInitiated; }

        // Wave 4.6.5d: exposes the boss agent's CharacterObject so dialog conditions can
        // override its display name via ConversationNamePatch without needing a partner
        // reference (since GetLookAgent fails during programmatic triggering).
        public BasicCharacterObject GetBossCharacter() { return _bossAgent?.Character; }

        // INVOKING-DISPATCH WINDOW (2026-04-30): set true ONLY during the OnAgentInteraction
        // call inside TryStartGuardConversation, false otherwise. Used by IsGreeterDispatchInFlight
        // to widen the "greeter wins" window to cover the brief moment when vanilla evaluates
        // dialog conditions AFTER _conversationStarted was already flipped true.
        private bool _invokingDispatch;
        public bool IsCurrentlyInvokingDispatch() { return _invokingDispatch; }
        public void SetInvokingDispatch(bool v) { _invokingDispatch = v; }

        // Returns true while the greeter guard is dispatched but conversation hasn't started yet.
        // HideoutVendorDialog uses this to SUPPRESS its own dialog conditions during the greeter
        // window — otherwise, when the greeter guard reaches the player and OnAgentInteraction
        // fires, both vendor and greeter `start` lines could match and the vendor line could
        // win the race. With this gate, vendor is dormant until greeter conversation completes.
        // Wave 4.11-fix v37 (2026-05-07): require _dispatchedGuard != null to distinguish
        // a REAL in-flight greeter dispatch from a SUPPRESSED one. When the greeter is
        // suppressed on return visits (line 684 sets _hasGreeted=true to prevent
        // re-dispatch), no guard is actually dispatched. The previous formulation
        // `_hasGreeted && !_conversationStarted` returned true for the suppressed case
        // too, which short-circuited ALL vendor/chief dialog conditions for the rest of
        // the mission (they bail at `if (beh.IsGreeterDispatchInFlight()) return false;`).
        // Symptoms: clicking chief/vendor in a return-visit hideout fell through to the
        // campaign-map `bp_encounter_start` line (priority 100, condition IsBpEncounter)
        // which routes to bp_encounter_root — looked like "vendor chat hijacking chief"
        // because the chief tree's trade option felt vendor-themed. Adding the
        // `_dispatchedGuard != null` clause: real dispatch sets the field at L1175ish; a
        // suppressed greeter never sets it; flag now reads correctly in both cases.
        public bool IsGreeterDispatchInFlight() { return _invokingDispatch || (_hasGreeted && !_conversationStarted && _dispatchedGuard != null); }

        // Per-mission roster of every position we've already spawned an NPC at. SpawnOne
        // rejects any candidate position closer than kMinSpawnSepSq to an existing entry —
        // prevents "two NPCs in the same spot" overlap that the user reported. Cleared on
        // mission teardown alongside the other per-mission sets.
        private readonly List<Vec3> _spawnedPositions = new List<Vec3>();
        private const float kMinSpawnSepSq = 1.0f * 1.0f; // 1 m minimum separation
        private Agent _bossAgent; // Feature C will reference this for first-visit dialog
        private float _lastWanderTickTime;
        private int _wanderRoundRobinIdx;
        private float _trainerCombatTickTime; // periodic-strike scheduler for trainer pairs
        private bool _trainerCombatDiagLogged; // diagnostic — log clip-resolution stats once per visit
        private float _ambientGestureTickTime; // periodic ambient gesture scheduler — adds "alive" feel

        // Feature B — greeter intercept. When player walks ≥5m from their spawn position,
        // the boss notices and walks toward them (engine pathfinding via SetScriptedPosition).
        // One-shot per visit. Reset on mission end.
        private bool _hasGreeted;            // Phase 1: guard dispatched
        private float _greeterTickTime;
        private Vec3 _playerSpawnPos;
        private bool _playerSpawnRecorded;
        private Agent _dispatchedGuard;       // the guard who was sent to investigate
        private bool _conversationStarted;    // Phase 2: conversation auto-triggered when guard arrived

        // Per-role spawn-time idle pools — each agent picks ONE random clip from their role's
        // pool at spawn, plays it on Channel 0 (full body) as a cyclic idle that loops natively.
        // Different agents get different clips → camp-wide visual variety. All names verified
        // via grep against vanilla action_sets.xml.
        private static readonly ActionIndexCache[] _bossIdleClips = new[]
        {
            ActionIndexCache.Create("act_command"),         // commanding pose — leader signature
            ActionIndexCache.Create("act_stand_1"),
            ActionIndexCache.Create("act_stand_5"),
            ActionIndexCache.Create("act_stand_staff"),
        };
        private static readonly ActionIndexCache[] _guardIdleClips = new[]
        {
            // ENVIRONMENT-INDEPENDENT idles only. Removed `act_dungeon_prisoner_idle_leaning_wall_cycle`
            // (needs wall directly behind agent — floats when spawned in open ground) and
            // `act_stand_fence_loop` (needs fence in front — leans on air otherwise). Per user
            // bug report showing floating bandits, tightened to clips with no geometry contract.
            ActionIndexCache.Create("act_stand_1"),
            ActionIndexCache.Create("act_stand_2"),
            ActionIndexCache.Create("act_stand_3"),
            ActionIndexCache.Create("act_stand_5"),
            ActionIndexCache.Create("act_stand_6"),
            ActionIndexCache.Create("act_stand_7"),
            ActionIndexCache.Create("act_stand_staff"),
        };
        private static readonly ActionIndexCache[] _vendorIdleClips = new[]
        {
            ActionIndexCache.Create("act_musician_idle_stand_calm"),
            ActionIndexCache.Create("act_musician_idle_stand_cheerful"),
            ActionIndexCache.Create("act_stand_4"),
            ActionIndexCache.Create("act_stand_8"),
            ActionIndexCache.Create("act_stand_9"),
        };

        // Sleep animation — verified in vanilla action_sets.xml. The `_cycle` suffix means
        // the clip loops natively, no anf_keep needed (which would freeze at end-frame).
        // Used by ConfigureSleeper on Channel 0 (full body — sleep IS the whole pose).
        private static readonly ActionIndexCache _animSleepCycle =
            ActionIndexCache.Create("act_dungeon_prisoner_sleep_cycle");
        private static readonly ActionIndexCache _animSleepCycleAlt =
            ActionIndexCache.Create("act_dungeon_prisoner_lay_cycle");

        // Ambient gesture clips — STATIONARY ONLY (no vertical/horizontal displacement).
        // Removed cheer/taunt family (act_cheer_*, act_taunt_cheer_*) — those animations
        // include arm-raise/jump motions with anf_displace_position-style root motion that
        // launched bandits into the sky in our spawn context. Per user bug report showing
        // a bandit floating high above the camp, we now only use contained-gesture clips.
        // Conversations and arguments stay rooted to spawn position — perfect for ambient.
        private static readonly ActionIndexCache[] _ambientGestureClips = new[]
        {
            // STRICT: only stationary in-place clips with no scene-context dependency.
            // REMOVED 2026-04-30 after user bug report (NPC floating mid-air):
            //   - act_conversation_warrior_loop / normal_loop — these have torso lean
            //     offsets that may displace pelvis vertically when played outside a
            //     conversation pair context
            //   - act_argue_trio_middle / _left / _right — designed for 3 specific
            //     positions around a table/fire; played solo they produce floating
            //   - act_argue_group_1_1 / _2_1 — same: expect specific group geometry
            // Kept: pure argue family (single-person torso/arm gestures, no offsets).
            ActionIndexCache.Create("act_argue"),
            ActionIndexCache.Create("act_argue_2"),
            ActionIndexCache.Create("act_argue_3"),
            ActionIndexCache.Create("act_argue_4"),
            ActionIndexCache.Create("act_argue_soft_1"),
            ActionIndexCache.Create("act_argue_soft_2"),
        };

        // Active swing clip names — VERIFIED to exist by grep against vanilla action_sets.xml
        // in this Bannerlord 1.2.x install. Vanilla pattern is `act_quick_release_<motion>_<weapon>`
        // for active-phase swings (the moment the weapon strikes through). Two main motions:
        //   overswing — overhead chop/slam (top→down)
        //   slashleft / slashright — horizontal swings
        // Played on Channel 1 with ignorePriority:true so the warrior-idle on Channel 0 keeps
        // animating the legs/torso. Mix of weapon classes so at least one resolves regardless
        // of what specific bandit equipment is wielded.
        private static readonly ActionIndexCache[] _trainerCombatClips = new[]
        {
            ActionIndexCache.Create("act_quick_release_overswing_1h"),
            ActionIndexCache.Create("act_quick_release_overswing_2h"),
            ActionIndexCache.Create("act_quick_release_overswing_spear"),
            ActionIndexCache.Create("act_quick_release_overswing_staff"),
            ActionIndexCache.Create("act_quick_release_overswing_flail"),
            ActionIndexCache.Create("act_quick_release_slashleft_1h"),
            ActionIndexCache.Create("act_quick_release_slashright_1h"),
            ActionIndexCache.Create("act_quick_release_slashleft_2h"),
            ActionIndexCache.Create("act_quick_release_slashleft_flail"),
            ActionIndexCache.Create("act_quick_release_slashleft_staff"),
        };

        public override void RegisterEvents()
        {
            CampaignEvents.OnMissionStartedEvent.AddNonSerializedListener(this, OnMissionStarted);
            CampaignEvents.OnMissionEndedEvent.AddNonSerializedListener(this, OnMissionEnded);
            CampaignEvents.MissionTickEvent.AddNonSerializedListener(this, OnMissionTick);
            // BUG H7 FIX (2026-04-30): clear RoleActionSets cache when the campaign session
            // changes. Static Monster clones from a previous session/campaign hold references
            // to disposed MBObjectManager state — re-init forces fresh clones against the
            // current session's `human` Monster. Both new-game and save-load paths covered.
            CampaignEvents.OnNewGameCreatedEvent.AddNonSerializedListener(this,
                (CampaignGameStarter _) => RoleActionSets.Reset());
            CampaignEvents.OnGameLoadedEvent.AddNonSerializedListener(this,
                (CampaignGameStarter _) => RoleActionSets.Reset());
        }

        // BUG M18 FIX (2026-04-30): public predicate for HideoutGreeterDialog to verify the
        // conversation partner is OUR dispatched greeter, not just any agent in peaceful mode.
        // Returns true ONLY when the agent currently in conversation with the player matches
        // _dispatchedGuard (set by TickGreeterIntercept Phase 1). Wrapped in try/catch because
        // Mission.Current is not always available when conditions fire.
        public bool IsCurrentConversationGreeter()
        {
            try
            {
                if (_dispatchedGuard == null) return false;
                var partner = Mission.Current?.MainAgent?.GetLookAgent();
                // Conversation partner is harder to query reliably across versions — fall back
                // to "is _dispatchedGuard active and within conversation distance to player".
                if (!_dispatchedGuard.IsActive()) return false;
                var player = Mission.Current?.MainAgent;
                if (player == null) return false;
                var dSq = _dispatchedGuard.Position.DistanceSquared(player.Position);
                return dSq < 9f; // 3m — comfortable conversation range
            }
            catch { return false; }
        }

        // Public accessor for HideoutAltLabelBehavior (Alt-key nameplate display).
        // Returns only agents that should get alt-key labels: the chief and vendors —
        // the important interactable NPCs. Skips ambient roles (guards/trainers/sitters/
        // wanderers) to keep the label list focused on who matters for player gameplay.
        public IEnumerable<Agent> GetLabelAgents()
        {
            foreach (var kv in _npcRoles)
            {
                if (kv.Value != NpcRole.Boss && kv.Value != NpcRole.Vendor) continue;
                var ag = kv.Key;
                if (ag == null || !ag.IsActive()) continue;
                yield return ag;
            }
        }

        // Public entry point called by the Mission.EndMission Harmony PREFIX in
        // MissionEndReleaseUsablesPatch. Runs BEFORE vanilla's UsableMachine.OnMissionEnded
        // iteration → preempts the AnimationPoint NRE we hit when chair-bound sitters are
        // torn down by vanilla without explicit release first.
        // Idempotent — safe to call multiple times (each StopUsingGameObject is a no-op
        // if the agent isn't currently bound).
        public void ReleaseAllSittersFromChairs()
        {
            int released = 0;
            foreach (var kv in _npcRoles)
            {
                if (kv.Value != NpcRole.Sitter) continue;
                var ag = kv.Key;
                if (ag == null || !ag.IsActive()) continue;
                try
                {
                    ag.StopUsingGameObject(true, Agent.StopUsingGameObjectFlags.None);
                    released++;
                }
                catch (Exception relEx)
                {
                    HideoutPeacefulVisitState.Log("ReleaseAllSittersFromChairs (PREFIX) fail: " + relEx.Message);
                }
            }
            if (released > 0)
                HideoutPeacefulVisitState.Log("ReleaseAllSittersFromChairs (PREFIX): released " + released + " sitter chair-bindings before EndMission body runs");
        }

        public override void SyncData(IDataStore dataStore) { }

        private void OnMissionEnded(IMission mission)
        {
            // BUG H1 FIX (2026-04-30): greeter-state fields MUST be cleared on EVERY mission end,
            // not only when the wander/role collections happen to be populated. The previous gate
            // (`if (_wanderingNpcs.Count > 0 || _wanderTargets.Count > 0 || _npcRoles.Count > 0)`)
            // skipped the entire cleanup block when collections were already empty (e.g. an early
            // mission abort). That left `_dispatchedGuard` / `_hasGreeted` / `_conversationStarted`
            // pointing at destroyed agents, breaking the next hideout's greeter dispatch with NREs.
            // Greeter state now resets unconditionally below; the wander-cleanup block stays gated
            // because it has actual work to do (sitter release, log line) only when populated.
            _hasGreeted = false;
            _greeterTickTime = 0f;
            _playerSpawnRecorded = false;
            _playerSpawnPos = Vec3.Zero;
            _dispatchedGuard = null;
            _conversationStarted = false;
            _foodVendor = null;
            _gearVendor = null;
            _slaverVendor = null;

            // CHAIR-BIND TEARDOWN PROTECTION: ALWAYS unpatch on mission end (idempotent).
            // Critical for stability — if this leaks past mission end, it'll break encyclopedia
            // preview the next time the player views one. Calling on every mission-end (even
            // non-peaceful ones) is safe because Disable() is a no-op when not patched.
            BandItPlus.Patches.PeacefulNoAnimationPointVisibilityPatch.Disable();

            // Clear wander state when a peaceful mission ends so we don't carry stale Agent
            // references into the next mission (those agents are about to be destroyed).
            if (_wanderingNpcs.Count > 0 || _wanderTargets.Count > 0 || _npcRoles.Count > 0)
            {
                HideoutPeacefulVisitState.Log("NpcWander: clearing wander state on mission end (" +
                    _wanderingNpcs.Count + " agents tracked, " + _npcRoles.Count + " roles)");

                // CRASH FIX (mission-exit NRE in AnimationPoint.SetAgentItemsVisibility):
                // Sitters bound to chair UsableMissionObjects via UseGameObject must be
                // explicitly released BEFORE vanilla's UsableMachine.OnMissionEnded iterates
                // and calls StopUsingGameObjectAux. If we don't release first, vanilla's
                // teardown races with agent destruction → NRE in SetAgentItemsVisibility.
                // We're in the CampaignEvents.OnMissionEndedEvent handler which fires during
                // Mission.EndMission BEFORE the UsableMachine iteration → safe to release here.
                int released = 0;
                foreach (var kv in _npcRoles)
                {
                    if (kv.Value != NpcRole.Sitter) continue;
                    var ag = kv.Key;
                    if (ag == null || !ag.IsActive()) continue;
                    try
                    {
                        ag.StopUsingGameObject(true, Agent.StopUsingGameObjectFlags.None);
                        released++;
                    }
                    catch (Exception relEx)
                    {
                        HideoutPeacefulVisitState.Log("StopUsingGameObject sitter fail: " + relEx.Message);
                    }
                }
                if (released > 0)
                    HideoutPeacefulVisitState.Log("NpcSpawn: released " + released + " sitters from chair bindings before mission teardown");

                _wanderingNpcs.Clear();
                _wanderTargets.Clear();
                _npcRoles.Clear();
                _usedNames.Clear();
                _spawnedPositions.Clear();
                _bodyguards.Clear(); // M7
                _bossAgent = null;
                _lastWanderTickTime = 0f;
                _wanderRoundRobinIdx = 0;
                _trainerCombatTickTime = 0f;
                _trainerCombatDiagLogged = false;
                _ambientGestureTickTime = 0f;
                // Greeter state already cleared above (H1 fix). Don't double-clear.
            }
        }

        private void OnMissionTick(float dt)
        {
            // Process any pending vendor trade. Dialog consequence only sets a flag (so the
            // conversation closes cleanly first); we open the actual trade screen here, ONE
            // tick after the dialog ends. Prevents the "stuck in conversation state after
            // trade closes" bug where player can't move post-trade.
            HideoutVendorDialog.TickProcessPendingTrade();

            // Trainer combat tick runs INDEPENDENTLY of wanderer tick — even when no wanderers
            // exist, trainers should still spar. Cheap (one agent per fire, throttled to ~3s).
            TickTrainerCombat(dt);

            // Ambient gesture tick — every ~5s pick a random wanderer/vendor and play a
            // taunt/cheer/argue/converse animation on upper body. Makes the camp feel alive.
            TickAmbientGesture(dt);

            // Greeter intercept (Feature B) — once per visit, when player walks ≥5m from
            // their initial spawn position, the boss walks toward them.
            TickGreeterIntercept(dt);

            // Wave 4.6.5: boss F-press detection. Vanilla F-press doesn't recognize the
            // boss agent as conversation-capable (he's at sp_boss / hideout_boss_fight,
            // not a vanilla interaction-trigger entity). Replicates the greeter pattern:
            // when player is near the boss AND presses F, programmatically open the
            // conversation via SetAsConversationAgent + OnAgentInteraction.
            TickBossInteraction(dt);
            TickSlaverInteraction(dt);  // Wave 4.12-fix4: parallel programmatic conversation trigger for slaver

            // Wave 4.6.7j: removed TickBossSparkle — vanilla golden quest markers don't
            // have particles. Gold glow is now achieved via brush override
            // (GUI/Brushes/BandItPlusQuestMarker.xml with color_factor="1.5").

            // Wave 4.6.7k removed: pulse via brush runtime mod was abandoned (vanilla
            // pulse requires a real StoryModeQuestBase backing). Pulse is now done at
            // the marker provider level via on/off marker visibility flicker. See
            // BanditCampNameMarkerProvider.CreateMarkers (Wave 4.6.7l).

            // Throttle: reassign one NPC's wander destination every 10 seconds (round-robin).
            // Spreads cost over many ticks; with 25 NPCs each gets a new destination every ~250s
            // (4 minutes) — slow enough that NPCs actually walk to each target, not teleport.
            if (_wanderingNpcs.Count == 0 || _wanderTargets.Count == 0) return;

            _lastWanderTickTime += dt;
            if (_lastWanderTickTime < 10f) return;
            _lastWanderTickTime = 0f;

            try
            {
                // BUG L2 FIX (2026-04-30): walk through round-robin entries until a LIVE wanderer
                // is found, instead of returning on the first dead-or-stationary candidate. With
                // the previous return-on-dead behavior, having any dead/non-wanderer agent in
                // _wanderingNpcs starved the next eligible wanderer of a destination for an
                // entire 10s tick interval. Now we skip past dead/stationary entries within
                // ONE tick. Bound the loop at _wanderingNpcs.Count to prevent infinite spin if
                // every entry is dead.
                Agent agent = null;
                int searched = 0;
                while (searched++ < _wanderingNpcs.Count)
                {
                    _wanderRoundRobinIdx = (_wanderRoundRobinIdx + 1) % _wanderingNpcs.Count;
                    var candidate = _wanderingNpcs[_wanderRoundRobinIdx];
                    if (candidate == null || !candidate.IsActive()) continue;
                    // Skip stationary roles — guards hold their post, trainers spar in place,
                    // vendors stay at their stall. Only Wanderers get reassigned destinations.
                    if (_npcRoles.TryGetValue(candidate, out var role) && role != NpcRole.Wanderer) continue;
                    agent = candidate;
                    break;
                }
                if (agent == null) return; // no live wanderer this tick

                // Anti-oscillation: sample 5 random targets, pick the one FARTHEST from agent's
                // current position. Stops the "run to spot, run back to start" behavior the user
                // reported — new destinations are biased away from where the NPC just came from.
                var currentPos = agent.Position;
                Vec3 bestTarget = _wanderTargets[MBRandom.RandomInt(_wanderTargets.Count)];
                float bestDistSq = bestTarget.DistanceSquared(currentPos);
                for (int i = 0; i < 4; i++)
                {
                    var candidate = _wanderTargets[MBRandom.RandomInt(_wanderTargets.Count)];
                    var d = candidate.DistanceSquared(currentPos);
                    if (d > bestDistSq) { bestDistSq = d; bestTarget = candidate; }
                }
                AssignWanderDestination(agent, bestTarget);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("NpcWander tick exception: " + ex.Message);
            }
        }

        // Periodic trainer-combat tick. Every ~3 seconds, picks one random active trainer and
        // plays a one-shot strike or defend animation. The strike clip naturally ends and blends
        // back to the warrior-idle from Path A's action set — no anf_keep, no priority override
        // needed (attack priority 10 already beats idle priority 0). If the trainer is currently
        // mid-strike, the new clip just queues — we don't care about overlapping cleanly.
        private void TickTrainerCombat(float dt)
        {
            _trainerCombatTickTime += dt;
            if (_trainerCombatTickTime < 3f) return;
            _trainerCombatTickTime = 0f;

            if (_npcRoles.Count == 0) return;

            // One-time diagnostic: confirm hypothesis that combat-clip names didn't resolve.
            // Logs how many of the 10 candidate clip names returned valid Index values.
            // Expected: 0 resolved → ActionIndexCache.Create returned act_none for all guessed
            // names → SetActionChannel was a silent no-op the whole time → user saw no swings.
            if (!_trainerCombatDiagLogged)
            {
                _trainerCombatDiagLogged = true;
                int resolved = 0; var firstName = "(none)";
                for (int i = 0; i < _trainerCombatClips.Length; i++)
                {
                    if (_trainerCombatClips[i].Index >= 0)
                    {
                        resolved++;
                        if (firstName == "(none)") firstName = "clip[" + i + "].Index=" + _trainerCombatClips[i].Index;
                    }
                }
                HideoutPeacefulVisitState.Log("TickTrainerCombat DIAG: " + resolved + "/" +
                    _trainerCombatClips.Length + " clip names resolved (first valid: " + firstName + ")");
            }

            // Reservoir-sample one random trainer (no list allocation per tick).
            Agent target = null;
            int seen = 0;
            foreach (var kv in _npcRoles)
            {
                if (kv.Value != NpcRole.Trainer) continue;
                if (kv.Key == null || !kv.Key.IsActive()) continue;
                seen++;
                if (MBRandom.RandomInt(seen) == 0) target = kv.Key;
            }
            if (target == null) return;

            // BREAKTHROUGH FIX (replaces both prior approaches — SetActionChannel-on-channel-0
            // and MovementFlags). Two-part research breakthrough from the user:
            //   1. CHANNEL 1 = upper body / combat (Channel 0 = full body, locks legs → freezes)
            //   2. Combat clip name pattern is `act_usage_<weapon>_<direction>_active` (NOT
            //      act_attack_swing_*). Same family as act_usage_ladder_*, act_usage_batteringram_*
            //      which are confirmed in the static cache.
            // Played on Channel 1 with ignorePriority:true and 0.2s blend lets the warrior-idle
            // (Path A) keep driving Channel 0 / legs, while swing animations layer on top.
            ActionIndexCache clip = ActionIndexCache.act_none;
            for (int i = 0; i < 4; i++)
            {
                var c = _trainerCombatClips[MBRandom.RandomInt(_trainerCombatClips.Length)];
                if (c.Index >= 0) { clip = c; break; }
            }
            if (clip.Index < 0) return; // no candidate name resolved this scene's animation set

            try
            {
                target.SetActionChannel(1, clip,                // Channel 1 = upper body
                    ignorePriority: true,                       // override AI's combat-idle re-assert
                    additionalFlags: (AnimFlags)0,              // no anf_keep — let strike end naturally
                    blendWithNextActionFactor: 0f,
                    actionSpeed: 1f,
                    blendInPeriod: 0.2f,                        // smooth handoff per research
                    blendOutPeriodToNoAnim: 0.2f,
                    startProgress: 0f,
                    useLinearSmoothing: false,
                    blendOutPeriod: 0.2f,
                    actionShift: 0,
                    forceFaceMorphRestart: false);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("TickTrainerCombat fail: " + ex.Message);
            }
        }

        // Periodic ambient gesture — picks one wanderer/vendor every ~5s. If another wanderer
        // is nearby (within 4m), they gesture TOGETHER and turn to face each other — reads as
        // a conversation between two bandits. If alone, solo gesture. Channel 1 (upper body),
        // legs keep their walking idle. Reservoir-sample target, scan-for-partner, render pair.
        private void TickAmbientGesture(float dt)
        {
            _ambientGestureTickTime += dt;
            if (_ambientGestureTickTime < 5f) return;
            _ambientGestureTickTime = 0f;

            if (_npcRoles.Count == 0) return;

            // Step 1: pick first target via reservoir sampling
            Agent target = null;
            int seen = 0;
            foreach (var kv in _npcRoles)
            {
                if (kv.Value != NpcRole.Wanderer && kv.Value != NpcRole.Vendor) continue;
                var ag = kv.Key;
                if (ag == null || !ag.IsActive()) continue;
                seen++;
                if (MBRandom.RandomInt(seen) == 0) target = ag;
            }
            if (target == null) return;

            // Step 2: find nearest wanderer/vendor partner within 4m (skip too-close <1m to
            // avoid clipping pairs). If no partner found in window → solo gesture.
            const float kPartnerMaxSq = 4f * 4f;
            const float kPartnerMinSq = 1f * 1f;
            Agent partner = null;
            float bestSq = kPartnerMaxSq;
            var targetPos = target.Position;
            foreach (var kv in _npcRoles)
            {
                if (kv.Value != NpcRole.Wanderer && kv.Value != NpcRole.Vendor) continue;
                var ag = kv.Key;
                if (ag == null || !ag.IsActive() || ag == target) continue;
                var d = ag.Position.DistanceSquared(targetPos);
                if (d < kPartnerMinSq || d > bestSq) continue;
                bestSq = d; partner = ag;
            }

            var clip = _ambientGestureClips[MBRandom.RandomInt(_ambientGestureClips.Length)];
            if (clip.Index < 0) return;

            PlayGestureOnAgent(target, clip);
            if (partner != null)
            {
                PlayGestureOnAgent(partner, clip);
                FaceEachOther(target, partner); // turn heads toward each other for conversation read
            }
        }

        // Play one upper-body gesture clip with proven safe parameters.
        private static void PlayGestureOnAgent(Agent agent, ActionIndexCache clip)
        {
            try
            {
                agent.SetActionChannel(1, clip,
                    ignorePriority: true,
                    additionalFlags: (AnimFlags)0,
                    blendWithNextActionFactor: 0f,
                    actionSpeed: 1f,
                    blendInPeriod: 0.25f,
                    blendOutPeriodToNoAnim: 0.3f,
                    startProgress: 0f,
                    useLinearSmoothing: false,
                    blendOutPeriod: 0.3f,
                    actionShift: 0,
                    forceFaceMorphRestart: false);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("PlayGestureOnAgent fail: " + ex.Message);
            }
        }

        // Set both agents' LookDirection toward each other so their head/torso turn to face
        // their conversation partner. IsLookDirectionLocked keeps the orientation while the
        // gesture animation plays. The look-direction setter is the lightest touch — no
        // body rotation, no movement — just where they're "looking."
        private static void FaceEachOther(Agent a, Agent b)
        {
            try
            {
                // BUG L1 FIX (2026-04-30): clear any previous lock first. If either agent was
                // a participant in a prior gesture pair, IsLookDirectionLocked stayed true and
                // pointed at the OLD partner. Setting LookDirection while locked is undefined —
                // the engine may ignore the new value. Unlock → set → relock guarantees the
                // new direction takes effect on each gesture refresh.
                try { a.IsLookDirectionLocked = false; } catch { }
                try { b.IsLookDirectionLocked = false; } catch { }

                var aPos = a.Position;
                var bPos = b.Position;
                var ab = bPos - aPos;
                if (ab.LengthSquared < 0.0001f) return; // same spot — can't compute direction
                var dirAB = ab.NormalizedCopy();
                a.LookDirection = dirAB;
                b.LookDirection = -dirAB;
                a.IsLookDirectionLocked = true;
                b.IsLookDirectionLocked = true;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("FaceEachOther fail: " + ex.Message);
            }
        }

        // Greeter intercept tick. Records player's spawn position on first fire, then every
        // 1s checks if player has walked ≥5m from spawn. When yes, the **chief dispatches
        // the nearest guard** to investigate the stranger — guards-first matches camp
        // hierarchy (sentries handle perimeter checks, chief stays on throne until you reach
        // him). One-shot per visit (the _hasGreeted gate). Pass C will replace the popup
        // with a guard-then-chief dialog arc.
        // Two-phase greeter:
        //   Phase 1: when player has walked ≥5m from spawn, find nearest guard, dispatch them
        //            via SetScriptedPosition toward player. Track in _dispatchedGuard.
        //   Phase 2: every 1s thereafter, monitor dispatched guard's distance to player.
        //            When ≤2.5m (speaking distance), auto-trigger conversation.
        private void TickGreeterIntercept(float dt)
        {
            _greeterTickTime += dt;
            // Wave 4.11-fix v24 (2026-05-07): tick reduced from 1s to 0.25s. With 1s tick,
            // the guard's SetScriptedPosition target updated only once per second, so the
            // pursuit visibly lagged the moving player (re-paths and direction snaps every
            // 1s). At 0.25s we update 4× more often → smooth tracking. Also the
            // conversation-trigger window narrows to 0.25s, so the gap during which the
            // player can "pass through" the speaking radius without firing the dialog is
            // 4× smaller. Phase 1 detection (5m walk threshold) is unchanged in behavior;
            // it just runs 4× sooner — harmless.
            if (_greeterTickTime < 0.25f) return;
            _greeterTickTime = 0f;

            // Wave 4.11-fix v26 (2026-05-07): suppress greeter dispatch on return visits.
            // Once the player has met the chief (culture-trust > 0), they're no longer a
            // stranger — the guard shouldn't run up to "investigate" again. Set _hasGreeted
            // = true so Phase 1 short-circuits, and bail before any further work.
            if (!_hasGreeted)
            {
                try
                {
                    var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                    var hideoutSettlement = MobileParty.MainParty?.CurrentSettlement;
                    var cId = hideoutSettlement?.Culture?.StringId;
                    if (bdm != null && !string.IsNullOrEmpty(cId) && bdm.GetCultureTrust(cId) > 0)
                    {
                        {
                            _hasGreeted = true;
                            HideoutPeacefulVisitState.Log(
                                "TickGreeterIntercept: suppressed (player already met chief, trust>0 for "
                                + cId + ")");
                            return;
                        }
                    }
                }
                catch { /* defensive — fall through to vanilla greeter */ }
            }

            var player = Mission.Current?.MainAgent;
            if (player == null || !player.IsActive()) return;

            // Phase 1: dispatch guard
            if (!_hasGreeted)
            {
                if (!_playerSpawnRecorded)
                {
                    _playerSpawnPos = player.Position;
                    _playerSpawnRecorded = true;
                    return;
                }

                var distFromSpawnSq = player.Position.DistanceSquared(_playerSpawnPos);
                if (distFromSpawnSq < 25f) return; // ≥5m threshold

                // Find nearest guard.
                // BUG M7 FIX (2026-04-30): EXCLUDE bodyguards from the greeter pool. Bodyguards
                // are stored with NpcRole.Guard for ActionSet purposes (UnarmedGuardSuffix-flavored
                // idle), but they're staged ±1.5m flanking the chief — sending one off as the
                // greeter abandons the boss visually. The `_bodyguards` HashSet (populated at
                // spawn time, see Pass A0) records which guards are bodyguards; we skip those
                // when picking a greeter. Other guards at sp_guard_* / patrol_point posts are
                // still eligible.
                Agent nearestGuard = null;
                float bestSq = float.MaxValue;
                var playerPos = player.Position;
                foreach (var kv in _npcRoles)
                {
                    if (kv.Value != NpcRole.Guard) continue;
                    var ag = kv.Key;
                    if (ag == null || !ag.IsActive()) continue;
                    if (_bodyguards.Contains(ag)) continue; // M7: skip bodyguards
                    var d = ag.Position.DistanceSquared(playerPos);
                    if (d < bestSq) { bestSq = d; nearestGuard = ag; }
                }
                if (nearestGuard == null) return;

                try
                {
                    var scene = nearestGuard.Mission?.Scene;
                    if (scene == null) return;

                    // CRITICAL: ApplyRoleIdle locked Channel 0 on a stand-pose with
                    // ignorePriority:true. With Channel 0 occupied by a stand-idle, the
                    // engine can't drive locomotion (run/walk animations need Channel 0).
                    // Result: agent slides at 0.95 m/s in stand pose. Releasing Channel 0
                    // by setting act_none lets the locomotion system drive properly.
                    try
                    {
                        nearestGuard.SetActionChannel(0, ActionIndexCache.act_none,
                            ignorePriority: true,
                            additionalFlags: (AnimFlags)0,
                            blendWithNextActionFactor: 0f, actionSpeed: 1f,
                            blendInPeriod: 0.1f, blendOutPeriodToNoAnim: 0.1f,
                            startProgress: 0f, useLinearSmoothing: false,
                            blendOutPeriod: 0.1f, actionShift: 0,
                            forceFaceMorphRestart: false);
                    }
                    catch { }

                    // Absolute speed cap of 6 m/s = run pace (vanilla run is ~5 m/s).
                    // isMultiplier:false so this isn't relative to a confused base.
                    try { nearestGuard.SetMaximumSpeedLimit(6f, false); } catch { }

                    var worldPos = new WorldPosition(scene, UIntPtr.Zero, playerPos, false);
                    nearestGuard.SetScriptedPosition(ref worldPos,
                        addHumanLikeDelay: false,                                  // no delay — go now
                        additionalFlags: Agent.AIScriptedFrameFlags.NoAttack);     // NO DoNotRun → full run

                    InformationManager.DisplayMessage(new InformationMessage(
                        BandItPlus.Localization.Get("bp_hideoutvisitnpcspawnbehavior_001", "The chief orders a guard to investigate the stranger..."), Colors.Yellow));
                    _hasGreeted = true;
                    _dispatchedGuard = nearestGuard;
                    HideoutPeacefulVisitState.Log("TickGreeterIntercept Phase 1: dispatched guard (dist=" +
                        Math.Sqrt(bestSq).ToString("F1") + "m) at run speed, now monitoring for arrival...");
                }
                catch (Exception ex)
                {
                    HideoutPeacefulVisitState.Log("TickGreeterIntercept Phase 1 fail: " + ex.Message);
                }
                return;
            }

            // Phase 2: track moving player + auto-trigger conversation when close.
            // BUG H2 FIX (2026-04-30): The entire phase 2 block is now wrapped in try/catch.
            // Previously `_dispatchedGuard.Position` access threw NRE if the guard was destroyed
            // between Phase 1 dispatch and Phase 2 evaluation (e.g. player attacks the guard, or
            // the agent is removed by a Harmony patch). The narrow inner try/catch only protected
            // the SetScriptedPosition call — the .Position read on line 619 was unprotected. Now
            // any failure cleanly nulls _dispatchedGuard so Phase 1 will pick a fresh greeter.
            if (_conversationStarted) return;
            if (_dispatchedGuard == null || !_dispatchedGuard.IsActive()) return;

            // Wave 4.11-fix v30 (2026-05-07): yield if a conversation is already active.
            // The greeter's Phase 2 was firing TryStartGuardConversation regardless of
            // whether the player was already talking to someone else (vendor, chief).
            // OnAgentInteraction would hijack the active conversation → "guard chat pops
            // in when talking to vendors or the chief". Now we check first: if any
            // conversation is active, the greeter's job is done — release the gate and
            // bail. The player IS being greeted (just by someone else they walked up to).
            try
            {
                var convoChar = Campaign.Current?.ConversationManager?.OneToOneConversationCharacter;
                if (convoChar != null)
                {
                    _conversationStarted = true;
                    HideoutPeacefulVisitState.Log(
                        "TickGreeterIntercept Phase 2: yielding to existing conversation (player already in dialog)");
                    return;
                }
            }
            catch { /* defensive */ }

            // Wave 4.11-fix v32 (2026-05-07): PROXIMITY yield. v30's convo check fails the
            // common race: player walks toward vendor/chief, greeter guard reaches 2.5m of
            // player FIRST, vendor convo hasn't started yet → v30 sees no convo → greeter
            // hijacks before player can click. Solution: also yield if the player is
            // physically near the boss agent or any vendor agent. Greeter's purpose is
            // "ensure the player meets SOMEONE in camp" — if they're standing next to a
            // key NPC, mission is already fulfilled. 3.5m radius covers the typical
            // approach-to-talk window.
            try
            {
                const float kProxYieldSqM = 3.5f * 3.5f; // 12.25 m²
                if (_bossAgent != null && _bossAgent.IsActive())
                {
                    var bossDistSq = _bossAgent.Position.DistanceSquared(player.Position);
                    if (bossDistSq <= kProxYieldSqM)
                    {
                        _conversationStarted = true;
                        HideoutPeacefulVisitState.Log(
                            "TickGreeterIntercept Phase 2: yielding to nearby NPC (player approaching boss at "
                            + System.Math.Sqrt(bossDistSq).ToString("F1") + "m)");
                        return;
                    }
                }
                foreach (var kv in _npcRoles)
                {
                    if (kv.Value != NpcRole.Vendor) continue;
                    var ag = kv.Key;
                    if (ag == null || !ag.IsActive()) continue;
                    var vdistSq = ag.Position.DistanceSquared(player.Position);
                    if (vdistSq <= kProxYieldSqM)
                    {
                        _conversationStarted = true;
                        HideoutPeacefulVisitState.Log(
                            "TickGreeterIntercept Phase 2: yielding to nearby NPC (player approaching vendor at "
                            + System.Math.Sqrt(vdistSq).ToString("F1") + "m)");
                        return;
                    }
                }
            }
            catch { /* defensive */ }

            try
            {
                var arrivalSq = _dispatchedGuard.Position.DistanceSquared(player.Position);

                if (arrivalSq > 6.25f) // 2.5m² — not yet at speaking distance
                {
                    // Re-dispatch toward player's CURRENT position so guard tracks the moving player.
                    var scene = _dispatchedGuard.Mission?.Scene;
                    if (scene != null)
                    {
                        var worldPos = new WorldPosition(scene, UIntPtr.Zero, player.Position, false);
                        _dispatchedGuard.SetScriptedPosition(ref worldPos,
                            addHumanLikeDelay: false,
                            additionalFlags: Agent.AIScriptedFrameFlags.NoAttack);
                    }
                    return;
                }

                // Within speaking distance — fire conversation.
                // Wave 4.11-fix v28 (2026-05-07): only lock the _conversationStarted gate
                // AFTER verifying conversation actually engaged. Previously this line ran
                // before TryStartGuardConversation, so a silent OnAgentInteraction failure
                // (player sprinting past, agent state mismatch) would lock the gate
                // permanently → "dialog skips" symptom. Now we attempt, then verify.
                TryStartGuardConversation(_dispatchedGuard);
                try
                {
                    var convoChar = Campaign.Current?.ConversationManager?.OneToOneConversationCharacter;
                    if (convoChar != null)
                    {
                        _conversationStarted = true;
                    }
                    else
                    {
                        HideoutPeacefulVisitState.Log(
                            "TickGreeterIntercept Phase 2: convo did not engage, retrying next tick");
                    }
                }
                catch { /* defensive — leave gate false so next tick retries */ }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("TickGreeterIntercept Phase 2 fail (clearing dispatched guard for re-pick): " + ex.Message);
                _dispatchedGuard = null;
                _hasGreeted = false; // allow Phase 1 to dispatch a fresh guard next tick
            }
        }

        // Open a conversation between the player and the dispatched guard.
        // Mission.OnAgentInteraction is the canonical click-to-talk handler — same code
        // path that fires when player clicks an NPC. Verified working at 04:09:39 (the
        // earlier "crash" was actually a SEPARATE fall-damage NRE in BattleAgentLogic,
        // patched independently in HideoutPeacefulVisitPatch.cs:PeacefulNoBattleAgentLogicHitPatch).
        // Bone index 0 = head/default. After call, vanilla overlay opens and our priority-200
        // HideoutGreeterDialog "start" line fires from Pass C registration.
        // Wave 4.6.7j: removed all particle-related helpers. Gold glow via brush override.

        // 2026-07-26 — REMOVED: the Wave 4.6.7k quest-marker colour pulse.
        //
        // It never worked and was never wired up. TickQuestMarkerPulse had zero callers,
        // and its two cache fields (_cachedQuestMarkerLayer / _cachedColorFactorProp)
        // were read but never assigned — CS0649 — so the branch that applied the pulse
        // was permanently unreachable. The helper it called,
        // SearchAndCacheQuestMarkerLayer, was pure discovery scaffolding: it walked
        // EVERY type in EVERY loaded assembly, filtered for "Brush" in the name, and
        // dumped their full method signatures into the log. That shipped in Release.
        //
        // Deleted rather than repaired: reinstating the feature means finding the real
        // brush API, which is new work, not a warning fix.

        private static System.Reflection.MethodInfo _cachedIdLookup;
        private static int ResolveParticleId(string name)
        {
            try
            {
                if (_cachedIdLookup == null)
                {
                    foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            foreach (var tt in asm.GetTypes())
                            {
                                if (!tt.IsClass || tt.Namespace == null) continue;
                                if (!tt.Namespace.StartsWith("TaleWorlds")) continue;
                                if (!tt.Name.Contains("Particle")) continue;
                                foreach (var m in tt.GetMethods(
                                    System.Reflection.BindingFlags.Public
                                    | System.Reflection.BindingFlags.Static))
                                {
                                    if (m.ReturnType != typeof(int)) continue;
                                    var ps = m.GetParameters();
                                    if (ps.Length != 1 || ps[0].ParameterType != typeof(string)) continue;
                                    _cachedIdLookup = m;
                                    HideoutPeacefulVisitState.Log("Cached id-lookup: " + tt.Name + "." + m.Name);
                                    break;
                                }
                                if (_cachedIdLookup != null) break;
                            }
                        }
                        catch { }
                        if (_cachedIdLookup != null) break;
                    }
                }
                if (_cachedIdLookup == null) return -1;
                object result = _cachedIdLookup.Invoke(null, new object[] { name });
                return result is int i ? i : -1;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("ResolveParticleId fail: " + ex.Message);
                return -1;
            }
        }

        // Wave 4.6.7h: reflection-based particle spawner. Scans Scene's methods for any
        // matching pattern (name contains "particle" or "burst", takes 2-3 args, accepts
        // a string name or int index). Logs once with what's available so we can lock in
        // the right method in a follow-up. Returns true if any method was successfully
        // invoked; false if nothing matched (we'll log that and the user gets no particle).
        private static System.Reflection.MethodInfo _cachedParticleMethod;
        private static bool _particleApiLogged;
        private static bool _idLogged;
        public static void SpawnParticleViaReflection(string particleName, TaleWorlds.Engine.Scene scene, ref MatrixFrame frame)
        {
            try
            {
                var sceneType = scene.GetType();
                // Wave 4.6.7i: Scene.CreateBurstParticle(int id, MatrixFrame) — confirmed.
                // First find the name→id lookup. Likely static on a Particle/MBParticle
                // type, or instance method on Scene.
                if (!_particleApiLogged)
                {
                    _particleApiLogged = true;
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("Particle type/method search:");
                    // Scene methods that might do name lookups.
                    foreach (var m in sceneType.GetMethods(
                        System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Static))
                    {
                        var n = m.Name;
                        if ((n.Contains("Particle") || n.Contains("Burst")) &&
                            (n.Contains("Index") || n.Contains("Get") || n.Contains("Find") || n.Contains("Lookup")))
                        {
                            sb.Append("  Scene.").Append(m.ReturnType.Name).Append(' ').Append(n).Append('(');
                            foreach (var p in m.GetParameters())
                                sb.Append(p.ParameterType.Name).Append(' ');
                            sb.AppendLine(")");
                        }
                    }
                    // Static types with Particle in name.
                    foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            foreach (var tt in asm.GetTypes())
                            {
                                if (!tt.IsClass) continue;
                                if (!tt.Name.Contains("Particle")) continue;
                                if (tt.Namespace == null || !tt.Namespace.StartsWith("TaleWorlds")) continue;
                                sb.AppendLine("  Type: " + tt.FullName);
                                foreach (var m in tt.GetMethods(
                                    System.Reflection.BindingFlags.Public
                                    | System.Reflection.BindingFlags.Static))
                                {
                                    if (m.Name.Contains("Index") || m.Name.Contains("FromName") || m.Name.Contains("Get"))
                                    {
                                        sb.Append("    static ").Append(m.ReturnType.Name).Append(' ').Append(m.Name).Append('(');
                                        foreach (var p in m.GetParameters())
                                            sb.Append(p.ParameterType.Name).Append(' ');
                                        sb.AppendLine(")");
                                    }
                                }
                            }
                        }
                        catch { /* some assemblies fail GetTypes — ignore */ }
                    }
                    HideoutPeacefulVisitState.Log(sb.ToString());
                }

                // Find CreateBurstParticle(int, MatrixFrame).
                if (_cachedParticleMethod == null)
                {
                    foreach (var m in sceneType.GetMethods(
                        System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.Instance))
                    {
                        if (m.Name != "CreateBurstParticle") continue;
                        var ps = m.GetParameters();
                        if (ps.Length < 2) continue;
                        if (ps[0].ParameterType != typeof(int)) continue;
                        _cachedParticleMethod = m;
                        HideoutPeacefulVisitState.Log("Cached: " + m.Name);
                        break;
                    }
                }

                // Try multiple candidate particle names — psys_game_sparkle_a may not be
                // loaded in the village mission scene; try alternatives.
                int particleId = -1;
                string usedName = null;
                // VERIFY-CALL phase: try a very obvious effect first to confirm the API
                // call works. If fire shows over the boss, the code path is right and we
                // just need to swap to the actual sparkle.
                string[] candidates = new[]
                {
                    "psys_campaign_main_quest_marker", "psys_campaign_quest_marker",
                    "psys_game_sparkle_a", "psys_game_sparkle_b",
                    "psys_fire_basic", "psys_smoke_basic"
                };
                foreach (var n in candidates)
                {
                    int id = ResolveParticleId(n);
                    if (id >= 0) { particleId = id; usedName = n; break; }
                }
                if (!_idLogged)
                {
                    _idLogged = true;
                    HideoutPeacefulVisitState.Log("Particle id resolution: name=" + (usedName ?? "ALL_FAILED") + " id=" + particleId);
                }
                if (particleId < 0) return;

                if (_cachedParticleMethod != null)
                {
                    var ps = _cachedParticleMethod.GetParameters();
                    var args = new object[ps.Length];
                    args[0] = particleId;
                    args[1] = frame;
                    for (int i = 2; i < ps.Length; i++)
                    {
                        if (ps[i].ParameterType == typeof(bool)) args[i] = true;
                        else args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
                    }
                    _cachedParticleMethod.Invoke(scene, args);
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("SpawnParticleViaReflection fail: " + ex.Message);
            }
        }

        // Wave 4.6.7f: spawns vanilla gold sparkle particles above the boss's head every
        // ~1.5 seconds when chief has an offerable trust quest. Uses native vanilla
        // particle system "psys_game_sparkle_a" — no custom asset authoring needed. The
        // sparkles match the nameplate gold "!" marker visually.
        private float _bossSparkleTimer;
        private const float kBossSparkleInterval = 0.4f; // tighter for visibility test
        private void TickBossSparkle(float dt)
        {
            try
            {
                if (!HideoutPeacefulVisitState.Active) return;
                if (_bossAgent == null || !_bossAgent.IsActive()) return;

                // Skip if chief has no offerable quest (Tier 2 reached or no profile).
                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                var hideout = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement;
                string cultureId = hideout?.Culture?.StringId
                    ?? hideout?.OwnerClan?.Culture?.StringId;
                if (bdm == null || string.IsNullOrEmpty(cultureId)) return;
                int trust = bdm.GetCultureTrust(cultureId);
                if (trust >= 2) return; // no more quests, no sparkle

                _bossSparkleTimer += dt;
                if (_bossSparkleTimer < kBossSparkleInterval) return;
                _bossSparkleTimer = 0f;

                var mission = Mission.Current;
                if (mission?.Scene == null) return;

                // Position: align with the nameplate "!" which floats above the head.
                // Agent.Position is at the agent's feet; the nameplate hovers around 3.5m
                // up. Spawn particles there so they read as "around the question mark"
                // rather than on the head.
                var pos = _bossAgent.Position;
                pos.z += 3.5f;
                var frame = new MatrixFrame(Mat3.Identity, pos);
                // Wave 4.6.7h: discover particle API by reflection ONCE and cache. Bannerlord
                // 1.2.x exposes Scene methods that aren't documented in our compile-time
                // assemblies but exist at runtime. Try common names; cache the working one.
                SpawnParticleViaReflection("psys_game_sparkle_a", mission.Scene, ref frame);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("TickBossSparkle fail: " + ex.Message);
            }
        }

        // Wave 4.6.5: boss F-press handler. When the player is within ~3m of the boss
        // and presses F (and isn't already in conversation / greeter dispatch), this fires
        // a programmatic conversation start — same pattern as TryStartGuardConversation
        // for the greeter. Without this, vanilla doesn't recognize the boss as talkable
        // (he's not at a vanilla interaction-trigger entity) and the F-press produces
        // either nothing or the "I'm not allowed to talk to you" fallback.
        // Throttled flag so we don't re-trigger every frame F is held.
        private bool _bossInteractionLoggedThisFrame;
        private void TickBossInteraction(float dt)
        {
            try
            {
                bool fPressed = Input.IsKeyPressed(InputKey.F);
                if (!fPressed) { _bossInteractionLoggedThisFrame = false; return; }
                if (_bossInteractionLoggedThisFrame) return;
                _bossInteractionLoggedThisFrame = true;

                if (!HideoutPeacefulVisitState.Active) return;
                if (_bossAgent == null || !_bossAgent.IsActive()) return;
                if (_invokingDispatch || IsGreeterDispatchInFlight()) return;

                var mission = Mission.Current;
                if (mission == null) return;
                var player = mission.MainAgent;
                if (player == null || !player.IsActive()) return;

                // Wave 4.6.5: do NOT bail on mission.Mode == Conversation. Vanilla's
                // F-press handler opens an empty "I'm not allowed" fail-conversation
                // because the boss isn't at a vanilla interaction-trigger entity. We
                // explicitly end that fail-conversation, then open ours. Without the
                // EndConversation step, OnAgentInteraction is a no-op while a
                // conversation is already active.
                float distSq = _bossAgent.Position.DistanceSquared(player.Position);
                if (distSq > 9f) return; // 3m^2

                if (mission.Mode == MissionMode.Conversation)
                {
                    try { Campaign.Current?.ConversationManager?.EndConversation(); }
                    catch (Exception endEx) {
                        HideoutPeacefulVisitState.Log("TickBossInteraction: EndConversation threw: " + endEx.Message);
                    }
                }

                try { _bossAgent.SetAsConversationAgent(true); } catch { /* defensive */ }
                // Wave 4.6.5c: do NOT set _invokingDispatch here — that flag is reserved
                // for the greeter. The greeter's IsPeacefulHideoutGreeter condition uses
                // it as "greeter always wins during dispatch", which would hijack our boss
                // dialog with the greeter dialog at priority 200 > our 170.
                // Wave 4.6.5d: set _bossConvBeingInitiated so IsChiefCampConv can identify
                // the boss as conversation partner without relying on GetLookAgent (which
                // returns null during programmatic conversation triggering).
                _bossConvBeingInitiated = true;
                try
                {
                    mission.OnAgentInteraction(player, _bossAgent, 0);
                }
                catch (Exception oaEx)
                {
                    HideoutPeacefulVisitState.Log("TickBossInteraction: OnAgentInteraction threw: " + oaEx.Message);
                }
                finally
                {
                    _bossConvBeingInitiated = false;
                }
                HideoutPeacefulVisitState.Log("TickBossInteraction: opened chief conversation");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("TickBossInteraction fail: " + ex.Message);
            }
        }

        // Wave 4.12-fix4 (2026-05-11): parallel to TickBossInteraction for the slaver
        // vendor. The slaver spawns at a synthetic position (boss + (0, -5, 0)) with no
        // scene-tagged interaction trigger — vanilla F-press fails the same way it does
        // for the boss. We force-open the conversation programmatically when F is pressed
        // within 3m of the slaver. The 5m offset between boss and slaver ensures the two
        // interaction zones don't overlap; TickBossInteraction runs first, so if player
        // is closer to boss, boss conversation opens and TickSlaverInteraction sees an
        // active conversation and bails.
        private void TickSlaverInteraction(float dt)
        {
            try
            {
                bool fPressed = Input.IsKeyPressed(InputKey.F);
                if (!fPressed) { _slaverInteractionLoggedThisFrame = false; return; }
                if (_slaverInteractionLoggedThisFrame) return;
                _slaverInteractionLoggedThisFrame = true;

                if (!HideoutPeacefulVisitState.Active) return;
                if (_slaverVendor == null || !_slaverVendor.IsActive()) return;
                if (_invokingDispatch || IsGreeterDispatchInFlight()) return;

                var mission = Mission.Current;
                if (mission == null) return;
                var player = mission.MainAgent;
                if (player == null || !player.IsActive()) return;

                float distSq = _slaverVendor.Position.DistanceSquared(player.Position);
                if (distSq > 9f) return;  // 3m^2 — same radius as boss interaction

                // If a conversation is already active (e.g. TickBossInteraction grabbed
                // this F-press first because player was simultaneously close to both),
                // bail — DON'T end an existing chief conversation to replace it with
                // slaver. The boss has narrative priority.
                if (mission.Mode == MissionMode.Conversation) return;

                try { _slaverVendor.SetAsConversationAgent(true); } catch { /* defensive */ }

                try
                {
                    mission.OnAgentInteraction(player, _slaverVendor, 0);
                }
                catch (Exception oaEx)
                {
                    HideoutPeacefulVisitState.Log("TickSlaverInteraction: OnAgentInteraction threw: " + oaEx.Message);
                }
                HideoutPeacefulVisitState.Log("TickSlaverInteraction: opened slaver conversation");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("TickSlaverInteraction fail: " + ex.Message);
            }
        }

        private void TryStartGuardConversation(Agent guard)
        {
            try
            {
                var mission = Mission.Current;
                if (mission == null) return;
                var player = mission.MainAgent;
                if (player == null || !player.IsActive()) return;

                // CRITICAL: mark guard as conversation agent BEFORE OnAgentInteraction.
                // Vanilla click-to-talk path does this internally; programmatic calls must
                // do it explicitly or the dialog UI won't actually open (only the interaction
                // event fires). This was the missing piece in earlier conversation attempts.
                try { guard.SetAsConversationAgent(true); } catch { /* defensive */ }

                // Set invoking-dispatch flag DURING the OnAgentInteraction call. Vanilla
                // evaluates dialog conditions synchronously inside this call. Our greeter
                // condition checks _invokingDispatch to know "this is OUR auto-dispatch
                // happening RIGHT NOW" — which previously failed because _conversationStarted
                // was already true by the time conditions evaluated.
                _invokingDispatch = true;
                try
                {
                    mission.OnAgentInteraction(player, guard, 0);
                }
                finally
                {
                    _invokingDispatch = false;
                }
                HideoutPeacefulVisitState.Log("TryStartGuardConversation: SetAsConversationAgent + OnAgentInteraction invoked");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("TryStartGuardConversation fail: " + ex.Message);
            }
        }

        private static void AssignWanderDestination(Agent agent, Vec3 target)
        {
            try
            {
                if (agent == null || !agent.IsActive()) return;
                var scene = agent.Mission?.Scene;
                if (scene == null) return;
                var worldPos = new WorldPosition(scene, UIntPtr.Zero, target, false);
                // DoNotRun = NPCs WALK to their destination instead of sprinting.
                // NoAttack = peaceful camp, no hostile reactions.
                agent.SetScriptedPosition(ref worldPos,
                    addHumanLikeDelay: true,
                    additionalFlags: Agent.AIScriptedFrameFlags.NoAttack | Agent.AIScriptedFrameFlags.DoNotRun);
            }
            catch (Exception ex)
            {
                // BUG M1 FIX (2026-04-30): replaced empty `catch {}` with logged catch. Previously
                // every failure (NavMesh missing for target, scene null mid-tick, agent destroyed)
                // was silently swallowed → wanderers froze with no diagnostic. Now we get a
                // single log line per failure so degenerate scenes are immediately visible.
                HideoutPeacefulVisitState.Log("AssignWanderDestination fail (" + ex.GetType().Name + "): " + ex.Message);
            }
        }

        private void OnMissionStarted(IMission mission)
        {
            try
            {
                if (!HideoutPeacefulVisitState.Active)
                {
                    return; // Not our peaceful walkable hideout
                }

                var concrete = mission as Mission;
                if (concrete == null || concrete.Scene == null)
                {
                    HideoutPeacefulVisitState.Log("NpcSpawn: BAIL — mission not a concrete Mission or no Scene");
                    return;
                }

                HideoutPeacefulVisitState.Log("NpcSpawn: peaceful mission started, scene=" + (concrete.SceneName ?? "?"));

                // CHAIR-BIND TEARDOWN PROTECTION: dynamically install the AnimationPoint patch
                // for the duration of THIS peaceful mission only. Removed in OnMissionEnded so
                // the patch never exists during encyclopedia preview (which broke previously
                // when the patch was registered globally at module load).
                BandItPlus.Patches.PeacefulNoAnimationPointVisibilityPatch.Enable();

                // Build a roster of culture-appropriate characters for variety (not all identical).
                // Same loop also identifies the highest-tier troop as the BOSS character — using
                // the strongest available troop gives the chief better gear/visuals than rank-and-file.
                var hideout = HideoutPeacefulVisitState.TargetHideout;
                var cultureId = hideout?.Culture?.StringId;
                var charPool = new List<CharacterObject>();
                CharacterObject bossChar = null;
                int bossTier = -1;
                if (!string.IsNullOrEmpty(cultureId))
                {
                    foreach (var c in MBObjectManager.Instance.GetObjectTypeList<CharacterObject>())
                    {
                        if (c == null || c.Culture?.StringId != cultureId) continue;
                        if (c.IsHero) continue;
                        if (c.Tier > bossTier) { bossTier = c.Tier; bossChar = c; }
                        if (charPool.Count < 6) charPool.Add(c);
                    }
                }
                if (charPool.Count == 0)
                {
                    var fallback = MBObjectManager.Instance.GetObject<CharacterObject>("looter");
                    if (fallback != null) charPool.Add(fallback);
                }
                if (charPool.Count == 0)
                {
                    HideoutPeacefulVisitState.Log("NpcSpawn: BAIL — no usable CharacterObjects");
                    return;
                }
                HideoutPeacefulVisitState.Log("NpcSpawn: character pool size=" + charPool.Count + " (culture=" + (cultureId ?? "fallback") + ")");

                // Scan ALL scene entities for spawn-point names — populate every "sp_npc_*" / "sp_common"
                // / "sp_guard_*" the scene designer placed. Skips sp_player (that's our spawn).
                var allEntities = new List<GameEntity>();
                concrete.Scene.GetEntities(ref allEntities);

                // Reset wander state for this fresh mission
                _wanderingNpcs.Clear();
                _wanderTargets.Clear();
                _npcRoles.Clear();
                _usedNames.Clear();
                _spawnedPositions.Clear();
                _bodyguards.Clear(); // M7
                _wanderRoundRobinIdx = 0;
                _lastWanderTickTime = 0f;

                // First pass: collect spawn points and split them by role-implying name pattern.
                // Vanilla scene-designer convention: sp_guard_* are watch posts, sp_common are
                // gathering points (vendors/idlers), patrol_point* and sp_npc_* are general use.
                // Boss spots are rarely named in vanilla scenes — we fall back to scene centroid.
                //
                // De-duplication: scene designers often stack multiple spawn markers at nearly
                // identical coordinates so the engine can pick variety. Without filtering, our
                // spawner placed multiple NPCs at the same point and they clipped into each other.
                // We keep only one spot per ~1.2m radius — close enough that any two NPCs read as
                // separate, far enough that capsule colliders don't overlap.
                const float kMinSpotSpacingSq = 1.2f * 1.2f;
                bool TooCloseToExisting(Vec3 p, List<(Vec3, Vec2, string)>[] lists)
                {
                    foreach (var list in lists)
                        foreach (var s in list)
                            if (s.Item1.DistanceSquared(p) < kMinSpotSpacingSq) return true;
                    return false;
                }

                var bossSpots    = new List<(Vec3 pos, Vec2 dir, string name)>();
                var sitterSpots  = new List<(Vec3 pos, Vec2 dir, string name)>();
                var guardSpots   = new List<(Vec3 pos, Vec2 dir, string name)>();
                var vendorSpots  = new List<(Vec3 pos, Vec2 dir, string name)>();
                var generalSpots = new List<(Vec3 pos, Vec2 dir, string name)>();
                var captiveSpots = new List<(Vec3 pos, Vec2 dir, string name)>();  // 2026-06-05 Wave 4.13: slaver-only captive role
                // Parallel list — same indices as sitterSpots. Stores chair UsableMissionObject
                // (StandingPoint script) when the entity carries one. null = no usable, fallback
                // to ground-sit pose (BeggarSuffix from Path A).
                var sitterUsables = new List<UsableMissionObject>();
                // BUG-FIX #2 (2026-04-30): track fireplace mesh positions for trainer-pair
                // exclusion zone. Populated in the classify loop below; consumed in Pass B.
                var firePositions = new List<Vec3>();
                // BIOME DIAGNOSTIC (2026-04-30): collect unique entity-name prefixes (text up
                // to the first `_` or full name if no underscore). Logged once per scene so
                // future biome-tuning has data — if a desert/steppe/sturgian hideout uses a
                // shelter or fire variant we don't yet match, the prefix will appear here
                // and we can extend the matchers above. HashSet deduplicates aggressively.
                var prefixDiagnostic = new HashSet<string>();
                var allLists = new[] { bossSpots, sitterSpots, guardSpots, vendorSpots, generalSpots, captiveSpots };
                int dedupedCount = 0;
                foreach (var ent in allEntities)
                {
                    if (ent?.Name == null) continue;
                    var lname = ent.Name.ToLowerInvariant();
                    if (lname.Contains("player")) continue;
                    // Capture prefix for biome-tuning diagnostic.
                    int us = lname.IndexOf('_');
                    prefixDiagnostic.Add(us > 0 ? lname.Substring(0, us) : lname);
                    var frame = ent.GetGlobalFrame();
                    var entry = (frame.origin, frame.rotation.f.AsVec2.Normalized(), ent.Name);

                    // Skip if a spot already exists within the spacing radius — kills the
                    // "two bandits clipping into each other" issue at the source.
                    if (TooCloseToExisting(frame.origin, allLists)) { dedupedCount++; continue; }

                    // Boss spots — rare; check first so explicit naming wins over generic patterns.
                    if (lname.Contains("boss") || lname.Contains("chief") ||
                        lname.Contains("leader") || lname.Contains("throne"))
                    {
                        bossSpots.Add(entry);
                        _wanderTargets.Add(frame.origin);
                    }
                    // Sitter spots — chairs, benches, named idle-sit positions in scene XML.
                    // Bandits placed here read as resting/loafing rather than milling around.
                    // ALSO try to grab the entity's UsableMissionObject script (StandingPoint
                    // for chairs) so we can bind the agent to it via UseGameObject after spawn.
                    // If the entity has no usable script, the sitter still spawns but uses the
                    // ground-sit pose only (BeggarSuffix action set from Path A).
                    else if (lname.Contains("sit") || lname.Contains("chair") || lname.Contains("bench"))
                    {
                        sitterSpots.Add(entry);
                        _wanderTargets.Add(frame.origin);
                        UsableMissionObject usable = null;
                        try { usable = ent.GetFirstScriptOfType<UsableMissionObject>(); }
                        catch { /* defensive — script lookup may throw on edge entities */ }
                        sitterUsables.Add(usable);
                    }
                    // 2026-06-05 Wave 4.13: captive/prisoner/cage entities — slaver-culture only.
                    // Substring match (not strict prefix) catches authored names like
                    // sp_captive_1, cage_02, prisoner_a, etc. Gated by culture at budget time.
                    else if (lname.Contains("captive") || lname.Contains("prisoner") || lname.Contains("cage"))
                    {
                        captiveSpots.Add(entry);
                        _wanderTargets.Add(frame.origin);
                    }
                    else if (lname.StartsWith("sp_guard_"))
                    {
                        guardSpots.Add(entry);
                        _wanderTargets.Add(frame.origin);
                    }
                    // Tent / yurt / pavilion shelter meshes — the proper vendor home base.
                    // BIOME COVERAGE (2026-04-30): expanded prefix list to include steppe yurts
                    // (Khuzait-themed bandit hideouts) and desert/Sturgian pavilions. STRICT
                    // prefix matches only — we got burned earlier by a `Contains("tent")` greedy
                    // match catching `content_root` and `equipment_*` entities. Each prefix here
                    // ends in `_` to require a separator (forces real shelter names).
                    else if (lname.StartsWith("tent_") || lname == "tent" ||
                             lname.StartsWith("bandit_tent_") ||
                             lname.StartsWith("yurt_") || lname == "yurt" ||
                             lname.StartsWith("pavilion_") || lname == "pavilion")
                    {
                        vendorSpots.Insert(0, entry);
                        _wanderTargets.Add(frame.origin);
                    }
                    else if (lname == "sp_common")
                    {
                        vendorSpots.Add(entry);
                        _wanderTargets.Add(frame.origin);
                    }
                    else if (lname.StartsWith("sp_npc_") || lname.StartsWith("patrol_point"))
                    {
                        generalSpots.Add(entry);
                        _wanderTargets.Add(frame.origin);
                    }

                    // BUG-FIX #2 (2026-04-30): Detect fireplace mesh entities to keep
                    // armed/sparring NPCs away from them. Recording position only — we don't
                    // reclassify the entity (it's already been routed to general/etc above
                    // if name-matches). Strict prefix matching to avoid the substring-greedy
                    // mistake we made with "tent" earlier.
                    // BIOME COVERAGE (2026-04-30): expanded for cross-biome fire-source meshes:
                    //   fireplace/firepit/campfire/bonfire — common across all biomes
                    //   hearth_*  — Sturgian/Vlandian indoor-style hearths
                    //   brazier_* — Aserai/desert metal fire bowls
                    //   torch_*   — wall-mounted flames in some scenes (treat as fire-zone too)
                    //   sp_npc_idle_fireplace_* / sp_fireplace_* — authored fire spawn anchors
                    if (lname.StartsWith("fireplace") || lname.StartsWith("firepit") ||
                        lname.StartsWith("campfire") || lname.StartsWith("bonfire") ||
                        lname.StartsWith("hearth_") || lname == "hearth" ||
                        lname.StartsWith("brazier_") || lname == "brazier" ||
                        lname.StartsWith("torch_") ||
                        lname.StartsWith("sp_npc_idle_fireplace") ||
                        lname.StartsWith("sp_fireplace"))
                    {
                        firePositions.Add(frame.origin);
                    }
                }
                if (dedupedCount > 0)
                    HideoutPeacefulVisitState.Log("NpcSpawn: skipped " + dedupedCount + " duplicate/overlapping spawn markers");
                if (firePositions.Count > 0)
                    HideoutPeacefulVisitState.Log("NpcSpawn: detected " + firePositions.Count + " fireplace position(s) — trainers will avoid 3m radius");
                // Biome diagnostic — emit unique prefixes for this scene. Sorted + capped to
                // 80 chars per line so the log line is readable. Limited to first 100 prefixes
                // to avoid log flooding on huge scenes.
                if (prefixDiagnostic.Count > 0)
                {
                    var prefixList = new List<string>(prefixDiagnostic);
                    prefixList.Sort();
                    if (prefixList.Count > 100) prefixList = prefixList.GetRange(0, 100);
                    HideoutPeacefulVisitState.Log("NpcSpawn: scene entity name prefixes (" + prefixDiagnostic.Count + " unique) = " + string.Join(",", prefixList));
                }

                // Boss-spot fallback: if the scene didn't name one, pick the spawn point closest
                // to the centroid of all spots. That's visually the "center of camp" — natural
                // place for the chief to stand. Computed only on fallback to avoid wasted work.
                if (bossSpots.Count == 0 && _wanderTargets.Count > 0)
                {
                    Vec3 centroid = Vec3.Zero;
                    foreach (var t in _wanderTargets) centroid += t;
                    centroid *= 1f / _wanderTargets.Count;

                    // search across all classified spots for the one closest to centroid
                    (Vec3 pos, Vec2 dir, string name)? best = null;
                    float bestSq = float.MaxValue;
                    void Consider(List<(Vec3, Vec2, string)> list)
                    {
                        foreach (var s in list)
                        {
                            var d = s.Item1.DistanceSquared(centroid);
                            if (d < bestSq) { bestSq = d; best = s; }
                        }
                    }
                    Consider(guardSpots); Consider(vendorSpots); Consider(generalSpots);
                    if (best.HasValue) bossSpots.Add(best.Value);
                }

                // Whole-scene scan for UsableMissionObject scripts. Bannerlord scenes commonly
                // split a chair into a cosmetic mesh entity (named like "chair_a_1") and a
                // separate spawn-point entity (named like "sp_npc_idle_sitting_2") carrying the
                // StandingPoint/UsableMissionObject script. Our name-filtered loop above only
                // catches one or the other — and usually the one WITHOUT the script. This pass
                // collects ALL usables regardless of name, then we pair sitters geometrically.
                var sceneUsables = new List<(Vec3 pos, UsableMissionObject usable, string name)>();
                foreach (var ent in allEntities)
                {
                    if (ent == null) continue;
                    UsableMissionObject u = null;
                    try { u = ent.GetFirstScriptOfType<UsableMissionObject>(); } catch { }
                    if (u == null) continue;
                    sceneUsables.Add((ent.GetGlobalFrame().origin, u, ent.Name ?? "?"));
                }

                // Diagnostic — log a sample of what we found so we know what entity names
                // and script types are actually present. First 5 only to avoid log spam.
                if (sceneUsables.Count > 0)
                {
                    int sampleCount = Math.Min(5, sceneUsables.Count);
                    var sb = new System.Text.StringBuilder();
                    sb.Append("NpcSpawn: scene has ").Append(sceneUsables.Count).Append(" UsableMissionObjects, sample: ");
                    for (int i = 0; i < sampleCount; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append("'").Append(sceneUsables[i].name).Append("'(")
                          .Append(sceneUsables[i].usable.GetType().Name).Append(")");
                    }
                    HideoutPeacefulVisitState.Log(sb.ToString());
                }
                else
                {
                    HideoutPeacefulVisitState.Log("NpcSpawn: scene has ZERO UsableMissionObjects — no chairs/benches with scripts available");
                }

                // Nearest-usable fallback: for each sitter spot whose name-filtered lookup
                // returned null, find the closest unused chair-class scene-usable within 3m.
                //
                // TYPE FILTER (added after diagnostic showed PatrolPoint pollution): the master
                // sceneUsables list contains ALL UsableMissionObjects, including 100+ PatrolPoints
                // (path waypoints) per hideout. Binding a sitter to a PatrolPoint via UseGameObject
                // makes them stand at chair-seat height in default standing pose — exactly the
                // "standing on chair" visual bug. Reject anything with "Patrol" in the type name;
                // accept only types with sit/chair/stand markers. Greedy: each usable claimed once.
                const float kPairRadiusSq = 3f * 3f;
                var claimedUsables = new HashSet<int>();
                int filteredOut = 0;
                for (int i = 0; i < sitterUsables.Count; i++)
                {
                    if (sitterUsables[i] != null) continue; // already paired by name match
                    var spotPos = sitterSpots[i].pos;
                    int bestIdx = -1;
                    float bestSq = kPairRadiusSq;
                    for (int j = 0; j < sceneUsables.Count; j++)
                    {
                        if (claimedUsables.Contains(j)) continue;
                        var typeName = sceneUsables[j].usable.GetType().Name;
                        // Reject patrol points / non-chair waypoints
                        if (typeName.Contains("Patrol")) { filteredOut++; continue; }
                        var d = sceneUsables[j].pos.DistanceSquared(spotPos);
                        if (d < bestSq) { bestSq = d; bestIdx = j; }
                    }
                    if (bestIdx >= 0)
                    {
                        sitterUsables[i] = sceneUsables[bestIdx].usable;
                        claimedUsables.Add(bestIdx);
                    }
                }
                if (filteredOut > 0)
                    HideoutPeacefulVisitState.Log("NpcSpawn: filtered out " + filteredOut + " non-chair usables (PatrolPoint etc) during sitter pairing");

                // SCENE-DATA REALITY: hideout scenes were authored for combat, so many "chair"
                // entities are decorative meshes WITHOUT IUsable scripts. The sitter spawn
                // markers next to them are positioned at chair-seat height, assuming a real
                // sit-bind will happen. If we spawn at that height without binding, the agent
                // renders standing pose in midair "on" the chair — exactly the visual bug the
                // user reported. SOLUTION: drop sitter spots whose pairing returned null. With
                // 14+ candidates per scene we have surplus; the first 4 with valid usables wins.
                {
                    var filteredSpots = new List<(Vec3 pos, Vec2 dir, string name)>();
                    var filteredUsables = new List<UsableMissionObject>();
                    for (int i = 0; i < sitterSpots.Count; i++)
                    {
                        if (sitterUsables[i] == null) continue;
                        filteredSpots.Add(sitterSpots[i]);
                        filteredUsables.Add(sitterUsables[i]);
                    }
                    int dropped = sitterSpots.Count - filteredSpots.Count;
                    if (dropped > 0)
                        HideoutPeacefulVisitState.Log("NpcSpawn: dropped " + dropped + " sitter spots with no chair-script (decorative-only chairs)");
                    sitterSpots = filteredSpots;
                    sitterUsables = filteredUsables;
                }

                int usableSitterCount = 0;
                foreach (var u in sitterUsables) if (u != null) usableSitterCount++;
                HideoutPeacefulVisitState.Log("NpcSpawn: classified spots — boss=" + bossSpots.Count +
                    " sitters=" + sitterSpots.Count + "(" + usableSitterCount + " with chair usable)" +
                    " guards=" + guardSpots.Count +
                    " vendors=" + vendorSpots.Count + " general=" + generalSpots.Count +
                    " captives=" + captiveSpots.Count +
                    " (total wander targets=" + _wanderTargets.Count + ")");

                // Role budget — derived once, scaled by what's available in the scene.
                // Tuned per user request: GUARDS DOMINATE (most populous specific role),
                // ensure ≥8 wanderers, ≥2 vendors, sitters cap at 8.
                const int spawnLimit = 43;
                int bossBudget    = (bossSpots.Count > 0 && bossChar != null) ? 1 : 0;
                int sitterBudget  = Math.Min(8, sitterSpots.Count);  // up to 8 sitters at chairs/benches
                // 2026-06-05 Wave 4.13: captives (slaver culture only) — max 4 even if scene
                // authors more cage/prisoner spots. Other cultures: zero captives even if a
                // scene re-uses captive-named entities (defensive: bp_slaver_caravans hideouts
                // share scenes with vanilla bandit hideouts; the gate prevents cross-culture spillover).
                int captiveBudget = (cultureId == "bp_slaver_caravans") ? Math.Min(4, captiveSpots.Count) : 0;
                if (captiveSpots.Count > 0 && captiveBudget == 0)
                    HideoutPeacefulVisitState.Log("NpcSpawn: " + captiveSpots.Count + " captive spots present but culture=" + cultureId + " (not bp_slaver_caravans) — skipped");
                // Sleepers DISABLED per user bug report: act_dungeon_prisoner_sleep_cycle
                // assumes agent pelvis is at ground level. Spawning at random general spots
                // (whose Y varies — tables, ledges, slopes) results in agents sleeping in
                // mid-air. Re-enabling would need scene-aware ground-position detection.
                // Re-enable trivially by changing 0 to 3 once we have ground-level detection.
                // sleeperBudget removed (M4): sleepers disabled, see Pass C1.5 comment block.
                int guardBudget   = Math.Min(12, guardSpots.Count);  // up to 12 guards if posts exist

                // Guard fallback (same shape as vendor fallback): hideout scenes typically have
                // only 2-3 sp_guard_* posts. To make guards the LARGEST specific role per user
                // request, promote general spots to guards until we have at least 10 guards.
                if (guardBudget < 10 && generalSpots.Count > 0)
                {
                    int promotionsNeeded = Math.Min(10 - guardBudget, generalSpots.Count);
                    int promoted = 0;
                    for (int k = 0; k < promotionsNeeded && generalSpots.Count > 0; k++)
                    {
                        int idx = generalSpots.Count - 1;
                        guardSpots.Add(generalSpots[idx]);
                        generalSpots.RemoveAt(idx);
                        promoted++;
                    }
                    if (promoted > 0)
                    {
                        guardBudget += promoted;
                        HideoutPeacefulVisitState.Log("NpcSpawn: promoted " + promoted + " general spots to guard (sp_guard_* short of 6)");
                    }
                }

                int trainerBudget = (generalSpots.Count >= 6) ? 4 : 0; // 2 pairs of trainers if room

                // BOSS-PROXIMITY VENDOR SELECTION (2026-04-30, user-requested):
                // Vendors should be close to the chief — they're part of his court/entourage,
                // not scattered across the camp. Replaces the prior "promote END-of-generalSpots"
                // approach which gave 25m vendor-vendor distances when scenes had no sp_common.
                //
                // Algorithm:
                //   1. Determine boss anchor position (bossSpots[0] if present, else campCenter
                //      computed below — but we need it now; recompute locally from _wanderTargets).
                //   2. Build candidate pool from existing vendorSpots (tents/sp_common preferred)
                //      + ALL remaining generalSpots.
                //   3. Sort candidates by distance-to-boss (ascending), with a small bonus for
                //      tents (subtract 2m equivalent) so a tent at 5m beats a general at 3m —
                //      tents still preferred when nearby.
                //   4. Pick the closest 2 with ≥1.5m mutual separation.
                //   5. Replace vendorSpots with these 2; remove promoted entries from generalSpots.
                Vec3 vendorAnchor;
                if (bossSpots.Count > 0) vendorAnchor = bossSpots[0].Item1;
                else
                {
                    Vec3 c = Vec3.Zero;
                    if (_wanderTargets.Count > 0)
                    {
                        foreach (var t in _wanderTargets) c += t;
                        c *= 1f / _wanderTargets.Count;
                    }
                    vendorAnchor = c;
                }

                // Build candidate pool with metadata: position, full entry, tent-flag, source-list-tag.
                // sourceTag: 0=existing vendorSpot (don't remove from generalSpots), 1=generalSpot.
                var vendorCandidates = new List<((Vec3 pos, Vec2 dir, string name) entry, bool isTent, int sourceTag, int sourceIdx)>();
                for (int v = 0; v < vendorSpots.Count; v++)
                {
                    var ve = vendorSpots[v];
                    bool isTent = ve.Item3 != null && (
                        ve.Item3.ToLowerInvariant().StartsWith("tent") ||
                        ve.Item3.ToLowerInvariant().StartsWith("yurt") ||
                        ve.Item3.ToLowerInvariant().StartsWith("pavilion") ||
                        ve.Item3.ToLowerInvariant().StartsWith("bandit_tent"));
                    vendorCandidates.Add((ve, isTent, 0, v));
                }
                for (int g = 0; g < generalSpots.Count; g++)
                {
                    vendorCandidates.Add((generalSpots[g], false, 1, g));
                }

                // MIN-DISTANCE-FROM-BOSS GATE (2026-04-30, user-requested):
                // Filter out candidates within 3m of the boss. Vendors stacked on the chief
                // looks wrong — they should be a few meters away (close enough to be his
                // "court" merchants, far enough not to crowd the throne). 3m is roughly
                // 2 character-widths, comfortable conversational distance.
                // Wave 4.12-fix5 (2026-05-11): bumped from 3m to 6m. User wants vendors
                // visibly separated from the chief — 3m allowed candidates that read as
                // "right next to the chief tent". 6m gives clear gap; existing sp_npc_wait
                // at ~7m from boss still qualifies, but tighter candidates get filtered out.
                const float kVendorMinFromBossSq = 6.0f * 6.0f;
                int beforeFilter = vendorCandidates.Count;
                vendorCandidates.RemoveAll(c => c.entry.pos.DistanceSquared(vendorAnchor) < kVendorMinFromBossSq);
                int filteredOutByBossDistance = beforeFilter - vendorCandidates.Count;
                if (filteredOutByBossDistance > 0)
                    HideoutPeacefulVisitState.Log("NpcSpawn: vendor candidates — dropped " + filteredOutByBossDistance + " too close to boss (<3m)");

                // Score: smaller = better. Distance to boss minus 2m bonus if tent (so tents
                // within ~2m extra range can beat closer non-tent generals). After the min-
                // distance filter, the "closest" candidate will be ≥3m away.
                vendorCandidates.Sort((a, b) =>
                {
                    float aD = (float)Math.Sqrt(a.entry.pos.DistanceSquared(vendorAnchor)) - (a.isTent ? 2f : 0f);
                    float bD = (float)Math.Sqrt(b.entry.pos.DistanceSquared(vendorAnchor)) - (b.isTent ? 2f : 0f);
                    return aD.CompareTo(bD);
                });

                // Pick the 2 best with ≥1.5m mutual separation.
                const float kVendorMinSepSq = 1.5f * 1.5f;
                var picked = new List<((Vec3 pos, Vec2 dir, string name) entry, int sourceTag, int sourceIdx)>(2);
                foreach (var cand in vendorCandidates)
                {
                    bool tooClose = false;
                    foreach (var p in picked)
                    {
                        if (cand.entry.pos.DistanceSquared(p.entry.pos) < kVendorMinSepSq) { tooClose = true; break; }
                    }
                    if (tooClose) continue;
                    picked.Add((cand.entry, cand.sourceTag, cand.sourceIdx));
                    if (picked.Count >= 2) break;
                }

                // Rebuild vendorSpots from the picks; remove any promoted generalSpots so they
                // don't double-spawn as wanderers. Sort generalSpots-removals descending so
                // index removal doesn't shift remaining indices.
                vendorSpots.Clear();
                var generalRemovals = new List<int>();
                foreach (var p in picked)
                {
                    vendorSpots.Add(p.entry);
                    if (p.sourceTag == 1) generalRemovals.Add(p.sourceIdx);
                }
                generalRemovals.Sort((a, b) => b.CompareTo(a));
                foreach (var idx in generalRemovals) generalSpots.RemoveAt(idx);

                // Wave 4.12-fix2 (2026-05-11): RESTORED scene-tagged tent positions for
                // food/gear vendors. Previous fix overrode vendorSpots with synthetic
                // boss-adjacent coordinates, but that planted vendors in OPEN GROUND, not
                // at the visible tent meshes. The original boss-proximity selection (built
                // around line 1820) already picks the 2 closest tent-tagged spots from the
                // scene — vendors stand at actual `tent_*` entity positions per scene XML.
                // Only fall back to synthetic positions if the scene has zero tent tags.
                if (vendorSpots.Count == 0 && _bossAgent != null && _bossAgent.IsActive())
                {
                    var bp = _bossAgent.Position;
                    vendorSpots.Add((new Vec3(bp.x - 2.5f, bp.y, bp.z), new Vec2(1f, 0f), "boss_fallback_food"));
                    vendorSpots.Add((new Vec3(bp.x + 2.5f, bp.y, bp.z), new Vec2(-1f, 0f), "boss_fallback_gear"));
                    HideoutPeacefulVisitState.Log("NpcSpawn: no tent tags in scene — vendors using boss-adjacent fallback at (" + bp.x.ToString("F1") + ", " + bp.y.ToString("F1") + ")");
                }

                int vendorBudget = vendorSpots.Count;
                if (vendorBudget > 0)
                {
                    Vec3 v0 = vendorSpots[0].Item1;
                    float d0 = (float)Math.Sqrt(v0.DistanceSquared(vendorAnchor));
                    string log = "NpcSpawn: vendor selection (boss-proximity) — picked " + vendorBudget + ", vendor[0] '" + vendorSpots[0].Item3 + "' " + d0.ToString("F1") + "m from boss";
                    if (vendorBudget >= 2)
                    {
                        float d01 = (float)Math.Sqrt(vendorSpots[1].Item1.DistanceSquared(v0));
                        log += "; vendor[1] '" + vendorSpots[1].Item3 + "' " + d01.ToString("F1") + "m from vendor[0]";
                    }
                    HideoutPeacefulVisitState.Log(log);
                }

                int spawned = 0;
                int rng = 0;

                // Compute camp centroid for facing overrides. Mesh-entity frames (esp. tents)
                // export with arbitrary forward vectors that don't match camp geometry — so we
                // override per-role: vendors face TOWARD centroid (looking at customers passing
                // through), guards/boss/bodyguards face AWAY from centroid (watching outward).
                // Sitters keep entity dir (chair binding handles it). Trainers keep partner-face.
                Vec3 _campCenter = Vec3.Zero;
                if (_wanderTargets.Count > 0)
                {
                    foreach (var t in _wanderTargets) _campCenter += t;
                    _campCenter *= 1f / _wanderTargets.Count;
                }
                HideoutPeacefulVisitState.Log("NpcSpawn: campCenter=(" + _campCenter.x.ToString("F1") +
                    "," + _campCenter.y.ToString("F1") + "," + _campCenter.z.ToString("F1") +
                    ") from " + _wanderTargets.Count + " wander targets");
                Vec2 InwardFrom(Vec3 pos, Vec2 fallback)
                {
                    var d = _campCenter - pos;
                    var v = new Vec2(d.x, d.y);
                    return (v.LengthSquared < 0.0001f) ? fallback : v.Normalized();
                }
                // --- Pass A0: BOSS + 2 BODYGUARDS ---
                // Boss is the highest-tier troop in the culture, placed at the throne/centroid spot.
                // Two bodyguards flank him at ±1.5m perpendicular to his facing direction (left/right
                // shoulder), facing the same way he does. Bodyguard positions are derived from the
                // boss spot at runtime so this works in every hideout without authored sp_bodyguard_*.
                if (bossBudget > 0)
                {
                    var sp = bossSpots[0];
                    // Wave 4.11-fix v25 (2026-05-07): use the registered named chief Hero
                    // (Vargolf the Iron-eyed, Mireborn, etc.) instead of the generic
                    // <culture>_boss CharacterObject when one is registered for this
                    // culture. Falls back to bossChar (highest-tier troop) if no chief.
                    // Player now meets THE chief in the hideout, not a generic boss.
                    Hero chiefHeroForScene = null;
                    try
                    {
                        var chiefReg = BandItPlus.Heroes.BanditChiefRegistry.Instance;
                        if (chiefReg != null) chiefHeroForScene = chiefReg.GetChief(cultureId);
                    }
                    catch { /* defensive — fall through to vanilla bossChar */ }
                    CharacterObject bossCharForSpawn =
                        (chiefHeroForScene != null && chiefHeroForScene.CharacterObject != null)
                            ? chiefHeroForScene.CharacterObject
                            : bossChar;
                    var bossPool = new List<CharacterObject> { bossCharForSpawn };
                    // Boss faces INWARD (toward camp centroid). Hideout boss spots like
                    // `hideout_boss_fight` are authored for the COMBAT scenario (boss vs.
                    // attacker breach direction) — that frame points wrong for peaceful
                    // walk-in. Centroid-toward facing makes the chief look across his
                    // camp, his men, and the path the player approaches by. Bodyguards
                    // inherit this via `fwd = bossDir` below.
                    var bossDir = InwardFrom(sp.Item1, sp.Item2);
                    var bossAgent = SpawnOne(concrete, bossPool, rng++, sp.Item1, bossDir, sp.Item3, NpcRole.Boss);
                    if (bossAgent != null)
                    {
                        spawned++;
                        _bossAgent = bossAgent;
                        _npcRoles[bossAgent] = NpcRole.Boss;
                        ConfigureBoss(bossAgent);
                        HideoutPeacefulVisitState.Log("NpcSpawn: boss = '" + bossCharForSpawn.StringId +
                            "' (tier " + bossTier + ") at '" + sp.Item3 + "'"
                            + (chiefHeroForScene != null ? " | chief Hero: " + (chiefHeroForScene.Name?.ToString() ?? "?") : " | generic boss"));

                        // Bodyguard placement: perpendicular to boss facing, ±1.5m. Both face the
                        // same direction as the boss (guarding outward, not facing him).
                        var fwd = bossDir; // outward from camp center, same as boss
                        const float kBodyguardSpacing = 1.5f;
                        // 90° clockwise perpendicular in XY plane
                        var perpR = new Vec2(-fwd.y, fwd.x);
                        var rightPos = sp.Item1 + new Vec3(perpR.x * kBodyguardSpacing, perpR.y * kBodyguardSpacing, 0f);
                        var leftPos  = sp.Item1 + new Vec3(-perpR.x * kBodyguardSpacing, -perpR.y * kBodyguardSpacing, 0f);

                        for (int bg = 0; bg < 2 && spawned < spawnLimit; bg++)
                        {
                            var bgPos  = (bg == 0) ? rightPos : leftPos;
                            var bgName = (bg == 0) ? "boss_bodyguard_R" : "boss_bodyguard_L";
                            var bgAgent = SpawnOne(concrete, charPool, rng++, bgPos, fwd, bgName, NpcRole.Guard);
                            if (bgAgent != null)
                            {
                                spawned++;
                                _npcRoles[bgAgent] = NpcRole.Guard;
                                _bodyguards.Add(bgAgent); // M7: mark as bodyguard, excluded from greeter pool
                                ConfigureGuard(bgAgent);
                            }
                        }
                    }
                }

                // --- Pass A: GUARDS at guard posts (stay put, weapons drawn, alert idle) ---
                for (int i = 0; i < guardBudget && spawned < spawnLimit; i++, spawned++, rng++)
                {
                    var sp = guardSpots[i];
                    // Use the entity-authored frame direction. sp_guard_* spots have
                    // intentional facing set by the scene designer (e.g. facing the
                    // entrance, watching a path). Overriding with centroid math gave
                    // wrong-direction results in asymmetric scenes.
                    var agent = SpawnOne(concrete, charPool, rng, sp.Item1, sp.Item2, sp.Item3, NpcRole.Guard);
                    if (agent == null) continue;
                    _npcRoles[agent] = NpcRole.Guard;
                    ConfigureGuard(agent);
                }

                // --- Pass B: TRAINERS — pick one general spot, find another nearby, spawn pair ---
                if (trainerBudget >= 2 && generalSpots.Count >= 2)
                {
                    int pairsToMake = trainerBudget / 2;
                    var usedIndices = new HashSet<int>();    // claimed by successful pairs
                    var failedIndices = new HashSet<int>();  // anchors with no valid partner
                    const int kMaxFailedAnchors = 8;         // give up after N isolated anchors
                    const float kMinPairDistSq = 1.5f * 1.5f;
                    const float kMaxPairDistSq = 4.0f * 4.0f;
                    // BUG-FIX #2 (2026-04-30): trainers carry melee weapons and spar with them.
                    // Spawning a sparring pair near a fireplace looks like armed bandits about
                    // to brawl by the cooking fire. Skip any anchor candidate within 3m of any
                    // detected fire — pair will pick another anchor or that pair won't spawn.
                    const float kFireExclusionRadiusSq = 3.0f * 3.0f;
                    bool TooCloseToAnyFire(Vec3 pos)
                    {
                        for (int fi = 0; fi < firePositions.Count; fi++)
                            if (pos.DistanceSquared(firePositions[fi]) < kFireExclusionRadiusSq) return true;
                        return false;
                    }
                    for (int p = 0; p < pairsToMake && spawned + 1 < spawnLimit; )
                    {
                        if (failedIndices.Count >= kMaxFailedAnchors) break;

                        // pick anchor index not already used or marked-failed
                        int anchorIdx = -1;
                        for (int tries = 0; tries < 20 && anchorIdx < 0; tries++)
                        {
                            int candidate = MBRandom.RandomInt(generalSpots.Count);
                            if (usedIndices.Contains(candidate) || failedIndices.Contains(candidate)) continue;
                            // M7-related: trainer near fire is jarring — skip & mark failed.
                            if (TooCloseToAnyFire(generalSpots[candidate].Item1))
                            {
                                failedIndices.Add(candidate);
                                continue;
                            }
                            anchorIdx = candidate;
                        }
                        if (anchorIdx < 0) break;
                        var anchor = generalSpots[anchorIdx];

                        // Find an unused partner in the SPARRING DISTANCE WINDOW (1.5m–4.0m).
                        // Too close → they clip into each other; too far → doesn't read as a pair.
                        // Within window, prefer the closest for a tight conversational pose.
                        int partnerIdx = -1;
                        float bestSq = float.MaxValue;
                        for (int j = 0; j < generalSpots.Count; j++)
                        {
                            // BUG H3 FIX (2026-04-30): also exclude `failedIndices`. Previously
                            // a spot that had been marked failed-as-anchor (no partner in the
                            // distance window) could still be silently picked as a partner here,
                            // producing pairs with one geometrically-isolated member.
                            if (j == anchorIdx || usedIndices.Contains(j) || failedIndices.Contains(j)) continue;
                            var d = generalSpots[j].Item1.DistanceSquared(anchor.Item1);
                            if (d < kMinPairDistSq || d > kMaxPairDistSq) continue;
                            if (d < bestSq) { bestSq = d; partnerIdx = j; }
                        }
                        if (partnerIdx < 0)
                        {
                            // Anchor has no partner in window — mark failed and try a different
                            // one. Bounded by kMaxFailedAnchors so we don't spin forever in scenes
                            // with truly isolated geometry.
                            failedIndices.Add(anchorIdx);
                            continue; // do NOT increment p — we haven't spawned a pair yet
                        }
                        usedIndices.Add(anchorIdx);
                        usedIndices.Add(partnerIdx);
                        var partner = generalSpots[partnerIdx];

                        // spawn both, facing each other (override dir to point at the other)
                        Vec3 a2b = partner.Item1 - anchor.Item1;
                        // BUG M3 FIX (2026-04-30): protect against div-by-zero when the trainer
                        // pair candidates are stacked vertically (Z-only difference). The 3D
                        // distance gate (kMinPairDistSq = 1.5²) treats them as valid neighbors
                        // but the 2D projection has length ≈ 0 → Normalized() produces NaN →
                        // NaN propagates to InitialDirection and corrupts the agent frame. If
                        // the 2D distance is degenerate, fall back to a default east-facing
                        // direction (anchor faces +X, partner faces -X).
                        var ab2D = new Vec2(a2b.x, a2b.y);
                        Vec2 anchorFace, partnerFace;
                        if (ab2D.LengthSquared < 0.01f) // 0.1m squared — vertical stack edge case
                        {
                            anchorFace = new Vec2(1f, 0f);
                            partnerFace = new Vec2(-1f, 0f);
                        }
                        else
                        {
                            anchorFace = ab2D.Normalized();
                            partnerFace = new Vec2(-ab2D.x, -ab2D.y).Normalized();
                        }

                        var aA = SpawnOne(concrete, charPool, rng++, anchor.Item1, anchorFace, anchor.Item3, NpcRole.Trainer);
                        if (aA != null) { spawned++; _npcRoles[aA] = NpcRole.Trainer; ConfigureTrainer(aA); }
                        var aB = SpawnOne(concrete, charPool, rng++, partner.Item1, partnerFace, partner.Item3, NpcRole.Trainer);
                        if (aB != null) { spawned++; _npcRoles[aB] = NpcRole.Trainer; ConfigureTrainer(aB); }
                        p++; // count this successful pair (loop header no longer auto-increments)
                    }
                    // Remove used trainer spots from the wanderer pool so we don't double-spawn
                    var filtered = new List<(Vec3, Vec2, string)>(generalSpots.Count);
                    for (int j = 0; j < generalSpots.Count; j++)
                        if (!usedIndices.Contains(j)) filtered.Add(generalSpots[j]);
                    generalSpots = filtered;
                }

                // --- Pass C: VENDORS at tents/sp_common (stay put, idle, no weapon) ---
                // For TENT entities the mesh-exported forward is arbitrary, so we override
                // to face inward toward camp center. For sp_common we trust the authored
                // frame (the level designer placed those facing the camp's natural traffic
                // path). Detect tent entries by their entity name prefix.
                //
                // (Old clustering-swap block removed 2026-04-30: superseded by boss-proximity
                // vendor selection earlier, which sorts ALL candidates by distance-to-boss
                // before picking the top 2. The swap was a no-op when only 2 vendor candidates
                // existed; now the selection is correct upstream.)
                for (int i = 0; i < vendorBudget && spawned < spawnLimit; i++, spawned++, rng++)
                {
                    var sp = vendorSpots[i];
                    bool isTent = sp.Item3 != null && sp.Item3.ToLowerInvariant().StartsWith("tent");
                    var vendorDir = isTent ? InwardFrom(sp.Item1, sp.Item2) : sp.Item2;
                    var agent = SpawnOne(concrete, charPool, rng, sp.Item1, vendorDir, sp.Item3, NpcRole.Vendor);
                    if (agent == null) continue;
                    _npcRoles[agent] = NpcRole.Vendor;
                    ConfigureVendor(agent);
                    // VENDOR TYPING (2026-04-30): first vendor sells food, second sells gear.
                    // HideoutVendorDialog reads these fields to route the trade dialog.
                    if (i == 0) _foodVendor = agent;
                    else if (i == 1) _gearVendor = agent;
                    // Wave 4.11-fix v54 (2026-05-08): rename the vendor agent to its
                    // authored profile name (e.g. "Halvar Cooper") so the world Alt-key
                    // nameplate, the dialog speaker label, AND the quest journal title
                    // all show the SAME name. Without this, the agent's pool-assigned
                    // random name (e.g. "Ulfric") shows on Alt-hold but profile name
                    // shows in dialog/journal — visible mismatch.
                    try
                    {
                        var cid = MobileParty.MainParty?.CurrentSettlement?.Culture?.StringId;
                        if (string.IsNullOrEmpty(cid)) cid = MobileParty.MainParty?.CurrentSettlement?.OwnerClan?.Culture?.StringId;
                        if (!string.IsNullOrEmpty(cid))
                        {
                            var profile = (i == 0)
                                ? BandItPlus.Cultures.VendorProfiles.GetFood(cid)
                                : BandItPlus.Cultures.VendorProfiles.GetGear(cid);
                            if (profile != null && !string.IsNullOrEmpty(profile.Name))
                            {
                                // v55 fix (2026-05-08): Agent._name is a TextObject,
                                // NOT a string. v54 was passing a raw string and the
                                // reflected SetValue threw silently (caught by the
                                // outer try/catch). Wrap in TextObject ctor so the
                                // type matches the field. Confirmed via reflection:
                                // `TaleWorlds.Localization.TextObject _name`.
                                var nameField = typeof(Agent).GetField("_name",
                                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                                nameField?.SetValue(agent, new TaleWorlds.Localization.TextObject(profile.Name));
                                HideoutPeacefulVisitState.Log("NpcSpawn: vendor[" + i + "] renamed to '" + profile.Name + "' (was random pool)");
                            }
                        }
                    }
                    catch (Exception nx)
                    {
                        HideoutPeacefulVisitState.Log("Vendor rename fail: " + nx.Message);
                    }
                }
                if (_foodVendor != null || _gearVendor != null)
                    HideoutPeacefulVisitState.Log("NpcSpawn: vendor types assigned — food=" + (_foodVendor != null ? "yes" : "no") + ", gear=" + (_gearVendor != null ? "yes" : "no"));

                // --- Wave 4.12 (2026-05-10): SLAVER vendor (3rd NPC, gated by chief Tier-3) ---
                // Conditions:
                //   - Chief trust >= 3 for current hideout's culture
                //   - SlaverProfiles entry exists for that culture
                //   - Gear vendor was successfully spawned (used as position anchor; offset 1.5m)
                // v1 uses a bare offset spawn; Phase 7 scene-tunes the position and adds prop authoring.
                try
                {
                    var slaverCid = MobileParty.MainParty?.CurrentSettlement?.Culture?.StringId;
                    if (string.IsNullOrEmpty(slaverCid))
                        slaverCid = MobileParty.MainParty?.CurrentSettlement?.OwnerClan?.Culture?.StringId;
                    var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                    int chiefTrust = (bdm != null && !string.IsNullOrEmpty(slaverCid))
                        ? bdm.GetCultureTrust(slaverCid) : 0;
                    bool hasSlaverProfile = !string.IsNullOrEmpty(slaverCid)
                        && BandItPlus.Cultures.SlaverProfiles.ByCultureId.ContainsKey(slaverCid);

                    // Wave 4.12-fix9 (2026-05-11): slaver is night-only. The slave trade
                    // operates under cover of darkness — narratively it makes sense, and
                    // mechanically it gives players a reason to time hideout visits at
                    // dusk. Window: hour 20 (8pm) through hour 6 (6am).
                    float slaverHour = CampaignTime.Now.CurrentHourInDay;
                    bool isNightTime = slaverHour < 6f || slaverHour >= 20f;
                    if (chiefTrust >= 3 && hasSlaverProfile && _bossAgent != null && _bossAgent.IsActive() && spawned < spawnLimit && isNightTime)
                    {
                        // Wave 4.12-fix5 (2026-05-11): slaver moved from -5m to -10m behind
                        // boss. User wants slaver clearly separated from chief, not just
                        // outside the 3m TickBossInteraction radius. 10m is well outside
                        // any chief-cluster ambiguity AND outside the boss-proximity vendor
                        // selection range (which picks closest sp_* tags).
                        var gpos = _bossAgent.Position;
                        var slaverPos = new Vec3(gpos.x, gpos.y - 10.0f, gpos.z);
                        var slaverDir = new Vec2(0, 1);

                        var sagent = SpawnOne(concrete, charPool, rng, slaverPos, slaverDir, "slaver_spawn", NpcRole.Vendor);
                        if (sagent != null)
                        {
                            _npcRoles[sagent] = NpcRole.Vendor;
                            ConfigureVendor(sagent);
                            _slaverVendor = sagent;
                            spawned++; rng++;

                            // Rename per profile (same TextObject reflection pattern as v55 food/gear rename).
                            try
                            {
                                var sprofile = BandItPlus.Cultures.SlaverProfiles.ByCultureId[slaverCid];
                                if (!string.IsNullOrEmpty(sprofile.Name))
                                {
                                    var nameField = typeof(Agent).GetField("_name",
                                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                                    nameField?.SetValue(sagent, new TaleWorlds.Localization.TextObject(sprofile.Name));
                                    HideoutPeacefulVisitState.Log("NpcSpawn: slaver vendor spawned for " + slaverCid + " as '" + sprofile.Name + "'");
                                }
                            }
                            catch (Exception srx)
                            {
                                HideoutPeacefulVisitState.Log("Slaver rename fail: " + srx.GetType().Name + ": " + srx.Message);
                            }
                        }
                        else
                        {
                            HideoutPeacefulVisitState.Log("NpcSpawn: slaver SpawnOne returned null for " + slaverCid);
                        }
                    }
                    else
                    {
                        HideoutPeacefulVisitState.Log("NpcSpawn: slaver skipped — chief trust=" + chiefTrust
                            + " hasProfile=" + hasSlaverProfile + " bossAgent=" + (_bossAgent != null)
                            + " isNight=" + isNightTime + " (hour=" + slaverHour.ToString("F1") + ")");
                    }
                }
                catch (Exception sex)
                {
                    HideoutPeacefulVisitState.Log("Slaver spawn EXCEPTION: " + sex.GetType().Name + ": " + sex.Message);
                }

                // BUG M4 FIX (2026-04-30): Sleeper pass removed. `sleeperBudget` is hard-coded to 0
                // (sleepers were disabled because act_dungeon_prisoner_sleep_cycle assumes pelvis at
                // ground Z but our spawn points have varying Y, producing mid-air sleep poses).
                // The loop was dead code — `for (int i = 0; i < 0; ...)` never executes — but it
                // referenced the obsolete logic and confused readers. To re-enable in the future,
                // change sleeperBudget to non-zero AND solve the ground-Z snap (see ConfigureSleeper).

                // --- Pass C2: SITTERS at chair/bench/sit spots (stationary, near rest areas) ---
                // For now they STAND at the chair location instead of sitting on it — playing the
                // sit-down animation requires SetActionChannel which has the same callback risk
                // pattern as WieldInitialWeapons(WithAnimation) and is deferred until we confirm
                // the rest of the role system is stable in-game.
                int chairBindCount = 0;
                for (int i = 0; i < sitterBudget && spawned < spawnLimit; i++, spawned++, rng++)
                {
                    var sp = sitterSpots[i];
                    var agent = SpawnOne(concrete, charPool, rng, sp.Item1, sp.Item2, sp.Item3, NpcRole.Sitter);
                    if (agent == null) continue;
                    _npcRoles[agent] = NpcRole.Sitter;
                    ConfigureSitter(agent);

                    // Layer 2: bind the agent to the chair's StandingPoint via UseGameObject.
                    // This is what makes them ACTUALLY SIT ON the chair geometry instead of
                    // just playing a ground-sit pose nearby. preferenceIndex=0 picks the first
                    // (and usually only) sit-preference defined on the StandingPoint.
                    var usable = (i < sitterUsables.Count) ? sitterUsables[i] : null;
                    if (usable != null)
                    {
                        try
                        {
                            // SAFETY GATE: only bind to chair if the agent actually has a
                            // wielded weapon. Without this check, archer bandits (or any troop
                            // whose equipment has no item the wield call could pick) would bind
                            // with nothing wielded → AnimationPoint.SetAgentItemsVisibility
                            // teardown NRE → game crash on TAB-leave.
                            // GetWieldedItemIndex returns EquipmentIndex.None when nothing is
                            // wielded; valid indices are >=0.
                            var wieldedIdx = agent.GetPrimaryWieldedItemIndex();
                            if ((int)wieldedIdx < 0)
                            {
                                HideoutPeacefulVisitState.Log("NpcSpawn: SKIP chair-bind for sitter at '" + sp.Item3 + "' — no wielded item (would crash teardown)");
                            }
                            else
                            {
                                // CHAIR-BIND RE-ENABLED 2026-04-30 (dynamic-patch path):
                                // PeacefulNoAnimationPointVisibilityPatch is now installed
                                // dynamically in OnMissionStarted (and removed in OnMissionEnded)
                                // so the IL trampoline never exists outside the peaceful mission
                                // window. Encyclopedia preview is unaffected. Chair-bind
                                // teardown NRE is protected by the active prefix. Sitters
                                // actually sit on the chair geometry.
                                agent.UseGameObject(usable, 0);
                                chairBindCount++;
                            }
                        }
                        catch (Exception bindEx)
                        {
                            HideoutPeacefulVisitState.Log("NpcSpawn: chair-bind for sitter at '" + sp.Item3 + "' FAILED: " + bindEx.Message);
                        }
                    }
                }
                if (chairBindCount > 0)
                    HideoutPeacefulVisitState.Log("NpcSpawn: bound " + chairBindCount + " sitters to chair UsableMissionObjects");

                // --- Pass C2: CAPTIVES at captive/prisoner/cage spots (slaver culture only, 2026-06-05 Wave 4.13) ---
                // Captives are bound NPCs at scene-authored restraint positions. They stand
                // stationary with default equipment — visual location reads as "imprisoned"
                // even without a custom bound-pose animation. Per-culture gate enforced via
                // captiveBudget (set to 0 for non-slaver cultures upstream). No wander tick,
                // no interaction, no dialog — purely environmental narrative.
                int captivesSpawned = 0;
                for (int i = 0; i < captiveBudget && spawned < spawnLimit; i++)
                {
                    var sp = captiveSpots[i];
                    rng++;
                    var agent = SpawnOne(concrete, charPool, rng, sp.Item1, sp.Item2, sp.Item3, NpcRole.Captive);
                    if (agent == null) continue;
                    spawned++;
                    captivesSpawned++;
                    _npcRoles[agent] = NpcRole.Captive;
                }
                if (captivesSpawned > 0)
                    HideoutPeacefulVisitState.Log("NpcSpawn: Pass C2 captives — spawned " + captivesSpawned + " captives at " + captiveSpots.Count + " spots (culture=" + cultureId + ")");

                // --- Pass D: WANDERERS at remaining general spots ---
                // BUG-FIX #3 (2026-04-30): apply a 1.5m min-distance check ONLY to wanderers.
                // Hideout scenes often have dense `patrol_point` clusters where 5+ markers sit
                // within a meter of each other in one yard. Without this check wanderers spawn
                // ON TOP of each other (user-reported "units inside other units"). Stationary
                // roles (boss/sitter/vendor/guard) are NOT subject to this check — they spawn
                // at scene-authored unique markers which are dedup'd at classify time, and
                // applying min-distance there killed boss/sitter placement in earlier attempts.
                const float kWandererMinSepSq = 1.5f * 1.5f;
                var wandererPositions = new List<Vec3>(generalSpots.Count);
                int wandererSkipped = 0;
                for (int i = 0; i < generalSpots.Count && spawned < spawnLimit; i++)
                {
                    var sp = generalSpots[i];
                    bool tooClose = false;
                    for (int j = 0; j < wandererPositions.Count; j++)
                    {
                        if (sp.Item1.DistanceSquared(wandererPositions[j]) < kWandererMinSepSq)
                        {
                            tooClose = true;
                            break;
                        }
                    }
                    if (tooClose) { wandererSkipped++; continue; }
                    rng++;
                    var agent = SpawnOne(concrete, charPool, rng, sp.Item1, sp.Item2, sp.Item3, NpcRole.Wanderer);
                    if (agent == null) continue;
                    spawned++;
                    wandererPositions.Add(sp.Item1);
                    _npcRoles[agent] = NpcRole.Wanderer;
                    ConfigureWanderer(agent);
                }
                if (wandererSkipped > 0)
                    HideoutPeacefulVisitState.Log("NpcSpawn: wanderers skipped " + wandererSkipped + " general spots within 1.5m of an existing wanderer (anti-overlap)");

                HideoutPeacefulVisitState.Log("NpcSpawn: spawned " + spawned + " NPCs — " +
                    "boss=" + CountRole(NpcRole.Boss) +
                    " guards=" + CountRole(NpcRole.Guard) +
                    " trainers=" + CountRole(NpcRole.Trainer) +
                    " vendors=" + CountRole(NpcRole.Vendor) +
                    " sitters=" + CountRole(NpcRole.Sitter) +
                    " sleepers=" + CountRole(NpcRole.Sleeper) +
                    " wanderers=" + CountRole(NpcRole.Wanderer) +
                    " (living hideout)");

                // Inject the Alt-key nameplate behavior. This pushes a context onto
                // SandBox.MissionNameMarkerFactory and registers BanditCampNameMarkerProvider,
                // which feeds Boss + Vendor agents into the SAME vanilla nameplate widget the
                // village mission uses for Notables. No custom UI — vanilla paints the labels.
                try
                {
                    concrete.AddMissionBehavior(new HideoutAltLabelBehavior());
                    HideoutPeacefulVisitState.Log("NpcSpawn: HideoutAltLabelBehavior injected (native Alt-nameplate registered)");
                }
                catch (Exception altEx)
                {
                    HideoutPeacefulVisitState.Log("NpcSpawn: failed to inject HideoutAltLabelBehavior: " + altEx.Message);
                }

                // Wave 4.12-fix v77 (2026-05-12): replaced v75/v76 modal popup with a
                // real GauntletLayer banner anchored to the top of the screen via
                // CampEntryBannerMissionView. Non-modal, auto-fading, uses vanilla
                // brushes via the GUI/Prefabs/CampEntryBanner.xml prefab — matches
                // the visual quality of town-entry overlays.
                try
                {
                    var entrySettlement = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement;
                    string campName = entrySettlement?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_hideoutvisitnpcspawnbehavior_002", "A Bandit Camp");
                    string cid = entrySettlement?.Culture?.StringId ?? "";

                    BandItPlus.Cultures.CultureProfile chiefProfile = null;
                    if (!string.IsNullOrEmpty(cid))
                        BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(cid, out chiefProfile);
                    string chiefName = chiefProfile?.ChiefName ?? "the chief";
                    string chiefTitle = chiefProfile?.ChiefTitle ?? "Bandit Chief";
                    string subtitle = chiefName + " — " + chiefTitle;

                    // v81: serialize the hideout's owning clan banner for the
                    // BannerTableauWidget sprite. Empty string => widget renders nothing.
                    string bannerCode = "";
                    try
                    {
                        var ownerClan = entrySettlement?.OwnerClan;
                        bannerCode = ownerClan?.Banner?.Serialize() ?? "";
                    }
                    catch { }

                    // v92.1 (2026-05-13): top transient banner DISABLED per user request.
                    // The persistent left info panel below already shows camp identity +
                    // chief + culture, so the duplicated top banner was redundant.
                    // Original line kept for reference; uncomment to re-enable.
                    // concrete.AddMissionBehavior(new BandItPlus.UI.CampEntryBannerMissionView(campName, subtitle, bannerCode));

                    // v186 (2026-05-23): BanditCampPanel replaces old CampInfoPanel +
                    // CampTopLeft. Parameterless — reads HideoutPeacefulVisitState.TargetHideout.
                    try
                    {
                        concrete.AddMissionBehavior(new BandItPlus.UI.BanditCampPanelMissionView());
                        HideoutPeacefulVisitState.Log("v186: BanditCampPanelMissionView injected for " + campName);
                    }
                    catch (Exception panelEx)
                    {
                        HideoutPeacefulVisitState.Log("v186 panel inject fail: "
                            + panelEx.GetType().Name + ": " + panelEx.Message);
                    }

                    // v123 (2026-05-15): first-visit cinematic intro. Shown ONCE per
                    // hideout when the culture has an authored HideoutCinemaIntro.
                    //
                    // 2026-06-03: gate now uses the dedicated _cinematicShownHideouts
                    // dict (Dictionary<string, double>, persists via proven SyncData
                    // type) instead of GetCampVisitCount. Visit-count gate was failing
                    // because Dictionary<string, int> wasn't persisting (no
                    // ConstructContainerDefinition registered for that type).
                    try
                    {
                        var cineBdm = TaleWorlds.CampaignSystem.Campaign.Current
                            ?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                        string cineSettlementId = entrySettlement?.StringId ?? "";
                        bool cineAlreadyShown = cineBdm != null
                            && !string.IsNullOrEmpty(cineSettlementId)
                            && cineBdm.HasShownCinematicForHideout(cineSettlementId);
                        BandItPlus.Cultures.CultureProfile cineProfile = null;
                        if (!string.IsNullOrEmpty(cid))
                            BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(cid, out cineProfile);
                        string cineText = cineProfile?.HideoutCinemaIntro;
                        if (!cineAlreadyShown && !string.IsNullOrEmpty(cineText))
                        {
                            concrete.AddMissionBehavior(new BandItPlus.UI.HideoutCinemaIntroMissionView(cineText, campName));
                            if (cineBdm != null && !string.IsNullOrEmpty(cineSettlementId))
                                cineBdm.MarkCinematicShown(cineSettlementId);
                            HideoutPeacefulVisitState.Log("v133: HideoutCinemaIntro injected (first visit) for "
                                + campName + " (" + cid + ") — marked as shown");
                        }
                        else
                        {
                            HideoutPeacefulVisitState.Log("v123: HideoutCinemaIntro skipped — alreadyShown="
                                + cineAlreadyShown + " hasText=" + (!string.IsNullOrEmpty(cineText)));
                        }
                    }
                    catch (Exception cineEx)
                    {
                        HideoutPeacefulVisitState.Log("v123 HideoutCinemaIntro inject fail: "
                            + cineEx.GetType().Name + ": " + cineEx.Message);
                    }

                    // v147: per-culture camp weather removed per user request.

                    // v94.1 (2026-05-13): top-left composite DISABLED — Quick Actions moved
                    // into the main info panel below Day/Time per user request, and the
                    // ACTIVE TASKS section was redundant with the main panel's QUESTS section.
                    // Original line kept for reference; uncomment to re-enable.
                    // concrete.AddMissionBehavior(new BandItPlus.UI.CampTopLeftMissionView(entrySettlement));
                    HideoutPeacefulVisitState.Log("v92.1: top banner disabled, left info panel active for "
                        + campName + " (" + cid + ", chief=" + chiefName + ")");
                }
                catch (Exception bannerEx)
                {
                    HideoutPeacefulVisitState.Log("v77 scene-entry banner fail: "
                        + bannerEx.GetType().Name + ": " + bannerEx.Message);
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("NpcSpawn EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------- Spawn helpers ----------

        // Reflection target for setting unique per-agent names. Bannerlord's Agent.Name is a
        // read-only property that reads from a private TextObject _name field. The standard
        // AgentBuildData has no Name() setter and the proper alternative (custom IAgentOriginBase)
        // is heavy. Direct private-field assignment is the lightweight equivalent: same end
        // result, no allocation of origin objects per spawn.
        private static readonly FieldInfo _agentNameField =
            typeof(Agent).GetField("_name", BindingFlags.NonPublic | BindingFlags.Instance);

        // Per-hideout used-name set. Reset on each peaceful mission start so two visits to
        // the same hideout get different name draws. Case-insensitive comparer keeps "Borek"
        // and "borek" treated as the same entry.
        private readonly HashSet<string> _usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Curated bandit name pool — pan-Calradian flavor. Bandit clans recruit from any culture,
        // so a mix of Vlandian/Sturgian/Battanian/Imperial/Aserai/Khuzait roots reads natural.
        // Plus a few rough-nickname epithets ("Black-Hand", "One-Ear") that fit a hideout vibe.
        // Pool size 80 — comfortably above the 43-NPC cap per hideout. If somehow exhausted,
        // AssignUniqueName falls back to a numeric "Bandit_N" tag.
        private static readonly string[] _banditNamePool = new[]
        {
            // Vlandian / western
            "{=bp_hideoutvisitnpcspawnbehavior_003}Borek","{=bp_hideoutvisitnpcspawnbehavior_004}Dietrich","{=bp_hideoutvisitnpcspawnbehavior_005}Edmund","{=bp_hideoutvisitnpcspawnbehavior_006}Ferand","{=bp_hideoutvisitnpcspawnbehavior_007}Gunther",
            "{=bp_hideoutvisitnpcspawnbehavior_008}Hagen","{=bp_hideoutvisitnpcspawnbehavior_009}Jorund","{=bp_hideoutvisitnpcspawnbehavior_010}Kaspar","{=bp_hideoutvisitnpcspawnbehavior_011}Lothar","{=bp_hideoutvisitnpcspawnbehavior_012}Magnard",
            "{=bp_hideoutvisitnpcspawnbehavior_013}Norbert","{=bp_hideoutvisitnpcspawnbehavior_014}Otho","{=bp_hideoutvisitnpcspawnbehavior_015}Pieter","{=bp_hideoutvisitnpcspawnbehavior_016}Roland","{=bp_hideoutvisitnpcspawnbehavior_017}Sigwald",
            "{=bp_hideoutvisitnpcspawnbehavior_018}Tancred","{=bp_hideoutvisitnpcspawnbehavior_019}Ulric","{=bp_hideoutvisitnpcspawnbehavior_020}Volsung","{=bp_hideoutvisitnpcspawnbehavior_021}Walther","{=bp_hideoutvisitnpcspawnbehavior_022}Renaud",
            // Sturgian / northern
            "{=bp_hideoutvisitnpcspawnbehavior_023}Asger","{=bp_hideoutvisitnpcspawnbehavior_024}Brunwulf","{=bp_hideoutvisitnpcspawnbehavior_025}Caedric","{=bp_hideoutvisitnpcspawnbehavior_026}Dagfinn","{=bp_hideoutvisitnpcspawnbehavior_027}Egil",
            "{=bp_hideoutvisitnpcspawnbehavior_028}Geirolf","{=bp_hideoutvisitnpcspawnbehavior_029}Hrothgar","{=bp_hideoutvisitnpcspawnbehavior_030}Kettil","{=bp_hideoutvisitnpcspawnbehavior_031}Leif","{=bp_hideoutvisitnpcspawnbehavior_032}Magnus",
            "{=bp_hideoutvisitnpcspawnbehavior_033}Njal","{=bp_hideoutvisitnpcspawnbehavior_034}Olek","{=bp_hideoutvisitnpcspawnbehavior_035}Ragnar","{=bp_hideoutvisitnpcspawnbehavior_036}Skoll","{=bp_hideoutvisitnpcspawnbehavior_037}Thorin",
            "{=bp_hideoutvisitnpcspawnbehavior_038}Ulfric","{=bp_hideoutvisitnpcspawnbehavior_039}Vidar","{=bp_hideoutvisitnpcspawnbehavior_040}Hakon","{=bp_hideoutvisitnpcspawnbehavior_041}Ingvar","{=bp_hideoutvisitnpcspawnbehavior_042}Bjorn",
            // Battanian / Imperial / Aserai / Khuzait mix
            "{=bp_hideoutvisitnpcspawnbehavior_043}Aengus","{=bp_hideoutvisitnpcspawnbehavior_044}Brennus","{=bp_hideoutvisitnpcspawnbehavior_045}Caedmon","{=bp_hideoutvisitnpcspawnbehavior_046}Donnchadh","{=bp_hideoutvisitnpcspawnbehavior_047}Eoghan",
            "{=bp_hideoutvisitnpcspawnbehavior_048}Andros","{=bp_hideoutvisitnpcspawnbehavior_049}Demos","{=bp_hideoutvisitnpcspawnbehavior_050}Galen","{=bp_hideoutvisitnpcspawnbehavior_051}Petros","{=bp_hideoutvisitnpcspawnbehavior_052}Zenon",
            "{=bp_hideoutvisitnpcspawnbehavior_053}Hadi","{=bp_hideoutvisitnpcspawnbehavior_054}Hisham","{=bp_hideoutvisitnpcspawnbehavior_055}Mahmud","{=bp_hideoutvisitnpcspawnbehavior_056}Naseem","{=bp_hideoutvisitnpcspawnbehavior_057}Tarek",
            "{=bp_hideoutvisitnpcspawnbehavior_058}Yusuf","{=bp_hideoutvisitnpcspawnbehavior_059}Zaid","{=bp_hideoutvisitnpcspawnbehavior_060}Batu","{=bp_hideoutvisitnpcspawnbehavior_061}Chinua","{=bp_hideoutvisitnpcspawnbehavior_062}Khasar",
            // Rough hideout epithets
            "{=bp_hideoutvisitnpcspawnbehavior_063}Black-Hand","{=bp_hideoutvisitnpcspawnbehavior_064}One-Ear","{=bp_hideoutvisitnpcspawnbehavior_065}Iron-Jaw","{=bp_hideoutvisitnpcspawnbehavior_066}Red-Beard","{=bp_hideoutvisitnpcspawnbehavior_067}Long-Knife",
            "{=bp_hideoutvisitnpcspawnbehavior_068}Stone-Fist","{=bp_hideoutvisitnpcspawnbehavior_069}The Wolf","{=bp_hideoutvisitnpcspawnbehavior_070}Cutter","{=bp_hideoutvisitnpcspawnbehavior_071}Crooked-Eye","{=bp_hideoutvisitnpcspawnbehavior_072}Bone-Saw",
            "{=bp_hideoutvisitnpcspawnbehavior_073}The Bear","{=bp_hideoutvisitnpcspawnbehavior_074}Ash-Tooth","{=bp_hideoutvisitnpcspawnbehavior_075}Grim","{=bp_hideoutvisitnpcspawnbehavior_076}Hollow","{=bp_hideoutvisitnpcspawnbehavior_077}Scab",
            "{=bp_hideoutvisitnpcspawnbehavior_078}Hawk-Nose","{=bp_hideoutvisitnpcspawnbehavior_079}Ravenmark","{=bp_hideoutvisitnpcspawnbehavior_080}Spider","{=bp_hideoutvisitnpcspawnbehavior_081}Drowned","{=bp_hideoutvisitnpcspawnbehavior_082}Salt-Tongue"
        };

        // Set a unique random name on the agent's private _name. Picks from the pool, retries
        // up to 24 times to dodge an already-used name, falls back to "Bandit_N" only if the
        // pool is somehow exhausted (>80 NPCs in one hideout — never in practice).
        private void AssignUniqueName(Agent agent, CharacterObject character, NpcRole role = NpcRole.Wanderer)
        {
            try
            {
                if (_agentNameField == null) return;

                string name = null;

                // Wave 4.8b: the Boss (hideout chief) gets the culture's authored ChiefName
                // (e.g., "Skarvac of the High Pass" for mountain_bandits). Falls through to
                // the random pool below if no profile/ChiefName is found.
                if (role == NpcRole.Boss)
                {
                    var hideout = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement;
                    string cultureId = hideout?.Culture?.StringId
                        ?? hideout?.OwnerClan?.Culture?.StringId;
                    if (!string.IsNullOrEmpty(cultureId)
                        && BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out var profile)
                        && profile != null && !string.IsNullOrEmpty(profile.ChiefName))
                    {
                        name = profile.ChiefName;
                    }
                }

                for (int tries = 0; tries < 24 && name == null; tries++)
                {
                    var candidate = _banditNamePool[MBRandom.RandomInt(_banditNamePool.Length)];
                    if (!_usedNames.Contains(candidate)) { name = candidate; break; }
                }
                if (name == null)
                {
                    // BUG M5 FIX (2026-04-30): cap the do-while at 1000 iterations to prevent
                    // a hang if `_usedNames` is corrupted (e.g. cross-mission state pollution)
                    // such that "Bandit_1" through "Bandit_<huge>" are all marked used. With cap,
                    // the loop falls through to a GUID-suffixed name guaranteed unique.
                    int n = _usedNames.Count;
                    int safetyTries = 0;
                    do { name = "Bandit_" + (++n); }
                    while (_usedNames.Contains(name) && ++safetyTries < 1000);
                    if (safetyTries >= 1000)
                    {
                        name = "Bandit_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                        HideoutPeacefulVisitState.Log("AssignUniqueName: pool + numeric suffix BOTH exhausted (likely corrupt state) — fell back to GUID name '" + name + "'");
                    }
                }
                _usedNames.Add(name);
                _agentNameField.SetValue(agent, new TextObject(name));
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("AssignUniqueName fail: " + ex.Message); }
        }

        // Map NpcRole → vanilla ActionSetCode suffix. The suffix selects which engine-driven
        // idle/walk/sit animation set the agent uses (per Path A of the actionset research doc).
        private static string SuffixForRole(NpcRole role)
        {
            switch (role)
            {
                case NpcRole.Boss:    return ActionSetCode.LordActionSetSuffix;
                case NpcRole.Guard:   return ActionSetCode.GuardSuffix;
                case NpcRole.Trainer: return ActionSetCode.WarriorActionSetSuffix;
                case NpcRole.Vendor:  return ActionSetCode.SellerSuffix;
                case NpcRole.Sitter:  return ActionSetCode.BeggarSuffix;
                case NpcRole.Sleeper: return ActionSetCode.BeggarSuffix;  // closest to relaxed/idle
                case NpcRole.Wanderer: default:
                    return ActionSetCode.HideoutBanditActionSetSuffix;
            }
        }

        // Single-agent spawn. Returns the spawned Agent or null on failure. All role-specific
        // configuration (weapons, wander assignment) happens in the caller after.
        // The role param drives ActionSet selection — engine then plays the appropriate
        // idle/walk animations automatically without us calling SetActionChannel.
        private Agent SpawnOne(Mission concrete, List<CharacterObject> charPool, int seed,
            Vec3 pos, Vec2 dir, string spotName, NpcRole role)
        {
            try
            {
                var npcChar = charPool[seed % charPool.Count];
                var npcEquip = npcChar.RandomBattleEquipment ?? npcChar.Equipment;
                if (npcEquip == null)
                {
                    // BUG M6 FIX (2026-04-30): some custom mod CharacterObjects have neither
                    // RandomBattleEquipment nor Equipment populated (e.g. equipment XML failed to
                    // parse, or a barebones placeholder character). GetBodyProperties(null, ...)
                    // throws NRE in vanilla. Skip this spawn cleanly with a log line; caller
                    // continues to next iteration without losing the entire pass.
                    HideoutPeacefulVisitState.Log("SpawnOne[" + role + "] '" + spotName + "' SKIP — " + npcChar.StringId + " has no equipment");
                    return null;
                }
                var npcBody = npcChar.GetBodyProperties(npcEquip, seed);

                var data = new AgentBuildData(npcChar)
                    .Equipment(npcEquip)
                    .BodyProperties(npcBody)
                    .IsFemale(npcChar.IsFemale)
                    .InitialPosition(in pos)
                    .InitialDirection(in dir)
                    .NoHorses(true)
                    .Controller(AgentControllerType.AI);
                if (concrete.PlayerTeam != null) data = data.Team(concrete.PlayerTeam);

                // Path A — apply role-specific Monster (cloned with overridden ActionSetCode).
                // The engine reads Monster.ActionSetCode at agent init and resolves to a
                // vanilla-registered MBActionSet, which drives all idle/walk transitions.
                var roleMonster = RoleActionSets.GetMonsterForSuffix(SuffixForRole(role), npcChar.IsFemale);
                if (roleMonster != null) data = data.Monster(roleMonster);

                var agent = concrete.SpawnAgent(data, false);
                if (agent != null)
                {
                    AssignUniqueName(agent, npcChar, role);
                    _spawnedPositions.Add(pos);

                    // NOTE: LookDirection-lock approach REVERTED on 2026-04-30. Setting
                    // IsLookDirectionLocked=true on stationary roles broke (a) the greeter
                    // dispatch — locked guards couldn't rotate to face the player or open
                    // conversation UI — and (b) idle/gesture animations that rely on the
                    // head-track system. We rely solely on InitialDirection for spawn
                    // facing; vanilla villagers also drift over time, which reads as a
                    // living camp rather than a frozen-doll pose anyway.

                    // Diagnostic — captured per spawn so we can confirm pos+dir reach the
                    // engine correctly. Cheap (1 line per agent, ~43 lines per mission).
                    HideoutPeacefulVisitState.Log("SpawnOne[" + role + "] '" + spotName +
                        "' pos=(" + pos.x.ToString("F1") + "," + pos.y.ToString("F1") + ")" +
                        " dir=(" + dir.x.ToString("F2") + "," + dir.y.ToString("F2") + ")");
                }
                return agent;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("NpcSpawn: SpawnOne at '" + spotName + "' FAILED: " + ex.Message);
                return null;
            }
        }

        // CRASH-SAFE NOTE: the previous version of these helpers called WieldInitialWeapons
        // and SetWatchState immediately after SpawnAgent. That caused a delayed native crash
        // (no managed exception, no crash dump) ~3 minutes after entry — most likely because
        // those APIs queue behavior callbacks that expect combat-AI types not present in the
        // village-mission stack we use for peaceful hideouts. We now rely purely on
        // equipment + position + facing + speed limit for visual role identity. All of those
        // are pure data, no behavior callbacks, no crash risk.

        // ARCHITECTURAL NOTE — animation reverted on 2026-04-27 after 3 iterations failed.
        // Attempts: (1) no flags → invisible (overridden by AI default-idle, priority 0)
        //           (2) ignorePriority:true → still invisible
        //           (3) ignorePriority:true + anf_keep → frozen mid-pose (sit-transition end frame
        //               held forever; beggar-idle locked partial; warrior-loop one-shot freeze).
        // Per superpowers:systematic-debugging Phase 4.5 (3+ fixes failed = wrong architecture):
        // SetActionChannel is the wrong abstraction for sustained idle poses on AI agents.
        // Vanilla does NOT work this way — civilians/villagers get an ActionSet via
        // LocationCharacter XML, and the engine selects per-tick which animation to play.
        // We're calling a one-shot animation API and trying to make it look like a state machine.
        // PROPER PATH: custom ActionSet per role (or use vanilla as_human_villager for non-combat
        // roles). That is research-sized — separate task. For now: revert to default standing
        // idle, rely on POSITION + EQUIPMENT + FACING for visual role identity. Trainers still
        // get melee-wielded via WieldInitialWeapons(Instant) — that one worked reliably.

        // BOSS: highest-tier troop at the throne/centroid spot. Distinct equipment makes him
        // visually the chief; default standing pose. Tracked in _bossAgent for Feature C dialog.
        private static void ConfigureBoss(Agent agent)
        {
            try
            {
                agent.SetMaximumSpeedLimit(0.5f, true);
                ApplyRoleIdle(agent, _bossIdleClips); // commanding pose / staff stand
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("ConfigureBoss fail: " + ex.Message); }
        }

        // GUARD: holds post at sp_guard_* (or as bodyguard flanking the boss). Position +
        // standing-at-gate is what reads as guard.
        private static void ConfigureGuard(Agent agent)
        {
            try
            {
                agent.SetMaximumSpeedLimit(0.6f, true);
                ApplyRoleIdle(agent, _guardIdleClips); // mix of stand variations + wall lean + fence
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("ConfigureGuard fail: " + ex.Message); }
        }

        // TRAINER: paired with another trainer, facing each other, melee drawn. Wield uses
        // Instant variant (data-only path, no animation queue) — confirmed safe and visible.
        // Combat-stance animation reverted; the wielded sword + face-to-face geometry carries
        // the "sparring partners" reading on its own.
        private static void ConfigureTrainer(Agent agent)
        {
            try
            {
                agent.SetMaximumSpeedLimit(0.5f, true);
                agent.WieldInitialWeapons(Agent.WeaponWieldActionType.Instant,
                    Equipment.InitialWeaponEquipPreference.MeleeForMainHand);
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("ConfigureTrainer fail: " + ex.Message); }
        }

        // VENDOR: stationary at sp_common. Default standing pose reads as merchant/idler.
        private static void ConfigureVendor(Agent agent)
        {
            try
            {
                agent.SetMaximumSpeedLimit(0.4f, true);
                ApplyRoleIdle(agent, _vendorIdleClips); // musician calm / cheerful / standard stand
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("ConfigureVendor fail: " + ex.Message); }
        }

        // SITTER: placed at chair/bench/sit spots. NOTE: they currently STAND at the chair
        // location instead of sitting — proper sitting requires the agent to be hooked up to
        // the scene's UseObject (chair entity), which is a vanilla LocationCharacter feature
        // we're not currently using. The position alone still distributes NPCs around resting
        // areas of the camp, which is better than nothing.
        // Apply ONE random clip from the role's idle pool as the agent's standing-idle.
        // Channel 0 (full body) with ignorePriority overrides Path A's default stand pose
        // for visual variety. No anf_keep — clips are cyclic idles that loop natively.
        // Up to 3 random tries to find a resolved clip in case some pool entries returned
        // act_none in this Bannerlord install.
        private static void ApplyRoleIdle(Agent agent, ActionIndexCache[] pool)
        {
            if (agent == null || pool == null || pool.Length == 0) return;
            ActionIndexCache clip = ActionIndexCache.act_none;
            for (int i = 0; i < 3; i++)
            {
                var c = pool[MBRandom.RandomInt(pool.Length)];
                if (c.Index >= 0) { clip = c; break; }
            }
            if (clip.Index < 0) return;
            try
            {
                agent.SetActionChannel(0, clip,
                    ignorePriority: true,
                    additionalFlags: (AnimFlags)0,
                    blendWithNextActionFactor: 0f,
                    actionSpeed: 1f,
                    blendInPeriod: 0.4f,
                    blendOutPeriodToNoAnim: 0.4f,
                    startProgress: 0f,
                    useLinearSmoothing: false,
                    blendOutPeriod: 0.4f,
                    actionShift: 0,
                    forceFaceMorphRestart: false);
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("ApplyRoleIdle fail: " + ex.Message); }
        }

        // SLEEPER: lying on the ground in a sleep cycle. Channel 0 (full body) plays the
        // dungeon-prisoner-sleep_cycle animation natively as a loop. Wield Any weapon to
        // pre-empt the AnimationPoint NRE pattern even though we don't bind to a usable
        // (defensive — minor cost, eliminates one risk class).
        private static void ConfigureSleeper(Agent agent)
        {
            try
            {
                agent.SetMaximumSpeedLimit(0.1f, true);
                // Defensive wield (same lesson as sitter fix — empty-hands agents triggered NREs)
                agent.WieldInitialWeapons(Agent.WeaponWieldActionType.Instant,
                    Equipment.InitialWeaponEquipPreference.Any);

                // Pick the lay or sleep cycle, fall back if one didn't resolve in this install
                var sleepClip = _animSleepCycle.Index >= 0 ? _animSleepCycle : _animSleepCycleAlt;
                if (sleepClip.Index < 0) return; // neither resolved — agent stays standing

                agent.SetActionChannel(0, sleepClip,
                    ignorePriority: true,
                    additionalFlags: (AnimFlags)0,    // NO anf_keep — _cycle loops natively
                    blendWithNextActionFactor: 0f,
                    actionSpeed: 1f,
                    blendInPeriod: 0.4f,              // slow blend-in — looks like lying down
                    blendOutPeriodToNoAnim: 0.4f,
                    startProgress: 0f,
                    useLinearSmoothing: false,
                    blendOutPeriod: 0.4f,
                    actionShift: 0,
                    forceFaceMorphRestart: false);
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("ConfigureSleeper fail: " + ex.Message); }
        }

        private static void ConfigureSitter(Agent agent)
        {
            try
            {
                agent.SetMaximumSpeedLimit(0.3f, true);
                // CRASH MITIGATION: vanilla AnimationPoint.SetAgentItemsVisibility NREs on
                // sitters with NO wielded item during chair-bind teardown. Switched from
                // MeleeForMainHand to Any — `Any` wields whatever's available (bow, axe,
                // sword, anything) which covers archer bandits whose equipment has no
                // melee item and thus wouldn't satisfy MeleeForMainHand.
                agent.WieldInitialWeapons(Agent.WeaponWieldActionType.Instant,
                    Equipment.InitialWeaponEquipPreference.Any);
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("ConfigureSitter fail: " + ex.Message); }
        }

        // WANDERER: original Feature A behavior — varied speed + initial far destination.
        // The tick loop will reassign their destination on a slow round-robin schedule.
        private void ConfigureWanderer(Agent agent)
        {
            // BUG M2 FIX (2026-04-30): _wanderingNpcs.Add must run BEFORE the try block.
            // Previously it was the first statement INSIDE the try, so if any subsequent line
            // (SetMaximumSpeedLimit, AssignWanderDestination) threw, the agent was registered
            // in _npcRoles (by caller) but missing from _wanderingNpcs — it would never be
            // wander-ticked. Worse, with all wanderers failing this way, the predicate
            // `_wanderingNpcs.Count == 0` short-circuits the entire wander system. Adding
            // OUTSIDE the try guarantees registration even if the post-add config fails.
            if (agent == null) return;
            _wanderingNpcs.Add(agent);
            try
            {
                float speedMult = 0.45f + (float)(MBRandom.RandomFloat * 0.20f);
                agent.SetMaximumSpeedLimit(speedMult, true);

                if (_wanderTargets.Count > 1)
                {
                    var spawnPos = agent.Position;
                    Vec3 initialTarget = _wanderTargets[MBRandom.RandomInt(_wanderTargets.Count)];
                    float bestSq = initialTarget.DistanceSquared(spawnPos);
                    for (int i = 0; i < 3; i++)
                    {
                        var c = _wanderTargets[MBRandom.RandomInt(_wanderTargets.Count)];
                        var d = c.DistanceSquared(spawnPos);
                        if (d > bestSq) { bestSq = d; initialTarget = c; }
                    }
                    AssignWanderDestination(agent, initialTarget);
                }
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("ConfigureWanderer fail: " + ex.Message); }
        }

        private int CountRole(NpcRole role)
        {
            int n = 0;
            foreach (var kv in _npcRoles) if (kv.Value == role) n++;
            return n;
        }
    }
}
