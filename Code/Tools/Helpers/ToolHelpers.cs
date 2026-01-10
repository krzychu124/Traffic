using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;

namespace Traffic.Tools.Helpers
{
    internal static class ToolHelpers
    {
        /// <summary>
        /// Test if there's any NetCompositionLane with matching flags
        /// </summary>
        /// <param name="lanes"></param>
        /// <param name="flags">Flags value to match</param>
        /// <returns></returns>
        internal static bool HasCompositionLaneWithFlag(ref DynamicBuffer<NetCompositionLane> lanes, LaneFlags flags)
        {
            foreach (NetCompositionLane lane in lanes)
            {
                if ((lane.m_Flags & flags) != 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Simplified CoursePos calculation, 
        /// </summary>
        /// <param name="curve"></param>
        /// <param name="controlPoint"></param>
        /// <param name="delta"></param>
        /// <returns></returns>
        internal static CoursePos GetCoursePos(Bezier4x3 curve, ControlPoint controlPoint, float delta)
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
}
