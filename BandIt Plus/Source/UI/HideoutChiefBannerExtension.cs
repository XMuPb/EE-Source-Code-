using System.Xml;
using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.Prefabs2;

namespace BandItPlus.UI
{
    // v110 (2026-05-15): UIExtenderEx PrefabExtension patches that inject a
    // MaskedTextureWidget rendering the chief's clan banner INTO the vanilla
    // settlement nameplate prefab. The widget is positioned to overlay the
    // existing SettlementBannerWidget area and is conditionally visible only
    // for hideouts that have a registered chief (HasHideoutChiefBanner=true).
    //
    // The injected widget binds to two new properties on SettlementNameplateVM
    // exposed via BandItPlusNameplateMixin's [DataSourceProperty] members:
    //   - HideoutChiefBannerVisual (BannerImageIdentifierVM)
    //   - HasHideoutChiefBanner (bool)
    //
    // Three prefab sizes (Small/Medium/Large) each get their own patch with
    // the size-appropriate widget dimensions, mirroring the constants defined
    // in each vanilla prefab:
    //   Small:  width=34, height=45  (Small.BannerWidth/Height)
    //   Medium: width=45, height=60  (Medium.BannerWidth/Height)
    //   Large:  width=54, height=72  (Large.BannerWidth/Height)

    // ── Small Nameplate ───────────────────────────────────────────────────
    // v110.2 (2026-05-15): DIAGNOSTIC — replaced with a bright red 60x60
    // UNCONDITIONAL Widget to test whether XML injection is reaching the
    // capsule at all. If user sees red boxes attached to EVERY settlement
    // nameplate (towns, castles, hideouts alike), injection works → revert
    // to MaskedTextureWidget + bindings. If user sees nothing, the XPath /
    // [PrefabExtension] discovery is failing.
    [PrefabExtension("SettlementNameplateItemSmall",
        "descendant::ButtonWidget[@Id='SettlementNameplateCapsuleWidget']")]
    public class HideoutChiefBannerSmallExtension : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Child;

        [PrefabExtensionXmlDocument]
        public XmlDocument GetWidget()
        {
            var doc = new XmlDocument();
            doc.LoadXml(@"<Widget
                Id=""BandItPlus_DiagBox""
                WidthSizePolicy=""Fixed"" HeightSizePolicy=""Fixed""
                SuggestedWidth=""60"" SuggestedHeight=""60""
                HorizontalAlignment=""Right"" VerticalAlignment=""Center""
                MarginRight=""-70""
                Sprite=""StdAssets\rectangle_card""
                Color=""#FF0000FF""
                IsVisible=""true""
                IsDisabled=""true"" />");
            return doc;
        }
    }

    // ── Medium Nameplate ──────────────────────────────────────────────────
    [PrefabExtension("SettlementNameplateItemMedium",
        "descendant::ButtonWidget[@Id='SettlementNameplateCapsuleWidget']")]
    public class HideoutChiefBannerMediumExtension : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Child;

        [PrefabExtensionXmlDocument]
        public XmlDocument GetWidget()
        {
            var doc = new XmlDocument();
            doc.LoadXml(@"<MaskedTextureWidget
                Id=""BandItPlus_HideoutChiefBanner""
                DataSource=""{HideoutChiefBannerVisual}""
                WidthSizePolicy=""Fixed"" HeightSizePolicy=""Fixed""
                SuggestedWidth=""45"" SuggestedHeight=""60""
                HorizontalAlignment=""Left"" VerticalAlignment=""Center""
                MarginLeft=""6""
                Brush=""Nameplate.FlatBanner.Medium""
                AdditionalArgs=""@AdditionalArgs""
                ImageId=""@Id""
                TextureProviderName=""@TextureProviderName""
                IsVisible=""@HasHideoutChiefBanner""
                IsDisabled=""true"" />");
            return doc;
        }
    }

    // ── Large Nameplate ───────────────────────────────────────────────────
    [PrefabExtension("SettlementNameplateItemLarge",
        "descendant::ButtonWidget[@Id='SettlementNameplateCapsuleWidget']")]
    public class HideoutChiefBannerLargeExtension : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Child;

        [PrefabExtensionXmlDocument]
        public XmlDocument GetWidget()
        {
            var doc = new XmlDocument();
            doc.LoadXml(@"<MaskedTextureWidget
                Id=""BandItPlus_HideoutChiefBanner""
                DataSource=""{HideoutChiefBannerVisual}""
                WidthSizePolicy=""Fixed"" HeightSizePolicy=""Fixed""
                SuggestedWidth=""54"" SuggestedHeight=""72""
                HorizontalAlignment=""Left"" VerticalAlignment=""Center""
                MarginLeft=""6""
                Brush=""Nameplate.FlatBanner.Large""
                AdditionalArgs=""@AdditionalArgs""
                ImageId=""@Id""
                TextureProviderName=""@TextureProviderName""
                IsVisible=""@HasHideoutChiefBanner""
                IsDisabled=""true"" />");
            return doc;
        }
    }
}
