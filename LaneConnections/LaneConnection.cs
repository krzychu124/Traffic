using Unity.Entities;

namespace Traffic.LaneConnections
{
    [InternalBufferCapacity(0)]
    public struct LaneConnection : IBufferElementData
    {
        public Entity connection;
    }
}
