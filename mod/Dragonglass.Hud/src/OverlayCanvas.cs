// Top-most UGUI overlay that displays a Texture2D stretched to the
// full screen. Created by DragonglassHudAddon on scene entry;
// destroyed with the addon. The native rendering plugin blits CEF's
// IOSurface directly into this texture's native handle each frame —
// the managed side never touches pixel bytes in the steady state.

using System;
using UnityEngine;
using UnityEngine.UI;

namespace Dragonglass.Hud
{
    /// <summary>
    /// Owns a screen-space overlay Canvas, a full-screen RawImage, and
    /// the backing Texture2D. Does not depend on `ShmReader`.
    /// </summary>
    public sealed class OverlayCanvas : IDisposable
    {
        // sortingOrder has no explicit upper bound in stock KSP, but
        // short.MaxValue sits above every stock canvas we've inspected.
        private const int TopMostSortingOrder = short.MaxValue;

        private GameObject _canvasGo;
        private GameObject _imageGo;
        private Canvas _canvas;
        private RawImage _rawImage;
        private Texture2D _texture;
        private HudRaycastFilter _raycastFilter;

        public int Width { get; }
        public int Height { get; }
        public Texture2D Texture { get { return _texture; } }

        /// <summary>
        /// Toggle the canvas's <see cref="Canvas.enabled"/> flag. Used
        /// by the HUD addon to hide the overlay entirely while KSP's
        /// inter-scene loading mask is up, so Unity doesn't composite
        /// a stale CEF frame over the planet spinner before CEF has a
        /// chance to publish a fresh one.
        /// </summary>
        public bool Visible
        {
            get { return _canvas != null && _canvas.enabled; }
            set { if (_canvas != null) _canvas.enabled = value; }
        }

        public OverlayCanvas(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException("width/height");
            Width = width;
            Height = height;

            _canvasGo = new GameObject("Dragonglass.Hud.Overlay");
            // HUD addon persists across scenes; its canvas must too,
            // otherwise Unity drops the overlay GameObject on every
            // scene transition.
            UnityEngine.Object.DontDestroyOnLoad(_canvasGo);
            _canvas = _canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = TopMostSortingOrder;
            // GraphicRaycaster is what makes the EventSystem actually ask
            // this canvas's raycast targets + filters whether they were
            // hit. Without it, `raycastTarget = true` on the RawImage is
            // silently ignored and every click falls through to KSP.
            _canvasGo.AddComponent<GraphicRaycaster>();
            // No CanvasScaler — we want the RawImage to fill the physical
            // screen in pixels, and scaling is effectively controlled by
            // the anchors below (stretch to full screen).

            _imageGo = new GameObject("Dragonglass.Hud.Overlay.Image");
            _imageGo.transform.SetParent(_canvasGo.transform, worldPositionStays: false);

            RectTransform rect = _imageGo.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            // RGBA32, not BGRA32 — the zero-copy blit path in DgHudNative
            // wraps the incoming IOSurface with CGLTexImageIOSurface2D's
            // internal format = GL_RGBA, so the source texture's canonical
            // byte order is RGBA. Unity's glBlitFramebuffer then does a
            // byte-for-byte copy to this destination; declaring this
            // Texture2D as BGRA32 causes Unity to render the bytes as if
            // they were BGRA, swapping R and B on display.
            _texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false, linear: false);
            _texture.filterMode = FilterMode.Point;
            _texture.wrapMode = TextureWrapMode.Clamp;

            _rawImage = _imageGo.AddComponent<RawImage>();
            _rawImage.texture = _texture;
            // Hit-test the overlay, but delegate per-pixel opacity to
            // `HudRaycastFilter`: opaque pixels block Unity's EventSystem
            // (so a click on a HUD button doesn't also toggle the KSP
            // widget behind us), transparent pixels pass the raycast
            // through (so camera drag and stock UI stay usable wherever
            // the HUD isn't painting). Mouse events we forward to CEF
            // are still sampled directly from `Input.mousePosition` and
            // shipped via the SHM input ring, not through UGUI.
            _rawImage.raycastTarget = true;
            _raycastFilter = _imageGo.AddComponent<HudRaycastFilter>();
            _raycastFilter.Configure(width, height);
            // Vertical flip via UV remap. CEF's OnPaint delivers BGRA
            // bytes in top-down row order (Y=0 at top), but Unity's
            // Texture2D memory layout is bottom-up (Y=0 at bottom). Left
            // uncorrected this reads as "mirrored text" because every
            // glyph gets vertically flipped. A negative-height uvRect
            // sampled from V=1 downward inverts the Y axis at the
            // shader with zero data copy.
            _rawImage.uvRect = new Rect(0f, 1f, 1f, -1f);
        }

        /// <summary>
        /// Publish the current canvas IOSurface ID to the raycast
        /// filter so it can sample alpha at the cursor. Called from
        /// the addon each frame alongside the zero-copy rebind.
        /// </summary>
        public void SetRaycastSurface(uint ioSurfaceId)
        {
            if (_raycastFilter != null)
                _raycastFilter.SetSurface(ioSurfaceId);
        }

        /// <summary>
        /// Native-backed handle (GLuint or MTLTexture*) for the current
        /// backing texture. The native plugin uses this as the
        /// destination of its GPU blit from the CEF-owned IOSurface.
        /// </summary>
        public IntPtr GetNativeTexturePtr()
        {
            if (_texture == null) return IntPtr.Zero;
            return _texture.GetNativeTexturePtr();
        }

        /// <summary>
        /// Upload raw BGRA8 bytes into the texture and commit. The
        /// buffer must be exactly <c>Width * Height * 4</c> bytes long.
        /// </summary>
        public void ApplyBgra(byte[] bgra)
        {
            if (bgra == null) throw new ArgumentNullException("bgra");
            int expected = Width * Height * 4;
            if (bgra.Length != expected)
            {
                throw new ArgumentException(
                    "bgra buffer is " + bgra.Length + " bytes, expected " + expected);
            }
            _texture.LoadRawTextureData(bgra);
            _texture.Apply(updateMipmaps: false);
        }

        public void Dispose()
        {
            _rawImage = null;
            _raycastFilter = null;
            _canvas = null;
            if (_imageGo != null)
            {
                UnityEngine.Object.Destroy(_imageGo);
                _imageGo = null;
            }
            if (_canvasGo != null)
            {
                UnityEngine.Object.Destroy(_canvasGo);
                _canvasGo = null;
            }
            if (_texture != null)
            {
                UnityEngine.Object.Destroy(_texture);
                _texture = null;
            }
        }
    }
}
