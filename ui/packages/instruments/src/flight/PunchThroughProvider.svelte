<script lang="ts">
  // Punch-through provider + in-band rect encoder.
  //
  // Provides the registry context to descendant <PunchThrough>
  // components and renders a hidden 1-px-tall canvas at the very top
  // of the page. Each `requestAnimationFrame` tick, we encode the
  // active rect set into that canvas's pixels via `putImageData`.
  //
  // Because the encoding canvas is part of the same compositor frame
  // as the visible HUD, the rect data and the visible chrome arrive
  // at the Unity-side compositor *atomically* (same IOSurface) — no
  // IPC race possible between the portrait position and the CEF
  // render the user is about to see.
  //
  // The Unity overlay crops the encoding row from the visible output
  // via its `RawImage.uvRect`, so the user never sees these pixels.
  //
  // Encoding scheme (RGBA bytes, alpha=255 always so values survive
  // CEF's premultiplied-alpha output):
  //
  //   pixel 0 (header):     R=magic (0xDD)  G=count    B=0
  //   per rect i, 4 pixels:
  //     pixel 1+i*4 (id_hash low 24 bits): R=hash_b0  G=hash_b1  B=hash_b2
  //     pixel 2+i*4 (x lo/hi, y lo):       R=x_lo  G=x_hi  B=y_lo
  //     pixel 3+i*4 (y hi, w lo/hi):       R=y_hi  G=w_lo  B=w_hi
  //     pixel 4+i*4 (h lo/hi, id_hash hi): R=h_lo  G=h_hi  B=hash_b3
  //
  // Full 32-bit id_hash is split across pixel 1 (low 24 bits) and the
  // otherwise-unused B byte of pixel 4 (high 8 bits) — keeps the mod
  // side's 32-bit `Fnv1a32` registration matching plugin lookups
  // exactly, no truncation collision risk.
  //
  // Mirrors the decoder in `mod/native/darwin-universal/src/DgHudNative.mm`.
  //
  // Dev-server / vanilla-browser detection: only render the encoding
  // canvas when bootstrapped under the KSP-side sidecar (signalled by
  // `?host=ksp` from the sidecar's URL builder). Outside that host
  // the canvas is meaningless — there's no Unity-side compositor on
  // the other end to read it back from the IOSurface — and it would
  // just paint a 1-px line at the top of the page.

  import { onDestroy, onMount, setContext } from 'svelte';
  import {
    PUNCH_THROUGH_CONTEXT_KEY,
    createPunchThroughRegistry,
    type PunchThroughEntry,
  } from './punch-through';
  import { isHostKsp } from '../host';

  let {
    children,
  }: {
    children?: import('svelte').Snippet;
  } = $props();

  const registry = createPunchThroughRegistry();
  setContext(PUNCH_THROUGH_CONTEXT_KEY, registry);

  const ENCODING_MAGIC = 0xdd;
  const PX_PER_RECT = 4;
  const MAX_RECTS = 16;

  let canvas = $state<HTMLCanvasElement | null>(null);
  let ctx: CanvasRenderingContext2D | null = null;
  let imageData: ImageData | null = null;
  let raf = 0;

  function ensureCanvasSized(): boolean {
    if (!canvas) return false;
    const dpr = window.devicePixelRatio || 1;
    const wantW = Math.max(1, Math.floor(window.innerWidth * dpr));
    if (canvas.width !== wantW || canvas.height !== 1) {
      canvas.width = wantW;
      canvas.height = 1;
      // willReadFrequently=false: we only write, never read.
      ctx = canvas.getContext('2d', { willReadFrequently: false });
      imageData = ctx ? ctx.createImageData(wantW, 1) : null;
    }
    return ctx !== null && imageData !== null;
  }

  function encode(snapshot: PunchThroughEntry[]): void {
    if (!ensureCanvasSized()) return;
    const data = imageData!.data;
    // Zero everything once; we overwrite the active prefix below and
    // leave the tail as transparent black (which is also alpha=0, so
    // the unused tail of the row stays invisible — important since
    // alpha=0 *would* fail the magic check were any reader to scan
    // beyond `count`, which the decoder doesn't but be defensive).
    data.fill(0);

    const dpr = window.devicePixelRatio || 1;
    // Read each entry's viewport rect *now*, inside the same rAF tick
    // we encode and paint in. This is the whole point of having one
    // rAF instead of per-component pumps: by the time this callback
    // runs, the browser has applied every pointermove-driven state
    // change for this frame, so `getBoundingClientRect()` returns the
    // exact rect that's about to be painted in the visible HUD chrome
    // — no inter-rAF ordering race.
    type Resolved = {
      idHash: number;
      x: number; y: number; w: number; h: number;
    };
    const resolved: Resolved[] = [];
    for (const e of snapshot) {
      if (!e.visible) continue;
      const r = e.el.getBoundingClientRect();
      const w = Math.max(0, Math.round(r.width * dpr)) & 0xffff;
      const h = Math.max(0, Math.round(r.height * dpr)) & 0xffff;
      if (w === 0 || h === 0) continue;
      resolved.push({
        idHash: fnv1a32(e.id),
        x: Math.round(r.left * dpr) | 0,
        y: Math.round(r.top * dpr) | 0,
        w,
        h,
      });
      if (resolved.length >= MAX_RECTS) break;
    }
    const count = resolved.length;

    // Header pixel.
    data[0] = ENCODING_MAGIC; // R
    data[1] = count;          // G
    data[2] = 0;              // B
    data[3] = 255;            // A

    for (let i = 0; i < count; i++) {
      const { idHash, x, y, w, h } = resolved[i];

      const x16 = x & 0xffff;
      const y16 = y & 0xffff;
      const off = (1 + i * PX_PER_RECT) * 4;

      // Pixel 1: id_hash low 24 bits.
      data[off + 0] = idHash & 0xff;
      data[off + 1] = (idHash >>> 8) & 0xff;
      data[off + 2] = (idHash >>> 16) & 0xff;
      data[off + 3] = 255;

      // Pixel 2: x_lo, x_hi, y_lo.
      data[off + 4] = x16 & 0xff;
      data[off + 5] = (x16 >>> 8) & 0xff;
      data[off + 6] = y16 & 0xff;
      data[off + 7] = 255;

      // Pixel 3: y_hi, w_lo, w_hi.
      data[off + 8] = (y16 >>> 8) & 0xff;
      data[off + 9] = w & 0xff;
      data[off + 10] = (w >>> 8) & 0xff;
      data[off + 11] = 255;

      // Pixel 4: h_lo, h_hi, id_hash high 8 bits.
      data[off + 12] = h & 0xff;
      data[off + 13] = (h >>> 8) & 0xff;
      data[off + 14] = (idHash >>> 24) & 0xff;
      data[off + 15] = 255;
    }

    ctx!.putImageData(imageData!, 0, 0);
  }

  // FNV-1a 32-bit. Matches the C# `Fnv1a32` in `PortraitCapture.cs`
  // and the C++ `fnv1a_32` in `DgHudNative.mm` so the mod's stream
  // texture registry and the plugin's per-frame lookup find each
  // other by the same key.
  function fnv1a32(s: string): number {
    let h = 0x811c9dc5 >>> 0;
    for (let i = 0; i < s.length; i++) {
      h ^= s.charCodeAt(i);
      h = Math.imul(h, 0x01000193) >>> 0;
    }
    return h >>> 0;
  }

  onMount(() => {
    if (!isHostKsp()) return;
    const tick = () => {
      raf = requestAnimationFrame(tick);
      encode(registry.snapshot());
    };
    raf = requestAnimationFrame(tick);
  });

  onDestroy(() => {
    if (raf) cancelAnimationFrame(raf);
    raf = 0;
  });

  const underKsp = isHostKsp();
</script>

{#if underKsp}
  <!-- 1-px-tall encoding row at top of page. Must paint over a
       transparent area so its pixels survive into the IOSurface
       intact. The Unity-side overlay crops this row via uvRect so
       the user never sees it. -->
  <canvas
    bind:this={canvas}
    class="punch-through-encoding"
    aria-hidden="true"
  ></canvas>
{/if}

{@render children?.()}

<style>
  .punch-through-encoding {
    position: fixed;
    top: 0;
    left: 0;
    width: 100vw;
    height: 1px;
    pointer-events: none;
    /* Forced above any scene content so the page can't paint over
       the encoding pixels. The Unity overlay crops this row from
       the user-visible output via `RawImage.uvRect`. HUD content
       positioned at exactly `top: 0` will lose 1 px of its top
       edge — a safe trade-off: we have no visual content there. */
    z-index: 2147483647;
  }
</style>
