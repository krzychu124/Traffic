using Colossal.Mathematics;
using Game.Common;
using Unity.Mathematics;

namespace Traffic.CommonData
{
    public struct CustomRaycastInput
    {
        public Line3.Segment line;
        public float3 offset;
        public float heightOverride;
        public float fovTan;
        public TypeMask typeMask;
        public VehicleGroup vehicleGroup;
        public ConnectionType connectionType;
        public ConnectorType connectorType;
    }
}
