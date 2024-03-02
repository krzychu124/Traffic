#define DEBUG_TOOL
using System.Text;
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
    public partial class ApplyLaneConnectionsSystem : GameSystemBase
    {
        private EntityQuery _tempQuery;
        private ToolOutputBarrier _toolOutputBarrier;

        protected override void OnCreate() {
            base.OnCreate();
            _toolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();
            _tempQuery = GetEntityQuery(ComponentType.ReadOnly<ConnectedEdge>(), ComponentType.ReadOnly<ModifiedLaneConnections>(), ComponentType.ReadOnly<Temp>());
            RequireForUpdate(_tempQuery);
        }

        protected override void OnUpdate() {
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

            // HandleTempConnectionsJob job = new HandleTempConnectionsJob()
            // {
            //     entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
            //     connectionDataTypeHandle = SystemAPI.GetComponentTypeHandle<ConnectionData>(true),
            //     dataOwnerTypeHandle = SystemAPI.GetComponentTypeHandle<DataOwner>(true),
            //     laneData = SystemAPI.GetComponentLookup<Lane>(true),
            //     ownerData = SystemAPI.GetComponentLookup<Owner>(true),
            //     edgeData = SystemAPI.GetComponentLookup<Edge>(true),
            //     subLaneData = SystemAPI.GetBufferLookup<SubLane>(true),
            //     connectedEdgesData = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
            //     generatedConnectionData = SystemAPI.GetBufferLookup<GeneratedConnection>(true),
            //     modifiedLaneConnectionData = SystemAPI.GetBufferLookup<ModifiedLaneConnections>(true),
            //     tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true),
            //     commandBuffer = _toolOutputBarrier.CreateCommandBuffer().AsParallelWriter(),
            // };
            //
            // JobHandle jobHandle = job.Schedule(_tempQuery, Dependency);
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

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
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
                    Temp temp = temps[i];
                    DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections = modifiedConnectionsBufferAccessor[i];
                    Logger.DebugTool($"Patching: {entity}, temp: {{original: {temp.m_Original} flags: {temp.m_Flags}}}, modifiedConnections: {modifiedLaneConnections.Length}");
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
                    if (temp.m_Original != Entity.Null && modifiedLaneConnectionData.HasBuffer(temp.m_Original))
                    {
                        DynamicBuffer<ModifiedLaneConnections> modified = modifiedLaneConnectionData[temp.m_Original];
                        Logger.DebugTool($"Original {temp.m_Original}, flags: {temp.m_Flags}, modifiedConnections: {modified.Length}");
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
                        Logger.DebugTool($"Original GeneratedConnections ({temp.m_Original}): \n{sb2}");
                    }

                    bool updated = false;
                    for (var j = 0; j < modifiedLaneConnections.Length; j++)
                    {
                        ModifiedLaneConnections modifiedLaneConnection = modifiedLaneConnections[j];
                        if (tempData.HasComponent(modifiedLaneConnection.modifiedConnections))
                        {
                            Temp tempConnection = tempData[modifiedLaneConnection.modifiedConnections];
                            Logger.DebugTool($"Testing old connection ({modifiedLaneConnection.modifiedConnections}) temp: {tempConnection.m_Original} flags: {tempConnection.m_Flags}");
                            if (tempData.HasComponent(modifiedLaneConnection.edgeEntity))
                            {
                                Temp tempEdge = tempData[modifiedLaneConnection.edgeEntity];
                                if ((tempConnection.m_Flags & TempFlags.Create) != 0)
                                {
                                    Logger.DebugTool($"[{j}] has Temp edge: {tempEdge.m_Original} ({modifiedLaneConnection.edgeEntity})");
                                    if ((tempEdge.m_Flags & TempFlags.Delete) == 0)
                                    {
                                        modifiedLaneConnection.edgeEntity = tempEdge.m_Original;
                                        modifiedLaneConnections[j] = modifiedLaneConnection;

                                        Logger.DebugTool($"[{j}] no delete, swap");
                                        if (temp.m_Original != Entity.Null && modifiedLaneConnectionData.HasBuffer(temp.m_Original))
                                        {
                                            DynamicBuffer<ModifiedLaneConnections> originalLaneConnections = modifiedLaneConnectionData[temp.m_Original];

                                            Logger.DebugTool($"[{j}] {temp.m_Original} has connections ({originalLaneConnections.Length})");
                                            if (generatedConnectionData.HasBuffer(modifiedLaneConnection.modifiedConnections))
                                            {

                                                DynamicBuffer<GeneratedConnection> generatedConnections = generatedConnectionData[modifiedLaneConnection.modifiedConnections];
                                                Logger.DebugTool($"[{j}] {temp.m_Original} patching generated connections");
                                                DataOwner dataOwner = dataOwnerData[modifiedLaneConnection.modifiedConnections];
                                                dataOwner.entity = temp.m_Original;
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
                                else if ((tempConnection.m_Flags & TempFlags.Modify) != 0 && tempConnection.m_Original != Entity.Null)
                                {
                                    Logger.DebugTool($"[{j}] no Temp edge: {tempEdge.m_Original} ({modifiedLaneConnection.edgeEntity})");
                                    if ((tempEdge.m_Flags & TempFlags.Delete) == 0)
                                    {
                                        modifiedLaneConnection.edgeEntity = tempEdge.m_Original;
                                        modifiedLaneConnections[j] = modifiedLaneConnection;

                                        Logger.DebugTool($"[{j}] no delete, swap");
                                        if (temp.m_Original != Entity.Null && modifiedLaneConnectionData.HasBuffer(temp.m_Original))
                                        {
                                            DynamicBuffer<ModifiedLaneConnections> originalLaneConnections = modifiedLaneConnectionData[temp.m_Original];

                                            Logger.DebugTool($"[{j}] {temp.m_Original} has connections ({originalLaneConnections.Length})");
                                            if (generatedConnectionData.HasBuffer(modifiedLaneConnection.modifiedConnections))
                                            {

                                                DynamicBuffer<GeneratedConnection> generatedConnections = generatedConnectionData[modifiedLaneConnection.modifiedConnections];
                                                Logger.DebugTool($"[{j}] {temp.m_Original} patching generated connections");
                                                DataOwner dataOwner = dataOwnerData[modifiedLaneConnection.modifiedConnections];
                                                dataOwner.entity = temp.m_Original;
                                                for (var k = 0; k < generatedConnections.Length; k++)
                                                {
                                                    GeneratedConnection connection = generatedConnections[k];
                                                    connection.sourceEntity = (temp.m_Flags & (TempFlags.Replace | TempFlags.Combine)) != 0 ? modifiedLaneConnection.edgeEntity : tempEdge.m_Original;
                                                    connection.targetEntity = (temp.m_Flags & (TempFlags.Replace | TempFlags.Combine)) != 0 ? connection.targetEntity : tempData[connection.targetEntity].m_Original;
                                                    generatedConnections[k] = connection;
                                                }

                                                commandBuffer.SetComponent<DataOwner>(modifiedLaneConnection.modifiedConnections, dataOwner);
                                                commandBuffer.RemoveComponent<Temp>(modifiedLaneConnection.modifiedConnections);
                                                commandBuffer.RemoveComponent<CustomLaneConnection>(modifiedLaneConnection.modifiedConnections);

                                                for (var k = 0; k < originalLaneConnections.Length; k++)
                                                {
                                                    if (originalLaneConnections[k].Equals(modifiedLaneConnection))
                                                    {
                                                        Logger.DebugTool(
                                                            $"Found Connection: e: {modifiedLaneConnection.edgeEntity}, idx: {modifiedLaneConnection.laneIndex} m: {modifiedLaneConnection.modifiedConnections}, deleting: {tempConnection.m_Original}");
                                                        commandBuffer.AddComponent<Deleted>(tempConnection.m_Original);
                                                        originalLaneConnections.RemoveAtSwapBack(k);
                                                        break;
                                                    }
                                                }

                                                originalLaneConnections.Add(modifiedLaneConnection);
                                                updated = true;
                                            }
                                        }
                                    }
                                }
                            }
                            if ((tempConnection.m_Flags & TempFlags.Delete) != 0 && tempConnection.m_Original != Entity.Null)
                            {
                                
                                Logger.DebugTool($"[{j}] Trying to delete modifiedConnection: {modifiedLaneConnection.modifiedConnections} | {tempConnection.m_Original}");
                                if (temp.m_Original != Entity.Null && modifiedLaneConnectionData.HasBuffer(temp.m_Original))
                                {
                                    Logger.DebugTool($"[{j}] Delete connection: {modifiedLaneConnection.modifiedConnections} | {tempConnection.m_Original}");
                                    DynamicBuffer<ModifiedLaneConnections> originalLaneConnections = modifiedLaneConnectionData[temp.m_Original];
                                    Logger.DebugTool($"[{j}] {temp.m_Original} has connections ({originalLaneConnections.Length})");
                                    for (var k = 0; k < originalLaneConnections.Length; k++)
                                    {
                                        if (originalLaneConnections[k].modifiedConnections.Equals(tempConnection.m_Original))
                                        {
                                            commandBuffer.AddComponent<Deleted>(tempConnection.m_Original);
                                            originalLaneConnections.RemoveAtSwapBack(k);
                                            Logger.DebugTool($"[{j}] Found modifiedLaneConnection, deleting {tempConnection.m_Original}");
                                            updated = true;
                                            break;
                                        }
                                    }
                                    if (originalLaneConnections.IsEmpty && !isEditNodeChunk)
                                    {
                                        Logger.DebugTool($"No connections left. Removing buffer from {temp.m_Original}");
                                        commandBuffer.RemoveComponent<ModifiedLaneConnections>(temp.m_Original);
                                        commandBuffer.RemoveComponent<ModifiedConnections>(temp.m_Original);
                                        updated = true;
                                    }
                                }
                            }
                        }
                    }
                    
                    if (updated)
                    {
                        commandBuffer.AddComponent<Updated>(temp.m_Original);
                    }
                }
            }
        }

//         private struct HandleTempConnectionsJob : IJobChunk
//         {
//             [ReadOnly] public EntityTypeHandle entityTypeHandle;
//             [ReadOnly] public ComponentTypeHandle<ConnectionData> connectionDataTypeHandle;
//             [ReadOnly] public ComponentTypeHandle<DataOwner> dataOwnerTypeHandle;
//             [ReadOnly] public ComponentLookup<Lane> laneData;
//             [ReadOnly] public ComponentLookup<Owner> ownerData;
//             // [ReadOnly] public ComponentLookup<ConnectionData> connectionData;
//             [ReadOnly] public ComponentLookup<Edge> edgeData;
//             [ReadOnly] public BufferLookup<SubLane> subLaneData;
//             [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgesData;
//             [ReadOnly] public BufferLookup<GeneratedConnection> generatedConnectionData;
//             [ReadOnly] public BufferLookup<ModifiedLaneConnections> modifiedLaneConnectionData;
//             [ReadOnly] public ComponentTypeHandle<Temp> tempTypeHandle;
//             public EntityCommandBuffer.ParallelWriter commandBuffer;
//
//             public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
//                 var entities = chunk.GetNativeArray(entityTypeHandle);
//                 var temps = chunk.GetNativeArray(ref tempTypeHandle);
//                 var connectionData = chunk.GetNativeArray(ref connectionDataTypeHandle);
//                 var dataOwners = chunk.GetNativeArray(ref dataOwnerTypeHandle);
//                 bool hasConnectionData = chunk.Has(ref connectionDataTypeHandle);
//
//                 //TODO Optimize code
//                 NativeList<Entity> updatedNodes = new NativeList<Entity>(Allocator.Temp);
//                 // NativeParallelMultiHashMap<Entity, LaneEnd> touchedLaneEnds = new NativeParallelMultiHashMap<Entity, LaneEnd>(1, Allocator.Temp);
//                 Logger.Debug($"HandleTempConnectionsJob: Processing {entities.Length} entities");
//
//                 for (var i = 0; i < entities.Length; i++)
//                 {
//                     Entity e = entities[i];
//                     Temp temp = temps[i];
//                     if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Create)) != 0 && hasConnectionData)
//                     {
//                         ConnectionData data = connectionData[i];
//                         Entity connectionOwner = dataOwners[i].entity;
//                         Entity laneConnectionEntity = temp.m_Original;
//                         if (connectionOwner != Entity.Null)
//                         {
//                             Logger.DebugTool($"Temp entity: {temp.m_Original}, flags {temp.m_Flags} |  | {data.sourceEdge} -> {data.targetEdge} : {data.laneIndexMap}");
//
//                             if (laneConnectionEntity == Entity.Null)
//                             {
//                                 Entity entity = commandBuffer.CreateEntity(unfilteredChunkIndex);
//                                 commandBuffer.AddComponent<DataOwner>(unfilteredChunkIndex, entity);
//                                 // commandBuffer.AddComponent<PrefabRef>(unfilteredChunkIndex, entity); TODO workaround for vanilla OriginalDelete check (ToolBaseSystem.GetAllowApply())
//                                 commandBuffer.SetComponent<DataOwner>(unfilteredChunkIndex, entity, new DataOwner(connectionOwner));
//                                 commandBuffer.AddBuffer<GeneratedConnection>(unfilteredChunkIndex, entity);
//                                 if ((temp.m_Flags & TempFlags.Replace) != 0)
//                                 {
//                                     commandBuffer.AppendToBuffer(unfilteredChunkIndex, entity, new GeneratedConnection()
//                                     {
//                                         method = data.method,
//                                         sourceEntity = data.sourceEdge,
//                                         targetEntity = data.targetEdge,
//                                         laneIndexMap = data.laneIndexMap,
//                                         isUnsafe = data.isUnsafe,
// #if DEBUG_GIZMO
//                                         debug_bezier = data.curve,
// #endif
//                                     });
//                                 }
//
//                                 commandBuffer.AppendToBuffer(unfilteredChunkIndex, connectionOwner, new ModifiedLaneConnections()
//                                 {
//                                     edgeEntity = data.sourceEdge,
//                                     laneIndex = data.laneIndexMap.x,
//                                     modifiedConnections = entity,
//                                 });
//
//                                 updatedNodes.Add(connectionOwner);
//                                 // touchedLaneEnds.Add(connectionOwner, new LaneEnd(connectionOwner , data.sourceEdge, data.laneIndexMap.x));
//
//                                 continue;
//                             }
//
//                             if ((temp.m_Flags & TempFlags.Delete) != 0)
//                             {
//                                 if (generatedConnectionData.HasBuffer(laneConnectionEntity))
//                                 {
//                                     Logger.Debug($"Deleting Lane connection");
//                                     int index = -1;
//                                     DynamicBuffer<GeneratedConnection> generatedConnections = generatedConnectionData[laneConnectionEntity];
//                                     for (int j = 0; j < generatedConnections.Length; j++)
//                                     {
//                                         GeneratedConnection con = generatedConnections[j];
//                                         Logger.DebugTool($"Testing connection: {con.sourceEntity} => {con.targetEntity} : {con.laneIndexMap}");
//                                         if (con.sourceEntity.Index == data.sourceEdge.Index &&
//                                             con.targetEntity.Index == data.targetEdge.Index &&
//                                             math.all(con.laneIndexMap == data.laneIndexMap))
//                                         {
//                                             index = j;
//                                             Logger.DebugTool($"Found connection: {j}");
//                                             break;
//                                         }
//                                     }
//                                     if (index >= 0)
//                                     {
//                                         generatedConnections.RemoveAtSwapBack(index);
//                                         DynamicBuffer<GeneratedConnection> dynamicBuffer = commandBuffer.SetBuffer<GeneratedConnection>(unfilteredChunkIndex, laneConnectionEntity);
//                                         dynamicBuffer.ResizeUninitialized(generatedConnections.Length);
//                                         for (int k = 0; k < dynamicBuffer.Length; k++)
//                                         {
//                                             dynamicBuffer[k] = generatedConnections[k];
//                                         }
//
//                                         updatedNodes.Add(connectionOwner);
//                                     }
//                                 }
//                             }
//                             else // modify existing
//                             {
//                                 if (generatedConnectionData.HasBuffer(laneConnectionEntity))
//                                 {
//                                     Logger.Debug($"Creating Lane connection");
//                                     GeneratedConnection generatedConnection = new GeneratedConnection()
//                                     {
//                                         method = data.method,
//                                         sourceEntity = data.sourceEdge,
//                                         targetEntity = data.targetEdge,
//                                         laneIndexMap = data.laneIndexMap,
//                                         isUnsafe = data.isUnsafe,
// #if DEBUG_GIZMO
//                                         debug_bezier = data.curve,
// #endif
//                                     };
//                                     commandBuffer.AppendToBuffer(unfilteredChunkIndex, laneConnectionEntity, generatedConnection);
//                                     updatedNodes.Add(connectionOwner);
//                                 }
//                             }
//
//                             // touchedLaneEnds.Add(connectionOwner, new LaneEnd(connectionOwner , data.sourceEdge, data.laneIndexMap.x));
//                         }
//                     }
//                 }
//
//                 if (updatedNodes.Length > 0)
//                 {
//                     // NativeHashSet<LaneEnd> laneEnds = new NativeHashSet<LaneEnd>(4, Allocator.Temp);
//                     for (var i = 0; i < updatedNodes.Length; i++)
//                     {
//                         Entity n = updatedNodes[i];
//                         commandBuffer.AddComponent<Updated>(unfilteredChunkIndex, n);
//                         DynamicBuffer<ConnectedEdge> edges = connectedEdgesData[n];
//                         if (edges.Length > 0)
//                         {
//                             //update connected nodes of every edge
//                             for (var j = 0; j < edges.Length; j++)
//                             {
//                                 commandBuffer.AddComponent<Updated>(unfilteredChunkIndex, edges[j].m_Edge);
//                                 Edge e = edgeData[edges[j].m_Edge];
//                                 Entity otherNode = e.m_Start == n ? e.m_End : e.m_Start;
//                                 commandBuffer.AddComponent<Updated>(unfilteredChunkIndex, otherNode);
//                             }
//
//                             // laneEnds.Clear();
//                             // DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections = modifiedLaneConnectionData[n];
//                             // for (int j = 0; j < modifiedLaneConnections.Length; j++)
//                             // {
//                             //     ModifiedLaneConnections connection = modifiedLaneConnections[j];
//                             //     laneEnds.Add(new LaneEnd(n, connection.edgeEntity, connection.laneIndex));
//                             // }
//
//                             // if (touchedLaneEnds.TryGetFirstValue(n, out LaneEnd end, out NativeParallelMultiHashMapIterator<Entity> it))
//                             // {
//                             //     do
//                             //     {
//                             //         laneEnds.Add(end);
//                             //     } while (touchedLaneEnds.TryGetNextValue(out end, ref it));
//                             //
//                             //     NativeArray<LaneEnd> ends = laneEnds.ToNativeArray(Allocator.Temp);
//                             //     DynamicBuffer<ModifiedLaneConnections> buffer = commandBuffer.SetBuffer<ModifiedLaneConnections>(unfilteredChunkIndex, n);
//                             //     buffer.ResizeUninitialized(ends.Length);
//                             //     for (int j = 0; j < ends.Length; j++)
//                             //     {
//                             //         buffer[j] = new ModifiedLaneConnections() { edgeEntity = ends[j].Edge(), laneIndex = ends[j].LaneIndex() };
//                             //     }
//                             // }
//                         }
//                     }
//                     // laneEnds.Dispose();
//
//                 }
//                 // touchedLaneEnds.Dispose();
//                 updatedNodes.Dispose();
//             }
//
//             private Entity FindLaneConnection(ConnectionData data, DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnections, out int connectionIndex) {
//                 for (var i = 0; i < modifiedLaneConnections.Length; i++)
//                 {
//                     var connection = modifiedLaneConnections[i];
//                     if (connection.edgeEntity == data.sourceEdge && connection.laneIndex == data.laneIndexMap.x)
//                     {
//                         connectionIndex = i;
//                         return connection.modifiedConnections;
//                     }
//                 }
//
//                 connectionIndex = -1;
//                 return Entity.Null;
//             }
//
//             private struct LaneEnd : IEquatable<LaneEnd>
//             {
//                 private int3 _data;
//                 private Entity _edge;
//
//                 public LaneEnd(Entity node, Entity edge, int laneIdx) {
//                     _data = new int3(node.Index, edge.Index, laneIdx);
//                     _edge = edge;
//                 }
//
//                 public bool MatchingOwner(Entity owner) {
//                     return _data.x == owner.Index;
//                 }
//
//                 public int LaneIndex() {
//                     return _data.z;
//                 }
//
//                 public Entity Edge() {
//                     return _edge;
//                 }
//
//                 public bool Equals(LaneEnd other) {
//                     return _data.Equals(other._data);
//                 }
//
//                 public override int GetHashCode() {
//                     return _data.GetHashCode();
//                 }
//             }
//         }
    }
}
