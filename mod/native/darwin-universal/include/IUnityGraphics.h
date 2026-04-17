// Minimal subset of Unity's IUnityGraphics.h. We don't actually subscribe
// to graphics events — we just need the interface declared for Metal to
// hang off of, and we use `IsAvailable()` + `MetalDevice()` at init time.

#pragma once

#include "IUnityInterface.h"

typedef enum UnityGfxRenderer
{
    kUnityGfxRendererNull           = 4,
    kUnityGfxRendererOpenGLCore     = 17,
    kUnityGfxRendererD3D11          = 2,
    kUnityGfxRendererD3D12          = 18,
    kUnityGfxRendererOpenGLES20     = 8,
    kUnityGfxRendererOpenGLES30     = 11,
    kUnityGfxRendererMetal          = 16,
    kUnityGfxRendererVulkan         = 21,
} UnityGfxRenderer;

typedef enum UnityGfxDeviceEventType
{
    kUnityGfxDeviceEventInitialize    = 0,
    kUnityGfxDeviceEventShutdown      = 1,
    kUnityGfxDeviceEventBeforeReset   = 2,
    kUnityGfxDeviceEventAfterReset    = 3,
} UnityGfxDeviceEventType;

typedef void (UNITY_INTERFACE_API * IUnityGraphicsDeviceEventCallback)(UnityGfxDeviceEventType eventType);

UNITY_DECLARE_INTERFACE(IUnityGraphics)
{
    UnityGfxRenderer(UNITY_INTERFACE_API * GetRenderer)();
    void(UNITY_INTERFACE_API * RegisterDeviceEventCallback)(IUnityGraphicsDeviceEventCallback callback);
    void(UNITY_INTERFACE_API * UnregisterDeviceEventCallback)(IUnityGraphicsDeviceEventCallback callback);
    int(UNITY_INTERFACE_API * ReserveEventIDRange)(int count);
};
UNITY_REGISTER_INTERFACE_GUID(0x7CBA0A9CA4DDB544ULL, 0x8C5AD4926EB17B11ULL, IUnityGraphics)
