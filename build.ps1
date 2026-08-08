param(
    [Parameter(Position=0)]
    [ValidateSet("linux", "macos", "windows", "all", "clean")]
    [string]$Command = "all",

    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",

    [ValidateSet("x64", "arm64", "both")]
    [string]$Arch = "both",

    [ValidateSet("auto", "always", "off")]
    [string]$AotMode = "auto"
)

$ErrorActionPreference = "Stop"

$ProjectDir = $PSScriptRoot
$OutputDir = Join-Path $ProjectDir "artifacts"
$CliProject = Join-Path $ProjectDir "src/AgentCommander.Cli/AgentCommander.Cli.csproj"
$GuiProject = Join-Path $ProjectDir "src/AgentCommander.Gui/AgentCommander.Gui.csproj"

$hostOs = if ($IsLinux) { "linux" } elseif ($IsMacOS) { "osx" } else { "win" }
$hostArch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq
    [System.Runtime.InteropServices.Architecture]::Arm64) { "arm64" } else { "x64" }
$HostRid = "$hostOs-$hostArch"

function Get-PlatformBase([string]$platform) {
    switch ($platform) {
        "linux" { return "linux" }
        "macos" { return "osx" }
        "windows" { return "win" }
        default { return "" }
    }
}

function Get-RidsFor([string]$platform) {
    $base = Get-PlatformBase $platform
    if (-not $base) { return @() }
    if ($Arch -eq "x64") { return @("$base-x64") }
    if ($Arch -eq "arm64") { return @("$base-arm64") }
    return @("$base-x64", "$base-arm64")
}

function Test-WantAot([string]$rid) {
    switch ($AotMode) {
        "off" { return $false }
        "always" { return $true }
        default { return $rid -eq $HostRid }
    }
}

function Write-Info($msg)  { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Warn($msg)  { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Write-Err($msg)   { Write-Host "[ERROR] $msg" -ForegroundColor Red }

function Build-Cli {
    param([string]$Rid, [string]$OutDir)

    if (Test-WantAot $Rid) {
        Write-Info "Building CLI for $Rid with Native AOT..."
        dotnet publish $CliProject `
            -c $Configuration `
            -r $Rid `
            -o $OutDir `
            --self-contained true `
            -p:PublishAot=true `
            -p:StripSymbols=true `
            -p:PublishSingleFile=true `
            -p:Version=$Version
    }
    else {
        Write-Warn "AOT_MODE=$AotMode: host=$HostRid, target=$Rid; cannot cross AOT, falling back to single-file/trimmed"
        Write-Info "Building CLI for $Rid (single-file/trimmed)..."
        dotnet publish $CliProject `
            -c $Configuration `
            -r $Rid `
            -o $OutDir `
            --self-contained true `
            -p:PublishAot=false `
            -p:PublishSingleFile=true `
            -p:PublishTrimmed=true `
            -p:TrimMode=partial `
            -p:Version=$Version `
            -p:IncludeNativeLibrariesForSelfExtract=true
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Err "Failed to build CLI for $Rid"
        exit 1
    }

    Write-Ok "CLI built: $OutDir"
}

function Build-Gui {
    param([string]$Rid, [string]$OutDir)

    Write-Info "Building GUI for $Rid..."
    dotnet publish $GuiProject `
        -c $Configuration `
        -r $Rid `
        -o $OutDir `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:Version=$Version `
        -p:IncludeNativeLibrariesForSelfExtract=true

    if ($LASTEXITCODE -ne 0) {
        Write-Err "Failed to build GUI for $Rid"
        exit 1
    }

    Write-Ok "GUI built: $OutDir"
}

function Build-Rid {
    param([string]$Platform, [string]$Rid)

    $outdir = Join-Path $OutputDir $Rid
    Write-Info "=== $Rid (CLI + GUI) ==="
    Build-Cli $Rid $outdir
    Build-Gui $Rid $outdir
    Write-Ok "$Rid bundled: $outdir"
}

function Pack-Rid {
    param([string]$Rid)

    $dir = Join-Path $OutputDir $Rid
    if (-not (Test-Path $dir)) { return }

    if ($IsWindows) {
        $archive = Join-Path $OutputDir "AgentCommander-$Version-$Rid.zip"
        Compress-Archive -Path "$dir/*" -DestinationPath $archive -Force
    }
    elseif (Get-Command tar -ErrorAction SilentlyContinue) {
        Push-Location $OutputDir
        try {
            tar czf "AgentCommander-$Version-$Rid.tar.gz" -C "$Rid" .
            if ($LASTEXITCODE -ne 0) {
                Write-Warn "tar z failed for $Rid, retrying without gzip"
                tar cf "AgentCommander-$Version-$Rid.tar" -C "$Rid" .
            }
        }
        finally {
            Pop-Location
        }
    }
    else {
        Write-Warn "No tar found, skipping compression for $Rid"
        return
    }

    Write-Ok "Packed $Rid"
}

function Clean-Artifacts {
    Write-Info "Cleaning build artifacts..."
    if (Test-Path $OutputDir) {
        Remove-Item -Recurse -Force $OutputDir
    }
    dotnet clean (Join-Path $ProjectDir "AgentCommander.slnx") -c $Configuration 2>$null | Out-Null
    Write-Ok "Cleaned"
}

Write-Host ""
Write-Host "  ========================================" -ForegroundColor White
Write-Host "  |     Agent Commander Build System      |" -ForegroundColor White
Write-Host "  ========================================" -ForegroundColor White
Write-Host ""
Write-Info "Configuration: $Configuration"
Write-Info "Version:       $Version"
Write-Info "ARCH:          $Arch"
Write-Info "AOT_MODE:      $AotMode (host RID: $HostRid)"
Write-Info "Output:        $OutputDir"
Write-Host ""

switch ($Command) {
    { $_ -in @("linux", "macos", "windows") } {
        foreach ($rid in (Get-Rids $_)) { Build-Rid $_ $rid }
        foreach ($rid in (Get-Rids $_)) { Pack-Rid $rid }
    }
    "all" {
        foreach ($platform in @("linux", "macos", "windows")) {
            foreach ($rid in (Get-Rids $platform)) { Build-Rid $platform $rid }
        }
        foreach ($platform in @("linux", "macos", "windows")) {
            foreach ($rid in (Get-Rids $platform)) { Pack-Rid $rid }
        }
    }
    "clean" {
        Clean-Artifacts
    }
}

Write-Host ""
Write-Ok "Build complete!"
if (Test-Path $OutputDir) {
    $archives = Get-ChildItem -Path $OutputDir -MaxDepth 1 -Include "*.zip","*.tar.gz","*.tar" -File
    if ($archives) {
        Write-Info "Artifacts:"
        foreach ($a in $archives) {
            $size = [math]::Round($a.Length / 1MB, 2)
            Write-Host "  $($a.Name) (${size} MB)"
        }
    }
}