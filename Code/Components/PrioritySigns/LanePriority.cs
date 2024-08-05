using System;
using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.Components.PrioritySigns
{
    //IMPORTANT Careful, it's reinterpreted from TempLanePriority!
    [InternalBufferCapacity(0)]
    public struct LanePriority: IBufferElementData, IEquatable<LanePriority>, ISerializable
    {
        /// <summary>
        /// (laneIndex, groupIndex, carriagewayIndex)
        /// </summary>
        public int3 laneIndex;
        public PriorityType priority;
        public bool isEnd;
        
        public bool Equals(LanePriority other)
        {
            return laneIndex.Equals(other.laneIndex);
        }

        public override int GetHashCode()
        {
            return laneIndex.GetHashCode();
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            Logger.Serialization($"Saving LanePriority: {laneIndex} {priority} {isEnd}");
            writer.Write(1);//data version
            writer.Write(laneIndex);
            writer.Write((ushort)priority);
            writer.Write(isEnd);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int v);
            reader.Read(out laneIndex);
            reader.Read(out ushort savedPriority);
            priority = (PriorityType)savedPriority;
            reader.Read(out isEnd);
            Logger.Serialization($"Reading DataOwner({v}): {laneIndex} {priority} {isEnd}");
        }
    }
}
