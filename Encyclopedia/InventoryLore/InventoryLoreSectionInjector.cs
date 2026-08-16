using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;
using TaleWorlds.ScreenSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Injects the Inventory Lore section into encyclopedia pages.
    /// Structure mirrors HeroTimelineSectionInjector: schedule from any thread,
    /// act only on the main thread, retry while the widget tree settles.
    /// </summary>
    public static class InventoryLoreSectionInjector
    {
        private static readonly List<Widget> _injectedWidgets = new List<Widget>();
        private static readonly Dictionary<string, bool> _collapseStates = new Dictionary<string, bool>();

        // Which cell is expanded, per entity page.
        private static readonly Dictionary<string, int> _selectedIndex = new Dictionary<string, int>();

        // Distinct widget-event names seen on a card, logged once each so the log (not guesswork)
        // reveals the real hover event name. Diagnostic aid for the hover tooltip.
        private static readonly HashSet<string> _loggedCardEvents = new HashSet<string>();

        // Current grid page, per entity page (for pagination).
        private static readonly Dictionary<string, int> _currentPage = new Dictionary<string, int>();

        // Slot background brush, resolved once (BrushWidget is the only thing that actually
        // renders — a plain Widget + Color draws nothing because Color merely tints a sprite).
        private static Brush _slotBgBrush;
        private static bool _slotBrushTried;

        private static volatile string _pendingEntityId;
        private static volatile bool _retryPending;
        private static volatile bool _clearPending;
        private static int _retryCount;
        private static System.Threading.Timer _retryTimer;

        private const int MaxRetries = 10;
        private const int RetryDelayMs = 150;
        private const int InitialInjectDelayMs = 700; // after Timeline's 600ms
        private const int GridColumns = 7;
        private const int GridColumnsNarrow = 6;
        private const float NarrowThreshold = 500f;
        private const float CardWidth = 82f;
        private const float CardHeight = 124f;
        private const float IconSize = 56f; // slot size; the native equipment slot is 65 tall
        private const float NameHeight = 30f; // fixed 2-line name area so worth always has room below
        private const float RowHeight = CardHeight + 8f; // rows are FIXED height so the scroll
                                                        // viewport math is exact and the last row
                                                        // is never clipped mid-card
        private const int PageRows = 6;       // scroll viewport shows exactly this many full rows

        private static readonly BindingFlags AllFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static void ScheduleClear()
        {
            DisposeRetryTimer();
            _retryPending = false;
            _clearPending = true;
        }

        public static void TickMainThread()
        {
            if (_clearPending)
            {
                _clearPending = false;
                RemoveOldWidgets();
            }
            if (_retryPending)
            {
                _retryPending = false;
                if (!EncyclopediaPageTracker.IsEncyclopediaOpen())
                {
                    _retryCount = MaxRetries;
                    DisposeRetryTimer();
                    return;
                }
                if (!string.IsNullOrEmpty(_pendingEntityId))
                    DoInject(_pendingEntityId);
            }
        }

        private static void DisposeRetryTimer()
        {
            var t = _retryTimer;
            _retryTimer = null;
            if (t != null)
            {
                try { t.Dispose(); }
                catch (Exception ex) { MCMSettings.DebugLog("[InventoryLoreSectionInjector]: " + ex.ToString()); }
            }
        }

        public static void InjectInventorySection(string entityId)
        {
            DisposeRetryTimer();
            _retryPending = false;
            _pendingEntityId = entityId;
            _retryCount = 0;
            _retryTimer = new System.Threading.Timer(_ => { _retryPending = true; },
                null, InitialInjectDelayMs, System.Threading.Timeout.Infinite);
        }

        /// <summary>
        /// Removes only widgets this injector created. Never walks the tree by
        /// type or id, so other sections cannot be affected.
        /// </summary>
        private static void RemoveOldWidgets()
        {
            foreach (var w in _injectedWidgets)
            {
                try
                {
                    if (w != null && w.ParentWidget != null)
                        w.ParentWidget.RemoveChild(w);
                }
                catch (Exception ex) { MCMSettings.DebugLog("[InventoryLoreSectionInjector]: " + ex.ToString()); }
            }
            _injectedWidgets.Clear();
        }

        private static void ScheduleRetry()
        {
            if (_retryCount >= MaxRetries) return;
            _retryCount++;
            DisposeRetryTimer();
            _retryTimer = new System.Threading.Timer(_ => { _retryPending = true; },
                null, RetryDelayMs, System.Threading.Timeout.Infinite);
        }

        private static void DoInject(string entityId)
        {
            try
            {
                RemoveOldWidgets();

                if (EncyclopediaEditBehavior.Instance == null) return;
                if (MCMSettings.Instance != null && !MCMSettings.Instance.EnableInventoryLore) return;

                var entries = InventoryLoreCollector.Collect(entityId);
                if (entries.Count == 0) return; // no empty section, ever

                var topScreen = ScreenManager.TopScreen;
                if (topScreen == null) return;

                bool layerWasSizeFallback;
                GauntletLayer encLayer = LoreSectionInjector.FindEncyclopediaLayer(topScreen, out layerWasSizeFallback);
                if (encLayer == null) { ScheduleRetry(); return; }

                Widget root = LoreSectionInjector.GetLayerRootWidget(encLayer);
                if (root == null) { ScheduleRetry(); return; }

                UIContext uiContext = root.EventManager != null ? root.EventManager.Context as UIContext : null;
                if (uiContext == null) { ScheduleRetry(); return; }

                // Place the section right after the Info/description block, above the
                // other content sections (Timeline, Relation Notes, ...). FindInsertionAfterInfo
                // walks to just past Info and stops before the first content section, which is
                // exactly "at the top after Info". Fall back to the generic anchor if that fails.
                Widget parent;
                int index;
                bool found = LoreSectionInjector.FindInsertionAfterInfo(root, out parent, out index);
                if (found)
                    MCMSettings.DebugLog("InventoryLoreSectionInjector: placing after Info at index " + index);
                if (!found)
                {
                    found = LoreSectionInjector.FindInsertionPoint(root, out parent, out index);
                    if (found)
                        MCMSettings.DebugLog("InventoryLoreSectionInjector: after-Info anchor not found, "
                            + "falling back to generic anchor at index " + index);
                }
                if (!found)
                {
                    ScheduleRetry();
                    return;
                }

                BuildAndInsertSection(uiContext, parent, index, entityId, entries);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("InventoryLoreSectionInjector.DoInject: " + ex.ToString());
            }
        }

        private static void BuildAndInsertSection(UIContext uiContext, Widget parent, int index,
            string entityId, List<ItemLoreEntry> entries)
        {
            try
            {
                var native = LoreSectionInjector.ExtractNativeSectionParts(parent);
                if (native == null || native.ReferenceHeader == null)
                {
                    MCMSettings.DebugLog("InventoryLoreSectionInjector: no native header to harvest, skipping");
                    return;
                }

                bool narrow = parent != null && parent.SuggestedWidth > 0
                    && parent.SuggestedWidth < NarrowThreshold;
                int columns = narrow ? GridColumnsNarrow : GridColumns;

                // ── Header wrapper (button) ──
                Widget headerWrapper = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget")
                                    ?? new Widget(uiContext);
                headerWrapper.Id = "EE_ItemGridHeader";
                headerWrapper.WidthSizePolicy = SizePolicy.StretchToParent;
                headerWrapper.HeightSizePolicy = SizePolicy.CoverChildren;
                if (native.ReferenceHeader != null)
                {
                    headerWrapper.MarginTop = native.ReferenceHeader.MarginTop;
                    headerWrapper.MarginBottom = native.ReferenceHeader.MarginBottom;
                    headerWrapper.MarginLeft = native.ReferenceHeader.MarginLeft;
                    headerWrapper.MarginRight = native.ReferenceHeader.MarginRight;
                }
                headerWrapper.DoNotAcceptEvents = false;
                headerWrapper.DoNotPassEventsToChildren = true;

                // ── Header bar (horizontal list) ──
                Widget headerBar = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                                ?? new Widget(uiContext);
                headerBar.Id = "EE_ItemGridHeaderBar";
                if (native.ReferencePlacement != null)
                {
                    LoreSectionInjector.CopyLayoutProperties(native.ReferencePlacement, headerBar);
                    LoreSectionInjector.CopyOrSetHorizontalLayout(native.ReferencePlacement, headerBar);
                }
                else
                {
                    headerBar.WidthSizePolicy = SizePolicy.StretchToParent;
                    headerBar.HeightSizePolicy = SizePolicy.CoverChildren;
                    LoreSectionInjector.CopyOrSetHorizontalLayout(null, headerBar);
                }

                // ── Collapse arrow ──
                BrushWidget arrow = null;
                if (native.NativeIndicator != null)
                {
                    arrow = new BrushWidget(uiContext);
                    arrow.Id = "EE_ItemGridArrow";
                    LoreSectionInjector.CopyLayoutProperties(native.NativeIndicator, arrow);
                    if (native.IndicatorBrush != null) arrow.Brush = native.IndicatorBrush;
                    LoreSectionInjector.ForceWidgetBrushState(arrow, native.NativeIndicator);
                    headerBar.AddChild(arrow);
                }

                // ── Title ──
                var titleText = new TextWidget(uiContext);
                titleText.Id = "EE_ItemGridTitle";
                titleText.Text = "Inventory Lore (" + entries.Count + ")";
                if (native.NativeTitle != null)
                {
                    LoreSectionInjector.CopyLayoutProperties(native.NativeTitle, titleText);
                    if (native.HeaderTextBrush != null) titleText.Brush = native.HeaderTextBrush;
                }
                else
                {
                    titleText.WidthSizePolicy = SizePolicy.CoverChildren;
                    titleText.HeightSizePolicy = SizePolicy.CoverChildren;
                    titleText.VerticalAlignment = VerticalAlignment.Center;
                    if (native.HeaderTextBrush != null) titleText.Brush = native.HeaderTextBrush;
                }
                headerBar.AddChild(titleText);

                // ── Line separator ──
                if (native.NativeLine != null)
                {
                    Widget line = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ImageWidget")
                               ?? new BrushWidget(uiContext);
                    line.Id = "EE_ItemGridLine";
                    LoreSectionInjector.CopyLayoutProperties(native.NativeLine, line);
                    if (native.LineBrush != null && line is BrushWidget bwLine)
                        bwLine.Brush = native.LineBrush;
                    headerBar.AddChild(line);
                }

                headerWrapper.AddChild(headerBar);

                // ── Content container (vertical) ──
                float contentMarginLeft = 25f;
                if (native.NativeIndicator != null)
                    contentMarginLeft = native.NativeIndicator.SuggestedWidth
                        + native.NativeIndicator.MarginLeft + native.NativeIndicator.MarginRight + 5f;

                Widget contentContainer = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                                       ?? new Widget(uiContext);
                contentContainer.Id = "EE_ItemGridContent";
                contentContainer.WidthSizePolicy = SizePolicy.StretchToParent;
                contentContainer.HeightSizePolicy = SizePolicy.CoverChildren;
                contentContainer.MarginLeft = contentMarginLeft;
                contentContainer.MarginBottom = 5f;
                LoreSectionInjector.SetVerticalLayoutTopToBottom(contentContainer);

                // ── Brushes: real native frame (loaded from BrushFactory, not just harvested) +
                //    small content text + gold worth ──
                Brush frameBrush = LoadNativeBrush("Encyclopedia.Frame")
                                   ?? LoreSectionInjector.FindBrushByName(parent, "Encyclopedia.Frame");
                Brush nameBrush = LoreSectionInjector.FindBrushByName(parent, "Encyclopedia.SubPage.History.Text")
                                  ?? LoreSectionInjector.FindBrushByName(parent, "Encyclopedia.Stat.DefinitionText")
                                  ?? LoreSectionInjector.FindBrushByName(parent, "Encyclopedia.Stat.ValueText")
                                  ?? LoreSectionInjector.FindAnyTextBrush(parent)
                                  ?? native.HeaderTextBrush;
                Brush valueBrush = nameBrush != null ? nameBrush.Clone() : null;
                if (valueBrush != null)
                    LoreSectionHelpers.SetBrushColor(valueBrush, 0.82f, 0.68f, 0.32f, 1f); // muted gold for worth

                // ── Build the FULL grid into its own container, then wrap it in a ScrollablePanel
                //    when it's taller than PageRows, so the section stays compact and scrolls.
                //    Reuses the mod's proven ScrollablePanel > ClipRect > content pattern
                //    (see TryWrapInScrollablePanel in EditableEncyclopediaPatches). ──
                Widget gridContainer = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                                       ?? new Widget(uiContext);
                gridContainer.Id = "EE_ItemGridRows";
                gridContainer.WidthSizePolicy = SizePolicy.StretchToParent;
                gridContainer.HeightSizePolicy = SizePolicy.CoverChildren;
                LoreSectionInjector.SetVerticalLayoutTopToBottom(gridContainer);
                BuildGrid(uiContext, gridContainer, entries, entityId, columns, 0,
                    frameBrush, nameBrush, valueBrush);

                int totalRows = (entries.Count + columns - 1) / columns;
                if (totalRows > PageRows)
                {
                    // Exactly PageRows rows. Both the rows and the viewport are sized via
                    // SuggestedHeight only (NOT ScaledSuggestedHeight), so the engine's UI-scale
                    // factor multiplies both identically and exactly PageRows rows fit.
                    float targetHeight = PageRows * RowHeight;
                    Widget scroll = WrapInScroll(uiContext, gridContainer, targetHeight);
                    contentContainer.AddChild(scroll != null ? scroll : gridContainer);
                }
                else
                {
                    contentContainer.AddChild(gridContainer);
                }

                // Detail panel removed: clicking no longer expands a bottom panel; the hover
                // tooltip is the sole item view now (per user request).

                // ── Collapse behaviour (persisted) ──
                LoreSectionHelpers.HookCollapseToggleWithPersistence(headerWrapper, contentContainer,
                    arrow, native.ReferenceHeader, entityId, _collapseStates);

                // Apply persisted collapse state (in-memory, mirrors Timeline/Lore pattern)
                if (_collapseStates.TryGetValue(entityId, out bool isCollapsed) && isCollapsed)
                {
                    contentContainer.IsVisible = false;
                    if (arrow != null)
                    {
                        try
                        {
                            var setStateMethod = arrow.GetType().GetMethod("SetState",
                                AllFlags, null, new[] { typeof(string) }, null);
                            setStateMethod?.Invoke(arrow, new object[] { "Collapsed" });
                        }
                        catch (Exception ex) { MCMSettings.DebugLog("[InventoryLoreSectionInjector]: " + ex.ToString()); }
                    }
                }

                // Path 2 defensive validation: bail before inserting if the current page's
                // layout has been restructured by a known conflicting mod. Mirrors Timeline/Lore/Journal.
                if (!EncyclopediaAnchorHelper.IsSafeToInjectOnCurrentPage(parent, "InventoryLore"))
                    return;

                // ── Insert: content first at index, then header at index (header ends up above) ──
                try
                {
                    if (index >= 0 && index <= parent.ChildCount)
                    {
                        parent.AddChildAtIndex(contentContainer, index);
                        parent.AddChildAtIndex(headerWrapper, index);
                    }
                    else
                    {
                        parent.AddChild(headerWrapper);
                        parent.AddChild(contentContainer);
                    }
                }
                catch (Exception ex)
                {
                    MCMSettings.DebugLog("[InventoryLoreSectionInjector]: " + ex.ToString());
                    parent.AddChild(headerWrapper);
                    parent.AddChild(contentContainer);
                }

                _injectedWidgets.Add(headerWrapper);
                _injectedWidgets.Add(contentContainer);

                MCMSettings.DebugLog("InventoryLoreSectionInjector: inserted section with "
                    + entries.Count + " items (" + columns + " cols) for " + entityId);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("InventoryLoreSectionInjector.BuildAndInsertSection: " + ex.ToString());
            }
        }

        /// <summary>
        /// Builds a grid of item cells as horizontal rows of up to `columns` cells.
        /// Gauntlet has no grid widget, so a grid is rows of horizontal ListPanels.
        /// </summary>
        private static void BuildGrid(UIContext uiContext, Widget contentContainer,
            List<ItemLoreEntry> entries, string entityId, int columns, int startIndex,
            Brush frameBrush, Brush nameBrush, Brush valueBrush)
        {
            // Fixed-size framed cards packed into horizontal rows. Fixed size + ClipContents
            // keeps every card in its own box, so nothing overflows onto neighbours.
            // cellIndex is the GLOBAL item index (startIndex + i) so selection survives paging.
            Widget currentRow = null;
            for (int i = 0; i < entries.Count; i++)
            {
                if (i % columns == 0)
                {
                    currentRow = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                                 ?? new Widget(uiContext);
                    currentRow.Id = "EE_ItemGridRow_" + (i / columns);
                    currentRow.WidthSizePolicy = SizePolicy.StretchToParent;
                    // Fixed (not CoverChildren) so every row is exactly RowHeight and the scroll
                    // viewport shows whole rows instead of clipping the last one mid-card.
                    currentRow.HeightSizePolicy = SizePolicy.Fixed;
                    currentRow.SuggestedHeight = RowHeight;
                    LoreSectionInjector.CopyOrSetHorizontalLayout(null, currentRow);
                    contentContainer.AddChild(currentRow);
                }
                currentRow.AddChild(BuildCard(uiContext, entries[i], entityId, startIndex + i,
                    frameBrush, nameBrush, valueBrush));
            }
        }

        /// <summary>
        /// Prev / page-indicator / Next controls below the grid, shown only when the item
        /// count exceeds one page. Reuses the clickable-text button helper.
        /// </summary>
        private static void BuildPager(UIContext uiContext, Widget parent, string entityId,
            int page, int totalPages, Brush brush)
        {
            Widget row = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                         ?? new Widget(uiContext);
            row.Id = "EE_ItemGridPager";
            row.WidthSizePolicy = SizePolicy.CoverChildren;
            row.HeightSizePolicy = SizePolicy.CoverChildren;
            row.MarginTop = 8f;
            row.HorizontalAlignment = HorizontalAlignment.Center;
            LoreSectionInjector.CopyOrSetHorizontalLayout(null, row);

            if (page > 0)
            {
                int target = page - 1;
                AddDetailButton(uiContext, row, "EE_ItemGridPrev", "< Prev", brush, () =>
                {
                    _currentPage[entityId] = target;
                    InjectInventorySection(entityId);
                });
            }

            var lbl = new TextWidget(uiContext);
            lbl.Id = "EE_ItemGridPageLbl";
            lbl.Text = "  Page " + (page + 1) + " / " + totalPages + "  ";
            lbl.WidthSizePolicy = SizePolicy.CoverChildren;
            lbl.HeightSizePolicy = SizePolicy.CoverChildren;
            lbl.VerticalAlignment = VerticalAlignment.Center;
            if (brush != null) lbl.Brush = brush;
            row.AddChild(lbl);

            if (page < totalPages - 1)
            {
                int target = page + 1;
                AddDetailButton(uiContext, row, "EE_ItemGridNext", "Next >", brush, () =>
                {
                    _currentPage[entityId] = target;
                    InjectInventorySection(entityId);
                });
            }

            parent.AddChild(row);
        }

        /// <summary>
        /// One premium item card: a native frame, the item tableau, its name (with stack
        /// count) and its worth in denars. Clicking opens the detail panel. Fixed size so
        /// the row grid aligns and nothing overlaps.
        /// </summary>
        private static Widget BuildCard(UIContext uiContext, ItemLoreEntry entry,
            string entityId, int cellIndex, Brush frameBrush, Brush nameBrush, Brush valueBrush)
        {
            Widget card = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget")
                          ?? new Widget(uiContext);
            card.Id = "EE_ItemGridCell_" + cellIndex;
            card.WidthSizePolicy = SizePolicy.Fixed;
            card.HeightSizePolicy = SizePolicy.Fixed;
            card.SuggestedWidth = CardWidth;
            card.SuggestedHeight = CardHeight;
            card.MarginRight = 6f;
            card.MarginBottom = 6f;
            SetWidgetProperty(card, card.GetType(), "ClipContents", true);
            // Capture hover/click on the card itself instead of letting the child widgets (fill,
            // icon, border, text) swallow them — otherwise the card's HoverBegin/HoverEnd never
            // fire and the tooltip (and click) don't work.
            card.DoNotPassEventsToChildren = true;

            // Card panel background: a subtle dark fill (Color on a plain Widget renders a
            // filled rect — same trick the Timeline divider uses). The native Encyclopedia.Frame
            // brush isn't present on the page, so we can't harvest it; this reads as a framed slot.
            // (No Color-only card background: a bare Widget with a Color renders nothing.
            //  The visible framing comes from the BrushWidget slot below.)

            // Inner vertical stack: icon, name, worth.
            Widget inner = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                           ?? new Widget(uiContext);
            inner.Id = "EE_ItemGridCardInner_" + cellIndex;
            inner.WidthSizePolicy = SizePolicy.StretchToParent;
            inner.HeightSizePolicy = SizePolicy.StretchToParent;
            inner.MarginTop = 8f;
            inner.MarginBottom = 6f;
            inner.MarginLeft = 6f;
            inner.MarginRight = 6f;
            LoreSectionInjector.SetVerticalLayoutTopToBottom(inner);
            card.AddChild(inner);

            // Framed icon slot, drawn ENTIRELY IN C# (not a native frame icon): a dark fill
            // behind, then the item image ON TOP (child, so it is never covered), then a thin
            // bronze border on the four edges only (edges don't touch the centre, so the item
            // stays fully visible). Solid colours are a generic white sprite tinted by Color -
            // that's the only way to render a solid shape in Gauntlet.
            Widget iconHolder = new Widget(uiContext);
            iconHolder.Id = "EE_ItemGridIconHolder_" + cellIndex;
            iconHolder.WidthSizePolicy = SizePolicy.Fixed;
            iconHolder.HeightSizePolicy = SizePolicy.Fixed;
            iconHolder.SuggestedWidth = IconSize;
            iconHolder.SuggestedHeight = IconSize;
            iconHolder.HorizontalAlignment = HorizontalAlignment.Center;
            iconHolder.ClipContents = true;
            inner.AddChild(iconHolder);

            // 1) dark fill behind the item
            AddPanelFill(uiContext, iconHolder, "EE_ItemGridSlotFill_" + cellIndex,
                new Color(0.05f, 0.045f, 0.035f, 0.95f));

            // 2) the item image on top of the fill
            bool iconOk = TryCreateItemIcon(uiContext, iconHolder, entry.ItemId);
            if (!iconOk)
            {
                var qmark = new TextWidget(uiContext);
                qmark.Id = "EE_ItemGridIconFallback_" + cellIndex;
                qmark.Text = "?";
                qmark.HorizontalAlignment = HorizontalAlignment.Center;
                qmark.VerticalAlignment = VerticalAlignment.Center;
                if (nameBrush != null) qmark.Brush = nameBrush;
                iconHolder.AddChild(qmark);
            }

            // 3) thin bronze border on the four edges, on top (does not cover the centre)
            AddBorder(uiContext, iconHolder, "EE_ItemGridSlotEdge_" + cellIndex,
                new Color(0.46f, 0.37f, 0.21f, 1f), 2f);

            // Name (+ stack count). Fixed 2-line height + clip, so the worth line below always
            // has a deterministic spot and never gets pushed out / clipped.
            var name = new TextWidget(uiContext);
            name.Id = "EE_ItemGridName_" + cellIndex;
            name.Text = entry.Quantity > 1 ? entry.DisplayName + "  x" + entry.Quantity : entry.DisplayName;
            name.WidthSizePolicy = SizePolicy.StretchToParent;
            name.HeightSizePolicy = SizePolicy.Fixed;
            name.SuggestedHeight = NameHeight;
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.MarginTop = 5f;
            SetWidgetProperty(name, name.GetType(), "ClipContents", true);
            if (nameBrush != null) name.Brush = nameBrush;
            inner.AddChild(name);

            // Worth in gold (number only; the full "N denars" is in the tooltip and detail panel).
            var worth = new TextWidget(uiContext);
            worth.Id = "EE_ItemGridValue_" + cellIndex;
            worth.Text = entry.Value > 0 ? entry.Value.ToString("N0") : "-";
            worth.WidthSizePolicy = SizePolicy.StretchToParent;
            worth.HeightSizePolicy = SizePolicy.CoverChildren;
            worth.HorizontalAlignment = HorizontalAlignment.Center;
            worth.MarginTop = 1f;
            if (valueBrush != null) worth.Brush = valueBrush;
            inner.AddChild(worth);

            // Override marker (top-left).
            if (entry.HasOverride)
            {
                var dot = new TextWidget(uiContext);
                dot.Id = "EE_ItemGridDot_" + cellIndex;
                dot.Text = "*";
                dot.HorizontalAlignment = HorizontalAlignment.Left;
                dot.VerticalAlignment = VerticalAlignment.Top;
                if (valueBrush != null) dot.Brush = valueBrush;
                card.AddChild(dot);
            }

            // Hover tooltip: full name, type/stats, worth AND the lore prose — the same rich info
            // the click detail panel shows, so hovering an item reveals everything without a click.
            string qtyPart = entry.Quantity > 1 ? " (x" + entry.Quantity + ")" : "";
            string stats = !string.IsNullOrEmpty(entry.StatsBlock) ? entry.StatsBlock : entry.StatLine;
            string hoverLore = ItemLoreLoader.GetItemLore(entry.ItemId, entry.OwnerId,
                entry.DisplayName, entry.Category);
            // StatsBlock already ends with "Worth: N denars" — no separate worth line (avoids the dupe).
            string tooltipText = entry.DisplayName + qtyPart + "\n" + stats;
            if (!string.IsNullOrEmpty(hoverLore)) tooltipText += "\n\n" + hoverLore;
            TrySetTooltip(card, tooltipText, entry.ItemId);

            // Click intentionally does nothing now: the hover tooltip is the sole item view
            // (the click-to-expand detail panel was removed per user request).

            return card;
        }

        /// <summary>
        /// Creates a native item tableau using the same pipeline as
        /// TryCreateHeroPortrait: ImageIdentifierWidget + a texture provider.
        /// Returns null on any failure so the cell can fall back to text.
        /// </summary>
        private static bool TryCreateItemIcon(UIContext uiContext, Widget cell, string itemId)
        {
            try
            {
                // Resolve the three types by full name (verified present in the game DLLs).
                Type imgWidgetType = null, imgIdVMType = null, providerType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (imgWidgetType == null)
                        imgWidgetType = asm.GetType("TaleWorlds.MountAndBlade.GauntletUI.Widgets.ImageIdentifierWidget");
                    if (imgIdVMType == null)
                        imgIdVMType = asm.GetType("TaleWorlds.Core.ViewModelCollection.ImageIdentifiers.ItemImageIdentifierVM");
                    if (providerType == null)
                        providerType = asm.GetType("TaleWorlds.MountAndBlade.GauntletUI.TextureProviders.ImageIdentifiers.ItemImageTextureProvider");
                }
                if (imgWidgetType == null)
                {
                    MCMSettings.DebugLog("EE_ItemGrid: ImageIdentifierWidget type not found");
                    return false;
                }

                ItemObject item = MBObjectManager.Instance.GetObject<ItemObject>(itemId);
                if (item == null)
                {
                    MCMSettings.DebugLog("EE_ItemGrid: item not found: " + itemId);
                    return false;
                }

                // Build ItemImageIdentifierVM(item, "") to read the tableau imageId, args, provider name.
                string imageId = null, additionalArgs = "", providerName = "ItemImageTextureProvider";
                if (imgIdVMType != null)
                {
                    object vm = null;
                    foreach (var c in imgIdVMType.GetConstructors())
                    {
                        var cp = c.GetParameters();
                        if (cp.Length == 2 && cp[0].ParameterType.IsAssignableFrom(item.GetType()))
                        {
                            try { vm = c.Invoke(new object[] { item, "" }); }
                            catch (Exception ex) { MCMSettings.DebugLog("EE_ItemGrid VM ctor2: " + ex.Message); }
                            break;
                        }
                    }
                    if (vm == null)
                    {
                        foreach (var c in imgIdVMType.GetConstructors())
                        {
                            var cp = c.GetParameters();
                            if (cp.Length == 1 && cp[0].ParameterType.IsAssignableFrom(item.GetType()))
                            {
                                try { vm = c.Invoke(new object[] { item }); }
                                catch (Exception ex) { MCMSettings.DebugLog("EE_ItemGrid VM ctor1: " + ex.Message); }
                                break;
                            }
                        }
                    }
                    if (vm != null)
                    {
                        imageId = imgIdVMType.GetProperty("Id", AllFlags)?.GetValue(vm) as string;
                        additionalArgs = (imgIdVMType.GetProperty("AdditionalArgs", AllFlags)?.GetValue(vm) as string) ?? "";
                        string pn = imgIdVMType.GetProperty("TextureProviderName", AllFlags)?.GetValue(vm) as string;
                        if (!string.IsNullOrEmpty(pn)) providerName = pn;
                    }
                }

                MCMSettings.DebugLog("EE_ItemGrid: item=" + itemId
                    + " imageId=" + (string.IsNullOrEmpty(imageId) ? "EMPTY" : imageId)
                    + " provider=" + providerName);
                if (string.IsNullOrEmpty(imageId))
                    return false;

                // Create the widget and push id/args/provider name onto it.
                var imgWidget = (Widget)Activator.CreateInstance(imgWidgetType, new object[] { uiContext });
                imgWidget.Id = "EE_ItemGridIcon";
                imgWidget.WidthSizePolicy = SizePolicy.StretchToParent;
                imgWidget.HeightSizePolicy = SizePolicy.StretchToParent;
                // Same margins the native equipment slot gives its ImageIdentifierWidget, so the
                // item sits inside the slot sprite rather than covering its border.
                imgWidget.MarginLeft = 3f;
                imgWidget.MarginRight = 4f;
                imgWidget.MarginTop = 3f;
                imgWidget.MarginBottom = 4f;

                // Uniform sizing: make every tableau fill its square box the same way.
                try
                {
                    var fitProp = imgWidgetType.GetProperty("ImageFit", AllFlags);
                    if (fitProp != null && fitProp.CanWrite && fitProp.PropertyType.IsEnum)
                    {
                        foreach (var fitName in new[] { "Stretch", "Fill", "Fit" })
                        {
                            if (Enum.IsDefined(fitProp.PropertyType, fitName))
                            { fitProp.SetValue(imgWidget, Enum.Parse(fitProp.PropertyType, fitName)); break; }
                        }
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("EE_ItemGrid ImageFit: " + ex.Message); }

                SetWidgetProperty(imgWidget, imgWidgetType, "TextureProviderName", providerName);
                SetWidgetProperty(imgWidget, imgWidgetType, "ImageId", imageId);
                SetWidgetProperty(imgWidget, imgWidgetType, "AdditionalArgs", additionalArgs);

                var setTexProp = imgWidgetType.GetMethod("SetTextureProviderProperty", AllFlags);
                if (setTexProp != null)
                {
                    try
                    {
                        setTexProp.Invoke(imgWidget, new object[] { "ImageId", imageId });
                        setTexProp.Invoke(imgWidget, new object[] { "AdditionalArgs", additionalArgs });
                        setTexProp.Invoke(imgWidget, new object[] { "IsReleased", false });
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("EE_ItemGrid SetTextureProviderProperty: " + ex.Message); }
                }

                // Force IsReleased=false in the widget's provider-properties dictionary.
                Type pst = imgWidgetType;
                while (pst != null && pst != typeof(object))
                {
                    foreach (var f in pst.GetFields(BindingFlags.Instance | BindingFlags.NonPublic
                        | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    {
                        if (f.Name.Contains("roviderPropert") && f.FieldType.IsGenericType)
                        {
                            var dict = f.GetValue(imgWidget) as System.Collections.IDictionary;
                            if (dict != null) dict["IsReleased"] = false;
                        }
                    }
                    pst = pst.BaseType;
                }

                // Add to the tree BEFORE assigning the provider (UIContext propagation),
                // mirroring the proven hero-portrait pipeline.
                cell.AddChild(imgWidget);

                // Create the concrete ItemImageTextureProvider and assign it to the widget's
                // backing field. Setting TextureProviderName alone never instantiates one — that
                // was the root cause of blank icons.
                if (providerType == null)
                {
                    MCMSettings.DebugLog("EE_ItemGrid: ItemImageTextureProvider type not found — icon blank");
                    return true;
                }

                object provider = null;
                try { provider = Activator.CreateInstance(providerType, true); }
                catch (Exception ex) { MCMSettings.DebugLog("EE_ItemGrid provider ctor: " + (ex.InnerException?.Message ?? ex.Message)); }
                if (provider == null) return true;

                var spMethod = providerType.GetMethod("SetProperty", AllFlags);
                if (spMethod != null)
                {
                    try
                    {
                        spMethod.Invoke(provider, new object[] { "ImageId", imageId });
                        spMethod.Invoke(provider, new object[] { "AdditionalArgs", additionalArgs });
                        spMethod.Invoke(provider, new object[] { "IsReleased", false });
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("EE_ItemGrid provider SetProperty: " + ex.Message); }
                }

                Type st = imgWidgetType;
                bool assigned = false;
                while (st != null && st != typeof(object) && !assigned)
                {
                    foreach (var f in st.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (f.Name.Contains("TextureProvider") && f.Name.Contains("BackingField")
                            && f.FieldType.IsAssignableFrom(provider.GetType()))
                        {
                            f.SetValue(imgWidget, provider);
                            assigned = true;
                            break;
                        }
                    }
                    st = st.BaseType;
                }
                MCMSettings.DebugLog("EE_ItemGrid: provider "
                    + (assigned ? "assigned OK" : "NOT assigned (backing field not found)"));
                return true;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("InventoryLoreSectionInjector.TryCreateItemIcon: " + ex.ToString());
                return false;
            }
        }

        private static void SetWidgetProperty(Widget widget, Type widgetType, string propName, object value)
        {
            try
            {
                Type search = widgetType;
                while (search != null && search != typeof(object))
                {
                    var prop = search.GetProperty(propName,
                        BindingFlags.Instance | BindingFlags.Public
                        | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (prop != null && prop.CanWrite)
                    {
                        prop.SetValue(widget, value);
                        return;
                    }
                    search = search.BaseType;
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("EE_ItemGrid SetWidgetProperty " + propName + ": " + ex.Message); }
        }

        /// <summary>
        /// Loads a native brush by name from the game's BrushFactory (e.g. "Encyclopedia.Frame").
        /// Unlike FindBrushByName this does NOT require the brush to already be in use on the page.
        /// </summary>
        private static Brush LoadNativeBrush(string brushName)
        {
            try
            {
                Type rmType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    rmType = asm.GetType("TaleWorlds.Engine.GauntletUI.UIResourceManager");
                    if (rmType != null) break;
                }
                if (rmType == null) return null;
                var bfProp = rmType.GetProperty("BrushFactory",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var bf = bfProp != null ? bfProp.GetValue(null) : null;
                if (bf == null) return null;
                var getBrush = bf.GetType().GetMethod("GetBrush", new[] { typeof(string) });
                if (getBrush == null) return null;
                var brush = getBrush.Invoke(bf, new object[] { brushName }) as Brush;
                MCMSettings.DebugLog("EE_ItemGrid LoadNativeBrush(" + brushName + ") = "
                    + (brush != null ? "ok" : "null"));
                return brush;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("EE_ItemGrid LoadNativeBrush " + brushName + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Draws a frame entirely in C#: a filled background plus four thin border edges.
        /// Fully deterministic — no sprite/brush lookup that can silently resolve to nothing —
        /// and it fits whatever box it's added to, at any size.
        /// Edges are added as children of <paramref name="parent"/>, so add this BEFORE the
        /// content that should sit inside it (fill first), and call AddBorder after for the
        /// border to draw on top.
        /// </summary>
        private static void AddPanelFill(UIContext uiContext, Widget parent, string id, Color fill)
        {
            var bg = MakeSolid(uiContext, id, fill);
            bg.WidthSizePolicy = SizePolicy.StretchToParent;
            bg.HeightSizePolicy = SizePolicy.StretchToParent;
            parent.AddChild(bg);
        }

        /// <summary>
        /// Adds four thin edge widgets forming a border rectangle on top of whatever is already
        /// in <paramref name="parent"/>. Pure C# geometry; each edge is a BlankWhiteSquare_9
        /// sprite tinted by Color, so it always renders and always fits.
        /// </summary>
        private static void AddBorder(UIContext uiContext, Widget parent, string id,
            Color edge, float thickness)
        {
            var top = MakeSolid(uiContext, id + "_T", edge);
            top.WidthSizePolicy = SizePolicy.StretchToParent;
            top.HeightSizePolicy = SizePolicy.Fixed;
            top.SuggestedHeight = thickness;
            top.VerticalAlignment = VerticalAlignment.Top;
            parent.AddChild(top);

            var bottom = MakeSolid(uiContext, id + "_B", edge);
            bottom.WidthSizePolicy = SizePolicy.StretchToParent;
            bottom.HeightSizePolicy = SizePolicy.Fixed;
            bottom.SuggestedHeight = thickness;
            bottom.VerticalAlignment = VerticalAlignment.Bottom;
            parent.AddChild(bottom);

            var left = MakeSolid(uiContext, id + "_L", edge);
            left.WidthSizePolicy = SizePolicy.Fixed;
            left.SuggestedWidth = thickness;
            left.HeightSizePolicy = SizePolicy.StretchToParent;
            left.HorizontalAlignment = HorizontalAlignment.Left;
            parent.AddChild(left);

            var right = MakeSolid(uiContext, id + "_R", edge);
            right.WidthSizePolicy = SizePolicy.Fixed;
            right.SuggestedWidth = thickness;
            right.HeightSizePolicy = SizePolicy.StretchToParent;
            right.HorizontalAlignment = HorizontalAlignment.Right;
            parent.AddChild(right);
        }

        /// <summary>
        /// A solid-colour rectangle drawn in C#: a plain Widget backed by the generic
        /// BlankWhiteSquare_9 sprite (the exact primitive the game uses for solid fills — e.g.
        /// InventoryItemPreview's dim backdrop is Sprite="BlankWhiteSquare_9" Color="#000000AA")
        /// tinted by Color. A Widget with NO sprite renders nothing, so the sprite is required.
        /// </summary>
        private static Widget MakeSolid(UIContext uiContext, string id, Color color)
        {
            var w = new Widget(uiContext);
            w.Id = id;
            w.DoNotAcceptEvents = true;
            if (!TrySetSpriteByName(uiContext, w, "BlankWhiteSquare_9"))
                TrySetSpriteByName(uiContext, w, "BlankWhiteSquare");
            SetWidgetProperty(w, w.GetType(), "Color", color);
            return w;
        }

        /// <summary>
        /// Loads a sprite by name from UIContext.SpriteData and sets it on a Widget.
        /// Mirrors JournalSectionInjector.TrySetSpriteByName. This is how the native UI backs an
        /// item slot: InventoryEquippedItemSlot.xml uses Sprite="Inventory\portrait_cart" on a
        /// plain Widget behind the ImageIdentifierWidget — a sprite, not a brush.
        /// </summary>
        private static bool TrySetSpriteByName(UIContext uiContext, Widget target, string spriteName)
        {
            try
            {
                var sdProp = uiContext.GetType().GetProperty("SpriteData", AllFlags);
                if (sdProp == null) return false;
                var spriteData = sdProp.GetValue(uiContext);
                if (spriteData == null) return false;

                object sprite = null;
                var getSprite = spriteData.GetType().GetMethod("GetSprite",
                    BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                if (getSprite != null)
                    sprite = getSprite.Invoke(spriteData, new object[] { spriteName });

                if (sprite == null)
                {
                    var indexer = spriteData.GetType().GetProperty("Item",
                        BindingFlags.Instance | BindingFlags.Public, null, null, new[] { typeof(string) }, null);
                    if (indexer != null)
                        sprite = indexer.GetValue(spriteData, new object[] { spriteName });
                }
                if (sprite == null) return false;

                var spriteProp = typeof(Widget).GetProperty("Sprite", AllFlags);
                if (spriteProp != null && spriteProp.CanWrite)
                {
                    spriteProp.SetValue(target, sprite);
                    return true;
                }
                var spriteField = typeof(Widget).GetField("_sprite", AllFlags);
                if (spriteField != null)
                {
                    spriteField.SetValue(target, sprite);
                    return true;
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("EE_ItemGrid TrySetSpriteByName('" + spriteName + "'): " + ex.Message);
            }
            return false;
        }

        /// <summary>
        /// Wraps tall content in a fixed-height ScrollablePanel so the section stays compact and
        /// scrolls, instead of paginating. Mirrors the mod's TryWrapInScrollablePanel structure:
        /// ScrollablePanel (Fixed height) > ClipRect (ClipContents) > content (CoverChildren).
        /// Returns null if the ScrollablePanel type can't be created (caller falls back to the
        /// bare content, which the encyclopedia page itself can still scroll).
        /// </summary>
        private static Widget WrapInScroll(UIContext uiContext, Widget content, float targetHeight)
        {
            try
            {
                Type scrollType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    scrollType = asm.GetType("TaleWorlds.GauntletUI.BaseTypes.ScrollablePanel")
                              ?? asm.GetType("TaleWorlds.GauntletUI.ExtraWidgets.ScrollablePanel");
                    if (scrollType != null) break;
                }
                if (scrollType == null)
                {
                    MCMSettings.DebugLog("EE_ItemGrid: ScrollablePanel type not found — showing grid unclipped");
                    return null;
                }

                var scrollPanel = (Widget)Activator.CreateInstance(scrollType, new object[] { uiContext });
                scrollPanel.Id = "EE_ItemGridScroll";
                scrollPanel.WidthSizePolicy = SizePolicy.StretchToParent;
                scrollPanel.HeightSizePolicy = SizePolicy.Fixed;
                // SuggestedHeight ONLY — do NOT set ScaledSuggestedHeight. The rows are also sized
                // via SuggestedHeight, so the engine scales viewport and rows by the same factor
                // and exactly PageRows rows fit. Setting ScaledSuggestedHeight pinned the viewport
                // to unscaled px while the rows scaled up, which showed 5.5-6.5 rows.
                scrollPanel.SuggestedHeight = targetHeight;

                var clipRect = new Widget(uiContext);
                clipRect.Id = "EE_ItemGridClip";
                clipRect.WidthSizePolicy = SizePolicy.StretchToParent;
                clipRect.HeightSizePolicy = SizePolicy.StretchToParent;
                clipRect.ClipContents = true;

                content.WidthSizePolicy = SizePolicy.StretchToParent;
                content.HeightSizePolicy = SizePolicy.CoverChildren;

                clipRect.AddChild(content);
                scrollPanel.AddChild(clipRect);

                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var clipProp = scrollType.GetProperty("ClipRect", flags);
                if (clipProp != null && clipProp.CanWrite) clipProp.SetValue(scrollPanel, clipRect);
                var innerProp = scrollType.GetProperty("InnerPanel", flags);
                if (innerProp != null && innerProp.CanWrite) innerProp.SetValue(scrollPanel, content);
                var autoHideProp = scrollType.GetProperty("AutoHideScrollBars", flags);
                if (autoHideProp != null && autoHideProp.CanWrite) autoHideProp.SetValue(scrollPanel, true);

                MCMSettings.DebugLog("EE_ItemGrid: wrapped grid in ScrollablePanel (h=" + targetHeight + ")");
                return scrollPanel;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("EE_ItemGrid WrapInScroll: " + ex.ToString());
                return null;
            }
        }

        /// <summary>
        /// Attaches a native hover hint to a widget. Verified against the shipped binaries: the
        /// engine's real hover-hint path is MBInformationManager.ShowHint(string) /
        /// HideInformations() — exactly what HintViewModel.ExecuteBeginHint/ExecuteEndHint call
        /// internally — and the widget fires "HoverBegin"/"HoverEnd" via its EventFire event
        /// (native prefabs bind Command.HoverBegin="ExecuteBeginHint").
        ///
        /// This is NOT InformationManager.ShowTooltip, whose Type arg is a domain dispatch key
        /// (typeof(ItemObject)/typeof(Hero) with a registered refresher), not a container/VM —
        /// passing List&lt;object&gt; there rendered nothing.
        ///
        /// The handler is ALWAYS attached and the hint API is resolved lazily on first hover, so
        /// it can never again bail before wiring hover. (The old bug: a wrong HintViewModel type
        /// name + non-existent ctor made the method return BEFORE AddEventHandler, so no card
        /// event ever reached us — the log showed zero "card event seen" lines.)
        /// </summary>
        private static void TrySetTooltip(Widget widget, string tooltipText, string itemId)
        {
            if (widget == null || string.IsNullOrEmpty(tooltipText)) return;
            try
            {
                var eventFireEvent = widget.GetType().GetEvent("EventFire", AllFlags);
                if (eventFireEvent == null)
                {
                    MCMSettings.DebugLog("EE_ItemGrid TrySetTooltip bail: '" + widget.Id
                        + "' (" + widget.GetType().Name + ") has no EventFire event");
                    return;
                }

                string hintText = tooltipText;
                string capturedItemId = itemId;
                bool[] usingNative = { false }; // remember which path opened, so we hide the right one

                Action<Widget, string, object[]> handler = (sender, eventName, args) =>
                {
                    // Diagnostic: log each DISTINCT event once so the log PROVES whether hover
                    // reaches the card (settles the "do child widgets swallow hover?" question).
                    if (_loggedCardEvents.Add(eventName))
                        MCMSettings.DebugLog("EE_ItemGrid card event seen: '" + eventName + "'");

                    try
                    {
                        if (eventName == "HoverBegin" || eventName == "OnHoverBegin"
                            || eventName == "MouseOver" || eventName == "Hover")
                        {
                            // Premium: the game's RICH native item tooltip (colored stats, bars,
                            // type icons). Fall back to the lore-bearing hint if it can't fire, so
                            // the lore is never lost. Never show both at once (usingNative gate).
                            if (ShowNativeItemTooltip(capturedItemId)) usingNative[0] = true;
                            else { usingNative[0] = false; ShowNativeHint(hintText); }
                        }
                        else if (eventName == "HoverEnd" || eventName == "OnHoverEnd"
                                 || eventName == "MouseOut")
                        {
                            if (usingNative[0]) HideNativeItemTooltip();
                            else HideNativeHint();
                            usingNative[0] = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        MCMSettings.DebugLog("EE_ItemGrid hover handler: " + ex.Message);
                    }
                };

                // ALWAYS attach — never bail before wiring hover.
                eventFireEvent.AddEventHandler(widget, handler);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("EE_ItemGrid TrySetTooltip: " + ex.ToString());
            }
        }

        // --- Native hover-hint API (TaleWorlds.Core.MBInformationManager.ShowHint / HideInformations),
        //     resolved once via reflection so we depend on no compile-time namespace and stay
        //     version-safe. This is the exact path HintViewModel.ExecuteBeginHint uses. ---
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
                if (mbType == null)
                {
                    MCMSettings.DebugLog("EE_ItemGrid hint: MBInformationManager type not found");
                    return;
                }
                _showHintMethod = mbType.GetMethod("ShowHint",
                    BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string) }, null);
                _hideInfoMethod = mbType.GetMethod("HideInformations",
                    BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
                MCMSettings.DebugLog("EE_ItemGrid hint API resolved: ShowHint="
                    + (_showHintMethod != null) + " HideInformations=" + (_hideInfoMethod != null));
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("EE_ItemGrid ResolveHintApi: " + ex.Message);
            }
        }

        private static void ShowNativeHint(string text)
        {
            ResolveHintApi();
            if (_showHintMethod != null) _showHintMethod.Invoke(null, new object[] { text });
            else MCMSettings.DebugLog("EE_ItemGrid ShowNativeHint: ShowHint method unavailable");
        }

        private static void HideNativeHint()
        {
            ResolveHintApi();
            if (_hideInfoMethod != null) _hideInfoMethod.Invoke(null, null);
        }

        /// <summary>
        /// Shows the game's RICH native item tooltip (colored stats, stat bars, type icons) for the
        /// given item id. The dispatch key MUST be typeof(ItemObject) and args[0] MUST be a boxed
        /// EquipmentElement — the native refresher (SandBox RefreshItemTooltip) does
        /// `args[0] as EquipmentElement?`, so a raw ItemObject renders nothing. The refresher is
        /// registered for the whole SandBox lifetime and the stock encyclopedia already renders
        /// sibling tooltips, so this works on the encyclopedia screen. Returns false if it could
        /// not fire, so the caller falls back to the lore-bearing hint (lore is never lost).
        /// </summary>
        private static bool ShowNativeItemTooltip(string itemId)
        {
            try
            {
                if (string.IsNullOrEmpty(itemId)) return false;
                if (MBObjectManager.Instance == null) return false; // main menu / not in a campaign
                ItemObject item = MBObjectManager.Instance.GetObject<ItemObject>(itemId);
                if (item == null) return false;
                // EquipmentElement is a struct; boxing it into object[] is exactly what the native
                // refresher expects (args[0] as EquipmentElement?). A raw ItemObject would be null.
                InformationManager.ShowTooltip(typeof(ItemObject),
                    new object[] { new EquipmentElement(item) });
                return true;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("EE_ItemGrid ShowNativeItemTooltip: " + ex.Message);
                return false;
            }
        }

        private static void HideNativeItemTooltip()
        {
            try { InformationManager.HideTooltip(); }
            catch (Exception ex) { MCMSettings.DebugLog("EE_ItemGrid HideNativeItemTooltip: " + ex.Message); }
        }

        /// <summary>
        /// The panel shown below the grid for the selected item: name, stats,
        /// lore prose, and the Edit / Rename / Reset actions.
        /// </summary>
        private static Widget BuildDetailPanel(UIContext uiContext, ItemLoreEntry entry,
            string entityId, LoreSectionInjector.NativeSectionParts native, Brush contentBrush, Brush goldBrush)
        {
            try
            {
                Widget panel = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                               ?? new Widget(uiContext);
                panel.Id = "EE_ItemGridDetail";
                panel.WidthSizePolicy = SizePolicy.StretchToParent;
                panel.HeightSizePolicy = SizePolicy.CoverChildren;
                panel.MarginTop = 8f;
                panel.MarginBottom = 6f;
                LoreSectionInjector.SetVerticalLayoutTopToBottom(panel);

                // Premium framing: a native encyclopedia sub-page frame behind the panel. If the
                // sprite sheet isn't resident the call returns false and we simply stay transparent.
                if (!TrySetSpriteByName(uiContext, panel, "Encyclopedia\\subpage_slick_frame"))
                    TrySetSpriteByName(uiContext, panel, "General\\TooltipHint\\tooltip_frame");

                // Native colour brushes: gold serif title, parchment lore. LoadNativeBrush returns
                // null when unavailable, so we fall back to the section's / passed-in brushes.
                Brush titleBrush = LoadNativeBrush("Encyclopedia.SubPage.Title.Text")
                                   ?? (native != null ? native.HeaderTextBrush : null);
                Brush statBrush = LoadNativeBrush("Encyclopedia.SubPage.Info.Text") ?? contentBrush;
                Brush loreBrush = LoadNativeBrush("Encyclopedia.SubPage.ItemDecription.Text") ?? contentBrush;

                // Header row: item-type icon (added only if the sprite resolves) + gold title.
                Widget header = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                                ?? new Widget(uiContext);
                header.Id = "EE_ItemGridDetailHeader";
                header.WidthSizePolicy = SizePolicy.StretchToParent;
                header.HeightSizePolicy = SizePolicy.CoverChildren;
                header.MarginTop = 12f;
                header.MarginLeft = 16f;
                header.MarginRight = 16f;
                header.MarginBottom = 4f;
                LoreSectionInjector.CopyOrSetHorizontalLayout(null, header);

                Widget typeIcon = new Widget(uiContext);
                typeIcon.Id = "EE_ItemGridDetailTypeIcon";
                typeIcon.WidthSizePolicy = SizePolicy.Fixed;
                typeIcon.HeightSizePolicy = SizePolicy.Fixed;
                typeIcon.SuggestedWidth = 34f;
                typeIcon.SuggestedHeight = 34f;
                typeIcon.VerticalAlignment = VerticalAlignment.Center;
                typeIcon.MarginRight = 8f;
                if (TrySetSpriteByName(uiContext, typeIcon, GetEquipmentTypeSprite(entry.ItemId, entry.Category)))
                    header.AddChild(typeIcon);

                var nameText = new TextWidget(uiContext);
                nameText.Id = "EE_ItemGridDetailName";
                nameText.Text = entry.DisplayName;
                nameText.WidthSizePolicy = SizePolicy.StretchToParent;
                nameText.HeightSizePolicy = SizePolicy.CoverChildren;
                nameText.VerticalAlignment = VerticalAlignment.Center;
                if (titleBrush != null) nameText.Brush = titleBrush;
                header.AddChild(nameText);
                panel.AddChild(header);

                AddDetailText(uiContext, panel, "EE_ItemGridDetailStats",
                    !string.IsNullOrEmpty(entry.StatsBlock)
                        ? entry.StatsBlock
                        : entry.StatLine + " - " + entry.Value.ToString("N0") + " denars",
                    statBrush);

                string lore = ItemLoreLoader.GetItemLore(entry.ItemId, entry.OwnerId,
                    entry.DisplayName, entry.Category);
                AddDetailText(uiContext, panel, "EE_ItemGridDetailProse", lore, loreBrush);

                Widget buttonRow = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                                   ?? new Widget(uiContext);
                buttonRow.Id = "EE_ItemGridDetailButtons";
                buttonRow.WidthSizePolicy = SizePolicy.CoverChildren;
                buttonRow.HeightSizePolicy = SizePolicy.CoverChildren;
                buttonRow.MarginTop = 6f;
                buttonRow.MarginLeft = 16f;
                buttonRow.MarginBottom = 10f;
                LoreSectionInjector.CopyOrSetHorizontalLayout(null, buttonRow);

                string capturedEntity = entityId;
                ItemLoreEntry capturedEntry = entry;

                AddDetailButton(uiContext, buttonRow, "EE_ItemGridEditBtn", "Edit Lore", goldBrush, () =>
                {
                    string current = ItemLoreLoader.GetItemLore(capturedEntry.ItemId,
                        capturedEntry.OwnerId, capturedEntry.DisplayName, capturedEntry.Category);
                    LoreEditorPopup.ScheduleShow("Lore: " + capturedEntry.DisplayName, current, 2000,
                        text =>
                        {
                            var b = EncyclopediaEditBehavior.Instance;
                            if (b != null) b.SetItemLoreOverride(capturedEntry.ItemId,
                                capturedEntry.OwnerId, text);
                            InjectInventorySection(capturedEntity);
                        },
                        () => { });
                });

                AddDetailButton(uiContext, buttonRow, "EE_ItemGridRenameBtn", "Rename", goldBrush, () =>
                {
                    LoreEditorPopup.ScheduleShow("Rename: " + capturedEntry.DisplayName,
                        capturedEntry.DisplayName, 120,
                        text =>
                        {
                            var b = EncyclopediaEditBehavior.Instance;
                            if (b != null && !string.IsNullOrWhiteSpace(text))
                                b.SetCustomName(capturedEntry.ItemId, text);
                            InjectInventorySection(capturedEntity);
                        },
                        () => { });
                });

                AddDetailButton(uiContext, buttonRow, "EE_ItemGridResetBtn", "Reset", goldBrush, () =>
                {
                    var b = EncyclopediaEditBehavior.Instance;
                    if (b != null) b.SetItemLoreOverride(capturedEntry.ItemId,
                        capturedEntry.OwnerId, null);
                    InjectInventorySection(capturedEntity);
                });

                panel.AddChild(buttonRow);
                return panel;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("InventoryLoreSectionInjector.BuildDetailPanel: " + ex.ToString());
                return null;
            }
        }

        /// <summary>
        /// Native item-type icon sprite (General\EquipmentIcons\equipment_type_*) for the detail
        /// header. Resolves the item to pick a precise icon from its ItemType; falls back to a
        /// coarse icon by lore category if the item can't be resolved.
        /// </summary>
        private static string GetEquipmentTypeSprite(string itemId, ItemLoreCategory category)
        {
            const string P = "General\\EquipmentIcons\\";
            try
            {
                ItemObject item = (MBObjectManager.Instance != null)
                    ? MBObjectManager.Instance.GetObject<ItemObject>(itemId) : null;
                if (item != null)
                {
                    switch (item.ItemType)
                    {
                        case ItemObject.ItemTypeEnum.OneHandedWeapon: return P + "equipment_type_one_handed";
                        case ItemObject.ItemTypeEnum.TwoHandedWeapon: return P + "equipment_type_two_handed";
                        case ItemObject.ItemTypeEnum.Polearm:         return P + "equipment_type_polearm";
                        case ItemObject.ItemTypeEnum.Bow:             return P + "equipment_type_bow";
                        case ItemObject.ItemTypeEnum.Crossbow:        return P + "equipment_type_crossbow";
                        case ItemObject.ItemTypeEnum.Thrown:          return P + "equipment_type_throwing";
                        case ItemObject.ItemTypeEnum.Arrows:
                        case ItemObject.ItemTypeEnum.Bolts:           return P + "equipment_type_quiver";
                        case ItemObject.ItemTypeEnum.Shield:          return P + "equipment_type_shield";
                        case ItemObject.ItemTypeEnum.HeadArmor:       return P + "equipment_type_head_armor";
                        case ItemObject.ItemTypeEnum.BodyArmor:       return P + "equipment_type_body_armor";
                        case ItemObject.ItemTypeEnum.HandArmor:       return P + "equipment_type_hand_armor";
                        case ItemObject.ItemTypeEnum.LegArmor:        return P + "equipment_type_leg_armor";
                        case ItemObject.ItemTypeEnum.Cape:            return P + "equipment_type_cape";
                        case ItemObject.ItemTypeEnum.Horse:
                        case ItemObject.ItemTypeEnum.HorseHarness:    return P + "equipment_type_mount";
                    }
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("EE_ItemGrid GetEquipmentTypeSprite: " + ex.Message);
            }
            switch (category)
            {
                case ItemLoreCategory.Weapon: return P + "equipment_type_one_handed";
                case ItemLoreCategory.Armor:  return P + "equipment_type_body_armor";
                case ItemLoreCategory.Mount:  return P + "equipment_type_mount";
                default:                      return P + "equipment_type_default";
            }
        }

        private static void AddDetailText(UIContext uiContext, Widget parent, string id,
            string text, Brush brush)
        {
            var tw = new TextWidget(uiContext);
            tw.Id = id;
            tw.Text = text ?? string.Empty;
            tw.WidthSizePolicy = SizePolicy.StretchToParent;
            tw.HeightSizePolicy = SizePolicy.CoverChildren;
            tw.MarginBottom = 3f;
            tw.MarginLeft = 16f;   // inset from the native frame edge, matching the header row
            tw.MarginRight = 16f;
            if (brush != null) tw.Brush = brush;
            parent.AddChild(tw);
        }

        private static void AddDetailButton(UIContext uiContext, Widget parent, string id,
            string label, Brush brush, Action onClick)
        {
            Widget btn = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ButtonWidget")
                         ?? new Widget(uiContext);
            btn.Id = id;
            btn.WidthSizePolicy = SizePolicy.CoverChildren;
            btn.HeightSizePolicy = SizePolicy.CoverChildren;
            btn.DoNotPassEventsToChildren = true;
            btn.MarginRight = 16f;
            btn.MarginTop = 2f;

            var tw = new TextWidget(uiContext);
            tw.Id = id + "Text";
            tw.Text = "[ " + label + " ]";
            // CoverChildren so the text sizes to its content on one line. Without this the text
            // gets ~0 width and wraps to one character per line (the garbled vertical buttons).
            tw.WidthSizePolicy = SizePolicy.CoverChildren;
            tw.HeightSizePolicy = SizePolicy.CoverChildren;
            if (brush != null) tw.Brush = brush;
            btn.AddChild(tw);

            LoreSectionHelpers.HookWidgetClick(btn, onClick);
            parent.AddChild(btn);
        }
    }
}
