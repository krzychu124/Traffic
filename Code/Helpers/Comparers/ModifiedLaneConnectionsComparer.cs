using System.Collections.Generic;
using System.Runtime.InteropServices;
using Traffic.Components.LaneConnections;
using Unity.Mathematics;

namespace Traffic.Helpers.Comparers
{
    [StructLayout(LayoutKind.Sequential, Size = 1)]
    internal struct ModifiedLaneConnectionsComparer : IComparer<ModifiedLaneConnections>
    {
        public int Compare(ModifiedLaneConnections x, ModifiedLaneConnections y)
        {
            int targetEntityComparison = x.edgeEntity.CompareTo(y.edgeEntity);
            return math.select(x.laneIndex.CompareTo(y.laneIndex), targetEntityComparison, targetEntityComparison != 0);
        }
    }
}
