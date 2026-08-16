using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using BandItPlus.HideoutVisit;

namespace BandItPlus.BanditRaid
{
    // Bandit Siege v2 — kingdom-less rebel-clan model. A dedicated independent clan + synthesized chief
    // spawns a chief-led warband, declares war via ApplyByRebellion, lays a visible party siege, force-
    // captures the target via ApplyBySiege after the siege is established, and HOLDS it as a reflavored
    // bandit holdout. No Kingdom is created => none of the temp-kingdom crash surface exists.
    // See spec/plan docs/superpowers/.../2026-06-29-bandit-siege-v2-rebel-clan*.
    public class BanditSiegeBehavior : CampaignBehaviorBase
    {
        private Clan _holdoutClan;
        private Hero _holdoutChief;
        // 2026-07-02 march-spam fix (not saved; rebuilt each session): last time a
        // Marches PANEL was shown (48h cooldown), and each lord party's committed
        // drive target (partyId -> (targetId, committedUntilHours)) so a cleared
        // order re-commits to the SAME fief instead of re-rolling a new one.
        private double _lastMarchPanelHours = -99999.0;
        private readonly System.Collections.Generic.Dictionary<string, (string targetId, double untilH)> _driveCommit
            = new System.Collections.Generic.Dictionary<string, (string, double)>();
        // dupe-guard tripwire memory: report each duplicated party id once per session
        private readonly System.Collections.Generic.HashSet<string> _dupeReportedIds
            = new System.Collections.Generic.HashSet<string>();
        // FINDING-3 fix (lift hysteresis): settlements we lifted a siege from are banned
        // from re-targeting for 72h (fed into `claimed` each sweep) — otherwise a party
        // could besiege -> lift -> re-besiege the same doomed fief in a daily loop.
        private readonly System.Collections.Generic.Dictionary<string, double> _liftedUntil
            = new System.Collections.Generic.Dictionary<string, double>();
        // FINDING-4 fix (truce hysteresis): when each truce was signed (kingdomId -> hours);
        // C4 never re-declares on a kingdom truced < 14 days ago. Non-saved by design.
        private readonly System.Collections.Generic.Dictionary<string, double> _truceAtHours
            = new System.Collections.Generic.Dictionary<string, double>();
        // 2026-07-02 log-churn fix: parties ACTUALLY sent to relieve a besieged fief. The
        // relief-release branch only fires for these — a recruit COMMUTE (goto own fief to
        // hire) keeps its travel order instead of being released+re-issued every sweep.
        private readonly System.Collections.Generic.HashSet<string> _relievingIds
            = new System.Collections.Generic.HashSet<string>();
        private string _holdoutChiefId = "";       // persisted StringId of the FOUNDING King (Vaykos) so the reclaim guard survives reload
        private MobileParty _siegeParty;
        private Settlement _siegeTarget;
        private string _siegeTargetId = "";         // persisted StringId of the current siege target so a reload doesn't re-randomize it
        private CultureObject _banditCulture;
        private double _siegeTimeoutHours = -1;
        private double _captureAtHours = -1;
        private double _lastPollLogHours = 0;
        private bool _done;
        private bool _attempted;
        private bool _mustering;
        private int _armyTarget;
        private double _lastMusterDayHours = 0;
        private bool _companionsHired;
        private bool _holdingPhase;            // after capture: drive the King outward to besiege, never idle inside
        private double _lastDriveHours;
        private Settlement _hideout;
        private System.Collections.Generic.List<Hero> _lieutenants;
        private bool _lieutenantsSpawned;
        private double _madKingWarTimeHours = -1;  // absolute CampaignTime hours when the King declares war (set at spawn)
        private bool _wentToWar;                     // one-shot: THE TURN has fired
        private bool _playerAllied;                  // player formally accepted the Mad King's alliance offer at THE TURN
        private string _lastMarchAnnouncedTargetId = ""; // 2026-07-01 (Bandit-King GUI): once-per-new-siege-target gate for the ForMarches panel (ephemeral)
        // 2026-07-02 WARLORD BRAIN (approved design; ALL non-saved — rebuilt each session, and the
        // diplomacy state deliberately degrades to a recompute from the live war/peace map after a reload):
        private string _preyKingdomId = "";        // C) StringId of the current PREY kingdom (the one war he presses)
        private bool _boostApiMissLogged;          // B) once-per-session log gate for a building-boost API mismatch
        private const float NearbyEnemyRadius = 50f; // A) map units — "nearby" for hostile field-strength scoring
        private static System.Reflection.FieldInfo _boostField;   // B) cached Town.BoostBuildingProcess (public int in 1.4.5/1.4.6)
        private static bool _boostFieldProbed;

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
        }

        public override void SyncData(IDataStore dataStore)
        {
            // Only the Mad-King war-clock + one-shot flags must survive save/reload (primitives — no container types).
            // Clan/party refs stay ephemeral: RunSiege re-discovers them each session by scanning Clan.All.
            try
            {
                dataStore.SyncData("bp_madking_wartime", ref _madKingWarTimeHours);
                dataStore.SyncData("bp_madking_wenttowar", ref _wentToWar);
                dataStore.SyncData("bp_madking_attempted", ref _attempted);
                dataStore.SyncData("bp_madking_playerallied", ref _playerAllied);
                // FIX 1: persist the mid-siege target + timers so a reload doesn't re-randomize the target and reset the capture clock.
                dataStore.SyncData("bp_madking_siegetargetid", ref _siegeTargetId);
                dataStore.SyncData("bp_madking_captureat", ref _captureAtHours);
                dataStore.SyncData("bp_madking_siegetimeout", ref _siegeTimeoutHours);
                // FIX 3+4: persist the FOUNDING King's identity so "Vaykos reclaims the crown" can still fire after a reload.
                dataStore.SyncData("bp_madking_chiefid", ref _holdoutChiefId);
            }
            catch { }
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            Log("siege behavior ready (rebel-clan v2)");
            try { AddHoldoutDialog(starter); } catch (Exception e) { Log("AddHoldoutDialog: " + e.Message); }
        }

        // Placeholder parley for the Bandit King + his lieutenants. They are built from notable/merchant templates,
        // so the engine would otherwise show the vanilla "Let me see what you have for sale..." trade dialog. A
        // high-priority (1000) greeting gated to bp_rebel_clan_ heroes routes the conversation into our own branch
        // — a "coming soon, next update" line with only a leave option — so the trade/sale option never appears.
        private void AddHoldoutDialog(CampaignGameStarter starter)
        {
            starter.AddDialogLine("bp_king_greet", "start", "bp_king_root",
                "{=bp_hcr_013}You stand before the Bandit King. A true parley with me is coming soon — in a future update. For now, be on your way.",
                new ConversationSentence.OnConditionDelegate(IsHoldoutHero), null, 1000);
            starter.AddPlayerLine("bp_king_leave", "bp_king_root", "close_window",
                "{=bp_hcr_014}As you say. Farewell.",
                null, null, 1000);
        }

        // True when the hero in the current 1-to-1 conversation is the King or one of his lieutenants
        // (any hero of a bp_rebel_clan_ holdout clan).
        private static bool IsHoldoutHero()
        {
            try
            {
                var h = Hero.OneToOneConversationHero;
                return h != null && h.Clan != null && h.Clan.StringId != null
                    && h.Clan.StringId.StartsWith("bp_rebel_clan_", System.StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private void OnHourlyTick()
        {
            try
            {
                var mcm = MCMSettings.Instance;
                // RELEASE gate = EnableBanditSieges (opt-in master toggle). BanditSiegePhase2Test also activates it +
                // switches to the fast 7-day buildup clock (see Phase A) so the arc stays quick to playtest.
                if (mcm == null || (!mcm.EnableBanditSieges && !mcm.BanditSiegePhase2Test)) return;
                RunSiege();
            }
            catch (Exception ex) { Log("OnHourlyTick threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void RunSiege()
        {
            try
            {
                // Resume after a save/reload (clan/party refs are ephemeral; the war-clock + flags persist via SyncData).
                // Re-find the holdout clan by StringId and resume the correct phase.
                if (!_holdingPhase && !_done && _holdoutClan == null && _attempted)
                {
                    Clan holdout = null;
                    foreach (var c in Clan.All)
                        if (c != null && c.StringId != null && c.StringId.StartsWith("bp_rebel_clan_") && !c.IsEliminated) { holdout = c; break; }
                    if (holdout == null) { _done = true; }                                                    // King eliminated
                    else if (holdout.Settlements != null && holdout.Settlements.Count > 0) { _holdoutClan = holdout; _holdingPhase = true; } // CONQUEST (owns a fief)
                    else
                    {
                        // Mid-first-siege (at war, no fief yet) OR buildup (at peace) — rebind the King + target.
                        _holdoutClan = holdout;
                        // FIX 3: resolve the FOUNDING King from the persisted StringId (GetObject<Hero> is unreliable for
                        // synthesized heroes — scan the clan's own heroes) so the reclaim guard survives reload. Fall back
                        // to the current Leader only if the founder is gone; backfill the id for older saves.
                        _holdoutChief = ResolveFoundingChief(holdout);

                        try { _siegeParty = _holdoutChief != null ? _holdoutChief.PartyBelongedTo : null; } catch { }
                        // FIX 6: a captured King has a null PartyBelongedTo. Do NOT latch _done (not persisted) and kill the
                        // whole session's arc — only end if the clan has NO living hero at all (truly eliminated). Otherwise
                        // keep the clan/chief bound so EnsureKingSuccessionAndEscape can free/crown a leader, and re-attempt.
                        if (_siegeParty == null || !_siegeParty.IsActive)
                        {
                            bool hasLivingHero = false;
                            try { if (holdout.Heroes != null) foreach (var hh in holdout.Heroes) if (hh != null && hh.IsAlive) { hasLivingHero = true; break; } } catch { }
                            if (!hasLivingHero) { _done = true; Log("resume: holdout has no living hero — ending"); }
                            else
                            {
                                // Recoverable (King captured/partyless). Keep bound; leave the arc open. Try to re-spawn a
                                // muster warband under the current FREE leader so a later tick resumes the buildup/siege.
                                if (_hideout == null) foreach (var s in Settlement.All) if (s != null && s.IsHideout && s.IsActive) { _hideout = s; break; }
                                // _banditCulture isn't persisted — re-derive from the holdout/hideout culture.
                                if (_banditCulture == null) _banditCulture = (_hideout != null ? _hideout.Culture : null) ?? holdout.Culture;
                                Hero freeLeader = null;
                                try { if (holdout.Leader != null && holdout.Leader.IsAlive && !holdout.Leader.IsPrisoner) freeLeader = holdout.Leader; } catch { }
                                if (freeLeader == null) { try { if (holdout.Heroes != null) foreach (var hh in holdout.Heroes) if (hh != null && hh.IsAlive && !hh.IsPrisoner) { freeLeader = hh; break; } } catch { } }
                                if (freeLeader != null && _banditCulture != null && _hideout != null)
                                {
                                    try
                                    {
                                        var reparty = SpawnSiegeWarband(_banditCulture, _hideout, freeLeader);
                                        if (reparty != null && reparty.LeaderHero == freeLeader) { _siegeParty = reparty; try { _siegeParty.SetMoveModeHold(); } catch { } }
                                    }
                                    catch (Exception e) { Log("resume respawn warband: " + e.Message); }
                                }
                                // Whether or not the re-spawn succeeded, do NOT latch _done and do NOT null _holdoutClan.
                                _lastMusterDayHours = CampaignTime.Now.ToHours;
                                _mustering = true;
                                Log("resume: MAD KING recoverable (party lost, King likely captured) — arc kept open, re-mustering (" + holdout.StringId + ")");
                            }
                        }
                        else
                        {
                            if (_hideout == null) foreach (var s in Settlement.All) if (s != null && s.IsHideout && s.IsActive) { _hideout = s; break; }
                            // FIX 1: prefer the PERSISTED siege target (so a reload doesn't re-randomize it); only fall back
                            // to a fresh random pick when the persisted one is missing/invalid. Keep _siegeTargetId in sync.
                            _siegeTarget = ResolvePersistedSiegeTarget() ?? PickSiegeTarget(_hideout);
                            _siegeTargetId = _siegeTarget != null ? _siegeTarget.StringId : "";
                            _armyTarget = _siegeTarget != null ? BanditRebelSiegeHelper.ComputeArmyTarget(_siegeTarget) : 300;
                            if (_wentToWar || IsAtWarWithAnyKingdom(holdout))
                            {
                                // FIX 1: do NOT unconditionally reset the timers — only initialize them when unset (default -1),
                                // so a persisted in-progress capture clock continues rather than restarting each reload.
                                if (_siegeTarget != null)
                                {
                                    BanditRebelSiegeHelper.BeginSiege(_siegeParty, _siegeTarget);
                                    if (_captureAtHours <= 0) _captureAtHours = CampaignTime.Now.ToHours + 24.0 * 8.0;
                                    if (_siegeTimeoutHours <= 0) _siegeTimeoutHours = CampaignTime.Now.ToHours + 24.0 * 30.0;
                                }
                                Log("resume: MAD KING conquest siege rebound (" + holdout.StringId + ", target=" + (_siegeTarget != null ? _siegeTarget.StringId : "?") + ")");
                            }
                            else
                            {
                                _lastMusterDayHours = CampaignTime.Now.ToHours;
                                _mustering = true;
                                Log("resume: HIDDEN WARLORD buildup rebound (" + holdout.StringId + ", war-clock persists)");
                            }
                        }
                    }
                }

                // Every hour, keep the Mad King's clan led by a FREE, LIVING hero (escape + succession) in BOTH the
                // buildup and conquest phases — a captured/killed King must never leave the clan leaderless (crash).
                if (_holdoutClan != null && _holdoutClan.StringId != null && _holdoutClan.StringId.StartsWith("bp_rebel_clan_"))
                    EnsureKingSuccessionAndEscape(_holdoutClan);

                if (_holdingPhase) { DriveHoldout(); return; }
                if (_done) return;

                // --- Phase A: stand up the holdout clan + warband + siege (once) ---
                if (_holdoutClan == null)
                {
                    if (_attempted) return; // single attempt per session
                    // RANDOM hideout (with a bandit-boss culture) so the King rises in a different region each campaign.
                    var hideouts = new System.Collections.Generic.List<Settlement>();
                    foreach (var s in Settlement.All)
                        if (s != null && s.IsHideout && s.IsActive && s.Culture != null && s.Culture.BanditBoss != null) hideouts.Add(s);
                    if (hideouts.Count == 0) { _attempted = true; _done = true; Log("siege: no hideout culture with a BanditBoss"); return; }
                    Settlement hideout = hideouts[MBRandom.RandomInt(hideouts.Count)];
                    CultureObject culture = hideout.Culture;
                    _hideout = hideout;
                    _attempted = true;
                    _banditCulture = culture;

                    var target = PickSiegeTarget(hideout);
                    if (target == null) { _done = true; Log("siege: no eligible target"); return; }

                    _holdoutClan = BanditRebelSiegeHelper.CreateHoldoutClan(culture, target, out _holdoutChief);
                    if (_holdoutClan == null || _holdoutChief == null) { _done = true; Log("siege: holdout clan creation failed"); return; }
                    // FIX 3: record the FOUNDING King's StringId here (and ONLY here) so the reclaim guard can tell the
                    // original Vaykos apart from any crowned heir after a reload.
                    _holdoutChiefId = _holdoutChief.StringId;

                    _siegeParty = SpawnSiegeWarband(culture, hideout, _holdoutChief);
                    if (_siegeParty == null || _siegeParty.LeaderHero != _holdoutChief)
                    { Log("siege: warband/leader bind failed — dissolving"); BanditRebelSiegeHelper.DissolveOnFailure(_holdoutClan, _siegeParty); _done = true; return; }

                    _siegeTarget = target;
                    _siegeTargetId = _siegeTarget != null ? _siegeTarget.StringId : ""; // FIX 1: persist the target so a reload doesn't re-randomize it
                    // MAD KING: do NOT declare war at birth. He builds strength HIDDEN and at PEACE for years, then
                    // "goes mad" and declares war on ALL kingdoms at once (GoToWar). Bandit-neutrality is always on.
                    BanditRebelSiegeHelper.MakePeaceWithAllBandits(_holdoutClan);
                    _armyTarget = BanditRebelSiegeHelper.ComputeArmyTarget(_siegeTarget);
                    int delayYears = 5; bool fastTest = false;
                    try { var mcm = MCMSettings.Instance; if (mcm != null) { delayYears = System.Math.Max(1, mcm.MadKingWarDelayYears); fastTest = mcm.BanditSiegePhase2Test; } } catch { }
                    // RELEASE: N years of hidden buildup. TEST (BanditSiegePhase2Test on): ~7 days so the arc is quick to playtest.
                    _madKingWarTimeHours = CampaignTime.Now.ToHours + (fastTest ? 24.0 * 7.0 : 24.0 * 365.0 * delayYears);
                    _lastMusterDayHours = CampaignTime.Now.ToHours;
                    _mustering = true;
                    try { _siegeParty.SetMoveModeHold(); } catch { } // park at the hideout; at peace so nothing hunts him
                    Log("HIDDEN WARLORD: mustering at hideout, at PEACE — war in " + (fastTest ? "~7 days (TEST)" : delayYears + " years"));
                    return;
                }

                // --- MUSTER: sit at the hideout, hire companions, grow the army to the garrison-scaled target ---
                if (_mustering)
                {
                    double mH = CampaignTime.Now.ToHours;
                    if (_siegeParty == null || !_siegeParty.IsActive) { Log("muster: party lost — dissolving"); BanditRebelSiegeHelper.DissolveOnFailure(_holdoutClan, _siegeParty); _done = true; return; }

                    if (!_companionsHired) { _lieutenants = BanditRebelSiegeHelper.HireCompanions(_holdoutChief, 3); _companionsHired = true; }

                    try { _siegeParty.SetMoveModeHold(); } catch { }
                    try { _siegeParty.IgnoreByOtherPartiesTill(CampaignTime.DaysFromNow(5f)); } catch { }

                    if (mH - _lastMusterDayHours >= 24.0)
                    {
                        _lastMusterDayHours = mH;
                        int perDay = (_armyTarget / 5) + 1;
                        int total = BanditRebelSiegeHelper.MusterStep(_siegeParty, _armyTarget, perDay);
                        Log("mustering " + _siegeTarget.Name + ": " + total + "/" + _armyTarget);
                    }

                    // Keep growing at PEACE until the war-clock trips — NEVER march early. THE TURN fires GoToWar.
                    if (CampaignTime.Now.ToHours >= _madKingWarTimeHours)
                    {
                        _mustering = false;
                        GoToWar();
                    }
                    return;
                }

                // --- Phase B: keep the party marching + protected; force-capture on the timer; else dissolve ---
                double nowH = CampaignTime.Now.ToHours;
                bool captured = _siegeTarget != null && _siegeTarget.OwnerClan == _holdoutClan;
                bool partyAlive = _siegeParty != null && _siegeParty.IsActive;
                bool timedOut = nowH >= _siegeTimeoutHours;

                if (nowH - _lastPollLogHours >= 24.0)
                {
                    _lastPollLogHours = nowH;
                    Log("siege poll: party=" + (partyAlive ? "alive" : "DEAD")
                        + " daysToAssault=" + (int)System.Math.Max(0, (_captureAtHours - nowH) / 24.0)
                        + " owner=" + (_siegeTarget != null && _siegeTarget.OwnerClan != null ? _siegeTarget.OwnerClan.Name.ToString() : "?"));
                }

                if (captured)
                {
                    Log("siege: CAPTURED " + _siegeTarget.Name + " — clan now holds it");
                    HoldCapturedFief(_holdoutClan, _siegeTarget, _siegeParty, _banditCulture);
                    _holdingPhase = true;   // was _done — keep driving the King outward to besiege enemy realms
                    return;
                }

                if (partyAlive)
                {
                    BanditRebelSiegeHelper.MaintainSiege(_siegeParty, _siegeTarget); // re-protect + keep heading to the target
                    if (nowH >= _captureAtHours)
                    {
                        Log("siege: assaulting " + _siegeTarget.Name + " — forcing capture");
                        bool captureInvoked = BanditRebelSiegeHelper.Capture(_holdoutChief, _siegeTarget);
                        // Capture DEFERS (false) while a battle rages at the walls (2026-07-02
                        // crash fix) => retry HOURLY until the fight resolves; once invoked,
                        // retry daily until OwnerClan flips (next poll detects it).
                        _captureAtHours = nowH + (captureInvoked ? 24.0 : 1.0);
                    }
                }

                if (!partyAlive || timedOut)
                {
                    Log("siege: " + (!partyAlive ? "party lost" : "timed out") + " on " + (_siegeTarget != null ? _siegeTarget.Name.ToString() : "?") + " — dissolving");
                    BanditRebelSiegeHelper.DissolveOnFailure(_holdoutClan, _siegeParty);
                    _done = true;
                }
            }
            catch (Exception ex) { Log("RunSiege threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // FIX 3: resolve the FOUNDING King (Vaykos) from the persisted _holdoutChiefId. GetObject<Hero>(id) is
        // unreliable for these synthesized heroes (see BanditChiefRegistry note), so scan the clan's own heroes by
        // StringId. Returns the live founder if still in the clan; else falls back to the clan Leader and backfills
        // _holdoutChiefId (older saves that predate the persisted id).
        private Hero ResolveFoundingChief(Clan holdout)
        {
            try
            {
                if (holdout == null) return null;
                if (!string.IsNullOrEmpty(_holdoutChiefId) && holdout.Heroes != null)
                {
                    foreach (var h in holdout.Heroes)
                        if (h != null && h.IsAlive && h.StringId == _holdoutChiefId) return h;
                }
                // founder missing/dead/gone (older save or crowned heir replaced him) -> fall back to current leader.
                var leader = holdout.Leader;
                if (string.IsNullOrEmpty(_holdoutChiefId) && leader != null) _holdoutChiefId = leader.StringId; // backfill only when id was never persisted
                return leader;
            }
            catch (Exception ex) { Log("ResolveFoundingChief threw " + ex.GetType().Name + ": " + ex.Message); return holdout != null ? holdout.Leader : null; }
        }

        // FIX 1: resolve the persisted siege target Settlement by StringId (Settlement.Find is the codebase's proven
        // by-id lookup). Returns it only if it's still a VALID siege target (a non-bandit town/castle), else null so
        // the caller falls back to a fresh random pick.
        private Settlement ResolvePersistedSiegeTarget()
        {
            try
            {
                if (string.IsNullOrEmpty(_siegeTargetId)) return null;
                var s = Settlement.Find(_siegeTargetId);
                if (s == null) return null;
                if (!(s.IsTown || s.IsCastle)) return null;
                if (s.MapFaction != null && s.MapFaction.IsBanditFaction) return null;
                return s;
            }
            catch (Exception ex) { Log("ResolvePersistedSiegeTarget threw " + ex.GetType().Name + ": " + ex.Message); return null; }
        }

        // Spawn a bandit warband and convert it to a LordPartyComponent led by the holdout chief (so LeaderHero
        // == chief and the party can wage war + besiege). The chief already belongs to the holdout clan.
        private MobileParty SpawnSiegeWarband(CultureObject culture, Settlement hideout, Hero chief)
        {
            try
            {
                var party = BanditWarbandFactory.SpawnWarband(culture, hideout);
                if (party == null) { Log("SpawnWarband returned null for " + culture.StringId); return null; }
                try { if (chief.CharacterObject != null) party.MemberRoster.AddToCounts(chief.CharacterObject, 1); } catch { }
                try { LordPartyComponent.ConvertPartyToLordParty(party, chief, chief); }
                catch (Exception ex) { Log("ConvertPartyToLordParty fail: " + ex.GetType().Name + " (inner: " + (ex.InnerException != null ? ex.InnerException.Message : "none") + ")"); }
                Log("spawned warband '" + party.StringId + "' leader=" + (party.LeaderHero != null ? party.LeaderHero.Name.ToString() : "NULL"));
                return (party.LeaderHero == chief) ? party : null;
            }
            catch (Exception ex) { Log("SpawnSiegeWarband threw " + ex.GetType().Name + ": " + ex.Message); return null; }
        }

        // Nearest weak (garrison <= cap), non-bandit, non-besieged town/castle to the hideout.
        private Settlement PickSiegeTarget(Settlement from)
        {
            try
            {
                int cap = 100000; // King can target any fief; the muster phase scales the army to the garrison
                // Collect ALL eligible fiefs and pick a RANDOM one — a different target every campaign (was: always
                // the single nearest, i.e. always Marunath). The muster scales the army to whatever garrison it has.
                var eligible = new System.Collections.Generic.List<Settlement>();
                foreach (var s in Settlement.All)
                {
                    if (s == null || !(s.IsTown || s.IsCastle)) continue;
                    if (s.MapFaction != null && s.MapFaction.IsBanditFaction) continue;
                    if (s.SiegeEvent != null) continue;
                    int g = (s.Town != null && s.Town.GarrisonParty != null && s.Town.GarrisonParty.MemberRoster != null) ? s.Town.GarrisonParty.MemberRoster.TotalManCount : 0;
                    if (g > cap) continue;
                    eligible.Add(s);
                }
                if (eligible.Count == 0) return null;
                return eligible[MBRandom.RandomInt(eligible.Count)];
            }
            catch (Exception ex) { Log("PickSiegeTarget threw " + ex.GetType().Name + ": " + ex.Message); return null; }
        }

        // STEP 1 helper: "is the player bandit-allied / at peace with the mod's bandits?" There is no single
        // authoritative flag — BanditAllianceBehavior.IsAllied is per-chief (cultureId) and PlayerBanditNeutrality
        // gates everything on MCMSettings.PeacefulEncounters. So the definitive check is:
        //   (a) the global bandit-peace MCM setting PeacefulEncounters is ON, OR
        //   (b) the player has non-negative relation with any registered bandit chief (BanditChiefRegistry).
        // Either signal means the King treats the player as a friend of the outlaw wilds and spares them.
        // The player has EARNED the Mad King's alliance ONLY by completing the outlaws' trust — being ALLIED
        // (BanditAllianceBehavior.IsAllied) with EVERY bandit-chief culture. A mere setting (PeacefulEncounters)
        // or one neutral relation is NOT enough — those fired the offer prematurely before any chief trust existed.
        private bool PlayerIsBanditAllied()
        {
            try { return BanditRebelSiegeHelper.PlayerHasEarnedBanditAlliance(); } catch { return false; }
        }

        // Escape + succession: keep the Mad King's clan led by a FREE, LIVING hero so it never goes leaderless
        // (a leaderless clan hard-crashes on FactionDiscontinuation -> WarPartyComponent.OnFinalize). Captured
        // outlaws get a strong escape chance and return; the founding King reclaims the crown once free.
        private void EnsureKingSuccessionAndEscape(Clan clan)
        {
            try
            {
                if (clan == null || clan.IsEliminated) return;
                // (a) escape: give each captured, living clan hero a strong chance to break out (realistic for outlaws).
                var heroes = new System.Collections.Generic.List<Hero>(clan.Heroes);
                foreach (var h in heroes)
                {
                    if (h == null || !h.IsAlive || !h.IsPrisoner) continue;
                    // FIX 2: never spring a prisoner the PLAYER captured — that would nullify the player's own catch.
                    // PartyBase holding this prisoner (verified: Hero.PartyBelongedToAsPrisoner -> PartyBase). A dungeon/
                    // settlement prisoner may have a null LeaderHero, so also check Owner + MapFaction.
                    PartyBase captor = null;
                    try { captor = h.PartyBelongedToAsPrisoner; } catch { }
                    bool playerHolds = false;
                    try
                    {
                        playerHolds = captor != null &&
                            (captor.LeaderHero == Hero.MainHero
                             || captor.Owner == Hero.MainHero
                             || (captor.MapFaction != null && Clan.PlayerClan != null && captor.MapFaction == Clan.PlayerClan.MapFaction));
                    }
                    catch { }
                    if (playerHolds) continue; // let the player keep/ransom his catch
                    if (MBRandom.RandomFloat < 0.10f) // ~10%/hour -> usually free within a day or so
                        try { TaleWorlds.CampaignSystem.Actions.EndCaptivityAction.ApplyByEscape(h); Log("escape: " + h.Name + " slipped captivity"); } catch { }
                }
                // (b) succession / never-leaderless: ensure a free living leader, preferring the founding King.
                var cur = clan.Leader;
                bool curOk = cur != null && cur.IsAlive && !cur.IsPrisoner;
                // prefer the founding King (_holdoutChief) whenever he is free+alive again (reclaim the crown)
                if (_holdoutChief != null && _holdoutChief.IsAlive && !_holdoutChief.IsPrisoner && _holdoutChief.Clan == clan && clan.Leader != _holdoutChief)
                {
                    try { clan.SetLeader(_holdoutChief); Log("Vaykos reclaims the crown"); } catch { }
                    return;
                }
                if (!curOk)
                {
                    Hero heir = null;
                    foreach (var h in heroes) { if (h != null && h.IsAlive && !h.IsPrisoner) { heir = h; break; } }
                    if (heir != null && heir != cur)
                        try { clan.SetLeader(heir); Log("crowned new Mad King: " + heir.Name); } catch { }
                    // if NO free living hero exists, do nothing — escape rolls will free one; if all are truly dead the clan ends (now crash-safe).
                }
            }
            catch (Exception e) { Log("succession/escape: " + e.Message); }
        }

        // THE TURN: the hidden King "goes mad" — declare war on every kingdom at once, announce it, then begin the
        // conquest (the existing march->siege->capture->hold->drive flow). One-shot / idempotent across reloads.
        private void GoToWar()
        {
            if (_wentToWar) return;
            _wentToWar = true;
            try { BanditRebelSiegeHelper.DeclareWarOnAllKingdoms(_holdoutClan); } catch (Exception e) { Log("goto-war declare: " + e.Message); }
            // 2026-07-01 (Bandit-King GUI, STEP C1): the plain "Mad King Rises" inquiry is
            // replaced by the premium ForRises panel carrying live data (kingdoms-at-war,
            // host size, lieutenants, fleet). Show() marshals to the main thread itself.
            try
            {
                int kingdomsAtWar = CountKingdomsAtWar(_holdoutClan);
                int ships = CountShips(_siegeParty);
                BandItPlus.UI.BanditKingBriefManager.Show(
                    BandItPlus.UI.BanditKingBriefData.ForRises(_holdoutClan, _siegeParty, kingdomsAtWar, ships));
            }
            catch (Exception e) { Log("goto-war rises panel: " + e.Message); }
            Log("THE MAD KING RISES — Vaykos declares war on all kingdoms");
            // STEP 2c: the King's stance toward the PLAYER hinges on whether they've EARNED the outlaws' FULL trust
            // (all bandit chiefs allied). EARNED => peace + a formal alliance offer: Accept keeps peace (allied),
            // Refuse => war. NOT earned => a rival: at war, guaranteed — explicit, since the kingdoms-only war loop
            // never touches an INDEPENDENT player clan.
            var playerFaction = Clan.PlayerClan != null ? Clan.PlayerClan.MapFaction : null;
            if (playerFaction != null && playerFaction != _holdoutClan)
            {
                if (PlayerIsBanditAllied())
                {
                    try { FactionManager.SetNeutral(playerFaction, _holdoutClan); } catch { }
                    // 2026-07-01 (Bandit-King GUI, STEP C1): the plain "Mad King's Offer" inquiry
                    // is replaced by the premium ForOffer panel. The EXISTING accept/refuse bodies
                    // move verbatim into the button delegates (Stand with him → peace/allied;
                    // Refuse → war). Show() marshals to the main thread itself.
                    try
                    {
                        var offer = BandItPlus.UI.BanditKingBriefData.ForOffer(_holdoutClan);
                        offer.OnPrimary = () => { _playerAllied = true; try { FactionManager.SetNeutral(playerFaction, _holdoutClan); } catch { } Log("player ACCEPTED the Mad King's alliance -> peace"); };
                        offer.OnSecondary = () => { _playerAllied = false; try { if (!_holdoutClan.IsAtWarWith(playerFaction)) DeclareWarAction.ApplyByDefault(_holdoutClan, playerFaction); } catch { } Log("player REFUSED the Mad King's alliance -> war"); };
                        BandItPlus.UI.BanditKingBriefManager.Show(offer);
                    }
                    catch (Exception e) { Log("goto-war alliance offer panel: " + e.Message); }
                }
                else
                {
                    try { if (!_holdoutClan.IsAtWarWith(playerFaction)) DeclareWarAction.ApplyByDefault(_holdoutClan, playerFaction); } catch (Exception e) { Log("goto-war player war: " + e.Message); }
                    Log("player has NOT earned the alliance -> at war with the Mad King");
                }
            }
            try
            {
                _siegeTargetId = _siegeTarget != null ? _siegeTarget.StringId : ""; // FIX 1: keep the persisted target in sync at the war->siege transition
                BanditRebelSiegeHelper.BeginSiege(_siegeParty, _siegeTarget);
                _captureAtHours = CampaignTime.Now.ToHours + 24.0 * 8.0;
                _siegeTimeoutHours = CampaignTime.Now.ToHours + 24.0 * 30.0;
                // STEP C2: announce the first conquest target (once per new target).
                AnnounceMarch(_siegeTarget, _captureAtHours);
            }
            catch (Exception e) { Log("goto-war begin siege: " + e.Message); }
        }

        // Save-safe conquest-phase test: is this holdout clan at war with any (non-bandit) kingdom?
        private static bool IsAtWarWithAnyKingdom(Clan clan)
        {
            try { if (clan != null) foreach (var k in Kingdom.All) if (k != null && !k.IsBanditFaction && clan.IsAtWarWith(k)) return true; } catch { }
            return false;
        }

        // 2026-07-01 (Bandit-King GUI): live count of non-bandit kingdoms the Mad King is at war with (for ForRises).
        private static int CountKingdomsAtWar(Clan clan)
        {
            int n = 0;
            try { if (clan != null) foreach (var k in Kingdom.All) if (k != null && !k.IsBanditFaction && clan.IsAtWarWith(k)) n++; } catch { }
            return n;
        }

        // 2026-07-01 (Bandit-King GUI): live ship count of the King's party (PartyBase.Ships — see GiveFleet). Guarded.
        private static int CountShips(MobileParty party)
        {
            try { return (party != null && party.Party != null && party.Party.Ships != null) ? party.Party.Ships.Count : 0; }
            catch { return 0; }
        }

        // 2026-07-01 (Bandit-King GUI, STEP C2): fire the ForMarches premium panel ONCE per NEW siege target.
        // Gated on _lastMarchAnnouncedTargetId so re-issuing the besiege order each poll doesn't spam the panel.
        private void AnnounceMarch(Settlement target, double captureAtHours)
        {
            try
            {
                if (target == null || target.StringId == null) return;
                if (_lastMarchAnnouncedTargetId == target.StringId) return; // already announced this target
                _lastMarchAnnouncedTargetId = target.StringId;
                BandItPlus.UI.BanditKingBriefManager.Show(
                    BandItPlus.UI.BanditKingBriefData.ForMarches(_holdoutClan, _siegeParty, target, captureAtHours));
            }
            catch (Exception e) { Log("announce-march: " + e.Message); }
        }

        // The clan already owns the fief (ApplyBySiege). Make it a permanent independent bandit holdout, set it at
        // PEACE with all bandits (its garrison/army must never fight looters/bandits), garrison the SURPLUS while
        // leaving the King a marching field army, and RELEASE the party to native AI so it stops idling inside the
        // captured fief and goes on to besiege enemy realms (driven by DriveHoldout each day).
        private void HoldCapturedFief(Clan clan, Settlement target, MobileParty warband, CultureObject banditCulture)
        {
            try { BanditRebelSiegeHelper.MakeIndependentBanditHoldout(clan); } catch (Exception e) { Log("hold independent: " + e.Message); }
            try { BanditRebelSiegeHelper.MakePeaceWithAllBandits(clan); } catch (Exception e) { Log("hold peace-bandits: " + e.Message); }

            try
            {
                int total = (warband != null && warband.MemberRoster != null) ? warband.MemberRoster.TotalManCount : 0;
                int keep = System.Math.Max(40, total / 2); // King keeps ~half as a field army; the rest defends the fief
                int n = BanditSeizeHelper.GarrisonInto(target, warband, true, keep);
                Log("hold: garrisoned " + n + " into " + target.Name + " (kept ~" + keep + " in the field)");
            }
            catch (Exception e) { Log("hold garrison: " + e.Message); }

            // Release the party to native lord AI: un-pin + re-enable decisions; one Hold so it doesn't re-path to
            // the now-owned target. The hourly DriveHoldout sweep redirects it to an enemy fief.
            try { if (warband != null) warband.IgnoreByOtherPartiesTill(CampaignTime.Now); } catch (Exception e) { Log("hold unpin: " + e.Message); }
            try { if (warband != null && warband.Ai != null) warband.Ai.SetDoNotMakeNewDecisions(false); } catch (Exception e) { Log("hold ai-release: " + e.Message); }
            try { if (warband != null) warband.SetMoveModeHold(); } catch { }

            // Give the King a real fleet so he is naval-capable like a native king (crosses water, the holdout
            // clan flips naval-capable once a party owns ships — see ClanNavalCapabilityPatch).
            try { BanditRebelSiegeHelper.GiveFleet(warband, 3); } catch (Exception e) { Log("king fleet: " + e.Message); }

            // Promote the hired lieutenants into their OWN native lord parties (once) — each leads troops, raids,
            // besieges, and sails just like the King, so the holdout fields a real bandit host, not one party.
            try
            {
                if (!_lieutenantsSpawned)
                {
                    _lieutenantsSpawned = true;
                    if (_lieutenants != null)
                    {
                        int half = System.Math.Max(80, BanditRebelSiegeHelper.ComputeArmyTarget(target) / 2);
                        foreach (var lt in _lieutenants)
                        {
                            try
                            {
                                var p = BanditRebelSiegeHelper.SpawnLieutenantParty(lt, banditCulture, _hideout, half, true);
                                if (p != null) Log("promoted lieutenant " + (lt != null && lt.Name != null ? lt.Name.ToString() : "?") + " -> " + p.StringId);
                            }
                            catch (Exception e2) { Log("promote lt: " + e2.Message); }
                        }
                    }
                }
            }
            catch (Exception e) { Log("promote: " + e.Message); }
        }

        // SAVE-SAFE holdout driver (behavior fields are null after a reload): re-discover the holdout clan from
        // Clan.All by its StringId prefix, keep it at peace with all bandits, and send its idle lord party to
        // besiege the nearest enemy-KINGDOM fief — so the Bandit King keeps expanding instead of idling inside.
        private void DriveHoldout()
        {
            try
            {
                if (_holdoutClan == null || _holdoutClan.IsEliminated)
                {
                    _holdoutClan = null;
                    foreach (var c in Clan.All)
                        if (c != null && c.StringId != null && c.StringId.StartsWith("bp_rebel_clan_") && !c.IsEliminated)
                        { _holdoutClan = c; break; }
                    // Keep conquering even while owning NO fief (a home fief can be retaken between captures) — only
                    // stop if the clan is truly eliminated (no parties, no heroes). Its lord parties still campaign.
                    if (_holdoutClan == null) { Log("drive: holdout clan eliminated — ending"); _holdingPhase = false; _done = true; return; }
                    Log("drive: (re)discovered holdout " + _holdoutClan.StringId);
                }
                // FIX 3/4: do NOT unconditionally rebind to the current leader (that overwrites the founding King with a
                // crowned heir, so "Vaykos reclaims the crown" could never fire). Keep the founder while he's still a
                // valid clan hero; only re-resolve (from _holdoutChiefId, else the Leader) when he's null/gone/dead.
                if (_holdoutChief == null || _holdoutChief.Clan != _holdoutClan || !_holdoutChief.IsAlive)
                    _holdoutChief = ResolveFoundingChief(_holdoutClan);

                double nowH = CampaignTime.Now.ToHours;

                // (Bandit peace is enforced authoritatively + silently by HoldoutBanditPeacePatch — no re-sweep here.)
                if (nowH - _lastDriveHours < 24.0) return; // once/day — the en-route skip prevents re-target thrash
                _lastDriveHours = nowH;

                // 2026-07-02 WARLORD BRAIN: the daily fief-development (B) and diplomacy (C) steps ride
                // this drive loop's existing once-per-day cadence — no new event subscriptions. Each is
                // fully self-gated on its own console MCM toggle and try/caught so a miss never stalls
                // the drive sweep. Diplomacy runs FIRST so today's prey choice steers today's targeting.
                try { RunDiplomacyBrain(); } catch (Exception eDip) { Log("RunDiplomacyBrain threw " + eDip.GetType().Name + ": " + eDip.Message); }
                try { DevelopHoldoutFiefs(); } catch (Exception eDev) { Log("DevelopHoldoutFiefs threw " + eDev.GetType().Name + ": " + eDev.Message); }

                // A) CALCULATING WARLORD: one hostile-field-party snapshot per sweep (positions +
                // strengths of enemy lord parties in the field) shared by the siege-lift check and the
                // attack gate below. Null when the cautious toggle is off (both checks then no-op).
                bool cautiousAI = false;
                try { cautiousAI = MCMSettings.Instance != null && MCMSettings.Instance.MadKingCautiousAI; } catch { }
                var sweepHostiles = cautiousAI ? SnapshotHostileFieldParties(_holdoutClan, null) : null;

                // Drive EVERY lord party of the holdout clan (the King + each promoted lieutenant) independently:
                // gather (reinforce + raid) when too weak, besiege the nearest enemy-kingdom fief when strong.
                var parties = new System.Collections.Generic.List<MobileParty>();
                var seenPartyIds = new System.Collections.Generic.HashSet<string>();
                try
                {
                    foreach (var wpc in _holdoutClan.WarPartyComponents)
                    {
                        var mp = wpc != null ? wpc.MobileParty : null;
                        if (mp == null || !mp.IsActive || mp.LeaderHero == null) continue;
                        // 2026-07-02 dupe guard: after captivity->escape cycles the clan's
                        // WarPartyComponents can yield the SAME party twice (observed: identical
                        // same-ms drive lines while escape/poll lines stayed single). Driving a
                        // party twice doubles orders and would double-hire. De-dupe by id and
                        // TRIPWIRE-log the corruption so the engine-side source stays visible.
                        string pid = mp.StringId ?? ("hash:" + mp.GetHashCode());
                        if (!seenPartyIds.Add(pid))
                        {
                            // tripwire CONFIRMED live (2026-07-02 01:09 logs: 3 warbands duped every
                            // sweep) — report each party ONCE per session, not every 5s (log bloat).
                            if (_dupeReportedIds.Add(pid)) Log("drive: DUPLICATE WarPartyComponent for " + pid + " — skipped (dupe guard; reported once per session)");
                            continue;
                        }
                        parties.Add(mp);
                    }
                }
                catch { }
                if (parties.Count == 0) return;

                var claimed = new System.Collections.Generic.HashSet<string>(); // de-conflict siege targets between lords
                // FINDING-3 fix: recently LIFTED fiefs stay off-limits for 72h — fed into the
                // shared claimed set so NO lord re-targets a doomed siege site while its relief
                // army still lingers. Expired bans are purged here.
                try
                {
                    var expiredLifts = new System.Collections.Generic.List<string>();
                    foreach (var kv in _liftedUntil)
                    {
                        if (nowH > kv.Value) expiredLifts.Add(kv.Key);
                        else claimed.Add(kv.Key);
                    }
                    foreach (var id in expiredLifts) _liftedUntil.Remove(id);
                }
                catch { }
                foreach (var lp in parties)
                {
                    try
                    {
                        // Already besieging, in battle, OR en route to a target — leave it committed. Re-issuing a
                        // besiege order every tick to a party already marching made lords oscillate between fiefs (thrash).
                        if ((lp.MapEvent != null) || (lp.SiegeEvent != null) || (lp.BesiegedSettlement != null) || (lp.TargetSettlement != null))
                        {
                            // A3) SIEGE SELF-PRESERVATION (2026-07-02 warlord brain): a besieging party LIFTS its
                            // siege when the hostile relief strength gathered nearby exceeds ~1.3x its own — unlock
                            // the AI, drop the target commitment, and fall back to the nearest owned fief. Never
                            // fires mid-battle (MapEvent) and NEVER for the King's scripted FIRST-arc siege
                            // (_siegeParty while the arc is still running, i.e. before _holdingPhase) — that set
                            // piece stays committed and is already crash-guarded.
                            // FINDING-6 fix: the old "!_holdingPhase" clause was vacuous (DriveHoldout
                            // only runs in the holding phase) — guard on the scripted-arc state itself.
                            if (cautiousAI && lp.BesiegedSettlement != null && lp.MapEvent == null
                                && !(lp == _siegeParty && !_done && _siegeTarget != null && lp.BesiegedSettlement == _siegeTarget))
                            {
                                float myStr = PartyStrength(lp);
                                float reliefSum, reliefMax;
                                SumFieldStrengthNear(sweepHostiles, lp.BesiegedSettlement.Position.X, lp.BesiegedSettlement.Position.Y, NearbyEnemyRadius, out reliefSum, out reliefMax);
                                if (myStr > 0f && reliefSum > 1.3f * myStr)
                                {
                                    string whoLift = lp.LeaderHero != null ? lp.LeaderHero.Name.ToString() : lp.StringId;
                                    string siteLift = lp.BesiegedSettlement.Name != null ? lp.BesiegedSettlement.Name.ToString() : "?";
                                    try { if (lp.Ai != null) lp.Ai.SetDoNotMakeNewDecisions(false); } catch { }
                                    try { lp.SetMoveModeHold(); } catch { } // clears the besiege order/_targetSettlement (1.4.5)
                                    var fallback = NearestOwnedFief(lp);
                                    if (fallback != null) { try { BandItPlus.Compat.PartyOrderCompat.GoToSettlement(lp, fallback); } catch { } }
                                    try { if (lp.StringId != null) _driveCommit.Remove(lp.StringId); } catch { }
                                    // FINDING-3 fix: ban this fief for 72h and claim it this sweep so
                                    // no lord (including this one) re-besieges the same doomed target.
                                    try
                                    {
                                        var siteId = lp.BesiegedSettlement.StringId;
                                        if (siteId != null) { _liftedUntil[siteId] = nowH + 72.0; claimed.Add(siteId); }
                                    }
                                    catch { }
                                    Log("drive: " + whoLift + " LIFTS siege of " + siteLift + " — relief army too strong (" + (int)reliefSum + " vs " + (int)myStr + ")");
                                    continue; // regroups today; the next daily sweep re-targets fresh
                                }
                            }
                            // RELIEF RELEASE: SendToRelieve froze the party on DefendSettlement(ownFief) with a
                            // do-not-decide AI and never unfroze it. If this party's target is an OWNED fief of the
                            // holdout clan that is no longer under threat (no siege, no active event), release it so it
                            // rejoins the offensive rotation this tick. An OFFENSIVE besiege target is an ENEMY fief —
                            // NOT in _holdoutClan.Settlements — so Contains() correctly leaves those committed.
                            if (lp.TargetSettlement != null
                                && _holdoutClan.Settlements != null && _holdoutClan.Settlements.Contains(lp.TargetSettlement)
                                && !lp.TargetSettlement.IsUnderSiege
                                && lp.MapEvent == null && lp.SiegeEvent == null
                                // log-churn fix: ONLY release parties SendToRelieve actually froze; a
                                // recruit commute to an owned fief keeps its travel order committed.
                                // Reload safety: _relievingIds is non-saved, so ALSO release any party
                                // already sitting INSIDE its own-fief target (a returned reliever).
                                && lp.StringId != null
                                && (_relievingIds.Contains(lp.StringId) || lp.CurrentSettlement == lp.TargetSettlement))
                            {
                                string whoRel = lp.LeaderHero != null ? lp.LeaderHero.Name.ToString() : lp.StringId;
                                try { if (lp.Ai != null) lp.Ai.SetDoNotMakeNewDecisions(false); } catch { }
                                try { lp.SetMoveModeHold(); } catch { } // clears _targetSettlement in 1.4.5
                                _relievingIds.Remove(lp.StringId);
                                Log("drive: " + whoRel + " relief done, released");
                                // fall through to the normal offensive pick this tick
                            }
                            else
                            {
                                var claimSt = lp.BesiegedSettlement ?? lp.TargetSettlement;
                                if (claimSt != null && claimSt.StringId != null) claimed.Add(claimSt.StringId);
                                continue;
                            }
                        }
                        string who = lp.LeaderHero != null ? lp.LeaderHero.Name.ToString() : lp.StringId;

                        // B2: DEFEND — if any fief the holdout OWNS is under siege, peel the nearest free lord off to
                        // relieve it instead of pressing an attack elsewhere. De-conflicted via `claimed` (HashSet<string>
                        // keyed by StringId) so lords don't all pile on one relief.
                        Settlement besiegedOwn = null;
                        try
                        {
                            if (_holdoutClan.Settlements != null)
                                foreach (var s in _holdoutClan.Settlements)
                                    if (s != null && s.IsUnderSiege && (s.StringId == null || !claimed.Contains(s.StringId))) { besiegedOwn = s; break; }
                        }
                        catch { }
                        if (besiegedOwn != null)
                        {
                            BanditRebelSiegeHelper.SendToRelieve(lp, besiegedOwn);
                            try { if (lp.StringId != null) _relievingIds.Add(lp.StringId); } catch { }
                            if (besiegedOwn.StringId != null) claimed.Add(besiegedOwn.StringId);
                            Log("drive: " + who + " RELIEVING " + besiegedOwn.Name);
                            continue; // this party defends this tick; skip the offensive pick
                        }

                        // 2026-07-02 target COMMITMENT: if this party was already ordered to a
                        // fief and the order got cleared (native AI reconsidered), re-commit to
                        // the SAME fief until it falls / becomes ours / the commitment expires —
                        // no more weathervane re-rolls between Flintolg/Pen Cannoc/Druimmor.
                        Settlement tgt = null;
                        try
                        {
                            (string targetId, double untilH) commit;
                            if (lp.StringId != null && _driveCommit.TryGetValue(lp.StringId, out commit))
                            {
                                if (nowH <= commit.untilH && commit.targetId != null)
                                {
                                    var cs = Settlement.Find(commit.targetId);
                                    if (cs != null && cs.OwnerClan != _holdoutClan
                                        && (cs.StringId == null || !claimed.Contains(cs.StringId)))
                                        tgt = cs;
                                }
                                if (tgt == null) _driveCommit.Remove(lp.StringId);
                            }
                        }
                        catch { }
                        if (tgt == null) tgt = PickDriveTargetExcluding(_holdoutClan, claimed);
                        int need = tgt != null ? BanditRebelSiegeHelper.ComputeArmyTarget(tgt) : 0;
                        int have = lp.MemberRoster != null ? lp.MemberRoster.TotalManCount : 0;
                        // A2) ATTACK GATE (2026-07-02 warlord brain): cautious mode besieges only with a
                        // decisive numbers advantage (>= 1.5x the fief's defense need) AND when no single
                        // hostile field army near the target outguns this party; legacy mode keeps need/2.
                                // FINDING-2 fix: clamp the cautious gate to a host one party can actually
                        // field — 1.5x a big town's need (up to 2000) would demand 3000 men and make
                        // towns unconquerable. 460 ~= the mustered host; caution beyond that comes
                        // from the relief-sum check below, not from impossible headcounts.
                        int gate = cautiousAI ? System.Math.Max(80, System.Math.Min((int)(need * 1.5), 460)) : System.Math.Max(80, need / 2);
                        if (cautiousAI && tgt != null && have >= gate)
                        {
                            float myAtkStr = PartyStrength(lp);
                            float sumT, strongestT;
                            SumFieldStrengthNear(sweepHostiles, tgt.Position.X, tgt.Position.Y, NearbyEnemyRadius, out sumT, out strongestT);
                            // FINDING-3 fix: gate on the SUM of nearby hostiles too (same metric the
                            // LIFT uses, tighter threshold) — three medium armies camped on a target
                            // must block the attack, not just one giant.
                            if (myAtkStr > 0f && (strongestT > myAtkStr || sumT > 1.2f * myAtkStr))
                            {
                                Log("drive: " + who + " holds off " + tgt.Name + " — hostile field strength too high (sum " + (int)sumT + ", strongest " + (int)strongestT + " vs " + (int)myAtkStr + ")");
                                tgt = null; // gather instead today; re-scored tomorrow
                            }
                        }
                        if (tgt != null && have >= gate)
                        {
                            if (tgt.StringId != null) claimed.Add(tgt.StringId);
                            BanditRebelSiegeHelper.SendToBesiege(lp, tgt);
                            try { if (lp.StringId != null && tgt.StringId != null) _driveCommit[lp.StringId] = (tgt.StringId, nowH + 72.0); } catch { }
                            Log("drive: " + who + " (army " + have + "/" + need + ") besieging " + tgt.Name);
                            // STEP C2 + 2026-07-02 spam fix: the full-screen Marches PANEL belongs
                            // to the KING's own march only — lieutenants campaign silently (their
                            // sieges still log above). A 48h cooldown stops back-to-back panels
                            // even for the King; AnnounceMarch's own gate still de-dupes targets.
                            bool isKingsMarch = _holdoutChief != null && lp.LeaderHero == _holdoutChief;
                            if (isKingsMarch && nowH - _lastMarchPanelHours >= 48.0)
                            {
                                _lastMarchPanelHours = nowH;
                                AnnounceMarch(tgt, CampaignTime.Now.ToHours + 24.0 * 8.0);
                            }
                        }
                        else
                        {
                            // FINDING-5 fix: while fief development is on, the reinforce floor rises
                            // to the garrison TARGET so the daily PAID garrison trickle isn't drained
                            // straight back into field parties (a 40g/head treadmill); legacy floor 60.
                            bool devOn = false; try { devOn = MCMSettings.Instance != null && MCMSettings.Instance.MadKingDevelopsFiefs; } catch { }
                            int reinf = BanditRebelSiegeHelper.ReinforceFromGarrison(lp, _holdoutClan, devOn ? 300 : 60);
                            // 2026-07-02 HIRE (approved design): native-first recruitment.
                            // (1) clan OWNS a fief -> march the weak party INTO it with a free
                            //     AI; vanilla RecruitmentCampaignBehavior then hires the fief's
                            //     notables' volunteers with the leader's own gold (real pools,
                            //     fief culture, native pacing). Replaces raiding in this mode.
                            // (2) clan owns NOTHING -> paid "call of the wilds" batch hire so
                            //     the war never stalls; raiding continues for loot.
                            Settlement recruitFief = null;
                            try
                            {
                                if (_holdoutClan.Settlements != null)
                                    foreach (var s in _holdoutClan.Settlements)
                                        if (s != null && s.IsFortification && !s.IsUnderSiege) { recruitFief = s; break; }
                            }
                            catch { }
                            if (recruitFief != null)
                            {
                                if (lp.CurrentSettlement != recruitFief)
                                {
                                    try { if (lp.Ai != null) lp.Ai.SetDoNotMakeNewDecisions(false); } catch { }
                                    try { BandItPlus.Compat.PartyOrderCompat.GoToSettlement(lp, recruitFief); } catch { }
                                }
                                Log("drive: " + who + " gathering (army " + have + "+" + reinf + " reinf; recruiting at " + recruitFief.Name + ")");
                            }
                            else
                            {
                                // FINDING-1 fix: hire toward the ATTACK GATE, not the bare need —
                                // capping at need (1.0x) while the gate wants 1.5x soft-locked the
                                // fief-less arc (grow to need, forever short of the gate, raid loop).
                                int hired = BanditRebelSiegeHelper.HireFromTheWilds(lp, _holdoutClan, System.Math.Max(need, gate));
                                var v = PickRaidTarget(_holdoutClan);
                                if (v != null) BanditRebelSiegeHelper.RaidVillage(lp, v);
                                Log("drive: " + who + " gathering (army " + have + "+" + reinf + " reinf; hired " + hired + " from the wilds" + (v != null ? "; raiding " + v.Name : "") + ")");
                            }
                        }
                    }
                    catch (Exception ex) { Log("DriveHoldout party threw " + ex.Message); }
                }
            }
            catch (Exception ex) { Log("DriveHoldout threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Nearest town/castle of a realm the holdout is AT WAR with (kingdoms only). The IsAtWarWith gate plus the
        // bandit-peace fix means bandit and minor factions are never targeted — the King never marches on allies.
        private Settlement PickDriveTarget(Clan holdout)
        {
            try
            {
                Settlement from = (holdout.Settlements != null && holdout.Settlements.Count > 0) ? holdout.Settlements[0] : null;
                if (from == null) return null;
                Settlement best = null; float bestSq = float.MaxValue;
                foreach (var s in Settlement.All)
                {
                    if (s == null || !(s.IsTown || s.IsCastle)) continue;
                    if (s.MapFaction == null || s.MapFaction.IsBanditFaction) continue;
                    if (s.OwnerClan == holdout) continue;
                    // FIX 5: spare the bandit-allied player's whole REALM (kingdom), not just their clan's own fiefs —
                    // the alliance is declared/spared at kingdom granularity, so fellow-vassal fiefs must be spared too.
                    if (PlayerIsBanditAllied() && Clan.PlayerClan != null &&
                        (s.OwnerClan == Clan.PlayerClan || (Clan.PlayerClan.Kingdom != null && s.MapFaction == Clan.PlayerClan.Kingdom)))
                        continue;
                    if (s.SiegeEvent != null) continue;
                    if (!holdout.IsAtWarWith(s.MapFaction)) continue; // only fiefs of realms he is actually at war with
                    float dx = s.Position.X - from.Position.X, dy = s.Position.Y - from.Position.Y;
                    float d = dx * dx + dy * dy;
                    if (d < bestSq) { bestSq = d; best = s; }
                }
                return best;
            }
            catch (Exception ex) { Log("PickDriveTarget threw " + ex.GetType().Name + ": " + ex.Message); return null; }
        }

        // Same as PickDriveTarget but skips any fief already CLAIMED by another holdout lord this tick, so the King
        // and each lieutenant pick DIFFERENT targets instead of all piling onto the nearest enemy fief.
        // 2026-07-02 WARLORD BRAIN (A1): with MadKingCautiousAI on, the pick is SCORED instead of nearest-only —
        // resistance = defenseNeed (garrison-scaled ComputeArmyTarget) + hostile field strength camped near the
        // candidate, scaled up with distance from the clan seat, with a strong bonus (x0.35) for fiefs of the
        // current PREY kingdom (diplomacy brain). Toggle off = the original nearest-only behaviour, unchanged.
        private Settlement PickDriveTargetExcluding(Clan holdout, System.Collections.Generic.HashSet<string> claimed)
        {
            try
            {
                Settlement from = (holdout.Settlements != null && holdout.Settlements.Count > 0) ? holdout.Settlements[0] : null;
                if (from == null) return null;
                bool cautious = false;
                try { cautious = MCMSettings.Instance != null && MCMSettings.Instance.MadKingCautiousAI; } catch { }
                System.Collections.Generic.List<(float x, float y, float str)> hostiles = null;
                Kingdom prey = null;
                if (cautious)
                {
                    hostiles = SnapshotHostileFieldParties(holdout, null);
                    if (!string.IsNullOrEmpty(_preyKingdomId))
                        try { foreach (var k in Kingdom.All) if (k != null && k.StringId == _preyKingdomId) { prey = k; break; } } catch { }
                }
                Settlement best = null; float bestScore = float.MaxValue;
                foreach (var s in Settlement.All)
                {
                    if (s == null || !(s.IsTown || s.IsCastle)) continue;
                    if (s.MapFaction == null || s.MapFaction.IsBanditFaction) continue;
                    if (s.OwnerClan == holdout) continue;
                    // FIX 5: spare the bandit-allied player's whole REALM (kingdom), not just their clan's own fiefs.
                    if (PlayerIsBanditAllied() && Clan.PlayerClan != null &&
                        (s.OwnerClan == Clan.PlayerClan || (Clan.PlayerClan.Kingdom != null && s.MapFaction == Clan.PlayerClan.Kingdom)))
                        continue;
                    if (s.SiegeEvent != null) continue;
                    if (!holdout.IsAtWarWith(s.MapFaction)) continue; // only fiefs of realms he is actually at war with
                    if (claimed != null && s.StringId != null && claimed.Contains(s.StringId)) continue; // another lord already took it
                    float dx = s.Position.X - from.Position.X, dy = s.Position.Y - from.Position.Y;
                    float d = dx * dx + dy * dy;
                    float score;
                    if (!cautious) score = d; // legacy: squared distance, nearest wins
                    else
                    {
                        float dist = (float)System.Math.Sqrt(d);
                        float defenseNeed = BanditRebelSiegeHelper.ComputeArmyTarget(s); // garrison-scaled need to crack it
                        float fieldStr, strongest;
                        SumFieldStrengthNear(hostiles, s.Position.X, s.Position.Y, NearbyEnemyRadius, out fieldStr, out strongest);
                        float resistance = defenseNeed + fieldStr;
                        score = resistance * (1f + dist / 100f);
                        try { if (prey != null && object.ReferenceEquals(s.MapFaction, prey)) score *= 0.35f; } catch { } // press the PREY's fiefs first
                    }
                    if (score < bestScore) { bestScore = score; best = s; }
                }
                return best;
            }
            catch (Exception ex) { Log("PickDriveTargetExcluding threw " + ex.GetType().Name + ": " + ex.Message); return null; }
        }

        // Nearest raidable enemy VILLAGE (kingdoms only — never bandit/own; only realms he is at war with).
        private Settlement PickRaidTarget(Clan holdout)
        {
            try
            {
                Settlement from = (holdout.Settlements != null && holdout.Settlements.Count > 0) ? holdout.Settlements[0] : null;
                if (from == null) return null;
                Settlement best = null; float bestSq = float.MaxValue;
                foreach (var s in Settlement.All)
                {
                    if (s == null || !s.IsVillage) continue;
                    if (s.MapFaction == null || s.MapFaction.IsBanditFaction) continue;
                    if (s.OwnerClan == holdout) continue;
                    // FIX 5: spare the bandit-allied player's whole REALM (kingdom), not just their clan's own villages.
                    if (PlayerIsBanditAllied() && Clan.PlayerClan != null &&
                        (s.OwnerClan == Clan.PlayerClan || (Clan.PlayerClan.Kingdom != null && s.MapFaction == Clan.PlayerClan.Kingdom)))
                        continue;
                    if (!holdout.IsAtWarWith(s.MapFaction)) continue;
                    try { if (s.Village != null && s.Village.VillageState != Village.VillageStates.Normal) continue; } catch { } // skip already-raided
                    float dx = s.Position.X - from.Position.X, dy = s.Position.Y - from.Position.Y;
                    float d = dx * dx + dy * dy;
                    if (d < bestSq) { bestSq = d; best = s; }
                }
                return best;
            }
            catch (Exception ex) { Log("PickRaidTarget threw " + ex.GetType().Name + ": " + ex.Message); return null; }
        }

        // ==================== 2026-07-02 WARLORD BRAIN helpers ====================

        // Party strength read. NOTE: this War Sails build (1.4.5/1.4.6) exposes
        // PartyBase.EstimatedStrength — the renamed TotalStrength (verified against the shipped
        // DLL: no TotalStrength member exists anywhere on PartyBase/MobileParty in this version).
        private static float PartyStrength(MobileParty mp)
        {
            try { return (mp != null && mp.Party != null) ? mp.Party.EstimatedStrength : 0f; } catch { return 0f; }
        }

        // Kingdom strength. Same rename story: this build's API is Kingdom.CurrentTotalStrength
        // (the spec's Kingdom.TotalStrength does not exist in the shipped 1.4.5/1.4.6 DLL).
        private static float KingdomStrength(Kingdom k)
        {
            try { return k != null ? k.CurrentTotalStrength : 0f; } catch { return 0f; }
        }

        // A) one snapshot of every hostile ENEMY-KINGDOM lord party currently in the FIELD (not
        // garrisoned/inside walls): (x, y, strength). Built once per daily sweep and shared by the
        // scored target pick, the attack gate, and the siege-lift check.
        private System.Collections.Generic.List<(float x, float y, float str)> SnapshotHostileFieldParties(Clan holdout, MobileParty exclude)
        {
            var list = new System.Collections.Generic.List<(float x, float y, float str)>();
            try
            {
                if (holdout == null) return list;
                foreach (var mp in MobileParty.All)
                {
                    if (mp == null || !mp.IsActive || mp == exclude) continue;
                    bool lord = false; try { lord = mp.IsLordParty; } catch { }
                    if (!lord) continue;
                    try { if (mp.CurrentSettlement != null) continue; } catch { } // not a FIELD army
                    IFaction f = null; try { f = mp.MapFaction; } catch { }
                    if (f == null || f.IsBanditFaction) continue;
                    bool atWar = false; try { atWar = holdout.IsAtWarWith(f); } catch { }
                    if (!atWar) continue;
                    float str = PartyStrength(mp);
                    if (str <= 0f) continue;
                    try { list.Add((mp.Position.X, mp.Position.Y, str)); } catch { }
                }
            }
            catch (Exception ex) { Log("SnapshotHostileFieldParties threw " + ex.GetType().Name + ": " + ex.Message); }
            return list;
        }

        // A) total + strongest-single hostile field strength within `radius` map units of (x, y).
        private static void SumFieldStrengthNear(System.Collections.Generic.List<(float x, float y, float str)> snap, float x, float y, float radius, out float sum, out float strongest)
        {
            sum = 0f; strongest = 0f;
            try
            {
                if (snap == null) return;
                float r2 = radius * radius;
                for (int i = 0; i < snap.Count; i++)
                {
                    float dx = snap[i].x - x, dy = snap[i].y - y;
                    if (dx * dx + dy * dy > r2) continue;
                    sum += snap[i].str;
                    if (snap[i].str > strongest) strongest = snap[i].str;
                }
            }
            catch { }
        }

        // A3) nearest owned town/castle to a lifting party — its fallback rally point.
        private Settlement NearestOwnedFief(MobileParty lp)
        {
            try
            {
                if (_holdoutClan == null || _holdoutClan.Settlements == null || lp == null) return null;
                Settlement best = null; float bestSq = float.MaxValue;
                foreach (var s in _holdoutClan.Settlements)
                {
                    if (s == null || !(s.IsTown || s.IsCastle)) continue;
                    float dx = s.Position.X - lp.Position.X, dy = s.Position.Y - lp.Position.Y;
                    float d = dx * dx + dy * dy;
                    if (d < bestSq) { bestSq = d; best = s; }
                }
                return best;
            }
            catch (Exception ex) { Log("NearestOwnedFief threw " + ex.GetType().Name + ": " + ex.Message); return null; }
        }

        // C) holdout war strength = sum of the clan's lord-party strengths + owned-fief garrison
        // strengths. De-duped by party id (the WarPartyComponents dupe corruption would otherwise
        // double-count — see the drive loop's dupe guard).
        private float ComputeHoldoutStrength()
        {
            float total = 0f;
            try
            {
                if (_holdoutClan == null) return 0f;
                var seen = new System.Collections.Generic.HashSet<string>();
                try
                {
                    foreach (var wpc in _holdoutClan.WarPartyComponents)
                    {
                        var mp = wpc != null ? wpc.MobileParty : null;
                        if (mp == null || !mp.IsActive) continue;
                        if (!seen.Add(mp.StringId ?? ("hash:" + mp.GetHashCode()))) continue;
                        total += PartyStrength(mp);
                    }
                }
                catch { }
                try
                {
                    if (_holdoutClan.Settlements != null)
                        foreach (var s in _holdoutClan.Settlements)
                        {
                            var gp = (s != null && s.Town != null) ? s.Town.GarrisonParty : null;
                            if (gp != null && gp.IsActive) total += PartyStrength(gp);
                        }
                }
                catch { }
            }
            catch (Exception ex) { Log("ComputeHoldoutStrength threw " + ex.GetType().Name + ": " + ex.Message); }
            return total;
        }

        // ==================== B) FULL LORDSHIP (gate: MadKingDevelopsFiefs) ====================
        // Once per campaign day (drive cadence): the King invests his REAL gold into every owned
        // fief — civic stats climb toward healthy targets (clamped, never reduced), the garrison
        // trickles up to strength, construction is boosted, a governor is seated, and bound
        // villages regrow hearths. Skipped entirely while the leader's gold is below the 50k war
        // reserve. One compact log line per fief per day.
        private void DevelopHoldoutFiefs()
        {
            try
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null || !mcm.MadKingDevelopsFiefs) return;
                if (_holdoutClan == null || _holdoutClan.IsEliminated || _holdoutClan.Settlements == null) return;
                Hero lead = null; try { lead = _holdoutClan.Leader; } catch { }
                if (lead == null || !lead.IsAlive) return;
                int gold = 0; try { gold = lead.Gold; } catch { }
                if (gold < 50000) return; // war-reserve floor: development pauses before the war chest starves

                foreach (var s in _holdoutClan.Settlements)
                {
                    try
                    {
                        // verifier hardening: with many fiefs a single day's spend can cross the
                        // reserve floor mid-loop — stop investing the moment it does.
                        try { if (lead.Gold < 50000) break; } catch { }
                        if (s == null || s.Town == null || !(s.IsTown || s.IsCastle)) continue;
                        var town = s.Town;
                        int cost = 0;
                        var parts = new System.Text.StringBuilder();

                        // Civic stats: small daily climbs toward targets, clamped, never reduced.
                        // Setters proven writable by the blockade code (BanditBlockadeBehavior).
                        try { if (town.Prosperity < 6000f) { town.Prosperity = System.Math.Min(6000f, town.Prosperity + 2f); cost += 50; parts.Append(" prosp+2"); } } catch { }
                        try { if (town.Loyalty < 75f) { town.Loyalty = System.Math.Min(75f, town.Loyalty + 1f); cost += 25; parts.Append(" loy+1"); } } catch { }
                        try { if (town.Security < 80f) { town.Security = System.Math.Min(80f, town.Security + 1f); cost += 25; parts.Append(" sec+1"); } } catch { }
                        try { if (town.FoodStocks < 400f) { town.FoodStocks = System.Math.Min(400f, town.FoodStocks + 10f); cost += 50; parts.Append(" food+10"); } } catch { }

                        // Garrison trickle: ~4 fief-culture troops/day toward 300 (town) / 200 (castle), 40g/head.
                        try
                        {
                            int gTarget = s.IsTown ? 300 : 200;
                            int added = BanditRebelSiegeHelper.GarrisonTrickle(s, gTarget, 4);
                            if (added > 0) { cost += added * 40; parts.Append(" garrison+" + added); }
                        }
                        catch (Exception eG) { Log("develop garrison threw " + eG.GetType().Name + ": " + eG.Message); }

                        // Building funding: reflection-guarded boost of the vanilla construction pathway —
                        // the daily construction tick consumes the public Town.BoostBuildingProcess int.
                        // (BuildingHelper.BoostBuildingProcessWithGold is deliberately NOT used: its IL
                        // charges Hero.MainHero — the PLAYER.) API mismatch skips silently, once-logged.
                        try
                        {
                            bool building = false; try { building = town.CurrentBuilding != null; } catch { }
                            if (building)
                            {
                                if (!_boostFieldProbed)
                                {
                                    _boostFieldProbed = true;
                                    try { _boostField = typeof(Town).GetField("BoostBuildingProcess", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic); } catch { _boostField = null; }
                                }
                                if (_boostField != null && _boostField.FieldType == typeof(int))
                                {
                                    _boostField.SetValue(town, (int)_boostField.GetValue(town) + 200);
                                    cost += 200; parts.Append(" build+200g");
                                }
                                else if (!_boostApiMissLogged)
                                {
                                    _boostApiMissLogged = true;
                                    Log("develop: Town.BoostBuildingProcess not found — building boost skipped (logged once)");
                                }
                            }
                        }
                        catch (Exception eB) { if (!_boostApiMissLogged) { _boostApiMissLogged = true; Log("develop building boost threw " + eB.GetType().Name + ": " + eB.Message + " (logged once)"); } }

                        // Governor: seat a FREE, un-partied clan hero (reflection-guarded ChangeGovernorAction).
                        try
                        {
                            Hero curGov = null; try { curGov = town.Governor; } catch { }
                            if (curGov == null && _holdoutClan.Heroes != null)
                            {
                                Hero gov = null;
                                foreach (var h in _holdoutClan.Heroes)
                                    if (h != null && h.IsAlive && !h.IsPrisoner && h != lead && h.PartyBelongedTo == null && h.GovernorOf == null) { gov = h; break; }
                                if (gov != null && BanditRebelSiegeHelper.SeatGovernor(town, gov))
                                    parts.Append(" governor=" + (gov.Name != null ? gov.Name.ToString() : "?"));
                            }
                        }
                        catch (Exception eGov) { Log("develop governor threw " + eGov.GetType().Name + ": " + eGov.Message); }

                        // Bound villages: hearth +1.5/day toward 400 — only while alive (not raided/looted).
                        try
                        {
                            if (s.BoundVillages != null)
                                foreach (var v in s.BoundVillages)
                                {
                                    if (v == null) continue;
                                    try
                                    {
                                        if (v.VillageState != Village.VillageStates.Normal) continue;
                                        if (v.Hearth < 400f) v.Hearth = System.Math.Min(400f, v.Hearth + 1.5f);
                                    }
                                    catch { }
                                }
                        }
                        catch { }

                        // Real-gold sink for everything bought today (proven idiom: HireFromTheWilds).
                        if (cost > 0)
                        {
                            try { GiveGoldAction.ApplyBetweenCharacters(lead, null, cost, true); }
                            catch (Exception eGold) { Log("develop gold sink threw " + eGold.GetType().Name + ": " + eGold.Message); }
                        }
                        if (parts.Length > 0)
                            Log("invests: " + (s.Name != null ? s.Name.ToString() : s.StringId) + parts + " (" + cost + "g)");
                    }
                    catch (Exception exF) { Log("DevelopHoldoutFiefs fief threw " + exF.GetType().Name + ": " + exF.Message); }
                }
            }
            catch (Exception ex) { Log("DevelopHoldoutFiefs threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // ==================== C) DIPLOMACY BRAIN (gate: MadKingDiplomacy) ====================
        // The total-war OPENING (Rises panel, oath, spare-the-ally logic) is untouched — this is
        // the DAILY step afterward: keep ONE prey kingdom under pressure, truce out of hopeless
        // wars (2.5x stronger, never the prey), cap concurrent kingdom wars at 3, and re-declare
        // on the most exposed truced kingdom once strength recovers. ABSOLUTE player exemption:
        // any faction the player belongs to is governed exclusively by the existing
        // Offer/spare/refuse flow and is never touched here. All state non-saved (_preyKingdomId
        // recomputes from the live war/peace map after a reload).
        private void RunDiplomacyBrain()
        {
            try
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null || !mcm.MadKingDiplomacy) return;
                if (!_wentToWar) return; // the scripted opening owns diplomacy until THE TURN has fired
                if (_holdoutClan == null || _holdoutClan.IsEliminated) return;

                IFaction playerFaction = null;
                try { playerFaction = Clan.PlayerClan != null ? Clan.PlayerClan.MapFaction : null; } catch { }
                Kingdom playerKingdom = null;
                try { playerKingdom = Clan.PlayerClan != null ? Clan.PlayerClan.Kingdom : null; } catch { }

                float holdoutStrength = ComputeHoldoutStrength();
                Settlement seat = (_holdoutClan.Settlements != null && _holdoutClan.Settlements.Count > 0) ? _holdoutClan.Settlements[0] : null;

                // Live kingdom stances — player-protected kingdoms are excluded from EVERY step.
                var enemies = new System.Collections.Generic.List<Kingdom>();
                var truced = new System.Collections.Generic.List<Kingdom>();
                foreach (var k in Kingdom.All)
                {
                    if (k == null || k.IsEliminated || k.IsBanditFaction) continue;
                    if (playerFaction != null && (IFaction)k == playerFaction) continue; // ABSOLUTE player exemption
                    if (playerKingdom != null && k == playerKingdom) continue;
                    bool atWar = false; try { atWar = _holdoutClan.IsAtWarWith(k); } catch { }
                    if (atWar) enemies.Add(k); else truced.Add(k);
                }

                // C1) PREY: keep one — the weakest enemy kingdom weighted by distance from the clan
                // seat. Recompute when the current prey is gone (eliminated / truced / protected).
                Kingdom prey = null;
                if (!string.IsNullOrEmpty(_preyKingdomId))
                    foreach (var k in enemies) if (k.StringId == _preyKingdomId) { prey = k; break; }
                if (prey == null && enemies.Count > 0)
                {
                    prey = PickPreyKingdom(enemies, seat);
                    _preyKingdomId = prey != null ? (prey.StringId ?? "") : "";
                    if (prey != null) Log("diplomacy: prey=" + prey.Name);
                }

                // C2) SUE FOR PEACE with overwhelming non-prey enemies (> 2.5x the holdout's strength).
                foreach (var k in new System.Collections.Generic.List<Kingdom>(enemies))
                {
                    if (k == prey) continue;
                    float ks = KingdomStrength(k);
                    if (holdoutStrength > 0f && ks > 2.5f * holdoutStrength)
                        if (TruceWith(k, "outmatched " + (int)ks + " vs " + (int)holdoutStrength)) { enemies.Remove(k); truced.Add(k); }
                }

                // C3) CAP concurrent kingdom wars at 3 — truce the STRONGEST extras (never the prey).
                while (enemies.Count > 3)
                {
                    Kingdom strongest = null; float bestS = -1f;
                    foreach (var k in enemies)
                    {
                        if (k == prey) continue;
                        float ks = KingdomStrength(k);
                        if (ks > bestS) { bestS = ks; strongest = k; }
                    }
                    if (strongest == null) break;
                    enemies.Remove(strongest); // remove regardless so a failed invoke can't loop forever
                    if (TruceWith(strongest, "war cap (3)")) truced.Add(strongest);
                }

                // C4) RE-DECLARE once strength recovers past the prey (1.5x) or the prey is gone —
                // most exposed truced kingdom becomes the new prey, via the existing DeclareWarAction
                // path. war_declared sting + toast, NOT a full Rises panel.
                float preyStrength = prey != null ? KingdomStrength(prey) : 0f;
                bool recovered = prey == null || preyStrength <= 0f || holdoutStrength > 1.5f * preyStrength;
                if (recovered && enemies.Count < 3 && truced.Count > 0)
                {
                    // FINDING-4 fix (truce hysteresis): never re-declare on a kingdom truced less
                    // than 14 days ago, and never on one that still outclasses the holdout 2x —
                    // otherwise C2's truce and C4's redeclare could fire the SAME day and lock the
                    // clan straight back into the hopeless war the truce just escaped.
                    double nowHours = CampaignTime.Now.ToHours;
                    var eligible = new System.Collections.Generic.List<Kingdom>();
                    foreach (var kT in truced)
                    {
                        if (kT == null) continue;
                        double trucedAt;
                        if (kT.StringId != null && _truceAtHours.TryGetValue(kT.StringId, out trucedAt) && nowHours - trucedAt < 336.0) continue;
                        if (holdoutStrength > 0f && KingdomStrength(kT) >= 2f * holdoutStrength) continue;
                        eligible.Add(kT);
                    }
                    var next = eligible.Count > 0 ? PickPreyKingdom(eligible, seat) : null;
                    if (next != null && BanditRebelSiegeHelper.DeclareWarOn(_holdoutClan, next))
                    {
                        _preyKingdomId = next.StringId ?? "";
                        Log("war REDECLARED on " + next.Name + " (strength recovered)");
                        try { BandItPlus.UI.BanditToastScreen.Show(BandItPlus.Localization.Get("bp_siegebehavior_001", "The Mad King Marches Again"), new TextObject("{=bp_hcr_015}Vaykos turns his gaze upon {KINGDOM}. The truce is ash.").SetTextVariable("KINGDOM", next.Name).ToString(), "event:/ui/notification/war_declared"); } catch { }
                    }
                }
            }
            catch (Exception ex) { Log("RunDiplomacyBrain threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // C) truce with a kingdom (reflection-tolerant MakePeaceAction). Clears the prey slot if
        // it was the prey. Toast + log on success.
        private bool TruceWith(Kingdom k, string why)
        {
            try
            {
                if (k == null) return false;
                if (!BanditRebelSiegeHelper.MakePeaceWith(_holdoutClan, k)) return false;
                if (_preyKingdomId == k.StringId) _preyKingdomId = "";
                // FINDING-4 fix: stamp the truce time for C4's 14-day re-declare hysteresis.
                try { if (k.StringId != null) _truceAtHours[k.StringId] = CampaignTime.Now.ToHours; } catch { }
                Log("truce with " + k.Name + " — for now (" + why + ")");
                try { BandItPlus.UI.BanditToastScreen.Show(BandItPlus.Localization.Get("bp_siegebehavior_002", "A Truce — For Now"), new TextObject("{=bp_hcr_016}Vaykos suspends his war with {KINGDOM}. The wolves remember.").SetTextVariable("KINGDOM", k.Name).ToString(), "event:/ui/notification/kingdom_decision"); } catch { }
                return true;
            }
            catch (Exception ex) { Log("TruceWith threw " + ex.GetType().Name + ": " + ex.Message); return false; }
        }

        // C) the weakest kingdom in `pool` weighted by distance from the clan seat ("most exposed"):
        // score = strength * (1 + nearestFiefDistance/100), minimal score wins. Distance uses the
        // Settlement.Position idiom (CampaignVec2 — NOT Position2D).
        private Kingdom PickPreyKingdom(System.Collections.Generic.List<Kingdom> pool, Settlement seat)
        {
            Kingdom best = null; float bestScore = float.MaxValue;
            try
            {
                if (pool == null) return null;
                foreach (var k in pool)
                {
                    try
                    {
                        if (k == null || k.IsEliminated) continue;
                        float ks = KingdomStrength(k);
                        float dist = 200f;
                        if (seat != null && k.Settlements != null)
                        {
                            float bestSq = float.MaxValue;
                            foreach (var s in k.Settlements)
                            {
                                if (s == null) continue;
                                float dx = s.Position.X - seat.Position.X, dy = s.Position.Y - seat.Position.Y;
                                float d = dx * dx + dy * dy;
                                if (d < bestSq) bestSq = d;
                            }
                            if (bestSq < float.MaxValue) dist = (float)System.Math.Sqrt(bestSq);
                        }
                        float score = System.Math.Max(1f, ks) * (1f + dist / 100f);
                        if (score < bestScore) { bestScore = score; best = k; }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log("PickPreyKingdom threw " + ex.GetType().Name + ": " + ex.Message); }
            return best;
        }

        private static void Log(string msg)
        {
            try { HideoutPeacefulVisitState.Log("[BP-Siege] " + msg); } catch { }
        }
    }
}
