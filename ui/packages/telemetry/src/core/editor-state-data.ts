/**
 * Minimal editor-scene state the UI uses to reason about user intent
 * inside the VAB/SPH. Emitted by `EditorStateTopic` every time the
 * value changes; the server broadcasts `null` outside the editor.
 *
 * Catalog-panel use case: when `heldPart` is non-null, clicking the
 * Dragonglass catalog discards the held part back to stock instead
 * of spawning a new one (mirrors KSP's drop-on-the-parts-bin gesture).
 */
export interface EditorStateData {
  /** Stock internal name of the part currently attached to the
   *  cursor (`EditorLogic.SelectedPart.partInfo.name`), or null when
   *  the cursor is empty or the player isn't in the editor. */
  readonly heldPart: string | null;
}
