using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Notifications;
using Game.Pathfind;
using Game.Prefabs;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Edge = Game.Net.Edge;
using SubLane = Game.Net.SubLane;
using TrackLane = Game.Net.TrackLane;

namespace Traffic.Tools
{
#if WITH_BURST
    [BurstCompile]
#endif
    public partial class ValidationSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct ValidateLaneConnectorTool : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<EditIntersection> editIntersectionType;
            [ReadOnly] public ComponentTypeHandle<Temp> tempType;
            [ReadOnly] public BufferTypeHandle<ModifiedLaneConnections> modifiedLaneConnectionsType;
            [ReadOnly] public ComponentLookup<Temp> tempData;
            [ReadOnly] public ComponentLookup<Upgraded> upgradedData;
            [ReadOnly] public ComponentLookup<Deleted> deletedData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Composition> compositionData;
            [ReadOnly] public ComponentLookup<NetCompositionData> netCompositionData;
            [ReadOnly] public ComponentLookup<TrackLane> trackLaneData;
            [ReadOnly] public ComponentLookup<TrackLaneData> trackLanePrefabData;
            [ReadOnly] public ComponentLookup<Lane> laneData;
            [ReadOnly] public ComponentLookup<Curve> curveData;
            [ReadOnly] public ComponentLookup<PrefabRef> prefabRefData;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgesBuffer;
            [ReadOnly] public BufferLookup<SubLane> subLaneBuffer;
            [ReadOnly] public Entity tightCurvePrefabEntity;
            public EntityCommandBuffer commandBuffer;
            public IconCommandBuffer iconCommandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                NativeArray<EditIntersection> editIntersections = chunk.GetNativeArray(ref editIntersectionType);
                NativeArray<Temp> temps = chunk.GetNativeArray(ref tempType);
                NativeList<ToolFeedbackInfo> feedbackInfos = new NativeList<ToolFeedbackInfo>(Allocator.Temp);
                BufferAccessor<ModifiedLaneConnections> modifiedConnectionsBuffer = chunk.GetBufferAccessor(ref modifiedLaneConnectionsType);

                bool hasModifiedConnections = chunk.Has(ref modifiedLaneConnectionsType);
                bool hasWarningIconEntity = tightCurvePrefabEntity != Entity.Null;
                
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Temp temp = temps[i];
                    Entity nodeEntity = Entity.Null;
                    int numConnections = hasModifiedConnections ? modifiedConnectionsBuffer[i].Length : 0;
                    if (chunk.Has(ref editIntersectionType))
                    {
                        EditIntersection e = editIntersections[i];
                        nodeEntity = e.node;
                    }
                    else if (hasModifiedConnections)
                    {
                        nodeEntity = entity;
                        
                        if (hasWarningIconEntity && subLaneBuffer.HasBuffer(nodeEntity))
                        {
                            DynamicBuffer<SubLane> subLanes = subLaneBuffer[nodeEntity];
                            for (var j = 0; j < subLanes.Length; j++)
                            {
                                SubLane subLane = subLanes[j];
                                if ((subLane.m_PathMethods & PathMethod.Track) != 0 &&
                                    trackLaneData.HasComponent(subLane.m_SubLane))
                                {
                                    Lane lane = laneData[subLane.m_SubLane];
                                    if (lane.m_StartNode.OwnerEquals(lane.m_EndNode))
                                    {
                                        commandBuffer.AddComponent<Error>(nodeEntity);
                                        commandBuffer.AddComponent<BatchesUpdated>(nodeEntity);
                                        break;
                                    }
                                    
                                    PrefabRef prefabRef = prefabRefData[subLane.m_SubLane];
                                    TrackLane trackLane = trackLaneData[subLane.m_SubLane];
                                    TrackLaneData track = trackLanePrefabData[prefabRef.m_Prefab];
                                    if (trackLane.m_Curviness > track.m_MaxCurviness)
                                    {
                                        Curve curve = curveData[subLane.m_SubLane];
                                        float3 position = MathUtils.Position(curve.m_Bezier, 0.5f);
                                        iconCommandBuffer.Add(subLane.m_SubLane, tightCurvePrefabEntity, position, IconPriority.Warning, IconClusterLayer.Default, (IconFlags)0, Entity.Null, isTemp: true);
                                        commandBuffer.AddComponent(nodeEntity, default(Warning));
                                        commandBuffer.AddComponent(nodeEntity, default(BatchesUpdated));
                                    }
                                }
                            }
                        }
                    }
                    
                    if (nodeEntity != Entity.Null && connectedEdgesBuffer.HasBuffer(nodeEntity))
                    {
                        DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgesBuffer[nodeEntity];
                        for (int j = 0; j < connectedEdges.Length; j++)
                        {
                            ConnectedEdge connectedEdge = connectedEdges[j];
                            if (j == 0)
                            {
                                // check node composition if is roundabout (node is start or end of connectedEdge),
                                // "Edge" entity holds info about network Composition (edge, startNode, endNode)
                                Edge edge = edgeData[connectedEdge.m_Edge];
                                bool? isStartNode = math.any(new bool2(edge.m_Start.Equals(nodeEntity), edge.m_End.Equals(nodeEntity))) ? edge.m_Start.Equals(nodeEntity) : null;
                                if (isStartNode.HasValue && compositionData.HasComponent(connectedEdge.m_Edge))
                                {
                                    Composition composition = compositionData[connectedEdge.m_Edge];
                                    CompositionFlags compositionFlags = netCompositionData[isStartNode.Value ? composition.m_StartNode : composition.m_EndNode].m_Flags;
                                    if ((compositionFlags.m_General &  CompositionFlags.General.Roundabout) != 0)
                                    {
                                        if (temp.m_Original != Entity.Null)
                                        {
                                            commandBuffer.AddComponent<Error>(entity);
                                            commandBuffer.AddComponent<BatchesUpdated>(entity);
                                            
                                            //highlight and block node modification
                                            commandBuffer.AddComponent<Error>(temp.m_Original);
                                            commandBuffer.AddComponent<BatchesUpdated>(temp.m_Original);
                                            feedbackInfos.Add(new ToolFeedbackInfo() { type = hasModifiedConnections ? FeedbackMessageType.ErrorApplyRoundabout : FeedbackMessageType.ErrorHasRoundabout});
                                        }
                                    }
                                }
                            }
                            
                            if (upgradedData.HasComponent(connectedEdge.m_Edge) && !deletedData.HasComponent(connectedEdge.m_Edge))
                            {
                                Upgraded upgraded = upgradedData[connectedEdge.m_Edge];
                                Edge edge = edgeData[connectedEdge.m_Edge];
                                CompositionFlags.Side side = edge.m_Start == nodeEntity ? upgraded.m_Flags.m_Left : upgraded.m_Flags.m_Right;
                                if ((side & (CompositionFlags.Side.ForbidStraight | CompositionFlags.Side.ForbidLeftTurn | CompositionFlags.Side.ForbidRightTurn)) != 0)
                                {
                                    bool hadUpgrade = false;
                                    if (tempData.HasComponent(connectedEdge.m_Edge))
                                    {
                                        Temp tempConnEdge = tempData[connectedEdge.m_Edge];
                                        if (tempConnEdge.m_Original!= Entity.Null &&
                                            tempConnEdge.m_Flags != 0 &&
                                            upgradedData.HasComponent(tempConnEdge.m_Original))
                                        {
                                            Edge oldEdge = edgeData[tempConnEdge.m_Original];
                                            Upgraded oldUpgradedEdge = upgradedData[tempConnEdge.m_Original];
                                            hadUpgrade = ((oldEdge.m_Start == temp.m_Original ? oldUpgradedEdge.m_Flags.m_Left : oldUpgradedEdge.m_Flags.m_Right) & (CompositionFlags.Side.ForbidStraight | CompositionFlags.Side.ForbidLeftTurn | CompositionFlags.Side.ForbidRightTurn)) != 0;
                                        }
                                    }
                                    else
                                    {
                                        feedbackInfos.Add(new ToolFeedbackInfo() {container = connectedEdge.m_Edge, type = FeedbackMessageType.WarnResetForbiddenTurnUpgrades});
                                    }
                                    
                                    if (!hadUpgrade && hasModifiedConnections)
                                    {
                                        feedbackInfos.Add(new ToolFeedbackInfo() {container = connectedEdge.m_Edge, type = FeedbackMessageType.WarnForbiddenTurnApply});
                                    }
                                    else if (hadUpgrade && numConnections > 0)
                                    {
                                        feedbackInfos.Add(new ToolFeedbackInfo() {container = connectedEdge.m_Edge, type = FeedbackMessageType.WarnResetForbiddenTurnUpgrades});
                                    }
                                }
                            }
                        }
                    }

                    if (feedbackInfos.Length > 0)
                    {
                        DynamicBuffer<ToolFeedbackInfo> feedbackBuffer = commandBuffer.AddBuffer<ToolFeedbackInfo>(entity);
                        feedbackBuffer.CopyFrom(feedbackInfos.AsArray());
                        feedbackInfos.Clear();
                    }
                }
                feedbackInfos.Dispose();
            }
        }
    }
}
