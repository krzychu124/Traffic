using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Traffic.CommonData;
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
        private EntityQuery _updatedEdgesQuery;
        private EntityQuery _updatedNodesQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            _updatedEdgesQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<ConnectedNode>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Updated>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });

            _updatedNodesQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<ConnectedEdge>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Updated>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.Exclude<ModifiedLaneConnections>(), }
            });
            RequireForUpdate(_updatedNodesQuery);
        }

        protected override void OnUpdate()
        {
            NativeArray<Entity> updatedNodes = _updatedNodesQuery.ToEntityArray(Allocator.TempJob);
#if DEBUG_CONNECTIONS
            Logger.Debug($"SyncCustomLaneConnectionsSystem Update! ({updatedNodes.Length})");
#endif
            EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob);

            JobHandle mapJobHandle = default;
            NativeParallelHashMap<NodeEdgeKey, Entity> tempMap = default;
            if (!_updatedEdgesQuery.IsEmptyIgnoreFilter)
            {
                int entityCount = _updatedEdgesQuery.CalculateEntityCount();
                tempMap = new NativeParallelHashMap<NodeEdgeKey, Entity>(entityCount * 2, Allocator.TempJob);
                
                mapJobHandle = new MapNodeEdgeEntitiesJob()
                {
                    entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                    tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                    edgeTypeHandle = SystemAPI.GetComponentTypeHandle<Edge>(true),
                    tempData = SystemAPI.GetComponentLookup<Temp>(true),
                    edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                    connectedEdgeBuffer = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                    nodeEdgeMap = tempMap,
#if DEBUG_CONNECTIONS
                    nodeData = SystemAPI.GetComponentLookup<Node>(true),
                    debugSystemName = nameof(SyncCustomLaneConnectionsSystem),
#endif
                }.Schedule(_updatedEdgesQuery, Dependency);

#if DEBUG_CONNECTIONS
                // early complete for immediate results (debug only)
                mapJobHandle.Complete();
                NativeKeyValueArrays<NodeEdgeKey, Entity> keyValueArrays = tempMap.GetKeyValueArrays(Allocator.Temp);
                string s = $"NodeEdgeKeyPairs (Sync: {keyValueArrays.Length}):\n";
                for (var i = 0; i < keyValueArrays.Length; i++)
                {
                    var pair = (keyValueArrays.Keys[i], keyValueArrays.Values[i]);
                    s += $"{pair.Item1.node} + {pair.Item1.edge} -> {pair.Item2}\n";
                }
                Logger.Debug(s);
#endif
            }

            JobHandle jobHandle = new SyncConnectionsJob()
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
                fakePrefabRef = Traffic.Systems.ModDefaultsSystem.FakePrefabRef,
                nodeEdgeMap = tempMap,
                tempNodes = updatedNodes.AsReadOnly(),
                commandBuffer = commandBuffer.AsParallelWriter(),
            }.Schedule(updatedNodes.Length, JobHandle.CombineDependencies(Dependency, mapJobHandle));

            jobHandle.Complete();
            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
            updatedNodes.Dispose(jobHandle);
            if (tempMap.IsCreated)
            {
                tempMap.Dispose(jobHandle);
            }
            Dependency = jobHandle;
#if DEBUG_CONNECTIONS
            Logger.Debug("SyncCustomLaneConnectionsSystem Update finished!");
#endif
        }
    }
}
