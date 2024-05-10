#if DEBUG
#define DEBUG_TOOL
#endif
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Traffic.CommonData;
using Traffic.Components.LaneConnections;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Traffic.Helpers
{
#if WITH_BURST
    [BurstCompile]
#endif
    public static class RaycastJobs
    {
        /*TODO try measure performance with ParallelFor */
#if WITH_BURST
        [BurstCompile]
#endif
        public struct FindConnectionNodeFromTreeJob : IJob 
        {
            private struct FindConnectionNodeIterator : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>, IUnsafeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
            {
                public float radius;
                public float3 offset;
                public Line3.Segment line;
                public NativeList<Entity> entityList;
            
                public bool Intersect(QuadTreeBoundsXZ bounds) {
                    bounds.m_Bounds.min += -offset;
                    bounds.m_Bounds.max += offset;
                    return MathUtils.Intersect(bounds.m_Bounds, line, out float2 _);
                }

                public void Iterate(QuadTreeBoundsXZ bounds, Entity item) {
                    float3 center = MathUtils.Center(bounds.m_Bounds);
                    if (MathUtils.Intersect(new Sphere3(radius, center), line, out float2 _))
                    {
                        entityList.Add(in item);
                    }
                }
            }
        
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
                    radius = math.cmax(input.offset),
                    offset = new float3(input.offset * new float3(1,0,1)),
                    entityList = entityList
                };
                searchTree.Iterate(ref nodeIterator);
            }
        }
    

#if WITH_BURST
        [BurstCompile]
#endif
        public struct RaycastLaneConnectionSubObjects : IJobParallelForDefer
        {
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
                    (isStrict ? (connector.vehicleGroup & input.vehicleGroup) == input.vehicleGroup : (connector.vehicleGroup & input.vehicleGroup) != 0) &&
                    (isStrict ? (connector.connectionType & nonStrict) == nonStrict : (connector.connectionType & nonStrict) != 0))
                {
                    result.Value = new CustomRaycastResult()
                    {
                        hit = new RaycastHit() { },
                        owner = entity
                    };
                    Logger.DebugTool($"OK: {entity}, {connector.position}, i: {connector.connectionType} => {input.connectionType} ({(connector.connectionType & input.connectionType) != 0}) | {connector.connectorType} => {input.connectorType} ({(connector.connectorType & input.connectorType) != 0}) | [{connector.vehicleGroup} => {input.vehicleGroup}] | nonStr: {nonStrict}");
                }
                else
                {
                    Logger.DebugTool($"Fail: {entity}, {connector.position}, i: {connector.connectionType} => {input.connectionType} ({(connector.connectionType & input.connectionType) != 0}) | {connector.connectorType} => {input.connectorType} ({(connector.connectorType & input.connectorType) != 0}) | [{connector.vehicleGroup} => {input.vehicleGroup}] | nonStr: {nonStrict}");
                }
            }
        }
    }
}
