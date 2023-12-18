using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.LaneConnections
{
    [InternalBufferCapacity(0)]
    public struct ForbiddenConnection : IBufferElementData
    {
        public Entity sourceEntity;
        public Entity targetEntity;
        public int2 laneIndexMap;
        public PathMethod method;
    }
}
