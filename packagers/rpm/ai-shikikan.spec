%global debug_package %{nil}
%undefine _missing_build_ids_terminate_build

Name:     ai-shikikan
Version:  @VERSION@
Release:  1
Summary:  AI Agent commander desktop app
License:  MIT
URL:      @HOMEPAGE@
AutoReqProv: no

%description
@DESC@

%install
rm -rf %{buildroot}
install -Dm755 @BIN@ %{buildroot}%{_prefix}/lib/ai-shikikan/AIShikikan.Gui
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