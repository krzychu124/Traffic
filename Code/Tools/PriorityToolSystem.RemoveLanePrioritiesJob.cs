using Game.Common;
using Game.Net;
using Traffic.Components;
using Traffic.Components.PrioritySigns;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.Tools
{
    public partial class PriorityToolSystem
    {
        private struct RemoveLanePrioritiesJob : IJobFor
        {
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<ModifiedPriorities> modifiedPriorityData;
            [ReadOnly] public NativeArray<Entity> entities;
            public EntityCommandBuffer.ParallelWriter commandBuffer;


            public void Execute(int index)
            {
                Entity entity = entities[index];
                if (modifiedPriorityData.HasComponent(entity))
                {
                    commandBuffer.RemoveComponent<ModifiedPriorities>(index, entity);
                }
                commandBuffer.RemoveComponent<LanePriority>(index, entity);
                Edge e = edgeData[entity];
                // update edge
                commandBuffer.AddComponent<Updated>(index, entity);
                // update nodes
                commandBuffer.AddComponent<Updated>(index, e.m_Start);
                commandBuffer.AddComponent<Updated>(index, e.m_End);
            }
        }
    }
}
