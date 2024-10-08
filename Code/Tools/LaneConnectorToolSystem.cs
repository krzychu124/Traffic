﻿#if DEBUG
// #define DEBUG_TOOL
#endif
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
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.LaneConnections;
using Traffic.Components.PrioritySigns;
using Traffic.Systems;
using Traffic.UISystems;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using LaneConnection = Traffic.Components.LaneConnections.LaneConnection;

namespace Traffic.Tools
{
#if WITH_BURST
    [BurstCompile]
#endif
    public partial class LaneConnectorToolSystem : ToolBaseSystem
    {
        public enum Mode
        {
            /// <summary>
            /// Main tool mode
            /// </summary>
            Default,
            /// <summary>
            /// Quick modification mode - prepare definitions for the current modification action
            /// Definitions will generate Temp entities which will be applied in the next frame
            /// </summary>
            ApplyQuickModifications,
        }

        public enum State
        {
            /// <summary>
            /// Waiting of intersection selection
            /// </summary>
            Default,
            /// <summary>
            /// Selected intersection, waiting for source lane connector selection
            /// </summary>
            SelectingSourceConnector,
            /// <summary>
            /// Selected intersection, selected source lane connector, waiting for target lane connector selection
            /// </summary>
            SelectingTargetConnector,
            /// <summary>
            /// Selected intersection, next frame after creating definitions for quick modification action
            /// </summary>
            ApplyingQuickModifications,
        }

        [Flags]
        public enum StateModifier
        {
            None,
            Road = 1,
            Track = 1 << 1,
            AnyConnector = Road | Track,
            FullMatch = 1 << 2,
            MakeUnsafe = 1 << 3,
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
            UTurnTrackNotAllowed,
            // RemoveLaneConnection, // raycast connection line required
        }

        public override string toolID => UIBindingConstants.LANE_CONNECTOR_TOOL;

        private Game.Tools.ValidationSystem _validationSystem;
        private ModRaycastSystem _modRaycastSystem;
        private ModUISystem _modUISystem;

        private ProxyAction _applyAction;
        private ProxyAction _secondaryApplyAction;
        private ProxyAction _removeAllConnectionsAction;
        private ProxyAction _removeUTurnsAction;
        private ProxyAction _removeUnsafeAction;
        private ProxyAction _resetIntersectionToDefaultsAction;
        private ToolOutputBarrier _toolOutputBarrier;
        private AudioManager _audioManager;
        private NativeList<ControlPoint> _controlPoints;
        private NativeValue<Tooltip> _tooltip;

        private Entity _selectedNode;
        private NativeValue<float2> _nodeElevation;
        private State _state;
        private StateModifier _stateModifiers = StateModifier.AnyConnector;
        private bool _majorStateModifiersChange;
        private bool _minorStateModifiersChange;
        private ControlPoint _lastControlPoint;
        private EntityQuery _toolSelectionQuery;
        private EntityQuery _definitionQuery;
        private EntityQuery _soundQuery;
        private EntityQuery _tempConnectionQuery;
        private EntityQuery _tempQuery;
        private EntityQuery _raycastHelpersQuery;
        private EntityQuery _editIntersectionQuery;
        private EntityQuery _toolFeedbackQuery;

        private Camera _mainCamera;
        private ComponentTypeSet _modifiedConnectionsTypeSet;


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
        public bool UIDisabled => (m_ToolRaycastSystem.raycastFlags & (RaycastFlags.DebugDisable | RaycastFlags.UIDisable)) != 0;
        public bool Underground { get; set; }

        public override bool allowUnderground => true;
        
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
            _nodeElevation = new NativeValue<float2>(Allocator.Persistent);
            // Systems
            _modUISystem = World.GetOrCreateSystemManaged<ModUISystem>();
            _toolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();
            _modRaycastSystem = World.GetOrCreateSystemManaged<ModRaycastSystem>();
            _validationSystem = World.GetOrCreateSystemManaged<Game.Tools.ValidationSystem>();
            _audioManager = World.GetOrCreateSystemManaged<AudioManager>();
            // Queries
            _definitionQuery = GetDefinitionQuery();
            _tempConnectionQuery = GetEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<DataTemp>(), ComponentType.ReadOnly<CustomLaneConnection>() }, None = new[] { ComponentType.ReadOnly<Deleted>(), } });
            _tempQuery = GetEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Temp>() } });
            _soundQuery = GetEntityQuery(ComponentType.ReadOnly<ToolUXSoundSettingsData>());
            _raycastHelpersQuery = GetEntityQuery(new EntityQueryDesc
            {
                Any = new[] { ComponentType.ReadOnly<ConnectorElement>(), ComponentType.ReadOnly<Connection>(), ComponentType.ReadOnly<Connector>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() }
            });
            _editIntersectionQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new [] { ComponentType.ReadOnly<EditIntersection>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() }
            });
            _toolFeedbackQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new []{ ComponentType.ReadOnly<ToolFeedbackInfo>(), ComponentType.ReadOnly<ToolActionBlocked>() },
                None = new []{ ComponentType.ReadOnly<Deleted>() },
            });
            _toolSelectionQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new []{ ComponentType.ReadOnly<ToolManaged>() }
            });
            // Actions
            _applyAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.ApplyTool);
            _secondaryApplyAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.CancelTool);
            _removeAllConnectionsAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.RemoveAllConnections);
            _removeUTurnsAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.RemoveUTurns);
            _removeUnsafeAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.RemoveUnsafe);
            _resetIntersectionToDefaultsAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.ResetIntersectionToDefaults);
            Enabled = false;
        }

        protected override void OnDestroy()
        {
            _controlPoints.Dispose();
            _nodeElevation.Dispose();
            _tooltip.Dispose();
            base.OnDestroy();
        }
        
        public override void SetUnderground(bool isUnderground)
        {
            Underground = isUnderground;
        }

        public override void ElevationUp()
        {
            Underground = false;
        }

        public override void ElevationDown()
        {
            Underground = true;
        }

        public override void ElevationScroll()
        {
            Underground = !Underground;
        }

        public void ToggleTool(bool enable)
        {
            if (enable && m_ToolSystem.activeTool != this)
            {
                m_ToolSystem.selected = Entity.Null;
                m_ToolSystem.activeTool = this;
            } 
            else if (!enable && m_ToolSystem.activeTool == this)
            {
                m_ToolSystem.selected = Entity.Null;
                m_ToolSystem.activeTool = m_DefaultToolSystem;
            }
        }

        protected override void OnStartRunning() {
            base.OnStartRunning();
            Logger.Debug($"Starting {nameof(LaneConnectorToolSystem)}");
            _mainCamera = Camera.main;
            _controlPoints.Clear();
            _nodeElevation.Clear();
            _lastControlPoint = default;
            
            _stateModifiers = StateModifier.AnyConnector;
            _majorStateModifiersChange = false;
            _minorStateModifiersChange = false;
            _tooltip.value = Tooltip.None;
            _state = State.Default;
            ToolMode = Mode.Default;
            requireUnderground = false;
            
            _modRaycastSystem.Enabled = true;
            _validationSystem.Enabled = false;
            
            //main actions
            // _applyAction.shouldBeEnabled = true;
            // _secondaryApplyAction.shouldBeEnabled = true;
        }

        protected override void OnStopRunning() {
            base.OnStopRunning();
            Logger.Debug($"Stopping {nameof(LaneConnectorToolSystem)}");
            _mainCamera = null;
            _selectedNode = Entity.Null;
            _nodeElevation.value = 0f;
            CleanupIntersectionHelpers();
            CleanupEditIntersection();
            ToggleToolManaged(Entity.Null);
            _modUISystem.SelectedIntersection = default;

            UpdateToolActions(false);
            _modRaycastSystem.Enabled = false;
            _validationSystem.Enabled = true;
        }

        public override void InitializeRaycast() {
            base.InitializeRaycast();

            if (_state == State.SelectingSourceConnector)
            {
                StateModifier prev = _stateModifiers & ~StateModifier.MakeUnsafe;
                if (Keyboard.current.ctrlKey.isPressed)
                {
                    _stateModifiers = StateModifier.Track | StateModifier.FullMatch;
                    if (Keyboard.current.shiftKey.isPressed)
                    {
                        _stateModifiers = StateModifier.AnyConnector | StateModifier.FullMatch;
                    }
                }
                else
                {
                    _stateModifiers = StateModifier.AnyConnector;
                    if (Keyboard.current.shiftKey.isPressed)
                    {
                        _stateModifiers = StateModifier.Road | StateModifier.FullMatch;
                    }
                }

                if (prev != _stateModifiers)
                {
                    _majorStateModifiersChange = true;
                }
                if (Keyboard.current.altKey.isPressed && (_stateModifiers & StateModifier.Track) == StateModifier.None)
                {
                    _stateModifiers |= StateModifier.MakeUnsafe;
                }
                else
                {
                    _stateModifiers &= ~StateModifier.MakeUnsafe;
                }
            } else if (_state == State.SelectingTargetConnector) {
                StateModifier prev = _stateModifiers;
                if (Keyboard.current.altKey.isPressed && 
                    _controlPoints.Length > 0 &&
                    EntityManager.TryGetComponent(_controlPoints[0].m_OriginalEntity, out Connector sourceConnector) &&
                    sourceConnector.vehicleGroup == VehicleGroup.Car)
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
                ToolOverlayParameterData overlayParameters = SystemAPI.GetSingleton<ToolOverlayParameterData>();
                CustomRaycastInput input;
                float posHitRadius = math.lerp(0.8f, 1.7f, math.clamp((overlayParameters.laneConnectorSize - 0.5f) / 1.5f, 0, 1f));
                input.line = ToolRaycastSystem.CalculateRaycastLine(_mainCamera);
                input.fovTan = math.tan(math.radians(_mainCamera.fieldOfView) * 0.5f);
                input.offset = new float3(posHitRadius, 0, posHitRadius);
                input.heightOverride = 0;
                input.typeMask = _state == State.SelectingTargetConnector ? TypeMask.Terrain : TypeMask.None;
                input.connectorType = _state switch
                {
                    State.SelectingSourceConnector => ConnectorType.Source,
                    State.SelectingTargetConnector => ConnectorType.Target,
                    _ => ConnectorType.Source | ConnectorType.Target
                };
                StateModifier mod = _stateModifiers & ~StateModifier.MakeUnsafe;
                
                input.vehicleGroup = VehicleGroup.None;
                if (_state == State.SelectingTargetConnector && _controlPoints.Length > 0 &&
                    EntityManager.TryGetComponent(_controlPoints[0].m_OriginalEntity, out Connector sourceConnector))
                {
                    input.heightOverride = sourceConnector.position.y;
                    
                    if ((_stateModifiers & StateModifier.MakeUnsafe) != 0)
                    {
                        input.connectionType = sourceConnector.connectionType & (ConnectionType.Road | ConnectionType.Strict);
                        input.vehicleGroup = sourceConnector.vehicleGroup & VehicleGroup.Car;
                    }
                    else
                    {
                        input.connectionType = sourceConnector.connectionType & (ConnectionType.Road | ConnectionType.Track | ConnectionType.Strict);
                        input.vehicleGroup = sourceConnector.vehicleGroup;
                    }
                }
                else
                {
                    input.vehicleGroup = mod switch
                    {
                        StateModifier.AnyConnector => VehicleGroup.Car | VehicleGroup.Subway | VehicleGroup.Train | VehicleGroup.Tram,
                        StateModifier.Road => VehicleGroup.Car,
                        StateModifier.Track => VehicleGroup.Subway | VehicleGroup.Train | VehicleGroup.Tram,
                        StateModifier.AnyConnector | StateModifier.FullMatch => VehicleGroup.Car | VehicleGroup.Tram,
                        StateModifier.Road | StateModifier.FullMatch => VehicleGroup.Car,
                        StateModifier.Track | StateModifier.FullMatch => VehicleGroup.Subway | VehicleGroup.Train | VehicleGroup.Tram,
                        _ => VehicleGroup.None,
                    };
                    input.connectionType = mod switch
                    {
                        0 => ConnectionType.All,
                        StateModifier.AnyConnector | StateModifier.FullMatch => ConnectionType.SharedCarTrack | ConnectionType.Strict,
                        StateModifier.Track | StateModifier.FullMatch => ConnectionType.Track,
                        StateModifier.Road | StateModifier.FullMatch => ConnectionType.Road | ConnectionType.Strict,
                        _ => ConnectionType.All
                    };
                    if ((_stateModifiers & StateModifier.MakeUnsafe) != StateModifier.None)
                    {
                        input.vehicleGroup &= ~(VehicleGroup.Subway | VehicleGroup.Train | VehicleGroup.Tram);
                        input.connectionType &= ~ConnectionType.Track;
                        input.connectionType |= ConnectionType.Strict;
                    }
                }
                _modRaycastSystem.SetInput(input);
            }
            
            if (Underground)
            {
                m_ToolRaycastSystem.collisionMask = CollisionMask.Underground;
            }
            else
            {
                m_ToolRaycastSystem.collisionMask = (CollisionMask.OnGround | CollisionMask.Overground);
            }
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
            return _toolFeedbackQuery.IsEmptyIgnoreFilter && (m_ToolSystem.ignoreErrors || m_ErrorQuery.IsEmptyIgnoreFilter);
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps) {
            if (m_FocusChanged)
            {
                return inputDeps;
            }

            bool prevRequireUnderground = requireUnderground;
            requireUnderground = Underground;
            if (prevRequireUnderground != Underground)
            {
                applyMode = ApplyMode.Clear;
                _state = State.Default;
                _lastControlPoint = default;
                _controlPoints.Clear();
                return UpdateDefinitions(inputDeps: SelectIntersectionNode(inputDeps, Entity.Null));
            }
            
            if (ToolMode == Mode.ApplyQuickModifications)
            {
                return ApplyQuickModification(inputDeps);
            }

            UpdateToolActions(true);
            if ((m_ToolRaycastSystem.raycastFlags & (RaycastFlags.DebugDisable | RaycastFlags.UIDisable)) == 0)
            {
                if (CheckToolboxActions(inputDeps, out JobHandle resultHandle))
                {
                    return resultHandle;
                }
                if (_secondaryApplyAction.WasPressedThisFrame())
                {
                    return Cancel(inputDeps);
                }
                if (_majorStateModifiersChange || _minorStateModifiersChange)
                {
                    return Update(inputDeps);
                }
                if (_applyAction.WasPressedThisFrame())
                {
                    return Apply(inputDeps);
                }
                
                return Update(inputDeps);
            }

            return Clear(inputDeps);
        }


        private JobHandle Apply(JobHandle inputDeps)
        {
            Logger.DebugTool($"[Apply {UnityEngine.Time.frameCount}] State: {_state}");
            switch (_state)
            {
                case State.Default:
                    if (IsApplyAllowed(useVanilla: false) && GetRaycastResult(out Entity entity, out RaycastHit _) &&
                        EntityManager.HasComponent<Node>(entity))
                    {
                        Logger.DebugTool($"[Apply {UnityEngine.Time.frameCount}]|Default|Entity: {entity}");
                        applyMode = ApplyMode.None;
                        _state = State.SelectingSourceConnector;
                        _controlPoints.Clear();
                        return SelectIntersectionNode(inputDeps, entity);
                    }
                    Logger.DebugTool($"[Apply {UnityEngine.Time.frameCount}]|Default| Miss, updating!");
                    return Update(inputDeps);

                case State.SelectingSourceConnector:
                    if (IsApplyAllowed(useVanilla: false) && 
                        GetCustomRaycastResult(out ControlPoint controlPointSource) &&
                        EntityManager.TryGetComponent(controlPointSource.m_OriginalEntity, out Connector sourceConnector) &&
                        sourceConnector.connectorType == ConnectorType.Source)
                    {
                        _lastControlPoint = controlPointSource;
                        _controlPoints.Add(in controlPointSource);
                        PlaySelectedSound();
                        _state = State.SelectingTargetConnector;
                        applyMode = ApplyMode.Clear;
                        inputDeps = UpdateDefinitions(inputDeps);
                        Logger.DebugTool($"[Apply {UnityEngine.Time.frameCount}]|Default|SelectSource| Hit!");
                        return inputDeps;
                    }
                    Logger.DebugTool($"[Apply {UnityEngine.Time.frameCount}]|Default|SelectSource| Miss!");
                    applyMode = ApplyMode.Clear;
                    _controlPoints.Clear();
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
                    // Logger.DebugTool($"Allow?: {GetAllowApply()} [{m_ToolSystem.ignoreErrors}|{m_ErrorQuery.IsEmptyIgnoreFilter}||{m_OriginalDeletedSystem.GetOriginalDeletedResult(0)}], hasTemp?: {!_tempConnectionQuery.IsEmptyIgnoreFilter} \n{sb}");
                    if (IsApplyAllowed(useVanilla: false) && !_tempConnectionQuery.IsEmptyIgnoreFilter)
                    {
                        if (GetCustomRaycastResult(out ControlPoint controlPoint) && EntityManager.TryGetComponent(controlPoint.m_OriginalEntity, out Connector connector))
                        {
                            Logger.DebugTool($"[Apply {UnityEngine.Time.frameCount}]|Default|SelectTarget| Hit: {controlPoint.m_OriginalEntity} | {connector.connectionType}");
                            if (connector.connectorType == ConnectorType.Target)
                            {
                                if (_controlPoints.Length > 0)
                                {
                                    _audioManager.PlayUISound(_soundQuery.GetSingleton<ToolUXSoundSettingsData>().m_NetStartSound);
                                    ControlPoint point = _controlPoints[0];
                                    _lastControlPoint = point;
                                    _controlPoints.Clear();
                                    _controlPoints.Add(in point);
                                    _state = State.SelectingTargetConnector;
                                }
                                else
                                {
                                    _audioManager.PlayUISound(_soundQuery.GetSingleton<ToolUXSoundSettingsData>().m_NetCancelSound);
                                    _lastControlPoint = default;
                                    _controlPoints.Clear();
                                    _state = State.SelectingSourceConnector;
                                }
                                applyMode = ApplyMode.Apply;
                                Logger.DebugTool($"[Apply {UnityEngine.Time.frameCount}]|Default|SelectTarget| Upcoming State: {_state}");
                                return UpdateDefinitions(inputDeps, true);
                            }
                        }
                        Logger.DebugTool($"[Apply {UnityEngine.Time.frameCount}]|Default|SelectTarget| Miss!");
                    }
                    else
                    {
                        Logger.DebugTool($"[Apply {UnityEngine.Time.frameCount}]|Default|SelectTarget| NotAllowed or isTempEmpty: {_tempConnectionQuery.IsEmptyIgnoreFilter}");
                        applyMode = ApplyMode.Clear;
                        _lastControlPoint = default;
                        _audioManager.PlayUISound(_soundQuery.GetSingleton<ToolUXSoundSettingsData>().m_PlaceBuildingFailSound);
                        return Update(inputDeps);
                    }
                    break;

                // case State.RemovingSourceConnections:
                // case State.RemovingTargetConnections:
                    // break;
            }
            Logger.DebugTool($"[Apply {UnityEngine.Time.frameCount}]| Different state: {_state}");
            return inputDeps;
        }

        private JobHandle Update(JobHandle inputHandle) {
            bool forceUpdate;
            bool majorChange = _majorStateModifiersChange;
            bool minorChange = _minorStateModifiersChange;
            // Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] State: {_state} minorChange: {minorChange} majorChange: {majorChange}");
            _majorStateModifiersChange = false;
            _minorStateModifiersChange = false;
            if (_state == State.Default)
            {
                if (GetRaycastResult(out ControlPoint controlPoint, out forceUpdate))
                {
                    if (_controlPoints.Length == 0)
                    {
                        Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] Default, no control points: {forceUpdate} | last: {_lastControlPoint.m_OriginalEntity}, raycasted: {controlPoint.m_OriginalEntity}");
                        _lastControlPoint = controlPoint;
                        _controlPoints.Add(in controlPoint);
                        applyMode = ApplyMode.Clear;
                        return UpdateDefinitions(inputHandle);
                    }
                    Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] Default, force: {forceUpdate} | {_lastControlPoint.m_OriginalEntity} == {controlPoint.m_OriginalEntity}");
                    if (_lastControlPoint.m_OriginalEntity.Equals(controlPoint.m_OriginalEntity))
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
                    // Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] Default, The Same, force: {forceUpdate} | {_lastControlPoint.m_OriginalEntity} == {controlPoint.m_OriginalEntity}");
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
                Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] Major change!");
                return UpdateDefinitions(inputHandle, updateEditIntersection: true);
            }

            if (GetCustomRaycastResult(out ControlPoint controlPoint2, out forceUpdate))
            {
                // Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] Hit: {controlPoint2.m_OriginalEntity}, f: {forceUpdate}");
                switch (_state)
                {
                    case State.SelectingSourceConnector:
                        Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] SelectSource {controlPoint2.m_OriginalEntity}");
                        if (_controlPoints.Length > 0 && _controlPoints[0].m_OriginalEntity.Equals(controlPoint2.m_OriginalEntity))
                        {
                            Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] SelectSource-nothing");
                            applyMode = ApplyMode.None;
                            return inputHandle;
                        }
                        _controlPoints.Clear();
                        _controlPoints.Add(controlPoint2);
                        _lastControlPoint = controlPoint2;
                        applyMode = ApplyMode.None;
                        Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] SelectSource-clear");
                        return inputHandle;

                    case State.SelectingTargetConnector:
                        Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] SelectTarget {controlPoint2.m_OriginalEntity}");
                        if (!minorChange && _controlPoints.Length > 1 && _lastControlPoint.m_OriginalEntity.Equals(controlPoint2.m_OriginalEntity))
                        {
                            if (!_lastControlPoint.m_Position.Equals(controlPoint2.m_Position))
                            {
                                //soft update (position only)
                                ControlPoint p1 = _controlPoints[0];
                                _controlPoints.Clear();
                                _controlPoints.Add(in p1);
                                _controlPoints.Add(in controlPoint2);
                                // Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] SelectTarget diff position");
                            }
                            applyMode = ApplyMode.None;
                            return inputHandle;
                        }
                        Entity lastEntity = _lastControlPoint.m_OriginalEntity;
                        _lastControlPoint = controlPoint2;
                        if (_controlPoints.Length > 0)
                        {
                            ControlPoint p1 = _controlPoints[0];
                            _controlPoints.Clear();
                            _controlPoints.Add(in p1);
                            if (lastEntity == controlPoint2.m_OriginalEntity)
                            {
                                _controlPoints.Add(in controlPoint2);
                            }
                        }
                        applyMode = ApplyMode.Clear;
                        // Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] SelectTarget-clear");
                        return UpdateDefinitions(inputHandle);
                }
            }
            // Logger.DebugTool($"[Update] {_state} No Hit: {controlPoint2.m_OriginalEntity}, f: {forceUpdate} | count: {_controlPoints.Length}");
            //TODO needs more tests (not quite sure if reliable) 
            if (_tempQuery.IsEmptyIgnoreFilter && !_definitionQuery.IsEmptyIgnoreFilter && _selectedNode != Entity.Null)
            {
                Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] Reset: {_lastControlPoint.m_OriginalEntity} | {_selectedNode}");
                _lastControlPoint = default;
                applyMode = ApplyMode.Clear;
                return UpdateDefinitions(inputHandle);
            }
            if (_lastControlPoint.m_OriginalEntity.Equals(controlPoint2.m_OriginalEntity))
            {
                Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] TheSame: {_lastControlPoint.m_OriginalEntity}, force: {forceUpdate}");
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
                Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] Different, updating: ({_lastControlPoint.m_Position}) {_lastControlPoint.m_OriginalEntity}");
                _lastControlPoint = default;
                if (_controlPoints.Length > 0)
                {
                    _controlPoints[_controlPoints.Length - 1] = default;
                }

                applyMode = ApplyMode.Clear;
                return UpdateDefinitions(inputHandle);
            }
            Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] SomethingElse {_lastControlPoint.m_HitPosition} | {_lastControlPoint.m_OriginalEntity}");
            return inputHandle;
        }

        private JobHandle Cancel(JobHandle inputHandle) {
            applyMode = ApplyMode.None;
            Logger.DebugTool($"[Cancel {UnityEngine.Time.frameCount}] State: {_state}");
            switch (_state)
            {
                case State.Default:
                    applyMode = ApplyMode.Clear;
                    _state = State.Default;
                    m_ToolSystem.activeTool = m_DefaultToolSystem;
                    return SelectIntersectionNode(inputHandle, Entity.Null);
                case State.SelectingSourceConnector:
                    applyMode = ApplyMode.Clear;
                    _state = State.Default;
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
            applyMode = ApplyMode.Clear;
            return inputDeps;
        }

        private JobHandle ApplyQuickModification(JobHandle inputDeps)
        {
            if (_state != State.ApplyingQuickModifications)
            {
                _state = State.ApplyingQuickModifications;
                _lastControlPoint = default;
                _controlPoints.Clear();             
                applyMode = ApplyMode.Clear;
                return UpdateDefinitions(inputDeps);
            }
            
            bool isAllowed = (IsApplyAllowed(useVanilla: false) && !_tempConnectionQuery.IsEmptyIgnoreFilter);
            ToolUXSoundSettingsData uxSounds = _soundQuery.GetSingleton<ToolUXSoundSettingsData>();
            _audioManager.PlayUISound(isAllowed ? uxSounds.m_SelectEntitySound : uxSounds.m_NetCancelSound);
            
            ToolMode = Mode.Default;
            _lastControlPoint = default;
            _controlPoints.Clear();
            _state = State.SelectingSourceConnector;
            applyMode = isAllowed ? ApplyMode.Apply : ApplyMode.Clear;
            return UpdateDefinitions(inputDeps, updateEditIntersection: true);
        }

        private JobHandle UpdateDefinitions(JobHandle inputDeps, bool updateEditIntersection = false) {
            JobHandle jobHandle = DestroyDefinitions(_definitionQuery, _toolOutputBarrier, inputDeps);
            Entity editingIntersection = Entity.Null;
            if (!_editIntersectionQuery.IsEmptyIgnoreFilter)
            {
                editingIntersection = _editIntersectionQuery.ToEntityArray(Allocator.Temp)[0];
                Logger.DebugTool($"[UpdateDefinitions][{UnityEngine.Time.frameCount}] Current Editing Intersection {editingIntersection}");
                if (editingIntersection != Entity.Null)
                {
                    if (!EntityManager.HasComponent<EditLaneConnections>(editingIntersection))
                    {
                        EntityManager.AddComponent<EditLaneConnections>(editingIntersection);
                    }
                    if (updateEditIntersection)
                    {
                        EntityQuery connectors = new EntityQueryBuilder(Allocator.Temp).WithAllRW<Connector, LaneConnection>().Build(EntityManager);
                        EntityQuery connections = new EntityQueryBuilder(Allocator.Temp).WithAll<Connection>().Build(EntityManager);
                        Logger.DebugTool($"[UpdateDefinitions] Cleanup connections. Connectors with LaneConnections: {connectors.CalculateEntityCount()}, connections: {connections.CalculateEntityCount()}");
                        NativeArray<Entity> entities = connectors.ToEntityArray(Allocator.Temp);
                        foreach (Entity entity in entities)
                        {
                            EntityManager.GetBuffer<LaneConnection>(entity).Clear();
                        }
                        EntityManager.DestroyEntity(connections);
                        EntityManager.AddComponent<Updated>(editingIntersection);
                        entities.Dispose();
                        connectors.Dispose();
                        connections.Dispose();
                    }
                }
            }
            
            Logger.DebugTool($"[UpdateDefinitions][{UnityEngine.Time.frameCount}] Scheduling CreateDefinitionsJob");
            CreateDefinitionsJob job = new CreateDefinitionsJob()
            {
                connectorData = SystemAPI.GetComponentLookup<Connector>(true),
                nodeData = SystemAPI.GetComponentLookup<Node>(true),
                prefabRefData = SystemAPI.GetComponentLookup<PrefabRef>(true),
                connectionsBuffer = SystemAPI.GetBufferLookup<LaneConnection>(true),
                connectionsBufferData = SystemAPI.GetBufferLookup<Connection>(true),
                modifiedConnectionBuffer = SystemAPI.GetBufferLookup<ModifiedLaneConnections>(true),
                generatedConnectionBuffer = SystemAPI.GetBufferLookup<GeneratedConnection>(true),
                connectorElementsBuffer = SystemAPI.GetBufferLookup<ConnectorElement>(true),
                quickActionData = SystemAPI.GetSingleton<ActionOverlayData>(),
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

        private JobHandle SelectIntersectionNode(JobHandle inputDeps, Entity node)
        {
            CleanupIntersectionHelpers();
            ToggleToolManaged(node);
            CleanupEditIntersection();
            _selectedNode = node;
            _modUISystem.SelectedIntersection = new ModUISystem.SelectedIntersectionData() { entity = node };
            if (node != Entity.Null) {
                SelectIntersectionNodeJob selectNodeJob = new SelectIntersectionNodeJob
                {
                    elevationData = SystemAPI.GetComponentLookup<Elevation>(true),
                    upgradedData = SystemAPI.GetComponentLookup<Upgraded>(true),
                    edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                    modifiedConnectionsData = SystemAPI.GetComponentLookup<ModifiedConnections>(true),
                    connectedEdgeBuffer = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                    modifiedConnectionsTypeSet = _modifiedConnectionsTypeSet,
                    node = node,
                    nodeElevation = _nodeElevation,
                    commandBuffer = _toolOutputBarrier.CreateCommandBuffer(),
                };
                JobHandle jobHandle = selectNodeJob.Schedule(inputDeps);
                _toolOutputBarrier.AddJobHandleForProducer(jobHandle);
                PlaySelectedSound();
                return DestroyDefinitions(_definitionQuery, _toolOutputBarrier, jobHandle);
            }
            _audioManager.PlayUISound(_soundQuery.GetSingleton<ToolUXSoundSettingsData>().m_NetCancelSound);
            
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
            Logger.DebugTool($"CleanupIntersectionHelpers! {_raycastHelpersQuery.CalculateEntityCount()}");
            EntityManager.AddComponent<Deleted>(_raycastHelpersQuery);
        }

        private void CleanupEditIntersection()
        {
            EntityManager.AddComponent<Deleted>(_editIntersectionQuery);
        }

        private void ToggleToolManaged(Entity entity)
        {
            if (!_toolSelectionQuery.IsEmptyIgnoreFilter)
            {
                EntityManager.RemoveComponent<ToolManaged>(_toolSelectionQuery);
            }
            if (entity != Entity.Null && !EntityManager.HasComponent<ToolManaged>(entity))
            {
                EntityManager.AddComponent<ToolManaged>(entity);
            }
        }

        private bool CheckToolboxActions(JobHandle inputDeps, out JobHandle jobHandle)
        {
            if (_state == State.Default || ToolMode == Mode.ApplyQuickModifications)
            {
                jobHandle = new JobHandle();
                return false;
            }
            
            if (_resetIntersectionToDefaultsAction.WasPerformedThisFrame())
            {
                ToolMode = Mode.ApplyQuickModifications;
                SetActionOverlay(ModUISystem.ActionOverlayPreview.ResetToVanilla);
                jobHandle = ApplyQuickModification(inputDeps);
                return true;
            }
            if (_removeAllConnectionsAction.WasPerformedThisFrame())
            {
                ToolMode = Mode.ApplyQuickModifications;
                SetActionOverlay(ModUISystem.ActionOverlayPreview.RemoveAllConnections);
                jobHandle = ApplyQuickModification(inputDeps);
                return true;
            }
            if (_removeUTurnsAction.WasPerformedThisFrame())
            {
                ToolMode = Mode.ApplyQuickModifications;
                SetActionOverlay(ModUISystem.ActionOverlayPreview.RemoveUTurns);
                jobHandle = ApplyQuickModification(inputDeps);
                return true;
            }
            if (_removeUnsafeAction.WasPerformedThisFrame())
            {
                ToolMode = Mode.ApplyQuickModifications;
                SetActionOverlay(ModUISystem.ActionOverlayPreview.RemoveUnsafe);
                jobHandle = ApplyQuickModification(inputDeps);
                return true;
            }
            
            jobHandle = new JobHandle();
            return false;
        }

        private void SetActionOverlay(ModUISystem.ActionOverlayPreview state)
        {
            bool isValid = _selectedNode != Entity.Null && EntityManager.Exists(_selectedNode);
            ActionOverlayData actionOverlayData = SystemAPI.GetSingleton<ActionOverlayData>();
            actionOverlayData.entity = state != ModUISystem.ActionOverlayPreview.None && isValid ? _selectedNode : Entity.Null;
            actionOverlayData.mode = isValid ? state : ModUISystem.ActionOverlayPreview.None;
            SystemAPI.SetSingleton(actionOverlayData);
        }

        private void UpdateToolActions(bool enable)
        {
            //toolbox actions
            bool toolboxActive = _state > State.Default && enable;
            _removeAllConnectionsAction.shouldBeEnabled = toolboxActive;
            _removeUTurnsAction.shouldBeEnabled = toolboxActive;
            _removeUnsafeAction.shouldBeEnabled = toolboxActive;
        }

        internal void ResetAllConnections()
        {
            Logger.DebugTool("Resetting All Lane Connections");
            _lastControlPoint = default;
            _selectedNode = Entity.Null;
            _controlPoints.Clear();
            CleanupIntersectionHelpers();
            
            EntityQuery query = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<ModifiedLaneConnections, Node>()
                .WithNone<Deleted, Temp>()
                .Build(EntityManager);
            if (!query.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> entities = query.ToEntityArray(Allocator.TempJob);
                Logger.DebugTool($"Resetting All Lane Connections from {entities.Length} nodes");
                EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
                JobHandle removeConnectionsHandle = new RemoveLaneConnectionsJob()
                {
                    entities = entities,
                    edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                    deletedData = SystemAPI.GetComponentLookup<Deleted>(true),
                    connectedEdgeData = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                    modifiedLaneConnectionsData = SystemAPI.GetBufferLookup<ModifiedLaneConnections>(true),
                    commandBuffer = commandBuffer.AsParallelWriter(),
                }.Schedule(entities.Length, Dependency);
                entities.Dispose(removeConnectionsHandle);
                removeConnectionsHandle.Complete();
                commandBuffer.Playback(EntityManager);
                commandBuffer.Dispose();
                _audioManager.PlayUISound(_soundQuery.GetSingleton<ToolUXSoundSettingsData>().m_BulldozeSound);
            }
            query.Dispose();
            
            // exist tool in case it's still active
            if (m_ToolSystem.activeTool == this)
            {
                m_ToolSystem.activeTool = m_DefaultToolSystem;
            }
        }
    }
}
