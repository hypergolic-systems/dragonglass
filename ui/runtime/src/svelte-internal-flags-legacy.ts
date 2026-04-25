// Side-effect entry — Svelte 5's compiled-component output emits
// `import 'svelte/internal/flags/legacy'` to opt the runtime into
// legacy compatibility mode. Re-export for importmap visibility.
import 'svelte/internal/flags/legacy';
