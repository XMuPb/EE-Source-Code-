using System;

namespace EditableEncyclopedia.ChronicleNoters
{
    /// <summary>
    /// Event-dimension tag flags for chronicle entries. Bitmask so an entry can carry multiple
    /// classifications (e.g., a war that's also family-related).
    /// </summary>
    [Flags]
    public enum EventTag
    {
        None     = 0,
        War      = 1,
        Crime    = 2,
        Politics = 4,
        Family   = 8,
        Other    = 16
    }

    /// <summary>
    /// Entity-dimension tag flags. Bitmask so an entry can be linked to multiple entity types
    /// (e.g., a clan-changes-kingdom event is both Kingdom and Clan).
    /// </summary>
    [Flags]
    public enum EntityTag
    {
        None       = 0,
        Kingdom    = 1,
        Clan       = 2,
        Hero       = 4,
        Settlement = 8,
        Other      = 16
    }

    public static class ChronicleTagExtensions
    {
        public static string DisplayName(this EventTag t)
        {
            switch (t)
            {
                case EventTag.War:      return "War";
                case EventTag.Crime:    return "Crime";
                case EventTag.Politics: return "Politics";
                case EventTag.Family:   return "Family";
                case EventTag.Other:    return "Other";
                default:                return t.ToString();
            }
        }

        public static string DisplayName(this EntityTag t)
        {
            switch (t)
            {
                case EntityTag.Kingdom:    return "Kingdom";
                case EntityTag.Clan:       return "Clan";
                case EntityTag.Hero:       return "Hero";
                case EntityTag.Settlement: return "Settlement";
                case EntityTag.Other:      return "Other";
                default:                   return t.ToString();
            }
        }

        // 2026-08-02: IconSpriteId(this EventTag) was REMOVED. It returned
        // General\Icons\swords / skull / crown / scales / star — none of which exist in any
        // shipped GUI file, so every one of them would have rendered as a blank square with
        // no error. Nothing consumed it: its only caller was ChronicleEntryVM.IconSpriteId,
        // which no prefab ever bound, and that property plus its OnPropertyChanged notify
        // were deleted in the same change. The letter glyphs in IconTextChar are the live
        // per-row type indicator and are unaffected.
    }
}
