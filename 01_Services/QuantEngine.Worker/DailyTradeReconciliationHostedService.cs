using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// [2026-05-14 unattended hardening] Daily reconciliation: count + volume + fee match
/// between local DB closed trades and OKX fills-history for the prior 24h UTC window.
///
/// Why: ledger drift is the silent killer in unattended periods. A local Trade gets
/// committed to DB but the OKX fill never happened (silent reject). Or OKX records a
/// fill we never observed (WebSocket drop + no replay match). Either way the operator
/// returning after 15 days needs ONE summary metric per day saying "ledgers agree" or
/// "ledgers diverge by N trades / $X volume — investigate".
///
/// What: each UTC midnight (configurable), pull
///   - local closed Trades whose ExitTime ∈ [yesterday-UTC-00:00, today-UTC-00:00]
///   - OKX fills-history in the same window
/// Compare:
///   - count tolerance: |OKX_fills - 2*DB_trades| ≤ 4 (each trade = entry+exit; small drift OK)
///   - volume tolerance: |Σ_OKX_qty - Σ_DB_qty * 2| / Σ_DB_qty ≤ 5%
///   - fee tolerance: |Σ_OKX_fee - Σ_DB_fee| / Σ_DB_fee ≤ 10% (when DB fee available)
/// Telegram alert if ANY tolerance breached. Always logs the table for ops grep.
///
/// Read-only — never mutates DB or portfolio state. Failure is best-effort logged + alerted.
/// </summary>
public sealed class DailyTradeReconciliationHostedService : BackgroundService
{
    private readonly ILogger<DailyTradeReconciliationHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PaperPortfolio _portfolio;
    private readonly IRealtimeAlerter? _alerter;

    // UTC time-of-day to run. Default 23:50 UTC — late enough that all closes for the
    // window have settled, early enough to alert before the next day starts.
    private readonly TimeOnly _runAtUtc = new(23, 50);

    // Tolerances. Conservative defaults; tighten after first 30d of clean reconciliations.
    private const int CountToleranceFills = 4;            // ± fills (≈2 incomplete entry+exit pairs)
    private const decimal VolumeTolerancePct = 0.05m;     // 5% volume drift
    private const decimal FeeTolerancePct = 0.10m;        // 10% fee drift

    private DateOnly? _lastRunDateUtc;

    public DailyTradeReconciliationHostedService(
        ILogger<DailyTradeReconciliationHostedService> logger,
        IServiceProvider serviceProvider,
        PaperPortfolio portfolio,
        IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _portfolio = portfolio;
        _alerter = alerter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "DailyTradeReconciliation starting. RunAt={RunAt} UTC daily. Tolerances: count=±{C}, volume={V:P0}, fee={F:P0}.",
            _runAtUtc, CountToleranceFills, VolumeTolerancePct, FeeTolerancePct);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            do
            {
                try
                {
                    var nowUtc = DateTime.UtcNow;
                    var todayUtc = DateOnly.FromDateTime(nowUtc);
                    var nowTime = TimeOnly.FromDateTime(nowUtc);

                    // 5-min window once per UTC day.
                    var inWindow = nowTime >= _runAtUtc && nowTime < _runAtUtc.AddMinutes(5);
                    var notRunToday = _lastRunDateUtc is null || _lastRunDateUtc.Value < todayUtc;
                    if (inWindow && notRunToday)
                    {
                        await RunOnceAsync(nowUtc, stoppingToken).ConfigureAwait(false);
                        _lastRunDateUtc = todayUtc;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DailyTradeReconciliation tick failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        // Window: [today-UTC-00:00 - 24h, today-UTC-00:00]. We run at 23:50 so this covers
        // the bulk of "today" plus the trailing edge yesterday. Small overlap is OK.
        var windowEnd = nowUtc.Date;       // today UTC midnight
        var windowStart = windowEnd.AddDays(-1);

        // ── DB side ──
        List<PaperPosition> dbClosed;
        lock (_portfolio)
        {
            dbClosed = _portfolio.ClosedPositions
                .Where(p => p.ExitTime.HasValue
                            && p.ExitTime.Value >= windowStart
                            && p.ExitTime.Value < windowEnd)
                .ToList();
        }

        var dbCount = dbClosed.Count;
        var dbVolume = dbClosed.Sum(p => p.Quantity * p.EntryPrice);  // entry-leg notional × 2 ≈ round-trip
        var dbFeesReal = dbClosed.Where(p => p.RealFeePaid.HasValue).Sum(p => p.RealFeePaid!.Value);
        var dbFeesAvailable = dbClosed.Count(p => p.RealFeePaid.HasValue);

        // ── OKX side ──
        IReadOnlyList<ExchangeFillSnapshot> okxFills;
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var exchange = scope.ServiceProvider.GetService<IExchangeService>();
            if (exchange is not IExchangePositionReader reader)
            {
                _logger.LogWarning("DailyTradeReconciliation: IExchangePositionReader not available. Skipping today.");
                return;
            }
            okxFills = await reader.GetFillsHistoryAsync(windowStart, windowEnd, instType: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DailyTradeReconciliation: OKX fills-history fetch failed.");
            return;
        }

        var okxCount = okxFills.Count;
        var okxVolume = okxFills.Sum(f => f.Quantity * f.Price);
        var okxFees = okxFills.Sum(f => f.FeePaid);

        // ── Compare ──
        // Each trade = entry + exit fill ⇒ expected OKX fills ≈ 2 × DB trades.
        var expectedFills = dbCount * 2;
        var fillCountDelta = okxCount - expectedFills;
        var fillCountBreach = Math.Abs(fillCountDelta) > CountToleranceFills;

        // Volume similarly doubled.
        var expectedVolume = dbVolume * 2m;
        var volumeAbsDelta = Math.Abs(okxVolume - expectedVolume);
        var volumeRelDelta = expectedVolume > 0m ? volumeAbsDelta / expectedVolume : 0m;
        var volumeBreach = expectedVolume > 0m && volumeRelDelta > VolumeTolerancePct;

        // Fee compare only meaningful when DB has captured RealFeePaid for ≥80% of closed trades.
        var feeReady = dbCount > 0 && dbFeesAvailable * 1.0 / dbCount >= 0.8;
        var feeAbsDelta = Math.Abs(okxFees - dbFeesReal);
        var feeRelDelta = dbFeesReal > 0m ? feeAbsDelta / dbFeesReal : 0m;
        var feeBreach = feeReady && dbFeesReal > 0m && feeRelDelta > FeeTolerancePct;

        var anyBreach = fillCountBreach || volumeBreach || feeBreach;

        _logger.LogWarning(
            "RECON_24H | Window={Start:yyyy-MM-dd HH:mm}-{End:HH:mm} UTC | DB trades={DbN} vol=${DbV:N2} feeReal=${DbFee:N2}(N={DbFeeN}) | OKX fills={OkxN} vol=${OkxV:N2} fee=${OkxFee:N2} | Δfills={DF} (tol±{TolF}) ΔvolPct={DVp:P1} (tol{TVp:P0}) ΔfeePct={DFp:P1} (tol{TFp:P0}) {Verdict}",
            windowStart, windowEnd, dbCount, dbVolume, dbFeesReal, dbFeesAvailable, okxCount, okxVolume, okxFees,
            fillCountDelta, CountToleranceFills, volumeRelDelta, VolumeTolerancePct, feeRelDelta, FeeTolerancePct,
            anyBreach ? "DRIFT-DETECTED" : "OK");

        if (anyBreach && _alerter is not null)
        {
            var lines = new List<string>();
            if (fillCountBreach) lines.Add($"  Fill count drift: OKX={okxCount} vs expected={expectedFills} (Δ{fillCountDelta:+0;-0;0}, tol±{CountToleranceFills})");
            if (volumeBreach)    lines.Add($"  Volume drift: OKX=${okxVolume:N0} vs expected=${expectedVolume:N0} ({volumeRelDelta:P1}, tol {VolumeTolerancePct:P0})");
            if (feeBreach)       lines.Add($"  Fee drift: OKX=${okxFees:N2} vs DB=${dbFeesReal:N2} ({feeRelDelta:P1}, tol {FeeTolerancePct:P0})");

            try
            {
                await _alerter.SendAsync(
                    key: "daily_recon_drift",
                    severity: AlertSeverity.Warning,
                    title: "⚠️ Daily reconciliation: ledger drift detected",
                    body:
                        $"Window: {windowStart:yyyy-MM-dd} UTC (24h)\n\n" +
                        string.Join("\n", lines) + "\n\n" +
                        "Implication: local DB and OKX fills-history disagree. Possible causes:\n" +
                        "  • WebSocket dropped fill (replay missed it)\n" +
                        "  • Cancel-verify failed → orphan venue order filled silently\n" +
                        "  • Clock skew between local and venue (window edge effect)\n\n" +
                        "Action: check OKX UI Order History for the window and grep worker log for OKX_ORPHAN_SUSPECT or Execution sync errors.",
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DailyTradeReconciliation: Telegram alert dispatch failed.");
            }
        }
    }
}
