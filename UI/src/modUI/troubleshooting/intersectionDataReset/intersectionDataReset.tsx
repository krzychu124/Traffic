import { Scrollable, Button, Icon } from "cs2/ui";
import { Entity } from "cs2/bindings";
import { useCallback } from "react";
import { trigger } from "cs2/api";
import mod from "mod.json";
import { JumpToEntity } from "modUI/troubleshooting/jumpToEntity/jumpToEntity";
import { UIBindingConstants } from "types/traffic";
import styles from "./intersectionDataReset.module.scss";

interface Props {
  affectedIntersections: Entity[];
}

export const IntersectionDataReset = ({affectedIntersections}: Props) => {

  const test = useCallback((e: Entity) => {trigger("camera", "focusEntity", e)}, []);
  const onNavigate = useCallback((e: Entity) => {
    trigger(mod.id, UIBindingConstants.NAVIGATE_TO_ENTITY, e);
  }, []);
  const onRemove = useCallback((index: number) => {
    trigger(mod.id, UIBindingConstants.REMOVE_ENTITY_FROM_LIST, index);
  }, []);

  return (
    <Scrollable className={styles.scrollable} >
      {affectedIntersections.map((e, index) => (
        <div className={styles.row} key={"intersection-" + index}>
          <JumpToEntity className={styles.name} entity={e} navigate={onNavigate}>
            <div className={styles.text}>
              <p >Node:&nbsp;<b className={styles.entity}>{e.index}</b></p>
            </div>
          </JumpToEntity>
          <Button variant="icon"
                  color="white"
                  className={styles.button}
                  onClick={() => onRemove(index)}
          >
            <Icon tinted  className={styles.closeIcon} src={'Media/Glyphs/Close.svg'} />
          </Button>
        </div>
      ))}
    </Scrollable>
  );
}