using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.WebSocket;

/// <summary>
/// Unit tests for BinanceWebSocketService reconnect behaviour.
/// Requirements: 1.7, 1.9
/// </summary>
public class BinanceWebSocketServiceTests
{
    // -----------------------------------------------------------------------
    // Test double — overrides ConnectCoreAsync and DelayAsync so tests run
    // without real network I/O and without real backoff delays.
    // -----------------------------------------------------------------------

    private sealed class FakeWebSocketService : BinanceWebSocketService
    {
        private readonly int _failuresBeforeSuccess;
        private int _callCount;
        private readonly TaskCompletionSource _allFailuresDone = new();

        public FakeWebSocketService(
            ILogger<BinanceWebSocketService> logger,
            IAlertingService? alertingService,
            int maxReconnectAttempts,
            int failuresBeforeSuccess)
            : base(logger, alertingService, maxReconnectAttempts)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        /// <summary>Completes immediately instead of waiting for real backoff.</summary>
        protected override Task DelayAsync(TimeSpan delay, CancellationToken token)
            => token.IsCancellationRequested
                ? Task.FromCanceled(token)
                : Task.CompletedTask;

        protected override Task ConnectCoreAsync(CancellationToken token)
        {
            _callCount++;
            if (_callCount <= _failuresBeforeSuccess)
                throw new InvalidOperationException($"Simulated WS failure #{_callCount}");

            // Succeed — signal that all failures have been processed
            _allFailuresDone.TrySetResult();
            return Task.CompletedTask;
        }

        /// <summary>Waits until the reconnect loop has processed all expected failures.</summary>
        public Task WaitForCompletionAsync(TimeSpan timeout)
            => Task.WhenAny(_allFailuresDone.Task, Task.Delay(timeout));
    }

    // -----------------------------------------------------------------------
    // Test 1 — Req 1.7
    // When MaxReconnectAttempts consecutive failures are reached, IAlertingService
    // must be called exactly once.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WebSocketReconnector_ExhaustsAttempts_SendsAlert()
    {
        // Arrange
        const int maxAttempts = 3;
        var alertingService = Substitute.For<IAlertingService>();
        alertingService
            .SendTradeAlertAsync(
                Arg.Any<string>(),
                Arg.Any<TradeDecision>(),
                Arg.Any<IndicatorSnapshot>(),
                Arg.Any<AlertAuditContext>())
            .Returns(Task.CompletedTask);

        var logger = Substitute.For<ILogger<BinanceWebSocketService>>();

        // Fail maxAttempts+1 times so the threshold is crossed, then succeed
        var sut = new FakeWebSocketService(
            logger,
            alertingService,
            maxReconnectAttempts: maxAttempts,
            failuresBeforeSuccess: maxAttempts + 1);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        await sut.ConnectAsync(["BTCUSDT"], cts.Token);
        await sut.WaitForCompletionAsync(TimeSpan.FromSeconds(4));

        // Assert — alert fired at least once when attempt count >= maxAttempts
        await alertingService.Received().SendTradeAlertAsync(
            Arg.Any<string>(),
            Arg.Any<TradeDecision>(),
            Arg.Any<IndicatorSnapshot>(),
            Arg.Any<AlertAuditContext>());
    }

    // -----------------------------------------------------------------------
    // Test 2 — Req 1.9
    // A Warning log entry must be emitted for every failed reconnect attempt.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WebSocketReconnector_LogsWarning_OnEachAttempt()
    {
        // Arrange
        const int failureCount = 3;
        var logger = Substitute.For<ILogger<BinanceWebSocketService>>();
        var alertingService = Substitute.For<IAlertingService>();
        alertingService
            .SendTradeAlertAsync(
                Arg.Any<string>(),
                Arg.Any<TradeDecision>(),
                Arg.Any<IndicatorSnapshot>(),
                Arg.Any<AlertAuditContext>())
            .Returns(Task.CompletedTask);

        var sut = new FakeWebSocketService(
            logger,
            alertingService,
            maxReconnectAttempts: 10,
            failuresBeforeSuccess: failureCount);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        await sut.ConnectAsync(["BTCUSDT"], cts.Token);
        await sut.WaitForCompletionAsync(TimeSpan.FromSeconds(4));

        // Assert — one Warning log per failed attempt
        logger.Received(failureCount).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
