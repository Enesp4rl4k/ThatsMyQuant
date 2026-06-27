using System.Text.Json;

namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// Outcome of parsing + validating <c>latest.json</c> against the EdgeLab
/// "Bot Integration Contract" (Quantlab signals/README.md). Each non-Ok variant
/// maps 1:1 to a row in that README's "Failure Modes" table — the bot must
/// hold positions and alert in every failure case rather than acting partially.
/// </summary>
public enum EdgeLabReadStatus
{
    Ok,
    Missing,            // file not present yet
    Malformed,          // unparseable JSON / missing required key
    SchemaMismatch,     // schema_version != 1 (contract pt 6)
    StrategyMismatch,   // doc.strategy != configured strategy
    TimeframeMismatch,  // metadata.timeframe != configured timeframe
    Stale,              // as_of_bar older than 2 * timeframe (contract pt 5)
    InsufficientBreadth,// n_symbols_eligible_at_bar < 2*k
    BadFactorValues,    // all-zero or NaN/Inf factor_values
    WeightAsymmetry     // sum(w)!=0 or sum(|w|)!=2 within tolerance — implicit beta exposure
}

public readonly record struct EdgeLabReadResult(
    EdgeLabReadStatus Status,
    EdgeLabSignalDocument? Document,
    string? FailureReason)
{
    public bool IsOk => Status == EdgeLabReadStatus.Ok;
}

/// <summary>
/// Pure read+validate pipeline for EdgeLab signal files. Stateless — all
/// freshness logic takes <c>now</c> as a parameter so tests can pin time.
/// The hosted service is the only thing that performs side-effects (orders,
/// alerts); this class only ever returns a tagged result.
/// </summary>
public static class EdgeLabSignalReader
{
    /// <summary>Resolve <c>{InputDir}/{Strategy}/latest.json</c>.</summary>
    public static string ResolveLatestPath(EdgeLabSignalOptions options) =>
        Path.Combine(options.InputDir, options.Strategy, "latest.json");

    public static EdgeLabReadResult Read(EdgeLabSignalOptions options, DateTimeOffset now)
    {
        var path = ResolveLatestPath(options);
        if (!File.Exists(path))
            return new EdgeLabReadResult(EdgeLabReadStatus.Missing, null, $"latest.json not found at {path}");

        EdgeLabSignalDocument? doc;
        try
        {
            using var fs = File.OpenRead(path);
            doc = JsonSerializer.Deserialize<EdgeLabSignalDocument>(fs);
        }
        catch (JsonException ex)
        {
            return new EdgeLabReadResult(EdgeLabReadStatus.Malformed, null, $"JSON parse error: {ex.Message}");
        }

        if (doc is null)
            return new EdgeLabReadResult(EdgeLabReadStatus.Malformed, null, "Deserialized document was null");

        return Validate(doc, options, now);
    }

    /// <summary>Run contract validations against an already-deserialised doc.
    /// Exposed so tests can supply hand-built documents without touching disk.</summary>
    public static EdgeLabReadResult Validate(EdgeLabSignalDocument doc, EdgeLabSignalOptions options, DateTimeOffset now)
    {
        // Contract pt 6 — schema_version == 1.
        if (doc.SchemaVersion is null)
            return new EdgeLabReadResult(EdgeLabReadStatus.Malformed, doc, "schema_version missing");
        if (doc.SchemaVersion != 1)
            return new EdgeLabReadResult(EdgeLabReadStatus.SchemaMismatch, doc, $"schema_version={doc.SchemaVersion}, expected 1");

        if (string.IsNullOrWhiteSpace(doc.Strategy))
            return new EdgeLabReadResult(EdgeLabReadStatus.Malformed, doc, "strategy missing");
        if (!string.Equals(doc.Strategy, options.Strategy, StringComparison.Ordinal))
            return new EdgeLabReadResult(EdgeLabReadStatus.StrategyMismatch, doc,
                $"doc.strategy={doc.Strategy}, configured={options.Strategy}");

        if (doc.AsOfBar is null)
            return new EdgeLabReadResult(EdgeLabReadStatus.Malformed, doc, "as_of_bar missing");
        if (doc.TargetWeights is null || doc.TargetWeights.Count == 0)
            return new EdgeLabReadResult(EdgeLabReadStatus.Malformed, doc, "target_weights missing or empty");
        if (doc.FactorValues is null || doc.FactorValues.Count == 0)
            return new EdgeLabReadResult(EdgeLabReadStatus.Malformed, doc, "factor_values missing or empty");
        if (doc.Metadata is null)
            return new EdgeLabReadResult(EdgeLabReadStatus.Malformed, doc, "metadata missing");

        if (!string.Equals(doc.Metadata.Timeframe, options.Timeframe, StringComparison.Ordinal))
            return new EdgeLabReadResult(EdgeLabReadStatus.TimeframeMismatch, doc,
                $"metadata.timeframe={doc.Metadata.Timeframe}, configured={options.Timeframe}");

        if (doc.Metadata.K is null or < 1)
            return new EdgeLabReadResult(EdgeLabReadStatus.Malformed, doc, "metadata.k missing or < 1");
        if (doc.Metadata.NSymbolsEligibleAtBar is null)
            return new EdgeLabReadResult(EdgeLabReadStatus.Malformed, doc, "metadata.n_symbols_eligible_at_bar missing");

        // Contract failure-mode row: n_symbols_eligible_at_bar < 2*k → refuse.
        var k = doc.Metadata.K.Value;
        if (doc.Metadata.NSymbolsEligibleAtBar.Value < 2 * k)
            return new EdgeLabReadResult(EdgeLabReadStatus.InsufficientBreadth, doc,
                $"n_symbols_eligible_at_bar={doc.Metadata.NSymbolsEligibleAtBar} < 2*k={2 * k}");

        // Contract failure-mode row: factor values all-zero / NaN.
        var anyFinite = false;
        var anyNonZero = false;
        foreach (var v in doc.FactorValues.Values)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                return new EdgeLabReadResult(EdgeLabReadStatus.BadFactorValues, doc,
                    "factor_values contain NaN/Inf");
            anyFinite = true;
            if (v != 0.0) anyNonZero = true;
        }
        if (!anyFinite || !anyNonZero)
            return new EdgeLabReadResult(EdgeLabReadStatus.BadFactorValues, doc,
                "factor_values are all zero");

        // Weight sanity — README §3 claims dollar-neutral 200% gross, k long + k short.
        // Without this gate, a producer bug (e.g. wrong sign on a leg) silently gives the
        // bot delta-1 market exposure. We never observe it because we trust the producer.
        // Tolerance allows JSON float rounding (1e-6 typical) but rejects asymmetric output.
        var sumW = 0.0;
        var sumAbsW = 0.0;
        foreach (var v in doc.TargetWeights.Values)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                return new EdgeLabReadResult(EdgeLabReadStatus.WeightAsymmetry, doc,
                    "target_weights contain NaN/Inf");
            sumW += v;
            sumAbsW += Math.Abs(v);
        }
        const double netTolerance = 1e-3;     // |Σw| ≤ 0.001 → at $5k basis, max $5 implicit directional bias
        const double grossTolerance = 1e-2;   // ||Σ|w|-expected| ≤ 0.01 → at most ±$50 of gross slippage
        // Expected gross is per-strategy (200% for classic XS, 50% for the $2K xs_momentum
        // panel). Sanity-floor a misconfigured non-positive value back to the 2.0 default.
        var expectedGross = options.ExpectedGross > 0 ? options.ExpectedGross : 2.0;
        if (Math.Abs(sumW) > netTolerance)
            return new EdgeLabReadResult(EdgeLabReadStatus.WeightAsymmetry, doc,
                $"sum(weights)={sumW:+0.000000;-0.000000;0} exceeds tolerance {netTolerance} (asymmetric, implies beta exposure)");
        if (Math.Abs(sumAbsW - expectedGross) > grossTolerance)
            return new EdgeLabReadResult(EdgeLabReadStatus.WeightAsymmetry, doc,
                $"sum(|weights|)={sumAbsW:0.000000} not within {grossTolerance} of {expectedGross:0.###} (gross exposure off contract)");

        // Contract pt 5 — staleness window. Original spec: 2 * timeframe. Updated 2026-05-03
        // to honour metadata.rebalance_every: max_age = 2 * rebalance_every * timeframe.
        // The reb=6 variant rebalances daily from 4h candles, so as_of_bar can legitimately
        // be up to 24h old (one full rebalance period); a 2*4h=8h cap would falsely reject
        // every signal it emits. Missing rebalance_every defaults to 1 → identical to old
        // behaviour for reb=1 producers (back-compat preserved).
        if (!TryParseTimeframe(options.Timeframe, out var tf))
            return new EdgeLabReadResult(EdgeLabReadStatus.Malformed, doc,
                $"unparseable timeframe '{options.Timeframe}'");

        var rebalanceEvery = doc.Metadata.RebalanceEvery ?? 1;
        if (rebalanceEvery < 1) rebalanceEvery = 1;  // sanity floor
        var maxAge = TimeSpan.FromTicks(tf.Ticks * 2L * rebalanceEvery);

        var age = now - doc.AsOfBar.Value;
        if (age > maxAge)
            return new EdgeLabReadResult(EdgeLabReadStatus.Stale, doc,
                $"as_of_bar={doc.AsOfBar:o} age={age} > 2*{rebalanceEvery}*{tf} (rebalance_every={rebalanceEvery})");

        return new EdgeLabReadResult(EdgeLabReadStatus.Ok, doc, null);
    }

    /// <summary>Parse short timeframe strings like "4h", "15m", "1d", "30s".
    /// Matches what EdgeLab emits in metadata.timeframe.</summary>
    public static bool TryParseTimeframe(string s, out TimeSpan span)
    {
        span = default;
        if (string.IsNullOrWhiteSpace(s) || s.Length < 2) return false;
        var unit = s[^1];
        if (!int.TryParse(s.AsSpan(0, s.Length - 1), out var n) || n <= 0) return false;
        span = unit switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => TimeSpan.Zero
        };
        return span > TimeSpan.Zero;
    }
}
