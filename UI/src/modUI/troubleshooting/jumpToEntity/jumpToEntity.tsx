import { PropsWithChildren } from "react";
import { Entity } from "cs2/bindings";

interface Props {
  className?: string;
  entity: Entity;
  navigate: (entity: Entity) => void;
}

export const JumpToEntity = ({entity, className, children, navigate}: PropsWithChildren<Props>) => {

  return (
    <div className={className}>
      <p  onClick={() => navigate(entity)}>{children}</p>
    </div>
  )
}