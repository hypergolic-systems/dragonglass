// Public types for @dragonglass/windows. Lives in a `.ts` file
// (rather than inside FloatingWindow.svelte's `<script module>`) so
// `tsc` can resolve named type exports — Svelte's typegen exposes
// only the default-export component, not module-block named exports.

export interface FloatingWindowPos {
  x: number;
  y: number;
}

export interface FloatingWindowSize {
  w: number;
  h: number;
}

export interface FloatingWindowProps {
  /** Plain title fallback when no `header` snippet is provided. */
  title?: string;
  /** Initial position in viewport pixels. */
  defaultPos?: FloatingWindowPos;
  /** Initial size in viewport pixels. */
  defaultSize?: FloatingWindowSize;
  /** Lower bound for resize. */
  minSize?: FloatingWindowSize;
  /** z-index. Externally managed so consumers can implement their
   *  own stacking model (Nova's HUD owns one z-counter per panel). */
  z?: number;
  /** Show a close button when provided. */
  onClose?: () => void;
  /** Fired on pointerdown anywhere on the window. Consumers raise
   *  the z-index here. */
  onRaise?: () => void;
  /** Slots — header for custom title bar contents (chips, buttons),
   *  children for the body. */
  header?: import('svelte').Snippet;
  children?: import('svelte').Snippet;
}
