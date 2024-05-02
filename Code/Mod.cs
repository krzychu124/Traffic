using System;
using System.Linq;
using System.Reflection;
using Colossal.IO.AssetDatabase;
using Game;
using Game.Modding;
using Game.Net;
using Game.Rendering;
using Game.SceneFlow;
using Game.Serialization;
using Game.Tools;
using JetBrains.Annotations;
using Traffic.Debug;
using Traffic.Rendering;
using Traffic.Systems;
using Traffic.Systems.ModCompatibility;
using Traffic.Tools;
using Traffic.UISystems;
using Traffic.Utils;
using Unity.Entities;
using UnityEngine;
using ApplyLaneConnectionsSystem = Traffic.Systems.LaneConnections.ApplyLaneConnectionsSystem;
using GenerateConnectorsSystem = Traffic.Systems.LaneConnections.GenerateConnectorsSystem;
using GenerateLaneConnectionsSystem = Traffic.Systems.LaneConnections.GenerateLaneConnectionsSystem;
using SearchSystem = Traffic.Systems.LaneConnections.SearchSystem;
using SyncCustomLaneConnectionsSystem = Traffic.Systems.LaneConnections.SyncCustomLaneConnectionsSystem;
using ValidationSystem = Traffic.Tools.ValidationSystem;

namespace Traffic
{
    [UsedImplicitly]
    public class Mod : IMod
    {
        public const string MOD_NAME = "Traffic";
        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(4);
        public static string InformationalVersion => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
        public static bool IsTLEEnabled => _isTLEEnabled ??= GameManager.instance.modManager.ListModsEnabled().Any(x => {
            Logger.Info($"{x}");
            return x.StartsWith("C2VM.CommonLibraries.LaneSystem");
        });
        private static bool? _isTLEEnabled;
 
        private ModSettings _modSettings;
        private const string SETTINGS_ASSET_NAME = "Traffic General Settings";

        public void OnLoad(UpdateSystem updateSystem) {
            Logger.Info($"{nameof(OnLoad)}, version: {InformationalVersion}");
            updateSystem.UpdateAt<ModUISystem>(SystemUpdatePhase.UIUpdate);
#if DEBUG_GIZMO            
            updateSystem.UpdateAt<LaneConnectorDebugSystem>(SystemUpdatePhase.DebugGizmos);
#endif
            updateSystem.UpdateAfter<ToolOverlaySystem, AreaRenderSystem>(SystemUpdatePhase.Rendering);
            
            VanillaSystemHelpers.ModifyLaneSystemUpdateRequirements(updateSystem.World.GetOrCreateSystemManaged<LaneSystem>());
            updateSystem.UpdateBefore<TrafficLaneSystem, LaneSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateBefore<SyncCustomLaneConnectionsSystem, TrafficLaneSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateAt<ModificationDataSyncSystem>(SystemUpdatePhase.Modification4B);
            updateSystem.UpdateAt<GenerateLaneConnectionsSystem>(SystemUpdatePhase.Modification3);

            updateSystem.UpdateAt<ModRaycastSystem>(SystemUpdatePhase.Raycast);
            updateSystem.UpdateAfter<ValidationSystem, Game.Tools.ValidationSystem>(SystemUpdatePhase.ModificationEnd);
            
            // updateSystem.UpdateAt<PriorityToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<LaneConnectorToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateBefore<ApplyLaneConnectionsSystem, ApplyNetSystem>(SystemUpdatePhase.ApplyTool);
            updateSystem.UpdateAt<GenerateConnectorsSystem>(SystemUpdatePhase.Modification5);
            updateSystem.UpdateAt<SearchSystem>(SystemUpdatePhase.Modification5);
            updateSystem.UpdateAt<LaneConnectorToolTooltipSystem>(SystemUpdatePhase.UITooltip);

            updateSystem.UpdateBefore<PreDeserialize<ModDefaultsSystem>>(SystemUpdatePhase.Deserialize);

#if DEBUG
            updateSystem.UpdateAt<NetworkDebugUISystem>(SystemUpdatePhase.UIUpdate);
#endif
#if DEBUG_TOOL
            // updateSystem.UpdateAt<CleanUp>(SystemUpdatePhase.Cleanup);
            // updateSystem.UpdateAt<ApplyTool>(SystemUpdatePhase.ApplyTool);
            // updateSystem.UpdateAt<ClearTool>(SystemUpdatePhase.ClearTool);
#endif
            Logger.Info($"Registering check TLE installed and enabled. RenderedFrame: {Time.renderedFrameCount}");
            GameManager.instance.RegisterUpdater(TLECompatibitlityFix);
            
            _modSettings = new ModSettings(this);
            _modSettings.RegisterInOptionsUI();
            AssetDatabase.global.LoadSettings(SETTINGS_ASSET_NAME, _modSettings, new ModSettings(this));
            if (!GameManager.instance.localizationManager.activeDictionary.ContainsID(_modSettings.GetSettingsLocaleID()))
            {
                GameManager.instance.localizationManager.AddSource("en-US", new Localization.LocaleEN(_modSettings));
            }
        }

        public void OnDispose() {
            Logger.Info(nameof(OnDispose));
            _modSettings?.UnregisterInOptionsUI();
            _modSettings = null;
        }

        private void TLECompatibitlityFix()
        {
            if (IsTLEEnabled)
            {
                Logger.Info($"Detected TLE installed and enabled!");
                try
                {
                    World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<UpdateSystem>().UpdateAfter<TLEDataMigrationSystem>(SystemUpdatePhase.Deserialize);
                    ComponentSystemBase tleLaneSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged(Type.GetType("Game.Net.C2VMPatchedLaneSystem, C2VM.CommonLibraries.LaneSystem"));
                    // ModsCompatibilityHelpers.ModifyTLELaneSystemUpdateRequirements(tleLaneSystem);
                    tleLaneSystem.Enabled = false;
                    World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<LaneSystem>().Enabled = true;
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogError($"**Traffic** mod exception!\nSomething went wrong while fixing compatibility with **Traffic Lights Enhancement** mod");
                    Logger.Error($"{e.Message}\n{e.StackTrace}\nInnerException:\n{e.InnerException?.Message}");
                }
            }
        }
    }
#if DEBUG_TOOL

    // internal partial class CleanUp : GameSystemBase
    // {
    //     protected override void OnUpdate() {
    //         Logger.Info("CleanUp!");
    //     }
    // }
    //
    // internal partial class ApplyTool : GameSystemBase
    // {
    //     private ToolSystem _toolSystem;
    //     private LaneConnectorToolSystem _laneConnectorTool;
    //     protected override void OnCreate() {
    //         base.OnCreate();
    //         _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
    //         _laneConnectorTool = World.GetOrCreateSystemManaged<LaneConnectorToolSystem>();
    //     }
    //
    //     protected override void OnUpdate() {
    //         if (_toolSystem.activeTool == _laneConnectorTool)
    //         {
    //             Logger.Info("ApplyTool!");
    //         }
    //     }
    // }
    // internal partial class ClearTool : GameSystemBase
    // {
    //     private ToolSystem _toolSystem;
    //     private LaneConnectorToolSystem _laneConnectorTool;
    //     protected override void OnCreate() {
    //         base.OnCreate();
    //         _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
    //         _laneConnectorTool = World.GetOrCreateSystemManaged<LaneConnectorToolSystem>();
    //     }
    //
    //     protected override void OnUpdate() {
    //         if (_toolSystem.activeTool == _laneConnectorTool)
    //         {
    //             Logger.Info("ClearTool!");
    //         }
    //     }
    // }
#endif
}
