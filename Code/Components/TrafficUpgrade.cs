using System;
using Unity.Entities;

namespace Traffic.Components
{
    public struct TrafficUpgrade : IComponentData
    {
        public UpgradeType left;
        public UpgradeType right;
    }

    [Flags]
    public enum UpgradeType
    {
        None,
        NoUturn,
    }
}