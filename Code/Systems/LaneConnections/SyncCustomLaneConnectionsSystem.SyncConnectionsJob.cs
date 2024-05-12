using System;
using System.Linq;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Traffic.Systems.LaneConnections
{
    public partial class SyncCustomLaneConnectionsSystem
    {
        internal struct EdgeInfo
        {
            public Entity edge;
            public bool wasTemp;
            public bool isStart;
            public bool2 compositionChanged;
            public NativeHashMap<int, ValueTuple<int3, float3>> compositionMapping;
        }

#if WITH_BURST
        [BurstCompile]
#endif
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
            [ReadOnly] public BufferLookup<NetCompositionLane> netCompositionLaneBuffer;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgeBuffer;
            [ReadOnly] public Entity fakePrefabRef;
            [ReadOnly] public bool leftHandTraffic;
            public NativeParallelHashMap<NodeEdgeKey, Entity> nodeEdgeMap;
            public NativeArray<Entity>.ReadOnly tempNodes;
            public EntityCommandBuffer.ParallelWriter commandBuffer;

            public void Execute(int index)
            {
                Entity entity = tempNodes[index];
                Temp temp = tempData[entity];
                
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

                                bool2 compositionChanged = false; //todo test with 'true'
                                bool originalEdgeNotNull = tempEdge.m_Original != Entity.Null;
                                NativeHashMap<int, ValueTuple<int3, float3>> compositionMapping = default;
                                if (compositionData.HasComponent(edgeValue.m_Edge) && originalEdgeNotNull && compositionData.HasComponent(tempEdge.m_Original))
                                {
                                    Entity originalEdge = tempEdge.m_Original;
                                    Composition composition = compositionData[edgeValue.m_Edge];
                                    Composition originalComposition = compositionData[originalEdge];
                                    if ((tempEdge.m_Flags & TempFlags.Combine) != 0)
                                    {
                                        if (nodeEdgeMap.IsCreated && nodeEdgeMap.TryGetValue(new NodeEdgeKey(entity, edgeValue.m_Edge), out Entity oldEdgeValue) && compositionData.HasComponent(oldEdgeValue))
                                        {
                                            originalComposition = compositionData[oldEdgeValue];
                                            Logger.DebugConnectionsSync($"Edge(temp-isCombine): {edgeValue.m_Edge}[{originalEdge}] replacing old: {oldEdgeValue}");
                                            originalEdge = oldEdgeValue;
                                        }
                                    }
#if DEBUG_CONNECTIONS_SYNC
                                    LogCompositionData($"\n\tTemp Edge Composition {edgeValue.m_Edge}", composition.m_Edge, composition.m_StartNode, composition.m_EndNode);
                                    LogCompositionData($"\n\tOriginal Edge Composition {tempEdge.m_Original}", originalComposition.m_Edge, originalComposition.m_StartNode, originalComposition.m_EndNode);
#endif
                                    compositionChanged = CheckComposition(entity, temp.m_Original, originalEdge, originalComposition, edgeValue.m_Edge, composition, !edgeValue.m_End, (tempEdge.m_Flags & TempFlags.Modify) != 0);
                                    if (math.all(!compositionChanged))
                                    {
                                        /*assume old and current edge has the same direction (would have the same EdgeIterator m_End value) */
                                        compositionMapping = CalculateEdgeLaneCompositionMapping(originalEdge, originalComposition, edgeValue.m_Edge, edgeValue.m_End, composition);
                                    }
                                }
                                if (originalEdgeNotNull)
                                {
                                    Logger.DebugConnectionsSync($"Edge(temp): {edgeValue.m_Edge} with original: {tempEdge.m_Original}");
                                    
                                    edgeMap.Add(tempEdge.m_Original, new EdgeInfo() { edge = edgeValue.m_Edge, wasTemp = true, isStart = !edgeValue.m_End, compositionChanged = compositionChanged, compositionMapping = compositionMapping});
                                }
                                
                                if (nodeEdgeMap.IsCreated && nodeEdgeMap.TryGetValue(new NodeEdgeKey(entity, edgeValue.m_Edge), out Entity oldEdge))
                                {
                                    Logger.DebugConnectionsSync($"Edge(temp): {edgeValue.m_Edge} with calculated: {oldEdge} | {entity} -> {edgeValue.m_Edge}");
                                    if (!originalEdgeNotNull && compositionData.HasComponent(edgeValue.m_Edge))
                                    {
                                        Composition composition = compositionData[edgeValue.m_Edge];
                                        compositionMapping = CalculateEdgeLaneCompositionMapping(edgeValue.m_Edge, composition, edgeValue.m_Edge, edgeValue.m_End, composition);
                                    }
                                    edgeMap.Add(oldEdge, new EdgeInfo() { edge = edgeValue.m_Edge, wasTemp = true, isStart = !edgeValue.m_End, compositionChanged = compositionChanged, compositionMapping = compositionMapping });
                                }
                            }
                            else
                            {
                                NativeHashMap<int, ValueTuple<int3, float3>> compositionMapping = default;
                                if (compositionData.HasComponent(edgeValue.m_Edge))
                                {
                                    Composition composition = compositionData[edgeValue.m_Edge];
                                    compositionMapping = CalculateEdgeLaneCompositionMapping(edgeValue.m_Edge, composition, edgeValue.m_Edge, edgeValue.m_End, composition);
                                }
                                edgeMap.Add(edgeValue.m_Edge, new EdgeInfo() { edge = edgeValue.m_Edge, wasTemp = false, isStart = !edgeValue.m_End, compositionChanged = false, compositionMapping = compositionMapping });
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
                            commandBuffer.AddComponent<PrefabRef>(index, genEntity, new PrefabRef(fakePrefabRef));
                            if (edgeMap.TryGetValue(connection.edgeEntity, out EdgeInfo newEdgeInfo))
                            {
                                if (!(newEdgeInfo.isStart ? newEdgeInfo.compositionChanged.x : newEdgeInfo.compositionChanged.y))
                                {
                                    NativeHashMap<int, ValueTuple<int3, float3>> newEdgeCompositionMapping = newEdgeInfo.compositionMapping;
                                    
                                    Logger.DebugConnectionsSync($"Edge ({connection.edgeEntity}): {newEdgeInfo.edge} {newEdgeInfo.wasTemp} start: {newEdgeInfo.isStart} | {newEdgeInfo.compositionChanged}");
                                    DynamicBuffer<GeneratedConnection> newConnections = commandBuffer.AddBuffer<GeneratedConnection>(index, genEntity);
                                    DynamicBuffer<GeneratedConnection> generatedConnections = generatedConnectionBuffer[connection.modifiedConnections];
                                    for (var k = 0; k < generatedConnections.Length; k++)
                                    {
                                        GeneratedConnection generatedConnection = generatedConnections[k];
                                        if (newEdgeCompositionMapping.IsCreated &&
                                            edgeMap.TryGetValue(generatedConnection.targetEntity, out EdgeInfo genEdgeInfo))
                                        {
                                            if (!(genEdgeInfo.isStart ? genEdgeInfo.compositionChanged.y : genEdgeInfo.compositionChanged.x))
                                            {
                                                NativeHashMap<int, ValueTuple<int3, float3>> genEdgeCompositionMapping = genEdgeInfo.compositionMapping;
                                                Logger.DebugConnectionsSync($"Target ({generatedConnection.targetEntity}): {genEdgeInfo.edge} {genEdgeInfo.wasTemp} start: {genEdgeInfo.isStart} | {genEdgeInfo.compositionChanged} || {(newEdgeCompositionMapping.IsCreated ? newEdgeCompositionMapping.Count : -1)} -> {(genEdgeCompositionMapping.IsCreated ? genEdgeCompositionMapping.Count : -1)}");
                                                if (genEdgeCompositionMapping.IsCreated)
                                                {
                                                    ValueTuple<int3, float3> sourceMap = newEdgeCompositionMapping[generatedConnection.laneIndexMap.x];
                                                    ValueTuple<int3, float3> targetMap = genEdgeCompositionMapping[generatedConnection.laneIndexMap.y];
                                                    Logger.DebugConnectionsSync($"Mappings ({newEdgeInfo.edge} -> {genEdgeInfo.edge}): [{generatedConnection.laneIndexMap}-{new int2(sourceMap.Item1.x, targetMap.Item1.x)}], [{generatedConnection.carriagewayAndGroupIndexMap}-{new int4(sourceMap.Item1.yz, targetMap.Item1.yz)}], [{generatedConnection.lanePositionMap}-{new float3x2(sourceMap.Item2, targetMap.Item2)}]");
                                                    if (math.any(new bool2(math.any(sourceMap.Item1 < 0), math.any(targetMap.Item1 < 0))))
                                                    {
                                                        Logger.DebugConnectionsSync($"Invalid mapping! ({generatedConnection.targetEntity}): {genEdgeInfo.edge} {genEdgeInfo.wasTemp} start: {genEdgeInfo.isStart} | {genEdgeInfo.compositionChanged}");
                                                        continue;
                                                    }
                                                    GeneratedConnection newConnection = new GeneratedConnection()
                                                    {
                                                        sourceEntity = newEdgeInfo.edge,
                                                        targetEntity = genEdgeInfo.edge,
                                                        method = generatedConnection.method,
                                                        isUnsafe = generatedConnection.isUnsafe,
                                                        laneIndexMap = new int2(sourceMap.Item1.x, targetMap.Item1.x),
                                                        lanePositionMap = new float3x2(sourceMap.Item2, targetMap.Item2),
                                                        carriagewayAndGroupIndexMap = new int4(sourceMap.Item1.yz, targetMap.Item1.yz),
#if DEBUG_GIZMO
                                                        debug_bezier = generatedConnection.debug_bezier,
#endif
                                                    };
                                                    newConnections.Add(newConnection);
                                                }
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

                                    bool validComposition = false;
                                    if (newEdgeCompositionMapping.IsCreated && newEdgeCompositionMapping.TryGetValue(connection.laneIndex, out ValueTuple<int3, float3> pair) && math.any(pair.Item1 >= 0))
                                    {
                                        validComposition = true;
                                        newModifiedConnections.Add(new ModifiedLaneConnections()
                                        {
                                            edgeEntity = newEdgeInfo.edge,
                                            laneIndex = pair.Item1.x,
                                            lanePosition = pair.Item2,
                                            carriagewayAndGroup = pair.Item1.yz,
                                            modifiedConnections = genEntity,
                                        });
                                    }
                                    else
                                    {
                                        newModifiedConnections.Add(new ModifiedLaneConnections()
                                        {
                                            edgeEntity = newEdgeInfo.edge,
                                            laneIndex = connection.laneIndex,
                                            lanePosition = connection.lanePosition,
                                            carriagewayAndGroup = connection.carriagewayAndGroup,
                                            modifiedConnections = genEntity,
                                        });
                                    }
                                    
                                    newModifiedConnectionTemp.m_Flags |= ((newConnections.Length == 0 && generatedConnections.Length > 0 ) || !validComposition) ? TempFlags.Delete : TempFlags.Modify;
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

                        var valueArray = edgeMap.GetValueArray(Allocator.Temp);
                        foreach (EdgeInfo edgeInfo in valueArray)
                        {
                            edgeInfo.compositionMapping.Dispose();
                        }
                        valueArray.Dispose();

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
                                laneConnections.AsNativeArray().Select(l => $"e: {l.edgeEntity} | l: {l.laneIndex} |: c: {l.modifiedConnections} | {l.carriagewayAndGroup}, {l.lanePosition} \n\t\t" +
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
                compositionStr += $"\n\t(NetCompositionLanes:\n\t{string.Join("\n\t", netCompositionLaneBuffer[edgeComposition].AsNativeArray().Select((l,i) => $"[{i}] {l.m_Position}|c: {l.m_Carriageway}||g: {l.m_Group}|i: {l.m_Index}|lane: {l.m_Lane}|flags:[{l.m_Flags}]"))}|";
                Logger.DebugConnectionsSync(compositionStr);
            }

            private bool2 CheckComposition(Entity tempNode, Entity originalNode, Entity originalEdge, Composition originalComposition, Entity tempEdge, Composition tempComposition, bool isStartEdge, bool isModifyFlag)
            {
                Entity tPrefab = prefabData[tempEdge];
                Entity oPrefab = prefabData[originalEdge];
                // Logger.DebugConnectionsSync($"|CheckComposition| tN: {tempNode}, oN: {originalNode} | tE: {tempEdge}, oE: {originalEdge} | iS: {isStartEdge}, modifying: {isModifyFlag} | tPref: {tPrefab}, oPref: {oPrefab}");

                Edge tEdge = edgeData[tempEdge];
                Edge oEdge = edgeData[originalEdge];
                // Logger.DebugConnectionsSync($"|CheckComposition|Edges| tE: {tEdge.m_Start} {tEdge.m_End} | oE: {oEdge.m_Start} {oEdge.m_End}");
                //check if edge was not inverted
                bool wasStart = oEdge.m_Start.Equals(originalNode);
                NetCompositionData nodeComposition = netCompositionData[wasStart ? originalComposition.m_StartNode : originalComposition.m_EndNode];
                bool wasStartEdge = !leftHandTraffic ? (nodeComposition.m_Flags.m_General & CompositionFlags.General.Invert) != 0 : (nodeComposition.m_Flags.m_General & CompositionFlags.General.Invert) == 0; //isStartNode ? tEdge.m_Start : tEdge.m_End;
                // Logger.DebugConnectionsSync($"|CheckComposition|Direction| tNode: {tempNode}, wasStart: {wasStartEdge} || ");
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
                    // Logger.DebugConnectionsSync($"|CheckComposition| Acceptable Edge Composition flags = Left: [{oELeft}]->[{tELeft}] Right: [{oERight}]->[{tERight}]");

                    Entity oldNodeCompositionEntity = wasStart ? originalComposition.m_StartNode : originalComposition.m_EndNode;
                    Entity newNodeCompositionEntity = isStartEdge ? tempComposition.m_StartNode : tempComposition.m_EndNode;
                    bool isDifferentComposition = !oldNodeCompositionEntity.Equals(newNodeCompositionEntity);
                    // Logger.DebugConnectionsSync($"|CheckComposition| {(isDifferentComposition ? "Different" : "The same")} node composition: {oldNodeCompositionEntity} => {newNodeCompositionEntity}");
                    if (isDifferentComposition)
                    {
                        NetCompositionData oNCompositionData = netCompositionData[oldNodeCompositionEntity];
                        NetCompositionData tNCompositionData = netCompositionData[newNodeCompositionEntity];
                        CompositionFlags.Side oNLeft = oNCompositionData.m_Flags.m_Left & importantFlags;
                        CompositionFlags.Side oNRight = oNCompositionData.m_Flags.m_Right & importantFlags;
                        CompositionFlags.Side tNLeft = tNCompositionData.m_Flags.m_Left & importantFlags;
                        CompositionFlags.Side tNRight = tNCompositionData.m_Flags.m_Right & importantFlags;
                        
                        // force composition changed result on roundabout
                        if ((tNCompositionData.m_Flags.m_General & CompositionFlags.General.Roundabout) != 0)
                        {
                            Logger.DebugConnectionsSync($"|CheckComposition| Temp node is Roundabout!, force composition change result");
                            return true;
                        }
                        
                        if (oNLeft != tNLeft || oNRight != tNRight)
                        {
                            Logger.DebugConnectionsSync($"|CheckComposition| Different Node Composition flags = Left: [{oNLeft}]->[{tNLeft}] Right: [{oNRight}]->[{tNRight}]");
                            return new bool2(oNLeft != tNLeft, oNRight != tNRight);
                        }
                        // Logger.DebugConnectionsSync($"|CheckComposition| Acceptable Node Composition flags = Left: [{oELeft}]->[{tELeft}] Right: [{oERight}]->[{tERight}]");
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
            
            private NativeHashMap<int, ValueTuple<int3, float3>> CalculateEdgeLaneCompositionMapping(Entity originalEdge, Composition originalComposition, Entity tempEdge, bool2 isEndMap, Composition tempComposition)
            {
                DynamicBuffer<NetCompositionLane> originalCompositionLanes = netCompositionLaneBuffer[originalComposition.m_Edge];
                NativeHashMap<int, ValueTuple<int3, float3>> compositionMapping = new NativeHashMap<int, ValueTuple<int3, float3>>(originalCompositionLanes.Length, Allocator.Temp);
                NetCompositionData originalCompositionData = netCompositionData[originalComposition.m_Edge];
                if (originalComposition.m_Edge == tempComposition.m_Edge)
                {
                    for (var i = 0; i < originalCompositionLanes.Length; i++)
                    {
                        NetCompositionLane compositionLane = originalCompositionLanes[i];
                        compositionLane.m_Position.x = math.select(0f - compositionLane.m_Position.x, compositionLane.m_Position.x, isEndMap.x);
                        compositionMapping.Add(i, new ValueTuple<int3, float3>(new int3(compositionLane.m_Index, compositionLane.m_Carriageway, compositionLane.m_Group), compositionLane.m_Position));
                    }
                    return compositionMapping;
                }
                
                DynamicBuffer<NetCompositionLane> tempCompositionLanes = netCompositionLaneBuffer[tempComposition.m_Edge];
                NetCompositionData tempCompositionData = netCompositionData[tempComposition.m_Edge];
                float3x2 middleOffsets = new float3x2(originalCompositionData.m_MiddleOffset * math.right(), tempCompositionData.m_MiddleOffset * math.right());
                
                for (var i = 0; i < originalCompositionLanes.Length; i++)
                {
                    NetCompositionLane compositionLane = originalCompositionLanes[i];
                    if (FindNetLaneComposition(compositionLane, isEndMap.y, in tempCompositionLanes, middleOffsets, out NetCompositionLane result))
                    {
                        compositionMapping.Add(i, new ValueTuple<int3, float3>(new int3(result.m_Index, result.m_Carriageway, result.m_Group), result.m_Position));
                    }
                    else
                    {
                        compositionMapping.Add(i, new ValueTuple<int3, float3>(new int3(-1), float3.zero));
                    }
                }
                
                return compositionMapping;
            }

            private bool FindNetLaneComposition(NetCompositionLane source, bool isEnd, in DynamicBuffer<NetCompositionLane> compositions, float3x2 middleOffsets, out NetCompositionLane result)
            {
                for (int i = 0; i < compositions.Length; i++)
                {
                    NetCompositionLane composition = compositions[i];
                    if (Approximately(source.m_Position - middleOffsets.c0, composition.m_Position - middleOffsets.c1) &&
                        (source.m_Flags & (LaneFlags.Road | LaneFlags.Track)) == (composition.m_Flags & (LaneFlags.Road | LaneFlags.Track)))
                    {
                        composition.m_Position.x = math.select(0f - composition.m_Position.x, composition.m_Position.x, isEnd);
                        result = composition;
                        return true;
                    }
                }
                
                result = new NetCompositionLane();
                return false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool Approximately(float3 a, float3 b)
            {
                return math.all(math.abs(a - b) < math.EPSILON);
            }
        }
    }
}
