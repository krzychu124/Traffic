﻿// #define DEBUG_CONNECTIONS
// #define DEBUG_CONNECTIONS_SYNC
using System;
using System.Diagnostics;
using System.Linq;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Traffic.Components;
using Traffic.Helpers;
using Traffic.Tools;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Traffic.LaneConnections
{
    /// <summary>
    /// Sync lane connections on existing nodes (ignore custom connections created on the same frame)
    /// - Node update (when composition didn't change, e.g.: traffic lights, stop signs set/unset)
    /// - Edge update (when composition didn't change significantly e.g.: added sidewalk, barrier, lights, trees)
    /// - Edge split (edge has been split into one or more edges)
    /// - Edge combine (node reduction) after removing 3rd edge, remaining two may trigger node reduction - generate updated edge joining two remaining nodes (when matching composition/asset))
    /// </summary>
    public partial class SyncCustomLaneConnectionsSystem : GameSystemBase
    {
        private EntityQuery _nodeEdgeQuery;
        private EntityQuery _query;

        protected override void OnCreate()
        {
            base.OnCreate();
            _nodeEdgeQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<ConnectedNode>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Updated>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });

            _query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<ConnectedEdge>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Updated>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.Exclude<ModifiedLaneConnections>(), }
            });
            RequireForUpdate(_query);
        }

        protected override void OnUpdate()
        {
            NativeArray<Entity> updatedNodes = _query.ToEntityArray(Allocator.TempJob);
#if DEBUG_CONNECTIONS
            Logger.Debug($"SyncCustomLaneConnectionsSystem Update! ({updatedNodes.Length})");
#endif
            EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob);

            JobHandle mapJobHandle = default;
            NativeParallelHashMap<NodeEdgeKey, Entity> tempMap = default;
            if (!_nodeEdgeQuery.IsEmptyIgnoreFilter)
            {
                int entityCount = _nodeEdgeQuery.CalculateEntityCount();
                tempMap = new NativeParallelHashMap<NodeEdgeKey, Entity>(entityCount * 2, Allocator.TempJob);
                MapOriginalEntities mapEntities = new MapOriginalEntities()
                {
                    entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                    tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                    edgeTypeHandle = SystemAPI.GetComponentTypeHandle<Edge>(true),
                    tempData = SystemAPI.GetComponentLookup<Temp>(true),
                    edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                    nodeData = SystemAPI.GetComponentLookup<Node>(true),
                    connectedEdgeBuffer = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                    nodeEdgeMap = tempMap
                };
                mapJobHandle = mapEntities.Schedule(_nodeEdgeQuery, Dependency);
#if DEBUG_CONNECTIONS
                mapJobHandle.Complete();
                NativeKeyValueArrays<NodeEdgeKey, Entity> keyValueArrays = tempMap.GetKeyValueArrays(Allocator.Temp);
                string s = "NodeEdgeKeyPairs (Sync):\n";
                for (var i = 0; i < keyValueArrays.Length; i++)
                {
                    var pair = (keyValueArrays.Keys[i], keyValueArrays.Values[i]);
                    s += $"{pair.Item1.node} + {pair.Item1.edge} -> {pair.Item2}\n";
                }
                Logger.Debug(s);
#endif
            }

            SyncConnectionsJob job = new SyncConnectionsJob()
            {
                nodeData = SystemAPI.GetComponentLookup<Node>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                tempData = SystemAPI.GetComponentLookup<Temp>(true),
                hiddenData = SystemAPI.GetComponentLookup<Hidden>(true),
                compositionData = SystemAPI.GetComponentLookup<Composition>(true),
                netCompositionData = SystemAPI.GetComponentLookup<NetCompositionData>(true),
                prefabData = SystemAPI.GetComponentLookup<PrefabRef>(true),
                modifiedConnectionsBuffer = SystemAPI.GetBufferLookup<ModifiedLaneConnections>(true),
                connectedEdgeBuffer = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                generatedConnectionBuffer = SystemAPI.GetBufferLookup<GeneratedConnection>(true),
                nodeEdgeMap = tempMap,
                tempNodes = updatedNodes.AsReadOnly(),
                commandBuffer = commandBuffer.AsParallelWriter(),
            };
            JobHandle jobHandle = job.Schedule(updatedNodes.Length, JobHandle.CombineDependencies(Dependency, mapJobHandle));
            jobHandle.Complete();
            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
            updatedNodes.Dispose(jobHandle);
            if (tempMap.IsCreated)
            {
                tempMap.Dispose(jobHandle);
            }
            Dependency = jobHandle;
#if DEBUG_CONNECTIONS
            Logger.Debug("SyncCustomLaneConnectionsSystem Update finished!");
#endif
        }


        private struct MapOriginalEntities : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Temp> tempTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Edge> edgeTypeHandle;
            [ReadOnly] public ComponentLookup<Temp> tempData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Node> nodeData;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgeBuffer;
            public NativeParallelHashMap<NodeEdgeKey, Entity> nodeEdgeMap;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                NativeArray<Temp> temps = chunk.GetNativeArray(ref tempTypeHandle);
                NativeArray<Edge> edges = chunk.GetNativeArray(ref edgeTypeHandle);

                for (int i = 0; i < edges.Length; i++)
                {
                    Entity entity = entities[i];
                    Temp temp = temps[i];
                    Edge edge = edges[i];
                    if ((temp.m_Flags & TempFlags.Delete) != 0)
                    {
                        Logger.DebugConnections($"|Edge|Delete| {entity} T[{temp.m_Original} | {temp.m_Flags}] start: {edge.m_Start} end: {edge.m_End}");
                    }
                    else if ((temp.m_Flags & TempFlags.Replace) != 0)
                    {
#if DEBUG_CONNECTIONS
                        Temp startNodeTemp = tempData[edge.m_Start];
                        Temp endNodeTemp = tempData[edge.m_End];
                        Logger.DebugConnections($"|Edge|Replace| {entity} T[{temp.m_Original} | {temp.m_Flags}]\n" +
                            $"\t\t\t\tStart: {edge.m_Start} | startT: {startNodeTemp.m_Original} [{startNodeTemp.m_Flags}]\n" +
                            $"\t\t\t\tEnd:   {edge.m_End} | endT: {endNodeTemp.m_Original} [{endNodeTemp.m_Flags}]");
#endif
                    }
                    else if (temp.m_Original != Entity.Null)
                    {
#if !DEBUG_CONNECTIONS
                        Temp startNodeTemp = tempData[edge.m_Start];
                        Temp endNodeTemp = tempData[edge.m_End];
                        Logger.DebugConnections($"|Edge|HasOriginal| {entity} T[{temp.m_Original} | {temp.m_Flags}]\n" +
                            $"Start: {edge.m_Start} | startT: {startNodeTemp.m_Original} [{startNodeTemp.m_Flags}]\n" +
                            $"End:   {edge.m_End} | endT: {endNodeTemp.m_Original} [{endNodeTemp.m_Flags}]");
#endif
                        if ((temp.m_Flags & TempFlags.Combine) != 0)
                        {
#if DEBUG_CONNECTIONS
                            Temp startNodeTemp = tempData[edge.m_Start];
                            Temp endNodeTemp = tempData[edge.m_End];
                            Edge originalEdge = edgeData[temp.m_Original];
                            Logger.DebugConnections($"|Edge|HasOriginal|Combine {entity} T[{temp.m_Original} | {temp.m_Flags}]\n" +
                            $"\t\tO_Start: {originalEdge.m_Start} |T_Start: {edge.m_Start} | T[{startNodeTemp.m_Original} | {startNodeTemp.m_Flags}]\n" +
                            $"\t\tO_End:   {originalEdge.m_End} |T_End:   {edge.m_End} | T[{endNodeTemp.m_Original} | {endNodeTemp.m_Flags}]");

                            bool2 isSameNode = new bool2(originalEdge.m_Start.Equals(startNodeTemp.m_Original), originalEdge.m_End.Equals(endNodeTemp.m_Original));
                            bool isStartChanged = !isSameNode.x;
                            Entity deletedNode = isStartChanged ? originalEdge.m_Start : originalEdge.m_End;
                            Entity otherNode = !isStartChanged ? originalEdge.m_Start : originalEdge.m_End;
                            if (connectedEdgeBuffer.HasBuffer(deletedNode))
                            {
                                Entity replacementSourceEdge = Entity.Null;
                                DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgeBuffer[deletedNode];
                                for (var j = 0; j < connectedEdges.Length; j++)
                                {
                                    ConnectedEdge deletedConnectedEdge = connectedEdges[j];
                                    Edge deletedEdge = edgeData[deletedConnectedEdge.m_Edge];
                                    if (deletedConnectedEdge.m_Edge.Equals(temp.m_Original))
                                    {
                                        Logger.DebugConnections($"|Edge|HasOriginal|Combine|TestConnected({j})|Skip| {deletedConnectedEdge.m_Edge} | [{deletedEdge.m_Start}; {deletedEdge.m_End}] ||Nodes deleted: {deletedNode} common: {otherNode}");
                                        continue;
                                    }
                                    Entity node = !deletedEdge.m_Start.Equals(deletedNode) ? deletedEdge.m_Start : deletedEdge.m_End;
                                    Logger.DebugConnections($"|Edge|HasOriginal|Combine|TestConnected({j})| {deletedConnectedEdge.m_Edge} | [{deletedEdge.m_Start}; {deletedEdge.m_End}] ||Nodes deleted: {deletedNode} common: {otherNode}");
                                    if (node.Equals(otherNode))
                                    {
                                        replacementSourceEdge = deletedConnectedEdge.m_Edge;
                                        break;
                                    }
                                }
                                if (replacementSourceEdge != Entity.Null)
                                {
                                    Logger.DebugConnections($"|Edge|HasOriginal|Combine|FoundDeletedEdge {replacementSourceEdge}");
                                    nodeEdgeMap.Add(new NodeEdgeKey(otherNode, replacementSourceEdge), entity);
                                    nodeEdgeMap.Add(new NodeEdgeKey(isStartChanged ? edge.m_End : edge.m_Start, entity), replacementSourceEdge);
                                }
                            }
#endif
                        }
                    }
                    else
                    {
                        Temp startNodeTemp = tempData[edge.m_Start];
                        Temp endNodeTemp = tempData[edge.m_End];
#if DEBUG_CONNECTIONS
                        bool startOriginalIsNode = startNodeTemp.m_Original != Entity.Null && nodeData.HasComponent(startNodeTemp.m_Original);
                        bool endOriginalIsNode = endNodeTemp.m_Original != Entity.Null && nodeData.HasComponent(endNodeTemp.m_Original);
                        Logger.DebugConnections($"|Edge|Else| {entity} T[{temp.m_Original} | {temp.m_Flags}]\n" +
                            $"\t\t\t\tStart: {edge.m_Start} | startT: {startNodeTemp.m_Original} [{startNodeTemp.m_Flags}] isNode: {startOriginalIsNode}\n" +
                            $"\t\t\t\tEnd:   {edge.m_End} | endT: {endNodeTemp.m_Original} [{endNodeTemp.m_Flags}] isNode: {endOriginalIsNode}");
#endif

                        // Node Edge mapping
                        // [node -> edge] : edge
                        // -----------------------------------
                        // [oldNode -> oldEdge] : newEdge
                        // [newNode -> newEdge] : oldEdge
                        if (startNodeTemp.m_Original != Entity.Null && edgeData.HasComponent(startNodeTemp.m_Original))
                        {
                            Edge startOriginalEdge = edgeData[startNodeTemp.m_Original];
                            nodeEdgeMap.Add(new NodeEdgeKey(startOriginalEdge.m_End, startNodeTemp.m_Original), entity);
                            nodeEdgeMap.Add(new NodeEdgeKey(edge.m_End, entity), startNodeTemp.m_Original);
                            Logger.DebugConnections($"|Edge|Else|Start| {entity} T[{temp.m_Original} | {temp.m_Flags}] | OrigEdge: {startNodeTemp.m_Original} start: {startOriginalEdge.m_Start} end: {startOriginalEdge.m_End}");
                        }
                        else
                        {
                            Logger.DebugConnections($"Temp Start original ({startNodeTemp.m_Original}) is Entity.null or not an edge! | {entity} | is node?: {(startNodeTemp.m_Original != Entity.Null ? nodeData.HasComponent(startNodeTemp.m_Original) : Entity.Null)}");
                        }

                        if (endNodeTemp.m_Original != Entity.Null && edgeData.HasComponent(endNodeTemp.m_Original))
                        {
                            Edge endOriginalEdge = edgeData[endNodeTemp.m_Original];
                            nodeEdgeMap.Add(new NodeEdgeKey(endOriginalEdge.m_Start, endNodeTemp.m_Original), entity);
                            nodeEdgeMap.Add(new NodeEdgeKey(edge.m_Start, entity), endNodeTemp.m_Original);
                            Logger.DebugConnections($"|Edge|Else|End| {entity} T[{temp.m_Original} | {temp.m_Flags}] | OrigEdge: {endNodeTemp.m_Original} start: {endOriginalEdge.m_Start} end: {endOriginalEdge.m_End}");
                        }
                        else
                        {
                            Logger.DebugConnections($"Temp End original ({endNodeTemp.m_Original}) is Entity.null or not an edge! | {entity} | is node?: {(endNodeTemp.m_Original != Entity.Null ? nodeData.HasComponent(endNodeTemp.m_Original) : Entity.Null)}");
                        }
                    }
                }
            }
        }

        private struct SyncConnectionsJob : IJobFor
        {
            [ReadOnly] public ComponentLookup<Node> nodeData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Temp> tempData;
            [ReadOnly] public ComponentLookup<Hidden> hiddenData;
            [ReadOnly] public ComponentLookup<Composition> compositionData;
            [ReadOnly] public ComponentLookup<NetCompositionData> netCompositionData;
            [ReadOnly] public ComponentLookup<PrefabRef> prefabData;
            [ReadOnly] public BufferLookup<ModifiedLaneConnections> modifiedConnectionsBuffer;
            [ReadOnly] public BufferLookup<GeneratedConnection> generatedConnectionBuffer;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgeBuffer;
            public NativeParallelHashMap<NodeEdgeKey, Entity> nodeEdgeMap;
            public NativeArray<Entity>.ReadOnly tempNodes;
            public EntityCommandBuffer.ParallelWriter commandBuffer;

            public void Execute(int index)
            {
                Entity entity = tempNodes[index];
                Temp temp = tempData[entity];

                //todo test node composition entity (modifiedConnections edge -> isStart/End node? -> (edge) start/End composition) ((maybe check lane index if still the same))
                Logger.DebugConnectionsSync($"({index}) Testing {entity} Temp entity: {temp.m_Original} flags: {temp.m_Flags}");

                if (temp.m_Original == Entity.Null)
                {
                    Logger.DebugConnectionsSync($"\tSkip, temp {entity} is a new node");
                    return;
                }

                if (!nodeData.HasComponent(temp.m_Original))
                {
                    Logger.DebugConnectionsSync($"\tSkip, temp {entity} is not a node! (probably edge split)");
                    return;
                }

                if (modifiedConnectionsBuffer.HasBuffer(temp.m_Original))
                {

                    if ((temp.m_Flags & TempFlags.Delete) == 0)
                    {
                        EdgeIterator edgeIterator = new EdgeIterator(Entity.Null, entity, connectedEdgeBuffer, edgeData, tempData, hiddenData, false);
                        NativeHashMap<Entity, EdgeInfo> edgeMap = new NativeHashMap<Entity, EdgeInfo>(4, Allocator.Temp);
                        Logger.DebugConnectionsSync($"======= Iterating edges of {entity} =======");
                        while (edgeIterator.GetNext(out EdgeIteratorValue edgeValue))
                        {
                            Logger.DebugConnectionsSync($"({index}) Iterating edges of ({entity}): {edgeValue.m_Edge} isTemp: {tempData.HasComponent(edgeValue.m_Edge)} isEnd: {edgeValue.m_End} isMiddle: {edgeValue.m_Middle}");

                            Edge edge = edgeData[edgeValue.m_Edge];
                            if (tempData.HasComponent(edgeValue.m_Edge))
                            {
                                Temp tempEdge = tempData[edgeValue.m_Edge];
                                Temp startTemp = tempData[edge.m_Start];
                                Temp endTemp = tempData[edge.m_End];
                                Logger.DebugConnectionsSync($"Edge(temp): {edgeValue.m_Edge}:\n\t\t\t\t\t\t\t\t\tStart:[{edge.m_Start}] End:[{edge.m_End}], nodeFlags  S[{startTemp.m_Original} - {startTemp.m_Flags}], E[{endTemp.m_Original} - {endTemp.m_Flags}]");

                                bool2 compositionChanged = false;
                                if (compositionData.HasComponent(edgeValue.m_Edge) && compositionData.HasComponent(tempEdge.m_Original))
                                {
                                    Composition composition = compositionData[edgeValue.m_Edge];
                                    Composition originalComposition = compositionData[tempEdge.m_Original];
#if DEBUG_CONNECTIONS_SYNC
                                    LogCompositionData($"\n\tTemp Edge Composition {edgeValue.m_Edge}", composition.m_Edge, composition.m_StartNode, composition.m_EndNode);
                                    LogCompositionData($"\n\tOriginal Edge Composition {tempEdge.m_Original}", originalComposition.m_Edge, originalComposition.m_StartNode, originalComposition.m_EndNode);
#endif
                                    compositionChanged = CheckComposition(entity, temp.m_Original, tempEdge.m_Original, originalComposition, edgeValue.m_Edge, composition, !edgeValue.m_End, (tempEdge.m_Flags & TempFlags.Modify) != 0);
                                }
                                if (tempEdge.m_Original != Entity.Null)
                                {
                                    Logger.DebugConnectionsSync($"Edge(temp): {edgeValue.m_Edge} with original: {tempEdge.m_Original}");
                                    edgeMap.Add(tempEdge.m_Original, new EdgeInfo() { edge = edgeValue.m_Edge, wasTemp = true, isStart = !edgeValue.m_End, compositionChanged = compositionChanged });
                                }
                                else if (nodeEdgeMap.IsCreated && nodeEdgeMap.TryGetValue(new NodeEdgeKey(entity, edgeValue.m_Edge), out Entity oldEdge))
                                {
                                    Logger.DebugConnectionsSync($"Edge(temp): {edgeValue.m_Edge} with calculated: {oldEdge} | {entity} -> {edgeValue.m_Edge}");
                                    edgeMap.Add(oldEdge, new EdgeInfo() { edge = edgeValue.m_Edge, wasTemp = true, isStart = !edgeValue.m_End, compositionChanged = compositionChanged });
                                }
                            }
                            else
                            {
                                edgeMap.Add(edgeValue.m_Edge, new EdgeInfo() { edge = edgeValue.m_Edge, wasTemp = false, isStart = !edgeValue.m_End, compositionChanged = false });
                                Logger.DebugConnectionsSync($"Edge: {edgeValue.m_Edge}: \n\t\t\t\t\t\t\t\t\tStart:[{edge.m_Start}] End:[{edge.m_End}]");
                            }
                        }
                        Logger.DebugConnectionsSync($"======= Iterating edges of {entity} finished =======");
#if DEBUG_CONNECTIONS_SYNC
                        NativeKeyValueArrays<Entity, EdgeInfo> nativeKeyValueArrays = edgeMap.GetKeyValueArrays(Allocator.Temp);
                        string keyValue = string.Empty;
                        for (var i = 0; i < nativeKeyValueArrays.Length; i++)
                        {
                            keyValue += $"\n\tEdge: {nativeKeyValueArrays.Keys[i]} -> {nativeKeyValueArrays.Values[i].edge} wasTemp: {nativeKeyValueArrays.Values[i].wasTemp} compositionChanged: {nativeKeyValueArrays.Values[i].compositionChanged}";
                        }
                        Logger.DebugConnectionsSync($"EdgeMap: {keyValue}");
#endif
                        NativeList<ModifiedLaneConnections> newModifiedConnections = new NativeList<ModifiedLaneConnections>(Allocator.Temp);
                        DynamicBuffer<ModifiedLaneConnections> laneConnections = modifiedConnectionsBuffer[temp.m_Original];
                        for (var i = 0; i < laneConnections.Length; i++)
                        {
                            ModifiedLaneConnections connection = laneConnections[i];
                            Logger.DebugConnectionsSync($"Testing connection edge: {connection.edgeEntity}, index: {connection.laneIndex} mc: {connection.modifiedConnections}");
                            Entity genEntity = commandBuffer.CreateEntity(index);
                            Temp newModifiedConnectionTemp = new Temp(connection.modifiedConnections, 0);
                            commandBuffer.AddComponent<DataOwner>(index, genEntity, new DataOwner(entity));
                            commandBuffer.AddComponent<PrefabRef>(index, genEntity, new PrefabRef(LaneConnectorToolSystem.FakePrefabRef));
                            if (edgeMap.TryGetValue(connection.edgeEntity, out EdgeInfo newEdgeInfo))
                            {
                                if (!(newEdgeInfo.isStart ? newEdgeInfo.compositionChanged.x : newEdgeInfo.compositionChanged.y))
                                {
                                    Logger.DebugConnectionsSync($"Edge ({connection.edgeEntity}): {newEdgeInfo.edge} {newEdgeInfo.wasTemp} start: {newEdgeInfo.isStart} | {newEdgeInfo.compositionChanged}");
                                    DynamicBuffer<GeneratedConnection> newConnections = commandBuffer.AddBuffer<GeneratedConnection>(index, genEntity);
                                    DynamicBuffer<GeneratedConnection> generatedConnections = generatedConnectionBuffer[connection.modifiedConnections];
                                    for (var k = 0; k < generatedConnections.Length; k++)
                                    {
                                        GeneratedConnection generatedConnection = generatedConnections[k];
                                        if (edgeMap.TryGetValue(generatedConnection.targetEntity, out EdgeInfo genEdgeInfo))
                                        {
                                            if (!(genEdgeInfo.isStart ? genEdgeInfo.compositionChanged.y : genEdgeInfo.compositionChanged.x))
                                            {
                                                Logger.DebugConnectionsSync($"Target ({generatedConnection.targetEntity}): {genEdgeInfo.edge} {genEdgeInfo.wasTemp} start: {genEdgeInfo.isStart} | {genEdgeInfo.compositionChanged}");
                                                GeneratedConnection newConnection = new GeneratedConnection()
                                                {
                                                    sourceEntity = newEdgeInfo.edge,
                                                    targetEntity = genEdgeInfo.edge,
                                                    method = generatedConnection.method,
                                                    isUnsafe = generatedConnection.isUnsafe,
                                                    laneIndexMap = generatedConnection.laneIndexMap,
#if DEBUG_GIZMO
                                                    debug_bezier = generatedConnection.debug_bezier,
#endif
                                                };
                                                newConnections.Add(newConnection);
                                            }
                                            else
                                            {
                                                Logger.DebugConnectionsSync($"Target composition changed ({generatedConnection.targetEntity}): {genEdgeInfo.edge} {genEdgeInfo.wasTemp} start: {genEdgeInfo.isStart} | {genEdgeInfo.compositionChanged}");
                                            }
                                        }
                                        else
                                        {
                                            Entity cachedTarget = Entity.Null;
                                            if (nodeEdgeMap.IsCreated)
                                            {
                                                nodeEdgeMap.TryGetValue(new NodeEdgeKey(temp.m_Original, generatedConnection.targetEntity), out cachedTarget);
                                            }
                                            Logger.DebugConnectionsSync($"No Target ({generatedConnection.targetEntity}) Calc: {cachedTarget}");
                                        }
                                    }
                                    newModifiedConnections.Add(new ModifiedLaneConnections()
                                    {
                                        edgeEntity = newEdgeInfo.edge,
                                        laneIndex = connection.laneIndex,
                                        modifiedConnections = genEntity,
                                    });
                                    newModifiedConnectionTemp.m_Flags |= (newConnections.Length == 0 && generatedConnections.Length > 0) ? TempFlags.Delete : TempFlags.Modify;
                                    Logger.DebugConnectionsSync($"Generated connections for {entity}({temp.m_Original})[{connection.edgeEntity}] => ({newEdgeInfo.edge}): {newConnections.Length} Flags: {newModifiedConnectionTemp.m_Flags}");
                                }
                                else
                                {
                                    newModifiedConnections.Add(new ModifiedLaneConnections()
                                    {
                                        edgeEntity = connection.edgeEntity,
                                        laneIndex = connection.laneIndex,
                                        modifiedConnections = genEntity,
                                    });
                                    newModifiedConnectionTemp.m_Flags |= TempFlags.Delete;
                                    Logger.DebugConnectionsSync($"Edge composition changed! {entity}({temp.m_Original})[{connection.edgeEntity}] NewEdge: {newEdgeInfo.edge} {newEdgeInfo.wasTemp} | {newEdgeInfo.compositionChanged}");
                                }
                            }
                            else
                            {
                                newModifiedConnections.Add(new ModifiedLaneConnections()
                                {
                                    edgeEntity = connection.edgeEntity,
                                    laneIndex = connection.laneIndex,
                                    modifiedConnections = genEntity,
                                });
                                newModifiedConnectionTemp.m_Flags |= TempFlags.Delete;
                                Entity cachedTarget = Entity.Null;
                                if (nodeEdgeMap.IsCreated)
                                {
                                    nodeEdgeMap.TryGetValue(new NodeEdgeKey(temp.m_Original, connection.edgeEntity), out cachedTarget);
                                }
                                Logger.DebugConnectionsSync($"Edge not found! {entity}({temp.m_Original})[{connection.edgeEntity}] Calc: {cachedTarget}");
                            }

                            commandBuffer.AddComponent<Temp>(index, genEntity, newModifiedConnectionTemp);
                        }

                        Logger.DebugConnectionsSync($"Regenerated connections for node: {entity}({temp.m_Original}) ({newModifiedConnections.Length})");
                        if (newModifiedConnections.Length > 0)
                        {
                            DynamicBuffer<ModifiedLaneConnections> modifiedConnection = commandBuffer.AddBuffer<ModifiedLaneConnections>(index, entity);
                            modifiedConnection.CopyFrom(newModifiedConnections.AsArray());
                        }


#if DEBUG_CONNECTIONS_SYNC
                        /*
                         * OLD IMPL.
                         */
                        bool hasTempConnectedEdges = connectedEdgeBuffer.HasBuffer(entity);
                        BufferLookup<GeneratedConnection> buffer = generatedConnectionBuffer;
                        Logger.DebugConnectionsSync($"Modified Connections at node: {temp.m_Original}:\n\t" +
                            string.Join("\n\t",
                                laneConnections.AsNativeArray().Select(l => $"e: {l.edgeEntity} | l: {l.laneIndex} |: c: {l.modifiedConnections} \n\t\t" +
                                    string.Join("\n\t\t",
                                        buffer.TryGetBuffer(l.modifiedConnections, out var data) ? data.ToNativeArray(Allocator.Temp).Select(d => $"[s: {d.sourceEntity} t: {d.targetEntity} idx: {d.laneIndexMap}]") : Array.Empty<string>())))
                        );
                        if (connectedEdgeBuffer.HasBuffer(temp.m_Original))
                        {
                            DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgeBuffer[temp.m_Original];
                            Logger.DebugConnectionsSync($"Has ConnectedEdges:\n\t{string.Join(",\n\t", connectedEdges.AsNativeArray().Select(c => c.m_Edge))}");
                            if (hasTempConnectedEdges)
                            {
                                DynamicBuffer<ConnectedEdge> tempConnectedEdges = connectedEdgeBuffer[entity];
                                Logger.DebugConnectionsSync($"Temp ConnectedEdges:\n\t{string.Join(",\n\t", tempConnectedEdges.AsNativeArray().Select(c => c.m_Edge))}");
                                for (var j = 0; j < tempConnectedEdges.Length; j++)
                                {
                                    ConnectedEdge edge = tempConnectedEdges[j];
                                    if (tempData.HasComponent(edge.m_Edge))
                                    {
                                        Temp t = tempData[edge.m_Edge];
                                        // if ((t.m_Flags & TempFlags.Delete) == 0)
                                        // {
                                        //     newOldEntityMap.Add(t.m_Original, edge.m_Edge);
                                        // }
                                        Logger.DebugConnectionsSync($"Temp ConnectedEdge {edge.m_Edge} -> original: {t.m_Original} flags: {t.m_Flags}");
                                    }
                                }
                            }
                            else
                            {
                                Logger.DebugConnectionsSync("\tNo Temp ConnectedEdges!");
                            }
                        }
                        else
                        {
                            Logger.DebugConnectionsSync($"\tNo connected Edges to {temp.m_Original}");
                        }
#endif
                    }
                    else
                    {
                        Logger.DebugConnectionsSync($"Delete node with modified connections: {temp.m_Original}, tempFlags: {temp.m_Flags}");
                    }
                }
                else
                {
                    Logger.DebugConnectionsSync($"No modified connections at node: {temp.m_Original}, tempFlags: {temp.m_Flags}");
                }
            }

            [Conditional("DEBUG_CONNECTIONS_SYNC")]
            private void LogCompositionData(string text, Entity edgeComposition, Entity startNodeComposition, Entity endNodeComposition)
            {

                NetCompositionData data1 = netCompositionData[edgeComposition];
                NetCompositionData data2 = netCompositionData[startNodeComposition];
                NetCompositionData data3 = netCompositionData[endNodeComposition];
                string compositionStr = $"{text}\n\t\tEdge:  {edgeComposition} \n\t\tStart: {startNodeComposition} \n\t\tEnd:   {endNodeComposition}";
                compositionStr += $"\n\t(NetComposition(Edge):  |G {data1.m_Flags.m_General} |L {data1.m_Flags.m_Left} |R {data1.m_Flags.m_Right} |";
                compositionStr += $"\n\t(NetComposition(Start): |G {data2.m_Flags.m_General} |L {data2.m_Flags.m_Left} |R {data2.m_Flags.m_Right} |";
                compositionStr += $"\n\t(NetComposition(End):   |G {data3.m_Flags.m_General} |L {data3.m_Flags.m_Left} |R {data3.m_Flags.m_Right} |";
                Logger.DebugConnectionsSync(compositionStr);
            }

            private bool2 CheckComposition(Entity tempNode, Entity originalNode, Entity originalEdge, Composition originalComposition, Entity tempEdge, Composition tempComposition, bool isStartEdge, bool isModifyFlag)
            {
                Entity tPrefab = prefabData[tempEdge];
                Entity oPrefab = prefabData[originalEdge];
                Logger.DebugConnectionsSync($"|CheckComposition| tN: {tempNode}, oN: {originalNode} | tE: {tempEdge}, oE: {originalEdge} | iS: {isStartEdge}, modifying: {isModifyFlag} | tPref: {tPrefab}, oPref: {oPrefab}");

                Edge tEdge = edgeData[tempEdge];
                Edge oEdge = edgeData[originalEdge];
                Logger.DebugConnectionsSync($"|CheckComposition|Edges| tE: {tEdge.m_Start} {tEdge.m_End} | oE: {oEdge.m_Start} {oEdge.m_End}");
                //check if edge was not inverted
                bool wasStart = oEdge.m_Start.Equals(originalNode);
                NetCompositionData nodeComposition = netCompositionData[wasStart ? originalComposition.m_StartNode : originalComposition.m_EndNode];
                bool wasStartEdge = (nodeComposition.m_Flags.m_General & CompositionFlags.General.Invert) != 0; //isStartNode ? tEdge.m_Start : tEdge.m_End;
                Logger.DebugConnectionsSync($"|CheckComposition|Direction| tNode: {tempNode}, wasStart: {wasStartEdge} || ");
                if (isStartEdge != wasStartEdge)
                {
                    Logger.DebugConnectionsSync($"|CheckComposition|Direction| Different edge direction! {wasStartEdge} => {isStartEdge}");
                    return true;
                }

                if (oPrefab == tPrefab)
                {
                    CompositionFlags.Side importantFlags = CompositionFlags.Side.ForbidLeftTurn | CompositionFlags.Side.ForbidStraight | CompositionFlags.Side.ForbidRightTurn | CompositionFlags.Side.PrimaryTrack | CompositionFlags.Side.SecondaryTrack |
                        CompositionFlags.Side.TertiaryTrack | CompositionFlags.Side.QuaternaryTrack;
                    // same prefab, same edge direction
                    // check road composition
                    NetCompositionData origEdgeNetCompositionData = netCompositionData[originalComposition.m_Edge];
                    NetCompositionData newEdgeNetCompositionData = netCompositionData[tempComposition.m_Edge];
                    CompositionFlags.Side oELeft = origEdgeNetCompositionData.m_Flags.m_Left & importantFlags;
                    CompositionFlags.Side oERight = origEdgeNetCompositionData.m_Flags.m_Right & importantFlags;
                    CompositionFlags.Side tELeft = newEdgeNetCompositionData.m_Flags.m_Left & importantFlags;
                    CompositionFlags.Side tERight = newEdgeNetCompositionData.m_Flags.m_Right & importantFlags;

                    if (oELeft != tELeft || oERight != tERight)
                    {
                        Logger.DebugConnectionsSync($"|CheckComposition| Different Edge Composition flags = Left: [{oELeft}]->[{tELeft}] Right: [{oERight}]->[{tERight}]");
                        return new bool2(oELeft != tELeft, oERight != tERight);
                    }
                    Logger.DebugConnectionsSync($"|CheckComposition| Acceptable Edge Composition flags = Left: [{oELeft}]->[{tELeft}] Right: [{oERight}]->[{tERight}]");

                    Entity oldNodeCompositionEntity = wasStart ? originalComposition.m_StartNode : originalComposition.m_EndNode;
                    Entity newNodeCompositionEntity = isStartEdge ? tempComposition.m_StartNode : tempComposition.m_EndNode;
                    bool isDifferentComposition = !oldNodeCompositionEntity.Equals(newNodeCompositionEntity);
                    Logger.DebugConnectionsSync($"|CheckComposition| {(isDifferentComposition ? "Different" : "The same")} node composition: {oldNodeCompositionEntity} => {newNodeCompositionEntity}");
                    if (isDifferentComposition)
                    {
                        NetCompositionData oNCompositionData = netCompositionData[oldNodeCompositionEntity];
                        NetCompositionData tNCompositionData = netCompositionData[oldNodeCompositionEntity];
                        CompositionFlags.Side oNLeft = oNCompositionData.m_Flags.m_Left & importantFlags;
                        CompositionFlags.Side oNRight = oNCompositionData.m_Flags.m_Right & importantFlags;
                        CompositionFlags.Side tNLeft = tNCompositionData.m_Flags.m_Left & importantFlags;
                        CompositionFlags.Side tNRight = tNCompositionData.m_Flags.m_Right & importantFlags;
                        if (oNLeft != tNLeft || oNRight != tNRight)
                        {
                            Logger.DebugConnectionsSync($"|CheckComposition| Different Node Composition flags = Left: [{oNLeft}]->[{tNLeft}] Right: [{oNRight}]->[{tNRight}]");
                            return new bool2(oNLeft != tNLeft, oNRight != tNRight);
                        }
                        Logger.DebugConnectionsSync($"|CheckComposition| Acceptable Node Composition flags = Left: [{oELeft}]->[{tELeft}] Right: [{oERight}]->[{tERight}]");
                    }
                    return false;
                }
                else
                {
                    //different road prefab
                    Logger.DebugConnectionsSync($"|CheckComposition| Different prefabs {oPrefab} => {tPrefab}");
                    return true;
                }
            }
        }

        internal struct EdgeInfo
        {
            public Entity edge;
            public bool wasTemp;
            public bool isStart;
            public bool2 compositionChanged;
        }
    }
}
