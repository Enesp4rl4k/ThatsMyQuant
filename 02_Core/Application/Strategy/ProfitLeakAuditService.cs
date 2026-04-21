using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using static ThatsMyQuant.Application.Strategy.StrategyMathUtils;

namespace ThatsMyQuant.Application.Strategy;

public sealed class ProfitLeakAuditService
{
    private readonly MarketExecutionProfileService _executionProfiles;

    public ProfitLeakAuditService(MarketExecutionProfileService? executionProfiles = null)
    {
        _executionProfiles = executionProfiles ?? new MarketExecutionProfileService();
    }

    public ProfitLeakAuditResult Evaluate(
        Signal signal,
        TradeDecision originalDecision,
        TradeDecision currentDecision,
        IReadOnlyList<Trade> recentTrades)
    {
        var closedTrades = recentTrades
            .Where(trade => trade.IsClosed && trade.ExitTime.HasValue)
            .ToList();
        var timestamp = signal.Timestamp == default ? DateTime.UtcNow : signal.Timestamp;
        var sessionTag = ResolveSessionBucket(timestamp);
        var currentVenue = ResolveVenueLabel(signal.Symbol, signal.Metadata);

        var exactBucket = BuildScopedBucketSummary(
            "Exact",
            closedTrades,
            trade =>
                Matches(trade.Symbol, signal.Symbol) &&
                Matches(trade.StrategyTag, currentDecision.StrategyTag) &&
                Matches(trade.RegimeTag, currentDecision.RegimeTag) &&
                Matches(ResolveSessionBucket(trade.ExitTime!.Value), sessionTag),
            trade =>
                Matches(trade.Symbol, signal.Symbol) &&
                Matches(trade.StrategyTag, currentDecision.StrategyTag) &&
                Matches(trade.RegimeTag, currentDecision.RegimeTag));

        var exitPolicyBucket = BuildScopedBucketSummary(
            "ExitPolicy",
            closedTrades,
            trade =>
                Matches(trade.Symbol, signal.Symbol) &&
                Matches(trade.StrategyTag, currentDecision.StrategyTag) &&
                Matches(trade.RegimeTag, currentDecision.RegimeTag) &&
                Matches(ResolveSessionBucket(trade.ExitTime!.Value), sessionTag) &&
                Matches(trade.ExitPolicyTag, currentDecision.ExitPolicyTag),
            trade =>
                Matches(trade.Symbol, signal.Symbol) &&
                Matches(trade.StrategyTag, currentDecision.StrategyTag) &&
                Matches(trade.RegimeTag, currentDecision.RegimeTag) &&
                Matches(trade.ExitPolicyTag, currentDecision.ExitPolicyTag));

        var regimeBucket = BuildBucketSummary(
            "Regime",
            closedTrades.Where(trade =>
                Matches(trade.StrategyTag, currentDecision.StrategyTag) &&
                Matches(trade.RegimeTag, currentDecision.RegimeTag)));

        var venueBucket = BuildBucketSummary(
            "Venue",
            closedTrades.Where(trade => Matches(ResolveVenueLabel(trade.Symbol, null), currentVenue)));

        var currentReducedSize = originalDecision.PositionSizePercent > 0m &&
                                 currentDecision.PositionSizePercent > 0m &&
                                 currentDecision.PositionSizePercent <= (originalDecision.PositionSizePercent * 0.75m);

        var reducedSizeBucket = currentReducedSize
            ? BuildBucketSummary(
                "ReducedSize",
                closedTrades.Where(trade =>
                    Matches(trade.StrategyTag, currentDecision.StrategyTag) &&
                    Matches(trade.RegimeTag, currentDecision.RegimeTag) &&
                    (trade.FinalAllocatorBias is > 0m and < 0.99m ||
                     (trade.PositionSizePercent > 0m && trade.PositionSizePercent <= (currentDecision.PositionSizePercent * 1.35m)))))
            : ProfitLeakBucketSummary.Empty("ReducedSize");

        var loserCluster = BuildLoserCluster(
            closedTrades.Where(trade =>
                Matches(trade.StrategyTag, currentDecision.StrategyTag) &&
                Matches(trade.RegimeTag, currentDecision.RegimeTag)),
            currentVenue);

        var approved = true;
        var sizeMultiplier = 1.00m;
        var reasons = new List<string>();

        if (exactBucket.SampleSize >= 4 &&
            exactBucket.ExpectedValue <= 0m &&
            (exactBucket.ProfitFactor < 0.98 || exactBucket.WinRate < 45))
        {
            approved = false;
            reasons.Add($"Exact bucket is net-negative after costs: EV {exactBucket.ExpectedValue:F2}, PF {exactBucket.ProfitFactor:F2}, WR {exactBucket.WinRate:F1}%.");
        }

        if (approved &&
            currentReducedSize &&
            reducedSizeBucket.SampleSize >= 4 &&
            reducedSizeBucket.ExpectedValue <= 0m &&
            reducedSizeBucket.ProfitFactor < 1.00)
        {
            approved = false;
            reasons.Add($"Reduced-size trades are behaving like noise: EV {reducedSizeBucket.ExpectedValue:F2}, PF {reducedSizeBucket.ProfitFactor:F2}.");
        }

        if (approved &&
            exitPolicyBucket.SampleSize >= 4 &&
            exitPolicyBucket.ExpectedValue <= 0m &&
            exitPolicyBucket.ProfitFactor < 1.00)
        {
            sizeMultiplier = Math.Min(sizeMultiplier, 0.82m);
            reasons.Add($"Exit policy leak: {currentDecision.ExitPolicyTag} is losing net after costs in this bucket.");
        }

        if (approved &&
            venueBucket.SampleSize >= 4 &&
            venueBucket.ExpectedValue <= 0m &&
            venueBucket.ProfitFactor < 1.00)
        {
            sizeMultiplier = Math.Min(sizeMultiplier, 0.88m);
            reasons.Add($"Venue leak: {currentVenue} is soft net of fees/slippage.");
        }

        if (approved &&
            regimeBucket.SampleSize >= 6 &&
            regimeBucket.ExpectedValue <= 0m &&
            regimeBucket.ProfitFactor < 1.00)
        {
            sizeMultiplier = Math.Min(sizeMultiplier, 0.90m);
            reasons.Add($"Regime leak: {currentDecision.StrategyTag}/{currentDecision.RegimeTag} is under water net of costs.");
        }

        if (approved &&
            loserCluster.LosingTrades >= 4 &&
            loserCluster.DegradedExecutionLossRate >= 0.65)
        {
            sizeMultiplier = Math.Min(sizeMultiplier, 0.90m);
            reasons.Add($"Losers are clustering around degraded execution ({loserCluster.DegradedExecutionLossRate:P0} of losses).");
        }

        return new ProfitLeakAuditResult(
            approved,
            sizeMultiplier,
            reasons.Count == 0 ? "No material profit leak detected." : string.Join(" ", reasons),
            exactBucket,
            exitPolicyBucket,
            regimeBucket,
            venueBucket,
            reducedSizeBucket,
            loserCluster,
            currentReducedSize);
    }

    private ProfitLeakBucketSummary BuildBucketSummary(string label, IEnumerable<Trade> trades)
    {
        var tradeList = trades.ToList();
        if (tradeList.Count == 0)
        {
            return ProfitLeakBucketSummary.Empty(label);
        }

        var pnls = tradeList.Select(CalculateNetPnl).ToList();
        var losses = pnls.Where(pnl => pnl < 0m).ToList();

        return new ProfitLeakBucketSummary(
            label,
            tradeList.Count,
            tradeList.Count(trade => CalculateNetPnl(trade) > 0m) * 100.0 / tradeList.Count,
            ComputeProfitFactor(pnls),
            pnls.Average(),
            pnls.Sum(),
            losses.Count == 0 ? 0m : Math.Abs(losses.Sum()) / losses.Count);
    }

    private ProfitLeakBucketSummary BuildScopedBucketSummary(
        string label,
        IReadOnlyList<Trade> trades,
        Func<Trade, bool> exactPredicate,
        Func<Trade, bool> broadPredicate)
    {
        var exact = trades.Where(exactPredicate).ToList();
        if (exact.Count >= 3)
        {
            return BuildBucketSummary(label, exact);
        }

        var broad = trades.Where(broadPredicate).ToList();
        return BuildBucketSummary(label, broad.Count > 0 ? broad : exact);
    }

    private ProfitLeakLoserClusterSummary BuildLoserCluster(IEnumerable<Trade> trades, string venueLabel)
    {
        var losers = trades
            .Select(trade => new
            {
                Trade = trade,
                NetPnl = CalculateNetPnl(trade),
                Degraded = IsDegradedExecution(trade),
                Venue = ResolveVenueLabel(trade.Symbol, null)
            })
            .Where(item => item.NetPnl < 0m)
            .ToList();

        if (losers.Count == 0)
        {
            return ProfitLeakLoserClusterSummary.Empty(venueLabel);
        }

        var degradedCount = losers.Count(item => item.Degraded);
        var weakExitPolicies = losers
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Trade.ExitPolicyTag) ? "Unknown" : item.Trade.ExitPolicyTag.Trim())
            .OrderByDescending(group => group.Count())
            .First()
            .Key;

        return new ProfitLeakLoserClusterSummary(
            venueLabel,
            losers.Count,
            (double)degradedCount / losers.Count,
            weakExitPolicies);
    }

    private decimal CalculateNetPnl(Trade trade)
    {
        var quantity = trade.ClosedQuantity > 0m ? trade.ClosedQuantity : (trade.Quantity > 0m ? trade.Quantity : trade.FilledQuantity);
        if (quantity <= 0m)
        {
            quantity = 1m;
        }

        var exitPrice = trade.AverageExitPrice > 0m ? trade.AverageExitPrice : trade.ExitPrice;
        var grossPnl = trade.RealisedPnL + trade.NetFunding;
        var fees = trade.TotalFeesPaid > 0m
            ? trade.TotalFeesPaid
            : _executionProfiles.CalculateRoundTripFees(trade.EntryPrice, exitPrice, quantity, trade.Symbol);
        var slippageCost = trade.EntryPrice > 0m
            ? trade.EntryPrice * quantity * (Math.Abs(trade.EntrySlippagePercent) / 100m)
            : 0m;
        return grossPnl - fees - slippageCost;
    }

    private bool IsDegradedExecution(Trade trade)
    {
        var profile = _executionProfiles.ResolveForSymbol(trade.Symbol);
        return Math.Abs(trade.EntrySlippagePercent) >= profile.SlippagePercent * 1.50m ||
               trade.ExecutionLatencyMs >= profile.PreferredMaxLatencyMs * 2 ||
               (trade.VenueExecutionBias > 0m && trade.VenueExecutionBias < 0.98m) ||
               (!string.IsNullOrWhiteSpace(trade.VenueExecutionScope) &&
                trade.VenueExecutionScope.Contains("degrad", StringComparison.OrdinalIgnoreCase));
    }

    private string ResolveVenueLabel(string symbol, IDictionary<string, string>? metadata)
    {
        var profile = _executionProfiles.ResolveForSymbol(symbol, metadata);
        return $"{profile.Venue} {profile.Market}".Trim();
    }

    private static bool Matches(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed record ProfitLeakAuditResult(
    bool Approved,
    decimal SizeMultiplier,
    string Summary,
    ProfitLeakBucketSummary ExactBucket,
    ProfitLeakBucketSummary ExitPolicyBucket,
    ProfitLeakBucketSummary RegimeBucket,
    ProfitLeakBucketSummary VenueBucket,
    ProfitLeakBucketSummary ReducedSizeBucket,
    ProfitLeakLoserClusterSummary LoserCluster,
    bool CurrentTradeIsReducedSize);

public sealed record ProfitLeakBucketSummary(
    string Label,
    int SampleSize,
    double WinRate,
    double ProfitFactor,
    decimal ExpectedValue,
    decimal TotalPnL,
    decimal AverageLoss)
{
    public static ProfitLeakBucketSummary Empty(string label) => new(label, 0, 0, 0, 0m, 0m, 0m);
}

public sealed record ProfitLeakLoserClusterSummary(
    string VenueLabel,
    int LosingTrades,
    double DegradedExecutionLossRate,
    string DominantExitPolicy)
{
    public static ProfitLeakLoserClusterSummary Empty(string venueLabel) => new(venueLabel, 0, 0, string.Empty);
}
