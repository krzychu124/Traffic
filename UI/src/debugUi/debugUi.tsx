import React, { useCallback, useState } from "react";
import classNames from "classnames";
import { Button, Number2 } from "cs2/ui";
import { tool } from "cs2/bindings";
import { useValue, trigger } from "cs2/api";
import { isDebugVisible$ } from "bindings";
import { NetworkDebugInfo } from "debugUi/networkDebugInfo/networkDebugInfo";
import { LaneConnectorTool } from "modUI/laneConnectorTool/laneConnectorTool";
import { UIBindingConstants } from "types/traffic";
import mod from "../../mod.json";
import trafficIcon from 'images/traffic_icon.svg';
import styles from 'debugUi/debugUi.module.scss';


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

export const DebugUiEditorButton = () => {
  const selectedTool = useValue(tool.activeTool$);
  const [position, setPosition] = useState<Number2>({ x: 0.025, y: 0.8 })

  const changeIsVisible = useCallback(() => {
    trigger(mod.id, UIBindingConstants.SET_VISIBILITY, selectedTool.id === UIBindingConstants.LANE_CONNECTOR_TOOL);
    trigger(mod.id, UIBindingConstants.TOGGLE_TOOL, selectedTool.id !== UIBindingConstants.LANE_CONNECTOR_TOOL);
  }, [selectedTool.id]);

  return (
    <div>
      <div style={{ position: "absolute", top: '55rem', left: '220rem', pointerEvents: 'auto' }}>
        <Button src={trafficIcon} variant="floating"
                className={classNames({ [styles.selected]: selectedTool.id === UIBindingConstants.LANE_CONNECTOR_TOOL }, styles.toggle)}
                onClick={changeIsVisible}
        />

        {/*{selectedTool.id === UIBindingConstants.LANE_CONNECTOR_TOOL && (*/}
        {/*  <Portal>*/}
        {/*    <NetworkDebugInfo inEditor />*/}
        {/*  </Portal>*/}
        {/*)}*/}
      </div>
      {selectedTool.id === UIBindingConstants.LANE_CONNECTOR_TOOL && (
        <LaneConnectorTool position={position} onPositionChanged={setPosition} />
      )}
    </div>
  )
}