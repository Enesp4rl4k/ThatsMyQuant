using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy.EdgeLab;
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
    private readonly EdgeLabDryRunLedger? _edgeLabDryRunLedger;
    private readonly EdgeLabReconciliationLedger? _edgeLabReconLedger;
    private readonly string _botToken;
    private readonly string _chatId;

    // [2026-05-14 unattended hardening] Digest moved from once-daily to 4×/day.
    // Operator may be away for days; "what happened in the last 6h" cadence catches
    // problems within a quarter-day vs 24h.
    // Override via Telegram:DigestTimes = "00:00,06:00,12:00,18:00" (comma-separated HH:mm).
    // Legacy Telegram:DailyDigestTime still honored if DigestTimes absent.
    private readonly TimeOnly[] _digestTimes;
    // Per-slot dedupe key: ($"{date:yyyy-MM-dd}|{slotIdx}"). Memory-only, reset on restart.
    private readonly HashSet<string> _sentSlots = new();

    public DailyTelegramDigestHostedService(
        ILogger<DailyTelegramDigestHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        PaperPortfolio portfolio,
        EdgeLabDryRunLedger? edgeLabDryRunLedger = null,
        EdgeLabReconciliationLedger? edgeLabReconLedger = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _portfolio = portfolio;
        _edgeLabDryRunLedger = edgeLabDryRunLedger;
        _edgeLabReconLedger = edgeLabReconLedger;
        _botToken = configuration["Telegram:BotToken"] ?? string.Empty;
        _chatId = configuration["Telegram:ChatId"] ?? string.Empty;

        // Parse digest times. Priority: DigestTimes (CSV) > legacy DailyDigestTime > default 4×/day.
        var raw = configuration["Telegram:DigestTimes"];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            _digestTimes = raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => TimeOnly.TryParse(s, out var t) ? (TimeOnly?)t : null)
                .Where(t => t.HasValue)
                .Select(t => t!.Value)
                .Distinct()
                .OrderBy(t => t)
                .ToArray();
        }
        if (_digestTimes is null || _digestTimes.Length == 0)
        {
            var legacy = configuration["Telegram:DailyDigestTime"];
            if (TimeOnly.TryParse(legacy, out var legacyTime))
            {
                _digestTimes = new[] { legacyTime };
            }
            else
            {
                // Default: 4×/day at quarter marks (00, 06, 12, 18 local).
                _digestTimes = new[]
                {
                    new TimeOnly(0, 0),
                    new TimeOnly(6, 0),
                    new TimeOnly(12, 0),
                    new TimeOnly(18, 0),
                };
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(_chatId))
        {
            _logger.LogWarning("DailyTelegramDigestHostedService disabled — Telegram BotToken or ChatId missing.");
            return;
        }

        _logger.LogInformation(
            "TelegramDigestHostedService starting. DigestTimes=[{Times}] local ({Count}/day).",
            string.Join(",", _digestTimes.Select(t => t.ToString("HH\\:mm"))),
            _digestTimes.Length);

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

                    // Fire once per slot: each slotTime opens a 5-min window; we check
                    // a per-slot dedupe key so each digest fires exactly once per day per slot.
                    for (int i = 0; i < _digestTimes.Length; i++)
                    {
                        var slotTime = _digestTimes[i];
                        var inWindow = nowTime >= slotTime && nowTime < slotTime.AddMinutes(5);
                        if (!inWindow) continue;

                        var slotKey = $"{todayLocal:yyyy-MM-dd}|{i}";
                        if (_sentSlots.Contains(slotKey)) continue;

                        var exchangeBalance = await TryGetExchangeBalanceAsync(stoppingToken).ConfigureAwait(false);
                        var msg = BuildDigest(nowLocal, exchangeBalance, slotTime, slotIndex: i, totalSlots: _digestTimes.Length);
                        var ok = await SendMessageAsync(msg, stoppingToken).ConfigureAwait(false);
                        if (ok)
                        {
                            _sentSlots.Add(slotKey);
                            // Garbage-collect old slot keys (keep only today + yesterday).
                            var cutoff = todayLocal.AddDays(-1);
                            _sentSlots.RemoveWhere(k =>
                                DateOnly.TryParseExact(k.Split('|')[0], "yyyy-MM-dd", out var d) && d < cutoff);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "TelegramDigestHostedService tick failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// [2026-05-14] Escape Markdown V1 special chars in user/dynamic strings to avoid
    /// "can't parse entities" 400 errors from Telegram. The error we saw was triggered
    /// by "fee_panic" — the underscore is the Markdown italic delimiter and Telegram
    /// looks for a closing `_` it never finds. Escape: _ * [ ` (backslash prefix).
    /// </summary>
    private static string EscapeMarkdown(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Replace(@"\", @"\\").Replace("_", @"\_").Replace("*", @"\*").Replace("[", @"\[").Replace("`", @"\`");
    }

    private string BuildStartupPing()
    {
        var sb = new StringBuilder();
        var schedule = string.Join(", ", _digestTimes.Select(t => t.ToString("HH\\:mm")));
        sb.AppendLine("📈 ThatsMyQuant Paper Digest enabled");
        sb.AppendLine($"Will report {_digestTimes.Length}×/day at: {schedule} (local).");
        // 2026-05-14: escape dynamic strategy names — they contain `_` (e.g. "fee_panic")
        // which Markdown V1 reads as italic delimiter and 400-errors when unmatched.
        sb.AppendLine($"Edges live: 13 strategies (breakout 4h/1h, {EscapeMarkdown("fee_panic")}, stablecoin, funding alpha).");
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

    private string BuildDigest(DateTime nowLocal, Account? exchange, TimeOnly slotTime, int slotIndex, int totalSlots)
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
        // Title differs by slot count: "Daily" for once-a-day, "Nh" for multi-slot.
        var hoursPerSlot = totalSlots > 0 ? 24 / totalSlots : 24;
        var titleQualifier = totalSlots == 1 ? "Daily" : $"{hoursPerSlot}h";
        sb.AppendLine($"📊 *Paper Trade {titleQualifier} Digest* (slot {slotIndex + 1}/{totalSlots} @ {slotTime:HH\\:mm})");
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

        // ── EdgeLab Dry-Run (last 24h intended orders) ──
        // Sits OUTSIDE the closed-trades guard above because dry-run by design
        // produces no closed positions; it still has its own intent stream.
        AppendEdgeLabDryRun(sb, nowLocal);

        return sb.ToString();
    }

    /// <summary>
    /// Reads <see cref="EdgeLabDryRunLedger"/> for entries in the last 24h and
    /// renders a compact summary: cycle count, intended order count, gross
    /// exposure, top symbols, last <c>as_of_bar</c> seen. Silently skipped
    /// when the ledger is unavailable or empty (e.g. EdgeLab is in <c>off</c>
    /// or <c>live</c> mode, or worker is fresh).
    /// </summary>
    private void AppendEdgeLabDryRun(System.Text.StringBuilder sb, DateTime nowLocal)
    {
        if (_edgeLabDryRunLedger is null) return;

        IReadOnlyList<EdgeLabDryRunEntry> entries;
        try { entries = _edgeLabDryRunLedger.ReadSince(DateTime.UtcNow.AddHours(-24)); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Daily digest: EdgeLab dry-run ledger read failed.");
            return;
        }
        if (entries.Count == 0) return;

        sb.AppendLine("*EdgeLab Dry-Run (last 24h)*");
        var cycles = entries
            .GroupBy(e => e.AsOfBar)
            .OrderBy(g => g.Key)
            .ToList();
        var totalGross = entries.Sum(e => e.NotionalUsd);
        var lastCycle = cycles[^1];
        sb.AppendLine($"  Cycles: {cycles.Count}  Intended orders: {entries.Count}  Gross: ${totalGross:N0}");
        sb.AppendLine($"  Last as_of: {lastCycle.Key:u}  ({lastCycle.Count()} orders, gross ${lastCycle.Sum(e => e.NotionalUsd):N0})");

        // Top symbols by appearance × side — gives a quick sense of which names
        // EdgeLab consistently picks at the bottom/top of the rank distribution.
        var topLong = entries.Where(e => e.Side == "Buy")
            .GroupBy(e => e.Symbol)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => $"{g.Key}×{g.Count()}");
        var topShort = entries.Where(e => e.Side == "Sell")
            .GroupBy(e => e.Symbol)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => $"{g.Key}×{g.Count()}");
        var longStr = string.Join(", ", topLong);
        var shortStr = string.Join(", ", topShort);
        if (longStr.Length > 0)  sb.AppendLine($"  🟢 Long picks : {longStr}");
        if (shortStr.Length > 0) sb.AppendLine($"  🔴 Short picks: {shortStr}");

        // Hypothetical PnL — the only signal that tells us "is the bot's behavior
        // consistent with the validated edge?" Cumulative is read from the latest
        // reconciliation entry (running total since worker first booted in dry-run).
        // At promotion-gate time (N≥30 cycles) we'll compare distribution to
        // EdgeLab's backtest to decide live-readiness.
        if (_edgeLabReconLedger is not null)
        {
            try
            {
                var reconEntries = _edgeLabReconLedger.ReadSince(DateTime.UtcNow.AddHours(-24));
                var last = _edgeLabReconLedger.ReadLast();
                if (reconEntries.Count > 0 && last is not null)
                {
                    var sumPeriod24h = reconEntries.Sum(e => e.PeriodPnLUsd);
                    var basisRef = last.Basis > 0 ? last.Basis : 1m;
                    var pctOfBasis = sumPeriod24h / basisRef * 100m;
                    var bestSym = reconEntries
                        .SelectMany(e => e.PerSymbolPnL)
                        .GroupBy(kv => kv.Key)
                        .Select(g => new { Sym = g.Key, Pnl = g.Sum(kv => kv.Value) })
                        .OrderByDescending(x => x.Pnl)
                        .FirstOrDefault();
                    var worstSym = reconEntries
                        .SelectMany(e => e.PerSymbolPnL)
                        .GroupBy(kv => kv.Key)
                        .Select(g => new { Sym = g.Key, Pnl = g.Sum(kv => kv.Value) })
                        .OrderBy(x => x.Pnl)
                        .FirstOrDefault();
                    sb.AppendLine($"  *Hypothetical PnL (mark-to-mark)*");
                    sb.AppendLine($"    24h period sum: ${sumPeriod24h:+0.00;-0.00;0.00} ({pctOfBasis:+0.00;-0.00;0.00}% of ${basisRef:N0} basis)");
                    sb.AppendLine($"    Cum since boot: ${last.CumulativePnLUsd:+0.00;-0.00;0.00}");
                    if (bestSym is not null && bestSym.Pnl != 0m)
                        sb.AppendLine($"    Best: {bestSym.Sym} ${bestSym.Pnl:+0.00;-0.00;0.00}   Worst: {worstSym!.Sym} ${worstSym.Pnl:+0.00;-0.00;0.00}");
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Daily digest: EdgeLab reconciliation read failed."); }
        }

        sb.AppendLine($"  _Mode: dry-run · no OKX traffic · contract: signals/README.md_");
        sb.AppendLine();
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
