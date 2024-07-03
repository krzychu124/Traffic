import React, { useCallback, useRef } from "react";
import classNames from "classnames";
import { Button} from "cs2/ui";
import { tool } from "cs2/bindings";
import { trigger, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { LaneConnectorTool } from "modUI/laneConnectorTool/laneConnectorTool";
import { UIBindingConstants, UIKeys, ModKeyBinds } from "types/traffic";
import mod from "mod.json";
import styles from "modUI/modUI.module.scss";
import trafficIcon from "images/traffic_icon.svg";
import { loadingErrorsPresent$, modKeyBindings$ } from "bindings";
import { DataLoadingProblemModal } from "modUI/troubleshooting/dataLoadingProblemModal";
import { DescriptionTooltipWithKeyBind } from "modUI/descriptionTooltipWithKeyBind/descriptionTooltipWithKeyBind";

export const ModUI = () => {
  // const [position, setPosition] = useState<Number2>({ x: 0.025, y: 0.8 })
  const loadingProblemsRef = useRef<any>(null);
  const selectedTool = useValue(tool.activeTool$);
  const loadingErrorsPresent = useValue(loadingErrorsPresent$);
  const {translate} = useLocalization();
  const keyBindings = useValue<ModKeyBinds>(modKeyBindings$);

  const toggleTol = useCallback(() => trigger(mod.id, UIBindingConstants.TOGGLE_TOOL, selectedTool.id !== UIBindingConstants.LANE_CONNECTOR_TOOL), [selectedTool.id]);

  return (
    <>
      {selectedTool.id === UIBindingConstants.LANE_CONNECTOR_TOOL && (
        <LaneConnectorTool showLoadingErrorsButton={loadingErrorsPresent} onOpenLoadingResults={() => loadingProblemsRef.current?.toggleModal()}/>
      )}
      {loadingErrorsPresent && (
        <DataLoadingProblemModal initState={selectedTool.id === UIBindingConstants.LANE_CONNECTOR_TOOL} ref={loadingProblemsRef} />
      )}
      <DescriptionTooltipWithKeyBind title="Traffic"
                                     description={translate(UIKeys.TRAFFIC_MOD)}
                                     keyBind={keyBindings?.laneConnectorTool}
      >
        <Button
          src={trafficIcon}
          variant="floating"
          className={classNames({ [styles.selected]: selectedTool.id === UIBindingConstants.LANE_CONNECTOR_TOOL }, styles.toggle)}
          onSelect={toggleTol}
        />
      </DescriptionTooltipWithKeyBind>
    </>
  );
}