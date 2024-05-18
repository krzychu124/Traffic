import { Button } from "cs2/ui";
import { useLocalization } from "cs2/l10n";
import { useCallback, useMemo, CSSProperties } from "react";
import classNames from "classnames";
import { UIBindingConstants, ModTool, ModKeyBinds } from "types/traffic";
import { useValue, trigger } from "cs2/api";
import { tool, Number2 } from "cs2/bindings";
import styles from "./toolSelectionPanel.module.scss";
import mod from "mod.json";
import { DescriptionTooltipWithKeyBind } from "modUI/descriptionTooltipWithKeyBind/descriptionTooltipWithKeyBind";
import { modKeyBindings$ } from "bindings";

interface Props {
  anchor: Number2 | null;
}

export const ToolSelectionPanel = ({anchor}: Props) => {
  const {translate} = useLocalization();
  const selectedTool = useValue(tool.activeTool$);
  const keyBindings = useValue<ModKeyBinds>(modKeyBindings$);

  const onLaneConnectorTool = useCallback(() => {
    trigger(mod.id, UIBindingConstants.TOGGLE_TOOL, ModTool.LaneConnector);
  }, []);
  const onPrioritiesTool = useCallback(() => {
    trigger(mod.id, UIBindingConstants.TOGGLE_TOOL, ModTool.Priorities);
  }, []);

  const panelPosition: Partial<CSSProperties> = useMemo(() => ({
    top: `${anchor?.y ||0}rem`,
    left: `${anchor?.x ||0}rem`,
  }), [anchor])

  return (
    <div className={styles.panel} style={panelPosition}>
      <DescriptionTooltipWithKeyBind title={"Lane Connector Tool"}
                                     description={"Manage lane connection at selected intersection"}
                                     keyBind={keyBindings.laneConnectorTool}
      >
        <Button variant="flat"
                className={classNames(styles.actionButton, {[styles.selected]: selectedTool.id === UIBindingConstants.LANE_CONNECTOR_TOOL})}
                onClick={onLaneConnectorTool}
                type="button"
        >
          {translate("-", "Lane Connections")}
        </Button>
      </DescriptionTooltipWithKeyBind>
      <DescriptionTooltipWithKeyBind title={"Priorities Tool"}
                                     description={"Manage lane priorities at selected intersection"}
                                     keyBind={keyBindings.prioritiesTool}
      >
        <Button variant="flat"
                className={classNames(styles.actionButton, {[styles.selected]: selectedTool.id === UIBindingConstants.PRIORITIES_TOOL})}
                onClick={onPrioritiesTool}
                type="button"
        >
          {translate("--", "Priorities")}
        </Button>
      </DescriptionTooltipWithKeyBind>
    </div>
  );
}