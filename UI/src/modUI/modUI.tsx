import React, { useCallback, useState } from "react";
import classNames from "classnames";
import { Button, Number2} from "cs2/ui";
import { tool } from "cs2/bindings";
import { trigger, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { LaneConnectorTool } from "modUI/laneConnectorTool/laneConnectorTool";
import { UIBindingConstants, UIKeys } from "types/traffic";
import { VanillaComponentsResolver } from "types/internal";
import mod from "mod.json";
import styles from "modUI/modUI.module.scss";
import trafficIcon from "images/traffic_icon.svg";

export const ModUI = () => {
  const [position, setPosition] = useState<Number2>({ x: 0.025, y: 0.8 })
  const selectedTool = useValue(tool.activeTool$);
  const {translate} = useLocalization();
  const {DescriptionTooltip} = VanillaComponentsResolver.instance;

  const toggleTol = useCallback(() => trigger(mod.id, UIBindingConstants.TOGGLE_TOOL, selectedTool.id !== UIBindingConstants.LANE_CONNECTOR_TOOL), [selectedTool.id]);

  return (
    <>
      {selectedTool.id === UIBindingConstants.LANE_CONNECTOR_TOOL && (
        <LaneConnectorTool position={position} onPositionChanged={setPosition} />
      )}
      <DescriptionTooltip title="Traffic"
                          description={translate(UIKeys.TRAFFIC_MOD)}
      >
        <Button
          src={trafficIcon}
          variant="floating"
          className={classNames({ [styles.selected]: selectedTool.id === UIBindingConstants.LANE_CONNECTOR_TOOL }, styles.toggle)}
          onSelect={toggleTol}
        />
      </DescriptionTooltip>
    </>
  );
}