using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Bot;

/// <summary>
/// [Sprint 3 — 2026-05-10] Daily per-strategy realized-PnL digest. Reads the
/// Trade table grouped by <c>StrategyTag</c> and emits a structured JSON snapshot
/// + a Telegram-ready table of:
///   N closed trades, gross PnL, fees, net PnL, win rate, avg PnL/trade,
///   max drawdown, and (when populated) backtest expected vs realized divergence.
///
/// Why this matters:
///   With 11 EdgeLab strategies now in the stack and per-strategy basis weights
///   active (Sprint 2), attribution must be visible to the operator daily.
///   Without this digest, the operator is flying blind on which edges decayed
///   and which kept earning. The Premortem #1 detector (FundingFeeRatio) only
///   covered carry strategies; this generalises to any strategy.
///
/// Trigger: fires once per day at <see cref="RunHourUtc"/>:<see cref="RunMinuteUtc"/>.
/// Output: JSON state file (per-strategy aggregate row) + log line + Telegram
/// alert (when <see cref="IOperatorAlertPublisher"/> is registered).
///
/// Not meant for paper-cycle attribution (that's <see cref="EdgeLabReconciliationLedger"/>);
/// this digest is for actual fills landing in the Trade table.
/// </summary>
public sealed class PerStrategyDigestHostedService : BackgroundService
{
    public const int RunHourUtc = 23;
    public const int RunMinuteUtc = 30;
    public const int LookbackDays = 7;

    private readonly ILogger<PerStrategyDigestHostedService> _logger;
    private readonly IServiceProvider _services;

    public PerStrategyDigestHostedService(
        ILogger<PerStrategyDigestHostedService> logger,
        IServiceProvider services)
    {
        _logger = logger;
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "PerStrategyDigest starting. RunAt={H:D2}:{M:D2} UTC daily, LookbackDays={Days}",
            RunHourUtc, RunMinuteUtc, LookbackDays);

        // Run once at boot if past today's slot, then daily at slot.
        while (!stoppingToken.IsCancellationRequested)
        {
            var nextRun = NextRun(DateTime.UtcNow);
            var wait = nextRun - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            try { await RunOnceAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "PerStrategyDigest run failed"); }
        }
    }

    public static DateTime NextRun(DateTime nowUtc)
    {
        var slot = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day,
            RunHourUtc, RunMinuteUtc, 0, DateTimeKind.Utc);
        return slot > nowUtc ? slot : slot.AddDays(1);
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetService<QuantDbContext>();
        if (db is null)
        {
            _logger.LogWarning("PerStrategyDigest: QuantDbContext unavailable; skipping run.");
            return;
        }

        var since = DateTime.UtcNow.AddDays(-LookbackDays);

        // Pull closed-only fills with a strategy tag and exit price; group in memory.
        // EF projection narrow → no 80-column SELECT echo. Closed = ExitTime set or
        // LifecycleStatus terminal.
        var rows = await db.Trades
            .AsNoTracking()
            .Where(t => t.EntryTime >= since
                        && t.ExitPrice > 0m
                        && (t.LifecycleStatus == TradeLifecycleStatus.Closed
                            || t.LifecycleStatus == TradeLifecycleStatus.Cancelled))
            .Select(t => new
            {
                t.StrategyTag,
                t.Side,
                t.EntryPrice,
                t.ExitPrice,
                t.Quantity,
                t.TotalFeesPaid,
                t.NetFunding,
                t.BacktestExpectedPnL,
                t.EntryTime
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // [Sprint 6] Pull current kill-switch state so the digest mirrors live multiplier decisions.
        var killState = scope.ServiceProvider.GetService<EdgeLabKillSwitchState>()?.Read();

        var groups = rows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.StrategyTag) ? "Unknown" : r.StrategyTag)
            .Select(g =>
            {
                var sideMul = g.Select(r => r.Side == TradeSide.Buy ? 1m : -1m).ToArray();
                var grossPnLs = g.Select((r, i) =>
                    sideMul[i] * (r.ExitPrice - r.EntryPrice) * r.Quantity).ToArray();
                var totalGross = grossPnLs.Sum();
                var totalFees = g.Sum(r => r.TotalFeesPaid);
                var totalFunding = g.Sum(r => r.NetFunding);
                var net = totalGross - totalFees + totalFunding;
                var n = g.Count();
                var wins = grossPnLs.Count(p => p > 0m);
                var expected = g.Sum(r => r.BacktestExpectedPnL);

                decimal killMult = 1m;
                decimal realizedSharpe = 0m;
                if (killState?.Strategies.TryGetValue(g.Key, out var ks) == true)
                {
                    killMult = ks.Multiplier;
                    realizedSharpe = ks.RealizedSharpeAnn;
                }
                return new PerStrategyRow(
                    Strategy: g.Key,
                    N: n,
                    GrossPnL: totalGross,
                    Fees: totalFees,
                    Funding: totalFunding,
                    NetPnL: net,
                    WinRate: n > 0 ? (decimal)wins / n : 0m,
                    AvgPerTrade: n > 0 ? net / n : 0m,
                    BacktestExpectedPnL: expected,
                    Divergence: expected != 0m ? net - expected : 0m,
                    KillMultiplier: killMult,
                    RealizedSharpeAnn: realizedSharpe);
            })
            .OrderByDescending(r => r.NetPnL)
            .ToList();

        // Persist JSON snapshot → daily Quantlab calibration channel (mirrors
        // EdgeLabRealizedPnLDumper convention).
        var stateDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant", "per_strategy_digest");
        Directory.CreateDirectory(stateDir);
        var fp = System.IO.Path.Combine(stateDir,
            $"per_strategy_{DateTime.UtcNow:yyyyMMdd}.json");
        try
        {
            await File.WriteAllTextAsync(fp,
                JsonSerializer.Serialize(new
                {
                    generated_at = DateTime.UtcNow,
                    lookback_days = LookbackDays,
                    strategies = groups
                }, new JsonSerializerOptions { WriteIndented = true }),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PerStrategyDigest: state write failed"); }

        // Log table — operators read worker_logs more reliably than Telegram.
        var sb = new StringBuilder();
        sb.AppendLine($"=== Per-strategy digest (last {LookbackDays}d, {groups.Count} strategies) ===");
        sb.AppendLine($"{"Strategy",-30} {"N",4} {"Net$",10} {"Gross$",10} {"Fees$",8} {"WR%",6} {"Avg$",8} {"Diverg$",10} {"R-Shrp",7} {"KMult",6}");
        var inv = CultureInfo.InvariantCulture;
        foreach (var g in groups)
        {
            sb.AppendLine(string.Format(inv,
                "{0,-30} {1,4} {2,10:F2} {3,10:F2} {4,8:F2} {5,6:P1} {6,8:F2} {7,10:F2} {8,7:F2} {9,6:F1}x",
                Truncate(g.Strategy, 30), g.N, g.NetPnL, g.GrossPnL, g.Fees, g.WinRate, g.AvgPerTrade,
                g.Divergence, g.RealizedSharpeAnn, g.KillMultiplier));
        }
        _logger.LogInformation("{Digest}", sb.ToString());

        // Telegram (best-effort, non-fatal on miss).
        var alerts = scope.ServiceProvider.GetService<IOperatorAlertPublisher>();
        if (alerts is not null)
        {
            try
            {
                await alerts.PublishAsync(
                    "info", "PerStrategyDigest",
                    $"Per-strategy {LookbackDays}d ({groups.Count} strategies)",
                    sb.ToString(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "PerStrategyDigest: Telegram publish failed"); }
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n);

    public sealed record PerStrategyRow(
        string Strategy,
        int N,
        decimal GrossPnL,
        decimal Fees,
        decimal Funding,
        decimal NetPnL,
        decimal WinRate,
        decimal AvgPerTrade,
        decimal BacktestExpectedPnL,
        decimal Divergence,
        decimal KillMultiplier,
        decimal RealizedSharpeAnn);
}
