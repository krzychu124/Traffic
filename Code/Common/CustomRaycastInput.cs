using Colossal.Mathematics;
using Game.Common;
using Traffic.LaneConnections;
using Unity.Mathematics;

namespace Traffic.Common
{
    public struct CustomRaycastInput
    {
        public Line3.Segment line;
        public float3 offset;
        public TypeMask typeMask;
        public ConnectionType connectionType;
        public ConnectorType connectorType;
    }
}