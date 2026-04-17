// Alpha-aware raycast blocker for the Dragonglass HUD overlay.
//
// The overlay is a full-screen RawImage composited over KSP's Flight
// scene. With a plain `raycastTarget = true`, Unity's EventSystem
// would block every click and drag across the entire screen — even
// on the 95% of the HUD that's transparent — killing KSP's camera
// drag and any click on the stock UI behind us. With `raycastTarget
// = false`, clicks pass through everywhere — including on our own
// buttons, which then *also* toggle whatever KSP widget happens to
// sit underneath (surface/sea-level altitude toggle, for example).
//
// Solution: `raycastTarget = true` with an `ICanvasRaycastFilter`
// attached. Unity consults the filter for every candidate hit; we
// sample the current CEF-owned IOSurface at the same pixel the
// cursor is over, and return true (block) for opaque pixels, false
// (pass through) for transparent ones. Same alpha source the mouse
// forwarder in `DragonglassHudAddon` uses, so Unity-side blocking
// and CEF-side click forwarding agree by construction.
//
// The current IOSurface ID is pushed in from `DragonglassHudAddon`
// each frame via `SetSurface`, mirroring its own `_currentIoSurfaceId`
// state. Before the first valid surface arrives (or during sidecar
// restarts), ID=0 → filter returns false → clicks pass through,
// which matches the "nothing useful on screen anyway" degraded state.

using UnityEngine;

namespace Dragonglass.Hud
{
    public sealed class HudRaycastFilter : MonoBehaviour, ICanvasRaycastFilter
    {
        private int _overlayWidth;
        private int _overlayHeight;
        private uint _currentIoSurfaceId;

        public void Configure(int overlayWidth, int overlayHeight)
        {
            _overlayWidth = overlayWidth;
            _overlayHeight = overlayHeight;
        }

        public void SetSurface(uint ioSurfaceId)
        {
            _currentIoSurfaceId = ioSurfaceId;
        }

        public bool IsRaycastLocationValid(Vector2 screenPoint, Camera eventCamera)
        {
            if (_currentIoSurfaceId == 0) return false;
            if (_overlayWidth <= 0 || _overlayHeight <= 0) return false;

            int screenW = Screen.width;
            int screenH = Screen.height;
            if (screenW <= 0 || screenH <= 0) return false;

            int cefX = (int)((screenPoint.x / (float)screenW) * _overlayWidth);
            int cefY = (int)(((screenH - screenPoint.y) / (float)screenH) * _overlayHeight);
            if (cefX < 0 || cefX >= _overlayWidth) return false;
            if (cefY < 0 || cefY >= _overlayHeight) return false;

            uint bgra;
            if (NativeBridge.DgHudNative_SamplePixel(
                    _currentIoSurfaceId, cefX, cefY, out bgra) != 1)
                return false;

            return (byte)(bgra >> 24) > 0;
        }
    }
}
