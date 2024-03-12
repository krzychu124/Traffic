using System;
using System.Linq;
using System.Text;
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
            // RemovingSourceConnections,
            // RemovingTargetConnections,
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
        private bool _majorStateModifiersChange;
        private bool _minorStateModifiersChange;
        private ControlPoint _lastControlPoint;
        private EntityQuery _definitionQuery;
        private EntityQuery _soundQuery;
        private EntityQuery _tempConnectionQuery;
        private EntityQuery _tempQuery;
        private EntityQuery _raycastHelpersQuery;
        private EntityQuery _editIntersectionQuery;

        private InputAction _delAction;
        private Camera _mainCamera;
        private ComponentTypeSet _modifiedConnectionsTypeSet;

        internal static Entity FakePrefabRef;

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
            _modifiedConnectionsTypeSet = new ComponentTypeSet(ComponentType.ReadWrite<ModifiedConnections>(), ComponentType.ReadWrite<ModifiedLaneConnections>());
            // Systems
            _toolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();
            _modRaycastSystem = World.GetOrCreateSystemManaged<ModRaycastSystem>();
            _audioManager = World.GetOrCreateSystemManaged<AudioManager>();
            // Queries
            _definitionQuery = GetDefinitionQuery();
            _tempConnectionQuery = GetEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<CustomLaneConnection>() }, None = new[] { ComponentType.ReadOnly<Deleted>(), } });
            _tempQuery = GetEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Temp>() } });
            _soundQuery = GetEntityQuery(ComponentType.ReadOnly<ToolUXSoundSettingsData>());
            _raycastHelpersQuery = GetEntityQuery(new EntityQueryDesc
            {
                Any = new[] { ComponentType.ReadOnly<Connection>(), ComponentType.ReadOnly<Connector>(), ComponentType.ReadOnly<EditIntersection>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>() }
            });
            _editIntersectionQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new [] { ComponentType.ReadOnly<EditIntersection>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>() }
            });
            // Actions
            _delAction = new InputAction("LaneConnectorTool_Del", InputActionType.Button, "<keyboard>/delete");
            _applyAction = InputManager.instance.FindAction("Tool", "Apply");
            _secondaryApplyAction = InputManager.instance.FindAction("Tool", "Secondary Apply");
            FakePrefabRef = EntityManager.CreateEntity(ComponentType.ReadWrite<PrefabRef>());
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
            _mainCamera = Camera.main;
            _controlPoints.Clear();
            _lastControlPoint = default;
            
            _stateModifiers = StateModifier.AnyConnector;
            _majorStateModifiersChange = false;
            _minorStateModifiersChange = false;
            _tooltip.value = Tooltip.None;
            _state = State.Default;
            ToolMode = Mode.Default;
            
            _applyAction.shouldBeEnabled = true;
            _secondaryApplyAction.shouldBeEnabled = true;
            _modRaycastSystem.Enabled = true;
            _delAction.Enable();
        }

        protected override void OnStopRunning() {
            base.OnStopRunning();
            _mainCamera = null;
            _selectedNode = Entity.Null;
            _nodeElevation = 0f;
            CleanupIntersectionHelpers();
            _applyAction.shouldBeEnabled = false;
            _secondaryApplyAction.shouldBeEnabled = false;
            _modRaycastSystem.Enabled = false;
            _delAction.Disable();
        }

        public override void InitializeRaycast() {
            base.InitializeRaycast();

            if (_state == State.SelectingSourceConnector)
            {
                StateModifier prev = _stateModifiers & ~StateModifier.MakeUnsafe;
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

                if (prev != _stateModifiers)
                {
                    _majorStateModifiersChange = true;
                }
                if (Keyboard.current.altKey.isPressed)
                {
                    _stateModifiers |= StateModifier.MakeUnsafe;
                }
                else
                {
                    _stateModifiers &= ~StateModifier.MakeUnsafe;
                }
            } else if (_state == State.SelectingTargetConnector) {
                StateModifier prev = _stateModifiers;
                if (Keyboard.current.altKey.isPressed)
                {
                    _stateModifiers |= StateModifier.MakeUnsafe;
                }
                else
                {
                    _stateModifiers &= ~StateModifier.MakeUnsafe;
                }
                if (prev != _stateModifiers)
                {
                    _minorStateModifiersChange = true;
                }
            }
            else
            {
                _stateModifiers = StateModifier.AnyConnector;
            }

            if (_mainCamera && _state > State.Default)
            {
                CustomRaycastInput input;
                input.line = ToolRaycastSystem.CalculateRaycastLine(_mainCamera);
                input.offset = new float3(0, 0, 0);
                input.typeMask = _state == State.SelectingTargetConnector ? TypeMask.Terrain : TypeMask.None;
                input.connectorType = _state switch
                {
                    State.SelectingSourceConnector => ConnectorType.Source | ConnectorType.TwoWay,
                    State.SelectingTargetConnector => ConnectorType.Target | ConnectorType.TwoWay,
                    _ => ConnectorType.All
                };
                StateModifier mod = _stateModifiers & ~StateModifier.MakeUnsafe;
                
                //TODO FIX matching connector type
                if (_state == State.SelectingTargetConnector && _controlPoints.Length > 0 &&
                    EntityManager.TryGetComponent(_controlPoints[0].m_OriginalEntity, out Connector sourceConnector))
                {
                    input.connectionType = sourceConnector.connectionType & (ConnectionType.Road | ConnectionType.Track | ConnectionType.Strict);
                }
                else
                {
                    input.connectionType = mod switch
                    {
                        0 => ConnectionType.All,
                        StateModifier.SharedRoadTrack => ConnectionType.SharedCarTrack,
                        StateModifier.TrackOnly => ConnectionType.Track | ConnectionType.Strict,
                        StateModifier.RoadOnly => ConnectionType.Road | ConnectionType.Strict,
                        _ => ConnectionType.Road
                    };
                }
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

        private bool IsApplyAllowed(bool useVanilla = true) {
            if (useVanilla)
            {
                return GetAllowApply();
            }
            // workaround for vanilla OriginalDeletedSystem result (fix bug)
            return m_ToolSystem.ignoreErrors || m_ErrorQuery.IsEmptyIgnoreFilter;
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
                else if (_majorStateModifiersChange || _minorStateModifiersChange)
                {
                    result = Update(inputDeps);
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
                    if (IsApplyAllowed(useVanilla: false) && GetRaycastResult(out Entity entity, out RaycastHit _) &&
                        EntityManager.HasComponent<Node>(entity))
                    {
                        applyMode = ApplyMode.None;
                        _state = State.SelectingSourceConnector;
                        _controlPoints.Clear();
                        return SelectIntersectionNode(inputDeps, entity);
                    }
                    return Update(inputDeps);

                case State.SelectingSourceConnector:
                    if (IsApplyAllowed(useVanilla: false))
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
                    // StringBuilder sb = new StringBuilder();
                    // NativeArray<Entity> entityArray = _tempQuery.ToEntityArray(Allocator.Temp);
                    // for (var i = 0; i < entityArray.Length; i++)
                    // {
                    //     var t = EntityManager.GetComponentTypes(entityArray[i]);
                    //     sb.Append($"{entityArray[i]} [").Append(string.Join(", ", t.Select(tt => tt.GetManagedType().Name))).AppendLine("]");
                    // }
                    // entityArray.Dispose();
                    // Logger.Debug($"Allow?: {GetAllowApply()} [{m_ToolSystem.ignoreErrors}|{m_ErrorQuery.IsEmptyIgnoreFilter}||{m_OriginalDeletedSystem.GetOriginalDeletedResult(0)}], hasTemp?: {!_tempConnectionQuery.IsEmptyIgnoreFilter} \n{sb}");
                    if (IsApplyAllowed(useVanilla: false) && !_tempConnectionQuery.IsEmptyIgnoreFilter)
                    {
                        if (GetCustomRaycastResult(out ControlPoint controlPoint) && EntityManager.TryGetComponent(controlPoint.m_OriginalEntity, out Connector connector))
                        {
                            Logger.Debug($"Hit: {controlPoint.m_OriginalEntity} | {connector.connectionType}");
                            if (connector.connectorType == ConnectorType.Target)
                            {
                                _audioManager.PlayUISound(_soundQuery.GetSingleton<ToolUXSoundSettingsData>().m_BulldozeSound);
                                if (_controlPoints.Length > 0)
                                {

                                    ControlPoint point = _controlPoints[0];
                                    _lastControlPoint = point;
                                    _controlPoints.Clear();
                                    _controlPoints.Add(in point);
                                    _state = State.SelectingTargetConnector;
                                }
                                else
                                {
                                    _lastControlPoint = default;
                                    _controlPoints.Clear();
                                    _state = State.SelectingSourceConnector;
                                }
                                applyMode = ApplyMode.Apply;
                                return UpdateDefinitions(inputDeps, true);
                            }
                        }
                    }
                    else
                    {
                        applyMode = ApplyMode.Clear;
                        return Update(inputDeps);
                    }
                    break;

                // case State.RemovingSourceConnections:
                // case State.RemovingTargetConnections:
                    // break;
            }
            return inputDeps;
        }

        private JobHandle Update(JobHandle inputHandle) {
            bool forceUpdate;
            bool majorChange = _majorStateModifiersChange;
            bool minorChange = _minorStateModifiersChange;
            _majorStateModifiersChange = false;
            _minorStateModifiersChange = false;
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
                    Logger.DebugTool($"[Update] Default, force: {forceUpdate} | {_lastControlPoint.m_OriginalEntity} == {controlPoint.m_OriginalEntity}");
                    if (_lastControlPoint.m_OriginalEntity.Equals(controlPoint.m_OriginalEntity)/* TODO fix bug originalDeletedSystem && !forceUpdate*/)
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
                Logger.DebugTool($"[Update] Default, No Hit, force: {forceUpdate} | {_lastControlPoint.m_OriginalEntity} == {controlPoint.m_OriginalEntity}");
                if (_lastControlPoint.m_OriginalEntity.Equals(controlPoint.m_OriginalEntity))
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

            if (majorChange)
            {
                _controlPoints.Clear();
                _lastControlPoint = default;
                _state = State.SelectingSourceConnector;
                applyMode = ApplyMode.Clear;
                return UpdateDefinitions(inputHandle, updateEditIntersection: true);
            }

            if (GetCustomRaycastResult(out ControlPoint controlPoint2, out forceUpdate))
            {
                Logger.DebugTool($"[Update] Hit: {controlPoint2.m_OriginalEntity}, f: {forceUpdate}");
                switch (_state)
                {
                    case State.SelectingSourceConnector:
                        Logger.DebugTool($"[Update] SelectSource {controlPoint2.m_OriginalEntity}");
                        if (_controlPoints.Length > 0 && _controlPoints[0].m_OriginalEntity.Equals(controlPoint2.m_OriginalEntity))
                        {
                            Logger.DebugTool($"[Update] SelectSource-nothing");
                            applyMode = ApplyMode.None;
                            return inputHandle;
                        }
                        _controlPoints.Clear();
                        _controlPoints.Add(controlPoint2);
                        _lastControlPoint = controlPoint2;
                        applyMode = ApplyMode.Clear;
                        Logger.DebugTool($"[Update] SelectSource-clear");
                        return UpdateDefinitions(inputHandle);

                    case State.SelectingTargetConnector:
                        Logger.DebugTool($"[Update] SelectTarget {controlPoint2.m_OriginalEntity}");
                        if (!minorChange && _controlPoints.Length > 1 && _lastControlPoint.m_OriginalEntity.Equals(controlPoint2.m_OriginalEntity))
                        {
                            if (!_lastControlPoint.m_Position.Equals(controlPoint2.m_Position))
                            {
                                //soft update (position only)
                                ControlPoint p1 = _controlPoints[0];
                                _controlPoints.Clear();
                                _controlPoints.Add(in p1);
                                _controlPoints.Add(in controlPoint2);
                            }
                            applyMode = ApplyMode.None;
                            Logger.DebugTool($"[Update] SelectTarget-nothing");
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
                        Logger.DebugTool($"[Update] SelectTarget-clear");
                        return UpdateDefinitions(inputHandle);
                }
            }
            // Logger.DebugTool($"[Update] {_state} No Hit: {controlPoint2.m_OriginalEntity}, f: {forceUpdate} | count: {_controlPoints.Length}");
            //TODO needs more tests (not quite sure if reliable) 
            if (_tempQuery.IsEmptyIgnoreFilter && !_definitionQuery.IsEmptyIgnoreFilter && !_editIntersectionQuery.IsEmptyIgnoreFilter && _selectedNode != Entity.Null)
            {
                Logger.DebugTool($"[Update] Reset: {_lastControlPoint.m_OriginalEntity} | {_selectedNode}");
                _lastControlPoint = default;
                applyMode = ApplyMode.Clear;
                return UpdateDefinitions(inputHandle);
            }
            if (_lastControlPoint.m_OriginalEntity.Equals(controlPoint2.m_OriginalEntity))
            {
                Logger.DebugTool($"[Update] TheSame: {_lastControlPoint.m_OriginalEntity}, force: {forceUpdate}");
                 // improve Vanilla OriginalDeleted result for custom objects lifetime (fix bug)
                //  if (forceUpdate || minorChange)
                // {
                //     _controlPoints.Clear();
                //     applyMode = ApplyMode.Clear;
                //     return UpdateDefinitions(inputHandle);
                // }

                applyMode = ApplyMode.None;
                return inputHandle;
            }
            else if (!_lastControlPoint.Equals(default))
            {
                Logger.DebugTool($"[Update] Different, updating: ({_lastControlPoint.m_Position}) {_lastControlPoint.m_OriginalEntity}");
                _lastControlPoint = default;
                if (_controlPoints.Length > 0)
                {
                    _controlPoints[_controlPoints.Length - 1] = default;
                }

                applyMode = ApplyMode.Clear;
                inputHandle = UpdateDefinitions(inputHandle);
            }
            Logger.DebugTool($"[Update] SomethingElse {_lastControlPoint.m_HitPosition} | {_lastControlPoint.m_OriginalEntity}");
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
            // Logger.DebugTool("Clearing...");
            return inputDeps;
        }

        /// <summary>
        /// TODO FIX ME - double check when it's updated
        /// </summary>
        private JobHandle UpdateDefinitions(JobHandle inputDeps, bool updateEditIntersection = false) {
            JobHandle jobHandle = DestroyDefinitions(_definitionQuery, _toolOutputBarrier, inputDeps);
            Entity editingIntersection = Entity.Null;
            if (!_editIntersectionQuery.IsEmptyIgnoreFilter)
            {
                editingIntersection = _editIntersectionQuery.GetSingletonEntity();
                if (editingIntersection != Entity.Null && updateEditIntersection)
                {
                    EntityQuery connectors = new EntityQueryBuilder(Allocator.Temp).WithAllRW<Connector, LaneConnection>().Build(EntityManager);
                    EntityQuery connections = new EntityQueryBuilder(Allocator.Temp).WithAll<Connection>().Build(EntityManager);
                    Logger.DebugTool($"Cleanup connections: connectors with LaneConnections: {connectors.CalculateEntityCount()}, connections: {connections.CalculateEntityCount()}");
                    NativeArray<Entity> entities = connectors.ToEntityArray(Allocator.Temp);
                    foreach (Entity entity in entities)
                    {
                        EntityManager.GetBuffer<LaneConnection>(entity).Clear();
                    }
                    EntityManager.DestroyEntity(connections);
                    EntityManager.AddComponent<Updated>(editingIntersection);
                    connectors.Dispose();
                    connections.Dispose();
                }
            }
            
            Logger.DebugTool("Scheduling CreateDefinitionsJob");
            CreateDefinitionsJob job = new CreateDefinitionsJob()
            {
                connectorData = SystemAPI.GetComponentLookup<Connector>(true),
                nodeData = SystemAPI.GetComponentLookup<Node>(true),
                laneData = SystemAPI.GetComponentLookup<Lane>(true),
                prefabRefData = SystemAPI.GetComponentLookup<PrefabRef>(true),
                connectionsBuffer = SystemAPI.GetBufferLookup<LaneConnections.LaneConnection>(true),
                connectionsBufferData = SystemAPI.GetBufferLookup<Connection>(true),
                modifiedConnectionBuffer = SystemAPI.GetBufferLookup<ModifiedLaneConnections>(true),
                generatedConnectionBuffer = SystemAPI.GetBufferLookup<GeneratedConnection>(true),
                connectorElementsBuffer = SystemAPI.GetBufferLookup<ConnectorElement>(true),
                controlPoints = GetControlPoints(out JobHandle pointDependencies),
                state = ToolState,
                stateModifier = ToolModifiers,
                intersectionNode = _selectedNode,
                editingIntersection = editingIntersection,
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
            // if (node == Entity.Null && 
            //     EntityManager.HasComponent<ModifiedConnections>(_selectedNode) &&
            //     EntityManager.TryGetBuffer<ModifiedLaneConnections>(_selectedNode, true, out DynamicBuffer<ModifiedLaneConnections> buffer) &&
            //     buffer.Length == 0)
            // {
            //     EntityManager.RemoveComponent(_selectedNode, in _modifiedConnectionsTypeSet);
            // }
            _selectedNode = node;
            if (node != Entity.Null)
            {
                // _state = State.SelectingSourceConnector;
                // _controlPoints.Clear();

                //TODO move to job
                EntityCommandBuffer ecb = _toolOutputBarrier.CreateCommandBuffer();
                Entity e = ecb.CreateEntity();
                ecb.AddComponent(e, new EditIntersection() { node = node });
                ecb.AddComponent<Updated>(e);
                Logger.DebugTool($"Inline Create EditIntersection entity {e}");
                _nodeElevation = 0;
                if (EntityManager.HasComponent<Elevation>(node))
                {
                    _nodeElevation = EntityManager.GetComponentData<Elevation>(node).m_Elevation.y;
                }
                if (!EntityManager.HasComponent<ModifiedConnections>(node))
                {
                    ecb.AddComponent(node, in _modifiedConnectionsTypeSet);
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
            Logger.DebugTool("CleanupIntersectionHelpers!");
            if (!_raycastHelpersQuery.IsEmptyIgnoreFilter)
            {
                Logger.DebugTool($"CleanupIntersectionHelpers! {_raycastHelpersQuery.CalculateEntityCount()}");
                EntityManager.AddComponent<Deleted>(_raycastHelpersQuery);
            }
        }

        private JobHandle ResetConnections(JobHandle handle) {
            DynamicBuffer<ModifiedLaneConnections> modifiedLaneConnectionsEnumerable = EntityManager.GetBuffer<ModifiedLaneConnections>(_selectedNode, false);
            for (var i = 0; i < modifiedLaneConnectionsEnumerable.Length; i++)
            {
                var modified = modifiedLaneConnectionsEnumerable[i].modifiedConnections;
                if (modified != Entity.Null)
                {
                    EntityManager.AddComponent<Deleted>(modified);
                }
            }
            modifiedLaneConnectionsEnumerable.Clear();
            // DynamicBuffer<GeneratedConnection> generatedConnections = EntityManager.GetBuffer<GeneratedConnection>(_selectedNode, false);
            // generatedConnections.Clear();
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
            applyMode = ApplyMode.Clear;
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
