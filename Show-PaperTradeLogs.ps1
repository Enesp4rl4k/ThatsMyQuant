# Live tail of the QuantEngine.Worker CLEF log with color coding + noise filter.
# Opens in its own window. Run alongside the worker to watch trades + decisions in real time.
[CmdletBinding()] param(
    [switch]$ShowAll  # Bypass filter - show ALL log lines (debug mode)
)

$ErrorActionPreference = 'Continue'
$Host.UI.RawUI.WindowTitle = "ThatsMyQuant Paper - Live Logs"

$logDir = Join-Path $env:LOCALAPPDATA "ThatsMyQuant\logs"
$today = Get-Date -Format "yyyyMMdd"
$clefPath = Join-Path $logDir "quantengine-$today.clef"

Write-Host ""
Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "  ThatsMyQuant Paper Trade - Live Log Viewer" -ForegroundColor Cyan
Write-Host "  Path: $clefPath" -ForegroundColor DarkGray
Write-Host "  Filter: ON (use -ShowAll to bypass)" -ForegroundColor DarkGray
Write-Host "  Press Ctrl+C to exit" -ForegroundColor DarkGray
Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host ""

# Wait for log file to exist (worker may still be booting)
$waitCount = 0
while (-not (Test-Path $clefPath)) {
    if ($waitCount -eq 0) { Write-Host "Waiting for worker to create log file..." -ForegroundColor Yellow }
    Start-Sleep -Seconds 1
    $waitCount = $waitCount + 1
    if (($waitCount % 10) -eq 0) { Write-Host "  ...still waiting ($waitCount sec)" -ForegroundColor DarkYellow }
    if ($waitCount -gt 120) {
        Write-Host "Worker log not appearing after 2 min - is the worker running?" -ForegroundColor Red
        Read-Host "Press Enter to exit"
        return
    }
}

# Patterns we EXCLUDE (noise)
$noisePatterns = @(
    'HTTP request', 'HTTP response',
    'Executed DbCommand',
    'Heartbeat',
    'orphaned venue order',
    'GetAccountStatusAsync'
)

# Patterns we color-code as INTERESTING
$tradePatterns = 'PAIRS TRADE|FUNDING TRADE|FUND_HYBRID|PAIR_HYBRID|mirrored.*trade'
$signalPatterns = 'Pairs diagnostics|Funding alpha .+: trades|Coint REJECT|Confluence:|WouldOpenNew|WouldClose'
$reconcilePatterns = 'RECONCILE|Reconciliation tick'
$bootPatterns = 'starting|Hosted services|seeded from real|InitialBalance updated|disabled'
$digestPatterns = 'Telegram daily digest|Telegram digest send|Realtime alert'
$alertPatterns = 'CIRCUIT BREAKER|RECONCILE GHOST|RECONCILE SIDE|ROLLBACK FAILED|Sync Stalled'

function Format-LogLine {
    param([string]$Line)
    try {
        $j = $Line | ConvertFrom-Json -ErrorAction Stop
        $msg = if ($j.'@m') { $j.'@m' } else { $j.'@mt' }
        if (-not $msg) { return $null }

        # Filter noise unless ShowAll
        if (-not $ShowAll) {
            foreach ($n in $noisePatterns) {
                if ($msg -like "*$n*") { return $null }
            }
        }

        $level = if ($j.'@l') { $j.'@l' } else { 'Info' }
        $tStr = if ($j.'@t') { $j.'@t'.Substring(11, 8) } else { (Get-Date -Format HH:mm:ss) }

        # Render structured fields inline
        $extras = ($j.PSObject.Properties | Where-Object { $_.Name -notin @('@t','@l','@m','@mt','@i','@x','@r','@tr','@sp','SourceContext','EventId','Service') } | ForEach-Object {
            $val = $_.Value
            if ($val -is [decimal] -or $val -is [double]) { $val = '{0:F4}' -f $val }
            "$($_.Name)=$val"
        }) -join ' '

        # Truncate template placeholders if real values rendered separately
        $cleanMsg = $msg -replace '\{[^}]+\}', '?'
        if ($extras) { $cleanMsg = "$cleanMsg [$extras]" }
        if ($cleanMsg.Length -gt 220) { $cleanMsg = $cleanMsg.Substring(0, 220) + '...' }

        # Pick color and tag (ASCII-only tags to avoid encoding issues)
        $color = 'White'
        $tag = '   INFO '
        if ($cleanMsg -match $alertPatterns) { $color = 'Red'; $tag = '!! ALERT' }
        elseif ($cleanMsg -match $tradePatterns) { $color = 'Green'; $tag = '$$ TRADE' }
        elseif ($cleanMsg -match $signalPatterns) { $color = 'Cyan'; $tag = '** SIG  ' }
        elseif ($cleanMsg -match $reconcilePatterns) { $color = 'Magenta'; $tag = '<> RECON' }
        elseif ($cleanMsg -match $digestPatterns) { $color = 'DarkCyan'; $tag = '>> TGM  ' }
        elseif ($cleanMsg -match $bootPatterns) { $color = 'Yellow'; $tag = '++ BOOT ' }
        elseif ($level -eq 'Error' -or $level -eq 'Fatal') { $color = 'Red'; $tag = 'XX ERR  ' }
        elseif ($level -eq 'Warning') { $color = 'DarkYellow'; $tag = '!  WARN ' }

        return @{ Color = $color; Text = "$tStr $tag $cleanMsg" }
    } catch {
        if ($ShowAll) { return @{ Color = 'DarkGray'; Text = "<RAW> $Line" } }
        return $null
    }
}

# Read existing tail (last ~5 lines) then follow new lines.
Get-Content -Path $clefPath -Tail 5 | ForEach-Object {
    $f = Format-LogLine -Line $_
    if ($f) { Write-Host $f.Text -ForegroundColor $f.Color }
}

Write-Host ""
Write-Host "--- following new entries ---" -ForegroundColor DarkGray
Write-Host ""

Get-Content -Path $clefPath -Tail 0 -Wait | ForEach-Object {
    $f = Format-LogLine -Line $_
    if ($f) { Write-Host $f.Text -ForegroundColor $f.Color }
}
