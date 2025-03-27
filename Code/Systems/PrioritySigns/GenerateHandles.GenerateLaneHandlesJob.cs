using System;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Traffic.Components.PrioritySigns;
using Traffic.Helpers.Comparers;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CarLane = Game.Net.CarLane;
using Edge = Game.Net.Edge;
using SecondaryLane = Game.Net.SecondaryLane;
using SubLane = Game.Net.SubLane;

namespace Traffic.Systems.PrioritySigns
{
    public partial class GenerateHandles
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct GenerateLaneHandlesJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<EditIntersection> editIntersectionType;
            [ReadOnly] public BufferTypeHandle<PriorityHandle> priorityHandleType;
            [ReadOnly] public ComponentLookup<CarLane> carLaneData;
            [ReadOnly] public ComponentLookup<Composition> compositionData;
            [ReadOnly] public ComponentLookup<Curve> curveData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<EdgeGeometry> edgeGeometryData;
            [ReadOnly] public ComponentLookup<EdgeLane> edgeLaneData;
            [ReadOnly] public ComponentLookup<Hidden> hiddenData;
            [ReadOnly] public ComponentLookup<Lane> laneData;
            [ReadOnly] public ComponentLookup<MasterLane> masterLaneData;
            [ReadOnly] public ComponentLookup<NetCompositionData> netCompositionData;
            [ReadOnly] public ComponentLookup<NetLaneData> netLaneData;
            [ReadOnly] public ComponentLookup<Node> nodeData;
            [ReadOnly] public ComponentLookup<SecondaryLane> secondaryLaneData;
            [ReadOnly] public ComponentLookup<Temp> tempData;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgesBuffer;
            [ReadOnly] public BufferLookup<LanePriority> lanePriorityBuffer;
            [ReadOnly] public BufferLookup<NetCompositionLane> prefabCompositionLaneBuffer;
            [ReadOnly] public BufferLookup<SubLane> subLanesBuffer;
            public EntityCommandBuffer commandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                NativeArray<EditIntersection> editIntersections = chunk.GetNativeArray(ref editIntersectionType);
                NativeParallelMultiHashMap<NodeEdgeLaneKey, Connection> connections = new(8, Allocator.Temp);
                NativeList<PriorityHandle> collectedPriorityHandles = new NativeList<PriorityHandle>(4, Allocator.Temp);
                NativeList<ValueTuple<Entity, LaneHandle, NativeHashSet<EdgeToEdgeKey>>> generatedLaneHandles = new NativeList<ValueTuple<Entity, LaneHandle, NativeHashSet<EdgeToEdgeKey>>>(2, Allocator.Temp);
                NativeHashMap<NodeEdgeLaneKey, LanePriority> oldPriorities = new NativeHashMap<NodeEdgeLaneKey, LanePriority>(8, Allocator.Temp);

                for (int i = 0; i < editIntersections.Length; i++)
                {
                    Entity editIntersectionEntity = entities[i];
                    EditIntersection editIntersection = editIntersections[i];
                    Entity node = editIntersection.node;
                    if (nodeData.HasComponent(node) && subLanesBuffer.HasBuffer(node))
                    {
                        DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgesBuffer[node];
                        DynamicBuffer<SubLane> subLanes = subLanesBuffer[node];

                        foreach (SubLane subLane in subLanes)
                        {
                            Entity subLaneEntity = subLane.m_SubLane;
                            if ((subLane.m_PathMethods & (PathMethod.Road | PathMethod.Track)) == 0 || masterLaneData.HasComponent(subLaneEntity))
                            {
                                continue;
                            }
                            Lane lane = laneData[subLaneEntity];
                            Entity sourceEdge = Helpers.NetUtils.FindEdge(connectedEdges, lane.m_StartNode);
                            Entity targetEdge = sourceEdge;
                            if (!lane.m_StartNode.OwnerEquals(lane.m_EndNode))
                            {
                                targetEdge = Helpers.NetUtils.FindEdge(connectedEdges, lane.m_EndNode);
                            }
                            if (sourceEdge == Entity.Null || targetEdge == Entity.Null)
                            {
                                continue;
                            }

                            Edge sourceEdgeData = edgeData[sourceEdge];
                            Edge targetEdgeData = edgeData[targetEdge];
                            bool2 isEdgeEndMap = new bool2(sourceEdgeData.m_End.Equals(node), targetEdgeData.m_End.Equals(node));

                            if (carLaneData.TryGetComponent(subLaneEntity, out CarLane carLane) && (carLane.m_Flags & (CarLaneFlags.Unsafe | CarLaneFlags.Forbidden)) != 0)
                            {
                                continue;
                            }
                            if (Helpers.NetUtils.GetAdditionalLaneDetails(sourceEdge, targetEdge, new int2(lane.m_StartNode.GetLaneIndex() & 0xff, lane.m_EndNode.GetLaneIndex() & 0xff), isEdgeEndMap, ref compositionData, ref prefabCompositionLaneBuffer,
                                out float3x2 lanePositionMap, out int4 carriagewayWithGroupMap))
                            {
                                Curve curve = curveData[subLaneEntity];
                                Bezier4x3 bezier = curve.m_Bezier;
                                int laneIndex = lane.m_StartNode.GetLaneIndex() & 0xff;
                                Logger.DebugConnections(
                                    $"GenerateLaneHandlesJob: Adding connection (subLane: {subLaneEntity}): idx[{lane.m_StartNode.GetLaneIndex() & 0xff}->{lane.m_EndNode.GetLaneIndex() & 0xff}] edge:[{sourceEdge}=>{targetEdge}] | methods: {subLane.m_PathMethods}");
                                float offset = math.clamp(1f / curve.m_Length, 0f, 1f);
                                Connection connection = new Connection(lane, MathUtils.Cut(bezier, new float2(offset, 1f - offset)), lanePositionMap, carriagewayWithGroupMap, subLane.m_PathMethods, sourceEdge, targetEdge, false, false);
                                connections.Add(new NodeEdgeLaneKey(node.Index, sourceEdge.Index, laneIndex), connection);
                            }
                        }

                        collectedPriorityHandles.Clear();
                        DynamicBuffer<PriorityHandle> priorityHandles;
                        if (chunk.Has(ref priorityHandleType))
                        {
                            priorityHandles = commandBuffer.SetBuffer<PriorityHandle>(editIntersectionEntity);
                        }
                        else
                        {
                            priorityHandles = commandBuffer.AddBuffer<PriorityHandle>(editIntersectionEntity);
                        }

                        NativeHashMap<EdgeToEdgeKey, int> targetGroups = new NativeHashMap<EdgeToEdgeKey, int>(2, Allocator.Temp);
                        EdgeIterator edgeIterator = new EdgeIterator(Entity.Null, node, connectedEdgesBuffer, edgeData, tempData, hiddenData, false);
                        while (edgeIterator.GetNext(out EdgeIteratorValue edgeValue))
                        {
                            Entity edge = edgeValue.m_Edge;
                            float delta = math.select(0f, 1f, edgeValue.m_End);

                            if (!subLanesBuffer.HasBuffer(edge))
                            {
                                continue;
                            }

                            if (tempData.HasComponent(edge))
                            {
                                Logger.Debug($"Node:Edge [{node}:{edge}] IS TEMP!");
                            }

                            oldPriorities.Clear();
                            if (lanePriorityBuffer.HasBuffer(edge))
                            {
                                CollectOldPriorities(ref oldPriorities, lanePriorityBuffer[edge], node, edge);
                            }

                            Composition edgeComposition = compositionData[edge];
                            float edgeWidth = netCompositionData[edgeComposition.m_Edge].m_Width;
                            EdgeGeometry edgeGeometry = edgeGeometryData[edge];
                            Segment edgeSegment = !edgeValue.m_End ? edgeGeometry.m_Start : edgeGeometry.m_End;
                            DynamicBuffer<NetCompositionLane> compositionLanes = prefabCompositionLaneBuffer[edgeComposition.m_Edge];

                            DynamicBuffer<SubLane> edgeSubLanes = subLanesBuffer[edge];

                            generatedLaneHandles.Clear();
                            targetGroups.Clear();
                            int groupIndex = 0;
                            foreach (SubLane subLane in edgeSubLanes)
                            {
                                Entity subLaneEntity = subLane.m_SubLane;
                                if (!edgeLaneData.HasComponent(subLaneEntity) ||
                                    (subLane.m_PathMethods & PathMethod.Road) == 0 ||
                                    secondaryLaneData.HasComponent(subLaneEntity) ||
                                    masterLaneData.HasComponent(subLaneEntity))
                                {
                                    continue;
                                }

                                EdgeLane edgeLane = edgeLaneData[subLaneEntity];
                                bool2 matchingDelta = edgeLane.m_EdgeDelta == delta;
                                if (!matchingDelta.y)
                                {
                                    continue;
                                }

                                Curve curve = curveData[subLaneEntity];
                                curve.m_Bezier = MathUtils.Invert(curve.m_Bezier);
                                Lane lane = laneData[subLaneEntity];
                                PathNode pathNode = lane.m_EndNode;
                                int laneIndex = pathNode.GetLaneIndex() & 0xff;

                                Entity laneHandle = commandBuffer.CreateEntity();
                                commandBuffer.AddComponent<DataOwner>(laneHandle, new DataOwner(editIntersectionEntity));
                                commandBuffer.AddComponent<LaneHandle>(laneHandle);
                                commandBuffer.AddComponent<Updated>(laneHandle);
                                collectedPriorityHandles.Add(new PriorityHandle() { edge = edge, isEnd = edgeValue.m_End, laneHandle = laneHandle });
                                PriorityType priorityType = GetPriorityType(subLaneEntity);
                                PriorityType originalPriority = priorityType;
                                if (oldPriorities.IsCreated && 
                                    oldPriorities.TryGetValue(new NodeEdgeLaneKey(node.Index, edge.Index, laneIndex), out LanePriority priority))
                                {
                                    originalPriority = priority.priority;
                                }

                                NetCompositionLane netCompositionLane = compositionLanes.ElementAt(laneIndex);
                                LaneHandle handle = new LaneHandle()
                                {
                                    edge = edge,
                                    node = node,
                                    laneIndex = new int3(netCompositionLane.m_Index, netCompositionLane.m_Group, netCompositionLane.m_Carriageway),
                                    handleGroup = ++groupIndex,
                                    length = curve.m_Length,
                                    curve = curve.m_Bezier,
                                    priority = priorityType,
                                    originalPriority = originalPriority,
                                    laneSegment = CalculateLaneSegment(ref edgeSegment, ref netCompositionLane, edgeWidth),
                                };
                                // commandBuffer.SetComponent(laneHandle, handle);
                                NativeHashSet<EdgeToEdgeKey> targetsSet = new NativeHashSet<EdgeToEdgeKey>(2, Allocator.Temp);
                                NodeEdgeLaneKey key = new NodeEdgeLaneKey(node.Index, edge.Index, laneIndex);
                                if (connections.TryGetFirstValue(key, out Connection connection, out NativeParallelMultiHashMapIterator<NodeEdgeLaneKey> it))
                                {
                                    DynamicBuffer<Connection> laneHandleConnections = commandBuffer.AddBuffer<Connection>(laneHandle);
                                    do
                                    {
                                        targetsSet.Add(new EdgeToEdgeKey(connection.sourceEdge, connection.targetEdge));
                                        laneHandleConnections.Add(connection);
                                    } while (connections.TryGetNextValue(out connection, ref it));
                                }

                                generatedLaneHandles.Add(new ValueTuple<Entity, LaneHandle, NativeHashSet<EdgeToEdgeKey>>(laneHandle, handle, targetsSet));
                            }

                            GroupByTarget(ref generatedLaneHandles);
                            AddComponents(ref generatedLaneHandles);
                        }

                        if (!collectedPriorityHandles.IsEmpty)
                        {
                            priorityHandles.CopyFrom(collectedPriorityHandles.AsArray());
                        }

                        connections.Clear();
                    }
                }

                oldPriorities.Dispose();
                generatedLaneHandles.Dispose();
                collectedPriorityHandles.Dispose();
                connections.Dispose();
            }

            private void CollectOldPriorities(ref NativeHashMap<NodeEdgeLaneKey, LanePriority> oldPriorities, DynamicBuffer<LanePriority> existingPriorities, Entity node, Entity edge)
            {
                foreach (LanePriority priority in existingPriorities)
                {
                    NodeEdgeLaneKey key = new NodeEdgeLaneKey(node.Index, edge.Index, priority.laneIndex.x);
                    oldPriorities.Add(key, priority);
                }
            }

            private PriorityType GetPriorityType(Entity subLaneEntity)
            {
                if (carLaneData.TryGetComponent(subLaneEntity, out CarLane data))
                {
                    return (data.m_Flags & CarLaneFlags.Yield) != 0 ? PriorityType.Yield :
                        (data.m_Flags & CarLaneFlags.Stop) != 0 ? PriorityType.Stop :
                        (data.m_Flags & CarLaneFlags.RightOfWay) != 0 ? PriorityType.RightOfWay : PriorityType.Default;
                }
                return PriorityType.Default;
            }

            private Segment CalculateLaneSegment(ref Segment edgeSegment, ref NetCompositionLane compositionLane, float edgeWidth)
            {
                float halfLaneWidth = math.max((netLaneData[compositionLane.m_Lane].m_Width - 0.3f) / 2f, 0.5f);
                float t = (compositionLane.m_Position.x - halfLaneWidth) / math.max(1f, edgeWidth) + 0.5f;
                float t2 = (compositionLane.m_Position.x + halfLaneWidth) / math.max(1f, edgeWidth) + 0.5f;
                Segment segment = new Segment()
                {
                    m_Left = MathUtils.Lerp(edgeSegment.m_Left, edgeSegment.m_Right, t),
                    m_Right = MathUtils.Lerp(edgeSegment.m_Left, edgeSegment.m_Right, t2),
                };
                segment.m_Length = new float2(MathUtils.Length(segment.m_Left), MathUtils.Length(segment.m_Right));

                return segment;
            }

            private void GroupByTarget(ref NativeList<ValueTuple<Entity, LaneHandle, NativeHashSet<EdgeToEdgeKey>>> laneHandles)
            {
                int currentIndex = 1;
                NativeHashMap<TargetEntitiesKey<EdgeToEdgeKey>, NativeList<int>> targetGroups = new NativeHashMap<TargetEntitiesKey<EdgeToEdgeKey>, NativeList<int>>(laneHandles.Length, Allocator.Temp);
                for (int i = 0; i < laneHandles.Length; i++)
                {
                    (Entity, LaneHandle, NativeHashSet<EdgeToEdgeKey>) laneHandle = laneHandles[i];
                    NativeArray<EdgeToEdgeKey> edgeToEdgeKeys = laneHandle.Item3.ToNativeArray(Allocator.Temp);
                    edgeToEdgeKeys.Sort(default(EdgeToEdgeComparer));
                    TargetEntitiesKey<EdgeToEdgeKey> targetEntitiesKey = new TargetEntitiesKey<EdgeToEdgeKey>(edgeToEdgeKeys);
                    if (targetGroups.TryGetValue(targetEntitiesKey, out NativeList<int> indexList))
                    {
                        indexList.Add(i);
                    }
                    else
                    {
                        NativeList<int> nativeList = new NativeList<int>(2, Allocator.Temp);
                        nativeList.Add(i);
                        targetGroups.Add(targetEntitiesKey, nativeList);
                    }
                }
                NativeKeyValueArrays<TargetEntitiesKey<EdgeToEdgeKey>,NativeList<int>> generatedGroups = targetGroups.GetKeyValueArrays(Allocator.Temp);
                for (var i = 0; i < generatedGroups.Length; i++)
                {
                    TargetEntitiesKey<EdgeToEdgeKey> key = generatedGroups.Keys[i];
                    NativeList<int> values = generatedGroups.Values[i];
                    foreach (int index in values)
                    {
                        (Entity, LaneHandle, NativeHashSet<EdgeToEdgeKey>) laneHandle = laneHandles[index];
                        laneHandle.Item2.handleGroup = currentIndex;
                        laneHandles[index] = laneHandle;
                    }
                    if (values.Length > 0)
                    {
                        currentIndex++;
                    }
                    key.Dispose();
                    values.Dispose();
                }
                generatedGroups.Dispose();
                targetGroups.Dispose();
            }

            private void AddComponents(ref NativeList<ValueTuple<Entity, LaneHandle, NativeHashSet<EdgeToEdgeKey>>> laneHandles)
            {
                foreach ((Entity, LaneHandle, NativeHashSet<EdgeToEdgeKey>) laneHandle in laneHandles)
                {
                    commandBuffer.SetComponent(laneHandle.Item1, laneHandle.Item2);
                    NativeHashSet<EdgeToEdgeKey> edgeToEdgeKeys = laneHandle.Item3;
                    edgeToEdgeKeys.Dispose();
                }
            }

            private struct TargetEntitiesKey<T> : IEquatable<TargetEntitiesKey<T>>, IDisposable
                where T : struct, IEquatable<T>
            {
                public NativeArray<T> targets;

                public TargetEntitiesKey(T[] collection)
                {
                    targets = new NativeArray<T>(collection, Allocator.Temp);
                }

                public TargetEntitiesKey(NativeArray<T> collection)
                {
                    targets = new NativeArray<T>(collection, Allocator.Temp);
                }

                public bool Equals(TargetEntitiesKey<T> other)
                {
                    if (targets.Equals(other.targets))
                    {
                        return true;
                    }
                    
                    if (targets.Length != other.targets.Length)
                    {
                        return false;
                    }
                    
                    for (int index = 0; index < targets.Length; index++)
                    {
                        if (!targets[index].Equals(other.targets[index]))
                        {
                            return false;
                        }
                    }
                    return true;
                }

                public override int GetHashCode()
                {
                    int hc = 0;
                    foreach (T target in targets)
                    {
                        hc ^= target.GetHashCode();
                    }
                    return hc;
                }

                public void Dispose()
                {
                    targets.Dispose();
                    targets = default;
                }
            }
        }
    }
}
