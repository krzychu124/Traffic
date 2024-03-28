using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using Traffic.Tools;
using Unity.Entities;

namespace Traffic
{
    [SettingsUISection(MaintenanceSection)]
    public class ModSettings : ModSetting
    {
        internal const string MainSection = "General";  
        internal const string MaintenanceSection = "Maintenance";

        [SettingsUIButton()]
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
