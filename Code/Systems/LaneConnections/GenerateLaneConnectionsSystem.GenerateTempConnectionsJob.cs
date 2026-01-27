using Game.Tools;
using Traffic.Components.LaneConnections;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

namespace Traffic.Systems.LaneConnections
{
    public partial class GenerateLaneConnectionsSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct GenerateTempConnectionsJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<CreationDefinition> creationDefinitionTypeHandle;
            [ReadOnly] public ComponentTypeHandle<ConnectionDefinition> connectionDefinitionTypeHandle;
            [ReadOnly] public BufferTypeHandle<TempLaneConnection> tempConnectionBufferTypeHandle;
            [ReadOnly] public NativeParallelHashMap<Entity, Entity>.ReadOnly tempEntityMap;

            public NativeParallelHashSet<Entity> createdModifiedLaneConnections;
            public NativeParallelMultiHashMap<Entity, TempModifiedConnections> createdModifiedConnections;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<CreationDefinition> definitions = chunk.GetNativeArray(ref creationDefinitionTypeHandle);
                NativeArray<ConnectionDefinition> connectionDefinitions = chunk.GetNativeArray(ref connectionDefinitionTypeHandle);
                BufferAccessor<TempLaneConnection> tempConnectionsAccessor = chunk.GetBufferAccessor(ref tempConnectionBufferTypeHandle);

                NativeList<GeneratedConnection> tempConnections = new NativeList<GeneratedConnection>(8, Allocator.Temp);
                for (int i = 0; i < definitions.Length; i++)
                {
                    CreationDefinition definition = definitions[i];
                    ConnectionDefinition connectionDefinition = connectionDefinitions[i];
                    tempConnections.Clear();
                    if (tempEntityMap.TryGetValue(definition.m_Original, out Entity tempNodeEntity))
                    {
                        Entity sourceEdgeEntity = connectionDefinition.edge;
                        if (tempEntityMap.TryGetValue(connectionDefinition.edge, out Entity edgeEntity))
                        {
                            Logger.Debug($"Found original source edge Entity: {edgeEntity} definitionEntity[{connectionDefinition.edge}]");
                            sourceEdgeEntity = edgeEntity;
                        } 
                        if (tempConnectionsAccessor.Length > 0)
                        {
                            DynamicBuffer<TempLaneConnection> tempLaneConnections = tempConnectionsAccessor[i];
                            for (int j = 0; j < tempLaneConnections.Length; j++)
                            {
                                if (tempEntityMap.TryGetValue(tempLaneConnections[j].targetEntity, out Entity targetEdgeEntity))
                                {
                                    tempConnections.Add(new GeneratedConnection
                                    {
                                        sourceEntity = sourceEdgeEntity,
                                        targetEntity = targetEdgeEntity,
                                        laneIndexMap = tempLaneConnections[j].laneIndexMap,
                                        lanePositionMap = tempLaneConnections[j].lanePositionMap,
                                        carriagewayAndGroupIndexMap = tempLaneConnections[j].carriagewayAndGroupIndexMap,
                                        method = tempLaneConnections[j].method,
                                        isUnsafe = tempLaneConnections[j].isUnsafe,
#if DEBUG_GIZMO
                                        debug_bezier = tempLaneConnections[j].bezier,
#endif
                                    });
                                }
                            }
                        }
#if DEBUG_CONNECTIONS
                        Logger.Debug($"Create modified connection: {tempNodeEntity} source: {sourceEdgeEntity}");
                        Logger.Debug($"Temp Connections ({tempConnections.Length}):");
                        for (var k = 0; k < tempConnections.Length; k++)
                        {
                            Logger.Debug($"[{k}] {tempConnections[k].ToString()}");
                        }
                        Logger.Debug("");
#endif
                        createdModifiedConnections.Add(tempNodeEntity, new TempModifiedConnections
                        {
                            dataOwner = tempNodeEntity,
                            owner = connectionDefinition.owner,
                            flags = connectionDefinition.owner != Entity.Null 
                                ? ((connectionDefinition.flags & ConnectionFlags.Remove) != 0 ? TempFlags.Delete : TempFlags.Modify) 
                                : TempFlags.Create,
                            edgeEntity = sourceEdgeEntity,
                            laneIndex = connectionDefinition.laneIndex,
                            carriagewayAndGroup = connectionDefinition.carriagewayAndGroup,
                            lanePosition = connectionDefinition.lanePosition,
                            generatedConnections = tempConnections.ToArray(Allocator.TempJob)
                        });
                        createdModifiedLaneConnections.Add(tempNodeEntity);
                    }
                }
                tempConnections.Dispose();
            }
        }
    }
}
