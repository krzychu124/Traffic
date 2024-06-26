﻿using System;

namespace Traffic.CommonData
{
    [Flags]
    public enum ConnectionType
    {
        Strict = 1,
        Road = 1 << 1,
        Track = 1 << 2,
        Utility = 1 << 3, //not implemented yet
        SharedCarTrack = Road | Track,
        All = Road | Track/* | Utility*/,
    }
}
