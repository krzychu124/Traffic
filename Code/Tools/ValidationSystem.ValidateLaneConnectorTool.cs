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
using Traffic.Components.PrioritySigns;
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
    public partial class ValidationSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct ValidateLaneConnectorTool : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<EditIntersection> editIntersectionType;
            [ReadOnly] public ComponentTypeHandle<ToolActionBlocked> toolActionBlockedType;
            [ReadOnly] public ComponentTypeHandle<Temp> tempType;
            [ReadOnly] public ComponentTypeHandle<Edge> edgeType;
            [ReadOnly] public BufferTypeHandle<ModifiedLaneConnections> modifiedLaneConnectionsType;
            [ReadOnly] public BufferTypeHandle<LanePriority> lanePriorityTypeHandle;
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
            [ReadOnly] public ComponentLookup<ToolManaged> toolManagedData;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgesBuffer;
            [ReadOnly] public BufferLookup<LanePriority> lanePriorityBuffer;
            [ReadOnly] public BufferLookup<SubLane> subLaneBuffer;
            [ReadOnly] public BufferLookup<GeneratedConnection> generatedConnectionsBuffer;
            [ReadOnly] public Entity tightCurvePrefabEntity;
            [ReadOnly] public bool priorityToolActive;
            [ReadOnly] public bool leftHandTraffic;
            public EntityCommandBuffer commandBuffer;
            public IconCommandBuffer iconCommandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                NativeArray<Temp> temps = chunk.GetNativeArray(ref tempType);
                NativeArray<EditIntersection> editIntersections = chunk.GetNativeArray(ref editIntersectionType);
                BufferAccessor<ModifiedLaneConnections> modifiedConnectionsBuffer = chunk.GetBufferAccessor(ref modifiedLaneConnectionsType);    

                bool hasBlocked = chunk.Has(ref toolActionBlockedType);
                bool hasEditIntersection = chunk.Has(ref editIntersectionType);
                bool hasModifiedConnections = chunk.Has(ref modifiedLaneConnectionsType);

                NativeList<ToolFeedbackInfo> feedbackInfos = new NativeList<ToolFeedbackInfo>(Allocator.Temp);

                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Temp temp = temps[i];
                    if (hasModifiedConnections && 
                        temp.m_Original != Entity.Null && 
                        toolManagedData.HasComponent(temp.m_Original))
                    {
                        if (CheckForTrackUTurn(entity, modifiedConnectionsBuffer[i]))
                        {
                            commandBuffer.AddComponent<ToolActionBlocked>(entity);
                        }
                        else if (hasBlocked)
                        {
                            commandBuffer.RemoveComponent<ToolActionBlocked>(entity);
                        }
                    } 
                    else if (hasEditIntersection)
                    {
                        Entity nodeEntity = editIntersections[i].node;
                        if (nodeEntity != Entity.Null)
                        {
                            if (CheckEditIntersection(entity, nodeEntity, temps[i], ref feedbackInfos))
                            {
                                commandBuffer.AddComponent<ToolActionBlocked>(entity);
                            }
                            else if (hasBlocked)
                            {
                                commandBuffer.RemoveComponent<ToolActionBlocked>(entity);
                            }
                            if (feedbackInfos.Length > 0)
                            {
                                DynamicBuffer<ToolFeedbackInfo> feedbackBuffer = commandBuffer.AddBuffer<ToolFeedbackInfo>(entity);
                                feedbackBuffer.CopyFrom(feedbackInfos.AsArray());
                                feedbackInfos.Clear();
                            }
                        }
                    }
                }
                
                if (!chunk.Has(ref lanePriorityTypeHandle))
                {
                    for (int i = 0; i < entities.Length; i++)
                    {
                        Entity entity = entities[i];
                        Temp temp = temps[i];
                        int numConnections = hasModifiedConnections ? modifiedConnectionsBuffer[i].Length : 0;

                        if ((temp.m_Flags & TempFlags.Delete) != 0)
                        {
                            continue;
                        }

                        bool hasFeedbackError = false;
                        if (connectedEdgesBuffer.HasBuffer(entity))
                        {
                            DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgesBuffer[entity];
                            for (int j = 0; j < connectedEdges.Length; j++)
                            {
                                ConnectedEdge connectedEdge = connectedEdges[j];
                                if (j == 0)
                                {
                                    // check node composition if is roundabout (node is start or end of connectedEdge),
                                    // "Edge" entity holds info about network Composition (edge, startNode, endNode)

                                    if ((temp.m_Flags & TempFlags.Essential) != 0)
                                    {
                                        Edge edge = edgeData[connectedEdge.m_Edge];
                                        bool hasPriorities = lanePriorityBuffer.HasBuffer(connectedEdge.m_Edge);
                                        bool? isStartNode = math.any(new bool2(edge.m_Start.Equals(entity), edge.m_End.Equals(entity))) ? edge.m_Start.Equals(entity) : null;
                                        if (isStartNode.HasValue && compositionData.HasComponent(connectedEdge.m_Edge))
                                        {
                                            Composition composition = compositionData[connectedEdge.m_Edge];
                                            CompositionFlags compositionFlags = netCompositionData[isStartNode.Value ? composition.m_StartNode : composition.m_EndNode].m_Flags;
                                            if ((compositionFlags.m_General & CompositionFlags.General.Roundabout) != 0 && temp.m_Original != Entity.Null)
                                            {
                                                if (tempData.HasComponent(connectedEdge.m_Edge))
                                                {
                                                    Temp tempEdge = tempData[connectedEdge.m_Edge];
                                                    if ((tempEdge.m_Flags & TempFlags.Delete) != 0)
                                                    {
                                                        continue;
                                                    }
                                                    if (compositionData.HasComponent(tempEdge.m_Original))
                                                    {
                                                        Composition edgeComposition = compositionData[tempEdge.m_Original];
                                                        CompositionFlags edgeCompositionFlags = netCompositionData[isStartNode.Value ? edgeComposition.m_StartNode : edgeComposition.m_EndNode].m_Flags;
                                                        if ((edgeCompositionFlags.m_General & CompositionFlags.General.Roundabout) != 0)
                                                        {
                                                            //skip, had a roundabout upgrade, nothing has changed
                                                            continue;
                                                        }
                                                    }
                                                }

                                                commandBuffer.AddComponent<Error>(entity);
                                                commandBuffer.AddComponent<BatchesUpdated>(entity);

                                                //highlight and block node modification
                                                commandBuffer.AddComponent<Error>(temp.m_Original);
                                                commandBuffer.AddComponent<BatchesUpdated>(temp.m_Original);
                                                feedbackInfos.Add(new ToolFeedbackInfo() { type = FeedbackMessageType.ErrorApplyRoundabout });
                                                hasFeedbackError = true;
                                            }
                                            else if (hasPriorities &&
                                                (compositionFlags.m_General & CompositionFlags.General.TrafficLights) != 0 && temp.m_Original != Entity.Null)
                                            {
                                                if (tempData.HasComponent(connectedEdge.m_Edge))
                                                {
                                                    Temp tempEdge = tempData[connectedEdge.m_Edge];
                                                    if ((tempEdge.m_Flags & TempFlags.Delete) != 0)
                                                    {
                                                        continue;
                                                    }
                                                    if (compositionData.HasComponent(tempEdge.m_Original))
                                                    {
                                                        Composition edgeComposition = compositionData[tempEdge.m_Original];
                                                        CompositionFlags edgeCompositionFlags = netCompositionData[isStartNode.Value ? edgeComposition.m_StartNode : edgeComposition.m_EndNode].m_Flags;
                                                        if ((edgeCompositionFlags.m_General & CompositionFlags.General.TrafficLights) != 0)
                                                        {
                                                            //skip, had a traffic lights upgrade, nothing has changed
                                                            continue;
                                                        }
                                                    }
                                                }

                                                feedbackInfos.Add(new ToolFeedbackInfo() { type = FeedbackMessageType.WarnResetPrioritiesTrafficLightsApply });
                                            }
                                        }
                                    }
                                }

                                if (upgradedData.HasComponent(connectedEdge.m_Edge) &&
                                    !deletedData.HasComponent(connectedEdge.m_Edge))
                                {
                                    Upgraded upgraded = upgradedData[connectedEdge.m_Edge];
                                    Edge edge = edgeData[connectedEdge.m_Edge];
                                    CompositionFlags.Side side = edge.m_Start == entity == !leftHandTraffic ? upgraded.m_Flags.m_Left : upgraded.m_Flags.m_Right;
                                    if ((side & (CompositionFlags.Side.ForbidStraight | CompositionFlags.Side.ForbidLeftTurn | CompositionFlags.Side.ForbidRightTurn)) != 0)
                                    {
                                        bool isUpgrade = false;
                                        if (tempData.HasComponent(connectedEdge.m_Edge))
                                        {
                                            Temp tempConnEdge = tempData[connectedEdge.m_Edge];
                                            isUpgrade = (tempConnEdge.m_Flags & (TempFlags.Essential | TempFlags.Upgrade)) != 0;
                                            if ((tempConnEdge.m_Flags & TempFlags.Delete) != 0)
                                            {
                                                continue;
                                            }
                                        }

                                        if (isUpgrade && hasModifiedConnections && numConnections > 0)
                                        {
                                            feedbackInfos.Add(new ToolFeedbackInfo() { container = connectedEdge.m_Edge, type = FeedbackMessageType.WarnForbiddenTurnApply });
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
                            if (hasFeedbackError)
                            {
                                commandBuffer.AddComponent<ToolActionBlocked>(entity);
                            }
                            else if (hasBlocked)
                            {
                                commandBuffer.RemoveComponent<ToolActionBlocked>(entity);
                            }
                        }
                    }
                }
                else if (chunk.Has(ref edgeType))
                {
                    NativeArray<Edge> edges = chunk.GetNativeArray(ref edgeType);
                    BufferAccessor<LanePriority> chunkLanePriorities = chunk.GetBufferAccessor(ref lanePriorityTypeHandle);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        Entity entity = entities[i];
                        Temp temp = temps[i];

                        bool2 result = false;
                        Edge edge = edges[i];
                        bool2 isUpgrade = new bool2(
                            tempData.TryGetComponent(edge.m_Start, out Temp startTemp) && (startTemp.m_Flags & (TempFlags.Upgrade | TempFlags.Essential)) != 0,
                            tempData.TryGetComponent(edge.m_End, out Temp endTemp) && (endTemp.m_Flags & (TempFlags.Upgrade | TempFlags.Essential)) != 0
                        );
                        bool2 hasPriorities = false;
                        DynamicBuffer<LanePriority> lanePriorities = chunkLanePriorities[i];
                        for (var j = 0; j < lanePriorities.Length; j++)
                        {
                            LanePriority lanePriority = lanePriorities[j];
                            hasPriorities |= new bool2(!lanePriority.isEnd && isUpgrade.x, lanePriority.isEnd && isUpgrade.y);
                        }

                        Composition composition = compositionData[entity];
                        CompositionFlags compositionFlagsStart = netCompositionData[composition.m_StartNode].m_Flags;
                        CompositionFlags compositionFlagsEnd = netCompositionData[composition.m_EndNode].m_Flags;
                        result.x = hasPriorities.x && temp.m_Original != Entity.Null && CheckNodeUpgrade(entity, isStart: true, compositionFlagsStart, ref feedbackInfos);
                        result.y = hasPriorities.y && temp.m_Original != Entity.Null && CheckNodeUpgrade(entity, isStart: false, compositionFlagsEnd, ref feedbackInfos);
                        
                        if (feedbackInfos.Length > 0)
                        {
                            Entity nodeEntity = result.x ? edge.m_Start : edge.m_End;
                            DynamicBuffer<ToolFeedbackInfo> feedbackBuffer = commandBuffer.AddBuffer<ToolFeedbackInfo>(nodeEntity);
                            feedbackBuffer.CopyFrom(feedbackInfos.AsArray());
                            feedbackInfos.Clear();
                        }
                    }
                }

                feedbackInfos.Dispose();
            }

            private bool CheckEditIntersection(Entity entity, Entity nodeEntity, Temp temp, ref NativeList<ToolFeedbackInfo> feedbackInfos)
            {

                if (!connectedEdgesBuffer.HasBuffer(nodeEntity))
                {
                    return false;
                }
                
                CompositionFlags.General testFlags = priorityToolActive ? (CompositionFlags.General.LevelCrossing | CompositionFlags.General.Roundabout | CompositionFlags.General.TrafficLights) : CompositionFlags.General.Roundabout;
                DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgesBuffer[nodeEntity];
                for (int j = 0; j < connectedEdges.Length; j++)
                {
                    ConnectedEdge connectedEdge = connectedEdges[j];
                    if (j == 0)
                    {
                        Edge edge = edgeData[connectedEdge.m_Edge];
                        bool? isStartNode = math.any(new bool2(edge.m_Start.Equals(nodeEntity), edge.m_End.Equals(nodeEntity))) ? edge.m_Start.Equals(nodeEntity) : null;
                        if (isStartNode.HasValue && compositionData.HasComponent(connectedEdge.m_Edge))
                        {
                            Composition composition = compositionData[connectedEdge.m_Edge];
                            CompositionFlags compositionFlags = netCompositionData[isStartNode.Value ? composition.m_StartNode : composition.m_EndNode].m_Flags;

                            if ((compositionFlags.m_General & testFlags) != 0 &&
                                temp.m_Original != Entity.Null)
                            { 
                                bool hasTrafficLights = (compositionFlags.m_General & CompositionFlags.General.TrafficLights) != 0;
                                commandBuffer.AddComponent<Error>(entity);
                                commandBuffer.AddComponent<BatchesUpdated>(entity);

                                //highlight and block node modification
                                commandBuffer.AddComponent<Error>(temp.m_Original);
                                commandBuffer.AddComponent<BatchesUpdated>(temp.m_Original);
                                feedbackInfos.Add(new ToolFeedbackInfo()
                                {
                                    type = !priorityToolActive 
                                        ? FeedbackMessageType.ErrorLaneConnectorNotSupported
                                        : hasTrafficLights 
                                            ? FeedbackMessageType.ErrorPrioritiesRemoveTrafficLights
                                            : FeedbackMessageType.ErrorPrioritiesNotSupported 
                                });
                                return true;
                            }
                        }
                    }

                    if (upgradedData.HasComponent(connectedEdge.m_Edge) &&
                        !deletedData.HasComponent(connectedEdge.m_Edge))
                    { 
                        Upgraded upgraded = upgradedData[connectedEdge.m_Edge];
                        if (priorityToolActive)
                        {
                            if ((upgraded.m_Flags.m_General & CompositionFlags.General.TrafficLights) != 0)
                            {
                                feedbackInfos.Add(new ToolFeedbackInfo() { container = connectedEdge.m_Edge, type = FeedbackMessageType.WarnResetPrioritiesTrafficLightsApply });
                            }
                        }
                        else
                        {
                            Edge edge = edgeData[connectedEdge.m_Edge];
                            CompositionFlags.Side side = edge.m_Start == nodeEntity == !leftHandTraffic ? upgraded.m_Flags.m_Left : upgraded.m_Flags.m_Right;
                            if ((side & (CompositionFlags.Side.ForbidStraight | CompositionFlags.Side.ForbidLeftTurn | CompositionFlags.Side.ForbidRightTurn)) != 0)
                            {
                                feedbackInfos.Add(new ToolFeedbackInfo() { container = connectedEdge.m_Edge, type = FeedbackMessageType.WarnResetForbiddenTurnUpgrades });
                            }
                        }
                    }
                }

                return false;
            }

            private bool CheckForTrackUTurn(Entity nodeEntity, DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections)
            {
                if (tightCurvePrefabEntity != Entity.Null &&
                    subLaneBuffer.TryGetBuffer(nodeEntity, out DynamicBuffer<SubLane> subLanes))
                {
                    for (int i = 0; i < subLanes.Length; i++)
                    {
                        SubLane subLane = subLanes[i];
                        if ((subLane.m_PathMethods & PathMethod.Track) != 0 &&
                            trackLaneData.HasComponent(subLane.m_SubLane))
                        {
                            Lane lane = laneData[subLane.m_SubLane];
                            // check track U-turn
                            if (lane.m_StartNode.OwnerEquals(lane.m_EndNode))
                            {
                                commandBuffer.AddComponent<Error>(nodeEntity);
                                commandBuffer.AddComponent<BatchesUpdated>(nodeEntity);
                                return true;
                            }

                            PrefabRef prefabRef = prefabRefData[subLane.m_SubLane];
                            TrackLane trackLane = trackLaneData[subLane.m_SubLane];
                            TrackLaneData track = trackLanePrefabData[prefabRef.m_Prefab];
                            // Does not seem to be working anymore. Vanilla bug?
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
                    for (int i = 0; i < modifiedLaneConnections.Length; i++)
                    {
                        ModifiedLaneConnections laneEndConnections = modifiedLaneConnections[i];
                        if (generatedConnectionsBuffer.TryGetBuffer(laneEndConnections.modifiedConnections, out DynamicBuffer<GeneratedConnection> connections) && !connections.IsEmpty)
                        {
                            foreach (GeneratedConnection generatedConnection in connections)
                            {
                                if ((generatedConnection.method & PathMethod.Track) != 0 &&
                                    generatedConnection.sourceEntity.Index == generatedConnection.targetEntity.Index)
                                {
                                    commandBuffer.AddComponent<Error>(nodeEntity);
                                    commandBuffer.AddComponent<BatchesUpdated>(nodeEntity);
                                    return true;
                                }
                            }
                        }
                    }
                }
                
                return false;
            }

            private bool CheckNodeUpgrade(Entity tempEntity, bool isStart, CompositionFlags compositionFlags, ref NativeList<ToolFeedbackInfo> feedbackInfos)
            {
                if ((compositionFlags.m_General & (CompositionFlags.General.LevelCrossing | CompositionFlags.General.Roundabout | CompositionFlags.General.TrafficLights)) == 0)
                {
                    return false;
                }
                
                bool hasTrafficLights = (compositionFlags.m_General & CompositionFlags.General.TrafficLights) != 0;
                bool hasLevelCrossing = (compositionFlags.m_General & CompositionFlags.General.LevelCrossing) != 0;
                bool hasRoundabout = (compositionFlags.m_General & CompositionFlags.General.Roundabout) != 0;
                if (tempData.HasComponent(tempEntity))
                {
                    Temp tempEdge = tempData[tempEntity];
                    if ((tempEdge.m_Flags & TempFlags.Delete) != 0)
                    {
                        return false;
                    }
                    if (compositionData.HasComponent(tempEdge.m_Original))
                    {
                        Composition edgeComposition = compositionData[tempEdge.m_Original];
                        CompositionFlags nodeCompositionFlags = netCompositionData[isStart ? edgeComposition.m_StartNode : edgeComposition.m_EndNode].m_Flags;
                        bool hadTrafficLights = (nodeCompositionFlags.m_General & CompositionFlags.General.TrafficLights) != 0;
                        bool hadLevelCrossing = (nodeCompositionFlags.m_General & CompositionFlags.General.LevelCrossing) != 0;
                        bool hadRoundabout = (nodeCompositionFlags.m_General & CompositionFlags.General.Roundabout) != 0;
                        if (hasRoundabout == hadRoundabout &&
                            hasLevelCrossing == hadLevelCrossing &&
                            hasTrafficLights == hadTrafficLights)
                        {
                            //skip, had traffic lights upgrade, nothing has changed
                            return false;
                        }

                        hasTrafficLights = hasTrafficLights != hadTrafficLights;
                        hasRoundabout = hasRoundabout != hadRoundabout;
                        // hasLevelCrossing = hasLevelCrossing != hadLevelCrossing;
                    }
                }

                FeedbackMessageType type = hasRoundabout 
                    ? FeedbackMessageType.WarnResetPrioritiesRoundaboutApply : hasTrafficLights 
                        ? FeedbackMessageType.WarnResetPrioritiesTrafficLightsApply : FeedbackMessageType.WarnResetPrioritiesChangeApply;
                feedbackInfos.Add(new ToolFeedbackInfo() { container = tempEntity, type = type });
                return true;
            }
        }
    }
}
