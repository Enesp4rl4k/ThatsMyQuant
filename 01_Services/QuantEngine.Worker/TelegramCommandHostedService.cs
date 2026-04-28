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
/// Two-way Telegram bot. Long-polls <c>getUpdates</c> every ~3s and replies to commands:
///   <c>/start /help</c>      → list commands
///   <c>/status</c>           → service health, exchange, regime, cash, equity
///   <c>/pnl</c>              → total + today PnL %, equity peak/drawdown
///   <c>/positions</c>        → open positions table (symbol/side/qty/uPnL)
///   <c>/digest</c>           → trigger an on-demand digest using same logic as DailyTelegramDigestHostedService
///
/// Reads:  Telegram:BotToken / Telegram:ChatId  (only the configured chat is allowed; messages from
///         other chats are dropped with a debug log).
///
/// Long-poll <c>offset</c> is in-memory — bot re-replays the last few messages on restart, which is
/// idempotent for read-only commands.
/// </summary>
public sealed class TelegramCommandHostedService : BackgroundService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(35) };

    private readonly ILogger<TelegramCommandHostedService> _logger;
    private readonly IServiceProvider _sp;
    private readonly PaperPortfolio _portfolio;
    private readonly string _botToken;
    private readonly string _chatId;
    private long _lastOffset;

    public TelegramCommandHostedService(
        ILogger<TelegramCommandHostedService> logger,
        IServiceProvider sp,
        IConfiguration cfg,
        PaperPortfolio portfolio)
    {
        _logger     = logger;
        _sp         = sp;
        _portfolio  = portfolio;
        _botToken   = cfg["Telegram:BotToken"] ?? string.Empty;
        _chatId     = cfg["Telegram:ChatId"]   ?? string.Empty;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(_chatId))
        {
            _logger.LogWarning("TelegramCommandHostedService disabled — BotToken or ChatId missing.");
            return;
        }

        _logger.LogInformation("TelegramCommandHostedService starting — long-poll mode (3s).");

        // Tell Telegram what commands we support so the user gets autocomplete in the chat
        await SetMyCommandsAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TelegramCommandHostedService poll failed; backing off 10s");
                try { await Task.Delay(10_000, stoppingToken).ConfigureAwait(false); } catch { /* shutdown */ }
            }
        }
    }

    // ─── Long-poll loop ───────────────────────────────────────────────────────

    private async Task PollOnceAsync(CancellationToken ct)
    {
        // timeout=25s long-poll; getUpdates blocks until message arrives or timeout
        var url = $"https://api.telegram.org/bot{_botToken}/getUpdates?timeout=25&offset={_lastOffset + 1}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("getUpdates returned {Status}", resp.StatusCode);
            await Task.Delay(5_000, ct).ConfigureAwait(false);
            return;
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean()) return;
        if (!doc.RootElement.TryGetProperty("result", out var arr) || arr.ValueKind != JsonValueKind.Array) return;

        foreach (var update in arr.EnumerateArray())
        {
            if (update.TryGetProperty("update_id", out var idEl) && idEl.TryGetInt64(out var uid))
                _lastOffset = Math.Max(_lastOffset, uid);

            if (!update.TryGetProperty("message", out var msg)) continue;
            await HandleMessageAsync(msg, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleMessageAsync(JsonElement msg, CancellationToken ct)
    {
        // Only respond to the configured chat ID — drop everything else for privacy
        if (!msg.TryGetProperty("chat", out var chat)) return;
        if (!chat.TryGetProperty("id", out var chatIdEl)) return;
        var fromChatId = chatIdEl.GetRawText();
        // Telegram sends numeric IDs; configured ChatId is a string. Compare as strings.
        if (!string.Equals(fromChatId.Trim(), _chatId.Trim(), StringComparison.Ordinal))
        {
            _logger.LogDebug("Drop message from non-allowed chat {ChatId}", fromChatId);
            return;
        }

        if (!msg.TryGetProperty("text", out var textEl)) return;
        var text = textEl.GetString()?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text) || !text.StartsWith('/')) return;

        // Strip @BotName suffix if present (Telegram sends "/status@MyBot" in groups)
        var cmd = text.Split(' ', 2)[0].ToLowerInvariant();
        var atIx = cmd.IndexOf('@');
        if (atIx > 0) cmd = cmd[..atIx];

        _logger.LogInformation("Telegram cmd received: {Cmd}", cmd);

        var reply = cmd switch
        {
            "/start" or "/help" => BuildHelp(),
            "/pnl"              => BuildPnl(),
            "/positions"        => BuildPositions(),
            "/status"           => await BuildStatusAsync(ct).ConfigureAwait(false),
            _                   => $"Unknown command: `{cmd}`\nTry /help",
        };

        await SendMessageAsync(reply, ct).ConfigureAwait(false);
    }

    // ─── Reply builders ───────────────────────────────────────────────────────

    private static string BuildHelp() => string.Join('\n', new[]
    {
        "🤖 *ThatsMyQuant Bot*",
        "",
        "*/status*    — service + exchange health snapshot",
        "*/pnl*       — total + today's P&L",
        "*/positions* — open positions",
        "*/help*      — this list",
        "",
        "_Daily digest at 00:00 local._",
    });

    private string BuildPnl()
    {
        decimal cash, equity, daily, initial;
        int closedCount, winCount;
        decimal grossWin, grossLoss;
        lock (_portfolio)
        {
            cash        = _portfolio.Cash;
            equity      = _portfolio.TotalEquity;
            daily       = _portfolio.DailyPnL;
            initial     = _portfolio.InitialBalance;
            closedCount = _portfolio.ClosedPositions.Count;
            winCount    = _portfolio.ClosedPositions.Count(p => p.RealizedPnL > 0);
            grossWin    = _portfolio.ClosedPositions.Where(p => p.RealizedPnL > 0).Sum(p => p.RealizedPnL);
            grossLoss   = Math.Abs(_portfolio.ClosedPositions.Where(p => p.RealizedPnL <= 0).Sum(p => p.RealizedPnL));
        }

        var totalPnL    = equity - initial;
        var totalPnLPct = initial > 0 ? totalPnL / initial * 100m : 0m;
        var dailyPct    = initial > 0 ? daily    / initial * 100m : 0m;
        var pf          = grossLoss > 0 ? grossWin / grossLoss : (grossWin > 0 ? 999m : 0m);
        var wr          = closedCount > 0 ? winCount * 100m / closedCount : 0m;

        var totalSign = totalPnL >= 0 ? "🟢" : "🔴";
        var dailySign = daily    >= 0 ? "🟢" : "🔴";

        var sb = new StringBuilder();
        sb.AppendLine("💰 *P&L Snapshot*");
        sb.AppendLine($"  Cash    : ${cash:N2}");
        sb.AppendLine($"  Equity  : ${equity:N2}");
        sb.AppendLine($"  Initial : ${initial:N2}");
        sb.AppendLine($"  Total   : {totalSign} ${totalPnL:N2} ({totalPnLPct:F2}%)");
        sb.AppendLine($"  Today   : {dailySign} ${daily:N2} ({dailyPct:F2}%)");
        sb.AppendLine();
        sb.AppendLine("📊 *Trade Stats*");
        sb.AppendLine($"  Closed  : {closedCount} (W:{winCount} / L:{closedCount - winCount})");
        sb.AppendLine($"  WinRate : {wr:F1}%");
        sb.AppendLine($"  PF      : {pf:F2}");
        return sb.ToString();
    }

    private string BuildPositions()
    {
        List<PaperPosition> open;
        lock (_portfolio) open = _portfolio.OpenPositions.ToList();

        if (open.Count == 0)
            return "📂 *Open Positions*\n_No open positions._";

        var sb = new StringBuilder();
        sb.AppendLine($"📂 *Open Positions* ({open.Count})");
        sb.AppendLine();
        foreach (var p in open)
        {
            var sideTag  = p.Side == ThatsMyQuant.Contracts.Enums.TradeSide.Buy ? "↑ LONG" : "↓ SHORT";
            var uPnL     = p.UnrealizedPnL;
            var pnlSign  = uPnL >= 0 ? "🟢" : "🔴";
            var notional = p.MarketValue;
            sb.AppendLine($"`{p.Symbol,-12}` {sideTag}");
            sb.AppendLine($"  Qty: {p.Quantity:F4} @ {p.EntryPrice:F4}");
            sb.AppendLine($"  Now: {p.CurrentPrice:F4}  Notional: ${notional:N2}");
            sb.AppendLine($"  uPnL: {pnlSign} ${uPnL:N2}");
            if (!string.IsNullOrWhiteSpace(p.StrategyTag))
                sb.AppendLine($"  Tag: `{p.StrategyTag}`");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private async Task<string> BuildStatusAsync(CancellationToken ct)
    {
        decimal cash, equity, initial;
        int openCount, closedCount;
        lock (_portfolio)
        {
            cash        = _portfolio.Cash;
            equity      = _portfolio.TotalEquity;
            initial     = _portfolio.InitialBalance;
            openCount   = _portfolio.OpenPositions.Count;
            closedCount = _portfolio.ClosedPositions.Count;
        }

        Account? exchange = null;
        try
        {
            using var scope = _sp.CreateScope();
            var ex = scope.ServiceProvider.GetService<IExchangeService>();
            if (ex is not null)
                exchange = await ex.GetAccountStatusAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "/status: exchange fetch failed"); }

        var sb = new StringBuilder();
        sb.AppendLine("🔍 *System Status*");
        sb.AppendLine($"  Service : QuantEngine.Worker");
        sb.AppendLine($"  Mode    : Paper (live runners + hybrid executors)");
        sb.AppendLine($"  UptimeUtc: {DateTime.UtcNow:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        if (exchange is not null && exchange.Balance > 0m)
        {
            sb.AppendLine("*Exchange (OKX Demo)*");
            sb.AppendLine($"  Balance : ${exchange.Balance:N2}");
            sb.AppendLine($"  Equity  : ${exchange.Equity:N2}");
            sb.AppendLine($"  Open    : {exchange.Positions?.Count ?? 0}");
        }
        else
        {
            sb.AppendLine("*Exchange*: unavailable / no creds");
        }
        sb.AppendLine();
        sb.AppendLine("*Paper Portfolio*");
        sb.AppendLine($"  Cash    : ${cash:N2}");
        sb.AppendLine($"  Equity  : ${equity:N2}");
        sb.AppendLine($"  Initial : ${initial:N2}");
        sb.AppendLine($"  Open    : {openCount}");
        sb.AppendLine($"  Closed  : {closedCount}");
        return sb.ToString();
    }

    // ─── Telegram I/O ────────────────────────────────────────────────────────

    private async Task SendMessageAsync(string text, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var payload = new { chat_id = _chatId, text, parse_mode = "Markdown" };
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogWarning("Telegram cmd reply failed: {Status} {Err}", resp.StatusCode, err);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram cmd reply threw.");
        }
    }

    private async Task SetMyCommandsAsync(CancellationToken ct)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/setMyCommands";
            var payload = new
            {
                commands = new[]
                {
                    new { command = "status",    description = "service + exchange health snapshot" },
                    new { command = "pnl",       description = "total + today's P&L" },
                    new { command = "positions", description = "open positions" },
                    new { command = "help",      description = "show available commands" },
                },
            };
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                _logger.LogInformation("Telegram bot commands registered.");
        }
        catch (Exception ex) { _logger.LogDebug(ex, "setMyCommands failed (non-fatal)"); }
    }
}
