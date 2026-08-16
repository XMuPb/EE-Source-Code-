using System;
using System.Collections.Generic;
using System.Reflection;
using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.ViewModels;
using SandBox.ViewModelCollection.Nameplate;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;

namespace EditableEncyclopedia
{
    /// <summary>
    /// UIExtenderEx ViewModelMixin for SettlementNameplateVM.
    /// Tracks all settlement nameplate VM instances and provides a static method
    /// to refresh their banner icons when a clan's banner is changed via the mod.
    ///
    /// The campaign map renders settlement banner icons through the nameplate UI system
    /// (GauntletLayer with SettlementNameplateVM instances), which is independent from
    /// the party visual "dirty flag" system. SetVisualAsDirty only refreshes mobile
    /// party icons, not settlement nameplates. This mixin bridges that gap.
    ///
    /// IMPORTANT: The game reverts NPC clan banners after we set them (detected by
    /// the watchdog). So we must use the stored banner CODE string to create fresh
    /// Banner objects, never read from clan.Banner which may be stale.
    /// </summary>
    [ViewModelMixin]
    public class SettlementNameplateMixin : BaseViewModelMixin<SettlementNameplateVM>
    {
        private static readonly List<WeakReference<SettlementNameplateVM>> _instances
            = new List<WeakReference<SettlementNameplateVM>>();

        private static readonly BindingFlags _flags
            = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private const int BannerCodePreviewLength = 40;

        public SettlementNameplateMixin(SettlementNameplateVM vm) : base(vm)
        {
            lock (_instances)
            {
                _instances.Add(new WeakReference<SettlementNameplateVM>(vm));
            }
        }

        /// <summary>
        /// Refreshes banner icons on all tracked settlement nameplate VMs
        /// belonging to the specified clan.
        /// </summary>
        /// <param name="targetClan">The clan whose settlements need banner refresh.</param>
        /// <param name="bannerCode">The serialized banner code to apply. If null, falls back to clan.Banner.</param>
        /// <returns>Number of nameplates refreshed.</returns>
        public static int RefreshBannersForClan(Clan targetClan, string bannerCode = null)
        {
            if (targetClan == null) return 0;

            // Create a fresh Banner object from the stored code.
            // This avoids reading from clan.Banner which the game may have reverted.
            Banner freshBanner = null;
            if (!string.IsNullOrEmpty(bannerCode))
            {
                try
                {
                    freshBanner = new Banner(bannerCode);
                    MCMSettings.DebugLog("SettlementNameplateMixin: created fresh Banner from stored code ("
                        + bannerCode.Substring(0, Math.Min(bannerCode.Length, BannerCodePreviewLength)) + "...)");
                }
                catch (Exception ex)
                {
                    MCMSettings.DebugLog("SettlementNameplateMixin: failed to create Banner from code: " + ex.ToString());
                }
            }
            if (freshBanner == null)
            {
                freshBanner = targetClan.Banner;
                MCMSettings.DebugLog("SettlementNameplateMixin: falling back to clan.Banner (code="
                    + (freshBanner?.Serialize()?.Substring(0, Math.Min(freshBanner?.Serialize()?.Length ?? 0, BannerCodePreviewLength)) ?? "null") + "...)");
            }

            int refreshed = 0;

            lock (_instances)
            {
                // Clean up dead weak references
                _instances.RemoveAll(wr => { SettlementNameplateVM t; return !wr.TryGetTarget(out t); });

                MCMSettings.DebugLog("SettlementNameplateMixin: " + _instances.Count
                    + " tracked nameplates, refreshing for clan " + targetClan.Name);

                foreach (var wr in _instances)
                {
                    if (!wr.TryGetTarget(out var vm)) continue;

                    try
                    {
                        Settlement settlement = GetSettlementFromVM(vm);
                        if (settlement == null) continue;
                        if (settlement.OwnerClan != targetClan) continue;

                        // CRITICAL ORDER: RefreshValues() FIRST, then override Banner AFTER.
                        // RefreshValues() re-reads from game state and may set the banner to
                        // the OLD (game-reverted) value. Our override must come AFTER.
                        try
                        {
                            vm.RefreshValues();
                        }
                        catch (Exception ex)
                        {
                            MCMSettings.DebugLog("  RefreshValues error: " + ex.ToString());
                        }

                        // Now override the Banner property with our fresh Banner from stored code
                        bool updated = SetBannerOnNameplate(vm, freshBanner);

                        if (updated)
                        {
                            refreshed++;
                            MCMSettings.DebugLog("  Refreshed nameplate for " + settlement.Name);
                        }
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("[SettlementNameplateMixin]: " + ex.ToString()); }
                }
            }

            MCMSettings.DebugLog("SettlementNameplateMixin: refreshed " + refreshed + " nameplates");
            return refreshed;
        }

        /// <summary>
        /// Refreshes the nameplate VM for a specific settlement so the map name updates.
        /// Call this after setting Settlement._name to make the change visible on the campaign map.
        /// </summary>
        public static void RefreshNameForSettlement(Settlement target)
        {
            if (target == null) return;

                string customName = null;
            try
            {
                customName = EncyclopediaEditBehavior.Instance?.GetCustomName(target.StringId);
            }
            catch { }

            lock (_instances)
            {
                _instances.RemoveAll(wr => { SettlementNameplateVM t; return !wr.TryGetTarget(out t); });

                foreach (var wr in _instances)
                {
                    if (!wr.TryGetTarget(out var vm)) continue;
                    try
                    {
                        Settlement settlement = GetSettlementFromVM(vm);
                        if (settlement == null || settlement != target) continue;

                        vm.RefreshValues();

                        // After RefreshValues, directly override the Name property on the VM
                        // RefreshValues reads Settlement.Name which may not reflect our change yet
                        if (!string.IsNullOrEmpty(customName))
                        {
                            try
                            {
                                // Try setting the Name property directly on the VM
                                var nameProp = vm.GetType().GetProperty("Name", _flags);
                                if (nameProp != null && nameProp.CanWrite)
                                {
                                    nameProp.SetValue(vm, customName);
                                    MCMSettings.DebugLog("Nameplate: set VM.Name = " + customName);
                                }
                                else
                                {
                                    // Try common field names for the displayed name
                                    foreach (var fieldName in new[] { "_nameText", "_name", "<Name>k__BackingField" })
                                    {
                                        var field = vm.GetType().GetField(fieldName, _flags);
                                        if (field != null && field.FieldType == typeof(string))
                                        {
                                            field.SetValue(vm, customName);
                                            MCMSettings.DebugLog("Nameplate: set VM." + fieldName + " = " + customName);
                                            break;
                                        }
                                    }
                                }
                                // Notify property changed to update the UI binding
                                try
                                {
                                    var onPropChanged = vm.GetType().GetMethod("OnPropertyChangedWithValue",
                                        new[] { typeof(string), typeof(string) });
                                    if (onPropChanged != null)
                                        onPropChanged.Invoke(vm, new object[] { customName, "Name" });
                                }
                                catch { }
                            }
                            catch (Exception ex) { MCMSettings.DebugLog("Nameplate: VM name override failed: " + ex.ToString()); }
                        }

                        MCMSettings.DebugLog("Refreshed map nameplate for settlement: " + target.Name);
                        return; // Found and refreshed — done
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("[SettlementNameplateMixin]: " + ex.ToString()); }
                }
            }

            MCMSettings.DebugLog("Settlement nameplate not found in tracked VMs for: " + target.StringId);
        }

        private static System.Threading.Timer _deferredRefreshTimer;
        private const int DeferredRefreshDelayMs = 500;
        private const int DeferredRefreshMaxRetries = 3;
        private static volatile int _deferredRefreshRetries;
        private static volatile bool _deferredRefreshPending;

        /// <summary>
        /// Schedules a deferred VM refresh — fires DeferredRefreshDelayMs later.
        /// Round 4 fix STC-1 + STC-2: previously, the Timer callback DIRECTLY called
        /// vm.RefreshValues() and walked the Gauntlet widget tree off the main thread,
        /// risking CTD. Additionally, the retry timer used a no-op `_ => { }` lambda,
        /// silently dropping retries 2 and 3. Now the Timer ONLY sets a volatile flag;
        /// the actual work happens in TickMainThread() on the main thread, and retries
        /// re-set the same flag.
        /// </summary>
        public static void ScheduleDeferredRefresh()
        {
            _deferredRefreshRetries = 0;
            try { _deferredRefreshTimer?.Dispose(); } catch { }
            _deferredRefreshTimer = new System.Threading.Timer(_ =>
            {
                _deferredRefreshPending = true;
            }, null, DeferredRefreshDelayMs, System.Threading.Timeout.Infinite);
        }

        /// <summary>
        /// MUST be called from OnApplicationTick on the main game thread.
        /// Processes the deferred-refresh flag set by the Timer callback.
        /// </summary>
        public static void TickMainThread()
        {
            if (!_deferredRefreshPending) return;
            _deferredRefreshPending = false;
            _deferredRefreshRetries++;

            try
            {
                DoDeferredRefresh();
            }
            catch (Exception ex) { MCMSettings.DebugLog("[SettlementNameplateMixin] TickMainThread error: " + ex.ToString()); }

            // Schedule a retry on the timer (still flag-only — main thread does the work).
            if (_deferredRefreshRetries < DeferredRefreshMaxRetries)
            {
                try { _deferredRefreshTimer?.Dispose(); } catch { }
                _deferredRefreshTimer = new System.Threading.Timer(_ =>
                {
                    _deferredRefreshPending = true;
                }, null, DeferredRefreshDelayMs * (_deferredRefreshRetries + 1), System.Threading.Timeout.Infinite);
            }
            else
            {
                try { _deferredRefreshTimer?.Dispose(); } catch { }
                _deferredRefreshTimer = null;
            }
        }

        /// <summary>
        /// The actual VM/widget refresh logic — extracted from the old Timer callback.
        /// MUST run on the main thread (called from TickMainThread).
        /// </summary>
        private static void DoDeferredRefresh()
        {
            var behavior = EncyclopediaEditBehavior.Instance;
            int nameOverrides = 0;
            lock (_instances)
            {
                _instances.RemoveAll(wr => { SettlementNameplateVM t; return !wr.TryGetTarget(out t); });
                foreach (var wr in _instances)
                {
                    if (wr.TryGetTarget(out var vm))
                    {
                        try { vm.RefreshValues(); }
                        catch (Exception ex) { MCMSettings.DebugLog("[SettlementNameplateMixin] deferred refresh: " + ex.ToString()); }

                        // Apply custom name if one exists
                        if (behavior != null)
                        {
                            try
                            {
                                Settlement s = GetSettlementFromVM(vm);
                                if (s != null)
                                {
                                    string customName = behavior.GetCustomName(s.StringId);
                                    if (!string.IsNullOrEmpty(customName))
                                    {
                                        // Debug: dump all string properties to find the right one
                                        if (nameOverrides == 0)
                                        {
                                            var allProps = vm.GetType().GetProperties(_flags);
                                            foreach (var prop in allProps)
                                            {
                                                if (prop.PropertyType == typeof(string) && prop.CanRead)
                                                {
                                                    try
                                                    {
                                                        string val = prop.GetValue(vm) as string;
                                                        if (!string.IsNullOrEmpty(val) && val.Length < 100)
                                                            MCMSettings.DebugLog("Nameplate prop: " + prop.Name + " = \"" + val + "\"");
                                                    }
                                                    catch (Exception ex) { MCMSettings.DebugLog("[SettlementNameplateMixin] prop dump: " + ex.ToString()); }
                                                }
                                            }
                                        }

                                        // VM.Name is correct but the Gauntlet widget text is cached.
                                        // Walk the widget tree to find and update TextWidgets directly.
                                        try
                                        {
                                            // Get the NameplateWidget that the VM is bound to
                                            // via the view layer's widget tree
                                            var topScreen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen;
                                            if (topScreen != null)
                                            {
                                                var layers = topScreen.GetType().GetProperty("Layers", _flags)?.GetValue(topScreen)
                                                    as System.Collections.IEnumerable;
                                                if (layers != null)
                                                {
                                                    foreach (var layer in layers)
                                                    {
                                                        // Find GauntletLayers with nameplate widgets
                                                        var rootWidget = layer.GetType().GetProperty("RootWidget", _flags)?.GetValue(layer);
                                                        if (rootWidget == null) continue;
                                                        try
                                                        {
                                                            UpdateTextWidgetsRecursive(rootWidget, s.Name?.ToString() ?? "", customName);
                                                        }
                                                        catch (Exception ex) { MCMSettings.DebugLog("[SettlementNameplateMixin] widget walk inner: " + ex.ToString()); }
                                                    }
                                                }
                                            }
                                        }
                                        catch (Exception ex) { MCMSettings.DebugLog("Nameplate: widget walk failed: " + ex.ToString()); }
                                        nameOverrides++;
                                    }
                                }
                            }
                            catch (Exception ex) { MCMSettings.DebugLog("[SettlementNameplateMixin] custom name apply: " + ex.ToString()); }
                        }
                    }
                }
            }
            MCMSettings.DebugLog("SettlementNameplateMixin: deferred refresh completed (retry " + _deferredRefreshRetries + ", nameOverrides=" + nameOverrides + ")");
        }

        /// <summary>
        /// Refreshes all tracked nameplate VMs on the main thread.
        /// Dumps all string properties of the first renamed VM for diagnosis.
        /// </summary>
        public static int RefreshAllNameplatesMainThread()
        {
            var behavior = EncyclopediaEditBehavior.Instance;
            if (behavior == null) return 0;

            int refreshed = 0;
            bool dumpedFirst = false;

            lock (_instances)
            {
                _instances.RemoveAll(wr => { SettlementNameplateVM t; return !wr.TryGetTarget(out t); });
                MCMSettings.DebugLog("NameplateMainThread: " + _instances.Count + " tracked VMs");

                foreach (var wr in _instances)
                {
                    if (!wr.TryGetTarget(out var vm)) continue;
                    try
                    {
                        Settlement s = GetSettlementFromVM(vm);
                        if (s == null) continue;

                        string customName = behavior.GetCustomName(s.StringId);
                        if (string.IsNullOrEmpty(customName)) continue;

                        // Dump ALL properties of the first renamed settlement's VM
                        if (!dumpedFirst)
                        {
                            dumpedFirst = true;
                            var allProps = vm.GetType().GetProperties(_flags);
                            foreach (var prop in allProps)
                            {
                                try
                                {
                                    if (!prop.CanRead) continue;
                                    object val = prop.GetValue(vm);
                                    string valStr = val?.ToString() ?? "null";
                                    if (valStr.Length > 80) valStr = valStr.Substring(0, 80) + "...";
                                    MCMSettings.DebugLog("  VM." + prop.Name + " (" + prop.PropertyType.Name + ") = " + valStr);
                                }
                                catch { }
                            }
                        }

                        // Set VM.Name and find the bound TextWidget to update its Text directly.
                        // The Gauntlet binding is one-time — setting VM.Name alone doesn't update the widget.
                        vm.Name = customName;
                        try
                        {
                            // The VM has a _boundWidget or similar reference to its view
                            // Walk the VM's type hierarchy to find any Widget reference
                            var vmType = vm.GetType();
                            var baseType = vmType;
                            while (baseType != null && baseType != typeof(object))
                            {
                                foreach (var field in baseType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                                {
                                    if (field.FieldType.Name.Contains("Widget") || field.FieldType.Name.Contains("View"))
                                    {
                                        if (!dumpedFirst)
                                            MCMSettings.DebugLog("NameplateMainThread: VM field " + field.Name + " (" + field.FieldType.Name + ")");
                                    }
                                }
                                baseType = baseType.BaseType;
                            }
                        }
                        catch { }
                        dumpedFirst = true;
                        refreshed++;
                    }
                    catch { }
                }
            }
            return refreshed;
        }

        /// <summary>
        /// Finds and updates the SettlementNameTextWidget for a renamed settlement.
        /// Walks all layers, their movies, and widget trees searching by widget Id.
        /// </summary>
        public static void UpdateNameplateWidgets(string settlementId, string customName)
        {
            if (string.IsNullOrEmpty(customName)) return;
            try
            {
                var topScreen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen;
                if (topScreen == null) return;

                var layersProp = topScreen.GetType().GetProperty("Layers", _flags);
                var layers = layersProp?.GetValue(topScreen) as System.Collections.IEnumerable;
                if (layers == null) return;

                int found = 0;
                foreach (var layer in layers)
                {
                    // Try RootWidget
                    var rootWidget = layer.GetType().GetProperty("RootWidget", _flags)?.GetValue(layer);
                    if (rootWidget != null)
                        found += FindAndSetNameWidget(rootWidget, customName);

                    // Try _moviesAndDatasources (GauntletLayer internal list of movie+datasource pairs)
                    var moviesField = layer.GetType().GetField("_moviesAndDatasources", _flags)
                        ?? layer.GetType().GetField("_movies", _flags);
                    if (moviesField != null)
                    {
                        var movies = moviesField.GetValue(layer) as System.Collections.IEnumerable;
                        if (movies != null)
                        {
                            foreach (var moviePair in movies)
                            {
                                // Each entry is a tuple/pair — first element is the movie (IGauntletMovie)
                                // which has a RootWidget
                                try
                                {
                                    object movie = null;
                                    var item1 = moviePair.GetType().GetField("Item1", _flags)
                                        ?? moviePair.GetType().GetProperty("Item1", _flags) as System.Reflection.MemberInfo;
                                    if (item1 is System.Reflection.FieldInfo fi)
                                        movie = fi.GetValue(moviePair);
                                    else if (item1 is System.Reflection.PropertyInfo pi)
                                        movie = pi.GetValue(moviePair);

                                    if (movie != null)
                                    {
                                        var movieRoot = movie.GetType().GetProperty("RootWidget", _flags)?.GetValue(movie);
                                        if (movieRoot != null)
                                            found += FindAndSetNameWidget(movieRoot, customName);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                if (found > 0)
                {
                    MCMSettings.DebugLog("Nameplate widget: updated " + found + " SettlementNameTextWidgets");
                    return;
                }

                // Nameplate widgets aren't in screen layers — they're in MapView system.
                // Search through MapScreen's views for the GauntletLayer holding nameplates.
                try
                {
                    var mapScreen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen;
                    // MapScreen._mapViews or similar — get all registered views
                    // Find _mapViewsContainer on MapScreen (holds nameplate views)
                    System.Reflection.FieldInfo viewsField = null;
                    object viewsContainer = null;
                    var searchType = mapScreen.GetType();
                    while (searchType != null && searchType != typeof(object))
                    {
                        var f = searchType.GetField("_mapViewsContainer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (f != null)
                        {
                            viewsContainer = f.GetValue(mapScreen);
                            MCMSettings.DebugLog("Nameplate widget: found _mapViewsContainer type=" + (viewsContainer?.GetType().Name ?? "null"));
                            // Get the views list from the container
                            if (viewsContainer != null)
                            {
                                // Dump ALL fields on the container and find a non-empty list
                                foreach (var cf in viewsContainer.GetType().GetFields(_flags))
                                {
                                    MCMSettings.DebugLog("Nameplate widget: container." + cf.Name + " (" + cf.FieldType.Name + ")");
                                    if (cf.FieldType.Name.Contains("List") || cf.FieldType.Name.Contains("Dictionary"))
                                    {
                                        try
                                        {
                                            var listVal = cf.GetValue(viewsContainer) as System.Collections.IEnumerable;
                                            if (listVal != null)
                                            {
                                                int cnt = 0;
                                                foreach (var item in listVal)
                                                {
                                                    cnt++;
                                                    if (cnt <= 3)
                                                        MCMSettings.DebugLog("Nameplate widget:   [" + cnt + "] " + item?.GetType().Name);
                                                }
                                                MCMSettings.DebugLog("Nameplate widget:   total=" + cnt);
                                                if (cnt > 0 && viewsField == null)
                                                    viewsField = cf;
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                            break;
                        }
                        searchType = searchType.BaseType;
                    }
                    // Fallback: _components on ScreenBase
                    if (viewsField == null)
                    {
                        searchType = mapScreen.GetType();
                        while (searchType != null && searchType != typeof(object) && viewsField == null)
                        {
                            viewsField = searchType.GetField("_components", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                            searchType = searchType.BaseType;
                        }
                    }
                    object viewsSource = viewsContainer ?? mapScreen;
                    if (viewsField != null)
                    {
                        var rawViews = viewsField.GetValue(viewsSource);
                        MCMSettings.DebugLog("Nameplate widget: found " + viewsField.Name + " type=" + (rawViews?.GetType().Name ?? "null") + " on " + viewsSource.GetType().Name);
                        var views = rawViews as System.Collections.IEnumerable;
                        if (views != null)
                        {
                            foreach (var view in views)
                            {
                                string viewName = view.GetType().Name;
                                if (!viewName.Contains("Nameplate") && !viewName.Contains("Settlement")) continue;
                                MCMSettings.DebugLog("Nameplate widget: inspecting " + viewName);

                                // Dump ALL fields on nameplate view + base types
                                var vType = view.GetType();
                                while (vType != null && vType != typeof(object))
                                {
                                    foreach (var vf in vType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                                        MCMSettings.DebugLog("Nameplate widget:   " + vType.Name + "." + vf.Name + " (" + vf.FieldType.Name + ")");
                                    vType = vType.BaseType;
                                }

                                // Search ALL fields for layers, movies, widgets
                                foreach (var field in view.GetType().GetFields(_flags))
                                {
                                    if (field.FieldType.Name.Contains("Layer") || field.FieldType.Name.Contains("Movie"))
                                    {
                                        var obj = field.GetValue(view);
                                        if (obj == null) continue;
                                        MCMSettings.DebugLog("Nameplate widget: accessing " + field.Name + " type=" + obj.GetType().Name);

                                        // Try RootWidget on this object
                                        var rootWidget = obj.GetType().GetProperty("RootWidget", _flags)?.GetValue(obj);
                                        MCMSettings.DebugLog("Nameplate widget: RootWidget=" + (rootWidget?.GetType().Name ?? "null")
                                            + " children=" + (rootWidget?.GetType().GetProperty("ChildCount", _flags)?.GetValue(rootWidget) ?? "?"));
                                        if (rootWidget != null)
                                        {
                                            found += FindAndSetNameWidget(rootWidget, customName);
                                        }
                                        // Also check movies
                                        var moviesField2 = obj.GetType().GetField("_moviesAndDatasources", _flags);
                                        if (moviesField2 != null)
                                        {
                                            var movies = moviesField2.GetValue(obj) as System.Collections.IEnumerable;
                                            if (movies != null)
                                                foreach (var mp in movies)
                                                {
                                                    try
                                                    {
                                                        object movie = null;
                                                        var i1 = mp.GetType().GetField("Item1", _flags);
                                                        if (i1 != null) movie = i1.GetValue(mp);
                                                        else { var p1 = mp.GetType().GetProperty("Item1", _flags); if (p1 != null) movie = p1.GetValue(mp); }
                                                        if (movie != null)
                                                        {
                                                            var mr = movie.GetType().GetProperty("RootWidget", _flags)?.GetValue(movie);
                                                            if (mr != null) found += FindAndSetNameWidget(mr, customName);
                                                        }
                                                    }
                                                    catch { }
                                                }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    // If 0 components, dump NavalMapScreen fields to find nameplate layer
                    if (viewsField == null || found == 0)
                    {
                        var t = mapScreen.GetType();
                        while (t != null && t != typeof(object))
                        {
                            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                            {
                                string ftName = f.FieldType.Name;
                                if (ftName.Contains("List") || ftName.Contains("View") || ftName.Contains("Layer")
                                    || ftName.Contains("Nameplate") || ftName.Contains("Gauntlet") || ftName.Contains("Widget"))
                                    MCMSettings.DebugLog("Nameplate widget: " + t.Name + "." + f.Name + " (" + ftName + ")");
                            }
                            t = t.BaseType;
                        }
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("Nameplate widget: MapView search failed: " + ex.ToString()); }

                if (found > 0)
                    MCMSettings.DebugLog("Nameplate widget: updated " + found + " via MapView");
                else
                    MCMSettings.DebugLog("Nameplate widget: SettlementNameTextWidget not found anywhere");
            }
            catch (Exception ex) { MCMSettings.DebugLog("Nameplate widget walk failed: " + ex.ToString()); }
        }

        /// <summary>
        /// Recursively searches for widgets with Id containing "SettlementName" or "NameText"
        /// and sets their Text property to the custom name.
        /// </summary>
        private static int FindAndSetNameWidget(object widget, string customName, int depth = 0)
        {
            if (widget == null || depth > 25) return 0;
            int found = 0;
            try
            {
                var type = widget.GetType();

                // Check widget Id
                var idProp = type.GetProperty("Id", _flags);
                string id = idProp?.GetValue(widget) as string ?? "";

                if (id == "SettlementNameTextWidget")
                {
                    var textProp = type.GetProperty("Text", _flags);
                    if (textProp != null && textProp.CanWrite)
                    {
                        string oldText = textProp.GetValue(widget) as string;
                        textProp.SetValue(widget, customName);
                        MCMSettings.DebugLog("Nameplate widget: set SettlementNameTextWidget.Text \"" + oldText + "\" -> \"" + customName + "\"");
                        found++;
                    }
                }

                // Recurse children
                var countProp = type.GetProperty("ChildCount", _flags);
                var getChild = type.GetMethod("GetChild", _flags, null, new[] { typeof(int) }, null);
                if (countProp != null && getChild != null)
                {
                    int count = (int)countProp.GetValue(widget);
                    for (int i = 0; i < count; i++)
                    {
                        var child = getChild.Invoke(widget, new object[] { i });
                        found += FindAndSetNameWidget(child, customName, depth + 1);
                    }
                }
            }
            catch { }
            return found;
        }

        /// <summary>
        /// Recursively walks a widget tree and replaces text matching origName with customName.
        /// Targets TextWidget and RichTextWidget instances.
        /// </summary>
        private static bool _loggedWidgetDiag = false;

        private static void UpdateTextWidgetsRecursive(object widget, string origName, string customName, int depth = 0)
        {
            if (widget == null || string.IsNullOrEmpty(origName) || depth > 20) return;
            try
            {
                var type = widget.GetType();
                string typeName = type.Name;

                // Check ALL widgets for a Text property, not just TextWidget
                var textProp = type.GetProperty("Text", _flags);
                if (textProp != null && textProp.CanRead && textProp.PropertyType == typeof(string))
                {
                    try
                    {
                        string text = textProp.GetValue(widget) as string;
                        if (text != null && text.Contains(origName))
                        {
                            if (textProp.CanWrite)
                            {
                                textProp.SetValue(widget, text.Replace(origName, customName));
                                MCMSettings.DebugLog("Nameplate: replaced widget text \"" + origName + "\" -> \"" + customName + "\" on " + typeName);
                            }
                            else
                            {
                                MCMSettings.DebugLog("Nameplate: found \"" + origName + "\" in " + typeName + ".Text but property is read-only");
                            }
                        }
                    }
                    catch { }
                }

                // Recurse via ChildCount + GetChild (Gauntlet standard pattern)
                var countProp = type.GetProperty("ChildCount", _flags);
                var getChild = type.GetMethod("GetChild", _flags, null, new[] { typeof(int) }, null);
                if (countProp != null && getChild != null)
                {
                    int count = (int)countProp.GetValue(widget);
                    for (int i = 0; i < count; i++)
                    {
                        var child = getChild.Invoke(widget, new object[] { i });
                        UpdateTextWidgetsRecursive(child, origName, customName, depth + 1);
                    }
                }
                else
                {
                    // Fallback: Children property
                    var childrenProp = type.GetProperty("Children", _flags);
                    if (childrenProp != null)
                    {
                        var children = childrenProp.GetValue(widget) as System.Collections.IEnumerable;
                        if (children != null)
                            foreach (var child in children)
                                UpdateTextWidgetsRecursive(child, origName, customName, depth + 1);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Gets the Settlement from a SettlementNameplateVM.
        /// The diagnostic dump confirmed: Settlement : Settlement [R]
        /// </summary>
        private static Settlement GetSettlementFromVM(SettlementNameplateVM vm)
        {
            // Direct property access (confirmed from diagnostic dump)
            try
            {
                var prop = vm.GetType().GetProperty("Settlement", _flags);
                if (prop == null)
                {
                    MCMSettings.DebugLog("[SettlementNameplateMixin]: GetProperty('Settlement') returned null");
                }
                var val = prop?.GetValue(vm) as Settlement;
                if (val != null) return val;
            }
            catch (Exception ex) { MCMSettings.DebugLog("[SettlementNameplateMixin]: " + ex.ToString()); }

            // Fallback: search fields
            foreach (var field in vm.GetType().GetFields(_flags))
            {
                try
                {
                    if (field.FieldType == typeof(Settlement) || field.FieldType.Name == "Settlement")
                    {
                        var val = field.GetValue(vm) as Settlement;
                        if (val != null) return val;
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("[SettlementNameplateMixin]: " + ex.ToString()); }
            }

            return null;
        }

        /// <summary>
        /// Sets the Banner property on the nameplate VM and fires property change notification.
        /// The diagnostic dump confirmed: Banner : BannerImageIdentifierVM [RW]
        /// </summary>
        private static bool SetBannerOnNameplate(SettlementNameplateVM vm, Banner banner)
        {
            bool updated = false;

            // Target the confirmed "Banner" property (BannerImageIdentifierVM [RW])
            try
            {
                var bannerProp = vm.GetType().GetProperty("Banner", _flags);
                if (bannerProp != null && bannerProp.CanWrite
                    && bannerProp.PropertyType.Name.Contains("ImageIdentifier"))
                {
                    var imgType = bannerProp.PropertyType;
                    var ctor = imgType.GetConstructor(new[] { typeof(Banner) })
                            ?? imgType.GetConstructor(new[] { typeof(Banner), typeof(bool) });
                    if (ctor != null)
                    {
                        var args = ctor.GetParameters().Length == 1
                            ? new object[] { banner }
                            : new object[] { banner, true };
                        var newImg = ctor.Invoke(args);
                        bannerProp.SetValue(vm, newImg);
                        updated = true;

                        // Also fire OnPropertyChanged to ensure Gauntlet rebinds the widget
                        try
                        {
                            var onPropChanged = vm.GetType().GetMethod("OnPropertyChanged",
                                _flags, null, new[] { typeof(string) }, null);
                            if (onPropChanged == null)
                                MCMSettings.DebugLog("[SettlementNameplateMixin]: GetMethod('OnPropertyChanged') returned null");
                            onPropChanged?.Invoke(vm, new object[] { "Banner" });
                        }
                        catch (Exception ex) { MCMSettings.DebugLog("[SettlementNameplateMixin]: " + ex.ToString()); }
                    }
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("[SettlementNameplateMixin]: " + ex.ToString()); }

            // Also check for any other ImageIdentifier properties (broader search)
            foreach (var prop in vm.GetType().GetProperties(_flags))
            {
                try
                {
                    if (!prop.CanWrite || !prop.CanRead) continue;
                    if (prop.Name == "Banner") continue; // Already handled above

                    bool isBannerImage = prop.PropertyType.Name.Contains("ImageIdentifier")
                        && (prop.Name.IndexOf("Banner", StringComparison.OrdinalIgnoreCase) >= 0
                            || prop.Name.IndexOf("Clan", StringComparison.OrdinalIgnoreCase) >= 0);

                    if (isBannerImage)
                    {
                        var imgType = prop.PropertyType;
                        var ctor = imgType.GetConstructor(new[] { typeof(Banner) })
                                ?? imgType.GetConstructor(new[] { typeof(Banner), typeof(bool) });
                        if (ctor != null)
                        {
                            var args = ctor.GetParameters().Length == 1
                                ? new object[] { banner }
                                : new object[] { banner, true };
                            var newImg = ctor.Invoke(args);
                            prop.SetValue(vm, newImg);
                            updated = true;
                            MCMSettings.DebugLog("  Also updated " + prop.Name + " on nameplate VM");
                        }
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("[SettlementNameplateMixin]: " + ex.ToString()); }
            }

            return updated;
        }
    }
}
