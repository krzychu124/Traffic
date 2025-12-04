using System;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.SceneFlow;
using Game.UI;
using Game.UI.Localization;
#if WITH_BURST
using Unity.Burst;
#endif
using Unity.Collections;
using Unity.Entities;
using CarLane = Game.Net.CarLane;
using Edge = Game.Net.Edge;
using SubLane = Game.Net.SubLane;

namespace Traffic.Systems.ModCompatibility
{
    using Colossal.IO.AssetDatabase;

#if WITH_BURST
    [BurstCompile]
#endif
    public partial class TLEDataMigrationSystem : GameSystemBase
    {
        private EntityQuery _query;
        private ComponentType _tleComponent;

        protected override void OnCreate()
        {
            base.OnCreate();
            Logger.Info($"Initializing {nameof(TLEDataMigrationSystem)}!");
            ExecutableAsset tleAsset = AssetDatabase.global.GetAsset<ExecutableAsset>(SearchFilter<ExecutableAsset>.ByCondition(asset => asset.isLoaded && asset.name.Equals("C2VM.CommonLibraries.LaneSystem")));
            Type customLaneDirectionType = tleAsset?.assembly.GetType("C2VM.CommonLibraries.LaneSystem.CustomLaneDirection", false);
            if (customLaneDirectionType == null)
            {
                Logger.Error($"CustomLaneDirection component type not found in C2VM.CommonLibraries.LaneSystem. Disabled Migration System!");
                Enabled = false;
                return;
            }
            try
            {
                _tleComponent = ComponentType.FromTypeIndex(TypeManager.GetTypeIndex(customLaneDirectionType));
                _query = GetEntityQuery(new EntityQueryDesc
                {
                    All = new[] { _tleComponent, ComponentType.ReadOnly<Node>() },
                    None = new[] { ComponentType.ReadOnly<Deleted>(), }
                });
                RequireForUpdate(_query);
            }
            catch (Exception e)
            {
                Enabled = false;
                Logger.Error($"Something went wrong while initializing {nameof(TLEDataMigrationSystem)}. Disabled Migration System!\n{e}");
                UnityEngine.Debug.LogException(e);
            }
        }

        protected override void OnUpdate()
        {
            Logger.Info($"Deserializing data from {_query.CalculateEntityCount()} TLE modified intersections");
            EntityCommandBuffer entityCommandBuffer = new EntityCommandBuffer(Allocator.TempJob);
            NativeQueue<int> generatedIntersectionsCount = new NativeQueue<int>(Allocator.TempJob);
            new MigrateCustomLaneDirectionsJob()
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                connectedEdgeBufferTypeHandle = SystemAPI.GetBufferTypeHandle<ConnectedEdge>(true),
                subLaneBufferTypeHandle = SystemAPI.GetBufferTypeHandle<SubLane>(true),
                compositionData = SystemAPI.GetComponentLookup<Composition>(true),
                carLaneData = SystemAPI.GetComponentLookup<CarLane>(true),
                curveData = SystemAPI.GetComponentLookup<Curve>(true),
                deletedData = SystemAPI.GetComponentLookup<Deleted>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                laneData = SystemAPI.GetComponentLookup<Lane>(true),
                masterLaneData = SystemAPI.GetComponentLookup<MasterLane>(true),
                prefabCompositionLaneBuffer = SystemAPI.GetBufferLookup<NetCompositionLane>(true),
                fakePrefabRef = Traffic.Systems.ModDefaultsSystem.FakePrefabRef,
                generatedIntersectionData = generatedIntersectionsCount.AsParallelWriter(),
                commandBuffer = entityCommandBuffer.AsParallelWriter(),
            }.ScheduleParallel(_query, Dependency).Complete();
            entityCommandBuffer.Playback(EntityManager);
            entityCommandBuffer.Dispose();
            
            int count = 0;
            while (generatedIntersectionsCount.TryDequeue(out int value)) { count += value; }
            generatedIntersectionsCount.Dispose();
            
            // delete TLE data components to prevent data corruption
            NativeArray<Entity> entities = _query.ToEntityArray(Allocator.Temp);
            EntityManager.RemoveComponent(entities, _tleComponent);
            
            Logger.Info($"Deserialized and updated {count} intersections with custom lane connections");
            GameManager.instance.userInterface.appBindings.ShowMessageDialog(
                new MessageDialog("Traffic mod ⇆ Traffic Lights Enhancement Alpha", 
                    $"**Traffic** mod detected **Traffic Lights Enhancement Alpha Lane Direction Tool** data ({entities.Length} intersections).\n\n" +
                    $"Data migration process successfully migrated {count} intersection configurations to the **Traffic's Lane Connector tool**", 
                    LocalizedString.Id("Common.ERROR_DIALOG_CONTINUE")), null);
        }
    }
}
