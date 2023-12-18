using System;

namespace Traffic.LaneConnections
{
    [Flags]
    public enum ConnectorType
    {
        Source = 1,
        Target = 1 << 1,
        TwoWay = 1 << 2,
        All = Source | Target | TwoWay,
    }
}
