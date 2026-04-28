using System.Text.Json;
using System.Text.Json.Nodes;

namespace Listener.Api.Services;

/// <summary>
/// Persists a small whitelist of runtime-tunable settings to <c>appsettings.runtime.json</c>
/// in the Listener.Api content root. The file is added to the configuration pipeline with
/// <c>reloadOnChange: true</c>, so options bound via <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/>
/// will pick up changes automatically. Writes are fully serialised under a single lock
/// to avoid torn JSON if multiple operators tweak settings concurrently.
/// </summary>
public sealed class WritableSettingsStore
{
    private readonly string _path;
    private readonly ILogger<WritableSettingsStore> _logger;
    private readonly object _gate = new();

    public WritableSettingsStore(IWebHostEnvironment env, ILogger<WritableSettingsStore> logger)
    {
        _path = System.IO.Path.Combine(env.ContentRootPath, "appsettings.runtime.json");
        _logger = logger;
    }

    public string Path => _path;

    /// <summary>
    /// Whitelisted hierarchical keys the operator may tune at runtime. Keys are
    /// colon-delimited (matching .NET configuration paths). Anything outside
    /// this set is rejected.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, KeySpec> AllowedKeys =
        new Dictionary<string, KeySpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["Paper:Leverage:DefaultLeverage"] = KeySpec.Number(1, 15),
            ["Paper:Leverage:MaxLeverage"] = KeySpec.Number(1, 15),
            ["Paper:PortfolioLimits:MaxConcurrentPositions"] = KeySpec.Integer(1, 20),
            ["Paper:PortfolioLimits:MaxTotalExposurePercent"] = KeySpec.Number(1, 100),
            ["Risk:Supervisor:MaxDrawdownPercent"] = KeySpec.Number(0.5, 50),
            ["RiskGovernor:MaxRequestedLeverage"] = KeySpec.Number(1, 15),
            ["RiskGovernor:MaxRequestedLeverageCross"] = KeySpec.Number(1, 15),
            ["RiskGovernor:MaxRequestedLeverageIsolated"] = KeySpec.Number(1, 15),
            ["Strategy:PairsTrading:Enabled"] = KeySpec.Boolean(),
            ["Funding:Enabled"] = KeySpec.Boolean(),
            // ── Strategy threshold tuning (live-tunable from web UI) ──
            ["Strategy:PairsTrading:ZScoreEntry"] = KeySpec.Number(0.5, 5.0),
            ["Strategy:PairsTrading:ZScoreExit"]  = KeySpec.Number(0.05, 2.0),
            ["Strategy:PairsTrading:ZScoreStop"]  = KeySpec.Number(1.0, 6.0),
            ["Strategy:PairsTrading:MinCorrelation"]    = KeySpec.Number(0.0, 1.0),
            ["Strategy:PairsTrading:MaxHedgeRatio"]     = KeySpec.Number(1.0, 30.0),
            ["Strategy:PairsTrading:MinHedgeRatio"]     = KeySpec.Number(0.01, 1.0),
            ["Strategy:PairsTrading:CointegrationLookbackBars"] = KeySpec.Integer(60, 1000),
            ["Funding:EntryThresholdRate"] = KeySpec.Number(0.00001, 0.01),  // 0.1bp .. 100bp
            ["Funding:Long:EntryThresholdRate"]  = KeySpec.Number(0.00001, 0.01),
            ["Funding:Short:EntryThresholdRate"] = KeySpec.Number(0.00001, 0.01),
            ["Paper:DirectionalScannerEnabled"] = KeySpec.Boolean(),
            ["Funding:Scout:Enabled"] = KeySpec.Boolean(),
            ["Pairs:Discovery:Enabled"] = KeySpec.Boolean(),
        };

    public IReadOnlyDictionary<string, object?> ReadAll()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return new Dictionary<string, object?>();
            try
            {
                var json = File.ReadAllText(_path);
                using var doc = JsonDocument.Parse(json);
                var flat = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                Flatten(doc.RootElement, "", flat);
                return flat;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WritableSettingsStore: failed to read {Path} — treating as empty.", _path);
                return new Dictionary<string, object?>();
            }
        }
    }

    public WriteResult ApplyPatch(IDictionary<string, JsonElement> patch)
    {
        var rejected = new Dictionary<string, string>();
        var accepted = new Dictionary<string, object?>();

        foreach (var (key, value) in patch)
        {
            if (!AllowedKeys.TryGetValue(key, out var spec))
            {
                rejected[key] = "Key not in writable whitelist.";
                continue;
            }
            if (!spec.TryCoerce(value, out var coerced, out var error))
            {
                rejected[key] = error;
                continue;
            }
            accepted[key] = coerced;
        }

        if (accepted.Count == 0)
        {
            return new WriteResult(0, accepted, rejected);
        }

        lock (_gate)
        {
            JsonObject root;
            if (File.Exists(_path))
            {
                try
                {
                    var existing = File.ReadAllText(_path);
                    root = JsonNode.Parse(existing) as JsonObject ?? new JsonObject();
                }
                catch
                {
                    root = new JsonObject();
                }
            }
            else
            {
                root = new JsonObject();
            }

            foreach (var (key, value) in accepted)
            {
                SetNested(root, key.Split(':'), value);
            }

            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_path, root.ToJsonString(opts));
            _logger.LogInformation(
                "WritableSettingsStore: applied {N} setting(s) to {Path}: {Keys}",
                accepted.Count, _path, string.Join(", ", accepted.Keys));
        }

        return new WriteResult(accepted.Count, accepted, rejected);
    }

    private static void SetNested(JsonObject root, string[] segments, object? value)
    {
        JsonObject cursor = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            if (cursor[seg] is JsonObject next)
            {
                cursor = next;
            }
            else
            {
                var created = new JsonObject();
                cursor[seg] = created;
                cursor = created;
            }
        }

        var leaf = segments[^1];
        cursor[leaf] = value switch
        {
            null => null,
            bool b => JsonValue.Create(b),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            decimal dec => JsonValue.Create(dec),
            string s => JsonValue.Create(s),
            _ => JsonValue.Create(value.ToString())
        };
    }

    private static void Flatten(JsonElement el, string prefix, Dictionary<string, object?> sink)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}:{prop.Name}";
                    Flatten(prop.Value, key, sink);
                }
                break;
            case JsonValueKind.Array:
                int idx = 0;
                foreach (var item in el.EnumerateArray())
                {
                    Flatten(item, $"{prefix}:{idx++}", sink);
                }
                break;
            case JsonValueKind.String:
                sink[prefix] = el.GetString();
                break;
            case JsonValueKind.Number:
                if (el.TryGetInt64(out var l)) sink[prefix] = l;
                else sink[prefix] = el.GetDouble();
                break;
            case JsonValueKind.True:
                sink[prefix] = true;
                break;
            case JsonValueKind.False:
                sink[prefix] = false;
                break;
            case JsonValueKind.Null:
                sink[prefix] = null;
                break;
        }
    }
}

public sealed record WriteResult(
    int AcceptedCount,
    IReadOnlyDictionary<string, object?> Accepted,
    IReadOnlyDictionary<string, string> Rejected);

public sealed class KeySpec
{
    public string Kind { get; private init; } = "string";
    public double? Min { get; private init; }
    public double? Max { get; private init; }

    public static KeySpec Integer(int min, int max) =>
        new() { Kind = "int", Min = min, Max = max };

    public static KeySpec Number(double min, double max) =>
        new() { Kind = "number", Min = min, Max = max };

    public static KeySpec Boolean() => new() { Kind = "bool" };

    public bool TryCoerce(JsonElement el, out object? coerced, out string error)
    {
        coerced = null;
        error = "";
        switch (Kind)
        {
            case "bool":
                if (el.ValueKind == JsonValueKind.True) { coerced = true; return true; }
                if (el.ValueKind == JsonValueKind.False) { coerced = false; return true; }
                if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var bs))
                { coerced = bs; return true; }
                error = "Expected boolean."; return false;

            case "int":
                {
                    long v;
                    if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out v)) { }
                    else if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out v)) { }
                    else { error = "Expected integer."; return false; }
                    if (Min is not null && v < (long)Min.Value) { error = $"Below min {Min}."; return false; }
                    if (Max is not null && v > (long)Max.Value) { error = $"Above max {Max}."; return false; }
                    coerced = (int)v;
                    return true;
                }

            case "number":
                {
                    double v;
                    if (el.ValueKind == JsonValueKind.Number) v = el.GetDouble();
                    else if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), out v)) { }
                    else { error = "Expected number."; return false; }
                    if (Min is not null && v < Min) { error = $"Below min {Min}."; return false; }
                    if (Max is not null && v > Max) { error = $"Above max {Max}."; return false; }
                    coerced = v;
                    return true;
                }

            default:
                if (el.ValueKind == JsonValueKind.String) { coerced = el.GetString(); return true; }
                error = "Expected string."; return false;
        }
    }
}
