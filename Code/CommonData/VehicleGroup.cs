using System;

namespace Traffic.CommonData
{
    
    [Flags]
    public enum VehicleGroup
    {
        None,
        Car = 1 << 0,
        Train = 1 << 1,
        Tram = 1 << 2,
        Subway = 1 << 3,
    }
}
