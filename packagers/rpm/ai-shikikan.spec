%global debug_package %{nil}
%undefine _missing_build_ids_terminate_build

Name:     ai-shikikan
Version:  @VERSION@
Release:  1
Summary:  AI Agent commander desktop app
License:  MIT
URL:      @HOMEPAGE@
# 依赖取自 Avalonia 12 X11 后端实际 dlopen 的系统库 (libX11/libXcursor/libXrandr/
# libXrender/libXext/libXfixes/libXi/libICE/libSM) + GL/GLib/fontconfig/freetype/ICU。
# libICE/libSM 是输入法 (fcitx/ibus, XIM) 的硬依赖, 缺失会导致 X11 平台起不来。
Requires: libX11, libXcursor, libXrandr, libXrender, libXext, libXfixes, libXi, libICE, libSM, mesa-libGL, glib2, fontconfig, freetype, libicu
AutoReqProv: no

%description
@DESC@

%install
rm -rf %{buildroot}
mkdir -p %{buildroot}%{_prefix}/lib/ai-shikikan
# 三种变体的发布布局不同, 按 @VARIANT@ 分支:
#   dotnet  框架依赖「多文件」布局 —— apphost + AIShikikan.Gui.dll + 依赖 DLL +
#          .deps.json + .runtimeconfig.json + 卫星资源目录 en/。只装 apphost
#          会得到无法启动的包 (缺主 DLL), 故整目录安装。
#   其它    单文件布局, 卫星资源已内嵌进单文件, 只需 apphost + 同目录原生共享库。
case "@VARIANT@" in
  dotnet)
    cp -a @BINDIR@/. %{buildroot}%{_prefix}/lib/ai-shikikan/
    # *.pdb 为调试符号, 不随包分发
    find %{buildroot}%{_prefix}/lib/ai-shikikan -name '*.pdb' -delete
    ;;
  *)
    install -m 755 @BIN@ %{buildroot}%{_prefix}/lib/ai-shikikan/AIShikikan.Gui
    # 原生共享库与可执行文件同目录, 供运行时 dlopen 动态引用; glob 为空则跳过
    if ls @BINDIR@/*.so >/dev/null 2>&1; then
      install -m 755 @BINDIR@/*.so %{buildroot}%{_prefix}/lib/ai-shikikan/
    fi
    ;;
esac
chmod 755 %{buildroot}%{_prefix}/lib/ai-shikikan/AIShikikan.Gui
mkdir -p %{buildroot}%{_bindir}
ln -sf %{_prefix}/lib/ai-shikikan/AIShikikan.Gui %{buildroot}%{_bindir}/ai-shikikan
install -Dm644 @DESKTOP@ %{buildroot}%{_datadir}/applications/ai-shikikan.desktop
install -Dm644 @ICON@ %{buildroot}%{_datadir}/icons/hicolor/256x256/apps/ai-shikikan.png
install -Dm644 @LICENSE@ %{buildroot}%{_docdir}/ai-shikikan/LICENSE

%files
%{_prefix}/lib/ai-shikikan
%{_bindir}/ai-shikikan
%{_datadir}/applications/ai-shikikan.desktop
%{_datadir}/icons/hicolor/256x256/apps/ai-shikikan.png
%{_docdir}/ai-shikikan