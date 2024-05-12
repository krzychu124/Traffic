import { bindValue } from "cs2/api";
import mod from "../../mod.json";
import { DebugData, UIBindingConstants, SelectedIntersectionData } from "types/traffic";
import { Entity } from "cs2/bindings";

export const debugTexts$ = bindValue<DebugData[]>(mod.id, UIBindingConstants.DEBUG_TEXTS, []);
export const isDebugVisible$ = bindValue<boolean>(mod.id, UIBindingConstants.IS_DEBUG, false);
export const selectedIntersection$ = bindValue<SelectedIntersectionData | null>(mod.id, UIBindingConstants.SELECTED_INTERSECTION, null);
export const loadingErrorsPresent$ = bindValue<boolean>(mod.id, UIBindingConstants.LOADING_ERRORS_PRESENT, false);
export const resetIntersectionsData$ = bindValue<Entity[]>(mod.id, UIBindingConstants.ERROR_AFFECTED_INTERSECTIONS, []);