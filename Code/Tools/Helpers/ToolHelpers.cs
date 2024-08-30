using Colossal.Mathematics;
using Game.Net;
using Game.Tools;

namespace Traffic.Tools.Helpers
{
    internal static class ToolHelpers
    {
        
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
