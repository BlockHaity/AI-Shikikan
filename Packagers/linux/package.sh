#!/usr/bin/env bash
set -euo pipefail

# Linux 系统包打包 (deb / rpm / pacman)。
#
# 本脚本是 release.yml 与 debug.yml 中「Build Linux packages」步骤的唯一实现:
# 两个工作流原本各自内嵌了一段约 170 行、几乎逐字重复的 bash, 抽到这里后
# CI 与本地维护者跑的是同一份逻辑, 产物命名也保持不变 (下游脚本与用户已依赖)。
#
# 用法 (在仓库根目录执行):
#   VERSION=1.0.0-vibe RID=linux-x64 VARIANTS="dotnet selfcontained aot" \
#   HOMEPAGE="https://github.com/BlockHaity/AI-Shikikan" \
#   MAINTAINER="someone <a@b.c>" \
#   ./Packagers/linux/package.sh
#
# 装出来的应用树长这样 (Worker 是独立进程, 放 worker/ 子目录):
#   <root>/usr/lib/<PKG>/AIShikikan.Gui        GUI 可执行文件
#   <root>/usr/lib/<PKG>/worker/AIShikikan.Worker
#                                         Worker 可执行文件
#   <root>/usr/bin/<PKG> -> ../lib/<PKG>/AIShikikan.Gui
#
# 常见组合:
#   ⚠️ STAGE_DIR **必须**与 ./build.sh 的输出一致 —— build.sh 写 artifacts/, 而不是 stage/。
#      漏掉它会在前置校验处报错(而不是装出坏包), 但报错信息不如一开始就对。
#   VERSION=$(cat VERSION) VARIANTS=dotnet STAGE_DIR=artifacts \
#     FORMATS=pacman ./Packagers/linux/package.sh      # 复用 ./build.sh 产物
#   # CI 里 STAGE_DIR=stage, WORKER_STAGE_DIR 随之派生为 stage/worker, 无需显式传。
#
# 环境变量契约:
#   VERSION      必填。版本号, 如 1.0.0-vibe; 缺失或格式非法立即退出。
#   VARIANTS     空格分隔的变体名, 默认 "dotnet selfcontained"; 合法值 dotnet/selfcontained/aot。
#   FORMATS      空格分隔的包格式, 默认 "deb rpm pacman"; 合法值 deb/rpm/pacman。
#                默认全开 = CI 行为; 本地缺工具时可裁剪 (如 FORMATS=deb)。
#   RID          默认按 uname -m 探测; 只接受 linux-x64 / linux-arm64。
#   STAGE_DIR    GUI 发布产物根目录, 默认 stage (相对仓库根); 每个变体在 $STAGE_DIR/<variant>。
#   WORKER_STAGE_DIR
#                Worker 发布产物根目录, **默认 ${STAGE_DIR}/worker** (相对仓库根);
#                每个变体在 $WORKER_STAGE_DIR/<variant>。与 STAGE_DIR 分开是因为
#                Worker 与 GUI 是两个独立可执行项目, 由 build.sh / CI 分别 publish,
#                不共用同一个输出根 —— 见「Worker 产物闸门」一节。
#   OUT_DIR      系统包输出目录, 默认 dist (相对仓库根)。
#   PKG          包名, 默认 ai-shikikan。
#   HOMEPAGE     默认 https://github.com/BlockHaity/AI-Shikikan (缺失时告警)。
#   MAINTAINER   默认 BlockHaity <blockhaity@users.noreply.github.com> (缺失时告警)。
#
# 相对路径 (STAGE_DIR / WORKER_STAGE_DIR / OUT_DIR) 一律按「仓库根」解析, 因此在
# 任意 cwd 下调用结果都一致 —— CI 里 cwd 是仓库根, 本地从别处调用也不会跑到奇怪的
# 目录里。

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGERS_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ROOT="$(cd "$PACKAGERS_DIR/.." && pwd)"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

info() { echo -e "${CYAN}[INFO]${NC} $*"; }
ok()   { echo -e "${GREEN}[OK]${NC} $*"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()  { echo -e "${RED}[ERROR]${NC} $*" >&2; }

die() { err "$*"; exit 1; }

# 在 GitHub Actions 里额外发一条 annotation, 让失败步骤在 UI 上直接高亮原因。
gh_error() {
    if [ "${GITHUB_ACTIONS:-false}" = "true" ]; then
        echo "::error::$*"
    fi
    err "$*"
}

# ---------- 输入归一化 ----------

VERSION="${VERSION:-}"
VARIANTS="${VARIANTS:-dotnet selfcontained}"
FORMATS="${FORMATS:-deb rpm pacman}"
RID="${RID:-}"
STAGE_DIR="${STAGE_DIR:-stage}"
# ⚠️ Worker 产物根默认**从 STAGE_DIR 派生**, 不是硬编码 "stage/worker":
# 两个默认值必须联动, 否则 `STAGE_DIR=artifacts ./package.sh` 会去找 stage/worker 而扑空 ——
# 而 build.sh 产出的恰恰是 artifacts/<variant> 与 artifacts/worker/<variant> 这一对。
# CI 传 STAGE_DIR=stage 时派生结果仍是 stage/worker, 行为与改造前逐字一致。
WORKER_STAGE_DIR="${WORKER_STAGE_DIR:-${STAGE_DIR}/worker}"
OUT_DIR="${OUT_DIR:-dist}"
PKG="${PKG:-ai-shikikan}"
HOMEPAGE="${HOMEPAGE:-}"
MAINTAINER="${MAINTAINER:-}"

DEFAULT_HOMEPAGE='https://github.com/BlockHaity/AI-Shikikan'
DEFAULT_MAINTAINER='BlockHaity <blockhaity@users.noreply.github.com>'
DESC="AI-Shikikan is an Agent commander: it orchestrates terminal agents such as Claude Code, OpenCode, Codex CLI and Gemini CLI to complete complex tasks."

# VERSION 必填: 三种包格式的版本字段全部由它推导, 缺失时若继续跑就会产出
# 「版本为空」却看不出异常的坏包, 所以宁可立刻失败。
[ -n "$VERSION" ] || die "缺少 VERSION (例: VERSION=1.0.0-vibe ./Packagers/linux/package.sh)"

# 三种包格式都要求版本以数字开头 (Debian policy / rpm / pacman 一致), 且不允许
# 空格、斜杠等会在下游产生路径或字段歧义的字符。
[[ "$VERSION" =~ ^[0-9][0-9A-Za-z.+~_-]*$ ]] \
    || die "VERSION 格式非法: '$VERSION' (要求形如 1.0.0 / 1.0.0-vibe: 数字开头, 只含 [0-9A-Za-z.+~_-])"

case "$RID" in
    '') RID="$(uname -m | sed -e 's/^x86_64$/x64/' -e 's/^amd64$/x64/' -e 's/^aarch64$/arm64/' -e 's/^arm64$/arm64/')"
        RID="linux-$RID" ;;
esac

case "$RID" in
    linux-x64)   DEB_ARCH=amd64;  PKG_ARCH=x86_64 ;;
    linux-arm64) DEB_ARCH=arm64;  PKG_ARCH=aarch64 ;;
    *) die "不支持的 RID '$RID' (只支持 linux-x64 / linux-arm64)" ;;
esac

if [ -z "$HOMEPAGE" ]; then
    warn "未设置 HOMEPAGE, 使用默认值 $DEFAULT_HOMEPAGE"
    HOMEPAGE="$DEFAULT_HOMEPAGE"
fi
if [ -z "$MAINTAINER" ]; then
    warn "未设置 MAINTAINER, 使用默认值 $DEFAULT_MAINTAINER"
    MAINTAINER="$DEFAULT_MAINTAINER"
fi

# deb: Debian Version 只允许 [0-9A-Za-z.+~-], 下划线非法;
#      '-' -> '~' 是预发布惯例, 保证 0.9.0~vibe 排序在 0.9.0 之前
# 注意: bash 会把替换串里的裸 '~' 做 tilde 展开成 $HOME, 需转义成 '\~'
DEB_VER="${VERSION//-/\~}"
# rpm/pacman: Version 允许下划线 ('-' 才是分隔符), 用 '_' 替换 '-'
PVER="${VERSION//-/_}"
# 防御性校验: 任何异常的展开都会在此暴露实值, 而非 dpkg 的晦涩报错
[[ "$DEB_VER" =~ ^[0-9A-Za-z.+~:-]+$ ]] || { echo "非法 DEB_VER='$DEB_VER' (VERSION='$VERSION')"; exit 1; }
[[ "$PVER" =~ ^[0-9A-Za-z._+]+$ ]] || { echo "非法 PVER='$PVER' (VERSION='$VERSION')"; exit 1; }

# 相对路径按仓库根解析 (见文件头说明)
case "$STAGE_DIR" in /*) STAGE_ABS="$STAGE_DIR" ;; *) STAGE_ABS="$ROOT/$STAGE_DIR" ;; esac
case "$WORKER_STAGE_DIR" in /*) WORKER_ABS="$WORKER_STAGE_DIR" ;; *) WORKER_ABS="$ROOT/$WORKER_STAGE_DIR" ;; esac
case "$OUT_DIR"   in /*) OUT_ABS="$OUT_DIR"   ;; *) OUT_ABS="$ROOT/$OUT_DIR"   ;; esac

# ---------- 前置依赖探测 ----------
# 放在真正动手之前: 否则「本机没装 rpmbuild」这种环境问题会伪装成第 170 行的
# dpkg 晦涩报错, 排查成本极高。缺哪个工具就指名道姓地一次性列出全部缺失项。

DESKTOP_SRC="$PACKAGERS_DIR/ai-shikikan.desktop"
DEB_CONTROL_SRC="$PACKAGERS_DIR/deb/control"
RPM_SPEC_SRC="$PACKAGERS_DIR/rpm/ai-shikikan.spec"
LOGO_SRC="$ROOT/src/AIShikikan.Gui/Assets/logo.jpg"
LICENSE_SRC="$ROOT/LICENSE"

# ---------- 校验变体/格式名 ----------
# 先校验「用户输入」再探测「运行环境」: 变体名打错时报「未知变体」远比报
# 「本机缺 dpkg-deb」有用。

for v in $VARIANTS; do
    case "$v" in
        dotnet|selfcontained|aot) ;;
        *) die "未知变体 '$v' (合法值: dotnet / selfcontained / aot)" ;;
    esac
done
for f in $FORMATS; do
    case "$f" in
        deb|rpm|pacman) ;;
        *) die "未知包格式 '$f' (合法值: deb / rpm / pacman)" ;;
    esac
done

# ---------- Worker 产物闸门 (单独 exit, 不并入下面的 missing 列表) ----------
# Worker 缺失不会让应用报错, 只会让 GUI 静默降级为「同进程执行」—— 用户看不到
# 任何提示, 维护者也不会怀疑包, 只有在任务明显变慢之后才隐约觉得不对。
# 这类「装出来看着完整、实际已降级」的坏包比构建失败危险得多, 所以这里硬失败:
# 宁可 CI 红灯, 也不产出一个有 desktop 图标、有主程序、唯独没有 Worker 的包。
# 刻意不并进下面的 missing 列表: 缺 dpkg-deb 是「环境没配好」, 缺 Worker 是
# 「发布链路漏了一步」, 混在一起会让人去装包而不是去查 publish 步骤。
worker_missing=()
for v in $VARIANTS; do
    [ -f "$WORKER_ABS/$v/AIShikikan.Worker" ] \
        || worker_missing+=("$WORKER_ABS/$v/AIShikikan.Worker")
done
if [ "${#worker_missing[@]}" -gt 0 ]; then
    gh_error "缺少 AIShikikan.Worker 产物, 拒绝打包 (缺 Worker 不会崩溃, 只会让应用静默降级为同进程执行, 用户无从察觉):"
    for m in "${worker_missing[@]}"; do err "  - $m"; done
    err "  先跑 ./build.sh <变体> (它会同时产出 GUI 与 Worker), 或 CI 中对应的 Worker publish 步骤。"
    err "  注意 Worker 产物在 artifacts/worker/<variant>, 与 GUI 的 artifacts/<variant> 不是同一个根目录;"
    err "  build.sh 没有 'worker' 这个子命令。"
    err "  自定义了输出目录时用 WORKER_STAGE_DIR=<worker 产物根> 指定。"
    exit 1
fi

missing=()
for f in "$DESKTOP_SRC" "$LOGO_SRC" "$LICENSE_SRC"; do
    [ -f "$f" ] || missing+=("文件 $f")
done
# 待打包的变体必须已有 GUI publish 产物, 否则会装出一个「有 desktop 图标但没有主程序」的坏包
for v in $VARIANTS; do
    [ -f "$STAGE_ABS/$v/AIShikikan.Gui" ] \
        || missing+=("变体产物 $STAGE_ABS/$v/AIShikikan.Gui (先跑 ./build.sh $v, 或用 STAGE_DIR=artifacts 复用其产物; Worker 产物见上面的闸门)")
done
if [[ " $FORMATS " == *" deb "* ]]; then
    [ -f "$DEB_CONTROL_SRC" ] || missing+=("文件 $DEB_CONTROL_SRC")
fi
if [[ " $FORMATS " == *" rpm "* ]]; then
    [ -f "$RPM_SPEC_SRC" ] || missing+=("文件 $RPM_SPEC_SRC")
fi

command -v convert >/dev/null 2>&1 || missing+=("命令 convert (ImageMagick, 用于生成 256x256 图标)")
if [[ " $FORMATS " == *" deb "* ]]; then
    command -v dpkg-deb >/dev/null 2>&1 || missing+=("命令 dpkg-deb (Debian 包, FORMATS 含 deb)")
fi
if [[ " $FORMATS " == *" rpm "* ]]; then
    command -v rpmbuild >/dev/null 2>&1 || missing+=("命令 rpmbuild (RPM 包, FORMATS 含 rpm; Arch 上装 rpm)")
fi
if [[ " $FORMATS " == *" pacman "* ]]; then
    command -v tar >/dev/null 2>&1 || missing+=("命令 tar (Arch 包, FORMATS 含 pacman)")
    tar --help 2>&1 | grep -q -- '--zstd' \
        || missing+=("tar 需支持 --zstd (Arch 包, FORMATS 含 pacman; GNU tar < 1.31 或 bsdtar 缺 zstd 都会踩到)")
fi

if [ "${#missing[@]}" -gt 0 ]; then
    err "缺少以下前置依赖, 请先安装后重试:"
    for m in "${missing[@]}"; do err "  - $m"; done
    err "  Debian/Ubuntu: apt-get install dpkg-dev rpm imagemagick zstd"
    err "  Arch:          pacman -S dpkg rpm imagemagick zstd"
    exit 1
fi

# ---------- 准备共用资源 (图标 / 桌面文件) ----------

# CI 里用的是 $RUNNER_TEMP; 本地则自建临时目录, 并在退出时清掉。
if [ -n "${RUNNER_TEMP:-}" ]; then
    WORK_TMP="$RUNNER_TEMP/ai-shikikan-pack"
    rm -rf "$WORK_TMP"
    mkdir -p "$WORK_TMP"
    CLEAN_TMP=""
else
    WORK_TMP="$(mktemp -d)"
    CLEAN_TMP="$WORK_TMP"
fi
cleanup() {
    if [ -n "$CLEAN_TMP" ]; then rm -rf "$CLEAN_TMP"; fi
}
trap cleanup EXIT

mkdir -p "$OUT_ABS"
convert "$LOGO_SRC" -resize 256x256 "$WORK_TMP/$PKG.png"
cp "$DESKTOP_SRC" "$WORK_TMP/$PKG.desktop"

info "版本:      $VERSION (deb=$DEB_VER, rpm/pacman=$PVER)"
info "架构:      $RID (deb=$DEB_ARCH, rpm/pacman=$PKG_ARCH)"
info "变体:      $VARIANTS"
info "包格式:    $FORMATS"
info "GUI 输入:  $STAGE_ABS"
info "Worker 输入: $WORKER_ABS"
info "包输出:    $OUT_ABS"

# ---------- 应用树安装 (GUI 与 Worker 的布局都必须分支处理) ----------
# GUI 三种变体的发布布局不同, 必须分支安装到 <root>/usr/lib/$PKG:
#   dotnet         框架依赖「多文件」布局 —— apphost + AIShikikan.Gui.dll
#                  + 69 个依赖 DLL + .deps.json + .runtimeconfig.json
#                  + 卫星资源目录 en/。只拷 apphost 与 *.so 会装出
#                  根本无法启动的包 (实测 77 个文件只装进 3 个, 运行报
#                  "The application to execute does not exist:
#                  .../AIShikikan.Gui.dll"), 故整目录安装。
#   aot / selfcontained
#                  单文件布局, 只需 apphost + 同目录原生共享库,
#                  但仍须带卫星资源目录 (en/AIShikikan.Gui.resources.dll
#                  是英文语言包, 非内嵌; 缺失只会回退为中文, 不崩溃)。
# 两种布局都排除 *.pdb 调试符号。
#
# Worker 装到 <root>/usr/lib/$PKG/worker/ (子目录), 布局规则与 GUI 同构但独立:
#   dotnet         框架依赖多文件: AIShikikan.Worker apphost +
#                  AIShikikan.Worker.dll + 自带的 AIShikikan.Core.dll + 其余
#                  依赖 DLL + .deps.json + .runtimeconfig.json, 必须整目录安装
#                  (同 GUI: 只拷 apphost 会装出起不来的包)。
#   aot / selfcontained
#                  只需 AIShikikan.Worker (+ 同目录 *.so, 若存在)。
#
# ⚠️ 刻意让 GUI 的 dotnet 变体与 Worker 的 dotnet 变体各带一份 AIShikikan.Core.dll:
# 两个进程各自独立加载, 升级任一侧都不会影响另一侧。若用
# --additionalprobingpath 让它们共享一份 Core, 就把「版本耦合」引进来了 ——
# 一侧换 Core 另一侧静默漂移, 排查起来几乎没有线索。省下的 ~2MB 远不值得。
#
# $1=目标根 $2=GUI 产物目录 $3=变体名 $4=Worker 产物目录
# (全部显式传参, 不依赖全局变量)
install_app_tree() {
    local tree_root="$1" tree_src="$2" tree_variant="$3" worker_src="$4"
    local libdir="$tree_root/usr/lib/$PKG"
    mkdir -p "$libdir"
    if [ "$tree_variant" = "dotnet" ]; then
        cp -a "$tree_src/." "$libdir/"
        find "$libdir" -name '*.pdb' -delete
    else
        install -m 755 "$tree_src/AIShikikan.Gui" "$libdir/AIShikikan.Gui"
        # 原生共享库 (libSkiaSharp/libHarfBuzzSharp 等) 必须与可执行文件同目录:
        # 运行时 dlopen 动态引用按 应用目录 -> 系统库路径 探测; selfcontained 因
        # IncludeNativeLibrariesForSelfExtract 已内嵌, glob 为空自动跳过
        local so
        for so in "$tree_src"/*.so; do
            [ -e "$so" ] || continue
            install -m 755 "$so" "$libdir/"
        done
        # 卫星资源目录必须随包分发: en/AIShikikan.Gui.resources.dll 是英文
        # 语言包(Strings.en.resx), 并未内嵌进单文件。缺失不崩溃, 但界面会
        # 静默回退为中文。目录名以实际产物为准, 不硬编码。
        # 注: 产物布局里 worker/ 若恰好就位于 GUI 目录内, 这里会顺带整目录带过去;
        # install_worker_tree 随后再显式写一遍, 保证最终形态只由那一个函数决定。
        local d
        for d in "$tree_src"/*/; do
            [ -d "$d" ] || continue
            cp -a "$d" "$libdir/"
        done
    fi
    install_worker_tree "$libdir" "$worker_src" "$tree_variant"
}

# Worker 独立成函数而不是复用 GUI 的分支: GUI 的分支还兼着卫星资源 en/ 之类的
# GUI 专属目录, 两边的布局将来大概率会分叉, 混在一起改一边必然带坏另一边。
install_worker_tree() {
    local libdir="$1" worker_src="$2" tree_variant="$3"
    local wdir="$libdir/worker"
    mkdir -p "$wdir"
    if [ "$tree_variant" = "dotnet" ]; then
        cp -a "$worker_src/." "$wdir/"
        find "$wdir" -name '*.pdb' -delete
    else
        install -Dm755 "$worker_src/AIShikikan.Worker" "$wdir/AIShikikan.Worker"
        local wso
        for wso in "$worker_src"/*.so; do
            [ -e "$wso" ] || continue
            install -m 755 "$wso" "$wdir/"
        done
        local wd
        for wd in "$worker_src"/*/; do
            [ -d "$wd" ] || continue
            cp -a "$wd" "$wdir/"
        done
    fi
}

build_deb() {
    local variant="$1" bindir="$2" worker_bindir="$3"
    local debroot="$WORK_TMP/debroot_$variant"
    rm -rf "$debroot"
    install_app_tree "$debroot" "$bindir" "$variant" "$worker_bindir"
    mkdir -p "$debroot/usr/bin"
    ln -sf "/usr/lib/$PKG/AIShikikan.Gui" "$debroot/usr/bin/$PKG"
    install -Dm644 "$WORK_TMP/$PKG.desktop" "$debroot/usr/share/applications/$PKG.desktop"
    install -Dm644 "$WORK_TMP/$PKG.png" "$debroot/usr/share/icons/hicolor/256x256/apps/$PKG.png"
    install -Dm644 "$LICENSE_SRC" "$debroot/usr/share/doc/$PKG/copyright"
    local size
    size="$(du -sk "$debroot" | cut -f1)"
    mkdir -p "$debroot/DEBIAN"
    sed -e "s|@VERSION@|$DEB_VER|g" \
        -e "s|@ARCH@|$DEB_ARCH|g" \
        -e "s|@MAINTAINER@|$MAINTAINER|g" \
        -e "s|@SIZE@|$size|g" \
        -e "s|@HOMEPAGE@|$HOMEPAGE|g" \
        -e "s|@DESC@|$DESC|g" \
        "$DEB_CONTROL_SRC" > "$debroot/DEBIAN/control"
    # 二进制包的 DEBIAN/control 只允许 RFC822 字段, 不接受 '#' 注释行
    # (dpkg-deb 会报 "field name '#' must be followed by colon", 行号还指向
    # 上一个字段, 极具误导性)。模板里一旦加注释就在这里提前拦下。
    if grep -n '^[[:space:]]*#' "$debroot/DEBIAN/control"; then
        gh_error "Packagers/deb/control 含注释行, 二进制包 control 不允许注释 (说明请写进 Packagers/README.md)"
        exit 1
    fi
    # dpkg 要求 Description 多行字段以换行终止, 缺尾换行会报 missing final newline
    [ -z "$(tail -c1 "$debroot/DEBIAN/control")" ] || printf '\n' >> "$debroot/DEBIAN/control"
    # 框架依赖变体: .NET 运行时来自系统包 (最佳努力, 缺包不阻塞安装);
    # 合并进已有 Recommends, 避免 dpkg-deb "duplicate value" 报错
    if [ "$variant" = "dotnet" ]; then
        if grep -q '^Recommends:' "$debroot/DEBIAN/control"; then
            sed -i 's/^Recommends: \(.*\)$/Recommends: \1, dotnet-runtime-10.0/' "$debroot/DEBIAN/control"
        else
            echo "Recommends: dotnet-runtime-10.0" >> "$debroot/DEBIAN/control"
        fi
    fi
    dpkg-deb --build --root-owner-group "$debroot" "$OUT_ABS/${PKG}_${DEB_VER}_${DEB_ARCH}_${variant}.deb"
    PRODUCED+=("$OUT_ABS/${PKG}_${DEB_VER}_${DEB_ARCH}_${variant}.deb")
    ok "deb:  $(basename "$OUT_ABS/${PKG}_${DEB_VER}_${DEB_ARCH}_${variant}.deb")"
}

build_rpm() {
    local variant="$1" bin="$2" bindir="$3" worker_bindir="$4"
    mkdir -p "$HOME/rpmbuild"/{BUILD,RPMS,SPECS,SOURCES,SRPMS}
    rm -rf "$HOME/rpmbuild"/RPMS/*
    local spec="$HOME/rpmbuild/SPECS/$PKG.spec"
    sed -e "s|@VERSION@|$PVER|g" \
        -e "s|@HOMEPAGE@|$HOMEPAGE|g" \
        -e "s|@DESC@|$DESC|g" \
        -e "s|@BIN@|$bin|g" \
        -e "s|@BINDIR@|$bindir|g" \
        -e "s|@VARIANT@|$variant|g" \
        -e "s|@DESKTOP@|$WORK_TMP/$PKG.desktop|g" \
        -e "s|@ICON@|$WORK_TMP/$PKG.png|g" \
        -e "s|@LICENSE@|$LICENSE_SRC|g" \
        "$RPM_SPEC_SRC" > "$spec"
    # rpm 这条路径不经过 install_app_tree: %install 段写在 spec 模板里, 而
    # 「变体布局怎么装」的唯一权威定义在本脚本。为了不在 spec 里再抄一份分支
    # (两份定义迟早漂移), 这里按变体生成「安装 Worker」的 shell 片段, 注入到
    # %install 末尾 (锚点 = %files 之前)。%files 里 %{_prefix}/lib/ai-shikikan
    # 是整目录条目, worker/ 落在其下, 无需为 Worker 单独加 %files 行。
    # 片段里直接写真实路径而不是 @WORKERBINDIR@ 占位符: 占位符替换发生在注入
    # 之前, 注入进来的占位符不会再被替换, rpmbuild 会把它当字面量路径而失败。
    local wsnippet="$WORK_TMP/rpm_worker_$variant.snippet"
    {
        echo "# ===== 以下两段由 package.sh 注入, 别手改 spec 模板 ====="
        if [ "$variant" != "dotnet" ]; then
            # GUI 侧: 单文件变体的卫星资源目录 (en/ = 英文语言包 Strings.en.resx,
            # **并未内嵌进单文件**)。spec 模板的 %install 只装 apphost + *.so, 漏掉它
            # → 英文界面**静默**回退为中文(不崩溃、不报错、没有任何日志)。
            # deb/pacman 走 install_app_tree, 那里有对应的子目录循环, 所以只有 rpm 会漏。
            # 这里按变体生成同一段循环, 让 package.sh 继续是「变体布局」的唯一权威。
            echo "# --- GUI 卫星资源目录 (与 install_app_tree 的同名循环对齐) ---"
            echo "for gd in $bindir/*/; do [ -d \"\$gd\" ] || continue; cp -a \"\$gd\" %{buildroot}%{_prefix}/lib/$PKG/; done"
        fi
        echo "# --- AIShikikan.Worker (独立进程执行器) ---"
        if [ "$variant" = "dotnet" ]; then
            # 框架依赖多文件: 必须整目录安装(只拷 apphost 会装出起不来的包)
            echo "mkdir -p %{buildroot}%{_prefix}/lib/$PKG/worker"
            echo "cp -a $worker_bindir/. %{buildroot}%{_prefix}/lib/$PKG/worker/"
        else
            echo "install -Dm755 $worker_bindir/AIShikikan.Worker %{buildroot}%{_prefix}/lib/$PKG/worker/AIShikikan.Worker"
            echo "if ls $worker_bindir/*.so >/dev/null 2>&1; then install -m 755 $worker_bindir/*.so %{buildroot}%{_prefix}/lib/$PKG/worker/; fi"
            # Worker 侧的卫星目录(目前无, 但与 GUI 侧同构, 将来加了语言包不用再改 spec)
            echo "for wd in $worker_bindir/*/; do [ -d \"\$wd\" ] || continue; cp -a \"\$wd\" %{buildroot}%{_prefix}/lib/$PKG/worker/; done"
        fi
    } > "$wsnippet"
    if ! awk -v snippet="$wsnippet" '
            /^%files/ && !done {
                while ((getline line < snippet) > 0) print line
                close(snippet)
                done = 1
            }
            { print }
            END { if (!done) exit 3 }
        ' "$spec" > "$spec.tmp"; then
        # 锚点没了 = spec 结构变了, 继续跑就会产出一个没有 Worker 的 rpm。
        # 与 deb control 的注释检查同理: 宁可中止, 不让坏包流出去。
        gh_error "无法把 Worker 安装片段注入 $RPM_SPEC_SRC: 未找到 %files 锚点。请检查 spec 结构, 或把 Worker 的 %install 行加回模板并同步调整本脚本的注入逻辑。"
        exit 1
    fi
    mv "$spec.tmp" "$spec"
    # 框架依赖变体: 声明系统 .NET 运行时 (rpm Recommends 不可满足仅告警)
    if [ "$variant" = "dotnet" ]; then
        sed -i '/^AutoReqProv: no/a Recommends: dotnet-runtime-10.0' "$spec"
    fi
    rpmbuild -bb --target "$PKG_ARCH" "$spec"
    local r
    for r in "$HOME"/rpmbuild/RPMS/*/*.rpm; do
        [ -e "$r" ] || continue
        # ⚠️ 必须**先算出最终文件名再动文件**。
        # 原实现先 mv 再用 ${r##*/} 记 PRODUCED, 而 $r 在 mv 之后仍指向旧名 ——
        # 于是 PRODUCED 里存的是一个磁盘上根本不存在的路径, 末尾的
        # ls "${PRODUCED[@]}" 必然报 "No such file or directory",
        # 让 package.sh 以退出码 2 失败(CI 实测踩到)。同一个 bug 还让上面那行
        # ok 日志打印出没有变体后缀的假文件名, 排障时被带偏。
        local renamed="${r%.rpm}_${variant}.rpm"
        local out="$OUT_ABS/${renamed##*/}"
        mv -f "$r" "$renamed"
        cp -f "$renamed" "$out"
        PRODUCED+=("$out")
        ok "rpm:  ${out##*/}"
    done
}

build_pacman() {
    local variant="$1" bindir="$2" worker_bindir="$3"
    local pkgroot="$WORK_TMP/pkgroot_$variant"
    rm -rf "$pkgroot"
    install_app_tree "$pkgroot" "$bindir" "$variant" "$worker_bindir"
    mkdir -p "$pkgroot/usr/bin"
    ln -sf "/usr/lib/$PKG/AIShikikan.Gui" "$pkgroot/usr/bin/$PKG"
    install -Dm644 "$WORK_TMP/$PKG.desktop" "$pkgroot/usr/share/applications/$PKG.desktop"
    install -Dm644 "$WORK_TMP/$PKG.png" "$pkgroot/usr/share/icons/hicolor/256x256/apps/$PKG.png"
    install -Dm644 "$LICENSE_SRC" "$pkgroot/usr/share/licenses/$PKG/LICENSE"
    local pkgsize
    pkgsize="$(du -sb "$pkgroot" | cut -f1)"
    cat > "$pkgroot/.PKGINFO" <<EOF
# Generated by Packagers/linux/package.sh
pkgname = $PKG
pkgver = $PVER-1
pkgdesc = AI Agent commander desktop app
url = $HOMEPAGE
builddate = $(date +%s)
packager = GitHub Actions
size = $pkgsize
arch = $PKG_ARCH
license = MIT
depend = libx11
depend = libxcursor
depend = libxrandr
depend = libxrender
depend = libxext
depend = libxfixes
depend = libxi
depend = libice
depend = libsm
depend = libxkbcommon
depend = libglvnd
depend = mesa
depend = fontconfig
depend = glib2
depend = icu
EOF
    # 框架依赖变体: 声明系统 .NET 运行时 (optdependency)
    if [ "$variant" = "dotnet" ]; then
        printf 'optdepend = dotnet-runtime: 框架依赖变体需要系统 .NET 10 运行时\n' >> "$pkgroot/.PKGINFO"
    fi
    tar --zstd -cf "$OUT_ABS/${PKG}-${PVER}-1-${PKG_ARCH}_${variant}.pkg.tar.zst" \
        -C "$pkgroot" .PKGINFO usr
    PRODUCED+=("$OUT_ABS/${PKG}-${PVER}-1-${PKG_ARCH}_${variant}.pkg.tar.zst")
    ok "pacman: ${PKG}-${PVER}-1-${PKG_ARCH}_${variant}.pkg.tar.zst"
}

# ---------- 主流程 ----------

# 本次运行实际产出的文件 (而不是 glob 匹配目录): 局部跑某种格式时,
# glob 里的 rpm/pkg.tar.zst 匹配不到会被原样传给 ls 并报错, 造成「明明产出了
# deb 却提示无产物」的误导。
PRODUCED=()

echo ""
info "=== Packaging Linux packages (deb / rpm / pacman) ==="

for VARIANT in $VARIANTS; do
    BIN="$STAGE_ABS/$VARIANT/AIShikikan.Gui"
    BINDIR="$(dirname "$BIN")"
    WORKER_BIN="$WORKER_ABS/$VARIANT/AIShikikan.Worker"
    WORKER_BINDIR="$(dirname "$WORKER_BIN")"
    chmod +x "$BIN"
    chmod +x "$WORKER_BIN"
    info "== 打包变体 $VARIANT =="
    for format in $FORMATS; do
        case "$format" in
            deb)    build_deb "$VARIANT" "$BINDIR" "$WORKER_BINDIR" ;;
            rpm)    build_rpm "$VARIANT" "$BIN" "$BINDIR" "$WORKER_BINDIR" ;;
            pacman) build_pacman "$VARIANT" "$BINDIR" "$WORKER_BINDIR" ;;
        esac
    done
done

echo ""
if [ "${#PRODUCED[@]}" -gt 0 ]; then
    info "产物列表:"
    ls -lh "${PRODUCED[@]}"
else
    warn "(无产物)"
fi
echo ""
ok "打包完成"