import React, { useCallback, useState, useRef, useEffect } from "react";
import classNames from "classnames";
import { Button, Number2 } from "cs2/ui";
import { tool } from "cs2/bindings";
import { useValue, trigger } from "cs2/api";
import { isDebugVisible$, modKeyBindings$ } from "bindings";
import { NetworkDebugInfo } from "debugUi/networkDebugInfo/networkDebugInfo";
import { LaneConnectorTool } from "modUI/laneConnectorTool/laneConnectorTool";
import { UIBindingConstants, ModKeyBinds, UIKeys, ModTool } from "types/traffic";
import mod from "../../mod.json";
import trafficIcon from 'images/traffic_icon.svg';
import styles from 'debugUi/debugUi.module.scss';
import { ToolSelectionPanel } from "modUI/toolSelectionPanel/toolSelectionPanel";
import { PriorityTool } from "modUI/priorityTool/priorityTool";
import { useLocalization } from "cs2/l10n";
import { DescriptionTooltipWithKeyBind } from "modUI/descriptionTooltipWithKeyBind/descriptionTooltipWithKeyBind";


export const DebugUi = () => {
  const isVisible = useValue(isDebugVisible$);
  const selectedTool = useValue(tool.activeTool$);

  const changeIsVisible = useCallback(() => {
    trigger(mod.id, UIBindingConstants.SET_VISIBILITY, selectedTool.id === UIBindingConstants.LANE_CONNECTOR_TOOL);
  }, [selectedTool.id]);

  return (
    <div>
      {isVisible && (
        <NetworkDebugInfo />
      )}
      <Button src={trafficIcon}
              variant="floating"
              className={classNames({ [styles.selected]: selectedTool.id === UIBindingConstants.LANE_CONNECTOR_TOOL }, styles.toggle)}
              onSelect={changeIsVisible} />
    </div>
  )
}
        {/*{selectedTool.id === UIBindingConstants.LANE_CONNECTOR_TOOL && (*/}
        {/*  <Portal>*/}
        {/*    <NetworkDebugInfo inEditor />*/}
        {/*  </Portal>*/}
        {/*)}*/}

export const DebugUiEditorButton = () => {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const lastTool = useRef<string |null>(null);
  const [mainMenuOpen, setMainMenuOpen] = useState(false);
  const {translate} = useLocalization();
  const selectedTool = useValue(tool.activeTool$);
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
    <div>
      <div ref={containerRef}
           style={{ position: "absolute", top: '55rem', left: '220rem', pointerEvents: 'auto' }}
      >
        <DescriptionTooltipWithKeyBind title="Traffic"
                                       description={translate(UIKeys.TRAFFIC_MOD)}
                                       keyBind={keyBindings?.laneConnectorTool}
        >
          <Button src={trafficIcon} variant="floating"
                  className={classNames({ [styles.selected]: mainMenuOpen }, styles.toggle)}
                  onClick={toggleMenu} />
        </DescriptionTooltipWithKeyBind>

        {mainMenuOpen && (
          <ToolSelectionPanel anchor={containerRef.current && {x: 0, y: containerRef.current.offsetHeight}} />
        )}
      </div>

      <div>
        {selectedTool.id === UIBindingConstants.LANE_CONNECTOR_TOOL && (
          <LaneConnectorTool isEditor />
        )}
        {selectedTool.id === UIBindingConstants.PRIORITIES_TOOL && (
          <PriorityTool isEditor />
        )}
      </div>
    </div>
  )
}