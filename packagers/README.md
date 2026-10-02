# packagers — Linux 系统包打包源文件

本目录保存 deb / rpm(dnf) / pacman 三种 Linux 系统包所需的打包源文件,
由 `.github/workflows/release.yml` 在 CI 中引用, 也可供本地维护者复用。

| 路径 | 目标 | 说明 |
|------|------|------|
| `ai-shikikan.desktop` | 全部 | 桌面入口文件 (共享) |
| `deb/control` | Debian / Ubuntu (.deb) | control 模板, 占位符由 CI 的 sed 替换 |
| `rpm/ai-shikikan.spec` | Fedora / RHEL / dnf (.rpm) | spec 模板, 占位符由 CI 的 sed 替换 |
| `pacman/PKGBUILD` | Arch Linux / Manjaro (.pkg.tar.zst) | makepkg 打包脚本 |

## 模板占位符

`deb/control` 与 `rpm/ai-shikikan.spec` 是模板文件, 发布工作流用 `sed` 把
`@VAR@` 替换为实际值:

| 占位符 | 含义 |
|--------|------|
| `@VERSION@` | 版本号 (rpm/pacman 中 `-` 替换为 `_`; deb 中替换为 `~`, 因 Debian 版本号不允许下划线) |
| `@ARCH@` / 由 `--target` 指定 | 架构 (amd64/arm64 / x86_64/aarch64) |
| `@MAINTAINER@` | 维护者 |
| `@SIZE@` | deb 安装体积 (KiB) |
| `@HOMEPAGE@` | 项目主页 |
| `@DESC@` | 描述文本 |
| `@BIN@` `@DESKTOP@` `@ICON@` `@LICENSE@` | rpm 打包时的源文件绝对路径 |

> ⚠️ **`deb/control` 里不能写任何 `#` 注释行**（连首行也不行）。二进制包的
> `DEBIAN/control` 只允许 RFC822 字段，dpkg 解析器把 `#` 当字段名，于是报
> `field name '#' must be followed by colon`，且行号指向**上一个**字段的下一行，
> 极具误导性。说明文字请写在本文件，或写成 CI 步骤里的 `#` 注释。
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

### Debian (dpkg-deb 手工布局)

CI 按 `deb/control` 模板套用布局后调用 `dpkg-deb --build`。

### RPM (rpmbuild)

```bash
mkdir -p ~/rpmbuild/{BUILD,RPMS,SPECS,SOURCES,SRPMS}
# 按模板替换占位符后:
rpmbuild -bb --target x86_64 ~/rpmbuild/SPECS/ai-shikikan.spec
```

### Arch (makepkg)

```bash
cd packagers/pacman
makepkg -f   # 需 Arch 环境 + base-devel, 联网下载 release 资产
```

`PKGBUILD` 的 `pkgver` 需与根目录 `VERSION` 保持一致, 发布前建议把
`sha256sums` 的 `SKIP` 替换为真实校验值。