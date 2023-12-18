using Colossal.Collections;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Tools;
using Traffic.Components;
using Traffic.LaneConnections;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using LaneConnection = Traffic.LaneConnections.LaneConnection;
using SubLane = Game.Net.SubLane;

namespace Traffic.Tools
{
    public partial class LaneConnectorToolSystem
    {
        private struct CreateDefinitionsJob : IJob
        {
            [ReadOnly] public State state;
            [ReadOnly] public StateModifier stateModifier;
            [ReadOnly] public Entity intersectionNode;
            [ReadOnly] public NativeList<ControlPoint> controlPoints;
            [ReadOnly] public ComponentLookup<Connector> connectorData;
            [ReadOnly] public ComponentLookup<Lane> laneData;
            [ReadOnly] public ComponentLookup<PrefabRef> prefabRefData;
            [ReadOnly] public BufferLookup<Connection> connectionsBufferData;
            [ReadOnly] public BufferLookup<LaneConnection> connectionsBuffer;
            [ReadOnly] public BufferLookup<SubLane> subLaneBuffer;
            [ReadOnly] public ComponentLookup<Node> nodeData;
            public NativeValue<Tooltip> tooltip;
            public EntityCommandBuffer commandBuffer;

            public void Execute() {
                tooltip.value = Tooltip.None;
                int count = controlPoints.Length;
                if (count < 1)
                {
                    return;
                }

                if (state == State.Default)
                {
                    Entity entity = controlPoints[0].m_OriginalEntity;
                    if (nodeData.HasComponent(entity))
                    {
                        tooltip.value = Tooltip.SelectIntersection;
                    }
                    Entity temp = commandBuffer.CreateEntity();
                    commandBuffer.AddComponent<Temp>(temp, new Temp(entity, TempFlags.Select));
                    EditIntersection edit = new EditIntersection()
                    {
                        node = entity
                    };
                    commandBuffer.AddComponent<EditIntersection>(temp, edit);
                }

                // if (state > State.Default && intersectionNode != Entity.Null && nodeData.HasComponent(intersectionNode))
                // {
                //     Entity nodeEntity = commandBuffer.CreateEntity();
                //     CreationDefinition def = new CreationDefinition()
                //     {
                //         m_Flags = CreationFlags.Select | CreationFlags.Recreate,
                //         m_Original = intersectionNode,
                //         m_Prefab = prefabRefData[intersectionNode].m_Prefab
                //     };
                //     
                //     commandBuffer.AddComponent<CreationDefinition>(nodeEntity, def);
                //     commandBuffer.AddComponent<Temp>(nodeEntity, new Temp(intersectionNode, TempFlags.Select));
                //     commandBuffer.AddComponent<Updated>(nodeEntity);
                //     Node n = nodeData[intersectionNode];
                //     Bezier4x3 b = new Bezier4x3(n.m_Position, n.m_Position, n.m_Position, n.m_Position);
                //     NetCourse nc = new NetCourse()
                //     {
                //         m_Curve = b,
                //         m_StartPosition = new CoursePos() {m_Flags = (CoursePosFlags.IsFirst | CoursePosFlags.IsLast | CoursePosFlags.IsRight | CoursePosFlags.IsLeft), m_ParentMesh = -1, m_Entity = intersectionNode, m_CourseDelta = 0, m_Position = n.m_Position, m_Rotation = NetUtils.GetNodeRotation(MathUtils.Tangent(b, 0))},
                //         m_EndPosition = new CoursePos() {m_Flags = (CoursePosFlags.IsFirst | CoursePosFlags.IsLast | CoursePosFlags.IsRight | CoursePosFlags.IsLeft), m_ParentMesh = -1, m_Entity = intersectionNode, m_CourseDelta = 0, m_Position = n.m_Position, m_Rotation = NetUtils.GetNodeRotation(MathUtils.Tangent(b, 1))},
                //         m_Length = MathUtils.Length(b),
                //         m_FixedIndex = -1,
                //     };
                //     commandBuffer.AddComponent<NetCourse>(nodeEntity, nc);
                // }

                Entity e = controlPoints[0].m_OriginalEntity;
                if (e == Entity.Null || !connectorData.HasComponent(e))
                {
                    return; // shouldn't happen!
                }

                Connector connector = connectorData[e];
                DynamicBuffer<LaneConnection> connections = connectionsBuffer[e];

                switch (state)
                {
                    case State.SelectingSourceConnector: {
                        if (count == 1)
                        {
                            if (connector.connectorType == ConnectorType.Source)
                            {
                                bool alreadyExists = !connections.IsEmpty;
                                tooltip.value = alreadyExists ? Tooltip.ModifyConnections : Tooltip.CreateConnection;
                            }
                            if (connector.connectorType == ConnectorType.Target)
                            {
                                tooltip.value = Tooltip.RemoveTargetConnections;
                            }
                        }
                        break;
                    }

                    case State.RemovingSourceConnections:
                        //todo
                        if (connector.connectorType == ConnectorType.Source)
                        {
                            tooltip.value = Tooltip.RemoveSourceConnections;
                        }
                        break;

                    case State.RemovingTargetConnections:
                        //todo
                        if (connector.connectorType == ConnectorType.Target)
                        {
                            tooltip.value = Tooltip.RemoveTargetConnections;
                        }
                        break;
                }

                if ((controlPoints.Length < 2 || connector.connectorType != ConnectorType.Source))
                {
                    return;
                }
                Entity target = controlPoints[1].m_OriginalEntity;
                if (target == Entity.Null || connector.connectorType != ConnectorType.Source || !connectorData.HasComponent(target) || connectorData[target].connectorType != ConnectorType.Target)
                {
                    if (target == Entity.Null)
                    {
                        tooltip.value = Tooltip.SelectConnectorToAddOrRemove;
                    }
                    return;
                }

                Connector targetConnector = connectorData[target];
                bool exists = FindConnection(e, connector, target, targetConnector, connections, out int2 connectionIndex);
                tooltip.value = exists ? Tooltip.RemoveConnection : Tooltip.CompleteConnection;

                Entity tempConnection = commandBuffer.CreateEntity();
                Entity subLane = Entity.Null;
                if (exists && math.all(connectionIndex > -1))
                {
                    LaneConnection connection = connections[connectionIndex.x];
                    DynamicBuffer<Connection> sourceConnections = connectionsBufferData[connection.connection];
                    Connection c = sourceConnections[connectionIndex.y];
                    DynamicBuffer<SubLane> subLanes = subLaneBuffer[connector.node];
                    if (FindSubLane(c, subLanes, out int subLaneIndex))
                    {
                        subLane = subLanes[subLaneIndex].m_SubLane;
                    }

                    ConnectionData data = new ConnectionData(c, connector.edge, targetConnector.edge, new int2(connector.laneIndex, targetConnector.laneIndex));
                    data.isUnsafe = (stateModifier & StateModifier.MakeUnsafe) != 0;
                    commandBuffer.AddComponent<ConnectionData>(tempConnection, data);
                }
                else
                {
                    Connection connection = new Connection()
                    {
                        method = StateModifierToPathMethod(stateModifier),
                        isUnsafe = (stateModifier & StateModifier.MakeUnsafe) != 0
                    };
                    ConnectionData data = new ConnectionData(connection, connector.edge, targetConnector.edge, new int2(connector.laneIndex, targetConnector.laneIndex));
                    commandBuffer.AddComponent<ConnectionData>(tempConnection, data);
                }
                commandBuffer.AddComponent<Temp>(tempConnection, new Temp(intersectionNode, exists ? TempFlags.Delete : TempFlags.Create));
                commandBuffer.AddComponent<Updated>(tempConnection);
                commandBuffer.AddComponent<CustomLaneConnection>(tempConnection);

                Logger.Info($"Created temp ConnectionData, exists: {exists} , subLane: {subLane}, owner: {intersectionNode}");
            }

            private PathMethod StateModifierToPathMethod(StateModifier modifier) {
                PathMethod method = 0;
                switch (modifier)
                {
                    case StateModifier.SharedRoadTrack:
                        method = PathMethod.Road | PathMethod.Track;
                        break;
                    case StateModifier.RoadOnly:
                        method = PathMethod.Road;
                        break;
                    case StateModifier.TrackOnly:
                        method = PathMethod.Track;
                        break;
                }
                return method;
            }

            private bool FindConnection(Entity source, Connector sourceConnector, Entity target, Connector targetConnector, DynamicBuffer<LaneConnection> connections, out int2 connectionIndex) {
                connectionIndex = -1;
                for (var i = 0; i < connections.Length; i++)
                {
                    LaneConnection connection = connections[i];
                    if (connectionsBufferData.HasBuffer(connection.connection))
                    {
                        var data = connectionsBufferData[connection.connection];
                        for (var j = 0; j < data.Length; j++)
                        {
                            Connection laneConnection = data[j];
                            if (laneConnection.sourceNode.OwnerEquals(new PathNode(sourceConnector.edge, 0)) &&
                                (laneConnection.sourceNode.GetLaneIndex() & 0xff) == sourceConnector.laneIndex &&
                                laneConnection.targetNode.OwnerEquals(new PathNode(targetConnector.edge, 0)) &&
                                (laneConnection.targetNode.GetLaneIndex() & 0xff) == targetConnector.laneIndex)
                            {
                                connectionIndex = new int2(i, j);
                                return true;
                            }
                        }
                    }
                }
                return false;
            }

            private bool FindSubLane(Connection c, DynamicBuffer<SubLane> subLanes, out int index) {
                index = -1;
                Lane tempLane = new Lane()
                {
                    m_StartNode = c.sourceNode,
                    m_EndNode = c.targetNode,
                    m_MiddleNode = c.ownerNode,
                };
                for (var i = 0; i < subLanes.Length; i++)
                {
                    SubLane s = subLanes[i];
                    Lane lane = laneData[s.m_SubLane];
                    if (tempLane.Equals(lane))
                    {
                        index = i;
                        return true;
                    }
                }
                return false;
            }
        }
    }
}
