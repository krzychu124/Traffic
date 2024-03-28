using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Traffic.Components;
using Traffic.LaneConnections;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.Tools
{
    public partial class ValidationSystem : GameSystemBase
    {
        private EntityQuery _tempQuery;
        private ModificationEndBarrier _modificationBarrier;

        protected override void OnCreate() {
            base.OnCreate();
            _modificationBarrier = World.GetOrCreateSystemManaged<ModificationEndBarrier>();
            _tempQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>()},
                Any = new []{ ComponentType.ReadOnly<EditIntersection>(), ComponentType.ReadOnly<ModifiedLaneConnections>()},
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });
            RequireForUpdate(_tempQuery);
        }

        protected override void OnUpdate() {
            ValidateLaneConnectorTool validateJob = new ValidateLaneConnectorTool()
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                editIntersectionType = SystemAPI.GetComponentTypeHandle<EditIntersection>(true),
                modifiedLaneConnectionsType = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                upgradedData = SystemAPI.GetComponentLookup<Upgraded>(true),
                deletedData = SystemAPI.GetComponentLookup<Deleted>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                compositionData = SystemAPI.GetComponentLookup<Composition>(true),
                netCompositionData = SystemAPI.GetComponentLookup<NetCompositionData>(true),
                warnResetUpgradeBuffer = SystemAPI.GetBufferLookup<WarnResetUpgrade>(true),
                connectedEdgesBuffer = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                commandBuffer = _modificationBarrier.CreateCommandBuffer(),
            };
            JobHandle jobHandle = validateJob.Schedule(_tempQuery, Dependency);
            _modificationBarrier.AddJobHandleForProducer(jobHandle);
            Dependency = jobHandle;
        }
    }
}
