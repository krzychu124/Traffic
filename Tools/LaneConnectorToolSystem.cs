using System;
using Colossal.Collections;
using Colossal.Entities;
using Game.Audio;
using Game.Common;
using Game.Input;
using Game.Net;
using Game.Notifications;
using Game.Prefabs;
using Game.Tools;
using Traffic.Common;
using Traffic.Components;
using Traffic.LaneConnections;
using Traffic.Systems;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using LaneConnection = Traffic.LaneConnections.LaneConnection;
using SubLane = Game.Net.SubLane;

namespace Traffic.Tools
{
    public partial class LaneConnectorToolSystem : ToolBaseSystem
    {
        public enum Mode
        {
            Default,
            ModifyRegularConnections,
            ModifyUnsafeConnections,
        }

        public enum State
        {
            Default,
            SelectingSourceConnector,
            SelectingTargetConnector,
            RemovingSourceConnections,
            RemovingTargetConnections,
        }

        [Flags]
        public enum StateModifier
        {
            AnyConnector,
            RoadOnly = 1,
            TrackOnly = 1 << 1,
            SharedRoadTrack = RoadOnly | TrackOnly,
            MakeUnsafe = 1 << 2,
        }

        public enum Tooltip
        {
            None,
            SelectIntersection,           // pointing intersection node, node selected or different selected - with SelectSourceConnector mode only!
            SelectConnectorToAddOrRemove, // pointing nothing, node selected - with SelectTargetConnector mode only!
            RemoveSourceConnections,      // pointing source only + cancel action possible (has connections)
            RemoveTargetConnections,      // pointing target only + cancel action possible (has connections)
            CreateConnection,             // source connector connections == 0
            ModifyConnections,            // source connector connections > 0
            RemoveConnection,             // pointing target - source+target is existing connection
            CompleteConnection,           // pointing target - source+target is new connection
            // RemoveLaneConnection, // raycast connection line required
        }

        public override string toolID => "Lane Connection Tool";

        private ModRaycastSystem _modRaycastSystem;

        private ProxyAction _applyAction;
        private ProxyAction _secondaryApplyAction;
        private ToolOutputBarrier _toolOutputBarrier;
        private AudioManager _audioManager;
        private NativeList<ControlPoint> _controlPoints;
        private NativeValue<Tooltip> _tooltip;

        private Entity _selectedNode;
        private float _nodeElevation;
        private State _state;
        private StateModifier _stateModifiers = StateModifier.AnyConnector;
        private ControlPoint _lastControlPoint;
        private EntityQuery _definitionQuery;
        private EntityQuery _soundQuery;
        private EntityQuery _tempConnectionQuery;
        private EntityQuery _raycastHelpersQuery;

        private InputAction _delAction;
        private Camera _mainCamera;

        public Mode ToolMode { get; set; }

        public State ToolState
        {
            get { return _state; }
        }

        public StateModifier ToolModifiers
        {
            get { return _stateModifiers; }
        }

        public Entity SelectedNode
        {
            get { return _selectedNode; }
        }

        public Tooltip tooltip => _tooltip.value;


        public override PrefabBase GetPrefab() {
            return null;
        }

        public override bool TrySetPrefab(PrefabBase prefab) {
            return false;
        }

        public NativeList<ControlPoint> GetControlPoints(out JobHandle dependencies) {
            dependencies = base.Dependency;
            return _controlPoints;
        }

        protected override void OnCreate() {
            base.OnCreate();
            _controlPoints = new NativeList<ControlPoint>(4, Allocator.Persistent);
            _tooltip = new NativeValue<Tooltip>(Allocator.Persistent);
            _delAction = new InputAction("LaneConnectorTool_Del", InputActionType.Button, "<keyboard>/delete");
            _modRaycastSystem = World.GetOrCreateSystemManaged<ModRaycastSystem>();
            _applyAction = InputManager.instance.FindAction("Tool", "Apply");
            _secondaryApplyAction = InputManager.instance.FindAction("Tool", "Secondary Apply");
            _toolOutputBarrier = World.GetExistingSystemManaged<ToolOutputBarrier>();
            _definitionQuery = GetDefinitionQuery();
            _tempConnectionQuery = GetEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<CustomLaneConnection>() }, None = new[] { ComponentType.ReadOnly<Deleted>(), } });
            _audioManager = World.GetExistingSystemManaged<AudioManager>();
            _soundQuery = GetEntityQuery(ComponentType.ReadOnly<ToolUXSoundSettingsData>());
            _raycastHelpersQuery = GetEntityQuery(new EntityQueryDesc
            {
                Any = new[] { ComponentType.ReadOnly<Connection>(), ComponentType.ReadOnly<Connector>(), ComponentType.ReadOnly<EditIntersection>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>() }
            });
            Enabled = false;
        }

        public void OnKeyPressed(EventModifiers modifiers, KeyCode code) {
            if (modifiers == EventModifiers.Control && code == KeyCode.R)
            {
                if (m_ToolSystem.activeTool != this && m_ToolSystem.activeTool == m_DefaultToolSystem)
                {
                    m_ToolSystem.selected = Entity.Null;
                    m_ToolSystem.activeTool = this;
                }
            }
        }

        protected override void OnStartRunning() {
            base.OnStartRunning();
            _controlPoints.Clear();
            _lastControlPoint = default;
            _applyAction.shouldBeEnabled = true;
            _secondaryApplyAction.shouldBeEnabled = true;
            _modRaycastSystem.Enabled = true;
            _tooltip.value = Tooltip.None;
            ToolMode = Mode.Default;
            _state = State.Default;
            _stateModifiers = StateModifier.AnyConnector;
            _delAction.Enable();
            _mainCamera = Camera.main;
        }

        protected override void OnStopRunning() {
            base.OnStopRunning();
            _selectedNode = Entity.Null;
            _nodeElevation = 0f;
            CleanupIntersectionHelpers();
            _applyAction.shouldBeEnabled = false;
            _secondaryApplyAction.shouldBeEnabled = false;
            _modRaycastSystem.Enabled = false;
            _delAction.Disable();
            _mainCamera = null;
        }

        public override void InitializeRaycast() {
            base.InitializeRaycast();

            if (_state <= State.SelectingSourceConnector)
            {
                if (Keyboard.current.ctrlKey.isPressed)
                {
                    _stateModifiers = StateModifier.TrackOnly;
                    if (Keyboard.current.shiftKey.isPressed)
                    {
                        _stateModifiers = StateModifier.SharedRoadTrack;
                    }
                }
                else
                {
                    _stateModifiers = StateModifier.AnyConnector;
                    if (Keyboard.current.shiftKey.isPressed)
                    {
                        _stateModifiers = StateModifier.RoadOnly;
                    }
                }
                if (Keyboard.current.altKey.isPressed)
                {
                    _stateModifiers |= StateModifier.MakeUnsafe;
                }
                else
                {
                    _stateModifiers &= ~StateModifier.MakeUnsafe;
                }
            }

            if (_mainCamera && _state > State.Default)
            {
                CustomRaycastInput input;
                input.line = ToolRaycastSystem.CalculateRaycastLine(_mainCamera);
                input.offset = new float3(0, _nodeElevation, 0);
                input.typeMask = _state == State.SelectingTargetConnector ? TypeMask.Terrain : TypeMask.None;
                input.connectorType = _state switch
                {
                    State.SelectingSourceConnector => ConnectorType.Source | ConnectorType.TwoWay,
                    State.SelectingTargetConnector => ConnectorType.Target | ConnectorType.TwoWay,
                    _ => ConnectorType.All
                };
                StateModifier mod = _stateModifiers & ~StateModifier.MakeUnsafe;
                input.connectionType = mod switch
                {
                    StateModifier.SharedRoadTrack => ConnectionType.All,
                    0 => ConnectionType.All,
                    StateModifier.TrackOnly => ConnectionType.Track,
                    _ => ConnectionType.Road
                };
                _modRaycastSystem.SetInput(input);
            }
            
            m_ToolRaycastSystem.collisionMask = (CollisionMask.OnGround | CollisionMask.Overground);
            m_ToolRaycastSystem.typeMask = (TypeMask.Net);
            m_ToolRaycastSystem.raycastFlags = RaycastFlags.SubElements | RaycastFlags.Cargo | RaycastFlags.Passenger | RaycastFlags.EditorContainers;
            m_ToolRaycastSystem.netLayerMask = Layer.Road | Layer.TrainTrack | Layer.TramTrack | Layer.SubwayTrack | Layer.PublicTransportRoad;
            m_ToolRaycastSystem.iconLayerMask = IconLayerMask.None;
            m_ToolRaycastSystem.utilityTypeMask = UtilityTypes.None;
        }

        private bool GetCustomRaycastResult(out ControlPoint controlPoint) {
            if (GetCustomRayCastResult(out Entity entity, out RaycastHit hit))
            {
                controlPoint = new ControlPoint(entity, hit);
                return true;
            }
            controlPoint = default;
            return false;
        }

        private bool GetCustomRaycastResult(out ControlPoint controlPoint, out bool forceUpdate) {
            forceUpdate = m_OriginalDeletedSystem.GetOriginalDeletedResult(1);
            return GetCustomRaycastResult(out controlPoint);
        }

        private bool GetCustomRayCastResult(out Entity entity, out RaycastHit hit) {
            if (_modRaycastSystem.GetRaycastResult(out CustomRaycastResult result) && !EntityManager.HasComponent<Deleted>(result.owner))
            {
                entity = result.owner;
                hit = result.hit;
                return true;
            }
            entity = Entity.Null;
            hit = new RaycastHit();
            return false;
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps) {
            if (m_FocusChanged)
            {
                return inputDeps;
            }

            if ((m_ToolRaycastSystem.raycastFlags & (RaycastFlags.DebugDisable | RaycastFlags.UIDisable)) == 0)
            {
                JobHandle result;
                if (_state == State.SelectingSourceConnector && Keyboard.current.deleteKey.wasPressedThisFrame)
                {
                    return ResetConnections(inputDeps);
                }
                
                if (_secondaryApplyAction.WasPressedThisFrame())
                {
                    result = Cancel(inputDeps);
                }
                else if ((_state != 0 || !_applyAction.WasPressedThisFrame()) &&
                    (_state == State.Default || !_applyAction.WasReleasedThisFrame()))
                {
                    result = Update(inputDeps);
                }
                else
                {
                    result = Apply(inputDeps);
                }
                return result;
            }

            return Clear(inputDeps);
        }


        private JobHandle Apply(JobHandle inputDeps) {
            switch (_state)
            {
                case State.Default:
                    if (GetAllowApply() && GetRaycastResult(out Entity entity, out RaycastHit _) &&
                        EntityManager.HasComponent<Node>(entity))
                    {
                        applyMode = ApplyMode.None;
                        return SelectIntersectionNode(inputDeps, entity);
                    }
                    return Update(inputDeps);

                case State.SelectingSourceConnector:
                    if (GetAllowApply())
                    {
                        applyMode = ApplyMode.Apply;
                        _controlPoints.Clear();
                        if (GetCustomRaycastResult(out ControlPoint controlPoint) && EntityManager.TryGetComponent(controlPoint.m_OriginalEntity, out Connector connector) && connector.connectorType == ConnectorType.Source)
                        {
                            _lastControlPoint = controlPoint;
                            _controlPoints.Add(in controlPoint);
                            _controlPoints.Add(in controlPoint);
                            PlaySelectedSound();
                            _state = State.SelectingTargetConnector;
                            inputDeps = UpdateDefinitions(inputDeps);
                        }
                        return inputDeps;
                    }
                    _audioManager.PlayUISound(_soundQuery.GetSingleton<ToolUXSoundSettingsData>().m_PlaceBuildingFailSound);
                    inputDeps = Update(inputDeps);
                    break;

                case State.SelectingTargetConnector:
                    if (GetAllowApply() && !_tempConnectionQuery.IsEmptyIgnoreFilter)
                    {
                        if (GetCustomRaycastResult(out ControlPoint controlPoint) && EntityManager.TryGetComponent(controlPoint.m_OriginalEntity, out Connector connector))
                        {
                            if (connector.connectorType == ConnectorType.Target)
                            {
                                _audioManager.PlayUISound(_soundQuery.GetSingleton<ToolUXSoundSettingsData>().m_BulldozeSound);
                                _lastControlPoint = default;
                                _controlPoints.Clear();
                                _state = State.SelectingSourceConnector;
                                applyMode = ApplyMode.Apply;
                                return SelectIntersectionNode(inputDeps, _selectedNode);
                            }
                        }
                    }
                    else
                    {
                        return Update(inputDeps);
                    }
                    break;

                case State.RemovingSourceConnections:
                case State.RemovingTargetConnections:
                    break;
            }
            return inputDeps;
        }

        private JobHandle Update(JobHandle inputHandle) {
            bool forceUpdate;
            if (_state == State.Default)
            {
                if (GetRaycastResult(out ControlPoint controlPoint, out forceUpdate))
                {
                    if (_controlPoints.Length == 0)
                    {
                        _lastControlPoint = controlPoint;
                        _controlPoints.Add(in controlPoint);
                        applyMode = ApplyMode.Clear;
                        return UpdateDefinitions(inputHandle);
                    }

                    if (_lastControlPoint.Equals(controlPoint) && !forceUpdate)
                    {
                        applyMode = ApplyMode.None;
                    }
                    else
                    {
                        _lastControlPoint = controlPoint;
                        _controlPoints[0] = controlPoint;
                        applyMode = ApplyMode.Clear;
                        inputHandle = UpdateDefinitions(inputHandle);
                    }
                    return inputHandle;
                }

                if (_lastControlPoint.Equals(controlPoint))
                {
                    if (forceUpdate)
                    {
                        applyMode = ApplyMode.Clear;
                        return UpdateDefinitions(inputHandle);
                    }

                    applyMode = ApplyMode.None;
                    return inputHandle;
                }
                _lastControlPoint = controlPoint;
                if (_controlPoints.Length > 0)
                {
                    _controlPoints.Clear();
                    applyMode = ApplyMode.Clear;
                    return UpdateDefinitions(inputHandle);
                }
                return inputHandle;
            }

            if (GetCustomRaycastResult(out ControlPoint controlPoint2, out forceUpdate))
            {
                switch (_state)
                {
                    case State.SelectingSourceConnector:
                        if (_controlPoints.Length > 0 && _controlPoints[0].m_OriginalEntity.Equals(controlPoint2.m_OriginalEntity))
                        {
                            applyMode = ApplyMode.None;
                            return inputHandle;
                        }
                        _controlPoints.Clear();
                        _controlPoints.Add(controlPoint2);
                        _lastControlPoint = controlPoint2;
                        applyMode = ApplyMode.Clear;
                        return UpdateDefinitions(inputHandle);

                    case State.SelectingTargetConnector:
                        if (_controlPoints.Length > 1 &&
                            !_controlPoints[0].m_OriginalEntity.Equals(controlPoint2.m_OriginalEntity) &&
                            _lastControlPoint.m_OriginalEntity.Equals(controlPoint2.m_OriginalEntity) && controlPoint2.m_OriginalEntity != Entity.Null && EntityManager.HasComponent<Connector>(controlPoint2.m_OriginalEntity))
                        {
                            applyMode = ApplyMode.None;
                            return inputHandle;
                        }
                        _lastControlPoint = controlPoint2;
                        if (_controlPoints.Length > 0)
                        {
                            ControlPoint p1 = _controlPoints[0];
                            _controlPoints.Clear();
                            _controlPoints.Add(in p1);
                            _controlPoints.Add(in controlPoint2);
                        }
                        applyMode = ApplyMode.Clear;
                        return UpdateDefinitions(inputHandle);
                    case State.RemovingSourceConnections:
                    case State.RemovingTargetConnections:
                        break;
                }
            }
            if (_lastControlPoint.Equals(controlPoint2))
            {
                if (forceUpdate)
                {
                    _controlPoints.Clear();
                    applyMode = ApplyMode.Clear;
                    return UpdateDefinitions(inputHandle);
                }

                applyMode = ApplyMode.None;
                return inputHandle;
            }
            else if (!_lastControlPoint.Equals(default))
            {
                _lastControlPoint = default;
                if (_controlPoints.Length > 0)
                {
                    _controlPoints[_controlPoints.Length - 1] = default;
                }

                applyMode = ApplyMode.Clear;
                inputHandle = UpdateDefinitions(inputHandle);
            }
            return inputHandle;
        }

        private JobHandle Cancel(JobHandle inputHandle) {
            applyMode = ApplyMode.None;
            switch (_state)
            {
                case State.Default:
                    applyMode = ApplyMode.Clear;
                    _state = State.Default;
                    return SelectIntersectionNode(inputHandle, Entity.Null);
                case State.SelectingSourceConnector:
                    applyMode = ApplyMode.Clear;
                    _state = State.Default;
                    CleanupIntersectionHelpers();
                    return SelectIntersectionNode(inputHandle, Entity.Null);
                case State.SelectingTargetConnector:
                    applyMode = ApplyMode.Clear;
                    _lastControlPoint = default;
                    _controlPoints.Clear();
                    _state = State.SelectingSourceConnector;
                    inputHandle = UpdateDefinitions(inputHandle);
                    break;
            }
            return inputHandle;
        }

        private JobHandle Clear(JobHandle inputDeps) {
            base.applyMode = ApplyMode.Clear;
            return inputDeps;
        }

        private JobHandle UpdateDefinitions(JobHandle inputDeps) {
            JobHandle jobHandle = DestroyDefinitions(_definitionQuery, _toolOutputBarrier, inputDeps);
            CreateDefinitionsJob job = new CreateDefinitionsJob()
            {
                connectorData = SystemAPI.GetComponentLookup<Connector>(true),
                nodeData = SystemAPI.GetComponentLookup<Node>(true),
                laneData = SystemAPI.GetComponentLookup<Lane>(true),
                prefabRefData = SystemAPI.GetComponentLookup<PrefabRef>(true),
                connectionsBuffer = SystemAPI.GetBufferLookup<LaneConnection>(true),
                connectionsBufferData = SystemAPI.GetBufferLookup<Connection>(true),
                subLaneBuffer = SystemAPI.GetBufferLookup<SubLane>(true),
                controlPoints = GetControlPoints(out JobHandle pointDependencies),
                state = ToolState,
                stateModifier = ToolModifiers,
                intersectionNode = _selectedNode,
                tooltip = _tooltip,
                commandBuffer = _toolOutputBarrier.CreateCommandBuffer(),
            };
            JobHandle handle = job.Schedule(JobHandle.CombineDependencies(inputDeps, pointDependencies));
            _toolOutputBarrier.AddJobHandleForProducer(handle);
            jobHandle = JobHandle.CombineDependencies(jobHandle, handle);

            return jobHandle;
        }

        private JobHandle SelectIntersectionNode(JobHandle inputDeps, Entity node) {
            CleanupIntersectionHelpers();
            _selectedNode = node;
            if (node != Entity.Null)
            {
                _state = State.SelectingSourceConnector;
                _controlPoints.Clear();

                //TODO move to job
                EntityCommandBuffer ecb = _toolOutputBarrier.CreateCommandBuffer();
                Entity e = ecb.CreateEntity();
                ecb.AddComponent(e, new EditIntersection() { node = node });
                ecb.AddComponent<Updated>(e);
                _nodeElevation = 0;
                if (EntityManager.HasComponent<Elevation>(node))
                {
                    _nodeElevation = EntityManager.GetComponentData<Elevation>(node).m_Elevation.y;
                }
                if (!EntityManager.HasComponent<ModifiedConnections>(node))
                {
                    ecb.AddComponent<ModifiedConnections>(node);
                    ecb.AddBuffer<GeneratedConnection>(node);
                    ecb.AddBuffer<ModifiedLaneConnections>(node);
                    //TODO add logic to validate and remove when no longer valid
                }

                if (EntityManager.HasBuffer<ConnectedEdge>(node))
                {
                    DynamicBuffer<ConnectedEdge> connectedEdges = EntityManager.GetBuffer<ConnectedEdge>(node);
                    bool anyUpdated = false;
                    for (var i = 0; i < connectedEdges.Length; i++)
                    {
                        ConnectedEdge connectedEdge = connectedEdges[i];
                        if (!EntityManager.HasComponent<Upgraded>(connectedEdge.m_Edge))
                        {
                            continue;
                        }
                        Edge edge = EntityManager.GetComponentData<Edge>(connectedEdge.m_Edge);
                        Upgraded upgraded = EntityManager.GetComponentData<Upgraded>(connectedEdge.m_Edge);
                        Entity otherNode = Entity.Null;
                        if (edge.m_Start == node)
                        {
                            upgraded.m_Flags.m_Left &= ~(CompositionFlags.Side.ForbidStraight | CompositionFlags.Side.ForbidLeftTurn | CompositionFlags.Side.ForbidRightTurn);
                            otherNode = edge.m_End;
                        }
                        else if (edge.m_End == node)
                        {
                            upgraded.m_Flags.m_Right &= ~(CompositionFlags.Side.ForbidStraight | CompositionFlags.Side.ForbidLeftTurn | CompositionFlags.Side.ForbidRightTurn);
                            otherNode = edge.m_Start;
                        }
                        
                        if (upgraded.m_Flags == default(CompositionFlags))
                        {
                            EntityManager.RemoveComponent<Upgraded>(connectedEdge.m_Edge);
                        }
                        else
                        {
                            EntityManager.SetComponentData(connectedEdge.m_Edge, upgraded);
                        }
                        EntityManager.AddComponent<Updated>(connectedEdge.m_Edge);
                        EntityManager.AddComponent<Updated>(otherNode);
                        anyUpdated = true;
                    }
                    if (anyUpdated)
                    {
                        EntityManager.AddComponent<Updated>(node);
                    }
                }
                PlaySelectedSound();
                inputDeps = UpdateDefinitions(inputDeps);
            }
            return inputDeps;
        }

        private void PlaySelectedSound(bool force = false) {
            Entity clipEntity = _soundQuery.GetSingleton<ToolUXSoundSettingsData>().m_SelectEntitySound;
            if (force)
            {
                _audioManager.PlayUISound(clipEntity);
            }
            else
            {
                _audioManager.PlayUISoundIfNotPlaying(clipEntity);
            }
        }

        private void CleanupIntersectionHelpers() {
            if (!_raycastHelpersQuery.IsEmptyIgnoreFilter)
            {
                EntityManager.AddComponent<Deleted>(_raycastHelpersQuery);
            }
        }

        private JobHandle ResetConnections(JobHandle handle) {
            DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnectionsEnumerable = EntityManager.GetBuffer<ModifiedLaneConnections>(_selectedNode, false);
            modifiedLaneConnectionsEnumerable.Clear();
            DynamicBuffer<GeneratedConnection> generatedConnections = EntityManager.GetBuffer<GeneratedConnection>(_selectedNode, false);
            generatedConnections.Clear();
            // update node and connected edges + their nodes
            EntityManager.AddComponent<Updated>(_selectedNode);
            DynamicBuffer<ConnectedEdge> edges = EntityManager.GetBuffer<ConnectedEdge>(_selectedNode, true);
            if (edges.Length > 0)
            {
                //update connected nodes of every edge
                for (var j = 0; j < edges.Length; j++)
                {
                    EntityManager.AddComponent<Updated>(edges[j].m_Edge);
                    Edge e = EntityManager.GetComponentData<Edge>(edges[j].m_Edge);
                    Entity otherNode = e.m_Start == _selectedNode ? e.m_End : e.m_Start;
                    EntityManager.AddComponent<Updated>(otherNode);
                }
            }
            return SelectIntersectionNode(handle, _selectedNode);
        }

        // private struct SelectEditIntersectionJob : IJob
        // {
        //     [ReadOnly] public Entity selectedIntersection;
        //     public NativeValue<Entity> selected;
        //     public EntityCommandBuffer commandBuffer;
        //
        //     public void Execute() {
        //         Entity e = commandBuffer.CreateEntity();
        //         commandBuffer.AddComponent(e, new EditIntersection() { node = selectedIntersection });
        //         commandBuffer.AddComponent<Updated>(e);
        //         selected.value = e;
        //     }
        // }
    }
}
