using Unity.Entities;

namespace Traffic.Components.PrioritySigns
{
    [InternalBufferCapacity(0)]
    public struct PriorityHandle : IBufferElementData
    {
        public Entity laneHandle;
        public Entity edge;
        public bool isEnd;
    }
}
