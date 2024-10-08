﻿using System.Text;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Traffic.Helpers.Comparers;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

namespace Traffic.Systems.LaneConnections
{
    public partial class ApplyLaneConnectionsSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct HandleTempEntitiesJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Temp> tempTypeHandle;
            [ReadOnly] public ComponentTypeHandle<EditIntersection> editIntersectionTypeHandle;
            [ReadOnly] public BufferTypeHandle<ModifiedLaneConnections> modifiedLaneConnectionTypeHandle;
            [ReadOnly] public ComponentLookup<Temp> tempData;
            [ReadOnly] public ComponentLookup<DataTemp> dataTemps;
            [ReadOnly] public Entity fakePrefabRef;
            public ComponentLookup<DataOwner> dataOwnerData;
            public BufferLookup<GeneratedConnection> generatedConnectionData;
            public BufferLookup<ModifiedLaneConnections> modifiedLaneConnectionData;
            public NativeParallelHashMap<NodeEdgeKey, Entity> tempEdgeMap;
            public EntityCommandBuffer commandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                NativeArray<Temp> temps = chunk.GetNativeArray(ref tempTypeHandle);
                BufferAccessor<ModifiedLaneConnections> modifiedConnectionsBufferAccessor = chunk.GetBufferAccessor(ref modifiedLaneConnectionTypeHandle);
                NativeArray<EditIntersection> editIntersections = chunk.GetNativeArray(ref editIntersectionTypeHandle);
                bool isEditNodeChunk = editIntersections.Length > 0;

                Logger.DebugTool($"Handle Temp Entities {entities.Length}, isEditNode: {isEditNodeChunk}");
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Temp tempNode = temps[i];
                    DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections = modifiedConnectionsBufferAccessor[i];
#if DEBUG_TOOL
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
                            if (dataTemps.HasComponent(modifiedLaneConnection.modifiedConnections))
                            {
                                DataTemp t = dataTemps[modifiedLaneConnection.modifiedConnections];
                                sb.AppendLine($" | DataTemp: {t.original} flags: {t.flags}");
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
                                if (dataTemps.HasComponent(modifiedLaneConnection.modifiedConnections))
                                {
                                    DataTemp t = dataTemps[modifiedLaneConnection.modifiedConnections];
                                    sb2.AppendLine($" | Temp: {t.original} flags: {t.flags}");
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
#endif
                    if (modifiedLaneConnections.Length == 0)
                    {
                        continue; // nothing has changed
                    }
                    
                    bool updated = false;
                    bool wasModified = tempNode.m_Original != Entity.Null && modifiedLaneConnectionData.HasBuffer(tempNode.m_Original);
                    NativeList<ModifiedLaneConnections> originalLaneConnections = GetOriginalNodeModifiedLaneConnectionsBuffer(tempNode);

                    for (int j = 0; j < modifiedLaneConnections.Length; j++)
                    {
                        ModifiedLaneConnections tempModifiedLaneConnection = modifiedLaneConnections[j];
                        if (dataTemps.HasComponent(tempModifiedLaneConnection.modifiedConnections))
                        {
                            DataTemp tempConnection = dataTemps[tempModifiedLaneConnection.modifiedConnections];
                            Logger.DebugTool($"Applying lane connection changes ({tempModifiedLaneConnection.modifiedConnections}) temp: {tempConnection.original} flags: {tempConnection.flags} ||" +
                                $"edge: {tempModifiedLaneConnection.edgeEntity} idx: {tempModifiedLaneConnection.laneIndex} pos: {tempModifiedLaneConnection.lanePosition} carriageway&group: {tempModifiedLaneConnection.carriagewayAndGroup}");
                            if ((tempConnection.flags & TempFlags.Delete) != 0 && tempConnection.original != Entity.Null)
                            {
                                Logger.DebugTool($"[{j}] Trying to delete modifiedConnection: {tempModifiedLaneConnection.modifiedConnections} | {tempConnection.original}");
                                if (wasModified)
                                {
                                    Logger.DebugTool($"[{j}] Delete connection: {tempModifiedLaneConnection.modifiedConnections} | {tempConnection.original}");
                                    // DynamicBuffer<ModifiedLaneConnections> originalLaneConnections = modifiedLaneConnectionData[tempNode.m_Original];
                                    Logger.DebugTool($"[{j}] {tempNode.m_Original} has connections ({originalLaneConnections.Length})");

                                    DeleteModifiedConnection(j, originalLaneConnections, tempConnection, tempNode, false, ref updated);
                                }
                            }
                            else if (tempData.HasComponent(tempModifiedLaneConnection.edgeEntity))
                            {
                                Temp tempEdge = tempData[tempModifiedLaneConnection.edgeEntity];
                                if ((tempConnection.flags & TempFlags.Create) != 0)
                                {
                                    Logger.DebugTool($"[{j}] has Temp edge: {tempEdge.m_Original} ({tempModifiedLaneConnection.edgeEntity})");
                                    if ((tempEdge.m_Flags & TempFlags.Delete) == 0)
                                    {
                                        CreateModifiedConnections(j, modifiedLaneConnections, originalLaneConnections, tempNode, tempEdge, ref tempModifiedLaneConnection, ref updated);
                                    }
                                }
                                else if ((tempConnection.flags & TempFlags.Modify) != 0 && tempConnection.original != Entity.Null)
                                {
                                    Logger.DebugTool($"[{j}] no Temp edge: {tempEdge.m_Original} ({tempModifiedLaneConnection.edgeEntity})");
                                    if ((tempEdge.m_Flags & TempFlags.Delete) == 0)
                                    {
                                        if (tempEdge.m_Original == Entity.Null)
                                        {
                                            ReplaceModifiedConnections(j, entity, tempNode, tempEdge, tempConnection, ref tempModifiedLaneConnection, ref updated);
                                        }
                                        else if ((tempEdge.m_Flags & TempFlags.Combine) != 0)
                                        {
                                            CombineModifiedConnections(j, entity, tempNode, tempEdge, tempConnection, ref tempModifiedLaneConnection, ref updated);
                                        }
                                        else
                                        {
                                            EditModifiedConnections(j, entity, tempNode, tempEdge, tempConnection, ref tempModifiedLaneConnection, ref updated);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (updated)
                    {
                        if (wasModified)
                        {
                            originalLaneConnections.Sort(default(ModifiedLaneConnectionsComparer));
                            modifiedLaneConnectionData[tempNode.m_Original].Clear();
                            modifiedLaneConnectionData[tempNode.m_Original].AddRange(originalLaneConnections.AsArray());
                        }
                        else
                        {
                            DynamicBuffer<ModifiedLaneConnections> newModifiedConnections = commandBuffer.AddBuffer<ModifiedLaneConnections>(tempNode.m_Original);
                            newModifiedConnections.CopyFrom(originalLaneConnections.AsArray());
                        }
                        commandBuffer.AddComponent<Updated>(tempNode.m_Original);
                    }
                    originalLaneConnections.Dispose();
                }
            }

            private NativeList<ModifiedLaneConnections> GetOriginalNodeModifiedLaneConnectionsBuffer(Temp tempNode)
            {
                if (tempNode.m_Original != Entity.Null && modifiedLaneConnectionData.HasBuffer(tempNode.m_Original))
                {
                    NativeList<ModifiedLaneConnections> list = new NativeList<ModifiedLaneConnections>(Allocator.Temp);
                    list.AddRange(modifiedLaneConnectionData[tempNode.m_Original].AsNativeArray());
                    return list;
                }
                
                return new NativeList<ModifiedLaneConnections>(Allocator.Temp);
            }

            private void DeleteModifiedConnection(int index, NativeList<ModifiedLaneConnections> originalLaneConnections, DataTemp tempConnection, Temp tempNode, bool isEditNodeChunk, ref bool updated)
            {

                for (var k = 0; k < originalLaneConnections.Length; k++)
                {
                    if (originalLaneConnections[k].modifiedConnections.Equals(tempConnection.original))
                    {
                        commandBuffer.AddComponent<Deleted>(tempConnection.original);
                        originalLaneConnections.RemoveAtSwapBack(k);
                        Logger.DebugTool($"[{index}] Found modifiedLaneConnection, deleting {tempConnection.original}");
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

            private void EditModifiedConnections(int index, Entity tempNodeEntity, Temp tempNode, Temp tempEdge, DataTemp tempConnection, ref ModifiedLaneConnections tempModifiedLaneConnection, ref bool updated)
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
                        Entity sourceEdgeEntity = (tempEdge.m_Flags & TempFlags.Replace) != 0 ? tempModifiedLaneConnection.edgeEntity : tempEdge.m_Original;
                        Logger.DebugTool($"[{index}] {tempNode.m_Original} patching generated connections ({generatedConnections.Length}) | sourceEdge: {sourceEdgeEntity}");
                        
                        for (int k = 0; k < generatedConnections.Length; k++)
                        {
                            GeneratedConnection connection = generatedConnections[k];
                            connection.sourceEntity = sourceEdgeEntity;
                            if (tempData.HasComponent(connection.targetEntity))
                            {
                                Temp tempTargetEdge = tempData[connection.targetEntity];
                                Logger.DebugTool($"Target is temp {connection.targetEntity} -> orig: {tempTargetEdge.m_Original} flags: {tempTargetEdge.m_Flags}");
                                if (tempTargetEdge.m_Original == Entity.Null || (tempTargetEdge.m_Flags & TempFlags.Combine) != 0)
                                {
                                    Logger.DebugTool($"Target {connection.targetEntity} has null temp original, do nothing, use new edge entity");
                                }
                                else if ((tempTargetEdge.m_Flags & TempFlags.Replace) == 0)
                                {
                                    connection.targetEntity = tempTargetEdge.m_Original;
                                }
                            }
                            generatedConnections[k] = connection;
#if DEBUG_TOOL
                            Logger.DebugTool($"Updated {k} {connection.ToString()}");
#endif
                        }
                        
                        generatedConnections.AsNativeArray().Sort(default(GeneratedConnectionComparer));
                        
                        for (var k = 0; k < originalLaneConnections.Length; k++)
                        {
                            ModifiedLaneConnections originalLaneConnection = originalLaneConnections[k];
                            if (originalLaneConnection.modifiedConnections == tempConnection.original)
                            {
                                Logger.DebugTool($"Found Connection: e: {originalLaneConnection.edgeEntity}, idx: {originalLaneConnection.laneIndex} mC: {originalLaneConnection.modifiedConnections} mCT: {tempModifiedLaneConnection.modifiedConnections}, updatingMc: {tempConnection.original}");
                                DynamicBuffer<GeneratedConnection> originalGeneratedConnections = generatedConnectionData[originalLaneConnection.modifiedConnections];
                                originalGeneratedConnections.CopyFrom(generatedConnections);
                                /*temp modified connection may have updated lane position and other details, update*/
                                originalLaneConnection.laneIndex = tempModifiedLaneConnection.laneIndex;
                                originalLaneConnection.carriagewayAndGroup = tempModifiedLaneConnection.carriagewayAndGroup;
                                originalLaneConnection.lanePosition = tempModifiedLaneConnection.lanePosition;
                                if ((tempEdge.m_Flags & TempFlags.Replace) != 0)
                                {
                                    originalLaneConnection.edgeEntity = tempModifiedLaneConnection.edgeEntity;
                                }
                                originalLaneConnections[k] = originalLaneConnection;
                                commandBuffer.AddComponent<Deleted>(tempModifiedLaneConnection.modifiedConnections);
                                break;
                            }
                        }

                        updated = true;
                    }
                }
            }

            private void CombineModifiedConnections(int index, Entity tempNodeEntity, Temp tempNode, Temp tempEdge, DataTemp tempConnection, ref ModifiedLaneConnections tempModifiedLaneConnection, ref bool updated )
            {
                Logger.DebugTool($"[{index}] no delete, combine old references and copy GeneratedConnections. Combine?:{(tempEdge.m_Flags & TempFlags.Combine) != 0} | hasOrgE:{tempEdge.m_Original}");
                if (tempNode.m_Original != Entity.Null && modifiedLaneConnectionData.HasBuffer(tempNode.m_Original))
                {
                    DynamicBuffer<ModifiedLaneConnections> originalLaneConnections = modifiedLaneConnectionData[tempNode.m_Original];

                    Logger.DebugTool($"[{index}] Original node {tempNode.m_Original} has {originalLaneConnections.Length} connections");
                    Entity tempConnectionsEntityOwner = tempModifiedLaneConnection.modifiedConnections;
                    if (generatedConnectionData.HasBuffer(tempConnectionsEntityOwner))
                    {
                        DynamicBuffer<GeneratedConnection> generatedConnections = generatedConnectionData[tempConnectionsEntityOwner];
                        Entity sourceEdgeEntity = tempModifiedLaneConnection.edgeEntity;
                        if (tempData.TryGetComponent(tempModifiedLaneConnection.edgeEntity, out Temp tempOtherEdge))
                        {
                            var t = tempOtherEdge;
                            Logger.DebugTool($"[{index}] {tempNode.m_Original} | edge {tempModifiedLaneConnection.edgeEntity} isTemp, {t.m_Original}, {t.m_Flags}, {t.m_CurvePosition}");
                        }
                        Logger.DebugTool($"[{index}] {tempNode.m_Original} patching generated connections ({generatedConnections.Length}) | sourceEdge ({tempModifiedLaneConnection.edgeEntity}): {sourceEdgeEntity}");
                        
                        for (int k = 0; k < generatedConnections.Length; k++)
                        {
                            GeneratedConnection connection = generatedConnections[k];
                            connection.sourceEntity = sourceEdgeEntity;
                            if (tempData.HasComponent(connection.targetEntity))
                            {
                                Temp tempTargetEdge = tempData[connection.targetEntity];
                                Logger.DebugTool($"Target is temp {connection.targetEntity} -> orig: {tempTargetEdge.m_Original} flags: {tempTargetEdge.m_Flags}");
                                if (tempTargetEdge.m_Original != Entity.Null && (tempTargetEdge.m_Flags & TempFlags.Combine) == 0)
                                {
                                    connection.targetEntity = tempTargetEdge.m_Original;
                                }
                            }
                            generatedConnections[k] = connection;
                            Logger.DebugTool($"Updated {k} {connection.ToString()}");
                        }
                        
                        generatedConnections.AsNativeArray().Sort(default(GeneratedConnectionComparer));
                        
                        Logger.DebugTool($"Searching for old edge using n[{tempNodeEntity}] e[{tempModifiedLaneConnection.edgeEntity}]...");
                        if (tempEdgeMap.TryGetValue(new NodeEdgeKey(tempNodeEntity, tempModifiedLaneConnection.edgeEntity), out Entity oldEntity))
                        {
                            Logger.DebugTool($"Found edge in tempEdgeMap: edge: {oldEntity} using n[{tempNodeEntity}] e[{tempModifiedLaneConnection.edgeEntity}]");
                            for (int k = 0; k < originalLaneConnections.Length; k++)
                            {
                                ModifiedLaneConnections originalLaneConnection = originalLaneConnections[k];
                                if (originalLaneConnection.modifiedConnections == tempConnection.original)
                                {
                                    Logger.DebugTool($"Found Connection: edge: {oldEntity}, idx: {originalLaneConnection.laneIndex} mC: {originalLaneConnection.modifiedConnections} mCT: {tempModifiedLaneConnection.modifiedConnections}, updatingTempOriginalMc: {tempConnection.original} + edge: {sourceEdgeEntity}");
                                    DynamicBuffer<GeneratedConnection> originalGeneratedConnections = generatedConnectionData[originalLaneConnection.modifiedConnections];
                                    originalGeneratedConnections.CopyFrom(generatedConnections);
                                    /*temp modified connection may have updated lane position and other details, update*/
                                    originalLaneConnection.laneIndex = tempModifiedLaneConnection.laneIndex;
                                    originalLaneConnection.carriagewayAndGroup = tempModifiedLaneConnection.carriagewayAndGroup;
                                    originalLaneConnection.lanePosition = tempModifiedLaneConnection.lanePosition;
                                    originalLaneConnection.edgeEntity = sourceEdgeEntity;
                                    originalLaneConnections[k] = originalLaneConnection;
                                    commandBuffer.AddComponent<Deleted>(tempModifiedLaneConnection.modifiedConnections);
                                    break;
                                }
                            }

                            updated = true;
                        }
                    }
                }
            }
            
            private void ReplaceModifiedConnections(int index, Entity tempNodeEntity, Temp tempNode, Temp tempEdge, DataTemp tempConnection, ref ModifiedLaneConnections tempModifiedLaneConnection, ref bool updated )
            {
                Logger.DebugTool($"[{index}] no delete, replace old references and copy GeneratedConnections. Combine?:{(tempEdge.m_Flags & TempFlags.Combine) != 0} | hasOrgE:{tempEdge.m_Original}");
                if (tempNode.m_Original != Entity.Null && modifiedLaneConnectionData.HasBuffer(tempNode.m_Original))
                {
                    DynamicBuffer<ModifiedLaneConnections> originalLaneConnections = modifiedLaneConnectionData[tempNode.m_Original];

                    Logger.DebugTool($"[{index}] Original node {tempNode.m_Original} has {originalLaneConnections.Length} connections");
                    Entity tempConnectionsEntityOwner = tempModifiedLaneConnection.modifiedConnections;
                    if (generatedConnectionData.HasBuffer(tempConnectionsEntityOwner))
                    {
                        DynamicBuffer<GeneratedConnection> generatedConnections = generatedConnectionData[tempConnectionsEntityOwner];
                        Entity sourceEdgeEntity = tempModifiedLaneConnection.edgeEntity;
                        Logger.DebugTool($"[{index}] {tempNode.m_Original} patching generated connections ({generatedConnections.Length}) | sourceEdge ({tempModifiedLaneConnection.edgeEntity}): {sourceEdgeEntity}");
                        
                        for (int k = 0; k < generatedConnections.Length; k++)
                        {
                            GeneratedConnection connection = generatedConnections[k];
                            connection.sourceEntity = sourceEdgeEntity;
                            if (tempData.HasComponent(connection.targetEntity))
                            {
                                Temp tempTargetEdge = tempData[connection.targetEntity];
                                Logger.DebugTool($"Target is temp {connection.targetEntity} -> orig: {tempTargetEdge.m_Original} flags: {tempTargetEdge.m_Flags}");
                                if (tempTargetEdge.m_Original != Entity.Null && (tempTargetEdge.m_Flags & TempFlags.Combine) == 0)
                                {
                                    connection.targetEntity = tempTargetEdge.m_Original;
                                }
                            }
                            generatedConnections[k] = connection;
                            Logger.DebugTool($"Updated {k} {connection.ToString()}");
                        }
                        
                        generatedConnections.AsNativeArray().Sort(default(GeneratedConnectionComparer));
                        
                        Logger.DebugTool($"Searching for old edge using n[{tempNodeEntity}] e[{tempModifiedLaneConnection.edgeEntity}]...");
                        if (tempEdgeMap.TryGetValue(new NodeEdgeKey(tempNodeEntity, tempModifiedLaneConnection.edgeEntity), out Entity oldEntity))
                        {
                            Logger.DebugTool($"Found edge in tempEdgeMap: edge: {oldEntity} using n[{tempNodeEntity}] e[{tempModifiedLaneConnection.edgeEntity}]");
                            for (int k = 0; k < originalLaneConnections.Length; k++)
                            {
                                ModifiedLaneConnections originalLaneConnection = originalLaneConnections[k];
                                if (originalLaneConnection.modifiedConnections == tempConnection.original)
                                {
                                    Logger.DebugTool($"Found Connection: edge: {oldEntity}, idx: {originalLaneConnection.laneIndex} mC: {originalLaneConnection.modifiedConnections} mCT: {tempModifiedLaneConnection.modifiedConnections}, updatingTempOriginalMc: {tempConnection.original} + edge: {sourceEdgeEntity}");
                                    DynamicBuffer<GeneratedConnection> originalGeneratedConnections = generatedConnectionData[originalLaneConnection.modifiedConnections];
                                    originalGeneratedConnections.CopyFrom(generatedConnections);
                                    /*temp modified connection may have updated lane position and other details, update*/
                                    originalLaneConnection.laneIndex = tempModifiedLaneConnection.laneIndex;
                                    originalLaneConnection.carriagewayAndGroup = tempModifiedLaneConnection.carriagewayAndGroup;
                                    originalLaneConnection.lanePosition = tempModifiedLaneConnection.lanePosition;
                                    originalLaneConnection.edgeEntity = sourceEdgeEntity;
                                    originalLaneConnections[k] = originalLaneConnection;
                                    commandBuffer.AddComponent<Deleted>(tempModifiedLaneConnection.modifiedConnections);
                                    break;
                                }
                            }

                            updated = true;
                        }
                    }
                }
            }

            private void CreateModifiedConnections(int index, DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections, NativeList<ModifiedLaneConnections> originalLaneConnections, Temp tempNode, Temp tempEdge, ref ModifiedLaneConnections modifiedLaneConnection, ref bool updated)
            {
                modifiedLaneConnection.edgeEntity = tempEdge.m_Original;
                modifiedLaneConnections[index] = modifiedLaneConnection;

                Logger.DebugTool($"[{index}] no delete, swap (CreateModifiedConnections)");
                Logger.DebugTool($"[{index}] {tempNode.m_Original} has connections ({originalLaneConnections.Length})");
                if (generatedConnectionData.HasBuffer(modifiedLaneConnection.modifiedConnections))
                {

                    DynamicBuffer<GeneratedConnection> generatedConnections = generatedConnectionData[modifiedLaneConnection.modifiedConnections];
                    Logger.DebugTool($"[{index}] {tempNode.m_Original} patching generated connections || originalNode: {tempNode.m_Original}");
                    DataOwner dataOwner = dataOwnerData[modifiedLaneConnection.modifiedConnections];
                    dataOwner.entity = tempNode.m_Original;
                    for (var k = 0; k < generatedConnections.Length; k++)
                    {
                        GeneratedConnection connection = generatedConnections[k];
                        connection.sourceEntity = tempEdge.m_Original;
                        connection.targetEntity = tempData[connection.targetEntity].m_Original;
                        generatedConnections[k] = connection;
                    }
                    
                    generatedConnections.AsNativeArray().Sort(default(GeneratedConnectionComparer));

                    commandBuffer.SetComponent<DataOwner>(modifiedLaneConnection.modifiedConnections, dataOwner);
                    commandBuffer.AddComponent<PrefabRef>(modifiedLaneConnection.modifiedConnections, new PrefabRef(fakePrefabRef));
                    commandBuffer.RemoveComponent<DataTemp>(modifiedLaneConnection.modifiedConnections);
                    commandBuffer.RemoveComponent<CustomLaneConnection>(modifiedLaneConnection.modifiedConnections);

                    originalLaneConnections.Add(modifiedLaneConnection);
                    updated = true;
                }
            }
        }
    }
}
