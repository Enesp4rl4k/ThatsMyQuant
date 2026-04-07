using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class HftExecutionServiceTests
{
    private readonly IExchangeService _exchangeService;
    private readonly ILogger<HftExecutionService> _logger;
    private readonly IMarketExecutionProfileService _profileService;
    private readonly ExecutionOptions _execOptions;
    private readonly HftExecutionService _sut;

    // Default profile: crypto venue with generous limits so happy-path passes all guards.
    // PreferredMaxLatencyMs is set high enough (200) so that even with volume-based liquidity
    // penalties the projected latency stays within the guard threshold.
    private static readonly MarketExecutionProfile DefaultProfile = new(
        Market: "Crypto",
        Venue: "Binance",
        SlippagePercent: 0.05m,
        SpreadPercent: 0.02m,
        CommissionPercentPerSide: 0.04m,
        MinHftVolume24h: 500_000m,
        PreferredMaxLatencyMs: 200);

    public HftExecutionServiceTests()
    {
        _exchangeService = Substitute.For<IExchangeService>();
        _logger = Substitute.For<ILogger<HftExecutionService>>();
        _profileService = Substitute.For<IMarketExecutionProfileService>();
        _execOptions = new ExecutionOptions();

        _profileService
            .ResolveForSymbol(Arg.Any<string>(), Arg.Any<IDictionary<string, string>?>())
            .Returns(DefaultProfile);

        var options = Substitute.For<IOptions<ExecutionOptions>>();
        options.Value.Returns(_execOptions);

        _sut = new HftExecutionService(
            _exchangeService,
            _logger,
            _profileService,
            options);
    }

    private static Signal CreateCryptoSignal(decimal volume24h = 2_000_000m, int confidence = 90)
    {
        // Use a timestamp inside the preferred HFT window (11:00 Turkey time = 08:00 UTC)
        var utcTimestamp = new DateTime(2026, 4, 4, 8, 0, 0, DateTimeKind.Utc);

        return new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTC-USDT",
            Side = TradeSide.Buy,
            Price = 65_000m,
            Source = "HFT-Probe",
            Confidence = confidence,
            Volume24h = volume24h,
            Timestamp = utcTimestamp,
            StrategyTag = "Scalping",
            RegimeTag = "Trending"
        };
    }

    private static Trade CreateExpectedTrade(Signal signal, decimal fillPrice = 65_010m)
    {
        return new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            EntryPrice = fillPrice,
            Quantity = 0.5m,
            Status = TradeStatus.Executed,
            EntryTime = DateTime.UtcNow
        };
    }

    // ───────────────────────────────────────────────
    // 1. Happy path
    // ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ShouldReturnTrade_WhenAllChecksPass()
    {
        // Arrange
        var signal = CreateCryptoSignal();
        var expectedTrade = CreateExpectedTrade(signal);

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>(), Arg.Any<CancellationToken>())
            .Returns(expectedTrade);

        // Act
        var result = await _sut.ExecuteAsync(signal, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Symbol.Should().Be("BTC-USDT");
        result.ExecutionMode.Should().Be("REAL");
        result.PromotionStage.Should().Be("LiveProbe");
        result.Status.Should().Be(TradeStatus.Executed);
        await _exchangeService.Received(1).ExecuteOrderAsync(signal, Arg.Any<CancellationToken>());
    }

    // ───────────────────────────────────────────────
    // 2. Liquidity below minimum
    // ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ShouldReject_WhenLiquidityBelowMinimum()
    {
        // Arrange: Volume24h is below DefaultProfile.MinHftVolume24h (500_000)
        var signal = CreateCryptoSignal(volume24h: 100_000m);

        // Act
        var result = await _sut.ExecuteAsync(signal, CancellationToken.None);

        // Assert
        result.Status.Should().Be(TradeStatus.Failed);
        result.Quantity.Should().Be(0);
        result.Symbol.Should().Be("BTC-USDT");
        result.SignalId.Should().Be(signal.Id);
        await _exchangeService.DidNotReceive().ExecuteOrderAsync(Arg.Any<Signal>(), Arg.Any<CancellationToken>());
    }

    // ───────────────────────────────────────────────
    // 3. Projected latency exceeds budget
    // ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ShouldReject_WhenProjectedLatencyExceedsBudget()
    {
        // Arrange: Use a tight-latency profile and low confidence to inflate projected latency.
        // EstimateProjectedLatencyMs = PreferredMaxLatencyMs + confidencePenalty + liquidityPenalty
        // Guard threshold = PreferredMaxLatencyMs * LatencyGuardMultiplier (default 1.35)
        // With PreferredMaxLatencyMs=10, threshold = 13.5ms.
        // confidencePenalty = max(0, 80 - 10) * 0.8 = 56  (confidence=10)
        // That alone (10 + 56 = 66) exceeds 13.5, so it will reject.
        var tightProfile = new MarketExecutionProfile(
            Market: "Crypto",
            Venue: "Binance",
            SlippagePercent: 0.05m,
            SpreadPercent: 0.02m,
            CommissionPercentPerSide: 0.04m,
            MinHftVolume24h: 500_000m,
            PreferredMaxLatencyMs: 10);

        _profileService
            .ResolveForSymbol(Arg.Any<string>(), Arg.Any<IDictionary<string, string>?>())
            .Returns(tightProfile);

        var signal = CreateCryptoSignal(volume24h: 2_000_000m, confidence: 10);

        // Act
        var result = await _sut.ExecuteAsync(signal, CancellationToken.None);

        // Assert
        result.Status.Should().Be(TradeStatus.Failed);
        result.Quantity.Should().Be(0);
        await _exchangeService.DidNotReceive().ExecuteOrderAsync(Arg.Any<Signal>(), Arg.Any<CancellationToken>());
    }

    // ───────────────────────────────────────────────
    // 4. Projected slippage exceeds guardrail
    // ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ShouldReject_WhenProjectedSlippageExceedsGuardrail()
    {
        // Arrange: Use a profile with tight slippage budget and very low volume
        // to push projected slippage above the guard.
        //
        // EstimateProjectedSlippagePercent:
        //   volumeRatio = Volume24h / MinHftVolume24h
        //   If volumeRatio < VolumeLowRatioThreshold (1.0), use VolumeFloorSlippageMultiplier (1.80)
        //   projected = SlippagePercent * 1.80
        //   guard = SlippagePercent * SlippageGuardMultiplier (1.75)
        //   1.80 > 1.75, so it rejects.
        //
        // We need volume > 0 and volume >= MinHftVolume24h to pass liquidity check,
        // but volumeRatio < 1.0 for floor multiplier.
        // Solution: set MinHftVolume24h low enough that volume passes, but ratio is below 1.0.
        var slippageProfile = new MarketExecutionProfile(
            Market: "Crypto",
            Venue: "Binance",
            SlippagePercent: 0.10m,
            SpreadPercent: 0.02m,
            CommissionPercentPerSide: 0.04m,
            MinHftVolume24h: 2_500_000m,
            PreferredMaxLatencyMs: 200);

        _profileService
            .ResolveForSymbol(Arg.Any<string>(), Arg.Any<IDictionary<string, string>?>())
            .Returns(slippageProfile);

        // Volume24h = 2_500_000 (passes liquidity check, ratio = 1.0 exactly)
        // volumeRatio = 1.0 -> that hits VolumeLowRatioThreshold (1.0) -> VolumeLowSlippageMultiplier (1.35)
        // projected = 0.10 * 1.35 = 0.135
        // guard = 0.10 * 1.75 = 0.175
        // 0.135 < 0.175, that passes. We need ratio < 1.0 for VolumeFloorSlippageMultiplier.
        // Use Volume24h just barely passing liquidity but ratio < 1.0? Not possible if
        // MinHftVolume24h is the liquidity threshold itself.
        // Actually, the liquidity check is: Volume24h > 0 && Volume24h < profile.MinHftVolume24h -> reject
        // So Volume24h >= MinHftVolume24h passes.
        // For volumeRatio < 1.0, we need Volume24h / MinHftVolume24h < 1.0, which is Volume24h < MinHftVolume24h -> rejected by liquidity check.
        //
        // Alternative: set Volume24h = 0 to skip liquidity check, and also skip volume ratio (defaults to 1.0).
        // With ratio = 1.0 and VolumeLowRatioThreshold = 1.0, it picks VolumeLowSlippageMultiplier (1.35).
        // projected = SlippagePercent * 1.35. guard = SlippagePercent * 1.75. 1.35 < 1.75 -> passes.
        //
        // Best approach: lower SlippageGuardMultiplier in ExecutionOptions to be tighter.
        _execOptions.SlippageGuardMultiplier = 1.0m;

        // With guard multiplier = 1.0:
        // guard = 0.10 * 1.0 = 0.10
        // With Volume24h=2_500_000, ratio=1.0, multiplier=VolumeLowSlippageMultiplier=1.35
        // projected = 0.10 * 1.35 = 0.135
        // 0.135 > 0.10 -> reject
        var signal = CreateCryptoSignal(volume24h: 2_500_000m, confidence: 90);

        // Act
        var result = await _sut.ExecuteAsync(signal, CancellationToken.None);

        // Assert
        result.Status.Should().Be(TradeStatus.Failed);
        result.Quantity.Should().Be(0);
        await _exchangeService.DidNotReceive().ExecuteOrderAsync(Arg.Any<Signal>(), Arg.Any<CancellationToken>());
    }

    // ───────────────────────────────────────────────
    // 5. Outside preferred HFT window
    // ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ShouldReject_OutsidePreferredHftWindow()
    {
        // Arrange: For Crypto (symbol contains "-"), the HFT window in Turkey time is
        // 10:00-13:00 or 15:00-18:00. Use 03:00 UTC = 06:00 Turkey time -> outside window.
        var signal = CreateCryptoSignal();
        signal.Timestamp = new DateTime(2026, 4, 4, 3, 0, 0, DateTimeKind.Utc);

        // Act
        var result = await _sut.ExecuteAsync(signal, CancellationToken.None);

        // Assert
        result.Status.Should().Be(TradeStatus.Failed);
        result.Quantity.Should().Be(0);
        result.Symbol.Should().Be("BTC-USDT");
        await _exchangeService.DidNotReceive().ExecuteOrderAsync(Arg.Any<Signal>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(7, 0)]  // 07:00 UTC = 10:00 Turkey -> inside window
    [InlineData(9, 30)] // 09:30 UTC = 12:30 Turkey -> inside window
    [InlineData(12, 0)] // 12:00 UTC = 15:00 Turkey -> inside window
    [InlineData(14, 0)] // 14:00 UTC = 17:00 Turkey -> inside window
    public async Task ExecuteAsync_ShouldAllow_InsidePreferredHftWindow(int utcHour, int utcMinute)
    {
        // Arrange
        var signal = CreateCryptoSignal();
        signal.Timestamp = new DateTime(2026, 4, 4, utcHour, utcMinute, 0, DateTimeKind.Utc);

        var expectedTrade = CreateExpectedTrade(signal);
        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>(), Arg.Any<CancellationToken>())
            .Returns(expectedTrade);

        // Act
        var result = await _sut.ExecuteAsync(signal, CancellationToken.None);

        // Assert
        result.Status.Should().NotBe(TradeStatus.Failed);
        await _exchangeService.Received(1).ExecuteOrderAsync(signal, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_BistSymbol_ShouldAlwaysPassWindowCheck()
    {
        // Arrange: BIST symbols (no "-" or "=") always pass the HFT window check.
        // Use 03:00 UTC which would fail for Crypto but should pass for BIST.
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "GARAN",
            Side = TradeSide.Buy,
            Price = 50m,
            Source = "HFT-Probe",
            Confidence = 90,
            Volume24h = 2_000_000m,
            Timestamp = new DateTime(2026, 4, 4, 3, 0, 0, DateTimeKind.Utc),
            StrategyTag = "Scalping",
            RegimeTag = "Trending"
        };

        var expectedTrade = CreateExpectedTrade(signal, fillPrice: 50.05m);
        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>(), Arg.Any<CancellationToken>())
            .Returns(expectedTrade);

        // Act
        var result = await _sut.ExecuteAsync(signal, CancellationToken.None);

        // Assert — should not be rejected by window check
        await _exchangeService.Received(1).ExecuteOrderAsync(signal, Arg.Any<CancellationToken>());
    }

    // ───────────────────────────────────────────────
    // 6. Cancellation token propagation
    // ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ShouldPropagateCancellationToken()
    {
        // Arrange
        var signal = CreateCryptoSignal();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        var expectedTrade = CreateExpectedTrade(signal);
        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>(), Arg.Any<CancellationToken>())
            .Returns(expectedTrade);

        // Act
        await _sut.ExecuteAsync(signal, token);

        // Assert: the exact token was forwarded to the exchange service
        await _exchangeService.Received(1).ExecuteOrderAsync(signal, token);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldThrow_WhenCancellationRequested()
    {
        // Arrange
        var signal = CreateCryptoSignal();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>(), Arg.Any<CancellationToken>())
            .Returns<Trade>(callInfo =>
            {
                callInfo.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return CreateExpectedTrade(signal);
            });

        // Act & Assert
        var act = () => _sut.ExecuteAsync(signal, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ───────────────────────────────────────────────
    // 7. Latency and slippage measurement on success
    // ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ShouldMeasureLatencyAndSlippage_OnSuccess()
    {
        // Arrange
        var signal = CreateCryptoSignal();
        signal.Price = 65_000m;

        var expectedTrade = CreateExpectedTrade(signal, fillPrice: 65_065m); // 0.1% slippage
        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>(), Arg.Any<CancellationToken>())
            .Returns(expectedTrade);

        // Act
        var result = await _sut.ExecuteAsync(signal, CancellationToken.None);

        // Assert: latency should be measured (non-negative)
        result.ExecutionLatencyMs.Should().BeGreaterThanOrEqualTo(0);

        // Assert: slippage = |fillPrice - signalPrice| / signalPrice * 100
        // = |65065 - 65000| / 65000 * 100 = 0.1%
        result.EntrySlippagePercent.Should().Be(0.1m);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSetSlippageToZero_WhenSignalPriceIsZero()
    {
        // Arrange
        var signal = CreateCryptoSignal();
        signal.Price = 0m;

        var expectedTrade = CreateExpectedTrade(signal, fillPrice: 100m);
        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>(), Arg.Any<CancellationToken>())
            .Returns(expectedTrade);

        // Act
        var result = await _sut.ExecuteAsync(signal, CancellationToken.None);

        // Assert
        result.EntrySlippagePercent.Should().Be(0m);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSetExecutionModeAndPromotionStage()
    {
        // Arrange
        var signal = CreateCryptoSignal();
        signal.Metadata["PromotionStage"] = "ShadowLive";

        var expectedTrade = CreateExpectedTrade(signal);
        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>(), Arg.Any<CancellationToken>())
            .Returns(expectedTrade);

        // Act
        var result = await _sut.ExecuteAsync(signal, CancellationToken.None);

        // Assert
        result.ExecutionMode.Should().Be("REAL");
        result.PromotionStage.Should().Be("ShadowLive");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDefaultPromotionStageToLiveProbe_WhenNotInMetadata()
    {
        // Arrange
        var signal = CreateCryptoSignal();
        // No PromotionStage in metadata

        var expectedTrade = CreateExpectedTrade(signal);
        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>(), Arg.Any<CancellationToken>())
            .Returns(expectedTrade);

        // Act
        var result = await _sut.ExecuteAsync(signal, CancellationToken.None);

        // Assert
        result.PromotionStage.Should().Be("LiveProbe");
    }

    // ───────────────────────────────────────────────
    // Edge cases: volume = 0 skips liquidity check
    // ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ShouldSkipLiquidityCheck_WhenVolume24hIsZero()
    {
        // Arrange: Volume24h = 0 means unknown — the guard `Volume24h > 0 && ...` is false, so no reject.
        // However, Volume24h=0 causes volumeRatio=1 in latency estimation, adding a liquidity penalty.
        // Use a generous latency profile so the penalty doesn't trip the latency guard.
        var generousProfile = new MarketExecutionProfile(
            Market: "Crypto",
            Venue: "Binance",
            SlippagePercent: 0.05m,
            SpreadPercent: 0.02m,
            CommissionPercentPerSide: 0.04m,
            MinHftVolume24h: 500_000m,
            PreferredMaxLatencyMs: 200);

        _profileService
            .ResolveForSymbol(Arg.Any<string>(), Arg.Any<IDictionary<string, string>?>())
            .Returns(generousProfile);

        var signal = CreateCryptoSignal(volume24h: 0m);

        var expectedTrade = CreateExpectedTrade(signal);
        _exchangeService.ExecuteOrderAsync(Arg.Any<Signal>(), Arg.Any<CancellationToken>())
            .Returns(expectedTrade);

        // Act
        var result = await _sut.ExecuteAsync(signal, CancellationToken.None);

        // Assert — liquidity check should be skipped, and latency/slippage guards pass with generous profile
        result.Status.Should().Be(TradeStatus.Executed);
        await _exchangeService.Received(1).ExecuteOrderAsync(signal, Arg.Any<CancellationToken>());
    }
}
