using Game;
using Game.Modding;
using Game.Net;
using Game.Rendering;
using JetBrains.Annotations;
using Traffic.LaneConnections;
using Traffic.Rendering;
using Traffic.Systems;
using Traffic.Tools;
using Traffic.UI;
using ApplyLaneConnectionsSystem = Traffic.Systems.ApplyLaneConnectionsSystem;

namespace Traffic
{
    [UsedImplicitly]
    public class Mod : IMod
    {

        public void OnCreateWorld(UpdateSystem updateSystem) {
            Logger.Info(nameof(OnCreateWorld));
            updateSystem.UpdateAt<ModUISystem>(SystemUpdatePhase.UIUpdate);
            
            updateSystem.UpdateAfter<ToolOverlaySystem, AreaRenderSystem>(SystemUpdatePhase.Rendering);
            
            // TODO update TrafficLaneSystem with the latest code from build before applying changes
            updateSystem.World.GetExistingSystemManaged<LaneSystem>().Enabled = false;
            updateSystem.UpdateBefore<TrafficLaneSystem, LaneSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateAt<ModificationDataSyncSystem>(SystemUpdatePhase.Modification3);

            updateSystem.UpdateAt<ApplyLaneConnectionsSystem>(SystemUpdatePhase.ApplyTool);
            updateSystem.UpdateAt<LaneConnectorToolTooltipSystem>(SystemUpdatePhase.UITooltip);
            updateSystem.UpdateAt<ModRaycastSystem>(SystemUpdatePhase.Raycast);
            updateSystem.UpdateAt<LaneConnections.SearchSystem>(SystemUpdatePhase.Modification5);
            updateSystem.UpdateAfter<ValidationSystem, Game.Tools.ValidationSystem>(SystemUpdatePhase.ModificationEnd);
            
            updateSystem.UpdateAt<PriorityToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<LaneConnectorToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateBefore<GenerateConnectorsSystem, Game.Net.SearchSystem>(SystemUpdatePhase.Modification5);
        }

        public void OnDispose() {
            Logger.Info(nameof(OnDispose));
        }

        public void OnLoad() {
            Logger.Info(nameof(OnLoad));
        }
    }
}
