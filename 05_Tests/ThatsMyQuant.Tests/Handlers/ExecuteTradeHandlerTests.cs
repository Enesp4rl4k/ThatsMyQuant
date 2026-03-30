using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Application.Handlers;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using Xunit;

namespace ThatsMyQuant.Tests.Handlers;

public class ExecuteTradeHandlerTests
{
    private readonly ILogger<ExecuteTradeHandler> _logger;
    private readonly IExchangeService _exchangeService;
    private readonly ITradeRepository _tradeRepository;
    private readonly ILogger<CircuitBreakerService> _circuitBreakerLogger;
    private readonly MemoryPortfolioManager _portfolio;
    private readonly CircuitBreakerService _circuitBreaker;
    private readonly PositionSupervisorService _positionSupervisor;
    private readonly ExecuteTradeHandler _handler;

    public ExecuteTradeHandlerTests()
    {
        _logger = Substitute.For<ILogger<ExecuteTradeHandler>>();
        _exchangeService = Substitute.For<IExchangeService>();
        _tradeRepository = Substitute.For<ITradeRepository>();
        _circuitBreakerLogger = Substitute.For<ILogger<CircuitBreakerService>>();
        _portfolio = new MemoryPortfolioManager();
        _portfolio.SetBalance("USDT", 100_000m);
        _circuitBreaker = new CircuitBreakerService(_circuitBreakerLogger, _portfolio);
        _positionSupervisor = new PositionSupervisorService();

        _handler = new ExecuteTradeHandler(_logger, _exchangeService, _tradeRepository, _circuitBreaker, _positionSupervisor);
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
    public async Task Handle_RepeatedExchangeFailures_ShouldTripCircuitBreaker()
    {
        var strictCircuitBreaker = new CircuitBreakerService(
            _circuitBreakerLogger,
            _portfolio,
            maxConsecutiveErrors: 3);
        var strictHandler = new ExecuteTradeHandler(_logger, _exchangeService, _tradeRepository, strictCircuitBreaker, _positionSupervisor);

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
}
