# Read-only paper-trade status report.
# Usage: .\Get-PaperTradeStatus.ps1
[CmdletBinding()] param()
$ErrorActionPreference = 'Continue'

Write-Host ""
Write-Host "==================================================================" -ForegroundColor Cyan
$now = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
Write-Host "  ThatsMyQuant Paper Trade Status   $now local" -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan

# Worker process
$proc = Get-CimInstance Win32_Process -Filter "Name='ThatsMyQuant.Bot.exe'" | Select-Object -First 1
if ($proc) {
    $p = Get-Process -Id $proc.ProcessId
    $uptimeMin = [int]((Get-Date) - $p.StartTime).TotalMinutes
    if ($uptimeMin -lt 60) { $uptimeStr = "$uptimeMin dk" }
    elseif ($uptimeMin -lt 1440) { $uptimeStr = "{0:F1} sa" -f ($uptimeMin/60) }
    else { $uptimeStr = "{0:F1} g" -f ($uptimeMin/1440) }
    $memMB = [int]($p.WorkingSet64/1MB)
    Write-Host "Worker         : ALIVE  PID=$($proc.ProcessId)  Mem=$memMB MB  Uptime=$uptimeStr" -ForegroundColor Green
} else {
    Write-Host "Worker         : DOWN - paper trade NOT RUNNING" -ForegroundColor Red
    return
}

# CLEF parsing
$clef = "$env:LOCALAPPDATA\ThatsMyQuant\logs\quantengine-$(Get-Date -Format yyyyMMdd).clef"
if (-not (Test-Path $clef)) { Write-Host "(no CLEF for today)"; return }

$lines = Get-Content $clef
$mirrored = @()
$portfolioSnapshots = @()
$warnings = @{}
$errors = 0

foreach ($ln in $lines) {
    try {
        $j = $ln | ConvertFrom-Json
        $msg = if ($j.'@m') { $j.'@m' } else { $j.'@mt' }

        if ($msg -match 'PAIRS TRADE|FUNDING TRADE') {
            $kind = if ($msg -match '^PAIRS') { 'Pairs' } else { 'Funding' }
            $sym = if ($j.Pair) { $j.Pair } elseif ($j.Symbol) { $j.Symbol } else { '?' }
            $mirrored += [PSCustomObject]@{
                T = $j.'@t'.Substring(0, 19)
                Kind = $kind
                Symbol = $sym
                Side = $j.Side
                PnL = [decimal]$j.PnL
                Reason = $j.Reason
            }
        }

        if ($msg -match 'Paper portfolio:|Pairs paper portfolio:|Funding paper portfolio:') {
            $portfolioSnapshots += [PSCustomObject]@{
                T = $j.'@t'.Substring(11,8)
                Cash = [decimal]$j.Cash
                Equity = [decimal]$j.Equity
                Open = [int]$j.Open
                Closed = [int]$j.Closed
            }
        }

        if ($j.'@l' -in @('Warning','Error','Fatal')) {
            $key = ($msg -split '\{')[0].Trim() -replace '\s+$',''
            if (-not $warnings.ContainsKey($key)) { $warnings[$key] = 0 }
            $warnings[$key]++
            if ($j.'@l' -in @('Error','Fatal')) { $errors++ }
        }
    } catch {}
}

# Latest portfolio snapshot
$latest = $portfolioSnapshots | Select-Object -Last 1
Write-Host ""
Write-Host "PORTFOLIO" -ForegroundColor Yellow
if ($latest) {
    $cashStr = "{0,12:N2}" -f $latest.Cash
    $eqStr = "{0,12:N2}" -f $latest.Equity
    Write-Host "  Cash        : `$$cashStr"
    Write-Host "  Equity      : `$$eqStr"
    Write-Host "  Open Pos    : $($latest.Open)"
    Write-Host "  Closed Pos  : $($latest.Closed)"
    # Pull initial balance from OKX (real basis), not hardcoded 100k.
    $initialBalance = $latest.Cash  # If 0 closed positions, Cash == InitialBalance (no PnL drift yet)
    if ($latest.Closed -gt 0) {
        # When trades have closed, derive initial balance from latest seeded sync log.
        $clef = "$env:LOCALAPPDATA\ThatsMyQuant\logs\quantengine-$(Get-Date -Format yyyyMMdd).clef"
        $seed = Get-Content $clef -Tail 5000 | Where-Object { $_ -match 'seeded from real' } | Select-Object -Last 1
        if ($seed) { try { $sj = $seed | ConvertFrom-Json; if ($sj.Balance) { $initialBalance = [decimal]$sj.Balance } } catch {} }
    }
    $pnl = $latest.Equity - $initialBalance
    $pnlPct = if ($initialBalance -gt 0) { ($pnl / $initialBalance) * 100 } else { 0 }
    $pnlStr = "{0,12:N2}" -f $pnl
    $pctStr = "{0:F2}" -f $pnlPct
    $pnlColor = if ($pnl -ge 0) { 'Green' } else { 'Red' }
    Write-Host "  Total PnL   : `$$pnlStr  ($pctStr pct)" -ForegroundColor $pnlColor
} else {
    Write-Host "  (no snapshot yet)"
}

# Mirrored trades by edge
Write-Host ""
Write-Host "TRADES BY EDGE" -ForegroundColor Yellow
if ($mirrored.Count -gt 0) {
    $bySymbol = $mirrored | Group-Object Symbol
    foreach ($g in $bySymbol) {
        $wins = ($g.Group | Where-Object { $_.PnL -gt 0 }).Count
        $losses = ($g.Group | Where-Object { $_.PnL -le 0 }).Count
        $sumPnL = ($g.Group | Measure-Object -Property PnL -Sum).Sum
        $wr = if ($g.Count -gt 0) { ($wins / $g.Count) * 100 } else { 0 }
        $color = if ($sumPnL -ge 0) { 'Green' } else { 'Red' }
        $line = "  {0,-22}  N={1,3}  W/L={2}/{3,-3}  WR={4,5:F1} pct  PnL=`${5,12:N2}" -f $g.Name, $g.Count, $wins, $losses, $wr, $sumPnL
        Write-Host $line -ForegroundColor $color
    }
    Write-Host ""
    $totalWins = ($mirrored | Where-Object { $_.PnL -gt 0 }).Count
    $totalSum = ($mirrored | Measure-Object -Property PnL -Sum).Sum
    $overallWR = if ($mirrored.Count -gt 0) { ($totalWins / $mirrored.Count) * 100 } else { 0 }
    $totalLine = "  {0,-22}  N={1,3}  W/L={2}/{3,-3}  WR={4,5:F1} pct  PnL=`${5,12:N2}" -f "TOPLAM", $mirrored.Count, $totalWins, ($mirrored.Count - $totalWins), $overallWR, $totalSum
    Write-Host $totalLine -ForegroundColor Cyan
} else {
    Write-Host "  (henuz trade yok - pairs 5dk, funding 30dk interval; ilk trade beklenir)"
}

# Last 5 trades
if ($mirrored.Count -gt 0) {
    Write-Host ""
    Write-Host "LAST 5 TRADES" -ForegroundColor Yellow
    $mirrored | Select-Object -Last 5 | ForEach-Object {
        $color = if ($_.PnL -ge 0) { 'Green' } else { 'Red' }
        $line = "  {0}  {1,-7}  {2,-22}  {3,-4}  PnL=`${4,8:N2}  ({5})" -f $_.T, $_.Kind, $_.Symbol, $_.Side, $_.PnL, $_.Reason
        Write-Host $line -ForegroundColor $color
    }
}

# Warnings summary
Write-Host ""
Write-Host "HEALTH (today log)" -ForegroundColor Yellow
$totalWarn = ($warnings.Values | Measure-Object -Sum).Sum
$errColor = if ($errors -gt 0) { 'Red' } else { 'Green' }
Write-Host "  Errors      : $errors" -ForegroundColor $errColor
Write-Host "  Warnings    : $totalWarn (top reasons below)"
$warnings.GetEnumerator() | Sort-Object -Property Value -Descending | Select-Object -First 5 | ForEach-Object {
    $cnt = "{0,4}" -f $_.Value
    Write-Host "    ${cnt}x  $($_.Key)"
}

Write-Host ""
Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host ""
