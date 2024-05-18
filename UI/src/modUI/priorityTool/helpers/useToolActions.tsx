import { useCallback } from "react";
import { trigger } from "cs2/api";
import mod from "mod.json";
import { UIBindingConstants, OverlayMode, PriorityToolSetMode, ActionOverlayPreview } from "types/traffic";

export const useToolActions = () => {

  const handleSelectPriority = useCallback(() => {
    trigger(mod.id, UIBindingConstants.TOOL_MODE, PriorityToolSetMode.Priority);
  }, []);
  const handleSelectStop = useCallback(() => {
    trigger(mod.id, UIBindingConstants.TOOL_MODE, PriorityToolSetMode.Stop);
  }, []);
  const handleSelectYield = useCallback(() => {
    trigger(mod.id, UIBindingConstants.TOOL_MODE, PriorityToolSetMode.Yield);
  }, []);
  const handleSelectReset = useCallback(() => {
    trigger(mod.id, UIBindingConstants.TOOL_MODE, PriorityToolSetMode.Reset);
  }, []);
  const handleLaneGroupOverlayMode = useCallback(() => {
    trigger(mod.id, UIBindingConstants.OVERLAY_MODE, OverlayMode.LaneGroup);
  }, []);
  const handleLaneOverlayMode = useCallback(() => {
    trigger(mod.id, UIBindingConstants.OVERLAY_MODE, OverlayMode.Lane);
  }, []);
  const handleEnterButton = useCallback(() => {
    trigger(mod.id, UIBindingConstants.SET_ACTION_OVERLAY_PREVIEW, ActionOverlayPreview.ResetToVanilla);
  }, []);
  const handleLeaveButton = useCallback(() => {
    trigger(mod.id, UIBindingConstants.SET_ACTION_OVERLAY_PREVIEW, ActionOverlayPreview.None);
  }, []);

  return {
    handleSelectPriority,
    handleSelectReset,
    handleSelectStop,
    handleSelectYield,
    handleLaneGroupOverlayMode,
    handleLaneOverlayMode,
    handleEnterButton,
    handleLeaveButton,
  }
}