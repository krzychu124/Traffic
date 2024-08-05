namespace Traffic
{
    public static class DataMigrationVersion
    {
        // [mod pre-v.1.7.3] incomplete composition information lead to corrupted data after update
        public static readonly int LaneConnectionDataUpgradeV1 = 2;
        // [vanilla 1.1.6f1] mod data corruption (vanilla change in Temp entity flow)
        public static readonly int LaneConnectionDataUpgradeV2 = 3;
        // [vanilla 1.1.7f1] Priority management introduction
        public static readonly int PriorityManagementDataV1 = 4;
    }
}
