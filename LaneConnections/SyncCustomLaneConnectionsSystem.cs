using System;
using System.Linq;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Traffic.Components;
using Traffic.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.LaneConnections
{
    /// <summary>
    /// Sync lane connections on existing nodes (ignore custom connections created on the same frame)
    /// </summary>
    public partial class SyncCustomLaneConnectionsSystem : GameSystemBase
    {
        private EntityQuery _query;
        
        protected override void OnCreate() {
            base.OnCreate();
            _query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Updated>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.Exclude<ModifiedLaneConnections>(),  }
            });
            RequireForUpdate(_query);
        }

        protected override void OnUpdate() {
            Logger.Debug("SyncCustomLaneConnectionsSystem Update!");
            NativeArray<Entity> updatedNodes = _query.ToEntityArray(Allocator.TempJob);
            EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
            SyncConnectionsJob job = new SyncConnectionsJob()
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                connectedEdgeTypeHandle = SystemAPI.GetBufferTypeHandle<ConnectedEdge>(true),
                nodeData = SystemAPI.GetComponentLookup<Node>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                tempData = SystemAPI.GetComponentLookup<Temp>(true),
                hiddenData = SystemAPI.GetComponentLookup<Hidden>(true),
                dataOwnerData = SystemAPI.GetComponentLookup<DataOwner>(true),
                compositionData = SystemAPI.GetComponentLookup<Composition>(true),
                netCompositionData = SystemAPI.GetComponentLookup<NetCompositionData>(true),
                prefabData = SystemAPI.GetComponentLookup<PrefabRef>(true),
                modifiedConnectionsBuffer = SystemAPI.GetBufferLookup<ModifiedLaneConnections>(true),
                connectedEdgeBuffer = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                generatedConnectionBuffer = SystemAPI.GetBufferLookup<GeneratedConnection>(true),
                tempNodes = updatedNodes.AsReadOnly(),
                commandBuffer = commandBuffer.AsParallelWriter(),
            };
            JobHandle jobHandle = job.Schedule(updatedNodes.Length, 1, Dependency);
            jobHandle.Complete();
            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
            updatedNodes.Dispose(jobHandle);
            Dependency = jobHandle;
            Logger.Debug("SyncCustomLaneConnectionsSystem Update finished!");
        }

        private struct SyncConnectionsJob : IJobParallelFor
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Temp> tempTypeHandle;
            [ReadOnly] public BufferTypeHandle<ConnectedEdge> connectedEdgeTypeHandle;
            [ReadOnly] public ComponentLookup<Node> nodeData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Temp> tempData;
            [ReadOnly] public ComponentLookup<Hidden> hiddenData;
            [ReadOnly] public ComponentLookup<DataOwner> dataOwnerData;
            [ReadOnly] public ComponentLookup<Composition> compositionData;
            [ReadOnly] public ComponentLookup<NetCompositionData> netCompositionData;
            [ReadOnly] public ComponentLookup<PrefabRef> prefabData;
            [ReadOnly] public BufferLookup<ModifiedLaneConnections> modifiedConnectionsBuffer;
            [ReadOnly] public BufferLookup<GeneratedConnection> generatedConnectionBuffer;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgeBuffer;
            public NativeArray<Entity>.ReadOnly tempNodes;
            public EntityCommandBuffer.ParallelWriter commandBuffer;

            public void Execute(int index) {
                Entity entity = tempNodes[index];
                Temp temp = tempData[entity];

                //todo test node composition entity (modifiedConnections edge -> isStart/End node? -> (edge) start/End composition) ((maybe check lane index if still the same))
                Logger.Debug($"({index}) Testing {entity} Temp node: {temp.m_Original} flags: {temp.m_Flags}");

                if (temp.m_Original == Entity.Null)
                {
                    Logger.Debug($"\tSkip, temp {entity} is a new node");
                    return;
                }

                if (nodeData.HasComponent(temp.m_Original) && modifiedConnectionsBuffer.HasBuffer(temp.m_Original))
                {

                    if ((temp.m_Flags & TempFlags.Delete) == 0)
                    {
                        EdgeIterator edgeIterator = new EdgeIterator(Entity.Null, entity, connectedEdgeBuffer, edgeData, tempData, hiddenData, false);
                        NativeHashMap<Entity, EdgeInfo> edgeMap = new NativeHashMap<Entity, EdgeInfo>(4, Allocator.Temp);
                        Logger.Debug($"======= Iterating edges of {entity} =======");
                        while (edgeIterator.GetNext(out EdgeIteratorValue edgeValue))
                        {
                            Logger.Debug($"({index}) Iterating edges of ({entity}): {edgeValue.m_Edge} isTemp: {tempData.HasComponent(edgeValue.m_Edge)} isEnd: {edgeValue.m_End} isMiddle: {edgeValue.m_Middle}");

                            Edge edge = edgeData[edgeValue.m_Edge];
                            if (tempData.HasComponent(edgeValue.m_Edge))
                            {
                                Temp tempEdge = tempData[edgeValue.m_Edge];
                                Temp startTemp = tempData[edge.m_Start];
                                Temp endTemp = tempData[edge.m_End];
                                bool compositionChanged = false;
                                string compositionStr = "\nComposition";
                                if (compositionData.HasComponent(edgeValue.m_Edge) && compositionData.HasComponent(tempEdge.m_Original))
                                {
                                    Composition composition = compositionData[edgeValue.m_Edge];
                                    NetCompositionData data1 = netCompositionData[composition.m_Edge];
                                    NetCompositionData data2 = netCompositionData[composition.m_StartNode];
                                    NetCompositionData data3 = netCompositionData[composition.m_EndNode];
                                    compositionStr += $"\n\t(E: {edgeValue.m_Edge} | cE: {composition.m_Edge} cStart: {composition.m_StartNode} cEnd: {composition.m_EndNode} |";
                                    compositionStr += $"\n\t\t(NetComposition(Edge):  |G {data1.m_Flags.m_General} |L {data1.m_Flags.m_Left} |R {data1.m_Flags.m_Right} |";
                                    compositionStr += $"\n\t\t(NetComposition(Start): |G {data2.m_Flags.m_General} |L {data2.m_Flags.m_Left} |R {data2.m_Flags.m_Right} |";
                                    compositionStr += $"\n\t\t(NetComposition(End):   |G {data3.m_Flags.m_General} |L {data3.m_Flags.m_Left} |R {data3.m_Flags.m_Right} |";
                                    Composition originalComposition = compositionData[tempEdge.m_Original];
                                    data1 = netCompositionData[originalComposition.m_Edge];
                                    data2 = netCompositionData[originalComposition.m_StartNode];
                                    data3 = netCompositionData[originalComposition.m_EndNode];
                                    compositionStr += $"\n\t(oE: {tempEdge.m_Original} | cE: {originalComposition.m_Edge} cStart: {originalComposition.m_StartNode} cEnd: {originalComposition.m_EndNode} |";
                                    compositionStr += $"\n\t\t(NetComposition(Edge):  |G {data1.m_Flags.m_General} |L {data1.m_Flags.m_Left} |R {data1.m_Flags.m_Right} |";
                                    compositionStr += $"\n\t\t(NetComposition(Start): |G {data2.m_Flags.m_General} |L {data2.m_Flags.m_Left} |R {data2.m_Flags.m_Right} |";
                                    compositionStr += $"\n\t\t(NetComposition(End):   |G {data3.m_Flags.m_General} |L {data3.m_Flags.m_Left} |R {data3.m_Flags.m_Right} |";
                                    compositionChanged = CheckComposition(entity, temp.m_Original, tempEdge.m_Original, originalComposition, edgeValue.m_Edge, composition, !edgeValue.m_End, (tempEdge.m_Flags & TempFlags.Modify) != 0);
                                    // compositionStr += $"(E: {composition.m_Edge} sN: {composition.m_StartNode} eN: {composition.m_EndNode} [s ({netCompositionData[composition.m_StartNode].m_Flags.m_General}) e: ({netCompositionData[composition.m_EndNode].m_Flags.m_General})] " +
                                    // $"|old| sN: {originalComposition.m_StartNode} eN: {originalComposition.m_EndNode} [s ({netCompositionData[originalComposition.m_StartNode].m_Flags.m_General}) e: ({netCompositionData[originalComposition.m_EndNode].m_Flags.m_General})])";
                                }
                                Logger.Debug($"Edge: {edgeValue.m_Edge}: \n\t\t\t\t\t\t\t\t\tStart({edge.m_Start}) End({edge.m_End}), nodeFlags ({startTemp.m_Original}): [{startTemp.m_Flags}], ({endTemp.m_Original}): [{endTemp.m_Flags}] | {compositionStr}");
                                edgeMap.Add(tempEdge.m_Original, new EdgeInfo() { edge = edgeValue.m_Edge, wasTemp = true, compositionChanged = compositionChanged });
                            }
                            else
                            {
                                edgeMap.Add(edgeValue.m_Edge, new EdgeInfo() { edge = edgeValue.m_Edge, wasTemp = false, compositionChanged = false });
                                Logger.Debug($"Edge: {edgeValue.m_Edge}: \n\t\t\t\t\t\t\t\t\tStart({edge.m_Start}) End({edge.m_End})");
                            }
                        }
                        Logger.Debug($"======= Iterating edges of {entity} finished =======");
                        NativeKeyValueArrays<Entity, EdgeInfo> nativeKeyValueArrays = edgeMap.GetKeyValueArrays(Allocator.Temp);
                        string keyValue = string.Empty;
                        for (var i = 0; i < nativeKeyValueArrays.Length; i++)
                        {
                            keyValue += $"\n\tEdge: {nativeKeyValueArrays.Keys[i]} -> {nativeKeyValueArrays.Values[i].edge} wasTemp: {nativeKeyValueArrays.Values[i].wasTemp} compositionChanged: {nativeKeyValueArrays.Values[i].compositionChanged}";
                        }
                        Logger.Debug($"EdgeMap: {keyValue}");

                        NativeList<ModifiedLaneConnections> newModifiedConnections = new NativeList<ModifiedLaneConnections>(Allocator.Temp);
                        DynamicBuffer<ModifiedLaneConnections> laneConnections = modifiedConnectionsBuffer[temp.m_Original];
                        for (var i = 0; i < laneConnections.Length; i++)
                        {
                            ModifiedLaneConnections connection = laneConnections[i];
                            Logger.Debug($"Testing connection edge: {connection.edgeEntity}, index: {connection.laneIndex} mc: {connection.modifiedConnections}");
                            Entity genEntity = commandBuffer.CreateEntity(index);
                            Temp newModifiedConnectionTemp = new Temp(connection.modifiedConnections, 0);
                            commandBuffer.AddComponent<DataOwner>(index, genEntity, new DataOwner(entity));
                            commandBuffer.AddComponent<PrefabRef>(index, genEntity, new PrefabRef(LaneConnectorToolSystem.FakePrefabRef));
                            if (edgeMap.TryGetValue(connection.edgeEntity, out EdgeInfo newEdgeInfo) && !newEdgeInfo.compositionChanged)
                            {
                                Logger.Debug($"Edge ({connection.edgeEntity}): {newEdgeInfo.edge} {newEdgeInfo.wasTemp}");
                                DynamicBuffer<GeneratedConnection> newConnections = commandBuffer.AddBuffer<GeneratedConnection>(index, genEntity);
                                DynamicBuffer<GeneratedConnection> generatedConnections = generatedConnectionBuffer[connection.modifiedConnections];
                                for (var k = 0; k < generatedConnections.Length; k++)
                                {
                                    GeneratedConnection generatedConnection = generatedConnections[k];
                                    if (edgeMap.TryGetValue(generatedConnection.targetEntity, out EdgeInfo genEdgeInfo) && !genEdgeInfo.compositionChanged)
                                    {
                                        Logger.Debug($"Target ({generatedConnection.targetEntity}): {genEdgeInfo.edge} {genEdgeInfo.wasTemp}");
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
                                }
                                newModifiedConnections.Add(new ModifiedLaneConnections()
                                {
                                    edgeEntity = newEdgeInfo.edge,
                                    laneIndex = connection.laneIndex,
                                    modifiedConnections = genEntity,
                                });
                                newModifiedConnectionTemp.m_Flags |= (newConnections.Length == 0 && generatedConnections.Length > 0) ? TempFlags.Delete : TempFlags.Modify;
                                Logger.Debug($"Generated connections for {entity}({temp.m_Original})[{connection.edgeEntity}] => ({newEdgeInfo.edge}): {newConnections.Length} Flags: {newModifiedConnectionTemp.m_Flags}");
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
                                Logger.Debug($"Edge not found! {entity}({temp.m_Original})[{connection.edgeEntity}]");
                            }
                            commandBuffer.AddComponent<Temp>(index, genEntity, newModifiedConnectionTemp);
                        }
                        Logger.Debug($"Regenerated connections for node: {entity}({temp.m_Original}) ({newModifiedConnections.Length})");
                        if (newModifiedConnections.Length > 0)
                        {
                            DynamicBuffer<ModifiedLaneConnections> modifiedConnection = commandBuffer.AddBuffer<ModifiedLaneConnections>(index, entity);
                            modifiedConnection.CopyFrom(newModifiedConnections.AsArray());
                        }


                        /*
                     * OLD IMPL.
                     */
                        bool hasTempConnectedEdges = connectedEdgeBuffer.HasBuffer(entity);
                        BufferLookup<GeneratedConnection> buffer = generatedConnectionBuffer;
                        Logger.Debug($"Modified Connections at node: {temp.m_Original}:\n\t" +
                            string.Join("\n\t",
                                laneConnections.AsNativeArray().Select(l => $"e: {l.edgeEntity} | l: {l.laneIndex} |: c: {l.modifiedConnections} \n\t\t" +
                                    string.Join("\n\t\t", buffer.TryGetBuffer(l.modifiedConnections, out var data) ? data.ToNativeArray(Allocator.Temp).Select(d => $"[s: {d.sourceEntity} t: {d.targetEntity} idx: {d.laneIndexMap}]") : Array.Empty<string>())))
                        );
                        if (connectedEdgeBuffer.HasBuffer(temp.m_Original))
                        {
                            DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgeBuffer[temp.m_Original];
                            Logger.Debug($"Has ConnectedEdges:\n\t{string.Join(",\n\t", connectedEdges.AsNativeArray().Select(c => c.m_Edge))}");
                            if (hasTempConnectedEdges)
                            {
                                DynamicBuffer<ConnectedEdge> tempConnectedEdges = connectedEdgeBuffer[entity];
                                Logger.Debug($"Temp ConnectedEdges:\n\t{string.Join(",\n\t", tempConnectedEdges.AsNativeArray().Select(c => c.m_Edge))}");
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
                                        Logger.Debug($"Temp ConnectedEdge {edge.m_Edge} -> original: {t.m_Original} flags: {t.m_Flags}");
                                    }
                                }
                            }
                            else
                            {
                                Logger.Debug("\tNo Temp ConnectedEdges!");
                            }
                        }
                        else
                        {
                            Logger.Debug($"\tNo connected Edges to {temp.m_Original}");
                        }
                    }
                    else
                    {
                        Logger.Debug($"Delete node with modified connections: {temp.m_Original}, tempFlags: {temp.m_Flags}");
                    }
                }
                else
                {
                    Logger.Debug($"No modified connections at node: {temp.m_Original}, tempFlags: {temp.m_Flags}");
                }
            }

            private bool CheckComposition(Entity tempNode, Entity originalNode, Entity originalEdge, Composition originalComposition, Entity tempEdge, Composition tempComposition, bool isStartEdge, bool isModifyFlag) {
                Entity tPrefab = prefabData[tempEdge];
                Entity oPrefab = prefabData[originalEdge];
                Logger.Debug($"|CheckComposition| tN: {tempNode}, oN: {originalNode} | tE: {tempEdge}, oE: {originalEdge} | iS: {isStartEdge}, modifying: {isModifyFlag} | tPref: {tPrefab}, oPref: {oPrefab}");

                Edge tEdge = edgeData[tempEdge];
                Edge oEdge = edgeData[originalEdge];
                Logger.Debug($"|CheckComposition|Edges| tE: {tEdge.m_Start} {tEdge.m_End} | oE: {oEdge.m_Start} {oEdge.m_End}");
                //check if edge was not inverted
                bool wasStart = oEdge.m_Start.Equals(originalNode);
                NetCompositionData nodeComposition = netCompositionData[wasStart ? originalComposition.m_StartNode : originalComposition.m_EndNode];
                bool wasStartEdge = (nodeComposition.m_Flags.m_General & CompositionFlags.General.Invert) != 0; //isStartNode ? tEdge.m_Start : tEdge.m_End;
                Logger.Debug($"|CheckComposition|Direction| tNode: {tempNode}, wasStart: {wasStartEdge} || ");
                if (isStartEdge != wasStartEdge)
                {
                    Logger.Debug($"|CheckComposition| Different edge direction! {wasStartEdge} => {isStartEdge}");
                    return true;
                }

                if (oPrefab == tPrefab)
                {
                    CompositionFlags.Side importantFlags = CompositionFlags.Side.ForbidLeftTurn | CompositionFlags.Side.ForbidStraight | CompositionFlags.Side.ForbidRightTurn | CompositionFlags.Side.PrimaryTrack | CompositionFlags.Side.SecondaryTrack | CompositionFlags.Side.TertiaryTrack | CompositionFlags.Side.QuaternaryTrack;
                    //same prefab, same edge direction
                    // check road composition
                    NetCompositionData origEdgeNetCompositionData = netCompositionData[originalComposition.m_Edge];
                    NetCompositionData newEdgeNetCompositionData = netCompositionData[tempComposition.m_Edge];
                    CompositionFlags.Side oELeft = origEdgeNetCompositionData.m_Flags.m_Left & importantFlags;
                    CompositionFlags.Side oERight = origEdgeNetCompositionData.m_Flags.m_Right & importantFlags;
                    CompositionFlags.Side tELeft = newEdgeNetCompositionData.m_Flags.m_Left & importantFlags;
                    CompositionFlags.Side tERight = newEdgeNetCompositionData.m_Flags.m_Right & importantFlags;

                    if (oELeft != tELeft || oERight != tERight)
                    {
                        Logger.Debug($"|CheckComposition| Different Edge Composition flags = Left: [{oELeft}]->[{tELeft}] Right: [{oERight}]->[{tERight}]");
                        return true;
                    }
                    Logger.Debug($"|CheckComposition| Acceptable Edge Composition flags = Left: [{oELeft}]->[{tELeft}] Right: [{oERight}]->[{tERight}]");

                    Entity oldNodeCompositionEntity = wasStart ? originalComposition.m_StartNode : originalComposition.m_EndNode;
                    Entity newNodeCompositionEntity = isStartEdge ? tempComposition.m_StartNode : tempComposition.m_EndNode;
                    bool isDifferentComposition = !oldNodeCompositionEntity.Equals(newNodeCompositionEntity);
                    Logger.Debug($"|CheckComposition| {(isDifferentComposition ? "Different" : "The same")} node composition: {oldNodeCompositionEntity} => {newNodeCompositionEntity}");
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
                            Logger.Debug($"|CheckComposition| Different Node Composition flags = Left: [{oNLeft}]->[{tNLeft}] Right: [{oNRight}]->[{tNRight}]");
                            return true;
                        }
                        Logger.Debug($"|CheckComposition| Acceptable Node Composition flags = Left: [{oELeft}]->[{tELeft}] Right: [{oERight}]->[{tERight}]");
                    }
                    return false;
                }
                else
                {
                    //different road prefab
                    Logger.Debug($"|CheckComposition| Different prefabs {oPrefab} => {tPrefab}");
                    return true;
                }
            }
        }

        internal struct EdgeInfo
        {
            public Entity edge;
            public bool wasTemp;
            public bool compositionChanged;
        }
    }
}
