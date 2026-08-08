param(
    [Parameter(Position=0)]
    [ValidateSet("linux", "macos", "windows", "all", "clean")]
    [string]$Command = "all",

    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",

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
    param([string]$Rid)

    $outdir = Join-Path $OutputDir "cli/$Rid"

    if (Test-WantAot $Rid) {
        Write-Info "Building CLI for $Rid with Native AOT..."
        dotnet publish $CliProject `
            -c $Configuration `
            -r $Rid `
            -o $outdir `
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
            -o $outdir `
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

    Write-Ok "CLI built: $outdir"
}

function Build-Gui {
    param([string]$Rid)

    $outdir = Join-Path $OutputDir "gui/$Rid"
    Write-Info "Building GUI for $Rid..."

    dotnet publish $GuiProject `
        -c $Configuration `
        -r $Rid `
        -o $outdir `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:Version=$Version `
        -p:IncludeNativeLibrariesForSelfExtract=true

    if ($LASTEXITCODE -ne 0) {
        Write-Err "Failed to build GUI for $Rid"
        exit 1
    }

    Write-Ok "GUI built: $outdir"
}

function Build-Platform {
    param([string]$Platform)

    switch ($Platform) {
        "linux" {
            Write-Info "=== Building for Linux ==="
            Build-Cli "linux-x64"
            Build-Cli "linux-arm64"
            Build-Gui "linux-x64"
            Build-Gui "linux-arm64"
        }
        "macos" {
            Write-Info "=== Building for macOS ==="
            Build-Cli "osx-x64"
            Build-Cli "osx-arm64"
            Build-Gui "osx-x64"
            Build-Gui "osx-arm64"
        }
        "windows" {
            Write-Info "=== Building for Windows ==="
            Build-Cli "win-x64"
            Build-Cli "win-arm64"
            Build-Gui "win-x64"
            Build-Gui "win-arm64"
        }
    }
}

function Pack-Platform {
    param([string]$Platform)

    Write-Info "Packing $Platform artifacts..."

    switch ($Platform) {
        "linux" {
            $rids = @("linux-x64", "linux-arm64")
            foreach ($rid in $rids) {
                $cliDir = Join-Path $OutputDir "cli/$rid"
                $guiDir = Join-Path $OutputDir "gui/$rid"

                if (Test-Path $cliDir) {
                    $archive = Join-Path $OutputDir "cli/AgentCommander.Cli-$Version-$rid.zip"
                    Compress-Archive -Path "$cliDir/*" -DestinationPath $archive -Force
                }
                if (Test-Path $guiDir) {
                    $archive = Join-Path $OutputDir "gui/AgentCommander.Gui-$Version-$rid.zip"
                    Compress-Archive -Path "$guiDir/*" -DestinationPath $archive -Force
                }
            }
        }
        "macos" {
            $rids = @("osx-x64", "osx-arm64")
            foreach ($rid in $rids) {
                $cliDir = Join-Path $OutputDir "cli/$rid"
                $guiDir = Join-Path $OutputDir "gui/$rid"

                if (Test-Path $cliDir) {
                    $archive = Join-Path $OutputDir "cli/AgentCommander.Cli-$Version-$rid.zip"
                    Compress-Archive -Path "$cliDir/*" -DestinationPath $archive -Force
                }
                if (Test-Path $guiDir) {
                    $archive = Join-Path $OutputDir "gui/AgentCommander.Gui-$Version-$rid.zip"
                    Compress-Archive -Path "$guiDir/*" -DestinationPath $archive -Force
                }
            }
        }
        "windows" {
            $rids = @("win-x64", "win-arm64")
            foreach ($rid in $rids) {
                $cliDir = Join-Path $OutputDir "cli/$rid"
                $guiDir = Join-Path $OutputDir "gui/$rid"

                if (Test-Path $cliDir) {
                    $archive = Join-Path $OutputDir "cli/AgentCommander.Cli-$Version-$rid.zip"
                    Compress-Archive -Path "$cliDir/*" -DestinationPath $archive -Force
                }
                if (Test-Path $guiDir) {
                    $archive = Join-Path $OutputDir "gui/AgentCommander.Gui-$Version-$rid.zip"
                    Compress-Archive -Path "$guiDir/*" -DestinationPath $archive -Force
                }
            }
        }
    }

    Write-Ok "Packed $Platform artifacts"
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
Write-Info "AOT_MODE:      $AotMode (host RID: $HostRid)"
Write-Info "Output:        $OutputDir"
Write-Host ""

switch ($Command) {
    { $_ -in @("linux", "macos", "windows") } {
        Build-Platform $_
        Pack-Platform $_
    }
    "all" {
        Build-Platform "linux"
        Build-Platform "macos"
        Build-Platform "windows"
        Pack-Platform "linux"
        Pack-Platform "macos"
        Pack-Platform "windows"
    }
    "clean" {
        Clean-Artifacts
    }
}

Write-Host ""
Write-Ok "Build complete!"
if (Test-Path $OutputDir) {
    $archives = Get-ChildItem -Path $OutputDir -Recurse -Include "*.zip","*.tar.gz" -File
    if ($archives) {
        Write-Info "Artifacts:"
        foreach ($a in $archives) {
            $size = [math]::Round($a.Length / 1MB, 2)
            Write-Host "  $($a.Name) (${size} MB)"
        }
    }
}
