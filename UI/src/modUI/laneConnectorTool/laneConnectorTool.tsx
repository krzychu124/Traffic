import { Panel, PanelSection, Button, Number2 } from "cs2/ui";
import { useRef, useCallback, useMemo } from "react";
import { useValue, trigger } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { selectedIntersection$ } from "bindings";
import mod from "mod.json";
import styles from 'modUI/laneConnectorTool/laneConnectorTool.module.scss';
import { UIBindingConstants, ActionOverlayPreview, UIKeys } from "types/traffic";
import { useToolActions } from "modUI/laneConnectorTool/helpers/useToolActions";
import { useUpdatePanelPosition } from "modUI/laneConnectorTool/helpers/useUpdatePanelPosition";
import { VanillaComponentsResolver } from "types/internal";


interface Props {
  position: Number2;
  onPositionChanged: (value: Number2) => void;
}

export const LaneConnectorTool = ({ position, onPositionChanged }: Props) => {
  const selected = useValue(selectedIntersection$);
  const isSelected = useMemo(() => (selected?.entity.index || 0) > 0, [selected])
  const panel = useRef<HTMLDivElement | null>(null);

  const {translate} = useLocalization();
  const {DescriptionTooltip} = VanillaComponentsResolver.instance;

  const {
    handleEnterButton,
    handleLeaveButton,
  } = useToolActions();

  const confirmActivePreview = useCallback(() => {
    trigger(mod.id, UIBindingConstants.APPLY_TOOL_ACTION_PREVIEW)
  }, []);
  useUpdatePanelPosition({panel, onPositionChanged});

  return (
    <Panel
      className={styles.panel}
      draggable
      initialPosition={position}
      header={(<>
        <span className={styles.title}>{translate(UIKeys.LANE_CONNECTION_TOOL)}</span>
      </>)}
    >
      <div ref={panel}>
        <PanelSection>
          {!isSelected && (
            <>
              <span className={styles.selectIntersectionMessage}>{translate(UIKeys.SELECT_INTERSECTION)}</span>
            </>
          )}
          {isSelected && (
            <>
              <Button variant="flat"
                      className={styles.actionButton}
                      onMouseEnter={() => handleEnterButton(ActionOverlayPreview.RemoveAllConnections)}
                      onMouseLeave={handleLeaveButton}
                      onClick={confirmActivePreview}
                      type="button"
              >
                {translate(UIKeys.REMOVE_ALL_CONNECTIONS)}
              </Button>
              <Button variant="flat"
                      className={styles.actionButton}
                      onMouseEnter={() => handleEnterButton(ActionOverlayPreview.RemoveUTurns)}
                      onMouseLeave={handleLeaveButton}
                      onClick={confirmActivePreview}
                      type="button"
              >
                {translate(UIKeys.REMOVE_U_TURNS)}
              </Button>
              <DescriptionTooltip
                title={translate(UIKeys.REMOVE_UNSAFE_TOOLTIP_TITLE, 'Unsafe Lane')}
                description={translate(UIKeys.REMOVE_UNSAFE_TOOLTIP_MESSAGE)}
                direction="right"
              >
                <Button variant="flat"
                        className={styles.actionButton}
                        onMouseEnter={() => handleEnterButton(ActionOverlayPreview.RemoveUnsafe)}
                        onMouseLeave={handleLeaveButton}
                        onClick={confirmActivePreview}
                        type="button"
                >
                    {translate(UIKeys.REMOVE_UNSAFE)}
                </Button>
              </DescriptionTooltip>
              <Button variant="flat"
                      className={styles.actionButton}
                      onMouseEnter={() => handleEnterButton(ActionOverlayPreview.ResetToVanilla)}
                      onMouseLeave={handleLeaveButton}
                      onClick={confirmActivePreview}
                      type="button"
              >
                {translate(UIKeys.RESET_TO_VANILLA)}
              </Button>
            </>
          )}
        </PanelSection>
      </div>
    </Panel>
  );
}