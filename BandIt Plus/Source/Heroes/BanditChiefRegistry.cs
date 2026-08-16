using System.Collections.Generic;
using BandItPlus.Cultures;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace BandItPlus.Heroes
{
    // Wave 4.8b: per-culture named chief registry — RENAME-ON-ENCOUNTER strategy.
    //
    // Background of failed approaches:
    //
    //   - Wave 4.8a tried CreateNotable(GangLeader, town). That created a "ghost" Hero
    //     attached to an Empire town clan. Encyclopedia indexed only the underlying
    //     CharacterObject (visible as a Troop entry), not as a proper Hero, and the
    //     hideout chief NPC was a separate dynamically-spawned bandit hero (Vuljotur etc.)
    //     so the player still saw random names in dialog AND the QuestGiver portrait.
    //
    //   - Wave 4.8a-pre tried renaming Clan.Heroes for vanilla bandit clans. Bandit
    //     clans have heroes=0 — they're placeholder factions that spawn heroes per-party.
    //
    // Working approach (this file):
    //   1. When the player encounters a bandit party / opens chief dialog, BanditDialogManager
    //      gets the actual encounter party's leader hero (the one the player is talking to).
    //   2. We call EnsureChiefNamed(leaderHero, cultureId), which:
    //        - Checks if we've already promoted a chief for this culture
    //        - If not, RENAMES this hero to the culture's ChiefName via Hero.SetName
    //        - Sets Occupation=GangLeader via reflection so they're encyclopedia-visible
    //        - Stores their StringId per culture (saveable)
    //   3. Subsequent dialog/quest acceptance uses GetChief(cultureId) which returns the
    //      same Hero, so the QuestGiver portrait + the hideout chief NPC are the same
    //      named entity (Skarvac, Vargolf, etc.).
    //
    // This means: the SAME hero you talk to in the hideout becomes the QuestGiver. No
    // ghost-notables. No clan-juggling. The Hero is already properly registered in
    // MBObjectManager so the StringId lookup just works.
    public class BanditChiefRegistry : CampaignBehaviorBase
    {
        // cultureId -> Hero.StringId (the StringId of whatever hero was promoted).
        private Dictionary<string, string> _chiefIdByCulture = new Dictionary<string, string>();

        // Wave 4.8c-fix: MBObjectManager.GetObject<Hero>(stringId) returns null for our
        // chiefs because their auto-generated Hero.StringId collides with the underlying
        // CharacterObject template's StringId — type-filtered lookup picks the wrong one.
        // Store strong references in a non-saveable runtime dict, rebuilt at session start.
        private static readonly Dictionary<string, Hero> _runtimeChiefRefs = new Dictionary<string, Hero>();

        // Wave 4.8b-fix: Campaign.Current?.GetCampaignBehavior<BanditChiefRegistry>()
        // returns null in some load paths. Capture the live instance in RegisterEvents
        // (which Bannerlord guarantees to call after AddBehavior) so callers always
        // see a valid reference.
        private static BanditChiefRegistry _instance;
        public static BanditChiefRegistry Instance => _instance;

        public override void RegisterEvents()
        {
            _instance = this;
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                "BanditChiefRegistry.RegisterEvents called (Wave 4.8c) — Instance set");
            // Wave 4.8c-final: create chiefs DURING new game init (after clans/settlements
            // are populated but BEFORE encyclopedia builds its search index). This is
            // crucial because OnSessionLaunched fires AFTER the search index is locked,
            // so heroes added then don't appear in search results.
            CampaignEvents.OnNewGameCreatedPartialFollowUpEndEvent.AddNonSerializedListener(this, OnNewGamePartialFollowUpEnd);
            // Wave 4.9: Stage 2 — dedicated bandit clan creation runs AFTER character
            // creation. OnSessionLaunched fires once world & character creation are done,
            // so NameGenerator.GenerateClanName is no longer iterating Clan.All in the
            // wizard flow. Safe to add new clans now.
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            // Save loads: chiefs are already persisted via _chiefIdByCulture, but we need
            // to repopulate the runtime strong-reference dict.
            CampaignEvents.OnGameLoadFinishedEvent.AddNonSerializedListener(this, OnGameLoadFinished);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
        }

        // Wave 4.9 Stage 2: assign each chief to a dedicated bandit-themed clan AFTER
        // character creation. Safe to create new clans here — NameGenerator no longer
        // iterates Clan.All in the wizard flow.
        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                "BanditChiefRegistry.OnSessionLaunched — Stage 2: assigning dedicated bandit clans");

            // Wave 4.11-fix v31 (2026-05-07): one-time cleanup to undo v22's
            // IsMinorFactionHero=true on chiefs that have it persisted in the save.
            // v27 disabled NEW assignments but existing saves still serialized the flag,
            // so the marriage daily-tick NRE returns on save reload. This loop force-
            // clears the flag on every registered chief at session start. Runs every
            // session — no harm if already false.
            try
            {
                int cleared = 0;
                foreach (var existingChief in GetAllChiefHeroes())
                {
                    if (existingChief == null) continue;
                    try
                    {
                        if (existingChief.IsMinorFactionHero)
                        {
                            existingChief.IsMinorFactionHero = false;
                            cleared++;
                        }
                    }
                    catch { /* defensive */ }
                }
                if (cleared > 0)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditChiefRegistry: v31 cleanup - cleared IsMinorFactionHero on "
                        + cleared + " chief(s) to prevent marriage daily-tick NRE");
                }
            }
            catch (System.Exception cleanupEx)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry: v31 cleanup failed: " + cleanupEx.Message);
            }

            // Wave 4.11-fix (2026-05-06): defer ChiefHeroTypePatch attachment to here
            // (post-character-creation) instead of OnSubModuleLoad. Attaching the
            // EncyclopediaHeroPageVM.Refresh postfix at mod-load time caused
            // CampaignUIHelper's static cctor to fire before Campaign.Current existed,
            // crashing CharacterCreationGainedPropertiesVM construction with a cached
            // TypeInitializationException. By the time OnSessionLaunched fires the
            // campaign object is fully initialized and the encyclopedia VMs can load
            // their dependencies safely.
            // Wave 4.11-fix v14 (2026-05-06): ChiefHeroTypePatch RE-ENABLED. The previous
            // "encyclopedia broken" symptom (v8 disable) was almost certainly caused by the
            // OnSessionLaunched brute-force Culture/Occupation reflection block, not by
            // ChiefHeroTypePatch.Postfix itself. That brute-force block is now permanently
            // disabled (#if false at the Stage 2 site below), and Culture is set at chief
            // creation instead (v12-v13). With the search-index/state divergence gone, the
            // UI-only Type override patch should be safe to attach. Display-only — never
            // mutates Hero state — so it can't break search by changing what's indexed.
            // If anything regresses, comment this block out as a single revert.
            try
            {
                var harmony = BandItPlus.HideoutVisit.HideoutPeacefulVisitState.HarmonyInstance;
                if (harmony != null)
                {
                    BandItPlus.Patches.ChiefHeroTypePatch.TryPatch(harmony);
                    // Wave 4.29.1: same deferral rule as above — the chronicle fallback
                    // also postfixes EncyclopediaHeroPageVM/ClanPageVM.Refresh, so it
                    // must attach post-campaign-init, never at OnSubModuleLoad (the
                    // CampaignUIHelper static-cctor crash documented at line 122).
                    BandItPlus.Patches.EncyclopediaChroniclePatcher.TryPatch(harmony);
                }
            }
            catch (System.Exception cpEx)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry: deferred ChiefHeroTypePatch.TryPatch failed: " + cpEx.Message);
            }
            try
            {
                int reassigned = 0;
                foreach (var kv in _runtimeChiefRefs)
                {
                    var chief = kv.Value;
                    if (chief == null || !chief.IsAlive) continue;
                    if (chief.Clan != null && chief.Clan.StringId.StartsWith("bp_chief_clan_")) continue; // already dedicated
                    // 2026-06-03 keep-UI fix: pass the chief's culture-matched bandit
                    // hideout as the clan home, NOT chief.HomeSettlement (which is the
                    // merchant_empire template's seed Empire town — same for all 14
                    // chiefs, causing them to all appear in that town's keep/hall UI).
                    // Each culture has its own dedicated hideout; distributing the
                    // bp_chief_clan_* clans across hideouts keeps the chiefs off the
                    // keep panel of any town.
                    Settlement preferredClanHome = FindCultureMatchedHideout(kv.Key)
                        ?? chief.HomeSettlement
                        ?? chief.Clan?.HomeSettlement;
                    var dedicatedClan = GetOrCreateChiefClan(kv.Key, preferredClanHome);
                    if (dedicatedClan == null) continue;
                    try
                    {
                        chief.Clan = dedicatedClan;
                        // Wave 4.9-fix: encyclopedia clan-page VM crashes (NRE in
                        // EncyclopediaClanPageVM ctor) if clan.Leader is null. Set
                        // leader and initialize members/post-init hooks so all
                        // vanilla clan-VM accessors find non-null state.
                        // InitMembers is internal — reflect it.
                        try
                        {
                            var imMethod = typeof(Clan).GetMethod("InitMembers",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                            if (imMethod != null) imMethod.Invoke(dedicatedClan, null);
                        }
                        catch { }
                        try { dedicatedClan.SetLeader(chief); } catch { }
                        try { dedicatedClan.AfterInitialized(); } catch { }
                        try { dedicatedClan.AddRenown(50f, false); } catch { }
                        // Wave 4.11-fix v11 (2026-05-06): isolated test — brute-force Culture/
                        // Occupation reflection block DISABLED to verify whether it was the cause
                        // of the encyclopedia search failure. If search works again with this
                        // block off, the writes here introduce state the search index doesn't
                        // tolerate (custom bandit-culture id on a Notable hero, or mid-session
                        // Occupation transition). v4-v7 implementations of this block are kept
                        // in git history for reference; restore by removing the #if false guard.
#if false
                        int culHeroN = 0, culCharN = 0, occHeroN = 0, occCharN = 0;
                        var occCulSet = new System.Collections.Generic.List<string>();
                        try
                        {
                            const System.Reflection.BindingFlags BF =
                                System.Reflection.BindingFlags.Instance
                                | System.Reflection.BindingFlags.NonPublic
                                | System.Reflection.BindingFlags.Public
                                | System.Reflection.BindingFlags.DeclaredOnly;

                            // CULTURE — set on Hero AND CharacterObject inheritance chains.
                            if (dedicatedClan.Culture != null)
                            {
                                for (var t = chief.GetType(); t != null; t = t.BaseType)
                                    foreach (var f in t.GetFields(BF))
                                        if (f.FieldType == typeof(TaleWorlds.Core.BasicCultureObject)
                                            || f.FieldType == typeof(CultureObject))
                                        {
                                            try { f.SetValue(chief, dedicatedClan.Culture); culHeroN++; occCulSet.Add("H." + t.Name + "." + f.Name); } catch { }
                                        }
                                if (chief.CharacterObject != null)
                                    for (var t = chief.CharacterObject.GetType(); t != null; t = t.BaseType)
                                        foreach (var f in t.GetFields(BF))
                                            if (f.FieldType == typeof(TaleWorlds.Core.BasicCultureObject)
                                                || f.FieldType == typeof(CultureObject))
                                            {
                                                try { f.SetValue(chief.CharacterObject, dedicatedClan.Culture); culCharN++; occCulSet.Add("C." + t.Name + "." + f.Name); } catch { }
                                            }
                            }

                            // OCCUPATION — same brute-force on both Hero and CharacterObject.
                            for (var t = chief.GetType(); t != null; t = t.BaseType)
                                foreach (var f in t.GetFields(BF))
                                    if (f.FieldType == typeof(Occupation))
                                    {
                                        try { f.SetValue(chief, Occupation.GangLeader); occHeroN++; occCulSet.Add("Ho." + t.Name + "." + f.Name); } catch { }
                                    }
                            if (chief.CharacterObject != null)
                                for (var t = chief.CharacterObject.GetType(); t != null; t = t.BaseType)
                                    foreach (var f in t.GetFields(BF))
                                        if (f.FieldType == typeof(Occupation))
                                        {
                                            try { f.SetValue(chief.CharacterObject, Occupation.GangLeader); occCharN++; occCulSet.Add("Co." + t.Name + "." + f.Name); } catch { }
                                        }

                            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                                "BanditChiefRegistry: w411-diag " + (chief.Name?.ToString() ?? "?")
                                + " | culHero=" + culHeroN + " culChar=" + culCharN
                                + " occHero=" + occHeroN + " occChar=" + occCharN
                                + " | fields=[" + string.Join(",", occCulSet) + "]");
                        }
                        catch (System.Exception bfx)
                        {
                            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                                "BanditChiefRegistry: w411-diag exception: " + bfx.GetType().Name + ": " + bfx.Message);
                        }
#endif
                        // Wave 4.9-fix: replace merchant gear with culture-themed equipment
                        // from the existing bandit chieftain/boss CharacterObject templates.
                        try { ApplyCultureEquipment(chief, kv.Key); } catch { }
                        try { ApplyChiefSkills(chief, kv.Key); } catch { }
                        try { DeclareClanWar(dedicatedClan, kv.Key); } catch { }
                        // Wave 4.11-fix: post-Stage-2 diagnostic — captures final chief state
                        // AFTER all reassignment/equipment/skill passes. The "created Hero"
                        // line emitted at creation time only captured pre-Stage-2 state
                        // (always isLord=False, clan=clan_empire_*). Lets us verify Type/Culture
                        // rendering after the fix instead of guessing.
                        try
                        {
                            // Wave 4.11-fix v20 (2026-05-07): expanded diagnostic to debug
                            // tooltip Type="Unknown". Adds the flags vanilla's hero tooltip
                            // handler likely checks: IsKnownToPlayer, HasMet, IsAlive,
                            // IsActive, IsDisabled, banditClan-flag, IsHumanPlayerCharacter.
                            // Diagnostic fix v21: read the public Hero.HasMet property
                            // directly (the property exists with public getter per reflection
                            // dump). Previously read the wrong auto-property backing-field
                            // name which doesn't exist — Hero's actual backing is _hasMet.
                            bool hasMet = false;
                            try { hasMet = chief.HasMet; } catch { }
                            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                                "BanditChiefRegistry: post-Stage2 chief " + (chief.Name?.ToString() ?? "?")
                                + " culture=" + (chief.Culture?.StringId ?? "null")
                                + " isLord=" + chief.IsLord
                                + " isNotable=" + chief.IsNotable
                                + " occ=" + chief.Occupation
                                + " clan=" + (chief.Clan?.StringId ?? "null")
                                + " | KnownToPlayer=" + chief.IsKnownToPlayer
                                + " HasMet=" + hasMet
                                + " IsAlive=" + chief.IsAlive
                                + " IsActive=" + chief.IsActive
                                + " IsDisabled=" + chief.IsDisabled
                                + " ClanIsBandit=" + (chief.Clan?.IsBanditFaction ?? false)
                                + " ClanIsMinor=" + (chief.Clan?.IsMinorFaction ?? false));
                        }
                        catch { }
                        reassigned++;
                    }
                    catch (System.Exception cx)
                    {
                        BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                            "BanditChiefRegistry: clan reassign for " + kv.Key + " failed: " + cx.Message);
                    }
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry: Stage 2 reassigned " + reassigned + " chiefs to dedicated clans");
                // Stage 2b: relationships pass — needs all chiefs alive and clan-led.
                try { ApplyChiefRelationships(); } catch (System.Exception rx) {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditChiefRegistry.ApplyChiefRelationships failed: " + rx.Message);
                }
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry.OnSessionLaunched failed: " + ex.Message);
            }
        }

        private void OnNewGamePartialFollowUpEnd(CampaignGameStarter starter)
        {
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                "BanditChiefRegistry.OnNewGamePartialFollowUpEnd — creating Hero entities pre-encyclopedia-index");
            CreateAllChiefs();
        }

        private void OnGameLoadFinished()
        {
            // Repopulate runtime refs from saved StringIds. If lookup fails (StringId
            // collision issue), recreate the chief.
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                "BanditChiefRegistry.OnGameLoadFinished — restoring chief runtime refs");
            try
            {
                // 07-13 ROOT-CAUSE FIX: _runtimeChiefRefs is process-static, so on a
                // SECOND (or later) campaign load within one app run it still holds Hero
                // refs from the PREVIOUS campaign. Those discarded heroes still report
                // IsAlive==true, so GetChief() hands them back as "canonical" and
                // KillOrphanChiefs() then murders the REAL loaded chiefs (they aren't
                // reference-equal to the stale ones). That is why chiefs survive the
                // first load after an app restart but die on every reload afterwards.
                // Clear the stale refs before rebuilding from the save so nothing
                // carries across campaigns.
                _runtimeChiefRefs.Clear();

                // v223 (2026-06-01): guarantee the dict exists. If the save predates
                // BP or the SyncData key wasn't found, _chiefIdByCulture may be null.
                if (_chiefIdByCulture == null)
                    _chiefIdByCulture = new System.Collections.Generic.Dictionary<string, string>();

                int restored = 0, missing = 0;
                foreach (var kv in _chiefIdByCulture)
                {
                    var hero = MBObjectManager.Instance.GetObject<Hero>(kv.Value);
                    if (hero != null && hero.IsAlive)
                    {
                        _runtimeChiefRefs[kv.Key] = hero;
                        restored++;
                    }
                    else missing++;
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry: restored " + restored + " refs, " + missing
                    + " missing (dictCount=" + _chiefIdByCulture.Count + ")");

                // v223 ROOT-CAUSE FIX (2026-06-01): when the saved dict is empty
                // (Count == 0), the foreach above never executes, so restored=0 AND
                // missing=0. The previous check `if (missing > 0) CreateAllChiefs()`
                // never fired, leaving _runtimeChiefRefs PERMANENTLY EMPTY for the
                // session — hideout banners stayed vanilla, the hideout tooltip
                // "Owner" row was blank, and chief portraits/names disappeared from
                // the encyclopedia. ANY save made before chief creation succeeded,
                // or with a different SyncData key, hit this trap.
                //
                // Symptoms in user's 01:07 session that pointed here:
                //   "BanditChiefRegistry: restored 0 refs, 0 missing"     <-- empty dict
                //   "v222 hideout banner map built: 0 hideouts (chiefs=0 ...)"
                //   Owner row blank in PropertyBasedTooltipVM screenshot
                //
                // Fix: ALSO trigger CreateAllChiefs when the dict is empty. This
                // covers (a) save-predates-chiefs, (b) SyncData key mismatch, AND
                // (c) the original missing>0 case. Single recovery branch.
                if (_chiefIdByCulture.Count == 0 || missing > 0)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "v223 OnGameLoadFinished -> forcing CreateAllChiefs (count="
                        + _chiefIdByCulture.Count + " missing=" + missing + ")");
                    CreateAllChiefs();
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "v223 OnGameLoadFinished post-CreateAllChiefs: _runtimeChiefRefs.Count="
                        + _runtimeChiefRefs.Count
                        + " _chiefIdByCulture.Count=" + _chiefIdByCulture.Count);

                    // v224 (2026-06-01): Stage 2 clan reassignment + chief-relationship
                    // pass already ran a few seconds ago at OnSessionLaunched, BEFORE
                    // v223's CreateAllChiefs() above brought the 14 chiefs into
                    // existence. That earlier run logged "Stage 2 reassigned 0 chiefs
                    // to dedicated clans" — it had nothing to reassign. As a result
                    // every chief is stuck on clan_empire_north_1 (merchant_empire
                    // template's default clan) and the banner builder serializes the
                    // EMPIRE eagle for every hideout. User screenshot 01:13 confirmed:
                    // "Granny Hod's Hideout" rendered with the purple Empire banner.
                    //
                    // Fix: re-invoke OnSessionLaunched(null) NOW that chiefs exist.
                    // Safe because:
                    //   - The `starter` parameter is NEVER REFERENCED inside the
                    //     method body (verified via grep — only mention is the
                    //     parameter declaration itself), so null is safe.
                    //   - IsMinorFactionHero cleanup loop is idempotent on freshly
                    //     created chiefs (cleared count = 0).
                    //   - ChiefHeroTypePatch.TryPatch is idempotent (Harmony won't
                    //     double-attach a patch to the same method).
                    //   - Stage 2 reassign loop's first guard
                    //     `if (chief.Clan.StringId.StartsWith("bp_chief_clan_"))
                    //      continue` makes it idempotent if Stage 2 already ran.
                    //   - Stage 2b ApplyChiefRelationships sets fixed relations
                    //     (idempotent).
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "v224 OnGameLoadFinished -> re-invoking OnSessionLaunched to run Stage 2 reassignment on freshly created chiefs");
                    try { OnSessionLaunched(null); }
                    catch (System.Exception stage2Ex)
                    {
                        BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                            "v224 OnGameLoadFinished -> Stage 2 re-invoke threw "
                            + stage2Ex.GetType().Name + ": " + stage2Ex.Message);
                    }

                    // 2026-06-03 Parts 2 + 3: orphan cleanup + chief HomeSettlement
                    // migration. Both run AFTER CreateAllChiefs + Stage 2 so
                    // _runtimeChiefRefs has the canonical chief for each culture.
                    // KillOrphanChiefs scans Hero.AllAliveHeroes for stale chiefs
                    // from previous sessions that aren't in _runtimeChiefRefs and
                    // marks them dead. MigrateChiefHomeSettlementsToHideouts updates
                    // each canonical chief's HomeSettlement to point to their bandit
                    // hideout instead of the merchant_empire seed town (Diathma).
                    try { KillOrphanChiefs(); }
                    catch (System.Exception koEx)
                    {
                        BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                            "OnGameLoadFinished KillOrphanChiefs threw "
                            + koEx.GetType().Name + ": " + koEx.Message);
                    }
                    try { MigrateChiefHomeSettlementsToHideouts(); }
                    catch (System.Exception mhEx)
                    {
                        BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                            "OnGameLoadFinished MigrateChiefHomeSettlements threw "
                            + mhEx.GetType().Name + ": " + mhEx.Message);
                    }
                }
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry.OnGameLoadFinished failed: " + ex.Message);
            }
        }

        private void CreateAllChiefs()
        {
            try
            {
                if (CultureProfileRegistry.ByCultureId == null) return;
                foreach (var kv in CultureProfileRegistry.ByCultureId)
                    EnsureChiefHeroExists(kv.Key);
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry.CreateAllChiefs failed: " + ex.Message);
            }
        }

        // Wave 4.8c: create a persistent Hero entity per vanilla bandit culture so the
        // chief appears in Encyclopedia → Heroes tab (not Troops). Uses HeroCreator
        // .CreateNotable(GangLeader, town). The hero gets the culture's authored ChiefName
        // and is anchored to a town's owning clan (non-bandit) for encyclopedia visibility.
        private void EnsureChiefHeroExists(string cultureId)
        {
            try
            {
                if (string.IsNullOrEmpty(cultureId)) return;
                if (!CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out var profile)) return;
                if (profile == null || string.IsNullOrEmpty(profile.ChiefName)) return;

                // Already have a live chief for this culture? Done.
                Hero existing = GetChief(cultureId);
                if (existing != null)
                {
                    // Repair stale state from old builds — clan=null or bandit clan
                    // hides them from Encyclopedia.
                    if (existing.Clan == null || existing.Clan.IsBanditFaction)
                        TryAssignNonBanditClan(existing);
                    return;
                }

                // 2026-06-03 orphan-chief dedup. v223's empty-dict recovery used to
                // unconditionally CreateAllChiefs whenever _chiefIdByCulture.Count==0
                // — but the OLD chiefs from previous sessions are still alive in
                // Hero.AllAliveHeroes (the engine SaveData'd them). Result: each
                // save-load added 14 more chiefs to the world, all named the same as
                // the canonical chief. Symptom: encyclopedia shows N duplicate
                // "Aerlin Cliffborn" entries, keep shows N copies, the older copies
                // stay on clan_empire_north_1 (their original creation clan).
                //
                // Fix: before creating a new chief, scan Hero.AllAliveHeroes for an
                // existing hero whose Name matches the culture's expected ChiefName
                // AND has the chief-signature occupation (GoodsTrader, from the
                // merchant_empire template). If found, REUSE it — register in the
                // dicts so subsequent loads find it via GetChief.
                try
                {
                    if (Hero.AllAliveHeroes != null)
                    {
                        foreach (var candidate in Hero.AllAliveHeroes)
                        {
                            if (candidate == null || !candidate.IsAlive) continue;
                            // Match by NAME (the chief's authored ChiefName), filtered by
                            // chief-signature occupation. GoodsTrader is the merchant_empire
                            // template default; chief-signature heroes always have it because
                            // CreateSpecialHero respects the template's occupation.
                            string candName = candidate.Name?.ToString();
                            if (string.IsNullOrEmpty(candName)) continue;
                            if (candName != profile.ChiefName) continue;
                            if (candidate.Occupation != Occupation.GoodsTrader) continue;
                            // Found an orphan-chief candidate. Reuse instead of creating
                            // another. Register in dicts so future GetChief() finds it.
                            _runtimeChiefRefs[cultureId] = candidate;
                            _chiefIdByCulture[cultureId] = candidate.StringId;
                            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                                "BanditChiefRegistry: orphan-chief REUSE for culture=" + cultureId
                                + " name='" + candName + "' heroId=" + candidate.StringId
                                + " (avoided creating a duplicate)");
                            // Repair clan to non-bandit if needed (same pattern as the GetChief
                            // existing-path above).
                            if (candidate.Clan == null || candidate.Clan.IsBanditFaction)
                                TryAssignNonBanditClan(candidate);
                            return;
                        }
                    }
                }
                catch (System.Exception orphanEx)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditChiefRegistry: orphan-chief scan threw "
                        + orphanEx.GetType().Name + ": " + orphanEx.Message);
                }

                // Wave 4.9 (A+B): all 14 cultures get chiefs — vanilla AND bp_xxx.
                // We no longer require a vanilla bandit clan match (that was only used
                // for legacy clan-anchoring). The chief is always parked in a town's
                // OwnerClan in stage 1. Stage 2 (OnSessionLaunched) reassigns chief.Clan
                // to a dedicated bandit-themed clan after character creation completes.

                // Anchor settlement: any town (notables need a settlement that supports them).
                Settlement anchor = null;
                if (Settlement.All != null)
                {
                    foreach (var s in Settlement.All)
                    {
                        if (s != null && s.IsTown) { anchor = s; break; }
                    }
                }
                if (anchor == null) return;

                // Wave 4.8c-deep: use CreateSpecialHero with our XML-defined hero template
                // (bp_chief_hero_template, is_hero="true"). This is how Calradia Expanded
                // and other notable-adding mods get encyclopedia visibility — the resulting
                // Hero's CharacterObject inherits IsHero=true so the encyclopedia indexes
                // them in the Heroes tab, not Troops. CreateNotable auto-generates a
                // CharacterObject without IsHero=true, hence our previous failures.
                // Wave 4.8c-final: try vanilla notable template first (proven encyclopedia-
                // visible AND properly clothed). Fall back to our custom template only if
                // vanilla one isn't available.
                var chiefTemplate = MBObjectManager.Instance.GetObject<CharacterObject>("gangleader_aserai")
                    ?? MBObjectManager.Instance.GetObject<CharacterObject>("gangleader_empire")
                    ?? MBObjectManager.Instance.GetObject<CharacterObject>("merchant_empire")
                    ?? MBObjectManager.Instance.GetObject<CharacterObject>("bp_chief_hero_template");
                if (chiefTemplate == null)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditChiefRegistry: no template found (tried gangleader_aserai, gangleader_empire, merchant_empire, bp_chief_hero_template)");
                    return;
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry: using template '" + chiefTemplate.StringId + "' for " + cultureId);
                // Wave 4.9: Stage 1 — chief in anchor town's OwnerClan only. NO new clan
                // creation here — that crashes NameGenerator.GenerateClanName during the
                // character-creation wizard's culture-pick stage. Stage 2 reassigns Clan
                // after OnSessionLaunched (i.e. after character creation completes).
                Hero chief = HeroCreator.CreateSpecialHero(chiefTemplate, anchor,
                    anchor.OwnerClan, null, MBRandom.RandomInt(35, 55));
                if (chief == null) return;

                // Wave 4.11-fix v12 (2026-05-06): set Culture + Occupation HERE at chief
                // creation, NOT at OnSessionLaunched. The encyclopedia search index is built
                // ONCE per session between this event and OnSessionLaunched — writing these
                // fields now means the index sees the right state from the start, so search
                // AND display both work. Doing this at OnSessionLaunched (v4-v10) made the
                // live state diverge from the indexed snapshot → silent search failure.
                // See memory: feedback_bannerlord_search_index_timing.md
                try
                {
                    var banditCulture = TaleWorlds.ObjectSystem.MBObjectManager.Instance
                        .GetObject<CultureObject>(cultureId);
                    int culH = 0, culC = 0;
                    const System.Reflection.BindingFlags BF =
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.DeclaredOnly;

                    if (banditCulture != null)
                    {
                        for (var t = chief.GetType(); t != null; t = t.BaseType)
                            foreach (var f in t.GetFields(BF))
                                if (f.FieldType == typeof(TaleWorlds.Core.BasicCultureObject)
                                    || f.FieldType == typeof(CultureObject))
                                {
                                    try { f.SetValue(chief, banditCulture); culH++; } catch { }
                                }
                        if (chief.CharacterObject != null)
                            for (var t = chief.CharacterObject.GetType(); t != null; t = t.BaseType)
                                foreach (var f in t.GetFields(BF))
                                    if (f.FieldType == typeof(TaleWorlds.Core.BasicCultureObject)
                                        || f.FieldType == typeof(CultureObject))
                                    {
                                        try { f.SetValue(chief.CharacterObject, banditCulture); culC++; } catch { }
                                    }
                    }

                    // Wave 4.11-fix v13 (2026-05-06): Occupation write REMOVED. Setting
                    // Occupation.GangLeader caused GangLeaderNeedsToOffloadStolenGoodsIssueBehavior
                    // .ConditionsHold to NRE during OnNewGameCreatedPartialFollowUpEnd's
                    // IssuesCampaignBehavior pass — vanilla iterates notables, calls the
                    // issue-check on every GangLeader, and our chiefs lack the gang/hideout
                    // state the issue handler dereferences. Memory file already warned:
                    // "Setting Hero.Occupation = GangLeader — encyclopedia uses
                    //  Settlement.Notables membership, not Occupation alone".
                    // Chiefs revert to merchant_empire template's default Occupation
                    // (GoodsTrader) which is safe. Custom "Type" rendering is a separate
                    // task best done via the disabled ChiefHeroTypePatch (display-only,
                    // no Hero state mutation).

                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditChiefRegistry: at-create chief " + (chief.Name?.ToString() ?? "?")
                        + " culture=" + (chief.Culture?.StringId ?? "null")
                        + " occ=" + chief.Occupation
                        + " (cul_h=" + culH + " cul_c=" + culC + ")");
                }
                catch (System.Exception cox)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditChiefRegistry: at-create reflection failed: " + cox.Message);
                }

                // Wave 4.8c-cosmetic: copy the XML template's civilian equipment onto the
                // hero so the encyclopedia portrait shows clothes (CreateSpecialHero
                // creates a hero with empty equipment by default).
                try
                {
                    var equipBackingField = typeof(Hero).GetField("<_civilianEquipment>k__BackingField",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var battleBackingField = typeof(Hero).GetField("<_battleEquipment>k__BackingField",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var templateCivilianEquip = chiefTemplate.RandomCivilianEquipment;
                    var templateBattleEquip = chiefTemplate.RandomBattleEquipment;
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditChiefRegistry: template civilian=" + (templateCivilianEquip == null ? "NULL" : "ok")
                        + " battle=" + (templateBattleEquip == null ? "NULL" : "ok")
                        + " — using template's full equipment list");
                    // AllEquipments is internal — reflect to access the list of all rosters.
                    var allEquipsProp = typeof(TaleWorlds.Core.BasicCharacterObject).GetProperty("AllEquipments",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    if (allEquipsProp != null)
                    {
                        var allEquips = allEquipsProp.GetValue(chiefTemplate) as System.Collections.IList;
                        if (allEquips != null && allEquips.Count > 0)
                        {
                            var firstEquip = allEquips[0] as Equipment;
                            if (firstEquip != null)
                            {
                                if (equipBackingField != null) equipBackingField.SetValue(chief, firstEquip.Clone(false));
                                if (battleBackingField != null) battleBackingField.SetValue(chief, firstEquip.Clone(false));
                                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                                    "BanditChiefRegistry: copied equipment from AllEquipments[0] (count=" + allEquips.Count + ")");
                            }
                        }
                        else
                        {
                            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                                "BanditChiefRegistry: AllEquipments empty/null — falling back to RandomBattleEquipment");
                            if (templateBattleEquip != null && battleBackingField != null)
                                battleBackingField.SetValue(chief, templateBattleEquip.Clone(false));
                            if (templateCivilianEquip != null && equipBackingField != null)
                                equipBackingField.SetValue(chief, templateCivilianEquip.Clone(false));
                        }
                    }
                }
                catch (System.Exception eqx)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditChiefRegistry: equipment copy failed: " + eqx.Message);
                }

                // Wave 4.8c-deep: CreateSpecialHero doesn't add to Settlement.Notables
                // (which is what the encyclopedia uses for the Heroes tab listing).
                // The backing field is `_notablesCache` (MBList<Hero>). Reflect to add.
                try
                {
                    var nf = typeof(Settlement).GetField("_notablesCache",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (nf != null)
                    {
                        var list = nf.GetValue(anchor);
                        if (list != null)
                        {
                            var addMethod = list.GetType().GetMethod("Add",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                            if (addMethod != null)
                                addMethod.Invoke(list, new object[] { chief });
                        }
                    }
                }
                catch (System.Exception nx)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditChiefRegistry: Settlement.Notables add failed: " + nx.Message);
                }

                // Assign a non-bandit clan so Encyclopedia surfaces them.
                TryAssignNonBanditClan(chief);

                // Rename to authored ChiefName.
                var nameTo = new TextObject(profile.ChiefName);
                chief.SetName(nameTo, nameTo);

                // Wave 4.11-fix v21 (2026-05-07): use the OFFICIAL public APIs found via
                // reflection on Hero — SetHasMet() public method and ChangeState() method,
                // not backing-field reflection (which silently misses the right field name).
                // Also call ChangeState(CharacterStates.Active) so IsActive=True — the
                // diagnostic confirmed IsActive=False was leaving chiefs as "inactive"
                // which makes vanilla's encyclopedia tooltip handler hide info as Unknown.
                try { chief.SetHasMet(); } catch { }
                try { chief.IsKnownToPlayer = true; } catch { }
                try { chief.ChangeState(Hero.CharacterStates.Active); } catch { }
                // Wave 4.11-fix v27 (2026-05-07): IsMinorFactionHero REVERTED.
                // v22 set this true to hit branch 9 of HeroHelper.GetCharacterTypeName
                // ("Minor Faction Hero" instead of "Unknown"). Deep research said it was
                // safe vs DefaultMarriageModel — but MarriageOfferCampaignBehavior.DailyTickClan
                // is a SEPARATE code path that calls Clan.HasNavalNavigationCapability BEFORE
                // any IsMinorFactionHero check. Our partially-init bp_chief_clan_* clans
                // NRE inside that getter, crashing the campaign map daily-tick.
                // Safe to revert because v23 (ChiefTypeNameHelperPatch) overrides
                // __result for our chiefs UNCONDITIONALLY — they still show their per-clan
                // ChiefTitle in the Type row regardless of which precedence branch vanilla
                // would have hit. Side-effect lost: the encyclopedia hero-page Stats panel
                // no longer gets an "Occupation:" row exposed automatically. Acceptable
                // tradeoff vs daily-tick crash.
                // try { chief.IsMinorFactionHero = true; } catch { }
                try
                {
                    string backstory = !string.IsNullOrEmpty(profile.ChiefBackstory)
                        ? profile.ChiefBackstory
                        : new TextObject("{=bp_hcm_008}Chief of the {CULTURE}. Encountered through bandit-trust diplomacy.")
                            .SetTextVariable("CULTURE", BandItPlus.Origins.BanditOriginBehavior.CultureDisplay(cultureId)).ToString();
                    chief.EncyclopediaText = new TextObject("{=*}" + backstory);
                }
                catch { }

                _chiefIdByCulture[cultureId] = chief.StringId;
                _runtimeChiefRefs[cultureId] = chief; // strong reference for reliable lookup
                bool inAliveList = false;
                try
                {
                    if (Campaign.Current?.AliveHeroes != null)
                        inAliveList = Campaign.Current.AliveHeroes.Contains(chief);
                }
                catch { }
                bool charIsHero = false;
                bool charIsBasicTroop = false;
                try
                {
                    charIsHero = chief.CharacterObject?.IsHero ?? false;
                    var btProp = typeof(CharacterObject).GetProperty("IsBasicTroop",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    if (btProp != null && chief.CharacterObject != null)
                        charIsBasicTroop = (bool)btProp.GetValue(chief.CharacterObject);
                }
                catch { }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry: created Hero '" + profile.ChiefName + "' for " + cultureId
                    + " (heroId=" + chief.StringId + ", clan=" + (chief.Clan?.StringId ?? "null")
                    + ", inAliveHeroes=" + inAliveList
                    + ", isLord=" + chief.IsLord + ", isNotable=" + chief.IsNotable
                    + ", isTemplate=" + chief.IsTemplate
                    + ", charObj.IsHero=" + charIsHero
                    + ", charObj.IsBasicTroop=" + charIsBasicTroop
                    + ", charObj.StringId=" + (chief.CharacterObject?.StringId ?? "null") + ")");
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry.EnsureChiefHeroExists(" + cultureId + ") failed: " + ex.Message);
            }
        }

        // Wave 4.8e: dedicated Bandit Plus clan per culture, fully initialized via direct
        // property setters (NOT InitializeClan reflection — that caused half-init clans
        // to crash NameGenerator.GenerateClanName during character creation). All required
        // fields are set explicitly so any vanilla LINQ predicate can iterate Clan.All
        // without NREs.
        // Deterministic char-sum hash of a culture id — stable across sessions
        // (string.GetHashCode is process-randomized, so unusable for this).
        private static int CultureHash(string cultureId)
        {
            int h = 17;
            if (cultureId != null)
                foreach (char ch in cultureId) h = h * 31 + ch;
            return h & 0x7FFFFFFF;
        }

        // v174 (2026-05-18): a deterministic per-clan banner. Banner.CreateRandomClanBanner
        // with a culture-id-derived seed (not MBRandom) gives each chief clan a
        // distinct sigil/layout that is STABLE across sessions. ApplyDarkBanditColors
        // then forces the palette dark — see below.
        private static TaleWorlds.Core.Banner ResolveBanditBanner(string cultureId)
        {
            return TaleWorlds.Core.Banner.CreateRandomClanBanner(CultureHash(cultureId));
        }

        // v174: force a dark, bandit-toned colour scheme onto the clan banner so
        // it can NEVER read as the Empire banner (purple). Background is a
        // near-black / iron / dark-red; icon is a muted bone / rust / grey. The
        // pick is keyed off the culture id so the 14 clans differ — and purple
        // is deliberately absent from both palettes.
        private static void ApplyDarkBanditColors(Clan clan, string cultureId)
        {
            if (clan == null) return;
            uint[] bg = { 0xFF141414u, 0xFF2A2A2Au, 0xFF3A1414u, 0xFF1A1F0Cu };
            uint[] icon = { 0xFFC8B98Cu, 0xFF8A4A2Au, 0xFF9A9A9Au, 0xFF6E1A1Au };
            int h = CultureHash(cultureId);
            try { clan.UpdateBannerColor(bg[h % bg.Length], icon[(h >> 8) % icon.Length]); }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "v174 ApplyDarkBanditColors fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // 2026-06-03 keep-UI fix helper: find the bandit hideout whose culture
        // matches the chief's bandit-culture id. For the 5 vanilla bandit cultures
        // (forest/mountain/steppe/sea/desert) the engine ships hideouts and a
        // direct match works. For the 9 BP-authored cultures (bp_sky_raiders,
        // bp_frost_reavers, etc.) Bannerlord doesn't spawn native hideouts — so
        // direct match returns null, leaving Hero.HomeSettlement on the merchant
        // template's seed Empire town (Diathma). We map each BP culture to the
        // closest-themed vanilla hideout culture and search there as fallback.
        // Last resort: ANY hideout (avoid Diathma at all costs).
        private static readonly System.Collections.Generic.Dictionary<string, string> BpCultureFallbackToVanillaHideoutCulture =
            new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["bp_frost_reavers"]      = "sea_raiders",       // Northern coast — Nord/cold raider thematic
                ["bp_marsh_stalkers"]     = "forest_bandits",    // Forest/wetland overlap
                ["bp_highwaymen"]         = "forest_bandits",    // Highway-near-forest bandits
                ["bp_slaver_caravans"]    = "desert_bandits",    // Aserai-region slavers
                ["bp_fallen_legionaries"] = "forest_bandits",    // Empire backcountry deserters
                ["bp_sky_raiders"]        = "mountain_bandits",  // Cliff/peak clan
                ["bp_steppe_wolves"]      = "steppe_bandits",    // Khan-styled steppe
                ["bp_pagan_cult"]         = "forest_bandits",    // Pagan groves in forests
                ["looters"]               = "forest_bandits"     // Lowest-tier, anywhere
            };

        private Settlement FindCultureMatchedHideout(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return null;

            try
            {
                if (Settlement.All == null) return null;

                // Pass 1: exact culture match.
                foreach (var s in Settlement.All)
                {
                    if (s == null || !s.IsHideout) continue;
                    if (s.Culture?.StringId == cultureId) return s;
                }

                // Pass 2: BP-culture fallback mapping → vanilla hideout culture.
                if (BpCultureFallbackToVanillaHideoutCulture.TryGetValue(cultureId, out var fallbackCulture)
                    && !string.IsNullOrEmpty(fallbackCulture))
                {
                    foreach (var s in Settlement.All)
                    {
                        if (s == null || !s.IsHideout) continue;
                        if (s.Culture?.StringId == fallbackCulture) return s;
                    }
                }

                // Pass 3: any hideout (last resort, just NOT a town).
                foreach (var s in Settlement.All)
                {
                    if (s == null || !s.IsHideout) continue;
                    return s;
                }
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "FindCultureMatchedHideout(" + cultureId + ") threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            return null;
        }

        // 2026-06-03 Part 2: orphan-chief cleanup pass. After CreateAllChiefs +
        // Stage 2 reassignment, scan Hero.AllAliveHeroes for any hero that LOOKS
        // like a chief (name matches one of the 14 culture ChiefNames + occupation
        // is GoodsTrader) but is NOT the canonical entry in _runtimeChiefRefs.
        // These are orphans from previous sessions whose CreateAllChiefs added new
        // copies without cleaning up the old ones. Mark them dead so they disappear
        // from the encyclopedia, town keep, hero search, and clan member lists.
        //
        // Safety guards: name match must be EXACT against an authored ChiefName,
        // AND occupation must be GoodsTrader, AND the hero must NOT be the current
        // canonical chief for that culture. This avoids accidentally killing any
        // unrelated NPC who happens to share a name.
        private void KillOrphanChiefs()
        {
            try
            {
                if (Hero.AllAliveHeroes == null || CultureProfileRegistry.ByCultureId == null) return;
                // Build name->culture lookup of canonical chiefs.
                var canonicalByName = new Dictionary<string, Hero>(System.StringComparer.Ordinal);
                foreach (var kv in _runtimeChiefRefs)
                {
                    if (kv.Value == null || !kv.Value.IsAlive) continue;
                    string n = kv.Value.Name?.ToString();
                    if (!string.IsNullOrEmpty(n)) canonicalByName[n] = kv.Value;
                }
                // Build expected-name set from CultureProfileRegistry.
                var expectedChiefNames = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var kv in CultureProfileRegistry.ByCultureId)
                {
                    if (kv.Value != null && !string.IsNullOrEmpty(kv.Value.ChiefName))
                        expectedChiefNames.Add(kv.Value.ChiefName);
                }
                int killed = 0;
                // Snapshot Hero.AllAliveHeroes since KillCharacterAction mutates the collection.
                var allAlive = new List<Hero>(Hero.AllAliveHeroes);
                foreach (var h in allAlive)
                {
                    if (h == null || !h.IsAlive) continue;
                    string hName = h.Name?.ToString();
                    if (string.IsNullOrEmpty(hName)) continue;
                    if (!expectedChiefNames.Contains(hName)) continue;       // not a chief-name match
                    if (h.Occupation != Occupation.GoodsTrader) continue;     // not chief-signature
                    // 07-13 SAFETY (defense-in-depth): only remove a name+occupation match
                    // when a canonical chief of this EXACT name is registered AND is a
                    // DIFFERENT hero — i.e. a genuine duplicate. If no canonical is
                    // registered for this name, this hero may BE the real (sole) chief we
                    // failed to register, so never kill it. This makes it impossible to
                    // wipe a legitimate chief even if the runtime refs are incomplete.
                    if (!canonicalByName.TryGetValue(hName, out var canonical)) continue; // no canonical -> keep
                    if (canonical == h) continue;                                         // this IS canonical -> keep
                    // A different hero shares the canonical chief's name: genuine duplicate.
                    try
                    {
                        KillCharacterAction.ApplyByMurder(h, null);
                        killed++;
                        BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                            "BanditChiefRegistry: KILLED orphan chief '" + hName
                            + "' (heroId=" + h.StringId + " clan=" + (h.Clan?.StringId ?? "<null>") + ")");
                    }
                    catch (System.Exception killEx)
                    {
                        BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                            "BanditChiefRegistry: KillCharacterAction for orphan '" + hName
                            + "' threw " + killEx.GetType().Name + ": " + killEx.Message);
                    }
                }
                if (killed > 0)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditChiefRegistry: KillOrphanChiefs killed " + killed + " orphan chief hero(es)");
                }
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry.KillOrphanChiefs threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // 2026-06-03 Part 3: migrate each canonical chief's Hero.HomeSettlement to
        // their culture-matched hideout. Previously chief.HomeSettlement = merchant_empire
        // template's seed Empire town (Diathma) — visible in encyclopedia hero page
        // "Home:" stat row. Reflection-write the backing field since Hero doesn't
        // expose a public setter for HomeSettlement during a campaign.
        private void MigrateChiefHomeSettlementsToHideouts()
        {
            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                "MigrateChiefHomeSettlementsToHideouts: entering. _runtimeChiefRefs.Count="
                + (_runtimeChiefRefs?.Count ?? -1));
            try
            {
                int migrated = 0, skippedAlreadyOk = 0, skippedNoHideout = 0, failedReflect = 0;

                // v2 2026-06-03: discover the HomeSettlement-mutation API ONCE up-front,
                // log what worked, then apply to all 14 chiefs. Candidates (in order):
                //   1. Hero.UpdateHomeSettlement(Settlement) — internal vanilla method
                //   2. Field <HomeSettlement>k__BackingField on Hero
                //   3. Field <HomeSettlement>k__BackingField on Hero's base class chain
                //   4. Field _homeSettlement (lowercase internal naming convention)
                System.Reflection.MethodInfo mUpdateHome = null;
                System.Reflection.FieldInfo fHomeBacking = null;
                try
                {
                    mUpdateHome = typeof(Hero).GetMethod("UpdateHomeSettlement",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                        null, new[] { typeof(Settlement) }, null);
                }
                catch { }
                if (mUpdateHome == null)
                {
                    // Walk the Hero type hierarchy for the backing field.
                    for (var t = typeof(Hero); t != null && t != typeof(object); t = t.BaseType)
                    {
                        fHomeBacking = t.GetField("<HomeSettlement>k__BackingField",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        if (fHomeBacking != null) break;
                        fHomeBacking = t.GetField("_homeSettlement",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        if (fHomeBacking != null) break;
                    }
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "MigrateChiefHomeSettlements: API discovery — UpdateHomeSettlement method="
                    + (mUpdateHome != null ? "FOUND" : "null")
                    + " backingField=" + (fHomeBacking != null
                        ? fHomeBacking.DeclaringType?.Name + "." + fHomeBacking.Name
                        : "null"));

                if (mUpdateHome == null && fHomeBacking == null)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "MigrateChiefHomeSettlements: NO mutation API found — aborting (cannot set HomeSettlement)");
                    return;
                }

                foreach (var kv in _runtimeChiefRefs)
                {
                    if (kv.Value == null || !kv.Value.IsAlive) continue;
                    var chief = kv.Value;
                    var hideout = FindCultureMatchedHideout(kv.Key);
                    if (hideout == null) { skippedNoHideout++; continue; }
                    if (chief.HomeSettlement == hideout) { skippedAlreadyOk++; continue; }
                    try
                    {
                        if (mUpdateHome != null)
                        {
                            mUpdateHome.Invoke(chief, new object[] { hideout });
                        }
                        else if (fHomeBacking != null)
                        {
                            fHomeBacking.SetValue(chief, hideout);
                        }
                        migrated++;
                        BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                            "MigrateChiefHomeSettlements: '" + (chief.Name?.ToString() ?? "?")
                            + "' HomeSettlement: " + (chief.HomeSettlement?.StringId ?? "<null>")
                            + " (target was " + hideout.StringId + ")");
                    }
                    catch (System.Exception migEx)
                    {
                        failedReflect++;
                        BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                            "MigrateChiefHomeSettlements: mutation for '"
                            + (chief.Name?.ToString() ?? "?") + "' threw "
                            + migEx.GetType().Name + ": " + migEx.Message);
                    }
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "MigrateChiefHomeSettlements: complete — migrated=" + migrated
                    + " alreadyOk=" + skippedAlreadyOk
                    + " noHideout=" + skippedNoHideout
                    + " failed=" + failedReflect);
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "MigrateChiefHomeSettlements OUTER threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private Clan GetOrCreateChiefClan(string cultureId, Settlement homeSettlement)
        {
            try
            {
                string clanId = "bp_chief_clan_" + cultureId;
                var existing = MBObjectManager.Instance.GetObject<Clan>(clanId);
                if (existing != null)
                {
                    // 2026-06-03 migration: existing dedicated clans from prior
                    // save versions have HomeSettlement set to the merchant_empire
                    // template's seed town (Diathma) — causing all 14 chiefs to
                    // appear in that town's keep. If the existing clan's home
                    // isn't a hideout, re-assign to the culture-matched hideout
                    // passed in by the caller.
                    try
                    {
                        if (homeSettlement != null
                            && homeSettlement.IsHideout
                            && (existing.HomeSettlement == null || !existing.HomeSettlement.IsHideout))
                        {
                            existing.SetInitialHomeSettlement(homeSettlement);
                            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                                "BanditChiefRegistry: migrated existing clan '" + clanId
                                + "' HomeSettlement -> " + homeSettlement.StringId
                                + " (was " + (existing.HomeSettlement?.StringId ?? "<null>") + ")");
                        }
                    }
                    catch (System.Exception migEx)
                    {
                        BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                            "BanditChiefRegistry: existing-clan home migration for '" + clanId
                            + "' threw " + migEx.GetType().Name + ": " + migEx.Message);
                    }
                    return existing;
                }

                var clan = Clan.CreateClan(clanId);
                if (clan == null) return null;

                // Resolve culture: prefer the bandit culture, fall back to home settlement's culture.
                var culture = MBObjectManager.Instance.GetObject<CultureObject>(cultureId);
                if (culture == null) culture = homeSettlement?.Culture;
                if (culture == null && Settlement.All != null)
                {
                    foreach (var s in Settlement.All)
                        if (s.IsTown && s.Culture != null) { culture = s.Culture; break; }
                }
                if (culture == null) return null; // can't safely create without culture

                // CRITICAL: set all required fields via DIRECT public setters BEFORE any
                // vanilla code can iterate Clan.All. Order matters — Name and Culture are
                // accessed first by NameGenerator.GenerateClanName.
                var clanName = new TextObject(HumanizeCultureForClan(cultureId));
                // Name + InformalName setters are internal — write backing fields.
                try
                {
                    var fName = typeof(Clan).GetField("<Name>k__BackingField",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var fInformal = typeof(Clan).GetField("<InformalName>k__BackingField",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (fName == null) fName = typeof(MBObjectBase).GetField("<Name>k__BackingField",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (fName != null) fName.SetValue(clan, clanName);
                    if (fInformal != null) fInformal.SetValue(clan, clanName);
                }
                catch { }
                clan.Culture = culture;
                clan.Banner = ResolveBanditBanner(cultureId);
                ApplyDarkBanditColors(clan, cultureId);

                // Wave 4.10c: clan-page encyclopedia description. Reuses authored
                // CampIntro text (per-culture lore about the camp/ethos) — gives each
                // clan its own thematic description on the encyclopedia clan page.
                if (CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out var clanProfile)
                    && clanProfile != null)
                {
                    string descText = !string.IsNullOrEmpty(clanProfile.ClanHistory)
                        ? clanProfile.ClanHistory
                        : clanProfile.CampIntro;
                    if (!string.IsNullOrEmpty(descText))
                    {
                        try
                        {
                            var fEncText = typeof(Clan).GetField("<EncyclopediaText>k__BackingField",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                            if (fEncText != null)
                                fEncText.SetValue(clan, new TextObject("{=*}" + descText));
                        }
                        catch { }
                    }
                }
                clan.Color = culture.Color;
                clan.Color2 = culture.Color2;
                try { clan.SetInitialHomeSettlement(homeSettlement); } catch { }
                try
                {
                    var fInit = typeof(MBObjectBase).GetField("<IsInitialized>k__BackingField",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (fInit != null) fInit.SetValue(clan, true);
                }
                catch { }

                // Mark as non-bandit minor faction.
                try
                {
                    var fBandit = typeof(Clan).GetField("<IsBanditFaction>k__BackingField",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (fBandit != null) fBandit.SetValue(clan, false);
                    var fMinor = typeof(Clan).GetField("<IsMinorFaction>k__BackingField",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (fMinor != null) fMinor.SetValue(clan, true);
                }
                catch { }

                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry: created clan '" + clanId + "' name='" + clanName
                    + "' culture=" + culture.StringId + " homeSettlement=" + homeSettlement?.StringId);
                return clan;
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry: GetOrCreateChiefClan(" + cultureId + ") failed: "
                    + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        // Wave 4.9-fix: per-culture equipment-source CharacterObject lookup. The chief
        // was created with merchant_empire (generic notable), but we want each culture's
        // chief in their own thematic armor/weapons. Use the existing bandit chieftain/
        // boss templates as the equipment source (no XML changes needed).
        // Wave 4.10-fix: corrected to actual top-tier (level 27) troop StringIds verified
        // against BandIt Plus's spnpccharacters.xml. Vanilla cultures use null and fall
        // through to FindHighestTierTroopForCulture which scans CharacterObject.All.
        private static readonly Dictionary<string, string> _cultureEquipTemplate = new Dictionary<string, string>
        {
            { "bp_frost_reavers",      "bp_frost_huscarl"     },
            { "bp_marsh_stalkers",     "bp_marsh_chieftain"   },
            { "bp_highwaymen",         "bp_road_warden"       },
            { "bp_slaver_caravans",    "bp_slaver_overseer"   },
            { "bp_fallen_legionaries", "bp_fallen_centurion"  },
            { "bp_sky_raiders",        "bp_cliff_warrior"     },
            { "bp_steppe_wolves",      "bp_steppe_chieftain"  },
            { "bp_pagan_cult",         "bp_high_druid"        },
            { "looters",               "looter"               },
            // Vanilla bandit cultures: leave null to trigger runtime culture-based lookup
        };

        private static CharacterObject FindHighestTierTroopForCulture(string cultureId)
        {
            CharacterObject best = null;
            int bestTier = -1;
            try
            {
                foreach (var co in CharacterObject.All)
                {
                    if (co == null) continue;
                    if (co.Culture?.StringId != cultureId) continue;
                    if (co.IsHero) continue;
                    if (co.Tier > bestTier) { bestTier = co.Tier; best = co; }
                }
            }
            catch { }
            return best;
        }

        private static void ApplyCultureEquipment(Hero chief, string cultureId)
        {
            if (chief == null || string.IsNullOrEmpty(cultureId)) return;
            CharacterObject source = null;
            string sourceLabel = null;
            if (_cultureEquipTemplate.TryGetValue(cultureId, out var sourceId)
                && !string.IsNullOrEmpty(sourceId))
            {
                source = MBObjectManager.Instance.GetObject<CharacterObject>(sourceId);
                sourceLabel = sourceId;
            }
            // Fallback: scan CharacterObject.All for highest-tier non-hero troop in culture.
            if (source == null)
            {
                source = FindHighestTierTroopForCulture(cultureId);
                if (source != null) sourceLabel = source.StringId + " (runtime-found)";
            }
            if (source == null)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry: no equipment source found for " + cultureId
                    + " (tried '" + (sourceId ?? "<null>") + "' + runtime culture scan)");
                return;
            }
            try
            {
                var allEquipsProp = typeof(TaleWorlds.Core.BasicCharacterObject).GetProperty("AllEquipments",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (allEquipsProp == null) return;
                var allEquips = allEquipsProp.GetValue(source) as System.Collections.IList;
                if (allEquips == null || allEquips.Count == 0) return;
                var firstEquip = allEquips[0] as Equipment;
                if (firstEquip == null) return;
                var civilianBacking = typeof(Hero).GetField("<_civilianEquipment>k__BackingField",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var battleBacking = typeof(Hero).GetField("<_battleEquipment>k__BackingField",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (civilianBacking != null) civilianBacking.SetValue(chief, firstEquip.Clone(false));
                if (battleBacking != null) battleBacking.SetValue(chief, firstEquip.Clone(false));
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry: applied " + sourceLabel + " equipment to chief of " + cultureId);
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry.ApplyCultureEquipment(" + cultureId + ") failed: " + ex.Message);
            }
        }

        // Wave 4.10: per-culture skill profile. Bandit chiefs are war/raid leaders so all
        // get high Tactics/Leadership/Roguery; the weapon skill emphasis varies by culture.
        private static void ApplyChiefSkills(Hero chief, string cultureId)
        {
            if (chief == null) return;
            try
            {
                // Common bandit-leader baseline
                chief.SetSkillValue(DefaultSkills.Tactics, 200);
                chief.SetSkillValue(DefaultSkills.Leadership, 230);
                chief.SetSkillValue(DefaultSkills.Roguery, 280);
                chief.SetSkillValue(DefaultSkills.Athletics, 180);
                chief.SetSkillValue(DefaultSkills.Riding, 160);
                chief.SetSkillValue(DefaultSkills.Charm, 140);
                chief.SetSkillValue(DefaultSkills.Scouting, 150);
                chief.SetSkillValue(DefaultSkills.OneHanded, 180);

                // Culture-specific weapon emphasis
                switch (cultureId)
                {
                    case "bp_marsh_stalkers":
                    case "forest_bandits":
                        chief.SetSkillValue(DefaultSkills.Bow, 250);
                        chief.SetSkillValue(DefaultSkills.Crossbow, 200);
                        break;
                    case "bp_steppe_wolves":
                    case "steppe_bandits":
                        chief.SetSkillValue(DefaultSkills.Bow, 240);
                        chief.SetSkillValue(DefaultSkills.Riding, 260);
                        chief.SetSkillValue(DefaultSkills.Throwing, 200);
                        break;
                    case "bp_frost_reavers":
                    case "sea_raiders":
                    case "mountain_bandits":
                        chief.SetSkillValue(DefaultSkills.TwoHanded, 250);
                        chief.SetSkillValue(DefaultSkills.OneHanded, 220);
                        chief.SetSkillValue(DefaultSkills.Throwing, 180);
                        break;
                    case "bp_fallen_legionaries":
                        chief.SetSkillValue(DefaultSkills.Polearm, 260);
                        chief.SetSkillValue(DefaultSkills.OneHanded, 230);
                        chief.SetSkillValue(DefaultSkills.Tactics, 280);
                        break;
                    case "bp_sky_raiders":
                        chief.SetSkillValue(DefaultSkills.TwoHanded, 240);
                        chief.SetSkillValue(DefaultSkills.Athletics, 240);
                        break;
                    case "bp_highwaymen":
                        chief.SetSkillValue(DefaultSkills.OneHanded, 220);
                        chief.SetSkillValue(DefaultSkills.Charm, 220);
                        chief.SetSkillValue(DefaultSkills.Trade, 200);
                        break;
                    case "bp_slaver_caravans":
                    case "desert_bandits":
                        chief.SetSkillValue(DefaultSkills.Polearm, 220);
                        chief.SetSkillValue(DefaultSkills.Riding, 240);
                        chief.SetSkillValue(DefaultSkills.Trade, 180);
                        break;
                    case "bp_pagan_cult":
                        chief.SetSkillValue(DefaultSkills.Charm, 240);
                        chief.SetSkillValue(DefaultSkills.Medicine, 220);
                        chief.SetSkillValue(DefaultSkills.Roguery, 300);
                        break;
                    case "looters":
                        // Peasant chief — lower across the board
                        chief.SetSkillValue(DefaultSkills.OneHanded, 120);
                        chief.SetSkillValue(DefaultSkills.Tactics, 100);
                        chief.SetSkillValue(DefaultSkills.Leadership, 140);
                        break;
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry: applied skill profile for " + cultureId);
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry.ApplyChiefSkills(" + cultureId + ") failed: " + ex.Message);
            }
        }

        // Wave 4.10: pair-wise friendships between bandit chiefs (loose alliances) and
        // some vanilla-lord enemies so the encyclopedia Friends/Enemies sections have data.
        private static void ApplyChiefRelationships()
        {
            try
            {
                var chiefs = new List<Hero>();
                foreach (var kv in _runtimeChiefRefs)
                    if (kv.Value != null && kv.Value.IsAlive) chiefs.Add(kv.Value);
                if (chiefs.Count < 2) return;

                // Loose-alliance pairings: each chief is friend with the next (circular)
                for (int i = 0; i < chiefs.Count; i++)
                {
                    var a = chiefs[i];
                    var b = chiefs[(i + 1) % chiefs.Count];
                    if (a == b) continue;
                    try { ChangeRelationAction.ApplyRelationChangeBetweenHeroes(a, b, 30, false); } catch { }
                }

                // Pick a few vanilla lord enemies per chief — random from major faction lords.
                var vanillaLords = new List<Hero>();
                foreach (var h in Hero.AllAliveHeroes)
                {
                    if (h == null || h.IsHumanPlayerCharacter) continue;
                    if (h.Clan == null || h.Clan.IsBanditFaction) continue;
                    if (!h.IsLord) continue;
                    vanillaLords.Add(h);
                    if (vanillaLords.Count > 60) break; // sample cap
                }
                if (vanillaLords.Count == 0) return;
                int li = 0;
                foreach (var chief in chiefs)
                {
                    for (int e = 0; e < 2; e++)
                    {
                        var enemy = vanillaLords[(li++) % vanillaLords.Count];
                        if (enemy == chief) continue;
                        try { ChangeRelationAction.ApplyRelationChangeBetweenHeroes(chief, enemy, -25, false); } catch { }
                    }
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry: relationships applied for " + chiefs.Count + " chiefs");
            }
            catch { }
        }

        // Wave 4.10: declare war on a thematic kingdom per culture so the chief's clan
        // shows "at war with X" in encyclopedia and AI behaves accordingly.
        // Wave 4.10-fix: culture-based kingdom lookup. Vanilla kingdom StringIds vary
        // by save (empire_s, empire_w, etc.) but their CULTURE.StringId is stable. Map
        // each bandit culture to a target enemy culture; find the kingdom by culture.
        private static readonly Dictionary<string, string> _cultureEnemyCulture = new Dictionary<string, string>
        {
            { "bp_frost_reavers",      "sturgia"  },
            { "bp_marsh_stalkers",     "vlandia"  },
            { "bp_highwaymen",         "empire"   },
            { "bp_slaver_caravans",    "aserai"   },
            { "bp_fallen_legionaries", "empire"   },
            { "bp_sky_raiders",        "vlandia"  },
            { "bp_steppe_wolves",      "khuzait"  },
            { "bp_pagan_cult",         "empire"   },
            { "forest_bandits",        "battania" },
            { "mountain_bandits",      "sturgia"  },
            { "steppe_bandits",        "khuzait"  },
            { "sea_raiders",           "sturgia"  },
            { "desert_bandits",        "aserai"   },
            // looters intentionally not at war
        };

        private static void DeclareClanWar(Clan clan, string cultureId)
        {
            if (clan == null) return;
            if (!_cultureEnemyCulture.TryGetValue(cultureId, out var enemyCulture)) return;
            Kingdom enemy = null;
            if (Kingdom.All != null)
            {
                foreach (var k in Kingdom.All)
                {
                    if (k == null || k.IsEliminated) continue;
                    if (k.Culture?.StringId == enemyCulture) { enemy = k; break; }
                }
            }
            if (enemy == null)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry: no kingdom with culture '" + enemyCulture + "' found for " + cultureId);
                return;
            }
            try
            {
                FactionManager.DeclareWar(clan, enemy);
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry: declared war " + clan.StringId + " vs " + enemy.StringId
                    + " (culture=" + enemyCulture + ")");
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry.DeclareClanWar(" + cultureId + ") failed: " + ex.Message);
            }
        }

        private static string HumanizeCultureForClan(string cultureId)
        {
            // "mountain_bandits" -> "Mountain Bandits"
            if (string.IsNullOrEmpty(cultureId)) return BandItPlus.Localization.Get("bp_chiefregistry_001", "Bandit Clan");
            string id = cultureId.StartsWith("bp_") ? cultureId.Substring(3) : cultureId;
            var parts = id.Split('_');
            var sb = new System.Text.StringBuilder();
            foreach (var p in parts)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToUpperInvariant(p[0]));
                if (p.Length > 1) sb.Append(p.Substring(1));
            }
            if (sb.Length == 0) return BandItPlus.Localization.Get("bp_chiefregistry_001", "Bandit Clan");
            // Emit a {=id} so the clan name LOCALIZES: bp_clan_<id> lives in the strings
            // table (EN + RU). new TextObject(...) at the sole caller parses the prefix; a
            // missing id falls back to the inline humanized English. Without this the
            // chief-clan names rendered raw English even with the RU strings present.
            return "{=bp_clan_" + id + "}" + sb.ToString();
        }

        private static void TryAssignNonBanditClan(Hero hero)
        {
            try
            {
                if (hero == null) return;
                if (hero.Clan != null && !hero.Clan.IsBanditFaction) return;
                if (Settlement.All == null) return;
                foreach (var s in Settlement.All)
                {
                    if (s == null || !s.IsTown) continue;
                    if (s.OwnerClan != null && !s.OwnerClan.IsBanditFaction)
                    { hero.Clan = s.OwnerClan; return; }
                }
            }
            catch { }
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("_bp_chiefIdByCulture", ref _chiefIdByCulture);
            if (_chiefIdByCulture == null) _chiefIdByCulture = new Dictionary<string, string>();
        }

        private void OnDailyTick()
        {
            // If the promoted chief died, drop the entry so the next encounter promotes
            // a replacement. We don't proactively pick a new one — wait for next contact.
            try
            {
                if (_chiefIdByCulture == null) return;
                var stale = new List<string>();
                foreach (var kv in _chiefIdByCulture)
                {
                    var hero = MBObjectManager.Instance.GetObject<Hero>(kv.Value);
                    if (hero == null || !hero.IsAlive) stale.Add(kv.Key);
                }
                foreach (var k in stale) _chiefIdByCulture.Remove(k);
            }
            catch { }
        }

        // Wave 4.8c-fix: enumerate live chief Heroes (used by encyclopedia patch).
        public IEnumerable<Hero> GetAllChiefHeroes()
        {
            if (_runtimeChiefRefs == null) yield break;
            foreach (var kv in _runtimeChiefRefs)
                if (kv.Value != null && kv.Value.IsAlive) yield return kv.Value;
        }

        // v222 (2026-06-01): expose the (cultureId, chief) pairs directly from
        // _runtimeChiefRefs for callers that need a per-culture lookup but where
        // GetChief(cultureId) is unreliable after save/load (StringId collision
        // issue). Used by the hideout banner builder which was returning 0 hideouts
        // because GetChief() returned null for every culture after a reload.
        public IEnumerable<KeyValuePair<string, Hero>> GetAllChiefByCulture()
        {
            if (_runtimeChiefRefs == null) yield break;
            foreach (var kv in _runtimeChiefRefs)
                if (kv.Value != null && kv.Value.IsAlive) yield return kv;
        }

        // Kept for backward-compat. Returns Hero.StringIds — but the encyclopedia patch
        // should use GetAllChiefHeroes() instead because GetObject<Hero>(stringId) is
        // unreliable due to CharacterObject/Hero StringId collisions.
        public IEnumerable<string> GetAllChiefStringIds()
        {
            foreach (var h in GetAllChiefHeroes()) yield return h.StringId;
        }

        public bool IsRegisteredChief(Hero hero)
        {
            if (hero == null) return false;
            foreach (var kv in _runtimeChiefRefs)
                if (kv.Value == hero) return true;
            return false;
        }

        // Public lookup used by BanditDialogManager to resolve the QuestGiver.
        // Prefers the runtime strong reference (reliable) over MBObjectManager lookup.
        public Hero GetChief(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return null;
            if (_runtimeChiefRefs.TryGetValue(cultureId, out var refd)
                && refd != null && refd.IsAlive)
                return refd;
            // Fall back to StringId lookup (cross-session persistence).
            if (_chiefIdByCulture == null || !_chiefIdByCulture.TryGetValue(cultureId, out var id)) return null;
            var hero = MBObjectManager.Instance.GetObject<Hero>(id);
            return (hero != null && hero.IsAlive) ? hero : null;
        }

        // Public mutator: promote the given encounter-leader hero to the culture's named
        // chief. Idempotent — if a different hero is already promoted for this culture
        // and is still alive, returns that hero unchanged. If no chief or current chief
        // is dead, promotes the candidate.
        //
        // Returns the chief hero (either pre-existing or newly-promoted), or null if no
        // promotion was possible (candidate null, profile missing, etc.).
        public Hero EnsureChiefNamed(Hero candidate, string cultureId)
        {
            try
            {
                if (candidate == null || string.IsNullOrEmpty(cultureId)) return candidate;
                if (!CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out var profile)) return candidate;
                if (profile == null || string.IsNullOrEmpty(profile.ChiefName)) return candidate;

                // If we already have a live chief for this culture, return them — don't
                // rename a fresh candidate (each bandit party has a different leader
                // hero, but we want a STABLE chief, so we keep the first one).
                Hero existing = GetChief(cultureId);
                if (existing != null) return existing;

                // Promote candidate.
                var nameTo = new TextObject(profile.ChiefName);
                candidate.SetName(nameTo, nameTo);

                // Set Occupation to GangLeader for encyclopedia visibility. Hero.Occupation
                // is read-only in 1.2.x — backing field is "_occupation".
                try
                {
                    var occField = typeof(Hero).GetField("_occupation",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (occField != null) occField.SetValue(candidate, Occupation.GangLeader);
                }
                catch { }

                // Mark as known so the Encyclopedia surfaces them immediately.
                try
                {
                    var hasMetField = typeof(Hero).GetField("_hasMet",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (hasMetField != null) hasMetField.SetValue(candidate, true);
                }
                catch { }

                _chiefIdByCulture[cultureId] = candidate.StringId;
                _runtimeChiefRefs[cultureId] = candidate;
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry: promoted " + profile.ChiefName + " for " + cultureId
                    + " (heroId=" + candidate.StringId + ", clan="
                    + (candidate.Clan?.StringId ?? "null") + ", occ=" + candidate.Occupation
                    + ", isNotable=" + candidate.IsNotable + ")");
                return candidate;
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditChiefRegistry.EnsureChiefNamed(" + cultureId + ") failed: "
                    + ex.GetType().Name + ": " + ex.Message);
                return candidate;
            }
        }
    }
}
