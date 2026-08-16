using System;
using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace BandItPlus
{
    // V3 slim (2026-05-22): MCM page now hosts only the essentials that must
    // live outside the in-game codex (master toggle, compat, diagnostics,
    // recovery actions). All gameplay tuning lives in the Royal Codex Console,
    // opened via the campaign-map button (BanditPlusMapBarInjector). Console-
    // only properties retain their C# bodies — MCMSettings.Instance.X reads
    // still work everywhere; only the [SettingProperty*] attributes are dropped
    // so MCM stops rendering them.
    public class MCMSettings : AttributeGlobalSettings<MCMSettings>
    {
        public override string Id => "BandItPlus_v1";
        public override string DisplayName => Localization.Get("bp_mcmsettings_001", "BandIt Plus");
        public override string FolderName => "BandItPlus";
        public override string FormatType => "json2";

        // -------- MCM-visible: 5 essentials. Console is opened via the
        // campaign-map button (BanditPlusMapBarInjector). --------

        [SettingPropertyBool("{=bp_mcmsettings_002}Enable BandIt Plus", Order = 0, HintText = "{=bp_mcmsettings_003}Master kill switch.")]
        [SettingPropertyGroup("{=bp_mcmsettings_004}General", GroupOrder = 0)]
        public bool EnableMod { get; set; } = true;

        [SettingPropertyBool("{=bp_mcmsettings_005}Compatibility mode", Order = 2, HintText = "{=bp_mcmsettings_006}Yield to other bandit-modifying mods.")]
        [SettingPropertyGroup("{=bp_mcmsettings_004}General", GroupOrder = 0)]
        public bool CompatibilityMode { get; set; } = false;

        [SettingPropertyBool("{=bp_mcmsettings_007}Debug logging", Order = 3, HintText = "{=bp_mcmsettings_008}Writes to debug.log.")]
        [SettingPropertyGroup("{=bp_mcmsettings_004}General", GroupOrder = 0)]
        public bool DebugLogging { get; set; } = false;

        [SettingPropertyInteger("{=bp_mcmsettings_009}Log verbosity", 0, 3, Order = 4, HintText = "{=bp_mcmsettings_010}0 = quiet, 3 = noisy.")]
        [SettingPropertyGroup("{=bp_mcmsettings_004}General", GroupOrder = 0)]
        public int LogVerbosity { get; set; } = 1;

        [SettingPropertyButton("{=bp_mcmsettings_011}Reset BandIt Plus relations to neutral", Order = 5, Content = "{=bp_mcmsettings_012}Reset", HintText = "{=bp_mcmsettings_013}Force-set all 8 BP clan relations to 0 for the player. Use on existing saves where neutral wasn't applied at game start.")]
        [SettingPropertyGroup("{=bp_mcmsettings_004}General", GroupOrder = 0)]
        public Action ResetRelationsButton
        {
            get => OnResetRelationsClicked;
            set { /* MCM requires a setter, ignore */ }
        }

        [SettingPropertyBool("{=bp_mcmsettings_014}Enable Chief Recruitment", Order = 6, HintText = "{=bp_mcmsettings_015}Master toggle for the Wave 5 chief alliance questline. OFF = no new alliance offers, but existing in-flight quests pause in place and resume when re-enabled.")]
        [SettingPropertyGroup("{=bp_mcmsettings_004}General", GroupOrder = 0)]
        public bool EnableChiefRecruitment { get; set; } = true;

        // -------- Console-only (set via the in-game Royal Codex) --------
        // Properties stay so MCMSettings.Instance.X reads and JSON persistence
        // continue working. Attribute removal just hides them from the MCM page.

        public bool ShowClanInUI { get; set; } = true;
        public bool PeacefulEncounters { get; set; } = true;

        // Spawning
        public float SpawnFrequencyMultiplier { get; set; } = 1.0f;
        public int HideoutCountPerClan { get; set; } = 2;
        public bool RespectBiomes { get; set; } = true;
        public bool AllowVanillaHideouts { get; set; } = true;

        // Per-clan toggles
        public bool EnableFrostReavers { get; set; } = true;
        public bool EnableMarshStalkers { get; set; } = true;
        public bool EnableHighwaymen { get; set; } = true;
        public bool EnableSlaverCaravans { get; set; } = true;
        public bool EnableFallenLegionaries { get; set; } = true;
        public bool EnableSkyRaiders { get; set; } = true;
        public bool EnableSteppeWolves { get; set; } = true;
        public bool EnablePaganCult { get; set; } = true;

        // Loot
        public float CoinDropMultiplier { get; set; } = 1.0f;
        public bool DropKeepsakes { get; set; } = true;
        public bool UseEliteBonus { get; set; } = true;
        public float KeepsakeChanceMultiplier { get; set; } = 1.0f;

        // Wave 4.15.0 (2026-06-07) — Autonomous bandit village raids (Phase A).
        // Wave 4.15.0-fix3 (2026-06-07): SAFE TO RE-ENABLE. Crash root cause fixed:
        //   spawned raid parties now reflect-set MobileParty._leaderHero =
        //   BanditChiefRegistry.GetChief(culture) — mirrors TrySummonWarband. Without
        //   a leader, WarPartyComponent.OnFinalize NRE'd when the party died in a
        //   MapEvent. Cultures with no chief (vanilla looters/sea_raiders/forest_bandits/
        //   etc.) silently skip the spawn — they have no leader to assign.
        // Tune via Royal Codex Console (no MCM attribute — console-only).
        public bool AutonomousRaidsEnabled { get; set; } = true;
        public float RaidPowerGate { get; set; } = 0.30f;             // start raids at 30% power factor
        public float DaysBetweenRaidsPerCulture { get; set; } = 14f;  // 2-week cooldown per culture
        public int MaxConcurrentRaiders { get; set; } = 3;            // global cap
        public float RaidSpawnChancePerHour { get; set; } = 0.04f;    // ~96% no-spawn per hour
        public float MaxRaidRange { get; set; } = 200f;               // world units from hideout
        public bool ExcludePlayerOwnedVillages { get; set; } = true;  // protect player's clan villages
        // Wave 4.15.0-fix13 (2026-06-08): beef-up multiplier for raid parties.
        // Default 2.0 means each spawned raider gets +2x the template stack values
        // as extra troops — typically takes party from ~30 to ~80-90 troops, strong
        // enough to attack villages solo without "circling waiting for reinforcements".
        public float RaidPartySizeMultiplier { get; set; } = 2.0f;

        // Wave 4.x — bandit SETTLEMENT BLOCKADE (towns/castles). Console-only (no MCM attribute), like the raids above.
        public bool EnableBanditBlockades { get; set; } = true;
        public float BlockadeMaxRange { get; set; } = 200f;          // world units from a hideout
        public float BlockadeWindowDays { get; set; } = 4f;          // days of pressure before the sack
        public float BlockadeDrainPerHour { get; set; } = 1.5f;      // prosperity/security points drained each hour
        public bool BlockadeExcludePlayerOwned { get; set; } = false;
        // Wave 4.x — blockade → rebellion escalation.
        public bool BlockadeCanCauseRebellion { get; set; } = true;          // master toggle for the escalation
        public float BlockadeLoyaltyDrainPerHour { get; set; } = 0.6f;       // loyalty bled each hour while parked — "earned" rebellion (a single 4-day blockade only dents ~58, so healthy towns survive one)
        public float BlockadeRebellionLoyaltyThreshold { get; set; } = 25f;  // force a rebellion only when loyalty is below this (vanilla constant)
        public bool BlockadeRebellionBanditSeize { get; set; } = true;       // on rebellion, reflavor the rebel clan to the bandits + garrison the warband
        // Realistic balance: rare + capped + opportunistic targeting.
        public float BlockadeBaseChancePerHour { get; set; } = 0.0015f;      // per-hour trigger base (scales with campaign year); ~a handful/year
        public int BlockadeMaxConcurrent { get; set; } = 2;                  // hard cap on simultaneous blockades
        public int BlockadeMaxGarrisonToTarget { get; set; } = 120;          // bandits only blockade fiefs with garrison at/below this
        // Bandit SIEGE (Phase 1 = kingdom round-trip proof). Both default OFF — opt-in test only.
        public bool EnableBanditSieges { get; set; } = false;                // master toggle for the whole siege feature
        public bool BanditSiegePhase1Test { get; set; } = false;             // debug: fire ONE temp-kingdom round-trip then stop (Phase 1 PROVEN 2026-06-29)
        public bool BanditSiegePhase2Test { get; set; } = true;              // TEST (revert after Phase 2 verified): spawn warband -> temp kingdom -> real siege of a weak town -> detect capture
        // Plain property (NOT an MCM-UI slider — a lone UI setting in a new group crashed the options screen on close;
        // sibling siege flags are console/code-only too). RELEASE default 5 yrs; BanditSiegePhase2Test overrides to ~7 days for testing.
        public int MadKingWarDelayYears { get; set; } = 5;                   // Mad King: years hidden-at-peace before declaring war on all kingdoms
        // Bandit-King GUI (2026-07-01): route the six Mad King / rebellion events through the premium
        // BanditKingBrief panel. Plain console/code props (NO [SettingProperty*] — a lone UI setting in a
        // new group crashes the options screen on close). ShowMadKingPanels=false → fall back to a plain
        // DisplayMessage. RebellionPanelsAsToasts is honored by the caller task (rebellion trio → toast).
        public bool ShowMadKingPanels { get; set; } = true;
        public bool RebellionPanelsAsToasts { get; set; } = false;
        // 2026-07-01 data-accuracy fix: scope the Rebellion panel to BANDIT-DRIVEN uprisings.
        // true (default) → only the blockade-FORCED rebellion (bandit-driven) shows the panel;
        // false → any Calradia uprising (incl. vanilla-fired) shows it. Plain console prop
        // (NO [SettingProperty*] — matches the sibling rebellion/siege flags above).
        public bool RebellionPanelsBanditOnly { get; set; } = true;
        // 2026-07-02 Mad King WARLORD BRAIN (approved design 2026-07-02-mad-king-warlord-brain-design):
        // plain console props (NO [SettingProperty*] — options-close crash memory), exactly the
        // ShowMadKingPanels pattern above. A) cautious war AI (scored targets, 1.5x attack gate,
        // siege lift), B) daily fief development, C) diplomacy brain (prey/truce/re-declare).
        public bool MadKingCautiousAI { get; set; } = true;
        public bool MadKingDevelopsFiefs { get; set; } = true;
        public bool MadKingDiplomacy { get; set; } = true;
        // hideout raid stealth/alarm (sneak-in scene missions). plain console props
        // (NO [SettingProperty*] — options-close crash). sensitivity scales how fast
        // the suspicion meter climbs while a bandit can see you (0.2..3).
        public bool HideoutStealthAlarm { get; set; } = true;
        public float HideoutAlarmSensitivity { get; set; } = 1.0f;
        // chief signature quests (2026-07-03). OFF hides every signature dialog
        // line; an already-active quest keeps its cleanup handlers.
        public bool EnableSignatureQuests { get; set; } = true;
        // sneak-in Shadow Bar (2026-07-03): suspicion + native 5s grace-timer HUD
        // in the hideout stealth mission. plain console prop.
        public bool SneakShadowHud { get; set; } = true;

        // 07-03 Console persistence. The Royal Codex writes straight onto this
        // instance, but MCM only serializes its Global JSON from ITS OWN options
        // screen — console changes silently reverted on restart. Decompile-verified
        // programmatic save: MCM.Abstractions.BaseSettingsProvider.Instance
        // .SaveSettings(BaseSettings). Called from the console's Close().
        public static void PersistNow()
        {
            try
            {
                var s = Instance;
                if (s == null) return;
                MCM.Abstractions.BaseSettingsProvider.Instance?.SaveSettings(s);
                // 07-13 Also persist the console-only properties (which carry no
                // [SettingProperty*] attribute, so MCM never serializes them) to BP's
                // companion JSON. Without this, console edits silently reverted on restart.
                ConsoleSettingsStore.Save();
            }
            catch (System.Exception ex)
            {
                try
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "[BP-Console] PersistNow " + ex.GetType().Name + ": " + ex.Message);
                }
                catch { }
            }
        }
        public int HideoutAlarmReinforcements { get; set; } = 16;  // CAP on alarm reinforcements (0 = off, hard cap 20); the actual count scales with squad size, troop tiers and clan tier
        // Wave 4.16.0 (2026-06-11): realistic muster + yearly escalation knobs.
        // Console-only (Royal Codex), same as the other raid settings.
        // RaidMusterBaseSize: troops a war band gathers before departing (scaled by
        // power + campaign year). RaidYearlyEscalationPercent: % growth per in-game
        // year applied to muster size, reinforcement count, and raid frequency
        // (0 = escalation off; capped at 4x overall).
        public int RaidMusterBaseSize { get; set; } = 45;
        public float RaidYearlyEscalationPercent { get; set; } = 25f;
        public bool ShowKeepsakeNotification { get; set; } = true;

        // Wave 4.28.x: campaign-map HUD visibility (Console-only, Bandit Power chapter).
        // Gates BanditHudService's mount of the roadside infamy/rank bar. Default on so
        // players see the preview; toggling off unmounts it on the next tick.
        // 07-10: default OFF — the map HUD still ships as a preview (UNAVAILABLE
        // watermark); players opt in from the codex until the full HUD lands.
        public bool ShowMapHud { get; set; } = false;
        // Wave 4.28.x: HUD sub-element toggles (Console HUD chapter).
        public bool ShowRankUpToast { get; set; } = true; // toast when infamy rank rises
        public bool ShowHudGlow { get; set; } = true;      // firelight halo on the roadside bar
        public bool ShowQuestAlert { get; set; } = true;   // pulsing badge during a hunt pledge
        // 07-10: every BP HUD is toggleable from the codex HUD chapter.
        public bool ShowChiefCard { get; set; } = true;    // hideout chief reveal card (assault + sneak)
        public bool ShowBattlePanel { get; set; } = true;  // BANDIT AI checklist in bandit field battles

        // Difficulty
        public float PartySizeMultiplier { get; set; } = 1.0f;
        public float HideoutBossStrength { get; set; } = 1.0f;
        public bool ScaleTierWithLevel { get; set; } = false;
        public float ReinforcementChance { get; set; } = 0.20f;

        // Bandit Power
        public bool EnableBanditPower { get; set; } = true;
        public int BanditMaxPartySize { get; set; } = 60;
        public int BanditPowerRampYears { get; set; } = 6;
        public float BanditMountDamageMultiplier { get; set; } = 2.0f;

        // ───────── BrainNPC ─────────
        // Three settings only. A tactical AI with thirty knobs is untunable, and every
        // bug report becomes config-dependent.
        public bool EnableBrainNpc { get; set; } = true;
        public float BrainNpcAggression { get; set; } = 1.0f;
        public bool BrainNpcShowTells { get; set; } = false;

        // Bandit Backup (Wave 4.29.0) — nearby bandits answer a call for reinforcements.
        // Console-adjustable plain props (same model as EnableBanditPower above).
        public bool EnableBanditBackup { get; set; } = true;
        public int BanditBackupChance { get; set; } = 35;    // % chance each nearby bandit party answers
        public int BanditBackupMaxParties { get; set; } = 2;  // cap on responders per call
        public int BanditBackupArrivalDelay { get; set; } = 30; // seconds before reinforcements arrive mid-battle

        // Bandit Captivity (Wave 4.31.0) — captured by bandits -> marched to their hideout + jailed.
        public bool EnableBanditCaptivity { get; set; } = true;
        public int CaptivityEscapeDifficulty { get; set; } = 3; // 1 (easy) .. 5 (hard)

        // -------- Button handlers --------

        public void OnResetRelationsClicked()
        {
            PlayerBanditNeutrality.Instance?.ResetAllRelations();
        }
    }
}
