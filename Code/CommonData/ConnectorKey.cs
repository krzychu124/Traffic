using System;
using Unity.Entities;

namespace Traffic.CommonData
{
    public struct ConnectorKey: IEquatable<ConnectorKey>
    {
        public Entity edge;
        public int index;

        public ConnectorKey(Entity e, int i) {
            edge = e;
            index = i;
        }
        
        public bool Equals(ConnectorKey other) {
            return edge.Equals(other.edge) && index == other.index;
        }

        public override bool Equals(object obj) {
            return obj is ConnectorKey other && Equals(other);
        }

        public override int GetHashCode() {
            unchecked
            {
                return (edge.GetHashCode() * 397) ^ index;
            }
        }
    }
}
