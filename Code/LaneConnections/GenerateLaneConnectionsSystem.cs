// #define DEBUG_CONNECTIONS
using System;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Traffic.Components;
using Traffic.Tools;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.LaneConnections
{
    public partial class GenerateLaneConnectionsSystem : GameSystemBase
    {
        private EntityQuery _query;
        private EntityQuery _definitionQuery;
        private ModificationBarrier3 _modificationBarrier;

        protected override void OnCreate() {
            base.OnCreate();
            _modificationBarrier = World.GetOrCreateSystemManaged<ModificationBarrier3>();
            _query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Updated>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });
            _definitionQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CreationDefinition>(), ComponentType.ReadOnly<ConnectionDefinition>(), ComponentType.ReadOnly<Updated>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });

            RequireForUpdate(_query);
        }

        protected override void OnUpdate() {
            Logger.DebugConnections("Run GenerateLaneConnectionsSystem");
            int count = _query.CalculateEntityCount();
            NativeParallelHashSet<Entity> createdModifiedLaneConnections = new NativeParallelHashSet<Entity>(16, Allocator.TempJob);
            NativeList<Entity> tempNodes = new NativeList<Entity>(count, Allocator.TempJob);
            NativeParallelHashMap<Entity, Entity> tempEntityMap = new NativeParallelHashMap<Entity, Entity>(count*4, Allocator.TempJob);
            
            // TODO investigate if EdgeIterator can be used instead
            FillTempNodeMap fillTempNodeMapJob = new FillTempNodeMap
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                connectedEdgeTypeHandle = SystemAPI.GetBufferTypeHandle<ConnectedEdge>(true),
                tempData = SystemAPI.GetComponentLookup<Temp>(true),
                nodeData = SystemAPI.GetComponentLookup<Node>(true),
                tempNodes = tempNodes.AsParallelWriter(),
                tempEntityMap = tempEntityMap,
            };
            JobHandle jobHandle = fillTempNodeMapJob.Schedule(_query, Dependency);

            //TODO SIMPLIFY
            if (!_definitionQuery.IsEmptyIgnoreFilter)
            {
                // UGLY CODE START (improve/redesign)
                NativeParallelMultiHashMap<Entity, TempModifiedConnections> createdModifiedConnections = new NativeParallelMultiHashMap<Entity, TempModifiedConnections>(4, Allocator.TempJob);
                EntityCommandBuffer entityCommandBuffer = new EntityCommandBuffer(Allocator.TempJob);
                GenerateTempConnectionsJob tempConnectionsJob = new GenerateTempConnectionsJob
                {
                    creationDefinitionTypeHandle = SystemAPI.GetComponentTypeHandle<CreationDefinition>(true),
                    connectionDefinitionTypeHandle = SystemAPI.GetComponentTypeHandle<ConnectionDefinition>(true),
                    tempConnectionBufferTypeHandle = SystemAPI.GetBufferTypeHandle<TempLaneConnection>(true),
                    tempEntityMap = tempEntityMap.AsReadOnly(),
                    createdModifiedLaneConnections = createdModifiedLaneConnections,
                    createdModifiedConnections = createdModifiedConnections,
                    commandBuffer = entityCommandBuffer.AsParallelWriter(),
                };
                JobHandle tempConnectionsHandle = tempConnectionsJob.Schedule(_definitionQuery, jobHandle);
                tempConnectionsHandle.Complete();
                
                // GetUniqueKeyArray() returns sorted array of unique keys, tightly packed from start of array and the number of remaining items!!
                // Length of returned array might be INCORRECT (internal NativeArray.Unique<T>() call is not performing resize for performance reasons)
                (NativeArray<Entity> keys, int uniqueKeyCount) = createdModifiedConnections.GetUniqueKeyArray(Allocator.Temp);
                NativeList<Entity> entities = new NativeList<Entity>(uniqueKeyCount, Allocator.TempJob);
                entities.ResizeUninitialized(uniqueKeyCount);
                new NativeSlice<Entity>(keys, 0, uniqueKeyCount).CopyTo(entities);
                NativeParallelHashSet<Entity> processedEntities = new NativeParallelHashSet<Entity>(entities.Length, Allocator.TempJob);
                MapTempConnectionsJob mapTempConnectionsJob = new MapTempConnectionsJob
                {
                    modifiedConnectionsBuffer = SystemAPI.GetBufferLookup<ModifiedLaneConnections>(true),
                    createdModifiedConnections = createdModifiedConnections,
                    keys = entities,
                    processedEntities = processedEntities,
                    commandBuffer = entityCommandBuffer.AsParallelWriter(),
                };
                JobHandle mapTempHandle = mapTempConnectionsJob.Schedule(entities, 1, tempConnectionsHandle);
                mapTempHandle.Complete();
                entityCommandBuffer.Playback(EntityManager);
                entityCommandBuffer.Dispose();
                processedEntities.Dispose(mapTempHandle);
                entities.Dispose();
                createdModifiedConnections.Dispose();
                // UGLY CODE END
            }

            tempNodes.Dispose(jobHandle);
            tempEntityMap.Dispose(jobHandle);
            createdModifiedLaneConnections.Dispose(jobHandle);

            _modificationBarrier.AddJobHandleForProducer(jobHandle);
            Dependency = jobHandle;
        }

        private struct FillTempNodeMap : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Temp> tempTypeHandle;
            [ReadOnly] public BufferTypeHandle<ConnectedEdge> connectedEdgeTypeHandle;
            [ReadOnly] public ComponentLookup<Node> nodeData;
            [ReadOnly] public ComponentLookup<Temp> tempData;

            public NativeList<Entity>.ParallelWriter tempNodes;
            public NativeParallelHashMap<Entity, Entity> tempEntityMap;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                NativeArray<Temp> temps = chunk.GetNativeArray(ref tempTypeHandle);
                BufferAccessor<ConnectedEdge> connectedEdgeAccessor = chunk.GetBufferAccessor(ref connectedEdgeTypeHandle);

                Logger.DebugConnections($"Run FillTempNodeMap ({entities.Length})[{unfilteredChunkIndex}]");

                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Temp temp = temps[i];
                    tempNodes.AddNoResize(entity);
                    if (temp.m_Original != Entity.Null && nodeData.HasComponent(temp.m_Original))
                    {
                        //temp on node entity can split an edge -> edge entity will be set in m_Original + temp.m_CurvePosition will be larger than 0.0f
                        if (tempEntityMap.TryAdd(temp.m_Original, entity))
                        {
                            Logger.DebugConnections($"Cache node: {temp.m_Original} -> {entity} flags: {temp.m_Flags}");
                        }
                    }
                    else
                    {
                        Logger.DebugConnections($"Not a node: {temp.m_Original} -> {entity} flags: {temp.m_Flags}");
                    }
                    if (connectedEdgeAccessor.Length > 0)
                    {
                        DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgeAccessor[i];
                        for (int j = 0; j < connectedEdges.Length; j++)
                        {
                            Entity edge = connectedEdges[j].m_Edge;
                            if (edge != Entity.Null && tempData.HasComponent(edge))
                            {
                                Temp tempEdge = tempData[edge];
                                if (tempEdge.m_Original != Entity.Null)
                                {
                                    if (tempEntityMap.TryAdd(tempEdge.m_Original, edge))
                                    {
                                        Logger.DebugConnections($"Cache edge of ({temp.m_Original}): {tempEdge.m_Original} -> {edge} flags: {tempEdge.m_Flags}");
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private struct GenerateTempConnectionsJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<CreationDefinition> creationDefinitionTypeHandle;
            [ReadOnly] public ComponentTypeHandle<ConnectionDefinition> connectionDefinitionTypeHandle;
            [ReadOnly] public BufferTypeHandle<TempLaneConnection> tempConnectionBufferTypeHandle;
            [ReadOnly] public NativeParallelHashMap<Entity, Entity>.ReadOnly tempEntityMap;

            public NativeParallelHashSet<Entity> createdModifiedLaneConnections;
            public NativeParallelMultiHashMap<Entity, TempModifiedConnections> createdModifiedConnections;
            public EntityCommandBuffer.ParallelWriter commandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<CreationDefinition> definitions = chunk.GetNativeArray(ref creationDefinitionTypeHandle);
                NativeArray<ConnectionDefinition> connectionDefinitions = chunk.GetNativeArray(ref connectionDefinitionTypeHandle);
                BufferAccessor<TempLaneConnection> tempConnectionsAccessor = chunk.GetBufferAccessor(ref tempConnectionBufferTypeHandle);

                NativeList<GeneratedConnection> tempConnections = new NativeList<GeneratedConnection>(8, Allocator.Temp);
                for (int i = 0; i < definitions.Length; i++)
                {
                    CreationDefinition definition = definitions[i];
                    ConnectionDefinition connectionDefinition = connectionDefinitions[i];
                    tempConnections.Clear();
                    if (tempConnectionsAccessor.Length > 0 &&
                        tempEntityMap.TryGetValue(definition.m_Original, out Entity tempNodeEntity) &&
                        tempEntityMap.TryGetValue(connectionDefinition.edge, out Entity sourceEdgeEntity))
                    {
                        DynamicBuffer<TempLaneConnection> tempLaneConnections = tempConnectionsAccessor[i];
                        for (int j = 0; j < tempLaneConnections.Length; j++)
                        {
                            if (tempEntityMap.TryGetValue(tempLaneConnections[j].targetEntity, out Entity targetEdgeEntity))
                            {
                                tempConnections.Add(new GeneratedConnection
                                {
                                    sourceEntity = sourceEdgeEntity,
                                    targetEntity = targetEdgeEntity,
                                    laneIndexMap = tempLaneConnections[j].laneIndexMap,
                                    method = tempLaneConnections[j].method,
                                    isUnsafe = tempLaneConnections[j].isUnsafe,
#if DEBUG_GIZMO
                                    debug_bezier = tempLaneConnections[j].bezier,
#endif
                                });
                            }
                        }
#if DEBUG_CONNECTIONS
                        Logger.Debug($"Create modified connection: {tempNodeEntity} source: {sourceEdgeEntity}");
                        Logger.Debug($"Temp Connections ({tempConnections.Length}):");
                        for (var k = 0; k < tempConnections.Length; k++)
                        {
                            Logger.Debug($"[{k}] {tempConnections[k].ToString()}");
                        }
                        Logger.Debug("");
#endif
                        createdModifiedConnections.Add(tempNodeEntity, new TempModifiedConnections
                        {
                            dataOwner = tempNodeEntity,
                            owner = connectionDefinition.owner,
                            flags = connectionDefinition.owner != Entity.Null ? TempFlags.Modify : TempFlags.Create,
                            edgeEntity = sourceEdgeEntity,
                            laneIndex = connectionDefinition.laneIndex,
                            generatedConnections = tempConnections.ToArray(Allocator.TempJob)
                        });
                        createdModifiedLaneConnections.Add(tempNodeEntity);
                    }
                }
                tempConnections.Dispose();
            }
        }
        
        private struct MapTempConnectionsJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeParallelMultiHashMap<Entity, TempModifiedConnections> createdModifiedConnections;
            [ReadOnly] public BufferLookup<ModifiedLaneConnections> modifiedConnectionsBuffer;
            [ReadOnly] public NativeList<Entity> keys;
            public NativeParallelHashSet<Entity> processedEntities;
            public EntityCommandBuffer.ParallelWriter commandBuffer;

            public void Execute(int index) {
                Entity nodeEntity = keys[index];
                Logger.DebugConnections($"Checking node: {nodeEntity}");
                if (processedEntities.Contains(nodeEntity))
                {
                    Logger.DebugConnections($"Oh, for some reason already processed!!! {nodeEntity}");
                    return;
                }
                if (!processedEntities.Add(nodeEntity))
                {
                    Logger.DebugConnections($"Adding entity {nodeEntity} to processed entities failed!!");
                }
                if (createdModifiedConnections.TryGetFirstValue(nodeEntity, out TempModifiedConnections item, out NativeParallelMultiHashMapIterator<Entity> iterator))
                {
                    int valueCount = createdModifiedConnections.CountValuesForKey(nodeEntity);
                    DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections;
                    if (!modifiedConnectionsBuffer.HasBuffer(nodeEntity))
                    {
                        Logger.DebugConnections($"No buffer in: {nodeEntity} ({valueCount})");
                        modifiedLaneConnections = commandBuffer.AddBuffer<ModifiedLaneConnections>(index, nodeEntity);
                    }
                    else
                    {
                        Logger.DebugConnections($"Has buffer in: {nodeEntity} ({valueCount})");
                        modifiedLaneConnections = commandBuffer.SetBuffer<ModifiedLaneConnections>(index, nodeEntity);
                    }
                    
                    do
                    {
                        Entity modifiedConnectionEntity = commandBuffer.CreateEntity(index);
                        commandBuffer.AddComponent<Temp>(index, modifiedConnectionEntity, new Temp(item.owner, item.flags));
                        commandBuffer.AddComponent<DataOwner>(index, modifiedConnectionEntity, new DataOwner(item.dataOwner));
                        commandBuffer.AddComponent<CustomLaneConnection>(index, modifiedConnectionEntity);
                        commandBuffer.AddComponent<PrefabRef>(index, modifiedConnectionEntity, new PrefabRef(LaneConnectorToolSystem.FakePrefabRef));
                        DynamicBuffer<GeneratedConnection> generatedConnections = commandBuffer.AddBuffer<GeneratedConnection>(index, modifiedConnectionEntity);
#if DEBUG_CONNECTIONS
                        int length = item.generatedConnections.Length;
#endif
                        generatedConnections.CopyFrom(item.generatedConnections);
                        item.generatedConnections.Dispose();
                        modifiedLaneConnections.Add(new ModifiedLaneConnections()
                        {
                            edgeEntity = item.edgeEntity,
                            laneIndex = item.laneIndex,
                            modifiedConnections = modifiedConnectionEntity,
                        });
#if DEBUG_CONNECTIONS
                        Logger.DebugConnections($"Added modified connection to {nodeEntity}: {modifiedConnectionEntity}, e: {item.edgeEntity} i: {item.laneIndex}, connections: {length}");
#endif
                    } while (createdModifiedConnections.TryGetNextValue(out item, ref iterator));
                }
            }
        }

        private struct TempModifiedConnections
        {
            public Entity dataOwner;
            public Entity owner;
            public TempFlags flags;
            public Entity edgeEntity;
            public int laneIndex;
            public NativeArray<GeneratedConnection> generatedConnections;
        }
    }
}
