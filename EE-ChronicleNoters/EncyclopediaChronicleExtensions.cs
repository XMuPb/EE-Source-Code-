using System.Xml;
using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.Prefabs2;

namespace EditableEncyclopedia
{
    /// <summary>
    /// UIExtenderEx PrefabExtension patches that inject a Chronicle container
    /// directly into #RightSideList as a new child.
    ///
    /// JournalSectionInjector discovers the container by its known ID and fills
    /// it with dynamic content (header, filters, entries, pagination).
    /// </summary>

    // ── Settlement Page ──────────────────────────────────────────────
    [PrefabExtension("EncyclopediaSettlementPage",
        "descendant::ListPanel[@Id='RightSideList']")]
    public class SettlementChronicleExtension : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Child;

        [PrefabExtensionXmlDocument]
        public XmlDocument GetChronicleContainer()
        {
            return ChronicleContainerXml.Create();
        }
    }

    // ── Clan Page ────────────────────────────────────────────────────
    [PrefabExtension("EncyclopediaClanPage",
        "descendant::ListPanel[@Id='RightSideList']")]
    public class ClanChronicleExtension : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Child;

        [PrefabExtensionXmlDocument]
        public XmlDocument GetChronicleContainer()
        {
            return ChronicleContainerXml.Create();
        }
    }

    // ── Kingdom / Faction Page ───────────────────────────────────────
    [PrefabExtension("EncyclopediaFactionPage",
        "descendant::ListPanel[@Id='RightSideList']")]
    public class FactionChronicleExtension : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Child;

        [PrefabExtensionXmlDocument]
        public XmlDocument GetChronicleContainer()
        {
            return ChronicleContainerXml.Create();
        }
    }

    /// <summary>
    /// Shared XML fragment for the Chronicle container widget.
    /// An empty vertical ListPanel that JournalSectionInjector discovers by ID
    /// and fills with dynamic content (header, filters, entries, pagination).
    /// </summary>
    internal static class ChronicleContainerXml
    {
        // ====================================================================
        // VERSION-CONDITIONAL ENUM (2026-05-28): Bannerlord 1.4.x inverts the
        // LayoutMethod enum (VerticalBottomToTop renders top-down). 1.3.x renders
        // as named. We resolve at runtime via BannerlordVersion.TopDownLayoutEnumName()
        // and substitute the right token into the XML. Children are added
        // header -> filters -> entries -> pagination, header at top.
        // Memory: reference_bannerlord_layoutmethod_enum_inverted.
        // ====================================================================
        private const string ContainerXmlTemplate =
            @"<ListPanel Id=""EditableEncyclopedia_ChronicleContainer""
                WidthSizePolicy=""StretchToParent""
                HeightSizePolicy=""CoverChildren""
                StackLayout.LayoutMethod=""{LAYOUT}""
                MarginTop=""10"" />";

        public static XmlDocument Create()
        {
            var doc = new XmlDocument();
            string xml = ContainerXmlTemplate.Replace("{LAYOUT}", BannerlordVersion.TopDownLayoutEnumName());
            doc.LoadXml(xml);
            return doc;
        }
    }
}
