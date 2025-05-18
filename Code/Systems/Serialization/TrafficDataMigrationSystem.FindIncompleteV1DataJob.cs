using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Edge = Game.Net.Edge;

namespace Traffic.Systems.Serialization
{
    public partial class TrafficDataMigrationSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct FindIncompleteV1DataJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public BufferTypeHandle<ModifiedLaneConnections> modifiedLaneConnectionsTypeHandle;
            [ReadOnly] public BufferTypeHandle<ConnectedEdge> connectedEdgeTypeHandle;
            [ReadOnly] public BufferLookup<GeneratedConnection> generatedConnectionBuffer;
            [ReadOnly] public BufferLookup<NetCompositionLane> netCompositionLaneBuffer;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Deleted> deletedData;
            [ReadOnly] public ComponentLookup<Composition> compositionData;
            [ReadOnly] public ComponentLookup<NetCompositionData> netCompositionData;
            [ReadOnly] public ComponentLookup<RoadComposition> roadCompositionData;
            [ReadOnly] public ComponentLookup<TrackComposition> trackCompositionData;
            [ReadOnly] public int2 invalidCarriagewayAndGroup;
            public NativeQueue<Entity>.ParallelWriter affectedEntities;
            public EntityCommandBuffer.ParallelWriter commandBuffer;
            
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                BufferAccessor<ModifiedLaneConnections> modifiedLaneConnectionsAccesor = chunk.GetBufferAccessor(ref modifiedLaneConnectionsTypeHandle);
                BufferAccessor<ConnectedEdge> connectedEdgeAccessor = chunk.GetBufferAccessor(ref connectedEdgeTypeHandle);
                
                NativeHashMap<Entity, EdgeComposition> edgeCompositionsMap = new NativeHashMap<Entity, EdgeComposition>(4, Allocator.Temp);
                ChunkEntityEnumerator enumerator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (enumerator.NextEntityIndex(out int index))
                {
                    DynamicBuffer<ModifiedLaneConnections> modifiedConnections = modifiedLaneConnectionsAccesor[index];
                    DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgeAccessor[index];
                    
                    bool anyConnectionAffected = false;
                    for (var i = 0; i < modifiedConnections.Length; i++)
                    {
                        anyConnectionAffected |= modifiedConnections[i].carriagewayAndGroup.Equals(invalidCarriagewayAndGroup);
                    }
                    
                    Entity affectedNodeEntity = entities[index];
                    if (anyConnectionAffected)
                    {
                        bool forceResetConnections = false;
                        bool notifyOnly = false;

                        TryMigrateLaneConnectionsSettings(index, affectedNodeEntity, ref edgeCompositionsMap, in modifiedConnections, in connectedEdges, ref forceResetConnections, ref notifyOnly);

                        if (forceResetConnections)
                        {
                            RemoveLaneConnectionsFromNodeEntity(index, affectedNodeEntity, in modifiedConnections, in connectedEdges);
                            affectedEntities.Enqueue(affectedNodeEntity);
                        }
                        else if (notifyOnly)
                        {
                            affectedEntities.Enqueue(affectedNodeEntity);
                        }
                        else
                        {
                            RequestUpdateAtNodeAndEdges(index, affectedNodeEntity, in connectedEdges);
                        }
                    }
                    else if (modifiedConnections.IsEmpty)
                    {
                        Logger.Serialization($"Found empty ModifiedLaneConnections buffer in {affectedNodeEntity}, removing.");
                        commandBuffer.RemoveComponent<ModifiedLaneConnections>(index, affectedNodeEntity);
                        commandBuffer.RemoveComponent<ModifiedConnections>(index, affectedNodeEntity);
                    }
                }
                ResetCompositionMap(ref edgeCompositionsMap);
                edgeCompositionsMap.Dispose();
            }

            private void TryMigrateLaneConnectionsSettings(int jobIndex, Entity nodeEntity, ref NativeHashMap<Entity, EdgeComposition> edgeCompositions, in DynamicBuffer<ModifiedLaneConnections> modifiedConnections, in DynamicBuffer<ConnectedEdge> connectedEdges, ref bool forceResetConnections, ref bool notifyOnly)
            {
                if (!FillEdgeCompositions(nodeEntity, in connectedEdges, ref edgeCompositions))
                {
                    forceResetConnections = true;
                    return;
                }

                NativeList<ModifiedLaneConnections> updatedConnections = new NativeList<ModifiedLaneConnections>(modifiedConnections.Length, Allocator.Temp);
                NativeList<GeneratedConnection> updatedGeneratedConnections = new NativeList<GeneratedConnection>(4, Allocator.Temp);
                bool interrupted = false;
                for (var i = 0; i < modifiedConnections.Length; i++)
                {
                    ModifiedLaneConnections modifiedConnection = modifiedConnections[i];
                    if (!edgeCompositions.TryGetValue(modifiedConnection.edgeEntity, out EdgeComposition compositions))
                    {
                        forceResetConnections = true;
                        Logger.Serialization($"Couldn't find edge {modifiedConnection.edgeEntity} in compositions! Reset all connections at {nodeEntity}");
                        interrupted = true;
                        break;
                    }
                    if (!compositions.compositionLanes.TryGetValue(modifiedConnection.laneIndex, out NetCompositionLane laneData))
                    {
                        forceResetConnections = true;
                        Logger.Serialization($"Couldn't find lane index {modifiedConnection.laneIndex} in composition! Reset all connections at {nodeEntity}");
                        interrupted = true;
                        break;
                    }

                    if (!generatedConnectionBuffer.TryGetBuffer(modifiedConnection.modifiedConnections, out DynamicBuffer<GeneratedConnection> connections))
                    {
                        forceResetConnections = true;
                        Logger.Serialization($"Couldn't find associated entity with generated connection buffer {modifiedConnection.modifiedConnections}! Reset all connections at {nodeEntity}");
                        interrupted = true;
                        break;
                    }
                    
                    // update current connection with new data
                    modifiedConnection.lanePosition = laneData.m_Position;
                    modifiedConnection.carriagewayAndGroup = new int2(laneData.m_Carriageway, laneData.m_Group);
                    
                    updatedGeneratedConnections.Clear();
                    if (!TryFixGeneratedConnections(nodeEntity, modifiedConnection, in connections, ref updatedGeneratedConnections, ref edgeCompositions, ref notifyOnly))
                    {
                        forceResetConnections = true;
                        Logger.Serialization($"Couldn't fix generated connections of {modifiedConnection.modifiedConnections}! Reset all connections at {nodeEntity}");
                        interrupted = true;
                        break;
                    }
                    
                    DynamicBuffer<GeneratedConnection> generatedConnections = commandBuffer.SetBuffer<GeneratedConnection>(jobIndex, modifiedConnection.modifiedConnections);
                    generatedConnections.CopyFrom(updatedGeneratedConnections.AsArray());
                    updatedConnections.Add(modifiedConnection);
                }

                if (!interrupted)
                {
                    DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections = commandBuffer.SetBuffer<ModifiedLaneConnections>(jobIndex, nodeEntity);
                    modifiedLaneConnections.CopyFrom(updatedConnections.AsArray());
                }
                updatedConnections.Dispose();
            }

            private bool TryFixGeneratedConnections(Entity nodeEntity, ModifiedLaneConnections modifiedConnection, in DynamicBuffer<GeneratedConnection> generatedConnections, ref NativeList<GeneratedConnection> updatedGeneratedConnections, ref NativeHashMap<Entity,EdgeComposition> edgeCompositions, ref bool notifyOnly)
            {
                for (var i = 0; i < generatedConnections.Length; i++)
                {
                    GeneratedConnection generatedConnection = generatedConnections[i];
                    if (generatedConnection.sourceEntity != modifiedConnection.edgeEntity)
                    {
                        Logger.Serialization($"Incorrect Source Edge in GeneratedConnections of {modifiedConnection.modifiedConnections}[{i}] - {generatedConnection.sourceEntity}! Reset all connections at {nodeEntity}");
                        updatedGeneratedConnections.Clear();
                        return false;
                    }

                    if (!edgeCompositions.TryGetValue(generatedConnection.targetEntity, out EdgeComposition edgeComposition))
                    {
                        Logger.Serialization($"Incorrect Target Edge in GeneratedConnections of {modifiedConnection.modifiedConnections}[{i}] - {generatedConnection.targetEntity}! Reset all connections at {nodeEntity}");
                        return false;
                    }

                    if (!edgeComposition.compositionLanes.TryGetValue(generatedConnection.laneIndexMap.y, out NetCompositionLane lane))
                    {
                        Logger.Serialization($"Lane with index {generatedConnection.laneIndexMap.y} not found! {modifiedConnection.modifiedConnections}[{i}] - {generatedConnection.targetEntity}. Reset all connections at {nodeEntity}");
                        return false;
                    }
                    if (((generatedConnection.method & PathMethod.Road) != 0) != edgeComposition.isRoad ||
                        ((generatedConnection.method & PathMethod.Track) != 0) != (edgeComposition.trackTypes != 0))
                    {
                        Logger.Serialization($"Incorrect lane type! G: {generatedConnection.method} composition:[isRoad:{edgeComposition.isRoad} track:{edgeComposition.trackTypes}] in {modifiedConnection.modifiedConnections}[{i}] - {generatedConnection.targetEntity}. Reset all connections at {nodeEntity}");
                        return false;
                    }

                    generatedConnection.carriagewayAndGroupIndexMap = new int4(modifiedConnection.carriagewayAndGroup, new int2(lane.m_Carriageway, lane.m_Group));
                    generatedConnection.lanePositionMap = new float3x2(modifiedConnection.lanePosition, lane.m_Position);
                    updatedGeneratedConnections.Add(generatedConnection);
                }
                return true;
            }


            private bool FillEdgeCompositions(Entity nodeEntity, in DynamicBuffer<ConnectedEdge> connectedEdges, ref NativeHashMap<Entity, EdgeComposition> compositions)
            {
                ResetCompositionMap(ref compositions);
                for (var i = 0; i < connectedEdges.Length; i++)
                {
                    ConnectedEdge connectedEdge = connectedEdges[i];
                    
                    if (!compositionData.TryGetComponent(connectedEdge.m_Edge, out Composition data))
                    {
                        return false;
                    }

                    if (i == 0)
                    {
                        Edge edge = edgeData[connectedEdge.m_Edge];
                        bool isEnd = edge.m_End.Equals(nodeEntity);
                        if (netCompositionData.TryGetComponent(isEnd ? data.m_EndNode : data.m_StartNode, out NetCompositionData nodeCompositionData) &&
                            (nodeCompositionData.m_Flags.m_General & CompositionFlags.General.Roundabout) != 0)
                        {
                            Logger.Serialization("Detected Roundabout, remove settings!");
                            return false;
                        }
                    }

                    Entity composition = data.m_Edge;
                    bool isRoad = roadCompositionData.HasComponent(composition);
                    TrackTypes trackTypes = TrackTypes.None;
                    if (trackCompositionData.HasComponent(composition))
                    {
                        trackTypes = trackCompositionData[composition].m_TrackType;
                    }

                    if (!isRoad && trackTypes == 0)
                    {
                        continue; // skip edge with not supported lanes
                    }
                    
                    EdgeComposition edgeComposition = new EdgeComposition()
                    {
                        isRoad = isRoad,
                        trackTypes = trackTypes,
                        composition = composition,
                        compositionLanes = new NativeHashMap<int, NetCompositionLane>(2, Allocator.Temp),
                    };

                    DynamicBuffer<NetCompositionLane> compositionLanes = netCompositionLaneBuffer[composition];
                    
                    for (var j = 0; j < compositionLanes.Length; j++)
                    {
                        NetCompositionLane lane = compositionLanes[j];
                        if ((lane.m_Flags & (LaneFlags.Road | LaneFlags.Slave | LaneFlags.Track)) != 0)
                        {
                            edgeComposition.compositionLanes.Add(j, lane);
                        }
                    }
                    
                    compositions.Add(connectedEdge.m_Edge, edgeComposition);
                }
                return true;
            }

            private void ResetCompositionMap(ref NativeHashMap<Entity, EdgeComposition> compositions)
            {
                NativeArray<Entity> keys = compositions.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < keys.Length; i++)
                {
                    var composition = compositions[keys[i]];
                    if (composition.compositionLanes.IsCreated)
                    {
                        composition.compositionLanes.Dispose();
                    }
                }
                keys.Dispose();
                compositions.Clear();
            }

            private void RemoveLaneConnectionsFromNodeEntity(int jobIndex, Entity entity, in DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections, in DynamicBuffer<ConnectedEdge> connectedEdges)
            {
                for (int i = 0; i < modifiedLaneConnections.Length; i++)
                {
                    Entity modified = modifiedLaneConnections[i].modifiedConnections;
                    if (modified != Entity.Null)
                    {
                        commandBuffer.AddComponent<Deleted>(jobIndex, modified);
                    }
                }
                commandBuffer.RemoveComponent<ModifiedLaneConnections>(jobIndex, entity);
                commandBuffer.RemoveComponent<ModifiedConnections>(jobIndex, entity);

                RequestUpdateAtNodeAndEdges(jobIndex, entity, in connectedEdges);
            }

            private void RequestUpdateAtNodeAndEdges(int jobIndex, Entity node, in DynamicBuffer<ConnectedEdge> connectedEdges)
            {
                if (connectedEdges.Length > 0)
                {
                    //update connected nodes at each edge
                    for (var j = 0; j < connectedEdges.Length; j++)
                    {
                        Entity edgeEntity = connectedEdges[j].m_Edge;
                        if (!deletedData.HasComponent(edgeEntity))
                        {
                            Edge e = edgeData[edgeEntity];
                            commandBuffer.AddComponent<Updated>(jobIndex, edgeEntity);
                            Entity otherNode = e.m_Start == node ? e.m_End : e.m_Start;
                            commandBuffer.AddComponent<Updated>(jobIndex, otherNode);
                        }
                    }
                }
                commandBuffer.AddComponent<Updated>(jobIndex, node);
            }

            private struct EdgeComposition
            {
                public Entity composition;
                public bool isRoad;
                public TrackTypes trackTypes;
                public NativeHashMap<int, NetCompositionLane> compositionLanes;
            }
        }
    }
}
