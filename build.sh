#!/usr/bin/env bash
set -euo pipefail

# 仅构建「当前平台」的三种变体:
#   aot           - Native AOT 编译, 无需 .NET 运行时, 启动最快
#   selfcontained - 自带 .NET 运行时的单文件裁剪发布, 开箱即用
#   dotnet        - 框架依赖发布, 需要目标机已安装 .NET 运行时
# 不再做跨平台/跨架构构建 (交叉编译由 CI 各 runner 分别完成)。
#
# 解决方案在仓库根 (AIShikikan.slnx), 可执行项目有两个:
# src/AIShikikan.Gui/ (主程序) 与 src/AIShikikan.Worker/ (工具执行子进程),
# 类库项目在 src/AIShikikan.Core/。产物名分别是 AIShikikan.Gui 与 AIShikikan.Worker。
#
# Worker 与 GUI 逐变体配对, 不是「只出一个 AOT 版 Worker」:
#   - 用户选 dotnet 变体的动机就是省磁盘, 塞给他一个 15MB 的 AOT Worker 等于用他
#     明确拒绝的方式解决问题;
#   - 反过来也不能假设「装了 .NET 运行时的用户存在」, 那正是 aot 变体存在的理由。
# 单一 Worker 变体必然在「用户所选变体」与「Worker 所需运行时」之间错配。
#
# 产物布局:
#   artifacts/<variant>/         GUI 变体
#   artifacts/worker/<variant>/  Worker 变体, 与同名 GUI 变体一一对应
# 归档 (zip / tar.gz) 里 Worker 位于 worker/ 子目录, 与 GUI 平级 ——
# 对应 WorkerLocator 的候选 3, 同时避开 AOT 原生库与框架依赖托管 DLL 的同名覆盖。

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR"
OUTPUT_DIR="$PROJECT_DIR/artifacts"
CONFIGURATION="${CONFIGURATION:-Release}"
VERSION="${VERSION:-$(cat "$PROJECT_DIR/VERSION" 2>/dev/null || echo 1.0.0-vibe)}"

GUI_PROJECT="$PROJECT_DIR/src/AIShikikan.Gui/AIShikikan.Gui.csproj"
WORKER_PROJECT="$PROJECT_DIR/src/AIShikikan.Worker/AIShikikan.Worker.csproj"

# Worker 产物不塞进 artifacts/<variant>/, 而是另起一级:
# 产物目录是 pack_variant 的输入, 而 dotnet publish 不会清空输出目录 ——
# 把 Worker 拷进去, 下一次构建的归档就会带上「上一次的陈旧 Worker」,
# 而归档文件名恒定不变, 陈旧内容会原样进到用户下载到的包里。
WORKER_OUTPUT_ROOT="$OUTPUT_DIR/worker"
# 归档 / Linux 系统包里的 Worker 子目录名, 与 WorkerLocator.WorkerSubdirectoryName 保持一致
WORKER_PACKAGE_SUBDIR="worker"

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

# 打包用的临时 staging 根目录。放在全局而不是 pack_variant 的局部, 是为了能被
# EXIT trap 兜住: cp / zip 中途失败时脚本会被 set -e 直接带走, 只有 trap 能保证
# 临时目录不残留在磁盘上。
STAGING_ROOT=""
cleanup_staging() {
    # 用 if 而不是 `[ -n ... ] && rm`, 否则 STAGING_ROOT 为空时函数返回 1,
    # 在 set -e 下会把「清理」本身变成一次失败退出
    if [ -n "$STAGING_ROOT" ]; then rm -rf "$STAGING_ROOT"; fi
    STAGING_ROOT=""
}
trap cleanup_staging EXIT

# $1=变体名 $2=项目文件 $3=输出目录
# GUI 与 Worker 走同一个函数: 变体参数只在这一处写一遍。
# 两处各写一份必然漂移, 而漂移一次就会产出「GUI 是 AOT、Worker 是框架依赖」的包 ——
# 装 AOT 包的用户会因为 Worker 缺 .NET 运行时而静默退化成进程内执行, 界面上没有任何报错。
publish_variant() {
    local variant="$1" project="$2" outdir="$3"

    case "$variant" in
        aot)
            dotnet publish "$project" \
                -c "$CONFIGURATION" -r "$HOST_RID" -o "$outdir" \
                --self-contained true \
                -p:PublishAot=true -p:PublishSingleFile=true -p:StripSymbols=true \
                -p:Version="$VERSION"
            ;;
        selfcontained)
            dotnet publish "$project" \
                -c "$CONFIGURATION" -r "$HOST_RID" -o "$outdir" \
                --self-contained true \
                -p:PublishAot=false -p:PublishSingleFile=true \
                -p:PublishTrimmed=true -p:TrimMode=partial \
                -p:IncludeNativeLibrariesForSelfExtract=true \
                -p:Version="$VERSION"
            ;;
        dotnet)
            dotnet publish "$project" \
                -c "$CONFIGURATION" -r "$HOST_RID" -o "$outdir" \
                --self-contained false \
                -p:PublishAot=false \
                -p:Version="$VERSION"
            ;;
        *)
            err "Unknown variant: $variant"; exit 1 ;;
    esac
}

# $1=变体名 (aot|selfcontained|dotnet)
build_variant() {
    local variant="$1"
    local outdir="$OUTPUT_DIR/$variant"
    local worker_outdir="$WORKER_OUTPUT_ROOT/$variant"

    info "=== Building $variant for $HOST_RID ==="
    publish_variant "$variant" "$GUI_PROJECT" "$outdir"
    ok "$variant built: $outdir"

    # Worker 与 GUI 同配置、同 RID、同版本、同发布参数, 只是换个输出目录
    if [ -f "$WORKER_PROJECT" ]; then
        info "--- Building worker for $variant ($HOST_RID) ---"
        publish_variant "$variant" "$WORKER_PROJECT" "$worker_outdir"
        ok "worker/$variant built: $worker_outdir"
    else
        # Worker 项目尚未落地时不让 GUI 的构建被拖死: WorkerLocator 会降级成进程内
        # 执行, 功能不受影响。真要发布时 pack_variant 会再告警一次, 不会静默漏掉。
        warn "找不到 Worker 项目: $WORKER_PROJECT (跳过 $variant 的 Worker 构建)"
    fi
}

pack_variant() {
    local variant="$1"
    local dir="$OUTPUT_DIR/$variant"
    local worker_dir="$WORKER_OUTPUT_ROOT/$variant"
    [ -d "$dir" ] || return 0

    # 归档要求 GUI 与 worker/ 平级, 但产物目录不能被污染(见 WORKER_OUTPUT_ROOT 的注释),
    # 因此整份内容先拷进临时目录再打包。staging 里仍用变体名做子目录, 使 zip 的顶层
    # 目录保持 <variant>/, 与改动前的归档结构一致。
    STAGING_ROOT="$(mktemp -d)"
    local stage_dir="$STAGING_ROOT/$variant"
    mkdir -p "$stage_dir"
    cp -a "$dir/." "$stage_dir/"

    if [ -d "$worker_dir" ]; then
        # GUI 产物目录里本来就带 worker/ 说明它是别处拷进来的(旧布局残留等):
        # cp -a 到一个已存在的目录会拷成 worker/<变体名> 嵌套一层, 归档看起来完全正常,
        # 但 GUI 的候选 3 找不到可执行文件 —— 先清掉, 让 Worker 产物目录说了算
        if [ -e "$stage_dir/$WORKER_PACKAGE_SUBDIR" ]; then
            warn "GUI 产物目录里已有 $WORKER_PACKAGE_SUBDIR/ 子目录, 已丢弃并以 Worker 产物目录为准"
            rm -rf "$stage_dir/$WORKER_PACKAGE_SUBDIR"
        fi
        cp -a "$worker_dir" "$stage_dir/$WORKER_PACKAGE_SUBDIR"
    else
        # 不 fail: 打包不一定紧跟构建(只想重打 GUI 的包是合法的), 但必须出声 ——
        # 缺 Worker 的包装出去, 用户拿到的就是「工具静默退回进程内执行」, 无从察觉。
        warn "变体 $variant 缺少 Worker 产物 ($worker_dir); 归档里将没有 $WORKER_PACKAGE_SUBDIR/ 子目录, 工具会退回进程内执行"
    fi

    # 补执行位: 归档在 scp / 解压到另一台机器后不保留权限位
    local exe
    for exe in "$stage_dir/AIShikikan.Gui" "$stage_dir/$WORKER_PACKAGE_SUBDIR/AIShikikan.Worker"; do
        [ -f "$exe" ] && chmod +x "$exe" 2>/dev/null || true
    done

    if [ "$HOST_RID" != "${HOST_RID#win-}" ]; then
        local archive="$OUTPUT_DIR/AIShikikan-$VERSION-$HOST_RID-$variant.zip"
        ( cd "$STAGING_ROOT" && zip -qr "$archive" "$variant/" -x '*.pdb' -x '*.dbg' ) \
            || warn "No zip tool found, skipping compression for $variant"
    else
        # 产物目录里的 GUI 也要保持可执行: README 让用户直接 ./artifacts/<variant>/AIShikikan.Gui
        chmod +x "$dir/AIShikikan.Gui" 2>/dev/null || true
        tar czf "$OUTPUT_DIR/AIShikikan-$VERSION-$HOST_RID-$variant.tar.gz" \
            --exclude='*.pdb' --exclude='*.dbg' -C "$stage_dir" .
    fi

    # 打包完立刻清掉, 不留到脚本退出
    cleanup_staging
    ok "Packed $variant"
}

show_usage() {
    cat <<EOF
Usage: $(basename "$0") [command]

Builds the current platform ($HOST_RID) only, in three variants:
  aot            Native AOT (no runtime needed, fastest startup)
  selfcontained  Bundled .NET runtime, single-file (trimmed)
  dotnet         Framework-dependent (requires .NET runtime installed)

Each variant also builds AIShikikan.Worker with identical publish settings.
Artifact layout:
  artifacts/<variant>/          GUI
  artifacts/worker/<variant>/   Worker, paired with the same variant
  archives ship the worker under worker/ (next to the GUI)

Commands:
  (none)         Build all three variants and package them
  aot            Build only the AOT variant
  selfcontained  Build only the self-contained variant
  dotnet         Build only the framework-dependent variant
  clean          Clean build artifacts

Options (env):
  CONFIGURATION=Release   Build configuration (default: Release)
  VERSION=1.0.0-vibe           Version string (default: from VERSION file)

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
