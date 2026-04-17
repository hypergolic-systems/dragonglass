# IPC Contract — Dragonglass HUD

This document is the **source of truth** for the shared-memory layout
and synchronization protocol used between `crates/dg-sidecar/` (Rust,
CEF host) and `mod/Dragonglass.Hud/` (C# KSP plugin). The Rust
`crates/dg-shm/src/layout.rs` and C# `mod/Dragonglass.Hud/src/Layout.cs`
must mirror the constants defined here exactly.

## Transport

- **Shared file path:**
  - macOS / Linux: `${TMPDIR:-/tmp}/dragonglass-<session>.shm`
  - Windows: `%LOCALAPPDATA%\Temp\dragonglass-<session>.shm`
  - `<session>` is a per-KSP-instance GUID suffix so multiple KSP
    instances don't collide.
- **Backing:** regular file on disk, mapped into both processes via
  `memmap2::MmapMut` (Rust) and `MemoryMappedFile.CreateFromFile` (C#).
  The OS keeps hot pages in the page cache.
- **Size:** fixed 4096 bytes (one page). Large enough for the frame
  header + input ring; pixel bytes never flow through SHM (see
  Transport notes below).
- **Lifecycle:** sidecar creates, truncates, writes the header, then
  loops serving frames + draining input. Plugin opens read/write; if
  the file is missing or `magic` is wrong, the plugin logs and stays
  dormant — it does not crash the scene.

## Pixel transport

Pixels do **not** flow through the shared file. On the `on_accelerated_paint`
callback, CEF hands the sidecar an `IOSurfaceRef`; the sidecar blits
it into a persistent canvas IOSurface marked `kIOSurfaceIsGlobal` and
publishes the canvas's `IOSurfaceID` via the header. The plugin wraps
the surface as an `MTLTexture` with `Texture2D.CreateExternalTexture`,
and the native rendering dylib blits it into the Unity `RawImage`
backing texture each frame — all GPU-local, no memcpy.

## Header (bytes 0–127, little-endian)

All offsets are in bytes from the start of the mapped region. Fields
are naturally aligned.

| Offset | Size | Field              | Meaning                                                         |
|-------:|-----:|--------------------|-----------------------------------------------------------------|
|    0   |  4   | `magic`            | `0x534C_4744` — ASCII `"DGLS"` little-endian                    |
|    4   |  2   | `version`          | `3`                                                             |
|    6   |  2   | `header_size`      | `128`                                                           |
|    8   |  4   | `width`            | frame width in pixels                                           |
|   12   |  4   | `height`           | frame height in pixels                                          |
|   16   |  4   | `stride`           | bytes per row (`width * 4`)                                     |
|   20   |  4   | `format`           | `1` = BGRA8 premultiplied alpha, origin top-left                |
|   24   |  8   | `seq`              | seqlock counter (`u64`, atomic). Odd = writer mid-update.       |
|   32   |  8   | `frame_id`         | monotonic counter incremented on each committed frame           |
|   40   | 16   | _reserved_         | zero-filled                                                     |
|   56   |  4   | `io_surface_id`    | global `IOSurfaceID` for the current canvas surface             |
|   60   |  4   | `io_surface_gen`   | bumped on each commit; plugin re-blits when this changes        |
|   64   | 64   | _reserved_         | zero-filled; reserved for future header fields                  |

### Seqlock protocol

Classical single-writer / single-reader seqlock over the frame
header. The invariant is that `seq` is even exactly when the header
reflects a committed, tear-free frame. Readers that observe an odd
`seq`, or that see `seq` change between the pre-read and post-read
sample, must discard and retry.

**Writer (sidecar):**

```text
1. s ← atomic_fetch_add(seq, 1)          // s becomes odd; "writing"
2. store io_surface_id, io_surface_gen   // plain stores, bracketed by (1) and (3)
3. atomic_fetch_add(seq, 1, Release)     // even; "stable"
```

**Reader (plugin):**

```text
1. s1 ← atomic_load(seq, Acquire)
2. if s1 is odd       → skip
3. if s1 == last_seen → skip (no change since last read)
4. load io_surface_id, io_surface_gen, frame_id
5. s2 ← atomic_load(seq, Acquire)
6. if s2 != s1        → torn; retry next tick
7. commit: native plugin re-blits if io_surface_gen changed
           last_seen ← s1
```

At 60 Hz the sidecar spends almost all of its time between
`fetch_add` calls, so torn reads are statistically rare.

## Input ring (bytes 128–4095, plugin → sidecar)

Single-producer single-consumer lock-free ring buffer. The plugin
(C#) is the sole producer; the sidecar (Rust) is the sole consumer.
Both indices wrap naturally as `u32` — the modulus is taken at access
time (`idx % INPUT_RING_CAPACITY`).

| Offset | Size | Field        | Written by | Meaning                                       |
|-------:|-----:|--------------|------------|-----------------------------------------------|
|   128  |  4   | `write_idx`  | plugin     | producer index (events written)               |
|   132  |  4   | `read_idx`   | sidecar    | consumer index (events consumed)              |
|   136  | 24   | _reserved_   |            | zero-filled padding to align the ring         |
|   160  | 3840 | `slots[240]` | plugin     | 240 slots × 16 bytes                          |

### Slot layout (16 bytes each)

| Offset | Size | Field    | Meaning                                         |
|-------:|-----:|----------|-------------------------------------------------|
|    0   |  1   | `type`   | `1`=MouseMove, `2`=MouseDown, `3`=MouseUp, `4`=MouseWheel |
|    1   |  1   | `button` | `0`=None, `1`=Left, `2`=Right, `3`=Middle       |
|    2   |  2   | _pad_    | zero-filled                                     |
|    4   |  4   | `x`      | CEF viewport x (i32)                            |
|    8   |  4   | `y`      | CEF viewport y (i32)                            |
|   12   |  4   | `extra`  | wheel delta (MouseWheel) or 0                   |

### Ring protocol

**Producer (plugin):**

```text
1. w ← load write_idx  (relaxed)
2. r ← load read_idx   (acquire)
3. if w - r >= 240     → ring full; drop event
4. store slot[w % 240]
5. store write_idx ← w + 1  (release)
```

**Consumer (sidecar):**

```text
loop forever:
  r ← load read_idx   (relaxed)
  w ← load write_idx  (acquire)
  while r != w:
    load slot[r % 240]
    r ← r + 1
  store read_idx ← r  (release)
```

## Lifecycle assumptions

- **Exactly one writer, exactly one reader** per direction. The
  seqlock is not safe with multiple writers; the ring is SPSC only.
- If the sidecar restarts, the plugin should re-open the file on its
  next `Update()`.
- **Plugin writes only to the input ring** (bytes 128+) and reads
  only the frame header. The sidecar writes only the frame header
  and reads only the input ring.

## Versioning

Any breaking change to the layout bumps `version`. The plugin checks
`version == 3` at open time; a mismatch means "sidecar is newer/older
than I understand" — log once and stay dormant.
