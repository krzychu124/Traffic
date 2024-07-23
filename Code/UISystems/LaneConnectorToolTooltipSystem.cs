using System.Collections.Generic;
using Game.Common;
using Game.Tools;
using Game.UI.Localization;
using Game.UI.Tooltip;
using Traffic.CommonData;
using Traffic.Components;
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
        private CachedLocalizedStringBuilder<FeedbackMessageType> _feedbackStringBuilder;
        private ToolSystem _toolSystem;
        private LaneConnectorToolSystem _laneConnectorTool;
        private StringTooltip _tooltip;
        private List<StringTooltip> _feedbackTooltips;
        private StringTooltip _tooltipModifierState;
#if DEBUG_TOOL
        private StringTooltip _posTooltip;
        private StringTooltip _posTooltip2;
#endif
        private EntityQuery _errorQuery;

        protected override void OnCreate() {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _laneConnectorTool = World.GetExistingSystemManaged<LaneConnectorToolSystem>();
            _tooltip = new StringTooltip { path = "laneConnectorTool" };
            _feedbackTooltips = new List<StringTooltip>()
            {
                new() { path = $"{Mod.MOD_NAME}.FeedbackMessage_0" },
                new() { path = $"{Mod.MOD_NAME}.FeedbackMessage_1" },
                new() { path = $"{Mod.MOD_NAME}.FeedbackMessage_2" },
                new() { path = $"{Mod.MOD_NAME}.FeedbackMessage_3" },
                new() { path = $"{Mod.MOD_NAME}.FeedbackMessage_4" },
            };
            _tooltipModifierState = new StringTooltip() { path = "laneConnectorToolModifierState", color = TooltipColor.Success };
#if DEBUG_TOOL
            _posTooltip = new StringTooltip() { path = "laneConnectorToolPosition", color = TooltipColor.Warning, };
            _posTooltip2 = new StringTooltip() { path = "laneConnectorToolPosition2", color = TooltipColor.Warning, };
#endif
            _tooltipStringBuilder = CachedLocalizedStringBuilder<LaneConnectorToolSystem.Tooltip>.Id((LaneConnectorToolSystem.Tooltip t) => $"{Localization.GetToolTooltipLocaleID("LaneConnector", $"{t:G}")}");
            _modifierStringBuilder = CachedLocalizedStringBuilder<LaneConnectorToolSystem.StateModifier>.Id((LaneConnectorToolSystem.StateModifier t) => $"{Localization.GetToolTooltipLocaleID("LaneConnector", $"{t:G}")}");
            _feedbackStringBuilder = CachedLocalizedStringBuilder<FeedbackMessageType>.Id((FeedbackMessageType m) => $"{Localization.GetToolTooltipLocaleID("FeedbackMessage", $"{m:G}")}");
            _errorQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new []{ComponentType.ReadOnly<ToolFeedbackInfo>()},
                None = new []{ ComponentType.ReadOnly<Deleted>()},
            });
        }

        protected override void OnUpdate() {
            
            bool hasError = false;
            if (!_errorQuery.IsEmptyIgnoreFilter)
            {
                NativeArray<ArchetypeChunk> archetypeChunks = _errorQuery.ToArchetypeChunkArray(Allocator.Temp);
                BufferTypeHandle<ToolFeedbackInfo> feedbackBufferType = SystemAPI.GetBufferTypeHandle<ToolFeedbackInfo>(true);

                int usedTooltips = 0;
                bool warningAdded = false;
                foreach (ArchetypeChunk chunk in archetypeChunks)
                {
                    if (hasError || usedTooltips > 5)
                    {
                        break;
                    }
                    BufferAccessor<ToolFeedbackInfo> feedbackInfoAccessor = chunk.GetBufferAccessor(ref feedbackBufferType);
                    for (var i = 0; i < feedbackInfoAccessor.Length; i++)
                    {
                        DynamicBuffer<ToolFeedbackInfo> feedbackInfos = feedbackInfoAccessor[i];
                        for (var j = 0; j < feedbackInfos.Length; j++)
                        {
                            if (usedTooltips++ > 5 || warningAdded || hasError)
                            {
                                break;
                            }
                            FeedbackMessageType messageType = feedbackInfos[j].type;
                            bool isError = messageType >= FeedbackMessageType.ErrorHasRoundabout;
                            StringTooltip tooltip = _feedbackTooltips[usedTooltips];
                            tooltip.icon = "coui://ui-mods/traffic-images/traffic_icon.svg";
                            tooltip.value = _feedbackStringBuilder[messageType];
                            tooltip.color = isError ? TooltipColor.Error : TooltipColor.Warning;
                            AddMouseTooltip(tooltip);
                            hasError |= isError;
                            warningAdded |= !isError;
                        }
                    }
                }
                
                archetypeChunks.Dispose();
            }

            if (hasError || _toolSystem.activeTool != _laneConnectorTool)
            {
                return;
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
                _tooltipModifierState.value = _modifierStringBuilder[_laneConnectorTool.ToolModifiers];
                AddMouseTooltip(_tooltipModifierState);
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
                case LaneConnectorToolSystem.Tooltip.UTurnTrackNotAllowed:
                    return TooltipColor.Error;
                case LaneConnectorToolSystem.Tooltip.CreateConnection:
                case LaneConnectorToolSystem.Tooltip.CompleteConnection:
                    return TooltipColor.Success;
            }
            return TooltipColor.Info;
        }
    }
}
