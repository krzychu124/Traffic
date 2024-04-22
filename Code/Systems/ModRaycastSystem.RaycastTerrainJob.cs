using Colossal.Mathematics;
using Game.Common;
using Game.Simulation;
using Traffic.CommonData;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Traffic.Systems
{
    public partial class ModRaycastSystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct RaycastTerrainJob : IJob
        {
            [ReadOnly] public CustomRaycastInput input;
            [ReadOnly] public TerrainHeightData terrainData;
            [ReadOnly] public Entity terrainEntity;
            public NativeReference<RaycastResult> result;
            
            public void Execute() {

                Line3.Segment segment = input.line + input.offset;
                if ((input.typeMask & TypeMask.Terrain) != 0 && TerrainUtils.Raycast(ref terrainData, segment, false, out float t2, out float3 normal))
                {
                    float3 pos = MathUtils.Position(segment, t2);
                    if (input.heightOverride > 0f)
                    {
                        float3 pos2 = pos;
                        pos2.y = input.heightOverride;
                        if (TryIntersectLineWithPlane(segment, new Triangle3(pos2, pos2 + new float3(1, 0, 1), pos2 + math.right()), minDot: 0.05f, out float d) && d >= 0f && (double)d <= 1.0)
                        {
                            pos = MathUtils.Position(segment, d);
                        }
                    }
                    RaycastResult value = new()
                    {
                        m_Owner = terrainEntity,
                        m_Hit = new RaycastHit
                        {
                            m_HitEntity = terrainEntity,
                            m_Position = pos,
                            m_HitPosition = pos,
                            m_HitDirection = normal,
                            m_CellIndex = int2.zero,
                            m_NormalizedDistance = t2 + 1f / math.max(1f, MathUtils.Length(segment)),
                            m_CurvePosition = 0
                        }
                    };
                    result.Value = value;
                }
            }
        }
    }
}
