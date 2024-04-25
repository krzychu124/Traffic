#if DEBUG
#define DEBUG_TOOL
#endif
// #define DEBUG_CONNECTIONS
using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.Systems.LaneConnections
{
    
    /// <summary>
    /// Apply changes in temporary entities containing ModifiedLaneConnections buffer
    /// </summary>
#if WITH_BURST
    [BurstCompile]
#endif
    public partial class ApplyLaneConnectionsSystem : GameSystemBase
    {
        private EntityQuery _tempQuery;
        private EntityQuery _tempEdgeQuery;
        private ToolOutputBarrier _toolOutputBarrier;

        protected override void OnCreate()
        {
            base.OnCreate();
            _toolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();
            _tempEdgeQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<ConnectedNode>(), ComponentType.ReadOnly<Temp>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });
            _tempQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { ComponentType.ReadOnly<ConnectedEdge>(), ComponentType.ReadOnly<Temp>() },
                Any = new []{ComponentType.ReadOnly<ModifiedConnections>(), ComponentType.ReadOnly<ModifiedLaneConnections>(), }
            });
            RequireForUpdate(_tempQuery);
        }

        protected override void OnUpdate()
        {
            Logger.DebugTool($"ApplyLaneConnectionsSystem: Process {_tempQuery.CalculateEntityCount()} entities");

            int entityCount = _tempEdgeQuery.CalculateEntityCount();
            NativeParallelHashMap<NodeEdgeKey, Entity> tempEdgeMap = new NativeParallelHashMap<NodeEdgeKey, Entity>(entityCount * 2, Allocator.TempJob);
            JobHandle mapEdgesJobHandle = new MapReplacedEdgesJob
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                edgeTypeHandle = SystemAPI.GetComponentTypeHandle<Edge>(true),
                tempData = SystemAPI.GetComponentLookup<Temp>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                connectedEdgeBuffer = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                nodeEdgeMap = tempEdgeMap,
            }.Schedule(_tempEdgeQuery, Dependency);
#if DEBUG_CONNECTIONS
            mapEdgesJobHandle.Complete();
            NativeKeyValueArrays<NodeEdgeKey, Entity> keyValueArrays = tempEdgeMap.GetKeyValueArrays(Allocator.Temp);
            string s = "NodeEdgeKeyPairs (Apply):\n";
            for (var i = 0; i < keyValueArrays.Length; i++)
            {
                var pair = (keyValueArrays.Keys[i], keyValueArrays.Values[i]);
                s += $"{pair.Item1.node} + {pair.Item1.edge} -> {pair.Item2}\n";
            }
            Logger.Debug(s);
#endif
            
            HandleTempEntitiesJob handleTempEntities = new HandleTempEntitiesJob()
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                editIntersectionTypeHandle = SystemAPI.GetComponentTypeHandle<EditIntersection>(true),
                modifiedLaneConnectionTypeHandle = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                tempData = SystemAPI.GetComponentLookup<Temp>(true),
                dataOwnerData = SystemAPI.GetComponentLookup<DataOwner>(false),
                generatedConnectionData = SystemAPI.GetBufferLookup<GeneratedConnection>(false),
                modifiedLaneConnectionData = SystemAPI.GetBufferLookup<ModifiedLaneConnections>(false),
                fakePrefabRef = Traffic.Systems.ModDefaultsSystem.FakePrefabRef,
                tempEdgeMap = tempEdgeMap,
                commandBuffer = _toolOutputBarrier.CreateCommandBuffer(),
            };
            JobHandle jobHandle = handleTempEntities.Schedule(_tempQuery, mapEdgesJobHandle);
            _toolOutputBarrier.AddJobHandleForProducer(jobHandle);
            tempEdgeMap.Dispose(jobHandle);
            Dependency = jobHandle;
        }
    }
}
