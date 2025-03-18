using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.SceneFlow;
using Game.UI;
using Game.UI.Localization;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Traffic.UISystems;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Profiling;
using Edge = Game.Net.Edge;

// ReSharper disable ConvertToUsingDeclaration

namespace Traffic.Systems.Serialization
{
#if WITH_BURST
    [BurstCompile]
#endif
    [FormerlySerializedAs("Traffic.Systems.DataMigration.TrafficDataMigrationSystem")]
    public partial class TrafficDataMigrationSystem : GameSystemBase, IDefaultSerializable, ISerializable
    {
        internal static readonly int2 InvalidCarriagewayAndGroup = new int2(-1);
        private EntityQuery _query;
        private int _version;
        private bool _loaded = false;

        protected override void OnCreate()
        {
            base.OnCreate();

            _query = SystemAPI.QueryBuilder()
                .WithAll<Node, ModifiedLaneConnections>()
                .Build();
        }

        protected override void OnUpdate()
        {
            if (!_loaded || _query.IsEmptyIgnoreFilter)
            {
                return;
            }
            
            Logger.Info($"{nameof(TrafficDataMigrationSystem)} migrating data version {_version}...");
            _loaded = false;

            bool regularValidationOnly = true;
            int allIntersections = CountIntersections();

            if (_version < DataMigrationVersion.LaneConnectionDataUpgradeV1)
            {
                regularValidationOnly = false;
                MigrateToV1();
            }
            else if (_version < DataMigrationVersion.LaneConnectionDataUpgradeV2)
            {
                regularValidationOnly = false;
                MigrateToV2();
            }
            
            Profiler.BeginSample(nameof(TrafficDataMigrationSystem));
            
            ValidateLoadedData();

            ModUISystem modUISystem = World.GetExistingSystemManaged<ModUISystem>();
            int count = modUISystem.AffectedIntersections.Count;
            Logger.Info($"Affected entities (1st pass): {count}");

            ValidateLoadedDataReferences();

            Profiler.EndSample();
            
            count = modUISystem.AffectedIntersections.Count;
            Logger.Info($"Affected entities (2nd pass): {count}");
            if (count > 0)
            {
                GameManager.instance.userInterface.appBindings.ShowMessageDialog(
                    new MessageDialog("Traffic mod data loading",
                        $"**Traffic** mod couldn't load data from {count} of {allIntersections} intersections.\n\n" +
                        "Use **Traffic's Lane Connector tool** to open loading results dialog for more information.",
                        LocalizedString.Id("Common.ERROR_DIALOG_CONTINUE")), null);
            }

            Logger.Info($"{nameof(TrafficDataMigrationSystem)} {(regularValidationOnly ? "validating" : "migrating")} data version {_version} done. Found {count} affected nodes of {allIntersections}");
        }

        private void ValidateLoadedData()
        {
            Logger.Info($"{nameof(TrafficDataMigrationSystem)} preparing validation job, data version: {_version}");
            Profiler.BeginSample("ValidateLoadedDataJob");
            Profiler.BeginSample("ValidateLoadedDataJob-PrepareData");
            NativeParallelMultiHashMap<Entity, Entity> referenceMap;
            
            EntityQuery dataOwnersQuery = SystemAPI.QueryBuilder().WithAll<DataOwner>().Build();
            if (!dataOwnersQuery.IsEmptyIgnoreFilter)
            {
                using (NativeArray<ArchetypeChunk> chunks = dataOwnersQuery.ToArchetypeChunkArray(Allocator.TempJob))
                {
                    int items = dataOwnersQuery.CalculateEntityCount();
                    Logger.Serialization($"Allocating NativeParallelMultiHashMap with {items * 4} capacity");
                    referenceMap = new NativeParallelMultiHashMap<Entity, Entity>(items * 4, Allocator.TempJob);
                    GetCollectDataOwnerReferencesJob(chunks, referenceMap).Complete();
                }
            }
            else
            {
                referenceMap = new NativeParallelMultiHashMap<Entity, Entity>(2, Allocator.TempJob);
            }

            Profiler.EndSample();
            Logger.Info($"Node -> LaneConnection DataOwner map: {referenceMap.Count()}, capacity: {referenceMap.Capacity}");

            NativeQueue<Entity> affectedEntities = new NativeQueue<Entity>(Allocator.TempJob);
            using (EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob))
            {
                JobHandle jobHandle = new ValidateLoadedDataJob()
                {
                    entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                    modifiedLaneConnectionsTypeHandle = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                    modifiedConnectionsTypeHandle = SystemAPI.GetComponentTypeHandle<ModifiedConnections>(true),
                    connectedEdgeTypeHandle = SystemAPI.GetBufferTypeHandle<ConnectedEdge>(true),
                    generatedConnectionBuffer = SystemAPI.GetBufferLookup<GeneratedConnection>(true),
                    netCompositionLaneBuffer = SystemAPI.GetBufferLookup<NetCompositionLane>(true),
                    edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                    deletedData = SystemAPI.GetComponentLookup<Deleted>(true),
                    dataOwnerData = SystemAPI.GetComponentLookup<DataOwner>(true),
                    compositionData = SystemAPI.GetComponentLookup<Composition>(true),
                    netCompositionData = SystemAPI.GetComponentLookup<NetCompositionData>(true),
                    roadCompositionData = SystemAPI.GetComponentLookup<RoadComposition>(true),
                    trackCompositionData = SystemAPI.GetComponentLookup<TrackComposition>(true),
                    prefabRefData = SystemAPI.GetComponentLookup<PrefabRef>(true),
                    fakePrefabEntity = Traffic.Systems.ModDefaultsSystem.FakePrefabRef,
                    dataOwnerRefs = referenceMap.AsReadOnly(),
                    entityInfoLookup = SystemAPI.GetEntityStorageInfoLookup(),
                    affectedEntities = affectedEntities.AsParallelWriter(),
                    commandBuffer = commandBuffer.AsParallelWriter(),
#if !SERIALIZATION
                }.ScheduleParallel(_query, Dependency);
#else
                }.Schedule(_query, Dependency);
#endif
                jobHandle.Complete();
                commandBuffer.Playback(EntityManager);
                Dependency = jobHandle;
            }
            referenceMap.Dispose();

            ModUISystem modUISystem = World.GetExistingSystemManaged<ModUISystem>();
            while (affectedEntities.TryDequeue(out Entity entity))
            {
                modUISystem.AddToAffectedIntersections(entity);
            }
            affectedEntities.Dispose();
            Profiler.EndSample();
        }

        private void ValidateLoadedDataReferences()
        {
            Profiler.BeginSample("ValidateLoadedReferencesJob");
            Profiler.BeginSample("ValidateLoadedReferencesJob-PrepareData");

            //prepare additional data
            EntityQuery invalidArchetypeQuery = SystemAPI.QueryBuilder().WithAll<Edge, ModifiedLaneConnections>().Build();
            if (!invalidArchetypeQuery.IsEmptyIgnoreFilter)
            {
                Logger.Warning($"Detected invalid archetype Edge + ModifiedLaneConnections -> {invalidArchetypeQuery.CalculateEntityCount()}");
            }
            
            NativeParallelMultiHashMap<Entity, Entity> referenceMap;
            
            EntityQuery nodesWithLaneConnectionsQuery = SystemAPI.QueryBuilder().WithAll<Node, ModifiedLaneConnections>().WithNone<Deleted>().Build();
            if (!nodesWithLaneConnectionsQuery.IsEmptyIgnoreFilter)
            {
                using (NativeArray<ArchetypeChunk> nodeChunks = nodesWithLaneConnectionsQuery.ToArchetypeChunkArray(Allocator.TempJob))
                {
                    int items = SystemAPI.QueryBuilder().WithAll<DataOwner>().WithNone<Deleted>().Build().CalculateEntityCount();
                    Logger.Serialization($"Allocating NativeParallelMultiHashMap with {items * 4} capacity");
                    referenceMap = new NativeParallelMultiHashMap<Entity, Entity>(items * 4, Allocator.TempJob);
                    GetCollectLaneConnectionDataReferencesJob(nodeChunks, referenceMap).Complete();
                }
            }
            else
            {
                referenceMap = new NativeParallelMultiHashMap<Entity, Entity>(2, Allocator.TempJob);
            }
            Logger.Info($"Node -> LaneConnection DataOwner map: {referenceMap.Count()}, capacity: {referenceMap.Capacity}");
            Profiler.EndSample();

            EntityQuery dataOwnerQuery = SystemAPI.QueryBuilder().WithAll<DataOwner>().WithNone<Deleted>().Build();
            NativeQueue<Entity> affectedEntities = new NativeQueue<Entity>(Allocator.TempJob);
            using (EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob))
            {
                JobHandle jobHandle = new ValidateLoadedReferencesJob()
                {
                    entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                    dataOwnerTypeHandle = SystemAPI.GetComponentTypeHandle<DataOwner>(true),
                    prefabRefTypeHandle = SystemAPI.GetComponentTypeHandle<PrefabRef>(true),
                    generatedConnectionsTypeHandle = SystemAPI.GetBufferTypeHandle<GeneratedConnection>(true),
                    modifiedConnectionsBuffer = SystemAPI.GetBufferLookup<ModifiedLaneConnections>(true),
                    fakePrefabEntity = Traffic.Systems.ModDefaultsSystem.FakePrefabRef,
                    entityInfoLookup = SystemAPI.GetEntityStorageInfoLookup(),
                    dataOwnerRefs = referenceMap.AsReadOnly(),
                    affectedEntities = affectedEntities.AsParallelWriter(),
                    commandBuffer = commandBuffer.AsParallelWriter(),
#if !SERIALIZATION
                }.ScheduleParallel(dataOwnerQuery, Dependency);
#else
                }.Schedule(dataOwnerQuery, Dependency);
#endif
                jobHandle.Complete();
                commandBuffer.Playback(EntityManager);
                Dependency = jobHandle;
            }
            referenceMap.Dispose();

            ModUISystem modUISystem = World.GetExistingSystemManaged<ModUISystem>();
            while (affectedEntities.TryDequeue(out Entity entity))
            {
                modUISystem.AddToAffectedIntersections(entity);
            }
            affectedEntities.Dispose();
            Profiler.EndSample();
        }

        private void MigrateToV1()
        {
            Logger.Info($"{nameof(TrafficDataMigrationSystem)} preparing migration job, data version: {_version}");
            using (NativeQueue<Entity> affectedEntities = new NativeQueue<Entity>(Allocator.TempJob))
            {
                using (EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob))
                {
                    JobHandle jobHandle = new FindIncompleteV1DataJob()
                    {
                        entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                        modifiedLaneConnectionsTypeHandle = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                        connectedEdgeTypeHandle = SystemAPI.GetBufferTypeHandle<ConnectedEdge>(true),
                        generatedConnectionBuffer = SystemAPI.GetBufferLookup<GeneratedConnection>(true),
                        netCompositionLaneBuffer = SystemAPI.GetBufferLookup<NetCompositionLane>(true),
                        edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                        deletedData = SystemAPI.GetComponentLookup<Deleted>(true),
                        compositionData = SystemAPI.GetComponentLookup<Composition>(true),
                        netCompositionData = SystemAPI.GetComponentLookup<NetCompositionData>(true),
                        roadCompositionData = SystemAPI.GetComponentLookup<RoadComposition>(true),
                        trackCompositionData = SystemAPI.GetComponentLookup<TrackComposition>(true),
                        invalidCarriagewayAndGroup = InvalidCarriagewayAndGroup,
                        affectedEntities = affectedEntities.AsParallelWriter(),
                        commandBuffer = commandBuffer.AsParallelWriter(),
                    }.ScheduleParallel(_query, Dependency);
                    jobHandle.Complete();
                    commandBuffer.Playback(EntityManager);
                    Dependency = jobHandle;
                }

                ModUISystem modUISystem = World.GetExistingSystemManaged<ModUISystem>();
                while (affectedEntities.TryDequeue(out Entity entity))
                {
                    modUISystem.AddToAffectedIntersections(entity);
                }
            }
        }

        private void MigrateToV2()
        {
            Logger.Info($"{nameof(TrafficDataMigrationSystem)} preparing migration job, data version: {_version}");
            using (NativeQueue<Entity> affectedEntities = new NativeQueue<Entity>(Allocator.TempJob))
            {
                using (EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob))
                {
                    JobHandle jobHandle = new FindIncompleteV2DataJob()
                    {
                        entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                        modifiedLaneConnectionsTypeHandle = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                        connectedEdgeTypeHandle = SystemAPI.GetBufferTypeHandle<ConnectedEdge>(true),
                        generatedConnectionBuffer = SystemAPI.GetBufferLookup<GeneratedConnection>(true),
                        netCompositionLaneBuffer = SystemAPI.GetBufferLookup<NetCompositionLane>(true),
                        edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                        deletedData = SystemAPI.GetComponentLookup<Deleted>(true),
                        dataOwnerData = SystemAPI.GetComponentLookup<DataOwner>(true),
                        compositionData = SystemAPI.GetComponentLookup<Composition>(true),
                        netCompositionData = SystemAPI.GetComponentLookup<NetCompositionData>(true),
                        roadCompositionData = SystemAPI.GetComponentLookup<RoadComposition>(true),
                        trackCompositionData = SystemAPI.GetComponentLookup<TrackComposition>(true),
                        affectedEntities = affectedEntities.AsParallelWriter(),
                        commandBuffer = commandBuffer.AsParallelWriter(),
#if !SERIALIZATION
                    }.ScheduleParallel(_query, Dependency);
#else
                    }.Schedule(_query, Dependency);
#endif
                    jobHandle.Complete();
                    commandBuffer.Playback(EntityManager);
                    Dependency = jobHandle;
                }

                ModUISystem modUISystem = World.GetExistingSystemManaged<ModUISystem>();
                while (affectedEntities.TryDequeue(out Entity entity))
                {
                    modUISystem.AddToAffectedIntersections(entity);
                }
            }
        }

        private int CountIntersections()
        {
            int allIntersections = 0;
            using (NativeArray<ArchetypeChunk> chunks = _query.ToArchetypeChunkArray(Allocator.Temp))
            {
                BufferTypeHandle<ModifiedLaneConnections> modifiedBufferHandle = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true);
                foreach (ArchetypeChunk chunk in chunks)
                {
                    BufferAccessor<ModifiedLaneConnections> accessor = chunk.GetBufferAccessor(ref modifiedBufferHandle);
                    for (int j = 0; j < accessor.Length; j++)
                    {
                        DynamicBuffer<ModifiedLaneConnections> connections = accessor[j];
                        if (!connections.IsEmpty)
                        {
                            allIntersections++;
                        }
                    }
                }
            }
            return allIntersections;
        }

        /// <summary>
        /// Collect LaneConnection -> Node(owner) references, in normal conditions lane connection can have only one owner
        /// </summary>
        /// <param name="chunks"></param>
        /// <param name="referenceMap"></param>
        /// <returns></returns>
        private JobHandle GetCollectLaneConnectionDataReferencesJob(NativeArray<ArchetypeChunk> chunks, NativeParallelMultiHashMap<Entity, Entity> referenceMap)
        {
            JobHandle job = new JobHandle();
            if (chunks.Length > 0)
            {
                job = new CollectLaneConnectionDataOwnersJob()
                {
                    entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                    laneConnectionsTypeHandle = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                    chunks = chunks,
                    referenceMap = referenceMap.AsParallelWriter(),
                }.ScheduleParallel(chunks.Length, 4, Dependency);
            }
            return job;
        }

        /// <summary>
        /// Collect Nodes that reference entity with DataOwner component (in most cases ModifiedLaneConnection data)
        /// </summary>
        /// <param name="chunks"></param>
        /// <param name="referenceMap"></param>
        /// <returns></returns>
        private JobHandle GetCollectDataOwnerReferencesJob(NativeArray<ArchetypeChunk> chunks, NativeParallelMultiHashMap<Entity, Entity> referenceMap)
        {
            JobHandle job = new JobHandle();
            if (chunks.Length > 0)
            {
                job = new CollectDataOwnersJob()
                {
                    entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                    dataOwnerTypeHandle = SystemAPI.GetComponentTypeHandle<DataOwner>(true),
                    laneConnectionsBuffer = SystemAPI.GetBufferLookup<ModifiedLaneConnections>(true),
                    chunks = chunks,
                    referenceMap = referenceMap.AsParallelWriter(),
                }.ScheduleParallel(chunks.Length, 4, Dependency);
            }
            return job;
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            _loaded = true;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(DataMigrationVersion.PriorityManagementDataV1);
            Logger.Serialization($"Saving ({nameof(TrafficDataMigrationSystem)} data version: {DataMigrationVersion.PriorityManagementDataV1}");
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out _version);
            Logger.Serialization($"Deserialized {nameof(TrafficDataMigrationSystem)} data version: {_version}");
        }

        public void SetDefaults(Context context)
        {
            _version = 0;
        }

#if WITH_BURST
        [BurstCompile]
#endif
        private struct CollectLaneConnectionDataOwnersJob : IJobFor
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public BufferTypeHandle<ModifiedLaneConnections> laneConnectionsTypeHandle;
            [ReadOnly] public NativeArray<ArchetypeChunk> chunks;
            [WriteOnly] public NativeParallelMultiHashMap<Entity, Entity>.ParallelWriter referenceMap;

            public void Execute(int index)
            {
                ArchetypeChunk chunk = chunks[index];
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                BufferAccessor<ModifiedLaneConnections> connectionsAccessor = chunk.GetBufferAccessor(ref laneConnectionsTypeHandle);
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    DynamicBuffer<ModifiedLaneConnections> connections = connectionsAccessor[i];
                    foreach (ModifiedLaneConnections connection in connections)
                    {
                        if (connection.modifiedConnections != Entity.Null)
                        {
                            referenceMap.Add(connection.modifiedConnections, entity);
                        }
                    }
                }
            }
        }

#if WITH_BURST
        [BurstCompile]
#endif
        private struct CollectDataOwnersJob : IJobFor
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<DataOwner> dataOwnerTypeHandle;
            [ReadOnly] public BufferLookup<ModifiedLaneConnections> laneConnectionsBuffer;
            [ReadOnly] public NativeArray<ArchetypeChunk> chunks;
            [WriteOnly] public NativeParallelMultiHashMap<Entity, Entity>.ParallelWriter referenceMap;

            public void Execute(int index)
            {
                ArchetypeChunk chunk = chunks[index];
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                NativeArray<DataOwner> owners = chunk.GetNativeArray(ref dataOwnerTypeHandle);
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    DataOwner owner = owners[i];
                    if (owner.entity != Entity.Null && 
                        laneConnectionsBuffer.TryGetBuffer(owner.entity, out DynamicBuffer<ModifiedLaneConnections> connections))
                    {
                        foreach (ModifiedLaneConnections connection in connections)
                        {
                            if (connection.modifiedConnections == entity)
                            {
                                referenceMap.Add(owner.entity, entity);
                            }
                        }
                    }
                }
            }
        }
    }
}
