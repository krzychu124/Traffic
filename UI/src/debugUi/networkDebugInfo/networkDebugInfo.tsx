import React, { useMemo } from "react";
import { useValue } from "cs2/api";
import { FloatingText } from "debugUi/shared/floatingText/floatingText";
import { DebugData } from "types/traffic";
import { debugTexts$ } from "bindings";
import styles from './networkDebugInfo.module.scss';
import classNames from "classnames";

const MAX = 200;
const debugData: DebugData[] = [];
interface Props {
  inEditor?: boolean;
}

export const NetworkDebugInfo = ({inEditor}: Props) => {
  const values = useValue(debugTexts$);

  const filteredData = useMemo(() => {
    debugData.length = 0;
    const max = (values.length < MAX ? values.length : MAX);
    for (let i = 0; i < max; i++) {
      debugData.push(values[i])
    }
    return debugData;
  }, [values]);

  return (
    <>
      <div className={classNames(styles.info, {[styles.inEditor]: inEditor})}>
        <span>Visible nodes: {filteredData.length}</span>
      </div>
      <div>{filteredData.map((data) => <FloatingText key={`item-${data.entity.index}`} data={data} />)}</div>
    </>
  );
}