import {TempFlags} from "types/traffic";
import { Number2 } from "cs2/ui";

export const TempFlagsStr: Record<number, string> = {
  0: "0",
  1: "Create",
  2: "Delete",
  4: "IsLast",
  8: "Essential",
  16: "Dragging",
  32: "Select",
  64: "Modify",
  128: "Regenerate",
  256: "Replace",
  512: "Upgrade",
  1024: "Hidden",
  2048: "Parent",
  4096: "Combine",
  8192: "RemoveCost",
  16384: "Optional",
  32768: "Cancel",
  65536: "SubDetail",
}

export const tempFlagsToString = (value: TempFlags) => {
  const flags: string[] = [];
  const stringsCount = Object.keys(TempFlagsStr).length;
  for(let i = 0; i < stringsCount && 2 ** i < value; i++) {
    if (2 ** i & value) {
      const strFlag: string | undefined = TempFlagsStr[2 ** i];
      flags.push(strFlag)
    }
  }
  return flags.length == 0 ? TempFlagsStr[0] : flags.join(", ");
}

export const fitScreen = (position: Number2): Number2 => {
  return {
    x: position.x < 0 ? 0 : position.x > 1 ? 0.95 : position.x,
    y: position.y < 0 ? 0 : position.y > 1 ? 0.95 : position.y,
  };
}

export const simpleBoundingRectComparer = (a?: DOMRect, b?: DOMRect) => a?.x === b?.x && a?.y === b?.y;