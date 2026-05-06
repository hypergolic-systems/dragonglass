import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// Two consumers:
//   - `npm run dev` — SPA dev server.
//   - `npm run build` — self-contained static bundle for the demo
//     site (Firebase Hosting). Output goes to apps/stock/dist.
//
// In KSP this app is loaded a different way: the runtime package
// (ui/runtime) bundles stock alongside svelte/instruments/telemetry
// as a shared-chunk multi-entry library, so everything under the
// sidecar's import map shares one Svelte runtime instance. The
// static bundle here is independent of that pipeline — it's just
// stock with its deps inlined, suitable for serving from any
// static host.
export default defineConfig({
  plugins: [svelte()],
  base: './',
});
