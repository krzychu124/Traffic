using System;
using Colossal.Entities;
using Game.Common;
using Game.Tools;
using Game.UI.Localization;
using Game.UI.Tooltip;
using Traffic.Components;
using Traffic.LaneConnections;
using Traffic.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.UISystems
{
    public partial class LaneConnectorToolTooltipSystem : TooltipSystemBase
    {
        private CachedLocalizedStringBuilder<LaneConnectorToolSystem.Tooltip> _tooltipStringBuilder;
        private CachedLocalizedStringBuilder<LaneConnectorToolSystem.StateModifier> _modifierStringBuilder;
        private ToolSystem _toolSystem;
        private LaneConnectorToolSystem _laneConnectorTool;
        // private NetToolSystem _netTool;
        private StringTooltip _tooltip;
        private StringTooltip _tooltipWarnings;
        private StringTooltip _tooltipErrors;
        private StringTooltip _tooltipInfo;
        private StringTooltip _tooltipDebug;
#if DEBUG_TOOL
        private StringTooltip _posTooltip;
        private StringTooltip _posTooltip2;
#endif
        private EntityQuery _warnQuery;
        private EntityQuery _errorQuery;

        protected override void OnCreate() {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _laneConnectorTool = World.GetExistingSystemManaged<LaneConnectorToolSystem>();
            _tooltip = new StringTooltip { path = "laneConnectorTool" };
            _tooltipInfo = new StringTooltip { path = "laneConnectorToolInfo" };
            _tooltipDebug = new StringTooltip() { path = "laneConnectorToolDebug", color = TooltipColor.Success };
            _tooltipWarnings = new StringTooltip() { path = "laneConnectorToolWarnings", color = TooltipColor.Warning, };
            _tooltipErrors = new StringTooltip() { path = "laneConnectorToolErrors", color = TooltipColor.Error, };
#if DEBUG_TOOL
            _posTooltip = new StringTooltip() { path = "laneConnectorToolPosition", color = TooltipColor.Warning, };
            _posTooltip2 = new StringTooltip() { path = "laneConnectorToolPosition2", color = TooltipColor.Warning, };
#endif
            _tooltipStringBuilder = CachedLocalizedStringBuilder<LaneConnectorToolSystem.Tooltip>.Id((LaneConnectorToolSystem.Tooltip t) => $"{Mod.MOD_NAME}.Tools.Tooltip[{t:G}]");
            _modifierStringBuilder = CachedLocalizedStringBuilder<LaneConnectorToolSystem.StateModifier>.Id((LaneConnectorToolSystem.StateModifier t) => $"{Mod.MOD_NAME}.Tools.Tooltip[{t:G}]");
            _warnQuery = GetEntityQuery(ComponentType.ReadOnly<EditIntersection>(), ComponentType.ReadOnly<WarnResetUpgrade>(), ComponentType.Exclude<Deleted>());
            _errorQuery = GetEntityQuery(ComponentType.ReadOnly<EditIntersection>(), ComponentType.ReadOnly<Error>(), ComponentType.Exclude<Deleted>());
        }

        protected override void OnUpdate() {
            if (_toolSystem.activeTool != _laneConnectorTool)
            {
                return;
            }
            
            if (_laneConnectorTool.ToolMode == LaneConnectorToolSystem.Mode.Default)
            {
                if (!_errorQuery.IsEmptyIgnoreFilter)
                {
                    _tooltipErrors.value = "Modifying lane connections on selected intersection is not supported";
                    AddMouseTooltip(_tooltipErrors);
                    return;
                } 
                else if(!_warnQuery.IsEmptyIgnoreFilter)
                {
                    _tooltipWarnings.value = "Entering modification mode will remove all Forbidden maneuvers";
                    AddMouseTooltip(_tooltipWarnings);
                }
            }
            if (_laneConnectorTool.ToolState == LaneConnectorToolSystem.State.SelectingSourceConnector &&
                _laneConnectorTool.SelectedNode != Entity.Null && 
                EntityManager.TryGetBuffer(_laneConnectorTool.SelectedNode, true, out DynamicBuffer<ModifiedLaneConnections> connections) &&
                !connections.IsEmpty)
            {
                _tooltipInfo.value = "Press Delete to reset Lane Connections";
                AddMouseTooltip(_tooltipInfo);
            }
            
#if DEBUG_TOOL
            if (_laneConnectorTool.ToolState > LaneConnectorToolSystem.State.Default)
            {
                NativeList<ControlPoint> controlPoints = _laneConnectorTool.GetControlPoints(out JobHandle _);
                if (controlPoints.Length > 0)
                {
                    _posTooltip.value = $"[0] {controlPoints[0].m_OriginalEntity} ({controlPoints[0].m_HitPosition})";
                    AddMouseTooltip(_posTooltip);

                    if (controlPoints.Length > 1)
                    {
                        _posTooltip2.value = $"[1] {controlPoints[1].m_OriginalEntity} ({controlPoints[1].m_HitPosition})";
                        AddMouseTooltip(_posTooltip2);
                    }
                }
            }
#endif
            if (_laneConnectorTool.tooltip != LaneConnectorToolSystem.Tooltip.None)
            {
                _tooltip.value = _tooltipStringBuilder[_laneConnectorTool.tooltip];
                _tooltip.color = GetColor(_laneConnectorTool.tooltip);
                AddMouseTooltip(_tooltip);
            }
            if (_laneConnectorTool.ToolModifiers != LaneConnectorToolSystem.StateModifier.AnyConnector && _laneConnectorTool.tooltip != LaneConnectorToolSystem.Tooltip.RemoveConnection)
            {
                _tooltipDebug.value = _modifierStringBuilder[_laneConnectorTool.ToolModifiers];
                AddMouseTooltip(_tooltipDebug);
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
                    return TooltipColor.Error;
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
