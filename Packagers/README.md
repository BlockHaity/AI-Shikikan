# Packagers — Linux 系统包打包源文件

本目录保存 deb / rpm(dnf) / pacman 三种 Linux 系统包所需的打包源文件与打包脚本。
打包逻辑统一收敛在 `linux/package.sh`，由 `.github/workflows/release.yml` 与
`.github/workflows/debug.yml` 调用（两个工作流不再各自内嵌一份重复 bash），
也可在本地直接复用。

| 路径 | 目标 | 说明 |
|------|------|------|
| `linux/package.sh` | 全部 | **打包入口**：读 `$STAGE_DIR/<variant>` 产物，产出 deb / rpm / pacman 三种包 |
| `ai-shikikan.desktop` | 全部 | 桌面入口文件 (共享) |
| `deb/control` | Debian / Ubuntu (.deb) | control 模板, 占位符由 `package.sh` 的 sed 替换 |
| `rpm/ai-shikikan.spec` | Fedora / RHEL / dnf (.rpm) | spec 模板, 占位符由 `package.sh` 的 sed 替换 |
| `pacman/PKGBUILD` | Arch Linux / Manjaro (.pkg.tar.zst) | makepkg 打包脚本（CI 不走这条路径，手工/原生 Arch 打包用） |

## `linux/package.sh` 环境变量契约

在**仓库根目录**执行。相对路径（`STAGE_DIR` / `OUT_DIR`）一律按仓库根解析，
因此在任意 cwd 下调用结果一致。

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `VERSION` | **必填** | 版本号（如 `1.0.0-vibe`）。缺失或格式非法（非数字开头 / 含非法字符）立即报错退出 |
| `VARIANTS` | `dotnet selfcontained` | 空格分隔的变体名，合法值 `dotnet` / `selfcontained` / `aot` |
| `FORMATS` | `deb rpm pacman` | 空格分隔的包格式。默认全开 = CI 行为；本地缺工具时可裁剪（如 `FORMATS=deb`） |
| `RID` | 按 `uname -m` 探测 | 只接受 `linux-x64` / `linux-arm64`，其他立即报错 |
| `STAGE_DIR` | `stage` | 发布产物根目录，每个变体在 `$STAGE_DIR/<variant>`，可执行文件名固定 `AIShikikan.Gui` |
| `OUT_DIR` | `dist` | 系统包输出目录 |
| `PKG` | `ai-shikikan` | 包名 |
| `HOMEPAGE` | `https://github.com/BlockHaity/AI-Shikikan` | 缺失时告警并使用默认值 |
| `MAINTAINER` | `BlockHaity <blockhaity@users.noreply.github.com>` | 缺失时告警并使用默认值 |

产物命名（下游脚本与用户已依赖，改动会破坏兼容）：

```
$OUT_DIR/ai-shikikan_<DEB_VER>_<amd64|arm64>_<variant>.deb
$OUT_DIR/ai-shikikan-<PVER>-1-<PKG_ARCH>_<variant>.pkg.tar.zst
$OUT_DIR/<rpmbuild 产出的名字>_<variant>.rpm
```

其中 deb 版本号把 `-` 换成 `~`（Debian 版本不允许下划线），rpm/pacman 换成 `_`
（rpm/pacman 版本不允许 `-`，那是 release 分隔符）。

### 外部工具依赖

脚本在动手前会一次性探测下列工具，缺哪个就指名道姓地报错退出（不会让打包跑到
一半才失败）：

| 工具 | 何时需要 | 安装 |
|------|----------|------|
| `dpkg-deb` | `FORMATS` 含 `deb` | Debian/Ubuntu: `apt-get install dpkg-dev`；Arch: `pacman -S dpkg` |
| `rpmbuild` | `FORMATS` 含 `rpm` | Debian/Ubuntu: `apt-get install rpm`；Arch: `pacman -S rpm` |
| `tar`（需支持 `--zstd`） | `FORMATS` 含 `pacman` | `zstd`；GNU tar < 1.31 不支持 `--zstd` |
| `convert`（ImageMagick） | 总是（生成 256×256 图标） | `apt-get install imagemagick` / `pacman -S imagemagick` |

### CI 调用方式

```bash
VERSION=1.0.0-vibe \
RID=linux-x64 \
VARIANTS="dotnet selfcontained aot" \
HOMEPAGE="$GITHUB_SERVER_URL/$GITHUB_REPOSITORY" \
MAINTAINER="$GITHUB_ACTOR <$GITHUB_ACTOR@users.noreply.github.com>" \
./Packagers/linux/package.sh
```

（脚本会复用 `$RUNNER_TEMP` 作为临时目录，与原 workflow 行为一致。）

## 模板占位符

`deb/control` 与 `rpm/ai-shikikan.spec` 是模板文件，`linux/package.sh` 用 `sed` 把
`@VAR@` 替换为实际值：

| 占位符 | 含义 |
|--------|------|
| `@VERSION@` | 版本号 (rpm/pacman 中 `-` 替换为 `_`; deb 中替换为 `~`, 因 Debian 版本号不允许下划线) |
| `@ARCH@` / 由 `--target` 指定 | 架构 (amd64/arm64 / x86_64/aarch64) |
| `@MAINTAINER@` | 维护者 |
| `@SIZE@` | deb 安装体积 (KiB) |
| `@HOMEPAGE@` | 项目主页 |
| `@DESC@` | 描述文本 |
| `@BIN@` `@BINDIR@` `@VARIANT@` | rpm 打包时的产物绝对路径与变体名（决定 `%install` 分支） |
| `@DESKTOP@` `@ICON@` `@LICENSE@` | rpm 打包时的源文件绝对路径 |

> ⚠️ **`deb/control` 里不能写任何 `#` 注释行**（连首行也不行）。二进制包的
> `DEBIAN/control` 只允许 RFC822 字段，dpkg 解析器把 `#` 当字段名，于是报
> `field name '#' must be followed by colon`，且行号指向**上一个**字段的下一行，
> 极具误导性。说明文字请写在本文件，或写成 `linux/package.sh` 里的 `#` 注释
> （脚本会在构建前主动 `grep` 拦截）。
> （`rpm/ai-shikikan.spec` 是 spec 脚本，允许注释，不受此限制。）

## 运行时依赖的来由

`Depends` / `Recommends` / `Requires` 里的库不是抄来的，是 Avalonia 12 的 X11 后端
在运行时实际 `dlopen` 的系统库：

| 库 | 为什么需要 |
|----|------------|
| `libx11` `libxcursor` `libxrandr` `libxrender` `libxext` `libxfixes` `libxi` | X11 窗口/光标/输入原语 |
| `libice6` `libsm6`（rpm: `libICE` `libSM`） | XIM 输入法（fcitx/ibus）硬依赖，缺失会导致 X11 平台起不来 |
| `libxkbcommon0`（rpm: `libxkbcommon`） | X11/XKB 键盘布局通用运行库；产物未直接 `dlopen`，属防御性声明，与桌面/输入法链路保持一致 |
| `libgl1`（rpm: `mesa-libGL`） | Skia 的 GL 后端 |
| `libglib2.0-0`（rpm: `glib2`）、`fontconfig`、`freetype` | 字体与 GLib 支撑 |
| ICU（deb: `Recommends` 宽版本链 63~78，rpm: `Requires: libicu`） | .NET Linux 运行时只带 `libSystem.Globalization.Native.so`，实际 `dlopen` 系统 ICU。**deb 侧故意降级为 Recommends**：各发行版 ICU 版本不同（jammy=70、24.04=74、25.04/26.04=76/78、bookworm=72、trixie=76），固定版本链会让 Ubuntu 22.04 等主流发行版直接装不上；缺 ICU 时 .NET 回退 invariant 模式仍可运行 |

`glib2.0-0` 与 ICU 的版本链用 `|` 提供新旧包名二选一，兼容引入 t64 后缀的发行版。

## 本地打包

`linux/package.sh` 消费的输入就是 `./build.sh` 的产物目录，因此本地打包是两步：

```bash
# 1) 先构建 (三种变体: aot / selfcontained / dotnet, 产物在 artifacts/<variant>/)
./build.sh                 # 全量; 或 ./build.sh dotnet 只出一个变体

# 2) 再打包 (默认复用 artifacts/ 作为 STAGE_DIR)
VERSION=$(cat VERSION) STAGE_DIR=artifacts VARIANTS=dotnet ./Packagers/linux/package.sh
```

只想验证某个变体的 deb、且本机没装 rpm/zstd 时：

```bash
VERSION=$(cat VERSION) STAGE_DIR=artifacts VARIANTS=dotnet FORMATS=deb ./Packagers/linux/package.sh
dpkg-deb -I dist/*.deb      # 读 Package / Version / Architecture
dpkg-deb -c dist/*.deb      # 看 /usr/bin/ai-shikikan 与 /usr/lib/ai-shikikan/AIShikikan.Gui
```

常用组合：

```bash
# CI 等价调用 (三种变体 + 三种格式)
VERSION=$(cat VERSION) STAGE_DIR=artifacts VARIANTS="dotnet selfcontained aot" ./Packagers/linux/package.sh

# 也可直接吃 workflow 的原始产物目录 stage/
VERSION=1.0.0-vibe VARIANTS=dotnet ./Packagers/linux/package.sh
```

清理：`rm -rf dist stage artifacts`。

### Arch (makepkg)

CI 不走 makepkg（`.pkg.tar.zst` 由 `package.sh` 用 `tar --zstd` 直接生成）；
`pacman/PKGBUILD` 面向 Arch 原生环境 / 打包复现：

```bash
cd Packagers/pacman
makepkg -f   # 需 Arch 环境 + base-devel, 联网下载 release 资产
```

`PKGBUILD` 的 `pkgver` / `_ghver` 由 `set-version.sh` 与根目录 `VERSION` 同步维护；
发布前建议把 `sha256sums` 的 `SKIP` 替换为真实校验值。