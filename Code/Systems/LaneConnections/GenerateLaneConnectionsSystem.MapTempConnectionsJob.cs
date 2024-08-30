using Game.Prefabs;
using Game.Tools;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.Systems.LaneConnections
{
    public partial class GenerateLaneConnectionsSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct MapTempConnectionsJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeParallelMultiHashMap<Entity, TempModifiedConnections> createdModifiedConnections;
            [ReadOnly] public BufferLookup<ModifiedLaneConnections> modifiedConnectionsBuffer;
            [ReadOnly] public NativeList<Entity> keys;
            [ReadOnly] public Entity fakePrefabRef;
            public NativeParallelHashSet<Entity> processedEntities;
            public EntityCommandBuffer.ParallelWriter commandBuffer;

            public void Execute(int index) {
                Entity nodeEntity = keys[index];
                Logger.DebugConnections($"Checking node: {nodeEntity}");
                if (processedEntities.Contains(nodeEntity))
                {
                    Logger.DebugConnections($"Oh, for some reason already processed!!! {nodeEntity}");
                    return;
                }
                if (!processedEntities.Add(nodeEntity))
                {
                    Logger.DebugConnections($"Adding entity {nodeEntity} to processed entities failed!!");
                }
                if (createdModifiedConnections.TryGetFirstValue(nodeEntity, out TempModifiedConnections item, out NativeParallelMultiHashMapIterator<Entity> iterator))
                {
#if DEBUG_CONNECTIONS
                    int valueCount = createdModifiedConnections.CountValuesForKey(nodeEntity);
#endif
                    DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections;
                    if (!modifiedConnectionsBuffer.HasBuffer(nodeEntity))
                    {
#if DEBUG_CONNECTIONS
                        Logger.DebugConnections($"No buffer in: {nodeEntity} ({valueCount})");
#endif
                        modifiedLaneConnections = commandBuffer.AddBuffer<ModifiedLaneConnections>(index, nodeEntity);
                    }
                    else
                    {
#if DEBUG_CONNECTIONS
                        Logger.DebugConnections($"Has buffer in: {nodeEntity} ({valueCount})");
#endif
                        modifiedLaneConnections = commandBuffer.SetBuffer<ModifiedLaneConnections>(index, nodeEntity);
                    }
                    
                    do
                    {
                        Entity modifiedConnectionEntity = commandBuffer.CreateEntity(index);
                        commandBuffer.AddComponent<DataTemp>(index, modifiedConnectionEntity, new DataTemp(item.owner, item.flags));
                        commandBuffer.AddComponent<DataOwner>(index, modifiedConnectionEntity, new DataOwner(item.dataOwner));
                        commandBuffer.AddComponent<CustomLaneConnection>(index, modifiedConnectionEntity);
                        commandBuffer.AddComponent<PrefabRef>(index, modifiedConnectionEntity, new PrefabRef(fakePrefabRef));
#if DEBUG_CONNECTIONS
                        int length = 0;
#endif
                        if (item.generatedConnections.IsCreated)
                        {
                            DynamicBuffer<GeneratedConnection> generatedConnections = commandBuffer.AddBuffer<GeneratedConnection>(index, modifiedConnectionEntity);
#if DEBUG_CONNECTIONS
                            length = item.generatedConnections.Length;
#endif
                            generatedConnections.CopyFrom(item.generatedConnections);
                            item.generatedConnections.Dispose();
                        }
                        modifiedLaneConnections.Add(new ModifiedLaneConnections()
                        {
                            edgeEntity = item.edgeEntity,
                            laneIndex = item.laneIndex,
                            lanePosition = item.lanePosition,
                            carriagewayAndGroup = item.carriagewayAndGroup,
                            modifiedConnections = modifiedConnectionEntity,
                        });
#if DEBUG_CONNECTIONS
                        Logger.DebugConnections($"Added modified connection to {nodeEntity}: {modifiedConnectionEntity}, e: {item.edgeEntity} i: {item.laneIndex}, connections: {length}");
#endif
                    } while (createdModifiedConnections.TryGetNextValue(out item, ref iterator));
                }
            }
        }
    }
}
