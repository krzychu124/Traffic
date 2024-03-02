using Unity.Entities;

namespace Traffic.LaneConnections
{
    [InternalBufferCapacity(0)]
    public struct ConnectorElement : IBufferElementData
    {
        public Entity entity;
    }
}
