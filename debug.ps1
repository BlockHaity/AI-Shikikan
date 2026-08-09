param(
    [Parameter(Position=0)]
    [ValidateSet("cli", "gui", "help")]
    [string]$App = "cli",

    [string]$Configuration = "Debug",
    [string]$Version = "",

    [ValidateSet("auto", "always", "off")]
    [string]$AotMode = "off",

    [Parameter(ValueFromRemainingArguments=$true)]
    [string[]]$AppArgs = @()
)

$ErrorActionPreference = "Stop"

$ProjectDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content -Raw -ErrorAction SilentlyContinue "$ProjectDir/VERSION")
    if ($Version) { $Version = $Version.Trim() }
}
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "1.0.0" }
$OutputDir = Join-Path $ProjectDir "artifacts/debug"
$CliProject = Join-Path $ProjectDir "src/AIShikikan.Cli/AIShikikan.Cli.csproj"
$GuiProject = Join-Path $ProjectDir "src/AIShikikan.Gui/AIShikikan.Gui.csproj"

$hostOs = if ($IsLinux) { "linux" } elseif ($IsMacOS) { "osx" } else { "win" }
$hostArch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq
    [System.Runtime.InteropServices.Architecture]::Arm64) { "arm64" } else { "x64" }
$HostRid = "$hostOs-$hostArch"

function Write-Info($msg)  { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Warn($msg)  { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Write-Err($msg)   { Write-Host "[ERROR] $msg" -ForegroundColor Red }

function Test-WantAot {
    switch ($AotMode) {
        "off" { return $false }
        "always" { return $true }
        default { return $true }
    }
}

function Publish-Cli {
    if (Test-WantAot) {
        Write-Info "Publishing CLI for $HostRid (AOT enabled)..."
        dotnet publish $CliProject `
            -c $Configuration `
            -r $HostRid `
            -o $OutputDir `
            --self-contained true `
            -p:PublishAot=true `
            -p:StripSymbols=true `
            -p:PublishSingleFile=true `
            -p:Version=$Version
    }
    else {
        Write-Info "Publishing CLI for $HostRid (single-file/trimmed)..."
        dotnet publish $CliProject `
            -c $Configuration `
            -r $HostRid `
            -o $OutputDir `
            --self-contained true `
            -p:PublishAot=false `
            -p:PublishSingleFile=true `
            -p:PublishTrimmed=true `
            -p:TrimMode=partial `
            -p:Version=$Version `
            -p:IncludeNativeLibrariesForSelfExtract=true
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Err "Failed to publish CLI for $HostRid"
        exit 1
    }
}

function Publish-Gui {
    Write-Info "Publishing GUI for $HostRid..."
    dotnet publish $GuiProject `
        -c $Configuration `
        -r $HostRid `
        -o $OutputDir `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:Version=$Version `
        -p:IncludeNativeLibrariesForSelfExtract=true

    if ($LASTEXITCODE -ne 0) {
        Write-Err "Failed to publish GUI for $HostRid"
        exit 1
    }
}

function Show-Usage {
    Write-Host @"
Usage: .\debug.ps1 <cli|gui> [app arguments...]

Compile a Debug build for the current platform and launch the app.

Arguments:
  cli|gui   App to launch: cli (terminal UI / REST API) or gui (Avalonia GUI)
  remainder Passed through to the launched app

Examples:
  .\debug.ps1 cli
  .\debug.ps1 cli --persona senior-architect
  .\debug.ps1 cli api --port 8090
  .\debug.ps1 gui
"@
}

Write-Host ""
Write-Info "Configuration: $Configuration"
Write-Info "Version:       $Version"
Write-Info "AOT_MODE:      $AotMode (host RID: $HostRid)"
Write-Info "Output:        $OutputDir"
Write-Host ""

switch ($App) {
    "cli" {
        Publish-Cli
        Write-Ok "CLI built, launching..."
        & (Join-Path $OutputDir "AIShikikan.Cli") @AppArgs
        exit $LASTEXITCODE
    }
    "gui" {
        Publish-Gui
        Write-Ok "GUI built, launching..."
        & (Join-Path $OutputDir "AIShikikan.Gui") @AppArgs
        exit $LASTEXITCODE
    }
    default {
        Show-Usage
        exit 1
    }
}