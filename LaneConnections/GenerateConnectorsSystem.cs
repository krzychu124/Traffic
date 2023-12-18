using System;
using System.Text;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Tools;
using Traffic.Components;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CarLane = Game.Net.CarLane;
using Edge = Game.Net.Edge;
using SecondaryLane = Game.Net.SecondaryLane;
using SubLane = Game.Net.SubLane;

namespace Traffic.LaneConnections
{
    public partial class GenerateConnectorsSystem : GameSystemBase
    {
        private EntityQuery _definitionQuery;
        private EntityQuery _connectorsQuery;
        private ModificationBarrier5 _modificationBarrier;
        
        protected override void OnCreate() {
            base.OnCreate();
            
            _modificationBarrier = base.World.GetOrCreateSystemManaged<ModificationBarrier5>();
            _definitionQuery = GetEntityQuery(ComponentType.ReadOnly<EditIntersection>(), ComponentType.ReadOnly<Updated>(), ComponentType.Exclude<Temp>(), ComponentType.Exclude<Deleted>());
            _connectorsQuery = GetEntityQuery(ComponentType.ReadOnly<Connector>(), ComponentType.ReadOnly<Updated>(), ComponentType.Exclude<Deleted>());
            
            RequireForUpdate(_definitionQuery);
        }

        protected override void OnUpdate() {
            NativeHashMap<NodeEdgeLaneKey, Entity> connectorsMap = new (32, Allocator.TempJob);
            EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
            GenerateConnectorsJob job = new GenerateConnectorsJob
            {
                editIntersectionType = SystemAPI.GetComponentTypeHandle<EditIntersection>(true),
                nodeData = SystemAPI.GetComponentLookup<Node>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                tempData = SystemAPI.GetComponentLookup<Temp>(true),
                hiddenData = SystemAPI.GetComponentLookup<Hidden>(true),
                // updatedData = SystemAPI.GetComponentLookup<Updated>(true),
                deletedData = SystemAPI.GetComponentLookup<Deleted>(true),
                compositionData = SystemAPI.GetComponentLookup<Composition>(true),
                prefabRefData = SystemAPI.GetComponentLookup<PrefabRef>(true),
                prefabCompositionData = SystemAPI.GetComponentLookup<NetCompositionData>(true),
                prefabNetLaneData = SystemAPI.GetComponentLookup<NetLaneData>(true),
                prefabTrackLaneData = SystemAPI.GetComponentLookup<TrackLaneData>(true),
                prefabUtilityLaneData = SystemAPI.GetComponentLookup<UtilityLaneData>(true),
                slaveLaneData = SystemAPI.GetComponentLookup<SlaveLane>(true),
                masterLaneData = SystemAPI.GetComponentLookup<MasterLane>(true),
                edgeLaneData = SystemAPI.GetComponentLookup<EdgeLane>(true),
                edgeGeometryData = SystemAPI.GetComponentLookup<EdgeGeometry>(true),
                curveData = SystemAPI.GetComponentLookup<Curve>(true),
                laneData = SystemAPI.GetComponentLookup<Lane>(true),
                secondaryLaneData = SystemAPI.GetComponentLookup<SecondaryLane>(true),
                connectedEdges = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                subLanes = SystemAPI.GetBufferLookup<SubLane>(true),
                prefabCompositionLanes = SystemAPI.GetBufferLookup<NetCompositionLane>(true),
                commandBuffer = commandBuffer,
            };
            Logger.Info($"Generate (def): {_definitionQuery.CalculateEntityCount()}");
            JobHandle jobHandle = job.Schedule(_definitionQuery, Dependency);
            JobHandle.ScheduleBatchedJobs();
            jobHandle.Complete();
            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
            
            Logger.Info($"Generate (connectors): {_connectorsQuery.CalculateEntityCount()}");
            CollectConnectorsJob collectConnectorsJob = new CollectConnectorsJob()
            {
                entityType = SystemAPI.GetEntityTypeHandle(),
                connectorType = SystemAPI.GetComponentTypeHandle<Connector>(true),
                resultMap = connectorsMap, 
            };
            JobHandle collectConnectorsHandle = collectConnectorsJob.Schedule(_connectorsQuery, JobHandle.CombineDependencies(Dependency, jobHandle));
            
            GenerateConnectionLanesJob job2 = new GenerateConnectionLanesJob()
            {
                editIntersectionType = SystemAPI.GetComponentTypeHandle<EditIntersection>(true),
                nodeData = SystemAPI.GetComponentLookup<Node>(true),
                laneData = SystemAPI.GetComponentLookup<Lane>(true),
                carLaneData = SystemAPI.GetComponentLookup<CarLane>(true),
                masterLaneData = SystemAPI.GetComponentLookup<MasterLane>(true),
                connectorData = SystemAPI.GetComponentLookup<Connector>(true),
                connectedEdgesBuffer = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                connectorsList = connectorsMap,
                subLanesBuffer = SystemAPI.GetBufferLookup<SubLane>(true),
                generatedConnectionBuffer = SystemAPI.GetBufferLookup<GeneratedConnection>(true),
                modifiedLaneConnectionsBuffer = SystemAPI.GetBufferLookup<ModifiedLaneConnections>(true),
                commandBuffer = _modificationBarrier.CreateCommandBuffer(),
            };
            JobHandle jobHandle2 = job2.Schedule(_definitionQuery, collectConnectorsHandle);
            _modificationBarrier.AddJobHandleForProducer(jobHandle2);
            connectorsMap.Dispose(jobHandle2);
            
            Dependency = jobHandle2;
        }

        private struct CollectConnectorsJob : IJobChunk
        {
            
            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentTypeHandle<Connector> connectorType;
            public NativeHashMap<NodeEdgeLaneKey,Entity> resultMap;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                NativeArray<Connector> connectors = chunk.GetNativeArray(ref connectorType);
                for (var i = 0; i < entities.Length; i++)
                {
                    Entity e = entities[i];
                    Connector connector = connectors[i];
                    resultMap.Add(new NodeEdgeLaneKey(connector.node.Index, connector.edge.Index, connector.laneIndex), e);
                }
            }
        }

        private struct GenerateConnectorsJob : IJobChunk
        {            
            [ReadOnly] public ComponentTypeHandle<EditIntersection> editIntersectionType;
            [ReadOnly] public ComponentLookup<Node> nodeData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Temp> tempData;
            [ReadOnly] public ComponentLookup<Hidden> hiddenData;
            // [ReadOnly] public ComponentLookup<Updated> updatedData;
            [ReadOnly] public ComponentLookup<Deleted> deletedData;
            [ReadOnly] public ComponentLookup<Composition> compositionData;
            [ReadOnly] public ComponentLookup<PrefabRef> prefabRefData;
            [ReadOnly] public ComponentLookup<NetCompositionData> prefabCompositionData;
            [ReadOnly] public ComponentLookup<NetLaneData> prefabNetLaneData;
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
            // public NativeParallelMultiHashMap<EdgeNodeKey, Connector>.ParallelWriter resultMap;
            public EntityCommandBuffer commandBuffer;
            
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<EditIntersection> editIntersections = chunk.GetNativeArray(ref editIntersectionType);
                NativeList<ConnectPosition> sourceConnectPositions = new NativeList<ConnectPosition>(32, Allocator.Temp);
                NativeList<ConnectPosition> targetConnectPositions = new NativeList<ConnectPosition>(32, Allocator.Temp);
                for (int i = 0; i < editIntersections.Length; i++)
                {
                    EditIntersection intersection = editIntersections[i];
                    Entity nodeEntity = intersection.node;
                    Logger.Info($"Check node entity: {nodeEntity}");
                    if (nodeData.HasComponent(nodeEntity))
                    {
                        Node node = nodeData[nodeEntity];
                        EdgeIterator edgeIterator = new EdgeIterator(Entity.Null, nodeEntity, connectedEdges, edgeData, tempData, hiddenData);
                        EdgeIteratorValue value;
                        bool hasEdges = false;
                        while (edgeIterator.GetNext(out value))
                        {
                            Logger.Info($"Check edge: {value.m_Edge}");
                            GetNodeConnectors(nodeEntity, value.m_Edge, value.m_End, sourceConnectPositions, targetConnectPositions);
                            hasEdges = true;
                        }

                        if (hasEdges)
                        {
                            Logger.Info($"Check node entity: {nodeEntity}. Has edges! Sources: {sourceConnectPositions.Length} Targets: {targetConnectPositions.Length}");
                            CreateConnectors(nodeEntity, sourceConnectPositions, targetConnectPositions);
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

                StringBuilder sb = new StringBuilder();
                sb.Append($"Node: {node} edge: {edge} isEnd: {isEnd} |S: {composition.m_StartNode} E: {composition.m_EndNode}").AppendLine();
                LaneFlags laneFlags = /*(!includeAnchored) ? LaneFlags.FindAnchor :*/ ((LaneFlags)0);
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
                        sb.Append($"> Checking subLane: {subLane} | ").AppendLine(j.ToString());
                        if (!edgeLaneData.HasComponent(subLane) || secondaryLaneData.HasComponent(subLane))
                        {
                            sb.Append($"No component: {!edgeLaneData.HasComponent(subLane)}").Append(" | hasSecondary: ").AppendLine(secondaryLaneData.HasComponent(subLane).ToString());
                            continue;
                        }
                        bool2 x = edgeLaneData[subLane].m_EdgeDelta == rhs;
                        if (!math.any(x))
                        {
                            sb.Append("Wrong delta ").AppendLine(edgeLaneData[subLane].m_EdgeDelta.ToString());
                            continue;
                        }
                        bool y = x.y;
                        Curve curve = curveData[subLane];
                        if (y)
                        {
                            curve.m_Bezier = MathUtils.Invert(curve.m_Bezier);
                        }
                        sb.AppendLine($"Y: {y}");
                        int compositionLaneIndex = -1;
                        float num2 = float.MaxValue;
                        PrefabRef prefabRef2 = prefabRefData[subLane];
                        NetLaneData netLaneData = prefabNetLaneData[prefabRef2.m_Prefab];
                        LaneFlags laneFlags2 = y ? LaneFlags.DisconnectedEnd : LaneFlags.DisconnectedStart;
                        LaneFlags laneFlags3 = netLaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.Underground);
                        LaneFlags laneFlags4 = LaneFlags.Invert | LaneFlags.Slave | LaneFlags.Road | LaneFlags.Track |
                            LaneFlags.Underground | laneFlags2;
                        sb.Append("Delta ").AppendLine(edgeLaneData[subLane].m_EdgeDelta.ToString());
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
                            sb.AppendLine($"Disconnected: {netLaneData.m_Flags} & {laneFlags2}");
                            continue;
                        }
                        TrackLaneData trackLaneData = default(TrackLaneData);
                        UtilityLaneData utilityLaneData = default(UtilityLaneData);
                        if ((netLaneData.m_Flags & LaneFlags.Track) != 0)
                        {
                            trackLaneData = prefabTrackLaneData[prefabRef2.m_Prefab];
                        }
                        
                        //todo consider skipping utility lanes if possible
                        if ((netLaneData.m_Flags & (LaneFlags.Utility | LaneFlags.Pedestrian | LaneFlags.Parking | LaneFlags.ParkingLeft | LaneFlags.ParkingRight)) != 0)
                        {
                            sb.AppendLine($"Incompatible: {netLaneData.m_Flags}");
                            continue;
                            utilityLaneData = prefabUtilityLaneData[prefabRef2.m_Prefab];
                        } 
                        if ((netLaneData.m_Flags & LaneFlags.Utility) != 0)
                        {
                            utilityLaneData = prefabUtilityLaneData[prefabRef2.m_Prefab];
                        }
                        sb.AppendLine($"Search compositions: {netCompositionLanes.Length} \n\t|1 {laneFlags} \n\t|2 {laneFlags2} \n\t|3 {laneFlags3} \n\t|4 {laneFlags4}");
                        for (int k = 0; k < netCompositionLanes.Length; k++)
                        {
                            NetCompositionLane netCompositionLane = netCompositionLanes[k];
                            sb.AppendLine($"> Checking composition ({k}): {netCompositionLane.m_Flags} | {netCompositionLane.m_Index} | {netCompositionLane.m_Position}");
                            if ((netCompositionLane.m_Flags & laneFlags4) != laneFlags3 || 
                                // ((netCompositionLane.m_Flags & laneFlags) != 0 && IsAnchored(node, ref anchorPrefabs, netCompositionLane.m_Lane)) ||
                                ((laneFlags3 & LaneFlags.Track) != 0 && prefabTrackLaneData[netCompositionLane.m_Lane].m_TrackTypes != trackLaneData.m_TrackTypes) ||
                                ((laneFlags3 & LaneFlags.Utility) != 0 && prefabUtilityLaneData[netCompositionLane.m_Lane].m_UtilityTypes != utilityLaneData.m_UtilityTypes))
                            {
                                sb.AppendLine($"Failed check ({k}): {netCompositionLane.m_Flags} \n\t|1 {laneFlags} \n\t|2 {laneFlags2} \n\t|3 {laneFlags3} \n\t|4 {laneFlags4}");
                                continue;
                            }
                            netCompositionLane.m_Position.x = math.select(0f - netCompositionLane.m_Position.x, netCompositionLane.m_Position.x, isEnd);
                            float num3 = netCompositionLane.m_Position.x / math.max(1f, netCompositionData.m_Width) + 0.5f;
                            if (MathUtils.Intersect(new Line2(edgeGeometry.m_Start.m_Right.a.xz, edgeGeometry.m_Start.m_Left.a.xz), new Line2(curve.m_Bezier.a.xz, curve.m_Bezier.b.xz), out float2 t))
                            {
                                float num4 = math.abs(num3 - t.x);
                                if (num4 < num2)
                                {
                                    sb.AppendLine($"Found better lane: {k}, t: {t} 2: {num2} 3: {num3} 4: {num4}");
                                    compositionLaneIndex = k;
                                    num2 = num4;
                                } else 
                                {
                                    sb.AppendLine($"Not better lane than set ({compositionLaneIndex}): {k}, t: {t} 2: {num2} 3: {num3} 4: {num4}");
                                }
                                
                            }
                        }

                        sb.AppendLine($"Calculated index: {compositionLaneIndex}, visited {(compositionLaneIndex > -1 && visitedCompositionLanes[compositionLaneIndex])}");
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
                                supportedType = GetConnectionType(netCompositionLaneData.m_Flags),
                            };

                            if ((netLaneData.m_Flags & LaneFlags.Twoway) != 0)
                            {
                                sb.AppendLine($"Connect Position (TwoWay): {value.order} | {value.position} | {value.compositionLane.m_Flags} | {value.compositionLane.m_Index} | {value.compositionLane.m_Position}");
                                targetConnectPositions.Add(in value);
                                sourceConnectPositions.Add(in value);
                            }
                            else if (!y)
                            {
                                sb.AppendLine($"Connect Position (Target): {value.order} | {value.position} | {value.compositionLane.m_Flags} | {value.compositionLane.m_Index} | {value.compositionLane.m_Position}");
                                targetConnectPositions.Add(in value);
                            }
                            else
                            {
                                sb.AppendLine($"Connect Position (Source): {value.order} | {value.position} | {value.compositionLane.m_Flags} | {value.compositionLane.m_Index} | {value.compositionLane.m_Position}");
                                sourceConnectPositions.Add(in value);
                            }
                        }
                    }
                }
                // Logger.Info(sb.ToString());
            }

            private void CreateConnectors(Entity node, NativeList<ConnectPosition> sourceConnectPositions, NativeList<ConnectPosition> targetConnectPositions) {
                //todo handle two-way connections
                for (int i = 0; i < sourceConnectPositions.Length; i++)
                {
                    ConnectPosition connectPosition = sourceConnectPositions[i];
                    Entity entity = commandBuffer.CreateEntity();
                    Connector connector = new Connector
                    {
                        edge = connectPosition.edge,
                        node = node,
                        laneIndex = connectPosition.compositionLane.m_Index,
                        position = connectPosition.position,
                        direction = connectPosition.direction,
                        connectorType = ConnectorType.Source,
                        connectionType = connectPosition.supportedType,
                    };
                    commandBuffer.AddComponent<Connector>(entity, connector);
                    commandBuffer.AddComponent(entity, default(Updated));
                    commandBuffer.AddBuffer<LaneConnection>(entity);
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
                        position = connectPosition.position,
                        direction = connectPosition.direction,
                        connectorType = ConnectorType.Target,
                        connectionType = connectPosition.supportedType,
                    };
                    commandBuffer.AddComponent<Connector>(entity, connector);
                    commandBuffer.AddComponent(entity, default(Updated));
                    commandBuffer.AddBuffer<LaneConnection>(entity);
                }
            }

            private ConnectionType GetConnectionType(LaneFlags flags) {
                ConnectionType type = 0;
                if ((flags & LaneFlags.Road) != 0)
                {
                    type |= ConnectionType.Road;
                }
                if ((flags & LaneFlags.Track) != 0)
                {
                    type |= ConnectionType.Track;
                }
                if ((flags & LaneFlags.Utility) != 0)
                {
                    type |= ConnectionType.Utility;
                }
                
                return type;
            }
        }


        private struct GenerateConnectionLanesJob : IJobChunk
        {            
            [ReadOnly] public ComponentTypeHandle<EditIntersection> editIntersectionType;
            [ReadOnly] public ComponentLookup<Node> nodeData;
            [ReadOnly] public ComponentLookup<Lane> laneData;
            [ReadOnly] public ComponentLookup<CarLane> carLaneData;
            [ReadOnly] public ComponentLookup<MasterLane> masterLaneData;
            [ReadOnly] public ComponentLookup<Connector> connectorData;
            [ReadOnly] public NativeHashMap<NodeEdgeLaneKey, Entity> connectorsList;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgesBuffer;
            [ReadOnly] public BufferLookup<SubLane> subLanesBuffer;
            [ReadOnly] public BufferLookup<GeneratedConnection> generatedConnectionBuffer;
            [ReadOnly] public BufferLookup<ModifiedLaneConnections> modifiedLaneConnectionsBuffer;
            public EntityCommandBuffer commandBuffer;
 
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<EditIntersection> editIntersections = chunk.GetNativeArray(ref editIntersectionType);
                Logger.Info($"Connectors: {connectorsList.Count}");
                NativeParallelMultiHashMap<Entity, Connection> connections = new (8, Allocator.Temp);
                NativeList<GeneratedConnection> tempConnections = new NativeList<GeneratedConnection>(8, Allocator.Temp);
                NativeHashSet<ModifiedLaneConnections> tempModified = new NativeHashSet<ModifiedLaneConnections>(4, Allocator.Temp);
                for (int i = 0; i < editIntersections.Length; i++)
                {
                    EditIntersection editIntersection = editIntersections[i];
                    Entity node = editIntersection.node;
                    if (nodeData.HasComponent(node) && subLanesBuffer.HasBuffer(node))
                    {
                        DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgesBuffer[node];
                        DynamicBuffer<SubLane> subLanes = subLanesBuffer[node];
                        bool generateConnections = generatedConnectionBuffer.HasBuffer(node) /*&& generatedConnectionBuffer[node].Length == 0*/;
                        
                        foreach (SubLane subLane in subLanes)
                        {
                            Entity subLaneEntity = subLane.m_SubLane;
                            if ((subLane.m_PathMethods & (PathMethod.Road | PathMethod.Track)) == 0 || masterLaneData.HasComponent(subLaneEntity))
                            {
                                continue;
                            }
                            Lane lane = laneData[subLaneEntity];
                            Entity sourceEdge = FindEdge(connectedEdges, lane.m_StartNode);
                            if (sourceEdge == Entity.Null)
                            {
                                continue;
                            }
                            bool isUnsafe = false;
                            bool isForbidden = false;
                            if (carLaneData.HasComponent(subLaneEntity))
                            {
                                CarLane carLane = carLaneData[subLaneEntity];
                                isUnsafe = (carLane.m_Flags & CarLaneFlags.Unsafe) != 0;
                                isForbidden = (carLane.m_Flags & CarLaneFlags.Forbidden) != 0;
                            }
                            Connection connection = new Connection(lane, subLaneEntity, subLane.m_PathMethods, isUnsafe, isForbidden);
                            connections.Add(sourceEdge, connection);
                        }

                        tempModified.Clear();
                        if (modifiedLaneConnectionsBuffer.HasBuffer(node))
                        {
                            DynamicBuffer<GeneratedConnection> generatedConnections = generatedConnectionBuffer[node];
                            DynamicBuffer<ModifiedLaneConnections> laneConnectionsEnumerable = modifiedLaneConnectionsBuffer[node];
                            for (var j = 0; j < laneConnectionsEnumerable.Length; j++)
                            {
                                ModifiedLaneConnections modifiedLaneConnections = laneConnectionsEnumerable[j];
                                tempModified.Add(modifiedLaneConnections);

                                for (var k = 0; k < generatedConnections.Length; k++)
                                {
                                    GeneratedConnection generatedConnection = generatedConnections[k];
                                    // add matching existing connection
                                    if (generatedConnection.sourceEntity == modifiedLaneConnections.edgeEntity && modifiedLaneConnections.laneIndex == generatedConnection.laneIndexMap.x)
                                    {
                                        tempConnections.Add(generatedConnection);
                                    }
                                }
                            }
                        }
                        
                        foreach (ConnectedEdge connectedEdge in connectedEdges)
                        {
                            Entity edge = connectedEdge.m_Edge;
                            if (connections.ContainsKey(edge))
                            {
                                Entity e = commandBuffer.CreateEntity();
                                DynamicBuffer<Connection> connectionsBuffer = commandBuffer.AddBuffer<Connection>(e);
                                foreach (Connection connection in connections.GetValuesForKey(edge))
                                {
                                    connectionsBuffer.Add(connection);

                                    Entity targetEdge = FindEdge(connectedEdges, connection.targetNode);
                                    int connectorIndex = connection.sourceNode.GetLaneIndex() & 0xff;
                                    if (connectorsList.TryGetValue(new NodeEdgeLaneKey(node.Index, edge.Index, connectorIndex), out Entity connector))
                                    {
                                        Logger.Info($"Detected connection n: {node} e: {edge} idx: {connectorIndex} | connector: {connector}");
                                        commandBuffer.AppendToBuffer(connector, new LaneConnection() { connection = e });
                                    }

                                    int2 indexMap = new int2(connectorIndex, connection.targetNode.GetLaneIndex() & 0xff);
                                    Entity con = commandBuffer.CreateEntity();
                                    commandBuffer.AddComponent(con, new ConnectionData(connection, edge, targetEdge, indexMap));
                                    commandBuffer.AddComponent<CustomLaneConnection>(con);
                                    
                                    // generate connection if not connected to modified lane
                                    if (generateConnections && targetEdge != Entity.Null && !tempModified.Contains(new ModifiedLaneConnections() {edgeEntity = edge, laneIndex = indexMap.x}))
                                    {
                                        tempConnections.Add(new GeneratedConnection
                                        {
                                            sourceEntity = edge,
                                            targetEntity = targetEdge,
                                            laneIndexMap = indexMap,
                                            method = connection.method,
                                            isUnsafe = connection.isUnsafe,
                                        });
                                    }
                                }
                            }
                        }

                        if (generateConnections)
                        {
                            //update buffer with old and new generated connections
                            DynamicBuffer<GeneratedConnection> generated = commandBuffer.SetBuffer<GeneratedConnection>(node);
                            generated.ResizeUninitialized(tempConnections.Length);
                            for (var j = 0; j < tempConnections.Length; j++)
                            {
                                generated[j] = tempConnections[j];
                            }
                        }

                        connections.Clear();
                        tempConnections.Clear();
                    }
                }
                connections.Dispose();
                tempConnections.Dispose();
                tempModified.Dispose();
            }

            private Entity FindEdge(DynamicBuffer<ConnectedEdge> edges, PathNode node) {
                foreach (ConnectedEdge connectedEdge in edges)
                {
                    if (node.OwnerEquals(new PathNode(connectedEdge.m_Edge, 0)))
                    {
                        return connectedEdge.m_Edge;
                    }
                }
                return Entity.Null;
            }
        }
        
        
        private struct ConnectPosition
        {
            public Entity edge;
            public NetCompositionLane compositionLane;
            public float order;
            public float3 position;
            public float3 direction;
            public ConnectionType supportedType;
        }

        private struct EdgeNodeKey : IEquatable<EdgeNodeKey>
        {
            public Entity edge;
            public Entity node;

            public EdgeNodeKey(Entity e, Entity n) {
                edge = e;
                node = n;
            }

            public bool Equals(EdgeNodeKey other) {
                return edge.Equals(other.edge) && node.Equals(other.node);
            }

            public override int GetHashCode() {
                unchecked
                {
                    return (edge.GetHashCode() * 397) ^ node.GetHashCode();
                }
            }
        }

        private struct NodeEdgeLaneKey : IEquatable<NodeEdgeLaneKey>
        {
            public int nodeIndex;
            public int edgeIndex;
            public int laneIndex;

            public NodeEdgeLaneKey(int nodeIndex, int edgeIndex, int laneIndex) {
                this.nodeIndex = nodeIndex;
                this.edgeIndex = edgeIndex;
                this.laneIndex = laneIndex;
            }
            
            public bool Equals(NodeEdgeLaneKey other) {
                return nodeIndex == other.nodeIndex && edgeIndex == other.edgeIndex && laneIndex == other.laneIndex;
            }

            public override bool Equals(object obj) {
                return obj is NodeEdgeLaneKey other && Equals(other);
            }

            public override int GetHashCode() {
                unchecked
                {
                    int hashCode = nodeIndex;
                    hashCode = (hashCode * 397) ^ edgeIndex;
                    hashCode = (hashCode * 397) ^ laneIndex;
                    return hashCode;
                }
            }
        }
    }
}
