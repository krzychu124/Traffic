using Colossal.Collections;
using Game.Common;
using Game.Tools;
using Traffic.Components;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

namespace Traffic.Tools
{
    public partial class TrafficToolClearSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct ClearEntitiesJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentTypeHandle<DataOwner> dataOwnerType;
            [ReadOnly] public ComponentTypeHandle<DataTemp> dataTempType;
            [ReadOnly] public ComponentLookup<DataOwner> dataOwnerData;
            [ReadOnly] public ComponentLookup<Temp> tempData;
            public EntityCommandBuffer.ParallelWriter commandBuffer;
            
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                NativeArray<DataOwner> owners = chunk.GetNativeArray(ref dataOwnerType);
                NativeArray<DataTemp> dataTemps = chunk.GetNativeArray(ref dataTempType);
                StackList<Entity> result = stackalloc Entity[chunk.Count];
                for (int index = 0; index < entities.Length; index++)
                {
                    Entity entity = entities[index];
                    Entity owner = owners[index].entity;
                    Entity tempOriginal = dataTemps[index].original;
                    bool noTemp = !tempData.HasComponent(owner);
                    bool noDataTemp = !dataOwnerData.HasComponent(tempOriginal);
                    if (owner == Entity.Null || noTemp || noDataTemp)
                    {
                        Logger.DebugConnections($"Deleting DataTemp modifiedConnection {entity}, owner: {owner}, noTemp: {noTemp} noDataTemp: {noDataTemp}");
                        result.AddNoResize(entity);
                    }
                }
                if (result.Length != 0)
                {
                    commandBuffer.AddComponent<Deleted>(unfilteredChunkIndex, result.AsArray());
                }
            }
        }
    }
}
