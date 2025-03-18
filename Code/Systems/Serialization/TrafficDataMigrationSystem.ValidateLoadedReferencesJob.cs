using Game.Common;
using Game.Net;
using Game.Prefabs;
using Traffic.Components;
using Traffic.Components.LaneConnections;
#if WITH_BURST
using Unity.Burst;
#endif
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Edge = Game.Net.Edge;
using NetUtils = Traffic.Systems.Helpers.NetUtils;

namespace Traffic.Systems.Serialization
{
    public partial class TrafficDataMigrationSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct ValidateLoadedReferencesJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<DataOwner> dataOwnerTypeHandle;
            [ReadOnly] public ComponentTypeHandle<PrefabRef> prefabRefTypeHandle;
            [ReadOnly] public BufferTypeHandle<GeneratedConnection> generatedConnectionsTypeHandle;
            [ReadOnly] public BufferLookup<ModifiedLaneConnections> modifiedConnectionsBuffer;
            [ReadOnly] public NativeParallelMultiHashMap<Entity, Entity>.ReadOnly dataOwnerRefs;
            [ReadOnly] public EntityStorageInfoLookup entityInfoLookup;
            [ReadOnly] public Entity fakePrefabEntity;
            public NativeQueue<Entity>.ParallelWriter affectedEntities;
            public EntityCommandBuffer.ParallelWriter commandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                NativeArray<DataOwner> dataOwners = chunk.GetNativeArray(ref dataOwnerTypeHandle);
                NativeArray<PrefabRef> prefabRefs = chunk.GetNativeArray(ref prefabRefTypeHandle);
                BufferAccessor<GeneratedConnection> generatedConnectionsAccessor = chunk.GetBufferAccessor(ref generatedConnectionsTypeHandle);

                ChunkEntityEnumerator enumerator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                bool hasPrefab = chunk.Has(ref prefabRefTypeHandle);
                bool hasGenerated = chunk.Has(ref generatedConnectionsTypeHandle);

                if (hasPrefab && hasGenerated)
                {
                    Logger.Serialization($"({unfilteredChunkIndex}) Chunk Archetype OK, DataOwner + PrefabRef + GeneratedConnections, count: {entities.Length}");
                    while (enumerator.NextEntityIndex(out int index))
                    {
                        Entity entity = entities[index];
                        DataOwner dataOwner = dataOwners[index];
                        Logger.Serialization($"({unfilteredChunkIndex})[{entity}] DataOwner {dataOwner.entity} PrefabRef {prefabRefs[index].m_Prefab} GeneratedConnections {generatedConnectionsAccessor[index].Length}");
                        if (!ValidateModifiedLaneConnectionReference(unfilteredChunkIndex, entity, dataOwner, prefabRefs[index].m_Prefab, out bool wasReferenced) && wasReferenced)
                        {
                            affectedEntities.Enqueue(dataOwner.entity);
                        }
                    }
                    return;
                }

                NativeHashSet<Entity> brokenRefs = new NativeHashSet<Entity>(32, Allocator.Temp);
                if (hasPrefab)
                {
                    Logger.Serialization($"Chunk with partial data, Only DataOwner + PrefabRef, count {entities.Length}");
                    while (enumerator.NextEntityIndex(out int index))
                    {
                        Entity entity = entities[index];
                        DataOwner dataOwner = dataOwners[index];
                        Logger.Serialization($"[{entity}] PrefabRef {prefabRefs[index].m_Prefab} DataOwner {dataOwner.entity}");
                        if (!ValidateModifiedLaneConnectionReference(unfilteredChunkIndex, entity, dataOwner, prefabRefs[index].m_Prefab, out bool wasReferenced, true) && wasReferenced)
                        {
                            Logger.Serialization($"[{entity}] DataOwner {dataOwner.entity} -> Found broken reference");
                            brokenRefs.Add(dataOwner.entity);
                        }
                    }
                }
                else if (hasGenerated)
                {
                    Logger.Serialization($"Chunk with partial data, Only DataOwner + GeneratedConnections, count {entities.Length}");
                    while (enumerator.NextEntityIndex(out int index))
                    {
                        Entity entity = entities[index];
                        DataOwner dataOwner = dataOwners[index];
                        DynamicBuffer<GeneratedConnection> generatedConnections = generatedConnectionsAccessor[index];
                        Logger.Serialization($"[{entity}] GeneratedConnections({generatedConnections.Length}) DataOwner {dataOwner.entity}");
                        foreach (GeneratedConnection generatedConnection in generatedConnections)
                        {
                            Logger.Serialization($"[{entity}] GeneratedConnection: {generatedConnection}");
                        }
                        if (!ValidateModifiedLaneConnectionReference(unfilteredChunkIndex, entity, dataOwner, fakePrefabEntity, out bool wasReferenced, true) && wasReferenced)
                        {
                            Logger.Serialization($"[{entity}] DataOwner {dataOwner.entity} -> Found broken reference");
                            brokenRefs.Add(dataOwner.entity);
                        }
                    }
                }
                else
                {
                    Logger.Serialization($"({unfilteredChunkIndex}) Chunk with partial data, only DataOwner, count {entities.Length}");
                    NativeList<Entity> toRemove = new NativeList<Entity>(entities.Length, Allocator.Temp);
                    while (enumerator.NextEntityIndex(out int index))
                    {
                        Entity entity = entities[index];
                        DataOwner dataOwner = dataOwners[index];
                        Logger.Serialization($"({unfilteredChunkIndex})[{entity}] DataOwner {dataOwner.entity}");
                        if (!ValidateModifiedLaneConnectionReference(unfilteredChunkIndex, entity, dataOwner, fakePrefabEntity, out bool wasReferenced, true) && wasReferenced)
                        {
                            Logger.Serialization($"[{entity}] DataOwner {dataOwner.entity} -> Found broken reference");
                            brokenRefs.Add(dataOwner.entity);
                        }
                    }
                }

                if (!brokenRefs.IsEmpty)
                {
                    foreach (Entity brokenRef in brokenRefs)
                    {
                        affectedEntities.Enqueue(brokenRef);
                    }
                }
                brokenRefs.Dispose();
            }

            /// <summary>
            /// Validate references to connection entity
            /// </summary>
            /// <param name="jobIndex">Unity ECS job unfiltered chunk index</param>
            /// <param name="connectionEntity">Connection Entity</param>
            /// <param name="connectionOwner">DataOwner of checked connection</param>
            /// <param name="connectionPrefabEntity">Hardcoded fake connection prefab</param>
            /// <param name="wasReferenced">True when owner contains reference to this entity</param>
            /// <param name="forceRemove">Use true when entity should be removed anyways</param>
            /// <returns>True when valid, otherwise false</returns>
            private bool ValidateModifiedLaneConnectionReference(int jobIndex, Entity connectionEntity, DataOwner connectionOwner, Entity connectionPrefabEntity, out bool wasReferenced, bool forceRemove = false)
            {
                wasReferenced = false;
                bool remove = forceRemove;
                if (connectionOwner.entity != Entity.Null)
                {
                    Logger.Serialization($"({jobIndex})[{connectionEntity}] DataOwner {connectionOwner.entity} -> Checking reference");
                    if (!entityInfoLookup.Exists(connectionOwner.entity))
                    {
                        Logger.Serialization($"({jobIndex})[{connectionEntity}] DataOwner {connectionOwner.entity} -> Referenced entity does not exist! Removing entity");
                        remove = true;
                    }
                    else if (!modifiedConnectionsBuffer.HasBuffer(connectionOwner.entity))
                    {
                        Logger.Serialization($"({jobIndex})[{connectionEntity}] DataOwner {connectionOwner.entity} -> Referenced entity does not have ModifiedLaneConnections buffer! Removing entity");
                        remove = true;
                    }
                    else if (NetUtils.IsReferencedByModifiedLaneConnectionItem(connectionEntity, modifiedConnectionsBuffer[connectionOwner.entity]))
                    {
                        wasReferenced = true;
                    }
                    else
                    {
                        Logger.Serialization($"({jobIndex})[{connectionEntity}] DataOwner {connectionOwner.entity} -> LaneConnection is missing in referenced entity ModifiedLaneConnections buffer! Removing entity");
                        remove = true;
                    }
                    if (!remove && connectionPrefabEntity != fakePrefabEntity)
                    {
                        Logger.Serialization($"({jobIndex})[{connectionEntity}] DataOwner {connectionOwner.entity} -> Incorrect prefabRef, updating {connectionPrefabEntity} -> {fakePrefabEntity}");
                        commandBuffer.SetComponent(jobIndex, connectionEntity, new PrefabRef(fakePrefabEntity));
                    }
                }
                else
                {
                    Logger.Serialization($"({jobIndex})[{connectionEntity}] DataOwner {connectionOwner.entity} -> referenced entity is Null! Removing entity");
                    remove = true;
                }

                if (remove)
                {
                    commandBuffer.AddComponent<Deleted>(jobIndex, connectionEntity);

                    if (dataOwnerRefs.TryGetFirstValue(connectionEntity, out Entity ownerEntity, out NativeParallelMultiHashMapIterator<Entity> it))
                    {
                        do
                        {
                            if (connectionOwner.entity == ownerEntity)
                            {
                                Logger.Serialization($"({jobIndex})[{connectionEntity}] DataOwner {connectionOwner.entity} -> Found the owner!");
                            }
                            else
                            {
                                Logger.Serialization($"({jobIndex})[{connectionEntity}] DataOwner {connectionOwner.entity} -> Found other owner: {ownerEntity}");
                            }
                        } while (dataOwnerRefs.TryGetNextValue(out ownerEntity, ref it));
                    }
                }

                return !remove;
            }
        }
    }
}
