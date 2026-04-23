# Dragonglass — top-level build orchestration

default:
    @just --list

# --- TypeScript (ui/) ---

ui-dev:
    cd ui && npm run dev

ui-dev-workbench:
    cd ui && npm run dev:workbench

ui-build:
    cd ui && npm run build

ui-typecheck:
    cd ui && npm run typecheck

# --- Rust (crates/) ---

sidecar-check:
    cargo check

sidecar-test:
    cargo test

# Build sidecar binaries, assemble macOS .app bundle, and ad-hoc codesign.
# Requires bundle-cef-app: cargo install cef --features build-util --bin bundle-cef-app
sidecar-bundle:
    #!/usr/bin/env bash
    set -euo pipefail

    if ! command -v bundle-cef-app &>/dev/null; then
        echo "bundle-cef-app not found in PATH" >&2
        echo "Install it once via: cargo install cef --features build-util --bin bundle-cef-app" >&2
        exit 1
    fi

    : "${CEF_PATH:=$HOME/.local/share/cef}"
    export CEF_PATH

    rm -rf crates/dg-sidecar/target/bundle
    cd crates/dg-sidecar
    bundle-cef-app dg-sidecar \
        -o target/bundle \
        -i dev.dragonglass.sidecar \
        -d "Dragonglass Sidecar" \
        -v 0.1.0

    # Mark as an agent app so it doesn't appear in the Dock or Cmd-Tab.
    /usr/libexec/PlistBuddy -c "Add :LSUIElement bool true" \
        target/bundle/dg-sidecar.app/Contents/Info.plist

    echo "Signing dg-sidecar.app..."
    codesign -f -s - --deep target/bundle/dg-sidecar.app >/dev/null
    echo "Built → crates/dg-sidecar/target/bundle/dg-sidecar.app"

# Build sidecar binaries + stage CEF Windows runtime next to them so
# dg-sidecar.exe is self-contained. The cef-dll-sys build script
# extracts the CEF distribution under target/<profile>/build/cef-dll-sys-*/out/
# cef_windows_x86_64/ — we copy the runtime DLLs, pak/data files, and
# locales/ out of there. Resulting layout: target/bundle/windows-x64/
# contains dg-sidecar.exe, dg-sidecar-helper.exe, and everything CEF
# needs to boot.
sidecar-bundle-windows:
    #!/usr/bin/env bash
    set -euo pipefail

    out="target/bundle/windows-x64"
    rm -rf "$out"
    mkdir -p "$out"

    echo "Building dg-sidecar + dg-sidecar-helper (release)..."
    cargo build --release --bin dg-sidecar --bin dg-sidecar-helper

    cp target/release/dg-sidecar.exe        "$out/"
    cp target/release/dg-sidecar-helper.exe "$out/"

    # Locate the most recently built CEF extraction. The hash in the
    # path varies per cargo metadata — pick the newest match.
    cef_dir=$(ls -dt target/release/build/cef-dll-sys-*/out/cef_windows_x86_64 2>/dev/null | head -1)
    if [ -z "$cef_dir" ] || [ ! -d "$cef_dir" ]; then
        echo "error: CEF distribution not found under target/release/build/cef-dll-sys-*/out/" >&2
        echo "       run 'cargo build --release' first — it extracts CEF as a build step." >&2
        exit 1
    fi
    echo "Staging CEF runtime from $cef_dir"

    # Runtime DLLs + data. The SDK pieces (include/, libcef_dll/,
    # cmake/, libcef.lib, CMakeLists.txt, archive.json, CREDITS.html,
    # bootstrap*.exe) are *not* needed at runtime and stay in target/.
    cp "$cef_dir"/*.dll "$out/"
    cp "$cef_dir"/*.pak "$out/"
    cp "$cef_dir"/*.bin "$out/" 2>/dev/null || true
    cp "$cef_dir"/*.dat "$out/"
    cp "$cef_dir"/*.json "$out/" 2>/dev/null || true
    cp -R "$cef_dir/locales" "$out/"

    echo "Built → $out"

# --- C# (mod/) ---

mod-build config="Release":
    cd mod && dotnet build -c {{config}}

# --- Native plugin (mod/native/) ---

native-build-darwin:
    #!/usr/bin/env bash
    set -euo pipefail
    cd mod/native/darwin-universal

    src="src/DgHudNative.mm"
    incdir="include"
    bundle="out/DgHudNative.bundle"
    inner="$bundle/Contents/MacOS/DgHudNative"
    flat="out/libDgHudNative.dylib"

    mkdir -p out

    arches="-arch x86_64 -arch arm64"
    cxxflags="-ObjC++ -std=c++17 -fobjc-arc -fPIC -O2 $arches \
              -Wall -Wextra -Wno-unused-parameter -I$incdir"
    frameworks="-framework Foundation -framework AppKit -framework Metal \
                -framework OpenGL -framework IOSurface \
                -framework CoreFoundation"

    # Bundle dylib
    echo "Building $bundle..."
    mkdir -p "$bundle/Contents/MacOS"
    clang++ $cxxflags -dynamiclib $arches $frameworks \
        -install_name "@rpath/$bundle/Contents/MacOS/DgHudNative" \
        -o "$inner" "$src"
    echo "Exported symbols:"
    nm -gU "$inner" | grep -E 'DgHudNative_|UnityPluginLoad|UnityPluginUnload' || true

    # Flat sibling dylib (Mono PInvoke fallback)
    echo "Building $flat..."
    clang++ $cxxflags -dynamiclib $arches $frameworks \
        -install_name "@rpath/$flat" \
        -o "$flat" "$src"

    # Info.plist
    mkdir -p "$bundle/Contents"
    cat > "$bundle/Contents/Info.plist" <<'PLIST'
    <?xml version="1.0" encoding="UTF-8"?>
    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
    <plist version="1.0">
    <dict>
      <key>CFBundleExecutable</key><string>DgHudNative</string>
      <key>CFBundleIdentifier</key><string>dev.dragonglass.native</string>
      <key>CFBundleName</key><string>DgHudNative</string>
      <key>CFBundlePackageType</key><string>BNDL</string>
      <key>CFBundleVersion</key><string>0.1</string>
      <key>CFBundleShortVersionString</key><string>0.1</string>
    </dict>
    </plist>
    PLIST

    echo "Built: $(pwd)/$bundle"

# Future: native-build-linux

native-clean:
    rm -rf mod/native/darwin-universal/out

native-test: native-build-darwin
    #!/usr/bin/env bash
    set -euo pipefail
    cd mod/native/darwin-universal
    echo "Smoke-testing DgHudNative.bundle..."
    clang -o /tmp/dghudnative-smoke test/smoke.c
    /tmp/dghudnative-smoke ./out/DgHudNative.bundle/Contents/MacOS/DgHudNative

# Build Windows native plugin (Rust cdylib -> DgHudNative.dll) and stage
# it under mod/native/windows-x64/out/. Parity with native-build-darwin.
native-build-windows:
    #!/usr/bin/env bash
    set -euo pipefail
    cargo build --release -p dg-hud-plugin-win
    mkdir -p mod/native/windows-x64/out
    cp target/release/DgHudNative.dll mod/native/windows-x64/out/
    echo "Built → mod/native/windows-x64/out/DgHudNative.dll"

# --- Release packaging ---

# Generate combined third-party license notices (.NET + Rust deps).
# cargo-about is optional — without it we emit a stub marker so the
# downstream dist recipes still produce a zip.
notices:
    #!/usr/bin/env bash
    set -euo pipefail
    mkdir -p release
    if cargo about --version >/dev/null 2>&1; then
        cargo about generate about.hbs -o release/THIRD_PARTY_NOTICES_RUST 2>/dev/null
    else
        echo "cargo-about not installed — skipping Rust crate notices (install via: cargo install cargo-about)" >&2
        echo "Rust crate notices omitted (cargo-about not installed)." > release/THIRD_PARTY_NOTICES_RUST
    fi
    cat mod/THIRD_PARTY_NOTICES release/THIRD_PARTY_NOTICES_RUST > release/THIRD_PARTY_NOTICES

# Telemetry plugin: WebSocket server broadcasting vessel state (standalone)
dist-telemetry: (mod-build "Release")
    #!/usr/bin/env bash
    set -euo pipefail
    stage=$(mktemp -d)
    root="$stage/GameData/Dragonglass_Telemetry"

    mkdir -p "$root/Plugins"
    cp mod/Dragonglass.Telemetry/build/Dragonglass.Telemetry.dll "$root/Plugins/"

    cp LICENSE "$root/"

    mkdir -p release
    out="{{justfile_directory()}}/release/Dragonglass_Telemetry.zip"
    rm -f "$out"
    cd "$stage"
    case "$(uname -s)" in
        MINGW*|MSYS*|CYGWIN*)
            src_win=$(cygpath -w "$PWD/GameData")
            out_win=$(cygpath -w "$out")
            powershell.exe -NoProfile -Command "Compress-Archive -Path '$src_win' -DestinationPath '$out_win' -Force" >/dev/null
            ;;
        *)
            zip -qr "$out" GameData/
            ;;
    esac
    rm -rf "$stage"
    echo "Built → release/Dragonglass_Telemetry.zip"

# Hud mod: Core plugin + Stock flight UI (platform-independent)
dist-hud: ui-build (mod-build "Release") notices
    #!/usr/bin/env bash
    set -euo pipefail
    stage=$(mktemp -d)
    root="$stage/GameData/Dragonglass_Hud"

    # Core plugin + bundled Harmony. KSP doesn't provide Harmony
    # itself; Dragonglass.Hud patches the stock stager/flight UI via
    # Harmony so we ship 0Harmony.dll alongside. KSP's AssemblyLoader
    # picks the highest-versioned copy if another mod also ships it.
    mkdir -p "$root/Plugins"
    cp mod/Dragonglass.Hud/build/Dragonglass.Hud.dll       "$root/Plugins/"
    cp mod/Dragonglass.Hud/build/0Harmony.dll              "$root/Plugins/"

    # Stock flight UI
    mkdir -p "$root/UI/Stock"
    cp -R ui/apps/stock/dist/* "$root/UI/Stock/"

    cp LICENSE "$root/"
    cp release/THIRD_PARTY_NOTICES "$root/"

    mkdir -p release
    out="{{justfile_directory()}}/release/Dragonglass_Hud.zip"
    rm -f "$out"
    cd "$stage"
    case "$(uname -s)" in
        MINGW*|MSYS*|CYGWIN*)
            src_win=$(cygpath -w "$PWD/GameData")
            out_win=$(cygpath -w "$out")
            powershell.exe -NoProfile -Command "Compress-Archive -Path '$src_win' -DestinationPath '$out_win' -Force" >/dev/null
            ;;
        *)
            zip -qr "$out" GameData/
            ;;
    esac
    rm -rf "$stage"
    echo "Built → release/Dragonglass_Hud.zip"

# macOS arm64 sidecar + native plugin
dist-hud-darwin-arm64: native-build-darwin sidecar-bundle
    #!/usr/bin/env bash
    set -euo pipefail
    stage=$(mktemp -d)
    root="$stage/GameData/Dragonglass_Hud"

    # Native rendering plugin (must be in Plugins/ for Unity's DllImport loader)
    # Both the .bundle directory and flat .dylib — Mono tries both patterns.
    mkdir -p "$root/Plugins"
    rsync -a mod/native/darwin-universal/out/DgHudNative.bundle "$root/Plugins/"
    cp mod/native/darwin-universal/out/libDgHudNative.dylib "$root/Plugins/"

    # CEF sidecar (.app bundle with framework, helpers, signature)
    mkdir -p "$root/Sidecar"
    rsync -a crates/dg-sidecar/target/bundle/dg-sidecar.app "$root/Sidecar/"

    mkdir -p release
    rm -f "{{justfile_directory()}}/release/Dragonglass_Hud_darwin_arm64.zip"
    cd "$stage"
    zip -qry "{{justfile_directory()}}/release/Dragonglass_Hud_darwin_arm64.zip" GameData/
    rm -rf "$stage"
    echo "Built → release/Dragonglass_Hud_darwin_arm64.zip"

# Windows x64 sidecar + native plugin
dist-hud-windows-x64: native-build-windows sidecar-bundle-windows
    #!/usr/bin/env bash
    set -euo pipefail
    stage=$(mktemp -d)
    root="$stage/GameData/Dragonglass_Hud"

    # Native rendering plugin ships alongside the managed DLL in
    # Plugins/: Unity/Mono's PInvoke resolver on Windows uses its own
    # hardcoded search paths (KSP_x64_Data/Mono/, Plugins/) and ignores
    # Windows' SetDllDirectory override, so LoadLibrary preloads aren't
    # picked up. DgHudNative.dll carries a VS_VERSIONINFO resource
    # (set via winresource in crates/dg-hud-plugin-win/build.rs) so
    # KSP's UrlDir scanner doesn't throw on its FileVersion.
    # The Plugins/ dir is usually populated by dist-hud running
    # earlier in the `install` chain, but this recipe may also run
    # standalone — mkdir -p covers both cases.
    mkdir -p "$root/Plugins"
    cp mod/native/windows-x64/out/DgHudNative.dll "$root/Plugins/"

    # CEF sidecar bundle (dg-sidecar.exe + helper + libcef.dll + all
    # the other CEF runtime DLLs + locales/) goes under PluginData/:
    # KSP's UrlDir.Create skips any directory literally named
    # "PluginData" (alongside ".svn" and "zDeprecated"), hiding the
    # whole subtree from the scanner. Without this, libcef.dll's
    # "146.0.10+g..." FileVersion and vulkan-1.dll's "Vulkan Loader"
    # FileVersion halt GameDatabase loading at "Loading part upgrades".
    # SidecarHost.ResolveBinary knows the PluginData/ prefix.
    mkdir -p "$root/PluginData/Sidecar"
    cp -R target/bundle/windows-x64/. "$root/PluginData/Sidecar/"

    mkdir -p release
    out="{{justfile_directory()}}/release/Dragonglass_Hud_windows_x64.zip"
    rm -f "$out"
    # Use PowerShell's Compress-Archive — built into Windows 10+, no extra deps.
    # -Path expects a Windows path; convert via cygpath.
    stage_win=$(cygpath -w "$stage/GameData")
    out_win=$(cygpath -w "$out")
    powershell.exe -NoProfile -Command "Compress-Archive -Path '$stage_win' -DestinationPath '$out_win' -Force"
    rm -rf "$stage"
    echo "Built → release/Dragonglass_Hud_windows_x64.zip"

# Build all release zips
dist: dist-telemetry dist-hud dist-hud-darwin-arm64
    #!/usr/bin/env bash
    echo "Release artifacts:"
    ls -lh release/*.zip

# --- Install into KSP ---

# Build and install into a KSP directory. Detects platform automatically.
#   just install ~/KSP_osx
#   just install ~/Documents/KSP_win64
install ksp_path: dist-telemetry dist-hud
    #!/usr/bin/env bash
    set -euo pipefail
    ksp="{{ksp_path}}"

    if [ ! -d "$ksp" ]; then
        echo "error: KSP directory not found: $ksp" >&2
        exit 1
    fi

    # Build the platform-specific sidecar/native zip on demand.
    os="$(uname -s)"
    case "$os" in
        Darwin)                   just dist-hud-darwin-arm64 ;;
        MINGW*|MSYS*|CYGWIN*)     just dist-hud-windows-x64 ;;
        *)                        echo "error: no dist recipe for $os" >&2; exit 1 ;;
    esac

    # Clean previous install
    rm -rf "$ksp/GameData/Dragonglass_Telemetry"
    rm -rf "$ksp/GameData/Dragonglass_Hud"

    # Pick an extractor that matches the archive format. On Windows the
    # zips are produced by PowerShell's Compress-Archive (backslash
    # separators make `unzip` warn + exit 1 under set -e); use
    # Expand-Archive for symmetry. On mac/linux keep plain unzip.
    extract() {
        case "$os" in
            MINGW*|MSYS*|CYGWIN*)
                src_win=$(cygpath -w "$1")
                dst_win=$(cygpath -w "$2")
                powershell.exe -NoProfile -Command "Expand-Archive -Path '$src_win' -DestinationPath '$dst_win' -Force" >/dev/null
                ;;
            *)
                unzip -qo "$1" -d "$2"
                ;;
        esac
    }

    # Unpack universal packages
    for zip in \
        release/Dragonglass_Telemetry.zip \
        release/Dragonglass_Hud.zip \
    ; do
        extract "$zip" "$ksp"
    done

    # Platform-specific sidecar + native plugin
    os="$(uname -s)"
    arch="$(uname -m)"
    case "${os}_${arch}" in
        Darwin_arm64)                       zip="release/Dragonglass_Hud_darwin_arm64.zip"; use_ditto=1 ;;
        MINGW*_x86_64|MSYS*_x86_64|CYGWIN*_x86_64) zip="release/Dragonglass_Hud_windows_x64.zip"; use_ditto=0 ;;
        *)             echo "error: no sidecar package for ${os}_${arch}" >&2; exit 1 ;;
    esac

    if [ -f "$zip" ]; then
        if [ "$use_ditto" = 1 ]; then
            # macOS: ditto preserves symlinks + resource forks from the zip.
            ditto -x -k "$zip" "$ksp"
        else
            # Windows/Linux: use extract() helper defined above.
            extract "$zip" "$ksp"
        fi
    else
        echo "error: $zip not found — run 'just dist' first" >&2
        exit 1
    fi

    echo "Installed → $ksp/GameData/Dragonglass_{Telemetry,Hud}"

# --- All ---

build: ui-build sidecar-bundle mod-build native-build-darwin

check: ui-typecheck sidecar-check
    cd mod && dotnet build --no-restore
