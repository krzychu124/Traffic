using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CarLane = Game.Net.CarLane;
using Edge = Game.Net.Edge;
using SecondaryLane = Game.Net.SecondaryLane;
using SubLane = Game.Net.SubLane;


namespace Traffic.Systems.LaneConnections
{
#if WITH_BURST
    [BurstCompile]
#endif
    public partial class GenerateConnectorsSystem : GameSystemBase
    {
        private EntityQuery _definitionQuery;
        private EntityQuery _connectorsQuery;
        private ModificationBarrier5 _modificationBarrier;
        
        protected override void OnCreate() {
            base.OnCreate();
            
            _modificationBarrier = base.World.GetOrCreateSystemManaged<ModificationBarrier5>();
            _definitionQuery = GetEntityQuery(ComponentType.ReadOnly<EditIntersection>(), ComponentType.ReadOnly<EditLaneConnections>(), ComponentType.ReadOnly<Updated>(), ComponentType.Exclude<Temp>(), ComponentType.Exclude<Deleted>());
            _connectorsQuery = GetEntityQuery(ComponentType.ReadOnly<Connector>(), ComponentType.Exclude<Deleted>());
            
            RequireForUpdate(_definitionQuery);
        }

        protected override void OnUpdate() {
            Logger.Debug($"GenerateConnectorsSystem[{UnityEngine.Time.frameCount}]");
            NativeParallelHashMap<NodeEdgeLaneKey, Entity> connectorsMap = new (128, Allocator.TempJob);
            EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
            GenerateConnectorsJob job = new GenerateConnectorsJob
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                editIntersectionType = SystemAPI.GetComponentTypeHandle<EditIntersection>(true),
                connectorElementType = SystemAPI.GetBufferTypeHandle<ConnectorElement>(true),
                nodeData = SystemAPI.GetComponentLookup<Node>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                tempData = SystemAPI.GetComponentLookup<Temp>(true),
                hiddenData = SystemAPI.GetComponentLookup<Hidden>(true),
                deletedData = SystemAPI.GetComponentLookup<Deleted>(true),
                compositionData = SystemAPI.GetComponentLookup<Composition>(true),
                prefabRefData = SystemAPI.GetComponentLookup<PrefabRef>(true),
                prefabCompositionData = SystemAPI.GetComponentLookup<NetCompositionData>(true),
                prefabNetLaneData = SystemAPI.GetComponentLookup<NetLaneData>(true),
                prefabCarLaneData = SystemAPI.GetComponentLookup<CarLaneData>(true),
                prefabTrackLaneData = SystemAPI.GetComponentLookup<TrackLaneData>(true),
                prefabUtilityLaneData = SystemAPI.GetComponentLookup<UtilityLaneData>(true),
                carLaneComponentData = SystemAPI.GetComponentLookup<CarLane>(true),
                slaveLaneData = SystemAPI.GetComponentLookup<SlaveLane>(true),
                masterLaneData = SystemAPI.GetComponentLookup<MasterLane>(true),
                edgeLaneData = SystemAPI.GetComponentLookup<EdgeLane>(true),
                edgeGeometryData = SystemAPI.GetComponentLookup<EdgeGeometry>(true),
                curveData = SystemAPI.GetComponentLookup<Curve>(true),
                laneData = SystemAPI.GetComponentLookup<Lane>(true),
                secondaryLaneData = SystemAPI.GetComponentLookup<SecondaryLane>(true),
                connectedEdges = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                subLanes = SystemAPI.GetBufferLookup<SubLane>(true),
                prefabCompositionLanes = SystemAPI.GetBufferLookup<NetCompositionLane>(true),
                commandBuffer = commandBuffer,
            };
            Logger.Debug($"Generating (def): {_definitionQuery.CalculateEntityCount()} | chunks: {_definitionQuery.CalculateChunkCount()}");
            JobHandle jobHandle = job.Schedule(_definitionQuery, Dependency);
            jobHandle.Complete();
            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
            
            Logger.Debug($"Generated (connectors): {_connectorsQuery.CalculateEntityCount()} | chunks: {_connectorsQuery.CalculateChunkCount()}");
            CollectConnectorsJob collectConnectorsJob = new CollectConnectorsJob()
            {
                entityType = SystemAPI.GetEntityTypeHandle(),
                connectorType = SystemAPI.GetComponentTypeHandle<Connector>(true),
                resultMap = connectorsMap, 
            };
            JobHandle collectConnectorsHandle = collectConnectorsJob.Schedule(_connectorsQuery, JobHandle.CombineDependencies(Dependency, jobHandle));
            
            GenerateConnectionLanesJob job2 = new GenerateConnectionLanesJob()
            {
                editIntersectionType = SystemAPI.GetComponentTypeHandle<EditIntersection>(true),
                carLaneData = SystemAPI.GetComponentLookup<CarLane>(true),
                compositionData = SystemAPI.GetComponentLookup<Composition>(true),
                curveData = SystemAPI.GetComponentLookup<Curve>(true),
                laneData = SystemAPI.GetComponentLookup<Lane>(true),
                masterLaneData = SystemAPI.GetComponentLookup<MasterLane>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                nodeData = SystemAPI.GetComponentLookup<Node>(true),
                connectedEdgesBuffer = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                prefabCompositionLaneBuffer= SystemAPI.GetBufferLookup<NetCompositionLane>(true),
                subLanesBuffer = SystemAPI.GetBufferLookup<SubLane>(true),
                connectorsList = connectorsMap,
                commandBuffer = _modificationBarrier.CreateCommandBuffer(),
            };
            JobHandle jobHandle2 = job2.Schedule(_definitionQuery, collectConnectorsHandle);
            _modificationBarrier.AddJobHandleForProducer(jobHandle2);
            connectorsMap.Dispose(jobHandle2);
            
            Dependency = jobHandle2;
        }


        private struct ConnectPosition
        {
            public Entity edge;
            public NetCompositionLane compositionLane;
            public float order;
            public float3 position;
            public float3 direction;
            public bool isTwoWay;
            public bool isHighway;
            public VehicleGroup vehicleGroup;
            // public ConnectionType supportedType;
        }
    }
}
