using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Traffic.Components.PrioritySigns;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.Systems.PrioritySigns
{
#if WITH_BURST
    [BurstCompile]
#endif
    public partial class SyncCustomPrioritiesSystem : GameSystemBase
    {
        private EntityQuery _updatedEdgesQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            _updatedEdgesQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<ConnectedNode>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Updated>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.Exclude<LanePriority>(), }
            });
            RequireForUpdate(_updatedEdgesQuery);
        }

        protected override void OnUpdate()
        {
            EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
            JobHandle jobHandle = new SyncOriginalPrioritiesJob()
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                edgeTypeHandle = SystemAPI.GetComponentTypeHandle<Edge>(true),
                compositionTypeHandle = SystemAPI.GetComponentTypeHandle<Composition>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                tempData = SystemAPI.GetComponentLookup<Temp>(true),
                netCompositionData = SystemAPI.GetComponentLookup<NetCompositionData>(true),
                lanePriorityData = SystemAPI.GetBufferLookup<LanePriority>(true),
                commandBuffer = commandBuffer.AsParallelWriter(),
            }.Schedule(_updatedEdgesQuery, Dependency);
            jobHandle.Complete();
            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
            Dependency = jobHandle;
        }
    }
}
