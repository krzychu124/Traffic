using Unity.Entities;

namespace Traffic.Components
{
    public struct DataOwner: IComponentData
    {
        public Entity entity;

        public DataOwner(Entity owner) {
            entity = owner;
        }
    }
}
