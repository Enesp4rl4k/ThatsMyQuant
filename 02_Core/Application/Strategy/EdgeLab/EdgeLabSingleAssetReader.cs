using System.Text.Json;

namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// Read + validate a single-asset (directional, non-beta-neutral) EdgeLab
/// signal. Schema is the same v1 contract used by XS strategies — only the
/// validation rules differ:
///
///   - target_weights MUST contain exactly 1 symbol
///   - metadata.strategy_kind MUST == "single_asset"
///   - the XS-specific weight-symmetry check (sum w == 0, sum |w| == 2) is SKIPPED
///   - the single weight magnitude is allowed in [0, leverage]
///   - n_symbols_* / k / reverse fields are ignored (XS-only)
///
/// Stale detection mirrors the XS reader: <c>as_of_bar</c> older than 2×
/// timeframe → <see cref="EdgeLabSingleAssetReadStatus.Stale"/>.
///
/// Added 2026-05-08 alongside MASTER_PLAN_2K pivot. The XS reader stays
/// untouched so existing strategies keep validating identically.
/// </summary>
public enum EdgeLabSingleAssetReadStatus
{
    Ok,
    Missing,
    Malformed,
    SchemaMismatch,         // schema_version != 1
    StrategyMismatch,       // doc.strategy != configured strategy
    KindMismatch,           // metadata.strategy_kind != "single_asset"
    Stale,                  // as_of_bar older than 2 * timeframe
    WeightShapeInvalid,     // !=1 symbol in target_weights, OR magnitude out of [0, leverage]
}

public readonly record struct EdgeLabSingleAssetReadResult(
    EdgeLabSingleAssetReadStatus Status,
    EdgeLabSignalDocument? Document,
    string? FailureReason,
    string? Symbol,
    double TargetWeight)
{
    public bool IsOk => Status == EdgeLabSingleAssetReadStatus.Ok;
}

public static class EdgeLabSingleAssetReader
{
    public static string ResolveLatestPath(string inputDir, string strategy) =>
        Path.Combine(inputDir, strategy, "latest.json");

    /// <summary>
    /// Bar size for a strategy, taken from its current signal's <c>metadata.timeframe</c>
    /// ("1h", "4h", "1d", "15m"). Drives the as_of+timeframe execution gate and the
    /// 2× staleness window, so a 4h/1d strategy must not run on the 1h default.
    /// Falls back when the file is missing, unreadable, or the value is unparseable.
    /// </summary>
    public static TimeSpan ResolveTimeframe(string inputDir, string strategy, TimeSpan fallback)
    {
        try
        {
            var path = ResolveLatestPath(inputDir, strategy);
            if (!File.Exists(path)) return fallback;
            using var fs = File.OpenRead(path);
            var doc = JsonSerializer.Deserialize<EdgeLabSignalDocument>(fs);
            return ParseTimeframe(doc?.Metadata?.Timeframe) ?? fallback;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    public static TimeSpan? ParseTimeframe(string? tf)
    {
        if (string.IsNullOrWhiteSpace(tf) || tf.Length < 2) return null;
        if (!int.TryParse(tf.AsSpan(0, tf.Length - 1), out var n) || n <= 0) return null;
        return char.ToLowerInvariant(tf[^1]) switch
        {
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => null,
        };
    }

    public static EdgeLabSingleAssetReadResult Read(
        string inputDir, string strategy, DateTimeOffset now,
        TimeSpan? timeframe = null)
    {
        var path = ResolveLatestPath(inputDir, strategy);
        if (!File.Exists(path))
            return new(EdgeLabSingleAssetReadStatus.Missing, null,
                $"latest.json not found at {path}", null, 0.0);

        EdgeLabSignalDocument? doc;
        try
        {
            using var fs = File.OpenRead(path);
            doc = JsonSerializer.Deserialize<EdgeLabSignalDocument>(fs);
        }
        catch (JsonException ex)
        {
            return new(EdgeLabSingleAssetReadStatus.Malformed, null,
                $"JSON parse error: {ex.Message}", null, 0.0);
        }

        if (doc is null)
            return new(EdgeLabSingleAssetReadStatus.Malformed, null,
                "Deserialized document was null", null, 0.0);

        return Validate(doc, strategy, now, timeframe);
    }

    public static EdgeLabSingleAssetReadResult Validate(
        EdgeLabSignalDocument doc, string strategy, DateTimeOffset now,
        TimeSpan? timeframe = null)
    {
        if (doc.SchemaVersion != 1)
            return new(EdgeLabSingleAssetReadStatus.SchemaMismatch, doc,
                $"schema_version={doc.SchemaVersion}, expected 1", null, 0.0);

        if (!string.Equals(doc.Strategy, strategy, StringComparison.Ordinal))
            return new(EdgeLabSingleAssetReadStatus.StrategyMismatch, doc,
                $"doc.strategy={doc.Strategy}, expected {strategy}", null, 0.0);

        var kind = doc.Metadata?.StrategyKind;
        if (!string.Equals(kind, "single_asset", StringComparison.Ordinal))
            return new(EdgeLabSingleAssetReadStatus.KindMismatch, doc,
                $"metadata.strategy_kind={kind}, expected single_asset", null, 0.0);

        if (doc.TargetWeights is null || doc.TargetWeights.Count != 1)
        {
            var n = doc.TargetWeights?.Count ?? 0;
            return new(EdgeLabSingleAssetReadStatus.WeightShapeInvalid, doc,
                $"target_weights has {n} entries, expected exactly 1", null, 0.0);
        }

        var kv = doc.TargetWeights.First();
        var leverage = doc.Metadata?.Leverage ?? 1.0;
        if (!double.IsFinite(kv.Value) || Math.Abs(kv.Value) > Math.Abs(leverage) + 1e-9)
        {
            return new(EdgeLabSingleAssetReadStatus.WeightShapeInvalid, doc,
                $"weight {kv.Value} out of [-{leverage}, +{leverage}]",
                kv.Key, kv.Value);
        }

        if (timeframe is { } tf && doc.AsOfBar is { } asOf)
        {
            var age = now - asOf;
            // 2x timeframe matches the XS reader's freshness rule. For event-
            // driven single-asset strategies (listing_drift, token_unlock) the
            // timeframe is the "expected refresh cadence" not literal bar size,
            // so config picks 1h or 4h depending on strategy.
            if (age > TimeSpan.FromTicks(tf.Ticks * 2))
            {
                return new(EdgeLabSingleAssetReadStatus.Stale, doc,
                    $"as_of_bar age {age} > 2× timeframe {tf}", kv.Key, kv.Value);
            }
        }

        return new(EdgeLabSingleAssetReadStatus.Ok, doc, null, kv.Key, kv.Value);
    }
}
