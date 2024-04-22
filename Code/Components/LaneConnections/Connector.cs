using Traffic.CommonData;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.Components.LaneConnections
{
    public struct Connector : IComponentData
    {
        public Entity edge;
        public Entity node;
        public int laneIndex;
        public float3 position;
        public float3 direction;
        public VehicleGroup vehicleGroup;
        public ConnectorType connectorType;
        public ConnectionType connectionType;
    }
}
