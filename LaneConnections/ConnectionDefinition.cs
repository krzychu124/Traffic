using Game.Pathfind;
using Unity.Entities;

namespace Traffic.LaneConnections
{
    public struct ConnectionDefinition : IComponentData
    {
        public Entity startEdge;
        public Entity startConnector;
        public Entity targetEdge;
        public Entity targetConnector;
        public Entity node;
        public PathMethod pathMethod;
    }
}
