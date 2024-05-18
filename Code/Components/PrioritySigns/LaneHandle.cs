using Colossal.Mathematics;
using Game.Net;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.Components.PrioritySigns
{
    public struct LaneHandle : IComponentData
    {
        public Entity edge;
        public Entity node;
        public int3 laneIndex;
        public int handleGroup;
        public Bezier4x3 curve;
        public Segment laneSegment;
        public PriorityType priority;
        public PriorityType originalPriority;
        public float length;
    }
}
