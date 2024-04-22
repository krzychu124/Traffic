using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Traffic.Components.LaneConnections;
using Unity.Burst;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.Systems
{
#if WITH_BURST
    [BurstCompile]
#endif
    public partial class ModificationDataSyncSystem : GameSystemBase
    {
        private ModificationBarrier4B _modificationBarrier;
        private EntityQuery _query;

        protected override void OnCreate() {
            base.OnCreate();
            _modificationBarrier = World.GetOrCreateSystemManaged<ModificationBarrier4B>();
            _query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ModifiedLaneConnections>(), ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<Deleted>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), }
            });
            RequireForUpdate(_query);
        }

        protected override void OnUpdate() {
            SyncModificationDataJob job = new SyncModificationDataJob()
            {
                entityType = SystemAPI.GetEntityTypeHandle(),
                tempType = SystemAPI.GetComponentTypeHandle<Temp>(true),
                deletedType = SystemAPI.GetComponentTypeHandle<Deleted>(true),
                modifiedLaneConnectionsType = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                commandBuffer = _modificationBarrier.CreateCommandBuffer().AsParallelWriter(),
            };
            JobHandle jobHandle = job.Schedule(_query, Dependency);
            _modificationBarrier.AddJobHandleForProducer(jobHandle);
            Dependency = jobHandle;
        }
    }
}
