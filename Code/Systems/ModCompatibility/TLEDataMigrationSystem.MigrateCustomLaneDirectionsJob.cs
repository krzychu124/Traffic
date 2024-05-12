using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CarLane = Game.Net.CarLane;
using Edge = Game.Net.Edge;
using SubLane = Game.Net.SubLane;

namespace Traffic.Systems.ModCompatibility
{
    public partial class TLEDataMigrationSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct MigrateCustomLaneDirectionsJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public BufferTypeHandle<ConnectedEdge> connectedEdgeBufferTypeHandle;
            [ReadOnly] public BufferTypeHandle<SubLane> subLaneBufferTypeHandle;
            [ReadOnly] public ComponentLookup<Composition> compositionData;
            [ReadOnly] public ComponentLookup<CarLane> carLaneData;
            [ReadOnly] public ComponentLookup<Curve> curveData;
            [ReadOnly] public ComponentLookup<Deleted> deletedData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Lane> laneData;
            [ReadOnly] public ComponentLookup<MasterLane> masterLaneData;
            [ReadOnly] public BufferLookup<NetCompositionLane> prefabCompositionLaneBuffer;
            [ReadOnly] public Entity fakePrefabRef;
            public NativeQueue<int>.ParallelWriter generatedIntersectionData;
            public EntityCommandBuffer.ParallelWriter commandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                BufferAccessor<SubLane> subLanesBufferAccessor = chunk.GetBufferAccessor(ref subLaneBufferTypeHandle);
                BufferAccessor<ConnectedEdge> connectedEdgesBufferAccessor = chunk.GetBufferAccessor(ref connectedEdgeBufferTypeHandle);
                NativeParallelMultiHashMap<Entity, Connection> connections = new(8, Allocator.Temp);
                NativeParallelMultiHashMap<NodeEdgeLaneKey, GeneratedConnection>  generatedConnectionsMap = new NativeParallelMultiHashMap<NodeEdgeLaneKey, GeneratedConnection>(4, Allocator.Temp);
                NativeList<ModifiedLaneConnections> generatedModifiedLaneConnections = new NativeList<ModifiedLaneConnections>(Allocator.Temp);
                NativeList<NodeEdgeLaneKey> nodeEdgeLaneKeys = new NativeList<NodeEdgeLaneKey>(16, Allocator.Temp);
                int generated = 0;

                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgesBufferAccessor[i];
                    DynamicBuffer<SubLane> subLanes = subLanesBufferAccessor[i];

                    foreach (SubLane subLane in subLanes)
                    {
                        Entity subLaneEntity = subLane.m_SubLane;
                        if ((subLane.m_PathMethods & (PathMethod.Road | PathMethod.Track)) == 0 || masterLaneData.HasComponent(subLaneEntity))
                        {
                            continue;
                        }
                        Lane lane = laneData[subLaneEntity];
                        Entity sourceEdge = Helpers.NetUtils.FindEdge(connectedEdges, lane.m_StartNode);
                        Entity targetEdge = sourceEdge;
                        if (!lane.m_StartNode.OwnerEquals(lane.m_EndNode))
                        {
                            targetEdge = Helpers.NetUtils.FindEdge(connectedEdges, lane.m_EndNode);
                        }
                        if (sourceEdge == Entity.Null || targetEdge == Entity.Null)
                        {
                            continue;
                        }
                        
                        Edge sourceEdgeData = edgeData[sourceEdge];
                        Edge targetEdgeData = edgeData[targetEdge];
                        bool2 isEdgeEndMap = new bool2(sourceEdgeData.m_End.Equals(entity), targetEdgeData.m_End.Equals(entity));
                        bool isUnsafe = false;
                        bool isForbidden = false;
                        if (carLaneData.HasComponent(subLaneEntity))
                        {
                            CarLane carLane = carLaneData[subLaneEntity];
                            isUnsafe = (carLane.m_Flags & CarLaneFlags.Unsafe) != 0;
                            isForbidden = (carLane.m_Flags & CarLaneFlags.Forbidden) != 0;
                        }
                        // Logger.DebugConnections(
                        // $"Adding connection (subLane: {subLaneEntity}): idx[{lane.m_StartNode.GetLaneIndex() & 0xff}->{lane.m_EndNode.GetLaneIndex() & 0xff}] edge:[{sourceEdge}=>{targetEdge}] | unsafe?: {isUnsafe}, methods: {subLane.m_PathMethods}");
                        if (!isForbidden && 
                            Helpers.NetUtils.GetAdditionalLaneDetails(sourceEdge, targetEdge, new int2(lane.m_StartNode.GetLaneIndex() & 0xff,lane.m_EndNode.GetLaneIndex() & 0xff), isEdgeEndMap, ref compositionData, ref prefabCompositionLaneBuffer, out float3x2 lanePositionMap, out int4 carriagewayWithGroupMap))
                        {
                            Bezier4x3 bezier = curveData[subLaneEntity].m_Bezier;
                            Connection connection = new Connection(lane, bezier, lanePositionMap, carriagewayWithGroupMap, subLane.m_PathMethods, sourceEdge, targetEdge, isUnsafe, isForbidden);
                            connections.Add(sourceEdge, connection);
                        }
                    }

                    
                    foreach (ConnectedEdge connectedEdge in connectedEdges)
                    {
                        Entity edge = connectedEdge.m_Edge;
                        if (connections.ContainsKey(edge))
                        {
                            foreach (Connection connection in connections.GetValuesForKey(edge))
                            {
                                int sourceLaneIndex = connection.sourceNode.GetLaneIndex() & 0xff;
                                int targetLaneIndex = connection.targetNode.GetLaneIndex() & 0xff;

                                generatedConnectionsMap.Add(
                                    new NodeEdgeLaneKey(entity.Index, edge.Index, sourceLaneIndex),
                                    new GeneratedConnection()
                                    {
                                        sourceEntity = connection.sourceEdge,
                                        targetEntity = connection.targetEdge,
                                        laneIndexMap = new int2(sourceLaneIndex, targetLaneIndex),
                                        lanePositionMap = connection.lanePositionMap,
                                        carriagewayAndGroupIndexMap = connection.laneCarriagewayWithGroupIndexMap,
                                        method = connection.method,
                                        isUnsafe = connection.isUnsafe,
#if DEBUG_GIZMO
                                        debug_bezier = connection.curve,
#endif
                                    });
                            }
                        }
                    }
                    
                    if (!generatedConnectionsMap.IsEmpty)
                    {
                        commandBuffer.AddComponent<ModifiedConnections>(unfilteredChunkIndex, entity);
                        DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections = commandBuffer.AddBuffer<ModifiedLaneConnections>(unfilteredChunkIndex, entity);

                        (NativeArray<NodeEdgeLaneKey> generatedKeys, int uniqueKeyCount) = generatedConnectionsMap.GetUniqueKeyArray(Allocator.Temp);
                        nodeEdgeLaneKeys.ResizeUninitialized(uniqueKeyCount);
                        new NativeSlice<NodeEdgeLaneKey>(generatedKeys, 0, uniqueKeyCount).CopyTo(nodeEdgeLaneKeys.AsArray());
                        
                        for (int j = 0; j < nodeEdgeLaneKeys.Length; j++)
                        {
                            NodeEdgeLaneKey key = nodeEdgeLaneKeys[j];
                            if (generatedConnectionsMap.TryGetFirstValue(key, out GeneratedConnection connection, out NativeParallelMultiHashMapIterator<NodeEdgeLaneKey> iterator))
                            {
                                Entity edgeEntity = connection.sourceEntity;
                                float3 lanePosition = connection.lanePositionMap.c0;
                                int2 carriagewayAndGroup = connection.carriagewayAndGroupIndexMap.xy;
                                Entity modifiedConnectionsEntity = commandBuffer.CreateEntity(unfilteredChunkIndex);
                                commandBuffer.AddComponent<DataOwner>(unfilteredChunkIndex, modifiedConnectionsEntity, new DataOwner(entity));
                                commandBuffer.AddComponent<PrefabRef>(unfilteredChunkIndex, modifiedConnectionsEntity, new PrefabRef(fakePrefabRef));
                                DynamicBuffer<GeneratedConnection> generatedConnections = commandBuffer.AddBuffer<GeneratedConnection>(unfilteredChunkIndex, modifiedConnectionsEntity);
                                do
                                {
                                    generatedConnections.Add(connection);
                                } while (generatedConnectionsMap.TryGetNextValue(out connection, ref iterator));
                                
                                generatedModifiedLaneConnections.Add(new ModifiedLaneConnections()
                                {
                                    edgeEntity = edgeEntity,
                                    laneIndex = key.laneIndex,
                                    lanePosition = lanePosition,
                                    carriagewayAndGroup = carriagewayAndGroup,
                                    modifiedConnections = modifiedConnectionsEntity
                                });
                            }
                        }
                        
                        nodeEdgeLaneKeys.Clear();
                        modifiedLaneConnections.CopyFrom(generatedModifiedLaneConnections.AsArray());
                        generatedModifiedLaneConnections.Clear();
                        generated++;
                        
                    }

                    commandBuffer.AddComponent<Updated>(unfilteredChunkIndex, entity);
                    //update connected nodes of every connected edge
                    for (int j = 0; j < connectedEdges.Length; j++)
                    {
                        Entity edgeEntity = connectedEdges[j].m_Edge;
                        if (!deletedData.HasComponent(edgeEntity))
                        {
                            Edge edge = edgeData[edgeEntity];
                            commandBuffer.AddComponent<Updated>(unfilteredChunkIndex, edgeEntity);
                            Entity otherNode = edge.m_Start == entity ? edge.m_End : edge.m_Start;
                            commandBuffer.AddComponent<Updated>(unfilteredChunkIndex, otherNode);
                        }
                    }
                    
                    generatedConnectionsMap.Clear();
                    connections.Clear();
                }
                
                generatedIntersectionData.Enqueue(generated);
                connections.Dispose();
                generatedConnectionsMap.Dispose();
                generatedModifiedLaneConnections.Dispose();
            }
        }
    }
}
