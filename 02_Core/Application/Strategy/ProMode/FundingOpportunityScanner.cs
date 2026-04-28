using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.Strategy.ProMode;

/// <summary>
/// Periodic scanner that polls funding rates across a watchlist and emits
/// <see cref="FundingOpportunity"/> events when a symbol's funding crosses an entry
/// threshold. NOT auto-executing — emits events for the Pro Mode orchestrator to queue
/// into the candidate pool. Execution decisions happen after OOS validation downstream.
///
/// Thread-safe. Designed for in-process polling every 30-60 seconds from a
/// DispatcherTimer / background task.
/// </summary>
public sealed class FundingOpportunityScanner
{
    private readonly ILogger<FundingOpportunityScanner> _logger;
    private readonly IFundingRateProvider _provider;

    public IReadOnlyList<string> Watchlist { get; init; } = Array.Empty<string>();
    /// <summary>Minimum absolute funding rate to emit as opportunity. Default 1.5 bp/8h.</summary>
    public decimal MinAbsFundingForSignal { get; init; } = 0.00015m;
    /// <summary>Strong signal threshold (emit as HighConviction). Default 3 bp/8h.</summary>
    public decimal StrongFundingThreshold { get; init; } = 0.0003m;

    public FundingOpportunityScanner(
        ILogger<FundingOpportunityScanner> logger,
        IFundingRateProvider provider)
    {
        _logger = logger;
        _provider = provider;
    }

    /// <summary>One scan cycle. Returns all opportunities whose funding |rate| ≥ threshold.</summary>
    public IReadOnlyList<FundingOpportunity> ScanOnce(DateTime asOfUtc)
    {
        var hits = new List<FundingOpportunity>();
        foreach (var symbol in Watchlist)
        {
            decimal rate;
            try { rate = _provider.GetRate(symbol, asOfUtc); }
            catch (Exception ex) { _logger.LogDebug(ex, "Scan failed for {Sym}", symbol); continue; }

            if (rate == 0m) continue;
            var absRate = Math.Abs(rate);
            if (absRate < MinAbsFundingForSignal) continue;

            var conviction = absRate >= StrongFundingThreshold
                ? OpportunityConviction.High
                : OpportunityConviction.Watch;
            var direction = rate > 0
                ? OpportunityDirection.FadeShort   // fade crowded longs
                : OpportunityDirection.FadeLong;   // fade crowded shorts
            hits.Add(new FundingOpportunity(
                Symbol: symbol,
                FundingRate: rate,
                FundingBps: (double)(rate * 10_000m),
                Conviction: conviction,
                Direction: direction,
                ObservedUtc: asOfUtc));
        }

        if (hits.Count > 0)
            _logger.LogDebug("Funding scan @ {Time}: {N} opportunities", asOfUtc, hits.Count);
        return hits;
    }
}

public sealed record FundingOpportunity(
    string Symbol,
    decimal FundingRate,
    double FundingBps,
    OpportunityConviction Conviction,
    OpportunityDirection Direction,
    DateTime ObservedUtc);

public enum OpportunityConviction { Watch, High }
public enum OpportunityDirection { FadeLong, FadeShort }
