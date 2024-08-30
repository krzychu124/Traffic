using System;
using Colossal.Mathematics;
using Game.Rendering;
using Game.Tools;
using Traffic.Components.LaneConnections;
using Traffic.Components.PrioritySigns;
using Traffic.Tools;
using Traffic.UISystems;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Traffic.Rendering
{
    public partial class ToolOverlaySystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct PriorityOverlaysJob : IJob
        {
            [ReadOnly] public NativeArray<ArchetypeChunk> chunks;
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<LaneHandle> laneHandleTypeHandle;
            [ReadOnly] public PriorityToolSystem.State state;
            [ReadOnly] public ModUISystem.PriorityToolSetMode setMode;
            [ReadOnly] public ModUISystem.OverlayMode overlayMode;
            [ReadOnly] public NativeList<ControlPoint> controlPoints;
            [ReadOnly] public ComponentLookup<LaneHandle> laneHandleData;
            [ReadOnly] public bool alwaysShowConnections;
            [ReadOnly] public BufferLookup<Connection> connectionBufferData;
            public OverlayRenderSystem.Buffer overlayBuffer;

            public void Execute()
            {
                if (state != PriorityToolSystem.State.ChangingPriority)
                {
                    return;
                }

                NativeList<ValueTuple<Entity, LaneHandle>> hoveredHandles = new NativeList<ValueTuple<Entity, LaneHandle>>(4, Allocator.Temp);
                ControlPoint controlPoint = controlPoints.Length > 0 ? controlPoints[0] : new ControlPoint();
                LaneHandle laneHandle = default;
                bool isHovering = controlPoint.m_OriginalEntity != Entity.Null && laneHandleData.TryGetComponent(controlPoint.m_OriginalEntity, out laneHandle);
                int laneGroup = isHovering && overlayMode == ModUISystem.OverlayMode.LaneGroup ? laneHandle.handleGroup : -1;
                
                for (var i = 0; i < chunks.Length; i++)
                {
                    ArchetypeChunk chunk = chunks[i];
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                    NativeArray<LaneHandle> laneHandles = chunk.GetNativeArray(ref laneHandleTypeHandle);
                    
                    for (var j = 0; j < laneHandles.Length; j++)
                    {
                        if (entities[j].Equals(controlPoint.m_OriginalEntity))
                        {
                            hoveredHandles.Add(new ValueTuple<Entity, LaneHandle>(entities[j], laneHandles[j]));
                            continue;
                        }
                        LaneHandle handle = laneHandles[j];

                        if (overlayMode == ModUISystem.OverlayMode.LaneGroup &&
                            isHovering &&
                            laneGroup == handle.handleGroup &&
                            handle.edge == laneHandle.edge)
                        {
                            hoveredHandles.Add(new ValueTuple<Entity, LaneHandle>(entities[j], laneHandles[j]));
                            continue;
                        }
                        Color color = GetPriorityColor(handle.originalPriority) * (isHovering ? new Color(1,1,1, 0.5f) : Color.white);
                        bool isDiffPriority = handle.priority != handle.originalPriority;
                        OverlayRenderingHelpers.DrawEdgeHalfOutline(handle.laneSegment, ref overlayBuffer, color, isHovering ? 0.1f : 0.14f, isDashed: isDiffPriority);
                        if (isDiffPriority)
                        {
                            color = GetPriorityColor(handle.priority) * (isHovering ? new Color(1, 1, 1, 0.5f) : Color.white);
                            var middleCurve = MathUtils.Cut(handle.curve, new float2(0, handle.laneSegment.middleLength / handle.length));
                            overlayBuffer.DrawCurve(color, color, 0f, 0, middleCurve, 0.2f);
                        }
                        if (alwaysShowConnections && !isHovering)
                        {
                            DrawConnections(entities[j], new Color(1,1,1,0), color, isDiffPriority);
                        }
                    }
                }
                
                if (!isHovering || hoveredHandles.IsEmpty)
                {
                    return;
                }


                Color colorHover = new Color(1,1,1,0);
                Color colorOutline = new Color(0f, 0.83f, 1f, 1f);
                switch (setMode)
                {
                    case ModUISystem.PriorityToolSetMode.None:
                    case ModUISystem.PriorityToolSetMode.Reset:
                        colorOutline = Color.white;
                        break;
                    case ModUISystem.PriorityToolSetMode.Yield:
                        colorOutline = GetPriorityColor(PriorityType.Yield);
                        break;
                    case ModUISystem.PriorityToolSetMode.Stop:
                        colorOutline = GetPriorityColor(PriorityType.Stop);
                        break;
                    case ModUISystem.PriorityToolSetMode.Priority:
                        colorOutline = GetPriorityColor(PriorityType.RightOfWay);
                        break;
                }
                
                foreach (ValueTuple<Entity, LaneHandle> hoveredHandle in hoveredHandles)
                {
                    bool isDashed = hoveredHandle.Item2.priority != hoveredHandle.Item2.originalPriority;
                    OverlayRenderingHelpers.DrawEdgeHalfOutline(hoveredHandle.Item2.laneSegment, ref overlayBuffer, colorOutline, 0.2f, isDashed);
                    DrawConnections(hoveredHandle.Item1, colorHover, colorOutline, isDashed);
                }
                
                hoveredHandles.Dispose();
            }

            private void DrawConnections(Entity hoveredHandle, Color32 colorHover, Color32 colorOutline, bool isDashed)
            {
                if (connectionBufferData.HasBuffer(hoveredHandle))
                {
                    DynamicBuffer<Connection> connections = connectionBufferData[hoveredHandle];
                    if (isDashed)
                    {
                        for (var i = 0; i < connections.Length; i++)
                        {
                            overlayBuffer.DrawDashedCurve(colorOutline, colorHover, 0.2f, 0, connections[i].curve, 2.8f, 2, 0.4f);
                        }
                    }
                    else
                    {
                        for (var i = 0; i < connections.Length; i++)
                        {
                            overlayBuffer.DrawCurve(colorOutline, colorHover, 0.2f, 0, connections[i].curve, 2.8f, new float2(0.5f));
                        }
                    }
                }
            }

            private Color GetPriorityColor(PriorityType type)
            {
                switch (type)
                {
                    case PriorityType.RightOfWay:
                        return new Color(0f, 0.75f, 0.1f);;
                    case PriorityType.Yield:
                        return new Color(1f, 0.56f, 0.01f); 
                    case PriorityType.Stop:
                        return new Color(0.82f, 0.25f, 0.16f);
                    case PriorityType.Default:
                    default:
                        return new Color(0f, 0.83f, 1f, 1f);
                }
            }
        }
    }
}
