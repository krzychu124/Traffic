import React, { useCallback, useState } from "react";
import { Portal, Button, Number2 } from "cs2/ui";
import classNames from "classnames";
import mod from "../../mod.json";
import trafficIcon from 'images/traffic_icon.svg';
import { NetworkDebugInfo } from "debugUi/networkDebugInfo/networkDebugInfo";
import styles from 'debugUi/debugUi.module.scss';
import { useValue, trigger } from "cs2/api";
import { isDebugVisible$ } from "bindings";
import {UIBindingConstants} from "types/traffic";
import { tool } from "cs2/bindings";
import { LaneConnectorTool } from "modUI/laneConnectorTool/laneConnectorTool";

const LC_TOOL = "Lane Connection Tool";

export const DebugUi = () => {
  const isVisible = useValue(isDebugVisible$);
  const selectedTool = useValue(tool.activeTool$);

  const changeIsVisible = useCallback(() => trigger(mod.id, UIBindingConstants.SET_VISIBILITY, selectedTool.id === LC_TOOL), [selectedTool.id]);

  return (
    <div>
      {isVisible && (
        <NetworkDebugInfo />
      )}
      <Button src={trafficIcon}
              variant="floating"
              className={classNames({[styles.selected]: selectedTool.id === LC_TOOL}, styles.toggle)}
              onSelect={changeIsVisible} />
    </div>
  )
}

export const DebugUiEditorButton = () => {
  const isVisible = useValue(isDebugVisible$);

  const changeIsVisible = useCallback(() => trigger(mod.id, UIBindingConstants.SET_VISIBILITY, !isVisible), [isVisible]);

  return (
    <div style={{ position: "absolute", top: '55rem', left: '220rem', pointerEvents: 'auto' }}>
      <Button src={trafficIcon} variant="floating" className={classNames({[styles.selected]: isVisible}, styles.toggle)}
              onClick={changeIsVisible} />
      {isVisible && (
        <Portal>
          <NetworkDebugInfo inEditor />
        </Portal>
      )}
    </div>
  )
}