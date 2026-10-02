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

function Write-Info($msg)  { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Warn($msg)  { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Write-Err($msg)   { Write-Host "[ERROR] $msg" -ForegroundColor Red }

function Show-Usage {
    Write-Host @"
Usage: .\debug.ps1 [-NoBuild] [-Configuration <cfg>] [app arguments...]

Build and launch the GUI via `dotnet run` (framework-dependent, no publish, no AOT binary).

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