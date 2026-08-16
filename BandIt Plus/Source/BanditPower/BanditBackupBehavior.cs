using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using BandItPlus.HideoutVisit;

namespace BandItPlus.BanditPower
{
    // Wave 4.29.0 — "call for backup". When the player fights a looter/bandit party,
    // nearby bandit/looter parties roll to answer.
    //
    // Spike 2026-06-22: there is NO public API to add a separate party to an in-progress
    // MapEvent, and the map clock pauses mid-mission, so SetMoveEngageParty convergence
    // never reaches the current fight (the party just chases and the player sees nothing).
    // So instead we FOLD each answering party's troops into the enemy force at battle start
    // — they deploy as reinforcement waves in THIS battle — and consume the answering party.
    // The WARBAND dossier reads CurrentBackupCount to show the in-battle "+N answered" banner.
    //
    // Transient: nothing is [SaveableField]. Chance / cap / on-off are Console settings.
    public class BanditBackupBehavior : CampaignBehaviorBase
    {
        private const float Radius = 14f; // map units; tuned constant

        // How many answering parties folded into the CURRENT player battle (dossier reads this).
        private static int _arrivedCount;
        public static int CurrentBackupCount => _arrivedCount;
        private static int _arrivedTroops;
        public static int CurrentBackupTroops => _arrivedTroops;

        // Wave 4.30.x — troops are NOT folded into the enemy roster (that put them in the
        // battle's INITIAL deployment so they spawned immediately; the roster snapshot is
        // frozen at battle start — spike 2026-06-22). Instead the answering parties' troops
        // are recorded here and spawned as a real timed reinforcement WAVE by the MissionView
        // via Mission.SpawnTroop. _originParty is the in-battle enemy party used as spawn origin.
        private static readonly System.Collections.Generic.List<CharacterObject> _pendingTroops =
            new System.Collections.Generic.List<CharacterObject>();
        public static System.Collections.Generic.List<CharacterObject> PendingTroops => _pendingTroops;
        private static PartyBase _originParty;
        public static PartyBase OriginParty => _originParty;

        // TASK B1 — when true, the recorded wave reinforces the PLAYER's side instead of the
        // enemy bandit side (only set when the player is bandit-allied and NO enemy bandit was
        // present, so the enemy path is untouched). MissionView reads this to pick the team.
        private static bool _reinforcePlayerSide;
        public static bool ReinforcePlayerSide => _reinforcePlayerSide;

        public override void RegisterEvents()
        {
            CampaignEvents.MapEventStarted.AddNonSerializedListener(this, OnMapEventStarted);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
        }

        public override void SyncData(IDataStore dataStore) { } // transient — nothing saved

        private void OnMapEventEnded(MapEvent mapEvent) { _arrivedCount = 0; _arrivedTroops = 0; _pendingTroops.Clear(); _originParty = null; _reinforcePlayerSide = false; }

        private void OnMapEventStarted(MapEvent mapEvent, PartyBase attacker, PartyBase defender)
        {
            try
            {
                _arrivedCount = 0;
                _arrivedTroops = 0;
                _pendingTroops.Clear();
                _originParty = null;
                _reinforcePlayerSide = false; // TASK B1 — reset first so player-side path never leaks between battles
                var s = MCMSettings.Instance;
                if (s == null || !s.EnableMod || !s.EnableBanditBackup) return;
                if (s.BanditBackupMaxParties <= 0 || s.BanditBackupChance <= 0) return;
                if (mapEvent == null) return;

                // 07-11 storyline guard: stay dormant through the StoryMode tutorial and
                // never fold reinforcements into a battle a quest is tracking — the fold's
                // DestroyPartyAction was eating Radagos' raider parties (quest bricked).
                if (BandItPlus.Patches.BpStoryGuard.StoryTutorialActive()) return;
                if (BandItPlus.Patches.BpStoryGuard.EventHasQuestParty(mapEvent)) return;

                MobileParty main = MobileParty.MainParty;
                if (main == null) return;

                // Player must be in this event; resolve the enemy side.
                bool playerIsAttacker = SideContains(mapEvent.AttackerSide, main);
                bool playerIsDefender = SideContains(mapEvent.DefenderSide, main);
                if (!playerIsAttacker && !playerIsDefender) return;
                var enemySide = playerIsAttacker ? mapEvent.DefenderSide : mapEvent.AttackerSide;
                if (enemySide == null) return;

                // Fold reinforcements into the largest enemy BANDIT party. If none, the enemy
                // isn't bandit → no backup.
                MobileParty target = null; int best = -1;
                foreach (var ep in enemySide.Parties)
                {
                    var mp = ep?.Party?.MobileParty;
                    if (mp == null || !BandItPlus.Cultures.CultureProfileRegistry.IsBanditParty(mp)) continue;
                    int sz = mp.MemberRoster != null ? mp.MemberRoster.TotalManCount : 0;
                    if (sz > best) { best = sz; target = mp; }
                }
                // TASK B1 — no enemy bandit present. If the player has EARNED the bandit alliance,
                // nearby bandits (and the Mad King's lords) instead reinforce the PLAYER's side.
                // This branch only runs when the enemy path produced NO wave, so it can never
                // touch the enemy path or a normal battle.
                if (target == null) { TryReinforcePlayerSide(mapEvent, main, s); return; }
                _originParty = target.Party;   // in-battle enemy party = spawn origin for the wave

                int cap = s.BanditBackupMaxParties, chance = s.BanditBackupChance;
                float px = main.Position.X, py = main.Position.Y, radiusSq = Radius * Radius;

                var candidates = new List<MobileParty>();
                foreach (var p in MobileParty.All)
                {
                    if (p == null || p == main || p == target) continue;
                    if (p.IsCurrentlyUsedByAQuest) continue;   // never consume a quest's party
                    if (p.MapEvent != null || p.CurrentSettlement != null || p.Army != null) continue;
                    if (!BandItPlus.Cultures.CultureProfileRegistry.IsBanditParty(p)) continue;
                    if (DistSq(px, py, p) > radiusSq) continue;
                    if (IsAtPeaceCulture(p)) continue;            // BP peace: allies don't dogpile
                    candidates.Add(p);
                }
                candidates.Sort((a, b) => DistSq(px, py, a).CompareTo(DistSq(px, py, b)));

                int joined = 0, troops = 0;
                foreach (var p in candidates)
                {
                    if (joined >= cap) break;
                    if (MBRandom.RandomInt(100) >= chance) continue;
                    try
                    {
                        var roster = p.MemberRoster;
                        int n = roster != null ? roster.TotalManCount : 0;
                        if (n <= 0) continue;
                        RecordTroops(roster);                         // deferred: spawned as a timed wave in-battle
                        troops += n;
                        joined++;
                        HideoutPeacefulVisitState.Log("[BP-Backup] " + (p.Name?.ToString() ?? "?")
                            + " answers the call (+" + n + " troops, " + joined + "/" + cap + ")");
                        DestroyPartyAction.Apply(null, p);            // party left the map to join the ambush
                    }
                    catch (Exception jex)
                    {
                        HideoutPeacefulVisitState.Log("[BP-Backup] join fail: "
                            + jex.GetType().Name + ": " + jex.Message);
                    }
                }

                _arrivedCount = joined;
                _arrivedTroops = troops;
                if (joined > 0) OnBackupJoined(joined, troops);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("[BP-Backup] OnMapEventStarted fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // No map toast — it fired on the encounter/escape menu (the map), which the user
        // doesn't want. The reinforcement notice is shown IN the battle instead, by
        // BanditAiStatusMissionView (AddQuickInformation banner + WARBAND dossier line),
        // which reads CurrentBackupCount once the mission is running.
        private static void OnBackupJoined(int count, int troops)
        {
            HideoutPeacefulVisitState.Log("[BP-Backup] " + count + " warband(s) / " + troops
                + " troops joined the enemy");
        }

        // TASK B1 — player-side reinforcement. Runs ONLY when the enemy path found no enemy
        // bandit (target == null) AND the player has earned the bandit alliance AND is a battle
        // participant. Reuses the enemy responder scan verbatim, PLUS accepts the Mad King's
        // lords (ActualClan.StringId starts with "bp_rebel_clan_"). On success it sets the SAME
        // count/troop/origin/pending fields the enemy path sets, plus _reinforcePlayerSide, so
        // BanditReinforcementMissionView mounts the overlay and spawns the wave on PlayerTeam.
        private void TryReinforcePlayerSide(MapEvent mapEvent, MobileParty main, MCMSettings s)
        {
            try
            {
                if (!BandItPlus.BanditRaid.BanditRebelSiegeHelper.PlayerIsBanditFriendly()) return; // LOOSE: any-chief allied OR PeacefulEncounters (backup kicks in early)
                // Player must be a battle participant (already verified in caller, re-assert cheaply).
                if (main == null || main.MapEvent == null) return;

                int cap = s.BanditBackupMaxParties, chance = s.BanditBackupChance;
                float px = main.Position.X, py = main.Position.Y, radiusSq = Radius * Radius;

                var candidates = new List<MobileParty>();
                foreach (var p in MobileParty.All)
                {
                    if (p == null || p == main) continue;
                    if (p.IsCurrentlyUsedByAQuest) continue;   // never consume a quest's party
                    if (p.MapEvent != null || p.CurrentSettlement != null || p.Army != null) continue;
                    // Same responder pool as the enemy path — ordinary wandering bandit parties ONLY.
                    // The Mad King's conquest lords (bp_rebel_clan_*) are EXCLUDED: DestroyPartyAction would
                    // fold (and remove) them from the map, cannibalizing the King's campaign. Ordinary bandits
                    // respawn; the King's lords must keep conquering.
                    bool isRebelLord = p.ActualClan?.StringId != null
                        && p.ActualClan.StringId.StartsWith("bp_rebel_clan_", StringComparison.Ordinal);
                    if (isRebelLord) continue;
                    if (!BandItPlus.Cultures.CultureProfileRegistry.IsBanditParty(p)) continue;
                    if (DistSq(px, py, p) > radiusSq) continue;
                    if (IsAtPeaceCulture(p)) continue; // BP peace gates raw bandits
                    candidates.Add(p);
                }
                candidates.Sort((a, b) => DistSq(px, py, a).CompareTo(DistSq(px, py, b)));

                int joined = 0, troops = 0;
                foreach (var p in candidates)
                {
                    if (joined >= cap) break;
                    if (MBRandom.RandomInt(100) >= chance) continue;
                    try
                    {
                        var roster = p.MemberRoster;
                        int n = roster != null ? roster.TotalManCount : 0;
                        if (n <= 0) continue;
                        RecordTroops(roster);
                        troops += n;
                        joined++;
                        DestroyPartyAction.Apply(null, p);
                    }
                    catch (Exception jex)
                    {
                        HideoutPeacefulVisitState.Log("[BP-Backup] PLAYER-SIDE join fail: "
                            + jex.GetType().Name + ": " + jex.Message);
                    }
                }

                if (joined <= 0) { _pendingTroops.Clear(); return; } // nothing answered → leave everything reset

                // Spawn origin must be a PLAYER-side in-battle party so PartyAgentOrigin deploys
                // the wave as friendly. main.Party is guaranteed on the player's side.
                _originParty = main.Party;
                _arrivedCount = joined;
                _arrivedTroops = troops;
                _reinforcePlayerSide = true;
                HideoutPeacefulVisitState.Log("[BP-Backup] PLAYER-SIDE: " + joined
                    + " warband(s) answer the call for the bandit-allied player (+" + troops + " troops)");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("[BP-Backup] TryReinforcePlayerSide fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Squared map distance (avoids sqrt). MobileParty.Position.X/.Y is the 1.4.5 API.
        private static float DistSq(float px, float py, MobileParty p)
        {
            float dx = px - p.Position.X, dy = py - p.Position.Y;
            return dx * dx + dy * dy;
        }

        // Flatten an answering party's roster into the pending-spawn list (capped at 200).
        private static void RecordTroops(TaleWorlds.CampaignSystem.Roster.TroopRoster roster)
        {
            if (roster == null) return;
            for (int i = 0; i < roster.Count; i++)
            {
                var ch = roster.GetCharacterAtIndex(i);
                if (ch == null) continue;
                int num = roster.GetElementNumber(i);
                for (int k = 0; k < num && _pendingTroops.Count < 200; k++)
                    _pendingTroops.Add(ch);
            }
        }

        private static bool SideContains(MapEventSide side, MobileParty party)
        {
            if (side == null) return false;
            foreach (var ep in side.Parties)
                if (ep?.Party?.MobileParty == party) return true;
            return false;
        }

        private static bool IsAtPeaceCulture(MobileParty p)
        {
            try
            {
                var cid = (p.ActualClan?.Culture ?? p.LeaderHero?.Culture)?.StringId;
                var origins = BandItPlus.Origins.BanditOriginBehavior.Instance;
                return !string.IsNullOrEmpty(cid) && origins != null && origins.IsAtPeaceWith(cid);
            }
            catch { return false; }
        }
    }
}
