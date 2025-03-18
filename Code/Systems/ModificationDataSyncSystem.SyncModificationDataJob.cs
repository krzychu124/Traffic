using Game.Common;
using Traffic.Components.LaneConnections;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

namespace Traffic.Systems
{
    public partial class ModificationDataSyncSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct SyncModificationDataJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public BufferTypeHandle<ModifiedLaneConnections> modifiedLaneConnectionsType;
            public EntityCommandBuffer.ParallelWriter commandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                BufferAccessor<ModifiedLaneConnections> modifiedConnectionsBuffer = chunk.GetBufferAccessor(ref modifiedLaneConnectionsType);
                ChunkEntityEnumerator enumerator = new ChunkEntityEnumerator();
                while (enumerator.NextEntityIndex(out int i))
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
