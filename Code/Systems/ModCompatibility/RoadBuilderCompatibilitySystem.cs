using System;
using Game;
using Game.Common;
using Game.Net;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Traffic.Components.PrioritySigns;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.Systems.ModCompatibility
{
    using Colossal.IO.AssetDatabase;
    
#if WITH_BURST
    [BurstCompile]
#endif
    public partial class RoadBuilderCompatibilitySystem : GameSystemBase
    {
        private ComponentType _roadBuilderUpdateTag;
        private EntityQuery _query;

        protected override void OnCreate()
        {
            base.OnCreate();
            ExecutableAsset rbAsset = AssetDatabase.global.GetAsset<ExecutableAsset>(SearchFilter<ExecutableAsset>.ByCondition(asset => asset.isLoaded && asset.name.Equals("RoadBuilder")));
            Type rbType = rbAsset?.assembly.GetType("RoadBuilder.Domain.Components.RoadBuilderUpdateFlagComponent", false);;
            if (rbType == null)
            {
                Logger.Error("RoadBuilderUpdateFlagComponent not found! Disabling RoadBuilderCompatibilitySystem...");
                Enabled = false;
                return;
            }
            
            _roadBuilderUpdateTag = new ComponentType(rbType, ComponentType.AccessMode.ReadOnly);
            _query = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { _roadBuilderUpdateTag },
                Any = new[] { ComponentType.ReadOnly<ModifiedConnections>(), ComponentType.ReadOnly<ModifiedLaneConnections>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), },
            }, new EntityQueryDesc()
            {
                All = new[] { _roadBuilderUpdateTag },
                Any = new[] { ComponentType.ReadOnly<LanePriority>(), ComponentType.ReadOnly<ModifiedPriorities>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>(), },
            });
            RequireForUpdate(_query);
        }

        protected override void OnUpdate()
        {
#if DEBUG
            int count = _query.CalculateEntityCount();
            Logger.Debug($"Found {count} RoadBuilder entities!");
#endif

            EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.TempJob);
            JobHandle jobHandle = new ResetTrafficSettings()
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                edgeTypeHandle = SystemAPI.GetComponentTypeHandle<Edge>(true),
                nodeTypeHandle = SystemAPI.GetComponentTypeHandle<Node>(true),
                modifiedConnectionsTypeHandle = SystemAPI.GetComponentTypeHandle<ModifiedConnections>(true),
                modifiedPrioritiesTypeHandle = SystemAPI.GetComponentTypeHandle<ModifiedPriorities>(true),
                laneConnectionsTypeHandle = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                lanePriorityTypeHandle = SystemAPI.GetBufferTypeHandle<LanePriority>(true),
                commandBuffer = ecb,
            }.Schedule(_query, Dependency);
            jobHandle.Complete();
            ecb.Playback(EntityManager);
            ecb.Dispose();

            Dependency = jobHandle;
        }
    }
}
