using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using Edge = Game.Net.Edge;

namespace Traffic.Systems.Helpers
{
    public static class NetUtils
    {
        /// <summary>
        /// Finds additional details based on Lane component data
        /// </summary>
        /// <param name="sourceEdge"></param>
        /// <param name="targetEdge"></param>
        /// <param name="laneIndexMap"></param>
        /// <param name="isEdgeEndMap"></param>
        /// <param name="compositionLanes">component lookup</param>
        /// <param name="compositionData">buffer lookup</param>
        /// <param name="lanePositionMap">(sourceLanePosition, targetLanePosition)</param>
        /// <param name="carriagewayWithGroupMap">(sourceCarriageway, sourceGroupIndex,targetCarriageway, targetGroupIndex)</param>
        /// <returns>true if both lane ends are valid</returns>
        public static bool GetAdditionalLaneDetails(Entity sourceEdge, Entity targetEdge, int2 laneIndexMap, bool2 isEdgeEndMap, ref ComponentLookup<Composition> compositionData, ref BufferLookup<NetCompositionLane> compositionLanes, out float3x2 lanePositionMap,
            out int4 carriagewayWithGroupMap)
        {
            bool2 result = false;
            lanePositionMap = float3x2.zero;
            carriagewayWithGroupMap = int4.zero;
            if (sourceEdge != Entity.Null)
            {
                Composition startComposition = compositionData[sourceEdge];
                DynamicBuffer<NetCompositionLane> sourceNetCompositionLanes = compositionLanes[startComposition.m_Edge];
                int sourceLaneIndex = laneIndexMap.x;
                if (sourceLaneIndex >= sourceNetCompositionLanes.Length)
                {
                    return false;
                }

                NetCompositionLane sourceNetCompositionLane = sourceNetCompositionLanes[sourceLaneIndex];
                float3 position = sourceNetCompositionLane.m_Position;
                position.x = math.select(0f- sourceNetCompositionLane.m_Position.x, sourceNetCompositionLane.m_Position.x, isEdgeEndMap.x);
                lanePositionMap.c0 = position;
                carriagewayWithGroupMap.x = sourceNetCompositionLane.m_Carriageway;
                carriagewayWithGroupMap.y = sourceNetCompositionLane.m_Group;
                result.x = true;

                //reuse the same edge
                if (sourceEdge.Equals(targetEdge))
                {
                    int targetLaneIndex = laneIndexMap.y;
                    if (targetLaneIndex >= sourceNetCompositionLanes.Length)
                    {
                        return false;
                    }

                    NetCompositionLane targetNetCompositionLane = sourceNetCompositionLanes[targetLaneIndex];
                    float3 targetPosition = targetNetCompositionLane.m_Position;
                    targetPosition.x = math.select(0f - targetNetCompositionLane.m_Position.x, targetNetCompositionLane.m_Position.x, isEdgeEndMap.y);
                    lanePositionMap.c1 = targetPosition;
                    carriagewayWithGroupMap.z = targetNetCompositionLane.m_Carriageway;
                    carriagewayWithGroupMap.w = targetNetCompositionLane.m_Group;
                    result.y = true;
                }
                else
                {
                    if (targetEdge != Entity.Null)
                    {
                        Composition targetComposition = compositionData[targetEdge];
                        DynamicBuffer<NetCompositionLane> targetNetCompositionLanes = compositionLanes[targetComposition.m_Edge];
                        int targetLaneIndex = laneIndexMap.y;
                        if (targetLaneIndex >= targetNetCompositionLanes.Length)
                        {
                            return false;
                        }

                        NetCompositionLane targetNetCompositionLane = targetNetCompositionLanes[targetLaneIndex];
                        float3 targetPosition = targetNetCompositionLane.m_Position;
                        targetPosition.x = math.select(0f - targetNetCompositionLane.m_Position.x, targetNetCompositionLane.m_Position.x, isEdgeEndMap.y);
                        lanePositionMap.c1 = targetPosition;
                        carriagewayWithGroupMap.z = targetNetCompositionLane.m_Carriageway;
                        carriagewayWithGroupMap.w = targetNetCompositionLane.m_Group;
                        result.y = true;
                    }
                }
            }

            return math.all(result);
        }

        /// <summary>
        /// Finds the edge based on PathNode Owner
        /// </summary>
        /// <param name="edges">ConnectedEdge buffer</param>
        /// <param name="node">node to test againts</param>
        /// <returns>Edge Entity if found or Entity.Null</returns>
        public static Entity FindEdge(DynamicBuffer<ConnectedEdge> edges, PathNode node)
        {
            foreach (ConnectedEdge connectedEdge in edges)
            {
                if (node.OwnerEquals(new PathNode(connectedEdge.m_Edge, 0)))
                {
                    return connectedEdge.m_Edge;
                }
            }
            return Entity.Null;
        }
    }
}
