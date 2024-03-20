import {Entity} from "cs2/utils";

export const toString = (entity?: Entity) => entity && entity.index > 0 ? `Entity(${entity.index}:${entity.version})` : 'Entity.null'