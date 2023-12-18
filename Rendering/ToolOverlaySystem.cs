using Colossal.Entities;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Rendering;
using Game.Tools;
using Traffic.Components;
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
        private ConnectorColorSet _colorSet;
    
        protected override void OnCreate() {
            base.OnCreate();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _laneConnectorToolSystem = World.GetOrCreateSystemManaged<LaneConnectorToolSystem>();
            _overlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
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

            _colorSet = new ConnectorColorSet
            {
                fillSourceColor = Color.clear,
                outlineSourceColor = new Color(0f, 0.83f, 1f, 0.85f),
                fillTargetColor = Color.clear,
                outlineTargetColor = new Color(0f, 0.52f, 0.87f, 0.85f),
                fillTwoWayColor = Color.clear,
                outlineTwoWayColor =  new Color(1f, 0.92f, 0.02f, 0.85f),
            };
            // RequireForUpdate(_definitionsQuery);
        }

        protected override void OnUpdate() {
            if (_toolSystem.activeTool == _laneConnectorToolSystem)
            {
                JobHandle jobHandle;
                LaneConnectorOverlayJob laneConnectorOverlayJob = new LaneConnectorOverlayJob
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
                _overlayRenderSystem.AddBufferWriter(jobHandle);

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
                                    node.m_Position,
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
                overlayBuffer.DrawCircle(highlightColor, Color.clear, 0.25f, OverlayRenderSystem.StyleFlags.Projected, new float2(0.0f, 1f), node2.m_Position, diameter2);
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
                                colorSet.outlineSourceColor,
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
                                colorSet.outlineTargetColor,
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
                    floatingPosition = state == LaneConnectorToolSystem.State.SelectingTargetConnector && connector != connector2;
                }
                Color dimmMain = state == LaneConnectorToolSystem.State.SelectingTargetConnector ? new Color(1f, 1f, 1f, 0.5f) : Color.white;
                BufferAccessor<Connection> connectionAccessor = chunk.GetBufferAccessor(ref connectionType);
                NativeList<ConnectionRenderData> hovered = new NativeList<ConnectionRenderData>(Allocator.Temp);
                Color dimm;
                
                for (var i = 0; i < connectionAccessor.Length; i++)
                {
                    
                    DynamicBuffer<Connection> connections = connectionAccessor[i];
                    foreach (Connection connection in connections)
                    {
                        if (!curveData.HasComponent(connection.curve))
                        {
                            // TODO FIX_ME delete removed connections!
                            continue;
                        }
                        
                        Color color = (connection.method & PathMethod.Track) != 0 ? colorSet.outlineTwoWayColor : connection.isForbidden ? new Color(0.81f, 0f, 0.14f, 0.79f) : colorSet.outlineSourceColor;
                        Color color2 = (connection.method & PathMethod.Track) != 0 ? colorSet.fillTwoWayColor : colorSet.fillSourceColor;
                        float width = connection.isForbidden ? 0.2f : connection.isUnsafe ? 0.35f : 0.5f;
                        Curve curve = curveData[connection.curve];
                        if (IsNotMatchingModifier(modifier, connection))
                        {
                            dimm = new Color(1, 1, 1, 0.1f);
                        }
                        else
                        {
                            dimm = connection.isUnsafe ? new Color(1f, 1f, 1f, 0.75f) : dimmMain;
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
                                    ? new Color(0.88f, 0.76f, 0f) 
                                    : connection.isForbidden ? Color.red : new Color(0f, 0.88f, 0f) 
                                : connection.isForbidden ? Color.red : new Color(0f, 0.88f, 0f) * dimm;
                            color2 = Color.clear;
                            width = connection.isForbidden ? 0.25f : connection.isUnsafe ? 0.35f : 0.55f;
                            hovered.Add(new ConnectionRenderData() {bezier = curve.m_Bezier, color = color, color2 = color2, width = width, isUnsafe = connection.isUnsafe, isForbidden = connection.isForbidden});
                            continue;
                        }
                        else if (AreEqual(connectorNode, connection.targetNode))
                        {
                            color = connection.isForbidden ? Color.red : new Color(0.75f, 0f, 0.34f);
                            color2 = Color.clear;
                            width = connection.isForbidden ? 0.25f : connection.isUnsafe ? 0.35f : 0.55f;
                            hovered.Add(new ConnectionRenderData() {bezier = curve.m_Bezier, color = color, color2 = color2, width = width, isUnsafe = connection.isUnsafe, isForbidden = connection.isForbidden});
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
                            overlayBuffer.DrawDashedCurve(color2 * dimm, color * dimm, outline, 0, curve.m_Bezier, width, 1.2f, 0.4f);
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
                            overlayBuffer.DrawCurve(color2 * dimm, color * dimm, outline, 0, curve.m_Bezier, width, float2.zero);
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
                            overlayBuffer.DrawDashedCurve(data.color2, data.color, 0f, 0, data.bezier, data.width, 1.2f, 0.4f);
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
                    if (!previewConnection)
                    {
                        Connector startCon = connectorsData[connector];
                        float3 cursorPos = targetControlPoint.m_Position;
                        if (math.distancesq(cursorPos.xz, startCon.position.xz) < 1)
                        {
                            return;
                        }
                        float3 middlePos = nodeData[startCon.node].m_Position;
                        Bezier4x3 floatingBezier = NetUtils.FitCurve(new Line3.Segment(startCon.position, startCon.position + (startCon.direction * 2f)), new Line3.Segment(cursorPos, middlePos));
                        overlayBuffer.DrawCurve(Color.yellow, Color.yellow, 0f, OverlayRenderSystem.StyleFlags.Projected, floatingBezier, 0.45f, float2.zero);
                    }
                    else if (connectorsData.TryGetComponent(connector2, out Connector t) && t.connectorType == ConnectorType.Target)
                    {
                        Connector s = connectorsData[connector];
                        Bezier4x3 connectionBezier = NetUtils.FitCurve(s.position, s.direction, -t.direction, t.position);
                        overlayBuffer.DrawCurve(new Color(0.63f, 1f, 0f), new Color(0.63f, 1f, 0f), 0f, OverlayRenderSystem.StyleFlags.Projected, connectionBezier, 0.45f, float2.zero);
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


        private struct ConnectorColorSet
        {
            public Color fillSourceColor;
            public Color outlineSourceColor;
            public Color fillTargetColor;
            public Color outlineTargetColor;
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
