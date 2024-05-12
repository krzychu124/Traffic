using Colossal.Mathematics;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.Components.LaneConnections
{
    [InternalBufferCapacity(0)]
    public struct TempLaneConnection : IBufferElementData
    {
        public Entity sourceEntity;
        public Entity targetEntity;
        public int2 laneIndexMap;
        public int4 carriagewayAndGroupIndexMap;
        public float3x2 lanePositionMap;
        public PathMethod method;
        public Bezier4x3 bezier;
        public bool isUnsafe;
        public ConnectionFlags flags;

        public TempLaneConnection(GeneratedConnection generatedConnection, Bezier4x3 curve) {
            sourceEntity = generatedConnection.sourceEntity;
            targetEntity = generatedConnection.targetEntity;
            laneIndexMap = generatedConnection.laneIndexMap;
            lanePositionMap = generatedConnection.lanePositionMap;
            carriagewayAndGroupIndexMap = generatedConnection.carriagewayAndGroupIndexMap;
            method = generatedConnection.method;
            isUnsafe = generatedConnection.isUnsafe;
            bezier = curve;
            flags = 0;
        }

        public TempLaneConnection(Entity source, Entity target, int2 map, float3x2 positionMap, int4 carriagewayAndGroupIndex, PathMethod method, bool isUnsafe, Bezier4x3 curve, ConnectionFlags flags) {
            sourceEntity = source;
            targetEntity = target;
            laneIndexMap = map;
            lanePositionMap = positionMap;
            carriagewayAndGroupIndexMap = carriagewayAndGroupIndex;
            bezier = curve;
            this.method = method;
            this.isUnsafe = isUnsafe;
            this.flags = flags;
        }
    }
}
