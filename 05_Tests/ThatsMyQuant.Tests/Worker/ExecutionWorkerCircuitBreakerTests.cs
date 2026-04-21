using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ThatsMyQuant.Application.HftInfrastructure;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Execution.Worker;
using Xunit;

namespace ThatsMyQuant.Tests.Worker;

public class ExecutionWorkerCircuitBreakerTests
{
    private static CircuitBreakerService MakeCircuitBreaker()
    {
        var portfolio = Substitute.For<IPortfolioManager>();
        portfolio.GetDrawdownPercent().Returns(0m);
        return new CircuitBreakerService(
            NullLogger<CircuitBreakerService>.Instance,
            portfolio,
            maxDrawdownPercent: 50m,
            maxConsecutiveErrors: 5,
            maxOrdersPerSecond: 100);
    }

    private static ExecutionWorker MakeWorker()
    {
        var streamingService = Substitute.For<IStreamingMarketDataService>();
        var mediator = Substitute.For<IMediator>();
        var tickLogger = Substitute.For<ITickLogger>();
        var auditLogger = Substitute.For<IAuditLogger>();
        var heartbeatMonitor = new HeartbeatMonitor(new DeadMansSwitch(
            auditLogger,
            MakeCircuitBreaker(),
            NullLogger<DeadMansSwitch>.Instance));
        var tickPipeline = new TickPipeline(
            streamingService,
            NullLogger<TickPipeline>.Instance,
            mediator,
            new FeatureEngineeringAgent(),
            tickLogger,
            heartbeatMonitor);

        var queue = new ExecutionSignalQueue();
        var telemetry = new OperationalTelemetry("WorkerTests");
        var serviceProvider = Substitute.For<IServiceProvider>();

        return new ExecutionWorker(
            NullLogger<ExecutionWorker>.Instance,
            serviceProvider,
            tickPipeline,
            queue,
            telemetry,
            MakeCircuitBreaker());
    }

    private static Signal MakeSignal(int retryCount = 0) => new()
    {
        Id = Guid.NewGuid(),
        Symbol = "BTCUSDT",
        Side = TradeSide.Buy,
        Price = 60_000m,
        Quantity = 0.01m,
        Status = TradeStatus.Queued,
        RetryCount = retryCount,
        Reasoning = "test"
    };

    private static async Task InvokeProcessSingleSignal(
        ExecutionWorker worker,
        Signal signal,
        ISignalRepository repo,
        IMediator mediator)
    {
        var method = typeof(ExecutionWorker).GetMethod(
            "ProcessSingleSignal",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(worker, [signal, repo, mediator, CancellationToken.None])!;
    }

    [Fact]
    public async Task ProcessSingleSignal_WhenMediatorReturnsExecutedTrade_UpdatesSignalStatus()
    {
        var worker = MakeWorker();
        var signal = MakeSignal();
        var repo = Substitute.For<ISignalRepository>();
        var mediator = Substitute.For<IMediator>();

        repo.UpdateAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);
        mediator.Send(Arg.Any<ExecuteTradeCommand>(), Arg.Any<CancellationToken>())
            .Returns(new Trade
            {
                Status = TradeStatus.Executed,
                LifecycleStatus = TradeLifecycleStatus.Active
            });

        await InvokeProcessSingleSignal(worker, signal, repo, mediator);

        signal.Status.Should().Be(TradeStatus.Executed);
        signal.ClaimedAtUtc.Should().BeNull();
        signal.ClaimedBy.Should().BeNull();
        await mediator.Received(1).Send(Arg.Any<ExecuteTradeCommand>(), Arg.Any<CancellationToken>());
        await repo.Received(1).UpdateAsync(signal);
    }

    [Fact]
    public async Task ProcessSingleSignal_WhenMediatorReturnsFailedTrade_MarksSignalFailed()
    {
        var worker = MakeWorker();
        var signal = MakeSignal();
        var repo = Substitute.For<ISignalRepository>();
        var mediator = Substitute.For<IMediator>();

        repo.UpdateAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);
        mediator.Send(Arg.Any<ExecuteTradeCommand>(), Arg.Any<CancellationToken>())
            .Returns(new Trade
            {
                Status = TradeStatus.Failed,
                LifecycleStatus = TradeLifecycleStatus.Failed
            });

        await InvokeProcessSingleSignal(worker, signal, repo, mediator);

        signal.Status.Should().Be(TradeStatus.Failed);
        signal.Reasoning.Should().Contain("Execution returned non-success");
        signal.ClaimedAtUtc.Should().BeNull();
        signal.ClaimedBy.Should().BeNull();
    }

    [Fact]
    public async Task ProcessSingleSignal_WhenMediatorThrowsAtThirdRetry_MarksDeadLetter()
    {
        var worker = MakeWorker();
        var signal = MakeSignal(retryCount: 2);
        var repo = Substitute.For<ISignalRepository>();
        var mediator = Substitute.For<IMediator>();

        repo.UpdateAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);
        mediator.Send(Arg.Any<ExecuteTradeCommand>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("exchange error"));

        await InvokeProcessSingleSignal(worker, signal, repo, mediator);

        signal.Status.Should().Be(TradeStatus.DeadLetter);
        signal.Reasoning.Should().Contain("Max Retries");
        signal.ClaimedAtUtc.Should().BeNull();
        signal.ClaimedBy.Should().BeNull();
    }
}
