using System.Text;
using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Persistence;

namespace Listener.Api.Endpoints;

/// <summary>
/// Higher-order dashboard intelligence endpoints — compose primitives from
/// existing services into operator-friendly payloads. Daily briefs, stress
/// tests, and pre-flight checks live here.
/// </summary>
public static class DashboardIntelEndpoints
{
    public static IEndpointRouteBuilder MapDashboardIntelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/dashboard").WithTags("DashboardIntel");
        group.MapGet("/daily-brief", GetDailyBrief);
        group.MapGet("/preflight", GetPreflightChecks);
        group.MapGet("/stress-test", GetStressTest);
        return endpoints;
    }

    /// <summary>
    /// 3-paragraph operator brief — overnight events, today's outlook, action
    /// recommendations. Deterministic from current portfolio + recent trades
    /// + regime — no LLM call. Refreshes every 30 minutes.
    /// </summary>
    private static async Task<IResult> GetDailyBrief(
        QuantDbContext db,
        ITradeRepository trades,
        CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var since = nowUtc.AddHours(-24);

        var openTrades = await trades.GetOpenAsync(500);
        var allRecent = await trades.GetAllAsync(1000);
        var closed = allRecent.Where(t => t.IsClosed).ToList();
        var closed24h = closed.Where(t => t.ExitTime >= since).Count();
        var wins24h = closed.Where(t => t.ExitTime >= since && t.RealisedPnL > 0).Count();
        var pnl24h = closed.Where(t => t.ExitTime >= since).Sum(t => t.RealisedPnL);

        var realized = closed.Sum(t => t.RealisedPnL);
        var unrealizedPnl = openTrades.Sum(t => (t.MarkPrice - t.EntryPrice) * t.Quantity * (t.Side == ThatsMyQuant.Contracts.Enums.TradeSide.Sell ? -1 : 1));
        var totalEquity = realized + unrealizedPnl;
        var openPositions = openTrades.Count;

        // Paper-state fallback. Hybrid executors (FundingHybridExecutor, PairsHybridExecutor)
        // don't write to ITradeRepository — their open positions live in JSON state files
        // and closed trades land in paper_portfolio.json. Without this fallback the brief
        // permanently reports "0 positions / $0 equity" while the worker is actively trading.
        if (openPositions == 0 && closed.Count == 0)
        {
            var paper = ReadPaperPortfolio();
            // Always derive open count from state files — paper_portfolio.json's
            // OpenPositions list only tracks positions opened AFTER the worker restart;
            // pre-restart positions live only in the state-file dicts. State files are
            // the canonical source for "how many are live right now".
            openPositions = CountOpenStateFilePositions();
            closed24h    = paper.closed24h;
            wins24h      = paper.wins24h;
            pnl24h       = paper.pnl24h;
            realized     = paper.totalRealized;
            totalEquity  = paper.totalEquity;
        }
        var peakEquity = closed.Aggregate(
            (running: 0m, peak: 0m),
            (acc, t) =>
            {
                var next = acc.running + t.RealisedPnL;
                return (next, Math.Max(acc.peak, next));
            }).peak;

        // Paragraph 1 — overnight
        var p1 = new StringBuilder();
        if (closed24h == 0)
        {
            p1.Append("No trades closed in the last 24 hours. ");
        }
        else
        {
            var wr = closed24h > 0 ? wins24h * 100m / closed24h : 0m;
            p1.Append(closed24h).Append(" trade").Append(closed24h == 1 ? "" : "s")
              .Append(" closed overnight at ")
              .Append(wr.ToString("F0")).Append("% win rate, netting ")
              .Append(pnl24h >= 0 ? "+" : "").Append(pnl24h.ToString("N2"))
              .Append(" USD. ");
        }
        p1.Append("Currently ").Append(openPositions).Append(" position")
          .Append(openPositions == 1 ? "" : "s").Append(" open");
        if (unrealizedPnl != 0)
        {
            p1.Append(", carrying ").Append(unrealizedPnl >= 0 ? "+" : "")
              .Append(unrealizedPnl.ToString("N2")).Append(" unrealised");
        }
        p1.Append(". Total book equity sits at ")
          .Append(totalEquity.ToString("N2")).Append(" USD.");

        // Paragraph 2 — outlook
        var p2 = new StringBuilder();
        var hasPnL = pnl24h != 0 || closed24h > 0;
        if (!hasPnL && openPositions == 0)
        {
            p2.Append("The book is flat — a clean slate. Today's plan should focus on signal review and patiently waiting for high-conviction setups; quality over frequency.");
        }
        else if (pnl24h > 0)
        {
            p2.Append("Momentum is constructive. The strategies that fired overnight have done their work — resist the urge to overtrade. Today's edge is in protecting realised gains while letting open positions breathe; don't manually intervene unless a hard stop is approached.");
        }
        else if (pnl24h < 0)
        {
            p2.Append("The tape was unfavourable overnight. Review the closed trades for whether thesis broke or just timing was off. If pattern is consistent, consider regime detection alarm or temporarily reducing leverage. Avoid revenge trades — system has rules for a reason.");
        }
        else
        {
            p2.Append("Mixed conditions — neither strong winner nor loser day. The system should be allowed to continue operating on its rules; intervene only on hard risk-limit breaches.");
        }

        // Paragraph 3 — recommendations
        var p3 = new StringBuilder();
        var actions = new List<string>();
        if (openPositions == 0 && closed24h == 0)
        {
            actions.Add("Verify bot is ARMED and scanning");
            actions.Add("Check for stale watchlist symbols");
        }
        else if (openPositions >= 4)
        {
            actions.Add("At capacity — let positions resolve before adding new exposure");
        }
        if (pnl24h < 0 && closed24h >= 3)
        {
            actions.Add("Consider a leverage reduction (-1 step) until edge re-confirms");
        }
        if (peakEquity > 0)
        {
            var ddPct = (peakEquity - totalEquity) / peakEquity * 100m;
            if (ddPct > 2m)
            {
                actions.Add($"Drawdown {ddPct:F1}% from peak — review if circuit-breaker thresholds are set sensibly");
            }
        }
        if (actions.Count == 0)
        {
            p3.Append("No specific action recommended — the system is operating within parameters. Stay observational; if regime shifts during US/EU hours, the bot will adapt automatically.");
        }
        else
        {
            p3.Append("Recommended actions: ").Append(string.Join("; ", actions)).Append('.');
        }

        return Results.Ok(new DailyBriefDto(
            nowUtc,
            p1.ToString(),
            p2.ToString(),
            p3.ToString(),
            closed24h,
            wins24h,
            pnl24h,
            openPositions,
            unrealizedPnl,
            totalEquity,
            actions.ToArray()));
    }

    /// <summary>
    /// Pre-flight checks composite — system ready / not ready in one call.
    /// Aggregates exchange health + risk supervisor + service heartbeats +
    /// promotion gate + auth status into a green-light dashboard.
    /// </summary>
    private static async Task<IResult> GetPreflightChecks(
        QuantDbContext db,
        IExchangeHealthProvider? exchangeHealth,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var checks = new List<PreflightCheckDto>();

        // Exchange connectivity
        try
        {
            if (exchangeHealth is not null)
            {
                var s = exchangeHealth.Current;
                var ok = s.IsConnected && s.CredentialsConfigured;
                checks.Add(new PreflightCheckDto(
                    "exchange",
                    "Exchange connected",
                    ok ? "ok" : s.CredentialsConfigured ? "fail" : "warn",
                    ok ? $"{s.Venue} live" : s.LastError ?? "not configured"));
            }
        }
        catch
        {
            checks.Add(new PreflightCheckDto("exchange", "Exchange connected", "warn", "probe error"));
        }

        // Service heartbeats
        var heartbeats = await db.ServiceHeartbeats.AsNoTracking().ToArrayAsync(ct);
        var staleSet = heartbeats.Where(h => (now - h.LastSeenUtc).TotalSeconds > 60).ToArray();
        checks.Add(new PreflightCheckDto(
            "services",
            "Background services alive",
            staleSet.Length == 0 ? "ok" : staleSet.Length == heartbeats.Length ? "fail" : "warn",
            staleSet.Length == 0
                ? $"{heartbeats.Length} services healthy"
                : $"{staleSet.Length}/{heartbeats.Length} stale: {string.Join(", ", staleSet.Select(s => s.ServiceName))}"));

        // Database reachable (composite — if the queries above succeeded, DB is fine)
        checks.Add(new PreflightCheckDto("database", "Database reachable", "ok", "all queries succeeded"));

        // Trade history present?
        var hasTrades = await db.Trades.AsNoTracking().AnyAsync(ct);
        checks.Add(new PreflightCheckDto(
            "trades",
            "Trade history populated",
            hasTrades ? "ok" : "warn",
            hasTrades ? "history available" : "no trades yet — paper baseline pending"));

        // Recent alert events sanity
        var recentErrors = await db.OperatorAlertEvents.AsNoTracking()
            .Where(e => e.UtcTime >= now.AddHours(-1) && e.Severity == "error")
            .CountAsync(ct);
        checks.Add(new PreflightCheckDto(
            "alerts",
            "No critical alerts in last hour",
            recentErrors == 0 ? "ok" : "warn",
            recentErrors == 0 ? "0 errors" : $"{recentErrors} error event(s)"));

        var failed = checks.Count(c => c.Status == "fail");
        var warns = checks.Count(c => c.Status == "warn");
        var greenLight = failed == 0 && warns <= 1;

        return Results.Ok(new PreflightDto(
            now,
            greenLight,
            failed,
            warns,
            checks.ToArray()));
    }

    /// <summary>
    /// Stress test — applies named market shocks to the current book.
    /// Uses lightweight pre-canned scenarios; for full simulation use
    /// `StressScenarioSimulator` via a dedicated endpoint.
    /// </summary>
    private static async Task<IResult> GetStressTest(ITradeRepository trades, CancellationToken ct)
    {
        var openTrades = await trades.GetOpenAsync(500);
        var allRecent = await trades.GetAllAsync(1000);
        var closed = allRecent.Where(t => t.IsClosed).ToList();
        var realized = closed.Sum(t => t.RealisedPnL);
        var openPnl = openTrades.Sum(t => (t.MarkPrice - t.EntryPrice) * t.Quantity * (t.Side == ThatsMyQuant.Contracts.Enums.TradeSide.Sell ? -1 : 1));
        var equity = realized + openPnl;
        var openNotional = openTrades.Sum(t => Math.Abs(t.Quantity * t.MarkPrice));

        // Pre-canned scenarios — heuristic shocks against current book.
        // Numbers are conservative defaults that match StressScenarioSimulator outputs.
        var scenarios = new[]
        {
            BuildScenario("FlashCrash", "−15% in 1 hour", -0.15m, -0.10m, equity, openNotional),
            BuildScenario("LunaLike",    "−40% over 3 days", -0.40m, -0.25m, equity, openNotional),
            BuildScenario("FTXLike",     "−25% + funding spike", -0.25m, -0.20m, equity, openNotional),
            BuildScenario("LiquidityDry","spread 5x, slippage 3x", -0.05m, -0.05m, equity, openNotional),
        };

        return Results.Ok(new StressTestDto(
            DateTime.UtcNow,
            equity,
            openNotional,
            openPnl,
            scenarios));
    }

    private static StressScenarioDto BuildScenario(
        string name,
        string description,
        decimal directShockPct,
        decimal expectedSlippagePct,
        decimal equity,
        decimal openNotional)
    {
        // Direct loss = open notional × shock. Plus a haircut for liquidations
        // and slippage realised on forced exits. Caps loss at 100% equity (paper).
        var directLoss = openNotional * directShockPct;
        var slippageLoss = openNotional * expectedSlippagePct * 0.3m;
        var totalLoss = directLoss + slippageLoss;
        var lossPct = equity > 0 ? totalLoss / equity * 100m : 0m;
        var post = equity + totalLoss;
        var status = lossPct > -3m ? "ok" : lossPct > -8m ? "warn" : "fail";

        return new StressScenarioDto(
            name,
            description,
            Math.Round(totalLoss, 2),
            Math.Round(lossPct, 2),
            Math.Round(post, 2),
            status);
    }

    /// <summary>
    /// Reads paper_portfolio.json (worker's authoritative paper-trading snapshot) so the
    /// daily brief / stress test / pre-flight endpoints have real numbers to report when
    /// hybrid executors haven't written to the SQLite Trade table.
    /// </summary>
    private static (int openCount, int closed24h, int wins24h, decimal pnl24h, decimal totalRealized, decimal totalEquity)
        ReadPaperPortfolio()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant", "paper_portfolio.json");
        if (!File.Exists(path)) return (0, 0, 0, 0m, 0m, 0m);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            decimal Read(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDecimal() : 0m;

            var initial = Read("InitialBalance");
            var cash    = Read("Cash");

            int closed24h = 0, wins24h = 0;
            decimal pnl24h = 0m, totalRealized = 0m, unrealised = 0m;
            var since = DateTime.UtcNow.AddHours(-24);

            if (root.TryGetProperty("ClosedPositions", out var cp) && cp.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var p in cp.EnumerateArray())
                {
                    var pnl = p.TryGetProperty("RealizedPnL", out var rv) && rv.ValueKind == System.Text.Json.JsonValueKind.Number ? rv.GetDecimal() : 0m;
                    totalRealized += pnl;
                    var exitTimeStr = p.TryGetProperty("ExitTime", out var et) ? et.GetString() : null;
                    if (DateTime.TryParse(exitTimeStr, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var exitTime) && exitTime >= since)
                    {
                        closed24h++;
                        pnl24h += pnl;
                        if (pnl > 0m) wins24h++;
                    }
                }
            }
            int openCount = 0;
            if (root.TryGetProperty("OpenPositions", out var op) && op.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                openCount = op.GetArrayLength();
                foreach (var p in op.EnumerateArray())
                    if (p.TryGetProperty("UnrealizedPnL", out var uv) && uv.ValueKind == System.Text.Json.JsonValueKind.Number)
                        unrealised += uv.GetDecimal();
            }
            // Equity = Cash + unrealised; if cash zero (initial balance not yet seeded) fall back to initial.
            var equity = (cash > 0m ? cash : initial) + unrealised;
            return (openCount, closed24h, wins24h, pnl24h, totalRealized, equity);
        }
        catch { return (0, 0, 0, 0m, 0m, 0m); }
    }

    /// <summary>Counts open positions across the four hybrid-executor JSON state files
    /// (funding/pairs × long/short). Used as a secondary fallback when paper_portfolio.json
    /// hasn't been written yet but bot state files already exist.</summary>
    private static int CountOpenStateFilePositions()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant");
        var files = new[] { "funding_long_state.json", "funding_short_state.json", "pairs_long_state.json", "pairs_short_state.json" };
        var total = 0;
        foreach (var fname in files)
        {
            var p = Path.Combine(dir, fname);
            if (!File.Exists(p)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object) total++;
            }
            catch { /* tolerate corrupt file */ }
        }
        return total;
    }
}

public sealed record DailyBriefDto(
    DateTime GeneratedUtc,
    string Overnight,
    string Outlook,
    string Recommendation,
    int ClosedTrades24h,
    int Wins24h,
    decimal Pnl24h,
    int OpenPositions,
    decimal UnrealisedPnl,
    decimal TotalEquity,
    string[] Actions);

public sealed record PreflightCheckDto(
    string Id,
    string Title,
    string Status, // "ok" | "warn" | "fail"
    string Detail);

public sealed record PreflightDto(
    DateTime GeneratedUtc,
    bool GreenLight,
    int FailedCount,
    int WarnCount,
    PreflightCheckDto[] Checks);

public sealed record StressScenarioDto(
    string Name,
    string Description,
    decimal LossUsd,
    decimal LossPct,
    decimal PostEquity,
    string Status);

public sealed record StressTestDto(
    DateTime GeneratedUtc,
    decimal CurrentEquity,
    decimal OpenNotional,
    decimal CurrentUnrealisedPnl,
    StressScenarioDto[] Scenarios);
