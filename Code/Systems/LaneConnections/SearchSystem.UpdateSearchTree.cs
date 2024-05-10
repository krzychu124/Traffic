using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Game.Rendering;
using Traffic.Components.LaneConnections;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.Systems.LaneConnections
{
    public partial class SearchSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct UpdateSearchTree : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentTypeHandle<Deleted> deletedType;
            [ReadOnly] public ComponentTypeHandle<Updated> updatedType;
            [ReadOnly] public ComponentTypeHandle<Connector> connectorType;
            public NativeQuadTree<Entity, QuadTreeBoundsXZ> searchTree;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                if (chunk.Has(ref deletedType))
                {
                    Logger.DebugTool($"Deleted Connectors: {entities.Length}");
                    foreach (Entity entity in entities)
                    {
                        searchTree.TryRemove(entity);
                    }
                    return;
                } 
                if (chunk.Has(ref updatedType))
                {
                    NativeArray<Connector> connectors = chunk.GetNativeArray(ref connectorType);
                    Logger.DebugTool($"Created/updated Connectors: {entities.Length}");
                    for (int index = 0; index < entities.Length; index++)
                    {
                        Entity entity = entities[index];
                        Connector connector = connectors[index];
                        int lod = RenderingUtils.CalculateLodLimit(RenderingUtils.GetRenderingSize(new float2(1f)));
                        searchTree.Add(entity, new QuadTreeBoundsXZ(new Bounds3(connector.position - .5f, connector.position + .5f), BoundsMask.NormalLayers, lod));
                    }
                }
            }
        }
    }
}
