#if DEBUG_GIZMO
using Colossal.Mathematics;
#endif
using Colossal.Serialization.Entities;
using Game.Pathfind;
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
        public PathMethod method;
        public bool isUnsafe;

#if DEBUG_GIZMO
        public Bezier4x3 debug_bezier;
#endif

        public override string ToString() {
            return $"s: {sourceEntity} t: {targetEntity} l: {laneIndexMap}, m: {method} u: {isUnsafe}";
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            Logger.Serialization($"Saving GeneratedConnection: {sourceEntity} {targetEntity}, lI: {laneIndexMap}, m:{method}, un:{isUnsafe}");
            writer.Write(1);//data version
            writer.Write(sourceEntity);
            writer.Write(targetEntity);
            writer.Write(laneIndexMap);
            writer.Write((ushort)method);
            writer.Write(isUnsafe);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int v);//data version
            reader.Read(out sourceEntity);
            reader.Read(out targetEntity);
            reader.Read(out laneIndexMap);
            reader.Read(out ushort savedMethod);
            method = (PathMethod)savedMethod;
            reader.Read(out isUnsafe);
            Logger.Serialization($"Read GeneratedConnection ({v}): {sourceEntity} {targetEntity}, lI: {laneIndexMap}, m:{method}, un:{isUnsafe}");
        }
    }
}
