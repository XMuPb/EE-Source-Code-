using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;
using BandItPlus.HideoutVisit;

namespace BandItPlus.BanditRaid
{
    // Turns a freshly-rebelled town into a BANDIT-flavored stronghold: reflavor the new independent rebel
    // clan to the bandit culture (culture/banner/name) and fold the blockading warband's troops into the
    // garrison. Does NOT destroy the warband — the caller defers that to outside its tick loop. Every engine
    // call is try/catch-guarded: a miss degrades to a plain rebellion, never a crash.
    public static class BanditSeizeHelper
    {
        // Reflavor `settlement.OwnerClan` (the rebel clan) + fold the warband's troops into the garrison.
        // `oldOwner` is the pre-rebellion owner; we only proceed if ownership actually flipped to a new clan.
        // Returns true if at least one troop was folded (caller then defers destroying the empty warband).
        public static bool TrySeize(Settlement settlement, string banditCultureId, MobileParty warband, Clan oldOwner)
        {
            try
            {
                if (settlement == null) return false;
                Clan rebel = settlement.OwnerClan;
                if (rebel == null || rebel == oldOwner)
                { Log("seize: owner not yet the rebel clan at " + settlement.Name + " — skip"); return false; }

                ReflavorClan(rebel, ResolveBanditCulture(banditCultureId));
                int folded = GarrisonInto(settlement, warband);
                // Crash fix (mirrors raid fix7): the rebel "Uprising" clan's mobile parties have a null
                // LeaderHero (rebellion clans of bandit-cultured seizes have no Lord). When
                // FactionDiscontinuation later destroys the fief-less clan, WarPartyComponent.OnFinalize
                // walks that null leader and hard-NREs. Assign the registry chief to any leaderless party.
                EnsureClanPartyLeaders(rebel, banditCultureId);
                Log("bandit-seized " + settlement.Name + " (culture " + banditCultureId + ", +" + folded + " garrison)");
                // 2026-07-01 (Bandit-King GUI, STEP C4): premium ForCityFalls panel on a successful
                // seize (settlement, seizing culture, garrison folded in). Show() marshals to the main
                // thread (TrySeize runs on the hourly tick). Guarded — never blocks the seize result.
                try
                {
                    string cultureName = ResolveBanditCulture(banditCultureId)?.Name?.ToString() ?? banditCultureId;
                    BandItPlus.UI.BanditKingBriefManager.Show(
                        BandItPlus.UI.BanditKingBriefData.ForCityFalls(settlement, cultureName, folded));
                }
                catch (Exception cfEx) { Log("city-falls panel: " + cfEx.Message); }
                return folded > 0;
            }
            catch (Exception ex) { Log("TrySeize threw " + ex.GetType().Name + ": " + ex.Message); return false; }
        }

        // Save-repair safety net: an earlier build set seized rebel clans' Culture to a bandit culture, which
        // NREs Clan.HasNavalNavigationCapability (War Sails) on the daily tick — and culture is serialized, so a
        // poisoned save keeps crashing. On load, find any settlement-owning, non-bandit clan stuck on a bandit
        // culture and reset it to the fief's (correct) culture. No-op on clean saves. Returns the count repaired.
        public static int RepairPoisonedClanCultures()
        {
            int fixedCount = 0;
            try
            {
                foreach (var clan in Clan.All)
                {
                    if (clan == null || clan.IsBanditFaction) continue;
                    if (clan.Culture == null || !clan.Culture.IsBandit) continue;       // only bandit-cultured clans
                    if (clan.Settlements == null || clan.Settlements.Count == 0) continue; // only settlement-owning rebel states

                    CultureObject fix = null;
                    foreach (var s in clan.Settlements)
                        if (s != null && s.Culture != null && !s.Culture.IsBandit) { fix = s.Culture; break; }
                    if (fix == null) continue;

                    try { clan.Culture = fix; fixedCount++; Log("repaired poisoned clan culture: " + (clan.Name != null ? clan.Name.ToString() : "?") + " -> " + fix.StringId); }
                    catch (Exception ex) { Log("repair set-culture fail: " + ex.Message); }
                }
            }
            catch (Exception ex) { Log("RepairPoisonedClanCultures threw " + ex.GetType().Name + ": " + ex.Message); }
            return fixedCount;
        }

        // 07-12 GoC-interop crash heal (player crash report). A Bandit-King rebellion could leave a
        // seized town with a NULL OwnerClan — the rebel "Uprising" clan was later removed while the
        // town kept its militia party. MilitiaPartyComponent.PartyOwner is literally
        // Settlement.OwnerClan.Leader with NO null guards (engine IL-verified), so any mod that walks
        // every party's MapFaction on session-start — Guilds of Calradia's
        // SmugglerProvisions.FeedAllFactionParties() — NREs on that ownerless militia. Vanilla never
        // walks those parties, so BP-alone and GoC-alone both load fine; only together does it bite.
        // Runs from OnGameLoaded, which the engine calls BEFORE OnSessionStart (where the sweep runs),
        // so this heals an already-poisoned save on the very next load. Returns the count healed.
        public static int RepairOwnerlessSettlements()
        {
            int fixedCount = 0;
            try
            {
                Clan globalFallback = null;
                foreach (var s in Settlement.All)
                {
                    try
                    {
                        if (s == null || s.Town == null) continue;   // militia lives on towns/castles
                        if (s.OwnerClan != null) continue;           // only the unambiguously broken ones
                        Clan newOwner = ResolveHealOwner(s, ref globalFallback);
                        if (newOwner == null || newOwner.Leader == null)
                        { Log("ownerless settlement " + SafeName(s) + " — no living clan to heal with; skipped"); continue; }

                        bool healed = false;
                        try
                        {
                            // Proper, bookkeeping-consistent reassignment: "the owning clan was
                            // destroyed, hand the fief to a new owner" — exactly our case.
                            TaleWorlds.CampaignSystem.Actions.ChangeOwnerOfSettlementAction.ApplyByDestroyClan(s, newOwner.Leader);
                            healed = s.OwnerClan != null;
                        }
                        catch (Exception aex) { Log("ApplyByDestroyClan on " + SafeName(s) + " threw " + aex.GetType().Name + ": " + aex.Message + " — trying Town.OwnerClan set"); }

                        if (!healed)
                        {
                            // Fallback: Settlement.OwnerClan is read-only but computed from the
                            // writable Town.OwnerClan. Setting it directly restores a walkable chain
                            // with no load-window event cascade.
                            try { s.Town.OwnerClan = newOwner; healed = s.OwnerClan != null; }
                            catch (Exception tex) { Log("Town.OwnerClan set on " + SafeName(s) + " threw " + tex.GetType().Name + ": " + tex.Message); }
                        }

                        if (healed) { fixedCount++; Log("healed ownerless settlement " + SafeName(s) + " -> " + SafeName(newOwner)); }
                    }
                    catch (Exception ex) { Log("RepairOwnerlessSettlements(one) threw " + ex.GetType().Name + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { Log("RepairOwnerlessSettlements threw " + ex.GetType().Name + ": " + ex.Message); }
            return fixedCount;
        }

        // A settlement's natural rescuer: a living, non-bandit, non-eliminated clan with a living
        // leader. Prefer one of the settlement's OWN culture that already holds land (a real faction
        // reclaiming its ground); otherwise the first valid clan found (cached as the global fallback).
        private static Clan ResolveHealOwner(Settlement s, ref Clan globalFallback)
        {
            CultureObject culture = null; try { culture = s.Culture; } catch { }
            foreach (var c in Clan.All)
            {
                if (!IsHealableOwner(c)) continue;
                if (globalFallback == null) globalFallback = c;
                if (culture != null && c.Culture == culture && c.Settlements != null && c.Settlements.Count > 0)
                    return c;   // best: a same-culture landed clan
            }
            return globalFallback;
        }

        private static bool IsHealableOwner(Clan c)
        {
            try { return c != null && !c.IsEliminated && !c.IsBanditFaction && c.Leader != null && c.Leader.IsAlive; }
            catch { return false; }
        }

        private static string SafeName(Settlement s)
        { try { return s != null && s.Name != null ? s.Name.ToString() : "?"; } catch { return "?"; } }

        private static string SafeName(Clan c)
        { try { return c != null && c.Name != null ? c.Name.ToString() : "?"; } catch { return "?"; } }

        public static void ReflavorClan(Clan rebel, CultureObject banditCulture)
        {
            try
            {
                // Do NOT set rebel.Culture to a bandit culture — it NREs Clan.HasNavalNavigationCapability
                // (War Sails) on the daily marriage tick. The bandit identity comes from the name + the folded
                // bandit garrison instead. We still copy a bandit banner for the map look (safe, non-null).
                if (banditCulture != null) EnsureBanner(rebel, banditCulture);
                TextObject nm = new TextObject("{=bp_hcr_017}{NAME} Uprising")
                    .SetTextVariable("NAME", rebel.Name != null ? rebel.Name.ToString() : BandItPlus.Localization.Get("bp_hcr_018", "Bandit"));
                try { rebel.ChangeClanName(nm, nm); } catch { }
            }
            catch (Exception ex) { Log("ReflavorClan threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // KEEP the engine's native rebellion banner. When a settlement rebels, the engine builds the rebel clan
        // WITH its own banner and the captured SETTLEMENT already displays it. Overwriting the clan banner here
        // (with a random one, or by sharing a bandit clan's) made the clan's PARTY banner DIFFER from its own
        // SETTLEMENT banner. Leaving the native rebellion banner untouched keeps the party and the settlement
        // IDENTICAL for free. Only synthesize one in the (impossible) case the rebel clan has no banner at all.
        public static void EnsureBanner(Clan rebel, CultureObject banditCulture)
        {
            try
            {
                if (rebel == null || rebel.Banner != null) return; // keep the native rebellion banner — party == settlement
                rebel.Banner = Banner.CreateRandomClanBanner(rebel.StringId != null ? rebel.StringId.GetHashCode() : 54321);
            }
            catch (Exception ex) { Log("EnsureBanner threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Fold the warband's surplus NON-hero troops into the settlement garrison and TRANSFER them (remove from
        // the warband — the old code only ADDED to the garrison, DUPLICATING troops). With keepFieldArmy, leave
        // ~keepCount men on the warband so the holdout King marches off with a real field army; the rest garrison.
        public static int GarrisonInto(Settlement settlement, MobileParty warband, bool keepFieldArmy = false, int keepCount = 0)
        {
            try
            {
                if (warband == null || warband.MemberRoster == null || settlement.Town == null) return 0;
                if (settlement.Town.GarrisonParty == null) TryAddGarrisonParty(settlement);
                var garrison = settlement.Town.GarrisonParty;
                if (garrison == null || garrison.MemberRoster == null) return 0;

                // Snapshot (character, number) BEFORE mutating — AddToCounts(-n) compacts/reindexes the live roster.
                var snapshot = new System.Collections.Generic.List<(CharacterObject ch, int num)>();
                for (int i = 0; i < warband.MemberRoster.Count; i++)
                {
                    var el = warband.MemberRoster.GetElementCopyAtIndex(i);
                    if (el.Character == null || el.Character.IsHero || el.Number <= 0) continue;
                    snapshot.Add((el.Character, el.Number));
                }

                int keepBudget = keepFieldArmy ? System.Math.Max(0, keepCount) : 0;
                int folded = 0;
                foreach (var (ch, num) in snapshot)
                {
                    int keepHere = System.Math.Min(keepBudget, num); // leave this many with the King
                    keepBudget -= keepHere;
                    int give = num - keepHere;                        // garrison the surplus
                    if (give <= 0) continue;
                    garrison.MemberRoster.AddToCounts(ch, give);      // into the garrison
                    warband.MemberRoster.AddToCounts(ch, -give);      // TRANSFER (remove from warband — no duplication)
                    folded += give;
                }
                return folded;
            }
            catch (Exception ex) { Log("GarrisonInto threw " + ex.GetType().Name + ": " + ex.Message); return 0; }
        }

        // AddGarrisonParty's signature varies across engine versions — invoke it reflectively with sensible
        // defaults (bool->true, value-types->default) so we don't hard-bind to one overload.
        private static void TryAddGarrisonParty(Settlement settlement)
        {
            try
            {
                var m = typeof(Settlement).GetMethod("AddGarrisonParty");
                if (m == null) return;
                var ps = m.GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
                    else if (ps[i].ParameterType == typeof(bool)) args[i] = true;
                    else if (ps[i].ParameterType.IsValueType) args[i] = Activator.CreateInstance(ps[i].ParameterType);
                    else args[i] = null;
                }
                m.Invoke(settlement, args);
            }
            catch (Exception ex) { Log("AddGarrisonParty reflect fail: " + ex.Message); }
        }

        // Crash fix (mirrors BanditRaidBehavior fix7): assign the culture's registry chief to every
        // leaderless mobile party owned by the seized rebel clan. Vanilla bandit cultures + rebellion
        // "Uprising" clans have no Lord, so their WarPartyComponent has a null LeaderHero — which NREs
        // in WarPartyComponent.OnFinalize when FactionDiscontinuationCampaignBehavior destroys the
        // fief-less clan on its daily tick. Setting a non-null leader makes OnFinalize walkable.
        private static void EnsureClanPartyLeaders(Clan rebel, string banditCultureId)
        {
            try
            {
                if (rebel == null) return;

                Hero chief = null;
                try { chief = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(banditCultureId); }
                catch (Exception ex) { Log("EnsureClanPartyLeaders GetChief threw " + ex.GetType().Name + ": " + ex.Message); }
                if (chief == null || !chief.IsAlive)
                {
                    Log("EnsureClanPartyLeaders: no live registry chief for culture " + banditCultureId +
                        " — cannot set party leaders; leaderless parties may still NRE on discontinuation.");
                    return;
                }

                // Snapshot first — assigning a leader can mutate the live party collection.
                var parties = new List<MobileParty>();
                try { if (rebel.WarPartyComponents != null) foreach (var wpc in rebel.WarPartyComponents) { if (wpc?.MobileParty != null) parties.Add(wpc.MobileParty); } }
                catch (Exception ex) { Log("EnsureClanPartyLeaders enumerate WarPartyComponents threw " + ex.GetType().Name + ": " + ex.Message); }

                int set = 0;
                foreach (var party in parties)
                {
                    try
                    {
                        if (party.LeaderHero != null) continue; // already walkable
                    }
                    catch { /* LeaderHero getter defensive — attempt to set anyway */ }
                    if (TrySetLeaderHero(party, chief)) set++;
                }
                Log("EnsureClanPartyLeaders: set leader chief '" + (chief.Name != null ? chief.Name.ToString() : chief.StringId) +
                    "' on " + set + "/" + parties.Count + " leaderless party(ies) of seized clan " +
                    (rebel.Name != null ? rebel.Name.ToString() : "?"));
            }
            catch (Exception ex) { Log("EnsureClanPartyLeaders threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static FieldInfo _cachedLeaderField;
        private static bool _leaderLookupAttempted;

        // Multi-strategy leader-hero assignment mirroring BanditRaidBehavior.TrySetLeaderHero (fix7+fix9+fix10):
        //   0) official MobileParty.ChangePartyLeader(hero) when present
        //   1) typed backing field by known name candidates (_leaderHero, <LeaderHero>k__BackingField, ...)
        //   2) scan NonPublic instance Hero fields whose name contains "leader"
        // Caches the first working field so repeated calls are O(1).
        private static bool TrySetLeaderHero(MobileParty party, Hero hero)
        {
            if (party == null || hero == null) return false;
            try
            {
                // Strategy 0 — official ChangePartyLeader(Hero) (public API since 1.2.x).
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
                        return true;
                    }
                }
                catch (Exception cplEx)
                {
                    Log("ChangePartyLeader threw " + cplEx.GetType().Name + ": " + cplEx.Message + " — falling through to reflection");
                }

                if (!_leaderLookupAttempted)
                {
                    _leaderLookupAttempted = true;
                    var t = typeof(MobileParty);
                    string[] fieldCandidates = new[]
                    {
                        "_leaderHero",
                        "<LeaderHero>k__BackingField",
                        "_partyLeader",
                        "_leader"
                    };
                    foreach (var name in fieldCandidates)
                    {
                        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                        if (f != null && f.FieldType == typeof(Hero)) { _cachedLeaderField = f; break; }
                    }
                    if (_cachedLeaderField == null)
                    {
                        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                        {
                            if (f.FieldType != typeof(Hero)) continue;
                            if (!f.Name.ToLower().Contains("leader")) continue;
                            _cachedLeaderField = f; break;
                        }
                    }
                }

                if (_cachedLeaderField != null)
                {
                    _cachedLeaderField.SetValue(party, hero);
                    return true;
                }
            }
            catch (Exception ex) { Log("TrySetLeaderHero threw " + ex.GetType().Name + ": " + ex.Message); }
            return false;
        }

        private static CultureObject ResolveBanditCulture(string cultureId)
        {
            try { return MBObjectManager.Instance.GetObject<CultureObject>(cultureId); } catch { return null; }
        }

        private static void Log(string msg)
        {
            try { HideoutPeacefulVisitState.Log("[BP-Blockade] " + msg); } catch { }
        }
    }
}
