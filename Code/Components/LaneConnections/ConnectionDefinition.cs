using System;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.Components.LaneConnections
{
    public struct ConnectionDefinition : IComponentData
    {
        public Entity edge;
#if DEBUG_CONNECTIONS                    
        public Entity connector;
#endif
        public Entity owner;
        public Entity node;
        public int laneIndex;
        public int2 carriagewayAndGroup;
        public float3 lanePosition;
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
