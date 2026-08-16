using TaleWorlds.MountAndBlade;

namespace EditableEncyclopedia
{
    public class SubModule_Core : MBSubModuleBase
    {
        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            // EE-Core bootstrap. No Harmony patches, no UI registration here.
            // Peer modules (EE-EditableEncyclopedia, EE-WebAPI, future EE-Ext-*)
            // depend on this module and register their own behaviors.
        }
    }
}
