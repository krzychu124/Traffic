using System.Collections.Generic;
using Colossal;
using Traffic.CommonData;
using Traffic.Tools;

namespace Traffic
{
    public class Localization
    {
        /*TODO Gamepad support*/
        public class UIKeys
        {
            public const string TRAFFIC_MOD = Mod.MOD_NAME+".UI.Tooltip.MainButton";
            public const string SHORTCUT = Mod.MOD_NAME+".UI.Tooltip.Shortcut";
            public const string LANE_CONNECTION_TOOL = Mod.MOD_NAME+".Tools.LaneConnector[Title]";
            public const string SELECT_INTERSECTION = Mod.MOD_NAME+".Tools.LaneConnector[SelectIntersectionMessage]";
            /*lane connector toolbox*/
            public const string REMOVE_ALL_CONNECTIONS = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[RemoveAllConnections]";
            public const string REMOVE_U_TURNS = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[RemoveUturns]";
            public const string REMOVE_UNSAFE = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[RemoveUnsafe]";
            public const string RESET_TO_VANILLA = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[ResetToVanilla]";
            /*tooltips*/
            public const string REMOVE_ALL_CONNECTIONS_TOOLTIP_TITLE = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[RemoveAllConnections].Tooltip.Title";
            public const string REMOVE_ALL_CONNECTIONS_TOOLTIP_MESSAGE = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[RemoveAllConnections].Tooltip.Message";
            public const string REMOVE_U_TURNS_TOOLTIP_TITLE = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[RemoveUTurns].Tooltip.Title";
            public const string REMOVE_U_TURNS_TOOLTIP_MESSAGE = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[RemoveUTurns].Tooltip.Message";
            public const string REMOVE_UNSAFE_TOOLTIP_TITLE = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[RemoveUnsafe].Tooltip.Title";
            public const string REMOVE_UNSAFE_TOOLTIP_MESSAGE = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[RemoveUnsafe].Tooltip.Message";
            public const string RESET_TO_VANILLA_TOOLTIP_TITLE = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[ResetToVanilla].Tooltip.Title";
            public const string RESET_TO_VANILLA_TOOLTIP_MESSAGE = Mod.MOD_NAME+".Tools.LaneConnector.Toolbox[ResetToVanilla].Tooltip.Message";
        }
        

        public class LocaleEN : IDictionarySource
        {
            private readonly ModSettings _setting;
            public LocaleEN(ModSettings setting)
            {
                _setting = setting;
            }

            public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
            {
                return new Dictionary<string, string>
                {
                    {_setting.GetSettingsLocaleID(), "Traffic" },
                    
                    {_setting.GetOptionGroupLocaleID(ModSettings.MainSection), "General"},
                    {_setting.GetOptionGroupLocaleID(ModSettings.LaneConnectorSection), "Lane Connector"},
                    {_setting.GetOptionGroupLocaleID(ModSettings.OverlaysSection), "Overlay Style"},
                    {_setting.GetOptionGroupLocaleID(ModSettings.AboutSection), "About"},
                    {_setting.GetOptionGroupLocaleID(ModSettings.ToolsSection), "Tools"},
                    {_setting.GetOptionGroupLocaleID(ModSettings.SelectedNodeSection), "Selected Node or Intersection"},
                    {_setting.GetOptionGroupLocaleID(ModSettings.OtherSection), "Other"},
                    {_setting.GetOptionTabLocaleID(ModSettings.GeneralTab), "General"},
                    {_setting.GetOptionTabLocaleID(ModSettings.KeybindingsTab), "Key Bindings"},
                     
                    //Keybindings
                    {_setting.GetBindingMapLocaleID(), "Traffic Mod"},
                    {_setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.ApplyTool), "Apply Tool"},
                    {_setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.CancelTool), "Cancel Tool"},
                    {_setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.ToggleLaneConnectorTool), "Toggle Lane Connector Tool"},
                    {_setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.RemoveAllConnections), "Remove Intersection Lane Connections"},
                    {_setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.RemoveUTurns), "Remove U-Turns"},
                    {_setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.RemoveUnsafe), "Remove Unsafe Lane Connections"},
                    {_setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.ResetIntersectionToDefaults), "Reset selected intersection to defaults"},

                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.UseVanillaToolActions)), "Use Vanilla Tool bindings" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.UseVanillaToolActions)), "When checked, the mod tool bindings will mimic vanilla key bindings" },
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.ApplyToolAction)), "Apply Tool Action" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.ApplyToolAction)), "Keybinding used for applying the tool action, e.g.: click to select intersection (default: Left Mouse Button)" },
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.CancelToolAction)), "Cancel Tool Action" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.CancelToolAction)), "Keybinding used for canceling the tool action, e.g.: click to reset intersection selection (default: Right Mouse Button)" },
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.LaneConnectorToolAction)), "Toggle Lane Connector Tool" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.LaneConnectorToolAction)), "Keybinding used for toggling the Lane Connector tool" },
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.RemoveIntersectionConnections)), "Remove Intersection Lane Connections" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.RemoveIntersectionConnections)), "Keybinding is used when intersection is selected by Lane Connector Tool" },
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.RemoveUTurnConnections)), "Remove U-Turn Lane Connections" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.RemoveUTurnConnections)), "Keybinding is used when intersection is selected by Lane Connector Tool" },
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.RemoveUnsafeConnections)), "Remove Unsafe Lane Connections" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.RemoveUnsafeConnections)), "Keybinding is used when intersection is selected by Lane Connector Tool" },
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.ResetIntersectionToDefaults)), "Reset selected intersection to defaults" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.ResetIntersectionToDefaults)), "Keybinding is used when intersection is selected by Lane Connector Tool" },
#if GAMEPAD_SUPPORT
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.LaneConnectorToolActionGamepad)), "Toggle Lane Connector Tool (Gamepad)" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.LaneConnectorToolActionGamepad)), "Keybinding used for toggling the Lane Connector tool using gamepad controller" },
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.ResetIntersectionToDefaultsGamepad)), "Reset selected intersection to defaults (Gamepad)" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.ResetIntersectionToDefaultsGamepad)), "Keybinding is used when intersection is selected by Lane Connector Tool" },
#endif
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.ResetBindings)), "Reset bindings" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.ResetBindings)), "Resets all bindings to the mod defaults" },

                    //About
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.ModVersion)), "Version" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.ModVersion)), $"Mod current version" },
                    
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.InformationalVersion)), "Informational Version" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.InformationalVersion)), $"Mod version with the commit ID" },
                    
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.OpenRepositoryAtVersion)), "Show on GitHub" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.OpenRepositoryAtVersion)), $"Opens the mod GitHub repository for the current version" },
                    
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.ResetLaneConnections)), "Reset Lane Connections" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.ResetLaneConnections)), $"While in-game, it will remove all custom lane connections" },
                    {_setting.GetOptionWarningLocaleID(nameof(ModSettings.ResetLaneConnections)), "Are you sure you want to remove all custom lane connections?" },
                    
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.FeedbackOutlineWidth)), "Feedback outline width" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.FeedbackOutlineWidth)), $"The width of the outline when feedback overlay is rendered, e.g.: when applying some changes may overwrite mod or vanilla settings" },
                    
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.ConnectorSize)), "Lane Connector size" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.ConnectorSize)), $"The size of the rendered lane connector overlay - the source or target lane circle" },
                    
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.ConnectionLaneWidth)), "Lane Connection width" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.ConnectionLaneWidth)), $"The width of the rendered lane connector overlay" },
                    
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.ResetStyle)), "Reset Style Settings" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.ResetStyle)), $"Resets style settings to the mod defaults" },
                    
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
                    {UIKeys.SHORTCUT, "Shortcut: "},
                    {UIKeys.LANE_CONNECTION_TOOL, "Lane Connection Tool"},
                    {UIKeys.SELECT_INTERSECTION, "Select intersection to begin editing"},
                    {UIKeys.REMOVE_ALL_CONNECTIONS, "Remove All Connections"},
                    {UIKeys.REMOVE_U_TURNS, "Remove U-Turns"},
                    {UIKeys.REMOVE_UNSAFE, "Remove Unsafe"},
                    {UIKeys.RESET_TO_VANILLA, "Reset To Vanilla"},
                    {UIKeys.REMOVE_ALL_CONNECTIONS_TOOLTIP_TITLE, "Remove Intersection Connections"},
                    {UIKeys.REMOVE_ALL_CONNECTIONS_TOOLTIP_MESSAGE, "Removes lane connections that can be configured by the Lane Connector tool. It doesn't touch two-way lane connections, since they are not supported yet."},
                    {UIKeys.REMOVE_U_TURNS_TOOLTIP_TITLE, "Remove U-Turns"},
                    {UIKeys.REMOVE_U_TURNS_TOOLTIP_MESSAGE, "Removes U-Turns from the selected intersection, applies only to U-turns on the same segment"},
                    {UIKeys.REMOVE_UNSAFE_TOOLTIP_TITLE, "Unsafe lane"},
                    {UIKeys.REMOVE_UNSAFE_TOOLTIP_MESSAGE, "Removes Unsafe lane connections from the selected intersection.\nUnsafe lane is a lane with a higher pathfinding penalty, meaning that lane connection has a lower selection priority when other options are available"},
                    {UIKeys.RESET_TO_VANILLA_TOOLTIP_TITLE, "Reset to Vanilla"},
                    {UIKeys.RESET_TO_VANILLA_TOOLTIP_MESSAGE, "Resets lane connections on the selected intersection to vanilla configuration"},
                    
#if !GAMEPAD_SUPPORT
                    /* Gamepad Hints*/
                    {_setting.GetBindingKeyHintLocaleID(ModSettings.KeyBindAction.ToggleLaneConnectorTool), "Traffic's Lane Connector"}
#endif
                };
            }
            
            public void Unload()
            {

            }
        }
    }
}
