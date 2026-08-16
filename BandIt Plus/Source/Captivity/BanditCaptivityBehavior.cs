using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using BandItPlus.HideoutVisit;

namespace BandItPlus.Captivity
{
    // Wave 4.31.0 — when a bandit/looter party captures the player, BP marches the captor to
    // its hideout and jails the player there. BP OWNS the captivity loop (CaptivityAutoEndPatch
    // suppresses vanilla's auto-release while Active) so the march + jail hold. Wait-menus advance
    // time so the captor can travel. Escape: wait (passive) / attempt / bribe. Walkable cell = Task 8.
    public class BanditCaptivityBehavior : CampaignBehaviorBase
    {
        public static BanditCaptivityBehavior Instance { get; private set; }

        private bool _active;
        private MobileParty _captor;
        private Settlement _targetHideout;
        private double _startHours;
        private float _escapeProgress;
        private bool _atHideout;
        private float _reassertTimer;
        // Cellmate (jailbreak) — the captive in your cell who can escape with you. ONE-TIME offer.
        private bool _cellmateOfferExpired;         // set once you fail a jailbreak and land back in the pit
        private bool _cellmateAccepted;             // you asked him to come on THIS attempt
        private CharacterObject _cellmateCharacter; // who joins your party on a successful escape

        public bool Active => _active;
        public bool AtHideout => _atHideout;

        public BanditCaptivityBehavior() { Instance = this; }

        public override void RegisterEvents()
        {
            CampaignEvents.HeroPrisonerTaken.AddNonSerializedListener(this, OnHeroPrisonerTaken);
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnCaptiveHourly);
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, OnPartyDestroyed);
            CampaignEvents.OnGameLoadFinishedEvent.AddNonSerializedListener(this, OnGameLoadFinished);
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("_bpcap_active", ref _active);
            dataStore.SyncData("_bpcap_captor", ref _captor);
            dataStore.SyncData("_bpcap_hideout", ref _targetHideout);
            dataStore.SyncData("_bpcap_start", ref _startHours);
            dataStore.SyncData("_bpcap_progress", ref _escapeProgress);
            dataStore.SyncData("_bpcap_athideout", ref _atHideout);
            dataStore.SyncData("_bpcap_cm_expired", ref _cellmateOfferExpired);
            dataStore.SyncData("_bpcap_cm_accepted", ref _cellmateAccepted);
            dataStore.SyncData("_bpcap_cm_char", ref _cellmateCharacter);
        }

        // ── Capture ───────────────────────────────────────────────────────────
        private void OnHeroPrisonerTaken(PartyBase captorParty, Hero prisoner)
        {
            try
            {
                if (prisoner != Hero.MainHero) return;
                var s = MCMSettings.Instance;
                if (s == null || !s.EnableMod || !s.EnableBanditCaptivity) return;
                var captor = captorParty?.MobileParty;
                if (captor == null || !BandItPlus.Cultures.CultureProfileRegistry.IsBanditParty(captor)) return;

                var hideout = ResolveHideout(captor);
                if (hideout == null) { Log("no hideout for captor -> vanilla captivity"); return; }

                _active = true;
                _captor = captor;
                _targetHideout = hideout;
                _atHideout = false;
                _reassertTimer = 0f;   // fresh march clock (drives the stall timeout)
                _escapeProgress = 0f;
                _startHours = CampaignTime.Now.ToHours;
                Log("captured by " + (captor.Name?.ToString() ?? "?") + " -> hideout=" + hideout.Name?.ToString());
                BeginMarch();
            }
            catch (Exception ex) { Log("OnHeroPrisonerTaken fail: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private Settlement ResolveHideout(MobileParty captor)
        {
            // ARCHITECTURE (2026-06-28): only a captor that GENUINELY OWNS a hideout (a custom BandIt Plus bandit
            // clan) gets the march + jailbreak — we march them to THEIR OWN lair. Vanilla looters / bandits have no
            // hideout of their own, so return null -> vanilla captivity. This structurally ends the whack-a-mole of
            // borrowing some nearby/custom/Looters hideout for a hideout-less captor (the "Ulan Twice-Banished's
            // Hideout" phantom): if the captor doesn't own a hideout, there is simply nothing to march to.
            var cc = captor.ActualClan;
            if (cc == null) { Log("captor has no clan (looter) -> vanilla captivity"); return null; }

            Settlement bestOwn = null; float bestOwnSq = float.MaxValue;
            foreach (var st in Settlement.All)
            {
                if (st == null || !st.IsHideout || !st.IsActive) continue;
                if (st.OwnerClan != cc) continue;                  // must be THIS captor-clan's own lair
                if (!(st.SettlementComponent is Hideout)) continue;
                float dx = st.Position.X - captor.Position.X, dy = st.Position.Y - captor.Position.Y;
                float d = dx * dx + dy * dy;
                if (d < bestOwnSq) { bestOwnSq = d; bestOwn = st; }
            }

            if (bestOwn != null)
            {
                RevealHideout(bestOwn);   // their genuine den -> reveal so the march can path in + the player sees it
                Log("captor -> OWN hideout " + bestOwn.Name?.ToString());
                return bestOwn;
            }
            Log("captor owns no hideout -> vanilla captivity");
            return null;
        }

        private void RevealHideout(Settlement st)
        {
            try
            {
                var ho = st.SettlementComponent as Hideout;
                if (ho != null) typeof(Hideout).GetProperty("IsSpotted")?.SetValue(ho, true);
            }
            catch (Exception ex) { Log("spot hideout fail: " + ex.Message); }
        }

        // ── March routing ─────────────────────────────────────────────────────
        private void OnTick(float dt)
        {
            if (!_active || _captor == null || !_captor.IsActive) return;
            if (_atHideout || _targetHideout == null) return;
            _reassertTimer += dt;
            // Stall fallback: if the captor can't reach the hideout in time (pulled into a battle / can't path), force the
            // jail open anyway so a wandering captor never soft-locks the player on the map.
            if (_reassertTimer > 15f) { Log("march stalled " + _reassertTimer.ToString("0") + "s -> force arrival"); OnReachedHideout(); return; }
            if (_reassertTimer < 0.5f) return;
            _reassertTimer = 0f;
            try
            {
                _captor.Ai.SetDoNotMakeNewDecisions(true);   // keep the vanilla AI frozen so it can't peel off to chase fights
                _captor.IgnoreByOtherPartiesTill(CampaignTime.HoursFromNow(2f)); // unmolested -> can't be intercepted/destroyed mid-march
                if (_captor.TargetSettlement != _targetHideout)
                    BandItPlus.Compat.PartyOrderCompat.GoToSettlement(_captor, _targetHideout);
                // Arrived = entered the hideout OR got within ~1 map unit of it (foreign hideouts aren't "entered").
                float dx = _captor.Position.X - _targetHideout.Position.X, dy = _captor.Position.Y - _targetHideout.Position.Y;
                if (_captor.CurrentSettlement == _targetHideout || (dx * dx + dy * dy) < 4.0f) OnReachedHideout(); // loosened radius: register arrival even if the captor can't fully enter a foreign hideout
            }
            catch (Exception ex) { Log("route tick fail: " + ex.Message); }
        }
        private void OnReachedHideout() { _atHideout = true; Log("arrived at hideout"); OpenJail(); }

        private void BeginMarch()
        {
            Log("march begins");
            try { GameMenu.ActivateGameMenu("bp_captivity_march"); }
            catch (Exception ex) { Log("march menu activate fail: " + ex.Message); }
        }
        private void OpenJail()
        {
            Log("jail opened");
            try { GameMenu.ActivateGameMenu("bp_captivity_jail"); }
            catch (Exception ex) { Log("jail menu activate fail: " + ex.Message); }
        }
        // ── Edge cases / save-load (Task 9) ───────────────────────────────────
        private void OnPartyDestroyed(MobileParty destroyed, PartyBase killer)
        {
            if (_active && destroyed != null && destroyed == _captor)
            {
                Log("captor destroyed -> released");
                EscapeNow("captor lost");
            }
        }
        private void OnGameLoadFinished()
        {
            if (!_active) return;
            try { GameMenu.ActivateGameMenu(_atHideout ? "bp_captivity_jail" : "bp_captivity_march"); }
            catch (Exception ex) { Log("load reactivate fail: " + ex.Message); }
        }

        // ── Menus ─────────────────────────────────────────────────────────────
        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            try
            {
                starter.AddWaitGameMenu("bp_captivity_march", "{=!}{BP_CAP_TEXT}",
                    new OnInitDelegate(MarchInit), new OnConditionDelegate(WaitCond), null,
                    new OnTickDelegate(CaptiveTick),
                    GameMenu.MenuAndOptionType.WaitMenuHideProgressAndHoursOption,
                    GameMenu.MenuOverlayType.None, 0f, GameMenu.MenuFlags.None, null);
                starter.AddGameMenuOption("bp_captivity_march", "bp_cap_slip", "{=bp_captivitybehavior_001}Try to slip your bonds",
                    new GameMenuOption.OnConditionDelegate(EscOptCond),
                    new GameMenuOption.OnConsequenceDelegate(SlipConseq), false, 0);

                starter.AddWaitGameMenu("bp_captivity_jail", "{=!}{BP_CAP_TEXT}",
                    new OnInitDelegate(JailInit), new OnConditionDelegate(WaitCond), null,
                    new OnTickDelegate(CaptiveTick),
                    GameMenu.MenuAndOptionType.WaitMenuHideProgressAndHoursOption,
                    GameMenu.MenuOverlayType.None, 0f, GameMenu.MenuFlags.None, null);
                starter.AddGameMenuOption("bp_captivity_jail", "bp_cap_bribe", "{=bp_captivitybehavior_002}Bribe a guard ({BP_CAP_BRIBE}{GOLD_ICON})",
                    new GameMenuOption.OnConditionDelegate(BribeCond),
                    new GameMenuOption.OnConsequenceDelegate(BribeConseq), false, 1);
                starter.AddGameMenuOption("bp_captivity_jail", "bp_cap_jailbreak", "{=bp_captivitybehavior_003}Attempt a jailbreak",
                    new GameMenuOption.OnConditionDelegate(JailbreakCond),
                    new GameMenuOption.OnConsequenceDelegate(JailbreakConseq), false, 2);
                Log("menus registered");

                // ── Cellmate conversation (auto-started inside the jailbreak mission; gated to that exact agent) ──
                starter.AddDialogLine("bp_cm_greet", "start", "bp_cm_1",
                    "{=bp_captivitybehavior_004}Easy, friend — don't sit up too quick. They cracked you good when they hauled you down here.",
                    CellmateConvoCond, null, 200);
                starter.AddPlayerLine("bp_cm_where", "bp_cm_1", "bp_cm_2",
                    "{=bp_captivitybehavior_005}Where... am I? What happened?", null, null);
                starter.AddDialogLine("bp_cm_story", "bp_cm_2", "bp_cm_3",
                    "{=bp_captivitybehavior_006}The pit. Beneath a bandit hideout. They ran your party down on the road and dragged the living here to rot. Three days, it's been, for me.",
                    null, null);
                // The escape offer — ONE TIME. Gone once you've failed and landed back here.
                starter.AddPlayerLine("bp_cm_together", "bp_cm_3", "bp_cm_accept",
                    "{=bp_captivitybehavior_007}Then come with me. We get out of here together.", CellmateOfferOpenCond, null);
                starter.AddPlayerLine("bp_cm_alone", "bp_cm_3", "bp_cm_bye",
                    "{=bp_captivitybehavior_008}Stay put. I move faster alone.", CellmateOfferOpenCond, null);
                starter.AddDialogLine("bp_cm_done", "bp_cm_3", "bp_cm_bye",
                    "{=bp_captivitybehavior_009}I had my chance with you, and we both ended back in this hole. I'm done running — go on alone.",
                    CellmateOfferExpiredCond, null);
                starter.AddDialogLine("bp_cm_accept", "bp_cm_accept", "close_window",
                    "{=bp_captivitybehavior_010}Aye. Get me past those guards and into open air, and my blade's yours. Lead on.",
                    null, CellmateAcceptConseq);
                starter.AddDialogLine("bp_cm_bye", "bp_cm_bye", "close_window",
                    "{=bp_captivitybehavior_011}...Your call. Mind the guards' rounds — and good luck to you.", null, CellmateConvoEndConseq);
            }
            catch (Exception ex) { Log("menu register fail: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void MarchInit(MenuCallbackArgs args)
        {
            args.MenuContext.GameMenu.GetText().SetTextVariable("BP_CAP_TEXT", new TextObject(
                "{=bp_captivitybehavior_012}Bound and slung over a saddle, you are dragged toward the brigands' hideout. You bide your time…"));
        }
        private void JailInit(MenuCallbackArgs args)
        {
            args.MenuContext.GameMenu.GetText().SetTextVariable("BP_CAP_TEXT", new TextObject(
                "{=bp_captivitybehavior_013}You rot in the brigands' pit beneath the hideout, watched by a surly guard."));
            MBTextManager.SetTextVariable("BP_CAP_BRIBE", BribeCost().ToString(), false);
        }
        private bool WaitCond(MenuCallbackArgs args) { args.MenuContext.GameMenu.StartWait(); return true; }
        private void CaptiveTick(MenuCallbackArgs args, CampaignTime dt)
        {
            if (!_active) { try { args.MenuContext.GameMenu.EndWait(); GameMenu.ExitToLast(); } catch { } }
        }

        private bool EscOptCond(MenuCallbackArgs args) { args.optionLeaveType = GameMenuOption.LeaveType.Escape; return true; }
        private void SlipConseq(MenuCallbackArgs args)
        {
            if (MBRandom.RandomFloat < 0.10f / DifficultyFactor()) EscapeNow("slip");
            else Log("slip failed");
        }

        // ── Cellmate dialog gating ──
        private bool CellmateConvoCond()
        {
            return BpJailbreakMissionController.CurrentCellmate != null
                && object.ReferenceEquals(SandBox.Conversation.ConversationMission.OneToOneConversationAgent, BpJailbreakMissionController.CurrentCellmate);
        }
        private bool CellmateOfferOpenCond() { return !_cellmateOfferExpired; }
        private bool CellmateOfferExpiredCond() { return _cellmateOfferExpired; }
        private void CellmateAcceptConseq()
        {
            _cellmateAccepted = true;
            _cellmateCharacter = BpJailbreakMissionController.CurrentCellmate?.Character as CharacterObject;
            try { BpJailbreakMissionController.StartCellmateFollow(); } catch (Exception ex) { Log("cellmate follow fail: " + ex.Message); }
            try { BpJailbreakMissionController.SuppressCellmateTalk(); } catch { }
            Log("cellmate agreed to escape together");
        }
        private void CellmateConvoEndConseq()   // any conversation end -> no more "[F] Talk"
        {
            try { BpJailbreakMissionController.SuppressCellmateTalk(); } catch { }
        }

        private bool BribeCond(MenuCallbackArgs args)
        {
            args.optionLeaveType = GameMenuOption.LeaveType.Bribe;
            int cost = BribeCost();
            if (Hero.MainHero.Gold < cost)
            {
                args.IsEnabled = false;
                args.Tooltip = new TextObject("{=bp_captivitybehavior_019}You cannot scrape together the bribe ({COST}).").SetTextVariable("COST", cost);
            }
            return true;
        }
        private void BribeConseq(MenuCallbackArgs args) { BribeOut(); }

        private bool JailbreakCond(MenuCallbackArgs args) { args.optionLeaveType = GameMenuOption.LeaveType.Mission; return _atHideout; }
        private void JailbreakConseq(MenuCallbackArgs args)
        {
            int d = MCMSettings.Instance != null ? MCMSettings.Instance.CaptivityEscapeDifficulty : 3;
            // Custom briefing menu (left art / right stakes); Begin launches the mission, Wait stays captive.
            string captorName = (_captor?.ActualClan?.Culture ?? _captor?.Party?.Culture ?? _captor?.LeaderHero?.Culture)?.Name?.ToString();
            int guards = BanditJailbreakLauncher.GuardCount(d);
            bool opened = BandItPlus.UI.BpJailbreakBriefScreen.Open(captorName, DifficultyLabel(d), guards, choice =>
            {
                if (choice == "begin") BanditJailbreakLauncher.Open(_captor, _targetHideout, d);
                // "wait" -> stay captive; the pit menu is still active underneath.
            });
            if (!opened) BanditJailbreakLauncher.Open(_captor, _targetHideout, d); // fallback: launch directly if the briefing won't open
        }

        private static string DifficultyLabel(int d)
        {
            switch (System.Math.Max(1, System.Math.Min(5, d)))
            {
                case 1: return BandItPlus.Localization.Get("bp_captivitybehavior_014", "Easy");
                case 2: return BandItPlus.Localization.Get("bp_captivitybehavior_015", "Cautious");
                case 3: return BandItPlus.Localization.Get("bp_captivitybehavior_016", "Risky");
                case 4: return BandItPlus.Localization.Get("bp_captivitybehavior_017", "Dangerous");
                default: return BandItPlus.Localization.Get("bp_captivitybehavior_018", "Hard");
            }
        }

        // ── Escape logic ──────────────────────────────────────────────────────
        private float DifficultyFactor()
        {
            int d = MCMSettings.Instance != null ? MCMSettings.Instance.CaptivityEscapeDifficulty : 3;
            return 0.25f + 0.25f * d; // d1=0.5 .. d5=1.5
        }
        private int BribeCost()
        {
            // Bribing skips the whole jailbreak (no gear loss, no risk), so it's priced as a rich man's shortcut:
            // a meaningful gold sink that scales with clan tier. Broke early players are left to actually escape.
            int baseCost = 2500 + (Clan.PlayerClan != null ? Clan.PlayerClan.Tier * 2000 : 0);
            return (int)(baseCost * DifficultyFactor());
        }
        private void BribeOut()
        {
            int cost = BribeCost();
            if (Hero.MainHero.Gold < cost) { Log("bribe unaffordable"); return; }
            Hero.MainHero.ChangeHeroGold(-cost);
            EscapeNow("bribe");
        }
        private void OnCaptiveHourly()
        {
            if (!_active || !_atHideout) return;
            _escapeProgress += 0.04f / DifficultyFactor();
            if (_escapeProgress >= 1f) EscapeNow("wait");
        }
        private void EscapeNow(string how)
        {
            try
            {
                Log("escaped via " + how);
                try { if (_captor != null && _captor.IsActive) _captor.Ai.SetDoNotMakeNewDecisions(false); } catch { } // resume normal AI
                _active = false; _atHideout = false; _captor = null; _targetHideout = null;
                if (PlayerCaptivity.IsCaptive)
                    TaleWorlds.CampaignSystem.Actions.EndCaptivityAction.ApplyByEscape(Hero.MainHero, null);
            }
            catch (Exception ex) { Log("EscapeNow fail: " + ex.Message); }
        }

        // ── Jailbreak outcomes (Wave 4.32) ────────────────────────────────────
        public void OnJailbreakSucceeded()
        {
            Log("jailbreak succeeded -> freed");
            if (_cellmateAccepted && _cellmateCharacter != null && !BpJailbreakMissionController.CellmateDead)
            {
                try
                {
                    // Make him a real CLAN companion (a Hero), not just a roster troop.
                    var hero = HeroCreator.CreateSpecialHero(_cellmateCharacter, null, null, null, -1);
                    if (hero != null)
                    {
                        hero.ChangeState(Hero.CharacterStates.Active);
                        TaleWorlds.CampaignSystem.Actions.AddCompanionAction.Apply(Clan.PlayerClan, hero);
                        TaleWorlds.CampaignSystem.Actions.AddHeroToPartyAction.Apply(hero, MobileParty.MainParty, true);
                        Log("cellmate joined the CLAN as a companion: " + hero.Name?.ToString());
                    }
                }
                catch (Exception ex) { Log("cellmate companion join fail: " + ex.GetType().Name + ": " + ex.Message); }
            }
            _cellmateAccepted = false; _cellmateCharacter = null;
            // Clean-escape reward (Wave 4.38): scaled renown + gold computed mission-side; Ghost runs pay 1.5x.
            try
            {
                int ren = BpJailbreakMissionController.RewardRenown;
                int gold = BpJailbreakMissionController.RewardGold;
                if (ren > 0) TaleWorlds.CampaignSystem.Actions.GainRenownAction.Apply(Hero.MainHero, ren, false);
                if (gold > 0) TaleWorlds.CampaignSystem.Actions.GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, gold, false);
            }
            catch (Exception ex) { Log("escape reward fail: " + ex.GetType().Name + ": " + ex.Message); }
            BpJailbreakMissionController.RewardRenown = 0; BpJailbreakMissionController.RewardGold = 0;
            EscapeNow("jailbreak");
        }
        public void OnJailbreakFailed()
        {
            Log("jailbreak failed -> wounded + back to pit");
            _cellmateOfferExpired = true; _cellmateAccepted = false; _cellmateCharacter = null; // he won't risk it again
            try
            {
                var h = Hero.MainHero;
                int dmg = (int)(h.HitPoints * 0.6f);
                if (dmg > 0) h.HitPoints = Math.Max(1, h.HitPoints - dmg); // the "insane" stakes
                _escapeProgress = Math.Max(0f, _escapeProgress - 0.25f);   // next escape harder
            }
            catch (Exception ex) { Log("wound apply fail: " + ex.Message); }
            if (_active && _atHideout) { try { GameMenu.ActivateGameMenu("bp_captivity_jail"); } catch { } }
        }
        public void OnJailbreakAborted()
        {
            Log("jailbreak aborted (open failed) -> back to pit, no penalty");
            if (_active && _atHideout) { try { GameMenu.ActivateGameMenu("bp_captivity_jail"); } catch { } }
        }

        internal static void Log(string m)
        {
            try { HideoutPeacefulVisitState.Log("[BP-Captivity] " + m); } catch { }
        }
    }
}
