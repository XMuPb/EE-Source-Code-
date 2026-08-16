using System.Xml;
using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.Prefabs2;

namespace EditableEncyclopedia
{
    /// <summary>
    /// UIExtenderEx PrefabExtension that injects a Chronicle button into the MapBar.
    /// Inserts as the last child of BottomInfoBar (a horizontal ListPanel) so the
    /// button appears inline with the existing info bar items (gold, influence, etc.).
    ///
    /// Click handling is hooked programmatically by GlobalChroniclePanel.TryInjectButton.
    /// </summary>
    [PrefabExtension("MapBar", "descendant::ListPanel[@Id='BottomInfoBar']")]
    public class MapBarChronicleExtension : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Child;

        [PrefabExtensionXmlDocument]
        public XmlDocument GetChronicleButton()
        {
            var doc = new XmlDocument();
            doc.LoadXml(
                @"<ButtonWidget Id=""EditableEncyclopedia_ChronicleButton""
                    DoNotPassEventsToChildren=""true""
                    WidthSizePolicy=""Fixed"" HeightSizePolicy=""Fixed""
                    SuggestedWidth=""40"" SuggestedHeight=""40""
                    VerticalAlignment=""Center""
                    MarginLeft=""8"" MarginRight=""8""
                    Brush=""MapBar.PanelButton"">
                    <Children>
                        <TextWidget WidthSizePolicy=""StretchToParent"" HeightSizePolicy=""StretchToParent""
                            HorizontalAlignment=""Center"" VerticalAlignment=""Center""
                            Brush=""MapBar.PanelButton.Text""
                            Text=""\u270D"" />
                    </Children>
                </ButtonWidget>");
            return doc;
        }
    }
}
