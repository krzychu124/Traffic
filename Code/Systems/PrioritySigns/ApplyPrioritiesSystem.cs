using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Traffic.Components;
using Traffic.Components.PrioritySigns;
using Unity.Burst;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.Systems.PrioritySigns
{
#if WITH_BURST
    [BurstCompile]
#endif
    public partial class ApplyPrioritiesSystem : GameSystemBase
    {
        private EntityQuery _tempEdgesQuery;
        private ToolOutputBarrier _toolOutputBarrier;

        protected override void OnCreate()
        {
            base.OnCreate();
            _tempEdgesQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<ConnectedNode>(), ComponentType.ReadOnly<Temp>() },
                Any = new[] { ComponentType.ReadOnly<ModifiedPriorities>(), ComponentType.ReadOnly<LanePriority>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });
            _toolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();

            RequireForUpdate(_tempEdgesQuery);
        }

        protected override void OnUpdate()
        {
            JobHandle jobHandle = new HandleTempEntitiesJob()
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                editIntersectionTypeHandle = SystemAPI.GetComponentTypeHandle<EditIntersection>(true),
                lanePriorityTypeHandle = SystemAPI.GetBufferTypeHandle<LanePriority>(true),
                lanePriorityData = SystemAPI.GetBufferLookup<LanePriority>(true),
                commandBuffer = _toolOutputBarrier.CreateCommandBuffer(),
            }.Schedule(_tempEdgesQuery, Dependency);
            _toolOutputBarrier.AddJobHandleForProducer(jobHandle);
            Dependency = jobHandle;
        }
    }
}
