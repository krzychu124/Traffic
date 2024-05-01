import { useCallback } from "react";
import { trigger } from "cs2/api";
import { ActionOverlayPreview, UIBindingConstants } from "types/traffic";
import mod from "mod.json";

export const useToolActions = () => {
  const updateActivePreview = useCallback((action: ActionOverlayPreview) => trigger(mod.id, UIBindingConstants.SET_ACTION_OVERLAY_PREVIEW, action), []);

  const handleEnterButton = useCallback((type: ActionOverlayPreview) => {
    updateActivePreview(type);
  }, [updateActivePreview]);
  const handleLeaveButton = useCallback(() => {
    updateActivePreview(ActionOverlayPreview.None)
  }, [updateActivePreview]);

  return {
    handleEnterButton,
    handleLeaveButton,
  }
}