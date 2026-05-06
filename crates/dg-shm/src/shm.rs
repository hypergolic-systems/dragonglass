//! File-backed shared-memory writer and reader implementing the
//! Dragonglass seqlock + input-ring protocol described in
//! `docs/ipc.md`.
//!
//! 4096 bytes total: a 128-byte seqlock header carrying IOSurface
//! ID/gen (sidecar → plugin), followed by an SPSC input event ring
//! (plugin → sidecar). No pixel bytes flow through the file — the
//! zero-copy IOSurface path is the only supported transport.
//!
//! Single-writer, single-reader per direction. The frame writer lives
//! in the sidecar; the frame reader lives in the KSP plugin (this
//! Rust reader is used by tests). The input ring's writer is the
//! plugin; its reader (`InputRingReader`) is the sidecar.

use std::fs::{File, OpenOptions};
use std::path::{Path, PathBuf};
use std::ptr;
use std::sync::atomic::{AtomicU64, Ordering};

use anyhow::{bail, Context, Result};
use memmap2::{Mmap, MmapMut};

use crate::layout::{
    FORMAT_BGRA8_PREMUL, HEADER_SIZE, INPUT_RING_CAPACITY, INPUT_SLOT_SIZE, MAGIC,
    OFF_CEF_WANTS_KEYBOARD, OFF_FORMAT, OFF_FRAME_ID, OFF_HEADER_SIZE, OFF_HEIGHT,
    OFF_INPUT_READ_IDX, OFF_INPUT_RING, OFF_INPUT_WRITE_IDX, OFF_IO_SURFACE_GEN, OFF_IO_SURFACE_ID,
    OFF_MAGIC, OFF_SEQ, OFF_STRIDE, OFF_VERSION, OFF_WIDTH, SHM_FILE_SIZE, SLOT_OFF_BUTTON,
    SLOT_OFF_EXTRA, SLOT_OFF_TYPE, SLOT_OFF_X, SLOT_OFF_Y, VERSION,
};

/// Default location for the shared file on the current OS.
pub fn default_shm_path() -> PathBuf {
    shm_path_for_session("default")
}

/// Session-scoped SHM path. Each KSP instance passes a unique session
/// ID so multiple instances don't stomp each other.
///
/// `std::env::temp_dir()` resolves per-platform: `$TMPDIR` then
/// `/tmp` on Unix, `%LOCALAPPDATA%\Temp` on Windows. The C# plugin
/// uses `Path.GetTempPath()`, which converges on the same directory.
pub fn shm_path_for_session(session_id: &str) -> PathBuf {
    std::env::temp_dir().join(format!("dragonglass-{session_id}.shm"))
}

/// Writer side of the shared-memory seqlock. Owned by the sidecar.
///
/// Backed by a regular file mmapped with `MmapMut`. The file is
/// `SHM_FILE_SIZE` (4096) bytes — the first 128 bytes are the frame
/// header, the rest is the input ring buffer. On macOS/Linux the OS
/// keeps hot pages in the page cache; at 60Hz the file is never actually
/// hit. Using a real file (instead of `shm_open`) lets the C# plugin use
/// `MemoryMappedFile.CreateFromFile` with the same path.
pub struct ShmWriter {
    _file: File,
    mmap: MmapMut,
    base: *mut u8,
    width: u32,
    height: u32,
    frame_id: u64,
}

// The raw pointer is derived from `mmap` and lives as long as `mmap` does.
// We never expose it outside this struct.
unsafe impl Send for ShmWriter {}

impl ShmWriter {
    /// Create (or recreate) the shared file at `path` and write the
    /// initial header. The file is `SHM_FILE_SIZE` (4096) bytes: 128-byte
    /// frame header + input ring buffer.
    pub fn create(path: &Path, width: u32, height: u32) -> Result<Self> {
        if width == 0 || height == 0 {
            bail!("invalid dimensions {}x{}", width, height);
        }

        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(true)
            .open(path)
            .with_context(|| format!("failed to open {}", path.display()))?;
        file.set_len(SHM_FILE_SIZE as u64)
            .with_context(|| format!("failed to size {} to {} bytes", path.display(), SHM_FILE_SIZE))?;

        let mut mmap = unsafe { MmapMut::map_mut(&file) }
            .with_context(|| format!("failed to mmap {}", path.display()))?;
        mmap.fill(0);

        let base = mmap.as_mut_ptr();
        let stride: u32 = width.checked_mul(4).context("stride overflow")?;

        unsafe {
            ptr::write(base.add(OFF_MAGIC) as *mut u32, MAGIC);
            ptr::write(base.add(OFF_VERSION) as *mut u16, VERSION);
            ptr::write(base.add(OFF_HEADER_SIZE) as *mut u16, HEADER_SIZE as u16);
            ptr::write(base.add(OFF_WIDTH) as *mut u32, width);
            ptr::write(base.add(OFF_HEIGHT) as *mut u32, height);
            ptr::write(base.add(OFF_STRIDE) as *mut u32, stride);
            ptr::write(base.add(OFF_FORMAT) as *mut u32, FORMAT_BGRA8_PREMUL);
            ptr::write(base.add(OFF_SEQ) as *mut u64, 0);
            ptr::write(base.add(OFF_FRAME_ID) as *mut u64, 0);
            ptr::write(base.add(OFF_IO_SURFACE_ID) as *mut u32, 0);
            ptr::write(base.add(OFF_IO_SURFACE_GEN) as *mut u32, 0);
        }

        Ok(Self {
            _file: file,
            mmap,
            base,
            width,
            height,
            frame_id: 0,
        })
    }

    pub fn width(&self) -> u32 {
        self.width
    }

    pub fn height(&self) -> u32 {
        self.height
    }

    /// Current committed frame id. Zero until the first write.
    pub fn frame_id(&self) -> u64 {
        self.frame_id
    }

    /// Raw pointer to the start of the mmap. Used to construct an
    /// `InputRingReader` that shares the same mapping.
    ///
    /// # Safety
    /// The returned pointer is valid for the lifetime of this `ShmWriter`.
    /// The caller must not outlive the writer.
    pub fn mmap_base(&self) -> *mut u8 {
        self.base
    }

    /// Publish a new viewport size for the current surface. Takes the
    /// seqlock so a concurrent reader that picks up `seq` mid-write
    /// bails out. The fields updated here are read at file-open time
    /// by the plugin (via `ShmReader`); the plugin discovers live
    /// changes through the IOSurface's own dimensions, not this
    /// header, so `set_dimensions` is mostly for external observers
    /// and keeps the published metadata honest after a resize.
    pub fn set_dimensions(&mut self, width: u32, height: u32) {
        let stride = width.saturating_mul(4);
        let base = self.base;

        unsafe {
            let seq = AtomicU64::from_ptr(base.add(OFF_SEQ) as *mut u64);
            seq.fetch_add(1, Ordering::AcqRel);
        }
        std::sync::atomic::fence(Ordering::Release);

        unsafe {
            ptr::write_volatile(base.add(OFF_WIDTH) as *mut u32, width);
            ptr::write_volatile(base.add(OFF_HEIGHT) as *mut u32, height);
            ptr::write_volatile(base.add(OFF_STRIDE) as *mut u32, stride);
        }

        unsafe {
            let seq = AtomicU64::from_ptr(base.add(OFF_SEQ) as *mut u64);
            seq.fetch_add(1, Ordering::Release);
        }

        self.width = width;
        self.height = height;
    }

    /// Commit a frame whose pixels live outside the shm (IOSurface
    /// referenced by `io_surface_id`). The header advances `seq` +
    /// `frame_id` so readers can detect a new frame.
    ///
    /// The seqlock protocol: bump seq to odd, mutate header fields,
    /// bump seq to even with Release ordering.
    pub fn write_header_only(&mut self, io_surface_id: u32, io_surface_gen: u32) {
        let base = self.base;

        // SAFETY: OFF_SEQ is 8-byte aligned and inside the mapped region.
        unsafe {
            let seq = AtomicU64::from_ptr(base.add(OFF_SEQ) as *mut u64);
            seq.fetch_add(1, Ordering::AcqRel);
        }

        // Why: AcqRel on the bump above is one-directional — it does not
        // stop the data stores below from being reordered ahead of the
        // seq→odd store on ARM. Close that side explicitly.
        std::sync::atomic::fence(Ordering::Release);

        self.frame_id = self.frame_id.wrapping_add(1);
        unsafe {
            ptr::write_volatile(base.add(OFF_FRAME_ID) as *mut u64, self.frame_id);
            ptr::write_volatile(base.add(OFF_IO_SURFACE_ID) as *mut u32, io_surface_id);
            ptr::write_volatile(base.add(OFF_IO_SURFACE_GEN) as *mut u32, io_surface_gen);
        }

        unsafe {
            let seq = AtomicU64::from_ptr(base.add(OFF_SEQ) as *mut u64);
            seq.fetch_add(1, Ordering::Release);
        }
    }

    /// Flush any pending writes to the underlying file.
    pub fn flush(&mut self) -> Result<()> {
        self.mmap.flush()?;
        Ok(())
    }

    /// Publish whether a CEF editable element is currently focused. The
    /// plugin reads this each frame and toggles a
    /// `ControlTypes.KEYBOARDINPUT` `InputLockManager` lock so KSP
    /// shortcut keys don't fire while the user is typing into a web
    /// input. Lives outside the frame seqlock — plain release-store is
    /// enough.
    pub fn write_cef_wants_keyboard(&mut self, wants: bool) {
        unsafe {
            let ptr = self.base.add(OFF_CEF_WANTS_KEYBOARD) as *mut u32;
            std::sync::atomic::AtomicU32::from_ptr(ptr)
                .store(if wants { 1 } else { 0 }, Ordering::Release);
        }
    }

}

/// Reader side of the shared-memory seqlock. Used by tests and the
/// standalone viewer example. The production reader lives in C# inside
/// the KSP plugin.
pub struct ShmReader {
    _file: File,
    _mmap: Mmap,
    base: *const u8,
    width: u32,
    height: u32,
}

unsafe impl Send for ShmReader {}

impl ShmReader {
    pub fn open(path: &Path) -> Result<Self> {
        let file = OpenOptions::new()
            .read(true)
            .open(path)
            .with_context(|| format!("failed to open {}", path.display()))?;
        let mmap = unsafe { Mmap::map(&file) }
            .with_context(|| format!("failed to mmap {}", path.display()))?;

        if mmap.len() < HEADER_SIZE {
            bail!("file smaller than header ({} < {})", mmap.len(), HEADER_SIZE);
        }
        let base = mmap.as_ptr();

        let (magic, version, header_size, width, height) = unsafe {
            (
                ptr::read(base.add(OFF_MAGIC) as *const u32),
                ptr::read(base.add(OFF_VERSION) as *const u16),
                ptr::read(base.add(OFF_HEADER_SIZE) as *const u16),
                ptr::read(base.add(OFF_WIDTH) as *const u32),
                ptr::read(base.add(OFF_HEIGHT) as *const u32),
            )
        };

        if magic != MAGIC {
            bail!("bad magic: 0x{:08x} (expected 0x{:08x})", magic, MAGIC);
        }
        if version != VERSION {
            bail!("unsupported version: {} (this reader speaks v{})", version, VERSION);
        }
        if header_size as usize != HEADER_SIZE {
            bail!("unexpected header_size: {}", header_size);
        }

        Ok(Self {
            _file: file,
            _mmap: mmap,
            base,
            width,
            height,
        })
    }

    pub fn width(&self) -> u32 {
        self.width
    }

    pub fn height(&self) -> u32 {
        self.height
    }

    /// Read the metadata fields of the current header (frame id,
    /// IOSurface id, IOSurface gen).
    ///
    /// Returns `None` on a torn read.
    pub fn read_header_snapshot(&self) -> Option<(u64, u32, u32)> {
        let base = self.base;
        unsafe {
            let seq = AtomicU64::from_ptr(base.add(OFF_SEQ) as *mut u64);
            let s1 = seq.load(Ordering::Acquire);
            if s1 & 1 == 1 {
                return None;
            }
            let frame_id = ptr::read_volatile(base.add(OFF_FRAME_ID) as *const u64);
            let io_id = ptr::read_volatile(base.add(OFF_IO_SURFACE_ID) as *const u32);
            let io_gen = ptr::read_volatile(base.add(OFF_IO_SURFACE_GEN) as *const u32);
            // Why: Acquire on the s2 load below is one-directional — it
            // does not stop the data loads above from being reordered
            // past it on ARM. Close that side explicitly so s1 == s2
            // genuinely implies the data loads didn't straddle a write.
            std::sync::atomic::fence(Ordering::Acquire);
            let s2 = seq.load(Ordering::Relaxed);
            if s2 != s1 {
                return None;
            }
            Some((frame_id, io_id, io_gen))
        }
    }
}

// ---------------------------------------------------------------------------
// Input ring buffer reader (sidecar side)
// ---------------------------------------------------------------------------

/// A single decoded input event from the ring buffer.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct InputEvent {
    pub kind: u8,
    pub button: u8,
    pub x: i32,
    pub y: i32,
    pub extra: i32,
}

/// Reader for the plugin→sidecar input ring buffer. The sidecar creates
/// this after opening the SHM file (which it also creates). The ring
/// lives in the same mmap as the frame header.
///
/// SPSC: plugin is the sole producer (writes `write_idx`), sidecar is the
/// sole consumer (writes `read_idx`).
pub struct InputRingReader {
    base: *mut u8,
}

// SAFETY: same as ShmWriter — pointer derived from mmap, lifetime tied
// to the mmap owner. We only expose this through methods that read/write
// at known offsets within the mapped region.
unsafe impl Send for InputRingReader {}

impl InputRingReader {
    /// Create a reader over a mutable mmap that includes the input region.
    /// The caller must ensure `base` points to a mapping of at least
    /// `SHM_FILE_SIZE` bytes and lives as long as this reader.
    pub unsafe fn new(base: *mut u8) -> Self {
        Self { base }
    }

    /// Drain all pending events, calling `f` for each. Returns the count
    /// of events consumed.
    pub fn drain(&self, mut f: impl FnMut(InputEvent)) -> usize {
        let base = self.base;
        let mut count = 0usize;
        unsafe {
            let write_idx_ptr = base.add(OFF_INPUT_WRITE_IDX) as *const u32;
            let read_idx_ptr = base.add(OFF_INPUT_READ_IDX) as *mut u32;

            let write_idx = std::sync::atomic::AtomicU32::from_ptr(write_idx_ptr as *mut u32)
                .load(Ordering::Acquire);
            let mut read_idx = ptr::read_volatile(read_idx_ptr);

            while read_idx != write_idx {
                let slot_idx = (read_idx as usize) % INPUT_RING_CAPACITY;
                let slot = base.add(OFF_INPUT_RING + slot_idx * INPUT_SLOT_SIZE);

                let event = InputEvent {
                    kind: ptr::read(slot.add(SLOT_OFF_TYPE)),
                    button: ptr::read(slot.add(SLOT_OFF_BUTTON)),
                    x: ptr::read(slot.add(SLOT_OFF_X) as *const i32),
                    y: ptr::read(slot.add(SLOT_OFF_Y) as *const i32),
                    extra: ptr::read(slot.add(SLOT_OFF_EXTRA) as *const i32),
                };
                f(event);
                count += 1;

                read_idx = read_idx.wrapping_add(1);
            }

            // Publish how far we've read so the producer knows it can
            // reuse those slots.
            std::sync::atomic::AtomicU32::from_ptr(read_idx_ptr)
                .store(read_idx, Ordering::Release);
        }
        count
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::layout::{INPUT_NAVIGATE, INPUT_NAVIGATE_CHUNK};
    use std::sync::atomic::{AtomicBool, AtomicU64 as StdAtomicU64, Ordering as O};
    use std::sync::Arc;
    use std::thread;
    use std::time::Duration;

    fn tmp_path(name: &str) -> PathBuf {
        let mut p = std::env::temp_dir();
        p.push(format!("dragonglass-test-{}-{}.shm", name, std::process::id()));
        p
    }

    #[test]
    fn header_roundtrip() {
        let path = tmp_path("header");
        let _ = std::fs::remove_file(&path);

        let w = ShmWriter::create(&path, 1920, 1200).expect("create");
        assert_eq!(w.width(), 1920);
        assert_eq!(w.height(), 1200);
        drop(w);

        let r = ShmReader::open(&path).expect("open");
        assert_eq!(r.width(), 1920);
        assert_eq!(r.height(), 1200);

        std::fs::remove_file(&path).ok();
    }

    #[test]
    fn write_header_only_publishes_io_surface_id() {
        let path = tmp_path("header-only");
        let _ = std::fs::remove_file(&path);

        let mut w = ShmWriter::create(&path, 4, 4).expect("create");
        let r = ShmReader::open(&path).expect("open");
        assert_eq!(r.read_header_snapshot(), Some((0, 0, 0)));

        w.write_header_only(0xAABBCCDD, 7);
        w.write_header_only(0xAABBCCDD, 7);
        assert_eq!(w.frame_id(), 2);

        let (frame_id, io_id, io_gen) = r.read_header_snapshot().expect("snapshot");
        assert_eq!(frame_id, 2);
        assert_eq!(io_id, 0xAABBCCDD);
        assert_eq!(io_gen, 7);

        std::fs::remove_file(&path).ok();
    }

    /// Two threads hammer the header for a fixed duration. Writer
    /// updates IOSurface ID/gen each frame, reader verifies consistency
    /// via seqlock. The real assertion is `torn == 0`.
    #[test]
    fn seqlock_no_torn_reads_under_contention() {
        let path = tmp_path("seqlock");
        let _ = std::fs::remove_file(&path);

        let w = ShmWriter::create(&path, 1920, 1200).expect("create");
        let r = ShmReader::open(&path).expect("open");

        let stop = Arc::new(AtomicBool::new(false));
        let frames_written = Arc::new(StdAtomicU64::new(0));
        let frames_observed = Arc::new(StdAtomicU64::new(0));
        let torn_reads = Arc::new(StdAtomicU64::new(0));

        let writer_handle = {
            let stop = stop.clone();
            let frames_written = frames_written.clone();
            thread::spawn(move || {
                let mut w = w;
                let mut frame: u64 = 0;
                while !stop.load(O::Relaxed) {
                    frame = frame.wrapping_add(1);
                    let id = (frame & 0xFFFF_FFFF) as u32;
                    let gen = frame as u32;
                    w.write_header_only(id, gen);
                    frames_written.fetch_add(1, O::Relaxed);
                    if frame % 32 == 0 {
                        thread::yield_now();
                    }
                }
            })
        };

        let reader_handle = {
            let stop = stop.clone();
            let frames_observed = frames_observed.clone();
            let torn_reads = torn_reads.clone();
            thread::spawn(move || {
                let mut last_frame_id: u64 = 0;
                while !stop.load(O::Relaxed) {
                    if let Some((frame_id, id, gen)) = r.read_header_snapshot() {
                        if frame_id > 0 {
                            // Verify consistency: id and gen should match
                            // the pattern the writer uses.
                            let expected_id = (frame_id & 0xFFFF_FFFF) as u32;
                            let expected_gen = frame_id as u32;
                            if id != expected_id || gen != expected_gen {
                                torn_reads.fetch_add(1, O::Relaxed);
                            }
                            if frame_id > last_frame_id {
                                last_frame_id = frame_id;
                                frames_observed.fetch_add(1, O::Relaxed);
                            }
                        }
                    } else {
                        std::hint::spin_loop();
                    }
                }
            })
        };

        thread::sleep(Duration::from_secs(2));
        stop.store(true, O::Relaxed);
        writer_handle.join().expect("writer join");
        reader_handle.join().expect("reader join");

        let written = frames_written.load(O::Relaxed);
        let observed = frames_observed.load(O::Relaxed);
        let torn = torn_reads.load(O::Relaxed);
        eprintln!(
            "seqlock stress: wrote {} frames, reader observed {}, torn {}",
            written, observed, torn
        );
        assert!(written > 10_000, "writer made almost no progress: {}", written);
        assert!(observed > 0, "reader observed nothing");
        assert_eq!(torn, 0, "observed {} torn reads out of {}", torn, observed);

        std::fs::remove_file(&path).ok();
    }

    /// Simulate the plugin's multi-slot navigate write (header slot
    /// declaring URL byte length, followed by chunk slots packing 12
    /// bytes each into x/y/extra) and verify the consumer reassembles
    /// the original URL byte-for-byte across drain calls.
    #[test]
    fn navigate_round_trip_through_ring() {
        let path = tmp_path("navigate");
        let _ = std::fs::remove_file(&path);

        let w = ShmWriter::create(&path, 4, 4).expect("create");
        let base = w.mmap_base();
        let reader = unsafe { InputRingReader::new(base) };

        let url = b"https://example.com/very/specific/path?query=1&x=abc";
        write_navigate(base, url);

        let mut nav: Option<(usize, Vec<u8>)> = None;
        let mut delivered: Vec<u8> = Vec::new();
        reader.drain(|evt| match evt.kind {
            INPUT_NAVIGATE => {
                nav = Some((evt.extra as usize, Vec::new()));
            }
            INPUT_NAVIGATE_CHUNK => {
                if let Some((expected, ref mut buf)) = nav {
                    let mut tmp = [0u8; 12];
                    tmp[0..4].copy_from_slice(&evt.x.to_le_bytes());
                    tmp[4..8].copy_from_slice(&evt.y.to_le_bytes());
                    tmp[8..12].copy_from_slice(&evt.extra.to_le_bytes());
                    let take = expected.saturating_sub(buf.len()).min(12);
                    buf.extend_from_slice(&tmp[..take]);
                    if buf.len() >= expected {
                        delivered = std::mem::take(buf);
                        nav = None;
                    }
                }
            }
            _ => {}
        });

        assert_eq!(delivered.as_slice(), url);

        // Consumer's read_idx caught up with producer's write_idx —
        // a second drain should see no events.
        let mut second_pass = 0;
        reader.drain(|_| second_pass += 1);
        assert_eq!(second_pass, 0);

        std::fs::remove_file(&path).ok();
    }

    /// Mirror of `ShmReader.WriteNavigate`: split `url` into a header
    /// slot (length in `extra`) plus 12-byte chunk slots, then bump
    /// write_idx once at the end so the consumer never observes a
    /// partial message.
    fn write_navigate(base: *mut u8, url: &[u8]) {
        let chunk_count = url.len().div_ceil(12);
        let slot_count = 1 + chunk_count;
        unsafe {
            let write_idx_ptr = base.add(OFF_INPUT_WRITE_IDX) as *mut u32;
            let read_idx_ptr = base.add(OFF_INPUT_READ_IDX) as *const u32;
            let write_idx = ptr::read(write_idx_ptr);
            let _read_idx = ptr::read(read_idx_ptr);
            assert!(slot_count <= INPUT_RING_CAPACITY);

            write_slot(base, write_idx, INPUT_NAVIGATE, 0, 0, 0, url.len() as i32);
            for i in 0..chunk_count {
                let off = i * 12;
                let take = (url.len() - off).min(12);
                let mut buf = [0u8; 12];
                buf[..take].copy_from_slice(&url[off..off + take]);
                let x = i32::from_le_bytes(buf[0..4].try_into().unwrap());
                let y = i32::from_le_bytes(buf[4..8].try_into().unwrap());
                let extra = i32::from_le_bytes(buf[8..12].try_into().unwrap());
                write_slot(
                    base,
                    write_idx + 1 + i as u32,
                    INPUT_NAVIGATE_CHUNK,
                    0,
                    x,
                    y,
                    extra,
                );
            }

            std::sync::atomic::fence(Ordering::Release);
            ptr::write(write_idx_ptr, write_idx + slot_count as u32);
        }
    }

    unsafe fn write_slot(
        base: *mut u8,
        abs_idx: u32,
        kind: u8,
        button: u8,
        x: i32,
        y: i32,
        extra: i32,
    ) {
        let slot_idx = (abs_idx as usize) % INPUT_RING_CAPACITY;
        let slot = base.add(OFF_INPUT_RING + slot_idx * INPUT_SLOT_SIZE);
        ptr::write(slot.add(SLOT_OFF_TYPE), kind);
        ptr::write(slot.add(SLOT_OFF_BUTTON), button);
        ptr::write(slot.add(SLOT_OFF_X) as *mut i32, x);
        ptr::write(slot.add(SLOT_OFF_Y) as *mut i32, y);
        ptr::write(slot.add(SLOT_OFF_EXTRA) as *mut i32, extra);
    }
}
