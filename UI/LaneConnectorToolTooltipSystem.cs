using Game.Common;
using Game.Tools;
using Game.UI.Localization;
using Game.UI.Tooltip;
using Traffic.Components;
using Traffic.LaneConnections;
using Traffic.Tools;
using Unity.Entities;

namespace Traffic.UI
{
    public partial class LaneConnectorToolTooltipSystem : TooltipSystemBase
    {
        private CachedLocalizedStringBuilder<LaneConnectorToolSystem.Tooltip> _stringBuilder;
        private ToolSystem _toolSystem;
        private LaneConnectorToolSystem _laneConnectorTool;
        // private NetToolSystem _netTool;
        private StringTooltip _tooltip;
        private StringTooltip _tooltipWarnings;
        private StringTooltip _tooltipDebug;
        private EntityQuery _warnQuery;
        // private NetPieceRequirements[] _warnRequirements = new[]
        // {
        //     NetPieceRequirements.ForbidStraight, NetPieceRequirements.ForbidLeftTurn, NetPieceRequirements.ForbidRightTurn, 
        //     NetPieceRequirements.OppositeForbidStraight, NetPieceRequirements.OppositeForbidLeftTurn, NetPieceRequirements.OppositeForbidRightTurn
        // };

        protected override void OnCreate() {
            base.OnCreate();
            _toolSystem = World.GetExistingSystemManaged<ToolSystem>();
            _laneConnectorTool = World.GetExistingSystemManaged<LaneConnectorToolSystem>();
            // _netTool = World.GetExistingSystemManaged<NetToolSystem>();
            _tooltip = new StringTooltip
            {
                path = "laneConnectorTool"
            };
            _tooltipDebug = new StringTooltip()
            {
                path = "laneConnectorToolDebug",
                color = TooltipColor.Success
            };
            _tooltipWarnings = new StringTooltip()
            {
                path = "laneConnectorToolWarnings",
                color = TooltipColor.Warning,
            };
            _stringBuilder = CachedLocalizedStringBuilder<LaneConnectorToolSystem.Tooltip>.Id((LaneConnectorToolSystem.Tooltip t) => $"Tools.INFO[{t:G}]");
            _warnQuery = GetEntityQuery(ComponentType.ReadOnly<EditIntersection>(), ComponentType.ReadOnly<WarnResetUpgrade>(), ComponentType.Exclude<Deleted>());
        }

        protected override void OnUpdate() {
            // TODO create feedback system
            // if (_toolSystem.activeTool == _netTool && _netTool.prefab && _netTool.prefab.Has<NetUpgrade>() && MatchingRequirement(_netTool.prefab.GetComponent<NetUpgrade>().m_SetState))
            // {
            //     _tooltip.value = "Applying upgrade may reset Traffic mod modifications";
            //     _tooltip.color = TooltipColor.Warning;
            //     AddMouseTooltip(_tooltip);
            // }
            
            if (_toolSystem.activeTool != _laneConnectorTool || (_laneConnectorTool.tooltip == LaneConnectorToolSystem.Tooltip.None && _laneConnectorTool.ToolModifiers == LaneConnectorToolSystem.StateModifier.AnyConnector))
            {
                return;
            }
            if (_laneConnectorTool.tooltip != LaneConnectorToolSystem.Tooltip.None)
            {
                _tooltip.value = _stringBuilder[_laneConnectorTool.tooltip];
                _tooltip.color = GetColor(_laneConnectorTool.tooltip);
                AddMouseTooltip(_tooltip);
            }
            if (_laneConnectorTool.ToolModifiers != LaneConnectorToolSystem.StateModifier.AnyConnector)
            {
                _tooltipDebug.value = $"Current mode: {_laneConnectorTool.ToolModifiers.ToString()}"; //todo replace with CachedLocalizedStringBuilder
                AddMouseTooltip(_tooltipDebug);
            }
            if (_laneConnectorTool.ToolMode == LaneConnectorToolSystem.Mode.Default && !_warnQuery.IsEmptyIgnoreFilter)
            {
                _tooltipWarnings.value = "Entering modification mode will remove all Forbidden maneuvers";
                AddMouseTooltip(_tooltipWarnings);
            }
        }

        private TooltipColor GetColor(LaneConnectorToolSystem.Tooltip tooltip) {
            switch (tooltip)
            {
                case LaneConnectorToolSystem.Tooltip.SelectIntersection:
                case LaneConnectorToolSystem.Tooltip.SelectConnectorToAddOrRemove:
                case LaneConnectorToolSystem.Tooltip.ModifyConnections:
                    return TooltipColor.Info;
                case LaneConnectorToolSystem.Tooltip.RemoveSourceConnections:
                case LaneConnectorToolSystem.Tooltip.RemoveTargetConnections:
                case LaneConnectorToolSystem.Tooltip.RemoveConnection:
                    return TooltipColor.Warning;
                case LaneConnectorToolSystem.Tooltip.CreateConnection:
                case LaneConnectorToolSystem.Tooltip.CompleteConnection:
                    return TooltipColor.Success;
            }
            return TooltipColor.Info;
        }

        // TODO use in feedback system
        // private bool MatchingRequirement(NetPieceRequirements[] requirements) {
        //     for (var i = 0; i < requirements.Length; i++)
        //     {
        //         for (var j = 0; j < _warnRequirements.Length; j++)
        //         {
        //             if (requirements[i] == _warnRequirements[j])
        //             {
        //                 return true;
        //             }   
        //         }
        //     }
        //     return false;
        // }
    }
}
