using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.IO;
using ThatsMyQuant.Dashboard.Services;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Dashboard.ViewModels;
using ThatsMyQuant.Dashboard.ViewModels.SubViewModels;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
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
        var tradeRepository = Substitute.For<ITradeRepository>();

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
        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 112m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "LiveProbe",
                EdgeScore = 54m,
                EdgeBand = "Probe",
                VenueExecutionBias = 0.92m,
                VenueExecutionScope = "BIST execution is degraded live",
                ExitTime = DateTime.UtcNow.AddHours(-4)
            },
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 97m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "LiveProbe",
                EdgeScore = 53m,
                EdgeBand = "Probe",
                VenueExecutionBias = 0.91m,
                VenueExecutionScope = "BIST execution is degraded live",
                ExitTime = DateTime.UtcNow.AddHours(-3)
            },
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 98m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "LiveProbe",
                EdgeScore = 52m,
                EdgeBand = "Probe",
                VenueExecutionBias = 0.92m,
                VenueExecutionScope = "BIST execution is degraded live",
                ExitTime = DateTime.UtcNow.AddHours(-2)
            },
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 99m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "LiveProbe",
                EdgeScore = 51m,
                EdgeBand = "Probe",
                VenueExecutionBias = 0.93m,
                VenueExecutionScope = "BIST execution is degraded live",
                ExitTime = DateTime.UtcNow.AddHours(-1)
            }
        });

        var viewModel = CreateViewModel(marketDataService, technicalAnalysis, aiAnalyst, tradeRepository);
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
        viewModel.HftWorkspaceHeadline.Should().Contain("Concordance");
        viewModel.BotStatusSummary.Should().Contain("Exact bucket");
        viewModel.BotStatusSummary.Should().Contain("Concordance");
        viewModel.ExecutionPlan.Should().Contain("Exact bucket");
        viewModel.ExecutionPlan.Should().Contain("Concordance");
        viewModel.HftRiskSummary.Should().Contain("Concordance");
        viewModel.DailyMarketBriefing.Should().Contain("Concordance");
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
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", PromotionStage = "LiveProbe", VenueExecutionBias = 0.92m, VenueExecutionScope = "BIST execution is degraded live", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 110m, Quantity = 1, ExitTime = DateTime.UtcNow.AddHours(-2) },
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", PromotionStage = "LiveProbe", VenueExecutionBias = 0.94m, VenueExecutionScope = "BIST execution is degraded live", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 95m, Quantity = 1, ExitTime = DateTime.UtcNow.AddHours(-1) },
            new Trade { Symbol = "GARAN", StrategyTag = "RangeReversion", RegimeTag = "Ranging", PromotionStage = "ScaledLive", VenueExecutionBias = 1.03m, VenueExecutionScope = "BIST execution is clean live", Status = TradeStatus.Executed, Side = TradeSide.Sell, EntryPrice = 100m, ExitPrice = 90m, Quantity = 2, ExitTime = DateTime.UtcNow.AddHours(-3) }
        });

        var viewModel = CreateViewModel(tradeRepository: tradeRepository);

        await viewModel.RefreshStrategyLeaderboardAsync();

        viewModel.StrategyLeaderboard.Should().HaveCount(2);
        viewModel.StrategyLeaderboard[0].StrategyTag.Should().Be("RangeReversion");
        viewModel.StrategyLeaderboard[0].ProfitFactor.Should().BeGreaterThan(1.0);
        viewModel.StrategyLeaderboard.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.VenuePromotionImpact));
        viewModel.StrategyLeaderboard.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.VenueExecutionSummary));
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
    public async Task ForceEnableStrategyCommand_ShouldMoveDisabledBucketIntoRecoveryMode()
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

        viewModel.ForceEnableStrategyCommand.Execute("TrendConfluence");

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
            PromotionStage = "LiveProbe",
            StageExplanation = "Stage LiveProbe via exact symbol-session bucket",
            EdgeSummary = "Edge 81 [Reduced]",
            ExitSummary = "Exit TrendRunner",
            ConcordanceImpact = "Concordance is holding LiveProbe",
            AllocatorImpact = "Allocator trimmed size (0.92x)",
            VenueImpact = "Venue capped size (0.96x)",
            Reasoning = "HTF alignment and displacement confirmed",
            Time = DateTime.UtcNow
        };

        await viewModel.ReviewTradeCommand.ExecuteAsync(trade);

        viewModel.SelectedTradeReviewTitle.Should().Contain("THYAO");
        viewModel.SelectedTradeReview.Should().Contain("High-confidence");
        viewModel.SelectedTradeReview.Should().Contain("Edge 81 [Reduced]");
        viewModel.SelectedTradeReview.Should().Contain("Exit TrendRunner");
        viewModel.SelectedTradeReview.Should().Contain("Concordance is holding LiveProbe");
        viewModel.SelectedTradeReview.Should().Contain("Venue capped size");
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
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", PromotionStage = "LiveProbe", VenueExecutionBias = 0.95m, Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 110m, Quantity = 1, ExitTime = DateTime.UtcNow.AddHours(-3) },
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", PromotionStage = "LiveProbe", VenueExecutionBias = 0.96m, Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 108m, Quantity = 1, ExitTime = DateTime.UtcNow.AddHours(-2) },
            new Trade { Symbol = "BTCUSDT", StrategyTag = "BreakoutHunter", RegimeTag = "Volatile", PromotionStage = "ScaledLive", VenueExecutionBias = 1.03m, Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 107m, Quantity = 1, ExitTime = DateTime.UtcNow.AddHours(-4) },
            new Trade { Symbol = "BTCUSDT", StrategyTag = "BreakoutHunter", RegimeTag = "Volatile", PromotionStage = "ScaledLive", VenueExecutionBias = 1.02m, Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 109m, Quantity = 1, ExitTime = DateTime.UtcNow.AddHours(-1) },
            new Trade { Symbol = "BTCUSDT", StrategyTag = "BreakoutHunter", RegimeTag = "Volatile", PromotionStage = "ScaledLive", VenueExecutionBias = 1.04m, Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 101m, ExitPrice = 111m, Quantity = 1, ExitTime = DateTime.UtcNow.AddMinutes(-30) },
            new Trade { Symbol = "GARAN", StrategyTag = "RangeReversion", RegimeTag = "Ranging", PromotionStage = "Paper", VenueExecutionBias = 0.99m, Status = TradeStatus.Executed, Side = TradeSide.Sell, EntryPrice = 100m, ExitPrice = 98m, Quantity = 1, ExitTime = DateTime.UtcNow.AddHours(-5) }
        });

        var viewModel = CreateViewModel(tradeRepository: tradeRepository);

        await viewModel.RefreshStrategyLeaderboardAsync();

        viewModel.CapitalAllocationRows.Should().NotBeEmpty();
        viewModel.CapitalAllocatorSummary.Should().Contain("Allocator");
        viewModel.CapitalAllocationRows.Sum(row => row.RecommendedCapital).Should().BeGreaterThan(0);
        viewModel.CapitalAllocationRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.PromotionStage));
        viewModel.CapitalAllocationRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.Symbol));
        viewModel.CapitalAllocationRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.SessionTag));
        viewModel.CapitalAllocationRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ExactBucketPromotionImpact));
        viewModel.CapitalAllocationRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ExactBucketSummary));
        viewModel.CapitalAllocationRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.VenuePromotionImpact));
        viewModel.CapitalAllocationRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.VenueExecutionSummary));
        viewModel.CapitalAllocationRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ConcordanceImpact));
        viewModel.CapitalAllocationRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ConcordanceSummary));
        viewModel.CapitalAllocatorSummary.Should().Contain("symbol-session ladder buckets");
        viewModel.StrategyLeaderboardStatus.Should().Contain("Dominant ladder stage");
        viewModel.StrategyLeaderboardStatus.Should().Contain("Stored stage context:");
    }

    [Fact]
    public async Task RefreshStrategyLeaderboardAsync_ShouldPopulateEdgeAnalytics()
    {
        var now = DateTime.UtcNow;
        var tradeRepository = Substitute.For<ITradeRepository>();
        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", ExecutionMode = "PAPER", PromotionStage = "Paper", FinalAllocatorBias = 1.06m, FinalAllocatorScope = "exact symbol-session bucket", VenueExecutionBias = 1.01m, VenueExecutionScope = "BIST execution is clean live", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 112m, Quantity = 1, EdgeScore = 86m, EdgeBand = "Full", ExitPolicyTag = "TrendRunner", EntrySlippagePercent = 0.04m, ExecutionLatencyMs = 90, ExitTime = now.AddHours(-3.4) },
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", ExecutionMode = "PAPER", PromotionStage = "Paper", FinalAllocatorBias = 1.04m, FinalAllocatorScope = "exact symbol-session bucket", VenueExecutionBias = 1.00m, VenueExecutionScope = "venue baseline neutral", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 110m, Quantity = 1, EdgeScore = 84m, EdgeBand = "Reduced", ExitPolicyTag = "TrendRunner", EntrySlippagePercent = 0.05m, ExecutionLatencyMs = 95, ExitTime = now.AddHours(-3.2) },
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", ExecutionMode = "REAL", PromotionStage = "ScaledLive", FinalAllocatorBias = 0.94m, FinalAllocatorScope = "exact symbol-session bucket", VenueExecutionBias = 0.96m, VenueExecutionScope = "BIST execution is softer than target", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 103m, Quantity = 1, EdgeScore = 88m, EdgeBand = "Full", ExitPolicyTag = "TrendRunner", EntrySlippagePercent = 0.08m, ExecutionLatencyMs = 180, ExitTime = now.AddHours(-3) },
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", ExecutionMode = "REAL", PromotionStage = "LiveProbe", FinalAllocatorBias = 0.92m, FinalAllocatorScope = "exact symbol-session bucket", VenueExecutionBias = 0.90m, VenueExecutionScope = "BIST execution is degraded live", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 96m, Quantity = 1, EdgeScore = 83m, EdgeBand = "Reduced", ExitPolicyTag = "TrendRunner", EntrySlippagePercent = 0.07m, ExecutionLatencyMs = 160, ExitTime = now.AddHours(-2.5) },
            new Trade { Symbol = "THYAO", StrategyTag = "TrendConfluence", RegimeTag = "Trending", PromotionStage = "LiveProbe", FinalAllocatorBias = 0.96m, FinalAllocatorScope = "symbol exit bucket", VenueExecutionBias = 0.92m, VenueExecutionScope = "venue execution degraded", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 95m, Quantity = 1, EdgeScore = 61m, EdgeBand = "Probe", ExitPolicyTag = "BreakoutFailure", EntrySlippagePercent = 0.42m, ExecutionLatencyMs = 1350, ExitTime = now.AddHours(-2) },
            new Trade { Symbol = "BTCUSDT", StrategyTag = "BreakoutHunter", RegimeTag = "Volatile", PromotionStage = "ScaledLive", FinalAllocatorBias = 1.08m, FinalAllocatorScope = "exact symbol-session bucket", VenueExecutionBias = 1.03m, VenueExecutionScope = "Binance execution is clean live", Status = TradeStatus.Executed, Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 116m, Quantity = 1, EdgeScore = 74m, EdgeBand = "Reduced", ExitPolicyTag = "BreakoutExpansion", EntrySlippagePercent = 0.05m, ExecutionLatencyMs = 120, ExitTime = now.AddHours(-1) },
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
        viewModel.StrategyLeaderboard.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ExactBucketPromotionImpact));
        viewModel.StrategyLeaderboard.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ExactBucketSummary));
        viewModel.StrategyLeaderboard.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.VenuePromotionImpact));
        viewModel.StrategyLeaderboard.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.VenueExecutionSummary));
        viewModel.StrategyLeaderboard.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ConcordanceImpact));
        viewModel.StrategyLeaderboard.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ConcordanceSummary));
        viewModel.ExitPolicyWinners.Should().NotBeEmpty();
        viewModel.ExitPolicyWinners.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ExitPolicyTag));
        viewModel.ExitPolicyWinners.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.Symbol));
        viewModel.ExitPolicyWinners.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.SessionTag));
        viewModel.ExitPolicyDriftRows.Should().NotBeEmpty();
        viewModel.ExitPolicyDriftRows.Should().OnlyContain(row => row.PaperTrades >= 2);
        viewModel.ExitPolicyDriftRows.Should().OnlyContain(row => row.LiveTrades >= 2);
        viewModel.FinalAllocatorBiasRows.Should().NotBeEmpty();
        viewModel.FinalAllocatorBiasRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.AllocatorScope));
        viewModel.FinalAllocatorBiasSummary.Should().NotBeNullOrWhiteSpace();
        viewModel.ExactBucketPromotionPressureRows.Should().NotBeEmpty();
        viewModel.ExactBucketPromotionPressureRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.PromotionImpact));
        viewModel.ExactBucketPromotionPressureRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ConcordanceImpact));
        viewModel.ExactBucketPromotionPressureRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ConcordanceSummary));
        viewModel.ExactBucketPromotionPressureSummary.Should().NotBeNullOrWhiteSpace();
        viewModel.VenueExecutionBiasRows.Should().NotBeEmpty();
        viewModel.VenueExecutionBiasRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.VenueExecutionScope));
        viewModel.VenueExecutionBiasRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.PromotionImpact));
        viewModel.VenueExecutionBiasRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ConcordanceImpact));
        viewModel.VenueExecutionBiasRows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.ConcordanceSummary));
        viewModel.VenueExecutionBiasSummary.Should().NotBeNullOrWhiteSpace();
        viewModel.VenueExecutionBiasSummary.Should().Contain("held back");
        viewModel.EdgeAnalyticsStatus.Should().Contain("persisted trade truth");
        viewModel.EdgeAnalyticsStatus.Should().Contain("Opportunity zones");
        viewModel.BestEdgeZoneSummary.Should().NotBeNullOrWhiteSpace();
        viewModel.WorstEdgeFailureSummary.Should().NotBeNullOrWhiteSpace();
        viewModel.BestEdgeZoneSummary.Should().Contain("Stored audit lead:");
        viewModel.WorstEdgeFailureSummary.Should().Contain("Stored failure lead:");
        viewModel.ExitPolicyAnalyticsSummary.Should().NotBeNullOrWhiteSpace();
        viewModel.ExitPolicyDriftSummary.Should().NotBeNullOrWhiteSpace();
        viewModel.ExitPolicyAnalyticsSummary.Should().Contain("Stored audit lead:");
        viewModel.ExitPolicyDriftSummary.Should().Contain("Stored drift lead:");
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
        viewModel.PortfolioRangeSummary.Should().Contain("Stored stage context: ScaledLive via default sizing scope");
        viewModel.PortfolioGrowthSubtext.Should().Contain("Dominant stage");
        viewModel.PortfolioGrowthSubtext.Should().Contain("Stored stage context: ScaledLive via default sizing scope");
    }

    [Fact]
    public async Task RefreshStrategyLeaderboardAsync_ShouldPushExactBucketPressureIntoLaunchGuard()
    {
        var now = DateTime.UtcNow;
        var tradeRepository = Substitute.For<ITradeRepository>();
        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "Trend Confluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 112m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "LiveProbe",
                EdgeScore = 54m,
                EdgeBand = "Probe",
                VenueExecutionBias = 0.92m,
                VenueExecutionScope = "BIST venue execution is degraded live",
                ExitTime = now.AddHours(-4)
            },
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "Trend Confluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 97m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "LiveProbe",
                EdgeScore = 53m,
                EdgeBand = "Probe",
                VenueExecutionBias = 0.91m,
                VenueExecutionScope = "BIST venue execution is degraded live",
                ExitTime = now.AddHours(-3)
            },
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "Trend Confluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 98m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "LiveProbe",
                EdgeScore = 52m,
                EdgeBand = "Probe",
                VenueExecutionBias = 0.92m,
                VenueExecutionScope = "BIST venue execution is degraded live",
                ExitTime = now.AddHours(-2)
            },
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "Trend Confluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 99m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "LiveProbe",
                EdgeScore = 51m,
                EdgeBand = "Probe",
                VenueExecutionBias = 0.93m,
                VenueExecutionScope = "BIST venue execution is degraded live",
                ExitTime = now.AddHours(-1)
            }
        });

        var viewModel = CreateViewModel(tradeRepository: tradeRepository);
        viewModel.SelectedBotStrategy = "Trend Confluence";
        viewModel.SelectedSymbol = "THYAO";

        await viewModel.RefreshStrategyLeaderboardAsync();

        viewModel.BotLaunchGuardStatus.Should().Be("Blocked");
        viewModel.BotLaunchGuardReason.Should().Contain("Exact bucket");
        viewModel.BotLaunchSummary.Should().Contain("exact bucket is capping");
        viewModel.BotLaunchSummary.Should().Contain("concordance is capping near paper");
        viewModel.DeploymentChecklist.Should().Contain(item => item.Title == "Exact bucket pressure" && !item.IsPassing);
        viewModel.DeploymentChecklist.Should().Contain(item => item.Title == "Concordance" && !item.IsPassing);
    }

    [Fact]
    public async Task RefreshStrategyLeaderboardAsync_ShouldPreferStoredTradeRecordsInSessionSummary()
    {
        var now = DateTime.UtcNow;
        var tradeRepository = Substitute.For<ITradeRepository>();
        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 108m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "ScaledLive",
                ExitTime = now.AddMinutes(-20)
            },
            new Trade
            {
                Symbol = "BTCUSDT",
                StrategyTag = "BreakoutHunter",
                RegimeTag = "Volatile",
                Status = TradeStatus.Failed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "LiveProbe",
                EntryTime = now.AddMinutes(-10)
            }
        });

        var viewModel = CreateViewModel(tradeRepository: tradeRepository);

        await viewModel.RefreshStrategyLeaderboardAsync();

        viewModel.SessionSummary.Should().Contain("stored trade records");
        viewModel.SessionSummary.Should().Contain("2 approved setups");
        viewModel.SessionSummary.Should().NotContain("journal entries");
    }

    [Fact]
    public async Task RefreshStrategyLeaderboardAsync_ShouldPreferStoredExactBucketPressureInAnalyticsSummaries()
    {
        var now = DateTime.UtcNow;
        var tradeRepository = Substitute.For<ITradeRepository>();
        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 105m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "ScaledLive",
                EdgeScore = 84m,
                EdgeBand = "Reduced",
                ExactBucketImpactLabel = "Exact bucket supports scaling",
                ExactBucketSummary = "Stored pressure says this exact bucket supports scaled deployment.",
                ExitTime = now.AddMinutes(-10)
            },
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 104m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "ScaledLive",
                EdgeScore = 81m,
                EdgeBand = "Reduced",
                ExactBucketImpactLabel = "Exact bucket supports scaling",
                ExactBucketSummary = "Stored pressure says this exact bucket supports scaled deployment.",
                ExitTime = now.AddMinutes(-20)
            },
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 103m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "ScaledLive",
                EdgeScore = 79m,
                EdgeBand = "Reduced",
                ExactBucketImpactLabel = "Exact bucket supports scaling",
                ExactBucketSummary = "Stored pressure says this exact bucket supports scaled deployment.",
                ExitTime = now.AddMinutes(-30)
            }
        });

        var viewModel = CreateViewModel(tradeRepository: tradeRepository);

        await viewModel.RefreshStrategyLeaderboardAsync();

        viewModel.ExactBucketPromotionPressureRows.Should().NotBeEmpty();
        viewModel.ExactBucketPromotionPressureRows[0].PromotionImpact.Should().Be("Exact bucket supports scaling");
        viewModel.ExactBucketPromotionPressureRows[0].Summary.Should().Be("Stored pressure says this exact bucket supports scaled deployment.");
        viewModel.StrategyLeaderboard.Should().Contain(row => row.ExactBucketSummary == "Stored pressure says this exact bucket supports scaled deployment.");
    }

    [Fact]
    public async Task RefreshStrategyLeaderboardAsync_ShouldPreferStoredVenueContextInAnalyticsSummaries()
    {
        var now = DateTime.UtcNow;
        var tradeRepository = Substitute.For<ITradeRepository>();
        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 104m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "ScaledLive",
                VenueExecutionBias = 0.96m,
                VenueExecutionScope = "Stored venue scope says fill quality is soft.",
                VenueImpactLabel = "Venue capped size (stored truth)",
                ExitTime = now.AddMinutes(-10)
            },
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 103m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "ScaledLive",
                VenueExecutionBias = 0.97m,
                VenueExecutionScope = "Stored venue scope says fill quality is soft.",
                VenueImpactLabel = "Venue capped size (stored truth)",
                ExitTime = now.AddMinutes(-20)
            },
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 102m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "ScaledLive",
                VenueExecutionBias = 0.95m,
                VenueExecutionScope = "Stored venue scope says fill quality is soft.",
                VenueImpactLabel = "Venue capped size (stored truth)",
                ExitTime = now.AddMinutes(-30)
            }
        });

        var viewModel = CreateViewModel(tradeRepository: tradeRepository);

        await viewModel.RefreshStrategyLeaderboardAsync();

        viewModel.VenueExecutionBiasRows.Should().NotBeEmpty();
        viewModel.VenueExecutionBiasRows[0].Summary.Should().Contain("Venue capped size (stored truth)");
        viewModel.VenueExecutionBiasRows[0].Summary.Should().Contain("Stored venue scope says fill quality is soft.");
        viewModel.StrategyLeaderboard.Should().Contain(row => row.VenueExecutionSummary.Contains("Venue capped size (stored truth)"));
    }

    [Fact]
    public async Task RefreshStrategyLeaderboardAsync_ShouldPreferStoredAllocatorContextInSummaries()
    {
        var now = DateTime.UtcNow;
        var tradeRepository = Substitute.For<ITradeRepository>();
        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 105m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "ScaledLive",
                FinalAllocatorBias = 1.04m,
                FinalAllocatorScope = "exact symbol-session bucket",
                ExitTime = now.AddMinutes(-10)
            },
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 104m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "ScaledLive",
                FinalAllocatorBias = 1.03m,
                FinalAllocatorScope = "exact symbol-session bucket",
                ExitTime = now.AddMinutes(-20)
            },
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 103m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "ScaledLive",
                FinalAllocatorBias = 1.05m,
                FinalAllocatorScope = "exact symbol-session bucket",
                ExitTime = now.AddMinutes(-30)
            }
        });

        var viewModel = CreateViewModel(tradeRepository: tradeRepository);

        await viewModel.RefreshStrategyLeaderboardAsync();

        viewModel.FinalAllocatorBiasRows.Should().NotBeEmpty();
        viewModel.FinalAllocatorBiasRows[0].Summary.Should().Contain("Allocator boosted size");
        viewModel.FinalAllocatorBiasRows[0].Summary.Should().Contain("Stored allocator scope: exact symbol-session bucket");
        viewModel.CapitalAllocatorSummary.Should().Contain("Allocator boosted size");
        viewModel.CapitalAllocatorSummary.Should().Contain("exact symbol-session bucket");
    }

    [Fact]
    public async Task RefreshStrategyLeaderboardAsync_ShouldPreferStoredVenueImpactLabelInTradeJournal()
    {
        var now = DateTime.UtcNow;
        var tradeRepository = Substitute.For<ITradeRepository>();
        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 106m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "ScaledLive",
                FinalAllocatorBias = 1.04m,
                FinalAllocatorScope = "exact symbol-session bucket",
                EdgeScore = 82.5m,
                EdgeBand = "Reduced",
                ExactBucketImpactLabel = "Exact bucket supports scaling",
                ExactBucketSummary = "Stored exact bucket summary",
                VenueExecutionBias = 0.95m,
                VenueImpactLabel = "Venue capped size (stored truth)",
                ConcordanceImpactLabel = "Venue is capping real edge",
                ConcordanceSummary = "Stored concordance says venue execution is undermining a good exact bucket.",
                ExitPolicyTag = "TrendRunner",
                ExitTime = now.AddMinutes(-10)
            }
        });

        var viewModel = CreateViewModel(tradeRepository: tradeRepository);

        await viewModel.RefreshStrategyLeaderboardAsync();

        viewModel.TradeJournal.Should().NotBeEmpty();
        viewModel.TradeJournal[0].VenueImpact.Should().Be("Venue capped size (stored truth)");
        viewModel.TradeJournal[0].AllocatorImpact.Should().Be("Allocator boosted size (1.04x)");
        viewModel.TradeJournal[0].PromotionStage.Should().Be("ScaledLive");
        viewModel.TradeJournal[0].StageExplanation.Should().Be("Stage ScaledLive via exact symbol-session bucket");
        viewModel.TradeJournal[0].ExactBucketImpact.Should().Be("Exact bucket supports scaling");
        viewModel.TradeJournal[0].ExactBucketSummary.Should().Be("Stored exact bucket summary");
        viewModel.TradeJournal[0].ConcordanceImpact.Should().Be("Venue is capping real edge");
        viewModel.TradeJournal[0].ConcordanceSummary.Should().Be("Stored concordance says venue execution is undermining a good exact bucket.");
        viewModel.TradeJournal[0].EdgeSummary.Should().Be("Edge 82.5 [Reduced]");
        viewModel.TradeJournal[0].ExitSummary.Should().Be("Exit TrendRunner");
        viewModel.TradeJournal[0].Reasoning.Should().Contain("Reduced");
        viewModel.TradeJournal[0].Reasoning.Should().Contain("Exit TrendRunner");
        viewModel.TradeJournal[0].Reasoning.Should().Contain("Exact bucket supports scaling");
        viewModel.TradeJournal[0].Reasoning.Should().Contain("Venue is capping real edge");
        viewModel.OperatorAuditAlerts.Should().NotBeEmpty();
        viewModel.OperatorAuditAlerts[0].Headline.Should().Contain("THYAO");
        viewModel.OperatorAuditAlerts[0].Detail.Should().Contain("Edge 82.5 [Reduced]");
        viewModel.OperatorAuditAlerts[0].Detail.Should().Contain("Exit TrendRunner");
        viewModel.OperatorAuditAlerts[0].Detail.Should().Contain("Exact bucket supports scaling");
        viewModel.OperatorAuditAlerts[0].Detail.Should().Contain("Venue is capping real edge");
    }

    [Fact]
    public async Task ExportLedgerCommand_ShouldWriteStoredTruthAuditCsv()
    {
        var now = DateTime.UtcNow;
        var tradeRepository = Substitute.For<ITradeRepository>();
        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 106m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "ScaledLive",
                FinalAllocatorBias = 1.04m,
                FinalAllocatorScope = "exact symbol-session bucket",
                EdgeScore = 82.5m,
                EdgeBand = "Reduced",
                ExactBucketImpactLabel = "Exact bucket supports scaling",
                ExactBucketSummary = "Stored exact bucket summary",
                VenueExecutionBias = 0.95m,
                VenueImpactLabel = "Venue capped size (stored truth)",
                ConcordanceImpactLabel = "Venue is capping real edge",
                ConcordanceSummary = "Stored concordance says venue execution is undermining a good exact bucket.",
                ExitPolicyTag = "TrendRunner",
                ExitTime = now.AddMinutes(-10)
            }
        });

        var viewModel = CreateViewModel(tradeRepository: tradeRepository);

        await viewModel.RefreshStrategyLeaderboardAsync();

        viewModel.TradeJournal[0].EdgeSummary = "Edge fake";
        viewModel.TradeJournal[0].ExitSummary = "Exit fake";
        viewModel.TradeJournal[0].VenueImpact = "Venue fake";
        viewModel.TradeJournal[0].AllocatorImpact = "Allocator fake";

        viewModel.ExportLedgerCommand.Execute(null);

        viewModel.LastLedgerExportPath.Should().NotBeNullOrWhiteSpace();
        File.Exists(viewModel.LastLedgerExportPath).Should().BeTrue();

        var csv = File.ReadAllText(viewModel.LastLedgerExportPath);
        csv.Should().Contain("PromotionStage");
        csv.Should().Contain("Edge 82.5 [Reduced]");
        csv.Should().Contain("Exit TrendRunner");
        csv.Should().Contain("Venue is capping real edge");
        csv.Should().Contain("Venue capped size (stored truth)");
        csv.Should().Contain("Allocator boosted size (1.04x)");

        File.Delete(viewModel.LastLedgerExportPath);
    }

    [Fact]
    public async Task ReviewTradeCommand_ShouldPreferPersistedTradeTruthWhenJournalRowWasMutated()
    {
        var now = DateTime.UtcNow;
        var tradeRepository = Substitute.For<ITradeRepository>();
        tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            new Trade
            {
                Symbol = "THYAO",
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending",
                Status = TradeStatus.Executed,
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                ExitPrice = 106m,
                Quantity = 1,
                ExecutionMode = "REAL",
                PromotionStage = "ScaledLive",
                FinalAllocatorBias = 1.04m,
                FinalAllocatorScope = "exact symbol-session bucket",
                EdgeScore = 82.5m,
                EdgeBand = "Reduced",
                ExactBucketImpactLabel = "Exact bucket supports scaling",
                ExactBucketSummary = "Stored exact bucket summary",
                VenueExecutionBias = 0.95m,
                VenueImpactLabel = "Venue capped size (stored truth)",
                ConcordanceImpactLabel = "Venue is capping real edge",
                ConcordanceSummary = "Stored concordance says venue execution is undermining a good exact bucket.",
                ExitPolicyTag = "TrendRunner",
                ExitTime = now.AddMinutes(-10)
            }
        });

        var viewModel = CreateViewModel(tradeRepository: tradeRepository);

        await viewModel.RefreshStrategyLeaderboardAsync();

        var row = viewModel.TradeJournal[0];
        row.EdgeSummary = "Edge fake";
        row.ExitSummary = "Exit fake";
        row.VenueImpact = "Venue fake";
        row.AllocatorImpact = "Allocator fake";
        row.ExactBucketImpact = "Bucket fake";
        row.ConcordanceImpact = "Concordance fake";

        await viewModel.ReviewTradeCommand.ExecuteAsync(row);

        viewModel.SelectedTradeReview.Should().Contain("Edge 82.5 [Reduced]");
        viewModel.SelectedTradeReview.Should().Contain("Exit TrendRunner");
        viewModel.SelectedTradeReview.Should().Contain("Exact bucket supports scaling");
        viewModel.SelectedTradeReview.Should().Contain("Venue is capping real edge");
        viewModel.SelectedTradeReview.Should().Contain("Venue capped size (stored truth)");
        viewModel.SelectedTradeReview.Should().Contain("Allocator boosted size (1.04x)");
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

        var agentCoordinator = new AgentCoordinator(
            Array.Empty<IFoundationAgent>(),
            Array.Empty<IPatternAgent>(),
            Array.Empty<IStrategyAgent>(),
            Array.Empty<IRiskGuardAgent>(),
            Array.Empty<IMetaLearningAgent>(),
            Substitute.For<ILogger<AgentCoordinator>>());

        var backtestEngine = new BacktestEngineApp(technicalAnalysis, agentCoordinator, Substitute.For<ILogger<BacktestEngineApp>>());
        var signalRepository = Substitute.For<ISignalRepository>();
        signalRepository.GetStatusCountsAsync().Returns(new Dictionary<TradeStatus, int>());
        if (tradeRepository == null)
        {
            tradeRepository = Substitute.For<ITradeRepository>();
            tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(Array.Empty<Trade>());
        }

        strategyHealthStore ??= Substitute.For<IStrategyHealthStore>();
        strategyHealthStore.LoadAsync().Returns(Task.FromResult<IReadOnlyList<StrategyHealthSnapshot>>(Array.Empty<StrategyHealthSnapshot>()));

        // Moved up for BacktestEngineApp dependency
        // var agentCoordinator = ...;

        return new MainViewModel(
            marketDataService,
            technicalAnalysis,
            aiAnalyst,
            Array.Empty<IAlertingService>(),
            backtestEngine,
            new RiskAgent(Substitute.For<ILogger<RiskAgent>>()),
            signalRepository,
            tradeRepository,
            new TradingLoopService(Substitute.For<ILogger<TradingLoopService>>()),
            strategyHealthStore,
            agentCoordinator,
            new FocusedBacktestViewModel(agentCoordinator, marketDataService, Array.Empty<IFocusedAgent>()),
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
