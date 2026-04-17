# Dragonglass

A KSP mod that replaces the Flight UI with a web-based UI rendered in CEF (Chromium Embedded Framework). CEF runs in a sidecar process and shares a GPU texture with KSP via IOSurface for compositing.

## Architecture

Three languages, three build systems, one monorepo:

- **`crates/`** — Rust (Cargo workspace at repo root). The CEF sidecar.
- **`mod/`** — C# (MSBuild) + native Obj-C/C++ (just). The KSP plugin.
- **`ui/`** — TypeScript/Svelte (npm workspaces, Vite). The web UI.

### Crates

- `dg-sidecar` — CEF host binary. Runs the browser, reads input events from SHM, publishes IOSurface IDs.
- `dg-shm` — Shared-memory signaling (4096-byte file: seqlock header + input ring buffer). Carries IOSurface ID/gen (sidecar→plugin) and mouse events (plugin→sidecar). Must stay in lockstep with `mod/Dragonglass.Hud/src/Layout.cs`.
- `dg-gpu` — IOSurface/Metal GPU texture sharing. Platform-gated (macOS only for now).

### Mod

- `Dragonglass.Hud` — KSP HUD addon. Spawns sidecar at startup, composites CEF output via native plugin in Flight scene.
- `Dragonglass.Telemetry` — Standalone WebSocket telemetry server (stub). Will broadcast vessel state to any connected UI.
- `native/darwin-universal/` — `DgHudNative` — C++/Obj-C Unity rendering plugin. Zero-copy GPU blit via OpenGL/Metal.

### UI

- `@dragonglass/instruments` — Reusable flight instrument components (Navball, tapes, readouts, clock).
- `@dragonglass/stock` — The shipped flight UI app (deployed to `UI/Stock`).
- `@dragonglass/workbench` — Experimentation app (not shipped).

## Build

Requires: `just`, `cargo`, `dotnet`, `npm` (Xcode CLT on macOS).

```
just build              # everything
just ui-dev             # stock app dev server
just ui-build           # typecheck + production build
just sidecar-bundle     # build + bundle CEF sidecar .app
just mod-build          # dotnet build
just native-build-darwin # macOS native plugin
```

## IPC

See `docs/ipc.md` for the shared-memory spec. The SHM file is 4096 bytes (one page): a seqlock header (bytes 0–127) carrying IOSurface ID/gen from sidecar to plugin, and an input ring buffer (bytes 128–4095) carrying mouse events from plugin to sidecar. The plugin reads the IOSurface ID each frame and wraps the surface as a GL/Metal texture for compositing.

## Key invariants

- `crates/dg-shm/src/layout.rs` and `mod/Dragonglass.Hud/src/Layout.cs` must define identical byte offsets. Any change to one must be mirrored in the other.
