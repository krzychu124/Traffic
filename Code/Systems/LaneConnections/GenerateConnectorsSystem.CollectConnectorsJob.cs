using Traffic.CommonData;
using Traffic.Components.LaneConnections;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

namespace Traffic.Systems.LaneConnections
{
    public partial class GenerateConnectorsSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct CollectConnectorsJob : IJobChunk
        {
            
            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentTypeHandle<Connector> connectorType;
            public NativeParallelHashMap<NodeEdgeLaneKey,Entity> resultMap;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                NativeArray<Connector> connectors = chunk.GetNativeArray(ref connectorType);
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity e = entities[i];
                    Connector connector = connectors[i];
                    Logger.DebugConnections($"Add connector ({e}): [{connector.connectorType}], [{connector.connectionType}] [{connector.node}]({connector.edge}) index: {connector.laneIndex} group: {connector.vehicleGroup} pos: {connector.position} || lanePos: {connector.lanePosition}");
                    resultMap.Add(new NodeEdgeLaneKey(connector.node.Index, connector.edge.Index, connector.laneIndex), e);
                }
            }
        }
    }
}
