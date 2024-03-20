import React, { useMemo, useState, useCallback } from "react";
import classNames from "classnames";
import { FormattedParagraphs } from "cs2/ui";
import { DebugData } from "types/traffic";
import { tempFlagsToString } from "types/internal";
import { toString } from "helpers/toString";
import { useValue } from "cs2/api";
import { tool } from "cs2/bindings";
import styles from 'debugUi/shared/floatingText/floatingText.module.scss';

interface Props {
  data: DebugData;
}

export const FloatingText = ({ data }: Props) => {
  const [viewDetails, setViewDetails] = useState(false);
  const isActiveTool = useValue(tool.activeTool$);

  const positionStyle = useMemo(() => {
    return { left: (data.position2d.x.toFixed(0)) + 'rem', top: (data.position2d.y.toFixed(0)) + 'rem' };
  }, [data.position2d])
  const handleClick = useCallback(() => setViewDetails(d => !d), []);

  return (
    <div className={classNames(styles.popup, { [styles.disableHover]: isActiveTool.id !== tool.DEFAULT_TOOL })}
         style={positionStyle}
         onClick={handleClick}
    >
      <div className={classNames(styles.netPartTitle, styles.titleText, { [styles.isEdge]: data.isEdge })}>
        <span>{(data.isEdge) ? "Edge" : "Node"}:</span>&nbsp;
        <span>{toString(data.entity)}</span>
      </div>
      {(viewDetails || data.isTemp) && (
        <>
          {data.isTemp && (
            <>
              <div className={classNames(styles.dFlexRow, styles.temp, styles.subtitleText, { [styles.isEdge]: data.originalIsEdge })}>
                <span>Temp:</span>&nbsp;{toString(data.original)}
              </div>
              <div className={classNames(styles.dFlexRow, styles.flags, styles.text)}>
                <span>TempFlags:</span>&nbsp;{tempFlagsToString(data.flags)}

              </div>
            </>
          )}
          <FormattedParagraphs className={styles.text}>
            {data.value}
          </FormattedParagraphs>
        </>
      )}
    </div>
  );
}