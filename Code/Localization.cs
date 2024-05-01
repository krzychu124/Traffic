﻿using System.Collections.Generic;
using Colossal;
using Game.Modding;
using Traffic.CommonData;
using Traffic.Tools;

namespace Traffic
{
    public class Localization
    {

        public class UIKeys
        {
            public const string TRAFFIC_MOD = Mod.MOD_NAME+".UI.Tooltip.MainButton";
            public const string LANE_CONNECTION_TOOL = Mod.MOD_NAME+".Tools.LaneConnector[Title]";
            public const string SELECT_INTERSECTION = Mod.MOD_NAME+".Tools.LaneConnector[SelectIntersectionMessage]";
            /*lane connector toolbox*/
            public const string REMOVE_ALL_CONNECTIONS = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[RemoveAllConnections]";
            public const string REMOVE_U_TURNS = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[RemoveUturns]";
            public const string REMOVE_UNSAFE = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[RemoveUnsafe]";
            public const string RESET_TO_VANILLA = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[ResetToVanilla]";
            /*tooltips*/
            public const string REMOVE_UNSAFE_TOOLTIP_TITLE = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[ResetToVanilla].Tooltip.Title";
            public const string REMOVE_UNSAFE_TOOLTIP_MESSAGE = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[ResetToVanilla].Tooltip.Message";
        }
        

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
                    
                    /* lane connector tool general tooltips */
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.LaneConnector[{nameof(LaneConnectorToolSystem.Tooltip.SelectIntersection)}]", "Select Intersection"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.LaneConnector[{nameof(LaneConnectorToolSystem.Tooltip.SelectConnectorToAddOrRemove)}]", "Select Connector to Add or Remove Connection"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.LaneConnector[{nameof(LaneConnectorToolSystem.Tooltip.RemoveSourceConnections)}]", "Remove Source Connections"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.LaneConnector[{nameof(LaneConnectorToolSystem.Tooltip.RemoveTargetConnections)}]", "Remove Target Connections"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.LaneConnector[{nameof(LaneConnectorToolSystem.Tooltip.CreateConnection)}]", "Create Connection"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.LaneConnector[{nameof(LaneConnectorToolSystem.Tooltip.ModifyConnections)}]", "Modify Connections"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.LaneConnector[{nameof(LaneConnectorToolSystem.Tooltip.RemoveConnection)}]", "Remove Connection"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.LaneConnector[{nameof(LaneConnectorToolSystem.Tooltip.CompleteConnection)}]", "Complete Connection"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.LaneConnector[{nameof(LaneConnectorToolSystem.Tooltip.UTurnTrackNotAllowed)}]", "U-Turn track connections are not allowed!"},
                    /* lane connector tool state */
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.LaneConnector[{nameof(LaneConnectorToolSystem.StateModifier.Road)}, {nameof(LaneConnectorToolSystem.StateModifier.FullMatch)}]", "Road-only Lane Connectors"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.LaneConnector[{nameof(LaneConnectorToolSystem.StateModifier.Track)}, {nameof(LaneConnectorToolSystem.StateModifier.FullMatch)}]", "Track-only Lane Connectors"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.LaneConnector[{nameof(LaneConnectorToolSystem.StateModifier.AnyConnector)}, {nameof(LaneConnectorToolSystem.StateModifier.FullMatch)}]", "Mixed Lane Type Connectors"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.LaneConnector[{nameof(LaneConnectorToolSystem.StateModifier.AnyConnector)}, {nameof(LaneConnectorToolSystem.StateModifier.MakeUnsafe)}]", "Make Unsafe"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.LaneConnector[{nameof(LaneConnectorToolSystem.StateModifier.Road)}, {nameof(LaneConnectorToolSystem.StateModifier.FullMatch)}, {nameof(LaneConnectorToolSystem.StateModifier.MakeUnsafe)}]", "Unsafe Road Lane Connection"},
                    /* general feedback */
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.FeedbackMessage[{nameof(FeedbackMessageType.WarnResetForbiddenTurnUpgrades)}]", "Entering Lane Connection modification mode will remove Forbidden maneuvers from connected roads"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.FeedbackMessage[{nameof(FeedbackMessageType.WarnForbiddenTurnApply)}]", "Applying Forbidden maneuver upgrade will reset Lane connections at a nearby intersection"},
                    
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.FeedbackMessage[{nameof(FeedbackMessageType.ErrorHasRoundabout)}]", "Modifying lane connections at a selected intersection is not supported"},
                    {$"{Mod.MOD_NAME}.Tools.Tooltip.FeedbackMessage[{nameof(FeedbackMessageType.ErrorApplyRoundabout)}]", "Roundabout upgrade cannot be used at an intersection with non-standard lane connections"},
                    
                    /*In-game UI*/
                    {UIKeys.TRAFFIC_MOD, "Modification provides additional tools that could help managing in-game traffic. At the moment the Lane Connector tool, more to come soon. If you have suggestions for new tools or improvements, feel free to comment on the mod forum thread or open a feature suggestion on the mod GitHub page"},
                    {UIKeys.LANE_CONNECTION_TOOL, "Lane Connection Tool"},
                    {UIKeys.SELECT_INTERSECTION, "Select intersection to begin editing"},
                    {UIKeys.REMOVE_ALL_CONNECTIONS, "Remove All Connections"},
                    {UIKeys.REMOVE_U_TURNS, "Remove U-Turns"},
                    {UIKeys.REMOVE_UNSAFE, "Remove Unsafe"},
                    {UIKeys.RESET_TO_VANILLA, "Reset To Vanilla"},
                    {UIKeys.REMOVE_UNSAFE_TOOLTIP_TITLE, "Unsafe lane"},
                    {UIKeys.REMOVE_UNSAFE_TOOLTIP_MESSAGE, "Unsafe lane is a lane with a higher pathfinding penalty, meaning that lane connection has a lower selection priority when other options are available"},
                };
            }
            
            public void Unload()
            {

            }
        }
    }
}
