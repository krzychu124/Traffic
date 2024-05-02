using System;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using Traffic.Components;
using Traffic.Rendering;
using Traffic.Tools;
using Unity.Entities;
using UnityEngine.Device;

namespace Traffic
{
    [SettingsUISection("Traffic", MainSection)]
    [SettingsUIGroupOrder(MainSection, LaneConnectorSection, OverlaysSection, AboutSection)]
    [SettingsUIShowGroupName( MainSection, LaneConnectorSection, OverlaysSection, AboutSection)]
    public class ModSettings : ModSetting
    {
        internal const string MainSection = "General";  
        internal const string LaneConnectorSection = "LaneConnections";
        internal const string OverlaysSection = "Overlays";
        internal const string AboutSection = "About";

        [SettingsUISection(MainSection, LaneConnectorSection)]
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
        
        [SettingsUISlider(min = 0.2f, max = 2f, step = 0.1f, unit = Game.UI.Unit.kFloatSingleFraction)]
        [SettingsUISection(MainSection, OverlaysSection)]
        [SettingsUIDisableByCondition(typeof(ModSettings), nameof(IsResetConnectionsHidden))]
        [SettingsUIHideByCondition(typeof(ModSettings), nameof(IsResetConnectionsHidden))]
        public float ConnectionLaneWidth { get; set; }
        
        [SettingsUISlider(min = 0.5f, max = 2f, step = 0.1f, unit = Game.UI.Unit.kFloatSingleFraction)]
        [SettingsUISection(MainSection, OverlaysSection)]
        [SettingsUIDisableByCondition(typeof(ModSettings), nameof(IsResetConnectionsHidden))]
        [SettingsUIHideByCondition(typeof(ModSettings), nameof(IsResetConnectionsHidden))]
        public float ConnectorSize { get; set; }
        
        [SettingsUISlider(min = 0.1f, max = 1f, step = 0.1f, unit = Game.UI.Unit.kFloatSingleFraction)]
        [SettingsUISection(MainSection, OverlaysSection)]
        [SettingsUIDisableByCondition(typeof(ModSettings), nameof(IsResetConnectionsHidden))]
        [SettingsUIHideByCondition(typeof(ModSettings), nameof(IsResetConnectionsHidden))]
        public float FeedbackOutlineWidth { get; set; }

        [SettingsUIButton()]
        [SettingsUISection(MainSection, OverlaysSection)]
        [SettingsUIDisableByCondition(typeof(ModSettings), nameof(IsResetConnectionsHidden))]
        [SettingsUIHideByCondition(typeof(ModSettings), nameof(IsResetConnectionsHidden))]
        public bool ResetStyle
        {
            set {
                ToolOverlaySystem toolOverlaySystem = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<ToolOverlaySystem>();
                toolOverlaySystem.SetDefautlOverlayParams(out ToolOverlayParameterData data);
                ConnectorSize = data.laneConnectorSize;
                ConnectionLaneWidth = data.laneConnectorLineWidth;
                FeedbackOutlineWidth = data.feedbackLinesWidth;
                ApplyAndSave();
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

        public ModSettings(IMod mod) : base(mod)
        {
            SetDefaults();
        }
        
        public sealed override void SetDefaults()
        {
            ConnectorSize = 1f;
            ConnectionLaneWidth = 0.4f;
            FeedbackOutlineWidth = 0.3f;
        }

        public override void Apply()
        {
            ToolOverlaySystem toolOverlaySystem = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<ToolOverlaySystem>();
            toolOverlaySystem.ApplyOverlayParams(new ToolOverlayParameterData()
            {
                feedbackLinesWidth = FeedbackOutlineWidth,
                laneConnectorSize = ConnectorSize,
                laneConnectorLineWidth = ConnectionLaneWidth,
            });
        }
    }
}
