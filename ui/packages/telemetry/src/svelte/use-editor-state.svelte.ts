import { getKsp } from './context';
import { EditorStateTopic } from '../core/topics';
import type { EditorStateData } from '../core/editor-state-data';

function defaults(): EditorStateData {
  return { heldPart: null };
}

/**
 * Subscribe to the `editorState` topic. Returns a reactive proxy so
 * components can read `editorState.heldPart` with fine-grained
 * reactivity — flips to a non-null name when the player picks a
 * part up onto the cursor, back to null when they drop/place it.
 *
 * Must be called during component initialization (needs Svelte context).
 */
export function useEditorState(): EditorStateData {
  const telemetry = getKsp();
  let data = $state<EditorStateData>(defaults());

  $effect(() => {
    return telemetry.subscribe(EditorStateTopic, (frame) => {
      Object.assign(data, frame);
    });
  });

  return data;
}
