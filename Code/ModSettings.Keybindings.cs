using Game.Input;
using Game.Settings;
using UnityEngine.InputSystem;

namespace Traffic
{
    
    /*TODO gamepad support*/
    // [SettingsUIGamepadAction(KeyBindAction.ToggleLaneConnectorTool, Usages.kDefaultUsage, Usages.kEditorUsage, Usages.kToolUsage)]
    // [SettingsUIGamepadAction(KeyBindAction.ResetIntersectionToDefaults, Usages.kDefaultUsage, Usages.kEditorUsage, Usages.kToolUsage)]
    [SettingsUIKeyboardAction(KeyBindAction.ToggleLaneConnectorTool, Usages.kDefaultUsage, Usages.kEditorUsage, Usages.kToolUsage)]
    [SettingsUIKeyboardAction(KeyBindAction.ResetIntersectionToDefaults, Usages.kMenuUsage, Usages.kToolUsage)]
    public partial class ModSettings
    {
        internal const string ToolsSection = "Tools";
        internal const string SelectedNodeSection = "KeybindsSelectedNode";
        
        [SettingsUISection(KeybindingsTab, ToolsSection)]
        [SettingsUIKeyboardBinding(Key.R, KeyBindAction.ToggleLaneConnectorTool, ctrl: true)]
        public ProxyBinding LaneConnectorToolAction { get; set; }
        
        // [SettingsUISection(KeybindingsTab, ToolsSection)]
        // [SettingsUIGamepadBinding(GamepadButton.LeftShoulder, KeyBindAction.ToggleLaneConnectorTool, true)]
        // public ProxyBinding LaneConnectorToolActionGamepad { get; set; }
        
        [SettingsUISection(KeybindingsTab, SelectedNodeSection)]
        [SettingsUIKeyboardBinding(Key.Delete, KeyBindAction.ResetIntersectionToDefaults)]
        public ProxyBinding ResetIntersectionToDefaults { get; set; }
        
        // [SettingsUISection(KeybindingsTab, SelectedNodeSection)]
        // [SettingsUIGamepadBinding(GamepadButton.B, KeyBindAction.ResetIntersectionToDefaults, true)]
        // public ProxyBinding ResetIntersectionToDefaultsGamepad { get; set; }
        
        internal static class KeyBindAction
        {
            internal const string ToggleLaneConnectorTool = "ToggleLaneConnectorTool";
            internal const string ResetIntersectionToDefaults = "ResetIntersectionToDefaults";
        }
    }
}
