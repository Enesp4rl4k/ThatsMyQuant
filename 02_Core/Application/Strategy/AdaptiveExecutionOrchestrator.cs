using System.Globalization;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

public sealed record AdaptiveExecutionPlan(
    Signal Signal,
    IExchangeService ExchangeService,
    string TargetExchange,
    decimal QuantityMultiplier,
    bool RouteChanged,
    string DecisionSummary);

public sealed class AdaptiveExecutionOrchestrator
{
    private readonly ILogger<AdaptiveExecutionOrchestrator> _logger;
    private readonly ITradeRepository _tradeRepository;
    private readonly IExchangeServiceResolver _exchangeResolver;
    private readonly MarketExecutionProfileService _executionProfiles;

    public AdaptiveExecutionOrchestrator(
        ILogger<AdaptiveExecutionOrchestrator> logger,
        ITradeRepository tradeRepository,
        IExchangeServiceResolver exchangeResolver,
        MarketExecutionProfileService executionProfiles)
    {
        _logger = logger;
        _tradeRepository = tradeRepository;
        _exchangeResolver = exchangeResolver;
        _executionProfiles = executionProfiles;
    }

    public async Task<AdaptiveExecutionPlan> CreatePlanAsync(Signal signal, CancellationToken ct = default)
    {
        var requestedExchange = ResolveRequestedExchange(signal);
        var recentTrades = await _tradeRepository.GetAllAsync(160);
        var candidates = GetCandidateExchanges(signal, requestedExchange)
            .Where(_exchangeResolver.CanResolve)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(exchange => AssessVenue(signal, exchange, recentTrades))
            .ToList();

        if (candidates.Count == 0)
        {
            candidates.Add(AssessVenue(signal, requestedExchange, recentTrades));
        }

        var primary = candidates.FirstOrDefault(candidate => string.Equals(candidate.Exchange, requestedExchange, StringComparison.OrdinalIgnoreCase))
            ?? candidates[0];
        var selected = SelectVenue(primary, candidates, signal);
        var throttleMultiplier = ResolveThrottleMultiplier(selected.HealthScore, selected.SampleCount, signal);
        var adaptedSignal = CloneSignal(signal);
        ApplyAdaptiveMetadata(adaptedSignal, signal, requestedExchange, selected, throttleMultiplier);

        var summary = BuildDecisionSummary(requestedExchange, selected, throttleMultiplier);
        if (!string.Equals(requestedExchange, selected.Exchange, StringComparison.OrdinalIgnoreCase) || throttleMultiplier < 0.999m)
        {
            _logger.LogInformation(
                "Adaptive execution adjusted {Symbol} from {RequestedExchange} to {SelectedExchange} at {ThrottleMultiplier:F2}x. {Summary}",
                signal.Symbol,
                requestedExchange,
                selected.Exchange,
                throttleMultiplier,
                summary);
        }

        return new AdaptiveExecutionPlan(
            adaptedSignal,
            _exchangeResolver.Resolve(selected.Exchange),
            selected.Exchange,
            throttleMultiplier,
            !string.Equals(requestedExchange, selected.Exchange, StringComparison.OrdinalIgnoreCase),
            summary);
    }

    private VenueAssessment SelectVenue(VenueAssessment primary, IReadOnlyList<VenueAssessment> candidates, Signal signal)
    {
        if (!IsCryptoSignal(signal))
        {
            return primary;
        }

        var alternate = candidates
            .Where(candidate => !string.Equals(candidate.Exchange, primary.Exchange, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.HealthScore)
            .FirstOrDefault();

        if (alternate == null)
        {
            return primary;
        }

        var primaryNeedsRelief = primary.HealthScore < 0.72m;
        var alternateIsMeaningfullyBetter = alternate.HealthScore >= primary.HealthScore + 0.12m;
        var alternateHasEvidence = alternate.SampleCount >= 3;

        return primaryNeedsRelief && alternateIsMeaningfullyBetter && alternateHasEvidence
            ? alternate
            : primary;
    }

    private VenueAssessment AssessVenue(Signal signal, string exchange, IReadOnlyList<Trade> recentTrades)
    {
        var profileMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Exchange"] = exchange
        };
        var profile = _executionProfiles.ResolveForSymbol(signal.Symbol, profileMetadata);

        var venueTrades = recentTrades
            .Where(trade => IsLiveTrade(trade))
            .Where(trade => string.Equals(InferTradeExchange(trade), exchange, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(trade => trade.ExitTime ?? trade.LastUpdatedTime ?? trade.EntryTime)
            .ToList();

        var symbolSample = venueTrades
            .Where(trade => string.Equals(trade.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToList();
        var sample = symbolSample.Count >= 3
            ? symbolSample
            : venueTrades.Take(12).ToList();

        if (sample.Count == 0)
        {
            return new VenueAssessment(
                exchange,
                1.00m,
                0,
                profile,
                "No stored live sample for this venue yet. Holding neutral routing and size.");
        }

        var avgSlippage = sample.Average(trade => Math.Abs(trade.EntrySlippagePercent));
        var avgLatency = sample.Average(trade => (decimal)Math.Max(0L, trade.ExecutionLatencyMs));
        var avgBias = sample
            .Select(trade => trade.VenueExecutionBias > 0m ? trade.VenueExecutionBias : 1m)
            .DefaultIfEmpty(1m)
            .Average();

        var slippageScore = ScoreInverse(avgSlippage, profile.SlippagePercent, Math.Max(profile.SlippagePercent * 4m, profile.SlippagePercent + 0.25m));
        var latencyScore = ScoreInverse(avgLatency, profile.PreferredMaxLatencyMs, Math.Max(profile.PreferredMaxLatencyMs * 4m, profile.PreferredMaxLatencyMs + 600m));
        var venueScore = Clamp(avgBias, 0.55m, 1.05m);
        var healthScore = Clamp((slippageScore * 0.45m) + (latencyScore * 0.35m) + (venueScore * 0.20m), 0m, 1.05m);

        var summary = $"{exchange} live score {healthScore:F2} from {sample.Count} trades: slip {avgSlippage:F3}% vs {profile.SlippagePercent:F3}%, latency {avgLatency:F0}ms vs {profile.PreferredMaxLatencyMs}ms, bias {avgBias:F2}.";
        return new VenueAssessment(exchange, healthScore, sample.Count, profile, summary);
    }

    private static decimal ResolveThrottleMultiplier(decimal healthScore, int sampleCount, Signal signal)
    {
        if (sampleCount == 0)
        {
            return 1.00m;
        }

        var multiplier = healthScore switch
        {
            < 0.40m => 0.35m,
            < 0.55m => 0.50m,
            < 0.70m => 0.70m,
            < 0.82m => 0.85m,
            _ => 1.00m
        };

        var promotionStage = signal.Metadata.GetValueOrDefault("PromotionStage", string.Empty);
        if (promotionStage.Contains("LiveProbe", StringComparison.OrdinalIgnoreCase) && healthScore < 0.90m)
        {
            multiplier = Math.Min(multiplier, 0.50m);
        }
        else if (promotionStage.Contains("ScaledLive", StringComparison.OrdinalIgnoreCase) && healthScore < 0.70m)
        {
            multiplier = Math.Min(multiplier, 0.65m);
        }

        return Clamp(multiplier, 0.35m, 1.00m);
    }

    private static void ApplyAdaptiveMetadata(
        Signal adaptedSignal,
        Signal originalSignal,
        string requestedExchange,
        VenueAssessment selected,
        decimal throttleMultiplier)
    {
        adaptedSignal.Exchange = selected.Exchange;
        adaptedSignal.Metadata["RequestedExchange"] = requestedExchange;
        adaptedSignal.Metadata["ExecutionVenue"] = selected.Exchange;
        adaptedSignal.Metadata["AdaptiveThrottleMultiplier"] = throttleMultiplier.ToString("F2", CultureInfo.InvariantCulture);
        adaptedSignal.Metadata["AdaptiveExecutionHealth"] = selected.HealthScore.ToString("F2", CultureInfo.InvariantCulture);
        adaptedSignal.Metadata["AdaptiveExecutionReason"] = selected.Summary;

        if (originalSignal.Quantity > 0m)
        {
            adaptedSignal.Metadata["AdaptiveOriginalQuantity"] = originalSignal.Quantity.ToString(CultureInfo.InvariantCulture);
            adaptedSignal.Quantity = RoundQuantity(originalSignal.Quantity * throttleMultiplier);
        }
        else if (originalSignal.Metadata.TryGetValue("RequestedQuantity", out var requestedQuantityText) &&
                 decimal.TryParse(requestedQuantityText, CultureInfo.InvariantCulture, out var requestedQuantity) &&
                 requestedQuantity > 0m)
        {
            adaptedSignal.Metadata["AdaptiveOriginalQuantity"] = requestedQuantity.ToString(CultureInfo.InvariantCulture);
            var throttledQuantity = RoundQuantity(requestedQuantity * throttleMultiplier);
            adaptedSignal.Quantity = throttledQuantity;
            adaptedSignal.Metadata["RequestedQuantity"] = throttledQuantity.ToString("0.########", CultureInfo.InvariantCulture);
        }

        if (originalSignal.Metadata.TryGetValue("RequestedPositionFraction", out var requestedFractionText) &&
            decimal.TryParse(requestedFractionText, CultureInfo.InvariantCulture, out var requestedFraction) &&
            requestedFraction > 0m)
        {
            adaptedSignal.Metadata["AdaptiveOriginalPositionFraction"] = requestedFraction.ToString(CultureInfo.InvariantCulture);
            adaptedSignal.Metadata["RequestedPositionFraction"] = (requestedFraction * throttleMultiplier).ToString("0.########", CultureInfo.InvariantCulture);
        }

        var routeChanged = !string.Equals(requestedExchange, selected.Exchange, StringComparison.OrdinalIgnoreCase);
        if (routeChanged || throttleMultiplier < 0.999m)
        {
            var venueImpact = routeChanged
                ? $"Adaptive route -> {selected.Exchange} ({throttleMultiplier:F2}x)"
                : $"Venue capped size ({throttleMultiplier:F2}x)";

            var scope = routeChanged
                ? $"{requestedExchange} execution degraded live; rerouted to {selected.Exchange}. {selected.Summary}"
                : $"{selected.Exchange} execution softened live; size trimmed. {selected.Summary}";

            adaptedSignal.Metadata["VenueImpactLabel"] = MergeMetadata(originalSignal.Metadata, "VenueImpactLabel", venueImpact);
            adaptedSignal.Metadata["VenueExecutionScope"] = MergeMetadata(originalSignal.Metadata, "VenueExecutionScope", scope);
        }
    }

    private static string BuildDecisionSummary(string requestedExchange, VenueAssessment selected, decimal throttleMultiplier)
    {
        var routeSummary = string.Equals(requestedExchange, selected.Exchange, StringComparison.OrdinalIgnoreCase)
            ? $"{selected.Exchange} retained"
            : $"{requestedExchange}->{selected.Exchange}";
        return $"{routeSummary} at {throttleMultiplier:F2}x. {selected.Summary}";
    }

    private static Signal CloneSignal(Signal signal)
    {
        return new Signal
        {
            Id = signal.Id,
            Source = signal.Source,
            Symbol = signal.Symbol,
            Side = signal.Side,
            Price = signal.Price,
            Status = signal.Status,
            RetryCount = signal.RetryCount,
            Quantity = signal.Quantity,
            Confidence = signal.Confidence,
            Reasoning = signal.Reasoning,
            StrategyTag = signal.StrategyTag,
            RegimeTag = signal.RegimeTag,
            MarketType = signal.MarketType,
            Exchange = signal.Exchange,
            Timestamp = signal.Timestamp,
            Volume24h = signal.Volume24h,
            OrderFlowImbalance = signal.OrderFlowImbalance,
            BestBid = signal.BestBid,
            BestAsk = signal.BestAsk,
            WallAgeMs = signal.WallAgeMs,
            ExecutionAction = signal.ExecutionAction,
            PriceActionSignal = signal.PriceActionSignal,
            ReasonFlags = signal.ReasonFlags,
            Metadata = new Dictionary<string, string>(signal.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static IEnumerable<string> GetCandidateExchanges(Signal signal, string requestedExchange)
    {
        yield return requestedExchange;

        if (!IsCryptoSignal(signal))
        {
            yield break;
        }

        if (!string.Equals(requestedExchange, "Binance", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Binance";
        }

        if (!string.Equals(requestedExchange, "OKX", StringComparison.OrdinalIgnoreCase))
        {
            yield return "OKX";
        }
    }

    private static bool IsCryptoSignal(Signal signal)
        => signal.MarketType == Contracts.Enums.MarketType.Crypto || signal.Symbol.Contains("-", StringComparison.OrdinalIgnoreCase);

    private static string ResolveRequestedExchange(Signal signal)
    {
        if (signal.Metadata.TryGetValue("Exchange", out var metadataExchange) && !string.IsNullOrWhiteSpace(metadataExchange))
        {
            return metadataExchange;
        }

        return string.IsNullOrWhiteSpace(signal.Exchange) ? "Binance" : signal.Exchange;
    }

    private static string InferTradeExchange(Trade trade)
    {
        if (trade.ExecutionMode.Contains("OKX", StringComparison.OrdinalIgnoreCase))
        {
            return "OKX";
        }

        if (trade.ExecutionMode.Contains("BINANCE", StringComparison.OrdinalIgnoreCase))
        {
            return "Binance";
        }

        if (trade.ExecutionMode.Contains("SIM", StringComparison.OrdinalIgnoreCase) ||
            trade.ExecutionMode.Contains("MOCK", StringComparison.OrdinalIgnoreCase))
        {
            return "Mock";
        }

        return "Binance";
    }

    private static bool IsLiveTrade(Trade trade)
        => string.Equals(trade.ExecutionMode, "REAL", StringComparison.OrdinalIgnoreCase)
           || trade.ExecutionMode.Contains("OKX", StringComparison.OrdinalIgnoreCase)
           || (!string.IsNullOrWhiteSpace(trade.PromotionStage) &&
               trade.PromotionStage.Contains("Live", StringComparison.OrdinalIgnoreCase));

    private static string MergeMetadata(IDictionary<string, string> metadata, string key, string addition)
    {
        if (!metadata.TryGetValue(key, out var existing) || string.IsNullOrWhiteSpace(existing))
        {
            return addition;
        }

        return existing.Contains(addition, StringComparison.OrdinalIgnoreCase)
            ? existing
            : $"{existing} | {addition}";
    }

    private static decimal ScoreInverse(decimal actual, decimal preferred, decimal hard)
    {
        if (actual <= preferred) return 1m;
        if (actual >= hard) return 0m;

        var span = Math.Max(0.0001m, hard - preferred);
        return Clamp(1m - ((actual - preferred) / span), 0m, 1m);
    }

    private static decimal RoundQuantity(decimal quantity)
    {
        var rounded = Math.Round(quantity, 8, MidpointRounding.AwayFromZero);
        return rounded <= 0m ? quantity : rounded;
    }

    private static decimal Clamp(decimal value, decimal min, decimal max)
        => value < min ? min : value > max ? max : value;

    private sealed record VenueAssessment(
        string Exchange,
        decimal HealthScore,
        int SampleCount,
        MarketExecutionProfile Profile,
        string Summary);
}
