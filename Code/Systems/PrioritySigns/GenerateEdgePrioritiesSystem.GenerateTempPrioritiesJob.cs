using Game.Tools;
using Traffic.Components.PrioritySigns;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

namespace Traffic.Systems.PrioritySigns
{
    public partial class GenerateEdgePrioritiesSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct GenerateTempPrioritiesJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<CreationDefinition> creationDefinitionTypeHandle;
            [ReadOnly] public ComponentTypeHandle<PriorityDefinition> priorityDefinitionTypeHandle;
            [ReadOnly] public BufferTypeHandle<TempLanePriority> tempLanePriorityBufferTypeHandle;
            [ReadOnly] public NativeParallelHashMap<Entity, Entity>.ReadOnly tempEntityMap;
            [ReadOnly] public BufferLookup<LanePriority> lanePrioritiesData;
            public EntityCommandBuffer.ParallelWriter commandBuffer;
 
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<CreationDefinition> definitions = chunk.GetNativeArray(ref creationDefinitionTypeHandle);
                NativeArray<PriorityDefinition> priorityDefinitions = chunk.GetNativeArray(ref priorityDefinitionTypeHandle);
                BufferAccessor<TempLanePriority> tempPrioritiesAccessor = chunk.GetBufferAccessor(ref tempLanePriorityBufferTypeHandle);
                
                for (int i = 0; i < definitions.Length; i++)
                {
                    CreationDefinition definition = definitions[i];
                    PriorityDefinition priorityDefinition = priorityDefinitions[i];
                    
                    if (tempPrioritiesAccessor.Length > 0 &&
                        // tempEntityMap.TryGetValue(definition.m_Original, out Entity tempNodeEntity) &&
                        tempEntityMap.TryGetValue(priorityDefinition.edge, out Entity sourceEdgeEntity))
                    {
                        DynamicBuffer<TempLanePriority> tempLaneConnections = tempPrioritiesAccessor[i];

                        if (lanePrioritiesData.HasBuffer(sourceEdgeEntity))
                        {
                            DynamicBuffer<LanePriority> lanePriorities = commandBuffer.SetBuffer<LanePriority>(unfilteredChunkIndex, sourceEdgeEntity);
                            lanePriorities.CopyFrom(tempLaneConnections.Reinterpret<LanePriority>().AsNativeArray());
                        }
                        else
                        {
                            DynamicBuffer<LanePriority> lanePriorities = commandBuffer.AddBuffer<LanePriority>(unfilteredChunkIndex, sourceEdgeEntity);
                            lanePriorities.CopyFrom(tempLaneConnections.Reinterpret<LanePriority>().AsNativeArray());
                        }
                    }
                }
            }
        }
    }
}
