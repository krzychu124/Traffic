﻿#define DEBUG_TOOL
using System.Text;
using Colossal.Collections;
using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Traffic.Components;
using Traffic.LaneConnections;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Traffic.Systems
{
    
    /// <summary>
    /// Apply changes in temporary entities containing ModifiedLaneConnections buffer
    /// </summary>
    public partial class ApplyLaneConnectionsSystem : GameSystemBase
    {
        private EntityQuery _tempQuery;
        private ToolOutputBarrier _toolOutputBarrier;

        protected override void OnCreate()
        {
            base.OnCreate();
            _toolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();
            _tempQuery = GetEntityQuery(ComponentType.ReadOnly<ConnectedEdge>(), ComponentType.ReadOnly<ModifiedLaneConnections>(), ComponentType.ReadOnly<Temp>());
            RequireForUpdate(_tempQuery);
        }

        protected override void OnUpdate()
        {
            Logger.DebugTool($"ApplyLaneConnectionsSystem: Process {_tempQuery.CalculateEntityCount()} entities");

            HandleTempEntities handleTempEntities = new HandleTempEntities()
            {
                entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
                editIntersectionTypeHandle = SystemAPI.GetComponentTypeHandle<EditIntersection>(true),
                modifiedLaneConnectionTypeHandle = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                tempData = SystemAPI.GetComponentLookup<Temp>(true),
                dataOwnerData = SystemAPI.GetComponentLookup<DataOwner>(false),
                generatedConnectionData = SystemAPI.GetBufferLookup<GeneratedConnection>(false),
                modifiedLaneConnectionData = SystemAPI.GetBufferLookup<ModifiedLaneConnections>(false),
                commandBuffer = _toolOutputBarrier.CreateCommandBuffer(),
            };
            JobHandle jobHandle = handleTempEntities.Schedule(_tempQuery, Dependency);
            _toolOutputBarrier.AddJobHandleForProducer(jobHandle);
            Dependency = jobHandle;
        }

        private struct HandleTempEntities : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Temp> tempTypeHandle;
            [ReadOnly] public ComponentTypeHandle<EditIntersection> editIntersectionTypeHandle;
            [ReadOnly] public BufferTypeHandle<ModifiedLaneConnections> modifiedLaneConnectionTypeHandle;
            [ReadOnly] public ComponentLookup<Temp> tempData;
            public ComponentLookup<DataOwner> dataOwnerData;
            public BufferLookup<GeneratedConnection> generatedConnectionData;
            public BufferLookup<ModifiedLaneConnections> modifiedLaneConnectionData;
            public EntityCommandBuffer commandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                NativeArray<Temp> temps = chunk.GetNativeArray(ref tempTypeHandle);
                BufferAccessor<ModifiedLaneConnections> modifiedConnectionsBufferAccessor = chunk.GetBufferAccessor(ref modifiedLaneConnectionTypeHandle);
                NativeArray<EditIntersection> editIntersections = chunk.GetNativeArray(ref editIntersectionTypeHandle);
                bool isEditNodeChunk = editIntersections.Length > 0;
                //TODO try to convert temp to regular
                Logger.DebugTool($"Handle Temp Entities {entities.Length}, isEditNode: {isEditNodeChunk}");
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Temp tempNode = temps[i];
                    DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections = modifiedConnectionsBufferAccessor[i];
                    Logger.DebugTool($"Patching: {entity}, temp: {{original: {tempNode.m_Original} flags: {tempNode.m_Flags}}}, modifiedConnections: {modifiedLaneConnections.Length}");
                    StringBuilder sb = new StringBuilder();
                    for (var j = 0; j < modifiedLaneConnections.Length; j++)
                    {
                        ModifiedLaneConnections modifiedLaneConnection = modifiedLaneConnections[j];
                        sb.Append($"[{j}] Edge: ").Append(modifiedLaneConnection.edgeEntity).Append(" laneIndex: ").Append(modifiedLaneConnection.laneIndex).Append(" modified: ").Append(modifiedLaneConnection.modifiedConnections);
                        if (modifiedLaneConnection.modifiedConnections != Entity.Null && generatedConnectionData.HasBuffer(modifiedLaneConnection.modifiedConnections))
                        {
                            DynamicBuffer<GeneratedConnection> data = generatedConnectionData[modifiedLaneConnection.modifiedConnections];
                            sb.Append(" BufferLength: ").Append(data.Length.ToString());
                            if (tempData.HasComponent(modifiedLaneConnection.modifiedConnections))
                            {
                                Temp t = tempData[modifiedLaneConnection.modifiedConnections];
                                sb.AppendLine($" | Temp: {t.m_Original} flags: {t.m_Flags}");
                            }
                            else
                            {
                                sb.AppendLine();
                            }

                            for (var k = 0; k < data.Length; k++)
                            {
                                sb.Append("\t").Append(data[k].ToString()).AppendLine();
                            }
                        }
                        else
                        {
                            sb.AppendLine();
                        }
                    }

                    Logger.DebugTool($"Temp GeneratedConnections ({entity}): \n{sb}");
                    if (tempNode.m_Original != Entity.Null && modifiedLaneConnectionData.HasBuffer(tempNode.m_Original))
                    {
                        DynamicBuffer<ModifiedLaneConnections> modified = modifiedLaneConnectionData[tempNode.m_Original];
                        Logger.DebugTool($"Original {tempNode.m_Original}, flags: {tempNode.m_Flags}, modifiedConnections: {modified.Length}");
                        StringBuilder sb2 = new StringBuilder();
                        for (var j = 0; j < modified.Length; j++)
                        {
                            ModifiedLaneConnections modifiedLaneConnection = modified[j];
                            sb2.Append($"[{j}] Edge: ").Append(modifiedLaneConnection.edgeEntity).Append(" laneIndex: ").Append(modifiedLaneConnection.laneIndex).Append(" modified: ").Append(modifiedLaneConnection.modifiedConnections);
                            if (modifiedLaneConnection.modifiedConnections != Entity.Null && generatedConnectionData.HasBuffer(modifiedLaneConnection.modifiedConnections))
                            {
                                DynamicBuffer<GeneratedConnection> data = generatedConnectionData[modifiedLaneConnection.modifiedConnections];
                                sb2.Append(" BufferLength: ").Append(data.Length.ToString());
                                if (tempData.HasComponent(modifiedLaneConnection.modifiedConnections))
                                {
                                    Temp t = tempData[modifiedLaneConnection.modifiedConnections];
                                    sb2.AppendLine($" | Temp: {t.m_Original} flags: {t.m_Flags}");
                                }
                                else
                                {
                                    sb2.AppendLine();
                                }

                                for (var k = 0; k < data.Length; k++)
                                {
                                    sb2.Append("\t").Append(data[k].ToString()).AppendLine();
                                }
                            }
                            else
                            {
                                sb2.AppendLine();
                            }
                        }
                        Logger.DebugTool($"Original GeneratedConnections ({tempNode.m_Original}): \n{sb2}");
                    }

                    bool updated = false;
                    for (int j = 0; j < modifiedLaneConnections.Length; j++)
                    {
                        ModifiedLaneConnections tempModifiedLaneConnection = modifiedLaneConnections[j];
                        if (tempData.HasComponent(tempModifiedLaneConnection.modifiedConnections))
                        {
                            Temp tempConnection = tempData[tempModifiedLaneConnection.modifiedConnections];
                            Logger.DebugTool($"Testing old connection ({tempModifiedLaneConnection.modifiedConnections}) temp: {tempConnection.m_Original} flags: {tempConnection.m_Flags}");
                            if (tempData.HasComponent(tempModifiedLaneConnection.edgeEntity))
                            {

                                Temp tempEdge = tempData[tempModifiedLaneConnection.edgeEntity];
                                if ((tempConnection.m_Flags & TempFlags.Create) != 0)
                                {
                                    Logger.DebugTool($"[{j}] has Temp edge: {tempEdge.m_Original} ({tempModifiedLaneConnection.edgeEntity})");
                                    if ((tempEdge.m_Flags & TempFlags.Delete) == 0)
                                    {
                                        CreateModifiedConnections(j, modifiedLaneConnections, tempNode, tempEdge, ref tempModifiedLaneConnection, ref updated);
                                    }
                                }
                                else if ((tempConnection.m_Flags & TempFlags.Modify) != 0 && tempConnection.m_Original != Entity.Null)
                                {
                                    Logger.DebugTool($"[{j}] no Temp edge: {tempEdge.m_Original} ({tempModifiedLaneConnection.edgeEntity})");
                                    if ((tempEdge.m_Flags & TempFlags.Delete) == 0)
                                    {
                                        EditModifiedConnections(j, modifiedLaneConnections, tempNode, tempEdge, tempConnection, ref tempModifiedLaneConnection, ref updated);
                                    }
                                }
                            }
                            if ((tempConnection.m_Flags & TempFlags.Delete) != 0 && tempConnection.m_Original != Entity.Null)
                            {
                                Logger.DebugTool($"[{j}] Trying to delete modifiedConnection: {tempModifiedLaneConnection.modifiedConnections} | {tempConnection.m_Original}");
                                if (tempNode.m_Original != Entity.Null && modifiedLaneConnectionData.HasBuffer(tempNode.m_Original))
                                {
                                    Logger.DebugTool($"[{j}] Delete connection: {tempModifiedLaneConnection.modifiedConnections} | {tempConnection.m_Original}");
                                    DynamicBuffer<ModifiedLaneConnections> originalLaneConnections = modifiedLaneConnectionData[tempNode.m_Original];
                                    Logger.DebugTool($"[{j}] {tempNode.m_Original} has connections ({originalLaneConnections.Length})");

                                    DeleteModifiedConnection(j, originalLaneConnections, tempConnection, tempNode, isEditNodeChunk, ref updated);
                                }
                            }
                        }
                    }

                    if (updated)
                    {
                        commandBuffer.AddComponent<Updated>(tempNode.m_Original);
                    }
                }
            }

            private void DeleteModifiedConnection(int index, DynamicBuffer<ModifiedLaneConnections> originalLaneConnections, Temp tempConnection, Temp tempNode, bool isEditNodeChunk, ref bool updated)
            {

                for (var k = 0; k < originalLaneConnections.Length; k++)
                {
                    if (originalLaneConnections[k].modifiedConnections.Equals(tempConnection.m_Original))
                    {
                        commandBuffer.AddComponent<Deleted>(tempConnection.m_Original);
                        originalLaneConnections.RemoveAtSwapBack(k);
                        Logger.DebugTool($"[{index}] Found modifiedLaneConnection, deleting {tempConnection.m_Original}");
                        updated = true;
                        break;
                    }
                }

                if (originalLaneConnections.IsEmpty && !isEditNodeChunk)
                {
                    Logger.DebugTool($"No connections left. Removing buffer from {tempNode.m_Original}");
                    commandBuffer.RemoveComponent<ModifiedLaneConnections>(tempNode.m_Original);
                    commandBuffer.RemoveComponent<ModifiedConnections>(tempNode.m_Original);
                    updated = true;
                }
            }

            private void EditModifiedConnections(int index, DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections, Temp tempNode, Temp tempEdge, Temp tempConnection, ref ModifiedLaneConnections tempModifiedLaneConnection, ref bool updated)
            {
                Logger.DebugTool($"[{index}] no delete, patch references and swap GeneratedConnections");
                if (tempNode.m_Original != Entity.Null && modifiedLaneConnectionData.HasBuffer(tempNode.m_Original))
                {
                    DynamicBuffer<ModifiedLaneConnections> originalLaneConnections = modifiedLaneConnectionData[tempNode.m_Original];

                    Logger.DebugTool($"[{index}] {tempNode.m_Original} has connections ({originalLaneConnections.Length})");
                    Entity tempConnectionsEntityOwner = tempModifiedLaneConnection.modifiedConnections;
                    if (generatedConnectionData.HasBuffer(tempConnectionsEntityOwner))
                    {
                        DynamicBuffer<GeneratedConnection> generatedConnections = generatedConnectionData[tempConnectionsEntityOwner];
                        Entity sourceEdgeEntity = (tempEdge.m_Flags & (TempFlags.Replace | TempFlags.Combine)) != 0 ? tempModifiedLaneConnection.edgeEntity : tempEdge.m_Original;
                        Logger.DebugTool($"[{index}] {tempNode.m_Original} patching generated connections ({generatedConnections.Length}) | sourceEdge: {sourceEdgeEntity}");
                        
                        for (int k = 0; k < generatedConnections.Length; k++)
                        {
                            GeneratedConnection connection = generatedConnections[k];
                            connection.sourceEntity = sourceEdgeEntity;
                            if (tempData.HasComponent(connection.targetEntity))
                            {
                                Temp tempTargetEdge = tempData[connection.targetEntity];
                                Logger.DebugTool($"Target is temp {connection.targetEntity} -> orig: {tempTargetEdge.m_Original} flags: {tempTargetEdge.m_Flags}");
                                if ((tempEdge.m_Flags & (TempFlags.Replace | TempFlags.Combine)) != 0)
                                {
                                    connection.targetEntity = connection.targetEntity;
                                }
                                else
                                {
                                    connection.targetEntity = tempTargetEdge.m_Original;
                                }
                            }
                            generatedConnections[k] = connection;
                            Logger.DebugTool($"Updated {k} {connection.ToString()}");
                        }

                        for (var k = 0; k < originalLaneConnections.Length; k++)
                        {
                            ModifiedLaneConnections originalLaneConnection = originalLaneConnections[k];
                            if (originalLaneConnection.laneIndex == tempModifiedLaneConnection.laneIndex &&
                                originalLaneConnection.edgeEntity.Equals(sourceEdgeEntity))
                            {
                                Logger.DebugTool($"Found Connection: e: {tempModifiedLaneConnection.edgeEntity}, idx: {tempModifiedLaneConnection.laneIndex} m: {tempModifiedLaneConnection.modifiedConnections}, updating: {tempConnection.m_Original}");
                                DynamicBuffer<GeneratedConnection> originalGeneratedConnections = generatedConnectionData[originalLaneConnection.modifiedConnections];
                                originalGeneratedConnections.CopyFrom(generatedConnections);
                                commandBuffer.AddComponent<Deleted>(tempModifiedLaneConnection.modifiedConnections);
                                break;
                            }
                        }

                        updated = true;
                    }
                }
            }

            private void CreateModifiedConnections(int index, DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections, Temp tempNode, Temp tempEdge, ref ModifiedLaneConnections modifiedLaneConnection, ref bool updated)
            {
                modifiedLaneConnection.edgeEntity = tempEdge.m_Original;
                modifiedLaneConnections[index] = modifiedLaneConnection;

                Logger.DebugTool($"[{index}] no delete, swap (CreateModifiedConnections)");
                if (tempNode.m_Original != Entity.Null && modifiedLaneConnectionData.HasBuffer(tempNode.m_Original))
                {
                    DynamicBuffer<ModifiedLaneConnections> originalLaneConnections = modifiedLaneConnectionData[tempNode.m_Original];

                    Logger.DebugTool($"[{index}] {tempNode.m_Original} has connections ({originalLaneConnections.Length})");
                    if (generatedConnectionData.HasBuffer(modifiedLaneConnection.modifiedConnections))
                    {

                        DynamicBuffer<GeneratedConnection> generatedConnections = generatedConnectionData[modifiedLaneConnection.modifiedConnections];
                        Logger.DebugTool($"[{index}] {tempNode.m_Original} patching generated connections");
                        DataOwner dataOwner = dataOwnerData[modifiedLaneConnection.modifiedConnections];
                        dataOwner.entity = tempNode.m_Original;
                        for (var k = 0; k < generatedConnections.Length; k++)
                        {
                            GeneratedConnection connection = generatedConnections[k];
                            connection.sourceEntity = tempEdge.m_Original;
                            connection.targetEntity = tempData[connection.targetEntity].m_Original;
                            generatedConnections[k] = connection;
                        }

                        commandBuffer.SetComponent<DataOwner>(modifiedLaneConnection.modifiedConnections, dataOwner);
                        commandBuffer.RemoveComponent<Temp>(modifiedLaneConnection.modifiedConnections);
                        commandBuffer.RemoveComponent<CustomLaneConnection>(modifiedLaneConnection.modifiedConnections);

                        originalLaneConnections.Add(modifiedLaneConnection);
                        updated = true;
                    }
                }
            }
        }
    }
}
