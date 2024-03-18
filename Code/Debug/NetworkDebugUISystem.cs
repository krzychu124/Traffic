using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Colossal.UI.Binding;
using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Game.UI;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Traffic.Debug
{
    public partial class NetworkDebugUISystem : UISystemBase
    {
        public override GameMode gameMode => GameMode.GameOrEditor;
        private ValueBinding<DebugData[]> _debugData;
        private EntityQuery _query;
        private Camera _mainCamera;
        private List<DebugData> _datas;
        
        protected override void OnCreate()
        {
            base.OnCreate();
            _datas = new List<DebugData>();
            AddBinding(_debugData = new ValueBinding<DebugData[]>(Mod.MOD_NAME, "debugTexts", Array.Empty<DebugData>(), new ArrayWriter<DebugData>(new ValueWriter<DebugData>())));
            _query = GetEntityQuery(new EntityQueryDesc()
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<ConnectedEdge>(), ComponentType.ReadOnly<NodeGeometry>(), ComponentType.ReadOnly<Road>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Hidden>()}
            }, new EntityQueryDesc()
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<ConnectedNode>(), ComponentType.ReadOnly<Curve>(),  ComponentType.ReadOnly<EdgeGeometry>(), ComponentType.ReadOnly<Road>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Hidden>()}
            });
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            
            int oldCount = _datas.Count;
            if (_query.IsEmptyIgnoreFilter)
            {
                if (oldCount > 0)
                {
                    _debugData.Update(Array.Empty<DebugData>());
                    _datas.Clear();
                }
                return;
            }
            
            if (!_mainCamera)
            {
                _mainCamera = Camera.main;
            }
            if (!_mainCamera)
            {
                return;
            }
            _datas.Clear();
            ComponentTypeHandle<Node> nodeTypeHandle = SystemAPI.GetComponentTypeHandle<Node>(true);
            ComponentTypeHandle<Temp> tempTypeHandle = SystemAPI.GetComponentTypeHandle<Temp>(true);
            ComponentTypeHandle<Edge> edgeTypeHandle = SystemAPI.GetComponentTypeHandle<Edge>(true);
            ComponentTypeHandle<Curve> curveTypeHandle = SystemAPI.GetComponentTypeHandle<Curve>(true);
            ComponentTypeHandle<Composition> compositionTypeHandle = SystemAPI.GetComponentTypeHandle<Composition>(true);
            ComponentLookup<Edge> edgeLookup = SystemAPI.GetComponentLookup<Edge>(true);
            BufferTypeHandle<ConnectedEdge> connectedEdgeTypeHandle = SystemAPI.GetBufferTypeHandle<ConnectedEdge>(true);
            EntityTypeHandle entityTypeHandle = SystemAPI.GetEntityTypeHandle();
            NativeArray<ArchetypeChunk> chunks = _query.ToArchetypeChunkArray(Allocator.Temp);
            foreach (ArchetypeChunk archetypeChunk in chunks)
            {
                bool isTemp = archetypeChunk.Has(ref tempTypeHandle);
                NativeArray<Temp> temps = archetypeChunk.GetNativeArray(ref tempTypeHandle);
                if (archetypeChunk.Has<Node>())
                {
                    NativeArray<Entity> entities = archetypeChunk.GetNativeArray(entityTypeHandle);
                    NativeArray<Node> nodes = archetypeChunk.GetNativeArray(ref nodeTypeHandle);
                    BufferAccessor<ConnectedEdge> connectedEdgesAccessor = archetypeChunk.GetBufferAccessor(ref connectedEdgeTypeHandle);
                    for (int index = 0; index < nodes.Length; index++)
                    {
                        Node node = nodes[index];
                        var pos = _mainCamera.WorldToScreenPoint(new Vector3(node.m_Position.x, node.m_Position.y, node.m_Position.z));
                        pos.y = Screen.height - pos.y;
                        if (pos.x is <= 0 or > 1900 || pos.y is <= 0 or > 950 || pos.z <= 0)
                        {
                            continue;
                        }
                        DynamicBuffer<ConnectedEdge> connectedEdges = connectedEdgesAccessor[index];
                        string info = $"ConnectedEdges ({connectedEdges.Length})";
                        for (var i = 0; i < connectedEdges.Length; i++)
                        {
                            ConnectedEdge connectedEdge = connectedEdges[i];
                            Edge edge = EntityManager.GetComponentData<Edge>(connectedEdge.m_Edge);
                            info += $"\nEdge: {connectedEdge.m_Edge}, Start: {edge.m_Start} End {edge.m_End}";
                        }

                        var item = new DebugData()
                        {
                            entity = entities[index],
                            position = node.m_Position,
                            position2d = pos,
                            isTemp = isTemp,
                            isEdge = false,
                            value = info,
                        };

                        if (isTemp && temps.Length > 0)
                        {
                            item.flags = temps[index].m_Flags;
                            item.original = temps[index].m_Original;
                            item.originalIsEdge = temps[index].m_Original != Entity.Null && edgeLookup.HasComponent(temps[index].m_Original);
                        }

                        _datas.Add(item);
                    }
                } 
                else if (archetypeChunk.Has<Edge>())
                {
                    NativeArray<Entity> entities = archetypeChunk.GetNativeArray(SystemAPI.GetEntityTypeHandle());
                    NativeArray<Edge> edges = archetypeChunk.GetNativeArray(ref edgeTypeHandle);
                    NativeArray<Curve> curves = archetypeChunk.GetNativeArray(ref curveTypeHandle);
                    bool hasComposition = archetypeChunk.Has(ref compositionTypeHandle);
                    NativeArray<Composition> compositions = archetypeChunk.GetNativeArray(ref compositionTypeHandle);
                    for (var i = 0; i < edges.Length; i++)
                    {
                        Edge edge = edges[i];
                        Curve curve = curves[i];
                        float3 middleEdgePos = MathUtils.Position(curve.m_Bezier, 0.5f);
                        var pos = _mainCamera.WorldToScreenPoint(new Vector3(middleEdgePos.x, middleEdgePos.y, middleEdgePos.z));
                        pos.y = Screen.height - pos.y;
                        if (pos.x is <= 0 or > 1900 || pos.y is <= 0 or > 950 || pos.z <= 0)
                        {
                            continue;
                        }
                        
                        string info = $"StartNode: {edge.m_Start} EndNode: {edge.m_End}\n";
                        if (hasComposition)
                        {
                            Composition composition = compositions[i];
                            info += $"Composition: \n Edge: {composition.m_Edge}\n StartNode: {composition.m_StartNode}\n EndNode: {composition.m_EndNode}";
                        }
                        var item = new DebugData()
                        {
                            entity = entities[i],
                            isEdge = true,
                            position = middleEdgePos,
                            position2d = pos,
                            isTemp = isTemp,
                            value = info,
                        };

                        if (isTemp && temps.Length > 0)
                        {
                            item.flags = temps[i].m_Flags;
                            item.original = temps[i].m_Original;
                            item.originalIsEdge = temps[i].m_Original != Entity.Null && edgeLookup.HasComponent(temps[i].m_Original);
                        }

                        _datas.Add(item);
                    }
                }
            }
            _datas.Sort((data, debugData) => data.position2d.z.CompareTo(debugData.position2d.z));
            if (_datas.Count > 500)
            {
                _datas.RemoveRange(499, _datas.Count - 500);
            }
            _debugData.Update(_datas.ToArray());
        }

        public struct DebugData : IJsonWritable
        {
            public Entity entity;
            public float3 position;
            public float3 position2d;
            public string value;
            public bool isEdge;
            public bool isTemp;
            public TempFlags flags;
            public Entity original;
            public bool originalIsEdge;

            public void Write(IJsonWriter writer)
            {
                writer.TypeBegin(GetType().FullName);
                writer.PropertyName(nameof(entity));
                writer.Write(entity);
                writer.PropertyName(nameof(position));
                writer.Write(position);
                writer.PropertyName(nameof(position2d));
                writer.Write(position2d);
                writer.PropertyName(nameof(isEdge));
                writer.Write(isEdge);
                writer.PropertyName(nameof(isTemp));
                writer.Write(isTemp);
                writer.PropertyName(nameof(value));
                writer.Write(value);
                writer.PropertyName(nameof(flags));
                writer.Write((ulong)flags);
                writer.PropertyName(nameof(original));
                writer.Write(original);
                writer.PropertyName(nameof(originalIsEdge));
                writer.Write(originalIsEdge);
                writer.TypeEnd();
            }
        }
    }
}
