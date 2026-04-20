//! Shared-memory header + input ring layout. Source of truth:
//! `docs/ipc.md`.
//!
//! This module must stay in lockstep with
//! `mod/Dragonglass.Hud/src/Layout.cs`. Any change here that shifts an
//! offset is a breaking protocol change and must bump `VERSION`.

use std::mem::{align_of, size_of};

/// ASCII `"DGLS"` little-endian.
pub const MAGIC: u32 = 0x534C_4744;

/// Protocol version. Bump on any header layout change.
///
/// v2 added `OFF_IO_SURFACE_ID` / `OFF_IO_SURFACE_GEN` for the zero-copy
/// (IOSurface) pipeline.
///
/// v3 grows the file from 128 bytes to 4096 bytes and adds a plugin→sidecar
/// input event ring buffer at offset 128. The frame header (bytes 0–127)
/// is unchanged — a v2 plugin reading a v3 file still works for frame
/// metadata; it just won't write input events.
pub const VERSION: u16 = 3;

/// Fixed header size in bytes. Payload starts at this offset.
pub const HEADER_SIZE: usize = 128;

/// Pixel format code for BGRA8 premultiplied alpha, origin top-left.
pub const FORMAT_BGRA8_PREMUL: u32 = 1;

// --- Header field byte offsets (mirrored in mod/Dragonglass.Hud/src/Layout.cs) ---
pub const OFF_MAGIC: usize = 0;
pub const OFF_VERSION: usize = 4;
pub const OFF_HEADER_SIZE: usize = 6;
pub const OFF_WIDTH: usize = 8;
pub const OFF_HEIGHT: usize = 12;
pub const OFF_STRIDE: usize = 16;
pub const OFF_FORMAT: usize = 20;
pub const OFF_SEQ: usize = 24;
pub const OFF_FRAME_ID: usize = 32;
// Bytes 40–55: reserved (formerly dirty-rect fields, unused).
/// IOSurfaceID of the most recently committed frame.
pub const OFF_IO_SURFACE_ID: usize = 56;
/// Monotonic counter bumped each time `OFF_IO_SURFACE_ID` changes. Lets
/// the plugin distinguish "same surface, next frame" (no rebind needed)
/// from "new surface, rebind external texture".
pub const OFF_IO_SURFACE_GEN: usize = 60;

// --- Input event ring buffer (v3, plugin → sidecar) -----------------------
//
// SPSC lock-free ring. The plugin (C#) is the sole producer; the sidecar
// (Rust) is the sole consumer. Both indices wrap naturally as u32 — the
// modulus is taken at access time (`idx % INPUT_RING_CAPACITY`).

/// Total file size in v3+. One 4 KiB page — comfortably holds the 128-byte
/// frame header plus the input ring.
pub const SHM_FILE_SIZE: usize = 4096;

/// Byte offset of the producer write index (u32, written by plugin).
pub const OFF_INPUT_WRITE_IDX: usize = 128;
/// Byte offset of the consumer read index (u32, written by sidecar).
pub const OFF_INPUT_READ_IDX: usize = 132;

/// Byte offset where the ring slots begin (16-byte aligned).
pub const OFF_INPUT_RING: usize = 160;
/// Size of one input event slot in bytes.
pub const INPUT_SLOT_SIZE: usize = 16;
/// Number of slots in the ring. `(4096 - 160) / 16 = 246`, but we round
/// down to 240 for a clean number.
pub const INPUT_RING_CAPACITY: usize = 240;

// --- Input event slot layout (16 bytes each) ---

/// Byte offset within a slot for the event type (u8).
pub const SLOT_OFF_TYPE: usize = 0;
/// Byte offset within a slot for the button code (u8).
pub const SLOT_OFF_BUTTON: usize = 1;
/// Byte offset within a slot for the x coordinate (i32).
pub const SLOT_OFF_X: usize = 4;
/// Byte offset within a slot for the y coordinate (i32).
pub const SLOT_OFF_Y: usize = 8;
/// Byte offset within a slot for extra data (i32) — wheel delta, etc.
pub const SLOT_OFF_EXTRA: usize = 12;

// --- Input event type codes (u8) ---
pub const INPUT_MOUSE_MOVE: u8 = 1;
pub const INPUT_MOUSE_DOWN: u8 = 2;
pub const INPUT_MOUSE_UP: u8 = 3;
pub const INPUT_MOUSE_WHEEL: u8 = 4;
/// Plugin is asking the sidecar to resize its CEF viewport + backing
/// IOSurface. `x` carries the new width, `y` the new height (both
/// positive `i32` pixel counts). Button stays `INPUT_BTN_NONE`.
pub const INPUT_RESIZE: u8 = 5;
/// Plugin is asking the sidecar to navigate the main frame to a new
/// URL. This event spans multiple ring slots: one header slot of this
/// type carrying the UTF-8 byte length in `extra`, followed by
/// `ceil(byte_len / 12)` `INPUT_NAVIGATE_CHUNK` slots that pack the
/// URL bytes into the `x` / `y` / `extra` fields (12 bytes per slot,
/// little-endian). The producer reserves all slots and bumps
/// `write_idx` once, so the consumer never observes a partial URL.
pub const INPUT_NAVIGATE: u8 = 6;
/// Continuation slot for `INPUT_NAVIGATE`. Carries 12 raw URL bytes
/// in the `x` / `y` / `extra` fields. Final chunk zero-pads any
/// unused tail bytes; the consumer trims to the header's declared
/// length.
pub const INPUT_NAVIGATE_CHUNK: u8 = 7;

/// Hard cap on the URL byte length a single `INPUT_NAVIGATE` message
/// may carry. Keeps any one navigate message well under ring capacity
/// (240 slots × 12 bytes = 2880 byte ceiling) so a navigate can never
/// starve concurrent mouse / resize traffic.
pub const MAX_NAV_URL_BYTES: usize = 2048;

// --- Input button codes (u8) ---
pub const INPUT_BTN_NONE: u8 = 0;
pub const INPUT_BTN_LEFT: u8 = 1;
pub const INPUT_BTN_RIGHT: u8 = 2;
pub const INPUT_BTN_MIDDLE: u8 = 3;

/// Compile-time invariants. Any violation is a layout bug.
const _: () = {
    assert!(HEADER_SIZE == 128);
    assert!(OFF_SEQ % 8 == 0, "seq must be 8-byte aligned for AtomicU64");
    assert!(OFF_FRAME_ID % 8 == 0);
    assert!(OFF_IO_SURFACE_ID % 4 == 0);
    assert!(OFF_IO_SURFACE_GEN % 4 == 0);
    assert!(OFF_IO_SURFACE_GEN + 4 <= HEADER_SIZE);
    // Basic sanity: u64 on every platform we care about is 8 bytes and aligns to 8.
    assert!(size_of::<u64>() == 8);
    assert!(align_of::<u64>() == 8);

    // v3 input ring invariants.
    assert!(SHM_FILE_SIZE == 4096);
    assert!(OFF_INPUT_WRITE_IDX == HEADER_SIZE); // starts right after header
    assert!(OFF_INPUT_WRITE_IDX % 4 == 0);
    assert!(OFF_INPUT_READ_IDX % 4 == 0);
    assert!(OFF_INPUT_RING % 16 == 0, "ring must be 16-byte aligned");
    assert!(INPUT_SLOT_SIZE == 16);
    assert!(OFF_INPUT_RING + INPUT_SLOT_SIZE * INPUT_RING_CAPACITY <= SHM_FILE_SIZE);
    assert!(SLOT_OFF_X % 4 == 0);
    assert!(SLOT_OFF_Y % 4 == 0);
    assert!(SLOT_OFF_EXTRA % 4 == 0);
};
