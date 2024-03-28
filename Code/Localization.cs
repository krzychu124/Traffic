using System.Collections.Generic;
using Colossal;
using Game.Modding;
using Traffic.Tools;

namespace Traffic
{
    public class Localization
    {

        

        public class LocaleEN : IDictionarySource
        {
            private readonly ModSetting _setting;
            public LocaleEN(ModSetting setting)
            {
                _setting = setting;
            }

            public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
            {
                return new Dictionary<string, string>
                {
                    {_setting.GetSettingsLocaleID(), "Traffic" },
                    {_setting.GetOptionLabelLocaleID(ModSettings.MaintenanceSection), "Maintenance"},
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.ResetLaneConnections)), "Reset Lane Connections" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.ResetLaneConnections)), $"While in-game, it will remove all custom lane connections" },
                    {_setting.GetOptionWarningLocaleID(nameof(ModSettings.ResetLaneConnections)), "Are you sure you want to remove all custom lane connections?" },
                    
                    {$"{Mod.MOD_NAME}.Tools.Tooltip[{nameof(LaneConnectorToolSystem.Tooltip.SelectIntersection)}]", "Select Intersection"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip[{nameof(LaneConnectorToolSystem.Tooltip.SelectConnectorToAddOrRemove)}]", "Select Connector to Add or Remove Connection"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip[{nameof(LaneConnectorToolSystem.Tooltip.RemoveSourceConnections)}]", "Remove Source Connections"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip[{nameof(LaneConnectorToolSystem.Tooltip.RemoveTargetConnections)}]", "Remove Target Connections"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip[{nameof(LaneConnectorToolSystem.Tooltip.CreateConnection)}]", "Create Connection"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip[{nameof(LaneConnectorToolSystem.Tooltip.ModifyConnections)}]", "Modify Connections"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip[{nameof(LaneConnectorToolSystem.Tooltip.RemoveConnection)}]", "Remove Connection"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip[{nameof(LaneConnectorToolSystem.Tooltip.CompleteConnection)}]", "Complete Connection"},
                    
                    {$"{Mod.MOD_NAME}.Tools.Tooltip[{nameof(LaneConnectorToolSystem.StateModifier.Road)}, {nameof(LaneConnectorToolSystem.StateModifier.FullMatch)}]", "Road-only Lane Connectors"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip[{nameof(LaneConnectorToolSystem.StateModifier.Track)}, {nameof(LaneConnectorToolSystem.StateModifier.FullMatch)}]", "Track-only Lane Connectors"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip[{nameof(LaneConnectorToolSystem.StateModifier.AnyConnector)}, {nameof(LaneConnectorToolSystem.StateModifier.FullMatch)}]", "Mixed Lane Type Connectors"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip[{nameof(LaneConnectorToolSystem.StateModifier.AnyConnector)}, {nameof(LaneConnectorToolSystem.StateModifier.MakeUnsafe)}]", "Make Unsafe"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip[{nameof(LaneConnectorToolSystem.StateModifier.Road)}, {nameof(LaneConnectorToolSystem.StateModifier.FullMatch)}, {nameof(LaneConnectorToolSystem.StateModifier.MakeUnsafe)}]", "Unsafe Road Lane Connection"},
                };
            }
            
            public void Unload()
            {

            }
        }
    }
}
