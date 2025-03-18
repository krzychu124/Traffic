using System;
using Colossal.Serialization.Entities;
using TrafficDataMigrationSystem = Traffic.Systems.Serialization.TrafficDataMigrationSystem;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.Components.LaneConnections
{
    [InternalBufferCapacity(0)]
    public struct ModifiedLaneConnections : IBufferElementData, IEquatable<ModifiedLaneConnections>, ISerializable
    {
        public int laneIndex;
        public int2 carriagewayAndGroup;
        public float3 lanePosition;
        public Entity edgeEntity;
        public Entity modifiedConnections;
        
        /// <summary>
        /// Equals for lane index and edge, ignores linked modifiedConnections entity!
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public bool Equals(ModifiedLaneConnections other) {
            return laneIndex == other.laneIndex && edgeEntity.Equals(other.edgeEntity);// todo check position
        }

        public override int GetHashCode() {
            unchecked
            {
                return (laneIndex * 397) ^ edgeEntity.GetHashCode();
            }
        }
        #if SERIALIZATION
        public override string ToString()
        {
            return $"MLC: ({laneIndex}), cag: {carriagewayAndGroup} pos: {lanePosition} edge: {edgeEntity} mc: {modifiedConnections}";
        }
        #endif

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            Logger.Serialization($"Saving ModifiedLaneConnections: {laneIndex} {edgeEntity}, genEnt: {modifiedConnections}");
            writer.Write(DataMigrationVersion.LaneConnectionDataUpgradeV1);
            writer.Write(laneIndex);
            writer.Write(carriagewayAndGroup);
            writer.Write(lanePosition);
            writer.Write(edgeEntity);
            writer.Write(modifiedConnections);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int v);
            if (v < DataMigrationVersion.LaneConnectionDataUpgradeV1)
            {
                // DO NOT CHANGE ORDER
                reader.Read(out laneIndex);
                reader.Read(out edgeEntity);
                reader.Read(out modifiedConnections);
                carriagewayAndGroup = TrafficDataMigrationSystem.InvalidCarriagewayAndGroup;
                lanePosition = float3.zero;
            }
            else
            {
                reader.Read(out laneIndex);
                reader.Read(out carriagewayAndGroup);
                reader.Read(out lanePosition);
                reader.Read(out edgeEntity);
                reader.Read(out modifiedConnections);
            }
            Logger.Serialization($"Reading ModifiedLaneConnections({v}): {laneIndex} {edgeEntity}, genEnt: {modifiedConnections}, carrGroup: {carriagewayAndGroup}, lanePos: {lanePosition}");
        }
    }
}
