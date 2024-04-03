using Traffic.UISystems;
using Unity.Entities;

namespace Traffic.Common
{
    public struct ActionOverlayData : IComponentData
    {
        public Entity entity;
        public ModUISystem.ActionOverlayPreview mode;
    }
}
