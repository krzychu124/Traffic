using Colossal.Collections;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Traffic.CommonData;
using Traffic.Components;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Traffic.Tools
{
    public partial class LaneConnectorToolSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct SelectIntersectionNodeJob : IJob
        {
            [ReadOnly] public ComponentLookup<Elevation> elevationData;
            [ReadOnly] public ComponentLookup<Upgraded> upgradedData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<ModifiedConnections> modifiedConnectionsData;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgeBuffer;
            [ReadOnly] public ComponentTypeSet modifiedConnectionsTypeSet;
            [ReadOnly] public Entity node;
            public NativeValue<float2> nodeElevation;
            public EntityCommandBuffer commandBuffer;
            
            public void Execute()
            {
                Entity selectedNode = commandBuffer.CreateEntity();
                commandBuffer.AddComponent(selectedNode, new EditIntersection() { node = node });
                commandBuffer.AddComponent<EditLaneConnections>(selectedNode);
                commandBuffer.AddComponent<Updated>(selectedNode);
                nodeElevation.value = 0f;
                if (elevationData.HasComponent(node))
                {
                    nodeElevation.value = elevationData[node].m_Elevation;
                }
                if (!modifiedConnectionsData.HasComponent(node))
                {
                    commandBuffer.AddComponent(node, in modifiedConnectionsTypeSet);
                }

                if (connectedEdgeBuffer.HasBuffer(node))
                {
                    DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgeBuffer[node];
                    bool anyUpdated = false;
                    for (int i = 0; i < connectedEdges.Length; i++)
                    {
                        ConnectedEdge connectedEdge = connectedEdges[i];
                        if (!upgradedData.HasComponent(connectedEdge.m_Edge))
                        {
                            continue;
                        }
                        Edge edge = edgeData[connectedEdge.m_Edge];
                        Upgraded upgraded = upgradedData[connectedEdge.m_Edge];
                        Entity otherNode = Entity.Null;
                        if (edge.m_Start == node)
                        {
                            upgraded.m_Flags.m_Left &= ~(CompositionFlags.Side.ForbidStraight | CompositionFlags.Side.ForbidLeftTurn | CompositionFlags.Side.ForbidRightTurn);
                            otherNode = edge.m_End;
                        }
                        else if (edge.m_End == node)
                        {
                            upgraded.m_Flags.m_Right &= ~(CompositionFlags.Side.ForbidStraight | CompositionFlags.Side.ForbidLeftTurn | CompositionFlags.Side.ForbidRightTurn);
                            otherNode = edge.m_Start;
                        }
                        
                        if (upgraded.m_Flags == default(CompositionFlags))
                        {
                            commandBuffer.RemoveComponent<Upgraded>(connectedEdge.m_Edge);
                        }
                        else
                        {
                            commandBuffer.SetComponent(connectedEdge.m_Edge, upgraded);
                        }
                        commandBuffer.AddComponent<Updated>(connectedEdge.m_Edge);
                        commandBuffer.AddComponent<Updated>(otherNode);
                        anyUpdated = true;
                    }
                    if (anyUpdated)
                    {
                        commandBuffer.AddComponent<Updated>(node);
                    }
                }
            }
        }
    }
}
