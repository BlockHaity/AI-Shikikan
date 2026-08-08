#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR"
OUTPUT_DIR="$PROJECT_DIR/artifacts/debug"
CONFIGURATION="${CONFIGURATION:-Debug}"
VERSION="${VERSION:-1.0.0}"
AOT_MODE="${AOT_MODE:-off}"

CLI_PROJECT="$PROJECT_DIR/src/AIShikikan.Cli/AIShikikan.Cli.csproj"
GUI_PROJECT="$PROJECT_DIR/src/AIShikikan.Gui/AIShikikan.Gui.csproj"

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

want_aot() {
    case "$AOT_MODE" in
        off) return 1 ;;
        always) return 0 ;;
        *) [ "$HOST_RID" = "$HOST_RID" ] ;;
    esac
}

publish_cli() {
    if want_aot; then
        info "Publishing CLI for $HOST_RID (AOT enabled)..."
        dotnet publish "$CLI_PROJECT" \
            -c "$CONFIGURATION" \
            -r "$HOST_RID" \
            -o "$OUTPUT_DIR" \
            --self-contained true \
            -p:PublishAot=true \
            -p:StripSymbols=true \
            -p:PublishSingleFile=true \
            -p:Version="$VERSION"
    else
        info "Publishing CLI for $HOST_RID (single-file/trimmed)..."
        dotnet publish "$CLI_PROJECT" \
            -c "$CONFIGURATION" \
            -r "$HOST_RID" \
            -o "$OUTPUT_DIR" \
            --self-contained true \
            -p:PublishAot=false \
            -p:PublishSingleFile=true \
            -p:PublishTrimmed=true \
            -p:TrimMode=partial \
            -p:Version="$VERSION" \
            -p:IncludeNativeLibrariesForSelfExtract=true
    fi
}

publish_gui() {
    info "Publishing GUI for $HOST_RID..."
    dotnet publish "$GUI_PROJECT" \
        -c "$CONFIGURATION" \
        -r "$HOST_RID" \
        -o "$OUTPUT_DIR" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:PublishTrimmed=false \
        -p:Version="$VERSION" \
        -p:IncludeNativeLibrariesForSelfExtract=true
}

show_usage() {
    cat <<EOF
Usage: $(basename "$0") <cli|gui> [app arguments...]

编译当前平台的 Debug 版本并启动程序。

Arguments:
  cli|gui   要启动的程序: cli (终端界面/REST API) 或 gui (Avalonia 图形界面)
  其余参数  原样传递给被启动的程序

Examples:
  $(basename "$0") cli
  $(basename "$0") cli --persona senior-architect
  $(basename "$0") cli api --port 8090
  $(basename "$0") gui

Options (env):
  CONFIGURATION=Debug   Build configuration (default: Debug)
  AOT_MODE=off          Native AOT strategy for CLI: auto | always | off (default: off)
  VERSION=1.0.0         Version string (default: 1.0.0)
EOF
}

main() {
    local app="${1:-}"
    [ -z "$app" ] && { show_usage; exit 1; }

    echo ""
    info "Configuration: $CONFIGURATION"
    info "Version:       $VERSION"
    info "AOT_MODE:      $AOT_MODE (host RID: $HOST_RID)"
    info "Output:        $OUTPUT_DIR"
    echo ""

    case "$app" in
        cli)
            publish_cli
            ok "CLI built, launching..."
            exec "$OUTPUT_DIR/AIShikikan.Cli" "${@:2}"
            ;;
        gui)
            publish_gui
            ok "GUI built, launching..."
            exec "$OUTPUT_DIR/AIShikikan.Gui" "${@:2}"
            ;;
        -h|--help|help)
            show_usage
            ;;
        *)
            err "Unknown app: $app"
            show_usage
            exit 1
            ;;
    esac
}

main "$@"