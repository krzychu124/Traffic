using System.Linq;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
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
using LaneConnection = Traffic.Components.LaneConnections.LaneConnection;

namespace Traffic.Tools
{
    public partial class LaneConnectorToolSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct CreateDefinitionsJob : IJob
        {
            [ReadOnly] public State state;
            [ReadOnly] public StateModifier stateModifier;
            [ReadOnly] public Entity editingIntersection;
            [ReadOnly] public Entity intersectionNode;
            [ReadOnly] public NativeList<ControlPoint> controlPoints;
            [ReadOnly] public ComponentLookup<Connector> connectorData;
            [ReadOnly] public ComponentLookup<PrefabRef> prefabRefData;
            [ReadOnly] public BufferLookup<Connection> connectionsBufferData;
            [ReadOnly] public BufferLookup<LaneConnection> connectionsBuffer;
            [ReadOnly] public BufferLookup<ModifiedLaneConnections> modifiedConnectionBuffer;
            [ReadOnly] public BufferLookup<GeneratedConnection> generatedConnectionBuffer;
            [ReadOnly] public BufferLookup<ConnectorElement> connectorElementsBuffer;
            [ReadOnly] public ComponentLookup<Node> nodeData;
            public NativeValue<Tooltip> tooltip;
            public EntityCommandBuffer commandBuffer;

            public void Execute() {
                Logger.DebugTool("Executing CreateDefinitionsJob");
                tooltip.value = Tooltip.None;
                int count = controlPoints.Length;

                Entity node = Entity.Null;
                if (state == State.Default && count > 0)
                {
                    if (nodeData.HasComponent(controlPoints[0].m_OriginalEntity))
                    {
                        node = controlPoints[0].m_OriginalEntity;
                        tooltip.value = Tooltip.SelectIntersection;
                        Entity temp = commandBuffer.CreateEntity();
                        commandBuffer.AddComponent<Temp>(temp, new Temp(node, TempFlags.Select));
                        EditIntersection edit = new EditIntersection()
                        {
                            node = node
                        };
                        commandBuffer.AddComponent<EditIntersection>(temp, edit);
                        commandBuffer.AddComponent<Updated>(temp);
                    }
                }
                else
                {
                    node = intersectionNode;
                }
                Logger.DebugTool($"Node: {node} | {intersectionNode}, state: {state}, count: {count}");
                if (nodeData.HasComponent(node))
                {
                    Logger.DebugTool($"Highlighting {node}");
                    /*---------- highlight selected node ---------*/
                    CreationDefinition nodeDef = new CreationDefinition()
                    {
                        m_Flags = 0,
                        m_Original = node,
                        m_Prefab = prefabRefData[node].m_Prefab
                    };

                    float3 pos = nodeData[node].m_Position;
                    ControlPoint point = new ControlPoint(node, new RaycastHit()
                    {
                        m_Position = pos,
                        m_HitEntity = node,
                        m_HitPosition = pos,
                    });

                    NetCourse netCourse = default(NetCourse);
                    netCourse.m_Curve = new Bezier4x3(point.m_Position, point.m_Position, point.m_Position, point.m_Position);
                    netCourse.m_StartPosition = GetCoursePos(netCourse.m_Curve, point, 0f);
                    netCourse.m_StartPosition.m_Flags |= (CoursePosFlags.IsFirst);
                    netCourse.m_StartPosition.m_ParentMesh = -1;
                    netCourse.m_EndPosition = GetCoursePos(netCourse.m_Curve, point, 1f);
                    netCourse.m_EndPosition.m_Flags |= (CoursePosFlags.IsLast);
                    netCourse.m_EndPosition.m_ParentMesh = -1;
                    netCourse.m_Length = MathUtils.Length(netCourse.m_Curve);
                    netCourse.m_FixedIndex = -1;

                    Entity nodeEntity = commandBuffer.CreateEntity();
                    commandBuffer.AddComponent(nodeEntity, nodeDef);
                    commandBuffer.AddComponent<Updated>(nodeEntity);
                    commandBuffer.AddComponent(nodeEntity, netCourse);
                    /*----------------------------------------------*/
                }

                if (state == State.Default || node == Entity.Null ||  editingIntersection == Entity.Null || count < 1)
                {
                    return;
                }
                Logger.DebugTool($"Creating connector definitions: {count}, {editingIntersection}");
                bool firstConnectorValid = false;
                bool nextConnectorValid = false;
                Entity firstConnectorEntity = controlPoints[0].m_OriginalEntity;
                Entity nextConnectorEntity = Entity.Null;
                Connector firstConnector = new Connector() { laneIndex = -1 };
                Connector nextConnector = new Connector() { laneIndex = -1 };
                if (firstConnectorEntity != Entity.Null && connectorData.HasComponent(firstConnectorEntity))
                {
                    firstConnectorValid = true;
                    firstConnector = connectorData[firstConnectorEntity];
                }

                if (count > 1)
                {
                    nextConnectorEntity = controlPoints[1].m_OriginalEntity;
                    if (nextConnectorEntity != Entity.Null && connectorData.HasComponent(nextConnectorEntity))
                    {
                        nextConnectorValid = true;
                        nextConnector = connectorData[nextConnectorEntity];
                    }
                }

                bool foundModifiedSource = false;
                bool connectionExists = false;
                DynamicBuffer<ConnectorElement> connectorElements = connectorElementsBuffer[editingIntersection];
                NativeHashMap<ConnectorKey, Entity> connectorsMap = new NativeHashMap<ConnectorKey, Entity>(connectorElements.Length, Allocator.Temp);
                FillConnectorMap(connectorElements, connectorsMap);
                if (modifiedConnectionBuffer.HasBuffer(intersectionNode))
                {
                    DynamicBuffer<ModifiedLaneConnections> modifiedConnections = modifiedConnectionBuffer[intersectionNode];
                    for (var i = 0; i < modifiedConnections.Length; i++)
                    {
                        ModifiedLaneConnections modifiedConnection = modifiedConnections[i];
                        if (connectorsMap.TryGetValue(new ConnectorKey(modifiedConnection.edgeEntity, modifiedConnection.laneIndex), out Entity sourceConnectorEntity))
                        {
                            foundModifiedSource |= sourceConnectorEntity == firstConnectorEntity;
                            CreateDefinitions(firstConnectorEntity, sourceConnectorEntity, connectorData[sourceConnectorEntity], nextConnector, modifiedConnection, connectorsMap, ref connectionExists);
                        }
                    }
                }

                if (state == State.SelectingTargetConnector)
                {
                    if (nextConnectorValid && firstConnectorValid)
                    {
                        if (!foundModifiedSource)
                        {
                            Connector sourceConnector = connectorData[firstConnectorEntity];
                            NativeList<Connection> filterConnections = new NativeList<Connection>(4, Allocator.Temp);
                            DynamicBuffer<LaneConnection> laneConnections = connectionsBuffer[firstConnectorEntity];
                            FilterBySource(sourceConnector, laneConnections, filterConnections);
                            CreateDefinitions(firstConnectorEntity, sourceConnector, nextConnector, connectorsMap, filterConnections, ref connectionExists);
                            filterConnections.Dispose();
                        }
                        if (nextConnector.edge == firstConnector.edge &&
                            firstConnector.vehicleGroup > VehicleGroup.Car)
                        {
                            tooltip.value = Tooltip.UTurnTrackNotAllowed;
                        }
                        else
                        {
                            tooltip.value = connectionExists ? Tooltip.RemoveConnection : Tooltip.CompleteConnection;
                        }
                    }
                    else
                    {
                        tooltip.value = Tooltip.SelectConnectorToAddOrRemove;
                    }
                } 
                else if (state == State.SelectingSourceConnector && firstConnectorValid)
                {
                    if (firstConnector.connectorType == ConnectorType.Source)
                    {
                        DynamicBuffer<LaneConnection> connections = connectionsBuffer[firstConnectorEntity];
                        bool alreadyExists = !connections.IsEmpty;
                        tooltip.value = alreadyExists ? Tooltip.ModifyConnections : Tooltip.CreateConnection;
                    }
                    if (firstConnector.connectorType == ConnectorType.Target)
                    {
                        tooltip.value = Tooltip.RemoveTargetConnections;
                    }
                }

                connectorsMap.Dispose();
            }

            private void FillConnectorMap(DynamicBuffer<ConnectorElement> connectorElements, NativeHashMap<ConnectorKey, Entity> connectorsMap) {
                for (int i = 0; i < connectorElements.Length; i++)
                {
                    Entity connectorEntity = connectorElements[i].entity;
                    Connector connector = connectorData[connectorEntity];
                    connectorsMap.Add(new ConnectorKey(connector.edge, connector.laneIndex), connectorEntity);
                }
            }

            private void CreateDefinitions(Entity source, Connector sourceConnector, Connector targetConnector, NativeHashMap<ConnectorKey, Entity> connectorsMap, NativeList<Connection> connections, ref bool connectionExists) {
                if (state != State.SelectingTargetConnector || targetConnector.laneIndex < 0)
                {
                    return;
                }
                Entity entity = commandBuffer.CreateEntity();
                CreationDefinition creationDefinition = new CreationDefinition()
                {
                    m_Original = sourceConnector.node,
                };
                ConnectionDefinition definition = new ConnectionDefinition()
                {
                    connector = source,
                    edge = sourceConnector.edge,
                    node = sourceConnector.node,
                    owner = Entity.Null,
                    laneIndex = sourceConnector.laneIndex,
                    lanePosition = sourceConnector.lanePosition,
                    carriagewayAndGroup = sourceConnector.carriagewayAndGroupIndex,
                    flags = ConnectionFlags.Create | ConnectionFlags.Essential
                };
                commandBuffer.AddComponent<CreationDefinition>(entity, creationDefinition);
                commandBuffer.AddComponent<ConnectionDefinition>(entity, definition);
                commandBuffer.AddComponent(entity, default(Updated));

                NativeList<TempLaneConnection> tempLaneConnections = new NativeList<TempLaneConnection>(4, Allocator.Temp);
                
                Logger.DebugTool($"Creating definitions ({sourceConnector.node}) for: {sourceConnector.edge}[{sourceConnector.laneIndex}] -> {targetConnector.edge}[{targetConnector.laneIndex}]");
                Logger.DebugTool($"Connections:\n\t{string.Join(",\n\t", connections.AsArray().Select(c => $"s: {c.sourceEdge} t: {c.targetEdge} index: [{c.sourceNode.GetLaneIndex() & 0xff};{c.targetNode.GetLaneIndex() & 0xff}] |details| {c.laneCarriagewayWithGroupIndexMap}, {c.lanePositionMap}"))}");

                bool found = false;
                for (var i = 0; i < connections.Length; i++)
                {
                    Connection connection = connections[i];
                    if (connection.sourceEdge != sourceConnector.edge)
                    {
                        continue;
                    }
                    if (connection.sourceEdge == sourceConnector.edge &&
                        connection.targetEdge == targetConnector.edge &&
                        (connection.sourceNode.GetLaneIndex() & 0xff) == sourceConnector.laneIndex &&
                        (connection.targetNode.GetLaneIndex() & 0xff) == targetConnector.laneIndex)
                    {
                        connectionExists = true;
                        found = true;
                        Logger.DebugTool($"FOUND: s: {connection.sourceEdge} t: {connection.targetEdge} index: {connection.sourceNode.GetLaneIndex() & 0xff}; {connection.targetNode.GetLaneIndex() & 0xff}");
                        continue; //remove mode: skip connection
                    }

                    bool notAllowed = sourceConnector.edge == targetConnector.edge && (connection.method & PathMethod.Track) != 0;
                    TempLaneConnection tempConnection = new TempLaneConnection(
                        connection.sourceEdge,
                        connection.targetEdge,
                        new int2(connection.sourceNode.GetLaneIndex() & 0xff, connection.targetNode.GetLaneIndex() & 0xff),
                        connection.lanePositionMap,
                        connection.laneCarriagewayWithGroupIndexMap,
                        connection.method,
                        connection.isUnsafe,
                        connection.curve,
                        ConnectionFlags.Create | ConnectionFlags.Essential
                    );
                    tempLaneConnections.Add(tempConnection);
                }

                if (!found)
                {
                    Logger.DebugTool($"NOT_FOUND: s: {sourceConnector.edge} t: {targetConnector.edge} index: {sourceConnector.laneIndex}; {targetConnector.laneIndex}");
                    PathMethod method = stateModifier == StateModifier.AnyConnector
                        ? DetectConnectionPathMethod(sourceConnector.connectionType, targetConnector.connectionType)
                        : StateModifierToPathMethod(stateModifier & ~StateModifier.MakeUnsafe);
                    bool notAllowed = sourceConnector.edge == targetConnector.edge && (method & PathMethod.Track) != 0;
                    TempLaneConnection connection = new TempLaneConnection(
                        sourceConnector.edge,
                        targetConnector.edge,
                        new int2(sourceConnector.laneIndex, targetConnector.laneIndex),
                        new float3x2(sourceConnector.lanePosition, targetConnector.lanePosition),
                        new int4(sourceConnector.carriagewayAndGroupIndex, targetConnector.carriagewayAndGroupIndex),
                        method,
                        (stateModifier & StateModifier.MakeUnsafe) != 0,
                        NetUtils.FitCurve(sourceConnector.position, sourceConnector.direction, -targetConnector.direction, targetConnector.position),
                        notAllowed ? ConnectionFlags.Highlight : ConnectionFlags.Create | ConnectionFlags.Essential | ConnectionFlags.Highlight
                    );
                    tempLaneConnections.Add(connection);
                }

                Logger.DebugTool($"Copying connections: {tempLaneConnections.Length}");
                DynamicBuffer<TempLaneConnection> laneConnections = commandBuffer.AddBuffer<TempLaneConnection>(entity);
                laneConnections.ResizeUninitialized(tempLaneConnections.Length);
                for (var i = 0; i < laneConnections.Length; i++)
                {
                    laneConnections[i] = tempLaneConnections[i];
                }
                tempLaneConnections.Dispose();
            }

            private void CreateDefinitions(Entity firstConnector, Entity source, Connector sourceConnector, Connector targetConnector, ModifiedLaneConnections modifiedConnections, NativeHashMap<ConnectorKey, Entity> connectorsMap, ref bool connectionExists) {
                bool isEditing = firstConnector == source;
                NativeList<TempLaneConnection> tempLaneConnections = new NativeList<TempLaneConnection>(4, Allocator.Temp);
                DynamicBuffer<GeneratedConnection> generatedConnections = generatedConnectionBuffer[modifiedConnections.modifiedConnections];
                Entity entity = commandBuffer.CreateEntity();
                CreationDefinition creationDefinition = new CreationDefinition()
                {
                    m_Original = sourceConnector.node,
                };
                ConnectionDefinition definition = new ConnectionDefinition()
                {
                    connector = source,
                    edge = sourceConnector.edge,
                    node = sourceConnector.node,
                    owner = modifiedConnections.modifiedConnections,
                    laneIndex = sourceConnector.laneIndex,
                    lanePosition = sourceConnector.lanePosition,
                    carriagewayAndGroup = sourceConnector.carriagewayAndGroupIndex,
                    flags = ConnectionFlags.Modify | ConnectionFlags.Essential
                };
                commandBuffer.AddComponent<CreationDefinition>(entity, creationDefinition);
                commandBuffer.AddComponent<ConnectionDefinition>(entity, definition);
                commandBuffer.AddComponent(entity, default(Updated));

                switch (state)
                {
                    case State.SelectingSourceConnector:
                        /*
                         * generate temp connections for existing generated connections
                         */
                        for (var i = 0; i < generatedConnections.Length; i++)
                        {
                            GeneratedConnection connection = generatedConnections[i];
                            if (connectorsMap.TryGetValue(new ConnectorKey(connection.sourceEntity, connection.laneIndexMap.x), out Entity sourceConnectorEntity) &&
                                connectorsMap.TryGetValue(new ConnectorKey(connection.targetEntity, connection.laneIndexMap.y), out Entity targetConnectorEntity))
                            {
                                Connector sConnector = connectorData[sourceConnectorEntity];
                                Connector tConnector = connectorData[targetConnectorEntity];
                                TempLaneConnection temp = new TempLaneConnection(
                                    connection,
                                    NetUtils.FitCurve(sConnector.position, sConnector.direction, -tConnector.direction, tConnector.position)
                                );
                                temp.flags |= ConnectionFlags.Essential;
                                if (sourceConnector.edge == connection.sourceEntity && sourceConnector.laneIndex == connection.laneIndexMap.x)
                                {
                                    // temp.flags |= ConnectionFlags.Highlight;
                                }
                                tempLaneConnections.Add(temp);
                            }
                        }
                        break;
                    case State.SelectingTargetConnector:
                        /*
                         * generate temp connections for existing generated connections
                         * add new: no connection
                         * skip: existing connection when pointing on matching target connector
                         */
                        bool found = false;
                        int2 laneIndexMap = new int2(sourceConnector.laneIndex, targetConnector.laneIndex);
                        for (var i = 0; i < generatedConnections.Length; i++)
                        {
                            GeneratedConnection connection = generatedConnections[i];
                            if (isEditing &&
                                sourceConnector.edge == connection.sourceEntity &&
                                targetConnector.edge == connection.targetEntity &&
                                math.all(connection.laneIndexMap == laneIndexMap))
                            {
                                found = true;
                                connectionExists = true;
                            }
                            else
                            {
                                if (connectorsMap.TryGetValue(new ConnectorKey(connection.targetEntity, connection.laneIndexMap.y), out Entity targetConnectorEntity))
                                {
                                    Connector connector = connectorData[targetConnectorEntity];
                                    TempLaneConnection temp = new TempLaneConnection(
                                        connection,
                                        NetUtils.FitCurve(sourceConnector.position, sourceConnector.direction, -connector.direction, connector.position)
                                    );
                                    temp.flags |= ConnectionFlags.Essential;
                                    tempLaneConnections.Add(temp);
                                }
                            }
                        }

                        if (isEditing && !found &&
                            targetConnector.laneIndex > -1 &&
                            sourceConnector.edge == modifiedConnections.edgeEntity &&
                            sourceConnector.laneIndex == modifiedConnections.laneIndex)
                        {
                            PathMethod method = stateModifier == StateModifier.AnyConnector
                                ? DetectConnectionPathMethod(sourceConnector.connectionType, targetConnector.connectionType)
                                : StateModifierToPathMethod(stateModifier & ~StateModifier.MakeUnsafe);

                            bool notAllowed = sourceConnector.edge == targetConnector.edge && (method & PathMethod.Track) != 0;
                            TempLaneConnection connection = new TempLaneConnection(
                                sourceConnector.edge,
                                targetConnector.edge,
                                laneIndexMap,
                                new float3x2(sourceConnector.lanePosition, targetConnector.lanePosition),
                                new int4(sourceConnector.carriagewayAndGroupIndex, targetConnector.carriagewayAndGroupIndex),
                                stateModifier == StateModifier.AnyConnector
                                    ? DetectConnectionPathMethod(sourceConnector.connectionType, targetConnector.connectionType)
                                    : StateModifierToPathMethod(stateModifier & ~StateModifier.MakeUnsafe),
                                (stateModifier & StateModifier.MakeUnsafe) != 0,
                                NetUtils.FitCurve(sourceConnector.position, sourceConnector.direction, -targetConnector.direction, targetConnector.position),
                                notAllowed ? ConnectionFlags.Highlight : ConnectionFlags.Create | ConnectionFlags.Essential | ConnectionFlags.Highlight
                            );
                            tempLaneConnections.Add(connection);
                        }
                        break;
                }
                
                DynamicBuffer<TempLaneConnection> laneConnections = commandBuffer.AddBuffer<TempLaneConnection>(entity);
                laneConnections.ResizeUninitialized(tempLaneConnections.Length);
                for (var i = 0; i < laneConnections.Length; i++)
                {
                    laneConnections[i] = tempLaneConnections[i];
                }
                tempLaneConnections.Dispose();
            }

            private CoursePos GetCoursePos(Bezier4x3 curve, ControlPoint controlPoint, float delta) {
                CoursePos result = default(CoursePos);

                result.m_Entity = controlPoint.m_OriginalEntity;
                result.m_SplitPosition = controlPoint.m_CurvePosition;
                result.m_Position = controlPoint.m_Position;
                result.m_Elevation = controlPoint.m_Elevation;
                result.m_Rotation = NetUtils.GetNodeRotation(MathUtils.Tangent(curve, delta));
                result.m_CourseDelta = delta;
                result.m_ParentMesh = controlPoint.m_ElementIndex.x;
                return result;
            }

            private PathMethod StateModifierToPathMethod(StateModifier modifier) {
                PathMethod method = 0;
                switch (modifier)
                {
                    case StateModifier.AnyConnector:
                    case StateModifier.AnyConnector | StateModifier.FullMatch:
                        method = PathMethod.Road | PathMethod.Track;
                        break;
                    case StateModifier.Road:
                    case StateModifier.Road | StateModifier.FullMatch:
                        method = PathMethod.Road;
                        break;
                    case StateModifier.Track:
                    case StateModifier.Track | StateModifier.FullMatch:
                        method = PathMethod.Track;
                        break;
                }
                return method;
            }

            private PathMethod DetectConnectionPathMethod(ConnectionType source, ConnectionType target) {
                ConnectionType shared = source & target & ConnectionType.SharedCarTrack;
                switch (shared)
                {

                    case ConnectionType.Road:
                        return PathMethod.Road;
                    case ConnectionType.Track:
                        return PathMethod.Track;
                    case ConnectionType.Utility:
                        return 0;
                    case ConnectionType.SharedCarTrack:
                        return PathMethod.Road | PathMethod.Track;
                    // case ConnectionType.All:
                        // return PathMethod.Road | PathMethod.Track;
                }
                return 0;
            }

            private void FilterBySource(Connector sourceConnector, DynamicBuffer<LaneConnection> connections, NativeList<Connection> results) {
                NativeHashSet<Lane> connectionSet = new NativeHashSet<Lane>(8, Allocator.Temp);
                for (int i = 0; i < connections.Length; i++)
                {
                    LaneConnection connection = connections[i];
                    if (connectionsBufferData.HasBuffer(connection.connection))
                    {
                        DynamicBuffer<Connection> data = connectionsBufferData[connection.connection];
                        for (int j = 0; j < data.Length; j++)
                        {
                            Connection laneConnection = data[j];
                            if (laneConnection.sourceNode.OwnerEquals(new PathNode(sourceConnector.edge, 0)) &&
                                (laneConnection.sourceNode.GetLaneIndex() & 0xff) == sourceConnector.laneIndex)
                            {
                                Lane l = new Lane()
                                {
                                    m_StartNode = laneConnection.sourceNode,
                                    m_EndNode = laneConnection.targetNode,
                                    m_MiddleNode = laneConnection.ownerNode,
                                };
                                if (!connectionSet.Contains(l))
                                {
                                    connectionSet.Add(l);
                                    results.Add(laneConnection);
                                }
                            }
                        }
                    }
                }
                connectionSet.Dispose();
            }
        }
    }
}
