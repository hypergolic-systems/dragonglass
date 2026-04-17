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

    echo "Signing dg-sidecar.app..."
    codesign -f -s - --deep target/bundle/dg-sidecar.app >/dev/null
    echo "Built → crates/dg-sidecar/target/bundle/dg-sidecar.app"

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
    frameworks="-framework Foundation -framework Metal \
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

# Future: native-build-linux, native-build-windows

native-clean:
    rm -rf mod/native/darwin-universal/out

native-test: native-build-darwin
    #!/usr/bin/env bash
    set -euo pipefail
    cd mod/native/darwin-universal
    echo "Smoke-testing DgHudNative.bundle..."
    clang -o /tmp/dghudnative-smoke test/smoke.c
    /tmp/dghudnative-smoke ./out/DgHudNative.bundle/Contents/MacOS/DgHudNative

# --- Release packaging ---

# Generate combined third-party license notices (.NET + Rust deps)
notices:
    #!/usr/bin/env bash
    set -euo pipefail
    mkdir -p release
    cargo about generate about.hbs -o release/THIRD_PARTY_NOTICES_RUST 2>/dev/null
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
    rm -f "{{justfile_directory()}}/release/Dragonglass_Telemetry.zip"
    cd "$stage"
    zip -qr "{{justfile_directory()}}/release/Dragonglass_Telemetry.zip" GameData/
    rm -rf "$stage"
    echo "Built → release/Dragonglass_Telemetry.zip"

# Hud mod: Core plugin + Stock flight UI (platform-independent)
dist-hud: ui-build (mod-build "Release") notices
    #!/usr/bin/env bash
    set -euo pipefail
    stage=$(mktemp -d)
    root="$stage/GameData/Dragonglass_Hud"

    # Core plugin (no vendored runtime deps — protobuf removed in v3)
    mkdir -p "$root/Plugins"
    cp mod/Dragonglass.Hud/build/Dragonglass.Hud.dll       "$root/Plugins/"

    # Stock flight UI
    mkdir -p "$root/UI/Stock"
    cp -R ui/apps/stock/dist/* "$root/UI/Stock/"

    cp LICENSE "$root/"
    cp release/THIRD_PARTY_NOTICES "$root/"

    mkdir -p release
    rm -f "{{justfile_directory()}}/release/Dragonglass_Hud.zip"
    cd "$stage"
    zip -qr "{{justfile_directory()}}/release/Dragonglass_Hud.zip" GameData/
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

# Build all release zips
dist: dist-telemetry dist-hud dist-hud-darwin-arm64
    #!/usr/bin/env bash
    echo "Release artifacts:"
    ls -lh release/*.zip

# --- Install into KSP ---

# Build and install into a KSP directory. Detects platform automatically.
#   just install ~/KSP_osx
install ksp_path: dist
    #!/usr/bin/env bash
    set -euo pipefail
    ksp="{{ksp_path}}"

    if [ ! -d "$ksp" ]; then
        echo "error: KSP directory not found: $ksp" >&2
        exit 1
    fi

    # Clean previous install
    rm -rf "$ksp/GameData/Dragonglass_Telemetry"
    rm -rf "$ksp/GameData/Dragonglass_Hud"

    # Unpack universal packages
    for zip in \
        release/Dragonglass_Telemetry.zip \
        release/Dragonglass_Hud.zip \
    ; do
        unzip -qo "$zip" -d "$ksp"
    done

    # Platform-specific sidecar + native plugin
    os="$(uname -s)"
    arch="$(uname -m)"
    case "${os}_${arch}" in
        Darwin_arm64)  zip="release/Dragonglass_Hud_darwin_arm64.zip" ;;
        *)             echo "error: no sidecar package for ${os}_${arch}" >&2; exit 1 ;;
    esac

    if [ -f "$zip" ]; then
        # Use ditto to preserve symlinks and resource forks from the zip
        ditto -x -k "$zip" "$ksp"
    else
        echo "error: $zip not found — run 'just dist' first" >&2
        exit 1
    fi

    echo "Installed → $ksp/GameData/Dragonglass_{Telemetry,Hud}"

# --- All ---

build: ui-build sidecar-bundle mod-build native-build-darwin

check: ui-typecheck sidecar-check
    cd mod && dotnet build --no-restore
