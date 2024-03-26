using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using Traffic.Tools;
using Unity.Entities;

namespace Traffic
{
    //TODO Add translations
    public class ModSettings : ModSetting
    {
        private const string MainSection = "General";  
        private const string MaintenanceSection = "Maintenance";

        [SettingsUIButton()]
        [SettingsUISection(MaintenanceSection)]
        [SettingsUIConfirmation()]
        [SettingsUIDisableByCondition(typeof(ModSettings), nameof(IsResetConnectionsDisabled))]
        public bool ResetLaneConnections
        {
            set {
                World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<LaneConnectorToolSystem>().ResetAllConnections();
            }
        }

        public bool IsResetConnectionsDisabled() => (GameManager.instance.gameMode & GameMode.GameOrEditor) == 0;
        
        public ModSettings(IMod mod) : base(mod) { }
        
        public override void SetDefaults()
        {
        }
    }
}
