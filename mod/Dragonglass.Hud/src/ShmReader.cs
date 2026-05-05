// Plain-C# reader/writer for the Dragonglass shared-memory format.
//
// Reads the seqlock frame header written by the Rust `ShmWriter` and
// writes into the SPSC input ring drained by the Rust
// `InputRingReader`. Uses `MemoryMappedFile` + unsafe pointer reads
// so we can atomically observe the counters without depending on
// `Volatile.Read` overloads that may or may not exist in KSP's
// bundled Mono.

using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Dragonglass.Hud
{
    /// <summary>
    /// Opens the shared file written by the sidecar and exposes a
    /// seqlock-safe per-frame read into a caller-owned staging buffer.
    /// </summary>
    public sealed class ShmReader : IDisposable
    {
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;

        public int Width { get; }
        public int Height { get; }
        public int Stride { get; }
        public string Path { get; }

        private ShmReader(
            string path,
            MemoryMappedFile mmf,
            MemoryMappedViewAccessor accessor,
            int width,
            int height,
            int stride)
        {
            Path = path;
            _mmf = mmf;
            _accessor = accessor;
            Width = width;
            Height = height;
            Stride = stride;
        }

        /// <summary>
        /// Location of the shared file written by the sidecar, scoped
        /// to this KSP instance's session ID so multiple instances
        /// don't collide.
        /// </summary>
        public static string DefaultPath()
        {
            string dir = Environment.GetEnvironmentVariable("TMPDIR");
            if (string.IsNullOrEmpty(dir))
            {
                dir = System.IO.Path.GetTempPath();
            }
            return System.IO.Path.Combine(dir,
                "dragonglass-" + SidecarHost.SessionId + ".shm");
        }

        /// <summary>
        /// Open the shared file at <paramref name="path"/> for reading,
        /// validate its header, and return a ready-to-use reader.
        /// Throws on missing file, wrong magic, wrong version, or
        /// mismatched dimensions.
        /// </summary>
        public static ShmReader Open(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("shm file not found", path);
            }

            // ReadWrite so we can write input events into the ring buffer
            // region while reading the frame header. The sidecar creates
            // the file; we just open it.
            //
            // Explicit FileStream with FileShare.ReadWrite | Delete is
            // required on Windows: the sidecar has the file mmapped
            // with write access, and the parameterized MMF.CreateFromFile
            // overload internally opens with FileShare.Read, which
            // conflicts with the sidecar's existing handle and throws
            // "Could not open file". macOS doesn't enforce share modes
            // so the simpler overload was fine there.
            FileStream fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            MemoryMappedFile mmf;
            try
            {
                mmf = MemoryMappedFile.CreateFromFile(
                    fs,
                    mapName: null,
                    capacity: 0,
                    access: MemoryMappedFileAccess.ReadWrite,
                    inheritability: HandleInheritability.None,
                    leaveOpen: false);
            }
            catch
            {
                fs.Dispose();
                throw;
            }

            MemoryMappedViewAccessor accessor = null;
            try
            {
                accessor = mmf.CreateViewAccessor(
                    offset: 0,
                    size: 0,
                    access: MemoryMappedFileAccess.ReadWrite);

                uint magic = accessor.ReadUInt32(Layout.OffMagic);
                if (magic != Layout.Magic)
                {
                    throw new InvalidDataException(
                        "bad magic: 0x" + magic.ToString("x8") +
                        " (expected 0x" + Layout.Magic.ToString("x8") + ")");
                }

                ushort version = accessor.ReadUInt16(Layout.OffVersion);
                if (version != Layout.Version)
                {
                    throw new InvalidDataException(
                        "unsupported version " + version +
                        " (this reader speaks v" + Layout.Version + ")");
                }

                ushort headerSize = accessor.ReadUInt16(Layout.OffHeaderSize);
                if (headerSize != Layout.HeaderSize)
                {
                    throw new InvalidDataException(
                        "unexpected header_size " + headerSize);
                }

                int width = (int)accessor.ReadUInt32(Layout.OffWidth);
                int height = (int)accessor.ReadUInt32(Layout.OffHeight);
                int stride = (int)accessor.ReadUInt32(Layout.OffStride);
                uint format = accessor.ReadUInt32(Layout.OffFormat);

                if (format != Layout.FormatBgra8Premul)
                {
                    throw new InvalidDataException(
                        "unsupported format code " + format);
                }
                if (width <= 0 || height <= 0)
                {
                    throw new InvalidDataException(
                        "bad dimensions " + width + "x" + height);
                }
                if (stride != width * 4)
                {
                    throw new InvalidDataException(
                        "unexpected stride " + stride + " for width " + width);
                }

                if (accessor.Capacity < Layout.ShmFileSize)
                {
                    throw new InvalidDataException(
                        "file too small: " + accessor.Capacity +
                        " bytes (need " + Layout.ShmFileSize + " for v" +
                        Layout.Version + " layout)");
                }

                ShmReader reader = new ShmReader(path, mmf, accessor, width, height, stride);
                accessor = null;
                mmf = null;
                return reader;
            }
            finally
            {
                if (accessor != null) accessor.Dispose();
                if (mmf != null) mmf.Dispose();
            }
        }

        /// <summary>
        /// Small POD struct returned by <see cref="TryReadHeader"/>.
        /// Contains everything the zero-copy path needs to decide
        /// whether to rebind the external texture.
        /// </summary>
        public struct HeaderSnapshot
        {
            public long FrameId;
            public uint IoSurfaceId;
            public uint IoSurfaceGen;
        }

        /// <summary>
        /// Read just the metadata fields of the current header under
        /// the seqlock protocol — never touches the payload region.
        /// Used by the zero-copy pixel path: the caller looks at
        /// <c>IoSurfaceId</c> / <c>IoSurfaceGen</c> to decide whether
        /// to call the native rendering plugin for a new MTLTexture
        /// rebind, without ever memcpying pixel bytes.
        /// </summary>
        /// <returns>
        /// true if the snapshot is consistent; false on a torn read
        /// or when the writer is mid-update (caller retries next tick).
        /// </returns>
        public bool TryReadHeader(out HeaderSnapshot snap)
        {
            snap = default(HeaderSnapshot);
            unsafe
            {
                byte* basePtr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                try
                {
                    long* seqPtr = (long*)(basePtr + Layout.OffSeq);
                    long* frameIdPtr = (long*)(basePtr + Layout.OffFrameId);
                    uint* ioIdPtr = (uint*)(basePtr + Layout.OffIoSurfaceId);
                    uint* ioGenPtr = (uint*)(basePtr + Layout.OffIoSurfaceGen);

                    long s1 = *seqPtr;
                    System.Threading.Thread.MemoryBarrier();
                    if ((s1 & 1L) == 1L) return false;
                    if (s1 == 0L) return false;

                    long frameId = *frameIdPtr;
                    uint ioId = *ioIdPtr;
                    uint ioGen = *ioGenPtr;

                    System.Threading.Thread.MemoryBarrier();
                    long s2 = *seqPtr;
                    if (s2 != s1) return false;

                    snap.FrameId = frameId;
                    snap.IoSurfaceId = ioId;
                    snap.IoSurfaceGen = ioGen;
                    return true;
                }
                finally
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }

        /// <summary>
        /// Read the sidecar-published punch-through stream-rect table
        /// under its dedicated u32 seqlock at byte 4096. Up to
        /// <see cref="Layout.StreamRectCapacity"/> slots; the buffer
        /// `slots` must be at least that long. Returns the active count
        /// (0 if torn / pre-write).
        /// </summary>
        public int TryReadStreamRects(byte[] slots)
        {
            if (slots == null) return 0;
            int needed = Layout.StreamRectCapacity * Layout.StreamSlotSize;
            if (slots.Length < needed) return 0;

            unsafe
            {
                byte* basePtr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                try
                {
                    uint* seqPtr = (uint*)(basePtr + Layout.OffStreamSeq);
                    uint* countPtr = (uint*)(basePtr + Layout.OffStreamCount);

                    uint s1 = *seqPtr;
                    System.Threading.Thread.MemoryBarrier();
                    if ((s1 & 1u) == 1u) return 0;

                    int count = (int)(*countPtr);
                    if (count < 0 || count > Layout.StreamRectCapacity) return 0;

                    int copyBytes = count * Layout.StreamSlotSize;
                    if (copyBytes > 0)
                    {
                        byte* src = basePtr + Layout.OffStreamRects;
                        fixed (byte* dst = slots)
                        {
                            Buffer.MemoryCopy(src, dst, slots.Length, copyBytes);
                        }
                    }

                    System.Threading.Thread.MemoryBarrier();
                    uint s2 = *seqPtr;
                    if (s2 != s1) return 0;

                    return count;
                }
                finally
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }

        /// <summary>
        /// Read the sidecar-published "CEF wants keyboard" flag. True
        /// when a CEF editable element is currently focused, so the
        /// plugin should apply a <c>ControlTypes.KEYBOARDINPUT</c>
        /// input lock and forward keys to the sidecar instead of
        /// letting KSP shortcut keys fire. Outside the seqlock — a
        /// plain volatile read is sufficient.
        /// </summary>
        public bool ReadCefWantsKeyboard()
        {
            unsafe
            {
                byte* basePtr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                try
                {
                    uint* flagPtr = (uint*)(basePtr + Layout.OffCefWantsKeyboard);
                    uint v = *flagPtr;
                    System.Threading.Thread.MemoryBarrier();
                    return v != 0;
                }
                finally
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }

        /// <summary>
        /// Write a single input event into the SPSC ring buffer. The
        /// plugin is the sole producer; the sidecar is the sole
        /// consumer. No-op if the ring is full.
        /// </summary>
        public void WriteInputEvent(byte type, byte button, int x, int y, int extra = 0)
        {
            unsafe
            {
                byte* basePtr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                try
                {
                    uint* writeIdxPtr = (uint*)(basePtr + Layout.OffInputWriteIdx);
                    uint* readIdxPtr = (uint*)(basePtr + Layout.OffInputReadIdx);

                    uint writeIdx = *writeIdxPtr;
                    System.Threading.Thread.MemoryBarrier();
                    uint readIdx = *readIdxPtr;

                    // Ring full — drop the event.
                    if (writeIdx - readIdx >= (uint)Layout.InputRingCapacity)
                        return;

                    int slotIdx = (int)(writeIdx % (uint)Layout.InputRingCapacity);
                    byte* slot = basePtr + Layout.OffInputRing +
                                 slotIdx * Layout.InputSlotSize;

                    *(slot + Layout.SlotOffType) = type;
                    *(slot + Layout.SlotOffButton) = button;
                    *(short*)(slot + 2) = 0; // reserved
                    *(int*)(slot + Layout.SlotOffX) = x;
                    *(int*)(slot + Layout.SlotOffY) = y;
                    *(int*)(slot + Layout.SlotOffExtra) = extra;

                    // Release-store the new write index so the consumer
                    // sees the slot contents before the index advances.
                    System.Threading.Thread.MemoryBarrier();
                    *writeIdxPtr = writeIdx + 1;
                }
                finally
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }

        /// <summary>
        /// Encode <paramref name="url"/> as UTF-8 and write a multi-slot
        /// <see cref="Layout.InputNavigate"/> message into the ring: one
        /// header slot followed by <c>ceil(byteLen / 12)</c> chunk slots
        /// packing the URL bytes into the x / y / extra fields. The
        /// write_idx bump happens once at the end so the sidecar never
        /// observes a partial URL. Returns false (and logs) if the URL
        /// is null/empty, exceeds <see cref="Layout.MaxNavUrlBytes"/>,
        /// or the ring lacks contiguous capacity for the whole message.
        /// </summary>
        public bool WriteNavigate(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;

            byte[] bytes = Encoding.UTF8.GetBytes(url);
            if (bytes.Length > Layout.MaxNavUrlBytes) return false;

            const int BytesPerChunk = 12;
            int chunkCount = (bytes.Length + BytesPerChunk - 1) / BytesPerChunk;
            int slotCount = 1 + chunkCount;
            if (slotCount > Layout.InputRingCapacity) return false;

            unsafe
            {
                byte* basePtr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                try
                {
                    uint* writeIdxPtr = (uint*)(basePtr + Layout.OffInputWriteIdx);
                    uint* readIdxPtr = (uint*)(basePtr + Layout.OffInputReadIdx);

                    uint writeIdx = *writeIdxPtr;
                    System.Threading.Thread.MemoryBarrier();
                    uint readIdx = *readIdxPtr;

                    // Ring lacks room for all N+1 slots. SPSC requires
                    // we not split the message, so drop the whole thing.
                    if (writeIdx - readIdx + (uint)slotCount > (uint)Layout.InputRingCapacity)
                        return false;

                    // Header slot: type, length-in-extra, rest zero.
                    WriteSlot(basePtr, writeIdx,
                        Layout.InputNavigate, 0, 0, 0, bytes.Length);

                    // Chunk slots: 12 bytes packed into x/y/extra each.
                    // Final chunk's tail past byteLen stays zero so the
                    // sidecar's length-driven trim drops the padding.
                    fixed (byte* src = bytes)
                    {
                        for (int i = 0; i < chunkCount; i++)
                        {
                            int srcOff = i * BytesPerChunk;
                            int remaining = bytes.Length - srcOff;
                            int copy = remaining < BytesPerChunk ? remaining : BytesPerChunk;

                            int x = 0, y = 0, extra = 0;
                            byte* xp = (byte*)&x;
                            byte* yp = (byte*)&y;
                            byte* ep = (byte*)&extra;
                            for (int b = 0; b < copy; b++)
                            {
                                byte v = src[srcOff + b];
                                if (b < 4) xp[b] = v;
                                else if (b < 8) yp[b - 4] = v;
                                else ep[b - 8] = v;
                            }

                            WriteSlot(basePtr, writeIdx + 1u + (uint)i,
                                Layout.InputNavigateChunk, 0, x, y, extra);
                        }
                    }

                    // Release-store the bumped index so the consumer
                    // sees every slot before the index advances.
                    System.Threading.Thread.MemoryBarrier();
                    *writeIdxPtr = writeIdx + (uint)slotCount;
                }
                finally
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
            return true;
        }

        private static unsafe void WriteSlot(
            byte* basePtr, uint absIdx, byte type, byte button, int x, int y, int extra)
        {
            int slotIdx = (int)(absIdx % (uint)Layout.InputRingCapacity);
            byte* slot = basePtr + Layout.OffInputRing + slotIdx * Layout.InputSlotSize;

            *(slot + Layout.SlotOffType) = type;
            *(slot + Layout.SlotOffButton) = button;
            *(short*)(slot + 2) = 0; // reserved
            *(int*)(slot + Layout.SlotOffX) = x;
            *(int*)(slot + Layout.SlotOffY) = y;
            *(int*)(slot + Layout.SlotOffExtra) = extra;
        }

        public void Dispose()
        {
            if (_accessor != null)
            {
                _accessor.Dispose();
                _accessor = null;
            }
            if (_mmf != null)
            {
                _mmf.Dispose();
                _mmf = null;
            }
        }
    }
}
