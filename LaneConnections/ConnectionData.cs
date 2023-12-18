using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.LaneConnections
{
    public struct ConnectionData : IComponentData
    {
        public PathNode sourceNode;
        public PathNode targetNode;
        public PathNode ownerNode;
        public Entity sourceEdge;
        public Entity targetEdge;
        public int2 laneIndexMap;
        public Entity curve;
        public PathMethod method;
        public bool isUnsafe;
        public bool isForbidden;

        public ConnectionData(Connection connection, Entity sourceEdge, Entity targetEdge, int2 indexMap) {
            sourceNode = connection.sourceNode;
            targetNode = connection.targetNode;
            ownerNode = connection.ownerNode;
            curve = connection.curve;
            method = connection.method;
            isUnsafe = connection.isUnsafe;
            isForbidden = connection.isForbidden;
            this.sourceEdge = sourceEdge;
            this.targetEdge = targetEdge;
            laneIndexMap = indexMap;
        }
    }
}
