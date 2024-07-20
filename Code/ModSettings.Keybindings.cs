using Game.Input;
using Game.Settings;

namespace Traffic
{
    
    [SettingsUIMouseAction(KeyBindAction.ApplyTool, ActionType.Button, false, usages:  new []{"Traffic.Tool"})]
    [SettingsUIMouseAction(KeyBindAction.CancelTool, ActionType.Button, false, usages:  new []{"Traffic.Tool"})]
    [SettingsUIKeyboardAction(KeyBindAction.ToggleLaneConnectorTool, Usages.kDefaultUsage, Usages.kEditorUsage, Usages.kToolUsage)]
    [SettingsUIKeyboardAction(KeyBindAction.TogglePrioritiesTool, Usages.kDefaultUsage, Usages.kEditorUsage, Usages.kToolUsage)]
    [SettingsUIKeyboardAction(KeyBindAction.RemoveAllConnections, Usages.kToolUsage, ModKeyUsages.LaneConnectorToolActive)]
    [SettingsUIKeyboardAction(KeyBindAction.RemoveUTurns, Usages.kToolUsage, ModKeyUsages.LaneConnectorToolActive)]
    [SettingsUIKeyboardAction(KeyBindAction.RemoveUnsafe, Usages.kToolUsage, ModKeyUsages.LaneConnectorToolActive)]
    [SettingsUIKeyboardAction(KeyBindAction.ResetIntersectionToDefaults, Usages.kToolUsage, ModKeyUsages.LaneConnectorToolActive)]
    [SettingsUIKeyboardAction(KeyBindAction.PrioritiesToggleDisplayMode, Usages.kToolUsage, ModKeyUsages.PrioritiesToolActive)]
    [SettingsUIKeyboardAction(KeyBindAction.PrioritiesPriority, Usages.kToolUsage, ModKeyUsages.PrioritiesToolActive)]
    [SettingsUIKeyboardAction(KeyBindAction.PrioritiesYield, Usages.kToolUsage, ModKeyUsages.PrioritiesToolActive)]
    [SettingsUIKeyboardAction(KeyBindAction.PrioritiesStop, Usages.kToolUsage, ModKeyUsages.PrioritiesToolActive)]
    [SettingsUIKeyboardAction(KeyBindAction.PrioritiesReset, Usages.kToolUsage, ModKeyUsages.PrioritiesToolActive)]
#if GAMEPAD_SUPPORT
    [SettingsUIGamepadAction(KeyBindAction.ToggleLaneConnectorTool, Usages.kDefaultUsage, Usages.kEditorUsage, Usages.kToolUsage)]
#endif
    public partial class ModSettings
    {
        internal const string ToolsSection = "Tools";
        internal const string PriorityToolSection = "KeybindsPriorityTools";
        internal const string SelectedNodeSection = "KeybindsSelectedNode";
        internal const string OtherSection = "OtherSection";

        [SettingsUISection(KeybindingsTab, ToolsSection)]
        [SettingsUISetter(typeof(ModSettings), nameof(OnUseVanillaToolActionsSet))]
        public bool UseVanillaToolActions { get; set; }
        
        [SettingsUISection(KeybindingsTab, ToolsSection)]
        [SettingsUIMouseBinding(BindingMouse.Left, KeyBindAction.ApplyTool, ctrl: false)]
        [SettingsUIDisableByCondition(typeof(ModSettings), nameof(UseVanillaToolActions))]
        public ProxyBinding ApplyToolAction { get; set; }
        
        [SettingsUISection(KeybindingsTab, ToolsSection)]
        [SettingsUIMouseBinding(BindingMouse.Right, KeyBindAction.CancelTool, ctrl: false)]
        [SettingsUIDisableByCondition(typeof(ModSettings), nameof(UseVanillaToolActions))]
        public ProxyBinding CancelToolAction { get; set; }
        
        [SettingsUIGamepadBinding()]
        [SettingsUISection(KeybindingsTab, ToolsSection)]
        [SettingsUIKeyboardBinding(BindingKeyboard.R, KeyBindAction.ToggleLaneConnectorTool, ctrl: true)]
        public ProxyBinding LaneConnectorToolAction { get; set; }
        
        [SettingsUISection(KeybindingsTab, ToolsSection)]
        [SettingsUIKeyboardBinding(BindingKeyboard.T, KeyBindAction.TogglePrioritiesTool, ctrl: true)]
        public ProxyBinding PrioritiesToolAction { get; set; }
        
#if GAMEPAD_SUPPORT
        [SettingsUISection(KeybindingsTab, ToolsSection)]
        [SettingsUIGamepadBinding(BindingGamepad.LeftShoulder, KeyBindAction.ToggleLaneConnectorTool, true)]
        public ProxyBinding LaneConnectorToolActionGamepad { get; set; }
#endif
        
        [SettingsUISection(KeybindingsTab, SelectedNodeSection)]
        [SettingsUIKeyboardBinding(BindingKeyboard.Digit1, KeyBindAction.RemoveAllConnections, ctrl: true)]
        public ProxyBinding RemoveIntersectionConnections { get; set; }
        
        [SettingsUISection(KeybindingsTab, SelectedNodeSection)]
        [SettingsUIKeyboardBinding(BindingKeyboard.Digit2, KeyBindAction.RemoveUTurns, ctrl: true)]
        public ProxyBinding RemoveUTurnConnections { get; set; }
        
        [SettingsUISection(KeybindingsTab, SelectedNodeSection)]
        [SettingsUIKeyboardBinding(BindingKeyboard.Digit3, KeyBindAction.RemoveUnsafe, ctrl: true)]
        public ProxyBinding RemoveUnsafeConnections { get; set; }
        
        [SettingsUISection(KeybindingsTab, SelectedNodeSection)]
        [SettingsUIKeyboardBinding(BindingKeyboard.Delete, KeyBindAction.ResetIntersectionToDefaults)]
        public ProxyBinding ResetIntersectionToDefaults { get; set; }
        
        [SettingsUISection(KeybindingsTab, PriorityToolSection)]
        [SettingsUIKeyboardBinding(BindingKeyboard.S, KeyBindAction.PrioritiesToggleDisplayMode, ctrl: true)]
        public ProxyBinding PrioritiesToggleDisplayModeAction { get; set; }
        
        [SettingsUISection(KeybindingsTab, PriorityToolSection)]
        [SettingsUIKeyboardBinding(BindingKeyboard.Digit1, KeyBindAction.PrioritiesPriority, ctrl: true)]
        public ProxyBinding PrioritiesUsePriorityAction { get; set; }
        
        [SettingsUISection(KeybindingsTab, PriorityToolSection)]
        [SettingsUIKeyboardBinding(BindingKeyboard.Digit2, KeyBindAction.PrioritiesYield, ctrl: true)]
        public ProxyBinding PrioritiesUseYieldAction { get; set; }
        
        [SettingsUISection(KeybindingsTab, PriorityToolSection)]
        [SettingsUIKeyboardBinding(BindingKeyboard.Digit3, KeyBindAction.PrioritiesStop, ctrl: true)]
        public ProxyBinding PrioritiesUseStopAction { get; set; }
        
        [SettingsUISection(KeybindingsTab, PriorityToolSection)]
        [SettingsUIKeyboardBinding(BindingKeyboard.Digit4, KeyBindAction.PrioritiesReset, ctrl: true)]
        public ProxyBinding PrioritiesUseResetAction { get; set; }
        
        
        [SettingsUISection(KeybindingsTab, OtherSection)]
        public bool ResetBindings
        {
            set {
                Logger.Info($"Reset key bindings, use vanilla actions [{UseVanillaToolActions}]");
                ResetKeyBindings();
                if (UseVanillaToolActions)
                {
                    DisposeToolActionWatchers();
                    RegisterToolActionWatchers();
                }
            }
        }
        
        private void OnUseVanillaToolActionsSet(bool value)
        {
            if (value)
            {
                RegisterToolActionWatchers();
            }
            else
            {
                DisposeToolActionWatchers();
            }
        }

        private void RegisterToolActionWatchers()
        {
            ProxyAction builtInApplyAction =  InputManager.instance.FindAction(InputManager.kToolMap, "Apply");
            ProxyBinding.Watcher applyWatcher = MimicVanillaAction(builtInApplyAction, GetAction(KeyBindAction.ApplyTool), "Mouse");
            _vanillaBindingWatchers.Add("Apply_Mouse", applyWatcher);
            
            ProxyAction builtInCancelAction = InputManager.instance.FindAction(InputManager.kToolMap, "Mouse Cancel");
            ProxyBinding.Watcher cancelWatcher = MimicVanillaAction(builtInCancelAction, GetAction(KeyBindAction.CancelTool), "Mouse");
            _vanillaBindingWatchers.Add("Cancel_Mouse", cancelWatcher);
        }

        private void DisposeToolActionWatchers()
        {
            if (_vanillaBindingWatchers.TryGetValue("Apply_Mouse", out ProxyBinding.Watcher applyWatcher))
            {
                applyWatcher.Dispose();
                _vanillaBindingWatchers.Remove("Apply_Mouse");
            }
            
            if (_vanillaBindingWatchers.TryGetValue("Cancel_Mouse", out ProxyBinding.Watcher cancelWatcher))
            {
                cancelWatcher.Dispose();
                _vanillaBindingWatchers.Remove("Cancel_Mouse");
            }
        }
        
        internal static class KeyBindAction
        {
            internal const string ApplyTool = "ApplyToolAction";
            internal const string CancelTool = "CancelToolAction";
            internal const string ToggleLaneConnectorTool = "ToggleLaneConnectorTool";
            internal const string TogglePrioritiesTool = "TogglePrioritiesTool";
            internal const string RemoveAllConnections = "RemoveAllConnections";
            internal const string RemoveUTurns = "RemoveUTurns";
            internal const string RemoveUnsafe = "RemoveUnsafe";
            internal const string ResetIntersectionToDefaults = "ResetIntersectionToDefaults";
            internal const string PrioritiesPriority = "PrioritiesPriority";
            internal const string PrioritiesYield = "PrioritiesYield";
            internal const string PrioritiesStop = "PrioritiesStop";
            internal const string PrioritiesReset = "PrioritiesReset";
            internal const string PrioritiesToggleDisplayMode = "PrioritiesToggleDisplayMode";
        }

        internal static class ModKeyUsages
        {
            internal const string PrioritiesToolActive = "Traffic.Tool.Priorities";
            internal const string LaneConnectorToolActive = "Traffic.Tool.LaneConnector";
            internal const string SelectedIntersection = "Traffic.Tool.SelectedIntersection";
        }
    }
}
