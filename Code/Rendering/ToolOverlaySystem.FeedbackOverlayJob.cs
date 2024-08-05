using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Traffic.CommonData;
using Traffic.Components;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Traffic.Rendering
{
    public partial class ToolOverlaySystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct FeedbackOverlayJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public BufferTypeHandle<ToolFeedbackInfo> feedbackInfoTypeHandle;
            [ReadOnly] public ComponentTypeHandle<EditIntersection> editIntersectionTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Node> nodeTypeHandle;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgeData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Game.Net.EdgeGeometry> edgeGeometryData;
            [ReadOnly] public ComponentLookup<Game.Net.StartNodeGeometry> startNodeGeometryData;
            [ReadOnly] public ComponentLookup<Game.Net.EndNodeGeometry> endNodeGeometryData;
            [ReadOnly] public ComponentLookup<NetGeometryData> netGeometryData;
            [ReadOnly] public ComponentLookup<PrefabRef> prefabRefData;
            [ReadOnly] public float lineWidth;
            public OverlayRenderSystem.Buffer overlayBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                BufferAccessor<ToolFeedbackInfo> feedbackBufferAccessor = chunk.GetBufferAccessor(ref feedbackInfoTypeHandle);
                NativeArray<Node> nodeChunkData = chunk.GetNativeArray(ref nodeTypeHandle);
                NativeArray<EditIntersection> editIntersectionsChunkData = chunk.GetNativeArray(ref editIntersectionTypeHandle);

                for (int i = 0; i < feedbackBufferAccessor.Length; i++)
                {
                    DynamicBuffer<ToolFeedbackInfo> feedbackInfos = feedbackBufferAccessor[i];
                    for (int j = 0; j < feedbackInfos.Length; j++)
                    {
                        ToolFeedbackInfo toolFeedbackInfo = feedbackInfos[j];
                        if (toolFeedbackInfo.container != Entity.Null && toolFeedbackInfo.type < FeedbackMessageType.ErrorLaneConnectorNotSupported)
                        {
                            if ((toolFeedbackInfo.type == FeedbackMessageType.WarnForbiddenTurnApply || 
                                toolFeedbackInfo.type == FeedbackMessageType.WarnResetPrioritiesTrafficLightsApply ||
                                toolFeedbackInfo.type == FeedbackMessageType.WarnResetPrioritiesRoundaboutApply||
                                toolFeedbackInfo.type == FeedbackMessageType.WarnResetPrioritiesChangeApply) &&
                                nodeChunkData.Length > 0)
                            {
                                OverlayRenderingHelpers.DrawNodeOutline(
                                    entities[i],
                                    ref connectedEdgeData,
                                    ref startNodeGeometryData,
                                    ref endNodeGeometryData,
                                    ref edgeData,
                                    ref edgeGeometryData,
                                    ref overlayBuffer,
                                    new Color(1f, 0.65f, 0f, 1f),
                                    lineWidth,
                                    0f
                                );
                            }
                            if (toolFeedbackInfo.type == FeedbackMessageType.WarnResetForbiddenTurnUpgrades &&
                                prefabRefData.HasComponent(toolFeedbackInfo.container))
                            {
                                PrefabRef prefabRef = prefabRefData[toolFeedbackInfo.container];
                                Edge edge = edgeData[toolFeedbackInfo.container];
                                if (netGeometryData.HasComponent(prefabRef.m_Prefab) && editIntersectionsChunkData.Length > 0)
                                {
                                    EditIntersection editIntersection = editIntersectionsChunkData[i];
                                    bool isNearEnd = editIntersection.node == edge.m_End;
                                    EdgeGeometry edgeGeometry = edgeGeometryData[toolFeedbackInfo.container];
                                    Segment edgeSegment = !isNearEnd ? edgeGeometry.m_Start : edgeGeometry.m_End;
                                    OverlayRenderingHelpers.DrawEdgeHalfOutline(
                                        edgeSegment,
                                        ref overlayBuffer,
                                        new Color(1f, 0.65f, 0f, 1f),
                                        lineWidth
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
