using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Traffic.LaneConnections;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.Systems
{
#if WITH_BURST
    [BurstCompile]
#endif
    public partial class ModificationDataSyncSystem : GameSystemBase
    {
        private ModificationBarrier4B _modificationBarrier;
        private EntityQuery _query;

        protected override void OnCreate() {
            base.OnCreate();
            _modificationBarrier = World.GetOrCreateSystemManaged<ModificationBarrier4B>();
            _query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ModifiedLaneConnections>(), ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<Deleted>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), }
            });
            RequireForUpdate(_query);
        }

        protected override void OnUpdate() {
            SyncModificationDataJob job = new SyncModificationDataJob()
            {
                entityType = SystemAPI.GetEntityTypeHandle(),
                tempType = SystemAPI.GetComponentTypeHandle<Temp>(true),
                deletedType = SystemAPI.GetComponentTypeHandle<Deleted>(true),
                modifiedLaneConnectionsType = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                commandBuffer = _modificationBarrier.CreateCommandBuffer().AsParallelWriter(),
            };
            JobHandle jobHandle = job.Schedule(_query, Dependency);
            _modificationBarrier.AddJobHandleForProducer(jobHandle);
            Dependency = jobHandle;
        }

#if WITH_BURST
        [BurstCompile]
#endif
        private struct SyncModificationDataJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentTypeHandle<Temp> tempType;
            [ReadOnly] public ComponentTypeHandle<Deleted> deletedType;
            [ReadOnly] public BufferTypeHandle<ModifiedLaneConnections> modifiedLaneConnectionsType;
            public EntityCommandBuffer.ParallelWriter commandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                if (chunk.Has(ref deletedType))
                {
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                    BufferAccessor<ModifiedLaneConnections> modifiedConnectionsBuffer = chunk.GetBufferAccessor(ref modifiedLaneConnectionsType);
                    if (chunk.Has(ref tempType))
                    {
                        Logger.Debug($"Removing Temp node connections (node count: {entities.Length})");
                    }
                    
                    for (var i = 0; i < entities.Length; i++)
                    {
                        var modifiedConnections = modifiedConnectionsBuffer[i];
                        Logger.Debug($"Removing node connections {entities[i]} count: ({modifiedConnections.Length})");
                        for (var j = 0; j < modifiedConnections.Length; j++)
                        {
                            ModifiedLaneConnections connections = modifiedConnections[j];
                            if (connections.modifiedConnections != Entity.Null)
                            {
                                Logger.Debug($"Removing generated connections from {entities[i]} [{j}]  -> {connections.modifiedConnections}");
                                commandBuffer.AddComponent<Deleted>(unfilteredChunkIndex, connections.modifiedConnections);
                            }
                        }
                    }
                }
            }
        }
    }
}
