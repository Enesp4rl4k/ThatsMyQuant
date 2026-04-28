using System.Collections.Concurrent;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Supervisor for N live pair engines sharing a fixed capital budget.
///
/// Responsibilities:
///   1. Register / deregister pair engines (typically from PairsDiscoveryService output).
///   2. Distribute a capital budget across active pairs proportional to their live
///      quality (half-life + |ADF t|), clamped to per-pair caps.
///   3. Enforce a cross-pair correlation guard — two pairs with highly correlated
///      residuals behave like one double-size bet. We detect it via the intersection
///      of their leg symbols and halve the allocation of the weaker one.
///   4. Expose <see cref="IPairPortfolioDiagnostics"/> for dashboards.
///
/// This is a logical supervisor: it does NOT own the MediatR pipeline; pair engines
/// keep firing ExecuteTradeCommand themselves. The coordinator only mutates
/// <see cref="PairsTradingOptions.CoordinatorAllocationScale"/> on each registered
/// engine's options bag to throttle exposure.
/// </summary>
public sealed class PairsPortfolioCoordinator : IPairPortfolioDiagnostics
{
    private readonly ILogger<PairsPortfolioCoordinator> _logger;
    private readonly ConcurrentDictionary<string, RegisteredPair> _pairs = new();
    private readonly SemaphoreSlim _rebalanceGate = new(1, 1);

    /// <summary>Max fraction of the coordinator's budget any single pair may consume.</summary>
    public decimal MaxPairShare { get; set; } = 0.35m;

    /// <summary>Minimum fraction of the coordinator's budget allocated to an active pair
    /// (protects small-quality pairs from being zeroed entirely).</summary>
    public decimal MinPairShare { get; set; } = 0.05m;

    public PairsPortfolioCoordinator(ILogger<PairsPortfolioCoordinator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Add a pair engine to be managed by this coordinator. Idempotent on
    /// (SymbolA, SymbolB) — re-registering replaces the prior entry.
    /// </summary>
    public void RegisterPair(PairsTradingEngine engine, PairsTradingOptions optionsBag)
    {
        var diag = engine.GetDiagnostics();
        var key = PairKey(diag.SymbolA, diag.SymbolB);
        _pairs[key] = new RegisteredPair(engine, optionsBag);
        _logger.LogInformation("Coordinator registered pair {Key}", key);
    }

    public bool DeregisterPair(string symbolA, string symbolB)
    {
        var removed = _pairs.TryRemove(PairKey(symbolA, symbolB), out _);
        if (removed) _logger.LogInformation("Coordinator deregistered pair {A}/{B}", symbolA, symbolB);
        return removed;
    }

    /// <summary>Force a capital rebalance across all registered pairs. Call this on a
    /// cadence (e.g. every N ticks or every minute) rather than per-tick — the weights
    /// only change meaningfully when quality or admission flips.</summary>
    public async Task RebalanceAsync(CancellationToken ct = default)
    {
        await _rebalanceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var active = _pairs.Values
                .Select(p => (pair: p, diag: p.Engine.GetDiagnostics()))
                .Where(t => t.diag.IsCointegrated)
                .ToList();

            if (active.Count == 0)
            {
                foreach (var p in _pairs.Values) p.Options.CoordinatorAllocationScale = 0m;
                return;
            }

            // Quality weight: prefer short half-life + strong |ADF t|. Cap at a sensible
            // ceiling so a single pair doesn't monopolise the book.
            double totalWeight = 0.0;
            var weights = new Dictionary<string, double>(active.Count);
            foreach (var (p, d) in active)
            {
                double hlScore = d.HalfLife is >= 5 and <= 30 ? 1.0
                    : d.HalfLife is >= 3 and <= 60 ? 0.6
                    : 0.3;
                double adfScore = Math.Min(1.0, Math.Abs(d.AdfTStatistic) / 4.0);
                double hurstScore = Math.Max(0.0, 1.0 - Math.Max(0.0, d.HurstExponent - 0.30) / 0.25);
                double w = hlScore * adfScore * hurstScore;
                if (w <= 0) w = 0.05;
                weights[PairKey(d.SymbolA, d.SymbolB)] = w;
                totalWeight += w;
            }
            if (totalWeight <= 0) totalWeight = 1.0;

            // Cross-pair leg overlap guard — if pair P shares a leg with pair Q, halve
            // the weaker one's weight. Prevents doubling exposure on a symbol the market
            // happens to feature in multiple admitted pairs.
            var adjusted = new Dictionary<string, double>(weights);
            for (int i = 0; i < active.Count; i++)
            {
                for (int j = i + 1; j < active.Count; j++)
                {
                    var dI = active[i].diag;
                    var dJ = active[j].diag;
                    bool overlap = dI.SymbolA == dJ.SymbolA || dI.SymbolA == dJ.SymbolB
                                   || dI.SymbolB == dJ.SymbolA || dI.SymbolB == dJ.SymbolB;
                    if (!overlap) continue;

                    var kI = PairKey(dI.SymbolA, dI.SymbolB);
                    var kJ = PairKey(dJ.SymbolA, dJ.SymbolB);
                    if (adjusted[kI] >= adjusted[kJ]) adjusted[kJ] *= 0.5;
                    else adjusted[kI] *= 0.5;
                }
            }
            double adjustedTotal = adjusted.Values.Sum();
            if (adjustedTotal <= 0) adjustedTotal = 1.0;

            foreach (var (p, d) in active)
            {
                var key = PairKey(d.SymbolA, d.SymbolB);
                double share = adjusted[key] / adjustedTotal;
                share = Math.Clamp(share, (double)MinPairShare, (double)MaxPairShare);
                p.Options.CoordinatorAllocationScale = (decimal)share;
            }

            // Anything not in `active` should be scaled to 0 so it cannot fire trades.
            var activeKeys = new HashSet<string>(active.Select(t => PairKey(t.diag.SymbolA, t.diag.SymbolB)));
            foreach (var kvp in _pairs)
            {
                if (!activeKeys.Contains(kvp.Key))
                    kvp.Value.Options.CoordinatorAllocationScale = 0m;
            }

            _logger.LogInformation(
                "Coordinator rebalanced: active={Active}/{Total}",
                active.Count, _pairs.Count);
        }
        finally
        {
            _rebalanceGate.Release();
        }
    }

    public IReadOnlyList<PairDiagnosticSnapshot> GetAllPairDiagnostics()
        => _pairs.Values.Select(p => p.Engine.GetDiagnostics()).ToList();

    public PairPortfolioSummary GetPortfolioSummary()
    {
        var snaps = GetAllPairDiagnostics();
        var active = snaps.Where(s => s.IsCointegrated).ToList();
        int openPos = snaps.Count(s => !string.Equals(s.Position, nameof(PairPositionState.Flat), StringComparison.Ordinal));
        double avgH = active.Count > 0 ? active.Average(s => s.HurstExponent) : 0.0;
        double avgHl = active.Count > 0 ? active.Average(s => s.HalfLife) : 0.0;
        decimal totalScale = _pairs.Values.Sum(p => p.Options.CoordinatorAllocationScale);
        return new PairPortfolioSummary(
            TotalPairs: snaps.Count,
            ActivePairs: active.Count,
            OpenPositions: openPos,
            AverageHurst: avgH,
            AverageHalfLife: avgHl,
            TotalAllocationScale: totalScale);
    }

    private static string PairKey(string a, string b)
        => string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

    private sealed record RegisteredPair(PairsTradingEngine Engine, PairsTradingOptions Options);
}
