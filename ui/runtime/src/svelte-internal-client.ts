// Re-export Svelte's compiled-component runtime so externally-built
// UI mods (Nova et al.) can resolve `import * as $ from
// 'svelte/internal/client'` through Dragonglass's importmap and
// share the same runtime instance with stock.
export * from 'svelte/internal/client';
