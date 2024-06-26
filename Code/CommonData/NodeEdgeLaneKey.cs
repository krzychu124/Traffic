using System;

namespace Traffic.CommonData
{
    internal struct NodeEdgeLaneKey : IEquatable<NodeEdgeLaneKey>, IComparable<NodeEdgeLaneKey>
    {
        public int nodeIndex;
        public int edgeIndex;
        public int laneIndex;

        public NodeEdgeLaneKey(int nodeIndex, int edgeIndex, int laneIndex) {
            this.nodeIndex = nodeIndex;
            this.edgeIndex = edgeIndex;
            this.laneIndex = laneIndex;
        }
            
        public bool Equals(NodeEdgeLaneKey other) {
            return nodeIndex == other.nodeIndex && edgeIndex == other.edgeIndex && laneIndex == other.laneIndex;
        }

        public override int GetHashCode() {
            unchecked
            {
                int hashCode = nodeIndex;
                hashCode = (hashCode * 397) ^ edgeIndex;
                hashCode = (hashCode * 397) ^ laneIndex;
                return hashCode;
            }
        }
        
        public int CompareTo(NodeEdgeLaneKey other)
        {
            int nodeIndexComparison = nodeIndex.CompareTo(other.nodeIndex);
            if (nodeIndexComparison != 0) return nodeIndexComparison;
            int edgeIndexComparison = edgeIndex.CompareTo(other.edgeIndex);
            if (edgeIndexComparison != 0) return edgeIndexComparison;
            return laneIndex.CompareTo(other.laneIndex);
        }
    }
}
