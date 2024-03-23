import { ModRegistrar } from "cs2/modding";
import { DebugUi, DebugUiEditorButton } from "debugUi/debugUi";

const register: ModRegistrar = (moduleRegistry) => {
    // While launching game in UI development mode (include --uiDeveloperMode in the launch options)
    // - Access the dev tools by opening localhost:9444 in chrome browser.
    moduleRegistry.append('GameTopLeft', DebugUi);
    moduleRegistry.append('Editor', DebugUiEditorButton);
}

export default register;