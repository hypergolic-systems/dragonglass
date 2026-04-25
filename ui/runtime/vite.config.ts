import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import { resolve } from 'path';

// The Dragonglass runtime: every ESM bundle the sidecar publishes
// under its core import-map specifiers, built in a single Vite pass.
// Multi-entry library mode + Rollup's auto-chunking emits svelte's
// client runtime, three.js, and other shared deps into one chunk
// each, referenced by every entry that needs them. Result: every
// runtime entry — including stock — shares a single Svelte runtime
// instance. Third-party mods that import from these specifiers
// share that runtime too; getKsp/setKsp contexts compose across the
// whole tree.
//
// The output drops into Dragonglass_Hud/UI/, where the static server
// serves it like any other mod's UI directory. Specifiers like
// `svelte` and `@dragonglass/stock` are mapped to URLs under
// /Dragonglass_Hud/UI/ via the import map.
export default defineConfig({
  plugins: [svelte()],
  build: {
    lib: {
      entry: {
        svelte:                            resolve(__dirname, 'src/svelte.ts'),
        // Svelte 5 compiled-component output imports these internal
        // sub-paths directly. Externally-built UI mods need each one
        // to be addressable via the importmap so they share chunks
        // (and therefore runtime state) with stock and the runtime.
        'svelte/internal/client':          resolve(__dirname, 'src/svelte-internal-client.ts'),
        'svelte/internal/disclose-version':resolve(__dirname, 'src/svelte-internal-disclose-version.ts'),
        'svelte/internal/flags/legacy':    resolve(__dirname, 'src/svelte-internal-flags-legacy.ts'),
        three:                             resolve(__dirname, 'src/three.ts'),
        threlte:                           resolve(__dirname, 'src/threlte.ts'),
        stock:                             resolve(__dirname, 'src/stock.ts'),
        'instruments/index':               resolve(__dirname, 'src/instruments.ts'),
        'telemetry/core':                  resolve(__dirname, 'src/telemetry-core.ts'),
        'telemetry/svelte':                resolve(__dirname, 'src/telemetry-svelte.ts'),
        'telemetry/simulated':             resolve(__dirname, 'src/telemetry-simulated.ts'),
        'telemetry/smoothing':             resolve(__dirname, 'src/telemetry-smoothing.ts'),
        'telemetry/dragonglass':           resolve(__dirname, 'src/telemetry-dragonglass.ts'),
      },
      formats: ['es'],
    },
    // Deterministic filenames — the import map points at fixed URLs.
    // Hashed names would force a sidecar restart every UI rebuild.
    rollupOptions: {
      output: {
        entryFileNames: '[name].js',
        chunkFileNames: 'chunks/[name]-[hash].js',
        // Top-level for stylesheets so the static server's runtime
        // CSS auto-link scan picks them up (it only looks at the
        // _runtime/ root, not subdirs).
        assetFileNames: '[name][extname]',
      },
    },
    // Don't minify — keeps stack traces and devtools-inspect readable.
    // The HUD surface is small; bytes aren't the bottleneck.
    minify: false,
    // Avoid Vite's default modulePreload polyfill injection — we serve
    // these as plain ESM, not via a preload list.
    modulePreload: false,
  },
});
