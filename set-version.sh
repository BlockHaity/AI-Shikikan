#!/usr/bin/env bash
# 统一修改 AI-Shikikan 版本号。想改版本时运行一次即可, 无需逐个文件手改。
# 会同步更新:
#   VERSION                          - 单一版本源
#   packagers/pacman/PKGBUILD        - pkgver (Arch 禁 '-', 自动用下划线) 与 _ghver
#   AGENTS.md                        - "当前版本" 标注
#   app.manifest                     - Win32 4 段数值版本 (取版本号的数字部分)
#   build.sh/ps1, debug.sh/ps1       - VERSION 缺失时的回退版本
#   .github/workflows/*.yml          - CI 回退版本 (VERSION 缺失时的兜底)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
info()  { echo -e "${CYAN}[INFO]${NC} $*"; }
ok()    { echo -e "${GREEN}[OK]${NC} $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()   { echo -e "${RED}[ERROR]${NC} $*" >&2; }

# 原地改文件: sed -i 的后缀参数在 GNU/BSD 下语义不同 —— GNU 直接跟选项,
# BSD 要求给一个后缀占位(习惯上给空串)。本脚本是本地开发脚本, 而项目同时
# 支持 macOS, 因此统一走 sed_i, 免得 macOS 上把脚本当成备份后缀而失败。
if [ "$(uname -s)" = "Darwin" ]; then
  sed_i() { local head=("${@:1:$#-1}"); sed -i '' "${head[@]}" "${!#}"; }
else
  sed_i() { sed -i "$@"; }
fi

# sed 正则字面量转义: 版本号里的 '.' 在正则里是通配符, 直接当 pattern 会顺带
# 改坏别处的文本(如把 '1.2.0' 匹配到 '1x2y0')。
sed_escape() { printf '%s' "$1" | sed -e 's/[][\.^$*+?(){}|\\/]/\\&/g'; }

CURRENT_VERSION="$(cat VERSION 2>/dev/null | tr -d '[:space:]')"

FALLBACK_FILES=(
  build.sh
  build.ps1
  debug.sh
  debug.ps1
  .github/workflows/release.yml
  .github/workflows/debug.yml
)

usage() {
    cat <<EOF
用法: $(basename "$0") <新版本号>   # 改版本
      $(basename "$0") current      # 显示当前版本

改版本时无需加 'v' 前缀 (GitHub tag 会自动补 v)。
会同步更新 VERSION / packagers/pacman/PKGBUILD / AGENTS.md / app.manifest
及构建脚本与 CI 工作流中的回退版本。

样例:
  $(basename "$0") 0.9.0
  $(basename "$0") 0.9.1-vibe

当前版本: ${CURRENT_VERSION:-未知}
EOF
}

show_current=0
if [ $# -lt 1 ]; then
    usage
    exit 1
fi
case "$1" in
    -h|--help|help)  usage; exit 0 ;;
    current|-v|--version) echo "$CURRENT_VERSION"; exit 0 ;;
esac

NEW_VERSION="${1#v}"   # 容忍传入 "v0.9.0"

# 简单校验: 数字开头, 仅允许数字/字母/./_/-
if ! [[ "$NEW_VERSION" =~ ^[0-9][0-9A-Za-z.]*([._-][0-9A-Za-z._-]+)?$ ]]; then
    err "非法版本号: $NEW_VERSION (应形如 0.9.0 或 0.9.1-vibe)"
    exit 1
fi

if [ "$NEW_VERSION" = "$CURRENT_VERSION" ]; then
    warn "版本号未变化 ($CURRENT_VERSION), 无需更新"
    exit 0
fi

OLD="$CURRENT_VERSION"
NEW="$NEW_VERSION"

# ---- 1) VERSION ----
printf '%s\n' "$NEW" > VERSION
ok "VERSION -> $NEW"

# ---- 2) packagers/pacman/PKGBUILD ----
PKGBUILD=packagers/pacman/PKGBUILD
ARCH_NEW="${NEW//-/_}"
sed_i -E "s/^pkgver=.*/pkgver=$ARCH_NEW/" "$PKGBUILD"
sed_i -E "s/^_ghver=.*/_ghver=$NEW/" "$PKGBUILD"
ok "packagers/pacman/PKGBUILD -> pkgver=$ARCH_NEW, _ghver=$NEW"

# ---- 3) AGENTS.md 版本标注 ----
# 用 python3 而非 sed: 中文全角括号在 sed 的反向引用下与 UTF-8 多字节混排时
# 匹配不可靠, 曾出现"脚本报告成功但文档仍写着旧版本"的静默失败。
# 注意标注的实际写法是「（当前 0.9.0-vibe, 用 ... 更新）」—— 右括号在句末,
# 并不紧跟版本号, 所以只匹配「（当前 <版本号>」这一段。仅用标准库, 无外部依赖。
python3 - "$NEW" <<'PY'
import re, sys, io

new = sys.argv[1]
path = "AGENTS.md"
text = io.open(path, encoding="utf-8").read()

# 兼容全角「（当前 x.y.z」与半角 "(当前 x.y.z" 两种写法
pattern = re.compile(r'([（(])当前\s+[0-9A-Za-z._-]+')
updated, count = pattern.subn(lambda m: f"{m.group(1)}当前 {new}", text)

if count == 0:
    sys.exit(f"{path}: 未找到「当前 x.y.z」版本标注, 请手动更新")

io.open(path, "w", encoding="utf-8").write(updated)
print(f"    (替换 {count} 处)")
PY
ok "AGENTS.md 版本标注 -> $NEW"

# ---- 4) app.manifest Win32 数值版本 (取数字部分, 补足 4 段) ----
if [[ "$NEW" =~ ^([0-9]+(\.[0-9]+){0,3}) ]]; then
    NUM_PART="${BASH_REMATCH[1]}"
else
    NUM_PART="$NEW"
fi
DOTS="${NUM_PART//[0-9]/}"
nparts=$(( ${#DOTS} + 1 ))
case "$nparts" in
    1) MANIFEST_VER="$NUM_PART.0.0.0" ;;
    2) MANIFEST_VER="$NUM_PART.0.0"   ;;
    3) MANIFEST_VER="$NUM_PART.0"     ;;
    *) MANIFEST_VER="$NUM_PART"       ;;
esac
sed_i -E "s#<assemblyIdentity version=\"[0-9.]+\"#<assemblyIdentity version=\"$MANIFEST_VER\"#g" app.manifest
ok "app.manifest -> $MANIFEST_VER"

# ---- 5) 构建脚本 / CI 里的回退版本 (VERSION 缺失时的兜底) ----
# 旧版本号可能是新版本号的前缀(如 1.2.0 vs 1.2.0-vibe), 因此在 OLD 后面加
# 边界断言, 避免把已经存在的 1.2.0-vibe 一起改坏。版本号可含 '.' '-' '_',
# 边界取"非版本字符或行尾"。
OLD_RE="$(sed_escape "$OLD")"
BOUNDARY='([^0-9A-Za-z._-]|$)'
for f in "${FALLBACK_FILES[@]}"; do
    if [ -f "$f" ] && grep -qE -- "$OLD_RE$BOUNDARY" "$f"; then
        sed_i -E "s/$OLD_RE$BOUNDARY/$NEW\\1/g" "$f"
        ok "$f 回退版本已同步"
    fi
done

echo ""
ok "版本已更新为 $NEW"
info "GitHub Release 信息: tag=v$NEW, 发布名=v$NEW"
info "建议提交并推送 (镜像会自动同步到 GitHub):"
echo "    git add -A && git commit -m \"chore: 更新版本号为 $NEW\" && git push origin vibe"