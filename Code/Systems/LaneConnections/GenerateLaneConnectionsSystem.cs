// #define DEBUG_CONNECTIONS

using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Traffic.Components.LaneConnections;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Traffic.Systems.LaneConnections
{
#if WITH_BURST
    [BurstCompile]
#endif
    public partial class GenerateLaneConnectionsSystem : GameSystemBase
    {
        private EntityQuery _query;
        private EntityQuery _definitionQuery;
        private ModificationBarrier3 _modificationBarrier;

        protected override void OnCreate() {
            base.OnCreate();
            _modificationBarrier = World.GetOrCreateSystemManaged<ModificationBarrier3>();
            _query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Updated>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });
            _definitionQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CreationDefinition>(), ComponentType.ReadOnly<ConnectionDefinition>(), ComponentType.ReadOnly<Updated>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });

            RequireForUpdate(_query);
        }

        protected override void OnUpdate() {
            Logger.DebugConnections("Run GenerateLaneConnectionsSystem");
            int count = _query.CalculateEntityCount();
            NativeParallelHashSet<Entity> createdModifiedLaneConnections = new NativeParallelHashSet<Entity>(16, Allocator.TempJob);
            NativeList<Entity> tempNodes = new NativeList<Entity>(count, Allocator.TempJob);
            NativeParallelHashMap<Entity, Entity> tempEntityMap = new NativeParallelHashMap<Entity, Entity>(count*4, Allocator.TempJob);
            
            // TODO investigate if EdgeIterator can be used instead
            FillTempNodeMapJob fillTempNodeMapJob = new FillTempNodeMapJob
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                connectedEdgeTypeHandle = SystemAPI.GetBufferTypeHandle<ConnectedEdge>(true),
                tempData = SystemAPI.GetComponentLookup<Temp>(true),
                nodeData = SystemAPI.GetComponentLookup<Node>(true),
                tempNodes = tempNodes.AsParallelWriter(),
                tempEntityMap = tempEntityMap,
            };
            JobHandle jobHandle = fillTempNodeMapJob.Schedule(_query, Dependency);

            //TODO SIMPLIFY
            if (!_definitionQuery.IsEmptyIgnoreFilter)
            {
                // UGLY CODE START (improve/redesign)
                NativeParallelMultiHashMap<Entity, TempModifiedConnections> createdModifiedConnections = new NativeParallelMultiHashMap<Entity, TempModifiedConnections>(4, Allocator.TempJob);
                EntityCommandBuffer entityCommandBuffer = new EntityCommandBuffer(Allocator.TempJob);
                GenerateTempConnectionsJob tempConnectionsJob = new GenerateTempConnectionsJob
                {
                    creationDefinitionTypeHandle = SystemAPI.GetComponentTypeHandle<CreationDefinition>(true),
                    connectionDefinitionTypeHandle = SystemAPI.GetComponentTypeHandle<ConnectionDefinition>(true),
                    tempConnectionBufferTypeHandle = SystemAPI.GetBufferTypeHandle<TempLaneConnection>(true),
                    tempEntityMap = tempEntityMap.AsReadOnly(),
                    createdModifiedLaneConnections = createdModifiedLaneConnections,
                    createdModifiedConnections = createdModifiedConnections,
                    commandBuffer = entityCommandBuffer.AsParallelWriter(),
                };
                JobHandle tempConnectionsHandle = tempConnectionsJob.Schedule(_definitionQuery, jobHandle);
                tempConnectionsHandle.Complete();
                
                // GetUniqueKeyArray() returns sorted array of unique keys, tightly packed from start of array and the number of remaining items!!
                // Length of returned array might be INCORRECT (internal NativeArray.Unique<T>() call is not performing resize for performance reasons)
                (NativeArray<Entity> keys, int uniqueKeyCount) = createdModifiedConnections.GetUniqueKeyArray(Allocator.Temp);
                NativeList<Entity> entities = new NativeList<Entity>(uniqueKeyCount, Allocator.TempJob);
                entities.ResizeUninitialized(uniqueKeyCount);
                new NativeSlice<Entity>(keys, 0, uniqueKeyCount).CopyTo(entities.AsArray());
                NativeParallelHashSet<Entity> processedEntities = new NativeParallelHashSet<Entity>(entities.Length, Allocator.TempJob);
                MapTempConnectionsJob mapTempConnectionsJob = new MapTempConnectionsJob
                {
                    modifiedConnectionsBuffer = SystemAPI.GetBufferLookup<ModifiedLaneConnections>(true),
                    createdModifiedConnections = createdModifiedConnections,
                    fakePrefabRef = Traffic.Systems.ModDefaultsSystem.FakePrefabRef,
                    keys = entities,
                    processedEntities = processedEntities,
                    commandBuffer = entityCommandBuffer.AsParallelWriter(),
                };
                JobHandle mapTempHandle = mapTempConnectionsJob.Schedule(entities, 1, tempConnectionsHandle);
                mapTempHandle.Complete();
                entityCommandBuffer.Playback(EntityManager);
                entityCommandBuffer.Dispose();
                processedEntities.Dispose(mapTempHandle);
                entities.Dispose();
                createdModifiedConnections.Dispose();
                // UGLY CODE END
            }

            tempNodes.Dispose(jobHandle);
            tempEntityMap.Dispose(jobHandle);
            createdModifiedLaneConnections.Dispose(jobHandle);

            _modificationBarrier.AddJobHandleForProducer(jobHandle);
            Dependency = jobHandle;
        }

        private struct TempModifiedConnections
        {
            public Entity dataOwner;
            public Entity owner;
            public TempFlags flags;
            public Entity edgeEntity;
            public int laneIndex;
            public int2 carriagewayAndGroup;
            public float3 lanePosition;
            public NativeArray<GeneratedConnection> generatedConnections;
        }
    }
}
