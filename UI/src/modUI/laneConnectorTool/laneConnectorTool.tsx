import { Panel, PanelSection, Button, Number2 } from "cs2/ui";
import { useRef, useCallback, useMemo, useEffect, useState } from "react";
import { useValue, trigger } from "cs2/api";
import { selectedIntersection$ } from "bindings";
import styles from 'modUI/laneConnectorTool/laneConnectorTool.module.scss';
import mod from "mod.json";
import { UIBindingConstants, ActionOverlayPreview } from "types/traffic";
import { useRem, useMemoizedValue } from "cs2/utils";
import { fitScreen, simpleBoundingRectComparer } from "types/internal";

// import { useRem, useCssLength } from "cs2/utils";

interface Props {
  position: Number2;
  onPositionChanged: (value: Number2) => void;
}

export const LaneConnectorTool = ({ position, onPositionChanged }: Props) => {
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


  const panel = useRef<any>();
  const currentRect = useMemoizedValue<DOMRect | undefined>(panel.current?.getBoundingClientRect(), simpleBoundingRectComparer);
  const rem = useRem();

  useEffect(() => {
    if (currentRect && currentRect?.x > 0 && currentRect?.y > 0) {
      const rect: DOMRect = currentRect;
      const newPos = { x: rect.x * rem / 1920, y: rect.y * rem / 1080 };
      onPositionChanged(fitScreen(newPos));
    }
  }, [currentRect, rem, onPositionChanged]);

  return (
    <Panel
      className={styles.panel}
      draggable
      initialPosition={position}
      header={(<>
        <span className={styles.title}>Lane Connection Tool</span>
      </>)}
    >
      <div ref={panel}>
        <PanelSection>
          {!isSelected && (
            <>
              <span className={styles.selectIntersectionMessage}>Select intersection to begin editing</span>
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
      </div>
    </Panel>
  );
}