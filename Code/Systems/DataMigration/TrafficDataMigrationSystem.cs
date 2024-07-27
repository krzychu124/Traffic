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
using Edge = Game.Net.Edge;

namespace Traffic.Systems.DataMigration
{
#if WITH_BURST
    [BurstCompile]
#endif
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

            if (_version >= DataMigrationVersion.LaneConnectionDataUpgradeV2)
            {
                Logger.Info($"{nameof(TrafficDataMigrationSystem)} migration not needed, data version: {_version}");
                return;
            }

            int allIntersections = 0;
            int count = 0;
            
            NativeArray<ArchetypeChunk> chunks = _query.ToArchetypeChunkArray(Allocator.Temp);
            BufferTypeHandle<ModifiedLaneConnections> modifiedBufferHandle = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true);
            for (var i = 0; i < chunks.Length; i++)
            {
                ArchetypeChunk chunk = chunks[i];
                BufferAccessor<ModifiedLaneConnections> accessor = chunk.GetBufferAccessor(ref modifiedBufferHandle);
                for (var j = 0; j < accessor.Length; j++)
                {
                    DynamicBuffer<ModifiedLaneConnections> connections = accessor[j];
                    if (!connections.IsEmpty)
                    {
                        allIntersections++;
                    }
                }
            }
            chunks.Dispose();
            
            if (_version < DataMigrationVersion.LaneConnectionDataUpgradeV1)
            {
                Logger.Info($"{nameof(TrafficDataMigrationSystem)} preparing migration job, data version: {_version}");
                NativeQueue<Entity> affectedEntities = new NativeQueue<Entity>(Allocator.TempJob);
                EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
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
                commandBuffer.Dispose();
                Dependency = jobHandle;

                count = affectedEntities.Count;
                ModUISystem modUISystem = World.GetExistingSystemManaged<ModUISystem>();
                while (affectedEntities.TryDequeue(out Entity entity))
                {
                    modUISystem.AddToAffectedIntersections(entity);
                }
                affectedEntities.Dispose();
            }
            else
            {
                Logger.Info($"{nameof(TrafficDataMigrationSystem)} preparing migration job, data version: {_version}");
                NativeQueue<Entity> affectedEntities = new NativeQueue<Entity>(Allocator.TempJob);
                EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
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
                }.ScheduleParallel(_query, Dependency);
                jobHandle.Complete();
                commandBuffer.Playback(EntityManager);
                commandBuffer.Dispose();
                Dependency = jobHandle;

                count = affectedEntities.Count;
                ModUISystem modUISystem = World.GetExistingSystemManaged<ModUISystem>();
                while (affectedEntities.TryDequeue(out Entity entity))
                {
                    modUISystem.AddToAffectedIntersections(entity);
                }
                affectedEntities.Dispose();
            }
           
            if (count > 0)
            {
                GameManager.instance.userInterface.appBindings.ShowMessageDialog(
                    new MessageDialog("Traffic mod data loading",
                        $"**Traffic** mod couldn't load data from {count} of {allIntersections} intersections.\n\n" +
                        "Use **Traffic's Lane Connector tool** to open loading results dialog for more information.",
                        LocalizedString.Id("Common.ERROR_DIALOG_CONTINUE")), null);
            }
            
            Logger.Info($"{nameof(TrafficDataMigrationSystem)} migrating data version {_version} done. Found {count} affected nodes of {allIntersections}");
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            _loaded = true;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(DataMigrationVersion.LaneConnectionDataUpgradeV2);
            Logger.Serialization($"Saving ({nameof(TrafficDataMigrationSystem)} data version: {DataMigrationVersion.LaneConnectionDataUpgradeV2}");
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
    }
}
