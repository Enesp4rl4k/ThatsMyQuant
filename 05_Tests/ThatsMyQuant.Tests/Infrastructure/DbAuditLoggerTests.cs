using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Text.Json;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure;
using ThatsMyQuant.Infrastructure.Persistence;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Infrastructure;

// Helper: wraps fixed DbContextOptions so tests can use a real in-memory SQLite DB
internal sealed class TestDbContextFactory : IDbContextFactory<QuantDbContext>
{
    private readonly DbContextOptions<QuantDbContext> _options;

    public TestDbContextFactory(DbContextOptions<QuantDbContext> options)
    {
        _options = options;
    }

    public QuantDbContext CreateDbContext() => new(_options);

    public Task<QuantDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}

public class DbAuditLoggerTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static (TestDbContextFactory factory, QuantDbContext seedCtx) CreateInMemoryFactory()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<QuantDbContext>()
            .UseSqlite(connection)
            .Options;

        // Keep one long-lived context open so the in-memory SQLite file stays alive
        var seedCtx = new QuantDbContext(options);
        seedCtx.Database.EnsureCreated();

        var factory = new TestDbContextFactory(options);
        return (factory, seedCtx);
    }

    private static DbAuditLogger CreateLogger(IDbContextFactory<QuantDbContext> factory)
        => new(factory, NullLogger<DbAuditLogger>.Instance);

    private static Gen<string> NonEmptyStringGen() =>
        ArbMap.Default.GeneratorFor<NonEmptyString>().Select(s => s.Get);

    // ── P3: order execution audit round-trip ─────────────────────────────────
    // Feature: execution-hardening, Property 3: order execution audit round-trip

    [Property(MaxTest = 20)]
    public Property OrderExecution_AuditRoundTrip()
    {
        var gen =
            from orderId in NonEmptyStringGen()
            from strategyId in NonEmptyStringGen()
            from justification in NonEmptyStringGen()
            select (orderId, strategyId, justification);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (orderId, strategyId, justification) = tuple;
            var (factory, seedCtx) = CreateInMemoryFactory();
            using (seedCtx)
            {
                var logger = CreateLogger(factory);
                logger.LogOrderExecutionAsync(orderId, strategyId, justification).GetAwaiter().GetResult();

                var rows = seedCtx.AuditLog.ToList();
                var row = rows.SingleOrDefault(r => r.CorrelationId == orderId);
                using var payload = row == null ? null : JsonDocument.Parse(row.Payload);

                return row != null
                    && row.EventType == "OrderExecution"
                    && row.CorrelationId == orderId
                    && payload != null
                    && payload.RootElement.GetProperty("orderId").GetString() == orderId
                    && payload.RootElement.GetProperty("strategyId").GetString() == strategyId
                    && payload.RootElement.GetProperty("justification").GetString() == justification;
            }
        });
    }

    // ── P4: security event audit round-trip ──────────────────────────────────
    // Feature: execution-hardening, Property 4: security event audit round-trip

    [Property(MaxTest = 20)]
    public Property SecurityEvent_AuditRoundTrip()
    {
        var gen =
            from principalId in NonEmptyStringGen()
            from role in NonEmptyStringGen()
            from actionType in NonEmptyStringGen()
            from details in NonEmptyStringGen()
            select (principalId, role, actionType, details);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (principalId, role, actionType, details) = tuple;
            var (factory, seedCtx) = CreateInMemoryFactory();
            using (seedCtx)
            {
                var logger = CreateLogger(factory);
                logger.LogSecurityEventAsync(principalId, role, actionType, details).GetAwaiter().GetResult();

                var rows = seedCtx.AuditLog.ToList();
                var row = rows.SingleOrDefault(r => r.CorrelationId == principalId);
                using var payload = row == null ? null : JsonDocument.Parse(row.Payload);

                return row != null
                    && row.EventType == "SecurityEvent"
                    && row.CorrelationId == principalId
                    && payload != null
                    && payload.RootElement.GetProperty("principalId").GetString() == principalId
                    && payload.RootElement.GetProperty("role").GetString() == role
                    && payload.RootElement.GetProperty("actionType").GetString() == actionType
                    && payload.RootElement.GetProperty("details").GetString() == details;
            }
        });
    }

    // ── P5: CorrelationId query returns only matching entries in order ─────────
    // Feature: execution-hardening, Property 5: CorrelationId query returns only matching entries in order

    [Property(MaxTest = 20)]
    public Property CorrelationIdQuery_ReturnsOnlyMatchingEntriesInOrder()
    {
        var gen =
            from count in Gen.Choose(2, 5)
            from ids in Gen.ListOf(NonEmptyStringGen(), count)
            from target in NonEmptyStringGen()
            select (ids, target);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (ids, target) = tuple;
            var (factory, seedCtx) = CreateInMemoryFactory();
            using (seedCtx)
            {
                var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

                var entries = new List<AuditEntry>();
                for (int i = 0; i < ids.Count; i++)
                {
                    entries.Add(new AuditEntry
                    {
                        Id = Guid.NewGuid(),
                        EventType = "OrderExecution",
                        CorrelationId = ids[i],
                        TimestampUtc = baseTime.AddSeconds(i * 10),
                        Payload = "{}"
                    });
                }
                // Add two entries with the target id at different times
                entries.Add(new AuditEntry
                {
                    Id = Guid.NewGuid(),
                    EventType = "OrderExecution",
                    CorrelationId = target,
                    TimestampUtc = baseTime.AddSeconds(100),
                    Payload = "{}"
                });
                entries.Add(new AuditEntry
                {
                    Id = Guid.NewGuid(),
                    EventType = "SecurityEvent",
                    CorrelationId = target,
                    TimestampUtc = baseTime.AddSeconds(50),
                    Payload = "{}"
                });

                seedCtx.AuditLog.AddRange(entries);
                seedCtx.SaveChanges();

                var logger = CreateLogger(factory);
                var results = logger.QueryByCorrelationIdAsync(target).GetAwaiter().GetResult();

                bool allMatch = results.All(r => r.CorrelationId == target);
                bool isOrdered = results.Zip(results.Skip(1))
                    .All(pair => pair.First.TimestampUtc <= pair.Second.TimestampUtc);
                bool containsExpected = results.Count >= 2;

                return allMatch && isOrdered && containsExpected;
            }
        });
    }

    // ── P6: time range query returns only in-range entries in order ───────────
    // Feature: execution-hardening, Property 6: time range query returns only in-range entries in order

    [Property(MaxTest = 20)]
    public Property TimeRangeQuery_ReturnsOnlyInRangeEntriesInOrder()
    {
        var gen =
            from count in Gen.Choose(3, 8)
            from offsets in Gen.ListOf(Gen.Choose(0, 200), count)
            from fromOffset in Gen.Choose(20, 80)
            from toOffset in Gen.Choose(100, 180)
            select (offsets, fromOffset, toOffset);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (offsets, fromOffset, toOffset) = tuple;
            var (factory, seedCtx) = CreateInMemoryFactory();
            using (seedCtx)
            {
                var baseTime = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
                var from = baseTime.AddSeconds(fromOffset);
                var to = baseTime.AddSeconds(toOffset);

                var entries = offsets.Select((offset, i) => new AuditEntry
                {
                    Id = Guid.NewGuid(),
                    EventType = "OrderExecution",
                    CorrelationId = $"corr-{i}",
                    TimestampUtc = baseTime.AddSeconds(offset),
                    Payload = "{}"
                }).ToList();

                seedCtx.AuditLog.AddRange(entries);
                seedCtx.SaveChanges();

                var logger = CreateLogger(factory);
                var results = logger.QueryByTimeRangeAsync(from, to).GetAwaiter().GetResult();

                bool allInRange = results.All(r => r.TimestampUtc >= from && r.TimestampUtc <= to);
                bool isOrdered = results.Zip(results.Skip(1))
                    .All(pair => pair.First.TimestampUtc <= pair.Second.TimestampUtc);
                int expectedCount = entries.Count(e => e.TimestampUtc >= from && e.TimestampUtc <= to);

                return allInRange && isOrdered && results.Count == expectedCount;
            }
        });
    }

    // ── Unit tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DbAuditLogger_WriteFailure_LogsErrorAndRethrows()
    {
        var factory = Substitute.For<IDbContextFactory<QuantDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>())
               .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        var auditLogger = new DbAuditLogger(factory, NullLogger<DbAuditLogger>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => auditLogger.LogOrderExecutionAsync("ord1", "strat1", "test"));
    }

    [Fact]
    public void DbAuditLogger_IsRegisteredAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThatsMyQuantInfrastructure();

        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IAuditLogger));

        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Singleton, descriptor!.Lifetime);
        Assert.Equal(typeof(DbAuditLogger), descriptor.ImplementationType);
    }
}
