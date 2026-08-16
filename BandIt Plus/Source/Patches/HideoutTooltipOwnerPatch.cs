using System;
using System.Collections;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using BandItPlus.HideoutVisit;

namespace BandItPlus.Patches
{
    // v157 (2026-05-16): DISPLAY-ONLY — fill the "Owner" row of a hideout's
    // campaign-map hover tooltip with the culture chief's name. Vanilla hideouts
    // have no owner hero, so the row renders blank (see in-game screenshot).
    //
    // Pattern referenced from EditableEncyclopedia's SettlementTooltipCulturePatch:
    // the map settlement tooltip is a PropertyBasedTooltipVM. Its base
    // TooltipBaseVM stores _invokedType / _invokedArgs, so the VM itself tells us
    // which Settlement it describes — no InformationManager.ShowTooltip capture
    // is needed (a refinement over EE's older dual-patch). We postfix
    // PropertyBasedTooltipVM.Refresh() and rewrite the Owner TooltipProperty's
    // ValueLabel after vanilla has rebuilt the list.
    //
    // DISPLAY ONLY — no Settlement.OwnerClan override (HideoutOwnerClanPatch
    // stays disabled), so vanilla raid-pathing / AI is untouched.
    //
    // Reflection-resolved (PropertyBasedTooltipVM's assembly isn't referenced by
    // BandItPlus.csproj), so registered via TryPatch() from SubModuleClassEntry,
    // not [HarmonyPatch] auto-discovery.
    public static class HideoutTooltipOwnerPatch
    {
        private static readonly BindingFlags F =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static FieldInfo _invokedArgsField;
        private static PropertyInfo _propertyListProp;
        private static PropertyInfo _defLabelProp;   // TooltipProperty.DefinitionLabel
        private static PropertyInfo _valueLabelProp; // TooltipProperty.ValueLabel
        private static bool _dumped;
        private static bool _firstApplyLogged;
        private static int _traceCount;

        private static void Trace(string msg)
        {
            if (_traceCount >= 12) return;
            _traceCount++;
            HideoutPeacefulVisitState.Log("v157 trace: " + msg);
        }

        // Called from SubModuleClassEntry.OnSubModuleLoad (mirrors EncyclopediaListPatcher).
        public static bool TryPatch(Harmony harmony)
        {
            try
            {
                Type vmType = AccessTools.TypeByName(
                    "TaleWorlds.Core.ViewModelCollection.Information.PropertyBasedTooltipVM");
                if (vmType == null)
                {
                    HideoutPeacefulVisitState.Log("v157 HideoutTooltipOwnerPatch: PropertyBasedTooltipVM not found");
                    return false;
                }

                var refresh = AccessTools.Method(vmType, "Refresh");
                if (refresh == null)
                {
                    HideoutPeacefulVisitState.Log("v157 HideoutTooltipOwnerPatch: Refresh() not found");
                    return false;
                }

                _propertyListProp = vmType.GetProperty("TooltipPropertyList", F);

                // _invokedArgs lives on the base TooltipBaseVM — walk the chain.
                for (Type bt = vmType.BaseType; bt != null && _invokedArgsField == null; bt = bt.BaseType)
                    _invokedArgsField = bt.GetField("_invokedArgs", F);

                var postfix = typeof(HideoutTooltipOwnerPatch).GetMethod(
                    nameof(RefreshPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(refresh, postfix: new HarmonyMethod(postfix));

                HideoutPeacefulVisitState.Log("v157 HideoutTooltipOwnerPatch: patched "
                    + vmType.Name + ".Refresh (propList=" + (_propertyListProp != null)
                    + " invokedArgs=" + (_invokedArgsField != null) + ")");
                return true;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v157 HideoutTooltipOwnerPatch.TryPatch fail: "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static void RefreshPostfix(object __instance)
        {
            try { Apply(__instance); }
            catch (Exception ex)
            {
                Trace("RefreshPostfix threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Apply(object vm)
        {
            if (vm == null || _propertyListProp == null || _invokedArgsField == null) return;

            // The VM carries its own subject in _invokedArgs — find the Settlement.
            var args = _invokedArgsField.GetValue(vm) as object[];
            if (args == null) return;
            Settlement settlement = null;
            foreach (var a in args)
                if (a is Settlement s) { settlement = s; break; }
            if (settlement == null || !settlement.IsHideout) return;

            var chief = Campaign.Current?
                .GetCampaignBehavior<BandItPlus.Heroes.BanditChiefRegistry>()?
                .GetChief(settlement.Culture?.StringId);
            if (chief == null)
            {
                Trace("no chief for hideout culture=" + (settlement.Culture?.StringId ?? "?"));
                return;
            }

            var list = _propertyListProp.GetValue(vm) as IEnumerable;
            if (list == null) return;

            // One-time structure dump so the exact row labels are known.
            if (!_dumped)
            {
                _dumped = true;
                var sb = new StringBuilder("v157 hideout tooltip rows: ");
                foreach (var item in list)
                {
                    EnsureItemProps(item);
                    string d = _defLabelProp?.GetValue(item)?.ToString() ?? "?";
                    string v = _valueLabelProp?.GetValue(item)?.ToString() ?? "?";
                    sb.Append('[').Append(d).Append("=\"").Append(v).Append("\"] ");
                }
                HideoutPeacefulVisitState.Log(sb.ToString());
            }

            // Rewrite the "Owner" row's value to the chief's name. Runs after
            // vanilla's Refresh(), so our value wins; re-asserts each refresh.
            string chiefName = chief.Name?.ToString() ?? "";
            if (chiefName.Length == 0) return;

            foreach (var item in list)
            {
                EnsureItemProps(item);
                if (_defLabelProp == null || _valueLabelProp == null) return;

                string d = _defLabelProp.GetValue(item)?.ToString() ?? "";
                if (d.IndexOf("Owner", StringComparison.OrdinalIgnoreCase) < 0) continue;

                string cur = _valueLabelProp.GetValue(item)?.ToString() ?? "";
                if (cur == chiefName) return; // already ours — vanilla left it alone
                _valueLabelProp.SetValue(item, chiefName);

                if (!_firstApplyLogged)
                {
                    _firstApplyLogged = true;
                    HideoutPeacefulVisitState.Log("v157 hideout Owner set to '" + chiefName
                        + "' (culture=" + settlement.Culture?.StringId + ")");
                }
                return;
            }

            Trace("no 'Owner' row in hideout tooltip (culture=" + settlement.Culture?.StringId + ")");
        }

        private static void EnsureItemProps(object item)
        {
            if (_defLabelProp != null || item == null) return;
            var t = item.GetType();
            _defLabelProp = t.GetProperty("DefinitionLabel", F);
            _valueLabelProp = t.GetProperty("ValueLabel", F);
        }
    }
}
