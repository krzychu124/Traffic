using Colossal.Mathematics;
using Game.Common;
using Game.Net;
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
using LaneConnection = Traffic.Components.LaneConnections.LaneConnection;
using SecondaryLane = Game.Net.SecondaryLane;
using SubLane = Game.Net.SubLane;

namespace Traffic.Systems.LaneConnections
{
    public partial class GenerateConnectorsSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct GenerateConnectorsJob : IJobChunk
        {            
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<EditIntersection> editIntersectionType;
            [ReadOnly] public BufferTypeHandle<ConnectorElement> connectorElementType;
            [ReadOnly] public ComponentLookup<Node> nodeData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Temp> tempData;
            [ReadOnly] public ComponentLookup<Hidden> hiddenData;
            [ReadOnly] public ComponentLookup<Deleted> deletedData;
            [ReadOnly] public ComponentLookup<Composition> compositionData;
            [ReadOnly] public ComponentLookup<PrefabRef> prefabRefData;
            [ReadOnly] public ComponentLookup<NetCompositionData> prefabCompositionData;
            [ReadOnly] public ComponentLookup<NetLaneData> prefabNetLaneData;
            [ReadOnly] public ComponentLookup<CarLaneData> prefabCarLaneData;
            [ReadOnly] public ComponentLookup<TrackLaneData> prefabTrackLaneData;
            [ReadOnly] public ComponentLookup<UtilityLaneData> prefabUtilityLaneData;
            [ReadOnly] public ComponentLookup<SlaveLane> slaveLaneData;
            [ReadOnly] public ComponentLookup<MasterLane> masterLaneData;
            [ReadOnly] public ComponentLookup<EdgeLane> edgeLaneData;
            [ReadOnly] public ComponentLookup<EdgeGeometry> edgeGeometryData;
            [ReadOnly] public ComponentLookup<Curve> curveData;
            [ReadOnly] public ComponentLookup<Lane> laneData;
            [ReadOnly] public ComponentLookup<SecondaryLane> secondaryLaneData;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdges;
            [ReadOnly] public BufferLookup<SubLane> subLanes;
            [ReadOnly] public BufferLookup<NetCompositionLane> prefabCompositionLanes;
            public EntityCommandBuffer commandBuffer;
            
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                NativeArray<EditIntersection> editIntersections = chunk.GetNativeArray(ref editIntersectionType);
                NativeList<ConnectPosition> sourceConnectPositions = new NativeList<ConnectPosition>(32, Allocator.Temp);
                NativeList<ConnectPosition> targetConnectPositions = new NativeList<ConnectPosition>(32, Allocator.Temp);
                for (int i = 0; i < editIntersections.Length; i++)
                {
                    if (chunk.Has(ref connectorElementType))
                    {
                        Logger.DebugConnections("Skip creating connectors");
                        continue;
                    }
                    EditIntersection intersection = editIntersections[i];
                    Entity nodeEntity = intersection.node;
                    Logger.Debug($"Check node entity: {nodeEntity}");
                    if (nodeData.HasComponent(nodeEntity))
                    {
                        Node node = nodeData[nodeEntity];
                        EdgeIterator edgeIterator = new EdgeIterator(Entity.Null, nodeEntity, connectedEdges, edgeData, tempData, hiddenData);
                        EdgeIteratorValue value;
                        bool hasEdges = false;
                        while (edgeIterator.GetNext(out value))
                        {
                            Logger.DebugConnections($"\tCheck edge: {value.m_Edge}");
                            GetNodeConnectors(nodeEntity, value.m_Edge, value.m_End, sourceConnectPositions, targetConnectPositions);
                            hasEdges = true;
                        }

                        if (hasEdges)
                        {
                            Logger.DebugConnections($"Check node entity: {nodeEntity}. Has edges! Sources: {sourceConnectPositions.Length} Targets: {targetConnectPositions.Length}");
                            CreateConnectors(entities[i], nodeEntity, sourceConnectPositions, targetConnectPositions);
                        }
                        sourceConnectPositions.Clear();
                        targetConnectPositions.Clear();
                    }
                }
                sourceConnectPositions.Dispose();
                targetConnectPositions.Dispose();
            }
            
            private unsafe void GetNodeConnectors(Entity node, Entity edge, bool isEnd, NativeList<ConnectPosition> sourceConnectPositions, NativeList<ConnectPosition> targetConnectPositions) {
                Composition composition = compositionData[edge];
                NetCompositionData netCompositionData = prefabCompositionData[composition.m_Edge];
                DynamicBuffer<NetCompositionLane> netCompositionLanes = prefabCompositionLanes[composition.m_Edge];
                EdgeGeometry edgeGeometry = edgeGeometryData[edge];

                if (isEnd)
                {
                    edgeGeometry.m_Start.m_Left = MathUtils.Invert(edgeGeometry.m_End.m_Right);
                    edgeGeometry.m_Start.m_Right = MathUtils.Invert(edgeGeometry.m_End.m_Left);
                }

                // StringBuilder sb = new StringBuilder();
                // sb.Append($"Node: {node} edge: {edge} isEnd: {isEnd} |S: {composition.m_StartNode} E: {composition.m_EndNode}").AppendLine();
                // LaneFlags laneFlags = /*(!includeAnchored) ? LaneFlags.FindAnchor :*/ ((LaneFlags)0);
                if (!deletedData.HasComponent(edge) && subLanes.HasBuffer(edge))
                {
                    DynamicBuffer<SubLane> dynamicBuffer2 = subLanes[edge];
                    float rhs = math.select(0f, 1f, isEnd);
                    bool* visitedCompositionLanes = stackalloc bool[(int)(uint)netCompositionLanes.Length];
                    for (int i = 0; i < netCompositionLanes.Length; i++)
                    {
                        visitedCompositionLanes[i] = false;
                    }
                    for (int j = 0; j < dynamicBuffer2.Length; j++)
                    {
                        Entity subLane = dynamicBuffer2[j].m_SubLane;
                        // sb.Append($"> Checking subLane: {subLane} | ").AppendLine(j.ToString());
                        if (!edgeLaneData.HasComponent(subLane) || secondaryLaneData.HasComponent(subLane))
                        {
                            // sb.Append($"No component: {!edgeLaneData.HasComponent(subLane)}").Append(" | hasSecondary: ").AppendLine(secondaryLaneData.HasComponent(subLane).ToString());
                            continue;
                        }
                        bool2 x = edgeLaneData[subLane].m_EdgeDelta == rhs;
                        if (!math.any(x))
                        {
                            // sb.Append("Wrong delta ").AppendLine(edgeLaneData[subLane].m_EdgeDelta.ToString());
                            continue;
                        }
                        bool y = x.y;
                        Curve curve = curveData[subLane];
                        if (y)
                        {
                            curve.m_Bezier = MathUtils.Invert(curve.m_Bezier);
                        }
                        // sb.AppendLine($"Y: {y}");
                        int compositionLaneIndex = -1;
                        float num2 = float.MaxValue;
                        PrefabRef prefabRef2 = prefabRefData[subLane];
                        NetLaneData netLaneData = prefabNetLaneData[prefabRef2.m_Prefab];
                        LaneFlags laneFlags2 = y ? LaneFlags.DisconnectedEnd : LaneFlags.DisconnectedStart;
                        LaneFlags laneFlags3 = netLaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.Underground);
                        LaneFlags laneFlags4 = LaneFlags.Invert | LaneFlags.Slave | LaneFlags.Road | LaneFlags.Track |
                            LaneFlags.Underground | laneFlags2;
                        // sb.Append("Delta ").AppendLine(edgeLaneData[subLane].m_EdgeDelta.ToString());
                        if (y != isEnd)
                        {
                            laneFlags3 |= LaneFlags.Invert;
                        }
                        if (slaveLaneData.HasComponent(subLane))
                        {
                            laneFlags3 |= LaneFlags.Slave;
                        }
                        if (masterLaneData.HasComponent(subLane))
                        {
                            laneFlags3 |= LaneFlags.Master;
                            laneFlags3 &= ~LaneFlags.Track;
                            laneFlags4 &= ~LaneFlags.Track;
                        }
                        else if ((netLaneData.m_Flags & laneFlags2) != 0)
                        {
                            // sb.AppendLine($"Disconnected: {netLaneData.m_Flags} & {laneFlags2}");
                            continue;
                        }
                        CarLaneData carLaneData = default(CarLaneData);
                        TrackLaneData trackLaneData = default(TrackLaneData);
                        UtilityLaneData utilityLaneData = default(UtilityLaneData);
                        if ((netLaneData.m_Flags & LaneFlags.Track) != 0)
                        {
                            trackLaneData = prefabTrackLaneData[prefabRef2.m_Prefab];
                        }
                        if ((netLaneData.m_Flags & LaneFlags.Road) != 0)
                        {
                            carLaneData = prefabCarLaneData[prefabRef2.m_Prefab];
                        }
                        
                        if ((netLaneData.m_Flags & (LaneFlags.Utility | LaneFlags.Pedestrian | LaneFlags.Parking | LaneFlags.ParkingLeft | LaneFlags.ParkingRight)) != 0)
                        {
                            // sb.AppendLine($"Incompatible: {netLaneData.m_Flags}");
                            continue;
                            // utilityLaneData = prefabUtilityLaneData[prefabRef2.m_Prefab];
                        } 
                        if ((netLaneData.m_Flags & LaneFlags.Utility) != 0)
                        {
                            utilityLaneData = prefabUtilityLaneData[prefabRef2.m_Prefab];
                        }
                        // sb.AppendLine($"Search compositions: {netCompositionLanes.Length} \n\t|1 {laneFlags} \n\t|2 {laneFlags2} \n\t|3 {laneFlags3} \n\t|4 {laneFlags4}");
                        for (int k = 0; k < netCompositionLanes.Length; k++)
                        {
                            NetCompositionLane netCompositionLane = netCompositionLanes[k];
                            // sb.AppendLine($"> Checking composition ({k}): {netCompositionLane.m_Flags} | {netCompositionLane.m_Index} | {netCompositionLane.m_Position}");
                            if ((netCompositionLane.m_Flags & laneFlags4) != laneFlags3 || 
                                // ((netCompositionLane.m_Flags & laneFlags) != 0 && IsAnchored(node, ref anchorPrefabs, netCompositionLane.m_Lane)) ||
                                ((laneFlags3 & LaneFlags.Track) != 0 && prefabTrackLaneData[netCompositionLane.m_Lane].m_TrackTypes != trackLaneData.m_TrackTypes) ||
                                ((laneFlags3 & LaneFlags.Utility) != 0 && prefabUtilityLaneData[netCompositionLane.m_Lane].m_UtilityTypes != utilityLaneData.m_UtilityTypes))
                            {
                                // sb.AppendLine($"Failed check ({k}): {netCompositionLane.m_Flags} \n\t|1 {laneFlags} \n\t|2 {laneFlags2} \n\t|3 {laneFlags3} \n\t|4 {laneFlags4}");
                                continue;
                            }
                            netCompositionLane.m_Position.x = math.select(0f - netCompositionLane.m_Position.x, netCompositionLane.m_Position.x, isEnd);
                            float num3 = netCompositionLane.m_Position.x / math.max(1f, netCompositionData.m_Width) + 0.5f;
                            if (MathUtils.Intersect(new Line2(edgeGeometry.m_Start.m_Right.a.xz, edgeGeometry.m_Start.m_Left.a.xz), new Line2(curve.m_Bezier.a.xz, curve.m_Bezier.b.xz), out float2 t))
                            {
                                float num4 = math.abs(num3 - t.x);
                                if (num4 < num2)
                                {
                                    // sb.AppendLine($"Found better lane: {k}, t: {t} 2: {num2} 3: {num3} 4: {num4}");
                                    compositionLaneIndex = k;
                                    num2 = num4;
                                } else 
                                {
                                    // sb.AppendLine($"Not better lane than set ({compositionLaneIndex}): {k}, t: {t} 2: {num2} 3: {num3} 4: {num4}");
                                }
                                
                            }
                        }

                        //sb.AppendLine($"Calculated index: {compositionLaneIndex}, visited {(compositionLaneIndex > -1 && visitedCompositionLanes[compositionLaneIndex])}");
                        if (compositionLaneIndex != -1 && !visitedCompositionLanes[compositionLaneIndex])
                        {
                            visitedCompositionLanes[compositionLaneIndex] = true;
                            NetCompositionLane netCompositionLaneData = netCompositionLanes[compositionLaneIndex];
                            netCompositionLaneData.m_Position.x = math.select(0f - netCompositionLaneData.m_Position.x, netCompositionLaneData.m_Position.x, isEnd);
                            float order = netCompositionLaneData.m_Position.x / math.max(1f, netCompositionData.m_Width) + 0.5f;
                            Lane lane = laneData[subLane];
                            if (y)
                            {
                                netCompositionLaneData.m_Index = (byte)(lane.m_EndNode.GetLaneIndex() & 0xFF);
                            }
                            else
                            {
                                netCompositionLaneData.m_Index = (byte)(lane.m_StartNode.GetLaneIndex() & 0xFF);
                            }
                            float3 tangent = MathUtils.StartTangent(curve.m_Bezier);
                            tangent = -MathUtils.Normalize(tangent, tangent.xz);
                            tangent.y = math.clamp(tangent.y, -1f, 1f);
                            ConnectPosition value = new ConnectPosition
                            {
                                edge = edge,
                                compositionLane = netCompositionLaneData,
                                order = order,
                                position =  curve.m_Bezier.a,
                                direction = tangent,
                                isTwoWay = (netLaneData.m_Flags & LaneFlags.Twoway) != 0,
                                vehicleGroup = GetVehicleGroup(netLaneData, carLaneData, trackLaneData)
                            };

                            if ((netLaneData.m_Flags & LaneFlags.Twoway) != 0)
                            {
                                // sb.AppendLine($"Connect Position (TwoWay): {value.order} | {value.position} | {value.compositionLane.m_Flags} | {value.compositionLane.m_Index} | {value.compositionLane.m_Position} || vehGroup: {value.vehicleGroup}");
                                targetConnectPositions.Add(in value);
                                sourceConnectPositions.Add(in value);
                            }
                            else if (!y)
                            {
                                // sb.AppendLine($"Connect Position (Target): {value.order} | {value.position} | {value.compositionLane.m_Flags} | {value.compositionLane.m_Index} | {value.compositionLane.m_Position} || vehGroup: {value.vehicleGroup}");
                                targetConnectPositions.Add(in value);
                            }
                            else
                            {
                                // sb.AppendLine($"Connect Position (Source): {value.order} | {value.position} | {value.compositionLane.m_Flags} | {value.compositionLane.m_Index} | {value.compositionLane.m_Position} || vehGroup: {value.vehicleGroup}");
                                sourceConnectPositions.Add(in value);
                            }
                        }
                    }
                }
                // Logger.Debug(sb.ToString());
            }

            private VehicleGroup GetVehicleGroup(NetLaneData netLaneData, CarLaneData carLaneData, TrackLaneData trackLaneData)
            {
                VehicleGroup group = VehicleGroup.None;
                if ((netLaneData.m_Flags & LaneFlags.Road) != 0)
                {
                    if ((carLaneData.m_RoadTypes & RoadTypes.Bicycle) != 0)
                    {
                        group |= VehicleGroup.Bike;
                    }
                    if ((carLaneData.m_RoadTypes & RoadTypes.Car) != 0)
                    {
                        group |= VehicleGroup.Car;
                    }
                }
                if ((netLaneData.m_Flags & LaneFlags.Track) != 0)
                {
                    group |= (VehicleGroup)((int)trackLaneData.m_TrackTypes << 1);
                }
                return group;
            }

            private void CreateConnectors(Entity selectedIntersection, Entity node, NativeList<ConnectPosition> sourceConnectPositions, NativeList<ConnectPosition> targetConnectPositions) {
                //todo handle two-way connections
                NativeList<Entity> connectors = new NativeList<Entity>(sourceConnectPositions.Length + targetConnectPositions.Length, Allocator.Temp);
                for (int i = 0; i < sourceConnectPositions.Length; i++)
                {
                    ConnectPosition connectPosition = sourceConnectPositions[i];
                    Entity entity = commandBuffer.CreateEntity();
                    Connector connector = new Connector
                    {
                        edge = connectPosition.edge,
                        node = node,
                        laneIndex = connectPosition.compositionLane.m_Index,
                        lanePosition = connectPosition.compositionLane.m_Position,
                        carriagewayAndGroupIndex = new int2(connectPosition.compositionLane.m_Carriageway, connectPosition.compositionLane.m_Group),
                        position = connectPosition.position,
                        direction = connectPosition.direction,
                        vehicleGroup = connectPosition.vehicleGroup,
                        connectorType = connectPosition.isTwoWay ? ConnectorType.TwoWay : ConnectorType.Source,
                    };
                    commandBuffer.AddComponent<Connector>(entity, connector);
                    commandBuffer.AddComponent(entity, default(Updated));
                    commandBuffer.AddBuffer<LaneConnection>(entity);
                    connectors.Add(entity);
                }
                
                for (int i = 0; i < targetConnectPositions.Length; i++)
                {
                    ConnectPosition connectPosition = targetConnectPositions[i];
                    Entity entity = commandBuffer.CreateEntity();
                    Connector connector = new Connector
                    {
                        edge = connectPosition.edge,
                        node = node,
                        laneIndex = connectPosition.compositionLane.m_Index,
                        lanePosition = connectPosition.compositionLane.m_Position,
                        carriagewayAndGroupIndex = new int2(connectPosition.compositionLane.m_Carriageway, connectPosition.compositionLane.m_Group),
                        position = connectPosition.position,
                        direction = connectPosition.direction,
                        vehicleGroup = connectPosition.vehicleGroup,
                        connectorType = connectPosition.isTwoWay ? ConnectorType.TwoWay : ConnectorType.Target,
                    };
                    commandBuffer.AddComponent<Connector>(entity, connector);
                    commandBuffer.AddComponent(entity, default(Updated));
                    commandBuffer.AddBuffer<LaneConnection>(entity);
                    connectors.Add(entity);
                }
                
                DynamicBuffer<ConnectorElement> connectorElements = commandBuffer.AddBuffer<ConnectorElement>(selectedIntersection);
                connectorElements.ResizeUninitialized(connectors.Length);
                for (int i = 0; i < connectors.Length; i++)
                {
                    connectorElements[i] = new ConnectorElement() { entity = connectors[i] };
                }

                connectors.Dispose();
            }
        }
    }
}
