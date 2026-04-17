// Minimal subset of Unity's IUnityInterface.h — enough to satisfy
// UnityPluginLoad/UnityPluginUnload and the IUnityInterfaces::Get<>()
// template. Vendored from the public Unity NativeRenderingPlugin sample.
// We only declare the symbols we actually touch.

#pragma once

#include <stdint.h>

#if defined(_WIN32)
    #define UNITY_INTERFACE_API __stdcall
    #define UNITY_INTERFACE_EXPORT __declspec(dllexport)
#else
    #define UNITY_INTERFACE_API
    #define UNITY_INTERFACE_EXPORT __attribute__((visibility("default")))
#endif

// GUID used to look up an interface from `IUnityInterfaces`. Each Unity
// interface declares its own constant pair of u64s.
typedef struct UnityInterfaceGUID {
    uint64_t m_GUIDHigh;
    uint64_t m_GUIDLow;

#ifdef __cplusplus
    UnityInterfaceGUID(uint64_t high, uint64_t low)
        : m_GUIDHigh(high), m_GUIDLow(low) {}
    inline bool operator==(const UnityInterfaceGUID& o) const {
        return m_GUIDHigh == o.m_GUIDHigh && m_GUIDLow == o.m_GUIDLow;
    }
#endif
} UnityInterfaceGUID;

// Base interface type. All Unity subsystem interfaces derive from this
// in concept; at the C ABI boundary it's just an opaque pointer.
struct IUnityInterface {};

#ifdef __cplusplus
    #define UNITY_DECLARE_INTERFACE(NAME) struct NAME : IUnityInterface
    #define UNITY_REGISTER_INTERFACE_GUID(HIGH, LOW, NAME) \
        template<> inline UnityInterfaceGUID UnityInterfaceGUIDGetter<NAME>::Get() { \
            return UnityInterfaceGUID(HIGH, LOW); \
        }

    template<typename T> struct UnityInterfaceGUIDGetter {
        static UnityInterfaceGUID Get();
    };
#else
    #define UNITY_DECLARE_INTERFACE(NAME) typedef struct NAME NAME; struct NAME
    #define UNITY_REGISTER_INTERFACE_GUID(HIGH, LOW, NAME)
#endif

// IUnityInterfaces is the registry Unity passes to UnityPluginLoad. We
// fetch specific graphics interfaces (e.g. IUnityGraphicsMetal) out of it
// at load time and stash the pointers.
struct IUnityInterfaces
{
    IUnityInterface* (UNITY_INTERFACE_API * GetInterface)(UnityInterfaceGUID guid);
    void             (UNITY_INTERFACE_API * RegisterInterface)(UnityInterfaceGUID guid, IUnityInterface* ptr);
    IUnityInterface* (UNITY_INTERFACE_API * GetInterfaceSplit)(uint64_t guidHigh, uint64_t guidLow);
    void             (UNITY_INTERFACE_API * RegisterInterfaceSplit)(uint64_t guidHigh, uint64_t guidLow, IUnityInterface* ptr);

#ifdef __cplusplus
    template<typename T>
    T* Get() {
        return static_cast<T*>(GetInterface(UnityInterfaceGUIDGetter<T>::Get()));
    }
#endif
};
