#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR"
OUTPUT_DIR="$PROJECT_DIR/artifacts"
CONFIGURATION="${CONFIGURATION:-Release}"
VERSION="${VERSION:-1.0.0}"
AOT_MODE="${AOT_MODE:-auto}"

CLI_PROJECT="$PROJECT_DIR/src/AgentCommander.Cli/AgentCommander.Cli.csproj"
GUI_PROJECT="$PROJECT_DIR/src/AgentCommander.Gui/AgentCommander.Gui.csproj"

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

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

info()  { echo -e "${CYAN}[INFO]${NC} $*"; }
ok()    { echo -e "${GREEN}[OK]${NC} $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()   { echo -e "${RED}[ERROR]${NC} $*" >&2; }

build_cli() {
    local rid="$1"
    local outdir="$OUTPUT_DIR/cli/$rid"

    if want_aot "$rid"; then
        info "Building CLI for $rid with Native AOT..."
        dotnet publish "$CLI_PROJECT" \
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
        info "Building CLI for $rid (single-file/trimmed)..."
        dotnet publish "$CLI_PROJECT" \
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

    ok "CLI built: $outdir"
}

want_aot() {
    local rid="$1"
    case "$AOT_MODE" in
        off) return 1 ;;
        always) return 0 ;;
        *) [ "$rid" = "$HOST_RID" ] ;;
    esac
}

build_gui() {
    local rid="$1"
    local outdir="$OUTPUT_DIR/gui/$rid"

    info "Building GUI for $rid..."
    dotnet publish "$GUI_PROJECT" \
        -c "$CONFIGURATION" \
        -r "$rid" \
        -o "$outdir" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:PublishTrimmed=false \
        -p:Version="$VERSION" \
        -p:IncludeNativeLibrariesForSelfExtract=true

    ok "GUI built: $outdir"
}

build_platform() {
    local platform="$1"

    case "$platform" in
        linux)
            info "=== Building for Linux ==="
            build_cli "linux-x64"
            build_cli "linux-arm64"
            build_gui "linux-x64"
            build_gui "linux-arm64"
            ;;
        macos)
            info "=== Building for macOS ==="
            build_cli "osx-x64"
            build_cli "osx-arm64"
            build_gui "osx-x64"
            build_gui "osx-arm64"
            ;;
        windows)
            info "=== Building for Windows ==="
            build_cli "win-x64"
            build_cli "win-arm64"
            build_gui "win-x64"
            build_gui "win-arm64"
            ;;
        *)
            err "Unknown platform: $platform"
            return 1
            ;;
    esac
}

pack_platform() {
    local platform="$1"

    info "Packing $platform artifacts..."

    case "$platform" in
        linux)
            for rid in linux-x64 linux-arm64; do
                if [ -d "$OUTPUT_DIR/cli/$rid" ]; then
                    chmod +x "$OUTPUT_DIR/cli/$rid/AgentCommander.Cli" 2>/dev/null || true
                    cd "$OUTPUT_DIR/cli"
                    tar czf "AgentCommander.Cli-$VERSION-$rid.tar.gz" -C "$rid" .
                    cd "$PROJECT_DIR"
                fi
                if [ -d "$OUTPUT_DIR/gui/$rid" ]; then
                    chmod +x "$OUTPUT_DIR/gui/$rid/AgentCommander.Gui" 2>/dev/null || true
                    cd "$OUTPUT_DIR/gui"
                    tar czf "AgentCommander.Gui-$VERSION-$rid.tar.gz" -C "$rid" .
                    cd "$PROJECT_DIR"
                fi
            done
            ;;
        macos)
            for rid in osx-x64 osx-arm64; do
                if [ -d "$OUTPUT_DIR/cli/$rid" ]; then
                    chmod +x "$OUTPUT_DIR/cli/$rid/AgentCommander.Cli" 2>/dev/null || true
                    cd "$OUTPUT_DIR/cli"
                    tar czf "AgentCommander.Cli-$VERSION-$rid.tar.gz" -C "$rid" .
                    cd "$PROJECT_DIR"
                fi
                if [ -d "$OUTPUT_DIR/gui/$rid" ]; then
                    chmod +x "$OUTPUT_DIR/gui/$rid/AgentCommander.Gui" 2>/dev/null || true
                    cd "$OUTPUT_DIR/gui"
                    tar czf "AgentCommander.Gui-$VERSION-$rid.tar.gz" -C "$rid" .
                    cd "$PROJECT_DIR"
                fi
            done
            ;;
        windows)
            for rid in win-x64 win-arm64; do
                if [ -d "$OUTPUT_DIR/cli/$rid" ]; then
                    cd "$OUTPUT_DIR/cli"
                    zip -r "AgentCommander.Cli-$VERSION-$rid.zip" "$rid/" > /dev/null 2>&1 || \
                    7z a "AgentCommander.Cli-$VERSION-$rid.zip" "./$rid/*" > /dev/null 2>&1 || \
                    warn "No zip tool found, skipping compression for CLI $rid"
                    cd "$PROJECT_DIR"
                fi
                if [ -d "$OUTPUT_DIR/gui/$rid" ]; then
                    cd "$OUTPUT_DIR/gui"
                    zip -r "AgentCommander.Gui-$VERSION-$rid.zip" "$rid/" > /dev/null 2>&1 || \
                    7z a "AgentCommander.Gui-$VERSION-$rid.zip" "./$rid/*" > /dev/null 2>&1 || \
                    warn "No zip tool found, skipping compression for GUI $rid"
                    cd "$PROJECT_DIR"
                fi
            done
            ;;
    esac

    ok "Packed $platform artifacts"
}

show_usage() {
    cat <<EOF
Usage: $(basename "$0") [command] [options]

Commands:
  linux       Build for Linux (x64 + arm64)
  macos       Build for macOS (x64 + arm64)
  windows     Build for Windows (x64 + arm64)
  all         Build for all platforms
  clean       Clean build artifacts

Options:
  CONFIGURATION=Release   Build configuration (default: Release)
  VERSION=1.0.0           Version string (default: 1.0.0)
  AOT_MODE=auto           Native AOT strategy for CLI: auto | always | off
                          auto   - AOT only when target RID equals host RID, else fallback
                          always - force AOT for all targets (requires cross toolchain)
                          off    - single-file/trimmed publish (default: auto)

Examples:
  $(basename "$0") linux
  $(basename "$0") all
  $(basename "$0") all VERSION=2.0.0
  AOT_MODE=off CONFIGURATION=Debug $(basename "$0") windows
EOF
}

clean() {
    info "Cleaning build artifacts..."
    rm -rf "$OUTPUT_DIR"
    dotnet clean "$PROJECT_DIR/AgentCommander.slnx" -c "$CONFIGURATION" > /dev/null 2>&1 || true
    ok "Cleaned"
}

main() {
    local command="${1:-all}"

    echo ""
    echo "  ╔══════════════════════════════════════╗"
    echo "  ║     Agent Commander Build System      ║"
    echo "  ╚══════════════════════════════════════╝"
    echo ""
    info "Configuration: $CONFIGURATION"
    info "Version:       $VERSION"
    info "AOT_MODE:      $AOT_MODE (host RID: $HOST_RID)"
    info "Output:        $OUTPUT_DIR"
    echo ""

    case "$command" in
        linux|macos|windows)
            build_platform "$command"
            pack_platform "$command"
            ;;
        all)
            build_platform "linux"
            build_platform "macos"
            build_platform "windows"
            pack_platform "linux"
            pack_platform "macos"
            pack_platform "windows"
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
