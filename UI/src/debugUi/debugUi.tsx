import React, { useState } from "react";
import { Portal, FloatingButton, Button } from "cs2/ui";
import classNames from "classnames";
import trafficIcon from 'images/traffic_icon.svg';
import { NetworkDebugInfo } from "debugUi/networkDebugInfo/networkDebugInfo";
import styles from 'debugUi/debugUi.module.scss';

export const DebugUi = () => {
  const [panelVisible, setPanelVisible] = useState(false);

  return (
    <div>
      <Button src={trafficIcon} variant="floating" className={classNames({[styles.selected]: panelVisible}, styles.toggle)}
                      onSelect={() => setPanelVisible(state => !state)} />
      {panelVisible && (
        <Portal>
          <NetworkDebugInfo />
        </Portal>
      )}
    </div>
  )
}

export const DebugUiEditorButton = () => {
  const [panelVisible, setPanelVisible] = useState(false);

  return (
    <div style={{ position: "absolute", top: '55rem', left: '220rem', pointerEvents: 'auto' }}>
      <Button src={trafficIcon} variant="floating" className={classNames({[styles.selected]: panelVisible}, styles.toggle)}
              onClick={() => setPanelVisible(state => !state)} />
      {panelVisible && (
        <Portal>
          <NetworkDebugInfo inEditor />
        </Portal>
      )}
    </div>
  )
}