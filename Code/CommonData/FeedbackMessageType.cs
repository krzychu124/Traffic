namespace Traffic.CommonData
{
    public enum FeedbackMessageType
    {
        WarnResetForbiddenTurnUpgrades,
        WarnForbiddenTurnApply,
        WarnResetPrioritiesTrafficLightsApply,
        ErrorLaneConnectorNotSupported,
        ErrorPrioritiesNotSupported,
        ErrorPrioritiesRemoveTrafficLights,
        ErrorApplyRoundabout,
        ErrorHasRoundabout,
    }
}
