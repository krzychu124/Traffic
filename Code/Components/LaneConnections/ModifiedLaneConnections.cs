using System;
using Unity.Entities;

namespace Traffic.Components.LaneConnections
{
    [InternalBufferCapacity(0)]
    public struct ModifiedLaneConnections : IBufferElementData, IEquatable<ModifiedLaneConnections>
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
    }
}
