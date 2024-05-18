using System;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.Components.PrioritySigns
{
    //IMPORTANT Careful, it's reinterpreted from TempLanePriority
    [InternalBufferCapacity(0)]
    public struct LanePriority: IBufferElementData, IEquatable<LanePriority>
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
    }
}
