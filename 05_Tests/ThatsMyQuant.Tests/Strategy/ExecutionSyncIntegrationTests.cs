using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Helpers;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public sealed class ExecutionSyncIntegrationTests
{
    [Fact]
    public async Task ExchangeExecutionSyncHostedService_PartialThenFill_KeepsPortfolioAndTradeConsistent()
    {
        var signalId = Guid.NewGuid();
        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signalId,
            Symbol = "BTCUSDT",
            Side = TradeSide.Buy,
            EntryPrice = 100m,
            RequestedQuantity = 1.0m,
            RemainingQuantity = 1.0m,
            Status = TradeStatus.Pending,
            LifecycleStatus = TradeLifecycleStatus.Accepted
        };

        var repo = new InMemoryTradeRepository(trade);
        var sync = new TestExecutionSync();
        var portfolio = new VenuePortfolioManager();
        portfolio.Clear();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TMQ_EXCHANGE_MODE"] = "OKX"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ITradeRepository>(repo);
        services.AddSingleton<IExchangeExecutionSync>(sync);
        services.AddSingleton<ISharedPortfolioStateManager>(portfolio);
        services.AddSingleton<IPortfolioManager>(portfolio);
        services.AddScoped<TradeExecutionSyncService>();
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<TradeExecutionSyncService>>(
            NullLogger<TradeExecutionSyncService>.Instance);
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ExchangeExecutionSyncHostedService>>(
            NullLogger<ExchangeExecutionSyncHostedService>.Instance);
        services.AddSingleton<ExchangeExecutionSyncHostedService>();

        using var provider = services.BuildServiceProvider();
        var hosted = provider.GetRequiredService<ExchangeExecutionSyncHostedService>();
        await hosted.StartAsync(CancellationToken.None);

        var now = DateTime.UtcNow;
        sync.Publish(new ExecutionReport(
            "BTCUSDT",
            ExecutionOrderIdentity.CreateClientOrderId(signalId),
            "ord-1",
            0.4m,
            0.6m,
            101m,
            101m,
            0.4m,
            "PARTIALLY_FILLED",
            12,
            now));

        sync.Publish(new ExecutionReport(
            "BTCUSDT",
            ExecutionOrderIdentity.CreateClientOrderId(signalId),
            "ord-1",
            1.0m,
            0m,
            102m,
            102m,
            0.6m,
            "FILLED",
            18,
            now.AddMilliseconds(50)));

        await WaitUntilAsync(() => repo.UpdateCount >= 2, TimeSpan.FromSeconds(2));

        trade.FilledQuantity.Should().Be(1.0m);
        trade.RemainingQuantity.Should().Be(0m);
        trade.LifecycleStatus.Should().Be(TradeLifecycleStatus.Active);
        trade.Status.Should().Be(TradeStatus.Executed);
        trade.SubmittedTime.Should().NotBeNull();
        trade.LastUpdatedTime.Should().NotBeNull();
        trade.ExecutionLatencyMs.Should().Be(18);
        trade.RealisedPnL.Should().Be(0m);

        portfolio.GetPosition(VenueKind.Okx, "BTCUSDT").Should().Be(1.0m);

        await hosted.StopAsync(CancellationToken.None);
        portfolio.Clear();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }

        predicate().Should().BeTrue("condition should eventually be met before timeout");
    }

    private sealed class InMemoryTradeRepository : ITradeRepository
    {
        private readonly Trade _trade;
        public int UpdateCount { get; private set; }

        public InMemoryTradeRepository(Trade trade)
        {
            _trade = trade;
        }

        public Task<Trade?> GetByIdAsync(Guid id) => Task.FromResult(id == _trade.Id ? _trade : null);

        public Task<Trade?> GetBySignalIdAsync(Guid signalId) => Task.FromResult(signalId == _trade.SignalId ? _trade : null);

        public Task<IReadOnlyList<Trade>> GetAllAsync(int count = 100) => Task.FromResult((IReadOnlyList<Trade>)new[] { _trade });

        public Task<IReadOnlyList<Trade>> GetOpenAsync(int count = 100) => Task.FromResult((IReadOnlyList<Trade>)new[] { _trade });

        public Task AddAsync(Trade trade) => Task.CompletedTask;

        public Task UpdateAsync(Trade trade)
        {
            UpdateCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TestExecutionSync : IExchangeExecutionSync
    {
        private readonly List<IObserver<ExecutionReport>> _observers = [];

        public IObservable<ExecutionReport> ExecutionReports => new ReportObservable(_observers);

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _observers.Clear();
            return ValueTask.CompletedTask;
        }

        public void Publish(ExecutionReport report)
        {
            foreach (var observer in _observers.ToArray())
            {
                observer.OnNext(report);
            }
        }

        private sealed class ReportObservable(List<IObserver<ExecutionReport>> observers) : IObservable<ExecutionReport>
        {
            public IDisposable Subscribe(IObserver<ExecutionReport> observer)
            {
                observers.Add(observer);
                return new Subscription(observers, observer);
            }
        }

        private sealed class Subscription(List<IObserver<ExecutionReport>> observers, IObserver<ExecutionReport> observer) : IDisposable
        {
            public void Dispose() => observers.Remove(observer);
        }
    }
}
