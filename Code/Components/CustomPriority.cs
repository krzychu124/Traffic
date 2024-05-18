using Traffic.Components.PrioritySigns;
using Unity.Entities;

namespace Traffic.Components
{
    public struct CustomPriority : IComponentData
    {
        public PriorityType left;
        public PriorityType right;
    }
}
