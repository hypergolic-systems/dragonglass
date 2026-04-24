export interface MenuItem {
  readonly label: string;
  readonly disabled?: boolean;
  readonly danger?: boolean;
  /** Fires on click or Enter. `ContextMenu` also dismisses after
   *  invocation — callers should not call the dismiss function
   *  themselves. */
  onSelect(): void;
}
