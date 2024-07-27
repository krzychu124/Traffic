using Game.Tools;
using Unity.Entities;

namespace Traffic.Components
{
    public struct DataTemp : IComponentData
    {
        public Entity original;
        public TempFlags flags;

        public DataTemp(Entity e, TempFlags tempFlags)
        {
            original = e;
            flags = tempFlags;
        }
    }
}
