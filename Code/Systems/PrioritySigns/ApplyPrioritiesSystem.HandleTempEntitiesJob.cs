using Game.Tools;
using Traffic.Components;
using Traffic.Components.PrioritySigns;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

namespace Traffic.Systems.PrioritySigns
{
    public partial class ApplyPrioritiesSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct HandleTempEntitiesJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Temp> tempTypeHandle;
            [ReadOnly] public ComponentTypeHandle<EditIntersection> editIntersectionTypeHandle;
            [ReadOnly] public BufferTypeHandle<LanePriority> lanePriorityTypeHandle;
            [ReadOnly] public BufferLookup<LanePriority> lanePriorityData;
            public EntityCommandBuffer commandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                if (!chunk.Has(ref lanePriorityTypeHandle))
                {
                    return;
                }
                
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                NativeArray<Temp> temps = chunk.GetNativeArray(ref tempTypeHandle);
                BufferAccessor<LanePriority> laneHandlesAccessor = chunk.GetBufferAccessor(ref lanePriorityTypeHandle);
                bool isEditNodeChunk = chunk.Has(ref editIntersectionTypeHandle);
                NativeList<LanePriority> nonDefaultPriorities = new NativeList<LanePriority>(Allocator.Temp);

                Logger.DebugTool($"Handle Temp Entities {entities.Length}, isEditNode: {isEditNodeChunk}");
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Temp tempEdge = temps[i];
                    DynamicBuffer<LanePriority> lanePriorities = laneHandlesAccessor[i];

                    if (tempEdge.m_Original != Entity.Null && (tempEdge.m_Flags & TempFlags.Delete) == 0)
                    {
                        nonDefaultPriorities.Clear();
                        DynamicBuffer<LanePriority> priorities;
                        for (var j = 0; j < lanePriorities.Length; j++)
                        {
                            LanePriority lanePriority = lanePriorities[j];
                            if (lanePriority.priority != PriorityType.Default)
                            {
                                nonDefaultPriorities.Add(lanePriority);
                            }
                        }

                        if ((nonDefaultPriorities.Length == 0 || (tempEdge.m_Flags & TempFlags.Modify) != 0) &&
                            lanePriorityData.HasBuffer(tempEdge.m_Original))
                        {
                            commandBuffer.RemoveComponent<LanePriority>(tempEdge.m_Original);
                            commandBuffer.RemoveComponent<ModifiedPriorities>(tempEdge.m_Original);
                            continue;
                        }

                        if (lanePriorityData.HasBuffer(tempEdge.m_Original))
                        {
                            priorities = commandBuffer.SetBuffer<LanePriority>(tempEdge.m_Original);
                        }
                        else
                        {
                            priorities = commandBuffer.AddBuffer<LanePriority>(tempEdge.m_Original);
                            commandBuffer.AddComponent<ModifiedPriorities>(tempEdge.m_Original);
                        }
                        priorities.CopyFrom(nonDefaultPriorities.AsArray());
                    }
                }
                nonDefaultPriorities.Dispose();
            }
        }
    }
}
