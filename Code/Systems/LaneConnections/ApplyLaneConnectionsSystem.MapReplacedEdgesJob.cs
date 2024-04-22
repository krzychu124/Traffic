using Game.Net;
using Game.Tools;
using Traffic.CommonData;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.Systems.LaneConnections
{
    public partial class ApplyLaneConnectionsSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct MapReplacedEdgesJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Temp> tempTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Edge> edgeTypeHandle;
            [ReadOnly] public ComponentLookup<Temp> tempData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
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
                    if ((temp.m_Flags & TempFlags.Combine) != 0 && temp.m_Original != Entity.Null)
                    {
                        Temp startNodeTemp = tempData[edge.m_Start];
                        Temp endNodeTemp = tempData[edge.m_End];
                        Edge originalEdge = edgeData[temp.m_Original];
                        Logger.DebugConnections($"|Apply|Combine {entity} T[{temp.m_Original} | {temp.m_Flags}]\n" +
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
                                Entity node = !deletedEdge.m_Start.Equals(deletedNode) ? deletedEdge.m_Start : deletedEdge.m_End;
                                Logger.DebugConnections($"|Apply|Combine|TestConnected({j})| {deletedConnectedEdge.m_Edge} | [{deletedEdge.m_Start}; {deletedEdge.m_End}] ||Nodes deleted: {deletedNode} common: {otherNode}");
                                if (node.Equals(otherNode))
                                {
                                    replacementSourceEdge = deletedConnectedEdge.m_Edge;
                                    break;
                                }
                            }
                            if (replacementSourceEdge != Entity.Null)
                            {
                                Logger.DebugConnections($"|Apply|Combine|FoundDeletedEdge {replacementSourceEdge}");
                                nodeEdgeMap.Add(new NodeEdgeKey(otherNode, replacementSourceEdge), entity);
                                nodeEdgeMap.Add(new NodeEdgeKey(isStartChanged ? edge.m_End : edge.m_Start, entity), replacementSourceEdge);
                            }
                        }
                    } 
                    else if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Replace)) == 0 && temp.m_Original == Entity.Null)
                    {
                        Temp startNodeTemp = tempData[edge.m_Start];
                        Temp endNodeTemp = tempData[edge.m_End];
                        // bool startOriginalIsNode = startNodeTemp.m_Original != Entity.Null && nodeData.HasComponent(startNodeTemp.m_Original);
                        // bool endOriginalIsNode = endNodeTemp.m_Original != Entity.Null && nodeData.HasComponent(endNodeTemp.m_Original);
                        // Logger.DebugConnections($"|Edge|Else| {entity} T[{temp.m_Original} | {temp.m_Flags}]\n" +
                        //     $"Start: {edge.m_Start} | startT: {startNodeTemp.m_Original} [{startNodeTemp.m_Flags}] isNode: {startOriginalIsNode}\n" +
                        //     $"End:   {edge.m_End} | endT: {endNodeTemp.m_Original} [{endNodeTemp.m_Flags}] isNode: {endOriginalIsNode}");

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
                            // Logger.DebugConnections($"|Edge|Else|Start| {entity} T[{temp.m_Original} | {temp.m_Flags}] | OrigEdge: {startNodeTemp.m_Original} start: {startOriginalEdge.m_Start} end: {startOriginalEdge.m_End}");
                        }
                        // else
                        // {
                        // Logger.DebugConnections($"Temp Start original ({startNodeTemp.m_Original}) is Entity.null or not an edge! | {entity} | is node?: {(startNodeTemp.m_Original != Entity.Null ? nodeData.HasComponent(startNodeTemp.m_Original) : Entity.Null)}");
                        // }

                        if (endNodeTemp.m_Original != Entity.Null && edgeData.HasComponent(endNodeTemp.m_Original))
                        {
                            Edge endOriginalEdge = edgeData[endNodeTemp.m_Original];
                            nodeEdgeMap.Add(new NodeEdgeKey(endOriginalEdge.m_Start, endNodeTemp.m_Original), entity);
                            nodeEdgeMap.Add(new NodeEdgeKey(edge.m_Start, entity), endNodeTemp.m_Original);
                            // Logger.DebugConnections($"|Edge|Else|End| {entity} T[{temp.m_Original} | {temp.m_Flags}] | OrigEdge: {endNodeTemp.m_Original} start: {endOriginalEdge.m_Start} end: {endOriginalEdge.m_End}");
                        }
                        // else
                        // {
                        // Logger.DebugConnections($"Temp End original ({endNodeTemp.m_Original}) is Entity.null or not an edge! | {entity} | is node?: {(endNodeTemp.m_Original != Entity.Null ? nodeData.HasComponent(endNodeTemp.m_Original) : Entity.Null)}");
                        // }
                    }
                }
            }
        }
    }
}
