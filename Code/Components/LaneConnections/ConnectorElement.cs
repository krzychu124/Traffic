using Unity.Entities;

namespace Traffic.Components.LaneConnections
{
    [InternalBufferCapacity(0)]
    public struct ConnectorElement : IBufferElementData
    {
        public Entity entity;
    }
}
