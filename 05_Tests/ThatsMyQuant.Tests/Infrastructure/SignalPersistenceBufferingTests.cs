using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Persistence;
using ThatsMyQuant.Infrastructure.Repositories;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Infrastructure;

public sealed class SignalPersistenceBufferingTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), "tmq-signal-buffer-tests", $"{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly SignalWriterChannel _channel;
    private readonly QuantPersistenceWorker _worker;

    public SignalPersistenceBufferingTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddDbContextPool<QuantDbContext>(options =>
            options.UseSqlite($"Data Source={_databasePath};Cache=Shared"), poolSize: 8);
        serviceCollection.AddSingleton<SignalWriterChannel>();

        _services = serviceCollection.BuildServiceProvider();
        _channel = _services.GetRequiredService<SignalWriterChannel>();
        _worker = new QuantPersistenceWorker(_channel, _services, NullLogger<QuantPersistenceWorker>.Instance);
    }

    [Fact]
    public async Task BufferedWrites_ShouldPersistLatestSignalSnapshot()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
        var repository = new SignalRepository(db, _channel);

        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTCUSDT",
            Source = "buffer-test",
            Side = TradeSide.Buy,
            Price = 102_500m,
            Status = TradeStatus.Pending,
            StrategyTag = "TrendConfluence",
            RegimeTag = "Trending",
            Metadata = new Dictionary<string, string>
            {
                ["stage"] = "initial"
            }
        };

        await repository.AddAsync(signal);

        signal.Status = TradeStatus.Queued;
        signal.Reasoning = "Buffered final state";
        signal.Metadata["stage"] = "queued";
        await repository.UpdateAsync(signal);

        var persisted = await WaitForSignalAsync(signal.Id, TimeSpan.FromSeconds(3));
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(TradeStatus.Queued);
        persisted.Reasoning.Should().Be("Buffered final state");
        persisted.Metadata["stage"].Should().Be("queued");
    }

    public async Task InitializeAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        await _worker.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _worker.StopAsync(CancellationToken.None);
        await _services.DisposeAsync();
    }

    private async Task<Signal?> WaitForSignalAsync(Guid signalId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
            var signal = await db.Signals.AsNoTracking().SingleOrDefaultAsync(s => s.Id == signalId);
            if (signal != null)
            {
                return signal;
            }

            await Task.Delay(25);
        }

        return null;
    }
}
