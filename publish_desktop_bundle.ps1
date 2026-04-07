$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$outRoot = Join-Path $root "dist\desktop-bundle"
$stagingRoot = Join-Path $root "dist\desktop-bundle.staging"
$runtime = "win-x64"
$configuration = "Release"

$projects = @(
    @{ Name = "Gateway.Api"; Path = "01_Services/Gateway.Api/ThatsMyQuant.Gateway.Api.csproj" },
    @{ Name = "Listener.Api"; Path = "01_Services/Listener.Api/Listener.Api.csproj" },
    @{ Name = "Execution.Worker"; Path = "01_Services/Execution.Worker/Execution.Worker.csproj" },
    @{ Name = "QuantEngine.Worker"; Path = "01_Services/QuantEngine.Worker/ThatsMyQuant.Bot.csproj" },
    @{ Name = "Dashboard"; Path = "04_UI/ThatsMyQuant.Dashboard/ThatsMyQuant.Dashboard.csproj" },
    @{ Name = "Launcher"; Path = "04_UI/ThatsMyQuant.Launcher/ThatsMyQuant.Launcher.csproj" }
)

function Stop-BundleProcesses {
    $targets = Get-CimInstance Win32_Process |
        Where-Object {
            $_.ExecutablePath -like "*desktop-bundle*" -or
            $_.CommandLine -like "*desktop-bundle*"
        }

    foreach ($process in $targets) {
        try {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
        }
        catch {
            Write-Warning "Could not stop process $($process.ProcessId): $($_.Exception.Message)"
        }
    }
}

Stop-BundleProcesses
Start-Sleep -Milliseconds 500

if (Test-Path $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $stagingRoot | Out-Null

foreach ($project in $projects) {
    $targetDir = Join-Path $stagingRoot $project.Name
    Write-Host "Publishing $($project.Name)..." -ForegroundColor Cyan
    dotnet publish (Join-Path $root $project.Path) `
        -c $configuration `
        -r $runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $targetDir
}

$startScriptPath = Join-Path $stagingRoot "Start-ThatsMyQuant.ps1"
@"
`$launcher = Join-Path `$PSScriptRoot "Launcher\ThatsMyQuant.Launcher.exe"
if (-not (Test-Path `$launcher)) {
    Write-Error "Launcher executable not found: `$launcher"
    exit 1
}

Start-Process -FilePath `$launcher
"@ | Set-Content -Path $startScriptPath -Encoding UTF8

$readmePath = Join-Path $stagingRoot "README.txt"
@"
ThatsMyQuant Desktop Bundle
===========================

Recommended start method:
  1. Run Start-ThatsMyQuant.ps1
  2. Or open Launcher\ThatsMyQuant.Launcher.exe directly

Bundle layout:
  Gateway.Api\
  Listener.Api\
  Execution.Worker\
  QuantEngine.Worker\
  Dashboard\
  Launcher\

The launcher manages the background services and opens the dashboard on demand.
"@ | Set-Content -Path $readmePath -Encoding UTF8

Write-Host ""
Write-Host "Desktop bundle is ready:" -ForegroundColor Green
Write-Host "  $outRoot" -ForegroundColor Yellow
Write-Host ""
Write-Host "Start the system with:" -ForegroundColor Green
Write-Host "  $outRoot\Start-ThatsMyQuant.ps1" -ForegroundColor Yellow

if (Test-Path $outRoot) {
    $backupRoot = "$outRoot.previous"
    if (Test-Path $backupRoot) {
        Remove-Item -LiteralPath $backupRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Move-Item -LiteralPath $outRoot -Destination $backupRoot -Force
}

Move-Item -LiteralPath $stagingRoot -Destination $outRoot -Force
