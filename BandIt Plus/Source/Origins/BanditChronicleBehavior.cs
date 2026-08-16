using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using BandItPlus.HideoutVisit;

namespace BandItPlus.Origins
{
    // Living Chronicle: records the first time the PLAYER hits each of five bandit-life
    // milestones; BanditChronicleCodex renders them as a saga on the hero's encyclopedia page.
    public class BanditChronicleBehavior : CampaignBehaviorBase
    {
        private static BanditChronicleBehavior _instance;
        public static BanditChronicleBehavior Instance => _instance;

        public static readonly string[] ChapterOrder =
            { "first_parley", "kin_oath", "open_camp", "horn_answered", "blood_for_blood" };

        // chapter id -> campaign day unlocked; chapter id -> captured context (chief/clan/place name)
        private Dictionary<string, double> _unlockedDay = new Dictionary<string, double>();
        private Dictionary<string, string> _context = new Dictionary<string, string>();

        // "|"-delimited chapter ids already pushed to EE-Core's journal — drift-immune dedupe, no container def.
        private string _pushedToEeCsv = "";
        private bool _migrated; // one-time per session: replay pre-existing unlocks into EE

        // main-thread toast queue (the map events below can fire off-thread)
        private readonly List<string> _pendingToasts = new List<string>();
        private readonly object _lock = new object();

        public BanditChronicleBehavior() { _instance = this; }

        public override void RegisterEvents()
        {
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick); // main thread: drain toasts
            CampaignEvents.VillageStateChanged.AddNonSerializedListener(this, OnVillageStateChanged);
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, OnSettlementOwnerChanged);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
            HideoutPeacefulVisitState.WalkableActivated += OnWalkableActivated;
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("_bpChronicleDay", ref _unlockedDay);
            dataStore.SyncData("_bpChronicleCtx", ref _context);
            dataStore.SyncData("_bpChroniclePushedEe", ref _pushedToEeCsv);
            if (_unlockedDay == null) _unlockedDay = new Dictionary<string, double>();
            if (_context == null) _context = new Dictionary<string, string>();
            if (_pushedToEeCsv == null) _pushedToEeCsv = "";
        }

        // --- public API ---
        public bool IsUnlocked(string id) => _unlockedDay.ContainsKey(id);

        // ordered (by unlock day) list of (id, day, context) for the codex to render
        public List<(string id, double day, string ctx)> GetUnlockedOrdered()
        {
            var list = new List<(string, double, string)>();
            foreach (var kv in _unlockedDay)
                list.Add((kv.Key, kv.Value, _context.TryGetValue(kv.Key, out var c) ? c : ""));
            list.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return list;
        }

        public void Unlock(string id, string context)
        {
            try
            {
                if (id == null || _unlockedDay.ContainsKey(id)) return;
                _unlockedDay[id] = CampaignTime.Now.ToDays;
                _context[id] = context ?? "";
                lock (_lock) { _pendingToasts.Add(id); }
                HideoutPeacefulVisitState.Log("[BP-Chronicle] unlocked " + id + " ctx=" + (context ?? ""));
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("[BP-Chronicle] Unlock: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // --- EE-Core journal push (optional; no-op when EE absent) ---
        private bool PushedToEe(string id) => ("|" + _pushedToEeCsv + "|").Contains("|" + id + "|");
        private void MarkPushedToEe(string id)
        {
            if (PushedToEe(id)) return;
            _pushedToEeCsv = string.IsNullOrEmpty(_pushedToEeCsv) ? id : _pushedToEeCsv + "|" + id;
        }

        // Push one chapter to EE-Core's journal on the player's hero page. Main-thread only.
        private void PushToEe(string id, string ctx, double day)
        {
            if (id == null || PushedToEe(id) || !BandItPlus.Integration.EeBridge.Available) return;
            var hero = Hero.MainHero;
            if (hero == null) return;
            string text = BanditChronicleCodex.RenderChapter(id, hero.Name != null ? hero.Name.ToString() : "", ctx, day);
            if (string.IsNullOrEmpty(text)) return;
            BandItPlus.Integration.EeBridge.AddJournalEntry(hero.StringId, text);
            MarkPushedToEe(id);
        }

        // --- main-thread toast drain + EE push ---
        private void OnTick(float dt)
        {
            // One-time migration: once EE-Core is available, replay any already-unlocked chapters
            // into its journal (covers saves whose milestones fired before this integration existed).
            if (!_migrated && BandItPlus.Integration.EeBridge.Available)
            {
                _migrated = true;
                try
                {
                    foreach (var kv in new List<KeyValuePair<string, double>>(_unlockedDay))
                        PushToEe(kv.Key, _context.TryGetValue(kv.Key, out var mc) ? mc : "", kv.Value);
                }
                catch (Exception ex) { HideoutPeacefulVisitState.Log("[BP-Chronicle] migrate: " + ex.Message); }
            }

            string id = null;
            lock (_lock) { if (_pendingToasts.Count > 0) { id = _pendingToasts[0]; _pendingToasts.RemoveAt(0); } }
            if (id == null) return;
            try
            {
                var t = new TextObject("{=bp_chronicle_toast}A new page is written in your chronicle — read it in your character's page.");
                InformationManager.DisplayMessage(new InformationMessage(t.ToString()));
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("[BP-Chronicle] toast: " + ex.Message); }

            // EE push runs here (main thread), not in Unlock which can fire off-thread.
            try { PushToEe(id, _context.TryGetValue(id, out var c2) ? c2 : "", _unlockedDay.TryGetValue(id, out var d2) ? d2 : CampaignTime.Now.ToDays); }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("[BP-Chronicle] ee push: " + ex.Message); }
        }

        // --- milestone hooks (event-driven) ---
        private void OnWalkableActivated(Settlement settlement)
        {
            if (_instance != this) return;   // static event survives across campaigns — ignore zombie handlers
            if (settlement == null) return;
            Unlock("open_camp", settlement.Name != null ? settlement.Name.ToString() : "");
        }

        private void OnVillageStateChanged(Village village, Village.VillageStates oldState,
            Village.VillageStates newState, MobileParty raiderParty)
        {
            try
            {
                if (village?.Settlement == null || newState != Village.VillageStates.Looted) return;
                bool playerRaid =
                    (raiderParty != null && (raiderParty == MobileParty.MainParty || raiderParty.LeaderHero == Hero.MainHero))
                    || village.Settlement.LastAttackerParty == MobileParty.MainParty;
                if (!playerRaid) return;
                Unlock("horn_answered", village.Settlement.Name.ToString());
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("[BP-Chronicle] village: " + ex.Message); }
        }

        private void OnSettlementOwnerChanged(Settlement settlement, bool openToClaim, Hero newOwner,
            Hero oldOwner, Hero capturerHero, ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail detail)
        {
            try
            {
                if (detail != ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.BySiege) return;
                if (settlement == null || newOwner == null || newOwner.Clan != Clan.PlayerClan) return;
                Unlock("blood_for_blood", settlement.Name != null ? settlement.Name.ToString() : "a stronghold");
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("[BP-Chronicle] owner: " + ex.Message); }
        }

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            try
            {
                if (mapEvent == null || !mapEvent.IsHideoutBattle || !mapEvent.IsPlayerMapEvent) return;
                if (mapEvent.WinningSide != mapEvent.PlayerSide) return;
                var s = mapEvent.MapEventSettlement;
                Unlock("blood_for_blood", s != null && s.Name != null ? s.Name.ToString() : "a hideout");
            }
            catch (Exception ex) { HideoutPeacefulVisitState.Log("[BP-Chronicle] mapevent: " + ex.Message); }
        }
    }
}
