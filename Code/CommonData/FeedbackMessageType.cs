namespace Traffic.CommonData
{
    public enum FeedbackMessageType
    {
        WarnResetForbiddenTurnUpgrades,
        WarnForbiddenTurnApply,
        WarnResetPrioritiesTrafficLightsApply,
        WarnResetPrioritiesRoundaboutApply,
        WarnResetPrioritiesChangeApply,
        WarnRoadBuilderApply,
        ErrorLaneConnectorNotSupported,
        ErrorPrioritiesNotSupported,
        ErrorPrioritiesRemoveTrafficLights,
        ErrorApplyRoundabout,
        ErrorHasRoundabout,
    }
}
