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

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

info()  { echo -e "${CYAN}[INFO]${NC} $*"; }
ok()    { echo -e "${GREEN}[OK]${NC} $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()   { echo -e "${RED}[ERROR]${NC} $*" >&2; }

show_usage() {
    cat <<EOF
Usage: $(basename "$0") [--no-build] [app arguments...]

以 dotnet run 启动 Debug 版 GUI（框架依赖运行，不 publish、不产出 AOT 二进制）。

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