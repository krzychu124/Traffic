using Colossal.Serialization.Entities;
using Game;
using Game.Prefabs;
using Game.Serialization;
using Traffic.Helpers;
using Unity.Entities;
using UnityEngine;

namespace Traffic.Systems
{
    public partial class ModDefaultsSystem : GameSystemBase, IPreDeserialize
    {
        internal static Entity FakePrefabRef;
        private PrefabSystem _prefabSystem;
        private FakePrefab _fakePrefab;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
        }

        protected override void OnUpdate() { }

        public void PreDeserialize(Context context)
        {
            if (!_fakePrefab)
            {
                Logger.Serialization($"PreDeserialize: Generating FakePrefab...");
                _fakePrefab = ScriptableObject.CreateInstance<FakePrefab>();
                _fakePrefab.name = "Traffic.FakePrefab";
                _fakePrefab.active = true;
                if (!_prefabSystem.TryGetPrefab(_fakePrefab.GetPrefabID(), out _) && 
                    _prefabSystem.AddPrefab(_fakePrefab) && 
                    _prefabSystem.TryGetEntity(_fakePrefab, out FakePrefabRef))
                {
                    Logger.Serialization($"Created Traffic.FakePrefab entity: {FakePrefabRef}");
                }
            }
        }
    }
}
