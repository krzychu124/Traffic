using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.PrioritySigns;
using Traffic.Tools.Helpers;
using Traffic.UISystems;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Traffic.Tools
{
    public partial class PriorityToolSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct CreateDefinitionsJob : IJob
        {
            [ReadOnly] public ComponentLookup<Node> nodeData;
            [ReadOnly] public ComponentLookup<LaneHandle> laneHandleData;
            [ReadOnly] public ComponentLookup<PrefabRef> prefabRefData;
            [ReadOnly] public ComponentLookup<DataOwner> dataOwnerData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Composition> compositionData;
            [ReadOnly] public BufferLookup<NetCompositionLane> netCompositionLanes;
            [ReadOnly] public BufferLookup<PriorityHandle> priorityHandles;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdges;
            [ReadOnly] public NativeList<ControlPoint> controlPoints;
            [ReadOnly] public BufferLookup<LanePriority> lanePriorities;
            [ReadOnly] public ActionOverlayData quickActionData;
            [ReadOnly] public ModUISystem.PriorityToolSetMode mode;
            [ReadOnly] public State state;
            [ReadOnly] public ModUISystem.OverlayMode overlayMode;
            [ReadOnly] public bool updateIntersection;
            public EntityCommandBuffer commandBuffer;

            public void Execute()
            {
                if ((state == State.Default || updateIntersection) && !controlPoints.IsEmpty)
                {
                    ControlPoint controlPoint = controlPoints[0];
                    if (controlPoint.m_OriginalEntity != Entity.Null && nodeData.HasComponent(controlPoint.m_OriginalEntity) && connectedEdges.HasBuffer(controlPoint.m_OriginalEntity))
                    {
                        Entity nodeEntity = controlPoint.m_OriginalEntity;
                        if (IsValidNode(connectedEdges[nodeEntity]))
                        {
                            Entity entity = commandBuffer.CreateEntity();
                            if (!updateIntersection)
                            {
                                commandBuffer.AddComponent<Temp>(entity, new Temp(nodeEntity, TempFlags.Select));
                            }

                            commandBuffer.AddComponent<EditIntersection>(entity, new EditIntersection() { node = nodeEntity });
                            commandBuffer.AddComponent<EditPriorities>(entity);
                            commandBuffer.AddComponent<Updated>(entity);
                        }
                    }

                    return;
                }
                if (state == State.ChangingPriority)
                {
                    if (!controlPoints.IsEmpty)
                    {
                        ControlPoint controlPoint = controlPoints[0];
                        if (controlPoint.m_OriginalEntity != Entity.Null && laneHandleData.HasComponent(controlPoint.m_OriginalEntity))
                        {
                            Entity handleEntity = controlPoint.m_OriginalEntity;
                            LaneHandle laneHandle = laneHandleData[handleEntity];
                            
                            if (!CreateNodeDefinition(laneHandle.node))
                            {
                                return;//invalid node...
                            }

                            Entity entity = commandBuffer.CreateEntity();
                            CreationDefinition definition = new CreationDefinition()
                            {
                                m_Original = laneHandle.edge
                            };
                            PriorityDefinition priorityDefinition = new PriorityDefinition()
                            {
                                edge = laneHandle.edge,
                                laneHandle = handleEntity,
                                node = laneHandle.node,
                            };

                            commandBuffer.AddComponent(entity, definition);
                            commandBuffer.AddComponent(entity, priorityDefinition);
                            commandBuffer.AddComponent<Updated>(entity);

                            DynamicBuffer<TempLanePriority> priorities;

                            PriorityType type = mode switch
                            {
                                ModUISystem.PriorityToolSetMode.Priority => PriorityType.RightOfWay,
                                ModUISystem.PriorityToolSetMode.Yield => PriorityType.Yield,
                                ModUISystem.PriorityToolSetMode.Stop => PriorityType.Stop,
                                ModUISystem.PriorityToolSetMode.Reset => PriorityType.Default,
                                _ => PriorityType.Default
                            };
                            if (lanePriorities.HasBuffer(laneHandle.edge))
                            {
                                DynamicBuffer<LanePriority> originalPriorities = lanePriorities[laneHandle.edge];
                                NativeList<TempLanePriority> tempPriorities = new NativeList<TempLanePriority>(originalPriorities.Length, Allocator.Temp);
                                NativeHashSet<int> laneIndexInGroup = overlayMode == ModUISystem.OverlayMode.LaneGroup ? CollectLanesInGroup(handleEntity, laneHandle) : default(NativeHashSet<int>);
                                /*copy all existint priorities skipping current handle or group of handles*/
                                CopySkipLaneHandle(ref tempPriorities, ref originalPriorities, laneHandle.laneIndex, laneIndexInGroup);
                                laneIndexInGroup.Dispose();

                                priorities = commandBuffer.AddBuffer<TempLanePriority>(entity);
                                /*copy remaining priorities */
                                priorities.CopyFrom(tempPriorities.AsArray());
                                tempPriorities.Dispose();
                                /*generate temp priorities for current state*/
                                FillTempPriorities(ref priorities, type, handleEntity, laneHandle);
                            }
                            else
                            {
                                priorities = commandBuffer.AddBuffer<TempLanePriority>(entity);
                                FillTempPriorities(ref priorities, type, handleEntity, laneHandle);
                            }
                        }
                    }
                } 
                else if (state == State.ApplyingQuickModifications)
                {
                    Entity node = controlPoints.IsEmpty ? Entity.Null : controlPoints[0].m_OriginalEntity;
                    if (node == Entity.Null || !quickActionData.entity.Equals(node) || quickActionData.mode != ModUISystem.ActionOverlayPreview.ResetToVanilla || !connectedEdges.HasBuffer(node))
                    {
                        Logger.DebugTool($"CreateDefinitionsJob finished! state:{state}, n:{node}, quickActionData: {quickActionData.entity} [{quickActionData.mode}]");
                        return;
                    }

                    DynamicBuffer<ConnectedEdge> connectedEdge = connectedEdges[node];
                    bool anyCreated = false;
                    foreach (ConnectedEdge edge in connectedEdge)
                    {
                        Entity edgeEntity = edge.m_Edge;
                        if (edgeData.HasComponent(edgeEntity) && lanePriorities.HasBuffer(edgeEntity))
                        {
                            Entity entity = commandBuffer.CreateEntity();
                            CreationDefinition definition = new CreationDefinition()
                            {
                                m_Original = edgeEntity
                            };
                            PriorityDefinition priorityDefinition = new PriorityDefinition()
                            {
                                edge = edgeEntity,
                                laneHandle = Entity.Null,
                                node = node,
                            };

                            commandBuffer.AddComponent(entity, definition);
                            commandBuffer.AddComponent(entity, priorityDefinition);
                            commandBuffer.AddComponent<Updated>(entity);
                            commandBuffer.AddBuffer<TempLanePriority>(entity);
                            anyCreated = true;
                        }
                    }
                    if (anyCreated)
                    {
                        CreateNodeDefinition(node);
                    }
                }
            }

            private bool IsValidNode(DynamicBuffer<ConnectedEdge> connectedEdge)
            {
                if (connectedEdge.Length > 2)
                {
                    int counter = 0;
                    foreach (ConnectedEdge edge in connectedEdge)
                    {
                        if (compositionData.TryGetComponent(edge.m_Edge, out Composition composition) &&
                            netCompositionLanes.TryGetBuffer(composition.m_Edge, out DynamicBuffer<NetCompositionLane> lanes))
                        {
                            if (ToolHelpers.HasCompositionLaneWithFlag(ref lanes, LaneFlags.Road))
                            {
                                counter++;
                            }
                        }
                    }
                    return counter > 2;
                }
                return false;
            }

            private void FillTempPriorities(ref DynamicBuffer<TempLanePriority> priorities, PriorityType type, Entity handleEntity, LaneHandle referenceLaneHandle)
            {
                if (overlayMode != ModUISystem.OverlayMode.LaneGroup || !dataOwnerData.HasComponent(handleEntity))
                {
                    bool isEnd = edgeData[referenceLaneHandle.edge].m_End == referenceLaneHandle.node;
                    priorities.Add(new TempLanePriority()
                    {
                        laneIndex = referenceLaneHandle.laneIndex,
                        priority = type,
                        isEnd = isEnd
                    });
                }
                else
                {
                    DataOwner dataOwner = dataOwnerData[handleEntity];
                    if (priorityHandles.HasBuffer(dataOwner.entity))
                    {
                        DynamicBuffer<PriorityHandle> handles = priorityHandles[dataOwner.entity];
                        foreach (PriorityHandle priorityHandle in handles)
                        {
                            if (priorityHandle.edge == referenceLaneHandle.edge &&
                                laneHandleData.TryGetComponent(priorityHandle.laneHandle, out LaneHandle otherHandle) &&
                                otherHandle.handleGroup == referenceLaneHandle.handleGroup)
                            {
                                priorities.Add(new TempLanePriority()
                                {
                                    laneIndex = otherHandle.laneIndex,
                                    priority = type,
                                    isEnd = priorityHandle.isEnd
                                });
                            }
                        }
                    }
                }
            }

            private NativeHashSet<int> CollectLanesInGroup(Entity handleEntity, LaneHandle referenceLaneHandle)
            {
                NativeHashSet<int> result = default;
                DataOwner dataOwner = dataOwnerData[handleEntity];
                if (priorityHandles.HasBuffer(dataOwner.entity))
                {
                    result = new NativeHashSet<int>(4, Allocator.Temp);
                    DynamicBuffer<PriorityHandle> handles = priorityHandles[dataOwner.entity];
                    foreach (PriorityHandle priorityHandle in handles)
                    {
                        if (priorityHandle.edge == referenceLaneHandle.edge &&
                            laneHandleData.TryGetComponent(priorityHandle.laneHandle, out LaneHandle otherHandle) &&
                            otherHandle.handleGroup == referenceLaneHandle.handleGroup)
                        {
                            result.Add(otherHandle.laneIndex.x);
                        }
                    }
                }
                return result;
            }

            private void CopySkipLaneHandle(ref NativeList<TempLanePriority> result, ref DynamicBuffer<LanePriority> originalPriorities, int3 laneIndex, NativeHashSet<int> laneIndexInGroup)
            {
                if (laneIndexInGroup.IsEmpty)
                {
                    result.CopyFrom(originalPriorities.AsNativeArray().Reinterpret<TempLanePriority>());
                    CollectionUtils.RemoveValueSwapBack(result, new TempLanePriority()
                    {
                        laneIndex = laneIndex
                    });
                }
                else
                {
                    foreach (TempLanePriority originalPriority in originalPriorities.AsNativeArray().Reinterpret<TempLanePriority>())
                    {
                        if (!laneIndexInGroup.Contains(originalPriority.laneIndex.x))
                        {
                            result.Add(originalPriority);
                        }
                    }
                }
            }

            private bool CreateNodeDefinition(Entity node)
            {
                if (node != Entity.Null && nodeData.HasComponent(node))
                {
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
                    netCourse.m_StartPosition = ToolHelpers.GetCoursePos(netCourse.m_Curve, point, 0f);
                    netCourse.m_StartPosition.m_Flags |= (CoursePosFlags.IsFirst);
                    netCourse.m_StartPosition.m_ParentMesh = -1;
                    netCourse.m_EndPosition = ToolHelpers.GetCoursePos(netCourse.m_Curve, point, 1f);
                    netCourse.m_EndPosition.m_Flags |= (CoursePosFlags.IsLast);
                    netCourse.m_EndPosition.m_ParentMesh = -1;
                    netCourse.m_Length = MathUtils.Length(netCourse.m_Curve);
                    netCourse.m_FixedIndex = -1;

                    Entity nodeEntity = commandBuffer.CreateEntity();
                    commandBuffer.AddComponent(nodeEntity, nodeDef);
                    commandBuffer.AddComponent(nodeEntity, netCourse);
                    commandBuffer.AddComponent<Updated>(nodeEntity);
                    /*----------------------------------------------*/
                    return true;
                }
                
                return false;
            }
        }
    }
}
