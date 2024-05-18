using System;
using Colossal.Collections;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Audio;
using Game.Common;
using Game.Input;
using Game.Net;
using Game.Notifications;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components;
using Traffic.Components.PrioritySigns;
using Traffic.Systems;
using Traffic.UISystems;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Traffic.Tools
{
    public partial class PriorityToolSystem : ToolBaseSystem
    {
        public enum Mode
        {
            Default,
            ApplyPreviewModifications,
        }

        public enum State
        {
            Default,
            ChangePriority,
        }

        public override string toolID => UIBindingConstants.PRIORITIES_TOOL;

        public ModUISystem.PriorityToolSetMode ToolSetMode
        {
            get => _toolSetMode;
            set {
                if (value <= ModUISystem.PriorityToolSetMode.Reset)
                {
                    _toolSetMode = value;
                    _toolModeChanged = true;
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
                    _overlayModeChanged = true;
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
        public Entity HoveredEntity;
        public float3 LastPos;
        private AudioManager _audioManager;
        private ToolOutputBarrier _toolOutputBarrier;
        private OverlayRenderSystem _overlayRenderSystem;
        private ModUISystem _modUISystem;

        private ModUISystem.PriorityToolSetMode _toolSetMode;
        private bool _toolModeChanged;
        private ModUISystem.OverlayMode _overlayMode;
        private bool _overlayModeChanged;

        private Entity _selectedNode;
        private NativeList<ControlPoint> _controlPoints;
        private ControlPoint _lastControlPoint;
        private EntityQuery _definitionQuery;
        private EntityQuery _soundQuery;
        private EntityQuery _highlightedQuery;
        private EntityQuery _raycastHelpersQuery;
        private EntityQuery _toolFeedbackQuery;

        private State _state;
        private ModRaycastSystem _modRaycastSystem;
        private Camera _mainCamera;

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
            _overlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            _modUISystem = World.GetOrCreateSystemManaged<ModUISystem>();
            _modRaycastSystem = World.GetOrCreateSystemManaged<ModRaycastSystem>();

            // queries
            _definitionQuery = GetDefinitionQuery();
            _soundQuery = GetEntityQuery(ComponentType.ReadOnly<ToolUXSoundSettingsData>());
            _raycastHelpersQuery = GetEntityQuery(new EntityQueryDesc
            {
                Any = new[] { ComponentType.ReadOnly<EditIntersection>(), ComponentType.ReadOnly<EditPriorities>(), ComponentType.ReadOnly<LaneHandle>(), },
                None = new[] { ComponentType.ReadOnly<Deleted>() }
            });
            _toolFeedbackQuery = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { ComponentType.ReadOnly<ToolFeedbackInfo>(), ComponentType.ReadOnly<ToolActionBlocked>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            _highlightedQuery = SystemAPI.QueryBuilder().WithAll<Highlighted, LaneHandle>().WithNone<Deleted>().Build();
            // Actions
            _applyAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.ApplyTool);
            _secondaryApplyAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.CancelTool);

            _toggleDisplayModeAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.PrioritiesToggleDisplayMode);
            _usePriorityAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.PrioritiesPriority);
            _useYieldAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.PrioritiesYield);
            _useStopAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.PrioritiesStop);
            _useResetAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.PrioritiesReset);
            _toggleDisplayModeAction.onInteraction += ActionPhaseChanged;
            _usePriorityAction.onInteraction += ActionPhaseChanged;
            _useYieldAction.onInteraction += ActionPhaseChanged;
            _useStopAction.onInteraction += ActionPhaseChanged;
            _useResetAction.onInteraction += ActionPhaseChanged;
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
            base.OnStartRunning();
            Logger.Info($"Starting {nameof(PriorityToolSystem)}");
            _controlPoints.Clear();
            _mainCamera = Camera.main;
            _state = State.Default;
            _selectedNode = Entity.Null;
            _overlayMode = ModUISystem.OverlayMode.LaneGroup;
            _toolSetMode = ModUISystem.PriorityToolSetMode.Yield;
            _modUISystem.SelectedIntersection = default;
            _modRaycastSystem.Enabled = true;
            UpdateActionState(true);
            ToolMode = Mode.Default;
        }

        protected override void OnStopRunning()
        {
            base.OnStopRunning();
            Logger.Info($"Stopping {nameof(PriorityToolSystem)}");
            _state = State.Default;
            _mainCamera = null;
            HoveredEntity = Entity.Null;
            LastPos = float3.zero;
            CleanupIntersectionHelpers();
            _modRaycastSystem.Enabled = false;
            UpdateActionState(false);
        }

        protected override void OnDestroy()
        {
            _toggleDisplayModeAction.onInteraction -= ActionPhaseChanged;
            _usePriorityAction.onInteraction -= ActionPhaseChanged;
            _useYieldAction.onInteraction -= ActionPhaseChanged;
            _useStopAction.onInteraction -= ActionPhaseChanged;
            _useResetAction.onInteraction -= ActionPhaseChanged;
            base.OnDestroy();
        }

        public NativeList<ControlPoint> GetControlPoints(out JobHandle dependencies)
        {
            dependencies = base.Dependency;
            return _controlPoints;
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

            m_ToolRaycastSystem.collisionMask = (CollisionMask.OnGround | CollisionMask.Overground);
            m_ToolRaycastSystem.typeMask = (TypeMask.Net);
            m_ToolRaycastSystem.raycastFlags = RaycastFlags.SubElements | RaycastFlags.Cargo | RaycastFlags.Passenger | RaycastFlags.EditorContainers;
            m_ToolRaycastSystem.netLayerMask = Layer.All;
            m_ToolRaycastSystem.iconLayerMask = IconLayerMask.None;
            m_ToolRaycastSystem.utilityTypeMask = UtilityTypes.None;
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            if (m_FocusChanged)
            {
                return inputDeps;
            }


            if (ToolMode == Mode.ApplyPreviewModifications)
            {
                ToolMode = Mode.Default;
                return ApplyPreviewedAction(inputDeps);
            }

            if ((m_ToolRaycastSystem.raycastFlags & (RaycastFlags.DebugDisable | RaycastFlags.UIDisable)) == 0)
            {
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


        private JobHandle Apply(JobHandle inputDeps)
        {
            if (_state == State.Default)
            {
                if (IsApplyAllowed(useVanilla: false) &&
                    GetRaycastResult(out Entity entity, out RaycastHit _) &&
                    EntityManager.HasComponent<Node>(entity) &&
                    EntityManager.TryGetBuffer(entity, isReadOnly: true, out DynamicBuffer<ConnectedEdge> edges) && edges.Length > 2)
                {
                    applyMode = ApplyMode.None;
                    _state = State.ChangePriority;
                    _controlPoints.Clear();
                    return SelectIntersectionNode(inputDeps, entity);
                }
                return Update(inputDeps);
            }
            if (_state == State.ChangePriority)
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

            if (_state == State.ChangePriority)
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
                case State.ChangePriority:
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

        private JobHandle SelectIntersectionNode(JobHandle inputDeps, Entity node)
        {
            CleanupIntersectionHelpers();
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
            else
            {
                _audioManager.PlayUISound(_soundQuery.GetSingleton<ToolUXSoundSettingsData>().m_NetCancelSound);
            }

            return inputDeps;
        }

        private JobHandle UpdateDefinitions(JobHandle inputDeps, bool apply = false)
        {
            JobHandle jobHandle = DestroyDefinitions(_definitionQuery, _toolOutputBarrier, inputDeps);

            if (apply)
            {
                CleanupIntersectionHelpers();
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
                lanePriorities = SystemAPI.GetBufferLookup<LanePriority>(true),
                priorityHandles = SystemAPI.GetBufferLookup<PriorityHandle>(true),
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

        private struct CreateDefinitionsJob : IJob
        {
            [ReadOnly] public ComponentLookup<Node> nodeData;
            [ReadOnly] public ComponentLookup<LaneHandle> laneHandleData;
            [ReadOnly] public ComponentLookup<PrefabRef> prefabRefData;
            [ReadOnly] public ComponentLookup<DataOwner> dataOwnerData;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public BufferLookup<PriorityHandle> priorityHandles;
            [ReadOnly] public NativeList<ControlPoint> controlPoints;
            [ReadOnly] public BufferLookup<LanePriority> lanePriorities;
            [ReadOnly] public ModUISystem.PriorityToolSetMode mode;
            [ReadOnly] public State state;
            [ReadOnly] public ModUISystem.OverlayMode overlayMode;
            [ReadOnly] public bool updateIntersection;
            public EntityCommandBuffer commandBuffer;

            public void Execute()
            {
                if ((state == State.Default || updateIntersection) && !controlPoints.IsEmpty)
                {
                    ControlPoint controlPoint = controlPoints[0];
                    if (controlPoint.m_OriginalEntity != Entity.Null && nodeData.HasComponent(controlPoint.m_OriginalEntity))
                    {
                        Entity nodeEntity = controlPoint.m_OriginalEntity;
                        Entity entity = commandBuffer.CreateEntity();
                        if (!updateIntersection)
                        {
                            commandBuffer.AddComponent<Temp>(entity, new Temp(nodeEntity, TempFlags.Select));
                        }
                        commandBuffer.AddComponent<EditIntersection>(entity, new EditIntersection() { node = nodeEntity });
                        commandBuffer.AddComponent<EditPriorities>(entity);
                        commandBuffer.AddComponent<Updated>(entity);
                        CreateNodeDefinition(nodeEntity);
                    }

                    return;
                }
                if (state == State.ChangePriority)
                {
                    if (!controlPoints.IsEmpty)
                    {
                        ControlPoint controlPoint = controlPoints[0];
                        if (controlPoint.m_OriginalEntity != Entity.Null && laneHandleData.HasComponent(controlPoint.m_OriginalEntity))
                        {
                            Entity handleEntity = controlPoint.m_OriginalEntity;
                            LaneHandle laneHandle = laneHandleData[handleEntity];

                            Entity entity = commandBuffer.CreateEntity();
                            CreationDefinition definition = new CreationDefinition()
                            {
                                m_Original = laneHandle.edge
                            };
                            PriorityDefinition priorityDefinition = new PriorityDefinition()
                            {
                                edge = laneHandle.edge,
                                laneHandle = handleEntity,
                                node = laneHandle.node,
                            };

                            commandBuffer.AddComponent(entity, definition);
                            commandBuffer.AddComponent(entity, priorityDefinition);
                            commandBuffer.AddComponent<Updated>(entity);
                            CreateNodeDefinition(laneHandle.node);

                            DynamicBuffer<TempLanePriority> priorities;

                            PriorityType type = mode switch
                            {
                                ModUISystem.PriorityToolSetMode.Priority => PriorityType.RightOfWay,
                                ModUISystem.PriorityToolSetMode.Yield => PriorityType.Yield,
                                ModUISystem.PriorityToolSetMode.Stop => PriorityType.Stop,
                                ModUISystem.PriorityToolSetMode.Reset => PriorityType.Default,
                                _ => PriorityType.Default
                            };
                            if (lanePriorities.HasBuffer(laneHandle.edge))
                            {
                                DynamicBuffer<LanePriority> originalPriorities = lanePriorities[laneHandle.edge];
                                NativeList<TempLanePriority> tempPriorities = new NativeList<TempLanePriority>(originalPriorities.Length, Allocator.Temp);
                                NativeHashSet<int> laneIndexInGroup = overlayMode == ModUISystem.OverlayMode.LaneGroup ? CollectLanesInGroup(handleEntity, laneHandle) : default(NativeHashSet<int>);
                                /*copy all existint priorities skipping current handle or group of handles*/
                                CopySkipLaneHandle(ref tempPriorities, ref originalPriorities, laneHandle.laneIndex, laneIndexInGroup);
                                laneIndexInGroup.Dispose();

                                priorities = commandBuffer.AddBuffer<TempLanePriority>(entity);
                                /*copy remaining priorities */
                                priorities.CopyFrom(tempPriorities.AsArray());
                                tempPriorities.Dispose();
                                /*generate temp priorities for current state*/
                                FillTempPriorities(ref priorities, type, handleEntity, laneHandle);
                            }
                            else
                            {
                                priorities = commandBuffer.AddBuffer<TempLanePriority>(entity);
                                FillTempPriorities(ref priorities, type, handleEntity, laneHandle);
                            }
                        }
                    }
                }
            }

            private void FillTempPriorities(ref DynamicBuffer<TempLanePriority> priorities, PriorityType type, Entity handleEntity, LaneHandle referenceLaneHandle)
            {
                if (overlayMode != ModUISystem.OverlayMode.LaneGroup || !dataOwnerData.HasComponent(handleEntity))
                {
                    bool isEnd = edgeData[referenceLaneHandle.edge].m_End == referenceLaneHandle.node;
                    priorities.Add(new TempLanePriority()
                    {
                        laneIndex = referenceLaneHandle.laneIndex,
                        priority = type,
                        isEnd = isEnd
                    });
                }
                else
                {
                    DataOwner dataOwner = dataOwnerData[handleEntity];
                    if (priorityHandles.HasBuffer(dataOwner.entity))
                    {
                        DynamicBuffer<PriorityHandle> handles = priorityHandles[dataOwner.entity];
                        foreach (PriorityHandle priorityHandle in handles)
                        {
                            if (priorityHandle.edge == referenceLaneHandle.edge &&
                                laneHandleData.TryGetComponent(priorityHandle.laneHandle, out LaneHandle otherHandle) &&
                                otherHandle.handleGroup == referenceLaneHandle.handleGroup)
                            {
                                priorities.Add(new TempLanePriority()
                                {
                                    laneIndex = otherHandle.laneIndex,
                                    priority = type,
                                    isEnd = priorityHandle.isEnd
                                });
                            }
                        }
                    }
                }
            }

            private NativeHashSet<int> CollectLanesInGroup(Entity handleEntity, LaneHandle referenceLaneHandle)
            {
                NativeHashSet<int> result = default;
                DataOwner dataOwner = dataOwnerData[handleEntity];
                if (priorityHandles.HasBuffer(dataOwner.entity))
                {
                    result = new NativeHashSet<int>(4, Allocator.Temp);
                    DynamicBuffer<PriorityHandle> handles = priorityHandles[dataOwner.entity];
                    foreach (PriorityHandle priorityHandle in handles)
                    {
                        if (priorityHandle.edge == referenceLaneHandle.edge &&
                            laneHandleData.TryGetComponent(priorityHandle.laneHandle, out LaneHandle otherHandle) &&
                            otherHandle.handleGroup == referenceLaneHandle.handleGroup)
                        {
                            result.Add(otherHandle.laneIndex.x);
                        }
                    }
                }
                return result;
            }

            private void CopySkipLaneHandle(ref NativeList<TempLanePriority> result, ref DynamicBuffer<LanePriority> originalPriorities, int3 laneIndex, NativeHashSet<int> laneIndexInGroup)
            {
                if (laneIndexInGroup.IsEmpty)
                {
                    result.CopyFrom(originalPriorities.AsNativeArray().Reinterpret<TempLanePriority>());
                    CollectionUtils.RemoveValueSwapBack(result, new TempLanePriority()
                    {
                        laneIndex = laneIndex
                    });
                }
                else
                {
                    foreach (TempLanePriority originalPriority in originalPriorities.AsNativeArray().Reinterpret<TempLanePriority>())
                    {
                        if (!laneIndexInGroup.Contains(originalPriority.laneIndex.x))
                        {
                            result.Add(originalPriority);
                        }
                    }
                }
            }

            private void CreateNodeDefinition(Entity node)
            {
                if (nodeData.HasComponent(node))
                {
                    CreationDefinition nodeDef = new CreationDefinition()
                    {
                        m_Flags = 0,
                        m_Original = node,
                        m_Prefab = prefabRefData[node].m_Prefab
                    };

                    float3 pos = nodeData[node].m_Position;
                    ControlPoint point = new ControlPoint(node, new RaycastHit()
                    {
                        m_Position = pos,
                        m_HitEntity = node,
                        m_HitPosition = pos,
                    });

                    NetCourse netCourse = default(NetCourse);
                    netCourse.m_Curve = new Bezier4x3(point.m_Position, point.m_Position, point.m_Position, point.m_Position);
                    netCourse.m_StartPosition = GetCoursePos(netCourse.m_Curve, point, 0f);
                    netCourse.m_StartPosition.m_Flags |= (CoursePosFlags.IsFirst);
                    netCourse.m_StartPosition.m_ParentMesh = -1;
                    netCourse.m_EndPosition = GetCoursePos(netCourse.m_Curve, point, 1f);
                    netCourse.m_EndPosition.m_Flags |= (CoursePosFlags.IsLast);
                    netCourse.m_EndPosition.m_ParentMesh = -1;
                    netCourse.m_Length = MathUtils.Length(netCourse.m_Curve);
                    netCourse.m_FixedIndex = -1;

                    Entity nodeEntity = commandBuffer.CreateEntity();
                    commandBuffer.AddComponent(nodeEntity, nodeDef);
                    commandBuffer.AddComponent(nodeEntity, netCourse);
                    commandBuffer.AddComponent<Updated>(nodeEntity);
                    /*----------------------------------------------*/
                }
            }

            private CoursePos GetCoursePos(Bezier4x3 curve, ControlPoint controlPoint, float delta)
            {
                CoursePos result = default(CoursePos);

                result.m_Entity = controlPoint.m_OriginalEntity;
                result.m_SplitPosition = controlPoint.m_CurvePosition;
                result.m_Position = controlPoint.m_Position;
                result.m_Elevation = controlPoint.m_Elevation;
                result.m_Rotation = NetUtils.GetNodeRotation(MathUtils.Tangent(curve, delta));
                result.m_CourseDelta = delta;
                result.m_ParentMesh = controlPoint.m_ElementIndex.x;
                return result;
            }
        }

        private void AddCustomPriorityToEdge(Entity e, RaycastHit rc)
        {
            bool isNearEnd = IsNearEnd(e, EntityManager.GetComponentData<Curve>(e), rc.m_HitPosition, false);
            if (!EntityManager.HasComponent<CustomPriority>(e))
            {
                CustomPriority priority = isNearEnd ? new CustomPriority() { left = PriorityType.Default, right = PriorityType.Yield } : new CustomPriority() { left = PriorityType.Yield, right = PriorityType.Default };
                EntityManager.AddComponentData<CustomPriority>(e, priority);
                Logger.Info($"Added custom priority to edge: {e} | {priority.left} {priority.right}");
            }
            else
            {
                CustomPriority customPriority = EntityManager.GetComponentData<CustomPriority>(e);
                CustomPriority priority = isNearEnd
                    ? new CustomPriority
                    {
                        left = customPriority.left,
                        right = GetNextPriority(customPriority.right)
                    }
                    : new CustomPriority()
                    {
                        left = GetNextPriority(customPriority.left),
                        right = customPriority.right,
                    };
                EntityManager.SetComponentData(e, priority);
                Logger.Info($"Updated custom priority to edge: {e} | {priority.left} {priority.right}");
            }

            if (!EntityManager.HasComponent<Updated>(e))
            {
                EntityCommandBuffer entityCommandBuffer = _toolOutputBarrier.CreateCommandBuffer();
                entityCommandBuffer.AddComponent<Updated>(e);
                Edge edge = EntityManager.GetComponentData<Edge>(e);
                if (!EntityManager.HasComponent<Updated>(edge.m_Start))
                {
                    entityCommandBuffer.AddComponent<Updated>(edge.m_Start);
                }
                if (!EntityManager.HasComponent<Updated>(edge.m_End))
                {
                    entityCommandBuffer.AddComponent<Updated>(edge.m_End);
                }
            }
        }

        private void RemoveCustomPriorityFromEdge(Entity e, RaycastHit rc)
        {
            if (EntityManager.HasComponent<CustomPriority>(e))
            {
                bool updated = false;
                var priority = EntityManager.GetComponentData<CustomPriority>(e);
                var oldPriority = priority;
                bool isNearEnd = IsNearEnd(e, EntityManager.GetComponentData<Curve>(e), rc.m_HitPosition, false);
                if (isNearEnd)
                {
                    priority.right = PriorityType.Default;
                }
                else
                {
                    priority.left = PriorityType.Default;
                }
                EntityCommandBuffer entityCommandBuffer = _toolOutputBarrier.CreateCommandBuffer();
                if (priority is { left: 0, right: 0 })
                {
                    entityCommandBuffer.RemoveComponent<CustomPriority>(e);
                    updated = true;
                }
                else if (priority.left != oldPriority.left || priority.right != oldPriority.right)
                {
                    entityCommandBuffer.SetComponent(e, priority);
                    updated = true;
                }
                Logger.Info($"Removed custom priority from edge: {e}");
                if (EntityManager.HasBuffer<Game.Net.SubLane>(e))
                {
                    DynamicBuffer<Game.Net.SubLane> sub = EntityManager.GetBuffer<Game.Net.SubLane>(e);
                    for (int i = 0; i < sub.Length; i++)
                    {
                        if (EntityManager.HasComponent<CustomPriority>(sub[i].m_SubLane))
                        {
                            updated = true;
                            entityCommandBuffer.RemoveComponent<CustomPriority>(sub[i].m_SubLane);
                            Logger.Info($"Removed custom priority from sublane {sub[i].m_SubLane}[{i}]");
                        }
                    }
                }

                if (updated)
                {
                    if (!EntityManager.HasComponent<Updated>(e))
                    {
                        entityCommandBuffer.AddComponent<Updated>(e);
                    }
                    Edge edge = EntityManager.GetComponentData<Edge>(e);
                    Logger.Info($"Updating: {e} || {priority.left} {priority.right}");
                    if (!EntityManager.HasComponent<Updated>(edge.m_Start))
                    {
                        entityCommandBuffer.AddComponent<Updated>(edge.m_Start);
                    }
                    if (!EntityManager.HasComponent<Updated>(edge.m_End))
                    {
                        entityCommandBuffer.AddComponent<Updated>(edge.m_End);
                    }
                }
                else
                {
                    Logger.Info($"Nothing changed: {e} | {oldPriority.left} {oldPriority.right} || {priority.left} {priority.right}");
                }
            }
        }

        private void AddTrafficUpgrade(Entity e, RaycastHit rc)
        {
            bool isNearEnd = IsNearEnd(e, EntityManager.GetComponentData<Curve>(e), rc.m_HitPosition, false);
            if (!EntityManager.HasComponent<TrafficUpgrade>(e))
            {
                TrafficUpgrade priority = isNearEnd ? new TrafficUpgrade() { left = UpgradeType.None, right = UpgradeType.NoUturn } : new TrafficUpgrade() { left = UpgradeType.NoUturn, right = UpgradeType.None };
                EntityManager.AddComponentData<TrafficUpgrade>(e, priority);
                Logger.Info($"Added custom traffic upgrade to edge: {e} | {priority.left} {priority.right}");
            }
            else
            {
                TrafficUpgrade trafficUpgrade = EntityManager.GetComponentData<TrafficUpgrade>(e);
                TrafficUpgrade upgrade = isNearEnd
                    ? new TrafficUpgrade
                    {
                        left = trafficUpgrade.left,
                        right = GetNextUpgrade(trafficUpgrade.right)
                    }
                    : new TrafficUpgrade()
                    {
                        left = GetNextUpgrade(trafficUpgrade.left),
                        right = trafficUpgrade.right,
                    };
                EntityManager.SetComponentData(e, upgrade);
                Logger.Info($"Updated custom upgrade to edge: {e} | {upgrade.left} {upgrade.right}");
            }

            if (!EntityManager.HasComponent<Updated>(e))
            {
                EntityCommandBuffer entityCommandBuffer = _toolOutputBarrier.CreateCommandBuffer();
                entityCommandBuffer.AddComponent<Updated>(e);
                Edge edge = EntityManager.GetComponentData<Edge>(e);
                if (!EntityManager.HasComponent<Updated>(edge.m_Start))
                {
                    entityCommandBuffer.AddComponent<Updated>(edge.m_Start);
                }
                if (!EntityManager.HasComponent<Updated>(edge.m_End))
                {
                    entityCommandBuffer.AddComponent<Updated>(edge.m_End);
                }
            }
        }

        private void RemoveTrafficUpgrade(Entity e, RaycastHit rc)
        {
            if (EntityManager.HasComponent<TrafficUpgrade>(e))
            {
                bool updated = false;
                var upgrade = EntityManager.GetComponentData<TrafficUpgrade>(e);
                var oldPriority = upgrade;
                bool isNearEnd = IsNearEnd(e, EntityManager.GetComponentData<Curve>(e), rc.m_HitPosition, false);
                if (isNearEnd)
                {
                    upgrade.right = UpgradeType.None;
                }
                else
                {
                    upgrade.left = UpgradeType.None;
                }
                EntityCommandBuffer entityCommandBuffer = _toolOutputBarrier.CreateCommandBuffer();
                if (upgrade is { left: 0, right: 0 })
                {
                    entityCommandBuffer.RemoveComponent<TrafficUpgrade>(e);
                    updated = true;
                }
                else if (upgrade.left != oldPriority.left || upgrade.right != oldPriority.right)
                {
                    entityCommandBuffer.SetComponent(e, upgrade);
                    updated = true;
                }
                if (updated)
                {
                    if (!EntityManager.HasComponent<Updated>(e))
                    {
                        entityCommandBuffer.AddComponent<Updated>(e);
                    }
                    Edge edge = EntityManager.GetComponentData<Edge>(e);
                    Logger.Info($"Updating: {e} || {upgrade.left} {upgrade.right}");
                    if (!EntityManager.HasComponent<Updated>(edge.m_Start))
                    {
                        entityCommandBuffer.AddComponent<Updated>(edge.m_Start);
                    }
                    if (!EntityManager.HasComponent<Updated>(edge.m_End))
                    {
                        entityCommandBuffer.AddComponent<Updated>(edge.m_End);
                    }
                }
            }
        }

        private PriorityType GetNextPriority(PriorityType customPriority)
        {
            switch (customPriority)
            {
                case PriorityType.Default:
                    return PriorityType.RightOfWay;
                case PriorityType.RightOfWay:
                    return PriorityType.Yield;
                case PriorityType.Yield:
                    return PriorityType.Stop;
                case PriorityType.Stop:
                    return PriorityType.Default;
                default:
                    throw new ArgumentOutOfRangeException(nameof(customPriority), customPriority, null);
            }
        }

        private UpgradeType GetNextUpgrade(UpgradeType type)
        {
            switch (type)
            {
                case UpgradeType.None:
                    return UpgradeType.NoUturn;
                case UpgradeType.NoUturn:
                    return UpgradeType.None;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        private bool IsNearEnd(Entity edge, Curve curve, float3 position, bool invert)
        {
            EdgeGeometry edgeGeometry;
            if (EntityManager.TryGetComponent(edge, out edgeGeometry))
            {
                Bezier4x3 startBezier = MathUtils.Lerp(edgeGeometry.m_Start.m_Left, edgeGeometry.m_Start.m_Right, 0.5f);
                Bezier4x3 endBezier = MathUtils.Lerp(edgeGeometry.m_End.m_Left, edgeGeometry.m_End.m_Right, 0.5f);
                float startBezierT;
                float distanceToStart = MathUtils.Distance(startBezier.xz, position.xz, out startBezierT);
                float endBezierT;
                float distanceToEnd = MathUtils.Distance(endBezier.xz, position.xz, out endBezierT);
                float middleLengthStart = edgeGeometry.m_Start.middleLength;
                float middleLengthEnd = edgeGeometry.m_End.middleLength;
                return math.select(startBezierT * middleLengthStart, middleLengthStart + endBezierT * middleLengthEnd, distanceToEnd < distanceToStart) > (middleLengthStart + middleLengthEnd) * 0.5f != invert;
            }
            float curveBezierT;
            MathUtils.Distance(curve.m_Bezier.xz, position.xz, out curveBezierT);
            return curveBezierT > 0.5f;
        }

        private void CleanupIntersectionHelpers()
        {
            Logger.DebugTool("CleanupIntersectionHelpers!");
            if (!_raycastHelpersQuery.IsEmptyIgnoreFilter)
            {
                Logger.DebugTool($"CleanupIntersectionHelpers! {_raycastHelpersQuery.CalculateEntityCount()}");
                EntityManager.AddComponent<Deleted>(_raycastHelpersQuery);
            }
        }

        internal JobHandle ApplyPreviewedAction(JobHandle inputDeps)
        {
            ActionOverlayData data = SystemAPI.GetSingleton<ActionOverlayData>();
            Logger.DebugTool($"ApplyPreviewedAction: {data.entity}, {data.mode}");
            if (data.mode == ModUISystem.ActionOverlayPreview.ResetToVanilla &&
                data.entity != Entity.Null &&
                data.entity.Equals(_selectedNode) &&
                !EntityManager.HasComponent<Deleted>(_selectedNode) &&
                EntityManager.HasComponent<NodeGeometry>(data.entity))
            {
                return ResetNodePriorities(inputDeps);
            }

            return inputDeps;
        }

        private JobHandle ResetNodePriorities(JobHandle handle)
        {
            Logger.DebugTool($"[Resetting Node Priorities {UnityEngine.Time.frameCount}] at {_selectedNode}");
            applyMode = ApplyMode.Clear;
            _lastControlPoint = default;
            _controlPoints.Clear();

            if (EntityManager.HasBuffer<ConnectedEdge>(_selectedNode))
            {
                _audioManager.PlayUISound(_soundQuery.GetSingleton<ToolUXSoundSettingsData>().m_BulldozeSound);
                JobHandle removePrioritiesHandle = new RemoveNodeLanePrioritiesJob()
                {
                    node = _selectedNode,
                    connectedEdgeData = SystemAPI.GetBufferLookup<ConnectedEdge>(),
                    lanePriorityBuffer = SystemAPI.GetBufferLookup<LanePriority>(),
                    edgeData = SystemAPI.GetComponentLookup<Edge>(true),
                    modifiedPriorityData = SystemAPI.GetComponentLookup<ModifiedPriorities>(true),
                    commandBuffer = _toolOutputBarrier.CreateCommandBuffer(),
                }.Schedule(handle);
                _toolOutputBarrier.AddJobHandleForProducer(removePrioritiesHandle);
                return UpdateDefinitions(removePrioritiesHandle, true);
            }

            return handle;
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

        private void UpdateActionState(bool shouldEnable)
        {
            //vanilla
            _applyAction.shouldBeEnabled = shouldEnable;
            _secondaryApplyAction.shouldBeEnabled = shouldEnable;
            
            //mod
            _toggleDisplayModeAction.shouldBeEnabled = shouldEnable;
            _usePriorityAction.shouldBeEnabled = shouldEnable;
            _useYieldAction.shouldBeEnabled = shouldEnable;
            _useStopAction.shouldBeEnabled = shouldEnable;
            _useResetAction.shouldBeEnabled = shouldEnable;          
        }

        private struct RemoveLanePrioritiesJob : IJobFor
        {
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public ComponentLookup<ModifiedPriorities> modifiedPriorityData;
            [ReadOnly] public NativeArray<Entity> entities;
            public EntityCommandBuffer.ParallelWriter commandBuffer;


            public void Execute(int index)
            {
                Entity entity = entities[index];
                if (modifiedPriorityData.HasComponent(entity))
                {
                    commandBuffer.RemoveComponent<ModifiedPriorities>(index, entity);
                }
                commandBuffer.RemoveComponent<LanePriority>(index, entity);
                Edge e = edgeData[entity];
                // update edge
                commandBuffer.AddComponent<Updated>(index, entity);
                // update nodes
                commandBuffer.AddComponent<Updated>(index, e.m_Start);
                commandBuffer.AddComponent<Updated>(index, e.m_End);
            }
        }

        private struct RemoveNodeLanePrioritiesJob : IJob
        {
            [ReadOnly] public Entity node;
            [ReadOnly] public ComponentLookup<Edge> edgeData;
            [ReadOnly] public BufferLookup<ConnectedEdge> connectedEdgeData;
            [ReadOnly] public BufferLookup<LanePriority> lanePriorityBuffer;
            [ReadOnly] public ComponentLookup<ModifiedPriorities> modifiedPriorityData;
            public EntityCommandBuffer commandBuffer;

            public void Execute()
            {
                bool changed = false;
                DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgeData[node];
                foreach (ConnectedEdge connectedEdge in connectedEdges)
                {
                    Entity edgeEntity = connectedEdge.m_Edge;
                    NativeList<LanePriority> priorities = default;
                    if (lanePriorityBuffer.HasBuffer(edgeEntity))
                    {
                        Edge e = edgeData[edgeEntity];
                        bool isEnd = e.m_End == node;
                        DynamicBuffer<LanePriority> lanePriorities = lanePriorityBuffer[edgeEntity];
                        priorities = new NativeList<LanePriority>(lanePriorities.Length, Allocator.Temp);
                        // collect priorities from other side of edge
                        foreach (LanePriority lanePriority in lanePriorities)
                        {
                            if (isEnd == !lanePriority.isEnd)
                            {
                                priorities.Add(lanePriority);
                            }
                        }

                        // update edge
                        commandBuffer.AddComponent<Updated>(edgeEntity);
                        // update other node
                        commandBuffer.AddComponent<Updated>(isEnd ? e.m_Start : e.m_End);
                    }

                    if (!priorities.IsCreated)
                    {
                        continue;
                    }

                    if (priorities.IsEmpty)
                    {
                        if (modifiedPriorityData.HasComponent(edgeEntity))
                        {
                            commandBuffer.RemoveComponent<ModifiedPriorities>(edgeEntity);
                        }

                        commandBuffer.RemoveComponent<LanePriority>(edgeEntity);
                        changed = true;
                    }
                    else
                    {
                        DynamicBuffer<LanePriority> lanePriorities = commandBuffer.SetBuffer<LanePriority>(edgeEntity);
                        lanePriorities.CopyFrom(priorities.AsArray());
                        changed = true;
                    }
                }

                if (changed)
                {
                    commandBuffer.AddComponent<Updated>(node);
                }
            }
        }
    }
}
