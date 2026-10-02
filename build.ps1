param(
    [Parameter(Position=0)]
    [ValidateSet("all", "aot", "selfcontained", "dotnet", "clean", "help")]
    [string]$Command = "all",

    [string]$Configuration = "Release",
    [string]$Version = ""
)

# 仅构建「当前平台」的三种变体 (与 build.sh 对齐):
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
# 产物布局 (与 build.sh 一致):
#   artifacts/<variant>/         GUI 变体
#   artifacts/worker/<variant>/  Worker 变体, 与同名 GUI 变体一一对应
# 归档 (zip) 里 Worker 位于 worker/ 子目录, 与 GUI 平级 —— 对应 WorkerLocator 的候选 3,
# 同时避开 AOT 原生库与框架依赖托管 DLL 的同名覆盖。

$ErrorActionPreference = "Stop"

# Windows PowerShell 5.1 没有 $IsWindows/$IsLinux/$IsMacOS 自动变量；5.1 仅存在于 Windows
if ($null -eq (Get-Variable -Name IsWindows -ErrorAction SilentlyContinue)) {
    $IsWindows = $true; $IsLinux = $false; $IsMacOS = $false
}

$ProjectDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content -Raw -ErrorAction SilentlyContinue "$ProjectDir/VERSION")
    if ($Version) { $Version = $Version.Trim() }
}
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "1.0.0-vibe" }
$OutputDir = Join-Path $ProjectDir "artifacts"
$GuiProject = Join-Path $ProjectDir "src/AIShikikan.Gui/AIShikikan.Gui.csproj"
$WorkerProject = Join-Path $ProjectDir "src/AIShikikan.Worker/AIShikikan.Worker.csproj"

# Worker 产物不塞进 artifacts\<variant>\, 而是另起一级:
# 产物目录是 Pack-Variant 的输入, 而 dotnet publish 不会清空输出目录 ——
# 把 Worker 拷进去, 下一次构建的归档就会带上「上一次的陈旧 Worker」,
# 而归档文件名恒定不变, 陈旧内容会原样进到用户下载到的包里。
$WorkerOutputRoot = Join-Path $OutputDir "worker"
# 归档 / Linux 系统包里的 Worker 子目录名, 与 WorkerLocator.WorkerSubdirectoryName 保持一致
$WorkerPackageSubdir = "worker"

$hostOs = if ($IsLinux) { "linux" } elseif ($IsMacOS) { "osx" } else { "win" }
$hostArch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq
    [System.Runtime.InteropServices.Architecture]::Arm64) { "arm64" } else { "x64" }
$HostRid = "$hostOs-$hostArch"

function Write-Info($msg)  { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Warn($msg)  { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Write-Err($msg)   { Write-Host "[ERROR] $msg" -ForegroundColor Red }

# $Variant=变体名 $Project=项目文件 $OutDir=输出目录
# GUI 与 Worker 走同一个函数: 变体参数只在这一处写一遍。
# 两处各写一份必然漂移, 而漂移一次就会产出「GUI 是 AOT、Worker 是框架依赖」的包 ——
# 装 AOT 包的用户会因为 Worker 缺 .NET 运行时而静默退化成进程内执行, 界面上没有任何报错。
# (PowerShell 5.1 没有 ?? / 三元运算符, 这里只用 switch + 普通赋值)
function Publish-Variant {
    param([string]$Variant, [string]$Project, [string]$OutDir)

    switch ($Variant) {
        "aot" {
            dotnet publish $Project `
                -c $Configuration -r $HostRid -o $OutDir `
                --self-contained true `
                -p:PublishAot=true -p:PublishSingleFile=true -p:StripSymbols=true `
                -p:Version=$Version
        }
        "selfcontained" {
            dotnet publish $Project `
                -c $Configuration -r $HostRid -o $OutDir `
                --self-contained true `
                -p:PublishAot=false -p:PublishSingleFile=true `
                -p:PublishTrimmed=true -p:TrimMode=partial `
                -p:IncludeNativeLibrariesForSelfExtract=true `
                -p:Version=$Version
        }
        "dotnet" {
            dotnet publish $Project `
                -c $Configuration -r $HostRid -o $OutDir `
                --self-contained false `
                -p:PublishAot=false `
                -p:Version=$Version
        }
        default {
            Write-Err "Unknown variant: $Variant"
            exit 1
        }
    }

    # 原生命令的非零退出码不会被 $ErrorActionPreference = Stop 转成异常 (5.1 与 7.x 都是),
    # 所以必须自己判; dotnet publish 失败时绝不能继续往下走
    if ($LASTEXITCODE -ne 0) {
        Write-Err "Failed to publish $Variant for $HostRid ($Project)"
        exit 1
    }
}

function Build-Variant {
    param([string]$Variant)

    $OutDir = Join-Path $OutputDir $Variant
    Write-Info "=== Building $Variant for $HostRid ==="

    Publish-Variant -Variant $Variant -Project $GuiProject -OutDir $OutDir
    Write-Ok "$Variant built: $OutDir"

    # Worker 与 GUI 同配置、同 RID、同版本、同发布参数, 只是换个输出目录
    if (Test-Path $WorkerProject) {
        $WorkerOutDir = Join-Path $WorkerOutputRoot $Variant
        Write-Info "--- Building worker for $Variant ($HostRid) ---"
        Publish-Variant -Variant $Variant -Project $WorkerProject -OutDir $WorkerOutDir
        Write-Ok "worker/$Variant built: $WorkerOutDir"
    }
    else {
        # Worker 项目尚未落地时不让 GUI 的构建被拖死: WorkerLocator 会降级成进程内执行,
        # 功能不受影响。真要发布时 Pack-Variant 会再告警一次, 不会静默漏掉。
        Write-Warn "Worker project not found: $WorkerProject (skipping the $Variant worker build)"
    }
}

function Pack-Variant {
    param([string]$Variant)

    $dir = Join-Path $OutputDir $Variant
    if (-not (Test-Path $dir)) { return }

    $workerDir = Join-Path $WorkerOutputRoot $Variant

    # 归档要求 GUI 与 worker\ 平级, 但产物目录不能被污染 (见 $WorkerOutputRoot 的注释),
    # 因此整份内容先拷进临时目录再打包。try/finally 保证中途失败也不残留临时目录;
    # (5.1 没有 $PSScriptRoot 之外的自动清理机制, 也不用依赖 CI 的 $RUNNER_TEMP)
    $stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) `
        ("ai-shikikan-pack-" + [guid]::NewGuid().ToString("N"))
    $stageDir = Join-Path $stagingRoot $Variant

    try {
        New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
        # Get-ChildItem -Force + 管道, 而不是 Copy-Item -Path "$dir\*":
        # 后者的通配符枚举在部分宿主上会漏掉隐藏项
        Get-ChildItem -Path $dir -Force | Copy-Item -Destination $stageDir -Recurse -Force

        if (Test-Path $workerDir) {
            $workerSubdir = Join-Path $stageDir $WorkerPackageSubdir
            # GUI 产物目录里本来就带 worker\ 说明它是别处拷进来的 (旧布局残留等):
            # Copy-Item 到一个已存在的目录会拷成 worker\<变体名> 嵌套一层, 归档看起来完全正常,
            # 但 GUI 的候选 3 找不到可执行文件 —— 先清掉, 让 Worker 产物目录说了算
            if (Test-Path $workerSubdir) {
                Write-Warn "GUI output already contains a $WorkerPackageSubdir\ subdirectory; discarding it in favour of the worker output directory"
                Remove-Item -Recurse -Force $workerSubdir
            }
            Copy-Item -Path $workerDir -Destination $workerSubdir -Recurse -Force
        }
        else {
            # 不 fail: 打包不一定紧跟构建 (只想重打 GUI 的包是合法的), 但必须出声 ——
            # 缺 Worker 的包装出去, 用户拿到的就是「工具静默退回进程内执行」, 无从察觉。
            Write-Warn "Variant $Variant has no worker output ($workerDir); archive will not contain $WorkerPackageSubdir\ and tools fall back to in-process execution"
        }

        if ($IsWindows) {
            $archive = Join-Path $OutputDir "AIShikikan-$Version-$HostRid-$Variant.zip"
            Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $archive -Force
        }
        elseif (Get-Command tar -ErrorAction SilentlyContinue) {
            # 产物目录里的 GUI 也要保持可执行: README 让用户直接 ./artifacts/<variant>/AIShikikan.Gui
            $guiBin = Join-Path $dir "AIShikikan.Gui"
            if (Test-Path $guiBin) { chmod +x $guiBin 2>$null | Out-Null }
            # 归档里的两个可执行文件同样补一次: 归档在解压到另一台机器后不保留权限位
            foreach ($exe in @("AIShikikan.Gui", (Join-Path $WorkerPackageSubdir "AIShikikan.Worker"))) {
                $exePath = Join-Path $stageDir $exe
                if (Test-Path $exePath) { chmod +x $exePath 2>$null | Out-Null }
            }
            Push-Location $stageDir
            try {
                $tarball = Join-Path $OutputDir "AIShikikan-$Version-$HostRid-$Variant.tar.gz"
                tar czf $tarball --exclude='*.pdb' --exclude='*.dbg' .
            }
            finally {
                Pop-Location
            }
        }
        else {
            Write-Warn "No tar found, skipping compression for $Variant"
            return
        }

        Write-Ok "Packed $Variant"
    }
    finally {
        if (Test-Path $stagingRoot) {
            Remove-Item -Recurse -Force $stagingRoot -ErrorAction SilentlyContinue
        }
    }
}

function Clean-Artifacts {
    Write-Info "Cleaning build artifacts..."
    if (Test-Path $OutputDir) {
        Remove-Item -Recurse -Force $OutputDir
    }
    dotnet clean (Join-Path $ProjectDir "AIShikikan.slnx") -c $Configuration 2>$null | Out-Null
    Write-Ok "Cleaned"
}

function Show-Usage {
    Write-Host @"
Usage: .\build.ps1 [command]

Builds the current platform ($HostRid) only, in three variants:
  aot            Native AOT (no runtime needed, fastest startup)
  selfcontained  Bundled .NET runtime, single-file (trimmed)
  dotnet         Framework-dependent (requires .NET runtime installed)

Each variant also builds AIShikikan.Worker with identical publish settings.
Artifact layout:
  artifacts/<variant>/          GUI
  artifacts/worker/<variant>/   Worker, paired with the same variant
  archives ship the worker under worker\ (next to the GUI)

Commands:
  (none)         Build all three variants and package them
  aot            Build only the AOT variant
  selfcontained  Build only the self-contained variant
  dotnet         Build only the framework-dependent variant
  clean          Clean build artifacts

Options:
  -Configuration Release   Build configuration (default: Release)
  -Version 1.0.0-vibe           Version string (default: from VERSION file)

Examples:
  .\build.ps1
  .\build.ps1 aot
  .\build.ps1 selfcontained -Configuration Debug
"@
}

Write-Host ""
Write-Host "  AI-Shikikan Build System"
Write-Host ""
Write-Info "Configuration: $Configuration"
Write-Info "Version:       $Version"
Write-Info "Host RID:      $HostRid"
Write-Info "Output:        $OutputDir"
Write-Host ""

$variants = switch ($Command) {
    "all"           { @("aot", "selfcontained", "dotnet") }
    "aot"           { @("aot") }
    "selfcontained" { @("selfcontained") }
    "dotnet"        { @("dotnet") }
    "clean"         { Clean-Artifacts; exit 0 }
    "help"          { Show-Usage; exit 0 }
    default         { Write-Err "Unknown command: $Command"; Show-Usage; exit 1 }
}

foreach ($v in $variants) { Build-Variant $v }
foreach ($v in $variants) { Pack-Variant $v }

Write-Host ""
Write-Ok "Build complete!"
if (Test-Path $OutputDir) {
    $archives = @(Get-ChildItem -Path (Join-Path $OutputDir '*') -File |
        Where-Object { $_.Name -like '*.zip' -or $_.Name -like '*.tar.gz' })
    if ($archives.Count -gt 0) {
        Write-Info "Artifacts:"
        foreach ($a in $archives) {
            $size = [math]::Round($a.Length / 1MB, 2)
            Write-Host "  $($a.Name) (${size} MB)"
        }
    }
    else {
        Write-Warn "(no archives found)"
    }
}
