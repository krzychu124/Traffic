using System;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.Common
{
    public struct CustomRaycastHit : IEquatable<CustomRaycastHit>
    {
        public Entity hitEntity;
        public float3 position;
        public float3 hitPosition;
        public float3 hitDirection;
        public float normalizedDistance;

        #region Equatable
        public bool Equals(CustomRaycastHit other) {
            return hitEntity.Equals(other.hitEntity) && position.Equals(other.position) && hitPosition.Equals(other.hitPosition) && hitDirection.Equals(other.hitDirection) && normalizedDistance.Equals(other.normalizedDistance);
        }

        public override bool Equals(object obj) {
            return obj is CustomRaycastHit other && Equals(other);
        }

        public override int GetHashCode() {
            unchecked
            {
                int hashCode = hitEntity.GetHashCode();
                hashCode = (hashCode * 397) ^ position.GetHashCode();
                hashCode = (hashCode * 397) ^ hitPosition.GetHashCode();
                hashCode = (hashCode * 397) ^ hitDirection.GetHashCode();
                hashCode = (hashCode * 397) ^ normalizedDistance.GetHashCode();
                return hashCode;
            }
        }

        public static bool operator ==(CustomRaycastHit left, CustomRaycastHit right) {
            return left.Equals(right);
        }

        public static bool operator !=(CustomRaycastHit left, CustomRaycastHit right) {
            return !left.Equals(right);
        }
        #endregion
    }
}
