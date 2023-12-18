using Game.Net;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.LaneConnections
{
    [InternalBufferCapacity(0)]
    public struct Connection : IBufferElementData
    {
        public PathNode sourceNode;
        public PathNode targetNode;
        public PathNode ownerNode;
        public Entity curve;
        public PathMethod method;
        public bool isUnsafe;
        public bool isForbidden;

        public Connection(PathNode start, PathNode end, PathNode owner, Entity curveOwner, PathMethod pathMethod, bool isUnsafe, bool isForbidden) {
            sourceNode = start;
            targetNode = end;
            ownerNode = owner;
            curve = curveOwner;
            method = pathMethod;
            this.isUnsafe = isUnsafe;
            this.isForbidden = isForbidden;
        }
        
        public Connection(Lane laneData, Entity curveOwner, PathMethod pathMethod, bool isUnsafe, bool isForbidden) {
            sourceNode = laneData.m_StartNode;
            targetNode = laneData.m_EndNode;
            ownerNode = laneData.m_MiddleNode;
            curve = curveOwner;
            method = pathMethod;
            this.isUnsafe = isUnsafe;
            this.isForbidden = isForbidden;
        }
    }
}
