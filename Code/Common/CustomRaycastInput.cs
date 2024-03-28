using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Traffic.LaneConnections;
using Unity.Mathematics;

namespace Traffic.Common
{
    public struct CustomRaycastInput
    {
        public Line3.Segment line;
        public float3 offset;
        public TypeMask typeMask;
        public VehicleGroup vehicleGroup;
        public ConnectionType connectionType;
        public ConnectorType connectorType;
    }
}
