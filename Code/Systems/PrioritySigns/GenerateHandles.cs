using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.PrioritySigns;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using CarLane = Game.Net.CarLane;
using Edge = Game.Net.Edge;
using SecondaryLane = Game.Net.SecondaryLane;
using SubLane = Game.Net.SubLane;

namespace Traffic.Systems.PrioritySigns
{
#if WITH_BURST
    [BurstCompile]
#endif
    public partial class GenerateHandles : GameSystemBase
    {
        private ModificationBarrier5 _modificationBarrier;
        private EntityQuery _query;

        protected override void OnCreate()
        {
            base.OnCreate();
            _modificationBarrier = base.World.GetOrCreateSystemManaged<ModificationBarrier5>();
            _query = SystemAPI.QueryBuilder()
                .WithAll<EditIntersection, EditPriorities, Updated>()
                .WithNone<Temp, Deleted>().Build();

            RequireForUpdate(_query);
        }

        protected override void OnUpdate()
        {
            EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
            JobHandle jobHandle = new GenerateLaneHandlesJob()
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                editIntersectionType = SystemAPI.GetComponentTypeHandle<EditIntersection>(true),
                priorityHandleType = SystemAPI.GetBufferTypeHandle<PriorityHandle>(true),
                carLaneData = SystemAPI.GetComponentLookup<CarLane>(true),
                compositionData = SystemAPI.GetComponentLookup<Composition>(true),
                curveData = SystemAPI.GetComponentLookup<Curve>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                edgeLaneData = SystemAPI.GetComponentLookup<EdgeLane>(true),
                edgeGeometryData = SystemAPI.GetComponentLookup<EdgeGeometry>(true),
                hiddenData = SystemAPI.GetComponentLookup<Hidden>(true),
                laneData = SystemAPI.GetComponentLookup<Lane>(true),
                masterLaneData = SystemAPI.GetComponentLookup<MasterLane>(true),
                nodeData = SystemAPI.GetComponentLookup<Node>(true),
                netCompositionData = SystemAPI.GetComponentLookup<NetCompositionData>(true),
                netLaneData = SystemAPI.GetComponentLookup<NetLaneData>(true),
                secondaryLaneData = SystemAPI.GetComponentLookup<SecondaryLane>(true),
                tempData = SystemAPI.GetComponentLookup<Temp>(true),
                connectedEdgesBuffer = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                lanePriorityBuffer = SystemAPI.GetBufferLookup<LanePriority>(true),
                prefabCompositionLaneBuffer = SystemAPI.GetBufferLookup<NetCompositionLane>(true),
                subLanesBuffer = SystemAPI.GetBufferLookup<SubLane>(true),
                commandBuffer = commandBuffer,
            }.Schedule(_query, Dependency);
            jobHandle.Complete();
            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();

            _modificationBarrier.AddJobHandleForProducer(jobHandle);
            Dependency = jobHandle;
        }
    }
}
