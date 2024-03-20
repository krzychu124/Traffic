import { bindValue } from "cs2/api";
import { DebugData } from "types/traffic";
import mod from "../../mod.json";

export const debugTexts$ = bindValue<DebugData[]>(mod.id, 'debugTexts', []);
