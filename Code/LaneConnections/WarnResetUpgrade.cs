using Unity.Entities;

namespace Traffic.LaneConnections
{
    [InternalBufferCapacity(0)]
    public struct WarnResetUpgrade : IBufferElementData
    {
        public Entity entity;
    }
}
