using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Traffic.Tools;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;
using Edge = Game.Net.Edge;

namespace Traffic.Rendering
{
#if WITH_BURST
    [BurstCompile]
#endif
    public partial class ToolOverlaySystem : GameSystemBase
    {
        private ToolSystem _toolSystem;
        private OverlayRenderSystem _overlayRenderSystem;
        private LaneConnectorToolSystem _laneConnectorToolSystem;
        private ConnectorColorSet _colorSet;
        private EntityQuery _connectorsQuery;
        private EntityQuery _connectionsQuery;
        private EntityQuery _toolFeedbackQuery;
#if DEBUG_GIZMO
        // private EntityQuery _modifiedConnectionsQuery;
        // private EntityQuery _editIntersectionQuery;
        private LaneConnectorDebugSystem _laneConnectorDebugSystem;
#endif
    
        protected override void OnCreate() {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _laneConnectorToolSystem = World.GetOrCreateSystemManaged<LaneConnectorToolSystem>();
            _overlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
#if DEBUG_GIZMO
            _laneConnectorDebugSystem = World.GetExistingSystemManaged<LaneConnectorDebugSystem>();
            // _editIntersectionQuery = GetEntityQuery(new EntityQueryDesc()
            // {
            //     All = new []{ ComponentType.ReadOnly<EditIntersection>() },
            //     None = new [] {ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>()}
            // });
            // _modifiedConnectionsQuery = GetEntityQuery(new EntityQueryDesc()
            // {
            //     All = new []{ ComponentType.ReadOnly<CreationDefinition>(), ComponentType.ReadOnly<ConnectionDefinition>(), ComponentType.ReadOnly<TempLaneConnection>(),  },
            //     None = new []{ ComponentType.ReadOnly<Deleted>(), },
            // });
#endif
            _connectorsQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new []{ ComponentType.ReadOnly<Connector>() },
                None = new []{ ComponentType.ReadOnly<Deleted>() },
            });
            _connectionsQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new []{ ComponentType.ReadOnly<Connection>() },
                None = new []{ ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<CustomLaneConnection>(), },
            });
            _toolFeedbackQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new []{ ComponentType.ReadOnly<ToolFeedbackInfo>() },
                None = new []{ ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Error>() },
            });
            
            _colorSet = new ConnectorColorSet
            {
                fillActiveColor = new Color(1f, 1f, 1f, 0.92f),
                outlineActiveColor = new Color(1f, 1f, 1f, 0.92f),
                fillSourceColor =new Color(0f, 0.83f, 1f, 1f),
                outlineSourceColor = new Color(0f, 0.83f, 1f, 1f),
                fillSourceTrackColor = Color.clear,
                outlineSourceTrackColor = new Color(0.87f, 0.6f, 0.26f, 1f),
                outlineSourceMixedColor = new Color(0.87f, 0.4f, 0.43f, 1f),
                fillTargetColor = Color.clear,
                outlineTargetColor = new Color(0f, 0.56f, 0.87f, 1f),
                fillTargetTrackColor = Color.clear,
                outlineTargetTrackColor = new Color(0.87f, 0.39f, 0.16f, 1f),
                outlineTargetMixedColor = new Color(0.87f, 0.11f, 0.32f, 1f),
                fillTwoWayColor = Color.clear,
                outlineTwoWayColor =  new Color(1f, 0.92f, 0.02f, 1f),
            };
            RequireAnyForUpdate(_connectorsQuery, _connectionsQuery, _toolFeedbackQuery);
        }

        protected override void OnUpdate() {
            JobHandle jobHandle = default;
            if (_toolSystem.activeTool == _laneConnectorToolSystem)
            {
                if (_laneConnectorToolSystem.ToolState > LaneConnectorToolSystem.State.Default)
                {
#if DEBUG_GIZMO
                    bool overlays = !_laneConnectorDebugSystem.GizmoEnabled;
#else
                    const bool overlays = true;
#endif

                    ActionOverlayData actionOverlayData = SystemAPI.GetSingleton<ActionOverlayData>();
                    if (overlays && !(_laneConnectorToolSystem.UIDisabled && actionOverlayData.mode == 0))
                    {
                        NativeArray<ArchetypeChunk> chunks = _connectionsQuery.ToArchetypeChunkArray(Allocator.TempJob);
                        ConnectionsOverlayJob connectionsOverlayJob = new ConnectionsOverlayJob
                        {
                            chunks = chunks,
                            connectorsData = SystemAPI.GetComponentLookup<Connector>(true),
                            nodeData = SystemAPI.GetComponentLookup<Node>(true),
                            connectionType = SystemAPI.GetBufferTypeHandle<Connection>(true),
                            state = _laneConnectorToolSystem.ToolState,
                            modifier = _laneConnectorToolSystem.ToolModifiers,
                            actionOverlayData = actionOverlayData,
                            controlPoints = _laneConnectorToolSystem.GetControlPoints(out JobHandle controlPointsJobHandle3),
                            colorSet = _colorSet,
                            overlayBuffer = _overlayRenderSystem.GetBuffer(out JobHandle overlayRenderJobHandle3)
                        };
                        JobHandle deps3 = JobHandle.CombineDependencies(jobHandle, JobHandle.CombineDependencies(controlPointsJobHandle3, overlayRenderJobHandle3));
                        jobHandle = connectionsOverlayJob.Schedule(deps3);
                        _overlayRenderSystem.AddBufferWriter(jobHandle);
                        chunks.Dispose(jobHandle);
                    }
                }
                
                if (!_connectorsQuery.IsEmptyIgnoreFilter)
                {
                    ConnectorsOverlayJob connectorsOverlayJob = new ConnectorsOverlayJob
                    {
                        entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                        connectorDataChunks = _connectorsQuery.ToArchetypeChunkListAsync(Allocator.TempJob, out JobHandle connectorsChunkJobHandle),
                        connectorData = SystemAPI.GetComponentLookup<Connector>(true),
                        connectorType = SystemAPI.GetComponentTypeHandle<Connector>(true),
                        state = _laneConnectorToolSystem.ToolState,
                        modifier = _laneConnectorToolSystem.ToolModifiers,
                        colorSet = _colorSet,
                        controlPoints = _laneConnectorToolSystem.GetControlPoints(out JobHandle controlPointsJobHandle2),
                        overlayBuffer = _overlayRenderSystem.GetBuffer(out JobHandle overlayRenderJobHandle2)
                    };
                    JobHandle deps2 = JobHandle.CombineDependencies(jobHandle, JobHandle.CombineDependencies(controlPointsJobHandle2, overlayRenderJobHandle2));
                    jobHandle = connectorsOverlayJob.Schedule(JobHandle.CombineDependencies(deps2, connectorsChunkJobHandle));
                    connectorsOverlayJob.connectorDataChunks.Dispose(jobHandle);
                    _overlayRenderSystem.AddBufferWriter(jobHandle);
                }

                /* TODO DEBUG TempGeneratedConnection
                if (!_modifiedConnectionsQuery.IsEmptyIgnoreFilter && _laneConnectorToolSystem.ToolState > LaneConnectorToolSystem.State.Default && !_laneConnectorDebugSystem.GizmoEnabled)
                {
                    ModifiedConnectionsOverlayJob modifiedConnectionsOverlayJob = new ModifiedConnectionsOverlayJob()
                    {
                        connectionDefinitionDataTypeHandle = SystemAPI.GetComponentTypeHandle<ConnectionDefinition>(true),
                        tempConnectionsDataTypeHandle = SystemAPI.GetBufferTypeHandle<TempLaneConnection>(true),
                        state = _laneConnectorToolSystem.ToolState,
                        modifier = _laneConnectorToolSystem.ToolModifiers,
                        colorSet = _colorSet,
                        overlayBuffer = _overlayRenderSystem.GetBuffer(out JobHandle overlayRenderJobHandle4)
                    };
                    JobHandle deps4 = JobHandle.CombineDependencies(jobHandle, overlayRenderJobHandle4);
                    jobHandle = modifiedConnectionsOverlayJob.Schedule(_modifiedConnectionsQuery, deps4);
                    _overlayRenderSystem.AddBufferWriter(jobHandle);
                }*/

            }
            
            if (!_toolFeedbackQuery.IsEmptyIgnoreFilter)
            {
                FeedbackOverlayJob feedbackOverlayJob = new FeedbackOverlayJob()
                {
                    entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                    editIntersectionTypeHandle = SystemAPI.GetComponentTypeHandle<EditIntersection>(true),
                    feedbackInfoTypeHandle = SystemAPI.GetBufferTypeHandle<ToolFeedbackInfo>(true),
                    nodeTypeHandle = SystemAPI.GetComponentTypeHandle<Node>(true),
                    connectedEdgeData = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                    edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                    edgeGeometryData = SystemAPI.GetComponentLookup<EdgeGeometry>(true),
                    startNodeGeometryData = SystemAPI.GetComponentLookup<StartNodeGeometry>(true),
                    endNodeGeometryData = SystemAPI.GetComponentLookup<EndNodeGeometry>(true),
                    netGeometryData = SystemAPI.GetComponentLookup<NetGeometryData>(true),
                    prefabRefData = SystemAPI.GetComponentLookup<PrefabRef>(true),
                    lineWidth = 0.25f,
                    overlayBuffer = _overlayRenderSystem.GetBuffer(out JobHandle overlayRenderJobHandle2)
                };
                JobHandle deps3 = JobHandle.CombineDependencies(jobHandle, overlayRenderJobHandle2);
                jobHandle = feedbackOverlayJob.Schedule(_toolFeedbackQuery, deps3);
                _overlayRenderSystem.AddBufferWriter(jobHandle);
            }
            
            Dependency = jobHandle;
        }

        //todo move to a system, make adjustable via options or expose as theme?
        private struct ConnectorColorSet
        {
            public Color fillActiveColor;
            public Color outlineActiveColor;
            public Color fillSourceColor;
            public Color outlineSourceColor;
            public Color fillSourceTrackColor;
            public Color outlineSourceTrackColor;
            public Color outlineSourceMixedColor;
            public Color fillTargetColor;
            public Color outlineTargetColor;
            public Color fillTargetTrackColor;
            public Color outlineTargetTrackColor;
            public Color outlineTargetMixedColor;
            public Color fillTwoWayColor;
            public Color outlineTwoWayColor;
        }
    }
}
