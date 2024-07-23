using Game;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Traffic.Helpers;
using Traffic.Systems.LaneConnections.SharedJobs;
using Traffic.Tools;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Traffic.Systems.LaneConnections
{
    /// <summary>
    /// Sync lane connections on existing nodes (ignore custom connections created in the same frame)
    /// - Node update (when composition didn't change, e.g.: traffic lights, stop signs set/unset)
    /// - Edge update (when composition didn't change significantly e.g.: added sidewalk, barrier, lights, trees)
    /// - Edge split (edge has been split into one or more edges)
    /// - Edge combine (node reduction) after removing 3rd edge, remaining two may trigger node reduction - generate updated edge joining two remaining nodes (when matching composition/asset))
    /// </summary>
#if WITH_BURST
    [BurstCompile]
#endif
    public partial class SyncCustomLaneConnectionsSystem : GameSystemBase
    {
        private CityConfigurationSystem _cityConfigurationSystem;
        private EntityQuery _updatedEdgesQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            _cityConfigurationSystem = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            _updatedEdgesQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<ConnectedNode>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Updated>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });
            RequireForUpdate(_updatedEdgesQuery);
        }

        protected override void OnUpdate()
        {
            int updatedEdges = _updatedEdgesQuery.CalculateEntityCount();
#if DEBUG_CONNECTIONS
            Logger.Debug($"SyncCustomLaneConnectionsSystem[{UnityEngine.Time.frameCount}] Update! ({updatedEdges})");
#endif
            NativeHashSet<Entity> requireUpdate = new NativeHashSet<Entity>(32, Allocator.Temp);
            int entityCount = _updatedEdgesQuery.CalculateEntityCount();
            NativeParallelHashMap<NodeEdgeKey, Entity> tempMap = new NativeParallelHashMap<NodeEdgeKey, Entity>(entityCount * 2, Allocator.TempJob);

            JobHandle mapJobHandle = new MapNodeEdgeEntitiesJob()
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                edgeTypeHandle = SystemAPI.GetComponentTypeHandle<Edge>(true),
                tempData = SystemAPI.GetComponentLookup<Temp>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                connectedEdgeBuffer = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                modifiedConnectionsBuffer = SystemAPI.GetBufferLookup<ModifiedLaneConnections>(true),
                nodeData = SystemAPI.GetComponentLookup<Node>(true),
                toolManagedEntities = SystemAPI.GetComponentLookup<ToolManaged>(true),
                nodeEdgeMap = tempMap,
                modifiedNodeSet = requireUpdate,
                collectUpdatedNodes = true,
#if DEBUG_CONNECTIONS
                debugSystemName = nameof(SyncCustomLaneConnectionsSystem),
#endif
            }.Schedule(_updatedEdgesQuery, Dependency);

            mapJobHandle.Complete();
#if DEBUG_CONNECTIONS
            // early complete for immediate results (debug only)
            NativeKeyValueArrays<NodeEdgeKey, Entity> keyValueArrays = tempMap.GetKeyValueArrays(Allocator.Temp);
            string s = $"NodeEdgeKeyPairs (Sync: {keyValueArrays.Length}):\n";
            for (var i = 0; i < keyValueArrays.Length; i++)
            {
                var pair = (keyValueArrays.Keys[i], keyValueArrays.Values[i]);
                s += $"{pair.Item1.node} + {pair.Item1.edge} -> {pair.Item2}\n";
            }
            Logger.Debug(s);
            string n = $"NodesToUpdate (Sync: {requireUpdate.Count})\n";
            NativeArray<Entity> reqLog = requireUpdate.ToNativeArray(Allocator.Temp);
            foreach (Entity entity in reqLog)
            {
                n += $"\tNode: {entity}\n";
            }
            Logger.Debug(n);
            reqLog.Dispose();
#endif
            JobHandle jobHandle = default;
            if (!requireUpdate.IsEmpty)
            {
                NativeArray<Entity> tempNodes = requireUpdate.ToNativeArray(Allocator.TempJob);
                EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
                jobHandle = new SyncConnectionsJob()
                {
                    nodeData = SystemAPI.GetComponentLookup<Node>(true),
                    edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                    tempData = SystemAPI.GetComponentLookup<Temp>(true),
                    hiddenData = SystemAPI.GetComponentLookup<Hidden>(true),
                    compositionData = SystemAPI.GetComponentLookup<Composition>(true),
                    netCompositionData = SystemAPI.GetComponentLookup<NetCompositionData>(true),
                    prefabData = SystemAPI.GetComponentLookup<PrefabRef>(true),
                    modifiedConnectionsBuffer = SystemAPI.GetBufferLookup<ModifiedLaneConnections>(true),
                    connectedEdgeBuffer = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                    generatedConnectionBuffer = SystemAPI.GetBufferLookup<GeneratedConnection>(true),
                    netCompositionLaneBuffer = SystemAPI.GetBufferLookup<NetCompositionLane>(true),
                    fakePrefabRef = Traffic.Systems.ModDefaultsSystem.FakePrefabRef,
                    leftHandTraffic = _cityConfigurationSystem.leftHandTraffic,
                    nodeEdgeMap = tempMap,
                    tempNodes = tempNodes.AsReadOnly(),
                    commandBuffer = commandBuffer.AsParallelWriter(),
                }.Schedule(tempNodes.Length, JobHandle.CombineDependencies(Dependency, mapJobHandle));

                jobHandle.Complete();
                commandBuffer.Playback(EntityManager);
                commandBuffer.Dispose();
                tempNodes.Dispose();
            }
            requireUpdate.Dispose();
            tempMap.Dispose();
            Dependency = jobHandle;
#if DEBUG_CONNECTIONS
            Logger.Debug("SyncCustomLaneConnectionsSystem Update finished!");
#endif
        }
    }
}
