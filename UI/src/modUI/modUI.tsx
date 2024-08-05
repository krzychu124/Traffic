import mod from "mod.json";
import styles from "modUI/modUI.module.scss";
import trafficIcon from "images/traffic_icon.svg";
import React, { useCallback, useRef, useState, useEffect } from "react";
import classNames from "classnames";
import { Button, Portal } from "cs2/ui";
import { tool } from "cs2/bindings";
import { trigger, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { LaneConnectorTool } from "modUI/laneConnectorTool/laneConnectorTool";
import { UIBindingConstants, UIKeys, ModKeyBinds, ModTool } from "types/traffic";
import { loadingErrorsPresent$, modKeyBindings$ } from "bindings";
import { ToolSelectionPanel } from "modUI/toolSelectionPanel/toolSelectionPanel";
import { PriorityTool } from "modUI/priorityTool/priorityTool";
import { DataLoadingProblemModal } from "modUI/troubleshooting/dataLoadingProblemModal";
import { DescriptionTooltipWithKeyBind } from "modUI/descriptionTooltipWithKeyBind/descriptionTooltipWithKeyBind";

export const ModUI = () => {
  // const [position, setPosition] = useState<Number2>({ x: 0.025, y: 0.8 })
  const [mainMenuOpen, setMainMenuOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const loadingProblemsRef = useRef<any>(null);
  const lastTool = useRef<string |null>(null);
  const selectedTool = useValue(tool.activeTool$);
  const loadingErrorsPresent = useValue(loadingErrorsPresent$);
  const {translate} = useLocalization();
  const keyBindings = useValue<ModKeyBinds>(modKeyBindings$);

  const toggleMenu = useCallback(() => {
    if (mainMenuOpen) {
      trigger(mod.id, UIBindingConstants.TOGGLE_TOOL, ModTool.None);
    }
    setMainMenuOpen(!mainMenuOpen);
  }, [mainMenuOpen]);

  useEffect(() => {
    const isAnyActive = selectedTool.id == UIBindingConstants.LANE_CONNECTOR_TOOL ||
                        selectedTool.id == UIBindingConstants.PRIORITIES_TOOL;
    if (!mainMenuOpen && isAnyActive) {
      setMainMenuOpen(true);
    } else if (mainMenuOpen && !isAnyActive && selectedTool.id !== lastTool.current) {
      setMainMenuOpen(false);
    }
    lastTool.current = selectedTool?.id;
  }, [mainMenuOpen, selectedTool?.id]);

  return (
    <>
      {selectedTool.id === UIBindingConstants.LANE_CONNECTOR_TOOL && (
        <LaneConnectorTool showLoadingErrorsButton={loadingErrorsPresent} onOpenLoadingResults={() => loadingProblemsRef.current?.toggleModal()} />
      )}
      {selectedTool.id === UIBindingConstants.PRIORITIES_TOOL && (
        <PriorityTool showLoadingErrorsButton={loadingErrorsPresent} onOpenLoadingResults={() => loadingProblemsRef.current?.toggleModal()} />
      )}

      {loadingErrorsPresent && (
        <DataLoadingProblemModal initState={mainMenuOpen} ref={loadingProblemsRef} />
      )}
      <div ref={containerRef}>
        <DescriptionTooltipWithKeyBind title="Traffic"
                                       description={translate(UIKeys.TRAFFIC_MOD)}
                                       keyBind={keyBindings?.laneConnectorTool}
        >
          <Button
            src={trafficIcon}
            variant="floating"
            className={classNames({ [styles.selected]: mainMenuOpen }, styles.toggle)}
            onSelect={toggleMenu}
          />
        </DescriptionTooltipWithKeyBind>

        {mainMenuOpen && (
          <Portal>
            <ToolSelectionPanel anchor={containerRef.current && {
              x: containerRef.current.offsetLeft + ((containerRef.current.parentNode as HTMLDivElement)?.offsetLeft || 0),
              y: containerRef.current.offsetHeight + ((containerRef.current.parentNode as HTMLDivElement)?.offsetTop || 0)
            }} />
          </Portal>
        )}
      </div>
    </>
  );
}