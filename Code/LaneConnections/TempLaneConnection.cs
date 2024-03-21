﻿using Colossal.Mathematics;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.LaneConnections
{
    [InternalBufferCapacity(0)]
    public struct TempLaneConnection : IBufferElementData
    {
        public Entity sourceEntity;
        public Entity targetEntity;
        public int2 laneIndexMap;
        public PathMethod method;
        public Bezier4x3 bezier;
        public bool isUnsafe;
        public ConnectionFlags flags;

        public TempLaneConnection(GeneratedConnection generatedConnection, Bezier4x3 curve) {
            sourceEntity = generatedConnection.sourceEntity;
            targetEntity = generatedConnection.targetEntity;
            laneIndexMap = generatedConnection.laneIndexMap;
            method = generatedConnection.method;
            isUnsafe = generatedConnection.isUnsafe;
            bezier = curve;
            flags = 0;
        }

        public TempLaneConnection(Entity source, Entity target, int2 map, PathMethod method, bool isUnsafe, Bezier4x3 curve, ConnectionFlags flags) {
            sourceEntity = source;
            targetEntity = target;
            laneIndexMap = map;
            bezier = curve;
            this.method = method;
            this.isUnsafe = isUnsafe;
            this.flags = flags;
        }
    }
}