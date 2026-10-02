#!/usr/bin/env bash
#
# debug.sh —— 以「能跑起来」为唯一目的的本地调试入口。
#
# 以前这里跑的是完整的 dotnet publish，每次改一行代码都要重新打包一遍
# (自包含 + 单文件 + 裁剪，几十秒起步)，调试成本被构建吃掉了。
# 现在改用 `dotnet run`：框架依赖(Debug JIT)直接启动 bin/ 下的中间产物，
# 增量编译完就 exec 程序本身，中间没有 publish 这一步。
#
# 代价是 debug 不再产出任何二进制：Native AOT 链接、自包含、单文件打包
# 这些只能在发布链路里验证，需要时请用 ./build.sh aot（或 ./build.sh all）。
#
# 工作目录刻意保持为调用者 shell 的当前目录 —— 不 cd 到项目目录。
# AppShell.Boot 用 Directory.GetCurrentDirectory() 判定 git 工作区根，
# 一旦 cd 到 src/AIShikikan.Gui/，doctor 会把项目子目录当成工作区、
# 聊天页的检查点也会建在错误的位置。用 `dotnet run --project <路径>`
# 就能指定项目而保持 cwd 不变，与旧的 exec 产物方式行为一致。
#
# Worker（工具执行子进程 AIShikikan.Worker）用框架依赖构建一次，再把绝对路径
# 注入 AISHIKIKAN_WORKER_PATH，让 WorkerLocator 的候选 1 确定命中：
# `dotnet run --project <GUI>` 只构建 GUI 的依赖图（ProjectReference 链），
# 独立项目 Worker 不在其间；而候选 4 要靠上溯 AIShikikan.slnx 再猜
# bin/<配置>/<tfm> 路径，注入环境变量是确定性的，且它的语义是「显式指定」——
# 路径写错会立刻报错，而不是悄悄降级成进程内执行。
#
# 版本号不做传参：dotnet run 没有 -p:Version 选项（-p 是 --project 的缩写），
# 根 Directory.Build.props 已从 VERSION 文件注入，这里再传只会是伪能力。
#
# 用法:
#   ./debug.sh                       编译并启动 GUI
#   ./debug.sh doctor                其余参数原样透传给程序
#   ./debug.sh --no-build doctor     跳过编译，直接跑上一次编译的产物
#   NO_BUILD=1 ./debug.sh --version  --no-build 的环境变量写法
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR"
CONFIGURATION="${CONFIGURATION:-Debug}"
NO_BUILD="${NO_BUILD:-0}"

# 可执行项目在 src/ 下；库项目 AIShikikan.Core 由项目引用隐式带上，无需单独指定
GUI_PROJECT="$PROJECT_DIR/src/AIShikikan.Gui/AIShikikan.Gui.csproj"
WORKER_PROJECT="$PROJECT_DIR/src/AIShikikan.Worker/AIShikikan.Worker.csproj"

# Worker 用框架依赖构建（开发期不 publish、不产出 AOT），产物落在这个目录。
# 刻意不传 -r/--runtime：只有指定了 RID 时 MSBuild 才会多下沉一层目录，
# 不传才是 <config>/<tfm>/ 这种两段式布局，也与 WorkerLocator 候选 4 的
# 第一优先探测路径一致。TFM 与根 Directory.Build.props 的 TargetFramework 对应。
WORKER_TFM="net10.0"
WORKER_OUTPUT_DIR="$PROJECT_DIR/src/AIShikikan.Worker/bin/$CONFIGURATION/$WORKER_TFM"
# restore 产物：决定这次能否用 --no-restore（见 build_worker 的注释）
WORKER_ASSETS="$PROJECT_DIR/src/AIShikikan.Worker/obj/project.assets.json"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

info()  { echo -e "${CYAN}[INFO]${NC} $*"; }
ok()    { echo -e "${GREEN}[OK]${NC} $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()   { echo -e "${RED}[ERROR]${NC} $*" >&2; }

# Worker 可执行文件名: Windows 是 AIShikikan.Worker.exe, 其他平台无扩展名。
# 与 WorkerLocator.GetExecutableName() 是同一条规则; 两处各拼一份迟早漂移成
# 「定位用无扩展名、启动用 .exe」这种只在单个平台上出现的偏差。
detect_worker_exe_name() {
    case "$(uname -s)" in
        MINGW*|MSYS*|CYGWIN*) echo "AIShikikan.Worker.exe" ;;
        *) echo "AIShikikan.Worker" ;;
    esac
}

# 构建 Worker 并把 AISHIKIKAN_WORKER_PATH 指向它。任何一步失败都只告警不中断:
# WorkerLocator 未命中时会降级为进程内执行 (InlineTransport), 功能不受影响 ——
# 为一个辅助进程把 GUI 卡在启动入口才是更糟的失败模式。
build_worker() {
    if [ ! -f "$WORKER_PROJECT" ]; then
        warn "找不到 Worker 项目: $WORKER_PROJECT, 跳过构建 (工具将退回进程内执行)"
        return 0
    fi

    info "Building AIShikikan.Worker ($CONFIGURATION, framework-dependent)..."

    # --no-restore: GUI 的 dotnet run 已经 restore 过依赖图, 再 restore 一次纯属浪费。
    # 唯一的例外是冷启动 —— dotnet run 恢复的是 GUI 的项目图 (含 ProjectReference 链),
    # 独立项目 Worker 不在其中; 此时 assets 文件还不存在, 仍带 --no-restore
    # 会直接 NETSDK1004 失败。所以先看 assets 在不在, 而不是等失败再补救。
    # 数组恒非空 (至少含项目路径): bash 3.2 (macOS 自带) 在 set -u 下展开空数组会报错。
    local build_args=("$WORKER_PROJECT" -c "$CONFIGURATION")
    if [ -f "$WORKER_ASSETS" ]; then
        build_args+=(--no-restore)
    else
        info "Worker 尚未 restore 过, 本次让其自行 restore"
    fi

    if ! dotnet build "${build_args[@]}"; then
        warn "Worker 编译失败, 不影响 GUI 启动 (工具将退回进程内执行)"
        return 0
    fi

    local worker_exe="$WORKER_OUTPUT_DIR/$(detect_worker_exe_name)"
    if [ -f "$worker_exe" ]; then
        # 必须是绝对路径: 相对路径会被 WorkerLocator 按 GUI 进程的当前工作目录解析,
        # 而本脚本刻意保持调用者的 cwd, 那个目录通常不是仓库根, 解析结果必然指错。
        export AISHIKIKAN_WORKER_PATH="$worker_exe"
        ok "Worker: $AISHIKIKAN_WORKER_PATH"
    else
        # 不导出一个不存在的路径: 候选 1 是「显式指定」语义, 配错即报错,
        # 那会让「脚本算错了路径」变成一个比降级更难查的问题。
        warn "未找到 Worker 产物 ($worker_exe); 不注入 AISHIKIKAN_WORKER_PATH, 工具将退回进程内执行"
    fi
}

show_usage() {
    cat <<EOF
Usage: $(basename "$0") [--no-build] [app arguments...]

以 dotnet run 启动 Debug 版 GUI（框架依赖运行，不 publish、不产出 AOT 二进制）。
启动前会用框架依赖构建一次 AIShikikan.Worker, 并把绝对路径注入
AISHIKIKAN_WORKER_PATH; Worker 缺失或编译失败都不影响 GUI 启动
(工具退回进程内执行)。

Arguments:
  --no-build   跳过编译, 直接运行上一次编译的产物（等价于 NO_BUILD=1）
  其余参数      原样传递给被启动的程序

Examples:
  $(basename "$0")
  $(basename "$0") doctor
  $(basename "$0") --version
  $(basename "$0") --no-build doctor
  NO_BUILD=1 $(basename "$0") doctor

Options (env):
  CONFIGURATION=Debug   构建配置 (default: Debug)
  NO_BUILD=1            同 --no-build, 跳过编译直接跑

Note:
  AOT / 自包含 / 单文件产物请用 ./build.sh aot 验证, debug.sh 只负责跑起来。
EOF
}

main() {
    # 帮助优先于一切：help / --help / -h 只认第一个参数，其余按程序参数处理
    case "${1:-}" in
        -h|--help|help)
            show_usage
            exit 0
            ;;
    esac

    # 摘掉脚本自身的开关，剩下的原样交给程序。
    # 裸 -- 之前都可以识别 --no-build；一旦遇到 -- ，其后一律视为程序参数。
    APP_ARGS=()
    while [ "$#" -gt 0 ]; do
        case "$1" in
            --)
                shift
                APP_ARGS+=("$@")
                break
                ;;
            --no-build)
                NO_BUILD=1
                shift
                ;;
            *)
                APP_ARGS+=("$1")
                shift
                ;;
        esac
    done

    if [ ! -f "$GUI_PROJECT" ]; then
        err "找不到可执行项目: $GUI_PROJECT"
        exit 1
    fi

    echo ""
    info "Project:      $GUI_PROJECT"
    info "Configuration: $CONFIGURATION"
    if [ "$NO_BUILD" != "0" ]; then
        # 跳过编译意味着可能跑的是上一轮代码，这里明确提醒而不是默默启动
        warn "NO_BUILD 生效: 跳过编译, 直接运行已存在的产物"
        # Worker 同样不编译; 上一轮产物仍在 src/AIShikikan.Worker/bin/ 下,
        # WorkerLocator 的候选 4 (上溯 AIShikikan.slnx 再拼 bin 路径) 照样能命中,
        # 所以不注入 AISHIKIKAN_WORKER_PATH 也不会丢功能
    else
        # 必须在 exec dotnet run 之前: exec 会让 dotnet run 直接接管本进程, 之后没有插入余地
        build_worker
    fi
    echo ""

    RUN_ARGS=(--project "$GUI_PROJECT" -c "$CONFIGURATION")
    if [ "$NO_BUILD" != "0" ]; then
        RUN_ARGS+=(--no-build)
    fi
    # 只有真的还有程序参数时才追加分隔用的 --，避免留下空的尾随 --（部分场景会被当成程序参数）
    if [ "${#APP_ARGS[@]}" -gt 0 ]; then
        RUN_ARGS+=(-- "${APP_ARGS[@]}")
    fi

    ok "Launching via dotnet run..."
    # exec 让 dotnet run 直接接管本进程，退出码原样透传给调用方（doctor 等子命令靠它判成败）
    exec dotnet run "${RUN_ARGS[@]}"
}

main "$@"