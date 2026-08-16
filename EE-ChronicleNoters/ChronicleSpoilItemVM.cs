using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace EditableEncyclopedia.ChronicleNoters
{
    /// <summary>
    /// One looted item stack in the reading pane's SPOILS strip: a 40px item icon,
    /// an "x12" count caption, the item's name kept as a text fallback, and — on hover —
    /// the GAME'S OWN rich item tooltip for the resolved <see cref="ItemObject"/>
    /// (coloured stats, stat bars, type icons), exactly as the encyclopedia shows it.
    ///
    /// 2026-08-02: created for the spoils rendering half of the battle/raid/conquest
    /// loot feature. The data half lives in the sibling EditableEncyclopedia module
    /// (ChronicleSpoilsCollector.cs / EncyclopediaEditBehavior._chronicleSpoils) and is
    /// read through EditableEncyclopediaAPI.GetChronicleSpoils(entityId, date, text).
    ///
    /// <see cref="ItemImage"/> is typed `object`, NOT ItemImageIdentifierVM, for the same
    /// reason ChronicleEntryVM.HeroPortrait and ChronicleEntryVM.ClanBanner are: the
    /// ImageIdentifiers namespace has moved between game patches
    /// (TaleWorlds.Core.ViewModelCollection.ImageIdentifiers vs the older
    /// TaleWorlds.Core.ImageIdentifiers), so the concrete type is resolved by reflection in
    /// ChronicleEntryPopulator.EnsureItemImageReflection() and never bound at compile time.
    /// Verified present in this install by reflecting
    /// bin\Win64_Shipping_Client\TaleWorlds.Core.ViewModelCollection.dll:
    ///   TaleWorlds.Core.ViewModelCollection.ImageIdentifiers.ItemImageIdentifierVM
    ///     : ImageIdentifierVM
    ///     .ctor(TaleWorlds.Core.ItemObject itemObject, System.String bannerCode)
    /// whose body stores both args, news up TaleWorlds.Core.ImageIdentifiers.ItemImageIdentifier
    /// (ItemObject, string) and assigns base ImageIdentifierVM.ImageIdentifier — the same shape
    /// CharacterImageIdentifierVM(CharacterCode) and BannerImageIdentifierVM(Banner, bool)
    /// already use in this prefab. The ctor never dereferences bannerCode, so an empty string
    /// is safe for a non-banner item.
    ///
    /// The tooltip is NOT hand-composed any more. Hand-built TooltipProperty rows could never
    /// match Native's item card, so this now calls the engine's own renderer, copying the
    /// proven recipe already shipping in the sibling module's Inventory Lore feature
    /// (EditableEncyclopedia\InventoryLore\InventoryLoreSectionInjector.cs, ShowNativeItemTooltip):
    ///   TaleWorlds.Library.InformationManager.ShowTooltip(
    ///       typeof(ItemObject), new object[] { new EquipmentElement(item) })
    /// The dispatch key MUST be typeof(ItemObject) and args[0] MUST be a BOXED EquipmentElement:
    /// SandBox's registered refresher does `args[0] as EquipmentElement?`, so handing it a raw
    /// ItemObject renders nothing at all — silently. Signatures verified by reflecting this
    /// install's bin\Win64_Shipping_Client:
    ///   TaleWorlds.Library.InformationManager.ShowTooltip(Type type, Object[] args) / HideTooltip()
    ///   TaleWorlds.Core.MBInformationManager.ShowHint(String hint) / HideInformations()
    ///   TaleWorlds.Core.EquipmentElement (struct)
    ///     .ctor(ItemObject item, ItemModifier, ItemObject cosmeticItem, Boolean isQuestItem)
    ///       -- trailing three are optional, so new EquipmentElement(item) is the shipped form.
    /// It renders over this panel because Native's TaleWorlds.MountAndBlade.GauntletUI
    /// .GauntletInformationView is a ScreenSystem GlobalLayer (layer order 115000) that
    /// subscribes to InformationManager.OnShowTooltip in its ctor — far above this panel's own
    /// layer (order 9500), so the card draws over the panel, not behind it.
    ///
    /// MBInformationManager is still reached by REFLECTION (see ResolveHintApi) rather than
    /// bound at compile time, matching the reference implementation: it is only the degraded
    /// text fallback, and a missing method there must cost a hint, not throw.
    ///
    /// The prefab binds the image through a WRAPPER Widget that carries
    /// IsVisible="@HasItemImage"; the ImageIdentifierWidget itself only carries
    /// DataSource="{ItemImage}" plus TextureProviderName + ImageId. DataSource re-scopes the
    /// binding context, so an IsVisible on the image widget would resolve against the image
    /// VM and silently never fire. For the same reason the hover commands
    /// Command.HoverBegin="ExecuteBeginHint" / Command.HoverEnd="ExecuteEndHint" sit on the
    /// ItemTemplate ROOT ListPanel — the widget whose data context IS this VM — and that root
    /// carries DoNotPassEventsToChildren="true" so the ImageIdentifierWidget underneath cannot
    /// swallow the hover. That is the shipped idiom: all 11 non-button Widgets in
    /// Native/SandBox/SandBoxCore/StoryMode that carry Command.HoverBegin over an image child
    /// use DoNotPassEventsToChildren on the parent and leave the child untouched (e.g. SandBox
    /// \GUI\Prefabs\KingdomManagement\Decision\DeclareWarDecisionPanel.xml line 57).
    /// </summary>
    public class ChronicleSpoilItemVM : ViewModel
    {
        /// <summary>Name captions sit in an 80px cell; anything longer is cut here.</summary>
        private const int NameFallbackMaxChars = 14;

        /// <param name="item">
        /// The already-resolved ItemObject the populator looked up for this stack. Kept rather
        /// than re-resolved lazily: ChronicleEntryPopulator.BuildSpoilItemRow ALREADY calls
        /// MBObjectManager.GetObject of ItemObject and returns null (skipping the row outright)
        /// when it fails, so by the time this ctor runs the object is known-good and a second
        /// lookup could only fail in ways the caller already ruled out.
        /// May still be null defensively; every ItemObject-derived tooltip row is then omitted
        /// and the card degrades to just the title and the quantity.
        /// </param>
        public ChronicleSpoilItemVM(object itemImage, int count, string itemName, ItemObject item)
        {
            _itemImage = itemImage;
            _count = count;
            _itemName = itemName ?? string.Empty;
            _item = item;
        }

        // ── Item icon (ItemImageIdentifierVM, typed object — see class remarks) ──
        private object _itemImage;
        [DataSourceProperty]
        public object ItemImage
        {
            get { return _itemImage; }
            set
            {
                if (_itemImage != value)
                {
                    _itemImage = value;
                    OnPropertyChanged("ItemImage");
                    OnPropertyChanged("HasItemImage");
                    OnPropertyChanged("HasNameFallback");
                }
            }
        }

        /// <summary>Gate for the icon WRAPPER widget (never put this on the ImageIdentifierWidget).</summary>
        [DataSourceProperty]
        public bool HasItemImage { get { return _itemImage != null; } }

        // ── Count caption ────────────────────────────────────────────
        private int _count;
        public int Count
        {
            get { return _count; }
            set
            {
                if (_count != value)
                {
                    _count = value;
                    OnPropertyChanged("CountLabel");
                    OnPropertyChanged("HasCountLabel");
                }
            }
        }

        /// <summary>"x12". Empty for a non-positive count so the caption simply reads blank.</summary>
        [DataSourceProperty]
        public string CountLabel
        {
            get
            {
                if (_count <= 0) return string.Empty;
                return "x" + _count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        [DataSourceProperty]
        public bool HasCountLabel { get { return _count > 0; } }

        // ── Item name (text fallback when no icon could be built) ────
        private string _itemName;
        [DataSourceProperty]
        public string ItemName
        {
            get { return _itemName; }
            set
            {
                if (_itemName != value)
                {
                    _itemName = value ?? string.Empty;
                    OnPropertyChanged("ItemName");
                    OnPropertyChanged("NameFallbackLabel");
                    OnPropertyChanged("HasNameFallback");
                }
            }
        }

        /// <summary>
        /// Truncated name shown INSTEAD of the icon when the icon could not be built (i.e. the
        /// reflection lookup failed on some future patch). Items whose StringId does not resolve
        /// to an ItemObject at all are dropped by the populator and never reach this VM, so a
        /// spoils cell is never a blank tile.
        /// </summary>
        [DataSourceProperty]
        public string NameFallbackLabel
        {
            get
            {
                string n = _itemName ?? string.Empty;
                if (n.Length > NameFallbackMaxChars) n = n.Substring(0, NameFallbackMaxChars);
                return n;
            }
        }

        [DataSourceProperty]
        public bool HasNameFallback
        {
            get { return _itemImage == null && !string.IsNullOrEmpty(_itemName); }
        }

        // ══════════════════════════════════════════════════════════════
        //  Hover tooltip — the engine's own item card (see class remarks)
        // ══════════════════════════════════════════════════════════════

        private readonly ItemObject _item;

        /// <summary>
        /// Which tooltip actually fired on the last HoverBegin. The two paths are dismissed by
        /// DIFFERENT engine calls — InformationManager.HideTooltip() for the rich item card,
        /// MBInformationManager.HideInformations() for the plain hint — so ExecuteEndHint has to
        /// remember which one it owes a dismissal to, or the card stays stuck under the cursor.
        /// </summary>
        private bool _nativeTooltipShown;
        private bool _textHintShown;

        /// <summary>
        /// Bound by the prefab's ItemTemplate root as Command.HoverBegin="ExecuteBeginHint".
        /// Rich native item card first; if it cannot fire (no ItemObject, or the engine call
        /// throws on some future patch) it degrades to the plain one-line hint so the item's
        /// identity is never lost. Public and parameterless: that is what Gauntlet's command
        /// binder looks for on the data source.
        /// </summary>
        public void ExecuteBeginHint()
        {
            if (TryShowNativeItemTooltip())
            {
                _nativeTooltipShown = true;
                return;
            }

            string text = BuildPlainHintText();
            if (string.IsNullOrEmpty(text)) return;
            if (TryShowTextHint(text)) _textHintShown = true;
        }

        /// <summary>Bound as Command.HoverEnd="ExecuteEndHint". Hides whichever path fired.</summary>
        public void ExecuteEndHint()
        {
            if (_nativeTooltipShown)
            {
                _nativeTooltipShown = false;
                HideNativeItemTooltip();
            }
            if (_textHintShown)
            {
                _textHintShown = false;
                HideTextHint();
            }
        }

        /// <summary>
        /// Shows the game's RICH item tooltip. args[0] is a BOXED EquipmentElement (a struct), NOT
        /// a raw ItemObject — SandBox's registered refresher does `args[0] as EquipmentElement?`
        /// and a raw ItemObject would null that out and render an empty card.
        ///
        /// No MBObjectManager lookup here (unlike the reference implementation, which only had a
        /// string id): ChronicleEntryPopulator.BuildSpoilItemRow already resolved the ItemObject
        /// and skipped the row outright when it failed, so _item is known-good by construction.
        ///
        /// [MethodImpl(NoInlining)] because a MissingMethodException raised while the JIT prepares
        /// a method cannot be caught by a try/catch inside that same method — the version-specific
        /// engine call therefore lives one frame down from ExecuteBeginHint's guard.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool TryShowNativeItemTooltip()
        {
            try
            {
                if (_item == null) return false;
                InformationManager.ShowTooltip(typeof(ItemObject),
                    new object[] { new EquipmentElement(_item) });
                return true;
            }
            catch { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void HideNativeItemTooltip()
        {
            try { InformationManager.HideTooltip(); }
            catch { }
        }

        /// <summary>Degraded path: MBInformationManager.ShowHint(text), resolved by reflection.</summary>
        private static bool TryShowTextHint(string text)
        {
            ResolveHintApi();
            if (_showHintMethod == null) return false;
            try { _showHintMethod.Invoke(null, new object[] { text }); return true; }
            catch { return false; }
        }

        private static void HideTextHint()
        {
            ResolveHintApi();
            if (_hideInfoMethod == null) return;
            try { _hideInfoMethod.Invoke(null, null); }
            catch { }
        }

        // TaleWorlds.Core.MBInformationManager.ShowHint(String) / HideInformations() — the exact
        // pair HintViewModel.ExecuteBeginHint/ExecuteEndHint uses. Resolved once by reflection so
        // no compile-time namespace is baked in; a miss simply means no fallback hint.
        private static bool _hintApiResolved;
        private static MethodInfo _showHintMethod;
        private static MethodInfo _hideInfoMethod;

        private static void ResolveHintApi()
        {
            if (_hintApiResolved) return;
            _hintApiResolved = true;
            try
            {
                Type mbType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    mbType = asm.GetType("TaleWorlds.Core.MBInformationManager");
                    if (mbType != null) break;
                }
                if (mbType == null) return;
                _showHintMethod = mbType.GetMethod("ShowHint",
                    BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string) }, null);
                _hideInfoMethod = mbType.GetMethod("HideInformations",
                    BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
            }
            catch { }
        }

        /// <summary>Fallback hint body: "Steppe Horse (x4)".</summary>
        private string BuildPlainHintText()
        {
            string n = _itemName ?? string.Empty;
            if (string.IsNullOrEmpty(n))
            {
                try { if (_item != null && _item.Name != null) n = _item.Name.ToString() ?? string.Empty; }
                catch { n = string.Empty; }
            }
            if (string.IsNullOrEmpty(n)) return string.Empty;
            if (_count > 1) return n + " (x" + _count.ToString(CultureInfo.InvariantCulture) + ")";
            return n;
        }

    }
}
