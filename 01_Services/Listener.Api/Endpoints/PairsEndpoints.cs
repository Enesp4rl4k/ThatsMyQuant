using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace Listener.Api.Endpoints;

public static class PairsEndpoints
{
    private static readonly string[] DefaultUniverse =
    {
        "BTC-USDT-SWAP", "ETH-USDT-SWAP", "SOL-USDT-SWAP", "AVAX-USDT-SWAP",
        "LINK-USDT-SWAP", "DOT-USDT-SWAP", "TRX-USDT-SWAP", "XLM-USDT-SWAP",
        "ADA-USDT-SWAP", "MATIC-USDT-SWAP", "ATOM-USDT-SWAP", "NEAR-USDT-SWAP",
    };

    public static IEndpointRouteBuilder MapPairsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/pairs").WithTags("Pairs");

        group.MapPost("/discover", Discover);
        group.MapPost("/test", TestPair);
        group.MapGet("/active", GetActivePairs);
        group.MapGet("/discoveries", GetDiscoveries);

        return endpoints;
    }

    /// <summary>
    /// Reads the `pairs_discovery_candidates.json` file written by
    /// `PairsDiscoveryHostedService` (every 6 hours) from LocalAppData and
    /// returns the parsed candidate list. Read-only, no recompute — the
    /// hosted service is the canonical writer.
    /// </summary>
    private static async Task<IResult> GetDiscoveries(CancellationToken ct)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant");
        var file = Path.Combine(dir, "pairs_discovery_candidates.json");

        if (!File.Exists(file))
        {
            return Results.Ok(new
            {
                generatedUtc = (DateTime?)null,
                timeframe    = (string?)null,
                lookbackBars = 0,
                universeCount = 0,
                candidates = Array.Empty<object>(),
                note = "No discovery candidates yet. PairsDiscoveryHostedService runs every 6 hours.",
            });
        }

        try
        {
            await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(fs, cancellationToken: ct).ConfigureAwait(false);
            // Pass through verbatim — schema matches what the writer emits.
            return Results.Content(doc.RootElement.GetRawText(), "application/json");
        }
        catch (Exception ex)
        {
            return Results.Problem(title: "Could not read discoveries", detail: ex.Message, statusCode: 500);
        }
    }

    private static async Task<IResult> GetActivePairs(
        IConfiguration config,
        IHistoricalMarketDataService history,
        CancellationToken ct)
    {
        var timeframe = config["Strategy:PairsTrading:Timeframe"] ?? "4h";
        var entryZ = double.TryParse(config["Strategy:PairsTrading:ZScoreEntry"], out var ez) ? ez : 2.0;
        var exitZ = double.TryParse(config["Strategy:PairsTrading:ZScoreExit"], out var xz) ? xz : 0.3;
        var stopZ = double.TryParse(config["Strategy:PairsTrading:ZScoreStop"], out var sz) ? sz : 3.0;

        var pairs = ResolveConfiguredPairs(config);
        if (pairs.Count == 0)
        {
            return Results.Ok(new ActivePairsResponse(
                Timeframe: timeframe,
                EntryZ: entryZ,
                ExitZ: exitZ,
                StopZ: stopZ,
                Pairs: Array.Empty<ActivePairSnapshot>()));
        }

        var endUtc = DateTime.UtcNow;
        var startUtc = endUtc.AddDays(-90);
        var results = new List<ActivePairSnapshot>(pairs.Count);

        foreach (var (y, x) in pairs)
        {
            try
            {
                var yc = await history.GetHistoricalCandlesAsync(y, timeframe, startUtc, endUtc, ct).ConfigureAwait(false);
                var xc = await history.GetHistoricalCandlesAsync(x, timeframe, startUtc, endUtc, ct).ConfigureAwait(false);
                if (yc.Count < 30 || xc.Count < 30)
                {
                    results.Add(ActivePairSnapshot.Failed(y, x, "Insufficient candles"));
                    continue;
                }

                var min = Math.Min(yc.Count, xc.Count);
                var ySeries = yc.Skip(yc.Count - min).Select(c => c.Close).ToList();
                var xSeries = xc.Skip(xc.Count - min).Select(c => c.Close).ToList();

                var diag = EngleGrangerDetector.TestCointegration(ySeries, xSeries);
                var beta = diag.HedgeRatio;
                var spreadNow = ySeries[^1] - beta * xSeries[^1];
                var stdDev = diag.SpreadStdDev <= 0m ? 1m : diag.SpreadStdDev;
                var z = (double)((spreadNow - diag.SpreadAverage) / stdDev);

                var state = Math.Abs(z) >= stopZ
                    ? "Stop"
                    : Math.Abs(z) <= exitZ
                        ? "AtMean"
                        : Math.Abs(z) >= entryZ
                            ? (z > 0 ? "ShortSpread" : "LongSpread")
                            : "Neutral";

                // Build a 60-bar tail of z-scores for the dashboard sparkline.
                var tail = Math.Min(60, min);
                var zTail = new double[tail];
                var stdD = (double)stdDev;
                var avg = (double)diag.SpreadAverage;
                for (int i = 0; i < tail; i++)
                {
                    var idx = min - tail + i;
                    var sp = (double)(ySeries[idx] - beta * xSeries[idx]);
                    zTail[i] = stdD == 0 ? 0 : (sp - avg) / stdD;
                }

                results.Add(new ActivePairSnapshot(
                    SymbolY: y,
                    SymbolX: x,
                    IsCointegrated: diag.IsCointegrated,
                    HedgeRatio: beta,
                    HalfLife: diag.HalfLife,
                    HurstExponent: diag.HurstExponent,
                    Correlation: diag.Correlation,
                    AdfTStat: diag.AdfTStatistic,
                    CurrentZ: z,
                    State: state,
                    SpreadAverage: diag.SpreadAverage,
                    SpreadStdDev: stdDev,
                    ZTail: zTail,
                    Error: null));
            }
            catch (Exception ex)
            {
                results.Add(ActivePairSnapshot.Failed(y, x, ex.Message));
            }
        }

        return Results.Ok(new ActivePairsResponse(
            Timeframe: timeframe,
            EntryZ: entryZ,
            ExitZ: exitZ,
            StopZ: stopZ,
            Pairs: results));
    }

    private static List<(string Y, string X)> ResolveConfiguredPairs(IConfiguration config)
    {
        var pairs = new List<(string, string)>();
        var section = config.GetSection("Strategy:PairsTrading:AllowedPairs");
        if (section.Exists())
        {
            foreach (var item in section.GetChildren())
            {
                var y = item["SymbolY"];
                var x = item["SymbolX"];
                if (!string.IsNullOrWhiteSpace(y) && !string.IsNullOrWhiteSpace(x))
                {
                    pairs.Add((y, x));
                }
            }
        }
        var csv = config["Strategy:PairsTrading:AllowedPairsCsv"];
        if (!string.IsNullOrWhiteSpace(csv))
        {
            foreach (var raw in csv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = raw.Split('/', '|', ',');
                if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    pairs.Add((parts[0].Trim(), parts[1].Trim()));
                }
            }
        }
        return pairs;
    }

    private static async Task<IResult> Discover(
        DiscoverRequest req,
        PairsDiscoveryService discovery,
        IHistoricalMarketDataService history,
        CancellationToken ct)
    {
        var symbols = (req.Symbols is { Length: > 0 } ? req.Symbols : DefaultUniverse).ToArray();
        var timeframe = string.IsNullOrWhiteSpace(req.Timeframe) ? "4H" : req.Timeframe;
        var days = req.Days <= 0 ? 180 : req.Days;
        var topN = req.TopN <= 0 ? 10 : req.TopN;

        var endUtc = DateTime.UtcNow;
        var startUtc = endUtc.AddDays(-days);

        var candles = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sym in symbols)
        {
            try
            {
                var data = await history.GetHistoricalCandlesAsync(sym, timeframe, startUtc, endUtc, ct).ConfigureAwait(false);
                if (data.Count == 0) continue;
                candles[sym] = data.Select(c => c.Close).ToList();
            }
            catch
            {
                // Skip the symbol on fetch failure; discovery proceeds with what we have.
            }
        }

        if (candles.Count < 2)
        {
            return Results.BadRequest(new { error = "Need at least 2 symbols with data." });
        }

        // Pairs Discovery requires aligned series — use the shortest length and trim from the end.
        var minLen = candles.Values.Min(v => v.Count);
        var aligned = candles.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<decimal>)kv.Value.Skip(kv.Value.Count - minLen).ToList(),
            StringComparer.OrdinalIgnoreCase);

        var found = discovery.Discover(aligned, topN);
        var dtos = found.Select(p => MapCandidate(p)).ToList();

        return Results.Ok(new DiscoverResponse(
            UniverseSize: aligned.Count,
            Bars: minLen,
            Timeframe: timeframe,
            Candidates: dtos));
    }

    private static async Task<IResult> TestPair(
        TestPairRequest req,
        IHistoricalMarketDataService history,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.SymbolY) || string.IsNullOrWhiteSpace(req.SymbolX))
        {
            return Results.BadRequest(new { error = "symbolY and symbolX required" });
        }

        var timeframe = string.IsNullOrWhiteSpace(req.Timeframe) ? "4H" : req.Timeframe;
        var days = req.Days <= 0 ? 180 : req.Days;
        var endUtc = DateTime.UtcNow;
        var startUtc = endUtc.AddDays(-days);

        var yCandles = await history.GetHistoricalCandlesAsync(req.SymbolY, timeframe, startUtc, endUtc, ct).ConfigureAwait(false);
        var xCandles = await history.GetHistoricalCandlesAsync(req.SymbolX, timeframe, startUtc, endUtc, ct).ConfigureAwait(false);

        if (yCandles.Count < 30 || xCandles.Count < 30)
        {
            return Results.BadRequest(new { error = "Insufficient candle history for one or both symbols." });
        }

        var minLen = Math.Min(yCandles.Count, xCandles.Count);
        var ySeries = yCandles.Skip(yCandles.Count - minLen).Select(c => c.Close).ToList();
        var xSeries = xCandles.Skip(xCandles.Count - minLen).Select(c => c.Close).ToList();
        var times = yCandles.Skip(yCandles.Count - minLen).Select(c => c.Timestamp).ToList();

        var diag = EngleGrangerDetector.TestCointegration(ySeries, xSeries);
        var beta = diag.HedgeRatio;

        // Build spread series Y - β·X with z-score for charting.
        var spread = new decimal[minLen];
        for (int i = 0; i < minLen; i++)
        {
            spread[i] = ySeries[i] - beta * xSeries[i];
        }

        var spreadAvg = diag.SpreadAverage;
        var spreadStd = diag.SpreadStdDev <= 0m ? 1m : diag.SpreadStdDev;
        var z = new decimal[minLen];
        for (int i = 0; i < minLen; i++)
        {
            z[i] = (spread[i] - spreadAvg) / spreadStd;
        }

        var dto = new PairTestResponse(
            SymbolY: req.SymbolY,
            SymbolX: req.SymbolX,
            Timeframe: timeframe,
            Bars: minLen,
            Diagnostics: MapDiagnostics(diag),
            QualityScore: PairsDiscoveryService.ComputeQualityScore(diag),
            Spread: spread,
            ZScore: z,
            Timestamps: times);

        return Results.Ok(dto);
    }

    private static PairCandidateDto MapCandidate(PairCandidate p) => new(
        SymbolY: p.SymbolY,
        SymbolX: p.SymbolX,
        QualityScore: p.QualityScore,
        Diagnostics: MapDiagnostics(p.Diagnostics));

    private static CointegrationDiagnosticsDto MapDiagnostics(EngleGrangerDetector.CointegrationResult r) => new(
        IsCointegrated: r.IsCointegrated,
        HedgeRatio: r.HedgeRatio,
        SpreadAverage: r.SpreadAverage,
        SpreadStdDev: r.SpreadStdDev,
        AdfTestStatistic: r.AdfTestStatistic,
        AdfTStatistic: r.AdfTStatistic,
        HalfLife: r.HalfLife,
        Correlation: r.Correlation,
        HurstExponent: r.HurstExponent,
        OuKappa: r.OuKappa,
        OuTheta: r.OuTheta,
        OuSigma: r.OuSigma);
}

public sealed record ActivePairsResponse(
    string Timeframe,
    double EntryZ,
    double ExitZ,
    double StopZ,
    IReadOnlyList<ActivePairSnapshot> Pairs);

public sealed record ActivePairSnapshot(
    string SymbolY,
    string SymbolX,
    bool IsCointegrated,
    decimal HedgeRatio,
    double HalfLife,
    double HurstExponent,
    double Correlation,
    double AdfTStat,
    double CurrentZ,
    string State,
    decimal SpreadAverage,
    decimal SpreadStdDev,
    double[] ZTail,
    string? Error)
{
    public static ActivePairSnapshot Failed(string y, string x, string err) =>
        new(y, x, false, 0m, 0, 0, 0, 0, 0, "Error", 0m, 0m, Array.Empty<double>(), err);
}

public sealed record DiscoverRequest(string[]? Symbols, string? Timeframe, int Days, int TopN);
public sealed record DiscoverResponse(int UniverseSize, int Bars, string Timeframe, IReadOnlyList<PairCandidateDto> Candidates);

public sealed record TestPairRequest(string SymbolY, string SymbolX, string? Timeframe, int Days);
public sealed record PairTestResponse(
    string SymbolY,
    string SymbolX,
    string Timeframe,
    int Bars,
    CointegrationDiagnosticsDto Diagnostics,
    double QualityScore,
    decimal[] Spread,
    decimal[] ZScore,
    IReadOnlyList<DateTime> Timestamps);

public sealed record PairCandidateDto(
    string SymbolY,
    string SymbolX,
    double QualityScore,
    CointegrationDiagnosticsDto Diagnostics);

public sealed record CointegrationDiagnosticsDto(
    bool IsCointegrated,
    decimal HedgeRatio,
    decimal SpreadAverage,
    decimal SpreadStdDev,
    decimal AdfTestStatistic,
    double AdfTStatistic,
    double HalfLife,
    double Correlation,
    double HurstExponent,
    double OuKappa,
    double OuTheta,
    double OuSigma);
