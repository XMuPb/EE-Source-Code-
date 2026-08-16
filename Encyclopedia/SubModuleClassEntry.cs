using System;
using Bannerlord.UIExtenderEx;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace EditableEncyclopedia
{
    /// <summary>
    /// The main entry point for the Editable Encyclopedia mod. Inherits from MBSubModuleBase
    /// and is referenced in SubModule.xml. Responsible for initializing Harmony patches
    /// and registering the EncyclopediaEditBehavior campaign behavior.
    /// </summary>
    public class SubModuleClassEntry : MBSubModuleBase
    {
        /// <summary>
        /// The Harmony instance used to apply and remove patches. Uses the ID "com.editableencyclopedia.patch".
        /// </summary>
        private Harmony _harmony;

        /// <summary>
        /// UIExtenderEx instance for extending game VMs (e.g., settlement nameplates).
        /// </summary>
        private UIExtender _uiExtender;

        // Round 4 finding P-C3: TextObject reflection handles cached as static so the per-settlement
        // TickNameReapply loop doesn't re-resolve them per call (they're constant for typeof(TextObject)).
        private static System.Reflection.MethodInfo s_textObjectSetValue;
        private static System.Reflection.FieldInfo s_textObjectValue;
        private static System.Reflection.FieldInfo s_textObjectCachedTokens;
        private static System.Reflection.FieldInfo s_textObjectCachedLangId;
        private static volatile bool s_textObjectCacheInitialized;

        private static void EnsureTextObjectReflectionCache()
        {
            if (s_textObjectCacheInitialized) return;
            try
            {
                var toFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                var toType = typeof(TaleWorlds.Localization.TextObject);
                s_textObjectSetValue = toType.GetMethod("SetValue", toFlags);
                s_textObjectValue = toType.GetField("Value", toFlags);
                s_textObjectCachedTokens = toType.GetField("cachedTokens", toFlags);
                s_textObjectCachedLangId = toType.GetField("cachedTextLanguageId", toFlags);
            }
            catch (Exception ex) { MCMSettings.DebugLog("EnsureTextObjectReflectionCache failed: " + ex.ToString()); }
            s_textObjectCacheInitialized = true;
        }

        /// <summary>
        /// Called when the module DLL is loaded. Initializes localization and UIExtenderEx.
        /// </summary>
        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();

            // Initialize UIExtenderEx to register our ViewModelMixins
            // (e.g., SettlementNameplateMixin for settlement banner icon refresh)
            try
            {
                _uiExtender = UIExtender.Create("EditableEncyclopedia");
                _uiExtender.Register(typeof(SubModuleClassEntry).Assembly);
                _uiExtender.Enable();
            }
            catch (Exception ex)
            {
                // UIExtenderEx may not be available in all setups; log and continue
                try { MCMSettings.DebugLog("UIExtenderEx init failed: " + ex.ToString()); } catch (Exception logEx) { MCMSettings.DebugLog("[SubModuleClassEntry]: " + logEx.Message); }
            }

            // Initialize localization early so language files are generated.
            // NOTE: MCMv5 settings may not yet be fully deserialized at this point,
            // so SelectedValue might return the default "en" instead of the user's
            // saved choice. This is re-applied in OnGameStart and on every tick-poll
            // (see ApplyMcmLanguage / OnApplicationTick).
            ApplyMcmLanguage("OnSubModuleLoad");

            // Detect Bannerlord encyclopedia API version (legacy 1.3.x vs modern 1.4.5+).
            // Logs the detected mode to debug.log; subsequent navigation/patch code
            // branches on EncyclopediaCompat.Mode.
            try { EncyclopediaCompat.DetectIfNeeded(); }
            catch (Exception ex) { MCMSettings.DebugLog("[SubModuleClassEntry] EncyclopediaCompat.DetectIfNeeded error: " + ex.ToString()); }
        }

        /// <summary>
        /// Reads the language code from MCM and re-initializes the Localization system
        /// if it differs from what's currently loaded. Safe to call repeatedly.
        /// </summary>
        internal static void ApplyMcmLanguage(string trigger)
        {
            try
            {
                string lang = "en";
                var settings = MCMSettings.Instance;
                if (settings != null && settings.Language != null && !string.IsNullOrWhiteSpace(settings.Language.SelectedValue))
                    lang = settings.Language.SelectedValue.Trim().ToLowerInvariant();

                if (Localization.GetCurrentLanguage() == lang)
                    return; // no change

                MCMSettings.DebugLog($"[Language] {trigger}: switching from '{Localization.GetCurrentLanguage()}' -> '{lang}'");
                Localization.SetLanguage(lang);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("[Language] ApplyMcmLanguage(" + trigger + ") error: " + ex.ToString());
                try { if (!Localization.IsInitialized()) Localization.Initialize("en"); } catch { }
            }
        }

        // Rate-limit the per-tick language poll so we don't hammer MCM every frame
        private float _langPollAccum = 0f;
        private const float LANG_POLL_INTERVAL = 1.5f;

        /// <summary>
        /// Called when the module is unloaded. Removes all Harmony patches registered under
        /// the mod's ID to avoid leaking patches.
        /// </summary>
        protected override void OnSubModuleUnloaded()
        {
            base.OnSubModuleUnloaded();
            _harmony?.UnpatchAll("com.editableencyclopedia.patch");
        }

        /// <summary>
        /// Called right before the main menu is shown, after all mods have loaded.
        /// Used to install the MCM label localization patches so the MCM panel is
        /// translated even when opened from the main menu (no campaign required).
        /// </summary>
        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            try
            {
                // Ensure language is loaded before patching — MCM reads labels after this
                ApplyMcmLanguage("OnBeforeInitialModuleScreenSetAsRoot");

                // Detect mods that restructure the encyclopedia settlement page
                // (e.g. Realm of Thrones). When detected, EE's settlement injectors
                // are skipped by default to avoid the "Lore wraps to its own column"
                // visual conflict reported 2026-05-25. Override via MCM
                // (Override Layout Mod Compat) if compatibility has been verified.
                ModCompatHelper.DetectAtStartup();

                // Install MCM localizer patches with a dedicated Harmony instance so they
                // run regardless of whether a campaign has started yet.
                var localizerHarmony = new Harmony("com.editableencyclopedia.mcm_localizer");
                McmLocalizer.TryApply(localizerHarmony);

                // v2.5.10: hide the "EEWebExtension" subgroup of "9. Extensions" when the
                // extension assembly isn't loaded OR the EnableWebExtension toggle is off.
                // Shares the localizer's Harmony instance — both target MCMv5 internals.
                WebExtensionVisibilityPatch.TryApply(localizerHarmony);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("[SubModuleClassEntry] OnBeforeInitialModuleScreenSetAsRoot error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Called when a game (campaign) starts. Registers EncyclopediaEditBehavior as a campaign
        /// behavior (only for Campaign mode) and applies all Harmony patches via PatchAll().
        /// </summary>
        /// <param name="game">The game instance being started.</param>
        /// <param name="gameStarterObject">The game starter object used to register behaviors.</param>
        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);

            // Re-apply localization here — OnSubModuleLoad runs before MCMv5 has
            // finished deserializing saved settings from disk, so the initial call
            // may have seen the default "en" instead of the user's saved choice.
            // OnGameStart runs later when MCM settings are guaranteed loaded.
            ApplyMcmLanguage("OnGameStart");

            if (gameStarterObject is CampaignGameStarter campaignStarter)
            {
                campaignStarter.AddBehavior(new EncyclopediaEditBehavior());
            }

            // Clear cached custom culture objects from any previous session
            CustomCultureManager.ClearCache();

            if (_harmony == null)
            {
                _harmony = new Harmony("com.editableencyclopedia.patch");
                _harmony.PatchAll();

                // Manually patch the encyclopedia list page to apply custom hero names
                EncyclopediaListPatcher.TryPatch(_harmony);

                // Patch Clan.UpdateBannerColorsAccordingToKingdom to prevent the game
                // from reverting custom NPC clan banners set via the mod
                ClanBannerReversionPatch.TryPatch(_harmony);

                // Capture the real gold + looted items the engine hands out in battles and raids
                // so the auto-journal can attach them to its chronicle lines. Each target method
                // is looked up individually and skipped if absent, so a game update degrades to
                // "no spoils recorded" rather than failing to patch.
                ChronicleSpoilsCollector.TryPatch(_harmony);

                // Patch MBSaveLoad.OnSave to sanitize settlement cultures before
                // the serializer writes them (swap custom → vanilla, then restore after)
                SaveSanitizerPatch.TryApply(_harmony);

                // Patch settlement tooltip handler to inject Culture line on the campaign map
                SettlementTooltipCulturePatch.TryApply(_harmony);

                // Patch TextWidget rendering to force Left alignment on lore section text
                LoreSectionInjector.TryApplyHarmonyPatch(_harmony);

                // Patch EditableTextWidget.OnRender to enforce scissor clipping
                // (text rendering bypasses ClipContents in v1.3.13)
                EncyclopediaEditPopup.TryApplyClipPatch(_harmony);

                // 2026-05-26: GauntletChatLogViewDiagnostics DISABLED on Bannerlord v1.4.5.
                // The diagnostic Harmony finalizers on GauntletChatLogView.OnLateTick/HandleInput
                // were installed as instrumentation during the NavalDLC clan crash investigation.
                // In v1.4.5 their MonoMod-generated IL wrapper itself NREs (crash dialog shows
                // "Source: MonoMod.Utils ... at GauntletChatLogView.OnLateTick_Patch1"), turning
                // a contained engine-side NRE into a hard CTD. The diagnostic served its purpose
                // (we now know the v1.4.5 encyclopedia subsystem refactor is the root cause —
                // see EncyclopediaCompat.cs); keep the file on disk for future re-enabling on
                // older Bannerlord versions but do not patch.
                // GauntletChatLogViewDiagnostics.TryApply(_harmony);

            }
        }

        /// <summary>
        /// Called every frame on the main game thread. Used to execute deferred
        /// banner visual refreshes that were scheduled from background threads.
        /// </summary>
        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);

            // Poll MCM every ~1.5s for a language change and re-apply live.
            // Covers: user changes language while the game is running, no restart needed.
            _langPollAccum += dt;
            if (_langPollAccum >= LANG_POLL_INTERVAL)
            {
                _langPollAccum = 0f;
                ApplyMcmLanguage("TickPoll");
            }

            EncyclopediaBannerEditPopup.TickMainThreadRefresh();
            EncyclopediaBannerEditPopup.TickBannerWatchdog();
            // Process deferred selection popup show (must run on main thread)
            SelectionPopupHelper.TickMainThread();
            // Process deferred Quick Tag Menu show (must run on main thread)
            EncyclopediaTagEditPopup.TickMainThread();
            // Process deferred lore editor popup show/close (must run on main thread)
            LoreEditorPopup.TickMainThread();
            // Process deferred timestamp widget operations (retries + clears)
            // that were scheduled from background timer threads.
            TimestampWidgetInjector.TickMainThread();
            // Process deferred tag widget injection (widget tree may not be ready)
            TagWidgetInjector.TickMainThread();
            // Process deferred lore section injection (widget tree may not be ready)
            LoreSectionInjector.TickMainThread();
            // Process deferred journal section injection (widget tree may not be ready)
            JournalSectionInjector.TickMainThread();
            // Process deferred stats widget injection (Settlement/Kingdom info panels)
            StatsWidgetInjector.TickMainThread();
            // Process deferred relation notes section injection (widget tree may not be ready)
            RelationNotesSectionInjector.TickMainThread();
            // Process deferred hero timeline section injection
            HeroTimelineSectionInjector.TickMainThread();
            InventoryLoreSectionInjector.TickMainThread();
            // Process deferred relation notes popup display
            RelationNotesInjector.TickMainThread();
            // Round 4 fix STC-1: process deferred SettlementNameplateMixin VM refresh on the
            // main thread (was incorrectly being done from a Timer callback off-thread).
            SettlementNameplateMixin.TickMainThread();
            // v2.5.2 fix: process deferred encyclopedia refresh + confirmation message after
            // culture/occupation changes (avoids same-thread reentrance crash inside
            // GauntletGamepadNavigationManager.Update).
            EncyclopediaPatchHelper.TickMainThread();
            // Process deferred edit popup injector show (programmatic overlay)
            EditPopupInjector.TickMainThread();
            // Process deferred description markdown formatting (widget replacement)
            DescriptionFormatter.Tick();
            // Process deferred character limit override on text inquiry dialog
            EncyclopediaEditPopup.TickCharLimitOverride();
            // Process deferred hero page injection retry (Clan→Map screen transition)
            HeroPageRefreshPatch.TickDeferredRetry();
            // Chronicle panel moved to EE-ChronicleNoters in v2.6.0 - that module ticks its own panel.
            // GlobalChroniclePanel.TickMainThread(dt);
            // Auto-unblock encyclopedia input when no popup is open
            EncyclopediaInputBlocker.TickAutoUnblock();
            // Force encyclopedia to stay "open" while our blocking layer is active
            // (IsFocusLayer causes the encyclopedia to think it's closing)
            EncyclopediaInputBlocker.TickForceEncyclopediaOpen();
            // Apply Harmony patches on encyclopedia navigation commands (deferred)
            EncyclopediaNavigationGuard.Tick();
            // Re-register custom culture definitions (with troop trees) after save load
            TickCustomCultureReapply();
            // Deferred settlement culture application after load
            TickDeferredSettlementCultures();
            // Deferred culture widget text override in native Info panel
            EncyclopediaPatchHelper.TickCultureWidgetOverride();
            // Show intro dialog for new campaigns
            TickIntroDialog();
            // Re-apply custom names to game objects after save load
            TickNameReapply();
            // Deferred nameplate refresh for renamed settlements
            TickNameplateRefresh();
        }

        private int _nameplateRefreshCountdown = 0;

        private void TickNameplateRefresh()
        {
            if (_nameplateRefreshCountdown <= 0) return;
            _nameplateRefreshCountdown--;
            if (_nameplateRefreshCountdown > 0) return;

            // Run on main thread — widget tree access requires it
            try
            {
                var behavior = EncyclopediaEditBehavior.Instance;
                if (behavior == null) return;

                // Refresh VM properties
                int updated = 0;
                try { updated = SettlementNameplateMixin.RefreshAllNameplatesMainThread(); } catch { }

                // Also find and update the actual TextWidgets directly
                foreach (var settlement in Settlement.All)
                {
                    try
                    {
                        if (settlement == null) continue;
                        string customName = behavior.GetCustomName(settlement.StringId);
                        if (string.IsNullOrEmpty(customName)) continue;
                        SettlementNameplateMixin.UpdateNameplateWidgets(settlement.StringId, customName);
                    }
                    catch { }
                }
                MCMSettings.DebugLog("TickNameplateRefresh: refreshed " + updated + " nameplates on main thread");
            }
            catch (Exception ex)
            {
                try { MCMSettings.DebugLog("TickNameplateRefresh failed: " + ex.ToString()); } catch { }
            }
        }

        /// <summary>
        /// After loading a save, pushes all stored custom names back onto game objects
        /// (Heroes, Settlements, Clans, Kingdoms). Deferred to tick because game objects
        /// may not be fully initialized during SyncData.
        /// </summary>
        private int _nameReapplyDelay = 0;

        private void TickNameReapply()
        {
            if (!EncyclopediaEditBehavior.NeedsNameReapply) return;

            // Wait a few ticks for game objects to be fully initialized
            if (_nameReapplyDelay < 5) { _nameReapplyDelay++; return; }

            EncyclopediaEditBehavior.NeedsNameReapply = false;
            _nameReapplyDelay = 0;

            try
            {
                var behavior = EncyclopediaEditBehavior.Instance;
                if (behavior == null) return;

                int applied = 0;

                // Re-apply hero names
                foreach (var hero in Hero.AllAliveHeroes)
                {
                    try
                    {
                        if (hero == null) continue;
                        string customName = behavior.GetCustomName(hero.StringId);
                        if (string.IsNullOrEmpty(customName)) continue;

                        EncyclopediaPageTracker.SetHeroName(hero, customName);
                        applied++;
                    }
                    catch { }
                }

                // Re-apply settlement names
                // Modify the existing TextObject in-place AND clear its cache
                bool anySettlementRenamed = false;
                foreach (var settlement in Settlement.All)
                {
                    try
                    {
                        if (settlement == null) continue;
                        string customName = behavior.GetCustomName(settlement.StringId);
                        if (string.IsNullOrEmpty(customName)) continue;

                        // Get the existing Name TextObject and modify it in-place
                        var nameTO = settlement.Name;
                        if (nameTO != null)
                        {
                            // Round 4 finding P-C3: use cached reflection handles instead of resolving per settlement.
                            EnsureTextObjectReflectionCache();
                            if (s_textObjectSetValue != null)
                            {
                                s_textObjectSetValue.Invoke(nameTO, new object[] { customName });
                                MCMSettings.DebugLog("TickNameReapply: used TextObject.SetValue for " + settlement.StringId);
                            }
                            else
                            {
                                // Fallback: set Value field + clear cache
                                if (s_textObjectValue != null) s_textObjectValue.SetValue(nameTO, customName);
                                if (s_textObjectCachedTokens != null) s_textObjectCachedTokens.SetValue(nameTO, null);
                                if (s_textObjectCachedLangId != null) s_textObjectCachedLangId.SetValue(nameTO, -1);
                                MCMSettings.DebugLog("TickNameReapply: set TextObject.Value + cleared cache for " + settlement.StringId
                                    + " — ToString() now returns: \"" + nameTO.ToString() + "\"");
                            }
                        }
                        else
                        {
                            // Name is null — use SetGameObjectName to create new TextObject
                            EncyclopediaPageTracker.SetGameObjectName(settlement, customName);
                        }
                        anySettlementRenamed = true;
                        applied++;
                    }
                    catch { }
                }
                // Schedule deferred nameplate refresh — SettlementNameplateVMs aren't
                // created until the map screen fully renders, which is well after this tick
                if (anySettlementRenamed)
                {
                    try { SettlementNameplateMixin.ScheduleDeferredRefresh(); } catch { }
                    // Also schedule a second refresh later for nameplates that load slowly
                    _nameplateRefreshCountdown = 300; // ~5 seconds
                }

                // Re-apply clan names
                foreach (var clan in Clan.All)
                {
                    try
                    {
                        if (clan == null) continue;
                        string customName = behavior.GetCustomName(clan.StringId);
                        if (string.IsNullOrEmpty(customName)) continue;

                        EncyclopediaPageTracker.SetGameObjectName(clan, customName);
                        applied++;
                    }
                    catch { }
                }

                // Re-apply kingdom names
                foreach (var kingdom in Kingdom.All)
                {
                    try
                    {
                        if (kingdom == null) continue;
                        string customName = behavior.GetCustomName(kingdom.StringId);
                        if (string.IsNullOrEmpty(customName)) continue;

                        EncyclopediaPageTracker.SetGameObjectName(kingdom, customName);
                        applied++;
                    }
                    catch { }
                }

                MCMSettings.DebugLog("TickNameReapply: applied " + applied + " custom names");
            }
            catch (Exception ex)
            {
                try { MCMSettings.DebugLog("TickNameReapply failed: " + ex.ToString()); } catch { }
            }
        }

        private int _introDelayTicks = -1;

        private void TickIntroDialog()
        {
            var beh = EncyclopediaEditBehavior.Instance;
            if (beh == null || !beh._showIntroOnNextTick) return;

            // Only check every ~5 seconds
            if (_introDelayTicks < 0) _introDelayTicks = 300;
            if (_introDelayTicks > 0) { _introDelayTicks--; return; }
            _introDelayTicks = 300; // reset for next check

            // Wait until the game is NOT paused — character creation keeps the game paused
            try
            {
                if (Campaign.Current == null) return;
                var mode = Campaign.Current.TimeControlMode;
                // StoppablePlay or UnstoppablePlay = game is running = player is on map
                // Stop or FastForwardStop = paused (character creation, dialogs, etc.)
                bool isPlaying = mode != CampaignTimeControlMode.Stop;
                if (!isPlaying) return;
            }
            catch { return; }

            beh._showIntroOnNextTick = false;
            _introDelayTicks = -1;

            try
            {
                // Pause the game while showing the intro
                Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
                Campaign.Current.SetTimeSpeed(0);

                InformationManager.ShowInquiry(
                    new InquiryData(
                        Localization.L("intro_title"),
                        Localization.L("intro_body"),
                        true, false,
                        Localization.L("intro_enter"),
                        null,
                        () => {
                            try
                            {
                                // Unpause the game
                                Campaign.Current.TimeControlMode = CampaignTimeControlMode.StoppablePlay;
                                Campaign.Current.SetTimeSpeed(1);
                                InformationManager.DisplayMessage(
                                    new InformationMessage(
                                        "[Editable Encyclopedia] The chronicle awaits. Press Ctrl+E on any encyclopedia page to begin writing.",
                                        Colors.Green));
                            }
                            catch { }
                        },
                        null),
                    true, false);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("Intro dialog failed: " + ex.ToString());
            }
        }

        /// <summary>
        /// Checks if custom culture definitions need to be re-registered after loading a save.
        /// Deferred to tick because MBObjectManager and Hero lists may not be fully ready during SyncData.
        /// </summary>
        private void TickCustomCultureReapply()
        {
            if (!EncyclopediaEditBehavior.NeedsCustomCultureReapply) return;
            EncyclopediaEditBehavior.NeedsCustomCultureReapply = false;

            try
            {
                CustomCultureManager.ReregisterAllFromSaveData();
                // Schedule deferred settlement culture application — Clan.Settlements
                // may not be fully resolved during the same tick as hero reapply
                _settlementCultureRetryTicks = 5;
            }
            catch (Exception ex)
            {
                try { MCMSettings.DebugLog("TickCustomCultureReapply failed: " + ex.ToString()); } catch (Exception logEx) { MCMSettings.DebugLog("[SubModuleClassEntry]: " + logEx.Message); }
            }
        }

        /// <summary>
        /// Countdown for deferred settlement culture application after save load.
        /// Waits a few ticks for Clan.Settlements to be fully resolved.
        /// </summary>
        private int _settlementCultureRetryTicks = 0;

        private void TickDeferredSettlementCultures()
        {
            if (_settlementCultureRetryTicks <= 0) return;
            _settlementCultureRetryTicks--;
            if (_settlementCultureRetryTicks > 0) return;

            try
            {
                var behavior = EncyclopediaEditBehavior.Instance;
                if (behavior == null) return;

                int applied = 0;
                foreach (var hero in Hero.AllAliveHeroes)
                {
                    try
                    {
                        if (hero?.Clan == null || hero.Clan.Leader != hero) continue;
                        string cultureId = behavior.GetCustomCulture(hero.StringId);
                        if (string.IsNullOrEmpty(cultureId)) continue;

                        var customCulture = CustomCultureManager.GetCachedCulture(cultureId);
                        if (customCulture == null) continue;

                        EncyclopediaCultureEditPopup.UpdateOwnedSettlementsCulture(hero, customCulture, hero.StringId);
                        applied++;
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("[SubModuleClassEntry]: " + ex.ToString()); }
                }

                if (applied > 0)
                    MCMSettings.DebugLog("TickDeferredSettlementCultures: processed " + applied + " clan leaders");
            }
            catch (Exception ex)
            {
                try { MCMSettings.DebugLog("TickDeferredSettlementCultures failed: " + ex.ToString()); } catch (Exception logEx) { MCMSettings.DebugLog("[SubModuleClassEntry]: " + logEx.Message); }
            }
        }
    }
}
