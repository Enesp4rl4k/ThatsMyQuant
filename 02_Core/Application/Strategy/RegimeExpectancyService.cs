using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Per-regime trade-level expectancy from OOS trades only (never train-fit). Fed by the
/// walk-forward OOS test windows so every sample is out-of-sample by construction — it cannot
/// be the source of overfitting even when queried live.
///
/// Live signal gating uses <see cref="GetExpectancy"/> to convert "Confidence" from a heuristic
/// number into a regime-conditioned edge proxy. The rule: only enter when historical OOS win%
/// for the detected regime exceeds a floor and sample size is large enough to trust.
/// </summary>
public sealed class RegimeExpectancyService
{
    private readonly ILogger<RegimeExpectancyService>? _logger;
    private readonly string _storePath;
    private readonly ConcurrentDictionary<string, RegimeStats> _byRegime = new(StringComparer.OrdinalIgnoreCase);

    public RegimeExpectancyService(ILogger<RegimeExpectancyService>? logger = null, string? storePath = null)
    {
        _logger = logger;
        _storePath = storePath ?? Path.Combine(AppContext.BaseDirectory, "regime_expectancy.json");
        TryLoad();
    }

    /// <summary>
    /// Record a batch of OOS trades. Intended to be called with trades from WFO test windows —
    /// never from the WFO train grid. Passing train trades will poison expectancy with in-sample bias.
    /// </summary>
    public void RecordOos(IEnumerable<BacktestTradeResult> oosTrades)
    {
        foreach (var group in oosTrades.GroupBy(t => Normalize(t.RegimeTag)))
        {
            var existing = _byRegime.GetOrAdd(group.Key, _ => new RegimeStats(group.Key));
            lock (existing)
            {
                foreach (var t in group)
                {
                    existing.TradeCount++;
                    if (t.PnL > 0) existing.WinCount++;
                    if (t.PnL > 0) existing.GrossProfit += t.PnL;
                    else existing.GrossLoss += Math.Abs(t.PnL);
                    existing.SumPnL += t.PnL;
                    existing.SumPnLSquared += t.PnL * t.PnL;
                }
                existing.LastUpdatedUtc = DateTime.UtcNow;
            }
        }
        TrySave();
    }

    public RegimeExpectancy GetExpectancy(string regime)
    {
        var key = Normalize(regime);
        if (!_byRegime.TryGetValue(key, out var s) || s.TradeCount == 0)
            return RegimeExpectancy.Empty(key);

        lock (s)
        {
            double winRate = (double)s.WinCount / s.TradeCount;
            decimal pf = s.GrossLoss > 0m ? s.GrossProfit / s.GrossLoss : s.GrossProfit > 0m ? 999m : 0m;
            decimal expectancyPerTrade = s.SumPnL / s.TradeCount;
            // Wilson 95% CI lower bound for winRate — shrinks small-sample advantage.
            double wilsonLower = WilsonLowerBound(s.WinCount, s.TradeCount, 1.96);
            return new RegimeExpectancy(
                Regime: key,
                TradeCount: s.TradeCount,
                WinRate: winRate,
                WinRateWilsonLower: wilsonLower,
                ProfitFactor: pf,
                ExpectancyPerTrade: expectancyPerTrade,
                IsTrustworthy: s.TradeCount >= 30 && wilsonLower >= 0.45);
        }
    }

    public IReadOnlyList<RegimeExpectancy> Snapshot()
        => _byRegime.Keys.Select(GetExpectancy).ToList();

    private static string Normalize(string? regime)
        => string.IsNullOrWhiteSpace(regime) ? "Unknown" : regime.Trim();

    private static double WilsonLowerBound(int wins, int n, double z)
    {
        if (n == 0) return 0;
        double p = (double)wins / n;
        double denom = 1 + z * z / n;
        double centre = p + z * z / (2 * n);
        double margin = z * Math.Sqrt(p * (1 - p) / n + z * z / (4 * n * n));
        return Math.Max(0, (centre - margin) / denom);
    }

    private void TryLoad()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var json = File.ReadAllText(_storePath);
            var data = JsonSerializer.Deserialize<List<RegimeStats>>(json);
            if (data is null) return;
            foreach (var s in data) _byRegime[Normalize(s.Regime)] = s;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "RegimeExpectancyService: failed to load {Path}", _storePath);
        }
    }

    private void TrySave()
    {
        try
        {
            var data = _byRegime.Values.ToList();
            File.WriteAllText(_storePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "RegimeExpectancyService: failed to save {Path}", _storePath);
        }
    }

    public sealed record RegimeStats(string Regime)
    {
        public int TradeCount { get; set; }
        public int WinCount { get; set; }
        public decimal GrossProfit { get; set; }
        public decimal GrossLoss { get; set; }
        public decimal SumPnL { get; set; }
        public decimal SumPnLSquared { get; set; }
        public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}

public sealed record RegimeExpectancy(
    string Regime,
    int TradeCount,
    double WinRate,
    double WinRateWilsonLower,
    decimal ProfitFactor,
    decimal ExpectancyPerTrade,
    bool IsTrustworthy)
{
    public static RegimeExpectancy Empty(string regime) =>
        new(regime, 0, 0, 0, 0m, 0m, false);
}
