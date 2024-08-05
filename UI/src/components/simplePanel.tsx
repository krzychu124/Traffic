import { PropsWithChildren, ReactElement, CSSProperties } from "react";
import styles from './simplePanel.module.scss';
import classNames from "classnames";

interface Props {
  className?: string;
  style?: CSSProperties;
  header?: ReactElement;
}

export const SimplePanel = ({header, className, style, children}: PropsWithChildren<Props>) => {

  return (
    <div className={classNames(styles.simplePanel, className)} style={style}>
      <div className={styles.header}>
        {header}
      </div>
      <div className={styles.content}>{children}</div>
    </div>
  )
}