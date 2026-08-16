using System;
using System.Collections;
using System.Collections.Specialized;
using System.Reflection;
using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.ViewModels;
using SandBox.ViewModelCollection.Nameplate;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using BandItPlus.HideoutVisit;

namespace BandItPlus.UI
{
    // v109 (2026-05-13): Path A — override the party-marker banners under hideout
    // nameplates (the 3 small banners shown below "Granny Hod's Hideout").
    //
    // v110 (2026-05-15): DISABLED. The visible "banners" under the hideout
    // nameplate are NOT party markers — they are baked into the hideout map
    // icon's .tpac sprite sheet. PartiesInSettlement.count=0 at construction
    // AND no CollectionChanged events ever fire (confirmed via debug log).
    // SettlementNameplateItemSmall.xml only renders party markers for
    // IsDefault/IsCaravan/IsLord parties — hideouts have none. The mixin is
    // kept (without [ViewModelMixin]) to preserve the diagnostic code for
    // future reference, but UIExtenderEx no longer auto-discovers it.
    // See memory: reference_bannerlord_hideout_modding.md
    // [ViewModelMixin] -- intentionally removed in v110 to deregister.
    public class BandItPlusPartyMarkersMixin : BaseViewModelMixin<SettlementNameplatePartyMarkersVM>
    {
        private static readonly BindingFlags _flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static bool _propsLogged;
        private static bool _firstApplyLogged;
        private static bool _markerStructLogged;
        private static bool _zeroOverrideLogged;
        private static bool _countLogged;

        // v109.3: handler called when a marker collection mutates (vanilla adds parties).
        private static void OnMarkerCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            try
            {
                if (e.NewItems == null) return;
                HideoutPeacefulVisitState.Log("v109.3 CollectionChanged: action=" + e.Action
                    + " newItems=" + e.NewItems.Count);
                foreach (var marker in e.NewItems)
                {
                    if (marker == null) continue;
                    if (!_markerStructLogged)
                    {
                        _markerStructLogged = true;
                        var sb = new System.Text.StringBuilder();
                        foreach (var mp in marker.GetType().GetProperties(_flags))
                            sb.Append(mp.Name).Append("(").Append(mp.PropertyType.Name).Append("); ");
                        HideoutPeacefulVisitState.Log("v109.3 marker type=" + marker.GetType().Name + " props: " + sb);
                    }
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v109.3 collection handler fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public BandItPlusPartyMarkersMixin(SettlementNameplatePartyMarkersVM vm) : base(vm)
        {
            try
            {
                if (!_propsLogged)
                {
                    _propsLogged = true;
                    var sb = new System.Text.StringBuilder();
                    foreach (var p in vm.GetType().GetProperties(_flags))
                        sb.Append(p.Name).Append("(").Append(p.PropertyType.Name).Append("); ");
                    HideoutPeacefulVisitState.Log("v109 PartyMarkers VM properties: " + sb);
                }
                TryOverrideMarkers(vm);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v109 ctor fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public override void OnRefresh()
        {
            base.OnRefresh();
            try { TryOverrideMarkers(ViewModel); } catch { }
        }

        private static void TryOverrideMarkers(SettlementNameplatePartyMarkersVM vm)
        {
            if (vm == null) return;

            Settlement settlement = ResolveSettlement(vm);
            if (settlement == null || !settlement.IsHideout) return;

            var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.Heroes.BanditChiefRegistry>();
            if (bdm == null) return;
            var cultureId = settlement.Culture?.StringId;
            if (string.IsNullOrEmpty(cultureId)) return;
            var chief = bdm.GetChief(cultureId);
            if (chief?.Clan?.Banner == null) return;

            int overridden = 0;
            int markersSeen = 0;
            foreach (var prop in vm.GetType().GetProperties(_flags))
            {
                object value;
                try { value = prop.GetValue(vm); } catch { continue; }
                if (value is IEnumerable enumerable && !(value is string))
                {
                    // v109.3: subscribe to CollectionChanged so we re-apply when
                    // vanilla adds party markers later (collection is empty at ctor time).
                    if (value is INotifyCollectionChanged ncc)
                    {
                        try
                        {
                            // Capture context for the handler — settlement-scoped reapply
                            var capturedVm = vm;
                            var capturedBanner = chief.Clan.Banner;
                            NotifyCollectionChangedEventHandler handler = (s, e) =>
                            {
                                try
                                {
                                    if (e.NewItems != null)
                                    {
                                        foreach (var newMarker in e.NewItems)
                                        {
                                            if (newMarker == null) continue;
                                            TrySetMarkerBanner(newMarker, capturedBanner);
                                        }
                                        HideoutPeacefulVisitState.Log("v109.4 CollectionChanged: applied to "
                                            + e.NewItems.Count + " new markers");
                                    }
                                }
                                catch { }
                            };
                            ncc.CollectionChanged += handler;
                        }
                        catch { }
                    }
                    if (!_countLogged)
                    {
                        _countLogged = true;
                        int countDbg = 0;
                        foreach (var _x in enumerable) countDbg++;
                        HideoutPeacefulVisitState.Log("v109.2 collection '" + prop.Name
                            + "' count=" + countDbg + " for hideout " + settlement.StringId);
                    }
                    foreach (var marker in enumerable)
                    {
                        if (marker == null) continue;
                        markersSeen++;
                        // v109.1: one-time log of marker type + property names for diagnosis
                        if (!_markerStructLogged)
                        {
                            _markerStructLogged = true;
                            var sb = new System.Text.StringBuilder();
                            foreach (var mp in marker.GetType().GetProperties(_flags))
                                sb.Append(mp.Name).Append("(").Append(mp.PropertyType.Name).Append("); ");
                            HideoutPeacefulVisitState.Log("v109.1 marker type=" + marker.GetType().Name + " props: " + sb);
                        }
                        if (TrySetMarkerBanner(marker, chief.Clan.Banner)) overridden++;
                    }
                }
                else if (value != null && value.GetType().Name.Contains("Marker"))
                {
                    markersSeen++;
                    if (TrySetMarkerBanner(value, chief.Clan.Banner)) overridden++;
                }
            }
            if (markersSeen > 0 && overridden == 0 && !_zeroOverrideLogged)
            {
                _zeroOverrideLogged = true;
                HideoutPeacefulVisitState.Log("v109.1 ZERO override despite " + markersSeen + " markers seen for " + cultureId);
            }

            if (overridden > 0 && !_firstApplyLogged)
            {
                _firstApplyLogged = true;
                HideoutPeacefulVisitState.Log("v109 first apply: overrode " + overridden
                    + " markers for " + cultureId + " on " + settlement.StringId);
            }
        }

        private static bool TrySetMarkerBanner(object marker, Banner banner)
        {
            // v109.4: prefab inspection (SettlementNameplateItemSmall.xml:119) confirmed
            // party marker banner property is "Visual" (NOT "Banner"). Target both for safety.
            foreach (var propName in new[] { "Visual", "Banner" })
            {
                var bannerProp = marker.GetType().GetProperty(propName, _flags);
                if (bannerProp == null) continue;
                if (bannerProp.PropertyType.Name != "BannerImageIdentifierVM") continue;
                try
                {
                    var ctor = bannerProp.PropertyType.GetConstructor(new[] { typeof(Banner) });
                    if (ctor != null)
                    {
                        bannerProp.SetValue(marker, ctor.Invoke(new object[] { banner }));
                        return true;
                    }
                    var ctors = bannerProp.PropertyType.GetConstructors();
                    foreach (var c in ctors)
                    {
                        var pars = c.GetParameters();
                        if (pars.Length == 0) continue;
                        if (pars[0].ParameterType == typeof(Banner))
                        {
                            var args = new object[pars.Length];
                            args[0] = banner;
                            for (int i = 1; i < pars.Length; i++)
                                args[i] = pars[i].DefaultValue ?? (pars[i].ParameterType.IsValueType ? Activator.CreateInstance(pars[i].ParameterType) : null);
                            bannerProp.SetValue(marker, c.Invoke(args));
                            return true;
                        }
                    }
                }
                catch { }
            }
            return false;
        }

        private static Settlement ResolveSettlement(SettlementNameplatePartyMarkersVM vm)
        {
            foreach (var fname in new[] { "_settlement", "Settlement" })
            {
                var f = vm.GetType().GetField(fname, _flags);
                if (f != null && typeof(Settlement).IsAssignableFrom(f.FieldType))
                    return f.GetValue(vm) as Settlement;
            }
            var prop = vm.GetType().GetProperty("Settlement", _flags);
            if (prop != null && typeof(Settlement).IsAssignableFrom(prop.PropertyType))
                return prop.GetValue(vm) as Settlement;
            return null;
        }
    }
}
