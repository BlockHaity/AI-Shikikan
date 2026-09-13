#!/usr/bin/env bash
set -euo pipefail

# 仅构建「当前平台」的三种变体:
#   aot           - Native AOT 编译, 无需 .NET 运行时, 启动最快
#   selfcontained - 自带 .NET 运行时的单文件裁剪发布, 开箱即用
#   dotnet        - 框架依赖发布, 需要目标机已安装 .NET 运行时
# 不再做跨平台/跨架构构建 (交叉编译由 CI 各 runner 分别完成)。

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR"
OUTPUT_DIR="$PROJECT_DIR/artifacts"
CONFIGURATION="${CONFIGURATION:-Release}"
VERSION="${VERSION:-$(cat "$PROJECT_DIR/VERSION" 2>/dev/null || echo 0.9.0)}"

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

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

info()  { echo -e "${CYAN}[INFO]${NC} $*"; }
ok()    { echo -e "${GREEN}[OK]${NC} $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()   { echo -e "${RED}[ERROR]${NC} $*" >&2; }

# $1=变体名 (aot|selfcontained|dotnet)
build_variant() {
    local variant="$1"
    local outdir="$OUTPUT_DIR/$variant"

    info "=== Building $variant for $HOST_RID ==="
    case "$variant" in
        aot)
            dotnet publish "$GUI_PROJECT" \
                -c "$CONFIGURATION" -r "$HOST_RID" -o "$outdir" \
                --self-contained true \
                -p:PublishAot=true -p:PublishSingleFile=true -p:StripSymbols=true \
                -p:Version="$VERSION"
            ;;
        selfcontained)
            dotnet publish "$GUI_PROJECT" \
                -c "$CONFIGURATION" -r "$HOST_RID" -o "$outdir" \
                --self-contained true \
                -p:PublishAot=false -p:PublishSingleFile=true \
                -p:PublishTrimmed=true -p:TrimMode=partial \
                -p:IncludeNativeLibrariesForSelfExtract=true \
                -p:Version="$VERSION"
            ;;
        dotnet)
            dotnet publish "$GUI_PROJECT" \
                -c "$CONFIGURATION" -r "$HOST_RID" -o "$outdir" \
                --self-contained false \
                -p:PublishAot=false \
                -p:Version="$VERSION"
            ;;
        *)
            err "Unknown variant: $variant"; exit 1 ;;
    esac
    ok "$variant built: $outdir"
}

pack_variant() {
    local variant="$1"
    local dir="$OUTPUT_DIR/$variant"
    [ -d "$dir" ] || return 0

    if [ "$HOST_RID" != "${HOST_RID#win-}" ]; then
        local archive="AIShikikan-$VERSION-$HOST_RID-$variant.zip"
        ( cd "$OUTPUT_DIR" && zip -qr "$archive" "$variant/" -x '*.pdb' -x '*.dbg' ) \
            || warn "No zip tool found, skipping compression for $variant"
    else
        chmod +x "$dir/AIShikikan.Gui" 2>/dev/null || true
        ( cd "$OUTPUT_DIR" && tar czf "AIShikikan-$VERSION-$HOST_RID-$variant.tar.gz" \
            --exclude='*.pdb' --exclude='*.dbg' -C "$variant" . )
    fi
    ok "Packed $variant"
}

show_usage() {
    cat <<EOF
Usage: $(basename "$0") [command]

Builds the current platform ($HOST_RID) only, in three variants:
  aot            Native AOT (no runtime needed, fastest startup)
  selfcontained  Bundled .NET runtime, single-file (trimmed)
  dotnet         Framework-dependent (requires .NET runtime installed)

Commands:
  (none)         Build all three variants and package them
  aot            Build only the AOT variant
  selfcontained  Build only the self-contained variant
  dotnet         Build only the framework-dependent variant
  clean          Clean build artifacts

Options (env):
  CONFIGURATION=Release   Build configuration (default: Release)
  VERSION=0.9.0           Version string (default: from VERSION file)

Examples:
  $(basename "$0")
  $(basename "$0") aot
  CONFIGURATION=Debug $(basename "$0") selfcontained
EOF
}

clean() {
    info "Cleaning build artifacts..."
    rm -rf "$OUTPUT_DIR"
    dotnet clean "$PROJECT_DIR/AIShikikan.slnx" -c "$CONFIGURATION" >/dev/null 2>&1 || true
    ok "Cleaned"
}

list_artifacts() {
    [ -d "$OUTPUT_DIR" ] || return 0
    local found=0
    info "Artifacts:"
    while IFS= read -r -d '' f; do
        echo "  $(basename "$f") ($(du -h "$f" | cut -f1))"
        found=1
    done < <(find "$OUTPUT_DIR" -maxdepth 1 -type f \
        \( -name "*.tar.gz" -o -name "*.zip" \) -print0)
    [ "$found" -eq 0 ] && warn "(no archives found)"
}

main() {
    local command="${1:-all}"

    echo ""
    echo "  AI-Shikikan Build System"
    echo ""
    info "Configuration: $CONFIGURATION"
    info "Version:       $VERSION"
    info "Host RID:      $HOST_RID"
    info "Output:        $OUTPUT_DIR"
    echo ""

    local variants=()
    case "$command" in
        all)   variants=(aot selfcontained dotnet) ;;
        aot|selfcontained|dotnet) variants=("$command") ;;
        clean) clean; exit 0 ;;
        -h|--help|help) show_usage; exit 0 ;;
        *) err "Unknown command: $command"; show_usage; exit 1 ;;
    esac

    for v in "${variants[@]}"; do
        build_variant "$v"
    done
    for v in "${variants[@]}"; do
        pack_variant "$v"
    done

    echo ""
    ok "Build complete!"
    list_artifacts
}

main "$@"
