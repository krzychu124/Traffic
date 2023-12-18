using Game.Net;
using Game.Prefabs;
using Traffic.Components;
using Traffic.LaneConnections;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

namespace Traffic.Tools
{
    public partial class ValidationSystem
    {
        private struct ValidateLaneConnectorTool : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<EditIntersection> editIntersectionType;
            [ReadOnly] public ComponentLookup<Upgraded> upgradedData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public BufferLookup<WarnResetUpgrade> warnResetUpgradeBuffer;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgesBuffer;
            public EntityCommandBuffer commandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                NativeArray<EditIntersection> editIntersections = chunk.GetNativeArray(ref editIntersectionType);
                NativeList<Entity> warnEntities = new NativeList<Entity>(Allocator.Temp);
                for (var i = 0; i < editIntersections.Length; i++)
                {
                    EditIntersection e = editIntersections[i];
                    Entity entity = entities[i];
                    if (connectedEdgesBuffer.HasBuffer(e.node))
                    {
                        DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgesBuffer[e.node];
                        for (var j = 0; j < connectedEdges.Length; j++)
                        {
                            ConnectedEdge connectedEdge = connectedEdges[j];
                            if (upgradedData.HasComponent(connectedEdge.m_Edge))
                            {
                                Upgraded upgraded = upgradedData[connectedEdge.m_Edge];
                                Edge edge = edgeData[connectedEdge.m_Edge];
                                CompositionFlags.Side side = edge.m_Start == e.node ? upgraded.m_Flags.m_Left : upgraded.m_Flags.m_Right;
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
