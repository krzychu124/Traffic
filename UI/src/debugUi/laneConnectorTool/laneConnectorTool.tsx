import { Panel, PanelSection, Button, Number2 } from "cs2/ui";
import { useRef, useCallback, useMemo } from "react";
import { useValue, trigger } from "cs2/api";
import { selectedIntersection$ } from "bindings";
import styles from './laneConnectorTool.module.scss';
import mod from "mod.json";
import { UIBindingConstants, ActionOverlayPreview } from "types/traffic";

interface Props {
  position: Number2;
}

export const LaneConnectorTool = ({ position }: Props) => {
  const panel = useRef();
  const selected = useValue(selectedIntersection$);

  const isSelected = useMemo(() => (selected?.entity.index || 0) > 0, [selected])

  const confirmActivePreview = useCallback((action: ActionOverlayPreview) => {
    trigger(mod.id, UIBindingConstants.APPLY_TOOL_ACTION_PREVIEW)
  }, []);
  const updateActivePreview = useCallback((action: ActionOverlayPreview) => trigger(mod.id, UIBindingConstants.SET_ACTION_OVERLAY_PREVIEW, action), []);

  const handleEnterButton = useCallback((type: ActionOverlayPreview) => {
    updateActivePreview(type);
  }, [updateActivePreview]);
  const handleLeaveButton = useCallback((type: string) => {
    updateActivePreview(ActionOverlayPreview.None)
  }, [updateActivePreview]);

  return (
    <div>
      <Panel
        className={styles.panel}
        draggable
        initialPosition={position}
        header={(<>
          <span className={styles.title}>Lane Connection Tool</span>
        </>)}
      >
        <div ref={panel.current}></div>
        <PanelSection>
          {!isSelected && (
            <>
              <span>Select intersection to begin editing</span>
            </>
          )}
          {isSelected && (
            <>
              <Button variant="flat"
                      className={styles.actionButton}
                      onMouseEnter={() => handleEnterButton(ActionOverlayPreview.RemoveAllConnections)}
                      onMouseLeave={() => handleLeaveButton('remove-all')}
                      onClick={() => confirmActivePreview(ActionOverlayPreview.RemoveAllConnections)}
                      type="button"
              >
                Remove All Connections
              </Button>
              <Button variant="flat"
                      className={styles.actionButton}
                      onMouseEnter={() => handleEnterButton(ActionOverlayPreview.RemoveUTurns)}
                      onMouseLeave={() => handleLeaveButton('remove-u-turns')}
                      onClick={() => confirmActivePreview(ActionOverlayPreview.RemoveUTurns)}
                      type="button"
              >
                Remove U-Turns
              </Button>
              <Button variant="flat"
                      className={styles.actionButton}
                      onMouseEnter={() => handleEnterButton(ActionOverlayPreview.RemoveUnsafe)}
                      onMouseLeave={() => handleLeaveButton('remove-unsafe')}
                      onClick={() => confirmActivePreview(ActionOverlayPreview.RemoveUnsafe)}
                      type="button"
              >
                Remove Unsafe
              </Button>
              <Button variant="flat"
                      className={styles.actionButton}
                      onMouseEnter={() => handleEnterButton(ActionOverlayPreview.ResetToVanilla)}
                      onMouseLeave={() => handleLeaveButton('reset-vanilla')}
                      onClick={() => confirmActivePreview(ActionOverlayPreview.ResetToVanilla)}
                      type="button"
              >
                Reset To Vanilla
              </Button>
            </>)}
        </PanelSection>
      </Panel>
    </div>
  );
}