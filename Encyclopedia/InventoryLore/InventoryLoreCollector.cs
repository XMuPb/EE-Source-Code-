using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Reads the four item sources for an encyclopedia entity into one ordered
    /// list. Empty sources contribute nothing, so no empty group ever renders.
    /// </summary>
    internal static class InventoryLoreCollector
    {
        public static List<ItemLoreEntry> Collect(string entityId)
        {
            var result = new List<ItemLoreEntry>();
            try
            {
                Hero hero = Hero.FindFirst(h => h.StringId == entityId);
                if (hero != null)
                {
                    AddEquipment(result, hero);
                    AddParty(result, hero);
                }
                AddStash(result, entityId);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("InventoryLoreCollector.Collect: " + ex.ToString());
            }
            return result;
        }

        private static void AddEquipment(List<ItemLoreEntry> result, Hero hero)
        {
            try
            {
                Equipment eq = hero.BattleEquipment;
                if (eq == null) return;

                for (int i = 0; i < (int)EquipmentIndex.NumEquipmentSetSlots; i++)
                {
                    EquipmentElement el = eq[(EquipmentIndex)i];
                    if (el.Item == null) continue;
                    result.Add(Build(el.Item, 1, hero.StringId, ItemLoreSource.HeroEquipment));
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("InventoryLoreCollector.AddEquipment: " + ex.ToString());
            }
        }

        private static void AddParty(List<ItemLoreEntry> result, Hero hero)
        {
            try
            {
                MobileParty party = hero.PartyBelongedTo;
                if (party == null || party.ItemRoster == null) return;

                bool isPlayer = party == MobileParty.MainParty;
                ItemLoreSource source = isPlayer ? ItemLoreSource.PlayerParty : ItemLoreSource.LordParty;

                foreach (ItemRosterElement el in party.ItemRoster)
                {
                    if (el.EquipmentElement.Item == null) continue;
                    result.Add(Build(el.EquipmentElement.Item, el.Amount, hero.StringId, source));
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("InventoryLoreCollector.AddParty: " + ex.ToString());
            }
        }

        private static void AddStash(List<ItemLoreEntry> result, string entityId)
        {
            try
            {
                Settlement settlement = Settlement.Find(entityId);
                if (settlement == null || settlement.ItemRoster == null) return;

                foreach (ItemRosterElement el in settlement.ItemRoster)
                {
                    if (el.EquipmentElement.Item == null) continue;
                    result.Add(Build(el.EquipmentElement.Item, el.Amount, entityId, ItemLoreSource.Stash));
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("InventoryLoreCollector.AddStash: " + ex.ToString());
            }
        }

        private static ItemLoreEntry Build(ItemObject item, int amount, string ownerId,
            ItemLoreSource source)
        {
            var behavior = EncyclopediaEditBehavior.Instance;
            var entry = new ItemLoreEntry
            {
                ItemId = item.StringId,
                OwnerId = ownerId,
                DisplayName = ResolveDisplayName(item, behavior),
                StatLine = BuildStatLine(item),
                StatsBlock = BuildStatsBlock(item),
                Quantity = amount,
                Value = item.Value,
                Category = ItemLoreLoader.GetCategory(item.ItemType),
                Source = source
            };
            entry.HasOverride = behavior != null
                && !string.IsNullOrEmpty(behavior.GetItemLoreOverride(item.StringId, ownerId));
            return entry;
        }

        private static string ResolveDisplayName(ItemObject item, EncyclopediaEditBehavior behavior)
        {
            if (behavior != null)
            {
                string custom = behavior.GetCustomName(item.StringId);
                if (!string.IsNullOrEmpty(custom)) return custom;
            }
            return item.Name != null ? item.Name.ToString() : item.StringId;
        }

        /// <summary>
        /// Builds the multi-line, type-aware stat block shown on hover and in the detail panel.
        /// Weapons report damage TYPE (cut/pierce/blunt) alongside the numbers, armour reports each
        /// coverage value plus material, mounts report speed/manoeuvre/charge/HP.
        /// </summary>
        private static string BuildStatsBlock(ItemObject item)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(item.ItemType.ToString());
                // item.Tier is a non-nullable enum whose ToString() is already "Tier3" etc.;
                // insert a space so it reads "Tier 3" instead of the doubled "Tier Tier3".
                sb.Append("  -  ").Append(item.Tier.ToString().Replace("Tier", "Tier "));
                if (item.Culture != null && item.Culture.Name != null)
                    sb.Append("  -  ").Append(item.Culture.Name.ToString());
                sb.AppendLine();

                if (item.WeaponComponent != null && item.WeaponComponent.PrimaryWeapon != null)
                {
                    var w = item.WeaponComponent.PrimaryWeapon;
                    if (w.SwingDamage > 0)
                        sb.AppendLine("Swing: " + w.SwingDamage + " " + w.SwingDamageType
                            + "  (speed " + w.SwingSpeed + ")");
                    if (w.ThrustDamage > 0)
                        sb.AppendLine("Thrust: " + w.ThrustDamage + " " + w.ThrustDamageType
                            + "  (speed " + w.ThrustSpeed + ")");
                    if (w.MissileDamage > 0)
                        sb.AppendLine("Missile: " + w.MissileDamage
                            + "  (speed " + w.MissileSpeed + ")");
                    if (w.WeaponLength > 0) sb.AppendLine("Length: " + w.WeaponLength);
                    if (w.Handling > 0) sb.AppendLine("Handling: " + w.Handling);
                    if (w.Accuracy > 0) sb.AppendLine("Accuracy: " + w.Accuracy);
                    if (w.BodyArmor > 0) sb.AppendLine("Shield armour: " + w.BodyArmor);
                }

                if (item.ArmorComponent != null)
                {
                    var a = item.ArmorComponent;
                    if (a.HeadArmor > 0) sb.AppendLine("Head armour: " + a.HeadArmor);
                    if (a.BodyArmor > 0) sb.AppendLine("Body armour: " + a.BodyArmor);
                    if (a.ArmArmor > 0) sb.AppendLine("Arm armour: " + a.ArmArmor);
                    if (a.LegArmor > 0) sb.AppendLine("Leg armour: " + a.LegArmor);
                    sb.AppendLine("Material: " + a.MaterialType);
                }

                if (item.HorseComponent != null)
                {
                    var h = item.HorseComponent;
                    sb.AppendLine("Speed: " + h.Speed + "   Manoeuvre: " + h.Maneuver);
                    sb.AppendLine("Charge: " + h.ChargeDamage + "   Hit points: " + h.HitPoints);
                }

                sb.AppendLine("Weight: " + item.Weight.ToString("0.#"));
                if (item.Difficulty > 0) sb.AppendLine("Requires skill: " + item.Difficulty);
                if (item.IsCraftedByPlayer) sb.AppendLine("Forged by your own hand.");
                sb.Append("Worth: ").Append(item.Value.ToString("N0")).Append(" denars");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("InventoryLoreCollector.BuildStatsBlock: " + ex.ToString());
                return BuildStatLine(item);
            }
        }

        private static string BuildStatLine(ItemObject item)
        {
            try
            {
                string type = item.ItemType.ToString();
                if (item.WeaponComponent != null && item.WeaponComponent.PrimaryWeapon != null)
                {
                    var w = item.WeaponComponent.PrimaryWeapon;
                    return type + " - swing " + w.SwingDamage + " - thrust " + w.ThrustDamage;
                }
                if (item.ArmorComponent != null)
                    return type + " - armor " + item.ArmorComponent.BodyArmor;
                if (item.HorseComponent != null)
                    return type + " - speed " + item.HorseComponent.Speed;
                return type;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("InventoryLoreCollector.BuildStatLine: " + ex.ToString());
                return string.Empty;
            }
        }
    }
}
