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
            MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(
                path,
                FileMode.Open,
                mapName: null,
                capacity: 0,
                access: MemoryMappedFileAccess.ReadWrite);

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
