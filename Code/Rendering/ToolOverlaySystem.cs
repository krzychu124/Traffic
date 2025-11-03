using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Traffic.Components.PrioritySigns;
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
        private PriorityToolSystem _priorityToolSystem;
        private ConnectorColorSet _colorSet;
        private EntityQuery _connectorsQuery;
        private EntityQuery _connectionsQuery;
        private EntityQuery _editIntersectionQuery;
        private EntityQuery _toolFeedbackQuery;
        private EntityQuery _laneHandlesQuery;
        private ToolOverlayParameterData _defaultOverlayParams;
#if DEBUG_GIZMO
        // private EntityQuery _modifiedConnectionsQuery;
        // private LaneConnectorDebugSystem _laneConnectorDebugSystem;
#endif
    
        protected override void OnCreate() {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _laneConnectorToolSystem = World.GetOrCreateSystemManaged<LaneConnectorToolSystem>();
            _priorityToolSystem = World.GetOrCreateSystemManaged<PriorityToolSystem>();
            _overlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
#if DEBUG_GIZMO
            // _laneConnectorDebugSystem = World.GetExistingSystemManaged<LaneConnectorDebugSystem>();
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
            _laneHandlesQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new []{ ComponentType.ReadOnly<LaneHandle>() },
                None = new []{ ComponentType.ReadOnly<Deleted>(), },
            });
            _editIntersectionQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new []{ ComponentType.ReadOnly<EditIntersection>() },
                None = new [] {ComponentType.ReadOnly<Deleted>()}
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
                outlineBikeSourceColor = new Color(0f, 1f, 0.43f),
                fillBikeSourceColor = new Color(1f, 1f, 1f, 0.92f),
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
            SetDefautlOverlayParams(out _defaultOverlayParams);
            
            RequireAnyForUpdate(_connectorsQuery, _connectionsQuery, _editIntersectionQuery, _toolFeedbackQuery);
        }

        protected override void OnUpdate() {
            JobHandle jobHandle = default;

            if (_toolSystem.activeTool == _laneConnectorToolSystem || _toolSystem.activeTool == _priorityToolSystem)
            {
                ToolOverlayParameterData overlayParameters = SystemAPI.GetSingleton<ToolOverlayParameterData>();
                if (!_editIntersectionQuery.IsEmptyIgnoreFilter)
                {
                    jobHandle = new HighlightIntersectionJob()
                    {
                        editIntersectionTypeHandle = SystemAPI.GetComponentTypeHandle<EditIntersection>(true),
                        tempComponentTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                        toolActionBlockedComponentTypeHandle = SystemAPI.GetComponentTypeHandle<ToolActionBlocked>(true),
                        editPrioritiesTypeHandle = SystemAPI.GetComponentTypeHandle<EditPriorities>(true),
                        connectedEdgeData = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                        edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                        nodeData = SystemAPI.GetComponentLookup<Node>(true),
                        edgeGeometryData = SystemAPI.GetComponentLookup<EdgeGeometry>(true),
                        startNodeGeometryData = SystemAPI.GetComponentLookup<StartNodeGeometry>(true),
                        endNodeGeometryData = SystemAPI.GetComponentLookup<EndNodeGeometry>(true),
                        lineWidth = overlayParameters.feedbackLinesWidth * 0.75f,
                        overlayBuffer = _overlayRenderSystem.GetBuffer(out JobHandle overlayRenderJobHandle)
                    }.Schedule(_editIntersectionQuery, JobHandle.CombineDependencies(Dependency, overlayRenderJobHandle));
                    _overlayRenderSystem.AddBufferWriter(jobHandle);
                }

                if (_toolSystem.activeTool == _priorityToolSystem)
                {
                    if (!_laneHandlesQuery.IsEmptyIgnoreFilter && _priorityToolSystem.ToolState == PriorityToolSystem.State.ChangingPriority)
                    {
                        NativeArray<ArchetypeChunk> chunks = _laneHandlesQuery.ToArchetypeChunkArray(Allocator.TempJob);
                        jobHandle = new PriorityOverlaysJob()
                        {
                            chunks = chunks,
                            state = _priorityToolSystem.ToolState,
                            setMode = _priorityToolSystem.ToolSetMode,
                            overlayMode = _priorityToolSystem.ToolOverlayMode,
                            entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                            laneHandleTypeHandle = SystemAPI.GetComponentTypeHandle<LaneHandle>(true),
                            laneHandleData = SystemAPI.GetComponentLookup<LaneHandle>(true),
                            connectionBufferData = SystemAPI.GetBufferLookup<Connection>(true),
                            controlPoints = _priorityToolSystem.GetControlPoints(out JobHandle controlPointsHandle),
                            alwaysShowConnections = overlayParameters.showLaneConnectionsPriority,
                            overlayBuffer = _overlayRenderSystem.GetBuffer(out JobHandle overlayHandle)
                        }.Schedule(JobHandle.CombineDependencies(jobHandle, controlPointsHandle, overlayHandle));
                        _overlayRenderSystem.AddBufferWriter(jobHandle);
                        chunks.Dispose(jobHandle);
                    }
                } 
                else if (_toolSystem.activeTool == _laneConnectorToolSystem)
                {

                    if (_laneConnectorToolSystem.ToolState > LaneConnectorToolSystem.State.Default)
                    {
                        ActionOverlayData actionOverlayData = SystemAPI.GetSingleton<ActionOverlayData>();
                        if (!(_laneConnectorToolSystem.UIDisabled && actionOverlayData.mode == 0))
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
                                connectionWidth = overlayParameters.laneConnectorLineWidth,
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
                            connectorSize = overlayParameters.laneConnectorSize,
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
            }

            if (!_toolFeedbackQuery.IsEmptyIgnoreFilter)
            {
                ToolOverlayParameterData overlayParameters = SystemAPI.GetSingleton<ToolOverlayParameterData>();
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
                    lineWidth = overlayParameters.feedbackLinesWidth,
                    overlayBuffer = _overlayRenderSystem.GetBuffer(out JobHandle overlayRenderJobHandle2)
                };
                JobHandle deps3 = JobHandle.CombineDependencies(jobHandle, overlayRenderJobHandle2);
                jobHandle = feedbackOverlayJob.Schedule(_toolFeedbackQuery, deps3);
                _overlayRenderSystem.AddBufferWriter(jobHandle);
            }
            
            Dependency = jobHandle;
        }

        public void SetDefautlOverlayParams(out ToolOverlayParameterData data)
        {
            
            data = new ToolOverlayParameterData()
            {
                laneConnectorSize = 1f,
                laneConnectorLineWidth = 0.4f,
                feedbackLinesWidth = 0.3f,
                showLaneConnectionsPriority = false,
            };
            if (SystemAPI.HasSingleton<ToolOverlayParameterData>())
            {
                SystemAPI.SetSingleton(_defaultOverlayParams);
            }
            else
            {
                Entity entity = EntityManager.CreateEntity(typeof(ToolOverlayParameterData));
                EntityManager.SetComponentData(entity, _defaultOverlayParams);
            }
        }

        public void ApplyOverlayParams(ToolOverlayParameterData parameters)
        {
            RefRW<ToolOverlayParameterData> currentData = SystemAPI.GetSingletonRW<ToolOverlayParameterData>();
            if (currentData.IsValid)
            {
                currentData.ValueRW.feedbackLinesWidth = parameters.feedbackLinesWidth;
                currentData.ValueRW.laneConnectorSize = parameters.laneConnectorSize;
                currentData.ValueRW.laneConnectorLineWidth = parameters.laneConnectorLineWidth;
                currentData.ValueRW.showLaneConnectionsPriority = parameters.showLaneConnectionsPriority;
            }
        }

        //todo move to a system, make adjustable via options or expose as theme?
        private struct ConnectorColorSet
        {
            public Color fillActiveColor;
            public Color outlineActiveColor;
            public Color fillSourceColor;
            public Color outlineSourceColor;
            public Color fillBikeSourceColor;
            public Color outlineBikeSourceColor;
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
