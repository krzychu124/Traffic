using Unity.Entities;

namespace Traffic.Components.PrioritySigns
{
    public struct PriorityDefinition : IComponentData
    {
        public Entity node;
        public Entity edge;
        public Entity laneHandle;
    }
}
