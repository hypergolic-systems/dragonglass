// Side-effect import: stock's main.ts mounts <App> into #app at the
// top level. By bringing stock into the runtime build alongside
// svelte/instruments/telemetry, Vite's auto-chunking puts svelte and
// telemetry into shared chunks — so stock's tree, runtime entries,
// and any third-party mod that imports from the runtime all share a
// single Svelte runtime instance.
import '@dragonglass/stock';
