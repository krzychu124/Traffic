import { useRef, useCallback, useMemo, CSSProperties } from "react";
import classNames from "classnames";
import { Button } from "cs2/ui";
import { useValue, trigger } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { selectedIntersection$, currentToolMode$, currentToolOverlayMode$, modKeyBindings$ } from "bindings";
import { UIBindingConstants, UIKeys, PriorityToolSetMode, OverlayMode, ModKeyBinds } from "types/traffic";
import { useToolActions } from "modUI/priorityTool/helpers/useToolActions";
import { DescriptionTooltipWithKeyBind } from "modUI/descriptionTooltipWithKeyBind/descriptionTooltipWithKeyBind";
import { SimplePanel } from "components/simplePanel";
import mod from "mod.json";
import styles from './priorityTool.module.scss';

interface Props {
  isEditor?: boolean;
  onOpenLoadingResults?: () => void;
  showLoadingErrorsButton?: boolean;
}

export const PriorityTool = ({ isEditor, showLoadingErrorsButton, onOpenLoadingResults }: Props) => {
  const selected = useValue(selectedIntersection$);
  const isSelected = useMemo(() => (selected?.entity.index || 0) > 0, [selected])
  const panel = useRef<HTMLDivElement | null>(null);
  const setActionMode = useValue<PriorityToolSetMode>(currentToolMode$);
  const overlayMode = useValue<number>(currentToolOverlayMode$);
  const keyBindings = useValue<ModKeyBinds>(modKeyBindings$);
  const { translate } = useLocalization();
  const {
    handleSelectPriority,
    handleSelectReset,
    handleSelectStop,
    handleSelectYield,
    handleLaneGroupOverlayMode,
    handleLaneOverlayMode,
    handleEnterButton,
    handleLeaveButton,
  } = useToolActions();

  const positionStyle: Partial<CSSProperties> = useMemo(() => ({ top: `${(isEditor ? 700 : 600)}rem`, left: `55rem` }), [isEditor]);

  const confirmActivePreview = useCallback(() => {
    trigger(mod.id, UIBindingConstants.APPLY_TOOL_ACTION_PREVIEW)
  }, []);

  return (
    <SimplePanel
      className={classNames(styles.panel)}
      style={positionStyle}
      header={(<div>
          <span className={styles.title}>{translate(UIKeys.PRIORITIES_TOOL)}</span>
      </div>)}
    >
      <div ref={panel}>
        {!isSelected && (
          <span className={styles.selectIntersectionMessage}>{translate(UIKeys.SELECT_INTERSECTION)}</span>
        )}
        {isSelected && (
          <>
            <div>
              <DescriptionTooltipWithKeyBind
                title={translate("UIKeys.KEY_PRIORITIES_TOGGLE_DISPLAY_MODE")}
                description={translate(UIKeys.TOGGLE_DISPLAY_MODE_TOOLTIP)}
                keyBind={keyBindings.toggleDisplayMode}
              >
                <span className={styles.sectionTitle}>{translate(UIKeys.APPLY_MODE)}</span>
              </DescriptionTooltipWithKeyBind>
              <div className={styles.applyMode}>
                <Button variant="flat"
                        className={classNames(styles.actionButton, styles.modeButton, {
                          [styles.selected]: overlayMode === OverlayMode.LaneGroup
                        })}
                        onClick={handleLaneGroupOverlayMode}
                        type="button"
                >
                  {translate(UIKeys.LANE_GROUP_MODE)}
                </Button>
                <Button variant="flat"
                        className={classNames(styles.actionButton, styles.modeButton, { [styles.selected]: overlayMode === OverlayMode.Lane })}
                        onClick={handleLaneOverlayMode}
                        type="button"
                >
                  {translate(UIKeys.LANE_MODE)}
                </Button>
              </div>
            </div>
            <div className={styles.actionModeDivider}></div>

            <span className={styles.sectionTitle}>{translate(UIKeys.APPLY_ACTION)}</span>
            <div>
              <DescriptionTooltipWithKeyBind
                title={translate(UIKeys.PRIORITY_ACTION)}
                description={translate(UIKeys.PRIORITY_ACTION_TOOLTIP)}
                keyBind={keyBindings.usePriority}
              >
                <Button variant="flat"
                        className={classNames(styles.actionButton, { [styles.selected]: setActionMode === PriorityToolSetMode.Priority })}
                        onClick={handleSelectPriority}
                        type="button"
                >
                  {translate(UIKeys.PRIORITY_ACTION)}
                </Button>
              </DescriptionTooltipWithKeyBind>
              <DescriptionTooltipWithKeyBind
                title={translate(UIKeys.YIELD_ACTION)}
                description={translate(UIKeys.YIELD_ACTION_TOOLTIP)}
                keyBind={keyBindings.useYield}
              >
                <Button variant="flat"
                        className={classNames(styles.actionButton, { [styles.selected]: setActionMode === PriorityToolSetMode.Yield })}
                        onClick={handleSelectYield}
                        type="button"
                >
                  {translate(UIKeys.YIELD_ACTION)}
                </Button>
              </DescriptionTooltipWithKeyBind>
              <DescriptionTooltipWithKeyBind
                title={translate(UIKeys.STOP_ACTION)}
                description={translate(UIKeys.STOP_ACTION_TOOLTIP)}
                keyBind={keyBindings.useStop}
              >
                <Button variant="flat"
                        className={classNames(styles.actionButton, { [styles.selected]: setActionMode === PriorityToolSetMode.Stop })}
                        onClick={handleSelectStop}
                        type="button"
                >
                  {translate(UIKeys.STOP_ACTION)}
                </Button>
              </DescriptionTooltipWithKeyBind>
              <DescriptionTooltipWithKeyBind
                title={translate(UIKeys.RESET_ACTION)}
                description={translate(UIKeys.RESET_ACTION_TOOLTIP)}
                keyBind={keyBindings.useReset}
              >
                <Button variant="flat"
                        className={classNames(styles.actionButton, { [styles.selected]: setActionMode === PriorityToolSetMode.Reset })}
                        onClick={handleSelectReset}
                        type="button"
                >
                  {translate(UIKeys.RESET_ACTION)}
                </Button>
              </DescriptionTooltipWithKeyBind>
              <div className={styles.actionModeDivider}></div>
              <DescriptionTooltipWithKeyBind
                title={translate(UIKeys.RESET_TO_VANILLA)}
                description={translate(UIKeys.RESET_TO_VANILLA_TOOLTIP_MESSAGE)}
                keyBind={keyBindings.resetDefaults}
              >
                <Button variant="flat"
                        className={classNames(styles.actionButton)}
                        onClick={confirmActivePreview}
                        onMouseEnter={handleEnterButton}
                        onMouseLeave={handleLeaveButton}
                        type="button"
                >
                  {translate(UIKeys.RESET_TO_VANILLA)}
                </Button>
              </DescriptionTooltipWithKeyBind>
            </div>
          </>
        )}
      </div>
    </SimplePanel>
  );
}