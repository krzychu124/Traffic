using Game.Common;
using Game.Net;
using Game.Prefabs;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Traffic.UISystems;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using LaneConnection = Traffic.Components.LaneConnections.LaneConnection;

namespace Traffic.Tools
{
    public partial class LaneConnectorToolSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        public struct ApplyLaneConnectionsActionJob : IJob
        {
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<Deleted> deletedData;
            [ReadOnly] public ComponentLookup<Connector> connectorData;
            [ReadOnly] public BufferLookup<Connection> connectionsBuffer;
            [ReadOnly] public BufferLookup<ConnectorElement> connectorElementBuffer;
            [ReadOnly] public BufferLookup<ModifiedLaneConnections> modifiedConnectionBuffer;
            [ReadOnly] public BufferLookup<GeneratedConnection> generatedConnectionBuffer;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgeBuffer;
            [ReadOnly] public Entity editIntersectionEntity;
            [ReadOnly] public Entity fakePrefabRef;
            [ReadOnly] public ActionOverlayData actionData;
            public BufferLookup<LaneConnection> laneConnectionsBuffer;
            public EntityCommandBuffer commandBuffer;

            public void Execute()
            {
                if (!connectorElementBuffer.HasBuffer(editIntersectionEntity))
                {
                    return;
                }

                Entity nodeEntity = actionData.entity;
                DynamicBuffer<ConnectorElement> connectorElements = connectorElementBuffer[editIntersectionEntity];
                NativeHashSet<ModifiedLaneConnections> generatedModifiedConnections = new NativeHashSet<ModifiedLaneConnections>(connectorElements.Length, Allocator.Temp);
                if (actionData.mode == ModUISystem.ActionOverlayPreview.RemoveAllConnections)
                {
                    for (var i = 0; i < connectorElements.Length; i++)
                    {
                        ConnectorElement element = connectorElements[i];
                        if (connectorData.HasComponent(element.entity))
                        {
                            Connector connector = connectorData[element.entity];
                            if ((connector.connectorType & (ConnectorType.Target | ConnectorType.TwoWay)) != 0)
                            {
                                continue;
                            }
                            Entity modified = commandBuffer.CreateEntity();
                            commandBuffer.AddComponent<DataOwner>(modified, new DataOwner(connector.node));
                            commandBuffer.AddComponent<PrefabRef>(modified, new PrefabRef(fakePrefabRef));
                            commandBuffer.AddBuffer<GeneratedConnection>(modified);

                            ModifiedLaneConnections modifiedLaneConnections = new ModifiedLaneConnections()
                            {
                                edgeEntity = connector.edge,
                                laneIndex = connector.laneIndex,
                                modifiedConnections = modified,
                            };
                            generatedModifiedConnections.Add(modifiedLaneConnections);
                        }
                    }
                }
                else if (actionData.mode == ModUISystem.ActionOverlayPreview.RemoveUTurns)
                {
                    DynamicBuffer<ModifiedLaneConnections> modifiedConnections = default;
                    if (modifiedConnectionBuffer.HasBuffer(nodeEntity))
                    {
                        modifiedConnections = modifiedConnectionBuffer[nodeEntity];
                    }
                    NativeList<ConnectorItem> sourceConnectors = new NativeList<ConnectorItem>(Allocator.Temp);
                    FilterSourceConnectors(connectorElements, sourceConnectors);

                    for (var i = 0; i < sourceConnectors.Length; i++)
                    {
                        ConnectorItem sourceConnectorItem = sourceConnectors[i];
                        Connector sourceConnector = sourceConnectorItem.connector;
                        bool hasModifiedConnectorConnections = false;
                        if (modifiedConnections.IsCreated)
                        {
                            // contains modified connections
                            for (var j = 0; j < modifiedConnections.Length; j++)
                            {
                                ModifiedLaneConnections modifiedConnection = modifiedConnections[j];
                                if (generatedConnectionBuffer.HasBuffer(modifiedConnection.modifiedConnections) &&
                                    modifiedConnection.edgeEntity.Equals(sourceConnector.edge) &&
                                    modifiedConnection.laneIndex == sourceConnector.laneIndex)
                                {
                                    hasModifiedConnectorConnections = true;
                                    // create copy of container, ignore U-turn connections
                                    Entity modified = commandBuffer.CreateEntity();
                                    commandBuffer.AddComponent<DataOwner>(modified, new DataOwner(sourceConnector.node));
                                    commandBuffer.AddComponent<PrefabRef>(modified, new PrefabRef(fakePrefabRef));
                                    DynamicBuffer<GeneratedConnection> newConnections = commandBuffer.AddBuffer<GeneratedConnection>(modified);
                                    DynamicBuffer<GeneratedConnection> connections = generatedConnectionBuffer[modifiedConnection.modifiedConnections];
                                    for (var k = 0; k < connections.Length; k++)
                                    {
                                        GeneratedConnection connection = connections[k];
                                        if (!connection.sourceEntity.Equals(connection.targetEntity))
                                        {
                                            newConnections.Add(connection);
                                        }
                                    }
                                    // update reference
                                    modifiedConnection.modifiedConnections = modified;
                                    generatedModifiedConnections.Add(modifiedConnection);
                                }
                            }
                        }
                        if (!hasModifiedConnectorConnections)
                        {
                            Entity modified = commandBuffer.CreateEntity();
                            commandBuffer.AddComponent<DataOwner>(modified, new DataOwner(sourceConnector.node));
                            commandBuffer.AddComponent<PrefabRef>(modified, new PrefabRef(fakePrefabRef));
                            DynamicBuffer<GeneratedConnection> connections = commandBuffer.AddBuffer<GeneratedConnection>(modified);
                            ModifiedLaneConnections modifiedLaneConnections = new ModifiedLaneConnections()
                            {
                                edgeEntity = sourceConnector.edge,
                                laneIndex = sourceConnector.laneIndex,
                                modifiedConnections = modified,
                            };
                            GenerateNonUturnConnections(sourceConnectorItem, connections);
                            generatedModifiedConnections.Add(modifiedLaneConnections);
                        }
                    }
                }
                else if (actionData.mode == ModUISystem.ActionOverlayPreview.RemoveUnsafe)
                {
                    DynamicBuffer<ModifiedLaneConnections> modifiedConnections = default;
                    if (modifiedConnectionBuffer.HasBuffer(nodeEntity))
                    {
                        modifiedConnections = modifiedConnectionBuffer[nodeEntity];
                    }
                    NativeList<ConnectorItem> sourceConnectors = new NativeList<ConnectorItem>(Allocator.Temp);
                    FilterSourceConnectors(connectorElements, sourceConnectors);

                    for (int i = 0; i < sourceConnectors.Length; i++)
                    {
                        ConnectorItem sourceConnectorItem = sourceConnectors[i];
                        Connector sourceConnector = sourceConnectorItem.connector;
                        bool hasModifiedConnectorConnections = false;
                        if (modifiedConnections.IsCreated)
                        {
                            // contains modified connections
                            for (var j = 0; j < modifiedConnections.Length; j++)
                            {
                                ModifiedLaneConnections modifiedConnection = modifiedConnections[j];
                                if (generatedConnectionBuffer.HasBuffer(modifiedConnection.modifiedConnections) &&
                                    modifiedConnection.edgeEntity.Equals(sourceConnector.edge) &&
                                    modifiedConnection.laneIndex == sourceConnector.laneIndex)
                                {
                                    hasModifiedConnectorConnections = true;
                                    // create copy of container, ignore unsafe connections
                                    Entity modified = commandBuffer.CreateEntity();
                                    commandBuffer.AddComponent<DataOwner>(modified, new DataOwner(sourceConnector.node));
                                    commandBuffer.AddComponent<PrefabRef>(modified, new PrefabRef(fakePrefabRef));
                                    DynamicBuffer<GeneratedConnection> newConnections = commandBuffer.AddBuffer<GeneratedConnection>(modified);
                                    DynamicBuffer<GeneratedConnection> oldConnections = generatedConnectionBuffer[modifiedConnection.modifiedConnections];
                                    for (int k = 0; k < oldConnections.Length; k++)
                                    {
                                        GeneratedConnection connection = oldConnections[k];
                                        if (!connection.isUnsafe)
                                        {
                                            newConnections.Add(connection);
                                        }
                                    }
                                    // update reference
                                    modifiedConnection.modifiedConnections = modified;
                                    generatedModifiedConnections.Add(modifiedConnection);
                                }
                            }
                        }
                        if (!hasModifiedConnectorConnections)
                        {
                            int unsafeIdx = -1;
                            if (laneConnectionsBuffer.HasBuffer(sourceConnectorItem.entity))
                            {
                                DynamicBuffer<LaneConnection> laneConnections = laneConnectionsBuffer[sourceConnectorItem.entity];
                                for (var j = 0; j < laneConnections.Length; j++)
                                {
                                    LaneConnection laneConnection = laneConnections[j];
                                    DynamicBuffer<Connection> currentConnections = connectionsBuffer[laneConnection.connection];
                                    for (var k = 0; k < currentConnections.Length; k++)
                                    {
                                        if (currentConnections[k].isUnsafe)
                                        {
                                            unsafeIdx = k;
                                            break;
                                        }
                                    }
                                }
                            }
                            if (unsafeIdx == -1)
                            {
                                // no modified connections of the lane, leave as vanilla
                                continue;
                            }

                            Entity modified = commandBuffer.CreateEntity();
                            commandBuffer.AddComponent<DataOwner>(modified, new DataOwner(sourceConnector.node));
                            commandBuffer.AddComponent<PrefabRef>(modified, new PrefabRef(fakePrefabRef));
                            DynamicBuffer<GeneratedConnection> connections = commandBuffer.AddBuffer<GeneratedConnection>(modified);
                            ModifiedLaneConnections modifiedLaneConnections = new ModifiedLaneConnections()
                            {
                                edgeEntity = sourceConnector.edge,
                                laneIndex = sourceConnector.laneIndex,
                                modifiedConnections = modified,
                            };
                            GenerateSafeConnections(sourceConnectorItem, connections);
                            generatedModifiedConnections.Add(modifiedLaneConnections);
                        }
                    }
                }

                NativeArray<ModifiedLaneConnections> generatedConnections = generatedModifiedConnections.ToNativeArray(Allocator.Temp);
                if (modifiedConnectionBuffer.HasBuffer(nodeEntity))
                {
                    // mark all existing linked ModifiedLaneConnection entities for deletion
                    DynamicBuffer<ModifiedLaneConnections> oldModifiedConnections = modifiedConnectionBuffer[nodeEntity];
                    for (var i = 0; i < oldModifiedConnections.Length; i++)
                    {
                        commandBuffer.AddComponent<Deleted>(oldModifiedConnections[i].modifiedConnections);
                    }
                }

                // fill intersection node ModifiedLaneConnections with new connections
                DynamicBuffer<ModifiedLaneConnections> updateModifiedConnections = modifiedConnectionBuffer.HasBuffer(nodeEntity) 
                    ? commandBuffer.SetBuffer<ModifiedLaneConnections>(nodeEntity) 
                    : commandBuffer.AddBuffer<ModifiedLaneConnections>(nodeEntity);
                updateModifiedConnections.CopyFrom(generatedConnections);
                // refresh EditIntersection container
                if (editIntersectionEntity != Entity.Null)
                {
                    commandBuffer.AddComponent<Updated>(editIntersectionEntity);
                    for (int i = 0; i < connectorElements.Length; i++)
                    {
                        ConnectorElement connectorElement = connectorElements[i];
                        DynamicBuffer<LaneConnection> connections = laneConnectionsBuffer[connectorElement.entity];
                        for (int j = 0; j < connections.Length; j++)
                        {
                            //destroy connections entities
                            commandBuffer.DestroyEntity(connections[j].connection);
                        }
                        //reset buffer since all referenced connection entities has been destroyed
                        commandBuffer.AddBuffer<LaneConnection>(connectorElement.entity);
                    }
                }

                // reapply tag to mark node as containing modified connections
                commandBuffer.AddComponent<ModifiedConnections>(nodeEntity);
                // refresh intersection node to apply changes
                commandBuffer.AddComponent<Updated>(nodeEntity);
                if (!connectedEdgeBuffer.HasBuffer(nodeEntity))
                {
                    return;
                }
                
                DynamicBuffer<ConnectedEdge> edges = connectedEdgeBuffer[nodeEntity];
                if (edges.Length > 0)
                {
                    //update connected nodes of every edge
                    for (var j = 0; j < edges.Length; j++)
                    {
                        Entity edgeEntity = edges[j].m_Edge;
                        if (!deletedData.HasComponent(edgeEntity))
                        {
                            Edge e = edgeData[edgeEntity];
                            commandBuffer.AddComponent<Updated>(edgeEntity);
                            Entity otherNode = e.m_Start == nodeEntity ? e.m_End : e.m_Start;
                            commandBuffer.AddComponent<Updated>(otherNode);
                        }
                    }
                }
            }

            private void GenerateNonUturnConnections(ConnectorItem connectorItem, DynamicBuffer<GeneratedConnection> resultConnections)
            {
                DynamicBuffer<LaneConnection> connectorConnections = laneConnectionsBuffer[connectorItem.entity];
                NativeHashSet<Entity> visitedConnections = new NativeHashSet<Entity>(connectorConnections.Length, Allocator.Temp);
                for (var j = 0; j < connectorConnections.Length; j++)
                {
                    LaneConnection laneEndConnection = connectorConnections[j];
                    if (!visitedConnections.Contains(laneEndConnection.connection))
                    {
                        visitedConnections.Add(laneEndConnection.connection);
                        DynamicBuffer<Connection> connections = connectionsBuffer[laneEndConnection.connection];
                        for (var k = 0; k < connections.Length; k++)
                        {
                            Connection connection = connections[k];
                            if (!connection.sourceEdge.Equals(connection.targetEdge))
                            {
                                resultConnections.Add(new GeneratedConnection()
                                {
                                    sourceEntity = connection.sourceEdge,
                                    targetEntity = connection.targetEdge,
                                    method = connection.method,
                                    isUnsafe = connection.isUnsafe,
                                    laneIndexMap = new int2(connectorItem.connector.laneIndex, connection.targetNode.GetLaneIndex() & 0xff),
                                });
                            }
                        }
                    }
                }
                visitedConnections.Dispose();
            }
            
            private void GenerateSafeConnections(ConnectorItem connectorItem, DynamicBuffer<GeneratedConnection> resultConnections)
            {
                DynamicBuffer<LaneConnection> connectorConnections = laneConnectionsBuffer[connectorItem.entity];
                NativeHashSet<Entity> visitedConnections = new NativeHashSet<Entity>(connectorConnections.Length, Allocator.Temp);
                for (var j = 0; j < connectorConnections.Length; j++)
                {
                    LaneConnection laneEndConnection = connectorConnections[j];
                    if (!visitedConnections.Contains(laneEndConnection.connection))
                    {
                        visitedConnections.Add(laneEndConnection.connection);
                        DynamicBuffer<Connection> connections = connectionsBuffer[laneEndConnection.connection];
                        for (var k = 0; k < connections.Length; k++)
                        {
                            Connection connection = connections[k];
                            if (!connection.isUnsafe)
                            {
                                resultConnections.Add(new GeneratedConnection()
                                {
                                    sourceEntity = connection.sourceEdge,
                                    targetEntity = connection.targetEdge,
                                    method = connection.method,
                                    isUnsafe = false,
                                    laneIndexMap = new int2(connectorItem.connector.laneIndex, connection.targetNode.GetLaneIndex() & 0xff),
                                });
                            }
                        }
                    }
                }
                visitedConnections.Dispose();
            }

            private void FilterSourceConnectors(DynamicBuffer<ConnectorElement> source, NativeList<ConnectorItem> result)
            {
                for (var i = 0; i < source.Length; i++)
                {
                    ConnectorElement element = source[i];
                    if (connectorData.HasComponent(element.entity))
                    {
                        Connector connector = connectorData[element.entity];
                        if (connector.connectorType == ConnectorType.Source &&
                            laneConnectionsBuffer.HasBuffer(element.entity))
                        {
                            result.Add(new ConnectorItem() { connector = connector, entity = element.entity });
                        }
                    }
                }
            }

            private struct ConnectorItem
            {
                public Entity entity;
                public Connector connector;
            }
        }
    }
}
