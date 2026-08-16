using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using BandItPlus.HideoutVisit;

namespace BandItPlus.BanditPower
{
    // v169 (2026-05-17): makes bandit hideouts tougher to assault. On session
    // launch and every weekly tick, sweeps Hideout.All and boosts each hideout's
    // bandit defender parties — extra troops (count) + tier upgrades (quality).
    //
    // Strength is the hybrid factor HideoutBossStrength (MCM, 0.5-2.5) * (1 +
    // ageFactor) where ageFactor is BanditPowerBehavior.PowerFactor() (0..1).
    // Default early campaign = 1.0x (vanilla); default late = 2.0x.
    //
    // Idempotent: per party we capture the vanilla baseline man-count once and
    // remember the last-applied factor, so weekly passes TOP UP toward the
    // current target rather than stacking. Count added is capped at +50% of
    // baseline so the player's size-capped assault squad is not swamped.
    public class HideoutDefenseBehavior : CampaignBehaviorBase
    {
        // party -> captured vanilla baseline TotalManCount (boxed int)
        private static readonly ConditionalWeakTable<MobileParty, object> _baseline
            = new ConditionalWeakTable<MobileParty, object>();
        // party -> last-applied defenseFactor (boxed float)
        private static readonly ConditionalWeakTable<MobileParty, object> _applied
            = new ConditionalWeakTable<MobileParty, object>();

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            CampaignEvents.WeeklyTickEvent.AddNonSerializedListener(this, OnWeeklyTick);
        }

        // No saved state — boosts are re-derived from the live roster each session.
        public override void SyncData(IDataStore dataStore) { }

        // v176: read-only accessor — the current hideout defense factor,
        // recomputed with the exact formula SweepHideouts() uses. The v176
        // Hideout Assault panel reads this for its "Reinforced Garrison +X%"
        // card. 1.0 = vanilla (no boost).
        public static double GetCurrentDefenseFactor()
        {
            try
            {
                var s = MCMSettings.Instance;
                if (s == null || !s.EnableMod) return 1.0;
                float age = BanditPowerBehavior.PowerFactor();   // 0..1
                return s.HideoutBossStrength * (1.0 + age);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v176 GetCurrentDefenseFactor fail: "
                    + ex.GetType().Name + ": " + ex.Message);
                return 1.0;
            }
        }

        private void OnSessionLaunched(CampaignGameStarter starter) => SweepHideouts();
        private void OnWeeklyTick() => SweepHideouts();

        private static void SweepHideouts()
        {
            try
            {
                var s = MCMSettings.Instance;
                if (s == null || !s.EnableMod) return;

                float age = BanditPowerBehavior.PowerFactor();           // 0..1
                double defenseFactor = s.HideoutBossStrength * (1.0 + age);

                int boostedParties = 0;
                foreach (var hideout in Hideout.All)
                {
                    var settlement = hideout?.Settlement;
                    if (settlement == null || !settlement.IsHideout) continue;
                    foreach (var party in settlement.Parties)
                    {
                        if (party == null || !party.IsBandit) continue;
                        if (BoostParty(party, defenseFactor)) boostedParties++;
                    }
                }
                if (boostedParties > 0)
                    HideoutPeacefulVisitState.Log("v169 HideoutDefense: boosted "
                        + boostedParties + " hideout party(ies) to factor "
                        + defenseFactor.ToString("0.00"));
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v169 HideoutDefense.SweepHideouts fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Returns true if this party received a (further) boost this pass.
        private static bool BoostParty(MobileParty party, double defenseFactor)
        {
            var roster = party.MemberRoster;
            if (roster == null) return false;

            // Capture the vanilla baseline man-count once, before any boost.
            if (!_baseline.TryGetValue(party, out var baseObj))
            {
                baseObj = roster.TotalManCount;
                _baseline.Add(party, baseObj);
            }
            int baseline = (int)baseObj;
            if (baseline <= 0) return false;

            float appliedFactor = _applied.TryGetValue(party, out var af) ? (float)af : 1.0f;
            if (defenseFactor <= appliedFactor + 0.05) return false; // already at target

            // --- count: top up toward target, capped at +50% of baseline ---
            int cap = (int)Math.Round(baseline * 0.5);
            int targetExtra = (int)Math.Round(baseline * (defenseFactor - 1.0));
            if (targetExtra > cap) targetExtra = cap;
            if (targetExtra < 0) targetExtra = 0;
            int alreadyExtra = (int)Math.Round(baseline * (appliedFactor - 1.0));
            if (alreadyExtra > cap) alreadyExtra = cap;
            if (alreadyExtra < 0) alreadyExtra = 0;
            int delta = targetExtra - alreadyExtra;
            if (delta > 0)
            {
                var reinforcement = TopTierTroop(roster);
                if (reinforcement != null) roster.AddToCounts(reinforcement, delta);
            }

            // --- quality: upgrade a fraction of the roster one tier step ---
            double upFrac = Clamp01((defenseFactor - 1.0) * 0.5);
            if (upFrac > 0) UpgradeFraction(roster, upFrac);

            _applied.Remove(party);
            _applied.Add(party, (float)defenseFactor);
            return true;
        }

        // The highest-Tier CharacterObject currently in the roster — added
        // reinforcements come in at this quality (this is the "stronger boss /
        // better troops" half of the boost).
        private static CharacterObject TopTierTroop(TroopRoster roster)
        {
            CharacterObject best = null;
            for (int i = 0; i < roster.Count; i++)
            {
                var ch = roster.GetElementCopyAtIndex(i).Character;
                if (ch == null || ch.IsHero) continue;
                if (best == null || ch.Tier > best.Tier) best = ch;
            }
            return best;
        }

        // Upgrades up to `fraction` of each upgradeable troop stack one step up
        // its troop tree (replace N of troop T with N of T's first UpgradeTarget).
        private static void UpgradeFraction(TroopRoster roster, double fraction)
        {
            // Snapshot first — we mutate the roster inside the loop.
            int count = roster.Count;
            var work = new System.Collections.Generic.List<TroopRosterElement>();
            for (int i = 0; i < count; i++)
                work.Add(roster.GetElementCopyAtIndex(i));

            foreach (var elem in work)
            {
                var troop = elem.Character;
                if (troop == null || troop.IsHero) continue;
                if (troop.UpgradeTargets == null || troop.UpgradeTargets.Length == 0) continue;
                var upgraded = troop.UpgradeTargets[0];
                if (upgraded == null) continue;

                int n = (int)Math.Round(elem.Number * fraction);
                if (n <= 0) continue;
                if (n > elem.Number) n = elem.Number;
                roster.AddToCounts(troop, -n);
                roster.AddToCounts(upgraded, n);
            }
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }
}
