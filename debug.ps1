<#
.SYNOPSIS
    以「能跑起来」为唯一目的的本地调试入口（框架依赖运行，不 publish）。

.DESCRIPTION
    以前这里跑的是完整的 dotnet publish，每次改一行代码都要重新打包一遍
    (自包含 + 单文件 + 裁剪)，调试成本被构建吃掉了。
    现在改用 `dotnet run`：Debug JIT 直接启动 bin\ 下的中间产物，增量编译完
    就启动程序本身，中间没有 publish 这一步。

    代价是 debug 不再产出任何二进制：Native AOT 链接、自包含、单文件打包
    只能在发布链路里验证，需要时请用 .\build.ps1 aot（或 .\build.ps1 all）。

    工作目录刻意保持为调用者 shell 的当前目录 —— 不 Set-Location 到项目目录。
    AppShell.Boot 用 Environment.CurrentDirectory 判定 git 工作区根，
    一旦 cd 到 src\AIShikikan.Gui\，doctor 会把项目子目录当成工作区、
    聊天页的检查点也会建在错误的位置。用 `dotnet run --project <路径>`
    就能指定项目而保持 cwd 不变，与旧的 exec 产物方式行为一致。

    Worker（工具执行子进程 AIShikikan.Worker）用框架依赖构建一次，再把绝对路径
    注入 AISHIKIKAN_WORKER_PATH，让 WorkerLocator 的候选 1 确定命中：
    `dotnet run --project <GUI>` 只构建 GUI 的依赖图（ProjectReference 链），
    独立项目 Worker 不在其间；而候选 4 要靠上溯 AIShikikan.slnx 再猜
    bin\<配置>\<tfm> 路径，注入环境变量是确定性的，且它的语义是「显式指定」——
    路径写错会立刻报错，而不是悄悄降级成进程内执行。

    版本号不做传参：dotnet run 没有 -p:Version 选项（-p 是 --project 的缩写），
    根 Directory.Build.props 已从 VERSION 文件注入，这里再传只会是伪能力。
#>
# PositionalBinding=$false 是必须的：PowerShell 默认按声明顺序做位置绑定，
# 于是 `.\debug.ps1 doctor` 的 doctor 会被当成 -Configuration 的值吞掉，
# 正是旧的 [ValidateSet]$App 定位参数在挡的那类坑。关掉位置绑定后，
# -NoBuild/-Configuration 只能具名传入，裸 token 一律落到 $AppArgs 直穿程序。
[CmdletBinding(PositionalBinding = $false)]
param(
    # 跳过编译，直接跑上一次编译的产物（等价 $env:NO_BUILD=1）
    [switch]$NoBuild,

    # 构建配置，映射到 dotnet run -c
    [string]$Configuration = "Debug",

    # 显示用法后退出
    [switch]$Help,

    # 程序参数直穿：.\debug.ps1 doctor / .\debug.ps1 --version 都直接可用
    [Parameter(ValueFromRemainingArguments=$true)]
    [string[]]$AppArgs = @()
)

$ErrorActionPreference = "Stop"

$ProjectDir = $PSScriptRoot
$GuiProject = Join-Path $ProjectDir "src/AIShikikan.Gui/AIShikikan.Gui.csproj"
$WorkerProject = Join-Path $ProjectDir "src/AIShikikan.Worker/AIShikikan.Worker.csproj"

# Windows PowerShell 5.1 没有 $IsWindows/$IsLinux/$IsMacOS 自动变量；5.1 仅存在于 Windows
if ($null -eq (Get-Variable -Name IsWindows -ErrorAction SilentlyContinue)) {
    $IsWindows = $true; $IsLinux = $false; $IsMacOS = $false
}

# Worker 可执行文件名: Windows 是 AIShikikan.Worker.exe, 其他平台无扩展名。
# 与 WorkerLocator.GetExecutableName() 是同一条规则; 两处各拼一份迟早漂移成
# 「定位用无扩展名、启动用 .exe」这种只在单个平台上出现的偏差。
# (5.1 没有三元运算符, 用 if 表达式赋值)
$WorkerExeName = if ($IsWindows) { "AIShikikan.Worker.exe" } else { "AIShikikan.Worker" }

# Worker 用框架依赖构建（开发期不 publish、不产出 AOT），产物落在这个目录。
# 刻意不传 -r/--runtime：只有指定了 RID 时 MSBuild 才会多下沉一层目录，
# 不传才是 <config>\<tfm>\ 这种两段式布局，也与 WorkerLocator 候选 4 的
# 第一优先探测路径一致。TFM 与根 Directory.Build.props 的 TargetFramework 对应。
$WorkerTfm = "net10.0"
$WorkerOutputDir = Join-Path $ProjectDir "src/AIShikikan.Worker/bin/$Configuration/$WorkerTfm"
# restore 产物：决定这次能否用 --no-restore（见 Build-Worker 的注释）
$WorkerAssets = Join-Path $ProjectDir "src/AIShikikan.Worker/obj/project.assets.json"

function Write-Info($msg)  { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Warn($msg)  { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Write-Err($msg)   { Write-Host "[ERROR] $msg" -ForegroundColor Red }

# 构建 Worker 并把 AISHIKIKAN_WORKER_PATH 指向它。任何一步失败都只告警不中断:
# WorkerLocator 未命中时会降级为进程内执行 (InlineTransport), 功能不受影响 ——
# 为一个辅助进程把 GUI 卡在启动入口才是更糟的失败模式。
function Build-Worker {
    if (-not (Test-Path $WorkerProject)) {
        Write-Warn "Worker project not found: $WorkerProject, skipping build (tools fall back to in-process execution)"
        return
    }

    Write-Info "Building AIShikikan.Worker ($Configuration, framework-dependent)..."

    # --no-restore: GUI 的 dotnet run 已经 restore 过依赖图, 再 restore 一次纯属浪费。
    # 唯一的例外是冷启动 —— dotnet run 恢复的是 GUI 的项目图 (含 ProjectReference 链),
    # 独立项目 Worker 不在其中; 此时 assets 文件还不存在, 仍带 --no-restore
    # 会直接 NETSDK1004 失败。所以先看 assets 在不在, 而不是等失败再补救。
    $buildArgs = @("build", $WorkerProject, "-c", $Configuration)
    if (Test-Path $WorkerAssets) {
        $buildArgs += "--no-restore"
    }
    else {
        Write-Info "Worker has not been restored yet; letting it restore this time"
    }

    & dotnet @buildArgs
    # 原生命令的非零退出码不会被 $ErrorActionPreference = Stop 转成异常, 必须自己判
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "Worker build failed, continuing anyway (tools fall back to in-process execution)"
        return
    }

    $workerExe = Join-Path $WorkerOutputDir $WorkerExeName
    if (Test-Path $workerExe) {
        # 必须是绝对路径: 相对路径会被 WorkerLocator 按 GUI 进程的当前工作目录解析,
        # 而本脚本刻意保持调用者的 cwd, 那个目录通常不是仓库根, 解析结果必然指错。
        $env:AISHIKIKAN_WORKER_PATH = $workerExe
        Write-Ok "Worker: $env:AISHIKIKAN_WORKER_PATH"
    }
    else {
        # 不导出一个不存在的路径: 候选 1 是「显式指定」语义, 配错即报错,
        # 那会让「脚本算错了路径」变成一个比降级更难查的问题。
        Write-Warn "Worker output not found ($workerExe); AISHIKIKAN_WORKER_PATH not set, tools fall back to in-process execution"
    }
}

function Show-Usage {
    Write-Host @"
Usage: .\debug.ps1 [-NoBuild] [-Configuration <cfg>] [app arguments...]

Build and launch the GUI via `dotnet run` (framework-dependent, no publish, no AOT binary).
AIShikikan.Worker is built first (framework-dependent) and its absolute path is
exported as AISHIKIKAN_WORKER_PATH; a missing or broken Worker never blocks the
GUI (tools fall back to in-process execution).

Arguments:
   -NoBuild              Skip the build and run the last build output
   -Configuration <cfg>  Build configuration (default: Debug)
   remainder             Passed through to the launched app

Note:
   PowerShell binds `-NoBuild`, not `--no-build`: a double-dash token with an
   embedded dash (`--no-build`) is not a valid parameter name here and will not
   reach this switch. Use `-NoBuild`, or set `$env:NO_BUILD=1`, which is honored
   as well.
   For the same reason, app arguments are passed through as bare tokens
   (`.\debug.ps1 doctor`), not via a `-App` selector.

Examples:
  .\debug.ps1
  .\debug.ps1 doctor
  .\debug.ps1 --version
  .\debug.ps1 -NoBuild doctor
  `$env:NO_BUILD=1; .\debug.ps1 doctor

AOT / self-contained / single-file artifacts must be validated with:
  .\build.ps1 aot
"@
}

# 帮助既认 -Help 开关，也认首个位置参数 help / --help / -h（与 debug.sh 对齐；
# 程序本身没有 help 子命令，所以不会误伤透传参数）
if ($Help -or ($AppArgs.Count -gt 0 -and ($AppArgs[0] -eq "help" -or $AppArgs[0] -eq "--help" -or $AppArgs[0] -eq "-h"))) {
    Show-Usage
    exit 0
}

$skipBuild = $NoBuild.IsPresent -or ($env:NO_BUILD -eq "1")

if (-not (Test-Path $GuiProject)) {
    Write-Err "Executable project not found: $GuiProject"
    exit 1
}

Write-Host ""
Write-Info "Project:       $GuiProject"
Write-Info "Configuration: $Configuration"
if ($skipBuild) {
    # 跳过编译意味着可能跑的是上一轮代码，这里明确提醒而不是默默启动
    Write-Warn "NO_BUILD in effect: skipping build, running existing output"
    # Worker 同样不编译; 上一轮产物仍在 src\AIShikikan.Worker\bin\ 下,
    # WorkerLocator 的候选 4 (上溯 AIShikikan.slnx 再拼 bin 路径) 照样能命中,
    # 所以不注入 AISHIKIKAN_WORKER_PATH 也不会丢功能
}
else {
    # 必须在 & dotnet @runArgs 之前: GUI 的构建走 dotnet run, 之后没有插入余地
    Build-Worker
}
Write-Host ""

# 参数用数组拼好再展开：@AppArgs 这类直穿在混合参数时容易丢掉引号，
# 且 PowerShell 不会自动插入 -- 分隔符，必须自己加
$runArgs = @("run", "--project", $GuiProject, "-c", $Configuration)
if ($skipBuild) {
    $runArgs += "--no-build"
}
# 只有真的还有程序参数时才追加分隔用的 --，避免留下空的尾随 --（部分场景会被当成程序参数）
if ($AppArgs.Count -gt 0) {
    $runArgs += "--"
    $runArgs += $AppArgs
}

Write-Ok "Launching via dotnet run..."
& dotnet @runArgs
# 退出码透传给调用方（doctor 等子命令靠它判成败）
exit $LASTEXITCODE