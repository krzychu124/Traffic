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
            _query = SystemAPI.QueryBuilder()
                .WithAll<ModifiedLaneConnections, Node, Deleted>()
                .WithNone<Temp>()
                .Build();
            RequireForUpdate(_query);
        }

        protected override void OnUpdate() {
            JobHandle jobHandle = new SyncModificationDataJob()
            {
                entityType = SystemAPI.GetEntityTypeHandle(),
                modifiedLaneConnectionsType = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                commandBuffer = _modificationBarrier.CreateCommandBuffer().AsParallelWriter(),
            }.Schedule(_query, Dependency);
            _modificationBarrier.AddJobHandleForProducer(jobHandle);
            Dependency = jobHandle;
        }
    }
}
