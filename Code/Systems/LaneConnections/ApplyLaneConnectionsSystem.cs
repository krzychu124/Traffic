﻿using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Traffic.Systems.LaneConnections.SharedJobs;
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
        private EntityQuery _tempNodesQuery;
        private EntityQuery _tempEdgesQuery;
        private ToolOutputBarrier _toolOutputBarrier;

        protected override void OnCreate()
        {
            base.OnCreate();
            _toolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();
            _tempEdgesQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<ConnectedNode>(), ComponentType.ReadOnly<Temp>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });
            _tempNodesQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { ComponentType.ReadOnly<ConnectedEdge>(), ComponentType.ReadOnly<Temp>() },
                Any = new[] { ComponentType.ReadOnly<ModifiedConnections>(), ComponentType.ReadOnly<ModifiedLaneConnections>(), }
            });
            RequireForUpdate(_tempNodesQuery);
        }

        protected override void OnUpdate()
        {
            Logger.DebugTool($"ApplyLaneConnectionsSystem: Process {_tempNodesQuery.CalculateEntityCount()} entities");

            int entityCount = _tempEdgesQuery.CalculateEntityCount();
            NativeParallelHashMap<NodeEdgeKey, Entity> tempEdgeMap = new NativeParallelHashMap<NodeEdgeKey, Entity>(entityCount * 2, Allocator.TempJob);
            JobHandle mapEdgesJobHandle = new MapNodeEdgeEntitiesJob
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                edgeTypeHandle = SystemAPI.GetComponentTypeHandle<Edge>(true),
                tempData = SystemAPI.GetComponentLookup<Temp>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                connectedEdgeBuffer = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                nodeEdgeMap = tempEdgeMap,
#if DEBUG_CONNECTIONS
                nodeData = SystemAPI.GetComponentLookup<Node>(true),
                debugSystemName = nameof(ApplyLaneConnectionsSystem),
#endif
            }.Schedule(_tempEdgesQuery, Dependency);
#if DEBUG_CONNECTIONS
            // early complete for immediate results (debug only)
            mapEdgesJobHandle.Complete();
            NativeKeyValueArrays<NodeEdgeKey, Entity> keyValueArrays = tempEdgeMap.GetKeyValueArrays(Allocator.Temp);
            string s = $"NodeEdgeKeyPairs (Apply: {keyValueArrays.Length}):\n";
            for (var i = 0; i < keyValueArrays.Length; i++)
            {
                var pair = (keyValueArrays.Keys[i], keyValueArrays.Values[i]);
                s += $"{pair.Item1.node} + {pair.Item1.edge} -> {pair.Item2}\n";
            }
            Logger.Debug(s);
#endif
            JobHandle jobHandle = new HandleTempEntitiesJob()
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
            }.Schedule(_tempNodesQuery, mapEdgesJobHandle);

            _toolOutputBarrier.AddJobHandleForProducer(jobHandle);
            tempEdgeMap.Dispose(jobHandle);
            Dependency = jobHandle;
        }
    }
}
