// Minimal subset of Unity's IUnityGraphicsMetal.h. Only `MetalDevice()`
// is actually used by DgHudNative — the rest of the interface is
// declared for ABI compatibility so the vtable offsets match Unity's
// implementation.

#pragma once

#include "IUnityInterface.h"

#ifdef __OBJC__
    @protocol MTLDevice;
    @protocol MTLCommandBuffer;
    @protocol MTLCommandEncoder;
    @protocol MTLTexture;
    @class MTLRenderPassDescriptor;
    typedef id<MTLDevice>        MTLDeviceRef;
    typedef id<MTLCommandBuffer> MTLCommandBufferRef;
    typedef id<MTLCommandEncoder> MTLCommandEncoderRef;
    typedef id<MTLTexture>       MTLTextureRef;
    typedef MTLRenderPassDescriptor* MTLRenderPassDescriptorRef;
#else
    typedef void* MTLDeviceRef;
    typedef void* MTLCommandBufferRef;
    typedef void* MTLCommandEncoderRef;
    typedef void* MTLTextureRef;
    typedef void* MTLRenderPassDescriptorRef;
#endif

UNITY_DECLARE_INTERFACE(IUnityGraphicsMetal)
{
    MTLCommandBufferRef         (UNITY_INTERFACE_API * CurrentCommandBuffer)();
    MTLCommandEncoderRef        (UNITY_INTERFACE_API * CurrentCommandEncoder)();
    void                        (UNITY_INTERFACE_API * EndCurrentCommandEncoder)();
    MTLRenderPassDescriptorRef  (UNITY_INTERFACE_API * CurrentRenderPassDescriptor)();
    MTLDeviceRef                (UNITY_INTERFACE_API * MetalDevice)();
    MTLTextureRef               (UNITY_INTERFACE_API * TextureFromRenderBuffer)(void* rb);
    MTLTextureRef               (UNITY_INTERFACE_API * TextureFromNativeTexture)(unsigned int nativeTex);
};
UNITY_REGISTER_INTERFACE_GUID(0x992C8EAD95844F76ULL, 0xB73647D9F259C86CULL, IUnityGraphicsMetal)
