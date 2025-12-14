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
        TrackGroup = Train | Tram | Subway,
        Bike = 1 << 4,
        SharedCarBike = Car | Bike,
        Highway = 1 << 5,
        All = Car | Train | Tram | Subway | Bike
    }
}
