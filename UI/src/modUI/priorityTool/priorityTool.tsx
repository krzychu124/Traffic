import { useRef, useCallback, useMemo, CSSProperties, useState } from "react";
import classNames from "classnames";
import { Panel, PanelSection, Button } from "cs2/ui";
import { useValue, trigger } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { selectedIntersection$, currentToolMode$, currentToolOverlayMode$, modKeyBindings$ } from "bindings";
import mod from "mod.json";
import styles from './priorityTool.module.scss';
import { UIBindingConstants, UIKeys, PriorityToolSetMode, OverlayMode, ModKeyBinds } from "types/traffic";
import { VanillaComponentsResolver } from "types/internal";
import { useToolActions } from "modUI/priorityTool/helpers/useToolActions";
import { DescriptionTooltipWithKeyBind } from "modUI/descriptionTooltipWithKeyBind/descriptionTooltipWithKeyBind";

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
    <Panel
      className={classNames(styles.panel)}
      style={positionStyle}
      header={(<>
        <DescriptionTooltipWithKeyBind
          title={translate("UIKeys.KEY_PRIORITIES_TOGGLE_DISPLAY_MODE")}
          description={translate(UIKeys.TOGGLE_DISPLAY_MODE_TOOLTIP)}
          keyBind={keyBindings.toggleDisplayMode}
        >
          <span className={styles.title}>Priority Tool</span>
        </DescriptionTooltipWithKeyBind>
      </>)}
    >
      <div ref={panel}>
        <PanelSection>
          {!isSelected && (
            <span className={styles.selectIntersectionMessage}>{translate(UIKeys.SELECT_INTERSECTION)}</span>
          )}
          {isSelected && (
            <>
              <div>
                <span className={styles.sectionTitle}>Display mode</span>
                <div className={styles.applyMode}>
                  <Button variant="flat"
                          className={classNames(styles.actionButton, styles.modeButton, {
                            [styles.selected]: overlayMode === OverlayMode.LaneGroup
                          })}
                          onClick={handleLaneGroupOverlayMode}
                          type="button"
                  >
                    Lane Group
                  </Button>
                  <Button variant="flat"
                          className={classNames(styles.actionButton, styles.modeButton, { [styles.selected]: overlayMode === OverlayMode.Lane })}
                          onClick={handleLaneOverlayMode}
                          type="button"
                  >
                    Lane
                  </Button>
                </div>
              </div>
              <div className={styles.actionModeDivider}></div>

              <span className={styles.sectionTitle}>Apply action</span>
              <div className="row">
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
                    Priority
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
                    Yield
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
                    Stop
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
                    Reset to defaults
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
        </PanelSection>
      </div>
    </Panel>
  );
}