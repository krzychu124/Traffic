using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.PrioritySigns;
using Traffic.Systems.LaneConnections;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.Systems.PrioritySigns
{
    public partial class GenerateEdgePrioritiesSystem : GameSystemBase
    {
        private EntityQuery _query;
        private EntityQuery _nodeQuery;
        private ModificationBarrier3 _modificationBarrier;

        protected override void OnCreate()
        {
            base.OnCreate();
            _nodeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Updated>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });
            _query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CreationDefinition>(), ComponentType.ReadOnly<PriorityDefinition>(), ComponentType.ReadOnly<Updated>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });
            _modificationBarrier = World.GetOrCreateSystemManaged<ModificationBarrier3>();
            RequireForUpdate(_query);
        }

        protected override void OnUpdate()
        {
            EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
            int count = _nodeQuery.CalculateEntityCount();
            NativeList<Entity> tempNodes = new NativeList<Entity>(count, Allocator.TempJob);
            NativeParallelHashMap<Entity, Entity> tempEntityMap = new NativeParallelHashMap<Entity, Entity>(4, Allocator.TempJob);
            
            GenerateLaneConnectionsSystem.FillTempNodeMapJob fillTempNodeMapJob = new GenerateLaneConnectionsSystem.FillTempNodeMapJob
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                connectedEdgeTypeHandle = SystemAPI.GetBufferTypeHandle<ConnectedEdge>(true),
                tempData = SystemAPI.GetComponentLookup<Temp>(true),
                nodeData = SystemAPI.GetComponentLookup<Node>(true),
                tempNodes = tempNodes.AsParallelWriter(),
                tempEntityMap = tempEntityMap,
            };
            JobHandle jobHandle = fillTempNodeMapJob.Schedule(_nodeQuery, Dependency);
            
            JobHandle jobHandle2 = new GenerateTempPrioritiesJob()
            {
                creationDefinitionTypeHandle = SystemAPI.GetComponentTypeHandle<CreationDefinition>(true),
                priorityDefinitionTypeHandle = SystemAPI.GetComponentTypeHandle<PriorityDefinition>(true),
                tempLanePriorityBufferTypeHandle = SystemAPI.GetBufferTypeHandle<TempLanePriority>(true),
                tempEntityMap = tempEntityMap.AsReadOnly(),
                lanePrioritiesData = SystemAPI.GetBufferLookup<LanePriority>(true),
                commandBuffer =  commandBuffer.AsParallelWriter(),
            }.Schedule(_query, jobHandle);
            jobHandle2.Complete();
            tempNodes.Dispose();
            tempEntityMap.Dispose();
            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
            
            _modificationBarrier.AddJobHandleForProducer(jobHandle);
            Dependency = jobHandle2;
        }

        private struct GenerateTempPrioritiesJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<CreationDefinition> creationDefinitionTypeHandle;
            [ReadOnly] public ComponentTypeHandle<PriorityDefinition> priorityDefinitionTypeHandle;
            [ReadOnly] public BufferTypeHandle<TempLanePriority> tempLanePriorityBufferTypeHandle;
            [ReadOnly] public NativeParallelHashMap<Entity, Entity>.ReadOnly tempEntityMap;
            [ReadOnly] public BufferLookup<LanePriority> lanePrioritiesData;
            public EntityCommandBuffer.ParallelWriter commandBuffer;
 
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<CreationDefinition> definitions = chunk.GetNativeArray(ref creationDefinitionTypeHandle);
                NativeArray<PriorityDefinition> priorityDefinitions = chunk.GetNativeArray(ref priorityDefinitionTypeHandle);
                BufferAccessor<TempLanePriority> tempPrioritiesAccessor = chunk.GetBufferAccessor(ref tempLanePriorityBufferTypeHandle);
                
                for (int i = 0; i < definitions.Length; i++)
                {
                    CreationDefinition definition = definitions[i];
                    PriorityDefinition priorityDefinition = priorityDefinitions[i];
                    
                    if (tempPrioritiesAccessor.Length > 0 &&
                        // tempEntityMap.TryGetValue(definition.m_Original, out Entity tempNodeEntity) &&
                        tempEntityMap.TryGetValue(priorityDefinition.edge, out Entity sourceEdgeEntity))
                    {
                        DynamicBuffer<TempLanePriority> tempLaneConnections = tempPrioritiesAccessor[i];

                        if (lanePrioritiesData.HasBuffer(sourceEdgeEntity))
                        {
                            DynamicBuffer<LanePriority> lanePriorities = commandBuffer.SetBuffer<LanePriority>(unfilteredChunkIndex, sourceEdgeEntity);
                            lanePriorities.CopyFrom(tempLaneConnections.Reinterpret<LanePriority>().AsNativeArray());
                        }
                        else
                        {
                            DynamicBuffer<LanePriority> lanePriorities = commandBuffer.AddBuffer<LanePriority>(unfilteredChunkIndex, sourceEdgeEntity);
                            lanePriorities.CopyFrom(tempLaneConnections.Reinterpret<LanePriority>().AsNativeArray());
                        }
                    }
                }
            }
        }
    }
}
