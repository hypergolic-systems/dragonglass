// Shared-memory header + input ring layout for the Dragonglass v3
// IPC contract.
//
// Source of truth: docs/ipc.md. This file MUST stay byte-for-byte in
// lockstep with crates/dg-shm/src/layout.rs. Every offset, every
// magic, every enum value appears in both places.

namespace Dragonglass.Hud
{
    internal static class Layout
    {
        /// <summary>ASCII "DGLS" little-endian.</summary>
        public const uint Magic = 0x534C_4744;

        /// <summary>
        /// Protocol version. v3 is the first version shipped under the
        /// Dragonglass name — 4096-byte file, seqlock header + SPSC
        /// input ring buffer at offset 128.
        /// </summary>
        public const ushort Version = 3;

        /// <summary>Fixed header size in bytes.</summary>
        public const int HeaderSize = 128;

        /// <summary>Total file size (one 4 KiB page).</summary>
        public const int ShmFileSize = 4096;

        /// <summary>Pixel format code: BGRA8 premultiplied alpha, origin top-left.</summary>
        public const uint FormatBgra8Premul = 1;

        // --- Header field byte offsets (mirrored in sidecar/src/layout.rs) ---
        public const int OffMagic = 0;
        public const int OffVersion = 4;
        public const int OffHeaderSize = 6;
        public const int OffWidth = 8;
        public const int OffHeight = 12;
        public const int OffStride = 16;
        public const int OffFormat = 20;
        public const int OffSeq = 24;
        public const int OffFrameId = 32;
        // Bytes 40–55: reserved (formerly dirty-rect fields, unused).
        /// <summary>
        /// IOSurfaceID of the most recently committed frame.
        /// </summary>
        public const int OffIoSurfaceId = 56;
        /// <summary>
        /// Monotonic counter bumped each time the IOSurfaceID changes.
        /// Lets the plugin distinguish "same surface, next frame" from
        /// "new surface, rebind external texture".
        /// </summary>
        public const int OffIoSurfaceGen = 60;
        /// <summary>
        /// u32 flag written by the sidecar when a CEF editable element
        /// gains or loses focus (0 = no editable focused, 1 = focused).
        /// The plugin polls this each frame and toggles a
        /// <c>ControlTypes.KEYBOARDINPUT</c> lock so KSP shortcut keys
        /// don't fire while the user is typing into a web input.
        /// Outside the seqlock — plain atomic load is sufficient.
        /// </summary>
        public const int OffCefWantsKeyboard = 64;

        // --- Input event ring buffer (v3, plugin → sidecar) ---

        /// <summary>Byte offset of the producer write index (u32).</summary>
        public const int OffInputWriteIdx = 128;
        /// <summary>Byte offset of the consumer read index (u32).</summary>
        public const int OffInputReadIdx = 132;
        /// <summary>Byte offset where ring slots begin.</summary>
        public const int OffInputRing = 160;
        /// <summary>Size of one input event slot in bytes.</summary>
        public const int InputSlotSize = 16;
        /// <summary>Number of slots in the ring.</summary>
        public const int InputRingCapacity = 240;

        // Slot layout (16 bytes each):
        //   byte 0:    type  (u8)
        //   byte 1:    button (u8)
        //   byte 2-3:  reserved
        //   byte 4-7:  x (i32)
        //   byte 8-11: y (i32)
        //   byte 12-15: extra (i32) — wheel delta, etc.
        public const int SlotOffType = 0;
        public const int SlotOffButton = 1;
        public const int SlotOffX = 4;
        public const int SlotOffY = 8;
        public const int SlotOffExtra = 12;

        // Input event type codes (u8).
        public const byte InputMouseMove = 1;
        public const byte InputMouseDown = 2;
        public const byte InputMouseUp = 3;
        public const byte InputMouseWheel = 4;
        /// <summary>
        /// Plugin is asking the sidecar to resize its CEF viewport +
        /// backing IOSurface. `x` carries the new width, `y` the new
        /// height (both positive i32 pixel counts). Button stays
        /// <see cref="InputBtnNone"/>.
        /// </summary>
        public const byte InputResize = 5;
        /// <summary>
        /// Plugin is asking the sidecar to navigate the main frame to
        /// a new URL. This event spans multiple ring slots: one header
        /// slot of this type carrying the UTF-8 byte length in
        /// <c>extra</c>, followed by <c>ceil(byte_len / 12)</c>
        /// <see cref="InputNavigateChunk"/> slots that pack URL bytes
        /// into the x / y / extra fields. The producer reserves all
        /// slots and bumps <c>write_idx</c> once.
        /// </summary>
        public const byte InputNavigate = 6;
        /// <summary>
        /// Continuation slot for <see cref="InputNavigate"/>. Carries
        /// 12 raw URL bytes in the x / y / extra fields. Final chunk
        /// zero-pads any unused tail bytes.
        /// </summary>
        public const byte InputNavigateChunk = 7;
        /// <summary>
        /// Raw key-down event. <c>x</c> is the Windows virtual-key
        /// code (VK_BACK = 8, VK_ESCAPE = 27, VK_LEFT = 37, …);
        /// <c>y</c> is a packed modifier bitmask (see
        /// <see cref="KeyModShift"/> etc.); <c>extra</c> unused.
        /// Mapped to CEF <c>KEYEVENT_RAWKEYDOWN</c> — drives DOM
        /// <c>keydown</c>.
        /// </summary>
        public const byte InputKeyDown = 8;
        /// <summary>
        /// Raw key-up event. Same slot encoding as
        /// <see cref="InputKeyDown"/>. Mapped to CEF
        /// <c>KEYEVENT_KEYUP</c>.
        /// </summary>
        public const byte InputKeyUp = 9;
        /// <summary>
        /// Typed character forwarded to CEF as KEYEVENT_CHAR. The low
        /// 16 bits of <c>extra</c> carry a single UTF-16 code unit;
        /// x / y / button are unused. Emitted alongside
        /// <see cref="InputKeyDown"/> when the press produces text.
        /// </summary>
        public const byte InputKeyChar = 10;

        // Key-event modifier bitmask (packed into `y` on
        // InputKeyDown / InputKeyUp).
        public const int KeyModShift = 1 << 0;
        public const int KeyModControl = 1 << 1;
        public const int KeyModAlt = 1 << 2;
        public const int KeyModMeta = 1 << 3;

        /// <summary>
        /// Hard cap on the URL byte length a single InputNavigate
        /// message may carry. Mirrors MAX_NAV_URL_BYTES on the Rust
        /// side.
        /// </summary>
        public const int MaxNavUrlBytes = 2048;

        // Input button codes (u8).
        public const byte InputBtnNone = 0;
        public const byte InputBtnLeft = 1;
        public const byte InputBtnRight = 2;
        public const byte InputBtnMiddle = 3;
    }
}
