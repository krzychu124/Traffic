using System.Linq;
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

        // [BurstCompile]
        private struct UpdateSearchTree : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentTypeHandle<Deleted> deletedType;
            [ReadOnly] public ComponentTypeHandle<Updated> updatedType;
            [ReadOnly] public ComponentTypeHandle<Connector> connectorType;
            public NativeQuadTree<Entity, QuadTreeBoundsXZ> searchTree;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                if (chunk.Has(ref deletedType))
                {
                    Logger.Debug($"Deleted Connectors: {entities.Length}");
                    foreach (Entity entity in entities)
                    {
                        searchTree.TryRemove(entity);
                    }
                    return;
                } 
                if (chunk.Has(ref updatedType))
                {
                    // NativeArray<Entity> newEntities = chunk.GetNativeArray(entityType);
                    NativeArray<Connector> connectors = chunk.GetNativeArray(ref connectorType);
                    Logger.Debug($"Created/updated Connectors: {entities.Length}");
                    for (int index = 0; index < entities.Length; index++)
                    {
                        Entity entity = entities[index];
                        Connector connector = connectors[index];
                        int lod = RenderingUtils.CalculateLodLimit(RenderingUtils.GetRenderingSize(new float2(0.75f)));
                        searchTree.Add(entity, new QuadTreeBoundsXZ(new Bounds3(connector.position - .075f, connector.position + .075f), BoundsMask.NormalLayers, lod));
                    }
                }
                // else
                // {
                //     Logger.Info($"Other: {entities.Length} {string.Join(",", chunk.Archetype.GetComponentTypes().Select(t => t.GetManagedType().Name))}");
                // }
            }
        }
    }
}