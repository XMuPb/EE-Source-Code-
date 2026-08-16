using TaleWorlds.Library;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Categories for timeline events, used for filtering and color-coding.
    /// </summary>
    public enum TimelineCategory
    {
        All,
        Battle,
        Siege,
        Capture,
        Death,
        Marriage,
        Birth,
        Family,
        Tournament,
        Politics,
        Raid,
        Vengeance,
        Insult,
        Peace,
        War,
        Army,
        Quest,
        Skill,
        Rebellion,
        Event // catch-all
    }

    /// <summary>
    /// A single timeline entry representing a personal hero biography event.
    /// </summary>
    public class TimelineEntry
    {
        public string Date;
        public string Text;
        public string RawText; // original text with «h:id»Name«/h» markers for clickable links
        public string Icon;
        public string SourceObjectId;
        public int SortKey; // numeric key for chronological sorting (year*10 + season)
        public TimelineCategory Category;
        public bool IsManualEntry; // true if this is a player-written note (editable/deletable)
        public int OriginalJournalIndex = -1; // index in the hero's journal (for edit/delete of manual entries)

        /// <summary>
        /// Returns the display color for this entry's category.
        /// Colors are muted to blend with the encyclopedia's medieval parchment aesthetic.
        /// </summary>
        public Color GetCategoryColor()
        {
            switch (Category)
            {
                case TimelineCategory.Death:
                    return new Color(0.75f, 0.25f, 0.25f, 1f); // muted red
                case TimelineCategory.Battle:
                case TimelineCategory.War:
                    return new Color(0.80f, 0.55f, 0.25f, 1f); // warm orange
                case TimelineCategory.Siege:
                    return new Color(0.70f, 0.50f, 0.30f, 1f); // bronze
                case TimelineCategory.Capture:
                    return new Color(0.60f, 0.45f, 0.55f, 1f); // muted purple
                case TimelineCategory.Marriage:
                    return new Color(0.70f, 0.55f, 0.65f, 1f); // soft rose
                case TimelineCategory.Birth:
                case TimelineCategory.Family:
                    return new Color(0.55f, 0.70f, 0.55f, 1f); // soft green
                case TimelineCategory.Tournament:
                    return new Color(0.75f, 0.70f, 0.35f, 1f); // gold
                case TimelineCategory.Politics:
                    return new Color(0.50f, 0.60f, 0.70f, 1f); // steel blue
                case TimelineCategory.Raid:
                    return new Color(0.70f, 0.40f, 0.30f, 1f); // rust
                case TimelineCategory.Vengeance:
                    return new Color(0.65f, 0.30f, 0.40f, 1f); // dark crimson
                case TimelineCategory.Insult:
                    return new Color(0.65f, 0.55f, 0.40f, 1f); // tan
                case TimelineCategory.Peace:
                    return new Color(0.50f, 0.65f, 0.60f, 1f); // sage
                case TimelineCategory.Army:
                    return new Color(0.65f, 0.50f, 0.35f, 1f); // army brown
                case TimelineCategory.Quest:
                    return new Color(0.55f, 0.65f, 0.45f, 1f); // olive green
                case TimelineCategory.Skill:
                    return new Color(0.55f, 0.60f, 0.70f, 1f); // light steel
                case TimelineCategory.Rebellion:
                    return new Color(0.75f, 0.35f, 0.30f, 1f); // rebel red
                default:
                    return new Color(0.60f, 0.58f, 0.52f, 1f); // neutral parchment
            }
        }
    }
}
