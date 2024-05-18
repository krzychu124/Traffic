using Colossal.Collections;
using Game;
using Game.Common;
using Game.Tools;
using Traffic.Components.LaneConnections;
using Traffic.Components.PrioritySigns;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.Systems.LaneConnections
{

#if WITH_BURST
    [BurstCompile]
#endif
    public partial class SearchSystem : GameSystemBase
    {
        private NativeQuadTree<Entity, QuadTreeBoundsXZ> _searchTree;
        private NativeQuadTree<Entity, QuadTreeBoundsXZ> _laneSearchTree;
        private EntityQuery _query;
        private EntityQuery _laneQuery;

        private JobHandle _readDependencies;
        private JobHandle _writeDependencies;
        private JobHandle _readLaneHandleDependencies;
        private JobHandle _writeLaneHandleDependencies;
        private ModificationBarrier5 _modificationBarrier;

        protected override void OnCreate() {
            base.OnCreate();
            _query = GetEntityQuery(new EntityQueryDesc[]
            {
                new()
                {
                    All = new[] { ComponentType.ReadOnly<Connector>() },
                    Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
                    None = new[] { ComponentType.ReadOnly<Temp>() }
                },
            });
            _laneQuery = GetEntityQuery(new EntityQueryDesc[]
            {
                new()
                {
                    All = new[] { ComponentType.ReadOnly<LaneHandle>() },
                    Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
                    None = new[] { ComponentType.ReadOnly<Temp>() }
                },
            });
            _searchTree = new NativeQuadTree<Entity, QuadTreeBoundsXZ>(4, Allocator.Persistent);
            _laneSearchTree = new NativeQuadTree<Entity, QuadTreeBoundsXZ>(4, Allocator.Persistent);
            _modificationBarrier = World.GetOrCreateSystemManaged<ModificationBarrier5>();
            RequireAnyForUpdate(_query, _laneQuery);
        }

        protected override void OnUpdate() {
            if (!_query.IsEmptyIgnoreFilter)
            {
                JobHandle jobHandle = new UpdateSearchTree
                {
                    entityType = SystemAPI.GetEntityTypeHandle(),
                    updatedType = SystemAPI.GetComponentTypeHandle<Updated>(true),
                    deletedType = SystemAPI.GetComponentTypeHandle<Deleted>(true),
                    connectorType = SystemAPI.GetComponentTypeHandle<Connector>(true),
                    searchTree = GetSearchTree(false, out JobHandle dependencies),
                }.Schedule(_query, JobHandle.CombineDependencies(Dependency, dependencies));
                _modificationBarrier.AddJobHandleForProducer(jobHandle);
                Dependency = jobHandle;
                AddSearchTreeWriter(base.Dependency);
            }
            if (!_laneQuery.IsEmptyIgnoreFilter)
            {
                JobHandle jobHandle = new UpdateLaneHandleSearchTree
                {
                    entityType = SystemAPI.GetEntityTypeHandle(),
                    updatedType = SystemAPI.GetComponentTypeHandle<Updated>(true),
                    deletedType = SystemAPI.GetComponentTypeHandle<Deleted>(true),
                    laneHandleType = SystemAPI.GetComponentTypeHandle<LaneHandle>(true),
                    searchTree = GetLaneHandleSearchTree(false, out JobHandle dependencies),
                }.Schedule(_laneQuery, JobHandle.CombineDependencies(Dependency, dependencies));
                _modificationBarrier.AddJobHandleForProducer(jobHandle);
                Dependency = jobHandle;
                AddLaneHandleSearchTreeWriter(base.Dependency);
            }
        }
        
        public NativeQuadTree<Entity, QuadTreeBoundsXZ> GetSearchTree(bool readOnly, out JobHandle dependencies)
        {
            dependencies = (readOnly ? _writeDependencies : JobHandle.CombineDependencies(_readDependencies, _writeDependencies));
            return _searchTree;
        }
        
        public NativeQuadTree<Entity, QuadTreeBoundsXZ> GetLaneHandleSearchTree(bool readOnly, out JobHandle dependencies)
        {
            dependencies = (readOnly ? _writeLaneHandleDependencies : JobHandle.CombineDependencies(_readLaneHandleDependencies, _writeLaneHandleDependencies));
            return _laneSearchTree;
        }

        public void AddSearchTreeReader(JobHandle jobHandle)
        {
            _readDependencies = JobHandle.CombineDependencies(_readDependencies, jobHandle);
        }

        public void AddSearchTreeWriter(JobHandle jobHandle)
        {
            _writeDependencies = jobHandle;
        }

        public void AddLaneHandleSearchTreeReader(JobHandle jobHandle)
        {
            _readLaneHandleDependencies = JobHandle.CombineDependencies(_readLaneHandleDependencies, jobHandle);
        }

        public void AddLaneHandleSearchTreeWriter(JobHandle jobHandle)
        {
            _writeLaneHandleDependencies = jobHandle;
        }

        protected override void OnDestroy() {
            _searchTree.Dispose();
            _laneSearchTree.Dispose();
            base.OnDestroy();
        }
    }
}