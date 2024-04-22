using System.Collections.Generic;
using System.Runtime.InteropServices;
using Traffic.Components.LaneConnections;
using Unity.Mathematics;

namespace Traffic.Helpers.Comparers
{
    [StructLayout(LayoutKind.Sequential, Size = 1)]
    internal struct GeneratedConnectionComparer : IComparer<GeneratedConnection>
    {
        public int Compare(GeneratedConnection x, GeneratedConnection y)
        {
            int targetEntityComparison = x.targetEntity.CompareTo(y.targetEntity);
            return math.select(x.laneIndexMap.y.CompareTo(y.laneIndexMap.y), targetEntityComparison, targetEntityComparison != 0);
        }
    }
}
