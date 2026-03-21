using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Dashboard.Services;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Dashboard.ViewModels;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Domain.ValueObjects;
using Xunit;

namespace ThatsMyQuant.Dashboard.Tests;

public class MainViewModelTests
{
    [Fact]
    public async Task SubmitPromptCommand_ShouldTranslateNaturalLanguageIntoSelections()
    {
        var viewModel = CreateViewModel();
        viewModel.StrategyPrompt = "Aggressive scalp BTC with 1m timeframe";

        await viewModel.SubmitPromptCommand.ExecuteAsync(null);

        viewModel.SelectedMarket.Should().Be("Crypto");
        viewModel.SelectedSymbol.Should().Be("BTC-USD");
        viewModel.SelectedRiskProfile.Should().Be("Aggressive");
        viewModel.SelectedTimeframe.Should().Be("1m");
        viewModel.PlainEnglishTranslation.Should().Contain("aggressively");
        viewModel.PlainEnglishTranslation.Should().Contain("BTC-USD");
    }

    [Fact]
    public async Task RefreshSelectedSymbolAsync_ShouldPopulateAiSafetyPanel()
    {
        var marketDataService = Substitute.For<IMarketDataService>();
        var technicalAnalysis = Substitute.For<ITechnicalAnalysisService>();
        var aiAnalyst = Substitute.For<IAiAnalystService>();

        var candles = CreateCandles();
        var snapshot = CreateSnapshot();
        var decision = new TradeDecision
        {
            ShouldTrade = true,
            Side = TradeSide.Buy,
            Confidence = 82,
            Reasoning = "Aligned trend and momentum",
            StopLoss = 98m,
            TakeProfit = 110m,
            PositionSizePercent = 0.04m,
            Indicators = snapshot
        };

        marketDataService.GetCandlesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(candles);
        technicalAnalysis.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(decision);
        technicalAnalysis.ComputeIndicatorsAsync(Arg.Any<List<Candle>>())
            .Returns(snapshot);
        aiAnalyst.AnalyzeSnapshotAsync(Arg.Any<string>(), Arg.Any<IndicatorSnapshot>())
            .Returns(new AiAnalysisResult
            {
                ExecutiveSummary = "Trend and volume support the setup.",
                SentimentScore = 0.62,
                TradeRecommendation = "Actionable setup with managed risk",
                ExecutionReadinessScore = 78,
                RiskSummary = "Only minor resistance proximity risk remains.",
                RiskFlags = new List<string> { "Price is close to resistance" }
            });

        var viewModel = CreateViewModel(marketDataService, technicalAnalysis, aiAnalyst);
        viewModel.SelectedSymbol = "THYAO";
        viewModel.SelectedRiskProfile = "Low";

        await viewModel.RefreshSelectedSymbolAsync();

        viewModel.AiTradeRecommendation.Should().Be("Actionable setup with managed risk");
        viewModel.ExecutionReadiness.Should().Be(78);
        viewModel.TraderSafetyStatus.Should().Be("Ready with controls");
        viewModel.AiRiskSummary.Should().Contain("resistance");
        viewModel.TraderWarnings.Should().ContainSingle().Which.Should().Contain("resistance");
        viewModel.AiSentimentMode.Should().Contain("Bullish");
        viewModel.ConfluenceScore.Should().Be(82);
        viewModel.TimeframeConfluences.Should().NotBeEmpty();
        viewModel.TimeframeConfluences[0].Value.Should().Contain("PMAX");
        viewModel.HftWorkspaceHeadline.Should().Contain("Execution mode");
        viewModel.HftExecutionBias.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ResolveRiskParameters_ShouldRespectLowRiskPreset()
    {
        var risk = MainViewModel.ResolveRiskParameters("Low");

        risk.MaxDailyLossPercent.Should().Be(1.0m);
        risk.MaxPositionSizePercent.Should().Be(2.5m);
        risk.StopLossPercent.Should().Be(1.5m);
        risk.TakeProfitPercent.Should().Be(4.0m);
    }

    [Fact]
    public async Task RefreshStrategyLeaderboardAsync_ShouldAggregateByStrategyAndRegime()
    {
        var tradeRepository = Substitute.For<ITradeRepository>();
        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade { StrategyTag = "TrendConfluence", RegimeTag = "Trending", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 110m, Quantity = 1 },
            new Trade { StrategyTag = "TrendConfluence", RegimeTag = "Trending", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 95m, Quantity = 1 },
            new Trade { StrategyTag = "RangeReversion", RegimeTag = "Ranging", Status = TradeStatus.Executed, Side = TradeSide.Sell, EntryPrice = 100m, ExitPrice = 90m, Quantity = 2 }
        });

        var viewModel = CreateViewModel(tradeRepository: tradeRepository);

        await viewModel.RefreshStrategyLeaderboardAsync();

        viewModel.StrategyLeaderboard.Should().HaveCount(2);
        viewModel.StrategyLeaderboard[0].StrategyTag.Should().Be("RangeReversion");
        viewModel.StrategyLeaderboard[0].ProfitFactor.Should().BeGreaterThan(1.0);
        viewModel.StrategyLeaderboardStatus.Should().Contain("strategy buckets");
    }

    [Fact]
    public async Task RefreshSelectedSymbolAsync_ShouldBlockWeakStrategyBucket()
    {
        var marketDataService = Substitute.For<IMarketDataService>();
        var technicalAnalysis = Substitute.For<ITechnicalAnalysisService>();
        var tradeRepository = Substitute.For<ITradeRepository>();

        marketDataService.GetCandlesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(CreateCandles());
        technicalAnalysis.ComputeIndicatorsAsync(Arg.Any<List<Candle>>())
            .Returns(CreateSnapshot());
        technicalAnalysis.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(new TradeDecision
            {
                ShouldTrade = true,
                Side = TradeSide.Buy,
                Confidence = 78,
                Reasoning = "Trend confluence is active",
                StopLoss = 98m,
                TakeProfit = 112m,
                PositionSizePercent = 0.04m,
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Indicators = CreateSnapshot()
            });

        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade
            {
                Symbol = "THYAO",
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 96m,
                Quantity = 1,
                Status = TradeStatus.Executed,
                ExitTime = DateTime.UtcNow.AddDays(-3),
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending"
            },
            new Trade
            {
                Symbol = "THYAO",
                Side = TradeSide.Buy,
                EntryPrice = 101m,
                ExitPrice = 97m,
                Quantity = 1,
                Status = TradeStatus.Executed,
                ExitTime = DateTime.UtcNow.AddDays(-2),
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending"
            },
            new Trade
            {
                Symbol = "THYAO",
                Side = TradeSide.Buy,
                EntryPrice = 102m,
                ExitPrice = 98m,
                Quantity = 1,
                Status = TradeStatus.Executed,
                ExitTime = DateTime.UtcNow.AddDays(-1),
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending"
            }
        });

        var viewModel = CreateViewModel(
            marketDataService: marketDataService,
            technicalAnalysis: technicalAnalysis,
            tradeRepository: tradeRepository);

        await viewModel.RefreshStrategyLeaderboardAsync();
        await viewModel.RefreshSelectedSymbolAsync();

        viewModel.StrategyThrottleStatus.Should().Be("Blocked");
        viewModel.TradeReadiness.Should().Be("Do Not Trade");
        viewModel.BotLaunchSummary.Should().Contain("throttle blocked");
        viewModel.BotLaunchGuardStatus.Should().Be("Disabled");
        viewModel.BotRecommendedFunding.Should().Be(0);
        viewModel.DisabledStrategies.Should().Contain(strategy => strategy.Contains("TrendConfluence"));
        viewModel.DeploymentChecklist.Should().NotBeEmpty();
        viewModel.DeploymentChecklist.Any(item => !item.IsPassing).Should().BeTrue();

        await viewModel.StartTradingCommand.ExecuteAsync(null);

        viewModel.Status.Should().Be("BLOCKED");
    }

    [Fact]
    public async Task ReEnableStrategyCommand_ShouldMoveDisabledBucketIntoRecoveryMode()
    {
        var tradeRepository = Substitute.For<ITradeRepository>();
        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade
            {
                Symbol = "THYAO",
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 96m,
                Quantity = 1,
                Status = TradeStatus.Executed,
                ExitTime = DateTime.UtcNow.AddDays(-3),
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending"
            },
            new Trade
            {
                Symbol = "THYAO",
                Side = TradeSide.Buy,
                EntryPrice = 101m,
                ExitPrice = 97m,
                Quantity = 1,
                Status = TradeStatus.Executed,
                ExitTime = DateTime.UtcNow.AddDays(-2),
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending"
            },
            new Trade
            {
                Symbol = "THYAO",
                Side = TradeSide.Buy,
                EntryPrice = 102m,
                ExitPrice = 98m,
                Quantity = 1,
                Status = TradeStatus.Executed,
                ExitTime = DateTime.UtcNow.AddDays(-1),
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending"
            }
        });

        var viewModel = CreateViewModel(tradeRepository: tradeRepository);

        await viewModel.RefreshStrategyLeaderboardAsync();
        viewModel.DisabledStrategies.Should().Contain("TrendConfluence");

        viewModel.ReEnableStrategyCommand.Execute("TrendConfluence");

        viewModel.DisabledStrategies.Should().NotContain("TrendConfluence");
        viewModel.StrategyThrottleStatus.Should().Be("Reduced");
        viewModel.BotLaunchGuardStatus.Should().BeOneOf("Reduced", "Caution");
        viewModel.BotRecommendedFunding.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RunBacktestCommand_ShouldPopulateBacktestLabMetrics()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedSymbol = "THYAO";
        viewModel.SelectedTimeframe = "1h";
        viewModel.SelectedRiskProfile = "Moderate";

        await viewModel.RunBacktestCommand.ExecuteAsync(null);

        viewModel.BacktestScenario.Should().Contain("THYAO");
        viewModel.BacktestScenario.Should().Contain("1h");
        viewModel.BacktestStatus.Should().Contain("Backtest complete");
        viewModel.BacktestDateRange.Should().Contain("Date range:");
        viewModel.BacktestPnlLabel.Should().Contain("PnL:");
        viewModel.BacktestRiskSummary.Should().Contain("Sharpe");
        viewModel.BacktestTradeBreakdown.Should().Contain("Wins");
        viewModel.BacktestTrades.Should().NotBeNull();
    }

    [Fact]
    public void ToggleTradingModeCommand_ShouldSwitchBetweenPaperAndLive()
    {
        var viewModel = CreateViewModel();

        viewModel.IsPaperMode.Should().BeTrue();
        viewModel.TradingModeLabel.Should().Be("PAPER");

        viewModel.ToggleTradingModeCommand.Execute(null);

        viewModel.IsPaperMode.Should().BeFalse();
        viewModel.TradingModeLabel.Should().Be("LIVE");
    }

    [Fact]
    public void CreateViewModel_ShouldExposeConfigValidationState()
    {
        var viewModel = CreateViewModel();

        viewModel.ConfigValidationStatus.Should().NotBeNullOrWhiteSpace();
        viewModel.ConfigValidationSummary.Should().NotBeNullOrWhiteSpace();
        viewModel.PortfolioExecutionModeLabel.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ReviewTradeCommand_ShouldPopulateCoachingFeedback()
    {
        var viewModel = CreateViewModel();
        var trade = new TradeJournalEntry
        {
            Symbol = "THYAO",
            Side = "Buy",
            Confidence = 81,
            Status = "Signal Approved",
            Reasoning = "HTF alignment and displacement confirmed",
            Time = DateTime.UtcNow
        };

        await viewModel.ReviewTradeCommand.ExecuteAsync(trade);

        viewModel.SelectedTradeReviewTitle.Should().Contain("THYAO");
        viewModel.SelectedTradeReview.Should().Contain("High-confidence");
        viewModel.SelectedTradeReview.Should().Contain("displacement");
    }

    [Fact]
    public void BotLaunchSettings_ShouldUpdateLaunchSummary()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedBotBaseAsset = "ETH";
        viewModel.SelectedBotQuoteAsset = "USDT";
        viewModel.BotFundingAmount = 3500;
        viewModel.GridUpperLimit = 18;
        viewModel.GridLowerLimit = 7;
        viewModel.GridDensity = 16;
        viewModel.GridSlopeBias = 72;

        viewModel.BotLaunchSummary.Should().Contain("ETH/USDT");
        var numericFormats = new[] { "3500", "3.500", "3,500" };
        numericFormats.Any(viewModel.BotLaunchSummary.Contains).Should().BeTrue();
        viewModel.BotLaunchSummary.Should().Contain("density 16");
        viewModel.BotLaunchSummary.Should().Contain("bullish");
        viewModel.BotRecommendedFunding.Should().BeGreaterThan(0);
        viewModel.BotLaunchGuardStatus.Should().NotBeNullOrWhiteSpace();
        viewModel.DeploymentChecklist.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public void SetPortfolioRangeCommand_ShouldUpdateGrowthSummary()
    {
        var viewModel = CreateViewModel();

        viewModel.SetPortfolioRangeCommand.Execute("Yearly");

        viewModel.SelectedPortfolioRange.Should().Be("Yearly");
        viewModel.PortfolioRangeSummary.Should().Contain("Yearly");
        viewModel.PortfolioGrowthHeadline.Should().NotBeNullOrWhiteSpace();
        viewModel.PortfolioGrowthSubtext.Should().Contain("No");
        viewModel.PortfolioGrowthPoints.Should().NotBeEmpty();
        viewModel.PortfolioXAxisLabels.Should().NotBeEmpty();
    }

    [Fact]
    public void SetSelectedBotStrategyCommand_ShouldUpdateLaunchSummary()
    {
        var viewModel = CreateViewModel();

        viewModel.SetSelectedBotStrategyCommand.Execute("Grid Runner");

        viewModel.SelectedBotStrategy.Should().Be("Grid Runner");
        viewModel.BotLaunchSummary.Should().Contain("Grid Runner");
    }

    [Fact]
    public void ApplyBotPresetCommand_ShouldSetFastStrategyConfiguration()
    {
        var viewModel = CreateViewModel();

        viewModel.ApplyBotPresetCommand.Execute("Trend Attack");

        viewModel.SelectedBotStrategy.Should().Be("Trend Confluence");
        viewModel.SelectedBotObjective.Should().Be("Capital Growth");
        viewModel.SelectedRiskProfile.Should().Be("Aggressive");
        viewModel.GridDensity.Should().Be(10);
        viewModel.GridSlopeBias.Should().BeGreaterThan(60);
        viewModel.BotLaunchSummary.Should().Contain("Trend Confluence");
    }

    [Fact]
    public void AutoTuneBotSetupCommand_ShouldImproveWorkflowGuidance()
    {
        var viewModel = CreateViewModel();
        viewModel.SetSelectedBotStrategyCommand.Execute("Grid Runner");
        viewModel.BotFundingAmount = 2400;

        viewModel.AutoTuneBotSetupCommand.Execute(null);

        viewModel.SelectedBotObjective.Should().Be("Income Flow");
        viewModel.GridDensity.Should().Be(16);
        viewModel.BotProductivityScore.Should().Contain("Setup score");
        viewModel.BotSetupVelocity.Should().NotBeNullOrWhiteSpace();
        viewModel.BotWorkflowAdvice.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RefreshStrategyLeaderboardAsync_ShouldPopulateCapitalAllocator()
    {
        var tradeRepository = Substitute.For<ITradeRepository>();
        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade { StrategyTag = "TrendConfluence", RegimeTag = "Trending", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 110m, Quantity = 1, ExitTime = DateTime.UtcNow.AddHours(-3) },
            new Trade { StrategyTag = "TrendConfluence", RegimeTag = "Trending", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 108m, Quantity = 1, ExitTime = DateTime.UtcNow.AddHours(-2) },
            new Trade { StrategyTag = "BreakoutHunter", RegimeTag = "Volatile", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 107m, Quantity = 1, ExitTime = DateTime.UtcNow.AddHours(-4) },
            new Trade { StrategyTag = "BreakoutHunter", RegimeTag = "Volatile", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 109m, Quantity = 1, ExitTime = DateTime.UtcNow.AddHours(-1) },
            new Trade { StrategyTag = "BreakoutHunter", RegimeTag = "Volatile", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 101m, ExitPrice = 111m, Quantity = 1, ExitTime = DateTime.UtcNow.AddMinutes(-30) },
            new Trade { StrategyTag = "RangeReversion", RegimeTag = "Ranging", Status = TradeStatus.Executed, Side = TradeSide.Sell, EntryPrice = 100m, ExitPrice = 98m, Quantity = 1, ExitTime = DateTime.UtcNow.AddHours(-5) }
        });

        var viewModel = CreateViewModel(tradeRepository: tradeRepository);

        await viewModel.RefreshStrategyLeaderboardAsync();

        viewModel.CapitalAllocationRows.Should().NotBeEmpty();
        viewModel.CapitalAllocatorSummary.Should().Contain("Allocator");
        viewModel.CapitalAllocationRows.Sum(row => row.RecommendedCapital).Should().BeGreaterThan(0);
        viewModel.CapitalAllocationRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.PromotionStage));
        viewModel.CapitalAllocationRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.Symbol));
        viewModel.CapitalAllocationRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.SessionTag));
        viewModel.CapitalAllocatorSummary.Should().Contain("symbol-session ladder buckets");
        viewModel.StrategyLeaderboardStatus.Should().Contain("Dominant ladder stage");
    }

    [Fact]
    public async Task RefreshStrategyLeaderboardAsync_ShouldPopulateEdgeAnalytics()
    {
        var now = DateTime.UtcNow;
        var tradeRepository = Substitute.For<ITradeRepository>();
        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", ExecutionMode = "PAPER", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 112m, Quantity = 1, EdgeScore = 86m, EdgeBand = "Full", ExitPolicyTag = "TrendRunner", EntrySlippagePercent = 0.04m, ExecutionLatencyMs = 90, ExitTime = now.AddHours(-3.4) },
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", ExecutionMode = "PAPER", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 110m, Quantity = 1, EdgeScore = 84m, EdgeBand = "Reduced", ExitPolicyTag = "TrendRunner", EntrySlippagePercent = 0.05m, ExecutionLatencyMs = 95, ExitTime = now.AddHours(-3.2) },
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", ExecutionMode = "REAL", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 103m, Quantity = 1, EdgeScore = 88m, EdgeBand = "Full", ExitPolicyTag = "TrendRunner", EntrySlippagePercent = 0.08m, ExecutionLatencyMs = 180, ExitTime = now.AddHours(-3) },
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", ExecutionMode = "REAL", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 96m, Quantity = 1, EdgeScore = 83m, EdgeBand = "Reduced", ExitPolicyTag = "TrendRunner", EntrySlippagePercent = 0.07m, ExecutionLatencyMs = 160, ExitTime = now.AddHours(-2.5) },
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 95m, Quantity = 1, EdgeScore = 61m, EdgeBand = "Probe", ExitPolicyTag = "BreakoutFailure", EntrySlippagePercent = 0.42m, ExecutionLatencyMs = 1350, ExitTime = now.AddHours(-2) },
            new Trade { Symbol = "BTCUSDT", StrategyTag = "BreakoutHunter", RegimeTag = "Volatile", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 116m, Quantity = 1, EdgeScore = 74m, EdgeBand = "Reduced", ExitPolicyTag = "BreakoutExpansion", EntrySlippagePercent = 0.05m, ExecutionLatencyMs = 120, ExitTime = now.AddHours(-1) },
            new Trade { Symbol = "ETHUSDT", StrategyTag = "HftMicroEdge", RegimeTag = "Volatile", Status = TradeStatus.Failed, Side = TradeSide.Sell, EntryPrice = 100m, Quantity = 1, EntryTime = now.AddMinutes(-40) }
        });

        var viewModel = CreateViewModel(tradeRepository: tradeRepository);

        await viewModel.RefreshStrategyLeaderboardAsync();

        viewModel.EdgeScoreBands.Should().NotBeEmpty();
        viewModel.EdgeScoreBands.Any(row => row.Band == "Full").Should().BeTrue();
        viewModel.EdgeScoreBands.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ExecutionMode));
        viewModel.EdgeScoreBands.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.PromotionStage));
        viewModel.EdgeFailureConditions.Should().NotBeEmpty();
        viewModel.EdgeFailureConditions.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ExecutionModeScope));
        viewModel.EdgeFailureConditions.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.PromotionStageScope));
        viewModel.EdgeOpportunities.Should().NotBeEmpty();
        viewModel.EdgeOpportunities.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ExecutionMode));
        viewModel.EdgeOpportunities.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.PromotionStage));
        viewModel.ExitPolicyWinners.Should().NotBeEmpty();
        viewModel.ExitPolicyWinners.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ExitPolicyTag));
        viewModel.ExitPolicyWinners.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.Symbol));
        viewModel.ExitPolicyWinners.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.SessionTag));
        viewModel.ExitPolicyDriftRows.Should().NotBeEmpty();
        viewModel.ExitPolicyDriftRows.Should().OnlyContain(row => row.PaperTrades >= 2);
        viewModel.ExitPolicyDriftRows.Should().OnlyContain(row => row.LiveTrades >= 2);
        viewModel.EdgeAnalyticsStatus.Should().Contain("Opportunity zones");
        viewModel.BestEdgeZoneSummary.Should().NotBeNullOrWhiteSpace();
        viewModel.WorstEdgeFailureSummary.Should().NotBeNullOrWhiteSpace();
        viewModel.ExitPolicyAnalyticsSummary.Should().NotBeNullOrWhiteSpace();
        viewModel.ExitPolicyDriftSummary.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RefreshStrategyLeaderboardAsync_ShouldDisableStrategyWhenHighEdgeBandsStillLose()
    {
        var now = DateTime.UtcNow;
        var tradeRepository = Substitute.For<ITradeRepository>();
        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 96m, Quantity = 1, EdgeScore = 88m, EdgeBand = "Full", ExitTime = now.AddHours(-4) },
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 97m, Quantity = 1, EdgeScore = 82m, EdgeBand = "Reduced", ExitTime = now.AddHours(-3) },
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 98m, Quantity = 1, EdgeScore = 79m, EdgeBand = "Reduced", ExitTime = now.AddHours(-2) }
        });

        var viewModel = CreateViewModel(tradeRepository: tradeRepository);

        await viewModel.RefreshStrategyLeaderboardAsync();

        viewModel.DisabledStrategies.Should().Contain(strategy => strategy.Equals("TrendConfluence", StringComparison.OrdinalIgnoreCase));
        viewModel.DisabledStrategiesSummary.Should().Contain("Edge-band");
    }

    [Fact]
    public async Task RefreshStrategyLeaderboardAsync_ShouldExposePortfolioPromotionStageSummary()
    {
        var now = DateTime.UtcNow;
        var tradeRepository = Substitute.For<ITradeRepository>();
        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 108m, Quantity = 1, ExecutionMode = "REAL", PromotionStage = "ScaledLive", ExitTime = now.AddHours(-4) },
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 106m, Quantity = 1, ExecutionMode = "REAL", PromotionStage = "ScaledLive", ExitTime = now.AddHours(-2) },
            new Trade { Symbol = "BTC-USD", StrategyTag = "BreakoutHunter", RegimeTag = "Volatile", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 102m, Quantity = 1, ExecutionMode = "PAPER", PromotionStage = "Paper", ExitTime = now.AddHours(-1) }
        });

        var viewModel = CreateViewModel(tradeRepository: tradeRepository);

        await viewModel.RefreshStrategyLeaderboardAsync();

        viewModel.PortfolioPromotionStageLabel.Should().Be("ScaledLive");
        viewModel.PortfolioRangeSummary.Should().Contain("Stage ScaledLive");
        viewModel.PortfolioGrowthSubtext.Should().Contain("Dominant stage");
    }

    private static MainViewModel CreateViewModel(
        IMarketDataService? marketDataService = null,
        ITechnicalAnalysisService? technicalAnalysis = null,
        IAiAnalystService? aiAnalyst = null,
        ITradeRepository? tradeRepository = null,
        IStrategyHealthStore? strategyHealthStore = null)
    {
        if (marketDataService == null)
        {
            marketDataService = Substitute.For<IMarketDataService>();
            marketDataService.GetCandlesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(CreateCandles());
        }

        if (technicalAnalysis == null)
        {
            technicalAnalysis = Substitute.For<ITechnicalAnalysisService>();
            technicalAnalysis.ComputeIndicatorsAsync(Arg.Any<List<Candle>>())
                .Returns(CreateSnapshot());
            technicalAnalysis.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
                .Returns(new TradeDecision
                {
                    ShouldTrade = false,
                    Confidence = 40,
                    Reasoning = "Waiting for confirmation",
                    Indicators = CreateSnapshot()
                });
        }

        if (aiAnalyst == null)
        {
            aiAnalyst = Substitute.For<IAiAnalystService>();
            aiAnalyst.AnalyzeSnapshotAsync(Arg.Any<string>(), Arg.Any<IndicatorSnapshot>())
                .Returns(new AiAnalysisResult
                {
                    ExecutiveSummary = "Waiting for analysis...",
                    SentimentScore = 0,
                    TradeRecommendation = "Wait",
                    ExecutionReadinessScore = 40,
                    RiskSummary = "No setup yet.",
                    RiskFlags = new List<string> { "Momentum confirmation is weak" }
                });
        }

        var backtestEngine = new BacktestEngine(technicalAnalysis, Substitute.For<ILogger<BacktestEngine>>());
        var signalRepository = Substitute.For<ISignalRepository>();
        signalRepository.GetStatusCountsAsync().Returns(new Dictionary<TradeStatus, int>());
        if (tradeRepository == null)
        {
            tradeRepository = Substitute.For<ITradeRepository>();
            tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(Array.Empty<Trade>());
        }

        strategyHealthStore ??= Substitute.For<IStrategyHealthStore>();
        strategyHealthStore.Load().Returns(Array.Empty<StrategyHealthSnapshot>());

        return new MainViewModel(
            marketDataService,
            technicalAnalysis,
            aiAnalyst,
            Array.Empty<IAlertingService>(),
            backtestEngine,
            new RiskAgent(Substitute.For<ILogger<RiskAgent>>()),
            signalRepository,
            tradeRepository,
            new TradingLoopService(),
            strategyHealthStore,
            enableBackgroundTimers: false,
            enableAutomaticAnalysis: false);
    }

    private static List<Candle> CreateCandles()
    {
        return Enumerable.Range(0, 100).Select(i => new Candle
        {
            Timestamp = DateTime.UtcNow.AddMinutes(-100 + i),
            Open = 100 + i,
            High = 101 + i,
            Low = 99 + i,
            Close = 100.5m + i,
            Volume = 1_000_000 + (i * 10_000)
        }).ToList();
    }

    private static IndicatorSnapshot CreateSnapshot()
    {
        return new IndicatorSnapshot
        {
            CurrentPrice = 105m,
            RSI = 58,
            MACD = 1.2,
            MACDSignal = 0.8,
            MACDHistogram = 0.4,
            EMA20 = 104,
            EMA50 = 102,
            BollingerUpper = 110,
            BollingerLower = 95,
            ATR = 2.1,
            VolumeRatio = 1.8,
            AlphaTrend = new AlphaTrendResult
            {
                Value = 103.5,
                IsBullish = true,
                IsConfirmed = true,
                Signal = TradeSide.Buy
            },
            PMax = new PMaxResult
            {
                MovingAverage = 104.2,
                Value = 102.8,
                Signal = TradeSide.Buy,
                IsBullish = true
            },
            NearestSupport = new SupportResistanceLevel
            {
                Price = 101m,
                TestCount = 3,
                IsBroken = false
            },
            NearestResistance = new SupportResistanceLevel
            {
                Price = 106m,
                TestCount = 2,
                IsBroken = false
            },
            DetectedPattern = "Bullish Engulfing",
            ComputedAt = DateTime.UtcNow
        };
    }
}
