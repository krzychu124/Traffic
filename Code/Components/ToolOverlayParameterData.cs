using Unity.Entities;

namespace Traffic.Components
{
    public struct ToolOverlayParameterData : IComponentData
    {
        /// <summary>
        /// acceptable range (0.5f;2f)
        /// </summary>
        public float laneConnectorSize;
        public float laneConnectorLineWidth;
        public float feedbackLinesWidth;
    }
}
