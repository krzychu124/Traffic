using Game;
using Game.Modding;
using Game.Net;
using Game.Rendering;
using Game.SceneFlow;
using JetBrains.Annotations;
using Traffic.Debug;
using Traffic.LaneConnections;
using Traffic.Rendering;
using Traffic.Systems;
using Traffic.Tools;
using Traffic.UISystems;
using Traffic.Utils;
using ApplyLaneConnectionsSystem = Traffic.Systems.ApplyLaneConnectionsSystem;
using ValidationSystem = Traffic.Tools.ValidationSystem;

namespace Traffic
{
    [UsedImplicitly]
    public class Mod : IMod
    {
        public const string MOD_NAME = "Traffic";
        
        private ModSettings _modSettings;

        public void OnLoad(UpdateSystem updateSystem) {
            Logger.Info(nameof(OnLoad));
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
            updateSystem.UpdateAt<ApplyLaneConnectionsSystem>(SystemUpdatePhase.ApplyTool);
            updateSystem.UpdateAt<GenerateConnectorsSystem>(SystemUpdatePhase.Modification5);
            updateSystem.UpdateAt<LaneConnections.SearchSystem>(SystemUpdatePhase.Modification5);
            updateSystem.UpdateAt<LaneConnectorToolTooltipSystem>(SystemUpdatePhase.UITooltip);
            
            updateSystem.UpdateAt<NetworkDebugUISystem>(SystemUpdatePhase.UIUpdate);
#if DEBUG_TOOL
            // updateSystem.UpdateAt<CleanUp>(SystemUpdatePhase.Cleanup);
            // updateSystem.UpdateAt<ApplyTool>(SystemUpdatePhase.ApplyTool);
            // updateSystem.UpdateAt<ClearTool>(SystemUpdatePhase.ClearTool);
#endif
            _modSettings = new ModSettings(this);
            _modSettings.RegisterInOptionsUI();
            if (!GameManager.instance.localizationManager.activeDictionary.ContainsID(_modSettings.GetSettingsLocaleID()))
            {
                GameManager.instance.localizationManager.AddSource("en-US", new Localization.LocaleEN(_modSettings));
            }
        }

        public void OnDispose() {
            Logger.Info(nameof(OnDispose));
            _modSettings.UnregisterInOptionsUI();
            _modSettings = null;
        }
    }
#if DEBUG_TOOL

    internal partial class CleanUp : GameSystemBase
    {
        protected override void OnUpdate() {
            Logger.Info("CleanUp!");
        }
    }
    
    internal partial class ApplyTool : GameSystemBase
    {
        private ToolSystem _toolSystem;
        private LaneConnectorToolSystem _laneConnectorTool;
        protected override void OnCreate() {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _laneConnectorTool = World.GetOrCreateSystemManaged<LaneConnectorToolSystem>();
        }

        protected override void OnUpdate() {
            if (_toolSystem.activeTool == _laneConnectorTool)
            {
                Logger.Info("ApplyTool!");
            }
        }
    }
    internal partial class ClearTool : GameSystemBase
    {
        private ToolSystem _toolSystem;
        private LaneConnectorToolSystem _laneConnectorTool;
        protected override void OnCreate() {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _laneConnectorTool = World.GetOrCreateSystemManaged<LaneConnectorToolSystem>();
        }

        protected override void OnUpdate() {
            if (_toolSystem.activeTool == _laneConnectorTool)
            {
                Logger.Info("ClearTool!");
            }
        }
    }
#endif
}
