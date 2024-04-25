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
                        if (toolFeedbackInfo.container != Entity.Null && toolFeedbackInfo.type < FeedbackMessageType.ErrorHasRoundabout)
                        {
                            if (toolFeedbackInfo.type == FeedbackMessageType.WarnForbiddenTurnApply && nodeChunkData.Length > 0)
                            {
                                DrawNodeOutline(entities[i]);
                            }
                            if (toolFeedbackInfo.type == FeedbackMessageType.WarnResetForbiddenTurnUpgrades && prefabRefData.HasComponent(toolFeedbackInfo.container))
                            {
                                PrefabRef prefabRef = prefabRefData[toolFeedbackInfo.container];
                                Edge edge = edgeData[toolFeedbackInfo.container];
                                if (netGeometryData.HasComponent(prefabRef.m_Prefab) && editIntersectionsChunkData.Length > 0)
                                {
                                    EditIntersection editIntersection = editIntersectionsChunkData[i];
                                    bool isNearEnd = editIntersection.node == edge.m_End;
                                    EdgeGeometry edgeGeometry = edgeGeometryData[toolFeedbackInfo.container];
                                    Segment edgeSegment = !isNearEnd ? edgeGeometry.m_Start : edgeGeometry.m_End;
                                    DrawEdgeHalfOutline(edgeSegment);
                                }
                            }
                        }
                    }
                }
            }

            private void DrawEdgeHalfOutline(Segment edgeSegment)
            {
                Color color = new Color(1f, 0.65f, 0f, 1f);
                //start edge line
                overlayBuffer.DrawLine( color, color, 0, 0, new Line3.Segment(edgeSegment.m_Left.a, edgeSegment.m_Right.a), lineWidth);
                //left edge line
                overlayBuffer.DrawCurve(color, color, 0, 0, edgeSegment.m_Left, lineWidth, 1);
                //right edge line
                overlayBuffer.DrawCurve(color, color, 0, 0, edgeSegment.m_Right, lineWidth, 1);
                //middle edge cut line
                overlayBuffer.DrawLine( color, color, 0, 0, new Line3.Segment(edgeSegment.m_Left.d, edgeSegment.m_Right.d), lineWidth);
            }

            private void DrawNodeOutline(Entity node)
            {
                if (connectedEdgeData.HasBuffer(node))
                {
                    Color color = new Color(1f, 0.65f, 0f, 1f);
                    DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgeData[node];
                    for (var i = 0; i < connectedEdges.Length; i++)
                    {
                        ConnectedEdge edge = connectedEdges[i];
                        bool isNearEnd = node == edgeData[edge.m_Edge].m_End;
                        EdgeGeometry edgeGeometry = edgeGeometryData[edge.m_Edge];
                        Segment edgeSegment = !isNearEnd ? edgeGeometry.m_Start : edgeGeometry.m_End;
                        overlayBuffer.DrawLine( color, color, 0, 0, new Line3.Segment(math.select(edgeSegment.m_Left.a, edgeSegment.m_Left.d, isNearEnd), math.select(edgeSegment.m_Right.a, edgeSegment.m_Right.d, isNearEnd)), lineWidth, 1);
                        EdgeNodeGeometry edgeNodeGeometry = !isNearEnd ? startNodeGeometryData[edge.m_Edge].m_Geometry : endNodeGeometryData[edge.m_Edge].m_Geometry;
                        overlayBuffer.DrawCurve(color, color, 0, 0, edgeNodeGeometry.m_Left.m_Left, lineWidth, 1);
                        overlayBuffer.DrawCurve(color, color, 0, 0, edgeNodeGeometry.m_Right.m_Right, lineWidth, 1);
                    }
                }
            }
        }
    }
}
