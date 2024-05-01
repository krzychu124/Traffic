using System;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using Traffic.Tools;
using Unity.Entities;
using UnityEngine.Device;

namespace Traffic
{
    [SettingsUISection("Traffic", MainSection)]
    [SettingsUIGroupOrder(MainSection, MaintenanceSection, AboutSection)]
    [SettingsUIShowGroupName( MainSection, MaintenanceSection, AboutSection)]
    public class ModSettings : ModSetting
    {
        internal const string MainSection = "General";  
        internal const string MaintenanceSection = "Maintenance";
        internal const string AboutSection = "About";

        [SettingsUISection(MainSection, MaintenanceSection)]
        [SettingsUIButton()]
        [SettingsUIConfirmation()]
        [SettingsUIDisableByCondition(typeof(ModSettings), nameof(IsResetConnectionsHidden))]
        [SettingsUIHideByCondition(typeof(ModSettings), nameof(IsResetConnectionsHidden))]
        public bool ResetLaneConnections
        {
            set {
                World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<LaneConnectorToolSystem>().ResetAllConnections();
            }
        }

        public bool IsResetConnectionsHidden()
        {
            return (GameManager.instance.gameMode & GameMode.GameOrEditor) == 0;
        }

        [SettingsUISection(MainSection, AboutSection)]
        public string ModVersion => Mod.Version;
        
        [SettingsUISection(MainSection, AboutSection)]
        public string InformationalVersion => Mod.InformationalVersion;

        [SettingsUISection(MainSection, AboutSection)]
        public bool OpenRepositoryAtVersion
        {
            set {
                try
                {
                    Application.OpenURL($"https://github.com/krzychu124/Traffic/commit/{Mod.InformationalVersion.Split('+')[1]}");
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogException(e);
                }
            }
        }

        public ModSettings(IMod mod) : base(mod) { }
        
        public override void SetDefaults()
        {
        }
    }
}
