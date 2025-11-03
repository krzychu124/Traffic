using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Colossal.Collections;
using Colossal.Mathematics;
using Game;
using Game.Areas;
using Game.Buildings;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
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
using Elevation = Game.Net.Elevation;
using SubLane = Game.Net.SubLane;
using Edge = Game.Net.Edge;
using Node = Game.Net.Node;
using CarLane = Game.Net.CarLane;
using EditorContainer = Game.Tools.EditorContainer;
using UtilityLane = Game.Net.UtilityLane;
using TrackLane = Game.Net.TrackLane;
using PedestrianLane = Game.Net.PedestrianLane;
using SecondaryLane = Game.Net.SecondaryLane;
using GeometryFlags = Game.Net.GeometryFlags;
using OutsideConnection = Game.Net.OutsideConnection;
using Transform = Game.Objects.Transform;

// ReSharper disable ForCanBeConvertedToForeach
// ReSharper disable ArrangeObjectCreationWhenTypeEvident
// ReSharper disable InconsistentNaming
// ReSharper disable RedundantCast
// ReSharper disable ArrangeDefaultValueWhenTypeEvident

namespace Traffic.Systems
{
#if WITH_BURST
    [Unity.Burst.BurstCompile]
#endif
    public partial class TrafficLaneSystem : GameSystemBase
    {
        #region InternalTypes
        
        private struct LaneKey : IEquatable<LaneKey>
        {
            private Lane m_Lane;
            private Entity m_Prefab;
            private LaneFlags m_Flags;

            public LaneKey(Lane lane, Entity prefab, LaneFlags flags)
            {
                m_Lane = lane;
                m_Prefab = prefab;
                m_Flags = (flags & (LaneFlags.Slave | LaneFlags.Master));
            }

            public void ReplaceOwner(Entity oldOwner, Entity newOwner)
            {
                m_Lane.m_StartNode.ReplaceOwner(oldOwner, newOwner);
                m_Lane.m_MiddleNode.ReplaceOwner(oldOwner, newOwner);
                m_Lane.m_EndNode.ReplaceOwner(oldOwner, newOwner);
            }

            public bool Equals(LaneKey other)
            {
                if (m_Lane.Equals(other.m_Lane) && m_Prefab.Equals(other.m_Prefab))
                {
                    return m_Flags == other.m_Flags;
                }
                return false;
            }

            public override int GetHashCode()
            {
                return m_Lane.GetHashCode();
            }
        }

        private struct ConnectionKey : IEquatable<ConnectionKey>
        {
            private int4 m_Data;

            public ConnectionKey(ConnectPosition sourcePosition, ConnectPosition targetPosition)
            {
                m_Data.x = sourcePosition.m_Owner.Index;
                m_Data.y = sourcePosition.m_LaneData.m_Index;
                m_Data.z = targetPosition.m_Owner.Index;
                m_Data.w = targetPosition.m_LaneData.m_Index;
            }
            
            /*NON-STOCK*/
            public ConnectionKey(int sourceEntityIdx, byte sourceGroup, int targetEntityIdx, byte targetGroup) {
                m_Data.x = sourceEntityIdx;
                m_Data.y = sourceGroup;
                m_Data.z = targetEntityIdx;
                m_Data.w = targetGroup;
            }
            /*NON-STOCK*/
            
            public bool Equals(ConnectionKey other)
            {
                return m_Data.Equals(other.m_Data);
            }

            public override int GetHashCode()
            {
                return m_Data.GetHashCode();
            }

            /*NON VANILLA - START*/
            public string GetString() {
                return $"ConnectionKey=(e: {m_Data.x}, l: {m_Data.y} -> e: {m_Data.z}, l: {m_Data.w})";
            }
            /*NON VANILLA - END*/
        }

        /*NON VANILLA - START*/
        private struct LaneEndKey : IEquatable<LaneEndKey>
        {
            private int2 _data;

            public LaneEndKey(Entity owner, int idx) {
                _data = new int2(owner.Index, idx);
            }

            public bool MatchingOwner(Entity owner) {
                return _data.x == owner.Index;
            }
            
            public bool Equals(LaneEndKey other) {
                return _data.Equals(other._data);
            }
            
            public override int GetHashCode()
            {
                return _data.GetHashCode();
            }
        }
        /*NON VANILLA - END*/

        private struct ConnectPosition
        {
            public NetCompositionLane m_LaneData;
            public Entity m_Owner;
            public Entity m_NodeComposition;
            public Entity m_EdgeComposition;
            public float3 m_Position;
            public float3 m_Tangent;
            public float m_Order;
            public CompositionData m_CompositionData;
            public float m_CurvePosition;
            public float m_BaseHeight;
            public float m_Elevation;
            public ushort m_GroupIndex;
            public byte m_SegmentIndex;
            public byte m_UnsafeCount;
            public byte m_ForbiddenCount;
            public byte m_SkippedCount;
            public RoadTypes m_RoadTypes;
            public TrackTypes m_TrackTypes;
            public UtilityTypes m_UtilityTypes;
            public bool m_IsEnd;
            public bool m_IsSideConnection;
        }

        private struct EdgeTarget
        {
            public Entity m_Edge;
            public Entity m_StartNode;
            public Entity m_EndNode;
            public float3 m_StartPos;
            public float3 m_StartTangent;
            public float3 m_EndPos;
            public float3 m_EndTangent;
        }

        private struct MiddleConnection
        {
            public ConnectPosition m_ConnectPosition;
            public Entity m_SourceEdge;
            public Entity m_SourceNode;
            public Curve m_TargetCurve;
            public float m_TargetCurvePos;
            public float m_Distance;
            public CompositionData m_TargetComposition;
            public Entity m_TargetLane;
            public Entity m_TargetOwner;
            public LaneFlags m_TargetFlags;
            public uint m_TargetGroup;
            public int m_SortIndex;
            public PathNode m_TargetNode;
            public ushort m_TargetCarriageway;
            public bool m_IsSource;
        }

        [StructLayout(LayoutKind.Sequential, Size = 1)]
        private struct SourcePositionComparer : IComparer<ConnectPosition>
        {
            public int Compare(ConnectPosition x, ConnectPosition y)
            {
                int num = x.m_Owner.Index - y.m_Owner.Index;
                return math.select((int)math.sign(x.m_Order - y.m_Order), num, num != 0);
            }
        }

        [StructLayout(LayoutKind.Sequential, Size = 1)]
        private struct TargetPositionComparer : IComparer<ConnectPosition>
        {
            public int Compare(ConnectPosition x, ConnectPosition y)
            {
                return (int)math.sign(x.m_Order - y.m_Order);
            }
        }

        [StructLayout(LayoutKind.Sequential, Size = 1)]
        private struct MiddleConnectionComparer : IComparer<MiddleConnection>
        {
            public int Compare(MiddleConnection x, MiddleConnection y)
            {
                return math.select(x.m_SortIndex - y.m_SortIndex, x.m_ConnectPosition.m_UtilityTypes - y.m_ConnectPosition.m_UtilityTypes, x.m_ConnectPosition.m_UtilityTypes != y.m_ConnectPosition.m_UtilityTypes);
            }
        }

        private struct LaneAnchor : IComparable<LaneAnchor>
        {
            public Entity m_Prefab;
            public float m_Order;
            public float3 m_Position;
            public PathNode m_PathNode;

            public int CompareTo(LaneAnchor other)
            {
                // ReSharper disable once CompareOfFloatsByEqualityOperator
                return math.select(math.select(0, math.select(1, -1, m_Order < other.m_Order), m_Order != other.m_Order), m_Prefab.Index - other.m_Prefab.Index, m_Prefab.Index != other.m_Prefab.Index);
            }
        }

        private struct LaneBuffer
        {
            public NativeHashMap<LaneKey, Entity> m_OldLanes;
            public NativeHashMap<LaneKey, Entity> m_OriginalLanes;
            public NativeHashMap<Entity, Unity.Mathematics.Random> m_SelectedSpawnables;
            public NativeList<Entity> m_Updates;

            public LaneBuffer(Allocator allocator)
            {
                m_OldLanes = new NativeHashMap<LaneKey, Entity>(32, allocator);
                m_OriginalLanes = new NativeHashMap<LaneKey, Entity>(32, allocator);
                m_SelectedSpawnables = new NativeHashMap<Entity, Unity.Mathematics.Random>(10, allocator);
                m_Updates = new NativeList<Entity>(32, allocator);
            }

            public void Clear()
            {
                m_OldLanes.Clear();
                m_OriginalLanes.Clear();
                m_SelectedSpawnables.Clear();
            }

            public void Dispose()
            {
                m_OldLanes.Dispose();
                m_OriginalLanes.Dispose();
                m_SelectedSpawnables.Dispose();
                m_Updates.Dispose();
            }
        }

        private struct CompositionData
        {
            public float m_SpeedLimit;
            public float m_Priority;
            public TaxiwayFlags m_TaxiwayFlags;
            public Game.Prefabs.RoadFlags m_RoadFlags;
        }

        #endregion    
    
        private CityConfigurationSystem m_CityConfigurationSystem;
        private ToolSystem m_ToolSystem;
        private TerrainSystem m_TerrainSystem;
        private ModificationBarrier4 m_ModificationBarrier;
        private LaneReferencesSystem m_LaneReferencesSystem;
        private EntityQuery m_OwnerQuery;
        private EntityQuery m_BuildingSettingsQuery;
        private ComponentTypeSet m_AppliedTypes;
        private ComponentTypeSet m_DeletedTempTypes;
        private ComponentTypeSet m_TempOwnerTypes;
        private ComponentTypeSet m_HideLaneTypes;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CityConfigurationSystem = base.World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_ToolSystem = base.World.GetOrCreateSystemManaged<ToolSystem>();
            m_LaneReferencesSystem = base.World.GetOrCreateSystemManaged<LaneReferencesSystem>();
            m_TerrainSystem = base.World.GetOrCreateSystemManaged<TerrainSystem>();
            m_ModificationBarrier = base.World.GetOrCreateSystemManaged<ModificationBarrier4>();
            m_OwnerQuery = GetEntityQuery(new EntityQueryDesc[]
            {
                new EntityQueryDesc
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<SubLane>(),
                        // ComponentType.ReadOnly<ModifiedLaneConnections>(),
                    },
                    Any = new ComponentType[]
                    {
                        ComponentType.ReadOnly<Updated>(),
                        ComponentType.ReadOnly<Deleted>()
                    },
                    None = new ComponentType[]
                    {
                        ComponentType.ReadOnly<OutsideConnection>(),
                        ComponentType.ReadOnly<Game.Objects.OutsideConnection>(),
                        ComponentType.ReadOnly<Area>()
                    }
                }
            });
            
            m_BuildingSettingsQuery = GetEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());
            m_AppliedTypes = new ComponentTypeSet(ComponentType.ReadWrite<Applied>(), ComponentType.ReadWrite<Created>(), ComponentType.ReadWrite<Updated>());
            m_DeletedTempTypes = new ComponentTypeSet(ComponentType.ReadWrite<Deleted>(), ComponentType.ReadWrite<Temp>());
            m_TempOwnerTypes = new ComponentTypeSet(ComponentType.ReadWrite<Temp>(), ComponentType.ReadWrite<Owner>());
            m_HideLaneTypes = new ComponentTypeSet(ComponentType.ReadWrite<CullingInfo>(), ComponentType.ReadWrite<MeshBatch>(), ComponentType.ReadWrite<MeshColor>());
            RequireForUpdate(m_OwnerQuery);
        }

        protected override void OnUpdate() {
            CustomUpdateLanesJob jobData = new CustomUpdateLanesJob
            {
                m_EntityType = SystemAPI.GetEntityTypeHandle(),
                m_EdgeType = SystemAPI.GetComponentTypeHandle<Edge>(true),
                m_EdgeGeometryType = SystemAPI.GetComponentTypeHandle<EdgeGeometry>(true),
                m_NodeGeometryType = SystemAPI.GetComponentTypeHandle<NodeGeometry>(true),
                m_CurveType = SystemAPI.GetComponentTypeHandle<Curve>(true),
                m_CompositionType = SystemAPI.GetComponentTypeHandle<Composition>(true),
                m_DeletedType = SystemAPI.GetComponentTypeHandle<Deleted>(true),
                m_OwnerType = SystemAPI.GetComponentTypeHandle<Owner>(true),
                m_OrphanType = SystemAPI.GetComponentTypeHandle<Orphan>(true),
                m_PseudoRandomSeedType = SystemAPI.GetComponentTypeHandle<PseudoRandomSeed>(true),
                m_DestroyedType = SystemAPI.GetComponentTypeHandle<Destroyed>(true),
                m_EditorContainerType = SystemAPI.GetComponentTypeHandle<EditorContainer>(true),
                m_TransformType = SystemAPI.GetComponentTypeHandle<Transform>(true),
                m_ElevationType = SystemAPI.GetComponentTypeHandle<Game.Objects.Elevation>(true),
                m_UnderConstructionType = SystemAPI.GetComponentTypeHandle<UnderConstruction>(true),
                m_TempType = SystemAPI.GetComponentTypeHandle<Temp>(true),
                m_PrefabRefType = SystemAPI.GetComponentTypeHandle<PrefabRef>(true),
                m_SubLaneType = SystemAPI.GetBufferTypeHandle<SubLane>(true),
                m_ConnectedNodeType = SystemAPI.GetBufferTypeHandle<ConnectedNode>(true),
                m_EdgeGeometryData = SystemAPI.GetComponentLookup<EdgeGeometry>(true),
                m_StartNodeGeometryData = SystemAPI.GetComponentLookup<StartNodeGeometry>(true),
                m_EndNodeGeometryData = SystemAPI.GetComponentLookup<EndNodeGeometry>(true),
                m_NodeGeometryData = SystemAPI.GetComponentLookup<NodeGeometry>(true),
                m_NodeData = SystemAPI.GetComponentLookup<Node>(true),
                m_EdgeData = SystemAPI.GetComponentLookup<Edge>(true),
                m_CurveData = SystemAPI.GetComponentLookup<Curve>(true),
                m_ElevationData = SystemAPI.GetComponentLookup<Game.Net.Elevation>(true),
                m_CompositionData = SystemAPI.GetComponentLookup<Composition>(true),
                m_LaneData = SystemAPI.GetComponentLookup<Lane>(true),
                m_EdgeLaneData = SystemAPI.GetComponentLookup<EdgeLane>(true),
                m_MasterLaneData = SystemAPI.GetComponentLookup<MasterLane>(true),
                m_SlaveLaneData = SystemAPI.GetComponentLookup<SlaveLane>(true),
                m_SecondaryLaneData = SystemAPI.GetComponentLookup<SecondaryLane>(true),
                m_LaneSignalData = SystemAPI.GetComponentLookup<LaneSignal>(true),
                m_BuildOrderData = SystemAPI.GetComponentLookup<BuildOrder>(true),
                m_UpdatedData = SystemAPI.GetComponentLookup<Updated>(true),
                m_OwnerData = SystemAPI.GetComponentLookup<Owner>(true),
                m_OverriddenData = SystemAPI.GetComponentLookup<Overridden>(true),
                m_PseudoRandomSeedData = SystemAPI.GetComponentLookup<PseudoRandomSeed>(true),
                m_TransformData = SystemAPI.GetComponentLookup<Transform>(true),
                m_AreaClearData = SystemAPI.GetComponentLookup<Clear>(true),
                m_TempData = SystemAPI.GetComponentLookup<Temp>(true),
                m_HiddenData = SystemAPI.GetComponentLookup<Hidden>(true),
                m_PrefabRefData = SystemAPI.GetComponentLookup<PrefabRef>(true),
                m_PrefabNetData = SystemAPI.GetComponentLookup<NetData>(true),
                m_PrefabGeometryData = SystemAPI.GetComponentLookup<NetGeometryData>(true),
                m_PrefabCompositionData = SystemAPI.GetComponentLookup<NetCompositionData>(true),
                m_RoadData = SystemAPI.GetComponentLookup<RoadComposition>(true),
                m_TrackData = SystemAPI.GetComponentLookup<TrackComposition>(true),
                m_WaterwayData = SystemAPI.GetComponentLookup<WaterwayComposition>(true),
                m_PathwayData = SystemAPI.GetComponentLookup<PathwayComposition>(true),
                m_TaxiwayData = SystemAPI.GetComponentLookup<TaxiwayComposition>(true),
                m_PrefabLaneArchetypeData = SystemAPI.GetComponentLookup<NetLaneArchetypeData>(true),
                m_NetLaneData = SystemAPI.GetComponentLookup<NetLaneData>(true),
                m_CarLaneData = SystemAPI.GetComponentLookup<CarLaneData>(true),
                m_PedestrianLaneData = SystemAPI.GetComponentLookup<PedestrianLaneData>(true),
                m_ParkingLaneData = SystemAPI.GetComponentLookup<ParkingLaneData>(true),
                m_TrackLaneData = SystemAPI.GetComponentLookup<TrackLaneData>(true),
                m_UtilityLaneData = SystemAPI.GetComponentLookup<UtilityLaneData>(true),
                m_PrefabSpawnableObjectData = SystemAPI.GetComponentLookup<SpawnableObjectData>(true),
                m_PrefabObjectGeometryData = SystemAPI.GetComponentLookup<ObjectGeometryData>(true),
                m_PrefabBuildingData = SystemAPI.GetComponentLookup<BuildingData>(true),
                m_PrefabNetObjectData = SystemAPI.GetComponentLookup<NetObjectData>(true),
                m_PrefabData = SystemAPI.GetComponentLookup<PrefabData>(true),
                m_Edges = SystemAPI.GetBufferLookup<ConnectedEdge>(true),
                m_Nodes = SystemAPI.GetBufferLookup<ConnectedNode>(true),
                m_SubLanes = SystemAPI.GetBufferLookup<SubLane>(true),
                m_CutRanges = SystemAPI.GetBufferLookup<CutRange>(true),
                m_SubAreas = SystemAPI.GetBufferLookup<Game.Areas.SubArea>(true),
                m_AreaNodes = SystemAPI.GetBufferLookup<Game.Areas.Node>(true),
                m_AreaTriangles = SystemAPI.GetBufferLookup<Triangle>(true),
                m_SubObjects = SystemAPI.GetBufferLookup<Game.Objects.SubObject>(true),
                m_InstalledUpgrades = SystemAPI.GetBufferLookup<InstalledUpgrade>(true),
                m_PrefabCompositionLanes = SystemAPI.GetBufferLookup<NetCompositionLane>(true),
                m_PrefabCompositionCrosswalks = SystemAPI.GetBufferLookup<NetCompositionCrosswalk>(true),
                m_DefaultNetLanes = SystemAPI.GetBufferLookup<DefaultNetLane>(true),
                m_PrefabSubLanes = SystemAPI.GetBufferLookup<Game.Prefabs.SubLane>(true),
                m_PlaceholderObjects = SystemAPI.GetBufferLookup<PlaceholderObjectElement>(true),
                m_ObjectRequirements = SystemAPI.GetBufferLookup<ObjectRequirementElement>(true),
                m_PrefabAuxiliaryLanes = SystemAPI.GetBufferLookup<AuxiliaryNetLane>(true),
                m_PrefabCompositionPieces = SystemAPI.GetBufferLookup<NetCompositionPiece>(true),
                
                /*NON VANILLA - START*/
                modifiedLaneConnectionsType = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                generatedConnections = SystemAPI.GetBufferLookup<GeneratedConnection>(true),
                dataTemps = SystemAPI.GetComponentLookup<DataTemp>(true),
                lanePriorities = SystemAPI.GetBufferLookup<LanePriority>(true),
                /*NON VANILLA - END*/

                m_LeftHandTraffic = m_CityConfigurationSystem.leftHandTraffic,
                m_EditorMode = m_ToolSystem.actionMode.IsEditor(),
                m_RandomSeed = RandomSeed.Next(),
                m_DefaultTheme = m_CityConfigurationSystem.defaultTheme,
                m_AppliedTypes = m_AppliedTypes,
                m_DeletedTempTypes = m_DeletedTempTypes,
                m_TempOwnerTypes = m_TempOwnerTypes,
                m_HideLaneTypes = m_HideLaneTypes,
                m_TerrainHeightData = m_TerrainSystem.GetHeightData(),
                m_BuildingConfigurationData = m_BuildingSettingsQuery.GetSingleton<BuildingConfigurationData>(),
                m_SkipLaneQueue = m_LaneReferencesSystem.GetSkipLaneQueue().AsParallelWriter(),
                m_CommandBuffer = m_ModificationBarrier.CreateCommandBuffer().AsParallelWriter(),
            };
#if DEBUG_LANE_SYS
            JobHandle jobHandle = jobData.Schedule(m_OwnerQuery, Dependency);
#else
            JobHandle jobHandle = jobData.ScheduleParallel(m_OwnerQuery, Dependency);
#endif
            
            m_TerrainSystem.AddCPUHeightReader(jobHandle);
            m_LaneReferencesSystem.AddSkipLaneWriter(jobHandle);
            m_ModificationBarrier.AddJobHandleForProducer(jobHandle);
            Dependency = jobHandle;
        }

#if WITH_BURST
        [Unity.Burst.BurstCompile]
#endif
        private struct CustomUpdateLanesJob : IJobChunk
        {
#region Fields
            [ReadOnly]
            public EntityTypeHandle m_EntityType;
            
            [ReadOnly]
            public ComponentTypeHandle<Edge> m_EdgeType;
            
            [ReadOnly]
            public ComponentTypeHandle<EdgeGeometry> m_EdgeGeometryType;
            
            [ReadOnly]
            public ComponentTypeHandle<NodeGeometry> m_NodeGeometryType;
            
            [ReadOnly]
            public ComponentTypeHandle<Curve> m_CurveType;
            
            [ReadOnly]
            public ComponentTypeHandle<Composition> m_CompositionType;
            
            [ReadOnly]
            public ComponentTypeHandle<Deleted> m_DeletedType;
            
            [ReadOnly]
            public ComponentTypeHandle<Owner> m_OwnerType;
            
            [ReadOnly]
            public ComponentTypeHandle<Orphan> m_OrphanType;
            
            [ReadOnly]
            public ComponentTypeHandle<PseudoRandomSeed> m_PseudoRandomSeedType;
            
            [ReadOnly]
            public ComponentTypeHandle<Destroyed> m_DestroyedType;
            
            [ReadOnly]
            public ComponentTypeHandle<Game.Tools.EditorContainer> m_EditorContainerType;
            
            [ReadOnly]
            public ComponentTypeHandle<Game.Objects.Transform> m_TransformType;
            
            [ReadOnly]
            public ComponentTypeHandle<Game.Objects.Elevation> m_ElevationType;
            
            [ReadOnly]
            public ComponentTypeHandle<UnderConstruction> m_UnderConstructionType;
            
            [ReadOnly]
            public ComponentTypeHandle<Temp> m_TempType;
            
            [ReadOnly]
            public ComponentTypeHandle<PrefabRef> m_PrefabRefType;
            
            [ReadOnly]
            public BufferTypeHandle<SubLane> m_SubLaneType;
            
            [ReadOnly]
            public BufferTypeHandle<ConnectedNode> m_ConnectedNodeType;
            
            [ReadOnly]
            public ComponentLookup<EdgeGeometry> m_EdgeGeometryData;
            
            [ReadOnly]
            public ComponentLookup<StartNodeGeometry> m_StartNodeGeometryData;
            
            [ReadOnly]
            public ComponentLookup<EndNodeGeometry> m_EndNodeGeometryData;
            
            [ReadOnly]
            public ComponentLookup<NodeGeometry> m_NodeGeometryData;
            
            [ReadOnly]
            public ComponentLookup<Node> m_NodeData;
            
            [ReadOnly]
            public ComponentLookup<Edge> m_EdgeData;
            
            [ReadOnly]
            public ComponentLookup<Curve> m_CurveData;
            
            [ReadOnly]
            public ComponentLookup<Elevation> m_ElevationData;
            
            [ReadOnly]
            public ComponentLookup<Composition> m_CompositionData;
            
            [ReadOnly]
            public ComponentLookup<Lane> m_LaneData;
            
            [ReadOnly]
            public ComponentLookup<EdgeLane> m_EdgeLaneData;
            
            [ReadOnly]
            public ComponentLookup<MasterLane> m_MasterLaneData;
            
            [ReadOnly]
            public ComponentLookup<SlaveLane> m_SlaveLaneData;
            
            [ReadOnly]
            public ComponentLookup<SecondaryLane> m_SecondaryLaneData;
            
            [ReadOnly]
            public ComponentLookup<LaneSignal> m_LaneSignalData;

            [ReadOnly]
            public ComponentLookup<BuildOrder> m_BuildOrderData;

            [ReadOnly]
            public ComponentLookup<Updated> m_UpdatedData;
            
            [ReadOnly]
            public ComponentLookup<Owner> m_OwnerData;
            
            [ReadOnly]
            public ComponentLookup<Overridden> m_OverriddenData;
            
            [ReadOnly]
            public ComponentLookup<PseudoRandomSeed> m_PseudoRandomSeedData;

            [ReadOnly]
            public ComponentLookup<Game.Objects.Transform> m_TransformData;
            
            [ReadOnly]
            public ComponentLookup<Clear> m_AreaClearData;
            
            [ReadOnly]
            public ComponentLookup<Temp> m_TempData;
            
            [ReadOnly]
            public ComponentLookup<Hidden> m_HiddenData;
            
            [ReadOnly]
            public ComponentLookup<PrefabRef> m_PrefabRefData;
            
            [ReadOnly]
            public ComponentLookup<NetData> m_PrefabNetData;
            
            [ReadOnly]
            public ComponentLookup<NetGeometryData> m_PrefabGeometryData;
            
            [ReadOnly]
            public ComponentLookup<NetCompositionData> m_PrefabCompositionData;
            
            [ReadOnly]
            public ComponentLookup<RoadComposition> m_RoadData;
            
            [ReadOnly]
            public ComponentLookup<TrackComposition> m_TrackData;
            
            [ReadOnly]
            public ComponentLookup<WaterwayComposition> m_WaterwayData;
            
            [ReadOnly]
            public ComponentLookup<PathwayComposition> m_PathwayData;
            
            [ReadOnly]
            public ComponentLookup<TaxiwayComposition> m_TaxiwayData;
            
            [ReadOnly]
            public ComponentLookup<NetLaneArchetypeData> m_PrefabLaneArchetypeData;
            
            [ReadOnly]
            public ComponentLookup<NetLaneData> m_NetLaneData;
            
            [ReadOnly]
            public ComponentLookup<CarLaneData> m_CarLaneData;

            [ReadOnly]
            public ComponentLookup<PedestrianLaneData> m_PedestrianLaneData;

            [ReadOnly]
            public ComponentLookup<ParkingLaneData> m_ParkingLaneData;

            [ReadOnly]
            public ComponentLookup<TrackLaneData> m_TrackLaneData;
            
            [ReadOnly]
            public ComponentLookup<UtilityLaneData> m_UtilityLaneData;
            
            [ReadOnly]
            public ComponentLookup<SpawnableObjectData> m_PrefabSpawnableObjectData;
            
            [ReadOnly]
            public ComponentLookup<ObjectGeometryData> m_PrefabObjectGeometryData;
            
            [ReadOnly]
            public ComponentLookup<BuildingData> m_PrefabBuildingData;

            [ReadOnly]
            public ComponentLookup<NetObjectData> m_PrefabNetObjectData;
            
            [ReadOnly]
            public ComponentLookup<PrefabData> m_PrefabData;
            
            [ReadOnly]
            public BufferLookup<ConnectedEdge> m_Edges;
            
            [ReadOnly]
            public BufferLookup<ConnectedNode> m_Nodes;
            
            [ReadOnly]
            public BufferLookup<SubLane> m_SubLanes;
            
            [ReadOnly]
            public BufferLookup<CutRange> m_CutRanges;
            
            [ReadOnly]
            public BufferLookup<Game.Objects.SubObject> m_SubObjects;
            
            [ReadOnly]
            public BufferLookup<InstalledUpgrade> m_InstalledUpgrades;
            
            [ReadOnly]
            public BufferLookup<Game.Areas.SubArea> m_SubAreas;
            
            [ReadOnly]
            public BufferLookup<Game.Areas.Node> m_AreaNodes;
            
            [ReadOnly]
            public BufferLookup<Triangle> m_AreaTriangles;
            
            [ReadOnly]
            public BufferLookup<NetCompositionLane> m_PrefabCompositionLanes;
            
            [ReadOnly]
            public BufferLookup<NetCompositionCrosswalk> m_PrefabCompositionCrosswalks;
            
            [ReadOnly]
            public BufferLookup<DefaultNetLane> m_DefaultNetLanes;
            
            [ReadOnly]
            public BufferLookup<Game.Prefabs.SubLane> m_PrefabSubLanes;
            
            [ReadOnly]
            public BufferLookup<PlaceholderObjectElement> m_PlaceholderObjects;
            
            [ReadOnly]
            public BufferLookup<ObjectRequirementElement> m_ObjectRequirements;
            
            [ReadOnly]
            public BufferLookup<AuxiliaryNetLane> m_PrefabAuxiliaryLanes;
            
            [ReadOnly]
            public BufferLookup<NetCompositionPiece> m_PrefabCompositionPieces;
            /*NON VANILLA - START*/
            [ReadOnly]
            public BufferTypeHandle<ModifiedLaneConnections> modifiedLaneConnectionsType;
            [ReadOnly]
            public BufferLookup<GeneratedConnection> generatedConnections;
            [ReadOnly]
            public ComponentLookup<DataTemp> dataTemps;
            [ReadOnly]
            public BufferLookup<LanePriority> lanePriorities;
            /*NON VANILLA - END*/
            
            [ReadOnly]
            public bool m_LeftHandTraffic;
            
            [ReadOnly]
            public bool m_EditorMode;
            
            [ReadOnly]
            public RandomSeed m_RandomSeed;
            
            [ReadOnly]
            public Entity m_DefaultTheme;
            
            [ReadOnly]
            public ComponentTypeSet m_AppliedTypes;
            
            [ReadOnly]
            public ComponentTypeSet m_DeletedTempTypes;

            [ReadOnly]
            public ComponentTypeSet m_TempOwnerTypes;
            
            [ReadOnly]
            public ComponentTypeSet m_HideLaneTypes;

            [ReadOnly]
            public TerrainHeightData m_TerrainHeightData;
            
            [ReadOnly]
            public BuildingConfigurationData m_BuildingConfigurationData;

            public NativeQueue<Lane>.ParallelWriter m_SkipLaneQueue;

            public EntityCommandBuffer.ParallelWriter m_CommandBuffer;
            #endregion

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
                if (chunk.Has(ref m_DeletedType))
                {
                    DeleteLanes(chunk, unfilteredChunkIndex);
                    return;
                }
                UpdateLanes(chunk, unfilteredChunkIndex);
            }

            private void DeleteLanes(ArchetypeChunk chunk, int chunkIndex) {
                BufferAccessor<SubLane> bufferAccessor = chunk.GetBufferAccessor(ref m_SubLaneType);
                for (int i = 0; i < bufferAccessor.Length; i++)
                {
                    DynamicBuffer<SubLane> dynamicBuffer = bufferAccessor[i];
                    for (int j = 0; j < dynamicBuffer.Length; j++)
                    {
                        Entity subLane = dynamicBuffer[j].m_SubLane;
                        if (!m_SecondaryLaneData.HasComponent(subLane))
                        {
                            m_CommandBuffer.AddComponent(chunkIndex, subLane, default(Deleted));
                        }
                    }
                }
#if DEBUG_LANE_SYS
                // if (chunk.Has<Edge>() || chunk.Has<Node>())
                // {
                //     NativeArray<ComponentType> componentTypes = chunk.Archetype.GetComponentTypes();
                //     Logger.DebugLaneSystem($"Deleting: {string.Join(", ", componentTypes.Select(c => c.GetManagedType().Name))}");
                //     componentTypes.Dispose();
                // }
#endif
            }

            private void UpdateLanes(ArchetypeChunk chunk, int chunkIndex) {
                LaneBuffer laneBuffer = new LaneBuffer(Allocator.Temp);
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                NativeArray<Edge> edges = chunk.GetNativeArray(ref m_EdgeType);
                NativeArray<PseudoRandomSeed> randomSeeds = chunk.GetNativeArray(ref m_PseudoRandomSeedType);
                NativeArray<Game.Objects.Transform> transforms = chunk.GetNativeArray(ref m_TransformType);
                NativeArray<Temp> tempComponents = chunk.GetNativeArray(ref m_TempType);
                BufferAccessor<SubLane> subLanesBuffers = chunk.GetBufferAccessor(ref m_SubLaneType);
                if (edges.Length != 0)
                {
                    Logger.DebugLaneSystem($"Has Edges {edges.Length}");
                    NativeList<ConnectPosition> nativeList = new NativeList<ConnectPosition>(32, Allocator.Temp);
                    NativeList<ConnectPosition> nativeList2 = new NativeList<ConnectPosition>(32, Allocator.Temp);
                    NativeList<ConnectPosition> tempBuffer = new NativeList<ConnectPosition>(32, Allocator.Temp);
                    NativeList<ConnectPosition> tempBuffer2 = new NativeList<ConnectPosition>(32, Allocator.Temp);
                    NativeList<EdgeTarget> edgeTargets = new NativeList<EdgeTarget>(4, Allocator.Temp);
                    NativeArray<Curve> nativeArray6 = chunk.GetNativeArray(ref m_CurveType);
                    NativeArray<EdgeGeometry> edgeGeometryComponents = chunk.GetNativeArray(ref m_EdgeGeometryType);
                    NativeArray<Composition> compositionComponents = chunk.GetNativeArray(ref m_CompositionType);
                    NativeArray<Game.Tools.EditorContainer> editorContainersComponents = chunk.GetNativeArray(ref m_EditorContainerType);
                    NativeArray<PrefabRef> prefabRefsComponents = chunk.GetNativeArray(ref m_PrefabRefType);
                    BufferAccessor<ConnectedNode> connectedNodesBuffers = chunk.GetBufferAccessor(ref m_ConnectedNodeType);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        Entity owner = entities[i];
                        DynamicBuffer<SubLane> lanes = subLanesBuffers[i];
                        Temp ownerTemp = default(Temp);
                        if (tempComponents.Length != 0)
                        {
                            ownerTemp = tempComponents[i];
                            if (m_SubLanes.HasBuffer(ownerTemp.m_Original))
                            {
                                DynamicBuffer<SubLane> lanes2 = m_SubLanes[ownerTemp.m_Original];
                                FillOldLaneBuffer(isEdge: true, isNode: false, owner, lanes2, laneBuffer.m_OriginalLanes);
                            }
                        }
                        FillOldLaneBuffer(isEdge: true, isNode: false, owner, lanes, laneBuffer.m_OldLanes);
                        if (edgeGeometryComponents.Length != 0)
                        {
                            Edge edge = edges[i];
                            EdgeGeometry geometryData = edgeGeometryComponents[i];
                            Curve curve = nativeArray6[i];
                            Composition composition = compositionComponents[i];
                            PrefabRef prefabRef = prefabRefsComponents[i];
                            DynamicBuffer<ConnectedNode> dynamicBuffer = connectedNodesBuffers[i];
                            NetGeometryData prefabGeometryData = m_PrefabGeometryData[prefabRef.m_Prefab];
                            int edgeLaneIndex = 65535;
                            int connectionIndex = 0;
                            int groupIndex = 0;
                            Unity.Mathematics.Random random = randomSeeds[i].GetRandom(PseudoRandomSeed.kSubLane);
                            bool isSingleCurve = NetUtils.TryGetCombinedSegmentForLanes(geometryData, prefabGeometryData, out Segment segment);
                            //nodes for connecting buildings to the road edge
                            for (int j = 0; j < dynamicBuffer.Length; j++)
                            {
                                ConnectedNode connectedNode = dynamicBuffer[j];
                                GetMiddleConnectionCurves(connectedNode.m_Node, edgeTargets);
                                GetNodeConnectPositions(connectedNode.m_Node, connectedNode.m_CurvePosition, nativeList, nativeList2, includeAnchored: true, ref groupIndex, out float _, out float _, out CompositionFlags _);
                                FilterNodeConnectPositions(owner, ownerTemp.m_Original, nativeList, edgeTargets);
                                FilterNodeConnectPositions(owner, ownerTemp.m_Original, nativeList2, edgeTargets);
                                CreateEdgeConnectionLanes(chunkIndex, ref edgeLaneIndex, ref connectionIndex, ref random, owner, laneBuffer, nativeList, nativeList2, tempBuffer, tempBuffer2, composition.m_Edge,
                                    geometryData, curve, isSingleCurve, tempComponents.Length != 0, ownerTemp);
                                nativeList.Clear();
                                nativeList2.Clear();
                            }
                            CreateEdgeLanes(chunkIndex, ref random, owner, laneBuffer, composition, edge, geometryData, segment, isSingleCurve, tempComponents.Length != 0, ownerTemp);
                        }
                        else if (editorContainersComponents.Length != 0)
                        {
                            Game.Tools.EditorContainer editorContainer = editorContainersComponents[i];
                            Curve curve2 = nativeArray6[i];
                            Segment segment = default(Segment);
                            segment.m_Left = curve2.m_Bezier;
                            segment.m_Right = curve2.m_Bezier;
                            segment.m_Length = curve2.m_Length;
                            Segment segment2 = segment;
                            NetLaneData netLaneData = m_NetLaneData[editorContainer.m_Prefab];
                            NetCompositionLane netCompositionLane = default(NetCompositionLane);
                            netCompositionLane.m_Flags = netLaneData.m_Flags;
                            netCompositionLane.m_Lane = editorContainer.m_Prefab;
                            NetCompositionLane prefabCompositionLaneData = netCompositionLane;
                            Unity.Mathematics.Random random2 = (randomSeeds.Length == 0) ? m_RandomSeed.GetRandom(owner.Index) : randomSeeds[i].GetRandom(PseudoRandomSeed.kSubLane);
                            CreateEdgeLane(chunkIndex, ref random2, owner, laneBuffer, segment2, default(NetCompositionData), default(CompositionData), default(DynamicBuffer<NetCompositionLane>),
                                prefabCompositionLaneData, new int2(0, 4), new float2(0f, 1f), default(NativeList<LaneAnchor>), default(NativeList<LaneAnchor>), false, tempComponents.Length != 0, ownerTemp);
                        }
                        RemoveUnusedOldLanes(chunkIndex, owner, lanes, laneBuffer.m_OldLanes);
                        laneBuffer.Clear();
                    }
                    nativeList.Dispose();
                    nativeList2.Dispose();
                    tempBuffer.Dispose();
                    tempBuffer2.Dispose();
                    edgeTargets.Dispose();
                }
                else if (transforms.Length != 0)
                {
                    Logger.DebugLaneSystem($"Has Transforms {transforms.Length}");
                    NativeArray<PrefabRef> nativeArray11 = chunk.GetNativeArray(ref m_PrefabRefType);
                    bool flag = m_EditorMode && !chunk.Has(ref m_OwnerType);
                    bool flag2 = !chunk.Has(ref m_ElevationType);
                    bool flag3 = chunk.Has(ref m_UnderConstructionType);
                    bool flag4 = chunk.Has(ref m_DestroyedType);
                    NativeList<ClearAreaData> clearAreas = default(NativeList<ClearAreaData>);
                    Game.Prefabs.SubLane prefabSubLane2 = default(Game.Prefabs.SubLane);
                    for (int k = 0; k < entities.Length; k++)
                    {
                        Entity entity = entities[k];
                        Game.Objects.Transform transform = transforms[k];
                        PrefabRef prefabRef2 = nativeArray11[k];
                        DynamicBuffer<SubLane> lanes3 = subLanesBuffers[k];
                        Temp ownerTemp2 = default(Temp);
                        if (tempComponents.Length != 0)
                        {
                            ownerTemp2 = tempComponents[k];
                            if (m_SubLanes.HasBuffer(ownerTemp2.m_Original))
                            {
                                DynamicBuffer<SubLane> lanes4 = m_SubLanes[ownerTemp2.m_Original];
                                FillOldLaneBuffer(isEdge: false, isNode: false, entity,lanes4, laneBuffer.m_OriginalLanes);
                            }
                        }
                        FillOldLaneBuffer(isEdge: false, isNode: false, entity,lanes3, laneBuffer.m_OldLanes);
                        if (!flag)
                        {
                            Entity entity2 = entity;
                            if ((ownerTemp2.m_Flags & (TempFlags.Delete | TempFlags.Select | TempFlags.Duplicate)) != 0 || ownerTemp2.m_Original != Entity.Null)
                            {
                                entity2 = ownerTemp2.m_Original;
                            }
                            bool flag5 = (ownerTemp2.m_Flags & (TempFlags.Delete | TempFlags.Select | TempFlags.Duplicate)) != 0;
                            if (m_InstalledUpgrades.TryGetBuffer(entity2, out DynamicBuffer<InstalledUpgrade> bufferData) && bufferData.Length != 0)
                            {
                                ClearAreaHelpers.FillClearAreas(bufferData, Entity.Null, m_TransformData, m_AreaClearData, m_PrefabRefData, m_PrefabObjectGeometryData, m_SubAreas, m_AreaNodes, m_AreaTriangles,
                                    ref clearAreas);
                                ClearAreaHelpers.InitClearAreas(clearAreas, transform);
                            }
                            else if (m_EditorMode && ownerTemp2.m_Original != Entity.Null)
                            {
                                flag5 = true;
                            }
                            bool flag6 = flag2;
                            if (flag6)
                            {
                                flag6 = (m_PrefabObjectGeometryData.TryGetComponent(prefabRef2.m_Prefab, out ObjectGeometryData componentData) && ((componentData.m_Flags & Game.Objects.GeometryFlags.DeleteOverridden) != 0 ||
                                    (componentData.m_Flags & (Game.Objects.GeometryFlags.Overridable | Game.Objects.GeometryFlags.Marker | Game.Objects.GeometryFlags.Brushable)) == 0));
                            }
                            Unity.Mathematics.Random random3 = randomSeeds[k].GetRandom(PseudoRandomSeed.kSubLane);
                            if (flag5)
                            {
                                if (m_SubLanes.TryGetBuffer(ownerTemp2.m_Original, out DynamicBuffer<SubLane> bufferData2))
                                {
                                    for (int l = 0; l < bufferData2.Length; l++)
                                    {
                                        Entity subLane = bufferData2[l].m_SubLane;
                                        CreateObjectLane(chunkIndex, ref random3, entity, subLane, laneBuffer, transform, default(Game.Prefabs.SubLane), l, flag6, cutForTraffic: false, tempComponents.Length != 0,
                                            ownerTemp2, clearAreas);
                                    }
                                }
                            }
                            else
                            {
                                int num = 0;
                                int num2 = 0;
                                if (m_PrefabSubLanes.TryGetBuffer(prefabRef2.m_Prefab, out DynamicBuffer<Game.Prefabs.SubLane> bufferData3))
                                {
                                    for (int m = 0; m < bufferData3.Length; m++)
                                    {
                                        Game.Prefabs.SubLane prefabSubLane = bufferData3[m];
                                        if (prefabSubLane.m_NodeIndex.y != prefabSubLane.m_NodeIndex.x)
                                        {
                                            num = m;
                                            num2 = math.max(num2, math.cmax(prefabSubLane.m_NodeIndex));
                                            if ((!flag3 || (m_NetLaneData[prefabSubLane.m_Prefab].m_Flags & (LaneFlags.Road | LaneFlags.Pedestrian | LaneFlags.Parking | LaneFlags.Track)) != 0) &&
                                                (!flag4 || !math.any(prefabSubLane.m_ParentMesh >= 0)))
                                            {
                                                CreateObjectLane(chunkIndex, ref random3, entity, Entity.Null, laneBuffer, transform, prefabSubLane, m, flag6, cutForTraffic: false, tempComponents.Length != 0, ownerTemp2,
                                                    clearAreas);
                                            }
                                        }
                                    }
                                }
                                if (flag3 && m_PrefabBuildingData.TryGetComponent(prefabRef2.m_Prefab, out BuildingData componentData2) &&
                                    m_NetLaneData.TryGetComponent(m_BuildingConfigurationData.m_ConstructionBorder, out NetLaneData componentData3))
                                {
                                    float3 @float = new float3((float)componentData2.m_LotSize.x * 4f - componentData3.m_Width * 0.5f, 0f, 0f);
                                    float3 rhs = new float3(0f, 0f, (float)componentData2.m_LotSize.y * 4f - componentData3.m_Width * 0.5f);
                                    prefabSubLane2.m_Prefab = m_BuildingConfigurationData.m_ConstructionBorder;
                                    prefabSubLane2.m_ParentMesh = -1;
                                    prefabSubLane2.m_NodeIndex = new int2(num2, num2 + 1);
                                    prefabSubLane2.m_Curve = NetUtils.StraightCurve(-@float - rhs, @float - rhs);
                                    CreateObjectLane(chunkIndex, ref random3, entity, Entity.Null, laneBuffer, transform, prefabSubLane2, num, flag6, (componentData2.m_Flags & Game.Prefabs.BuildingFlags.BackAccess) != 0,
                                        tempComponents.Length != 0, ownerTemp2, clearAreas);
                                    prefabSubLane2.m_NodeIndex = new int2(num2 + 1, num2 + 2);
                                    prefabSubLane2.m_Curve = NetUtils.StraightCurve(@float - rhs, @float + rhs);
                                    CreateObjectLane(chunkIndex, ref random3, entity, Entity.Null, laneBuffer, transform, prefabSubLane2, num + 1, flag6,
                                        (componentData2.m_Flags & Game.Prefabs.BuildingFlags.LeftAccess) != 0, tempComponents.Length != 0, ownerTemp2, clearAreas);
                                    prefabSubLane2.m_NodeIndex = new int2(num2 + 2, num2 + 3);
                                    prefabSubLane2.m_Curve = NetUtils.StraightCurve(@float + rhs, -@float + rhs);
                                    CreateObjectLane(chunkIndex, ref random3, entity, Entity.Null, laneBuffer, transform, prefabSubLane2, num + 2, flag6, cutForTraffic: true, tempComponents.Length != 0, ownerTemp2,
                                        clearAreas);
                                    prefabSubLane2.m_NodeIndex = new int2(num2 + 3, num2);
                                    prefabSubLane2.m_Curve = NetUtils.StraightCurve(-@float + rhs, -@float - rhs);
                                    CreateObjectLane(chunkIndex, ref random3, entity, Entity.Null, laneBuffer, transform, prefabSubLane2, num + 3, flag6,
                                        (componentData2.m_Flags & Game.Prefabs.BuildingFlags.RightAccess) != 0, tempComponents.Length != 0, ownerTemp2, clearAreas);
                                    num += 4;
                                    num2 += 4;
                                }
                            }
                        }
                        RemoveUnusedOldLanes(chunkIndex, entity, lanes3, laneBuffer.m_OldLanes);
                        laneBuffer.Clear();
                        if (clearAreas.IsCreated)
                        {
                            clearAreas.Clear();
                        }
                    }
                    if (clearAreas.IsCreated)
                    {
                        clearAreas.Dispose();
                    }
                }
                else
                {
                    Logger.DebugLaneSystem($"Has Nodes {entities.Length}");
                    NativeParallelHashSet<ConnectionKey> createdConnections = new NativeParallelHashSet<ConnectionKey>(32, Allocator.Temp);
                    NativeList<ConnectPosition> sourceNodeConnectPositions = new NativeList<ConnectPosition>(32, Allocator.Temp);
                    NativeList<ConnectPosition> targetNodeConnectPositions = new NativeList<ConnectPosition>(32, Allocator.Temp);
                    NativeList<ConnectPosition> sourceMainCarConnectPositions = new NativeList<ConnectPosition>(32, Allocator.Temp);
                    NativeList<ConnectPosition> targetMainCarConnectPositions = new NativeList<ConnectPosition>(32, Allocator.Temp);
                    NativeList<ConnectPosition> tempSourceConnectPositions = new NativeList<ConnectPosition>(32, Allocator.Temp);
                    NativeList<ConnectPosition> tempTargetConnectPositions = new NativeList<ConnectPosition>(32, Allocator.Temp);
                    NativeHashSet<LaneEndKey> tempModifiedLaneEnds = new NativeHashSet<LaneEndKey>(4, Allocator.Temp);
                    NativeHashSet<ConnectionKey> tempMainConnectionKeys = new NativeHashSet<ConnectionKey>(4, Allocator.Temp);
                    NativeList<MiddleConnection> middleConnections = new NativeList<MiddleConnection>(4, Allocator.Temp);
                    NativeList<EdgeTarget> tempEdgeTargets = new NativeList<EdgeTarget>(4, Allocator.Temp);
                    NativeArray<Orphan> orphanComponents = chunk.GetNativeArray(ref m_OrphanType);
                    NativeArray<NodeGeometry> nodeGeometryComponents = chunk.GetNativeArray(ref m_NodeGeometryType);
                    NativeArray<PrefabRef> prefabRefComponents = chunk.GetNativeArray(ref m_PrefabRefType);

                    // NON-STOCK
                    BufferAccessor<ModifiedLaneConnections> modifiedLaneConnections = chunk.GetBufferAccessor(ref modifiedLaneConnectionsType);
                    NativeHashMap<int, int> priorities = new NativeHashMap<int, int>(8, Allocator.Temp);
                    NativeHashMap<EdgeToEdgeKey, int2> edgeLaneOffsetsMap = new NativeHashMap<EdgeToEdgeKey, int2>(4, Allocator.Temp); 
                    // NON-STOCK-END
                
                    for (int entityIndex = 0; entityIndex < entities.Length; entityIndex++)
                    {
                        Entity entity3 = entities[entityIndex];
                        DynamicBuffer<SubLane> lanes5 = subLanesBuffers[entityIndex];
                        Temp ownerTemp3 = default(Temp);
                        if (tempComponents.Length != 0) //chunk has Temp component (updating existing(m_Original) subLanes)
                        {
                            ownerTemp3 = tempComponents[entityIndex];
                            if (m_SubLanes.HasBuffer(ownerTemp3.m_Original))
                            {
                                DynamicBuffer<SubLane> lanes6 = m_SubLanes[ownerTemp3.m_Original];
                                FillOldLaneBuffer(isEdge: false, isNode: true, entity3, lanes6, laneBuffer.m_OriginalLanes);
                            }
                        }
                        FillOldLaneBuffer(isEdge: false, isNode: true, entity3, lanes5, laneBuffer.m_OldLanes);
                        if (nodeGeometryComponents.Length != 0) //chunk has NodeGeometry component
                        {
                            
                            float3 position = m_NodeData[entity3].m_Position;
                            position.y = nodeGeometryComponents[entityIndex].m_Position;
                            int groupIndex2 = 1;
                            GetNodeConnectPositions(entity3, 0f, sourceNodeConnectPositions, targetNodeConnectPositions, includeAnchored: false, ref groupIndex2, out float middleRadius2, out float roundaboutSize2, out CompositionFlags intersectionFlags2);
                            bool flag7 = groupIndex2 <= 2;
                            GetMiddleConnections(entity3, ownerTemp3.m_Original, middleConnections, tempEdgeTargets, tempSourceConnectPositions, tempTargetConnectPositions, ref groupIndex2);
                            FilterMainCarConnectPositions(sourceNodeConnectPositions, sourceMainCarConnectPositions);
                            FilterMainCarConnectPositions(targetNodeConnectPositions, targetMainCarConnectPositions);
                            int prevLaneIndex = 0;
                            Unity.Mathematics.Random random4 = randomSeeds[entityIndex].GetRandom(PseudoRandomSeed.kSubLane);
                            
                            /*NON-STOCK*/
                            bool testKeys = false;
                            if (modifiedLaneConnections.Length > 0 /*&& tempComponents.Length == 0*/)
                            {
                                DynamicBuffer<ModifiedLaneConnections> connections = modifiedLaneConnections[entityIndex];
                                FillModifiedLaneConnections(connections, tempModifiedLaneEnds, tempComponents.Length != 0);
                                testKeys = true;
                            }
                            /*NON-STOCK*/
                            RoadTypes roadTypes = RoadTypes.None;
                            if (middleRadius2 > 0f)
                            {
                                roadTypes = GetRoundaboutRoadPassThrough(entity3);
                            }
                            if (middleRadius2 > 0f && (roadTypes == RoadTypes.None || flag7)) // isRoundabout
                            {
                                if (sourceMainCarConnectPositions.Length != 0 || targetMainCarConnectPositions.Length != 0)
                                {
                                    ConnectPosition roundaboutLane = default(ConnectPosition);
                                    ConnectPosition dedicatedLane = default(ConnectPosition);
                                    int laneCount = 0;
                                    uint laneGroup = 0u;
                                    float laneWidth = float.MaxValue;
                                    float spaceForLanes = float.MaxValue;
                                    bool isPublicOnly = true;
                                    int nextLaneIndex = prevLaneIndex;
                                    bool bicycleOnly = true;
                                    int highwayConnectPositions = 0;
                                    int nonHighwayConnectPositions = 0;
                                    for (int num5 = 0; num5 < sourceMainCarConnectPositions.Length; num5++)
                                    {
                                        ConnectPosition connectPosition = sourceMainCarConnectPositions[num5];
                                        bicycleOnly &= (connectPosition.m_RoadTypes == RoadTypes.Bicycle);
                                        if ((connectPosition.m_CompositionData.m_RoadFlags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0)
                                        {
                                            highwayConnectPositions++;
                                        }
                                        else
                                        {
                                            nonHighwayConnectPositions++;
                                        }
                                    }
                                    for (int num6 = 0; num6 < targetMainCarConnectPositions.Length; num6++)
                                    {
                                        ConnectPosition connectPosition = targetMainCarConnectPositions[num6];
                                        bicycleOnly &= (connectPosition.m_RoadTypes == RoadTypes.Bicycle);
                                        if ((connectPosition.m_CompositionData.m_RoadFlags & (Game.Prefabs.RoadFlags.DefaultIsForward | Game.Prefabs.RoadFlags.DefaultIsBackward)) != 0)
                                        {
                                            if ((connectPosition.m_CompositionData.m_RoadFlags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0)
                                            {
                                                highwayConnectPositions++;
                                            }
                                            else
                                            {
                                                nonHighwayConnectPositions++;
                                            }
                                        }
                                    }
                                    bool preferHighway = highwayConnectPositions > nonHighwayConnectPositions || highwayConnectPositions >= 2;
                                    ConnectPosition connectPosition2;
                                    bool flag9;
                                    if (sourceMainCarConnectPositions.Length != 0)
                                    {
                                        int num7 = 0;
                                        for (int num8 = 0; num8 < sourceMainCarConnectPositions.Length; num8++)
                                        {
                                            ConnectPosition main = sourceMainCarConnectPositions[num8];
                                            FilterActualCarConnectPositions(main, sourceNodeConnectPositions, tempSourceConnectPositions);
                                            bool roundaboutLane2 = GetRoundaboutLane(tempSourceConnectPositions, roundaboutSize2, ref roundaboutLane, ref dedicatedLane, ref laneCount, ref laneWidth, ref isPublicOnly, ref spaceForLanes, isSource: true,
                                                preferHighway, bicycleOnly);
                                            num7 = math.select(num7, num8, roundaboutLane2);
                                            tempSourceConnectPositions.Clear();
                                        }
                                        if (roundaboutLane.m_LaneData.m_Lane == Entity.Null)
                                        {
                                            int laneCount2 = 0;
                                            float spaceForLanes2 = float.MaxValue;
                                            foreach (ConnectPosition main2 in targetMainCarConnectPositions)
                                            {
                                                FilterActualCarConnectPositions(main2, targetNodeConnectPositions, tempTargetConnectPositions);
                                                GetRoundaboutLane(tempTargetConnectPositions, roundaboutSize2, ref roundaboutLane, ref dedicatedLane, ref laneCount2, ref laneWidth, ref isPublicOnly, ref spaceForLanes2, isSource: false, preferHighway,
                                                    bicycleOnly);
                                                tempTargetConnectPositions.Clear();
                                            }
                                            laneCount = math.select(laneCount, 1, laneCount == 0 && laneCount2 != 0);
                                            spaceForLanes = spaceForLanes2 * (float)laneCount;
                                        }
                                        connectPosition2 = sourceMainCarConnectPositions[num7];
                                        flag9 = true;
                                    }
                                    else
                                    {
                                        int num10 = 0;
                                        for (int num11 = 0; num11 < targetMainCarConnectPositions.Length; num11++)
                                        {
                                            ConnectPosition main3 = targetMainCarConnectPositions[num11];
                                            FilterActualCarConnectPositions(main3, targetNodeConnectPositions, tempTargetConnectPositions);
                                            bool roundaboutLane3 = GetRoundaboutLane(tempTargetConnectPositions, roundaboutSize2, ref roundaboutLane, ref dedicatedLane, ref laneCount, ref laneWidth, ref isPublicOnly, ref spaceForLanes, isSource: false,
                                                preferHighway, bicycleOnly);
                                            num10 = math.select(num10, num11, roundaboutLane3);
                                            tempTargetConnectPositions.Clear();
                                        }
                                        connectPosition2 = targetMainCarConnectPositions[num10];
                                        flag9 = false;
                                    }
                                    ExtractNextConnectPosition(connectPosition2, position, sourceMainCarConnectPositions, targetMainCarConnectPositions, out ConnectPosition nextPosition, out bool nextIsSource);
                                    if (flag9)
                                    {
                                        FilterActualCarConnectPositions(connectPosition2, sourceNodeConnectPositions, tempSourceConnectPositions);
                                    }
                                    if (!nextIsSource)
                                    {
                                        FilterActualCarConnectPositions(nextPosition, targetNodeConnectPositions, tempTargetConnectPositions);
                                    }
                                    int laneCount3 = GetRoundaboutLaneCount(connectPosition2, nextPosition, tempSourceConnectPositions, tempTargetConnectPositions, targetNodeConnectPositions, position);
                                    connectPosition2 = nextPosition;
                                    flag9 = nextIsSource;
                                    tempSourceConnectPositions.Clear();
                                    tempTargetConnectPositions.Clear();
                                    int length = sourceMainCarConnectPositions.Length;
                                    while (sourceMainCarConnectPositions.Length != 0 || targetMainCarConnectPositions.Length != 0)
                                    {
                                        ExtractNextConnectPosition(connectPosition2, position, sourceMainCarConnectPositions, targetMainCarConnectPositions, out ConnectPosition nextPosition2, out bool nextIsSource2);
                                        if (flag9)
                                        {
                                            FilterActualCarConnectPositions(connectPosition2, sourceNodeConnectPositions, tempSourceConnectPositions);
                                        }
                                        if (!nextIsSource2)
                                        {
                                            FilterActualCarConnectPositions(nextPosition2, targetNodeConnectPositions, tempTargetConnectPositions);
                                        }
                                        CreateRoundaboutCarLanes(chunkIndex, ref random4, entity3, laneBuffer, ref prevLaneIndex, -1, ref laneGroup, connectPosition2, nextPosition2, middleConnections, tempSourceConnectPositions,
                                            tempTargetConnectPositions, targetNodeConnectPositions, roundaboutLane, dedicatedLane, intersectionFlags2, position, middleRadius2, ref laneCount3, laneCount, length, spaceForLanes, roadTypes, flag7, tempComponents.Length != 0, ownerTemp3);
                                        connectPosition2 = nextPosition2;
                                        flag9 = nextIsSource2;
                                        tempSourceConnectPositions.Clear();
                                        tempTargetConnectPositions.Clear();
                                    }
                                    if (flag9)
                                    {
                                        FilterActualCarConnectPositions(connectPosition2, sourceNodeConnectPositions, tempSourceConnectPositions);
                                    }
                                    if (!nextIsSource)
                                    {
                                        FilterActualCarConnectPositions(nextPosition, targetNodeConnectPositions, tempTargetConnectPositions);
                                    }
                                    CreateRoundaboutCarLanes(chunkIndex, ref random4, entity3, laneBuffer, ref prevLaneIndex, nextLaneIndex, ref laneGroup, connectPosition2, nextPosition, middleConnections, tempSourceConnectPositions,
                                        tempTargetConnectPositions, targetNodeConnectPositions, roundaboutLane, dedicatedLane, intersectionFlags2, position, middleRadius2, ref laneCount3, laneCount, length, spaceForLanes, roadTypes, flag7, tempComponents.Length != 0, ownerTemp3);
                                    tempSourceConnectPositions.Clear();
                                    tempTargetConnectPositions.Clear();
                                }
                            }
                            else
                            {
                                RoadTypes roadTypes2 = RoadTypes.None;
                                for (int num12 = 0; num12 < sourceMainCarConnectPositions.Length; num12++)
                                {
                                    ConnectPosition sourceMainCarConnectPos = sourceMainCarConnectPositions[num12];
                                    int nodeLaneIndex = prevLaneIndex;
                                    if (roadTypes2 != sourceMainCarConnectPos.m_RoadTypes)
                                    {
                                        tempTargetConnectPositions.Clear();
                                        FilterActualCarConnectPositions(sourceMainCarConnectPos.m_RoadTypes, targetNodeConnectPositions, tempTargetConnectPositions);
                                        roadTypes2 = sourceMainCarConnectPos.m_RoadTypes;
                                    }
                                    int yield = CalculateYieldOffset(sourceMainCarConnectPos, sourceMainCarConnectPositions, targetMainCarConnectPositions);
                                    FilterActualCarConnectPositions(sourceMainCarConnectPos, sourceNodeConnectPositions, tempSourceConnectPositions);
                                    //NON-STOCK
                                    priorities.Clear();
                                    if (lanePriorities.HasBuffer(sourceMainCarConnectPos.m_Owner))
                                    {
                                        for (int i = 0; i < tempSourceConnectPositions.Length; i++)
                                        {
                                            priorities.Add(tempSourceConnectPositions[i].m_LaneData.m_Index, FindPriority(tempSourceConnectPositions[i], out int priority) ? priority : yield);
                                        }
                                    }
                                    //NON-STOCK-END
                                    
                                    tempMainConnectionKeys.Clear();
                                    
                                    ProcessCarConnectPositions(chunkIndex, ref nodeLaneIndex, ref random4, entity3, prefabRefComponents[entityIndex].m_Prefab, laneBuffer, middleConnections, createdConnections, tempSourceConnectPositions, tempTargetConnectPositions, sourceNodeConnectPositions, roadTypes,
                                            intersectionFlags2, tempComponents.Length != 0, ownerTemp3, yield, /*NON-STOCK*/ tempModifiedLaneEnds, tempMainConnectionKeys, priorities);
                                    //NON-STOCK
                                    if (modifiedLaneConnections.Length > 0)
                                    {
                                        DynamicBuffer<ModifiedLaneConnections> modifiedConnections = modifiedLaneConnections[entityIndex];
                                        int idx = nodeLaneIndex;
                                        for (var i = 0; i < modifiedConnections.Length; i++)
                                        {
                                            ModifiedLaneConnections connectionsEntity = modifiedConnections[i];
                                            if (connectionsEntity.edgeEntity != sourceMainCarConnectPos.m_Owner || connectionsEntity.modifiedConnections == Entity.Null || !generatedConnections.HasBuffer(connectionsEntity.modifiedConnections))
                                            {
                                                Logger.DebugLaneSystem($"Skip 1 {connectionsEntity.edgeEntity} != {sourceMainCarConnectPos.m_Owner}, {connectionsEntity.modifiedConnections} | {!generatedConnections.HasBuffer(connectionsEntity.modifiedConnections)}");
                                                continue;
                                            }
                                        
                                            DynamicBuffer<GeneratedConnection> connections = generatedConnections[connectionsEntity.modifiedConnections];
                                            ConnectPosition cs = FindNodeConnectPosition(tempSourceConnectPositions, connectionsEntity.edgeEntity, connectionsEntity.laneIndex, TrackTypes.None,  out int sourcePosIndex, out int sourcePosGroupIndex, out int sourceLaneCount);
                                            if (cs.m_Owner == Entity.Null || cs.m_Owner != sourceMainCarConnectPos.m_Owner || cs.m_LaneData.m_Group != sourceMainCarConnectPos.m_LaneData.m_Group)
                                            {
                                                Logger.DebugLaneSystem($"Skip 2 o: {cs.m_Owner} | sO: {sourceMainCarConnectPos.m_Owner} || lIdx: {connectionsEntity.laneIndex} ||g: {cs.m_LaneData.m_Group} sG: {sourceMainCarConnectPos.m_LaneData.m_Group}");
                                                continue;
                                            }
                                            for (var j = 0; j < connections.Length; j++)
                                            {
                                                GeneratedConnection connection = connections[j];
                                                ConnectPosition ct = FindNodeConnectPosition(tempTargetConnectPositions, connection.targetEntity, connection.laneIndexMap.y, TrackTypes.None, out int targetPosIndex, out int targetPosGroupIndex, out int targetLaneCount);
                                                ConnectionKey key = new ConnectionKey(cs, ct);
                                                if (ct.m_Owner != Entity.Null && !createdConnections.Contains(key))
                                                {
                                                    EdgeToEdgeKey edgeKey = new EdgeToEdgeKey(cs.m_Owner, ct.m_Owner);
                                                    //TODO improve edgekey support more lane groups per edge
                                                    if (!edgeLaneOffsetsMap.TryGetValue(edgeKey, out int2 edgeConnectionOffset))
                                                    {
                                                        edgeConnectionOffset = CalculateLaneConnectionOffsets(tempSourceConnectPositions, tempTargetConnectPositions, sourcePosIndex, targetPosIndex, sourcePosGroupIndex, targetPosGroupIndex, sourceLaneCount, targetLaneCount);
                                                        edgeLaneOffsetsMap.Add(edgeKey, edgeConnectionOffset);
                                                    }
                                                    int yield2 = CalculateYieldOffset(cs, sourceMainCarConnectPositions, targetMainCarConnectPositions);
                                                    uint group = (uint)(cs.m_GroupIndex | (ct.m_GroupIndex << 16));
                                                    bool isTurn = IsTurn(cs, ct, out bool right, out bool gentle, out bool uturn);
                                                    float curviness = -1;
                                                    bool isLeftLimit = sourcePosIndex == 0 && targetPosIndex == 0;
                                                    bool isRightLimit = (sourcePosIndex == tempSourceConnectPositions.Length - 1) & (targetPosIndex == tempTargetConnectPositions.Length - 1);
                                                    bool2 merge = false;
                                                    if ((!isTurn || !uturn) && connection.isUnsafe)
                                                    {
                                                        merge = CalculateLaneMergeFlags(edgeConnectionOffset, sourcePosGroupIndex, targetPosGroupIndex, sourceLaneCount, targetLaneCount);
                                                    }
                                                    bool isSkipped = false;
                                                    if (CreateNodeLane(chunkIndex, ref idx, ref random4, ref curviness, ref isSkipped, entity3, laneBuffer, middleConnections, cs, ct, intersectionFlags2, group, 0, connection.isUnsafe, false, tempComponents.Length != 0, (connection.method & (PathMethod.Road | PathMethod.Track)) == PathMethod.Track, yield2, ownerTemp3, isTurn, right, gentle, uturn, false, isLeftLimit, isRightLimit, merge.x,merge.y, false, RoadTypes.None,
                                                            /*NON-STOCK*/(connection.method & (PathMethod.Road | PathMethod.Track)) == PathMethod.Road))
                                                    {
                                                        createdConnections.Add(key);
                                                        tempMainConnectionKeys.Add(new ConnectionKey(cs.m_Owner.Index, cs.m_LaneData.m_Group, ct.m_Owner.Index, ct.m_LaneData.m_Group));
                                                        Logger.DebugLaneSystem($"Added CustomGenerated ({key.GetString()}) to created! [ {cs.m_GroupIndex} | {ct.m_GroupIndex} -> {group} || G: [{(byte)(group)}|{(byte)(group >> 8)}] | [{(byte)(group >> 16)}|{(byte)(group >> 24)}] <= {group}");
                                                    }    
                                                }
                                            }
                                        }
                                    }
                                    //NON-STOCK-END
                                    tempSourceConnectPositions.Clear();

                                    /*
                                     * TODO insert lane generator, by sourceMainCarPos
                                     */
                                    for (int num13 = 0; num13 < targetMainCarConnectPositions.Length; num13++)
                                    {
                                        ConnectPosition targetPosition = targetMainCarConnectPositions[num13];
                                        if ((targetPosition.m_RoadTypes & sourceMainCarConnectPos.m_RoadTypes) == 0)
                                        {
                                            continue;
                                        }
                                        bool isUnsafe = false;
                                        bool isForbidden = false;
                                        bool isSkipped = false;
                                        for (int num14 = 0; num14 < tempTargetConnectPositions.Length; num14++)
                                        {
                                            ConnectPosition value = tempTargetConnectPositions[num14];
                                            if (value.m_Owner == targetPosition.m_Owner && value.m_LaneData.m_Group == targetPosition.m_LaneData.m_Group)
                                            {
                                                if (value.m_SkippedCount != 0)
                                                {
                                                    isSkipped = true;
                                                    value.m_SkippedCount = 0;
                                                    tempTargetConnectPositions[num14] = value;
                                                }
                                                else if (value.m_ForbiddenCount != 0)
                                                {
                                                    isUnsafe = true;
                                                    isForbidden = true;
                                                    value.m_UnsafeCount = 0;
                                                    value.m_ForbiddenCount = 0;
                                                    tempTargetConnectPositions[num14] = value;
                                                }
                                                else if (value.m_UnsafeCount != 0)
                                                {
                                                    isUnsafe = true;
                                                    value.m_UnsafeCount = 0;
                                                    tempTargetConnectPositions[num14] = value;
                                                }
                                            }
                                        }
                                        if (((sourceMainCarConnectPos.m_LaneData.m_Flags | targetPosition.m_LaneData.m_Flags) & LaneFlags.Master) != 0)
                                        {
                                            uint group = (uint)(sourceMainCarConnectPos.m_GroupIndex | (targetPosition.m_GroupIndex << 16));
                                            //NON-STOCK
                                            if (testKeys && !tempMainConnectionKeys.Contains(new ConnectionKey(sourceMainCarConnectPos.m_Owner.Index, sourceMainCarConnectPos.m_LaneData.m_Group, targetPosition.m_Owner.Index, targetPosition.m_LaneData.m_Group)))
                                            {
                                                Logger.DebugLaneSystem($"Skipped Master Lane connection! {new ConnectionKey(sourceMainCarConnectPos, targetPosition).GetString()} [ {sourceMainCarConnectPos.m_GroupIndex}[{sourceMainCarConnectPos.m_GroupIndex >> 8}] ({sourceMainCarConnectPos.m_LaneData.m_Group}) | {targetPosition.m_GroupIndex}[{targetPosition.m_GroupIndex >> 8}] ({targetPosition.m_LaneData.m_Group})] G: [{(byte)(group)}|{(byte)(group >> 8)}] | [{(byte)(group >> 16)}|{(byte)(group >> 24)}] <= {group}");
                                                continue;
                                            }
                                            //NON-STOCK-END
                                            
                                            bool isTurn = IsTurn(sourceMainCarConnectPos, targetPosition, out bool right, out bool gentle, out bool uturn);
                                            float curviness = -1f;
                                            if (CreateNodeLane(chunkIndex, ref nodeLaneIndex, ref random4, ref curviness, ref isSkipped, entity3, laneBuffer, middleConnections, sourceMainCarConnectPos, targetPosition, intersectionFlags2, group, 0, isUnsafe,
                                                isForbidden, tempComponents.Length != 0, trackOnly: false, 0, ownerTemp3, isTurn, right, gentle, uturn, isRoundabout: false, isLeftLimit: false, isRightLimit: false,
                                                isMergeLeft: false, isMergeRight: false, fixedTangents: false, RoadTypes.None))
                                            {
                                                Logger.DebugLaneSystem($"Added Master Lane connection {new ConnectionKey(sourceMainCarConnectPos, targetPosition).GetString()} [ {sourceMainCarConnectPos.m_GroupIndex}[{sourceMainCarConnectPos.m_GroupIndex >> 8}] ({sourceMainCarConnectPos.m_LaneData.m_Group}) | {targetPosition.m_GroupIndex}[{targetPosition.m_GroupIndex >> 8}] ({targetPosition.m_LaneData.m_Group})] G: [{(byte)(group)}|{(byte)(group >> 8)}] | [{(byte)(group >> 16)}|{(byte)(group >> 24)}] <= {group}");
                                                createdConnections.Add(new ConnectionKey(sourceMainCarConnectPos, targetPosition));
                                            }
                                        }
                                    }
                                    prevLaneIndex += 256;
                                }
                                tempTargetConnectPositions.Clear();
                                edgeLaneOffsetsMap.Clear();
                            }
                            sourceMainCarConnectPositions.Clear();
                            targetMainCarConnectPositions.Clear();
                            TrackTypes trackTypes = FilterTrackConnectPositions(sourceNodeConnectPositions, sourceMainCarConnectPositions) & FilterTrackConnectPositions(targetNodeConnectPositions, targetMainCarConnectPositions);
                            TrackTypes trackTypes2 = TrackTypes.Train;
                            while (trackTypes != 0)
                            {
                                if ((trackTypes & trackTypes2) != 0)
                                {
                                    trackTypes = (TrackTypes)((uint)trackTypes & (uint)(byte)(~(int)trackTypes2));
                                    FilterTrackConnectPositions(trackTypes2, targetMainCarConnectPositions, tempTargetConnectPositions);
                                    if (tempTargetConnectPositions.Length != 0)
                                    {
                                        int index = 0;
                                        while (index < sourceMainCarConnectPositions.Length)
                                        {
                                            FilterTrackConnectPositions(ref index, trackTypes2, sourceMainCarConnectPositions, tempSourceConnectPositions);
                                            if (tempSourceConnectPositions.Length != 0)
                                            {
                                                int nodeLaneIndex2 = prevLaneIndex;
                                                ProcessTrackConnectPositions(chunkIndex, ref nodeLaneIndex2, ref random4, entity3, laneBuffer, middleConnections, createdConnections, tempSourceConnectPositions, tempTargetConnectPositions,
                                                    tempComponents.Length != 0, ownerTemp3, /*NON-STOCK*/tempModifiedLaneEnds);
                                                
                                                /*NON-STOCK*/ //TODO OPTIMIZE!
                                                if (modifiedLaneConnections.Length > 0)
                                                {
                                                    DynamicBuffer<ModifiedLaneConnections> modifiedConnections = modifiedLaneConnections[entityIndex];
                                                    for (var i = 0; i < modifiedConnections.Length; i++)
                                                    {
                                                        ModifiedLaneConnections connectionsEntity = modifiedConnections[i];
                                                        ConnectPosition sourcePosition = FindNodeConnectPosition(tempSourceConnectPositions, connectionsEntity.edgeEntity, connectionsEntity.laneIndex, trackTypes2, out int sIndex, out _, out _);
                                                        if (connectionsEntity.edgeEntity != sourcePosition.m_Owner || sourcePosition.m_Owner == Entity.Null || connectionsEntity.modifiedConnections == Entity.Null || !generatedConnections.HasBuffer(connectionsEntity.modifiedConnections))
                                                        {
                                                            Logger.DebugLaneSystem(
                                                                $"Skip Track {connectionsEntity.edgeEntity} != {sourcePosition.m_Owner}, {connectionsEntity.modifiedConnections} | {!generatedConnections.HasBuffer(connectionsEntity.modifiedConnections)}");
                                                            continue;
                                                        }

                                                        DynamicBuffer<GeneratedConnection> connections = generatedConnections[connectionsEntity.modifiedConnections];
                                                        for (var j = 0; j < connections.Length; j++)
                                                        {
                                                            GeneratedConnection connection = connections[j];
                                                            if ((connection.method & PathMethod.Track) == (PathMethod)0)
                                                            {
                                                                continue;
                                                            }
                                                            ConnectPosition targetPosition = FindNodeConnectPosition(tempTargetConnectPositions, connection.targetEntity, connection.laneIndexMap.y, trackTypes2, out _, out _, out _);
                                                            if (targetPosition.m_Owner == Entity.Null)
                                                            {
                                                                Logger.DebugLaneSystem($"Skip Track, no target pos for {trackTypes}");
                                                                continue;
                                                            }
                                                            ConnectionKey key = new ConnectionKey(sourcePosition, targetPosition);
                                                            if (createdConnections.Contains(key))
                                                            {
                                                                Logger.DebugLaneSystem($"Skip Track, connection already added: {key.GetString()}");
                                                                continue;
                                                            }
                                                            
                                                            bool isTurn = IsTurn(sourcePosition, targetPosition, out bool right, out bool gentle, out bool uturn);
                                                            float curviness = -1f;
                                                            bool isSkipped = false;
                                                            if (CreateNodeLane(chunkIndex, ref nodeLaneIndex2, ref random4, ref curviness, ref isSkipped, entity3, laneBuffer, middleConnections, sourcePosition, targetPosition, default(CompositionFlags), 0u, 0, isUnsafe: false, isForbidden: false,
                                                                isTemp: tempComponents.Length != 0 /*todo*/,
                                                                trackOnly: true, 0, ownerTemp3, isTurn, right, gentle, uturn, isRoundabout: false, isLeftLimit: false /*todo*/, isRightLimit: false /*todo*/, isMergeLeft: false, isMergeRight: false,
                                                                fixedTangents: false, RoadTypes.None))
                                                            {
                                                                createdConnections.Add(key);
                                                                tempMainConnectionKeys.Add(new ConnectionKey(sourcePosition.m_Owner.Index, sourcePosition.m_LaneData.m_Group, targetPosition.m_Owner.Index, targetPosition.m_LaneData.m_Group));
                                                                Logger.DebugLaneSystem($"Added CustomGenerated Track: {trackTypes} | ({key.GetString()}) to created! [ {sourcePosition.m_GroupIndex} | {targetPosition.m_GroupIndex} -> 0 ");
                                                            }
                                                        }

                                                    }
                                                }
                                                /*NON-STOCK*/
                                                tempSourceConnectPositions.Clear();
                                                prevLaneIndex += 256;
                                            }
                                        }
                                        tempTargetConnectPositions.Clear();
                                    }
                                }
                                trackTypes2 = (TrackTypes)((uint)trackTypes2 << 1);
                            }
                            sourceMainCarConnectPositions.Clear();
                            targetMainCarConnectPositions.Clear();
                            for (int num15 = 0; num15 < 2; num15++)
                            {
                                FilterPedestrianConnectPositions(targetNodeConnectPositions, sourceMainCarConnectPositions, middleConnections, num15 == 1);
                                CreateNodePedestrianLanes(chunkIndex, ref prevLaneIndex, ref random4, entity3, laneBuffer, sourceMainCarConnectPositions, targetMainCarConnectPositions, tempTargetConnectPositions, tempComponents.Length != 0, ownerTemp3, position, middleRadius2, roundaboutSize2);
                                sourceMainCarConnectPositions.Clear();
                                targetMainCarConnectPositions.Clear();
                                tempTargetConnectPositions.Clear();
                            }
                            tempModifiedLaneEnds.Clear();
                            UtilityTypes utilityTypes = FilterUtilityConnectPositions(targetNodeConnectPositions, targetMainCarConnectPositions);
                            UtilityTypes utilityTypes2 = UtilityTypes.WaterPipe;
                            while (utilityTypes != 0)
                            {
                                if ((utilityTypes & utilityTypes2) != 0)
                                {
                                    utilityTypes = (UtilityTypes)((uint)utilityTypes & (uint)(byte)(~(int)utilityTypes2));
                                    FilterUtilityConnectPositions(utilityTypes2, targetMainCarConnectPositions, tempTargetConnectPositions);
                                    FilterUtilityConnectPositions(utilityTypes2, sourceMainCarConnectPositions, tempSourceConnectPositions);
                                    if (tempTargetConnectPositions.Length != 0 || tempSourceConnectPositions.Length != 0)
                                    {
                                        CreateNodeUtilityLanes(chunkIndex, ref prevLaneIndex, ref random4, entity3, laneBuffer, tempTargetConnectPositions, tempSourceConnectPositions, middleConnections, position, middleRadius2 > 0f,tempComponents.Length != 0, ownerTemp3);
                                        tempTargetConnectPositions.Clear();
                                        tempSourceConnectPositions.Clear();
                                    }
                                }
                                utilityTypes2 = (UtilityTypes)((uint)utilityTypes2 << 1);
                            }
                            CreateNodeConnectionLanes(chunkIndex, ref prevLaneIndex, ref random4, entity3, laneBuffer, middleConnections, tempTargetConnectPositions, intersectionFlags2, middleRadius2 > 0f, tempComponents.Length != 0, ownerTemp3);
                            createdConnections.Clear();
                            sourceNodeConnectPositions.Clear();
                            targetNodeConnectPositions.Clear();
                            sourceMainCarConnectPositions.Clear();
                            targetMainCarConnectPositions.Clear();
                            middleConnections.Clear();
                            if (orphanComponents.Length != 0)
                            {
                                CreateOrphanLanes(chunkIndex, ref random4, entity3, laneBuffer, orphanComponents[entityIndex], position, prefabRefComponents[entityIndex].m_Prefab, ref prevLaneIndex, tempComponents.Length != 0, ownerTemp3);
                            }
                        }
                        RemoveUnusedOldLanes(chunkIndex, entity3, lanes5, laneBuffer.m_OldLanes);
                        laneBuffer.Clear();
                    }
                    createdConnections.Dispose();
                    sourceNodeConnectPositions.Dispose();
                    targetNodeConnectPositions.Dispose();
                    sourceMainCarConnectPositions.Dispose();
                    targetMainCarConnectPositions.Dispose();
                    tempSourceConnectPositions.Dispose();
                    tempTargetConnectPositions.Dispose();
                    middleConnections.Dispose();
                    tempEdgeTargets.Dispose();
                    tempModifiedLaneEnds.Dispose();
                }
                UpdateLanes(chunkIndex, laneBuffer.m_Updates);
                laneBuffer.Dispose();
            }

            /// <summary>
            /// NON-STOCK
            /// </summary>
            private int2 CalculateLaneConnectionOffsets(NativeList<ConnectPosition> sourceConnectPositions, NativeList<ConnectPosition> targetConnectPositions, int sourcePosIndex, int targetPosIndex,  int sourcePosGroupIndex, int targetPosGroupIndex, int sourceLaneCount, int targetLaneCount)
            {
                int calcFirstSrcGroupIdx = 0;
                int firstTargetGroupIdx = 0;
                int currentMaxConnections = math.max(sourceLaneCount, targetLaneCount);
                int calcSourceConnInGroup = sourceLaneCount;
                int targetGroupIdxCount = targetLaneCount;
                int laneInGroupCount = math.min(calcSourceConnInGroup, targetGroupIdxCount);
                int initialOffset = 0;
                int offsetTestCount = currentMaxConnections - laneInGroupCount;
                int sourceIdxOffset = 0;
                int targetIdxOffset = 0;

                int targetGroupOffset = targetPosIndex - targetPosGroupIndex;
                float lastMaxDistSqSlice = float.MaxValue;
                for (int n = initialOffset; n <= offsetTestCount; n++)
                {
                    int num65 = math.max(n + calcSourceConnInGroup - currentMaxConnections, 0);
                    int num66 = math.max(n + targetGroupIdxCount - currentMaxConnections, 0);
                    num65 += calcFirstSrcGroupIdx;
                    num66 += firstTargetGroupIdx;
                    ConnectPosition sourcePosSliceStart = sourceConnectPositions[num65];
                    ConnectPosition sourcePosSliceEnd = sourceConnectPositions[num65 + laneInGroupCount - 1];
                    ConnectPosition targetPosSliceStart = targetConnectPositions[targetGroupOffset + num66];
                    ConnectPosition targetPosSliceEnd = targetConnectPositions[targetGroupOffset + num66 + laneInGroupCount - 1];
                    float num67 = math.max(0f, math.dot(sourcePosSliceStart.m_Tangent, targetPosSliceStart.m_Tangent) * -0.5f); // value [0..0.5f] 60deg left
                    float num68 = math.max(0f, math.dot(sourcePosSliceEnd.m_Tangent, targetPosSliceEnd.m_Tangent) * -0.5f);     // value [0..0.5f] 60deg right
                    num67 *= math.distance(sourcePosSliceStart.m_Position.xz, targetPosSliceStart.m_Position.xz);
                    num68 *= math.distance(sourcePosSliceEnd.m_Position.xz, targetPosSliceEnd.m_Position.xz);
                    sourcePosSliceStart.m_Position.xz += sourcePosSliceStart.m_Tangent.xz * num67;
                    targetPosSliceStart.m_Position.xz += targetPosSliceStart.m_Tangent.xz * num67;
                    sourcePosSliceEnd.m_Position.xz += sourcePosSliceEnd.m_Tangent.xz * num68;
                    targetPosSliceEnd.m_Position.xz += targetPosSliceEnd.m_Tangent.xz * num68;
                    float distSqSourceSlice = math.distancesq(sourcePosSliceStart.m_Position.xz, targetPosSliceStart.m_Position.xz);
                    float distSqTartetSlice = math.distancesq(sourcePosSliceEnd.m_Position.xz, targetPosSliceEnd.m_Position.xz);
                    float maxDistSqSlice = math.max(distSqSourceSlice, distSqTartetSlice);
                    
                    if (maxDistSqSlice < lastMaxDistSqSlice)
                    {
                        sourceIdxOffset = math.min(currentMaxConnections - targetGroupIdxCount - n, 0);
                        targetIdxOffset = math.min(currentMaxConnections - calcSourceConnInGroup - n, 0);
                        lastMaxDistSqSlice = maxDistSqSlice;
                    }
                }
                
                return new int2(sourceIdxOffset, targetIdxOffset);
            }

            /// <summary>
            /// NON-STOCK
            /// </summary>
            private bool2 CalculateLaneMergeFlags(int2 offsets, int sourcePosGroupIndex, int targetPosGroupIndex, int sourceLaneCount, int targetLaneCount)
            {
                int calcSourceConnInGroup = sourceLaneCount;
                int targetGroupIdxCount = targetLaneCount;
                int laneIndex = math.select(sourcePosGroupIndex, targetPosGroupIndex, targetLaneCount > sourceLaneCount);//validate with more tests
                bool srcOffsetLeft = laneIndex + offsets.x < 0;
                bool srcOffsetRight = laneIndex + offsets.x >= calcSourceConnInGroup;
                bool targetOffsetLeft = laneIndex + offsets.y < 0;
                bool targetOffsetRight = laneIndex + offsets.y >= targetGroupIdxCount;
                bool isMergeLeft = srcOffsetLeft | targetOffsetLeft;
                bool isMergeRight = srcOffsetRight | targetOffsetRight;

                return new bool2(isMergeLeft, isMergeRight);
            }

            /// <summary>
            /// NON-STOCK
            /// </summary>
            private ConnectPosition FindNodeConnectPosition(NativeList<ConnectPosition> connectPositions, Entity owner, int laneIndex, TrackTypes trackTypes, out int index, out int laneGroupIndex, out int laneCount)
            {
                laneCount = 0;
                index = -1;
                laneGroupIndex = -1;
                ConnectPosition resultConnectPos = new ConnectPosition(); 
                bool found = false;
                for (int i = 0; i < connectPositions.Length; i++)
                {
                    ConnectPosition p = connectPositions[i];
                    if (p.m_Owner == owner)
                    {
                        if (!found && 
                            p.m_LaneData.m_Index == laneIndex &&
                            (trackTypes == 0 || (p.m_TrackTypes & trackTypes) != 0))
                        {
                            index = i;
                            laneGroupIndex = laneCount;
                            resultConnectPos =  p;
                            found = true;
                        }
                        laneCount++;
                    }
                }
                
                return resultConnectPos;
            }

            private void CreateOrphanLanes(int jobIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, Orphan orphan, float3 middlePosition, Entity prefab, ref int nodeLaneIndex, bool isTemp, Temp ownerTemp)
            {
                if (!m_DefaultNetLanes.HasBuffer(prefab))
                {
                    return;
                }
                DynamicBuffer<DefaultNetLane> dynamicBuffer = m_DefaultNetLanes[prefab];
                int num = -1;
                int num2 = -1;
                for (int i = 0; i < dynamicBuffer.Length; i++)
                {
                    if ((dynamicBuffer[i].m_Flags & LaneFlags.Pedestrian) != 0)
                    {
                        if (num == -1)
                        {
                            num = i;
                        }
                        else
                        {
                            num2 = i;
                        }
                    }
                }
                if (num2 > num)
                {
                    DefaultNetLane defaultNetLane = dynamicBuffer[num];
                    DefaultNetLane defaultNetLane2 = dynamicBuffer[num2];
                    ConnectPosition connectPosition = default(ConnectPosition);
                    connectPosition.m_LaneData.m_Lane = defaultNetLane.m_Lane;
                    connectPosition.m_LaneData.m_Flags = defaultNetLane.m_Flags;
                    connectPosition.m_NodeComposition = orphan.m_Composition;
                    connectPosition.m_Owner = owner;
                    connectPosition.m_BaseHeight = middlePosition.y;
                    connectPosition.m_Position = middlePosition;
                    connectPosition.m_Position.xy += defaultNetLane.m_Position.xy;
                    connectPosition.m_Tangent = new float3(0f, 0f, 1f);
                    ConnectPosition connectPosition2 = default(ConnectPosition);
                    connectPosition2.m_LaneData.m_Lane = defaultNetLane2.m_Lane;
                    connectPosition2.m_LaneData.m_Flags = defaultNetLane2.m_Flags;
                    connectPosition2.m_NodeComposition = orphan.m_Composition;
                    connectPosition2.m_Owner = owner;
                    connectPosition2.m_BaseHeight = middlePosition.y;
                    connectPosition2.m_Position = middlePosition;
                    connectPosition2.m_Position.xy += defaultNetLane2.m_Position.xy;
                    connectPosition2.m_Tangent = new float3(0f, 0f, 1f);
                    PathNode pathNode = new PathNode(owner, (ushort)nodeLaneIndex++);
                    CreateNodePedestrianLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, connectPosition2, connectPosition, pathNode, pathNode, default(float2), isCrosswalk: false, isSideConnection: true, isTemp, ownerTemp,
                        fixedTangents: false, hasSignals: false, out Bezier4x3 curve, out PathNode middleNode, out PathNode endNode);
                    connectPosition.m_Tangent = -connectPosition.m_Tangent;
                    connectPosition2.m_Tangent = -connectPosition2.m_Tangent;
                    CreateNodePedestrianLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, connectPosition, connectPosition2, endNode, pathNode, default(float2), isCrosswalk: false, isSideConnection: true, isTemp, ownerTemp,
                        fixedTangents: false, hasSignals: false, out curve, out middleNode, out PathNode _);
                }
            }

            private int GetRoundaboutLaneCount(ConnectPosition prevMainPosition, ConnectPosition nextMainPosition, NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer, NativeList<ConnectPosition> allTargets,
                float3 middlePosition)
            {
                float2 fromVector = math.normalizesafe(prevMainPosition.m_Position.xz - middlePosition.xz);
                float2 toVector = math.normalizesafe(nextMainPosition.m_Position.xz - middlePosition.xz);
                float num = (!prevMainPosition.m_Position.Equals(nextMainPosition.m_Position))
                    ? (m_LeftHandTraffic ? MathUtils.RotationAngleRight(fromVector, toVector) : MathUtils.RotationAngleLeft(fromVector, toVector))
                    : (math.PI * 2f);
                int num2 = math.max(1, Mathf.CeilToInt(num * (2f / math.PI) - 0.003141593f));
                if (sourceBuffer.Length >= 2)
                {
                    sourceBuffer.Sort(default(TargetPositionComparer));
                }
                if (targetBuffer.Length >= 2)
                {
                    targetBuffer.Sort(default(TargetPositionComparer));
                }
                int num3 = int.MaxValue;
                int x = int.MaxValue;
                int num4 = 0;
                int num5 = 0;
                for (int i = 0; i < sourceBuffer.Length; i++)
                {
                    if (sourceBuffer[i].m_RoadTypes == prevMainPosition.m_RoadTypes)
                    {
                        num3 = math.min(num3, i);
                        num4++;
                    }
                }
                for (int j = 0; j < targetBuffer.Length; j++)
                {
                    if (targetBuffer[j].m_RoadTypes == nextMainPosition.m_RoadTypes)
                    {
                        x = math.min(x, j);
                        num5++;
                    }
                }
                int num6 = math.max(1, num4);
                if (num2 == 1)
                {
                    if (num4 > 0 && num5 > 0)
                    {
                        int y = math.clamp(num4 + num5 - GetRoundaboutTargetLaneCount(sourceBuffer[num3 + num4 - 1], allTargets), 0, math.min(num5, num4 - 1));
                        y = math.min(1, y);
                        return num6 - y;
                    }
                    return num6;
                }
                int num7 = num5 - math.select(1, 0, num5 <= 1);
                return math.max(1, num6 - num7);
            }

            private int GetRoundaboutTargetLaneCount(ConnectPosition sourcePosition, NativeList<ConnectPosition> allTargets)
            {
                int num = 0;
                for (int i = 0; i < allTargets.Length; i++)
                {
                    ConnectPosition targetPosition = allTargets[i];
                    if ((targetPosition.m_LaneData.m_Flags & (LaneFlags.Master | LaneFlags.Road | LaneFlags.BicyclesOnly)) == LaneFlags.Road)
                    {
                        IsTurn(sourcePosition, targetPosition, out bool _, out bool _, out bool uturn);
                        num += math.select(1, 0, uturn);
                    }
                }
                return num;
            }

            private void CreateRoundaboutCarLanes(int jobIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, ref int prevLaneIndex, int nextLaneIndex, ref uint laneGroup, ConnectPosition prevMainPosition,
                ConnectPosition nextMainPosition, NativeList<MiddleConnection> middleConnections, NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer, NativeList<ConnectPosition> allTargets, ConnectPosition lane,
                ConnectPosition dedicatedLane, CompositionFlags intersectionFlags, float3 middlePosition, float middleRadius, ref int laneCount, int totalLaneCount, int totalSourceCount, float spaceForLanes, RoadTypes roadPassThrough, bool isDeadEnd,
                bool isTemp, Temp ownerTemp)
            {
                float2 @float = math.normalizesafe(prevMainPosition.m_Position.xz - middlePosition.xz);
                float2 float2 = math.normalizesafe(nextMainPosition.m_Position.xz - middlePosition.xz);
                float num = (!prevMainPosition.m_Position.Equals(nextMainPosition.m_Position))
                    ? (m_LeftHandTraffic ? MathUtils.RotationAngleRight(@float, float2) : MathUtils.RotationAngleLeft(@float, float2))
                    : (math.PI * 2f);
                int num2 = math.max(1, Mathf.CeilToInt(num * (2f / math.PI) - 0.003141593f));
                float2 float3 = @float;
                float num3 = spaceForLanes;
                if (m_NetLaneData.TryGetComponent(dedicatedLane.m_LaneData.m_Lane, out NetLaneData componentData))
                {
                    num3 += componentData.m_Width * (2f / 3f);
                }
                ConnectPosition connectPosition = default(ConnectPosition);
                connectPosition.m_LaneData.m_Lane = lane.m_LaneData.m_Lane;
                connectPosition.m_RoadTypes = lane.m_RoadTypes;
                connectPosition.m_NodeComposition = lane.m_NodeComposition;
                connectPosition.m_EdgeComposition = lane.m_EdgeComposition;
                connectPosition.m_CompositionData = lane.m_CompositionData;
                connectPosition.m_BaseHeight = middlePosition.y + prevMainPosition.m_BaseHeight - prevMainPosition.m_Position.y;
                connectPosition.m_Tangent.xz = (m_LeftHandTraffic ? MathUtils.Right(float3) : MathUtils.Left(float3));
                connectPosition.m_SegmentIndex = (byte)(prevLaneIndex >> 8);
                connectPosition.m_Owner = owner;
                connectPosition.m_Position.y = middlePosition.y;
                ConnectPosition connectPosition2 = default(ConnectPosition);
                connectPosition2.m_LaneData.m_Lane = lane.m_LaneData.m_Lane;
                connectPosition2.m_RoadTypes = lane.m_RoadTypes;
                connectPosition2.m_NodeComposition = lane.m_NodeComposition;
                connectPosition2.m_EdgeComposition = lane.m_EdgeComposition;
                connectPosition2.m_CompositionData = lane.m_CompositionData;
                connectPosition2.m_Owner = owner;
                if (sourceBuffer.Length >= 2)
                {
                    sourceBuffer.Sort(default(TargetPositionComparer));
                }
                if (targetBuffer.Length >= 2)
                {
                    targetBuffer.Sort(default(TargetPositionComparer));
                }
                int num4 = int.MaxValue;
                int num5 = int.MaxValue;
                int num6 = 0;
                int num7 = 0;
                for (int i = 0; i < sourceBuffer.Length; i++)
                {
                    ConnectPosition connectPosition3 = sourceBuffer[i];
                    if (connectPosition3.m_RoadTypes == prevMainPosition.m_RoadTypes && ((uint)(connectPosition3.m_RoadTypes & lane.m_RoadTypes) & (uint)(byte)(~(int)dedicatedLane.m_RoadTypes)) != 0)
                    {
                        num4 = math.min(num4, i);
                        num6++;
                    }
                }
                for (int j = 0; j < targetBuffer.Length; j++)
                {
                    ConnectPosition connectPosition4 = targetBuffer[j];
                    if (connectPosition4.m_RoadTypes == nextMainPosition.m_RoadTypes && ((uint)(connectPosition4.m_RoadTypes & lane.m_RoadTypes) & (uint)(byte)(~(int)dedicatedLane.m_RoadTypes)) != 0)
                    {
                        num5 = math.min(num5, j);
                        num7++;
                    }
                }
                num4 = math.select(num4, 0, num4 == int.MaxValue);
                num5 = math.select(num5, 0, num5 == int.MaxValue);
                int num8 = 0;
                int num9 = num7 - math.select(1, 0, num7 <= 1);
                if (num2 == 1 && num6 > 0 && num7 > 0)
                {
                    num8 = math.clamp(num6 + num7 - GetRoundaboutTargetLaneCount(sourceBuffer[num4 + num6 - 1], allTargets), 0, math.min(num7, num6 - 1));
                    if (num8 == 0 && num6 < totalLaneCount)
                    {
                        int num10 = math.max(1, laneCount - num9);
                        num8 = math.select(0, 1, num10 + num6 >= totalLaneCount);
                    }
                    num8 = math.min(1, num8);
                }
                for (int k = 1; k <= num2; k++)
                {
                    int num11 = math.max(1, math.select(laneCount - num9, laneCount, k != num2));
                    int num12 = math.select(math.min(totalLaneCount, num11 + num6) - num8, num11, k != 1);
                    int nodeLaneIndex = prevLaneIndex + totalLaneCount + 2;
                    prevLaneIndex += 256;
                    int num13 = laneCount;
                    int num14 = num12;
                    if (dedicatedLane.m_LaneData.m_Lane != Entity.Null)
                    {
                        nodeLaneIndex++;
                        num13++;
                        num14++;
                    }
                    float2 float4;
                    if (k == num2)
                    {
                        float4 = float2;
                        connectPosition2.m_NodeComposition = lane.m_NodeComposition;
                        connectPosition2.m_EdgeComposition = lane.m_EdgeComposition;
                        connectPosition2.m_CompositionData = lane.m_CompositionData;
                        connectPosition2.m_BaseHeight = middlePosition.y + nextMainPosition.m_BaseHeight - nextMainPosition.m_Position.y;
                        connectPosition2.m_SegmentIndex = (byte)(math.select(prevLaneIndex, nextLaneIndex, nextLaneIndex >= 0) >> 8);
                        connectPosition2.m_Position.y = middlePosition.y;
                    }
                    else
                    {
                        float num15 = (float)k / (float)num2;
                        float4 = (m_LeftHandTraffic ? MathUtils.RotateRight(float3, num * num15) : MathUtils.RotateLeft(float3, num * num15));
                        connectPosition2.m_CompositionData.m_SpeedLimit = math.lerp(prevMainPosition.m_CompositionData.m_SpeedLimit, nextMainPosition.m_CompositionData.m_SpeedLimit, num15);
                        connectPosition2.m_BaseHeight = middlePosition.y + math.lerp(prevMainPosition.m_BaseHeight, nextMainPosition.m_BaseHeight, num15) - math.lerp(prevMainPosition.m_Position.y, nextMainPosition.m_Position.y, num15);
                        connectPosition2.m_SegmentIndex = (byte)(prevLaneIndex >> 8);
                        connectPosition2.m_Position.y = middlePosition.y;
                    }
                    float2 float5 = m_LeftHandTraffic ? MathUtils.RotateRight(float3, num * ((float)k - 0.5f) / (float)num2) : MathUtils.RotateLeft(float3, num * ((float)k - 0.5f) / (float)num2);
                    connectPosition2.m_Tangent.xz = (m_LeftHandTraffic ? MathUtils.Left(float4) : MathUtils.Right(float4));
                    float3 float6 = default(float3);
                    float6.xz = (m_LeftHandTraffic ? MathUtils.Right(float5) : MathUtils.Left(float5));
                    float3 centerPosition = default(float3);
                    centerPosition.y = math.lerp(connectPosition.m_Position.y, connectPosition2.m_Position.y, 0.5f);
                    bool flag = num13 >= 2;
                    bool flag2 = num14 >= 2;
                    bool flag3 = k == 1 && sourceBuffer.Length >= 1;
                    bool flag4 = k == num2 && targetBuffer.Length >= 1;
                    bool flag5 = num2 == 1 && sourceBuffer.Length >= 1 && targetBuffer.Length >= 1;
                    float curviness = -1f;
                    bool flag6 = roadPassThrough != 0 || (isDeadEnd && (nextLaneIndex < 0 || flag3 || flag4));
                    bool isUnsafe = flag6;
                    bool isSkipped = false;
                    for (int l = 0; l < num14; l++)
                    {
                        int num16 = math.select(l, num14 - l - 1, m_LeftHandTraffic);
                        int num17;
                        float rhs;
                        float rhs2;
                        if (l == num12)
                        {
                            num17 = laneCount;
                            connectPosition.m_LaneData.m_Lane = dedicatedLane.m_LaneData.m_Lane;
                            connectPosition2.m_LaneData.m_Lane = dedicatedLane.m_LaneData.m_Lane;
                            connectPosition.m_RoadTypes = dedicatedLane.m_RoadTypes;
                            connectPosition2.m_RoadTypes = dedicatedLane.m_RoadTypes;
                            rhs = middleRadius + num3;
                            rhs2 = middleRadius + num3;
                            connectPosition.m_LaneData.m_Flags = (dedicatedLane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary | LaneFlags.BicyclesOnly));
                            connectPosition2.m_LaneData.m_Flags = (dedicatedLane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary | LaneFlags.BicyclesOnly));
                        }
                        else
                        {
                            num17 = math.max(0, l - num12 + num11);
                            rhs = middleRadius + ((float)num17 + 0.5f) * spaceForLanes / (float)totalLaneCount;
                            rhs2 = middleRadius + ((float)l + 0.5f) * spaceForLanes / (float)totalLaneCount;
                            connectPosition.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary | LaneFlags.BicyclesOnly));
                            connectPosition2.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary | LaneFlags.BicyclesOnly));
                        }
                        connectPosition.m_Position.xz = middlePosition.xz + @float * rhs;
                        connectPosition.m_LaneData.m_Index = (byte)num17;
                        if (flag)
                        {
                            connectPosition.m_LaneData.m_Flags |= LaneFlags.Slave;
                        }
                        connectPosition2.m_Position.xz = middlePosition.xz + float4 * rhs2;
                        connectPosition2.m_LaneData.m_Index = (byte)l;
                        if (flag2)
                        {
                            connectPosition2.m_LaneData.m_Flags |= LaneFlags.Slave;
                        }
                        bool a = l == 0;
                        bool b = l >= num12 - 1 && k > 1 && k < num2;
                        if (m_LeftHandTraffic)
                        {
                            CommonUtils.Swap(ref a, ref b);
                        }
                        if (l < num12 && num17 != l)
                        {
                            ConnectPosition sourcePosition = connectPosition;
                            ConnectPosition targetPosition = connectPosition2;
                            float3 position = targetPosition.m_Position;
                            position.xz = middlePosition.xz + float4 * rhs;
                            Bezier4x3 bezier4x = NetUtils.FitCurve(sourcePosition.m_Position, sourcePosition.m_Tangent, -targetPosition.m_Tangent, position);
                            sourcePosition.m_Tangent = bezier4x.b - bezier4x.a;
                            targetPosition.m_Tangent = MathUtils.Normalize(targetPosition.m_Tangent, targetPosition.m_Tangent.xz);
                            CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, ref isSkipped, owner, laneBuffer, middleConnections, sourcePosition, targetPosition, intersectionFlags, laneGroup, (ushort)num16, flag6, isForbidden: false,
                                isTemp, trackOnly: false, 0, ownerTemp, isTurn: false, isRight: false, isGentle: false, isUTurn: false, isRoundabout: true, a, b, isMergeLeft: false, isMergeRight: false, fixedTangents: true, roadPassThrough);
                        }
                        else
                        {
                            curviness = -1f;
                            CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, ref isSkipped, owner, laneBuffer, middleConnections, connectPosition, connectPosition2, intersectionFlags, laneGroup, (ushort)num16, flag6,
                                isForbidden: false, isTemp, trackOnly: false, 0, ownerTemp, isTurn: false, isRight: false, isGentle: false, isUTurn: false, isRoundabout: true, a, b, isMergeLeft: false, isMergeRight: false, fixedTangents: false,
                                roadPassThrough);
                        }
                        if (l == num12)
                        {
                            connectPosition.m_LaneData.m_Lane = lane.m_LaneData.m_Lane;
                            connectPosition2.m_LaneData.m_Lane = lane.m_LaneData.m_Lane;
                            connectPosition.m_RoadTypes = lane.m_RoadTypes;
                            connectPosition2.m_RoadTypes = lane.m_RoadTypes;
                        }
                    }
                    if (!isTemp && (flag || flag2))
                    {
                        float rhs3 = middleRadius + (float)laneCount * 0.5f * spaceForLanes / (float)totalLaneCount;
                        float rhs4 = middleRadius + (float)num12 * 0.5f * spaceForLanes / (float)totalLaneCount;
                        connectPosition.m_Position.xz = middlePosition.xz + @float * rhs3;
                        connectPosition.m_LaneData.m_Index = (byte)math.select(0, num13, flag);
                        connectPosition.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary | LaneFlags.BicyclesOnly));
                        connectPosition.m_LaneData.m_Flags |= LaneFlags.Master;
                        connectPosition2.m_Position.xz = middlePosition.xz + float4 * rhs4;
                        connectPosition2.m_LaneData.m_Index = (byte)math.select(0, num14, flag2);
                        connectPosition2.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary | LaneFlags.BicyclesOnly));
                        connectPosition2.m_LaneData.m_Flags |= LaneFlags.Master;
                        curviness = -1f;
                        CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, ref isSkipped, owner, laneBuffer, middleConnections, connectPosition, connectPosition2, intersectionFlags, laneGroup, 0, isUnsafe, isForbidden: false, isTemp,
                            trackOnly: false, 0, ownerTemp, isTurn: false, isRight: false, isGentle: false, isUTurn: false, isRoundabout: true, isLeftLimit: false, isRightLimit: false, isMergeLeft: false, isMergeRight: false, fixedTangents: false,
                            roadPassThrough);
                    }
                    laneGroup++;
                    float num18 = 0f;
                    Curve curve;
                    if (flag3)
                    {
                        bool flag7 = flag2;
                        int yield = math.select(0, 1, totalSourceCount >= 2);
                        isSkipped = false;
                        isUnsafe = true;
                        for (int m = 0; m < num14; m++)
                        {
                            int num19 = math.select(m, num14 - m - 1, m_LeftHandTraffic);
                            float num20;
                            float rhs5;
                            int index;
                            if (m == num12)
                            {
                                index = math.select(sourceBuffer.Length - 1, 0, m_LeftHandTraffic);
                                connectPosition2.m_LaneData.m_Lane = dedicatedLane.m_LaneData.m_Lane;
                                connectPosition2.m_RoadTypes = dedicatedLane.m_RoadTypes;
                                num20 = middleRadius + num3;
                                rhs5 = middleRadius + num3;
                                connectPosition2.m_LaneData.m_Flags = (dedicatedLane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary | LaneFlags.BicyclesOnly));
                                flag6 = (num6 == sourceBuffer.Length || roadPassThrough != RoadTypes.None);
                            }
                            else
                            {
                                index = math.max(0, m + math.min(0, num6 - num12));
                                index = num4 + math.select(index, num6 - index - 1, m_LeftHandTraffic);
                                num20 = middleRadius + ((float)(m + totalLaneCount - math.max(num6, num12)) + 0.5f) * spaceForLanes / (float)totalLaneCount;
                                rhs5 = middleRadius + ((float)m + 0.5f) * spaceForLanes / (float)totalLaneCount;
                                connectPosition2.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary | LaneFlags.BicyclesOnly));
                                flag6 = (num6 == 0);
                            }
                            centerPosition.xz = middlePosition.xz + float5 * num20;
                            connectPosition2.m_Position.xz = middlePosition.xz + float4 * rhs5;
                            connectPosition2.m_LaneData.m_Index = (byte)m;
                            if (flag2)
                            {
                                connectPosition2.m_LaneData.m_Flags |= LaneFlags.Slave;
                            }
                            bool a2 = false;
                            bool b2 = m >= num12 - 1 && k < num2;
                            if (m_LeftHandTraffic)
                            {
                                CommonUtils.Swap(ref a2, ref b2);
                            }
                            ConnectPosition sourcePosition2 = sourceBuffer[index];
                            ConnectPosition targetPosition2 = connectPosition2;
                            PresetCurve(ref sourcePosition2, ref targetPosition2, middlePosition, centerPosition, float6, num20, 0f, num / (float)num2, 2f);
                            Bezier4x3 bezier4x2 = new Bezier4x3(sourcePosition2.m_Position, sourcePosition2.m_Position + sourcePosition2.m_Tangent, targetPosition2.m_Position + targetPosition2.m_Tangent, targetPosition2.m_Position);
                            curve = default(Curve);
                            curve.m_Bezier = bezier4x2;
                            curve.m_Length = 1f;
                            curviness = NetUtils.CalculateStartCurviness(curve, m_NetLaneData[sourcePosition2.m_LaneData.m_Lane].m_Width);
                            if ((sourcePosition2.m_RoadTypes & targetPosition2.m_RoadTypes) != 0)
                            {
                                flag7 |= ((sourcePosition2.m_LaneData.m_Flags & LaneFlags.Slave) != 0);
                                CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, ref isSkipped, owner, laneBuffer, middleConnections, sourcePosition2, targetPosition2, intersectionFlags, laneGroup, (ushort)num19, flag6,
                                    isForbidden: false, isTemp, trackOnly: false, yield, ownerTemp, isTurn: false, isRight: false, isGentle: false, isUTurn: false, isRoundabout: true, a2, b2, isMergeLeft: false, isMergeRight: false, fixedTangents: true,
                                    roadPassThrough);
                                isUnsafe = (isUnsafe && flag6);
                            }
                            if (m == num12)
                            {
                                connectPosition2.m_LaneData.m_Lane = lane.m_LaneData.m_Lane;
                                connectPosition2.m_RoadTypes = lane.m_RoadTypes;
                            }
                            else
                            {
                                num18 = math.max(num18, math.distance(MathUtils.Position(bezier4x2, 0.5f).xz, middlePosition.xz));
                            }
                        }
                        if (flag7)
                        {
                            float start = middleRadius + ((float)(totalLaneCount - math.max(num6, num12)) + 0.5f) * spaceForLanes / (float)totalLaneCount;
                            float end = middleRadius + ((float)(num12 - 1 + totalLaneCount - math.max(num6, num12)) + 0.5f) * spaceForLanes / (float)totalLaneCount;
                            float num21 = math.lerp(start, end, 0.5f);
                            centerPosition.xz = middlePosition.xz + float5 * num21;
                            float rhs6 = middleRadius + (float)num12 * 0.5f * spaceForLanes / (float)totalLaneCount;
                            connectPosition2.m_Position.xz = middlePosition.xz + float4 * rhs6;
                            connectPosition2.m_LaneData.m_Index = (byte)math.select(0, num14, flag2);
                            connectPosition2.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary | LaneFlags.BicyclesOnly));
                            connectPosition2.m_LaneData.m_Flags |= LaneFlags.Master;
                            ConnectPosition sourcePosition3 = prevMainPosition;
                            ConnectPosition targetPosition3 = connectPosition2;
                            PresetCurve(ref sourcePosition3, ref targetPosition3, middlePosition, centerPosition, float6, num21, 0f, num / (float)num2, 2f);
                            curviness = -1f;
                            CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, ref isSkipped, owner, laneBuffer, middleConnections, sourcePosition3, targetPosition3, intersectionFlags, laneGroup, 0, isUnsafe, isForbidden: false, isTemp,
                                trackOnly: false, yield, ownerTemp, isTurn: false, isRight: false, isGentle: false, isUTurn: false, isRoundabout: true, isLeftLimit: false, isRightLimit: false, isMergeLeft: false, isMergeRight: false, fixedTangents: true,
                                roadPassThrough);
                        }
                        laneGroup++;
                    }
                    if (flag4)
                    {
                        bool flag8 = flag;
                        int yield2 = math.select(0, -1, totalSourceCount >= 2);
                        int num22 = math.select(targetBuffer.Length, targetBuffer.Length + 1, num7 == targetBuffer.Length && dedicatedLane.m_LaneData.m_Lane != Entity.Null);
                        isSkipped = false;
                        isUnsafe = true;
                        for (int n = 0; n < num22; n++)
                        {
                            int num23 = math.select(n, num22 - n - 1, m_LeftHandTraffic);
                            int num24 = math.min(n, targetBuffer.Length - 1);
                            num24 = math.select(targetBuffer.Length - num24 - 1, num24, m_LeftHandTraffic);
                            int num25;
                            float falseValue;
                            float rhs7;
                            if (num24 < num5 || num24 >= num5 + num7 || n >= targetBuffer.Length)
                            {
                                num25 = num13 - 1;
                                connectPosition.m_LaneData.m_Lane = dedicatedLane.m_LaneData.m_Lane;
                                connectPosition.m_RoadTypes = dedicatedLane.m_RoadTypes;
                                falseValue = middleRadius + ((float)math.min(totalLaneCount - 1, n + math.max(0, totalLaneCount - num7)) + 0.5f) * spaceForLanes / (float)totalLaneCount;
                                falseValue = math.select(falseValue, middleRadius + num3, num25 == laneCount || n >= targetBuffer.Length);
                                rhs7 = middleRadius + num3;
                                connectPosition.m_LaneData.m_Flags = (dedicatedLane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary | LaneFlags.BicyclesOnly));
                                flag6 = (num25 != laneCount || n >= targetBuffer.Length || roadPassThrough != RoadTypes.None);
                            }
                            else
                            {
                                num25 = math.min(laneCount - 1, n + math.max(0, laneCount - num7));
                                falseValue = middleRadius + ((float)math.min(totalLaneCount - 1, n + math.max(0, totalLaneCount - num7)) + 0.5f) * spaceForLanes / (float)totalLaneCount;
                                rhs7 = middleRadius + ((float)num25 + 0.5f) * spaceForLanes / (float)totalLaneCount;
                                connectPosition.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary | LaneFlags.BicyclesOnly));
                                flag6 = (n >= laneCount);
                            }
                            centerPosition.xz = middlePosition.xz + float5 * falseValue;
                            connectPosition.m_Position.xz = middlePosition.xz + @float * rhs7;
                            connectPosition.m_LaneData.m_Index = (byte)num25;
                            if (flag)
                            {
                                connectPosition.m_LaneData.m_Flags |= LaneFlags.Slave;
                            }
                            bool a3 = false;
                            bool b3 = n >= num7 - 1 && k > 1;
                            if (m_LeftHandTraffic)
                            {
                                CommonUtils.Swap(ref a3, ref b3);
                            }
                            ConnectPosition sourcePosition4 = connectPosition;
                            ConnectPosition targetPosition4 = targetBuffer[num24];
                            PresetCurve(ref sourcePosition4, ref targetPosition4, middlePosition, centerPosition, float6, falseValue, num / (float)num2, 0f, 2f);
                            Bezier4x3 bezier4x3 = new Bezier4x3(sourcePosition4.m_Position, sourcePosition4.m_Position + sourcePosition4.m_Tangent, targetPosition4.m_Position + targetPosition4.m_Tangent, targetPosition4.m_Position);
                            curve = default(Curve);
                            curve.m_Bezier = bezier4x3;
                            curve.m_Length = 1f;
                            curviness = NetUtils.CalculateEndCurviness(curve, m_NetLaneData[targetPosition4.m_LaneData.m_Lane].m_Width);
                            if ((sourcePosition4.m_RoadTypes & targetPosition4.m_RoadTypes) != 0)
                            {
                                flag8 |= ((targetPosition4.m_LaneData.m_Flags & LaneFlags.Slave) != 0);
                                CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, ref isSkipped, owner, laneBuffer, middleConnections, sourcePosition4, targetPosition4, intersectionFlags, laneGroup, (ushort)num23, flag6,
                                    isForbidden: false, isTemp, trackOnly: false, yield2, ownerTemp, isTurn: true, !m_LeftHandTraffic, isGentle: true, isUTurn: false, isRoundabout: true, a3, b3, flag6 && m_LeftHandTraffic, flag6 && !m_LeftHandTraffic,
                                    fixedTangents: true, roadPassThrough);
                                isUnsafe = (isUnsafe && flag6);
                            }
                            if (num24 < num5 || num24 >= num5 + num7 || n >= targetBuffer.Length)
                            {
                                connectPosition.m_LaneData.m_Lane = lane.m_LaneData.m_Lane;
                                connectPosition.m_RoadTypes = lane.m_RoadTypes;
                            }
                            else
                            {
                                num18 = math.max(num18, math.distance(MathUtils.Position(bezier4x3, 0.5f).xz, middlePosition.xz));
                            }
                        }
                        if (flag8)
                        {
                            float start2 = middleRadius + ((float)math.min(totalLaneCount - 1, math.max(0, totalLaneCount - num7)) + 0.5f) * spaceForLanes / (float)totalLaneCount;
                            float end2 = middleRadius + ((float)math.min(totalLaneCount - 1, num7 - 1 + math.max(0, totalLaneCount - num7)) + 0.5f) * spaceForLanes / (float)totalLaneCount;
                            float num26 = math.lerp(start2, end2, 0.5f);
                            centerPosition.xz = middlePosition.xz + float5 * num26;
                            float rhs8 = middleRadius + (float)laneCount * 0.5f * spaceForLanes / (float)totalLaneCount;
                            connectPosition.m_Position.xz = middlePosition.xz + @float * rhs8;
                            connectPosition.m_LaneData.m_Index = (byte)math.select(0, num13, flag);
                            connectPosition.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary | LaneFlags.BicyclesOnly));
                            connectPosition.m_LaneData.m_Flags |= LaneFlags.Master;
                            ConnectPosition sourcePosition5 = connectPosition;
                            ConnectPosition targetPosition5 = nextMainPosition;
                            PresetCurve(ref sourcePosition5, ref targetPosition5, middlePosition, centerPosition, float6, num26, num / (float)num2, 0f, 2f);
                            curviness = -1f;
                            CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, ref isSkipped, owner, laneBuffer, middleConnections, sourcePosition5, targetPosition5, intersectionFlags, laneGroup, 0, isUnsafe, isForbidden: false, isTemp,
                                trackOnly: false, yield2, ownerTemp, isTurn: true, !m_LeftHandTraffic, isGentle: true, isUTurn: false, isRoundabout: true, isLeftLimit: false, isRightLimit: false, isMergeLeft: false, isMergeRight: false,
                                fixedTangents: true, roadPassThrough);
                        }
                        laneGroup++;
                    }
                    if (flag5)
                    {
                        bool flag9 = false;
                        bool flag10 = false;
                        int yield3 = math.select(0, 1, totalSourceCount >= 2);
                        float num27 = middleRadius + ((float)totalLaneCount - 0.5f) * spaceForLanes / (float)totalLaneCount;
                        num27 = math.lerp(num27, math.max(num27, num18), 0.5f);
                        float2 float7 = m_LeftHandTraffic ? MathUtils.RotateRight(float3, num * ((float)k - 0.75f) / (float)num2) : MathUtils.RotateLeft(float3, num * ((float)k - 0.75f) / (float)num2);
                        float2 float8 = m_LeftHandTraffic ? MathUtils.RotateRight(float3, num * ((float)k - 0.25f) / (float)num2) : MathUtils.RotateLeft(float3, num * ((float)k - 0.25f) / (float)num2);
                        float3 centerTangent = default(float3);
                        float3 centerTangent2 = default(float3);
                        centerTangent.xz = (m_LeftHandTraffic ? MathUtils.Right(float7) : MathUtils.Left(float7));
                        centerTangent2.xz = (m_LeftHandTraffic ? MathUtils.Right(float8) : MathUtils.Left(float8));
                        int num28 = sourceBuffer.Length - 1;
                        int valueToClamp = math.select(num28, sourceBuffer.Length - num28 - 1, m_LeftHandTraffic);
                        valueToClamp = math.clamp(valueToClamp, num4, num4 + num6 - 1);
                        int num29 = 0;
                        int valueToClamp2 = math.select(num29, targetBuffer.Length - num29 - 1, m_LeftHandTraffic);
                        valueToClamp2 = math.clamp(valueToClamp2, num5, num5 + num7 - 1);
                        ConnectPosition sourcePosition6 = sourceBuffer[valueToClamp];
                        ConnectPosition targetPosition6 = targetBuffer[valueToClamp2];
                        float t;
                        float y = MathUtils.Distance(NetUtils.FitCurve(sourcePosition6.m_Position, sourcePosition6.m_Tangent, -targetPosition6.m_Tangent, targetPosition6.m_Position).xz, middlePosition.xz, out t);
                        num27 = math.max(num27, y);
                        float num30 = middleRadius + num3;
                        bool flag11 = dedicatedLane.m_LaneData.m_Lane != Entity.Null && (sourceBuffer.Length > num6 || targetBuffer.Length > num7);
                        if (flag11)
                        {
                            float num31 = 0.5f * spaceForLanes / (float)totalLaneCount + num3 - spaceForLanes;
                            num27 = math.min(num27, num30 + 0.75f - num31);
                            num30 = math.max(num30, num27 + num31);
                        }
                        ConnectPosition connectPosition5 = default(ConnectPosition);
                        connectPosition5.m_LaneData.m_Lane = lane.m_LaneData.m_Lane;
                        connectPosition5.m_NodeComposition = lane.m_NodeComposition;
                        connectPosition5.m_EdgeComposition = lane.m_EdgeComposition;
                        connectPosition5.m_CompositionData = lane.m_CompositionData;
                        connectPosition5.m_Owner = owner;
                        connectPosition5.m_CompositionData.m_SpeedLimit = math.lerp(connectPosition.m_CompositionData.m_SpeedLimit, connectPosition2.m_CompositionData.m_SpeedLimit, 0.5f);
                        connectPosition5.m_BaseHeight = math.lerp(connectPosition.m_BaseHeight, connectPosition2.m_BaseHeight, 0.5f);
                        connectPosition5.m_SegmentIndex = connectPosition2.m_SegmentIndex;
                        connectPosition5.m_Tangent = float6;
                        connectPosition5.m_Position.y = centerPosition.y;
                        connectPosition5.m_LaneData.m_Index = (byte)(num14 + 1);
                        connectPosition5.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary | LaneFlags.BicyclesOnly));
                        connectPosition5.m_Position.xz = middlePosition.xz + float5 * num27;
                        connectPosition5.m_RoadTypes = lane.m_RoadTypes;
                        ConnectPosition connectPosition6 = connectPosition5;
                        connectPosition6.m_LaneData.m_Lane = dedicatedLane.m_LaneData.m_Lane;
                        connectPosition6.m_LaneData.m_Index = (byte)(num14 + 2);
                        connectPosition6.m_LaneData.m_Flags = (dedicatedLane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary | LaneFlags.BicyclesOnly));
                        connectPosition6.m_Position.xz = middlePosition.xz + float5 * num30;
                        connectPosition6.m_RoadTypes = dedicatedLane.m_RoadTypes;
                        float3 float9 = middlePosition;
                        float9.y = math.lerp(connectPosition.m_Position.y, centerPosition.y, 0.5f);
                        float9.xz += float7 * num27;
                        float3 float10 = middlePosition;
                        float10.y = math.lerp(centerPosition.y, connectPosition2.m_Position.y, 0.5f);
                        float10.xz += float8 * num27;
                        isSkipped = false;
                        isUnsafe = true;
                        bool isSkipped2 = false;
                        int num32 = math.select(1, 2, (num6 < sourceBuffer.Length || flag11) && num7 != 0 && num6 != 0);
                        int num33 = math.select(targetBuffer.Length, targetBuffer.Length + 1, flag11 && targetBuffer.Length == num7 && num6 != 0);
                        int num34 = math.max(num32, num33);
                        for (int num35 = 0; num35 < num34; num35++)
                        {
                            int num36 = math.select(num35, targetBuffer.Length - num35, m_LeftHandTraffic);
                            num28 = math.select(sourceBuffer.Length - 1, sourceBuffer.Length - 2, num35 < num34 - 1 && sourceBuffer.Length > num6 && sourceBuffer.Length > 1);
                            valueToClamp = math.select(num28, sourceBuffer.Length - num28 - 1, m_LeftHandTraffic);
                            num29 = math.max(0, targetBuffer.Length - 1 - num35);
                            valueToClamp2 = math.select(num29, targetBuffer.Length - num29 - 1, m_LeftHandTraffic);
                            bool flag12 = valueToClamp < num4 || valueToClamp >= num4 + num6;
                            bool flag13 = valueToClamp2 < num5 || valueToClamp2 >= num5 + num7;
                            if (num34 - num35 <= num32)
                            {
                                bool a4 = false;
                                bool b4 = true;
                                if (m_LeftHandTraffic)
                                {
                                    CommonUtils.Swap(ref a4, ref b4);
                                }
                                sourcePosition6 = sourceBuffer[valueToClamp];
                                targetPosition6 = connectPosition5;
                                float3 centerPosition2 = float9;
                                float middleOffset = num27;
                                if (flag11 && (flag12 || flag13))
                                {
                                    targetPosition6 = connectPosition6;
                                    centerPosition2.xz += float7 * (num30 - num27);
                                    middleOffset = num30;
                                }
                                if ((sourcePosition6.m_RoadTypes & targetPosition6.m_RoadTypes) != 0)
                                {
                                    flag9 |= ((sourcePosition6.m_LaneData.m_Flags & LaneFlags.Slave) != 0);
                                    targetPosition6.m_Tangent = -targetPosition6.m_Tangent;
                                    flag6 = (flag11 && flag12 != flag13);
                                    isUnsafe = (isUnsafe && flag6);
                                    PresetCurve(ref sourcePosition6, ref targetPosition6, middlePosition, centerPosition2, centerTangent, middleOffset, 0f, num * 0.5f / (float)num2, 2f);
                                    curviness = -1f;
                                    CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, ref isSkipped, owner, laneBuffer, middleConnections, sourcePosition6, targetPosition6, intersectionFlags, laneGroup, (ushort)num36, flag6,
                                        isForbidden: false, isTemp, trackOnly: false, yield3, ownerTemp, isTurn: true, !m_LeftHandTraffic, isGentle: false, isUTurn: false, isRoundabout: true, a4, b4, isMergeLeft: false, isMergeRight: false,
                                        fixedTangents: true, roadPassThrough);
                                }
                            }
                            if (num35 < num33)
                            {
                                bool a5 = false;
                                bool b5 = num35 >= num7 - 1;
                                if (m_LeftHandTraffic)
                                {
                                    CommonUtils.Swap(ref a5, ref b5);
                                }
                                sourcePosition6 = connectPosition5;
                                targetPosition6 = targetBuffer[valueToClamp2];
                                float3 centerPosition3 = float10;
                                float middleOffset2 = num27;
                                if (flag11 && (flag12 || flag13))
                                {
                                    sourcePosition6 = connectPosition6;
                                    centerPosition3.xz += float8 * (num30 - num27);
                                    middleOffset2 = num30;
                                }
                                if ((sourcePosition6.m_RoadTypes & targetPosition6.m_RoadTypes) != 0)
                                {
                                    flag10 |= ((targetPosition6.m_LaneData.m_Flags & LaneFlags.Slave) != 0);
                                    PresetCurve(ref sourcePosition6, ref targetPosition6, middlePosition, centerPosition3, centerTangent2, middleOffset2, num * 0.5f / (float)num2, 0f, 2f);
                                    flag6 = (num29 > targetBuffer.Length - num7 || (flag11 && flag12 != flag13) || (!flag11 && flag13));
                                    isUnsafe = (isUnsafe && flag6);
                                    curviness = -1f;
                                    CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, ref isSkipped2, owner, laneBuffer, middleConnections, sourcePosition6, targetPosition6, intersectionFlags, laneGroup + 1, (ushort)num36, flag6,
                                        isForbidden: false, isTemp, trackOnly: false, 0, ownerTemp, isTurn: true, !m_LeftHandTraffic, isGentle: false, isUTurn: false, isRoundabout: true, a5, b5, flag6 && !m_LeftHandTraffic, flag6 && m_LeftHandTraffic,
                                        fixedTangents: true, roadPassThrough);
                                }
                            }
                        }
                        if (flag9)
                        {
                            sourcePosition6 = prevMainPosition;
                            if (!flag10 && num7 == 0)
                            {
                                targetPosition6 = connectPosition6;
                                targetPosition6.m_LaneData.m_Index = (byte)(num14 + 2);
                            }
                            else
                            {
                                targetPosition6 = connectPosition5;
                                targetPosition6.m_LaneData.m_Index = (byte)math.select(num14 + 1, num14 + 3, flag10);
                            }
                            targetPosition6.m_Tangent = -targetPosition6.m_Tangent;
                            PresetCurve(ref sourcePosition6, ref targetPosition6, middlePosition, float9, centerTangent, num27, 0f, num * 0.5f / (float)num2, 2f);
                            curviness = -1f;
                            CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, ref isSkipped, owner, laneBuffer, middleConnections, sourcePosition6, targetPosition6, intersectionFlags, laneGroup, 0, isUnsafe, isForbidden: false, isTemp,
                                trackOnly: false, yield3, ownerTemp, isTurn: true, !m_LeftHandTraffic, isGentle: false, isUTurn: false, isRoundabout: true, isLeftLimit: false, isRightLimit: false, isMergeLeft: false, isMergeRight: false,
                                fixedTangents: true, roadPassThrough);
                        }
                        if (flag10)
                        {
                            if (!flag9 && num6 == 0)
                            {
                                sourcePosition6 = connectPosition6;
                                sourcePosition6.m_LaneData.m_Index = (byte)(num14 + 2);
                            }
                            else
                            {
                                sourcePosition6 = connectPosition5;
                                sourcePosition6.m_LaneData.m_Index = (byte)math.select(num14 + 1, num14 + 3, flag9);
                            }
                            targetPosition6 = nextMainPosition;
                            PresetCurve(ref sourcePosition6, ref targetPosition6, middlePosition, float10, centerTangent2, num27, num * 0.5f / (float)num2, 0f, 2f);
                            curviness = -1f;
                            CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, ref isSkipped2, owner, laneBuffer, middleConnections, sourcePosition6, targetPosition6, intersectionFlags, laneGroup + 1, 0, isUnsafe, isForbidden: false,
                                isTemp, trackOnly: false, 0, ownerTemp, isTurn: true, !m_LeftHandTraffic, isGentle: false, isUTurn: false, isRoundabout: true, isLeftLimit: false, isRightLimit: false, isMergeLeft: false, isMergeRight: false,
                                fixedTangents: true, roadPassThrough);
                        }
                        laneGroup += 2u;
                    }
                    @float = float4;
                    connectPosition = connectPosition2;
                    connectPosition.m_Tangent = -connectPosition.m_Tangent;
                    laneCount = num12;
                }
            }

            private void PresetCurve(ref ConnectPosition sourcePosition, ref ConnectPosition targetPosition, float3 middlePosition, float3 centerPosition, float3 centerTangent, float middleOffset, float startAngle, float endAngle, float smoothness
            ) {
                Bezier4x3 bezier4x = NetUtils.FitCurve(sourcePosition.m_Position, sourcePosition.m_Tangent, centerTangent, centerPosition);
                Bezier4x3 bezier4x2 = NetUtils.FitCurve(centerPosition, centerTangent, -targetPosition.m_Tangent, targetPosition.m_Position);
                float2 @float = smoothness * new float2(0.425f, 0.075f);
                if (startAngle > 0f)
                {
                    float num = math.distance(targetPosition.m_Position.xz, middlePosition.xz) - middleOffset;
                    float rhs = math.max(math.distance(bezier4x.a.xz, bezier4x.b.xz) * 2f, middleOffset * math.tan(startAngle / 2f) - num * @float.y);
                    float rhs2 = math.max(math.distance(bezier4x2.d.xz, bezier4x2.c.xz) * smoothness, num * @float.x);
                    sourcePosition.m_Tangent = MathUtils.Normalize(sourcePosition.m_Tangent, sourcePosition.m_Tangent.xz) * rhs;
                    targetPosition.m_Tangent = MathUtils.Normalize(targetPosition.m_Tangent, targetPosition.m_Tangent.xz) * rhs2;
                }
                else if (endAngle > 0f)
                {
                    float num2 = math.distance(sourcePosition.m_Position.xz, middlePosition.xz) - middleOffset;
                    float rhs3 = math.max(math.distance(bezier4x.a.xz, bezier4x.b.xz) * smoothness, num2 * @float.x);
                    float rhs4 = math.max(math.distance(bezier4x2.d.xz, bezier4x2.c.xz) * 2f, middleOffset * math.tan(endAngle / 2f) - num2 * @float.y);
                    sourcePosition.m_Tangent = MathUtils.Normalize(sourcePosition.m_Tangent, sourcePosition.m_Tangent.xz) * rhs3;
                    targetPosition.m_Tangent = MathUtils.Normalize(targetPosition.m_Tangent, targetPosition.m_Tangent.xz) * rhs4;
                }
            }

            private void ExtractNextConnectPosition(ConnectPosition prevPosition, float3 middlePosition, NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer,
                out ConnectPosition nextPosition, out bool nextIsSource
            ) {
                float2 fromVector = math.normalizesafe(prevPosition.m_Position.xz - middlePosition.xz);
                float num = float.MaxValue;
                nextPosition = default(ConnectPosition);
                nextIsSource = false;
                int num2 = -1;
                int num3 = -1;
                if (sourceBuffer.Length + targetBuffer.Length == 1)
                {
                    if (sourceBuffer.Length == 1)
                    {
                        nextPosition = sourceBuffer[0];
                        num2 = 0;
                    }
                    else
                    {
                        nextPosition = targetBuffer[0];
                        num3 = 0;
                    }
                }
                else
                {
                    for (int i = 0; i < targetBuffer.Length; i++)
                    {
                        ConnectPosition connectPosition = targetBuffer[i];
                        if (connectPosition.m_GroupIndex != prevPosition.m_GroupIndex)
                        {
                            float2 toVector = math.normalizesafe(connectPosition.m_Position.xz - middlePosition.xz);
                            float num4 = m_LeftHandTraffic ? MathUtils.RotationAngleRight(fromVector, toVector) : MathUtils.RotationAngleLeft(fromVector, toVector);
                            if (num4 < num)
                            {
                                num = num4;
                                nextPosition = connectPosition;
                                num3 = i;
                            }
                        }
                    }
                    for (int j = 0; j < sourceBuffer.Length; j++)
                    {
                        ConnectPosition connectPosition2 = sourceBuffer[j];
                        if (connectPosition2.m_GroupIndex != prevPosition.m_GroupIndex)
                        {
                            float2 toVector2 = math.normalizesafe(connectPosition2.m_Position.xz - middlePosition.xz);
                            float num5 = m_LeftHandTraffic ? MathUtils.RotationAngleRight(fromVector, toVector2) : MathUtils.RotationAngleLeft(fromVector, toVector2);
                            if (num5 < num)
                            {
                                num = num5;
                                nextPosition = connectPosition2;
                                num2 = j;
                            }
                        }
                    }
                }
                if (num2 >= 0)
                {
                    sourceBuffer.RemoveAtSwapBack(num2);
                    nextIsSource = true;
                }
                else if (num3 >= 0)
                {
                    targetBuffer.RemoveAtSwapBack(num3);
                    nextIsSource = false;
                }
            }

            private bool GetRoundaboutLane(NativeList<ConnectPosition> buffer, float roundaboutSize, ref ConnectPosition roundaboutLane, ref ConnectPosition dedicatedLane, ref int laneCount, ref float laneWidth, ref bool isPublicOnly,
                ref float spaceForLanes, bool isSource, bool preferHighway, bool bicycleOnly)
            {
                bool flag = false;
                if (buffer.Length > 0)
                {
                    ConnectPosition connectPosition = buffer[0];
                    NetCompositionData compositionData = m_PrefabCompositionData[connectPosition.m_NodeComposition];
                    DynamicBuffer<NetCompositionPiece> pieces = m_PrefabCompositionPieces[connectPosition.m_EdgeComposition];
                    float2 @float = NetCompositionHelpers.CalculateRoundaboutSize(compositionData, pieces);
                    float num = connectPosition.m_IsEnd ? @float.y : @float.x;
                    float num2 = roundaboutSize - num;
                    int num3 = 0;
                    int num4 = -1;
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        ConnectPosition connectPosition2 = buffer[i];
                        if (!bicycleOnly && connectPosition2.m_RoadTypes == RoadTypes.Bicycle)
                        {
                            num4 = i;
                            continue;
                        }
                        Entity entity = connectPosition2.m_LaneData.m_Lane;
                        if ((connectPosition2.m_LaneData.m_Flags & LaneFlags.Track) != 0 && m_CarLaneData.HasComponent(entity))
                        {
                            CarLaneData carLaneData = m_CarLaneData[entity];
                            if (carLaneData.m_NotTrackLanePrefab != Entity.Null)
                            {
                                entity = carLaneData.m_NotTrackLanePrefab;
                            }
                        }
                        NetLaneData netLaneData = m_NetLaneData[entity];
                        num2 = ((!isSource) ? math.max(num2, netLaneData.m_Width * 1.33333337f) : (num2 + netLaneData.m_Width * 1.33333337f));
                        if ((connectPosition2.m_CompositionData.m_RoadFlags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0 == preferHighway)
                        {
                            bool flag2 = (netLaneData.m_Flags & LaneFlags.PublicOnly) != 0;
                            if ((isPublicOnly && !flag2) | ((isPublicOnly == flag2) & (netLaneData.m_Width < laneWidth)))
                            {
                                laneWidth = netLaneData.m_Width;
                                isPublicOnly = flag2;
                                roundaboutLane = connectPosition2;
                            }
                        }
                        num3++;
                    }
                    num3 = math.select(1, num3, isSource || num3 == 0);
                    flag = (num3 > laneCount);
                    laneCount = math.max(laneCount, num3);
                    if (num4 != -1 && (flag || dedicatedLane.m_LaneData.m_Lane == Entity.Null))
                    {
                        dedicatedLane = buffer[num4];
                    }
                    if (num3 != 0)
                    {
                        spaceForLanes = math.min(spaceForLanes, num2);
                    }
                }
                return flag;
            }

            private unsafe void FillOldLaneBuffer(bool isEdge, bool isNode, Entity owner, DynamicBuffer<SubLane> lanes, NativeHashMap<LaneKey, Entity> laneBuffer)
            {
                StackList<PathNode> stackList = stackalloc PathNode[256];
                StackList<PathNode> stackList2 = stackalloc PathNode[256];
                byte* tempArray = stackalloc byte[256];
                StackList<bool> stackList3 = new Span<bool>(tempArray, 256);
                if (isNode)
                {
                    EdgeIterator edgeIterator = new EdgeIterator(Entity.Null, owner, m_Edges, m_EdgeData, m_TempData, m_HiddenData);
                    EdgeIteratorValue value;
                    while (edgeIterator.GetNext(out value))
                    {
                        if (!m_SubLanes.TryGetBuffer(value.m_Edge, out DynamicBuffer<SubLane> bufferData))
                        {
                            continue;
                        }
                        float rhs = math.select(0f, 1f, value.m_End);
                        int num = math.select(0, 4, value.m_End);
                        for (int i = 0; i < bufferData.Length; i++)
                        {
                            Entity subLane = bufferData[i].m_SubLane;
                            if ((bufferData[i].m_PathMethods & (PathMethod.Pedestrian | PathMethod.Road | PathMethod.Bicycle)) == 0 || m_SecondaryLaneData.HasComponent(subLane) || !m_EdgeLaneData.TryGetComponent(subLane, out EdgeLane componentData))
                            {
                                continue;
                            }
                            bool2 x = componentData.m_EdgeDelta == rhs;
                            if (math.any(x))
                            {
                                Lane lane = m_LaneData[subLane];
                                PathNode pathNode = x.x ? lane.m_StartNode : lane.m_EndNode;
                                PathNode middleNode = lane.m_MiddleNode;
                                middleNode.SetSegmentIndex((byte)num);
                                if (!middleNode.Equals(pathNode) && stackList.Length < stackList.Capacity)
                                {
                                    pathNode.SetOwner(owner);
                                    stackList.AddNoResize(pathNode);
                                    stackList2.AddNoResize(middleNode);
                                    stackList3.AddNoResize(x.y);
                                }
                            }
                        }
                    }
                }
                for (int j = 0; j < lanes.Length; j++)
                {
                    Entity subLane2 = lanes[j].m_SubLane;
                    if (m_SecondaryLaneData.HasComponent(subLane2))
                    {
                        continue;
                    }
                    LaneFlags laneFlags = (LaneFlags)0;
                    if (m_MasterLaneData.HasComponent(subLane2))
                    {
                        laneFlags |= LaneFlags.Master;
                    }
                    if (m_SlaveLaneData.HasComponent(subLane2))
                    {
                        laneFlags |= LaneFlags.Slave;
                    }
                    Lane lane2 = m_LaneData[subLane2];
                    if (isEdge)
                    {
                        if ((lanes[j].m_PathMethods & (PathMethod.Pedestrian | PathMethod.Road | PathMethod.Track | PathMethod.Bicycle)) != 0 && m_EdgeLaneData.TryGetComponent(subLane2, out EdgeLane componentData2))
                        {
                            bool4 @bool = componentData2.m_EdgeDelta.xyxy == new float4(0f, 0f, 1f, 1f);
                            if (@bool.x | @bool.z)
                            {
                                lane2.m_StartNode = lane2.m_MiddleNode;
                                lane2.m_StartNode.SetSegmentIndex((byte)math.select(0, 4, @bool.z));
                            }
                            if (@bool.y | @bool.w)
                            {
                                lane2.m_EndNode = lane2.m_MiddleNode;
                                lane2.m_EndNode.SetSegmentIndex((byte)math.select(0, 4, @bool.w));
                            }
                        }
                    }
                    else if (isNode)
                    {
                        if ((lanes[j].m_PathMethods & PathMethod.Pedestrian) != 0)
                        {
                            for (int k = 0; k < stackList.Length; k++)
                            {
                                if (stackList[k].Equals(lane2.m_StartNode))
                                {
                                    lane2.m_StartNode = new PathNode(lane2.m_StartNode, 0.5f);
                                }
                                else if (stackList[k].Equals(lane2.m_EndNode))
                                {
                                    lane2.m_EndNode = new PathNode(lane2.m_EndNode, 0.5f);
                                }
                            }
                        }
                        else if ((lanes[j].m_PathMethods & (PathMethod.Road | PathMethod.Bicycle)) != 0)
                        {
                            for (int l = 0; l < stackList.Length; l++)
                            {
                                if (stackList[l].Equals(lane2.m_StartNode) && stackList3[l])
                                {
                                    lane2.m_StartNode = stackList2[l];
                                }
                                else if (stackList[l].Equals(lane2.m_EndNode) && !stackList3[l])
                                {
                                    lane2.m_EndNode = stackList2[l];
                                }
                            }
                        }
                    }
                    LaneKey key = new LaneKey(lane2, m_PrefabRefData[subLane2].m_Prefab, laneFlags);
                    laneBuffer.TryAdd(key, subLane2);
                }
            }

            private void RemoveUnusedOldLanes(int jobIndex, Entity owner, DynamicBuffer<SubLane> lanes, NativeHashMap<LaneKey, Entity> laneBuffer)
            {
                StackList<Entity> stackList = stackalloc Entity[lanes.Length];
                NativeHashMap<LaneKey, Entity>.Enumerator enumerator = laneBuffer.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    stackList.AddNoResize(enumerator.Current.Value);
                }
                enumerator.Dispose();
                laneBuffer.Clear();
                if (stackList.Length != 0)
                {
                    m_CommandBuffer.RemoveComponent(jobIndex, stackList.AsArray(), in m_AppliedTypes);
                    m_CommandBuffer.AddComponent<Deleted>(jobIndex, stackList.AsArray());
                }
            }

            private void UpdateLanes(int jobIndex, NativeList<Entity> laneBuffer)
            {
                if (laneBuffer.Length != 0)
                {
                    m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, laneBuffer.AsArray());
                    m_CommandBuffer.AddComponent<Updated>(jobIndex, laneBuffer.AsArray());
                }
            }

            private void CreateEdgeConnectionLanes(int jobIndex, ref int edgeLaneIndex, ref int connectionIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, NativeList<ConnectPosition> sourceBuffer,
                NativeList<ConnectPosition> targetBuffer, NativeList<ConnectPosition> tempBuffer1, NativeList<ConnectPosition> tempBuffer2, Entity composition, EdgeGeometry geometryData, Curve curve, bool isSingleCurve, bool isTemp,
                Temp ownerTemp
            ) {
                NetCompositionData prefabCompositionData = m_PrefabCompositionData[composition];
                CompositionData compositionData = GetCompositionData(composition);
                DynamicBuffer<NetCompositionLane> prefabCompositionLanes = m_PrefabCompositionLanes[composition];
                int num = -1;
                for (int i = 0; i < sourceBuffer.Length; i++)
                {
                    ConnectPosition connectPosition = sourceBuffer[i];
                    if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Road) == 0 && (connectPosition.m_RoadTypes & RoadTypes.Bicycle) == 0)
                    {
                        continue;
                    }
                    float3 rhs = MathUtils.Position(curve.m_Bezier, connectPosition.m_CurvePosition);
                    float2 value = MathUtils.Right(MathUtils.Tangent(curve.m_Bezier, connectPosition.m_CurvePosition).xz);
                    MathUtils.TryNormalize(ref value);
                    float3 @float = connectPosition.m_Position - rhs;
                    @float.x = math.dot(value, @float.xz);
                    float num2 = connectPosition.m_LaneData.m_Position.y + connectPosition.m_Elevation;
                    if (m_ElevationData.HasComponent(owner))
                    {
                        num2 = ((!(@float.x > 0f)) ? (num2 - m_ElevationData[owner].m_Elevation.x) : (num2 - m_ElevationData[owner].m_Elevation.y));
                    }
                    int num3 = FindBestConnectionLane(prefabCompositionLanes, @float.xy, num2, LaneFlags.Road, connectPosition.m_LaneData.m_Flags);
                    if (num3 != -1)
                    {
                        if (connectPosition.m_GroupIndex != num)
                        {
                            num = connectPosition.m_GroupIndex;
                        }
                        else
                        {
                            connectionIndex--;
                        }
                        CreateCarEdgeConnections(jobIndex, ref edgeLaneIndex, ref random, owner, laneBuffer, geometryData, prefabCompositionData, compositionData, connectPosition, @float.xy,
                            connectionIndex++, isSingleCurve, isSource: true, isTemp, ownerTemp, prefabCompositionLanes, num3);
                    }
                }
                num = -1;
                for (int j = 0; j < targetBuffer.Length; j++)
                {
                    ConnectPosition connectPosition2 = targetBuffer[j];
                    float3 rhs2 = MathUtils.Position(curve.m_Bezier, connectPosition2.m_CurvePosition);
                    float2 value2 = MathUtils.Right(MathUtils.Tangent(curve.m_Bezier, connectPosition2.m_CurvePosition).xz);
                    MathUtils.TryNormalize(ref value2);
                    float3 float2 = connectPosition2.m_Position - rhs2;
                    float2.x = math.dot(value2, float2.xz);
                    float num4 = connectPosition2.m_LaneData.m_Position.y + connectPosition2.m_Elevation;
                    if (m_ElevationData.HasComponent(owner))
                    {
                        num4 = ((!(float2.x > 0f)) ? (num4 - m_ElevationData[owner].m_Elevation.x) : (num4 - m_ElevationData[owner].m_Elevation.y));
                    }
                    if ((connectPosition2.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                    {
                        int num5 = FindBestConnectionLane(prefabCompositionLanes, float2.xy, num4, LaneFlags.Pedestrian, connectPosition2.m_LaneData.m_Flags);
                        if (num5 != -1)
                        {
                            NetCompositionLane prefabCompositionLaneData = prefabCompositionLanes[num5];
                            CreateEdgeConnectionLane(jobIndex, ref edgeLaneIndex, ref random, owner, laneBuffer, geometryData.m_Start, geometryData.m_End, prefabCompositionData, compositionData,
                                prefabCompositionLaneData, connectPosition2, connectionIndex++, isSingleCurve, useGroundPosition: false, isSource: false, isTemp, ownerTemp);
                        }
                    }
                    if ((connectPosition2.m_LaneData.m_Flags & LaneFlags.Road) == 0 && (connectPosition2.m_RoadTypes & RoadTypes.Bicycle) == 0)
                    {
                        continue;
                    }
                    int num6 = FindBestConnectionLane(prefabCompositionLanes, float2.xy, num4, LaneFlags.Road, connectPosition2.m_LaneData.m_Flags);
                    if (num6 != -1)
                    {
                        if (connectPosition2.m_GroupIndex != num)
                        {
                            num = connectPosition2.m_GroupIndex;
                        }
                        else
                        {
                            connectionIndex--;
                        }
                        CreateCarEdgeConnections(jobIndex, ref edgeLaneIndex, ref random, owner, laneBuffer, geometryData, prefabCompositionData, compositionData, connectPosition2, float2.xy,
                            connectionIndex++, isSingleCurve, isSource: false, isTemp, ownerTemp, prefabCompositionLanes, num6);
                    }
                }
                UtilityTypes utilityTypes = FilterUtilityConnectPositions(targetBuffer, tempBuffer1);
                UtilityTypes utilityTypes2 = UtilityTypes.WaterPipe;
                while (utilityTypes != 0)
                {
                    if ((utilityTypes & utilityTypes2) != 0)
                    {
                        utilityTypes = (UtilityTypes)((uint)utilityTypes & (uint)(byte)(~(int)utilityTypes2));
                        FilterUtilityConnectPositions(utilityTypes2, tempBuffer1, tempBuffer2);
                        if (tempBuffer2.Length != 0)
                        {
                            int4 @int = -1;
                            int4 int2 = -1;
                            for (int k = 0; k < tempBuffer2.Length; k++)
                            {
                                ConnectPosition connectPosition3 = tempBuffer2[k];
                                if ((m_NetLaneData[connectPosition3.m_LaneData.m_Lane].m_Flags & LaneFlags.Underground) != 0)
                                {
                                    int2.xy = math.select(int2.xy, k, new bool2(int2.x == -1, k > int2.y));
                                    if (@int.x != -1)
                                    {
                                        break;
                                    }
                                }
                                else
                                {
                                    @int.xy = math.select(@int.xy, k, new bool2(@int.x == -1, k > @int.y));
                                    if (int2.x != -1)
                                    {
                                        break;
                                    }
                                }
                            }
                            for (int l = 0; l < prefabCompositionLanes.Length; l++)
                            {
                                NetCompositionLane netCompositionLane = prefabCompositionLanes[l];
                                if ((netCompositionLane.m_Flags & LaneFlags.Utility) == 0 || (m_UtilityLaneData[netCompositionLane.m_Lane].m_UtilityTypes & utilityTypes2) == 0)
                                {
                                    continue;
                                }
                                if ((m_NetLaneData[netCompositionLane.m_Lane].m_Flags & LaneFlags.Underground) != 0)
                                {
                                    int2.zw = math.select(int2.zw, l, new bool2(int2.z == -1, l > int2.w));
                                    if (@int.z != -1)
                                    {
                                        break;
                                    }
                                }
                                else
                                {
                                    @int.zw = math.select(@int.zw, l, new bool2(@int.z == -1, l > @int.w));
                                    if (int2.z != -1)
                                    {
                                        break;
                                    }
                                }
                            }
                            @int = math.select(@int, int2, (@int == -1) | (math.any(@int == -1) & math.all(int2 != -1)));
                            if (math.all(@int.xz != -1))
                            {
                                ConnectPosition connectPosition4 = tempBuffer2[@int.x];
                                NetLaneData netLaneData = m_NetLaneData[connectPosition4.m_LaneData.m_Lane];
                                UtilityLaneData utilityLaneData = m_UtilityLaneData[connectPosition4.m_LaneData.m_Lane];
                                NetCompositionLane prefabCompositionLaneData2 = prefabCompositionLanes[@int.z];
                                UtilityLaneData utilityLaneData2 = m_UtilityLaneData[prefabCompositionLaneData2.m_Lane];
                                NetLaneData netLaneData2 = m_NetLaneData[prefabCompositionLaneData2.m_Lane];
                                bool useGroundPosition = false;
                                if (((netLaneData.m_Flags ^ netLaneData2.m_Flags) & LaneFlags.Underground) != 0)
                                {
                                    if ((netLaneData.m_Flags & LaneFlags.Underground) != 0)
                                    {
                                        useGroundPosition = true;
                                        connectPosition4.m_LaneData.m_Lane = GetConnectionLanePrefab(connectPosition4.m_LaneData.m_Lane, utilityLaneData,
                                            utilityLaneData2.m_VisualCapacity < utilityLaneData.m_VisualCapacity, wantLarger: false);
                                    }
                                    else
                                    {
                                        GetGroundPosition(connectPosition4.m_Owner, math.select(0f, 1f, connectPosition4.m_IsEnd), ref connectPosition4.m_Position);
                                        connectPosition4.m_LaneData.m_Lane = GetConnectionLanePrefab(prefabCompositionLaneData2.m_Lane, utilityLaneData2,
                                            utilityLaneData.m_VisualCapacity < utilityLaneData2.m_VisualCapacity, utilityLaneData.m_VisualCapacity > utilityLaneData2.m_VisualCapacity);
                                    }
                                }
                                else
                                {
                                    @int.y++;
                                    connectPosition4.m_Position = CalculateUtilityConnectPosition(tempBuffer2, @int.xy);
                                    if (utilityLaneData.m_VisualCapacity < utilityLaneData2.m_VisualCapacity)
                                    {
                                        connectPosition4.m_LaneData.m_Lane = GetConnectionLanePrefab(connectPosition4.m_LaneData.m_Lane, utilityLaneData, wantSmaller: false, wantLarger: false);
                                    }
                                    else
                                    {
                                        connectPosition4.m_LaneData.m_Lane = GetConnectionLanePrefab(prefabCompositionLaneData2.m_Lane, utilityLaneData2, wantSmaller: false,
                                            utilityLaneData.m_VisualCapacity > utilityLaneData2.m_VisualCapacity);
                                    }
                                }
                                CreateEdgeConnectionLane(jobIndex, ref edgeLaneIndex, ref random, owner, laneBuffer, geometryData.m_Start, geometryData.m_End, prefabCompositionData, compositionData,
                                    prefabCompositionLaneData2, connectPosition4, connectionIndex++, isSingleCurve, useGroundPosition, isSource: false, isTemp, ownerTemp);
                            }
                            tempBuffer2.Clear();
                        }
                    }
                    utilityTypes2 = (UtilityTypes)((uint)utilityTypes2 << 1);
                }
                tempBuffer1.Clear();
            }

            private void GetGroundPosition(Entity entity, float curvePosition, ref float3 position) {
                if (m_NodeData.HasComponent(entity))
                {
                    position = m_NodeData[entity].m_Position;
                }
                else if (m_CurveData.HasComponent(entity))
                {
                    position = MathUtils.Position(m_CurveData[entity].m_Bezier, curvePosition);
                }
                if (m_ElevationData.HasComponent(entity))
                {
                    position.y -= math.csum(m_ElevationData[entity].m_Elevation) * 0.5f;
                }
            }

            private Entity GetConnectionLanePrefab(Entity lanePrefab, UtilityLaneData utilityLaneData, bool wantSmaller, bool wantLarger) {
                Entity entity = lanePrefab;
                if (m_UtilityLaneData.TryGetComponent(utilityLaneData.m_LocalConnectionPrefab, out UtilityLaneData componentData))
                {
                    if ((wantSmaller && componentData.m_VisualCapacity < utilityLaneData.m_VisualCapacity) || (wantLarger && componentData.m_VisualCapacity > utilityLaneData.m_VisualCapacity) ||
                        (!wantSmaller && !wantLarger && componentData.m_VisualCapacity == utilityLaneData.m_VisualCapacity))
                    {
                        return utilityLaneData.m_LocalConnectionPrefab;
                    }
                    if (componentData.m_VisualCapacity == utilityLaneData.m_VisualCapacity)
                    {
                        entity = utilityLaneData.m_LocalConnectionPrefab;
                    }
                }
                if (m_UtilityLaneData.TryGetComponent(utilityLaneData.m_LocalConnectionPrefab2, out componentData))
                {
                    if ((wantSmaller && componentData.m_VisualCapacity < utilityLaneData.m_VisualCapacity) || (wantLarger && componentData.m_VisualCapacity > utilityLaneData.m_VisualCapacity) ||
                        (!wantSmaller && !wantLarger && componentData.m_VisualCapacity == utilityLaneData.m_VisualCapacity))
                    {
                        return utilityLaneData.m_LocalConnectionPrefab2;
                    }
                    if (entity == lanePrefab && componentData.m_VisualCapacity == utilityLaneData.m_VisualCapacity)
                    {
                        entity = utilityLaneData.m_LocalConnectionPrefab2;
                    }
                }
                return entity;
            }

            private void FindAnchors(Entity owner, NativeParallelHashSet<Entity> anchorPrefabs) {
                if (!m_TransformData.HasComponent(owner))
                {
                    return;
                }
                PrefabRef prefabRef = m_PrefabRefData[owner];
                if (m_PrefabSubLanes.HasBuffer(prefabRef.m_Prefab))
                {
                    DynamicBuffer<Game.Prefabs.SubLane> dynamicBuffer = m_PrefabSubLanes[prefabRef.m_Prefab];
                    for (int i = 0; i < dynamicBuffer.Length; i++)
                    {
                        Game.Prefabs.SubLane subLane = dynamicBuffer[i];
                        anchorPrefabs.Add(subLane.m_Prefab);
                    }
                }
            }

            private void FindAnchors(Entity owner, float order, NativeList<LaneAnchor> anchors) {
                if (!m_TransformData.HasComponent(owner))
                {
                    return;
                }
                PrefabRef prefabRef = m_PrefabRefData[owner];
                Game.Objects.Transform transform = m_TransformData[owner];
                if (!m_PrefabSubLanes.HasBuffer(prefabRef.m_Prefab))
                {
                    return;
                }
                DynamicBuffer<Game.Prefabs.SubLane> dynamicBuffer = m_PrefabSubLanes[prefabRef.m_Prefab];
                for (int i = 0; i < dynamicBuffer.Length; i++)
                {
                    Game.Prefabs.SubLane subLane = dynamicBuffer[i];
                    LaneAnchor value;
                    if (subLane.m_NodeIndex.x != subLane.m_NodeIndex.y)
                    {
                        value = new LaneAnchor
                        {
                            m_Prefab = subLane.m_Prefab,
                            m_Position = ObjectUtils.LocalToWorld(transform, subLane.m_Curve.a),
                            m_Order = order + 1f,
                            m_PathNode = new PathNode(owner, (ushort)subLane.m_NodeIndex.x)
                        };
                        anchors.Add(in value);
                        value = new LaneAnchor
                        {
                            m_Prefab = subLane.m_Prefab,
                            m_Position = ObjectUtils.LocalToWorld(transform, subLane.m_Curve.d),
                            m_Order = order + 1f,
                            m_PathNode = new PathNode(owner, (ushort)subLane.m_NodeIndex.y)
                        };
                        anchors.Add(in value);
                    }
                    else
                    {
                        value = new LaneAnchor
                        {
                            m_Prefab = subLane.m_Prefab,
                            m_Position = ObjectUtils.LocalToWorld(transform, subLane.m_Curve.a),
                            m_Order = order,
                            m_PathNode = new PathNode(owner, (ushort)subLane.m_NodeIndex.x)
                        };
                        anchors.Add(in value);
                    }
                }
            }

            private bool IsAnchored(Entity owner, ref NativeParallelHashSet<Entity> anchorPrefabs, Entity prefab) {
                if (!anchorPrefabs.IsCreated)
                {
                    anchorPrefabs = new NativeParallelHashSet<Entity>(8, Allocator.Temp);
                    if (m_SubObjects.HasBuffer(owner))
                    {
                        DynamicBuffer<Game.Objects.SubObject> dynamicBuffer = m_SubObjects[owner];
                        for (int i = 0; i < dynamicBuffer.Length; i++)
                        {
                            FindAnchors(dynamicBuffer[i].m_SubObject, anchorPrefabs);
                        }
                    }
                    if (m_OwnerData.HasComponent(owner))
                    {
                        FindAnchors(m_OwnerData[owner].m_Owner, anchorPrefabs);
                    }
                }
                return anchorPrefabs.Contains(prefab);
            }

            private void FindAnchors(Entity node, NativeList<LaneAnchor> anchors, NativeList<LaneAnchor> tempBuffer) {
                if (m_SubObjects.HasBuffer(node))
                {
                    DynamicBuffer<Game.Objects.SubObject> dynamicBuffer = m_SubObjects[node];
                    for (int i = 0; i < dynamicBuffer.Length; i++)
                    {
                        FindAnchors(dynamicBuffer[i].m_SubObject, 0f, tempBuffer);
                    }
                }
                if (m_OwnerData.HasComponent(node))
                {
                    FindAnchors(m_OwnerData[node].m_Owner, 2f, tempBuffer);
                }
                if (tempBuffer.Length == 0)
                {
                    return;
                }
                if (anchors.Length > 1)
                {
                    anchors.Sort();
                }
                if (tempBuffer.Length > 1)
                {
                    tempBuffer.Sort();
                }
                int num = 0;
                int num2 = 0;
                while (num < anchors.Length && num2 < tempBuffer.Length)
                {
                    LaneAnchor laneAnchor = anchors[num];
                    LaneAnchor laneAnchor2 = tempBuffer[num2];
                    while (laneAnchor.m_Prefab.Index != laneAnchor2.m_Prefab.Index)
                    {
                        if (laneAnchor.m_Prefab.Index < laneAnchor2.m_Prefab.Index)
                        {
                            if (++num >= anchors.Length)
                            {
                                goto end_IL_03f5;
                            }
                            laneAnchor = anchors[num];
                        }
                        else
                        {
                            if (++num2 >= tempBuffer.Length)
                            {
                                goto end_IL_03f5;
                            }
                            laneAnchor2 = tempBuffer[num2];
                        }
                    }
                    float3 position = laneAnchor.m_Position;
                    float3 position2 = laneAnchor.m_Position;
                    int j;
                    for (j = num + 1; j < anchors.Length; j++)
                    {
                        LaneAnchor laneAnchor3 = anchors[j];
                        if (laneAnchor3.m_Prefab != laneAnchor.m_Prefab)
                        {
                            break;
                        }
                        position += laneAnchor3.m_Position;
                        position2 = laneAnchor3.m_Position;
                    }
                    position /= (float)(j - num);
                    int k;
                    for (k = num2 + 1; k < tempBuffer.Length && !(tempBuffer[k].m_Prefab != laneAnchor2.m_Prefab); k++)
                    {
                    }
                    int num3 = j - num;
                    int num4 = k - num2;
                    if (num4 > num3)
                    {
                        for (int l = num2; l < k; l++)
                        {
                            LaneAnchor value = tempBuffer[l];
                            value.m_Order = value.m_Order * 10000f + math.distance(value.m_Position, position);
                            tempBuffer[l] = value;
                        }
                        tempBuffer.AsArray().GetSubArray(num2, num4).Sort();
                        num4 = num3;
                    }
                    if (num4 > 1)
                    {
                        float3 y = position2 - laneAnchor.m_Position;
                        int num5 = num2 + num4;
                        for (int m = num2; m < num5; m++)
                        {
                            LaneAnchor value2 = tempBuffer[m];
                            value2.m_Order = math.dot(value2.m_Position - position, y);
                            tempBuffer[m] = value2;
                        }
                        tempBuffer.AsArray().GetSubArray(num2, num4).Sort();
                        float num6 = (tempBuffer[num5 - 1].m_Order - tempBuffer[num2].m_Order) * 0.01f;
                        int num7 = num2;
                        while (num7 < num5)
                        {
                            LaneAnchor laneAnchor4 = tempBuffer[num7];
                            int n;
                            for (n = num7 + 1; n < num5 && !(tempBuffer[n].m_Order - laneAnchor4.m_Order >= num6); n++)
                            {
                            }
                            if (n > num7 + 1)
                            {
                                y = anchors[num + n - num2 - 1].m_Position - anchors[num + num7 - num2].m_Position;
                                for (int num8 = num7; num8 < n; num8++)
                                {
                                    LaneAnchor value3 = tempBuffer[num8];
                                    value3.m_Order = math.dot(value3.m_Position - position, y);
                                    tempBuffer[num8] = value3;
                                }
                                tempBuffer.AsArray().GetSubArray(num7, n - num7).Sort();
                            }
                            num7 = n;
                        }
                    }
                    for (int num9 = 0; num9 < num4; num9++)
                    {
                        anchors[num + num9] = tempBuffer[num2 + num9];
                    }
                    num = j;
                    num2 = k;
                    continue;
                    end_IL_03f5:
                    break;
                }
                tempBuffer.Clear();
            }

            private void CreateEdgeLanes(int jobIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, Composition composition, Edge edge, EdgeGeometry geometryData,
                Segment combinedSegment, bool isSingleCurve, bool isTemp, Temp ownerTemp
            ) {
                NetCompositionData prefabCompositionData = m_PrefabCompositionData[composition.m_Edge];
                CompositionData compositionData = GetCompositionData(composition.m_Edge);
                DynamicBuffer<NetCompositionLane> prefabCompositionLanes = m_PrefabCompositionLanes[composition.m_Edge];
                NativeList<LaneAnchor> nativeList = default(NativeList<LaneAnchor>);
                NativeList<LaneAnchor> nativeList2 = default(NativeList<LaneAnchor>);
                NativeList<LaneAnchor> tempBuffer = default(NativeList<LaneAnchor>);
                for (int i = 0; i < prefabCompositionLanes.Length; i++)
                {
                    NetCompositionLane netCompositionLane = prefabCompositionLanes[i];
                    if ((netCompositionLane.m_Flags & LaneFlags.FindAnchor) != 0)
                    {
                        if (!nativeList.IsCreated)
                        {
                            nativeList = new NativeList<LaneAnchor>(8, Allocator.Temp);
                        }
                        if (!nativeList2.IsCreated)
                        {
                            nativeList2 = new NativeList<LaneAnchor>(8, Allocator.Temp);
                        }
                        if (!tempBuffer.IsCreated)
                        {
                            tempBuffer = new NativeList<LaneAnchor>(8, Allocator.Temp);
                        }
                        LaneAnchor laneAnchor = default(LaneAnchor);
                        laneAnchor.m_Prefab = netCompositionLane.m_Lane;
                        laneAnchor.m_Order = i;
                        laneAnchor.m_PathNode = new PathNode(owner, netCompositionLane.m_Index, 0);
                        LaneAnchor value = laneAnchor;
                        laneAnchor = default(LaneAnchor);
                        laneAnchor.m_Prefab = netCompositionLane.m_Lane;
                        laneAnchor.m_Order = i;
                        laneAnchor.m_PathNode = new PathNode(owner, netCompositionLane.m_Index, 4);
                        LaneAnchor value2 = laneAnchor;
                        float t = netCompositionLane.m_Position.x / math.max(1f, prefabCompositionData.m_Width) + 0.5f;
                        value.m_Position = math.lerp(geometryData.m_Start.m_Left.a, geometryData.m_Start.m_Right.a, t);
                        value2.m_Position = math.lerp(geometryData.m_End.m_Left.d, geometryData.m_End.m_Right.d, t);
                        if (netCompositionLane.m_Position.z > 0.001f)
                        {
                            float y = math.distance(value.m_Position, value2.m_Position);
                            float3 @float = (value2.m_Position - value.m_Position) * (netCompositionLane.m_Position.z / math.max(netCompositionLane.m_Position.z * 4f, y));
                            value.m_Position += @float;
                            value2.m_Position -= @float;
                        }
                        value.m_Position.y += netCompositionLane.m_Position.y;
                        value2.m_Position.y += netCompositionLane.m_Position.y;
                        nativeList.Add(in value);
                        nativeList2.Add(in value2);
                    }
                }
                if (nativeList.IsCreated)
                {
                    FindAnchors(edge.m_Start, nativeList, tempBuffer);
                }
                if (nativeList2.IsCreated)
                {
                    FindAnchors(edge.m_End, nativeList2, tempBuffer);
                }
                bool hasAuxiliaryLanes = false;
                if (isSingleCurve)
                {
                    for (int j = 0; j < prefabCompositionLanes.Length; j++)
                    {
                        NetCompositionLane prefabCompositionLaneData = prefabCompositionLanes[j];
                        hasAuxiliaryLanes |= ((prefabCompositionLaneData.m_Flags & LaneFlags.HasAuxiliary) != 0);
                        CreateEdgeLane(jobIndex, ref random, owner, laneBuffer, combinedSegment, prefabCompositionData, compositionData, prefabCompositionLanes, prefabCompositionLaneData, new int2(0, 4), new float2(0f, 1f),
                            nativeList, nativeList2, true, isTemp, ownerTemp);
                    }
                }
                else
                {
                    for (int k = 0; k < prefabCompositionLanes.Length; k++)
                    {
                        NetCompositionLane prefabCompositionLaneData2 = prefabCompositionLanes[k];
                        hasAuxiliaryLanes |= ((prefabCompositionLaneData2.m_Flags & LaneFlags.HasAuxiliary) != 0);
                        CreateEdgeLane(jobIndex, ref random, owner, laneBuffer, geometryData.m_Start, prefabCompositionData, compositionData, prefabCompositionLanes, prefabCompositionLaneData2, new int2(0, 2),
                            new float2(0f, 0.5f), nativeList, nativeList2, new bool2(x: true, y: false), isTemp, ownerTemp);
                        CreateEdgeLane(jobIndex, ref random, owner, laneBuffer, geometryData.m_End, prefabCompositionData, compositionData, prefabCompositionLanes, prefabCompositionLaneData2, new int2(2, 4),
                            new float2(0.5f, 1f), nativeList, nativeList2, new bool2(x: false, y: true), isTemp, ownerTemp);
                    }
                }
                if (hasAuxiliaryLanes)
                {
                    float2 float11 = default(float2);
                    for (int l = 0; l < prefabCompositionLanes.Length; l++)
                    {
                        NetCompositionLane netCompositionLane2 = prefabCompositionLanes[l];
                        if ((netCompositionLane2.m_Flags & LaneFlags.HasAuxiliary) == 0)
                        {
                            continue;
                        }
                        DynamicBuffer<AuxiliaryNetLane> dynamicBuffer = m_PrefabAuxiliaryLanes[netCompositionLane2.m_Lane];
                        int num = 5;
                        for (int m = 0; m < dynamicBuffer.Length; m++)
                        {
                            AuxiliaryNetLane auxiliaryNetLane = dynamicBuffer[m];
                            if (!NetCompositionHelpers.TestLaneFlags(auxiliaryNetLane, prefabCompositionData.m_Flags))
                            {
                                continue;
                            }
                            bool flag2 = false;
                            bool flag3 = false;
                            float3 float2 = 0f;
                            if (auxiliaryNetLane.m_Spacing.x > 0.1f)
                            {
                                for (int n = 0; n < prefabCompositionLanes.Length; n++)
                                {
                                    NetCompositionLane netCompositionLane3 = prefabCompositionLanes[n];
                                    if ((netCompositionLane3.m_Flags & LaneFlags.HasAuxiliary) == 0)
                                    {
                                        continue;
                                    }
                                    for (int num2 = 0; num2 < dynamicBuffer.Length; num2++)
                                    {
                                        AuxiliaryNetLane lane = dynamicBuffer[num2];
                                        if (NetCompositionHelpers.TestLaneFlags(lane, prefabCompositionData.m_Flags) && !(lane.m_Prefab != auxiliaryNetLane.m_Prefab) && !(lane.m_Spacing.x <= 0.1f) &&
                                            (n != l || num2 != m))
                                        {
                                            if (n < l || (n == l && num2 < m))
                                            {
                                                flag3 = true;
                                                float2 = netCompositionLane3.m_Position;
                                                float2.xy += lane.m_Position.xy;
                                            }
                                            else
                                            {
                                                flag2 = true;
                                            }
                                        }
                                    }
                                }
                            }
                            float num3 = geometryData.m_Start.middleLength + geometryData.m_End.middleLength;
                            float num4 = 0f;
                            int num5 = 0;
                            float3 float3 = default(float3);
                            float3 float4 = default(float3);
                            float3 float5 = default(float3);
                            float3 float6 = default(float3);
                            float3 float7 = default(float3);
                            float3 float8 = default(float3);
                            float3 float9 = default(float3);
                            float3 float10 = default(float3);
                            NetCompositionLane prefabCompositionLaneData3 = netCompositionLane2;
                            prefabCompositionLaneData3.m_Lane = auxiliaryNetLane.m_Prefab;
                            prefabCompositionLaneData3.m_Position.xy += auxiliaryNetLane.m_Position.xy;
                            prefabCompositionLaneData3.m_Flags = (m_NetLaneData[auxiliaryNetLane.m_Prefab].m_Flags | auxiliaryNetLane.m_Flags);
                            if ((auxiliaryNetLane.m_Flags & LaneFlags.EvenSpacing) != 0)
                            {
                                NetCompositionData compositionData2 = m_PrefabCompositionData[composition.m_StartNode];
                                NetCompositionData compositionData3 = m_PrefabCompositionData[composition.m_EndNode];
                                EdgeNodeGeometry geometry = m_StartNodeGeometryData[owner].m_Geometry;
                                EdgeNodeGeometry geometry2 = m_EndNodeGeometryData[owner].m_Geometry;
                                if (!NetCompositionHelpers.TestLaneFlags(auxiliaryNetLane, compositionData2.m_Flags))
                                {
                                    float x = (geometry.m_Left.middleLength + geometry.m_Right.middleLength) * 0.5f;
                                    x = math.min(x, auxiliaryNetLane.m_Spacing.z * 0.333333343f);
                                    num3 += x;
                                    num4 -= x;
                                    float3 = geometry.m_Right.m_Right.d - geometryData.m_Start.m_Left.a;
                                    float4 = geometry.m_Left.m_Left.d - geometryData.m_Start.m_Right.a;
                                }
                                else if (auxiliaryNetLane.m_Position.z > 0.1f)
                                {
                                    float t2 = prefabCompositionLaneData3.m_Position.x / math.max(1f, prefabCompositionData.m_Width) + 0.5f;
                                    Bezier4x3 curve = MathUtils.Lerp(geometryData.m_Start.m_Left, geometryData.m_Start.m_Right, t2);
                                    float3 value3 = -MathUtils.StartTangent(curve);
                                    value3 = MathUtils.Normalize(value3, value3.xz);
                                    float4 = (float3 = CalculateAuxialryZOffset(curve.a, value3, geometry, compositionData2, auxiliaryNetLane));
                                    if (flag3)
                                    {
                                        t2 = float2.x / math.max(1f, prefabCompositionData.m_Width) + 0.5f;
                                        curve = MathUtils.Lerp(geometryData.m_Start.m_Left, geometryData.m_Start.m_Right, t2);
                                        value3 = -MathUtils.StartTangent(curve);
                                        value3 = MathUtils.Normalize(value3, value3.xz);
                                        float8 = (float7 = CalculateAuxialryZOffset(curve.a, value3, geometry, compositionData2, auxiliaryNetLane));
                                    }
                                }
                                if (!NetCompositionHelpers.TestLaneFlags(auxiliaryNetLane, compositionData3.m_Flags))
                                {
                                    float x2 = (geometry2.m_Left.middleLength + geometry2.m_Right.middleLength) * 0.5f;
                                    x2 = math.min(x2, auxiliaryNetLane.m_Spacing.z * 0.333333343f);
                                    num3 += x2;
                                    float5 = geometry2.m_Left.m_Left.d - geometryData.m_End.m_Left.d;
                                    float6 = geometry2.m_Right.m_Right.d - geometryData.m_End.m_Right.d;
                                }
                                else if (auxiliaryNetLane.m_Position.z > 0.1f)
                                {
                                    float t3 = prefabCompositionLaneData3.m_Position.x / math.max(1f, prefabCompositionData.m_Width) + 0.5f;
                                    Bezier4x3 curve2 = MathUtils.Lerp(geometryData.m_End.m_Left, geometryData.m_End.m_Right, t3);
                                    float3 value4 = MathUtils.EndTangent(curve2);
                                    value4 = MathUtils.Normalize(value4, value4.xz);
                                    float6 = (float5 = CalculateAuxialryZOffset(curve2.d, value4, geometry2, compositionData3, auxiliaryNetLane));
                                    if (flag3)
                                    {
                                        t3 = float2.x / math.max(1f, prefabCompositionData.m_Width) + 0.5f;
                                        curve2 = MathUtils.Lerp(geometryData.m_End.m_Left, geometryData.m_End.m_Right, t3);
                                        value4 = MathUtils.EndTangent(curve2);
                                        value4 = MathUtils.Normalize(value4, value4.xz);
                                        float10 = (float9 = CalculateAuxialryZOffset(curve2.d, value4, geometry2, compositionData3, auxiliaryNetLane));
                                    }
                                }
                            }
                            if (auxiliaryNetLane.m_Spacing.z > 0.1f)
                            {
                                num5 = Mathf.FloorToInt(num3 / auxiliaryNetLane.m_Spacing.z + 0.5f);
                                num5 = (((auxiliaryNetLane.m_Flags & LaneFlags.EvenSpacing) == 0) ? math.select(num5, 1, (num5 == 0) & (num3 > auxiliaryNetLane.m_Spacing.z * 0.1f)) : math.max(0, num5 - 1));
                            }
                            float num6;
                            float num7;
                            if ((auxiliaryNetLane.m_Flags & LaneFlags.EvenSpacing) != 0)
                            {
                                num6 = 1f;
                                num7 = num3 / (float)(num5 + 1);
                            }
                            else
                            {
                                num6 = 0.5f;
                                num7 = num3 / (float)num5;
                            }
                            float num8 = (num6 - 1f) * num7 + num4;
                            if (num8 > geometryData.m_Start.middleLength)
                            {
                                Bounds1 t4 = new Bounds1(0f, 1f);
                                MathUtils.ClampLength(MathUtils.Lerp(geometryData.m_End.m_Left, geometryData.m_End.m_Right, 0.5f).xz, ref t4, num8 - geometryData.m_Start.middleLength);
                                float11.x = 1f + t4.max;
                            }
                            else
                            {
                                Bounds1 t5 = new Bounds1(0f, 1f);
                                MathUtils.ClampLength(MathUtils.Lerp(geometryData.m_Start.m_Left, geometryData.m_Start.m_Right, 0.5f).xz, ref t5, num8);
                                float11.x = t5.max;
                            }
                            for (int num9 = 0; num9 <= num5; num9++)
                            {
                                num8 = ((float)num9 + num6) * num7 + num4;
                                if (num8 > geometryData.m_Start.middleLength)
                                {
                                    Bounds1 t6 = new Bounds1(0f, 1f);
                                    MathUtils.ClampLength(MathUtils.Lerp(geometryData.m_End.m_Left, geometryData.m_End.m_Right, 0.5f).xz, ref t6, num8 - geometryData.m_Start.middleLength);
                                    float11.y = 1f + t6.max;
                                }
                                else
                                {
                                    Bounds1 t7 = new Bounds1(0f, 1f);
                                    MathUtils.ClampLength(MathUtils.Lerp(geometryData.m_Start.m_Left, geometryData.m_Start.m_Right, 0.5f).xz, ref t7, num8);
                                    float11.y = t7.max;
                                }
                                Segment segment = default(Segment);
                                if (float11.x >= 1f)
                                {
                                    segment.m_Left = MathUtils.Cut(geometryData.m_End.m_Left, float11 - 1f);
                                    segment.m_Right = MathUtils.Cut(geometryData.m_End.m_Right, float11 - 1f);
                                }
                                else if (float11.y <= 1f)
                                {
                                    segment.m_Left = MathUtils.Cut(geometryData.m_Start.m_Left, float11);
                                    segment.m_Right = MathUtils.Cut(geometryData.m_Start.m_Right, float11);
                                }
                                else
                                {
                                    float2 t8 = new float2(float11.x, 1f);
                                    float2 t9 = new float2(0f, float11.y - 1f);
                                    segment.m_Left = MathUtils.Join(MathUtils.Cut(geometryData.m_Start.m_Left, t8), MathUtils.Cut(geometryData.m_End.m_Left, t9));
                                    segment.m_Right = MathUtils.Join(MathUtils.Cut(geometryData.m_Start.m_Right, t8), MathUtils.Cut(geometryData.m_End.m_Right, t9));
                                }
                                Segment segment2 = segment;
                                if ((auxiliaryNetLane.m_Flags & LaneFlags.EvenSpacing) != 0)
                                {
                                    if (num9 == 0)
                                    {
                                        segment.m_Left.a += float3;
                                        segment.m_Right.a += float4;
                                        segment2.m_Left.a += float7;
                                        segment2.m_Right.a += float8;
                                    }
                                    if (num9 == num5)
                                    {
                                        segment.m_Left.d += float5;
                                        segment.m_Right.d += float6;
                                        segment2.m_Left.d += float9;
                                        segment2.m_Right.d += float10;
                                    }
                                }
                                float2 edgeDelta = math.select(float11 * 0.5f, new float2(0f, 1f), num9 == new int2(0, num5));
                                if (auxiliaryNetLane.m_Spacing.x > 0.1f)
                                {
                                    Segment segment3 = default(Segment);
                                    float3 lhs = netCompositionLane2.m_Position.x;
                                    lhs.xz += new float2(0f - auxiliaryNetLane.m_Spacing.x, auxiliaryNetLane.m_Spacing.x);
                                    lhs.x = math.select(lhs.x, float2.x, flag3);
                                    lhs = math.saturate(lhs / math.max(1f, prefabCompositionData.m_Width) + 0.5f);
                                    float3 startPos;
                                    if (num9 == 0)
                                    {
                                        startPos = ((!flag3) ? math.lerp(segment.m_Left.a, segment.m_Right.a, lhs.x) : math.lerp(segment2.m_Left.a, segment2.m_Right.a, lhs.x));
                                        segment3.m_Left = NetUtils.StraightCurve(startPos, math.lerp(segment.m_Left.a, segment.m_Right.a, lhs.y));
                                        segment3.m_Right = segment3.m_Left;
                                        CreateEdgeLane(jobIndex, ref random, owner, laneBuffer, segment3, prefabCompositionData, compositionData, prefabCompositionLanes, prefabCompositionLaneData3,
                                            new int2(num, num + 2), edgeDelta.xx, nativeList, nativeList2, new bool2(!flag3, y: false), isTemp, ownerTemp);
                                        num += 2;
                                        if (!flag2)
                                        {
                                            segment3.m_Left = NetUtils.StraightCurve(segment3.m_Left.d, math.lerp(segment.m_Left.a, segment.m_Right.a, lhs.z));
                                            segment3.m_Right = segment3.m_Left;
                                            CreateEdgeLane(jobIndex, ref random, owner, laneBuffer, segment3, prefabCompositionData, compositionData, prefabCompositionLanes, prefabCompositionLaneData3,
                                                new int2(num, num + 2), edgeDelta.xx, nativeList, nativeList2, new bool2(x: false, y: true), isTemp, ownerTemp);
                                            num += 2;
                                        }
                                        num++;
                                    }
                                    startPos = ((!flag3) ? math.lerp(segment.m_Left.d, segment.m_Right.d, lhs.x) : math.lerp(segment2.m_Left.d, segment2.m_Right.d, lhs.x));
                                    segment3.m_Left = NetUtils.StraightCurve(startPos, math.lerp(segment.m_Left.d, segment.m_Right.d, lhs.y));
                                    segment3.m_Right = segment3.m_Left;
                                    CreateEdgeLane(jobIndex, ref random, owner, laneBuffer, segment3, prefabCompositionData, compositionData, prefabCompositionLanes, prefabCompositionLaneData3, new int2(num, num + 2),
                                        edgeDelta.yy, nativeList, nativeList2, new bool2(!flag3, y: false), isTemp, ownerTemp);
                                    num += 2;
                                    if (!flag2)
                                    {
                                        segment3.m_Left = NetUtils.StraightCurve(segment3.m_Left.d, math.lerp(segment.m_Left.d, segment.m_Right.d, lhs.z));
                                        segment3.m_Right = segment3.m_Left;
                                        CreateEdgeLane(jobIndex, ref random, owner, laneBuffer, segment3, prefabCompositionData, compositionData, prefabCompositionLanes, prefabCompositionLaneData3,
                                            new int2(num, num + 2), edgeDelta.yy, nativeList, nativeList2, new bool2(x: false, y: true), isTemp, ownerTemp);
                                        num += 2;
                                    }
                                    num++;
                                }
                                else
                                {
                                    CreateEdgeLane(jobIndex, ref random, owner, laneBuffer, segment, prefabCompositionData, compositionData, prefabCompositionLanes, prefabCompositionLaneData3, new int2(num, num + 2),
                                        edgeDelta, nativeList, nativeList2, true, isTemp, ownerTemp);
                                    num += 2;
                                }
                                float11.x = float11.y;
                            }
                            if (auxiliaryNetLane.m_Spacing.x <= 0.1f)
                            {
                                num++;
                            }
                        }
                    }
                }
                if (nativeList.IsCreated)
                {
                    nativeList.Dispose();
                }
                if (nativeList2.IsCreated)
                {
                    nativeList2.Dispose();
                }
                if (tempBuffer.IsCreated)
                {
                    tempBuffer.Dispose();
                }
            }

            private float3 CalculateAuxialryZOffset(float3 position, float3 tangent, EdgeNodeGeometry nodeGeometry, NetCompositionData compositionData, AuxiliaryNetLane auxiliaryLane) {
                float num = auxiliaryLane.m_Position.z;
                if ((compositionData.m_Flags.m_General & CompositionFlags.General.DeadEnd) != 0)
                {
                    num = math.max(0f, math.min(num, compositionData.m_Width * 0.125f));
                }
                else if (nodeGeometry.m_MiddleRadius > 0f)
                {
                    if (MathUtils.Intersect(new Line2(position.xz, position.xz + tangent.xz), new Line2(nodeGeometry.m_Right.m_Left.d.xz, nodeGeometry.m_Right.m_Right.d.xz), out float2 t))
                    {
                        num = math.max(0f, math.min(num, t.x * 0.5f));
                    }
                }
                else
                {
                    if (MathUtils.Intersect(new Line2(position.xz, position.xz + tangent.xz), new Line2(nodeGeometry.m_Left.m_Left.d.xz, nodeGeometry.m_Left.m_Right.d.xz), out float2 t2))
                    {
                        num = math.max(0f, math.min(num, t2.x * 0.5f));
                    }
                    if (MathUtils.Intersect(new Line2(position.xz, position.xz + tangent.xz), new Line2(nodeGeometry.m_Right.m_Left.d.xz, nodeGeometry.m_Right.m_Right.d.xz), out float2 t3))
                    {
                        num = math.max(0f, math.min(num, t3.x * 0.5f));
                    }
                }
                return tangent * num;
            }

            private int FindBestConnectionLane(DynamicBuffer<NetCompositionLane> prefabCompositionLanes, float2 offset, float elevationOffset, LaneFlags laneType, LaneFlags laneFlags) {
                float num = float.MaxValue;
                int result = -1;
                for (int i = 0; i < prefabCompositionLanes.Length; i++)
                {
                    NetCompositionLane netCompositionLane = prefabCompositionLanes[i];
                    if ((netCompositionLane.m_Flags & laneType) == 0)
                    {
                        continue;
                    }
                    if ((laneFlags & LaneFlags.Master) != 0)
                    {
                        if ((netCompositionLane.m_Flags & LaneFlags.Slave) != 0)
                        {
                            continue;
                        }
                    }
                    else if ((netCompositionLane.m_Flags & LaneFlags.Master) != 0)
                    {
                        continue;
                    }
                    float2 lhs = new float2(offset.y, elevationOffset);
                    lhs = math.abs(lhs - netCompositionLane.m_Position.yy);
                    float num2 = math.lengthsq(new float2(netCompositionLane.m_Position.x - offset.x, math.cmin(lhs)));
                    if (num2 < num)
                    {
                        num = num2;
                        result = i;
                    }
                }
                return result;
            }

            private void CreateCarEdgeConnections(int jobIndex, ref int edgeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, EdgeGeometry geometryData,
                NetCompositionData prefabCompositionData, CompositionData compositionData, ConnectPosition connectPosition, float2 offset, int connectionIndex, bool isSingleCurve, bool isSource,
                bool isTemp, Temp ownerTemp, DynamicBuffer<NetCompositionLane> prefabCompositionLanes, int bestIndex
            )
            {
                NetCompositionLane netCompositionLane = prefabCompositionLanes[bestIndex];
                int num = -1;
                int index = 0;
                int index2 = 0;
                float num2 = float.MaxValue;
                float num3 = float.MaxValue;
                for (int i = 0; i < prefabCompositionLanes.Length; i++)
                {
                    NetCompositionLane prefabCompositionLaneData = prefabCompositionLanes[i];
                    if ((prefabCompositionLaneData.m_Flags & LaneFlags.Road) == 0 || prefabCompositionLaneData.m_Carriageway != netCompositionLane.m_Carriageway)
                    {
                        continue;
                    }
                    if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Master) != 0)
                    {
                        if ((prefabCompositionLaneData.m_Flags & LaneFlags.Slave) != 0)
                        {
                            continue;
                        }
                    }
                    else if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Slave) != 0 && (prefabCompositionLaneData.m_Flags & LaneFlags.Master) != 0)
                    {
                        continue;
                    }
                    if ((m_CarLaneData[prefabCompositionLaneData.m_Lane].m_RoadTypes & (RoadTypes.Car | RoadTypes.Bicycle)) == 0)
                    {
                        continue;
                    }
                    if (num != -1 && (prefabCompositionLaneData.m_Group != num || (prefabCompositionLaneData.m_Flags & LaneFlags.Slave) == 0))
                    {
                        NetCompositionLane prefabCompositionLaneData2 = prefabCompositionLanes[index];
                        CreateEdgeConnectionLane(jobIndex, ref edgeLaneIndex, ref random, owner, laneBuffer, geometryData.m_Start, geometryData.m_End, prefabCompositionData, compositionData, prefabCompositionLaneData2, connectPosition, connectionIndex,
                            isSingleCurve, useGroundPosition: false, isSource, isTemp, ownerTemp);
                        if ((prefabCompositionLaneData2.m_Flags & LaneFlags.BicyclesOnly) != 0 && num3 != float.MaxValue)
                        {
                            prefabCompositionLaneData2 = prefabCompositionLanes[index2];
                            CreateEdgeConnectionLane(jobIndex, ref edgeLaneIndex, ref random, owner, laneBuffer, geometryData.m_Start, geometryData.m_End, prefabCompositionData, compositionData, prefabCompositionLaneData2, connectPosition, connectionIndex,
                                isSingleCurve, useGroundPosition: false, isSource, isTemp, ownerTemp);
                        }
                        num = -1;
                        num2 = float.MaxValue;
                        num3 = float.MaxValue;
                    }
                    if ((prefabCompositionLaneData.m_Flags & LaneFlags.Slave) != 0)
                    {
                        num = prefabCompositionLaneData.m_Group;
                        float num4 = math.abs(prefabCompositionLaneData.m_Position.x - offset.x);
                        if (num4 < num2)
                        {
                            num2 = num4;
                            index = i;
                        }
                        if ((prefabCompositionLaneData.m_Flags & LaneFlags.BicyclesOnly) == 0 && num4 < num3)
                        {
                            num3 = num4;
                            index2 = i;
                        }
                    }
                    else
                    {
                        CreateEdgeConnectionLane(jobIndex, ref edgeLaneIndex, ref random, owner, laneBuffer, geometryData.m_Start, geometryData.m_End, prefabCompositionData, compositionData,
                            prefabCompositionLaneData, connectPosition, connectionIndex, isSingleCurve, useGroundPosition: false, isSource, isTemp, ownerTemp);
                    }
                }
                if (num != -1)
                {
                    NetCompositionLane prefabCompositionLaneData3 = prefabCompositionLanes[index];
                    CreateEdgeConnectionLane(jobIndex, ref edgeLaneIndex, ref random, owner, laneBuffer, geometryData.m_Start, geometryData.m_End, prefabCompositionData, compositionData, prefabCompositionLaneData3, connectPosition, connectionIndex,
                        isSingleCurve, useGroundPosition: false, isSource, isTemp, ownerTemp);
                    if ((prefabCompositionLaneData3.m_Flags & LaneFlags.BicyclesOnly) != 0 && num3 != float.MaxValue)
                    {
                        prefabCompositionLaneData3 = prefabCompositionLanes[index2];
                        CreateEdgeConnectionLane(jobIndex, ref edgeLaneIndex, ref random, owner, laneBuffer, geometryData.m_Start, geometryData.m_End, prefabCompositionData, compositionData, prefabCompositionLaneData3, connectPosition, connectionIndex,
                            isSingleCurve, useGroundPosition: false, isSource, isTemp, ownerTemp);
                    }
                }
            }

            private CompositionData GetCompositionData(Entity composition) {
                CompositionData result = default(CompositionData);
                if (m_RoadData.HasComponent(composition))
                {
                    RoadComposition roadComposition = m_RoadData[composition];
                    result.m_SpeedLimit = roadComposition.m_SpeedLimit;
                    result.m_RoadFlags = roadComposition.m_Flags;
                    result.m_Priority = roadComposition.m_Priority;
                }
                else if (m_TrackData.HasComponent(composition))
                {
                    TrackComposition trackComposition = m_TrackData[composition];
                    result.m_SpeedLimit = trackComposition.m_SpeedLimit;
                }
                else if (m_WaterwayData.HasComponent(composition))
                {
                    WaterwayComposition waterwayComposition = m_WaterwayData[composition];
                    result.m_SpeedLimit = waterwayComposition.m_SpeedLimit;
                }
                else if (m_PathwayData.HasComponent(composition))
                {
                    PathwayComposition pathwayComposition = m_PathwayData[composition];
                    result.m_SpeedLimit = pathwayComposition.m_SpeedLimit;
                }
                else if (m_TaxiwayData.HasComponent(composition))
                {
                    TaxiwayComposition taxiwayComposition = m_TaxiwayData[composition];
                    result.m_SpeedLimit = taxiwayComposition.m_SpeedLimit;
                    result.m_TaxiwayFlags = taxiwayComposition.m_Flags;
                }
                else
                {
                    result.m_SpeedLimit = 1f;
                }
                return result;
            }

            private NetCompositionLane FindClosestLane(DynamicBuffer<NetCompositionLane> prefabCompositionLanes, LaneFlags all, LaneFlags none, float3 position, int carriageWay = -1) {
                float num = float.MaxValue;
                NetCompositionLane result = default(NetCompositionLane);
                if (!prefabCompositionLanes.IsCreated)
                {
                    return result;
                }
                for (int i = 0; i < prefabCompositionLanes.Length; i++)
                {
                    NetCompositionLane netCompositionLane = prefabCompositionLanes[i];
                    if ((netCompositionLane.m_Flags & (all | none)) == all && (carriageWay == -1 || netCompositionLane.m_Carriageway == carriageWay))
                    {
                        float num2 = math.lengthsq(netCompositionLane.m_Position - position);
                        if (num2 < num)
                        {
                            num = num2;
                            result = netCompositionLane;
                        }
                    }
                }
                return result;
            }

            private static void Invert(ref Lane laneData, ref Curve curveData, ref EdgeLane edgeLaneData) {
                PathNode startNode = laneData.m_StartNode;
                laneData.m_StartNode = laneData.m_EndNode;
                laneData.m_EndNode = startNode;
                curveData.m_Bezier = MathUtils.Invert(curveData.m_Bezier);
                edgeLaneData.m_EdgeDelta = edgeLaneData.m_EdgeDelta.yx;
            }

            private void CreateNodeConnectionLanes(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, NativeList<MiddleConnection> middleConnections,
                NativeList<ConnectPosition> tempBuffer, CompositionFlags intersectionFlags, bool isRoundabout, bool isTemp, Temp ownerTemp)
            {
                int num = 0;
                for (int i = 0; i < middleConnections.Length; i++)
                {
                    MiddleConnection value = middleConnections[i];
                    if (value.m_TargetLane != Entity.Null)
                    {
                        middleConnections[num++] = value;
                    }
                }
                middleConnections.RemoveRange(num, middleConnections.Length - num);
                if (middleConnections.Length >= 2)
                {
                    middleConnections.Sort(default(MiddleConnectionComparer));
                }
                StackList<uint> stackList = stackalloc uint[middleConnections.Length];
                for (int j = 0; j < middleConnections.Length; j++)
                {
                    MiddleConnection middleConnection = middleConnections[j];
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Utility) != 0)
                    {
                        int4 @int = -1;
                        int4 int2 = -1;
                        for (int k = j; k < middleConnections.Length; k++)
                        {
                            MiddleConnection middleConnection2 = middleConnections[k];
                            if ((middleConnection2.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Utility) == 0 ||
                                (middleConnection2.m_ConnectPosition.m_UtilityTypes & middleConnection.m_ConnectPosition.m_UtilityTypes) == 0 || middleConnection2.m_SourceNode != middleConnection.m_SourceNode)
                            {
                                break;
                            }
                            tempBuffer.Add(in middleConnection2.m_ConnectPosition);
                            if ((middleConnection2.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Underground) != 0)
                            {
                                int2.xy = math.select(int2.xy, j, new bool2(int2.x == -1, j > int2.y));
                            }
                            else
                            {
                                @int.xy = math.select(@int.xy, j, new bool2(@int.x == -1, j > @int.y));
                            }
                            if ((middleConnection2.m_TargetFlags & LaneFlags.Underground) != 0)
                            {
                                int2.zw = math.select(int2.zw, j, new bool2(int2.z == -1, j > int2.w));
                            }
                            else
                            {
                                @int.zw = math.select(@int.zw, j, new bool2(@int.z == -1, j > @int.w));
                            }
                        }
                        j += math.max(0, tempBuffer.Length - 1);
                        @int = math.select(@int, int2, (@int == -1) | (math.any(@int == -1) & math.all(int2 != -1)));
                        if (math.all(@int.xz != -1))
                        {
                            middleConnection = middleConnections[@int.x];
                            MiddleConnection middleConnection3 = middleConnections[@int.z];
                            UtilityLaneData utilityLaneData = m_UtilityLaneData[middleConnection.m_ConnectPosition.m_LaneData.m_Lane];
                            UtilityLaneData utilityLaneData2 = m_UtilityLaneData[middleConnection3.m_TargetLane];
                            bool useGroundPosition = false;
                            if (((middleConnection.m_ConnectPosition.m_LaneData.m_Flags ^ middleConnection3.m_TargetFlags) & LaneFlags.Underground) != 0)
                            {
                                if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Underground) != 0)
                                {
                                    useGroundPosition = true;
                                    middleConnection.m_ConnectPosition.m_LaneData.m_Lane = GetConnectionLanePrefab(middleConnection.m_ConnectPosition.m_LaneData.m_Lane, utilityLaneData,
                                        utilityLaneData2.m_VisualCapacity < utilityLaneData.m_VisualCapacity, wantLarger: false);
                                }
                                else
                                {
                                    GetGroundPosition(middleConnection.m_ConnectPosition.m_Owner, math.select(0f, 1f, middleConnection.m_ConnectPosition.m_IsEnd), ref middleConnection.m_ConnectPosition.m_Position);
                                    middleConnection.m_ConnectPosition.m_LaneData.m_Lane = GetConnectionLanePrefab(middleConnection3.m_TargetLane, utilityLaneData2,
                                        utilityLaneData.m_VisualCapacity < utilityLaneData2.m_VisualCapacity, utilityLaneData.m_VisualCapacity > utilityLaneData2.m_VisualCapacity);
                                }
                            }
                            else
                            {
                                @int.y++;
                                middleConnection.m_ConnectPosition.m_Position = CalculateUtilityConnectPosition(tempBuffer, new int2(0, @int.y - @int.x));
                                if (utilityLaneData.m_VisualCapacity < utilityLaneData2.m_VisualCapacity)
                                {
                                    middleConnection.m_ConnectPosition.m_LaneData.m_Lane =
                                        GetConnectionLanePrefab(middleConnection.m_ConnectPosition.m_LaneData.m_Lane, utilityLaneData, wantSmaller: false, wantLarger: false);
                                }
                                else
                                {
                                    middleConnection.m_ConnectPosition.m_LaneData.m_Lane = GetConnectionLanePrefab(middleConnection3.m_TargetLane, utilityLaneData2, wantSmaller: false,
                                        utilityLaneData.m_VisualCapacity > utilityLaneData2.m_VisualCapacity);
                                }
                            }
                            CreateNodeConnectionLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, middleConnection, intersectionFlags, useGroundPosition, isTemp, ownerTemp);
                        }
                        tempBuffer.Clear();
                    }
                    else
                    {
                        if ((middleConnection.m_TargetFlags & LaneFlags.Road) == 0)
                        {
                            continue;
                        }
                        float num2 = float.MaxValue;
                        Entity entity = Entity.Null;
                        ushort num3 = 0;
                        uint num4 = 0u;
                        int num5 = j;
                        for (; j < middleConnections.Length; j++)
                        {
                            MiddleConnection middleConnection4 = middleConnections[j];
                            if (middleConnection4.m_ConnectPosition.m_GroupIndex != middleConnection.m_ConnectPosition.m_GroupIndex || middleConnection4.m_IsSource != middleConnection.m_IsSource)
                            {
                                break;
                            }
                            if ((middleConnection4.m_TargetFlags & LaneFlags.Master) == 0 && middleConnection4.m_Distance < num2)
                            {
                                num2 = middleConnection4.m_Distance;
                                entity = middleConnection4.m_TargetOwner;
                                num3 = middleConnection4.m_TargetCarriageway;
                                num4 = middleConnection4.m_TargetGroup;
                            }
                        }
                        j--;
                        for (int l = num5; l <= j; l++)
                        {
                            middleConnection = middleConnections[l];
                            if ((middleConnection.m_TargetFlags & (LaneFlags.Master | LaneFlags.BicyclesOnly)) != LaneFlags.BicyclesOnly)
                            {
                                continue;
                            }
                            for (int m = num5; m <= j; m++)
                            {
                                MiddleConnection middleConnection5 = middleConnections[m];
                                if ((middleConnection5.m_TargetFlags & LaneFlags.Master) == 0 && middleConnection5.m_Distance < middleConnection.m_Distance &&
                                    ((int)(middleConnection5.m_TargetGroup ^ middleConnection.m_TargetGroup) & (middleConnection.m_IsSource ? (-65536) : 65535)) == 0)
                                {
                                    middleConnection.m_Distance = float.MaxValue;
                                    middleConnections[l] = middleConnection;
                                    break;
                                }
                            }
                        }
                        for (int n = num5; n <= j; n++)
                        {
                            middleConnection = middleConnections[n];
                            bool flag = middleConnection.m_TargetCarriageway == num3 && middleConnection.m_Distance < float.MaxValue;
                            if (flag && isRoundabout)
                            {
                                flag = (middleConnection.m_TargetOwner == entity && (entity != owner || middleConnection.m_TargetGroup == num4));
                            }
                            if (flag && (middleConnection.m_TargetFlags & LaneFlags.Master) != 0)
                            {
                                flag = false;
                                for (int num6 = num5; num6 <= j; num6++)
                                {
                                    MiddleConnection middleConnection6 = middleConnections[num6];
                                    if ((middleConnection6.m_TargetFlags & LaneFlags.Master) == 0 && middleConnection6.m_TargetGroup == middleConnection.m_TargetGroup && middleConnection6.m_TargetCarriageway == num3 &&
                                        middleConnection6.m_Distance < float.MaxValue && (!isRoundabout || (middleConnection6.m_TargetOwner == entity && (entity != owner || middleConnection6.m_TargetGroup == num4))))
                                    {
                                        flag = true;
                                        break;
                                    }
                                }
                            }
                            if (flag && (middleConnection.m_TargetFlags & LaneFlags.Master) != 0)
                            {
                                uint num7 = (uint)(middleConnection.m_IsSource
                                    ? (middleConnection.m_ConnectPosition.m_GroupIndex | ((int)middleConnection.m_TargetGroup & -65536))
                                    : ((int)((middleConnection.m_TargetGroup & 0xFFFF) | (uint)(middleConnection.m_ConnectPosition.m_GroupIndex << 16))));
                                for (int num8 = 0; num8 < stackList.Length; num8++)
                                {
                                    if (stackList[num8] == num7)
                                    {
                                        flag = false;
                                        break;
                                    }
                                }
                                if (flag)
                                {
                                    stackList.AddNoResize(num7);
                                }
                            }
                            if (flag)
                            {
                                CompositionFlags intersectionFlags2 = intersectionFlags;
                                intersectionFlags2.m_General &= ~CompositionFlags.General.TrafficLights;
                                CreateNodeConnectionLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, middleConnection, intersectionFlags2, useGroundPosition: false, isTemp, ownerTemp);
                            }
                        }
                    }
                }
            }

            private void CreateNodeConnectionLane(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, MiddleConnection middleConnection, CompositionFlags intersectionFlags, bool useGroundPosition,
                bool isTemp, Temp ownerTemp
            ) {
                Owner component = default(Owner);
                component.m_Owner = owner;
                Temp temp = default(Temp);
                if (isTemp)
                {
                    temp.m_Flags = (ownerTemp.m_Flags & (TempFlags.Create | TempFlags.Delete | TempFlags.Select | TempFlags.Modify | TempFlags.Hidden));
                    if ((ownerTemp.m_Flags & TempFlags.Replace) != 0)
                    {
                        temp.m_Flags |= TempFlags.Modify;
                    }
                }
                if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                {
                    middleConnection.m_ConnectPosition.m_LaneData.m_Lane = m_PedestrianLaneData[middleConnection.m_ConnectPosition.m_LaneData.m_Lane].m_NotWalkLanePrefab;
                }
                PrefabRef component2 = new PrefabRef(middleConnection.m_ConnectPosition.m_LaneData.m_Lane);
                CheckPrefab(ref component2.m_Prefab, ref random, out Unity.Mathematics.Random outRandom, laneBuffer);
                NodeLane component3 = default(NodeLane);
                NetLaneData netLaneData = m_NetLaneData[component2.m_Prefab];
                if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Pedestrian)) != 0)
                {
                    if (m_NetLaneData.TryGetComponent(middleConnection.m_TargetLane, out NetLaneData componentData))
                    {
                        component3.m_WidthOffset.x = componentData.m_Width - netLaneData.m_Width;
                    }
                    if (m_NetLaneData.TryGetComponent(middleConnection.m_ConnectPosition.m_LaneData.m_Lane, out NetLaneData componentData2))
                    {
                        component3.m_WidthOffset.y = componentData2.m_Width - netLaneData.m_Width;
                    }
                    if (middleConnection.m_IsSource)
                    {
                        component3.m_WidthOffset = component3.m_WidthOffset.yx;
                    }
                    component3.m_Flags |= (NodeLaneFlags)((component3.m_WidthOffset.x != 0f) ? 1 : 0);
                    component3.m_Flags |= (NodeLaneFlags)((component3.m_WidthOffset.y != 0f) ? 2 : 0);
                    if ((componentData.m_Flags & LaneFlags.BicyclesOnly) != 0)
                    {
                        component3.m_Flags |= (NodeLaneFlags)(middleConnection.m_IsSource ? 8 : 4);
                    }
                    if ((componentData2.m_Flags & LaneFlags.BicyclesOnly) != 0)
                    {
                        component3.m_Flags |= (NodeLaneFlags)(middleConnection.m_IsSource ? 4 : 8);
                    }
                }
                Curve curve = default(Curve);
                if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Pedestrian)) != 0)
                {
                    float length = math.sqrt(math.distance(middleConnection.m_ConnectPosition.m_Position, MathUtils.Position(middleConnection.m_TargetCurve.m_Bezier, middleConnection.m_TargetCurvePos)));
                    if (middleConnection.m_IsSource)
                    {
                        Bounds1 t = new Bounds1(middleConnection.m_TargetCurvePos, 1f);
                        MathUtils.ClampLength(middleConnection.m_TargetCurve.m_Bezier, ref t, ref length);
                        middleConnection.m_TargetCurvePos = t.max;
                    }
                    else
                    {
                        Bounds1 t2 = new Bounds1(0f, middleConnection.m_TargetCurvePos);
                        MathUtils.ClampLengthInverse(middleConnection.m_TargetCurve.m_Bezier, ref t2, ref length);
                        middleConnection.m_TargetCurvePos = t2.min;
                    }
                    float3 @float = MathUtils.Position(middleConnection.m_TargetCurve.m_Bezier, middleConnection.m_TargetCurvePos);
                    float3 value = MathUtils.Tangent(middleConnection.m_TargetCurve.m_Bezier, middleConnection.m_TargetCurvePos);
                    value = MathUtils.Normalize(value, value.xz);
                    if (middleConnection.m_IsSource)
                    {
                        value = -value;
                    }
                    if (math.distance(middleConnection.m_ConnectPosition.m_Position, @float) >= 0.01f)
                    {
                        curve.m_Bezier = NetUtils.FitCurve(@float, value, -middleConnection.m_ConnectPosition.m_Tangent, middleConnection.m_ConnectPosition.m_Position);
                    }
                    else
                    {
                        curve.m_Bezier = NetUtils.StraightCurve(@float, middleConnection.m_ConnectPosition.m_Position);
                    }
                    if (middleConnection.m_IsSource)
                    {
                        curve.m_Bezier = MathUtils.Invert(curve.m_Bezier);
                    }
                    curve.m_Length = MathUtils.Length(curve.m_Bezier);
                }
                else if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Utility) != 0)
                {
                    float3 position = MathUtils.Position(middleConnection.m_TargetCurve.m_Bezier, middleConnection.m_TargetCurvePos);
                    if (useGroundPosition)
                    {
                        GetGroundPosition(owner, middleConnection.m_ConnectPosition.m_CurvePosition, ref position);
                    }
                    if (math.abs(position.y - middleConnection.m_ConnectPosition.m_Position.y) >= 0.01f)
                    {
                        curve.m_Bezier.a = position;
                        curve.m_Bezier.b = math.lerp(position, middleConnection.m_ConnectPosition.m_Position, new float3(0.25f, 0.5f, 0.25f));
                        curve.m_Bezier.c = math.lerp(position, middleConnection.m_ConnectPosition.m_Position, new float3(0.75f, 0.5f, 0.75f));
                        curve.m_Bezier.d = middleConnection.m_ConnectPosition.m_Position;
                    }
                    else
                    {
                        curve.m_Bezier = NetUtils.StraightCurve(position, middleConnection.m_ConnectPosition.m_Position);
                    }
                    if (middleConnection.m_IsSource)
                    {
                        curve.m_Bezier = MathUtils.Invert(curve.m_Bezier);
                    }
                    curve.m_Length = MathUtils.Length(curve.m_Bezier);
                }
                else
                {
                    float3 float2 = MathUtils.Position(middleConnection.m_TargetCurve.m_Bezier, middleConnection.m_TargetCurvePos);
                    curve.m_Bezier = NetUtils.StraightCurve(float2, middleConnection.m_ConnectPosition.m_Position);
                    if (middleConnection.m_IsSource)
                    {
                        curve.m_Bezier = MathUtils.Invert(curve.m_Bezier);
                    }
                    curve.m_Length = math.distance(float2, middleConnection.m_ConnectPosition.m_Position);
                }
                Lane lane = default(Lane);
                uint num = 0u;
                if (middleConnection.m_IsSource)
                {
                    lane.m_StartNode = new PathNode(middleConnection.m_ConnectPosition.m_Owner, middleConnection.m_ConnectPosition.m_LaneData.m_Index, middleConnection.m_ConnectPosition.m_SegmentIndex);
                    lane.m_MiddleNode = new PathNode(owner, (ushort)nodeLaneIndex++);
                    lane.m_EndNode = new PathNode(middleConnection.m_TargetNode, middleConnection.m_TargetCurvePos);
                    num = (uint)(middleConnection.m_ConnectPosition.m_GroupIndex | ((int)middleConnection.m_TargetGroup & -65536));
                }
                else
                {
                    lane.m_StartNode = new PathNode(middleConnection.m_TargetNode, middleConnection.m_TargetCurvePos);
                    lane.m_MiddleNode = new PathNode(owner, (ushort)nodeLaneIndex++);
                    lane.m_EndNode = new PathNode(middleConnection.m_ConnectPosition.m_Owner, middleConnection.m_ConnectPosition.m_LaneData.m_Index, middleConnection.m_ConnectPosition.m_SegmentIndex);
                    num = ((middleConnection.m_TargetGroup & 0xFFFF) | (uint)(middleConnection.m_ConnectPosition.m_GroupIndex << 16));
                }
                bool flag = false;
                CarLane component4 = default(CarLane);
                if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Pedestrian)) != 0)
                {
                    component4.m_DefaultSpeedLimit = middleConnection.m_TargetComposition.m_SpeedLimit;
                    component4.m_Curviness = NetUtils.CalculateCurviness(curve, netLaneData.m_Width);
                    component4.m_CarriagewayGroup = middleConnection.m_TargetCarriageway;
                    component4.m_Flags |= (CarLaneFlags.Unsafe | CarLaneFlags.SideConnection);
                    if (middleConnection.m_IsSource)
                    {
                        component4.m_Flags |= CarLaneFlags.Yield;
                    }
                    if ((intersectionFlags.m_General & CompositionFlags.General.TrafficLights) != 0)
                    {
                        component4.m_Flags |= CarLaneFlags.TrafficLights;
                        flag = true;
                    }
                    if (math.dot(MathUtils.Right(MathUtils.StartTangent(curve.m_Bezier).xz), MathUtils.EndTangent(curve.m_Bezier).xz) >= 0f)
                    {
                        component4.m_Flags |= CarLaneFlags.TurnRight;
                    }
                    else
                    {
                        component4.m_Flags |= CarLaneFlags.TurnLeft;
                    }
                    if ((middleConnection.m_TargetComposition.m_TaxiwayFlags & TaxiwayFlags.Runway) != 0)
                    {
                        component4.m_Flags |= CarLaneFlags.Runway;
                    }
                    if ((middleConnection.m_TargetComposition.m_RoadFlags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0)
                    {
                        component4.m_Flags |= CarLaneFlags.Highway;
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                    {
                        middleConnection.m_ConnectPosition.m_LaneData.m_Flags &= ~LaneFlags.Pedestrian;
                        middleConnection.m_ConnectPosition.m_LaneData.m_Flags |= LaneFlags.Road;
                        component4.m_Flags |= (CarLaneFlags)(middleConnection.m_IsSource ? 4096 : 8192);
                    }
                    middleConnection.m_ConnectPosition.m_LaneData.m_Flags &= ~(LaneFlags.Slave | LaneFlags.Master);
                    middleConnection.m_ConnectPosition.m_LaneData.m_Flags |= (middleConnection.m_TargetFlags & (LaneFlags.Slave | LaneFlags.Master));
                }
                else
                {
                    middleConnection.m_ConnectPosition.m_LaneData.m_Flags &= ~(LaneFlags.Slave | LaneFlags.Master);
                }
                UtilityLane component5 = default(UtilityLane);
                if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Utility) != 0)
                {
                    component5.m_Flags |= UtilityLaneFlags.VerticalConnection;
                    if (m_PrefabRefData.TryGetComponent(middleConnection.m_ConnectPosition.m_Owner, out PrefabRef componentData3) && m_PrefabNetData.TryGetComponent(componentData3.m_Prefab, out NetData componentData4) &&
                        m_PrefabGeometryData.TryGetComponent(componentData3.m_Prefab, out NetGeometryData componentData5) && (componentData4.m_RequiredLayers & (Layer.PowerlineLow | Layer.PowerlineHigh | Layer.WaterPipe | Layer.SewagePipe)) != 0 &&
                        (componentData5.m_Flags & GeometryFlags.Marker) == 0)
                    {
                        component5.m_Flags |= UtilityLaneFlags.PipelineConnection;
                    }
                }
                LaneKey laneKey = new LaneKey(lane, component2.m_Prefab, middleConnection.m_ConnectPosition.m_LaneData.m_Flags);
                LaneKey laneKey2 = laneKey;
                if (isTemp)
                {
                    ReplaceTempOwner(ref laneKey2, owner);
                    ReplaceTempOwner(ref laneKey2, middleConnection.m_ConnectPosition.m_Owner);
                    GetOriginalLane(laneBuffer, laneKey2, ref temp);
                }
                PseudoRandomSeed componentData6 = default(PseudoRandomSeed);
                if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0 && !m_PseudoRandomSeedData.TryGetComponent(temp.m_Original, out componentData6))
                {
                    componentData6 = new PseudoRandomSeed(ref outRandom);
                }
                if (laneBuffer.m_OldLanes.TryGetValue(laneKey, out Entity item))
                {
                    laneBuffer.m_OldLanes.Remove(laneKey);
                    m_CommandBuffer.SetComponent(jobIndex, item, lane);
                    m_CommandBuffer.SetComponent(jobIndex, item, component3);
                    m_CommandBuffer.SetComponent(jobIndex, item, curve);
                    if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
                    {
                        if (!m_PseudoRandomSeedData.HasComponent(item))
                        {
                            m_CommandBuffer.AddComponent(jobIndex, item, componentData6);
                        }
                        else
                        {
                            m_CommandBuffer.SetComponent(jobIndex, item, componentData6);
                        }
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Road) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, item, component4);
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Utility) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, item, component5);
                    }
                    if (isTemp)
                    {
                        laneBuffer.m_Updates.Add(in item);
                        m_CommandBuffer.SetComponent(jobIndex, item, temp);
                    }
                    else if (m_TempData.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent(jobIndex, item, in m_DeletedTempTypes);
                        m_CommandBuffer.AddComponent(jobIndex, item, in m_AppliedTypes);
                    }
                    else
                    {
                        laneBuffer.m_Updates.Add(in item);
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Master) != 0)
                    {
                        MasterLane component6 = default(MasterLane);
                        component6.m_Group = num;
                        m_CommandBuffer.SetComponent(jobIndex, item, component6);
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Slave) != 0)
                    {
                        SlaveLane component7 = default(SlaveLane);
                        component7.m_Group = num;
                        component7.m_MinIndex = 0;
                        component7.m_MaxIndex = 0;
                        component7.m_SubIndex = 0;
                        component7.m_Flags |= SlaveLaneFlags.AllowChange;
                        component7.m_Flags |= (SlaveLaneFlags)(middleConnection.m_IsSource ? 4096 : 2048);
                        m_CommandBuffer.SetComponent(jobIndex, item, component7);
                    }
                    if (flag)
                    {
                        if (!m_LaneSignalData.HasComponent(item))
                        {
                            m_CommandBuffer.AddComponent(jobIndex, item, default(LaneSignal));
                        }
                    }
                    else if (m_LaneSignalData.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent<LaneSignal>(jobIndex, item);
                    }
                }
                else
                {
                    NetLaneArchetypeData netLaneArchetypeData = m_PrefabLaneArchetypeData[component2.m_Prefab];
                    EntityArchetype archetype = ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Slave) != 0)
                        ? netLaneArchetypeData.m_NodeSlaveArchetype
                        : (((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Master) == 0) ? netLaneArchetypeData.m_NodeLaneArchetype : netLaneArchetypeData.m_NodeMasterArchetype);
                    Entity e = m_CommandBuffer.CreateEntity(jobIndex, archetype);
                    m_CommandBuffer.SetComponent(jobIndex, e, component2);
                    m_CommandBuffer.SetComponent(jobIndex, e, lane);
                    m_CommandBuffer.SetComponent(jobIndex, e, component3);
                    m_CommandBuffer.SetComponent(jobIndex, e, curve);
                    if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, componentData6);
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Road) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component4);
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Utility) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component5);
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Master) != 0)
                    {
                        MasterLane component8 = default(MasterLane);
                        component8.m_Group = num;
                        m_CommandBuffer.SetComponent(jobIndex, e, component8);
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Slave) != 0)
                    {
                        SlaveLane component9 = default(SlaveLane);
                        component9.m_Group = num;
                        component9.m_MinIndex = 0;
                        component9.m_MaxIndex = 0;
                        component9.m_SubIndex = 0;
                        component9.m_Flags |= SlaveLaneFlags.AllowChange;
                        component9.m_Flags |= (SlaveLaneFlags)(middleConnection.m_IsSource ? 4096 : 2048);
                        m_CommandBuffer.SetComponent(jobIndex, e, component9);
                    }
                    if (isTemp)
                    {
                        m_CommandBuffer.AddComponent(jobIndex, e, in m_TempOwnerTypes);
                        m_CommandBuffer.SetComponent(jobIndex, e, component);
                        m_CommandBuffer.SetComponent(jobIndex, e, temp);
                    }
                    else
                    {
                        m_CommandBuffer.AddComponent(jobIndex, e, component);
                    }
                    if (flag)
                    {
                        m_CommandBuffer.AddComponent(jobIndex, e, default(LaneSignal));
                    }
                }
            }

            private void CreateEdgeConnectionLane(int jobIndex, ref int edgeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, Segment startSegment, Segment endSegment,
                NetCompositionData prefabCompositionData, CompositionData compositionData, NetCompositionLane prefabCompositionLaneData, ConnectPosition connectPosition,
                int connectionIndex, bool isSingleCurve, bool useGroundPosition, bool isSource, bool isTemp, Temp ownerTemp
            ) {
                float t = prefabCompositionLaneData.m_Position.x / math.max(1f, prefabCompositionData.m_Width) + 0.5f;
                Owner component = default(Owner);
                component.m_Owner = owner;
                Temp temp = default(Temp);
                if (isTemp)
                {
                    temp.m_Flags = (ownerTemp.m_Flags & (TempFlags.Create | TempFlags.Delete | TempFlags.Select | TempFlags.Modify | TempFlags.Hidden));
                    if ((ownerTemp.m_Flags & TempFlags.Replace) != 0)
                    {
                        temp.m_Flags |= TempFlags.Modify;
                    }
                }
                if ((prefabCompositionLaneData.m_Flags & LaneFlags.Road) != 0 && (connectPosition.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                {
                    connectPosition.m_LaneData.m_Lane = m_PedestrianLaneData[connectPosition.m_LaneData.m_Lane].m_NotWalkLanePrefab;
                }
                PrefabRef component2 = new PrefabRef(connectPosition.m_LaneData.m_Lane);
                CheckPrefab(ref component2.m_Prefab, ref random, out Unity.Mathematics.Random outRandom, laneBuffer);
                bool flag = false;
                Bezier4x3 bezier4x = default(Bezier4x3);
                Bezier4x3 bezier4x2 = default(Bezier4x3);
                Bezier4x3 curve;
                byte b;
                float t2;
                if (isSingleCurve)
                {
                    curve = MathUtils.Lerp(MathUtils.Join(startSegment.m_Left, endSegment.m_Left), MathUtils.Join(startSegment.m_Right, endSegment.m_Right), t);
                    curve.a.y += prefabCompositionLaneData.m_Position.y;
                    curve.b.y += prefabCompositionLaneData.m_Position.y;
                    curve.c.y += prefabCompositionLaneData.m_Position.y;
                    curve.d.y += prefabCompositionLaneData.m_Position.y;
                    if ((prefabCompositionLaneData.m_Flags & (LaneFlags.Road | LaneFlags.Pedestrian | LaneFlags.Parking | LaneFlags.Track)) != 0 && (prefabCompositionLaneData.m_Flags & LaneFlags.Invert) != 0)
                    {
                        curve = MathUtils.Invert(curve);
                    }
                    b = 2;
                    MathUtils.Distance(curve, connectPosition.m_Position, out t2);
                }
                else
                {
                    bezier4x = MathUtils.Lerp(startSegment.m_Left, startSegment.m_Right, t);
                    bezier4x2 = MathUtils.Lerp(endSegment.m_Left, endSegment.m_Right, t);
                    bezier4x.a.y += prefabCompositionLaneData.m_Position.y;
                    bezier4x.b.y += prefabCompositionLaneData.m_Position.y;
                    bezier4x.c.y += prefabCompositionLaneData.m_Position.y;
                    bezier4x.d.y += prefabCompositionLaneData.m_Position.y;
                    bezier4x2.a.y += prefabCompositionLaneData.m_Position.y;
                    bezier4x2.b.y += prefabCompositionLaneData.m_Position.y;
                    bezier4x2.c.y += prefabCompositionLaneData.m_Position.y;
                    bezier4x2.d.y += prefabCompositionLaneData.m_Position.y;
                    if ((prefabCompositionLaneData.m_Flags & (LaneFlags.Road | LaneFlags.Pedestrian | LaneFlags.Parking | LaneFlags.Track)) != 0 && (prefabCompositionLaneData.m_Flags & LaneFlags.Invert) != 0)
                    {
                        bezier4x = MathUtils.Invert(bezier4x);
                        bezier4x2 = MathUtils.Invert(bezier4x2);
                        flag = !flag;
                    }
                    float t3;
                    float num = MathUtils.Distance(bezier4x, connectPosition.m_Position, out t3);
                    if (MathUtils.Distance(bezier4x2, connectPosition.m_Position, out float t4) < num)
                    {
                        curve = bezier4x2;
                        b = 3;
                        t2 = t4;
                        flag = !flag;
                    }
                    else
                    {
                        curve = bezier4x;
                        b = 1;
                        t2 = t3;
                    }
                }
                NodeLane component3 = default(NodeLane);
                NetLaneData netLaneData = m_NetLaneData[component2.m_Prefab];
                if ((prefabCompositionLaneData.m_Flags & LaneFlags.Road) != 0)
                {
                    if (m_NetLaneData.TryGetComponent(prefabCompositionLaneData.m_Lane, out NetLaneData componentData))
                    {
                        component3.m_WidthOffset.x = componentData.m_Width - netLaneData.m_Width;
                    }
                    if (m_NetLaneData.TryGetComponent(connectPosition.m_LaneData.m_Lane, out NetLaneData componentData2))
                    {
                        component3.m_WidthOffset.y = componentData2.m_Width - netLaneData.m_Width;
                    }
                    if (isSource)
                    {
                        component3.m_WidthOffset = component3.m_WidthOffset.yx;
                    }
                    component3.m_Flags |= (NodeLaneFlags)((component3.m_WidthOffset.x != 0f) ? 1 : 0);
                    component3.m_Flags |= (NodeLaneFlags)((component3.m_WidthOffset.y != 0f) ? 2 : 0);
                    if ((componentData.m_Flags & LaneFlags.BicyclesOnly) != 0)
                    {
                        component3.m_Flags |= (NodeLaneFlags)(isSource ? 8 : 4);
                    }
                    if ((componentData2.m_Flags & LaneFlags.BicyclesOnly) != 0)
                    {
                        component3.m_Flags |= (NodeLaneFlags)(isSource ? 4 : 8);
                    }
                }
                Curve curve2 = default(Curve);
                if ((prefabCompositionLaneData.m_Flags & LaneFlags.Road) != 0)
                {
                    float num2 = math.sqrt(math.distance(connectPosition.m_Position, MathUtils.Position(curve, t2)));
                    float length = num2;
                    if (isSource)
                    {
                        Bounds1 t5 = new Bounds1(t2, 1f);
                        if (!MathUtils.ClampLength(curve, ref t5, ref length) && !isSingleCurve && !flag)
                        {
                            length = num2 - length;
                            if (b == 1)
                            {
                                curve = bezier4x2;
                                b = 3;
                            }
                            else
                            {
                                curve = bezier4x;
                                b = 1;
                            }
                            t5 = new Bounds1(0f, 1f);
                            MathUtils.ClampLength(curve, ref t5, ref length);
                        }
                        t2 = t5.max;
                    }
                    else
                    {
                        Bounds1 t6 = new Bounds1(0f, t2);
                        if (!MathUtils.ClampLengthInverse(curve, ref t6, ref length) && !isSingleCurve && flag)
                        {
                            length = num2 - length;
                            if (b == 1)
                            {
                                curve = bezier4x2;
                                b = 3;
                            }
                            else
                            {
                                curve = bezier4x;
                                b = 1;
                            }
                            t6 = new Bounds1(0f, 1f);
                            MathUtils.ClampLengthInverse(curve, ref t6, ref length);
                        }
                        t2 = t6.min;
                    }
                    float3 @float = MathUtils.Position(curve, t2);
                    float3 value = MathUtils.Tangent(curve, t2);
                    value = MathUtils.Normalize(value, value.xz);
                    if (isSource)
                    {
                        value = -value;
                    }
                    if (math.distance(connectPosition.m_Position, @float) >= 0.01f)
                    {
                        curve2.m_Bezier = NetUtils.FitCurve(@float, value, -connectPosition.m_Tangent, connectPosition.m_Position);
                    }
                    else
                    {
                        curve2.m_Bezier = NetUtils.StraightCurve(@float, connectPosition.m_Position);
                    }
                    if (isSource)
                    {
                        curve2.m_Bezier = MathUtils.Invert(curve2.m_Bezier);
                    }
                    curve2.m_Length = MathUtils.Length(curve2.m_Bezier);
                }
                else if ((prefabCompositionLaneData.m_Flags & LaneFlags.Utility) != 0)
                {
                    float3 position = MathUtils.Position(curve, t2);
                    if (useGroundPosition)
                    {
                        GetGroundPosition(owner, connectPosition.m_CurvePosition, ref position);
                    }
                    if (math.abs(position.y - connectPosition.m_Position.y) >= 0.01f)
                    {
                        curve2.m_Bezier.a = position;
                        curve2.m_Bezier.b = math.lerp(position, connectPosition.m_Position, new float3(0.25f, 0.5f, 0.25f));
                        curve2.m_Bezier.c = math.lerp(position, connectPosition.m_Position, new float3(0.75f, 0.5f, 0.75f));
                        curve2.m_Bezier.d = connectPosition.m_Position;
                    }
                    else
                    {
                        curve2.m_Bezier = NetUtils.StraightCurve(position, connectPosition.m_Position);
                    }
                    if (isSource)
                    {
                        curve2.m_Bezier = MathUtils.Invert(curve2.m_Bezier);
                    }
                    curve2.m_Length = MathUtils.Length(curve2.m_Bezier);
                }
                else
                {
                    float3 float2 = MathUtils.Position(curve, t2);
                    curve2.m_Bezier = NetUtils.StraightCurve(float2, connectPosition.m_Position);
                    if (isSource)
                    {
                        curve2.m_Bezier = MathUtils.Invert(curve2.m_Bezier);
                    }
                    curve2.m_Length = math.distance(float2, connectPosition.m_Position);
                }
                Lane lane = default(Lane);
                if (isSource)
                {
                    lane.m_StartNode = new PathNode(connectPosition.m_Owner, connectPosition.m_LaneData.m_Index, connectPosition.m_SegmentIndex);
                    lane.m_MiddleNode = new PathNode(owner, (ushort)edgeLaneIndex--);
                    lane.m_EndNode = new PathNode(owner, prefabCompositionLaneData.m_Index, b, t2);
                }
                else
                {
                    lane.m_StartNode = new PathNode(owner, prefabCompositionLaneData.m_Index, b, t2);
                    lane.m_MiddleNode = new PathNode(owner, (ushort)edgeLaneIndex--);
                    lane.m_EndNode = new PathNode(connectPosition.m_Owner, connectPosition.m_LaneData.m_Index, connectPosition.m_SegmentIndex);
                }
                CarLane component4 = default(CarLane);
                if ((prefabCompositionLaneData.m_Flags & LaneFlags.Road) != 0)
                {
                    component4.m_DefaultSpeedLimit = compositionData.m_SpeedLimit;
                    component4.m_Curviness = NetUtils.CalculateCurviness(curve2, netLaneData.m_Width);
                    component4.m_CarriagewayGroup = (ushort)((b << 8) | prefabCompositionLaneData.m_Carriageway);
                    component4.m_Flags |= (CarLaneFlags.Unsafe | CarLaneFlags.SideConnection);
                    if (isSource)
                    {
                        component4.m_Flags |= CarLaneFlags.Yield;
                    }
                    if (math.dot(MathUtils.Right(MathUtils.StartTangent(curve2.m_Bezier).xz), MathUtils.EndTangent(curve2.m_Bezier).xz) >= 0f)
                    {
                        component4.m_Flags |= CarLaneFlags.TurnRight;
                    }
                    else
                    {
                        component4.m_Flags |= CarLaneFlags.TurnLeft;
                    }
                    if ((compositionData.m_TaxiwayFlags & TaxiwayFlags.Runway) != 0)
                    {
                        component4.m_Flags |= CarLaneFlags.Runway;
                    }
                    if ((compositionData.m_RoadFlags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0)
                    {
                        component4.m_Flags |= CarLaneFlags.Highway;
                    }
                    if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                    {
                        connectPosition.m_LaneData.m_Flags &= ~LaneFlags.Pedestrian;
                        connectPosition.m_LaneData.m_Flags |= LaneFlags.Road;
                        component4.m_Flags |= (CarLaneFlags)(isSource ? 4096 : 8192);
                    }
                    connectPosition.m_LaneData.m_Flags |= (prefabCompositionLaneData.m_Flags & (LaneFlags.Slave | LaneFlags.Master));
                }
                else
                {
                    connectPosition.m_LaneData.m_Flags &= ~(LaneFlags.Slave | LaneFlags.Master);
                }
                PedestrianLane component5 = default(PedestrianLane);
                if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                {
                    component5.m_Flags |= PedestrianLaneFlags.SideConnection;
                }
                UtilityLane component6 = default(UtilityLane);
                if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Utility) != 0)
                {
                    component6.m_Flags |= UtilityLaneFlags.VerticalConnection;
                    if (m_PrefabRefData.TryGetComponent(connectPosition.m_Owner, out PrefabRef componentData3) && m_PrefabNetData.TryGetComponent(componentData3.m_Prefab, out NetData componentData4) &&
                        m_PrefabGeometryData.TryGetComponent(componentData3.m_Prefab, out NetGeometryData componentData5) && (componentData4.m_RequiredLayers & (Layer.PowerlineLow | Layer.PowerlineHigh | Layer.WaterPipe | Layer.SewagePipe)) != 0 &&
                        (componentData5.m_Flags & GeometryFlags.Marker) == 0)
                    {
                        component6.m_Flags |= UtilityLaneFlags.PipelineConnection;
                    }
                }
                uint group = (uint)(prefabCompositionLaneData.m_Group | (connectionIndex + 4 << 8));
                LaneKey laneKey = new LaneKey(lane, component2.m_Prefab, connectPosition.m_LaneData.m_Flags);
                LaneKey laneKey2 = laneKey;
                if (isTemp)
                {
                    ReplaceTempOwner(ref laneKey2, owner);
                    ReplaceTempOwner(ref laneKey2, connectPosition.m_Owner);
                    GetOriginalLane(laneBuffer, laneKey2, ref temp);
                }
                PseudoRandomSeed componentData6 = default(PseudoRandomSeed);
                if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0 && !m_PseudoRandomSeedData.TryGetComponent(temp.m_Original, out componentData6))
                {
                    componentData6 = new PseudoRandomSeed(ref outRandom);
                }
                if (laneBuffer.m_OldLanes.TryGetValue(laneKey, out Entity item))
                {
                    laneBuffer.m_OldLanes.Remove(laneKey);
                    m_CommandBuffer.SetComponent(jobIndex, item, lane);
                    m_CommandBuffer.SetComponent(jobIndex, item, component3);
                    m_CommandBuffer.SetComponent(jobIndex, item, curve2);
                    if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
                    {
                        if (!m_PseudoRandomSeedData.HasComponent(item))
                        {
                            m_CommandBuffer.AddComponent(jobIndex, item, componentData6);
                        }
                        else
                        {
                            m_CommandBuffer.SetComponent(jobIndex, item, componentData6);
                        }
                    }
                    if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Road) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, item, component4);
                    }
                    if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, item, component5);
                    }
                    if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Utility) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, item, component6);
                    }
                    if (isTemp)
                    {
                        laneBuffer.m_Updates.Add(in item);
                        m_CommandBuffer.SetComponent(jobIndex, item, temp);
                    }
                    else if (m_TempData.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent(jobIndex, item, in m_DeletedTempTypes);
                        m_CommandBuffer.AddComponent(jobIndex, item, in m_AppliedTypes);
                    }
                    else
                    {
                        laneBuffer.m_Updates.Add(in item);
                    }
                    if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Master) != 0)
                    {
                        MasterLane component7 = default(MasterLane);
                        component7.m_Group = group;
                        m_CommandBuffer.SetComponent(jobIndex, item, component7);
                    }
                    if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Slave) != 0)
                    {
                        SlaveLane component8 = default(SlaveLane);
                        component8.m_Group = group;
                        component8.m_MinIndex = prefabCompositionLaneData.m_Index;
                        component8.m_MaxIndex = prefabCompositionLaneData.m_Index;
                        component8.m_SubIndex = prefabCompositionLaneData.m_Index;
                        component8.m_Flags |= SlaveLaneFlags.AllowChange;
                        component8.m_Flags |= (SlaveLaneFlags)(isSource ? 4096 : 2048);
                        m_CommandBuffer.SetComponent(jobIndex, item, component8);
                    }
                }
                else
                {
                    NetLaneArchetypeData netLaneArchetypeData = m_PrefabLaneArchetypeData[component2.m_Prefab];
                    EntityArchetype archetype = ((connectPosition.m_LaneData.m_Flags & LaneFlags.Slave) != 0)
                        ? netLaneArchetypeData.m_NodeSlaveArchetype
                        : (((connectPosition.m_LaneData.m_Flags & LaneFlags.Master) == 0) ? netLaneArchetypeData.m_NodeLaneArchetype : netLaneArchetypeData.m_NodeMasterArchetype);
                    Entity e = m_CommandBuffer.CreateEntity(jobIndex, archetype);
                    m_CommandBuffer.SetComponent(jobIndex, e, component2);
                    m_CommandBuffer.SetComponent(jobIndex, e, lane);
                    m_CommandBuffer.SetComponent(jobIndex, e, component3);
                    m_CommandBuffer.SetComponent(jobIndex, e, curve2);
                    if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, componentData6);
                    }
                    if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Road) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component4);
                    }
                    if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component5);
                    }
                    if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Utility) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component6);
                    }
                    if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Master) != 0)
                    {
                        MasterLane component9 = default(MasterLane);
                        component9.m_Group = group;
                        m_CommandBuffer.SetComponent(jobIndex, e, component9);
                    }
                    if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Slave) != 0)
                    {
                        SlaveLane component10 = default(SlaveLane);
                        component10.m_Group = group;
                        component10.m_MinIndex = prefabCompositionLaneData.m_Index;
                        component10.m_MaxIndex = prefabCompositionLaneData.m_Index;
                        component10.m_SubIndex = prefabCompositionLaneData.m_Index;
                        component10.m_Flags |= SlaveLaneFlags.AllowChange;
                        component10.m_Flags |= (SlaveLaneFlags)(isSource ? 4096 : 2048);
                        m_CommandBuffer.SetComponent(jobIndex, e, component10);
                    }
                    if (isTemp)
                    {
                        m_CommandBuffer.AddComponent(jobIndex, e, in m_TempOwnerTypes);
                        m_CommandBuffer.SetComponent(jobIndex, e, component);
                        m_CommandBuffer.SetComponent(jobIndex, e, temp);
                    }
                    else
                    {
                        m_CommandBuffer.AddComponent(jobIndex, e, component);
                    }
                }
            }

            private bool FindAnchor(ref float3 position, ref PathNode pathNode, Entity prefab, NativeList<LaneAnchor> anchors) {
                if (!anchors.IsCreated)
                {
                    return false;
                }
                for (int i = 0; i < anchors.Length; i++)
                {
                    LaneAnchor value = anchors[i];
                    if (value.m_Prefab == prefab)
                    {
                        bool num = !value.m_PathNode.Equals(pathNode);
                        if (num)
                        {
                            position = value.m_Position;
                            pathNode = value.m_PathNode;
                        }
                        value.m_Prefab = Entity.Null;
                        anchors[i] = value;
                        return num;
                    }
                }
                return false;
            }

            private void CreateEdgeLane(int jobIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, Segment segment, NetCompositionData prefabCompositionData, CompositionData compositionData,
                DynamicBuffer<NetCompositionLane> prefabCompositionLanes, NetCompositionLane prefabCompositionLaneData, int2 segmentIndex, float2 edgeDelta, NativeList<LaneAnchor> startAnchors,
                NativeList<LaneAnchor> endAnchors, bool2 canAnchor, bool isTemp, Temp ownerTemp
            ) {
                LaneFlags laneFlags = prefabCompositionLaneData.m_Flags;
                float t = prefabCompositionLaneData.m_Position.x / math.max(1f, prefabCompositionData.m_Width) + 0.5f;
                Owner component = default(Owner);
                component.m_Owner = owner;
                Temp temp = default(Temp);
                if (isTemp)
                {
                    temp.m_Flags = (ownerTemp.m_Flags & (TempFlags.Create | TempFlags.Delete | TempFlags.Select | TempFlags.Modify | TempFlags.Hidden));
                    if ((ownerTemp.m_Flags & TempFlags.Replace) != 0)
                    {
                        temp.m_Flags |= TempFlags.Modify;
                    }
                }
                Lane laneData = default(Lane);
                int num = math.csum(segmentIndex) / 2;
                laneData.m_StartNode = new PathNode(owner, prefabCompositionLaneData.m_Index, (byte)segmentIndex.x);
                laneData.m_MiddleNode = new PathNode(owner, prefabCompositionLaneData.m_Index, (byte)num);
                laneData.m_EndNode = new PathNode(owner, prefabCompositionLaneData.m_Index, (byte)segmentIndex.y);
                PrefabRef component2 = new PrefabRef(prefabCompositionLaneData.m_Lane);
                if ((laneFlags & (LaneFlags.Master | LaneFlags.Road | LaneFlags.Track)) == (LaneFlags.Master | LaneFlags.Road | LaneFlags.Track))
                {
                    laneFlags &= ~LaneFlags.Track;
                    component2.m_Prefab = m_CarLaneData[component2.m_Prefab].m_NotTrackLanePrefab;
                }
                CheckPrefab(ref component2.m_Prefab, ref random, out Unity.Mathematics.Random outRandom, laneBuffer);
                NetLaneData netLaneData = m_NetLaneData[component2.m_Prefab];
                EdgeLane edgeLaneData = default(EdgeLane);
                edgeLaneData.m_EdgeDelta = edgeDelta;
                Curve curveData = default(Curve);
                curveData.m_Bezier = MathUtils.Lerp(segment.m_Left, segment.m_Right, t);
                curveData.m_Bezier.a.y += prefabCompositionLaneData.m_Position.y;
                curveData.m_Bezier.b.y += prefabCompositionLaneData.m_Position.y;
                curveData.m_Bezier.c.y += prefabCompositionLaneData.m_Position.y;
                curveData.m_Bezier.d.y += prefabCompositionLaneData.m_Position.y;
                bool2 @bool = false;
                if ((laneFlags & LaneFlags.FindAnchor) != 0)
                {
                    @bool.x = (canAnchor.x && !FindAnchor(ref curveData.m_Bezier.a, ref laneData.m_StartNode, prefabCompositionLaneData.m_Lane, startAnchors));
                    @bool.y = (canAnchor.y && !FindAnchor(ref curveData.m_Bezier.d, ref laneData.m_EndNode, prefabCompositionLaneData.m_Lane, endAnchors));
                }
                UtilityLane component3 = default(UtilityLane);
                HangingLane component4 = default(HangingLane);
                bool flag = false;
                if ((laneFlags & LaneFlags.Utility) != 0)
                {
                    UtilityLaneData utilityLaneData = m_UtilityLaneData[prefabCompositionLaneData.m_Lane];
                    if (utilityLaneData.m_Hanging != 0f)
                    {
                        curveData.m_Bezier.b = math.lerp(curveData.m_Bezier.a, curveData.m_Bezier.d, 0.333333343f);
                        curveData.m_Bezier.c = math.lerp(curveData.m_Bezier.a, curveData.m_Bezier.d, 2f / 3f);
                        float num2 = math.distance(curveData.m_Bezier.a.xz, curveData.m_Bezier.d.xz) * utilityLaneData.m_Hanging * 1.33333337f;
                        curveData.m_Bezier.b.y -= num2;
                        curveData.m_Bezier.c.y -= num2;
                        component4.m_Distances = 0.1f;
                        if ((laneFlags & LaneFlags.FindAnchor) != 0)
                        {
                            component4.m_Distances = math.select(component4.m_Distances, 0f, canAnchor);
                        }
                        flag = true;
                    }
                    if (@bool.x)
                    {
                        component3.m_Flags |= UtilityLaneFlags.SecondaryStartAnchor;
                    }
                    if (@bool.y)
                    {
                        component3.m_Flags |= UtilityLaneFlags.SecondaryEndAnchor;
                    }
                }
                curveData.m_Length = MathUtils.Length(curveData.m_Bezier);
                if ((laneFlags & (LaneFlags.Road | LaneFlags.Pedestrian | LaneFlags.Parking | LaneFlags.Track)) != 0 && (laneFlags & LaneFlags.Invert) != 0)
                {
                    Invert(ref laneData, ref curveData, ref edgeLaneData);
                }
                CarLane component5 = default(CarLane);
                //bool priorityChanged = false;//TODO NON-STOCK
                if ((laneFlags & LaneFlags.Road) != 0)
                {
                    component5.m_DefaultSpeedLimit = compositionData.m_SpeedLimit;
                    component5.m_Curviness = NetUtils.CalculateCurviness(curveData, netLaneData.m_Width);
                    component5.m_CarriagewayGroup = (ushort)((segmentIndex.x << 8) | prefabCompositionLaneData.m_Carriageway);
                    if ((laneFlags & LaneFlags.Invert) != 0)
                    {
                        component5.m_Flags |= CarLaneFlags.Invert;
                    }
                    if ((laneFlags & LaneFlags.Twoway) != 0)
                    {
                        component5.m_Flags |= CarLaneFlags.Twoway;
                    }
                    if ((laneFlags & LaneFlags.PublicOnly) != 0)
                    {
                        component5.m_Flags |= CarLaneFlags.PublicOnly;
                    }
                    if ((compositionData.m_TaxiwayFlags & TaxiwayFlags.Runway) != 0)
                    {
                        component5.m_Flags |= CarLaneFlags.Runway;
                    }
                    if ((compositionData.m_RoadFlags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0)
                    {
                        component5.m_Flags |= CarLaneFlags.Highway;
                    }
                    if ((laneFlags & LaneFlags.LeftLimit) != 0)
                    {
                        component5.m_Flags |= CarLaneFlags.LeftLimit;
                    }
                    if ((laneFlags & LaneFlags.RightLimit) != 0)
                    {
                        component5.m_Flags |= CarLaneFlags.RightLimit;
                    }
                    if ((laneFlags & LaneFlags.ParkingLeft) != 0)
                    {
                        component5.m_Flags |= CarLaneFlags.ParkingLeft;
                    }
                    if ((laneFlags & LaneFlags.ParkingRight) != 0)
                    {
                        component5.m_Flags |= CarLaneFlags.ParkingRight;
                    }
                    //NON-STOCK-CODE
                    if (lanePriorities.HasBuffer(owner))
                    {
                        DynamicBuffer<LanePriority> priorities = lanePriorities[owner];
                        PriorityType priority = PriorityType.Default;
                        
                        for (var i = 0; i < priorities.Length; i++)
                        {
                            LanePriority lanePriority = priorities[i];
                            if (lanePriority.laneIndex.x == prefabCompositionLaneData.m_Index)
                            {
                                priority = lanePriority.priority;
                                break;
                            }
                        }

                        if (priority != PriorityType.Default)
                        {
                            component5.m_Flags |= priority == PriorityType.Stop ? CarLaneFlags.Stop :
                            priority == PriorityType.Yield ? CarLaneFlags.Yield :
                            priority == PriorityType.RightOfWay ? CarLaneFlags.RightOfWay : 0;
                        }
                    }
                    //NON-STOCK-CODE-END
                }
                TrackLane component6 = default(TrackLane);
                if ((laneFlags & LaneFlags.Track) != 0)
                {
                    component6.m_SpeedLimit = compositionData.m_SpeedLimit;
                    component6.m_Curviness = NetUtils.CalculateCurviness(curveData, netLaneData.m_Width);
                    component6.m_Flags |= TrackLaneFlags.AllowMiddle;
                    if ((laneFlags & LaneFlags.Invert) != 0)
                    {
                        component6.m_Flags |= TrackLaneFlags.Invert;
                    }
                    if ((laneFlags & LaneFlags.Twoway) != 0)
                    {
                        component6.m_Flags |= TrackLaneFlags.Twoway;
                    }
                    if ((laneFlags & LaneFlags.Road) == 0)
                    {
                        component6.m_Flags |= TrackLaneFlags.Exclusive;
                    }
                    if (((prefabCompositionData.m_Flags.m_Left | prefabCompositionData.m_Flags.m_Right) & (CompositionFlags.Side.PrimaryStop | CompositionFlags.Side.SecondaryStop)) != 0)
                    {
                        component6.m_Flags |= TrackLaneFlags.Station;
                    }
                }
                Game.Net.ParkingLane component7 = default(Game.Net.ParkingLane);
                if ((laneFlags & LaneFlags.Parking) != 0)
                {
                    LaneFlags laneFlags2 = LaneFlags.Slave;
                    if (m_ParkingLaneData.TryGetComponent(component2.m_Prefab, out ParkingLaneData componentData) && (componentData.m_RoadTypes & ~RoadTypes.Bicycle) != 0)
                    {
                        laneFlags2 |= LaneFlags.BicyclesOnly;
                    }
                    NetCompositionLane netCompositionLane = FindClosestLane(prefabCompositionLanes, LaneFlags.Road, laneFlags2, prefabCompositionLaneData.m_Position);
                    NetCompositionLane netCompositionLane2 = FindClosestLane(prefabCompositionLanes, LaneFlags.Pedestrian, (LaneFlags)0, prefabCompositionLaneData.m_Position);
                    if (netCompositionLane.m_Lane != Entity.Null)
                    {
                        laneData.m_StartNode = new PathNode(owner, netCompositionLane.m_Index, (byte)num, 0.5f);
                        if ((laneFlags & LaneFlags.Twoway) != 0)
                        {
                            NetCompositionLane netCompositionLane3 = FindClosestLane(prefabCompositionLanes, LaneFlags.Road | (~netCompositionLane.m_Flags & LaneFlags.Invert), laneFlags2 | (netCompositionLane.m_Flags & LaneFlags.Invert),
                                netCompositionLane.m_Position, netCompositionLane.m_Carriageway);
                            if (netCompositionLane3.m_Lane != Entity.Null)
                            {
                                component7.m_AdditionalStartNode = new PathNode(owner, netCompositionLane3.m_Index, (byte)num, 0.5f);
                                component7.m_Flags |= ParkingLaneFlags.AdditionalStart;
                            }
                        }
                    }
                    if (netCompositionLane2.m_Lane != Entity.Null)
                    {
                        laneData.m_EndNode = new PathNode(owner, netCompositionLane2.m_Index, (byte)num, 0.5f);
                    }
                    if ((laneFlags & LaneFlags.Invert) != 0)
                    {
                        component7.m_Flags |= ParkingLaneFlags.Invert;
                    }
                    if ((laneFlags & LaneFlags.Virtual) != 0)
                    {
                        component7.m_Flags |= ParkingLaneFlags.VirtualLane;
                    }
                    if ((laneFlags & LaneFlags.PublicOnly) != 0)
                    {
                        component7.m_Flags |= ParkingLaneFlags.SpecialVehicles;
                    }
                    if (edgeLaneData.m_EdgeDelta.x == 0f || edgeLaneData.m_EdgeDelta.x == 1f)
                    {
                        component7.m_Flags |= ParkingLaneFlags.StartingLane;
                    }
                    if (edgeLaneData.m_EdgeDelta.y == 0f || edgeLaneData.m_EdgeDelta.y == 1f)
                    {
                        component7.m_Flags |= ParkingLaneFlags.EndingLane;
                    }
                    if (prefabCompositionLaneData.m_Position.x >= 0f)
                    {
                        component7.m_Flags |= ParkingLaneFlags.RightSide;
                    }
                    else
                    {
                        component7.m_Flags |= ParkingLaneFlags.LeftSide;
                    }
                    if ((laneFlags & LaneFlags.ParkingLeft) != 0)
                    {
                        component7.m_Flags |= ParkingLaneFlags.ParkingLeft;
                    }
                    if ((laneFlags & LaneFlags.ParkingRight) != 0)
                    {
                        component7.m_Flags |= ParkingLaneFlags.ParkingRight;
                    }
                }
                PedestrianLane component8 = default(PedestrianLane);
                if ((laneFlags & LaneFlags.Pedestrian) != 0)
                {
                    component8.m_Flags |= PedestrianLaneFlags.AllowMiddle;
                    if ((laneFlags & LaneFlags.OnWater) != 0)
                    {
                        component8.m_Flags |= PedestrianLaneFlags.OnWater;
                    }
                    if ((prefabCompositionData.m_State & (CompositionState.HasForwardRoadLanes | CompositionState.HasBackwardRoadLanes | CompositionState.HasForwardTrackLanes | CompositionState.HasBackwardTrackLanes)) == 0 &&
                        m_PedestrianLaneData.TryGetComponent(prefabCompositionLaneData.m_Lane, out PedestrianLaneData componentData2) && componentData2.m_NotWalkLanePrefab != Entity.Null)
                    {
                        Entity entity = owner;
                        Owner componentData3;
                        while (m_OwnerData.TryGetComponent(entity, out componentData3))
                        {
                            entity = componentData3.m_Owner;
                        }
                        if (m_TransformData.HasComponent(entity))
                        {
                            component8.m_Flags |= PedestrianLaneFlags.AllowBicycle;
                        }
                    }
                }
                uint group = (uint)(prefabCompositionLaneData.m_Group | (segmentIndex.x << 8));
                LaneKey laneKey = new LaneKey(laneData, component2.m_Prefab, laneFlags);
                LaneKey laneKey2 = laneKey;
                if (isTemp)
                {
                    ReplaceTempOwner(ref laneKey2, owner);
                    GetOriginalLane(laneBuffer, laneKey2, ref temp);
                }
                PseudoRandomSeed componentData4 = default(PseudoRandomSeed);
                if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0 && !m_PseudoRandomSeedData.TryGetComponent(temp.m_Original, out componentData4))
                {
                    componentData4 = new PseudoRandomSeed(ref outRandom);
                }
                Entity item;
                bool flag2 = laneBuffer.m_OldLanes.TryGetValue(laneKey, out item);
                if (flag2)
                {
                    Curve curve = m_CurveData[item];
                    if (math.dot(curve.m_Bezier.d - curve.m_Bezier.a, curveData.m_Bezier.d - curveData.m_Bezier.a) < 0f)
                    {
                        flag2 = false;
                    }
                }
                //NON-STOCK-CODE
                // else if (priorityChanged)
                // {
                //     item = owner;
                //     flag2 = true;
                // }
                //NON-STOCK-CODE-END
                bool flag3 = m_PrefabData.IsComponentEnabled(component2.m_Prefab);
                if (flag2)
                {
                    laneBuffer.m_OldLanes.Remove(laneKey);
                    m_CommandBuffer.SetComponent(jobIndex, item, laneData);
                    m_CommandBuffer.SetComponent(jobIndex, item, edgeLaneData);
                    m_CommandBuffer.SetComponent(jobIndex, item, curveData);
                    if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
                    {
                        if (!m_PseudoRandomSeedData.HasComponent(item))
                        {
                            m_CommandBuffer.AddComponent(jobIndex, item, componentData4);
                        }
                        else
                        {
                            m_CommandBuffer.SetComponent(jobIndex, item, componentData4);
                        }
                    }
                    if (flag3)
                    {
                        if ((laneFlags & LaneFlags.Road) != 0)
                        {
                            m_CommandBuffer.SetComponent(jobIndex, item, component5);
                        }
                        if ((laneFlags & LaneFlags.Track) != 0)
                        {
                            m_CommandBuffer.SetComponent(jobIndex, item, component6);
                        }
                        if ((laneFlags & LaneFlags.Parking) != 0)
                        {
                            m_CommandBuffer.SetComponent(jobIndex, item, component7);
                        }
                        if ((laneFlags & LaneFlags.Pedestrian) != 0)
                        {
                            m_CommandBuffer.SetComponent(jobIndex, item, component8);
                        }
                        if ((laneFlags & LaneFlags.Utility) != 0)
                        {
                            m_CommandBuffer.SetComponent(jobIndex, item, component3);
                        }
                        if (flag)
                        {
                            m_CommandBuffer.AddComponent(jobIndex, item, component4);
                        }
                    }
                    if (isTemp)
                    {
                        laneBuffer.m_Updates.Add(in item);
                        m_CommandBuffer.SetComponent(jobIndex, item, temp);
                    }
                    else if (m_TempData.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent(jobIndex, item, in m_DeletedTempTypes);
                        m_CommandBuffer.AddComponent(jobIndex, item, in m_AppliedTypes);
                    }
                    else
                    {
                        laneBuffer.m_Updates.Add(in item);
                    }
                    if ((laneFlags & LaneFlags.Master) != 0)
                    {
                        MasterLane component9 = default(MasterLane);
                        component9.m_Group = group;
                        m_CommandBuffer.SetComponent(jobIndex, item, component9);
                    }
                    if ((laneFlags & LaneFlags.Slave) != 0)
                    {
                        SlaveLane component10 = default(SlaveLane);
                        component10.m_Group = group;
                        component10.m_MinIndex = prefabCompositionLaneData.m_Index;
                        component10.m_MaxIndex = prefabCompositionLaneData.m_Index;
                        component10.m_SubIndex = prefabCompositionLaneData.m_Index;
                        component10.m_Flags |= SlaveLaneFlags.AllowChange;
                        if ((laneFlags & LaneFlags.DisconnectedStart) != 0)
                        {
                            component10.m_Flags |= SlaveLaneFlags.StartingLane;
                        }
                        if ((laneFlags & LaneFlags.DisconnectedEnd) != 0)
                        {
                            component10.m_Flags |= SlaveLaneFlags.EndingLane;
                        }
                        m_CommandBuffer.SetComponent(jobIndex, item, component10);
                    }
                    return;
                }
                EntityArchetype entityArchetype = default(EntityArchetype);
                NetLaneArchetypeData netLaneArchetypeData = m_PrefabLaneArchetypeData[component2.m_Prefab];
                entityArchetype = (((laneFlags & LaneFlags.Slave) != 0)
                    ? netLaneArchetypeData.m_EdgeSlaveArchetype
                    : (((laneFlags & LaneFlags.Master) == 0) ? netLaneArchetypeData.m_EdgeLaneArchetype : netLaneArchetypeData.m_EdgeMasterArchetype));
                Entity e = m_CommandBuffer.CreateEntity(jobIndex, entityArchetype);
                m_CommandBuffer.SetComponent(jobIndex, e, component2);
                m_CommandBuffer.SetComponent(jobIndex, e, laneData);
                m_CommandBuffer.SetComponent(jobIndex, e, edgeLaneData);
                m_CommandBuffer.SetComponent(jobIndex, e, curveData);
                if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
                {
                    m_CommandBuffer.SetComponent(jobIndex, e, componentData4);
                }
                if (flag3)
                {
                    if ((laneFlags & LaneFlags.Road) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component5);
                    }
                    if ((laneFlags & LaneFlags.Track) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component6);
                    }
                    if ((laneFlags & LaneFlags.Parking) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component7);
                    }
                    if ((laneFlags & LaneFlags.Pedestrian) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component8);
                    }
                    if ((laneFlags & LaneFlags.Utility) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component3);
                    }
                    if (flag)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component4);
                    }
                }
                if ((laneFlags & LaneFlags.Master) != 0)
                {
                    MasterLane component11 = default(MasterLane);
                    component11.m_Group = group;
                    m_CommandBuffer.SetComponent(jobIndex, e, component11);
                }
                if ((laneFlags & LaneFlags.Slave) != 0)
                {
                    SlaveLane component12 = default(SlaveLane);
                    component12.m_Group = group;
                    component12.m_MinIndex = prefabCompositionLaneData.m_Index;
                    component12.m_MaxIndex = prefabCompositionLaneData.m_Index;
                    component12.m_SubIndex = prefabCompositionLaneData.m_Index;
                    component12.m_Flags |= SlaveLaneFlags.AllowChange;
                    if ((laneFlags & LaneFlags.DisconnectedStart) != 0)
                    {
                        component12.m_Flags |= SlaveLaneFlags.StartingLane;
                    }
                    if ((laneFlags & LaneFlags.DisconnectedEnd) != 0)
                    {
                        component12.m_Flags |= SlaveLaneFlags.EndingLane;
                    }
                    m_CommandBuffer.SetComponent(jobIndex, e, component12);
                }
                if (isTemp)
                {
                    m_CommandBuffer.AddComponent(jobIndex, e, in m_TempOwnerTypes);
                    m_CommandBuffer.SetComponent(jobIndex, e, component);
                    m_CommandBuffer.SetComponent(jobIndex, e, temp);
                }
                else
                {
                    m_CommandBuffer.AddComponent(jobIndex, e, component);
                }
            }

            private void CreateObjectLane(int jobIndex, ref Unity.Mathematics.Random random, Entity owner, Entity original, LaneBuffer laneBuffer, Game.Objects.Transform transform, Game.Prefabs.SubLane prefabSubLane,
                int laneIndex, bool sampleTerrain, bool cutForTraffic, bool isTemp, Temp ownerTemp, NativeList<ClearAreaData> clearAreas
            ) {
                if (original != Entity.Null)
                {
                    prefabSubLane.m_Prefab = m_PrefabRefData[original].m_Prefab;
                }
                Owner component = default(Owner);
                component.m_Owner = owner;
                Temp temp = default(Temp);
                if (isTemp)
                {
                    temp.m_Flags = (ownerTemp.m_Flags & (TempFlags.Create | TempFlags.Delete | TempFlags.Select | TempFlags.Modify | TempFlags.Hidden));
                    if ((ownerTemp.m_Flags & TempFlags.Replace) != 0)
                    {
                        temp.m_Flags |= TempFlags.Modify;
                    }
                }
                Lane laneData = default(Lane);
                if (original != Entity.Null)
                {
                    laneData = m_LaneData[original];
                }
                else
                {
                    laneData.m_StartNode = new PathNode(owner, (ushort)prefabSubLane.m_NodeIndex.x);
                    laneData.m_MiddleNode = new PathNode(owner, (ushort)(65535 - laneIndex));
                    laneData.m_EndNode = new PathNode(owner, (ushort)prefabSubLane.m_NodeIndex.y);
                }
                PrefabRef component2 = new PrefabRef(prefabSubLane.m_Prefab);
                Unity.Mathematics.Random outRandom = random;
                Curve curveData = default(Curve);
                if (original != Entity.Null)
                {
                    curveData = m_CurveData[original];
                }
                else
                {
                    CheckPrefab(ref component2.m_Prefab, ref random, out outRandom, laneBuffer);
                    curveData.m_Bezier.a = ObjectUtils.LocalToWorld(transform, prefabSubLane.m_Curve.a);
                    curveData.m_Bezier.b = ObjectUtils.LocalToWorld(transform, prefabSubLane.m_Curve.b);
                    curveData.m_Bezier.c = ObjectUtils.LocalToWorld(transform, prefabSubLane.m_Curve.c);
                    curveData.m_Bezier.d = ObjectUtils.LocalToWorld(transform, prefabSubLane.m_Curve.d);
                }
                NetLaneData netLaneData = m_NetLaneData[component2.m_Prefab];
                UtilityLane component3 = default(UtilityLane);
                if ((netLaneData.m_Flags & LaneFlags.Utility) != 0)
                {
                    UtilityLaneData utilityLaneData = m_UtilityLaneData[prefabSubLane.m_Prefab];
                    if (cutForTraffic)
                    {
                        component3.m_Flags |= UtilityLaneFlags.CutForTraffic;
                    }
                    if (utilityLaneData.m_Hanging != 0f)
                    {
                        curveData.m_Bezier.b = math.lerp(curveData.m_Bezier.a, curveData.m_Bezier.d, 0.333333343f);
                        curveData.m_Bezier.c = math.lerp(curveData.m_Bezier.a, curveData.m_Bezier.d, 2f / 3f);
                        float num = math.distance(curveData.m_Bezier.a.xz, curveData.m_Bezier.d.xz) * utilityLaneData.m_Hanging * 1.33333337f;
                        curveData.m_Bezier.b.y -= num;
                        curveData.m_Bezier.c.y -= num;
                    }
                }
                bool2 @bool = false;
                float2 elevation = 0f;
                if (original != Entity.Null)
                {
                    if (m_ElevationData.TryGetComponent(original, out Elevation componentData))
                    {
                        @bool = (componentData.m_Elevation != float.MinValue);
                        elevation = componentData.m_Elevation;
                    }
                }
                else
                {
                    @bool = (prefabSubLane.m_ParentMesh >= 0);
                    elevation = math.select(float.MinValue, new float2(prefabSubLane.m_Curve.a.y, prefabSubLane.m_Curve.d.y), @bool);
                }
                if (ClearAreaHelpers.ShouldClear(clearAreas, curveData.m_Bezier, !math.all(@bool)))
                {
                    return;
                }
                if (sampleTerrain && !math.all(@bool))
                {
                    Curve curve = NetUtils.AdjustPosition(curveData, @bool.x, math.any(@bool), @bool.y, ref m_TerrainHeightData);
                    if (math.any(math.abs(curve.m_Bezier.y.abcd - curveData.m_Bezier.y.abcd) >= 0.01f))
                    {
                        curveData = curve;
                    }
                }
                curveData.m_Length = MathUtils.Length(curveData.m_Bezier);
                if (original == Entity.Null && (netLaneData.m_Flags & (LaneFlags.Road | LaneFlags.Pedestrian | LaneFlags.Parking | LaneFlags.Track)) != 0 && (netLaneData.m_Flags & LaneFlags.Invert) != 0)
                {
                    EdgeLane edgeLaneData = default(EdgeLane);
                    Invert(ref laneData, ref curveData, ref edgeLaneData);
                }
                CarLane component4 = default(CarLane);
                if ((netLaneData.m_Flags & LaneFlags.Road) != 0)
                {
                    component4.m_DefaultSpeedLimit = 3f;
                    component4.m_Curviness = NetUtils.CalculateCurviness(curveData, netLaneData.m_Width);
                    component4.m_CarriagewayGroup = (ushort)laneIndex;
                    if ((netLaneData.m_Flags & LaneFlags.Invert) != 0)
                    {
                        component4.m_Flags |= CarLaneFlags.Invert;
                    }
                    if ((netLaneData.m_Flags & LaneFlags.Twoway) != 0)
                    {
                        component4.m_Flags |= CarLaneFlags.Twoway;
                    }
                    if ((netLaneData.m_Flags & LaneFlags.PublicOnly) != 0)
                    {
                        component4.m_Flags |= CarLaneFlags.PublicOnly;
                    }
                }
                TrackLane component5 = default(TrackLane);
                if ((netLaneData.m_Flags & LaneFlags.Track) != 0)
                {
                    component5.m_SpeedLimit = 3f;
                    component5.m_Curviness = NetUtils.CalculateCurviness(curveData, netLaneData.m_Width);
                    component5.m_Flags |= (TrackLaneFlags.AllowMiddle | TrackLaneFlags.Station);
                    if ((netLaneData.m_Flags & LaneFlags.Invert) != 0)
                    {
                        component5.m_Flags |= TrackLaneFlags.Invert;
                    }
                    if ((netLaneData.m_Flags & LaneFlags.Twoway) != 0)
                    {
                        component5.m_Flags |= TrackLaneFlags.Twoway;
                    }
                    if ((netLaneData.m_Flags & LaneFlags.Road) == 0)
                    {
                        component5.m_Flags |= TrackLaneFlags.Exclusive;
                    }
                }
                Game.Net.ParkingLane component6 = default(Game.Net.ParkingLane);
                if ((netLaneData.m_Flags & LaneFlags.Parking) != 0)
                {
                    if ((netLaneData.m_Flags & LaneFlags.Invert) != 0)
                    {
                        component6.m_Flags |= ParkingLaneFlags.Invert;
                    }
                    if ((netLaneData.m_Flags & LaneFlags.Virtual) != 0)
                    {
                        component6.m_Flags |= ParkingLaneFlags.VirtualLane;
                    }
                    if ((netLaneData.m_Flags & LaneFlags.PublicOnly) != 0)
                    {
                        component6.m_Flags |= ParkingLaneFlags.SpecialVehicles;
                    }
                    component6.m_Flags |= (ParkingLaneFlags.StartingLane | ParkingLaneFlags.EndingLane | ParkingLaneFlags.FindConnections);
                }
                PedestrianLane component7 = default(PedestrianLane);
                if ((netLaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                {
                    component7.m_Flags |= PedestrianLaneFlags.AllowMiddle;
                    if ((netLaneData.m_Flags & LaneFlags.OnWater) != 0)
                    {
                        component7.m_Flags |= PedestrianLaneFlags.OnWater;
                    }
                }
                LaneKey laneKey = new LaneKey(laneData, component2.m_Prefab, netLaneData.m_Flags);
                if (original != Entity.Null)
                {
                    temp.m_Original = original;
                }
                else
                {
                    LaneKey laneKey2 = laneKey;
                    if (isTemp)
                    {
                        ReplaceTempOwner(ref laneKey2, owner);
                        GetOriginalLane(laneBuffer, laneKey2, ref temp);
                    }
                }
                PseudoRandomSeed componentData2 = default(PseudoRandomSeed);
                if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0 && !m_PseudoRandomSeedData.TryGetComponent(temp.m_Original, out componentData2))
                {
                    componentData2 = new PseudoRandomSeed(ref outRandom);
                }
                if (laneBuffer.m_OldLanes.TryGetValue(laneKey, out Entity item))
                {
                    laneBuffer.m_OldLanes.Remove(laneKey);
                    m_CommandBuffer.SetComponent(jobIndex, item, laneData);
                    m_CommandBuffer.SetComponent(jobIndex, item, curveData);
                    if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
                    {
                        if (!m_PseudoRandomSeedData.HasComponent(item))
                        {
                            m_CommandBuffer.AddComponent(jobIndex, item, componentData2);
                        }
                        else
                        {
                            m_CommandBuffer.SetComponent(jobIndex, item, componentData2);
                        }
                    }
                    if ((netLaneData.m_Flags & LaneFlags.Road) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, item, component4);
                    }
                    if ((netLaneData.m_Flags & LaneFlags.Track) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, item, component5);
                    }
                    if ((netLaneData.m_Flags & LaneFlags.Parking) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, item, component6);
                    }
                    if ((netLaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, item, component7);
                    }
                    if ((netLaneData.m_Flags & LaneFlags.Utility) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, item, component3);
                    }
                    if (math.any(@bool))
                    {
                        m_CommandBuffer.AddComponent(jobIndex, item, new Elevation(elevation));
                    }
                    else if (m_ElevationData.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent<Elevation>(jobIndex, item);
                    }
                    if (isTemp)
                    {
                        laneBuffer.m_Updates.Add(in item);
                        m_CommandBuffer.SetComponent(jobIndex, item, temp);
                    }
                    else if (m_TempData.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent(jobIndex, item, in m_DeletedTempTypes);
                        m_CommandBuffer.AddComponent(jobIndex, item, in m_AppliedTypes);
                    }
                    else
                    {
                        laneBuffer.m_Updates.Add(in item);
                    }
                    return;
                }
                NetLaneArchetypeData netLaneArchetypeData = m_PrefabLaneArchetypeData[component2.m_Prefab];
                Entity e = m_CommandBuffer.CreateEntity(jobIndex, netLaneArchetypeData.m_LaneArchetype);
                m_CommandBuffer.SetComponent(jobIndex, e, component2);
                m_CommandBuffer.SetComponent(jobIndex, e, laneData);
                m_CommandBuffer.SetComponent(jobIndex, e, curveData);
                if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
                {
                    m_CommandBuffer.SetComponent(jobIndex, e, componentData2);
                }
                if ((netLaneData.m_Flags & LaneFlags.Road) != 0)
                {
                    m_CommandBuffer.SetComponent(jobIndex, e, component4);
                }
                if ((netLaneData.m_Flags & LaneFlags.Track) != 0)
                {
                    m_CommandBuffer.SetComponent(jobIndex, e, component5);
                }
                if ((netLaneData.m_Flags & LaneFlags.Parking) != 0)
                {
                    m_CommandBuffer.SetComponent(jobIndex, e, component6);
                }
                if ((netLaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                {
                    m_CommandBuffer.SetComponent(jobIndex, e, component7);
                }
                if ((netLaneData.m_Flags & LaneFlags.Utility) != 0)
                {
                    m_CommandBuffer.SetComponent(jobIndex, e, component3);
                }
                if ((netLaneData.m_Flags & LaneFlags.Secondary) != 0)
                {
                    m_CommandBuffer.RemoveComponent<SecondaryLane>(jobIndex, e);
                }
                if (isTemp)
                {
                    m_CommandBuffer.AddComponent(jobIndex, e, in m_TempOwnerTypes);
                    m_CommandBuffer.SetComponent(jobIndex, e, component);
                    m_CommandBuffer.SetComponent(jobIndex, e, temp);
                    if (original != Entity.Null)
                    {
                        if (m_OverriddenData.HasComponent(original))
                        {
                            m_CommandBuffer.AddComponent(jobIndex, e, default(Overridden));
                        }
                        if (m_CutRanges.TryGetBuffer(original, out DynamicBuffer<CutRange> bufferData))
                        {
                            m_CommandBuffer.AddBuffer<CutRange>(jobIndex, e).CopyFrom(bufferData);
                        }
                    }
                }
                else
                {
                    m_CommandBuffer.AddComponent(jobIndex, e, component);
                }
                if (math.any(@bool))
                {
                    m_CommandBuffer.AddComponent(jobIndex, e, new Elevation(elevation));
                }
            }

            /// <summary>
            /// Replace Owner in LaneKey
            /// </summary>
            private void ReplaceTempOwner(ref LaneKey laneKey, Entity owner) {
                if (m_TempData.HasComponent(owner))
                {
                    Temp temp = m_TempData[owner];
                    if (temp.m_Original != Entity.Null && (!m_EdgeData.HasComponent(temp.m_Original) || m_EdgeData.HasComponent(owner)))
                    {
                        laneKey.ReplaceOwner(owner, temp.m_Original);
                    }
                }
            }

            private void GetOriginalLane(LaneBuffer laneBuffer, LaneKey laneKey, ref Temp temp) {
                if (laneBuffer.m_OriginalLanes.TryGetValue(laneKey, out Entity item))
                {
                    temp.m_Original = item;
                    laneBuffer.m_OriginalLanes.Remove(laneKey);
                }
            }

            private void GetMiddleConnectionCurves(Entity node, NativeList<EdgeTarget> edgeTargets) {
                edgeTargets.Clear();
                float3 position = m_NodeData[node].m_Position;
                EdgeIterator edgeIterator = new EdgeIterator(Entity.Null, node, m_Edges, m_EdgeData, m_TempData, m_HiddenData, includeMiddleConnections: true);
                EdgeIteratorValue value;
                EdgeTarget value2 = default(EdgeTarget);
                while (edgeIterator.GetNext(out value))
                {
                    Logger.DebugLaneSystem($"(GetMiddleConnectionCurves) Iterating edges of ({node}): {value.m_Edge} isTemp: {m_TempData.HasComponent(value.m_Edge)} isEnd: {value.m_End} isMiddle: {value.m_Middle}");
                    if (value.m_Middle)
                    {
                        Edge edge = m_EdgeData[value.m_Edge];
                        EdgeNodeGeometry geometry = m_StartNodeGeometryData[value.m_Edge].m_Geometry;
                        EdgeNodeGeometry geometry2 = m_EndNodeGeometryData[value.m_Edge].m_Geometry;
                        value2.m_Edge = value.m_Edge;
                        value2.m_StartNode = ((math.any(geometry.m_Left.m_Length > 0.05f) | math.any(geometry.m_Right.m_Length > 0.05f)) ? edge.m_Start : value.m_Edge);
                        value2.m_EndNode = ((math.any(geometry2.m_Left.m_Length > 0.05f) | math.any(geometry2.m_Right.m_Length > 0.05f)) ? edge.m_End : value.m_Edge);
                        Curve curve = m_CurveData[value.m_Edge];
                        EdgeGeometry edgeGeometry = m_EdgeGeometryData[value.m_Edge];
                        MathUtils.Distance(curve.m_Bezier.xz, position.xz, out float t);
                        float3 @float = MathUtils.Position(curve.m_Bezier, t);
                        if (math.dot(y: MathUtils.Right(MathUtils.Tangent(curve.m_Bezier, t).xz), x: position.xz - @float.xz) < 0f)
                        {
                            value2.m_StartPos = edgeGeometry.m_Start.m_Left.a;
                            value2.m_EndPos = edgeGeometry.m_End.m_Left.d;
                            value2.m_StartTangent = math.normalizesafe(-MathUtils.StartTangent(edgeGeometry.m_Start.m_Left));
                            value2.m_EndTangent = math.normalizesafe(MathUtils.EndTangent(edgeGeometry.m_End.m_Left));
                        }
                        else
                        {
                            value2.m_StartPos = edgeGeometry.m_Start.m_Right.a;
                            value2.m_EndPos = edgeGeometry.m_End.m_Right.d;
                            value2.m_StartTangent = math.normalizesafe(-MathUtils.StartTangent(edgeGeometry.m_Start.m_Right));
                            value2.m_EndTangent = math.normalizesafe(MathUtils.EndTangent(edgeGeometry.m_End.m_Right));
                        }
                        edgeTargets.Add(in value2);
                    }
                }
            }

            private void FilterNodeConnectPositions(Entity owner, Entity original, NativeList<ConnectPosition> connectPositions, NativeList<EdgeTarget> edgeTargets) {
                int num = 0;
                int num2 = -1;
                bool flag = false;
                for (int i = 0; i < connectPositions.Length; i++)
                {
                    ConnectPosition connectPosition = connectPositions[i];
                    if (connectPosition.m_GroupIndex != num2)
                    {
                        num2 = connectPosition.m_GroupIndex;
                        flag = false;
                    }
                    if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Master) != 0)
                    {
                        if (!flag)
                        {
                            for (int j = i + 1; j < connectPositions.Length; j++)
                            {
                                ConnectPosition connectPosition2 = connectPositions[j];
                                if (connectPosition2.m_GroupIndex != num2)
                                {
                                    break;
                                }
                                if (CheckConnectPosition(owner, original, connectPosition2, edgeTargets))
                                {
                                    flag = true;
                                    break;
                                }
                            }
                            if (!flag)
                            {
                                continue;
                            }
                        }
                    }
                    else
                    {
                        if (!CheckConnectPosition(owner, original, connectPosition, edgeTargets))
                        {
                            continue;
                        }
                        flag = true;
                    }
                    connectPositions[num++] = connectPosition;
                }
                connectPositions.RemoveRange(num, connectPositions.Length - num);
            }

            private bool CheckConnectPosition(Entity owner, Entity original, ConnectPosition connectPosition, NativeList<EdgeTarget> edgeTargets) {
                Entity lhs = Entity.Null;
                float num = float.MaxValue;
                for (int i = 0; i < edgeTargets.Length; i++)
                {
                    EdgeTarget edgeTarget = edgeTargets[i];
                    float2 x = connectPosition.m_Position.xz - edgeTarget.m_StartPos.xz;
                    float2 x2 = connectPosition.m_Position.xz - edgeTarget.m_EndPos.xz;
                    float num2 = math.dot(x, edgeTarget.m_StartTangent.xz);
                    float num3 = math.dot(x2, edgeTarget.m_EndTangent.xz);
                    float num4 = math.length(x);
                    float num5 = math.length(x2);
                    Entity entity;
                    float num6;
                    if (num2 > 0f)
                    {
                        entity = edgeTarget.m_StartNode;
                        num6 = num4 + num2;
                    }
                    else if (num3 > 0f)
                    {
                        entity = edgeTarget.m_EndNode;
                        num6 = num5 + num3;
                    }
                    else
                    {
                        entity = edgeTarget.m_Edge;
                        num6 = math.select(num4 + num2, num5 + num3, num5 < num4);
                    }
                    if (num6 < num)
                    {
                        lhs = entity;
                        num = num6;
                    }
                }
                if (!(lhs == owner))
                {
                    return lhs == original;
                }
                return true;
            }

            private void GetMiddleConnections(Entity owner, Entity original, NativeList<MiddleConnection> middleConnections, NativeList<EdgeTarget> tempEdgeTargets, NativeList<ConnectPosition> tempBuffer1,
                NativeList<ConnectPosition> tempBuffer2, ref int groupIndex
            ) {
                EdgeIterator edgeIterator = new EdgeIterator(Entity.Null, owner, m_Edges, m_EdgeData, m_TempData, m_HiddenData);
                StackList<EdgeIteratorValueSorted> list = stackalloc EdgeIteratorValueSorted[edgeIterator.GetMaxCount()];
                edgeIterator.AddSorted(ref m_BuildOrderData, ref list);
                for (int i = 0; i < list.Length; i++)
                {
                    EdgeIteratorValueSorted edgeIteratorValueSorted = list[i];
                    Logger.DebugLaneSystem($"(GetMiddleConnections) Iterating edges of ({owner}): {edgeIteratorValueSorted.m_Edge} isTemp: {m_TempData.HasComponent(edgeIteratorValueSorted.m_Edge)} isEnd: {edgeIteratorValueSorted.m_End} isMiddle: {edgeIteratorValueSorted.m_Middle}");
                    DynamicBuffer<ConnectedNode> dynamicBuffer = m_Nodes[edgeIteratorValueSorted.m_Edge];
                    for (int j = 0; j < dynamicBuffer.Length; j++)
                    {
                        ConnectedNode connectedNode = dynamicBuffer[j];
                        GetMiddleConnectionCurves(connectedNode.m_Node, tempEdgeTargets);
                        bool flag = false;
                        for (int k = 0; k < tempEdgeTargets.Length; k++)
                        {
                            EdgeTarget edgeTarget = tempEdgeTargets[k];
                            if (edgeTarget.m_StartNode == owner || edgeTarget.m_StartNode == original || edgeTarget.m_EndNode == owner || edgeTarget.m_EndNode == original)
                            {
                                flag = true;
                                break;
                            }
                        }
                        if (!flag)
                        {
                            continue;
                        }
                        int groupIndex2 = groupIndex;
                        GetNodeConnectPositions(connectedNode.m_Node, connectedNode.m_CurvePosition, tempBuffer1, tempBuffer2, includeAnchored: true, ref groupIndex2, out float _, out float _, out CompositionFlags _);
                        FilterNodeConnectPositions(owner, original, tempBuffer1, tempEdgeTargets);
                        FilterNodeConnectPositions(owner, original, tempBuffer2, tempEdgeTargets);
                        Entity entity = default(Entity);
                        if (tempBuffer1.Length != 0)
                        {
                            entity = tempBuffer1[0].m_Owner;
                        }
                        else if (tempBuffer2.Length != 0)
                        {
                            entity = tempBuffer2[0].m_Owner;
                        }
                        if (entity != Entity.Null)
                        {
                            flag = false;
                            for (int l = 0; l < middleConnections.Length; l++)
                            {
                                if (middleConnections[l].m_ConnectPosition.m_Owner == entity)
                                {
                                    flag = true;
                                    break;
                                }
                            }
                            if (!flag)
                            {
                                groupIndex = groupIndex2;
                                for (int m = 0; m < tempBuffer1.Length; m++)
                                {
                                    MiddleConnection value = default(MiddleConnection);
                                    value.m_ConnectPosition = tempBuffer1[m];
                                    value.m_ConnectPosition.m_IsSideConnection = true;
                                    value.m_SourceEdge = edgeIteratorValueSorted.m_Edge;
                                    value.m_SourceNode = connectedNode.m_Node;
                                    value.m_SortIndex = middleConnections.Length;
                                    value.m_Distance = float.MaxValue;
                                    value.m_IsSource = true;
                                    middleConnections.Add(in value);
                                }
                                for (int n = 0; n < tempBuffer2.Length; n++)
                                {
                                    MiddleConnection value2 = default(MiddleConnection);
                                    value2.m_ConnectPosition = tempBuffer2[n];
                                    value2.m_ConnectPosition.m_IsSideConnection = true;
                                    value2.m_SourceEdge = edgeIteratorValueSorted.m_Edge;
                                    value2.m_SourceNode = connectedNode.m_Node;
                                    value2.m_SortIndex = middleConnections.Length;
                                    value2.m_Distance = float.MaxValue;
                                    value2.m_IsSource = false;
                                    middleConnections.Add(in value2);
                                }
                            }
                        }
                        tempBuffer1.Clear();
                        tempBuffer2.Clear();
                    }
                }
            }

            private RoadTypes GetRoundaboutRoadPassThrough(Entity owner)
            {
                RoadTypes roadTypes = RoadTypes.None;
                if (m_SubObjects.TryGetBuffer(owner, out DynamicBuffer<Game.Objects.SubObject> bufferData))
                {
                    for (int i = 0; i < bufferData.Length; i++)
                    {
                        if (m_PrefabRefData.TryGetComponent(bufferData[i].m_SubObject, out PrefabRef componentData) && m_PrefabNetObjectData.TryGetComponent(componentData.m_Prefab, out NetObjectData componentData2) && (componentData2.m_CompositionFlags.m_General & CompositionFlags.General.Roundabout) != 0)
                        {
                            roadTypes |= componentData2.m_RoadPassThrough;
                        }
                    }
                }
                return roadTypes;
            }
            
            private void GetNodeConnectPositions(Entity owner, float curvePosition, NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer, bool includeAnchored, ref int groupIndex,
                out float middleRadius, out float roundaboutSize, out CompositionFlags intersectionFlags
            ) {
                middleRadius = 0f;
                roundaboutSize = 0f;
                intersectionFlags = default(CompositionFlags);
                bool hasConnectedEdges = false;
                NativeParallelHashSet<Entity> anchorPrefabs = default(NativeParallelHashSet<Entity>);
                float2 elevation = default(float2);
                if (m_ElevationData.HasComponent(owner))
                {
                    elevation = m_ElevationData[owner].m_Elevation;
                }
                
                PrefabRef prefabRef = m_PrefabRefData[owner];
                NetGeometryData prefabGeometryData = m_PrefabGeometryData[prefabRef.m_Prefab];
                EdgeIterator edgeIterator = new EdgeIterator(Entity.Null, owner, m_Edges, m_EdgeData, m_TempData, m_HiddenData);
                StackList<EdgeIteratorValueSorted> list = stackalloc EdgeIteratorValueSorted[edgeIterator.GetMaxCount()];
                edgeIterator.AddSorted(ref m_BuildOrderData, ref list);
                for (int i = 0; i < list.Length; i++)
                {
                    EdgeIteratorValueSorted edgeIteratorValueSorted = list[i];
                    Logger.DebugLaneSystem($"(GetNodeConnectPos) Iterating edges of ({owner}): {edgeIteratorValueSorted.m_Edge} isTemp: {m_TempData.HasComponent(edgeIteratorValueSorted.m_Edge)} isEnd: {edgeIteratorValueSorted.m_End} isMiddle: {edgeIteratorValueSorted.m_Middle}");
                    GetNodeConnectPositions(owner, edgeIteratorValueSorted.m_Edge, edgeIteratorValueSorted.m_End, groupIndex++, curvePosition, elevation, prefabGeometryData, sourceBuffer, targetBuffer, includeAnchored, ref middleRadius, ref roundaboutSize, ref intersectionFlags, ref anchorPrefabs);
                    hasConnectedEdges = true;
                }
                
                if (!hasConnectedEdges && m_DefaultNetLanes.HasBuffer(prefabRef.m_Prefab))
                {
                    Node node = m_NodeData[owner];
                    if (m_NodeGeometryData.TryGetComponent(owner, out NodeGeometry componentData))
                    {
                        node.m_Position.y = componentData.m_Position;
                    }
                    DynamicBuffer<DefaultNetLane> dynamicBuffer = m_DefaultNetLanes[prefabRef.m_Prefab];
                    LaneFlags laneFlags = (!includeAnchored) ? LaneFlags.FindAnchor : ((LaneFlags)0);
                    for (int j = 0; j < dynamicBuffer.Length; j++)
                    {
                        NetCompositionLane laneData = new NetCompositionLane(dynamicBuffer[j]);
                        if ((laneData.m_Flags & LaneFlags.Utility) == 0 || ((laneData.m_Flags & laneFlags) != 0 && IsAnchored(owner, ref anchorPrefabs, laneData.m_Lane)))
                        {
                            continue;
                        }
                        bool flag2 = (laneData.m_Flags & LaneFlags.Invert) != 0;
                        if (((int)laneData.m_Flags & (flag2 ? 512/*DisconnectedEnd*/ : 256/*DisconnectedStart*/)) == 0)
                        {
                            laneData.m_Position.x = 0f - laneData.m_Position.x;
                            float num = laneData.m_Position.x / math.max(1f, prefabGeometryData.m_DefaultWidth) + 0.5f;
                            float3 end = node.m_Position + math.rotate(node.m_Rotation, new float3(prefabGeometryData.m_DefaultWidth * -0.5f, 0f, 0f));
                            float3 position = math.lerp(node.m_Position + math.rotate(node.m_Rotation, new float3(prefabGeometryData.m_DefaultWidth * 0.5f, 0f, 0f)), end, num);
                            ConnectPosition value = default(ConnectPosition);
                            value.m_LaneData = laneData;
                            value.m_Owner = owner;
                            value.m_Position = position;
                            value.m_Position.y += laneData.m_Position.y;
                            value.m_Tangent = math.forward(node.m_Rotation);
                            value.m_Tangent = -MathUtils.Normalize(value.m_Tangent, value.m_Tangent.xz);
                            value.m_Tangent.y = math.clamp(value.m_Tangent.y, -1f, 1f);
                            value.m_GroupIndex = (ushort)(laneData.m_Group | (groupIndex << 8));
                            value.m_CurvePosition = curvePosition;
                            value.m_BaseHeight = position.y;
                            value.m_Elevation = math.lerp(elevation.x, elevation.y, 0.5f);
                            value.m_Order = num;
                            if ((laneData.m_Flags & LaneFlags.Road) != 0)
                            {
                                CarLaneData carLaneData = m_CarLaneData[laneData.m_Lane];
                                value.m_RoadTypes = carLaneData.m_RoadTypes;
                            }
                            if ((laneData.m_Flags & LaneFlags.Track) != 0)
                            {
                                TrackLaneData trackLaneData = m_TrackLaneData[laneData.m_Lane];
                                value.m_TrackTypes = trackLaneData.m_TrackTypes;
                            }
                            if ((laneData.m_Flags & LaneFlags.Utility) != 0)
                            {
                                UtilityLaneData utilityLaneData = m_UtilityLaneData[laneData.m_Lane];
                                value.m_UtilityTypes = utilityLaneData.m_UtilityTypes;
                            }
                            if ((laneData.m_Flags & (LaneFlags.Pedestrian | LaneFlags.Utility)) != 0)
                            {
                                targetBuffer.Add(in value);
                            }
                            else if ((laneData.m_Flags & LaneFlags.Twoway) != 0)
                            {
                                targetBuffer.Add(in value);
                                sourceBuffer.Add(in value);
                            }
                            else if (!flag2)
                            {
                                targetBuffer.Add(in value);
                            }
                            else
                            {
                                sourceBuffer.Add(in value);
                            }
                        }
                    }
                    groupIndex++;
                }
                if (anchorPrefabs.IsCreated)
                {
                    anchorPrefabs.Dispose();
                }
            }

            private unsafe void GetNodeConnectPositions(Entity node, Entity edge, bool isEnd, int groupIndex, float curvePosition, float2 elevation, NetGeometryData prefabGeometryData,
                NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer, bool includeAnchored, ref float middleRadius, ref float roundaboutSize, ref CompositionFlags intersectionFlags,
                ref NativeParallelHashSet<Entity> anchorPrefabs
            ) {
                Composition composition = m_CompositionData[edge];
                PrefabRef prefabRef = m_PrefabRefData[edge];
                CompositionData compositionData = GetCompositionData(composition.m_Edge);
                NetCompositionData netCompositionData = m_PrefabCompositionData[composition.m_Edge];
                NetCompositionData netCompositionData2 = m_PrefabCompositionData[isEnd ? composition.m_EndNode : composition.m_StartNode];
                NetGeometryData netGeometryData = m_PrefabGeometryData[prefabRef.m_Prefab];
                DynamicBuffer<NetCompositionLane> netCompositionLanes = m_PrefabCompositionLanes[composition.m_Edge];
                EdgeGeometry edgeGeometry = m_EdgeGeometryData[edge];
                EdgeNodeGeometry geometry;
                if (isEnd)
                {
                    edgeGeometry.m_Start.m_Left = MathUtils.Invert(edgeGeometry.m_End.m_Right);
                    edgeGeometry.m_Start.m_Right = MathUtils.Invert(edgeGeometry.m_End.m_Left);
                    geometry = m_EndNodeGeometryData[edge].m_Geometry;
                }
                else
                {
                    geometry = m_StartNodeGeometryData[edge].m_Geometry;
                }
                float2 @float = NetCompositionHelpers.CalculateRoundaboutSize(pieces: m_PrefabCompositionPieces[composition.m_Edge], compositionData: netCompositionData2);
                middleRadius = math.max(middleRadius, geometry.m_MiddleRadius);
                roundaboutSize = math.max(roundaboutSize, math.select(@float.x, @float.y, isEnd));
                intersectionFlags |= netCompositionData2.m_Flags;
                bool isSideConnection = (netGeometryData.m_MergeLayers & prefabGeometryData.m_MergeLayers) == 0 && (prefabGeometryData.m_MergeLayers & Layer.Road) != 0;
                LaneFlags laneFlags = (!includeAnchored) ? LaneFlags.FindAnchor : ((LaneFlags)0);
                bool flag = false;
                if ((netCompositionData.m_State & (CompositionState.HasForwardRoadLanes | CompositionState.HasBackwardRoadLanes | CompositionState.HasForwardTrackLanes | CompositionState.HasBackwardTrackLanes)) == 0)
                {
                    Entity entity = edge;
                    Owner componentData;
                    while (m_OwnerData.TryGetComponent(entity, out componentData))
                    {
                        entity = componentData.m_Owner;
                    }
                    flag = m_TransformData.HasComponent(entity);
                }
                if (!m_UpdatedData.HasComponent(edge) && m_SubLanes.HasBuffer(edge))
                {
                    DynamicBuffer<SubLane> dynamicBuffer2 = m_SubLanes[edge];
                    float rhs = math.select(0f, 1f, isEnd);
                    bool* ptr = stackalloc bool[(int)(uint)netCompositionLanes.Length];
                    for (int i = 0; i < netCompositionLanes.Length; i++)
                    {
                        ptr[i] = false;
                    }
                    for (int j = 0; j < dynamicBuffer2.Length; j++)
                    {
                        Entity subLane = dynamicBuffer2[j].m_SubLane;
                        if (!m_EdgeLaneData.HasComponent(subLane) || m_SecondaryLaneData.HasComponent(subLane))
                        {
                            continue;
                        }
                        bool2 x = m_EdgeLaneData[subLane].m_EdgeDelta == rhs;
                        if (!math.any(x))
                        {
                            continue;
                        }
                        bool y = x.y;
                        Curve curve = m_CurveData[subLane];
                        if (y)
                        {
                            curve.m_Bezier = MathUtils.Invert(curve.m_Bezier);
                        }
                        int num = -1;
                        float num2 = float.MaxValue;
                        PrefabRef prefabRef2 = m_PrefabRefData[subLane];
                        NetLaneData netLaneData = m_NetLaneData[prefabRef2.m_Prefab];
                        LaneFlags laneFlags2 = y ? LaneFlags.DisconnectedEnd : LaneFlags.DisconnectedStart;
                        LaneFlags laneFlags3 = netLaneData.m_Flags & (LaneFlags.Road | LaneFlags.Pedestrian | LaneFlags.Parking | LaneFlags.Track | LaneFlags.Utility | LaneFlags.Underground | LaneFlags.OnWater);
                        LaneFlags laneFlags4 = LaneFlags.Invert | LaneFlags.Slave | LaneFlags.Master | LaneFlags.Road | LaneFlags.Pedestrian | LaneFlags.Parking | LaneFlags.Track | LaneFlags.Utility |
                            LaneFlags.Underground | LaneFlags.OnWater | laneFlags2;
                        if (y != isEnd)
                        {
                            laneFlags3 |= LaneFlags.Invert;
                        }
                        if (m_SlaveLaneData.HasComponent(subLane))
                        {
                            laneFlags3 |= LaneFlags.Slave;
                        }
                        if (m_MasterLaneData.HasComponent(subLane))
                        {
                            laneFlags3 |= LaneFlags.Master;
                            laneFlags3 &= ~LaneFlags.Track;
                            laneFlags4 &= ~LaneFlags.Track;
                        }
                        else if ((netLaneData.m_Flags & laneFlags2) != 0)
                        {
                            continue;
                        }
                        TrackLaneData trackLaneData = default(TrackLaneData);
                        UtilityLaneData utilityLaneData = default(UtilityLaneData);
                        if ((netLaneData.m_Flags & LaneFlags.Track) != 0)
                        {
                            trackLaneData = m_TrackLaneData[prefabRef2.m_Prefab];
                        }
                        if ((netLaneData.m_Flags & LaneFlags.Utility) != 0)
                        {
                            utilityLaneData = m_UtilityLaneData[prefabRef2.m_Prefab];
                        }
                        for (int k = 0; k < netCompositionLanes.Length; k++)
                        {
                            NetCompositionLane netCompositionLane = netCompositionLanes[k];
                            if ((netCompositionLane.m_Flags & laneFlags4) != laneFlags3 || ((netCompositionLane.m_Flags & laneFlags) != 0 && IsAnchored(node, ref anchorPrefabs, netCompositionLane.m_Lane)) ||
                                ((laneFlags3 & LaneFlags.Track) != 0 && m_TrackLaneData[netCompositionLane.m_Lane].m_TrackTypes != trackLaneData.m_TrackTypes) ||
                                ((laneFlags3 & LaneFlags.Utility) != 0 && m_UtilityLaneData[netCompositionLane.m_Lane].m_UtilityTypes != utilityLaneData.m_UtilityTypes))
                            {
                                continue;
                            }
                            netCompositionLane.m_Position.x = math.select(0f - netCompositionLane.m_Position.x, netCompositionLane.m_Position.x, isEnd);
                            float num3 = netCompositionLane.m_Position.x / math.max(1f, netCompositionData.m_Width) + 0.5f;
                            if (MathUtils.Intersect(new Line2(edgeGeometry.m_Start.m_Right.a.xz, edgeGeometry.m_Start.m_Left.a.xz), new Line2(curve.m_Bezier.a.xz, curve.m_Bezier.b.xz), out float2 t))
                            {
                                float num4 = math.abs(num3 - t.x);
                                if (num4 < num2)
                                {
                                    num = k;
                                    num2 = num4;
                                }
                            }
                        }
                        if (num == -1 || ptr[num])
                        {
                            continue;
                        }
                        ptr[num] = true;
                        NetCompositionLane laneData = netCompositionLanes[num];
                        laneData.m_Position.x = math.select(0f - laneData.m_Position.x, laneData.m_Position.x, isEnd);
                        float order = laneData.m_Position.x / math.max(1f, netCompositionData.m_Width) + 0.5f;
                        laneData.m_Index = (byte)(m_LaneData[subLane].m_MiddleNode.GetLaneIndex() & 0xFF);
                        ConnectPosition value = default(ConnectPosition);
                        value.m_LaneData = laneData;
                        value.m_Owner = edge;
                        value.m_NodeComposition = (isEnd ? composition.m_EndNode : composition.m_StartNode);
                        value.m_EdgeComposition = composition.m_Edge;
                        value.m_Position = curve.m_Bezier.a;
                        value.m_Tangent = MathUtils.StartTangent(curve.m_Bezier);
                        value.m_Tangent = -MathUtils.Normalize(value.m_Tangent, value.m_Tangent.xz);
                        value.m_Tangent.y = math.clamp(value.m_Tangent.y, -1f, 1f);
                        value.m_SegmentIndex = (byte)math.select(0, 4, isEnd);
                        value.m_GroupIndex = (ushort)(laneData.m_Group | (groupIndex << 8));
                        value.m_CompositionData = compositionData;
                        value.m_CurvePosition = curvePosition;
                        value.m_BaseHeight = curve.m_Bezier.a.y;
                        value.m_BaseHeight -= laneData.m_Position.y;
                        value.m_Elevation = math.lerp(elevation.x, elevation.y, 0.5f);
                        value.m_IsEnd = isEnd;
                        value.m_Order = order;
                        value.m_IsSideConnection = isSideConnection;
                        PedestrianLaneData componentData2;
                        if ((laneData.m_Flags & LaneFlags.Road) != 0)
                        {
                            CarLaneData carLaneData = m_CarLaneData[laneData.m_Lane];
                            value.m_RoadTypes = carLaneData.m_RoadTypes;
                        }
                        else if ((laneData.m_Flags & LaneFlags.Pedestrian) != 0 && flag && m_PedestrianLaneData.TryGetComponent(laneData.m_Lane, out componentData2) && componentData2.m_NotWalkLanePrefab != Entity.Null)
                        {
                            value.m_RoadTypes = RoadTypes.Bicycle;
                        }
                        if ((laneData.m_Flags & LaneFlags.Track) != 0)
                        {
                            TrackLaneData trackLaneData2 = m_TrackLaneData[laneData.m_Lane];
                            value.m_TrackTypes = trackLaneData2.m_TrackTypes;
                        }
                        if ((laneData.m_Flags & LaneFlags.Utility) != 0)
                        {
                            UtilityLaneData utilityLaneData2 = m_UtilityLaneData[laneData.m_Lane];
                            value.m_UtilityTypes = utilityLaneData2.m_UtilityTypes;
                        }
                        if ((laneData.m_Flags & (LaneFlags.Pedestrian | LaneFlags.Utility)) != 0)
                        {
                            targetBuffer.Add(in value);
                            if (value.m_RoadTypes != 0)
                            {
                                sourceBuffer.Add(in value);
                            }
                        }
                        else if ((laneData.m_Flags & LaneFlags.Twoway) != 0)
                        {
                            targetBuffer.Add(in value);
                            sourceBuffer.Add(in value);
                        }
                        else if (!y)
                        {
                            targetBuffer.Add(in value);
                        }
                        else
                        {
                            sourceBuffer.Add(in value);
                        }
                    }
                    return;
                }
                for (int l = 0; l < netCompositionLanes.Length; l++)
                {
                    NetCompositionLane laneData2 = netCompositionLanes[l];
                    bool flag2 = isEnd == ((laneData2.m_Flags & LaneFlags.Invert) == 0);
                    if (((uint)laneData2.m_Flags & (uint)(flag2 ? 512/*DisconnectedEnd*/ : 256/*DisconnectedStart*/)) != 0 ||
                        ((laneData2.m_Flags & laneFlags) != 0 && IsAnchored(node, ref anchorPrefabs, laneData2.m_Lane)))
                    {
                        continue;
                    }
                    laneData2.m_Position.x = math.select(0f - laneData2.m_Position.x, laneData2.m_Position.x, isEnd);
                    float num5 = laneData2.m_Position.x / math.max(1f, netCompositionData.m_Width) + 0.5f;
                    Bezier4x3 curve2 = MathUtils.Lerp(edgeGeometry.m_Start.m_Right, edgeGeometry.m_Start.m_Left, num5);
                    ConnectPosition value2 = default(ConnectPosition);
                    value2.m_LaneData = laneData2;
                    value2.m_Owner = edge;
                    value2.m_NodeComposition = (isEnd ? composition.m_EndNode : composition.m_StartNode);
                    value2.m_EdgeComposition = composition.m_Edge;
                    value2.m_Position = curve2.a;
                    value2.m_Position.y += laneData2.m_Position.y;
                    value2.m_Tangent = MathUtils.StartTangent(curve2);
                    value2.m_Tangent = -MathUtils.Normalize(value2.m_Tangent, value2.m_Tangent.xz);
                    value2.m_Tangent.y = math.clamp(value2.m_Tangent.y, -1f, 1f);
                    value2.m_SegmentIndex = (byte)math.select(0, 4, isEnd);
                    value2.m_GroupIndex = (ushort)(laneData2.m_Group | (groupIndex << 8));
                    value2.m_CompositionData = compositionData;
                    value2.m_CurvePosition = curvePosition;
                    value2.m_BaseHeight = curve2.a.y;
                    value2.m_Elevation = math.lerp(elevation.x, elevation.y, 0.5f);
                    value2.m_IsEnd = isEnd;
                    value2.m_Order = num5;
                    value2.m_IsSideConnection = isSideConnection;
                    PedestrianLaneData componentData3;
                    if ((laneData2.m_Flags & LaneFlags.Road) != 0)
                    {
                        CarLaneData carLaneData2 = m_CarLaneData[laneData2.m_Lane];
                        value2.m_RoadTypes = carLaneData2.m_RoadTypes;
                    }
                    else if ((laneData2.m_Flags & LaneFlags.Pedestrian) != 0 && flag && m_PedestrianLaneData.TryGetComponent(laneData2.m_Lane, out componentData3) && componentData3.m_NotWalkLanePrefab != Entity.Null)
                    {
                        value2.m_RoadTypes = RoadTypes.Bicycle;
                    }
                    if ((laneData2.m_Flags & LaneFlags.Track) != 0)
                    {
                        TrackLaneData trackLaneData3 = m_TrackLaneData[laneData2.m_Lane];
                        value2.m_TrackTypes = trackLaneData3.m_TrackTypes;
                    }
                    if ((laneData2.m_Flags & LaneFlags.Utility) != 0)
                    {
                        UtilityLaneData utilityLaneData3 = m_UtilityLaneData[laneData2.m_Lane];
                        value2.m_UtilityTypes = utilityLaneData3.m_UtilityTypes;
                    }
                    if ((laneData2.m_Flags & (LaneFlags.Pedestrian | LaneFlags.Utility)) != 0)
                    {
                        targetBuffer.Add(in value2);
                        if (value2.m_RoadTypes != 0)
                        {
                            sourceBuffer.Add(in value2);
                        }
                    }
                    else if ((laneData2.m_Flags & LaneFlags.Twoway) != 0)
                    {
                        targetBuffer.Add(in value2);
                        sourceBuffer.Add(in value2);
                    }
                    else if (!flag2)
                    {
                        targetBuffer.Add(in value2);
                    }
                    else
                    {
                        sourceBuffer.Add(in value2);
                    }
                }
            }

            private void FillModifiedLaneConnections(DynamicBuffer<ModifiedLaneConnections> connections, NativeHashSet<LaneEndKey> output, bool isTemp)
            {
                DataTemp temp;
                for (var i = 0; i < connections.Length; i++)
                {
                    ModifiedLaneConnections connection = connections[i];
                    if (!isTemp || dataTemps.TryGetComponent(connection.modifiedConnections, out temp) && (temp.flags & TempFlags.Delete) == 0)
                    {
                        output.Add(new LaneEndKey(connection.edgeEntity, connection.laneIndex));
                    }
                }
            }

            // TODO Unused, needs more work or rewrite because edge-edge alignment affects result
            // private void FindIsEdgeTurn(NativeHashMap<EdgeToEdgeKey, bool> map, Entity sourceEdge, bool isEndSource, Entity targetEdge, bool isEndTarget, out bool isEdgeTurn)
            // {
            //     isEdgeTurn = false;
            //     Curve sourceCurve = m_CurveData[sourceEdge];
            //     Curve targetCurve = m_CurveData[targetEdge];
            //     isEdgeTurn = NetUtils.IsTurn(
            //         math.select(sourceCurve.m_Bezier.a.xz, sourceCurve.m_Bezier.d.xz, isEndSource),
            //         math.normalizesafe((isEndSource ? MathUtils.EndTangent(sourceCurve.m_Bezier): MathUtils.StartTangent(sourceCurve.m_Bezier)).xz), 
            //         math.select(targetCurve.m_Bezier.a.xz, targetCurve.m_Bezier.d.xz, isEndTarget),
            //         math.normalizesafe(-(isEndTarget ? MathUtils.EndTangent(targetCurve.m_Bezier): MathUtils.StartTangent(targetCurve.m_Bezier)).xz),
            //         out _, out _, out _);
            //     Logger.DebugLaneSystem($"FindIsEdgeTurn: {sourceEdge} -> {targetEdge} = isEdgeTurn: {isEdgeTurn}");
            //     map.Add(new EdgeToEdgeKey(sourceEdge, targetEdge), isEdgeTurn);
            // }
            
            /*private void FilterAllowedCarConnectPositions(ConnectPosition source, NativeParallelHashSet<ConnectionKey> forbidden, NativeList<ConnectPosition> input, NativeList<ConnectPosition> output) {
                
                for (int i = 0; i < input.Length; i++)
                {
                    ConnectPosition value = input[i];
                    if (!forbidden.Contains(new ConnectionKey(source, value)))
                    {
                        output.Add(in value);
                    } 
                }
            }*/

            private bool FindPriority(ConnectPosition source, out int result)
            {
                if (lanePriorities.HasBuffer(source.m_Owner))
                {
                    DynamicBuffer<LanePriority> priorities = lanePriorities[source.m_Owner];
                    for (var i = 0; i < priorities.Length; i++)
                    {
                        LanePriority priority = priorities[i];
                        if (priority.laneIndex.x == source.m_LaneData.m_Index)
                        {
                            if (priority.priority == PriorityType.Default)
                            {
                                break;
                            }
                            
                            result = priority.priority == PriorityType.Stop ? 2 :
                                priority.priority == PriorityType.Yield ? 1 :
                                priority.priority == PriorityType.RightOfWay ? -1 : 0;
                            return true;
                        } 
                    }
                }
                
                result = 0;
                return false;
            }

            /// <summary>
            /// Collects non-Slave road-only connect positions
            /// </summary>
            private void FilterMainCarConnectPositions(NativeList<ConnectPosition> input, NativeList<ConnectPosition> output)
            {
                bool flag = false;
                for (int i = 0; i < input.Length; i++)
                {
                    ConnectPosition value = input[i];
                    if ((value.m_LaneData.m_Flags & (LaneFlags.Slave | LaneFlags.Road)) == LaneFlags.Road)
                    {
                        output.Add(in value);
                        flag = true;
                    }
                    else if ((value.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0 && value.m_RoadTypes != 0)
                    {
                        output.Add(in value);
                    }
                }
                if (!flag)
                {
                    output.Clear();
                }
            }

            /// <summary>
            /// Collects non-Master road-only connect positions
            /// </summary>
            private void FilterActualCarConnectPositions(RoadTypes roadTypes, NativeList<ConnectPosition> input, NativeList<ConnectPosition> output) {
                for (int i = 0; i < input.Length; i++)
                {
                    ConnectPosition value = input[i];
                    if (((value.m_LaneData.m_Flags & (LaneFlags.Master | LaneFlags.Road)) == LaneFlags.Road || (value.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0) && (value.m_RoadTypes & roadTypes) != 0)
                    {
                        output.Add(in value);
                    }
                }
            }
        
            /// <summary>
            /// Collects non-Master road-only connect positions with the same Owner and lane group
            /// </summary>
            private void FilterActualCarConnectPositions(ConnectPosition main, NativeList<ConnectPosition> input, NativeList<ConnectPosition> output) {
                for (int i = 0; i < input.Length; i++)
                {
                    ConnectPosition value = input[i];
                    if (((value.m_LaneData.m_Flags & (LaneFlags.Master | LaneFlags.Road)) == LaneFlags.Road || (value.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0) && 
                        value.m_Owner == main.m_Owner &&
                        value.m_LaneData.m_Group == main.m_LaneData.m_Group)
                    {
                        output.Add(in value);
                    }
                }
            }

            private TrackTypes FilterTrackConnectPositions(NativeList<ConnectPosition> input, NativeList<ConnectPosition> output) {
                TrackTypes trackTypes = TrackTypes.None;
                for (int i = 0; i < input.Length; i++)
                {
                    ConnectPosition value = input[i];
                    if ((value.m_LaneData.m_Flags & (LaneFlags.Master | LaneFlags.Track)) == LaneFlags.Track)
                    {
                        output.Add(in value);
                        trackTypes |= value.m_TrackTypes;
                    }
                }
                return trackTypes;
            }

            private void FilterTrackConnectPositions(ref int index, TrackTypes trackType, NativeList<ConnectPosition> input, NativeList<ConnectPosition> output) {
                ConnectPosition value = default(ConnectPosition);
                while (index < input.Length)
                {
                    value = input[index++];
                    if ((value.m_TrackTypes & trackType) != 0)
                    {
                        output.Add(in value);
                        break;
                    }
                }
                while (index < input.Length)
                {
                    ConnectPosition value2 = input[index];
                    if (!(value2.m_Owner != value.m_Owner))
                    {
                        if ((value2.m_TrackTypes & trackType) != 0)
                        {
                            output.Add(in value2);
                        }
                        index++;
                        continue;
                    }
                    break;
                }
            }

            private void FilterTrackConnectPositions(TrackTypes trackType, NativeList<ConnectPosition> input, NativeList<ConnectPosition> output) {
                for (int i = 0; i < input.Length; i++)
                {
                    ConnectPosition value = input[i];
                    if ((value.m_TrackTypes & trackType) != 0)
                    {
                        output.Add(in value);
                    }
                }
            }

            private void FilterPedestrianConnectPositions(NativeList<ConnectPosition> input, NativeList<ConnectPosition> output, NativeList<MiddleConnection> middleConnections, bool onWater)
            {
                LaneFlags laneFlags = onWater ? (LaneFlags.Pedestrian | LaneFlags.OnWater) : LaneFlags.Pedestrian;
                for (int i = 0; i < input.Length; i++)
                {
                    ConnectPosition value = input[i];
                    if ((value.m_LaneData.m_Flags & (LaneFlags.Pedestrian | LaneFlags.OnWater)) == laneFlags)
                    {
                        output.Add(in value);
                    }
                }
                int num = int.MinValue;
                for (int j = 0; j < middleConnections.Length; j++)
                {
                    MiddleConnection middleConnection = middleConnections[j];
                    if (middleConnection.m_SortIndex != num && !middleConnection.m_IsSource)
                    {
                        num = middleConnection.m_SortIndex;
                        if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & (LaneFlags.Pedestrian | LaneFlags.OnWater)) == laneFlags)
                        {
                            output.Add(in middleConnection.m_ConnectPosition);
                        }
                    }
                }
            }

            private UtilityTypes FilterUtilityConnectPositions(NativeList<ConnectPosition> input, NativeList<ConnectPosition> output) {
                UtilityTypes utilityTypes = UtilityTypes.None;
                for (int i = 0; i < input.Length; i++)
                {
                    ConnectPosition value = input[i];
                    if ((value.m_LaneData.m_Flags & LaneFlags.Utility) != 0)
                    {
                        output.Add(in value);
                        utilityTypes |= value.m_UtilityTypes;
                    }
                }
                return utilityTypes;
            }

            private void FilterUtilityConnectPositions(UtilityTypes utilityTypes, NativeList<ConnectPosition> input, NativeList<ConnectPosition> output) {
                for (int i = 0; i < input.Length; i++)
                {
                    ConnectPosition value = input[i];
                    if ((value.m_UtilityTypes & utilityTypes) != 0)
                    {
                        output.Add(in value);
                    }
                }
            }

            private int CalculateYieldOffset(ConnectPosition source, NativeList<ConnectPosition> sources, NativeList<ConnectPosition> targets) {
                NetCompositionData netCompositionData = m_PrefabCompositionData[source.m_NodeComposition];
                if ((netCompositionData.m_Flags.m_General & CompositionFlags.General.AllWayStop) != 0)
                {
                    return 2; //stop (all stop)
                }
                if ((netCompositionData.m_Flags.m_General & (CompositionFlags.General.LevelCrossing | CompositionFlags.General.TrafficLights)) != 0)
                {
                    return 0; // no priority set (has traffic lights)
                }
            
                //NON-STOCK-CODE
                if (FindPriority(source, out int priority))
                {
                    return priority;
                }
                //NON-STOCK-CODE-END
            
                Entity entity = Entity.Null;
                for (int i = 0; i < sources.Length; i++)
                {
                    ConnectPosition sourceConnectPos = sources[i];
                    if (sourceConnectPos.m_Owner != source.m_Owner &&
                        sourceConnectPos.m_Owner != entity &&
                        sourceConnectPos.m_CompositionData.m_Priority - source.m_CompositionData.m_Priority > 0.99f)
                    {
                        if (entity != Entity.Null)
                        {
                            return 1; //yield
                        }
                        entity = sourceConnectPos.m_Owner;
                    }
                }
                if (entity == Entity.Null)
                {
                    if ((source.m_CompositionData.m_RoadFlags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0)
                    {
                        int num = 0;
                        for (int j = 0; j < sources.Length; j++)
                        {
                            ConnectPosition sourcePosition = sources[j];
                            bool noTurns = false;
                            for (int k = 0; k < targets.Length; k++)
                            {
                                ConnectPosition targetPosition = targets[k];
                                if (targetPosition.m_Owner != sourcePosition.m_Owner)
                                {
                                    bool gentle;
                                    bool isTurn = IsTurn(sourcePosition, targetPosition, out bool _, out gentle, out bool _);
                                    noTurns = (noTurns || !isTurn || gentle);
                                }
                            }
                            if (noTurns)
                            {
                                if (sourcePosition.m_Owner == source.m_Owner)
                                {
                                    return 0; // no priority set
                                }
                                num++;
                            }
                        }
                        return math.select(0, 1, num >= 1); // no priority set (false) || yield (true)
                    }
                    return 0; // no priority set
                }
                for (int l = 0; l < targets.Length; l++)
                {
                    ConnectPosition targetConnectPos = targets[l];
                    if (targetConnectPos.m_Owner != source.m_Owner &&
                        targetConnectPos.m_Owner != entity &&
                        targetConnectPos.m_CompositionData.m_Priority - source.m_CompositionData.m_Priority > 0.99f)
                    {
                        return 1; //yield
                    }
                }
                return 0; // no priority set
            }

            private void ProcessCarConnectPositions(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, Entity prefab, LaneBuffer laneBuffer, NativeList<MiddleConnection> middleConnections,
                NativeParallelHashSet<ConnectionKey> createdConnections, NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer, NativeList<ConnectPosition> allSources, RoadTypes roadPassThrough,
                CompositionFlags intersectionFlags, bool isTemp, Temp ownerTemp, int yield, 
                /*NON-STOCK*/NativeHashSet<LaneEndKey> modifiedLaneEndConnections, NativeHashSet<ConnectionKey> createdGroups, NativeHashMap<int, int> priorities
            )
            {
                if (sourceBuffer.Length >= 1 && targetBuffer.Length >= 1)
                {
                    sourceBuffer.Sort(default(SourcePositionComparer));
                    ConnectPosition sourcePosition = sourceBuffer[0];
                    SortTargets(sourcePosition, targetBuffer);
#if DEBUG_LANE_SYS
                    StringBuilder sb = new StringBuilder();
                    for (var i = 0; i < targetBuffer.Length; i++)
                    {
                        sb.Append("\ti[").Append(targetBuffer[i].m_LaneData.m_Index).Append("] g[").Append(targetBuffer[i].m_LaneData.m_Group).Append("] gi[").Append(targetBuffer[i].m_GroupIndex).Append("] ").Append(targetBuffer[i].m_Owner).Append(" f[").Append(targetBuffer[i].m_LaneData.m_Flags.ToString()).AppendLine("]");
                    }
                    Logger.DebugLaneSystem($"Sorted targets\n  S({sourcePosition.m_Owner} | {sourcePosition.m_LaneData.m_Index}[{sourcePosition.m_GroupIndex}] ({sourcePosition.m_LaneData.m_Group}) -> [{sourcePosition.m_LaneData.m_Flags}]), nLaneIdx: {nodeLaneIndex}, owner: {owner}:\n{sb}");
#endif
                    CreateNodeCarLanes(jobIndex, ref nodeLaneIndex, ref random, owner, prefab, laneBuffer, middleConnections, createdConnections, sourceBuffer, targetBuffer, allSources, roadPassThrough, intersectionFlags, isTemp, ownerTemp, yield, modifiedLaneEndConnections, createdGroups, priorities);
                }
            }

            private void SortTargets(ConnectPosition sourcePosition, NativeList<ConnectPosition> targetBuffer) {
                float2 x = new float2(sourcePosition.m_Tangent.z, 0f - sourcePosition.m_Tangent.x);
                for (int i = 0; i < targetBuffer.Length; i++)
                {
                    ConnectPosition value = targetBuffer[i];
                    float2 value2 = value.m_Position.xz - sourcePosition.m_Position.xz;
                    value2 -= value.m_Tangent.xz;
                    MathUtils.TryNormalize(ref value2);
                    float order;
                    if (math.dot(sourcePosition.m_Tangent.xz, value2) > 0f)
                    {
                        order = math.dot(x, value2) * 0.5f;
                    }
                    else
                    {
                        float num = math.dot(x, value2);
                        order = math.select(-1f, 1f, num >= 0f) - num * 0.5f;
                    }
                    value.m_Order = order;
                    targetBuffer[i] = value;
                }
                targetBuffer.Sort(default(TargetPositionComparer));
            }

            private int2 CalculateSourcesBetween(ConnectPosition source, ConnectPosition target, NativeList<ConnectPosition> allSources) {
                float2 x = MathUtils.Right(target.m_Position.xz - source.m_Position.xz);
                int2 result = 0;
                for (int i = 0; i < allSources.Length; i++)
                {
                    ConnectPosition sourcePosition = allSources[i];
                    if (sourcePosition.m_GroupIndex != source.m_GroupIndex && (sourcePosition.m_LaneData.m_Flags & (LaneFlags.Master | LaneFlags.Road)) == LaneFlags.Road &&
                        ((sourcePosition.m_RoadTypes & source.m_RoadTypes & ~RoadTypes.Bicycle) != 0 || (sourcePosition.m_RoadTypes == RoadTypes.Bicycle && source.m_RoadTypes == RoadTypes.Bicycle)) &&
                        !(IsTurn(sourcePosition, target, out bool _, out bool _, out bool uturn) && uturn))
                    {
                        result += math.select(new int2(0, 1), new int2(1, 0), math.dot(x, target.m_Position.xz - sourcePosition.m_Position.xz) > 0f);
                    }
                }
                return result;
            }

            private void CreateNodeCarLanes(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, Entity prefab, LaneBuffer laneBuffer, NativeList<MiddleConnection> middleConnections, NativeParallelHashSet<ConnectionKey> createdConnections,
                NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer, NativeList<ConnectPosition> allSources, RoadTypes roadPassThrough, CompositionFlags intersectionFlags, bool isTemp, Temp ownerTemp, int yield, NativeHashSet<LaneEndKey> modifiedLaneEndConnections, NativeHashSet<ConnectionKey> createdGroups, NativeHashMap<int, int> priorities
            ) {
                ConnectPosition sourcePositionFirst = sourceBuffer[0];
                ConnectPosition sourcePositionLast = sourceBuffer[^1];
                RoadTypes allRoadTypes = RoadTypes.None;
                for (int i = 0; i < sourceBuffer.Length; i++)
                {
                    allRoadTypes |= sourceBuffer[i].m_RoadTypes;
                }
                for (int j = 0; j < targetBuffer.Length; j++)
                {
                    allRoadTypes |= targetBuffer[j].m_RoadTypes;
                }
                RoadTypes roadTypesFirst = (((uint)allRoadTypes & (uint)(byte)(~(int)sourcePositionFirst.m_RoadTypes)) != 0) ? sourcePositionFirst.m_RoadTypes : RoadTypes.None;
                RoadTypes roadTypesLast = (((uint)allRoadTypes & (uint)(byte)(~(int)sourcePositionLast.m_RoadTypes)) != 0) ? sourcePositionLast.m_RoadTypes : RoadTypes.None;
                if (roadTypesFirst == RoadTypes.Car)
                {
                    roadTypesFirst = RoadTypes.None;
                }
                if (roadTypesLast == RoadTypes.Car)
                {
                    roadTypesLast = RoadTypes.None;
                }
                int k;
                for (k = 0; k < sourceBuffer.Length && sourceBuffer[k].m_RoadTypes == roadTypesFirst; k++) { }
                int l;
                for (l = 0; l < sourceBuffer.Length && sourceBuffer[sourceBuffer.Length - 1 - l].m_RoadTypes == roadTypesLast; l++) { }
                int num = sourceBuffer.Length - k - l;
                int num2 = k;
                if (k + l > sourceBuffer.Length)
                {
                    if (m_LeftHandTraffic)
                    {
                        l = 0;
                    }
                    else
                    {
                        k = 0;
                    }
                    num = sourceBuffer.Length;
                    num2 = 0;
                }
                StackList<int> stackList = stackalloc int[targetBuffer.Length];
                StackList<int> stackList2 = stackalloc int[targetBuffer.Length];
                int num3 = int.MaxValue;
                int num4 = -1;
                int num5 = 0;
                while (num5 < targetBuffer.Length)
                {
                    ConnectPosition connectPosition = targetBuffer[num5];
                    int m;
                    ConnectPosition connectPosition2;
                    for (m = num5 + 1; m < targetBuffer.Length; m++)
                    {
                        connectPosition2 = targetBuffer[m];
                        if (connectPosition2.m_GroupIndex != connectPosition.m_GroupIndex)
                        {
                            break;
                        }
                    }
                    connectPosition2 = targetBuffer[m - 1];
                    int n = 0;
                    int num6 = 0;
                    RoadTypes roadTypes4 = (((uint)allRoadTypes & (uint)(byte)(~(int)connectPosition.m_RoadTypes)) != 0) ? connectPosition.m_RoadTypes : RoadTypes.None;
                    RoadTypes roadTypes5 = (((uint)allRoadTypes & (uint)(byte)(~(int)connectPosition2.m_RoadTypes)) != 0) ? connectPosition2.m_RoadTypes : RoadTypes.None;
                    if (roadTypes4 == RoadTypes.Car)
                    {
                        roadTypes4 = RoadTypes.None;
                    }
                    if (roadTypes5 == RoadTypes.Car)
                    {
                        roadTypes5 = RoadTypes.None;
                    }
                    for (; num5 + n < m && targetBuffer[num5 + n].m_RoadTypes == roadTypes4; n++) { }
                    for (; m - num6 > num5 && targetBuffer[m - 1 - num6].m_RoadTypes == roadTypes5; num6++) { }
                    if (n + num6 > m - num5)
                    {
                        if (m_LeftHandTraffic)
                        {
                            num6 = 0;
                        }
                        else
                        {
                            n = 0;
                        }
                        for (int num7 = num5; num7 < m; num7++)
                        {
                            if (num7 - num5 < n || num7 >= m - num6)
                            {
                                stackList2.AddNoResize(num7);
                            }
                            stackList.AddNoResize(num7);
                        }
                    }
                    else
                    {
                        for (int num8 = num5; num8 < m; num8++)
                        {
                            if (num8 - num5 < n || num8 >= m - num6)
                            {
                                stackList2.AddNoResize(num8);
                                continue;
                            }
                            num3 = math.min(num3, stackList.Length);
                            num4 = stackList.Length;
                            stackList.AddNoResize(num8);
                        }
                    }
                    num5 = m;
                }
                
                NetGeometryData netGeometryData = m_PrefabGeometryData[prefab];
                NetGeometryData netGeometryData2 = m_PrefabGeometryData[m_PrefabRefData[sourcePositionFirst.m_Owner]];
                NetCompositionData netCompositionData = m_PrefabCompositionData[sourcePositionFirst.m_NodeComposition];
                CompositionFlags.Side side = ((netCompositionData.m_Flags.m_General & CompositionFlags.General.Invert) != 0 != ((sourcePositionFirst.m_LaneData.m_Flags & LaneFlags.Invert) != 0))
                    ? netCompositionData.m_Flags.m_Left
                    : netCompositionData.m_Flags.m_Right;
                bool isForbidLeftTurn = (side & CompositionFlags.Side.ForbidLeftTurn) != 0;
                bool isForbidRightTurn = (side & CompositionFlags.Side.ForbidRightTurn) != 0;
                bool isForbidStraight = (side & CompositionFlags.Side.ForbidStraight) != 0;
                int num10 = 0;
                int num11 = 0;
                int num12 = 0;
                while (num10 < stackList.Length)
                {
                    ConnectPosition targetPosition = targetBuffer[stackList[num10]];
                    int num13;
                    for (num13 = num10 + 1; num13 < stackList.Length; num13++)
                    {
                        ConnectPosition connectPosition3 = targetBuffer[stackList[num13]];
                        if (connectPosition3.m_GroupIndex != targetPosition.m_GroupIndex)
                        {
                            break;
                        }
                        targetPosition = connectPosition3;
                    }
                    if (!IsTurn(sourcePositionFirst, targetPosition, out bool right, out bool _, out bool uturn) || right || !uturn)
                    {
                        break;
                    }
                    num10 = num13;
                    if (targetPosition.m_Owner == sourcePositionFirst.m_Owner && targetPosition.m_LaneData.m_Carriageway == sourcePositionFirst.m_LaneData.m_Carriageway)
                    {
                        num11 = num13;
                    }
                    if (isForbidLeftTurn)
                    {
                        num12 = num13;
                    }
                }

                int num14 = 0;
                int num15 = 0;
                int num16 = 0;
                while (num14 < stackList.Length - num10)
                {
                    ConnectPosition targetPosition2 = targetBuffer[stackList[stackList.Length - num14 - 1]];
                    int num17;
                    for (num17 = num14 + 1; num17 < stackList.Length - num10; num17++)
                    {
                        ConnectPosition connectPosition4 = targetBuffer[stackList[stackList.Length - num17 - 1]];
                        if (connectPosition4.m_GroupIndex != targetPosition2.m_GroupIndex)
                        {
                            break;
                        }
                        targetPosition2 = connectPosition4;
                    }
                    if (!IsTurn(sourcePositionLast, targetPosition2, out bool right2, out bool _, out bool uturn2) || !right2 || !uturn2)
                    {
                        break;
                    }
                    num14 = num17;
                    if ((targetPosition2.m_Owner == sourcePositionLast.m_Owner && targetPosition2.m_LaneData.m_Carriageway == sourcePositionLast.m_LaneData.m_Carriageway) || isForbidRightTurn)
                    {
                        num15 = num17;
                    }
                    if (isForbidRightTurn)
                    {
                        num16 = num17;
                    }
                }

                int num18 = 0;
                int num19 = 0;
                int num20 = 0;
                while (num10 + num18 < stackList.Length - num14)
                {
                    ConnectPosition targetPosition3 = targetBuffer[stackList[num10 + num18]];
                    int num21;
                    for (num21 = num18 + 1; num10 + num21 < stackList.Length - num14; num21++)
                    {
                        ConnectPosition connectPosition5 = targetBuffer[stackList[num10 + num21]];
                        if (connectPosition5.m_GroupIndex != targetPosition3.m_GroupIndex)
                        {
                            break;
                        }
                        targetPosition3 = connectPosition5;
                    }
                    if (!IsTurn(sourcePositionFirst, targetPosition3, out bool right3, out bool gentle3, out bool _) || right3)
                    {
                        break;
                    }
                    if (gentle3)
                    {
                        num19 += num21 - num18;
                    }
                    num18 = num21;
                    if (isForbidLeftTurn)
                    {
                        num20 = num21;
                    }
                }

                int num22 = 0;
                int num23 = 0;
                int num24 = 0;
                while (num14 + num22 < stackList.Length - num10 - num18)
                {
                    ConnectPosition targetPosition4 = targetBuffer[stackList[stackList.Length - num14 - num22 - 1]];
                    int num25;
                    for (num25 = num22 + 1; num14 + num25 < stackList.Length - num10 - num18; num25++)
                    {
                        ConnectPosition connectPosition6 = targetBuffer[stackList[stackList.Length - num14 - num25 - 1]];
                        if (connectPosition6.m_GroupIndex != targetPosition4.m_GroupIndex)
                        {
                            break;
                        }
                        targetPosition4 = connectPosition6;
                    }
                    if (!IsTurn(sourcePositionLast, targetPosition4, out bool right4, out bool gentle4, out bool _) || !right4)
                    {
                        break;
                    }
                    if (gentle4)
                    {
                        num23 += num25 - num22;
                    }
                    num22 = num25;
                    if (isForbidRightTurn)
                    {
                        num24 = num25;
                    }
                }

                int num26 = num10 + num14;
                int num27 = num18 + num22;
                int num28 = stackList.Length - num26;
                int num29 = num28 - num27;
                int num30 = math.select(0, num29, isForbidStraight);
                int num31 = num29 - num30;
                int num32 = math.min(num, num28);
                if (num11 + num15 == stackList.Length)
                {
                    num12 = math.max(0, num12 - num11);
                    num16 = math.max(0, num16 - num15);
                    num11 = 0;
                    num15 = 0;
                }
                int num33 = num18 - num20;
                int num34 = num22 - num24;
                int num35 = num33 + num34;
                int num36 = num10 - math.max(num11, num12);
                int num37 = num14 - math.max(num15, num16);
                int num38 = num36 + num37;
                int num39 = num - num32;
                int num40 = math.min(num36, math.max(0, num39 * num36 + num38 - 1) / math.max(1, num38));
                int num41 = math.min(num37, math.max(0, num39 * num37 + num38 - 1) / math.max(1, num38));
                if (num40 + num41 > num39)
                {
                    if (m_LeftHandTraffic)
                    {
                        num41 = num39 - num40;
                    }
                    else
                    {
                        num40 = num39 - num41;
                    }
                }
                int num42 = math.min(num32, num31);
                if (num42 >= 2 && num28 >= 4)
                {
                    int num43 = math.max(num33, num34);
                    int num44 = math.max(0, num43 - 1) * num / (num28 - 1);
                    num42 = math.clamp(num - num44, 1, num42);
                }
                num39 = num32 - num42;
                int num45 = math.min(num33, math.max(0, num39 * num33 + num35 - 1) / math.max(1, num35));
                int num46 = math.min(num34, math.max(0, num39 * num34 + num35 - 1) / math.max(1, num35));
                if (num45 + num46 > num39)
                {
                    if (num34 > num33)
                    {
                        num45 = num39 - num46;
                    }
                    else if (num33 > num34)
                    {
                        num46 = num39 - num45;
                    }
                    else if (m_LeftHandTraffic)
                    {
                        num45 = num39 - num46;
                    }
                    else
                    {
                        num46 = num39 - num45;
                    }
                }
                num39 = num - num42 - num40 - num41 - num45 - num46;
                if (num39 > 0)
                {
                    if (num31 > 0)
                    {
                        num42 += num39;
                    }
                    else if (num35 > 0)
                    {
                        int num47 = (num39 * num33 + num35 - 1) / num35;
                        int num48 = (num39 * num34 + num35 - 1) / num35;
                        if (num47 + num48 > num39)
                        {
                            if (m_LeftHandTraffic)
                            {
                                num47 = num39 - num48;
                            }
                            else
                            {
                                num48 = num39 - num47;
                            }
                        }
                        num45 += num47;
                        num46 += num48;
                    }
                    else if (num38 > 0)
                    {
                        int num49 = (num39 * num36 + num38 - 1) / num38;
                        int num50 = (num39 * num37 + num38 - 1) / num38;
                        if (num49 + num50 > num39)
                        {
                            if (m_LeftHandTraffic)
                            {
                                num49 = num39 - num50;
                            }
                            else
                            {
                                num50 = num39 - num49;
                            }
                        }
                        num40 += num49;
                        num41 += num50;
                    }
                    else if (num29 > 0)
                    {
                        num42 += num39;
                    }
                    else if (num27 > 0)
                    {
                        int num51 = (num39 * num18 + num27 - 1) / num27;
                        int num52 = (num39 * num22 + num27 - 1) / num27;
                        if (num51 + num52 > num39)
                        {
                            if (m_LeftHandTraffic)
                            {
                                num51 = num39 - num52;
                            }
                            else
                            {
                                num52 = num39 - num51;
                            }
                        }
                        num45 += num51;
                        num46 += num52;
                    }
                    else if (num26 > 0)
                    {
                        int num53 = (num39 * num10 + num26 - 1) / num26;
                        int num54 = (num39 * num14 + num26 - 1) / num26;
                        if (num53 + num54 > num39)
                        {
                            if (m_LeftHandTraffic)
                            {
                                num53 = num39 - num54;
                            }
                            else
                            {
                                num54 = num39 - num53;
                            }
                        }
                        num40 += num53;
                        num41 += num54;
                    }
                    else
                    {
                        num42 += num39;
                    }
                }
                int num55 = math.max(num40, math.select(0, 1, num10 != 0));
                int num56 = math.max(num41, math.select(0, 1, num14 != 0));
                int num57 = num45 + math.select(0, 1, num18 > num45 && num > num45);
                int num58 = num46 + math.select(0, 1, num22 > num46 && num > num46);
                if (num42 == 0 && num57 > num45 && num58 > num46)
                {
                    if (num58 > num57)
                    {
                        num58 = math.max(1, num58 - 1);
                    }
                    else if (num57 > num58)
                    {
                        num57 = math.max(1, num57 - 1);
                    }
                    else if (m_LeftHandTraffic ? (num57 <= 1) : (num58 > 1))
                    {
                        num58 = math.max(1, num58 - 1);
                    }
                    else
                    {
                        num57 = math.max(1, num57 - 1);
                    }
                }
                int num59 = num10 + num18;
                int num60 = num14 + num22;
                if (num29 == 0)
                {
                    num59 -= num19;
                    num60 -= num23;
                }
                int num61 = -1;
                int num62 = targetBuffer.Length;
                if (num59 < stackList.Length)
                {
                    num61 = stackList[num59];
                }
                else if (stackList.Length != 0)
                {
                    num61 = stackList[^1];
                }
                if (num60 < stackList.Length)
                {
                    num62 = stackList[stackList.Length - num60 - 1];
                }
                else if (stackList.Length != 0)
                {
                    num62 = stackList[0];
                }

                for (int num63 = 0; num63 < k; num63++)
                {
                    int num64 = num63;
                    ConnectPosition sourcePosition3 = sourceBuffer[num63];
                    int num65 = 0;
                    int num66 = 0;
                    int num67 = stackList.Length - num60;
                    int num68 = stackList2.Length;
                    while (num68 > 0 && stackList2[num68 - 1] > num62)
                    {
                        num68--;
                    }
                    while (num65 < num67 || num66 < num68)
                    {
                        int num70;
                        ConnectPosition targetPosition5;
                        bool flag3;
                        if (num65 < num67)
                        {
                            int num69 = num65;
                            num70 = stackList[num65++];
                            targetPosition5 = targetBuffer[num70];
                            flag3 = true;
                            if (num66 < num68)
                            {
                                int num71 = stackList2[num66];
                                ConnectPosition connectPosition7 = targetBuffer[num71];
                                if (num71 < num70 || targetPosition5.m_GroupIndex == connectPosition7.m_GroupIndex)
                                {
                                    targetPosition5 = connectPosition7;
                                    num70 = num71;
                                    num65 -= math.select(0, 1, targetPosition5.m_GroupIndex != connectPosition7.m_GroupIndex);
                                    num66++;
                                    flag3 = false;
                                }
                            }
                            if (num65 != num69)
                            {
                                for (; num65 < num67 && targetBuffer[stackList[num65]].m_GroupIndex == targetPosition5.m_GroupIndex; num65++) { }
                            }
                        }
                        else
                        {
                            num70 = stackList2[num66++];
                            targetPosition5 = targetBuffer[num70];
                            flag3 = false;
                        }
                        if ((!flag3 || num64 < num2 || num64 >= num2 + num) && (sourcePosition3.m_RoadTypes & targetPosition5.m_RoadTypes) != 0)
                        {
                            
                            /*NON-STOCK*/
                            LaneEndKey item = new LaneEndKey(sourcePosition3.m_Owner, sourcePosition3.m_LaneData.m_Index);
                            if (modifiedLaneEndConnections.Contains(item))
                            {
                                continue;
                            }
                            /*NON-STOCK-END*/
                            bool flag4 = flag3 && targetPosition5.m_RoadTypes != sourcePosition3.m_RoadTypes;
                            bool right5;
                            bool gentle5;
                            bool uturn5;
                            bool isTurn = IsTurn(sourcePosition3, targetPosition5, out right5, out gentle5, out uturn5);
                            uint group = (uint)(sourcePosition3.m_GroupIndex | (targetPosition5.m_GroupIndex << 16));
                            bool isLeftLimit = num64 == 0 && num70 == 0;
                            bool isRightLimit = (num64 == sourceBuffer.Length - 1) & (num70 == targetBuffer.Length - 1);
                            float curviness = -1f;
                            bool isSkipped = false;
                            NetGeometryData netGeometryData3 = m_PrefabGeometryData[m_PrefabRefData[targetPosition5.m_Owner]];
                            flag4 |= ((netGeometryData.m_MergeLayers & netGeometryData2.m_MergeLayers & netGeometryData3.m_MergeLayers) == 0);
                            if (CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, ref isSkipped, owner, laneBuffer, middleConnections, sourcePosition3, targetPosition5, intersectionFlags, group, 0, flag4, isForbidden: false, isTemp,
                                trackOnly: false, yield, ownerTemp, isTurn, right5, gentle5, uturn5, isRoundabout: false, isLeftLimit, isRightLimit, isMergeLeft: false, isMergeRight: false, fixedTangents: false, roadPassThrough))
                            {
                                createdConnections.Add(new ConnectionKey(sourcePosition3, targetPosition5));
                                /*NON-STOCK*/
                                Logger.DebugLaneSystem(
                                    $"Added Node Lane connections [1stLoop: {sourcePosition3.m_RoadTypes}|{targetPosition5.m_RoadTypes}][0] {new ConnectionKey(sourcePosition3, targetPosition5).GetString()} [ {sourcePosition3.m_GroupIndex}[{sourcePosition3.m_GroupIndex >> 8}] ({sourcePosition3.m_LaneData.m_Group}) | {targetPosition5.m_GroupIndex}[{targetPosition5.m_GroupIndex >> 8}] ({targetPosition5.m_LaneData.m_Group}) ] G: [{(byte)(group)}|{(byte)(group >> 8)}] | [{(byte)(group >> 16)}|{(byte)(group >> 24)}] <= {group}");
                                createdGroups.Add(new ConnectionKey(sourcePosition3.m_Owner.Index, sourcePosition3.m_LaneData.m_Group, targetPosition5.m_Owner.Index, targetPosition5.m_LaneData.m_Group));
                                /*NON-STOCK-END*/
                            }
                        }
                    }
                }

                for (int num72 = 0; num72 < l; num72++)
                {
                    int num73 = sourceBuffer.Length - l + num72;
                    ConnectPosition sourcePosition4 = sourceBuffer[num73];
                    int num74 = num59;
                    int num75;
                    for (num75 = 0; num75 < stackList2.Length && stackList2[num75] < num61; num75++) { }
                    while (num74 < stackList.Length || num75 < stackList2.Length)
                    {
                        int num76;
                        ConnectPosition targetPosition6;
                        bool flag5;
                        if (num74 < stackList.Length)
                        {
                            num76 = stackList[num74++];
                            targetPosition6 = targetBuffer[num76];
                            flag5 = true;
                            for (; num74 < stackList.Length; num74++)
                            {
                                ConnectPosition connectPosition8 = targetBuffer[stackList[num74]];
                                if (connectPosition8.m_GroupIndex != targetPosition6.m_GroupIndex)
                                {
                                    break;
                                }
                                targetPosition6 = connectPosition8;
                            }
                            if (num75 < stackList2.Length)
                            {
                                int num77 = stackList2[num75];
                                ConnectPosition connectPosition9 = targetBuffer[num77];
                                if (num77 < num76 || targetPosition6.m_GroupIndex == connectPosition9.m_GroupIndex)
                                {
                                    targetPosition6 = connectPosition9;
                                    num76 = num77;
                                    num74 -= math.select(0, 1, targetPosition6.m_GroupIndex != connectPosition9.m_GroupIndex);
                                    num75++;
                                    flag5 = false;
                                }
                            }
                        }
                        else
                        {
                            num76 = stackList2[num75++];
                            targetPosition6 = targetBuffer[num76];
                            flag5 = false;
                        }
                        if ((!flag5 || num73 < num2 || num73 >= num2 + num) && (sourcePosition4.m_RoadTypes & targetPosition6.m_RoadTypes) != 0)
                        {
                            /*NON-STOCK*/
                            LaneEndKey item = new LaneEndKey(sourcePosition4.m_Owner, sourcePosition4.m_LaneData.m_Index);
                            if (modifiedLaneEndConnections.Contains(item))
                            {
                                continue;
                            }
                            /*NON-STOCK-END*/
                            bool flag6 = flag5 && targetPosition6.m_RoadTypes != sourcePosition4.m_RoadTypes;
                            bool right6;
                            bool gentle6;
                            bool uturn6;
                            bool isTurn2 = IsTurn(sourcePosition4, targetPosition6, out right6, out gentle6, out uturn6);
                            uint group2 = (uint)(sourcePosition4.m_GroupIndex | (targetPosition6.m_GroupIndex << 16));
                            bool isLeftLimit2 = num73 == 0 && num76 == 0;
                            bool isRightLimit2 = (num73 == sourceBuffer.Length - 1) & (num76 == targetBuffer.Length - 1);
                            float curviness2 = -1f;
                            bool isSkipped2 = false;
                            NetGeometryData netGeometryData4 = m_PrefabGeometryData[m_PrefabRefData[targetPosition6.m_Owner]];
                            flag6 |= ((netGeometryData.m_MergeLayers & netGeometryData2.m_MergeLayers & netGeometryData4.m_MergeLayers) == 0);
                            if (CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness2, ref isSkipped2, owner, laneBuffer, middleConnections, sourcePosition4, targetPosition6, intersectionFlags, group2, 16383, flag6, isForbidden: false,
                                isTemp, trackOnly: false, yield, ownerTemp, isTurn2, right6, gentle6, uturn6, isRoundabout: false, isLeftLimit2, isRightLimit2, isMergeLeft: false, isMergeRight: false, fixedTangents: false, roadPassThrough))
                            {
                                createdConnections.Add(new ConnectionKey(sourcePosition4, targetPosition6));
                                /*NON-STOCK*/
                                Logger.DebugLaneSystem(
                                    $"Added Node Lane connections [2ndLoop: {sourcePosition4.m_RoadTypes}|{targetPosition6.m_RoadTypes}][16383] {new ConnectionKey(sourcePosition4, targetPosition6).GetString()} [ {sourcePosition4.m_GroupIndex}[{sourcePosition4.m_GroupIndex >> 8}] ({sourcePosition4.m_LaneData.m_Group}) | {targetPosition6.m_GroupIndex}[{targetPosition6.m_GroupIndex >> 8}] ({targetPosition6.m_LaneData.m_Group}) ] G: [{(byte)(group2)}|{(byte)(group2 >> 8)}] | [{(byte)(group2 >> 16)}|{(byte)(group2 >> 24)}] <= {group2}");
                                createdGroups.Add(new ConnectionKey(sourcePosition4.m_Owner.Index, sourcePosition4.m_LaneData.m_Group, targetPosition6.m_Owner.Index, targetPosition6.m_LaneData.m_Group));
                                /*NON-STOCK-END*/
                            }
                        }
                    }
                }
                
                int curTargetPosIdx = 0;
                while (curTargetPosIdx < stackList.Length)
                {
                    ConnectPosition connectPosition10 = targetBuffer[stackList[curTargetPosIdx]];
                    ConnectPosition targetPosition7 = connectPosition10;
                    int num79;
                    for (num79 = curTargetPosIdx + 1; num79 < stackList.Length; num79++)
                    {
                        ConnectPosition connectPosition11 = targetBuffer[stackList[num79]];
                        if (connectPosition11.m_GroupIndex != connectPosition10.m_GroupIndex)
                        {
                            break;
                        }
                        targetPosition7 = connectPosition11;
                    }
                    int num80 = num79 - curTargetPosIdx;
                    int num81 = stackList.Length - num79;
                    uint group = (uint)(sourcePositionFirst.m_GroupIndex | (connectPosition10.m_GroupIndex << 16));
                    bool isTurn = curTargetPosIdx < num10 + num18 || num81 < num14 + num22;
                    bool isUTurn = curTargetPosIdx < num10 || num81 < num14;
                    bool flag9 = curTargetPosIdx < num11 || num81 < num15;
                    bool isForbidden = curTargetPosIdx < num12 + num20 || num81 < num16 + num24;
                    bool isRight = num81 < num14 + num22;
                    bool isGentle = false;
                    int num82;
                    int num83;
                    if (isTurn)
                    {
                        if (isUTurn)
                        {
                            if (isRight)
                            {
                                num82 = (num81 * num56 + math.select(0, num14 - 1, num56 > num14)) / num14;
                                num83 = ((num81 + num80) * num56 + num14 - 1) / num14 - 1;
                                num82 = num - num82 - 1;
                                num83 = num - num83 - 1;
                                CommonUtils.Swap(ref num82, ref num83);
                            }
                            else
                            {
                                int num84 = curTargetPosIdx;
                                num82 = (num84 * num55 + math.select(0, num10 - 1, num55 > num10)) / num10;
                                num83 = ((num84 + num80) * num55 + num10 - 1) / num10 - 1;
                            }
                        }
                        else if (isRight)
                        {
                            int num85 = num81 - num14;
                            num82 = (num85 * num58 + math.select(0, num22 - 1, num58 > num22)) / num22;
                            num83 = ((num85 + num80) * num58 + num22 - 1) / num22 - 1;
                            num82 = num - num41 - num82 - 1;
                            num83 = num - num41 - num83 - 1;
                            CommonUtils.Swap(ref num82, ref num83);
                            IsTurn(sourceBuffer[num82 + num2], connectPosition10, out bool _, out bool gentle7, out bool _);
                            IsTurn(sourceBuffer[num83 + num2], targetPosition7, out bool _, out bool gentle8, out bool _);
                            isGentle = (gentle7 && gentle8);
                        }
                        else
                        {
                            int num86 = curTargetPosIdx - num10;
                            num82 = (num86 * num57 + math.select(0, num18 - 1, num57 > num18)) / num18;
                            num83 = ((num86 + num80) * num57 + num18 - 1) / num18 - 1;
                            num82 = num40 + num82;
                            num83 = num40 + num83;
                            IsTurn(sourceBuffer[num82 + num2], connectPosition10, out bool _, out bool gentle9, out bool _);
                            IsTurn(sourceBuffer[num83 + num2], targetPosition7, out bool _, out bool gentle10, out bool _);
                            isGentle = (gentle9 && gentle10);
                        }
                    }
                    else
                    {
                        int num87 = curTargetPosIdx - num10 - num18;
                        if (num42 == 0)
                        {
                            num82 = ((!m_LeftHandTraffic) ? math.min(num40 + num45, num - 1) : math.max(num40 + num45 - 1, 0));
                            num83 = num82;
                        }
                        else
                        {
                            num82 = (num87 * num42 + math.select(0, num29 - 1, num42 > num29)) / num29;
                            num83 = ((num87 + num80) * num42 + num29 - 1) / num29 - 1;
                            num82 = num40 + num45 + num82;
                            num83 = num40 + num45 + num83;
                        }
                        if (num30 > 0)
                        {
                            isForbidden = ((!m_LeftHandTraffic) ? (isForbidden || num29 - num87 - 1 < num30) : (isForbidden || num87 < num30));
                        }
                    }
                    int num88 = num83 - num82 + 1;
                    int num89 = math.max(num88, num80);
                    int num90 = math.min(num88, num80);
                    int num91 = 0;
                    int num92 = num89 - num90;
                    int num93 = 0;
                    int num94 = 0;
                    float num95 = float.MaxValue;
                    int2 @int = 0;
                    if (num80 > num88)
                    {
                        @int = CalculateSourcesBetween(sourceBuffer[num82 + num2], targetBuffer[stackList[curTargetPosIdx]], allSources);
                        if (math.any(@int >= 1))
                        {
                            int num96 = math.csum(@int);
                            int num97;
                            int num98;
                            if (num96 > num92)
                            {
                                num97 = @int.x * num92 / num96;
                                num98 = @int.y * num92 / num96;
                                if ((num92 >= 2) & math.all(@int >= 1))
                                {
                                    num97 = math.max(num97, 1);
                                    num98 = math.max(num98, 1);
                                }
                            }
                            else
                            {
                                num97 = @int.x;
                                num98 = @int.y;
                            }
                            num91 += num97;
                            num92 -= num98;
                        }
                    }
                    for (int num99 = num91; num99 <= num92; num99++)
                    {
                        int num100 = math.max(num99 + num88 - num89, 0);
                        int num101 = math.max(num99 + num80 - num89, 0);
                        num100 += num82;
                        num101 += curTargetPosIdx;
                        ConnectPosition connectPosition12 = sourceBuffer[num100 + num2];
                        ConnectPosition connectPosition13 = sourceBuffer[num100 + num2 + num90 - 1];
                        ConnectPosition connectPosition14 = targetBuffer[stackList[num101]];
                        ConnectPosition connectPosition15 = targetBuffer[stackList[num101 + num90 - 1]];
                        float num102 = math.max(0f, math.dot(connectPosition12.m_Tangent, connectPosition14.m_Tangent) * -0.5f);
                        float num103 = math.max(0f, math.dot(connectPosition13.m_Tangent, connectPosition15.m_Tangent) * -0.5f);
                        num102 *= math.distance(connectPosition12.m_Position.xz, connectPosition14.m_Position.xz);
                        num103 *= math.distance(connectPosition13.m_Position.xz, connectPosition15.m_Position.xz);
                        connectPosition12.m_Position.xz += connectPosition12.m_Tangent.xz * num102;
                        connectPosition14.m_Position.xz += connectPosition14.m_Tangent.xz * num102;
                        connectPosition13.m_Position.xz += connectPosition13.m_Tangent.xz * num103;
                        connectPosition15.m_Position.xz += connectPosition15.m_Tangent.xz * num103;
                        float x = math.distancesq(connectPosition12.m_Position.xz, connectPosition14.m_Position.xz);
                        float y = math.distancesq(connectPosition13.m_Position.xz, connectPosition15.m_Position.xz);
                        float num104 = math.max(x, y);
                        if (num104 < num95)
                        {
                            num93 = math.min(num89 - num80 - num99, 0);
                            num94 = math.min(num89 - num88 - num99, 0);
                            num95 = num104;
                        }
                    }
                    for (int laneIndex = 0; laneIndex < num89; laneIndex++)
                    {
                        int num106 = math.clamp(laneIndex + num93, 0, num88 - 1);
                        int num107 = math.clamp(laneIndex + num94, 0, num80 - 1);
                        bool flag12 = laneIndex + num93 < 0;
                        bool flag13 = laneIndex + num93 >= num88;
                        bool flag14 = laneIndex + num94 < 0;
                        bool flag15 = laneIndex + num94 >= num80;
                        bool isMergeLeft = flag12 || flag14;
                        bool isMergeRight = flag13 || flag15;
                        bool isUnsafe = (!isTurn)
                            ? (isMergeLeft || isMergeRight || isForbidden)
                            : ((!isRight) ? (isMergeLeft || isMergeRight || flag9 || isForbidden || (isUTurn && num40 == 0)) : (isMergeLeft || isMergeRight || flag9 || isForbidden || (isUTurn && num41 == 0)));
                        num106 += num82;
                        num107 += curTargetPosIdx;
                        int num108 = stackList[num107];
                        bool isLeftLimit = num106 == 0 && num107 == num3;
                        bool isRightLimit = num106 == num - 1 && num107 == num4;
                        ConnectPosition sourcePosition5 = sourceBuffer[num106 + num2];
                        ConnectPosition connectPosition16 = targetBuffer[num108];
                        if ((sourcePosition5.m_CompositionData.m_RoadFlags & connectPosition16.m_CompositionData.m_RoadFlags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0 && ((flag12 & (@int.x > 0)) | (flag13 & (@int.y > 0))))
                        {
                            continue;
                        }
                        bool flag19 = ((uint)allRoadTypes & (uint)(byte)(~(int)sourcePosition5.m_RoadTypes)) != 0 && sourcePosition5.m_RoadTypes != RoadTypes.Car;
                        bool flag20 = ((uint)allRoadTypes & (uint)(byte)(~(int)connectPosition16.m_RoadTypes)) != 0 && connectPosition16.m_RoadTypes != RoadTypes.Car;
                        if (laneIndex == 0 || laneIndex == num89 - 1)
                        {
                            int num109 = math.select(1, 0, flag20);
                            foreach (int num111 in stackList2)
                            {
                                if (math.abs(num111 - num108) != num109 || (k != 0 && num111 <= num62) || (l != 0 && num111 >= num61))
                                {
                                    continue;
                                }
                                ConnectPosition targetPosition8 = targetBuffer[num111];
                                if (targetPosition8.m_GroupIndex == connectPosition16.m_GroupIndex && (sourcePosition5.m_RoadTypes & targetPosition8.m_RoadTypes) != 0)
                                {
                                    LaneEndKey item = new LaneEndKey(sourcePosition5.m_Owner, sourcePosition5.m_LaneData.m_Index);
                                    if (modifiedLaneEndConnections.Contains(item))
                                    {
                                        continue;
                                    }
                                    int num112 = math.select(0, 16383, num111 > num108);
                                    bool isLeftLimit4 = num106 + num2 == 0 && num111 == 0;
                                    bool isRightLimit4 = (num106 + num2 == sourceBuffer.Length - 1) & (num111 == targetBuffer.Length - 1);
                                    float curviness = -1f;
                                    bool isSkipped = false;
                                    if (CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, ref isSkipped, owner, laneBuffer, middleConnections, sourcePosition5, targetPosition8, intersectionFlags, group, (ushort)num112,
                                        isUnsafe: true, isForbidden, isTemp, trackOnly: false, yield, ownerTemp, isTurn, isRight, isGentle, isUTurn, isRoundabout: false, isLeftLimit4, isRightLimit4, isMergeLeft: false, isMergeRight: false,
                                        fixedTangents: false,
                                        roadPassThrough))
                                    {
                                        Logger.DebugLaneSystem(
                                            $"Added Node Lane connections [FirstLast: {sourcePosition5.m_RoadTypes}|{targetPosition8.m_RoadTypes}][{num112}] {new ConnectionKey(sourcePosition5, targetPosition8).GetString()} [ {sourcePosition5.m_GroupIndex}[{sourcePosition5.m_GroupIndex >> 8}] ({sourcePosition5.m_LaneData.m_Group}) | {targetPosition8.m_GroupIndex}[{targetPosition8.m_GroupIndex >> 8}] ({targetPosition8.m_LaneData.m_Group}) ] G: [{(byte)(group)}|{(byte)(group >> 8)}] | [{(byte)(group >> 16)}|{(byte)(group >> 24)}] <= {group}");

                                        createdConnections.Add(new ConnectionKey(sourcePosition5, targetPosition8));
                                        createdGroups.Add(new ConnectionKey(sourcePosition5.m_Owner.Index, sourcePosition5.m_LaneData.m_Group, targetPosition8.m_Owner.Index, targetPosition8.m_LaneData.m_Group));
                                    }
                                }
                            }
                        }
                        if (!flag20 && (sourcePosition5.m_RoadTypes & connectPosition16.m_RoadTypes) != 0)
                        {
                            LaneEndKey item = new LaneEndKey(sourcePosition5.m_Owner, sourcePosition5.m_LaneData.m_Index);
                            // Logger.DebugLaneSystem($"Creating Node Lane ({m}): {item.GetString()}");
                            if (modifiedLaneEndConnections.Contains(item))
                            {
                                continue;
                            }
                            NetGeometryData netGeometryData5 = m_PrefabGeometryData[m_PrefabRefData[connectPosition16.m_Owner]];
                            isUnsafe |= ((netGeometryData.m_MergeLayers & netGeometryData2.m_MergeLayers & netGeometryData5.m_MergeLayers) == 0);
                            isUnsafe = (isUnsafe || flag19);
                            float curviness = -1f;
                            bool isSkipped = false;
                            int priority = yield;
                            if (priorities.Count > 0 && priorities.TryGetValue(sourcePosition5.m_LaneData.m_Index, out int p))
                            {
                                priority = p;
                            }
                            if (CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, ref isSkipped, owner, laneBuffer, middleConnections, sourcePosition5, connectPosition16, intersectionFlags, group, (ushort)(laneIndex + 1), isUnsafe,
                                isForbidden, isTemp, trackOnly: false, priority, ownerTemp,
                                isTurn, isRight, isGentle, isUTurn, isRoundabout: false, isLeftLimit, isRightLimit, isMergeLeft, isMergeRight, fixedTangents: false, roadPassThrough))
                            {
                                Logger.DebugLaneSystem(
                                    $"Added Node Lane connections {new ConnectionKey(sourcePosition5, connectPosition16).GetString()} [ {sourcePosition5.m_GroupIndex}[{sourcePosition5.m_GroupIndex >> 8}] ({sourcePosition5.m_LaneData.m_Group}) | {connectPosition16.m_GroupIndex}[{connectPosition16.m_GroupIndex >> 8}] ({connectPosition16.m_LaneData.m_Group}) ] G: [{(byte)(group)}|{(byte)(group >> 8)}] | [{(byte)(group >> 16)}|{(byte)(group >> 24)}] <= {group}");
                                createdConnections.Add(new ConnectionKey(sourcePosition5, connectPosition16));
                                createdGroups.Add(new ConnectionKey(sourcePosition5.m_Owner.Index, sourcePosition5.m_LaneData.m_Group, connectPosition16.m_Owner.Index, connectPosition16.m_LaneData.m_Group));
                                // Logger.DebugLaneSystem("Adding to created! (CreateNodeCarLanes)");
                            }
                            if (isSkipped)
                            {
                                connectPosition16.m_SkippedCount++;
                                targetBuffer[num108] = connectPosition16;
                            }
                            else if (isForbidden)
                            {
                                connectPosition16.m_UnsafeCount++;
                                connectPosition16.m_ForbiddenCount++;
                                targetBuffer[num108] = connectPosition16;
                            }
                            else if (isUTurn && (isRight ? num41 : num40) == 0)
                            {
                                connectPosition16.m_UnsafeCount++;
                                targetBuffer[num108] = connectPosition16;
                            }
                        }
                    }
                    curTargetPosIdx = num79;
                }
            }

            private void ProcessTrackConnectPositions(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, NativeList<MiddleConnection> middleConnections,
                NativeParallelHashSet<ConnectionKey> createdConnections, NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer, bool isTemp, Temp ownerTemp, /*NON-STOCK*/NativeHashSet<LaneEndKey> modifiedLaneEndConnections
            ) {
                sourceBuffer.Sort(default(SourcePositionComparer));
                ConnectPosition sourcePosition = sourceBuffer[0];
                SortTargets(sourcePosition, targetBuffer);
                CreateNodeTrackLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, middleConnections, createdConnections, sourceBuffer, targetBuffer, isTemp, ownerTemp, /*NON-STOCK*/modifiedLaneEndConnections);
            }

            private void CreateNodeTrackLanes(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, NativeList<MiddleConnection> middleConnections,
                NativeParallelHashSet<ConnectionKey> createdConnections, NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer, bool isTemp, Temp ownerTemp, /*NON-STOCK*/NativeHashSet<LaneEndKey> modifiedLaneEndConnections
            ) {
                ConnectPosition connectPosition = sourceBuffer[0];
                for (int i = 1; i < sourceBuffer.Length; i++)
                {
                    ConnectPosition connectPosition2 = sourceBuffer[i];
                    connectPosition.m_Position += connectPosition2.m_Position;
                    connectPosition.m_Tangent += connectPosition2.m_Tangent;
                }
                connectPosition.m_Position /= (float)sourceBuffer.Length;
                connectPosition.m_Tangent.y = 0f;
                connectPosition.m_Tangent = math.normalizesafe(connectPosition.m_Tangent);
                TrackLaneData trackLaneData = m_TrackLaneData[connectPosition.m_LaneData.m_Lane];
                NetCompositionData netCompositionData = m_PrefabCompositionData[connectPosition.m_NodeComposition];
                int num = targetBuffer.Length;
                int num2 = 0;
                ConnectPosition connectPosition3 = targetBuffer[0];
                int num3 = 0;
                for (int j = 1; j < targetBuffer.Length; j++)
                {
                    ConnectPosition connectPosition4 = targetBuffer[j];
                    if (connectPosition4.m_Owner.Equals(connectPosition3.m_Owner))
                    {
                        connectPosition3.m_Position += connectPosition4.m_Position;
                        connectPosition3.m_Tangent += connectPosition4.m_Tangent;
                        continue;
                    }
                    connectPosition3.m_Position /= (float)(j - num3);
                    connectPosition3.m_Tangent.y = 0f;
                    connectPosition3.m_Tangent = math.normalizesafe(connectPosition3.m_Tangent);
                    if (!connectPosition3.m_Owner.Equals(connectPosition.m_Owner))
                    {
                        float distance = math.max(1f, math.distance(connectPosition.m_Position, connectPosition3.m_Position));
                        if (NetUtils.CalculateCurviness(connectPosition.m_Tangent, -connectPosition3.m_Tangent, distance) <= trackLaneData.m_MaxCurviness)
                        {
                            num = math.min(num, num3);
                            num2 = math.max(num2, j - num);
                        }
                    }
                    connectPosition3 = connectPosition4;
                    num3 = j;
                }
                connectPosition3.m_Position /= (float)(targetBuffer.Length - num3);
                connectPosition3.m_Tangent.y = 0f;
                connectPosition3.m_Tangent = math.normalizesafe(connectPosition3.m_Tangent);
                if (!connectPosition3.m_Owner.Equals(connectPosition.m_Owner))
                {
                    float distance2 = math.max(1f, math.distance(connectPosition.m_Position, connectPosition3.m_Position));
                    if (NetUtils.CalculateCurviness(connectPosition.m_Tangent, -connectPosition3.m_Tangent, distance2) <= trackLaneData.m_MaxCurviness)
                    {
                        num = math.min(num, num3);
                        num2 = math.max(num2, targetBuffer.Length - num);
                    }
                }
                for (int k = 0; k < sourceBuffer.Length; k++)
                {
                    ConnectPosition sourcePosition = sourceBuffer[k];
                    /*NON-STOCK*/
                    if (modifiedLaneEndConnections.Contains(new LaneEndKey(sourcePosition.m_Owner, sourcePosition.m_LaneData.m_Index)))
                    {
                        continue;
                    }
                    /*NON-STOCK-END*/
                    for (int l = 0; l < num2; l++)
                    {
                        ConnectPosition targetPosition = targetBuffer[num + l];
                        if (createdConnections.Contains(new ConnectionKey(sourcePosition, targetPosition)))
                        {
                            continue;
                        }
                        if ((netCompositionData.m_Flags.m_General & CompositionFlags.General.Intersection) == 0)
                        {
                            float num4 = math.distance(sourcePosition.m_Position, targetPosition.m_Position);
                            if (num4 > 1f)
                            {
                                float3 x = math.normalizesafe(sourcePosition.m_Tangent);
                                float3 y = -math.normalizesafe(targetPosition.m_Tangent);
                                if (math.dot(x, y) > 0.99f)
                                {
                                    float3 @float = (targetPosition.m_Position - sourcePosition.m_Position) / num4;
                                    if (math.min(math.dot(x, @float), math.dot(@float, y)) < 0.01f)
                                    {
                                        continue;
                                    }
                                }
                            }
                        }
                        bool isLeftLimit = num + l == 0;
                        bool isRightLimit = num + l == targetBuffer.Length - 1;
                        bool right;
                        bool gentle;
                        bool uturn;
                        bool isTurn = IsTurn(sourcePosition, targetPosition, out right, out gentle, out uturn);
                        bool isSkipped = false;
                        float curviness = -1f;
                        CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, ref isSkipped, owner, laneBuffer, middleConnections, sourcePosition, targetPosition, default(CompositionFlags), 0u, 0, isUnsafe: false, isForbidden: false, isTemp,
                            trackOnly: true, 0, ownerTemp, isTurn, right, gentle, uturn, isRoundabout: false, isLeftLimit, isRightLimit, isMergeLeft: false, isMergeRight: false, fixedTangents: false, RoadTypes.None);
                    }
                }
            }

            private bool IsTurn(ConnectPosition sourcePosition, ConnectPosition targetPosition, out bool right, out bool gentle, out bool uturn) {
                return NetUtils.IsTurn(sourcePosition.m_Position.xz, sourcePosition.m_Tangent.xz, targetPosition.m_Position.xz, targetPosition.m_Tangent.xz, out right, out gentle, out uturn);
            }

            private void ModifyCurveHeight(ref Bezier4x3 curve, float startBaseHeight, float endBaseHeight, NetCompositionData startCompositionData, NetCompositionData endCompositionData) {
                float num = startBaseHeight + startCompositionData.m_SurfaceHeight.min;
                float num2 = endBaseHeight + endCompositionData.m_SurfaceHeight.min;
                float num3 = math.max(curve.a.y, curve.d.y);
                float num4 = math.min(curve.a.y, curve.d.y);
                if ((startCompositionData.m_Flags.m_General & (CompositionFlags.General.Roundabout | CompositionFlags.General.LevelCrossing)) != 0)
                {
                    curve.b.y += (math.max(0f, num4 - curve.b.y) - math.max(0f, curve.b.y - num3)) * (2f / 3f);
                }
                if ((endCompositionData.m_Flags.m_General & (CompositionFlags.General.Roundabout | CompositionFlags.General.LevelCrossing)) != 0)
                {
                    curve.c.y += (math.max(0f, num4 - curve.c.y) - math.max(0f, curve.c.y - num3)) * (2f / 3f);
                }
                curve.b.y += math.max(0f, num - math.max(curve.a.y, curve.b.y)) * 1.33333337f;
                curve.c.y += math.max(0f, num2 - math.max(curve.d.y, curve.c.y)) * 1.33333337f;
            }

            private bool CanConnectTrack(bool isUTurn, bool isRoundabout, ConnectPosition sourcePosition, ConnectPosition targetPosition, PrefabRef prefabRef) {
                if (isUTurn || isRoundabout)
                {
                    return false;
                }
                if (((sourcePosition.m_LaneData.m_Flags | targetPosition.m_LaneData.m_Flags) & LaneFlags.Master) != 0)
                {
                    return false;
                }
                if ((sourcePosition.m_LaneData.m_Flags & targetPosition.m_LaneData.m_Flags & LaneFlags.Track) == 0)
                {
                    return false;
                }
                if (m_TrackLaneData.TryGetComponent(prefabRef.m_Prefab, out TrackLaneData componentData))
                {
                    float distance = math.max(1f, math.distance(sourcePosition.m_Position, targetPosition.m_Position));
                    sourcePosition.m_Tangent.y = 0f;
                    targetPosition.m_Tangent.y = 0f;
                    if (NetUtils.CalculateCurviness(sourcePosition.m_Tangent, -targetPosition.m_Tangent, distance) > componentData.m_MaxCurviness)
                    {
                        return false;
                    }
                    return sourcePosition.m_TrackTypes == targetPosition.m_TrackTypes;
                }
                return false;
            }

            private bool CheckPrefab(ref Entity prefab, ref Unity.Mathematics.Random random, out Unity.Mathematics.Random outRandom, LaneBuffer laneBuffer)
            {
                if (!m_EditorMode)
                {
                    if (m_PlaceholderObjects.TryGetBuffer(prefab, out DynamicBuffer<PlaceholderObjectElement> bufferData))
                    {
                        float num = -1f;
                        Entity entity = Entity.Null;
                        Entity key = Entity.Null;
                        Unity.Mathematics.Random random2 = default(Unity.Mathematics.Random);
                        int num2 = 0;
                        for (int i = 0; i < bufferData.Length; i++)
                        {
                            Entity @object = bufferData[i].m_Object;
                            float num3 = 0f;
                            if (m_ObjectRequirements.TryGetBuffer(@object, out DynamicBuffer<ObjectRequirementElement> bufferData2))
                            {
                                int num4 = -1;
                                bool flag = true;
                                for (int j = 0; j < bufferData2.Length; j++)
                                {
                                    ObjectRequirementElement objectRequirementElement = bufferData2[j];
                                    if (objectRequirementElement.m_Group != num4)
                                    {
                                        if (!flag)
                                        {
                                            break;
                                        }
                                        num4 = objectRequirementElement.m_Group;
                                        flag = false;
                                    }
                                    flag |= (objectRequirementElement.m_Requirement == m_DefaultTheme);
                                }
                                if (!flag)
                                {
                                    continue;
                                }
                            }
                            SpawnableObjectData spawnableObjectData = m_PrefabSpawnableObjectData[@object];
                            Entity entity2 = (spawnableObjectData.m_RandomizationGroup != Entity.Null) ? spawnableObjectData.m_RandomizationGroup : @object;
                            Unity.Mathematics.Random random3 = random;
                            random.NextInt();
                            random.NextInt();
                            if (laneBuffer.m_SelectedSpawnables.TryGetValue(entity2, out Unity.Mathematics.Random item))
                            {
                                num3 += 0.5f;
                                random3 = item;
                            }
                            if (num3 > num)
                            {
                                num = num3;
                                entity = @object;
                                key = entity2;
                                random2 = random3;
                                num2 = spawnableObjectData.m_Probability;
                            }
                            else if (num3 == num)
                            {
                                num2 += spawnableObjectData.m_Probability;
                                if (random.NextInt(num2) < spawnableObjectData.m_Probability)
                                {
                                    entity = @object;
                                    key = entity2;
                                    random2 = random3;
                                }
                            }
                        }
                        if (random.NextInt(100) < num2)
                        {
                                laneBuffer.m_SelectedSpawnables.TryAdd(key, random2);
                            prefab = entity;
                            outRandom = random2;
                            return true;
                        }
                        outRandom = random;
                        random.NextInt();
                        random.NextInt();
                        return false;
                    }
                    Entity key2 = prefab;
                    if (m_PrefabSpawnableObjectData.TryGetComponent(prefab, out SpawnableObjectData componentData) && componentData.m_RandomizationGroup != Entity.Null)
                    {
                        key2 = componentData.m_RandomizationGroup;
                    }
                    outRandom = random;
                    random.NextInt();
                    random.NextInt();
                    if (laneBuffer.m_SelectedSpawnables.TryGetValue(key2, out Unity.Mathematics.Random item2))
                    {
                        outRandom = item2;
                    }
                    else
                    {
                        laneBuffer.m_SelectedSpawnables.TryAdd(key2, outRandom);
                    }
                    return true;
                }
                outRandom = random;
                random.NextInt();
                random.NextInt();
                return true;
            }

            private bool CreateNodeLane(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, ref float curviness, ref bool isSkipped, Entity owner, LaneBuffer laneBuffer, NativeList<MiddleConnection> middleConnections,
                ConnectPosition sourcePosition, ConnectPosition targetPosition, CompositionFlags intersectionFlags, uint group, ushort laneIndex, bool isUnsafe, bool isForbidden, bool isTemp, bool trackOnly, int yield, Temp ownerTemp, bool isTurn,
                bool isRight, bool isGentle, bool isUTurn, bool isRoundabout, bool isLeftLimit, bool isRightLimit, bool isMergeLeft, bool isMergeRight, bool fixedTangents, RoadTypes roadPassThrough,
                /*NON-STOCK*/bool forceRoadOnly = false
            ) {
                NetCompositionData netCompositionData = m_PrefabCompositionData[sourcePosition.m_NodeComposition];
                NetCompositionData netCompositionData2 = m_PrefabCompositionData[targetPosition.m_NodeComposition];
                if (isUTurn && (netCompositionData.m_State & CompositionState.BlockUTurn) != 0)
                {
                    return false;
                }
                if ((sourcePosition.m_LaneData.m_Flags & targetPosition.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                {
                    return false;
                }
                Owner component = default(Owner);
                component.m_Owner = owner;
                Temp temp = default(Temp);
                if (isTemp)
                {
                    temp.m_Flags = (ownerTemp.m_Flags & (TempFlags.Create | TempFlags.Delete | TempFlags.Select | TempFlags.Modify | TempFlags.Hidden));
                    if ((ownerTemp.m_Flags & TempFlags.Replace) != 0)
                    {
                        temp.m_Flags |= TempFlags.Modify;
                    }
                }
                LaneFlags laneFlags = (LaneFlags)0;
                PrefabRef prefabRef = default(PrefabRef);
                if (trackOnly)
                {
                    laneFlags = (sourcePosition.m_LaneData.m_Flags & ~(LaneFlags.Slave | LaneFlags.Master));
                    prefabRef.m_Prefab = sourcePosition.m_LaneData.m_Lane;
                    if ((laneFlags & (LaneFlags.Road | LaneFlags.Track)) == (LaneFlags.Road | LaneFlags.Track))
                    {
                        laneFlags &= ~LaneFlags.Road;
                        prefabRef.m_Prefab = m_TrackLaneData[prefabRef.m_Prefab].m_FallbackPrefab;
                    }
                }
                else
                {
                    if ((sourcePosition.m_LaneData.m_Flags & LaneFlags.Master) != 0)
                    {
                        laneFlags = sourcePosition.m_LaneData.m_Flags;
                        prefabRef.m_Prefab = sourcePosition.m_LaneData.m_Lane;
                    }
                    else if ((targetPosition.m_LaneData.m_Flags & LaneFlags.Master) != 0)
                    {
                        laneFlags = targetPosition.m_LaneData.m_Flags;
                        prefabRef.m_Prefab = targetPosition.m_LaneData.m_Lane;
                    }
                    else if ((sourcePosition.m_LaneData.m_Flags & LaneFlags.Slave) != 0)
                    {
                        laneFlags = sourcePosition.m_LaneData.m_Flags;
                        prefabRef.m_Prefab = sourcePosition.m_LaneData.m_Lane;
                    }
                    else if ((targetPosition.m_LaneData.m_Flags & LaneFlags.Slave) != 0)
                    {
                        laneFlags = targetPosition.m_LaneData.m_Flags;
                        prefabRef.m_Prefab = targetPosition.m_LaneData.m_Lane;
                    }
                    else
                    {
                        laneFlags = sourcePosition.m_LaneData.m_Flags;
                        prefabRef.m_Prefab = sourcePosition.m_LaneData.m_Lane;
                    }
                    int num = math.select(0, 1, (sourcePosition.m_CompositionData.m_RoadFlags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0);
                    int num2 = math.select(0, 1, (targetPosition.m_CompositionData.m_RoadFlags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0);
                    if (m_PrefabCompositionData.HasComponent(sourcePosition.m_EdgeComposition))
                    {
                        NetCompositionData netCompositionData3 = m_PrefabCompositionData[sourcePosition.m_EdgeComposition];
                        num = math.select(num, num + 2, (netCompositionData3.m_Flags.m_General & CompositionFlags.General.Tiles) != 0);
                        num = math.select(num, num - 4, (netCompositionData3.m_Flags.m_General & CompositionFlags.General.Gravel) != 0);
                    }
                    if (m_PrefabCompositionData.HasComponent(targetPosition.m_EdgeComposition))
                    {
                        NetCompositionData netCompositionData4 = m_PrefabCompositionData[targetPosition.m_EdgeComposition];
                        num2 = math.select(num2, num2 + 2, (netCompositionData4.m_Flags.m_General & CompositionFlags.General.Tiles) != 0);
                        num2 = math.select(num2, num2 - 4, (netCompositionData4.m_Flags.m_General & CompositionFlags.General.Gravel) != 0);
                    }
                    num = math.select(num, num + 8, (sourcePosition.m_LaneData.m_Flags & (LaneFlags.Pedestrian | LaneFlags.BicyclesOnly)) != 0);
                    num2 = math.select(num2, num2 + 8, (targetPosition.m_LaneData.m_Flags & (LaneFlags.Pedestrian | LaneFlags.BicyclesOnly)) != 0);
                    if (num > num2 && prefabRef.m_Prefab != sourcePosition.m_LaneData.m_Lane)
                    {
                        laneFlags = ((laneFlags & (LaneFlags.Slave | LaneFlags.Master)) | (sourcePosition.m_LaneData.m_Flags & ~(LaneFlags.Slave | LaneFlags.Master)));
                        prefabRef.m_Prefab = sourcePosition.m_LaneData.m_Lane;
                    }
                    if (num2 > num && prefabRef.m_Prefab != targetPosition.m_LaneData.m_Lane)
                    {
                        laneFlags = ((laneFlags & (LaneFlags.Slave | LaneFlags.Master)) | (targetPosition.m_LaneData.m_Flags & ~(LaneFlags.Slave | LaneFlags.Master)));
                        prefabRef.m_Prefab = targetPosition.m_LaneData.m_Lane;
                    }
                    if ((laneFlags & (LaneFlags.Road | LaneFlags.PublicOnly)) == (LaneFlags.Road | LaneFlags.PublicOnly) &&
                        (sourcePosition.m_LaneData.m_Flags & targetPosition.m_LaneData.m_Flags & LaneFlags.PublicOnly) == 0)
                    {
                        laneFlags &= ~LaneFlags.PublicOnly;
                        prefabRef.m_Prefab = m_CarLaneData[prefabRef.m_Prefab].m_NotBusLanePrefab;
                    }
                    if ((laneFlags & (LaneFlags.Road | LaneFlags.Track)) == (LaneFlags.Road | LaneFlags.Track) && (!CanConnectTrack(isUTurn, isRoundabout, sourcePosition, targetPosition, prefabRef) || forceRoadOnly))
                    {
                        laneFlags &= ~LaneFlags.Track;
                        prefabRef.m_Prefab = m_CarLaneData[prefabRef.m_Prefab].m_NotTrackLanePrefab;
                    }
                    if ((laneFlags & LaneFlags.Pedestrian) != 0)
                    {
                        laneFlags &= ~LaneFlags.Pedestrian;
                        laneFlags |= LaneFlags.Road;
                        prefabRef.m_Prefab = m_PedestrianLaneData[prefabRef.m_Prefab].m_NotWalkLanePrefab;
                    }
                }
                CheckPrefab(ref prefabRef.m_Prefab, ref random, out Unity.Mathematics.Random outRandom, laneBuffer);
                NetLaneData netLaneData = m_NetLaneData[prefabRef.m_Prefab];
                DynamicBuffer<AuxiliaryNetLane> dynamicBuffer = default(DynamicBuffer<AuxiliaryNetLane>);
                int num3 = 0;
                if ((netLaneData.m_Flags & LaneFlags.HasAuxiliary) != 0)
                {
                    dynamicBuffer = m_PrefabAuxiliaryLanes[prefabRef.m_Prefab];
                    num3 += dynamicBuffer.Length;
                }
                float3 position = sourcePosition.m_Position;
                float3 position2 = targetPosition.m_Position;
                for (int i = 0; i <= num3; i++)
                {
                    if (i != 0)
                    {
                        AuxiliaryNetLane auxiliaryNetLane = dynamicBuffer[i - 1];
                        if (!NetCompositionHelpers.TestLaneFlags(auxiliaryNetLane, netCompositionData.m_Flags) || !NetCompositionHelpers.TestLaneFlags(auxiliaryNetLane, netCompositionData2.m_Flags) ||
                            auxiliaryNetLane.m_Spacing.x > 0.1f)
                        {
                            continue;
                        }
                        sourcePosition.m_Position = position;
                        targetPosition.m_Position = position2;
                        sourcePosition.m_Position.y += auxiliaryNetLane.m_Position.y;
                        targetPosition.m_Position.y += auxiliaryNetLane.m_Position.y;
                        prefabRef.m_Prefab = auxiliaryNetLane.m_Prefab;
                        netLaneData = m_NetLaneData[prefabRef.m_Prefab];
                        laneFlags = (netLaneData.m_Flags | auxiliaryNetLane.m_Flags);
                        if (auxiliaryNetLane.m_Position.z > 0.1f)
                        {
                            if (sourcePosition.m_Owner != owner)
                            {
                                if ((sourcePosition.m_LaneData.m_Flags & LaneFlags.HasAuxiliary) != 0)
                                {
                                    DynamicBuffer<AuxiliaryNetLane> dynamicBuffer2 = m_PrefabAuxiliaryLanes[sourcePosition.m_LaneData.m_Lane];
                                    for (int j = 0; j < dynamicBuffer2.Length; j++)
                                    {
                                        AuxiliaryNetLane auxiliaryNetLane2 = dynamicBuffer2[j];
                                        if (auxiliaryNetLane2.m_Prefab == auxiliaryNetLane.m_Prefab)
                                        {
                                            auxiliaryNetLane = auxiliaryNetLane2;
                                            break;
                                        }
                                    }
                                }
                                EdgeNodeGeometry nodeGeometry = (!sourcePosition.m_IsEnd) ? m_StartNodeGeometryData[sourcePosition.m_Owner].m_Geometry : m_EndNodeGeometryData[sourcePosition.m_Owner].m_Geometry;
                                sourcePosition.m_Position += CalculateAuxialryZOffset(sourcePosition.m_Position, sourcePosition.m_Tangent, nodeGeometry, netCompositionData, auxiliaryNetLane);
                            }
                            if (targetPosition.m_Owner != owner)
                            {
                                if ((targetPosition.m_LaneData.m_Flags & LaneFlags.HasAuxiliary) != 0)
                                {
                                    DynamicBuffer<AuxiliaryNetLane> dynamicBuffer3 = m_PrefabAuxiliaryLanes[targetPosition.m_LaneData.m_Lane];
                                    for (int k = 0; k < dynamicBuffer3.Length; k++)
                                    {
                                        AuxiliaryNetLane auxiliaryNetLane3 = dynamicBuffer3[k];
                                        if (auxiliaryNetLane3.m_Prefab == auxiliaryNetLane.m_Prefab)
                                        {
                                            auxiliaryNetLane = auxiliaryNetLane3;
                                            break;
                                        }
                                    }
                                }
                                EdgeNodeGeometry nodeGeometry2 = (!targetPosition.m_IsEnd) ? m_StartNodeGeometryData[targetPosition.m_Owner].m_Geometry : m_EndNodeGeometryData[targetPosition.m_Owner].m_Geometry;
                                targetPosition.m_Position += CalculateAuxialryZOffset(targetPosition.m_Position, targetPosition.m_Tangent, nodeGeometry2, netCompositionData2, auxiliaryNetLane);
                            }
                        }
                    }
                    NodeLane component2 = default(NodeLane);
                    if ((laneFlags & LaneFlags.Road) != 0)
                    {
                        if (m_NetLaneData.TryGetComponent(sourcePosition.m_LaneData.m_Lane, out NetLaneData componentData))
                        {
                            component2.m_WidthOffset.x = componentData.m_Width - netLaneData.m_Width;
                        }
                        if (m_NetLaneData.TryGetComponent(targetPosition.m_LaneData.m_Lane, out NetLaneData netLaneData2))
                        {
                            component2.m_WidthOffset.y = netLaneData2.m_Width - netLaneData.m_Width;
                        }
                        component2.m_Flags |= (NodeLaneFlags)((component2.m_WidthOffset.x != 0f) ? 1 : 0);
                        component2.m_Flags |= (NodeLaneFlags)((component2.m_WidthOffset.y != 0f) ? 2 : 0);
                        component2.m_Flags |= (NodeLaneFlags)(((componentData.m_Flags & LaneFlags.BicyclesOnly) != 0) ? 4 : 0);
                        component2.m_Flags |= (NodeLaneFlags)(((netLaneData2.m_Flags & LaneFlags.BicyclesOnly) != 0) ? 8 : 0);
                    }
                    Curve curve = default(Curve);
                    bool shouldSkipLane = false;
                    if (math.distance(sourcePosition.m_Position, targetPosition.m_Position) >= 0.1f)
                    {
                        if (fixedTangents)
                        {
                            curve.m_Bezier = new Bezier4x3(sourcePosition.m_Position, sourcePosition.m_Position + sourcePosition.m_Tangent, targetPosition.m_Position + targetPosition.m_Tangent,
                                targetPosition.m_Position);
                        }
                        else
                        {
                            curve.m_Bezier = NetUtils.FitCurve(sourcePosition.m_Position, sourcePosition.m_Tangent, -targetPosition.m_Tangent, targetPosition.m_Position);
                        }
                    }
                    else
                    {
                        curve.m_Bezier = NetUtils.StraightCurve(sourcePosition.m_Position, targetPosition.m_Position);
                        shouldSkipLane = true;
                    }
                    ModifyCurveHeight(ref curve.m_Bezier, sourcePosition.m_BaseHeight, targetPosition.m_BaseHeight, netCompositionData, netCompositionData2);
                    UtilityLane component3 = default(UtilityLane);
                    HangingLane component4 = default(HangingLane);
                    bool hasHangingLane = false;
                    if ((laneFlags & LaneFlags.Utility) != 0)
                    {
                        UtilityLaneData utilityLaneData = m_UtilityLaneData[prefabRef.m_Prefab];
                        if (utilityLaneData.m_Hanging != 0f)
                        {
                            curve.m_Bezier.b = math.lerp(curve.m_Bezier.a, curve.m_Bezier.d, 0.333333343f);
                            curve.m_Bezier.c = math.lerp(curve.m_Bezier.a, curve.m_Bezier.d, 2f / 3f);
                            float num4 = math.distance(curve.m_Bezier.a.xz, curve.m_Bezier.d.xz) * utilityLaneData.m_Hanging * 1.33333337f;
                            curve.m_Bezier.b.y -= num4;
                            curve.m_Bezier.c.y -= num4;
                            component4.m_Distances = 0.1f;
                            hasHangingLane = true;
                        }
                        if ((laneFlags & LaneFlags.FindAnchor) != 0)
                        {
                            component3.m_Flags |= (UtilityLaneFlags.SecondaryStartAnchor | UtilityLaneFlags.SecondaryEndAnchor);
                            component4.m_Distances = 0f;
                        }
                    }
                    if ((laneFlags & LaneFlags.Road) != 0 && isUTurn && sourcePosition.m_Owner == targetPosition.m_Owner && sourcePosition.m_LaneData.m_Carriageway != targetPosition.m_LaneData.m_Carriageway)
                    {
                        DynamicBuffer<NetCompositionPiece> dynamicBuffer4 = m_PrefabCompositionPieces[sourcePosition.m_NodeComposition];
                        float2 @float = new float2(math.distance(curve.m_Bezier.a.xz, curve.m_Bezier.b.xz), math.distance(curve.m_Bezier.d.xz, curve.m_Bezier.c.xz));
                        float2 float2 = float.MinValue;
                        for (int l = 0; l < dynamicBuffer4.Length; l++)
                        {
                            NetCompositionPiece netCompositionPiece = dynamicBuffer4[l];
                            if ((netCompositionPiece.m_PieceFlags & NetPieceFlags.BlockTraffic) != 0)
                            {
                                float2 float3 = new float2(sourcePosition.m_LaneData.m_Position.x, targetPosition.m_LaneData.m_Position.x);
                                float3 -= netCompositionPiece.m_Offset.x;
                                bool2 @bool = float3 > 0f;
                                if (@bool.x != @bool.y)
                                {
                                    float3 = math.abs(float3) - (netLaneData.m_Width + component2.m_WidthOffset);
                                    float3 = (netCompositionPiece.m_Size.z + netCompositionPiece.m_Size.x * 0.5f - float3) * 1.33333337f;
                                    float3 += math.max(0f, float3 - math.max(0f, float3.yx));
                                    float2 = math.max(float2, float3 - @float);
                                }
                            }
                        }
                        if (math.any(float2 > 0f))
                        {
                            float2 = math.max(float2, math.max(math.min(0f, -float2.yx), @float * -0.5f));
                            curve.m_Bezier.b += sourcePosition.m_Tangent * float2.x;
                            curve.m_Bezier.c += targetPosition.m_Tangent * float2.y;
                        }
                    }
                    curve.m_Length = MathUtils.Length(curve.m_Bezier);
                    bool hasTrafficLights = false;
                    CarLane component5 = default(CarLane);
                    if ((laneFlags & LaneFlags.Road) != 0)
                    {
                        component5.m_DefaultSpeedLimit = math.lerp(sourcePosition.m_CompositionData.m_SpeedLimit, targetPosition.m_CompositionData.m_SpeedLimit, 0.5f);
                        if (curviness < 0f)
                        {
                            curviness = NetUtils.CalculateCurviness(curve, m_NetLaneData[prefabRef.m_Prefab].m_Width);
                        }
                        component5.m_Curviness = curviness;
                        bool flag4 = (sourcePosition.m_CompositionData.m_RoadFlags & targetPosition.m_CompositionData.m_RoadFlags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0;
                        if (isUnsafe)
                        {
                            component5.m_Flags |= CarLaneFlags.Unsafe;
                        }
                        if (isForbidden)
                        {
                            component5.m_Flags |= CarLaneFlags.Forbidden;
                        }
                        if (isRoundabout)
                        {
                            component5.m_Flags |= CarLaneFlags.Roundabout;
                        }
                        if (isLeftLimit)
                        {
                            component5.m_Flags |= CarLaneFlags.LeftLimit;
                        }
                        if (isRightLimit)
                        {
                            component5.m_Flags |= CarLaneFlags.RightLimit;
                        }
                        if (flag4)
                        {
                            component5.m_Flags |= CarLaneFlags.Highway;
                        }
                        if ((netCompositionData.m_Flags.m_General & CompositionFlags.General.Intersection) != 0 || isRoundabout || isUTurn || sourcePosition.m_IsSideConnection)
                        {
                            component5.m_CarriagewayGroup = 0;
                            component5.m_Flags |= CarLaneFlags.ForbidPassing;
                            if (isTurn)
                            {
                                if (isGentle)
                                {
                                    component5.m_Flags |= (CarLaneFlags)(isRight ? 524288 : 262144);
                                }
                                else if (isUTurn)
                                {
                                    component5.m_Flags |= (CarLaneFlags)(isRight ? 131072 : 2);
                                }
                                else
                                {
                                    component5.m_Flags |= (CarLaneFlags)(isRight ? 32 : 16);
                                }
                            }
                            else
                            {
                                component5.m_Flags |= CarLaneFlags.Forward;
                            }
                        }
                        else
                        {
                            if (sourcePosition.m_Owner.Index >= targetPosition.m_Owner.Index)
                            {
                                component5.m_CarriagewayGroup = targetPosition.m_LaneData.m_Carriageway;
                            }
                            else
                            {
                                component5.m_CarriagewayGroup = sourcePosition.m_LaneData.m_Carriageway;
                            }
                            if (flag4)
                            {
                                if (m_PrefabCompositionData.HasComponent(sourcePosition.m_EdgeComposition) &&
                                    (m_PrefabCompositionData[sourcePosition.m_EdgeComposition].m_State & (CompositionState.HasForwardRoadLanes | CompositionState.HasBackwardRoadLanes | CompositionState.Multilane)) ==
                                    (CompositionState.HasForwardRoadLanes | CompositionState.HasBackwardRoadLanes | CompositionState.Multilane))
                                {
                                    component5.m_Flags |= CarLaneFlags.ForbidPassing;
                                }
                                if (m_PrefabCompositionData.HasComponent(targetPosition.m_EdgeComposition) &&
                                    (m_PrefabCompositionData[targetPosition.m_EdgeComposition].m_State & (CompositionState.HasForwardRoadLanes | CompositionState.HasBackwardRoadLanes | CompositionState.Multilane)) ==
                                    (CompositionState.HasForwardRoadLanes | CompositionState.HasBackwardRoadLanes | CompositionState.Multilane))
                                {
                                    component5.m_Flags |= CarLaneFlags.ForbidPassing;
                                }
                            }
                            if (component5.m_Curviness > math.select(math.PI / 180f, math.PI / 360f, flag4))
                            {
                                component5.m_Flags |= CarLaneFlags.ForbidPassing;
                            }
                        }
                        if ((sourcePosition.m_LaneData.m_Flags & targetPosition.m_LaneData.m_Flags & LaneFlags.Twoway) != 0)
                        {
                            if (sourcePosition.m_Owner.Index >= targetPosition.m_Owner.Index)
                            {
                                return false;
                            }
                            component5.m_Flags |= CarLaneFlags.Twoway;
                        }
                        if ((sourcePosition.m_LaneData.m_Flags & targetPosition.m_LaneData.m_Flags & LaneFlags.PublicOnly) != 0)
                        {
                            component5.m_Flags |= CarLaneFlags.PublicOnly;
                        }
                        if ((sourcePosition.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                        {
                            component5.m_Flags |= CarLaneFlags.SecondaryStart;
                        }
                        if ((targetPosition.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                        {
                            component5.m_Flags |= CarLaneFlags.SecondaryEnd;
                        }
                        if ((sourcePosition.m_CompositionData.m_TaxiwayFlags & targetPosition.m_CompositionData.m_TaxiwayFlags & TaxiwayFlags.Runway) != 0)
                        {
                            component5.m_Flags |= CarLaneFlags.Runway;
                        }
                        switch (yield)
                        {
                            case 1:
                                component5.m_Flags |= CarLaneFlags.Yield;
                                shouldSkipLane = false;
                                break;
                            case 2:
                                component5.m_Flags |= CarLaneFlags.Stop;
                                shouldSkipLane = false;
                                break;
                            case -1:
                                component5.m_Flags |= CarLaneFlags.RightOfWay;
                                shouldSkipLane = false;
                                break;
                        }
                        if ((netCompositionData.m_Flags.m_General & CompositionFlags.General.TrafficLights) != 0)
                        {
                            component5.m_Flags |= CarLaneFlags.TrafficLights;
                            hasTrafficLights = true;
                            shouldSkipLane = false;
                        }
                        if ((netCompositionData.m_Flags.m_General & CompositionFlags.General.LevelCrossing) != 0)
                        {
                            component5.m_Flags |= CarLaneFlags.LevelCrossing;
                            hasTrafficLights = true;
                            shouldSkipLane = false;
                        }
                    }
                    TrackLane component6 = default(TrackLane);
                    if ((laneFlags & LaneFlags.Track) != 0)
                    {
                        component6.m_SpeedLimit = math.lerp(sourcePosition.m_CompositionData.m_SpeedLimit, targetPosition.m_CompositionData.m_SpeedLimit, 0.5f);
                        if (curviness < 0f)
                        {
                            curviness = NetUtils.CalculateCurviness(curve, m_NetLaneData[prefabRef.m_Prefab].m_Width);
                        }
                        component6.m_Curviness = curviness;
                        if (component6.m_Curviness > 1E-06f && m_TrackLaneData.TryGetComponent(prefabRef.m_Prefab, out TrackLaneData componentData))
                        {
                            component6.m_Curviness = math.min(component6.m_Curviness, componentData.m_MaxCurviness);
                        }
                        if ((sourcePosition.m_LaneData.m_Flags & targetPosition.m_LaneData.m_Flags & LaneFlags.Twoway) != 0)
                        {
                            bool num5 = sourcePosition.m_IsEnd == ((sourcePosition.m_LaneData.m_Flags & LaneFlags.Invert) == 0);
                            bool flag5 = targetPosition.m_IsEnd == ((targetPosition.m_LaneData.m_Flags & LaneFlags.Invert) == 0);
                            if (num5 != flag5)
                            {
                                if (flag5)
                                {
                                    return false;
                                }
                            }
                            else if (sourcePosition.m_Owner.Index >= targetPosition.m_Owner.Index)
                            {
                                return false;
                            }
                        }
                        if (((sourcePosition.m_LaneData.m_Flags | targetPosition.m_LaneData.m_Flags) & LaneFlags.Twoway) != 0)
                        {
                            component6.m_Flags |= TrackLaneFlags.Twoway;
                        }
                        if ((laneFlags & LaneFlags.Road) == 0)
                        {
                            component6.m_Flags |= TrackLaneFlags.Exclusive;
                        }
                        if ((netCompositionData.m_Flags.m_General & CompositionFlags.General.TrafficLights) != 0)
                        {
                            hasTrafficLights = true;
                            shouldSkipLane = false;
                        }
                        if ((netCompositionData.m_Flags.m_General & CompositionFlags.General.LevelCrossing) != 0)
                        {
                            component6.m_Flags |= TrackLaneFlags.LevelCrossing;
                            hasTrafficLights = true;
                            shouldSkipLane = false;
                        }
                        if (((netCompositionData.m_Flags.m_Left | netCompositionData.m_Flags.m_Right | netCompositionData2.m_Flags.m_Left | netCompositionData2.m_Flags.m_Right) & (CompositionFlags.Side.PrimaryStop | CompositionFlags.Side.SecondaryStop)) != 0)
                        {
                            component6.m_Flags |= TrackLaneFlags.Station;
                        }
                        if ((netCompositionData.m_Flags.m_General & CompositionFlags.General.Intersection) != 0 && isTurn)
                        {
                            component6.m_Flags |= (TrackLaneFlags)(isRight ? 8192/*TurnRight*/ : 4096/*TurnLeft*/);
                        }
                    }
                    Lane lane = default(Lane);
                    if (i != 0)
                    {
                        lane.m_StartNode = new PathNode(owner, (ushort)nodeLaneIndex++);
                        lane.m_MiddleNode = new PathNode(owner, (ushort)nodeLaneIndex++);
                        lane.m_EndNode = new PathNode(owner, (ushort)nodeLaneIndex++);
                        shouldSkipLane = false;
                    }
                    else
                    {
                        lane.m_StartNode = new PathNode(sourcePosition.m_Owner, sourcePosition.m_LaneData.m_Index, sourcePosition.m_SegmentIndex);
                        lane.m_MiddleNode = new PathNode(owner, (ushort)nodeLaneIndex++);
                        lane.m_EndNode = new PathNode(targetPosition.m_Owner, targetPosition.m_LaneData.m_Index, targetPosition.m_SegmentIndex);
                    }
                    if ((component5.m_Flags & CarLaneFlags.Unsafe) == 0)
                    {
                        for (int m = 0; m < middleConnections.Length; m++)
                        {
                            MiddleConnection value = middleConnections[m];
                            if (value.m_ConnectPosition.m_RoadTypes == RoadTypes.None || (isRoundabout && roadPassThrough != 0 && ((sourcePosition.m_Owner == owner && !value.m_IsSource) || (targetPosition.m_Owner == owner && value.m_IsSource))))
                            {
                                continue;
                            }
                            LaneFlags laneFlags2 = laneFlags;
                            if ((value.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Master) != 0)
                            {
                                if ((laneFlags & LaneFlags.Slave) != 0)
                                {
                                    continue;
                                }
                                laneFlags2 |= LaneFlags.Master;
                            }
                            else if ((value.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Slave) != 0)
                            {
                                if ((laneFlags & LaneFlags.Master) != 0)
                                {
                                    continue;
                                }
                                laneFlags2 |= LaneFlags.Slave;
                            }
                            uint num6;
                            uint num7;
                            if (isRoundabout)
                            {
                                num6 = (group | (group << 16));
                                num7 = uint.MaxValue;
                            }
                            else if ((laneFlags & LaneFlags.Master) != 0)
                            {
                                num6 = group;
                                num7 = uint.MaxValue;
                            }
                            else if (value.m_IsSource)
                            {
                                num6 = group;
                                num7 = 4294901760u;
                            }
                            else
                            {
                                num6 = group;
                                num7 = 65535u;
                            }
                            int num8 = m;
                            if (value.m_TargetLane != Entity.Null)
                            {
                                value.m_Distance = float.MaxValue;
                                num8 = -1;
                                for (; m < middleConnections.Length; m++)
                                {
                                    MiddleConnection middleConnection = middleConnections[m];
                                    if (middleConnection.m_SortIndex != value.m_SortIndex)
                                    {
                                        break;
                                    }
                                    if (((middleConnection.m_TargetGroup ^ num6) & num7) == 0 &&
                                        (((middleConnection.m_TargetFlags ^ laneFlags2) & (LaneFlags.Master | LaneFlags.BicyclesOnly)) == 0 || (middleConnection.m_TargetFlags & laneFlags2 & LaneFlags.Master) != 0))
                                    {
                                        value = middleConnection;
                                        num8 = m;
                                    }
                                }
                                m--;
                            }
                            float num9 = math.length(MathUtils.Size(MathUtils.Bounds(curve.m_Bezier) | value.m_ConnectPosition.m_Position));
                            float num10 = MathUtils.Distance(curve.m_Bezier, new Line3.Segment(value.m_ConnectPosition.m_Position, value.m_ConnectPosition.m_Position + value.m_ConnectPosition.m_Tangent * num9), out float2 t);
                            num10 += num9 * t.y;
                            if (roadPassThrough != 0)
                            {
                                if (value.m_IsSource)
                                {
                                    t.x = math.lerp(t.x, 1f, 0.5f);
                                }
                                else
                                {
                                    t.x = math.lerp(0f, t.x, 0.5f);
                                }
                            }
                            if (num10 < value.m_Distance)
                            {
                                value.m_Distance = num10;
                                value.m_TargetLane = prefabRef.m_Prefab;
                                value.m_TargetOwner = (value.m_IsSource ? sourcePosition.m_Owner : targetPosition.m_Owner);
                                value.m_TargetGroup = num6;
                                value.m_TargetNode = lane.m_MiddleNode;
                                value.m_TargetCarriageway = component5.m_CarriagewayGroup;
                                value.m_TargetComposition = (value.m_IsSource ? targetPosition.m_CompositionData : sourcePosition.m_CompositionData);
                                value.m_TargetCurve = curve;
                                value.m_TargetCurvePos = t.x;
                                value.m_TargetFlags = laneFlags2;
                                if (num8 != -1)
                                {
                                    middleConnections[num8] = value;
                                }
                                else
                                {
                                    CollectionUtils.Insert(middleConnections, ++m, value);
                                }
                            }
                        }
                    }
                    if ((laneFlags & LaneFlags.Master) != 0)
                    {
                        shouldSkipLane = isSkipped;
                    }
                    else if (i == 0)
                    {
                        isSkipped |= shouldSkipLane;
                    }
                    if (shouldSkipLane)
                    {
                        if (isTemp)
                        {
                            lane.m_StartNode = new PathNode(lane.m_StartNode, secondaryNode: true);
                            lane.m_MiddleNode = new PathNode(lane.m_MiddleNode, secondaryNode: true);
                            lane.m_EndNode = new PathNode(lane.m_EndNode, secondaryNode: true);
                        }
                        m_SkipLaneQueue.Enqueue(lane);
                        continue;
                    }
                    LaneKey laneKey = new LaneKey(lane, prefabRef.m_Prefab, laneFlags);
                    LaneKey laneKey2 = laneKey;
                    if (isTemp)
                    {
                        ReplaceTempOwner(ref laneKey2, owner);
                        ReplaceTempOwner(ref laneKey2, sourcePosition.m_Owner);
                        ReplaceTempOwner(ref laneKey2, targetPosition.m_Owner);
                        GetOriginalLane(laneBuffer, laneKey2, ref temp);
                    }
                    PseudoRandomSeed componentData2 = default(PseudoRandomSeed);
                    if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0 && !m_PseudoRandomSeedData.TryGetComponent(temp.m_Original, out componentData2))
                    {
                        componentData2 = new PseudoRandomSeed(ref outRandom);
                    }
                    if (laneBuffer.m_OldLanes.TryGetValue(laneKey, out Entity item))
                    {
                        laneBuffer.m_OldLanes.Remove(laneKey);
                        m_CommandBuffer.SetComponent(jobIndex, item, lane);
                        m_CommandBuffer.SetComponent(jobIndex, item, component2);
                        m_CommandBuffer.SetComponent(jobIndex, item, curve);
                        if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
                        {
                            if (!m_PseudoRandomSeedData.HasComponent(item))
                            {
                                m_CommandBuffer.AddComponent(jobIndex, item, componentData2);
                            }
                            else
                            {
                                m_CommandBuffer.SetComponent(jobIndex, item, componentData2);
                            }
                        }
                        if ((laneFlags & LaneFlags.Road) != 0)
                        {
                            m_CommandBuffer.SetComponent(jobIndex, item, component5);
                        }
                        if ((laneFlags & LaneFlags.Track) != 0)
                        {
                            m_CommandBuffer.SetComponent(jobIndex, item, component6);
                        }
                        if ((laneFlags & LaneFlags.Utility) != 0)
                        {
                            m_CommandBuffer.SetComponent(jobIndex, item, component3);
                        }
                        if (hasHangingLane)
                        {
                            m_CommandBuffer.AddComponent(jobIndex, item, component4);
                        }
                        if (isTemp)
                        {
                            laneBuffer.m_Updates.Add(in item);
                            m_CommandBuffer.SetComponent(jobIndex, item, temp);
                        }
                        else if (m_TempData.HasComponent(item))
                        {
                            m_CommandBuffer.RemoveComponent(jobIndex, item, in m_DeletedTempTypes);
                            m_CommandBuffer.AddComponent(jobIndex, item, in m_AppliedTypes);
                        }
                        else
                        {
                            laneBuffer.m_Updates.Add(in item);
                        }
                        if ((laneFlags & LaneFlags.Master) != 0)
                        {
                            MasterLane component7 = default(MasterLane);
                            component7.m_Group = group;
                            m_CommandBuffer.SetComponent(jobIndex, item, component7);
                        }
                        if ((laneFlags & LaneFlags.Slave) != 0)
                        {
                            SlaveLane component8 = default(SlaveLane);
                            component8.m_Group = group;
                            component8.m_MinIndex = laneIndex;
                            component8.m_MaxIndex = laneIndex;
                            component8.m_SubIndex = laneIndex;
                            if (isMergeLeft)
                            {
                                component8.m_Flags |= SlaveLaneFlags.MergingLane;
                            }
                            if (isMergeRight)
                            {
                                component8.m_Flags |= SlaveLaneFlags.MergingLane;
                            }
                            m_CommandBuffer.SetComponent(jobIndex, item, component8);
                        }
                        if (hasTrafficLights)
                        {
                            if (!m_LaneSignalData.HasComponent(item))
                            {
                                m_CommandBuffer.AddComponent(jobIndex, item, default(LaneSignal));
                            }
                        }
                        else if (m_LaneSignalData.HasComponent(item))
                        {
                            m_CommandBuffer.RemoveComponent<LaneSignal>(jobIndex, item);
                        }
                        continue;
                    }
                    NetLaneArchetypeData netLaneArchetypeData = m_PrefabLaneArchetypeData[prefabRef.m_Prefab];
                    EntityArchetype entityArchetype = (((laneFlags & LaneFlags.Slave) != 0)
                        ? netLaneArchetypeData.m_NodeSlaveArchetype
                        : (((laneFlags & LaneFlags.Master) == 0) ? netLaneArchetypeData.m_NodeLaneArchetype : netLaneArchetypeData.m_NodeMasterArchetype));
                    Entity e = m_CommandBuffer.CreateEntity(jobIndex, entityArchetype);
                    if (((netCompositionData.m_State | netCompositionData2.m_State) & CompositionState.Hidden) != 0)
                    {
                        m_CommandBuffer.RemoveComponent(jobIndex, e, in m_HideLaneTypes);
                    }
                    m_CommandBuffer.SetComponent(jobIndex, e, prefabRef);
                    m_CommandBuffer.SetComponent(jobIndex, e, lane);
                    m_CommandBuffer.SetComponent(jobIndex, e, component2);
                    m_CommandBuffer.SetComponent(jobIndex, e, curve);
                    if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, componentData2);
                    }
                    if ((laneFlags & LaneFlags.Road) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component5);
                    }
                    if ((laneFlags & LaneFlags.Track) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component6);
                    }
                    if ((laneFlags & LaneFlags.Utility) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component3);
                    }
                    if (hasHangingLane)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component4);
                    }
                    if ((laneFlags & LaneFlags.Master) != 0)
                    {
                        MasterLane component9 = default(MasterLane);
                        component9.m_Group = group;
                        m_CommandBuffer.SetComponent(jobIndex, e, component9);
                    }
                    if ((laneFlags & LaneFlags.Slave) != 0)
                    {
                        SlaveLane component10 = default(SlaveLane);
                        component10.m_Group = group;
                        component10.m_MinIndex = laneIndex;
                        component10.m_MaxIndex = laneIndex;
                        component10.m_SubIndex = laneIndex;
                        if (isMergeLeft)
                        {
                            component10.m_Flags |= SlaveLaneFlags.MergingLane;
                        }
                        if (isMergeRight)
                        {
                            component10.m_Flags |= SlaveLaneFlags.MergingLane;
                        }
                        m_CommandBuffer.SetComponent(jobIndex, e, component10);
                    }
                    if (isTemp)
                    {
                        m_CommandBuffer.AddComponent(jobIndex, e, in m_TempOwnerTypes);
                        m_CommandBuffer.SetComponent(jobIndex, e, component);
                        m_CommandBuffer.SetComponent(jobIndex, e, temp);
                    }
                    else
                    {
                        m_CommandBuffer.AddComponent(jobIndex, e, component);
                    }
                    if (hasTrafficLights)
                    {
                        m_CommandBuffer.AddComponent(jobIndex, e, default(LaneSignal));
                    }
                }
                return true;
            }

            private float3 CalculateUtilityConnectPosition(NativeList<ConnectPosition> buffer, int2 bufferRange) {
                float4 lhs = default(float4);
                for (int i = bufferRange.x + 1; i < bufferRange.y; i++)
                {
                    ConnectPosition connectPosition = buffer[i];
                    float3 tangent = connectPosition.m_Tangent;
                    if (!(math.lengthsq(tangent.xz) > 0.01f))
                    {
                        continue;
                    }
                    float3 @float;
                    if (!m_NodeData.HasComponent(connectPosition.m_Owner))
                    {
                        @float = ((connectPosition.m_SegmentIndex == 0) ? m_StartNodeGeometryData[connectPosition.m_Owner].m_Geometry.m_Middle.d : m_EndNodeGeometryData[connectPosition.m_Owner].m_Geometry.m_Middle.d);
                    }
                    else
                    {
                        @float = m_NodeData[connectPosition.m_Owner].m_Position;
                        if (m_NodeGeometryData.TryGetComponent(connectPosition.m_Owner, out NodeGeometry componentData))
                        {
                            @float.y = componentData.m_Position;
                        }
                    }
                    for (int j = bufferRange.x; j < i; j++)
                    {
                        ConnectPosition connectPosition2 = buffer[j];
                        float3 tangent2 = connectPosition2.m_Tangent;
                        if (math.lengthsq(tangent2.xz) > 0.01f)
                        {
                            float num = math.dot(tangent.xz, tangent2.xz);
                            float2 float2 = math.distance(connectPosition.m_Position.xz, connectPosition2.m_Position.xz) * new float2(math.max(0f, num * 0.5f), 1f - math.abs(num) * 0.5f);
                            Line2.Segment segment = new Line2.Segment(connectPosition.m_Position.xz + tangent.xz * float2.x, connectPosition.m_Position.xz + tangent.xz * float2.y);
                            Line2.Segment segment2 = new Line2.Segment(connectPosition2.m_Position.xz + tangent2.xz * float2.x, connectPosition2.m_Position.xz + tangent2.xz * float2.y);
                            MathUtils.Distance(segment, segment2, out float2 t);
                            float4 lhs2 = default(float4);
                            lhs2.y = @float.y + connectPosition.m_Position.y - connectPosition.m_BaseHeight + @float.y + connectPosition2.m_Position.y - connectPosition2.m_BaseHeight;
                            lhs2.xz = MathUtils.Position(segment, t.x) + MathUtils.Position(segment2, t.y);
                            lhs2.w = 2f;
                            float rhs = 1.01f - math.abs(num);
                            lhs += lhs2 * rhs;
                        }
                    }
                }
                if (lhs.w == 0f)
                {
                    return buffer[bufferRange.x].m_Position;
                }
                return (lhs / lhs.w).xyz;
            }

            private void CreateNodeUtilityLanes(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, NativeList<ConnectPosition> buffer1, NativeList<ConnectPosition> buffer2,
                NativeList<MiddleConnection> middleConnections, float3 middlePosition, bool isRoundabout, bool isTemp, Temp ownerTemp)
            {
                if (buffer1.Length >= 2)
                {
                    buffer1.Sort(default(SourcePositionComparer));
                    Entity rhs = Entity.Null;
                    int num = 0;
                    int num2 = 0;
                    int num3 = 0;
                    for (int i = 0; i < buffer1.Length; i++)
                    {
                        ConnectPosition connectPosition = buffer1[i];
                        if (connectPosition.m_Owner != rhs)
                        {
                            if (i - num > num2)
                            {
                                num3 = num;
                                num2 = i - num;
                            }
                            rhs = connectPosition.m_Owner;
                            num = i;
                        }
                    }
                    if (buffer1.Length - num > num2)
                    {
                        num3 = num;
                        num2 = buffer1.Length - num;
                    }
                    for (int j = 0; j < num2; j++)
                    {
                        ConnectPosition value = buffer1[num3 + j];
                        value.m_Order = j;
                        buffer1[num3 + j] = buffer1[j];
                        buffer1[j] = value;
                    }
                    for (int k = num2; k < buffer1.Length; k++)
                    {
                        ConnectPosition value2 = buffer1[k];
                        float num4 = float.MaxValue;
                        int num5 = 0;
                        for (int l = 0; l < num2; l++)
                        {
                            ConnectPosition connectPosition2 = buffer1[l];
                            float num6 = math.distancesq(value2.m_Position, connectPosition2.m_Position);
                            if (num6 < num4)
                            {
                                num4 = num6;
                                num5 = l;
                            }
                        }
                        value2.m_Order = num5;
                        buffer1[k] = value2;
                    }
                    buffer1.Sort(default(TargetPositionComparer));
                    float num7 = -1f;
                    num = 0;
                    for (int m = 0; m < buffer1.Length; m++)
                    {
                        ConnectPosition connectPosition3 = buffer1[m];
                        if (connectPosition3.m_Order != num7)
                        {
                            if (m - num > 0)
                            {
                                CreateNodeUtilityLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, buffer1, buffer2, middleConnections, new int2(num, m), middlePosition, isRoundabout, isTemp, ownerTemp);
                            }
                            num7 = connectPosition3.m_Order;
                            num = m;
                        }
                    }
                    if (buffer1.Length > num)
                    {
                        CreateNodeUtilityLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, buffer1, buffer2, middleConnections, new int2(num, buffer1.Length), middlePosition, isRoundabout, isTemp, ownerTemp);
                    }
                }
                else
                {
                    CreateNodeUtilityLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, buffer1, buffer2, middleConnections, new int2(0, buffer1.Length), middlePosition, isRoundabout, isTemp, ownerTemp);
                }
                if (buffer1.Length <= 0)
                {
                    return;
                }
                ConnectPosition connectPosition4 = buffer1[0];
                for (int n = 0; n < middleConnections.Length; n++)
                {
                    MiddleConnection value3 = middleConnections[n];
                    if ((value3.m_ConnectPosition.m_UtilityTypes & connectPosition4.m_UtilityTypes) != 0 && value3.m_SourceEdge == connectPosition4.m_Owner && value3.m_TargetLane == Entity.Null)
                    {
                        float num8 = math.distance(connectPosition4.m_Position, value3.m_ConnectPosition.m_Position);
                        if (num8 < value3.m_Distance)
                        {
                            value3.m_Distance = num8;
                            value3.m_TargetLane = connectPosition4.m_LaneData.m_Lane;
                            value3.m_TargetNode = new PathNode(connectPosition4.m_Owner, connectPosition4.m_LaneData.m_Index, connectPosition4.m_SegmentIndex);
                            value3.m_TargetCurve = new Curve
                            {
                                m_Bezier = new Bezier4x3(connectPosition4.m_Position, connectPosition4.m_Position, connectPosition4.m_Position, connectPosition4.m_Position)
                            };
                            value3.m_TargetCurvePos = 0f;
                            value3.m_TargetFlags = connectPosition4.m_LaneData.m_Flags;
                            middleConnections[n] = value3;
                        }
                    }
                }
            }

            private void CreateNodeUtilityLanes(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, NativeList<ConnectPosition> buffer1, NativeList<ConnectPosition> buffer2,
                NativeList<MiddleConnection> middleConnections, int2 bufferRange, float3 middlePosition, bool isRoundabout, bool isTemp, Temp ownerTemp)
            {
                int num = bufferRange.y - bufferRange.x;
                if (num == 2 && buffer2.Length == 0)
                {
                    ConnectPosition connectPosition = buffer1[bufferRange.x];
                    ConnectPosition connectPosition2 = buffer1[bufferRange.x + 1];
                    if (connectPosition.m_LaneData.m_Lane == connectPosition2.m_LaneData.m_Lane)
                    {
                        CreateNodeUtilityLane(jobIndex, -1, ref nodeLaneIndex, ref random, owner, laneBuffer, connectPosition, connectPosition2, middleConnections, isTemp, ownerTemp);
                        return;
                    }
                }
                if (num < 2 && (num <= 0 || buffer2.Length <= 0) && !(num == 1 && isRoundabout))
                {
                    return;
                }
                float3 position;
                if (num >= 2)
                {
                    position = CalculateUtilityConnectPosition(buffer1, bufferRange);
                }
                else if (isRoundabout)
                {
                    float3 position2 = buffer1[bufferRange.x].m_Position;
                    float3 tangent = buffer1[bufferRange.x].m_Tangent;
                    position = position2 + tangent * math.dot(tangent, middlePosition - position2);
                    position.y = middlePosition.y;
                    position.y = middlePosition.y + position2.y - buffer1[bufferRange.x].m_BaseHeight;
                }
                else
                {
                    position = buffer1[bufferRange.x].m_Position;
                }
                int endNodeLaneIndex = nodeLaneIndex++;
                if (num >= 2 || isRoundabout)
                {
                    for (int i = bufferRange.x; i < bufferRange.y; i++)
                    {
                        ConnectPosition connectPosition3 = buffer1[i];
                        ConnectPosition connectPosition4 = default(ConnectPosition);
                        connectPosition4.m_Position = position;
                        CreateNodeUtilityLane(jobIndex, endNodeLaneIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, connectPosition3, connectPosition4, middleConnections, isTemp, ownerTemp);
                    }
                }
                foreach (ConnectPosition connectPosition5 in buffer2)
                {
                    ConnectPosition connectPosition6 = default(ConnectPosition);
                    connectPosition6.m_Position = position;
                    CreateNodeUtilityLane(jobIndex, endNodeLaneIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, connectPosition5, connectPosition6, middleConnections, isTemp, ownerTemp);
                }
            }

            private void CreateNodeUtilityLane(int jobIndex, int endNodeLaneIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, ConnectPosition connectPosition1,
                ConnectPosition connectPosition2, NativeList<MiddleConnection> middleConnections, bool isTemp, Temp ownerTemp
            ) {
                Owner component = default(Owner);
                component.m_Owner = owner;
                Temp temp = default(Temp);
                if (isTemp)
                {
                    temp.m_Flags = (ownerTemp.m_Flags & (TempFlags.Create | TempFlags.Delete | TempFlags.Select | TempFlags.Modify | TempFlags.Hidden));
                    if ((ownerTemp.m_Flags & TempFlags.Replace) != 0)
                    {
                        temp.m_Flags |= TempFlags.Modify;
                    }
                }
                Lane lane = default(Lane);
                if (connectPosition1.m_Owner != Entity.Null)
                {
                    lane.m_StartNode = new PathNode(connectPosition1.m_Owner, connectPosition1.m_LaneData.m_Index, connectPosition1.m_SegmentIndex);
                }
                else
                {
                    lane.m_StartNode = new PathNode(owner, (ushort)nodeLaneIndex++);
                }
                lane.m_MiddleNode = new PathNode(owner, (ushort)nodeLaneIndex++);
                if (connectPosition2.m_Owner != Entity.Null)
                {
                    lane.m_EndNode = new PathNode(connectPosition2.m_Owner, connectPosition2.m_LaneData.m_Index, connectPosition2.m_SegmentIndex);
                }
                else
                {
                    lane.m_EndNode = new PathNode(owner, (ushort)endNodeLaneIndex);
                    float2 value = connectPosition2.m_Position.xz - connectPosition1.m_Position.xz;
                    if (MathUtils.TryNormalize(ref value))
                    {
                        connectPosition2.m_Tangent.xz = math.reflect(connectPosition1.m_Tangent.xz, value);
                    }
                }
                PrefabRef component2 = default(PrefabRef);
                component2.m_Prefab = connectPosition1.m_LaneData.m_Lane;
                CheckPrefab(ref component2.m_Prefab, ref random, out Unity.Mathematics.Random outRandom, laneBuffer);
                NetLaneData netLaneData = m_NetLaneData[component2.m_Prefab];
                bool flag = math.distance(connectPosition1.m_Position, connectPosition2.m_Position) < 0.1f;
                Curve curve = default(Curve);
                if (connectPosition1.m_Tangent.Equals(default(float3)))
                {
                    if (math.abs(connectPosition1.m_Position.y - connectPosition2.m_Position.y) >= 0.01f)
                    {
                        curve.m_Bezier.a = connectPosition1.m_Position;
                        curve.m_Bezier.b = math.lerp(connectPosition1.m_Position, connectPosition2.m_Position, new float3(0.25f, 0.5f, 0.25f));
                        curve.m_Bezier.c = math.lerp(connectPosition1.m_Position, connectPosition2.m_Position, new float3(0.75f, 0.5f, 0.75f));
                        curve.m_Bezier.d = connectPosition2.m_Position;
                    }
                    else
                    {
                        curve.m_Bezier = NetUtils.StraightCurve(connectPosition1.m_Position, connectPosition2.m_Position);
                    }
                }
                else
                {
                    curve.m_Bezier = NetUtils.FitCurve(connectPosition1.m_Position, connectPosition1.m_Tangent, -connectPosition2.m_Tangent, connectPosition2.m_Position);
                }
                curve.m_Length = MathUtils.Length(curve.m_Bezier);
                for (int i = 0; i < middleConnections.Length; i++)
                {
                    MiddleConnection value2 = middleConnections[i];
                    if ((value2.m_ConnectPosition.m_UtilityTypes & connectPosition1.m_UtilityTypes) != 0 && (value2.m_SourceEdge == connectPosition1.m_Owner || value2.m_SourceEdge == connectPosition2.m_Owner))
                    {
                        if (value2.m_TargetLane != Entity.Null && ((value2.m_TargetFlags ^ value2.m_ConnectPosition.m_LaneData.m_Flags) & LaneFlags.Underground) != 0 &&
                            ((connectPosition1.m_LaneData.m_Flags ^ value2.m_ConnectPosition.m_LaneData.m_Flags) & LaneFlags.Underground) == 0)
                        {
                            value2.m_Distance = float.MaxValue;
                        }
                        float t;
                        float num = MathUtils.Distance(curve.m_Bezier, value2.m_ConnectPosition.m_Position, out t);
                        if (num < value2.m_Distance)
                        {
                            value2.m_Distance = num;
                            value2.m_TargetLane = connectPosition1.m_LaneData.m_Lane;
                            value2.m_TargetNode = lane.m_MiddleNode;
                            value2.m_TargetCurve = curve;
                            value2.m_TargetCurvePos = t;
                            value2.m_TargetFlags = connectPosition1.m_LaneData.m_Flags;
                            middleConnections[i] = value2;
                        }
                    }
                }
                if (flag)
                {
                    return;
                }
                LaneKey laneKey = new LaneKey(lane, component2.m_Prefab, (LaneFlags)0);
                LaneKey laneKey2 = laneKey;
                if (isTemp)
                {
                    ReplaceTempOwner(ref laneKey2, owner);
                    ReplaceTempOwner(ref laneKey2, connectPosition1.m_Owner);
                    ReplaceTempOwner(ref laneKey2, connectPosition2.m_Owner);
                    GetOriginalLane(laneBuffer, laneKey2, ref temp);
                }
                PseudoRandomSeed componentData = default(PseudoRandomSeed);
                if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0 && !m_PseudoRandomSeedData.TryGetComponent(temp.m_Original, out componentData))
                {
                    componentData = new PseudoRandomSeed(ref outRandom);
                }
                if (laneBuffer.m_OldLanes.TryGetValue(laneKey, out Entity item))
                {
                    laneBuffer.m_OldLanes.Remove(laneKey);
                    m_CommandBuffer.SetComponent(jobIndex, item, lane);
                    m_CommandBuffer.SetComponent(jobIndex, item, curve);
                    if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
                    {
                        if (!m_PseudoRandomSeedData.HasComponent(item))
                        {
                            m_CommandBuffer.AddComponent(jobIndex, item, componentData);
                        }
                        else
                        {
                            m_CommandBuffer.SetComponent(jobIndex, item, componentData);
                        }
                    }
                    if (isTemp)
                    {
                        laneBuffer.m_Updates.Add(in item);
                        m_CommandBuffer.SetComponent(jobIndex, item, temp);
                    }
                    else if (m_TempData.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent(jobIndex, item, in m_DeletedTempTypes);
                        m_CommandBuffer.AddComponent(jobIndex, item, in m_AppliedTypes);
                    }
                    else
                    {
                        laneBuffer.m_Updates.Add(in item);
                    }
                }
                else
                {
                    EntityArchetype nodeLaneArchetype = m_PrefabLaneArchetypeData[component2.m_Prefab].m_NodeLaneArchetype;
                    Entity e = m_CommandBuffer.CreateEntity(jobIndex, nodeLaneArchetype);
                    m_CommandBuffer.SetComponent(jobIndex, e, component2);
                    m_CommandBuffer.SetComponent(jobIndex, e, lane);
                    m_CommandBuffer.SetComponent(jobIndex, e, curve);
                    if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, componentData);
                    }
                    if (isTemp)
                    {
                        m_CommandBuffer.AddComponent(jobIndex, e, in m_TempOwnerTypes);
                        m_CommandBuffer.SetComponent(jobIndex, e, component);
                        m_CommandBuffer.SetComponent(jobIndex, e, temp);
                    }
                    else
                    {
                        m_CommandBuffer.AddComponent(jobIndex, e, component);
                    }
                }
            }

            private void CreateNodePedestrianLanes(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, NativeList<ConnectPosition> buffer, NativeList<ConnectPosition> tempBuffer, NativeList<ConnectPosition> tempBuffer2, bool isTemp, Temp ownerTemp, float3 middlePosition, float middleRadius, float roundaboutSize)
            {
                if (buffer.Length >= 2)
                {
                    buffer.Sort(default(SourcePositionComparer));
                }
                int num = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    ConnectPosition connectPosition = buffer[i];
                    num += math.select(1, 0, connectPosition.m_IsSideConnection);
                }
                bool flag = true;
                if (num == 0 || (num == 1 && middleRadius <= 0f))
                {
                    num = buffer.Length;
                    flag = false;
                }
                if (num == 0)
                {
                    return;
                }
                StackList<int2> stackList = stackalloc int2[num];
                Bounds1 bounds = new Bounds1(float.MaxValue, 0f);
                Segment segment = default(Segment);
                int num2 = -1;
                int num3 = -1;
                int num4 = 0;
                bool flag2 = false;
                bool flag3 = false;
                int num5 = 0;
                Segment left = default(Segment);
                while (num5 < buffer.Length)
                {
                    ConnectPosition connectPosition2 = buffer[num5];
                    int j;
                    for (j = num5 + 1; j < buffer.Length && !(buffer[j].m_Owner != connectPosition2.m_Owner); j++) { }

                    if (buffer[j - 1].m_IsSideConnection && flag)
                    {
                        num5 = j;
                        continue;
                    }
                    if (m_PrefabCompositionCrosswalks.TryGetBuffer(connectPosition2.m_NodeComposition, out DynamicBuffer<NetCompositionCrosswalk> bufferData))
                    {
                        if (connectPosition2.m_SegmentIndex != 0)
                        {
                            EndNodeGeometry endNodeGeometry = m_EndNodeGeometryData[connectPosition2.m_Owner];
                            if (endNodeGeometry.m_Geometry.m_MiddleRadius > 0f)
                            {
                                left = endNodeGeometry.m_Geometry.m_Left;
                            }
                            else
                            {
                                left.m_Left = endNodeGeometry.m_Geometry.m_Left.m_Left;
                                left.m_Right = endNodeGeometry.m_Geometry.m_Right.m_Right;
                                left.m_Length = new float2(endNodeGeometry.m_Geometry.m_Left.m_Length.x, endNodeGeometry.m_Geometry.m_Right.m_Length.y);
                            }
                        }
                        else
                        {
                            StartNodeGeometry startNodeGeometry = m_StartNodeGeometryData[connectPosition2.m_Owner];
                            if (startNodeGeometry.m_Geometry.m_MiddleRadius > 0f)
                            {
                                left = startNodeGeometry.m_Geometry.m_Left;
                            }
                            else
                            {
                                left.m_Left = startNodeGeometry.m_Geometry.m_Left.m_Left;
                                left.m_Right = startNodeGeometry.m_Geometry.m_Right.m_Right;
                                left.m_Length = new float2(startNodeGeometry.m_Geometry.m_Left.m_Length.x, startNodeGeometry.m_Geometry.m_Right.m_Length.y);
                            }
                        }
                        NetCompositionData netCompositionData = m_PrefabCompositionData[connectPosition2.m_NodeComposition];
                        if (bufferData.Length >= 1)
                        {
                            flag2 |= ((netCompositionData.m_Flags.m_General & CompositionFlags.General.Crosswalk) != 0);
                            float3 start = bufferData[0].m_Start;
                            float3 end = bufferData[bufferData.Length - 1].m_End;
                            float t = start.x / math.max(1f, netCompositionData.m_Width) + 0.5f;
                            float t2 = end.x / math.max(1f, netCompositionData.m_Width) + 0.5f;
                            float3 x = math.lerp(left.m_Left.a, left.m_Right.a, t);
                            end = math.lerp(left.m_Left.a, left.m_Right.a, t2);
                            float num6 = math.distance(x, end);
                            if (num6 < bounds.min)
                            {
                                num2 = num5;
                            }
                            bounds |= num6;
                        }
                        flag3 |= math.any(left.m_Length >= left.m_Length.yx * 4f + 0.1f);
                        num4++;
                    }
                    num5 = j;
                }
                bool flag4 = false;
                num5 = 0;
                Segment left2 = default(Segment);
                while (num5 < buffer.Length)
                {
                    ConnectPosition targetPosition = buffer[num5];
                    int k;
                    for (k = num5 + 1; k < buffer.Length && !(buffer[k].m_Owner != targetPosition.m_Owner); k++) { }
                    ConnectPosition connectPosition3 = buffer[k - 1];
                    if (connectPosition3.m_IsSideConnection && flag)
                    {
                        num5 = k;
                        continue;
                    }
                    int num7 = nodeLaneIndex;
                    bool flag5 = k == num5 + 1;
                    if (FindNextRightLane(connectPosition3, buffer, out int2 result))
                    {
                        ConnectPosition targetPosition2 = buffer[result.x];
                        bool flag6 = result.y == result.x + 1;
                        int num8 = 0;
                        while (targetPosition2.m_IsSideConnection && flag)
                        {
                            if (++num8 > buffer.Length)
                            {
                                tempBuffer2.Clear();
                                return;
                            }
                            for (int l = result.x; l < result.y; l++)
                            {
                                ConnectPosition value = buffer[l];
                                tempBuffer2.Add(in value);
                            }
                            int index = result.y - 1;
                            if (!FindNextRightLane(buffer[index], buffer, out result))
                            {
                                tempBuffer2.Clear();
                                return;
                            }
                            targetPosition2 = buffer[result.x];
                        }
                        if (num > 2)
                        {
                            CreateNodePedestrianLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, connectPosition3, targetPosition2, tempBuffer2, flag5, flag6, isTemp, ownerTemp, middlePosition, middleRadius, roundaboutSize);
                        }
                        else if (num == 2)
                        {
                            if (connectPosition3.m_Owner.Index == targetPosition2.m_Owner.Index)
                            {
                                CreateNodePedestrianLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, connectPosition3, targetPosition2, tempBuffer2, flag5, flag6, isTemp, ownerTemp, middlePosition, middleRadius, roundaboutSize);
                            }
                            else if (flag4 || (flag5 && flag6 && middleRadius > 0f))
                            {
                                CreateNodePedestrianLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, connectPosition3, targetPosition2, tempBuffer2, flag5, flag6, isTemp, ownerTemp, middlePosition, middleRadius, roundaboutSize);
                                flag4 = false;
                            }
                            else
                            {
                                flag4 = true;
                            }
                        }
                        if (flag5)
                        {
                            stackList.AddNoResize(new int2(num5, result.x));
                        }
                    }
                    if (num == 1 && middleRadius > 0f)
                    {
                        CreateNodePedestrianLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, connectPosition3, targetPosition, tempBuffer2, flag5, flag5, isTemp, ownerTemp, middlePosition, middleRadius, roundaboutSize);
                    }
                    if (!flag4)
                    {
                        tempBuffer2.Clear();
                    }
                    if (m_PrefabCompositionCrosswalks.TryGetBuffer(targetPosition.m_NodeComposition, out DynamicBuffer<NetCompositionCrosswalk> bufferData2))
                    {
                        if (targetPosition.m_SegmentIndex != 0)
                        {
                            EndNodeGeometry endNodeGeometry2 = m_EndNodeGeometryData[targetPosition.m_Owner];
                            if (endNodeGeometry2.m_Geometry.m_MiddleRadius > 0f)
                            {
                                left2 = endNodeGeometry2.m_Geometry.m_Left;
                            }
                            else
                            {
                                left2.m_Left = endNodeGeometry2.m_Geometry.m_Left.m_Left;
                                left2.m_Right = endNodeGeometry2.m_Geometry.m_Right.m_Right;
                                left2.m_Length = new float2(endNodeGeometry2.m_Geometry.m_Left.m_Length.x, endNodeGeometry2.m_Geometry.m_Right.m_Length.y);
                            }
                        }
                        else
                        {
                            StartNodeGeometry startNodeGeometry2 = m_StartNodeGeometryData[targetPosition.m_Owner];
                            if (startNodeGeometry2.m_Geometry.m_MiddleRadius > 0f)
                            {
                                left2 = startNodeGeometry2.m_Geometry.m_Left;
                            }
                            else
                            {
                                left2.m_Left = startNodeGeometry2.m_Geometry.m_Left.m_Left;
                                left2.m_Right = startNodeGeometry2.m_Geometry.m_Right.m_Right;
                                left2.m_Length = new float2(startNodeGeometry2.m_Geometry.m_Left.m_Length.x, startNodeGeometry2.m_Geometry.m_Right.m_Length.y);
                            }
                        }
                        NetCompositionData netCompositionData2 = m_PrefabCompositionData[targetPosition.m_NodeComposition];
                        bool flag7 = false;
                        if (num4 == 2)
                        {
                            if ((netCompositionData2.m_Flags.m_General & (CompositionFlags.General.DeadEnd | CompositionFlags.General.Intersection | CompositionFlags.General.LevelCrossing)) == 0)
                            {
                                if ((netCompositionData2.m_Flags.m_General & CompositionFlags.General.Crosswalk) != 0 || !flag3)
                                {
                                    flag7 = true;
                                }
                            }
                            else if ((netCompositionData2.m_Flags.m_General & (CompositionFlags.General.DeadEnd | CompositionFlags.General.LevelCrossing)) == 0)
                            {
                                if (!flag2 && !flag3)
                                {
                                    if (bounds.max > bounds.min + 0.1f)
                                    {
                                        if (num5 != num2)
                                        {
                                            num5 = k;
                                            continue;
                                        }
                                    }
                                    else
                                    {
                                        flag7 = true;
                                    }
                                }
                                else if (flag2 && (netCompositionData2.m_Flags.m_General & CompositionFlags.General.Crosswalk) == 0)
                                {
                                    num5 = k;
                                    continue;
                                }
                            }
                        }
                        if (flag7 && num3 == -1)
                        {
                            segment = left2;
                            num3 = num7;
                            num5 = k;
                            continue;
                        }
                        if (bufferData2.Length >= 1)
                        {
                            tempBuffer.ResizeUninitialized(bufferData2.Length + 1);
                            for (int m = 0; m < tempBuffer.Length; m++)
                            {
                                float3 @float = (m != 0) ? ((m != tempBuffer.Length - 1) ? math.lerp(bufferData2[m - 1].m_End, bufferData2[m].m_Start, 0.5f) : bufferData2[m - 1].m_End) : bufferData2[m].m_Start;
                                float t3 = @float.x / math.max(1f, netCompositionData2.m_Width) + 0.5f;
                                ConnectPosition value2 = default(ConnectPosition);
                                value2.m_Position = math.lerp(left2.m_Left.a, left2.m_Right.a, t3);
                                value2.m_Position.y += @float.y;
                                tempBuffer[m] = value2;
                            }
                            for (int n = num5; n < k; n++)
                            {
                                ConnectPosition value3 = buffer[n];
                                float num9 = float.MaxValue;
                                int index2 = 0;
                                for (int num10 = 0; num10 < tempBuffer.Length; num10++)
                                {
                                    ConnectPosition connectPosition4 = tempBuffer[num10];
                                    if (connectPosition4.m_Owner == Entity.Null)
                                    {
                                        float num11 = math.lengthsq(value3.m_Position - connectPosition4.m_Position);
                                        if (num11 < num9)
                                        {
                                            num9 = num11;
                                            index2 = num10;
                                        }
                                    }
                                }
                                tempBuffer[index2] = value3;
                            }
                            for (int num12 = 0; num12 < tempBuffer.Length; num12++)
                            {
                                ConnectPosition value4 = tempBuffer[num12];
                                if (value4.m_Owner == Entity.Null)
                                {
                                    value4.m_Owner = owner;
                                    value4.m_NodeComposition = targetPosition.m_NodeComposition;
                                    value4.m_EdgeComposition = targetPosition.m_EdgeComposition;
                                    value4.m_Tangent = targetPosition.m_Tangent;
                                    tempBuffer[num12] = value4;
                                }
                            }
                            PathNode pathNode = default(PathNode);
                            PathNode endPathNode = default(PathNode);
                            for (int num13 = 0; num13 < bufferData2.Length; num13++)
                            {
                                NetCompositionCrosswalk netCompositionCrosswalk = bufferData2[num13];
                                ConnectPosition sourcePosition = tempBuffer[num13];
                                ConnectPosition targetPosition3 = tempBuffer[num13 + 1];
                                float num14 = netCompositionCrosswalk.m_Start.x / math.max(1f, netCompositionData2.m_Width) + 0.5f;
                                float num15 = netCompositionCrosswalk.m_End.x / math.max(1f, netCompositionData2.m_Width) + 0.5f;
                                if (flag7)
                                {
                                    sourcePosition.m_Position = math.lerp(left2.m_Left.d, left2.m_Right.d, num14);
                                    targetPosition3.m_Position = math.lerp(left2.m_Left.d, left2.m_Right.d, num15);
                                    float2 x2 = 0.5f;
                                    if (flag)
                                    {
                                        float3 rhs = (sourcePosition.m_Position + targetPosition3.m_Position) * 0.5f;
                                        Bounds2 bounds2 = new Bounds2(2f, 0f);
                                        for (int num16 = 0; num16 < buffer.Length; num16++)
                                        {
                                            ConnectPosition connectPosition5 = buffer[num16];
                                            if (!connectPosition5.m_IsSideConnection)
                                            {
                                                continue;
                                            }
                                            PrefabRef prefabRef = m_PrefabRefData[connectPosition5.m_Owner];
                                            if ((m_PrefabNetData[prefabRef.m_Prefab].m_RequiredLayers & Layer.Pathway) != 0)
                                            {
                                                if (math.dot(targetPosition3.m_Position - sourcePosition.m_Position, connectPosition5.m_Position - rhs) < 0f)
                                                {
                                                    float t4;
                                                    float num17 = MathUtils.Distance(left2.m_Left, connectPosition5.m_Position, out t4);
                                                    float t5;
                                                    float num18 = MathUtils.Distance(segment.m_Right, connectPosition5.m_Position, out t5);
                                                    bounds2.x |= math.select(t4, 2f - t5, num18 < num17);
                                                }
                                                else
                                                {
                                                    float t6;
                                                    float num19 = MathUtils.Distance(left2.m_Right, connectPosition5.m_Position, out t6);
                                                    float t7;
                                                    float num20 = MathUtils.Distance(segment.m_Left, connectPosition5.m_Position, out t7);
                                                    bounds2.y |= math.select(t6, 2f - t7, num20 < num19);
                                                }
                                            }
                                        }
                                        bool2 x3 = bounds2.max >= bounds2.min;
                                        if (math.any(x3))
                                        {
                                            if (!math.all(x3))
                                            {
                                                if (x3.x)
                                                {
                                                    bounds2.y = bounds2.x;
                                                }
                                                else
                                                {
                                                    bounds2.x = bounds2.y;
                                                }
                                            }
                                            float2 float2 = MathUtils.Center(bounds2);
                                            float2 = math.lerp(float2.x, float2.y, new float2(num14, num15));
                                            bool2 @bool = float2 > 1f;
                                            x2.x = 1f - float2.x * 0.5f;
                                            x2.y = float2.y * 0.5f;
                                            x2 = math.saturate(x2);
                                            sourcePosition.m_Position = MathUtils.Position(MathUtils.Lerp(@bool.x ? segment.m_Right : left2.m_Left, @bool.x ? segment.m_Left : left2.m_Right, num14), @bool.x ? (2f - float2.x) : float2.x);
                                            targetPosition3.m_Position = MathUtils.Position(MathUtils.Lerp(@bool.y ? segment.m_Right : left2.m_Left, @bool.y ? segment.m_Left : left2.m_Right, num15), @bool.y ? (2f - float2.y) : float2.y);
                                        }
                                    }
                                    if (num13 == 0)
                                    {
                                        sourcePosition.m_Owner = owner;
                                        pathNode = new PathNode(owner, (ushort)num3, x2.x);
                                        endPathNode = pathNode;
                                    }
                                    if (num13 == bufferData2.Length - 1)
                                    {
                                        targetPosition3.m_Owner = owner;
                                        endPathNode = new PathNode(owner, (ushort)num7, x2.y);
                                    }
                                }
                                else
                                {
                                    sourcePosition.m_Position = math.lerp(left2.m_Left.a, left2.m_Right.a, num14);
                                    targetPosition3.m_Position = math.lerp(left2.m_Left.a, left2.m_Right.a, num15);
                                    sourcePosition.m_Position += sourcePosition.m_Tangent * netCompositionCrosswalk.m_Start.z;
                                    targetPosition3.m_Position += targetPosition3.m_Tangent * netCompositionCrosswalk.m_End.z;
                                    if (num13 == 0 && sourcePosition.m_Owner == owner)
                                    {
                                        pathNode = new PathNode(owner, (ushort)nodeLaneIndex++);
                                        endPathNode = pathNode;
                                    }
                                }
                                sourcePosition.m_Position.y += netCompositionCrosswalk.m_Start.y;
                                targetPosition3.m_Position.y += netCompositionCrosswalk.m_End.y;
                                sourcePosition.m_LaneData.m_Lane = netCompositionCrosswalk.m_Lane;
                                targetPosition3.m_LaneData.m_Lane = netCompositionCrosswalk.m_Lane;
                                CreateNodePedestrianLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, sourcePosition, targetPosition3, pathNode, endPathNode, default(float2), isCrosswalk: true, isSideConnection: false, isTemp, ownerTemp,
                                    fixedTangents: false, (netCompositionCrosswalk.m_Flags & LaneFlags.CrossRoad) != 0, out Bezier4x3 _, out PathNode _, out PathNode endNode);
                                pathNode = endNode;
                                endPathNode = endNode;
                            }
                        }
                    }
                    num5 = k;
                }
                if (flag2 || !(middleRadius <= 0f))
                {
                    return;
                }
                for (int num21 = 1; num21 < stackList.Length; num21++)
                {
                    int2 lhs = stackList[num21];
                    for (int num22 = 0; num22 < num21; num22++)
                    {
                        int2 @int = stackList[num22];
                        if (math.all(lhs != @int.yx))
                        {
                            ConnectPosition sourcePosition2 = buffer[lhs.x];
                            ConnectPosition targetPosition4 = buffer[@int.x];
                            CreateNodePedestrianLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, sourcePosition2, targetPosition4, tempBuffer2, sourceHasSingleLane: true, targetHasSingleLane: true, isTemp, ownerTemp, middlePosition,
                                middleRadius, roundaboutSize);
                        }
                    }
                }
            }

            private void CreateNodePedestrianLanes(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, ConnectPosition sourcePosition, ConnectPosition targetPosition,
                NativeList<ConnectPosition> sideConnections, bool sourceHasSingleLane, bool targetHasSingleLane, bool isTemp, Temp ownerTemp, float3 middlePosition, float middleRadius, float roundaboutSize)
            {
                PathNode middleNode2;
                float t;
                Bezier4x3 curve2;
                if (middleRadius == 0f)
                {
                    ConnectPosition sourcePosition2 = sourcePosition;
                    ConnectPosition targetPosition2 = targetPosition;
                    PathNode endNode = default(PathNode);
                    CreateNodePedestrianLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, sourcePosition2, targetPosition2, default(PathNode), endNode, default(float2), isCrosswalk: false, isSideConnection: false, isTemp, ownerTemp,
                        fixedTangents: false, hasSignals: true, out Bezier4x3 curve, out PathNode middleNode, out middleNode2);
                    for (int i = 0; i < sideConnections.Length; i++)
                    {
                        ConnectPosition targetPosition3 = sideConnections[i];
                        float num = MathUtils.Distance(curve, targetPosition3.m_Position, out t);
                        float3 @float = targetPosition3.m_Position + targetPosition3.m_Tangent * (num * 0.5f);
                        MathUtils.Distance(curve, @float, out float t2);
                        ConnectPosition sourcePosition3 = default(ConnectPosition);
                        sourcePosition3.m_LaneData.m_Lane = targetPosition3.m_LaneData.m_Lane;
                        sourcePosition3.m_LaneData.m_Flags = targetPosition3.m_LaneData.m_Flags;
                        sourcePosition3.m_NodeComposition = targetPosition3.m_NodeComposition;
                        sourcePosition3.m_EdgeComposition = targetPosition3.m_EdgeComposition;
                        sourcePosition3.m_Owner = owner;
                        sourcePosition3.m_BaseHeight = math.lerp(sourcePosition.m_BaseHeight, targetPosition.m_BaseHeight, t2);
                        sourcePosition3.m_Position = MathUtils.Position(curve, t2);
                        sourcePosition3.m_Tangent = @float - sourcePosition3.m_Position;
                        sourcePosition3.m_Tangent = MathUtils.Normalize(sourcePosition3.m_Tangent, sourcePosition3.m_Tangent.xz);
                        PathNode pathNode = new PathNode(middleNode, t2);
                        CreateNodePedestrianLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, sourcePosition3, targetPosition3, pathNode, pathNode, default(float2), isCrosswalk: false, isSideConnection: true, isTemp, ownerTemp,
                            fixedTangents: false, hasSignals: true, out curve2, out middleNode2, out endNode);
                    }
                    return;
                }
                float2 float2 = math.normalizesafe(sourcePosition.m_Position.xz - middlePosition.xz);
                float2 toVector = math.normalizesafe(targetPosition.m_Position.xz - middlePosition.xz);
                NetCompositionData netCompositionData = m_PrefabCompositionData[sourcePosition.m_NodeComposition];
                NetCompositionData netCompositionData2 = m_PrefabCompositionData[targetPosition.m_NodeComposition];
                float start = netCompositionData.m_Width * 0.5f - sourcePosition.m_LaneData.m_Position.x;
                float end = netCompositionData2.m_Width * 0.5f + targetPosition.m_LaneData.m_Position.x;
                float v = 0f;
                if (sourceHasSingleLane)
                {
                    start = 1f;
                    v = 2f;
                }
                if (targetHasSingleLane)
                {
                    end = 1f;
                    v = 2f;
                }
                float num2 = middleRadius + roundaboutSize - math.lerp(start, end, 0.5f);
                bool test = sourcePosition.m_Position.Equals(targetPosition.m_Position) && sourcePosition.m_Owner == targetPosition.m_Owner && sourcePosition.m_LaneData.m_Index == targetPosition.m_LaneData.m_Index;
                float num3 = math.select(MathUtils.RotationAngleLeft(float2, toVector), math.PI * 2f, test);
                int num4 = 1 + math.max(1, Mathf.CeilToInt(num3 * (2f / math.PI) - 0.003141593f));
                if (num4 == 2)
                {
                    float y = MathUtils.Distance(NetUtils.FitCurve(sourcePosition.m_Position, sourcePosition.m_Tangent, -targetPosition.m_Tangent, targetPosition.m_Position).xz, middlePosition.xz, out t);
                    num2 = math.max(num2, y);
                }
                ConnectPosition connectPosition = sourcePosition;
                float num5 = 0f;
                PathNode pathNode2 = default(PathNode);
                if (num4 >= 2)
                {
                    for (int j = 0; j < sideConnections.Length; j++)
                    {
                        ref ConnectPosition reference = ref sideConnections.ElementAt(j);
                        float2 toVector2 = math.normalizesafe(reference.m_Position.xz - middlePosition.xz);
                        reference.m_Order = MathUtils.RotationAngleLeft(float2, toVector2);
                        if (reference.m_Order > num3)
                        {
                            float num6 = MathUtils.RotationAngleRight(float2, toVector2);
                            reference.m_Order = math.select(0f, num3, num6 > reference.m_Order - num3);
                        }
                    }
                }
                for (int k = 1; k <= num4; k++)
                {
                    float num7 = math.saturate(((float)k - 0.5f) / ((float)num4 - 1f));
                    ConnectPosition connectPosition2 = default(ConnectPosition);
                    if (k == num4)
                    {
                        connectPosition2 = targetPosition;
                    }
                    else
                    {
                        float2 float3 = MathUtils.RotateLeft(float2, num3 * num7);
                        connectPosition2.m_LaneData.m_Lane = sourcePosition.m_LaneData.m_Lane;
                        connectPosition2.m_LaneData.m_Flags = sourcePosition.m_LaneData.m_Flags;
                        connectPosition2.m_NodeComposition = sourcePosition.m_NodeComposition;
                        connectPosition2.m_EdgeComposition = sourcePosition.m_EdgeComposition;
                        connectPosition2.m_Owner = owner;
                        connectPosition2.m_BaseHeight = math.lerp(sourcePosition.m_BaseHeight, targetPosition.m_BaseHeight, num7);
                        connectPosition2.m_Position.y = math.lerp(sourcePosition.m_Position.y, targetPosition.m_Position.y, num7);
                        connectPosition2.m_Position.xz = middlePosition.xz + float3 * num2;
                        connectPosition2.m_Tangent.xz = MathUtils.Right(float3);
                    }
                    ConnectPosition sourcePosition4 = connectPosition;
                    ConnectPosition targetPosition4 = connectPosition2;
                    float2 float4 = v;
                    Bezier4x3 curve3;
                    PathNode middleNode3;
                    if (k > 1 && k < num4)
                    {
                        CreateNodePedestrianLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, sourcePosition4, targetPosition4, pathNode2, pathNode2, float4, isCrosswalk: false, isSideConnection: false, isTemp, ownerTemp,
                            fixedTangents: false, hasSignals: true, out curve3, out middleNode3, out PathNode endNode2);
                        pathNode2 = endNode2;
                    }
                    else
                    {
                        float num8 = math.lerp(num5, num7, 0.5f);
                        float2 float5 = MathUtils.RotateLeft(float2, num3 * num8);
                        float3 centerPosition = middlePosition;
                        centerPosition.y = math.lerp(connectPosition.m_Position.y, connectPosition2.m_Position.y, 0.5f);
                        centerPosition.xz += float5 * num2;
                        float3 centerTangent = default(float3);
                        centerTangent.xz = MathUtils.Left(float5);
                        if (k == 1)
                        {
                            PresetCurve(ref sourcePosition4, ref targetPosition4, middlePosition, centerPosition, centerTangent, num2, 0f, num3 / (float)num4, 2f);
                        }
                        else
                        {
                            PresetCurve(ref sourcePosition4, ref targetPosition4, middlePosition, centerPosition, centerTangent, num2, num3 / (float)num4, 0f, 2f);
                        }
                        float4 = math.select(float4, 0f, new bool2(k == 1, k == num4));
                        CreateNodePedestrianLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, sourcePosition4, targetPosition4, pathNode2, pathNode2, float4, isCrosswalk: false, isSideConnection: false, isTemp, ownerTemp,
                            fixedTangents: true, hasSignals: true, out curve3, out middleNode3, out PathNode endNode3);
                        pathNode2 = endNode3;
                    }
                    for (int l = 0; l < sideConnections.Length; l++)
                    {
                        ConnectPosition targetPosition5 = sideConnections[l];
                        if ((!(targetPosition5.m_Order < num3 * num5) || k == 1) && (!(targetPosition5.m_Order >= num3 * num7) || k == num4))
                        {
                            float num9 = MathUtils.Distance(curve3, targetPosition5.m_Position, out t);
                            float3 float6 = targetPosition5.m_Position + targetPosition5.m_Tangent * (num9 * 0.5f);
                            MathUtils.Distance(curve3, float6, out float t3);
                            ConnectPosition sourcePosition5 = default(ConnectPosition);
                            sourcePosition5.m_LaneData.m_Lane = targetPosition5.m_LaneData.m_Lane;
                            sourcePosition5.m_LaneData.m_Flags = targetPosition5.m_LaneData.m_Flags;
                            sourcePosition5.m_NodeComposition = targetPosition5.m_NodeComposition;
                            sourcePosition5.m_EdgeComposition = targetPosition5.m_EdgeComposition;
                            sourcePosition5.m_Owner = owner;
                            sourcePosition5.m_BaseHeight = math.lerp(sourcePosition.m_BaseHeight, targetPosition.m_BaseHeight, t3);
                            sourcePosition5.m_Position = MathUtils.Position(curve3, t3);
                            sourcePosition5.m_Tangent = float6 - sourcePosition5.m_Position;
                            sourcePosition5.m_Tangent = MathUtils.Normalize(sourcePosition5.m_Tangent, sourcePosition5.m_Tangent.xz);
                            PathNode pathNode3 = new PathNode(middleNode3, t3);
                            CreateNodePedestrianLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, sourcePosition5, targetPosition5, pathNode3, pathNode3, default(float2), isCrosswalk: false, isSideConnection: true, isTemp, ownerTemp,
                                fixedTangents: false, hasSignals: true, out curve2, out PathNode _, out middleNode2);
                        }
                    }
                    connectPosition = connectPosition2;
                    connectPosition.m_Tangent = -connectPosition2.m_Tangent;
                    num5 = num7;
                }
            }

            private bool FindNextRightLane(ConnectPosition position, NativeList<ConnectPosition> buffer, out int2 result) {
                float2 x = new float2(position.m_Tangent.z, 0f - position.m_Tangent.x);
                float num = -1f;
                result = default(int2);
                int num2 = 0;
                while (num2 < buffer.Length)
                {
                    ConnectPosition connectPosition = buffer[num2];
                    int i;
                    for (i = num2 + 1; i < buffer.Length && !(buffer[i].m_Owner != connectPosition.m_Owner); i++)
                    {
                    }
                    if (!connectPosition.m_Owner.Equals(position.m_Owner) || i != num2 + 1)
                    {
                        float2 value = connectPosition.m_Position.xz - position.m_Position.xz;
                        value -= connectPosition.m_Tangent.xz;
                        MathUtils.TryNormalize(ref value);
                        float num3;
                        if (math.dot(position.m_Tangent.xz, value) > 0f)
                        {
                            num3 = math.dot(x, value) * 0.5f;
                        }
                        else
                        {
                            float num4 = math.dot(x, value);
                            num3 = math.select(-1f, 1f, num4 >= 0f) - num4 * 0.5f;
                        }
                        if (num3 > num)
                        {
                            num = num3;
                            result = new int2(num2, i);
                        }
                    }
                    num2 = i;
                }
                return num != -1f;
            }

            private void CreateNodePedestrianLane(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, ConnectPosition sourcePosition,
                ConnectPosition targetPosition, PathNode startPathNode, PathNode endPathNode, float2 overrideWidths, bool isCrosswalk, bool isSideConnection, bool isTemp, Temp ownerTemp, bool fixedTangents, bool hasSignals,
                out Bezier4x3 curve, out PathNode middleNode, out PathNode endNode
            ) {
                curve = default(Bezier4x3);
                NetCompositionData startCompositionData = m_PrefabCompositionData[sourcePosition.m_NodeComposition];
                NetCompositionData endCompositionData = m_PrefabCompositionData[targetPosition.m_NodeComposition];
                Owner component = default(Owner);
                component.m_Owner = owner;
                Temp temp = default(Temp);
                if (isTemp)
                {
                    temp.m_Flags = (ownerTemp.m_Flags & (TempFlags.Create | TempFlags.Delete | TempFlags.Select | TempFlags.Modify | TempFlags.Hidden));
                    if ((ownerTemp.m_Flags & TempFlags.Replace) != 0)
                    {
                        temp.m_Flags |= TempFlags.Modify;
                    }
                }
                Lane lane = default(Lane);
                if (sourcePosition.m_Owner == owner)
                {
                    lane.m_StartNode = startPathNode;
                }
                else
                {
                    lane.m_StartNode = new PathNode(sourcePosition.m_Owner, sourcePosition.m_LaneData.m_Index, sourcePosition.m_SegmentIndex);
                }
                lane.m_MiddleNode = new PathNode(owner, (ushort)nodeLaneIndex++);
                if (targetPosition.m_Owner == owner)
                {
                    if (!endPathNode.Equals(startPathNode))
                    {
                        lane.m_EndNode = endPathNode;
                    }
                    else
                    {
                        lane.m_EndNode = new PathNode(owner, (ushort)nodeLaneIndex++);
                    }
                }
                else
                {
                    lane.m_EndNode = new PathNode(targetPosition.m_Owner, targetPosition.m_LaneData.m_Index, targetPosition.m_SegmentIndex);
                }
                middleNode = lane.m_MiddleNode;
                endNode = lane.m_EndNode;
                PrefabRef component2 = default(PrefabRef);
                component2.m_Prefab = sourcePosition.m_LaneData.m_Lane;
                NetLaneData netLaneData = m_NetLaneData[sourcePosition.m_LaneData.m_Lane];
                NetLaneData netLaneData2 = m_NetLaneData[targetPosition.m_LaneData.m_Lane];
                netLaneData.m_Width = math.select(netLaneData.m_Width, overrideWidths.x, overrideWidths.x != 0f);
                netLaneData2.m_Width = math.select(netLaneData2.m_Width, overrideWidths.y, overrideWidths.y != 0f);
                if (!isCrosswalk)
                {
                    bool num = sourcePosition.m_LaneData.m_Position.x > 0f;
                    bool flag = targetPosition.m_LaneData.m_Position.x > 0f;
                    CompositionFlags.Side side = num ? startCompositionData.m_Flags.m_Right : startCompositionData.m_Flags.m_Left;
                    CompositionFlags.Side side2 = flag ? endCompositionData.m_Flags.m_Right : endCompositionData.m_Flags.m_Left;
                    int num2 = math.select(0, 1, (sourcePosition.m_CompositionData.m_RoadFlags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0);
                    int num3 = math.select(0, 1, (targetPosition.m_CompositionData.m_RoadFlags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0);
                    num2 = math.select(num2, 1 - num2, (side & CompositionFlags.Side.Sidewalk) != 0);
                    num3 = math.select(num3, 1 - num3, (side2 & CompositionFlags.Side.Sidewalk) != 0);
                    if (m_PrefabCompositionData.HasComponent(sourcePosition.m_EdgeComposition))
                    {
                        NetCompositionData netCompositionData = m_PrefabCompositionData[sourcePosition.m_EdgeComposition];
                        num2 = math.select(num2, num2 + 2, (netCompositionData.m_Flags.m_General & CompositionFlags.General.Tiles) != 0);
                        num2 = math.select(num2, num2 - 4, (netCompositionData.m_Flags.m_General & CompositionFlags.General.Gravel) != 0);
                    }
                    if (m_PrefabCompositionData.HasComponent(targetPosition.m_EdgeComposition))
                    {
                        NetCompositionData netCompositionData2 = m_PrefabCompositionData[targetPosition.m_EdgeComposition];
                        num3 = math.select(num3, num3 + 2, (netCompositionData2.m_Flags.m_General & CompositionFlags.General.Tiles) != 0);
                        num3 = math.select(num3, num3 - 4, (netCompositionData2.m_Flags.m_General & CompositionFlags.General.Gravel) != 0);
                    }
                    if ((sourcePosition.m_LaneData.m_Flags & targetPosition.m_LaneData.m_Flags & LaneFlags.OnWater) != 0)
                    {
                        num2 += math.select(0, 1, netLaneData.m_Width < netLaneData2.m_Width);
                        num3 += math.select(0, 1, netLaneData2.m_Width < netLaneData.m_Width);
                    }
                    if (num2 > num3 && component2.m_Prefab != sourcePosition.m_LaneData.m_Lane)
                    {
                        component2.m_Prefab = sourcePosition.m_LaneData.m_Lane;
                    }
                    if (num3 > num2 && component2.m_Prefab != targetPosition.m_LaneData.m_Lane)
                    {
                        component2.m_Prefab = targetPosition.m_LaneData.m_Lane;
                    }
                }
                CheckPrefab(ref component2.m_Prefab, ref random, out Unity.Mathematics.Random outRandom, laneBuffer);
                PedestrianLane component3 = default(PedestrianLane);
                NodeLane component4 = default(NodeLane);
                Curve component5 = default(Curve);
                float2 lhs = 0f;
                bool flag2 = false;
                NetLaneData netLaneData3 = m_NetLaneData[component2.m_Prefab];
                component4.m_WidthOffset.x = netLaneData.m_Width - netLaneData3.m_Width;
                component4.m_WidthOffset.y = netLaneData2.m_Width - netLaneData3.m_Width;
                component4.m_Flags |= (NodeLaneFlags)((component4.m_WidthOffset.x != 0f) ? 1 : 0);
                component4.m_Flags |= (NodeLaneFlags)((component4.m_WidthOffset.y != 0f) ? 2 : 0);
                if (isCrosswalk)
                {
                    component3.m_Flags |= PedestrianLaneFlags.Crosswalk;
                    if ((startCompositionData.m_Flags.m_General & CompositionFlags.General.Crosswalk) == 0)
                    {
                        if ((startCompositionData.m_Flags.m_General & CompositionFlags.General.DeadEnd) != 0)
                        {
                            return;
                        }
                        component3.m_Flags |= PedestrianLaneFlags.Unsafe;
                        hasSignals = false;
                    }
                    component5.m_Bezier = NetUtils.StraightCurve(sourcePosition.m_Position, targetPosition.m_Position);
                    component5.m_Length = math.distance(sourcePosition.m_Position, targetPosition.m_Position);
                    hasSignals &= ((startCompositionData.m_Flags.m_General & CompositionFlags.General.TrafficLights) != 0);
                    if (component5.m_Length >= 0.1f)
                    {
                        float2 y = math.normalizesafe(targetPosition.m_Position.xz - sourcePosition.m_Position.xz);
                        lhs = new float2(math.dot(sourcePosition.m_Tangent.xz, y), math.dot(targetPosition.m_Tangent.xz, y));
                        lhs = math.tan(math.PI / 2f - math.acos(math.saturate(math.abs(lhs))));
                        lhs = lhs * (netLaneData3.m_Width + component4.m_WidthOffset) * 0.5f;
                        lhs = math.select(math.saturate(lhs / component5.m_Length), 0f, lhs < 0.01f);
                    }
                }
                else
                {
                    if (sourcePosition.m_Owner == targetPosition.m_Owner &&
                        ((startCompositionData.m_Flags.m_General & (CompositionFlags.General.DeadEnd | CompositionFlags.General.Roundabout)) == 0 ||
                        (startCompositionData.m_Flags.m_Right & CompositionFlags.Side.AbruptEnd) != 0))
                    {
                        return;
                    }
                    if (math.distance(sourcePosition.m_Position, targetPosition.m_Position) >= 0.1f)
                    {
                        if (fixedTangents)
                        {
                            component5.m_Bezier = new Bezier4x3(sourcePosition.m_Position, sourcePosition.m_Position + sourcePosition.m_Tangent, targetPosition.m_Position + targetPosition.m_Tangent,
                                targetPosition.m_Position);
                        }
                        else
                        {
                            component5.m_Bezier = NetUtils.FitCurve(sourcePosition.m_Position, sourcePosition.m_Tangent, -targetPosition.m_Tangent, targetPosition.m_Position);
                        }
                    }
                    else
                    {
                        component5.m_Bezier = NetUtils.StraightCurve(sourcePosition.m_Position, targetPosition.m_Position);
                        flag2 = true;
                    }
                    if (isSideConnection)
                    {
                        component3.m_Flags |= PedestrianLaneFlags.SideConnection;
                    }
                    else
                    {
                        component3.m_Flags |= PedestrianLaneFlags.AllowMiddle;
                        ModifyCurveHeight(ref component5.m_Bezier, sourcePosition.m_BaseHeight, targetPosition.m_BaseHeight, startCompositionData, endCompositionData);
                    }
                    if ((sourcePosition.m_RoadTypes & targetPosition.m_RoadTypes & RoadTypes.Bicycle) != 0)
                    {
                        component3.m_Flags |= PedestrianLaneFlags.AllowBicycle;
                    }
                    component5.m_Length = MathUtils.Length(component5.m_Bezier);
                    hasSignals &= ((startCompositionData.m_Flags.m_General & CompositionFlags.General.LevelCrossing) != 0);
                }
                curve = component5.m_Bezier;
                if ((sourcePosition.m_LaneData.m_Flags & targetPosition.m_LaneData.m_Flags & LaneFlags.OnWater) != 0)
                {
                    component3.m_Flags |= PedestrianLaneFlags.OnWater;
                }
                if (flag2)
                {
                    if (isTemp)
                    {
                        lane.m_StartNode = new PathNode(lane.m_StartNode, secondaryNode: true);
                        lane.m_MiddleNode = new PathNode(lane.m_MiddleNode, secondaryNode: true);
                        lane.m_EndNode = new PathNode(lane.m_EndNode, secondaryNode: true);
                    }
                    m_SkipLaneQueue.Enqueue(lane);
                    return;
                }
                LaneKey laneKey = new LaneKey(lane, component2.m_Prefab, (LaneFlags)0);
                LaneKey laneKey2 = laneKey;
                if (isTemp)
                {
                    ReplaceTempOwner(ref laneKey2, owner);
                    ReplaceTempOwner(ref laneKey2, sourcePosition.m_Owner);
                    ReplaceTempOwner(ref laneKey2, targetPosition.m_Owner);
                    GetOriginalLane(laneBuffer, laneKey2, ref temp);
                }
                PseudoRandomSeed componentData = default(PseudoRandomSeed);
                if ((netLaneData3.m_Flags & LaneFlags.PseudoRandom) != 0 && !m_PseudoRandomSeedData.TryGetComponent(temp.m_Original, out componentData))
                {
                    componentData = new PseudoRandomSeed(ref outRandom);
                }
                CutRange elem;
                if (laneBuffer.m_OldLanes.TryGetValue(laneKey, out Entity item))
                {
                    laneBuffer.m_OldLanes.Remove(laneKey);
                    m_CommandBuffer.SetComponent(jobIndex, item, lane);
                    m_CommandBuffer.SetComponent(jobIndex, item, component4);
                    m_CommandBuffer.SetComponent(jobIndex, item, component5);
                    m_CommandBuffer.SetComponent(jobIndex, item, component3);
                    if ((netLaneData3.m_Flags & LaneFlags.PseudoRandom) != 0)
                    {
                        if (!m_PseudoRandomSeedData.HasComponent(item))
                        {
                            m_CommandBuffer.AddComponent(jobIndex, item, componentData);
                        }
                        else
                        {
                            m_CommandBuffer.SetComponent(jobIndex, item, componentData);
                        }
                    }
                    if (hasSignals)
                    {
                        if (!m_LaneSignalData.HasComponent(item))
                        {
                            m_CommandBuffer.AddComponent(jobIndex, item, default(LaneSignal));
                        }
                    }
                    else if (m_LaneSignalData.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent<LaneSignal>(jobIndex, item);
                    }
                    if (math.any(lhs != 0f))
                    {
                        DynamicBuffer<CutRange> dynamicBuffer = (!m_CutRanges.HasBuffer(item)) ? m_CommandBuffer.AddBuffer<CutRange>(jobIndex, item) : m_CommandBuffer.SetBuffer<CutRange>(jobIndex, item);
                        if (lhs.x != 0f)
                        {
                            elem = new CutRange
                            {
                                m_CurveDelta = new Bounds1(0f, lhs.x)
                            };
                            dynamicBuffer.Add(elem);
                        }
                        if (lhs.y != 0f)
                        {
                            elem = new CutRange
                            {
                                m_CurveDelta = new Bounds1(1f - lhs.y, 1f)
                            };
                            dynamicBuffer.Add(elem);
                        }
                    }
                    else
                    {
                        m_CommandBuffer.RemoveComponent<CutRange>(jobIndex, item);
                    }
                    if (isTemp)
                    {
                        laneBuffer.m_Updates.Add(in item);
                        m_CommandBuffer.SetComponent(jobIndex, item, temp);
                    }
                    else if (m_TempData.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent(jobIndex, item, in m_DeletedTempTypes);
                        m_CommandBuffer.AddComponent(jobIndex, item, in m_AppliedTypes);
                    }
                    else
                    {
                        laneBuffer.m_Updates.Add(in item);
                    }
                    return;
                }
                EntityArchetype nodeLaneArchetype = m_PrefabLaneArchetypeData[component2.m_Prefab].m_NodeLaneArchetype;
                Entity e = m_CommandBuffer.CreateEntity(jobIndex, nodeLaneArchetype);
                m_CommandBuffer.SetComponent(jobIndex, e, component2);
                m_CommandBuffer.SetComponent(jobIndex, e, lane);
                m_CommandBuffer.SetComponent(jobIndex, e, component4);
                m_CommandBuffer.SetComponent(jobIndex, e, component5);
                m_CommandBuffer.SetComponent(jobIndex, e, component3);
                if ((netLaneData3.m_Flags & LaneFlags.PseudoRandom) != 0)
                {
                    m_CommandBuffer.SetComponent(jobIndex, e, componentData);
                }
                if (isTemp)
                {
                    m_CommandBuffer.AddComponent(jobIndex, e, in m_TempOwnerTypes);
                    m_CommandBuffer.SetComponent(jobIndex, e, component);
                    m_CommandBuffer.SetComponent(jobIndex, e, temp);
                }
                else
                {
                    m_CommandBuffer.AddComponent(jobIndex, e, component);
                }
                if (hasSignals)
                {
                    m_CommandBuffer.AddComponent(jobIndex, e, default(LaneSignal));
                }
                if (math.any(lhs != 0f))
                {
                    DynamicBuffer<CutRange> dynamicBuffer2 = m_CommandBuffer.AddBuffer<CutRange>(jobIndex, e);
                    if (lhs.x != 0f)
                    {
                        elem = new CutRange
                        {
                            m_CurveDelta = new Bounds1(0f, lhs.x)
                        };
                        dynamicBuffer2.Add(elem);
                    }
                    if (lhs.y != 0f)
                    {
                        elem = new CutRange
                        {
                            m_CurveDelta = new Bounds1(1f - lhs.y, 1f)
                        };
                        dynamicBuffer2.Add(elem);
                    }
                }
            }
        }
    }
}
