using Colossal.Collections;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components.LaneConnections;
using Traffic.Components.PrioritySigns;
using Traffic.Helpers;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using SearchSystem = Traffic.Systems.LaneConnections.SearchSystem;

namespace Traffic.Systems
{

#if WITH_BURST
    [BurstCompile]
#endif
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

            if ((input.typeMask & TypeMask.Lanes) != 0)
            {
                RaycastJobs.FindLaneHandleFromTreeJob laneHandleJob = new RaycastJobs.FindLaneHandleFromTreeJob()
                {
                    expandFovTan = input.fovTan,
                    input = input,
                    entityList = entities,
                    searchTree = _searchSystem.GetLaneHandleSearchTree(true, out JobHandle laneHandleSearchDependency)
                };
                JobHandle jobHandle2 = laneHandleJob.Schedule(JobHandle.CombineDependencies(Dependency, jobHandle, laneHandleSearchDependency));
                _searchSystem.AddLaneHandleSearchTreeReader(jobHandle2);
                jobHandle2.Complete();
            }
            else
            {

                RaycastJobs.FindConnectionNodeFromTreeJob job = new RaycastJobs.FindConnectionNodeFromTreeJob()
                {
                    input = input,
                    entityList = entities,
                    searchTree = _searchSystem.GetSearchTree(true, out JobHandle dependencies)
                };
                JobHandle jobHandle3 = job.Schedule(JobHandle.CombineDependencies(Dependency, jobHandle, dependencies));
                _searchSystem.AddSearchTreeReader(jobHandle3);
                jobHandle3.Complete();
            }
            //TODO change to accumulator to get best match instead overwriting results
            NativeReference<CustomRaycastResult> customRes = new NativeReference<CustomRaycastResult>(Allocator.TempJob);
            NativeAccumulator<RaycastResult> accumulator = new NativeAccumulator<RaycastResult>(Allocator.TempJob);
            if ((input.typeMask & TypeMask.Lanes) != 0)
            {
                JobHandle jobHandleLaneHandle = new RaycastJobs.RaycastLaneHandles()
                {
                    fovTan = input.fovTan,
                    laneHandleData = SystemAPI.GetComponentLookup<LaneHandle>(true),
                    input = input,
                    entities = entities.AsReadOnly(),
                    results = accumulator.AsParallelWriter(),
                }.Schedule(entities, 1, Dependency);
                jobHandleLaneHandle.Complete();
            }
            else
            {
                RaycastJobs.RaycastLaneConnectionSubObjects raycastLaneConnectionSubObjects = new RaycastJobs.RaycastLaneConnectionSubObjects()
                {
                    connectorData = SystemAPI.GetComponentLookup<Connector>(true),
                    entities = entities.AsReadOnly(),
                    input = input,
                    result = customRes
                };
                JobHandle jobHandle4 = raycastLaneConnectionSubObjects.Schedule(entities, 1, Dependency);
                jobHandle4.Complete();
            }
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
            if (accumulator.Length > 0 && accumulator.GetResult().m_Owner != Entity.Null)
            {
                var result = accumulator.GetResult();
                _result.Value = new CustomRaycastResult()
                {
                    hit = result.m_Hit,
                    owner = result.m_Owner
                };
            }
            entities.Dispose();
            customRes.Dispose();
            accumulator.Dispose();
        }

        public void SetInput(CustomRaycastInput input) {
            _input.Value = input;
        }

        public bool GetRaycastResult(out CustomRaycastResult result) {
            result = _result.Value;
            return result.owner != Entity.Null;
        }
        
        private static bool TryIntersectLineWithPlane(Line3 line, Triangle3 plane, float minDot, out float d)
        {
            float3 x = math.normalize(MathUtils.NormalCW(plane));
            if (math.abs(math.dot(x, math.normalize(line.ab))) > minDot)
            {
                float3 y = line.a - plane.a;
                d = (0f - math.dot(x, y)) / math.dot(x, line.ab);
                return true;
            }
            d = 0f;
            return false;
        }
    }


}