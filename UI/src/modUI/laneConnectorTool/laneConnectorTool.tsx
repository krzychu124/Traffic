import { useRef, useCallback, useMemo, CSSProperties } from "react";
import classNames from "classnames";
import { Panel, PanelSection, Button } from "cs2/ui";
import { useValue, trigger } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { selectedIntersection$ } from "bindings";
import mod from "mod.json";
import styles from 'modUI/laneConnectorTool/laneConnectorTool.module.scss';
import { UIBindingConstants, ActionOverlayPreview, UIKeys } from "types/traffic";
import { useToolActions } from "modUI/laneConnectorTool/helpers/useToolActions";
import { VanillaComponentsResolver } from "types/internal";

interface Props {
  isEditor?: boolean;
  onOpenLoadingResults?: () => void;
  showLoadingErrorsButton?: boolean;
}

export const LaneConnectorTool = ({isEditor, showLoadingErrorsButton, onOpenLoadingResults}: Props) => {
  const selected = useValue(selectedIntersection$);
  const isSelected = useMemo(() => (selected?.entity.index || 0) > 0, [selected])
  const panel = useRef<HTMLDivElement | null>(null);

  const {translate} = useLocalization();
  const {DescriptionTooltip} = VanillaComponentsResolver.instance;
  const positionStyle: Partial<CSSProperties> = useMemo(() => ({ top:`${(isEditor ? 800: 750)}rem`, left: `55rem` }), [isEditor]);

  const {
    handleEnterButton,
    handleLeaveButton,
  } = useToolActions();

  const confirmActivePreview = useCallback(() => {
    trigger(mod.id, UIBindingConstants.APPLY_TOOL_ACTION_PREVIEW)
  }, []);

  return (
    <Panel
      className={classNames(styles.panel, {[styles.withIssues]: showLoadingErrorsButton})}
      style={positionStyle}
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
          {!isSelected &&
           showLoadingErrorsButton &&
           onOpenLoadingResults && (
             <div className={styles.loadingErrors}>
               <p className={styles.loadingErrorsMessage}>The mod data has been loaded with errors</p>
               <Button variant="flat"
                       className={styles.actionLoadingResultsButton}
                       onClick={onOpenLoadingResults}
                       type="button"
               >
                 {translate("Show data loading results", "Show data loading results")}
               </Button>
             </div>
           )
          }
        </PanelSection>
      </div>
    </Panel>
  );
}