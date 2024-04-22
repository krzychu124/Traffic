using System;
using Unity.Entities;

namespace Traffic.CommonData
{
    internal struct EdgeToEdgeKey : IEquatable<EdgeToEdgeKey>
    {
        public Entity edge1;
        public Entity edge2;

        public EdgeToEdgeKey(Entity edge1, Entity edge2)
        {
            this.edge1 = edge1;
            this.edge2 = edge2;
        }


        public bool Equals(EdgeToEdgeKey other)
        {
            return edge1.Equals(other.edge1) && edge2.Equals(other.edge2);
        }

        public override bool Equals(object obj)
        {
            return obj is EdgeToEdgeKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (edge1.GetHashCode() * 397) ^ edge2.GetHashCode();
            }
        }
    }
}
