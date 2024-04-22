using System;
using Unity.Entities;

namespace Traffic.CommonData
{
    internal struct NodeEdgeKey : IEquatable<NodeEdgeKey>
    {
        public Entity node;
        public Entity edge;

        public NodeEdgeKey(Entity node, Entity edge)
        {
            this.edge = edge;
            this.node = node;
        }


        public bool Equals(NodeEdgeKey other)
        {
            return node.Equals(other.node) && edge.Equals(other.edge);
        }

        public override bool Equals(object obj)
        {
            return obj is NodeEdgeKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (node.GetHashCode() * 397) ^ edge.GetHashCode();
            }
        }
    }
}
