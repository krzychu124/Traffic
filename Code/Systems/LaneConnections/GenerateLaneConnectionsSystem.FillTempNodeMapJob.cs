using Game.Net;
using Game.Tools;
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
        internal struct FillTempNodeMapJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Temp> tempTypeHandle;
            [ReadOnly] public BufferTypeHandle<ConnectedEdge> connectedEdgeTypeHandle;
            [ReadOnly] public ComponentLookup<Node> nodeData;
            [ReadOnly] public ComponentLookup<Temp> tempData;

            public NativeList<Entity>.ParallelWriter tempNodes;
            public NativeParallelHashMap<Entity, Entity> tempEntityMap;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                NativeArray<Temp> temps = chunk.GetNativeArray(ref tempTypeHandle);
                BufferAccessor<ConnectedEdge> connectedEdgeAccessor = chunk.GetBufferAccessor(ref connectedEdgeTypeHandle);

                Logger.DebugConnections($"Run FillTempNodeMap ({entities.Length})[{unfilteredChunkIndex}]");

                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    tempNodes.AddNoResize(entity);
#if DEBUG_CONNECTIONS
                    Temp temp = temps[i];
                    if (temp.m_Original != Entity.Null && nodeData.HasComponent(temp.m_Original))
                    {
                        //temp on node entity can split an edge -> edge entity will be set in m_Original + temp.m_CurvePosition will be larger than 0.0f
                        if (tempEntityMap.TryAdd(temp.m_Original, entity))
                        {
                            Logger.DebugConnections($"Cache node: {temp.m_Original} -> {entity} flags: {temp.m_Flags}");
                        }
                    }
                    else
                    {
                        Logger.DebugConnections($"Not a node: {temp.m_Original} -> {entity} flags: {temp.m_Flags}");
                    }
#else
                    Temp temp = temps[i];
                    if (temp.m_Original != Entity.Null && nodeData.HasComponent(temp.m_Original))
                    {
                        //temp on node entity can split an edge -> edge entity will be set in m_Original + temp.m_CurvePosition will be larger than 0.0f
                        tempEntityMap.TryAdd(temp.m_Original, entity);
                    }
#endif
                    if (connectedEdgeAccessor.Length > 0)
                    {
                        DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgeAccessor[i];
                        for (int j = 0; j < connectedEdges.Length; j++)
                        {
                            Entity edge = connectedEdges[j].m_Edge;
                            if (tempData.HasComponent(edge))
                            {
                                Temp tempEdge = tempData[edge];
                                if (tempEdge.m_Original != Entity.Null)
                                {
#if DEBUG_CONNECTIONS
                                    if (tempEntityMap.TryAdd(tempEdge.m_Original, edge))
                                    {
                                        Logger.DebugConnections($"Cache edge of ({temp.m_Original}): {tempEdge.m_Original} -> {edge} flags: {tempEdge.m_Flags}");
                                    }
#else
                                    tempEntityMap.TryAdd(tempEdge.m_Original, edge);
#endif
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
