using Colossal.Mathematics;
using Game.Net;
using Game.Pathfind;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using LaneConnection = Traffic.Components.LaneConnections.LaneConnection;

namespace Traffic.Systems.LaneConnections
{
    public partial class GenerateConnectorsSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct GenerateConnectionLanesJob : IJobChunk
        {            
            [ReadOnly] public ComponentTypeHandle<EditIntersection> editIntersectionType;
            [ReadOnly] public ComponentLookup<Node> nodeData;
            [ReadOnly] public ComponentLookup<Lane> laneData;
            [ReadOnly] public ComponentLookup<CarLane> carLaneData;
            [ReadOnly] public ComponentLookup<MasterLane> masterLaneData;
            [ReadOnly] public ComponentLookup<Curve> curveData;
            [ReadOnly] public NativeParallelHashMap<NodeEdgeLaneKey, Entity> connectorsList;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgesBuffer;
            [ReadOnly] public BufferLookup<SubLane> subLanesBuffer;
            public EntityCommandBuffer commandBuffer;
 
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<EditIntersection> editIntersections = chunk.GetNativeArray(ref editIntersectionType);
                Logger.DebugConnections($"Connectors: {connectorsList.Count()}");
                NativeParallelMultiHashMap<Entity, Connection> connections = new (8, Allocator.Temp);
                
                for (int i = 0; i < editIntersections.Length; i++)
                {
                    EditIntersection editIntersection = editIntersections[i];
                    Entity node = editIntersection.node;
                    if (nodeData.HasComponent(node) && subLanesBuffer.HasBuffer(node))
                    {
                        DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgesBuffer[node];
                        DynamicBuffer<SubLane> subLanes = subLanesBuffer[node];
                        
                        foreach (SubLane subLane in subLanes)
                        {
                            Entity subLaneEntity = subLane.m_SubLane;
                            if ((subLane.m_PathMethods & (PathMethod.Road | PathMethod.Track)) == 0 || masterLaneData.HasComponent(subLaneEntity))
                            {
                                continue;
                            }
                            Lane lane = laneData[subLaneEntity];
                            Entity sourceEdge = FindEdge(connectedEdges, lane.m_StartNode);
                            Entity targetEdge = sourceEdge;
                            if (!lane.m_StartNode.OwnerEquals(lane.m_EndNode))
                            {
                                targetEdge = FindEdge(connectedEdges, lane.m_EndNode);
                            }
                            if (sourceEdge == Entity.Null || targetEdge == Entity.Null)
                            {
                                continue;
                            }
                            bool isUnsafe = false;
                            bool isForbidden = false;
                            if (carLaneData.HasComponent(subLaneEntity))
                            {
                                CarLane carLane = carLaneData[subLaneEntity];
                                isUnsafe = (carLane.m_Flags & CarLaneFlags.Unsafe) != 0;
                                isForbidden = (carLane.m_Flags & CarLaneFlags.Forbidden) != 0;
                            }
                            Bezier4x3 bezier = curveData[subLaneEntity].m_Bezier;
                            Logger.DebugConnections($"Adding connection (subLane: {subLaneEntity}): idx[{lane.m_StartNode.GetLaneIndex() & 0xff}->{lane.m_EndNode.GetLaneIndex() & 0xff}] edge:[{sourceEdge}=>{targetEdge}] | unsafe?: {isUnsafe}, methods: {subLane.m_PathMethods}");
                            Connection connection = new Connection(lane, bezier, subLane.m_PathMethods, sourceEdge, targetEdge, isUnsafe, isForbidden);
                            connections.Add(sourceEdge, connection);
                        }

                        foreach (ConnectedEdge connectedEdge in connectedEdges)
                        {
                            Entity edge = connectedEdge.m_Edge;
                            if (connections.ContainsKey(edge))
                            {
                                foreach (Connection connection in connections.GetValuesForKey(edge))
                                {
                                    // TODO REDESIGN, GET RID OF LANE_CONNECTION buffer
                                    int connectorIndex = connection.sourceNode.GetLaneIndex() & 0xff;
                                    if (connectorsList.TryGetValue(new NodeEdgeLaneKey(node.Index, edge.Index, connectorIndex), out Entity connector))
                                    {
                                        Entity e = commandBuffer.CreateEntity();
                                        DynamicBuffer<Connection> connectionsBuffer = commandBuffer.AddBuffer<Connection>(e);
                                        Logger.DebugConnections($"Adding connection to buffer ({e}): idx[{connection.sourceNode.GetLaneIndex() & 0xff}->{connection.targetNode.GetLaneIndex() & 0xff}] edge[{connection.sourceEdge}=>{connection.targetEdge}]");
                                        connectionsBuffer.Add(connection);
                                        Logger.DebugConnections($"Detected connection n[{node}] e[{edge}] idx[{connectorIndex}] | connector: {connector} | e: {e}");
                                        commandBuffer.AppendToBuffer(connector, new LaneConnection() { connection = e });
                                    }
                                }
                            }
                        }
                        connections.Clear();
                    }
                }
                connections.Dispose();
            }

            private Entity FindEdge(DynamicBuffer<ConnectedEdge> edges, PathNode node) {
                foreach (ConnectedEdge connectedEdge in edges)
                {
                    if (node.OwnerEquals(new PathNode(connectedEdge.m_Edge, 0)))
                    {
                        return connectedEdge.m_Edge;
                    }
                }
                return Entity.Null;
            }
        }
    }
}
