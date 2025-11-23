using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Traffic.Components;
using Traffic.Components.LaneConnections;
#if WITH_BURST
using Unity.Burst;
#endif
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
        private struct ValidateLoadedDataJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public BufferTypeHandle<ModifiedLaneConnections> modifiedLaneConnectionsTypeHandle;
            [ReadOnly] public BufferTypeHandle<ConnectedEdge> connectedEdgeTypeHandle;
            [ReadOnly] public ComponentTypeHandle<ModifiedConnections> modifiedConnectionsTypeHandle;
            [ReadOnly] public BufferLookup<GeneratedConnection> generatedConnectionBuffer;
            [ReadOnly] public BufferLookup<NetCompositionLane> netCompositionLaneBuffer;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Deleted> deletedData;
            [ReadOnly] public ComponentLookup<DataOwner> dataOwnerData;
            [ReadOnly] public ComponentLookup<Composition> compositionData;
            [ReadOnly] public ComponentLookup<NetCompositionData> netCompositionData;
            [ReadOnly] public ComponentLookup<RoadComposition> roadCompositionData;
            [ReadOnly] public ComponentLookup<TrackComposition> trackCompositionData;
            [ReadOnly] public ComponentLookup<TrackLaneData> trackLaneData;
            [ReadOnly] public ComponentLookup<NetLaneData> netLaneData;
            [ReadOnly] public ComponentLookup<CarLaneData> carLaneData;
            [ReadOnly] public ComponentLookup<PrefabRef> prefabRefData;
            [ReadOnly] public Entity fakePrefabEntity;
            [ReadOnly] public NativeParallelMultiHashMap<Entity, Entity>.ReadOnly dataOwnerRefs;
            [ReadOnly] public EntityStorageInfoLookup entityInfoLookup;
            public NativeQueue<Entity>.ParallelWriter affectedEntities;
            public EntityCommandBuffer.ParallelWriter commandBuffer;
            
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                BufferAccessor<ModifiedLaneConnections> modifiedLaneConnectionsAccessor = chunk.GetBufferAccessor(ref modifiedLaneConnectionsTypeHandle);
                BufferAccessor<ConnectedEdge> connectedEdgeAccessor = chunk.GetBufferAccessor(ref connectedEdgeTypeHandle);
                NativeArray<ModifiedConnections> moddedConnections = chunk.GetNativeArray(ref modifiedConnectionsTypeHandle);

                bool interrupted = false;
                NativeHashMap<Entity, EdgeComposition> edgeCompositionsMap = new NativeHashMap<Entity, EdgeComposition>(4, Allocator.Temp);
                ChunkEntityEnumerator enumerator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (enumerator.NextEntityIndex(out int index))
                {
                    DynamicBuffer<ModifiedLaneConnections> modifiedConnections = modifiedLaneConnectionsAccessor[index];
                    DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgeAccessor[index];
                    
                    Entity affectedNodeEntity = entities[index];
                    
                    if (!modifiedConnections.IsEmpty)
                    {
                        Logger.Serialization($"Validating {affectedNodeEntity}...");
                        if (ValidateLaneConnectionsSettings(index, affectedNodeEntity, ref edgeCompositionsMap, in modifiedConnections, in connectedEdges))
                        {
                            RequestUpdateAtNodeAndEdges(index, affectedNodeEntity, in connectedEdges);
                        }
                        else
                        {
                            interrupted = true;
                            RemoveLaneConnectionsFromNodeEntity(index, affectedNodeEntity, in modifiedConnections, in connectedEdges);
                            affectedEntities.Enqueue(affectedNodeEntity);
                        }
                    }
                    else
                    {
                        interrupted = true;
                        Logger.Serialization($"Found empty ModifiedLaneConnections buffer in {affectedNodeEntity}, removing.");
                        commandBuffer.RemoveComponent<ModifiedLaneConnections>(index, affectedNodeEntity);
                        commandBuffer.RemoveComponent<ModifiedConnections>(index, affectedNodeEntity);
                    }
                }
                // add missing component
                if (!interrupted && !chunk.Has(ref modifiedConnectionsTypeHandle))
                {
                    commandBuffer.AddComponent<ModifiedConnections>(unfilteredChunkIndex, entities);
                }
                ResetCompositionMap(ref edgeCompositionsMap);
                edgeCompositionsMap.Dispose();
            }

            private bool ValidateLaneConnectionsSettings(int jobIndex, Entity nodeEntity, ref NativeHashMap<Entity, EdgeComposition> edgeCompositions, in DynamicBuffer<ModifiedLaneConnections> modifiedConnections, in DynamicBuffer<ConnectedEdge> connectedEdges)
            {
                if (!FillEdgeCompositions(nodeEntity, in connectedEdges, ref edgeCompositions))
                {
                    return false;
                }

                NativeList<ModifiedLaneConnections> updatedConnections = new NativeList<ModifiedLaneConnections>(modifiedConnections.Length, Allocator.Temp);
                NativeList<GeneratedConnection> updatedGeneratedConnections = new NativeList<GeneratedConnection>(4, Allocator.Temp);
                bool forceResetConnections = false;
                for (int i = 0; i < modifiedConnections.Length; i++)
                {
                    ModifiedLaneConnections modifiedConnection = modifiedConnections[i];
                    if (modifiedConnection.modifiedConnections == Entity.Null)
                    {
                        forceResetConnections = true;
                        Logger.Serialization($"Invalid reference to entity with generated connections! Reset all connections at {nodeEntity}");
                        break;
                    }
                    if (!entityInfoLookup.Exists(modifiedConnection.modifiedConnections))
                    {
                        forceResetConnections = true;
                        Logger.Serialization($"Entity referenced by {nameof(modifiedConnection.modifiedConnections)} ({modifiedConnection.modifiedConnections}) does not exist! Reset all connections at {nodeEntity}");
                        break;
                    }
                    if (!edgeData.HasComponent(modifiedConnection.edgeEntity))
                    {
                        forceResetConnections = true;
                        Logger.Serialization($"Couldn't find edge {modifiedConnection.edgeEntity}! Reset all connections at {nodeEntity}");
                        break;
                    }
                    if (!edgeCompositions.TryGetValue(modifiedConnection.edgeEntity, out EdgeComposition compositions))
                    {
                        forceResetConnections = true;
                        Logger.Serialization($"Couldn't find edge {modifiedConnection.edgeEntity} in compositions! Reset all connections at {nodeEntity}");
                        break;
                    }
                    if (!compositions.compositionLanes.TryGetValue(modifiedConnection.laneIndex, out NetCompositionLane laneData))
                    {
                        forceResetConnections = true;
                        Logger.Serialization($"Couldn't find lane index {modifiedConnection.laneIndex} in composition! Reset all connections at {nodeEntity}");
                        break;
                    }

                    if (!generatedConnectionBuffer.TryGetBuffer(modifiedConnection.modifiedConnections, out DynamicBuffer<GeneratedConnection> connections))
                    {
                        forceResetConnections = true;
                        Logger.Serialization($"Couldn't find associated entity with generated connection buffer {modifiedConnection.modifiedConnections}! Reset all connections at {nodeEntity}");
                        break;
                    }
                    
                    // update current connection with new data
                    modifiedConnection.lanePosition = laneData.m_Position;
                    modifiedConnection.carriagewayAndGroup = new int2(laneData.m_Carriageway, laneData.m_Group);

                    updatedGeneratedConnections.Clear();
                    if (!TryFixGeneratedConnections(nodeEntity, modifiedConnection, in connections, ref updatedGeneratedConnections, ref edgeCompositions))
                    {
                        forceResetConnections = true;
                        Logger.Serialization($"Couldn't fix generated connections of {modifiedConnection.modifiedConnections}! Reset all connections at {nodeEntity}");
                        break;
                    }

                    DynamicBuffer<GeneratedConnection> generatedConnections = commandBuffer.SetBuffer<GeneratedConnection>(jobIndex, modifiedConnection.modifiedConnections);
                    generatedConnections.CopyFrom(updatedGeneratedConnections.AsArray());
                    updatedConnections.Add(modifiedConnection);

                    ValidateDataOwner(jobIndex, modifiedConnection.modifiedConnections, nodeEntity);
                    ValidatePrefabRef(jobIndex, modifiedConnection.modifiedConnections);
                }

                if (!forceResetConnections)
                {
                    DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections = commandBuffer.SetBuffer<ModifiedLaneConnections>(jobIndex, nodeEntity);
                    modifiedLaneConnections.CopyFrom(updatedConnections.AsArray());
                }
                updatedConnections.Dispose();

                return !forceResetConnections;
            }

            private bool TryFixGeneratedConnections(Entity nodeEntity, ModifiedLaneConnections modifiedConnection, in DynamicBuffer<GeneratedConnection> generatedConnections, ref NativeList<GeneratedConnection> updatedGeneratedConnections, ref NativeHashMap<Entity,EdgeComposition> edgeCompositions)
            {
                for (var i = 0; i < generatedConnections.Length; i++)
                {
                    GeneratedConnection generatedConnection = generatedConnections[i];
                    if (generatedConnection.sourceEntity != modifiedConnection.edgeEntity || !entityInfoLookup.Exists(generatedConnection.sourceEntity))
                    {
                        Logger.Serialization($"Incorrect Source Edge in GeneratedConnections of {modifiedConnection.modifiedConnections}[{i}] - {generatedConnection.sourceEntity}! Reset all connections at {nodeEntity}");
                        updatedGeneratedConnections.Clear();
                        return false;
                    }

                    if (!edgeCompositions.TryGetValue(generatedConnection.targetEntity, out EdgeComposition edgeComposition) || !entityInfoLookup.Exists(generatedConnection.targetEntity))
                    {
                        Logger.Serialization($"Incorrect Target Edge in GeneratedConnections of {modifiedConnection.modifiedConnections}[{i}] - {generatedConnection.targetEntity}! Reset all connections at {nodeEntity}");
                        return false;
                    }

                    if (!edgeComposition.compositionLanes.TryGetValue(generatedConnection.laneIndexMap.y, out NetCompositionLane lane))
                    {
                        Logger.Serialization($"Lane with index {generatedConnection.laneIndexMap.y} not found! {modifiedConnection.modifiedConnections}[{i}] - {generatedConnection.targetEntity}. Reset all connections at {nodeEntity}");
                        return false;
                    }
                    if ((((generatedConnection.method & PathMethod.Road) != 0) && !edgeComposition.isRoad) ||
                        ((generatedConnection.method == PathMethod.Bicycle) && !edgeComposition.isBike) ||
                        (!generatedConnection.isUnsafe && (generatedConnection.method & PathMethod.Track) != 0) && (edgeComposition.trackTypes == 0))
                    {
                        Logger.Serialization($"Incorrect lane type! G: {generatedConnection.method} composition:[isRoad:{edgeComposition.isRoad} isBike:{edgeComposition.isBike} track:{edgeComposition.trackTypes}] in {modifiedConnection.modifiedConnections}[{i}] - {generatedConnection.targetEntity}. Reset all connections at {nodeEntity}");
                        return false;
                    }

                    if (generatedConnection.isUnsafe && (generatedConnection.method & PathMethod.Track) != 0 && edgeComposition.trackTypes == 0)
                    {
                        Logger.Serialization($"Incorrect lane type! G: {generatedConnection.method} composition:[isRoad:{edgeComposition.isRoad} isBike:{edgeComposition.isBike} track:{edgeComposition.trackTypes}]. Fix method type");
                        generatedConnection.method &= ~PathMethod.Track;
                        if ((generatedConnection.method & PathMethod.Road) == 0)
                        {
                            Logger.Serialization($"Invalid method type for unsafe connection! G: {generatedConnection.method} composition:[isRoad:{edgeComposition.isRoad} isBike:{edgeComposition.isBike} track:{edgeComposition.trackTypes}]. Reset all connections at {nodeEntity}");
                            return false;
                        }
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
                        Logger.Serialization($"Couldn't find composition data for edge {connectedEdge.m_Edge} at node {nodeEntity}");
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

                    if (!netCompositionLaneBuffer.HasBuffer(composition))
                    {
                        Logger.Serialization($"Not found composition lane buffer in ({composition}), remove settings!");
                        continue;
                    }
                    
                    EdgeComposition edgeComposition = new EdgeComposition()
                    {
                        isRoad = isRoad,
                        isBike = false, //evaluated below
                        trackTypes = trackTypes,
                        composition = composition,
                        compositionLanes = new NativeHashMap<int, NetCompositionLane>(2, Allocator.Temp),
                    };
                    bool isBike = false;

                    DynamicBuffer<NetCompositionLane> compositionLanes = netCompositionLaneBuffer[composition];
                    trackTypes = TrackTypes.None;
                    for (var j = 0; j < compositionLanes.Length; j++)
                    {
                        NetCompositionLane lane = compositionLanes[j];
                        if ((lane.m_Flags & (LaneFlags.Road | LaneFlags.BicyclesOnly | LaneFlags.Slave | LaneFlags.Track)) != 0)
                        {
                            edgeComposition.compositionLanes.Add(j, lane);
                            if ((lane.m_Flags & LaneFlags.Track) != 0 && lane.m_Lane != Entity.Null &&
                                trackLaneData.TryGetComponent(lane.m_Lane, out TrackLaneData trackLane))
                            {
                                trackTypes |= trackLane.m_TrackTypes;
                            }
                            if ((lane.m_Flags & LaneFlags.Road) != 0 && lane.m_Lane != Entity.Null &&
                                carLaneData.TryGetComponent(lane.m_Lane, out CarLaneData carLane))
                            {
                                isBike |= ((carLane.m_RoadTypes & RoadTypes.Bicycle) != 0);
                            }
                            if ((lane.m_Flags & LaneFlags.BicyclesOnly) != 0 && lane.m_Lane != Entity.Null &&
                                netLaneData.TryGetComponent(lane.m_Lane, out NetLaneData laneData))
                            {
                                isBike |= ((laneData.m_Flags & LaneFlags.BicyclesOnly) != 0);
                            }
                        }
                    }
                    edgeComposition.isBike = isBike;
                    
                    if (edgeComposition.trackTypes == 0 && trackTypes != TrackTypes.None)
                    {
                        edgeComposition.trackTypes = trackTypes;
                    }
                    Logger.Serialization($"EdgeComposition ({nodeEntity}): {connectedEdge.m_Edge} | {edgeComposition.isRoad} {edgeComposition.trackTypes} {edgeComposition.composition} {edgeComposition.compositionLanes.Count}");
                    
                    if (!isRoad && !isBike && trackTypes == 0)
                    {
                        Logger.Serialization($"Not supported non-road composition, skip Edge!");
                        continue; // skip edge with not supported lanes
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
                Logger.Serialization($"RemoveLaneConnectionsFromNodeEntity ({jobIndex}) [node: {entity}], modifiedConnections: {modifiedLaneConnections.Length}! Removing");
                NativeHashSet<Entity> removed = new NativeHashSet<Entity>(modifiedLaneConnections.Length, Allocator.Temp);
                for (int i = 0; i < modifiedLaneConnections.Length; i++)
                {
                    Entity modified = modifiedLaneConnections[i].modifiedConnections;
                    if (modified != Entity.Null && entityInfoLookup.Exists(modified))
                    {
                        Logger.Serialization($"({jobIndex}) Removing laneConnection ref {modified} from {entity}"); 
                        commandBuffer.AddComponent<Deleted>(jobIndex, modified);
                        removed.Add(modified);
                    }
                    else
                    {
                        Logger.Serialization($"({jobIndex}) LaneConnection[{i}] is null or no longer exists. {modifiedLaneConnections[i]}"); 
                    }
                }
                
                NativeHashSet<Entity> entities = new NativeHashSet<Entity>(math.max(4, modifiedLaneConnections.Length - removed.Count), Allocator.Temp);
                if (dataOwnerRefs.TryGetFirstValue(entity, out Entity dataOwnerRef, out NativeParallelMultiHashMapIterator<Entity> it))
                {
                    do
                    {
                        if (!removed.Contains(dataOwnerRef))
                        {
                            if (entityInfoLookup.Exists(dataOwnerRef))
                            {
                                Logger.Serialization($"({jobIndex})[{dataOwnerRef}] Broken reference to {entity}! Removing"); 
                                entities.Add(dataOwnerRef);
                            }
                            else
                            {
                                Logger.Serialization($"({jobIndex})[{dataOwnerRef}] Found but no longer exists."); 
                            }
                        }
                        else
                        {
                            Logger.Serialization($"({jobIndex})[{dataOwnerRef}] Already removed, skip."); 
                        }
                    } while (dataOwnerRefs.TryGetNextValue(out dataOwnerRef, ref it));
                }
                
                if (!entities.IsEmpty)
                {
                    Logger.Serialization($"({jobIndex}) Found {entities.Count} broken references to {entity}! Removing");
                    commandBuffer.AddComponent<Deleted>(jobIndex, entities.ToNativeArray(Allocator.Temp));
                }

                if (entities.Count > 0 && removed.Count + entities.Count < modifiedLaneConnections.Length)
                {
                    Logger.Serialization($"({jobIndex}) Something went wrong. Not all connections were found! {entity}, r: {removed.Count} e: {entities.Count} mc: {modifiedLaneConnections.Length}");
                }
                
                removed.Dispose();
                entities.Dispose();

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

            private void ValidatePrefabRef(int jobIndex, Entity modifiedConnections)
            {
                if (!prefabRefData.TryGetComponent(modifiedConnections, out PrefabRef prefabRef))
                {
                    Logger.Serialization($"Missing PrefabRef in {modifiedConnections} entity! Adding with {fakePrefabEntity}");
                    commandBuffer.AddComponent(jobIndex, modifiedConnections, new PrefabRef(fakePrefabEntity));
                }
                else if (prefabRef.m_Prefab != fakePrefabEntity)
                {
                    Logger.Serialization($"Incorrect PrefabRef reference {prefabRef.m_Prefab} entity! Updating with {fakePrefabEntity}");
                    commandBuffer.SetComponent(jobIndex, modifiedConnections, new PrefabRef(fakePrefabEntity));
                }
            }

            private void ValidateDataOwner(int jobIndex, Entity modifiedConnections, Entity nodeEntity)
            {
                if (!dataOwnerData.TryGetComponent(modifiedConnections, out DataOwner dataOwner))
                {
                    Logger.Serialization($"Missing DataOwner in {modifiedConnections} entity! Adding with {nodeEntity}");
                    commandBuffer.AddComponent(jobIndex, modifiedConnections, new DataOwner(nodeEntity));
                }
                else if (dataOwner.entity == Entity.Null || dataOwner.entity != nodeEntity)
                {
                    Logger.Serialization($"Incorrect DataOwner ref {dataOwner.entity} in {modifiedConnections} entity! Updating with {nodeEntity}");
                    commandBuffer.SetComponent(jobIndex, modifiedConnections, new DataOwner(nodeEntity));
                }
            }
            
            private struct EdgeComposition
            {
                public Entity composition;
                public bool isRoad;
                public bool isBike;
                public TrackTypes trackTypes;
                public NativeHashMap<int, NetCompositionLane> compositionLanes;
            }
        }
    }
}
