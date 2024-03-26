using Colossal.Entities;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Rendering;
using Game.Tools;
using Traffic.Components;
using Traffic.Debug;
using Traffic.LaneConnections;
using Traffic.Tools;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Traffic.Rendering
{
    public partial class ToolOverlaySystem : GameSystemBase
    {
        private ToolSystem _toolSystem;
        private OverlayRenderSystem _overlayRenderSystem;
        private LaneConnectorToolSystem _laneConnectorToolSystem;
        // private EntityQuery _queryConnections;
        private EntityQuery _editIntersectionQuery;
        // private EntityQuery _definitionsQuery;
        private EntityQuery _connectorsQuery;
        private EntityQuery _connectionsQuery;
        private EntityQuery _modifiedConnectionsQuery;
        private ConnectorColorSet _colorSet;
#if DEBUG_GIZMO
        private LaneConnectorDebugSystem _laneConnectorDebugSystem;
#endif
    
        protected override void OnCreate() {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _laneConnectorToolSystem = World.GetOrCreateSystemManaged<LaneConnectorToolSystem>();
            _overlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
#if DEBUG_GIZMO
            _laneConnectorDebugSystem = World.GetExistingSystemManaged<LaneConnectorDebugSystem>();
#endif
            // _queryConnections = new EntityQueryBuilder(Allocator.Temp)
            //     .WithAll<LaneConnectionDefinition>()
            //     .WithNone<Hidden, Deleted>()
            //     .Build(EntityManager);
            _editIntersectionQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new []{ ComponentType.ReadOnly<EditIntersection>() },
                None = new [] {ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>()}
            });
            // _definitionsQuery = GetEntityQuery(new EntityQueryDesc()
            // {
            //     All = new []{ ComponentType.ReadOnly<CreationDefinition>() },
            //     Any = new []{ ComponentType.ReadOnly<NodeDefinition>() },
            // });
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
            
            _modifiedConnectionsQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new []{ ComponentType.ReadOnly<CreationDefinition>(), ComponentType.ReadOnly<ConnectionDefinition>(), ComponentType.ReadOnly<TempLaneConnection>(),  },
                None = new []{ ComponentType.ReadOnly<Deleted>(), },
            });

            _colorSet = new ConnectorColorSet
            {
                fillActiveColor = Color.clear,
                outlineActiveColor = Color.white,
                fillSourceColor = Color.clear,
                outlineSourceColor = new Color(0f, 0.83f, 1f, 0.85f),
                fillSourceTrackColor = Color.clear,
                outlineSourceTrackColor = new Color(0.87f, 0.6f, 0.26f, 0.85f),
                outlineSourceMixedColor = new Color(0.87f, 0.4f, 0.43f, 0.85f),
                fillTargetColor = Color.clear,
                outlineTargetColor = new Color(0f, 0.56f, 0.87f, 0.85f),
                fillTargetTrackColor = Color.clear,
                outlineTargetTrackColor = new Color(0.87f, 0.39f, 0.16f, 0.85f),
                outlineTargetMixedColor = new Color(0.87f, 0.11f, 0.32f, 0.85f),
                fillTwoWayColor = Color.clear,
                outlineTwoWayColor =  new Color(1f, 0.92f, 0.02f, 0.85f),
            };
            // RequireForUpdate(_definitionsQuery);
        }

        protected override void OnUpdate() {
            if (_toolSystem.activeTool == _laneConnectorToolSystem && !_laneConnectorToolSystem.UIDisabled)
            {
                JobHandle jobHandle = default;
                /*LaneConnectorOverlayJob laneConnectorOverlayJob = new LaneConnectorOverlayJob
                {
                    editIntersectionType = SystemAPI.GetComponentTypeHandle<EditIntersection>(true),
                    nodes = SystemAPI.GetComponentLookup<Game.Net.Node>(true),
                    nodesGeometry = SystemAPI.GetComponentLookup<Game.Net.NodeGeometry>(true),
                    definitionChunks = _editIntersectionQuery.ToArchetypeChunkListAsync(Allocator.TempJob, out JobHandle outJobHandle),
                    controlPoints = _laneConnectorToolSystem.GetControlPoints(out JobHandle controlPointsJobHandle),
                    overlayBuffer = _overlayRenderSystem.GetBuffer(out JobHandle overlayRenderJobHandle),
                    state = _laneConnectorToolSystem.ToolState,
                };
                JobHandle deps = JobHandle.CombineDependencies(controlPointsJobHandle, overlayRenderJobHandle);
                jobHandle = laneConnectorOverlayJob.Schedule(JobHandle.CombineDependencies(Dependency, outJobHandle, deps));
                laneConnectorOverlayJob.definitionChunks.Dispose(jobHandle);
                _overlayRenderSystem.AddBufferWriter(jobHandle);*/

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
                
                if (!_connectionsQuery.IsEmptyIgnoreFilter && _laneConnectorToolSystem.ToolState > LaneConnectorToolSystem.State.Default)
                {
#if DEBUG_GIZMO
                    bool overlays = !_laneConnectorDebugSystem.GizmoEnabled;
#else
                    const bool overlays = true;
#endif

                    if (overlays)
                    {
                        ConnectionsOverlayJob connectionsOverlayJob = new ConnectionsOverlayJob
                        {
                            curveData = SystemAPI.GetComponentLookup<Curve>(true),
                            connectorsData = SystemAPI.GetComponentLookup<Connector>(true),
                            nodeData = SystemAPI.GetComponentLookup<Node>(true),
                            connectionType = SystemAPI.GetBufferTypeHandle<Connection>(true),
                            state = _laneConnectorToolSystem.ToolState,
                            modifier = _laneConnectorToolSystem.ToolModifiers,
                            controlPoints = _laneConnectorToolSystem.GetControlPoints(out JobHandle controlPointsJobHandle3),
                            colorSet = _colorSet,
                            overlayBuffer = _overlayRenderSystem.GetBuffer(out JobHandle overlayRenderJobHandle3)
                        };
                        JobHandle deps3 = JobHandle.CombineDependencies(jobHandle, JobHandle.CombineDependencies(controlPointsJobHandle3, overlayRenderJobHandle3));
                        jobHandle = connectionsOverlayJob.Schedule(_connectionsQuery, deps3);
                        _overlayRenderSystem.AddBufferWriter(jobHandle);
                    }
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

                Dependency = jobHandle;
            }
        }
        
        private struct LaneConnectorOverlayJob : IJob
        {
            [ReadOnly] public ComponentTypeHandle<EditIntersection> editIntersectionType;
            [ReadOnly] public ComponentLookup<Game.Net.Node> nodes;
            [ReadOnly] public ComponentLookup<Game.Net.NodeGeometry> nodesGeometry;
            [ReadOnly] public NativeList<ArchetypeChunk> definitionChunks;
            [ReadOnly] public NativeList<ControlPoint> controlPoints;
            [ReadOnly] public LaneConnectorToolSystem.State state;
            public OverlayRenderSystem.Buffer overlayBuffer;
        
            public void Execute() {
                Color selectColor = new Color(0f, 0.54f, 1f);//move to settings...
                Color highlightColor = new Color(0f, 1f, 0.26f, 0.5f);
                for (int i = 0; i < definitionChunks.Length; i++)
                {
                    ArchetypeChunk archetypeChunk = definitionChunks[i];
                    NativeArray<EditIntersection> editIntersections = archetypeChunk.GetNativeArray(ref editIntersectionType);
                    if (editIntersections.Length != 0)
                    {
                        for (int j = 0; j < editIntersections.Length; j++)
                        {
                            EditIntersection editIntersection = editIntersections[i];
                            Entity nodeEntity = editIntersection.node;
                            if (nodeEntity != Entity.Null && nodes.HasComponent(nodeEntity))
                            {
                                Node node = nodes[nodeEntity];
                                float diameter = 10f;
                                float3 position = node.m_Position;
                                if (nodesGeometry.HasComponent(nodeEntity))
                                {
                                    diameter = MathUtils.Size(nodesGeometry[nodeEntity].m_Bounds).x + 5f;
                                }

                                overlayBuffer.DrawCircle(
                                    selectColor,
                                    Color.clear,
                                    0.25f,
                                    0,
                                    new float2(0.0f, 1f),
                                    position,
                                    diameter);
                            }
                        }
                    }
                }
                
                if (controlPoints.Length != 1 || state != LaneConnectorToolSystem.State.Default)
                {
                    return;
                }
                
                ControlPoint p = controlPoints[0];
                if (!nodes.HasComponent(p.m_OriginalEntity))
                {
                    return;
                }
                Node node2 = nodes[p.m_OriginalEntity];
                float diameter2 = 10f;
                if (nodesGeometry.HasComponent(p.m_OriginalEntity))
                {
                    diameter2 = MathUtils.Size(nodesGeometry[p.m_OriginalEntity].m_Bounds).x + 5f;
                }
                float3 position2 = node2.m_Position;
                overlayBuffer.DrawCircle(highlightColor, Color.clear, 0.25f, 0, new float2(0.0f, 1f), position2, diameter2);
            }
        }

        private struct ConnectorsOverlayJob : IJob
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public NativeList<ArchetypeChunk> connectorDataChunks;
            [ReadOnly] public ComponentTypeHandle<Connector> connectorType;
            [ReadOnly] public ComponentLookup<Connector> connectorData;
            [ReadOnly] public LaneConnectorToolSystem.State state;
            [ReadOnly] public LaneConnectorToolSystem.StateModifier modifier;
            [ReadOnly] public ConnectorColorSet colorSet;
            [ReadOnly] public NativeList<ControlPoint> controlPoints;
            public OverlayRenderSystem.Buffer overlayBuffer;

            public void Execute() {
                bool renderSource = state == LaneConnectorToolSystem.State.SelectingSourceConnector;
                bool renderTarget = state == LaneConnectorToolSystem.State.SelectingTargetConnector;
                
                Entity source = Entity.Null;
                Entity target = Entity.Null;
                Connector sourceConnector = default;
                if (controlPoints.Length > 0)
                {
                    source = controlPoints[0].m_OriginalEntity;
                    if (connectorData.HasComponent(source))
                    {
                        sourceConnector = connectorData[source];
                    }
                    if (controlPoints.Length > 1)
                    { 
                        target = controlPoints[1].m_OriginalEntity;
                    }
                }
                for (int i = 0; i < connectorDataChunks.Length; i++)
                {
                    ArchetypeChunk chunk = connectorDataChunks[i];
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                    NativeArray<Connector> connectors = chunk.GetNativeArray(ref connectorType);
                    for (int j = 0; j < connectors.Length; j++)
                    {
                        Entity entity = entities[j];
                        bool isSource = entity == source;
                        bool isTarget = entity == target && renderTarget;
                        float diameter = isSource || isTarget ? 1.2f : 1f;
                        float outline = isSource || isTarget ? 0.25f : 0.2f;
                        Connector connector = connectors[j];
                        if (IsNotMatchingModifier(modifier, connector))
                        {
                            continue;
                        }
                        
                        float3 position = connector.position;
                        if ((connector.connectorType & ConnectorType.Source) != 0 && (renderSource || isSource))
                        {
                            overlayBuffer.DrawCircle(
                                connector.connectionType == ConnectionType.SharedCarTrack 
                                    ? colorSet.outlineSourceMixedColor : connector.connectionType == ConnectionType.Track 
                                        ? colorSet.outlineSourceTrackColor : colorSet.outlineSourceColor,
                                colorSet.fillSourceColor,
                                outline,
                                0,//OverlayRenderSystem.StyleFlags.Projected,
                                new float2(0.0f, 1f),
                                position,
                                diameter);
                        }
                        else if ((connector.connectorType & ConnectorType.Target) != 0 && renderTarget)
                        {
                            overlayBuffer.DrawCircle(
                                connector.connectionType == ConnectionType.SharedCarTrack 
                                    ? colorSet.outlineTargetMixedColor : connector.connectionType == ConnectionType.Track 
                                        ? colorSet.outlineTargetTrackColor : colorSet.outlineTargetColor,
                                colorSet.fillTargetColor,
                                outline,
                                0,//OverlayRenderSystem.StyleFlags.Projected,
                                new float2(0.0f, 1f),
                                position,
                                diameter);
                        } 
                        else if ((connector.connectorType & ConnectorType.TwoWay) != 0)
                        {
                            overlayBuffer.DrawCircle(
                                colorSet.outlineTwoWayColor,
                                colorSet.fillTwoWayColor,
                                outline,
                                0,//OverlayRenderSystem.StyleFlags.Projected,
                                new float2(0.0f, 1f),
                                position,
                                diameter);
                        }
                    }
                }  
            }

            private bool IsNotMatchingModifier(LaneConnectorToolSystem.StateModifier stateModifier, Connector connector) {
                return stateModifier == LaneConnectorToolSystem.StateModifier.TrackOnly && (connector.connectionType & (ConnectionType.Track)) == 0 ||
                    stateModifier == LaneConnectorToolSystem.StateModifier.RoadOnly && (connector.connectionType & (ConnectionType.Road)) == 0 ||
                    stateModifier == LaneConnectorToolSystem.StateModifier.SharedRoadTrack && (connector.connectionType & ConnectionType.SharedCarTrack) != ConnectionType.SharedCarTrack;
            }
        }

        private struct ConnectionsOverlayJob : IJobChunk
        {
            [ReadOnly] public ComponentLookup<Curve> curveData;
            [ReadOnly] public ComponentLookup<Connector> connectorsData;
            [ReadOnly] public ComponentLookup<Node> nodeData;
            [ReadOnly] public BufferTypeHandle<Connection> connectionType;
            [ReadOnly] public LaneConnectorToolSystem.State state;
            [ReadOnly] public LaneConnectorToolSystem.StateModifier modifier;
            [ReadOnly] public NativeList<ControlPoint> controlPoints;
            [ReadOnly] public ConnectorColorSet colorSet;
            public OverlayRenderSystem.Buffer overlayBuffer;
            
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {

                Entity connector = Entity.Null;
                Entity connector2 = Entity.Null;
                ControlPoint targetControlPoint = default;
                bool floatingPosition = false;
                bool previewConnection = false;
                if (controlPoints.Length > 0)
                {
                    connector = controlPoints[0].m_OriginalEntity;
                    if (state == LaneConnectorToolSystem.State.SelectingTargetConnector && controlPoints.Length > 1)
                    {
                        targetControlPoint = controlPoints[1];
                        connector2 = controlPoints[1].m_OriginalEntity;
                    }
                }
                PathNode connectorNode = new PathNode();
                PathNode connectorNode2 = new PathNode();
                if (connector != Entity.Null)
                {
                    Connector c = connectorsData[connector];
                    connectorNode = new PathNode(c.edge, (ushort)c.laneIndex);
                    if (connector2 != Entity.Null && state == LaneConnectorToolSystem.State.SelectingTargetConnector && connectorsData.HasComponent(connector2))
                    {
                        Connector c2 = connectorsData[connector2];
                        connectorNode2 = new PathNode(c2.edge, (ushort)c2.laneIndex);
                        previewConnection = true;
                    }
                    floatingPosition = state == LaneConnectorToolSystem.State.SelectingTargetConnector && connector != connector2;//TODO bug
                }
                Color dimmMain = state == LaneConnectorToolSystem.State.SelectingTargetConnector ? new Color(1f, 1f, 1f, 0.9f) : new Color(1f, 1f, 1f, 0.75f);
                BufferAccessor<Connection> connectionAccessor = chunk.GetBufferAccessor(ref connectionType);
                NativeList<ConnectionRenderData> hovered = new NativeList<ConnectionRenderData>(Allocator.Temp);
                Color dimm;
                LaneConnectorToolSystem.StateModifier currentModifier = modifier & ~LaneConnectorToolSystem.StateModifier.MakeUnsafe;
                    
                for (var i = 0; i < connectionAccessor.Length; i++)
                {
                    
                    DynamicBuffer<Connection> connections = connectionAccessor[i];
                    foreach (Connection connection in connections)
                    {
                        // if (!curveData.HasComponent(connection.curve))
                        // {
                        //     // TODO FIX_ME delete removed connections!
                        //     continue;
                        // }
                        
                        Color color = (connection.method & PathMethod.Track) != 0 ? colorSet.outlineTwoWayColor : connection.isForbidden ? new Color(0.81f, 0f, 0.14f, 0.79f) : colorSet.outlineSourceColor;
                        Color color2 = (connection.method & PathMethod.Track) != 0 ? colorSet.fillTwoWayColor : colorSet.fillSourceColor;
                        float width = connection.isForbidden ? 0.2f : connection.isUnsafe ? 0.35f : 0.5f;
                        Bezier4x3 curve = connection.curve;
                        if (IsNotMatchingModifier(currentModifier, connection))
                        {
                            dimm = new Color(1, 1, 1, 0.3f);
                        }
                        else
                        {
                            dimm = connection.isUnsafe ? new Color(1f, 1f, 1f, 0.65f) : dimmMain;
                        }
                        
                        if (AreEqual(connectorNode, connection.sourceNode))
                        {
                            bool isTarget = state == LaneConnectorToolSystem.State.SelectingTargetConnector && AreEqual(connectorNode2, connection.targetNode);
                            if (isTarget)
                            {
                                floatingPosition = false;
                            }
                            color = (state == LaneConnectorToolSystem.State.SelectingSourceConnector || AreEqual(connectorNode2, connection.targetNode)) 
                                ? state == LaneConnectorToolSystem.State.SelectingTargetConnector 
                                    ? new Color(1f, 0f, 0.15f, 0.9f) 
                                    : connection.isForbidden ? new Color(1f, 0f, 0.15f, 0.9f) : colorSet.outlineActiveColor 
                                : connection.isForbidden ? new Color(1f, 0f, 0.15f, 0.9f) : colorSet.outlineActiveColor * dimm;
                            color2 = Color.clear;
                            width = connection.isForbidden ? 0.25f : connection.isUnsafe ? 0.35f : 0.55f;
                            hovered.Add(new ConnectionRenderData() {bezier = curve, color = color, color2 = color2, width = width, isUnsafe = connection.isUnsafe, isForbidden = connection.isForbidden});
                            continue;
                        }
                        else if (AreEqual(connectorNode, connection.targetNode))
                        {
                            color = connection.isForbidden ? new Color(1f, 0f, 0.15f, 0.9f) : new Color(0.75f, 0f, 0.34f);
                            color2 = Color.clear;
                            width = connection.isForbidden ? 0.25f : connection.isUnsafe ? 0.35f : 0.55f;
                            hovered.Add(new ConnectionRenderData() {bezier = curve, color = color, color2 = color2, width = width, isUnsafe = connection.isUnsafe, isForbidden = connection.isForbidden});
                            continue;
                        }
                        if (connection.isUnsafe || connection.isForbidden)
                        {
                            float outline = 0;
                            if ((connection.method & (PathMethod.Road | PathMethod.Track)) == (PathMethod.Road | PathMethod.Track))
                            {
                                width = width * 1.33f;
                                outline = width / 3;
                                color2 = color;
                                color = colorSet.outlineSourceColor;
                            }
                            overlayBuffer.DrawDashedCurve(color2 * dimm, color * dimm, outline, 0, curve, width, 1.2f, 0.4f);
                        }
                        else
                        {
                            float outline = 0;
                            if ((connection.method & (PathMethod.Road | PathMethod.Track)) == (PathMethod.Road | PathMethod.Track))
                            {
                                width = width * 1.33f;
                                outline = width / 3;
                                color2 = color;
                                color = colorSet.outlineSourceColor;
                            }
                            overlayBuffer.DrawCurve(color2 * dimm, color * dimm, outline, 0, curve, width, float2.zero);
                        }
                    }
                }
                if (!hovered.IsEmpty)
                {
                    for (int i = 0; i < hovered.Length; i++)
                    {
                        ConnectionRenderData data = hovered[i];
                        if (data.isUnsafe || data.isForbidden)
                        {
                            overlayBuffer.DrawDashedCurve(data.color2, data.color, 0f, 0, data.bezier, data.width * 0.75f, 1.2f, 0.4f);
                        }
                        else
                        {
                            overlayBuffer.DrawCurve(data.color2, data.color, 0f, 0, data.bezier, data.width, float2.zero);
                        }
                    }
                }
                hovered.Dispose();

                if (floatingPosition)
                {
                    bool isUnsafe = (modifier & LaneConnectorToolSystem.StateModifier.MakeUnsafe) != 0;
                    if (!previewConnection)
                    {
                        Connector startCon = connectorsData[connector];
                        float3 cursorPos = targetControlPoint.m_Position;
                        if (targetControlPoint.m_OriginalEntity == Entity.Null || math.distancesq(cursorPos.xz, startCon.position.xz) < 1)
                        {
                            return;
                        }
                        float3 middlePos = nodeData[startCon.node].m_Position;
                        cursorPos.y = startCon.position.y;
                        Bezier4x3 floatingBezier = NetUtils.FitCurve(new Line3.Segment(startCon.position, startCon.position + (startCon.direction * 2f)), new Line3.Segment(cursorPos, middlePos));
                        if (isUnsafe)
                        {
                            overlayBuffer.DrawDashedCurve(Color.yellow, Color.yellow, 0f, 0, floatingBezier, 0.40f, 1f, 0.45f);
                        }
                        else
                        {
                            overlayBuffer.DrawCurve(Color.yellow, Color.yellow, 0f, 0, floatingBezier, 0.45f, float2.zero);
                        }
                    }
                    else if (connectorsData.TryGetComponent(connector2, out Connector t) && t.connectorType == ConnectorType.Target)
                    {
                        Connector s = connectorsData[connector];
                        Bezier4x3 connectionBezier = NetUtils.FitCurve(s.position, s.direction, -t.direction, t.position);
                        if (isUnsafe)
                        {
                            overlayBuffer.DrawDashedCurve(new Color(0.38f, 1f, 0f), new Color(0.38f, 1f, 0f), 0f, 0, connectionBezier, 0.45f, 1.2f, 0.4f);
                        }
                        else
                        {
                            overlayBuffer.DrawCurve(new Color(0.38f, 1f, 0f), new Color(0.38f, 1f, 0f), 0f, 0, connectionBezier, 0.45f, float2.zero);
                        }
                    }
                }
            }

            private bool IsNotMatchingModifier(LaneConnectorToolSystem.StateModifier stateModifier, Connection connection) {
                return stateModifier == LaneConnectorToolSystem.StateModifier.TrackOnly && (connection.method & (PathMethod.Road | PathMethod.Track)) != PathMethod.Track ||
                    stateModifier == LaneConnectorToolSystem.StateModifier.RoadOnly && (connection.method & (PathMethod.Road | PathMethod.Track)) != PathMethod.Road ||
                    stateModifier == LaneConnectorToolSystem.StateModifier.SharedRoadTrack && (connection.method & (PathMethod.Road | PathMethod.Track)) != (PathMethod.Road | PathMethod.Track);
            }

            private bool AreEqual(PathNode node1, PathNode node2) {
                return node1.OwnerEquals(node2) && (node1.GetLaneIndex() & 0xff) == (node2.GetLaneIndex() & 0xff);
            }
        }


        private struct ModifiedConnectionsOverlayJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<ConnectionDefinition> connectionDefinitionDataTypeHandle;
            [ReadOnly] public BufferTypeHandle<TempLaneConnection> tempConnectionsDataTypeHandle;
            [ReadOnly] public LaneConnectorToolSystem.State state;
            [ReadOnly] public LaneConnectorToolSystem.StateModifier modifier;
            [ReadOnly] public ConnectorColorSet colorSet;
            public OverlayRenderSystem.Buffer overlayBuffer;
            
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                NativeArray<ConnectionDefinition> connectionDefinitions = chunk.GetNativeArray(ref connectionDefinitionDataTypeHandle);
                BufferAccessor<TempLaneConnection> tempConnectionsAccessor = chunk.GetBufferAccessor(ref tempConnectionsDataTypeHandle);

                for (var i = 0; i < connectionDefinitions.Length; i++)
                {
                    // ConnectionDefinition definition = connectionDefinitions[i];
                    DynamicBuffer<TempLaneConnection> connections = tempConnectionsAccessor[i];
                    
                    for (var j = 0; j < connections.Length; j++)
                    {
                        TempLaneConnection connection = connections[j];
                        Color color;
                        Color fillColor;
                        if ((connection.flags & ConnectionFlags.Highlight) != 0)
                        {
                            color = new Color(1f, 0.26f, 0.01f);
                            fillColor = new Color(1f, 0.26f, 0.01f);
                        }
                        else if ((connection.flags & ConnectionFlags.Create) != 0)
                        {
                            color = new Color(0.53f, 0f, 1f);
                            fillColor = new Color(0.53f, 0f, 1f);
                        }
                        else
                        {
                            color = new Color(0f, 0.12f, 1f);
                            fillColor = new Color(0f, 0.12f, 1f);
                        }
                        if (connection.isUnsafe)
                        {
                            overlayBuffer.DrawDashedCurve(color, fillColor, 0f, 0, connection.bezier, 0.3f, 1.2f, 0.4f);
                        }
                        else
                        {
                            overlayBuffer.DrawCurve(color, fillColor, 0f, 0, connection.bezier, 0.3f, float2.zero);
                        }
                    }
                }
            }
        }

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

    internal struct ConnectionRenderData
    {
        public Color color;
        public Color color2;
        public Bezier4x3 bezier;
        public float width;
        public bool isUnsafe;
        public bool isForbidden;
    }
}
