using System;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.SceneFlow;
using Game.UI;
using Game.UI.Localization;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.LaneConnections;
#if WITH_BURST
using Unity.Burst;
#endif
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CarLane = Game.Net.CarLane;
using Edge = Game.Net.Edge;
using SubLane = Game.Net.SubLane;

namespace Traffic.Systems.ModCompatibility
{
    public partial class TLEDataMigrationSystem : GameSystemBase
    {
        private EntityQuery _query;
        private ComponentType _tleComponent;

        protected override void OnCreate()
        {
            base.OnCreate();
            Logger.Info($"Initializing {nameof(TLEDataMigrationSystem)}!");
            Type customLaneDirectionType = Type.GetType("C2VM.CommonLibraries.LaneSystem.CustomLaneDirection, C2VM.CommonLibraries.LaneSystem", false);
            try
            {
                _tleComponent = ComponentType.FromTypeIndex(TypeManager.GetTypeIndex(customLaneDirectionType));
                _query = GetEntityQuery(new EntityQueryDesc
                {
                    All = new[] { _tleComponent, ComponentType.ReadOnly<Node>() },
                    None = new[] { ComponentType.ReadOnly<Deleted>(), }
                });
                RequireForUpdate(_query);
            }
            catch (Exception e)
            {
                Enabled = false;
                Logger.Error($"Something went wrong while initializing {nameof(TLEDataMigrationSystem)}. Disabled Migration System!\n{e}");
                UnityEngine.Debug.LogException(e);
            }
        }

        protected override void OnUpdate()
        {
            Logger.Info($"Deserializing data from {_query.CalculateEntityCount()} TLE modified intersections");
            EntityCommandBuffer entityCommandBuffer = new EntityCommandBuffer(Allocator.TempJob);
            NativeQueue<int> generatedIntersectionsCount = new NativeQueue<int>(Allocator.TempJob);
            new MigrateCustomLaneDirectinosJob()
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                connectedEdgeBufferTypeHandle = SystemAPI.GetBufferTypeHandle<ConnectedEdge>(true),
                subLaneBufferTypeHandle = SystemAPI.GetBufferTypeHandle<SubLane>(true),
                carLaneData = SystemAPI.GetComponentLookup<CarLane>(true),
                curveData = SystemAPI.GetComponentLookup<Curve>(true),
                deletedData = SystemAPI.GetComponentLookup<Deleted>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                laneData = SystemAPI.GetComponentLookup<Lane>(true),
                masterLaneData = SystemAPI.GetComponentLookup<MasterLane>(true),
                fakePrefabRef = Traffic.Systems.ModDefaultsSystem.FakePrefabRef,
                generatedIntersectionData = generatedIntersectionsCount.AsParallelWriter(),
                commandBuffer = entityCommandBuffer.AsParallelWriter(),
            }.ScheduleParallel(_query, Dependency).Complete();
            entityCommandBuffer.Playback(EntityManager);
            entityCommandBuffer.Dispose();
            
            int count = 0;
            while (generatedIntersectionsCount.TryDequeue(out int value)) { count += value; }
            generatedIntersectionsCount.Dispose();
            
            // delete TLE data components to prevent data corruption
            NativeArray<Entity> entities = _query.ToEntityArray(Allocator.Temp);
            EntityManager.RemoveComponent(entities, _tleComponent);
            
            Logger.Info($"Deserialized and updated {count} intersections with custom lane connections");
            GameManager.instance.userInterface.appBindings.ShowMessageDialog(
                new MessageDialog("Traffic mod ⇆ Traffic Lights Enhancement Alpha", 
                    $"**Traffic** mod detected **Traffic Lights Enhancement Alpha Lane Direction Tool** data ({entities.Length} intersections).\n\n" +
                    $"Data migration process successfully migrated {count} intersection configurations to the **Traffic's Lane Connector tool**", 
                    LocalizedString.Id("Common.ERROR_DIALOG_CONTINUE")), null);
        }

#if WITH_BURST
        [BurstCompile]  
#endif
        private struct MigrateCustomLaneDirectinosJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public BufferTypeHandle<ConnectedEdge> connectedEdgeBufferTypeHandle;
            [ReadOnly] public BufferTypeHandle<SubLane> subLaneBufferTypeHandle;
            [ReadOnly] public ComponentLookup<CarLane> carLaneData;
            [ReadOnly] public ComponentLookup<Curve> curveData;
            [ReadOnly] public ComponentLookup<Deleted> deletedData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Lane> laneData;
            [ReadOnly] public ComponentLookup<MasterLane> masterLaneData;
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
                        Entity sourceEdge = FindEdge(connectedEdges, lane.m_StartNode);
                        Entity targetEdge = sourceEdge;
                        if (!lane.m_StartNode.OwnerEquals(lane.m_EndNode))
                        {
                            targetEdge = FindEdge(connectedEdges, lane.m_EndNode);
                        }
                        if (sourceEdge == Entity.Null || targetEdge == Entity.Null)
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
                        Bezier4x3 bezier = curveData[subLaneEntity].m_Bezier;
                        // Logger.DebugConnections(
                            // $"Adding connection (subLane: {subLaneEntity}): idx[{lane.m_StartNode.GetLaneIndex() & 0xff}->{lane.m_EndNode.GetLaneIndex() & 0xff}] edge:[{sourceEdge}=>{targetEdge}] | unsafe?: {isUnsafe}, methods: {subLane.m_PathMethods}");
                        if (!isForbidden)
                        {
                            Connection connection = new Connection(lane, bezier, subLane.m_PathMethods, sourceEdge, targetEdge, isUnsafe, isForbidden);
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

            private Entity FindEdge(DynamicBuffer<ConnectedEdge> edges, PathNode node)
            {
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
    }
}
