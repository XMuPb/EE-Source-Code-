namespace EditableEncyclopedia
{
    /// <summary>
    /// Which lore pool an item draws from. Maps ItemObject.ItemTypeEnum onto
    /// the four folders under LoreStory/ItemLore/.
    /// </summary>
    public enum ItemLoreCategory
    {
        Weapon,
        Armor,
        Mount,
        Goods
    }

    /// <summary>
    /// Which roster an item came from. Used to group the grid and to decide
    /// whether a group renders at all.
    /// </summary>
    public enum ItemLoreSource
    {
        HeroEquipment,
        PlayerParty,
        LordParty,
        Stash
    }

    /// <summary>
    /// One cell in the Inventory Lore grid. Pure data, no engine references,
    /// so the collector can be reasoned about without the widget tree.
    /// </summary>
    public class ItemLoreEntry
    {
        public string ItemId;          // ItemObject.StringId
        public string OwnerId;         // hero/clan/settlement StringId that owns the roster
        public string DisplayName;     // resolved name (custom override wins)
        public string StatLine;        // e.g. "TwoHandedWeapon - swing 118 - thrust 92"
        public string StatsBlock;      // multi-line, type-aware stats shown on hover / in detail
        public int Quantity = 1;
        public int Value;              // item value in denars
        public ItemLoreCategory Category;
        public ItemLoreSource Source;
        public bool HasOverride;       // true when player wrote custom lore (drives the gold dot)

        /// <summary>
        /// Key used for override storage. Per (item, owner) so two lords'
        /// identical swords can carry different lore.
        /// </summary>
        public string OverrideKey
        {
            get { return ItemId + "|" + OwnerId; }
        }
    }
}
