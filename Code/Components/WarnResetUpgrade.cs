using Unity.Entities;

namespace Traffic.Components
{
    [InternalBufferCapacity(0)]
    public struct WarnResetUpgrade : IBufferElementData
    {
        public Entity entity;
    }
}
