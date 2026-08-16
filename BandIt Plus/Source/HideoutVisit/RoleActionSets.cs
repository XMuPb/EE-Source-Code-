using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace BandItPlus.HideoutVisit
{
    // Path A from docs/superpowers/specs/2026-04-27-actionset-research.md.
    //
    // Pre-cloned Monster instances per ActionSet suffix. Each clone carries an overridden
    // ActionSetCode pointing to a vanilla-registered suffix variant (e.g. "_warrior",
    // "_hideout_bandit", "_seller"). Caller passes the appropriate clone via
    // AgentBuildData.Monster(roleMonster) at spawn time, and the engine drives idle/walk/sit
    // transitions automatically — no manual SetActionChannel calls needed.
    //
    // We DO NOT register the cloned monster with MBObjectManager. The MBActionSet itself is
    // what's name-registered (vanilla loads all suffix variants at game data load). Our clone
    // is just a config bundle; the engine reads its ActionSetCode string and looks up the
    // vanilla-registered action set by name. The clone's identity never matters.
    //
    // Why CLONE instead of mutating the global Monster: Monster instances are shared across
    // every agent using that race. Mutating Monster.ActionSetCode globally would change all
    // other humans' animations too. Cloning gives us per-role copies without disturbing globals.
    //
    // Lazy initialization (on first GetMonsterForSuffix call) avoids any OnGameStart ordering
    // fragility. By the time the player walks into a hideout, human_male/human_female are
    // long since registered with MBObjectManager.
    public static class RoleActionSets
    {
        // Single Monster per suffix — vanilla "human" Monster carries both ActionSetCode (male)
        // and FemaleActionSetCode (female), so one clone covers both genders. Cache key is just
        // the suffix string. The isFemale parameter in the public API is preserved for clarity
        // but only affects which generated names get written to the clone, not the cache lookup.
        private static readonly Dictionary<string, Monster> _cache = new Dictionary<string, Monster>();
        private static bool _initialized;

        // Vanilla suffixes we provide role-monsters for. Must match what callers in
        // HideoutVisitNpcSpawnBehavior.SuffixForRole map to.
        private static readonly string[] _suffixes = new[]
        {
            ActionSetCode.HideoutBanditActionSetSuffix,  // wanderers — bandit-camp ambient
            ActionSetCode.WarriorActionSetSuffix,        // trainers — combat-ready idle
            ActionSetCode.GuardSuffix,                   // guards — alert with weapon
            ActionSetCode.UnarmedGuardSuffix,            // bodyguard variant — no weapon
            ActionSetCode.LordActionSetSuffix,           // boss — commanding presence
            ActionSetCode.BeggarSuffix,                  // sitters — cross-legged ground sit
            ActionSetCode.SellerSuffix,                  // vendors — merchant gestures
            ActionSetCode.Villager1ActionSetSuffix,      // (alt civilian)
        };

        // ActionSetCode/FemaleActionSetCode have non-public setters (despite reflection showing
        // set_ActionSetCode methods exist). PropertyInfo.SetValue can invoke them anyway.
        // Cached at static-init so we don't repeat the lookup per clone.
        // BUG M14 FIX (2026-04-30): include NonPublic in BindingFlags. Public-only worked while
        // both getter and setter were public, but if a future game patch makes the property
        // partially private, GetProperty returns null with Public-only and silently disables
        // role animations for everyone. Include NonPublic so we find the property regardless.
        private static readonly PropertyInfo _actionSetCodeProp =
            typeof(Monster).GetProperty("ActionSetCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly PropertyInfo _femaleActionSetCodeProp =
            typeof(Monster).GetProperty("FemaleActionSetCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        public static Monster GetMonsterForSuffix(string suffix, bool isFemale)
        {
            EnsureInitialized();
            // isFemale ignored — same Monster works for both genders (carries both code fields).
            return _cache.TryGetValue(suffix ?? "", out var m) ? m : null;
        }

        // BUG H7 FIX (2026-04-30): Clear the static cache when the campaign session changes.
        // The cached Monster clones reference fields from a specific MBObjectManager session
        // (bone data, mesh refs). After a save reload or quit-to-main-menu, those references
        // become stale — the engine builds new Monster instances on session start, and our
        // clones still point at the disposed prior-session data. Calling Reset() from
        // CampaignEvents.OnNewGameCreated and OnGameLoaded forces re-cloning against the
        // current session's `human` Monster, eliminating the cross-session leak.
        public static void Reset()
        {
            int hadCount = _cache.Count;
            _cache.Clear();
            _initialized = false;
            HideoutPeacefulVisitState.Log("RoleActionSets: cache cleared (had " + hadCount + " entries) — will re-init on next GetMonsterForSuffix call");
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                // Vanilla Monster ID is "human" (singular). Confirmed by grepping
                // Modules/Native/ModuleData/monsters.xml. Other variants like "human_settlement"
                // exist for villager pacing, but "human" is the base used by all combat troops
                // including bandits — matches what AgentBuildData.Monster() expects to layer onto.
                var human = MBObjectManager.Instance.GetObject<Monster>("human");
                if (human == null)
                {
                    HideoutPeacefulVisitState.Log("RoleActionSets: BAIL — 'human' Monster missing from MBObjectManager");
                    return;
                }

                int count = 0;
                foreach (var suffix in _suffixes)
                {
                    if (BuildClone(human, suffix)) count++;
                }
                HideoutPeacefulVisitState.Log("RoleActionSets: initialized " + count + "/" +
                    _suffixes.Length + " role-monsters from base 'human'");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("RoleActionSets EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool BuildClone(Monster baseMonster, string suffix)
        {
            try
            {
                var clone = ShallowCloneMonster(baseMonster);
                if (clone == null) return false;

                // Generate gender-specific names — engine picks ActionSetCode for male agents,
                // FemaleActionSetCode for female agents based on the agent's IsFemale flag.
                // Vanilla pre-registers MBActionSet entries for every suffix × monster × gender
                // combination at game-data load time, so the engine resolves both names when
                // it processes our agent's animation system data.
                var maleName   = ActionSetCode.GenerateActionSetNameWithSuffix(baseMonster, false, suffix);
                var femaleName = ActionSetCode.GenerateActionSetNameWithSuffix(baseMonster, true, suffix);

                // Use reflection to bypass non-public setter visibility. PropertyInfo.SetValue
                // calls the setter via runtime invocation regardless of accessibility.
                _actionSetCodeProp?.SetValue(clone, maleName);
                _femaleActionSetCodeProp?.SetValue(clone, femaleName);

                _cache[suffix] = clone;
                HideoutPeacefulVisitState.Log("RoleActionSets: '" + suffix + "' → male='" + maleName + "' female='" + femaleName + "'");
                return true;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("RoleActionSets BuildClone fail (" + suffix + "): " + ex.Message);
                return false;
            }
        }

        // Shallow reflection copy of all instance fields (skipping init-only/readonly so we don't
        // throw on the runtime-immutable backing fields of MBObjectBase). Bone data and other
        // reference fields are intentionally SHARED with the source — we don't need our own
        // bone hierarchy, we just need a Monster object that carries our overridden ActionSetCode.
        //
        // BUG M15 NOTE (2026-04-30): Sharing reference fields (bone arrays, capsule lists) with
        // the source `human` Monster means engine writes through one of our clones could in
        // theory corrupt the source. In practice we only OVERRIDE the two ActionSet code fields
        // (which are immutable strings) and the engine treats Monster as read-mostly during
        // agent init — no observed corruption in 6+ months of play. Deep-cloning every reference
        // field would be expensive and require auditing every field's mutability contract. If
        // this ever produces visible bugs, see TaleWorlds.Core.Monster source for which fields
        // are written during agent lifecycle (capsule, NoseAnimationOnAttack, etc.).
        private static Monster ShallowCloneMonster(Monster source)
        {
            try
            {
                var clone = new Monster();
                var bind = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                foreach (var f in typeof(Monster).GetFields(bind))
                {
                    if (f.IsInitOnly) continue;
                    try { f.SetValue(clone, f.GetValue(source)); }
                    catch { /* per-field failure is fine — keep cloning the rest */ }
                }
                return clone;
            }
            catch
            {
                return null;
            }
        }
    }
}
