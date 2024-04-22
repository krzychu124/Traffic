using Unity.Entities;

namespace Traffic.Components.LaneConnections
{
    [InternalBufferCapacity(0)]
    public struct LaneConnection : IBufferElementData
    {
        public Entity connection;
    }
}
