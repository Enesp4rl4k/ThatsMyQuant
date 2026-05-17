using System.Globalization;
using System.Text.Json;

namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// [Sprint 6 — 2026-05-10] Pure-function kill-switch math for the
/// Quantlab → TMQ integration contract (reports/deploy_runbook_2026_05_10/
/// TMQ_INTEGRATION_CONTRACT.md).
///
/// Each Quantlab signal carries two thresholds:
///   - <c>kill_switch_realized_sharpe_30cyc</c> — typically -0.3
///   - <c>kill_switch_realized_sharpe_hard</c>  — typically -1.0
///
/// TMQ tracks a rolling realized 30-cycle Sharpe (annualised using Quantlab's
/// <c>expected_cyc_per_yr</c> hint) per StrategyTag. The decision matrix:
///
///     realized30c >= soft_threshold  → multiplier = 1.0  (full Kelly)
///     hard <= realized30c < soft     → multiplier = 0.5  (half Kelly demote)
///     realized30c < hard             → multiplier = 0.0  (force dry-run)
///     n_cycles &lt; min_for_decision → multiplier = 1.0  (insufficient sample)
///
/// Pure-function: tested directly without hosted-service plumbing.
/// </summary>
public static class EdgeLabKillSwitch
{
    public const decimal FullKellyMultiplier = 1.0m;
    public const decimal HalfKellyMultiplier = 0.5m;
    public const decimal HardKillMultiplier = 0.0m;

    /// <summary>
    /// Minimum closed cycles required before the kill switch is allowed to fire.
    /// Lower than the contract's 30 because we need SOMETHING to act on if a
    /// strategy goes catastrophically negative early; default to 10 = Quantlab's
    /// MinPaperCyclesForLive promotion threshold.
    /// </summary>
    public const int MinCyclesForDecision = 10;

    public sealed record Decision(
        decimal Multiplier,
        decimal RealizedSharpeAnn,
        int NCycles,
        string Reason);

    /// <summary>
    /// Apply the contract's two-step kill switch given a sample of realized
    /// per-cycle returns (in fraction, e.g. 0.03 = +3%). Returns the multiplier
    /// to apply to the strategy's configured Weight.
    /// </summary>
    /// <param name="realizedPerCycleReturns">Fractional per-cycle returns (most-recent first or last — order-agnostic).</param>
    /// <param name="cyclesPerYear">Quantlab's <c>expected_cyc_per_yr</c> hint, used for Sharpe annualisation.</param>
    /// <param name="softThreshold">Half-Kelly trigger Sharpe (typically -0.3).</param>
    /// <param name="hardThreshold">Hard-kill trigger Sharpe (typically -1.0).</param>
    public static Decision Resolve(
        IReadOnlyList<decimal> realizedPerCycleReturns,
        double cyclesPerYear,
        double softThreshold,
        double hardThreshold)
    {
        if (realizedPerCycleReturns is null || realizedPerCycleReturns.Count < MinCyclesForDecision)
        {
            return new Decision(FullKellyMultiplier, 0m, realizedPerCycleReturns?.Count ?? 0,
                $"insufficient sample (n={realizedPerCycleReturns?.Count ?? 0} < {MinCyclesForDecision})");
        }

        // Limit to the last 30 cycles per the contract.
        var window = realizedPerCycleReturns.Count > 30
            ? realizedPerCycleReturns.Skip(realizedPerCycleReturns.Count - 30).ToList()
            : realizedPerCycleReturns.ToList();

        var n = window.Count;
        var meanFrac = window.Average();
        decimal varSum = 0m;
        foreach (var r in window) varSum += (r - meanFrac) * (r - meanFrac);
        var stdFrac = n > 1 ? Sqrt(varSum / (n - 1)) : 0m;

        decimal sharpePerCycle = stdFrac > 0m ? meanFrac / stdFrac : 0m;
        var sqrtCpy = (decimal)Math.Sqrt(Math.Max(1.0, cyclesPerYear));
        var sharpeAnn = sharpePerCycle * sqrtCpy;

        var sharpeAnnDouble = (double)sharpeAnn;
        if (sharpeAnnDouble < hardThreshold)
            return new Decision(HardKillMultiplier, sharpeAnn, n,
                $"realized Sharpe {sharpeAnnDouble:F2} < hard {hardThreshold:F2} → hard-kill");

        if (sharpeAnnDouble < softThreshold)
            return new Decision(HalfKellyMultiplier, sharpeAnn, n,
                $"realized Sharpe {sharpeAnnDouble:F2} < soft {softThreshold:F2} → half-Kelly");

        return new Decision(FullKellyMultiplier, sharpeAnn, n,
            $"realized Sharpe {sharpeAnnDouble:F2} >= soft {softThreshold:F2} → full Kelly");
    }

    private static decimal Sqrt(decimal v)
    {
        if (v <= 0m) return 0m;
        return (decimal)Math.Sqrt((double)v);
    }
}

/// <summary>
/// Per-strategy multiplier state, persisted as a single JSON document so the
/// hosted services and digests share a consistent view between ticks. Atomic
/// write via temp + move; lock guards in-process concurrent writers.
/// </summary>
public sealed class EdgeLabKillSwitchState
{
    private readonly object _lock = new();
    private readonly string _path;
    public string FilePath => _path;

    public EdgeLabKillSwitchState() : this(DefaultPath()) { }

    public EdgeLabKillSwitchState(string path)
    {
        _path = path;
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ThatsMyQuant",
        "edge_kill_switch.json");

    public sealed record StrategyState(
        string Strategy,
        decimal Multiplier,
        decimal RealizedSharpeAnn,
        int NCycles,
        string Reason,
        DateTime UpdatedUtc);

    public sealed record StateDocument(
        DateTime GeneratedUtc,
        Dictionary<string, StrategyState> Strategies);

    public StateDocument Read()
    {
        if (!File.Exists(_path))
            return new StateDocument(DateTime.UtcNow,
                new Dictionary<string, StrategyState>(StringComparer.OrdinalIgnoreCase));
        lock (_lock)
        {
            try
            {
                var json = File.ReadAllText(_path);
                var doc = JsonSerializer.Deserialize<StateDocument>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (doc != null)
                {
                    return new StateDocument(doc.GeneratedUtc,
                        new Dictionary<string, StrategyState>(doc.Strategies, StringComparer.OrdinalIgnoreCase));
                }
            }
            catch (Exception) { /* corrupted file: pretend empty, will be overwritten on next write */ }
            return new StateDocument(DateTime.UtcNow,
                new Dictionary<string, StrategyState>(StringComparer.OrdinalIgnoreCase));
        }
    }

    public void Write(StateDocument doc)
    {
        lock (_lock)
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    /// <summary>
    /// Per-strategy multiplier lookup. Defaults to 1.0 (full Kelly) when no
    /// state row exists — matches the "no decision yet" semantics in
    /// <see cref="EdgeLabKillSwitch.Resolve"/>.
    /// </summary>
    public decimal GetMultiplier(string strategy)
    {
        var s = Read().Strategies;
        return s.TryGetValue(strategy, out var row) ? row.Multiplier : EdgeLabKillSwitch.FullKellyMultiplier;
    }
}
