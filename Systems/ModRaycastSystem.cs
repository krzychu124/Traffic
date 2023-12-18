using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using Traffic.Common;
using Traffic.Helpers;
using Traffic.LaneConnections;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using SearchSystem = Traffic.LaneConnections.SearchSystem;

namespace Traffic.Systems
{

    public partial class ModRaycastSystem : GameSystemBase
    {
        private SearchSystem _searchSystem;
        private TerrainSystem _terrainSystem;
        private EntityQuery _terrainQuery;
        private NativeReference<CustomRaycastInput> _input;
        private NativeReference<CustomRaycastResult> _result;
        private NativeReference<RaycastResult> _terrainResult;

        protected override void OnCreate() {
            base.OnCreate();
            _searchSystem = World.GetOrCreateSystemManaged<SearchSystem>();
            _terrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            _input = new NativeReference<CustomRaycastInput>(Allocator.Persistent);
            _result = new NativeReference<CustomRaycastResult>(Allocator.Persistent);
            _terrainResult = new NativeReference<RaycastResult>(Allocator.Persistent);
            _terrainQuery = GetEntityQuery(ComponentType.ReadOnly<Terrain>(), ComponentType.Exclude<Temp>());
        }

        protected override void OnUpdate() {
            _result.Value = new CustomRaycastResult();
            _terrainResult.Value = new RaycastResult();
            PerformRaycast();
            _input.Value = new CustomRaycastInput();
        }

        private void PerformRaycast() {
            CustomRaycastInput input = _input.Value;

            JobHandle jobHandle = default;
            if ((input.typeMask & TypeMask.Terrain) != 0)
            {
                RaycastTerrainJob terrainJob = new RaycastTerrainJob()
                {
                    input = input,
                    result = _terrainResult,
                    terrainData = _terrainSystem.GetHeightData(),
                    terrainEntity = _terrainQuery.GetSingletonEntity(),
                };
                jobHandle = terrainJob.Schedule(Dependency);
                _terrainSystem.AddCPUHeightReader(jobHandle);
            }
            
            NativeList<Entity> entities = new NativeList<Entity>(Allocator.TempJob);
            RaycastJobs.FindConnectionNodeFromTreeJob job = new RaycastJobs.FindConnectionNodeFromTreeJob()
            {
                expand = 0.25f,
                input = input,
                entityList = entities,
                searchTree = _searchSystem.GetSearchTree(true, out JobHandle dependencies)
            };
            
            JobHandle jobHandle2 = job.Schedule(JobHandle.CombineDependencies(Dependency, jobHandle, dependencies));
            _searchSystem.AddSearchTreeReader(jobHandle2);
            jobHandle2.Complete();
            NativeReference<CustomRaycastResult> customRes = new NativeReference<CustomRaycastResult>(Allocator.TempJob);
            RaycastJobs.RaycastLaneConnectionSubObjects raycastLaneConnectionSubObjects = new RaycastJobs.RaycastLaneConnectionSubObjects()
            {
                connectorData = SystemAPI.GetComponentLookup<Connector>(true),
                terrainResult = _terrainResult.AsReadOnly(),
                entities = entities.AsReadOnly(),
                input = input,
                result = customRes
            };
            JobHandle jobHandle3 = raycastLaneConnectionSubObjects.Schedule(entities, 1, Dependency);
            jobHandle3.Complete();
            if ((input.typeMask & TypeMask.Terrain) != 0 && _terrainResult.Value.m_Owner != Entity.Null)
            {
                _result.Value = new CustomRaycastResult
                {
                    hit = _terrainResult.Value.m_Hit,
                    owner = _terrainResult.Value.m_Owner,
                };
            }
            if (customRes.Value.owner != Entity.Null)
            {
                _result.Value = customRes.Value;
            }
            entities.Dispose();
            customRes.Dispose();
        }

        public void SetInput(CustomRaycastInput input) {
            _input.Value = input;
        }

        public bool GetRaycastResult(out CustomRaycastResult result) {
            result = _result.Value;
            return result.owner != Entity.Null;
        }
        
        private struct RaycastTerrainJob : IJob
        {
            [ReadOnly] public CustomRaycastInput input;
            [ReadOnly] public TerrainHeightData terrainData;
            [ReadOnly] public Entity terrainEntity;
            public NativeReference<RaycastResult> result;
            
            public void Execute() {

                Line3.Segment segment = input.line + input.offset;
                if ((input.typeMask & TypeMask.Terrain) != 0 && TerrainUtils.Raycast(ref terrainData, segment, false, out float t2, out float3 normal))
                {
                    float3 pos = MathUtils.Position(segment, t2);
                    RaycastResult value = new()
                    {
                        m_Owner = terrainEntity,
                        m_Hit = new RaycastHit
                        {
                            m_HitEntity = terrainEntity,
                            m_Position = pos,
                            m_HitPosition = pos,
                            m_HitDirection = normal,
                            m_CellIndex = int2.zero,
                            m_NormalizedDistance = t2 + 1f / math.max(1f, MathUtils.Length(segment)),
                            m_CurvePosition = 0
                        }
                    };
                    result.Value = value;
                }
            }
        }
    }


}