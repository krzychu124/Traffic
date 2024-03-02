using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Traffic.Common;
using Traffic.LaneConnections;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Traffic.Helpers
{
    public static class RaycastJobs
    {
        /*TODO try measure performance with ParallelFor */
        public struct FindConnectionNodeFromTreeJob : IJob 
        {
            private struct FindConnectionNodeIterator : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>, IUnsafeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
            {
                public Line3.Segment line;
                public float3 minOffset;
                public float3 maxOffset;
                public NativeList<Entity> entityList;
            
                public bool Intersect(QuadTreeBoundsXZ bounds) {
                    bounds.m_Bounds.min += minOffset;
                    bounds.m_Bounds.max += maxOffset;
                    return MathUtils.Intersect(bounds.m_Bounds, line, out float2 _);
                }

                public void Iterate(QuadTreeBoundsXZ bounds, Entity item) {
                    bounds.m_Bounds.min += minOffset;
                    bounds.m_Bounds.max += maxOffset;
                    if (MathUtils.Intersect(bounds.m_Bounds, line, out float2 _))
                    {
                        entityList.Add(in item);
                    }
                }
            }
        
            [ReadOnly]
            public float expand;
            [ReadOnly]
            public CustomRaycastInput input;
            [ReadOnly]
            public NativeQuadTree<Entity, QuadTreeBoundsXZ> searchTree;
            [WriteOnly]
            public NativeList<Entity> entityList;
        
            public void Execute() {
                FindConnectionNodeIterator nodeIterator = new FindConnectionNodeIterator
                {
                    line = input.line,
                    minOffset = math.min(-input.offset, 0f - expand),
                    maxOffset = math.max(-input.offset, expand),
                    entityList = entityList
                };
                searchTree.Iterate(ref nodeIterator);
            }
        }
    

        public struct RaycastLaneConnectionSubObjects : IJobParallelForDefer
        {
            [ReadOnly] public NativeReference<RaycastResult>.ReadOnly terrainResult;
            [ReadOnly] public ComponentLookup<Connector> connectorData;
            [ReadOnly] public NativeArray<Entity>.ReadOnly entities;
            [ReadOnly] public CustomRaycastInput input;
            public NativeReference<CustomRaycastResult> result;
        
            public void Execute(int index) {
                Entity entity = entities[index];
                bool isStrict = (input.connectionType & ConnectionType.Strict) != 0;
                ConnectionType nonStrict = input.connectionType & ~ConnectionType.Strict;
                if (connectorData.TryGetComponent(entity, out Connector connector) &&
                    (connector.connectorType & input.connectorType) != 0 &&
                    (isStrict ? (connector.connectionType & nonStrict) == nonStrict : (connector.connectionType & nonStrict) != 0))
                {
                    result.Value = new CustomRaycastResult()
                    {
                        hit = new RaycastHit() { },
                        owner = entity
                    };
                    Logger.DebugTool($"OK: {entity}, {connector.position}, i: {connector.connectionType} => {input.connectionType} ({(connector.connectionType & input.connectionType) != 0}) | {connector.connectorType} => {input.connectorType} ({(connector.connectorType & input.connectorType) != 0}) nonStr: {nonStrict}");
                }
                else
                {
                    Logger.DebugTool($"Fail: {entity}, {connector.position}, i: {connector.connectionType} => {input.connectionType} ({(connector.connectionType & input.connectionType) != 0}) | {connector.connectorType} => {input.connectorType} ({(connector.connectorType & input.connectorType) != 0}) nonStr: {nonStrict}");
                }
            }
        }
    }
}
