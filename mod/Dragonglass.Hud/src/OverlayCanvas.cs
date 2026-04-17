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
        private RawImage _rawImage;
        private Texture2D _texture;

        public int Width { get; }
        public int Height { get; }
        public Texture2D Texture { get { return _texture; } }

        public OverlayCanvas(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException("width/height");
            Width = width;
            Height = height;

            _canvasGo = new GameObject("Dragonglass.Hud.Overlay");
            Canvas canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = TopMostSortingOrder;
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

            _texture = new Texture2D(width, height, TextureFormat.BGRA32, mipChain: false, linear: false);
            _texture.filterMode = FilterMode.Point;
            _texture.wrapMode = TextureWrapMode.Clamp;

            _rawImage = _imageGo.AddComponent<RawImage>();
            _rawImage.texture = _texture;
            // Don't hit-test the overlay: a full-screen RawImage with
            // raycastTarget=true silently swallows every click/drag that
            // Unity's EventSystem would otherwise route to the flight
            // camera controller, making right-click-drag (camera look)
            // appear dead on any frame where the cursor sits inside the
            // HUD. Mouse events we *want* to forward to CEF are sampled
            // directly from `Input.mousePosition` and shipped via the
            // SHM input ring, not through UGUI.
            _rawImage.raycastTarget = false;
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
