using System;
using System.Reflection;
using HarmonyLib;
using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.ViewModels;
using SandBox.ViewModelCollection.Nameplate;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using BandItPlus.HideoutVisit;

namespace BandItPlus.UI
{
    // v108 (2026-05-13): targeted Phase 2 — UIExtenderEx ViewModelMixin for the
    // vanilla SettlementNameplateVM. When the nameplate is constructed for a
    // HIDEOUT, override its banner reference to the chief's clan banner.
    //
    // v110 (2026-05-15): adds NEW exposed properties (HideoutChiefBannerVisual,
    // HasHideoutChiefBanner) so a PrefabExtension can inject a custom widget
    // bound to them. The vanilla SettlementBannerWidget (line 85 of
    // SettlementNameplateItemSmall.xml) is either hidden behind the hideout
    // sprite or rendered too subtly to be visible — injecting a fresh widget
    // ON TOP via prefab XPath patch is the only path that can visually override
    // the baked hideout icon.
    //
    // Pattern adapted from EditableEncyclopedia\SettlementNameplateMixin.cs:28.
    [ViewModelMixin]
    public class BandItPlusNameplateMixin : BaseViewModelMixin<SettlementNameplateVM>
    {
        private static readonly BindingFlags _flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static bool _firstAppliedLogged;
        private static int _ctorCount;
        private static bool _propListLogged;

        // v110: new bindable properties exposed to XML via [DataSourceProperty].
        // UIExtenderEx auto-discovers these on mixin registration and routes
        // {HideoutChiefBannerVisual}/{HasHideoutChiefBanner} bindings to them.
        //
        // We type HideoutChiefBannerVisual as ViewModel (the common base) because
        // BannerImageIdentifierVM lives in TaleWorlds.Core.ViewModelCollection.ImageIdentifiers
        // which is NOT a compile-time accessible namespace in our SDK references
        // (the assembly is loaded at runtime via SandBox.ViewModelCollection.Nameplate).
        // We construct the concrete VM via reflection in Apply().
        private ViewModel _hideoutChiefBannerVisual;
        private bool _hasHideoutChiefBanner;

        // Cached BannerImageIdentifierVM type, resolved via reflection once on first use.
        private static System.Type _bannerImageIdentifierVMType;
        private static System.Reflection.ConstructorInfo _bannerImageIdentifierVMCtor;

        [DataSourceProperty]
        public ViewModel HideoutChiefBannerVisual
        {
            get { return _hideoutChiefBannerVisual; }
            set
            {
                if (value != _hideoutChiefBannerVisual)
                {
                    _hideoutChiefBannerVisual = value;
                    ViewModel?.OnPropertyChangedWithValue(value, "HideoutChiefBannerVisual");
                }
            }
        }

        [DataSourceProperty]
        public bool HasHideoutChiefBanner
        {
            get { return _hasHideoutChiefBanner; }
            set
            {
                if (value != _hasHideoutChiefBanner)
                {
                    _hasHideoutChiefBanner = value;
                    ViewModel?.OnPropertyChangedWithValue(value, "HasHideoutChiefBanner");
                }
            }
        }

        public BandItPlusNameplateMixin(SettlementNameplateVM vm) : base(vm)
        {
            try
            {
                _ctorCount++;
                Settlement s = ResolveSettlement(vm);
                if (_ctorCount <= 5 || (s != null && s.IsHideout))
                {
                    HideoutPeacefulVisitState.Log("v110 mixin #" + _ctorCount
                        + " ctor vmType=" + vm.GetType().Name
                        + " settlement=" + (s?.StringId ?? "null")
                        + " isHideout=" + (s?.IsHideout ?? false));
                }
                if (!_propListLogged)
                {
                    _propListLogged = true;
                    var sb = new System.Text.StringBuilder();
                    foreach (var p in vm.GetType().GetProperties(_flags))
                        sb.Append(p.Name).Append("(").Append(p.PropertyType.Name).Append("); ");
                    HideoutPeacefulVisitState.Log("v110 mixin VM properties: " + sb);
                }
                Apply(vm);
                // v120: a hideout nameplate just constructed — open the urgent
                // poll window so its banner is overridden within ~100ms.
                if (s != null && s.IsHideout)
                {
                    _urgentUntilTick = Environment.TickCount + 2000;
                    // Wave 4.28.x: register for the peace-relation re-apply timer, so a
                    // hideout spotted while hostile recolours to neutral once peace is earned
                    // (OnRefresh/RefreshValues don't re-fire on an already-placed nameplate).
                    lock (_liveHideoutVMs) _liveHideoutVMs.Add(new System.WeakReference(vm));
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v110 mixin ctor fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public override void OnRefresh()
        {
            base.OnRefresh();
            try { Apply(ViewModel); } catch { }
        }

        private void Apply(SettlementNameplateVM vm)
        {
            if (vm == null) return;
            Settlement settlement = ResolveSettlement(vm);
            if (settlement == null || !settlement.IsHideout)
            {
                HasHideoutChiefBanner = false;
                HideoutChiefBannerVisual = null;
                return;
            }

            // Wave 4.27.0: neutralise the hostile (red) nameplate if at peace with this camp.
            ApplyPeaceRelation(vm, settlement);

            var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.Heroes.BanditChiefRegistry>();
            if (bdm == null) { HasHideoutChiefBanner = false; HideoutChiefBannerVisual = null; return; }
            var cultureId = settlement.Culture?.StringId;
            if (string.IsNullOrEmpty(cultureId)) { HasHideoutChiefBanner = false; HideoutChiefBannerVisual = null; return; }
            var chief = bdm.GetChief(cultureId);
            if (chief?.Clan?.Banner == null) { HasHideoutChiefBanner = false; HideoutChiefBannerVisual = null; return; }

            // 1) Override the vanilla Banner property as before (no-op on hideouts
            //    where the widget is hidden, but kept for non-hideout edge cases).
            TryOverrideVanillaBanner(vm, chief.Clan.Banner);

            // 2) v110: populate our NEW bindable properties for the injected widget.
            //    Instantiate BannerImageIdentifierVM via reflection — see above for
            //    why the type is not compile-time accessible.
            try
            {
                var ctor = ResolveBannerImageIdentifierVMType(vm);
                if (ctor == null)
                {
                    HideoutPeacefulVisitState.Log("v110.1 mixin: BannerImageIdentifierVM ctor not found via reflection");
                    HasHideoutChiefBanner = false;
                    HideoutChiefBannerVisual = null;
                    return;
                }
                // Build args: arg[0] = banner, remaining = default values.
                var pars = ctor.GetParameters();
                var args = new object[pars.Length];
                args[0] = chief.Clan.Banner;
                for (int i = 1; i < pars.Length; i++)
                {
                    if (pars[i].HasDefaultValue) args[i] = pars[i].DefaultValue;
                    else if (pars[i].ParameterType.IsValueType)
                        args[i] = Activator.CreateInstance(pars[i].ParameterType);
                    else args[i] = null;
                }
                var newVisual = ctor.Invoke(args) as ViewModel;
                HideoutChiefBannerVisual = newVisual;
                HasHideoutChiefBanner = newVisual != null;
                if (newVisual != null && !_firstAppliedLogged)
                {
                    _firstAppliedLogged = true;
                    HideoutPeacefulVisitState.Log("v110.1 mixin: first injected banner set for "
                        + cultureId + " on " + settlement.StringId
                        + " (vmType=" + newVisual.GetType().Name + " ctorArgs=" + pars.Length + ")");
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v110.1 mixin BannerImageIdentifierVM fail: "
                    + ex.GetType().Name + ": " + ex.Message
                    + (ex.InnerException != null ? " inner=" + ex.InnerException.Message : ""));
                HasHideoutChiefBanner = false;
                HideoutChiefBannerVisual = null;
            }

            // 3) v111 (2026-05-15): walk SettlementParties.PartiesInSettlement and
            //    override Visual on each marker. PartiesInSettlement IS populated
            //    (per screenshot showing two banners under hideout text) but only
            //    AFTER ctor time, so we re-walk on every OnRefresh().
            try
            {
                TryOverridePartyMarkers(vm, chief.Clan.Banner, cultureId, settlement);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v111 marker walk fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Wave 4.27.0 (2026-06-17): NEUTRALISE an at-peace hideout's hostile (red) map
        // nameplate. The red capsule is driven by SettlementNameplateVM.Relation (2 =
        // Enemy), computed in the engine's RefreshRelationStatus() — which only re-runs on
        // CampaignEvents.WarDeclared / MakePeace. BP's pledge-peace appends to a CSV and
        // fires NO MakePeaceAction, so a HIDEOUT spotted while hostile keeps its cached red
        // (_bindRelation = 2) forever ("spotted already → color never changes"). We force
        // Relation -> 0 (Neutral) AND reset the _bindRelation staging field (else the
        // per-frame RefreshBindValues copies the cached 2 straight back). Gated on peace,
        // so non-peace hideouts keep vanilla red.
        private static PropertyInfo _relationProp;
        private static FieldInfo _bindRelationField;
        private static bool _relationMembersResolved;
        private static void ApplyPeaceRelation(SettlementNameplateVM vm, Settlement settlement)
        {
            try
            {
                if (vm == null || settlement == null || !settlement.IsHideout) return;
                // Wave 4.28.x: stay hostile-red until peace is EARNED (not just vouched/camp access).
                if (!BandItPlus.BanditDialogManager.IsHideoutAtEarnedPeace(settlement)) return;

                if (!_relationMembersResolved)
                {
                    _relationMembersResolved = true;
                    _relationProp = typeof(SettlementNameplateVM).GetProperty("Relation", _flags);
                    _bindRelationField = typeof(SettlementNameplateVM).GetField("_bindRelation", _flags);
                    HideoutPeacefulVisitState.Log("[BP-Nameplate] relation members: Relation="
                        + (_relationProp != null) + " _bindRelation=" + (_bindRelationField != null));
                }

                // Stop the per-frame RefreshBindValues from copying the cached enemy (2) back.
                try { _bindRelationField?.SetValue(vm, 0); } catch { }

                // Push Neutral (0) onto the bound property; its setter fires the widget repaint.
                if (_relationProp != null && _relationProp.CanWrite)
                {
                    object cur = null;
                    try { cur = _relationProp.GetValue(vm); } catch { }
                    if (!(cur is int ci) || ci != 0)
                    {
                        try { _relationProp.SetValue(vm, 0); } catch { }
                    }
                }
            }
            catch { }
        }

        // Wave 4.28.x: live hideout nameplate VMs, re-ticked to re-assert at-peace neutrality.
        private static readonly System.Collections.Generic.List<System.WeakReference> _liveHideoutVMs
            = new System.Collections.Generic.List<System.WeakReference>();
        private static int _peaceRelationTickAt;

        // THE FIX (Wave 4.28.x). Root cause: ApplyPeaceRelation only ran at ctor /
        // OnRefresh / RefreshValues — none of which re-fire for an already-placed nameplate.
        // So earning peace (a CSV change firing NO engine relation event) never recoloured a
        // hideout spotted while hostile: it kept _bindRelation = 2 (red) forever. This timer
        // re-applies ApplyPeaceRelation to every live hideout nameplate VM (~400ms), mirroring
        // TickHideoutBanners' proven re-apply pattern; ApplyPeaceRelation self-gates on peace
        // and is a no-op once Relation is already 0, so non-peace hideouts keep vanilla red.
        public static void TickPeaceRelations()
        {
            if (Environment.TickCount < _peaceRelationTickAt) return;
            _peaceRelationTickAt = Environment.TickCount + 400;
            try
            {
                lock (_liveHideoutVMs)
                {
                    for (int i = _liveHideoutVMs.Count - 1; i >= 0; i--)
                    {
                        var vm = _liveHideoutVMs[i].Target as SettlementNameplateVM;
                        if (vm == null) { _liveHideoutVMs.RemoveAt(i); continue; }
                        Settlement s = ResolveSettlement(vm);
                        if (s == null || !s.IsHideout) continue;
                        ApplyPeaceRelation(vm, s);
                    }
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("TickPeaceRelations fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // v111: walks vm.SettlementParties.PartiesInSettlement on every Apply()
        // and overrides each marker's Visual (BannerImageIdentifierVM) to the
        // chief's clan banner. The collection is populated AFTER ctor time so
        // we re-walk on every OnRefresh() — no event subscription needed.
        private static int _markerWalkCount;
        private static bool _markerStructLogged;
        private static int _lastMarkerCount = -1;
        private static void TryOverridePartyMarkers(SettlementNameplateVM vm, Banner banner,
            string cultureId, Settlement settlement)
        {
            var partiesVmProp = vm.GetType().GetProperty("SettlementParties", _flags);
            if (partiesVmProp == null) return;
            object partiesVm;
            try { partiesVm = partiesVmProp.GetValue(vm); } catch { return; }
            if (partiesVm == null) return;

            var collectionProp = partiesVm.GetType().GetProperty("PartiesInSettlement", _flags);
            if (collectionProp == null) return;
            object collection;
            try { collection = collectionProp.GetValue(partiesVm); } catch { return; }
            if (!(collection is System.Collections.IEnumerable enumerable)) return;

            int markerCount = 0;
            int overridden = 0;
            foreach (var marker in enumerable)
            {
                if (marker == null) continue;
                markerCount++;
                if (!_markerStructLogged)
                {
                    _markerStructLogged = true;
                    var sb = new System.Text.StringBuilder();
                    foreach (var mp in marker.GetType().GetProperties(_flags))
                        sb.Append(mp.Name).Append("(").Append(mp.PropertyType.Name).Append("); ");
                    HideoutPeacefulVisitState.Log("v111 marker type=" + marker.GetType().Name + " props: " + sb);
                }
                if (TrySetMarkerVisual(marker, banner)) overridden++;
            }

            // Log when marker count changes OR first 5 walks per session.
            _markerWalkCount++;
            if (markerCount != _lastMarkerCount || _markerWalkCount <= 5)
            {
                _lastMarkerCount = markerCount;
                HideoutPeacefulVisitState.Log("v111 marker walk #" + _markerWalkCount
                    + " " + settlement.StringId + " (" + cultureId + ")"
                    + " markers=" + markerCount + " overridden=" + overridden);
            }
        }

        private static bool TrySetMarkerVisual(object marker, Banner banner)
        {
            // Per v109.4 + v110.1: target "Visual" property of type
            // BannerImageIdentifierVM, instantiate via (Banner, bool nineGrid) ctor.
            foreach (var propName in new[] { "Visual", "Banner" })
            {
                var p = marker.GetType().GetProperty(propName, _flags);
                if (p == null) continue;
                if (p.PropertyType.Name != "BannerImageIdentifierVM") continue;
                if (_bannerImageIdentifierVMCtor == null) return false;
                try
                {
                    var pars = _bannerImageIdentifierVMCtor.GetParameters();
                    var args = new object[pars.Length];
                    args[0] = banner;
                    for (int i = 1; i < pars.Length; i++)
                    {
                        if (pars[i].HasDefaultValue) args[i] = pars[i].DefaultValue;
                        else if (pars[i].ParameterType.IsValueType)
                            args[i] = Activator.CreateInstance(pars[i].ParameterType);
                        else args[i] = null;
                    }
                    p.SetValue(marker, _bannerImageIdentifierVMCtor.Invoke(args));
                    return true;
                }
                catch { return false; }
            }
            return false;
        }

        // Resolve BannerImageIdentifierVM via the vanilla VM's "Banner" property type.
        // v110.1 (2026-05-15): use typeof(SettlementNameplateVM).GetProperty (static type)
        // not vm.GetType() (UIExtenderEx-wrapped subclass) — see v110 log line 45645 where
        // the wrapped subclass's reflection lookup missed the 1-arg ctor. Also enumerate
        // ALL constructors and dump signatures for diagnosis when no single-arg form exists.
        // Fallback to multi-arg ctor with Banner-first matches the v108 working pattern.
        private static System.Reflection.ConstructorInfo[] _bannerImageIdentifierVMCtors;
        private static int _bannerImageIdentifierVMCtorParamCount;
        private static System.Reflection.ConstructorInfo ResolveBannerImageIdentifierVMType(SettlementNameplateVM vm)
        {
            if (_bannerImageIdentifierVMCtor != null) return _bannerImageIdentifierVMCtor;
            var bannerProp = typeof(SettlementNameplateVM).GetProperty("Banner", _flags);
            if (bannerProp == null) bannerProp = vm.GetType().GetProperty("Banner", _flags);
            if (bannerProp == null) return null;
            _bannerImageIdentifierVMType = bannerProp.PropertyType;

            // Try the 1-arg(Banner) form first.
            _bannerImageIdentifierVMCtor = _bannerImageIdentifierVMType.GetConstructor(new[] { typeof(Banner) });
            if (_bannerImageIdentifierVMCtor != null)
            {
                _bannerImageIdentifierVMCtorParamCount = 1;
                HideoutPeacefulVisitState.Log("v110.1 resolved BannerImageIdentifierVM type=" + _bannerImageIdentifierVMType.FullName
                    + " using 1-arg(Banner) ctor");
                return _bannerImageIdentifierVMCtor;
            }

            // Fallback: enumerate all public constructors, dump signatures, pick the
            // first one whose first parameter is Banner.
            _bannerImageIdentifierVMCtors = _bannerImageIdentifierVMType.GetConstructors();
            var sb = new System.Text.StringBuilder("v110.1 BannerImageIdentifierVM ctors (");
            sb.Append(_bannerImageIdentifierVMCtors.Length).Append("): ");
            foreach (var c in _bannerImageIdentifierVMCtors)
            {
                sb.Append("(");
                var ps = c.GetParameters();
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(ps[i].ParameterType.Name).Append(' ').Append(ps[i].Name);
                }
                sb.Append("); ");
                if (_bannerImageIdentifierVMCtor == null && ps.Length > 0
                    && (ps[0].ParameterType == typeof(Banner) || ps[0].ParameterType.Name == "Banner"))
                {
                    _bannerImageIdentifierVMCtor = c;
                    _bannerImageIdentifierVMCtorParamCount = ps.Length;
                }
            }
            HideoutPeacefulVisitState.Log(sb.ToString());
            HideoutPeacefulVisitState.Log("v110.1 selected ctor with " + _bannerImageIdentifierVMCtorParamCount + " args (found="
                + (_bannerImageIdentifierVMCtor != null) + ")");
            return _bannerImageIdentifierVMCtor;
        }

        private static System.Reflection.MethodInfo _onPropChangedWithValue;
        private static bool _onPropChangedResolved;
        private static void TryOverrideVanillaBanner(SettlementNameplateVM vm, Banner banner)
        {
            var bannerProp = typeof(SettlementNameplateVM).GetProperty("Banner", _flags);
            if (bannerProp == null) return;
            if (bannerProp.PropertyType.Name != "BannerImageIdentifierVM") return;
            object newBannerVM = null;
            try
            {
                var ctor = bannerProp.PropertyType.GetConstructor(new[] { typeof(Banner) });
                if (ctor != null)
                {
                    newBannerVM = ctor.Invoke(new object[] { banner });
                }
                else
                {
                    foreach (var c in bannerProp.PropertyType.GetConstructors())
                    {
                        var pars = c.GetParameters();
                        if (pars.Length == 0) continue;
                        if (pars[0].ParameterType == typeof(Banner))
                        {
                            var args = new object[pars.Length];
                            args[0] = banner;
                            for (int i = 1; i < pars.Length; i++)
                                args[i] = pars[i].DefaultValue ?? (pars[i].ParameterType.IsValueType
                                    ? Activator.CreateInstance(pars[i].ParameterType) : null);
                            newBannerVM = c.Invoke(args);
                            break;
                        }
                    }
                }
                if (newBannerVM == null) return;
                bannerProp.SetValue(vm, newBannerVM);
            }
            catch (Exception bex)
            {
                HideoutPeacefulVisitState.Log("v111.2 vanilla-banner override fail: "
                    + bex.GetType().Name + ": " + bex.Message);
                return;
            }

            // v111.2 (2026-05-15): v111.1 used OnPropertyChanged(name) which raises
            // the standard PropertyChanged event. Gauntlet's DataSource binding
            // listens to PropertyChangedWithValue instead — raised by
            // OnPropertyChangedWithValue(value, name). Reflect for that method
            // (it may be generic <T>(T, string), so handle MakeGenericMethod).
            try
            {
                if (!_onPropChangedResolved)
                {
                    _onPropChangedResolved = true;
                    // v111.3 (2026-05-15): ViewModel has type-specific overloads of
                    // OnPropertyChangedWithValue (bool/int/float/string/object/generic).
                    // v111.2 grabbed the (bool,string) overload and threw ArgumentException.
                    // Match the overload whose first param ACCEPTS our reference-type
                    // value; fall back to a generic <T> definition.
                    var bannerVMType = newBannerVM.GetType();
                    System.Reflection.MethodInfo genericCandidate = null;
                    foreach (var mi in vm.GetType().GetMethods(_flags))
                    {
                        if (mi.Name != "OnPropertyChangedWithValue") continue;
                        var ps = mi.GetParameters();
                        if (ps.Length != 2 || ps[1].ParameterType != typeof(string)) continue;
                        if (mi.IsGenericMethodDefinition)
                        {
                            genericCandidate = mi;
                            continue;
                        }
                        if (ps[0].ParameterType.IsAssignableFrom(bannerVMType))
                        {
                            _onPropChangedWithValue = mi;
                            break;
                        }
                    }
                    if (_onPropChangedWithValue == null && genericCandidate != null)
                        _onPropChangedWithValue = genericCandidate;
                    HideoutPeacefulVisitState.Log("v111.3 OnPropertyChangedWithValue resolved="
                        + (_onPropChangedWithValue != null
                            ? _onPropChangedWithValue.DeclaringType.FullName + " param0="
                              + _onPropChangedWithValue.GetParameters()[0].ParameterType.Name
                              + (_onPropChangedWithValue.IsGenericMethodDefinition ? " <generic>" : "")
                            : "NULL"));
                }
                if (_onPropChangedWithValue != null)
                {
                    var m = _onPropChangedWithValue;
                    if (m.IsGenericMethodDefinition)
                        m = m.MakeGenericMethod(newBannerVM.GetType());
                    m.Invoke(vm, new object[] { newBannerVM, "Banner" });
                }
            }
            catch (Exception nex)
            {
                HideoutPeacefulVisitState.Log("v111.3 OnPropertyChangedWithValue invoke fail: "
                    + nex.GetType().Name + ": " + nex.Message
                    + (nex.InnerException != null ? " inner=" + nex.InnerException.Message : ""));
            }
        }

        private static Settlement ResolveSettlement(SettlementNameplateVM vm)
        {
            foreach (var fname in new[] { "_settlement", "Settlement" })
            {
                var f = typeof(SettlementNameplateVM).GetField(fname, _flags);
                if (f != null && typeof(Settlement).IsAssignableFrom(f.FieldType))
                    return f.GetValue(vm) as Settlement;
            }
            var prop = typeof(SettlementNameplateVM).GetProperty("Settlement", _flags);
            if (prop != null) return prop.GetValue(vm) as Settlement;
            return null;
        }

        // v113 (2026-05-15): one-shot widget-tree diagnostic. The mixin ctor
        // requests a dump when a hideout nameplate is constructed; the dump runs
        // ~3s later (via SubModuleClassEntry.OnApplicationTick) once widgets are
        // built. Walks the screen widget tree and logs every Banner/Nameplate
        // widget's Id, type, visibility, size, Sprite, ImageId. DEFINITIVELY
        // answers whether the visible left banner is a {Banner}-bound widget or
        // a texture-baked nameplate sprite.
        private static volatile int _dumpAtTick;
        private static volatile bool _dumpDone;
        private static int _widgetsVisited;

        public static void RequestWidgetDump()
        {
            if (_dumpDone || _dumpAtTick != 0) return;
            _dumpAtTick = Environment.TickCount + 3000;
        }

        public static void TickWidgetDump()
        {
            if (_dumpDone || _dumpAtTick == 0) return;
            if (Environment.TickCount < _dumpAtTick) return;
            _dumpDone = true;
            try { DumpBannerWidgets(); }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v113 dump fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string WProp(object widget, string prop)
        {
            try { return System.Convert.ToString(widget.GetType().GetProperty(prop, _flags)?.GetValue(widget)); }
            catch { return "?"; }
        }

        private static void DumpBannerWidgets()
        {
            var top = TaleWorlds.ScreenSystem.ScreenManager.TopScreen;
            if (top == null) { HideoutPeacefulVisitState.Log("v114 dump: TopScreen=null"); return; }
            HideoutPeacefulVisitState.Log("v114 dump START TopScreen=" + top.GetType().Name);
            _widgetsVisited = 0;
            int total = 0;

            // (1) screen-layer walk (v113 — found 0; kept for completeness).
            var layers = top.GetType().GetProperty("Layers", _flags)?.GetValue(top)
                as System.Collections.IEnumerable;
            if (layers != null)
            {
                foreach (var layer in layers)
                {
                    var root = layer.GetType().GetProperty("RootWidget", _flags)?.GetValue(layer);
                    if (root != null) total += DumpWidgetRec(root, 0);
                    var moviesField = layer.GetType().GetField("_moviesAndDatasources", _flags);
                    var movies = moviesField?.GetValue(layer) as System.Collections.IEnumerable;
                    if (movies != null)
                    {
                        foreach (var mp in movies)
                        {
                            object movie = null;
                            var i1 = mp.GetType().GetField("Item1", _flags);
                            if (i1 != null) movie = i1.GetValue(mp);
                            var mr = movie?.GetType().GetProperty("RootWidget", _flags)?.GetValue(movie);
                            if (mr != null) total += DumpWidgetRec(mr, 0);
                        }
                    }
                }
            }
            HideoutPeacefulVisitState.Log("v114 layer walk: visited=" + _widgetsVisited
                + " bannerWidgets=" + total);

            // (2) v114: MapView-container walk — EE pattern (SettlementNameplateMixin.cs:496).
            total += DumpMapViewWidgets(top);
            HideoutPeacefulVisitState.Log("v114 dump END: total banner/nameplate widgets=" + total);
        }

        // v114: searches the map screen's _mapViewsContainer for nameplate views,
        // walks their GauntletLayer/Movie RootWidgets. Adapted from EE
        // SettlementNameplateMixin.cs:496-642.
        private static int DumpMapViewWidgets(object mapScreen)
        {
            object container = null;
            var t = mapScreen.GetType();
            while (t != null && t != typeof(object) && container == null)
            {
                var f = t.GetField("_mapViewsContainer",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) container = f.GetValue(mapScreen);
                t = t.BaseType;
            }
            if (container == null)
            {
                // Fallback diagnostic: dump the map screen's own field names.
                var sb = new System.Text.StringBuilder("v114 mapview: _mapViewsContainer NOT FOUND. screen fields: ");
                var st = mapScreen.GetType();
                while (st != null && st != typeof(object))
                {
                    foreach (var sf in st.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        sb.Append(st.Name).Append('.').Append(sf.Name).Append('(').Append(sf.FieldType.Name).Append("); ");
                    st = st.BaseType;
                }
                HideoutPeacefulVisitState.Log(sb.ToString());
                return 0;
            }
            HideoutPeacefulVisitState.Log("v115 mapview: container=" + container.GetType().Name);

            int viewsSeen = 0, bannerFound = 0;
            foreach (var cf in container.GetType().GetFields(_flags))
            {
                object cv;
                try { cv = cf.GetValue(container); } catch { continue; }
                if (cv is string) continue;
                if (!(cv is System.Collections.IEnumerable coll)) continue;
                foreach (var view in coll)
                {
                    if (view == null) continue;
                    viewsSeen++;
                    // v116: target ONLY the settlement nameplate view.
                    if (view.GetType().Name != "GauntletMapSettlementNameplateView") continue;

                    HideoutPeacefulVisitState.Log("v116 NAMEPLATE VIEW found");
                    var vsb = new System.Text.StringBuilder("v116 view fields: ");
                    foreach (var f in AllFields(view.GetType()))
                    {
                        object fv = null;
                        try { fv = f.GetValue(view); } catch { }
                        vsb.Append(f.Name).Append('(').Append(f.FieldType.Name).Append(")=")
                           .Append(fv == null ? "null" : fv.GetType().Name).Append("; ");
                    }
                    HideoutPeacefulVisitState.Log(vsb.ToString());

                    object gl = FindGauntletLayer(view, 0,
                        new System.Collections.Generic.HashSet<object>());
                    if (gl != null)
                    {
                        HideoutPeacefulVisitState.Log("v118 GauntletLayer found type=" + gl.GetType().Name);
                        // v118: enumerate THIS layer's own _movieIdentifiers — do NOT
                        // recurse the shared _twoDimensionContext/UIContext (v117 wandered
                        // into the party-nameplate tree that way).
                        var miField = gl.GetType().GetField("_movieIdentifiers", _flags);
                        var movies = miField?.GetValue(gl) as System.Collections.IEnumerable;
                        int mi = 0;
                        if (movies != null)
                        {
                            foreach (var m in movies)
                            {
                                mi++;
                                if (m == null) continue;
                                object root = FindWidgetRoot(m, 0,
                                    new System.Collections.Generic.HashSet<object>());
                                HideoutPeacefulVisitState.Log("v118 movie[" + mi + "] type="
                                    + m.GetType().Name + " root="
                                    + (root != null ? root.GetType().Name : "NULL"));
                                if (root != null) bannerFound += DumpWidgetRec(root, 0);
                            }
                        }
                        HideoutPeacefulVisitState.Log("v118 _movieIdentifiers count=" + mi
                            + " bannerWidgetsLogged=" + bannerFound);
                    }
                    else
                    {
                        HideoutPeacefulVisitState.Log("v118 GauntletLayer NOT FOUND in nameplate view");
                    }
                }
            }
            HideoutPeacefulVisitState.Log("v116 mapview: viewsSeen=" + viewsSeen
                + " visited=" + _widgetsVisited + " bannerWidgets=" + bannerFound);
            return bannerFound;
        }

        // v116: recursively locate the GauntletLayer inside the nameplate view —
        // it may be nested inside a wrapper-layer object (e.g. the MapBar view
        // holds its real GauntletLayer in a _gauntletLayer field on a wrapper).
        private static object FindGauntletLayer(object obj, int depth,
            System.Collections.Generic.HashSet<object> seen)
        {
            if (obj == null || depth > 4 || !seen.Add(obj)) return null;
            var t = obj.GetType();
            var bt = t;
            while (bt != null && bt != typeof(object))
            {
                if (bt.Name == "GauntletLayer") return obj;
                bt = bt.BaseType;
            }
            foreach (var f in AllFields(t))
            {
                var ftn = f.FieldType.Name;
                if (ftn.IndexOf("Layer", StringComparison.OrdinalIgnoreCase) < 0
                    && ftn.IndexOf("Gauntlet", StringComparison.OrdinalIgnoreCase) < 0) continue;
                object v;
                try { v = f.GetValue(obj); } catch { continue; }
                var r = FindGauntletLayer(v, depth + 1, seen);
                if (r != null) return r;
            }
            return null;
        }

        // v117: recursively descend from a GauntletLayer (through UIContext,
        // _movieIdentifiers, movies, etc.) and return the first widget-like
        // object — one exposing ChildCount + GetChild(int). Depth-limited and
        // cycle-guarded.
        private static object FindWidgetRoot(object obj, int depth,
            System.Collections.Generic.HashSet<object> seen)
        {
            if (obj == null || depth > 7 || !seen.Add(obj)) return null;
            var t = obj.GetType();
            // Is it a widget?
            if (t.GetProperty("ChildCount", _flags) != null
                && t.GetMethod("GetChild", _flags, null, new[] { typeof(int) }, null) != null)
                return obj;
            // Try common root-ish properties.
            foreach (var pn in new[] { "RootWidget", "Root", "RootView" })
            {
                var p = t.GetProperty(pn, _flags);
                if (p == null) continue;
                object pv = null;
                try { pv = p.GetValue(obj); } catch { }
                var r = FindWidgetRoot(pv, depth + 1, seen);
                if (r != null) return r;
            }
            // Recurse into context/movie/list/manager-ish fields and collections.
            foreach (var f in AllFields(t))
            {
                var ftn = f.FieldType.Name;
                bool follow = ftn.IndexOf("Context", StringComparison.OrdinalIgnoreCase) >= 0
                    || ftn.IndexOf("Movie", StringComparison.OrdinalIgnoreCase) >= 0
                    || ftn.IndexOf("Manager", StringComparison.OrdinalIgnoreCase) >= 0
                    || ftn.IndexOf("Widget", StringComparison.OrdinalIgnoreCase) >= 0
                    || ftn.IndexOf("Gauntlet", StringComparison.OrdinalIgnoreCase) >= 0
                    || ftn.IndexOf("List", StringComparison.OrdinalIgnoreCase) >= 0
                    || ftn.IndexOf("Identifier", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!follow) continue;
                object fv = null;
                try { fv = f.GetValue(obj); } catch { }
                if (fv == null) continue;
                if (fv is System.Collections.IEnumerable en && !(fv is string))
                {
                    foreach (var item in en)
                    {
                        var r = FindWidgetRoot(item, depth + 1, seen);
                        if (r != null) return r;
                    }
                }
                else
                {
                    var r = FindWidgetRoot(fv, depth + 1, seen);
                    if (r != null) return r;
                }
            }
            return null;
        }

        // v115: collect every field across the full type hierarchy (GetFields
        // omits inherited private fields — must walk base types with DeclaredOnly).
        private static System.Collections.Generic.List<FieldInfo> AllFields(Type type)
        {
            var list = new System.Collections.Generic.List<FieldInfo>();
            var t = type;
            while (t != null && t != typeof(object))
            {
                list.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                t = t.BaseType;
            }
            return list;
        }

        // v115: given a layer/movie/gauntlet object, try every known path to its
        // widget tree and recurse the dump.
        private static int TryWidgetsFrom(object obj, string viewName, string fieldName)
        {
            int found = 0;
            // path A: direct RootWidget property
            var rw = obj.GetType().GetProperty("RootWidget", _flags)?.GetValue(obj);
            if (rw != null) found += DumpWidgetRec(rw, 0);
            // path B: _movies / _moviesAndDatasources collections
            foreach (var mfName in new[] { "_movies", "_moviesAndDatasources" })
            {
                var mf = obj.GetType().GetField(mfName, _flags);
                var movies = mf?.GetValue(obj) as System.Collections.IEnumerable;
                if (movies == null) continue;
                foreach (var mp in movies)
                {
                    if (mp == null) continue;
                    object movie = mp;
                    var i1 = mp.GetType().GetField("Item1", _flags);
                    if (i1 != null) movie = i1.GetValue(mp) ?? mp;
                    var mr = movie?.GetType().GetProperty("RootWidget", _flags)?.GetValue(movie)
                        ?? movie?.GetType().GetProperty("RootView", _flags)?.GetValue(movie);
                    if (mr != null) found += DumpWidgetRec(mr, 0);
                }
            }
            // path C: _gauntletUIContext -> Root
            var ctxField = obj.GetType().GetField("_gauntletUIContext", _flags);
            var ctx = ctxField?.GetValue(obj);
            if (ctx != null)
            {
                var ctxRoot = ctx.GetType().GetProperty("Root", _flags)?.GetValue(ctx);
                if (ctxRoot != null) found += DumpWidgetRec(ctxRoot, 0);
            }
            if (found > 0)
                HideoutPeacefulVisitState.Log("v115 mapview HIT: view=" + viewName
                    + " field=" + fieldName + " bannerWidgets=" + found);
            return found;
        }

        private static int DumpWidgetRec(object widget, int depth)
        {
            if (widget == null || depth > 35) return 0;
            int found = 0;
            _widgetsVisited++;
            try
            {
                var t = widget.GetType();
                string id = "";
                try { id = t.GetProperty("Id", _flags)?.GetValue(widget) as string ?? ""; } catch { }
                bool match = depth <= 2
                    || id.IndexOf("Banner", StringComparison.OrdinalIgnoreCase) >= 0
                    || id.IndexOf("Nameplate", StringComparison.OrdinalIgnoreCase) >= 0
                    || id.IndexOf("Settlement", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.Name.IndexOf("Banner", StringComparison.OrdinalIgnoreCase) >= 0;
                if (match)
                {
                    HideoutPeacefulVisitState.Log("v113 W d=" + depth + " Id='" + id + "' t=" + t.Name
                        + " Vis=" + WProp(widget, "IsVisible") + " Hid=" + WProp(widget, "IsHidden")
                        + " W=" + WProp(widget, "Width") + " H=" + WProp(widget, "Height")
                        + " Spr=" + WProp(widget, "Sprite") + " ImgId=" + WProp(widget, "ImageId"));
                    found++;
                }
                var countProp = t.GetProperty("ChildCount", _flags);
                var getChild = t.GetMethod("GetChild", _flags, null, new[] { typeof(int) }, null);
                if (countProp != null && getChild != null)
                {
                    int cnt = 0;
                    try { cnt = (int)countProp.GetValue(widget); } catch { }
                    for (int i = 0; i < cnt; i++)
                    {
                        object child = null;
                        try { child = getChild.Invoke(widget, new object[] { i }); } catch { }
                        found += DumpWidgetRec(child, depth + 1);
                    }
                }
            }
            catch { }
            return found;
        }

        // ─────────────────────────────────────────────────────────────────
        // v119 (2026-05-15): THE FIX. Root cause (fully traced v108->v118):
        // the hideout's left banner is the SettlementBannerWidget — a
        // MaskedTextureWidget whose ImageId property holds a serialized banner
        // code string. Setting SettlementNameplateVM.Banner never propagated.
        // The widget tree IS reachable: MapViewsContainer ->
        // GauntletMapSettlementNameplateView -> GauntletLayer -> _movieIdentifiers
        // -> movie -> RootWidget -> ... -> SettlementNameplateCapsuleWidget ->
        // {SettlementNameTextWidget, SettlementBannerWidget}.
        //
        // Walk the tree, match each capsule's name text to a renamed hideout,
        // set the sibling SettlementBannerWidget.ImageId to the chief's clan
        // banner code. Widget setters self-invalidate, so reflection SetValue
        // on ImageId re-renders. Re-applied on a ~1.5s throttle.
        private static int _bannerApplyAtTick;
        private static int _bannerApplyLogCount;
        // v120: when a hideout nameplate VM constructs, the mixin ctor sets this
        // ~2s ahead. While Environment.TickCount is below it, TickHideoutBanners
        // polls every ~100ms instead of every 1.5s — so a freshly-spotted hideout
        // gets the chief banner within ~100ms (masked by the nameplate fade-in)
        // instead of flashing the vanilla banner for up to 1.5s.
        private static volatile int _urgentUntilTick;
        private static System.Collections.Generic.Dictionary<string, string> _hideoutBannerCodes;

        public static void TickHideoutBanners()
        {
            if (Environment.TickCount < _bannerApplyAtTick) return;
            bool urgent = Environment.TickCount < _urgentUntilTick;
            _bannerApplyAtTick = Environment.TickCount + (urgent ? 100 : 1500);
            try { ApplyHideoutBannerOverrides(); }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v119 tick fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void BuildHideoutBannerMap()
        {
            _hideoutBannerCodes = new System.Collections.Generic.Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.Heroes.BanditChiefRegistry>();
            if (bdm == null)
            {
                HideoutPeacefulVisitState.Log("v222 hideout banner map: registry null (skip)");
                return;
            }

            // v222 (2026-06-01): use GetAllChiefByCulture() instead of GetChief(cultureId).
            // GetChief() returns null after save/load when _runtimeChiefRefs rehydration
            // fails due to StringId collision (chiefs use CharacterObject StringIds which
            // MBObjectManager.GetObject<Hero>() can't resolve). GetAllChiefByCulture()
            // iterates _runtimeChiefRefs directly, which IS populated after CreateAllChiefs
            // re-runs at OnGameLoadFinished. This was returning 0 hideouts pre-v222.
            var chiefByCulture = new System.Collections.Generic.Dictionary<string, Hero>(
                StringComparer.OrdinalIgnoreCase);
            int chiefCount = 0;
            foreach (var kv in bdm.GetAllChiefByCulture())
            {
                chiefByCulture[kv.Key] = kv.Value;
                chiefCount++;
            }

            int hideoutsScanned = 0, missingChief = 0, missingBanner = 0;
            foreach (var s in Settlement.All)
            {
                if (s == null || !s.IsHideout) continue;
                hideoutsScanned++;
                string cid = s.Culture?.StringId;
                if (string.IsNullOrEmpty(cid)) { missingChief++; continue; }
                if (!chiefByCulture.TryGetValue(cid, out var chief))
                {
                    missingChief++;
                    continue;
                }
                if (chief?.Clan?.Banner == null) { missingBanner++; continue; }
                string code;
                try { code = chief.Clan.Banner.Serialize(); } catch { missingBanner++; continue; }
                if (string.IsNullOrEmpty(code)) { missingBanner++; continue; }
                string name = s.Name?.ToString();
                if (!string.IsNullOrEmpty(name)) _hideoutBannerCodes[name.Trim()] = code;
            }
            HideoutPeacefulVisitState.Log(
                "v222 hideout banner map built: " + _hideoutBannerCodes.Count + " hideouts (chiefs="
                + chiefCount + " hideoutsScanned=" + hideoutsScanned
                + " missingChief=" + missingChief + " missingBanner=" + missingBanner + ")");
        }

        private static void ApplyHideoutBannerOverrides()
        {
            if (Campaign.Current == null) return;
            if (_hideoutBannerCodes == null || _hideoutBannerCodes.Count == 0)
            {
                BuildHideoutBannerMap();
                if (_hideoutBannerCodes.Count == 0) return;
            }
            var top = TaleWorlds.ScreenSystem.ScreenManager.TopScreen;
            if (top == null) return;
            object container = null;
            var ct = top.GetType();
            while (ct != null && ct != typeof(object) && container == null)
            {
                var f = ct.GetField("_mapViewsContainer",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) container = f.GetValue(top);
                ct = ct.BaseType;
            }
            if (container == null) return;

            int applied = 0;
            foreach (var cf in container.GetType().GetFields(_flags))
            {
                object cv;
                try { cv = cf.GetValue(container); } catch { continue; }
                if (cv is string || !(cv is System.Collections.IEnumerable coll)) continue;
                foreach (var view in coll)
                {
                    if (view == null
                        || view.GetType().Name != "GauntletMapSettlementNameplateView") continue;
                    object gl = FindGauntletLayer(view, 0,
                        new System.Collections.Generic.HashSet<object>());
                    if (gl == null) continue;
                    var miField = gl.GetType().GetField("_movieIdentifiers", _flags);
                    var movies = miField?.GetValue(gl) as System.Collections.IEnumerable;
                    if (movies == null) continue;
                    foreach (var m in movies)
                    {
                        if (m == null) continue;
                        object root = FindWidgetRoot(m, 0,
                            new System.Collections.Generic.HashSet<object>());
                        if (root != null) applied += ApplyToCapsules(root, 0);
                    }
                }
            }
            if (applied > 0 && _bannerApplyLogCount < 8)
            {
                _bannerApplyLogCount++;
                HideoutPeacefulVisitState.Log("v119 applied chief banner to "
                    + applied + " hideout nameplate(s)");
            }
        }

        private static int ApplyToCapsules(object widget, int depth)
        {
            if (widget == null || depth > 35) return 0;
            int applied = 0;
            try
            {
                var t = widget.GetType();
                string id = t.GetProperty("Id", _flags)?.GetValue(widget) as string ?? "";
                if (id == "SettlementNameplateCapsuleWidget")
                    applied += ProcessCapsule(widget);

                var countProp = t.GetProperty("ChildCount", _flags);
                var getChild = t.GetMethod("GetChild", _flags, null, new[] { typeof(int) }, null);
                if (countProp != null && getChild != null)
                {
                    int cnt = 0;
                    try { cnt = (int)countProp.GetValue(widget); } catch { }
                    for (int i = 0; i < cnt; i++)
                    {
                        object child = null;
                        try { child = getChild.Invoke(widget, new object[] { i }); } catch { }
                        applied += ApplyToCapsules(child, depth + 1);
                    }
                }
            }
            catch { }
            return applied;
        }

        private static int ProcessCapsule(object capsule)
        {
            object nameW = null, bannerW = null;
            FindCapsuleChildren(capsule, 0, ref nameW, ref bannerW);
            if (nameW == null || bannerW == null) return 0;
            string name = null;
            try { name = nameW.GetType().GetProperty("Text", _flags)?.GetValue(nameW) as string; }
            catch { }
            if (string.IsNullOrEmpty(name)) return 0;
            if (!_hideoutBannerCodes.TryGetValue(name.Trim(), out string code)) return 0;
            try
            {
                var imgProp = bannerW.GetType().GetProperty("ImageId", _flags);
                if (imgProp == null || !imgProp.CanWrite) return 0;
                string cur = imgProp.GetValue(bannerW) as string;
                if (cur == code) return 0;  // already applied — no-op
                imgProp.SetValue(bannerW, code);
                return 1;
            }
            catch { return 0; }
        }

        private static void FindCapsuleChildren(object w, int depth,
            ref object nameW, ref object bannerW)
        {
            if (w == null || depth > 10 || (nameW != null && bannerW != null)) return;
            try
            {
                var t = w.GetType();
                string id = t.GetProperty("Id", _flags)?.GetValue(w) as string ?? "";
                if (id == "SettlementNameTextWidget") nameW = w;
                else if (id == "SettlementBannerWidget") bannerW = w;
                var countProp = t.GetProperty("ChildCount", _flags);
                var getChild = t.GetMethod("GetChild", _flags, null, new[] { typeof(int) }, null);
                if (countProp != null && getChild != null)
                {
                    int cnt = 0;
                    try { cnt = (int)countProp.GetValue(w); } catch { }
                    for (int i = 0; i < cnt; i++)
                    {
                        object child = null;
                        try { child = getChild.Invoke(w, new object[] { i }); } catch { }
                        FindCapsuleChildren(child, depth + 1, ref nameW, ref bannerW);
                    }
                }
            }
            catch { }
        }
        // ─────────────────────────────────────────────────────────────────

        // v112 (2026-05-15): Harmony postfix on SettlementNameplateVM.RefreshValues.
        // v111.3 confirmed our mixin sets Banner + fires the correct notification
        // ONCE at ctor time — but the visible banner never changes and OnRefresh
        // never re-fires (marker-walk log only ever shows walk #1). Hypothesis:
        // vanilla RefreshValues recomputes Banner from settlement.MapFaction AFTER
        // our ctor override, reverting it. This postfix re-applies the chief banner
        // AFTER vanilla's RefreshValues so we always win the race.
        //
        // Nested class so it can call the outer class's private static override
        // helpers. Auto-discovered by Harmony PatchAll() in SubModuleClassEntry.
        [HarmonyPatch]
        private static class NameplateRefreshPatch
        {
            private static bool _firstPostfixLogged;

            static MethodBase TargetMethod()
            {
                var declared = AccessTools.DeclaredMethod(typeof(SettlementNameplateVM), "RefreshValues");
                HideoutPeacefulVisitState.Log("v112 TargetMethod: SettlementNameplateVM.RefreshValues declared="
                    + (declared != null));
                return declared ?? AccessTools.Method(typeof(SettlementNameplateVM), "RefreshValues");
            }

            static void Postfix(object __instance)
            {
                try
                {
                    if (!(__instance is SettlementNameplateVM vm)) return;
                    Settlement settlement = ResolveSettlement(vm);
                    if (settlement == null || !settlement.IsHideout) return;

                    // Wave 4.27.0: re-assert the at-peace neutral relation AFTER vanilla's
                    // RefreshValues (which would otherwise copy the cached red back).
                    ApplyPeaceRelation(vm, settlement);

                    var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.Heroes.BanditChiefRegistry>();
                    var cultureId = settlement.Culture?.StringId;
                    if (string.IsNullOrEmpty(cultureId)) return;
                    var chief = bdm?.GetChief(cultureId);
                    if (chief?.Clan?.Banner == null) return;

                    // Re-resolve the BannerImageIdentifierVM ctor (cached after first call).
                    ResolveBannerImageIdentifierVMType(vm);
                    TryOverrideVanillaBanner(vm, chief.Clan.Banner);
                    TryOverridePartyMarkers(vm, chief.Clan.Banner, cultureId, settlement);

                    if (!_firstPostfixLogged)
                    {
                        _firstPostfixLogged = true;
                        HideoutPeacefulVisitState.Log("v112 postfix: RefreshValues fired for hideout "
                            + settlement.StringId + " — re-applied chief banner AFTER vanilla refresh");
                    }
                }
                catch (Exception ex)
                {
                    HideoutPeacefulVisitState.Log("v112 postfix fail: "
                        + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }
    }
}
