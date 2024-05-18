using Colossal.Mathematics;
using Game.Net;
using Game.Rendering;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Traffic.Rendering
{
    public static class OverlayRenderingHelpers
    {
        public static void DrawNodeOutline(Entity node, ref BufferLookup<ConnectedEdge> connectedEdgeData, ref ComponentLookup<StartNodeGeometry> startNodeGeometryData, ref ComponentLookup<EndNodeGeometry> endNodeGeometryData,
            ref ComponentLookup<Edge> edgeData, ref ComponentLookup<EdgeGeometry> edgeGeometryData, ref OverlayRenderSystem.Buffer overlayBuffer, Color color, float lineWidth, float offsetLength)
        {
            if (connectedEdgeData.HasBuffer(node))
            {
                DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgeData[node];
                for (var i = 0; i < connectedEdges.Length; i++)
                {
                    ConnectedEdge edge = connectedEdges[i];
                    bool isNearEnd = node == edgeData[edge.m_Edge].m_End;
                    EdgeGeometry edgeGeometry = edgeGeometryData[edge.m_Edge];
                    Segment edgeSegment = !isNearEnd ? edgeGeometry.m_Start : edgeGeometry.m_End;
                    if (offsetLength > 0f && math.all(edgeSegment.m_Length > 0f))
                    {
                        // more or less the offset length :)
                        float2 offsetFrac = math.clamp(offsetLength / edgeSegment.m_Length, float2.zero, new float2(1f));
                        float4 cut = new float4(
                            math.select(new float2(0f, offsetFrac.x), new float2(1f - offsetFrac.x, 1f), isNearEnd),
                            math.select(new float2(0f, offsetFrac.y), new float2(1f - offsetFrac.y, 1f), isNearEnd)
                        );
                        Bezier4x3 leftToCorner = MathUtils.Cut(edgeSegment.m_Left, cut.xy);
                        Bezier4x3 rightToCorner = MathUtils.Cut(edgeSegment.m_Right, cut.zw);
                        float3 leftCorner = math.select(leftToCorner.a, leftToCorner.d, !isNearEnd);
                        float3 rightCorner = math.select(rightToCorner.a, rightToCorner.d, !isNearEnd);
                        overlayBuffer.DrawLine(color, color, 0, 0, new Line3.Segment(leftCorner, rightCorner), lineWidth, 1);
                        overlayBuffer.DrawCurve(color, color, 0, 0, leftToCorner, lineWidth, 1);
                        overlayBuffer.DrawCurve(color, color, 0, 0, rightToCorner, lineWidth, 1);
                    }
                    else
                    {
                        overlayBuffer.DrawLine(color, color, 0, 0, new Line3.Segment(math.select(edgeSegment.m_Left.a, edgeSegment.m_Left.d, isNearEnd), math.select(edgeSegment.m_Right.a, edgeSegment.m_Right.d, isNearEnd)), lineWidth, 1);
                    }
                    
                    EdgeNodeGeometry edgeNodeGeometry = !isNearEnd ? startNodeGeometryData[edge.m_Edge].m_Geometry : endNodeGeometryData[edge.m_Edge].m_Geometry;
                    overlayBuffer.DrawCurve(color, color, 0, 0, edgeNodeGeometry.m_Left.m_Left, lineWidth, 1);
                    overlayBuffer.DrawCurve(color, color, 0, 0, edgeNodeGeometry.m_Right.m_Right, lineWidth, 1);
                    if (edgeNodeGeometry.m_MiddleRadius > 0)
                    {
                        overlayBuffer.DrawCurve(color, color, 0, 0, edgeNodeGeometry.m_Left.m_Right, lineWidth, 1);
                        overlayBuffer.DrawCurve(color, color, 0, 0, edgeNodeGeometry.m_Right.m_Left, lineWidth, 1);
                    }
                }
            }
        }

        public static void DrawEdgeHalfOutline(Segment edgeSegment, ref OverlayRenderSystem.Buffer overlayBuffer, Color color, float lineWidth, bool isDashed = false)
        {
            //start edge line
            overlayBuffer.DrawLine(color, color, 0, 0, new Line3.Segment(edgeSegment.m_Left.a, edgeSegment.m_Right.a), lineWidth);
            if (!isDashed)
            {
                //left edge line
                overlayBuffer.DrawCurve(color, color, 0, 0, edgeSegment.m_Left, lineWidth, 1);
                //right edge line
                overlayBuffer.DrawCurve(color, color, 0, 0, edgeSegment.m_Right, lineWidth, 1);
            }
            else
            {
                //left edge line
                overlayBuffer.DrawDashedCurve(color, color, 0, 0, edgeSegment.m_Left, lineWidth, 2, 0.4f);
                //right edge line
                overlayBuffer.DrawDashedCurve(color, color, 0, 0, edgeSegment.m_Right, lineWidth, 2, 0.4f);
            }
            //middle edge cut line
            overlayBuffer.DrawLine(color, color, 0, 0, new Line3.Segment(edgeSegment.m_Left.d, edgeSegment.m_Right.d), lineWidth);
        }
    }
}
