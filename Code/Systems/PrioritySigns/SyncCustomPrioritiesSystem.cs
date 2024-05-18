using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Traffic.Components;
using Traffic.Components.PrioritySigns;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Traffic.Systems.PrioritySigns
{
    public partial class SyncCustomPrioritiesSystem : GameSystemBase
    {
        private EntityQuery _updatedEdgesQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            _updatedEdgesQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<ConnectedNode>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Updated>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.Exclude<LanePriority>(), }
            });
            RequireForUpdate(_updatedEdgesQuery);
        }

        protected override void OnUpdate()
        {
            EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
            JobHandle jobHandle = new SyncOriginalPrioritiesJob()
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                edgeTypeHandle = SystemAPI.GetComponentTypeHandle<Edge>(true),
                compositionTypeHandle = SystemAPI.GetComponentTypeHandle<Composition>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                tempData = SystemAPI.GetComponentLookup<Temp>(true),
                netCompositionData = SystemAPI.GetComponentLookup<NetCompositionData>(true),
                lanePriorityData = SystemAPI.GetBufferLookup<LanePriority>(true),
                commandBuffer = commandBuffer.AsParallelWriter(),
            }.Schedule(_updatedEdgesQuery, Dependency);
            jobHandle.Complete();
            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
            Dependency = jobHandle;
        }

        private struct SyncOriginalPrioritiesJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Temp> tempTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Edge> edgeTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Composition> compositionTypeHandle;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Temp> tempData;
            [ReadOnly] public ComponentLookup<NetCompositionData> netCompositionData;
            [ReadOnly] public BufferLookup<LanePriority> lanePriorityData;
            public EntityCommandBuffer.ParallelWriter commandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                NativeArray<Temp> temps = chunk.GetNativeArray(ref tempTypeHandle);
                NativeArray<Edge> edges = chunk.GetNativeArray(ref edgeTypeHandle);
                NativeArray<Composition> compositions = chunk.GetNativeArray(ref compositionTypeHandle);

                CompositionFlags.General testFlags = CompositionFlags.General.LevelCrossing | CompositionFlags.General.Roundabout | CompositionFlags.General.TrafficLights;
                Logger.DebugTool($"Sync original Entities {entities.Length}");
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Temp tempEdge = temps[i];
                    Logger.DebugTool($"Try Sync Entity {entity}, {tempEdge.m_Original}[{tempEdge.m_Flags}]");
                    if (tempEdge.m_Original != Entity.Null &&
                        edgeData.HasComponent(tempEdge.m_Original) &&
                        (tempEdge.m_Flags & TempFlags.Delete) == 0 &&
                        lanePriorityData.HasBuffer(tempEdge.m_Original))
                    {
                        if ((tempEdge.m_Flags & (TempFlags.Combine | TempFlags.Modify)) != 0)
                        {
                            Logger.DebugTool($"Force default, detected significant edge modification!");
                            DynamicBuffer<LanePriority> originalPriorities = lanePriorityData[tempEdge.m_Original];
                            DynamicBuffer<LanePriority> newLanePriorities = commandBuffer.AddBuffer<LanePriority>(unfilteredChunkIndex, entity);
                            for (var j = 0; j < originalPriorities.Length; j++)
                            {
                                LanePriority priority = originalPriorities[j];
                                priority.priority = PriorityType.Default;
                                newLanePriorities.Add(priority);
                            }
                            
                            commandBuffer.AddComponent<ModifiedPriorities>(unfilteredChunkIndex, entity);
                            continue;
                        }
                        
                        Edge edge = edges[i];
                        bool2 isUpgrade = new bool2(
                            tempData.TryGetComponent(edge.m_Start, out Temp startTemp) && (startTemp.m_Flags & TempFlags.Upgrade) != 0,
                            tempData.TryGetComponent(edge.m_End, out Temp endTemp) && (endTemp.m_Flags & TempFlags.Upgrade) != 0
                        );
                        Logger.DebugTool($"Synchronizing Entity {entity}, {tempEdge.m_Original}[{tempEdge.m_Flags}]");
                        DynamicBuffer<LanePriority> priorities = lanePriorityData[tempEdge.m_Original];
                        DynamicBuffer<LanePriority> lanePriorities = commandBuffer.AddBuffer<LanePriority>(unfilteredChunkIndex, entity);
                        lanePriorities.CopyFrom(priorities.AsNativeArray());
                        commandBuffer.AddComponent<ModifiedPriorities>(unfilteredChunkIndex, entity);

                        Composition composition = compositions[i];
                        CompositionFlags compositionFlagsStart = netCompositionData[composition.m_StartNode].m_Flags;
                        CompositionFlags compositionFlagsEnd = netCompositionData[composition.m_EndNode].m_Flags;
                        if (math.any(isUpgrade) &&
                            ((compositionFlagsStart.m_General & testFlags) != 0 || (compositionFlagsEnd.m_General & testFlags) != 0))
                        {
                            for (var j = 0; j < lanePriorities.Length; j++)
                            {
                                LanePriority lanePriority = lanePriorities[j];
                                if (!lanePriority.isEnd && isUpgrade.x)
                                {
                                    lanePriority.priority = PriorityType.Default;
                                    lanePriorities[j] = lanePriority;
                                } 
                                else if (lanePriority.isEnd && isUpgrade.y)
                                {
                                    lanePriority.priority = PriorityType.Default;
                                    lanePriorities[j] = lanePriority;
                                }
                            }
                            Logger.DebugTool($"Force default, detected upgrade s:[{compositionFlagsStart.m_General}] e:[{compositionFlagsEnd.m_General}]!");
                        }
                    }
                }
            }
        }
    }
}
