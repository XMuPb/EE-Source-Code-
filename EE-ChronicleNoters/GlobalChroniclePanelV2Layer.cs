using System;
using System.Collections.Generic;
using EditableEncyclopedia.ChronicleNoters;
using TaleWorlds.Engine.GauntletUI;
using System.Reflection;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Gauntlet-layer wire-up for the Chronicle panel (the only panel — legacy V1 removed
    /// 2026-05-29). Opened/closed from GlobalChroniclePanel.TogglePanelUnlocked when the
    /// MapBar button is clicked (or Escape is pressed while open).
    ///
    /// Lifecycle:
    ///   Open(topScreen)  -> builds VM, populates entries, loads GlobalChroniclePanelV2 prefab,
    ///                       attaches a new GauntletLayer to the top screen
    ///   Close()          -> removes the layer + nulls cached refs
    /// </summary>
    internal static class GlobalChroniclePanelV2Layer
    {
        private static readonly IEELogger Log = EECoreLogger.For("EE-ChronicleNoters.V2Layer");

        private static GauntletLayer _layer;
        private static object _movie; // GauntletMovieIdentifier (project-existing pattern stores as object)
        private static GlobalChroniclePanelVM _vm;
        private static ScreenBase _hostScreen;
        private static readonly BindingFlags AllFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static bool IsOpen { get { return _layer != null; } }

        /// <summary>Opens the redesigned panel as a new Gauntlet layer on top of the given screen.</summary>
        public static void Open(ScreenBase topScreen)
        {
            if (topScreen == null) return;
            if (IsOpen) { Close(); }

            try
            {
                Log.Info("Open: building VM and populating entries");
                _vm = new GlobalChroniclePanelVM(Close);
                _vm.OnCloseRequested = Close;
                _vm.OnResetRequested = OnResetRequested;

                // 2026-05-29 fix: wire the per-row click handler so the reading pane updates
                // when the user clicks an entry. ChronicleEntryVM.ExecuteSelectEntry invokes
                // this static Action; without wiring, clicks fire but nothing changes.
                ChronicleEntryVM.EntryClicked = entry =>
                {
                    if (_vm != null) _vm.SelectedEntry = entry;
                };

                // 2026-05-29 fix: VIEW IN ENCYCLOPEDIA should also close this panel so the
                // encyclopedia page underneath is visible. ChronicleEntryVM raises CloseRequested
                // after a successful GoToLink.
                ChronicleEntryVM.CloseRequested = Close;

                // Populate entry VMs from the canonical journal API. Bookmark + note
                // lookups left null in this dormant scaffold; Phase 7 will wire them
                // to ChronicleBookmarksBehavior.
                // Cross-reference rows (MORE FROM / ALSO IN) select by EntryId. Route that through
                // the SAME handler a row click uses (ChronicleEntryVM.EntryClicked, wired above) so
                // there is exactly one selection mechanism. The closure captures `entries`, which is
                // assigned on the very next statement — the callback can only fire after the panel
                // is live, long after BuildAllEntryVMs has returned.
                List<ChronicleEntryVM> entries = null;
                Action<string> selectById = delegate(string targetId)
                {
                    if (string.IsNullOrEmpty(targetId) || entries == null) return;
                    var handler = ChronicleEntryVM.EntryClicked;
                    if (handler == null) return;
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var candidate = entries[i];
                        if (candidate != null && candidate.EntryId == targetId) { handler(candidate); return; }
                    }
                };

                entries = ChronicleEntryPopulator.BuildAllEntryVMs(
                    isBookmarked: null,
                    getNoteText: null,
                    selectById: selectById);
                Log.Info("Open: BuildAllEntryVMs returned " + (entries == null ? "NULL" : entries.Count.ToString()) + " entries");

                // Dump first 2 entries' bindable properties so we can verify the VM data the prefab will bind against.
                int sampleN = Math.Min(2, entries.Count);
                for (int i = 0; i < sampleN; i++)
                {
                    var s = entries[i];
                    Log.Info("Open: sample entry[" + i + "]"
                        + " Year=" + s.EntryYear
                        + " Date='" + (s.DateLabel ?? "") + "'"
                        + " Title='" + (s.TitleText ?? "") + "'"
                        + " EvTag=" + s.EventTags
                        + " EnTag=" + s.EntityTags
                        + " BodyLen=" + (s.BodyText == null ? 0 : s.BodyText.Length));
                }

                _vm.ClearSourceEntries();
                _vm.TotalCount = entries.Count;
                foreach (var e in entries) _vm.AddSourceEntry(e);
                _vm.InitializeYearStripFromEntries();
                Log.Info("Open: VM populated. TotalCount=" + _vm.TotalCount + " VisibleCount=" + _vm.VisibleCount
                    + " YearStripCount=" + (_vm.YearStrip == null || _vm.YearStrip.Years == null ? 0 : _vm.YearStrip.Years.Count));

                // Build the Gauntlet layer + movie (idiom mirrors existing CreateOverlay)
                _layer = new GauntletLayer("GlobalChroniclePanelV2", 9500, false);
                _movie = _layer.LoadMovie("GlobalChroniclePanelV2", _vm);
                Log.Info("Open: LoadMovie returned " + (_movie == null ? "NULL (prefab parse failed!)" : "OK"));
                _layer.InputRestrictions.SetInputRestrictions(true);
                topScreen.AddLayer(_layer);
                _hostScreen = topScreen;
                    // 2026-05-30: 1.4.x LayoutMethod inversion compat. The prefab hardcodes
                    // VerticalBottomToTop (the pre-1.4 top-down value); on 1.4 that renders bottom-up,
                    // so flip the static vertical stacks to the version-correct top-down enum. No-op on 1.3.
                    ApplyLayoutCompat(_layer);
                Log.Info("Open: layer added to host screen. IsOpen=" + IsOpen);

                // Post-bind diagnostic: confirm VM state seen by the bound prefab.
                // Helps disambiguate "list empty due to data" vs "list empty due to rendering."
                Log.Info("Open: post-bind state. vm.VisibleEntries=" + (_vm.VisibleEntries == null ? "NULL" : _vm.VisibleEntries.Count + " items")
                    + " vm.SelectedEntry=" + (_vm.SelectedEntry == null ? "NULL" : "set")
                    + " vm.YearStrip.Years=" + (_vm.YearStrip == null || _vm.YearStrip.Years == null ? "NULL" : _vm.YearStrip.Years.Count + " items"));
            }
            catch (Exception ex)
            {
                // Defensive: any prefab parse/load failure during the first open shouldn't
                // crash the game. Reset state so a subsequent attempt can retry cleanly.
                Log.Error("Open: exception during V2 panel open", ex);
                Close();
            }
        }

        /// <summary>
        /// 1.4.x compat: the prefab's vertical stacks hardcode "VerticalBottomToTop" (top-down on
        /// ≤1.3 due to the engine's enum inversion). On 1.4+ that renders bottom-up, so re-point every
        /// such stack to the version-correct top-down enum. No-op on ≤1.3 (already correct).
        /// The entry-row body is alignment-based (not a stack) so it is unaffected and not walked here.
        /// </summary>
        private static void ApplyLayoutCompat(GauntletLayer layer)
        {
            try
            {
                if (BannerlordVersion.IsLayoutEnumInverted()) return; // ≤1.3: prefab already correct
                string topDown = BannerlordVersion.TopDownLayoutEnumName(); // "VerticalTopToBottom" on 1.4
                if (topDown == "VerticalBottomToTop") return;               // defensive: nothing to change
                Widget root = GetLayerRoot(layer);
                if (root == null) { Log.Error("ApplyLayoutCompat: could not resolve layer root widget"); return; }
                int flipped = FlipVerticalStacks(root, topDown, 0);
                Log.Info("ApplyLayoutCompat: re-pointed " + flipped + " vertical stack(s) to '" + topDown + "' (1.4 layout compat)");
            }
            catch (Exception ex) { Log.Error("ApplyLayoutCompat failed", ex); }
        }

        private static Widget GetLayerRoot(GauntletLayer layer)
        {
            try
            {
                var t = typeof(GauntletLayer);
                foreach (var fn in new[] { "_gauntletUIContext", "_uiContext", "_context" })
                {
                    var f = t.GetField(fn, AllFlags);
                    if (f != null)
                    {
                        var ctx = f.GetValue(layer) as UIContext;
                        if (ctx != null && ctx.EventManager != null && ctx.EventManager.Root != null)
                            return ctx.EventManager.Root;
                    }
                }
                foreach (var pn in new[] { "UIContext", "_gauntletUIContext" })
                {
                    var p = t.GetProperty(pn, AllFlags);
                    if (p != null && p.CanRead)
                    {
                        var ctx = p.GetValue(layer) as UIContext;
                        if (ctx != null && ctx.EventManager != null && ctx.EventManager.Root != null)
                            return ctx.EventManager.Root;
                    }
                }
            }
            catch { }
            return null;
        }

        private static int FlipVerticalStacks(Widget w, string topDownName, int depth)
        {
            if (w == null || depth > 40) return 0;
            int count = 0;
            try
            {
                var layoutProp = w.GetType().GetProperty("StackLayout", AllFlags);
                if (layoutProp != null)
                {
                    var layout = layoutProp.GetValue(w);
                    if (layout != null)
                    {
                        var methodProp = layout.GetType().GetProperty("LayoutMethod", AllFlags);
                        if (methodProp != null && methodProp.CanWrite)
                        {
                            var cur = methodProp.GetValue(layout);
                            string curName = cur != null ? cur.ToString() : "";
                            if (curName == "VerticalBottomToTop")
                            {
                                foreach (var ev in Enum.GetValues(methodProp.PropertyType))
                                {
                                    if (ev.ToString() == topDownName) { methodProp.SetValue(layout, ev); count++; break; }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            for (int i = 0; i < w.ChildCount; i++)
                count += FlipVerticalStacks(w.GetChild(i), topDownName, depth + 1);
            return count;
        }

        public static void Close()
        {
            try
            {
                if (_layer != null && _hostScreen != null)
                {
                    _hostScreen.RemoveLayer(_layer);
                }
            }
            catch { }
            _layer = null;
            _movie = null;
            _vm = null;
            _hostScreen = null;
        }

        /// <summary>
        /// Reset callback wired to the VM. Shows a confirmation inquiry; on "Clear" it
        /// wipes ALL chronicle entries via the public EE API, then closes the panel
        /// (reopening shows the emptied panel). On "Cancel" it does nothing.
        /// </summary>
        private static void OnResetRequested()
        {
            try
            {
                InformationManager.ShowInquiry(
                    new InquiryData(
                        "Reset Chronicle",
                        "Permanently clear all chronicle entries? This cannot be undone.",
                        true, true,
                        "Clear",
                        "Cancel",
                        delegate()
                        {
                            EditableEncyclopediaAPI.ClearAllChronicleEntries();
                            Close();
                        },
                        delegate() { }),
                    false, false);
            }
            catch (Exception ex)
            {
                Log.Error("OnResetRequested: failed to show reset confirmation", ex);
            }
        }
    }
}
