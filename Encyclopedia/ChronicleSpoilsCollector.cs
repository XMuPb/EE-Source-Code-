using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;

namespace EditableEncyclopedia
{
    /// <summary>
    /// One looted item stack: the item's StringId plus how many were taken.
    /// The renderer resolves the image from the StringId via MBObjectManager.
    /// </summary>
    public class ChronicleSpoilItem
    {
        public string ItemId;
        public int Count;

        public ChronicleSpoilItem() { }

        public ChronicleSpoilItem(string itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }
    }

    /// <summary>
    /// Gold + looted items attached to a single chronicle entry.
    /// <see cref="Items"/> is never null (possibly empty).
    /// </summary>
    public class ChronicleSpoils
    {
        /// <summary>Denars gained. Can be zero, and can be negative (gold lost by the defeated side).</summary>
        public int Gold;

        /// <summary>How many item stacks were dropped because of the storage cap.</summary>
        public int OmittedItemStacks;

        public List<ChronicleSpoilItem> Items = new List<ChronicleSpoilItem>();

        /// <summary>True when the stored item list is not the complete list.</summary>
        public bool Truncated { get { return OmittedItemStacks > 0; } }

        public bool IsEmpty { get { return Gold == 0 && (Items == null || Items.Count == 0); } }
    }

    /// <summary>
    /// Collects the *actual* gold and items the engine hands out during a battle or a raid,
    /// so the auto-journal can attach them to the chronicle line it writes.
    ///
    /// Everything here is transient — nothing in this class is saved. The persistent copy
    /// lives in EncyclopediaEditBehavior._chronicleSpoils, keyed by chronicle entry key.
    ///
    /// Why Harmony and not before/after diffing of hero gold:
    ///   * MapEventParty.PlunderedGold / GoldLost are the engine's own per-party battle gold
    ///     figures, but MapEventParty.CommitGoldChanges() zeroes both after handing the denars
    ///     over, and that runs BEFORE CampaignEvents.MapEventEnded fires. A prefix on
    ///     CommitGoldChanges is the last moment the numbers are intact.
    ///   * MapEvent.LootDefeatedPartyItems(...) is the single engine method that moves looted
    ///     gear. Bracketing it with a prefix/postfix pair gives an exact diff of
    ///     MapEventParty.RosterToReceiveLootItems — nothing else can mutate the roster inside
    ///     one synchronous call, so trade/production/wages cannot pollute it.
    ///   * RaidEventComponent.LootItemInRaid(party, item, count, ref roster) is the engine's
    ///     per-item raid loot call, so raid loot is read item-by-item as it happens.
    ///
    /// All three targets were verified present by reflection in
    /// bin\Win64_Shipping_Client\TaleWorlds.CampaignSystem.dll for this install. Each patch is
    /// installed independently and skipped (with a debug log) if its target is missing, so a
    /// game update that renames one method degrades to "no spoils captured" instead of throwing.
    /// </summary>
    internal static class ChronicleSpoilsCollector
    {
        /// <summary>Maximum item stacks persisted per chronicle entry (highest total value kept).</summary>
        internal const int MaxStoredItemStacks = 12;

        /// <summary>Settlement spoils stashes older than this many campaign hours are ignored.</summary>
        private const double SettlementStashMaxAgeHours = 72.0;

        private const int MaxSettlementStashes = 64;

        private static bool _patchAttempted;

        // ── Transient accumulation ────────────────────────────────────────

        private class HeroBucket
        {
            public int Gold;
            public readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
            public readonly Dictionary<string, int> UnitValues = new Dictionary<string, int>();
        }

        private class EventAccumulator
        {
            public readonly Dictionary<string, HeroBucket> ByHero = new Dictionary<string, HeroBucket>();
        }

        // ConditionalWeakTable keys off object identity and holds no strong reference, so a
        // finished MapEvent (and its accumulator) is collected automatically. That removes both
        // the leak risk and the hash-collision risk of an int-keyed dictionary.
        private static readonly ConditionalWeakTable<MapEvent, EventAccumulator> _events =
            new ConditionalWeakTable<MapEvent, EventAccumulator>();

        private class SettlementStash
        {
            public ChronicleSpoils Spoils;
            public double StampHours;
        }

        private static readonly Dictionary<string, SettlementStash> _settlementStashes =
            new Dictionary<string, SettlementStash>();

        // ── Recording (called from the Harmony patches) ───────────────────

        private static HeroBucket GetBucket(MapEvent mapEvent, string heroId)
        {
            if (mapEvent == null || string.IsNullOrEmpty(heroId)) return null;
            EventAccumulator acc = _events.GetOrCreateValue(mapEvent);
            if (acc == null) return null;
            HeroBucket bucket;
            if (!acc.ByHero.TryGetValue(heroId, out bucket))
            {
                bucket = new HeroBucket();
                acc.ByHero[heroId] = bucket;
            }
            return bucket;
        }

        internal static void AddGold(MapEvent mapEvent, string heroId, int gold)
        {
            if (gold == 0) return;
            HeroBucket bucket = GetBucket(mapEvent, heroId);
            if (bucket == null) return;
            bucket.Gold += gold;
        }

        internal static void AddItem(MapEvent mapEvent, string heroId, string itemId, int count, int unitValue)
        {
            if (count <= 0 || string.IsNullOrEmpty(itemId)) return;
            HeroBucket bucket = GetBucket(mapEvent, heroId);
            if (bucket == null) return;
            int existing;
            bucket.Counts.TryGetValue(itemId, out existing);
            bucket.Counts[itemId] = existing + count;
            if (unitValue > 0) bucket.UnitValues[itemId] = unitValue;
        }

        // ── Reading (called from the auto-journal handlers) ───────────────

        /// <summary>
        /// Snapshot of what one hero gained in this map event. Never returns null.
        /// Item stacks are sorted by total value (descending) and capped at
        /// <see cref="MaxStoredItemStacks"/>; the remainder is counted in OmittedItemStacks.
        /// </summary>
        internal static ChronicleSpoils PeekForHero(MapEvent mapEvent, string heroId)
        {
            var result = new ChronicleSpoils();
            try
            {
                if (mapEvent == null || string.IsNullOrEmpty(heroId)) return result;
                EventAccumulator acc;
                if (!_events.TryGetValue(mapEvent, out acc) || acc == null) return result;
                HeroBucket bucket;
                if (!acc.ByHero.TryGetValue(heroId, out bucket) || bucket == null) return result;
                result.Gold = bucket.Gold;
                FillItems(result, bucket);
            }
            catch (Exception ex) { MCMSettings.DebugLog("ChronicleSpoilsCollector: PeekForHero failed: " + ex.ToString()); }
            return result;
        }

        /// <summary>
        /// Merged snapshot across several heroes (used for the settlement-level siege entry).
        /// </summary>
        internal static ChronicleSpoils PeekAggregate(MapEvent mapEvent, IEnumerable<string> heroIds)
        {
            var result = new ChronicleSpoils();
            try
            {
                if (mapEvent == null || heroIds == null) return result;
                EventAccumulator acc;
                if (!_events.TryGetValue(mapEvent, out acc) || acc == null) return result;

                var merged = new HeroBucket();
                var seen = new HashSet<string>();
                foreach (var heroId in heroIds)
                {
                    if (string.IsNullOrEmpty(heroId) || !seen.Add(heroId)) continue;
                    HeroBucket bucket;
                    if (!acc.ByHero.TryGetValue(heroId, out bucket) || bucket == null) continue;
                    merged.Gold += bucket.Gold;
                    foreach (var kvp in bucket.Counts)
                    {
                        int existing;
                        merged.Counts.TryGetValue(kvp.Key, out existing);
                        merged.Counts[kvp.Key] = existing + kvp.Value;
                    }
                    foreach (var kvp in bucket.UnitValues)
                        merged.UnitValues[kvp.Key] = kvp.Value;
                }
                result.Gold = merged.Gold;
                FillItems(result, merged);
            }
            catch (Exception ex) { MCMSettings.DebugLog("ChronicleSpoilsCollector: PeekAggregate failed: " + ex.ToString()); }
            return result;
        }

        private static void FillItems(ChronicleSpoils target, HeroBucket bucket)
        {
            var ordered = new List<ChronicleSpoilItem>();
            var weights = new Dictionary<string, long>();
            foreach (var kvp in bucket.Counts)
            {
                if (kvp.Value <= 0) continue;
                ordered.Add(new ChronicleSpoilItem(kvp.Key, kvp.Value));
                int unit;
                bucket.UnitValues.TryGetValue(kvp.Key, out unit);
                weights[kvp.Key] = (long)unit * kvp.Value;
            }
            // Most valuable stacks first, so a truncated list keeps what the player cares about.
            ordered.Sort(delegate (ChronicleSpoilItem a, ChronicleSpoilItem b)
            {
                long wa, wb;
                weights.TryGetValue(a.ItemId, out wa);
                weights.TryGetValue(b.ItemId, out wb);
                if (wa != wb) return wb.CompareTo(wa);
                return string.CompareOrdinal(a.ItemId, b.ItemId);
            });

            if (ordered.Count > MaxStoredItemStacks)
            {
                target.OmittedItemStacks = ordered.Count - MaxStoredItemStacks;
                ordered.RemoveRange(MaxStoredItemStacks, ordered.Count - MaxStoredItemStacks);
            }
            target.Items = ordered;
        }

        // ── Settlement hand-off (siege battle -> owner change) ────────────

        /// <summary>
        /// Remembers the spoils of the assault that just took a settlement, so the separate
        /// OnSettlementOwnerChanged entry can reuse them.
        /// </summary>
        internal static void StashSettlementSpoils(string settlementId, ChronicleSpoils spoils)
        {
            try
            {
                if (string.IsNullOrEmpty(settlementId) || spoils == null || spoils.IsEmpty) return;
                if (_settlementStashes.Count >= MaxSettlementStashes) PruneSettlementStashes();
                var stash = new SettlementStash();
                stash.Spoils = spoils;
                stash.StampHours = CurrentHours();
                _settlementStashes[settlementId] = stash;
            }
            catch (Exception ex) { MCMSettings.DebugLog("ChronicleSpoilsCollector: StashSettlementSpoils failed: " + ex.ToString()); }
        }

        /// <summary>
        /// Consumes a stashed settlement snapshot if it is recent enough. Never returns null.
        /// </summary>
        internal static ChronicleSpoils TakeSettlementSpoils(string settlementId)
        {
            try
            {
                SettlementStash stash;
                if (!string.IsNullOrEmpty(settlementId) && _settlementStashes.TryGetValue(settlementId, out stash) && stash != null)
                {
                    _settlementStashes.Remove(settlementId);
                    if (CurrentHours() - stash.StampHours <= SettlementStashMaxAgeHours && stash.Spoils != null)
                        return stash.Spoils;
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("ChronicleSpoilsCollector: TakeSettlementSpoils failed: " + ex.ToString()); }
            return new ChronicleSpoils();
        }

        private static void PruneSettlementStashes()
        {
            double now = CurrentHours();
            var stale = new List<string>();
            foreach (var kvp in _settlementStashes)
            {
                if (kvp.Value == null || now - kvp.Value.StampHours > SettlementStashMaxAgeHours)
                    stale.Add(kvp.Key);
            }
            foreach (var id in stale) _settlementStashes.Remove(id);
            // Still full of fresh entries? Drop everything rather than grow without bound.
            if (_settlementStashes.Count >= MaxSettlementStashes) _settlementStashes.Clear();
        }

        private static double CurrentHours()
        {
            try { return CampaignTime.Now.ToHours; }
            catch (Exception ex) { MCMSettings.DebugLog("ChronicleSpoilsCollector: CampaignTime read failed: " + ex.ToString()); return 0.0; }
        }

        /// <summary>Clears cross-campaign leftovers. Called when a save loads.</summary>
        internal static void Reset()
        {
            try { _settlementStashes.Clear(); }
            catch (Exception ex) { MCMSettings.DebugLog("ChronicleSpoilsCollector: Reset failed: " + ex.ToString()); }
        }

        // ── Harmony installation ─────────────────────────────────────────

        private const BindingFlags AllFlags =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private const BindingFlags PatchFlags = BindingFlags.Static | BindingFlags.NonPublic;

        internal static void TryPatch(Harmony harmony)
        {
            if (_patchAttempted) return;
            _patchAttempted = true;
            if (harmony == null) return;

            InstallPrefix(harmony, typeof(MapEventParty), "CommitGoldChanges", Type.EmptyTypes, "CommitGoldChangesPrefix");
            InstallBracket(harmony, typeof(MapEvent), "LootDefeatedPartyItems", "LootDefeatedPartyItemsPrefix", "LootDefeatedPartyItemsPostfix");
            InstallPostfix(harmony, typeof(RaidEventComponent), "LootItemInRaid", null, "LootItemInRaidPostfix");
        }

        private static MethodInfo FindTarget(Type owner, string name, Type[] signature)
        {
            try
            {
                MethodInfo target = signature != null
                    ? owner.GetMethod(name, AllFlags, null, signature, null)
                    : owner.GetMethod(name, AllFlags);
                if (target == null)
                    MCMSettings.DebugLog("ChronicleSpoils: target " + owner.Name + "." + name + " not found — spoils capture disabled for it");
                return target;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("ChronicleSpoils: lookup of " + owner.Name + "." + name + " failed: " + ex.ToString());
                return null;
            }
        }

        private static void InstallPrefix(Harmony harmony, Type owner, string name, Type[] signature, string patchName)
        {
            try
            {
                MethodInfo target = FindTarget(owner, name, signature);
                if (target == null) return;
                var patch = typeof(ChronicleSpoilsCollector).GetMethod(patchName, PatchFlags);
                if (patch == null) return;
                harmony.Patch(target, prefix: new HarmonyMethod(patch));
                MCMSettings.DebugLog("ChronicleSpoils: patched " + owner.Name + "." + name + " (prefix)");
            }
            catch (Exception ex) { MCMSettings.DebugLog("ChronicleSpoils: prefix patch of " + owner.Name + "." + name + " failed: " + ex.ToString()); }
        }

        private static void InstallPostfix(Harmony harmony, Type owner, string name, Type[] signature, string patchName)
        {
            try
            {
                MethodInfo target = FindTarget(owner, name, signature);
                if (target == null) return;
                var patch = typeof(ChronicleSpoilsCollector).GetMethod(patchName, PatchFlags);
                if (patch == null) return;
                harmony.Patch(target, postfix: new HarmonyMethod(patch));
                MCMSettings.DebugLog("ChronicleSpoils: patched " + owner.Name + "." + name + " (postfix)");
            }
            catch (Exception ex) { MCMSettings.DebugLog("ChronicleSpoils: postfix patch of " + owner.Name + "." + name + " failed: " + ex.ToString()); }
        }

        private static void InstallBracket(Harmony harmony, Type owner, string name, string prefixName, string postfixName)
        {
            try
            {
                MethodInfo target = FindTarget(owner, name, null);
                if (target == null) return;
                var prefix = typeof(ChronicleSpoilsCollector).GetMethod(prefixName, PatchFlags);
                var postfix = typeof(ChronicleSpoilsCollector).GetMethod(postfixName, PatchFlags);
                if (prefix == null || postfix == null) return;
                harmony.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
                MCMSettings.DebugLog("ChronicleSpoils: patched " + owner.Name + "." + name + " (prefix+postfix)");
            }
            catch (Exception ex) { MCMSettings.DebugLog("ChronicleSpoils: bracket patch of " + owner.Name + "." + name + " failed: " + ex.ToString()); }
        }

        // ── Patch bodies ─────────────────────────────────────────────────
        // Every body is fully wrapped: a throw here would abort an engine method
        // mid-battle, so failure must always degrade to "no spoils recorded".

        /// <summary>
        /// Last moment before MapEventParty.CommitGoldChanges() hands the denars over and
        /// resets PlunderedGold/GoldLost to zero.
        /// </summary>
        private static void CommitGoldChangesPrefix(MapEventParty __instance)
        {
            try
            {
                if (__instance == null) return;
                PartyBase party = __instance.Party;
                if (party == null) return;
                Hero leader = party.LeaderHero;
                if (leader == null) return;
                MapEvent mapEvent = party.MapEvent;
                if (mapEvent == null) return;
                int delta = __instance.PlunderedGold - __instance.GoldLost;
                if (delta == 0) return;
                AddGold(mapEvent, leader.StringId, delta);
            }
            catch (Exception ex) { MCMSettings.DebugLog("ChronicleSpoils: CommitGoldChangesPrefix failed: " + ex.ToString()); }
        }

        private static void LootDefeatedPartyItemsPrefix(MapEvent __instance, out object __state)
        {
            __state = null;
            try { __state = SnapshotRosters(__instance); }
            catch (Exception ex) { MCMSettings.DebugLog("ChronicleSpoils: LootDefeatedPartyItemsPrefix failed: " + ex.ToString()); }
        }

        private static void LootDefeatedPartyItemsPostfix(MapEvent __instance, object __state)
        {
            try
            {
                var before = __state as Dictionary<MapEventParty, Dictionary<string, int>>;
                if (before == null || __instance == null) return;

                foreach (var kvp in before)
                {
                    MapEventParty mep = kvp.Key;
                    if (mep == null) continue;
                    PartyBase party = mep.Party;
                    Hero leader = party != null ? party.LeaderHero : null;
                    if (leader == null) continue;

                    Dictionary<string, int> after = SnapshotRoster(mep);
                    if (after == null) continue;

                    foreach (var entry in after)
                    {
                        int previous;
                        kvp.Value.TryGetValue(entry.Key, out previous);
                        int gained = entry.Value - previous;
                        if (gained > 0)
                            AddItem(__instance, leader.StringId, entry.Key, gained, LookupItemValue(entry.Key));
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("ChronicleSpoils: LootDefeatedPartyItemsPostfix failed: " + ex.ToString()); }
        }

        private static void LootItemInRaidPostfix(RaidEventComponent __instance, PartyBase __0, ItemObject __1, int __2)
        {
            try
            {
                if (__instance == null || __0 == null || __1 == null || __2 <= 0) return;
                Hero leader = __0.LeaderHero;
                if (leader == null) return;
                MapEvent mapEvent = __instance.MapEvent;
                if (mapEvent == null) return;
                AddItem(mapEvent, leader.StringId, __1.StringId, __2, __1.Value);
            }
            catch (Exception ex) { MCMSettings.DebugLog("ChronicleSpoils: LootItemInRaidPostfix failed: " + ex.ToString()); }
        }

        // ── Roster snapshots ─────────────────────────────────────────────

        private static Dictionary<MapEventParty, Dictionary<string, int>> SnapshotRosters(MapEvent mapEvent)
        {
            if (mapEvent == null) return null;
            var result = new Dictionary<MapEventParty, Dictionary<string, int>>();
            AddSideSnapshot(result, mapEvent.AttackerSide);
            AddSideSnapshot(result, mapEvent.DefenderSide);
            return result;
        }

        private static void AddSideSnapshot(Dictionary<MapEventParty, Dictionary<string, int>> target, MapEventSide side)
        {
            try
            {
                if (side == null || side.Parties == null) return;
                foreach (MapEventParty mep in side.Parties)
                {
                    if (mep == null || target.ContainsKey(mep)) continue;
                    PartyBase party = mep.Party;
                    if (party == null || party.LeaderHero == null) continue;
                    Dictionary<string, int> snapshot = SnapshotRoster(mep);
                    if (snapshot != null) target[mep] = snapshot;
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("ChronicleSpoils: AddSideSnapshot failed: " + ex.ToString()); }
        }

        /// <summary>
        /// Reads MapEventParty.RosterToReceiveLootItems — the engine's own "where does this
        /// party's loot land" accessor (the player encounter's loot roster for the player,
        /// the party inventory for everyone else).
        /// </summary>
        private static Dictionary<string, int> SnapshotRoster(MapEventParty mep)
        {
            try
            {
                if (mep == null) return null;
                ItemRoster roster = mep.RosterToReceiveLootItems;
                if (roster == null) return null;
                var counts = new Dictionary<string, int>();
                int count = roster.Count;
                for (int i = 0; i < count; i++)
                {
                    ItemRosterElement element = roster.GetElementCopyAtIndex(i);
                    ItemObject item = element.EquipmentElement.Item;
                    if (item == null) continue;
                    string id = item.StringId;
                    if (string.IsNullOrEmpty(id)) continue;
                    int existing;
                    counts.TryGetValue(id, out existing);
                    counts[id] = existing + element.Amount;
                }
                return counts;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("ChronicleSpoils: SnapshotRoster failed: " + ex.ToString());
                return null;
            }
        }

        private static int LookupItemValue(string itemId)
        {
            try
            {
                if (string.IsNullOrEmpty(itemId)) return 0;
                var manager = TaleWorlds.ObjectSystem.MBObjectManager.Instance;
                if (manager == null) return 0;
                ItemObject item = manager.GetObject<ItemObject>(itemId);
                return item != null ? item.Value : 0;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("ChronicleSpoils: LookupItemValue failed for " + itemId + ": " + ex.ToString());
                return 0;
            }
        }
    }
}
