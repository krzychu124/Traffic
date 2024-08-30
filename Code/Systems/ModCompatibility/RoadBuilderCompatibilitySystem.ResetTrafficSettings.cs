using Game.Common;
using Game.Net;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Traffic.Components.PrioritySigns;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

namespace Traffic.Systems.ModCompatibility
{
    public partial class RoadBuilderCompatibilitySystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct ResetTrafficSettings : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Node> nodeTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Edge> edgeTypeHandle;
            [ReadOnly] public ComponentTypeHandle<ModifiedConnections> modifiedConnectionsTypeHandle;
            [ReadOnly] public ComponentTypeHandle<ModifiedPriorities> modifiedPrioritiesTypeHandle;
            [ReadOnly] public BufferTypeHandle<ModifiedLaneConnections> laneConnectionsTypeHandle;
            [ReadOnly] public BufferTypeHandle<LanePriority> lanePriorityTypeHandle;

            public EntityCommandBuffer commandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);

                if (chunk.Has(ref nodeTypeHandle))
                {
                    bool hasConnectionsTag = chunk.Has(ref modifiedConnectionsTypeHandle);
                    bool hasConnections = chunk.Has(ref laneConnectionsTypeHandle);
                    
                    foreach (Entity entity in entities)
                    {
                        Logger.Debug($"NodeEntity {entity}");
                    }
                    
                    if (hasConnections)
                    {
                        commandBuffer.RemoveComponent<ModifiedLaneConnections>(entities);
                        commandBuffer.AddComponent<Updated>(entities);
                    }
                    if (hasConnectionsTag)
                    {
                        commandBuffer.RemoveComponent<ModifiedConnections>(entities);
                    }
                }
                else if (chunk.Has(ref edgeTypeHandle))
                {
                    bool hasPrioritiesTag = chunk.Has(ref modifiedPrioritiesTypeHandle);
                    bool hasPriorities = chunk.Has(ref lanePriorityTypeHandle);
                    NativeArray<Edge> edges = chunk.GetNativeArray(ref edgeTypeHandle);
                    foreach (Entity entity in entities)
                    {
                        Logger.Debug($"EdgeEntity {entity}");
                    }
                    
                    if (hasPriorities)
                    {
                        commandBuffer.RemoveComponent<LanePriority>(entities);
                        foreach (Edge edge in edges)
                        {
                            commandBuffer.AddComponent<Updated>(edge.m_Start);
                            commandBuffer.AddComponent<Updated>(edge.m_End);
                        }
                        commandBuffer.AddComponent<Updated>(entities);
                    }
                    if (hasPrioritiesTag)
                    {
                        commandBuffer.RemoveComponent<ModifiedPriorities>(entities);
                    }
                }
                else
                {
                    Logger.Debug("Unsupported chunk type!");
                }
            }
        }
    }
}
