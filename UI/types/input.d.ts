declare module "cs2/input" {
  import React$1 from 'react';
  import { CSSProperties, PropsWithChildren, ReactNode } from 'react';
  
  export export class FocusSymbol {
  	readonly debugName: string;
  	readonly r: number;
  	constructor(debugName: string);
  	toString(): string;
  }
  /**
   * Special focus key that disables the focus of the component.
   */
  export export const FOCUS_DISABLED: FocusSymbol;
  /**
   * Special focus key that assigns an internally generated, unique focus key to the component.
   *
   * This is useful if the component is inside of a `NavigationScope` and there is no need to manually control focus,
   * or the focus key is defined by a higher level `FocusKeyOverride` component.
   */
  export export const FOCUS_AUTO: FocusSymbol;
  export type FocusKey = typeof FOCUS_DISABLED | typeof FOCUS_AUTO | UniqueFocusKey;
  export type UniqueFocusKey = FocusSymbol | string | number;
  export export function useUniqueFocusKey(focusKey: FocusKey, debugName: string): UniqueFocusKey | null;
  export interface FocusController {
  	isChildFocused(focusKey: UniqueFocusKey): boolean;
  	registerChild(focusKey: UniqueFocusKey, element: FocusController): void;
  	unregisterChild(focusKey: UniqueFocusKey): void;
  	attachCallback(callback: FocusCallback): void;
  	detachCallback(callback: FocusCallback): void;
  	attachTo(controller: FocusController): void;
  	detach(): void;
  	getBounds(): FocusDOMRect | null;
  	getFocusedBounds(): FocusDOMRect | null;
  	debugTrace(): string;
  	deepDebugTrace(): string;
  }
  export interface FocusDOMRect {
  	left: number;
  	top: number;
  	right: number;
  	bottom: number;
  	width: number;
  	height: number;
  }
  export interface FocusCallback {
  	(selfFocused: boolean, currentFocus: FocusController | null): void;
  }
  export export enum FocusActivation {
  	Always = "always",
  	AnyChildren = "anyChildren",
  	FocusedChild = "focusedChild"
  }
  export enum FocusLimits {
  	Center = "center",
  	Bounds = "bounds"
  }
  export export const FocusContext: import("react").Context<FocusController>;
  export export const disabledFocusController: FocusController;
  export export abstract class FocusControllerBase implements FocusController {
  	private readonly propagateCurrent;
  	private _parentController;
  	private _enabled;
  	private _focusKey;
  	private _lastFocused;
  	private readonly callbacks;
  	constructor(propagateCurrent?: boolean);
  	isChildFocused: (focusKey: UniqueFocusKey) => boolean;
  	get focused(): boolean;
  	protected abstract isChildFocusedImpl(focusKey: UniqueFocusKey): boolean;
  	abstract registerChild(focusKey: UniqueFocusKey, element: FocusController): void;
  	abstract unregisterChild(focusKey: UniqueFocusKey): void;
  	attachCallback: (callback: FocusCallback) => void;
  	detachCallback: (callback: FocusCallback) => void;
  	abstract getBounds(): FocusDOMRect | null;
  	abstract getFocusedBounds(): FocusDOMRect | null;
  	attachTo(controller: FocusController): void;
  	detach(): void;
  	protected get enabled(): boolean;
  	protected set enabled(value: boolean);
  	protected get focusKey(): UniqueFocusKey | null;
  	protected set focusKey(focusKey: UniqueFocusKey | null);
  	protected updateChildren: (currentFocus: FocusController | null) => void;
  	protected onFocusUpdate: FocusCallback;
  	protected onFocusEnterImpl(_: FocusController | null): void;
  	private _tryAttach;
  	private _tryDetach;
  	debugTrace(): string;
  	deepDebugTrace(): string;
  	protected get debugName(): string;
  	protected abstract get debugFocusedChild(): FocusController | null;
  }
  export export function useElementFocusController(focusKey: UniqueFocusKey | null, elementRef: React$1.RefObject<HTMLElement | SVGElement | null>, activation?: FocusActivation, allowChildren?: boolean): ElementFocusController;
  export interface BoundsCallback {
  	(): DOMRect | null;
  }
  export class ElementFocusController extends FocusControllerBase {
  	readonly getBounds: BoundsCallback;
  	private readonly activation;
  	private readonly allowChildren;
  	private childFocusKey;
  	private childElement;
  	constructor(focusKey: UniqueFocusKey | null, getBounds: BoundsCallback, activation: FocusActivation, allowChildren: boolean);
  	isChildFocusedImpl: (childFocusKey: UniqueFocusKey) => boolean;
  	registerChild: (childFocusKey: UniqueFocusKey, element: FocusController) => void;
  	unregisterChild: (childFocusKey: UniqueFocusKey) => void;
  	getFocusedBounds(): FocusDOMRect | DOMRect | null;
  	protected get debugFocusedChild(): FocusController | null;
  }
  export export function useKeyFocusController(focusKey: UniqueFocusKey | null): KeyFocusController;
  export class KeyFocusController extends FocusControllerBase {
  	private childFocusKey;
  	private childElement;
  	constructor(focusKey: UniqueFocusKey | null);
  	isChildFocusedImpl: (childFocusKey: UniqueFocusKey) => boolean;
  	registerChild: (focusKey: UniqueFocusKey, element: FocusController) => void;
  	unregisterChild: (focusKey: UniqueFocusKey) => void;
  	getBounds: () => FocusDOMRect | null;
  	getFocusedBounds(): FocusDOMRect | null;
  	protected get debugFocusedChild(): FocusController | null;
  }
  export export function useMultiChildFocusController(focusKey: UniqueFocusKey | null, activation: FocusActivation, limits: FocusLimits): MultiChildFocusController;
  export interface RefocusCallback {
  	(previousElement: FocusController | null): void;
  }
  export export class MultiChildFocusController extends FocusControllerBase {
  	readonly activation: FocusActivation;
  	readonly limits: FocusLimits;
  	readonly children: Map<UniqueFocusKey, FocusController>;
  	private _focusedChildKey;
  	onRefocus: RefocusCallback | null;
  	constructor(focusKey: UniqueFocusKey | null, activation: FocusActivation, limits: FocusLimits);
  	get focusedChildKey(): UniqueFocusKey | null;
  	set focusedChildKey(nextFocusedChildKey: UniqueFocusKey | null);
  	has(focusKey: UniqueFocusKey): boolean;
  	get(focusKey: UniqueFocusKey): FocusController | undefined;
  	entries(): Iterable<[
  		UniqueFocusKey,
  		FocusController
  	]>;
  	isChildFocusedImpl: (focusKey: UniqueFocusKey) => boolean;
  	registerChild: (focusKey: UniqueFocusKey, element: FocusController) => void;
  	unregisterChild: (focusKey: UniqueFocusKey) => void;
  	getBounds(): {
  		left: number;
  		top: number;
  		right: number;
  		bottom: number;
  		width: number;
  		height: number;
  	} | null;
  	getFocusedBounds(): FocusDOMRect | null;
  	onFocusEnterImpl(previousElement: FocusController | null): void;
  	protected get debugFocusedChild(): FocusController | null;
  }
  export export function usePassThroughFocusController(debugName: string, enabled?: boolean, childFocused?: boolean): PassThroughFocusController;
  export class PassThroughFocusController extends FocusControllerBase {
  	private childElement;
  	private _childFocused;
  	get enabled(): boolean;
  	set enabled(value: boolean);
  	get childFocused(): boolean;
  	set childFocused(value: boolean);
  	private _debugName;
  	get debugName(): string;
  	set debugName(value: string);
  	isChildFocusedImpl: (focusKey: UniqueFocusKey) => boolean;
  	registerChild: (focusKey: UniqueFocusKey, element: FocusController) => void;
  	unregisterChild: (focusKey: UniqueFocusKey) => void;
  	getBounds: () => FocusDOMRect | null;
  	getFocusedBounds(): FocusDOMRect | null;
  	protected get debugFocusedChild(): FocusController | null;
  }
  export export function useRootFocusController(): RootFocusController;
  export class RootFocusController extends FocusControllerBase {
  	private childElement;
  	constructor();
  	get debugName(): string;
  	isChildFocusedImpl: (focusKey: UniqueFocusKey) => boolean;
  	registerChild: (focusKey: UniqueFocusKey, element: FocusController) => void;
  	unregisterChild: (focusKey: UniqueFocusKey) => void;
  	getBounds: () => null;
  	getFocusedBounds(): null;
  	protected get debugFocusedChild(): FocusController | null;
  }
  export interface Number2 {
  	readonly x: number;
  	readonly y: number;
  }
  export export enum NavigationDirection {
  	Horizontal = "horizontal",
  	Vertical = "vertical",
  	Both = "both",
  	None = "none"
  }
  export export function transformNavigationInput(value: Number2, dir: NavigationDirection): Number2;
  export export function getClosestKey(controller: MultiChildFocusController, pos: Number2, anchor: Number2): UniqueFocusKey | null;
  export export function getClosestKeyInDirection(controller: MultiChildFocusController, focusedElement: FocusController, dir: Number2, anchor: Number2, ignoreKey?: UniqueFocusKey): UniqueFocusKey | null;
  export export const focusAnchorCenter: Number2;
  export export const focusAnchorTop: Number2;
  export export const focusAnchorLeft: Number2;
  export export const focusAnchorBottom: Number2;
  export export const focusAnchorRight: Number2;
  export export function getElementFocusPosition(rect: FocusDOMRect | null, anchor: Number2): Number2 | null;
  export interface NavigationScopeProps {
  	focusKey?: FocusKey;
  	debugName?: string;
  	focused: UniqueFocusKey | null;
  	direction?: NavigationDirection;
  	activation?: FocusActivation;
  	limits?: FocusLimits;
  	onFocused?: (focused: boolean) => void;
  	onChange: (key: UniqueFocusKey | null) => void;
  	onRefocus?: RefocusHandler;
  	allowFocusExit?: boolean;
  	allowLooping?: boolean | "x" | "y";
  	jumpSections?: boolean;
  }
  /**
   * A stateless component that allows the user to navigate between multiple focusable children with a gamepad.
   *
   * The `onRefocus` callback controls what happens when the focus is lost within the scope.
   *
   * The focus behavior of the scope can be controlled by the `activation` prop.
   *
   * Optionally, a `focusKey` for the component itself can be set.
   */
  export export const NavigationScope: ({ focusKey, debugName, focused, direction, activation, limits, children, onFocused, onChange, onRefocus, allowFocusExit, allowLooping, jumpSections, }: React$1.PropsWithChildren<NavigationScopeProps>) => JSX.Element;
  export type RefocusHandler = (focusController: MultiChildFocusController, lastElement: FocusController | null) => UniqueFocusKey | null;
  export export const refocusClosestKeyIfNoFocus: RefocusHandler;
  export export const refocusClosestKey: RefocusHandler;
  export interface AutoNavigationScopeProps {
  	focusKey?: FocusKey;
  	initialFocused?: UniqueFocusKey | null;
  	direction?: NavigationDirection;
  	activation?: FocusActivation;
  	limits?: FocusLimits;
  	onRefocus?: RefocusHandler;
  	onChange?: (key: UniqueFocusKey | null) => void;
  	onFocused?: (focused: boolean) => void;
  	allowFocusExit?: boolean;
  	forceFocus?: UniqueFocusKey | null;
  	debugName?: string;
  	allowLooping?: boolean | "x" | "y";
  	jumpSections?: boolean;
  }
  /**
   * Automatic navigation in lists, grids and forms.
   */
  export export const AutoNavigationScope: ({ onRefocus, onChange, allowFocusExit, allowLooping, debugName, focusKey, forceFocus, initialFocused, ...props }: React$1.PropsWithChildren<AutoNavigationScopeProps>) => JSX.Element;
  export interface FocusBoundaryProps {
  	disabled?: boolean;
  	onFocusChange?: FocusCallback;
  }
  /**
   * A passive component that allows you to add or remove the focusable child from the focus tree.
   *
   * It can contain zero or one focusable children.
   *
   * The component itself cannot be focused, it purely acts as a wrapper around a focusable child.
   */
  export export const FocusBoundary: ({ disabled, children, onFocusChange }: React$1.PropsWithChildren<FocusBoundaryProps>) => JSX.Element;
  export interface Entity {
  	index: number;
  	version: number;
  }
  export type FocusChangeListener = (key: UniqueFocusKey | null) => void;
  export type ChangeCallback<T> = (id: T) => void;
  export type KeyParser<T> = (key: UniqueFocusKey) => T | null | undefined;
  export export function useFocusChangeListener<T>(parser: KeyParser<T>, onChange: ChangeCallback<T>): FocusChangeListener;
  export export function useEntityFocusChangeListener(onChange: ChangeCallback<Entity>): FocusChangeListener;
  export export function useStringFocusChangeListener(onChange: ChangeCallback<string>): FocusChangeListener;
  export interface FocusDisabledProps {
  	disabled?: boolean;
  }
  export export const FocusDisabled: ({ disabled, children }: React$1.PropsWithChildren<FocusDisabledProps>) => JSX.Element;
  export enum UISound {
  	selectItem = "select-item",
  	dragSlider = "drag-slider",
  	hoverItem = "hover-item",
  	expandPanel = "expand-panel",
  	grabSlider = "grabSlider",
  	selectDropdown = "select-dropdown",
  	selectToggle = "select-toggle",
  	focusInputField = "focus-input-field",
  	signatureBuildingEvent = "signature-building-event",
  	bulldoze = "bulldoze",
  	bulldozeEnd = "bulldoze-end",
  	relocateBuilding = "relocate-building",
  	mapTilePurchaseMode = "map-tile-purchase-mode",
  	mapTilePurchaseModeEnd = "map-tile-purchase-mode-end",
  	xpEvent = "xp-event",
  	milestoneEvent = "milestone-event",
  	economy = "economy",
  	chirpEvent = "chirp-event",
  	likeChirp = "like-chirp",
  	chirper = "chirper",
  	purchase = "purchase",
  	enableBuilding = "enable-building",
  	disableBuilding = "disable-building",
  	pauseSimulation = "pause-simulation",
  	resumeSimulation = "resume-simulation",
  	simulationSpeed1 = "simulation-speed-1",
  	simulationSpeed2 = "simulation-speed-2",
  	simulationSpeed3 = "simulation-speed-3",
  	togglePolicy = "toggle-policy",
  	takeLoan = "take-loan",
  	removeItem = "remove-item",
  	toggleInfoMode = "toggle-info-mode",
  	takePhoto = "take-photo",
  	tutorialTriggerCompleteEvent = "tutorial-trigger-complete-event",
  	selectRadioNetwork = "select-radio-network",
  	selectRadioStation = "select-radio-station",
  	generateRandomName = "generate-random-name",
  	decreaseElevation = "decrease-elevation",
  	increaseElevation = "increase-elevation",
  	selectPreviousItem = "select-previous-item",
  	selectNextItem = "select-next-item",
  	openPanel = "open-panel",
  	closePanel = "close-panel",
  	openMenu = "open-menu",
  	closeMenu = "close-menu",
  	clickDisableButton = "click-disable-button"
  }
  export interface PassiveFocusDivProps extends React$1.HTMLAttributes<HTMLDivElement> {
  	onFocusChange?: (focused: boolean) => void;
  	focusSound?: UISound | string | null;
  }
  /**
   * A passive div element that has a `focused` class name when the focusable child inside of it is focused.
   *
   * It can contain zero or one focusable children.
   *
   * The component itself cannot be focused, it purely acts as a wrapper around a focusable child.
   * That means if there is no focusable child inside of it, it can never receive the `focused` class name.
   */
  export export const PassiveFocusDiv: (props: PassiveFocusDivProps & React$1.RefAttributes<HTMLDivElement>) => React$1.ReactElement<any, string | React$1.JSXElementConstructor<any>> | null;
  export interface ActiveFocusDivProps extends PassiveFocusDivProps {
  	focusKey?: FocusKey;
  	debugName?: string;
  	activation?: FocusActivation;
  }
  /**
   * A focusable div element. It has a `focused` class name while it is focused.
   *
   * It can contain zero or one focusable children.
   *
   * Unlike the `PassiveFocusDiv`, the element itself can be focused, even if there are no elements inside of it.
   */
  export export const ActiveFocusDiv: (props: ActiveFocusDivProps & React$1.RefAttributes<HTMLDivElement>) => React$1.ReactElement<any, string | React$1.JSXElementConstructor<any>> | null;
  export export function useFocused(focusController: FocusController): boolean;
  export export function useFocusedRef(focusController: FocusController): React$1.RefObject<boolean>;
  export export function useFocusCallback(focusController: FocusController, callback: FocusCallback | null | undefined): void;
  export interface FocusKeyOverrideProps {
  	focusKey: FocusKey | undefined;
  }
  /**
   * A passive component that overrides the focusKey of the child (so it will be registered to the parent using the `focusKey` prop)
   *
   * It can contain zero or one focusable children.
   *
   * The component itself cannot be focused, it purely acts as a wrapper around a focusable child.
   */
  export export const FocusKeyOverride: ({ focusKey, children }: React$1.PropsWithChildren<FocusKeyOverrideProps>) => JSX.Element;
  export interface FocusNodeProps {
  	controller: FocusController;
  }
  export export const FocusNode: ({ controller, children }: React$1.PropsWithChildren<FocusNodeProps>) => JSX.Element;
  export export const FocusRoot: ({ children }: React$1.PropsWithChildren) => JSX.Element;
  export interface FocusScopeProps {
  	focusKey?: FocusKey;
  	debugName?: string;
  	focused: UniqueFocusKey | null;
  	activation?: FocusActivation;
  	limits?: FocusLimits;
  }
  /**
   * A stateless component that allows you to control which child inside of it is focused.
   *
   * It can contain multiple focusable children.
   *
   * The component will only be focused while a child inside of it is focused
   * (if the `focusedChildKey` is null or there is no child with that key, it will unregister itself from the parent).
   *
   * Optionally, a `focusKey` for the component itself can be set.
   */
  export export const FocusScope: ({ focusKey, debugName, focused, activation, limits, children }: React$1.PropsWithChildren<FocusScopeProps>) => JSX.Element;
  export interface SelectableFocusBoundaryProps {
  	onSelectedStateChanged?: (selected: boolean) => void;
  }
  export export const SelectableFocusBoundary: ({ onSelectedStateChanged, children }: React$1.PropsWithChildren<SelectableFocusBoundaryProps>) => JSX.Element;
  export type Action = () => void | boolean;
  export type Action1D = (value: number) => void | boolean;
  export type Action2D = (value: Number2) => void | boolean;
  export interface InputActionsDefinition {
  	"Move Horizontal": Action1D;
  	"Change Slider Value": Action1D;
  	"Change Tool Option": Action1D;
  	"Change Value": Action1D;
  	"Change Line Schedule": Action1D;
  	"Select Popup Button": Action1D;
  	"Move Vertical": Action1D;
  	"Switch Radio Station": Action1D;
  	"Scroll Vertical": Action1D;
  	"Scroll Assets": Action1D;
  	"Select": Action;
  	"Purchase Dev Tree Node": Action;
  	"Select Chirp Sender": Action;
  	"Save Game": Action;
  	"Overwrite Save": Action;
  	"Confirm": Action;
  	"Expand Group": Action;
  	"Collapse Group": Action;
  	"Select Route": Action;
  	"Remove Operating District": Action;
  	"Upgrades Menu": Action;
  	"Upgrades Menu Secondary": Action;
  	"Purchase Map Tile": Action;
  	"Unfollow Citizen": Action;
  	"Like Chirp": Action;
  	"Unlike Chirp": Action;
  	"Enable Info Mode": Action;
  	"Disable Info Mode": Action;
  	"Toggle Tool Color Picker": Action;
  	"Cinematic Mode": Action;
  	"Photo Mode": Action;
  	"Focus Citizen": Action;
  	"Unfocus Citizen": Action;
  	"Focus Line Panel": Action;
  	"Focus Occupants Panel": Action;
  	"Focus Info Panel": Action;
  	"Quaternary Action": Action;
  	"Close": Action;
  	"Back": Action;
  	"Leave Underground Mode": Action;
  	"Leave Info View": Action;
  	"Leave Map Tile View": Action;
  	"Jump Section": Action1D;
  	"Switch Tab": Action1D;
  	"Switch Option Section": Action1D;
  	"Switch DLC": Action1D;
  	"Switch Ordering": Action1D;
  	"Switch Radio Network": Action1D;
  	"Change Time Scale": Action1D;
  	"Switch Page": Action1D;
  	"Default Tool": Action;
  	"Default Tool UI": Action;
  	"Tool Options": Action;
  	"Switch Toolmode": Action;
  	"Toggle Snapping": Action;
  	"Toggle Contour Lines": Action;
  	"Capture Keyframe": Action;
  	"Reset Property": Action;
  	"Toggle Property": Action;
  	"Previous Tutorial Phase": Action;
  	"Continue Tutorial": Action;
  	"Finish Tutorial": Action;
  	"Close Tutorial": Action;
  	"Focus Tutorial List": Action;
  	"Start Next Tutorial": Action;
  	"Pause Simulation": Action;
  	"Resume Simulation": Action;
  	"Switch Speed": Action;
  	"Speed 1": Action;
  	"Speed 2": Action;
  	"Speed 3": Action;
  	"Bulldozer": Action;
  	"Exit Underground Mode": Action;
  	"Enter Underground Mode": Action;
  	"Increase Elevation": Action;
  	"Decrease Elevation": Action;
  	"Change Elevation": Action1D;
  	"Advisor": Action;
  	"Quicksave": Action;
  	"Quickload": Action;
  	"Focus Selected Object": Action;
  	"Hide UI": Action;
  	"Map Tile Purchase Panel": Action;
  	"Info View": Action;
  	"Progression Panel": Action;
  	"Economy Panel": Action;
  	"City Information Panel": Action;
  	"Statistic Panel": Action;
  	"Transportation Overview Panel": Action;
  	"Notification Panel": Action;
  	"Chirper Panel": Action;
  	"Lifepath Panel": Action;
  	"Event Journal Panel": Action;
  	"Radio Panel": Action;
  	"Photo Mode Panel": Action;
  	"Take Photo": Action;
  	"Relocate Selected Object": Action;
  	"Toggle Selected Object Active": Action;
  	"Delete Selected Object": Action;
  	"Toggle Selected Object Emptying": Action;
  	"Toggle Selected Lot Edit": Action;
  	"Toggle Follow Selected Citizen": Action;
  	"Toggle Traffic Routes": Action;
  	"Pause Menu": Action;
  	"Load Game": Action;
  	"Start Game": Action;
  	"Save Options": Action;
  	"Switch User": Action;
  	"Unset Binding": Action;
  	"Reset Binding": Action;
  	"Switch Savegame Location": Action1D;
  	"Show Advanced": Action;
  	"Hide Advanced": Action;
  	"Select Directory": Action;
  	"Search Options": Action;
  	"Clear Search": Action;
  	"Credit Speed": Action1D;
  	"Debug UI": Action;
  	"Debug Prefab Tool": Action;
  	"Debug Change Field": Action1D;
  	"Debug Multiplier": Action1D;
  }
  export type InputAction = keyof InputActionsDefinition;
  export type InputActionRequest = {
  	action: InputAction;
  	actionContext?: string;
  };
  export type InputActions = {
  	[K in InputAction]?: InputActionsDefinition[K] | {
  		actionContext?: string;
  		onAction: InputActionsDefinition[K] | null;
  	} | null;
  };
  export interface ButtonTheme {
  	button: string;
  	hint: string;
  }
  export interface ButtonSounds {
  	select?: UISound | string | null;
  	hover?: UISound | string | null;
  	focus?: UISound | string | null;
  }
  export interface ButtonProps extends React$1.ButtonHTMLAttributes<HTMLButtonElement | HTMLDivElement> {
  	focusKey?: FocusKey;
  	debugName?: string;
  	selected?: boolean;
  	theme?: Partial<ButtonTheme>;
  	sounds?: ButtonSounds | null;
  	selectAction?: InputAction;
  	selectSound?: UISound | string | null;
  	tooltipLabel?: React$1.ReactNode;
  	disableHint?: boolean;
  	/** When the button is clicked or the SELECT button on a gamepad is pressed */
  	onSelect?: () => void;
  	as?: "button" | "div";
  	hintAction?: InputAction;
  	actionContext?: string;
  	forceHint?: boolean;
  	shortcut?: InputAction | InputActionRequest;
  	allowFocusableChildren?: boolean;
  }
  export interface ClassProps {
  	className?: string;
  }
  export interface StyleProps extends ClassProps {
  	style?: React$1.CSSProperties;
  }
  export interface ValueBinding<T> {
  	readonly value: T;
  	subscribe(listener?: BindingListener<T>): ValueSubscription<T>;
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
  export interface ControlPath {
  	name: string;
  	device: string;
  	displayName?: string;
  }
  export enum KeyboardLayout {
  	AutoDetect = 0,
  	International = 1
  }
  export enum ControlScheme {
  	keyboardAndMouse = 0,
  	gamepad = 1
  }
  export enum GamepadType {
  	Xbox = 0,
  	PS = 1
  }
  export interface InputActionHintsProps extends ClassProps {
  	disabled?: boolean;
  	specifiedActions?: string[];
  	excludedActions?: string[];
  	labels?: boolean;
  	buttonAs?: ButtonProps["as"];
  	delay?: number;
  	delayIgnoreCounter?: number;
  }
  export export const InputActionHints: (props: InputActionHintsProps & React$1.RefAttributes<HTMLDivElement>) => React$1.ReactElement<any, string | React$1.JSXElementConstructor<any>> | null;
  export export const ActionHintLayout: ({ children, className, ...props }: ButtonProps) => JSX.Element;
  export export function useGamepadType(): GamepadType;
  export export function useKeyboardLayout(): KeyboardLayout;
  export export function useLayoutMap(): Record<string, ControlPath>;
  export enum ShortInputPathOption {
  	FallbackToLong = 1,
  	FallbackToControl = 2
  }
  export interface InputHintTheme {
  	hint: string;
  	button: string;
  	icon: string;
  	label: string;
  }
  export interface InputHintProps extends ClassProps, StyleProps {
  	action?: InputAction;
  	actionContext?: string;
  	bindingIndex?: number;
  	active?: boolean;
  	controlScheme?: ControlScheme;
  	theme?: Partial<InputHintTheme>;
  	shortName?: ShortInputPathOption;
  	showLabel?: boolean;
  	tooltip?: boolean;
  	tooltipClassName?: string;
  }
  export export const InputHint: ({ action, active, controlScheme, ...props }: InputHintProps) => JSX.Element | null;
  export export const ActiveControlSchemeInputHint: (props: InputHintProps) => JSX.Element;
  export export const FocusedInputHint: (props: InputHintProps) => JSX.Element | null;
  export interface ControlIconsProps extends ClassProps, StyleProps {
  	modifiers: ControlPath[];
  	bindings: ControlPath[];
  	showName?: boolean;
  	shortName?: ShortInputPathOption;
  	theme?: Partial<InputHintTheme>;
  }
  export export const ControlIcons: ({ modifiers, bindings, showName, shortName, theme, className, style, children }: React$1.PropsWithChildren<ControlIconsProps>) => JSX.Element;
  export interface ControlIconProps extends ClassProps {
  	binding: ControlPath;
  	modifier: boolean;
  	shortName?: ShortInputPathOption;
  	theme?: InputHintTheme;
  }
  export export const ControlIcon: React$1.FC<ControlIconProps>;
  export export function useInputControlIcon(binding: ControlPath): string | null;
  export enum GamepadButton$1 {
  	buttonSouth = 0,
  	buttonEast = 1,
  	buttonWest = 2,
  	buttonNorth = 3,
  	leftShoulder = 4,
  	rightShoulder = 5,
  	leftTrigger = 6,
  	rightTrigger = 7,
  	select = 8,
  	start = 9,
  	leftStickPress = 10,
  	rightStickPress = 11,
  	up = 12,
  	down = 13,
  	left = 14,
  	right = 15
  }
  export export function gamepadButtonFromString(name: string): GamepadButton$1 | undefined;
  export export enum GamepadAxis {
  	leftStickX = 0,
  	leftStickY = 1,
  	RightStickX = 2,
  	RightStickY = 3
  }
  export interface PointerBarrierProps {
  	onClick: () => void;
  }
  export export const PointerBarrier: React$1.FC<PointerBarrierProps>;
  export export const EventInputProvider: ({ children }: React$1.PropsWithChildren) => JSX.Element;
  export export const GamepadPointerEventProvider: ({ children }: React$1.PropsWithChildren) => JSX.Element;
  export export const NativeInputProvider: ({ children }: React$1.PropsWithChildren) => JSX.Element;
  export export const navActions: InputAction[];
  export interface InputActionBarrierProps {
  	includes?: InputAction[];
  	excludes?: InputAction[];
  	disabled?: boolean;
  }
  export export const InputActionBarrier: React$1.NamedExoticComponent<React$1.PropsWithChildren<InputActionBarrierProps>>;
  export interface InputActionConsumerProps {
  	actions: InputActions | null;
  	actionContext?: string;
  	disabled?: boolean;
  	ignoreFocusState?: boolean;
  }
  export export const InputActionConsumer: React$1.NamedExoticComponent<React$1.PropsWithChildren<InputActionConsumerProps>>;
  export interface SingleActionConsumerProps {
  	action: InputAction;
  	disabled?: boolean;
  	actionContext?: string;
  	onAction?: () => void;
  }
  /** When the Gamepad "A" button is pressed */
  export export const SelectConsumer: ({ action, ...props }: React$1.PropsWithChildren<Partial<SingleActionConsumerProps>>) => JSX.Element;
  /** When the Keyboard "ESC" or Gamepad "B" button is pressed */
  export export const BackConsumer: ({ action, ...props }: React$1.PropsWithChildren<Partial<SingleActionConsumerProps>>) => JSX.Element;
  export interface ExpandConsumerProps extends Omit<SingleActionConsumerProps, "action"> {
  	expanded: boolean;
  	expandable: boolean;
  }
  /** When the Gamepad "X" button is pressed */
  export export const ExpandConsumer: ({ expanded, expandable, disabled, children, onAction }: React$1.PropsWithChildren<ExpandConsumerProps>) => JSX.Element;
  export interface InputActionEvent {
  	action: InputAction;
  	value: null | number | Number2;
  }
  export export const inputActionNames$: ValueBinding<(keyof InputActionsDefinition)[]>;
  export export const onInputActionPerformed$: EventBinding<InputActionEvent>;
  export export const onInputActionReleased$: EventBinding<InputActionEvent>;
  export export function setInputActionPriority(action: InputAction, requestId: string | null, priority: number, force: boolean): void;
  export export class InputStack {
  	_items: InputStackItem[];
  	contains(action: InputAction): boolean;
  	indexOf(action: InputAction): number;
  	lastIndexOf(action: InputAction): number;
  	findItem(action: InputAction): {
  		context: string;
  		index: number;
  	};
  	findLastItem(action: InputAction): {
  		context: string;
  		index: number;
  	};
  	push(action: InputAction, context: string, callback: Function): void;
  	removeWhere(predicate: (action: InputAction) => boolean): void;
  	clear(): void;
  	dispatchInputEvent(action: InputAction, value: any): boolean;
  	debugPrint(): void;
  }
  export class InputStackItem {
  	readonly action: InputAction;
  	readonly context: string;
  	readonly callback: Function;
  	constructor(action: InputAction, context: string, callback: Function);
  }
  export interface InputController {
  	attachChild(controller: InputController): void;
  	detachChild(controller: InputController): void;
  	transformStack(stack: InputStack): void;
  	setDirty(): void;
  }
  export export const defaultInputController: InputController;
  export export const InputContext: React$1.Context<InputController>;
  export type InputStackTransformer = (stack: InputStack) => void;
  export enum InputControllerState {
  	Disabled = 0,
  	ActiveOnFocus = 1,
  	AlwaysActive = 2
  }
  export export function useInputController(state: InputControllerState, transformer: InputStackTransformer | null): InputController;
  export export class InputControllerImpl implements InputController {
  	private _parent;
  	private _child;
  	private _transformer;
  	get transformer(): InputStackTransformer | null;
  	set transformer(transformer: InputStackTransformer | null);
  	attachTo(controller: InputController): void;
  	detach(): void;
  	attachChild(controller: InputController): void;
  	detachChild(controller: InputController): void;
  	transformStack(stack: InputStack): void;
  	setDirty(): void;
  }
  export export class RootInputControllerImpl implements InputController {
  	private stack;
  	private onStackChanged;
  	private _child;
  	private _udpateHandle;
  	constructor(stack: InputStack, onStackChanged: () => void);
  	attachChild(controller: InputController): void;
  	detachChild(controller: InputController): void;
  	transformStack(): void;
  	setDirty(): void;
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
  
  export {
  	GamepadButton$1 as GamepadButton,
  };
  
  export {};
  
}