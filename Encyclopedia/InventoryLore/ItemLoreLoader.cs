using System;
using System.Collections.Generic;
using System.IO;
using TaleWorlds.Core;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Loads item lore pools from LoreStory/ItemLore/ and resolves a deterministic
    /// entry per (item, owner) pair. Mirrors LoreStoryLoader's structure.
    /// </summary>
    internal static class ItemLoreLoader
    {
        private const string FolderName = "LoreStory";
        private const string SubFolderName = "ItemLore";
        private const string EntrySeparator = "---";

        // Category -> pool of entries
        private static Dictionary<ItemLoreCategory, List<string>> _pools;
        private static bool _loaded;

        private static readonly Dictionary<ItemLoreCategory, string> CategoryToFolder =
            new Dictionary<ItemLoreCategory, string>
            {
                { ItemLoreCategory.Weapon, "Weapon" },
                { ItemLoreCategory.Armor,  "Armor"  },
                { ItemLoreCategory.Mount,  "Mount"  },
                { ItemLoreCategory.Goods,  "Goods"  }
            };

        /// <summary>
        /// Maps a Bannerlord item type onto a lore pool. Unknown types fall
        /// through to Goods rather than throwing.
        /// </summary>
        public static ItemLoreCategory GetCategory(ItemObject.ItemTypeEnum itemType)
        {
            switch (itemType)
            {
                case ItemObject.ItemTypeEnum.OneHandedWeapon:
                case ItemObject.ItemTypeEnum.TwoHandedWeapon:
                case ItemObject.ItemTypeEnum.Polearm:
                case ItemObject.ItemTypeEnum.Bow:
                case ItemObject.ItemTypeEnum.Crossbow:
                case ItemObject.ItemTypeEnum.Thrown:
                case ItemObject.ItemTypeEnum.Shield:
                case ItemObject.ItemTypeEnum.Arrows:
                case ItemObject.ItemTypeEnum.Bolts:
                    return ItemLoreCategory.Weapon;

                case ItemObject.ItemTypeEnum.HeadArmor:
                case ItemObject.ItemTypeEnum.BodyArmor:
                case ItemObject.ItemTypeEnum.LegArmor:
                case ItemObject.ItemTypeEnum.HandArmor:
                case ItemObject.ItemTypeEnum.Cape:
                    return ItemLoreCategory.Armor;

                case ItemObject.ItemTypeEnum.Horse:
                case ItemObject.ItemTypeEnum.HorseHarness:
                    return ItemLoreCategory.Mount;

                default:
                    return ItemLoreCategory.Goods;
            }
        }

        /// <summary>
        /// Loads all four pools. Safe to call repeatedly.
        /// </summary>
        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            _pools = new Dictionary<ItemLoreCategory, List<string>>();
            foreach (var kvp in CategoryToFolder)
                _pools[kvp.Key] = new List<string>();

            try
            {
                string moduleFolder = LoreStoryLoader.FindModuleFolder();
                if (string.IsNullOrEmpty(moduleFolder))
                {
                    MCMSettings.DebugLog("ItemLoreLoader: module folder not found");
                    return;
                }

                string root = Path.Combine(Path.Combine(moduleFolder, FolderName), SubFolderName);
                if (!Directory.Exists(root))
                {
                    MCMSettings.DebugLog("ItemLoreLoader: ItemLore folder not found at " + root);
                    return;
                }

                foreach (var kvp in CategoryToFolder)
                {
                    string dir = Path.Combine(root, kvp.Value);
                    if (!Directory.Exists(dir)) continue;

                    foreach (string file in Directory.GetFiles(dir, "*.txt"))
                        _pools[kvp.Key].AddRange(LoadEntries(file));

                    MCMSettings.DebugLog("ItemLoreLoader: " + kvp.Value + " pool = "
                        + _pools[kvp.Key].Count + " entries");
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("ItemLoreLoader.EnsureLoaded: " + ex.ToString());
            }
        }

        private static List<string> LoadEntries(string filePath)
        {
            var result = new List<string>();
            try
            {
                string raw = File.ReadAllText(filePath);
                string[] lines = raw.Split('\n');
                var current = new System.Text.StringBuilder();

                foreach (string line in lines)
                {
                    if (line.Trim() == EntrySeparator)
                    {
                        string done = current.ToString().Trim();
                        if (done.Length > 0) result.Add(done);
                        current.Length = 0;
                    }
                    else
                    {
                        current.AppendLine(line.TrimEnd('\r'));
                    }
                }

                string tail = current.ToString().Trim();
                if (tail.Length > 0) result.Add(tail);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("ItemLoreLoader.LoadEntries " + filePath + ": " + ex.ToString());
            }
            return result;
        }

        /// <summary>
        /// Returns the lore for an item. Player override wins; otherwise a
        /// deterministic pool entry keyed on (item, owner) so the same pair
        /// always resolves to the same text across save/reload.
        /// </summary>
        public static string GetItemLore(string itemId, string ownerId, string itemName,
            ItemLoreCategory category)
        {
            try
            {
                var behavior = EncyclopediaEditBehavior.Instance;
                if (behavior != null)
                {
                    string custom = behavior.GetItemLoreOverride(itemId, ownerId);
                    if (!string.IsNullOrEmpty(custom)) return custom;
                }

                EnsureLoaded();

                List<string> pool;
                if (_pools == null || !_pools.TryGetValue(category, out pool) || pool.Count == 0)
                    return string.Empty;

                int index = LoreStoryLoader.GetStableHash(itemId + "|" + ownerId) % pool.Count;
                string text = pool[index];

                // {item} is resolved here so LoreStoryLoader stays untouched.
                text = text.Replace("{item}", itemName ?? "this item");

                // All remaining tokens go through the existing resolver.
                text = LoreStoryLoader.ResolvePlaceholders(text, ownerId);
                // Neutralise any tokens the owner left unresolved (settlement stash owners have
                // no gender, so {his}/{man}/etc. would otherwise show literally), then strip any
                // other stray {token} so nothing raw ever reaches the player.
                return CleanLeftoverTokens(text);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("ItemLoreLoader.GetItemLore: " + ex.ToString());
                return string.Empty;
            }
        }

        /// <summary>
        /// Replaces gender/person tokens the non-hero (e.g. settlement) resolver leaves behind
        /// with neutral words, then strips any other stray {token} so no raw placeholder ever shows.
        /// </summary>
        private static string CleanLeftoverTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = text.Replace("{his}", "its").Replace("{His}", "Its")
                       .Replace("{he}", "it").Replace("{He}", "It")
                       .Replace("{him}", "it")
                       .Replace("{man}", "owner").Replace("{woman}", "owner")
                       .Replace("{son}", "heir").Replace("{brother}", "kin")
                       .Replace("{lord}", "lord").Replace("{Lord}", "Lord")
                       .Replace("{father}", "forebears").Replace("{mother}", "forebears")
                       .Replace("{spouse}", "family");
            if (text.IndexOf('{') >= 0)
                text = System.Text.RegularExpressions.Regex.Replace(text, "\\{[a-zA-Z_]+\\}", "");
            return text;
        }

        public static void ClearCache()
        {
            _loaded = false;
            _pools = null;
        }
    }
}
