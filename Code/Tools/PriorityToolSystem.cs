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

namespace Traffic.Tools
{
#if WITH_BURST
    [BurstCompile]
#endif
    public partial class PriorityToolSystem : ToolBaseSystem
    {
        public enum Mode
        {
            Default,
            /// <summary>
            /// Quick modification mode - prepare definitions for the current modification action
            /// Definitions will generate Temp entities which will be applied in the next frame
            /// </summary>
            ApplyQuickModifications,
        }

        public enum State
        {
            Default,
            ChangingPriority,
            /// <summary>
            /// Selected intersection, next frame after creating definitions for quick modification action
            /// </summary>
            ApplyingQuickModifications,
        }

        public override string toolID => UIBindingConstants.PRIORITIES_TOOL;

        public ModUISystem.PriorityToolSetMode ToolSetMode
        {
            get => _toolSetMode;
            set {
                if (value <= ModUISystem.PriorityToolSetMode.Reset)
                {
                    _toolSetMode = value;
                    // _toolModeChanged = true;
                }
            }
        }

        public ModUISystem.OverlayMode ToolOverlayMode
        {
            get => _overlayMode;
            set {
                if (value <= ModUISystem.OverlayMode.Lane)
                {
                    _overlayMode = value;
                    // _overlayModeChanged = true;
                }
            }
        }

        public State ToolState => _state;

        public Mode ToolMode { get; set; }

        private ProxyAction _applyAction;
        private ProxyAction _secondaryApplyAction;
        private ProxyAction _toggleDisplayModeAction;
        private ProxyAction _usePriorityAction;
        private ProxyAction _useYieldAction;
        private ProxyAction _useStopAction;
        private ProxyAction _useResetAction;
        private ProxyAction _resetIntersectionToDefaultsAction;
        private AudioManager _audioManager;
        private ToolOutputBarrier _toolOutputBarrier;
        private ModUISystem _modUISystem;
        private Game.Tools.ValidationSystem _validationSystem;

        private ModUISystem.PriorityToolSetMode _toolSetMode;
        private ModUISystem.OverlayMode _overlayMode;
        // private bool _toolModeChanged;
        // private bool _overlayModeChanged;

        private Entity _selectedNode;
        private NativeList<ControlPoint> _controlPoints;
        private ControlPoint _lastControlPoint;
        private EntityQuery _definitionQuery;
        private EntityQuery _tempPrioritiesQuery;
        private EntityQuery _soundQuery;
        private EntityQuery _raycastHelpersQuery;
        private EntityQuery _editIntersectionQuery;
        private EntityQuery _hoveringIntersectionQuery;
        private EntityQuery _toolFeedbackQuery;

        private State _state;
        private ModRaycastSystem _modRaycastSystem;
        private Camera _mainCamera;

        public bool Underground { get; set; }
        public override bool allowUnderground => true;
        
        public override PrefabBase GetPrefab()
        {
            return null;
        }

        public override bool TrySetPrefab(PrefabBase prefab)
        {
            return false;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            _controlPoints = new NativeList<ControlPoint>(1, Allocator.Persistent);

            // Systems
            _audioManager = World.GetOrCreateSystemManaged<AudioManager>();
            _toolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();
            _modUISystem = World.GetOrCreateSystemManaged<ModUISystem>();
            _modRaycastSystem = World.GetOrCreateSystemManaged<ModRaycastSystem>();
            _validationSystem = World.GetOrCreateSystemManaged<Game.Tools.ValidationSystem>();

            // queries
            _definitionQuery = GetDefinitionQuery();
            _tempPrioritiesQuery = GetEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<LanePriority>(), ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Temp>(), }, None = new[] { ComponentType.ReadOnly<Deleted>(), } });
            _soundQuery = GetEntityQuery(ComponentType.ReadOnly<ToolUXSoundSettingsData>());
            _raycastHelpersQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new [] { ComponentType.ReadOnly<LaneHandle>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), },
            });
            _editIntersectionQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new [] { ComponentType.ReadOnly<EditIntersection>(), ComponentType.ReadOnly<EditPriorities>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            _hoveringIntersectionQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new [] { ComponentType.ReadOnly<EditIntersection>(), ComponentType.ReadOnly<Temp>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            _toolFeedbackQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { ComponentType.ReadOnly<ToolFeedbackInfo>(), ComponentType.ReadOnly<ToolActionBlocked>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            
            // Actions
            _applyAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.ApplyTool);
            _secondaryApplyAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.CancelTool);

            _toggleDisplayModeAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.PrioritiesToggleDisplayMode);
            _usePriorityAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.PrioritiesPriority);
            _useYieldAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.PrioritiesYield);
            _useStopAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.PrioritiesStop);
            _useResetAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.PrioritiesReset);
            _resetIntersectionToDefaultsAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.ResetIntersectionToDefaults);
            ManageActionListeners(enable: true);
            Enabled = false;
        }

        private void ActionPhaseChanged(ProxyAction action, InputActionPhase phase)
        {
            if (phase != InputActionPhase.Performed) 
                return;

            if (action.name.Equals(ModSettings.KeyBindAction.PrioritiesToggleDisplayMode))
            {
                ToolOverlayMode = _overlayMode == ModUISystem.OverlayMode.Lane ? ModUISystem.OverlayMode.LaneGroup : ModUISystem.OverlayMode.Lane;
                return;   
            }
            
            ToolSetMode = action.name switch
            {
                ModSettings.KeyBindAction.PrioritiesPriority => ModUISystem.PriorityToolSetMode.Priority,
                ModSettings.KeyBindAction.PrioritiesYield => ModUISystem.PriorityToolSetMode.Yield,
                ModSettings.KeyBindAction.PrioritiesStop => ModUISystem.PriorityToolSetMode.Stop,
                ModSettings.KeyBindAction.PrioritiesReset => ModUISystem.PriorityToolSetMode.Reset,
                _ => ToolSetMode
            };
        }

        protected override void OnStartRunning()
        {
            Logger.DebugTool($"Starting {nameof(PriorityToolSystem)}");
            base.OnStartRunning();
            _controlPoints.Clear();
            _mainCamera = Camera.main;
            _state = State.Default;
            _selectedNode = Entity.Null;
            _overlayMode = ModUISystem.OverlayMode.LaneGroup;
            _toolSetMode = ModUISystem.PriorityToolSetMode.Yield;
            _modUISystem.SelectedIntersection = default;
            _modRaycastSystem.Enabled = true;
            _validationSystem.Enabled = false;
            requireUnderground = false;
            ToolMode = Mode.Default;
        }

        protected override void OnStopRunning()
        {
            Logger.DebugTool($"Stopping {nameof(PriorityToolSystem)}");
            base.OnStopRunning();
            _state = State.Default;
            _mainCamera = null;
            CleanupIntersectionHelpers();
            CleanupEditIntersection();
            _modRaycastSystem.Enabled = false;
            _validationSystem.Enabled = true;
            UpdateActionState(false);
        }

        public NativeList<ControlPoint> GetControlPoints(out JobHandle dependencies)
        {
            dependencies = base.Dependency;
            return _controlPoints;
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
        
        private bool IsApplyAllowed(bool useVanilla = true)
        {
            if (useVanilla)
            {
                return GetAllowApply();
            }
            // workaround for vanilla OriginalDeletedSystem result (fix bug)
            return _toolFeedbackQuery.IsEmptyIgnoreFilter && (m_ToolSystem.ignoreErrors || m_ErrorQuery.IsEmptyIgnoreFilter);
        }

        private bool GetCustomRaycastResult(out ControlPoint controlPoint)
        {
            if (GetCustomRayCastResult(out Entity entity, out RaycastHit hit))
            {
                controlPoint = new ControlPoint(entity, hit);
                return true;
            }
            controlPoint = default;
            return false;
        }

        private bool GetCustomRaycastResult(out ControlPoint controlPoint, out bool forceUpdate)
        {
            forceUpdate = m_OriginalDeletedSystem.GetOriginalDeletedResult(1);
            return GetCustomRaycastResult(out controlPoint);
        }

        private bool GetCustomRayCastResult(out Entity entity, out RaycastHit hit)
        {
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

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();

            if (_mainCamera && _state > State.Default)
            {
                CustomRaycastInput input = new CustomRaycastInput();
                input.line = ToolRaycastSystem.CalculateRaycastLine(_mainCamera);
                input.offset = new float3(0, 0, 1.5f);
                input.typeMask = TypeMask.Lanes;
                input.fovTan = math.tan(math.radians(_mainCamera.fieldOfView) * 0.5f);
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
            m_ToolRaycastSystem.netLayerMask = Layer.Road | Layer.Pathway;
            m_ToolRaycastSystem.iconLayerMask = IconLayerMask.None;
            m_ToolRaycastSystem.utilityTypeMask = UtilityTypes.None;
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
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
            
            UpdateActionState(true);
            
            if (ToolMode == Mode.ApplyQuickModifications)
            {
                return ApplyQuickModification(inputDeps);
            }

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
            if (_state == State.Default)
            {
                if (IsApplyAllowed(useVanilla: false) &&
                    GetRaycastResult(out Entity entity, out RaycastHit _) &&
                    !_hoveringIntersectionQuery.IsEmptyIgnoreFilter &&
                    EntityManager.HasComponent<Node>(entity) &&
                    EntityManager.TryGetBuffer(entity, isReadOnly: true, out DynamicBuffer<ConnectedEdge> edges) && edges.Length > 2)
                {
                    applyMode = ApplyMode.None;
                    _state = State.ChangingPriority;
                    _controlPoints.Clear();
                    return SelectIntersectionNode(inputDeps, entity);
                }
                return Update(inputDeps);
            }
            if (_state == State.ChangingPriority)
            {
                if (IsApplyAllowed(useVanilla: false) &&
                    GetCustomRaycastResult(out ControlPoint cp) &&
                    EntityManager.HasComponent<LaneHandle>(cp.m_OriginalEntity))
                {
                    applyMode = ApplyMode.Apply;
                    return UpdateDefinitions(inputDeps, true);
                }
            }

            return inputDeps;
        }

        private JobHandle Update(JobHandle inputHandle)
        {
            if (_state == State.Default)
            {
                if (GetRaycastResult(out ControlPoint controlPoint, out bool forceUpdate))
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

            if (_state == State.ChangingPriority)
            {
                if (GetCustomRaycastResult(out ControlPoint cp) && EntityManager.HasComponent<LaneHandle>(cp.m_OriginalEntity))
                {
                    Logger.DebugTool($"[Update {UnityEngine.Time.frameCount}] SelectTarget {cp.m_OriginalEntity}");
                    if (_lastControlPoint.m_OriginalEntity.Equals(cp.m_OriginalEntity))
                    {
                        applyMode = ApplyMode.None;
                        return inputHandle;
                    }

                    _lastControlPoint = cp;
                    _controlPoints.Clear();
                    _controlPoints.Add(in cp);
                    applyMode = ApplyMode.Clear;
                    return UpdateDefinitions(inputHandle);
                }
                if (!_lastControlPoint.Equals(default))
                {
                    _lastControlPoint = default;
                    if (_controlPoints.Length > 0)
                    {
                        _controlPoints.Clear();
                    }

                    applyMode = ApplyMode.Clear;
                    return UpdateDefinitions(inputHandle);
                }
            }
            return inputHandle;
        }

        private JobHandle Cancel(JobHandle inputHandle)
        {
            applyMode = ApplyMode.None;
            Logger.DebugTool($"[Cancel {UnityEngine.Time.frameCount}] State: {_state}");
            switch (_state)
            {
                case State.Default:
                    m_ToolSystem.activeTool = m_DefaultToolSystem;
                    return SelectIntersectionNode(inputHandle, Entity.Null);
                case State.ChangingPriority:
                    applyMode = ApplyMode.Clear;
                    _state = State.Default;
                    return SelectIntersectionNode(inputHandle, Entity.Null);
            }

            return inputHandle;
        }

        private JobHandle Clear(JobHandle inputDeps)
        {
            base.applyMode = ApplyMode.Clear;
            return inputDeps;
        }
        
        private JobHandle ApplyQuickModification(JobHandle inputDeps)
        {
            if (_state != State.ApplyingQuickModifications)
            {
                _state = State.ApplyingQuickModifications;
                _lastControlPoint = default;
                _controlPoints.Clear();
                _controlPoints.Add(new ControlPoint() {m_OriginalEntity = _selectedNode});
                applyMode = ApplyMode.Clear;
                return UpdateDefinitions(inputDeps);
            }
            
            bool isAllowed = (IsApplyAllowed(useVanilla: false) && !_tempPrioritiesQuery.IsEmptyIgnoreFilter);
            ToolUXSoundSettingsData uxSounds = _soundQuery.GetSingleton<ToolUXSoundSettingsData>();
            _audioManager.PlayUISound(isAllowed ? uxSounds.m_SelectEntitySound : uxSounds.m_NetCancelSound);
            
            ToolMode = Mode.Default;
            _lastControlPoint = default;
            _state = State.ChangingPriority;
            applyMode = isAllowed ? ApplyMode.Apply : ApplyMode.Clear;
            return UpdateDefinitions(inputDeps, true);
        }

        private JobHandle SelectIntersectionNode(JobHandle inputDeps, Entity node)
        {
            CleanupIntersectionHelpers();
            CleanupEditIntersection();
            _selectedNode = node;
            _modUISystem.SelectedIntersection = new ModUISystem.SelectedIntersectionData() { entity = node };
            if (node != Entity.Null && EntityManager.HasComponent<Node>(node))
            {
                Entity entity = EntityManager.CreateEntity();
                EntityManager.AddComponent<EditIntersection>(entity);
                EntityManager.SetComponentData<EditIntersection>(entity, new EditIntersection() { node = node });
                EntityManager.AddComponent<EditPriorities>(entity);
                EntityManager.AddComponent<Updated>(entity);
                _audioManager.PlayUISound(_soundQuery.GetSingleton<ToolUXSoundSettingsData>().m_SelectEntitySound);
                return DestroyDefinitions(_definitionQuery, _toolOutputBarrier, inputDeps);
            }
            _audioManager.PlayUISound(_soundQuery.GetSingleton<ToolUXSoundSettingsData>().m_NetCancelSound);

            return inputDeps;
        }

        private JobHandle UpdateDefinitions(JobHandle inputDeps, bool apply = false)
        {
            JobHandle jobHandle = DestroyDefinitions(_definitionQuery, _toolOutputBarrier, inputDeps);

            if (apply)
            {
                CleanupIntersectionHelpers();
                CleanupEditIntersection();
                _controlPoints.Clear();
                _controlPoints.Add(new ControlPoint()
                {
                    m_OriginalEntity = _selectedNode
                });
            }

            JobHandle definitionJobHandle = new CreateDefinitionsJob()
            {
                nodeData = SystemAPI.GetComponentLookup<Node>(true),
                laneHandleData = SystemAPI.GetComponentLookup<LaneHandle>(true),
                prefabRefData = SystemAPI.GetComponentLookup<PrefabRef>(true),
                dataOwnerData = SystemAPI.GetComponentLookup<DataOwner>(true),
                edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                compositionData = SystemAPI.GetComponentLookup<Composition>(true),
                netCompositionLanes = SystemAPI.GetBufferLookup<NetCompositionLane>(true),
                lanePriorities = SystemAPI.GetBufferLookup<LanePriority>(true),
                priorityHandles = SystemAPI.GetBufferLookup<PriorityHandle>(true),
                connectedEdges = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                quickActionData = SystemAPI.GetSingleton<ActionOverlayData>(),
                state = _state,
                mode = _toolSetMode,
                overlayMode = _overlayMode,
                controlPoints = _controlPoints,
                updateIntersection = apply,
                commandBuffer = _toolOutputBarrier.CreateCommandBuffer(),
            }.Schedule(inputDeps);

            _toolOutputBarrier.AddJobHandleForProducer(definitionJobHandle);

            return JobHandle.CombineDependencies(definitionJobHandle, jobHandle);
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
                Logger.DebugTool($"Delete! {_selectedNode} state: {_state}");
                ToolMode = Mode.ApplyQuickModifications;
                SetActionOverlay(ModUISystem.ActionOverlayPreview.ResetToVanilla);
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
        
        private void ManageActionListeners(bool enable)
        {
            if (enable)
            {
                _toggleDisplayModeAction.onInteraction += ActionPhaseChanged;
                _usePriorityAction.onInteraction += ActionPhaseChanged;
                _useYieldAction.onInteraction += ActionPhaseChanged;
                _useStopAction.onInteraction += ActionPhaseChanged;
                _useResetAction.onInteraction += ActionPhaseChanged;
            }
            else
            {
                _toggleDisplayModeAction.onInteraction -= ActionPhaseChanged;
                _usePriorityAction.onInteraction -= ActionPhaseChanged;
                _useYieldAction.onInteraction -= ActionPhaseChanged;
                _useStopAction.onInteraction -= ActionPhaseChanged;
                _useResetAction.onInteraction -= ActionPhaseChanged;
            }
        }

        internal void ResetAllPriorities()
        {
            Logger.DebugTool("Resetting All Priorities");
            _lastControlPoint = default;
            _selectedNode = Entity.Null;
            _controlPoints.Clear();
            CleanupIntersectionHelpers();

            EntityQuery query = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<LanePriority, Edge>()
                .WithNone<Deleted>()
                .Build(EntityManager);
            if (!query.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> entities = query.ToEntityArray(Allocator.TempJob);
                Logger.DebugTool($"Resetting All Priorities from {entities.Length} edges");
                EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
                JobHandle removePrioritiesHandle = new RemoveLanePrioritiesJob()
                {
                    entities = entities,
                    edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                    modifiedPriorityData = SystemAPI.GetComponentLookup<ModifiedPriorities>(true),
                    commandBuffer = commandBuffer.AsParallelWriter(),
                }.Schedule(entities.Length, Dependency);
                entities.Dispose(removePrioritiesHandle);
                removePrioritiesHandle.Complete();
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

        private void CleanupIntersectionHelpers()
        {
            Logger.DebugTool($"CleanupIntersectionHelpers! {_raycastHelpersQuery.CalculateEntityCount()}");
            EntityManager.RemoveComponent<PriorityHandle>(_editIntersectionQuery);
            EntityManager.AddComponent<Deleted>(_raycastHelpersQuery);
        }

        private void CleanupEditIntersection()
        {
            Logger.DebugTool("CleanupEditIntersection!");
            EntityManager.AddComponent<Deleted>(_editIntersectionQuery);
        }

        private void UpdateActionState(bool shouldEnable)
        {            
            //toolbox
            bool toolboxActive = _state == State.ChangingPriority && shouldEnable;
            _toggleDisplayModeAction.shouldBeEnabled = toolboxActive;
            _usePriorityAction.shouldBeEnabled = toolboxActive;
            _useYieldAction.shouldBeEnabled = toolboxActive;
            _useStopAction.shouldBeEnabled = toolboxActive;
            _useResetAction.shouldBeEnabled = toolboxActive;
        }

        protected override void OnDestroy()
        {
            ManageActionListeners(enable: false);
            base.OnDestroy();
        }
    }
}
