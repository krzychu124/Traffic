import React, { useState, forwardRef, useImperativeHandle, useCallback, useMemo } from "react";
import { Button, Panel } from "cs2/ui";
import { IntersectionDataReset } from "modUI/troubleshooting/intersectionDataReset/intersectionDataReset";
import { trigger, useValue } from "cs2/api";
import { resetIntersectionsData$, selectedIntersection$ } from "bindings";
import { UIBindingConstants } from "types/traffic";
import mod from "mod.json";
import styles from "./dataLoadingProblemModal.module.scss";

interface RefProps {
  toggleModal?: () => void;
}

interface Props {
  initState: boolean;
}


export const DataLoadingProblemModal = forwardRef<RefProps, Props>(({ initState }, ref) => {
  const [isOpen, setIsOpen] = useState(initState);
  const selected = useValue(selectedIntersection$);
  const affectedIntersections = useValue(resetIntersectionsData$);

  const isIntersectionSelected = useMemo(() => (selected?.entity.index || 0) > 0, [selected])
  const handleRemoveAll = useCallback(() => {
    setIsOpen(false);
    trigger(mod.id, UIBindingConstants.REMOVE_ENTITY_FROM_LIST, -1);
  }, []);

  const header = useMemo(() => (<span className={styles.title}>Traffic Data Load Issues</span>), []);

  useImperativeHandle(ref, () => {
    return {
      toggleModal() {
        setIsOpen(true);
      }
    }
  }, []);

  return (
    <>
      {isOpen && !isIntersectionSelected && !!affectedIntersections.length && (
        <>
          <Panel className={styles.modalContainer}
                 header={header}
          >
            <div className={styles.infoSection}>
              <div className={styles.infoMain}>
                <p cohinline="cohinline">The mod intersection settings has not been loaded correctly at the following
                  <b className={styles.value}> {affectedIntersections.length} </b>nodes:
                </p>
              </div>
              <div className={styles.info}>
                <span>Click on the ID to navigate to the affected node</span>
                <span><b>X</b>&nbsp;- remove from list</span>
                <span><b>Ignore All</b>&nbsp;- clear the list and ignore errors</span>
                <span><b>Roundabouts</b>&nbsp;can be ignored, they are not supported yet</span>
              </div>
            </div>

            <IntersectionDataReset affectedIntersections={affectedIntersections} />

            <div className={styles.actions}>
              <Button variant="flat" className={styles.actionButton} onClick={handleRemoveAll}>
                Ignore All
              </Button>
              <Button variant="flat" className={styles.actionButton} onClick={() => setIsOpen(false)}>
                Minimize
              </Button>
            </div>
          </Panel>
        </>
      )}
    </>
  );
});