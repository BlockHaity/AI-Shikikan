#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR"
OUTPUT_DIR="$PROJECT_DIR/artifacts"
CONFIGURATION="${CONFIGURATION:-Release}"
VERSION="${VERSION:-$(cat "$PROJECT_DIR/VERSION" 2>/dev/null || echo 0.9.0)}"
AOT_MODE="${AOT_MODE:-auto}"
ARCH="${ARCH:-both}"

GUI_PROJECT="$PROJECT_DIR/AIShikikan.Gui.csproj"

detect_host_rid() {
    local arch="x64"
    case "$(uname -m)" in
        x86_64|amd64) arch="x64" ;;
        aarch64|arm64) arch="arm64" ;;
    esac
    case "$(uname -s)" in
        Darwin) echo "osx-$arch" ;;
        MINGW*|MSYS*|CYGWIN*) echo "win-$arch" ;;
        *) echo "linux-$arch" ;;
    esac
}
HOST_RID="$(detect_host_rid)"

platform_base() {
    case "$1" in
        linux) echo "linux" ;;
        macos) echo "osx" ;;
        windows) echo "win" ;;
        *) echo "" ;;
    esac
}

archs_for() {
    case "$ARCH" in
        x64) echo "x64" ;;
        arm64) echo "arm64" ;;
        *) echo "x64 arm64" ;;
    esac
}

rids_for() {
    local platform="$1" base arch
    base="$(platform_base "$platform")"
    [ -z "$base" ] && return 1
    for arch in $(archs_for); do
        echo "$base-$arch"
    done
}

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

info()  { echo -e "${CYAN}[INFO]${NC} $*"; }
ok()    { echo -e "${GREEN}[OK]${NC} $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()   { echo -e "${RED}[ERROR]${NC} $*" >&2; }

want_aot() {
    local rid="$1"
    case "$AOT_MODE" in
        off) return 1 ;;
        always) return 0 ;;
        *) [ "$rid" = "$HOST_RID" ] ;;
    esac
}

build_gui() {
    local rid="$1" outdir="$2"

    if want_aot "$rid"; then
        info "Building GUI for $rid with Native AOT..."
        dotnet publish "$GUI_PROJECT" \
            -c "$CONFIGURATION" \
            -r "$rid" \
            -o "$outdir" \
            --self-contained true \
            -p:PublishAot=true \
            -p:StripSymbols=true \
            -p:PublishSingleFile=true \
            -p:Version="$VERSION"
    else
        warn "AOT_MODE=$AOT_MODE: host=$HOST_RID, target=$rid; 无法交叉 AOT, 回退到单文件裁剪发布"
        info "Building GUI for $rid (single-file/trimmed)..."
        dotnet publish "$GUI_PROJECT" \
            -c "$CONFIGURATION" \
            -r "$rid" \
            -o "$outdir" \
            --self-contained true \
            -p:PublishAot=false \
            -p:PublishSingleFile=true \
            -p:PublishTrimmed=true \
            -p:TrimMode=partial \
            -p:Version="$VERSION" \
            -p:IncludeNativeLibrariesForSelfExtract=true
    fi

    ok "GUI built: $outdir"
}

build_rid() {
    local platform="$1" rid="$2"
    local outdir="$OUTPUT_DIR/$rid"

    info "=== $rid ==="
    build_gui "$rid" "$outdir"
    ok "$rid bundled: $outdir"
}

pack_rid() {
    local rid="$1"
    local dir="$OUTPUT_DIR/$rid"

    [ -d "$dir" ] || return 0

    chmod +x "$dir/AIShikikan.Gui" 2>/dev/null || true

    case "$rid" in
        win-*)
            cd "$OUTPUT_DIR"
            zip -r "AIShikikan-$VERSION-$rid.zip" "$rid/" -x '*.pdb' -x '*.dbg' > /dev/null 2>&1 || \
            7z a "AIShikikan-$VERSION-$rid.zip" "./$rid/*" "-x!*.pdb" "-x!*.dbg" > /dev/null 2>&1 || \
            warn "No zip tool found, skipping compression for $rid"
            cd "$PROJECT_DIR"
            ;;
        *)
            cd "$OUTPUT_DIR"
            tar czf "AIShikikan-$VERSION-$rid.tar.gz" -C "$rid" --exclude='*.pdb' --exclude='*.dbg' .
            cd "$PROJECT_DIR"
            ;;
    esac

    ok "Packed $rid"
}

show_usage() {
    cat <<EOF
Usage: $(basename "$0") [command] [options]

Commands:
  linux       Build for Linux
  macos       Build for macOS
  windows     Build for Windows
  all         Build for all platforms
  clean       Clean build artifacts

Options:
  CONFIGURATION=Release   Build configuration (default: Release)
  VERSION=0.9.0           Version string (default: 0.9.0)
  ARCH=both               Instruction set architecture: x64 | arm64 | both (default: both)
  AOT_MODE=auto           Native AOT strategy: auto | always | off
                          auto   - AOT only when target RID equals host RID, else fallback
                          always - force AOT for all targets (requires cross toolchain)
                          off    - single-file/trimmed publish (default: auto)

Examples:
  $(basename "$0") linux
  ARCH=arm64 $(basename "$0") linux
  ARCH=x64 $(basename "$0") all
  VERSION=2.0.0 $(basename "$0") all
  ARCH=x64 AOT_MODE=off CONFIGURATION=Debug $(basename "$0") windows
EOF
}

clean() {
    info "Cleaning build artifacts..."
    rm -rf "$OUTPUT_DIR"
    dotnet clean "$PROJECT_DIR/AIShikikan.slnx" -c "$CONFIGURATION" > /dev/null 2>&1 || true
    ok "Cleaned"
}

main() {
    local command="${1:-all}"

    echo ""
    echo "  ╔══════════════════════════════════════╗"
    echo "  ║     AI-Shikikan Build System      ║"
    echo "  ╚══════════════════════════════════════╝"
    echo ""
    info "Configuration: $CONFIGURATION"
    info "Version:       $VERSION"
    info "ARCH:          $ARCH"
    info "AOT_MODE:      $AOT_MODE (host RID: $HOST_RID)"
    info "Output:        $OUTPUT_DIR"
    echo ""

    case "$command" in
        linux|macos|windows)
            for rid in $(rids_for "$command"); do
                build_rid "$command" "$rid"
            done
            for rid in $(rids_for "$command"); do
                pack_rid "$rid"
            done
            ;;
        all)
            for platform in linux macos windows; do
                for rid in $(rids_for "$platform"); do
                    build_rid "$platform" "$rid"
                done
            done
            for platform in linux macos windows; do
                for rid in $(rids_for "$platform"); do
                    pack_rid "$rid"
                done
            done
            ;;
        clean)
            clean
            ;;
        -h|--help|help)
            show_usage
            ;;
        *)
            err "Unknown command: $command"
            show_usage
            exit 1
            ;;
    esac

    echo ""
    ok "Build complete!"
    if [ -d "$OUTPUT_DIR" ]; then
        info "Artifacts:"
        find "$OUTPUT_DIR" -maxdepth 1 -type f \( -name "*.tar.gz" -o -name "*.zip" \) -exec ls -lh {} \; 2>/dev/null | while read -r line; do
            echo "  $line"
        done
    fi
}

main "$@"
