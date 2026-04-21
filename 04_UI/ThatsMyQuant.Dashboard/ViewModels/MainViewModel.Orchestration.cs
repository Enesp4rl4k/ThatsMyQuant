using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MainViewModel
{
    private void UpdateBotLaunchSummary()
    {
        var slopeText = Studio.GridSlopeBias < 40 ? "bearish" : Studio.GridSlopeBias > 60 ? "bullish" : "neutral";
        UpdateBotLaunchRecommendation();
        var exactBucketPressure = ResolveSelectedExactBucketPressure();
        var concordanceSignal = ResolveSelectedConcordanceSignal();
        var exactBucketLaunchText = exactBucketPressure is null
            ? "exact bucket pressure unconfirmed"
            : exactBucketPressure.Value.Impact.ToLowerInvariant();
        var concordanceLaunchText = concordanceSignal is null
            ? "concordance unconfirmed"
            : concordanceSignal.Value.Impact.ToLowerInvariant();

        Studio.BotLaunchSummary =
            $"{Studio.SelectedBotStrategy} on {Studio.SelectedBotBaseAsset}/{Studio.SelectedBotQuoteAsset} with {Studio.BotFundingAmount:N0} {Studio.SelectedBotQuoteAsset}, " +
            $"{Studio.SelectedBotObjective.ToLowerInvariant()} objective, grid {Studio.GridLowerLimit:F1}% to {Studio.GridUpperLimit:F1}%, density {Studio.GridDensity}, slope {slopeText}, throttle {StrategyThrottleStatus.ToLowerInvariant()}, {exactBucketLaunchText}, {concordanceLaunchText}, recommended capital {Studio.BotRecommendedFunding:N0}.";
        var modeHint = IsPaperMode
            ? "Paper mode is active, so this launch will stay sandboxed until you switch to live."
            : "Live mode is active. Double-check broker routing and risk limits before activating.";
        Studio.BotLaunchHint =
            modeHint +
            " Same automated desk loop as Terminal START LOOP. Listener, Execution, and QuantEngine are separate processes—start them from the Launcher app or the README ops checklist; this button does not spawn them.";

        UpdateBotProductivityInsights();
    }

    private void UpdateBotProductivityInsights()
    {
        var score = CalculateBotProductivityScore();
        Studio.BotProductivityScore = $"Setup score {score}/100";
        Studio.BotProductivityColor = ResolveBotProductivityColor(score);
        Studio.BotSetupVelocity = ResolveBotSetupVelocity(score);
        Studio.BotWorkflowAdvice = ResolveBotWorkflowAdvice();
    }

    private int CalculateBotProductivityScore()
    {
        var score = 40;
        score += Studio.BotLaunchGuardStatus switch
        {
            "Ready" => 30,
            "Reduced" => 18,
            "Caution" => 10,
            "Disabled" => -10,
            "Blocked" => -15,
            _ => 0
        };
        score += StrategyThrottleStatus switch
        {
            "Normal" => 15,
            "Reduced" => 5,
            "Learning" => 3,
            _ => 0
        };

        if (Studio.BotFundingAmount > 0 && Studio.BotRecommendedFunding > 0)
        {
            var alignment = (double)(Studio.BotRecommendedFunding / Studio.BotFundingAmount);
            score += alignment >= 0.9 ? 10 : alignment >= 0.6 ? 5 : 0;
        }

        if (Studio.GridDensity is >= 8 and <= 16)
        {
            score += 5;
        }

        if (Studio.GridUpperLimit > Studio.GridLowerLimit && (Studio.GridUpperLimit - Studio.GridLowerLimit) >= 4)
        {
            score += 5;
        }

        return Math.Clamp(score, 0, 100);
    }

    private Brush ResolveBotProductivityColor(int score) => score >= 75 ? GreenBrush : score >= 55 ? OrangeBrush : RedBrush;

    private string ResolveBotSetupVelocity(int score)
    {
        if (score >= 75)
        {
            return "Fast lane. This configuration is clean enough for quick deployment.";
        }

        if (score >= 55)
        {
            return "Medium friction. Use auto-tune to tighten capital and grid settings.";
        }

        return "Slow lane. Too much launch drag or weak edge is hurting deployment quality.";
    }

    private string ResolveBotWorkflowAdvice() => Studio.BotLaunchGuardStatus switch
    {
        "Disabled" => "Recovery first: re-enable only after paper validation and start with reduced size.",
        "Blocked" => "Do not deploy. Backtest or change strategy before spending more operator time here.",
        "Caution" => "Session is weak. Save time by waiting for a better killzone instead of tweaking endlessly.",
        "Reduced" => "Lean into smaller funding and narrower decision scope. Productivity comes from disciplined repetition.",
        "Ready" => "This bot is launch-efficient. Paper it once, then promote if execution quality holds.",
        _ => "Use presets or auto-tune to reduce launch friction."
    };

    private void UpdateBotLaunchRecommendation()
    {
        var activeSession = SessionContexts.FirstOrDefault(context => context.IsActive)?.Name ?? "Unknown";
        var strategyDisabled = DisabledStrategies.Any(s => StrategyTagsMatch(s, Studio.SelectedBotStrategy));

        var health = _persistedStrategyHealth.FirstOrDefault(s => StrategyTagsMatch(s.StrategyTag, Studio.SelectedBotStrategy));
        var kellyFraction = 1.0m;
        if (health != null && health.Trades >= 5 && (decimal)health.ProfitFactor > 1.0m)
        {
            kellyFraction = ThatsMyQuant.Application.Risk.KellyCriterionCalculator.CalculateFractionalKelly((decimal)health.WinRate / 100m, (decimal)health.ProfitFactor, 0.5m);
        }
        else if (health != null && health.Trades >= 5)
        {
            kellyFraction = 0.25m;
        }

        var multipliers = CalculateLaunchMultipliers(activeSession);
        Studio.BotRecommendedFunding = Math.Round(Studio.BotFundingAmount * multipliers.Throttle * multipliers.Risk * multipliers.Objective * multipliers.Session * kellyFraction, 0);

        if (EvaluateLaunchGuards(activeSession, strategyDisabled))
        {
            UpdateDeploymentChecklist(activeSession, strategyDisabled);
            RefreshCapitalAllocation();
            return;
        }

        Studio.BotLaunchGuardStatus = "Ready";
        Studio.BotLaunchGuardColor = GreenBrush;
        Studio.BotLaunchGuardReason = "All core signals and session context are aligned.";
        Studio.BotProfitabilityFocus = "Deployment is recommended. Start in paper mode or with small live probe size.";

        UpdateDeploymentChecklist(activeSession, strategyDisabled);
        RefreshCapitalAllocation();
    }

    private (decimal Throttle, decimal Risk, decimal Objective, decimal Session) CalculateLaunchMultipliers(string activeSession)
    {
        var throttle = StrategyThrottleStatus switch
        {
            "Blocked" => 0m,
            "Reduced" => 0.5m,
            "Normal" => 1m,
            "Learning" => 0.35m,
            "Unavailable" => 0.25m,
            _ => 0.25m
        };
        var risk = Studio.SelectedRiskProfile switch
        {
            "Low" => 0.55m,
            "Aggressive" => 1m,
            _ => 0.8m
        };
        var objective = Studio.SelectedBotObjective switch
        {
            "Low Drawdown" => 0.6m,
            "Income Flow" => 0.8m,
            "Fast Rotation" => 0.9m,
            _ => 1m
        };
        var session = activeSession switch
        {
            "London Killzone" => 1m,
            "NY Killzone" => 0.95m,
            "Asia" => 0.65m,
            "Close / Dead Zone" => 0.45m,
            _ => 0.6m
        };

        return (throttle, risk, objective, session);
    }

    private bool EvaluateLaunchGuards(string activeSession, bool strategyDisabled)
    {
        var isForceEnabled = IsForceEnabledStrategy(Studio.SelectedBotStrategy);

        if (strategyDisabled)
        {
            return UpdateLaunchGuard("Disabled", RedBrush, $"{Studio.SelectedBotStrategy} is on the diagonal list.", "Do not deploy disabled strategies.", 0);
        }

        if (StrategyThrottleStatus == "Blocked")
        {
            return UpdateLaunchGuard("Blocked", RedBrush, "Strategy bucket is underperforming.", "Focus on research and backtesting.", null);
        }

        var pressure = ResolveSelectedExactBucketPressure();
        if (pressure is { } p && p.Impact.Contains("near Paper", StringComparison.OrdinalIgnoreCase))
        {
            if (isForceEnabled)
            {
                return UpdateLaunchGuard("Reduced", OrangeBrush, $"Exact bucket: {p.Summary}", "Manual recovery is enabled, so keep this deployment in reduced probe mode.", null);
            }

            return UpdateLaunchGuard("Blocked", RedBrush, $"Exact bucket: {p.Summary}", "Exact bucket learning is rejecting this deployment.", 0);
        }

        var concordance = ResolveSelectedConcordanceSignal();
        if (concordance is { } c && (c.Impact.Contains("near Paper", StringComparison.OrdinalIgnoreCase) || c.Impact.Contains("capping real edge", StringComparison.OrdinalIgnoreCase)))
        {
            if (isForceEnabled)
            {
                return UpdateLaunchGuard("Caution", OrangeBrush, c.Summary, "Manual recovery is enabled, but concordance still requires probe-size discipline.", null);
            }

            return UpdateLaunchGuard("Blocked", RedBrush, c.Summary, "Concordance logic is capping this deployment.", 0);
        }

        if (activeSession.Contains("Zone", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateLaunchGuard("Caution", OrangeBrush, "Market is in a low-liquidity or transition zone.", "Deployment is possible but not recommended for new strategies.", null);
        }

        return false;
    }

    private bool UpdateLaunchGuard(string status, Brush color, string reason, string focus, decimal? funding)
    {
        Studio.BotLaunchGuardStatus = status;
        Studio.BotLaunchGuardColor = color;
        Studio.BotLaunchGuardReason = reason;
        Studio.BotProfitabilityFocus = focus;
        if (funding.HasValue)
        {
            Studio.BotRecommendedFunding = funding.Value;
        }

        return true;
    }

    private void UpdateDeploymentChecklist(string activeSession, bool strategyDisabled)
    {
        DeploymentChecklist.Clear();

        AddDeploymentCheck(
            "Strategy status",
            !strategyDisabled,
            strategyDisabled
                ? $"{Studio.SelectedBotStrategy} is currently disabled by strategy health controls."
                : $"{Studio.SelectedBotStrategy} is enabled for deployment review.");

        var sessionPassing = !activeSession.Contains("Zone", StringComparison.OrdinalIgnoreCase);
        AddDeploymentCheck(
            "Session quality",
            sessionPassing,
            sessionPassing
                ? $"{activeSession} is acceptable for deployment."
                : $"{activeSession} is weak for fresh risk. Wait for a better session.");

        var throttlePassing = StrategyThrottleStatus is "Normal" or "Learning";
        AddDeploymentCheck(
            "Throttle",
            throttlePassing,
            string.IsNullOrWhiteSpace(StrategyThrottleReason)
                ? $"Throttle state is {StrategyThrottleStatus}."
                : StrategyThrottleReason);

        var exactBucketPressure = ResolveSelectedExactBucketPressure();
        var exactBucketPassing = exactBucketPressure is null ||
            !exactBucketPressure.Value.Impact.Contains("near Paper", StringComparison.OrdinalIgnoreCase);
        AddDeploymentCheck(
            "Exact bucket pressure",
            exactBucketPassing,
            exactBucketPressure?.Summary ?? "No exact-bucket pressure stored yet.");

        var concordance = ResolveSelectedConcordanceSignal();
        var concordancePassing = concordance is null ||
            (!concordance.Value.Impact.Contains("near Paper", StringComparison.OrdinalIgnoreCase) &&
             !concordance.Value.Impact.Contains("capping real edge", StringComparison.OrdinalIgnoreCase));
        AddDeploymentCheck(
            "Concordance",
            concordancePassing,
            concordance?.Summary ?? "Concordance is still unconfirmed.");

        var fundingPassing = Studio.BotRecommendedFunding > 0;
        AddDeploymentCheck(
            "Capital sizing",
            fundingPassing,
            fundingPassing
                ? $"Recommended capital is {Studio.BotRecommendedFunding:N0} {Studio.SelectedBotQuoteAsset}."
                : "Recommended capital is zero, so deployment should stay blocked.");
    }

    private (string Impact, string Summary, Brush Color)? ResolveSelectedExactBucketPressure()
        => ResolveExactBucketPressure(Studio.SelectedBotStrategy, MarketOverview.SelectedSymbol);

    private (decimal AverageBias, string Summary)? ResolveSelectedVenueSignal()
    {
        if (string.IsNullOrWhiteSpace(Studio.SelectedBotStrategy))
        {
            return null;
        }

        var exactVenue = VenueExecutionBiasRows.FirstOrDefault(row =>
            StrategyTagsMatch(row.StrategyTag, Studio.SelectedBotStrategy) &&
            (string.IsNullOrWhiteSpace(MarketOverview.SelectedSymbol) || row.Symbol.Equals(MarketOverview.SelectedSymbol, StringComparison.OrdinalIgnoreCase)));

        if (exactVenue is not null)
        {
            return (exactVenue.AverageBias, exactVenue.Summary);
        }

        var broadVenue = StrategyLeaderboard.FirstOrDefault(row =>
            StrategyTagsMatch(row.StrategyTag, Studio.SelectedBotStrategy) &&
            !string.IsNullOrWhiteSpace(row.VenueExecutionSummary));

        return broadVenue is null ? null : (broadVenue.AverageVenueExecutionBias, broadVenue.VenueExecutionSummary);
    }

    private (string Impact, string Summary, Brush Color)? ResolveSelectedConcordanceSignal()
    {
        var exactBucketPressure = ResolveSelectedExactBucketPressure();
        var venueSignal = ResolveSelectedVenueSignal();

        if (exactBucketPressure is null || venueSignal is null)
        {
            return null;
        }

        return BuildConcordanceDisplay(
            exactBucketPressure.Value.Impact,
            exactBucketPressure.Value.Summary,
            venueSignal.Value.AverageBias,
            venueSignal.Value.Summary);
    }

    private (string Impact, string Summary, Brush Color)? ResolveExactBucketPressure(string? strategyTag, string? symbol)
    {
        if (string.IsNullOrWhiteSpace(strategyTag))
        {
            return null;
        }

        var exact = ExactBucketPromotionPressureRows.FirstOrDefault(row =>
            StrategyTagsMatch(row.StrategyTag, strategyTag) &&
            (string.IsNullOrWhiteSpace(symbol) || row.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)));

        if (exact is not null)
        {
            return (exact.PromotionImpact, exact.Summary, exact.PromotionImpactColor);
        }

        var broad = StrategyLeaderboard.FirstOrDefault(row =>
            StrategyTagsMatch(row.StrategyTag, strategyTag));

        if (broad is not null && !string.IsNullOrWhiteSpace(broad.ExactBucketPromotionImpact))
        {
            return (broad.ExactBucketPromotionImpact, broad.ExactBucketSummary, broad.ExactBucketPromotionImpactColor);
        }

        return null;
    }

    private static bool StrategyTagsMatch(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        static string Normalize(string value) => value.Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        return Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private void AddDeploymentCheck(string title, bool isPassing, string detail)
    {
        DeploymentChecklist.Add(new ChecklistItemViewModel
        {
            Title = title,
            Detail = detail,
            Status = isPassing ? "Pass" : "Review",
            IsPassing = isPassing,
            Color = isPassing ? GreenBrush : OrangeBrush
        });
    }

    private void EvaluateStrategyThrottle()
    {
        var focusBucket = FindBestStrategyBucketForThrottle();

        if (focusBucket == null || focusBucket.Trades < 3)
        {
            StrategyThrottleStatus = "Learning";
            StrategyThrottleReason = "The selected strategy needs at least 3 closed trades before automatic throttling kicks in.";
            StrategyThrottleColor = GrayBrush;
            return;
        }

        var isForceEnabled = IsForceEnabledStrategy(Studio.SelectedBotStrategy);
        if (HasEdgeBandFailure(focusBucket.StrategyTag, focusBucket.RegimeTag))
        {
            UpdateThrottleForEdgeBandFailure(focusBucket, isForceEnabled);
            return;
        }

        if (focusBucket.ProfitFactor < 1.0 || (focusBucket.TotalPnL < 0 && focusBucket.WinRate < 45))
        {
            UpdateThrottleForUnderperformance(focusBucket, isForceEnabled);
            return;
        }

        UpdateThrottleForSoftOrNormalPerformance(focusBucket);
    }

    private StrategyLeaderboardRow? FindBestStrategyBucketForThrottle()
    {
        var selected = StrategyLeaderboard.FirstOrDefault(row =>
            StrategyTagsMatch(row.StrategyTag, Studio.SelectedBotStrategy));

        selected ??= _persistedStrategyHealth
            .Where(row => StrategyTagsMatch(row.StrategyTag, Studio.SelectedBotStrategy))
            .OrderByDescending(row => row.RecordedAtUtc)
            .Select(row => new StrategyLeaderboardRow
            {
                StrategyTag = row.StrategyTag,
                RegimeTag = row.RegimeTag,
                Trades = row.Trades,
                WinRate = row.WinRate,
                ProfitFactor = row.ProfitFactor,
                TotalPnL = row.TotalPnL
            })
            .FirstOrDefault();

        var weakest = StrategyLeaderboard.OrderBy(row => row.ProfitFactor).ThenBy(row => row.TotalPnL).FirstOrDefault();
        weakest ??= _persistedStrategyHealth
            .OrderBy(row => row.ProfitFactor)
            .ThenBy(row => row.TotalPnL)
            .Select(row => new StrategyLeaderboardRow
            {
                StrategyTag = row.StrategyTag,
                RegimeTag = row.RegimeTag,
                Trades = row.Trades,
                WinRate = row.WinRate,
                ProfitFactor = row.ProfitFactor,
                TotalPnL = row.TotalPnL
            })
            .FirstOrDefault();

        return selected ?? weakest;
    }

    private void UpdateThrottleForEdgeBandFailure(StrategyLeaderboardRow bucket, bool isForceEnabled)
    {
        if (isForceEnabled)
        {
            StrategyThrottleStatus = "Reduced";
            StrategyThrottleReason = $"{BuildEdgeBandFailureReason(bucket.StrategyTag, bucket.RegimeTag)} Manual recovery is active, so size stays cut.";
            StrategyThrottleColor = OrangeBrush;
        }
        else
        {
            StrategyThrottleStatus = "Blocked";
            StrategyThrottleReason = BuildEdgeBandFailureReason(bucket.StrategyTag, bucket.RegimeTag);
            StrategyThrottleColor = RedBrush;
        }
    }

    private void UpdateThrottleForUnderperformance(StrategyLeaderboardRow bucket, bool isForceEnabled)
    {
        if (isForceEnabled)
        {
            StrategyThrottleStatus = "Reduced";
            StrategyThrottleReason = $"{bucket.StrategyTag} is in manual recovery mode. Reduced size active.";
            StrategyThrottleColor = OrangeBrush;
        }
        else
        {
            StrategyThrottleStatus = "Blocked";
            StrategyThrottleReason = $"{bucket.StrategyTag} in {bucket.RegimeTag} is underperforming.";
            StrategyThrottleColor = RedBrush;
        }
    }

    private void UpdateThrottleForSoftOrNormalPerformance(StrategyLeaderboardRow bucket)
    {
        if (bucket.ProfitFactor < 1.25 || bucket.WinRate < 52)
        {
            StrategyThrottleStatus = "Reduced";
            StrategyThrottleReason = $"{bucket.StrategyTag} is tradable but soft. Keep reduced size.";
            StrategyThrottleColor = OrangeBrush;
        }
        else
        {
            StrategyThrottleStatus = "Normal";
            StrategyThrottleReason = $"{bucket.StrategyTag} has enough edge to trade at normal size.";
            StrategyThrottleColor = GreenBrush;
        }
    }

    private TradeDecision ApplyStrategyThrottle(TradeDecision decision)
    {
        var forceEnabled = IsForceEnabledStrategy(decision.StrategyTag);
        var bucket = StrategyLeaderboard.FirstOrDefault(row =>
            row.StrategyTag.Equals(decision.StrategyTag, StringComparison.OrdinalIgnoreCase) &&
            row.RegimeTag.Equals(decision.RegimeTag, StringComparison.OrdinalIgnoreCase));

        bucket ??= _persistedStrategyHealth
            .Where(row =>
                row.StrategyTag.Equals(decision.StrategyTag, StringComparison.OrdinalIgnoreCase) &&
                row.RegimeTag.Equals(decision.RegimeTag, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => row.RecordedAtUtc)
            .Select(row => new StrategyLeaderboardRow
            {
                StrategyTag = row.StrategyTag,
                RegimeTag = row.RegimeTag,
                Trades = row.Trades,
                WinRate = row.WinRate,
                ProfitFactor = row.ProfitFactor,
                TotalPnL = row.TotalPnL
            })
            .FirstOrDefault();

        if (bucket == null || bucket.Trades < 3)
        {
            StrategyThrottleStatus = "Learning";
            StrategyThrottleReason = $"{decision.StrategyTag} in {decision.RegimeTag} is still gathering enough samples.";
            StrategyThrottleColor = GrayBrush;
            return decision;
        }

        if (HasEdgeBandFailure(decision.StrategyTag, decision.RegimeTag))
        {
            if (forceEnabled)
            {
                StrategyThrottleStatus = "Reduced";
                StrategyThrottleReason = $"{BuildEdgeBandFailureReason(decision.StrategyTag, decision.RegimeTag)} Manual recovery keeps sizing aggressively reduced.";
                StrategyThrottleColor = OrangeBrush;

                return decision with
                {
                    ShouldTrade = decision.ShouldTrade,
                    Confidence = Math.Min(decision.Confidence, 55),
                    PositionSizePercent = Math.Min(decision.PositionSizePercent, 0.01m),
                    Reasoning = $"Edge-band recovery override reduced {decision.StrategyTag} in {decision.RegimeTag}. {decision.Reasoning}"
                };
            }

            StrategyThrottleStatus = "Blocked";
            StrategyThrottleReason = BuildEdgeBandFailureReason(decision.StrategyTag, decision.RegimeTag);
            StrategyThrottleColor = RedBrush;

            return decision with
            {
                ShouldTrade = false,
                Confidence = Math.Min(decision.Confidence, 45),
                Reasoning = $"Edge-band failure blocked {decision.StrategyTag} in {decision.RegimeTag}. {decision.Reasoning}"
            };
        }

        if (bucket.ProfitFactor < 1.0 || (bucket.TotalPnL < 0 && bucket.WinRate < 45))
        {
            if (forceEnabled)
            {
                StrategyThrottleStatus = "Reduced";
                StrategyThrottleReason = $"{decision.StrategyTag} is in manual recovery mode. The trade is allowed, but size stays aggressively reduced.";
                StrategyThrottleColor = OrangeBrush;

                return decision with
                {
                    ShouldTrade = decision.ShouldTrade,
                    Confidence = Math.Min(decision.Confidence, 55),
                    PositionSizePercent = Math.Min(decision.PositionSizePercent, 0.01m),
                    Reasoning = $"Manual recovery override reduced {decision.StrategyTag} in {decision.RegimeTag}. {decision.Reasoning}"
                };
            }

            StrategyThrottleStatus = "Blocked";
            StrategyThrottleReason = $"{decision.StrategyTag} in {decision.RegimeTag} is blocked because the current bucket is losing quality.";
            StrategyThrottleColor = RedBrush;

            return decision with
            {
                ShouldTrade = false,
                Confidence = Math.Min(decision.Confidence, 45),
                Reasoning = $"Auto-throttle blocked {decision.StrategyTag} in {decision.RegimeTag}. {decision.Reasoning}"
            };
        }

        if (bucket.ProfitFactor < 1.25 || bucket.WinRate < 52)
        {
            StrategyThrottleStatus = "Reduced";
            StrategyThrottleReason = $"{decision.StrategyTag} in {decision.RegimeTag} is size-reduced until the bucket stabilizes.";
            StrategyThrottleColor = OrangeBrush;

            return decision with
            {
                Confidence = Math.Max(0, decision.Confidence - 10),
                PositionSizePercent = Math.Max(0m, decision.PositionSizePercent * 0.5m),
                Reasoning = $"Auto-throttle reduced size for {decision.StrategyTag} in {decision.RegimeTag}. {decision.Reasoning}"
            };
        }

        StrategyThrottleStatus = "Normal";
        StrategyThrottleReason = $"{decision.StrategyTag} in {decision.RegimeTag} is cleared for normal sizing.";
        StrategyThrottleColor = GreenBrush;
        return decision;
    }

    private void UpdateSessionSummary()
    {
        var topStrategy = StrategyLeaderboard.FirstOrDefault();
        var weakestStrategy = StrategyLeaderboard
            .OrderBy(row => row.ProfitFactor)
            .ThenBy(row => row.TotalPnL)
            .FirstOrDefault();
        var sessionActivity = BuildSessionActivitySnapshot();

        AiAnalystView.SessionSummary =
            $"Session: {sessionActivity.Count} recent {sessionActivity.SourceLabel}, {sessionActivity.ApprovedCount} approved setups, " +
            $"queue {QueueHealth.ToLowerInvariant()}. " +
            $"Top bucket: {(topStrategy == null ? "n/a" : $"{topStrategy.StrategyTag} ({topStrategy.RegimeTag})")}. " +
            $"Weakest bucket: {(weakestStrategy == null ? "n/a" : $"{weakestStrategy.StrategyTag} ({weakestStrategy.RegimeTag})")}.";
    }

    private SessionActivitySnapshot BuildSessionActivitySnapshot()
    {
        var recentTrades = _tradeCache
            .OrderByDescending(trade => trade.ExitTime ?? trade.EntryTime)
            .Take(10)
            .ToList();

        if (recentTrades.Count > 0)
        {
            var approvedTradeCount = recentTrades.Count(trade =>
                trade.Status == TradeStatus.Executed ||
                trade.Status == TradeStatus.Failed ||
                trade.Status == TradeStatus.Queued);

            return new SessionActivitySnapshot(recentTrades.Count, approvedTradeCount, "stored trade records");
        }

        var recentSignals = TradeJournal.Take(10).ToList();
        var approvedCount = recentSignals.Count(entry => entry.Status.Contains("Approved", StringComparison.OrdinalIgnoreCase));
        return new SessionActivitySnapshot(recentSignals.Count, approvedCount, "journal entries");
    }

    private void UpdateTradingMode()
    {
        TradingModeLabel = IsPaperMode ? "PAPER" : "LIVE";
        TradingModeColor = IsPaperMode ? OrangeBrush : RedBrush;
        UpdateRuntimeIntegrityStatus();
    }

    private void UpdateRuntimeIntegrityStatus()
    {
        var simulationMode = string.Equals(Environment.GetEnvironmentVariable("TMQ_SIMULATION_MODE"), "true", StringComparison.OrdinalIgnoreCase);
        var exchangeMode = string.IsNullOrWhiteSpace(ExchangeSettings.SelectedExchangeMode)
            ? "Disabled"
            : ExchangeSettings.SelectedExchangeMode;
        var hasGeminiKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TMQ_GEMINI_API_KEY"));
        var hasBinanceKeys = !string.IsNullOrWhiteSpace(ExchangeSettings.BinanceApiKey) &&
                             !string.IsNullOrWhiteSpace(ExchangeSettings.BinanceApiSecret);
        var hasOkxKeys =
            !string.IsNullOrWhiteSpace(ExchangeSettings.OkxApiKey) &&
            !string.IsNullOrWhiteSpace(ExchangeSettings.OkxPassphrase) &&
            !string.IsNullOrWhiteSpace(ExchangeSettings.OkxSecretKey);

        MarketOverview.ExecutionServiceStatus = ResolveExecutionServiceStatus(exchangeMode, simulationMode, hasBinanceKeys, hasOkxKeys);
        IsHftRuntimeAvailable = exchangeMode.Equals("Binance", StringComparison.OrdinalIgnoreCase) ||
                                exchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase) ||
                                exchangeMode.Equals("BinanceFutures", StringComparison.OrdinalIgnoreCase) ||
                                exchangeMode.Equals("BinanceFuturesTestnet", StringComparison.OrdinalIgnoreCase) ||
                                exchangeMode.Equals("OKX", StringComparison.OrdinalIgnoreCase) ||
                                exchangeMode.Equals("OKXSwap", StringComparison.OrdinalIgnoreCase);
        MarketOverview.AiProviderStatus = ResolveAiProviderStatus(hasGeminiKey, simulationMode);

        var configIssues = IdentifyConfigurationGaps(simulationMode);
        UpdateConfigValidationStatus(configIssues, simulationMode);
        UpdateIntegrityVisuals();
    }

    private string ResolveExecutionServiceStatus(string exchangeMode, bool simulationMode, bool hasBinanceKeys, bool hasOkxKeys)
    {
        var okxDemoMode = ExchangeSettings.IsDemoMode;

        if ((exchangeMode.Equals("Mock", StringComparison.OrdinalIgnoreCase) || simulationMode) && !hasBinanceKeys)
        {
            return "SIMULATION BROKER";
        }

        if ((exchangeMode.Equals("Binance", StringComparison.OrdinalIgnoreCase) ||
             exchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase) ||
             exchangeMode.Equals("BinanceFutures", StringComparison.OrdinalIgnoreCase) ||
             exchangeMode.Equals("BinanceFuturesTestnet", StringComparison.OrdinalIgnoreCase)) && hasBinanceKeys)
        {
            return exchangeMode switch
            {
                var mode when mode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase) => "BINANCE TESTNET",
                var mode when mode.Equals("BinanceFutures", StringComparison.OrdinalIgnoreCase) => "BINANCE FUTURES",
                var mode when mode.Equals("BinanceFuturesTestnet", StringComparison.OrdinalIgnoreCase) => "BINANCE FUTURES TESTNET",
                _ => "BINANCE LIVE"
            };
        }

        if ((exchangeMode.Equals("OKX", StringComparison.OrdinalIgnoreCase) ||
             exchangeMode.Equals("OKXSwap", StringComparison.OrdinalIgnoreCase)) && hasOkxKeys)
        {
            if (exchangeMode.Equals("OKXSwap", StringComparison.OrdinalIgnoreCase))
            {
                return okxDemoMode ? "OKX SWAP DEMO" : "OKX SWAP LIVE";
            }

            return okxDemoMode ? "OKX DEMO" : "OKX LIVE";
        }

        return "EXECUTION DISABLED";
    }

    private string ResolveAiProviderStatus(bool hasGeminiKey, bool simulationMode)
    {
        if (hasGeminiKey)
        {
            return "GEMINI LIVE";
        }

        if (simulationMode)
        {
            return "AI SIMULATION";
        }

        return "RULE-BASED FALLBACK";
    }

    private List<string> IdentifyConfigurationGaps(bool simulationMode)
    {
        var issues = new List<string>();
        if (!IsHftRuntimeAvailable)
        {
            issues.Add("HFT streaming disabled");
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TMQ_TELEGRAM_BOT_TOKEN")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TMQ_TELEGRAM_CHAT_ID")))
        {
            issues.Add("telegram alert not configured");
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TMQ_ALERT_WEBHOOK_URL")))
        {
            issues.Add("webhook alert not configured");
        }

        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "models", "price_prediction.onnx")))
        {
            issues.Add("onnx model path missing");
        }

        if (MarketOverview.ExecutionServiceStatus == "EXECUTION DISABLED")
        {
            issues.Add("no live exchange configured");
        }

        if (MarketOverview.AiProviderStatus == "RULE-BASED FALLBACK")
        {
            issues.Add("no live AI provider configured");
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TMQ_GEMINI_API_KEY")) && !simulationMode)
        {
            issues.Add("AI analyst running on deterministic fallback");
        }

        return issues;
    }

    private void UpdateConfigValidationStatus(List<string> issues, bool simulationMode)
    {
        if (issues.Count == 0)
        {
            MarketOverview.ConfigValidationStatus = "CONFIG VALID";
            MarketOverview.ConfigValidationSummary = "Runtime is configured for real services with no major startup gaps.";
        }
        else
        {
            MarketOverview.ConfigValidationStatus = simulationMode ? "SIMULATION CONFIG" : "CONFIG GAPS";
            MarketOverview.ConfigValidationSummary = string.Join(" | ", issues.Distinct(StringComparer.OrdinalIgnoreCase));
        }
    }

    private void UpdateIntegrityVisuals()
    {
        if (MarketOverview.ExecutionServiceStatus == "EXECUTION DISABLED")
        {
            PortfolioView.PortfolioExecutionModeLabel = "SAFE / DISABLED EXECUTION";
            PortfolioView.PortfolioExecutionModeColor = RedBrush;
            MarketOverview.RuntimeIntegrityStatus = "SAFE MODE";
            MarketOverview.RuntimeIntegrityColor = RedBrush;
            return;
        }

        if (IsPaperMode || MarketOverview.ExecutionServiceStatus == "SIMULATION BROKER")
        {
            PortfolioView.PortfolioExecutionModeLabel = "SIMULATED EXECUTION";
            PortfolioView.PortfolioExecutionModeColor = OrangeBrush;
        }
        else
        {
            PortfolioView.PortfolioExecutionModeLabel = "REAL EXECUTION";
            PortfolioView.PortfolioExecutionModeColor = GreenBrush;
        }

        if (MarketOverview.ExecutionServiceStatus == "SIMULATION BROKER" || MarketOverview.AiProviderStatus == "AI SIMULATION")
        {
            MarketOverview.RuntimeIntegrityStatus = "SIMULATION MODE";
            MarketOverview.RuntimeIntegrityColor = OrangeBrush;
            return;
        }

        if (MarketOverview.AiProviderStatus == "RULE-BASED FALLBACK")
        {
            MarketOverview.RuntimeIntegrityStatus = "LIVE RULES ONLY";
            MarketOverview.RuntimeIntegrityColor = OrangeBrush;
            return;
        }

        MarketOverview.RuntimeIntegrityStatus = "REAL SERVICES";
        MarketOverview.RuntimeIntegrityColor = GreenBrush;
    }

    private string GetCurrentExecutionModeLabel()
    {
        if (MarketOverview.ExecutionServiceStatus == "EXECUTION DISABLED")
        {
            return "SAFE";
        }

        return IsPaperMode || MarketOverview.ExecutionServiceStatus == "SIMULATION BROKER"
            ? "SIM"
            : "REAL";
    }

    private Brush GetCurrentExecutionModeColor()
    {
        return GetCurrentExecutionModeLabel() switch
        {
            "REAL" => GreenBrush,
            "SAFE" => RedBrush,
            _ => OrangeBrush
        };
    }

    private int _lastSessionHour = -1;

    private void UpdateSessionContext()
    {
        var localNow = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Turkey Standard Time");
        var hour = localNow.Hour;

        if (hour == _lastSessionHour && SessionContexts.Count > 0)
        {
            return;
        }

        _lastSessionHour = hour;

        SessionContexts.Clear();
        AddSessionContext("Asia", hour is >= 3 and < 10, "Lower-liquidity handoff. Favor patience.");
        AddSessionContext("London Killzone", hour is >= 10 and < 13, "Best displacement window for PA continuation.");
        AddSessionContext("NY Killzone", hour is >= 15 and < 18, "Watch for reversal, sweep, and expansion.");
        AddSessionContext("Close / Dead Zone", !(hour is >= 10 and < 13) && !(hour is >= 15 and < 18), "Reduce aggression when liquidity thins.");

        var active = SessionContexts.FirstOrDefault(item => item.IsActive);
        CurrentSessionLabel = active == null
            ? $"Session context: {localNow:HH:mm} Istanbul"
            : $"{active.Name} active · {localNow:HH:mm} Istanbul";
        SessionGuidance = active?.Guidance ?? "No killzone is active. Expect slower, choppier PA unless volatility expands.";
        UpdateBotLaunchSummary();
    }

    private void AddSessionContext(string name, bool isActive, string guidance)
    {
        SessionContexts.Add(new SessionContextViewModel
        {
            Name = name,
            Guidance = guidance,
            IsActive = isActive,
            Color = isActive ? GreenBrush : GrayBrush
        });
    }
}
