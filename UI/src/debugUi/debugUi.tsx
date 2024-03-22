import React, { useCallback } from "react";
import { Portal, Button } from "cs2/ui";
import classNames from "classnames";
import mod from "../../mod.json";
import trafficIcon from 'images/traffic_icon.svg';
import { NetworkDebugInfo } from "debugUi/networkDebugInfo/networkDebugInfo";
import styles from 'debugUi/debugUi.module.scss';
import { useValue, trigger } from "cs2/api";
import { isDebugVisible$ } from "bindings";
import {UIBindingConstants} from "types/traffic";

export const DebugUi = () => {
  const isVisible = useValue(isDebugVisible$);

  const changeIsVisible = useCallback(() => trigger(mod.id, UIBindingConstants.SET_VISIBILITY, !isVisible), [isVisible]);

  return (
    <div>
      <Button src={trafficIcon} variant="floating" className={classNames({[styles.selected]: isVisible}, styles.toggle)}
                      onSelect={changeIsVisible} />
      {isVisible && (
        <Portal>
          <NetworkDebugInfo />
        </Portal>
      )}
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