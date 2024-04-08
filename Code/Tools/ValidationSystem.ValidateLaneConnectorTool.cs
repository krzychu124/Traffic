using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Traffic.Components;
using Traffic.LaneConnections;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.Tools
{
#if WITH_BURST
    [BurstCompile]
#endif
    public partial class ValidationSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct ValidateLaneConnectorTool : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<EditIntersection> editIntersectionType;
            [ReadOnly] public BufferTypeHandle<ModifiedLaneConnections> modifiedLaneConnectionsType;
            [ReadOnly] public ComponentLookup<Upgraded> upgradedData;
            [ReadOnly] public ComponentLookup<Deleted> deletedData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Composition> compositionData;
            [ReadOnly] public ComponentLookup<NetCompositionData> netCompositionData;
            [ReadOnly] public BufferLookup<WarnResetUpgrade> warnResetUpgradeBuffer;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgesBuffer;
            public EntityCommandBuffer commandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                NativeArray<EditIntersection> editIntersections = chunk.GetNativeArray(ref editIntersectionType);
                NativeList<Entity> warnEntities = new NativeList<Entity>(Allocator.Temp);
                
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity nodeEntity = Entity.Null;
                    if (chunk.Has(ref editIntersectionType))
                    {
                        EditIntersection e = editIntersections[i];
                        nodeEntity = e.node;
                    }
                    else if (chunk.Has(ref modifiedLaneConnectionsType))
                    {
                        nodeEntity = entity;
                    }
                    
                    if (nodeEntity != Entity.Null && connectedEdgesBuffer.HasBuffer(nodeEntity))
                    {
                        DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgesBuffer[nodeEntity];
                        for (int j = 0; j < connectedEdges.Length; j++)
                        {
                            ConnectedEdge connectedEdge = connectedEdges[j];
                            if (j == 0)
                            {
                                // check node composition if is roundabout (node is start or end of connectedEdge),
                                // "Edge" entity holds info about network Composition (edge, startNode, endNode)
                                Edge edge = edgeData[connectedEdge.m_Edge];
                                //todo check if possible that node might not be assigned to start/end node of Edge
                                bool? isStartNode = math.any(new bool2(edge.m_Start.Equals(nodeEntity), edge.m_End.Equals(nodeEntity))) ? edge.m_Start.Equals(nodeEntity) : null;
                                if (isStartNode.HasValue && compositionData.HasComponent(connectedEdge.m_Edge))
                                {
                                    Composition composition = compositionData[connectedEdge.m_Edge];
                                    CompositionFlags compositionFlags = netCompositionData[isStartNode.Value ? composition.m_StartNode : composition.m_EndNode].m_Flags;
                                    if ((compositionFlags.m_General &  CompositionFlags.General.Roundabout) != 0)
                                    {
                                        commandBuffer.AddComponent<BatchesUpdated>(nodeEntity);
                                        commandBuffer.AddComponent<Error>(entity);
                                        commandBuffer.AddComponent<Error>(nodeEntity);
                                    }
                                }
                            }
                            
                            if (upgradedData.HasComponent(connectedEdge.m_Edge) && !deletedData.HasComponent(connectedEdge.m_Edge))
                            {
                                Upgraded upgraded = upgradedData[connectedEdge.m_Edge];
                                Edge edge = edgeData[connectedEdge.m_Edge];
                                CompositionFlags.Side side = edge.m_Start == nodeEntity ? upgraded.m_Flags.m_Left : upgraded.m_Flags.m_Right;
                                if ((side & (CompositionFlags.Side.ForbidStraight | CompositionFlags.Side.ForbidLeftTurn | CompositionFlags.Side.ForbidRightTurn)) != 0)
                                {
                                    warnEntities.Add(connectedEdge.m_Edge);
                                }
                            }
                        }
                    }
                    
                    if (warnEntities.Length > 0)
                    {
                        DynamicBuffer<WarnResetUpgrade> warnResetUpgrades = warnResetUpgradeBuffer.HasBuffer(entity) ? commandBuffer.SetBuffer<WarnResetUpgrade>(entity) : commandBuffer.AddBuffer<WarnResetUpgrade>(entity);
                        warnResetUpgrades.ResizeUninitialized(warnEntities.Length);
                        for (var j = 0; j < warnEntities.Length; j++)
                        {
                            warnResetUpgrades[j] = new WarnResetUpgrade() { entity = warnEntities[j] };
                        }
                    }
                    else if (warnResetUpgradeBuffer.HasBuffer(entity))
                    {
                        commandBuffer.RemoveComponent<WarnResetUpgrade>(entity);
                    }
                    warnEntities.Clear();
                }
                warnEntities.Dispose();
            }
        }
    }
}
