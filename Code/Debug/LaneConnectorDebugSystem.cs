using System.Collections.Generic;
using Colossal;
using Colossal.Mathematics;
using Game.Common;
using Game.Debug;
using Game.Net;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Traffic.Components.PrioritySigns;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Traffic.Debug
{
    public partial class LaneConnectorDebugSystem : BaseDebugSystem
    {
        private GizmosSystem _gizmosSystem;
        private EntityQuery _query;
        private EntityQuery _query2;

        private Option _connectorOption;
        private Option _prioritiesOption;
        private Option _connectionsOption;
        private Option _tempOnlyOption;

        public bool GizmoEnabled => _connectionsOption.enabled;

        protected override void OnCreate() {
            base.OnCreate();
            base.Enabled = false;

            _gizmosSystem = World.GetOrCreateSystemManaged<GizmosSystem>();
            _query = GetEntityQuery(new EntityQueryDesc()
            {
                All = new [] {ComponentType.ReadOnly<EditLaneConnections>()},
                Any = new[] { ComponentType.ReadOnly<Connector>(), ComponentType.ReadOnly<Connection>(), ComponentType.ReadOnly<EditIntersection>(), ComponentType.ReadOnly<ModifiedIntersection>(), ComponentType.ReadOnly<ModifiedConnections>(),  ComponentType.ReadOnly<GeneratedConnection>(), ComponentType.Exclude<Hidden>(),  }
            });
            _query2 = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { ComponentType.ReadOnly<LaneHandle>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>(), }
            });

            _connectorOption = AddOption("Connectors", false);
            _connectionsOption = AddOption("Connections", false);
            _prioritiesOption = AddOption("Priorities", false);
            _tempOnlyOption = AddOption("Temp Only", false);
            RequireAnyForUpdate(_query, _query2);
        }

        protected override void OnUpdate() {
            base.OnUpdate();

            JobHandle jobHandle = Dependency;
            if (!_query.IsEmptyIgnoreFilter)
            {
                GizmoJob jobData = new GizmoJob()
                {
                    connectorOption = _connectorOption.enabled,
                    connectionsOption = _connectionsOption.enabled,
                    tempOnlyOption = _tempOnlyOption.enabled,
                    entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                    connectorType = SystemAPI.GetComponentTypeHandle<Connector>(true),
                    editIntersectionType = SystemAPI.GetComponentTypeHandle<EditIntersection>(true),
                    modifiedIntersectionType = SystemAPI.GetComponentTypeHandle<ModifiedIntersection>(true),
                    modifiedConnectionsType = SystemAPI.GetComponentTypeHandle<ModifiedConnections>(true),
                    connectionType = SystemAPI.GetBufferTypeHandle<Connection>(true),
                    generatedConnectionType = SystemAPI.GetBufferTypeHandle<GeneratedConnection>(true),
                    tempType = SystemAPI.GetComponentTypeHandle<Temp>(true),
                    nodeData = SystemAPI.GetComponentLookup<Node>(true),
                    tempData = SystemAPI.GetComponentLookup<Temp>(true),
                    gizmoBatcher = _gizmosSystem.GetGizmosBatcher(out JobHandle dependencies)
                };
                jobHandle = jobData.ScheduleParallel(_query, JobHandle.CombineDependencies(Dependency, dependencies));
                _gizmosSystem.AddGizmosBatcherWriter(jobHandle);
            }
            if (!_query2.IsEmptyIgnoreFilter)
            {
                jobHandle = new GizmoJob2()
                {
                    entityTypeHandle = SystemAPI.GetEntityTypeHandle(),
                    laneHandleType = SystemAPI.GetComponentTypeHandle<LaneHandle>(true),
                    highlightedType = SystemAPI.GetComponentTypeHandle<Highlighted>(true),
                    connectionType = SystemAPI.GetBufferTypeHandle<Connection>(true),
                    laneHandlesOption = _prioritiesOption.enabled,
                    gizmoBatcher = _gizmosSystem.GetGizmosBatcher(out JobHandle dependencies)
                }.Schedule(_query2, JobHandle.CombineDependencies(jobHandle, dependencies));
                _gizmosSystem.AddGizmosBatcherWriter(jobHandle);
            }
            Dependency = jobHandle;
        }

        private void RefreshGizmoDebug(DebugUI.Field<int> field, int i) {
            RefreshGizmoDebug();
        }

        public void RefreshGizmoDebug() {
            DebugUI.Panel panel = DebugManager.instance.GetPanel("Traffic_Debug", true, -1);
            DebugUI.Container container = new DebugUI.Container("Lane Connector Tool");
            InitGizmoFields(container, "Lane Connector");
            panel.children.Clear();
            panel.children.Add(container);
        }

        private void InitGizmoFields(DebugUI.Container container, string name) {
            DebugUI.EnumField item = new DebugUI.EnumField
            {
                displayName = name,
                getter = (() => Enabled ? 1 : 0),
                setter = delegate(int value) { Enabled = value != 0; },
                autoEnum = typeof(ToggleEnum),
                onValueChanged = RefreshGizmoDebug,
                getIndex = (() => Enabled ? 1 : 0),
                setIndex = delegate { }
            };
            container.children.Add(item);
            if (Enabled)
            {
                BaseDebugSystem baseDebugSystem = this;
                List<BaseDebugSystem.Option> options = baseDebugSystem.options;
                if (options.Count != 0)
                {
                    DebugUI.Container container2 = new DebugUI.Container();
                    for (int i = 0; i < options.Count; i++)
                    {
                        BaseDebugSystem.Option option = options[i];
                        DebugUI.EnumField item2 = new DebugUI.EnumField
                        {
                            displayName = option.name,
                            getter = (() => option.enabled ? 1 : 0),
                            setter = delegate(int value) { option.enabled = ((value != 0) ? true : false); },
                            autoEnum = typeof(ToggleEnum),
                            getIndex = (() => option.enabled ? 1 : 0),
                            setIndex = delegate { }
                        };
                        container2.children.Add(item2);
                    }
                    container.children.Add(container2);
                }
                baseDebugSystem.OnEnabled(container);
            }
        }
        
        private enum ToggleEnum
        {
            Disabled,
            Enabled
        }
        
        private struct GizmoJob2 : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<LaneHandle> laneHandleType;
            [ReadOnly] public ComponentTypeHandle<Highlighted> highlightedType;
            [ReadOnly] public BufferTypeHandle<Connection> connectionType;
            [ReadOnly] public bool laneHandlesOption;
            
            public GizmoBatcher gizmoBatcher;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                NativeArray<LaneHandle> handles = chunk.GetNativeArray(ref laneHandleType);
                BufferAccessor<Connection> connectionsAccessor = chunk.GetBufferAccessor(ref connectionType);

                bool hasConnections = chunk.Has(ref connectionType);
                bool isHighlighted = chunk.Has(ref highlightedType);
                
                for (int i = 0; i < entities.Length; i++)
                {
                    LaneHandle handle = handles[i];
                    gizmoBatcher.DrawBezier(handle.curve, isHighlighted ? Color.green : Color.cyan);

                    if (hasConnections)
                    {
                        DynamicBuffer<Connection> connections = connectionsAccessor[i];

                        if (laneHandlesOption)
                        {
                            for (var j = 0; j < connections.Length; j++)
                            {
                                Connection connection = connections[j];
                                gizmoBatcher.DrawBezier(connection.curve, isHighlighted ? Color.green : Color.blue);
                            }
                        }
                    }
                }
            }
        } 

        private struct GizmoJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<Temp> tempType;
            [ReadOnly] public ComponentTypeHandle<Connector> connectorType;
            [ReadOnly] public ComponentTypeHandle<EditIntersection> editIntersectionType;
            [ReadOnly] public ComponentTypeHandle<ModifiedIntersection> modifiedIntersectionType;
            [ReadOnly] public ComponentTypeHandle<ModifiedConnections> modifiedConnectionsType;
            [ReadOnly] public ComponentLookup<Node> nodeData;
            [ReadOnly] public ComponentLookup<Temp> tempData;
            [ReadOnly] public BufferTypeHandle<Connection> connectionType;
            [ReadOnly] public BufferTypeHandle<GeneratedConnection> generatedConnectionType;
            [ReadOnly] public bool connectorOption;
            [ReadOnly] public bool connectionsOption;
            [ReadOnly] public bool tempOnlyOption;
            public GizmoBatcher gizmoBatcher;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                bool hasTemp = chunk.Has(ref tempType);
                NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                
                if (chunk.Has(ref editIntersectionType))
                {
                    NativeArray<EditIntersection> editIntersections = chunk.GetNativeArray(ref editIntersectionType);
                    for (var i = 0; i < editIntersections.Length; i++)
                    {
                        Entity entity = editIntersections[i].node;
                        if (nodeData.HasComponent(entity))
                        {
                            Node node = nodeData[entity];
                            gizmoBatcher.DrawWireNode(node.m_Position, 4f, hasTemp ? new Color(0f, 0.37f, 0.88f) : Color.cyan);
                        }
                    }
                } 
                else if (chunk.Has(ref modifiedConnectionsType))
                {
                    // NativeArray<ModifiedIntersection> modifiedIntersections = chunk.GetNativeArray(ref modifiedIntersectionType);
                    for (var i = 0; i < entities.Length; i++)
                    {
                        Entity entity = entities[i];
                        if (nodeData.HasComponent(entity))
                        {
                            Node node = nodeData[entity];
                            gizmoBatcher.DrawWireNode(node.m_Position, 3f, new Color(1f, 0.43f, 0f));
                        }
                    }
                }
                
                if (chunk.Has(ref tempType))
                {
                    if (chunk.Has(ref editIntersectionType))
                    {
                        NativeArray<EditIntersection> editIntersections = chunk.GetNativeArray(ref editIntersectionType);
                        for (var i = 0; i < editIntersections.Length; i++)
                        {
                            Entity entity = editIntersections[i].node;
                            if (nodeData.HasComponent(entity))
                            {
                                Node node = nodeData[entity];
                                gizmoBatcher.DrawWireNode(node.m_Position, 4f, new Color(0f, 0.37f, 0.88f));
                            }
                        }
                    }
                }
                else if (chunk.Has(ref connectorType) && connectorOption)
                {
                    NativeArray<Connector> connector = chunk.GetNativeArray(ref connectorType);

                    for (var i = 0; i < connector.Length; i++)
                    {
                        float3 position = connector[i].position;
                        gizmoBatcher.DrawWireNode(position, 1f, Color.green);
                        gizmoBatcher.DrawRay(position, connector[i].direction, Color.magenta);
                    }
                }
                else if (chunk.Has(ref connectionType) && connectionsOption)
                {
                    BufferAccessor<Connection> connectionsAccessor = chunk.GetBufferAccessor(ref connectionType);
                    for (var i = 0; i < connectionsAccessor.Length; i++)
                    {
                        DynamicBuffer<Connection> connections = connectionsAccessor[i];
                        for (var j = 0; j < connections.Length; j++)
                        {
                            Connection connection = connections[j];
                            gizmoBatcher.DrawBezier(connection.curve, Color.magenta, MathUtils.Length(connection.curve));
                        }
                    }
                }

                if (!hasTemp && tempOnlyOption)
                {
                    return;
                }
#if DEBUG_GIZMO
                if (chunk.Has(ref generatedConnectionType))
                {
                    BufferAccessor<GeneratedConnection> bufferAccessor = chunk.GetBufferAccessor(ref generatedConnectionType);
                    for (var i = 0; i < bufferAccessor.Length; i++)
                    {
                        // hasTemp = tempData.HasComponent(entities[i]);
                        DynamicBuffer<GeneratedConnection> dynamicBuffer = bufferAccessor[i];
                        for (var j = 0; j < dynamicBuffer.Length; j++)
                        {
                            GeneratedConnection connection = dynamicBuffer[j];
                            Color color = (hasTemp && tempOnlyOption) ? new Color(1f, 0.48f, 0f) : Color.yellow;
                            gizmoBatcher.DrawCurve(connection.debug_bezier, MathUtils.Length(connection.debug_bezier), color);
                            float3 pos = MathUtils.Position(connection.debug_bezier, 0.5f);
                            float3 dir = math.normalize(MathUtils.Tangent(connection.debug_bezier, 0.5f));
                            gizmoBatcher.DrawArrowHead(pos, dir, color);
                        }
                    }
                }
#endif
            }
        }
    }
}
