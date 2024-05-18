using System;
using System.Collections.Generic;
using Colossal;
using Traffic.CommonData;
using Traffic.Tools;

namespace Traffic
{
    public partial class Localization
    {
        public class LocaleEN : IDictionarySource
        {
            private readonly ModSettings _setting;
            private Dictionary<string, string> _translations;

            public LocaleEN(ModSettings setting)
            {
                _setting = setting;
                LocaleSources["en-US"] = new Tuple<string, string, IDictionarySource>("English", "100", this);
                _translations = Load();
            }

            public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
            {
                return _translations;
            }

            public Dictionary<string, string> Load(bool dumpTranslations = false)
            {
                return new Dictionary<string, string>
                {
                    { _setting.GetSettingsLocaleID(), "Traffic" },
                    { GetLanguageNameLocaleID(), "English"},
                    { _setting.GetOptionTabLocaleID(ModSettings.GeneralTab), "General" },
                    { _setting.GetOptionTabLocaleID(ModSettings.KeybindingsTab), "Key Bindings" },

                    { _setting.GetOptionGroupLocaleID(ModSettings.MainSection), "General" },
                    { _setting.GetOptionGroupLocaleID(ModSettings.LaneConnectorSection), "Lane Connector" },
                    {_setting.GetOptionGroupLocaleID(ModSettings.PrioritiesSection), "Priorities"},
                    { _setting.GetOptionGroupLocaleID(ModSettings.OverlaysSection), "Overlay Style" },
                    { _setting.GetOptionGroupLocaleID(ModSettings.AboutSection), "About" },
                    { _setting.GetOptionGroupLocaleID(ModSettings.ToolsSection), "Tools" },
                    {_setting.GetOptionGroupLocaleID(ModSettings.PriorityToolSection), "Priorities Tool Active"},
                    { _setting.GetOptionGroupLocaleID(ModSettings.SelectedNodeSection), "Selected Node or Intersection" },
                    { _setting.GetOptionGroupLocaleID(ModSettings.OtherSection), "Other" },

                    //Keybindings
                    { _setting.GetBindingMapLocaleID(), "Traffic Mod" },
                    { _setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.ApplyTool), "Apply Tool" },
                    { _setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.CancelTool), "Cancel Tool" },
                    { _setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.ToggleLaneConnectorTool), "Toggle Lane Connector Tool" },
                    { _setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.TogglePrioritiesTool), "Toggle Priorities Tool"},
                    { _setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.RemoveAllConnections), "Remove Intersection Lane Connections" },
                    { _setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.RemoveUTurns), "Remove U-Turns" },
                    { _setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.RemoveUnsafe), "Remove Unsafe Lane Connections" },
                    { _setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.ResetIntersectionToDefaults), "Reset selected intersection to defaults" },
                    {_setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.PrioritiesPriority), "Use Priority Action"},
                    {_setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.PrioritiesYield), "Use Yield Action"},
                    {_setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.PrioritiesStop), "Use Stop Action"},
                    {_setting.GetBindingKeyLocaleID(ModSettings.KeyBindAction.PrioritiesReset), "Use Reset Action"},

                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.UseVanillaToolActions)), "Use Vanilla Tool bindings" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.UseVanillaToolActions)), "When checked, the mod tool bindings will mimic vanilla key bindings" },
                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.ApplyToolAction)), "Apply Tool Action" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.ApplyToolAction)), "Keybinding used for applying the tool action, e.g.: click to select intersection (default: Left Mouse Button)" },
                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.CancelToolAction)), "Cancel Tool Action" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.CancelToolAction)), "Keybinding used for canceling the tool action, e.g.: click to reset intersection selection (default: Right Mouse Button)" },
                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.LaneConnectorToolAction)), "Toggle Lane Connector Tool" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.LaneConnectorToolAction)), "Keybinding used for toggling the Lane Connector tool" },
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.PrioritiesToolAction)), "Toggle Priorities Tool" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.PrioritiesToolAction)), "Keybinding used for toggling the Priorities tool" },
                    
                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.RemoveIntersectionConnections)), "Remove Intersection Lane Connections" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.RemoveIntersectionConnections)), "Keybinding is used when intersection is selected by Lane Connector Tool" },
                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.RemoveUTurnConnections)), "Remove U-Turn Lane Connections" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.RemoveUTurnConnections)), "Keybinding is used when intersection is selected by Lane Connector Tool" },
                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.RemoveUnsafeConnections)), "Remove Unsafe Lane Connections" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.RemoveUnsafeConnections)), "Keybinding is used when intersection is selected by Lane Connector Tool" },
                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.ResetIntersectionToDefaults)), "Reset selected intersection to defaults" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.ResetIntersectionToDefaults)), "Keybinding is used when intersection is selected by Lane Connector Tool" },
                    
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.PrioritiesToggleDisplayModeAction)), "Toggle Display Mode" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.PrioritiesToggleDisplayModeAction)), "Keybinding used for toggling between Priority tool display modes (Lane Group ⇆ Lane)" },
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.PrioritiesUsePriorityAction)), "Use Priority Action" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.PrioritiesUsePriorityAction)), "" },
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.PrioritiesUseYieldAction)), "Use Yield Action" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.PrioritiesUseYieldAction)), "" },
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.PrioritiesUseStopAction)), "Use Stop Action" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.PrioritiesUseStopAction)), "" },
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.PrioritiesUseResetAction)), "Use Reset Action" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.PrioritiesUseResetAction)), "" },
#if GAMEPAD_SUPPORT
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.LaneConnectorToolActionGamepad)), "Toggle Lane Connector Tool (Gamepad)" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.LaneConnectorToolActionGamepad)), "Keybinding used for toggling the Lane Connector tool using gamepad controller" },
                    {_setting.GetOptionLabelLocaleID(nameof(ModSettings.ResetIntersectionToDefaultsGamepad)), "Reset selected intersection to defaults (Gamepad)" },
                    {_setting.GetOptionDescLocaleID(nameof(ModSettings.ResetIntersectionToDefaultsGamepad)), "Keybinding is used when intersection is selected by Lane Connector Tool" },
#endif
                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.ResetBindings)), "Reset bindings" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.ResetBindings)), "Resets all bindings to the mod defaults" },

                    //Language
                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.CurrentLocale)), "Language" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.CurrentLocale)), "Sets the language used in the mod" },
                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.UseGameLanguage)), "Use Game Language" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.UseGameLanguage)), "Disabling the option will allow for selecting different language than currently set for the game interface (including not supported by the game).\nWhen enabled, the mod language will match selected game language" },
                    
                    //About
                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.ModVersion)), "Version" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.ModVersion)), $"Mod current version" },

                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.InformationalVersion)), "Informational Version" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.InformationalVersion)), $"Mod version with the commit ID" },

                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.OpenRepositoryAtVersion)), "Show on GitHub" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.OpenRepositoryAtVersion)), $"Opens the mod GitHub repository for the current version" },
                    
                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.TranslationCoverageStatus)), dumpTranslations ? "Translation status: " : "Source language" },

                    //Lane Connector
                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.ResetLaneConnections)), "Reset Lane Connections" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.ResetLaneConnections)), $"While in-game, it will remove all custom lane connections" },
                    { _setting.GetOptionWarningLocaleID(nameof(ModSettings.ResetLaneConnections)), "Are you sure you want to remove all custom lane connections?" },

                    // {_setting.GetOptionLabelLocaleID(nameof(ModSettings.ResetPriorities)), "Reset Priority settings" },
                    // {_setting.GetOptionDescLocaleID(nameof(ModSettings.ResetPriorities)), $"While in-game, it will remove all custom priority settings" },
                    // {_setting.GetOptionWarningLocaleID(nameof(ModSettings.ResetPriorities)), "Are you sure you want to remove all custom priority settings?" },

                    //Overlays
                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.FeedbackOutlineWidth)), "Feedback outline width" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.FeedbackOutlineWidth)), $"The width of the outline when feedback overlay is rendered, e.g.: when applying some changes may overwrite mod or vanilla settings" },

                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.ConnectorSize)), "Lane Connector size" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.ConnectorSize)), $"The size of the rendered lane connector overlay - the source or target lane circle" },

                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.ConnectionLaneWidth)), "Lane Connection width" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.ConnectionLaneWidth)), $"The width of the rendered lane connector overlay" },

                    { _setting.GetOptionLabelLocaleID(nameof(ModSettings.ResetStyle)), "Reset Style Settings" },
                    { _setting.GetOptionDescLocaleID(nameof(ModSettings.ResetStyle)), $"Resets style settings to the mod defaults" },

                    /* lane connector tool general tooltips */
                    { GetToolTooltipLocaleID("LaneConnector", nameof(LaneConnectorToolSystem.Tooltip.SelectIntersection)), "Select Intersection" },
                    { GetToolTooltipLocaleID("LaneConnector", nameof(LaneConnectorToolSystem.Tooltip.SelectConnectorToAddOrRemove)), "Select Connector to Add or Remove Connection" },
                    { GetToolTooltipLocaleID("LaneConnector", nameof(LaneConnectorToolSystem.Tooltip.RemoveSourceConnections)), "Remove Source Connections" },
                    { GetToolTooltipLocaleID("LaneConnector", nameof(LaneConnectorToolSystem.Tooltip.RemoveTargetConnections)), "Remove Target Connections" },
                    { GetToolTooltipLocaleID("LaneConnector", nameof(LaneConnectorToolSystem.Tooltip.CreateConnection)), "Create Connection" },
                    { GetToolTooltipLocaleID("LaneConnector", nameof(LaneConnectorToolSystem.Tooltip.ModifyConnections)), "Modify Connections" },
                    { GetToolTooltipLocaleID("LaneConnector", nameof(LaneConnectorToolSystem.Tooltip.RemoveConnection)), "Remove Connection" },
                    { GetToolTooltipLocaleID("LaneConnector", nameof(LaneConnectorToolSystem.Tooltip.CompleteConnection)), "Complete Connection" },
                    { GetToolTooltipLocaleID("LaneConnector", nameof(LaneConnectorToolSystem.Tooltip.UTurnTrackNotAllowed)), "U-Turn track connections are not allowed!" },
                    /* lane connector tool state */
                    { GetToolTooltipLocaleID("LaneConnector", $"{nameof(LaneConnectorToolSystem.StateModifier.Road)}, {nameof(LaneConnectorToolSystem.StateModifier.FullMatch)}"), "Road-only Lane Connectors" },
                    { GetToolTooltipLocaleID("LaneConnector", $"{nameof(LaneConnectorToolSystem.StateModifier.Track)}, {nameof(LaneConnectorToolSystem.StateModifier.FullMatch)}"), "Track-only Lane Connectors" },
                    { GetToolTooltipLocaleID("LaneConnector", $"{nameof(LaneConnectorToolSystem.StateModifier.AnyConnector)}, {nameof(LaneConnectorToolSystem.StateModifier.FullMatch)}"), "Mixed Lane Type Connectors" },
                    { GetToolTooltipLocaleID("LaneConnector", $"{nameof(LaneConnectorToolSystem.StateModifier.AnyConnector)}, {nameof(LaneConnectorToolSystem.StateModifier.MakeUnsafe)}"), "Make Unsafe" },
                    {
                        GetToolTooltipLocaleID("LaneConnector", $"{nameof(LaneConnectorToolSystem.StateModifier.Road)}, {nameof(LaneConnectorToolSystem.StateModifier.FullMatch)}, {nameof(LaneConnectorToolSystem.StateModifier.MakeUnsafe)}"),
                        "Unsafe Road Lane Connection"
                    },
                    /* general feedback */
                    { GetToolTooltipLocaleID("FeedbackMessage", nameof(FeedbackMessageType.WarnResetForbiddenTurnUpgrades)), "[Traffic] Entering Lane Connection modification mode will remove Forbidden maneuvers from connected roads" },
                    { GetToolTooltipLocaleID("FeedbackMessage", nameof(FeedbackMessageType.WarnForbiddenTurnApply)), "[Traffic] Applying Forbidden maneuver upgrade will reset Lane connections at a nearby intersection" },
                    { GetToolTooltipLocaleID("FeedbackMessage", nameof(FeedbackMessageType.WarnResetPrioritiesTrafficLightsApply)), "[Traffic] Applying Traffic Lights upgrade will reset Priority settings at the selected intersection"},

                    { GetToolTooltipLocaleID("FeedbackMessage", nameof(FeedbackMessageType.ErrorHasRoundabout)), "[Traffic] Modifying lane connections at a selected intersection is not supported" },
                    { GetToolTooltipLocaleID("FeedbackMessage", nameof(FeedbackMessageType.ErrorApplyRoundabout)), "[Traffic] Roundabout upgrade cannot be used at an intersection with non-standard lane connections" },
                    { GetToolTooltipLocaleID("FeedbackMessage", nameof(FeedbackMessageType.ErrorPrioritiesNotSupported)), "[Traffic] Modifying priority settings at a selected intersection is not supported"},
                    { GetToolTooltipLocaleID("FeedbackMessage", nameof(FeedbackMessageType.ErrorPrioritiesRemoveTrafficLights)), "[Traffic] Remove traffic lights to modify priority settings at a selected intersection"},

                    /*In-game UI*/
                    {
                        UIKeys.TRAFFIC_MOD,
                        "Modification provides additional tools that could help managing in-game traffic. At the moment the Lane Connector tool, more to come soon. If you have suggestions for new tools or improvements, feel free to comment on the mod forum thread or open a feature suggestion on the mod GitHub page"
                    },
                    { UIKeys.SHORTCUT, "Shortcut: " },
                    { UIKeys.LANE_CONNECTOR_TOOL, "Lane Connector Tool" },
                    { UIKeys.SELECT_INTERSECTION, "Select intersection to begin editing" },
                    { UIKeys.REMOVE_ALL_CONNECTIONS, "Remove All Connections" },
                    { UIKeys.REMOVE_U_TURNS, "Remove U-Turns" },
                    { UIKeys.REMOVE_UNSAFE, "Remove Unsafe" },
                    { UIKeys.RESET_TO_VANILLA, "Reset To Vanilla" },
                    { UIKeys.REMOVE_ALL_CONNECTIONS_TOOLTIP_TITLE, "Remove Intersection Connections" },
                    { UIKeys.REMOVE_ALL_CONNECTIONS_TOOLTIP_MESSAGE, "Removes lane connections that can be configured by the Lane Connector tool. It doesn't touch two-way lane connections, since they are not supported yet." },
                    { UIKeys.REMOVE_U_TURNS_TOOLTIP_TITLE, "Remove U-Turns" },
                    { UIKeys.REMOVE_U_TURNS_TOOLTIP_MESSAGE, "Removes U-Turns from the selected intersection, applies only to U-turns on the same segment" },
                    { UIKeys.REMOVE_UNSAFE_TOOLTIP_TITLE, "Unsafe lane" },
                    {
                        UIKeys.REMOVE_UNSAFE_TOOLTIP_MESSAGE,
                        "Removes Unsafe lane connections from the selected intersection.\nUnsafe lane is a lane with a higher pathfinding penalty, meaning that lane connection has a lower selection priority when other options are available"
                    },
                    { UIKeys.RESET_TO_VANILLA_TOOLTIP_TITLE, "Reset to Vanilla" },
                    { UIKeys.RESET_TO_VANILLA_TOOLTIP_MESSAGE, "Resets lane connections on the selected intersection to vanilla configuration" },
                    { UIKeys.PRIORITY_ACTION, "Priority"},
                    { UIKeys.YIELD_ACTION, "Yield"},
                    { UIKeys.STOP_ACTION, "Stop"},
                    { UIKeys.RESET_ACTION, "Reset to default"},  
                    { UIKeys.PRIORITY_ACTION_TOOLTIP, "Highest priority, but may still give way to trams"},
                    { UIKeys.YIELD_ACTION_TOOLTIP, "Give way to other traffic coming from roads with higher priority"},
                    { UIKeys.STOP_ACTION_TOOLTIP, "Stop at stop line and give way other traffic coming from roads with higher priority"},
                    { UIKeys.RESET_ACTION_TOOLTIP, "Resets selected lane or lane group to vanilla default settings"},
                    { UIKeys.TOGGLE_DISPLAY_MODE_TOOLTIP, "Toggle between display modes"},
#if GAMEPAD_SUPPORT
                    /* Gamepad Hints*/
                    { _setting.GetBindingKeyHintLocaleID(ModSettings.KeyBindAction.ToggleLaneConnectorTool), "Traffic's Lane Connector" }
#endif
                };
            }

            public void Unload() { }
            
            public override string ToString()
            {
                return "Traffic.Locale.en-US";
            }
        }
    }
}
