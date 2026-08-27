param(
    [Parameter(Position=0)]
    [ValidateSet("gui", "help")]
    [string]$App = "gui",

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
$GuiProject = Join-Path $ProjectDir "AIShikikan.Gui.csproj"

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

function Publish-Gui {
    if (Test-WantAot) {
        Write-Info "Publishing GUI for $HostRid (AOT enabled)..."
        dotnet publish $GuiProject `
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
        Write-Info "Publishing GUI for $HostRid (single-file/trimmed)..."
        dotnet publish $GuiProject `
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
        Write-Err "Failed to publish GUI for $HostRid"
        exit 1
    }
}

function Show-Usage {
    Write-Host @"
Usage: .\debug.ps1 [gui] [app arguments...]

Compile a Debug build for the current platform and launch the app.

Arguments:
   remainder  Passed through to the launched app

Examples:
  .\debug.ps1
  .\debug.ps1 --version
  .\debug.ps1 doctor
"@
}

Write-Host ""
Write-Info "Configuration: $Configuration"
Write-Info "Version:       $Version"
Write-Info "AOT_MODE:      $AotMode (host RID: $HostRid)"
Write-Info "Output:        $OutputDir"
Write-Host ""

switch ($App) {
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
