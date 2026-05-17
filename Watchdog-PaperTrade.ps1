# Paper-trade watchdog. Runs every 1 minute via Task Scheduler. If the worker process is
# missing OR the worker_heartbeat.txt file is older than 5 minutes (silent death),
# OR the process is Not Responding for >2min, kills + relaunches.
# Idempotent: does nothing when worker is healthy.
#
# 2026-05-14 unattended hardening: added worker_heartbeat.txt as primary signal
# (HB hosted service writes it every 60s), Telegram alert on every restart, and
# Not Responding detection. CLEF log heartbeat retained as fallback.
[CmdletBinding()] param()

$ErrorActionPreference = 'Continue'
$workDir = "C:\Users\parlak\Downloads\ThatsMyQuant"
$logDir = Join-Path $workDir "_runtime_logs"
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$watchdogLog = Join-Path $logDir "watchdog.log"
$stateDir = Join-Path $env:LOCALAPPDATA "ThatsMyQuant"
$heartbeatFile = Join-Path $stateDir "worker_heartbeat.txt"

# Telegram debounce: max 1 alert per 5 min for the same reason (Task Scheduler runs every
# 1 min, so without debounce a single crash → 5+ alerts during recovery).
$telegramDebounceFile = Join-Path $stateDir "watchdog_last_alert.txt"

function Write-Log($msg) {
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $msg"
    $line | Add-Content -LiteralPath $watchdogLog -Encoding utf8
}

function Send-TelegramAlert {
    param([string]$Reason)
    $token = $env:Telegram__BotToken
    $chatId = $env:Telegram__ChatId
    if (-not $token -or -not $chatId) { return }

    # Debounce: skip if same reason fired within 5 min
    if (Test-Path $telegramDebounceFile) {
        try {
            $last = Get-Content $telegramDebounceFile -ErrorAction Stop
            $lastTime = [DateTime]::Parse(($last -split '\|')[0])
            $lastReason = ($last -split '\|')[1]
            if ($lastReason -eq $Reason -and ((Get-Date) - $lastTime).TotalMinutes -lt 5) {
                Write-Log "[Telegram] suppressed (debounce 5min for reason=$Reason)"
                return
            }
        } catch {}
    }

    try {
        $body = "*Watchdog: worker restarted*`nReason: $Reason`nTime: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
        $payload = @{ chat_id = $chatId; text = $body; parse_mode = 'Markdown' } | ConvertTo-Json -Compress
        $uri = "https://api.telegram.org/bot$token/sendMessage"
        $null = Invoke-RestMethod -Uri $uri -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 8
        Set-Content -LiteralPath $telegramDebounceFile -Value "$(Get-Date -Format o)|$Reason" -Encoding utf8 -Force
        Write-Log "[Telegram] alert sent (reason=$Reason)"
    } catch {
        Write-Log "[Telegram] alert failed: $_"
    }
}

# 1. Process check
$proc = Get-CimInstance Win32_Process -Filter "Name='ThatsMyQuant.Bot.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1
$processAlive = $null -ne $proc
$reason = $null

# 2. Heartbeat check — PRIMARY: worker_heartbeat.txt (written by HB hosted service every 60s)
if ($processAlive) {
    if (Test-Path $heartbeatFile) {
        $hbAge = ((Get-Date) - (Get-Item $heartbeatFile).LastWriteTime).TotalMinutes
        if ($hbAge -gt 5) {
            Write-Log "WORKER STALE: PID=$($proc.ProcessId) worker_heartbeat.txt age=${hbAge:F1}min (>5min). Killing + relaunching."
            try { Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop } catch { Write-Log "Stop-Process failed: $_" }
            $processAlive = $false
            $reason = "Heartbeat stale (${hbAge:F1}min old)"
            Start-Sleep -Seconds 2
        }
    } else {
        # HB file doesn't exist yet — could be fresh boot. Fall back to CLEF check.
        $clef = "$env:LOCALAPPDATA\ThatsMyQuant\logs\quantengine-$(Get-Date -Format yyyyMMdd).clef"
        if (Test-Path $clef) {
            $clefAge = ((Get-Date) - (Get-Item $clef).LastWriteTime).TotalMinutes
            if ($clefAge -gt 5) {
                Write-Log "WORKER STALE (CLEF fallback): PID=$($proc.ProcessId) lastLog=${clefAge:F1}min ago."
                try { Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop } catch { Write-Log "Stop-Process failed: $_" }
                $processAlive = $false
                $reason = "CLEF log stale (${clefAge:F1}min old)"
                Start-Sleep -Seconds 2
            }
        }
    }
}

# 3. Not Responding check — process exists but Windows message pump is hung
if ($processAlive) {
    try {
        $p = Get-Process -Id $proc.ProcessId -ErrorAction Stop
        $uptimeSec = ((Get-Date) - $p.StartTime).TotalSeconds
        if (-not $p.Responding -and $uptimeSec -gt 120) {
            Write-Log "WORKER HUNG: PID=$($p.Id) not responding (uptime=${uptimeSec}s). Killing."
            try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { Write-Log "Stop-Process failed: $_" }
            $processAlive = $false
            $reason = "Process not responding"
            Start-Sleep -Seconds 2
        }
    } catch {}
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

if (-not $reason) { $reason = "Worker process not running" }
Write-Log "WORKER DOWN ($reason) — relaunching via run-worker.bat"
$stdout = Join-Path $logDir "paper-stdout.log"
$stderr = Join-Path $logDir "paper-stderr.log"
foreach ($f in @($stdout, $stderr)) { if (Test-Path $f) { try { Remove-Item $f -Force -ErrorAction Stop } catch {} } }
try {
    $newProc = Start-Process -FilePath "cmd.exe" -ArgumentList @("/c", $batch) -WorkingDirectory $workDir -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
    Write-Log "Launched cmd PID=$($newProc.Id) (batch=$batch)"
    Send-TelegramAlert -Reason $reason
} catch {
    Write-Log "Batch launch failed: $_"
    Send-TelegramAlert -Reason "$reason + LAUNCH FAILED: $_"
    exit 1
}
exit 0
