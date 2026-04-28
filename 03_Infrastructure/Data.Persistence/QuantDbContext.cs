using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Infrastructure.Persistence;

public class QuantDbContext : DbContext
{
    private static readonly JsonSerializerOptions s_jsonOpts = new();

    public DbSet<Signal> Signals => Set<Signal>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<WebhookRequestLog> WebhookRequestLogs => Set<WebhookRequestLog>();
    public DbSet<OperatorUser> OperatorUsers => Set<OperatorUser>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<AlertHistoryEntry> AlertHistory => Set<AlertHistoryEntry>();
    public DbSet<OperatorAlertEvent> OperatorAlertEvents => Set<OperatorAlertEvent>();
    public DbSet<ServiceHeartbeat> ServiceHeartbeats => Set<ServiceHeartbeat>();

    public QuantDbContext(DbContextOptions<QuantDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Signal Configuration
        modelBuilder.Entity<Signal>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Symbol).IsRequired().HasMaxLength(20);
            entity.Property(s => s.Source).HasMaxLength(50);
            entity.Property(s => s.Price).HasPrecision(18, 8);
            entity.Property(s => s.Side).HasConversion<string>().HasMaxLength(10);
            entity.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(s => s.StrategyTag).HasMaxLength(50);
            entity.Property(s => s.RegimeTag).HasMaxLength(30);
            entity.Property(s => s.ClaimedBy).HasMaxLength(128);

            // Store Dictionary<string,string> as JSON column with a comparer
            entity.Property(s => s.Metadata)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, s_jsonOpts),
                    v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, s_jsonOpts) ?? new())
                .Metadata.SetValueComparer(new ValueComparer<Dictionary<string, string>>(
                    (c1, c2) => c1 != null && c2 != null && c1.SequenceEqual(c2),
                    c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                    c => c.ToDictionary(entry => entry.Key, entry => entry.Value)));
            
            entity.Property(s => s.Metadata).HasColumnType("TEXT");
            
            // microstructure microstructure microstructure 
            entity.Property(s => s.OrderFlowImbalance);
            entity.Property(s => s.BestBid);
            entity.Property(s => s.BestAsk);
            entity.Property(s => s.WallAgeMs);
            entity.Property(s => s.Quantity).HasPrecision(18, 8);
            entity.Property(s => s.Confidence);
            entity.Property(s => s.Reasoning).HasMaxLength(1000);
            entity.Property(s => s.Volume24h).HasPrecision(18, 2);
            entity.Property(s => s.ExecutionAction).HasConversion<string>().HasMaxLength(20);
            entity.Property(s => s.PriceActionSignal).HasConversion<string>().HasMaxLength(20);
            // New OrderType column (added 2026-04-24 for Market vs Limit routing).
            // Stored as string (enum name) with a default so existing rows on older
            // DB files continue to function — default matches legacy always-limit behaviour.
            entity.Property(s => s.OrderType)
                .HasConversion<string>()
                .HasMaxLength(16)
                .HasDefaultValue(ThatsMyQuant.Contracts.Enums.OrderType.Limit);

            entity.HasIndex(s => s.Symbol);
            entity.HasIndex(s => s.Timestamp);
            entity.HasIndex(s => new { s.StrategyTag, s.RegimeTag });
            entity.HasIndex(s => new { s.Status, s.ClaimedAtUtc });
        });

        // Trade Configuration
        modelBuilder.Entity<Trade>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Symbol).IsRequired().HasMaxLength(20);
            entity.Property(t => t.EntryPrice).HasPrecision(18, 8);
            entity.Property(t => t.ExitPrice).HasPrecision(18, 8);
            entity.Property(t => t.Quantity).HasPrecision(18, 8);
            entity.Property(t => t.Side).HasConversion<string>().HasMaxLength(10);
            entity.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(t => t.LifecycleStatus).HasConversion<string>().HasMaxLength(24);
            entity.Property(t => t.StrategyTag).HasMaxLength(50);
            entity.Property(t => t.RegimeTag).HasMaxLength(30);
            entity.Property(t => t.FeatureDriftImpactLabel).HasMaxLength(120);
            entity.Property(t => t.FeatureDriftSummary).HasMaxLength(500);
            entity.Property(t => t.MlModelVersion).HasMaxLength(120);
            entity.Property(t => t.ProfitLeakAuditSummary).HasMaxLength(500);
            entity.Property(t => t.ProfitLeakWorstBucketLabel).HasMaxLength(40);
            entity.Property(t => t.ProfitLeakWorstBucketBeforeLiveChange).HasMaxLength(160);
            entity.Property(t => t.ProfitLeakWorstBucketAfterLiveChange).HasMaxLength(200);
            entity.Property(t => t.RequestedQuantity).HasPrecision(18, 8);
            entity.Property(t => t.FilledQuantity).HasPrecision(18, 8);
            entity.Property(t => t.RemainingQuantity).HasPrecision(18, 8);
            entity.Property(t => t.ClosedQuantity).HasPrecision(18, 8);
            entity.Property(t => t.AverageExitPrice).HasPrecision(18, 8);
            entity.Property(t => t.StopLoss).HasPrecision(18, 8);
            entity.Property(t => t.TakeProfit).HasPrecision(18, 8);
            entity.Property(t => t.PartialTakeProfit).HasPrecision(18, 8);
            entity.Property(t => t.PartialExitFraction).HasPrecision(18, 8);
            entity.Property(t => t.BreakevenTriggerFraction).HasPrecision(18, 8);
            entity.Property(t => t.TrailingStopPrice).HasPrecision(18, 8);
            entity.Property(t => t.OptimalPriceSinceEntry).HasPrecision(18, 8);
            entity.Property(t => t.MarkPrice).HasPrecision(18, 8);
            entity.Property(t => t.ProfitLeakAuditSizeMultiplier).HasPrecision(18, 8);
            entity.Property(t => t.ProfitLeakExactNetExpectancy).HasPrecision(18, 8);
            entity.Property(t => t.ProfitLeakExitPolicyNetExpectancy).HasPrecision(18, 8);
            entity.Property(t => t.ProfitLeakRegimeNetExpectancy).HasPrecision(18, 8);
            entity.Property(t => t.ProfitLeakVenueNetExpectancy).HasPrecision(18, 8);
            entity.Property(t => t.ProfitLeakReducedSizeNetExpectancy).HasPrecision(18, 8);
            entity.Property(t => t.ProfitLeakLoserClusterRate).HasPrecision(18, 8);
            entity.Property(t => t.TotalFeesPaid).HasPrecision(18, 8);
            entity.Property(t => t.NetFunding).HasPrecision(18, 8);
            entity.Property(t => t.ExitReason).HasMaxLength(80);
            entity.Ignore(t => t.IsClosed); // Computed property, not stored
            entity.Ignore(t => t.IsPositionOpen);

            entity.HasIndex(t => t.SignalId).IsUnique();
            entity.HasIndex(t => t.Symbol);
            entity.HasIndex(t => new { t.StrategyTag, t.RegimeTag });
            // Attribution / analytics-path composite indexes (see memory: P&L tile, Quant Lab folds).
            entity.HasIndex(t => new { t.StrategyTag, t.ExitTime });
            entity.HasIndex(t => new { t.Status, t.ExitTime });
        });

        // Account Configuration
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Provider).HasMaxLength(50);
            entity.Property(a => a.Balance).HasPrecision(18, 2);
            entity.Property(a => a.Equity).HasPrecision(18, 2);

            // Positions stored as owned entities
            entity.OwnsMany(a => a.Positions, pos =>
            {
                pos.Property(p => p.Symbol).HasMaxLength(20);
                pos.Property(p => p.Quantity).HasPrecision(18, 8);
                pos.Property(p => p.AverageEntryPrice).HasPrecision(18, 8);
                pos.Property(p => p.UnrealizedPnl).HasPrecision(18, 8);
            });
        });

        modelBuilder.Entity<WebhookRequestLog>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdempotencyKey).IsRequired().HasMaxLength(120);
            entity.Property(x => x.Nonce).IsRequired().HasMaxLength(120);
            entity.Property(x => x.Signature).IsRequired().HasMaxLength(256);
            entity.HasIndex(x => x.IdempotencyKey).IsUnique();
            entity.HasIndex(x => x.Nonce).IsUnique();
            entity.HasIndex(x => x.ExpiresAtUtc);
        });

        modelBuilder.Entity<OperatorUser>(entity =>
        {
            entity.ToTable("OperatorUsers");
            entity.HasKey(u => u.Id);

            entity.Property(u => u.Username)
                  .IsRequired()
                  .HasMaxLength(64);

            entity.Property(u => u.Email)
                  .IsRequired()
                  .HasMaxLength(256);

            entity.Property(u => u.PasswordHash)
                  .IsRequired();

            entity.Property(u => u.Role)
                  .HasConversion<string>()
                  .HasMaxLength(16);

            entity.Property(u => u.IsActive).IsRequired();
            entity.Property(u => u.CreatedAt).IsRequired();
            entity.Property(u => u.MustChangePassword).IsRequired();

            entity.HasIndex(u => u.Username).IsUnique();
            entity.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.EventType).HasMaxLength(30);
            entity.Property(a => a.CorrelationId).HasMaxLength(120);
            entity.Property(a => a.Payload).HasColumnType("TEXT");
            entity.HasIndex(a => a.CorrelationId);
            entity.HasIndex(a => a.TimestampUtc);
        });

        modelBuilder.Entity<OperatorAlertEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Severity).HasMaxLength(16);
            entity.Property(e => e.Source).HasMaxLength(40);
            entity.Property(e => e.Title).HasMaxLength(200);
            entity.Property(e => e.Body).HasColumnType("TEXT");
            entity.Property(e => e.ProcessName).HasMaxLength(40);
            entity.Property(e => e.CorrelationId).HasMaxLength(120);
            entity.HasIndex(e => e.Id);
            entity.HasIndex(e => e.UtcTime);
        });

        modelBuilder.Entity<ServiceHeartbeat>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.ServiceName).IsRequired().HasMaxLength(64);
            entity.Property(s => s.Version).HasMaxLength(40);
            entity.Property(s => s.MachineName).HasMaxLength(64);
            entity.Property(s => s.Status).HasMaxLength(24);
            entity.HasIndex(s => s.ServiceName).IsUnique();
        });

        modelBuilder.Entity<AlertHistoryEntry>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Symbol).HasMaxLength(20);
            entity.Property(a => a.Title).HasMaxLength(200);
            entity.Property(a => a.Body).HasColumnType("TEXT");
            entity.Property(a => a.Metadata).HasColumnType("TEXT");
            entity.Property(a => a.FailureReason).HasMaxLength(500);
            entity.Property(a => a.CorrelationId).HasMaxLength(120);
            entity.Property(a => a.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(a => a.Channel).HasConversion<string>().HasMaxLength(20);
            entity.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(a => a.TimestampUtc);
            entity.HasIndex(a => a.Symbol);
            entity.HasIndex(a => new { a.Kind, a.Status });
        });
    }
}
