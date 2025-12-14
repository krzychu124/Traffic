using Game.Pathfind;
using Traffic.CommonData;
using Traffic.Tools;

namespace Traffic.Systems.Helpers
{
    public static class ConnectionUtils
    {
        public static bool CanConnect(VehicleGroup from, VehicleGroup to, bool strict)
        {
            return strict ? (from & to) == to : (from & to) != 0;
        }

        public static PathMethod CalculatePathMethod(VehicleGroup from, VehicleGroup to, LaneConnectorToolSystem.StateModifier modifierIgnoreUnsafe)
        {
            VehicleGroup shared = from & to;
            switch (shared)
            {
                case VehicleGroup.Bike:
                    return PathMethod.Bicycle;
                case VehicleGroup.Car:
                    return PathMethod.Road;
                case VehicleGroup.Tram:
                case VehicleGroup.Train:
                case VehicleGroup.Subway:
                    return PathMethod.Track;
                default:
                    bool forceRoad = (modifierIgnoreUnsafe & LaneConnectorToolSystem.StateModifier.RoadOnly) == LaneConnectorToolSystem.StateModifier.RoadOnly;
                    bool forceTrack = (modifierIgnoreUnsafe & LaneConnectorToolSystem.StateModifier.TrackOnly) == LaneConnectorToolSystem.StateModifier.TrackOnly;
                    PathMethod result = 0;
                    if ((shared & VehicleGroup.Bike) != 0 && !forceRoad)
                    {
                        result |= PathMethod.Bicycle;
                    }
                    if ((shared & VehicleGroup.Car) != 0 && !forceTrack) 
                    {
                        result |= PathMethod.Road;
                        
                    }
                    if ((shared & VehicleGroup.TrackGroup) != 0 && !forceRoad) 
                    {
                        result |= PathMethod.Track;
                        
                    }
                    return result;
            }
        }

        public static bool ForceUnsafe(VehicleGroup fromGroup, VehicleGroup toGroup)
        {
            VehicleGroup fromIgnoreTrack = fromGroup & ~VehicleGroup.TrackGroup;
            if (fromIgnoreTrack == VehicleGroup.None)
            {
                return false;
            }
            VehicleGroup toIgnoreTrack = toGroup & ~VehicleGroup.TrackGroup;
            return ((fromIgnoreTrack & VehicleGroup.SharedCarBike) == VehicleGroup.SharedCarBike && (toIgnoreTrack & (VehicleGroup.Bike | VehicleGroup.Car)) == VehicleGroup.Bike) ||
                (fromIgnoreTrack & (VehicleGroup.Bike | VehicleGroup.Car)) == VehicleGroup.Bike && (toIgnoreTrack & VehicleGroup.SharedCarBike) == VehicleGroup.SharedCarBike;
        }

        public static bool UnsafeAllowed(VehicleGroup group, LaneConnectorToolSystem.StateModifier modifier)
        {
            return (modifier == LaneConnectorToolSystem.StateModifier.RoadOnly && (group & (VehicleGroup.Bike | VehicleGroup.Car)) != 0) || 
                (group == (group & (VehicleGroup.Car | VehicleGroup.Bike | VehicleGroup.Highway)));
        }
    }
}
