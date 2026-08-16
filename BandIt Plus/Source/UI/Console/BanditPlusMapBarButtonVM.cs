using TaleWorlds.Library;

namespace BandItPlus.UI.Console
{
    // v183 Phase 3 (2026-05-20): tiny DataSource VM for the map-bar button.
    // The button prefab binds Command.Click="ExecuteOpenBanditPlusConsole" to
    // this VM's method. Routes to BanditPlusConsoleService.OpenFromButton().
    public class BanditPlusMapBarButtonVM : ViewModel
    {
        public void ExecuteOpenBanditPlusConsole()
        {
            try
            {
                BanditPlusConsoleService.OpenFromButton();
            }
            catch (System.Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("v183 MapBar button click fail: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
