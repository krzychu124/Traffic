using Game;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Notifications;
using Game.Prefabs;
using Game.Tools;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Traffic.Components.PrioritySigns;
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
        private ToolSystem _toolSystem;
        private BulldozeToolSystem _bulldozeToolSystem;
        private PriorityToolSystem _priorityToolSystem;
        private CityConfigurationSystem _cityConfigurationSystem;
        private Entity _tightCurveErrorPrefab;

        protected override void OnCreate() {
            base.OnCreate();
            _modificationBarrier = World.GetOrCreateSystemManaged<ModificationEndBarrier>();
            _iconCommandSystem = World.GetOrCreateSystemManaged<IconCommandSystem>();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _bulldozeToolSystem = World.GetOrCreateSystemManaged<BulldozeToolSystem>();
            _priorityToolSystem = World.GetOrCreateSystemManaged<PriorityToolSystem>();
            _cityConfigurationSystem = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            _tempQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>()},
                Any = new []{ ComponentType.ReadOnly<EditIntersection>(), ComponentType.ReadOnly<ModifiedLaneConnections>(), ComponentType.ReadOnly<LanePriority>()},
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });
            _toolErrorPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<NotificationIconData>(), ComponentType.ReadOnly<ToolErrorData>());

            RequireForUpdate(_tempQuery);
        }

        protected override void OnUpdate() 
        {
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

            if (!_bulldozeToolSystem.toolID.Equals(_toolSystem.activeTool?.toolID))
            {
                ValidateLaneConnectorTool validateJob = new ValidateLaneConnectorTool()
                {
                    entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                    editIntersectionType = SystemAPI.GetComponentTypeHandle<EditIntersection>(true),
                    toolActionBlockedType = SystemAPI.GetComponentTypeHandle<ToolActionBlocked>(true),
                    tempType = SystemAPI.GetComponentTypeHandle<Temp>(true),
                    edgeType = SystemAPI.GetComponentTypeHandle<Edge>(true),
                    modifiedLaneConnectionsType = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                    lanePriorityTypeHandle = SystemAPI.GetBufferTypeHandle<LanePriority>(true),
                    tempData = SystemAPI.GetComponentLookup<Temp>(true),
                    upgradedData = SystemAPI.GetComponentLookup<Upgraded>(true),
                    deletedData = SystemAPI.GetComponentLookup<Deleted>(true),
                    edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                    compositionData = SystemAPI.GetComponentLookup<Composition>(true),
                    netCompositionData = SystemAPI.GetComponentLookup<NetCompositionData>(true),
                    trackLaneData = SystemAPI.GetComponentLookup<TrackLane>(true),
                    trackLanePrefabData = SystemAPI.GetComponentLookup<TrackLaneData>(true),
                    laneData = SystemAPI.GetComponentLookup<Lane>(true),
                    curveData = SystemAPI.GetComponentLookup<Curve>(true),
                    prefabRefData = SystemAPI.GetComponentLookup<PrefabRef>(true),
                    toolManagedData = SystemAPI.GetComponentLookup<ToolManaged>(true),
                    connectedEdgesBuffer = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                    lanePriorityBuffer = SystemAPI.GetBufferLookup<LanePriority>(true),
                    subLaneBuffer = SystemAPI.GetBufferLookup<SubLane>(true),
                    tightCurvePrefabEntity = _tightCurveErrorPrefab,
                    leftHandTraffic = _cityConfigurationSystem.leftHandTraffic,
                    priorityToolActive = _toolSystem.activeTool == _priorityToolSystem,
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
}
