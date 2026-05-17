using System.Text.Json;

namespace Listener.Api.Endpoints;

/// <summary>
/// 2026-05-11 — EdgeLab Live observability endpoint.
///
/// Aggregates the various state JSON files written by the worker's monitoring
/// hosted services (MakerFillRatio, FundingFeeRatio, PairwiseCorrelation, HRP,
/// kill switch) plus per-strategy signal staleness/last-target-weight into a
/// single payload the web UI polls every ~15s.
///
/// Read-only. No mutation. Returns 200 with partial payload if a state file
/// is missing — the dashboard handles nulls.
/// </summary>
public static class EdgeLabEndpoints
{
    public static IEndpointRouteBuilder MapEdgeLabEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/edgelab").WithTags("EdgeLab");
        group.MapGet("/live", GetLiveSnapshot);
        return endpoints;
    }

    private static IResult GetLiveSnapshot(IConfiguration configuration)
    {
        var stateDir = ResolveStateDir(configuration);
        var inputDir = configuration["EdgeLabSingleAsset:InputDir"];
        var strategies = configuration
            .GetSection("EdgeLabSingleAsset:Strategies").Get<string[]>() ?? Array.Empty<string>();
        var overridesSection = configuration.GetSection("EdgeLabSingleAsset:StrategyOverrides");

        // 2026-05-11 — Calibration data: Quantlab's sanity_realized_vs_backtest.py
        // produces <QuantlabRoot>/reports/calibration/<strategy>_calibration.json.
        // We surface those records per strategy so the dashboard shows realized
        // Sharpe + promotion-gate pass/fail without a separate page.
        var quantlabRoot = configuration["Quantlab:Root"] ?? "C:/Users/parlak/Downloads/Quantlab";
        var calibrationDir = Path.Combine(quantlabRoot, "reports", "calibration");

        var perStrategy = strategies.Select(name =>
        {
            var ovr = overridesSection.GetSection(name);
            var weight = ovr["Weight"];
            var promotionStatus = ovr["PromotionStatus"];
            var staleAlertHours = ovr["StaleAlertHours"];

            // Read latest.json for each strategy (signal age + target weights).
            DateTimeOffset? generatedAt = null;
            DateTimeOffset? asOfBar = null;
            Dictionary<string, double>? targetWeights = null;
            string? state = null;
            double? expectedSharpeAnn = null;

            if (!string.IsNullOrWhiteSpace(inputDir))
            {
                var path = Path.Combine(inputDir, name, "latest.json");
                if (File.Exists(path))
                {
                    try
                    {
                        using var fs = File.OpenRead(path);
                        using var doc = JsonDocument.Parse(fs);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("generated_at", out var ga) &&
                            ga.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(ga.GetString(), out var gaParsed))
                            generatedAt = gaParsed;
                        if (root.TryGetProperty("as_of_bar", out var ab) &&
                            ab.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(ab.GetString(), out var abParsed))
                            asOfBar = abParsed;
                        if (root.TryGetProperty("target_weights", out var tw) &&
                            tw.ValueKind == JsonValueKind.Object)
                        {
                            targetWeights = new Dictionary<string, double>();
                            foreach (var p in tw.EnumerateObject())
                            {
                                if (p.Value.ValueKind == JsonValueKind.Number)
                                    targetWeights[p.Name] = p.Value.GetDouble();
                            }
                        }
                        if (root.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object)
                        {
                            if (md.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String)
                                state = st.GetString();
                            if (md.TryGetProperty("expected_sharpe_ann", out var es) &&
                                es.ValueKind == JsonValueKind.Number)
                                expectedSharpeAnn = es.GetDouble();
                        }
                    }
                    catch { /* tolerate parse errors */ }
                }
            }

            var signalAgeSec = generatedAt is null
                ? (double?)null
                : (DateTimeOffset.UtcNow - generatedAt.Value).TotalSeconds;
            int? staleHours = int.TryParse(staleAlertHours, out var sah) ? sah : null;
            bool isStale = signalAgeSec.HasValue && staleHours.HasValue &&
                signalAgeSec.Value > staleHours.Value * 3600.0;

            var calibration = ReadJsonFile(Path.Combine(calibrationDir, $"{name}_calibration.json"));

            return new
            {
                name,
                weight = decimal.TryParse(weight, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var w) ? (decimal?)w : null,
                promotion_status = promotionStatus,
                stale_alert_hours = staleHours,
                generated_at = generatedAt,
                as_of_bar = asOfBar,
                signal_age_sec = signalAgeSec,
                state,
                expected_sharpe_ann = expectedSharpeAnn,
                target_weights = targetWeights,
                stale = isStale,
                calibration
            };
        }).ToList();

        return Results.Ok(new
        {
            computed_at_utc = DateTime.UtcNow,
            state_dir = stateDir,
            strategies = perStrategy,
            maker_fill_ratio = ReadJsonFile(Path.Combine(stateDir, "maker_fill_ratio.json")),
            funding_fee_ratio = ReadJsonFile(Path.Combine(stateDir, "funding_fee_ratio.json")),
            pairwise_correlation = ReadJsonFile(Path.Combine(stateDir, "pairwise_correlation.json")),
            hrp_allocator = ReadJsonFile(Path.Combine(stateDir, "hrp_allocator.json")),
            edge_kill_switch = ReadJsonFile(Path.Combine(stateDir, "edge_kill_switch.json"))
        });
    }

    private static string ResolveStateDir(IConfiguration cfg)
    {
        var dir = cfg["StateDirectory"];
        if (!string.IsNullOrWhiteSpace(dir))
            return Environment.ExpandEnvironmentVariables(dir);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant");
    }

    private static object? ReadJsonFile(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs);
            // Re-serialise to a generic object the JSON pipeline will preserve.
            return JsonSerializer.Deserialize<JsonElement>(doc.RootElement.GetRawText());
        }
        catch
        {
            return null;
        }
    }
}

