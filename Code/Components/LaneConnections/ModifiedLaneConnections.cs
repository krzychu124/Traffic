using System;
using Colossal.Serialization.Entities;
using Unity.Entities;

namespace Traffic.Components.LaneConnections
{
    [InternalBufferCapacity(0)]
    public struct ModifiedLaneConnections : IBufferElementData, IEquatable<ModifiedLaneConnections>, ISerializable
    {
        public int laneIndex;
        public Entity edgeEntity;
        public Entity modifiedConnections;
        
        /// <summary>
        /// Equals for lane index and edge, ignores linked modifiedConnections entity!
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public bool Equals(ModifiedLaneConnections other) {
            return laneIndex == other.laneIndex && edgeEntity.Equals(other.edgeEntity);
        }

        public override int GetHashCode() {
            unchecked
            {
                return (laneIndex * 397) ^ edgeEntity.GetHashCode();
            }
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            Logger.Serialization($"Saving ModifiedLaneConnections: {laneIndex} {edgeEntity}, genEnt: {modifiedConnections}");
            writer.Write(1);
            writer.Write(laneIndex);
            writer.Write(edgeEntity);
            writer.Write(modifiedConnections);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int v);
            reader.Read(out laneIndex);
            reader.Read(out edgeEntity);
            reader.Read(out modifiedConnections);
            Logger.Serialization($"Reading ModifiedLaneConnections({v}): {laneIndex} {edgeEntity}, genEnt: {modifiedConnections}");
        }
    }
}
