using Unity.Entities;

namespace Traffic.Components
{
    public struct ToolOverlayParameterData : IComponentData
    {
        public float laneConnectorSize;
        public float laneConnectorLineWidth;
        public float feedbackLinesWidth;
    }
}
