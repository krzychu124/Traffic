using Colossal.Collections;
using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Traffic.LaneConnections;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.Systems
{
    public partial class ModificationDataSyncSystem : GameSystemBase
    {
        private ModificationBarrier4B _modificationBarrier;
        private EntityQuery _query;

        protected override void OnCreate() {
            base.OnCreate();
            _modificationBarrier = World.GetOrCreateSystemManaged<ModificationBarrier4B>();
            _query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ModifiedLaneConnections>(), ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<Deleted>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), }
            });
            RequireForUpdate(_query);
        }

        protected override void OnUpdate() {
            SyncModificationDataJob job = new SyncModificationDataJob()
            {
                entityType = SystemAPI.GetEntityTypeHandle(),
                // nodeType = SystemAPI.GetComponentTypeHandle<Node>(true),
                tempType = SystemAPI.GetComponentTypeHandle<Temp>(true),
                deletedType = SystemAPI.GetComponentTypeHandle<Deleted>(true),
                // connectedEdges = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                // edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                // tempData = SystemAPI.GetComponentLookup<Temp>(true),
                // hiddenData = SystemAPI.GetComponentLookup<Hidden>(true),
                modifiedLaneConnectionsType = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                // generatedConnectionsType = SystemAPI.GetBufferTypeHandle<GeneratedConnection>(true),
                commandBuffer = _modificationBarrier.CreateCommandBuffer().AsParallelWriter(),
            };
            JobHandle jobHandle = job.Schedule(_query, Dependency);
            _modificationBarrier.AddJobHandleForProducer(jobHandle);
            Dependency = jobHandle;
        }

        private struct SyncModificationDataJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityType;
            // [ReadOnly] public ComponentTypeHandle<Node> nodeType;
            [ReadOnly] public ComponentTypeHandle<Temp> tempType;
            [ReadOnly] public ComponentTypeHandle<Deleted> deletedType;
            // [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdges;
            // [ReadOnly] public ComponentLookup<Edge> edgeData;
            // [ReadOnly] public ComponentLookup<Temp> tempData;
            // [ReadOnly] public ComponentLookup<Hidden> hiddenData;
            [ReadOnly] public BufferTypeHandle<ModifiedLaneConnections> modifiedLaneConnectionsType;
            // [ReadOnly] public BufferTypeHandle<GeneratedConnection> generatedConnectionsType;
            public EntityCommandBuffer.ParallelWriter commandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                if (chunk.Has(ref deletedType))
                {
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                    BufferAccessor<ModifiedLaneConnections> modifiedConnectionsBuffer = chunk.GetBufferAccessor(ref modifiedLaneConnectionsType);
                    if (chunk.Has(ref tempType))
                    {
                        Logger.Info($"Removing Temp node connections (node count: {entities.Length})");
                    }
                    
                    for (var i = 0; i < entities.Length; i++)
                    {
                        var modifiedConnections = modifiedConnectionsBuffer[i];
                        Logger.Info($"Removing node connections {entities[i]} count: ({modifiedConnections.Length})");
                        for (var j = 0; j < modifiedConnections.Length; j++)
                        {
                            ModifiedLaneConnections connections = modifiedConnections[j];
                            if (connections.modifiedConnections != Entity.Null)
                            {
                                Logger.Debug($"Removing generated connections from {entities[i]} [{j}]  -> {connections.modifiedConnections}");
                                commandBuffer.AddComponent<Deleted>(unfilteredChunkIndex, connections.modifiedConnections);
                            }
                        }
                    }
                }
                /*else if (chunk.Has<Updated>())
                {
                    NativeHashSet<Entity> tempEntities = new NativeHashSet<Entity>(4, Allocator.Temp);
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                    BufferAccessor<ModifiedLaneConnections> modifiedConnectionsBuffer = chunk.GetBufferAccessor(ref modifiedLaneConnectionsType);
                    //TODO FIX generated connections
                    // BufferAccessor<GeneratedConnection> generatedConnectionsBuffer = chunk.GetBufferAccessor(ref generatedConnectionsType);
                    for (var i = 0; i < entities.Length; i++)
                    {
                        DynamicBuffer<ModifiedLaneConnections> modifiedConnections = modifiedConnectionsBuffer[i];

                        if (modifiedConnections.Length == 0)
                        {
                            continue;
                        }

                        for (var j = 0; j < modifiedConnections.Length; j++)
                        {
                            ModifiedLaneConnections modifiedLaneConnection = modifiedConnections[j];
                            tempEntities.Add(modifiedLaneConnection.edgeEntity);
                        }

                        EdgeIterator edgeIterator = new EdgeIterator(Entity.Null, entities[i], connectedEdges, edgeData, tempData, hiddenData);
                        while (edgeIterator.GetNext(out EdgeIteratorValue edge))
                        {
                            tempEntities.Remove(edge.m_Edge);
                        }

                        if (tempEntities.Count == 0)
                        {
                            continue; //all edges found
                        }

                        NativeArray<Entity> edges = tempEntities.ToNativeArray(Allocator.Temp);

                        // DynamicBuffer<GeneratedConnection> generatedConnections = generatedConnectionsBuffer[i];
                        int beforeModified = modifiedConnections.Length;
                        // int beforeGenerated = generatedConnections.Length;
                        for (var j = 0; j < edges.Length; j++)
                        {
                            Logger.Info($"Removing connections with edge {edges[j]}");
                            RemoveWithEdge(modifiedConnections, edges[j]);
                            // RemoveWithEdge(generatedConnections, edges[j]);
                        }
                        edges.Dispose();
                        Entity node = entities[i];
                        if (beforeModified != modifiedConnections.Length)
                        {
                            Logger.Info($"Removing ModifiedLaneConnections {beforeModified} != {modifiedConnections.Length}");
                            DynamicBuffer<ModifiedLaneConnections> laneConnectionsEnumerable = commandBuffer.SetBuffer<ModifiedLaneConnections>(unfilteredChunkIndex, node);
                            laneConnectionsEnumerable.ResizeUninitialized(modifiedConnections.Length);
                            for (var j = 0; j < laneConnectionsEnumerable.Length; j++)
                            {
                                laneConnectionsEnumerable[j] = modifiedConnections[j];
                            }
                        }
                        // if (beforeGenerated != generatedConnections.Length)
                        // {
                        //     Logger.Info($"Removing GeneratedConnection {beforeGenerated} != {generatedConnections.Length}");
                        //     DynamicBuffer<GeneratedConnection> generatedConnectionsEnumerable = commandBuffer.SetBuffer<GeneratedConnection>(unfilteredChunkIndex, node);
                        //     for (var j = 0; j < generatedConnectionsEnumerable.Length; j++)
                        //     {
                        //         generatedConnectionsEnumerable[j] = generatedConnections[j];
                        //     }
                        // }
                        tempEntities.Clear();
                    }
                    tempEntities.Dispose();
                }*/
            }

            public void RemoveWithEdge(DynamicBuffer<ModifiedLaneConnections> buffer, Entity edge) {
                for (var i = 0; i < buffer.Length; i++)
                {
                    if (buffer[i].edgeEntity.Index == edge.Index)
                    {
                        buffer.RemoveAtSwapBack(i);
                        i--;
                    }
                }
            }
            
            public void RemoveWithEdge(DynamicBuffer<GeneratedConnection> buffer, Entity edge) {
                for (var i = 0; i < buffer.Length; i++)
                {
                    if (buffer[i].sourceEntity.Index == edge.Index || buffer[i].targetEntity.Index == edge.Index)
                    {
                        buffer.RemoveAtSwapBack(i);
                        i--;
                    }
                }
            }
        }
    }
}
