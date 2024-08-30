using Game;
using Game.Common;
using Game.Tools;
using Traffic.Components;
using Unity.Burst;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.Tools
{
#if WITH_BURST
    [BurstCompile]
#endif
    public partial class TrafficToolClearSystem : GameSystemBase
    {
        private EntityQuery _query;
        private ToolOutputBarrier _toolOutputBarrier;
        
        protected override void OnCreate()
        {
            base.OnCreate();
            _toolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();
            _query = GetEntityQuery(new EntityQueryDesc() {All = new []{ComponentType.ReadOnly<DataTemp>()}, None = new []{ComponentType.ReadOnly<Deleted>()}});
            RequireForUpdate(_query);
        }

        protected override void OnUpdate()
        {
            JobHandle job = new ClearEntitiesJob()
            {
                entityType = SystemAPI.GetEntityTypeHandle(),
                dataOwnerType = SystemAPI.GetComponentTypeHandle<DataOwner>(true),
                dataTempType = SystemAPI.GetComponentTypeHandle<DataTemp>(true),
                dataOwnerData = SystemAPI.GetComponentLookup<DataOwner>(true),
                tempData = SystemAPI.GetComponentLookup<Temp>(true),
                commandBuffer = _toolOutputBarrier.CreateCommandBuffer().AsParallelWriter()
            }.ScheduleParallel(_query, Dependency);
            _toolOutputBarrier.AddJobHandleForProducer(job);
            Dependency = job;
        }
    }
}
