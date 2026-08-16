using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.MountAndBlade.View.MissionViews;
using BandItPlus.Cultures;
using BandItPlus.Heroes;
using BandItPlus.HideoutVisit;

namespace BandItPlus.UI
{
    // v186 (2026-05-23): MissionView for the new unified BanditCampPanel.
    // Mounts a GauntletLayer when the player enters a peaceful bandit camp
    // (HideoutPeacefulVisitState.Active == true). Lifecycle pattern mirrors
    // BanditAiStatusMissionView (the WARBAND right-side battle panel).
    //
    // Phase 1 skeleton: real data scan added in Task 5, animations in Task 6.
    public class BanditCampPanelMissionView : MissionView
    {
        private GauntletLayer _layer;
        private BanditCampPanelVM _vm;
        private bool _layerAdded;
        private TaleWorlds.MountAndBlade.View.Screens.MissionScreen _missionScreen;

        private float _elapsed;
        private const float SlideDurationSec = 0.4f;
        private const float TargetTopMargin = 0f;
        private const float StartTopMargin = -220f;

        // v2.1 (2026-05-25): live-refresh timer. The HUD was previously populated
        // once on mount and never updated — quest text stayed "(none active)" after
        // accepting a quest, party gold didn't refresh after spending, trust dots
        // were frozen. Re-running PopulateVM every 1s keeps the HUD live.
        private float _refreshTimer;
        private const float RefreshIntervalSec = 1.0f;

        // v2.2 (2026-05-25): Esc/Options/Photo-Mode auto-hide — port of v1
        // CampInfoPanelMissionView's v104.2/v104.3/v104.5 pattern.
        //
        // Problem: when the player presses Escape (or opens Options / Photo Mode)
        // the engine pushes a new Screen on top of MissionScreen but our layer
        // is still drawing — the HUD sits on top of the menu and intercepts
        // clicks. The fix has three parts:
        //   v104.2 — hide on Esc press (one-shot, fires before the Esc menu mounts)
        //   v104.3 — show on resume (TopScreen returns to _missionScreen)
        //   v104.5 — input claim follows visibility: claim while shown (buttons
        //            need the mouse), release while hidden (so the Esc menu can
        //            capture the click on Resume / its own buttons).
        //
        // For Options / Photo Mode we also check TopScreen != _missionScreen each
        // frame — covers cases where the menu was opened via a non-Esc path.
        private bool _hiddenByEsc;
        private bool _inputCurrentlyClaimed;

        public override void OnMissionScreenInitialize()
        {
            base.OnMissionScreenInitialize();
            try
            {
                // Idempotency guard — if init fires twice (camera reinit, mod
                // conflict), don't orphan the first layer.
                if (_layerAdded) return;

                if (!HideoutPeacefulVisitState.Active)
                {
                    HideoutPeacefulVisitState.Log("v186 BanditCampPanel: not active, skipping mount");
                    return;
                }

                _vm = new BanditCampPanelVM();
                _vm.IsPanelVisible = true;
                PopulateVM(_vm);

                _layer = new GauntletLayer(name: "BanditCampPanel", localOrder: 300, shouldClear: false);
                _layer.LoadMovie("BanditCampPanel", _vm);

                // Attach to live MissionScreen via ScreenManager (same pattern as BanditAiStatusMissionView).
                var screen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen
                    as TaleWorlds.MountAndBlade.View.Screens.MissionScreen;
                if (screen != null)
                {
                    screen.AddLayer(_layer);
                    _missionScreen = screen;
                    _layerAdded = true;
                    // v2.1 (2026-05-25) BUTTON FIX: without these calls the layer
                    // receives no mouse events — services buttons never fire.
                    // Same pattern v1 CampInfoPanel/CampTopLeft used (proven to work).
                    // SetInputRestrictions(false) = "don't restrict, let mouse through to me".
                    // IsFocusLayer=false = don't steal keyboard from the mission scene.
                    try
                    {
                        _layer.InputRestrictions.SetInputRestrictions(false);
                        _layer.IsFocusLayer = false;
                        _inputCurrentlyClaimed = true;
                    }
                    catch (Exception ex)
                    {
                        HideoutPeacefulVisitState.Log("v2.1 layer-input wire FAIL: "
                            + ex.GetType().Name + ": " + ex.Message);
                    }
                    _vm.BackdropAlpha = 0f;
                    _vm.PanelTopMargin = StartTopMargin;
                    _vm.SectionGlowFactor = 1.0f;
                    _elapsed = 0f;
                }

                HideoutPeacefulVisitState.Log("v186 BanditCampPanel: layer mounted");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v186 BanditCampPanel mount FAIL: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Populates the VM with real data from the current peaceful camp visit.
        // Source-of-truth: HideoutPeacefulVisitState.TargetHideout (set by
        // WalkableHideoutEncounter when player enters).
        private static void PopulateVM(BanditCampPanelVM vm)
        {
            try
            {
                var settlement = HideoutPeacefulVisitState.TargetHideout
                                 ?? Settlement.CurrentSettlement;
                if (settlement == null)
                {
                    vm.CampName = BandItPlus.Localization.Get("bp_camppanelmissionview_001", "BANDIT CAMP");
                    vm.LoreLine = "";
                    vm.ChiefName = BandItPlus.Localization.Get("bp_camppanelmissionview_002", "Unknown Chief");
                    vm.ChiefTitle = "";
                    vm.CultureName = "";
                    vm.ChiefTrustDots = "○○○○○";
                    vm.FoodVendorTrustDots = "○○○○○";
                    vm.GearVendorTrustDots = "○○○○○";
                    vm.ChiefQuestText = BandItPlus.Localization.Get("bp_camppanelmissionview_003", "(none active)");
                    vm.FoodQuestText = BandItPlus.Localization.Get("bp_camppanelmissionview_003", "(none active)");
                    vm.GearQuestText = BandItPlus.Localization.Get("bp_camppanelmissionview_003", "(none active)");
                    vm.ServicesText = BandItPlus.Localization.Get("bp_camppanelmissionview_004", "Trade . Recruit . Rest . Drink");
                    vm.FoodContactStatus = BandItPlus.Localization.Get("bp_camppanelmissionview_005", "Unknown");
                    vm.GearContactStatus = BandItPlus.Localization.Get("bp_camppanelmissionview_005", "Unknown");
                    vm.CustomOrderStatus = BandItPlus.Localization.Get("bp_camppanelmissionview_006", "None pending");
                    vm.BannerCodeText = "";
                    vm.TimeOfDayLine = ComputeTimeOfDayLine();
                    vm.PartyGoldText   = "—";
                    vm.PartyTroopsText = "—";
                    vm.PartyFoodText   = "—";
                    vm.LastVisitLine   = BandItPlus.Localization.Get("bp_camppanelmissionview_007", "first visit");
                    vm.RumorsCount     = BandItPlus.Localization.Get("bp_camppanelmissionview_008", "none");
                    vm.BountyAmount    = "—";
                    vm.ContactsText    = BandItPlus.Localization.Get("bp_camppanelmissionview_009", "F— G—");  // 2026-05-26: no settlement → no contacts known
                    return;
                }

                var cultureId = settlement.Culture?.StringId ?? "";

                vm.CampName = (settlement.Name?.ToString() ?? BandItPlus.Localization.Get("bp_camppanelmissionview_010", "Bandit Camp")).ToUpperInvariant();
                vm.CultureName = (settlement.Culture?.Name?.ToString() ?? "").ToUpperInvariant();

                // Chief name/title/lore from CultureProfileRegistry (same source as CampInfoPanelMissionView).
                CultureProfile profile = null;
                if (!string.IsNullOrEmpty(cultureId))
                    CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out profile);

                vm.ChiefName = profile?.ChiefName ?? BandItPlus.Localization.Get("bp_camppanelmissionview_011", "the chief");
                vm.ChiefTitle = profile?.ChiefTitle ?? BandItPlus.Localization.Get("bp_camppanelmissionview_012", "Bandit Chief");
                vm.LoreLine = profile?.CampIntro ?? "";

                // Banner: chief clan → OwnerClan → MapFaction (same priority as CampInfoPanelMissionView).
                vm.BannerCodeText = ResolveHideoutBannerCode(settlement, cultureId);

                // Trust dots from BanditDialogManager (saveable, keyed by settlementId).
                var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                int chiefTrust = bdm?.GetCultureTrust(cultureId) ?? 0;
                int foodTrust  = bdm?.GetFoodVendorTrust(cultureId) ?? 0;
                int gearTrust  = bdm?.GetGearVendorTrust(cultureId) ?? 0;

                vm.ChiefTrustDots      = BuildTrustDots(chiefTrust);
                vm.FoodVendorTrustDots = BuildTrustDots(foodTrust);
                vm.GearVendorTrustDots = BuildTrustDots(gearTrust);

                // v2: rapport status word + dot color + filled count per row
                var chiefR = BuildRapportFromTrust(chiefTrust);
                vm.ChiefRapportLabel    = chiefR.label;
                vm.ChiefRapportDotColor = chiefR.color;
                vm.ChiefRapportFilled   = chiefR.filled;
                var foodR = BuildRapportFromTrust(foodTrust);
                vm.FoodVendorRapportLabel    = foodR.label;
                vm.FoodVendorRapportDotColor = foodR.color;
                vm.FoodVendorRapportFilled   = foodR.filled;
                var gearR = BuildRapportFromTrust(gearTrust);
                vm.GearVendorRapportLabel    = gearR.label;
                vm.GearVendorRapportDotColor = gearR.color;
                vm.GearVendorRapportFilled   = gearR.filled;
                // 2026-06-05 Wave 4.13: SLAVER rapport row — read slaver-vendor trust from
                // BanditDialogManager. For non-slaver cultures this returns 0 (stranger) which
                // is accurate (no slaver to build trust with).
                int slaverTrust = bdm?.GetSlaverTrust(cultureId) ?? 0;
                var slaverR = BuildRapportFromTrust(slaverTrust);
                vm.SlaverVendorRapportLabel    = slaverR.label;
                vm.SlaverVendorRapportDotColor = slaverR.color;
                vm.SlaverVendorRapportFilled   = slaverR.filled;

                // 2026-06-05: rumors count derived from the culture profile's authored
                // Rumors[] array length — gives players a count of unlocked rumor lines.
                // BountyAmount stays placeholder (no bounty system); VM property kept for
                // XML backward compat, displayed as "—".
                {
                    if (BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out var rumProf)
                        && rumProf?.Rumors != null)
                        vm.RumorsCount = rumProf.Rumors.Length.ToString();
                    else
                        vm.RumorsCount = "0";
                }
                vm.BountyAmount = "—";  // VM property kept; XML row replaced by CONTACTS

                // 2026-05-26: CONTACTS — Tier-3 vendor-introduction reach for THIS
                // camp's culture. Compact two-vendor status using BandIt Plus' dot
                // vocabulary (matches the rapport-dot ● ○ language elsewhere):
                //   ●  introduced + met (full reach at that settlement)
                //   ◯  introduced, not yet visited (half reach)
                //   —  not introduced (no contact yet)
                // Population logic short — the BanditDialogManager exposes everything
                // we need via GetContactSettlement / HasContactSettlementMet.
                {
                    char foodDot = '—';
                    char gearDot = '—';
                    if (bdm != null && !string.IsNullOrEmpty(cultureId))
                    {
                        if (!string.IsNullOrEmpty(bdm.GetContactSettlement(cultureId, "food")))
                            foodDot = bdm.HasContactSettlementMet(cultureId, "food") ? '●' : '◯';
                        if (!string.IsNullOrEmpty(bdm.GetContactSettlement(cultureId, "gear")))
                            gearDot = bdm.HasContactSettlementMet(cultureId, "gear") ? '●' : '◯';
                    }
                    vm.ContactsText = "F" + foodDot + " G" + gearDot;
                }

                // 2026-06-05 Wave 4.13: visit-tracking wired via BanditDialogManager._visitedHideouts.
                // Per-settlement timestamp still not tracked — binary "first/returning" is enough.
                vm.LastVisitLine = (bdm != null && bdm.HasVisitedHideoutById(settlement.StringId))
                    ? BandItPlus.Localization.Get("bp_camppanelmissionview_013", "returning visitor")
                    : BandItPlus.Localization.Get("bp_camppanelmissionview_007", "first visit");
                // 2026-06-05 Wave 4.13: PRISONERS — slaver-camp narrative indicator. Reads the
                // current settlement's culture (already cached as cultureId above) and shows
                // "12 in chains" for the slaver caravans culture, "—" otherwise.
                vm.PrisonersText = (cultureId == "bp_slaver_caravans")
                    ? BandItPlus.Localization.Get("bp_camppanelmissionview_014", "12 in chains")
                    : "—";

                // v2: party stats for the bottom flavor strip
                try
                {
                    long gold = Hero.MainHero?.Gold ?? 0;
                    vm.PartyGoldText = gold.ToString("N0") + "g";
                    var mainParty = MobileParty.MainParty;
                    int healthy = (mainParty?.MemberRoster?.TotalManCount ?? 0) - (mainParty?.MemberRoster?.TotalWounded ?? 0);
                    int total   = mainParty?.MemberRoster?.TotalManCount ?? 0;
                    vm.PartyTroopsText = healthy + "/" + total;
                    vm.PartyFoodText   = new TaleWorlds.Localization.TextObject("{=bp_camppanelmissionview_food}{COUNT} food").SetTextVariable("COUNT", CountPartyFood()).ToString();
                }
                catch (Exception ex)
                {
                    HideoutPeacefulVisitState.Log("v2 party stats fail: " + ex.Message);
                    vm.PartyGoldText   = "—";
                    vm.PartyTroopsText = "—";
                    vm.PartyFoodText   = "—";
                }

                // v2.1 (2026-05-25): query real quest data from BanditDialogManager.
                // Food/gear vendor quests use GetFoodVendorQuestTier / GetGearVendorQuestTier (tier > 0 = active).
                // Chief trust quests use GetActiveChiefQuestTier + GetActiveChiefQuestData (TrustQuest struct).
                // Falls back to idle placeholders when no quest is active.
                if (bdm != null)
                {
                    int foodQuestTier = bdm.GetFoodVendorQuestTier(cultureId);
                    string foodQ = foodQuestTier > 0
                        ? new TaleWorlds.Localization.TextObject("{=bp_hcc_002}Active food quest: Tier {TIER}").SetTextVariable("TIER", foodQuestTier).ToString()
                        : "";
                    vm.FoodQuestText = string.IsNullOrEmpty(foodQ)
                        ? BandItPlus.Localization.Get("bp_camppanelmissionview_015", "— vendor has no requests —")
                        : foodQ;
                    if (!string.IsNullOrEmpty(foodQ))
                        HideoutPeacefulVisitState.Log("v2.1 quest found (food @ " + cultureId + "): " + foodQ);

                    int gearQuestTier = bdm.GetGearVendorQuestTier(cultureId);
                    string gearQ = gearQuestTier > 0
                        ? new TaleWorlds.Localization.TextObject("{=bp_hcc_003}Active gear quest: Tier {TIER}").SetTextVariable("TIER", gearQuestTier).ToString()
                        : "";
                    vm.GearQuestText = string.IsNullOrEmpty(gearQ)
                        ? BandItPlus.Localization.Get("bp_camppanelmissionview_016", "— quartermaster idle —")
                        : gearQ;
                    if (!string.IsNullOrEmpty(gearQ))
                        HideoutPeacefulVisitState.Log("v2.1 quest found (gear @ " + cultureId + "): " + gearQ);

                    int chiefQuestTier = bdm.GetActiveChiefQuestTier(cultureId);
                    string chiefQ = FormatChiefQuestText(chiefQuestTier, bdm.GetActiveChiefQuestData(cultureId));
                    vm.ChiefQuestText = string.IsNullOrEmpty(chiefQ)
                        ? BandItPlus.Localization.Get("bp_camppanelmissionview_017", "— awaiting an audience —")
                        : chiefQ;
                    if (!string.IsNullOrEmpty(chiefQ))
                        HideoutPeacefulVisitState.Log("v2.1 quest found (chief @ " + cultureId + "): " + chiefQ);

                    // 2026-06-06 Wave 4.13.9 — SLAVER activity row, mirrors food/gear/chief.
                    int slaverQuestTier = bdm.GetActiveSlaverQuestTier(cultureId);
                    string slaverQ = slaverQuestTier > 0
                        ? new TaleWorlds.Localization.TextObject("{=bp_hcc_019}Active slaver task: Tier {TIER}").SetTextVariable("TIER", slaverQuestTier).ToString()
                        : "";
                    vm.SlaverActivityText = string.IsNullOrEmpty(slaverQ)
                        ? BandItPlus.Localization.Get("bp_camppanelmissionview_018", "— blocks silent —")
                        : slaverQ;
                    if (!string.IsNullOrEmpty(slaverQ))
                        HideoutPeacefulVisitState.Log("v2.1 quest found (slaver @ " + cultureId + "): " + slaverQ);
                }
                else
                {
                    vm.ChiefQuestText     = BandItPlus.Localization.Get("bp_camppanelmissionview_017", "— awaiting an audience —");
                    vm.FoodQuestText      = BandItPlus.Localization.Get("bp_camppanelmissionview_015", "— vendor has no requests —");
                    vm.GearQuestText      = BandItPlus.Localization.Get("bp_camppanelmissionview_016", "— quartermaster idle —");
                    vm.SlaverActivityText = BandItPlus.Localization.Get("bp_camppanelmissionview_018", "— blocks silent —");
                }

                vm.ServicesText       = BandItPlus.Localization.Get("bp_camppanelmissionview_004", "Trade . Recruit . Rest . Drink");
                // 2026-06-05: wire per-vendor contact reach + custom-order status from
                // BanditDialogManager state. Contacts: Introduced→Settlement-met progression.
                // Custom orders: pending vs ready (due-hours vs CampaignTime.Now.ToHours).
                if (bdm != null && !string.IsNullOrEmpty(cultureId))
                {
                    vm.FoodContactStatus = bdm.HasVendorContactsIntroduced(cultureId, "food")
                        ? (bdm.HasContactSettlementMet(cultureId, "food") ? BandItPlus.Localization.Get("bp_camppanelmissionview_019", "Met") : BandItPlus.Localization.Get("bp_camppanelmissionview_020", "Introduced"))
                        : BandItPlus.Localization.Get("bp_camppanelmissionview_005", "Unknown");
                    vm.GearContactStatus = bdm.HasVendorContactsIntroduced(cultureId, "gear")
                        ? (bdm.HasContactSettlementMet(cultureId, "gear") ? BandItPlus.Localization.Get("bp_camppanelmissionview_019", "Met") : BandItPlus.Localization.Get("bp_camppanelmissionview_020", "Introduced"))
                        : BandItPlus.Localization.Get("bp_camppanelmissionview_005", "Unknown");
                    bool foodPending = bdm.HasPendingCustomOrder(cultureId, "food");
                    bool gearPending = bdm.HasPendingCustomOrder(cultureId, "gear");
                    double nowHours = CampaignTime.Now.ToHours;
                    bool foodReady = foodPending && nowHours >= bdm.GetPendingCustomOrderDueHours(cultureId, "food");
                    bool gearReady = gearPending && nowHours >= bdm.GetPendingCustomOrderDueHours(cultureId, "gear");
                    vm.CustomOrderStatus = (foodReady || gearReady) ? BandItPlus.Localization.Get("bp_camppanelmissionview_021", "Ready!")
                        : (foodPending || gearPending) ? BandItPlus.Localization.Get("bp_camppanelmissionview_022", "In progress...")
                        : BandItPlus.Localization.Get("bp_camppanelmissionview_006", "None pending");
                }
                else
                {
                    vm.FoodContactStatus  = BandItPlus.Localization.Get("bp_camppanelmissionview_005", "Unknown");
                    vm.GearContactStatus  = BandItPlus.Localization.Get("bp_camppanelmissionview_005", "Unknown");
                    vm.CustomOrderStatus  = BandItPlus.Localization.Get("bp_camppanelmissionview_006", "None pending");
                }

                vm.TimeOfDayLine = ComputeTimeOfDayLine();
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v186 PopulateVM fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Banner resolution matching CampInfoPanelMissionView priority order.
        private static string ResolveHideoutBannerCode(Settlement settlement, string cultureId)
        {
            try
            {
                if (!string.IsNullOrEmpty(cultureId))
                {
                    var registry = Campaign.Current?.GetCampaignBehavior<BanditChiefRegistry>();
                    var chief = registry?.GetChief(cultureId);
                    var code = chief?.Clan?.Banner?.Serialize();
                    if (!string.IsNullOrEmpty(code)) return code;
                }
                var ownerCode = settlement?.OwnerClan?.Banner?.Serialize();
                if (!string.IsNullOrEmpty(ownerCode)) return ownerCode;
                var factionCode = settlement?.MapFaction?.Banner?.Serialize();
                if (!string.IsNullOrEmpty(factionCode)) return factionCode;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v186 BannerResolve fail: " + ex.Message);
            }
            return "";
        }

        // v2.1 (2026-05-25): mirrors CampInfoPanelMissionView.FormatChiefQuest.
        // Returns "" when no chief quest is active (tier <= 0 or TrustQuest null).
        private static string FormatChiefQuestText(int tier, BandItPlus.Cultures.TrustQuest q)
        {
            if (tier <= 0 || q == null) return "";
            string detail;
            if (q.IsGoldQuest) detail = q.GoldAmount + " denars";
            else if (!string.IsNullOrEmpty(q.ShortLabel)) detail = q.ShortLabel;
            else if (q.ItemIds != null && q.ItemIds.Length > 0)
            {
                int cnt = (q.Counts != null && q.Counts.Length > 0) ? q.Counts[0] : 1;
                detail = cnt + "× " + q.ItemIds[0];
            }
            else detail = BandItPlus.Localization.Get("bp_camppanelmissionview_023", "task in progress");
            return new TaleWorlds.Localization.TextObject("{=bp_hcc_016}Tier {TIER} Chief Task · {DETAIL}")
                .SetTextVariable("TIER", tier)
                .SetTextVariable("DETAIL", detail)
                .ToString();
        }

        // Maps a BanditDialogManager trust int (0-3) to a 5-dot string.
        // Trust scale: 0=stranger, 1=tolerated, 2=trusted, 3=kin.
        // Mapped to 5 dots: 0→0 filled, 1→2, 2→3, 3→5.
        private static string BuildTrustDots(int trust)
        {
            int filled = trust <= 0 ? 0
                       : trust == 1 ? 2
                       : trust == 2 ? 3
                       : 5;
            return new string('●', filled) + new string('○', 5 - filled);
        }

        // v2 (2026-05-24): maps the BanditDialogManager trust scale (0-3) to
        // a status word, status hex color, and a filled-dot count for the new
        // horizontal panel's Rapport cell.
        private static (string label, string color, int filled) BuildRapportFromTrust(int trust)
        {
            if (trust <= 0) return (BandItPlus.Localization.Get("bp_camppanelmissionview_024", "stranger"), "#888888FF", 0);
            if (trust == 1) return (BandItPlus.Localization.Get("bp_camppanelmissionview_025", "wary"),     "#C84848FF", 2);
            if (trust == 2) return (BandItPlus.Localization.Get("bp_camppanelmissionview_026", "known"),    "#E8B048FF", 3);
            return                     (BandItPlus.Localization.Get("bp_camppanelmissionview_027", "trusted"),  "#88C868FF", 5);
        }

        // v2: walks MainParty.ItemRoster summing food items for the bottom strip.
        private static int CountPartyFood()
        {
            try
            {
                var roster = MobileParty.MainParty?.ItemRoster;
                if (roster == null) return 0;
                int total = 0;
                for (int i = 0; i < roster.Count; i++)
                {
                    var item = roster.GetElementCopyAtIndex(i);
                    if (item.EquipmentElement.Item != null && item.EquipmentElement.Item.IsFood)
                        total += item.Amount;
                }
                return total;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v2 CountPartyFood fail: " + ex.Message);
                return 0;
            }
        }

        // Returns a time-of-day phrase keyed on CampaignTime.Now.
        private static string ComputeTimeOfDayLine()
        {
            try
            {
                var hour = (int)(CampaignTime.Now.ToHours % 24.0);
                string phase;
                if      (hour < 5)  phase = BandItPlus.Localization.Get("bp_camppanelmissionview_028", "the camp sleeps under starlight");
                else if (hour < 8)  phase = BandItPlus.Localization.Get("bp_camppanelmissionview_029", "dawn breaks over the camp");
                else if (hour < 12) phase = BandItPlus.Localization.Get("bp_camppanelmissionview_030", "morning settles on the camp");
                else if (hour < 16) phase = BandItPlus.Localization.Get("bp_camppanelmissionview_031", "the camp endures the long noon");
                else if (hour < 19) phase = BandItPlus.Localization.Get("bp_camppanelmissionview_032", "afternoon shadows lengthen");
                else if (hour < 22) phase = BandItPlus.Localization.Get("bp_camppanelmissionview_033", "dusk falls over the camp");
                else                phase = BandItPlus.Localization.Get("bp_camppanelmissionview_034", "the camp burns its night fires");
                return "- " + phase + " -";
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v186 ComputeTimeOfDayLine fail: "
                    + ex.GetType().Name + ": " + ex.Message);
                return BandItPlus.Localization.Get("bp_camppanelmissionview_035", "- the camp endures -");
            }
        }

        // v186 bugfix (2026-05-24): OnMissionScreenTick NEVER fires for
        // AddMissionBehavior'd MissionViews — panel stays at alpha=0 and
        // MarginLeft=-400 forever (invisible off-screen). Use OnMissionTick
        // instead (matches working BanditAiStatusMissionView pattern, which
        // pinned the same lesson at file:18-19).
        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            if (_vm == null || !_layerAdded) return;

            // v2.3 (2026-05-25): Esc / Options / Photo-Mode auto-hide — port of v1
            // CampInfoPanelMissionView's PROVEN v104.2/v104.3/v104.5 pattern.
            //
            // ROOT CAUSE (v2.2 failure, 2026-05-25 logs):
            //   Bannerlord's Esc menu, Options, and Photo Mode are GauntletLayers
            //   pushed onto the SAME MissionScreen — TopScreen never changes.
            //   v2.2 tried `TopScreen != _missionScreen` and the comparison was
            //   FALSE while the menu was up, so the auto-show branch fired 7ms
            //   after the hide and the HUD popped back up.
            //
            //   v1 CampInfoPanelMissionView's v102.2 diagnostic already proved
            //   layer-detection is impossible: "none of MissionScreen's layers set
            //   IsFocusLayer=true even with Esc menu open." (see file:212-214)
            //
            // PROVEN PATTERN (v1 ships this):
            //   v104.2  Esc one-shot → hide + set _hiddenByEsc = true
            //   v104.3  while _hiddenByEsc, poll WASD/Space/LMB/RMB. When the
            //           player is actively controlling their agent (i.e. the
            //           menu has been dismissed and they're back in the scene),
            //           show the HUD again.
            //   v104.5  input claim follows visibility — claim while shown
            //           (button clicks), release while hidden (so the Esc menu
            //           can capture clicks on Resume/Options/Photo Mode/Exit).
            try
            {
                bool escPressed = TaleWorlds.InputSystem.Input.IsKeyPressed(
                    TaleWorlds.InputSystem.InputKey.Escape);

                if (escPressed && _vm.IsPanelVisible)
                {
                    _vm.IsPanelVisible = false;
                    _hiddenByEsc = true;
                    HideoutPeacefulVisitState.Log("v2.3 Esc auto-hide fired");
                }

                if (_hiddenByEsc)
                {
                    // 2026-05-25: LMB/RMB removed from the poll — the player
                    // CLICKS to navigate the Esc menu (Resume / Options / Photo
                    // Mode / Exit), so mouse-button polling fired auto-show on
                    // top of the menu mid-transition. WASD/Space are pure agent-
                    // control signals: they only fire once the menu is dismissed
                    // and the player is actually moving in the scene.
                    bool playerActive =
                        TaleWorlds.InputSystem.Input.IsKeyDown(TaleWorlds.InputSystem.InputKey.W) ||
                        TaleWorlds.InputSystem.Input.IsKeyDown(TaleWorlds.InputSystem.InputKey.A) ||
                        TaleWorlds.InputSystem.Input.IsKeyDown(TaleWorlds.InputSystem.InputKey.S) ||
                        TaleWorlds.InputSystem.Input.IsKeyDown(TaleWorlds.InputSystem.InputKey.D) ||
                        TaleWorlds.InputSystem.Input.IsKeyDown(TaleWorlds.InputSystem.InputKey.Space);
                    if (playerActive)
                    {
                        _vm.IsPanelVisible = true;
                        _hiddenByEsc = false;
                        HideoutPeacefulVisitState.Log("v2.3 auto-show: player input detected");
                    }
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v2.3 Esc-hide block fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            bool shouldHide = _hiddenByEsc || HideoutPeacefulVisitState.CinematicActive;

            // v104.5 input claim follows visibility — claim while shown so
            // service buttons get clicks, release while hidden so the Esc menu
            // can capture input on its own buttons.
            if (_layer != null)
            {
                bool wantClaim = !shouldHide;
                if (wantClaim != _inputCurrentlyClaimed)
                {
                    try
                    {
                        _layer.InputRestrictions.SetInputRestrictions(!wantClaim);
                        _inputCurrentlyClaimed = wantClaim;
                        HideoutPeacefulVisitState.Log("v2.2 input claim → "
                            + (wantClaim ? "claimed" : "released"));
                    }
                    catch (Exception ex)
                    {
                        HideoutPeacefulVisitState.Log("v2.2 input claim toggle fail: "
                            + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            // v2 (2026-05-25): hide camp panel while HideoutCinemaIntro is up so
            // the cinematic narrative isn't covered. Reset _elapsed each frame
            // so the slide-in animation plays fresh once the cinematic dismisses.
            if (shouldHide)
            {
                _vm.IsPanelVisible = false;
                _elapsed = 0f;
                return;
            }
            _vm.IsPanelVisible = true;

            // v2.1: re-scan campaign data every 1s so HUD reflects accepted quests,
            // updated rapport, party gold/troops/food changes, time-of-day, etc.
            _refreshTimer += dt;
            if (_refreshTimer >= RefreshIntervalSec)
            {
                _refreshTimer = 0f;
                PopulateVM(_vm);
            }

            _elapsed += dt;

            // Slide-in + fade-in (concurrent, 0.4s ease-out cubic)
            float t = _elapsed / SlideDurationSec;
            if (t >= 1.0f)
            {
                _vm.BackdropAlpha = 1.0f;
                _vm.PanelTopMargin = TargetTopMargin;
            }
            else
            {
                _vm.BackdropAlpha = t;
                float easeOut = 1.0f - (1.0f - t) * (1.0f - t) * (1.0f - t);
                _vm.PanelTopMargin = StartTopMargin + (TargetTopMargin - StartTopMargin) * easeOut;
            }

            // Section-glow pulse (sine, 1.6s loop, range 0.9 to 1.0)
            float phase = (_elapsed % 1.6f) / 1.6f;
            float sine = (float)Math.Sin(phase * 2.0 * Math.PI);
            _vm.SectionGlowFactor = 0.9f + 0.1f * sine;
        }

        public override void OnMissionScreenFinalize()
        {
            try
            {
                if (_layerAdded && _layer != null && _missionScreen != null)
                {
                    _missionScreen.RemoveLayer(_layer);
                    _layer = null;
                    _vm = null;
                    _layerAdded = false;
                    HideoutPeacefulVisitState.Log("v186 BanditCampPanel: layer removed");
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v186 BanditCampPanel finalize FAIL: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            base.OnMissionScreenFinalize();
        }
    }
}
