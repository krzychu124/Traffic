declare module "cs2/api" {
  import { MutableRefObject } from 'react';
  
  export interface ValueBinding<T> {
  	readonly value: T;
  	subscribe(listener?: BindingListener<T>): ValueSubscription<T>;
  	dispose(): void;
  }
  export interface MapBinding<K, V> {
  	getValue(key: K): V;
  	subscribe(key: K, listener?: BindingListener<V>): ValueSubscription<V>;
  	dispose(): void;
  }
  export interface EventBinding<T> {
  	subscribe(listener: BindingListener<T>): Subscription;
  	dispose(): void;
  }
  export interface BindingListener<T> {
  	(value: T): void;
  }
  export interface Subscription {
  	dispose(): void;
  }
  export interface ValueSubscription<T> extends Subscription {
  	readonly value: T;
  	setChangeListener(listener: BindingListener<T>): void;
  }
  export export function bindValue<T>(group: string, name: string, fallbackValue?: T): ValueBinding<T>;
  export export function bindLocalValue<T>(initialValue: T): LocalValueBinding<T>;
  export export function bindMap<K, V>(group: string, name: string, keyStringifier?: (key: K) => string): MapBinding<K, V>;
  export export function bindEvent<T>(group: string, name: string): EventBinding<T>;
  export export function bindTrigger(group: string, name: string): () => void;
  export export function bindTriggerWithArgs<T extends any[] = [
  ]>(group: string, name: string): T extends [
  ] ? unknown : (...args: T) => void;
  export export function trigger(group: string, name: string, ...args: any[]): void;
  export export function call<T>(group: string, name: string, ...args: any[]): Promise<T>;
  /** Subscribe to a ValueBinding. Return fallback value or throw an error if the binding is not registered on the C# side */
  export export function useValue<V>(binding: ValueBinding<V>): V;
  export export function useReducedValue<T, V>(binding: ValueBinding<V>, reducer: (current: T, next: V) => T, initial: T): T;
  export export function useValueRef<V>(binding: ValueBinding<V>): MutableRefObject<V>;
  export export function useValueOnChange<V>(binding: ValueBinding<V>, onChange: (value: V, prevValue: V) => void, depth?: number): V;
  export export function useMapValueOnChange<K, V>(binding: MapBinding<K, V>, key: K, onChange: (newValue: V) => void): V;
  /** Subscribe to a MapBinding value. throw an error if the binding is not registered on the C# side */
  export export function useMapValue<K, V>(binding: MapBinding<K, V>, key: undefined): undefined;
  export export function useMapValue<K, V>(binding: MapBinding<K, V>, key: K): V;
  export export function useMapValues<K, V>(binding: MapBinding<K, V>, keys: K[]): V[];
  export class LocalValueBinding<T> implements ValueBinding<T> {
  	readonly listeners: ListenerRef<T>[];
  	disposed: boolean;
  	_value: T;
  	constructor(initialValue: T);
  	get registered(): boolean;
  	get value(): T;
  	subscribe: (listener?: BindingListener<T>) => {
  		readonly value: T;
  		setChangeListener: (listener: BindingListener<T>) => void;
  		dispose(): void;
  	};
  	dispose: () => void;
  	update: (newValue: T) => void;
  }
  export class ListenerRef<T> {
  	listener: BindingListener<T> | undefined;
  	constructor(listener: BindingListener<T> | undefined);
  	set: (listener: BindingListener<T>) => void;
  	call: (newValue: T) => void;
  }
  // https://coherent-labs.com/Documentation/cpp-gameface/d1/dea/shape_morphing.html
  // https://coherent-labs.com/Documentation/cpp-gameface/d4/d08/interface_morph_animation.html
  export export interface HTMLImageElement {
  	getSrcSVGAnimation(): MorphAnimation | null;
  }
  export export interface Element {
  	getMaskSVGAnimation(): MorphAnimation | null;
  }
  export export interface MorphAnimation {
  	currentTime: number;
  	playbackRate: number;
  	play(): void;
  	pause(): void;
  	reverse(): void;
  	playFromTo(playTime: number, pauseTime: number, callback?: () => void): void;
  }
  
  export {};
  
}