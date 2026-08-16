using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Localization;

namespace BandItPlus.HideoutVisit
{
    // v105 (2026-05-13): Phase 1 of "Hideouts feel like our chief's territory" —
    // rename bandit hideouts on the world map from generic "Hideout" to a chief-themed
    // name. Reads the BanditChiefRegistry chief Hero for each culture and formats:
    //   "{ChiefName}'s Hideout"   e.g., "Skarvac's Hideout"
    //
    // Falls back to "{CultureDisplayName} Hideout" when no chief is registered yet
    // (early-load timing) — so saves still load cleanly even before chiefs spawn.
    //
    // Fires on OnSessionLaunched so the rename re-applies every save load. No
    // persistence needed (rename is deterministic from registry state).
    public class HideoutRenameBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            // OnSessionLaunched fires for both new games AND loaded saves, so single hook suffices.
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        }

        public override void SyncData(IDataStore dataStore) { /* nothing to persist */ }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            ApplyHideoutNames("OnSessionLaunched");
            LogHideoutDiagnostic();
        }

        private static bool _diagLogged;
        // v106.2: one-time diagnostic logging hideout's faction/component structure
        // so we can identify the right field to override the map banner.
        private void LogHideoutDiagnostic()
        {
            if (_diagLogged) return;
            _diagLogged = true;
            try
            {
                Settlement sample = null;
                foreach (var s in Settlement.All)
                {
                    if (s != null && s.IsHideout && s.Culture?.StringId == "forest_bandits")
                    { sample = s; break; }
                }
                if (sample == null) return;

                var owner = sample.OwnerClan;
                var mapFac = sample.MapFaction;
                HideoutPeacefulVisitState.Log("v106.2 diag: hideout=" + sample.StringId
                    + " name=" + sample.Name + " owner=" + (owner?.Name?.ToString() ?? "null")
                    + " ownerIsBandit=" + (owner?.IsBanditFaction.ToString() ?? "null")
                    + " ownerIsMinor=" + (owner?.IsMinorFaction.ToString() ?? "null")
                    + " mapFac=" + (mapFac?.Name?.ToString() ?? "null")
                    + " mapFacBannerNull=" + (mapFac?.Banner == null));

                // Log SettlementComponent type
                var component = sample.SettlementComponent;
                HideoutPeacefulVisitState.Log("v106.2 component type=" + (component?.GetType()?.FullName ?? "null"));

                // Enumerate fields on the component for anything banner/faction related
                if (component != null)
                {
                    var fields = component.GetType().GetFields(
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.FlattenHierarchy);
                    var sb = new System.Text.StringBuilder();
                    foreach (var f in fields)
                    {
                        var n = f.FieldType.Name;
                        if (n.IndexOf("Banner", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || n.IndexOf("Faction", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || n.IndexOf("Clan", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            sb.Append(f.Name).Append(":").Append(n).Append("; ");
                        }
                    }
                    HideoutPeacefulVisitState.Log("v106.2 component faction/banner fields: " + sb);
                }

                // Same enumeration on Settlement itself
                var sfields = sample.GetType().GetFields(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                var sb2 = new System.Text.StringBuilder();
                foreach (var f in sfields)
                {
                    var n = f.FieldType.Name;
                    if (n.IndexOf("Banner", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("Faction", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        sb2.Append(f.Name).Append(":").Append(n).Append("; ");
                    }
                }
                HideoutPeacefulVisitState.Log("v106.2 settlement faction/banner fields: " + sb2);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v106.2 diag fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void ApplyHideoutNames(string trigger)
        {
            int renamed = 0;
            int skipped = 0;
            try
            {
                BandItPlus.Heroes.BanditChiefRegistry registry = null;
                try { registry = Campaign.Current?.GetCampaignBehavior<BandItPlus.Heroes.BanditChiefRegistry>(); }
                catch { }

                if (Settlement.All == null) return;
                foreach (var settlement in Settlement.All)
                {
                    if (settlement == null || !settlement.IsHideout) continue;
                    try
                    {
                        string cultureId = settlement.Culture?.StringId;
                        if (string.IsNullOrEmpty(cultureId)) { skipped++; continue; }

                        TextObject newName = null;
                        var chief = registry?.GetChief(cultureId);
                        if (chief != null && !string.IsNullOrEmpty(chief.FirstName?.ToString()))
                        {
                            // v105.2: strip only after " of " (the "titled-name" pattern)
                            // so "Skarvac of the High Pass" → "Skarvac" but "Granny Hod"
                            // stays intact as "Granny Hod's Hideout".
                            string raw = chief.FirstName.ToString();
                            int ofIdx = raw.IndexOf(" of ", System.StringComparison.OrdinalIgnoreCase);
                            string trimmed = ofIdx > 0 ? raw.Substring(0, ofIdx) : raw;
                            newName = new TextObject("{=bp_hideoutrename_001}{NAME}'s Hideout").SetTextVariable("NAME", trimmed);
                        }
                        else
                        {
                            // Fallback to authored ChiefName from CultureProfileRegistry
                            BandItPlus.Cultures.CultureProfile profile = null;
                            BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out profile);
                            if (profile != null && !string.IsNullOrEmpty(profile.ChiefName))
                            {
                                // v105.2: same " of " strip as primary path
                                int ofIdx = profile.ChiefName.IndexOf(" of ", System.StringComparison.OrdinalIgnoreCase);
                                string trimmedName = ofIdx > 0 ? profile.ChiefName.Substring(0, ofIdx) : profile.ChiefName;
                                newName = new TextObject("{=bp_hideoutrename_001}{NAME}'s Hideout").SetTextVariable("NAME", trimmedName);
                            }
                            else
                            {
                                string cultureDisplay = settlement.Culture?.Name?.ToString();
                                if (!string.IsNullOrEmpty(cultureDisplay))
                                    newName = new TextObject("{=bp_hideoutrename_002}{CULTURE} Hideout").SetTextVariable("CULTURE", cultureDisplay);
                            }
                        }

                        if (newName != null)
                        {
                            var nameField = settlement.GetType().GetField("_name",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                            if (nameField != null)
                            {
                                nameField.SetValue(settlement, newName);
                                renamed++;
                            }
                            else skipped++;
                        }
                        else skipped++;

                        // v106.1 hideout-OwnerClan write REMOVED 2026-05-18 —
                        // vestigial dead code. Vanilla rejects
                        // ChangeOwnerOfSettlementAction for hideouts (throws), and
                        // Settlement.OwnerClan has no backing field, so both the
                        // Action and the reflection-field strategies always failed
                        // — it just logged 8 errors per session and did nothing.
                        // Hideout ownership is display-only via the v157 tooltip
                        // patch (HideoutTooltipOwnerPatch); no settlement-data
                        // write is wanted. See reference_bannerlord_hideout_modding.
                    }
                    catch (Exception perEx)
                    {
                        skipped++;
                        HideoutPeacefulVisitState.Log("HideoutRename per-settlement fail: " + perEx.GetType().Name + ": " + perEx.Message);
                    }
                }
                HideoutPeacefulVisitState.Log("HideoutRename " + trigger + ": renamed=" + renamed + " skipped=" + skipped);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("HideoutRename outer fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
