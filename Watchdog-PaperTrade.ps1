# Paper-trade watchdog - runs every 1 min via Task Scheduler (ThatsMyQuant-Watchdog).
# Detects worker death / hang / silent integration failures, relaunches, and ALERTS.
#
# 2026-06-27 rewrite - fixes the defects that let the worker stay silently dead for 13 days:
#   1. Telegram alerts now read telegram_settings.json directly. The previous version read
#      $env:Telegram__BotToken, which is NEVER set in the scheduled-task context, so every
#      alert silently returned at the guard -> outages were invisible.
#   2. Relaunch now uses the canonical Launch-PaperTrade-Clean.ps1 (fresh DPAPI decrypt; the
#      task runs as parlak Interactive so CurrentUser DPAPI works). Drops the stale May-11
#      run-worker.bat that carried PLAINTEXT OKX secret+passphrase+token and could relaunch
#      a broken/mock worker.
#   3. Restart-loop escalation: if relaunch keeps failing, stop hammering and escalate.
#   4. Integration alarm: repeated edge-rejection / ERR in the live log -> alert (no restart),
#      so producer-contract drift (Malformed / WeightAsymmetry / not actionable) is surfaced
#      instead of failing closed in silence.
#   5. Watchdog self-heartbeat: writes its own liveness file every run.
#
# ASCII-only on purpose (PS 5.1 + Turkish locale mangles non-ASCII source bytes).
[CmdletBinding()] param(
    [string]$WorkDir           = "C:\Users\parlak\Downloads\ThatsMyQuant",
    [int]$HeartbeatStaleMin    = 5,
    [int]$IntegrationWindowMin = 10,
    [int]$IntegrationThreshold = 3,
    [int]$RestartLoopWindowMin = 15,
    [int]$RestartLoopMax       = 3
)

$ErrorActionPreference = 'Continue'
$stateDir = Join-Path $env:LOCALAPPDATA "ThatsMyQuant"
if (-not (Test-Path $stateDir)) { New-Item -ItemType Directory -Path $stateDir -Force | Out-Null }
$logDir = Join-Path $WorkDir "_runtime_logs"
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

$watchdogLog      = Join-Path $logDir "watchdog.log"
$selfHeartbeat    = Join-Path $stateDir "watchdog_heartbeat.txt"
$heartbeatFile    = Join-Path $stateDir "worker_heartbeat.txt"
$alertStateFile   = Join-Path $stateDir "watchdog_last_alert.txt"
$restartStateFile = Join-Path $stateDir "watchdog_restarts.json"
$workerLogDir     = Join-Path $stateDir "worker_logs"

function Write-Log($msg) {
    $line = "[" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + "] " + $msg
    $line | Add-Content -LiteralPath $watchdogLog -Encoding utf8
}

$script:TgToken = $null
$script:TgChat  = $null
$tgFile = Join-Path $WorkDir "telegram_settings.json"
if (Test-Path $tgFile) {
    try {
        $tg = Get-Content $tgFile -Raw | ConvertFrom-Json
        $script:TgToken = [string]$tg.BotToken
        $script:TgChat  = [string]$tg.ChatId
    } catch {}
}
if (-not $script:TgToken -and $env:Telegram__BotToken) {
    $script:TgToken = $env:Telegram__BotToken
    $script:TgChat  = $env:Telegram__ChatId
}

function Send-Telegram {
    param([string]$Text, [string]$DebounceKey, [int]$DebounceMin = 5)
    if (-not $script:TgToken -or -not $script:TgChat) {
        Write-Log ("[TG] no token/chat - alert dropped: " + $Text)
        return
    }
    if ($DebounceKey -and (Test-Path $alertStateFile)) {
        try {
            foreach ($line in Get-Content $alertStateFile) {
                $p = $line -split '\|', 2
                if ($p[1] -eq $DebounceKey -and ((Get-Date) - [DateTime]::Parse($p[0])).TotalMinutes -lt $DebounceMin) {
                    Write-Log ("[TG] suppressed (debounce " + $DebounceMin + "min key=" + $DebounceKey + ")")
                    return
                }
            }
        } catch {}
    }
    try {
        $payload = @{ chat_id = $script:TgChat; text = $Text } | ConvertTo-Json -Compress
        $uri = "https://api.telegram.org/bot" + $script:TgToken + "/sendMessage"
        $null = Invoke-RestMethod -Uri $uri -Method Post -Body $payload -ContentType 'application/json' -TimeoutSec 8
        Write-Log ("[TG] sent: " + ($Text -replace '\r?\n', ' '))
        if ($DebounceKey) {
            $keep = @()
            if (Test-Path $alertStateFile) {
                $keep = @(Get-Content $alertStateFile | Where-Object { ($_ -split '\|', 2)[1] -ne $DebounceKey })
            }
            $entry = (Get-Date -Format o) + '|' + $DebounceKey
            $out = @($keep) + $entry
            $out | Set-Content -LiteralPath $alertStateFile -Encoding utf8
        }
    } catch {
        Write-Log ("[TG] send failed: " + $_)
    }
}

# Self-heartbeat every run so a dead watchdog is detectable.
$selfHbLine = (Get-Date -Format o) + '|pid=' + $PID
$selfHbLine | Set-Content -LiteralPath $selfHeartbeat -Encoding utf8 -Force

function Get-RecentRestarts {
    if (-not (Test-Path $restartStateFile)) { return @() }
    try { $a = Get-Content $restartStateFile -Raw | ConvertFrom-Json } catch { return @() }
    $cut = (Get-Date).AddMinutes(-$RestartLoopWindowMin)
    return @($a | Where-Object { [DateTime]::Parse($_) -gt $cut })
}
function Record-Restart {
    $list = @(Get-RecentRestarts) + (Get-Date -Format o)
    $list | ConvertTo-Json -Compress | Set-Content -LiteralPath $restartStateFile -Encoding utf8 -Force
}

function Restart-Worker {
    param([string]$Reason)
    $recent = @(Get-RecentRestarts)
    if ($recent.Count -ge $RestartLoopMax) {
        Write-Log ("RESTART LOOP: " + $recent.Count + " restarts in " + $RestartLoopWindowMin + "min - backing off, NOT relaunching (" + $Reason + ")")
        $now = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
        $msg = "[CRITICAL] Watchdog: RESTART LOOP" + "`nWorker failed to stay up (" + $recent.Count + "x in " + $RestartLoopWindowMin + "min)." + "`nReason: " + $Reason + "`nAuto-restart paused - MANUAL INTERVENTION needed." + "`n" + $now
        Send-Telegram -Text $msg -DebounceKey 'restartloop' -DebounceMin 30
        return
    }
    $launcher = Join-Path $WorkDir "Launch-PaperTrade-Clean.ps1"
    if (-not (Test-Path $launcher)) {
        Write-Log ("ABORT: launcher missing at " + $launcher)
        Send-Telegram -Text "[CRITICAL] Watchdog: launcher MISSING, cannot relaunch worker." -DebounceKey 'nolauncher' -DebounceMin 30
        return
    }
    if (-not (Test-Path $workerLogDir)) { New-Item -ItemType Directory -Path $workerLogDir -Force | Out-Null }
    $out = Join-Path $workerLogDir ("worker_" + (Get-Date -Format yyyyMMdd_HHmmss) + ".out.log")
    try {
        $p = Start-Process powershell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$launcher,'-SkipBuild') -RedirectStandardOutput $out -RedirectStandardError ($out + ".err") -WindowStyle Hidden -PassThru
        "$out" | Set-Content -LiteralPath (Join-Path $WorkDir ".last_worker_log.txt") -Encoding utf8 -Force
        Record-Restart
        Write-Log ("RELAUNCHED via launcher (host PID=" + $p.Id + ") reason='" + $Reason + "' log=" + $out)
        $now = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
        $msg = "[WARN] Watchdog: worker restarted" + "`nReason: " + $Reason + "`nLog: " + (Split-Path $out -Leaf) + "`n" + $now
        Send-Telegram -Text $msg -DebounceKey 'restart' -DebounceMin 5
    } catch {
        Write-Log ("RELAUNCH FAILED: " + $_)
        $now = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
        Send-Telegram -Text ("[CRITICAL] Watchdog: RELAUNCH FAILED" + "`nReason: " + $Reason + "`nError: " + $_ + "`n" + $now) -DebounceKey 'relaunchfail' -DebounceMin 15
    }
}

# 1. Process + heartbeat health
$proc = Get-CimInstance Win32_Process -Filter "Name='ThatsMyQuant.Bot.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1
$alive = $null -ne $proc
$reason = $null

if (-not $alive) {
    $reason = "Worker process not running"
} else {
    if (Test-Path $heartbeatFile) {
        $hbAge = ((Get-Date) - (Get-Item $heartbeatFile).LastWriteTime).TotalMinutes
        if ($hbAge -gt $HeartbeatStaleMin) {
            $reason = "Heartbeat stale (" + ("{0:F1}" -f $hbAge) + "min, limit " + $HeartbeatStaleMin + "min)"
            Write-Log ("WORKER HUNG: PID=" + $proc.ProcessId + " " + $reason + " - killing.")
            try { Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop } catch { Write-Log ("Stop-Process failed: " + $_) }
            $alive = $false
            Start-Sleep -Seconds 2
        }
    }
    if ($alive) {
        try {
            $pp = Get-Process -Id $proc.ProcessId -ErrorAction Stop
            if (-not $pp.Responding -and ((Get-Date) - $pp.StartTime).TotalSeconds -gt 120) {
                $reason = "Process not responding"
                Write-Log ("WORKER HUNG: PID=" + $pp.Id + " not responding - killing.")
                try { Stop-Process -Id $pp.Id -Force -ErrorAction Stop } catch { Write-Log ("Stop-Process failed: " + $_) }
                $alive = $false
                Start-Sleep -Seconds 2
            }
        } catch {}
    }
}

if (-not $alive) {
    Restart-Worker -Reason $reason
    exit 0
}

# 2. Integration health - scan the live log for silent edge failures (the class of all 3
# bugs found 2026-06-27: producer-contract drift the reader rejects every cycle).
try {
    $newest = Get-ChildItem $workerLogDir -Filter 'worker_*.out.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newest) {
        $cut = (Get-Date).AddMinutes(-$IntegrationWindowMin)
        $tail = Get-Content $newest.FullName -Tail 400 -ErrorAction SilentlyContinue
        $rx = 'Malformed|WeightAsymmetry|not actionable|UNIQUE constraint failed|could not be converted|\bERR\b'
        $rxSkip = 'Telegram|DPAPI|PlatformNotSupported|OkxClockSync'
        $hits = 0
        foreach ($line in $tail) {
            if ($line -match $rx -and $line -notmatch $rxSkip) {
                if ($line -match '^\[(\d{2}):(\d{2}):(\d{2})') {
                    $t = (Get-Date).Date.AddHours([int]$Matches[1]).AddMinutes([int]$Matches[2]).AddSeconds([int]$Matches[3])
                    if ($t -lt $cut) { continue }
                }
                $hits++
            }
        }
        if ($hits -ge $IntegrationThreshold) {
            Write-Log ("INTEGRATION ALARM: " + $hits + " rejection/error lines in last " + $IntegrationWindowMin + "min of " + $newest.Name)
            $sample = ($tail | Where-Object { $_ -match $rx -and $_ -notmatch $rxSkip } | Select-Object -Last 1)
            if ($sample) { $sample = $sample -replace '[*_]', ' '; if ($sample.Length -gt 180) { $sample = $sample.Substring(0, 180) } }
            $now = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
            $msg = "[WARN] Watchdog: integration alarm" + "`n" + $hits + " rejection/error lines in " + $IntegrationWindowMin + "min (worker UP, no restart)." + "`nLatest: " + $sample + "`n" + $now
            Send-Telegram -Text $msg -DebounceKey 'integration' -DebounceMin 30
        }
    }
} catch { Write-Log ("Integration scan failed: " + $_) }

exit 0
