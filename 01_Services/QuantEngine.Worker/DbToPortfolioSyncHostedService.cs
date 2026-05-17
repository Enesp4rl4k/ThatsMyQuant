using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// [2026-05-15 ledger drift fix — Option C, revised after diagnostics]
///
/// PROBLEM: EdgeLab strategies route through <c>OkxExecutionService.ExecuteOrderAsync</c>
/// which RETURNS a Trade object but NEVER persists it to DB (the QuantEngine.Worker
/// project doesn't even register <c>ITradeRepository</c>). And the paper-only
/// <see cref="PaperPortfolio"/> in-memory book is only updated by paper-mode executors
/// (FundingHybrid, PairsHybrid). Net result: an EdgeLab fill creates a real OKX
/// position with NO local representation anywhere — neither in DB nor PaperPortfolio.
/// Heartbeat says "Open=0" while OKX shows 5+ live positions. Operator-blind.
///
/// SOLUTION (this service): every 60s, call <see cref="IExchangePositionReader.GetOpenPositionsAsync"/>
/// to read OKX-side open positions directly, project each into a <see cref="PaperPosition"/>
/// mirror tagged with <c>ExecutionMode="LIVE_OKX"</c>, and atomically replace the
/// LIVE_OKX-tagged subset of <see cref="PaperPortfolio.OpenPositions"/>. Paper-only
/// entries (FundingHybrid mirrors etc.) are LEFT UNTOUCHED.
///
/// Read-only mirror — never modifies OKX. Failure is logged + retried next tick.
///
/// Architectural note: this is the PRAGMATIC fix. The proper fix is to make
/// OkxExecutionService persist Trade rows to DB on every fill (currently only
/// returns the object), then have ALL monitors read from DB Trades as the canonical
/// source. Track in 8-day-sprint backlog as Sprint-Post-Vacation #1.
///
/// Closed positions: OKX positions API only returns OPEN. Closed-position history
/// requires GetFillsHistoryAsync which is paginated + slower. For unattended-period
/// observability we need OPEN visibility (drawdown, kill-switch, dashboard); closed
/// history is captured by EdgeLabReconciliationLedger + DailyTradeReconciliation.
/// </summary>
public sealed class DbToPortfolioSyncHostedService : BackgroundService
{
    /// <summary>Sentinel value for PaperPosition.ExecutionMode that marks rows
    /// hydrated from OKX by this service (vs paper-mode entries written by FundingHybrid
    /// etc.). Distinct so we can replace ONLY this subset on each sync tick without
    /// clobbering paper-only history.</summary>
    public const string OkxMirrorMarker = "LIVE_OKX";

    private readonly ILogger<DbToPortfolioSyncHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PaperPortfolio _portfolio;

    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ClosedHistoryInterval = TimeSpan.FromMinutes(5);  // closed history doesn't need 60s refresh
    private static readonly TimeSpan ClosedHistoryLookback = TimeSpan.FromDays(7);

    private DateTime _lastClosedHistorySync = DateTime.MinValue;

    public DbToPortfolioSyncHostedService(
        ILogger<DbToPortfolioSyncHostedService> logger,
        IServiceProvider serviceProvider,
        PaperPortfolio portfolio)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _portfolio = portfolio;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogWarning(
            "DbToPortfolioSyncHostedService starting. Mirror OKX OpenPositions → PaperPortfolio every {S}s. Marker='{Marker}'.",
            SyncInterval.TotalSeconds, OkxMirrorMarker);

        // 30s grace for boot — let exchange service / WebSocket settle.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(SyncInterval);
        try
        {
            do
            {
                try { await SyncOnceAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "DbToPortfolioSync tick failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        // Pull fresh OKX positions + a way to read current marks OFF the lock.
        IReadOnlyList<ExchangePositionSnapshot> okxPositions;
        IReadOnlyList<ClosedPositionSnapshot>? okxClosed = null;
        Func<string, decimal>? markLookup = null;
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var exchange = scope.ServiceProvider.GetService<IExchangeService>();
            if (exchange is not IExchangePositionReader reader)
            {
                _logger.LogDebug("DbToPortfolioSync: exchange does not implement IExchangePositionReader. Skipping.");
                return;
            }
            okxPositions = await reader.GetOpenPositionsAsync(ct).ConfigureAwait(false);

            // [2026-05-16] Also mirror CLOSED positions every 5 min — closed history doesn't
            // change every 60s and the endpoint is heavier. Without this, monitors that read
            // PaperPortfolio.ClosedPositions (digest, TCA, ShadowAudit, PromotionGate) only
            // ever see stale paper-mode entries and miss every EdgeLab live close.
            if (DateTime.UtcNow - _lastClosedHistorySync >= ClosedHistoryInterval)
            {
                try
                {
                    var since = DateTime.UtcNow - ClosedHistoryLookback;
                    okxClosed = await reader.GetClosedPositionsHistoryAsync(since, DateTime.UtcNow, ct).ConfigureAwait(false);
                    _lastClosedHistorySync = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DbToPortfolioSync: GetClosedPositionsHistoryAsync failed; will retry next 5min.");
                }
            }

            // [2026-05-15] Pull mark prices from the cached OKX order-book snapshots
            // (populated every WebSocket tick). Using mid = (bestBid + bestAsk) / 2 gives
            // a fair-value estimate without an extra REST call. Falls back to entry price
            // when the book is missing/stale (UnrealizedPnL = 0 in that case — safer than
            // a wrong number).
            if (exchange is ThatsMyQuant.Infrastructure.Services.OkxExecutionService okxExec)
            {
                markLookup = sym =>
                {
                    if (okxExec.TryGetLatestOrderBook(sym, out var book)
                        && book.Bids.Length > 0 && book.Asks.Length > 0
                        && book.Bids[0].Price > 0m && book.Asks[0].Price > 0m)
                    {
                        return (book.Bids[0].Price + book.Asks[0].Price) / 2m;
                    }
                    return 0m;
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DbToPortfolioSync: GetOpenPositionsAsync failed; skipping this tick.");
            return;
        }

        // Project to PaperPosition mirrors. OKX only gives Symbol/Qty/IsLong/AvgEntry —
        // enough for monitor visibility (heartbeat count, TotalEquity sum, dashboard).
        // Detailed fields (StopLoss, TakeProfit, MaxFavorable, etc.) are unavailable from
        // this API — left as defaults.
        var nowUtc = DateTime.UtcNow;
        var openMirrors = okxPositions
            .Where(p => p.Quantity > 0m)
            .Select(p => ProjectFromOkx(p, nowUtc, markLookup))
            .ToList();

        // Project closed positions if we fetched fresh ones this tick.
        // [2026-05-16] Attribution lookup: resolve strategy tag from the ledger written
        // by EdgeLabSingleAssetHostedService on every successful fill. Falls back to
        // "OkxLive" when no match (closed before ledger started, or non-EdgeLab fill).
        List<PaperPosition>? closedMirrors = null;
        if (okxClosed is not null)
        {
            EdgeLabFillAttributionLedger? attributionLedger = null;
            try
            {
                using var attribScope = _serviceProvider.CreateScope();
                attributionLedger = attribScope.ServiceProvider.GetService<EdgeLabFillAttributionLedger>();
            }
            catch { /* attribution is optional — proceed without */ }

            closedMirrors = okxClosed
                .Where(c => c.Quantity > 0m && c.OpenAvgPrice > 0m && c.CloseAvgPrice > 0m)
                .Select(c =>
                {
                    var strategy = attributionLedger?.LookupStrategy(c.Symbol, c.OpenTimeUtc, toleranceSeconds: 90);
                    return ProjectClosedFromOkx(c with { StrategyTag = strategy });
                })
                .ToList();
        }

        int prevOpen, prevOkxOpen, newOkxOpen;
        int prevClosed, prevOkxClosed, newOkxClosed;

        // Atomically swap the LIVE_OKX subset under the same monitor that writers use.
        lock (_portfolio)
        {
            prevOpen = _portfolio.OpenPositions.Count;
            prevOkxOpen = _portfolio.OpenPositions.Count(p => p.ExecutionMode == OkxMirrorMarker);
            prevClosed = _portfolio.ClosedPositions.Count;
            prevOkxClosed = _portfolio.ClosedPositions.Count(p => p.ExecutionMode == OkxMirrorMarker);

            _portfolio.OpenPositions.RemoveAll(p => p.ExecutionMode == OkxMirrorMarker);
            _portfolio.OpenPositions.AddRange(openMirrors);

            // Only replace ClosedPositions LIVE_OKX subset if we fetched fresh closed list
            // (every 5 min). Open positions get refreshed every 60s.
            if (closedMirrors is not null)
            {
                _portfolio.ClosedPositions.RemoveAll(p => p.ExecutionMode == OkxMirrorMarker);
                _portfolio.ClosedPositions.AddRange(closedMirrors);
            }

            newOkxOpen = openMirrors.Count;
            newOkxClosed = closedMirrors?.Count ?? prevOkxClosed;
        }

        var delta = newOkxOpen - prevOkxOpen;
        var deltaClosed = newOkxClosed - prevOkxClosed;

        // Quiet log when nothing changes (avoid noise). Verbose on first tick or any change.
        if (delta != 0 || prevOpen == 0 || newOkxOpen > 0 || (closedMirrors is not null && deltaClosed != 0))
        {
            // Render symbol×qty list so operator can sanity-check at a glance.
            var summary = openMirrors.Count == 0
                ? "(none)"
                : string.Join(", ", openMirrors.Take(8).Select(p =>
                    $"{p.Symbol} {(p.Side == TradeSide.Buy ? "+" : "-")}{p.Quantity:F4}"));
            _logger.LogInformation(
                "DbToPortfolioSync: open {PrevOkx}→{NewOkx} (Δ{D:+0;-0;0}) closed {PrevClosed}→{NewClosed} (Δ{DC:+0;-0;0}) [paper-only open={POnly} closed={PConly}]  {Summary}",
                prevOkxOpen, newOkxOpen, delta, prevOkxClosed, newOkxClosed, deltaClosed,
                prevOpen - prevOkxOpen, prevClosed - prevOkxClosed, summary);
        }
    }

    private static PaperPosition ProjectClosedFromOkx(ClosedPositionSnapshot c)
    {
        var side = c.WasLong ? TradeSide.Buy : TradeSide.Sell;
        // RealizedPnl from OKX is NET of fees on swap (fees deducted at fill). Store as-is.
        // PaperPosition.RealizedPnL is what PerStrategyAttribution / TCA aggregate. Match here.
        return new PaperPosition
        {
            Symbol = c.Symbol,
            Side = side,
            EntryPrice = c.OpenAvgPrice,
            CurrentPrice = c.CloseAvgPrice,
            Quantity = c.Quantity,
            OpenedQuantity = c.Quantity,
            EntryTime = c.OpenTimeUtc,
            ExitTime = c.CloseTimeUtc,
            RealizedPnL = c.RealizedPnl,
            ExitReason = "OKX position closed",
            // OKX positions-history doesn't surface strategy attribution. Tag as "OkxLive"
            // so per-strategy digest can group them separately from paper-only entries.
            StrategyTag = string.IsNullOrEmpty(c.StrategyTag) ? "OkxLive" : c.StrategyTag,
            RegimeTag = "Live",
            ExecutionMode = OkxMirrorMarker,
            PromotionStage = "Live",
            RealFeePaid = c.FeePaid > 0m ? c.FeePaid : null,
            IsLeveragedPaperPosition = true,
            Leverage = 1m,
            MarginMode = MarginMode.Isolated,
        };
    }

    private static PaperPosition ProjectFromOkx(
        ExchangePositionSnapshot p,
        DateTime nowUtc,
        Func<string, decimal>? markLookup)
    {
        var side = p.IsLong ? TradeSide.Buy : TradeSide.Sell;
        var entry = p.AvgEntryPrice ?? 0m;
        // [2026-05-15] Pull live mark from the OKX WebSocket book cache when available.
        // When unavailable (no book yet, stale, or non-OKX exchange), fall back to entry —
        // gives UnrealizedPnL = 0 which is "no info" rather than wrong info.
        var mark = markLookup?.Invoke(p.Symbol) ?? 0m;
        if (mark <= 0m) mark = entry;

        return new PaperPosition
        {
            Symbol = p.Symbol,
            Side = side,
            EntryPrice = entry,
            CurrentPrice = mark,
            Quantity = p.Quantity,
            RemainingQuantity = p.Quantity,
            OpenedQuantity = p.Quantity,
            EntryTime = nowUtc,              // unknown from this API; nowUtc is placeholder for "currently open"
            StrategyTag = "OkxMirror",       // OKX positions API doesn't surface strategy attribution
            RegimeTag = "Unknown",
            ExecutionMode = OkxMirrorMarker,
            PromotionStage = "Live",
            // [2026-05-15 BUG FIX] Critical: mark as leveraged so PaperPortfolio.TotalEquity
            // uses UnrealizedPnL (per-leg P&L vs entry) NOT MarketValue (treats short as
            // owing tokens, gives -2× notional reading on a perp short → fake -18% drawdown).
            IsLeveragedPaperPosition = true,
            Leverage = 1m,                   // OKX position snapshot doesn't surface leverage; PaperPortfolio uses Lev for margin display only
            MarginMode = MarginMode.Isolated,
        };
    }
}
