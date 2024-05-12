using Game.Net;
using Game.Tools;
using Traffic.CommonData;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.Systems.LaneConnections.SharedJobs
{
    /// <summary>
    /// Searching and mapping updated edges with their connected node
    /// </summary>
#if WITH_BURST
        [BurstCompile]
#endif
    internal struct MapNodeEdgeEntitiesJob : IJobChunk
    {
        [ReadOnly] public EntityTypeHandle entityTypeHandle;
        [ReadOnly] public ComponentTypeHandle<Temp> tempTypeHandle;
        [ReadOnly] public ComponentTypeHandle<Edge> edgeTypeHandle;
        [ReadOnly] public ComponentLookup<Temp> tempData;
        [ReadOnly] public ComponentLookup<Edge> edgeData;
#if DEBUG_CONNECTIONS
        [ReadOnly] public ComponentLookup<Node> nodeData;
        [ReadOnly] public FixedString32Bytes debugSystemName;
#endif
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
#if DEBUG_CONNECTIONS
                    Logger.DebugConnections($"({debugSystemName})|Edge|Delete| {entity} T[{temp.m_Original} | {temp.m_Flags}] start: {edge.m_Start} end: {edge.m_End}");
#endif
                    continue;
                }
                
                if ((temp.m_Flags & TempFlags.Replace) != 0)
                {
                    Temp startNodeTemp = tempData[edge.m_Start];
                    Temp endNodeTemp = tempData[edge.m_End];
#if DEBUG_CONNECTIONS
                    Logger.DebugConnections($"({debugSystemName})|Edge|Replace| {entity} T[{temp.m_Original} | {temp.m_Flags}]\n" +
                        $"\t\t\t\tStart: {edge.m_Start} | startT: {startNodeTemp.m_Original} [{startNodeTemp.m_Flags}]\n" +
                        $"\t\t\t\tEnd:   {edge.m_End} | endT: {endNodeTemp.m_Original} [{endNodeTemp.m_Flags}]");
#endif
                    if (temp.m_Original != Entity.Null && edgeData.HasComponent(temp.m_Original))
                    {
                        Edge originalEdge = edgeData[temp.m_Original];
                        bool2 commonNodeCheck = new bool2(originalEdge.m_Start.Equals(startNodeTemp.m_Original), originalEdge.m_End.Equals(endNodeTemp.m_Original));
                        bool isStartChanged = !commonNodeCheck.x;
                        Entity commonTempNodeEntity = !isStartChanged ? edge.m_Start : edge.m_End;
                        Temp commonTempNode = !isStartChanged ? startNodeTemp : endNodeTemp;

                        // original node -> orignal edge ->> new edge
                        nodeEdgeMap.Add(new NodeEdgeKey(commonTempNode.m_Original, temp.m_Original), entity);
                        // temp node -> new edge ->> original edge
                        nodeEdgeMap.Add(new NodeEdgeKey(commonTempNodeEntity, entity), temp.m_Original);
                    }
                    continue;
                }

                if (temp.m_Original != Entity.Null)
                {
#if DEBUG_CONNECTIONS
                    Temp startNodeTempTest = tempData[edge.m_Start];
                    Temp endNodeTempTest = tempData[edge.m_End];
                    Logger.DebugConnections($"({debugSystemName})|Edge|HasOriginal| {entity} T[{temp.m_Original} | {temp.m_Flags}]\n" +
                        $"Start: {edge.m_Start} | startT: {startNodeTempTest.m_Original} [{startNodeTempTest.m_Flags}]\n" +
                        $"End:   {edge.m_End} | endT: {endNodeTempTest.m_Original} [{endNodeTempTest.m_Flags}]");
#endif
                    if ((temp.m_Flags & TempFlags.Combine) != 0)
                    {
                        /*
                             * 'entity' - new edge entity identifier, result of combine operation
                             * find common node of temp combine edge, get the opposite of original edge that is going to be combined => it's deleted common node
                             * loop through the other temp node connected edges, find deleted edge with deleted common node
                             * cache mapping for deleted edge and entity as it'll be new edge joining 'entity' Edge start+end temp nodes
                             */
                        Temp startNodeTemp = tempData[edge.m_Start];
                        Temp endNodeTemp = tempData[edge.m_End];
                        Edge originalEdge = edgeData[temp.m_Original];
#if DEBUG_CONNECTIONS
                        Logger.DebugConnections($"({debugSystemName})|Edge|HasOriginal|Combine|TEST_startNode isNode: {nodeData.HasComponent(edge.m_Start)} isTemp: {tempData.HasComponent(edge.m_Start)}");
                        Logger.DebugConnections($"({debugSystemName})|Edge|HasOriginal|Combine|TEST_endNode isNode: {nodeData.HasComponent(edge.m_End)} isTemp: {tempData.HasComponent(edge.m_End)}");
                        Logger.DebugConnections($"({debugSystemName})|Edge|HasOriginal|Combine {entity} T[{temp.m_Original} | {temp.m_Flags}]\n" +
                            $"\t\tO_Start: {originalEdge.m_Start} |T_Start: {edge.m_Start} | T[{startNodeTemp.m_Original} | {startNodeTemp.m_Flags}]\n" +
                            $"\t\tO_End:   {originalEdge.m_End} |T_End:   {edge.m_End} | T[{endNodeTemp.m_Original} | {endNodeTemp.m_Flags}]");
#endif

                        bool2 commonNodeCheck = new bool2(originalEdge.m_Start.Equals(startNodeTemp.m_Original), originalEdge.m_End.Equals(endNodeTemp.m_Original));
                        bool isStartChanged = !commonNodeCheck.x;
                        Entity otherTempNodeEntity = isStartChanged ? edge.m_Start : edge.m_End;
                        Entity commonTempNodeEntity = !isStartChanged ? edge.m_Start : edge.m_End;
                        Temp otherTempNode = isStartChanged ? startNodeTemp : endNodeTemp;
                        Temp commonTempNode = !isStartChanged ? startNodeTemp : endNodeTemp;
                        Entity originalDeleted = originalEdge.m_Start.Equals(commonTempNode.m_Original) ? originalEdge.m_End : originalEdge.m_Start;
                        if (connectedEdgeBuffer.HasBuffer(otherTempNodeEntity))
                        {
                            DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgeBuffer[otherTempNodeEntity]; // temp edges of non-common node from combined edge
#if DEBUG_CONNECTIONS
                            Logger.DebugConnections($"({debugSystemName})|Edge|HasOriginal|Combine|ConnectedEdges[{connectedEdges.Length}] of {otherTempNodeEntity}");
#endif
                            for (var j = 0; j < connectedEdges.Length; j++)
                            {
                                ConnectedEdge deletedConnectedEdge = connectedEdges[j];
                                Temp tempEdge = tempData[deletedConnectedEdge.m_Edge];
                                Edge tempEdgeData = edgeData[deletedConnectedEdge.m_Edge];
                                bool isStartDeletedEdgeNodeChanged = tempEdgeData.m_Start.Equals(otherTempNodeEntity);
                                Entity otherTempEdgeNode =  isStartDeletedEdgeNodeChanged ? tempEdgeData.m_End : tempEdgeData.m_Start;
                                Temp otherTempDeletedNode = tempData[otherTempEdgeNode];
#if DEBUG_CONNECTIONS
                                Logger.DebugConnections($"({debugSystemName})|Edge|HasOriginal|Combine|TestConnected({j})| [{deletedConnectedEdge.m_Edge} O: {tempEdge.m_Original}] | s-e:[{tempEdgeData.m_Start}; {tempEdgeData.m_End}] || Nodes deleted: {originalDeleted}, tempDel: [{otherTempEdgeNode} O: {otherTempDeletedNode.m_Original}], common: [{commonTempNodeEntity} O: {commonTempNode.m_Original}]");
#endif
                                if (originalDeleted.Equals(otherTempDeletedNode.m_Original))
                                {
#if DEBUG_CONNECTIONS
                                    Logger.DebugConnections($"({debugSystemName})|Edge|HasOriginal|Combine|FoundDeletedEdge({j})| [{deletedConnectedEdge.m_Edge} O: {tempEdge.m_Original}] | s-e:[{tempEdgeData.m_Start}; {tempEdgeData.m_End}] || Nodes deleted: {originalDeleted}, tempDel: [{otherTempEdgeNode} O: {otherTempDeletedNode.m_Original}], common: [{commonTempNodeEntity} O: {commonTempNode.m_Original}]");
#endif
                                    // original node -> original edge ->> new edge
                                    nodeEdgeMap.Add(new NodeEdgeKey(otherTempNode.m_Original, tempEdge.m_Original), entity);
                                    // temp node -> new edge ->> original edge
                                    nodeEdgeMap.Add(new NodeEdgeKey(otherTempNodeEntity, entity), tempEdge.m_Original);
                                    // temp node -> temp del edge ->> original edge
                                    nodeEdgeMap.Add(new NodeEdgeKey(otherTempNodeEntity, deletedConnectedEdge.m_Edge), tempEdge.m_Original);
                                    break;
                                }
                            }
                                
                            // original node -> orignal edge ->> new edge
                            nodeEdgeMap.Add(new NodeEdgeKey(commonTempNode.m_Original, temp.m_Original), entity);
                            // temp node -> new edge ->> original edge
                            nodeEdgeMap.Add(new NodeEdgeKey(commonTempNodeEntity, entity), temp.m_Original);
                        }
                    }
                }
                else
                {
                    Temp startNodeTemp = tempData[edge.m_Start];
                    Temp endNodeTemp = tempData[edge.m_End];
#if DEBUG_CONNECTIONS
                    bool startOriginalIsNode = startNodeTemp.m_Original != Entity.Null && nodeData.HasComponent(startNodeTemp.m_Original);
                    bool endOriginalIsNode = endNodeTemp.m_Original != Entity.Null && nodeData.HasComponent(endNodeTemp.m_Original);
                    Logger.DebugConnections($"({debugSystemName})|Edge|Else| {entity} T[{temp.m_Original} | {temp.m_Flags}]\n" +
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
#if DEBUG_CONNECTIONS
                        Logger.DebugConnections($"({debugSystemName})|Edge|Else|Start| {entity} T[{temp.m_Original} | {temp.m_Flags}] | OrigEdge: {startNodeTemp.m_Original} start: {startOriginalEdge.m_Start} end: {startOriginalEdge.m_End}");
                    }
                    else
                    {
                        Logger.DebugConnections($"({debugSystemName}) Temp Start original ({startNodeTemp.m_Original}) is Entity.null or not an edge! | {entity} | is node?: {(startNodeTemp.m_Original != Entity.Null ? nodeData.HasComponent(startNodeTemp.m_Original) : Entity.Null)}");
#endif
                    }
                    if (endNodeTemp.m_Original != Entity.Null && edgeData.HasComponent(endNodeTemp.m_Original))
                    {
                        Edge endOriginalEdge = edgeData[endNodeTemp.m_Original];
                        nodeEdgeMap.Add(new NodeEdgeKey(endOriginalEdge.m_Start, endNodeTemp.m_Original), entity);
                        nodeEdgeMap.Add(new NodeEdgeKey(edge.m_Start, entity), endNodeTemp.m_Original);
#if DEBUG_CONNECTIONS
                        Logger.DebugConnections($"({debugSystemName})|Edge|Else|End| {entity} T[{temp.m_Original} | {temp.m_Flags}] | OrigEdge: {endNodeTemp.m_Original} start: {endOriginalEdge.m_Start} end: {endOriginalEdge.m_End}");
                    }
                    else
                    {
                        Logger.DebugConnections($"({debugSystemName}) Temp End original ({endNodeTemp.m_Original}) is Entity.null or not an edge! | {entity} | is node?: {(endNodeTemp.m_Original != Entity.Null ? nodeData.HasComponent(endNodeTemp.m_Original) : Entity.Null)}");
#endif
                    }
                }
            }

        }
    }
}
