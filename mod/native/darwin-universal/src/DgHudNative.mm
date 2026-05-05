// KSP Web HUD native rendering plugin.
//
// macOS dylib loaded by KSP/Unity via P/Invoke. The purpose of this
// library is to take an IOSurfaceID published by the sidecar's CEF
// accelerated-paint path and, on Unity's render thread, copy that
// IOSurface's GPU contents into a regular Unity-owned Texture2D
// *without* round-tripping through CPU memory.
//
// Architecture recap:
//
//   sidecar                    shared memory           plugin (this file)
//   -------                    -------------           ------------------
//   CEF renders →              header writes           render-thread event:
//   IOSurface pool             (io_surface_id)           look up IOSurface
//   (2-surface rotation)   →                           → wrap as source texture
//                                                      → GPU blit into
//                                                        Unity's dest texture
//
// Two graphics backends share the same shell:
//
//   * OpenGL Core path (primary on KSP 1.12.5 — Unity's player ignores
//     -force-gfx-metal and hard-pins GL on macOS). Uses
//     `CGLTexImageIOSurface2D` to bind the IOSurface as a
//     `GL_TEXTURE_RECTANGLE`, then `glBlitFramebuffer` to copy into
//     Unity's destination `GL_TEXTURE_2D` via two FBOs. One GPU-local
//     blit per frame (~sub-ms on Apple Silicon, still cheap on
//     dedicated GPUs because it's GPU→GPU).
//
//   * Metal path (compiled, currently dormant). Uses
//     `[MTLDevice newTextureWithDescriptor:iosurface:plane:]` plus a
//     Metal blit encoder. Kept so switching Unity backends in the
//     future is a no-op on this side.
//
// Unity side is deliberately simple: C# creates a normal
// `Texture2D(w, h, BGRA32)`, hands its `GetNativeTexturePtr()` to this
// plugin once at Start(), and each Update() requests a render-thread
// blit by calling `GL.IssuePluginEvent(DgHudNative_GetRenderEventFunc(), 0)`
// after updating the pending surface id via `DgHudNative_UpdatePending`.
// No `CreateExternalTexture`, no lifecycle juggling of external handles.
//
// The "zero-copy" claim: zero *CPU↔GPU* copies — the bytes CEF rendered
// never pass through system RAM on their way to Unity's shader. There
// IS still one GPU-local copy (the blit), which is near-free on Apple
// Silicon's unified memory and sub-millisecond on dedicated GPUs
// because it's a straight memcpy inside VRAM. True zero-blit paths
// (custom shader sampling the RECT texture directly) are a follow-up
// optimization — intentionally not in v0 because they'd require us
// to hijack Unity's RawImage shader, which is scope creep.

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsMetal.h"

#import <Foundation/Foundation.h>
#import <AppKit/AppKit.h>
#import <Metal/Metal.h>
#import <IOSurface/IOSurface.h>
#include <OpenGL/gl3.h>
#include <OpenGL/gl3ext.h>
#include <OpenGL/CGLIOSurface.h>
#include <OpenGL/CGLCurrent.h>

#include <stdint.h>
#include <string.h>
#include <stdio.h>
#include <cstdlib>
#include <atomic>
#include <unordered_map>
#include <mutex>
#include <vector>

// OpenGL rectangle-texture target. Present in OpenGL 3.1 core and
// Apple's gl3ext.h — redeclared here so the file compiles even on
// an SDK where gl3ext.h omits it.
#ifndef GL_TEXTURE_RECTANGLE
#define GL_TEXTURE_RECTANGLE 0x84F5
#endif

// Suppress the "OpenGL is deprecated" warnings from every gl* call in
// this translation unit. OpenGL remains functional on macOS and is the
// only backend KSP's Unity 2019.4 player exposes; we know it's deprecated.
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"

namespace {

// ---------------------------------------------------------------------
// Plugin-level state (shared across all exported calls and both backends).
// ---------------------------------------------------------------------

enum class Backend {
    Unknown = 0,
    OpenGLCore,
    Metal,
};

// Populated by UnityPluginLoad. Null until Unity loads the plugin.
IUnityInterfaces* g_unity_interfaces = nullptr;
IUnityGraphics* g_graphics = nullptr;
IUnityGraphicsMetal* g_metal = nullptr;
Backend g_backend = Backend::Unknown;

// Target texture bound to a Unity-owned Texture2D. Populated by
// `DgHudNative_SetTargetTexture` from C# after the plugin loads.
// On GL backend: a GLuint name (stored in a uintptr_t to survive
// cross-arch sign-extension). On Metal backend: an `id<MTLTexture>`
// cast to a raw pointer — Unity owns the retain.
uintptr_t g_dest_tex = 0;
uint32_t g_dest_w = 0;
uint32_t g_dest_h = 0;

// Pending surface state published by C# each frame. The render-thread
// event picks it up at the next callback and performs the blit. An
// atomic pair is enough here — we only care about latest-wins.
std::atomic<uint64_t> g_pending_packed{0};   // (gen << 32) | id
std::atomic<uint64_t> g_last_completed{0};   // last value we blit'd

static inline uint64_t pack_pending(uint32_t id, uint32_t gen) {
    return (static_cast<uint64_t>(gen) << 32) | static_cast<uint64_t>(id);
}
static inline void unpack_pending(uint64_t packed, uint32_t* id, uint32_t* gen) {
    *id = static_cast<uint32_t>(packed & 0xffffffffu);
    *gen = static_cast<uint32_t>(packed >> 32);
}

// Per-frame / per-run stats for debugging. Logged lazily on the main
// thread via stderr; not performance-critical.
std::atomic<uint64_t> g_blit_count{0};
std::atomic<uint64_t> g_error_count{0};
std::atomic<uint64_t> g_cache_miss_count{0};

// ---------------------------------------------------------------------
// Punch-through stream rect table (sidecar → plugin via SHM, mirrored
// here from the C# side via DgHudNative_UpdateStreamRects). Each slot
// describes one HTML <PunchThrough> placeholder: the rect to draw the
// portrait at, the chroma color CEF paints there, and the threshold
// for the chroma-key shader. Mirrors `StreamRect` in dg-shm and the
// 24-byte slot layout in `crates/dg-shm/src/layout.rs`.
// ---------------------------------------------------------------------

constexpr int kStreamRectCapacity = 16;

#pragma pack(push, 1)
struct StreamRectSlot {
    uint32_t id_hash;
    int16_t  x;
    int16_t  y;
    uint16_t w;
    uint16_t h;
    uint8_t  chroma_r;
    uint8_t  chroma_g;
    uint8_t  chroma_b;
    uint8_t  threshold;
    uint32_t flags;
    uint32_t _pad;
};
#pragma pack(pop)
static_assert(sizeof(StreamRectSlot) == 24, "StreamRectSlot layout mismatch");

constexpr uint32_t kStreamFlagVisible = 1u << 0;

std::mutex g_stream_rects_mu;
StreamRectSlot g_stream_rects[kStreamRectCapacity];
int g_stream_rect_count = 0;
std::atomic<uint64_t> g_stream_rect_revision{0}; // bumped on update

// ---------------------------------------------------------------------
// Stream texture registry (mod → plugin via P/Invoke).
//
// Each registered stream id_hash is a GL_TEXTURE_2D containing the
// latest portrait bytes the mod uploaded via DgHudNative_PushStreamFrame.
// The compositor binds the stream's texture as the "portrait" sampler
// in the chroma-key shader pass for any rect whose id_hash matches.
// ---------------------------------------------------------------------

struct StreamTexEntry {
    GLuint tex;
    uint32_t width;
    uint32_t height;
    // Pending CPU-side bytes waiting for the next render-thread tick
    // to upload. We can't call glTexImage2D from the C# thread that
    // pushed the frame — only the render thread has the GL context.
    std::vector<uint8_t> pending_bytes;
    uint32_t pending_w;
    uint32_t pending_h;
    bool has_pending;
};

std::mutex g_stream_tex_mu;
std::unordered_map<uint32_t, StreamTexEntry> g_stream_tex;

// Split error counters so C# can tell which branch is failing without
// relying on stderr (Unity's stderr capture is unreliable on macOS).
std::atomic<uint64_t> g_err_no_ctx{0};           // CGLGetCurrentContext returned null
std::atomic<uint64_t> g_err_iosurface_lookup{0}; // IOSurfaceLookup returned null
std::atomic<uint64_t> g_err_cgl_tex_image{0};    // CGLTexImageIOSurface2D failed
std::atomic<uint64_t> g_err_fbo_incomplete{0};   // FBO validation failed
std::atomic<uint64_t> g_err_no_dest{0};          // g_dest_tex is zero when blit requested

// GL-path cache: IOSurfaceID → GLuint rect-texture. CEF pools two
// surfaces so this map stays at size 2; we never evict. On plugin
// unload we delete the textures.
struct GlCacheEntry {
    GLuint rect_tex;
    uint32_t width;
    uint32_t height;
};
std::mutex g_gl_cache_mu;
std::unordered_map<uint32_t, GlCacheEntry> g_gl_cache;

// Last error message the plugin wants to surface to C#. Unity's
// stderr redirection isn't reliable on macOS, so exporting a string
// getter is the most portable way to get errors into KSP.log.
char g_last_error[512] = {0};
std::mutex g_last_error_mu;

// Forward declarations — defined further down.
void log_prefixed(const char* fmt, ...);
void gl_composite_streams(GLuint cef_rect_tex, uint32_t cef_w, uint32_t cef_h);

// Currently unused after the mach-bridge cleanup, but kept (cheap +
// useful) so future failure paths can surface a message into KSP.log
// via `DgHudNative_GetLastError`.
__attribute__((unused))
void set_last_error(const char* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    std::lock_guard<std::mutex> lock(g_last_error_mu);
    vsnprintf(g_last_error, sizeof(g_last_error), fmt, args);
    va_end(args);
    log_prefixed("last_error set: %s", g_last_error);
}

// GL state we create lazily once we have a current context. Two FBOs:
// one with the rect texture attached (rebound per-surface), one with
// Unity's dest 2D texture attached (stable once set).
GLuint g_src_fbo = 0;
GLuint g_dst_fbo = 0;
GLuint g_last_dst_tex_attached = 0; // to avoid re-attaching every frame

// Metal-path cache: parallel structure for the dormant Metal backend.
std::mutex g_metal_cache_mu;
std::unordered_map<uint32_t, void*> g_metal_cache; // raw id<MTLTexture>

// Simple prefixed logger. Unity captures stderr into the game log.
void log_prefixed(const char* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    fprintf(stderr, "[DgHudNative] ");
    vfprintf(stderr, fmt, args);
    fputc('\n', stderr);
    va_end(args);
}

// ---------------------------------------------------------------------
// OpenGL backend
// ---------------------------------------------------------------------

// Look up or create the rect texture wrapping a given IOSurfaceID. Must
// run on a thread with a current CGL context (Unity's render thread).
GLuint gl_get_or_create_rect_tex(uint32_t io_surface_id,
                                 uint32_t* out_w, uint32_t* out_h) {
    {
        std::lock_guard<std::mutex> lock(g_gl_cache_mu);
        auto it = g_gl_cache.find(io_surface_id);
        if (it != g_gl_cache.end()) {
            *out_w = it->second.width;
            *out_h = it->second.height;
            return it->second.rect_tex;
        }
    }

    g_cache_miss_count.fetch_add(1, std::memory_order_relaxed);

    CGLContextObj ctx = CGLGetCurrentContext();
    if (!ctx) {
        g_err_no_ctx.fetch_add(1, std::memory_order_relaxed);
        g_error_count.fetch_add(1, std::memory_order_relaxed);
        return 0;
    }

    // The sidecar publishes a canvas IOSurface created with
    // `kIOSurfaceIsGlobal=true`, so a plain `IOSurfaceLookup` from
    // any process in the same user session resolves it. This is the
    // only path we use — the earlier mach-port bridge experiment is
    // gone.
    IOSurfaceRef surface = IOSurfaceLookup((IOSurfaceID)io_surface_id);
    if (!surface) {
        g_err_iosurface_lookup.fetch_add(1, std::memory_order_relaxed);
        g_error_count.fetch_add(1, std::memory_order_relaxed);
        return 0;
    }
    log_prefixed("gl: IOSurfaceLookup(0x%x) = %p", io_surface_id, surface);

    GLsizei w = (GLsizei)IOSurfaceGetWidth(surface);
    GLsizei h = (GLsizei)IOSurfaceGetHeight(surface);

    GLuint tex = 0;
    glGenTextures(1, &tex);
    glBindTexture(GL_TEXTURE_RECTANGLE, tex);
    // Sampling parameters — rectangle textures have distinct defaults
    // from 2D textures, so set them explicitly even though we only
    // ever blit and never actually sample.
    glTexParameteri(GL_TEXTURE_RECTANGLE, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_RECTANGLE, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_RECTANGLE, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_RECTANGLE, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

    CGLError err = CGLTexImageIOSurface2D(
        ctx, GL_TEXTURE_RECTANGLE,
        GL_RGBA, w, h,
        GL_BGRA, GL_UNSIGNED_INT_8_8_8_8_REV,
        surface, 0);
    glBindTexture(GL_TEXTURE_RECTANGLE, 0);
    CFRelease(surface);

    if (err != kCGLNoError) {
        log_prefixed("gl: CGLTexImageIOSurface2D failed: %d", (int)err);
        glDeleteTextures(1, &tex);
        g_err_cgl_tex_image.fetch_add(1, std::memory_order_relaxed);
        g_error_count.fetch_add(1, std::memory_order_relaxed);
        return 0;
    }

    {
        std::lock_guard<std::mutex> lock(g_gl_cache_mu);
        g_gl_cache[io_surface_id] = GlCacheEntry{tex, (uint32_t)w, (uint32_t)h};
    }
    log_prefixed("gl: cached rect_tex=%u for IOSurface id=0x%x (%dx%d)",
                 tex, io_surface_id, w, h);
    *out_w = (uint32_t)w;
    *out_h = (uint32_t)h;
    return tex;
}

void gl_ensure_fbos() {
    if (g_src_fbo == 0) glGenFramebuffers(1, &g_src_fbo);
    if (g_dst_fbo == 0) glGenFramebuffers(1, &g_dst_fbo);
}

void gl_blit(uint32_t io_surface_id) {
    if (g_dest_tex == 0) {
        g_err_no_dest.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    uint32_t src_w = 0, src_h = 0;
    GLuint src_tex = gl_get_or_create_rect_tex(io_surface_id, &src_w, &src_h);
    if (src_tex == 0) return;

    gl_ensure_fbos();

    GLuint dst_tex = static_cast<GLuint>(g_dest_tex);
    // Attach the source rect tex fresh every blit; it changes when CEF
    // rotates surfaces. This is a cheap GL state change.
    glBindFramebuffer(GL_READ_FRAMEBUFFER, g_src_fbo);
    glFramebufferTexture2D(GL_READ_FRAMEBUFFER, GL_COLOR_ATTACHMENT0,
                           GL_TEXTURE_RECTANGLE, src_tex, 0);

    // Dest: attach Unity's 2D texture once and remember it so we don't
    // thrash FBO completeness validation every frame.
    glBindFramebuffer(GL_DRAW_FRAMEBUFFER, g_dst_fbo);
    if (g_last_dst_tex_attached != dst_tex) {
        glFramebufferTexture2D(GL_DRAW_FRAMEBUFFER, GL_COLOR_ATTACHMENT0,
                               GL_TEXTURE_2D, dst_tex, 0);
        g_last_dst_tex_attached = dst_tex;
    }

    // FBO completeness check (first blit only, keep it cheap).
    static bool s_checked = false;
    if (!s_checked) {
        GLenum rs = glCheckFramebufferStatus(GL_READ_FRAMEBUFFER);
        GLenum ds = glCheckFramebufferStatus(GL_DRAW_FRAMEBUFFER);
        if (rs != GL_FRAMEBUFFER_COMPLETE || ds != GL_FRAMEBUFFER_COMPLETE) {
            log_prefixed("gl: FBO incomplete read=0x%x draw=0x%x", rs, ds);
            g_err_fbo_incomplete.fetch_add(1, std::memory_order_relaxed);
            g_error_count.fetch_add(1, std::memory_order_relaxed);
            glBindFramebuffer(GL_READ_FRAMEBUFFER, 0);
            glBindFramebuffer(GL_DRAW_FRAMEBUFFER, 0);
            return;
        }
        s_checked = true;
        log_prefixed("gl: FBOs complete — blit ready (src=%ux%u dst=%ux%u)",
                     src_w, src_h, g_dest_w, g_dest_h);
    }

    glBlitFramebuffer(
        0, 0, (GLint)src_w, (GLint)src_h,
        0, 0, (GLint)g_dest_w, (GLint)g_dest_h,
        GL_COLOR_BUFFER_BIT, GL_NEAREST);

    glBindFramebuffer(GL_READ_FRAMEBUFFER, 0);
    glBindFramebuffer(GL_DRAW_FRAMEBUFFER, 0);

    g_blit_count.fetch_add(1, std::memory_order_relaxed);

    // Composite punch-through stream rects on top of the CEF blit:
    // anywhere CEF painted the chroma color inside a registered rect,
    // reveal the matching stream's portrait texture instead.
    gl_composite_streams(src_tex, src_w, src_h);
}

// ---------------------------------------------------------------------
// Chroma-key shader compositor
// ---------------------------------------------------------------------
//
// Pass strategy: after the existing CEF→dest fullscreen blit, render a
// small NDC quad per visible stream rect. The fragment shader samples
// the CEF rect texture (sampler2DRect at gl_FragCoord) and the
// stream's portrait texture (sampler2D at normalised UV inside the
// rect); chroma-distance smoothstep blends portrait↔CEF. Outside the
// per-rect viewport, the CEF blit's pixels stand untouched.
//
// Lazily compiled on first composite — no GL state created until at
// least one stream is active.

GLuint g_chroma_program = 0;
GLint g_chroma_loc_cef = -1;
GLint g_chroma_loc_portrait = -1;
GLint g_chroma_loc_chroma = -1;
GLint g_chroma_loc_threshold = -1;
GLint g_chroma_loc_rect_origin = -1;
GLint g_chroma_loc_rect_size = -1;
GLuint g_chroma_vao = 0;
GLuint g_chroma_vbo = 0;
bool g_chroma_compile_failed = false;

static const char* kChromaVertSrc =
    "#version 150 core\n"
    "in vec2 a_pos;\n"
    "void main() { gl_Position = vec4(a_pos, 0.0, 1.0); }\n";

// Coordinate convention in this shader:
//
//   * `gl_FragCoord.xy` is window-space pixel coords with origin
//     bottom-left (GL default).
//   * The CEF rect texture (`u_cef`, sampler2DRect) was wrapped from
//     an IOSurface with top-down memory layout, but `CGLTexImageIOSurface2D`
//     maps that into the GL texture's bottom-up coord system — so a
//     CEF-visual-top pixel ends up at GL Y=0 in the rect texture.
//   * The dest 2D texture (Unity's RawImage backing) follows the same
//     bottom-up convention; the prior `glBlitFramebuffer` copies
//     coords 1:1, so dest GL (X, Y) holds CEF data from the same GL
//     (X, Y). Unity then rolls a `uvRect = (0, 1, 1, -1)` over it to
//     display top-up on screen.
//
// Implication: a CSS-style top-left rect at (rx, ry, rw, rh) lives in
// dest GL coords at the *same* (rx, ry) — no Y flip — and we sample
// CEF at `gl_FragCoord.xy` directly.
//
// For the portrait UV we still flip V because GL textures sample
// bottom-up and our checkerboard bytes were uploaded top-down.
static const char* kChromaFragSrc =
    "#version 150 core\n"
    "out vec4 frag_color;\n"
    "uniform sampler2DRect u_cef;\n"
    "uniform sampler2D u_portrait;\n"
    "uniform vec3 u_chroma;\n"
    "uniform float u_threshold;\n"
    "uniform vec2 u_rect_origin;\n"  // GL coords (= CSS coords)
    "uniform vec2 u_rect_size;\n"    // w, h in pixels
    "void main() {\n"
    "    vec2 fb = gl_FragCoord.xy;\n"
    "    vec4 cef = texture(u_cef, fb);\n"
    "    vec2 local = (fb - u_rect_origin) / u_rect_size;\n"
    "    vec2 portrait_uv = vec2(local.x, 1.0 - local.y);\n"
    "    vec4 portrait = texture(u_portrait, portrait_uv);\n"
    "    vec3 d = abs(cef.rgb - u_chroma);\n"
    "    float dist = max(max(d.r, d.g), d.b);\n"
    "    float lo = u_threshold * 0.5;\n"
    "    float hi = u_threshold * 1.5;\n"
    "    float key = smoothstep(lo, hi, dist);\n"
    "    frag_color = mix(portrait, cef, key);\n"
    "}\n";

GLuint compile_shader(GLenum type, const char* src) {
    GLuint sh = glCreateShader(type);
    glShaderSource(sh, 1, &src, nullptr);
    glCompileShader(sh);
    GLint ok = 0;
    glGetShaderiv(sh, GL_COMPILE_STATUS, &ok);
    if (!ok) {
        char log[1024] = {0};
        glGetShaderInfoLog(sh, sizeof(log) - 1, nullptr, log);
        log_prefixed("chroma: shader compile failed (%s): %s",
                     type == GL_VERTEX_SHADER ? "vert" : "frag", log);
        glDeleteShader(sh);
        return 0;
    }
    return sh;
}

bool gl_chroma_ensure_program() {
    if (g_chroma_program != 0) return true;
    if (g_chroma_compile_failed) return false;

    GLuint vs = compile_shader(GL_VERTEX_SHADER, kChromaVertSrc);
    if (!vs) { g_chroma_compile_failed = true; return false; }
    GLuint fs = compile_shader(GL_FRAGMENT_SHADER, kChromaFragSrc);
    if (!fs) { glDeleteShader(vs); g_chroma_compile_failed = true; return false; }

    GLuint prog = glCreateProgram();
    glAttachShader(prog, vs);
    glAttachShader(prog, fs);
    glBindAttribLocation(prog, 0, "a_pos");
    glLinkProgram(prog);
    glDeleteShader(vs);
    glDeleteShader(fs);

    GLint linked = 0;
    glGetProgramiv(prog, GL_LINK_STATUS, &linked);
    if (!linked) {
        char log[1024] = {0};
        glGetProgramInfoLog(prog, sizeof(log) - 1, nullptr, log);
        log_prefixed("chroma: program link failed: %s", log);
        glDeleteProgram(prog);
        g_chroma_compile_failed = true;
        return false;
    }

    g_chroma_program = prog;
    g_chroma_loc_cef         = glGetUniformLocation(prog, "u_cef");
    g_chroma_loc_portrait    = glGetUniformLocation(prog, "u_portrait");
    g_chroma_loc_chroma      = glGetUniformLocation(prog, "u_chroma");
    g_chroma_loc_threshold   = glGetUniformLocation(prog, "u_threshold");
    g_chroma_loc_rect_origin = glGetUniformLocation(prog, "u_rect_origin");
    g_chroma_loc_rect_size   = glGetUniformLocation(prog, "u_rect_size");

    // Fullscreen NDC quad as a triangle strip.
    static const float kQuad[8] = {
        -1.0f, -1.0f,
         1.0f, -1.0f,
        -1.0f,  1.0f,
         1.0f,  1.0f,
    };
    glGenVertexArrays(1, &g_chroma_vao);
    glGenBuffers(1, &g_chroma_vbo);
    glBindVertexArray(g_chroma_vao);
    glBindBuffer(GL_ARRAY_BUFFER, g_chroma_vbo);
    glBufferData(GL_ARRAY_BUFFER, sizeof(kQuad), kQuad, GL_STATIC_DRAW);
    glEnableVertexAttribArray(0);
    glVertexAttribPointer(0, 2, GL_FLOAT, GL_FALSE, 0, (void*)0);
    glBindVertexArray(0);

    log_prefixed("chroma: program ready (id=%u)", prog);
    return true;
}

// Look up (and lazily upload pending bytes for) a stream texture.
// Returns 0 if the stream isn't registered or has no content yet.
GLuint stream_tex_get_or_upload(uint32_t id_hash) {
    StreamTexEntry* entry = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_stream_tex_mu);
        auto it = g_stream_tex.find(id_hash);
        if (it == g_stream_tex.end()) return 0;
        entry = &it->second;
    }
    // We hold a pointer into the map without the lock — safe because
    // the map is only modified by render-thread code (this fn) and by
    // the C# Push/Remove paths, both of which take the same mutex
    // before mutating. The pointer survives the unlock by virtue of
    // unordered_map's stable references after non-rehashing inserts;
    // for safety, re-take the lock before mutating .pending_*.
    std::lock_guard<std::mutex> lock(g_stream_tex_mu);
    auto it = g_stream_tex.find(id_hash);
    if (it == g_stream_tex.end()) return 0;
    entry = &it->second;

    if (entry->has_pending) {
        if (entry->tex == 0) {
            glGenTextures(1, &entry->tex);
            glBindTexture(GL_TEXTURE_2D, entry->tex);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        } else {
            glBindTexture(GL_TEXTURE_2D, entry->tex);
        }
        if (entry->pending_w != entry->width || entry->pending_h != entry->height) {
            glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA,
                         (GLsizei)entry->pending_w, (GLsizei)entry->pending_h, 0,
                         GL_BGRA, GL_UNSIGNED_INT_8_8_8_8_REV,
                         entry->pending_bytes.data());
            entry->width = entry->pending_w;
            entry->height = entry->pending_h;
        } else {
            glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0,
                            (GLsizei)entry->width, (GLsizei)entry->height,
                            GL_BGRA, GL_UNSIGNED_INT_8_8_8_8_REV,
                            entry->pending_bytes.data());
        }
        glBindTexture(GL_TEXTURE_2D, 0);
        entry->has_pending = false;
    }
    return entry->tex;
}

void gl_composite_streams(GLuint cef_rect_tex, uint32_t cef_w, uint32_t cef_h) {
    (void)cef_w;
    // Snapshot the active rect set under the mutex so we don't hold it
    // across GL calls.
    StreamRectSlot rects_local[kStreamRectCapacity];
    int n = 0;
    {
        std::lock_guard<std::mutex> lock(g_stream_rects_mu);
        n = g_stream_rect_count;
        if (n > 0) memcpy(rects_local, g_stream_rects,
                          (size_t)n * sizeof(StreamRectSlot));
    }
    if (n == 0) return;
    if (!gl_chroma_ensure_program()) return;
    if (g_dest_tex == 0) return;

    // Save state we touch so we don't break Unity's rendering.
    GLint prev_program = 0;
    GLint prev_vao = 0;
    GLint prev_active_tex = GL_TEXTURE0;
    GLint prev_tex_2d = 0;
    GLint prev_tex_rect = 0;
    GLint prev_fbo_draw = 0;
    GLint prev_viewport[4] = {0, 0, 0, 0};
    GLboolean prev_blend = GL_FALSE;
    GLboolean prev_depth = GL_FALSE;
    GLboolean prev_cull = GL_FALSE;
    GLboolean prev_scissor = GL_FALSE;
    glGetIntegerv(GL_CURRENT_PROGRAM, &prev_program);
    glGetIntegerv(GL_VERTEX_ARRAY_BINDING, &prev_vao);
    glGetIntegerv(GL_ACTIVE_TEXTURE, &prev_active_tex);
    glGetIntegerv(GL_TEXTURE_BINDING_2D, &prev_tex_2d);
    glGetIntegerv(GL_TEXTURE_BINDING_RECTANGLE, &prev_tex_rect);
    glGetIntegerv(GL_DRAW_FRAMEBUFFER_BINDING, &prev_fbo_draw);
    glGetIntegerv(GL_VIEWPORT, prev_viewport);
    prev_blend = glIsEnabled(GL_BLEND);
    prev_depth = glIsEnabled(GL_DEPTH_TEST);
    prev_cull  = glIsEnabled(GL_CULL_FACE);
    prev_scissor = glIsEnabled(GL_SCISSOR_TEST);

    glDisable(GL_BLEND);
    glDisable(GL_DEPTH_TEST);
    glDisable(GL_CULL_FACE);
    glDisable(GL_SCISSOR_TEST);

    // Bind dest FBO with Unity's destination texture attached. The
    // CEF blit above already attached it (g_last_dst_tex_attached);
    // re-bind via g_dst_fbo so we render into the same target.
    glBindFramebuffer(GL_DRAW_FRAMEBUFFER, g_dst_fbo);

    glUseProgram(g_chroma_program);
    glUniform1i(g_chroma_loc_cef, 0);
    glUniform1i(g_chroma_loc_portrait, 1);

    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_RECTANGLE, cef_rect_tex);

    glBindVertexArray(g_chroma_vao);

    int composited = 0;
    for (int i = 0; i < n; ++i) {
        const StreamRectSlot& r = rects_local[i];
        if ((r.flags & kStreamFlagVisible) == 0) continue;
        if (r.w == 0 || r.h == 0) continue;
        GLuint portrait = stream_tex_get_or_upload(r.id_hash);
        if (portrait == 0) continue;

        // CSS top-left coords map *directly* to dest GL coords —
        // both the CEF rect texture and the dest 2D texture follow
        // the same bottom-up convention after `CGLTexImageIOSurface2D`
        // and `glBlitFramebuffer`. Unity's `uvRect=(0,1,1,-1)` does
        // the visual flip at display time. So no Y inversion here.
        GLint vx = (GLint)r.x;
        GLint vy = (GLint)r.y;
        GLsizei vw = (GLsizei)r.w;
        GLsizei vh = (GLsizei)r.h;
        glViewport(vx, vy, vw, vh);

        glUniform2f(g_chroma_loc_rect_origin, (float)vx, (float)vy);
        glUniform2f(g_chroma_loc_rect_size, (float)vw, (float)vh);
        glUniform3f(g_chroma_loc_chroma,
                    (float)r.chroma_r / 255.0f,
                    (float)r.chroma_g / 255.0f,
                    (float)r.chroma_b / 255.0f);
        glUniform1f(g_chroma_loc_threshold, (float)r.threshold / 255.0f);

        glActiveTexture(GL_TEXTURE1);
        glBindTexture(GL_TEXTURE_2D, portrait);

        glDrawArrays(GL_TRIANGLE_STRIP, 0, 4);
        composited++;
    }

    // Restore state.
    glBindVertexArray((GLuint)prev_vao);
    glActiveTexture(GL_TEXTURE1);
    glBindTexture(GL_TEXTURE_2D, 0);
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_RECTANGLE, (GLuint)prev_tex_rect);
    glBindTexture(GL_TEXTURE_2D, (GLuint)prev_tex_2d);
    glActiveTexture((GLenum)prev_active_tex);
    glUseProgram((GLuint)prev_program);
    glBindFramebuffer(GL_DRAW_FRAMEBUFFER, (GLuint)prev_fbo_draw);
    glViewport(prev_viewport[0], prev_viewport[1], prev_viewport[2], prev_viewport[3]);
    if (prev_blend) glEnable(GL_BLEND);
    if (prev_depth) glEnable(GL_DEPTH_TEST);
    if (prev_cull)  glEnable(GL_CULL_FACE);
    if (prev_scissor) glEnable(GL_SCISSOR_TEST);

    (void)composited;  // useful for future stats
}

// ---------------------------------------------------------------------
// Metal backend (dormant — compiled but not reachable on KSP 1.12.5)
// ---------------------------------------------------------------------

id<MTLDevice> metal_device() {
    if (!g_metal) return nil;
    return g_metal->MetalDevice();
}

id<MTLTexture> metal_get_or_create_src_tex(uint32_t io_surface_id) {
    {
        std::lock_guard<std::mutex> lock(g_metal_cache_mu);
        auto it = g_metal_cache.find(io_surface_id);
        if (it != g_metal_cache.end()) {
            return (__bridge id<MTLTexture>)it->second;
        }
    }
    id<MTLDevice> dev = metal_device();
    if (!dev) return nil;
    IOSurfaceRef surface = IOSurfaceLookup((IOSurfaceID)io_surface_id);
    if (!surface) return nil;
    MTLTextureDescriptor* desc = [MTLTextureDescriptor
        texture2DDescriptorWithPixelFormat:MTLPixelFormatBGRA8Unorm
                                     width:IOSurfaceGetWidth(surface)
                                    height:IOSurfaceGetHeight(surface)
                                 mipmapped:NO];
    desc.usage = MTLTextureUsageShaderRead;
    desc.storageMode = MTLStorageModeShared;
    id<MTLTexture> tex = [dev newTextureWithDescriptor:desc
                                             iosurface:surface
                                                 plane:0];
    CFRelease(surface);
    if (!tex) return nil;
    std::lock_guard<std::mutex> lock(g_metal_cache_mu);
    g_metal_cache[io_surface_id] = (__bridge_retained void*)tex;
    return tex;
}

void metal_blit(uint32_t io_surface_id) {
    if (g_dest_tex == 0 || !g_metal) return;
    id<MTLTexture> src = metal_get_or_create_src_tex(io_surface_id);
    if (!src) return;
    id<MTLTexture> dst = (__bridge id<MTLTexture>)(void*)g_dest_tex;
    id<MTLCommandBuffer> cb = g_metal->CurrentCommandBuffer();
    id<MTLBlitCommandEncoder> enc = [cb blitCommandEncoder];
    MTLOrigin origin = {0, 0, 0};
    MTLSize size = {src.width, src.height, 1};
    [enc copyFromTexture:src
             sourceSlice:0
             sourceLevel:0
            sourceOrigin:origin
              sourceSize:size
               toTexture:dst
        destinationSlice:0
        destinationLevel:0
       destinationOrigin:origin];
    [enc endEncoding];
    g_blit_count.fetch_add(1, std::memory_order_relaxed);
}

// ---------------------------------------------------------------------
// Unity render event: the one entry point Unity calls on its render
// thread after `GL.IssuePluginEvent(func, eventId)` on the main thread.
// ---------------------------------------------------------------------

static void UNITY_INTERFACE_API OnRenderEvent(int /*event_id*/) {
    uint64_t pending = g_pending_packed.load(std::memory_order_acquire);
    if (pending == 0) return;
    // Skip duplicate work: only blit when gen advances. This is cheap
    // insurance against C# firing render events for unchanged state.
    if (pending == g_last_completed.load(std::memory_order_relaxed)) return;
    uint32_t id = 0, gen = 0;
    unpack_pending(pending, &id, &gen);
    if (id == 0) return;

    switch (g_backend) {
        case Backend::OpenGLCore: gl_blit(id); break;
        case Backend::Metal:       metal_blit(id); break;
        default: break;
    }
    g_last_completed.store(pending, std::memory_order_release);
}

void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType /*type*/) {
    // Nothing to do yet — we resolve the backend lazily on first use
    // inside UnityPluginLoad so Unity's subsystems are definitely up.
}

} // namespace

// ---------------------------------------------------------------------
// Unity plugin lifecycle
// ---------------------------------------------------------------------

extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API
UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    g_unity_interfaces = unityInterfaces;
    if (unityInterfaces) {
        g_graphics = unityInterfaces->Get<IUnityGraphics>();
        g_metal = unityInterfaces->Get<IUnityGraphicsMetal>();
    }
    // Pick backend from Unity's reported renderer. This is the
    // authoritative answer — Metal backend on KSP 1.12.5 is Null
    // regardless of what any command-line flag claims.
    if (g_graphics) {
        UnityGfxRenderer r = g_graphics->GetRenderer();
        switch (r) {
            case kUnityGfxRendererOpenGLCore:
                g_backend = Backend::OpenGLCore; break;
            case kUnityGfxRendererMetal:
                g_backend = Backend::Metal; break;
            default:
                g_backend = Backend::Unknown; break;
        }
        g_graphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
        log_prefixed("UnityPluginLoad: renderer=%d backend=%s",
                     (int)r,
                     g_backend == Backend::OpenGLCore ? "OpenGLCore" :
                     g_backend == Backend::Metal ? "Metal" : "Unknown");
    } else {
        log_prefixed("UnityPluginLoad: IUnityGraphics unavailable");
    }
}

extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API
UnityPluginUnload()
{
    log_prefixed("UnityPluginUnload (blit count=%llu, errors=%llu)",
                 (unsigned long long)g_blit_count.load(),
                 (unsigned long long)g_error_count.load());
    if (g_graphics) {
        g_graphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    }
    // Drop GL cache. Render thread is gone so we can't safely
    // glDeleteTextures here — just forget the names; Unity's context
    // destruction reaps them. Same logic for the chroma program /
    // VAO / VBO and per-stream textures.
    {
        std::lock_guard<std::mutex> lock(g_gl_cache_mu);
        g_gl_cache.clear();
    }
    g_src_fbo = 0;
    g_dst_fbo = 0;
    g_last_dst_tex_attached = 0;
    g_chroma_program = 0;
    g_chroma_vao = 0;
    g_chroma_vbo = 0;
    g_chroma_compile_failed = false;
    {
        std::lock_guard<std::mutex> lock(g_stream_tex_mu);
        g_stream_tex.clear();
    }
    {
        std::lock_guard<std::mutex> lock(g_stream_rects_mu);
        g_stream_rect_count = 0;
    }
    {
        std::lock_guard<std::mutex> lock(g_metal_cache_mu);
        for (auto& kv : g_metal_cache) {
            id<MTLTexture> t = (__bridge_transfer id<MTLTexture>)kv.second;
            (void)t;
        }
        g_metal_cache.clear();
    }
    g_dest_tex = 0;
    g_metal = nullptr;
    g_graphics = nullptr;
    g_unity_interfaces = nullptr;
    g_backend = Backend::Unknown;
}

// ---------------------------------------------------------------------
// Exported C API for the managed plugin side.
// ---------------------------------------------------------------------

extern "C" UNITY_INTERFACE_EXPORT int
DgHudNative_IsReady(void)
{
    return (g_backend != Backend::Unknown) ? 1 : 0;
}

extern "C" UNITY_INTERFACE_EXPORT int
DgHudNative_GetBackend(void)
{
    return static_cast<int>(g_backend);
}

/// C# side tells the plugin which graphics backend Unity is using,
/// queried from `SystemInfo.graphicsDeviceType`. Needed because Unity
/// only invokes `UnityPluginLoad` for plugins registered via the
/// PluginImporter at build time — pure DllImport plugins (like this
/// one) don't get the `IUnityInterfaces*` automatically. For the GL
/// path we don't actually need anything from `IUnityInterfaces` since
/// OpenGL functions work off the current context, so this explicit
/// backend declaration is enough to unblock the render-event path.
///
/// Values:
///   1 = OpenGL Core
///   2 = Metal
extern "C" UNITY_INTERFACE_EXPORT void
DgHudNative_SetBackend(int backend)
{
    Backend old = g_backend;
    switch (backend) {
        case 1: g_backend = Backend::OpenGLCore; break;
        case 2: g_backend = Backend::Metal; break;
        default: g_backend = Backend::Unknown; break;
    }
    if (old != g_backend) {
        log_prefixed("SetBackend: %d → %s", backend,
                     g_backend == Backend::OpenGLCore ? "OpenGLCore" :
                     g_backend == Backend::Metal ? "Metal" : "Unknown");
    }
}

/// Tell the plugin which Unity-owned texture to blit into. Called once
/// from C# after `Texture2D` allocation, passing `tex.GetNativeTexturePtr()`
/// as an opaque handle:
///   * OpenGL Core backend: handle is a GLuint texture name.
///   * Metal backend: handle is an `id<MTLTexture>` pointer (unretained).
/// Width/height must match the texture's dimensions and the sidecar's
/// `VIEWPORT_WIDTH`/`VIEWPORT_HEIGHT`.
extern "C" UNITY_INTERFACE_EXPORT void
DgHudNative_SetTargetTexture(void* nativeTex, int width, int height)
{
    g_dest_tex = reinterpret_cast<uintptr_t>(nativeTex);
    g_dest_w = (uint32_t)width;
    g_dest_h = (uint32_t)height;
    // Force re-attach on next render event.
    g_last_dst_tex_attached = 0;
    log_prefixed("SetTargetTexture: handle=%p %dx%d", nativeTex, width, height);
}

/// Publish the latest (id, gen) pair from C#. Cheap atomic store; the
/// actual GL/Metal work happens inside the next render event.
extern "C" UNITY_INTERFACE_EXPORT void
DgHudNative_UpdatePending(uint32_t io_surface_id, uint32_t io_surface_gen)
{
    g_pending_packed.store(pack_pending(io_surface_id, io_surface_gen),
                           std::memory_order_release);
}

/// Return the render-event function pointer that C# passes to
/// `GL.IssuePluginEvent`. Unity will invoke this on its render thread
/// at the right point in the frame.
typedef void (UNITY_INTERFACE_API *RenderEventFunc)(int);
extern "C" UNITY_INTERFACE_EXPORT RenderEventFunc
DgHudNative_GetRenderEventFunc(void)
{
    return OnRenderEvent;
}

/// Diagnostic stats — C# can log these once per second for visibility.
extern "C" UNITY_INTERFACE_EXPORT void
DgHudNative_GetStats(uint64_t* out_blits, uint64_t* out_errors,
                         uint64_t* out_cache_misses)
{
    if (out_blits) *out_blits = g_blit_count.load(std::memory_order_relaxed);
    if (out_errors) *out_errors = g_error_count.load(std::memory_order_relaxed);
    if (out_cache_misses) *out_cache_misses = g_cache_miss_count.load(std::memory_order_relaxed);
}

/// Read a single 4-byte BGRA pixel from a globally-lookupable
/// IOSurface without going through the GPU path. Used by the
/// latency probe to sample the marker rectangle from whatever
/// canvas IOSurface the sidecar published — the shm payload is
/// stale zeros under the zero-copy pipeline, so this is the only
/// way to observe the live canvas contents from the plugin side.
///
/// Returns 1 on success (pixel written to *out_bgra as a
/// little-endian u32 with bytes ordered B, G, R, A in memory),
/// 0 on failure.
extern "C" UNITY_INTERFACE_EXPORT int
DgHudNative_SamplePixel(uint32_t io_surface_id, int x, int y,
                            uint32_t* out_bgra)
{
    if (!out_bgra || io_surface_id == 0) return 0;
    *out_bgra = 0;
    IOSurfaceRef surface = IOSurfaceLookup((IOSurfaceID)io_surface_id);
    if (!surface) return 0;

    IOReturn lr = IOSurfaceLock(surface, kIOSurfaceLockReadOnly, NULL);
    if (lr != kIOReturnSuccess) {
        CFRelease(surface);
        return 0;
    }

    void* base = IOSurfaceGetBaseAddress(surface);
    size_t stride = IOSurfaceGetBytesPerRow(surface);
    size_t width = IOSurfaceGetWidth(surface);
    size_t height = IOSurfaceGetHeight(surface);
    int ok = 0;
    if (base && (size_t)x < width && (size_t)y < height) {
        const uint8_t* row = (const uint8_t*)base + (size_t)y * stride;
        const uint8_t* px = row + (size_t)x * 4;
        // Memory order is B, G, R, A for BGRA8; read as-is.
        *out_bgra = ((uint32_t)px[0]) |
                    ((uint32_t)px[1] << 8) |
                    ((uint32_t)px[2] << 16) |
                    ((uint32_t)px[3] << 24);
        ok = 1;
    }

    IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, NULL);
    CFRelease(surface);
    return ok;
}

/// Retrieve the most recent error message set by the native plugin.
/// Written to by `set_last_error` from any call site that wants to
/// surface a specific failure to the C# side. Empty string if no
/// error has been recorded.
extern "C" UNITY_INTERFACE_EXPORT void
DgHudNative_GetLastError(char* buf, int buf_len)
{
    if (!buf || buf_len <= 0) return;
    std::lock_guard<std::mutex> lock(g_last_error_mu);
    strncpy(buf, g_last_error, buf_len - 1);
    buf[buf_len - 1] = '\0';
}

/// Return the dimensions of the IOSurface wrapping the given
/// IOSurfaceID. The plugin uses this after a `(id, gen)` roll to
/// decide whether the sidecar has resized — if the source dims don't
/// match the currently-bound destination texture, the plugin tears
/// down + recreates its Texture2D before re-binding via
/// `DgHudNative_SetTargetTexture`.
///
/// First tries the GL cache (cheap — we already have the dims there
/// from the last `gl_get_or_create_rect_tex`). Falls back to
/// `IOSurfaceLookup` + `IOSurfaceGetWidth/Height` when the surface
/// hasn't been wrapped yet (typically the first query after a
/// resize).
///
/// Returns 1 on success, 0 on failure (id=0, lookup miss, or null
/// output pointers).
extern "C" UNITY_INTERFACE_EXPORT int
DgHudNative_GetSourceSize(uint32_t io_surface_id, uint32_t* out_w, uint32_t* out_h)
{
    if (!out_w || !out_h || io_surface_id == 0) return 0;
    {
        std::lock_guard<std::mutex> lock(g_gl_cache_mu);
        auto it = g_gl_cache.find(io_surface_id);
        if (it != g_gl_cache.end()) {
            *out_w = it->second.width;
            *out_h = it->second.height;
            return 1;
        }
    }
    IOSurfaceRef surface = IOSurfaceLookup((IOSurfaceID)io_surface_id);
    if (!surface) return 0;
    *out_w = (uint32_t)IOSurfaceGetWidth(surface);
    *out_h = (uint32_t)IOSurfaceGetHeight(surface);
    CFRelease(surface);
    return 1;
}

/// Backing scale factor of KSP's main NSWindow — 1.0 on a
/// non-Retina display, 2.0 on Retina, occasionally 3.0 on external
/// 5K/8K panels. This maps 1:1 to `window.devicePixelRatio` in the
/// browser, which is what CEF's `device_scale_factor` expects on
/// its end of the pipe.
///
/// Returns 0.0 when no backing scale is determinable (no window
/// yet, headless run) so the C# caller can fall back to
/// `Screen.dpi`. We prefer this over `[NSScreen mainScreen]` alone
/// because the "main screen" from AppKit's perspective is the one
/// with keyboard focus, not necessarily the one showing KSP — on a
/// mixed-DPI multi-monitor setup those differ.
extern "C" UNITY_INTERFACE_EXPORT float
DgHudNative_GetBackingScale(void)
{
    // Walk up to any visible window backing the KSP process. Typical
    // order of fallbacks:
    //   1. keyWindow — the window currently receiving events
    //   2. mainWindow — the app's primary window
    //   3. first visible window — KSP occasionally defers keyWindow
    //      assignment during scene transitions
    //   4. main screen — last resort if no window exists yet
    @autoreleasepool {
        NSApplication* app = [NSApplication sharedApplication];
        if (!app) return 0.0f;
        NSWindow* w = [app keyWindow];
        if (!w) w = [app mainWindow];
        if (!w) {
            for (NSWindow* candidate in [app windows]) {
                if ([candidate isVisible]) { w = candidate; break; }
            }
        }
        if (w) {
            CGFloat s = [w backingScaleFactor];
            if (s > 0.0) return (float)s;
        }
        NSScreen* scr = [NSScreen mainScreen];
        if (scr) {
            CGFloat s = [scr backingScaleFactor];
            if (s > 0.0) return (float)s;
        }
        return 0.0f;
    }
}

/// FNV-1a 32-bit hash. Mirrors the implementation in dg-sidecar's
/// streams.rs and the JS-side pump — every side hashes the same
/// string the same way, so the rect table's `id_hash` joins cleanly
/// against the plugin's stream registry.
static uint32_t fnv1a_32(const char* s) {
    uint32_t h = 0x811C9DC5u;
    for (; s && *s; ++s) {
        h ^= (uint8_t)*s;
        h *= 0x01000193u;
    }
    return h;
}

/// Upload (or replace) the bytes for a stream texture. Called from C#
/// when the mod has captured a fresh frame from a Unity RenderTexture
/// (Kerbal IVA portrait, map view, etc.).
///
/// `bgra_bytes` points to `width * height * 4` bytes in BGRA8
/// premultiplied alpha. The plugin copies them into a per-stream
/// staging buffer; the actual GL upload happens on the render thread
/// the next time the compositor sees this stream's rect.
extern "C" UNITY_INTERFACE_EXPORT void
DgHudNative_PushStreamFrame(uint32_t id_hash, int width, int height,
                            const void* bgra_bytes)
{
    if (id_hash == 0 || width <= 0 || height <= 0 || !bgra_bytes) return;
    size_t byte_count = (size_t)width * (size_t)height * 4;
    std::lock_guard<std::mutex> lock(g_stream_tex_mu);
    StreamTexEntry& e = g_stream_tex[id_hash];  // creates if missing
    e.pending_bytes.resize(byte_count);
    memcpy(e.pending_bytes.data(), bgra_bytes, byte_count);
    e.pending_w = (uint32_t)width;
    e.pending_h = (uint32_t)height;
    e.has_pending = true;
}

/// Forget a stream — drops its GPU texture (deferred to the render
/// thread on next composite if present) and its staging bytes. Call
/// when the corresponding Unity source goes away (Kerbal exits seat,
/// map view closed). The compositor stops drawing it immediately.
extern "C" UNITY_INTERFACE_EXPORT void
DgHudNative_RemoveStream(uint32_t id_hash)
{
    if (id_hash == 0) return;
    std::lock_guard<std::mutex> lock(g_stream_tex_mu);
    auto it = g_stream_tex.find(id_hash);
    if (it == g_stream_tex.end()) return;
    // We can't safely glDeleteTextures from a non-render thread.
    // Marking the entry's tex as 0 effectively orphans the texture
    // (Unity reaps it on context destruction); for v1 this is fine
    // — stream lifecycle is rare. A render-thread eviction queue is
    // a future cleanup.
    if (it->second.tex != 0) {
        log_prefixed("stream %u removed (orphaning tex=%u)",
                     id_hash, it->second.tex);
    }
    g_stream_tex.erase(it);
}

/// Convenience: register a small synthetic checkerboard texture under
/// the stream id "test". Lets the UI verify the chroma-key pipeline
/// end-to-end without any mod-side capture work — mount a
/// `<PunchThrough id="test">` and the checkerboard appears under the
/// chroma fill.
///
/// Returns the FNV-1a hash of "test" so callers can join it against
/// the rect table if needed.
extern "C" UNITY_INTERFACE_EXPORT uint32_t
DgHudNative_RegisterTestStream(int width, int height)
{
    if (width <= 0) width = 128;
    if (height <= 0) height = 128;
    std::vector<uint8_t> px((size_t)width * (size_t)height * 4);
    const int cell = 16;
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            bool a = ((x / cell) + (y / cell)) & 1;
            uint8_t* p = &px[((size_t)y * width + x) * 4];
            // BGRA premultiplied. Two contrasty colors.
            if (a) { p[0] = 0xCC; p[1] = 0x44; p[2] = 0x88; p[3] = 0xFF; }
            else   { p[0] = 0x22; p[1] = 0x22; p[2] = 0x22; p[3] = 0xFF; }
        }
    }
    uint32_t h = fnv1a_32("test");
    DgHudNative_PushStreamFrame(h, width, height, px.data());
    log_prefixed("test stream registered (id_hash=0x%08x, %dx%d)", h, width, height);
    return h;
}

/// Push the latest punch-through stream rect snapshot to the plugin.
/// Called from C# each Update() after reading the SHM stream-rect
/// table (sidecar wrote it in response to a `cefQuery` from the page).
///
/// `data` points to `count * sizeof(StreamRectSlot)` bytes laid out
/// per the dg-shm v4 spec; `count` is clamped to `kStreamRectCapacity`.
/// Cheap mutex copy — the actual compositing happens on the render
/// thread.
extern "C" UNITY_INTERFACE_EXPORT void
DgHudNative_UpdateStreamRects(const void* data, int count)
{
    if (count < 0) count = 0;
    if (count > kStreamRectCapacity) count = kStreamRectCapacity;
    std::lock_guard<std::mutex> lock(g_stream_rects_mu);
    int prev_count = g_stream_rect_count;
    g_stream_rect_count = count;
    if (count > 0 && data) {
        memcpy(g_stream_rects, data, (size_t)count * sizeof(StreamRectSlot));
    }
    // Revision bump lets future render-thread code skip work when
    // nothing changed; for now we still log on every update for
    // visibility.
    uint64_t rev = g_stream_rect_revision.fetch_add(1, std::memory_order_release) + 1;
    if (count != prev_count) {
        log_prefixed("streams: %d active (rev=%llu)",
                     count, (unsigned long long)rev);
    }
}

/// Detailed error breakdown so C# can tell which branch is failing
/// without relying on native stderr output (which Unity does not
/// capture on macOS in our deployment).
extern "C" UNITY_INTERFACE_EXPORT void
DgHudNative_GetErrorBreakdown(
    uint64_t* out_no_ctx, uint64_t* out_iosurface_lookup,
    uint64_t* out_cgl_tex_image, uint64_t* out_fbo_incomplete,
    uint64_t* out_no_dest)
{
    if (out_no_ctx) *out_no_ctx = g_err_no_ctx.load(std::memory_order_relaxed);
    if (out_iosurface_lookup) *out_iosurface_lookup = g_err_iosurface_lookup.load(std::memory_order_relaxed);
    if (out_cgl_tex_image) *out_cgl_tex_image = g_err_cgl_tex_image.load(std::memory_order_relaxed);
    if (out_fbo_incomplete) *out_fbo_incomplete = g_err_fbo_incomplete.load(std::memory_order_relaxed);
    if (out_no_dest) *out_no_dest = g_err_no_dest.load(std::memory_order_relaxed);
}

#pragma clang diagnostic pop
