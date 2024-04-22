﻿using Game;
using Game.Common;
using Game.Net;
using Game.Notifications;
using Game.Prefabs;
using Game.Tools;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using SubLane = Game.Net.SubLane;
using TrackLane = Game.Net.TrackLane;

namespace Traffic.Tools
{
    public partial class ValidationSystem : GameSystemBase
    {
        private EntityQuery _tempQuery;
        private EntityQuery _toolErrorPrefabQuery;
        private ModificationEndBarrier _modificationBarrier;
        private IconCommandSystem _iconCommandSystem;
        private Entity _tightCurveErrorPrefab;

        protected override void OnCreate() {
            base.OnCreate();
            _modificationBarrier = World.GetOrCreateSystemManaged<ModificationEndBarrier>();
            _iconCommandSystem = World.GetOrCreateSystemManaged<IconCommandSystem>();
            _tempQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>()},
                Any = new []{ ComponentType.ReadOnly<EditIntersection>(), ComponentType.ReadOnly<ModifiedLaneConnections>()},
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });
            _toolErrorPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<NotificationIconData>(), ComponentType.ReadOnly<ToolErrorData>());

            RequireForUpdate(_tempQuery);
        }

        protected override void OnUpdate() {

            if (!_toolErrorPrefabQuery.IsEmptyIgnoreFilter && _tightCurveErrorPrefab == Entity.Null)
            {
                NativeArray<ArchetypeChunk> toolErrorChunks = _toolErrorPrefabQuery.ToArchetypeChunkArray(Allocator.Temp);
                EntityTypeHandle entityTypeHandle = SystemAPI.GetEntityTypeHandle();
                ComponentTypeHandle<ToolErrorData> toolErrorTypeHandle = SystemAPI.GetComponentTypeHandle<ToolErrorData>(true);
                for (var i = 0; i < toolErrorChunks.Length; i++)
                {
                    NativeArray<ToolErrorData> array = toolErrorChunks[i].GetNativeArray(ref toolErrorTypeHandle);
                    for (var j = 0; j < array.Length; j++)
                    {
                        if (array[j].m_Error == ErrorType.TightCurve)
                        {
                            NativeArray<Entity> entities = toolErrorChunks[i].GetNativeArray(entityTypeHandle);
                            _tightCurveErrorPrefab = entities[j];
                            break;
                        }
                    }
                    
                    if (_tightCurveErrorPrefab != Entity.Null)
                    {
                        break;
                    }
                }
            }
            
            ValidateLaneConnectorTool validateJob = new ValidateLaneConnectorTool()
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                editIntersectionType = SystemAPI.GetComponentTypeHandle<EditIntersection>(true),
                subLaneTypeHandle = SystemAPI.GetBufferTypeHandle<SubLane>(true),
                modifiedLaneConnectionsType = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                upgradedData = SystemAPI.GetComponentLookup<Upgraded>(true),
                deletedData = SystemAPI.GetComponentLookup<Deleted>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                compositionData = SystemAPI.GetComponentLookup<Composition>(true),
                netCompositionData = SystemAPI.GetComponentLookup<NetCompositionData>(true),
                trackLaneData= SystemAPI.GetComponentLookup<TrackLane>(true),
                trackLanePrefabData = SystemAPI.GetComponentLookup<TrackLaneData>(true),
                laneData= SystemAPI.GetComponentLookup<Lane>(true),
                curveData= SystemAPI.GetComponentLookup<Curve>(true),
                prefabRefData= SystemAPI.GetComponentLookup<PrefabRef>(true),
                warnResetUpgradeBuffer = SystemAPI.GetBufferLookup<WarnResetUpgrade>(true),
                connectedEdgesBuffer = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                subLaneBuffer = SystemAPI.GetBufferLookup<SubLane>(true),
                tightCurvePrefabEntity = _tightCurveErrorPrefab,
                commandBuffer = _modificationBarrier.CreateCommandBuffer(),
                iconCommandBuffer = _iconCommandSystem.CreateCommandBuffer(),
            };
            JobHandle jobHandle = validateJob.Schedule(_tempQuery, Dependency);
            _iconCommandSystem.AddCommandBufferWriter(jobHandle);
            _modificationBarrier.AddJobHandleForProducer(jobHandle);
            Dependency = jobHandle;
        }
    }
}
