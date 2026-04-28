# Paper-trade watchdog. Runs every 1 minute via Task Scheduler. If the worker process is
# missing OR the most recent CLEF heartbeat is older than 3 minutes (silent death), relaunches.
# Idempotent: does nothing when worker is healthy.
[CmdletBinding()] param()

$ErrorActionPreference = 'Continue'
$workDir = "C:\Users\parlak\Downloads\ThatsMyQuant"
$logDir = Join-Path $workDir "_runtime_logs"
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$watchdogLog = Join-Path $logDir "watchdog.log"

function Write-Log($msg) {
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $msg"
    $line | Add-Content -LiteralPath $watchdogLog -Encoding utf8
}

# 1. Process check
$proc = Get-CimInstance Win32_Process -Filter "Name='ThatsMyQuant.Bot.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1
$processAlive = $null -ne $proc

# 2. Heartbeat check (only when process exists)
$heartbeatStale = $false
if ($processAlive) {
    $clef = "$env:LOCALAPPDATA\ThatsMyQuant\logs\quantengine-$(Get-Date -Format yyyyMMdd).clef"
    if (Test-Path $clef) {
        $lastWrite = (Get-Item $clef).LastWriteTime
        $ageMinutes = ((Get-Date) - $lastWrite).TotalMinutes
        if ($ageMinutes -gt 3) {
            $heartbeatStale = $true
            Write-Log "WORKER STALE: PID=$($proc.ProcessId) lastLog=${ageMinutes:F1}min ago. Killing + relaunching."
            try { Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop } catch { Write-Log "Stop-Process failed: $_" }
            $processAlive = $false
            Start-Sleep -Seconds 2
        }
    }
}

if ($processAlive) {
    # Healthy — exit silently (no log spam)
    exit 0
}

# 3. Relaunch via the embedded-credentials batch file. The original launcher PS1 does
# DPAPI decryption — fine when run interactively, but unreliable in hidden/scheduled
# context where DPAPI throws "PlatformNotSupportedException". The batch file at
# _runtime_logs\run-worker.bat already has credentials baked in by an interactive
# launch and is safe to call repeatedly from a hidden cmd.exe.
$batch = Join-Path $logDir "run-worker.bat"
if (-not (Test-Path $batch)) {
    # Fallback: try the legacy launcher (will work if scheduled task user has DPAPI access).
    Write-Log "BATCH MISSING at $batch — falling back to Launch-PaperTrade-Clean.ps1"
    $launcher = Join-Path $workDir "Launch-PaperTrade-Clean.ps1"
    if (-not (Test-Path $launcher)) { Write-Log "ABORT: both batch and launcher missing"; exit 1 }
    $launchArgs = @("-NoProfile","-ExecutionPolicy","Bypass","-WindowStyle","Hidden","-File",$launcher)
    try {
        $stdout = Join-Path $logDir "paper-stdout.log"
        $stderr = Join-Path $logDir "paper-stderr.log"
        foreach ($f in @($stdout, $stderr)) { if (Test-Path $f) { try { Remove-Item $f -Force -ErrorAction Stop } catch {} } }
        $newProc = Start-Process -FilePath "powershell.exe" -ArgumentList $launchArgs -WorkingDirectory $workDir -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
        Write-Log "Fallback launched PID=$($newProc.Id)"
    } catch { Write-Log "Fallback launch failed: $_"; exit 1 }
    exit 0
}

Write-Log "WORKER DOWN — relaunching via run-worker.bat"
$stdout = Join-Path $logDir "paper-stdout.log"
$stderr = Join-Path $logDir "paper-stderr.log"
foreach ($f in @($stdout, $stderr)) { if (Test-Path $f) { try { Remove-Item $f -Force -ErrorAction Stop } catch {} } }
try {
    $newProc = Start-Process -FilePath "cmd.exe" -ArgumentList @("/c", $batch) -WorkingDirectory $workDir -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
    Write-Log "Launched cmd PID=$($newProc.Id) (batch=$batch)"
} catch {
    Write-Log "Batch launch failed: $_"
    exit 1
}
exit 0
