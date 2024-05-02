using Game.Net;
using Game.Rendering;
using Game.Tools;
using Traffic.Components;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Traffic.Rendering
{
    public partial class ToolOverlaySystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct HighlightIntersectionJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<EditIntersection> editIntersectionTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Temp> tempComponentTypeHandle;
            [ReadOnly] public ComponentTypeHandle<ToolActionBlocked> toolActionBlockedComponentTypeHandle;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgeData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Node> nodeData;
            [ReadOnly] public ComponentLookup<Game.Net.EdgeGeometry> edgeGeometryData;
            [ReadOnly] public ComponentLookup<Game.Net.StartNodeGeometry> startNodeGeometryData;
            [ReadOnly] public ComponentLookup<Game.Net.EndNodeGeometry> endNodeGeometryData;
            [ReadOnly] public float lineWidth;
            public OverlayRenderSystem.Buffer overlayBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<EditIntersection> editIntersections = chunk.GetNativeArray(ref editIntersectionTypeHandle);
                bool hasTemp = chunk.Has(ref tempComponentTypeHandle);
                bool hasBlocked = chunk.Has(ref toolActionBlockedComponentTypeHandle);
                for (int i = 0; i < editIntersections.Length; i++)
                {
                    EditIntersection intersection = editIntersections[i];
                    if (nodeData.HasComponent(intersection.node))
                    {
                        OverlayRenderingHelpers.DrawNodeOutline(
                            intersection.node,
                            ref connectedEdgeData,
                            ref startNodeGeometryData,
                            ref endNodeGeometryData,
                            ref edgeData,
                            ref edgeGeometryData,
                            ref overlayBuffer,
                            hasBlocked ? Color.red : (hasTemp ? Color.white : new Color(0f, 0.83f, 1f, 1f)),
                            lineWidth,
                            !hasTemp ? 2f : 0f
                        );
                    }
                }
            }
        }
    }
}
