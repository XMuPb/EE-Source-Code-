using HarmonyLib;
using System;
using System.Reflection;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BandItPlus.Patches
{
    // Wave 4.15.0-fix21 (2026-06-08) — Harmony PREFIX that suppresses vanilla
    // bandit AI's TargetSettlement-clearing for BandIt Plus raid parties.
    //
    // ROOT CAUSE (per fix15/fix16 DIAG evidence): vanilla MobilePartyAi.DecideOnNewBehavior
    // fires hourly via the campaign tick, validates that party.TargetSettlement is in
    // the AI's "allowed targets" set (bandit parties default to hideouts only), and on
    // failure directly assigns TargetSettlement = null. This freezes our raid parties
    // mid-route. fix16's brute-force re-issue + fix18's AI-flag flip + fix19's
    // SetMoveGoToPoint(absent in 1.4.5) all failed to stop the micro-oscillation.
    //
    // FIX: PREFIX returns false (skip vanilla logic) for any party currently tracked
    // in BanditRaid.BanditRaidBehavior._raidTargets. Identified via public helper
    // BanditRaidBehavior.Instance.IsTrackedRaider(MobileParty).
    //
    // VERSION-SAFE: target type/method may not exist by this name in older versions,
    // so we GUARD attach with Prepare() + log success/failure. If unavailable, patch
    // is skipped silently (existing re-issue loop in BanditRaidBehavior remains as
    // fallback). Auto-discovered by Harmony.PatchAll() in SubModuleClassEntry.
    // Wave 4.15.0-fix23 (2026-06-08): bare [HarmonyPatch] attribute is REQUIRED so
    // Harmony.PatchAll discovers the class. Without it, TargetMethod() is never
    // invoked. fix22 removed the typed-attribute and forgot to leave the bare one.
    [HarmonyPatch]
    public static class BanditAiSuppressionPatch
    {
        private static bool _prepareLogged;
        private static bool _appliedOnce;
        private static MethodBase _resolvedMethod;

        // Wave 4.15.0-fix24 (2026-06-08): static cctor logs at class JIT time so we
        // can verify whether the class is being loaded at all. If we don't see this
        // log in the next session, the running DLL isn't this build (cache issue).
        static BanditAiSuppressionPatch()
        {
            try
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "[BP-AiSuppress] Static cctor — class JIT'd. Wave 4.15.0-fix24 active.");
            }
            catch { /* never let logging crash a static cctor */ }
        }

        // Wave 4.15.0-fix22 (2026-06-08): TargetMethod() probes multiple candidate
        // names + scans for any AI-tick-shaped method since 1.4.5 renamed/removed
        // DecideOnNewBehavior. Returns null if nothing matches — Harmony skips the
        // patch entirely (no regression, just no suppression).
        public static MethodBase TargetMethod()
        {
            if (_resolvedMethod != null) return _resolvedMethod;
            try
            {
                string[] candidates = {
                    "DecideOnNewBehavior",
                    "TickBehavior",
                    "TickAi",
                    "Tick",
                    "HourlyTick",
                    "OnTick",
                    "PartyAiTick",
                    "OnPartyAiTickPeriodic",
                    "CheckPartyAi"
                };
                foreach (var name in candidates)
                {
                    var m = AccessTools.Method(typeof(MobilePartyAi), name);
                    if (m != null)
                    {
                        _resolvedMethod = m;

                        // 2026-07-26 — WARNING, verified against a user's harmony.log:
                        //
                        //   [xmupb.banditplus]
                        //   System.Void MobilePartyAi::Tick(System.Single dt)
                        //
                        // The INTENDED target, DecideOnNewBehavior, does not exist on
                        // 1.4.5 (nor do TickBehavior/TickAi/CheckPartyAi/PartyAiTick/
                        // OnPartyAiTickPeriodic). This probe therefore falls through to
                        // "Tick" — the per-frame AI driver — and because our Prefix
                        // returns false for tracked raid parties, we suppress the WHOLE
                        // AI tick for them, not just a decision call. Every piece of
                        // vanilla bookkeeping inside Tick is skipped too.
                        //
                        // That is much broader than this patch's stated intent, and it
                        // is the surface other party-AI mods collide with:
                        // PartyAIUpdated (github.com/TheVigilantWolf/PartyAIUpdated)
                        // postfixes MobilePartyAi.GetBehaviors, which vanilla calls from
                        // inside Tick — so for our tracked parties their postfix never
                        // runs, and for everything else both mods drive the same AI.
                        //
                        // Not changed here: narrowing the suppression is a design change
                        // to the raid-lock feature and needs playtesting, not a blind
                        // edit. Logged loudly so it stops being invisible.
                        bool isBroadTick = name == "Tick" || name == "OnTick" || name == "HourlyTick";
                        SafeLog("TargetMethod resolved: MobilePartyAi." + name
                            + (isBroadTick
                                ? "  *** BROAD TARGET — suppressing this skips the ENTIRE AI tick "
                                  + "for tracked raid parties, not just a decision call. The intended "
                                  + "target (DecideOnNewBehavior) is absent on this build. Expect "
                                  + "interaction with other party-AI mods. ***"
                                : ""));
                        return m;
                    }
                }
                // Final diagnostic: dump all NonPublic instance methods so we can see what's there
                if (!_prepareLogged)
                {
                    _prepareLogged = true;
                    var allMethods = typeof(MobilePartyAi).GetMethods(
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    SafeLog("TargetMethod: NONE of " + candidates.Length + " candidates matched. "
                        + "MobilePartyAi has " + allMethods.Length + " methods. Names:");
                    foreach (var m in allMethods)
                    {
                        if (m.IsSpecialName) continue; // skip property getters/setters
                        SafeLog("  - " + m.Name + " (" + m.GetParameters().Length + " params)");
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                SafeLog("TargetMethod threw " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        // Wave 4.15.0-fix24 (2026-06-08): Prepare() with NO PARAMS — matches the
        // working BanditSpawnPatch pattern in this codebase. With (MethodBase original)
        // arg, Harmony's dynamic-targeting lifecycle silently skipped (TargetMethod
        // was never called per fix22/fix23 log evidence).
        public static bool Prepare()
        {
            var found = TargetMethod() != null;
            SafeLog("Prepare: " + (found ? "RESOLVED — patch will attach" : "NOT FOUND — patch SKIPPED"));
            return found;
        }

        // PREFIX: skip the original DecideOnNewBehavior for tracked raid parties.
        // Returning false from a prefix tells Harmony to SUPPRESS the original method
        // call — exactly what we want, since the original is what clears our
        // TargetSettlement.
        //
        // For ALL other parties (player, lord, caravan, even non-tracked bandit
        // parties) return true so vanilla AI runs unchanged.
        //
        // __instance is MobilePartyAi. MobilePartyAi exposes its owning MobileParty via the
        // public `MobileParty` property in 1.2.x-1.4.5. If for any reason the property
        // isn't there or returns null, we fail OPEN (run original) so non-raid
        // parties never suffer for a reflection miss.
        // Cached reflection lookup for the MobilePartyAi → owning MobileParty back-ref.
        // In 1.4.5 there's no public .MobileParty property, but there's a private
        // field (likely `_mobileParty` or similar). Try a few candidate names.
        private static FieldInfo _cachedPartyBackref;
        private static bool _partyBackrefAttempted;

        private static MobileParty GetOwningParty(MobilePartyAi ai)
        {
            if (ai == null) return null;
            try
            {
                if (!_partyBackrefAttempted)
                {
                    _partyBackrefAttempted = true;
                    string[] candidates = { "_mobileParty", "_party", "<MobileParty>k__BackingField" };
                    foreach (var name in candidates)
                    {
                        var f = typeof(MobilePartyAi).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                        if (f != null && f.FieldType == typeof(MobileParty))
                        {
                            _cachedPartyBackref = f;
                            SafeLog("Resolved MobilePartyAi back-ref field '" + name + "'");
                            break;
                        }
                    }
                    if (_cachedPartyBackref == null)
                    {
                        // Fallback scan: any NonPublic instance MobileParty field.
                        foreach (var f in typeof(MobilePartyAi).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                        {
                            if (f.FieldType == typeof(MobileParty))
                            {
                                _cachedPartyBackref = f;
                                SafeLog("Resolved MobilePartyAi back-ref via scan: '" + f.Name + "'");
                                break;
                            }
                        }
                    }
                }
                if (_cachedPartyBackref == null) return null;
                return _cachedPartyBackref.GetValue(ai) as MobileParty;
            }
            catch { return null; }
        }

        // Wave 4.15.0-fix25 (2026-06-08): use PREFIX+POSTFIX preservation instead of
        // skipping Tick entirely. fix24's full skip blocked the engine's movement
        // processing too, freezing parties. New strategy: save TargetSettlement before
        // Tick runs (PREFIX) and restore it after (POSTFIX) if Tick cleared it.
        // Result: Tick processes movement normally + my Postfix undoes any clear.
        public static bool Prefix(MobilePartyAi __instance, out Settlement __state)
        {
            __state = null;
            try
            {
                if (__instance == null) return true;
                var party = GetOwningParty(__instance);
                if (party == null) return true;

                var raidBeh = BandItPlus.BanditRaid.BanditRaidBehavior.Instance;
                if (raidBeh == null) return true;

                if (raidBeh.IsTrackedRaider(party))
                {
                    // Save the target BEFORE Tick runs. Postfix will restore if cleared.
                    __state = party.TargetSettlement;
                    if (!_appliedOnce)
                    {
                        _appliedOnce = true;
                        SafeLog("First preservation fired for tracked raider '"
                            + (party.StringId ?? "?")
                            + "' — saved target '" + (__state != null ? __state.Name.ToString() : "<null>")
                            + "', will restore in Postfix if cleared.");
                    }
                }
                return true; // ALWAYS let vanilla Tick run (so movement processes)
            }
            catch (Exception ex)
            {
                SafeLog("Prefix threw " + ex.GetType().Name + ": " + ex.Message + " — vanilla runs unmodified.");
                return true;
            }
        }

        // Postfix: if vanilla Tick cleared our target, restore it — BUT only if the
        // party is FAR from the village. When close (within ~10 world units), allow
        // the engine's arrival-detection / raid-trigger logic to run unimpeded.
        // fix26 (2026-06-08): the prior "always restore" approach caused oscillation
        // near the village — restoring target interrupts the engine's "you arrived"
        // signal, so the raid never gets triggered.
        public static void Postfix(MobilePartyAi __instance, Settlement __state)
        {
            try
            {
                if (__instance == null || __state == null) return;
                var party = GetOwningParty(__instance);
                if (party == null) return;

                // Wave 4.15.0-fix31 (2026-06-10): if the party is currently raiding
                // (BeingRaided state, SetMoveModeHold pin active), do NOT restore
                // TargetSettlement — that would overwrite the pin with a move order
                // and cause the "enter, leave, re-enter" loop the user observed in
                // their Tharklif screenshot. Let the pin persist for the full raid.
                var raidBeh = BandItPlus.BanditRaid.BanditRaidBehavior.Instance;
                if (raidBeh != null && raidBeh.IsRaidingInProgress(party)) return;

                // Wave 4.15.0-fix27 (2026-06-08): tighten distance check from 10 → 2 units.
                // Research workflow confirmed no public raid-trigger API exists; engine
                // triggers raid only when party.CurrentSettlement == village (actually
                // ENTERED, not just nearby). fix26's 10-unit threshold left parties
                // stranded in the approach zone. Keep restoring target until parties
                // are actually at the village center (within ~2 units), where engine's
                // settlement-entry logic fires automatically.
                var dx = party.Position.X - __state.Position.X;
                var dy = party.Position.Y - __state.Position.Y;
                var distSq = dx * dx + dy * dy;
                if (distSq < 4f) return;  // sqrt(4) = 2 world units → engine entry zone

                // FAR from village + target was cleared → restore so party keeps walking.
                if (party.TargetSettlement != __state)
                {
                    BandItPlus.Compat.PartyOrderCompat.GoToSettlement(party, __state);
                }
            }
            catch
            {
                // Defensive: postfix swallow — never let restore-attempt fail Tick.
            }
        }

        private static void SafeLog(string message)
        {
            try
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-AiSuppress] " + message);
            }
            catch { /* never let logging crash a Harmony patch */ }
        }
    }
}
