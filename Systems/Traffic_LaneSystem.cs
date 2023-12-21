using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
using Game.Simulation;
using Game.Tools;
using Traffic.LaneConnections;
using Unity.Burst;
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
using ParkingLane = Game.Net.ParkingLane;
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
    // [BurstCompile]
    public partial class TrafficLaneSystem : GameSystemBase
    {
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

            public string GetString() {
                return $"ConnectionKey=(e: {m_Data.x}, l: {m_Data.y} -> e: {m_Data.z}, l: {m_Data.w})";
            }
        }

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
            public ushort m_TargetIndex;
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
                return math.select(math.select(0, math.select(1, -1, m_Order < other.m_Order), m_Order != other.m_Order), m_Prefab.Index - other.m_Prefab.Index, m_Prefab.Index != other.m_Prefab.Index);
            }
        }

        private struct LaneBuffer
        {
            public NativeParallelHashMap<LaneKey, Entity> m_OldLanes;
            public NativeParallelHashMap<LaneKey, Entity> m_OriginalLanes;

			public NativeParallelHashMap<Entity, Unity.Mathematics.Random> m_SelectedSpawnables;

            public LaneBuffer(Allocator allocator)
            {
                m_OldLanes = new NativeParallelHashMap<LaneKey, Entity>(32, allocator);
                m_OriginalLanes = new NativeParallelHashMap<LaneKey, Entity>(32, allocator);
				m_SelectedSpawnables = new NativeParallelHashMap<Entity, Unity.Mathematics.Random>(10, allocator);
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
            }
        }

        private struct CompositionData
        {
            public float m_SpeedLimit;
            public float m_Priority;
            public TaxiwayFlags m_TaxiwayFlags;
            public Game.Prefabs.RoadFlags m_RoadFlags;
        }
    
    
        private CityConfigurationSystem m_CityConfigurationSystem;
        private ToolSystem m_ToolSystem;
        private TerrainSystem m_TerrainSystem;
        private ModificationBarrier4 m_ModificationBarrier;
        private EntityQuery m_OwnerQuery;
        private EntityQuery m_BuildingSettingsQuery;
        private ComponentTypeSet m_AppliedTypes;
        private ComponentTypeSet m_DeletedTempTypes;
        // private NativeArray<ushort> _heights;
        protected override void OnCreate() {
            base.OnCreate();
            m_CityConfigurationSystem = base.World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_ToolSystem = base.World.GetOrCreateSystemManaged<ToolSystem>();
            m_TerrainSystem = base.World.GetExistingSystemManaged<TerrainSystem>();
            m_ModificationBarrier = base.World.GetOrCreateSystemManaged<ModificationBarrier4>();
            m_OwnerQuery = GetEntityQuery(new EntityQueryDesc[]
            {
                new EntityQueryDesc
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<SubLane>()
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
            RequireForUpdate(m_OwnerQuery);
            // Logger.Debug("Traffic_LaneSystem Created!");
            // _heights = new NativeArray<ushort>(4, Allocator.Persistent);
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
                m_TrackLaneData = SystemAPI.GetComponentLookup<TrackLaneData>(true),
                m_UtilityLaneData = SystemAPI.GetComponentLookup<UtilityLaneData>(true),
                m_PrefabSpawnableObjectData = SystemAPI.GetComponentLookup<SpawnableObjectData>(true),
                m_PrefabObjectGeometryData = SystemAPI.GetComponentLookup<ObjectGeometryData>(true),
                m_PrefabBuildingData = SystemAPI.GetComponentLookup<BuildingData>(true),
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
                
                generatedConnectionsType = SystemAPI.GetBufferTypeHandle<GeneratedConnection>(true),
                modifiedLaneConnectionsType = SystemAPI.GetBufferTypeHandle<ModifiedLaneConnections>(true),
                
                m_LeftHandTraffic = m_CityConfigurationSystem.leftHandTraffic,
                m_EditorMode = m_ToolSystem.actionMode.IsEditor(),
                m_RandomSeed = RandomSeed.Next(),
                m_DefaultTheme = m_CityConfigurationSystem.defaultTheme,
                m_AppliedTypes = m_AppliedTypes,
                m_DeletedTempTypes = m_DeletedTempTypes,
                m_TerrainHeightData = m_TerrainSystem.GetHeightData(false),
                m_BuildingConfigurationData = m_BuildingSettingsQuery.GetSingleton<BuildingConfigurationData>(),
                m_CommandBuffer = m_ModificationBarrier.CreateCommandBuffer().AsParallelWriter(),
                // NON-STOCK
                // m_CustomPriorityData = SystemAPI.GetComponentLookup<CustomPriority>(true),
                // m_TrafficUpgradeData = SystemAPI.GetComponentLookup<TrafficUpgrade>(true),
                // NON-STOCK-END
            };
            JobHandle jobHandle = jobData.Schedule(m_OwnerQuery, Dependency);
            
            m_TerrainSystem.AddCPUHeightReader(jobHandle);
            m_ModificationBarrier.AddJobHandleForProducer(jobHandle);
            Dependency = jobHandle;
        }

        // [BurstCompile]
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
            public ComponentLookup<TrackLaneData> m_TrackLaneData;
            
            [ReadOnly]
            public ComponentLookup<UtilityLaneData> m_UtilityLaneData;
            
            [ReadOnly]
            public ComponentLookup<SpawnableObjectData> m_PrefabSpawnableObjectData;
            
            [ReadOnly]
            public ComponentLookup<ObjectGeometryData> m_PrefabObjectGeometryData;
            
            [ReadOnly]
            public ComponentLookup<BuildingData> m_PrefabBuildingData;
            //
            // // NON-STOCK
            // // [ReadOnly]
            // // public ComponentLookup<CustomPriority> m_CustomPriorityData;
            // // [ReadOnly]
            // // public ComponentLookup<TrafficUpgrade> m_TrafficUpgradeData;
            // // NON-STOCK-END
            //
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
            public BufferTypeHandle<GeneratedConnection> generatedConnectionsType;
            [ReadOnly]
            public BufferTypeHandle<ModifiedLaneConnections> modifiedLaneConnectionsType;
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
            public TerrainHeightData m_TerrainHeightData;
            
            [ReadOnly]
            public BuildingConfigurationData m_BuildingConfigurationData;
            
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

                if (chunk.Has<Edge>())
                {
                    NativeArray<ComponentType> componentTypes = chunk.Archetype.GetComponentTypes();
                    Logger.Debug($"Deleting: {string.Join(", ", componentTypes.Select(c => c.GetManagedType().Name))}");
                    componentTypes.Dispose();
                    // NativeArray<Edge> edges = chunk.GetNativeArray(ref m_EdgeType);
                    // BufferAccessor<ModifiedLaneConnections> modifiedLaneConnections = chunk.GetBufferAccessor(ref modifiedLaneConnectionsType);
                    // for (var i = 0; i < edges.Length; i++)
                    // {
                    //     if (modifiedLaneConnections.)
                    // }
                }
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
                                FillOldLaneBuffer(lanes2, laneBuffer.m_OriginalLanes);
                            }
                        }
                        FillOldLaneBuffer(lanes, laneBuffer.m_OldLanes);
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
                            //nodes for connecting buildings to the road edge
                            for (int j = 0; j < dynamicBuffer.Length; j++)
                            {
                                ConnectedNode connectedNode = dynamicBuffer[j];
                                GetMiddleConnectionCurves(connectedNode.m_Node, edgeTargets);
                                GetNodeConnectPositions(connectedNode.m_Node, connectedNode.m_CurvePosition, nativeList, nativeList2, includeAnchored: true, ref groupIndex, out float _, out float _);
                                FilterNodeConnectPositions(owner, ownerTemp.m_Original, nativeList, edgeTargets);
                                FilterNodeConnectPositions(owner, ownerTemp.m_Original, nativeList2, edgeTargets);
                                CreateEdgeConnectionLanes(chunkIndex, ref edgeLaneIndex, ref connectionIndex, ref random, owner, laneBuffer, nativeList, nativeList2, tempBuffer, tempBuffer2, composition.m_Edge,
                                    geometryData, prefabGeometryData, curve, tempComponents.Length != 0, ownerTemp);
                                nativeList.Clear();
                                nativeList2.Clear();
                            }
                            CreateEdgeLanes(chunkIndex, ref random, owner, laneBuffer, composition, edge, geometryData, prefabGeometryData, tempComponents.Length != 0, ownerTemp);
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
                            Unity.Mathematics.Random random2 = m_RandomSeed.GetRandom(owner.Index);
                            CreateEdgeLane(chunkIndex, ref random2, owner, laneBuffer, segment2, default(NetCompositionData), default(CompositionData), default(DynamicBuffer<NetCompositionLane>),
                                prefabCompositionLaneData, new int2(0, 4), new float2(0f, 1f), default(NativeList<LaneAnchor>), default(NativeList<LaneAnchor>), false, tempComponents.Length != 0, ownerTemp);
                        }
                        RemoveUnusedOldLanes(chunkIndex, lanes, laneBuffer.m_OldLanes);
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
                                FillOldLaneBuffer(lanes4, laneBuffer.m_OriginalLanes);
                            }
                        }
                        FillOldLaneBuffer(lanes3, laneBuffer.m_OldLanes);
                        if (!flag)
                        {
                            Entity entity2 = entity;
                            if ((ownerTemp2.m_Flags & (TempFlags.Delete | TempFlags.Select)) != 0 && ownerTemp2.m_Original != Entity.Null)
                            {
                                entity2 = ownerTemp2.m_Original;
                            }
                            bool flag5 = (ownerTemp2.m_Flags & (TempFlags.Delete | TempFlags.Select)) != 0;
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
                                flag6 = (m_PrefabObjectGeometryData.TryGetComponent(prefabRef2.m_Prefab, out ObjectGeometryData componentData) &&
                                    ((componentData.m_Flags & Game.Objects.GeometryFlags.DeleteOverridden) != 0 ||
                                    (componentData.m_Flags & (Game.Objects.GeometryFlags.Overridable | Game.Objects.GeometryFlags.Marker)) == 0));
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
                        RemoveUnusedOldLanes(chunkIndex, lanes3, laneBuffer.m_OldLanes);
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
                    NativeParallelHashSet<ConnectionKey> createdConnections = new NativeParallelHashSet<ConnectionKey>(32, Allocator.Temp);
                    NativeList<ConnectPosition> sourceNodeConnectPositions = new NativeList<ConnectPosition>(32, Allocator.Temp);
                    NativeList<ConnectPosition> targetNodeConnectPositions = new NativeList<ConnectPosition>(32, Allocator.Temp);
                    NativeList<ConnectPosition> sourceMainCarConnectPositions = new NativeList<ConnectPosition>(32, Allocator.Temp);
                    NativeList<ConnectPosition> targetMainCarConnectPositions = new NativeList<ConnectPosition>(32, Allocator.Temp);
                    NativeList<ConnectPosition> tempSourceConnectPositions = new NativeList<ConnectPosition>(32, Allocator.Temp);
                    NativeList<ConnectPosition> tempTargetConnectPositions = new NativeList<ConnectPosition>(32, Allocator.Temp);
                    NativeParallelHashSet<LaneEndKey> tempModifiedLaneEnds = new NativeParallelHashSet<LaneEndKey>(4, Allocator.Temp);
                    NativeHashSet<ConnectionKey> tempMainConnectionKeys = new NativeHashSet<ConnectionKey>(4, Allocator.Temp);
                    NativeList<MiddleConnection> middleConnections = new NativeList<MiddleConnection>(4, Allocator.Temp);
                    NativeList<EdgeTarget> tempEdgeTargets = new NativeList<EdgeTarget>(4, Allocator.Temp);
                    NativeArray<Orphan> orphanComponents = chunk.GetNativeArray(ref m_OrphanType);
                    NativeArray<NodeGeometry> nodeGeometryComponents = chunk.GetNativeArray(ref m_NodeGeometryType);
                    NativeArray<PrefabRef> prefabRefComponents = chunk.GetNativeArray(ref m_PrefabRefType);
                    BufferAccessor<GeneratedConnection> generatedConnections = chunk.GetBufferAccessor(ref generatedConnectionsType);
                    BufferAccessor<ModifiedLaneConnections> modifiedLaneConnections = chunk.GetBufferAccessor(ref modifiedLaneConnectionsType);
                
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
                                FillOldLaneBuffer(lanes6, laneBuffer.m_OriginalLanes);
                            }
                        }
                        FillOldLaneBuffer(lanes5, laneBuffer.m_OldLanes);
                        if (nodeGeometryComponents.Length != 0) //chunk has NodeGeometry component
                        {
                            
                            float3 position = m_NodeData[entity3].m_Position;
                            position.y = nodeGeometryComponents[entityIndex].m_Position;
                            int groupIndex2 = 1;
                            GetNodeConnectPositions(entity3, 0f, sourceNodeConnectPositions, targetNodeConnectPositions, includeAnchored: false, ref groupIndex2, out float middleRadius2, out float roundaboutSize2);
                            GetMiddleConnections(entity3, ownerTemp3.m_Original, middleConnections, tempEdgeTargets, tempSourceConnectPositions, tempTargetConnectPositions, ref groupIndex2);
                            FilterMainCarConnectPositions(sourceNodeConnectPositions, sourceMainCarConnectPositions);
                            FilterMainCarConnectPositions(targetNodeConnectPositions, targetMainCarConnectPositions);
                            int prevLaneIndex = 0;
                            Unity.Mathematics.Random random4 = randomSeeds[entityIndex].GetRandom(PseudoRandomSeed.kSubLane);
                            
                            /*NON-STOCK*/
                            bool testKeys = false;
                            if (modifiedLaneConnections.Length > 0 && tempComponents.Length == 0)
                            {
                                DynamicBuffer<ModifiedLaneConnections> connections = modifiedLaneConnections[entityIndex];
                                FillModifiedLaneConnections(connections, tempModifiedLaneEnds);
                            }
                            if (generatedConnections.Length > 0)
                            {
                                testKeys = true;
                            }
                            /*NON-STOCK*/
                            
                            if (middleRadius2 > 0f) // isRoundabout
                            {
                                if (sourceMainCarConnectPositions.Length != 0 || targetMainCarConnectPositions.Length != 0)
                                {
                                    ConnectPosition roundaboutLane = default(ConnectPosition);
                                    int laneCount = 0;
                                    uint laneGroup = 0u;
                                    float laneWidth = float.MaxValue;
                                    float spaceForLanes = float.MaxValue;
                                    bool isPublicOnly = true;
                                    int nextLaneIndex = prevLaneIndex;
                                    int highwayConnectPositions = 0;
                                    int nonHighwayConnectPositions = 0;
                                    for (int num5 = 0; num5 < sourceMainCarConnectPositions.Length; num5++)
                                    {
                                        if ((sourceMainCarConnectPositions[num5].m_CompositionData.m_RoadFlags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0)
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
                                    bool flag7;
                                    if (sourceMainCarConnectPositions.Length != 0)
                                    {
                                        int num7 = 0;
                                        for (int num8 = 0; num8 < sourceMainCarConnectPositions.Length; num8++)
                                        {
                                            ConnectPosition main = sourceMainCarConnectPositions[num8];
                                            FilterActualCarConnectPositions(main, sourceNodeConnectPositions, tempSourceConnectPositions);
                                            bool roundaboutLane2 = GetRoundaboutLane(tempSourceConnectPositions, roundaboutSize2, ref roundaboutLane, ref laneCount, ref laneWidth, ref isPublicOnly, ref spaceForLanes, isSource: true,
                                                preferHighway);
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
                                                GetRoundaboutLane(tempTargetConnectPositions, roundaboutSize2, ref roundaboutLane, ref laneCount2, ref laneWidth, ref isPublicOnly, ref spaceForLanes2, isSource: false, preferHighway);
                                                tempTargetConnectPositions.Clear();
                                            }
                                            spaceForLanes = spaceForLanes2 * (float)laneCount;
                                        }
                                        connectPosition2 = sourceMainCarConnectPositions[num7];
                                        flag7 = true;
                                    }
                                    else
                                    {
                                        int num10 = 0;
                                        for (int num11 = 0; num11 < targetMainCarConnectPositions.Length; num11++)
                                        {
                                            ConnectPosition main3 = targetMainCarConnectPositions[num11];
                                            FilterActualCarConnectPositions(main3, targetNodeConnectPositions, tempTargetConnectPositions);
                                            bool roundaboutLane3 = GetRoundaboutLane(tempTargetConnectPositions, roundaboutSize2, ref roundaboutLane, ref laneCount, ref laneWidth, ref isPublicOnly, ref spaceForLanes, isSource: false,
                                                preferHighway);
                                            num10 = math.select(num10, num11, roundaboutLane3);
                                            tempTargetConnectPositions.Clear();
                                        }
                                        connectPosition2 = targetMainCarConnectPositions[num10];
                                        flag7 = false;
                                    }
                                    ExtractNextConnectPosition(connectPosition2, position, sourceMainCarConnectPositions, targetMainCarConnectPositions, out ConnectPosition nextPosition, out bool nextIsSource);
                                    if (flag7)
                                    {
                                        FilterActualCarConnectPositions(connectPosition2, sourceNodeConnectPositions, tempSourceConnectPositions);
                                    }
                                    if (!nextIsSource)
                                    {
                                        FilterActualCarConnectPositions(nextPosition, targetNodeConnectPositions, tempTargetConnectPositions);
                                    }
                                    int laneCount3 = GetRoundaboutLaneCount(connectPosition2, nextPosition, tempSourceConnectPositions, tempTargetConnectPositions, targetNodeConnectPositions, position);
                                    connectPosition2 = nextPosition;
                                    flag7 = nextIsSource;
                                    tempSourceConnectPositions.Clear();
                                    tempTargetConnectPositions.Clear();
                                    while (sourceMainCarConnectPositions.Length != 0 || targetMainCarConnectPositions.Length != 0)
                                    {
                                        ExtractNextConnectPosition(connectPosition2, position, sourceMainCarConnectPositions, targetMainCarConnectPositions, out ConnectPosition nextPosition2, out bool nextIsSource2);
                                        if (flag7)
                                        {
                                            FilterActualCarConnectPositions(connectPosition2, sourceNodeConnectPositions, tempSourceConnectPositions);
                                        }
                                        if (!nextIsSource2)
                                        {
                                            FilterActualCarConnectPositions(nextPosition2, targetNodeConnectPositions, tempTargetConnectPositions);
                                        }
                                        CreateRoundaboutCarLanes(chunkIndex, ref random4, entity3, laneBuffer, ref prevLaneIndex, -1, ref laneGroup, connectPosition2, nextPosition2, middleConnections, tempSourceConnectPositions,
                                            tempTargetConnectPositions, targetNodeConnectPositions, roundaboutLane, position, middleRadius2, ref laneCount3, laneCount, spaceForLanes, tempComponents.Length != 0, ownerTemp3);
                                        connectPosition2 = nextPosition2;
                                        flag7 = nextIsSource2;
                                        tempSourceConnectPositions.Clear();
                                        tempTargetConnectPositions.Clear();
                                    }
                                    if (flag7)
                                    {
                                        FilterActualCarConnectPositions(connectPosition2, sourceNodeConnectPositions, tempSourceConnectPositions);
                                    }
                                    if (!nextIsSource)
                                    {
                                        FilterActualCarConnectPositions(nextPosition, targetNodeConnectPositions, tempTargetConnectPositions);
                                    }
                                    CreateRoundaboutCarLanes(chunkIndex, ref random4, entity3, laneBuffer, ref prevLaneIndex, nextLaneIndex, ref laneGroup, connectPosition2, nextPosition, middleConnections, tempSourceConnectPositions,
                                        tempTargetConnectPositions, targetNodeConnectPositions, roundaboutLane, position, middleRadius2, ref laneCount3, laneCount, spaceForLanes, tempComponents.Length != 0, ownerTemp3);
                                    tempSourceConnectPositions.Clear();
                                    tempTargetConnectPositions.Clear();
                                }
                            }
                            else
                            {
                                FilterActualCarConnectPositions(targetNodeConnectPositions, tempTargetConnectPositions);

                                for (int num12 = 0; num12 < sourceMainCarConnectPositions.Length; num12++)
                                {
                                    ConnectPosition sourceMainCarConnectPos = sourceMainCarConnectPositions[num12];
                                    int nodeLaneIndex = prevLaneIndex;
                                    int yield = CalculateYieldOffset(sourceMainCarConnectPos, sourceMainCarConnectPositions, targetMainCarConnectPositions);
                                    FilterActualCarConnectPositions(sourceMainCarConnectPos, sourceNodeConnectPositions, tempSourceConnectPositions);
                                    
                                    tempMainConnectionKeys.Clear();
                                    
                                    ProcessCarConnectPositions(chunkIndex, ref nodeLaneIndex, ref random4, entity3, laneBuffer, middleConnections, createdConnections, tempSourceConnectPositions, tempTargetConnectPositions, sourceNodeConnectPositions,
                                            tempComponents.Length != 0, ownerTemp3, yield, /*NON-STOCK*/ tempModifiedLaneEnds, tempMainConnectionKeys);
                                    // Logger.Debug("ProcessCarConnectPositions Done!");
                                    /*
                                     *
                                     */
                                    if (generatedConnections.Length > 0 && tempComponents.Length == 0)
                                    {
                                        DynamicBuffer<GeneratedConnection> connections = generatedConnections[entityIndex];
                                        int idx = prevLaneIndex;
                                        int index = 9999;
                                        
                                        for (var i = 0; i < connections.Length; i++)
                                        {
                                            var connection = connections[i];
                                            
                                            ConnectPosition cs = FindNodeConnectPosition(tempSourceConnectPositions, connection.sourceEntity, connection.laneIndexMap.x, out _);
                                            ConnectPosition ct = FindNodeConnectPosition(tempTargetConnectPositions, connection.targetEntity, connection.laneIndexMap.y, out int targetPosIndex);
                                            ConnectionKey key = new ConnectionKey(cs, ct);
                                            if (!createdConnections.Contains(key) && cs.m_Owner != Entity.Null && ct.m_Owner != Entity.Null && cs.m_Owner == sourceMainCarConnectPos.m_Owner && cs.m_LaneData.m_Group == sourceMainCarConnectPos.m_LaneData.m_Group)
                                            {
                                                if (connection.isUnsafe && targetPosIndex < tempTargetConnectPositions.Length)
                                                {
                                                    // update target connect pos to set more accurate flag of the main connection later (standalone/master)
                                                    ct.m_UnsafeCount++;
                                                    tempTargetConnectPositions[targetPosIndex] = ct;
                                                }
                                                int yield2 = CalculateYieldOffset(cs, sourceMainCarConnectPositions, targetMainCarConnectPositions);
                                                uint group = (uint)(cs.m_GroupIndex | (ct.m_GroupIndex << 16));
                                                bool isTurn = IsTurn(cs, ct, out bool right, out bool gentle, out bool uturn);
                                                float curviness = -1;
                                                if (CreateNodeLane(chunkIndex, ref idx, ref random4, ref curviness, entity3, laneBuffer, middleConnections, cs, ct, group, 0, connection.isUnsafe, false, false, (connection.method & (PathMethod.Road | PathMethod.Track)) == PathMethod.Track, yield2, ownerTemp3, isTurn, right, gentle, uturn, false, false, false, false, false, false))
                                                {
                                                    createdConnections.Add(key);
                                                    tempMainConnectionKeys.Add(new ConnectionKey(cs.m_Owner.Index, cs.m_LaneData.m_Group, ct.m_Owner.Index, ct.m_LaneData.m_Group));
                                                    Logger.Debug($"Added CustomGenerated ({key.GetString()}) to created! [ {cs.m_GroupIndex} | {ct.m_GroupIndex} -> {group} || G: [{(byte)(group)}|{(byte)(group >> 8)}] | [{(byte)(group >> 16)}|{(byte)(group >> 24)}] <= {group}");
                                                }    
                                            }
                                        }
                                    }
                                    tempSourceConnectPositions.Clear();

                                    /*
                                     * TODO insert lane generator, by sourceMainCarPos
                                     */
                                    for (int num13 = 0; num13 < targetMainCarConnectPositions.Length; num13++)
                                    {
                                        ConnectPosition targetPosition = targetMainCarConnectPositions[num13];
                                        bool isUnsafe = false;
                                        bool isForbidden = false;
                                        for (int num14 = 0; num14 < tempTargetConnectPositions.Length; num14++)
                                        {
                                            ConnectPosition value = tempTargetConnectPositions[num14];
                                            if (value.m_Owner == targetPosition.m_Owner && value.m_LaneData.m_Group == targetPosition.m_LaneData.m_Group)
                                            {
                                                if (value.m_ForbiddenCount != 0)
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
                                            if (testKeys && !tempMainConnectionKeys.Contains(new ConnectionKey(sourceMainCarConnectPos.m_Owner.Index, sourceMainCarConnectPos.m_LaneData.m_Group, targetPosition.m_Owner.Index, targetPosition.m_LaneData.m_Group)))
                                            {
                                                Logger.Debug($"Skipped Master Lane connection! {new ConnectionKey(sourceMainCarConnectPos, targetPosition).GetString()} [ {sourceMainCarConnectPos.m_GroupIndex}[{sourceMainCarConnectPos.m_GroupIndex >> 8}] ({sourceMainCarConnectPos.m_LaneData.m_Group}) | {targetPosition.m_GroupIndex}[{targetPosition.m_GroupIndex >> 8}] ({targetPosition.m_LaneData.m_Group})] G: [{(byte)(group)}|{(byte)(group >> 8)}] | [{(byte)(group >> 16)}|{(byte)(group >> 24)}] <= {group}");
                                                continue;
                                            }
                                            
                                            bool isTurn = IsTurn(sourceMainCarConnectPos, targetPosition, out bool right, out bool gentle, out bool uturn);
                                            float curviness = -1f;
                                            if (CreateNodeLane(chunkIndex, ref nodeLaneIndex, ref random4, ref curviness, entity3, laneBuffer, middleConnections, sourceMainCarConnectPos, targetPosition, group, 0, isUnsafe,
                                                isForbidden, tempComponents.Length != 0, trackOnly: false, 0, ownerTemp3, isTurn, right, gentle, uturn, isRoundabout: false, isLeftLimit: false, isRightLimit: false,
                                                isMergeLeft: false, isMergeRight: false, fixedTangents: false))
                                            {
                                                Logger.Debug($"Added Master Lane connection {new ConnectionKey(sourceMainCarConnectPos, targetPosition).GetString()} [ {sourceMainCarConnectPos.m_GroupIndex}[{sourceMainCarConnectPos.m_GroupIndex >> 8}] ({sourceMainCarConnectPos.m_LaneData.m_Group}) | {targetPosition.m_GroupIndex}[{targetPosition.m_GroupIndex >> 8}] ({targetPosition.m_LaneData.m_Group})] G: [{(byte)(group)}|{(byte)(group >> 8)}] | [{(byte)(group >> 16)}|{(byte)(group >> 24)}] <= {group}");
                                                createdConnections.Add(new ConnectionKey(sourceMainCarConnectPos, targetPosition));
                                            }
                                            // Logger.Debug("Adding Master Lane connections done!");
                                        }
                                    }
                                    prevLaneIndex += 256;
                                }
                                tempTargetConnectPositions.Clear();

                                
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
                            FilterPedestrianConnectPositions(targetNodeConnectPositions, sourceMainCarConnectPositions, middleConnections);
                            CreateNodePedestrianLanes(chunkIndex, ref prevLaneIndex, ref random4, entity3, laneBuffer, sourceMainCarConnectPositions, targetMainCarConnectPositions, tempTargetConnectPositions, tempComponents.Length != 0, ownerTemp3, position, middleRadius2, roundaboutSize2);
                            sourceMainCarConnectPositions.Clear();
                            targetMainCarConnectPositions.Clear();
                            tempTargetConnectPositions.Clear();
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
                                        CreateNodeUtilityLanes(chunkIndex, ref prevLaneIndex, ref random4, entity3, laneBuffer, tempTargetConnectPositions, tempSourceConnectPositions, middleConnections, tempComponents.Length != 0, ownerTemp3);
                                        tempTargetConnectPositions.Clear();
                                        tempSourceConnectPositions.Clear();
                                    }
                                }
                                utilityTypes2 = (UtilityTypes)((uint)utilityTypes2 << 1);
                            }
                            CreateNodeConnectionLanes(chunkIndex, ref prevLaneIndex, ref random4, entity3, laneBuffer, middleConnections, tempTargetConnectPositions, middleRadius2 > 0f, tempComponents.Length != 0, ownerTemp3);
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
                        RemoveUnusedOldLanes(chunkIndex, lanes5, laneBuffer.m_OldLanes);
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
                laneBuffer.Dispose();
            }

            private ConnectPosition FindNodeConnectPosition(NativeList<ConnectPosition> connectPositions, Entity owner, int laneIndex, out int index) {
                index = -1;
                for (var i = 0; i < connectPositions.Length; i++)
                {
                    ConnectPosition p = connectPositions[i];
                    if (p.m_Owner == owner && p.m_LaneData.m_Index == laneIndex)
                    {
                        index = i;
                        return p;
                    }
                }
                return new ConnectPosition();
            }

            private void CreateOrphanLanes(int jobIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, Orphan orphan, float3 middlePosition, Entity prefab, ref int nodeLaneIndex, bool isTemp, Temp ownerTemp) {
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
                    CreateNodePedestrianLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, connectPosition2, connectPosition, pathNode, pathNode, isCrosswalk: false, isSideConnection: true, isTemp,
                        ownerTemp, fixedTangents: false, hasSignals: false, out Bezier4x3 curve, out PathNode middleNode, out PathNode endNode);
                    connectPosition.m_Tangent = -connectPosition.m_Tangent;
                    connectPosition2.m_Tangent = -connectPosition2.m_Tangent;
                    CreateNodePedestrianLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, connectPosition, connectPosition2, endNode, pathNode, isCrosswalk: false, isSideConnection: true, isTemp,
                        ownerTemp, fixedTangents: false, hasSignals: false, out curve, out middleNode, out PathNode _);
                }
            }

            private int GetRoundaboutLaneCount(ConnectPosition prevMainPosition, ConnectPosition nextMainPosition, NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer, NativeList<ConnectPosition> allTargets, float3 middlePosition)
            {
                float2 fromVector = math.normalizesafe(prevMainPosition.m_Position.xz - middlePosition.xz);
                float2 toVector = math.normalizesafe(nextMainPosition.m_Position.xz - middlePosition.xz);
                float num = (!prevMainPosition.m_Position.Equals(nextMainPosition.m_Position))
                    ? (m_LeftHandTraffic ? MathUtils.RotationAngleRight(fromVector, toVector) : MathUtils.RotationAngleLeft(fromVector, toVector))
                    : (math.PI * 2f);
                int num2 = math.max(1, Mathf.CeilToInt(num * (2f / math.PI) - 0.003141593f));
                int num3 = math.max(1, sourceBuffer.Length);
                if (num2 == 1)
                {
                    if (sourceBuffer.Length > 0 && targetBuffer.Length > 0)
                    {
                        int y = math.clamp(sourceBuffer.Length + targetBuffer.Length - GetRoundaboutTargetLaneCount(sourceBuffer[sourceBuffer.Length - 1], allTargets), 0,
                            math.min(targetBuffer.Length, sourceBuffer.Length - 1));
                        y = math.min(1, y);
                        return num3 - y;
                    }
                    return num3;
                }
                int num4 = targetBuffer.Length - math.select(1, 0, targetBuffer.Length <= 1);
                return math.max(1, num3 - num4);
            }

            private int GetRoundaboutTargetLaneCount(ConnectPosition sourcePosition, NativeList<ConnectPosition> allTargets) {
                int num = 0;
                for (int i = 0; i < allTargets.Length; i++)
                {
                    ConnectPosition targetPosition = allTargets[i];
                    if ((targetPosition.m_LaneData.m_Flags & (LaneFlags.Master | LaneFlags.Road)) == LaneFlags.Road)
                    {
                        IsTurn(sourcePosition, targetPosition, out bool _, out bool _, out bool uturn);
                        num += math.select(1, 0, uturn);
                    }
                }
                return num;
            }

            private void CreateRoundaboutCarLanes(int jobIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, ref int prevLaneIndex, int nextLaneIndex, ref uint laneGroup,
                ConnectPosition prevMainPosition, ConnectPosition nextMainPosition, NativeList<MiddleConnection> middleConnections, NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer,
                NativeList<ConnectPosition> allTargets, ConnectPosition lane, float3 middlePosition, float middleRadius, ref int laneCount, int totalLaneCount, float spaceForLanes, bool isTemp, Temp ownerTemp
            ) {
                float2 @float = math.normalizesafe(prevMainPosition.m_Position.xz - middlePosition.xz);
                float2 float2 = math.normalizesafe(nextMainPosition.m_Position.xz - middlePosition.xz);
                float num = (!prevMainPosition.m_Position.Equals(nextMainPosition.m_Position))
                    ? (m_LeftHandTraffic ? MathUtils.RotationAngleRight(@float, float2) : MathUtils.RotationAngleLeft(@float, float2))
                    : (math.PI * 2f);
                int num2 = math.max(1, Mathf.CeilToInt(num * (2f / math.PI) - 0.003141593f));
                float2 float3 = @float;
                ConnectPosition connectPosition = default(ConnectPosition);
                connectPosition.m_LaneData.m_Lane = lane.m_LaneData.m_Lane;
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
                int num3 = 0;
                int num4 = targetBuffer.Length - math.select(1, 0, targetBuffer.Length <= 1);
                if (num2 == 1 && sourceBuffer.Length > 0 && targetBuffer.Length > 0)
                {
                    num3 = math.clamp(sourceBuffer.Length + targetBuffer.Length - GetRoundaboutTargetLaneCount(sourceBuffer[sourceBuffer.Length - 1], allTargets), 0, math.min(targetBuffer.Length, sourceBuffer.Length - 1));
                    if ((num3 == 0) & (sourceBuffer.Length < totalLaneCount))
                    {
                        int num5 = math.max(1, laneCount - num4);
                        num3 = math.select(0, 1, num5 + sourceBuffer.Length >= totalLaneCount);
                    }
                    num3 = math.min(1, num3);
                }
                for (int i = 1; i <= num2; i++)
                {
                    int num6 = math.max(1, math.select(laneCount - num4, laneCount, i != num2));
                    int num7 = math.select(math.min(totalLaneCount, num6 + sourceBuffer.Length) - num3, num6, i != 1);
                    int nodeLaneIndex = prevLaneIndex + totalLaneCount + 2;
                    prevLaneIndex += 256;
                    float2 float4;
                    if (i == num2)
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
                        float num8 = (float)i / (float)num2;
                        float4 = (m_LeftHandTraffic ? MathUtils.RotateRight(float3, num * num8) : MathUtils.RotateLeft(float3, num * num8));
                        connectPosition2.m_CompositionData.m_SpeedLimit = math.lerp(prevMainPosition.m_CompositionData.m_SpeedLimit, nextMainPosition.m_CompositionData.m_SpeedLimit, num8);
                        connectPosition2.m_BaseHeight = middlePosition.y + math.lerp(prevMainPosition.m_BaseHeight, nextMainPosition.m_BaseHeight, num8) - math.lerp(prevMainPosition.m_Position.y, nextMainPosition.m_Position.y, num8);
                        connectPosition2.m_SegmentIndex = (byte)(prevLaneIndex >> 8);
                        connectPosition2.m_Position.y = middlePosition.y;
                    }
                    float2 float5 = m_LeftHandTraffic ? MathUtils.RotateRight(float3, num * ((float)i - 0.5f) / (float)num2) : MathUtils.RotateLeft(float3, num * ((float)i - 0.5f) / (float)num2);
                    connectPosition2.m_Tangent.xz = (m_LeftHandTraffic ? MathUtils.Left(float4) : MathUtils.Right(float4));
                    float3 float6 = default(float3);
                    float6.xz = (m_LeftHandTraffic ? MathUtils.Right(float5) : MathUtils.Left(float5));
                    float3 centerPosition = default(float3);
                    centerPosition.y = math.lerp(connectPosition.m_Position.y, connectPosition2.m_Position.y, 0.5f);
                    bool flag = laneCount >= 2;
                    bool flag2 = num7 >= 2;
                    bool flag3 = i == 1 && sourceBuffer.Length >= 1;
                    bool flag4 = i == num2 && targetBuffer.Length >= 1;
                    bool flag5 = num2 == 1 && sourceBuffer.Length >= 1 && targetBuffer.Length >= 1;
                    float curviness = -1f;
                    for (int j = 0; j < num7; j++)
                    {
                        int num9 = math.select(j, num7 - j - 1, m_LeftHandTraffic);
                        int num10 = math.max(0, j - num7 + num6);
                        float rhs = middleRadius + ((float)num10 + 0.5f) * spaceForLanes / (float)totalLaneCount;
                        float rhs2 = middleRadius + ((float)j + 0.5f) * spaceForLanes / (float)totalLaneCount;
                        connectPosition.m_Position.xz = middlePosition.xz + @float * rhs;
                        connectPosition.m_LaneData.m_Index = (byte)num10;
                        connectPosition.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary));
                        if (flag)
                        {
                            connectPosition.m_LaneData.m_Flags |= LaneFlags.Slave;
                        }
                        connectPosition2.m_Position.xz = middlePosition.xz + float4 * rhs2;
                        connectPosition2.m_LaneData.m_Index = (byte)j;
                        connectPosition2.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary));
                        if (flag2)
                        {
                            connectPosition2.m_LaneData.m_Flags |= LaneFlags.Slave;
                        }
                        bool a = j == 0;
                        bool b = j == num7 - 1 && i > 1 && i < num2;
                        if (m_LeftHandTraffic)
                        {
                            CommonUtils.Swap(ref a, ref b);
                        }
                        if (num10 != j)
                        {
                            ConnectPosition sourcePosition = connectPosition;
                            ConnectPosition targetPosition = connectPosition2;
                            float3 position = targetPosition.m_Position;
                            position.xz = middlePosition.xz + float4 * rhs;
                            Bezier4x3 bezier4x = NetUtils.FitCurve(sourcePosition.m_Position, sourcePosition.m_Tangent, -targetPosition.m_Tangent, position);
                            sourcePosition.m_Tangent = bezier4x.b - bezier4x.a;
                            targetPosition.m_Tangent = MathUtils.Normalize(targetPosition.m_Tangent, targetPosition.m_Tangent.xz);
                            CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, owner, laneBuffer, middleConnections, sourcePosition, targetPosition, laneGroup, (ushort)num9, isUnsafe: false,
                                isForbidden: false, isTemp, trackOnly: false, 0, ownerTemp, isTurn: false, isRight: false, isGentle: false, isUTurn: false, isRoundabout: true, a, b, isMergeLeft: false,
                                isMergeRight: false, fixedTangents: true);
                        }
                        else
                        {
                            curviness = -1f;
                            CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, owner, laneBuffer, middleConnections, connectPosition, connectPosition2, laneGroup, (ushort)num9, isUnsafe: false,
                                isForbidden: false, isTemp, trackOnly: false, 0, ownerTemp, isTurn: false, isRight: false, isGentle: false, isUTurn: false, isRoundabout: true, a, b, isMergeLeft: false,
                                isMergeRight: false, fixedTangents: false);
                        }
                    }
                    if (!isTemp && (flag || flag2))
                    {
                        float rhs3 = middleRadius + (float)laneCount * 0.5f * spaceForLanes / (float)totalLaneCount;
                        float rhs4 = middleRadius + (float)num7 * 0.5f * spaceForLanes / (float)totalLaneCount;
                        connectPosition.m_Position.xz = middlePosition.xz + @float * rhs3;
                        connectPosition.m_LaneData.m_Index = (byte)math.select(0, laneCount, flag);
                        connectPosition.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary));
                        connectPosition.m_LaneData.m_Flags |= LaneFlags.Master;
                        connectPosition2.m_Position.xz = middlePosition.xz + float4 * rhs4;
                        connectPosition2.m_LaneData.m_Index = (byte)math.select(0, num7, flag2);
                        connectPosition2.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary));
                        connectPosition2.m_LaneData.m_Flags |= LaneFlags.Master;
                        curviness = -1f;
                        CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, owner, laneBuffer, middleConnections, connectPosition, connectPosition2, laneGroup, 0, isUnsafe: false, isForbidden: false,
                            isTemp, trackOnly: false, 0, ownerTemp, isTurn: false, isRight: false, isGentle: false, isUTurn: false, isRoundabout: true, isLeftLimit: false, isRightLimit: false, isMergeLeft: false,
                            isMergeRight: false, fixedTangents: false);
                    }
                    laneGroup++;
                    float num11 = 0f;
                    Curve curve;
                    if (flag3)
                    {
                        bool flag6 = flag2;
                        for (int k = 0; k < num7; k++)
                        {
                            int num12 = math.select(k, num7 - k - 1, m_LeftHandTraffic);
                            int num13 = math.max(0, k + math.min(0, sourceBuffer.Length - num7));
                            num13 = math.select(num13, sourceBuffer.Length - num13 - 1, m_LeftHandTraffic);
                            float num14 = middleRadius + ((float)(k + totalLaneCount - math.max(sourceBuffer.Length, num7)) + 0.5f) * spaceForLanes / (float)totalLaneCount;
                            centerPosition.xz = middlePosition.xz + float5 * num14;
                            float rhs5 = middleRadius + ((float)k + 0.5f) * spaceForLanes / (float)totalLaneCount;
                            connectPosition2.m_Position.xz = middlePosition.xz + float4 * rhs5;
                            connectPosition2.m_LaneData.m_Index = (byte)k;
                            connectPosition2.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary));
                            if (flag2)
                            {
                                connectPosition2.m_LaneData.m_Flags |= LaneFlags.Slave;
                            }
                            bool a2 = false;
                            bool b2 = k == num7 - 1 && i < num2;
                            if (m_LeftHandTraffic)
                            {
                                CommonUtils.Swap(ref a2, ref b2);
                            }
                            ConnectPosition sourcePosition2 = sourceBuffer[num13];
                            ConnectPosition targetPosition2 = connectPosition2;
                            flag6 |= ((sourcePosition2.m_LaneData.m_Flags & LaneFlags.Slave) != 0);
                            PresetCurve(ref sourcePosition2, ref targetPosition2, middlePosition, centerPosition, float6, num14, 0f, num / (float)num2, 2f);
                            Bezier4x3 bezier4x2 = new Bezier4x3(sourcePosition2.m_Position, sourcePosition2.m_Position + sourcePosition2.m_Tangent, targetPosition2.m_Position + targetPosition2.m_Tangent,
                                targetPosition2.m_Position);
                            num11 = math.max(num11, math.distance(MathUtils.Position(bezier4x2, 0.5f).xz, middlePosition.xz));
                            curve = default(Curve);
                            curve.m_Bezier = bezier4x2;
                            curve.m_Length = 1f;
                            curviness = NetUtils.CalculateStartCurviness(curve, m_NetLaneData[sourcePosition2.m_LaneData.m_Lane].m_Width);
                            CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, owner, laneBuffer, middleConnections, sourcePosition2, targetPosition2, laneGroup, (ushort)num12, isUnsafe: false,
                                isForbidden: false, isTemp, trackOnly: false, 1, ownerTemp, isTurn: false, isRight: false, isGentle: false, isUTurn: false, isRoundabout: true, a2, b2, isMergeLeft: false,
                                isMergeRight: false, fixedTangents: true);
                        }
                        if (flag6)
                        {
                            float x = middleRadius + ((float)(totalLaneCount - math.max(sourceBuffer.Length, num7)) + 0.5f) * spaceForLanes / (float)totalLaneCount;
                            float y = middleRadius + ((float)(num7 - 1 + totalLaneCount - math.max(sourceBuffer.Length, num7)) + 0.5f) * spaceForLanes / (float)totalLaneCount;
                            float num15 = math.lerp(x, y, 0.5f);
                            centerPosition.xz = middlePosition.xz + float5 * num15;
                            float rhs6 = middleRadius + (float)num7 * 0.5f * spaceForLanes / (float)totalLaneCount;
                            connectPosition2.m_Position.xz = middlePosition.xz + float4 * rhs6;
                            connectPosition2.m_LaneData.m_Index = (byte)math.select(0, num7, flag2);
                            connectPosition2.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary));
                            connectPosition2.m_LaneData.m_Flags |= LaneFlags.Master;
                            ConnectPosition sourcePosition3 = prevMainPosition;
                            ConnectPosition targetPosition3 = connectPosition2;
                            PresetCurve(ref sourcePosition3, ref targetPosition3, middlePosition, centerPosition, float6, num15, 0f, num / (float)num2, 2f);
                            curviness = -1f;
                            CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, owner, laneBuffer, middleConnections, sourcePosition3, targetPosition3, laneGroup, 0, isUnsafe: false,
                                isForbidden: false, isTemp, trackOnly: false, 1, ownerTemp, isTurn: false, isRight: false, isGentle: false, isUTurn: false, isRoundabout: true, isLeftLimit: false, isRightLimit: false,
                                isMergeLeft: false, isMergeRight: false, fixedTangents: true);
                        }
                        laneGroup++;
                    }
                    if (flag4)
                    {
                        bool flag7 = flag;
                        for (int l = 0; l < targetBuffer.Length; l++)
                        {
                            int num16 = math.select(l, targetBuffer.Length - l - 1, m_LeftHandTraffic);
                            int num17 = math.min(laneCount - 1, l + math.max(0, laneCount - targetBuffer.Length));
                            float num18 = middleRadius + ((float)math.min(totalLaneCount - 1, l + math.max(0, totalLaneCount - targetBuffer.Length)) + 0.5f) * spaceForLanes / (float)totalLaneCount;
                            centerPosition.xz = middlePosition.xz + float5 * num18;
                            float rhs7 = middleRadius + ((float)num17 + 0.5f) * spaceForLanes / (float)totalLaneCount;
                            connectPosition.m_Position.xz = middlePosition.xz + @float * rhs7;
                            connectPosition.m_LaneData.m_Index = (byte)num17;
                            connectPosition.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary));
                            if (flag)
                            {
                                connectPosition.m_LaneData.m_Flags |= LaneFlags.Slave;
                            }
                            bool a3 = false;
                            bool b3 = l == targetBuffer.Length - 1 && i > 1;
                            if (m_LeftHandTraffic)
                            {
                                CommonUtils.Swap(ref a3, ref b3);
                            }
                            ConnectPosition sourcePosition4 = connectPosition;
                            ConnectPosition targetPosition4 = targetBuffer[targetBuffer.Length - 1 - num16];
                            flag7 |= ((targetPosition4.m_LaneData.m_Flags & LaneFlags.Slave) != 0);
                            PresetCurve(ref sourcePosition4, ref targetPosition4, middlePosition, centerPosition, float6, num18, num / (float)num2, 0f, 2f);
                            Bezier4x3 bezier4x3 = new Bezier4x3(sourcePosition4.m_Position, sourcePosition4.m_Position + sourcePosition4.m_Tangent, targetPosition4.m_Position + targetPosition4.m_Tangent,
                                targetPosition4.m_Position);
                            num11 = math.max(num11, math.distance(MathUtils.Position(bezier4x3, 0.5f).xz, middlePosition.xz));
                            bool flag8 = l >= laneCount;
                            curve = default(Curve);
                            curve.m_Bezier = bezier4x3;
                            curve.m_Length = 1f;
                            curviness = NetUtils.CalculateEndCurviness(curve, m_NetLaneData[targetPosition4.m_LaneData.m_Lane].m_Width);
                            CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, owner, laneBuffer, middleConnections, sourcePosition4, targetPosition4, laneGroup, (ushort)num16, flag8,
                                isForbidden: false, isTemp, trackOnly: false, -1, ownerTemp, isTurn: true, isRight: !m_LeftHandTraffic, isGentle: true, isUTurn: false, isRoundabout: true, a3, b3, flag8 && m_LeftHandTraffic,
                                flag8 && !m_LeftHandTraffic, fixedTangents: true);
                        }
                        if (flag7)
                        {
                            float x2 = middleRadius + ((float)math.min(totalLaneCount - 1, math.max(0, totalLaneCount - targetBuffer.Length)) + 0.5f) * spaceForLanes / (float)totalLaneCount;
                            float y2 = middleRadius + ((float)math.min(totalLaneCount - 1, targetBuffer.Length - 1 + math.max(0, totalLaneCount - targetBuffer.Length)) + 0.5f) * spaceForLanes / (float)totalLaneCount;
                            float num19 = math.lerp(x2, y2, 0.5f);
                            centerPosition.xz = middlePosition.xz + float5 * num19;
                            float rhs8 = middleRadius + (float)laneCount * 0.5f * spaceForLanes / (float)totalLaneCount;
                            connectPosition.m_Position.xz = middlePosition.xz + @float * rhs8;
                            connectPosition.m_LaneData.m_Index = (byte)math.select(0, laneCount, flag);
                            connectPosition.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary));
                            connectPosition.m_LaneData.m_Flags |= LaneFlags.Master;
                            ConnectPosition sourcePosition5 = connectPosition;
                            ConnectPosition targetPosition5 = nextMainPosition;
                            PresetCurve(ref sourcePosition5, ref targetPosition5, middlePosition, centerPosition, float6, num19, num / (float)num2, 0f, 2f);
                            curviness = -1f;
                            CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, owner, laneBuffer, middleConnections, sourcePosition5, targetPosition5, laneGroup, 0, isUnsafe: false,
                                isForbidden: false, isTemp, trackOnly: false, -1, ownerTemp, isTurn: true, isRight: !m_LeftHandTraffic, isGentle: true, isUTurn: false, isRoundabout: true, isLeftLimit: false, isRightLimit: false,
                                isMergeLeft: false, isMergeRight: false, fixedTangents: true);
                        }
                        laneGroup++;
                    }
                    if (flag5)
                    {
                        bool flag9 = false;
                        bool flag10 = false;
                        float x3 = middleRadius + ((float)totalLaneCount - 0.5f) * spaceForLanes / (float)totalLaneCount;
                        x3 = math.lerp(x3, math.max(x3, num11), 0.5f);
                        float2 float7 = m_LeftHandTraffic ? MathUtils.RotateRight(float3, num * ((float)i - 0.75f) / (float)num2) : MathUtils.RotateLeft(float3, num * ((float)i - 0.75f) / (float)num2);
                        float2 float8 = m_LeftHandTraffic ? MathUtils.RotateRight(float3, num * ((float)i - 0.25f) / (float)num2) : MathUtils.RotateLeft(float3, num * ((float)i - 0.25f) / (float)num2);
                        float3 centerTangent = default(float3);
                        float3 centerTangent2 = default(float3);
                        centerTangent.xz = (m_LeftHandTraffic ? MathUtils.Right(float7) : MathUtils.Left(float7));
                        centerTangent2.xz = (m_LeftHandTraffic ? MathUtils.Right(float8) : MathUtils.Left(float8));
                        int num20 = sourceBuffer.Length - 1;
                        int index = math.select(num20, sourceBuffer.Length - num20 - 1, m_LeftHandTraffic);
                        int num21 = 0;
                        int index2 = math.select(num21, targetBuffer.Length - num21 - 1, m_LeftHandTraffic);
                        ConnectPosition connectPosition3 = sourceBuffer[index];
                        ConnectPosition targetPosition6 = targetBuffer[index2];
                        float t;
                        float y3 = MathUtils.Distance(NetUtils.FitCurve(connectPosition3.m_Position, connectPosition3.m_Tangent, -targetPosition6.m_Tangent, targetPosition6.m_Position).xz, middlePosition.xz, out t);
                        x3 = math.max(x3, y3);
                        ConnectPosition connectPosition4 = default(ConnectPosition);
                        connectPosition4.m_LaneData.m_Lane = lane.m_LaneData.m_Lane;
                        connectPosition4.m_NodeComposition = lane.m_NodeComposition;
                        connectPosition4.m_EdgeComposition = lane.m_EdgeComposition;
                        connectPosition4.m_CompositionData = lane.m_CompositionData;
                        connectPosition4.m_Owner = owner;
                        connectPosition4.m_CompositionData.m_SpeedLimit = math.lerp(connectPosition.m_CompositionData.m_SpeedLimit, connectPosition2.m_CompositionData.m_SpeedLimit, 0.5f);
                        connectPosition4.m_BaseHeight = math.lerp(connectPosition.m_BaseHeight, connectPosition2.m_BaseHeight, 0.5f);
                        connectPosition4.m_SegmentIndex = connectPosition2.m_SegmentIndex;
                        connectPosition4.m_Tangent = float6;
                        connectPosition4.m_Position.y = centerPosition.y;
                        connectPosition4.m_LaneData.m_Index = (byte)(num7 + 1);
                        connectPosition4.m_LaneData.m_Flags = (lane.m_LaneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.PublicOnly | LaneFlags.HasAuxiliary));
                        connectPosition4.m_Position.xz = middlePosition.xz + float5 * x3;
                        float3 centerPosition2 = middlePosition;
                        centerPosition2.y = math.lerp(connectPosition.m_Position.y, centerPosition.y, 0.5f);
                        centerPosition2.xz += float7 * x3;
                        float3 centerPosition3 = middlePosition;
                        centerPosition3.y = math.lerp(centerPosition.y, connectPosition2.m_Position.y, 0.5f);
                        centerPosition3.xz += float8 * x3;
                        for (int m = 0; m < targetBuffer.Length; m++)
                        {
                            int num22 = math.select(m, targetBuffer.Length - m - 1, m_LeftHandTraffic);
                            num21 = targetBuffer.Length - 1 - m;
                            index2 = math.select(num21, targetBuffer.Length - num21 - 1, m_LeftHandTraffic);
                            if (m == 0)
                            {
                                bool a4 = false;
                                bool b4 = true;
                                if (m_LeftHandTraffic)
                                {
                                    CommonUtils.Swap(ref a4, ref b4);
                                }
                                connectPosition3 = sourceBuffer[index];
                                targetPosition6 = connectPosition4;
                                flag9 |= ((connectPosition3.m_LaneData.m_Flags & LaneFlags.Slave) != 0);
                                targetPosition6.m_Tangent = -targetPosition6.m_Tangent;
                                PresetCurve(ref connectPosition3, ref targetPosition6, middlePosition, centerPosition2, centerTangent, x3, 0f, num * 0.5f / (float)num2, 2f);
                                curviness = -1f;
                                CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, owner, laneBuffer, middleConnections, connectPosition3, targetPosition6, laneGroup, (ushort)num22, isUnsafe: false,
                                    isForbidden: false, isTemp, trackOnly: false, 1, ownerTemp, isTurn: true, isRight: !m_LeftHandTraffic, isGentle: false, isUTurn: false, isRoundabout: true, a4, b4, isMergeLeft: false,
                                    isMergeRight: false, fixedTangents: true);
                            }
                            bool a5 = false;
                            bool b5 = m == targetBuffer.Length - 1;
                            if (m_LeftHandTraffic)
                            {
                                CommonUtils.Swap(ref a5, ref b5);
                            }
                            connectPosition3 = connectPosition4;
                            targetPosition6 = targetBuffer[index2];
                            flag10 |= ((targetPosition6.m_LaneData.m_Flags & LaneFlags.Slave) != 0);
                            PresetCurve(ref connectPosition3, ref targetPosition6, middlePosition, centerPosition3, centerTangent2, x3, num * 0.5f / (float)num2, 0f, 2f);
                            bool flag11 = num21 != 0;
                            curviness = -1f;
                            CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, owner, laneBuffer, middleConnections, connectPosition3, targetPosition6, laneGroup + 1, (ushort)num22, flag11,
                                isForbidden: false, isTemp, trackOnly: false, 0, ownerTemp, isTurn: true, isRight: !m_LeftHandTraffic, isGentle: false, isUTurn: false, isRoundabout: true, a5, b5, flag11 && !m_LeftHandTraffic,
                                flag11 && m_LeftHandTraffic, fixedTangents: true);
                        }
                        if (flag9)
                        {
                            connectPosition3 = prevMainPosition;
                            targetPosition6 = connectPosition4;
                            targetPosition6.m_Tangent = -targetPosition6.m_Tangent;
                            PresetCurve(ref connectPosition3, ref targetPosition6, middlePosition, centerPosition2, centerTangent, x3, 0f, num * 0.5f / (float)num2, 2f);
                            curviness = -1f;
                            CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, owner, laneBuffer, middleConnections, connectPosition3, targetPosition6, laneGroup, 0, isUnsafe: false,
                                isForbidden: false, isTemp, trackOnly: false, 1, ownerTemp, isTurn: true, isRight: !m_LeftHandTraffic, isGentle: false, isUTurn: false, isRoundabout: true, isLeftLimit: false, isRightLimit: false,
                                isMergeLeft: false, isMergeRight: false, fixedTangents: true);
                        }
                        if (flag10)
                        {
                            connectPosition3 = connectPosition4;
                            targetPosition6 = nextMainPosition;
                            PresetCurve(ref connectPosition3, ref targetPosition6, middlePosition, centerPosition3, centerTangent2, x3, num * 0.5f / (float)num2, 0f, 2f);
                            curviness = -1f;
                            CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, owner, laneBuffer, middleConnections, connectPosition3, targetPosition6, laneGroup + 1, 0, isUnsafe: false,
                                isForbidden: false, isTemp, trackOnly: false, 0, ownerTemp, isTurn: true, isRight: !m_LeftHandTraffic, isGentle: false, isUTurn: false, isRoundabout: true, isLeftLimit: false, isRightLimit: false,
                                isMergeLeft: false, isMergeRight: false, fixedTangents: true);
                        }
                        laneGroup += 2u;
                    }
                    @float = float4;
                    connectPosition = connectPosition2;
                    connectPosition.m_Tangent = -connectPosition.m_Tangent;
                    laneCount = num7;
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

            private bool GetRoundaboutLane(NativeList<ConnectPosition> buffer, float roundaboutSize, ref ConnectPosition roundaboutLane, ref int laneCount, ref float laneWidth, ref bool isPublicOnly, ref float spaceForLanes, bool isSource, bool preferHighway
            ) {
                bool result = false;
                if (buffer.Length > 0)
                {
                    ConnectPosition connectPosition = buffer[0];
                    NetCompositionData netCompositionData = m_PrefabCompositionData[connectPosition.m_NodeComposition];
                    float num = connectPosition.m_IsEnd ? netCompositionData.m_RoundaboutSize.y : netCompositionData.m_RoundaboutSize.x;
                    float num2 = roundaboutSize - num;
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        ConnectPosition connectPosition2 = buffer[i];
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
                            bool flag = (netLaneData.m_Flags & LaneFlags.PublicOnly) != 0;
                            if ((isPublicOnly && !flag) | ((isPublicOnly == flag) & (netLaneData.m_Width < laneWidth)))
                            {
                                laneWidth = netLaneData.m_Width;
                                isPublicOnly = flag;
                                roundaboutLane = connectPosition2;
                            }
                        }
                    }
                    int num3 = math.select(1, buffer.Length, isSource);
                    result = (num3 > laneCount);
                    laneCount = math.max(laneCount, num3);
                    spaceForLanes = math.min(spaceForLanes, num2);
                }
                return result;
            }

            private void FillOldLaneBuffer(DynamicBuffer<SubLane> lanes, NativeParallelHashMap<LaneKey, Entity> laneBuffer) {
                for (int i = 0; i < lanes.Length; i++)
                {
                    Entity subLane = lanes[i].m_SubLane;
                    if (!m_SecondaryLaneData.HasComponent(subLane))
                    {
                        LaneFlags laneFlags = (LaneFlags)0;
                        if (m_MasterLaneData.HasComponent(subLane))
                        {
                            laneFlags |= LaneFlags.Master;
                        }
                        if (m_SlaveLaneData.HasComponent(subLane))
                        {
                            laneFlags |= LaneFlags.Slave;
                        }
                        LaneKey key = new LaneKey(m_LaneData[subLane], m_PrefabRefData[subLane].m_Prefab, laneFlags);
                        laneBuffer.TryAdd(key, subLane);
                    }
                }
            }

            private void RemoveUnusedOldLanes(int jobIndex, DynamicBuffer<SubLane> lanes, NativeParallelHashMap<LaneKey, Entity> laneBuffer) {
                for (int i = 0; i < lanes.Length; i++)
                {
                    Entity subLane = lanes[i].m_SubLane;
                    if (!m_SecondaryLaneData.HasComponent(subLane))
                    {
                        LaneFlags laneFlags = (LaneFlags)0;
                        if (m_MasterLaneData.HasComponent(subLane))
                        {
                            laneFlags |= LaneFlags.Master;
                        }
                        if (m_SlaveLaneData.HasComponent(subLane))
                        {
                            laneFlags |= LaneFlags.Slave;
                        }
                        LaneKey key = new LaneKey(m_LaneData[subLane], m_PrefabRefData[subLane].m_Prefab, laneFlags);
                        if (laneBuffer.TryGetValue(key, out Entity _))
                        {
                            m_CommandBuffer.RemoveComponent(jobIndex, subLane, in m_AppliedTypes);
                            m_CommandBuffer.AddComponent(jobIndex, subLane, default(Deleted));
                            laneBuffer.Remove(key);
                        }
                    }
                }
            }

            private void CreateEdgeConnectionLanes(int jobIndex, ref int edgeLaneIndex, ref int connectionIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, NativeList<ConnectPosition> sourceBuffer,
                NativeList<ConnectPosition> targetBuffer, NativeList<ConnectPosition> tempBuffer1, NativeList<ConnectPosition> tempBuffer2, Entity composition, EdgeGeometry geometryData, NetGeometryData prefabGeometryData, Curve curve, bool isTemp,
                Temp ownerTemp
            ) {
                NetCompositionData prefabCompositionData = m_PrefabCompositionData[composition];
                CompositionData compositionData = GetCompositionData(composition);
                DynamicBuffer<NetCompositionLane> prefabCompositionLanes = m_PrefabCompositionLanes[composition];
                int num = -1;
                for (int i = 0; i < sourceBuffer.Length; i++)
                {
                    ConnectPosition connectPosition = sourceBuffer[i];
                    if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Road) == 0)
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
                        CreateCarEdgeConnections(jobIndex, ref edgeLaneIndex, ref random, owner, laneBuffer, geometryData, prefabCompositionData, prefabGeometryData, compositionData, connectPosition, @float.xy,
                            connectionIndex++, isSource: true, isTemp, ownerTemp, prefabCompositionLanes, num3);
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
                            CreateEdgeConnectionLane(jobIndex, ref edgeLaneIndex, ref random, owner, laneBuffer, geometryData.m_Start, geometryData.m_End, prefabCompositionData, prefabGeometryData, compositionData,
                                prefabCompositionLaneData, connectPosition2, connectionIndex++, useGroundPosition: false, isSource: false, isTemp, ownerTemp);
                        }
                    }
                    if ((connectPosition2.m_LaneData.m_Flags & LaneFlags.Road) == 0)
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
                        CreateCarEdgeConnections(jobIndex, ref edgeLaneIndex, ref random, owner, laneBuffer, geometryData, prefabCompositionData, prefabGeometryData, compositionData, connectPosition2, float2.xy,
                            connectionIndex++, isSource: false, isTemp, ownerTemp, prefabCompositionLanes, num6);
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
                                CreateEdgeConnectionLane(jobIndex, ref edgeLaneIndex, ref random, owner, laneBuffer, geometryData.m_Start, geometryData.m_End, prefabCompositionData, prefabGeometryData, compositionData,
                                    prefabCompositionLaneData2, connectPosition4, connectionIndex++, useGroundPosition, isSource: false, isTemp, ownerTemp);
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
                NetGeometryData prefabGeometryData, bool isTemp, Temp ownerTemp
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
                        float s = netCompositionLane.m_Position.x / math.max(1f, prefabCompositionData.m_Width) + 0.5f;
                        value.m_Position = math.lerp(geometryData.m_Start.m_Left.a, geometryData.m_Start.m_Right.a, s);
                        value2.m_Position = math.lerp(geometryData.m_End.m_Left.d, geometryData.m_End.m_Right.d, s);
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
                if ((prefabGeometryData.m_Flags & (GeometryFlags.StraightEdges | GeometryFlags.SmoothSlopes)) == GeometryFlags.StraightEdges)
                {
                    Segment start = geometryData.m_Start;
                    start.m_Left = MathUtils.Join(geometryData.m_Start.m_Left, geometryData.m_End.m_Left);
                    start.m_Right = MathUtils.Join(geometryData.m_Start.m_Right, geometryData.m_End.m_Right);
                    start.m_Length = geometryData.m_Start.m_Length + geometryData.m_End.m_Length;
                    for (int j = 0; j < prefabCompositionLanes.Length; j++)
                    {
                        NetCompositionLane prefabCompositionLaneData = prefabCompositionLanes[j];
                        hasAuxiliaryLanes |= ((prefabCompositionLaneData.m_Flags & LaneFlags.HasAuxiliary) != 0);
                        CreateEdgeLane(jobIndex, ref random, owner, laneBuffer, start, prefabCompositionData, compositionData, prefabCompositionLanes, prefabCompositionLaneData, new int2(0, 4), new float2(0f, 1f),
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
                                    float t = prefabCompositionLaneData3.m_Position.x / math.max(1f, prefabCompositionData.m_Width) + 0.5f;
                                    Bezier4x3 curve = MathUtils.Lerp(geometryData.m_Start.m_Left, geometryData.m_Start.m_Right, t);
                                    float3 value3 = -MathUtils.StartTangent(curve);
                                    value3 = MathUtils.Normalize(value3, value3.xz);
                                    float4 = (float3 = CalculateAuxialryZOffset(curve.a, value3, geometry, compositionData2, auxiliaryNetLane));
                                    if (flag3)
                                    {
                                        t = float2.x / math.max(1f, prefabCompositionData.m_Width) + 0.5f;
                                        curve = MathUtils.Lerp(geometryData.m_Start.m_Left, geometryData.m_Start.m_Right, t);
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
                                    float t2 = prefabCompositionLaneData3.m_Position.x / math.max(1f, prefabCompositionData.m_Width) + 0.5f;
                                    Bezier4x3 curve2 = MathUtils.Lerp(geometryData.m_End.m_Left, geometryData.m_End.m_Right, t2);
                                    float3 value4 = MathUtils.EndTangent(curve2);
                                    value4 = MathUtils.Normalize(value4, value4.xz);
                                    float6 = (float5 = CalculateAuxialryZOffset(curve2.d, value4, geometry2, compositionData3, auxiliaryNetLane));
                                    if (flag3)
                                    {
                                        t2 = float2.x / math.max(1f, prefabCompositionData.m_Width) + 0.5f;
                                        curve2 = MathUtils.Lerp(geometryData.m_End.m_Left, geometryData.m_End.m_Right, t2);
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
                                Bounds1 t3 = new Bounds1(0f, 1f);
                                MathUtils.ClampLength(MathUtils.Lerp(geometryData.m_End.m_Left, geometryData.m_End.m_Right, 0.5f).xz, ref t3, num8 - geometryData.m_Start.middleLength);
                                float11.x = 1f + t3.max;
                            }
                            else
                            {
                                Bounds1 t4 = new Bounds1(0f, 1f);
                                MathUtils.ClampLength(MathUtils.Lerp(geometryData.m_Start.m_Left, geometryData.m_Start.m_Right, 0.5f).xz, ref t4, num8);
                                float11.x = t4.max;
                            }
                            for (int num9 = 0; num9 <= num5; num9++)
                            {
                                num8 = ((float)num9 + num6) * num7 + num4;
                                if (num8 > geometryData.m_Start.middleLength)
                                {
                                    Bounds1 t5 = new Bounds1(0f, 1f);
                                    MathUtils.ClampLength(MathUtils.Lerp(geometryData.m_End.m_Left, geometryData.m_End.m_Right, 0.5f).xz, ref t5, num8 - geometryData.m_Start.middleLength);
                                    float11.y = 1f + t5.max;
                                }
                                else
                                {
                                    Bounds1 t6 = new Bounds1(0f, 1f);
                                    MathUtils.ClampLength(MathUtils.Lerp(geometryData.m_Start.m_Left, geometryData.m_Start.m_Right, 0.5f).xz, ref t6, num8);
                                    float11.y = t6.max;
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
                                    float2 t7 = new float2(float11.x, 1f);
                                    float2 t8 = new float2(0f, float11.y - 1f);
                                    segment.m_Left = MathUtils.Join(MathUtils.Cut(geometryData.m_Start.m_Left, t7), MathUtils.Cut(geometryData.m_End.m_Left, t8));
                                    segment.m_Right = MathUtils.Join(MathUtils.Cut(geometryData.m_Start.m_Right, t7), MathUtils.Cut(geometryData.m_End.m_Right, t8));
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
                    if (math.cmin(math.abs(new float2(offset.y, elevationOffset) - netCompositionLane.m_Position.yy)) < 2f)
                    {
                        float num2 = math.abs(netCompositionLane.m_Position.x - offset.x);
                        if (num2 < num)
                        {
                            num = num2;
                            result = i;
                        }
                    }
                }
                return result;
            }

            private void CreateCarEdgeConnections(int jobIndex, ref int edgeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, EdgeGeometry geometryData,
                NetCompositionData prefabCompositionData, NetGeometryData prefabGeometryData, CompositionData compositionData, ConnectPosition connectPosition, float2 offset, int connectionIndex, bool isSource,
                bool isTemp, Temp ownerTemp, DynamicBuffer<NetCompositionLane> prefabCompositionLanes, int bestIndex
            ) {
                NetCompositionLane netCompositionLane = prefabCompositionLanes[bestIndex];
                int num = -1;
                int index = 0;
                float num2 = float.MaxValue;
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
                    if (m_CarLaneData[prefabCompositionLaneData.m_Lane].m_RoadTypes != RoadTypes.Car)
                    {
                        continue;
                    }
                    if (num != -1 && (prefabCompositionLaneData.m_Group != num || (prefabCompositionLaneData.m_Flags & LaneFlags.Slave) == 0))
                    {
                        NetCompositionLane prefabCompositionLaneData2 = prefabCompositionLanes[index];
                        CreateEdgeConnectionLane(jobIndex, ref edgeLaneIndex, ref random, owner, laneBuffer, geometryData.m_Start, geometryData.m_End, prefabCompositionData, prefabGeometryData, compositionData,
                            prefabCompositionLaneData2, connectPosition, connectionIndex, useGroundPosition: false, isSource, isTemp, ownerTemp);
                        num = -1;
                        num2 = float.MaxValue;
                    }
                    if ((prefabCompositionLaneData.m_Flags & LaneFlags.Slave) != 0)
                    {
                        num = prefabCompositionLaneData.m_Group;
                        float num3 = math.abs(prefabCompositionLaneData.m_Position.x - offset.x);
                        if (num3 < num2)
                        {
                            num2 = num3;
                            index = i;
                        }
                    }
                    else
                    {
                        CreateEdgeConnectionLane(jobIndex, ref edgeLaneIndex, ref random, owner, laneBuffer, geometryData.m_Start, geometryData.m_End, prefabCompositionData, prefabGeometryData, compositionData,
                            prefabCompositionLaneData, connectPosition, connectionIndex, useGroundPosition: false, isSource, isTemp, ownerTemp);
                    }
                }
                if (num != -1)
                {
                    NetCompositionLane prefabCompositionLaneData3 = prefabCompositionLanes[index];
                    CreateEdgeConnectionLane(jobIndex, ref edgeLaneIndex, ref random, owner, laneBuffer, geometryData.m_Start, geometryData.m_End, prefabCompositionData, prefabGeometryData, compositionData,
                        prefabCompositionLaneData3, connectPosition, connectionIndex, useGroundPosition: false, isSource, isTemp, ownerTemp);
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
                NativeList<ConnectPosition> tempBuffer, bool isRoundabout, bool isTemp, Temp ownerTemp
            ) {
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
                            CreateNodeConnectionLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, middleConnection, useGroundPosition, isTemp, ownerTemp);
                        }
                        tempBuffer.Clear();
                    }
                    else
                    {
                        if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Road) == 0)
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
                            bool flag = middleConnection.m_TargetCarriageway == num3;
                            if (flag && isRoundabout)
                            {
                                flag = (middleConnection.m_TargetOwner == entity && (entity != owner || middleConnection.m_TargetGroup == num4));
                            }
                            if (flag && (middleConnection.m_TargetFlags & LaneFlags.Master) != 0)
                            {
                                flag = false;
                                for (int m = num5; m <= j; m++)
                                {
                                    MiddleConnection middleConnection5 = middleConnections[m];
                                    if ((middleConnection5.m_TargetFlags & LaneFlags.Master) == 0 && middleConnection5.m_TargetGroup == middleConnection.m_TargetGroup && middleConnection5.m_TargetCarriageway == num3 &&
                                        (!isRoundabout || (middleConnection5.m_TargetOwner == entity && (entity != owner || middleConnection5.m_TargetGroup == num4))))
                                    {
                                        flag = true;
                                        break;
                                    }
                                }
                            }
                            if (flag)
                            {
                                CreateNodeConnectionLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, middleConnection, useGroundPosition: false, isTemp, ownerTemp);
                            }
                        }
                    }
                }
            }

            private void CreateNodeConnectionLane(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, MiddleConnection middleConnection, bool useGroundPosition,
                bool isTemp, Temp ownerTemp
            ) {
                Owner component = default(Owner);
                component.m_Owner = owner;
                Temp temp = default(Temp);
                if (isTemp)
                {
                    temp.m_Flags = (ownerTemp.m_Flags & (TempFlags.Create | TempFlags.Delete | TempFlags.Select | TempFlags.Modify | TempFlags.Hidden));
                    if ((ownerTemp.m_Flags & (TempFlags.Replace | TempFlags.Upgrade)) != 0)
                    {
                        temp.m_Flags |= TempFlags.Modify;
                    }
                }
                PrefabRef component2 = new PrefabRef(middleConnection.m_ConnectPosition.m_LaneData.m_Lane);
				CheckPrefab(ref component2.m_Prefab, ref random, out Unity.Mathematics.Random outRandom, laneBuffer);
                NodeLane component3 = default(NodeLane);
				NetLaneData netLaneData = m_NetLaneData[component2.m_Prefab];
                if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Road) != 0)
                {
                    if (m_NetLaneData.HasComponent(middleConnection.m_TargetLane))
                    {
                        NetLaneData netLaneData2 = m_NetLaneData[middleConnection.m_TargetLane];
                        component3.m_WidthOffset.x = netLaneData2.m_Width - netLaneData.m_Width;
                    }
                    if (m_NetLaneData.HasComponent(middleConnection.m_ConnectPosition.m_LaneData.m_Lane))
                    {
                        NetLaneData netLaneData3 = m_NetLaneData[middleConnection.m_ConnectPosition.m_LaneData.m_Lane];
                        component3.m_WidthOffset.y = netLaneData3.m_Width - netLaneData.m_Width;
                    }
                    if (middleConnection.m_IsSource)
                    {
                        component3.m_WidthOffset = component3.m_WidthOffset.yx;
                    }
                }
                Curve curve = default(Curve);
                if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Road) != 0)
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
                    lane.m_EndNode = new PathNode(owner, middleConnection.m_TargetIndex, middleConnection.m_TargetCurvePos);
                    num = (uint)(middleConnection.m_ConnectPosition.m_GroupIndex | ((int)middleConnection.m_TargetGroup & -65536));
                }
                else
                {
                    lane.m_StartNode = new PathNode(owner, middleConnection.m_TargetIndex, middleConnection.m_TargetCurvePos);
                    lane.m_MiddleNode = new PathNode(owner, (ushort)nodeLaneIndex++);
                    lane.m_EndNode = new PathNode(middleConnection.m_ConnectPosition.m_Owner, middleConnection.m_ConnectPosition.m_LaneData.m_Index, middleConnection.m_ConnectPosition.m_SegmentIndex);
                    num = ((middleConnection.m_TargetGroup & 0xFFFF) | (uint)(middleConnection.m_ConnectPosition.m_GroupIndex << 16));
                }
                CarLane component4 = default(CarLane);
                if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Road) != 0)
                {
                    component4.m_DefaultSpeedLimit = middleConnection.m_TargetComposition.m_SpeedLimit;
					component4.m_Curviness = NetUtils.CalculateCurviness(curve, netLaneData.m_Width);
                    component4.m_CarriagewayGroup = middleConnection.m_TargetCarriageway;
                    component4.m_Flags |= (CarLaneFlags.Unsafe | CarLaneFlags.SideConnection);
                    if (middleConnection.m_IsSource)
                    {
                        component4.m_Flags |= CarLaneFlags.Yield;
                    }
                    //NON-STOCK-CODE
                    // if (m_CustomPriorityData.HasComponent(middleConnection.m_ConnectPosition.m_Owner))
                    // {
                    //     CustomPriority priority = m_CustomPriorityData[middleConnection.m_ConnectPosition.m_Owner];
                    //     //Logger.Debug($"[Edge_middle] Owner: {middleConnection.m_ConnectPosition.m_Owner} has CustomPriority data! {middleConnection.m_ConnectPosition.m_IsEnd} priority: {priority.left} | {priority.right}");
                    //     if (middleConnection.m_ConnectPosition.m_IsEnd && priority.right != 0)
                    //     {
                    //         component4.m_Flags |= priority.right == PriorityType.Stop ? CarLaneFlags.Stop :
                    //             priority.right == PriorityType.Yield ? CarLaneFlags.Yield :
                    //             priority.right == PriorityType.RightOfWay ? CarLaneFlags.RightOfWay : 0;
                    //     } else if (!middleConnection.m_ConnectPosition.m_IsEnd && priority.left != 0)
                    //     {
                    //         component4.m_Flags |= priority.left == PriorityType.Stop ? CarLaneFlags.Stop :
                    //             priority.left == PriorityType.Yield ? CarLaneFlags.Yield :
                    //             priority.left == PriorityType.RightOfWay ? CarLaneFlags.RightOfWay : 0;
                    //     }
                    // }
                    //NON-STOCK-CODE-END
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
                    middleConnection.m_ConnectPosition.m_LaneData.m_Flags &= ~(LaneFlags.Slave | LaneFlags.Master);
                    middleConnection.m_ConnectPosition.m_LaneData.m_Flags |= (middleConnection.m_TargetFlags & (LaneFlags.Slave | LaneFlags.Master));
                }
                else
                {
                    middleConnection.m_ConnectPosition.m_LaneData.m_Flags &= ~(LaneFlags.Slave | LaneFlags.Master);
                }
                PedestrianLane component5 = default(PedestrianLane);
                UtilityLane component6 = default(UtilityLane);
                if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Utility) != 0 && m_PrefabRefData.TryGetComponent(middleConnection.m_ConnectPosition.m_Owner, out PrefabRef componentData) &&
                    m_PrefabNetData.TryGetComponent(componentData.m_Prefab, out NetData componentData2) && m_PrefabGeometryData.TryGetComponent(componentData.m_Prefab, out NetGeometryData componentData3) &&
                    (componentData2.m_RequiredLayers & (Layer.PowerlineLow | Layer.PowerlineHigh | Layer.WaterPipe | Layer.SewagePipe)) != 0 && (componentData3.m_Flags & GeometryFlags.Marker) == 0)
                {
                    component6.m_Flags |= UtilityLaneFlags.PipelineConnection;
                }
                LaneKey laneKey = new LaneKey(lane, component2.m_Prefab, middleConnection.m_ConnectPosition.m_LaneData.m_Flags);
                LaneKey laneKey2 = laneKey;
                if (isTemp)
                {
                    ReplaceTempOwner(ref laneKey2, owner);
                    ReplaceTempOwner(ref laneKey2, middleConnection.m_ConnectPosition.m_Owner);
                    GetOriginalLane(laneBuffer, laneKey2, ref temp);
                }
				PseudoRandomSeed componentData4 = default(PseudoRandomSeed);
				if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0 && !m_PseudoRandomSeedData.TryGetComponent(temp.m_Original, out componentData4))
				{
					componentData4 = new PseudoRandomSeed(ref outRandom);
				}
                if (laneBuffer.m_OldLanes.TryGetValue(laneKey, out Entity item))
                {
                    laneBuffer.m_OldLanes.Remove(laneKey);
                    m_CommandBuffer.SetComponent(jobIndex, item, component3);
                    m_CommandBuffer.SetComponent(jobIndex, item, curve);
					if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
					{
						m_CommandBuffer.AddComponent(jobIndex, item, componentData4);
					}
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Road) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, item, component4);
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, item, component5);
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Utility) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, item, component6);
                    }
                    if (isTemp)
                    {
                        m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, item);
                        m_CommandBuffer.AddComponent(jobIndex, item, default(Updated));
                        m_CommandBuffer.SetComponent(jobIndex, item, temp);
                    }
                    else if (m_TempData.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent(jobIndex, item, in m_DeletedTempTypes);
                        m_CommandBuffer.AddComponent(jobIndex, item, in m_AppliedTypes);
                    }
                    else
                    {
                        m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, item);
                        m_CommandBuffer.AddComponent(jobIndex, item, default(Updated));
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Master) != 0)
                    {
                        MasterLane component7 = default(MasterLane);
                        component7.m_Group = num;
                        m_CommandBuffer.SetComponent(jobIndex, item, component7);
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Slave) != 0)
                    {
                        SlaveLane component8 = default(SlaveLane);
                        component8.m_Group = num;
                        component8.m_MinIndex = 0;
                        component8.m_MaxIndex = 0;
                        component8.m_SubIndex = 0;
                        component8.m_Flags |= SlaveLaneFlags.AllowChange;
                        component8.m_Flags |= (SlaveLaneFlags)(middleConnection.m_IsSource ? 4096 : 2048);
                        m_CommandBuffer.SetComponent(jobIndex, item, component8);
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
						m_CommandBuffer.SetComponent(jobIndex, e, componentData4);
					}
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Road) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component4);
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component5);
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Utility) != 0)
                    {
                        m_CommandBuffer.SetComponent(jobIndex, e, component6);
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Master) != 0)
                    {
                        MasterLane component9 = default(MasterLane);
                        component9.m_Group = num;
                        m_CommandBuffer.SetComponent(jobIndex, e, component9);
                    }
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Slave) != 0)
                    {
                        SlaveLane component10 = default(SlaveLane);
                        component10.m_Group = num;
                        component10.m_MinIndex = 0;
                        component10.m_MaxIndex = 0;
                        component10.m_SubIndex = 0;
                        component10.m_Flags |= SlaveLaneFlags.AllowChange;
                        component10.m_Flags |= (SlaveLaneFlags)(middleConnection.m_IsSource ? 4096 : 2048);
                        m_CommandBuffer.SetComponent(jobIndex, e, component10);
                    }
                    m_CommandBuffer.AddComponent(jobIndex, e, component);
                    if (isTemp)
                    {
                        m_CommandBuffer.AddComponent(jobIndex, e, temp);
                    }
                }
            }

            private void CreateEdgeConnectionLane(int jobIndex, ref int edgeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, Segment startSegment, Segment endSegment,
                NetCompositionData prefabCompositionData, NetGeometryData prefabGeometryData, CompositionData compositionData, NetCompositionLane prefabCompositionLaneData, ConnectPosition connectPosition,
                int connectionIndex, bool useGroundPosition, bool isSource, bool isTemp, Temp ownerTemp
            ) {
                float t = prefabCompositionLaneData.m_Position.x / math.max(1f, prefabCompositionData.m_Width) + 0.5f;
                Owner component = default(Owner);
                component.m_Owner = owner;
                Temp temp = default(Temp);
                if (isTemp)
                {
                    temp.m_Flags = (ownerTemp.m_Flags & (TempFlags.Create | TempFlags.Delete | TempFlags.Select | TempFlags.Modify | TempFlags.Hidden));
                    if ((ownerTemp.m_Flags & (TempFlags.Replace | TempFlags.Upgrade)) != 0)
                    {
                        temp.m_Flags |= TempFlags.Modify;
                    }
                }
                PrefabRef component2 = new PrefabRef(connectPosition.m_LaneData.m_Lane);
				CheckPrefab(ref component2.m_Prefab, ref random, out Unity.Mathematics.Random outRandom, laneBuffer);
                bool flag = (prefabGeometryData.m_Flags & (GeometryFlags.StraightEdges | GeometryFlags.SmoothSlopes)) == GeometryFlags.StraightEdges;
                bool flag2 = false;
                Bezier4x3 bezier4x = default(Bezier4x3);
                Bezier4x3 bezier4x2 = default(Bezier4x3);
                Bezier4x3 curve;
                byte b;
                float t2;
                if (flag)
                {
                    curve = MathUtils.Lerp(MathUtils.Join(startSegment.m_Left, endSegment.m_Left), MathUtils.Join(startSegment.m_Right, endSegment.m_Right), t);
                    curve.a.y += prefabCompositionLaneData.m_Position.y;
                    curve.b.y += prefabCompositionLaneData.m_Position.y;
                    curve.c.y += prefabCompositionLaneData.m_Position.y;
                    curve.d.y += prefabCompositionLaneData.m_Position.y;
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
                        flag2 = !flag2;
                    }
                    float t3;
                    float num = MathUtils.Distance(bezier4x, connectPosition.m_Position, out t3);
                    if (MathUtils.Distance(bezier4x2, connectPosition.m_Position, out float t4) < num)
                    {
                        curve = bezier4x2;
                        b = 3;
                        t2 = t4;
                        flag2 = !flag2;
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
                if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Road) != 0)
                {
                    if (m_NetLaneData.HasComponent(prefabCompositionLaneData.m_Lane))
                    {
                        NetLaneData netLaneData2 = m_NetLaneData[prefabCompositionLaneData.m_Lane];
                        component3.m_WidthOffset.x = netLaneData2.m_Width - netLaneData.m_Width;
                    }
                    if (m_NetLaneData.HasComponent(connectPosition.m_LaneData.m_Lane))
                    {
                        NetLaneData netLaneData3 = m_NetLaneData[connectPosition.m_LaneData.m_Lane];
                        component3.m_WidthOffset.y = netLaneData3.m_Width - netLaneData.m_Width;
                    }
                    if (isSource)
                    {
                        component3.m_WidthOffset = component3.m_WidthOffset.yx;
                    }
                }
                Curve curve2 = default(Curve);
                if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Road) != 0)
                {
                    float num2 = math.sqrt(math.distance(connectPosition.m_Position, MathUtils.Position(curve, t2)));
                    float length = num2;
                    if (isSource)
                    {
                        Bounds1 t5 = new Bounds1(t2, 1f);
                        if (!MathUtils.ClampLength(curve, ref t5, ref length) && !flag && !flag2)
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
                        if (!MathUtils.ClampLengthInverse(curve, ref t6, ref length) && !flag && flag2)
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
                else if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Utility) != 0)
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
                if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Road) != 0)
                {
                    component4.m_DefaultSpeedLimit = compositionData.m_SpeedLimit;
					component4.m_Curviness = NetUtils.CalculateCurviness(curve2, netLaneData.m_Width);
                    component4.m_CarriagewayGroup = (ushort)((b << 8) | prefabCompositionLaneData.m_Carriageway);
                    component4.m_Flags |= (CarLaneFlags.Unsafe | CarLaneFlags.SideConnection);
                    if (isSource)
                    {
                        component4.m_Flags |= CarLaneFlags.Yield;
                    }
                    //todo
                    /*if (m_CustomPriorityData.HasComponent(connectPosition.m_Owner))
                    {
                        CustomPriority priority = m_CustomPriorityData[connectPosition.m_Owner];
                        Logger.Debug($"[Edge] Owner: {connectPosition.m_Owner} has CustomPriority data! {connectPosition.m_IsEnd} priority: {priority.left} | {priority.right}");
                        if (connectPosition.m_IsEnd && priority.right != 0)
                        {
                            component4.m_Flags |= priority.right == PriorityType.Stop ? CarLaneFlags.Stop :
                                priority.right == PriorityType.Yield ? CarLaneFlags.Yield :
                                priority.right == PriorityType.RightOfWay ? CarLaneFlags.RightOfWay : 0;
                        } else if (!connectPosition.m_IsEnd && priority.left != 0)
                        {
                            component4.m_Flags |= priority.left == PriorityType.Stop ? CarLaneFlags.Stop :
                                priority.left == PriorityType.Yield ? CarLaneFlags.Yield :
                                priority.left == PriorityType.RightOfWay ? CarLaneFlags.RightOfWay : 0;
                        }
                    }*/
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
                    connectPosition.m_LaneData.m_Flags |= (prefabCompositionLaneData.m_Flags & (LaneFlags.Slave | LaneFlags.Master));
                }
                else
                {
                    connectPosition.m_LaneData.m_Flags &= ~(LaneFlags.Slave | LaneFlags.Master);
                }
                PedestrianLane component5 = default(PedestrianLane);
                UtilityLane component6 = default(UtilityLane);
                if ((connectPosition.m_LaneData.m_Flags & LaneFlags.Utility) != 0 && m_PrefabRefData.TryGetComponent(connectPosition.m_Owner, out PrefabRef componentData) &&
                    m_PrefabNetData.TryGetComponent(componentData.m_Prefab, out NetData componentData2) && m_PrefabGeometryData.TryGetComponent(componentData.m_Prefab, out NetGeometryData componentData3) &&
                    (componentData2.m_RequiredLayers & (Layer.PowerlineLow | Layer.PowerlineHigh | Layer.WaterPipe | Layer.SewagePipe)) != 0 && (componentData3.m_Flags & GeometryFlags.Marker) == 0)
                {
                    component6.m_Flags |= UtilityLaneFlags.PipelineConnection;
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
				PseudoRandomSeed componentData4 = default(PseudoRandomSeed);
				if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0 && !m_PseudoRandomSeedData.TryGetComponent(temp.m_Original, out componentData4))
				{
					componentData4 = new PseudoRandomSeed(ref outRandom);
				}
                if (laneBuffer.m_OldLanes.TryGetValue(laneKey, out Entity item))
                {
                    laneBuffer.m_OldLanes.Remove(laneKey);
                    m_CommandBuffer.SetComponent(jobIndex, item, component3);
                    m_CommandBuffer.SetComponent(jobIndex, item, curve2);
					if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
					{
						m_CommandBuffer.AddComponent(jobIndex, item, componentData4);
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
                        m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, item);
                        m_CommandBuffer.AddComponent(jobIndex, item, default(Updated));
                        m_CommandBuffer.SetComponent(jobIndex, item, temp);
                    }
                    else if (m_TempData.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent(jobIndex, item, in m_DeletedTempTypes);
                        m_CommandBuffer.AddComponent(jobIndex, item, in m_AppliedTypes);
                    }
                    else
                    {
                        m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, item);
                        m_CommandBuffer.AddComponent(jobIndex, item, default(Updated));
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
						m_CommandBuffer.SetComponent(jobIndex, e, componentData4);
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
                    m_CommandBuffer.AddComponent(jobIndex, e, component);
                    if (isTemp)
                    {
                        m_CommandBuffer.AddComponent(jobIndex, e, temp);
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
                    if ((ownerTemp.m_Flags & (TempFlags.Replace | TempFlags.Upgrade)) != 0)
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
                bool priorityChanged = false;//TODO NON-STOCK
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
                    // if (m_CustomPriorityData.HasComponent(owner))
                    // {
                    //     priorityChanged = true;
                    //     CustomPriority priority = m_CustomPriorityData[owner];
                    //     bool2 tempStart = (edgeDelta == new float2(0f, 0.5f));
                    //     bool2 tempStartIndex = (segmentIndex == new int2(0, 2));
                    //     bool isStart = tempStart.x && tempStart.y && tempStartIndex.x && tempStartIndex.y;
                    //     bool2 tempEnd = (edgeDelta == new float2(0.5f, 1f));
                    //     bool2 tempEndIndex = (segmentIndex == new int2(2, 4));
                    //     bool isEnd = tempEnd.x && tempEnd.y && tempEndIndex.x && tempEndIndex.y;
                    //     //Logger.Debug($"[Edge] Owner: {owner} has CustomPriority data! s: {isStart} e: {isEnd} priority: {priority.left} | {priority.right}");
                    //     if (isEnd && priority.right != 0)
                    //     {
                    //         component5.m_Flags |= priority.right == PriorityType.Stop ? CarLaneFlags.Stop :
                    //             priority.right == PriorityType.Yield ? CarLaneFlags.Yield :
                    //             priority.right == PriorityType.RightOfWay ? CarLaneFlags.RightOfWay : 0;
                    //     }
                    //     else if (isStart && priority.left != 0)
                    //     {
                    //         component5.m_Flags |= priority.left == PriorityType.Stop ? CarLaneFlags.Stop :
                    //             priority.left == PriorityType.Yield ? CarLaneFlags.Yield :
                    //             priority.left == PriorityType.RightOfWay ? CarLaneFlags.RightOfWay : 0;
                    //     }
                    // }
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
                    if (((prefabCompositionData.m_Flags.m_Left | prefabCompositionData.m_Flags.m_Right) & CompositionFlags.Side.PrimaryStop) != 0)
                    {
                        component6.m_Flags |= TrackLaneFlags.Station;
                    }
                }
                ParkingLane component7 = default(ParkingLane);
                if ((laneFlags & LaneFlags.Parking) != 0)
                {
                    NetCompositionLane netCompositionLane = FindClosestLane(prefabCompositionLanes, LaneFlags.Road, LaneFlags.Slave, prefabCompositionLaneData.m_Position);
                    NetCompositionLane netCompositionLane2 = FindClosestLane(prefabCompositionLanes, LaneFlags.Pedestrian, (LaneFlags)0, prefabCompositionLaneData.m_Position);
                    if (netCompositionLane.m_Lane != Entity.Null)
                    {
                        laneData.m_StartNode = new PathNode(owner, netCompositionLane.m_Index, (byte)num, 0.5f);
                        if ((laneFlags & LaneFlags.Twoway) != 0)
                        {
                            NetCompositionLane netCompositionLane3 = FindClosestLane(prefabCompositionLanes, LaneFlags.Road | (~netCompositionLane.m_Flags & LaneFlags.Invert),
                                LaneFlags.Slave | (netCompositionLane.m_Flags & LaneFlags.Invert), netCompositionLane.m_Position, netCompositionLane.m_Carriageway);
                            if (netCompositionLane3.m_Lane != Entity.Null)
                            {
                                component7.m_SecondaryStartNode = new PathNode(owner, netCompositionLane3.m_Index, (byte)num, 0.5f);
                                component7.m_Flags |= ParkingLaneFlags.SecondaryStart;
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
                }
                uint group = (uint)(prefabCompositionLaneData.m_Group | (segmentIndex.x << 8));
                LaneKey laneKey = new LaneKey(laneData, component2.m_Prefab, laneFlags);
                LaneKey laneKey2 = laneKey;
                if (isTemp)
                {
                    ReplaceTempOwner(ref laneKey2, owner);
                    GetOriginalLane(laneBuffer, laneKey2, ref temp);
                }
				PseudoRandomSeed componentData = default(PseudoRandomSeed);
				if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0 && !m_PseudoRandomSeedData.TryGetComponent(temp.m_Original, out componentData))
				{
					componentData = new PseudoRandomSeed(ref outRandom);
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
                if (flag2)
                {
                    laneBuffer.m_OldLanes.Remove(laneKey);
                    m_CommandBuffer.SetComponent(jobIndex, item, laneData);
                    m_CommandBuffer.SetComponent(jobIndex, item, edgeLaneData);
                    m_CommandBuffer.SetComponent(jobIndex, item, curveData);
					if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
					{
						m_CommandBuffer.AddComponent(jobIndex, item, componentData);
					}
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
                    if (isTemp)
                    {
                        m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, item);
                        m_CommandBuffer.AddComponent(jobIndex, item, default(Updated));
                        m_CommandBuffer.SetComponent(jobIndex, item, temp);
                    }
                    else if (m_TempData.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent(jobIndex, item, in m_DeletedTempTypes);
                        m_CommandBuffer.AddComponent(jobIndex, item, in m_AppliedTypes);
                    }
                    else
                    {
                        m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, item);
                        m_CommandBuffer.AddComponent(jobIndex, item, default(Updated));
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
					m_CommandBuffer.SetComponent(jobIndex, e, componentData);
				}
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
                m_CommandBuffer.AddComponent(jobIndex, e, component);
                if (isTemp)
                {
                    m_CommandBuffer.AddComponent(jobIndex, e, temp);
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
                    if ((ownerTemp.m_Flags & (TempFlags.Replace | TempFlags.Upgrade)) != 0)
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
                ParkingLane component6 = default(ParkingLane);
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
                    m_CommandBuffer.SetComponent(jobIndex, item, curveData);
					if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
					{
						m_CommandBuffer.AddComponent(jobIndex, item, componentData2);
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
                        m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, item);
                        m_CommandBuffer.AddComponent(jobIndex, item, default(Updated));
                        m_CommandBuffer.SetComponent(jobIndex, item, temp);
                    }
                    else if (m_TempData.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent(jobIndex, item, in m_DeletedTempTypes);
                        m_CommandBuffer.AddComponent(jobIndex, item, in m_AppliedTypes);
                    }
                    else
                    {
                        m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, item);
                        m_CommandBuffer.AddComponent(jobIndex, item, default(Updated));
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
                m_CommandBuffer.AddComponent(jobIndex, e, component);
                if (math.any(@bool))
                {
                    m_CommandBuffer.AddComponent(jobIndex, e, new Elevation(elevation));
                }
                if (!isTemp)
                {
                    return;
                }
                m_CommandBuffer.AddComponent(jobIndex, e, temp);
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
                EdgeIteratorValue value;
                while (edgeIterator.GetNext(out value))
                {
                    DynamicBuffer<ConnectedNode> dynamicBuffer = m_Nodes[value.m_Edge];
                    for (int i = 0; i < dynamicBuffer.Length; i++)
                    {
                        ConnectedNode connectedNode = dynamicBuffer[i];
                        GetMiddleConnectionCurves(connectedNode.m_Node, tempEdgeTargets);
                        bool flag = false;
                        for (int j = 0; j < tempEdgeTargets.Length; j++)
                        {
                            EdgeTarget edgeTarget = tempEdgeTargets[j];
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
                        GetNodeConnectPositions(connectedNode.m_Node, connectedNode.m_CurvePosition, tempBuffer1, tempBuffer2, includeAnchored: true, ref groupIndex2, out float _, out float _);
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
                            for (int k = 0; k < middleConnections.Length; k++)
                            {
                                if (middleConnections[k].m_ConnectPosition.m_Owner == entity)
                                {
                                    flag = true;
                                    break;
                                }
                            }
                            if (!flag)
                            {
                                groupIndex = groupIndex2;
                                for (int l = 0; l < tempBuffer1.Length; l++)
                                {
                                    MiddleConnection value2 = default(MiddleConnection);
                                    value2.m_ConnectPosition = tempBuffer1[l];
                                    value2.m_ConnectPosition.m_IsSideConnection = true;
                                    value2.m_SourceEdge = value.m_Edge;
                                    value2.m_SourceNode = connectedNode.m_Node;
                                    value2.m_SortIndex = middleConnections.Length;
                                    value2.m_Distance = float.MaxValue;
                                    value2.m_IsSource = true;
                                    middleConnections.Add(in value2);
                                }
                                for (int m = 0; m < tempBuffer2.Length; m++)
                                {
                                    MiddleConnection value3 = default(MiddleConnection);
                                    value3.m_ConnectPosition = tempBuffer2[m];
                                    value3.m_ConnectPosition.m_IsSideConnection = true;
                                    value3.m_SourceEdge = value.m_Edge;
                                    value3.m_SourceNode = connectedNode.m_Node;
                                    value3.m_SortIndex = middleConnections.Length;
                                    value3.m_Distance = float.MaxValue;
                                    value3.m_IsSource = false;
                                    middleConnections.Add(in value3);
                                }
                            }
                        }
                        tempBuffer1.Clear();
                        tempBuffer2.Clear();
                    }
                }
            }

            private void GetNodeConnectPositions(Entity owner, float curvePosition, NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer, bool includeAnchored, ref int groupIndex,
                out float middleRadius, out float roundaboutSize
            ) {
                middleRadius = 0f;
                roundaboutSize = 0f;
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
                EdgeIteratorValue value;
                while (edgeIterator.GetNext(out value))
                {
                    GetNodeConnectPositions(owner, value.m_Edge, value.m_End, groupIndex++, curvePosition, elevation, prefabGeometryData, sourceBuffer, targetBuffer, includeAnchored, ref middleRadius, ref roundaboutSize, ref anchorPrefabs);
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
                    for (int i = 0; i < dynamicBuffer.Length; i++)
                    {
                        NetCompositionLane laneData = new NetCompositionLane(dynamicBuffer[i]);
                        if ((laneData.m_Flags & LaneFlags.Utility) == 0 || ((laneData.m_Flags & laneFlags) != 0 && IsAnchored(owner, ref anchorPrefabs, laneData.m_Lane)))
                        {
                            continue;
                        }
                        bool flag2 = (laneData.m_Flags & LaneFlags.Invert) != 0;
                        if (((int)laneData.m_Flags & (flag2 ? 512/*DisconnectedEnd*/ : 256/*DisconnectedStart*/)) == 0)
                        {
                            laneData.m_Position.x = 0f - laneData.m_Position.x;
                            float num = laneData.m_Position.x / math.max(1f, prefabGeometryData.m_DefaultWidth) + 0.5f;
                            float3 y = node.m_Position + math.rotate(node.m_Rotation, new float3(prefabGeometryData.m_DefaultWidth * -0.5f, 0f, 0f));
                            float3 position = math.lerp(node.m_Position + math.rotate(node.m_Rotation, new float3(prefabGeometryData.m_DefaultWidth * 0.5f, 0f, 0f)), y, num);
                            ConnectPosition value2 = default(ConnectPosition);
                            value2.m_LaneData = laneData;
                            value2.m_Owner = owner;
                            value2.m_Position = position;
                            value2.m_Position.y += laneData.m_Position.y;
                            value2.m_Tangent = math.forward(node.m_Rotation);
                            value2.m_Tangent = -MathUtils.Normalize(value2.m_Tangent, value2.m_Tangent.xz);
                            value2.m_Tangent.y = math.clamp(value2.m_Tangent.y, -1f, 1f);
                            value2.m_GroupIndex = (ushort)(laneData.m_Group | (groupIndex << 8));
                            value2.m_CurvePosition = curvePosition;
                            value2.m_BaseHeight = position.y;
                            value2.m_Elevation = math.lerp(elevation.x, elevation.y, 0.5f);
                            value2.m_Order = num;
                            if ((laneData.m_Flags & LaneFlags.Track) != 0)
                            {
                                TrackLaneData trackLaneData = m_TrackLaneData[laneData.m_Lane];
                                value2.m_TrackTypes = trackLaneData.m_TrackTypes;
                            }
                            if ((laneData.m_Flags & LaneFlags.Utility) != 0)
                            {
                                UtilityLaneData utilityLaneData = m_UtilityLaneData[laneData.m_Lane];
                                value2.m_UtilityTypes = utilityLaneData.m_UtilityTypes;
                            }
                            if ((laneData.m_Flags & (LaneFlags.Pedestrian | LaneFlags.Utility)) != 0)
                            {
                                targetBuffer.Add(in value2);
                            }
                            else if ((laneData.m_Flags & LaneFlags.Twoway) != 0)
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
                    groupIndex++;
                }
                if (anchorPrefabs.IsCreated)
                {
                    anchorPrefabs.Dispose();
                }
            }

            private unsafe void GetNodeConnectPositions(Entity node, Entity edge, bool isEnd, int groupIndex, float curvePosition, float2 elevation, NetGeometryData prefabGeometryData,
                NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer, bool includeAnchored, ref float middleRadius, ref float roundaboutSize, ref NativeParallelHashSet<Entity> anchorPrefabs
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
                middleRadius = math.max(middleRadius, geometry.m_MiddleRadius);
                roundaboutSize = math.max(roundaboutSize, math.select(netCompositionData2.m_RoundaboutSize.x, netCompositionData2.m_RoundaboutSize.y, isEnd));
                bool isSideConnection = (netGeometryData.m_MergeLayers & prefabGeometryData.m_MergeLayers) == 0 && (prefabGeometryData.m_MergeLayers & Layer.Road) != 0;
                LaneFlags laneFlags = (!includeAnchored) ? LaneFlags.FindAnchor : ((LaneFlags)0);
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
                        if (num != -1 && !ptr[num])
                        {
                            ptr[num] = true;
                            NetCompositionLane laneData = netCompositionLanes[num];
                            laneData.m_Position.x = math.select(0f - laneData.m_Position.x, laneData.m_Position.x, isEnd);
                            float order = laneData.m_Position.x / math.max(1f, netCompositionData.m_Width) + 0.5f;
                            Lane lane = m_LaneData[subLane];
                            if (y)
                            {
                                laneData.m_Index = (byte)(lane.m_EndNode.GetLaneIndex() & 0xFF);
                            }
                            else
                            {
                                laneData.m_Index = (byte)(lane.m_StartNode.GetLaneIndex() & 0xFF);
                            }
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
                    }
                    return;
                }
                for (int l = 0; l < netCompositionLanes.Length; l++)
                {
                    NetCompositionLane laneData2 = netCompositionLanes[l];
                    bool flag = isEnd == ((laneData2.m_Flags & LaneFlags.Invert) == 0);
                    if (((int)laneData2.m_Flags & (flag ? 512/*DisconnectedEnd*/ : 256/*DisconnectedStart*/)) == 0 &&
                        ((laneData2.m_Flags & laneFlags) == 0 || !IsAnchored(node, ref anchorPrefabs, laneData2.m_Lane)))
                    {
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
                        }
                        else if ((laneData2.m_Flags & LaneFlags.Twoway) != 0)
                        {
                            targetBuffer.Add(in value2);
                            sourceBuffer.Add(in value2);
                        }
                        else if (!flag)
                        {
                            targetBuffer.Add(in value2);
                        }
                        else
                        {
                            sourceBuffer.Add(in value2);
                        }
                    }
                }
            }

            private void FillModifiedLaneConnections(DynamicBuffer<ModifiedLaneConnections> connections, NativeParallelHashSet<LaneEndKey> output) {
                for (var i = 0; i < connections.Length; i++)
                {
                    ModifiedLaneConnections connection = connections[i];
                    output.Add(new LaneEndKey(connection.edgeEntity, connection.laneIndex));
                }
            }
            
            private void FillModifiedLaneMasterConnections(ConnectPosition sourceMainCarConnectPos, DynamicBuffer<GeneratedConnection> generatedConnection, NativeParallelHashSet<ConnectionKey> createdConnections,
                NativeHashSet<ConnectionKey> tempMainConnectionKeys
            ) {
                tempMainConnectionKeys.Clear();
                for (var i = 0; i < generatedConnection.Length; i++)
                {
                    GeneratedConnection connection = generatedConnection[i];
                    //collect main connection keys (edge+laneGroup pairs)
                }
            }
            
            private void FilterAllowedCarConnectPositions(ConnectPosition source, NativeParallelHashSet<ConnectionKey> forbidden, NativeList<ConnectPosition> input, NativeList<ConnectPosition> output) {
                
                for (int i = 0; i < input.Length; i++)
                {
                    ConnectPosition value = input[i];
                    if (!forbidden.Contains(new ConnectionKey(source, value)))
                    {
                        output.Add(in value);
                    } 
                }
            }

            /// <summary>
            /// Collects non-Slave road-only connect positions
            /// </summary>
            private void FilterMainCarConnectPositions(NativeList<ConnectPosition> input, NativeList<ConnectPosition> output) {
                for (int i = 0; i < input.Length; i++)
                {
                    ConnectPosition value = input[i];
                    if ((value.m_LaneData.m_Flags & (LaneFlags.Slave | LaneFlags.Road)) == LaneFlags.Road)
                    {
                        output.Add(in value);
                    }
                }
            }

            /// <summary>
            /// Collects non-Master road-only connect positions
            /// </summary>
            private void FilterActualCarConnectPositions(NativeList<ConnectPosition> input, NativeList<ConnectPosition> output) {
                for (int i = 0; i < input.Length; i++)
                {
                    ConnectPosition value = input[i];
                    if ((value.m_LaneData.m_Flags & (LaneFlags.Master | LaneFlags.Road)) == LaneFlags.Road)
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
                    if ((value.m_LaneData.m_Flags & (LaneFlags.Master | LaneFlags.Road)) == LaneFlags.Road &&
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

            private void FilterPedestrianConnectPositions(NativeList<ConnectPosition> input, NativeList<ConnectPosition> output, NativeList<MiddleConnection> middleConnections) {
                for (int i = 0; i < input.Length; i++)
                {
                    ConnectPosition value = input[i];
                    if ((value.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                    {
                        output.Add(in value);
                    }
                }
                for (int j = 0; j < middleConnections.Length; j++)
                {
                    MiddleConnection middleConnection = middleConnections[j];
                    if ((middleConnection.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Pedestrian) != 0)
                    {
                        output.Add(in middleConnection.m_ConnectPosition);
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
                // if (m_CustomPriorityData.HasComponent(source.m_Owner))
                // {
                //     CustomPriority priority = m_CustomPriorityData[source.m_Owner];
                //     //Logger.Debug($"[Node] Owner: {source.m_Owner} has CustomPriority data! {source.m_IsEnd} priority: {priority.left} | {priority.right}");
                //     if (source.m_IsEnd && priority.right != 0)
                //     {
                //         return priority.right == PriorityType.Stop ? 2 :
                //             priority.right == PriorityType.Yield ? 1 :
                //             priority.right == PriorityType.RightOfWay ? -1 : 0;
                //     }
                //     if (!source.m_IsEnd && priority.left != 0)
                //     {
                //         return priority.left == PriorityType.Stop ? 2 :
                //             priority.left == PriorityType.Yield ? 1 :
                //             priority.left == PriorityType.RightOfWay ? -1 : 0;
                //     }
                // }
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

            private void ProcessCarConnectPositions(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, NativeList<MiddleConnection> middleConnections,
                NativeParallelHashSet<ConnectionKey> createdConnections, NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer, NativeList<ConnectPosition> allSources, bool isTemp,
                Temp ownerTemp, int yield, /*NON-STOCK*/NativeParallelHashSet<LaneEndKey> modifiedLaneEndConnections, NativeHashSet<ConnectionKey> createdGroups
            ) {
                if (sourceBuffer.Length >= 1 && targetBuffer.Length >= 1)
                {
                    sourceBuffer.Sort(default(SourcePositionComparer));
                    ConnectPosition sourcePosition = sourceBuffer[0];
                    SortTargets(sourcePosition, targetBuffer);
                    StringBuilder sb = new StringBuilder();
                    for (var i = 0; i < targetBuffer.Length; i++)
                    {
                        sb.Append("\ti[").Append(targetBuffer[i].m_LaneData.m_Index).Append("] g[").Append(targetBuffer[i].m_LaneData.m_Group).Append("] gi[").Append(targetBuffer[i].m_GroupIndex).Append("] ").Append(targetBuffer[i].m_Owner).Append(" f[").Append(targetBuffer[i].m_LaneData.m_Flags.ToString()).AppendLine("]");
                    }
                    Logger.Debug($"Sorted targets\n  S({sourcePosition.m_Owner} | {sourcePosition.m_LaneData.m_Index}[{sourcePosition.m_GroupIndex}] ({sourcePosition.m_LaneData.m_Group}) -> [{sourcePosition.m_LaneData.m_Flags}]), nLaneIdx: {nodeLaneIndex}, owner: {owner}:\n{sb}");
                    CreateNodeCarLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, middleConnections, createdConnections, sourceBuffer, targetBuffer, allSources, isTemp, ownerTemp, yield, modifiedLaneEndConnections, createdGroups);
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
                        !(IsTurn(sourcePosition, target, out bool _, out bool _, out bool uturn) && uturn))
                    {
                        result += math.select(new int2(0, 1), new int2(1, 0), math.dot(x, target.m_Position.xz - sourcePosition.m_Position.xz) > 0f);
                    }
                }
                return result;
            }

            private void CreateNodeCarLanes(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, NativeList<MiddleConnection> middleConnections, NativeParallelHashSet<ConnectionKey> createdConnections,
                NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer, NativeList<ConnectPosition> allSources, bool isTemp, Temp ownerTemp, int yield, NativeParallelHashSet<LaneEndKey> modifiedLaneEndConnections, NativeHashSet<ConnectionKey> createdGroups
            ) {
                ConnectPosition sourcePositionFirst = sourceBuffer[0];
                ConnectPosition sourcePositionLast = sourceBuffer[sourceBuffer.Length - 1];
                NetCompositionData netCompositionData = m_PrefabCompositionData[sourcePositionFirst.m_NodeComposition];
                CompositionFlags.Side side = ((netCompositionData.m_Flags.m_General & CompositionFlags.General.Invert) != 0 != ((sourcePositionFirst.m_LaneData.m_Flags & LaneFlags.Invert) != 0))
                    ? netCompositionData.m_Flags.m_Left
                    : netCompositionData.m_Flags.m_Right;
                bool isForbidLeftTurn = (side & CompositionFlags.Side.ForbidLeftTurn) != 0;
                bool isForbidRightTurn = (side & CompositionFlags.Side.ForbidRightTurn) != 0;
                bool isForbidStraight = (side & CompositionFlags.Side.ForbidStraight) != 0;
                int num2 = 0;
                int num3 = 0;
                int num4 = 0;
                while (num2 < targetBuffer.Length)
                {
                    ConnectPosition targetPosition = targetBuffer[num2];
                    int i;
                    for (i = num2 + 1; i < targetBuffer.Length; i++)
                    {
                        ConnectPosition connectPosition = targetBuffer[i];
                        if (connectPosition.m_GroupIndex != targetPosition.m_GroupIndex)
                        {
                            break;
                        }
                        targetPosition = connectPosition;
                    }
                    if (!IsTurn(sourcePositionFirst, targetPosition, out bool right, out bool _, out bool uturn) || right || !uturn)
                    {
                        break;
                    }
                    num2 = i;
                    if (targetPosition.m_Owner == sourcePositionFirst.m_Owner && targetPosition.m_LaneData.m_Carriageway == sourcePositionFirst.m_LaneData.m_Carriageway)
                    {
                        num3 = i;
                    }
                    if (isForbidLeftTurn)
                    {
                        num4 = i;
                    }
                }
                int num5 = 0;
                int num6 = 0;
                int num7 = 0;
                while (num5 < targetBuffer.Length - num2)
                {
                    ConnectPosition targetPosition2 = targetBuffer[targetBuffer.Length - num5 - 1];
                    int j;
                    for (j = num5 + 1; j < targetBuffer.Length - num2; j++)
                    {
                        ConnectPosition connectPosition2 = targetBuffer[targetBuffer.Length - j - 1];
                        if (connectPosition2.m_GroupIndex != targetPosition2.m_GroupIndex)
                        {
                            break;
                        }
                        targetPosition2 = connectPosition2;
                    }
                    if (!IsTurn(sourcePositionLast, targetPosition2, out bool right2, out bool _, out bool uturn2) || !right2 || !uturn2)
                    {
                        break;
                    }
                    num5 = j;
                    if ((targetPosition2.m_Owner == sourcePositionLast.m_Owner && targetPosition2.m_LaneData.m_Carriageway == sourcePositionLast.m_LaneData.m_Carriageway) || isForbidRightTurn)
                    {
                        num6 = j;
                    }
                    if (isForbidRightTurn)
                    {
                        num7 = j;
                    }
                }
                int num8 = 0;
                int num9 = 0;
                while (num2 + num8 < targetBuffer.Length - num5)
                {
                    ConnectPosition targetPosition3 = targetBuffer[num2 + num8];
                    int k;
                    for (k = num8 + 1; num2 + k < targetBuffer.Length - num5; k++)
                    {
                        ConnectPosition connectPosition3 = targetBuffer[num2 + k];
                        if (connectPosition3.m_GroupIndex != targetPosition3.m_GroupIndex)
                        {
                            break;
                        }
                        targetPosition3 = connectPosition3;
                    }
                    if (!IsTurn(sourcePositionFirst, targetPosition3, out bool right3, out bool _, out bool _) || right3)
                    {
                        break;
                    }
                    num8 = k;
                    if (isForbidLeftTurn)
                    {
                        num9 = k;
                    }
                }
                int num10 = 0;
                int num11 = 0;
                while (num5 + num10 < targetBuffer.Length - num2 - num8)
                {
                    ConnectPosition targetPosition4 = targetBuffer[targetBuffer.Length - num5 - num10 - 1];
                    int l;
                    for (l = num10 + 1; num5 + l < targetBuffer.Length - num2 - num8; l++)
                    {
                        ConnectPosition connectPosition4 = targetBuffer[targetBuffer.Length - num5 - l - 1];
                        if (connectPosition4.m_GroupIndex != targetPosition4.m_GroupIndex)
                        {
                            break;
                        }
                        targetPosition4 = connectPosition4;
                    }
                    if (!IsTurn(sourcePositionLast, targetPosition4, out bool right4, out bool _, out bool _) || !right4)
                    {
                        break;
                    }
                    num10 = l;
                    if (isForbidRightTurn)
                    {
                        num11 = l;
                    }
                }
                int num12 = num2 + num5;
                int num13 = num8 + num10;
                int num14 = targetBuffer.Length - num12;
                int num15 = num14 - num13;
                int num16 = math.select(0, num15, isForbidStraight);
                int num17 = num15 - num16;
                int num18 = math.min(sourceBuffer.Length, num14);
                if (num3 + num6 == targetBuffer.Length)
                {
                    num4 = math.max(0, num4 - num3);
                    num7 = math.max(0, num7 - num6);
                    num3 = 0;
                    num6 = 0;
                }
                int num19 = num8 - num9;
                int num20 = num10 - num11;
                int num21 = num19 + num20;
                int num22 = num2 - math.max(num3, num4);
                int num23 = num5 - math.max(num6, num7);
                int num24 = num22 + num23;
                int num25 = sourceBuffer.Length - num18;
                int num26 = math.min(num22, math.max(0, num25 * num22 + num24 - 1) / math.max(1, num24));
                int num27 = math.min(num23, math.max(0, num25 * num23 + num24 - 1) / math.max(1, num24));
                if (num26 + num27 > num25)
                {
                    if (m_LeftHandTraffic)
                    {
                        num27 = num25 - num26;
                    }
                    else
                    {
                        num26 = num25 - num27;
                    }
                }
                int num28 = math.min(num18, num17);
                if (num28 >= 2 && num14 >= 4)
                {
                    int num29 = math.max(num19, num20);
                    int num30 = math.max(0, num29 - 1) * sourceBuffer.Length / (num14 - 1);
                    num28 = math.clamp(sourceBuffer.Length - num30, 1, num28);
                }
                num25 = num18 - num28;
                int num31 = math.min(num19, math.max(0, num25 * num19 + num21 - 1) / math.max(1, num21));
                int num32 = math.min(num20, math.max(0, num25 * num20 + num21 - 1) / math.max(1, num21));
                if (num31 + num32 > num25)
                {
                    if (num20 > num19)
                    {
                        num31 = num25 - num32;
                    }
                    else if (num19 > num20)
                    {
                        num32 = num25 - num31;
                    }
                    else if (m_LeftHandTraffic)
                    {
                        num31 = num25 - num32;
                    }
                    else
                    {
                        num32 = num25 - num31;
                    }
                }
                num25 = sourceBuffer.Length - num28 - num26 - num27 - num31 - num32;
                if (num25 > 0)
                {
                    if (num17 > 0)
                    {
                        num28 += num25;
                    }
                    else if (num21 > 0)
                    {
                        int num33 = (num25 * num19 + num21 - 1) / num21;
                        int num34 = (num25 * num20 + num21 - 1) / num21;
                        if (num33 + num34 > num25)
                        {
                            if (m_LeftHandTraffic)
                            {
                                num33 = num25 - num34;
                            }
                            else
                            {
                                num34 = num25 - num33;
                            }
                        }
                        num31 += num33;
                        num32 += num34;
                    }
                    else if (num24 > 0)
                    {
                        int num35 = (num25 * num22 + num24 - 1) / num24;
                        int num36 = (num25 * num23 + num24 - 1) / num24;
                        if (num35 + num36 > num25)
                        {
                            if (m_LeftHandTraffic)
                            {
                                num35 = num25 - num36;
                            }
                            else
                            {
                                num36 = num25 - num35;
                            }
                        }
                        num26 += num35;
                        num27 += num36;
                    }
                    else if (num15 > 0)
                    {
                        num28 += num25;
                    }
                    else if (num13 > 0)
                    {
                        int num37 = (num25 * num8 + num13 - 1) / num13;
                        int num38 = (num25 * num10 + num13 - 1) / num13;
                        if (num37 + num38 > num25)
                        {
                            if (m_LeftHandTraffic)
                            {
                                num37 = num25 - num38;
                            }
                            else
                            {
                                num38 = num25 - num37;
                            }
                        }
                        num31 += num37;
                        num32 += num38;
                    }
                    else if (num12 > 0)
                    {
                        int num39 = (num25 * num2 + num12 - 1) / num12;
                        int num40 = (num25 * num5 + num12 - 1) / num12;
                        if (num39 + num40 > num25)
                        {
                            if (m_LeftHandTraffic)
                            {
                                num39 = num25 - num40;
                            }
                            else
                            {
                                num40 = num25 - num39;
                            }
                        }
                        num26 += num39;
                        num27 += num40;
                    }
                    else
                    {
                        num28 += num25;
                    }
                }
                int num41 = math.max(num26, math.select(0, 1, num2 != 0));
                int num42 = math.max(num27, math.select(0, 1, num5 != 0));
                int num43 = num31 + math.select(0, 1, (num8 > num31) & (sourceBuffer.Length > num31));
                int num44 = num32 + math.select(0, 1, (num10 > num32) & (sourceBuffer.Length > num32));
                if (num28 == 0 && num43 > num31 && num44 > num32)
                {
                    if (num44 > num43)
                    {
                        num44 = math.max(1, num44 - 1);
                    }
                    else if (num43 > num44)
                    {
                        num43 = math.max(1, num43 - 1);
                    }
                    else if (m_LeftHandTraffic ? (num43 <= 1) : (num44 > 1))
                    {
                        num44 = math.max(1, num44 - 1);
                    }
                    else
                    {
                        num43 = math.max(1, num43 - 1);
                    }
                }
                int curTargetPosIdx = 0;
                while (curTargetPosIdx < targetBuffer.Length)
                {
                    ConnectPosition connectPosition5 = targetBuffer[curTargetPosIdx];
                    ConnectPosition targetPosition5 = connectPosition5;
                    int m;
                    for (m = curTargetPosIdx + 1; m < targetBuffer.Length; m++)
                    {
                        ConnectPosition connectPosition6 = targetBuffer[m];
                        if (connectPosition6.m_GroupIndex != connectPosition5.m_GroupIndex)
                        {
                            break;
                        }
                        targetPosition5 = connectPosition6;
                    }
                    int num46 = m - curTargetPosIdx;
                    int num47 = targetBuffer.Length - m;
                    uint group = (uint)(sourcePositionFirst.m_GroupIndex | (connectPosition5.m_GroupIndex << 16));
                    bool isTurn = curTargetPosIdx < num2 + num8 || num47 < num5 + num10;
                    bool isUTurn = curTargetPosIdx < num2 || num47 < num5;
                    bool flag5 = curTargetPosIdx < num3 || num47 < num6;
                    bool isForbidden = curTargetPosIdx < num4 + num9 || num47 < num7 + num11;
                    bool isRight = num47 < num5 + num10;
                    bool isGentle = false;
                    int num48;
                    int num49;
                    if (isTurn)
                    {
                        bool right5;
                        bool uturn5;
                        if (isUTurn)
                        {
                            if (isRight)
                            {
                                num48 = (num47 * num42 + math.select(0, num5 - 1, num42 > num5)) / num5;
                                num49 = ((num47 + num46) * num42 + num5 - 1) / num5 - 1;
                                num48 = sourceBuffer.Length - num48 - 1;
                                num49 = sourceBuffer.Length - num49 - 1;
                                CommonUtils.Swap(ref num48, ref num49);
                            }
                            else
                            {
                                int num50 = curTargetPosIdx;
                                num48 = (num50 * num41 + math.select(0, num2 - 1, num41 > num2)) / num2;
                                num49 = ((num50 + num46) * num41 + num2 - 1) / num2 - 1;
                            }
                        }
                        else if (isRight)
                        {
                            int num51 = num47 - num5;
                            num48 = (num51 * num44 + math.select(0, num10 - 1, num44 > num10)) / num10;
                            num49 = ((num51 + num46) * num44 + num10 - 1) / num10 - 1;
                            num48 = sourceBuffer.Length - num27 - num48 - 1;
                            num49 = sourceBuffer.Length - num27 - num49 - 1;
                            CommonUtils.Swap(ref num48, ref num49);
                            IsTurn(sourceBuffer[num48], connectPosition5, out right5, out bool gentle5, out uturn5);
                            IsTurn(sourceBuffer[num49], targetPosition5, out uturn5, out bool gentle6, out right5);
                            isGentle = (gentle5 && gentle6);
                        }
                        else
                        {
                            int num52 = curTargetPosIdx - num2;
                            num48 = (num52 * num43 + math.select(0, num8 - 1, num43 > num8)) / num8;
                            num49 = ((num52 + num46) * num43 + num8 - 1) / num8 - 1;
                            num48 = num26 + num48;
                            num49 = num26 + num49;
                            IsTurn(sourceBuffer[num48], connectPosition5, out right5, out bool gentle7, out uturn5);
                            IsTurn(sourceBuffer[num49], targetPosition5, out uturn5, out bool gentle8, out right5);
                            isGentle = (gentle7 && gentle8);
                        }
                    }
                    else
                    {
                        int num53 = curTargetPosIdx - num2 - num8;
                        if (num28 == 0)
                        {
                            num48 = ((!m_LeftHandTraffic) ? math.min(num26 + num31, sourceBuffer.Length - 1) : math.max(num26 + num31 - 1, 0));
                            num49 = num48;
                        }
                        else
                        {
                            num48 = (num53 * num28 + math.select(0, num15 - 1, num28 > num15)) / num15;
                            num49 = ((num53 + num46) * num28 + num15 - 1) / num15 - 1;
                            num48 = num26 + num31 + num48;
                            num49 = num26 + num31 + num49;
                        }
                        if (num16 > 0)
                        {
                            isForbidden = ((!m_LeftHandTraffic) ? (isForbidden || num15 - num53 - 1 < num16) : (isForbidden || num53 < num16));
                        }
                    }
                    int num54 = num49 - num48 + 1;
                    int num55 = math.max(num54, num46);
                    int num56 = math.min(num54, num46);
                    int num57 = 0;
                    int num58 = num55 - num56;
                    int num59 = 0;
                    int num60 = 0;
                    float num61 = float.MaxValue;
                    int2 @int = 0;
                    if (num46 > num54)
                    {
                        @int = CalculateSourcesBetween(sourceBuffer[num48], targetBuffer[curTargetPosIdx], allSources);
                        if (math.any(@int >= 1))
                        {
                            int num62 = math.csum(@int);
                            int num63;
                            int num64;
                            if (num62 > num58)
                            {
                                num63 = @int.x * num58 / num62;
                                num64 = @int.y * num58 / num62;
                                if ((num58 >= 2) & math.all(@int >= 1))
                                {
                                    num63 = math.max(num63, 1);
                                    num64 = math.max(num64, 1);
                                }
                            }
                            else
                            {
                                num63 = @int.x;
                                num64 = @int.y;
                            }
                            num57 += num63;
                            num58 -= num64;
                        }
                    }
                    for (int n = num57; n <= num58; n++)
                    {
                        int num65 = math.max(n + num54 - num55, 0);
                        int num66 = math.max(n + num46 - num55, 0);
                        num65 += num48;
                        num66 += curTargetPosIdx;
                        ConnectPosition connectPosition7 = sourceBuffer[num65];
                        ConnectPosition connectPosition8 = sourceBuffer[num65 + num56 - 1];
                        ConnectPosition connectPosition9 = targetBuffer[num66];
                        ConnectPosition connectPosition10 = targetBuffer[num66 + num56 - 1];
                        float num67 = math.max(0f, math.dot(connectPosition7.m_Tangent, connectPosition9.m_Tangent) * -0.5f);
                        float num68 = math.max(0f, math.dot(connectPosition8.m_Tangent, connectPosition10.m_Tangent) * -0.5f);
                        num67 *= math.distance(connectPosition7.m_Position.xz, connectPosition9.m_Position.xz);
                        num68 *= math.distance(connectPosition8.m_Position.xz, connectPosition10.m_Position.xz);
                        connectPosition7.m_Position.xz += connectPosition7.m_Tangent.xz * num67;
                        connectPosition9.m_Position.xz += connectPosition9.m_Tangent.xz * num67;
                        connectPosition8.m_Position.xz += connectPosition8.m_Tangent.xz * num68;
                        connectPosition10.m_Position.xz += connectPosition10.m_Tangent.xz * num68;
                        float x = math.distancesq(connectPosition7.m_Position.xz, connectPosition9.m_Position.xz);
                        float y = math.distancesq(connectPosition8.m_Position.xz, connectPosition10.m_Position.xz);
                        float num69 = math.max(x, y);
                        if (num69 < num61)
                        {
                            num59 = math.min(num55 - num46 - n, 0);
                            num60 = math.min(num55 - num54 - n, 0);
                            num61 = num69;
                        }
                    }
                    for (int laneIndex = 0; laneIndex < num55; laneIndex++)
                    {
                        int num71 = math.clamp(laneIndex + num59, 0, num54 - 1);
                        int num72 = math.clamp(laneIndex + num60, 0, num46 - 1);
                        bool flag8 = laneIndex + num59 < 0;
                        bool flag9 = laneIndex + num59 >= num54;
                        bool flag10 = laneIndex + num60 < 0;
                        bool flag11 = laneIndex + num60 >= num46;
                        bool isMergeLeft = flag8 || flag10;
                        bool isMergeRight = flag9 || flag11;
                        bool isUnsafe = (!isTurn) ? (isMergeLeft || isMergeRight || isForbidden) : ((!isRight) ? (isMergeLeft || isMergeRight || flag5 || isForbidden || (isUTurn && num26 == 0)) : (isMergeLeft || isMergeRight || flag5 || isForbidden || (isUTurn && num27 == 0)));
                        num71 += num48;
                        num72 += curTargetPosIdx;
                        bool isLeftLimit = num71 == 0 && num72 == 0;
                        bool isRightLimit = (num71 == sourceBuffer.Length - 1) & (num72 == targetBuffer.Length - 1);
                        ConnectPosition sourcePosition3 = sourceBuffer[num71];
                        ConnectPosition connectPosition11 = targetBuffer[num72];
                        if ((sourcePosition3.m_CompositionData.m_RoadFlags & connectPosition11.m_CompositionData.m_RoadFlags & Game.Prefabs.RoadFlags.UseHighwayRules) == 0 || !((flag8 & (@int.x > 0)) | (flag9 & (@int.y > 0))))
                        {
                            LaneEndKey item = new LaneEndKey(sourcePosition3.m_Owner, sourcePosition3.m_LaneData.m_Index);
                            // Logger.Debug($"Creating Node Lane ({m}): {item.GetString()}");
                            if (modifiedLaneEndConnections.Contains(item))
                            {
                                // Logger.Debug("Banned, skipping");
                                continue;
                            }
                            // Logger.Debug("Not banned!");
                            float curviness = -1f;
                            if (CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, owner, laneBuffer, middleConnections, sourcePosition3, connectPosition11, group, (ushort)laneIndex, isUnsafe, isForbidden, isTemp, trackOnly: false, yield, ownerTemp,
                                isTurn, isRight, isGentle, isUTurn, isRoundabout: false, isLeftLimit, isRightLimit, isMergeLeft, isMergeRight, fixedTangents: false))
                            {
                                Logger.Debug($"Added Node Lane connections {new ConnectionKey(sourcePosition3, connectPosition11).GetString()} [ {sourcePosition3.m_GroupIndex}[{sourcePosition3.m_GroupIndex >> 8}] ({sourcePosition3.m_LaneData.m_Group}) | {connectPosition11.m_GroupIndex}[{connectPosition11.m_GroupIndex >> 8}] ({connectPosition11.m_LaneData.m_Group}) ] G: [{(byte)(group)}|{(byte)(group >> 8)}] | [{(byte)(group >> 16)}|{(byte)(group >> 24)}] <= {group}");
                                createdConnections.Add(new ConnectionKey(sourcePosition3, connectPosition11));
                                createdGroups.Add(new ConnectionKey(sourcePosition3.m_Owner.Index, sourcePosition3.m_LaneData.m_Group, connectPosition11.m_Owner.Index, connectPosition11.m_LaneData.m_Group));
                                // Logger.Debug("Adding to created! (CreateNodeCarLanes)");
                            }
                            if (isForbidden)
                            {
                                connectPosition11.m_UnsafeCount++;
                                connectPosition11.m_ForbiddenCount++;
                                targetBuffer[num72] = connectPosition11;
                            }
                            else if (isUTurn && (isRight ? num27 : num26) == 0)
                            {
                                connectPosition11.m_UnsafeCount++;
                                targetBuffer[num72] = connectPosition11;
                            }
                        }
                    }
                    curTargetPosIdx = m;
                }
            }

            private void ProcessTrackConnectPositions(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, NativeList<MiddleConnection> middleConnections,
                NativeParallelHashSet<ConnectionKey> createdConnections, NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer, bool isTemp, Temp ownerTemp, /*NON-STOCK*/NativeParallelHashSet<LaneEndKey> modifiedLaneEndConnections
            ) {
                sourceBuffer.Sort(default(SourcePositionComparer));
                ConnectPosition sourcePosition = sourceBuffer[0];
                SortTargets(sourcePosition, targetBuffer);
                CreateNodeTrackLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, middleConnections, createdConnections, sourceBuffer, targetBuffer, isTemp, ownerTemp, /*NON-STOCK*/modifiedLaneEndConnections);
            }

            private void CreateNodeTrackLanes(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, NativeList<MiddleConnection> middleConnections,
                NativeParallelHashSet<ConnectionKey> createdConnections, NativeList<ConnectPosition> sourceBuffer, NativeList<ConnectPosition> targetBuffer, bool isTemp, Temp ownerTemp, /*NON-STOCK*/NativeParallelHashSet<LaneEndKey> modifiedLaneEndConnections
            ) {
                ConnectPosition connectPosition = sourceBuffer[0];
                for (int i = 1; i < sourceBuffer.Length; i++)
                {
                    ConnectPosition connectPosition2 = sourceBuffer[i];
                    connectPosition.m_Position += connectPosition2.m_Position;
                    connectPosition.m_Tangent += connectPosition2.m_Tangent;
                }
                connectPosition.m_Position /= (float)sourceBuffer.Length;
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
                        float curviness = -1f;
                        /*NON-STOCK*/
                        if (modifiedLaneEndConnections.Contains(new LaneEndKey(sourcePosition.m_Owner, sourcePosition.m_LaneData.m_Index)))
                        {
                            continue;
                        }
                        /*NON-STOCK-END*/
                        CreateNodeLane(jobIndex, ref nodeLaneIndex, ref random, ref curviness, owner, laneBuffer, middleConnections, sourcePosition, targetPosition, 0u, 0, isUnsafe: false, isForbidden: false, isTemp,
                            trackOnly: true, 0, ownerTemp, isTurn, right, gentle, uturn, isRoundabout: false, isLeftLimit, isRightLimit, isMergeLeft: false, isMergeRight: false, fixedTangents: false);
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

            private bool CreateNodeLane(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, ref float curviness, Entity owner, LaneBuffer laneBuffer, NativeList<MiddleConnection> middleConnections,
                ConnectPosition sourcePosition, ConnectPosition targetPosition, uint group, ushort laneIndex, bool isUnsafe, bool isForbidden, bool isTemp, bool trackOnly, int yield, Temp ownerTemp, bool isTurn,
                bool isRight, bool isGentle, bool isUTurn, bool isRoundabout, bool isLeftLimit, bool isRightLimit, bool isMergeLeft, bool isMergeRight, bool fixedTangents
            ) {
                NetCompositionData netCompositionData = m_PrefabCompositionData[sourcePosition.m_NodeComposition];
                NetCompositionData netCompositionData2 = m_PrefabCompositionData[targetPosition.m_NodeComposition];
                if (isUTurn && (netCompositionData.m_State & CompositionState.BlockUTurn) != 0)
                {
                    // Logger.Debug("CreateNodeLane: Block Uturn!");
                    return false;
                }

                // NON-STOCK
                // if (m_TrafficUpgradeData.TryGetComponent(sourcePosition.m_Owner, out TrafficUpgrade upgrade))
                // {
                //     if (isUTurn && (sourcePosition.m_IsEnd && (upgrade.right & UpgradeType.NoUturn) != 0 || (!sourcePosition.m_IsEnd && (upgrade.left & UpgradeType.NoUturn) != 0)))
                //     {
                //         return false;
                //     } 
                //     if (!isUTurn && !isTurn && (sourcePosition.m_IsEnd && (upgrade.right & UpgradeType.NoForward) != 0 || (!sourcePosition.m_IsEnd && (upgrade.left & UpgradeType.NoForward) != 0)))
                //     {
                //         return false;
                //     }
                // }
                // NON-STOCK-END
                Owner component = default(Owner);
                component.m_Owner = owner;
                Temp temp = default(Temp);
                if (isTemp)
                {
                    temp.m_Flags = (ownerTemp.m_Flags & (TempFlags.Create | TempFlags.Delete | TempFlags.Select | TempFlags.Modify | TempFlags.Hidden));
                    if ((ownerTemp.m_Flags & (TempFlags.Replace | TempFlags.Upgrade)) != 0)
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
                    if ((laneFlags & (LaneFlags.Road | LaneFlags.Track)) == (LaneFlags.Road | LaneFlags.Track) && !CanConnectTrack(isUTurn, isRoundabout, sourcePosition, targetPosition, prefabRef))
                    {
                        laneFlags &= ~LaneFlags.Track;
                        prefabRef.m_Prefab = m_CarLaneData[prefabRef.m_Prefab].m_NotTrackLanePrefab;
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
                        if (m_NetLaneData.HasComponent(sourcePosition.m_LaneData.m_Lane))
                        {
                            NetLaneData netLaneData2 = m_NetLaneData[sourcePosition.m_LaneData.m_Lane];
                            component2.m_WidthOffset.x = netLaneData2.m_Width - netLaneData.m_Width;
                        }
                        if (m_NetLaneData.HasComponent(targetPosition.m_LaneData.m_Lane))
                        {
                            NetLaneData netLaneData3 = m_NetLaneData[targetPosition.m_LaneData.m_Lane];
                            component2.m_WidthOffset.y = netLaneData3.m_Width - netLaneData.m_Width;
                        }
                    }
                    Curve curve = default(Curve);
                    if (math.distance(sourcePosition.m_Position, targetPosition.m_Position) >= 0.01f)
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
                    }
                    ModifyCurveHeight(ref curve.m_Bezier, sourcePosition.m_BaseHeight, targetPosition.m_BaseHeight, netCompositionData, netCompositionData2);
                    UtilityLane component3 = default(UtilityLane);
                    HangingLane component4 = default(HangingLane);
                    bool flag = false;
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
                            flag = true;
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
                        bool flag3 = (sourcePosition.m_CompositionData.m_RoadFlags & targetPosition.m_CompositionData.m_RoadFlags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0;
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
                        if (flag3)
                        {
                            component5.m_Flags |= CarLaneFlags.Highway;
                        }
                        if ((netCompositionData.m_Flags.m_General & CompositionFlags.General.Intersection) != 0 || isRoundabout || isUTurn)
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
                            if (flag3)
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
                            if (component5.m_Curviness > math.select(math.PI / 180f, math.PI / 360f, flag3))
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
                        if ((sourcePosition.m_CompositionData.m_TaxiwayFlags & targetPosition.m_CompositionData.m_TaxiwayFlags & TaxiwayFlags.Runway) != 0)
                        {
                            component5.m_Flags |= CarLaneFlags.Runway;
                        }
                        switch (yield)
                        {
                            case 1:
                                component5.m_Flags |= CarLaneFlags.Yield;
                                break;
                            case 2:
                                component5.m_Flags |= CarLaneFlags.Stop;
                                break;
                            case -1:
                                component5.m_Flags |= CarLaneFlags.RightOfWay;
                                break;
                        }
                        if ((netCompositionData.m_Flags.m_General & CompositionFlags.General.TrafficLights) != 0)
                        {
                            component5.m_Flags |= CarLaneFlags.TrafficLights;
                            hasTrafficLights = true;
                        }
                        if ((netCompositionData.m_Flags.m_General & CompositionFlags.General.LevelCrossing) != 0)
                        {
                            component5.m_Flags |= CarLaneFlags.LevelCrossing;
                            hasTrafficLights = true;
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
                        if ((sourcePosition.m_LaneData.m_Flags & targetPosition.m_LaneData.m_Flags & LaneFlags.Twoway) != 0)
                        {
                            bool num5 = sourcePosition.m_IsEnd == ((sourcePosition.m_LaneData.m_Flags & LaneFlags.Invert) == 0);
                            bool flag4 = targetPosition.m_IsEnd == ((targetPosition.m_LaneData.m_Flags & LaneFlags.Invert) == 0);
                            if (num5 != flag4)
                            {
                                if (flag4)
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
                        }
                        if ((netCompositionData.m_Flags.m_General & CompositionFlags.General.LevelCrossing) != 0)
                        {
                            component6.m_Flags |= TrackLaneFlags.LevelCrossing;
                            hasTrafficLights = true;
                        }
                        if (((netCompositionData.m_Flags.m_Left | netCompositionData.m_Flags.m_Right | netCompositionData2.m_Flags.m_Left | netCompositionData2.m_Flags.m_Right) & CompositionFlags.Side.PrimaryStop) != 0)
                        {
                            component6.m_Flags |= TrackLaneFlags.Station;
                        }
                        if ((netCompositionData.m_Flags.m_General & CompositionFlags.General.Intersection) != 0 && isTurn)
                        {
                            component6.m_Flags |= (TrackLaneFlags)(isRight ? 8192/*TurnRight*/ : 4096/*TurnLeft*/);
                        }
                    }
                    Lane lane = default(Lane);
                    ushort num6;
                    if (i != 0)
                    {
                        lane.m_StartNode = new PathNode(owner, (ushort)nodeLaneIndex++);
                        num6 = (ushort)nodeLaneIndex++;
                        lane.m_MiddleNode = new PathNode(owner, num6);
                        lane.m_EndNode = new PathNode(owner, (ushort)nodeLaneIndex++);
                    }
                    else
                    {
                        lane.m_StartNode = new PathNode(sourcePosition.m_Owner, sourcePosition.m_LaneData.m_Index, sourcePosition.m_SegmentIndex);
                        num6 = (ushort)nodeLaneIndex++;
                        lane.m_MiddleNode = new PathNode(owner, num6);
                        lane.m_EndNode = new PathNode(targetPosition.m_Owner, targetPosition.m_LaneData.m_Index, targetPosition.m_SegmentIndex);
                    }
                    if ((component5.m_Flags & CarLaneFlags.Unsafe) == 0)
                    {
                        for (int m = 0; m < middleConnections.Length; m++)
                        {
                            MiddleConnection value = middleConnections[m];
                            if ((value.m_ConnectPosition.m_LaneData.m_Flags & LaneFlags.Road) == 0)
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
                            uint num7;
                            uint num8;
                            if (isRoundabout)
                            {
                                num7 = (group | (group << 16));
                                num8 = uint.MaxValue;
                            }
                            else if ((laneFlags & LaneFlags.Master) != 0)
                            {
                                num7 = group;
                                num8 = uint.MaxValue;
                            }
                            else if (value.m_IsSource)
                            {
                                num7 = group;
                                num8 = 4294901760u;
                            }
                            else
                            {
                                num7 = group;
                                num8 = 65535u;
                            }
                            int num9 = m;
                            if (value.m_TargetLane != Entity.Null)
                            {
                                value.m_Distance = float.MaxValue;
                                num9 = -1;
                                for (; m < middleConnections.Length; m++)
                                {
                                    MiddleConnection middleConnection = middleConnections[m];
                                    if (middleConnection.m_SortIndex != value.m_SortIndex)
                                    {
                                        break;
                                    }
                                    if (((middleConnection.m_TargetGroup ^ num7) & num8) == 0 && ((middleConnection.m_TargetFlags ^ laneFlags2) & LaneFlags.Master) == 0)
                                    {
                                        value = middleConnection;
                                        num9 = m;
                                    }
                                }
                                m--;
                            }
                            float num10 = math.length(MathUtils.Size(MathUtils.Bounds(curve.m_Bezier) | value.m_ConnectPosition.m_Position));
                            float num11 = MathUtils.Distance(curve.m_Bezier, new Line3.Segment(value.m_ConnectPosition.m_Position, value.m_ConnectPosition.m_Position + value.m_ConnectPosition.m_Tangent * num10),
                                out float2 t);
                            num11 += num10 * t.y;
                            if (num11 < value.m_Distance)
                            {
                                value.m_Distance = num11;
                                value.m_TargetLane = prefabRef.m_Prefab;
                                value.m_TargetOwner = (value.m_IsSource ? sourcePosition.m_Owner : targetPosition.m_Owner);
                                value.m_TargetGroup = num7;
                                value.m_TargetIndex = num6;
                                value.m_TargetCarriageway = component5.m_CarriagewayGroup;
                                value.m_TargetComposition = (value.m_IsSource ? targetPosition.m_CompositionData : sourcePosition.m_CompositionData);
                                value.m_TargetCurve = curve;
                                value.m_TargetCurvePos = t.x;
                                value.m_TargetFlags = laneFlags2;
                                if (num9 != -1)
                                {
                                    middleConnections[num9] = value;
                                }
                                else
                                {
                                    CollectionUtils.Insert(middleConnections, m + 1, value);
                                }
                            }
                        }
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
					PseudoRandomSeed componentData = default(PseudoRandomSeed);
					if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0 && !m_PseudoRandomSeedData.TryGetComponent(temp.m_Original, out componentData))
					{
						componentData = new PseudoRandomSeed(ref outRandom);
					}
                    if (laneBuffer.m_OldLanes.TryGetValue(laneKey, out Entity item))
                    {
                        laneBuffer.m_OldLanes.Remove(laneKey);
                        m_CommandBuffer.SetComponent(jobIndex, item, component2);
                        m_CommandBuffer.SetComponent(jobIndex, item, curve);
						if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
						{
							m_CommandBuffer.AddComponent(jobIndex, item, componentData);
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
                        if (flag)
                        {
                            m_CommandBuffer.AddComponent(jobIndex, item, component4);
                        }
                        if (isTemp)
                        {
                            m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, item);
                            m_CommandBuffer.AddComponent(jobIndex, item, default(Updated));
                            m_CommandBuffer.SetComponent(jobIndex, item, temp);
                        }
                        else if (m_TempData.HasComponent(item))
                        {
                            m_CommandBuffer.RemoveComponent(jobIndex, item, in m_DeletedTempTypes);
                            m_CommandBuffer.AddComponent(jobIndex, item, in m_AppliedTypes);
                        }
                        else
                        {
                            m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, item);
                            m_CommandBuffer.AddComponent(jobIndex, item, default(Updated));
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
                    EntityArchetype entityArchetype = default(EntityArchetype);
                    NetLaneArchetypeData netLaneArchetypeData = m_PrefabLaneArchetypeData[prefabRef.m_Prefab];
                    entityArchetype = (((laneFlags & LaneFlags.Slave) != 0)
                        ? netLaneArchetypeData.m_NodeSlaveArchetype
                        : (((laneFlags & LaneFlags.Master) == 0) ? netLaneArchetypeData.m_NodeLaneArchetype : netLaneArchetypeData.m_NodeMasterArchetype));
                    Entity e = m_CommandBuffer.CreateEntity(jobIndex, entityArchetype);
                    m_CommandBuffer.SetComponent(jobIndex, e, prefabRef);
                    m_CommandBuffer.SetComponent(jobIndex, e, lane);
                    m_CommandBuffer.SetComponent(jobIndex, e, component2);
                    m_CommandBuffer.SetComponent(jobIndex, e, curve);
					if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
					{
						m_CommandBuffer.SetComponent(jobIndex, e, componentData);
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
                    if (flag)
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
                    m_CommandBuffer.AddComponent(jobIndex, e, component);
                    if (hasTrafficLights)
                    {
                        m_CommandBuffer.AddComponent(jobIndex, e, default(LaneSignal));
                    }
                    if (isTemp)
                    {
                        m_CommandBuffer.AddComponent(jobIndex, e, temp);
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

            private void CreateNodeUtilityLanes(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, NativeList<ConnectPosition> buffer1,
                NativeList<ConnectPosition> buffer2, NativeList<MiddleConnection> middleConnections, bool isTemp, Temp ownerTemp
            ) {
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
                                CreateNodeUtilityLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, buffer1, buffer2, middleConnections, new int2(num, m), isTemp, ownerTemp);
                            }
                            num7 = connectPosition3.m_Order;
                            num = m;
                        }
                    }
                    if (buffer1.Length > num)
                    {
                        CreateNodeUtilityLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, buffer1, buffer2, middleConnections, new int2(num, buffer1.Length), isTemp, ownerTemp);
                    }
                }
                else
                {
                    CreateNodeUtilityLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, buffer1, buffer2, middleConnections, new int2(0, buffer1.Length), isTemp, ownerTemp);
                }
            }

            private void CreateNodeUtilityLanes(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, NativeList<ConnectPosition> buffer1,
                NativeList<ConnectPosition> buffer2, NativeList<MiddleConnection> middleConnections, int2 bufferRange, bool isTemp, Temp ownerTemp
            ) {
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
                if (num < 2 && (num <= 0 || buffer2.Length <= 0))
                {
                    return;
                }
                float3 position = (num < 2) ? buffer1[bufferRange.x].m_Position : CalculateUtilityConnectPosition(buffer1, bufferRange);
                int endNodeLaneIndex = nodeLaneIndex++;
                if (num >= 2)
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
                    if ((ownerTemp.m_Flags & (TempFlags.Replace | TempFlags.Upgrade)) != 0)
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
                ushort num = (ushort)nodeLaneIndex++;
                lane.m_MiddleNode = new PathNode(owner, num);
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
                        float num2 = MathUtils.Distance(curve.m_Bezier, value2.m_ConnectPosition.m_Position, out t);
                        if (num2 < value2.m_Distance)
                        {
                            value2.m_Distance = num2;
                            value2.m_TargetLane = connectPosition1.m_LaneData.m_Lane;
                            value2.m_TargetIndex = num;
                            value2.m_TargetCurve = curve;
                            value2.m_TargetCurvePos = t;
                            value2.m_TargetFlags = connectPosition1.m_LaneData.m_Flags;
                            middleConnections[i] = value2;
                        }
                    }
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
                    m_CommandBuffer.SetComponent(jobIndex, item, curve);
					if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
					{
						m_CommandBuffer.AddComponent(jobIndex, item, componentData);
					}
                    if (isTemp)
                    {
                        m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, item);
                        m_CommandBuffer.AddComponent(jobIndex, item, default(Updated));
                        m_CommandBuffer.SetComponent(jobIndex, item, temp);
                    }
                    else if (m_TempData.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent(jobIndex, item, in m_DeletedTempTypes);
                        m_CommandBuffer.AddComponent(jobIndex, item, in m_AppliedTypes);
                    }
                    else
                    {
                        m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, item);
                        m_CommandBuffer.AddComponent(jobIndex, item, default(Updated));
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
                    m_CommandBuffer.AddComponent(jobIndex, e, component);
                    if (isTemp)
                    {
                        m_CommandBuffer.AddComponent(jobIndex, e, temp);
                    }
                }
            }

            private void CreateNodePedestrianLanes(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, NativeList<ConnectPosition> buffer,
                NativeList<ConnectPosition> tempBuffer, NativeList<ConnectPosition> tempBuffer2, bool isTemp, Temp ownerTemp, float3 middlePosition, float middleRadius, float roundaboutSize
            ) {
                if (buffer.Length <= 1)
                {
                    return;
                }
                buffer.Sort(default(SourcePositionComparer));
                int num = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    ConnectPosition connectPosition = buffer[i];
                    num += math.select(1, 0, connectPosition.m_IsSideConnection);
                }
                int num2 = -1;
                int num3 = 0;
                Segment left = default(Segment);
                while (num3 < buffer.Length)
                {
                    ConnectPosition connectPosition2 = buffer[num3];
                    int j;
                    for (j = num3 + 1; j < buffer.Length && !(buffer[j].m_Owner != connectPosition2.m_Owner); j++)
                    {
                    }
                    ConnectPosition connectPosition3 = buffer[j - 1];
                    if (connectPosition3.m_IsSideConnection)
                    {
                        num3 = j;
                        continue;
                    }
                    int num4 = nodeLaneIndex;
                    if (FindNextRightLane(connectPosition3, buffer, out int2 result))
                    {
                        ConnectPosition targetPosition = buffer[result.x];
                        int num5 = 0;
                        while (targetPosition.m_IsSideConnection)
                        {
                            if (++num5 > buffer.Length)
                            {
                                tempBuffer2.Clear();
                                return;
                            }
                            for (int k = result.x; k < result.y; k++)
                            {
                                ConnectPosition value = buffer[k];
                                tempBuffer2.Add(in value);
                            }
                            int index = result.y - 1;
                            if (!FindNextRightLane(buffer[index], buffer, out result))
                            {
                                tempBuffer2.Clear();
                                return;
                            }
                            targetPosition = buffer[result.x];
                        }
                        if (num > 2)
                        {
                            CreateNodePedestrianLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, connectPosition3, targetPosition, tempBuffer2, isTemp, ownerTemp, middlePosition, middleRadius,
                                roundaboutSize);
                        }
                        else if (num == 2)
                        {
                            if (connectPosition3.m_Owner.Index == targetPosition.m_Owner.Index)
                            {
                                CreateNodePedestrianLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, connectPosition3, targetPosition, tempBuffer2, isTemp, ownerTemp, middlePosition, middleRadius,
                                    roundaboutSize);
                            }
                            else if (connectPosition3.m_Owner.Index < targetPosition.m_Owner.Index)
                            {
                                CreateNodePedestrianLanes(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, connectPosition3, targetPosition, tempBuffer2, isTemp, ownerTemp, middlePosition, middleRadius,
                                    roundaboutSize);
                            }
                        }
                        tempBuffer2.Clear();
                    }
                    if (m_PrefabCompositionCrosswalks.HasBuffer(connectPosition2.m_NodeComposition))
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
                            }
                        }
                        NetCompositionData netCompositionData = m_PrefabCompositionData[connectPosition2.m_NodeComposition];
                        DynamicBuffer<NetCompositionCrosswalk> dynamicBuffer = m_PrefabCompositionCrosswalks[connectPosition2.m_NodeComposition];
                        bool flag = (netCompositionData.m_Flags.m_General & (CompositionFlags.General.Intersection | CompositionFlags.General.Crosswalk)) == CompositionFlags.General.Crosswalk;
                        if (flag && num2 == -1)
                        {
                            num2 = num4;
                            num3 = j;
                            continue;
                        }
                        if (dynamicBuffer.Length >= 1)
                        {
                            tempBuffer.ResizeUninitialized(dynamicBuffer.Length + 1);
                            for (int l = 0; l < tempBuffer.Length; l++)
                            {
                                float3 @float = (l != 0) ? ((l != tempBuffer.Length - 1) ? math.lerp(dynamicBuffer[l - 1].m_End, dynamicBuffer[l].m_Start, 0.5f) : dynamicBuffer[l - 1].m_End) : dynamicBuffer[l].m_Start;
                                float s = @float.x / math.max(1f, netCompositionData.m_Width) + 0.5f;
                                ConnectPosition value2 = default(ConnectPosition);
                                value2.m_Position = math.lerp(left.m_Left.a, left.m_Right.a, s);
                                value2.m_Position.y += @float.y;
                                tempBuffer[l] = value2;
                            }
                            for (int m = num3; m < j; m++)
                            {
                                ConnectPosition value3 = buffer[m];
                                float num6 = float.MaxValue;
                                int index2 = 0;
                                for (int n = 0; n < tempBuffer.Length; n++)
                                {
                                    ConnectPosition connectPosition4 = tempBuffer[n];
                                    if (connectPosition4.m_Owner == Entity.Null)
                                    {
                                        float num7 = math.lengthsq(value3.m_Position - connectPosition4.m_Position);
                                        if (num7 < num6)
                                        {
                                            num6 = num7;
                                            index2 = n;
                                        }
                                    }
                                }
                                tempBuffer[index2] = value3;
                            }
                            for (int num8 = 0; num8 < tempBuffer.Length; num8++)
                            {
                                ConnectPosition value4 = tempBuffer[num8];
                                if (value4.m_Owner == Entity.Null)
                                {
                                    value4.m_Owner = owner;
                                    value4.m_NodeComposition = connectPosition2.m_NodeComposition;
                                    value4.m_EdgeComposition = connectPosition2.m_EdgeComposition;
                                    value4.m_Tangent = connectPosition2.m_Tangent;
                                    tempBuffer[num8] = value4;
                                }
                            }
                            PathNode pathNode = default(PathNode);
                            PathNode endPathNode = default(PathNode);
                            for (int num9 = 0; num9 < dynamicBuffer.Length; num9++)
                            {
                                NetCompositionCrosswalk netCompositionCrosswalk = dynamicBuffer[num9];
                                ConnectPosition sourcePosition = tempBuffer[num9];
                                ConnectPosition targetPosition2 = tempBuffer[num9 + 1];
                                float s2 = netCompositionCrosswalk.m_Start.x / math.max(1f, netCompositionData.m_Width) + 0.5f;
                                float s3 = netCompositionCrosswalk.m_End.x / math.max(1f, netCompositionData.m_Width) + 0.5f;
                                if (flag)
                                {
                                    sourcePosition.m_Position = math.lerp(left.m_Left.d, left.m_Right.d, s2);
                                    targetPosition2.m_Position = math.lerp(left.m_Left.d, left.m_Right.d, s3);
                                    if (num9 == 0)
                                    {
                                        sourcePosition.m_Owner = owner;
                                        pathNode = new PathNode(owner, (ushort)num2, 0.5f);
                                        endPathNode = pathNode;
                                    }
                                    if (num9 == dynamicBuffer.Length - 1)
                                    {
                                        targetPosition2.m_Owner = owner;
                                        endPathNode = new PathNode(owner, (ushort)num4, 0.5f);
                                    }
                                }
                                else
                                {
                                    sourcePosition.m_Position = math.lerp(left.m_Left.a, left.m_Right.a, s2);
                                    targetPosition2.m_Position = math.lerp(left.m_Left.a, left.m_Right.a, s3);
                                    sourcePosition.m_Position += sourcePosition.m_Tangent * netCompositionCrosswalk.m_Start.z;
                                    targetPosition2.m_Position += targetPosition2.m_Tangent * netCompositionCrosswalk.m_End.z;
                                    if (num9 == 0 && sourcePosition.m_Owner == owner)
                                    {
                                        pathNode = new PathNode(owner, (ushort)nodeLaneIndex++);
                                        endPathNode = pathNode;
                                    }
                                }
                                sourcePosition.m_Position.y += netCompositionCrosswalk.m_Start.y;
                                targetPosition2.m_Position.y += netCompositionCrosswalk.m_End.y;
                                sourcePosition.m_LaneData.m_Lane = netCompositionCrosswalk.m_Lane;
                                targetPosition2.m_LaneData.m_Lane = netCompositionCrosswalk.m_Lane;
                                CreateNodePedestrianLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, sourcePosition, targetPosition2, pathNode, endPathNode, isCrosswalk: true, isSideConnection: false,
                                    isTemp, ownerTemp, fixedTangents: false, (netCompositionCrosswalk.m_Flags & LaneFlags.CrossRoad) != 0, out Bezier4x3 _, out PathNode _, out PathNode endNode);
                                pathNode = endNode;
                                endPathNode = endNode;
                            }
                        }
                    }
                    num3 = j;
                }
            }

            private void CreateNodePedestrianLanes(int jobIndex, ref int nodeLaneIndex, ref Unity.Mathematics.Random random, Entity owner, LaneBuffer laneBuffer, ConnectPosition sourcePosition,
                ConnectPosition targetPosition, NativeList<ConnectPosition> sideConnections, bool isTemp, Temp ownerTemp, float3 middlePosition, float middleRadius, float roundaboutSize
            ) {
                float t;
                Bezier4x3 curve2;
                if (middleRadius == 0f)
                {
                    ConnectPosition sourcePosition2 = sourcePosition;
                    ConnectPosition targetPosition2 = targetPosition;
                    PathNode endNode = default(PathNode);
                    CreateNodePedestrianLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, sourcePosition2, targetPosition2, default(PathNode), endNode, isCrosswalk: false, isSideConnection: false, isTemp,
                        ownerTemp, fixedTangents: false, hasSignals: true, out Bezier4x3 curve, out PathNode middleNode, out PathNode endNode2);
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
                        CreateNodePedestrianLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, sourcePosition3, targetPosition3, pathNode, pathNode, isCrosswalk: false, isSideConnection: true, isTemp,
                            ownerTemp, fixedTangents: false, hasSignals: true, out curve2, out endNode2, out endNode);
                    }
                    return;
                }
                float2 float2 = math.normalizesafe(sourcePosition.m_Position.xz - middlePosition.xz);
                float2 toVector = math.normalizesafe(targetPosition.m_Position.xz - middlePosition.xz);
                NetCompositionData netCompositionData = m_PrefabCompositionData[sourcePosition.m_NodeComposition];
                NetCompositionData netCompositionData2 = m_PrefabCompositionData[targetPosition.m_NodeComposition];
                float x = netCompositionData.m_Width * 0.5f - sourcePosition.m_LaneData.m_Position.x;
                float y = netCompositionData2.m_Width * 0.5f + targetPosition.m_LaneData.m_Position.x;
                float num2 = middleRadius + roundaboutSize - math.lerp(x, y, 0.5f);
                float num3 = MathUtils.RotationAngleLeft(float2, toVector);
                int num4 = 1 + math.max(1, Mathf.CeilToInt(num3 * (2f / math.PI) - 0.003141593f));
                if (num4 == 2)
                {
                    float y2 = MathUtils.Distance(NetUtils.FitCurve(sourcePosition.m_Position, sourcePosition.m_Tangent, -targetPosition.m_Tangent, targetPosition.m_Position).xz, middlePosition.xz, out t);
                    num2 = math.max(num2, y2);
                }
                ConnectPosition connectPosition = sourcePosition;
                float x2 = 0f;
                PathNode pathNode2 = default(PathNode);
                for (int j = 1; j <= num4; j++)
                {
                    float num5 = math.saturate(((float)j - 0.5f) / ((float)num4 - 1f));
                    ConnectPosition connectPosition2 = default(ConnectPosition);
                    if (j == num4)
                    {
                        connectPosition2 = targetPosition;
                    }
                    else
                    {
                        float2 float3 = MathUtils.RotateLeft(float2, num3 * num5);
                        connectPosition2.m_LaneData.m_Lane = sourcePosition.m_LaneData.m_Lane;
                        connectPosition2.m_LaneData.m_Flags = sourcePosition.m_LaneData.m_Flags;
                        connectPosition2.m_NodeComposition = sourcePosition.m_NodeComposition;
                        connectPosition2.m_EdgeComposition = sourcePosition.m_EdgeComposition;
                        connectPosition2.m_Owner = owner;
                        connectPosition2.m_BaseHeight = math.lerp(sourcePosition.m_BaseHeight, targetPosition.m_BaseHeight, num5);
                        connectPosition2.m_Position.y = math.lerp(sourcePosition.m_Position.y, targetPosition.m_Position.y, num5);
                        connectPosition2.m_Position.xz = middlePosition.xz + float3 * num2;
                        connectPosition2.m_Tangent.xz = MathUtils.Right(float3);
                    }
                    ConnectPosition sourcePosition4 = connectPosition;
                    ConnectPosition targetPosition4 = connectPosition2;
                    PathNode endNode;
                    if (j > 1 && j < num4)
                    {
                        CreateNodePedestrianLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, sourcePosition4, targetPosition4, pathNode2, pathNode2, isCrosswalk: false, isSideConnection: false, isTemp,
                            ownerTemp, fixedTangents: false, hasSignals: true, out curve2, out endNode, out PathNode endNode3);
                        pathNode2 = endNode3;
                    }
                    else
                    {
                        float num6 = math.lerp(x2, num5, 0.5f);
                        float2 float4 = MathUtils.RotateLeft(float2, num3 * num6);
                        float3 centerPosition = middlePosition;
                        centerPosition.y = math.lerp(connectPosition.m_Position.y, connectPosition2.m_Position.y, 0.5f);
                        centerPosition.xz += float4 * num2;
                        float3 centerTangent = default(float3);
                        centerTangent.xz = MathUtils.Left(float4);
                        if (j == 1)
                        {
                            PresetCurve(ref sourcePosition4, ref targetPosition4, middlePosition, centerPosition, centerTangent, num2, 0f, num3 / (float)num4, 2f);
                        }
                        else
                        {
                            PresetCurve(ref sourcePosition4, ref targetPosition4, middlePosition, centerPosition, centerTangent, num2, num3 / (float)num4, 0f, 2f);
                        }
                        CreateNodePedestrianLane(jobIndex, ref nodeLaneIndex, ref random, owner, laneBuffer, sourcePosition4, targetPosition4, pathNode2, pathNode2, isCrosswalk: false, isSideConnection: false, isTemp,
                            ownerTemp, fixedTangents: true, hasSignals: true, out curve2, out endNode, out PathNode endNode4);
                        pathNode2 = endNode4;
                    }
                    connectPosition = connectPosition2;
                    connectPosition.m_Tangent = -connectPosition2.m_Tangent;
                    x2 = num5;
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
                ConnectPosition targetPosition, PathNode startPathNode, PathNode endPathNode, bool isCrosswalk, bool isSideConnection, bool isTemp, Temp ownerTemp, bool fixedTangents, bool hasSignals,
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
                    if ((ownerTemp.m_Flags & (TempFlags.Replace | TempFlags.Upgrade)) != 0)
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
                NetLaneData netLaneData = m_NetLaneData[component2.m_Prefab];
                if (m_NetLaneData.HasComponent(sourcePosition.m_LaneData.m_Lane))
                {
                    NetLaneData netLaneData2 = m_NetLaneData[sourcePosition.m_LaneData.m_Lane];
                    component4.m_WidthOffset.x = netLaneData2.m_Width - netLaneData.m_Width;
                }
                if (m_NetLaneData.HasComponent(targetPosition.m_LaneData.m_Lane))
                {
                    NetLaneData netLaneData3 = m_NetLaneData[targetPosition.m_LaneData.m_Lane];
                    component4.m_WidthOffset.y = netLaneData3.m_Width - netLaneData.m_Width;
                }
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
                        lhs = lhs * (netLaneData.m_Width + component4.m_WidthOffset) * 0.5f;
                        lhs = math.select(math.saturate(lhs / component5.m_Length), 0f, lhs < 0.01f);
                    }
                }
                else
                {
                    if (sourcePosition.m_Owner == targetPosition.m_Owner && (startCompositionData.m_Flags.m_General & (CompositionFlags.General.DeadEnd | CompositionFlags.General.Roundabout)) == 0)
                    {
                        return;
                    }
                    if (math.distance(sourcePosition.m_Position, targetPosition.m_Position) >= 0.01f)
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
                    }
                    if (!isSideConnection)
                    {
                        component3.m_Flags |= PedestrianLaneFlags.AllowMiddle;
                        ModifyCurveHeight(ref component5.m_Bezier, sourcePosition.m_BaseHeight, targetPosition.m_BaseHeight, startCompositionData, endCompositionData);
                    }
                    component5.m_Length = MathUtils.Length(component5.m_Bezier);
                    hasSignals &= ((startCompositionData.m_Flags.m_General & CompositionFlags.General.LevelCrossing) != 0);
                }
                curve = component5.m_Bezier;
                if ((sourcePosition.m_LaneData.m_Flags & targetPosition.m_LaneData.m_Flags & LaneFlags.OnWater) != 0)
                {
                    component3.m_Flags |= PedestrianLaneFlags.OnWater;
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
				if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0 && !m_PseudoRandomSeedData.TryGetComponent(temp.m_Original, out componentData))
				{
					componentData = new PseudoRandomSeed(ref outRandom);
				}
                CutRange elem;
                if (laneBuffer.m_OldLanes.TryGetValue(laneKey, out Entity item))
                {
                    laneBuffer.m_OldLanes.Remove(laneKey);
                    m_CommandBuffer.SetComponent(jobIndex, item, component4);
                    m_CommandBuffer.SetComponent(jobIndex, item, component5);
                    m_CommandBuffer.SetComponent(jobIndex, item, component3);
					if ((netLaneData.m_Flags & LaneFlags.PseudoRandom) != 0)
					{
						m_CommandBuffer.AddComponent(jobIndex, item, componentData);
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
                        m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, item);
                        m_CommandBuffer.AddComponent(jobIndex, item, default(Updated));
                        m_CommandBuffer.SetComponent(jobIndex, item, temp);
                    }
                    else if (m_TempData.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent(jobIndex, item, in m_DeletedTempTypes);
                        m_CommandBuffer.AddComponent(jobIndex, item, in m_AppliedTypes);
                    }
                    else
                    {
                        m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, item);
                        m_CommandBuffer.AddComponent(jobIndex, item, default(Updated));
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
                m_CommandBuffer.AddComponent(jobIndex, e, component);
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
                if (isTemp)
                {
                    m_CommandBuffer.AddComponent(jobIndex, e, temp);
                }
            }

            // void IJobChunk.Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
            //     Execute(in chunk, unfilteredChunkIndex, useEnabledMask, in chunkEnabledMask);
            // }
        }
    }
}
