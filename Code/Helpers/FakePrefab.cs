using System.Collections.Generic;
using Game.Prefabs;
using Traffic.Components;
using Unity.Entities;

namespace Traffic.Helpers
{
    /// <summary>
    /// Represents fake prefab,
    /// used purely for vanilla validation workaround with custom entites interacting with vanilla ones
    /// </summary>
    public class FakePrefab : PrefabBase
    {
        public override void GetPrefabComponents(HashSet<ComponentType> prefabComponents)
        {
            base.GetPrefabComponents(prefabComponents);
            prefabComponents.Add(ComponentType.ReadOnly<FakePrefabData>());
        }

        public override void GetArchetypeComponents(HashSet<ComponentType> prefabComponents)
        {
            base.GetArchetypeComponents(prefabComponents);
            prefabComponents.Add(ComponentType.ReadOnly<FakePrefabData>());
        }
    }
}
