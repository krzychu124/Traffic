using Game.Common;
using Unity.Entities;

namespace Traffic.Common
{
    public struct CustomRaycastResult
    {
        public RaycastHit hit;
        public Entity owner;
    }
}
