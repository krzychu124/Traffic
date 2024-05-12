#if DEBUG_GIZMO
using Colossal.Mathematics;
#endif
using Colossal.Serialization.Entities;
using Game.Pathfind;
using Traffic.CommonData;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.Components.LaneConnections
{
    [InternalBufferCapacity(0)]
    public struct GeneratedConnection : IBufferElementData, ISerializable
    {
        public Entity sourceEntity;
        public Entity targetEntity;
        public int2 laneIndexMap;
        public int4 carriagewayAndGroupIndexMap;
        public float3x2 lanePositionMap;
        public PathMethod method;
        public bool isUnsafe;

#if DEBUG_GIZMO
        public Bezier4x3 debug_bezier;
#endif

        public override string ToString() {
            return $"s: {sourceEntity} t: {targetEntity} l: {laneIndexMap}, p: {lanePositionMap}, c&gIdx: {carriagewayAndGroupIndexMap} m: {method} u: {isUnsafe}";
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            Logger.Serialization($"Saving GeneratedConnection: {sourceEntity} {targetEntity}, lI: {laneIndexMap}, m:{method}, un:{isUnsafe}");
            writer.Write(DataMigrationVersion.LaneConnectionDataUpgradeV1);
            writer.Write(sourceEntity);
            writer.Write(targetEntity);
            writer.Write(laneIndexMap);
            writer.Write(carriagewayAndGroupIndexMap);
            writer.Write(lanePositionMap.c0);//split to 2 components
            writer.Write(lanePositionMap.c1);
            writer.Write((ushort)method);
            writer.Write(isUnsafe);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int v);//data version
            if (v < DataMigrationVersion.LaneConnectionDataUpgradeV1)
            {
                // DO NOT CHANGE ORDER
                reader.Read(out sourceEntity);
                reader.Read(out targetEntity);
                reader.Read(out laneIndexMap);
                reader.Read(out ushort savedMethod);
                method = (PathMethod)savedMethod;
                reader.Read(out isUnsafe);
                carriagewayAndGroupIndexMap = new int4(-1);
                lanePositionMap = new float3x2();
            }
            else
            {
                reader.Read(out sourceEntity);
                reader.Read(out targetEntity);
                reader.Read(out laneIndexMap);
                reader.Read(out carriagewayAndGroupIndexMap);
                reader.Read(out float3 lanePosC0);
                reader.Read(out float3 lanePosC1);
                //merge 2 components
                lanePositionMap = new float3x2(lanePosC0, lanePosC1);
                //merge 2 components
                reader.Read(out ushort savedMethod);
                method = (PathMethod)savedMethod;
                reader.Read(out isUnsafe);
            }
            Logger.Serialization($"Read GeneratedConnection ({v}): {sourceEntity} {targetEntity}, lI: {laneIndexMap}, m:{method}, un:{isUnsafe} || carrGroup: {carriagewayAndGroupIndexMap}, posMap: {lanePositionMap}");
        }
    }
}
