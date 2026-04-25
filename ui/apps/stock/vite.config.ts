import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// Stock's only Vite config now: dev-server SPA mode for `npm run dev`.
// Production builds happen inside the runtime package (ui/runtime),
// where stock is bundled alongside svelte/instruments/telemetry as a
// shared-chunk multi-entry build. That's how stock's tree shares the
// same Svelte runtime instance with everything else under the
// import map.
export default defineConfig({
  plugins: [svelte()],
  base: './',
});
