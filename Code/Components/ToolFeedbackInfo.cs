using Traffic.CommonData;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.Components
{
    [InternalBufferCapacity(0)]
    public struct ToolFeedbackInfo : IBufferElementData
    {
        public Entity container;
        public float3 position;
        public FeedbackMessageType type;
    }
}
