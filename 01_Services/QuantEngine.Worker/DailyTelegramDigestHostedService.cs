using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Sends a daily digest to the configured Telegram chat at local midnight (00:00) summarising
/// paper-trade portfolio state, trades by edge, win rate per edge, total PnL and top warnings.
/// Fires once per local day; the wake-loop runs every 60 seconds and gates the send by date.
/// </summary>
public sealed class DailyTelegramDigestHostedService : BackgroundService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ILogger<DailyTelegramDigestHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PaperPortfolio _portfolio;
    private readonly string _botToken;
    private readonly string _chatId;
    private readonly TimeOnly _digestTime;
    private DateOnly? _lastDigestDate;

    public DailyTelegramDigestHostedService(
        ILogger<DailyTelegramDigestHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        PaperPortfolio portfolio)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _portfolio = portfolio;
        _botToken = configuration["Telegram:BotToken"] ?? string.Empty;
        _chatId = configuration["Telegram:ChatId"] ?? string.Empty;
        // Default 00:00 local. Override via Telegram:DailyDigestTime ("HH:mm").
        var raw = configuration["Telegram:DailyDigestTime"];
        _digestTime = TimeOnly.TryParse(raw, out var t) ? t : new TimeOnly(0, 0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(_chatId))
        {
            _logger.LogWarning("DailyTelegramDigestHostedService disabled — Telegram BotToken or ChatId missing.");
            return;
        }

        _logger.LogInformation("DailyTelegramDigestHostedService starting. DigestTime={Time} (local).", _digestTime);

        // Send a startup ping so the user knows the digest service is wired.
        await SendMessageAsync(BuildStartupPing(), stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            do
            {
                try
                {
                    var nowLocal = DateTime.Now;
                    var todayLocal = DateOnly.FromDateTime(nowLocal);
                    var nowTime = TimeOnly.FromDateTime(nowLocal);

                    // Fire once between digestTime and digestTime+5min, only if we haven't already today.
                    var inWindow = nowTime >= _digestTime && nowTime < _digestTime.AddMinutes(5);
                    var notSentToday = _lastDigestDate is null || _lastDigestDate.Value < todayLocal;
                    if (inWindow && notSentToday)
                    {
                        var exchangeBalance = await TryGetExchangeBalanceAsync(stoppingToken).ConfigureAwait(false);
                        var msg = BuildDigest(nowLocal, exchangeBalance);
                        var ok = await SendMessageAsync(msg, stoppingToken).ConfigureAwait(false);
                        if (ok) _lastDigestDate = todayLocal;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DailyTelegramDigestHostedService tick failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private string BuildStartupPing()
    {
        var sb = new StringBuilder();
        sb.AppendLine("📈 ThatsMyQuant Paper Daily Digest enabled");
        sb.AppendLine($"Will report at {_digestTime:HH\\:mm} local each night.");
        sb.AppendLine($"Edges live: pairs (TRX/XLM, SOL/AVAX, SOL/LINK 4h) + funding (LINK/LTC/DOGE/AAVE 1h).");
        sb.AppendLine($"Initial Cash: ${_portfolio.Cash:N2}");
        return sb.ToString();
    }

    private async Task<Account?> TryGetExchangeBalanceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var exchange = scope.ServiceProvider.GetService<IExchangeService>();
            if (exchange is null) return null;
            return await exchange.GetAccountStatusAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Digest: exchange balance fetch failed.");
            return null;
        }
    }

    private string BuildDigest(DateTime nowLocal, Account? exchange)
    {
        // Snapshot under lock to avoid mid-mutation reads from the trading hosted services.
        List<PaperPosition> closed;
        decimal cash, equity, dailyPnl, initial;
        int openCount;
        lock (_portfolio)
        {
            closed = _portfolio.ClosedPositions.ToList();
            cash = _portfolio.Cash;
            equity = _portfolio.TotalEquity;
            dailyPnl = _portfolio.DailyPnL;
            initial = _portfolio.InitialBalance;
            openCount = _portfolio.OpenPositions.Count;
        }

        decimal totalPnL = equity - initial;
        decimal totalPnLPct = initial > 0 ? totalPnL / initial * 100m : 0m;
        decimal dailyPnLPct = initial > 0 ? dailyPnl / initial * 100m : 0m;

        var sb = new StringBuilder();
        sb.AppendLine("📊 *Paper Trade Daily Digest*");
        sb.AppendLine($"_{nowLocal:yyyy-MM-dd HH:mm} local_");
        sb.AppendLine();
        if (exchange is not null && exchange.Balance > 0m)
        {
            sb.AppendLine("*Exchange (OKX Demo)*");
            sb.AppendLine($"  Balance : ${exchange.Balance:N2}");
            sb.AppendLine($"  Equity  : ${exchange.Equity:N2}");
            sb.AppendLine($"  Open    : {exchange.Positions?.Count ?? 0}");
            sb.AppendLine();
        }
        sb.AppendLine("*Paper Portfolio (simulated)*");
        sb.AppendLine($"  Cash    : ${cash:N2}");
        sb.AppendLine($"  Equity  : ${equity:N2}");
        sb.AppendLine($"  Open    : {openCount}");
        sb.AppendLine($"  Closed  : {closed.Count}");
        var totalSign = totalPnL >= 0 ? "🟢" : "🔴";
        sb.AppendLine($"  Total   : {totalSign} ${totalPnL:N2} ({totalPnLPct:F2}%)");
        var dailySign = dailyPnl >= 0 ? "🟢" : "🔴";
        sb.AppendLine($"  Today   : {dailySign} ${dailyPnl:N2} ({dailyPnLPct:F2}%)");
        sb.AppendLine();

        if (closed.Count == 0)
        {
            sb.AppendLine("*No closed trades yet.*");
            sb.AppendLine("Pairs interval=5min, Funding=30min — entries depend on cointegration spread + funding rate extremes.");
        }
        else
        {
            // ── Per-Strategy Attribution (which EDGE made the money) ──
            sb.AppendLine("*Per-Strategy Attribution*");
            var byStrategy = closed
                .GroupBy(p => string.IsNullOrWhiteSpace(p.StrategyTag) ? "Unknown" : p.StrategyTag)
                .ToList();
            foreach (var g in byStrategy.OrderByDescending(g => g.Sum(p => p.RealizedPnL)))
            {
                var n = g.Count();
                var wins = g.Count(p => p.RealizedPnL > 0);
                var sumPnL = g.Sum(p => p.RealizedPnL);
                var grossW = g.Where(p => p.RealizedPnL > 0).Sum(p => p.RealizedPnL);
                var grossL = Math.Abs(g.Where(p => p.RealizedPnL <= 0).Sum(p => p.RealizedPnL));
                var stratPF = grossL > 0 ? grossW / grossL : (grossW > 0 ? 999m : 0m);
                var avgWin = wins > 0 ? grossW / wins : 0m;
                var avgLoss = (n - wins) > 0 ? grossL / (n - wins) : 0m;
                var avgRR = avgLoss > 0 ? avgWin / avgLoss : 0m;
                var wr = n > 0 ? wins * 100m / n : 0m;
                var pnlSign = sumPnL >= 0 ? "🟢" : "🔴";
                sb.AppendLine($"  {pnlSign} `{g.Key,-22}` N={n,3} WR={wr,5:F1}% PF={stratPF,4:F2} avgRR={avgRR,4:F2} PnL=${sumPnL,9:N2}");
            }
            sb.AppendLine();

            // ── Bonferroni progress widget (2026-05-01 risk playbook) ──
            // Per pre-registered hypothesis, where ARE we vs the stopping rule? Operator
            // discipline lever: this shows "how many trades to verdict" instead of leaving
            // the operator guessing and tempted to peek at intermediate stats.
            sb.AppendLine("*Bonferroni-Test Progress*");
            // Funding hypothesis: m=7, N_target=200, required |t|≥2.69
            var fundingTrades = closed.Where(p => p.StrategyTag != null && p.StrategyTag.StartsWith("FundingAlpha", StringComparison.OrdinalIgnoreCase)
                                                  && p.RealFeePaid.HasValue).ToList();
            // Pairs hypothesis: m=3, N_target=50, required |t|≥2.39
            var pairsTrades = closed.Where(p => p.StrategyTag != null && p.StrategyTag.StartsWith("PairsTrading", StringComparison.OrdinalIgnoreCase)
                                                && p.RealFeePaid.HasValue).ToList();
            sb.AppendLine($"  Funding:  N={fundingTrades.Count,3}/200  ({(fundingTrades.Count * 100.0 / 200):F0}%)  required |t|≥2.69");
            sb.AppendLine($"  Pairs:    N={pairsTrades.Count,3}/50   ({(pairsTrades.Count * 100.0 / 50):F0}%)   required |t|≥2.39");
            // ETA estimate: average trades/day from current sample window
            if (fundingTrades.Count >= 2)
            {
                var span = (fundingTrades.Max(p => p.ExitTime ?? DateTime.UtcNow) - fundingTrades.Min(p => p.EntryTime)).TotalDays;
                if (span >= 0.5)
                {
                    var rate = fundingTrades.Count / span;
                    var remain = (200 - fundingTrades.Count) / Math.Max(0.1, rate);
                    sb.AppendLine($"  Funding ETA: ~{remain:F0} days at current {rate:F2} trades/day");
                }
            }
            if (pairsTrades.Count >= 2)
            {
                var span = (pairsTrades.Max(p => p.ExitTime ?? DateTime.UtcNow) - pairsTrades.Min(p => p.EntryTime)).TotalDays;
                if (span >= 0.5)
                {
                    var rate = pairsTrades.Count / span;
                    var remain = (50 - pairsTrades.Count) / Math.Max(0.1, rate);
                    sb.AppendLine($"  Pairs ETA:   ~{remain:F0} days at current {rate:F2} trades/day");
                }
            }
            sb.AppendLine($"  _NO MID-TEST DECISIONS — wait for stopping rule._");
            sb.AppendLine();

            // ── Per-Symbol breakdown (which INSTRUMENT carried each edge) ──
            sb.AppendLine("*Per-Symbol Breakdown*");
            var bySymbol = closed.GroupBy(p => string.IsNullOrWhiteSpace(p.Symbol) ? "?" : p.Symbol);
            foreach (var g in bySymbol.OrderByDescending(g => g.Sum(p => p.RealizedPnL)).Take(10))
            {
                var n = g.Count();
                var wins = g.Count(p => p.RealizedPnL > 0);
                var sumPnL = g.Sum(p => p.RealizedPnL);
                var wr = n > 0 ? wins * 100m / n : 0m;
                var pnlSign = sumPnL >= 0 ? "🟢" : "🔴";
                sb.AppendLine($"  {pnlSign} `{g.Key,-12}` N={n,3} WR={wr,5:F1}% PnL=${sumPnL,9:N2}");
            }
            sb.AppendLine();

            // ── Cost leakage tile (TCA-style) ──
            // PaperPosition doesn't carry explicit fees; estimate at the configured RoundTripCostRate (8bp default).
            decimal estFees = closed.Sum(p =>
            {
                var qty = p.Quantity > 0 ? p.Quantity : Math.Max(p.OpenedQuantity, 1m);
                var px = p.CurrentPrice > 0 ? p.CurrentPrice : p.EntryPrice;
                return px * qty * 0.0008m;
            });
            decimal grossSum = closed.Sum(p => p.RealizedPnL) + estFees;
            decimal leakagePct = grossSum > 0 ? (estFees / grossSum) * 100m : 0m;
            sb.AppendLine("*Cost Leakage (estimated)*");
            sb.AppendLine($"  Gross PnL : ${grossSum:N2}");
            sb.AppendLine($"  Est Fees  : ${estFees:N2}");
            var leakSign = leakagePct > 40m ? "🔴" : leakagePct > 25m ? "🟡" : "🟢";
            sb.AppendLine($"  Leakage   : {leakSign} {leakagePct:F1}%");
            sb.AppendLine();

            // Aggregate metrics
            var totalWins = closed.Count(p => p.RealizedPnL > 0);
            var grossWin = closed.Where(p => p.RealizedPnL > 0).Sum(p => p.RealizedPnL);
            var grossLoss = Math.Abs(closed.Where(p => p.RealizedPnL <= 0).Sum(p => p.RealizedPnL));
            var pf = grossLoss > 0 ? grossWin / grossLoss : (grossWin > 0 ? 999m : 0m);
            var overallWR = closed.Count > 0 ? totalWins * 100m / closed.Count : 0m;

            sb.AppendLine("*Aggregate*");
            sb.AppendLine($"  Trades  : {closed.Count} (W:{totalWins} / L:{closed.Count - totalWins})");
            sb.AppendLine($"  WinRate : {overallWR:F1}%");
            sb.AppendLine($"  PF      : {pf:F2}");
            sb.AppendLine();

            // ── AI Takeaways (last 24h reviews from AiTradeReviewHandler) ──
            AppendAiTakeaways(sb, nowLocal);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Appends up to 5 most-impactful AI reviews (by |NetPnl|) from the last 24h.
    /// Reads JSONL written by AiTradeReviewHandler. Silently skipped when no reviews exist.
    /// </summary>
    private void AppendAiTakeaways(StringBuilder sb, DateTime nowLocal)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThatsMyQuant", "ai_trade_reviews.jsonl");
            if (!File.Exists(path)) return;
            var cutoffUtc = DateTime.UtcNow.AddHours(-24);
            var lines = File.ReadAllLines(path);
            var entries = new List<(DateTime Ts, string Sym, string Strat, decimal Pnl, decimal PnlPct, string Review)>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var ts = root.GetProperty("ts").GetDateTime();
                    if (ts < cutoffUtc) continue;
                    entries.Add((
                        ts,
                        root.GetProperty("symbol").GetString() ?? "?",
                        root.GetProperty("strategy").GetString() ?? "?",
                        root.GetProperty("netPnl").GetDecimal(),
                        root.GetProperty("netPnlPct").GetDecimal(),
                        root.GetProperty("review").GetString() ?? string.Empty));
                }
                catch { /* skip bad line */ }
            }
            if (entries.Count == 0) return;

            sb.AppendLine("*AI Takeaways (24h)*");
            foreach (var e in entries.OrderByDescending(e => Math.Abs(e.Pnl)).Take(5))
            {
                var emoji = e.Pnl >= 0 ? "✅" : "❌";
                var pnlPart = $"{e.Pnl:+0.00;-0.00} ({e.PnlPct:+0.00;-0.00}%)";
                var review = e.Review.Length > 200 ? e.Review.Substring(0, 200) + "…" : e.Review;
                sb.AppendLine($"{emoji} `{e.Sym} {e.Strat}` {pnlPart}");
                sb.AppendLine($"   _{review}_");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to append AI takeaways to digest.");
        }
    }

    private async Task<bool> SendMessageAsync(string text, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var payload = new
            {
                chat_id = _chatId,
                text,
                parse_mode = "Markdown"
            };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogWarning("Telegram digest send failed: {Status} {Err}", resp.StatusCode, err);
                return false;
            }
            _logger.LogInformation("Telegram daily digest sent ({Len} chars).", text.Length);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram digest send threw.");
            return false;
        }
    }
}
