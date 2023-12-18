using Unity.Entities;

namespace Traffic.Components
{
    public struct CustomPriority : IComponentData
    {
        public PriorityType left;
        public PriorityType right;
    }

    public enum PriorityType
    {
        None,
        RightOfWay,
        Yield,
        Stop,
    }
}
