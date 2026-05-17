# Restart-Worker.ps1
# Proper TMQ Worker restart — handles process kill + relaunch with full DPAPI context.
#
# Usage:
#   1. Foreground (window stays open):   .\Restart-Worker.ps1
#   2. Background (window can close):    .\Restart-Worker.ps1 -Background
#
# Background mode uses Windows Scheduled Task wrapper so DPAPI user profile is loaded
# correctly (Start-Process spawn loses profile context → "DPAPI not supported" error).

[CmdletBinding()]
param(
    [switch]$Background
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "=== TMQ Worker Restart ===" -ForegroundColor Cyan

# Step 1: Find and stop existing worker
$existing = Get-Process -Name 'ThatsMyQuant.Bot' -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "[stop] Found ThatsMyQuant.Bot PID=$($existing.Id) (started $($existing.StartTime))" -ForegroundColor Yellow
    Stop-Process -Id $existing.Id -Force
    Start-Sleep -Seconds 2

    # Verify stopped
    $still = Get-Process -Name 'ThatsMyQuant.Bot' -ErrorAction SilentlyContinue
    if ($still) {
        Write-Host "[err] Failed to stop bot. Manual kill required: Stop-Process -Id $($still.Id) -Force" -ForegroundColor Red
        exit 1
    }
    Write-Host "[stop] Old worker stopped." -ForegroundColor Green
} else {
    Write-Host "[stop] No existing worker running." -ForegroundColor Gray
}

# Step 2: Verify config is correct
$configPath = Join-Path $PSScriptRoot "01_Services\QuantEngine.Worker\appsettings.Paper.json"
if (!(Test-Path $configPath)) {
    Write-Host "[err] Config not found: $configPath" -ForegroundColor Red
    exit 1
}
$cfg = Get-Content $configPath -Raw | ConvertFrom-Json

# Track B sanity check
$exec = $cfg.OrderExecution
if ($exec.PostOnlyMode -ne $true) { Write-Host "[warn] PostOnlyMode != true" -ForegroundColor Yellow }
if ($exec.EnableMakerRouting -ne $true) { Write-Host "[warn] EnableMakerRouting != true (Track B not active!)" -ForegroundColor Yellow }
Write-Host "[config] PostOnlyMode=$($exec.PostOnlyMode) EnableMakerRouting=$($exec.EnableMakerRouting) MakerUrgency=$($exec.MakerUrgency)" -ForegroundColor Cyan

$strategies = $cfg.EdgeLabSingleAsset.Strategies
Write-Host "[config] Strategies: $($strategies -join ', ')" -ForegroundColor Cyan

# Step 3: Start worker
$launcher = Join-Path $PSScriptRoot "Launch-Worker-Background.ps1"
if (!(Test-Path $launcher)) {
    Write-Host "[err] Launcher not found: $launcher" -ForegroundColor Red
    exit 1
}

if ($Background) {
    # Background mode: detached process via cmd.exe START so it survives parent close
    # AND inherits user profile DPAPI context (current PS session has it loaded)
    Write-Host "[run] Starting worker in BACKGROUND (window detached)..." -ForegroundColor Cyan
    $logPath = Join-Path $PSScriptRoot "logs\worker_$(Get-Date -Format 'yyyy-MM-dd_HHmmss').log"
    if (!(Test-Path (Split-Path $logPath))) { New-Item -ItemType Directory -Path (Split-Path $logPath) -Force | Out-Null }
    Write-Host "[run] Log: $logPath" -ForegroundColor Gray

    # Use Start-Process with -NoNewWindow:$false to detach. Output redirected to log.
    # NOTE: This still inherits the calling PS session's env + DPAPI context.
    $startInfo = @{
        FilePath = "powershell.exe"
        ArgumentList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $launcher)
        WorkingDirectory = $PSScriptRoot
        RedirectStandardOutput = $logPath
        RedirectStandardError = "$logPath.err"
        WindowStyle = "Hidden"
    }
    $proc = Start-Process @startInfo -PassThru
    Write-Host "[run] Started PID=$($proc.Id) (parent PS launcher; bot child spawns from this)" -ForegroundColor Green
    Write-Host "[wait] Waiting 30s for build + boot..." -ForegroundColor Gray
    Start-Sleep -Seconds 30

    $bot = Get-Process -Name 'ThatsMyQuant.Bot' -ErrorAction SilentlyContinue
    if ($bot) {
        Write-Host "[ok] Bot running PID=$($bot.Id) since $($bot.StartTime)" -ForegroundColor Green
        Write-Host "[ok] Memory: $([math]::Round($bot.WorkingSet64/1MB,1)) MB" -ForegroundColor Gray

        # Last 10 log lines
        if (Test-Path $logPath) {
            Write-Host "`n[log tail]" -ForegroundColor Cyan
            Get-Content $logPath -Tail 10
        }
    } else {
        Write-Host "[err] Bot not running. Check log: $logPath.err" -ForegroundColor Red
        if (Test-Path "$logPath.err") {
            Get-Content "$logPath.err" -Tail 20
        }
        exit 1
    }
} else {
    # Foreground mode: just call the launcher (simplest, terminal stays open)
    Write-Host "[run] Starting worker in FOREGROUND. Press Ctrl+C to stop. Close terminal kills bot." -ForegroundColor Cyan
    & $launcher
}
