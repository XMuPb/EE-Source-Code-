using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BandItPlus.Compat
{
    // 2026-07-26 — version-safe MobileParty movement orders.
    //
    // WHY THIS FILE EXISTS
    // --------------------
    // Every SetMove* overload this mod calls takes MobileParty.NavigationType —
    // the War Sails (1.4.5) navigation enum. None of those overloads exist on
    // 1.3.15. A user on 1.3.15 reported:
    //
    //   System.MissingMethodException: 'Void MobileParty.SetMoveRaidSettlement(
    //       Settlement, NavigationType, Boolean)'
    //     at BandItPlus.BanditRaid.BanditRaidBehavior.OnVillageStateChanged(...)
    //
    // Those call sites were ALL already inside try/catch — and it crashed anyway.
    //
    // THE TRAP: the CLR resolves method tokens while JIT-compiling the CONTAINING
    // method, i.e. before its first IL instruction runs. A try/catch in the same
    // method cannot catch a MissingMethodException for a call in that method.
    // That is why the reported stack trace surfaces at OnVillageStateChanged
    // rather than inside its guarded block — and why the SetMoveGoToSettlement
    // "fallback" in the catch block was never going to rescue anything: it takes
    // NavigationType too, so it is missing on exactly the builds that need it.
    //
    // THE FIX: each version-specific call lives ALONE in a
    // [MethodImpl(NoInlining)] island. The JIT failure then happens when that
    // island is prepared — inside a try here, one frame up, where it is
    // catchable. NoInlining is load-bearing: without it the JIT may fold the
    // island back into its caller and reintroduce the exact failure.
    //
    // Per verb the order is: probe the 1.4.x island once -> on failure, reflect
    // for whatever arity this build exposes -> otherwise report unavailable and
    // let the caller skip the order.
    //
    // Behaviour on 1.4.5 is unchanged: same call, one stack frame deeper.
    // Callers keep their own SetDoNotMakeNewDecisions bookkeeping.
    public static class PartyOrderCompat
    {
        private enum Strategy
        {
            Unprobed = 0,
            Direct,      // 1.4.x compile-time signature works
            Reflected,   // another signature found by probe
            Unavailable  // no such API on this build
        }

        private const BindingFlags kAllInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // One binding per movement verb. `Island` invokes the direct 1.4.x call;
        // `IslandName` names the private method backing it, prepared via
        // RuntimeHelpers.PrepareMethod to force JIT inside a guarded frame.
        private sealed class Binding
        {
            public string DisplayName;
            public string[] Candidates;
            public string IslandName;
            public Action<MobileParty, object> Island;

            public Strategy Strategy = Strategy.Unprobed;
            public MethodInfo Reflected;
            public bool OnAi;
        }

        private static readonly Binding GoTo = new Binding
        {
            DisplayName = "SetMoveGoToSettlement",
            Candidates = new[] { "SetMoveGoToSettlement", "MoveGoToSettlement" },
            IslandName = "Island_GoTo",
            Island = (p, t) => Island_GoTo(p, (Settlement)t)
        };

        private static readonly Binding Raid = new Binding
        {
            DisplayName = "SetMoveRaidSettlement",
            Candidates = new[] { "SetMoveRaidSettlement", "MoveRaidSettlement", "SetRaidSettlement" },
            IslandName = "Island_Raid",
            Island = (p, t) => Island_Raid(p, (Settlement)t)
        };

        private static readonly Binding Besiege = new Binding
        {
            DisplayName = "SetMoveBesiegeSettlement",
            Candidates = new[] { "SetMoveBesiegeSettlement", "MoveBesiegeSettlement" },
            IslandName = "Island_Besiege",
            Island = (p, t) => Island_Besiege(p, (Settlement)t)
        };

        private static readonly Binding Defend = new Binding
        {
            DisplayName = "SetMoveDefendSettlement",
            Candidates = new[] { "SetMoveDefendSettlement", "MoveDefendSettlement" },
            IslandName = "Island_Defend",
            Island = (p, t) => Island_Defend(p, (Settlement)t)
        };

        private static readonly Binding Engage = new Binding
        {
            DisplayName = "SetMoveEngageParty",
            Candidates = new[] { "SetMoveEngageParty", "MoveEngageParty" },
            IslandName = "Island_Engage",
            Island = (p, t) => Island_Engage(p, (MobileParty)t)
        };

        private static readonly Binding Escort = new Binding
        {
            DisplayName = "SetMoveEscortParty",
            Candidates = new[] { "SetMoveEscortParty", "MoveEscortParty" },
            IslandName = "Island_Escort",
            Island = (p, t) => Island_Escort(p, (MobileParty)t)
        };

        // ───────────────────────── public API ─────────────────────────
        //
        // Drop-in replacements for the direct engine calls. Each returns true
        // when the order was actually issued, so callers can fall through to
        // their own alternative. None of them ever throw.

        public static bool GoToSettlement(MobileParty party, Settlement target)
        {
            return Issue(GoTo, party, target, typeof(Settlement));
        }

        public static bool RaidSettlement(MobileParty party, Settlement target)
        {
            return Issue(Raid, party, target, typeof(Settlement));
        }

        public static bool BesiegeSettlement(MobileParty party, Settlement target)
        {
            return Issue(Besiege, party, target, typeof(Settlement));
        }

        public static bool DefendSettlement(MobileParty party, Settlement target)
        {
            return Issue(Defend, party, target, typeof(Settlement));
        }

        public static bool EngageParty(MobileParty party, MobileParty target)
        {
            return Issue(Engage, party, target, typeof(MobileParty));
        }

        public static bool EscortParty(MobileParty party, MobileParty target)
        {
            return Issue(Escort, party, target, typeof(MobileParty));
        }

        // True when the running build exposes a raid-intent API at all.
        // Safe from a Harmony Prepare() — never throws.
        public static bool IsRaidIntentAvailable
        {
            get
            {
                EnsureProbed(Raid, typeof(Settlement));
                return Raid.Strategy == Strategy.Direct || Raid.Strategy == Strategy.Reflected;
            }
        }

        // The concrete MethodInfo a Harmony patch should target, or null when the
        // running build has no such method (the patch must then decline to apply).
        public static MethodInfo ResolveRaidMethodForPatching()
        {
            return ResolveOnParty(Raid, typeof(Settlement));
        }

        // Emit one line per verb at startup so a user's log states exactly which
        // engine APIs resolved on their build. Turns a crash into a log line.
        public static void LogResolvedApis()
        {
            EnsureProbed(GoTo, typeof(Settlement));
            EnsureProbed(Raid, typeof(Settlement));
            EnsureProbed(Besiege, typeof(Settlement));
            EnsureProbed(Defend, typeof(Settlement));
            EnsureProbed(Engage, typeof(MobileParty));
            EnsureProbed(Escort, typeof(MobileParty));
        }

        // ───────────────────────── dispatch ─────────────────────────

        private static bool Issue(Binding b, MobileParty party, object target, Type targetType)
        {
            if (party == null || target == null) return false;

            EnsureProbed(b, targetType);

            if (b.Strategy == Strategy.Direct)
            {
                try
                {
                    b.Island(party, target);
                    return true;
                }
                catch (Exception ex)
                {
                    // Should not happen once probed, but never trust an engine API
                    // twice. Demote to reflection and retry once.
                    Log(b.DisplayName + ": direct path failed post-probe ("
                        + Unwrap(ex).GetType().Name + ") — demoting to reflection");
                    ProbeReflection(b, targetType);
                    return b.Strategy == Strategy.Reflected && InvokeReflected(b, party, target);
                }
            }

            if (b.Strategy == Strategy.Reflected)
                return InvokeReflected(b, party, target);

            return false;
        }

        private static bool InvokeReflected(Binding b, MobileParty party, object target)
        {
            try
            {
                object instance = b.OnAi ? (object)party.Ai : party;
                if (instance == null || b.Reflected == null) return false;
                b.Reflected.Invoke(instance, BuildArgs(b.Reflected, target));
                return true;
            }
            catch (Exception ex)
            {
                var root = Unwrap(ex);
                Log(b.DisplayName + ": reflected invoke threw "
                    + root.GetType().Name + ": " + root.Message);
                return false;
            }
        }

        // ───────────────────────── probing ─────────────────────────

        private static void EnsureProbed(Binding b, Type targetType)
        {
            if (b.Strategy != Strategy.Unprobed) return;

            // PrepareMethod forces the JIT to compile the island right here,
            // inside this try — so a missing method or missing NavigationType
            // type surfaces as a catchable exception instead of killing whichever
            // campaign-event handler happened to call us first.
            try
            {
                var island = typeof(PartyOrderCompat)
                    .GetMethod(b.IslandName, BindingFlags.Static | BindingFlags.NonPublic);
                if (island == null)
                    throw new MissingMethodException("island " + b.IslandName + " missing");

                RuntimeHelpers.PrepareMethod(island.MethodHandle);

                b.Strategy = Strategy.Direct;
                Log(b.DisplayName + ": native 1.4.x signature available.");
                return;
            }
            catch (Exception ex)
            {
                Log(b.DisplayName + ": 1.4.x signature unavailable on this build ("
                    + Unwrap(ex).GetType().Name + ") — probing alternatives.");
            }

            ProbeReflection(b, targetType);
        }

        private static void ProbeReflection(Binding b, Type targetType)
        {
            b.Reflected = null;
            b.OnAi = false;

            try
            {
                var onParty = ResolveOnParty(b, targetType);
                if (onParty != null)
                {
                    b.Reflected = onParty;
                    b.OnAi = false;
                }
                else
                {
                    foreach (var name in b.Candidates)
                    {
                        var m = typeof(MobilePartyAi).GetMethod(name, kAllInstance);
                        if (m != null && IsUsableSignature(m, targetType))
                        {
                            b.Reflected = m;
                            b.OnAi = true;
                            break;
                        }
                    }
                }
            }
            catch { }

            if (b.Reflected != null)
            {
                b.Strategy = Strategy.Reflected;
                Log(b.DisplayName + ": resolved " + (b.OnAi ? "MobilePartyAi." : "MobileParty.")
                    + b.Reflected.Name + " (" + b.Reflected.GetParameters().Length
                    + " params) by reflection.");
            }
            else
            {
                b.Strategy = Strategy.Unavailable;
                Log(b.DisplayName + ": NO equivalent API on this build — orders of this kind are skipped.");
            }
        }

        private static MethodInfo ResolveOnParty(Binding b, Type targetType)
        {
            try
            {
                foreach (var name in b.Candidates)
                {
                    var m = typeof(MobileParty).GetMethod(name, kAllInstance);
                    if (m != null && IsUsableSignature(m, targetType)) return m;
                }
            }
            catch { }
            return null;
        }

        // Usable = first parameter accepts our target, and every remaining
        // parameter is something we can synthesise a sane default for.
        private static bool IsUsableSignature(MethodInfo m, Type targetType)
        {
            var ps = m.GetParameters();
            if (ps.Length == 0) return false;
            if (!ps[0].ParameterType.IsAssignableFrom(targetType)) return false;
            for (int i = 1; i < ps.Length; i++)
            {
                var t = ps[i].ParameterType;
                bool ok = t.IsEnum || t == typeof(bool) || !t.IsValueType;
                if (!ok) return false;
            }
            return true;
        }

        // Synthesise an argument array for whatever arity this build exposes:
        // the target first, then Default-ish enum values, false for bools, null
        // for reference types. Covers every observed shape, including
        // SetMoveDefendSettlement(Settlement, bool, NavigationType) where the
        // bool sits in the middle.
        private static object[] BuildArgs(MethodInfo m, object target)
        {
            var ps = m.GetParameters();
            var args = new object[ps.Length];
            args[0] = target;
            for (int i = 1; i < ps.Length; i++)
            {
                var t = ps[i].ParameterType;
                if (t.IsEnum) args[i] = DefaultEnumValue(t);
                else if (t == typeof(bool)) args[i] = false;
                else if (t.IsValueType) args[i] = Activator.CreateInstance(t);
                else args[i] = null;
            }
            return args;
        }

        // Prefer a member literally named "Default"; otherwise take the first.
        private static object DefaultEnumValue(Type enumType)
        {
            try
            {
                foreach (var name in Enum.GetNames(enumType))
                {
                    if (string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase))
                        return Enum.Parse(enumType, name);
                }
                var values = Enum.GetValues(enumType);
                if (values.Length > 0) return values.GetValue(0);
            }
            catch { }
            return Activator.CreateInstance(enumType);
        }

        // ───────────────────────── version-specific islands ─────────────────────────
        //
        // NOTHING may be added to these methods. Each holds exactly ONE
        // version-specific call so that a MissingMethodException /
        // TypeLoadException raised while JIT-compiling it is attributable to that
        // verb and catchable by the prober above. NoInlining is load-bearing,
        // not decorative. Do not merge them, do not add logging, do not add guards.

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Island_GoTo(MobileParty p, Settlement s)
        {
            p.SetMoveGoToSettlement(s, MobileParty.NavigationType.Default, false);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Island_Raid(MobileParty p, Settlement s)
        {
            p.SetMoveRaidSettlement(s, MobileParty.NavigationType.Default, false);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Island_Besiege(MobileParty p, Settlement s)
        {
            p.SetMoveBesiegeSettlement(s, MobileParty.NavigationType.Default);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Island_Defend(MobileParty p, Settlement s)
        {
            p.SetMoveDefendSettlement(s, false, MobileParty.NavigationType.Default);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Island_Engage(MobileParty p, MobileParty t)
        {
            p.SetMoveEngageParty(t, MobileParty.NavigationType.Default);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Island_Escort(MobileParty p, MobileParty t)
        {
            p.SetMoveEscortParty(t, MobileParty.NavigationType.Default, false);
        }

        // ───────────────────────── shared helpers ─────────────────────────

        internal static Exception Unwrap(Exception ex)
        {
            while (ex != null && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        private static void Log(string message)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Compat] " + message); }
            catch { }
        }
    }
}
