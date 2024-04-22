using Game.Common;
using Game.Net;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.Tools
{
    public partial class LaneConnectorToolSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct RemoveLaneConnectionsJob: IJobFor
        {
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Deleted> deletedData;
            [ReadOnly] public BufferLookup<ModifiedLaneConnections> modifiedLaneConnectionsData;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgeData;
            [ReadOnly] public NativeArray<Entity> entities;
            public EntityCommandBuffer.ParallelWriter commandBuffer;

            public void Execute(int index)
            {
                Entity entity = entities[index];
                if (modifiedLaneConnectionsData.HasBuffer(entity))
                {
                    DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections = modifiedLaneConnectionsData[entity];
                    for (int i = 0; i < modifiedLaneConnections.Length; i++)
                    {
                        Entity modified = modifiedLaneConnections[i].modifiedConnections;
                        if (modified != Entity.Null)
                        {
                            commandBuffer.AddComponent<Deleted>(index, modified);
                        }
                    }
                    commandBuffer.RemoveComponent<ModifiedLaneConnections>(index, entity);
                    commandBuffer.RemoveComponent<ModifiedConnections>(index, entity);
                    
                    DynamicBuffer<ConnectedEdge> edges = connectedEdgeData[entity];
                    if (edges.Length > 0)
                    {
                        //update connected nodes of every edge
                        for (var j = 0; j < edges.Length; j++)
                        {
                            Entity edgeEntity = edges[j].m_Edge;
                            if (!deletedData.HasComponent(edgeEntity))
                            {
                                Edge e = edgeData[edgeEntity];
                                commandBuffer.AddComponent<Updated>(index, edgeEntity);
                                Entity otherNode = e.m_Start == entity ? e.m_End : e.m_Start;
                                commandBuffer.AddComponent<Updated>(index, otherNode);
                            }
                        }
                    }
                }
                
                commandBuffer.AddComponent<Updated>(index, entity);
            }
        }
    }
}
