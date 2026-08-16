using Bannerlord.UIExtenderEx;
using TaleWorlds.MountAndBlade;

namespace EditableEncyclopedia.ChronicleNoters
{
    // EE-ChronicleNoters - the home module for everything Chronicle.
    //
    // Owns:
    //   - GlobalChroniclePanel (the J-button panel)
    //   - GlobalChroniclePanelVM
    //   - ChronicleButtonVM
    //   - MapBarChronicleExtension (J button injection)
    //   - EncyclopediaChronicleExtensions (Settlement/Clan/Faction chronicle sections + "+ Add Note")
    //
    // The "+ Add Note" feature on chronicle entries (the actual notes feature) is part of
    // the migrated chronicle code above - notes attach to specific chronicle entries via
    // their stable IDs and are stored alongside chronicle data in EncyclopediaEditBehavior.
    //
    // Depends on:
    //   - EE-Core (PeerRegistry for cross-module discovery)
    //   - EditableEncyclopedia (chronicle data still lives in EncyclopediaEditBehavior;
    //     Phase B.2 will migrate data ownership to this module via SaveSchemaMigrator).
    public class SubModule_ChronicleNoters : MBSubModuleBase
    {
        private static readonly IEELogger Log = EECoreLogger.For("EE-ChronicleNoters");
        private UIExtender _uiExtender;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();

            PeerRegistry.Register(
                moduleId: "EE-ChronicleNoters",
                version: "v1.0.0",
                capabilities: new[] { "chronicle-panel", "chronicle-sections", "add-note" });

            // Register UIExtenderEx to discover [PrefabExtension] classes in THIS assembly:
            //   - MapBarChronicleExtension (J button)
            //   - SettlementChronicleExtension / ClanChronicleExtension / FactionChronicleExtension
            //     (the Chronicle sections with the "+ Add Note" button)
            try
            {
                _uiExtender = UIExtender.Create("EE-ChronicleNoters");
                _uiExtender.Register(typeof(SubModule_ChronicleNoters).Assembly);
                _uiExtender.Enable();
                Log.Info("UIExtenderEx registered - J button and chronicle sections wired");
            }
            catch (System.Exception ex)
            {
                Log.Error("UIExtenderEx init failed - chronicle UI may not appear", ex);
            }

            Log.Info("EE-ChronicleNoters loaded");
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);

            // GlobalChroniclePanel.TickMainThread runs per-frame logic that hooks click
            // handling on the UIExtenderEx-injected J button widget and creates a fallback
            // button if needed. Without this tick the J button has no behavior.
            GlobalChroniclePanel.TickMainThread(dt);
        }
    }
}
