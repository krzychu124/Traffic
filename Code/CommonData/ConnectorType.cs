using System;

namespace Traffic.CommonData
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
