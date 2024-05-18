namespace Traffic.CommonData
{
    public class UIBindingConstants
    {
        public const string DEBUG_TEXTS = "debugTexts";
        public const string IS_DEBUG = "isDebugEnabled";
        public const string SET_VISIBILITY = "setVisibility";
        //general
        public const string TOGGLE_TOOL = "toggleTool";
        public const string KEY_BINDINGS = "keybindings";
        public const string TOOL_MODE = "toolMode";
        public const string CURRENT_TOOL_MODE = "currentToolMode";
        public const string OVERLAY_MODE = "overlayMode";

        //tools
        public const string LANE_CONNECTOR_TOOL = "Lane Connector Tool";
        public const string PRIORITIES_TOOL = "Priorities Tool";
        
        // lane connector tool
        public const string SET_ACTION_OVERLAY_PREVIEW = "setActionOverlayPreview";
        public const string SELECTED_INTERSECTION = "selectedIntersection";
        public const string APPLY_TOOL_ACTION_PREVIEW = "applyToolActionPreview";
        
        // priorities tool
        
        //troubleshooting
        public const string LOADING_ERRORS_PRESENT = "loadingErrorsPresent";
        public const string ERROR_AFFECTED_INTERSECTIONS = "errorAffectedIntersection";
        public const string REMOVE_ENTITY_FROM_LIST = "removeEntityFromList";
        
        // helpers
        public const string NAVIGATE_TO_ENTITY = "navigateToEntity";
    }
}
