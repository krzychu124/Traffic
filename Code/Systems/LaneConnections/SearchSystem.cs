using Colossal.Collections;
using Game;
using Game.Common;
using Game.Tools;
using Traffic.Components.LaneConnections;
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
        private EntityQuery _query;

        private JobHandle _readDependencies;

        private JobHandle _writeDependencies;
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
            _searchTree = new NativeQuadTree<Entity, QuadTreeBoundsXZ>(4, Allocator.Persistent);
            _modificationBarrier = World.GetOrCreateSystemManaged<ModificationBarrier5>();
            RequireForUpdate(_query);
        }

        protected override void OnUpdate() {
            // Logger.Info($"Updating SearchSystem (frame: {UnityEngine.Time.renderedFrameCount}). Chunks: {_query.CalculateChunkCount()}, entities: {_query.CalculateEntityCount()}");

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
        }
        
        public NativeQuadTree<Entity, QuadTreeBoundsXZ> GetSearchTree(bool readOnly, out JobHandle dependencies)
        {
            dependencies = (readOnly ? _writeDependencies : JobHandle.CombineDependencies(_readDependencies, _writeDependencies));
            return _searchTree;
        }

        public void AddSearchTreeReader(JobHandle jobHandle)
        {
            _readDependencies = JobHandle.CombineDependencies(_readDependencies, jobHandle);
        }

        public void AddSearchTreeWriter(JobHandle jobHandle)
        {
            _writeDependencies = jobHandle;
        }

        protected override void OnDestroy() {
            _searchTree.Dispose();
            base.OnDestroy();
        }
    }
}