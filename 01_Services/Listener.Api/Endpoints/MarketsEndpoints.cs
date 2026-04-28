using Microsoft.AspNetCore.Mvc;
using MediatR;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace Listener.Api.Endpoints;

/// <summary>
/// Lightweight market data surface for the dashboard's Markets page —
/// watchlist symbols (with last price + 24h change + sparkline) and
/// per-symbol candle history for the main chart.
/// </summary>
public static class MarketsEndpoints
{
    private static readonly string[] DefaultWatchlist = new[]
    {
        // Top crypto by liquidity — covers our active strategies + popular references
        "BTCUSDT", "ETHUSDT", "SOLUSDT", "AVAXUSDT", "LINKUSDT",
        "BNBUSDT", "XRPUSDT", "ADAUSDT", "DOGEUSDT", "MATICUSDT",
        "DOTUSDT",  "LTCUSDT",  "TRXUSDT", "XLMUSDT",  "AAVEUSDT",
        "ATOMUSDT", "NEARUSDT", "ARBUSDT", "OPUSDT",   "INJUSDT",
    };

    public static IEndpointRouteBuilder MapMarketsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/markets").WithTags("Markets");
        group.MapGet("/symbols", GetSymbols);
        group.MapGet("/candles/{symbol}", GetCandles);
        group.MapGet("/orderbook/{symbol}", GetOrderBook);
        group.MapGet("/analysis/{symbol}", GetAnalysis);
        group.MapGet("/narrative/{symbol}", GetNarrative);
        group.MapPost("/orders", SubmitManualOrder);
        return endpoints;
    }

    /// <summary>
    /// Submits a manual order from the Markets / Order Entry panel.
    /// Routes the request as a `Signal` through the standard `ProcessSignalCommand`
    /// mediator pipeline — exactly what the bot uses, but with `Source="Markets.Manual"`.
    /// In paper mode this becomes a paper trade; in live mode it triggers a real fill.
    /// </summary>
    private static async Task<IResult> SubmitManualOrder(
        [FromBody] ManualOrderDto dto,
        IMediator mediator,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("MarketsEndpoints.SubmitManualOrder");
        if (dto is null)
            return Results.BadRequest(new { error = "body required" });
        if (string.IsNullOrWhiteSpace(dto.Symbol))
            return Results.BadRequest(new { error = "symbol required" });
        if (dto.Quantity <= 0m)
            return Results.BadRequest(new { error = "quantity must be > 0" });
        if (dto.Entry <= 0m)
            return Results.BadRequest(new { error = "entry must be > 0" });

        var side = string.Equals(dto.Side, "long", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dto.Side, "buy", StringComparison.OrdinalIgnoreCase)
            ? TradeSide.Buy
            : TradeSide.Sell;

        var orderType = string.Equals(dto.OrderType, "market", StringComparison.OrdinalIgnoreCase)
            ? OrderType.Market
            : OrderType.Limit;

        var signal = new Signal
        {
            Source       = "Markets.Manual",
            Symbol       = dto.Symbol.ToUpperInvariant(),
            Side         = side,
            Price        = dto.Entry,
            Quantity     = dto.Quantity,
            Status       = TradeStatus.Pending,
            OrderType    = orderType,
            MarketType   = MarketType.Crypto,
            StrategyTag  = "ManualOrderEntry",
            Confidence   = 100,
            Reasoning    = $"Manual order from Markets UI · risk={dto.RiskPct:F2}% · lev={dto.Leverage:F1}x",
            Exchange     = "OKX",
        };

        if (dto.StopLoss.HasValue && dto.StopLoss.Value > 0m)
            signal.Metadata["StopLoss"] = dto.StopLoss.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (dto.TakeProfit.HasValue && dto.TakeProfit.Value > 0m)
            signal.Metadata["TakeProfit"] = dto.TakeProfit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (dto.Leverage.HasValue && dto.Leverage.Value > 0m)
            signal.Metadata["RequestedLeverage"] = dto.Leverage.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            var accepted = await mediator.Send(new ProcessSignalCommand(signal), ct);
            log.LogInformation("Manual order {Id} {Symbol} {Side} qty={Qty} @ {Price} → accepted={Accepted}, status={Status}",
                signal.Id, signal.Symbol, signal.Side, signal.Quantity, signal.Price, accepted, signal.Status);

            return Results.Ok(new ManualOrderResponseDto(
                SignalId: signal.Id,
                Accepted: accepted,
                Status:   signal.Status.ToString(),
                Reasoning: signal.Reasoning ?? string.Empty));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Manual order failed for {Symbol}", signal.Symbol);
            return Results.Problem(title: "Manual order failed", detail: ex.Message, statusCode: 500);
        }
    }

    /// <summary>
    /// Generates a deterministic Claude-style analyst paragraph from the same
    /// indicators GetAnalysis computes. No LLM call — just a thoughtful template
    /// that reads naturally. Updates whenever indicators move.
    /// </summary>
    private static async Task<IResult> GetNarrative(
        string symbol,
        IMarketDataService marketData,
        [FromQuery] string timeframe = "1h",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return Results.BadRequest(new { error = "symbol is required" });
        symbol = symbol.ToUpperInvariant();

        try
        {
            var candles = await marketData.GetCandlesAsync(symbol, 100, "Crypto", timeframe);
            if (candles is null || candles.Count < 30)
                return Results.Ok(new NarrativeDto(symbol, timeframe, DateTime.UtcNow,
                    "Insufficient candle history to form a read. Once we have ~30 bars on this timeframe, the analyst will weigh in.",
                    "Neutral", Array.Empty<string>()));

            var ordered = candles.OrderBy(c => c.Timestamp).ToArray();
            var closes = ordered.Select(c => c.Close).ToArray();
            var highs = ordered.Select(c => c.High).ToArray();
            var lows = ordered.Select(c => c.Low).ToArray();
            var last = closes[^1];
            var rsi = ComputeRsi(closes, 14);
            var ma20 = closes.TakeLast(20).Average();
            var ma50 = closes.TakeLast(Math.Min(50, closes.Length)).Average();

            // ATR%
            var trs = new decimal[ordered.Length];
            trs[0] = highs[0] - lows[0];
            for (int i = 1; i < ordered.Length; i++)
                trs[i] = Math.Max(highs[i] - lows[i], Math.Max(Math.Abs(highs[i] - closes[i - 1]), Math.Abs(lows[i] - closes[i - 1])));
            var atr = trs.TakeLast(14).Average();
            var atrPct = last > 0 ? atr / last * 100m : 0m;

            // 24h change
            var sliceLen = Math.Min(24, ordered.Length - 1);
            var prior24h = closes[ordered.Length - 1 - sliceLen];
            var pct24h = prior24h > 0 ? (last - prior24h) / prior24h * 100m : 0m;

            string regime;
            var trendStrength = ma20 > 0 ? Math.Abs(last - ma20) / ma20 : 0m;
            if (atrPct > 4m) regime = "Volatile";
            else if (last > ma20 && ma20 > ma50 && trendStrength > 0.005m) regime = "Trending Up";
            else if (last < ma20 && ma20 < ma50 && trendStrength > 0.005m) regime = "Trending Down";
            else regime = "Ranging";

            string bias =
                rsi > 70m ? "Overbought" :
                rsi < 30m ? "Oversold" :
                last > ma20 && rsi > 55m ? "Bullish" :
                last < ma20 && rsi < 45m ? "Bearish" :
                "Neutral";

            // Compose the paragraph
            var sb = new System.Text.StringBuilder();
            sb.Append(symbol).Append(" is trading at ").Append(FormatPrice(last))
              .Append(", ").Append(pct24h >= 0 ? "up " : "down ").Append(Math.Abs(pct24h).ToString("F2")).Append("% over the last 24 hours");

            switch (regime)
            {
                case "Trending Up":
                    sb.Append(", and the tape is constructive — price is holding above both the 20- and 50-period averages, suggesting buyers remain in control of the structure.");
                    break;
                case "Trending Down":
                    sb.Append(", and momentum is heavy on the downside — the price is below both moving averages, with sellers consistently meeting bids on each push higher.");
                    break;
                case "Volatile":
                    sb.Append(", under expanded volatility (ATR ").Append(atrPct.ToString("F1")).Append("%). Position sizing and stops want to be wider than usual, and one-sided conviction is harder to justify.");
                    break;
                default:
                    sb.Append(", with price coiling around the 20-period mean. The tape is range-bound; mean-reversion setups are favoured over trend continuation until the structure resolves.");
                    break;
            }
            sb.Append(' ');

            switch (bias)
            {
                case "Overbought":
                    sb.Append("RSI at ").Append(rsi.ToString("F0")).Append(" reads stretched — be wary of a pull-back or sideways consolidation before fresh longs are added.");
                    break;
                case "Oversold":
                    sb.Append("RSI at ").Append(rsi.ToString("F0")).Append(" sits in compressed territory; a relief bounce is the path of least resistance, though counter-trend trades remain risk-managed.");
                    break;
                case "Bullish":
                    sb.Append("RSI at ").Append(rsi.ToString("F0")).Append(" supports the bid — momentum and trend are aligned, so pull-backs to the 20-MA are the cleaner entry.");
                    break;
                case "Bearish":
                    sb.Append("RSI at ").Append(rsi.ToString("F0")).Append(" confirms the offer — bounces into the 20-MA are the cleaner short, and continuation lower remains the base case.");
                    break;
                default:
                    sb.Append("RSI at ").Append(rsi.ToString("F0")).Append(" is mid-range — neither side has the upper hand. Wait for a break of the recent range or a divergence to engage.");
                    break;
            }

            // Risk parenthetical
            if (atrPct > 5m)
                sb.Append(" Risk note — realised volatility is elevated; size into anything new conservatively.");
            else if (atrPct < 1m)
                sb.Append(" Risk note — volatility has compressed; expansion is statistically due, so prepare for a regime shift.");

            // Bullets — actionable summary
            var bullets = new List<string>();
            if (last > ma20 && ma20 > ma50)
                bullets.Add($"MA20 ({FormatPrice(ma20)}) > MA50 ({FormatPrice(ma50)}) — trend support intact");
            else if (last < ma20 && ma20 < ma50)
                bullets.Add($"MA20 ({FormatPrice(ma20)}) < MA50 ({FormatPrice(ma50)}) — trend pressure persists");
            else
                bullets.Add($"MAs interlaced — MA20 {FormatPrice(ma20)} vs MA50 {FormatPrice(ma50)}");

            bullets.Add($"RSI {rsi:F0} · ATR {atrPct:F2}% · 24h Δ {(pct24h >= 0 ? "+" : "")}{pct24h:F2}%");

            return Results.Ok(new NarrativeDto(symbol, timeframe, DateTime.UtcNow, sb.ToString(), bias, bullets.ToArray()));
        }
        catch (Exception ex)
        {
            return Results.Problem(title: "Narrative failed", detail: ex.Message, statusCode: 502);
        }
    }

    private static string FormatPrice(decimal v) =>
        v >= 1000 ? v.ToString("N2") : v >= 1 ? v.ToString("F2") : v.ToString("F5");


    private static async Task<IResult> GetSymbols(
        IMarketDataService marketData,
        [FromQuery] string? include,
        CancellationToken ct)
    {
        var requested = string.IsNullOrWhiteSpace(include)
            ? DefaultWatchlist
            : include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant())
                .Distinct()
                .Take(40)
                .ToArray();

        var snapshots = await Task.WhenAll(requested.Select(async sym =>
        {
            try
            {
                var candles = await marketData.GetCandlesAsync(sym, 25, "Crypto", "1h");
                if (candles is null || candles.Count < 2)
                    return new MarketSymbolDto(sym, 0m, 0m, Array.Empty<decimal>());

                var ordered = candles.OrderBy(c => c.Timestamp).ToArray();
                var last = ordered[^1].Close;
                var firstPriceInWindow = ordered[0].Close;
                var change24h = firstPriceInWindow == 0m
                    ? 0m
                    : (last - firstPriceInWindow) / firstPriceInWindow * 100m;
                var spark = ordered.Select(c => c.Close).ToArray();
                return new MarketSymbolDto(sym, last, change24h, spark);
            }
            catch
            {
                return new MarketSymbolDto(sym, 0m, 0m, Array.Empty<decimal>());
            }
        }));

        return Results.Ok(snapshots);
    }

    /// <summary>
    /// Synthesized order book from latest candle close. Real L2 depth requires
    /// exchange WebSocket subscription — out of scope for the snapshot view.
    /// Spread is sized by inferred volatility (ATR fallback), and 12 levels per side
    /// are placed with a geometric-ish step so the ladder looks realistic.
    /// </summary>
    private static async Task<IResult> GetOrderBook(
        string symbol,
        IMarketDataService marketData,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return Results.BadRequest(new { error = "symbol is required" });
        symbol = symbol.ToUpperInvariant();

        try
        {
            var candles = await marketData.GetCandlesAsync(symbol, 24, "Crypto", "1h");
            if (candles is null || candles.Count == 0)
                return Results.Ok(new OrderBookDto(symbol, 0m, 0m, Array.Empty<OrderBookLevelDto>(), Array.Empty<OrderBookLevelDto>(), true));

            var ordered = candles.OrderBy(c => c.Timestamp).ToArray();
            var last = ordered[^1].Close;
            // Average true range as a proxy for spread sizing
            var atr = ordered
                .Skip(1)
                .Zip(ordered, (a, b) => Math.Max(a.High - a.Low, Math.Max(Math.Abs(a.High - b.Close), Math.Abs(a.Low - b.Close))))
                .Average();

            var step = Math.Max(atr * 0.02m, last * 0.0005m);  // ~5bps minimum
            var spread = step;
            var midPrice = last;
            var bestBid = midPrice - spread / 2m;
            var bestAsk = midPrice + spread / 2m;
            var spreadBps = midPrice > 0 ? spread / midPrice * 10_000m : 0m;

            var rng = new Random(symbol.GetHashCode() ^ DateTime.UtcNow.Hour);
            var levels = 12;

            decimal[] sizes()
            {
                var arr = new decimal[levels];
                var baseSize = (decimal)(rng.NextDouble() * 0.8 + 0.4);
                for (int i = 0; i < levels; i++)
                {
                    var jitter = (decimal)(rng.NextDouble() * 0.5 + 0.75);
                    arr[i] = Math.Round(baseSize * (1 + i * 0.18m) * jitter, 4);
                }
                return arr;
            }

            var bidSizes = sizes();
            var askSizes = sizes();

            var bids = new OrderBookLevelDto[levels];
            var asks = new OrderBookLevelDto[levels];
            decimal cumBid = 0m, cumAsk = 0m;
            for (int i = 0; i < levels; i++)
            {
                var bidPrice = Math.Round(bestBid - i * step, last >= 1 ? 2 : 5);
                var askPrice = Math.Round(bestAsk + i * step, last >= 1 ? 2 : 5);
                cumBid += bidSizes[i];
                cumAsk += askSizes[i];
                bids[i] = new OrderBookLevelDto(bidPrice, bidSizes[i], Math.Round(cumBid, 4));
                asks[i] = new OrderBookLevelDto(askPrice, askSizes[i], Math.Round(cumAsk, 4));
            }

            return Results.Ok(new OrderBookDto(symbol, midPrice, Math.Round(spreadBps, 2), bids, asks, true));
        }
        catch
        {
            return Results.Ok(new OrderBookDto(symbol, 0m, 0m, Array.Empty<OrderBookLevelDto>(), Array.Empty<OrderBookLevelDto>(), true));
        }
    }

    /// <summary>
    /// Computes lightweight technical analysis from recent candles — RSI(14),
    /// MA(20), MA(50), ATR(14), heuristic regime classification, volatility.
    /// Pure server-side (no external indicator service dependency).
    /// </summary>
    private static async Task<IResult> GetAnalysis(
        string symbol,
        IMarketDataService marketData,
        [FromQuery] string timeframe = "1h",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return Results.BadRequest(new { error = "symbol is required" });
        symbol = symbol.ToUpperInvariant();

        try
        {
            var candles = await marketData.GetCandlesAsync(symbol, 100, "Crypto", timeframe);
            if (candles is null || candles.Count < 30)
                return Results.Ok(new AnalysisDto(symbol, timeframe, 0m, 0m, 0m, 0m, 0m, 0m, "Unknown", "Neutral", 0m, new[] { "insufficient candle history" }));

            var ordered = candles.OrderBy(c => c.Timestamp).ToArray();
            var closes = ordered.Select(c => c.Close).ToArray();
            var highs = ordered.Select(c => c.High).ToArray();
            var lows = ordered.Select(c => c.Low).ToArray();

            var last = closes[^1];

            // RSI(14)
            var rsi = ComputeRsi(closes, 14);

            // Moving averages
            var ma20 = closes.TakeLast(20).Average();
            var ma50 = closes.TakeLast(Math.Min(50, closes.Length)).Average();

            // ATR(14)
            var trs = new decimal[ordered.Length];
            trs[0] = highs[0] - lows[0];
            for (int i = 1; i < ordered.Length; i++)
            {
                trs[i] = Math.Max(highs[i] - lows[i], Math.Max(Math.Abs(highs[i] - closes[i - 1]), Math.Abs(lows[i] - closes[i - 1])));
            }
            var atr = trs.TakeLast(14).Average();
            var atrPct = last > 0 ? atr / last * 100m : 0m;

            // Volatility — stdev of returns over last 24
            var returns = new List<decimal>();
            for (int i = Math.Max(1, closes.Length - 24); i < closes.Length; i++)
            {
                if (closes[i - 1] > 0)
                    returns.Add((closes[i] - closes[i - 1]) / closes[i - 1]);
            }
            decimal vol = 0m;
            if (returns.Count > 1)
            {
                var mean = returns.Average();
                var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
                vol = (decimal)Math.Sqrt((double)variance) * 100m;
            }

            // Regime heuristic
            string regime;
            string bias;
            var trendStrength = ma20 > 0 ? Math.Abs(last - ma20) / ma20 : 0m;
            if (atrPct > 4m) regime = "Volatile";
            else if (last > ma20 && ma20 > ma50 && trendStrength > 0.005m) regime = "Trending Up";
            else if (last < ma20 && ma20 < ma50 && trendStrength > 0.005m) regime = "Trending Down";
            else regime = "Ranging";

            if (rsi > 70m) bias = "Overbought";
            else if (rsi < 30m) bias = "Oversold";
            else if (last > ma20 && rsi > 55m) bias = "Bullish";
            else if (last < ma20 && rsi < 45m) bias = "Bearish";
            else bias = "Neutral";

            var notes = new List<string>();
            if (last > ma20 && ma20 > ma50) notes.Add("MA20 > MA50 — trend support");
            else if (last < ma20 && ma20 < ma50) notes.Add("MA20 < MA50 — trend pressure");
            if (rsi > 70m) notes.Add($"RSI {rsi:F0} stretched — pull-back risk");
            else if (rsi < 30m) notes.Add($"RSI {rsi:F0} compressed — bounce zone");
            if (atrPct > 5m) notes.Add($"High volatility (ATR {atrPct:F1}%)");

            return Results.Ok(new AnalysisDto(
                symbol,
                timeframe,
                last,
                Math.Round(rsi, 2),
                Math.Round(ma20, last >= 1 ? 2 : 5),
                Math.Round(ma50, last >= 1 ? 2 : 5),
                Math.Round(atr, last >= 1 ? 2 : 5),
                Math.Round(atrPct, 2),
                regime,
                bias,
                Math.Round(vol, 2),
                notes.ToArray()));
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "Analysis failed",
                detail: ex.Message,
                statusCode: 502);
        }
    }

    private static decimal ComputeRsi(decimal[] closes, int period)
    {
        if (closes.Length < period + 1) return 50m;
        decimal gain = 0m, loss = 0m;
        for (int i = closes.Length - period; i < closes.Length; i++)
        {
            var diff = closes[i] - closes[i - 1];
            if (diff > 0) gain += diff;
            else loss += -diff;
        }
        if (loss == 0m) return 100m;
        var rs = gain / loss;
        return 100m - 100m / (1m + rs);
    }

    private static async Task<IResult> GetCandles(
        string symbol,
        IMarketDataService marketData,
        [FromQuery] string timeframe = "1h",
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return Results.BadRequest(new { error = "symbol is required" });

        symbol = symbol.ToUpperInvariant();
        limit = Math.Clamp(limit, 20, 1000);
        var allowedTimeframes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "5m", "15m", "30m", "1h", "4h", "1d", "1w",
        };
        if (!allowedTimeframes.Contains(timeframe))
            timeframe = "1h";

        try
        {
            var candles = await marketData.GetCandlesAsync(symbol, limit, "Crypto", timeframe);
            if (candles is null || candles.Count == 0)
                return Results.Ok(Array.Empty<MarketCandleDto>());

            var dtos = candles
                .OrderBy(c => c.Timestamp)
                .Select(c => new MarketCandleDto(
                    c.Timestamp.ToString("O"),
                    c.Open,
                    c.High,
                    c.Low,
                    c.Close,
                    c.Volume))
                .ToArray();

            return Results.Ok(dtos);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "Failed to load candles",
                detail: ex.Message,
                statusCode: 502);
        }
    }
}

public sealed record MarketSymbolDto(
    string Symbol,
    decimal LastPrice,
    decimal ChangePct24h,
    IReadOnlyList<decimal> Sparkline);

public sealed record MarketCandleDto(
    string Time,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

public sealed record OrderBookLevelDto(decimal Price, decimal Size, decimal Cumulative);

public sealed record OrderBookDto(
    string Symbol,
    decimal MidPrice,
    decimal SpreadBps,
    OrderBookLevelDto[] Bids,
    OrderBookLevelDto[] Asks,
    bool IsSynthetic);

public sealed record NarrativeDto(
    string Symbol,
    string Timeframe,
    DateTime GeneratedUtc,
    string Body,
    string Bias,
    string[] Bullets);

public sealed record ManualOrderDto(
    string Symbol,
    string Side,         // "long" | "short" | "buy" | "sell"
    string OrderType,    // "limit" | "market"
    decimal Quantity,
    decimal Entry,
    decimal? StopLoss,
    decimal? TakeProfit,
    decimal? Leverage,
    decimal RiskPct);

public sealed record ManualOrderResponseDto(
    Guid SignalId,
    bool Accepted,
    string Status,
    string Reasoning);

public sealed record AnalysisDto(
    string Symbol,
    string Timeframe,
    decimal LastPrice,
    decimal Rsi14,
    decimal Ma20,
    decimal Ma50,
    decimal Atr14,
    decimal AtrPct,
    string Regime,
    string Bias,
    decimal Volatility24h,
    string[] Notes);

