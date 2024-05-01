import { useEffect, MutableRefObject } from "react";
import { useMemoizedValue, useRem } from "cs2/utils";
import { Number2 } from "cs2/ui";
import { fitScreen, simpleBoundingRectComparer } from "types/internal";

interface Props {
  panel: MutableRefObject<HTMLDivElement | null>;
  onPositionChanged: (value: Number2) => void;
}

export const useUpdatePanelPosition = ({ panel, onPositionChanged }: Props) => {
  const currentRect = useMemoizedValue<DOMRect | undefined>(panel.current?.getBoundingClientRect(), simpleBoundingRectComparer);
  const rem = useRem();

  useEffect(() => {
    if (currentRect && currentRect?.x > 0 && currentRect?.y > 0) {
      const rect: DOMRect = currentRect;
      const newPos = { x: rect.x * rem / 1920, y: rect.y * rem / 1080 };
      onPositionChanged(fitScreen(newPos));
    }
  }, [currentRect, rem, onPositionChanged]);
}