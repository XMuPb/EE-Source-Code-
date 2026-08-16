using HarmonyLib;
using Bannerlord.UIExtenderEx;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BandItPlus
{
    public class SubModuleClassEntry : MBSubModuleBase
    {
        private Harmony _harmony;
        private UIExtender _uiExtender;

        // T37 — main-thread deferral queue. Background-thread callers (e.g.
        // CampaignEvents.MapEventEnded fires on a worker per CLAUDE.md threading
        // notes) enqueue actions here; we drain in OnApplicationTick which is
        // guaranteed-main-thread. Lock around all access — TaleWorlds engine
        // ticks on the main thread only, but enqueues can come from arbitrary
        // threads.
        private static readonly System.Collections.Generic.Queue<System.Action> _mainThreadQueue
            = new System.Collections.Generic.Queue<System.Action>();
        private static readonly object _mainThreadQueueLock = new object();

        public static void EnqueueMainThread(System.Action action)
        {
            if (action == null) return;
            lock (_mainThreadQueueLock)
            {
                _mainThreadQueue.Enqueue(action);
            }
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            _harmony = new Harmony("xmupb.banditplus");
            // Store globally so HideoutVisitNpcSpawnBehavior can dynamically Patch/Unpatch
            // mission-scoped patches (e.g. AnimationPoint render-pipeline protection).
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.HarmonyInstance = _harmony;
            // 2026-07-26 — resolve the version-specific MobileParty.SetMove* overloads
            // BEFORE PatchAll(), so (a) the raid tracer's Prepare() has a cached answer
            // and (b) a user's log states plainly which engine APIs exist on their build.
            // A 1.3.15 user's MissingMethodException crash becomes one log line here.
            try { BandItPlus.Compat.PartyOrderCompat.LogResolvedApis(); } catch { }
            _harmony.PatchAll();
            // v215 (2026-05-31) — install zero-Heisenberg first-chance exception trap
            // BEFORE any save-related code can run. Captures the actual exception the engine
            // swallows during SaveResultType.GeneralFailure. See SaveExceptionTrap.cs header
            // for why this is safe where v206-v210's SaveDiagnosticPatch was not.
            BandItPlus.Diagnostics.SaveExceptionTrap.Initialize();
            // v108: UIExtenderEx — registers our [ViewModelMixin] classes (BandItPlusNameplateMixin)
            try
            {
                _uiExtender = UIExtender.Create("BandItPlus");
                _uiExtender.Register(typeof(SubModuleClassEntry).Assembly);
                _uiExtender.Enable();
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("v108 UIExtender registered");
            }
            catch (System.Exception uiEx)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("v108 UIExtender init fail: "
                    + uiEx.GetType().Name + ": " + uiEx.Message);
            }
            // Wave 4.8c: runtime-resolve EncyclopediaListVM.Refresh (its namespace isn't
            // statically referenceable cleanly) and append our chief Heroes to the list.
            BandItPlus.Patches.EncyclopediaListPatcher.TryPatch(_harmony);
            // v157 (2026-05-16): fill the "Owner" row of a hideout's campaign-map
            // hover tooltip with the culture chief's name (display-only). Reflection-
            // resolved target (PropertyBasedTooltipVM), so registered manually here.
            BandItPlus.Patches.HideoutTooltipOwnerPatch.TryPatch(_harmony);
            // Wave 4.11-fix (2026-05-06): ChiefHeroTypePatch.TryPatch is DEFERRED to
            // OnSessionLaunched in BanditChiefRegistry. Patching EncyclopediaHeroPageVM
            // at OnSubModuleLoad caused CampaignUIHelper's static cctor to fire before
            // Campaign.Current was set up, throwing TypeInitializationException during
            // character creation (the failure was cached and surfaced when
            // CharacterCreationGainedPropertiesVM was constructed).
            string loadedLine = Localization.Get("bp_mod_loaded",
                "BandIt Plus loaded — 8 new bandit clans active.");
            // CS0162 is expected: BpBuild.IsDev is a compile-time const (false in
            // Release), so this append is unreachable in a Release build. That is the
            // point of the flag — suppressed rather than removed so the dev-build
            // marker still works when IsDev is true.
#pragma warning disable CS0162
            if (BandItPlus.Diagnostics.BpBuild.IsDev) loadedLine += " [DEV BUILD]";
#pragma warning restore CS0162
            InformationManager.DisplayMessage(new InformationMessage(loadedLine, Colors.Yellow));
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            // Touch the singleton to force MCM registration.
            var _ = MCMSettings.Instance;
            // 07-13 Restore console-only settings (clan toggles, spawn/loot/raid tuning)
            // from BP's companion JSON. MCM only persists its own 5 attributed props, so
            // without this the console settings reverted to C# defaults every launch.
            ConsoleSettingsStore.Load();
        }

        // v119 (2026-05-15): drives the hideout chief-banner override. Walks the
        // settlement nameplate widget tree on a ~1.5s throttle and sets each
        // hideout's SettlementBannerWidget.ImageId to the chief's clan banner code.
        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            // Wave 4.28.0: persistent campaign-map HUD (mounts after the origin is chosen).
            try { BandItPlus.HUD.BanditHudService.Tick(dt); } catch { }
            try { BandItPlus.UI.BandItPlusNameplateMixin.TickHideoutBanners(); }
            catch (System.Exception ex) {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("TickHideoutBanners fail: " + ex.GetType().Name + ": " + ex.Message);
            }
            // Wave 4.28.x: re-assert at-peace hideout nameplate neutrality (recolour from red).
            try { BandItPlus.UI.BandItPlusNameplateMixin.TickPeaceRelations(); } catch { }

            try { if (BandItPlus.UI.BanditToastScreen.IsOpen) BandItPlus.UI.BanditToastScreen.Tick(dt); }
            catch (System.Exception) { }
            try { if (BandItPlus.UI.BanditContactScreen.IsOpen) BandItPlus.UI.BanditContactScreen.Tick(dt); }
            catch (System.Exception) { }
            try { if (BandItPlus.UI.BanditLetterScreen.IsOpen) BandItPlus.UI.BanditLetterScreen.Tick(dt); }
            catch (System.Exception) { }
            try { if (BandItPlus.UI.BanditParleyScreen.IsOpen) BandItPlus.UI.BanditParleyScreen.Tick(dt); }
            catch (System.Exception) { }
            try { if (BandItPlus.UI.BpJailbreakBriefScreen.IsOpen) BandItPlus.UI.BpJailbreakBriefScreen.Tick(dt); }
            catch (System.Exception) { }
            try { if (BandItPlus.UI.BanditKingBriefScreen.IsOpen) BandItPlus.UI.BanditKingBriefScreen.Tick(dt); }
            catch (System.Exception) { }
            // v183 Phase 1 (2026-05-20): BandIt Plus Console per-frame tick.
            // 2026-05-22: open-hotkey removed; map-bar button is the only
            // open path. Tick still drives animations + Escape-to-close.
            // Per project memory: System.Exception must be qualified
            // (file lacks 'using System;').
            try { BandItPlus.UI.Console.BanditPlusConsoleService.Tick(dt); }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("v183 Console service tick fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            // v183 Phase 3 (2026-05-20): map-bar button injector — walks the
            // campaign-map widget tree, injects our ⚔ button alongside EE's
            // Chronicle button (or into a MapBar-Id container). Self-limiting:
            // gives up after ~10s of attempts if no target found.
            try { BandItPlus.UI.Console.BanditPlusMapBarInjector.Tick(dt); }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("v183 MapBar injector tick fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            // T37 — drain the main-thread deferral queue. Background-thread events
            // (MapEventEnded etc.) enqueue mutations here; we execute them on the
            // guaranteed-main-thread engine tick. Snapshot the queue under lock,
            // then execute outside the lock so an action's exception (or its own
            // re-enqueue) can't deadlock or block other producers.
            System.Action[] toRun = null;
            lock (_mainThreadQueueLock)
            {
                if (_mainThreadQueue.Count > 0)
                {
                    toRun = _mainThreadQueue.ToArray();
                    _mainThreadQueue.Clear();
                }
            }
            if (toRun != null)
            {
                for (int i = 0; i < toRun.Length; i++)
                {
                    try { toRun[i](); }
                    catch (System.Exception ex)
                    {
                        BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                            "[BP] main-thread deferred action threw "
                            + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
        }

        // v160 (2026-05-16): inject the bandit battle-tactics behavior into every
        // mission. The behavior no-ops unless it is a field battle with a bandit
        // team — see BanditBattleTacticsBehavior.
        private static bool _v160InjectLogged;

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);
            try
            {
                // 2026-07-28: battle tactics and the AI status panel were removed from
                // this mod. All combat-AI work — target selection, formation stance,
                // shape, RBM arbitration — now lives in the separate BrainPlus module,
                // which hooks itself into missions via its own BrainPlusMissionBehavior
                // rather than being registered from here. BandItPlus deliberately keeps
                // no tactical or brain code.
                mission.AddMissionBehavior(new BandItPlus.UI.BanditReinforcementMissionView());
                // v175: crimson Hideout Assault panel (~15s, hideout assaults).
                mission.AddMissionBehavior(new BandItPlus.UI.HideoutAssaultPanelMissionView());
                // the gold BANDIT AI panel is retired from missions — its content now
                // lives as one line on the chief-reveal card (HideoutBrief).
                // hideout raid stealth/alarm (self-gates on sneak-in hideout scenes).
                mission.AddMissionBehavior(new BandItPlus.UI.BpHideoutAlarmController());
                // sneak-in Shadow Bar (self-gates: arms ONLY when the native
                // stealth logic is present — inverse of the alarm gate).
                mission.AddMissionBehavior(new BandItPlus.UI.BpSneakShadowController());
                if (!_v160InjectLogged)
                {
                    _v160InjectLogged = true;
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "v160 OnMissionBehaviorInitialize fired — behavior added (isFieldBattle="
                        + mission.IsFieldBattle + ")");
                }
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v160 OnMissionBehaviorInitialize fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            if (game.GameType is Campaign && gameStarterObject is CampaignGameStarter starter)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "SubModule.OnGameStart: registering campaign behaviors (Wave 4.8a build)");
                starter.AddBehavior(new BandItPlusCampaignBehavior());
                // Wave 4.8a: per-culture named-chief registry. Promotes one bandit hero
                // per culture to the CultureProfile.ChiefName so QuestGiver portraits in
                // the journal show a stable, named chief instead of a random bandit.
                try
                {
                    starter.AddBehavior(new BandItPlus.Heroes.BanditChiefRegistry());
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "SubModule: BanditChiefRegistry registered OK");
                }
                catch (System.Exception cre)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "SubModule: BanditChiefRegistry registration FAILED: " + cre.GetType().Name + ": " + cre.Message);
                }
                starter.AddBehavior(new PlayerBanditNeutrality());
                starter.AddBehavior(new BanditDialogManager());
                // Wave 4.28.0: Infamy / Bandit Rank backend (drives the campaign-map HUD ribbon).
                starter.AddBehavior(new BandItPlus.HUD.BanditRankBehavior());
                // Wave 5 (Chief Recruitment): persistent alliance state behavior.
                // Must register AFTER BanditDialogManager because Phase 2's
                // IsEligibleForOffer predicate reads BanditDialogManager._cultureTrust.
                // Logs RegisterEvents attach success/failure per memory
                // bannerlord-harmony-attach-log — verify in bp_save_trap.log on first load.
                try
                {
                    starter.AddBehavior(new BandItPlus.Behaviors.BanditAllianceBehavior());
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "SubModule: BanditAllianceBehavior registered OK");
                }
                catch (System.Exception aex)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "SubModule: BanditAllianceBehavior registration FAILED: "
                        + aex.GetType().Name + ": " + aex.Message);
                }
                starter.AddBehavior(new BandItPlus.HideoutVisit.HideoutVisitNpcSpawnBehavior());
                // v105: rename world-map hideouts to chief-themed names
                starter.AddBehavior(new BandItPlus.HideoutVisit.HideoutRenameBehavior());
                starter.AddBehavior(new BandItPlus.HideoutVisit.HideoutGreeterDialog());
                starter.AddBehavior(new BandItPlus.HideoutVisit.HideoutVendorDialog());
                // Wave 4.12 (2026-05-10): slaver vendor dialog, gated by chief Tier-3.
                starter.AddBehavior(new BandItPlus.HideoutVisit.HideoutSlaverDialog());
                starter.AddBehavior(new BandItPlus.Heroes.SlaverHeroRegistry());
                // Wave 4.12-fix v70 (2026-05-12): recurring caravan supply from
                // intro'd contacts (Tier 4 of Contacts roadmap).
                starter.AddBehavior(new BandItPlus.HideoutVisit.ContactCaravanBehavior());
                // Wave 4.12-fix v72 (2026-05-12): premium-popup contact arrival.
                starter.AddBehavior(new BandItPlus.HideoutVisit.ContactArrivalBehavior());
                // v158 (2026-05-16): Bandit Power — bandit/looter parties grow
                // stronger over the campaign (recruit, enlarge, upgrade tiers).
                // The behavior grows parties; the model lifts the size limit so
                // the extra troops do not desert.
                starter.AddBehavior(new BandItPlus.BanditPower.BanditPowerBehavior());
                starter.AddModel(new BandItPlus.BanditPower.BanditPartySizeModel());
                // v162 (2026-05-17): bandit parties fly the chief clan banner on
                // every UI surface via PartyBase.CustomBanner (single source).
                starter.AddBehavior(new BandItPlus.BanditPower.BanditPartyBannerBehavior());
                // v168 (2026-05-17): Bandit Battle Spoils — awards scaling
                // coin/keepsake/gear + a "Spoils of Victory" popup when the
                // player wins a battle against bandits. Listens to
                // CampaignEvents.MapEventEnded (the supported hook).
                starter.AddBehavior(new BandItPlus.BanditPower.BanditSpoilsBehavior());
                starter.AddBehavior(new BandItPlus.BanditPower.BanditBackupBehavior());
                starter.AddBehavior(new BandItPlus.Captivity.BanditCaptivityBehavior());
                // Wave 4.15.0 (2026-06-07): Autonomous bandit village raids (Phase A).
                // Bandit cultures periodically spawn raid parties from their hideouts
                // to attack nearby non-bandit-owned villages. APIs proven stable across
                // 1.2.x → 1.4.5 per workflow research wm2s277vj + wba8a1ubc.
                starter.AddBehavior(new BandItPlus.BanditRaid.BanditRaidBehavior());
                // Wave 4.x: atmospheric bandit blockade of towns/castles (sibling to raids).
                starter.AddBehavior(new BandItPlus.BanditRaid.BanditBlockadeBehavior());
                // Bandit siege Phase 1: kingdom round-trip test harness (gated by BanditSiegePhase1Test).
                starter.AddBehavior(new BandItPlus.BanditRaid.BanditSiegeBehavior());
                // Wave 4.15.0-fix41 (2026-06-10): pure-observer raid lifecycle trace.
                // Logs every vanilla raid-related event so user can player-raid a village
                // and we see the real API sequence.
                starter.AddBehavior(new BandItPlus.Patches.RaidLifecycleTraceBehavior());
                // Wave 4.19.0 (2026-06-11): "Blood & Banners" origin system — the
                // once-per-campaign choice popup + per-culture bandit peace state.
                starter.AddBehavior(new BandItPlus.Origins.BanditOriginBehavior());
                // Living Chronicle: records player milestones onto the hero's encyclopedia page.
                starter.AddBehavior(new BandItPlus.Origins.BanditChronicleBehavior());
                // v169 (2026-05-17): Hideout Defenses — boosts hideout defender
                // rosters (count + tier) so assaults are a real fight.
                starter.AddBehavior(new BandItPlus.BanditPower.HideoutDefenseBehavior());
            }
        }
    }
}
