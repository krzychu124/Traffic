using Colossal.Mathematics;
using Game.Net;
using Game.Pathfind;
using Unity.Entities;

namespace Traffic.Components.LaneConnections
{
    [InternalBufferCapacity(0)]
    public struct Connection : IBufferElementData
    {
        public PathNode sourceNode;
        public PathNode targetNode;
        public PathNode ownerNode;
        public Entity sourceEdge;
        public Entity targetEdge;
        public Bezier4x3 curve;
        public PathMethod method;
        public bool isUnsafe;
        public bool isForbidden;
        
        public Connection(Lane laneData, Bezier4x3 curve, PathMethod pathMethod, Entity sourceEdge, Entity targetEdge, bool isUnsafe, bool isForbidden) {
            sourceNode = laneData.m_StartNode;
            targetNode = laneData.m_EndNode;
            ownerNode = laneData.m_MiddleNode;
            method = pathMethod;
            this.curve = curve;
            this.sourceEdge = sourceEdge;
            this.targetEdge = targetEdge;
            this.isUnsafe = isUnsafe;
            this.isForbidden = isForbidden;
        }
    }
}
