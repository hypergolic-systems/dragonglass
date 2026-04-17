// Obj-C implementation of the sidecar's IOSurface canvas + Metal
// blitter. Called from Rust via a minimal C API declared in
// iosurface_blitter.h. Split out of the Rust module because
// metal-rs's bindings don't cover `newTextureWithDescriptor:iosurface:plane:`
// and fighting the crate's abstractions isn't worth it — raw Obj-C
// is the path of least resistance here.
//
// Architecture recap: this module owns a persistent "canvas"
// IOSurface allocated with `kIOSurfaceIsGlobal = true` so it's
// lookupable cross-process via `IOSurfaceLookup(id)` from the KSP
// plugin. Every `on_accelerated_paint`, the Rust side calls
// `dg_blitter_blit_from_cef` with CEF's per-frame IOSurface
// handle; we wrap it as an MTLTexture (cached by id) and issue a
// single MTLBlit copy into the canvas texture. Plugin reads the
// canvas by ID and CGL-wraps it as a GL_TEXTURE_RECTANGLE.

#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <IOSurface/IOSurface.h>
#include <stdint.h>
#include <stdio.h>

#include "iosurface_blitter.h"

@interface KspBlitter : NSObject
@property (nonatomic, strong) id<MTLDevice> device;
@property (nonatomic, strong) id<MTLCommandQueue> queue;
@property (nonatomic, strong) id<MTLTexture> canvasTexture;
@property (nonatomic) IOSurfaceRef canvasSurface;
@property (nonatomic) IOSurfaceID canvasID;
@property (nonatomic) uint32_t width;
@property (nonatomic) uint32_t height;
@property (nonatomic, strong) NSMutableDictionary<NSNumber*, id<MTLTexture>>* srcCache;
@end

@implementation KspBlitter

- (instancetype)initWithWidth:(uint32_t)width height:(uint32_t)height {
    self = [super init];
    if (!self) return nil;
    self.width = width;
    self.height = height;

    self.device = MTLCreateSystemDefaultDevice();
    if (!self.device) {
        fprintf(stderr, "[dragonglass] blitter: MTLCreateSystemDefaultDevice returned nil\n");
        return nil;
    }
    self.queue = [self.device newCommandQueue];

    // Build the property dictionary for IOSurfaceCreate with
    // kIOSurfaceIsGlobal=true. This is the critical flag: global
    // surfaces are lookupable by ID from any process in the same
    // user session via IOSurfaceLookup(id). Apple marks the flag
    // deprecated for security ("global surfaces are insecure"),
    // but confirmed empirically on macOS 15 that it still works.
    NSDictionary* props = @{
        (__bridge NSString*)kIOSurfaceWidth:           @(width),
        (__bridge NSString*)kIOSurfaceHeight:          @(height),
        (__bridge NSString*)kIOSurfaceBytesPerElement: @4,
        (__bridge NSString*)kIOSurfacePixelFormat:     @(0x42475241), // 'BGRA'
        (__bridge NSString*)kIOSurfaceIsGlobal:        @YES,
    };
    self.canvasSurface = IOSurfaceCreate((__bridge CFDictionaryRef)props);
    if (!self.canvasSurface) {
        fprintf(stderr, "[dragonglass] blitter: IOSurfaceCreate returned NULL\n");
        return nil;
    }
    self.canvasID = IOSurfaceGetID(self.canvasSurface);

    // Wrap the canvas IOSurface as an MTLTexture with both read and
    // render-target usage so the Metal blit encoder can write into it.
    MTLTextureDescriptor* desc = [MTLTextureDescriptor
        texture2DDescriptorWithPixelFormat:MTLPixelFormatBGRA8Unorm
                                     width:width
                                    height:height
                                 mipmapped:NO];
    desc.usage = MTLTextureUsageShaderRead | MTLTextureUsageRenderTarget;
    desc.storageMode = MTLStorageModeShared;
    self.canvasTexture = [self.device newTextureWithDescriptor:desc
                                                     iosurface:self.canvasSurface
                                                         plane:0];
    if (!self.canvasTexture) {
        fprintf(stderr, "[dragonglass] blitter: canvas newTextureWithDescriptor returned nil\n");
        CFRelease(self.canvasSurface);
        self.canvasSurface = NULL;
        return nil;
    }

    self.srcCache = [NSMutableDictionary dictionary];
    fprintf(stderr, "[dragonglass] blitter: canvas ready id=0x%x (%ux%u)\n",
            self.canvasID, width, height);
    return self;
}

- (void)dealloc {
    if (self.canvasSurface) {
        CFRelease(self.canvasSurface);
    }
}

- (void)blitFromCefSurface:(void*)cefHandle withID:(uint32_t)cefID {
    if (!cefHandle) return;

    // Look up or create the source MTLTexture wrapping this CEF
    // IOSurface. CEF rotates between ~2 surfaces so this cache stays
    // at size 2 in steady state — one MTLTexture creation per
    // distinct CEF IOSurface we ever see.
    NSNumber* key = @(cefID);
    id<MTLTexture> src = self.srcCache[key];
    if (!src) {
        IOSurfaceRef cefSurface = (IOSurfaceRef)cefHandle;
        MTLTextureDescriptor* desc = [MTLTextureDescriptor
            texture2DDescriptorWithPixelFormat:MTLPixelFormatBGRA8Unorm
                                         width:self.width
                                        height:self.height
                                     mipmapped:NO];
        desc.usage = MTLTextureUsageShaderRead;
        desc.storageMode = MTLStorageModeShared;
        src = [self.device newTextureWithDescriptor:desc
                                          iosurface:cefSurface
                                              plane:0];
        if (!src) {
            fprintf(stderr, "[dragonglass] blitter: wrap CEF surface 0x%x failed\n", cefID);
            return;
        }
        self.srcCache[key] = src;
        fprintf(stderr, "[dragonglass] blitter: cached CEF src for id=0x%x (pool now %lu)\n",
                cefID, (unsigned long)self.srcCache.count);
    }

    // Issue a single MTLBlit encoder copy from CEF's source into our
    // canvas. No fencing — we accept single-frame tearing risk in
    // exchange for simplicity; at 60fps it's invisible.
    id<MTLCommandBuffer> cmd = [self.queue commandBuffer];
    id<MTLBlitCommandEncoder> enc = [cmd blitCommandEncoder];
    MTLOrigin origin = {0, 0, 0};
    MTLSize size = MTLSizeMake(self.width, self.height, 1);
    [enc copyFromTexture:src
             sourceSlice:0
             sourceLevel:0
            sourceOrigin:origin
              sourceSize:size
               toTexture:self.canvasTexture
        destinationSlice:0
        destinationLevel:0
       destinationOrigin:origin];
    [enc endEncoding];
    [cmd commit];
}

@end

// ---------------------------------------------------------------------
// C API
// ---------------------------------------------------------------------

void* dg_blitter_create(uint32_t width, uint32_t height) {
    KspBlitter* b = [[KspBlitter alloc] initWithWidth:width height:height];
    return (__bridge_retained void*)b;
}

void dg_blitter_destroy(void* handle) {
    if (!handle) return;
    KspBlitter* b = (__bridge_transfer KspBlitter*)handle;
    (void)b;
}

uint32_t dg_blitter_canvas_id(void* handle) {
    if (!handle) return 0;
    KspBlitter* b = (__bridge KspBlitter*)handle;
    return b.canvasID;
}

void dg_blitter_blit_from_cef(void* handle,
                              void* cef_surface_handle,
                              uint32_t cef_surface_id) {
    if (!handle) return;
    KspBlitter* b = (__bridge KspBlitter*)handle;
    [b blitFromCefSurface:cef_surface_handle withID:cef_surface_id];
}

