using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace Listener.Api.Endpoints;

public static class DashboardEndpoints
{
    private const int RecentClosedCount = 100;

    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api").WithTags("Dashboard");

        group.MapGet("/portfolio", GetPortfolioSummary);
        group.MapGet("/portfolio/equity-timeline", GetEquityTimeline);
        group.MapGet("/positions", GetPositions);
        group.MapGet("/positions/{id:guid}", GetPositionDetail);
        group.MapGet("/positions/{id:guid}/candles", GetPositionCandles);
        group.MapPost("/positions/close", RequestPositionClose);
        group.MapPost("/positions/flatten-all", RequestFlattenAll);
        group.MapGet("/trades", GetRecentTrades);
        group.MapGet("/trades/by-strategy", GetTradesByStrategy);
        group.MapGet("/signals/recent", GetRecentSignals);
        group.MapGet("/exchange/health", GetExchangeHealth);

        return endpoints;
    }

    private static async Task<IResult> GetTradesByStrategy(ITradeRepository trades)
    {
        var all = await trades.GetAllAsync(2000);
        var closed = all.Where(t => t.IsClosed).ToList();

        var groups = closed
            .GroupBy(t => string.IsNullOrWhiteSpace(t.StrategyTag) ? "Untagged" : t.StrategyTag)
            .OrderByDescending(g => g.Sum(t => t.RealisedPnL))
            .Select(g =>
            {
                var list = g.ToList();
                var winners = list.Where(t => t.RealisedPnL > 0m).ToList();
                var losers = list.Where(t => t.RealisedPnL < 0m).ToList();
                var grossProfit = winners.Sum(t => t.RealisedPnL);
                var grossLoss = Math.Abs(losers.Sum(t => t.RealisedPnL));
                var pf = grossLoss <= 0m ? 0d : (double)(grossProfit / grossLoss);

                // Sharpe approximation: mean(returns) / stdev(returns) * sqrt(252) using PnL% per trade
                var rets = list.Select(t => (double)t.PnlPercent).ToArray();
                var mean = rets.Length == 0 ? 0d : rets.Average();
                var variance = rets.Length <= 1 ? 0d : rets.Select(r => (r - mean) * (r - mean)).Sum() / (rets.Length - 1);
                var stdev = Math.Sqrt(variance);
                var sharpe = stdev <= 0 ? 0d : mean / stdev * Math.Sqrt(252);

                return new StrategyBreakdownDto(
                    StrategyTag: g.Key,
                    TradeCount: list.Count,
                    WinCount: winners.Count,
                    LossCount: losers.Count,
                    WinRate: list.Count == 0 ? 0d : (double)winners.Count / list.Count,
                    ProfitFactor: pf,
                    NetPnl: list.Sum(t => t.RealisedPnL),
                    AveragePnl: list.Count == 0 ? 0m : list.Sum(t => t.RealisedPnL) / list.Count,
                    Sharpe: sharpe,
                    MaxWin: list.Count == 0 ? 0m : list.Max(t => t.RealisedPnL),
                    MaxLoss: list.Count == 0 ? 0m : list.Min(t => t.RealisedPnL),
                    LastTradeUtc: list.Max(t => t.ExitTime ?? t.EntryTime));
            })
            .ToList();

        return Results.Ok(groups);
    }

    private static async Task<IResult> GetRecentSignals(
        ISignalRepository signals,
        int? limit)
    {
        var n = Math.Clamp(limit ?? 25, 1, 200);
        var recent = await signals.GetRecentAsync(n);
        var dtos = recent
            .OrderByDescending(s => s.Timestamp)
            .Select(s => new SignalSnapshotDto(
                Id: s.Id,
                Symbol: s.Symbol,
                Side: s.Side.ToString(),
                Price: s.Price,
                Quantity: s.Quantity,
                Confidence: s.Confidence,
                Reasoning: s.Reasoning ?? "",
                Status: s.Status.ToString(),
                StrategyTag: s.StrategyTag ?? "Untagged",
                RegimeTag: s.RegimeTag ?? "Unknown",
                Source: s.Source ?? "",
                Exchange: s.Exchange ?? "",
                Timestamp: s.Timestamp,
                ReasonFlags: s.ReasonFlags.ToString()))
            .ToList();

        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetEquityTimeline(ITradeRepository trades)
    {
        var all = await trades.GetAllAsync(5000);
        var closed = all.Where(t => t.IsClosed && t.ExitTime.HasValue)
            .OrderBy(t => t.ExitTime!.Value)
            .ToList();

        if (closed.Count == 0) return Results.Ok(Array.Empty<EquityPointDto>());

        var byDay = closed
            .GroupBy(t => DateOnly.FromDateTime(t.ExitTime!.Value.ToUniversalTime()))
            .OrderBy(g => g.Key);

        decimal cumulative = 0m;
        var points = new List<EquityPointDto>();
        foreach (var day in byDay)
        {
            var dailyPnl = day.Sum(t => t.RealisedPnL);
            cumulative += dailyPnl;
            points.Add(new EquityPointDto(
                Date: day.Key.ToString("yyyy-MM-dd"),
                DailyPnL: dailyPnl,
                CumulativeEquity: cumulative,
                TradeCount: day.Count()));
        }

        return Results.Ok(points);
    }

    private static async Task<IResult> GetPortfolioSummary(ITradeRepository trades, string? executionMode)
    {
        var openRaw = await trades.GetOpenAsync(500);
        var allRecent = await trades.GetAllAsync(1000);

        // Apply venue filter (executionMode) to both open and closed buckets so per-venue totals reconcile.
        bool MatchVenue(Trade t) =>
            string.IsNullOrWhiteSpace(executionMode) ||
            string.Equals(t.ExecutionMode, executionMode, StringComparison.OrdinalIgnoreCase);

        var open   = openRaw.Where(MatchVenue).ToList();
        var closed = allRecent.Where(t => t.IsClosed && MatchVenue(t)).ToList();

        var realized = closed.Sum(t => t.RealisedPnL);
        var unrealized = open.Sum(ComputeUnrealized);
        var fees = closed.Sum(t => t.TotalFeesPaid) + open.Sum(t => t.TotalFeesPaid);
        var funding = closed.Sum(t => t.NetFunding) + open.Sum(t => t.NetFunding);

        var winners = closed.Where(t => t.RealisedPnL > 0m).ToList();
        var losers = closed.Where(t => t.RealisedPnL < 0m).ToList();
        var winRate = closed.Count == 0 ? 0d : (double)winners.Count / closed.Count;

        var grossProfit = winners.Sum(t => t.RealisedPnL);
        var grossLoss = Math.Abs(losers.Sum(t => t.RealisedPnL));
        var profitFactor = grossLoss <= 0m ? 0d : (double)(grossProfit / grossLoss);

        var avgWin = winners.Count > 0 ? winners.Average(t => Math.Abs(t.RealisedPnL)) : 0m;
        var avgLoss = losers.Count > 0 ? losers.Average(t => Math.Abs(t.RealisedPnL)) : 0m;
        var avgRr = avgLoss <= 0m ? 0d : (double)(avgWin / avgLoss);

        var avgPnL = closed.Count == 0 ? 0m : closed.Sum(t => t.RealisedPnL) / closed.Count;

        // Balance is opaque from Trade history alone; expose realized as the floor.
        // Web layer can override via dedicated paper-portfolio endpoint when wired.
        var balance = realized;
        var totalEquity = balance + unrealized;

        var dto = new PortfolioSummaryDto(
            Balance: balance,
            RealizedPnL: realized,
            UnrealizedPnL: unrealized,
            TotalEquity: totalEquity,
            TotalFeesPaid: fees,
            NetFunding: funding,
            OpenPositionCount: open.Count,
            ClosedTradeCount: closed.Count,
            WinningTradeCount: winners.Count,
            LosingTradeCount: losers.Count,
            WinRate: winRate,
            ProfitFactor: profitFactor,
            AverageRiskReward: avgRr,
            AverageTradePnL: avgPnL);

        return Results.Ok(dto);
    }

    private static async Task<IResult> GetPositions(ITradeRepository trades, string? executionMode)
    {
        var open = await trades.GetOpenAsync(500);
        IEnumerable<Trade> q = open;
        if (!string.IsNullOrWhiteSpace(executionMode))
        {
            q = q.Where(t => string.Equals(t.ExecutionMode, executionMode, StringComparison.OrdinalIgnoreCase));
        }
        var dtos = q.Select(MapPosition).ToList();
        return Results.Ok(dtos);
    }

    /// <summary>
    /// Operator-initiated close request. INSERTs a row into ManualCloseRequests; the
    /// QuantEngine.Worker polls this table and dispatches to PaperTradingService.
    /// Returns the queued request id immediately (fire-and-forget from the UI's POV).
    /// </summary>
    /// <summary>
    /// Operator-initiated FLATTEN ALL — closes every open position regardless
    /// of symbol. INSERTs a sentinel row (Symbol = "__FLATTEN_ALL__") which the
    /// QuantEngine.Worker's ManualCloseRequestProcessor recognises and dispatches
    /// to PaperTradingService.FlattenAllPositionsAsync.
    /// </summary>
    private static async Task<IResult> RequestFlattenAll(
        [Microsoft.AspNetCore.Mvc.FromBody] FlattenAllRequestDto? dto,
        ThatsMyQuant.Infrastructure.Persistence.QuantDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("DashboardEndpoints.RequestFlattenAll");
        var reason = string.IsNullOrWhiteSpace(dto?.Reason) ? "OperatorFlatAll" : dto!.Reason!.Trim();
        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO "ManualCloseRequests" ("Symbol", "Side", "Reason", "Status", "RequestedUtc")
                VALUES ('__FLATTEN_ALL__', NULL, $reason, 'Pending', $now);
                SELECT last_insert_rowid();
                """;
            var pReason = cmd.CreateParameter(); pReason.ParameterName = "$reason"; pReason.Value = reason; cmd.Parameters.Add(pReason);
            var pNow    = cmd.CreateParameter(); pNow.ParameterName = "$now";    pNow.Value = DateTime.UtcNow.ToString("O"); cmd.Parameters.Add(pNow);

            var idObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var id = idObj is long l ? l : Convert.ToInt64(idObj);
            log.LogWarning("FLATTEN ALL requested: id={Id} reason={Reason}", id, reason);
            return Results.Ok(new PositionCloseResponseDto(Id: id, Status: "Pending", Symbol: "__FLATTEN_ALL__"));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "RequestFlattenAll failed");
            return Results.Problem(title: "Flat-all queue failed", detail: ex.Message, statusCode: 500);
        }
    }

    private static async Task<IResult> RequestPositionClose(
        [Microsoft.AspNetCore.Mvc.FromBody] PositionCloseRequestDto dto,
        ThatsMyQuant.Infrastructure.Persistence.QuantDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("DashboardEndpoints.RequestPositionClose");
        if (dto is null || string.IsNullOrWhiteSpace(dto.Symbol))
            return Results.BadRequest(new { error = "symbol required" });

        var sym = dto.Symbol.Trim().ToUpperInvariant();
        var side = string.IsNullOrWhiteSpace(dto.Side) ? null : dto.Side.Trim();
        var reason = string.IsNullOrWhiteSpace(dto.Reason) ? "OperatorManual" : dto.Reason.Trim();

        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO "ManualCloseRequests" ("Symbol", "Side", "Reason", "Status", "RequestedUtc")
                VALUES ($symbol, $side, $reason, 'Pending', $now);
                SELECT last_insert_rowid();
                """;
            var pSym = cmd.CreateParameter(); pSym.ParameterName = "$symbol"; pSym.Value = sym; cmd.Parameters.Add(pSym);
            var pSide = cmd.CreateParameter(); pSide.ParameterName = "$side"; pSide.Value = (object?)side ?? DBNull.Value; cmd.Parameters.Add(pSide);
            var pReason = cmd.CreateParameter(); pReason.ParameterName = "$reason"; pReason.Value = reason; cmd.Parameters.Add(pReason);
            var pNow = cmd.CreateParameter(); pNow.ParameterName = "$now"; pNow.Value = DateTime.UtcNow.ToString("O"); cmd.Parameters.Add(pNow);

            var idObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var id = idObj is long l ? l : Convert.ToInt64(idObj);

            log.LogInformation("ManualCloseRequest queued: id={Id} symbol={Sym} side={Side} reason={Reason}", id, sym, side, reason);
            return Results.Ok(new PositionCloseResponseDto(Id: id, Status: "Pending", Symbol: sym));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to queue ManualCloseRequest for {Symbol}", sym);
            return Results.Problem(title: "Could not queue close request", detail: ex.Message, statusCode: 500);
        }
    }

    private static async Task<IResult> GetPositionDetail(Guid id, ITradeRepository trades)
    {
        var trade = await trades.GetByIdAsync(id);
        if (trade is null)
        {
            return Results.NotFound();
        }

        var dto = new PositionDetailDto(
            Position: MapPosition(trade),
            Trade: MapTradeJournal(trade),
            Reasoning: trade.ConcordanceSummary,
            MaxFavorableExcursionPercent: trade.MaxFavorableExcursionPercent,
            MaxAdverseExcursionPercent: trade.MaxAdverseExcursionPercent,
            SignalToFillDriftBps: trade.SignalToFillDriftBps,
            TotalFeesPaid: trade.TotalFeesPaid,
            NetFunding: trade.NetFunding,
            MlModelVersion: trade.MlModelVersion,
            PromotionStage: trade.PromotionStage);

        return Results.Ok(dto);
    }

    private static async Task<IResult> GetPositionCandles(
        Guid id,
        ITradeRepository trades,
        IMarketDataService marketData)
    {
        var trade = await trades.GetByIdAsync(id);
        if (trade is null) return Results.NotFound();

        try
        {
            var candles = await marketData.GetCandlesAsync(trade.Symbol, 120, "Crypto", "1h");
            if (candles is null || candles.Count == 0)
                return Results.Ok(Array.Empty<CandleDto>());

            var dtos = candles
                .OrderBy(c => c.Timestamp)
                .Select(c => new CandleDto(
                    Time: c.Timestamp.ToString("O"),
                    Open: c.Open,
                    High: c.High,
                    Low: c.Low,
                    Close: c.Close,
                    Volume: c.Volume))
                .ToList();

            return Results.Ok(dtos);
        }
        catch
        {
            return Results.Ok(Array.Empty<CandleDto>());
        }
    }

    private static async Task<IResult> GetRecentTrades(
        ITradeRepository trades,
        int? limit,
        string? strategy,
        string? status,
        string? executionMode,   // PAPER | REAL | OKX_DEMO  (matches Trade.ExecutionMode)
        DateTime? from,
        DateTime? to)
    {
        // Pull a generous pool because filtering happens client-side here.
        var poolSize = Math.Clamp((limit ?? RecentClosedCount) * 4, 100, 2000);
        var all = await trades.GetAllAsync(poolSize);

        IEnumerable<Trade> q = all;

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (status.Equals("open", StringComparison.OrdinalIgnoreCase))
            {
                q = q.Where(t => !t.IsClosed);
            }
            else if (status.Equals("closed", StringComparison.OrdinalIgnoreCase))
            {
                q = q.Where(t => t.IsClosed);
            }
        }
        else
        {
            q = q.Where(t => t.IsClosed);
        }

        if (!string.IsNullOrWhiteSpace(strategy))
        {
            q = q.Where(t => string.Equals(t.StrategyTag, strategy, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(executionMode))
        {
            q = q.Where(t => string.Equals(t.ExecutionMode, executionMode, StringComparison.OrdinalIgnoreCase));
        }

        if (from.HasValue)
        {
            var fromUtc = from.Value.ToUniversalTime();
            q = q.Where(t => (t.ExitTime ?? t.EntryTime) >= fromUtc);
        }

        if (to.HasValue)
        {
            var toUtc = to.Value.ToUniversalTime();
            q = q.Where(t => (t.ExitTime ?? t.EntryTime) <= toUtc);
        }

        var count = Math.Clamp(limit ?? RecentClosedCount, 1, 500);
        var dtos = q
            .OrderByDescending(t => t.ExitTime ?? t.EntryTime)
            .Take(count)
            .Select(MapTradeJournal)
            .ToList();
        return Results.Ok(dtos);
    }

    private static IResult GetExchangeHealth(IExchangeHealthProvider provider)
    {
        var snap = provider.Current;
        var dto = new ExchangeHealthDto(
            Venue: snap.Venue,
            CredentialsConfigured: snap.CredentialsConfigured,
            IsConnected: snap.IsConnected,
            IsDemoMode: snap.IsDemoMode,
            Balance: snap.Balance,
            LastSuccessUtc: snap.LastSuccessUtc,
            LastAttemptUtc: snap.LastAttemptUtc,
            LastError: snap.LastError,
            ConsecutiveFailures: snap.ConsecutiveFailures);
        return Results.Ok(dto);
    }

    private static PositionDto MapPosition(Trade t)
    {
        var mark = t.MarkPrice > 0m ? t.MarkPrice : t.EntryPrice;
        var unrealized = ComputeUnrealized(t, mark);
        var roe = ComputeRoe(t, unrealized);
        return new PositionDto(
            Id: t.Id,
            Symbol: t.Symbol,
            Side: t.Side == TradeSide.Sell ? "SHORT" : "LONG",
            Quantity: t.Quantity,
            RemainingQuantity: t.RemainingQuantity > 0m ? t.RemainingQuantity : t.Quantity,
            EntryPrice: t.EntryPrice,
            MarkPrice: mark,
            UnrealizedPnL: unrealized,
            Roe: roe,
            Leverage: t.Leverage,
            LifecycleStatus: t.LifecycleStatus.ToString(),
            ExitPolicyTag: t.ExitPolicyTag,
            StrategyTag: t.StrategyTag,
            StopLoss: t.StopLoss,
            TakeProfit: t.TakeProfit,
            EntryTime: t.EntryTime);
    }

    private static TradeJournalDto MapTradeJournal(Trade t)
    {
        return new TradeJournalDto(
            Id: t.Id,
            Symbol: t.Symbol,
            Side: t.Side == TradeSide.Sell ? "SHORT" : "LONG",
            EntryPrice: t.EntryPrice,
            ExitPrice: t.AverageExitPrice > 0m ? t.AverageExitPrice : t.ExitPrice,
            Quantity: t.Quantity,
            RealizedPnL: t.RealisedPnL,
            PnlPercent: t.PnlPercent,
            Status: t.Status.ToString(),
            LifecycleStatus: t.LifecycleStatus.ToString(),
            ExecutionMode: t.ExecutionMode,
            ExitReason: t.ExitReason,
            StrategyTag: t.StrategyTag,
            EntryTime: t.EntryTime,
            ExitTime: t.ExitTime);
    }

    private static decimal ComputeUnrealized(Trade t)
    {
        var mark = t.MarkPrice > 0m ? t.MarkPrice : t.EntryPrice;
        return ComputeUnrealized(t, mark);
    }

    private static decimal ComputeUnrealized(Trade t, decimal mark)
    {
        var qty = t.RemainingQuantity > 0m ? t.RemainingQuantity : t.Quantity;
        if (qty <= 0m) return 0m;
        var direction = t.Side == TradeSide.Sell ? -1m : 1m;
        return (mark - t.EntryPrice) * qty * direction;
    }

    private static double ComputeRoe(Trade t, decimal unrealized)
    {
        var notional = t.EntryPrice * (t.RemainingQuantity > 0m ? t.RemainingQuantity : t.Quantity);
        if (notional <= 0m) return 0d;
        var leverage = t.Leverage <= 0m ? 1m : t.Leverage;
        var marginUsed = notional / leverage;
        if (marginUsed <= 0m) return 0d;
        return (double)(unrealized / marginUsed);
    }
}
