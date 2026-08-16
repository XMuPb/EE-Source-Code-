using System.Collections.Generic;
using TaleWorlds.SaveSystem;

namespace EditableEncyclopedia
{
    // Public data types exposed by EE-Core's API surface.
    // These shapes are part of the save format contract — DO NOT rename, reorder fields,
    // or change the SaveableTypeDefiner base ID without a save schema migration.

    /// <summary>
    /// Represents a single journal entry with a campaign date and text.
    /// </summary>
    public class JournalEntry
    {
        public string Date;
        public string Text;
    }

    public class RelationHistoryEntry
    {
        public string Date;
        public string Change;
        public string Text;
    }

    /// <summary>
    /// Tells the save system about our saveable fields.
    /// Base ID 6_317_420 is the save-compat anchor — keep stable across releases.
    /// </summary>
    public class EditableEncyclopediaSaveDefiner : SaveableTypeDefiner
    {
        public EditableEncyclopediaSaveDefiner() : base(6_317_420) { }

        protected override void DefineClassTypes()
        {
            // No custom classes to register beyond the behavior itself.
        }

        protected override void DefineContainerDefinitions()
        {
            ConstructContainerDefinition(typeof(Dictionary<string, string>));
            ConstructContainerDefinition(typeof(Dictionary<string, int>));
        }
    }

    /// <summary>
    /// Import mode for tag import operations.
    /// </summary>
    public enum TagImportMode
    {
        /// <summary>Overwrite existing tags for the same object ID.</summary>
        Overwrite,
        /// <summary>Skip objects that already have tags.</summary>
        Skip,
        /// <summary>Merge imported tags with existing tags (union, deduplicated).</summary>
        Merge
    }

    /// <summary>
    /// Holds a tag name and its usage count across all entries.
    /// </summary>
    public class TagUsageInfo
    {
        public string Tag;
        public int Count;
    }
}
