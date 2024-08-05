using System;
using Game;
using Game.Common;
using Game.Net;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Traffic.Components.PrioritySigns;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.Systems.ModCompatibility
{
    public partial class RoadBuilderCompatibilitySystem : GameSystemBase
    {
        private ComponentType _roadBuilderUpdateTag;
        private EntityQuery _query;

        protected override void OnCreate()
        {
            base.OnCreate();
            Type rbType = Type.GetType("RoadBuilder.Domain.Components.RoadBuilderUpdateFlagComponent, RoadBuilder", false);
            if (rbType == null)
            {
                Enabled = false;
                return;
            }
            // testTypeHandle = GetDynamicComponentTypeHandle(new ComponentType(Type.GetType("RoadBuilder.Domain.Components.RoadBuilderNetwork, RoadBuilder"), ComponentType.AccessMode.ReadOnly)),
            _roadBuilderUpdateTag = new ComponentType(rbType, ComponentType.AccessMode.ReadOnly);
            _query = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { _roadBuilderUpdateTag },
                Any = new[] { ComponentType.ReadOnly<ModifiedConnections>(), ComponentType.ReadOnly<ModifiedLaneConnections>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), },
            }, new EntityQueryDesc()
            {
                All = new[] { _roadBuilderUpdateTag },
                Any = new[] { ComponentType.ReadOnly<LanePriority>(), ComponentType.ReadOnly<ModifiedPriorities>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>(), },
            });
            RequireForUpdate(_query);
        }

        protected override void OnUpdate()
        {
            int count = _query.CalculateEntityCount();
            Logger.Info($"Found {count} RoadBuilder entities!");

            EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.TempJob);
            JobHandle jobHandle = new ResetTrafficSettings()
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                edgeTypeHandle = SystemAPI.GetComponentTypeHandle<Edge>(true),
                nodeTypeHandle = SystemAPI.GetComponentTypeHandle<Node>(true),
                modifiedConnectionsTypeHandle = SystemAPI.GetComponentTypeHandle<ModifiedConnections>(true),
                modifiedPrioritiesTypeHandle = SystemAPI.GetComponentTypeHandle<ModifiedPriorities>(true),
                laneConnectionsTypeHandle = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                lanePriorityTypeHandle = SystemAPI.GetBufferTypeHandle<LanePriority>(true),
                commandBuffer = ecb,
            }.Schedule(_query, Dependency);
            jobHandle.Complete();
            ecb.Playback(EntityManager);
            ecb.Dispose();

            Dependency = jobHandle;
        }

        private struct ResetTrafficSettings : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Node> nodeTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Edge> edgeTypeHandle;
            [ReadOnly] public ComponentTypeHandle<ModifiedConnections> modifiedConnectionsTypeHandle;
            [ReadOnly] public ComponentTypeHandle<ModifiedPriorities> modifiedPrioritiesTypeHandle;
            [ReadOnly] public BufferTypeHandle<ModifiedLaneConnections> laneConnectionsTypeHandle;
            [ReadOnly] public BufferTypeHandle<LanePriority> lanePriorityTypeHandle;

            public EntityCommandBuffer commandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);

                if (chunk.Has(ref nodeTypeHandle))
                {
                    bool hasConnectionsTag = chunk.Has(ref modifiedConnectionsTypeHandle);
                    bool hasConnections = chunk.Has(ref laneConnectionsTypeHandle);
                    
                    foreach (Entity entity in entities)
                    {
                        Logger.Debug($"NodeEntity {entity}");
                    }
                    
                    if (hasConnections)
                    {
                        commandBuffer.RemoveComponent<ModifiedLaneConnections>(entities);
                        commandBuffer.AddComponent<Updated>(entities);
                    }
                    if (hasConnectionsTag)
                    {
                        commandBuffer.RemoveComponent<ModifiedConnections>(entities);
                    }
                }
                else if (chunk.Has(ref edgeTypeHandle))
                {
                    bool hasPrioritiesTag = chunk.Has(ref modifiedPrioritiesTypeHandle);
                    bool hasPriorities = chunk.Has(ref lanePriorityTypeHandle);
                    NativeArray<Edge> edges = chunk.GetNativeArray(ref edgeTypeHandle);
                    foreach (Entity entity in entities)
                    {
                        Logger.Debug($"EdgeEntity {entity}");
                    }
                    
                    if (hasPriorities)
                    {
                        commandBuffer.RemoveComponent<LanePriority>(entities);
                        foreach (Edge edge in edges)
                        {
                            commandBuffer.AddComponent<Updated>(edge.m_Start);
                            commandBuffer.AddComponent<Updated>(edge.m_End);
                        }
                        commandBuffer.AddComponent<Updated>(entities);
                    }
                    if (hasPrioritiesTag)
                    {
                        commandBuffer.RemoveComponent<ModifiedPriorities>(entities);
                    }
                }
                else
                {
                    Logger.Debug("Unsupported chunk type!");
                }
            }
        }
    }
}
