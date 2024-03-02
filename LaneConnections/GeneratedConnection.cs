using Colossal.Mathematics;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.LaneConnections
{
    [InternalBufferCapacity(0)]
    public struct GeneratedConnection : IBufferElementData
    {
        public Entity sourceEntity;
        public Entity targetEntity;
        public int2 laneIndexMap;
        public PathMethod method;
        public bool isUnsafe;

#if DEBUG_GIZMO
        public Bezier4x3 debug_bezier;
#endif

        public override string ToString() {
            return $"s: {sourceEntity} t: {targetEntity} l: {laneIndexMap}, m: {method} u: {isUnsafe}";
        }
    }
}
