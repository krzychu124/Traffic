using Game;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Unity.Entities;

namespace Traffic.Systems
{
    public partial class TrafficDataClearSystem : GameSystemBase
    {
        private EntityQuery _query;
        
        protected override void OnCreate()
        {
            base.OnCreate();
            _query = SystemAPI.QueryBuilder()
                .WithAny<GeneratedConnection, CustomLaneConnection, EditIntersection>()
                .WithNone<FakePrefabData>()
                .Build();
            RequireForUpdate(_query);
        }

        protected override void OnUpdate()
        {
            Logger.Info($"Cleared {_query.CalculateEntityCount()} | noFilter: {_query.CalculateEntityCountWithoutFiltering()}");
            EntityManager.DestroyEntity(_query);
        }
    }
}
