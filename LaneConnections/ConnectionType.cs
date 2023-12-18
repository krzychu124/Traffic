using System;

namespace Traffic.LaneConnections
{
    [Flags]
    public enum ConnectionType
    {
        Road = 1,
        Track = 1 << 1,
        Utility = 1 << 2,
        SharedCarTrack = Road | Track,
        All = Road | Track | Utility,
    }
}
