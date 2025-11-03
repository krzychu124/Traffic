using Colossal.Mathematics;
using Game.Net;
using Game.Pathfind;
using Game.Rendering;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components.LaneConnections;
using Traffic.Systems.Helpers;
using Traffic.Tools;
using Traffic.UISystems;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using NetUtils = Game.Net.NetUtils;

namespace Traffic.Rendering
{
    public partial class ToolOverlaySystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct ConnectionsOverlayJob : IJob
        {
            [ReadOnly] public NativeArray<ArchetypeChunk> chunks;
            [ReadOnly] public ComponentLookup<Connector> connectorsData;
            [ReadOnly] public ComponentLookup<Node> nodeData;
            [ReadOnly] public BufferTypeHandle<Connection> connectionType;
            [ReadOnly] public ActionOverlayData actionOverlayData;
            [ReadOnly] public LaneConnectorToolSystem.State state;
            [ReadOnly] public LaneConnectorToolSystem.StateModifier modifier;
            [ReadOnly] public NativeList<ControlPoint> controlPoints;
            [ReadOnly] public ConnectorColorSet colorSet;
            [ReadOnly] public float connectionWidth;
            public OverlayRenderSystem.Buffer overlayBuffer;

            public void Execute()
            {

                Entity connector = Entity.Null;
                Entity connector2 = Entity.Null;
                ControlPoint targetControlPoint = default;
                bool floatingPosition = false;
                bool previewConnection = false;
                bool selectingTarget = state == LaneConnectorToolSystem.State.SelectingTargetConnector;
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
                    floatingPosition = state == LaneConnectorToolSystem.State.SelectingTargetConnector && connector != connector2; //TODO bug
                }
                Color dimmMain = state == LaneConnectorToolSystem.State.SelectingTargetConnector ? new Color(1f, 1f, 1f, 1f) : new Color(1f, 1f, 1f, 0.65f);
                NativeList<ConnectionRenderData> hovered = new NativeList<ConnectionRenderData>(Allocator.Temp);
                LaneConnectorToolSystem.StateModifier currentModifier = modifier & ~LaneConnectorToolSystem.StateModifier.MakeUnsafe;
                bool makeUnsafe = (modifier & LaneConnectorToolSystem.StateModifier.MakeUnsafe) != 0;
                Color dimm;
                for (var index = 0; index < chunks.Length; index++)
                {
                    ArchetypeChunk chunk = chunks[index];
                    BufferAccessor<Connection> connectionAccessor = chunk.GetBufferAccessor(ref connectionType);
                    for (var i = 0; i < connectionAccessor.Length; i++)
                    {

                        DynamicBuffer<Connection> connections = connectionAccessor[i];
                        foreach (Connection connection in connections)
                        {
                            Bezier4x3 curve = connection.curve;
                            if (actionOverlayData.mode != ModUISystem.ActionOverlayPreview.None)
                            {
                                if (actionOverlayData.mode == ModUISystem.ActionOverlayPreview.RemoveAllConnections)
                                {
                                    hovered.Add(new ConnectionRenderData() { bezier = curve, color = new Color(1f, 0f, 0.15f, 0.9f), color2 = Color.clear, width = connectionWidth, isUnsafe = connection.isUnsafe, isForbidden = connection.isForbidden });
                                    continue;
                                }
                                if (actionOverlayData.mode == ModUISystem.ActionOverlayPreview.RemoveUTurns && connection.sourceEdge == connection.targetEdge)
                                {
                                    hovered.Add(new ConnectionRenderData() { bezier = curve, color = new Color(1f, 0f, 0.15f, 0.9f), color2 = Color.clear, width = connectionWidth, isUnsafe = connection.isUnsafe, isForbidden = connection.isForbidden });
                                    continue;
                                }
                                if (connection.isUnsafe && actionOverlayData.mode == ModUISystem.ActionOverlayPreview.RemoveUnsafe)
                                {
                                    hovered.Add(new ConnectionRenderData() { bezier = curve, color = new Color(1f, 0f, 0.15f, 0.9f), color2 = Color.clear, width = connectionWidth, isUnsafe = connection.isUnsafe, isForbidden = connection.isForbidden });
                                    continue;
                                }
                            }

                            Color color = (connection.method & PathMethod.Track) != 0 ? colorSet.outlineTwoWayColor :
                                connection.isForbidden ? new Color(0.81f, 0f, 0.14f, 0.79f) :
                                connection.method == PathMethod.Bicycle ? colorSet.outlineBikeSourceColor : colorSet.outlineSourceColor;
                            Color color2 = (connection.method & PathMethod.Track) != 0 ? colorSet.fillTwoWayColor :
                                connection.method == PathMethod.Bicycle ? colorSet.fillBikeSourceColor : colorSet.fillSourceColor;
                            float width = connection.isForbidden ? 0.25f :
                                connection.isUnsafe ? connectionWidth * 0.8f : connectionWidth;
                            bool isNotMatching = IsNotMatchingModifier(currentModifier, connection, makeUnsafe);
                            if (isNotMatching || selectingTarget)
                            {
                                dimm = new Color(1, 1, 1, selectingTarget ? 0.3f : 0.2f);
                            }
                            else
                            {
                                dimm = connection.isUnsafe ? new Color(1f, 1f, 1f, 0.55f) : dimmMain;
                            }

                            if (AreEqual(connectorNode, connection.sourceNode))
                            {
                                bool isTarget = state == LaneConnectorToolSystem.State.SelectingTargetConnector && AreEqual(connectorNode2, connection.targetNode);
                                if (isTarget)
                                {
                                    floatingPosition = false;
                                }
                                color = (state == LaneConnectorToolSystem.State.SelectingSourceConnector || AreEqual(connectorNode2, connection.targetNode)) ? state == LaneConnectorToolSystem.State.SelectingTargetConnector ?
                                        new Color(1f, 0f, 0.15f, 0.9f) :
                                        connection.isForbidden ? new Color(1f, 0f, 0.15f, 0.9f) : colorSet.outlineActiveColor :
                                    connection.isForbidden ? new Color(1f, 0f, 0.15f, 0.9f) : colorSet.outlineActiveColor;
                                color2 = Color.clear;
                                width = connection.isForbidden ? 0.25f :
                                    connection.isUnsafe ? connectionWidth * 0.9f : connectionWidth * 1.2f;
                                hovered.Add(new ConnectionRenderData()
                                {
                                    bezier = curve, color = color * (isNotMatching? dimm : dimmMain), color2 = color2, width = width, isUnsafe = connection.isUnsafe, isForbidden = connection.isForbidden, isBikeOnly = connection.method == PathMethod.Bicycle
                                });
                                continue;
                            }
                            if (AreEqual(connectorNode, connection.targetNode))
                            {
                                color = connection.isForbidden ? new Color(1f, 0f, 0.15f, 0.9f) : new Color(0.75f, 0f, 0.34f);
                                color2 = Color.clear;
                                width = connection.isForbidden ? 0.25f :
                                    connection.isUnsafe ? connectionWidth * 0.9f : connectionWidth * 1.2f;
                                hovered.Add(new ConnectionRenderData()
                                    { bezier = curve, color = color, color2 = color2, width = width, isUnsafe = connection.isUnsafe, isForbidden = connection.isForbidden, isBikeOnly = connection.method == PathMethod.Bicycle });
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
                }
                if (!hovered.IsEmpty)
                {
                    for (int i = 0; i < hovered.Length; i++)
                    {
                        ConnectionRenderData data = hovered[i];
                        if (data.isUnsafe || data.isForbidden)
                        {
                            overlayBuffer.DrawDashedCurve(data.color2, data.color, data.isBikeOnly ? 0.1f : 0f, 0, data.bezier, data.width * 1.2f, 1.4f, 0.6f);
                        }
                        else
                        {
                            overlayBuffer.DrawCurve(data.color2, data.color, data.isBikeOnly ? 0.1f : 0f, 0, data.bezier, data.width * 1.1f, float2.zero);
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
                        if (targetControlPoint.m_OriginalEntity == Entity.Null ||
                            math.distancesq(cursorPos.xz, startCon.position.xz) < 1)
                        {
                            return;
                        }
                        float3 middlePos = nodeData[startCon.node].m_Position;
                        cursorPos.y = startCon.position.y;
                        Bezier4x3 floatingBezier = NetUtils.FitCurve(new Line3.Segment(startCon.position, startCon.position + (startCon.direction * 2f)), new Line3.Segment(cursorPos, middlePos));
                        if (makeUnsafe)
                        {
                            overlayBuffer.DrawDashedCurve(Color.yellow, Color.yellow, 0f, 0, floatingBezier, connectionWidth, 1.5f, 0.65f);
                        }
                        else
                        {
                            overlayBuffer.DrawCurve(Color.yellow, Color.yellow, 0f, 0, floatingBezier, connectionWidth * 1.1f, float2.zero);
                        }
                    }
                    else if (connectorsData.TryGetComponent(connector2, out Connector t) && t.connectorType == ConnectorType.Target)
                    {
                        Connector s = connectorsData[connector];
                        Bezier4x3 connectionBezier = NetUtils.FitCurve(s.position, s.direction, -t.direction, t.position);
                        // force unsafe to notify about confirm action result (more accurate visuals, very little drawback)
                        makeUnsafe |= ConnectionUtils.ForceUnsafe(s.vehicleGroup, t.vehicleGroup);
                        if (makeUnsafe)
                        {
                            overlayBuffer.DrawDashedCurve(new Color(0.38f, 1f, 0f), new Color(0.38f, 1f, 0f), 0f, 0, connectionBezier, connectionWidth, 1.5f, 0.65f);
                        }
                        else
                        {
                            overlayBuffer.DrawCurve(new Color(0.38f, 1f, 0f), new Color(0.38f, 1f, 0f), 0f, 0, connectionBezier, connectionWidth * 1.1f, float2.zero);
                        }
                    }
                }
            }

            private bool IsNotMatchingModifier(LaneConnectorToolSystem.StateModifier stateModifier, Connection connection, bool makeUnsafe)
            {
                return ((stateModifier & (LaneConnectorToolSystem.StateModifier.Road | LaneConnectorToolSystem.StateModifier.FullMatch)) == (LaneConnectorToolSystem.StateModifier.Road | LaneConnectorToolSystem.StateModifier.FullMatch) &&
                    (connection.method & PathMethod.Road) == 0) ||
                    ((stateModifier & (LaneConnectorToolSystem.StateModifier.Track | LaneConnectorToolSystem.StateModifier.FullMatch)) == (LaneConnectorToolSystem.StateModifier.Track | LaneConnectorToolSystem.StateModifier.FullMatch) &&
                    (connection.method & PathMethod.Track) == 0) ||
                    (makeUnsafe && (connection.method & (PathMethod.Road | PathMethod.Bicycle)) == 0);
            }

            private bool AreEqual(PathNode node1, PathNode node2)
            {
                return node1.OwnerEquals(node2) && (node1.GetLaneIndex() & 0xff) == (node2.GetLaneIndex() & 0xff);
            }

            private struct ConnectionRenderData
            {
                public Color color;
                public Color color2;
                public Bezier4x3 bezier;
                public float width;
                public bool isUnsafe;
                public bool isForbidden;
                public bool isBikeOnly;
            }
        }
    }
}
