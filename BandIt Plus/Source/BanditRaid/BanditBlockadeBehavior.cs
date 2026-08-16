using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using BandItPlus.HideoutVisit;

namespace BandItPlus.BanditRaid
{
    // Wave 4.x — atmospheric bandit BLOCKADE of towns/castles. Ephemeral (non-serialized): in-flight
    // blockades are released on session load (save-safe), exactly like BanditRaidBehavior. See spec
    // docs/superpowers/specs/2026-06-28-bandit-blockade-design.md + plan 2026-06-28-bandit-blockade.md.
    public class BanditBlockadeBehavior : CampaignBehaviorBase
    {
        private sealed class BlockadeContext
        {
            public Settlement Target;
            public string CultureId;
            public Settlement Hideout;
            public double StartHours;   // when the warband began the blockade (-1 = still marching)
            public bool Started;        // arrived + draining
            public double LastLoggedDist = -1.0; // throttle for the marching diagnostic (only log real progress)
            public bool WarnedBrink;    // one-time "brink of rebellion" popup latch
        }

        private readonly Dictionary<MobileParty, BlockadeContext> _active = new Dictionary<MobileParty, BlockadeContext>();
        private readonly Dictionary<Settlement, double> _cooldownUntilHours = new Dictionary<Settlement, double>();
        private readonly Dictionary<string, double> _lastBlockadeDayByCulture = new Dictionary<string, double>();
        private double _escalBaseline = -1.0;
        private readonly List<MobileParty> _deferredDestroy = new List<MobileParty>(); // warbands to remove AFTER the tick loop (destroying mid-loop mutates _active)

        // 2026-07-01 (Bandit-King GUI, STEP C5) — CRUSHED detector. Tracked set of settlement
        // StringIds currently held by a bandit/rebel holdout (bandit-faction OR bp_rebel_clan_
        // prefix). Rebuilt each daily scan; when a previously-tracked settlement returns to a
        // NORMAL kingdom clan, the uprising was crushed → fire ForCrushed. Ephemeral (rebuilt on
        // load, so a reload never false-fires; a genuine retake across a save is simply missed).
        private readonly Dictionary<string, string> _banditHeldSettlements = new Dictionary<string, string>(); // settlementId -> holder clanId
        private double _lastCrushScanDays = -1.0;

        public override void RegisterEvents()
        {
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, OnMobilePartyDestroyed);
        }

        public override void SyncData(IDataStore dataStore) { /* ephemeral — nothing persisted */ }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            _active.Clear(); // release any in-flight blockades on load (save-safe)
            _deferredDestroy.Clear();
            _banditHeldSettlements.Clear(); // rebuilt on the first daily crush-scan (no false retake-fire across a reload)
            _lastCrushScanDays = -1.0;
            BanditRebellionTrigger.WarmUp(); // arm + confirm the rebellion reflection up-front
            int repaired = BanditSeizeHelper.RepairPoisonedClanCultures(); // self-heal saves poisoned by the old culture-set seize
            if (repaired > 0) Log("repaired " + repaired + " poisoned clan culture(s) from a prior build");
            Log("blockade behavior ready");
        }

        private const double DAYS_PER_GAME_YEAR = 84.0;

        private void OnHourlyTick()
        {
            try
            {
                var mcm = MCMSettings.Instance;
                if (mcm == null || !mcm.EnableBanditBlockades) return;

                TickActiveBlockades();
                ScanForCrushedRebellions(); // STEP C5 — daily bandit/rebel-held → normal-clan retake detector

                double nowDays = CampaignTime.Now.ToDays;
                if (_escalBaseline < 0) _escalBaseline = nowDays;
                float years = (float)((nowDays - _escalBaseline) / DAYS_PER_GAME_YEAR);
                float baseChance = mcm.BlockadeBaseChancePerHour;
                float chance = Math.Min(baseChance * 6f, baseChance * (1f + years * 0.5f)); // rare, scales with campaign year
                if (MBRandom.RandomFloat > chance) return;

                TryStartBlockade();
            }
            catch (Exception ex) { Log("hourly fail: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // 2026-07-01 (Bandit-King GUI, STEP C5) — CRUSHED detector. Once/day, diff the set of
        // settlements held by a bandit/rebel holdout against last scan. A settlement that was
        // bandit/rebel-held and is now owned by a NORMAL kingdom clan → the uprising was crushed;
        // fire the premium ForCrushed panel with the retaking clan. Rebuild the tracked set at the
        // end so newly-seized settlements are watched going forward.
        //
        // LIMITATION: this is a coarse daily poll, not an owner-change event hook. A settlement
        // that is seized AND retaken within the same game-day is missed; a genuine retake that
        // straddles a save/reload is also missed (the set is rebuilt empty on load to avoid false
        // fires). This was the deliberate "minimal detector" choice — it never blocks the other 5.
        private void ScanForCrushedRebellions()
        {
            try
            {
                double nowDays = CampaignTime.Now.ToDays;
                if (_lastCrushScanDays >= 0 && (nowDays - _lastCrushScanDays) < 1.0) return; // once/day
                bool firstScan = _lastCrushScanDays < 0;
                _lastCrushScanDays = nowDays;

                var current = new Dictionary<string, string>(); // settlementId -> holder clanId (this scan)
                foreach (var s in Settlement.All)
                {
                    if (s == null || !s.IsActive || !(s.IsTown || s.IsCastle)) continue;
                    var owner = s.OwnerClan;
                    if (owner == null || s.StringId == null) continue;
                    if (IsBanditOrRebelHolder(owner))
                    {
                        current[s.StringId] = owner.StringId ?? "";
                    }
                    else if (!firstScan && _banditHeldSettlements.TryGetValue(s.StringId, out var formerHolderId))
                    {
                        // Was bandit/rebel-held last scan; now a normal kingdom clan owns it.
                        // FIX C (2026-07-01) — false-fire suppression. The Mad-King holdout is a
                        // MULTI-FIEF conqueror; losing ONE of his many fiefs USED to wrongly fire
                        // the "uprising scattered" finale while he kept marching. Only fire
                        // ForCrushed when the FORMER holder clan is genuinely finished: gone,
                        // eliminated, or holding zero towns+castles. A true one-fief rebellion
                        // clan losing its single town = 0 remaining = still fires correctly.
                        Clan former = ResolveClan(formerHolderId);
                        bool holderFinished =
                            former == null
                            || former.IsEliminated
                            || (RemainingFiefCount(former) == 0);

                        if (holderFinished)
                        {
                            try { BandItPlus.UI.BanditKingBriefManager.Show(
                                BandItPlus.UI.BanditKingBriefData.ForCrushed(s, owner)); }
                            catch (Exception e) { Log("crushed panel: " + e.Message); }
                            Log("rebellion CRUSHED at " + s.Name + " — retaken by " + (owner.Name != null ? owner.Name.ToString() : "?")
                                + " (former holder '" + (formerHolderId ?? "?") + "' finished)");
                        }
                        else
                        {
                            Log("crush SUPPRESSED at " + s.Name + " — former holder '" + (formerHolderId ?? "?")
                                + "' still holds " + RemainingFiefCount(former) + " fief(s); not the finale");
                        }
                    }
                }

                _banditHeldSettlements.Clear();
                foreach (var kv in current) _banditHeldSettlements[kv.Key] = kv.Value;
            }
            catch (Exception ex) { Log("crush-scan fail: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // A "bandit/rebel holdout" holder = a bandit-faction clan OR one of the mod's synthesized
        // rebel/Mad-King clans (StringId prefix bp_rebel_clan_).
        private static bool IsBanditOrRebelHolder(Clan clan)
        {
            try
            {
                if (clan == null) return false;
                if (clan.IsBanditFaction) return true;
                if (clan.StringId != null && clan.StringId.StartsWith("bp_rebel_clan_")) return true;
                return false;
            }
            catch { return false; }
        }

        // FIX C — resolve the FORMER holder clan tracked for a settlement by its StringId.
        // Returns null when the clan no longer exists in the campaign (already destroyed).
        private static Clan ResolveClan(string clanId)
        {
            try
            {
                if (string.IsNullOrEmpty(clanId)) return null;
                return Clan.FindFirst(c => c != null && c.StringId == clanId);
            }
            catch { return null; }
        }

        // FIX C — count the towns+castles a clan still owns. Used to distinguish a truly
        // finished holdout (0 fiefs) from a multi-fief Mad King who merely lost one fief.
        private static int RemainingFiefCount(Clan clan)
        {
            try
            {
                if (clan == null) return 0;
                int count = 0;
                var fiefs = clan.Fiefs; // Town list (towns + castles); villages are not Towns
                if (fiefs != null)
                {
                    foreach (var t in fiefs) { if (t != null) count++; }
                }
                return count;
            }
            catch { return 0; }
        }

        private void TryStartBlockade()
        {
            var mcm = MCMSettings.Instance;
            if (mcm != null && _active.Count >= mcm.BlockadeMaxConcurrent) return; // hard cap on simultaneous blockades
            double nowDays = CampaignTime.Now.ToDays;
            foreach (var hideout in Settlement.All)
            {
                if (hideout == null || !hideout.IsHideout || !hideout.IsActive) continue;
                var culture = hideout.Culture;
                if (culture == null) continue;
                string cid = culture.StringId;

                if (_lastBlockadeDayByCulture.TryGetValue(cid, out double last) && (nowDays - last) < 6.0) continue;
                if (HasActiveBlockadeForCulture(cid)) continue;

                Settlement target = PickTargetSettlement(hideout);
                if (target == null) continue;

                MobileParty warband = BanditWarbandFactory.SpawnWarband(culture, hideout);
                if (warband == null) continue;

                BandItPlus.Compat.PartyOrderCompat.GoToSettlement(warband, target);
                _active[warband] = new BlockadeContext { Target = target, CultureId = cid, Hideout = hideout, StartHours = -1, Started = false };
                _lastBlockadeDayByCulture[cid] = nowDays;
                Log("blockade dispatched: " + culture.Name + " -> " + target.Name);
                return; // one per tick
            }
        }

        private bool HasActiveBlockadeForCulture(string cid)
        {
            foreach (var kv in _active) if (kv.Value.CultureId == cid) return true;
            return false;
        }

        private void TickActiveBlockades()
        {
            var mcm = MCMSettings.Instance;
            double nowHours = CampaignTime.Now.ToHours;
            var done = new List<MobileParty>();

            foreach (var kv in _active)
            {
                MobileParty wb = kv.Key; BlockadeContext ctx = kv.Value;
                if (wb == null || !wb.IsActive || ctx.Target == null || !ctx.Target.IsActive) { done.Add(wb); continue; }

                // Abort if the target became bandit-owned or entered a real siege.
                if ((ctx.Target.MapFaction != null && ctx.Target.MapFaction.IsBanditFaction) || ctx.Target.SiegeEvent != null)
                { ReleaseWarband(wb, ctx); done.Add(wb); continue; }

                // Town rebelled (vanilla fired it on its own) — blockade succeeded. The rebel clan already owns
                // the town, so seize directly: reflavor + garrison the warband (deferred destroy), else withdraw.
                if (ctx.Target.Town != null && ctx.Target.Town.InRebelliousState)
                {
                    Notify(new TextObject("{=bp_hcr_001}The blockade has broken {TARGET} — it rises in rebellion!").SetTextVariable("TARGET", ctx.Target.Name).ToString());
                    Log("rebellion detected at " + ctx.Target.Name);
                    // 2026-07-01 data-accuracy fix: this is the VANILLA-fired rebellion path (any
                    // Calradia uprising the engine triggered on its own — not strictly bandit-driven).
                    // GATE it behind RebellionPanelsBanditOnly: by default (true) it stays quiet so only
                    // the bandit-driven forced path shows the panel; set false → all uprisings show it.
                    if (!(MCMSettings.Instance?.RebellionPanelsBanditOnly ?? true))
                    {
                        // Town is already InRebelliousState here, so vanilla has already reset loyalty —
                        // the real break value is not obtainable on this path. Pass -1f so ForRebellion
                        // OMITS the "Loyalty at break" row rather than showing the wrong (post-reset) number.
                        Clan formerOwner = null; // not tracked on the vanilla-fired path
                        try { BandItPlus.UI.BanditKingBriefManager.Show(
                            BandItPlus.UI.BanditKingBriefData.ForRebellion(ctx.Target, wb, formerOwner, -1f)); }
                        catch (Exception e) { Log("rebellion panel: " + e.Message); }
                    }
                    StampCooldown(ctx.Target);
                    bool seized = mcm.BlockadeCanCauseRebellion && mcm.BlockadeRebellionBanditSeize
                                  && BanditSeizeHelper.TrySeize(ctx.Target, ctx.CultureId, wb, null);
                    if (seized) _deferredDestroy.Add(wb); else SendWarbandHome(wb, ctx);
                    done.Add(wb);
                    continue;
                }

                float dx = wb.Position.X - ctx.Target.Position.X, dy = wb.Position.Y - ctx.Target.Position.Y;
                bool nearTarget = (dx * dx + dy * dy) <= 12.0f; // ~3.5 map units — park at the OUTSKIRTS, outside the settlement's encounter range

                if (!ctx.Started)
                {
                    if (!nearTarget)
                    {
                        // SetMoveGoToSettlement makes a bandit MICRO-OSCILLATE outside a town it can't enter
                        // (BanditRaidBehavior.cs:616 + fix19). Use a positional move (bypasses settlement-AI)
                        // and LOCK the AI so vanilla bandit decisions can't yank the warband back. Fall back to
                        // the settlement move only if SetMoveGoToPoint is missing on this engine version.
                        try { if (wb.Ai != null) wb.Ai.SetDoNotMakeNewDecisions(true); } catch { }
                        BanditWarbandFactory.ProtectFromAi(wb); // survive the approach — garrison/patrols would wipe a lone band
                        if (!BanditWarbandFactory.MoveToPoint(wb, new Vec2(ctx.Target.Position.X, ctx.Target.Position.Y)))
                        { try { BandItPlus.Compat.PartyOrderCompat.GoToSettlement(wb, ctx.Target); } catch { } }
                        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                        if (ctx.LastLoggedDist < 0 || Math.Abs(dist - ctx.LastLoggedDist) >= 0.5f) // throttle: only log real progress
                        {
                            Log("marching: " + wb.Name + " -> " + ctx.Target.Name + " dist=" + dist.ToString("0.0"));
                            ctx.LastLoggedDist = dist;
                        }
                        continue; // still marching
                    }
                    ctx.Started = true; ctx.StartHours = nowHours;
                    wb.SetMoveModeHold();
                    try { if (wb.Ai != null) wb.Ai.SetDoNotMakeNewDecisions(true); } catch { } // lock parked
                    BanditWarbandFactory.ProtectFromAi(wb);
                    Notify(new TextObject("{=bp_hcr_002}{TARGET} is being blockaded by {WARBAND}!").SetTextVariable("TARGET", ctx.Target.Name).SetTextVariable("WARBAND", wb.Name).ToString());
                    Log("blockade begun at " + ctx.Target.Name);
                    continue;
                }

                wb.SetMoveModeHold(); // re-assert each tick (vanilla AI clears it)
                try { if (wb.Ai != null) wb.Ai.SetDoNotMakeNewDecisions(true); } catch { }
                BanditWarbandFactory.ProtectFromAi(wb);

                float drain = mcm.BlockadeDrainPerHour;
                var town = ctx.Target.Town;
                if (town != null)
                {
                    if (ctx.Target.IsTown) town.Prosperity = Math.Max(0f, town.Prosperity - drain);
                    town.Security = Math.Max(0f, town.Security - drain);
                    if (ctx.Target.IsCastle) town.FoodStocks = Math.Max(0f, town.FoodStocks - drain);

                    // Loyalty is the rebellion lever (clamped 0–100). A one-time brink warning fires near the threshold.
                    if (mcm.BlockadeCanCauseRebellion)
                    {
                        town.Loyalty = Math.Max(0f, Math.Min(100f, town.Loyalty - mcm.BlockadeLoyaltyDrainPerHour));
                        if (!ctx.WarnedBrink && town.Loyalty <= mcm.BlockadeRebellionLoyaltyThreshold + 5f)
                        {
                            ctx.WarnedBrink = true;
                            Notify(new TextObject("{=bp_hcr_003}{TARGET} teeters on the brink of rebellion!").SetTextVariable("TARGET", ctx.Target.Name).ToString());
                        }
                    }
                }

                double daysBlockaded = (nowHours - ctx.StartHours) / 24.0;
                if (daysBlockaded >= mcm.BlockadeWindowDays) { Sack(wb, ctx); done.Add(wb); }
            }

            foreach (var wb in done) _active.Remove(wb);

            // Deferred party removal: DestroyPartyAction fires MobilePartyDestroyed → OnMobilePartyDestroyed
            // mutates _active. Never do that inside the foreach above — only here, once _active is stable.
            if (_deferredDestroy.Count > 0)
            {
                foreach (var p in _deferredDestroy)
                {
                    try { if (p != null && p.IsActive) TaleWorlds.CampaignSystem.Actions.DestroyPartyAction.Apply(null, p); }
                    catch (Exception ex) { Log("deferred destroy fail: " + ex.Message); }
                }
                _deferredDestroy.Clear();
            }
        }

        private void Notify(string text)
        {
            try { InformationManager.DisplayMessage(new InformationMessage(text)); } catch { }
        }

        private void OnMobilePartyDestroyed(MobileParty party, PartyBase destroyer)
        {
            if (party == null || !_active.TryGetValue(party, out var ctx)) return;
            _active.Remove(party);
            StampCooldown(ctx.Target);

            bool byPlayer = destroyer != null && destroyer.MobileParty == MobileParty.MainParty;
            if (byPlayer)
            {
                TaleWorlds.CampaignSystem.Actions.GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, 800, false);
                TaleWorlds.CampaignSystem.Actions.GainRenownAction.Apply(Hero.MainHero, 3f);
                var leader = ctx.Target?.MapFaction?.Leader;
                if (leader != null && leader != Hero.MainHero)
                    TaleWorlds.CampaignSystem.Actions.ChangeRelationAction.ApplyPlayerRelation(leader, 2);
                Notify(new TextObject("{=bp_hcr_004}You drove the bandits off {TARGET}!").SetTextVariable("TARGET", ctx.Target != null ? ctx.Target.Name.ToString() : BandItPlus.Localization.Get("bp_hcr_005", "the settlement")).ToString());
            }
            Log("blockade ended (warband destroyed, byPlayer=" + byPlayer + ")");
        }

        private void StampCooldown(Settlement s)
        {
            if (s == null) return;
            _cooldownUntilHours[s] = CampaignTime.Now.ToHours + 24.0 * 14.0; // 14-day cooldown
        }

        // Window expired with the blockade unbroken. If escalation is enabled and the town is eligible (not
        // player-owned, loyalty already crushed, not already rebelling), force a rebellion — the dramatic payoff.
        // Otherwise fall back to a one-time devastation hit. Either way the warband then withdraws.
        private void Sack(MobileParty wb, BlockadeContext ctx)
        {
            var mcm = MCMSettings.Instance;
            var town = ctx.Target?.Town;

            Clan oldOwner = ctx.Target?.OwnerClan;
            // 2026-07-01 data-accuracy fix: TryStartRebellion RESETS the town's loyalty to 100,
            // so capture the REAL break value HERE, strictly BEFORE that call. -1f if unavailable
            // → ForRebellion omits the "Loyalty at break" row rather than showing a wrong number.
            float loyaltyAtBreak = ctx.Target?.Town?.Loyalty ?? -1f;
            bool rebelled = false;
            if (mcm != null && mcm.BlockadeCanCauseRebellion && town != null
                && !town.InRebelliousState
                && !(mcm.BlockadeExcludePlayerOwned && ctx.Target.OwnerClan == Clan.PlayerClan)
                && town.Loyalty < mcm.BlockadeRebellionLoyaltyThreshold)
            {
                rebelled = BanditRebellionTrigger.TryStartRebellion(ctx.Target);
            }

            if (rebelled)
            {
                Notify(new TextObject("{=bp_hcr_001}The blockade has broken {TARGET} — it rises in rebellion!").SetTextVariable("TARGET", ctx.Target.Name).ToString());
                Log("blockade FORCED REBELLION at " + ctx.Target.Name + " (loyalty at break=" + loyaltyAtBreak + ")");
                // 2026-07-01 (Bandit-King GUI, STEP C3): premium ForRebellion panel. This is the
                // BANDIT-DRIVEN forced path — it ALWAYS fires (no toggle gate). Ownership flipped
                // synchronously, so OwnerClan is the rebel/uprising clan now; oldOwner is the former lord.
                // loyaltyAtBreak was captured above, BEFORE TryStartRebellion reset loyalty to 100.
                // Show() marshals to the main thread (Sack runs on the hourly tick). Guarded — never blocks the seize.
                try { BandItPlus.UI.BanditKingBriefManager.Show(
                    BandItPlus.UI.BanditKingBriefData.ForRebellion(ctx.Target, wb, oldOwner, loyaltyAtBreak)); }
                catch (Exception e) { Log("rebellion panel: " + e.Message); }
                // Seize the just-rebelled town: ownership flipped synchronously, so OwnerClan is the rebel clan now.
                bool seized = mcm.BlockadeRebellionBanditSeize
                              && BanditSeizeHelper.TrySeize(ctx.Target, ctx.CultureId, wb, oldOwner);
                StampCooldown(ctx.Target);
                if (seized) _deferredDestroy.Add(wb); else SendWarbandHome(wb, ctx);
                return;
            }

            // Devastation fallback (no rebellion).
            if (town != null)
            {
                if (ctx.Target.IsTown) town.Prosperity = Math.Max(0f, town.Prosperity - 200f);
                town.Security = Math.Max(0f, town.Security - 25f);
            }
            Notify(new TextObject("{=bp_hcr_006}{TARGET} was sacked by {WARBAND}!").SetTextVariable("TARGET", ctx.Target.Name).SetTextVariable("WARBAND", wb.Name).ToString());
            Log("blockade SACK at " + ctx.Target.Name);
            StampCooldown(ctx.Target);
            SendWarbandHome(wb, ctx);
        }

        private void ReleaseWarband(MobileParty wb, BlockadeContext ctx)
        {
            StampCooldown(ctx.Target);
            SendWarbandHome(wb, ctx);
            Log("blockade released at " + (ctx.Target != null ? ctx.Target.Name.ToString() : "?"));
        }

        private void SendWarbandHome(MobileParty wb, BlockadeContext ctx)
        {
            try
            {
                if (wb == null || !wb.IsActive) return;
                try { if (wb.Ai != null) wb.Ai.SetDoNotMakeNewDecisions(false); } catch { } // release the blockade lock
                Settlement home = ctx.Hideout ?? wb.HomeSettlement;
                if (home != null) BandItPlus.Compat.PartyOrderCompat.GoToSettlement(wb, home);
            }
            catch (Exception ex) { Log("send-home fail: " + ex.Message); }
        }

        // Nearest eligible town/castle to the hideout: not bandit-owned, off cooldown, within BlockadeMaxRange,
        // not already under a real siege. Mirrors PickTargetVillage's range shape (BanditRaidBehavior.cs:1062).
        private Settlement PickTargetSettlement(Settlement hideoutAnchor)
        {
            var mcm = MCMSettings.Instance;
            if (mcm == null) return null;
            float maxRangeSq = mcm.BlockadeMaxRange * mcm.BlockadeMaxRange;
            double nowHours = CampaignTime.Now.ToHours;

            var eligible = new System.Collections.Generic.List<Settlement>();       // pick a RANDOM in-range eligible fief (was: always the nearest)
            foreach (var s in Settlement.All)
            {
                if (s == null || !(s.IsTown || s.IsCastle)) continue;
                if (s.MapFaction != null && s.MapFaction.IsBanditFaction) continue;     // skip bandit-owned
                if (mcm.BlockadeExcludePlayerOwned && s.OwnerClan == Clan.PlayerClan) continue;
                if (s.SiegeEvent != null) continue;                                     // already contested
                if (_cooldownUntilHours.TryGetValue(s, out double until) && nowHours < until) continue;

                // Opportunistic: bandits only prey on weakly-garrisoned fiefs (never a 600-troop city).
                int garrison = (s.Town != null && s.Town.GarrisonParty != null && s.Town.GarrisonParty.MemberRoster != null)
                               ? s.Town.GarrisonParty.MemberRoster.TotalManCount : 0;
                if (garrison > mcm.BlockadeMaxGarrisonToTarget) continue;

                float dx = s.Position.X - hideoutAnchor.Position.X;
                float dy = s.Position.Y - hideoutAnchor.Position.Y;
                if (dx * dx + dy * dy > maxRangeSq) continue;

                eligible.Add(s);
            }
            if (eligible.Count == 0) return null;
            return eligible[MBRandom.RandomInt(eligible.Count)];
        }

        private static void Log(string msg)
        {
            try { HideoutPeacefulVisitState.Log("[BP-Blockade] " + msg); } catch { }
        }
    }
}
