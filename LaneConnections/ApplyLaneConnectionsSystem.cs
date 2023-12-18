using System;
using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Edge = Game.Net.Edge;

namespace Traffic.LaneConnections
{
    public partial class ApplyLaneConnectionsSystem : GameSystemBase
    {
        private EntityQuery _tempQuery;
        private ToolOutputBarrier _toolOutputBarrier;
        
        protected override void OnCreate() {
            base.OnCreate();
            _toolOutputBarrier = World.GetExistingSystemManaged<ToolOutputBarrier>();
            _tempQuery = GetEntityQuery(ComponentType.ReadOnly<CustomLaneConnection>(), ComponentType.ReadOnly<Temp>());
            RequireForUpdate(_tempQuery);
        }

        protected override void OnUpdate() {
            Logger.Info($"ApplyLaneConnectionsSystem: Process {_tempQuery.CalculateEntityCount()} entities");

            HandleTempConnectionsJob job = new HandleTempConnectionsJob()
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                connectionDataTypeHandle = SystemAPI.GetComponentTypeHandle<ConnectionData>(true),
                laneData = SystemAPI.GetComponentLookup<Lane>(true),
                ownerData = SystemAPI.GetComponentLookup<Owner>(true),
                connectionData = SystemAPI.GetComponentLookup<ConnectionData>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                subLaneData = SystemAPI.GetBufferLookup<SubLane>(true),
                connectedEdgesData = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                // forbiddenConnectionData = SystemAPI.GetBufferLookup<ForbiddenConnection>(true),
                generatedConnectionData = SystemAPI.GetBufferLookup<GeneratedConnection>(true),
                modifiedLaneConnectionData = SystemAPI.GetBufferLookup<ModifiedLaneConnections>(true),
                tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                commandBuffer = _toolOutputBarrier.CreateCommandBuffer().AsParallelWriter(),
            };
            JobHandle jobHandle = job.Schedule(_tempQuery, Dependency);
            _toolOutputBarrier.AddJobHandleForProducer(jobHandle);
            Dependency = jobHandle;
        }

        private struct HandleTempConnectionsJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<ConnectionData> connectionDataTypeHandle;
            [ReadOnly] public ComponentLookup<Lane> laneData;
            [ReadOnly] public ComponentLookup<Owner> ownerData;
            [ReadOnly] public ComponentLookup<ConnectionData> connectionData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public BufferLookup<SubLane> subLaneData;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgesData;
            [ReadOnly] public BufferLookup<GeneratedConnection> generatedConnectionData;
            // [ReadOnly] public BufferLookup<ForbiddenConnection> forbiddenConnectionData;
            [ReadOnly] public BufferLookup<ModifiedLaneConnections> modifiedLaneConnectionData;
            [ReadOnly] public ComponentTypeHandle<Temp> tempTypeHandle;
            public EntityCommandBuffer.ParallelWriter commandBuffer;
            
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                var entities = chunk.GetNativeArray(entityTypeHandle);
                var temps = chunk.GetNativeArray(ref tempTypeHandle);
                bool hasConnectionData = chunk.Has(ref connectionDataTypeHandle);

                //TODO Optimize code
                NativeList<Entity> updatedNodes = new NativeList<Entity>(Allocator.Temp);
                NativeParallelMultiHashMap<Entity, LaneEnd> touchedLaneEnds = new NativeParallelMultiHashMap<Entity, LaneEnd>(1, Allocator.Temp);
                for (var i = 0; i < entities.Length; i++)
                {
                    Entity e = entities[i];
                    Temp temp = temps[i];
                    if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Create)) != 0 && hasConnectionData)
                    {
                        ConnectionData data = connectionData[e];
                        Entity owner = temp.m_Original;
                        if (owner != Entity.Null)
                        {
                            Logger.Info($"Temp entity: {temp.m_Original}, flags {temp.m_Flags} | {data.sourceEdge} -> {data.targetEdge} : {data.laneIndexMap}");

                            if ((temp.m_Flags & TempFlags.Delete) != 0)
                            {
                                if (generatedConnectionData.HasBuffer(owner))
                                {
                                    int index = -1;
                                    DynamicBuffer<GeneratedConnection> generatedConnections = generatedConnectionData[owner];
                                    for (int j = 0; j < generatedConnections.Length; j++)
                                    {
                                        GeneratedConnection con = generatedConnections[j];
                                        Logger.Info($"Testing connection: {con.sourceEntity} => {con.targetEntity} : {con.laneIndexMap}");
                                        if (con.sourceEntity.Index == data.sourceEdge.Index &&
                                            con.targetEntity.Index == data.targetEdge.Index &&
                                            math.all(con.laneIndexMap == data.laneIndexMap))
                                        {
                                            index = j;
                                            Logger.Info($"Found connection: {j}");
                                            break;
                                        }
                                    }
                                    if (index >= 0)
                                    {
                                        generatedConnections.RemoveAtSwapBack(index);
                                        DynamicBuffer<GeneratedConnection> dynamicBuffer = commandBuffer.SetBuffer<GeneratedConnection>(unfilteredChunkIndex, owner);
                                        dynamicBuffer.ResizeUninitialized(generatedConnections.Length);
                                        for (int k = 0; k < dynamicBuffer.Length; k++)
                                        {
                                            dynamicBuffer[k] = generatedConnections[k];
                                        }
                                    
                                        updatedNodes.Add(owner);
                                    }
                                }
                            }
                            else
                            {
                                if (generatedConnectionData.HasBuffer(owner))
                                {
                                    GeneratedConnection generatedConnection = new GeneratedConnection()
                                    {
                                        method = data.method,
                                        sourceEntity = data.sourceEdge,
                                        targetEntity = data.targetEdge,
                                        laneIndexMap = data.laneIndexMap,
                                        isUnsafe = data.isUnsafe,
                                    };
                                    commandBuffer.AppendToBuffer(unfilteredChunkIndex, owner, generatedConnection);
                                    updatedNodes.Add(owner);
                                }
                            }

                            touchedLaneEnds.Add(owner, new LaneEnd(owner , data.sourceEdge, data.laneIndexMap.x));
                        }
                    }
                }
                
                if (updatedNodes.Length > 0)
                {
                    NativeHashSet<LaneEnd> laneEnds = new NativeHashSet<LaneEnd>(4, Allocator.Temp);
                    for (var i = 0; i < updatedNodes.Length; i++)
                    {
                        Entity n = updatedNodes[i];
                        commandBuffer.AddComponent<Updated>(unfilteredChunkIndex, n);
                        DynamicBuffer<ConnectedEdge> edges = connectedEdgesData[n];
                        if (edges.Length > 0)
                        {
                            //update connected nodes of every edge
                            for (var j = 0; j < edges.Length; j++)
                            {
                                commandBuffer.AddComponent<Updated>(unfilteredChunkIndex, edges[j].m_Edge);
                                Edge e = edgeData[edges[j].m_Edge];
                                Entity otherNode = e.m_Start == n ? e.m_End : e.m_Start;
                                commandBuffer.AddComponent<Updated>(unfilteredChunkIndex, otherNode);
                            }
                            
                            laneEnds.Clear();
                            DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections = modifiedLaneConnectionData[n];
                            for (int j = 0; j < modifiedLaneConnections.Length; j++)
                            {
                                ModifiedLaneConnections connection = modifiedLaneConnections[j];
                                laneEnds.Add(new LaneEnd(n, connection.edgeEntity, connection.laneIndex));
                            }
                            
                            if (touchedLaneEnds.TryGetFirstValue(n, out LaneEnd end, out NativeParallelMultiHashMapIterator<Entity> it))
                            {
                                do
                                {
                                    laneEnds.Add(end);
                                } while (touchedLaneEnds.TryGetNextValue(out end, ref it));
    
                                NativeArray<LaneEnd> ends = laneEnds.ToNativeArray(Allocator.Temp);
                                DynamicBuffer<ModifiedLaneConnections> buffer = commandBuffer.SetBuffer<ModifiedLaneConnections>(unfilteredChunkIndex, n);
                                buffer.ResizeUninitialized(ends.Length);
                                for (int j = 0; j < ends.Length; j++)
                                {
                                    buffer[j] = new ModifiedLaneConnections() { edgeEntity = ends[j].Edge(), laneIndex = ends[j].LaneIndex() };
                                }
                            }
                        }
                    }
                    laneEnds.Dispose();

                }
                touchedLaneEnds.Dispose();
                updatedNodes.Dispose();
            }
            
            private struct LaneEnd : IEquatable<LaneEnd>
            {
                private int3 _data;
                private Entity _edge;

                public LaneEnd(Entity node, Entity edge, int laneIdx) {
                    _data = new int3(node.Index, edge.Index, laneIdx);
                    _edge = edge;
                }

                public bool MatchingOwner(Entity owner) {
                    return _data.x == owner.Index;
                }

                public int LaneIndex() {
                    return _data.z;
                }

                public Entity Edge() {
                    return _edge;
                }
            
                public bool Equals(LaneEnd other) {
                    return _data.Equals(other._data);
                }
            
                public override int GetHashCode()
                {
                    return _data.GetHashCode();
                }
            }
        }
    }
}
