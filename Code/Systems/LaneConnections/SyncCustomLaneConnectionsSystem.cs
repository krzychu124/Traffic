// #define DEBUG_CONNECTIONS
// #define DEBUG_CONNECTIONS_SYNC

using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components.LaneConnections;
using Traffic.Helpers;
using Traffic.Tools;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Traffic.Systems.LaneConnections
{
    /// <summary>
    /// Sync lane connections on existing nodes (ignore custom connections created on the same frame)
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
        private EntityQuery _nodeEdgeQuery;
        private EntityQuery _query;

        protected override void OnCreate()
        {
            base.OnCreate();
            _nodeEdgeQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<ConnectedNode>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Updated>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });

            _query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<ConnectedEdge>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Updated>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.Exclude<ModifiedLaneConnections>(), }
            });
            RequireForUpdate(_query);
        }

        protected override void OnUpdate()
        {
            NativeArray<Entity> updatedNodes = _query.ToEntityArray(Allocator.TempJob);
#if DEBUG_CONNECTIONS
            Logger.Debug($"SyncCustomLaneConnectionsSystem Update! ({updatedNodes.Length})");
#endif
            EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob);

            JobHandle mapJobHandle = default;
            NativeParallelHashMap<NodeEdgeKey, Entity> tempMap = default;
            if (!_nodeEdgeQuery.IsEmptyIgnoreFilter)
            {
                int entityCount = _nodeEdgeQuery.CalculateEntityCount();
                tempMap = new NativeParallelHashMap<NodeEdgeKey, Entity>(entityCount * 2, Allocator.TempJob);
                MapOriginalEntitiesJob mapEntities = new MapOriginalEntitiesJob()
                {
                    entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                    tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                    edgeTypeHandle = SystemAPI.GetComponentTypeHandle<Edge>(true),
                    tempData = SystemAPI.GetComponentLookup<Temp>(true),
                    edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                    nodeData = SystemAPI.GetComponentLookup<Node>(true),
                    connectedEdgeBuffer = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                    nodeEdgeMap = tempMap
                };
                mapJobHandle = mapEntities.Schedule(_nodeEdgeQuery, Dependency);
#if DEBUG_CONNECTIONS
                mapJobHandle.Complete();
                NativeKeyValueArrays<NodeEdgeKey, Entity> keyValueArrays = tempMap.GetKeyValueArrays(Allocator.Temp);
                string s = "NodeEdgeKeyPairs (Sync):\n";
                for (var i = 0; i < keyValueArrays.Length; i++)
                {
                    var pair = (keyValueArrays.Keys[i], keyValueArrays.Values[i]);
                    s += $"{pair.Item1.node} + {pair.Item1.edge} -> {pair.Item2}\n";
                }
                Logger.Debug(s);
#endif
            }

            SyncConnectionsJob job = new SyncConnectionsJob()
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
            };
            JobHandle jobHandle = job.Schedule(updatedNodes.Length, JobHandle.CombineDependencies(Dependency, mapJobHandle));
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

        internal struct EdgeInfo
        {
            public Entity edge;
            public bool wasTemp;
            public bool isStart;
            public bool2 compositionChanged;
        }
    }
}
