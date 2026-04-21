using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Exceptions;
using ThatsMyQuant.Application.Handlers;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Options;
using Xunit;

namespace ThatsMyQuant.Tests.Handlers;

public class ExecuteTradeHandlerTests
{
    private readonly ILogger<ExecuteTradeHandler> _logger;
    private readonly ILogger<AdaptiveExecutionOrchestrator> _adaptiveExecutionLogger;
    private readonly IExchangeService _exchangeService;
    private readonly IExchangeService _okxExchangeService;
    private readonly IExchangeServiceResolver _exchangeResolver;
    private readonly ITradeRepository _tradeRepository;
    private readonly ILogger<CircuitBreakerService> _circuitBreakerLogger;
    private readonly MemoryPortfolioManager _portfolio;
    private readonly CircuitBreakerService _circuitBreaker;
    private readonly PositionSupervisorService _positionSupervisor;
    private readonly AdaptiveExecutionOrchestrator _adaptiveExecutionOrchestrator;
    private readonly ExecuteTradeHandler _handler;

    public ExecuteTradeHandlerTests()
    {
        _logger = Substitute.For<ILogger<ExecuteTradeHandler>>();
        _adaptiveExecutionLogger = Substitute.For<ILogger<AdaptiveExecutionOrchestrator>>();
        _exchangeService = Substitute.For<IExchangeService>();
        _okxExchangeService = Substitute.For<IExchangeService>();
        _exchangeResolver = Substitute.For<IExchangeServiceResolver>();
        _tradeRepository = Substitute.For<ITradeRepository>();
        _circuitBreakerLogger = Substitute.For<ILogger<CircuitBreakerService>>();
        _portfolio = new MemoryPortfolioManager();
        _portfolio.SetBalance("USDT", 100_000m);
        _circuitBreaker = new CircuitBreakerService(_circuitBreakerLogger, _portfolio);
        _positionSupervisor = new PositionSupervisorService();
        _tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(Array.Empty<Trade>());
        _exchangeResolver.CanResolve(Arg.Any<string>()).Returns(true);
        _exchangeResolver.Resolve(Arg.Any<string>()).Returns(_exchangeService);
        _exchangeResolver.Resolve(Arg.Is<string>(value => value.Equals("OKX", StringComparison.OrdinalIgnoreCase))).Returns(_okxExchangeService);
        _adaptiveExecutionOrchestrator = new AdaptiveExecutionOrchestrator(
            _adaptiveExecutionLogger,
            _tradeRepository,
            _exchangeResolver,
            new MarketExecutionProfileService());

        _handler = new ExecuteTradeHandler(
            _logger,
            _tradeRepository,
            _circuitBreaker,
            _positionSupervisor,
            _adaptiveExecutionOrchestrator,
            Options.Create(new OrderBookExecutionOptions()));
    }


    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldReturnTradeAndPersist()
    {
        // Arrange
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "GARAN",
            Side = TradeSide.Buy,
            Price = 50.00m,
            Source = "Manual"
        };

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = signal.Price,
            Quantity = 100,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        // Act
        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Symbol.Should().Be("GARAN");
        result.Status.Should().Be(TradeStatus.Executed);
        result.LifecycleStatus.Should().Be(TradeLifecycleStatus.Active);
        result.Side.Should().Be(TradeSide.Buy);
        result.EntrySlippagePercent.Should().Be(0);
        await _tradeRepository.Received(1).AddAsync(Arg.Any<Trade>());
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldCarryLifecycleRiskControls()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Quantity = 1.5m,
            Source = "Manual"
        };
        signal.Metadata["StopLoss"] = "97.5";
        signal.Metadata["TakeProfit"] = "108.0";
        signal.Metadata["PartialTakeProfit"] = "104.0";
        signal.Metadata["PartialExitFraction"] = "0.40";
        signal.Metadata["BreakevenTriggerFraction"] = "0.55";
        signal.Metadata["MaxHoldingBars"] = "18";
        signal.Metadata["ExitPolicyTag"] = "TrendRunner";
        signal.Metadata["RequestedPositionFraction"] = "0.03";
        signal.Metadata["RequestedQuantity"] = "1.5";

        var expectedTrade = new Trade
        {
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            EntryPrice = 100.5m,
            Quantity = 1.5m,
            EntryTime = DateTime.UtcNow,
            Status = TradeStatus.Executed
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.LifecycleStatus.Should().Be(TradeLifecycleStatus.Active);
        result.StopLoss.Should().Be(97.5m);
        result.TakeProfit.Should().Be(108.0m);
        result.PartialTakeProfit.Should().Be(104.0m);
        result.PartialExitFraction.Should().Be(0.40m);
        result.BreakevenTriggerFraction.Should().Be(0.55m);
        result.MaxHoldingBars.Should().Be(18);
        result.RemainingQuantity.Should().Be(1.5m);
        result.RequestedQuantity.Should().Be(1.5m);
        result.PositionSizePercent.Should().Be(0.03m);
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldPersistProfitLeakMetadata()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Quantity = 1m,
            Source = "Manual"
        };
        signal.Metadata["ProfitLeakAuditSummary"] = "Exit policy leak is soft but persistent.";
        signal.Metadata["ProfitLeakWorstBucketLabel"] = "ExitPolicy";
        signal.Metadata["ProfitLeakWorstBucketBeforeLiveChange"] = "ExitPolicy: EV -12.50, PF 0.84, WR 40.0%, n=5";
        signal.Metadata["ProfitLeakWorstBucketAfterLiveChange"] = "Reduced to 0.82x after live change. ExitPolicy: EV -12.50, PF 0.84, WR 40.0%, n=5";
        signal.Metadata["ProfitLeakAuditSizeMultiplier"] = "0.82";
        signal.Metadata["ProfitLeakExactNetExpectancy"] = "-4.50";
        signal.Metadata["ProfitLeakExitPolicyNetExpectancy"] = "-12.50";
        signal.Metadata["ProfitLeakRegimeNetExpectancy"] = "-6.75";
        signal.Metadata["ProfitLeakVenueNetExpectancy"] = "-2.10";
        signal.Metadata["ProfitLeakReducedSizeNetExpectancy"] = "-9.30";
        signal.Metadata["ProfitLeakReducedSizeNoise"] = "true";
        signal.Metadata["ProfitLeakLoserClusterRate"] = "0.67";
        signal.Metadata["ProfitLeakDominantLosingExitPolicy"] = "TrendRunner";

        var expectedTrade = new Trade
        {
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            EntryPrice = 100.2m,
            Quantity = 1m,
            EntryTime = DateTime.UtcNow,
            Status = TradeStatus.Executed
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.ProfitLeakWorstBucketLabel.Should().Be("ExitPolicy");
        result.ProfitLeakWorstBucketBeforeLiveChange.Should().Contain("ExitPolicy");
        result.ProfitLeakWorstBucketAfterLiveChange.Should().Contain("Reduced to 0.82x");
        result.ProfitLeakAuditSizeMultiplier.Should().Be(0.82m);
        result.ProfitLeakExitPolicyNetExpectancy.Should().Be(-12.50m);
        result.ProfitLeakReducedSizeNoise.Should().BeTrue();
        result.ProfitLeakLoserClusterRate.Should().Be(0.67m);
        result.ProfitLeakDominantLosingExitPolicy.Should().Be("TrendRunner");
    }

    [Fact]
    public async Task Handle_AcceptedVenueOrderWithoutFill_ShouldPreserveAcceptedLifecycle()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Quantity = 2m,
            Source = "Manual"
        };

        var acceptedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            EntryPrice = signal.Price,
            RequestedQuantity = 2m,
            Quantity = 0m,
            FilledQuantity = 0m,
            RemainingQuantity = 2m,
            Status = TradeStatus.Pending,
            LifecycleStatus = TradeLifecycleStatus.Accepted,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(acceptedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.Status.Should().Be(TradeStatus.Pending);
        result.LifecycleStatus.Should().Be(TradeLifecycleStatus.Accepted);
        result.Quantity.Should().Be(0m);
        result.RemainingQuantity.Should().Be(2m);
        await _tradeRepository.Received().UpdateAsync(Arg.Is<Trade>(trade =>
            trade.LifecycleStatus == TradeLifecycleStatus.Accepted &&
            trade.Status == TradeStatus.Pending &&
            trade.RemainingQuantity == 2m));
    }

    [Fact]
    public async Task Handle_PartiallyFilledVenueOrder_ShouldPreservePartialFillLifecycle()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Quantity = 2m,
            Source = "Manual"
        };

        var partialTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            EntryPrice = 100.6m,
            RequestedQuantity = 2m,
            Quantity = 0.75m,
            FilledQuantity = 0.75m,
            RemainingQuantity = 1.25m,
            Status = TradeStatus.Executed,
            LifecycleStatus = TradeLifecycleStatus.PartiallyFilled,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(partialTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.Status.Should().Be(TradeStatus.Executed);
        result.LifecycleStatus.Should().Be(TradeLifecycleStatus.PartiallyFilled);
        result.Quantity.Should().Be(0.75m);
        result.FilledQuantity.Should().Be(0.75m);
        result.RemainingQuantity.Should().Be(1.25m);
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldCaptureEntrySlippage()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = 101m,
            Quantity = 1,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.EntrySlippagePercent.Should().Be(1m);
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldPersistEdgeScoreMetadata()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };
        signal.Metadata["EdgeScore"] = "82.50";
        signal.Metadata["EdgeBand"] = "Reduced";

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = 100m,
            Quantity = 1,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.EdgeScore.Should().Be(82.50m);
        result.EdgeBand.Should().Be("Reduced");
        await _tradeRepository.Received(1).AddAsync(Arg.Is<Trade>(trade =>
            trade.EdgeScore == 82.50m &&
            trade.EdgeBand == "Reduced"));
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldPersistVenueImpactLabel()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };
        signal.Metadata["VenueImpactLabel"] = "Venue capped size (0.96x)";
        signal.Metadata["VenueExecutionScope"] = "Binance execution is softer than target";

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = 100m,
            Quantity = 1,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.VenueImpactLabel.Should().Be("Venue capped size (0.96x)");
        result.VenueExecutionScope.Should().Be("Binance execution is softer than target");
        await _tradeRepository.Received(1).AddAsync(Arg.Is<Trade>(trade =>
            trade.VenueImpactLabel == "Venue capped size (0.96x)" &&
            trade.VenueExecutionScope == "Binance execution is softer than target"));
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldPersistExactBucketPressure()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };
        signal.Metadata["ExactBucketImpactLabel"] = "Exact bucket supports scaling";
        signal.Metadata["ExactBucketSummary"] = "Exact bucket is healthy: PF 1.44, WR 58.0%, EV 2.35.";

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = 100m,
            Quantity = 1,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.ExactBucketImpactLabel.Should().Be("Exact bucket supports scaling");
        result.ExactBucketSummary.Should().Be("Exact bucket is healthy: PF 1.44, WR 58.0%, EV 2.35.");
        await _tradeRepository.Received(1).AddAsync(Arg.Is<Trade>(trade =>
            trade.ExactBucketImpactLabel == "Exact bucket supports scaling" &&
            trade.ExactBucketSummary == "Exact bucket is healthy: PF 1.44, WR 58.0%, EV 2.35."));
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldPersistConcordanceSignal()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };
        signal.Metadata["ConcordanceImpactLabel"] = "Concordance supports scaling";
        signal.Metadata["ConcordanceSummary"] = "Exact bucket edge and venue execution are aligned for scaled deployment.";

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = 100m,
            Quantity = 1,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.ConcordanceImpactLabel.Should().Be("Concordance supports scaling");
        result.ConcordanceSummary.Should().Be("Exact bucket edge and venue execution are aligned for scaled deployment.");
        await _tradeRepository.Received(1).AddAsync(Arg.Is<Trade>(trade =>
            trade.ConcordanceImpactLabel == "Concordance supports scaling" &&
            trade.ConcordanceSummary == "Exact bucket edge and venue execution are aligned for scaled deployment."));
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldPersistFeatureDriftMetadata()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };
        signal.Metadata["FeatureDriftImpactLabel"] = "ML drift trimmed size";
        signal.Metadata["FeatureDriftSummary"] = "Elevated ML feature drift reduced model influence and trimmed size (3 features, max z-score 4.10).";
        signal.Metadata["FeatureDriftDetected"] = "true";
        signal.Metadata["FeatureDriftCritical"] = "false";
        signal.Metadata["DriftedFeatureCount"] = "3";
        signal.Metadata["FeatureDriftMaxZScore"] = "4.10";

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = 100m,
            Quantity = 1,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.FeatureDriftImpactLabel.Should().Be("ML drift trimmed size");
        result.FeatureDriftSummary.Should().Contain("Elevated ML feature drift");
        result.FeatureDriftDetected.Should().BeTrue();
        result.FeatureDriftCritical.Should().BeFalse();
        result.DriftedFeatureCount.Should().Be(3);
        result.FeatureDriftMaxZScore.Should().Be(4.10m);
        await _tradeRepository.Received(1).AddAsync(Arg.Is<Trade>(trade =>
            trade.FeatureDriftImpactLabel == "ML drift trimmed size" &&
            trade.FeatureDriftDetected &&
            !trade.FeatureDriftCritical &&
            trade.DriftedFeatureCount == 3 &&
            trade.FeatureDriftMaxZScore == 4.10m));
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldPersistModelVersion()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };
        signal.Metadata["MlModelVersion"] = "PredictiveAlphaModel@20260328063000";

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = 100m,
            Quantity = 1,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.MlModelVersion.Should().Be("PredictiveAlphaModel@20260328063000");
        await _tradeRepository.Received(1).AddAsync(Arg.Is<Trade>(trade =>
            trade.MlModelVersion == "PredictiveAlphaModel@20260328063000"));
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldPersistExitPolicyTag()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };
        signal.Metadata["ExitPolicyTag"] = "MeanReversion";

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = 100m,
            Quantity = 1,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.ExitPolicyTag.Should().Be("MeanReversion");
        await _tradeRepository.Received(1).AddAsync(Arg.Is<Trade>(trade =>
            trade.ExitPolicyTag == "MeanReversion"));
    }

    [Fact]
    public async Task Handle_SuccessfulExecution_ShouldPersistRequestedPositionFraction()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };
        signal.Metadata["RequestedPositionFraction"] = "0.0375";

        var expectedTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = 100m,
            Quantity = 1,
            EntryTime = DateTime.UtcNow
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(expectedTrade);

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.PositionSizePercent.Should().Be(0.0375m);
        await _tradeRepository.Received(1).AddAsync(Arg.Is<Trade>(trade =>
            trade.PositionSizePercent == 0.0375m));
    }

    [Fact]
    public async Task Handle_WhenPrimaryVenueDegrades_ShouldRouteToAlternateVenueAndTrimSize()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Quantity = 2.0m,
            Exchange = "Binance",
            MarketType = MarketType.Crypto,
            Source = "Manual"
        };
        signal.Metadata["PromotionStage"] = "LiveProbe";
        signal.Metadata["RequestedPositionFraction"] = "0.08";

        _tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            CreateLiveTrade("BTC-USD", "REAL", 0.32m, 720, 0.76m),
            CreateLiveTrade("BTC-USD", "REAL", 0.28m, 680, 0.82m),
            CreateLiveTrade("BTC-USD", "REAL", 0.30m, 760, 0.78m),
            CreateLiveTrade("BTC-USD", "REAL-OKX", 0.18m, 320, 0.92m),
            CreateLiveTrade("BTC-USD", "REAL-OKX", 0.17m, 310, 0.93m),
            CreateLiveTrade("BTC-USD", "REAL-OKX", 0.16m, 300, 0.94m)
        });

        _okxExchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(callInfo =>
        {
            var routedSignal = callInfo.Arg<Signal>();
            return new Trade
            {
                Id = Guid.NewGuid(),
                SignalId = routedSignal.Id,
                Symbol = routedSignal.Symbol,
                Side = routedSignal.Side,
                EntryPrice = routedSignal.Price,
                Quantity = routedSignal.Quantity,
                FilledQuantity = routedSignal.Quantity,
                RemainingQuantity = routedSignal.Quantity,
                RequestedQuantity = routedSignal.Quantity,
                Status = TradeStatus.Executed,
                LifecycleStatus = TradeLifecycleStatus.Active,
                ExecutionMode = "REAL-OKX",
                EntryTime = DateTime.UtcNow
            };
        });

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        await _okxExchangeService.Received(1).ExecuteOrderAsync(Arg.Is<Signal>(routed =>
            routed.Exchange == "OKX" &&
            routed.Quantity == 1.0m &&
            routed.Metadata["AdaptiveThrottleMultiplier"] == "0.50"));
        await _exchangeService.DidNotReceive().ExecuteOrderAsync(Arg.Any<Signal>());
        result.VenueImpactLabel.Should().Contain("Adaptive route -> OKX");
        result.VenueExecutionScope.Should().Contain("rerouted to OKX");
        result.PositionSizePercent.Should().Be(0.04m);
        result.ExecutionMode.Should().Be("REAL-OKX");
    }

    [Fact]
    public async Task Handle_WhenAlternateVenueUnavailable_ShouldRetainVenueAndThrottleSize()
    {
        Signal? capturedSignal = null;
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Quantity = 4.0m,
            Exchange = "Binance",
            MarketType = MarketType.Crypto,
            Source = "Manual"
        };
        signal.Metadata["RequestedPositionFraction"] = "0.10";

        _exchangeResolver.CanResolve(Arg.Is<string>(value => value.Equals("OKX", StringComparison.OrdinalIgnoreCase))).Returns(false);
        _tradeRepository.GetAllAsync(Arg.Any<int>()).Returns(new[]
        {
            CreateLiveTrade("BTC-USD", "REAL", 0.18m, 290, 0.92m),
            CreateLiveTrade("BTC-USD", "REAL", 0.17m, 280, 0.91m),
            CreateLiveTrade("BTC-USD", "REAL", 0.16m, 270, 0.93m)
        });

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>()).Returns(callInfo =>
        {
            var routedSignal = callInfo.Arg<Signal>();
            capturedSignal = routedSignal;
            return new Trade
            {
                Id = Guid.NewGuid(),
                SignalId = routedSignal.Id,
                Symbol = routedSignal.Symbol,
                Side = routedSignal.Side,
                EntryPrice = routedSignal.Price,
                Quantity = routedSignal.Quantity,
                FilledQuantity = routedSignal.Quantity,
                RemainingQuantity = routedSignal.Quantity,
                RequestedQuantity = routedSignal.Quantity,
                Status = TradeStatus.Executed,
                LifecycleStatus = TradeLifecycleStatus.Active,
                ExecutionMode = "REAL",
                EntryTime = DateTime.UtcNow
            };
        });

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        capturedSignal.Should().NotBeNull();
        capturedSignal!.Exchange.Should().Be("Binance");
        capturedSignal.Quantity.Should().Be(3.4m);
        capturedSignal.Metadata["AdaptiveThrottleMultiplier"].Should().Be("0.85");
        result.VenueImpactLabel.Should().Contain("Venue capped size");
        result.VenueExecutionScope.Should().Contain("size trimmed");
        result.PositionSizePercent.Should().Be(0.085m);
    }

    [Fact]
    public async Task Handle_HftSignalWithWeakLiquidity_ShouldReturnFailedTrade()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "HFT-Probe",
            Volume24h = 100_000m
        };
        signal.Metadata["ExecutionMode"] = "HFT";

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.Status.Should().Be(TradeStatus.Failed);
        await _exchangeService.DidNotReceive().ExecuteOrderAsync(Arg.Any<Signal>());
    }

    [Fact]
    public async Task Handle_HaltedCircuitBreaker_ShouldFailBeforeVenueCall()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };

        _circuitBreaker.Halt("Manual emergency stop");

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.Status.Should().Be(TradeStatus.Failed);
        result.Symbol.Should().Be("BTC-USD");
        await _exchangeService.DidNotReceive().ExecuteOrderAsync(Arg.Any<Signal>());
        await _tradeRepository.Received(1).AddAsync(Arg.Is<Trade>(trade =>
            trade.Status == TradeStatus.Failed &&
            trade.SignalId == signal.Id));
    }

    [Fact]
    public async Task Handle_ExchangeFailure_ShouldPersistFailedTradeAndNotThrow()
    {
        // Arrange
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "THYAO",
            Side = TradeSide.Sell,
            Price = 300.00m,
            Source = "TradingView"
        };

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>())
            .ThrowsAsync(new Exception("Broker connection lost"));

        // Act
        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(TradeStatus.Failed);
        result.Symbol.Should().Be("THYAO");
        result.Quantity.Should().Be(0); // Failed trade, no quantity executed
        await _tradeRepository.Received(1).AddAsync(Arg.Any<Trade>());
    }

    [Fact]
    public async Task Handle_WhenConcurrentInsertLosesRace_ShouldReturnExistingTradeAndSkipVenue()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            Price = 100m,
            Source = "Manual"
        };

        var existingTrade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            EntryPrice = 100m,
            Quantity = 1m,
            Status = TradeStatus.Executed,
            LifecycleStatus = TradeLifecycleStatus.Active,
            EntryTime = DateTime.UtcNow
        };

        _tradeRepository.GetBySignalIdAsync(signal.Id)
            .Returns(
                Task.FromResult<Trade?>(null),
                Task.FromResult<Trade?>(existingTrade));
        _tradeRepository.AddAsync(Arg.Any<Trade>())
            .ThrowsAsync(new DuplicateSignalTradeException(signal.Id));

        var result = await _handler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);

        result.Should().BeSameAs(existingTrade);
        await _exchangeService.DidNotReceive().ExecuteOrderAsync(Arg.Any<Signal>());
        await _tradeRepository.Received(1).AddAsync(Arg.Any<Trade>());
        await _tradeRepository.DidNotReceive().UpdateAsync(Arg.Any<Trade>());
    }

    [Fact]
    public async Task Handle_RepeatedExchangeFailures_ShouldTripCircuitBreaker()
    {
        var strictCircuitBreaker = new CircuitBreakerService(
            _circuitBreakerLogger,
            _portfolio,
            maxConsecutiveErrors: 3);
        var strictHandler = new ExecuteTradeHandler(
            _logger,
            _tradeRepository,
            strictCircuitBreaker,
            _positionSupervisor,
            _adaptiveExecutionOrchestrator,
            Options.Create(new OrderBookExecutionOptions()));

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>())
            .ThrowsAsync(new Exception("Venue rejected request"));

        for (var i = 0; i < 3; i++)
        {
            var signal = new Signal
            {
                Id = Guid.NewGuid(),
                Symbol = $"BTC-USD-{i}",
                Side = TradeSide.Buy,
                Price = 100m,
                Source = "Manual"
            };

            await strictHandler.Handle(new ExecuteTradeCommand(signal), CancellationToken.None);
        }

        strictCircuitBreaker.IsHalted.Should().BeTrue();
        strictCircuitBreaker.HaltReason.Should().Contain("Error cascade halt");
    }

    private static Trade CreateLiveTrade(string symbol, string executionMode, decimal slippagePercent, long latencyMs, decimal venueBias)
    {
        return new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = Guid.NewGuid(),
            Symbol = symbol,
            Side = TradeSide.Buy,
            EntryPrice = 100m,
            ExitPrice = 101m,
            Quantity = 1m,
            ClosedQuantity = 1m,
            Status = TradeStatus.Executed,
            LifecycleStatus = TradeLifecycleStatus.Closed,
            ExecutionMode = executionMode,
            PromotionStage = "ScaledLive",
            EntrySlippagePercent = slippagePercent,
            ExecutionLatencyMs = latencyMs,
            VenueExecutionBias = venueBias,
            EntryTime = DateTime.UtcNow.AddMinutes(-30),
            ExitTime = DateTime.UtcNow.AddMinutes(-5)
        };
    }
}
