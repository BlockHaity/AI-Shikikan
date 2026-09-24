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
| `@VERSION@` | 版本号 (deb/rpm 中 `-` 会被替换为 `_`) |
| `@ARCH@` / 由 `--target` 指定 | 架构 (amd64/arm64 / x86_64/aarch64) |
| `@MAINTAINER@` | 维护者 |
| `@SIZE@` | deb 安装体积 (KiB) |
| `@HOMEPAGE@` | 项目主页 |
| `@DESC@` | 描述文本 |
| `@BIN@` `@DESKTOP@` `@ICON@` `@LICENSE@` | rpm 打包时的源文件绝对路径 |

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