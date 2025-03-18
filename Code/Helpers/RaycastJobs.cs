#if DEBUG
#define DEBUG_TOOL
#endif
using System.Runtime.CompilerServices;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Traffic.CommonData;
using Traffic.Components.LaneConnections;
using Traffic.Components.PrioritySigns;
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
        
#if WITH_BURST
        [BurstCompile]
#endif
        public struct FindLaneHandleFromTreeJob : IJob 
        {
            private struct FindLaneHandleIterator : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>, IUnsafeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
            {
                public float3 minOffset;
                public float3 maxOffset;
                public Line3.Segment line;
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

            [ReadOnly] public float expandFovTan;        
            [ReadOnly] public CustomRaycastInput input;
            [ReadOnly] public NativeQuadTree<Entity, QuadTreeBoundsXZ> searchTree;
            [WriteOnly] public NativeList<Entity> entityList;
        
            public void Execute() {
                float minLaneRadius = Game.Net.RaycastJobs.GetMinLaneRadius(expandFovTan, MathUtils.Length(input.line));
                FindLaneHandleIterator nodeIterator = new FindLaneHandleIterator
                {
                    line = input.line,
                    minOffset = math.min(-input.offset, 0f - minLaneRadius),
                    maxOffset = math.max(-input.offset, minLaneRadius),
                    entityList = entityList
                };
                searchTree.Iterate(ref nodeIterator);
            }
        }

#if WITH_BURST
        [BurstCompile]
#endif
        public struct RaycastLaneHandles : IJobParallelForDefer
        {
            [ReadOnly] public ComponentLookup<LaneHandle> laneHandleData;
            [ReadOnly] public NativeArray<Entity>.ReadOnly entities;
            [ReadOnly] public float fovTan;
            [ReadOnly] public CustomRaycastInput input;
            [WriteOnly] public NativeAccumulator<RaycastResult>.ParallelWriter results;

            public void Execute(int index) {
                Entity entity = entities[index];
                if (laneHandleData.TryGetComponent(entity, out LaneHandle laneHandle))
                {
                    float2 t;
                    float distance = MathUtils.Distance(laneHandle.curve, input.line, out t);
                    float3 position = MathUtils.Position(input.line, t.y);
                    
                    float cameraDistance = math.distance(position, input.line.a);
                    float minLaneRadius = GetMinLaneRadius(fovTan, cameraDistance);
                    minLaneRadius = math.max(minLaneRadius, 1.5f);
                    if (distance < minLaneRadius)
                    {
                        results.Accumulate(new RaycastResult()
                        {
                            m_Hit = new RaycastHit()
                            {
                                m_HitEntity = entity,
                                m_Position = MathUtils.Position(laneHandle.curve, t.x),
                                m_HitPosition = position,
                                m_CurvePosition = t.x,
                                m_NormalizedDistance = t.y - (minLaneRadius - distance) / math.max(1f, MathUtils.Length(input.line)),
                            },
                            m_Owner = entity,
                        });
                    }
                }
                else
                {
                    Logger.DebugTool($"FAIL: {entity}, {laneHandle.curve}");
                }
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static float GetMinLaneRadius(float fovTan, float cameraDistance)
            {
                return cameraDistance * fovTan * 0.01f;
            }
        }
    }
}
