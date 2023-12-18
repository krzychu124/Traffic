using Colossal.Collections;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Rendering;
using Game.Tools;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Traffic.LaneConnections
{

    public partial class SearchSystem : GameSystemBase
    {
        private NativeQuadTree<Entity, QuadTreeBoundsXZ> _searchTree;
        private EntityQuery _query;

        private JobHandle _readDependencies;

        private JobHandle _writeDependencies;

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

            RequireForUpdate(_query);
        }

        protected override void OnUpdate() {
            Logger.Info($"Updating SearchSystem: {_query.CalculateChunkCount()}, {_query.CalculateEntityCount()}");

            JobHandle jobHandle = new UpdateSearchTree{
                entityType = SystemAPI.GetEntityTypeHandle(), 
                deletedType = SystemAPI.GetComponentTypeHandle<Deleted>(true),
                connectorType = SystemAPI.GetComponentTypeHandle<Connector>(true),
                searchTree = GetSearchTree(false, out JobHandle dependencies),
            }.Schedule(_query, JobHandle.CombineDependencies(Dependency, dependencies));
            Dependency = jobHandle;
            AddSearchTreeWriter(base.Dependency);
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

        [BurstCompile]
        private struct UpdateSearchTree : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentTypeHandle<Deleted> deletedType;
            [ReadOnly] public ComponentTypeHandle<Connector> connectorType;
            public NativeQuadTree<Entity, QuadTreeBoundsXZ> searchTree;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                if (chunk.Has(ref deletedType))
                {
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                    foreach (Entity entity in entities)
                    {
                        searchTree.Remove(entity);
                    }
                    return;
                }

                NativeArray<Entity> newEntities = chunk.GetNativeArray(entityType);
                NativeArray<Connector> connectors = chunk.GetNativeArray(ref connectorType);
                for (int index = 0; index < newEntities.Length; index++)
                {
                    Entity entity = newEntities[index];
                    Connector connector = connectors[index];
                    int lod = RenderingUtils.CalculateLodLimit(RenderingUtils.GetRenderingSize(new float2(1f)));
                    searchTree.Add(entity, new QuadTreeBoundsXZ(new Bounds3(connector.position - .15f, connector.position + .15f), BoundsMask.NormalLayers, lod));
                }
            }
        }
    }
}