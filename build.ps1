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
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "0.9.0-vibe" }
$OutputDir = Join-Path $ProjectDir "artifacts"
$GuiProject = Join-Path $ProjectDir "AIShikikan.Gui.csproj"

$hostOs = if ($IsLinux) { "linux" } elseif ($IsMacOS) { "osx" } else { "win" }
$hostArch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq
    [System.Runtime.InteropServices.Architecture]::Arm64) { "arm64" } else { "x64" }
$HostRid = "$hostOs-$hostArch"

function Write-Info($msg)  { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Warn($msg)  { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Write-Err($msg)   { Write-Host "[ERROR] $msg" -ForegroundColor Red }

function Build-Variant {
    param([string]$Variant)

    $OutDir = Join-Path $OutputDir $Variant
    Write-Info "=== Building $Variant for $HostRid ==="

    switch ($Variant) {
        "aot" {
            dotnet publish $GuiProject `
                -c $Configuration -r $HostRid -o $OutDir `
                --self-contained true `
                -p:PublishAot=true -p:PublishSingleFile=true -p:StripSymbols=true `
                -p:Version=$Version
        }
        "selfcontained" {
            dotnet publish $GuiProject `
                -c $Configuration -r $HostRid -o $OutDir `
                --self-contained true `
                -p:PublishAot=false -p:PublishSingleFile=true `
                -p:PublishTrimmed=true -p:TrimMode=partial `
                -p:IncludeNativeLibrariesForSelfExtract=true `
                -p:Version=$Version
        }
        "dotnet" {
            dotnet publish $GuiProject `
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

    if ($LASTEXITCODE -ne 0) {
        Write-Err "Failed to build $Variant for $HostRid"
        exit 1
    }
    Write-Ok "$Variant built: $OutDir"
}

function Pack-Variant {
    param([string]$Variant)

    $dir = Join-Path $OutputDir $Variant
    if (-not (Test-Path $dir)) { return }

    if ($IsWindows) {
        $archive = Join-Path $OutputDir "AIShikikan-$Version-$HostRid-$Variant.zip"
        Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $archive -Force
    }
    elseif (Get-Command tar -ErrorAction SilentlyContinue) {
        $bin = Join-Path $dir "AIShikikan.Gui"
        if (Test-Path $bin) { chmod +x $bin 2>$null | Out-Null }
        Push-Location $OutputDir
        try {
            tar czf "AIShikikan-$Version-$HostRid-$Variant.tar.gz" `
                --exclude='*.pdb' --exclude='*.dbg' -C "$Variant" .
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

Commands:
  (none)         Build all three variants and package them
  aot            Build only the AOT variant
  selfcontained  Build only the self-contained variant
  dotnet         Build only the framework-dependent variant
  clean          Clean build artifacts

Options:
  -Configuration Release   Build configuration (default: Release)
  -Version 0.9.0-vibe           Version string (default: from VERSION file)

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
