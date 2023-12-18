using System;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Input;
using Game.Net;
using Game.Notifications;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Traffic.Components;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Traffic.Tools
{

    public partial class PriorityToolSystem : ToolBaseSystem
    {
        public override string toolID { get; } = "Priority Tool";

        private ProxyAction _applyAction;
        private ProxyAction _secondaryApplyAction;
        public Entity HoveredEntity;
        public float3 LastPos;
        private ToolOutputBarrier _toolOutputBarrier;
        private int Mode = 0;
        private OverlayRenderSystem _overlayRenderSystem;

        public override PrefabBase GetPrefab() {
            return null;
        }

        public override bool TrySetPrefab(PrefabBase prefab) {
            return false;
        }

        protected override void OnCreate() {
            base.OnCreate();
            _applyAction = InputManager.instance.FindAction("Tool", "Apply");
            _secondaryApplyAction = InputManager.instance.FindAction("Tool", "Secondary Apply");
            _toolOutputBarrier = World.GetExistingSystemManaged<ToolOutputBarrier>();
            _overlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            Enabled = false;
        }

        protected override void OnStartRunning() {
            base.OnStartRunning();
            _applyAction.shouldBeEnabled = true;
            _secondaryApplyAction.shouldBeEnabled = true;
            Mode = 0;
        }

        protected override void OnStopRunning() {
            base.OnStopRunning();
            HoveredEntity = Entity.Null;
            LastPos = float3.zero;
        }

        public void OnKeyPressed(EventModifiers modifiers, KeyCode code) {
            if (modifiers == EventModifiers.Control && code == KeyCode.T)
            {
                if (m_ToolSystem.activeTool != this && m_ToolSystem.activeTool == m_DefaultToolSystem)
                {
                    m_ToolSystem.selected = Entity.Null;
                    m_ToolSystem.activeTool = this;
                }
                else if (m_ToolSystem.activeTool == this)
                {
                    Mode = GetNextMode();
                }
            }
        }

        private int GetNextMode() {
            int current = Mode;
            return current > 1 ? 0 : current + 1;
        }

        public override void InitializeRaycast() {
            base.InitializeRaycast();

            m_ToolRaycastSystem.collisionMask = (CollisionMask.OnGround | CollisionMask.Overground);
            m_ToolRaycastSystem.typeMask = (TypeMask.Net);
            m_ToolRaycastSystem.raycastFlags = RaycastFlags.SubElements | RaycastFlags.Cargo | RaycastFlags.Passenger | RaycastFlags.EditorContainers;
            m_ToolRaycastSystem.netLayerMask = Layer.All;
            m_ToolRaycastSystem.iconLayerMask = IconLayerMask.None;
            m_ToolRaycastSystem.utilityTypeMask = UtilityTypes.None;
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps) {
            if (GetRaycastResult(out Entity e, out RaycastHit rc))
            {
                Entity prev = HoveredEntity;
                HoveredEntity = e;
                LastPos = rc.m_HitPosition;

                var buffer = _overlayRenderSystem.GetBuffer(out JobHandle dependencies);
                var deps = JobHandle.CombineDependencies(inputDeps, dependencies);
                if (Mode == 0)
                {
                    _applyAction.SetDisplayProperties("Set Priority", 20);
                }
                else
                {
                    _applyAction.SetDisplayProperties("Apply Traffic Upgrade", 20);
                }

                bool isEdge = e != Entity.Null && EntityManager.HasComponent<Edge>(e);
                if (isEdge)
                {
                    bool isNearEnd = IsNearEnd(e, EntityManager.GetComponentData<Curve>(e), rc.m_HitPosition, false);
                    Curve c = EntityManager.GetComponentData<Curve>(e);
                    NetGeometryData netGeometryData = EntityManager.GetComponentData<NetGeometryData>(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab);
                    bool isElevated = EntityManager.TryGetComponent(e, out Elevation elevation) && math.all(elevation.m_Elevation > float2.zero);
                    buffer.DrawCurve(new Color(0f, 1f, 1f, 0.65f),
                        new Color(0f, 0.1f, 1f, 0.05f),
                        0.20f,
                        OverlayRenderSystem.StyleFlags.Projected,
                        MathUtils.Cut(c.m_Bezier, !isNearEnd ? new float2(0f, 0.5f) : new float2(0.5f, 1f)),
                        isElevated ? netGeometryData.m_ElevatedWidth : netGeometryData.m_DefaultWidth,
                        new float2(new bool2(!isNearEnd, isNearEnd)));
                }

                if (_applyAction.WasPressedThisFrame() && isEdge)
                {
                    if (Mode == 0)
                    {
                        AddCustomPriorityToEdge(e, rc);
                    }
                    else
                    {
                        AddTrafficUpgrade(e, rc);
                    }
                }

                if (_secondaryApplyAction.WasReleasedThisFrame() && isEdge)
                {
                    if (Mode == 0)
                    {
                        RemoveCustomPriorityFromEdge(e, rc);
                    }
                    else
                    {
                        RemoveTrafficUpgrade(e, rc);
                    }
                }
                return deps;
            }

            return inputDeps;
        }

        private void AddCustomPriorityToEdge(Entity e, RaycastHit rc) {
            bool isNearEnd = IsNearEnd(e, EntityManager.GetComponentData<Curve>(e), rc.m_HitPosition, false);
            if (!EntityManager.HasComponent<CustomPriority>(e))
            {
                CustomPriority priority = isNearEnd ? new CustomPriority() { left = PriorityType.None, right = PriorityType.Yield } : new CustomPriority() { left = PriorityType.Yield, right = PriorityType.None };
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

        private void RemoveCustomPriorityFromEdge(Entity e, RaycastHit rc) {
            if (EntityManager.HasComponent<CustomPriority>(e))
            {
                bool updated = false;
                var priority = EntityManager.GetComponentData<CustomPriority>(e);
                var oldPriority = priority;
                bool isNearEnd = IsNearEnd(e, EntityManager.GetComponentData<Curve>(e), rc.m_HitPosition, false);
                if (isNearEnd)
                {
                    priority.right = PriorityType.None;
                }
                else
                {
                    priority.left = PriorityType.None;
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

        private void AddTrafficUpgrade(Entity e, RaycastHit rc) {
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

        private void RemoveTrafficUpgrade(Entity e, RaycastHit rc) {
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

        private PriorityType GetNextPriority(PriorityType customPriority) {
            switch (customPriority)
            {
                case PriorityType.None:
                    return PriorityType.RightOfWay;
                case PriorityType.RightOfWay:
                    return PriorityType.Yield;
                case PriorityType.Yield:
                    return PriorityType.Stop;
                case PriorityType.Stop:
                    return PriorityType.None;
                default:
                    throw new ArgumentOutOfRangeException(nameof(customPriority), customPriority, null);
            }
        }

        private UpgradeType GetNextUpgrade(UpgradeType type) {
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

        private bool IsNearEnd(Entity edge, Curve curve, float3 position, bool invert) {
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

        private string HitToString(RaycastHit hit) {
            return
                $"m_HitEntity: {hit.m_HitEntity} m_Position: {hit.m_Position} m_HitPosition: {hit.m_HitPosition} m_HitDirection: {hit.m_HitDirection} m_CellIndex: {hit.m_CellIndex} m_NormalizedDistance: {hit.m_NormalizedDistance} m_CurvePosition: {hit.m_CurvePosition} ";
        }
    }
}