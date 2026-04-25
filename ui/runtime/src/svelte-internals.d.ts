// Svelte's package.json deliberately doesn't ship types for the
// `svelte/internal/*` sub-paths — they're an implementation detail
// the compiler emits direct imports against. We re-export them as
// importmap entries (so externally-built UI mods can share runtime
// chunks with stock) and need to silence tsc here.
declare module 'svelte/internal/client';
declare module 'svelte/internal/disclose-version';
declare module 'svelte/internal/flags/legacy';
