# Dragonglass

A KSP mod that replaces the Flight UI with a web-based UI rendered in CEF (Chromium Embedded Framework). CEF runs in a sidecar process and shares a GPU texture with KSP — IOSurface on macOS, DXGI shared NT handle on Windows — for compositing.

## Architecture

Three languages, three build systems, one monorepo:

- **`crates/`** — Rust (Cargo workspace at repo root). The CEF sidecar and the Windows native plugin.
- **`mod/`** — C# (MSBuild) + native Obj-C/C++ (just). The KSP plugin.
- **`ui/`** — TypeScript/Svelte (npm workspaces, Vite). The web UI.

### Crates

- `dg-sidecar` — CEF host binary. Runs the browser, reads input events from SHM, publishes shared-texture handles.
- `dg-shm` — Shared-memory signaling (4096-byte file: seqlock header + input ring buffer). Carries the canvas handle (sidecar→plugin) and input events (plugin→sidecar). Must stay in lockstep with `mod/Dragonglass.Hud/src/Layout.cs`.
- `dg-gpu` — Cross-platform GPU texture sharing: `iosurface_bridge` (macOS, IOSurface/Metal) and `d3d11_bridge` (Windows, D3D11 keyed-mutex shared NT handle).
- `dg-hud-plugin-win` — Rust cdylib producing `DgHudNative.dll` for Windows (the C# `[DllImport("DgHudNative")]` target). Receives the DXGI handle and blits via D3D11.

### Mod

- `Dragonglass.Hud` — KSP HUD addon. Spawns sidecar at startup, composites CEF output via the native plugin in the Flight scene.
- `Dragonglass.Telemetry` — WebSocket telemetry server broadcasting flight, engine, part-catalog, staging, and PAW state to any connected UI. Topics live under `src/Topics/` with installers that wire them onto `SubscriptionBus`.
- `native/darwin-universal/` — `DgHudNative.bundle` — C++/Obj-C Unity rendering plugin. Zero-copy GPU blit via OpenGL/Metal. The Windows equivalent is the `dg-hud-plugin-win` crate above.

### UI

- `@dragonglass/instruments` — Reusable flight instrument components (Navball, tapes, readouts, clock).
- `@dragonglass/telemetry` — Telemetry client: WebSocket transport, smoothing, simulated source for offline dev, Svelte stores.
- `@dragonglass/stock` — The shipped flight UI app (deployed to `UI/Stock`).
- `@dragonglass/workbench` — Experimentation app (not shipped).

## Build

Requires: `just`, `cargo`, `dotnet`, `npm` (Xcode CLT on macOS).

```
just build                  # everything (macOS-host targets)
just ui-dev                 # stock app dev server
just ui-build               # typecheck + production build
just sidecar-bundle         # build + bundle macOS CEF sidecar .app
just sidecar-bundle-windows # build + stage Windows CEF sidecar
just mod-build              # dotnet build
just native-build-darwin    # macOS native plugin (DgHudNative.bundle)
just native-build-windows   # Windows native plugin (DgHudNative.dll)
just install <ksp-path>     # build + install into a KSP directory (auto-detects host platform)
```

## IPC

See `docs/ipc.md` for the shared-memory spec. The SHM file is 4096 bytes (one page): a seqlock header (bytes 0–127) carrying the per-platform canvas handle from sidecar to plugin, and an input ring buffer (bytes 128–4095) carrying mouse, wheel, resize, navigate, and keyboard events from plugin to sidecar. The plugin reads the canvas handle each frame and wraps the surface as a platform-native external texture for compositing.

## Key invariants

- `crates/dg-shm/src/layout.rs` and `mod/Dragonglass.Hud/src/Layout.cs` must define identical byte offsets. Any change to one must be mirrored in the other.
