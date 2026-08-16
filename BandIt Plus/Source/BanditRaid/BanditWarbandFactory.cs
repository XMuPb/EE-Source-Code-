using System;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;
using BandItPlus.HideoutVisit;

namespace BandItPlus.BanditRaid
{
    // Wave 4.x — shared bandit-warband spawn factory. A faithful, isolated copy of the proven, crash-hardened
    // spawn from BanditRaidBehavior.SpawnRaidParty (+ its TryFindValidSpawnPos / TrySetLeaderHero helpers),
    // used by BanditBlockadeBehavior. The original raid spawn is intentionally LEFT UNTOUCHED (zero risk to the
    // working raids); both could be migrated onto this factory later.
    //
    // WHY the crash-prevention matters: vanilla bandit cultures (looters/sea_raiders/...) have no LeaderHero, and
    // a leaderless bandit party crashes WarPartyComponent.OnFinalize when it dies in a MapEvent. So we MUST assign
    // a chief Hero (BanditChiefRegistry) after creation, pass the matching bandit Clan + a valid party template,
    // and validate terrain — exactly as the raid spawn does.
    public static class BanditWarbandFactory
    {
        private static bool _leaderLookupAttempted;
        private static FieldInfo _cachedLeaderField;
        private static PropertyInfo _cachedLeaderProp;

        // Spawn a crash-safe bandit warband at the given hideout. Returns the party, or null if it could not be
        // spawned safely (no chief, no clan, no template, no walkable terrain, or leader assignment failed).
        public static MobileParty SpawnWarband(CultureObject culture, Settlement hideoutSettlement)
        {
            try
            {
                if (culture == null || hideoutSettlement == null || hideoutSettlement.Hideout == null) return null;

                // Chief leader (REQUIRED — a leaderless bandit party crashes on death).
                Hero leaderHero = null;
                try { leaderHero = BandItPlus.Heroes.BanditChiefRegistry.Instance?.GetChief(culture.StringId); }
                catch { }
                if (leaderHero == null || !leaderHero.IsAlive)
                {
                    Log("no chief hero for " + culture.StringId + " — skipping warband spawn (would crash on death).");
                    return null;
                }

                // Matching bandit Clan (CreateBanditParty NREs with a null clan for vanilla bandit cultures).
                Clan banditClan = null;
                try
                {
                    foreach (var c in Clan.All)
                    {
                        if (c == null || !c.IsBanditFaction) continue;
                        if (c.Culture == culture) { banditClan = c; break; }
                    }
                }
                catch { }
                if (banditClan == null)
                {
                    try { if (leaderHero.Clan != null && leaderHero.Clan.IsBanditFaction) banditClan = leaderHero.Clan; }
                    catch { }
                }
                if (banditClan == null)
                {
                    Log("no matching bandit Clan for " + culture.StringId + " — skipping warband spawn.");
                    return null;
                }

                // Party template (boss -> default -> looters fallback).
                PartyTemplateObject template = null;
                try { template = culture.BanditBossPartyTemplate; } catch { }
                if (template == null) { try { template = culture.DefaultPartyTemplate; } catch { } }
                if (template == null) { try { template = MBObjectManager.Instance.GetObject<PartyTemplateObject>("looters_template"); } catch { } }
                if (template == null) { Log("no party template for " + culture.StringId + " — skipping warband spawn."); return null; }

                // Terrain-validated spawn position near the hideout.
                CampaignVec2 spawnPos;
                if (!TryFindValidSpawnPos(hideoutSettlement.GetPosition2D, out spawnPos))
                {
                    Log("no walkable spawn position near " + (hideoutSettlement.Name?.ToString() ?? "?") + " — skipping warband spawn.");
                    return null;
                }

                string partyId = "bp_warband_" + culture.StringId + "_" + ((int)CampaignTime.Now.ToHours).ToString();

                MobileParty warband = null;
                try
                {
                    warband = BanditPartyComponent.CreateBanditParty(
                        partyId, banditClan, hideoutSettlement.Hideout, /* isBossParty: */ true, template, spawnPos);
                }
                catch (Exception spawnEx)
                {
                    Log("CreateBanditParty threw " + spawnEx.GetType().Name + ": " + spawnEx.Message + " (culture=" + culture.StringId + ")");
                    return null;
                }
                if (warband == null) return null;

                if (!TrySetLeaderHero(warband, leaderHero))
                {
                    Log("leader assignment failed — abandoning warband " + partyId + " (would crash on death).");
                    return null;
                }
                return warband;
            }
            catch (Exception ex)
            {
                Log("SpawnWarband error: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        // Multi-strategy leader assignment (copied from BanditRaidBehavior.TrySetLeaderHero, made static).
        private static bool TrySetLeaderHero(MobileParty party, Hero hero)
        {
            if (party == null || hero == null) return false;
            try
            {
                // Strategy 0 — official ChangePartyLeader (preferred when present).
                try
                {
                    var changeMethod = typeof(MobileParty).GetMethod("ChangePartyLeader",
                        BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(Hero) }, null);
                    if (changeMethod != null) { changeMethod.Invoke(party, new object[] { hero }); return true; }
                }
                catch { /* fall through */ }

                if (!_leaderLookupAttempted)
                {
                    var t = typeof(MobileParty);
                    string[] fieldCandidates = { "_leaderHero", "<LeaderHero>k__BackingField", "_partyLeader", "_leader" };
                    foreach (var name in fieldCandidates)
                    {
                        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                        if (f != null && f.FieldType == typeof(Hero)) { _cachedLeaderField = f; break; }
                    }
                    if (_cachedLeaderField == null)
                    {
                        var p = t.GetProperty("LeaderHero", BindingFlags.Instance | BindingFlags.Public);
                        if (p != null && p.CanWrite) _cachedLeaderProp = p;
                    }
                    if (_cachedLeaderField == null && _cachedLeaderProp == null)
                    {
                        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                        {
                            if (f.FieldType != typeof(Hero)) continue;
                            if (!f.Name.ToLower().Contains("leader")) continue;
                            _cachedLeaderField = f; break;
                        }
                    }
                    _leaderLookupAttempted = true;
                }

                if (_cachedLeaderField != null) { _cachedLeaderField.SetValue(party, hero); return true; }
                if (_cachedLeaderProp != null) { _cachedLeaderProp.SetValue(party, hero); return true; }

                // Hero-side ownership fallback.
                bool setHeroPartySide = false;
                try
                {
                    var pblProp = typeof(Hero).GetProperty("PartyBelongedTo", BindingFlags.Instance | BindingFlags.Public);
                    if (pblProp != null && pblProp.CanWrite) { pblProp.SetValue(hero, party); setHeroPartySide = true; }
                    else
                    {
                        var pblField = typeof(Hero).GetField("_partyBelongedTo", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (pblField != null) { pblField.SetValue(hero, party); setHeroPartySide = true; }
                    }
                }
                catch { }

                // PartyBase.SetCustomOwner fallback.
                try
                {
                    var partyBase = party.Party;
                    if (partyBase != null)
                    {
                        var setOwnerMethod = partyBase.GetType().GetMethod("SetCustomOwner",
                            BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(Hero) }, null);
                        if (setOwnerMethod != null) { setOwnerMethod.Invoke(partyBase, new object[] { hero }); return true; }
                    }
                }
                catch { }

                if (setHeroPartySide) return true;
                Log("all leader-resolution strategies failed (engine version mismatch).");
                return false;
            }
            catch (Exception ex) { Log("TrySetLeaderHero threw " + ex.GetType().Name + ": " + ex.Message); return false; }
        }

        // Terrain validation (copied from BanditRaidBehavior.TryFindValidSpawnPos, made static).
        private static bool TryFindValidSpawnPos(Vec2 anchor, out CampaignVec2 result)
        {
            result = new CampaignVec2(anchor, /* isOnLand: */ true);
            try
            {
                var sceneWrapper = Campaign.Current?.MapSceneWrapper;
                if (sceneWrapper == null) return true;

                bool valid = false;
                try { valid = sceneWrapper.GetFaceIndex(result).IsValid(); } catch { }
                if (valid) return true;

                float[] offsets = { -0.5f, 0.0f, 0.5f, 1.0f };
                for (int i = 0; i < offsets.Length; i++)
                {
                    for (int j = 0; j < offsets.Length; j++)
                    {
                        Vec2 candVec = anchor + new Vec2(offsets[i], offsets[j]);
                        CampaignVec2 cand = new CampaignVec2(candVec, /* isOnLand: */ true);
                        try { if (sceneWrapper.GetFaceIndex(cand).IsValid()) { result = cand; return true; } }
                        catch { /* try next */ }
                    }
                }
                return false;
            }
            catch (Exception ex) { Log("TryFindValidSpawnPos error: " + ex.GetType().Name + ": " + ex.Message); return true; }
        }

        private static MethodInfo _cachedGoToPointMethod;
        private static bool _goToPointMethodChecked;

        // Positional move that BYPASSES settlement-AI (ported from BanditRaidBehavior fix19). The vanilla
        // bandit-AI override that freezes parties in micro-oscillation only acts on settlement-targeted
        // moves; a pure pathfind to a Vec2 isn't second-guessed. Returns false if the engine lacks
        // SetMoveGoToPoint(Vec2) (caller then falls back to SetMoveGoToSettlement).
        public static bool MoveToPoint(MobileParty party, Vec2 point)
        {
            if (party == null) return false;
            try
            {
                if (!_goToPointMethodChecked)
                {
                    _cachedGoToPointMethod = typeof(MobileParty).GetMethod(
                        "SetMoveGoToPoint", BindingFlags.Instance | BindingFlags.Public,
                        null, new[] { typeof(Vec2) }, null);
                    _goToPointMethodChecked = true;
                    Log(_cachedGoToPointMethod != null
                        ? "Resolved SetMoveGoToPoint(Vec2) — bypassing settlement-AI"
                        : "SetMoveGoToPoint(Vec2) not found — callers fall back to SetMoveGoToSettlement");
                }
                if (_cachedGoToPointMethod == null) return false;
                _cachedGoToPointMethod.Invoke(party, new object[] { point });
                return true;
            }
            catch (Exception ex) { Log("MoveToPoint threw " + ex.GetType().Name + ": " + ex.Message); return false; }
        }

        // Keep a blockading warband from being destroyed by AI parties (garrison/militia/patrols) as it
        // marches up to and parks outside a hostile town/castle. Only the PLAYER should drive it off, so we
        // make every other party ignore it. Time-based (expires in 2h) — the caller re-asserts each tick, and
        // it lapses naturally once the blockade ends and the caller stops refreshing it.
        public static void ProtectFromAi(MobileParty party)
        {
            try { if (party != null) party.IgnoreByOtherPartiesTill(CampaignTime.HoursFromNow(2f)); } catch { }
        }

        private static void Log(string msg)
        {
            try { HideoutPeacefulVisitState.Log("[BP-Warband] " + msg); } catch { }
        }
    }
}
