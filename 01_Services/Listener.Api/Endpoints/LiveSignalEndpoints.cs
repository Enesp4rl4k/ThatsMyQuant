using System.Collections.Concurrent;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;

namespace Listener.Api.Endpoints;

/// <summary>
/// Live Signal Explorer endpoints — surface "what's happening RIGHT NOW" for each
/// configured symbol/pair so the operator can see why a trade did or didn't fire
/// without having to grep through CLEF logs. Powers a Web UI page that updates every
/// 15s via polling (SignalR push can be added later if performance demands).
///
/// Per-symbol results are cached for <see cref="CacheSeconds"/> seconds because
/// re-running the full backtest runner on every poll would saturate market data calls
/// and burn CPU. 30-second cache is fresh enough for human consumption.
/// </summary>
public static class LiveSignalEndpoints
{
    private const int CacheSeconds = 30;
    private static readonly ConcurrentDictionary<string, (DateTime AsOfUtc, FundingLiveSignalRowDto Row)> _fundingCache = new();
    private static readonly ConcurrentDictionary<string, (DateTime AsOfUtc, PairsLiveSignalRowDto Row)> _pairsCache = new();

    public static IEndpointRouteBuilder MapLiveSignalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/live-signals").WithTags("LiveSignals");

        group.MapGet("/funding", GetFundingSignals);
        group.MapGet("/pairs", GetPairsSignals);
        group.MapGet("/summary", GetSummary);

        return endpoints;
    }

    private static async Task<IResult> GetFundingSignals(
        IConfiguration cfg,
        IFundingRateProvider fundingProvider,
        IMarketDataService marketData,
        FundingAlphaBacktestRunner runner,
        string? symbols,
        CancellationToken ct)
    {
        var allocCsv = cfg["Funding:AllocationCsv"] ?? "LTCUSDT:0.13,DOGEUSDT:0.12";
        var watchlistCsv = cfg["Funding:Scout:WatchlistCsv"] ?? "LTCUSDT,DOGEUSDT,AAVEUSDT,LINKUSDT";

        var staticSymbols = ParseAllocation(allocCsv);
        var scoutSymbols = watchlistCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allSyms = string.IsNullOrWhiteSpace(symbols)
            ? staticSymbols.Keys.Concat(scoutSymbols).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var entryThreshold = ParseDecimal(cfg["Funding:EntryThresholdRate"], 0.0001m);
        var timeframe = cfg["Funding:Timeframe"] ?? "1h";
        var lookback = int.TryParse(cfg["Funding:CandleLookback"], out var lb) && lb > 0 ? lb : 300;

        var fcfg = new FundingAlphaConfig(
            EntryThreshold: entryThreshold,
            ExitThreshold: ParseDecimal(cfg["Funding:ExitThresholdRate"], 0.00005m),
            TakeProfitPct: ParseDecimal(cfg["Funding:TakeProfitPct"], 0.02m),
            StopLossPct: ParseDecimal(cfg["Funding:StopLossPct"], 0.015m),
            MaxHoldingBars: int.TryParse(cfg["Funding:MaxHoldingBars"], out var mh) && mh > 0 ? mh : 16);

        var rows = new List<FundingLiveSignalRowDto>();
        var nowUtc = DateTime.UtcNow;

        foreach (var sym in allSyms)
        {
            if (ct.IsCancellationRequested) break;

            // Cache check
            if (_fundingCache.TryGetValue(sym, out var cached) && (nowUtc - cached.AsOfUtc).TotalSeconds < CacheSeconds)
            {
                rows.Add(cached.Row with { CacheHit = true });
                continue;
            }

            try
            {
                decimal rate = 0m;
                try { rate = fundingProvider.GetRate(sym, nowUtc); } catch { /* leave 0 */ }
                var rateBps = rate * 10_000m;

                string verdict;
                string verdictReason;
                int? signalCount = null;
                double? backtestSharpe = null;
                decimal? backtestPf = null;
                int? backtestTrades = null;

                var candles = await marketData.GetCandlesAsync(sym, lookback, "Crypto", timeframe).ConfigureAwait(false);
                if (candles == null || candles.Count < 60)
                {
                    verdict = "INSUFFICIENT_DATA";
                    verdictReason = $"Only {candles?.Count ?? 0} candles available (need 60+).";
                }
                else
                {
                    var result = await runner.RunAsync(sym, candles, fcfg, 10_000m, ct).ConfigureAwait(false);
                    backtestPf = (decimal)result.ProfitFactor;
                    backtestSharpe = result.SharpeRatio;
                    backtestTrades = result.TotalTrades;
                    signalCount = result.EntrySignals;

                    var sig = result.LiveSignal;
                    var role = staticSymbols.ContainsKey(sym) ? "STATIC" : (scoutSymbols.Contains(sym) ? "SCOUT" : "OTHER");

                    verdict = sig.Kind switch
                    {
                        FundingLiveSignalKind.WouldOpenNew => "WOULD_OPEN",
                        FundingLiveSignalKind.WouldClose => "WOULD_CLOSE",
                        FundingLiveSignalKind.HoldOpen => "HOLD_OPEN",
                        _ => "NO_SIGNAL"
                    };
                    if (verdict == "NO_SIGNAL")
                    {
                        var absRate = Math.Abs(rate);
                        if (absRate < entryThreshold)
                            verdictReason = $"Funding {rateBps:+0.0;-0.0}bp below entry threshold ±{entryThreshold * 10_000m:F1}bp.";
                        else if (result.TotalTrades < 5)
                            verdictReason = $"Funding {rateBps:+0.0;-0.0}bp ABOVE threshold but backtest sample too small ({result.TotalTrades} trades) — runner needs more history.";
                        else if (result.ProfitFactor < 1.0m)
                            verdictReason = $"Funding above threshold but backtest negative-edge (PF={result.ProfitFactor:F2}) — runner refuses entry.";
                        else
                            verdictReason = $"Funding above threshold, runner held off — likely failed momentum/quality filter on the live bar.";
                    }
                    else
                    {
                        verdictReason = sig.Reasoning ?? string.Empty;
                    }
                }

                var row = new FundingLiveSignalRowDto(
                    Symbol: sym,
                    Role: staticSymbols.ContainsKey(sym) ? "STATIC" : (scoutSymbols.Contains(sym) ? "SCOUT" : "OTHER"),
                    CurrentFundingBps: rateBps,
                    EntryThresholdBps: entryThreshold * 10_000m,
                    DistanceFromEntryBps: Math.Abs(rateBps) - (entryThreshold * 10_000m),
                    Verdict: verdict,
                    VerdictReason: verdictReason,
                    BacktestPf: backtestPf,
                    BacktestSharpe: backtestSharpe,
                    BacktestTrades: backtestTrades,
                    EntrySignalsInWindow: signalCount,
                    AsOfUtc: nowUtc,
                    CacheHit: false);
                rows.Add(row);
                _fundingCache[sym] = (nowUtc, row);
            }
            catch (Exception ex)
            {
                rows.Add(new FundingLiveSignalRowDto(
                    Symbol: sym, Role: "ERROR", CurrentFundingBps: 0m,
                    EntryThresholdBps: entryThreshold * 10_000m, DistanceFromEntryBps: 0m,
                    Verdict: "ERROR", VerdictReason: ex.Message,
                    BacktestPf: null, BacktestSharpe: null, BacktestTrades: null, EntrySignalsInWindow: null,
                    AsOfUtc: nowUtc, CacheHit: false));
            }
        }

        return Results.Ok(rows.OrderByDescending(r => Math.Abs(r.CurrentFundingBps)));
    }

    private static async Task<IResult> GetPairsSignals(
        IConfiguration cfg,
        IMarketDataService marketData,
        CancellationToken ct)
    {
        var pairsCsv = cfg["Strategy:PairsTrading:AllowedPairsCsv"] ?? string.Empty;
        var timeframe = cfg["Strategy:PairsTrading:Timeframe"] ?? "4h";
        var lookback = int.TryParse(cfg["Strategy:PairsTrading:CandleLookback"], out var lb) && lb > 0 ? lb : 250;
        var minCorr = ParseDecimal(cfg["Strategy:PairsTrading:MinCorrelation"], 0.30m);
        var maxHurst = ParseDouble(cfg["Strategy:PairsTrading:MaxHurstExponent"], 0.45);
        var minHl = ParseDouble(cfg["Strategy:PairsTrading:MinHalfLifeBars"], 5);
        var maxHl = ParseDouble(cfg["Strategy:PairsTrading:MaxHalfLifeBars"], 80);
        var minBeta = ParseDecimal(cfg["Strategy:PairsTrading:MinHedgeRatio"], 0.10m);
        var maxBeta = ParseDecimal(cfg["Strategy:PairsTrading:MaxHedgeRatio"], 15m);

        var pairs = pairsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(p => p.Length >= 2)
            .Select(p => (Y: p[0], X: p[1]))
            .ToList();

        var rows = new List<PairsLiveSignalRowDto>();
        var nowUtc = DateTime.UtcNow;

        foreach (var (y, x) in pairs)
        {
            if (ct.IsCancellationRequested) break;
            var key = $"{y}:{x}";
            if (_pairsCache.TryGetValue(key, out var cached) && (nowUtc - cached.AsOfUtc).TotalSeconds < CacheSeconds)
            {
                rows.Add(cached.Row with { CacheHit = true });
                continue;
            }

            try
            {
                var candlesY = await marketData.GetCandlesAsync(y, lookback, "Crypto", timeframe).ConfigureAwait(false);
                var candlesX = await marketData.GetCandlesAsync(x, lookback, "Crypto", timeframe).ConfigureAwait(false);
                if (candlesY == null || candlesX == null || candlesY.Count < 100 || candlesX.Count < 100)
                {
                    var miss = new PairsLiveSignalRowDto(key, "INSUFFICIENT_DATA", $"Y={candlesY?.Count ?? 0} X={candlesX?.Count ?? 0} candles", null, null, null, null, null, null, "INSUFFICIENT_DATA", "Need 100+ aligned candles per leg.", nowUtc, false);
                    rows.Add(miss);
                    _pairsCache[key] = (nowUtc, miss);
                    continue;
                }

                // Direct cointegration test on aligned close-price arrays.
                var n = Math.Min(candlesY.Count, candlesX.Count);
                var ySeries = candlesY.Skip(candlesY.Count - n).Select(c => c.Close).ToArray();
                var xSeries = candlesX.Skip(candlesX.Count - n).Select(c => c.Close).ToArray();

                var coint = EngleGrangerDetector.TestCointegration(ySeries, xSeries);
                var corrDouble = ComputePearsonCorrelation(ySeries, xSeries);

                // Apply hybrid's admission gates.
                var absBeta = Math.Abs(coint.HedgeRatio);
                bool gateCorr = (decimal)Math.Abs(corrDouble) >= minCorr;
                bool gateHurst = coint.HurstExponent <= maxHurst;
                bool gateHl = coint.HalfLife >= minHl && coint.HalfLife <= maxHl;
                bool gateBeta = absBeta >= minBeta && absBeta <= maxBeta;
                bool isCointegrated = coint.IsCointegrated && gateCorr && gateHurst && gateHl && gateBeta;

                // Current spread + z-score
                decimal? currentZ = null;
                if (n >= 30)
                {
                    var spreads = new decimal[n];
                    for (int i = 0; i < n; i++) spreads[i] = ySeries[i] - coint.HedgeRatio * xSeries[i];
                    var window = spreads.Skip(Math.Max(0, n - 30)).ToArray();
                    var mean = window.Average();
                    var variance = window.Sum(v => (v - mean) * (v - mean)) / window.Length;
                    var std = (decimal)Math.Sqrt((double)variance);
                    if (std > 0) currentZ = (spreads[^1] - mean) / std;
                }

                string status, verdict, reason;
                if (!isCointegrated)
                {
                    status = "NOT_COINTEGRATED";
                    verdict = "NO_SIGNAL";
                    var fails = new List<string>();
                    if (!coint.IsCointegrated) fails.Add($"ADF t={coint.AdfTStatistic:F2} (need < critical)");
                    if (!gateCorr) fails.Add($"|corr|={Math.Abs(corrDouble):F2} < {minCorr}");
                    if (!gateHurst) fails.Add($"Hurst={coint.HurstExponent:F2} > {maxHurst:F2}");
                    if (!gateHl) fails.Add($"HL={coint.HalfLife:F1} ∉ [{minHl},{maxHl}]");
                    if (!gateBeta) fails.Add($"|β|={absBeta:F3} ∉ [{minBeta},{maxBeta}]");
                    reason = "Failed gate(s): " + string.Join("; ", fails);
                }
                else
                {
                    status = "COINTEGRATED";
                    if (currentZ.HasValue && Math.Abs(currentZ.Value) >= 2.0m)
                    {
                        verdict = "WOULD_OPEN";
                        reason = $"|z|={Math.Abs(currentZ.Value):F2} ≥ 2.0 entry threshold ({(currentZ.Value > 0 ? "Y rich, short Y / long X" : "Y cheap, long Y / short X")}).";
                    }
                    else
                    {
                        verdict = "NO_SIGNAL";
                        reason = currentZ.HasValue
                            ? $"Cointegrated but z={currentZ.Value:F2} in dead zone (|z| < 2.0)."
                            : "Cointegrated, z-score not computable from current window.";
                    }
                }

                var row = new PairsLiveSignalRowDto(
                    Pair: key,
                    Status: status,
                    StatusDetail: $"β={coint.HedgeRatio:F3} ADF t={coint.AdfTStatistic:F2} HL={coint.HalfLife:F1} corr={corrDouble:F2} Hurst={coint.HurstExponent:F2}",
                    Beta: coint.HedgeRatio,
                    AdfTStat: coint.AdfTStatistic,
                    HalfLife: coint.HalfLife,
                    Correlation: corrDouble,
                    CurrentZScore: currentZ,
                    BacktestPf: null, // skip — backtest is too heavy for live polling
                    Verdict: verdict,
                    VerdictReason: reason,
                    AsOfUtc: nowUtc,
                    CacheHit: false);
                rows.Add(row);
                _pairsCache[key] = (nowUtc, row);
            }
            catch (Exception ex)
            {
                rows.Add(new PairsLiveSignalRowDto(key, "ERROR", ex.Message, null, null, null, null, null, null, "ERROR", ex.Message, nowUtc, false));
            }
        }

        return Results.Ok(rows);
    }

    private static double ComputePearsonCorrelation(decimal[] a, decimal[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        if (n < 2) return 0;
        double sumA = 0, sumB = 0, sumAB = 0, sumA2 = 0, sumB2 = 0;
        for (int i = 0; i < n; i++)
        {
            double da = (double)a[i], db = (double)b[i];
            sumA += da; sumB += db; sumAB += da * db; sumA2 += da * da; sumB2 += db * db;
        }
        double cov = sumAB - sumA * sumB / n;
        double varA = sumA2 - sumA * sumA / n;
        double varB = sumB2 - sumB * sumB / n;
        double denom = Math.Sqrt(varA * varB);
        return denom > 0 ? cov / denom : 0;
    }

    private static double ParseDouble(string? raw, double fallback)
        => double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static IResult GetSummary(IConfiguration cfg)
    {
        return Results.Ok(new
        {
            asOfUtc = DateTime.UtcNow,
            funding = new
            {
                entryThresholdBps = ParseDecimal(cfg["Funding:EntryThresholdRate"], 0.0001m) * 10_000m,
                scoutThresholdBps = ParseDecimal(cfg["Funding:Scout:AlertThresholdRate"], 0.00005m) * 10_000m,
                hybridMode = cfg["Funding:HybridMode"] ?? "off",
                staticAllocation = cfg["Funding:AllocationCsv"] ?? string.Empty,
                opportunisticEnabled = bool.TryParse(cfg["Funding:Opportunistic:Enabled"], out var oe) && oe,
                opportunisticMaxPositions = int.TryParse(cfg["Funding:Opportunistic:MaxPositions"], out var omp) ? omp : 0
            },
            pairs = new
            {
                hybridMode = cfg["Strategy:PairsTrading:HybridMode"] ?? "off",
                allowedPairs = cfg["Strategy:PairsTrading:AllowedPairsCsv"] ?? string.Empty,
                timeframe = cfg["Strategy:PairsTrading:Timeframe"] ?? "4h"
            },
            cacheSeconds = CacheSeconds
        });
    }

    private static Dictionary<string, decimal> ParseAllocation(string csv)
    {
        var d = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && decimal.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var w))
                d[parts[0]] = w;
        }
        return d;
    }

    private static decimal ParseDecimal(string? raw, decimal fallback)
        => decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
}

public sealed record FundingLiveSignalRowDto(
    string Symbol,
    string Role,
    decimal CurrentFundingBps,
    decimal EntryThresholdBps,
    decimal DistanceFromEntryBps,
    string Verdict,
    string VerdictReason,
    decimal? BacktestPf,
    double? BacktestSharpe,
    int? BacktestTrades,
    int? EntrySignalsInWindow,
    DateTime AsOfUtc,
    bool CacheHit);

public sealed record PairsLiveSignalRowDto(
    string Pair,
    string Status,
    string StatusDetail,
    decimal? Beta,
    double? AdfTStat,
    double? HalfLife,
    double? Correlation,
    decimal? CurrentZScore,
    decimal? BacktestPf,
    string Verdict,
    string VerdictReason,
    DateTime AsOfUtc,
    bool CacheHit);
