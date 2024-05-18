using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Game.Rendering;
using Traffic.Components.PrioritySigns;
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
        private struct UpdateLaneHandleSearchTree : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentTypeHandle<Deleted> deletedType;
            [ReadOnly] public ComponentTypeHandle<Updated> updatedType;
            [ReadOnly] public ComponentTypeHandle<LaneHandle> laneHandleType;
            public NativeQuadTree<Entity, QuadTreeBoundsXZ> searchTree;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                if (chunk.Has(ref deletedType))
                {
                    Logger.DebugTool($"Deleted Lane Handles: {entities.Length}");
                    foreach (Entity entity in entities)
                    {
                        searchTree.TryRemove(entity);
                    }
                    return;
                }
                if (chunk.Has(ref updatedType))
                {
                    NativeArray<LaneHandle> connectors = chunk.GetNativeArray(ref laneHandleType);
                    Logger.DebugTool($"Created/updated Lane Handles: {entities.Length}");
                    for (int index = 0; index < entities.Length; index++)
                    {
                        Entity entity = entities[index];
                        LaneHandle laneHandle = connectors[index];
                        int lod = RenderingUtils.CalculateLodLimit(RenderingUtils.GetRenderingSize(new float2(1f)));
                        searchTree.Add(entity, new QuadTreeBoundsXZ(MathUtils.Bounds(laneHandle.curve), BoundsMask.NormalLayers, lod));
                    }
                }
            }
        }
    }
}
