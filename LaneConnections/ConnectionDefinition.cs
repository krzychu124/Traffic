using System;
using Unity.Entities;

namespace Traffic.LaneConnections
{
    public struct ConnectionDefinition : IComponentData
    {
        public Entity edge;
        public Entity connector;
        public Entity owner;
        public Entity node;
        public int laneIndex;
        public ConnectionFlags flags;
    }


    [Flags]
    public enum ConnectionFlags
    {
        Modify = 1 << 0,
        Create = 1 << 1,
        Remove = 1 << 2,
        Highlight = 1 << 3,
        Essential = 1 << 4,
    }
}
