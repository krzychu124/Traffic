import { ModRegistrar } from "cs2/modding";
import { DebugUiEditorButton } from "debugUi/debugUi";
import { ModUI } from "modUI/modUI";

const register: ModRegistrar = (moduleRegistry) => {
  moduleRegistry.append('GameTopLeft', ModUI);
  moduleRegistry.append('Editor', DebugUiEditorButton);
}

export default register;