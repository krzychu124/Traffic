using Game.Common;
using Unity.Entities;

namespace Traffic.CommonData
{
    public struct CustomRaycastResult
    {
        public RaycastHit hit;
        public Entity owner;
    }
}
