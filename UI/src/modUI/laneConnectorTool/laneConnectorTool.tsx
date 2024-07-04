import { useRef, useCallback, useMemo, CSSProperties } from "react";
import classNames from "classnames";
import { Panel, PanelSection, Button } from "cs2/ui";
import { useValue, trigger } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { DescriptionTooltipWithKeyBind } from "modUI/descriptionTooltipWithKeyBind/descriptionTooltipWithKeyBind";
import { selectedIntersection$, modKeyBindings$ } from "bindings";
import { UIBindingConstants, ActionOverlayPreview, UIKeys, ModKeyBinds } from "types/traffic";
import { useToolActions } from "modUI/laneConnectorTool/helpers/useToolActions";
import { VanillaComponentsResolver } from "types/internal";
import mod from "mod.json";
import styles from 'modUI/laneConnectorTool/laneConnectorTool.module.scss';

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
  const keyBindings = useValue<ModKeyBinds>(modKeyBindings$);

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
        <span className={styles.title}>{translate(UIKeys.LANE_CONNECTOR_TOOL)}</span>
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
              <DescriptionTooltipWithKeyBind
                title={translate(UIKeys.REMOVE_ALL_CONNECTIONS_TOOLTIP_TITLE, 'Remove Intersection Connections')}
                description={translate(UIKeys.REMOVE_ALL_CONNECTIONS_TOOLTIP_MESSAGE)}
                direction="right"
                keyBind={keyBindings?.removeAllConnections}
                className={styles.actionButtonContainer}
              >
                <Button variant="flat"
                        className={styles.actionButton}
                        onMouseEnter={() => handleEnterButton(ActionOverlayPreview.RemoveAllConnections)}
                        onMouseLeave={handleLeaveButton}
                        onClick={confirmActivePreview}
                        type="button"
                >
                  {translate(UIKeys.REMOVE_ALL_CONNECTIONS)}
                </Button>
              </DescriptionTooltipWithKeyBind>
              <DescriptionTooltipWithKeyBind
                title={translate(UIKeys.REMOVE_U_TURNS_TOOLTIP_TITLE, 'Remove U-Turns')}
                description={translate(UIKeys.REMOVE_U_TURNS_TOOLTIP_MESSAGE)}
                direction="right"
                keyBind={keyBindings?.removeUTurns}
                className={styles.actionButtonContainer}
              >
                <Button variant="flat"
                        className={styles.actionButton}
                        onMouseEnter={() => handleEnterButton(ActionOverlayPreview.RemoveUTurns)}
                        onMouseLeave={handleLeaveButton}
                        onClick={confirmActivePreview}
                        type="button"
                >
                  {translate(UIKeys.REMOVE_U_TURNS)}
                </Button>
              </DescriptionTooltipWithKeyBind>
              <DescriptionTooltipWithKeyBind
                title={translate(UIKeys.REMOVE_UNSAFE_TOOLTIP_TITLE, 'Unsafe Lane')}
                description={translate(UIKeys.REMOVE_UNSAFE_TOOLTIP_MESSAGE)}
                direction="right"
                keyBind={keyBindings?.removeUnsafe}
                className={styles.actionButtonContainer}
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
              </DescriptionTooltipWithKeyBind>
              <DescriptionTooltipWithKeyBind
                title={translate(UIKeys.RESET_TO_VANILLA_TOOLTIP_TITLE, 'Reset to Vanilla')}
                description={translate(UIKeys.RESET_TO_VANILLA_TOOLTIP_MESSAGE)}
                direction="right"
                keyBind={keyBindings?.resetDefaults}
                className={styles.actionButtonContainer}
              >
                <Button variant="flat"
                        className={styles.actionButton}
                        onMouseEnter={() => handleEnterButton(ActionOverlayPreview.ResetToVanilla)}
                        onMouseLeave={handleLeaveButton}
                        onClick={confirmActivePreview}
                        type="button"
                >
                  {translate(UIKeys.RESET_TO_VANILLA)}
                </Button>
              </DescriptionTooltipWithKeyBind>
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