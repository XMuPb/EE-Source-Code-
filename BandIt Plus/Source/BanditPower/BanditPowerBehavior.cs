using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using BandItPlus.HideoutVisit;

namespace BandItPlus.BanditPower
{
    // v158 (2026-05-16): "Bandit Power" — bandit/looter parties grow stronger
    // over the campaign instead of staying static cannon-fodder.
    //
    // Progression model: a 0..1 "power factor" ramps from campaign start over
    // MCM "Power ramp (years)". It drives the party-size cap and the rate /
    // quality of troop gain. Four ways parties gain troops:
    //   - passive growth   — daily chance to recruit a few men (OnDailyTickParty)
    //   - village recruit  — extra men when entering a village (OnSettlementEntered)
    //   - hideout hire     — bigger top-up when reaching a hideout
    //   - absorb defeated  — winners of a battle fold in extra men (OnMapEventEnded)
    // Plus tier upgrades: as the factor rises, roster troops upgrade along their
    // upgrade trees, so late-game bandits are veteran-heavy, not just numerous.
    //
    // v159 (2026-05-16): AI improvements — bandit parties also get bolder on the
    // map (Aggressiveness scaled by the power factor) and grow as a combined-arms
    // force (archers + cavalry, not an infantry blob) so the vanilla battle AI
    // can actually maneuver them. No TeamAI modding — the vanilla general AI is
    // competent; it just needs a real force to command.
    //
    // Size growth is gated by BanditPartySizeModel (registered alongside this) —
    // without it vanilla's PartySizeLimitModel would desert the extra men.
    // Everything is gated behind MCM EnableMod + EnableBanditPower.
    public class BanditPowerBehavior : CampaignBehaviorBase
    {
        // Campaign time (in CampaignTime hours-since-epoch) at which this campaign
        // first ran with Bandit Power. The engine exposes no campaign-start-time
        // property, so we anchor the progression ramp ourselves and persist it.
        private static double _startHours = -1.0;

        // Vanilla Aggressiveness captured once per party (boxed float, weak key)
        // so the factor-scaled boost is scale-agnostic and never compounds.
        private static readonly ConditionalWeakTable<MobileParty, object> _aggroBase =
            new ConditionalWeakTable<MobileParty, object>();
        private static bool _aggroLogged;

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            CampaignEvents.DailyTickPartyEvent.AddNonSerializedListener(this, OnDailyTickParty);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
            CampaignEvents.SettlementEntered.AddNonSerializedListener(this, OnSettlementEntered);
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("BanditPower_StartHours", ref _startHours);
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            // Fresh campaign, or first load of an existing save with this feature —
            // anchor the ramp to "now" so progression starts cleanly.
            if (_startHours < 0.0)
                _startHours = CampaignTime.Now.ToHours;
        }

        // ───────────────────────── progression ─────────────────────────

        // 0..1 ramp from campaign start over MCM "Power ramp (years)" in-game years.
        public static float PowerFactor()
        {
            try
            {
                if (Campaign.Current == null || _startHours < 0.0) return 0f;
                double hoursPerYear = CampaignTime.Years(1f).ToHours;
                if (hoursPerYear <= 0.0) return 0f;
                double years = (CampaignTime.Now.ToHours - _startHours) / hoursPerYear;
                double ramp = Math.Max(1, MCMSettings.Instance?.BanditPowerRampYears ?? 6);
                double f = years / ramp;
                return f <= 0 ? 0f : (f >= 1 ? 1f : (float)f);
            }
            catch { return 0f; }
        }

        // Effective party-size cap for a bandit party.
        // Read by BanditPartySizeModel (raises vanilla cap toward this value) and by
        // GrowParty() (uses it as the headroom ceiling).
        //
        // 2026-05-23 fix: returns the MCM value directly. The previous formula
        // `floor + (max - floor) * PowerFactor()` with hardcoded floor=10 produced
        // cap=10 at campaign start regardless of MCM setting, so GrowParty() never
        // fired for looters / small bandit parties and the user-visible result was
        // "the Max party size setting does nothing." The `BanditPowerRampYears`
        // setting still paces growth via factor-scaled gains in OnDailyTickParty /
        // OnSettlementEntered / OnMapEventEnded — that's where time progression
        // belongs, not on the ceiling itself.
        public static int CurrentPartySizeCap()
        {
            int max = MCMSettings.Instance?.BanditMaxPartySize ?? 60;
            return max < 10 ? 10 : max;
        }

        // ───────────────────────── event handlers ──────────────────────

        private void OnDailyTickParty(MobileParty party)
        {
            if (!Enabled() || !IsBandit(party)) return;
            float factor = PowerFactor();

            // Map aggression — bolder engage/flee behaviour as the campaign ages.
            ApplyAggression(party, factor);

            // Passive growth — daily chance scales slightly with progression.
            if (MBRandom.RandomFloat < 0.5f)
                GrowParty(party, MBRandom.RandomInt(1, 3 + (int)(factor * 4f)), "passive");

            // Tier upgrades — pure time-scaling: zero chance at campaign start,
            // ramping to 60% daily at full progression. Early-game bandits stay
            // vanilla-tier; upgrades are earned by campaign age alone.
            if (MBRandom.RandomFloat < factor * 0.6f)
                UpgradeTroops(party, MBRandom.RandomInt(1, 3));
        }

        private void OnSettlementEntered(MobileParty party, Settlement settlement, Hero hero)
        {
            if (!Enabled() || !IsBandit(party) || settlement == null) return;
            float factor = PowerFactor();

            if (settlement.IsVillage)
                GrowParty(party, MBRandom.RandomInt(2, 5 + (int)(factor * 6f)), "village");
            else if (settlement.IsHideout)
                GrowParty(party, MBRandom.RandomInt(4, 9 + (int)(factor * 10f)), "hideout");
        }

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            if (!Enabled() || mapEvent == null || !mapEvent.HasWinner) return;
            var winner = mapEvent.Winner;
            if (winner == null) return;
            float factor = PowerFactor();

            foreach (var mep in winner.Parties)
            {
                var mp = mep?.Party?.MobileParty;
                if (!IsBandit(mp)) continue;
                // Victors fold in stragglers / freed prisoners.
                GrowParty(mp, MBRandom.RandomInt(2, 6 + (int)(factor * 6f)), "absorb");
            }
        }

        // ───────────────────────── core helpers ────────────────────────

        private static bool Enabled()
        {
            var s = MCMSettings.Instance;
            return s != null && s.EnableMod && s.EnableBanditPower;
        }

        private static bool IsBandit(MobileParty p)
            => p != null && p.MapFaction != null && p.MapFaction.IsBanditFaction;

        // Adds `count` troops to a bandit party, clamped to its current cap.
        private static void GrowParty(MobileParty party, int count, string reason)
        {
            if (count <= 0) return;
            var roster = party?.MemberRoster;
            if (roster == null) return;

            int room = CurrentPartySizeCap() - roster.TotalManCount;
            if (room <= 0) return;
            if (count > room) count = room;

            var troop = PickCompositionTroop(party);
            if (troop == null) return;

            roster.AddToCounts(troop, count);
            LogOnce(reason, party, count, troop);
        }

        // ───────────────────────── map aggression ──────────────────────

        // Scale the party's engage/flee aggression by the power factor:
        // base × (1 + factor). The vanilla base is captured once (weak table)
        // so the boost is scale-agnostic and never compounds day to day.
        private static void ApplyAggression(MobileParty party, float factor)
        {
            try
            {
                if (!_aggroBase.TryGetValue(party, out var boxed))
                {
                    boxed = party.Aggressiveness;
                    _aggroBase.Add(party, boxed);
                }
                float baseAggro = (float)boxed;
                float target = baseAggro * (1f + factor);
                if (party.Aggressiveness < target)
                {
                    party.Aggressiveness = target;
                    if (!_aggroLogged)
                    {
                        _aggroLogged = true;
                        HideoutPeacefulVisitState.Log("v159 BanditPower [aggression] "
                            + party.StringId + " base=" + baseAggro.ToString("0.00")
                            + " -> " + target.ToString("0.00")
                            + " factor=" + factor.ToString("0.00"));
                    }
                }
            }
            catch { }
        }

        // ──────────────────────── composition ──────────────────────────

        // Class bucket: 0 = infantry, 1 = ranged, 2 = cavalry.
        private static int ClassOf(CharacterObject c)
        {
            if (c == null) return 0;
            if (c.IsRanged) return 1;
            if (c.IsMounted) return 2;
            return 0;
        }

        // Combined-arms troop pick. Builds the party's available troop pool
        // (roster troops + everything reachable through their upgrade trees),
        // buckets it by class, then returns the lowest-tier troop of the class
        // the party is most short of (target mix ≈ 55% inf / 30% ranged / 15%
        // cav). Self-limiting — a looter party has only infantry in its tree, so
        // it stays infantry; a forest-bandit tree branches into archers, giving
        // the vanilla battle AI a real combined-arms force to maneuver.
        private static CharacterObject PickCompositionTroop(MobileParty party)
        {
            var roster = party.MemberRoster;
            if (roster == null) return null;

            var pool = new List<CharacterObject>[3];
            for (int k = 0; k < 3; k++) pool[k] = new List<CharacterObject>();
            int[] current = new int[3];

            // DFS over the upgrade graph — explicit stack + visited-set so a
            // troop reachable by two upgrade paths is bucketed only once.
            var seen = new HashSet<CharacterObject>();
            var stack = new Stack<CharacterObject>();
            for (int i = 0; i < roster.Count; i++)
            {
                var c = roster.GetCharacterAtIndex(i);
                if (c == null || c.IsHero) continue;
                current[ClassOf(c)] += roster.GetElementNumber(i);
                stack.Push(c);
            }
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (c == null || c.IsHero || !seen.Add(c)) continue;
                pool[ClassOf(c)].Add(c);
                if (c.UpgradeTargets != null)
                    foreach (var u in c.UpgradeTargets)
                        if (u != null) stack.Push(u);
            }

            // Roster held only the leader — fall back to the culture basic troop.
            if (seen.Count == 0)
                return party.MapFaction?.Culture?.BasicTroop;

            float[] target = { 0.55f, 0.30f, 0.15f };
            int total = current[0] + current[1] + current[2];
            if (total < 1) total = 1;

            // Pick the class furthest below its target that the pool can supply.
            int bestClass = -1;
            float bestDeficit = float.MinValue;
            for (int k = 0; k < 3; k++)
            {
                if (pool[k].Count == 0) continue; // culture lacks this class
                float deficit = target[k] - (float)current[k] / total;
                if (deficit > bestDeficit) { bestDeficit = deficit; bestClass = k; }
            }
            if (bestClass < 0) return null;

            // Lowest tier of that class — raw material for later upgrades.
            CharacterObject pick = null;
            foreach (var c in pool[bestClass])
                if (pick == null || c.Tier < pick.Tier) pick = c;
            return pick;
        }

        // Upgrades a few roster troops one step along their upgrade tree.
        private static void UpgradeTroops(MobileParty party, int batches)
        {
            var roster = party?.MemberRoster;
            if (roster == null) return;

            for (int done = 0; done < batches; done++)
            {
                CharacterObject from = null;
                int available = 0;
                for (int i = 0; i < roster.Count; i++)
                {
                    var c = roster.GetCharacterAtIndex(i);
                    if (c == null || c.IsHero) continue;
                    if (c.UpgradeTargets == null || c.UpgradeTargets.Length == 0) continue;
                    int n = roster.GetElementNumber(i);
                    if (n <= 0) continue;
                    from = c;
                    available = n;
                    break;
                }
                if (from == null) return; // nothing left that can upgrade

                var to = from.UpgradeTargets[MBRandom.RandomInt(from.UpgradeTargets.Length)];
                if (to == null) return;

                int up = Math.Min(available, MBRandom.RandomInt(1, 4));
                roster.AddToCounts(from, -up);
                roster.AddToCounts(to, up);
                LogOnce("upgrade", party, up, to);
            }
        }

        // ───────────────────────── diagnostics ─────────────────────────

        // First occurrence of each mechanism logs once — enough to verify the
        // system fires without flooding the log every game-day.
        private static readonly HashSet<string> _loggedReasons = new HashSet<string>();

        private static void LogOnce(string reason, MobileParty party, int count, CharacterObject troop)
        {
            if (!_loggedReasons.Add(reason)) return;
            HideoutPeacefulVisitState.Log("v158 BanditPower [" + reason + "] +" + count + " "
                + (troop?.Name?.ToString() ?? "?") + " -> " + party.StringId
                + " (size=" + party.MemberRoster.TotalManCount
                + " cap=" + CurrentPartySizeCap()
                + " factor=" + PowerFactor().ToString("0.00") + ")");
        }
    }
}
