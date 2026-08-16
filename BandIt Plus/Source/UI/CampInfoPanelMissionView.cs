using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.MountAndBlade.View.MissionViews;
using BandItPlus.HideoutVisit;

namespace BandItPlus.UI
{
    // Wave 4.12-fix v84 (2026-05-13): persistent left-side RPG info panel.
    // Reuses the v79 attach pattern (ScreenManager.TopScreen via OnMissionTick)
    // since MissionScreen property stays null when added via AddMissionBehavior.
    //
    // Unlike CampEntryBannerMissionView (transient 12s welcome), this view stays
    // visible for the whole scene. Refreshes VM each ~1s so trust/quest changes
    // mid-scene update live.
    public class CampInfoPanelMissionView : MissionView
    {
        private GauntletLayer _layer;
        private CampInfoPanelVM _vm;
        private bool _layerAddedToScreen;
        // v122 (2026-05-15): screen reference cached at layer-add time. Re-querying
        // ScreenManager.TopScreen at finalize fails — the engine pops the mission
        // screen before OnMissionScreenFinalize runs, so the cast returns null and
        // RemoveLayer is skipped, leaking the GauntletLayer + movie + VM.
        private TaleWorlds.MountAndBlade.View.Screens.MissionScreen _missionScreen;
        private float _tickAccum;
        // v91 (2026-05-13): cinematic backdrop alpha animation.
        // Fade-in over 0.8s on first attach, then ongoing subtle sine pulse
        // (0.89-0.97) for "breathing" candlelight feel.
        private float _elapsedSinceAttach;
        private bool _loggedAnimStart;
        private const float FadeInSeconds = 1.5f;        // v92: slowed for perceptibility
        private const float SlideInSeconds = 0.6f;       // v92: slide finishes before fade
        private const float SlideStartX = -260f;          // v92: starts off-screen left
        private const float SlideEndX = 20f;              // v92: settles at vanilla anchor
        private const float PulseMid = 0.92f;
        private const float PulseAmp = 0.08f;             // v92: 16% swing = visible candle-breath
        private const float PulseFreq = 2.0f;             // ~3.1s period

        private readonly string _cultureId;
        private readonly string _campName;
        private readonly string _cultureDisplayName;
        private readonly string _bannerCode;
        private readonly string _settlementId;  // v96: for Camp Memory persistence keyed by settlement

        public CampInfoPanelMissionView(Settlement settlement)
        {
            _campName = settlement?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_campinfopanelmissionview_001", "A Bandit Camp");
            _cultureId = settlement?.Culture?.StringId ?? "";
            _cultureDisplayName = settlement?.Culture?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_campinfopanelmissionview_002", "Bandits");
            _bannerCode = ResolveHideoutBannerCode(settlement, _cultureId);
            _settlementId = settlement?.StringId ?? "";
            // v96: Camp Memory — increment visit count + record first-visit time
            // (BanditDialogManager handles "first time" check internally).
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                bdm?.RecordHideoutVisit(_settlementId);
            }
            catch { }
        }

        // v87 (2026-05-13): hideout banner resolution. Bandit hideouts often have
        // null OwnerClan in vanilla, so we try three sources in priority order:
        //   1. BandIt Plus's synthesized chief Hero's clan banner (always exists if
        //      the chief has been registered for this culture — most reliable)
        //   2. Settlement.OwnerClan.Banner (works for some bandit hideouts)
        //   3. Settlement.MapFaction.Banner (last resort)
        // Logs which source resolved so we can verify in-game.
        private static string ResolveHideoutBannerCode(Settlement settlement, string cultureId)
        {
            try
            {
                // Source 1: BanditChiefRegistry chief Hero
                if (!string.IsNullOrEmpty(cultureId))
                {
                    var registry = TaleWorlds.CampaignSystem.Campaign.Current
                        ?.GetCampaignBehavior<BandItPlus.Heroes.BanditChiefRegistry>();
                    var chief = registry?.GetChief(cultureId);
                    var code = chief?.Clan?.Banner?.Serialize();
                    if (!string.IsNullOrEmpty(code))
                    {
                        HideoutPeacefulVisitState.Log("v87 BannerResolve: chief-clan source for " + cultureId);
                        return code;
                    }
                }
                // Source 2: Settlement.OwnerClan
                var ownerCode = settlement?.OwnerClan?.Banner?.Serialize();
                if (!string.IsNullOrEmpty(ownerCode))
                {
                    HideoutPeacefulVisitState.Log("v87 BannerResolve: OwnerClan source for " + cultureId);
                    return ownerCode;
                }
                // Source 3: MapFaction (covers minor-faction bandit clans)
                var faction = settlement?.MapFaction;
                var factionCode = faction?.Banner?.Serialize();
                if (!string.IsNullOrEmpty(factionCode))
                {
                    HideoutPeacefulVisitState.Log("v87 BannerResolve: MapFaction source for " + cultureId);
                    return factionCode;
                }
                HideoutPeacefulVisitState.Log("v87 BannerResolve: NO source found for " + cultureId);
                return "";
            }
            catch (System.Exception ex)
            {
                HideoutPeacefulVisitState.Log("v87 BannerResolve exception: " + ex.GetType().Name + ": " + ex.Message);
                return "";
            }
        }

        public override void OnMissionScreenInitialize()
        {
            base.OnMissionScreenInitialize();
            try
            {
                _vm = new CampInfoPanelVM();
                // v100: populate hover hints via reflection (HintViewModel namespace
                // varies by Bannerlord version — EE pattern from HeroTimelineSectionInjector).
                _vm.TalkChiefHint  = TryCreateHint(BandItPlus.Localization.Get("bp_campinfopanelmissionview_003", "Talk to the chief — leadership, trust, quest progression."));
                _vm.FoodVendorHint = TryCreateHint(BandItPlus.Localization.Get("bp_campinfopanelmissionview_004", "Visit the food vendor — provisions and supply runs."));
                _vm.GearVendorHint = TryCreateHint(BandItPlus.Localization.Get("bp_campinfopanelmissionview_005", "Visit the gear vendor — weapons, armor, smithing tasks."));
                _vm.LeaveHint      = TryCreateHint(BandItPlus.Localization.Get("bp_campinfopanelmissionview_006", "Leave camp — return to the world map."));
                // v149: per-section hover hints
                _vm.ChiefSectionHint   = TryCreateHint(BandItPlus.Localization.Get("bp_campinfopanelmissionview_007", "Chief trust. Talk to the chief and finish his tasks to rise from Stranger toward Sworn — higher trust unlocks better services."));
                _vm.VendorsSectionHint = TryCreateHint(BandItPlus.Localization.Get("bp_campinfopanelmissionview_008", "The camp's food and gear vendors. Trade with them and run their supply jobs to raise each vendor's trust."));
                _vm.QuestsSectionHint  = TryCreateHint(BandItPlus.Localization.Get("bp_campinfopanelmissionview_009", "Active jobs at this camp — food, gear and chief tasks plus any custom order. Completing them raises trust and unlocks rewards."));
                _vm.NetworkSectionHint = TryCreateHint(BandItPlus.Localization.Get("bp_campinfopanelmissionview_010", "Your standing in the camp's network — vendor contacts and the services they open once you have earned enough trust."));
                RefreshVM();
                _layer = new GauntletLayer(name: "CampInfoPanel", localOrder: 140, shouldClear: true);
                _layer.LoadMovie("CampInfoPanel", _vm);
                // v103 (2026-05-13): REMOVED SetInputRestrictions(false) — confirmed empirically
                // (v102.2 diagnostic) that it was the cause of Esc menu blocking clicks.
                // None of the mission-screen layers set IsFocusLayer=true even when Esc is
                // open, so we couldn't detect+release dynamically. Just leave the default
                // input handling — Gauntlet ButtonWidgets should still receive clicks on
                // their own widget area without an explicit layer-wide input claim.
                _layer.IsFocusLayer = false;
                HideoutPeacefulVisitState.Log("v84: CampInfoPanel prebuilt for " + _campName);
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v84 CampInfoPanel pre-init fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            if (!_layerAddedToScreen && _layer != null)
            {
                try
                {
                    var screen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen
                        as TaleWorlds.MountAndBlade.View.Screens.MissionScreen;
                    if (screen != null)
                    {
                        screen.AddLayer(_layer);
                        _missionScreen = screen;   // v122: cache for finalize
                        _layerAddedToScreen = true;
                        _elapsedSinceAttach = 0f;
                        HideoutPeacefulVisitState.Log("v84: CampInfoPanel layer attached for " + _campName);
                    }
                }
                catch (Exception ex)
                {
                    HideoutPeacefulVisitState.Log("v84 CampInfoPanel addlayer fail: "
                        + ex.GetType().Name + ": " + ex.Message);
                    _layerAddedToScreen = true;
                }
            }

            // v102.2 diagnostic: log screen state every 2s so we can see what happens at Esc
            if (_layerAddedToScreen && _layer != null)
            {
                _diagAccum += dt;
                if (_diagAccum >= 2.0f)
                {
                    _diagAccum = 0f;
                    try
                    {
                        var topScreen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen;
                        var topType = topScreen?.GetType()?.Name ?? "null";
                        int layerCount = -1;
                        string layerSummary = "";
                        if (topScreen is TaleWorlds.MountAndBlade.View.Screens.MissionScreen ms2)
                        {
                            layerCount = ms2.Layers.Count;
                            var sb = new System.Text.StringBuilder();
                            foreach (var lo in ms2.Layers)
                            {
                                var gl = lo as GauntletLayer;
                                if (gl != null)
                                    sb.Append("[GL/focus=").Append(gl.IsFocusLayer).Append("]");
                                else
                                    sb.Append("[").Append(lo?.GetType().Name ?? "null").Append("]");
                            }
                            layerSummary = sb.ToString();
                        }
                        HideoutPeacefulVisitState.Log("v102.2 diag: top=" + topType
                            + " layerCount=" + layerCount + " layers=" + layerSummary);
                    }
                    catch (System.Exception ex)
                    {
                        HideoutPeacefulVisitState.Log("v102.2 diag fail: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            // v103: v102 dynamic-toggle logic REMOVED. Diagnostic v102.2 proved that none
            // of MissionScreen's layers set IsFocusLayer=true even with Esc menu open,
            // so detection was impossible. Fix moved to root cause: no input claim at init.

            // v104.4: Y-key toggle REMOVED — auto-hide on Esc + auto-show on player
            // input now fully manage visibility without needing manual toggle (and
            // Y was conflicting with chat-log binding).
            if (_layerAddedToScreen && _vm != null)
            {
                try
                {
                    if (TaleWorlds.InputSystem.Input.IsKeyPressed(TaleWorlds.InputSystem.InputKey.Escape) && _vm.IsPanelVisible)
                    {
                        _vm.IsPanelVisible = false;
                        _hiddenByEsc = true;
                        HideoutPeacefulVisitState.Log("v104.2 Esc auto-hide fired");
                    }
                    // v104.3: auto-show on resume — detect when player returns to gameplay
                    // by polling movement keys (WASD) or mouse-look. If panel was hidden by
                    // Esc and player is actively controlling their agent, restore the panel.
                    if (_hiddenByEsc)
                    {
                        bool playerActive =
                            TaleWorlds.InputSystem.Input.IsKeyDown(TaleWorlds.InputSystem.InputKey.W) ||
                            TaleWorlds.InputSystem.Input.IsKeyDown(TaleWorlds.InputSystem.InputKey.A) ||
                            TaleWorlds.InputSystem.Input.IsKeyDown(TaleWorlds.InputSystem.InputKey.S) ||
                            TaleWorlds.InputSystem.Input.IsKeyDown(TaleWorlds.InputSystem.InputKey.D) ||
                            TaleWorlds.InputSystem.Input.IsKeyDown(TaleWorlds.InputSystem.InputKey.Space) ||
                            TaleWorlds.InputSystem.Input.IsKeyPressed(TaleWorlds.InputSystem.InputKey.LeftMouseButton) ||
                            TaleWorlds.InputSystem.Input.IsKeyPressed(TaleWorlds.InputSystem.InputKey.RightMouseButton);
                        if (playerActive)
                        {
                            _vm.IsPanelVisible = true;
                            _hiddenByEsc = false;
                            HideoutPeacefulVisitState.Log("v104.3 auto-show: player input detected");
                        }
                    }
                    // v104.5: input claim follows visibility — claim when panel visible
                    // (for button clicks), release when hidden (so Esc menu works).
                    if (_layer != null)
                    {
                        bool wantClaim = _vm.IsPanelVisible;
                        if (wantClaim != _hasInputClaim)
                        {
                            _hasInputClaim = wantClaim;
                            // SetInputRestrictions(false) = claim input on this layer
                            // SetInputRestrictions(true)  = release input
                            _layer.InputRestrictions.SetInputRestrictions(!wantClaim);
                            HideoutPeacefulVisitState.Log("v104.5 input claim: " + (wantClaim ? "CLAIMED (buttons active)" : "RELEASED (menu/hidden)"));
                        }
                    }
                }
                catch (Exception ex)
                {
                    HideoutPeacefulVisitState.Log("v122 input-claim block fail: "
                        + ex.GetType().Name + ": " + ex.Message);
                }
            }

            // v92: cinematic — slide-in from off-screen left + fade-in + ongoing pulse.
            if (_layerAddedToScreen && _vm != null)
            {
                _elapsedSinceAttach += dt;

                // Slide: ease-out cubic over SlideInSeconds, settles at SlideEndX
                float slideT = _elapsedSinceAttach >= SlideInSeconds
                    ? 1f
                    : _elapsedSinceAttach / SlideInSeconds;
                float slideEased = 1f - (1f - slideT) * (1f - slideT) * (1f - slideT);
                _vm.PanelLeftMargin = SlideStartX + slideEased * (SlideEndX - SlideStartX);

                // Fade-in alpha (linear) gated by full fade-in time
                float fadeIn = _elapsedSinceAttach >= FadeInSeconds
                    ? 1f
                    : _elapsedSinceAttach / FadeInSeconds;
                float pulse = PulseMid + PulseAmp * (float)System.Math.Sin(_elapsedSinceAttach * PulseFreq);
                _vm.BackdropAlpha = fadeIn * pulse;

                // v94: gentle section icon glow — sine-wave ColorFactor cycle 1.1..1.7
                // gives the gold-tinted section icons a subtle "alive" pulse without
                // being distracting.
                _vm.SectionGlowFactor = 1.4f + 0.3f * (float)System.Math.Sin(_elapsedSinceAttach * 1.2f);

                // Diagnostic: log first-tick values once to verify bindings fire
                if (!_loggedAnimStart && _elapsedSinceAttach > 0.05f)
                {
                    _loggedAnimStart = true;
                    HideoutPeacefulVisitState.Log(
                        "v92 anim t=" + _elapsedSinceAttach.ToString("F2") +
                        " alpha=" + _vm.BackdropAlpha.ToString("F2") +
                        " left=" + _vm.PanelLeftMargin.ToString("F1"));
                }
            }

            _tickAccum += dt;
            if (_tickAccum >= 1.0f)
            {
                _tickAccum = 0f;
                if (_vm != null) RefreshVM();
            }
        }

        public override void OnMissionScreenFinalize()
        {
            try
            {
                if (_layer != null)
                {
                    // v122: use the screen cached at add-time. Re-querying TopScreen
                    // here returns null (engine pops the mission screen before
                    // finalize), which would skip RemoveLayer and leak the layer.
                    var screen = _missionScreen
                        ?? (TaleWorlds.ScreenSystem.ScreenManager.TopScreen
                            as TaleWorlds.MountAndBlade.View.Screens.MissionScreen);
                    if (screen != null) screen.RemoveLayer(_layer);
                    _layer = null;
                }
                _missionScreen = null;
                _vm = null;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v84 CampInfoPanel finalize fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            base.OnMissionScreenFinalize();
        }

        private void RefreshVM()
        {
            try
            {
                var bdm = Campaign.Current?.GetCampaignBehavior<BandItPlus.BanditDialogManager>();
                if (bdm == null) return;

                // Section 1: identity
                BandItPlus.Cultures.CultureProfile chiefProfile = null;
                if (!string.IsNullOrEmpty(_cultureId))
                    BandItPlus.Cultures.CultureProfileRegistry.ByCultureId.TryGetValue(_cultureId, out chiefProfile);
                _vm.CampName = _campName;
                _vm.ChiefName = chiefProfile?.ChiefName ?? BandItPlus.Localization.Get("bp_campinfopanelmissionview_011", "the chief");
                _vm.ChiefTitle = chiefProfile?.ChiefTitle ?? BandItPlus.Localization.Get("bp_campinfopanelmissionview_012", "Bandit Chief");
                _vm.CultureName = _cultureDisplayName;
                // v151: chief face portrait — built once, when the culture id is valid.
                if (!_chiefPortraitTried)
                {
                    _chiefPortraitTried = true;
                    var chiefReg = Campaign.Current?.GetCampaignBehavior<BandItPlus.Heroes.BanditChiefRegistry>();
                    var chiefHero = chiefReg?.GetChief(_cultureId);
                    if (chiefHero != null) _vm.ChiefPortrait = TryCreateChiefPortrait(chiefHero);
                    _vm.HasChiefPortrait = _vm.ChiefPortrait != null;
                }
                // v94 (2026-05-13): lore + day/time cinematic context
                _vm.LoreLine = chiefProfile?.CampIntro ?? "";
                // v96: lore card body — same source, surfaced via clickable chief banner
                _vm.CampLoreCardBody = chiefProfile?.CampIntro ?? BandItPlus.Localization.Get("bp_campinfopanelmissionview_013", "No lore is yet known of this place.");
                _vm.TimeOfDayLine = ComputeTimeOfDayLine();
                // v95: player banner (next to chief banner)
                _vm.PlayerBannerCode = ResolvePlayerBannerCode();
                // v153 (code-review fix): removed the per-tick BackdropTintHex
                // write — the v146 sleek-HUD rebuild dropped the @BackdropTintHex
                // binding (explicit dark panel color now), so it was dead work.
                // v96: Camp Memory line — "5th visit · First met Spring 12, Year 1084 · 243 days ago"
                _vm.CampMemoryLine = FormatCampMemory(bdm);
                // v99 Next Step hint: moved below to read computed trust/quest vars (forward ref fix)

                // Section 2: chief trust — v95 narrative journey (Stranger → Tolerated → Trusted → Sworn)
                int chiefTrust = bdm.GetCultureTrust(_cultureId);
                _vm.ChiefTrustLabel = FormatTrustJourney(chiefTrust);

                // Section 3: food vendor
                var foodProfile = BandItPlus.Cultures.VendorProfiles.GetFood(_cultureId);
                int foodTrust = bdm.GetFoodVendorTrust(_cultureId);
                _vm.FoodVendorName = foodProfile?.Name ?? BandItPlus.Localization.Get("bp_campinfopanelmissionview_014", "Food Vendor");
                _vm.FoodVendorTrustLabel = BandItPlus.Localization.Get("bp_campinfopanelmissionview_015", "Trust");

                // Section 4: gear vendor
                var gearProfile = BandItPlus.Cultures.VendorProfiles.GetGear(_cultureId);
                int gearTrust = bdm.GetGearVendorTrust(_cultureId);
                _vm.GearVendorName = gearProfile?.Name ?? BandItPlus.Localization.Get("bp_campinfopanelmissionview_016", "Gear Vendor");
                _vm.GearVendorTrustLabel = BandItPlus.Localization.Get("bp_campinfopanelmissionview_017", "Trust");

                // v150: trust progress-bar fills (tier 0..3 -> 0..150px gold bar)
                _vm.ChiefTrustFillWidth = TrustFill(chiefTrust);
                _vm.FoodTrustFillWidth  = TrustFill(foodTrust);
                _vm.GearTrustFillWidth  = TrustFill(gearTrust);

                // Section 5: active vendor quest
                int foodQuestTier = bdm.GetFoodVendorQuestTier(_cultureId);
                int gearQuestTier = bdm.GetGearVendorQuestTier(_cultureId);
                _vm.ActiveFoodQuestText = foodQuestTier > 0
                    ? new TaleWorlds.Localization.TextObject("{=bp_hcc_002}Active food quest: Tier {TIER}").SetTextVariable("TIER", foodQuestTier).ToString()
                    : BandItPlus.Localization.Get("bp_campinfopanelmissionview_018", "No active food quest");
                _vm.ActiveGearQuestText = gearQuestTier > 0
                    ? new TaleWorlds.Localization.TextObject("{=bp_hcc_003}Active gear quest: Tier {TIER}").SetTextVariable("TIER", gearQuestTier).ToString()
                    : BandItPlus.Localization.Get("bp_campinfopanelmissionview_019", "No active gear quest");
                // v94.2: chief trust quest — was missing, made QUESTS section appear
                // empty even after accepting a chief task.
                // v95: enrich with item/gold details — "Quest Stakes"
                int chiefQuestTier = bdm.GetActiveChiefQuestTier(_cultureId);
                _vm.ActiveChiefQuestText = FormatChiefQuest(chiefQuestTier, bdm.GetActiveChiefQuestData(_cultureId));
                // v99: NextStepHint — placed here so all trust/quest vars exist
                _vm.NextStepHint = FormatNextStep(chiefTrust, chiefQuestTier, foodTrust, gearTrust);

                // v152: CAMP RECORD — derived relationship summary (no save data;
                // a "where you stand" record from values already computed above).
                string[] standNames = {
                    BandItPlus.Localization.Get("bp_hcc_004", "a stranger"),
                    BandItPlus.Localization.Get("bp_hcc_005", "tolerated"),
                    BandItPlus.Localization.Get("bp_hcc_006", "trusted"),
                    BandItPlus.Localization.Get("bp_hcc_007", "kin at the fire") };
                int ctRec = chiefTrust < 0 ? 0 : (chiefTrust > 3 ? 3 : chiefTrust);
                _vm.CampRecordLine1 = new TaleWorlds.Localization.TextObject("{=bp_hcc_008}The chief counts you {STANDING}.").SetTextVariable("STANDING", standNames[ctRec]).ToString();
                int vendorStanding = (foodTrust < 0 ? 0 : foodTrust) + (gearTrust < 0 ? 0 : gearTrust);
                _vm.CampRecordLine2 = vendorStanding > 0
                    ? BandItPlus.Localization.Get("bp_campinfopanelmissionview_020", "You have earned a name with the camp's vendors.")
                    : BandItPlus.Localization.Get("bp_campinfopanelmissionview_021", "The vendors do not yet know your face.");
                int activeTasks = (foodQuestTier > 0 ? 1 : 0) + (gearQuestTier > 0 ? 1 : 0) + (chiefQuestTier > 0 ? 1 : 0);
                _vm.CampRecordLine3 = activeTasks > 0
                    ? (activeTasks == 1
                        ? new TaleWorlds.Localization.TextObject("{=bp_hcc_009}{N} task owed to this camp.").SetTextVariable("N", activeTasks).ToString()
                        : new TaleWorlds.Localization.TextObject("{=bp_hcc_010}{N} tasks owed to this camp.").SetTextVariable("N", activeTasks).ToString())
                    : BandItPlus.Localization.Get("bp_campinfopanelmissionview_022", "No tasks are owed to this camp.");

                // Section 6: contact intro status
                bool foodIntro = bdm.HasVendorContactsIntroduced(_cultureId, "food");
                bool gearIntro = bdm.HasVendorContactsIntroduced(_cultureId, "gear");
                _vm.FoodContactStatus = foodIntro
                    ? new TaleWorlds.Localization.TextObject("{=bp_hcc_011}Food contact: {SETTLEMENT}").SetTextVariable("SETTLEMENT", FormatSettlement(bdm.GetContactSettlement(_cultureId, "food"))).ToString()
                    : BandItPlus.Localization.Get("bp_campinfopanelmissionview_023", "Food contact: not introduced");
                _vm.GearContactStatus = gearIntro
                    ? new TaleWorlds.Localization.TextObject("{=bp_hcc_012}Gear contact: {SETTLEMENT}").SetTextVariable("SETTLEMENT", FormatSettlement(bdm.GetContactSettlement(_cultureId, "gear"))).ToString()
                    : BandItPlus.Localization.Get("bp_campinfopanelmissionview_024", "Gear contact: not introduced");

                // Section 7: pending Custom Order (food OR gear — show first active)
                string customOrder = BandItPlus.Localization.Get("bp_campinfopanelmissionview_025", "No pending custom order");
                try
                {
                    if (bdm.HasPendingCustomOrder(_cultureId, "food"))
                    {
                        var item = bdm.GetPendingCustomOrderItemId(_cultureId, "food");
                        int qty = bdm.GetPendingCustomOrderQuantity(_cultureId, "food");
                        double due = bdm.GetPendingCustomOrderDueHours(_cultureId, "food");
                        int daysLeft = System.Math.Max(1, (int)System.Math.Ceiling((due - CampaignTime.Now.ToHours) / 24.0));
                        customOrder = new TaleWorlds.Localization.TextObject("{=bp_hcc_013}Order (food): {QTY}× {ITEM} in {DAYS}d")
                            .SetTextVariable("QTY", qty)
                            .SetTextVariable("ITEM", item)
                            .SetTextVariable("DAYS", daysLeft)
                            .ToString();
                    }
                    else if (bdm.HasPendingCustomOrder(_cultureId, "gear"))
                    {
                        var item = bdm.GetPendingCustomOrderItemId(_cultureId, "gear");
                        int qty = bdm.GetPendingCustomOrderQuantity(_cultureId, "gear");
                        double due = bdm.GetPendingCustomOrderDueHours(_cultureId, "gear");
                        int daysLeft = System.Math.Max(1, (int)System.Math.Ceiling((due - CampaignTime.Now.ToHours) / 24.0));
                        customOrder = new TaleWorlds.Localization.TextObject("{=bp_hcc_014}Order (gear): {QTY}× {ITEM} in {DAYS}d")
                            .SetTextVariable("QTY", qty)
                            .SetTextVariable("ITEM", item)
                            .SetTextVariable("DAYS", daysLeft)
                            .ToString();
                    }
                }
                catch (Exception ex)
                {
                    HideoutPeacefulVisitState.Log("v122 custom-order read fail: "
                        + ex.GetType().Name + ": " + ex.Message);
                }
                _vm.CustomOrderStatus = customOrder;

                // Section 9: services available — gated by trust tiers
                var services = new System.Collections.Generic.List<string>();
                if (chiefTrust >= 1) services.Add(BandItPlus.Localization.Get("bp_campinfopanelmissionview_026", "Recruit"));
                if (foodTrust >= 1) services.Add(BandItPlus.Localization.Get("bp_campinfopanelmissionview_027", "Doctor"));
                if (chiefTrust >= 2) services.Add(BandItPlus.Localization.Get("bp_campinfopanelmissionview_028", "Trainer"));
                if (chiefTrust >= 3) services.Add(BandItPlus.Localization.Get("bp_campinfopanelmissionview_029", "Slaver"));
                _vm.ServicesText = services.Count > 0
                    ? new TaleWorlds.Localization.TextObject("{=bp_hcc_015}Services: {LIST}").SetTextVariable("LIST", string.Join(" · ", services)).ToString()
                    : BandItPlus.Localization.Get("bp_campinfopanelmissionview_030", "Services: none yet — earn trust");

                // Section 10: banner sprite
                _vm.BannerCodeText = _bannerCode;
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v84 CampInfoPanel refresh fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // v95: Trust narrative journey — same data, narrative framing.
        // Stranger → Tolerated → Trusted → Sworn. Current tier in [brackets],
        // followed by pips, followed by next-milestone hint.
        // Example T1: "[Tolerated]   ◆ ◇ ◇   →  Trusted"
        private static string FormatTrustJourney(int tier)
        {
            string[] labels = {
                BandItPlus.Localization.Get("bp_campinfopanelmissionview_031", "Stranger"),
                BandItPlus.Localization.Get("bp_campinfopanelmissionview_032", "Tolerated"),
                BandItPlus.Localization.Get("bp_campinfopanelmissionview_033", "Trusted"),
                BandItPlus.Localization.Get("bp_campinfopanelmissionview_034", "Sworn") };
            int idx = tier < 0 ? 0 : tier > 3 ? 3 : tier;
            // v150: pips + next-milestone dropped — trust is now a gold bar.
            return "[" + labels[idx] + "]";
        }

        // v150: trust tier (0..3) -> gold-bar fill width in pixels (track is 150px).
        private static float TrustFill(int tier)
        {
            int t = tier < 0 ? 0 : (tier > 3 ? 3 : tier);
            return t / 3f * 150f;
        }

        // v95: Chief quest stakes — enrich the tier line with item/gold details
        // pulled from the TrustQuest data already authored per culture.
        private static string FormatChiefQuest(int tier, BandItPlus.Cultures.TrustQuest q)
        {
            if (tier <= 0 || q == null) return BandItPlus.Localization.Get("bp_campinfopanelmissionview_035", "No active chief task");
            string detail;
            if (q.IsGoldQuest) detail = q.GoldAmount + " denars";
            else if (!string.IsNullOrEmpty(q.ShortLabel)) detail = q.ShortLabel;
            else if (q.ItemIds != null && q.ItemIds.Length > 0)
            {
                int cnt = (q.Counts != null && q.Counts.Length > 0) ? q.Counts[0] : 1;
                detail = cnt + "× " + q.ItemIds[0];
            }
            else detail = BandItPlus.Localization.Get("bp_campinfopanelmissionview_036", "task in progress");
            return new TaleWorlds.Localization.TextObject("{=bp_hcc_016}Tier {TIER} Chief Task · {DETAIL}")
                .SetTextVariable("TIER", tier)
                .SetTextVariable("DETAIL", detail)
                .ToString();
        }

        // v95: player's clan banner serialized for the second BannerTableauWidget.
        // Empty string => widget renders nothing (player without clan).
        private static string ResolvePlayerBannerCode()
        {
            try
            {
                return TaleWorlds.CampaignSystem.Hero.MainHero?.Clan?.Banner?.Serialize() ?? "";
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v122 ResolvePlayerBannerCode fail: "
                    + ex.GetType().Name + ": " + ex.Message);
                return "";
            }
        }

        // v100: reflection helper — finds HintViewModel type at runtime (namespace
        // varies between Bannerlord versions), creates instance via its TextObject ctor.
        // Returns null if type can't be found; HintWidget gracefully renders nothing.
        // Pattern copied from EditableEncyclopedia\Timeline\HeroTimelineSectionInjector.cs:1843.
        // v122: cache the resolved HintViewModel type — previously re-scanned
        // every loaded assembly on each of the 4 TryCreateHint calls per init.
        private static System.Type _cachedHintVmType;
        private static bool _hintVmTypeResolved;
        private static bool _ctorsLogged;
        // v102 (2026-05-13): track whether an Esc-menu / focus layer is above us
        // so we can release our input claim and let the menu receive clicks.
        private bool _wasFocusLayerAbove;
        // v102.2: continuous diagnostic — logs screen state every 2s so we can see
        // what changes when Esc is pressed.
        private float _diagAccum;
        // v104: pause detection via Mission.Time advancement
        private float _lastMissionTime = -1f;
        private float _stillTime;
        // v104.3: tracks whether panel was hidden by Esc (so we know to auto-show on resume)
        private bool _hiddenByEsc;
        // v104.5: tracks current input claim state — toggled with visibility
        private bool _hasInputClaim;
        // v151: chief portrait is built once (first RefreshVM with a valid culture id)
        private bool _chiefPortraitTried;
        private static object TryCreateHint(string text)
        {
            try
            {
                // v122: resolve the HintViewModel type once, then reuse the cache.
                if (!_hintVmTypeResolved)
                {
                    _hintVmTypeResolved = true;
                    string foundIn = null;
                    foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var t1 = asm.GetType("TaleWorlds.Library.HintViewModel");
                        if (t1 != null) { _cachedHintVmType = t1; foundIn = asm.GetName().Name + ".TaleWorlds.Library.HintViewModel"; break; }
                        var t2 = asm.GetType("TaleWorlds.Core.ViewModelCollection.HintViewModel");
                        if (t2 != null) { _cachedHintVmType = t2; foundIn = asm.GetName().Name + ".TaleWorlds.Core.ViewModelCollection.HintViewModel"; break; }
                        var t3 = asm.GetType("TaleWorlds.Core.ViewModelCollection.Information.HintViewModel");
                        if (t3 != null) { _cachedHintVmType = t3; foundIn = asm.GetName().Name + ".TaleWorlds.Core.ViewModelCollection.Information.HintViewModel"; break; }
                        var t4 = asm.GetType("TaleWorlds.GauntletUI.Data.HintViewModel");
                        if (t4 != null) { _cachedHintVmType = t4; foundIn = asm.GetName().Name + ".TaleWorlds.GauntletUI.Data.HintViewModel"; break; }
                    }
                    HideoutPeacefulVisitState.Log("v100 TryCreateHint diag: hintVmType=" + (foundIn ?? "NOT_FOUND"));
                }
                System.Type hintVmType = _cachedHintVmType;
                if (hintVmType == null) return null;
                var textObjType = typeof(TaleWorlds.Localization.TextObject);
                var textObj = System.Activator.CreateInstance(textObjType, new object[] { text, null });

                // v100.1: enumerate ALL public ctors to find the right signature.
                // Log them once for diagnosis.
                if (!_ctorsLogged)
                {
                    _ctorsLogged = true;
                    foreach (var c in hintVmType.GetConstructors())
                    {
                        var pars = c.GetParameters();
                        string sig = string.Join(",", System.Linq.Enumerable.Select(pars, p => p.ParameterType.Name));
                        HideoutPeacefulVisitState.Log("v100.1 HintViewModel ctor: (" + sig + ")");
                    }
                }

                // v100.2: Bannerlord 1.3.x HintViewModel has only these two ctors:
                //   HintViewModel()  AND  HintViewModel(TextObject, String)
                // Prefer the 2-arg ctor (full init); fall back to () + property set.

                // 1. (TextObject, String) — pass hint text + empty hotkey string
                var ctor2 = hintVmType.GetConstructor(new[] { textObjType, typeof(string) });
                if (ctor2 != null) return ctor2.Invoke(new object[] { textObj, "" });

                // 2. () + set HintText property (try both TextObject and string types)
                var ctor0 = hintVmType.GetConstructor(System.Type.EmptyTypes);
                if (ctor0 != null)
                {
                    var hint = ctor0.Invoke(null);
                    var hintTextProp = hintVmType.GetProperty("HintText");
                    if (hintTextProp != null)
                    {
                        if (hintTextProp.PropertyType == textObjType)
                            hintTextProp.SetValue(hint, textObj);
                        else
                            hintTextProp.SetValue(hint, text);
                    }
                    return hint;
                }

                HideoutPeacefulVisitState.Log("v100.2 no usable ctor found on " + hintVmType.FullName);
                return null;
            }
            catch (System.Exception ex)
            {
                HideoutPeacefulVisitState.Log("v100 TryCreateHint exception: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        // v151: build a CharacterImageIdentifierVM for the chief's face portrait.
        // Namespace-resilient reflection (EE's proven portrait recipe). Returns the
        // VM as object; the prefab's ImageIdentifierWidget binds it as DataSource.
        // Null on failure — the widget then renders nothing.
        private static object TryCreateChiefPortrait(TaleWorlds.CampaignSystem.Hero hero)
        {
            try
            {
                if (hero?.CharacterObject == null) return null;
                System.Type charCodeType = null, charImgIdVMType = null, charImgIdType = null;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    charCodeType    = charCodeType    ?? asm.GetType("TaleWorlds.Core.CharacterCode");
                    charImgIdVMType = charImgIdVMType ?? asm.GetType("TaleWorlds.Core.ViewModelCollection.ImageIdentifiers.CharacterImageIdentifierVM");
                    charImgIdType   = charImgIdType   ?? asm.GetType("TaleWorlds.Core.ImageIdentifiers.CharacterImageIdentifier");
                }
                if (charCodeType == null || charImgIdVMType == null)
                {
                    HideoutPeacefulVisitState.Log("v151 portrait: types not found (CharCode="
                        + (charCodeType != null) + " VM=" + (charImgIdVMType != null) + ")");
                    return null;
                }
                var heroCharType = hero.CharacterObject.GetType();
                System.Reflection.MethodInfo createFrom = null;
                foreach (var m in charCodeType.GetMethods(System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Public))
                {
                    if (m.Name != "CreateFrom") continue;
                    var p = m.GetParameters();
                    if (p.Length == 1 && p[0].ParameterType.IsAssignableFrom(heroCharType)) { createFrom = m; break; }
                }
                if (createFrom == null) { HideoutPeacefulVisitState.Log("v151 portrait: CreateFrom not found"); return null; }
                var charCode = createFrom.Invoke(null, new object[] { hero.CharacterObject });
                if (charCode == null) return null;
                foreach (var c in charImgIdVMType.GetConstructors())
                {
                    var cp = c.GetParameters();
                    if (cp.Length == 1 && cp[0].ParameterType.IsAssignableFrom(charCode.GetType()))
                        return c.Invoke(new object[] { charCode });
                }
                if (charImgIdType != null)
                {
                    object charImgId = null;
                    foreach (var c in charImgIdType.GetConstructors())
                    {
                        var cp = c.GetParameters();
                        if (cp.Length == 1 && cp[0].ParameterType.IsAssignableFrom(charCode.GetType()))
                        { charImgId = c.Invoke(new object[] { charCode }); break; }
                    }
                    if (charImgId != null)
                        foreach (var c in charImgIdVMType.GetConstructors())
                        {
                            var cp = c.GetParameters();
                            if (cp.Length == 1 && cp[0].ParameterType.IsAssignableFrom(charImgIdType))
                                return c.Invoke(new object[] { charImgId });
                        }
                }
                HideoutPeacefulVisitState.Log("v151 portrait: no usable CharacterImageIdentifierVM ctor");
                return null;
            }
            catch (System.Exception ex)
            {
                HideoutPeacefulVisitState.Log("v151 portrait fail: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        // v99: smart "next step" hint — reads trust/quest state and suggests the
        // highest-leverage next action for the player to take at this camp.
        // Priority order:
        //   1. Active chief quest? → "Complete: Tier N task"
        //   2. Chief trust 0? → "Talk to the chief to earn tolerance"
        //   3. Chief trust 1-2, no quest? → "Ask the chief for another task"
        //   4. Food/gear vendors locked? → "Earn vendor trust by completing a task"
        //   5. Chief Sworn → "You are kin at this fire — services unlocked"
        private static string FormatNextStep(int chiefTrust, int chiefQuestTier, int foodTrust, int gearTrust)
        {
            try
            {
                if (chiefQuestTier > 0) return BandItPlus.Localization.Get("bp_campinfopanelmissionview_037", "→ Complete the active chief task");
                if (chiefTrust == 0)     return BandItPlus.Localization.Get("bp_campinfopanelmissionview_038", "→ Talk to the chief to earn tolerance");
                if (chiefTrust == 3)     return BandItPlus.Localization.Get("bp_campinfopanelmissionview_039", "★ Sworn at this fire — all services unlocked");
                if (foodTrust == 0 && gearTrust == 0) return BandItPlus.Localization.Get("bp_campinfopanelmissionview_040", "→ Visit a vendor to unlock trade");
                return BandItPlus.Localization.Get("bp_campinfopanelmissionview_041", "→ Ask the chief for another task");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v122 FormatNextStep fail: "
                    + ex.GetType().Name + ": " + ex.Message);
                return "";
            }
        }

        // v96: Camp Memory line — visit count + first-met date + days-ago.
        // Reads from BanditDialogManager (Saveable, keyed by settlementId).
        // First visit: "1st visit · You have just arrived"
        // Returning: "5th visit · First met Spring 12, Year 1084 · 243 days ago"
        private string FormatCampMemory(BandItPlus.BanditDialogManager bdm)
        {
            try
            {
                if (bdm == null || string.IsNullOrEmpty(_settlementId)) return "";
                int visitCount = bdm.GetCampVisitCount(_settlementId);
                if (visitCount <= 1) return BandItPlus.Localization.Get("bp_campinfopanelmissionview_042", "First visit · You have just arrived");

                string visitNum = visitCount + Ordinal(visitCount);
                double firstHours = bdm.GetCampFirstVisitHours(_settlementId);
                if (firstHours <= 0) return new TaleWorlds.Localization.TextObject("{=bp_hcc_017}{VISITNUM} visit").SetTextVariable("VISITNUM", visitNum).ToString();

                var firstTime = CampaignTime.Hours((float)firstHours);
                int firstYear = firstTime.GetYear;
                string firstSeason = SeasonName((int)firstTime.GetSeasonOfYear);
                int firstDayOfSeason = (int)firstTime.GetDayOfSeason + 1;
                int daysAgo = (int)(CampaignTime.Now.ToHours - firstHours) / 24;

                return new TaleWorlds.Localization.TextObject("{=bp_hcc_018}{VISITNUM} visit · First met {SEASON} {DAY}, Year {YEAR} · {DAYSAGO} days ago")
                    .SetTextVariable("VISITNUM", visitNum)
                    .SetTextVariable("SEASON", firstSeason)
                    .SetTextVariable("DAY", firstDayOfSeason)
                    .SetTextVariable("YEAR", firstYear)
                    .SetTextVariable("DAYSAGO", daysAgo)
                    .ToString();
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v122 FormatCampMemory fail: "
                    + ex.GetType().Name + ": " + ex.Message);
                return "";
            }
        }

        private static string Ordinal(int n)
        {
            int mod100 = n % 100;
            if (mod100 >= 11 && mod100 <= 13) return "th";
            switch (n % 10) { case 1: return "st"; case 2: return "nd"; case 3: return "rd"; default: return "th"; }
        }

        private static string SeasonName(int season)
        {
            return season == 0 ? BandItPlus.Localization.Get("bp_campinfopanelmissionview_043", "Spring")
                 : season == 1 ? BandItPlus.Localization.Get("bp_campinfopanelmissionview_044", "Summer")
                 : season == 2 ? BandItPlus.Localization.Get("bp_campinfopanelmissionview_045", "Autumn")
                 : BandItPlus.Localization.Get("bp_campinfopanelmissionview_046", "Winter");
        }

        // v95: time-of-day tint hex — applied to backdrop Color via @-binding.
        // Dawn warm gold · Day neutral · Dusk red-orange · Night cool blue.
        // Subtle (alpha 0xFF kept; only RGB shifts) so text stays legible.
        private static string ComputeTimeOfDayTint()
        {
            try
            {
                var now = CampaignTime.Now;
                float hour = (float)(now.ToHours - System.Math.Floor(now.ToHours / 24.0) * 24.0);
                if (hour < 5f)  return "#A8B4CCFF";   // deep night — cool blue-gray
                if (hour < 8f)  return "#FFD8A8FF";   // dawn — warm gold
                if (hour < 17f) return "#FFFFFFFF";   // day — neutral
                if (hour < 20f) return "#FFB890FF";   // dusk — red-orange
                return "#B0BACCFF";                   // night — cool blue
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("v122 ComputeTimeOfDayTint fail: "
                    + ex.GetType().Name + ": " + ex.Message);
                return "#FFFFFFFF";
            }
        }

        // v90 (2026-05-13): visual pip rendering for trust tiers.
        // ◆ (U+25C6) = filled, ◇ (U+25C7) = empty.
        // Bannerlord's default UI font supports both glyphs (already used for
        // popup divider markers per memory feedback_bannerlord_inquiry_popup_pattern).
        private static string PipString(int filled, int max)
        {
            var sb = new System.Text.StringBuilder(max * 2);
            for (int i = 0; i < max; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(i < filled ? '◆' : '◇');
            }
            return sb.ToString();
        }

        // v94.2 (2026-05-13): compute in-world date + time-of-day flavor.
        // Uses GetYear/GetSeasonOfYear (proven working in EE) instead of the broken
        // CampaignStartTime path that produced "Day 26022".
        // Output: "Spring 15 · Year 1086 · Dusk falls over the camp"
        private static string ComputeTimeOfDayLine()
        {
            try
            {
                var now = CampaignTime.Now;
                int year = now.GetYear;
                int season = (int)now.GetSeasonOfYear;
                string seasonName = season == 0 ? BandItPlus.Localization.Get("bp_campinfopanelmissionview_047", "Spring")
                                  : season == 1 ? BandItPlus.Localization.Get("bp_campinfopanelmissionview_048", "Summer")
                                  : season == 2 ? BandItPlus.Localization.Get("bp_campinfopanelmissionview_049", "Autumn")
                                  : BandItPlus.Localization.Get("bp_campinfopanelmissionview_050", "Winter");
                int dayOfSeason = (int)now.GetDayOfSeason + 1;
                float hour = (float)(now.ToHours - System.Math.Floor(now.ToHours / 24.0) * 24.0);
                string phase;
                if (hour < 5f) phase = BandItPlus.Localization.Get("bp_campinfopanelmissionview_051", "Deep night holds the camp");
                else if (hour < 8f) phase = BandItPlus.Localization.Get("bp_campinfopanelmissionview_052", "Dawn breaks over the camp");
                else if (hour < 12f) phase = BandItPlus.Localization.Get("bp_campinfopanelmissionview_053", "Morning sun warms the camp");
                else if (hour < 14f) phase = BandItPlus.Localization.Get("bp_campinfopanelmissionview_054", "Midday lull over the camp");
                else if (hour < 18f) phase = BandItPlus.Localization.Get("bp_campinfopanelmissionview_055", "Afternoon shadows lengthen");
                else if (hour < 21f) phase = BandItPlus.Localization.Get("bp_campinfopanelmissionview_056", "Dusk falls over the camp");
                else phase = BandItPlus.Localization.Get("bp_campinfopanelmissionview_057", "Night closes in on the camp");
                return seasonName + " " + dayOfSeason + " · Year " + year + " · " + phase;
            }
            catch
            {
                return "";
            }
        }

        private static string FormatSettlement(string settlementId)
        {
            if (string.IsNullOrEmpty(settlementId)) return BandItPlus.Localization.Get("bp_campinfopanelmissionview_058", "(unassigned)");
            try
            {
                var s = Settlement.Find(settlementId);
                return s?.Name?.ToString() ?? settlementId;
            }
            catch { return settlementId; }
        }
    }
}
