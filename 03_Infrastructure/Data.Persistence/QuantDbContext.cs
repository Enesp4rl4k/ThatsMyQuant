using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Infrastructure.Persistence;

public class QuantDbContext : DbContext
{
    public DbSet<Signal> Signals => Set<Signal>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<WebhookRequestLog> WebhookRequestLogs => Set<WebhookRequestLog>();

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

            // 🚀 GC DESTRUCTION: Erase dynamic Dictionary JSON runtime abstractions from target architecture
            entity.Ignore(s => s.Metadata);
            
            // Native FPU Database bounds mapping
            entity.Property(s => s.OrderFlowImbalance);
            entity.Property(s => s.BestBid).HasPrecision(18, 8);
            entity.Property(s => s.BestAsk).HasPrecision(18, 8);
            entity.Property(s => s.WallAgeMs);
            
            // Native bitwise / enumeration execution states stored explicitly over String-Concatenation models
            entity.Property(s => s.ExecutionAction).HasConversion<string>().HasMaxLength(40);
            entity.Property(s => s.PriceActionSignal).HasConversion<string>().HasMaxLength(40);
            entity.Property(s => s.ReasonFlags).HasConversion<string>().HasMaxLength(255);

            entity.HasIndex(s => s.Symbol);
            entity.HasIndex(s => s.Timestamp);
            entity.HasIndex(s => new { s.StrategyTag, s.RegimeTag });
        });

        // Trade Configuration
        modelBuilder.Entity<Trade>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Symbol).IsRequired().HasMaxLength(20);
            entity.Property(t => t.EntryPrice).HasPrecision(18, 8);
            entity.Property(t => t.ExitPrice).HasPrecision(18, 8);
            entity.Property(t => t.Quantity).HasPrecision(18, 8);
            entity.Property(t => t.EdgeScore).HasPrecision(18, 8);
            entity.Property(t => t.EdgeBand).HasMaxLength(20);
            entity.Property(t => t.ExactBucketImpactLabel).HasMaxLength(80);
            entity.Property(t => t.ExactBucketSummary).HasMaxLength(200);
            entity.Property(t => t.FinalAllocatorBias).HasPrecision(18, 8);
            entity.Property(t => t.FinalAllocatorScope).HasMaxLength(50);
            entity.Property(t => t.VenueExecutionBias).HasPrecision(18, 8);
            entity.Property(t => t.VenueExecutionScope).HasMaxLength(50);
            entity.Property(t => t.VenueImpactLabel).HasMaxLength(80);
            entity.Property(t => t.ConcordanceImpactLabel).HasMaxLength(80);
            entity.Property(t => t.ConcordanceSummary).HasMaxLength(240);
            entity.Property(t => t.ExecutionMode).HasMaxLength(20);
            entity.Property(t => t.PromotionStage).HasMaxLength(20);
            entity.Property(t => t.EntrySlippagePercent).HasPrecision(18, 8);
            entity.Property(t => t.ExecutionLatencyMs);
            entity.Property(t => t.Side).HasConversion<string>().HasMaxLength(10);
            entity.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(t => t.StrategyTag).HasMaxLength(50);
            entity.Property(t => t.RegimeTag).HasMaxLength(30);
            entity.Ignore(t => t.IsClosed); // Computed property, not stored

            entity.HasIndex(t => t.SignalId).IsUnique();
            entity.HasIndex(t => t.Symbol);
            entity.HasIndex(t => new { t.StrategyTag, t.RegimeTag });
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
    }
}
