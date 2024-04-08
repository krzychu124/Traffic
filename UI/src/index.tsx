import { ModRegistrar } from "cs2/modding";
import { DebugUiEditorButton } from "debugUi/debugUi";
import { ModUI } from "modUI/modUI";

const register: ModRegistrar = (moduleRegistry) => {
    // While launching game in UI development mode (include --uiDeveloperMode in the launch options)
    // - Access the dev tools by opening localhost:9444 in chrome browser.
    // moduleRegistry.append('GameTopLeft', DebugUi);
    moduleRegistry.append('GameTopLeft', ModUI);
    moduleRegistry.append('Editor', DebugUiEditorButton);
}

export default register;