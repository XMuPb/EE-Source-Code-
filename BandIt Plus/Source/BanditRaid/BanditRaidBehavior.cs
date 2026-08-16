using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace BandItPlus.BanditRaid
{
    // Wave 4.15.0 (2026-06-07) — Autonomous bandit village raids (Phase A).
    //
    // Bandit cultures periodically spawn raid parties from their hideouts to attack
    // nearby non-bandit-owned villages. Uses BanditPartyComponent.CreateBanditParty
    // to spawn (proven pattern from BanditAllianceBehavior.TrySummonWarband) +
    // MobileParty.Ai.SetMoveRaidSettlement to drive engine-native raid AI.
    //
    // All APIs used are STABLE across Bannerlord 1.2.x → 1.4.5 (per workflow
    // research wm2s277vj + wba8a1ubc):
    //   - CampaignEvents.HourlyTickEvent (driver)
    //   - CampaignEvents.VillageStateChangedEvent (completion detection — 4-param
    //     stable, raiderParty as 4th arg unambiguously identifies our raider)
    //   - Village.VillageStates enum (Normal / BeingRaided / Looted — stable)
    //   - BanditPartyComponent.CreateBanditParty (6-arg stable signature)
    //   - MobileParty.Ai.SetMoveRaidSettlement (stable)
    //   - Settlement.Position.X/.Y for distance (NOT Position2D — that property
    //     fails to compile on 1.2.x per memory note)
    //
    // Non-serialized (no SyncData). Raiders are ephemeral; tracking re-builds on
    // session load. Save-compat safe.
    public class BanditRaidBehavior : CampaignBehaviorBase
    {
        private static BanditRaidBehavior _instance;
        public static BanditRaidBehavior Instance { get { return _instance; } }

        // Active raid parties tracked by culture string id.
        // Key = culture id (e.g. "forest_bandits"); Value = the spawned MobileParty.
        private Dictionary<string, MobileParty> _activeRaiders = new Dictionary<string, MobileParty>();

        // Last-raid-day per culture for cooldown enforcement (in-game days).
        private Dictionary<string, double> _lastRaidDayByCulture = new Dictionary<string, double>();

        // Wave 4.15.0-fix15: counter for periodic raid diagnostic logging.
        private int _diagHourCounter = 0;

        // Wave 4.15.0-fix30 (2026-06-09): per-village raid-start tracking. After fix28
        // sets state to BeingRaided via ApplyBySettingToBeingRaided, we observed 0
        // engine-driven transitions to Looted (RaidEventComponent.CreateRaidEvent is
        // absent in 1.4.5 so vanilla's looting-timer MapEvent.Update never runs).
        // ALSO: user observed parties enter then LEAVE villages immediately — vanilla
        // bandit AI moves them away. fix30 (a) pins the party at the village for the
        // duration of the raid via SetMoveModeHold + position lock, and (b) tracks
        // start time so OnHourlyTick can manually flip to Looted after ~6 in-game
        // hours + fire VillageStateChanged so quest subscribers react.
        private Dictionary<Village, double> _raidStartTimes = new Dictionary<Village, double>();
        // Cached: raider party associated with each tracked village raid (for the
        // 4th arg of VillageStateChanged event when we manually fire it).
        private Dictionary<Village, MobileParty> _raidStartParties = new Dictionary<Village, MobileParty>();
        // How long a raid runs before we force-transition to Looted (in-game hours).
        // Vanilla raids take ~6-12 hours depending on hearth + party strength.
        private const double RAID_DURATION_HOURS = 6.0;

        // Wave 4.15.0-fix34 (2026-06-10): per-village cooldown after raid completion.
        // Vanilla restores Village.VillageState to Normal after some hours of recovery,
        // and without this cooldown the proximity-trigger would IMMEDIATELY re-raid the
        // same village the next time any bandit party walks past. User reported they
        // don't want "raid same village again" behavior.
        private Dictionary<Village, double> _lastRaidedVillageHours = new Dictionary<Village, double>();
        private const double PER_VILLAGE_REPEAT_COOLDOWN_HOURS = 168.0; // 7 in-game days

        // Wave 4.15.0-fix31 (2026-06-10): once a raid triggers, the party transitions
        // from "traveling-to-target" (tracked in _raidTargets) to "raiding-in-place"
        // (tracked here). This dict signals to the Harmony Postfix + the OnHourlyTick
        // re-issue loop that they MUST NOT issue movement orders for these parties —
        // SetMoveModeHold from fix30 needs to persist for the full raid duration.
        // User-observed bug: parties were leaving the village mid-loot because
        // fix26's Postfix + fix16's re-issue kept calling SetMoveGoToSettlement,
        // overriding the pin. fix31 removes them from those code paths entirely.
        private Dictionary<MobileParty, Village> _raidsInProgress = new Dictionary<MobileParty, Village>();

        // Public query for Patches/BanditAiSuppressionPatch.Postfix — when true,
        // the Postfix should NOT restore TargetSettlement (let SetMoveModeHold stick).
        public bool IsRaidingInProgress(MobileParty party)
        {
            if (party == null) return false;
            try { return _raidsInProgress.ContainsKey(party); }
            catch { return false; }
        }

        // Wave 4.15.0-fix28 (2026-06-09): cached reflection handles for the actual
        // PUBLIC raid-trigger APIs found via DLL inspection workflow wkfbx9jx5.
        // Previous "no public API" verdict was WRONG — these are real 1.4.5 methods.
        //   Strategy 0: RaidEventComponent.CreateRaidEvent(PartyBase, PartyBase)
        //               public static — drives FULL lifecycle (BeingRaided + MapEvent + Looted timer)
        //   Strategy 1: ChangeVillageStateAction.ApplyBySettingToBeingRaided(Settlement, MobileParty)
        //               public static — sets state + fires vanilla subscribers (no MapEvent)
        //   Strategy 2: ChangeVillageStateAction.ApplyInternal — NonPublic reflection fallback
        //   Strategy 3: Village._villageState FieldInfo raw write — last resort, no events
        private static MethodInfo _cachedCreateRaidEventMethod;
        private static MethodInfo _cachedApplyBeingRaidedMethod;
        private static MethodInfo _cachedApplyInternalMethod;
        private static FieldInfo _cachedVillageStateField;
        private static bool _raidTriggerResolved;

        // Wave 4.15.0-fix16 (2026-06-08): per-party target tracking so we can
        // RE-ISSUE the move command when vanilla bandit AI clears it (which it does
        // immediately after our initial SetMoveGoToSettlement, per DIAG evidence).
        // Tracks ALL spawned raid parties (leads + reinforcements) → their target village.
        private Dictionary<MobileParty, Settlement> _raidTargets = new Dictionary<MobileParty, Settlement>();

        // ───────── 2026-07-26: fix46 re-trigger throttle ─────────
        // The fix46 "vanilla mimic" below re-fires raid intent every time the engine
        // reverts BeingRaided → Normal. It had NO cap, NO cooldown and NO give-up
        // condition, so when the engine kept reverting (exactly the case fix46
        // exists for) the cycle ran unbounded: each re-trigger re-entered
        // BeingRaided, which re-fired the on-screen banner and reset raid progress
        // before it could accumulate, while still granting loot per cycle.
        // Users reported: "prompts to clear every second and then I get no raid
        // progress. Tons of gold which is nice but the raid never ends."
        // Bounded here by attempts AND by in-game time, then the party is released.
        private const int kMaxRaidRetriggers = 4;
        private const float kRetriggerCooldownHours = 1f;
        private const float kRaidBannerCooldownHours = 6f;

        private readonly Dictionary<Settlement, int> _retriggerCount = new Dictionary<Settlement, int>();
        private readonly Dictionary<Settlement, double> _retriggerLastHour = new Dictionary<Settlement, double>();
        private readonly Dictionary<Settlement, double> _raidBannerLastHour = new Dictionary<Settlement, double>();

        // ───────── Wave 4.16.0 (2026-06-11): realistic muster + reinforcement call ─────────
        // Raid parties no longer spawn at full strength and march immediately. They
        // spawn SMALL at the hideout, HOLD there gathering recruits each hour (war
        // band mustering), and depart only once the muster target is reached (or the
        // deadline passes). When their raid MapEvent actually starts, the hideout
        // answers with reinforcement war bands (the "call for help"). Everything
        // scales up each campaign year — stronger, thirstier, more demanding.
        private class RaidContext
        {
            public CultureObject Culture;
            public Settlement Hideout;
            public Settlement Village;
            public Clan BanditClan;
            public PartyTemplateObject Template;
            public Hero Leader;
            public float Power;
            public bool ReinforcementsCalled;
        }
        private Dictionary<MobileParty, int> _musterTargets = new Dictionary<MobileParty, int>();
        private Dictionary<MobileParty, double> _musterDeadlines = new Dictionary<MobileParty, double>();
        private Dictionary<MobileParty, RaidContext> _raidContexts = new Dictionary<MobileParty, RaidContext>();
        // First in-game day the raid system ran in this campaign — escalation baseline.
        // Persisted in SyncData so "stronger every year" survives save/load.
        private double _escalationBaselineDays = -1.0;
        // Wave 4.17.1: campaign-lifetime villages-looted counter (Diagnostics board).
        private int _totalVillagesLooted;
        private const float ESCALATION_PER_YEAR = 0.25f;   // +25% per campaign year
        private const float ESCALATION_CAP = 4.0f;          // max 4x
        private const double MUSTER_MAX_HOURS = 48.0;        // 2 in-game days to gather
        private const double DAYS_PER_GAME_YEAR = 84.0;      // 4 seasons x 21 days

        // Wave 4.15.0-fix21 (2026-06-08): public query for BanditAiSuppressionPatch.
        // Returns true iff `party` is a raid party currently tracked by this behavior
        // (i.e. present as a key in _raidTargets with a non-null target Settlement).
        // The Harmony PREFIX uses this to early-return on tracked raiders, suppressing
        // vanilla bandit AI's TargetSettlement clearing for ONLY our parties.
        // Defensive: never throws — patch hot-path tolerates a null/garbage return.
        public bool IsTrackedRaider(MobileParty party)
        {
            if (party == null) return false;
            try
            {
                Settlement target;
                if (!_raidTargets.TryGetValue(party, out target)) return false;
                return target != null && party.IsActive;
            }
            catch
            {
                return false;
            }
        }

        // Wave 4.15.0-fix49 (2026-06-10): used by BlockPrematureRaidRevertPatch to
        // prevent the engine from reverting BeingRaided→Normal on villages our
        // tracked raiders are currently attacking.
        public bool IsRaidTargetSettlement(Settlement settlement)
        {
            if (settlement == null) return false;
            try
            {
                foreach (var kv in _raidTargets)
                {
                    if (kv.Value == settlement && kv.Key != null && kv.Key.IsActive)
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public override void RegisterEvents()
        {
            _instance = this;
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
            CampaignEvents.VillageStateChanged.AddNonSerializedListener(this, OnVillageStateChanged);
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            // Wave 4.16.0: reinforcement call — when one of our raids actually starts
            // (engine creates the raid MapEvent), the hideout sends help.
            CampaignEvents.MapEventStarted.AddNonSerializedListener(this, OnRaidMapEventStarted);
            // Wave 4.18.0: bounty claims — detect the player destroying a bounty band.
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, OnMobilePartyDestroyedBounty);
        }

        public override void SyncData(IDataStore dataStore)
        {
            // Party tracking is non-persistent — re-built each session. Active raid
            // parties already in the world simply continue their engine-driven AI.
            // Wave 4.16.0: the yearly-escalation baseline IS persisted so bandits
            // keep getting stronger across save/load ("stronger every year").
            dataStore.SyncData("_bpRaidEscalationBaselineDays", ref _escalationBaselineDays);
            // Wave 4.17.1: campaign-lifetime looted counter for the Diagnostics board.
            dataStore.SyncData("_bpRaidTotalVillagesLooted", ref _totalVillagesLooted);
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            _activeRaiders.Clear();
            _lastRaidDayByCulture.Clear();
            // Wave 4.15.0-fix32 (2026-06-10): per the workflow's HONEST verdict
            // ("you've been guessing for 30 fixes"), dump the actual API surface of
            // EnterSettlementAction + LeaveSettlementAction in user's 1.4.5 install.
            // The log output (search for "fix32 PROBE") tells us EXACTLY which static
            // methods exist with which signatures — then fix33 can be built on
            // verified ground instead of more reflection chains that might fail.
            ProbeEnterSettlementApi();
            ProbeLeaveSettlementApi();
            // Wave 4.15.0-fix35 (2026-06-10): same probe pattern for the action classes
            // we need to make raids have real consequences — relation drops, hearth
            // damage, war/crime escalation. User flagged: "looters and bandits raiding
            // the village are not in war with the clan etc". Probe first, ship code after.
            ProbeRaidConsequenceApis();
        }

        // Wave 4.15.0-fix35 (2026-06-10): dump every action class we might need to
        // apply REAL post-raid consequences. Logs search for "fix35 PROBE".
        private static bool _raidConsequenceProbed;
        private void ProbeRaidConsequenceApis()
        {
            if (_raidConsequenceProbed) return;
            _raidConsequenceProbed = true;
            // Candidate action classes — full names in TaleWorlds.CampaignSystem.Actions namespace.
            string[] candidates = {
                "ChangeRelationAction",
                "ChangeCrimeRatingAction",
                "DeclareWarAction",
                "MakeKingdomsEnemyAction",
                "MakePeaceAction",
                "ChangeHearthLevelAction",
                "ChangeVillageHearthAction",
                "ChangeVillagerCountAction",
                "ChangeSettlementHearthAction",
                "ChangeOwnerOfSettlementAction",
                // fix35 round 2 (2026-06-10): user-supplied tip class names
                "DestroySettlementAction",
                "GoldChangeAction",
                "RaidSettlementAction"
            };
            foreach (var name in candidates)
            {
                try
                {
                    var t = Type.GetType(
                        "TaleWorlds.CampaignSystem.Actions." + name + ", TaleWorlds.CampaignSystem",
                        throwOnError: false);
                    if (t == null)
                    {
                        SafeLog("fix35 PROBE: " + name + " — NOT FOUND");
                        continue;
                    }
                    int count = 0;
                    foreach (var m in t.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (m.IsSpecialName) continue;
                        var ps = m.GetParameters();
                        string sig = m.Name + "(";
                        for (int i = 0; i < ps.Length; i++)
                            sig += (i > 0 ? "," : "") + ps[i].ParameterType.Name;
                        sig += ") => " + m.ReturnType.Name + " [" + (m.IsPublic ? "public" : "non-public") + "]";
                        SafeLog("fix35 PROBE " + name + ": " + sig);
                        count++;
                    }
                    SafeLog("fix35 PROBE: " + name + " — " + count + " methods dumped");
                }
                catch (Exception ex)
                {
                    SafeLog("fix35 PROBE " + name + " threw " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            // fix35 round 3 (2026-06-10): user asked "have a look in the game files,
            // I think there should be all answer". Comprehensive game-file inspection:
            // enumerate EVERY type in the TaleWorlds.CampaignSystem.Actions namespace
            // and dump each class name + count of methods. That gives us the complete
            // list of Action APIs in 1.4.5 — no guessing class names that don't exist.
            try
            {
                SafeLog("fix35 PROBE: enumerating ALL classes in TaleWorlds.CampaignSystem.Actions namespace:");
                var asm = typeof(TaleWorlds.CampaignSystem.Actions.ChangeRelationAction).Assembly;
                int actionClassCount = 0;
                foreach (var t in asm.GetTypes())
                {
                    if (t.Namespace == "TaleWorlds.CampaignSystem.Actions" && t.IsClass)
                    {
                        var pubMethods = t.GetMethods(BindingFlags.Public | BindingFlags.Static).Length;
                        SafeLog("fix35 PROBE Action class: " + t.Name + " (" + pubMethods + " public static methods)");
                        actionClassCount++;
                    }
                }
                SafeLog("fix35 PROBE: " + actionClassCount + " classes found in TaleWorlds.CampaignSystem.Actions");
            }
            catch (Exception ex)
            {
                SafeLog("fix35 PROBE Actions-namespace enumeration threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            // fix35 round 2: dump the VillageStates enum to see if it has more values
            // (user's tip mentioned a "Raided" burned-recovery state in addition to
            // Normal/BeingRaided/Looted — would explain the gap in our completion handler).
            try
            {
                SafeLog("fix35 PROBE: Village.VillageStates enum values:");
                foreach (var v in Enum.GetValues(typeof(Village.VillageStates)))
                {
                    SafeLog("fix35 PROBE VillageStates value: " + v.ToString()
                        + " (" + ((int)v) + ")");
                }
            }
            catch (Exception ex)
            {
                SafeLog("fix35 PROBE VillageStates enum dump threw " + ex.GetType().Name + ": " + ex.Message);
            }

            // Also dump Village field/property surface for direct manipulation candidates
            // (hearth amount, prosperity, owner, etc.)
            try
            {
                SafeLog("fix35 PROBE: Village property/field dump (looking for hearth setter):");
                var villageType = typeof(Village);
                foreach (var p in villageType.GetProperties(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (p.PropertyType == typeof(float) || p.PropertyType == typeof(int) || p.PropertyType == typeof(double))
                    {
                        SafeLog("fix35 PROBE Village.prop " + p.Name + " : " + p.PropertyType.Name
                            + " canRead=" + p.CanRead + " canWrite=" + p.CanWrite);
                    }
                }
                foreach (var f in villageType.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.FieldType == typeof(float) || f.FieldType == typeof(int) || f.FieldType == typeof(double))
                    {
                        SafeLog("fix35 PROBE Village.field " + f.Name + " : " + f.FieldType.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                SafeLog("fix35 PROBE Village dump threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Wave 4.15.0-fix32 (2026-06-10): one-shot diagnostic probe. Dumps EVERY
        // static method on TaleWorlds.CampaignSystem.Actions.EnterSettlementAction
        // with parameter type signatures. User pastes the output back so fix33
        // can target the REAL method by exact signature, no guessing.
        private static bool _enterSettlementProbed;
        private void ProbeEnterSettlementApi()
        {
            if (_enterSettlementProbed) return;
            _enterSettlementProbed = true;
            try
            {
                var t = Type.GetType(
                    "TaleWorlds.CampaignSystem.Actions.EnterSettlementAction, TaleWorlds.CampaignSystem",
                    throwOnError: false);
                if (t == null)
                {
                    SafeLog("fix32 PROBE: EnterSettlementAction type NOT FOUND in 1.4.5. " +
                        "Engine doesn't expose this Action class — bandits can't be made to " +
                        "physically enter a settlement via Action API.");
                    return;
                }
                SafeLog("fix32 PROBE: EnterSettlementAction type RESOLVED. Dumping methods:");
                int count = 0;
                foreach (var m in t.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.IsSpecialName) continue;
                    var ps = m.GetParameters();
                    string sig = m.Name + "(";
                    for (int i = 0; i < ps.Length; i++)
                        sig += (i > 0 ? "," : "") + ps[i].ParameterType.Name;
                    sig += ") => " + m.ReturnType.Name + " [" + (m.IsPublic ? "public" : "non-public") + "]";
                    SafeLog("fix32 PROBE candidate: " + sig);
                    count++;
                }
                SafeLog("fix32 PROBE: EnterSettlementAction dumped " + count + " methods.");
            }
            catch (Exception ex)
            {
                SafeLog("fix32 ProbeEnterSettlementApi threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool _leaveSettlementProbed;
        private void ProbeLeaveSettlementApi()
        {
            if (_leaveSettlementProbed) return;
            _leaveSettlementProbed = true;
            try
            {
                var t = Type.GetType(
                    "TaleWorlds.CampaignSystem.Actions.LeaveSettlementAction, TaleWorlds.CampaignSystem",
                    throwOnError: false);
                if (t == null)
                {
                    SafeLog("fix32 PROBE: LeaveSettlementAction type NOT FOUND in 1.4.5.");
                    return;
                }
                SafeLog("fix32 PROBE: LeaveSettlementAction type RESOLVED. Dumping methods:");
                int count = 0;
                foreach (var m in t.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.IsSpecialName) continue;
                    var ps = m.GetParameters();
                    string sig = m.Name + "(";
                    for (int i = 0; i < ps.Length; i++)
                        sig += (i > 0 ? "," : "") + ps[i].ParameterType.Name;
                    sig += ") => " + m.ReturnType.Name + " [" + (m.IsPublic ? "public" : "non-public") + "]";
                    SafeLog("fix32 PROBE candidate: " + sig);
                    count++;
                }
                SafeLog("fix32 PROBE: LeaveSettlementAction dumped " + count + " methods.");
            }
            catch (Exception ex)
            {
                SafeLog("fix32 ProbeLeaveSettlementApi threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnHourlyTick()
        {
            try
            {
                var s = MCMSettings.Instance;
                if (s == null || !s.EnableMod || !s.AutonomousRaidsEnabled) return;

                // Wave 4.16.0: advance mustering war bands (gather recruits, depart
                // when strong enough). Runs before the re-issue loop so departing
                // parties get their march order this same tick.
                ProcessMusterPhase();

                // Wave 4.18.0: expire bounties (timeout or band reached home).
                SweepBounties();

                // Power gate — wait until bandit power has ramped up before raiding starts.
                float power = 0f;
                try { power = BandItPlus.BanditPower.BanditPowerBehavior.PowerFactor(); }
                catch { /* defensive — never let power read crash the tick */ }
                if (power < s.RaidPowerGate) return;

                // Cleanup orphaned raiders (party destroyed, completed raid, or
                // engine despawned them).
                // Wave 4.15.0-fix12 (2026-06-08): LOG cleanup events so we can see
                // when raid parties die in transit (intercepted by patrols, lords,
                // other bandits) vs. successfully complete raids.
                var stale = new List<string>();
                foreach (var kv in _activeRaiders)
                {
                    if (kv.Value == null || !kv.Value.IsActive)
                        stale.Add(kv.Key);
                }
                foreach (var k in stale)
                {
                    SafeLog("Cleaned up stale raider for " + k +
                        " (party null/destroyed — likely intercepted in transit or already finished offscreen).");
                    _activeRaiders.Remove(k);
                }

                // Wave 4.15.0-fix16 (2026-06-08): RE-ISSUE move command for any tracked
                // raid party whose engine TargetSettlement has been cleared by vanilla
                // bandit AI. DIAG showed the AI clears the target ~1 hour after spawn,
                // freezing the party in the wilderness. Brute-force re-issue keeps them
                // marching toward their assigned village until they arrive or die.
                var orphanTargets = new List<MobileParty>();
                int reissued = 0;
                foreach (var kv in _raidTargets)
                {
                    var p = kv.Key;
                    var v = kv.Value;
                    if (p == null || !p.IsActive || v == null)
                    {
                        orphanTargets.Add(p);
                        continue;
                    }
                    try
                    {
                        // Wave 4.15.0-fix47 (2026-06-10): fix37's abandon-redirect was
                        // REMOVING our parties from _raidTargets when engine flipped
                        // BeingRaided — which broke fix46's vanilla mimic re-trigger
                        // (lookup found nothing). Disabled. Engine handles repeat-raid
                        // refusal naturally; we just need to stay in _raidTargets so
                        // fix46 can find us when the engine reverts and we need to
                        // re-trigger.
                        if (false && v.IsVillage && v.Village != null
                            && v.Village.VillageState != Village.VillageStates.Normal)
                        {
                            SafeLog("fix37: raider '" + p.StringId + "' target '" + v.Name
                                + "' is " + v.Village.VillageState
                                + " (already raided) — sending home + dropping tracking.");
                            try
                            {
                                if (p.HomeSettlement != null)
                                {
                                    BandItPlus.Compat.PartyOrderCompat.GoToSettlement(p, p.HomeSettlement);
                                }
                                if (p.Ai != null) p.Ai.SetDoNotMakeNewDecisions(false);
                            }
                            catch { }
                            orphanTargets.Add(p);
                            continue;
                        }

                        // Skip if party already reached the target village (raid in progress).
                        if (p.CurrentSettlement == v) continue;

                        // ─── fix28 (2026-06-09): proximity check + raid trigger ───
                        // If our raid party is at the village doorstep but the engine
                        // hasn't auto-triggered the raid, force the state transition via
                        // the canonical vanilla entry point (RaidEventComponent.CreateRaidEvent
                        // or ChangeVillageStateAction.ApplyBySettingToBeingRaided). Vanilla
                        // subscribers (VillagerCampaignBehavior, MapEventManager looting
                        // timer) take it from there → BeingRaided→Looted naturally.
                        // Wave 4.16.0: parties still mustering at the hideout are
                        // intentionally parked — don't re-issue march orders for them.
                        if (_musterTargets.ContainsKey(p)) continue;

                        // Wave 4.16.3: party is INSIDE a live MapEvent (raiding or
                        // fighting) — the engine owns it. Touching its move orders
                        // mid-battle desyncs the raid. Leave it alone; the Looted
                        // handler releases it when the raid completes.
                        if (p.MapEvent != null) continue;

                        // Wave 4.16.1: target already looted (by us or anyone) — the
                        // mission is over. Send the band home instead of re-issuing
                        // march orders toward a smoking ruin (loitering fix for
                        // late-arriving reinforcements).
                        if (v.Village != null
                            && v.Village.VillageState == Village.VillageStates.Looted)
                        {
                            ReleaseRaider(p, "target already looted");
                            orphanTargets.Add(p);
                            continue;
                        }

                        float dx = p.Position.X - v.Position.X;
                        float dy = p.Position.Y - v.Position.Y;
                        float distSqr = dx * dx + dy * dy;
                        // Wave 4.15.0-fix50 (2026-06-10): WORKFLOW-VERIFIED REWRITE.
                        // After exhaustive engine decompile + BanditMilitiasRedux source
                        // diff (workflow wf_dfd48d4c), the verified single API call that
                        // creates a REAL vanilla raid MapEvent in 1.4.5 War Sails is
                        // RaidEventComponent.CreateRaidEvent(PartyBase attacker, PartyBase defender).
                        // This replaces the entire fix28/30/31/33/38/39 simulation chain.
                        // After this call: the engine creates the MapEvent, sets village
                        // state, starts the per-tick RaidEventComponent.Update damage drain,
                        // and progresses to Looted naturally. We do NOTHING else — no pin,
                        // no position write, no manual state flip.
                        if (distSqr <= 4.0f && v.IsVillage && v.Village != null
                            && v.Village.VillageState == Village.VillageStates.Normal
                            // fix34: per-village cooldown (still respected).
                            && !(_lastRaidedVillageHours.TryGetValue(v.Village, out var lastHours)
                                 && (CampaignTime.Now.ToHours - lastHours) < PER_VILLAGE_REPEAT_COOLDOWN_HOURS))
                        {
                            try
                            {
                                if (TryCreateRealRaidEvent(p, v))
                                {
                                    // Wave 4.16.3 (2026-06-11): KEEP the party in
                                    // _raidTargets (was orphanTargets.Add(p) — that
                                    // removed the attacker from tracking, so the Looted
                                    // release-sweep never found it and it stood outside
                                    // the looted village forever, user-observed at
                                    // Nifdal). The MapEvent-guard below stops the
                                    // re-issue loop from disturbing it mid-raid; the
                                    // Looted handler now releases it home.
                                    continue;
                                }
                            }
                            catch (Exception crEx)
                            {
                                SafeLog("fix50 CreateRaidEvent threw "
                                    + crEx.GetType().Name + ": " + crEx.Message);
                            }
                        }

                        // Skip if engine still respects our target.
                        var curTarget = p.TargetSettlement;
                        if (curTarget == v) continue;
                        // Re-issue the move order. Bandit AI cleared the target — push it back in.
                        if (p.Ai != null) p.Ai.SetDoNotMakeNewDecisions(false);
                        // fix19: try positional move FIRST (bypasses settlement-AI override).
                        // The Vec2 point is the village's position — engine path-finds to it
                        // without invoking the bandit-AI settlement behavior that froze us.
                        Vec2 villagePoint = new Vec2(v.Position.X, v.Position.Y);
                        if (!TryMoveGoToPoint(p, villagePoint))
                        {
                            // fix17 fallback: SetMoveRaidSettlement (raid intent, not in 1.4.5).
                            if (!TryRaidSettlement(p, v))
                            {
                                // Final fallback: original SetMoveGoToSettlement (causes micro-oscillation).
                                BandItPlus.Compat.PartyOrderCompat.GoToSettlement(p, v);
                            }
                        }
                        reissued++;
                    }
                    catch (Exception rEx)
                    {
                        SafeLog("Re-issue move for '" + (p.StringId ?? "?") + "' threw "
                            + rEx.GetType().Name + ": " + rEx.Message);
                    }
                }
                foreach (var op in orphanTargets) _raidTargets.Remove(op);
                if (reissued > 0)
                {
                    SafeLog("Re-issued move order for " + reissued + " raid party(ies) (vanilla bandit AI had cleared the target).");
                }

                // ─── fix30 (2026-06-09): manual BeingRaided → Looted timer ───
                // Vanilla looting-timer absent (no RaidEventComponent.CreateRaidEvent
                // in 1.4.5), so we drive completion ourselves. After RAID_DURATION_HOURS
                // of BeingRaided state, force transition to Looted via reflection +
                // event fire so quest subscribers + our own OnVillageStateChanged
                // detector ("Raid complete: X looted Y" log) react.
                // fix31: belt-and-braces — re-pin every raiding-in-progress party each
                // hour in case anything else (other mods, leftover engine ticks) tried
                // to move them. SetMoveModeHold is idempotent.
                var orphanRaiders = new List<MobileParty>();
                foreach (var kv2 in _raidsInProgress)
                {
                    var rParty = kv2.Key;
                    var rVill = kv2.Value;
                    if (rParty == null || !rParty.IsActive) { orphanRaiders.Add(rParty); continue; }
                    // Wave 4.15.0-fix37 (2026-06-10): GROUND-TRUTH diagnostic. User is
                    // visually watching parties leave/re-enter villages in a loop while
                    // the log claims they "entered". Log the ACTUAL party state so we
                    // can see what the engine is doing to them between our ticks. NO
                    // MORE GUESSING — instrument first.
                    try
                    {
                        var curSettlement = rParty.CurrentSettlement;
                        var tgtSettlement = rParty.TargetSettlement;
                        SafeLog("fix37 DIAG raider '" + rParty.StringId
                            + "' raidVill='" + (rVill?.Name?.ToString() ?? "<null>")
                            + "' CurrentSettlement='" + (curSettlement?.Name?.ToString() ?? "<null>")
                            + "' TargetSettlement='" + (tgtSettlement?.Name?.ToString() ?? "<null>")
                            + "' pos=(" + rParty.Position.X.ToString("F1") + ","
                            + rParty.Position.Y.ToString("F1") + ")"
                            + " VS=" + (rVill != null ? rVill.VillageState.ToString() : "<null>")
                            + " troops=" + (rParty.MemberRoster?.TotalManCount ?? -1));

                        // If party drifted OUT of the settlement we put them in, force
                        // them back in. This is fix37's hypothesis: the engine is auto-
                        // ejecting them. Re-fire EnterSettlementAction so they go back.
                        if (rVill != null
                            && rVill.Settlement != null
                            && curSettlement != rVill.Settlement
                            && rParty.IsActive)
                        {
                            SafeLog("fix37: raider '" + rParty.StringId
                                + "' DRIFTED OUT of '" + rVill.Name
                                + "' (CurrentSettlement=" + (curSettlement?.Name?.ToString() ?? "<null>")
                                + ") — RE-ENTERING via EnterSettlementAction.");
                            try
                            {
                                TaleWorlds.CampaignSystem.Actions.EnterSettlementAction
                                    .ApplyForParty(rParty, rVill.Settlement);
                            }
                            catch (Exception reEnterEx)
                            {
                                SafeLog("fix37 re-enter threw " + reEnterEx.GetType().Name + ": " + reEnterEx.Message);
                            }
                        }
                    }
                    catch (Exception diagEx)
                    {
                        SafeLog("fix37 DIAG block threw " + diagEx.GetType().Name + ": " + diagEx.Message);
                    }

                    try
                    {
                        rParty.SetMoveModeHold();
                        if (rParty.Ai != null) rParty.Ai.SetDoNotMakeNewDecisions(true);
                    }
                    catch { /* defensive */ }
                }
                foreach (var op2 in orphanRaiders) _raidsInProgress.Remove(op2);

                double nowHours = CampaignTime.Now.ToHours;
                var completedRaids = new List<Village>();
                foreach (var kv in _raidStartTimes)
                {
                    var vill = kv.Key;
                    double startHours = kv.Value;
                    if (vill == null) { completedRaids.Add(vill); continue; }
                    // Already past BeingRaided (engine OR another mod handled it) — drop tracking
                    if (vill.VillageState != Village.VillageStates.BeingRaided)
                    {
                        completedRaids.Add(vill);
                        continue;
                    }
                    if (nowHours - startHours < RAID_DURATION_HOURS) continue;
                    // Time to complete.
                    MobileParty raiderParty;
                    _raidStartParties.TryGetValue(vill, out raiderParty);
                    if (TryCompleteRaid(vill, raiderParty))
                    {
                        // Wave 4.15.0-fix35 (2026-06-10): apply verified post-raid
                        // consequences using probe-confirmed PUBLIC APIs.
                        ApplyRaidConsequences(vill, raiderParty);
                        completedRaids.Add(vill);
                    }
                }
                foreach (var done in completedRaids)
                {
                    _raidStartTimes.Remove(done);
                    _raidStartParties.Remove(done);
                    // fix31: release pin once raid completes — let bandit AI take over
                    // (likely returns party to hideout).
                    var releaseList = new List<MobileParty>();
                    foreach (var ridKv in _raidsInProgress)
                    {
                        if (ridKv.Value == done) releaseList.Add(ridKv.Key);
                    }
                    foreach (var releaseParty in releaseList)
                    {
                        try
                        {
                            // Wave 4.15.0-fix33 (2026-06-10): VERIFIED via fix32 PROBE
                            // that LeaveSettlementAction.ApplyForParty(MobileParty) is
                            // PUBLIC in 1.4.5. Make the raider party physically LEAVE
                            // the village now that the raid is complete.
                            if (releaseParty != null
                                && releaseParty.CurrentSettlement != null
                                && releaseParty.CurrentSettlement.IsVillage)
                            {
                                TaleWorlds.CampaignSystem.Actions.LeaveSettlementAction
                                    .ApplyForParty(releaseParty);
                                SafeLog("fix33: LeaveSettlementAction.ApplyForParty('"
                                    + releaseParty.StringId
                                    + "') — raid done, bandits exiting village.");
                            }
                            if (releaseParty != null && releaseParty.Ai != null)
                            {
                                releaseParty.Ai.SetDoNotMakeNewDecisions(false);
                            }
                            // Wave 4.15.0-fix34 (2026-06-10): explicit "go home" order so
                            // raiders walk back to their hideout instead of loitering near
                            // the looted village. MobileParty.HomeSettlement points to the
                            // hideout for parties spawned via BanditPartyComponent.
                            try
                            {
                                if (releaseParty != null
                                    && releaseParty.HomeSettlement != null
                                    && releaseParty.IsActive)
                                {
                                    BandItPlus.Compat.PartyOrderCompat.GoToSettlement(
                                        releaseParty, releaseParty.HomeSettlement);
                                    SafeLog("fix34: '" + releaseParty.StringId
                                        + "' ordered home → '" + releaseParty.HomeSettlement.Name + "'");
                                }
                            }
                            catch (Exception homeEx)
                            {
                                SafeLog("fix34 go-home order threw "
                                    + homeEx.GetType().Name + ": " + homeEx.Message);
                            }
                        }
                        catch (Exception leaveEx)
                        {
                            SafeLog("fix33 LeaveSettlementAction threw "
                                + leaveEx.GetType().Name + ": " + leaveEx.Message);
                        }
                        _raidsInProgress.Remove(releaseParty);
                    }
                    // fix34: extend per-culture cooldown so this culture doesn't
                    // immediately spawn a fresh wave targeting the SAME area. The existing
                    // _lastRaidDayByCulture cooldown already covers the spawn side, but
                    // we also need a per-VILLAGE cooldown so when a village's state
                    // resets to Normal after vanilla recovery, our proximity-trigger
                    // doesn't immediately re-raid it. Stamp here.
                    _lastRaidedVillageHours[done] = nowHours;
                }

                // Wave 4.15.0-fix15 diagnostic: every ~6 in-game hours dump the
                // state of each active raider so we can see if they're actually
                // walking to the target or if vanilla AI hijacked them.
                _diagHourCounter++;
                if (_diagHourCounter >= 6 && _activeRaiders.Count > 0)
                {
                    _diagHourCounter = 0;
                    foreach (var kv in _activeRaiders)
                    {
                        try
                        {
                            var p = kv.Value;
                            if (p == null) continue;
                            // Use Position (not Position2D — memory says fails on 1.2.x).
                            float px = p.Position.X;
                            float py = p.Position.Y;
                            int troops = p.MemberRoster != null ? p.MemberRoster.TotalManCount : -1;
                            string targetName = "?";
                            try { targetName = p.TargetSettlement?.Name?.ToString() ?? "<null>"; }
                            catch { }
                            SafeLog("DIAG raider " + kv.Key + " '" + p.StringId
                                + "' pos=(" + px.ToString("F1") + "," + py.ToString("F1") + ")"
                                + " troops=" + troops
                                + " ai.targetSettlement=" + targetName);
                        }
                        catch { }
                    }
                }

                // Global concurrent cap — don't flood the map with raiders.
                if (_activeRaiders.Count >= s.MaxConcurrentRaiders) return;

                // Per-hour spawn chance, modulated by power factor so early-game
                // raids are rare and late-game raids feel intense.
                float spawnChance = s.RaidSpawnChancePerHour * (0.5f + power * 0.5f);
                // Wave 4.18.0: raid seasons — post-harvest hunger drives raiding.
                // Autumn/winter x1.5, spring/summer x0.75. Season derived from day
                // arithmetic (21-day seasons, campaign starts in spring) — avoids
                // version-fragile season APIs.
                int seasonIdx = ((int)(CampaignTime.Now.ToDays / 21.0)) % 4; // 0 spr, 1 sum, 2 aut, 3 win
                spawnChance *= (seasonIdx >= 2) ? 1.5f : 0.75f;
                if (MBRandom.RandomFloat > spawnChance) return;

                // Pick a hideout + culture that's eligible for raiding right now.
                var eligible = PickEligibleHideouts();
                if (eligible == null || eligible.Count == 0) return;
                var pick = eligible[MBRandom.RandomInt(eligible.Count)];

                // Pick a target village within range.
                var target = PickTargetVillage(pick.hideout);
                if (target == null) return;

                // Spawn raid party + send it. Pass power so the spawn can scale
                // beef-up multiplier + reinforcement count.
                SpawnRaidParty(pick.culture, pick.hideout, target, power);
            }
            catch (Exception ex)
            {
                SafeLog("OnHourlyTick error: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnVillageStateChanged(Village village, Village.VillageStates oldState, Village.VillageStates newState, MobileParty raiderParty)
        {
            try
            {
                if (village == null) return;

                // Wave 4.16.4 (2026-06-11): declare war on EVERY raid start, regardless
                // of trigger path (fix50 CreateRaidEvent, fix46 re-trigger, engine
                // self-start). Ataconia's raid came via the fix46 path and skipped the
                // declaration entirely — user-observed: Mountain Bandits raided it but
                // no war with its kingdom appeared. Idempotent via IsAtWarWith guards.
                if (oldState == Village.VillageStates.Normal
                    && newState == Village.VillageStates.BeingRaided
                    && raiderParty != null
                    && IsTrackedRaider(raiderParty))
                {
                    DeclareRaidWar(raiderParty, village.Settlement);

                    // Wave 4.16.6 (2026-06-11): on-screen raid alert so the player can
                    // find the action without reading log files (user asked "which
                    // village is getting raided" three sessions running).
                    //
                    // 2026-07-26: rate-limited per village. This fires on the STATE
                    // TRANSITION, and the engine re-enters BeingRaided on every fix46
                    // re-trigger — so an unthrottled banner produced the reported
                    // "prompts to clear every second". Announce a raid once, not once
                    // per transition.
                    try
                    {
                        if (ShouldAnnounceRaid(village.Settlement))
                        {
                            var factionName = raiderParty.MapFaction != null
                                ? raiderParty.MapFaction.Name.ToString() : BandItPlus.Localization.Get("bp_raidbehavior_001", "Bandits");
                            InformationManager.DisplayMessage(new InformationMessage(
                                new TextObject("{=bp_hcr_007}⚔ {FACTION} are raiding {VILLAGE}!")
                                    .SetTextVariable("FACTION", factionName)
                                    .SetTextVariable("VILLAGE", village.Name).ToString(),
                                Colors.Red));
                        }
                    }
                    catch { }
                }

                // Wave 4.15.0-fix46 (2026-06-10): vanilla mimic for raid revert.
                // Engine fires VillageStateChanged BeingRaided → Normal with raider=null
                // 300ms-1s after we trigger a raid. Vanilla LORDS get the same revert
                // but immediately re-trigger their raid (4ms later). Our bandits get
                // reverted and vanilla bandit AI walks them away → no recovery.
                // Fix: when the revert is for OUR tracked target village, find the
                // bp_raider party still targeting it and re-fire SetMoveRaidSettlement
                // to mimic the vanilla lord's re-trigger pattern.
                if (oldState == Village.VillageStates.BeingRaided
                    && newState == Village.VillageStates.Normal)
                {
                    try
                    {
                        MobileParty recoverParty = null;
                        var villageSettlement = village.Settlement;
                        foreach (var kv in _raidTargets)
                        {
                            if (kv.Value == villageSettlement && kv.Key != null && kv.Key.IsActive)
                            {
                                recoverParty = kv.Key;
                                break;
                            }
                        }
                        if (recoverParty != null && villageSettlement != null)
                        {
                            // 2026-07-26: bounded. See kMaxRaidRetriggers above.
                            if (!MayRetriggerRaid(villageSettlement, recoverParty))
                            {
                                // Give up on this village: stop re-arming, unlock the
                                // party's AI and send it home. Leaving it locked with
                                // SetDoNotMakeNewDecisions stranded raiders outside the
                                // village forever (previously-reported symptom).
                                ReleaseRaiderAfterFailedRetriggers(recoverParty, villageSettlement);
                                return;
                            }

                            // Routed through PartyOrderCompat: the old direct call to
                            // SetMoveRaidSettlement(Settlement, NavigationType, bool)
                            // is absent on 1.3.15, and the surrounding try/catch could
                            // NOT catch it — MissingMethodException is raised while
                            // JIT-compiling this method. That is the crash reported at
                            // OnVillageStateChanged.
                            if (BandItPlus.Compat.PartyOrderCompat.RaidSettlement(recoverParty, villageSettlement))
                            {
                                if (recoverParty.Ai != null)
                                    recoverParty.Ai.SetDoNotMakeNewDecisions(true);
                                SafeLog("fix46: re-triggered raid for '" + recoverParty.StringId
                                    + "' at '" + village.Name + "' (attempt "
                                    + _retriggerCount[villageSettlement] + "/" + kMaxRaidRetriggers + ").");
                            }
                            else
                            {
                                SafeLog("fix46: no raid-intent API on this build — releasing '"
                                    + recoverParty.StringId + "' instead of re-triggering.");
                                ReleaseRaiderAfterFailedRetriggers(recoverParty, villageSettlement);
                            }
                        }
                    }
                    catch (Exception fix46Ex)
                    {
                        SafeLog("fix46 re-trigger threw "
                            + fix46Ex.GetType().Name + ": " + fix46Ex.Message);
                    }
                    return;
                }

                if (newState != Village.VillageStates.Looted) return;

                // Wave 4.16.1 (2026-06-11): release EVERY tracked party targeting this
                // village — lead AND reinforcements. Without this they stayed in
                // _raidTargets and the hourly re-issue loop marched them straight back
                // to the (now looted) village forever (user: "after the raid ended
                // they are waiting outside the village and not retreating").
                // Wave 4.16.3 (2026-06-11): stamp the per-village repeat cooldown on the
                // NATIVE path. Previously only the dead simulation path stamped it —
                // user observed Nifdal raided twice within 17 minutes by two cultures.
                try { _lastRaidedVillageHours[village] = CampaignTime.Now.ToHours; }
                catch { }
                // 2026-07-26: the raid resolved — clear its throttle state so a future
                // raid on this village starts from a clean budget.
                ClearRaidThrottleState(village.Settlement);
                // Wave 4.17.1: campaign-lifetime counter for the Diagnostics board.
                _totalVillagesLooted++;

                // Wave 4.18.0: the world answers — a nearby lord rides out to hunt the
                // looters, and the victim clan posts a bounty the player can claim.
                if (raiderParty != null)
                {
                    TriggerLordRetaliation(raiderParty, village);
                    PostRaidBounty(raiderParty, village);
                }

                try
                {
                    var lootedSettlement = village.Settlement;
                    List<MobileParty> toRelease = null;
                    foreach (var rkv in _raidTargets)
                    {
                        if (rkv.Value == lootedSettlement && rkv.Key != null)
                            (toRelease ?? (toRelease = new List<MobileParty>())).Add(rkv.Key);
                    }
                    if (toRelease != null)
                    {
                        foreach (var rp in toRelease)
                        {
                            ReleaseRaider(rp, "raid complete — heading home");
                            _raidTargets.Remove(rp);
                        }
                    }
                }
                catch (Exception relEx)
                {
                    SafeLog("Looted release error: " + relEx.GetType().Name + ": " + relEx.Message);
                }

                if (raiderParty == null) return;

                // Only care if this is one of our tracked raid parties.
                string foundKey = null;
                foreach (var kv in _activeRaiders)
                {
                    if (kv.Value == raiderParty) { foundKey = kv.Key; break; }
                }
                if (string.IsNullOrEmpty(foundKey)) return;

                _activeRaiders.Remove(foundKey);
                _lastRaidDayByCulture[foundKey] = CampaignTime.Now.ToDays;

                SafeLog("Raid complete: " + foundKey + " looted " +
                    (village.Settlement != null ? village.Settlement.Name.ToString() : "village"));

                // Wave 4.16.6: on-screen loot alert (companion to the raid-start alert).
                try
                {
                    var lootFaction = raiderParty != null && raiderParty.MapFaction != null
                        ? raiderParty.MapFaction.Name.ToString() : BandItPlus.Localization.Get("bp_raidbehavior_002", "Bandits");
                    InformationManager.DisplayMessage(new InformationMessage(
                        new TextObject("{=bp_hcr_008}🔥 {VILLAGE} has been looted by {FACTION}!")
                            .SetTextVariable("VILLAGE", village.Name)
                            .SetTextVariable("FACTION", lootFaction).ToString(),
                        Colors.Yellow));
                }
                catch { }
            }
            catch (Exception ex)
            {
                SafeLog("OnVillageStateChanged error: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ───────────────────────── target selection ─────────────────────────

        private List<(Settlement hideout, CultureObject culture)> PickEligibleHideouts()
        {
            var list = new List<(Settlement, CultureObject)>();
            try
            {
                var s = MCMSettings.Instance;
                var nowDays = CampaignTime.Now.ToDays;
                var minGap = s.DaysBetweenRaidsPerCulture;

                foreach (var settlement in Settlement.All)
                {
                    if (settlement == null || !settlement.IsHideout) continue;
                    if (settlement.Hideout == null) continue;
                    if (settlement.Culture == null) continue;

                    var cid = settlement.Culture.StringId;
                    if (string.IsNullOrEmpty(cid)) continue;

                    // Already raiding from this culture? Skip.
                    if (_activeRaiders.ContainsKey(cid)) continue;

                    // Per-culture cooldown. Wave 4.16.0: divided by the yearly scale —
                    // each campaign year bandits grow THIRSTIER and raid more often.
                    double lastDay;
                    if (_lastRaidDayByCulture.TryGetValue(cid, out lastDay))
                    {
                        double effectiveGap = minGap / GetYearlyScale();
                        if (nowDays - lastDay < effectiveGap) continue;
                    }

                    list.Add((settlement, settlement.Culture));
                }
            }
            catch (Exception ex)
            {
                SafeLog("PickEligibleHideouts error: " + ex.GetType().Name + ": " + ex.Message);
            }
            return list;
        }

        private Settlement PickTargetVillage(Settlement hideoutAnchor)
        {
            try
            {
                if (hideoutAnchor == null) return null;

                var maxRange = MCMSettings.Instance.MaxRaidRange;
                var maxRangeSq = maxRange * maxRange;

                Settlement best = null;
                float bestScore = float.MaxValue;

                foreach (var s in Settlement.All)
                {
                    if (s == null || !s.IsVillage) continue;
                    if (s.Village == null) continue;

                    // Skip villages already being raided / recently looted.
                    var vs = s.Village.VillageState;
                    if (vs == Village.VillageStates.BeingRaided) continue;
                    if (vs == Village.VillageStates.Looted) continue;
                    // fix34: skip villages that we raided within the per-village cooldown.
                    if (_lastRaidedVillageHours.TryGetValue(s.Village, out var lastHrs)
                        && (CampaignTime.Now.ToHours - lastHrs) < PER_VILLAGE_REPEAT_COOLDOWN_HOURS)
                    {
                        continue;
                    }

                    // Skip bandit-owned villages (don't raid your kin).
                    if (s.OwnerClan != null && s.OwnerClan.IsBanditFaction) continue;

                    // Skip player-owned villages if MCM toggle says so.
                    if (MCMSettings.Instance.ExcludePlayerOwnedVillages)
                    {
                        if (s.OwnerClan == Clan.PlayerClan) continue;
                    }

                    // Distance via Settlement.Position (NOT Position2D — that fails
                    // to compile on 1.2.x per memory note).
                    var dx = hideoutAnchor.Position.X - s.Position.X;
                    var dy = hideoutAnchor.Position.Y - s.Position.Y;
                    var distSq = dx * dx + dy * dy;
                    if (distSq > maxRangeSq) continue;

                    // Score: distance + random jitter so it's not always the closest village.
                    // Wave 4.18.0: hearth divides the score — bandits prefer RICH
                    // villages. A 600-hearth village outscores a 200-hearth one at
                    // similar range; jitter keeps it non-deterministic.
                    float hearth = s.Village.Hearth;
                    if (hearth < 50f) hearth = 50f;
                    var score = distSq * (0.7f + MBRandom.RandomFloat * 0.6f) / (hearth / 300f);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = s;
                    }
                }

                return best;
            }
            catch (Exception ex)
            {
                SafeLog("PickTargetVillage error: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        // ───────────────────────── spawn ─────────────────────────

        private void SpawnRaidParty(CultureObject culture, Settlement hideoutSettlement, Settlement targetVillage, float power)
        {
            try
            {
                if (culture == null || hideoutSettlement == null || targetVillage == null) return;
                if (hideoutSettlement.Hideout == null) return;

                // Wave 4.15.0-fix3 (2026-06-07): CRASH FIX. The user identified the cause:
                //   "looters and bandits don't have leader, lord or notable that why"
                // Bandit parties with no LeaderHero crash the engine's WarPartyComponent.
                // OnFinalize when they die in a MapEvent. Reflect-set _leaderHero to a
                // real Hero from BanditChiefRegistry after creation.
                Hero leaderHero = null;
                try { leaderHero = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(culture.StringId); }
                catch { }
                if (leaderHero == null || !leaderHero.IsAlive)
                {
                    SafeLog("Skipping raid spawn for " + culture.StringId +
                        " — no chief hero in registry. Vanilla bandit cultures lack leaders" +
                        " and dying without one crashes WarPartyComponent.OnFinalize.");
                    return;
                }

                // Wave 4.15.0-fix5 (2026-06-08): pass the matching bandit Clan to
                // CreateBanditParty instead of null. Log analysis showed CreateBanditParty
                // consistently NRE'd for vanilla cultures (steppe_bandits, desert_bandits,
                // sea_raiders) even with valid terrain + non-null template. Theory: the
                // engine's internal init path for these vanilla bandit cultures requires
                // a real Clan reference (TrySummonWarband-with-null works for BP custom
                // cultures because those have their alliance setup providing it elsewhere).
                Clan banditClan = null;
                try
                {
                    foreach (var c in Clan.All)
                    {
                        if (c == null || !c.IsBanditFaction) continue;
                        if (c.Culture == culture) { banditClan = c; break; }
                    }
                }
                catch { /* defensive — fallthrough to null */ }
                if (banditClan == null)
                {
                    // Fallback: try the leader hero's own clan if it's a bandit faction.
                    try
                    {
                        if (leaderHero.Clan != null && leaderHero.Clan.IsBanditFaction)
                            banditClan = leaderHero.Clan;
                    }
                    catch { }
                }
                if (banditClan == null)
                {
                    SafeLog("Skipping raid spawn for " + culture.StringId +
                        " — no matching bandit Clan found in Clan.All. CreateBanditParty" +
                        " would NRE with null clan for this culture.");
                    return;
                }

                // Wave 4.15.0-fix8 (2026-06-08): fix6's hard skip on `clan.Leader == null`
                // was too strict — it blocked EVERY spawn because all hideouts in the
                // world use vanilla bandit cultures whose clans have null Leaders by
                // design. The actual crash (WarPartyComponent.OnFinalize) walks
                // PARTY.LeaderHero.Clan, NOT clan.Leader. So as long as fix7 successfully
                // sets party._leaderHero = chief AFTER creation, the engine cleanup is
                // safe regardless of the parent clan's Leader state.
                //
                // Log clan.Leader status for forensics but don't block — let fix5's Clan
                // arg get CreateBanditParty past its internal init, then fix7 sets the
                // party leader, then OnFinalize is safe.
                bool clanHasLeader = false;
                try { clanHasLeader = (banditClan.Leader != null); } catch { }
                if (!clanHasLeader)
                {
                    SafeLog("Note: " + culture.StringId + " clan=" + banditClan.StringId +
                        " has no Lord (vanilla bandit design). Proceeding — fix7 will set" +
                        " party.LeaderHero from BanditChiefRegistry chief, which is what" +
                        " WarPartyComponent.OnFinalize actually walks.");
                }

                // Wave 4.15.0-fix1 (2026-06-07): template resolution mirrors the proven
                // SpawnCh2TargetParty / TrySummonWarband pattern — try BanditBossPartyTemplate
                // first, then DefaultPartyTemplate, then ultimately the vanilla
                // looters_template as a last-ditch fallback. Without the looters_template
                // safety net, BP custom cultures whose templates aren't bandit-compatible
                // would cause CreateBanditParty to NRE.
                PartyTemplateObject template = null;
                try { template = culture.BanditBossPartyTemplate; } catch { }
                if (template == null)
                {
                    try { template = culture.DefaultPartyTemplate; } catch { }
                }
                if (template == null)
                {
                    try { template = MBObjectManager.Instance.GetObject<PartyTemplateObject>("looters_template"); } catch { }
                }
                if (template == null)
                {
                    SafeLog("No usable party template for culture " + culture.StringId + " — aborting raid spawn.");
                    return;
                }

                // Wave 4.15.0-fix4 (2026-06-07): terrain-validated spawn position.
                // The engine's CreateBanditParty NREs internally if the spawn point lands
                // on unwalkable terrain (e.g. water, edge of map, or weird hideout-coast
                // geometry). Mirror the proven SpawnCh2TargetParty pattern: try the
                // hideout's exact position first, then a small grid of nearby offsets.
                CampaignVec2 spawnPos;
                if (!TryFindValidSpawnPos(hideoutSettlement.GetPosition2D, out spawnPos))
                {
                    SafeLog("No valid walkable spawn position near " +
                        (hideoutSettlement.Name?.ToString() ?? "?") +
                        " — skipping raid (would NRE inside engine's CreateBanditParty).");
                    return;
                }

                // Synthetic id: include culture + hour stamp for uniqueness.
                string partyId = "bp_raider_" + culture.StringId
                    + "_" + ((int)CampaignTime.Now.ToHours).ToString();

                MobileParty raidParty = null;
                try
                {
                    // isBossParty: true matches the proven TrySummonWarband call — gives
                    // a heavier roster suitable for raiding + avoids the NRE path some
                    // BP custom cultures hit with isBossParty=false.
                    // Wave 4.15.0-fix5: pass banditClan (resolved above) instead of null.
                    raidParty = BanditPartyComponent.CreateBanditParty(
                        partyId,
                        /* clan: */ banditClan,
                        hideoutSettlement.Hideout,
                        /* isBossParty: */ true,
                        template,
                        spawnPos);
                }
                catch (Exception spawnEx)
                {
                    SafeLog("CreateBanditParty threw " + spawnEx.GetType().Name + ": " + spawnEx.Message
                        + " (culture=" + culture.StringId
                        + ", hideout=" + (hideoutSettlement.Name?.ToString() ?? "?")
                        + ", template=" + (template?.StringId ?? "null") + ")");
                    return;
                }
                if (raidParty == null) return;

                // Wave 4.15.0-fix50 (2026-06-10): workflow-verified — DO NOT inject a
                // synthetic raid-captain Hero into MemberRoster. fix44's roster injection
                // gave the engine TWO leader candidates (chief via ChangePartyLeader +
                // captain in roster index 0). WarPartyComponent.OnFinalize walked the
                // wrong candidate when MapEvent collapsed = 7-second death observed in
                // logs. Workflow synthesis cites BanditMilitiasRedux: "NEVER injects a
                // Hero into bandit roster index 0 — and Redux raids work in 1.4.5".
                //
                // Risk 3 fallback (per workflow): keep ChangePartyLeader(chief) for
                // crash-prevention. If CreateRaidEvent NREs without a LeaderHero we can
                // revisit, but tests should run with this clean state first.
                Hero leaderForAssignment = leaderHero;

                // Wave 4.15.0-fix7 (2026-06-08): multi-strategy leader assignment.
                bool leaderSet = TrySetLeaderHero(raidParty, leaderForAssignment);

                // Without the leader successfully set, this party will crash on death.
                // Better to bail now than leave a ticking bomb on the world map.
                if (!leaderSet)
                {
                    SafeLog("Leader assignment failed — abandoning party " + partyId
                        + " (would crash on death without leader).");
                    return;
                }

                // Wave 4.15.0-fix15 (2026-06-08): CRITICAL FIX. User visual confirmation
                // at Nifdal showed only vanilla looters circling — none of MY spawned
                // raiders were there despite "Spawned" log entries. Root cause: vanilla
                // bandit AI was overriding my SetMoveGoToSettlement immediately after
                // the spawn (default "make new decisions" enabled). The party would
                // path-find toward my target for one tick, then bandit-AI would re-
                // route it to random wandering/looting.
                //
                // Fix: SetDoNotMakeNewDecisions(false) BEFORE the move command locks the
                // bandit AI down so my target sticks. Then send the move order.
                // Wave 4.16.0 (2026-06-11): MUSTER PHASE. The party does NOT march
                // immediately. It holds at the hideout gathering recruits each hour
                // (ProcessMusterPhase in OnHourlyTick). Departure happens there once
                // the muster target is reached or the 48h deadline passes.
                try
                {
                    if (raidParty.Ai != null)
                        raidParty.Ai.SetDoNotMakeNewDecisions(false);
                    raidParty.SetMoveModeHold();
                }
                catch (Exception aiEx)
                {
                    SafeLog("muster hold threw " + aiEx.GetType().Name + ": " + aiEx.Message);
                }

                // Wave 4.15.0-fix13 (2026-06-08): beef up the raid party with extra
                // bandit troops post-creation so it's strong enough to commit to
                // raids without "circling the village waiting for reinforcements"
                // (user-observed behavior — vanilla bandit AI waits when below
                // strength-vs-defense threshold). Multiplier scales with PowerFactor
                // so early-game raids stay modest, late-game raids feel scary.
                // Wave 4.16.0 (2026-06-11): spawn at ~40% strength — the rest is
                // GATHERED at the hideout during the muster phase (realistic war-band
                // assembly). Yearly escalation makes each year's bands stronger.
                float yearScale = GetYearlyScale();
                try
                {
                    float baseMultiplier = MCMSettings.Instance.RaidPartySizeMultiplier;
                    float scaled = baseMultiplier * (0.5f + power * 1.5f) * 0.4f;
                    BeefUpRaidParty(raidParty, template, scaled);
                }
                catch (Exception beefEx)
                {
                    SafeLog("BeefUpRaidParty threw " + beefEx.GetType().Name + ": " + beefEx.Message);
                }

                _activeRaiders[culture.StringId] = raidParty;
                // fix16: remember target so OnHourlyTick can re-issue the move command
                // whenever vanilla bandit AI clears the engine's TargetSettlement.
                _raidTargets[raidParty] = targetVillage;

                // Wave 4.16.0: muster bookkeeping. Target scales with power AND the
                // campaign year ("stronger every year, more demanding"). MCM-tunable
                // base size. Departure handled by ProcessMusterPhase.
                int musterBase = 45;
                try { musterBase = MCMSettings.Instance.RaidMusterBaseSize; } catch { }
                int musterTarget = (int)(musterBase * (0.6f + power) * yearScale);
                if (musterTarget < 25) musterTarget = 25;
                _musterTargets[raidParty] = musterTarget;
                _musterDeadlines[raidParty] = CampaignTime.Now.ToHours + MUSTER_MAX_HOURS;

                // Wave 4.16.0: store context for the reinforcement-on-raid-start call.
                // Reinforcements are NO LONGER spawned here — they answer the horn when
                // the raid MapEvent actually begins (OnRaidMapEventStarted).
                _raidContexts[raidParty] = new RaidContext
                {
                    Culture = culture,
                    Hideout = hideoutSettlement,
                    Village = targetVillage,
                    BanditClan = banditClan,
                    Template = template,
                    Leader = leaderHero,
                    Power = power,
                    ReinforcementsCalled = false
                };
                // Wave 4.16.1 (2026-06-11): apply the culture chief's clan banner NOW
                // instead of waiting for the hourly v162 sweep — user saw vanilla
                // banners on freshly spawned raid war bands.
                try { BandItPlus.BanditPower.BanditPartyBannerBehavior.TryApply(raidParty); }
                catch { }

                SafeLog("muster: '" + partyId + "' gathering at " + hideoutSettlement.Name
                    + " — target " + musterTarget + " troops (yearScale="
                    + yearScale.ToString("0.00") + "), deadline +"
                    + MUSTER_MAX_HOURS + "h, then raids " + targetVillage.Name);

                // Wave 4.15.0-fix11 (2026-06-08): set the per-culture cooldown HERE at
                // spawn time, not only on raid completion. Otherwise the same culture
                // can spawn multiple raid parties in quick succession while previous
                // ones are still in transit (observed in fix9 logs: mountain_bandits
                // spawned 12:59:49 + 13:06:58, desert_bandits 12:59:31 + 13:07:15).
                // OnVillageStateChanged still re-stamps the cooldown when the raid
                // completes — that's just a refresh.
                _lastRaidDayByCulture[culture.StringId] = CampaignTime.Now.ToDays;

                SafeLog("Spawned " + culture.StringId + " raider '" + partyId
                    + "' from " + hideoutSettlement.Name
                    + " → target " + targetVillage.Name);
            }
            catch (Exception ex)
            {
                SafeLog("SpawnRaidParty error: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ───────────────────────── leader assignment (multi-strategy) ─────────────────────────

        // Cached after first successful resolution.
        private static FieldInfo _cachedLeaderField;
        private static PropertyInfo _cachedLeaderProp;
        private static bool _leaderLookupAttempted;

        // Wave 4.15.0-fix7+fix9+fix10 (2026-06-08): bulletproof leader-hero assignment
        // across Bannerlord 1.2.x → 1.4.5 War Sails. Strategy chain in order:
        //   0) MobileParty.ChangePartyLeader(hero) — OFFICIAL vanilla API (deep-research
        //      finding from apidoc.bannerlord.com + docs.bannerlordmodding.lt). Tried
        //      via method-lookup-by-reflection so absent versions fall through cleanly.
        //   1) typed field by known name candidates (_leaderHero, backing field, _partyLeader)
        //   2) public property LeaderHero with non-public setter
        //   3) scan all NonPublic instance Hero fields whose name contains "leader"
        //   4) hero.PartyBelongedTo = party (Hero-side ownership)
        //   5) PartyBase.SetCustomOwner(chief) — VERIFIED working in 1.4.5 War Sails
        //      per fix9 log analysis (workflow wi6cqfock).
        // Caches the first successful path so subsequent calls are O(1).
        private bool TrySetLeaderHero(MobileParty party, Hero hero)
        {
            if (party == null || hero == null) return false;
            try
            {
                // Strategy 0 — official ChangePartyLeader method (preferred when present).
                try
                {
                    var changeMethod = typeof(MobileParty).GetMethod(
                        "ChangePartyLeader",
                        BindingFlags.Instance | BindingFlags.Public,
                        null,
                        new[] { typeof(Hero) },
                        null);
                    if (changeMethod != null)
                    {
                        changeMethod.Invoke(party, new object[] { hero });
                        SafeLog("Called official MobileParty.ChangePartyLeader(chief) — clean path");
                        return true;
                    }
                }
                catch (Exception cplEx)
                {
                    SafeLog("ChangePartyLeader threw " + cplEx.GetType().Name + ": " + cplEx.Message + " — falling through");
                }
                if (!_leaderLookupAttempted)
                {
                    var t = typeof(MobileParty);
                    string[] fieldCandidates = new[]
                    {
                        "_leaderHero",                       // pre-1.4.x classic
                        "<LeaderHero>k__BackingField",       // auto-property backing field
                        "_partyLeader",                      // alternate naming
                        "_leader"
                    };
                    foreach (var name in fieldCandidates)
                    {
                        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                        if (f != null && f.FieldType == typeof(Hero))
                        {
                            _cachedLeaderField = f;
                            SafeLog("Resolved leader field via name '" + name + "'");
                            break;
                        }
                    }
                    if (_cachedLeaderField == null)
                    {
                        // Try the public LeaderHero property — some versions expose
                        // a non-public setter accessible only via reflection.
                        var p = t.GetProperty("LeaderHero", BindingFlags.Instance | BindingFlags.Public);
                        if (p != null && p.CanWrite)
                        {
                            _cachedLeaderProp = p;
                            SafeLog("Resolved leader via public property LeaderHero setter");
                        }
                    }
                    if (_cachedLeaderField == null && _cachedLeaderProp == null)
                    {
                        // Final fallback: scan all NonPublic instance Hero fields,
                        // pick the one with "leader" in its name (case-insensitive).
                        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                        {
                            if (f.FieldType != typeof(Hero)) continue;
                            if (!f.Name.ToLower().Contains("leader")) continue;
                            _cachedLeaderField = f;
                            SafeLog("Resolved leader field via scan: '" + f.Name + "'");
                            break;
                        }
                    }
                    _leaderLookupAttempted = true;
                }

                if (_cachedLeaderField != null)
                {
                    _cachedLeaderField.SetValue(party, hero);
                    return true;
                }
                if (_cachedLeaderProp != null)
                {
                    _cachedLeaderProp.SetValue(party, hero);
                    return true;
                }

                // Wave 4.15.0-fix9 (2026-06-08): all MobileParty-level reflection failed
                // (likely 1.4.5 moved LeaderHero off the type). Try alternative APIs:
                //
                // (a) Set hero.PartyBelongedTo = raidParty — Hero-side ownership.
                //     The engine's getter for PartyBase.LeaderHero typically derives
                //     from looking up which Hero has its PartyBelongedTo pointing at
                //     this party. So setting from the hero side achieves the same.
                bool setHeroPartySide = false;
                try
                {
                    var pblProp = typeof(Hero).GetProperty("PartyBelongedTo",
                        BindingFlags.Instance | BindingFlags.Public);
                    if (pblProp != null && pblProp.CanWrite)
                    {
                        pblProp.SetValue(hero, party);
                        setHeroPartySide = true;
                        SafeLog("Set hero.PartyBelongedTo via public property setter");
                    }
                    else
                    {
                        // Try the private backing field on Hero.
                        var pblField = typeof(Hero).GetField("_partyBelongedTo",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        if (pblField != null)
                        {
                            pblField.SetValue(hero, party);
                            setHeroPartySide = true;
                            SafeLog("Set hero._partyBelongedTo via reflection");
                        }
                    }
                }
                catch (Exception pblEx)
                {
                    SafeLog("Hero.PartyBelongedTo set threw " + pblEx.GetType().Name + ": " + pblEx.Message);
                }

                // (b) Try PartyBase.SetCustomOwner — public API in many versions.
                try
                {
                    var partyBase = party.Party;
                    if (partyBase != null)
                    {
                        var setOwnerMethod = partyBase.GetType().GetMethod(
                            "SetCustomOwner",
                            BindingFlags.Instance | BindingFlags.Public,
                            null,
                            new[] { typeof(Hero) },
                            null);
                        if (setOwnerMethod != null)
                        {
                            setOwnerMethod.Invoke(partyBase, new object[] { hero });
                            SafeLog("Called PartyBase.SetCustomOwner(chief)");
                            return true;  // Most likely successful path
                        }
                    }
                }
                catch (Exception socEx)
                {
                    SafeLog("PartyBase.SetCustomOwner threw " + socEx.GetType().Name + ": " + socEx.Message);
                }

                // (c) If we at least set hero.PartyBelongedTo, treat as success.
                //     The party.LeaderHero getter typically derives from this.
                if (setHeroPartySide) return true;

                SafeLog("All leader-resolution strategies failed (engine version mismatch). " +
                    "MobileParty leader cannot be assigned for raid party.");
                return false;
            }
            catch (Exception ex)
            {
                SafeLog("TrySetLeaderHero threw " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // ───────────────────────── terrain validation ─────────────────────────

        // Wave 4.15.0-fix4 (2026-06-07): proven SpawnCh2TargetParty pattern.
        // Try the anchor position first. If the engine's GetFaceIndex says it's not
        // walkable, walk a 4x4 grid of small offsets looking for a valid face. Return
        // false if nothing nearby works — caller must skip the spawn entirely.
        private bool TryFindValidSpawnPos(Vec2 anchor, out CampaignVec2 result)
        {
            result = new CampaignVec2(anchor, /* isOnLand: */ true);
            try
            {
                var sceneWrapper = Campaign.Current?.MapSceneWrapper;
                if (sceneWrapper == null)
                {
                    // No scene wrapper available — accept the anchor and let the engine try.
                    return true;
                }

                // 1) Initial position check.
                bool valid = false;
                try
                {
                    var face = sceneWrapper.GetFaceIndex(result);
                    valid = face.IsValid();
                }
                catch (Exception faceEx)
                {
                    SafeLog("GetFaceIndex(initial) threw " + faceEx.GetType().Name + ": " + faceEx.Message);
                }
                if (valid) return true;

                // 2) Grid retry — same offsets pattern as SpawnCh2TargetParty.
                float[] offsets = { -0.5f, 0.0f, 0.5f, 1.0f };
                for (int i = 0; i < offsets.Length; i++)
                {
                    for (int j = 0; j < offsets.Length; j++)
                    {
                        Vec2 candVec = anchor + new Vec2(offsets[i], offsets[j]);
                        CampaignVec2 cand = new CampaignVec2(candVec, /* isOnLand: */ true);
                        try
                        {
                            var face = sceneWrapper.GetFaceIndex(cand);
                            if (face.IsValid())
                            {
                                result = cand;
                                return true;
                            }
                        }
                        catch { /* defensive — try next offset */ }
                    }
                }

                // 3) Nothing valid nearby.
                return false;
            }
            catch (Exception ex)
            {
                SafeLog("TryFindValidSpawnPos error: " + ex.GetType().Name + ": " + ex.Message);
                // Fall back to the unvalidated anchor — slight risk of engine NRE,
                // but the outer try/catch on CreateBanditParty still catches it.
                return true;
            }
        }

        // ───────────────────────── Wave 4.18.0: the world answers ─────────────────────────

        // Bounty on loot-laden returning war bands. Runtime-only (not saved) — a
        // bounty that evaporates on reload is acceptable; they expire in 3 days anyway.
        private class BountyInfo
        {
            public int Gold;
            public Clan OwnerClan;
            public double ExpiresHours;
            public string VillageName;
            public Settlement HomeHideout;
        }
        private Dictionary<MobileParty, BountyInfo> _bounties = new Dictionary<MobileParty, BountyInfo>();

        // Lord retaliation: the victim kingdom's nearest free lord gets a hunt order
        // against the returning raiders. No spawned parties — real lords only (they
        // are already at war with the chief clan via DeclareRaidWar).
        private void TriggerLordRetaliation(MobileParty raider, Village village)
        {
            try
            {
                if (raider == null || !raider.IsActive) return;
                if (village == null || village.Settlement == null) return;
                var kingdom = village.Settlement.MapFaction;
                if (kingdom == null) return;

                MobileParty bestLord = null;
                float bestSq = 80f * 80f; // only lords within 80 world units answer
                foreach (var p in MobileParty.All)
                {
                    if (p == null || !p.IsLordParty || !p.IsActive) continue;
                    if (p.MapFaction != kingdom) continue;
                    if (p.Army != null) continue;     // don't yank armies off their tasks
                    if (p.MapEvent != null) continue; // busy fighting
                    var dx = p.Position.X - village.Settlement.Position.X;
                    var dy = p.Position.Y - village.Settlement.Position.Y;
                    var dsq = dx * dx + dy * dy;
                    if (dsq < bestSq) { bestSq = dsq; bestLord = p; }
                }
                if (bestLord == null)
                {
                    SafeLog("retaliation: no free " + kingdom.Name + " lord within range of " + village.Name);
                    return;
                }
                BandItPlus.Compat.PartyOrderCompat.EngageParty(bestLord, raider);
                SafeLog("retaliation: '"
                    + (bestLord.LeaderHero != null ? bestLord.LeaderHero.Name.ToString() : bestLord.StringId)
                    + "' hunts '" + raider.StringId + "' for the raid on " + village.Name);
                try
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        new TextObject("{=bp_hcr_009}⚔ {LORD} rides to avenge {VILLAGE}!")
                            .SetTextVariable("LORD", bestLord.LeaderHero != null ? bestLord.LeaderHero.Name.ToString() : BandItPlus.Localization.Get("bp_raidbehavior_003", "A lord"))
                            .SetTextVariable("VILLAGE", village.Name).ToString(), Colors.Red));
                }
                catch { }
            }
            catch (Exception ex)
            {
                SafeLog("TriggerLordRetaliation threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Post a bounty on the looter. Claimed by the PLAYER destroying the band
        // before it reaches its hideout (OnMobilePartyDestroyedBounty).
        private void PostRaidBounty(MobileParty raider, Village village)
        {
            try
            {
                if (raider == null || !raider.IsActive) return;
                if (village == null || village.Settlement == null) return;
                if (_bounties.ContainsKey(raider)) return;
                var ownerClan = village.Settlement.OwnerClan;
                if (ownerClan == null || ownerClan == Clan.PlayerClan) return;

                int troops = raider.MemberRoster != null ? raider.MemberRoster.TotalManCount : 50;
                int gold = 300 + troops * 5;
                RaidContext ctx;
                _raidContexts.TryGetValue(raider, out ctx);
                _bounties[raider] = new BountyInfo
                {
                    Gold = gold,
                    OwnerClan = ownerClan,
                    ExpiresHours = CampaignTime.Now.ToHours + 72.0,
                    VillageName = village.Name.ToString(),
                    HomeHideout = ctx != null && ctx.Hideout != null ? ctx.Hideout : raider.HomeSettlement
                };
                try
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        new TextObject("{=bp_hcr_010}💰 The {CLAN} offer {GOLD} gold for the heads of the {VILLAGE} raiders!")
                            .SetTextVariable("CLAN", ownerClan.Name)
                            .SetTextVariable("GOLD", gold)
                            .SetTextVariable("VILLAGE", village.Name).ToString(),
                        Colors.Yellow));
                }
                catch { }
                SafeLog("bounty: " + gold + " gold on '" + raider.StringId
                    + "' (raided " + village.Name + ", expires +72h)");
            }
            catch (Exception ex)
            {
                SafeLog("PostRaidBounty threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnMobilePartyDestroyedBounty(MobileParty party, PartyBase destroyer)
        {
            try
            {
                if (party == null) return;

                bool playerKill = destroyer != null
                    && (destroyer == PartyBase.MainParty
                        || (destroyer.MobileParty != null && destroyer.MobileParty == MobileParty.MainParty)
                        || (destroyer.LeaderHero != null && destroyer.LeaderHero == Hero.MainHero));

                BountyInfo b;
                if (!_bounties.TryGetValue(party, out b))
                {
                    // Wave 4.20.0 origin perk — LAWKEEPER: "every head has a price."
                    // Any bandit party the player destroys pays flat head-money,
                    // even without a posted raid bounty.
                    if (playerKill && party.IsBandit)
                    {
                        try
                        {
                            var originsHm = BandItPlus.Origins.BanditOriginBehavior.Instance;
                            if (originsHm != null
                                && originsHm.Origin == BandItPlus.Origins.BanditOriginBehavior.BanditOrigin.Lawkeeper)
                            {
                                const int HEAD_MONEY = 120;
                                TaleWorlds.CampaignSystem.Actions.GiveGoldAction
                                    .ApplyBetweenCharacters(null, Hero.MainHero, HEAD_MONEY, false);
                                InformationManager.DisplayMessage(new InformationMessage(
                                    new TextObject("{=bp_hcr_011}⚖ Head-money: {GOLD} gold for the {PARTY} (Lawkeeper).")
                                        .SetTextVariable("GOLD", HEAD_MONEY)
                                        .SetTextVariable("PARTY", party.Name).ToString(), Colors.Yellow));
                            }
                        }
                        catch { }
                    }
                    return;
                }
                _bounties.Remove(party);
                if (!playerKill) return;

                // Wave 4.19.0: Lawkeeper origin earns +25% on bandit bounties.
                float bountyMult = 1.0f;
                try
                {
                    var origins = BandItPlus.Origins.BanditOriginBehavior.Instance;
                    if (origins != null) bountyMult = origins.BountyMultiplier;
                }
                catch { }
                int payout = (int)(b.Gold * bountyMult);
                TaleWorlds.CampaignSystem.Actions.GiveGoldAction
                    .ApplyBetweenCharacters(null, Hero.MainHero, payout, false);
                if (b.OwnerClan != null && b.OwnerClan.Leader != null && b.OwnerClan.Leader != Hero.MainHero)
                {
                    try
                    {
                        TaleWorlds.CampaignSystem.Actions.ChangeRelationAction
                            .ApplyRelationChangeBetweenHeroes(Hero.MainHero, b.OwnerClan.Leader, 5, true);
                    }
                    catch { }
                }
                try
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        new TextObject("{=bp_hcr_012}💰 Bounty claimed! {GOLD} gold from the {CLAN} for slaying the {VILLAGE} raiders.")
                            .SetTextVariable("GOLD", payout)
                            .SetTextVariable("CLAN", b.OwnerClan != null ? b.OwnerClan.Name.ToString() : BandItPlus.Localization.Get("bp_raidbehavior_004", "village"))
                            .SetTextVariable("VILLAGE", b.VillageName).ToString()
                        + (bountyMult > 1.0f ? BandItPlus.Localization.Get("bp_raidbehavior_005", " (Lawkeeper bonus)") : ""),
                        Colors.Yellow));
                }
                catch { }
                SafeLog("bounty CLAIMED by player: " + b.Gold + " gold (" + b.VillageName + " raiders)");
            }
            catch (Exception ex)
            {
                SafeLog("OnMobilePartyDestroyedBounty threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Hourly: drop bounties that expired or whose band made it home.
        private void SweepBounties()
        {
            if (_bounties.Count == 0) return;
            List<MobileParty> drop = null;
            try
            {
                double nowH = CampaignTime.Now.ToHours;
                foreach (var kv in _bounties)
                {
                    var p = kv.Key;
                    var b = kv.Value;
                    if (p == null || !p.IsActive)
                    {
                        (drop ?? (drop = new List<MobileParty>())).Add(p);
                        continue;
                    }
                    bool home = p.CurrentSettlement != null && b.HomeHideout != null
                        && p.CurrentSettlement == b.HomeHideout;
                    if (home || nowH >= b.ExpiresHours)
                    {
                        (drop ?? (drop = new List<MobileParty>())).Add(p);
                        if (home)
                            SafeLog("bounty expired: '" + p.StringId + "' reached its hideout — loot secured.");
                    }
                }
            }
            catch { }
            if (drop != null)
                foreach (var p in drop) _bounties.Remove(p);
        }

        // ───────────────────────── Wave 4.17.1 diagnostics provider ─────────────────────────

        // Snapshot for the Royal Codex Diagnostics chapter's Raid Operations board.
        // Pure read — formats live tracking state into display strings so the console
        // VM stays decoupled from raid internals.
        public sealed class RaidDiagnostics
        {
            public List<string> Operations = new List<string>();
            public string LootedTotal = "0";
            public string Escalation = "1.00x";
            public string Readiness = "";
        }

        private static readonly string[] _diagCultures =
            { "forest_bandits", "mountain_bandits", "steppe_bandits", "desert_bandits", "sea_raiders" };

        public RaidDiagnostics GetRaidDiagnostics()
        {
            var d = new RaidDiagnostics();
            try
            {
                d.LootedTotal = _totalVillagesLooted.ToString();

                float scale = GetYearlyScale();
                double yearsElapsed = 0.0;
                if (_escalationBaselineDays >= 0.0)
                    yearsElapsed = (CampaignTime.Now.ToDays - _escalationBaselineDays) / DAYS_PER_GAME_YEAR;
                d.Escalation = new TextObject("{=bp_raidesc}{SCALE}x  (year {YEARS})")
                    .SetTextVariable("SCALE", scale.ToString("0.00"))
                    .SetTextVariable("YEARS", yearsElapsed.ToString("0.0")).ToString();

                // Active operations — every tracked war band with a target.
                foreach (var kv in _raidTargets)
                {
                    if (d.Operations.Count >= 5) break;
                    var p = kv.Key;
                    var v = kv.Value;
                    if (p == null || !p.IsActive || v == null) continue;

                    string faction = p.MapFaction != null ? p.MapFaction.Name.ToString() : BandItPlus.Localization.Get("bp_raidbehavior_006", "Bandits");
                    int troops = p.MemberRoster != null ? p.MemberRoster.TotalManCount : 0;
                    string status;
                    int musterTarget;
                    if (_musterTargets.TryGetValue(p, out musterTarget))
                        status = "mustering " + troops + "/" + musterTarget;
                    else if (p.MapEvent != null)
                        status = BandItPlus.Localization.Get("bp_raidbehavior_007", "RAIDING now");
                    else
                        status = "marching (" + troops + " men)";
                    d.Operations.Add(faction + " → " + v.Name + " — " + status);
                }

                // Per-culture readiness (escalation-adjusted cooldown remaining).
                double gapDays = 14.0;
                try { gapDays = MCMSettings.Instance.DaysBetweenRaidsPerCulture; } catch { }
                gapDays = gapDays / scale;
                double nowDays = CampaignTime.Now.ToDays;
                var parts = new List<string>();
                foreach (var cid in _diagCultures)
                {
                    string name = BandItPlus.Origins.BanditOriginBehavior.CultureDisplay(cid);
                    double lastDay;
                    if (_lastRaidDayByCulture.TryGetValue(cid, out lastDay))
                    {
                        double remaining = gapDays - (nowDays - lastDay);
                        parts.Add(remaining <= 0.0
                            ? new TextObject("{=bp_raidready_ready}{CULTURE} ready").SetTextVariable("CULTURE", name).ToString()
                            : new TextObject("{=bp_raidready_days}{CULTURE} {DAYS}d").SetTextVariable("CULTURE", name).SetTextVariable("DAYS", remaining.ToString("0.0")).ToString());
                    }
                    else
                    {
                        parts.Add(new TextObject("{=bp_raidready_ready}{CULTURE} ready").SetTextVariable("CULTURE", name).ToString());
                    }
                }
                d.Readiness = new TextObject("{=bp_raidready_next}Next raids:   {LIST}").SetTextVariable("LIST", string.Join("  ·  ", parts)).ToString();
            }
            catch (Exception ex)
            {
                SafeLog("GetRaidDiagnostics threw " + ex.GetType().Name + ": " + ex.Message);
            }
            return d;
        }

        // ───────────────────────── Wave 4.16.0 muster + escalation ─────────────────────────

        // Yearly escalation: 1.0 at campaign start of the raid feature, +25% per
        // in-game year (84 days), capped at 4x. Baseline persisted in SyncData so
        // the escalation survives save/load. Drives muster size, reinforcement
        // count, and raid frequency ("stronger, thirstier, more demanding").
        private float GetYearlyScale()
        {
            try
            {
                double nowDays = CampaignTime.Now.ToDays;
                if (_escalationBaselineDays < 0.0) _escalationBaselineDays = nowDays;
                float years = (float)((nowDays - _escalationBaselineDays) / DAYS_PER_GAME_YEAR);
                if (years < 0f) years = 0f;
                float escalationPct = ESCALATION_PER_YEAR;
                try
                {
                    // MCM/console-tunable: RaidYearlyEscalationPercent (0 disables).
                    escalationPct = MCMSettings.Instance.RaidYearlyEscalationPercent / 100f;
                }
                catch { }
                float scale = 1f + escalationPct * years;
                if (scale < 1f) scale = 1f;
                return scale > ESCALATION_CAP ? ESCALATION_CAP : scale;
            }
            catch { return 1f; }
        }

        // Hourly muster advance: each mustering party gathers recruits at its hideout.
        // When the target is met (or 48h deadline hits) the war band departs for its
        // village. Dead parties get cleaned up.
        private void ProcessMusterPhase()
        {
            if (_musterTargets.Count == 0) return;
            List<MobileParty> done = null;
            try
            {
                foreach (var kv in _musterTargets)
                {
                    var p = kv.Key;
                    int target = kv.Value;
                    if (p == null || !p.IsActive || p.MemberRoster == null)
                    {
                        (done ?? (done = new List<MobileParty>())).Add(p);
                        continue;
                    }
                    RaidContext ctx;
                    _raidContexts.TryGetValue(p, out ctx);

                    int current = p.MemberRoster.TotalManCount;
                    if (current < target && ctx != null && ctx.Template != null)
                    {
                        // 4-9 recruits per hour answer the muster call.
                        int gain = 4 + MBRandom.RandomInt(0, 6);
                        AddMusterRecruits(p, ctx.Template, gain);
                        current = p.MemberRoster.TotalManCount;
                    }

                    double deadline;
                    _musterDeadlines.TryGetValue(p, out deadline);
                    bool ready = current >= target;
                    bool overdue = CampaignTime.Now.ToHours >= deadline;
                    if (!ready && !overdue)
                    {
                        // Keep the band parked at the hideout while gathering.
                        try { p.SetMoveModeHold(); } catch { }
                        continue;
                    }

                    // DEPART — issue the march order (same chain as the re-issue loop).
                    (done ?? (done = new List<MobileParty>())).Add(p);
                    var village = ctx != null ? ctx.Village : null;
                    if (village == null)
                    {
                        Settlement t;
                        _raidTargets.TryGetValue(p, out t);
                        village = t;
                    }
                    if (village == null) continue;
                    try
                    {
                        if (p.Ai != null) p.Ai.SetDoNotMakeNewDecisions(false);
                        Vec2 villagePoint = new Vec2(village.Position.X, village.Position.Y);
                        if (!TryMoveGoToPoint(p, villagePoint))
                        {
                            if (!TryRaidSettlement(p, village))
                            {
                                BandItPlus.Compat.PartyOrderCompat.GoToSettlement(p, village);
                            }
                        }
                        SafeLog("muster: '" + p.StringId + "' DEPARTS with " + current + "/"
                            + target + " troops" + (overdue && !ready ? " (deadline)" : "")
                            + " → raiding " + village.Name);

                        // Wave 4.16.1 (2026-06-11): call reinforcements NOW, at departure,
                        // so they march alongside the war band. The previous trigger
                        // (MapEventStarted) fired when the raid began — raids resolve in
                        // ~1 in-game hour, so backup always arrived after it was over
                        // (user-observed). MapEventStarted hook remains as a no-op
                        // fallback via the ReinforcementsCalled flag.
                        if (ctx != null && !ctx.ReinforcementsCalled)
                        {
                            ctx.ReinforcementsCalled = true;
                            float ys = GetYearlyScale();
                            int rc = 1 + (int)Math.Floor(ctx.Power * 1.5f * ys);
                            if (rc > 3) rc = 3;
                            if (rc < 1) rc = 1;
                            SafeLog("REINFORCEMENT CALL: " + rc + " war band(s) ride out from "
                                + (ctx.Hideout != null ? ctx.Hideout.Name.ToString() : "?")
                                + " alongside the raid on " + village.Name
                                + " (yearScale=" + ys.ToString("0.00") + ")");
                            SpawnReinforcementParties(ctx.Culture, ctx.Hideout, village,
                                ctx.Leader, ctx.BanditClan, ctx.Template, rc, ctx.Power);
                        }
                    }
                    catch (Exception mEx)
                    {
                        SafeLog("muster depart threw " + mEx.GetType().Name + ": " + mEx.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                SafeLog("ProcessMusterPhase error: " + ex.GetType().Name + ": " + ex.Message);
            }
            if (done != null)
            {
                foreach (var p in done)
                {
                    _musterTargets.Remove(p);
                    _musterDeadlines.Remove(p);
                }
            }
        }

        // Wave 4.16.2 (2026-06-11): war declaration at raid start. Mirrors the old
        // fix36 logic (user: "if the bandits raid the village they should be at war
        // with that clan") but fires on the NATIVE raid path. Declares against the
        // village's owner CLAN first (clan-level hostility), then against the owning
        // KINGDOM if separate — both guarded by IsAtWarWith so repeat raids no-op.
        // DeclareWarAction.ApplyByKingdomDecision(IFaction, IFaction) is probe-verified
        // PUBLIC in 1.4.5.
        private void DeclareRaidWar(MobileParty raider, Settlement village)
        {
            try
            {
                var banditFaction = raider != null ? raider.MapFaction : null;
                if (banditFaction == null || village == null) return;

                var ownerClan = (IFaction)village.OwnerClan;
                var ownerKingdom = (IFaction)village.MapFaction;
                // (Wave 4.19.1: the per-raid "war DIAG" dumps from 4.16.5 are
                // retired — the war path is proven; the "war:" action logs below
                // still record every actual declaration.)

                if (ownerClan != null && ownerClan != banditFaction
                    && !banditFaction.IsAtWarWith(ownerClan))
                {
                    TaleWorlds.CampaignSystem.Actions.DeclareWarAction
                        .ApplyByKingdomDecision(banditFaction, ownerClan);
                    SafeLog("war: '" + banditFaction.Name + "' declares war on clan '"
                        + ownerClan.Name + "' (raiding " + village.Name + ")");
                }
                if (ownerKingdom != null && ownerKingdom != ownerClan
                    && ownerKingdom != banditFaction
                    && !banditFaction.IsAtWarWith(ownerKingdom))
                {
                    TaleWorlds.CampaignSystem.Actions.DeclareWarAction
                        .ApplyByKingdomDecision(banditFaction, ownerKingdom);
                    SafeLog("war: '" + banditFaction.Name + "' declares war on kingdom '"
                        + ownerKingdom.Name + "' (raiding " + village.Name + ")");
                }
                else if (ownerKingdom != null
                    && banditFaction.IsAtWarWith(ownerKingdom))
                {
                    // Bandit factions are hardcoded at war with every kingdom by the
                    // engine — nothing to declare, hostility already exists. Logged so
                    // the war state is visible (the Kingdoms diplomacy screen only
                    // lists kingdom-vs-kingdom wars, so bandit wars never show there).
                    SafeLog("war: '" + banditFaction.Name + "' already at war with '"
                        + ownerKingdom.Name + "' (engine default for bandit factions) — raid on "
                        + village.Name + " proceeds under existing hostility.");
                }

                // Wave 4.16.3 (2026-06-11): declare via the CHIEF'S DEDICATED CLAN too.
                // The raid party's faction is a vanilla bandit clan — those are
                // perpetually at war by engine default AND filtered out of every
                // diplomacy/encyclopedia Wars list, so the player never SEES the war
                // (user screenshot: Skylfing's Wars showed only Vlandia). The chief's
                // dedicated clan (whose banner the war band flies) is a regular clan —
                // a DeclareWarAction stance from IT shows up in the UI like vanilla
                // minor-faction wars do.
                try
                {
                    string cultureId = banditFaction.Culture != null ? banditFaction.Culture.StringId : null;
                    var chief = !string.IsNullOrEmpty(cultureId)
                        ? BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(cultureId)
                        : null;
                    var chiefClan = chief != null ? chief.Clan : null;
                    // Wave 4.16.5: no !IsBanditFaction guard here — some chief dedicated
                    // clans ARE bandit-flagged (spawn code reuses leaderHero.Clan when
                    // IsBanditFaction), and bandit-flagged custom clans hold war stances
                    // just fine (Mountain Bandits' visible Sturgia war proved it).
                    if (chiefClan != null
                        && (IFaction)chiefClan != ownerKingdom
                        && ownerKingdom != null
                        && !chiefClan.IsAtWarWith(ownerKingdom))
                    {
                        TaleWorlds.CampaignSystem.Actions.DeclareWarAction
                            .ApplyByKingdomDecision(chiefClan, ownerKingdom);
                        SafeLog("war: chief clan '" + chiefClan.Name + "' declares war on '"
                            + ownerKingdom.Name + "' (raiding " + village.Name
                            + ") — visible in diplomacy/encyclopedia.");
                    }
                }
                catch (Exception chiefWarEx)
                {
                    SafeLog("war: chief-clan declaration threw "
                        + chiefWarEx.GetType().Name + ": " + chiefWarEx.Message);
                }
            }
            catch (Exception ex)
            {
                SafeLog("DeclareRaidWar threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Wave 4.16.1 (2026-06-11): post-raid release. Re-enables vanilla AI and
        // sends the band home to its hideout; vanilla bandit AI takes over on
        // arrival (patrol/garrison). Cleans muster + context tracking. The caller
        // removes the party from _raidTargets (iteration safety).
        private void ReleaseRaider(MobileParty p, string reason)
        {
            try
            {
                if (p != null && p.IsActive)
                {
                    RaidContext rc;
                    _raidContexts.TryGetValue(p, out rc);
                    var home = (rc != null && rc.Hideout != null) ? rc.Hideout : p.HomeSettlement;
                    if (p.Ai != null) p.Ai.SetDoNotMakeNewDecisions(false);
                    if (home != null)
                    {
                        BandItPlus.Compat.PartyOrderCompat.GoToSettlement(p, home);
                    }
                    else
                    {
                        p.SetMoveModeHold();
                    }
                    SafeLog("release: '" + p.StringId + "' heading home — " + reason);
                }
            }
            catch (Exception ex)
            {
                SafeLog("ReleaseRaider threw " + ex.GetType().Name + ": " + ex.Message);
            }
            _raidContexts.Remove(p);
            _musterTargets.Remove(p);
            _musterDeadlines.Remove(p);
        }

        // Adds `count` recruits from a random stack of the culture's party template.
        // PartyTemplateStack is a struct in 1.4.x — index + direct null-check.
        private void AddMusterRecruits(MobileParty party, PartyTemplateObject template, int count)
        {
            try
            {
                if (party == null || party.MemberRoster == null || count <= 0) return;
                if (template == null || template.Stacks == null || template.Stacks.Count == 0) return;
                var stack = template.Stacks[MBRandom.RandomInt(0, template.Stacks.Count)];
                if (stack.Character == null) return;
                party.MemberRoster.AddToCounts(stack.Character, count);
            }
            catch { /* never let muster math break the hourly tick */ }
        }

        // Wave 4.16.0: the CALL FOR REINFORCEMENTS. Fires when the engine starts a
        // raid MapEvent whose attacker is one of our tracked war bands. The hideout
        // answers with 1-3 reinforcement bands (count scales with power AND year).
        private void OnRaidMapEventStarted(MapEvent mapEvent, PartyBase attacker, PartyBase defender)
        {
            try
            {
                if (mapEvent == null || attacker == null || attacker.MobileParty == null) return;
                if (!mapEvent.IsRaid) return;
                var party = attacker.MobileParty;
                RaidContext ctx;
                if (!_raidContexts.TryGetValue(party, out ctx) || ctx == null) return;
                if (ctx.ReinforcementsCalled) return;
                ctx.ReinforcementsCalled = true;

                float yearScale = GetYearlyScale();
                int count = 1 + (int)Math.Floor(ctx.Power * 1.5f * yearScale);
                if (count > 3) count = 3;
                if (count < 1) count = 1;
                SafeLog("REINFORCEMENT CALL: raid on "
                    + (ctx.Village != null ? ctx.Village.Name.ToString() : "?")
                    + " has begun — " + count + " war band(s) ride out from "
                    + (ctx.Hideout != null ? ctx.Hideout.Name.ToString() : "?")
                    + " (yearScale=" + yearScale.ToString("0.00") + ")");
                SpawnReinforcementParties(ctx.Culture, ctx.Hideout, ctx.Village,
                    ctx.Leader, ctx.BanditClan, ctx.Template, count, ctx.Power);
            }
            catch (Exception ex)
            {
                SafeLog("OnRaidMapEventStarted threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ───────────────────────── fix50 real raid event creator ─────────────────────────

        // Wave 4.15.0-fix50 (2026-06-10): the SINGLE verified API call that creates a
        // real vanilla raid MapEvent in 1.4.5. Resolved once + cached. Replaces the entire
        // fix28/30/31/33/38/39 simulation chain. After this call returns true, the engine
        // owns the raid: it creates the MapEvent, sets Village state, starts the per-tick
        // RaidEventComponent.Update damage drain, progresses to Looted naturally.
        private static System.Reflection.MethodInfo _fix50CreateRaidEventMethod;
        private static bool _fix50CreateRaidEventResolved;

        private bool TryCreateRealRaidEvent(MobileParty party, Settlement village)
        {
            if (party == null || party.Party == null || village == null || village.Party == null)
                return false;
            try
            {
                if (!_fix50CreateRaidEventResolved)
                {
                    _fix50CreateRaidEventResolved = true;
                    var componentType = System.Type.GetType(
                        "TaleWorlds.CampaignSystem.MapEvents.RaidEventComponent, TaleWorlds.CampaignSystem",
                        throwOnError: false);
                    if (componentType == null)
                    {
                        SafeLog("fix50: RaidEventComponent type not found — raid feature unavailable on this engine version.");
                        return false;
                    }
                    var partyBaseType = System.Type.GetType(
                        "TaleWorlds.CampaignSystem.Party.PartyBase, TaleWorlds.CampaignSystem",
                        throwOnError: false);
                    _fix50CreateRaidEventMethod = componentType.GetMethod(
                        "CreateRaidEvent",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                        binder: null,
                        types: new System.Type[] { partyBaseType, partyBaseType },
                        modifiers: null);
                    if (_fix50CreateRaidEventMethod == null)
                    {
                        SafeLog("fix50: CreateRaidEvent(PartyBase,PartyBase) overload not found — raid feature unavailable.");
                        return false;
                    }
                    SafeLog("fix50: RaidEventComponent.CreateRaidEvent(PartyBase,PartyBase) RESOLVED — using real vanilla raid pipeline.");
                }
                if (_fix50CreateRaidEventMethod == null) return false;

                // Invoke. Engine internally creates MapEvent, sets BeingRaided, starts tick.
                var result = _fix50CreateRaidEventMethod.Invoke(null, new object[] { party.Party, village.Party });
                if (result == null)
                {
                    SafeLog("fix50: CreateRaidEvent returned null for '" + party.StringId
                        + "' → '" + village.Name + "' (engine rejected). Will not retry this tick.");
                    return false;
                }
                SafeLog("fix50: REAL RAID STARTED — CreateRaidEvent succeeded: '"
                    + party.StringId + "' → '" + village.Name
                    + "'. Engine now owns lifecycle.");

                // Wave 4.16.2 (2026-06-11): declare war AT RAID START. The old fix36
                // declaration lived in ApplyRaidConsequences, which only ran on the
                // dead simulation-timer path — so native raids never declared war
                // (user-reported). Raiding a village IS an act of war.
                DeclareRaidWar(party, village);
                return true;
            }
            catch (System.Exception ex)
            {
                SafeLog("fix50 TryCreateRealRaidEvent threw "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // ───────────────────────── raid captain registry (fix44) ─────────────────────────

        // Wave 4.15.0-fix44 (2026-06-10): one dedicated raid-captain Hero per bandit
        // culture. Created lazily on first raid for that culture, cached for the
        // session. The captain's only purpose: be a Hero in the lead raid party's
        // MemberRoster index 0 so the engine's raid validator accepts the party as a
        // legitimate attacker (the fix43 trace proved this satisfies the validator).
        // The chief Hero is completely untouched — stays at hideout for dialog.
        private Dictionary<string, Hero> _raidCaptainsByCulture = new Dictionary<string, Hero>();

        private Hero EnsureRaidCaptain(CultureObject culture, Clan banditClan, Settlement anchor)
        {
            if (culture == null) return null;
            try
            {
                Hero existing;
                if (_raidCaptainsByCulture.TryGetValue(culture.StringId, out existing)
                    && existing != null && existing.IsAlive)
                {
                    return existing;
                }

                // Pick a Hero template that exists in the user's install. Same fallback
                // chain BanditChiefRegistry uses for chief Heroes.
                var template = TaleWorlds.ObjectSystem.MBObjectManager.Instance
                    .GetObject<CharacterObject>("gangleader_aserai")
                    ?? TaleWorlds.ObjectSystem.MBObjectManager.Instance
                        .GetObject<CharacterObject>("gangleader_empire")
                    ?? TaleWorlds.ObjectSystem.MBObjectManager.Instance
                        .GetObject<CharacterObject>("merchant_empire")
                    ?? TaleWorlds.ObjectSystem.MBObjectManager.Instance
                        .GetObject<CharacterObject>("bp_chief_hero_template");
                if (template == null)
                {
                    SafeLog("fix44 EnsureRaidCaptain: no Hero template found — captain creation aborted.");
                    return null;
                }

                // Owner clan = bandit clan (so captain shares faction/banner with the raid party).
                var ownerClan = banditClan ?? (anchor != null ? anchor.OwnerClan : null);
                if (ownerClan == null)
                {
                    SafeLog("fix44 EnsureRaidCaptain: no owner clan for " + culture.StringId);
                    return null;
                }

                Hero captain = HeroCreator.CreateSpecialHero(template, anchor, ownerClan, null,
                    MBRandom.RandomInt(28, 42));
                if (captain == null)
                {
                    SafeLog("fix44 EnsureRaidCaptain: HeroCreator returned null for " + culture.StringId);
                    return null;
                }
                // Cache + log.
                _raidCaptainsByCulture[culture.StringId] = captain;
                SafeLog("fix44: created raid captain '" + captain.Name
                    + "' for culture '" + culture.StringId
                    + "' (clan='" + ownerClan.Name + "')");
                return captain;
            }
            catch (Exception ex)
            {
                SafeLog("fix44 EnsureRaidCaptain threw "
                    + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        // ───────────────────────── raid trigger (fix28) ─────────────────────────

        // Wave 4.15.0-fix28 (2026-06-09): force the village into BeingRaided when our
        // raid party is at the doorstep. The vanilla engine refuses to auto-trigger
        // raids for mod-spawned bandit parties (confirmed across 27 fix iterations
        // and one prior research workflow). The fix is to invoke the ACTUAL vanilla
        // raid entry point ourselves.
        //
        // Strategy chain (resolved on first call, cached forever):
        //   0) RaidEventComponent.CreateRaidEvent(PartyBase, PartyBase) — full lifecycle
        //   1) ChangeVillageStateAction.ApplyBySettingToBeingRaided(Settlement, MobileParty) — state + events
        //   2) ChangeVillageStateAction.ApplyInternal — NonPublic fallback
        //   3) Village._villageState raw field write — last resort, no events
        //
        // Returns true iff post-call read confirms VillageState != Normal.
        private bool TryTriggerVillageRaid(MobileParty party, Settlement village)
        {
            if (party == null || village == null)
            {
                SafeLog("TryTriggerVillageRaid: null arg — aborting.");
                return false;
            }
            if (!village.IsVillage || village.Village == null)
            {
                SafeLog("TryTriggerVillageRaid: " + (village.StringId ?? "?") + " is not a Village — aborting.");
                return false;
            }
            if (village.Village.VillageState != Village.VillageStates.Normal)
            {
                SafeLog("TryTriggerVillageRaid: " + village.StringId + " already "
                    + village.Village.VillageState + " — no-op.");
                return true; // already triggered (by us earlier, or by vanilla, or by another mod)
            }

            // Resolve reflection handles once.
            if (!_raidTriggerResolved)
            {
                try
                {
                    var raidEventType = Type.GetType(
                        "TaleWorlds.CampaignSystem.RaidEventComponent, TaleWorlds.CampaignSystem",
                        throwOnError: false);
                    if (raidEventType != null)
                    {
                        _cachedCreateRaidEventMethod = raidEventType.GetMethod(
                            "CreateRaidEvent",
                            BindingFlags.Public | BindingFlags.Static,
                            null,
                            new Type[] { typeof(PartyBase), typeof(PartyBase) },
                            null);
                    }

                    var actionType = Type.GetType(
                        "TaleWorlds.CampaignSystem.Actions.ChangeVillageStateAction, TaleWorlds.CampaignSystem",
                        throwOnError: false);
                    if (actionType != null)
                    {
                        _cachedApplyBeingRaidedMethod = actionType.GetMethod(
                            "ApplyBySettingToBeingRaided",
                            BindingFlags.Public | BindingFlags.Static,
                            null,
                            new Type[] { typeof(Settlement), typeof(MobileParty) },
                            null);
                        _cachedApplyInternalMethod = actionType.GetMethod(
                            "ApplyInternal",
                            BindingFlags.NonPublic | BindingFlags.Static);
                    }

                    _cachedVillageStateField = typeof(Village).GetField(
                        "_villageState",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    _raidTriggerResolved = true;
                    SafeLog("TryTriggerVillageRaid resolved: "
                        + "CreateRaidEvent=" + (_cachedCreateRaidEventMethod != null)
                        + " ApplyBySettingToBeingRaided=" + (_cachedApplyBeingRaidedMethod != null)
                        + " ApplyInternal=" + (_cachedApplyInternalMethod != null)
                        + " _villageState=" + (_cachedVillageStateField != null));
                }
                catch (Exception resolveEx)
                {
                    SafeLog("TryTriggerVillageRaid resolve threw "
                        + resolveEx.GetType().Name + ": " + resolveEx.Message);
                    _raidTriggerResolved = true; // don't retry every hour
                }
            }

            // ── Strategy 0: RaidEventComponent.CreateRaidEvent (PREFERRED — full lifecycle)
            if (_cachedCreateRaidEventMethod != null)
            {
                try
                {
                    _cachedCreateRaidEventMethod.Invoke(null,
                        new object[] { party.Party, village.Village.Settlement.Party });
                    if (village.Village.VillageState != Village.VillageStates.Normal)
                    {
                        SafeLog("Raid TRIGGERED via RaidEventComponent.CreateRaidEvent: '"
                            + (party.StringId ?? "?") + "' → " + village.Name
                            + " (state=" + village.Village.VillageState + ")");
                        return true;
                    }
                }
                catch (Exception ex0)
                {
                    var inner = (ex0 as TargetInvocationException)?.InnerException ?? ex0;
                    SafeLog("CreateRaidEvent threw " + inner.GetType().Name + ": " + inner.Message
                        + " — trying next strategy.");
                }
            }

            // ── Strategy 1: ChangeVillageStateAction.ApplyBySettingToBeingRaided
            if (_cachedApplyBeingRaidedMethod != null)
            {
                try
                {
                    _cachedApplyBeingRaidedMethod.Invoke(null,
                        new object[] { village, party });
                    if (village.Village.VillageState != Village.VillageStates.Normal)
                    {
                        SafeLog("Raid TRIGGERED via ApplyBySettingToBeingRaided: '"
                            + (party.StringId ?? "?") + "' → " + village.Name
                            + " (state=" + village.Village.VillageState + ")");
                        return true;
                    }
                }
                catch (Exception ex1)
                {
                    var inner = (ex1 as TargetInvocationException)?.InnerException ?? ex1;
                    SafeLog("ApplyBySettingToBeingRaided threw " + inner.GetType().Name + ": "
                        + inner.Message + " — trying next strategy.");
                }
            }

            // ── Strategy 2: ApplyInternal (NonPublic reflection — older signature)
            if (_cachedApplyInternalMethod != null)
            {
                try
                {
                    var pars = _cachedApplyInternalMethod.GetParameters();
                    // Two known shapes: (Settlement, MobileParty) or (Village, MobileParty)
                    object[] args = pars.Length == 2 && pars[0].ParameterType == typeof(Village)
                        ? new object[] { village.Village, party }
                        : new object[] { village, party };
                    _cachedApplyInternalMethod.Invoke(null, args);
                    if (village.Village.VillageState != Village.VillageStates.Normal)
                    {
                        SafeLog("Raid TRIGGERED via ApplyInternal reflection: '"
                            + (party.StringId ?? "?") + "' → " + village.Name);
                        return true;
                    }
                }
                catch (Exception ex2)
                {
                    var inner = (ex2 as TargetInvocationException)?.InnerException ?? ex2;
                    SafeLog("ApplyInternal threw " + inner.GetType().Name + ": " + inner.Message
                        + " — trying next strategy.");
                }
            }

            // ── Strategy 3: Raw field write (no events fired — vanilla subscribers won't react)
            if (_cachedVillageStateField != null)
            {
                try
                {
                    _cachedVillageStateField.SetValue(village.Village, Village.VillageStates.BeingRaided);
                    if (village.Village.VillageState != Village.VillageStates.Normal)
                    {
                        SafeLog("Raid TRIGGERED via _villageState field write (NO EVENTS FIRED): '"
                            + (party.StringId ?? "?") + "' → " + village.Name
                            + " — vanilla subscribers will not react, raid may stall.");
                        return true;
                    }
                }
                catch (Exception ex3)
                {
                    SafeLog("_villageState field write threw " + ex3.GetType().Name + ": " + ex3.Message);
                }
            }

            SafeLog("TryTriggerVillageRaid: ALL STRATEGIES FAILED for '"
                + (party.StringId ?? "?") + "' → " + (village.Name?.ToString() ?? village.StringId));
            return false;
        }

        // ───────────────────────── raid completion (fix30) ─────────────────────────

        // Wave 4.15.0-fix30 (2026-06-09): drive BeingRaided → Looted transition
        // ourselves since vanilla's looting-timer MapEvent.Update is absent.
        // Tries a public/internal ChangeVillageStateAction method first, then falls
        // back to raw field write + manual event invocation.
        private static MethodInfo _cachedApplyEndingRaidMethod;
        private static MethodInfo _cachedDispatcherStateChangedMethod;
        private static bool _completeRaidResolved;

        private bool TryCompleteRaid(Village village, MobileParty raiderParty)
        {
            if (village == null) return false;

            if (!_completeRaidResolved)
            {
                try
                {
                    var actionType = Type.GetType(
                        "TaleWorlds.CampaignSystem.Actions.ChangeVillageStateAction, TaleWorlds.CampaignSystem",
                        throwOnError: false);
                    if (actionType != null)
                    {
                        // Try common method names for the Looted side. Vanilla has
                        // multiple ApplyBy* methods in this class.
                        string[] candidateNames = {
                            "ApplyByEndingRaidByPeace",
                            "ApplyByEndingRaid",
                            "ApplyByEndRaid",
                            "ApplyByVillageLooted",
                            "ApplyBySettingToLooted",
                            "ApplyByEndRaidByDestroyed"
                        };
                        foreach (var name in candidateNames)
                        {
                            var m = actionType.GetMethod(name,
                                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                            if (m != null)
                            {
                                _cachedApplyEndingRaidMethod = m;
                                SafeLog("TryCompleteRaid resolved: ChangeVillageStateAction." + name);
                                break;
                            }
                        }
                    }

                    // CampaignEventDispatcher to manually fire VillageStateChanged
                    var dispatcherType = Type.GetType(
                        "TaleWorlds.CampaignSystem.CampaignEventDispatcher, TaleWorlds.CampaignSystem",
                        throwOnError: false);
                    if (dispatcherType != null)
                    {
                        // Vanilla name varies: OnVillageStateChanged / VillageStateChanged
                        var candidates = new[] { "OnVillageStateChanged", "VillageStateChangedEvent", "OnVillageBeingRaided" };
                        foreach (var name in candidates)
                        {
                            var m = dispatcherType.GetMethod(name,
                                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            if (m != null)
                            {
                                _cachedDispatcherStateChangedMethod = m;
                                SafeLog("TryCompleteRaid resolved CampaignEventDispatcher." + name);
                                break;
                            }
                        }
                    }

                    _completeRaidResolved = true;
                }
                catch (Exception resolveEx)
                {
                    SafeLog("TryCompleteRaid resolve threw " + resolveEx.GetType().Name + ": " + resolveEx.Message);
                    _completeRaidResolved = true;
                }
            }

            // Strategy 1: public/internal ChangeVillageStateAction ApplyBy* method
            if (_cachedApplyEndingRaidMethod != null)
            {
                try
                {
                    var pars = _cachedApplyEndingRaidMethod.GetParameters();
                    object[] args;
                    // Common shapes: (Settlement), (Village), (Settlement, MobileParty), (Village, MobileParty)
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(Village))
                        args = new object[] { village };
                    else if (pars.Length == 1 && pars[0].ParameterType == typeof(Settlement))
                        args = new object[] { village.Settlement };
                    else if (pars.Length == 2 && pars[0].ParameterType == typeof(Village))
                        args = new object[] { village, raiderParty };
                    else if (pars.Length == 2 && pars[0].ParameterType == typeof(Settlement))
                        args = new object[] { village.Settlement, raiderParty };
                    else
                    {
                        SafeLog("TryCompleteRaid: ApplyBy* method has unexpected signature: " + pars.Length + " params — skipping strategy 1.");
                        args = null;
                    }
                    if (args != null)
                    {
                        _cachedApplyEndingRaidMethod.Invoke(null, args);
                        if (village.VillageState == Village.VillageStates.Looted)
                        {
                            SafeLog("Raid complete: " + (raiderParty?.StringId ?? "?")
                                + " looted " + village.Name + " (via ApplyBy* completion method)");
                            return true;
                        }
                    }
                }
                catch (Exception ex1)
                {
                    var inner = (ex1 as TargetInvocationException)?.InnerException ?? ex1;
                    SafeLog("TryCompleteRaid ApplyBy* threw " + inner.GetType().Name + ": " + inner.Message);
                }
            }

            // Strategy 2: raw field write to _villageState + manual event fire
            if (_cachedVillageStateField != null)
            {
                try
                {
                    var oldState = village.VillageState;
                    _cachedVillageStateField.SetValue(village, Village.VillageStates.Looted);
                    if (village.VillageState == Village.VillageStates.Looted)
                    {
                        SafeLog("Raid complete: " + (raiderParty?.StringId ?? "?")
                            + " looted " + village.Name + " (via _villageState field write)");
                        // Best-effort: hearth damage so the looting feels real
                        try
                        {
                            village.Hearth = (float)Math.Max(20.0, village.Hearth * 0.6);
                        }
                        catch { /* ignore */ }
                        return true;
                    }
                }
                catch (Exception ex2)
                {
                    SafeLog("TryCompleteRaid field write threw " + ex2.GetType().Name + ": " + ex2.Message);
                }
            }

            SafeLog("TryCompleteRaid: ALL STRATEGIES FAILED for '" + village.Name + "'");
            return false;
        }

        // ───────────────────────── raid consequences (fix35) ─────────────────────────

        // Wave 4.15.0-fix35 (2026-06-10): apply REAL post-raid consequences using
        // ONLY APIs the probe verified exist as PUBLIC in user's 1.4.5 install:
        //   - Village.Hearth (Single, canRead+canWrite) — direct hearth damage
        //   - ChangeRelationAction.ApplyRelationChangeBetweenHeroes(Hero,Hero,Int32,Boolean)
        //   - ChangeCrimeRatingAction.Apply(IFaction, Single, Boolean)
        // Skipped APIs (NOT FOUND in 1.4.5):
        //   - ChangeHearthLevelAction, ChangeVillageHearthAction, etc.
        //   - MakeKingdomsEnemyAction
        // Skipped intentionally (too aggressive for every raid):
        //   - DeclareWarAction.ApplyByKingdomDecision — would cause war on every raid
        private void ApplyRaidConsequences(Village village, MobileParty raiderParty)
        {
            if (village == null) return;

            // 1. Hearth damage (real economic consequence — reduces village production)
            try
            {
                float hearthBefore = village.Hearth;
                float damage = Math.Max(20f, hearthBefore * 0.25f); // 25% of current, min 20
                village.Hearth = Math.Max(20f, hearthBefore - damage);
                SafeLog("fix35: hearth damage applied to '" + village.Name
                    + "': " + hearthBefore.ToString("F0") + " → " + village.Hearth.ToString("F0")
                    + " (-" + damage.ToString("F0") + ")");
            }
            catch (Exception hex)
            {
                SafeLog("fix35 hearth damage threw " + hex.GetType().Name + ": " + hex.Message);
            }

            // 2. Relation drop between the bandit chief and the village's notables
            //    (so the village remembers who raided them — and the owner clan if any)
            try
            {
                Hero raiderChief = raiderParty?.LeaderHero;
                if (raiderChief != null)
                {
                    // Drop relation with all notables in this village
                    if (village.Settlement != null && village.Settlement.Notables != null)
                    {
                        foreach (var notable in village.Settlement.Notables)
                        {
                            if (notable == null || !notable.IsAlive) continue;
                            try
                            {
                                TaleWorlds.CampaignSystem.Actions.ChangeRelationAction
                                    .ApplyRelationChangeBetweenHeroes(
                                        raiderChief, notable, -10, /* showQuickNotification: */ false);
                            }
                            catch { /* defensive — per-notable failure shouldn't kill the loop */ }
                        }
                        SafeLog("fix35: relation -10 applied between '" + raiderChief.Name
                            + "' and notables of '" + village.Name + "'");
                    }
                    // Drop relation with the owner clan's leader (the lord who owns the village)
                    var ownerClan = village.Settlement?.OwnerClan;
                    if (ownerClan != null && ownerClan.Leader != null && ownerClan.Leader.IsAlive)
                    {
                        try
                        {
                            TaleWorlds.CampaignSystem.Actions.ChangeRelationAction
                                .ApplyRelationChangeBetweenHeroes(
                                    raiderChief, ownerClan.Leader, -15, false);
                            SafeLog("fix35: relation -15 applied between '" + raiderChief.Name
                                + "' and owner '" + ownerClan.Leader.Name + "' of clan '"
                                + ownerClan.Name + "'");
                        }
                        catch (Exception relEx)
                        {
                            SafeLog("fix35 owner relation threw " + relEx.GetType().Name + ": " + relEx.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SafeLog("fix35 relation block threw " + ex.GetType().Name + ": " + ex.Message);
            }

            // 3. Crime rating: the bandit clan becomes more "wanted" by the village's kingdom
            try
            {
                var ownerKingdom = village.Settlement?.OwnerClan?.Kingdom;
                var banditFaction = raiderParty?.MapFaction;
                if (ownerKingdom != null && banditFaction != null && banditFaction != ownerKingdom)
                {
                    TaleWorlds.CampaignSystem.Actions.ChangeCrimeRatingAction
                        .Apply(ownerKingdom, /* crimeRatingChange: */ 5f, /* showNotification: */ false);
                    SafeLog("fix35: crime rating +5 against '" + ownerKingdom.Name
                        + "' (bandits raided their village '" + village.Name + "')");
                }
            }
            catch (Exception ex)
            {
                SafeLog("fix35 crime rating threw " + ex.GetType().Name + ": " + ex.Message);
            }

            // 4. Wave 4.15.0-fix36 (2026-06-10): Declare war between the bandit faction
            // and the village's owner faction. User explicitly asked for this:
            // "if the looters or bandits raid the village that means they should be at
            // war with that CLan". Probe verified DeclareWarAction.ApplyByKingdomDecision
            // (IFaction, IFaction) is PUBLIC in 1.4.5. Guard: skip if already at war,
            // skip if same faction (defensive), skip if either side is null.
            try
            {
                var banditFaction = raiderParty?.MapFaction;
                // Prefer the village's clan-level faction (so raid creates clan-level
                // hostility), falling back to settlement faction if owner clan is null.
                var ownerFaction = (IFaction)village.Settlement?.OwnerClan
                                   ?? (IFaction)village.Settlement?.MapFaction;
                if (banditFaction != null
                    && ownerFaction != null
                    && banditFaction != ownerFaction
                    && !banditFaction.IsAtWarWith(ownerFaction))
                {
                    TaleWorlds.CampaignSystem.Actions.DeclareWarAction
                        .ApplyByKingdomDecision(banditFaction, ownerFaction);
                    SafeLog("fix36: DeclareWarAction.ApplyByKingdomDecision — '"
                        + banditFaction.Name + "' now at war with '"
                        + ownerFaction.Name + "' (raided village '" + village.Name + "')");
                }
                else if (banditFaction != null && ownerFaction != null
                         && banditFaction.IsAtWarWith(ownerFaction))
                {
                    SafeLog("fix36: '" + banditFaction.Name + "' already at war with '"
                        + ownerFaction.Name + "' — skip declaration.");
                }
            }
            catch (Exception ex)
            {
                SafeLog("fix36 DeclareWarAction threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ───────────────────────── positional move (SetMoveGoToPoint) ─────────────────────────

        // Wave 4.15.0-fix19 (2026-06-08): SetMoveGoToPoint bypasses settlement-AI
        // entirely. The bandit AI override that froze our parties in micro-oscillation
        // only acts on settlement-targeted moves. Positional moves are a pure pathfind
        // to a Vec2 and the AI doesn't second-guess them. Method-lookup-by-reflection
        // since the signature varies across versions (Vec2 vs Vec3).
        private static System.Reflection.MethodInfo _cachedGoToPointMethod;
        private static bool _goToPointMethodChecked;

        private bool TryMoveGoToPoint(MobileParty party, Vec2 point)
        {
            if (party == null) return false;
            try
            {
                if (!_goToPointMethodChecked)
                {
                    _cachedGoToPointMethod = typeof(MobileParty).GetMethod(
                        "SetMoveGoToPoint",
                        BindingFlags.Instance | BindingFlags.Public,
                        null,
                        new[] { typeof(Vec2) },
                        null);
                    _goToPointMethodChecked = true;
                    if (_cachedGoToPointMethod != null)
                    {
                        SafeLog("Resolved SetMoveGoToPoint(Vec2) via reflection — bypassing settlement-AI for raid targeting");
                    }
                    else
                    {
                        SafeLog("SetMoveGoToPoint(Vec2) not found in this Bannerlord version — falling back to SetMoveGoToSettlement");
                    }
                }
                if (_cachedGoToPointMethod == null) return false;
                _cachedGoToPointMethod.Invoke(party, new object[] { point });
                return true;
            }
            catch (Exception ex)
            {
                SafeLog("TryMoveGoToPoint threw " + ex.GetType().Name + ": " + ex.Message + " — falling back");
                return false;
            }
        }

        // ───────────────────────── raid intent (SetMoveRaidSettlement) ─────────────────────────

        // Wave 4.15.0-fix17 (2026-06-08): try SetMoveRaidSettlement before falling
        // back to SetMoveGoToSettlement. The deep-research workflow found that
        // MobileParty.SetMoveRaidSettlement(Settlement) exists in 1.4.x and signals
        // explicit raid intent — the engine treats this with higher priority than
        // generic GoTo for bandit parties, overriding the "rest/regroup" state that
        // froze our raid parties even with locked AI (DIAG showed pos=stable, target
        // correctly set, but party not moving).
        //
        // Method-lookup-by-reflection so absent versions fall through cleanly. Caches
        // the method handle after first success.
        // Wave 4.15.0-fix29 (2026-06-09): web-research workflow confirmed
        // SetMoveRaidSettlement lives on MobilePartyAi, NOT on MobileParty. My fix17
        // probed the wrong type. BanditMilitias mod (JungleDruid/BanditMilitias,
        // MilitiaBehavior.cs lines 352, 406-407) uses the canonical pattern:
        //   party.Ai.SetMoveRaidSettlement(settlement);
        //   party.Ai.SetDoNotMakeNewDecisions(true);
        // The second call is REQUIRED — without it vanilla bandit AI replans away
        // from the raid order. With it, raid intent persists until VillageState
        // transitions BeingRaided → Looted and engine releases the lock.
        private static System.Reflection.MethodInfo _cachedRaidSettlementMethodOnAi;
        private static bool _raidSettlementMethodChecked;

        private bool TryRaidSettlement(MobileParty party, Settlement settlement)
        {
            if (party == null || settlement == null || party.Ai == null) return false;

            // 2026-07-26: routed through PartyOrderCompat. The old code called
            // party.SetMoveRaidSettlement(Settlement, NavigationType, bool) directly
            // inside a try/catch — which does NOT protect against the overload being
            // absent on 1.3.15, because the CLR raises MissingMethodException while
            // JIT-compiling THIS method, before the try is ever entered. See the
            // header of Source/Compat/PartyOrderCompat.cs.
            if (!BandItPlus.Compat.PartyOrderCompat.RaidSettlement(party, settlement))
                return false;

            try { party.Ai.SetDoNotMakeNewDecisions(true); } catch { }

            if (!_raidSettlementMethodChecked)
            {
                _raidSettlementMethodChecked = true;
                SafeLog("raid intent issued via PartyOrderCompat — engine creates real MapEvent.");
            }
            return true;
        }

        private bool TryRaidSettlement_OLD(MobileParty party, Settlement settlement)
        {
            if (party == null || settlement == null || party.Ai == null) return false;

            // Wave 4.15.0-fix32 (DEPRECATED by fix40): reflection probe approach.
            try
            {
                if (!_raidSettlementMethodChecked)
                {
                    _raidSettlementMethodChecked = true;
                    string[] candidateNames = {
                        "SetMoveRaidSettlement",
                        "MoveRaidSettlement",
                        "SetRaidSettlement",
                        "SetAiBehavior",
                        "SetDecisionToRaid",
                        "AiDecideOnRaid"
                    };
                    var allFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    foreach (var name in candidateNames)
                    {
                        var m = typeof(MobilePartyAi).GetMethod(name, allFlags);
                        if (m != null)
                        {
                            _cachedRaidSettlementMethodOnAi = m;
                            SafeLog("fix32: resolved MobilePartyAi." + name
                                + " (" + m.GetParameters().Length + " params)");
                            break;
                        }
                    }
                    if (_cachedRaidSettlementMethodOnAi == null)
                    {
                        SafeLog("fix32: NO raid-intent method found on MobilePartyAi in 1.4.5 — engine refactored this API. Will rely on TryTriggerVillageRaid + manual completion (fix28/fix30 chain).");
                    }
                }

                if (_cachedRaidSettlementMethodOnAi != null && party.Ai != null)
                {
                    var pars = _cachedRaidSettlementMethodOnAi.GetParameters();
                    object[] args = pars.Length == 1
                        ? new object[] { settlement }
                        : (pars.Length == 2 ? new object[] { settlement, party } : null);
                    if (args == null) return false;
                    _cachedRaidSettlementMethodOnAi.Invoke(party.Ai, args);
                    party.Ai.SetDoNotMakeNewDecisions(true);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                SafeLog("fix32 TryRaidSettlement threw "
                    + ex.GetType().Name + ": " + ex.Message + " — falling back");
                return false;
            }
        }

        // ───────────────────────── reinforcement parties ─────────────────────────

        // Wave 4.15.0-fix14 (2026-06-08): spawn 1-2 escort/reinforcement bandit
        // parties from the same hideout, also marching to the same target village.
        // They travel together with the lead raider and the engine's clustering AI
        // treats them as combined force when evaluating attack viability — which
        // means the lead raider will commit to attacking instead of circling.
        // Untracked (not in _activeRaiders) so they don't block future spawns;
        // engine handles their lifecycle independently.
        private void SpawnReinforcementParties(
            CultureObject culture,
            Settlement hideoutSettlement,
            Settlement targetVillage,
            Hero leaderHero,
            Clan banditClan,
            PartyTemplateObject template,
            int count,
            float power)
        {
            for (int i = 0; i < count; i++)
            {
                try
                {
                    // Validate spawn position again — each reinforcement gets its own
                    // terrain-validated CampaignVec2 with slight offset so they don't
                    // overlap on the world map.
                    Vec2 baseAnchor = hideoutSettlement.GetPosition2D;
                    Vec2 jitter = new Vec2(
                        (i + 1) * 0.3f * (i % 2 == 0 ? 1f : -1f),
                        (i + 1) * 0.3f * (i % 2 == 1 ? 1f : -1f));
                    CampaignVec2 spawnPos;
                    if (!TryFindValidSpawnPos(baseAnchor + jitter, out spawnPos)) continue;

                    string partyId = "bp_raider_reinf" + (i + 1) + "_" + culture.StringId
                        + "_" + ((int)CampaignTime.Now.ToHours).ToString();

                    MobileParty reinf = BanditPartyComponent.CreateBanditParty(
                        partyId,
                        banditClan,
                        hideoutSettlement.Hideout,
                        /* isBossParty: */ true,
                        template,
                        spawnPos);
                    if (reinf == null) continue;

                    // Wave 4.15.0-fix44 (2026-06-10): NO captain injection on reinforcements
                    // (the chief is no longer used as a leader; the captain stays only on
                    // the lead party to avoid Hero ping-pong between roster slots). Engine
                    // treats reinforcements as escorts of the lead party.
                    // Reinforcements share the same leader (via ChangePartyLeader) for
                    // crash-prevention on death.
                    TrySetLeaderHero(reinf, leaderHero);

                    // Beef up the reinforcement too (smaller multiplier — they're escorts).
                    BeefUpRaidParty(reinf, template, MCMSettings.Instance.RaidPartySizeMultiplier * 0.7f);

                    // Wave 4.16.1: chief clan banner immediately (not vanilla).
                    try { BandItPlus.BanditPower.BanditPartyBannerBehavior.TryApply(reinf); }
                    catch { }

                    // Send to same village — they cluster naturally en route.
                    // fix15: lock down bandit AI before move so vanilla bandit-AI
                    // can't override the target with random wandering/looting.
                    // fix19: SetMoveGoToPoint primary, raid intent + GoToSettlement fallback.
                    if (reinf.Ai != null) reinf.Ai.SetDoNotMakeNewDecisions(false);
                    Vec2 reinfPoint = new Vec2(targetVillage.Position.X, targetVillage.Position.Y);
                    if (!TryMoveGoToPoint(reinf, reinfPoint))
                    {
                        if (!TryRaidSettlement(reinf, targetVillage))
                        {
                            BandItPlus.Compat.PartyOrderCompat.GoToSettlement(reinf, targetVillage);
                        }
                    }
                    // fix16: track reinforcement so OnHourlyTick re-issues the move
                    // command if bandit AI clears the target.
                    _raidTargets[reinf] = targetVillage;

                    SafeLog("Spawned reinforcement #" + (i + 1) + " '" + partyId
                        + "' (culture=" + culture.StringId
                        + ") → joining raid on " + targetVillage.Name);
                }
                catch (Exception rEx)
                {
                    SafeLog("SpawnReinforcementParties #" + (i + 1)
                        + " threw " + rEx.GetType().Name + ": " + rEx.Message);
                }
            }
        }

        // ───────────────────────── party beef-up ─────────────────────────

        // Wave 4.15.0-fix13 (2026-06-08): add extra bandit troops to a freshly
        // spawned raid party so it's strong enough to commit to an attack rather
        // than circling the village waiting for reinforcements. Iterates the
        // template's stacks and adds `multiplier` extra of each stack's character.
        private void BeefUpRaidParty(MobileParty party, PartyTemplateObject template, float multiplier)
        {
            if (party?.MemberRoster == null || template?.Stacks == null) return;
            if (multiplier <= 0f) return;

            int totalAdded = 0;
            // PartyTemplateStack is a struct in 1.4.x — cannot use ?. on it. Iterate
            // by index and null-check the Character reference directly.
            for (int i = 0; i < template.Stacks.Count; i++)
            {
                var stack = template.Stacks[i];
                if (stack.Character == null) continue;
                // Use the stack's nominal value as the base — multiplier of N adds N*nominal extras.
                int nominal = Math.Max(1, stack.MaxValue);
                int extra = (int)(nominal * multiplier);
                if (extra < 1) continue;
                try
                {
                    party.MemberRoster.AddToCounts(stack.Character, extra);
                    totalAdded += extra;
                }
                catch (Exception addEx)
                {
                    SafeLog("BeefUpRaidParty AddToCounts threw for "
                        + (stack.Character.StringId ?? "?")
                        + ": " + addEx.GetType().Name + ": " + addEx.Message);
                }
            }
            if (totalAdded > 0)
            {
                SafeLog("Beefed raid party '" + party.StringId + "' with +"
                    + totalAdded + " troops (multiplier=" + multiplier.ToString("0.00") + ")");
            }
        }

        // ───────────────────────── log helper ─────────────────────────

        // ───────────────── 2026-07-26: raid re-trigger / banner throttling ─────────────────

        // One raid banner per village per kRaidBannerCooldownHours. The alert fires on
        // the Normal → BeingRaided transition, and every fix46 re-trigger re-enters that
        // state — so without this the player got a fresh prompt on each engine revert.
        private bool ShouldAnnounceRaid(Settlement settlement)
        {
            if (settlement == null) return false;
            try
            {
                double now = CampaignTime.Now.ToHours;
                double last;
                if (_raidBannerLastHour.TryGetValue(settlement, out last)
                    && now - last < kRaidBannerCooldownHours)
                {
                    return false;
                }
                _raidBannerLastHour[settlement] = now;
                return true;
            }
            catch { return true; }
        }

        // Gate the fix46 re-trigger on BOTH an attempt cap and an in-game cooldown.
        // Returns false once this village's budget is spent; the caller then releases
        // the party instead of re-arming the raid forever.
        private bool MayRetriggerRaid(Settlement settlement, MobileParty party)
        {
            if (settlement == null || party == null) return false;
            try
            {
                double now = CampaignTime.Now.ToHours;

                double last;
                if (_retriggerLastHour.TryGetValue(settlement, out last)
                    && now - last < kRetriggerCooldownHours)
                {
                    // Engine is reverting in a tight loop — this is the runaway case.
                    // Skip silently rather than re-arming on the same game hour.
                    return false;
                }

                int count;
                _retriggerCount.TryGetValue(settlement, out count);
                if (count >= kMaxRaidRetriggers)
                {
                    SafeLog("fix46: giving up on '" + settlement.Name + "' after "
                        + count + " re-triggers — engine keeps reverting the raid.");
                    return false;
                }

                _retriggerCount[settlement] = count + 1;
                _retriggerLastHour[settlement] = now;
                return true;
            }
            catch { return false; }
        }

        // Unlock and send home a raider whose village gave up on it. Leaving the AI
        // locked with SetDoNotMakeNewDecisions(true) is what previously stranded
        // raiders standing outside a village indefinitely.
        private void ReleaseRaiderAfterFailedRetriggers(MobileParty party, Settlement settlement)
        {
            try
            {
                _raidTargets.Remove(party);
                if (party.Ai != null) party.Ai.SetDoNotMakeNewDecisions(false);
                if (party.HomeSettlement != null)
                    BandItPlus.Compat.PartyOrderCompat.GoToSettlement(party, party.HomeSettlement);
                else
                    party.SetMoveModeHold();
                SafeLog("fix46: released '" + party.StringId + "' from '"
                    + (settlement != null ? settlement.Name.ToString() : "?") + "' — heading home.");
            }
            catch (Exception ex)
            {
                SafeLog("fix46 release threw " + ex.GetType().Name + ": " + ex.Message);
            }
            ClearRaidThrottleState(settlement);
        }

        private void ClearRaidThrottleState(Settlement settlement)
        {
            if (settlement == null) return;
            try
            {
                _retriggerCount.Remove(settlement);
                _retriggerLastHour.Remove(settlement);
                _raidBannerLastHour.Remove(settlement);
            }
            catch { }
        }

        private static void SafeLog(string message)
        {
            try
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Raid] " + message);
            }
            catch { /* never let logging itself crash a behavior tick */ }
        }
    }
}
