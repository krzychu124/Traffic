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
        internal static Entity BikeDriveLanePrefabRef;
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
                    Logger.Serialization($"Created 'Traffic.FakePrefab' entity: {FakePrefabRef}");
                }
                
                Logger.Serialization("PreDeserialize: Searching for 'Bike Drive Lane 1.5'...");
                PrefabID bikeLaneId = new PrefabID(nameof(NetLaneGeometryPrefab), "Bicycle Drive Lane 1.5");
                if (_prefabSystem.TryGetPrefab(bikeLaneId, out PrefabBase bikePrefabData) &&
                    _prefabSystem.TryGetEntity(bikePrefabData, out BikeDriveLanePrefabRef))
                {
                    Logger.Serialization($"Found 'Bike Drive Lane 1.5' entity: {BikeDriveLanePrefabRef}");
                }
            }
        }
    }
}
