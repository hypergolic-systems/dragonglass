#![allow(non_snake_case)]

//! Windows KSP native plugin (`DgHudNative.dll`). Counterpart of
//! `mod/native/darwin-universal/src/DgHudNative.mm`.
//!
//! Each frame:
//!   1. C# reads the sidecar's SHM header, gets `(handle_lo32, gen)`.
//!   2. C# calls `DgHudNative_UpdatePending(handle_lo32, gen)`.
//!   3. C# fires `GL.IssuePluginEvent(renderEventFunc, 0)`.
//!   4. Unity invokes our render-thread callback. We open the DXGI
//!      shared NT handle as an `ID3D11Texture2D`, acquire its keyed
//!      mutex (key 1 — sidecar releases with 1 after each paint),
//!      `CopyResource` into the Unity-owned destination texture, and
//!      release the mutex (key 0 — sidecar can paint again).
//!
//! The lib only has real code on Windows; on other platforms it
//! compiles to an empty `cdylib` so workspace `cargo check` still
//! works uniformly.

#[cfg(target_os = "windows")]
mod imp;
