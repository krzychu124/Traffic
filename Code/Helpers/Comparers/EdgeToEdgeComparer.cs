using System.Collections.Generic;
using Traffic.CommonData;

namespace Traffic.Helpers.Comparers
{
    internal struct EdgeToEdgeComparer: IComparer<EdgeToEdgeKey>
    {
        public int Compare(EdgeToEdgeKey x, EdgeToEdgeKey y)
        {
            int edge1Comparison = x.edge1.CompareTo(y.edge1);
            if (edge1Comparison != 0) return edge1Comparison;
            return x.edge2.CompareTo(y.edge2);
        }
    }
}
