// Smoke test: dlopen the bundle and verify each exported symbol is
// resolvable. Catches link errors + missing `__attribute__((visibility))`
// markers before we ship the bundle into GameData/Plugins/.
//
// Does not exercise UnityPluginLoad — that requires a real
// IUnityInterfaces table which only Unity constructs.

#include <stdio.h>
#include <dlfcn.h>

int main(int argc, char** argv) {
    if (argc < 2) {
        fprintf(stderr, "usage: %s <path/to/DgHudNative.bundle>\n", argv[0]);
        return 2;
    }
    void* h = dlopen(argv[1], RTLD_NOW);
    if (!h) {
        fprintf(stderr, "dlopen failed: %s\n", dlerror());
        return 1;
    }
    const char* names[] = {
        "UnityPluginLoad",
        "UnityPluginUnload",
        "DgHudNative_IsReady",
        "DgHudNative_PollSurface",
        "DgHudNative_ReleaseTexture",
    };
    int failed = 0;
    for (unsigned i = 0; i < sizeof(names) / sizeof(names[0]); i++) {
        void* sym = dlsym(h, names[i]);
        printf("  %-32s %s\n", names[i], sym ? "OK" : "MISSING");
        if (!sym) failed++;
    }
    dlclose(h);
    return failed ? 1 : 0;
}
