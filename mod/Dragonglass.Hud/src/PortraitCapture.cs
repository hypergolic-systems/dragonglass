// Mirrors stock KSP's `KerbalPortraitGallery` IVA portraits into the
// Dragonglass HUD's chroma-key punch-through pipeline.
//
// Each Update():
//   1. Walk `KerbalPortraitGallery.Instance.Portraits`.
//   2. For each portrait whose `crewMember.avatarTexture` is live,
//      copy the RT into a reusable `Texture2D(256, 256, RGBA32)` via
//      `ReadPixels`, get the raw RGBA bytes, and push them to the
//      native plugin's stream registry under id `kerbal:<name>`.
//   3. Unregister streams whose Kerbal disappeared this frame
//      (vessel switch, EVA, transfer, death).
//
// The HUD UI mounts a `<PunchThrough id="kerbal:<name>">` per portrait
// (driven by the `PortraitsTopic` telemetry list) — wherever it is on
// the screen, the native compositor's chroma-key shader composites
// that registered RGBA texture under the chroma color CEF painted
// there. End result: the IVA face from the stock camera renders
// inside our HUD layout, with HTML overlays (name plate, status,
// hover affordances) untouched.
//
// We use synchronous `Texture2D.ReadPixels` rather than
// `AsyncGPUReadback` for v1: ~3 portraits at 256² is microseconds of
// GPU stall and avoids the lifecycle complexity of pending async
// callbacks across vessel/scene transitions. Switch to
// `AsyncGPUReadback` if/when the cost shows up in profiling.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KSP.UI.Screens.Flight;
using UnityEngine;

namespace Dragonglass.Hud
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class PortraitCapture : MonoBehaviour
    {
        private const string LogPrefix = "[Dragonglass/Portraits] ";

        // `Kerbal.avatarTexture` is hard-coded to 256×256 in stock KSP
        // (see `Kerbal.cs`'s `RenderTexture(256, 256, 24)`). If that
        // ever changes we'll resize on the fly, but bake the default
        // in here so first-frame allocation matches.
        private const int PortraitSize = 256;

        // Stream id-hashes we have registered with the native plugin
        // this Flight session. On scene unload we iterate this set and
        // call `DgHudNative_RemoveStream` for each — the plugin's
        // texture cache leaks otherwise (Unity's GL context dies, but
        // our plugin map outlives it).
        private readonly HashSet<uint> _registered = new HashSet<uint>();

        // Reusable readback target. Allocated once, reused every
        // frame. RGBA32 matches both the stock RT format and the
        // plugin's `DgHudNative_PushStreamFrame` byte-order contract.
        private Texture2D _readbackTex;

        // Per-frame scratch set for "what did I see this frame", used
        // to detect newly-disappeared streams and unregister them.
        // Field rather than local to avoid a per-frame allocation.
        private readonly HashSet<uint> _seenThisFrame = new HashSet<uint>();
        private readonly List<uint> _toRemove = new List<uint>();

        private void Start()
        {
            _readbackTex = new Texture2D(PortraitSize, PortraitSize, TextureFormat.RGBA32, mipChain: false, linear: false);
            _readbackTex.hideFlags = HideFlags.HideAndDontSave;
            Debug.Log(LogPrefix + "addon active (Flight scene)");
        }

        private void OnDestroy()
        {
            foreach (uint hash in _registered)
            {
                try { NativeBridge.DgHudNative_RemoveStream(hash); }
                catch (Exception) { /* plugin may already be torn down */ }
            }
            _registered.Clear();
            if (_readbackTex != null)
            {
                UnityEngine.Object.Destroy(_readbackTex);
                _readbackTex = null;
            }
        }

        private void Update()
        {
            KerbalPortraitGallery gallery = KerbalPortraitGallery.Instance;
            if (gallery == null || gallery.Portraits == null) return;

            _seenThisFrame.Clear();

            for (int i = 0; i < gallery.Portraits.Count; i++)
            {
                KerbalPortrait portrait = gallery.Portraits[i];
                if (portrait == null) continue;
                Kerbal kerbal = portrait.crewMember;
                if (kerbal == null) continue;
                RenderTexture rt = kerbal.avatarTexture;
                if (rt == null) continue;
                ProtoCrewMember pcm = portrait.crewPcm;
                if (pcm == null || string.IsNullOrEmpty(pcm.name)) continue;

                string streamId = "kerbal:" + pcm.name;
                uint hash = Fnv1a32(streamId);
                _seenThisFrame.Add(hash);
                CapturePortrait(rt, hash);
            }

            // Drop streams whose Kerbal is no longer in the gallery.
            _toRemove.Clear();
            foreach (uint hash in _registered)
            {
                if (!_seenThisFrame.Contains(hash)) _toRemove.Add(hash);
            }
            for (int i = 0; i < _toRemove.Count; i++)
            {
                NativeBridge.DgHudNative_RemoveStream(_toRemove[i]);
                _registered.Remove(_toRemove[i]);
            }
        }

        private void CapturePortrait(RenderTexture rt, uint idHash)
        {
            // ReadPixels samples from whatever's currently bound as
            // the active RenderTexture. Save the previous binding so
            // we don't disturb anyone else's draw.
            RenderTexture prev = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                _readbackTex.ReadPixels(new Rect(0, 0, PortraitSize, PortraitSize), 0, 0, recalculateMipMaps: false);
                _readbackTex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            }
            finally
            {
                RenderTexture.active = prev;
            }

            // GetRawTextureData<byte>() returns a NativeArray over the
            // texture's native storage — zero-alloc and zero-copy.
            // The plugin's PushStreamFrame memcpy's the bytes under a
            // mutex before returning, so the pointer is only needed
            // for the duration of the call.
            unsafe
            {
                Unity.Collections.NativeArray<byte> raw = _readbackTex.GetRawTextureData<byte>();
                IntPtr ptr = (IntPtr)Unity.Collections.LowLevel.Unsafe
                    .NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(raw);
                NativeBridge.DgHudNative_PushStreamFrame(idHash, PortraitSize, PortraitSize, ptr);
            }
            _registered.Add(idHash);
        }

        /// <summary>
        /// FNV-1a 32-bit hash of the UTF-8 byte sequence. Mirrors
        /// `fnv1a_32` in `crates/dg-sidecar/src/streams.rs` and the
        /// macOS plugin so a stream id like <c>"kerbal:Jeb"</c>
        /// produces the same 32-bit hash on every side of the wire.
        /// </summary>
        private static uint Fnv1a32(string s)
        {
            uint h = 0x811C9DC5u;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s);
            for (int i = 0; i < bytes.Length; i++)
            {
                h ^= bytes[i];
                h *= 0x01000193u;
            }
            return h;
        }
    }
}
